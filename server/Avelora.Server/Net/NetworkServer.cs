#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using Avelora.Net;
using Avelora.Server.Configuration;
using Avelora.Server.Entities;
using Avelora.Server.Persistence;
using Avelora.Server.Simulation;
using Avelora.Shared;
using Avelora.Shared.Protocol;

namespace Avelora.Server.Net
{
    /// <summary>
    /// Sesiones de red: handshake, entrada de intenciones y snapshots (plan del servidor §4).
    /// </summary>
    /// <remarks>
    /// Dos hilos, dos responsabilidades y una frontera muy fina:
    /// <list type="bullet">
    /// <item><b>Hilo de red</b>: bombea el transporte, valida el saludo, decodifica intenciones y
    /// las encola en <see cref="Zone.Inputs"/>. Nunca toca entidades.</item>
    /// <item><b>Hilo de simulación</b>: vacía los eventos de control (alta/baja) en
    /// <see cref="BeforeTick"/> y encola snapshots en <see cref="AfterTick"/>. Nunca toca el socket.</item>
    /// </list>
    ///
    /// Se comunican con dos colas concurrentes y un diccionario conexión → entidad. El diccionario
    /// lo escribe solo la simulación y lo lee la red; una lectura obsoleta como mucho encola una
    /// intención para una entidad que ya no existe, y la zona la descarta y la cuenta.
    /// </remarks>
    public sealed class NetworkServer : ITickObserver, IDisposable
    {
        private readonly Zone _zone;
        private readonly ServerConfig _config;
        private readonly IServerTransport _transport;
        private readonly byte[] _mapHash;
        private readonly IPlayerRepository? _repository;
        private readonly CharacterSaveService? _saves;
        private readonly LoginWorker? _login;
        private readonly string _registrationCode;
        private readonly long _saveEveryTicks;
        private readonly RSA _serverKey;
        private readonly bool _ownsServerKey;
        private readonly Func<long> _clock;
        private long _nextSaveTick;

        /// <summary>
        /// Cuántas veces el tope se tolera antes de cortar. Los inputs van a 20/s y el tope es 90:
        /// un pico de reenvío no corta, pero insistir sí.
        /// </summary>
        private const int RateLimitDisconnectFactor = 3;

        /// <summary>
        /// «No hay conexión anterior». No puede ser 0: LiteNetLib empieza en 0 y reutiliza los ids,
        /// así que 0 es una conexión real.
        /// </summary>
        private const int NoConnection = -1;

        // Frontera entre hilos.
        private readonly ConcurrentQueue<ControlEvent> _control = new ConcurrentQueue<ControlEvent>();
        private readonly ConcurrentQueue<OutboundMessage> _outbound = new ConcurrentQueue<OutboundMessage>();
        private readonly ConcurrentDictionary<int, int> _connectionToEntity = new ConcurrentDictionary<int, int>();
        private readonly ConcurrentDictionary<int, int> _connectionToAccount = new ConcurrentDictionary<int, int>();
        private readonly ConcurrentDictionary<int, int> _onlineAccounts = new ConcurrentDictionary<int, int>();

        // Solo hilo de red.
        private readonly Dictionary<int, long> _handshakeDeadlines = new Dictionary<int, long>();
        private readonly Dictionary<int, byte[]> _challengeNonces = new Dictionary<int, byte[]>();
        private readonly Dictionary<int, PendingDisconnect> _pendingDisconnects =
            new Dictionary<int, PendingDisconnect>();
        private readonly Dictionary<int, HelloMessage> _pendingLogins = new Dictionary<int, HelloMessage>();
        private readonly Dictionary<string, int> _failedLogins =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Logins en vuelo por cuenta. Cuenta los encolados y el que se está calculando, igual que
        /// el tope global, pero por cuenta: cinco saludos seguidos no se escapan por llegar antes
        /// de que vuelva el primer resultado. Solo la toca el hilo de red.
        /// </summary>
        private readonly Dictionary<string, int> _loginsInFlightPerAccount =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, int> _messagesThisSecond = new Dictionary<int, int>();

        /// <summary>Segundos, dentro del minuto actual, en los que cada conexión superó el límite.</summary>
        private readonly Dictionary<int, int> _secondsOverLimit = new Dictionary<int, int>();

        /// <summary>Último segundo en el que se contó ese exceso, para sumar una sola vez por segundo.</summary>
        private readonly Dictionary<int, long> _lastOverLimitSecond = new Dictionary<int, long>();

        private long _currentSecond;
        private long _currentMinute;

        /// <summary>
        /// Minuto de la ventana del límite de frecuencia. Va aparte de <see cref="_currentMinute"/>
        /// (la de los logins fallidos) para que limpiar una no borre la otra.
        /// </summary>
        private long _currentRateMinute;

        // Solo hilo de simulación.
        private readonly Dictionary<int, Session> _sessions = new Dictionary<int, Session>();
        private readonly List<Entity> _visibleBuffer = new List<Entity>(64);
        private int _nextCharacterId;

        private Thread? _netThread;
        private volatile bool _stopping;

        private long _messagesReceived;
        private long _bytesReceived;
        private long _messagesRejected;
        private long _handshakesRejected;
        private long _rateLimited;
        private long _rateLimitedDisconnects;
        private long _droppedOutbound;
        private long _playersJoined;
        private long _playersLeft;
        private long _snapshotsSent;
        private long _snapshotsTruncated;
        private long _entitiesSent;
        private long _snapshotBytes;
        private long _loginsSucceeded;
        private long _loginsRejected;

        public NetworkServer(
            Zone zone,
            ServerConfig config,
            IServerTransport transport,
            byte[] mapHash,
            IPlayerRepository? repository = null,
            CharacterSaveService? saves = null,
            string? registrationCode = null,
            RSA? serverKey = null,
            Func<long>? clock = null)
        {
            _zone = zone ?? throw new ArgumentNullException(nameof(zone));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _clock = clock ?? (() => Environment.TickCount64);

            if (serverKey == null)
            {
                _serverKey = RSA.Create(2048);
                _ownsServerKey = true;
            }
            else
            {
                _serverKey = serverKey;
                _ownsServerKey = false;

                if (_serverKey.KeySize != 2048)
                {
                    throw new ArgumentException(
                        "La clave del servidor debe ser RSA de 2048 bits (el desafío manda 256 bytes de módulo).",
                        nameof(serverKey));
                }
            }

            PublicKey = _serverKey.ExportParameters(includePrivateParameters: false);
            KeyFingerprint = HelloSecrets.Fingerprint(PublicKey);

            if (mapHash is null || mapHash.Length != ProtocolConstants.MapHashSize)
            {
                throw new ArgumentException(
                    "La huella del mapa debe tener " + ProtocolConstants.MapHashSize + " bytes.",
                    nameof(mapHash));
            }

            _mapHash = mapHash;
            _repository = repository;
            _saves = saves;
            _registrationCode = registrationCode ?? string.Empty;
            _nextCharacterId = config.FirstCharacterId;

            if (_repository != null && _saves == null)
            {
                throw new ArgumentException(
                    "Con persistencia activa hace falta el servicio de guardado.",
                    nameof(saves));
            }

            _saveEveryTicks = Math.Max(
                1L,
                (long)Math.Round(
                    config.SaveIntervalSeconds * SimulationConstants.TickRate,
                    MidpointRounding.AwayFromZero));

            _nextSaveTick = _saveEveryTicks;

            // El login (RSA + PBKDF2 + SQLite) corre en su propio hilo: el de red nunca espera a
            // disco ni al descifrado.
            if (_repository != null)
            {
                _login = new LoginWorker(
                    _repository,
                    _registrationCode,
                    _serverKey.ExportParameters(includePrivateParameters: true));
            }

            _transport.Connected += OnConnected;
            _transport.Disconnected += OnDisconnected;
            _transport.Received += OnReceived;
        }

        /// <summary>Puerto UDP efectivo.</summary>
        public int Port => _transport.Port;

        /// <summary>Clave pública que viaja en el desafío (sin la parte privada).</summary>
        public RSAParameters PublicKey { get; }

        /// <summary>Huella SHA-256 de módulo ‖ exponente, en hexadecimal.</summary>
        public string KeyFingerprint { get; }

        /// <summary>Abre el socket y arranca el hilo de red.</summary>
        public void Start()
        {
            if (_netThread != null)
            {
                throw new InvalidOperationException("La red ya está en marcha.");
            }

            _transport.Start(_config.Port);

            _netThread = new Thread(NetLoop)
            {
                Name = "avelora-net",
                IsBackground = true,
            };

            _netThread.Start();
        }

        /// <summary>Cierra el hilo de red y el socket.</summary>
        public void Stop()
        {
            if (_stopping)
            {
                return;
            }

            _stopping = true;
            _netThread?.Join(TimeSpan.FromSeconds(2));
            _netThread = null;
            _transport.Stop();
            _login?.Dispose();

            if (_ownsServerKey)
            {
                _serverKey.Dispose();
            }
        }

        public void Dispose() => Stop();

        /// <summary>
        /// Una vuelta del hilo de red: procesar eventos, recoger los logins terminados, vaciar la
        /// cola de salida y expirar los saludos que no llegan. Es público para poder probarlo sin
        /// arrancar el hilo.
        /// </summary>
        public void PollOnce()
        {
            // Los cortes vencidos van primero: un rechazo apuntado en esta misma vuelta espera a
            // la siguiente, aunque el retardo sea 0. Así el mensaje fiable siempre sale antes.
            ExpirePendingDisconnects();
            _transport.Poll();
            DrainLogins();
            DrainOutbound();
            ExpireHandshakes();
        }

        public void BeforeTick(Zone zone)
        {
            while (_control.TryDequeue(out ControlEvent control))
            {
                if (control.Kind == ControlEventKind.Join)
                {
                    AddPlayer(zone, control);
                }
                else
                {
                    RemovePlayer(control.ConnectionId);
                }
            }

            // El guardado periódico se engancha al hilo de simulación, después de aplicar altas y
            // bajas. Encolar una foto es barato; escribir en SQLite lo hace el hilo de guardado.
            if (_saves != null && zone.TickCount >= _nextSaveTick)
            {
                SaveAllPlayers(zone);
                _nextSaveTick = zone.TickCount + _saveEveryTicks;
            }
        }

        /// <summary>
        /// Encola el estado de todos los jugadores conectados. Se llama al cerrar el servidor,
        /// cuando el hilo de simulación ya ha terminado; no debe usarse en mitad de un tick.
        /// </summary>
        public void SaveAll(Zone zone)
        {
            if (_saves != null)
            {
                SaveAllPlayers(zone);
            }
        }

        public void AfterTick(Zone zone)
        {
            if (_sessions.Count == 0)
            {
                return;
            }

            if (zone.TickCount % _config.SnapshotEveryTicks != 0)
            {
                return;
            }

            foreach (KeyValuePair<int, Session> pair in _sessions)
            {
                Session session = pair.Value;

                if (!zone.Entities.TryGet(session.EntityId, out Entity? entity) || !(entity is PlayerEntity recipient))
                {
                    continue;
                }

                byte[] snapshot = BuildSnapshot(zone, recipient);
                Enqueue(session.ConnectionId, snapshot, DeliveryMode.Sequenced, disconnectAfter: false);
                Interlocked.Increment(ref _snapshotsSent);
            }
        }

        /// <summary>Cifras para los informes. Se pueden leer desde otro hilo.</summary>
        public NetworkStats Snapshot() => new NetworkStats(
            _transport.ConnectionCount,
            Interlocked.Read(ref _messagesReceived),
            Interlocked.Read(ref _bytesReceived),
            Interlocked.Read(ref _messagesRejected),
            Interlocked.Read(ref _handshakesRejected),
            Interlocked.Read(ref _rateLimited),
            Interlocked.Read(ref _rateLimitedDisconnects),
            Interlocked.Read(ref _droppedOutbound),
            Interlocked.Read(ref _playersJoined),
            Interlocked.Read(ref _playersLeft),
            Interlocked.Read(ref _snapshotsSent),
            Interlocked.Read(ref _snapshotsTruncated),
            Interlocked.Read(ref _entitiesSent),
            Interlocked.Read(ref _snapshotBytes),
            Interlocked.Read(ref _loginsSucceeded),
            Interlocked.Read(ref _loginsRejected),
            _saves?.Saved ?? 0,
            _saves?.Failed ?? 0,
            _saves?.Pending ?? 0,
            _saves?.LastError);

        // ---------------------------------------------------------------- hilo de red

        private void NetLoop()
        {
            while (!_stopping)
            {
                PollOnce();
                Thread.Sleep(1);
            }
        }

        private void OnConnected(int connectionId)
        {
            _handshakeDeadlines[connectionId] = Environment.TickCount64 +
                (long)Math.Round(_config.HandshakeTimeoutSeconds * 1000d, MidpointRounding.AwayFromZero);

            // El desafío va primero y por el canal fiable: el cliente no puede mandar el saludo
            // cifrado sin conocer el nonce. Es de un solo uso y se borra al llegar el Hello.
            var nonce = new byte[HelloSecrets.NonceSize];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                random.GetBytes(nonce);
            }

            _challengeNonces[connectionId] = nonce;

            byte[] challenge = MessageCodec.EncodeChallenge(new ChallengeMessage(
                nonce,
                PublicKey.Modulus!,
                PublicKey.Exponent!));

            _transport.Send(connectionId, challenge, challenge.Length, DeliveryMode.ReliableOrdered);
        }

        private void OnDisconnected(int connectionId, DisconnectReason reason)
        {
            _handshakeDeadlines.Remove(connectionId);
            _pendingDisconnects.Remove(connectionId);
            _messagesThisSecond.Remove(connectionId);
            _challengeNonces.Remove(connectionId);
            _secondsOverLimit.Remove(connectionId);
            _lastOverLimitSecond.Remove(connectionId);

            // Si estaba autenticándose, su resultado ya no interesa. Nada que reservar: la
            // conexión se ha ido antes de que el login terminara.
            _pendingLogins.Remove(connectionId);

            // La baja se aplica en el hilo de simulación: aquí solo se avisa.
            _control.Enqueue(ControlEvent.Leave(connectionId));
        }

        private void OnReceived(int connectionId, byte[] buffer, int offset, int length)
        {
            Interlocked.Increment(ref _messagesReceived);

            // Bytes de carga útil que llegan a la sesión, se acepten o se rechacen después: es lo
            // que cuesta atender a la red y lo que pide el informe de resistencia (G-05).
            if (length > 0)
            {
                Interlocked.Add(ref _bytesReceived, length);
            }

            if (_pendingDisconnects.ContainsKey(connectionId))
            {
                // Ya se le envió el Reject y está a punto de cortarse: nada de lo que mande
                // mientras tanto puede afectar a la sesión.
                Interlocked.Increment(ref _messagesRejected);
                return;
            }

            if (length <= 0 || length > ProtocolConstants.MaxMessageSize)
            {
                Interlocked.Increment(ref _messagesRejected);
                return;
            }

            if (_pendingLogins.ContainsKey(connectionId))
            {
                // Autenticándose: no hay sesión todavía, así que cualquier mensaje sobra.
                Interlocked.Increment(ref _messagesRejected);
                return;
            }

            if (_handshakeDeadlines.ContainsKey(connectionId))
            {
                HandleHello(connectionId, buffer, offset, length);
                return;
            }

            if (!_connectionToEntity.TryGetValue(connectionId, out int entityId))
            {
                // Conexión sin saludar (o ya dada de baja): no hay a quién aplicar el input.
                Interlocked.Increment(ref _messagesRejected);
                return;
            }

            RateLimitVerdict verdict = CheckRate(connectionId);

            if (verdict == RateLimitVerdict.Disconnect)
            {
                // Insistir después de que se le hayan descartado mensajes no es un pico de jitter:
                // es abuso. Se le dice por qué y se cierra (plan del servidor §7).
                Interlocked.Increment(ref _rateLimited);
                Interlocked.Increment(ref _rateLimitedDisconnects);

                byte[] reject = MessageCodec.EncodeReject(new RejectMessage(RejectReason.RateLimited));
                _transport.Send(connectionId, reject, reject.Length, DeliveryMode.ReliableOrdered);
                ScheduleDisconnect(connectionId, DisconnectReason.Rejected);
                return;
            }

            if (verdict == RateLimitVerdict.Drop)
            {
                Interlocked.Increment(ref _rateLimited);
                return;
            }

            if (!MessageCodec.TryReadMoveInput(buffer, offset, length, out MoveInputMessage input))
            {
                Interlocked.Increment(ref _messagesRejected);
                return;
            }

            // G-03: el mensaje lleva el input más nuevo y copias de los anteriores. Se encolan del
            // más viejo al más nuevo para que el búfer los vea en orden; el tope de mensajes por
            // segundo no cambia, porque esto es un solo datagrama.
            for (int i = input.Count - 1; i >= 0; i--)
            {
                input.TryGetDirection(i, out sbyte directionX, out sbyte directionY);
                uint sequence = input.Sequence - (uint)i;

                _zone.Inputs.TryEnqueue(new MoveIntent(entityId, sequence, directionX, directionY, isRedundant: i > 0));
            }
        }

        private void HandleHello(int connectionId, byte[] buffer, int offset, int length)
        {
            // Llegó un saludo: el nonce de esta conexión deja de valer, se haya leído bien o no.
            // Así un Hello reenviado no puede volver a usarlo.
            _challengeNonces.TryGetValue(connectionId, out byte[]? nonce);
            _challengeNonces.Remove(connectionId);

            if (!MessageCodec.TryReadHello(buffer, offset, length, out HelloMessage hello))
            {
                RejectHandshake(connectionId, RejectReason.MalformedMessage);
                return;
            }

            if (hello.ProtocolVersion != ProtocolConstants.Version)
            {
                RejectHandshake(connectionId, RejectReason.ProtocolVersion);
                return;
            }

            if (hello.TickRate != (ushort)SimulationConstants.TickRate)
            {
                RejectHandshake(connectionId, RejectReason.TickRate);
                return;
            }

            if (!MapHashMatches(hello.MapHash))
            {
                RejectHandshake(connectionId, RejectReason.MapMismatch);
                return;
            }

            if (nonce == null)
            {
                // Sin desafío no se puede comprobar que el saludo sea de ahora mismo. Se registra
                // el motivo con la conexión y no se cuenta como intento fallido de la cuenta.
                Console.Error.WriteLine(
                    "AVISO: saludo rechazado en la conexión " + connectionId +
                    ": no había un desafío pendiente (¿reenviado o fuera de orden?).");
                Interlocked.Increment(ref _loginsRejected);
                RejectHandshake(connectionId, RejectReason.InvalidCredentials);
                return;
            }

            if (_repository == null)
            {
                _handshakeDeadlines.Remove(connectionId);

                // Modo sin persistencia (pruebas o configuración explícita): la identidad es local.
                // No hay hilo de login donde descifrar, así que las credenciales no se validan.
                _control.Enqueue(ControlEvent.Join(connectionId, hello, 0, string.Empty, null));
                return;
            }

            string accountName = LoginValidation.NormalizeAccount(hello.AccountName);
            string characterName = LoginValidation.ResolveCharacterName(hello.CharacterName, accountName);

            if (!LoginValidation.IsValidAccountName(accountName) ||
                !LoginValidation.IsValidCharacterName(characterName))
            {
                Interlocked.Increment(ref _loginsRejected);
                RejectHandshake(connectionId, RejectReason.InvalidCredentials);
                return;
            }

            // Límite por cuenta: si ya agotó sus intentos en este minuto, no se encola nada y no
            // se gasta ni un ciclo de PBKDF2.
            if (IsAccountRateLimited(accountName))
            {
                Interlocked.Increment(ref _loginsRejected);
                Interlocked.Increment(ref _rateLimited);
                RejectHandshake(connectionId, RejectReason.RateLimited);
                return;
            }

            // Tope de logins en vuelo: cuenta lo que el hilo de login tiene pendiente (cola y
            // cálculo), no las conexiones abiertas. Si contáramos las conexiones, un cliente
            // podría saludar y desconectarse en bucle mientras la cola crece sin tope.
            if (_login!.InFlight >= _config.MaxPendingLogins)
            {
                Interlocked.Increment(ref _rateLimited);
                RejectHandshake(connectionId, RejectReason.RateLimited);
                return;
            }

            _pendingLogins[connectionId] = hello;
            _handshakeDeadlines.Remove(connectionId);

            // Se cuenta en vuelo antes de encolar: mientras el resultado no se recoja, este saludo
            // sigue ocupando un intento de la cuenta.
            _loginsInFlightPerAccount.TryGetValue(accountName, out int inFlight);
            _loginsInFlightPerAccount[accountName] = inFlight + 1;

            // El alta la resolverá el hilo de login y llegará por la cola de resultados.
            _login!.Enqueue(new LoginRequest(
                connectionId,
                accountName,
                hello.EncryptedSecrets,
                nonce,
                characterName,
                _zone.Id,
                _config.Spawn,
                _mapHash));
        }

        /// <summary>
        /// Vacía la cola de resultados del hilo de login. Aquí se aplica el rechazo o se reserva la
        /// cuenta y se encola el alta, todo en el hilo de red.
        /// </summary>
        private void DrainLogins()
        {
            if (_login == null)
            {
                return;
            }

            while (_login.TryDequeueResult(out LoginResult? result))
            {
                // El intento deja de estar en vuelo aunque la conexión se haya ido: el hueco del
                // tope global y el de la cuenta se liberan al sacar el resultado, como antes.
                ReleaseLoginInFlight(result.AccountName);

                if (!_pendingLogins.TryGetValue(result.ConnectionId, out HelloMessage hello))
                {
                    // La conexión se cerró mientras se autenticaba: el resultado se descarta y no
                    // se reserva nada.
                    continue;
                }

                _pendingLogins.Remove(result.ConnectionId);

                if (result.Error != null)
                {
                    Interlocked.Increment(ref _loginsRejected);
                    Console.Error.WriteLine("ERROR: no se pudo autenticar la cuenta: " + result.Error);
                    RejectHandshake(result.ConnectionId, RejectReason.Unknown);
                    continue;
                }

                if (!result.Success)
                {
                    Interlocked.Increment(ref _loginsRejected);

                    if (result.CryptoFailure != null)
                    {
                        // Descifrado fallido o nonce de otra sesión: se registra con la conexión y
                        // no suma al cupo de la cuenta.
                        Console.Error.WriteLine(
                            "AVISO: saludo rechazado en la conexión " + result.ConnectionId +
                            ": " + result.CryptoFailure + ".");
                        RejectHandshake(result.ConnectionId, result.Failure);
                        continue;
                    }

                    // Solo cuenta el fallo de una cuenta que existe: una invitación incorrecta de
                    // una cuenta inexistente no debe llenarle el cupo a ese nombre.
                    if (result.Failure == RejectReason.InvalidCredentials && result.CountsAsFailedAttempt)
                    {
                        RecordFailedLogin(result.AccountName);
                    }

                    RejectHandshake(result.ConnectionId, result.Failure);
                    continue;
                }

                // La reserva es por cuenta: si ya estaba conectada, pasa a la conexión nueva y la
                // simulación echará a la anterior al procesar el alta.
                int replacedConnectionId = ReserveAccount(result.AccountId, result.ConnectionId);
                _connectionToAccount[result.ConnectionId] = result.AccountId;
                Interlocked.Increment(ref _loginsSucceeded);

                _control.Enqueue(ControlEvent.Join(
                    result.ConnectionId,
                    hello,
                    result.AccountId,
                    result.AccountName,
                    result.Character,
                    replacedConnectionId));
            }
        }

        /// <summary>
        /// Reserva la cuenta para esta conexión. Devuelve la conexión anterior si la cuenta ya
        /// estaba conectada, o <see cref="NoConnection"/> si es la primera.
        /// </summary>
        private int ReserveAccount(int accountId, int connectionId)
        {
            while (true)
            {
                if (_onlineAccounts.TryGetValue(accountId, out int existing))
                {
                    if (_onlineAccounts.TryUpdate(accountId, connectionId, existing))
                    {
                        return existing;
                    }
                }
                else if (_onlineAccounts.TryAdd(accountId, connectionId))
                {
                    return NoConnection;
                }
            }
        }

        /// <summary>
        /// ¿La cuenta ya agotó los intentos del minuto actual? Se cuentan los fallos y los logins
        /// que aún están en vuelo: si no, dieciséis saludos seguidos entrarían todos antes de que
        /// volviera el primer resultado.
        /// </summary>
        private bool IsAccountRateLimited(string accountName)
        {
            long minute = Environment.TickCount64 / 60_000;
            ExpireFailedLogins(minute);

            _failedLogins.TryGetValue(accountName, out int failures);
            _loginsInFlightPerAccount.TryGetValue(accountName, out int inFlight);

            return failures + inFlight >= _config.MaxFailedLoginsPerAccountPerMinute;
        }

        /// <summary>Descuenta un login en vuelo de su cuenta; al llegar a cero se quita la entrada.</summary>
        private void ReleaseLoginInFlight(string accountName)
        {
            if (!_loginsInFlightPerAccount.TryGetValue(accountName, out int inFlight))
            {
                return;
            }

            if (inFlight <= 1)
            {
                _loginsInFlightPerAccount.Remove(accountName);
            }
            else
            {
                _loginsInFlightPerAccount[accountName] = inFlight - 1;
            }
        }

        private void RecordFailedLogin(string accountName)
        {
            long minute = Environment.TickCount64 / 60_000;
            ExpireFailedLogins(minute);

            _failedLogins.TryGetValue(accountName, out int failures);
            _failedLogins[accountName] = failures + 1;
        }

        /// <summary>Al cambiar de minuto se tira la ventana anterior: el diccionario no crece.</summary>
        private void ExpireFailedLogins(long minute)
        {
            if (minute == _currentMinute)
            {
                return;
            }

            _currentMinute = minute;
            _failedLogins.Clear();
        }

        private void RejectHandshake(int connectionId, RejectReason reason)
        {
            Interlocked.Increment(ref _handshakesRejected);

            _handshakeDeadlines.Remove(connectionId);
            _messagesThisSecond.Remove(connectionId);

            byte[] bytes = MessageCodec.EncodeReject(new RejectMessage(reason));
            _transport.Send(connectionId, bytes, bytes.Length, DeliveryMode.ReliableOrdered);
            ScheduleDisconnect(connectionId, DisconnectReason.Rejected);
        }

        private void ExpireHandshakes()
        {
            if (_handshakeDeadlines.Count == 0)
            {
                return;
            }

            long now = Environment.TickCount64;
            List<int>? expired = null;

            foreach (KeyValuePair<int, long> pair in _handshakeDeadlines)
            {
                if (pair.Value <= now)
                {
                    (expired ??= new List<int>()).Add(pair.Key);
                }
            }

            if (expired == null)
            {
                return;
            }

            for (int i = 0; i < expired.Count; i++)
            {
                int connectionId = expired[i];

                Interlocked.Increment(ref _handshakesRejected);
                _handshakeDeadlines.Remove(connectionId);
                _messagesThisSecond.Remove(connectionId);

                byte[] bytes = MessageCodec.EncodeReject(new RejectMessage(RejectReason.Timeout));
                _transport.Send(connectionId, bytes, bytes.Length, DeliveryMode.ReliableOrdered);
                ScheduleDisconnect(connectionId, DisconnectReason.Timeout);
            }
        }

        private void DrainOutbound()
        {
            while (_outbound.TryDequeue(out OutboundMessage message))
            {
                if (message.Data != null)
                {
                    _transport.Send(message.ConnectionId, message.Data, message.Data.Length, message.Mode);
                }

                if (message.DisconnectAfter)
                {
                    // El rechazo acaba de salir en la línea de arriba; el corte espera para que
                    // el mensaje fiable llegue primero.
                    ScheduleDisconnect(message.ConnectionId, DisconnectReason.Rejected);
                }
            }
        }

        /// <summary>
        /// Apunta una conexión para cerrarla cuando venza el retardo. El envío del <c>Reject</c>
        /// ya se hizo; solo falta el corte, que se hará en <see cref="ExpirePendingDisconnects"/>.
        /// </summary>
        private void ScheduleDisconnect(int connectionId, DisconnectReason reason)
        {
            _pendingDisconnects[connectionId] = new PendingDisconnect(
                Environment.TickCount64 + _config.RejectDisconnectDelayMilliseconds,
                reason);
        }

        /// <summary>
        /// Cierra las conexiones cuyo retardo de corte ya venció. Se llama al principio de cada
        /// vuelta: una conexión apuntada en esta vuelta no se corta hasta la siguiente.
        /// </summary>
        private void ExpirePendingDisconnects()
        {
            if (_pendingDisconnects.Count == 0)
            {
                return;
            }

            long now = Environment.TickCount64;
            List<int>? expired = null;

            foreach (KeyValuePair<int, PendingDisconnect> pair in _pendingDisconnects)
            {
                if (!pair.Value.DisconnectIssued && pair.Value.Deadline <= now)
                {
                    (expired ??= new List<int>()).Add(pair.Key);
                }
            }

            if (expired == null)
            {
                return;
            }

            for (int i = 0; i < expired.Count; i++)
            {
                int connectionId = expired[i];
                PendingDisconnect pending = _pendingDisconnects[connectionId];

                // Se conserva hasta que el transporte avise de la desconexión: así sus mensajes
                // rezagados se siguen descartando y no se repite el corte.
                _pendingDisconnects[connectionId] = pending.Issued();
                _transport.Disconnect(connectionId, pending.Reason);
            }
        }

        /// <summary>
        /// Tope de mensajes por conexión y segundo: un cliente no puede inundar la cola. Hasta el
        /// tope se acepta; por encima se descarta; a partir de <see cref="RateLimitDisconnectFactor"/>
        /// veces el tope se considera abuso y se cierra la conexión. Además, quien se queda justo
        /// por encima durante <see cref="ServerConfig.MaxSecondsOverMessageLimit"/> segundos del
        /// mismo minuto también se desconecta: inundar despacio no deja la sesión abierta para
        /// siempre.
        /// </summary>
        private RateLimitVerdict CheckRate(int connectionId)
        {
            long now = _clock();
            long second = now / 1000;
            long minute = now / 60_000;

            if (minute != _currentRateMinute)
            {
                _currentRateMinute = minute;
                _secondsOverLimit.Clear();
                _lastOverLimitSecond.Clear();
            }

            if (second != _currentSecond)
            {
                _currentSecond = second;
                _messagesThisSecond.Clear();
            }

            _messagesThisSecond.TryGetValue(connectionId, out int count);
            count++;
            _messagesThisSecond[connectionId] = count;

            int limit = _config.MaxMessagesPerSecondPerConnection;

            if (count > limit * RateLimitDisconnectFactor)
            {
                RemoveRateLimitState(connectionId);
                return RateLimitVerdict.Disconnect;
            }

            if (count > limit)
            {
                // Una vez por segundo: el primer mensaje que pasa del tope marca ese segundo. Los
                // demás del mismo segundo no vuelven a sumar.
                if (!_lastOverLimitSecond.TryGetValue(connectionId, out long markedSecond) ||
                    markedSecond != second)
                {
                    _lastOverLimitSecond[connectionId] = second;

                    _secondsOverLimit.TryGetValue(connectionId, out int seconds);
                    seconds++;
                    _secondsOverLimit[connectionId] = seconds;

                    if (seconds >= _config.MaxSecondsOverMessageLimit)
                    {
                        RemoveRateLimitState(connectionId);
                        return RateLimitVerdict.Disconnect;
                    }
                }

                return RateLimitVerdict.Drop;
            }

            return RateLimitVerdict.Allow;
        }

        private void RemoveRateLimitState(int connectionId)
        {
            _messagesThisSecond.Remove(connectionId);
            _secondsOverLimit.Remove(connectionId);
            _lastOverLimitSecond.Remove(connectionId);
        }

        private bool MapHashMatches(byte[] mapHash)
        {
            if (mapHash is null || mapHash.Length != _mapHash.Length)
            {
                return false;
            }

            int difference = 0;
            for (int i = 0; i < mapHash.Length; i++)
            {
                difference |= mapHash[i] ^ _mapHash[i];
            }

            return difference == 0;
        }

        // ---------------------------------------------------------------- hilo de simulación

        private void AddPlayer(Zone zone, ControlEvent control)
        {
            int connectionId = control.ConnectionId;

            if (_sessions.ContainsKey(connectionId))
            {
                ReleaseConnection(connectionId);
                return;
            }

            // Misma cuenta en otra conexión: se echa a la anterior antes de contar el aforo, así
            // reconectar no depende de que quede hueco. Si es el mismo personaje, se conserva la
            // posición viva que tenía (más reciente que la de la base de datos).
            Vec2? previousPosition = control.ReplacesConnectionId != NoConnection
                ? ReplaceSession(control.ReplacesConnectionId, control.Character?.Id ?? 0)
                : null;

            if (_sessions.Count >= _config.MaxPlayers)
            {
                ReleaseConnection(connectionId);

                byte[] reject = MessageCodec.EncodeReject(new RejectMessage(RejectReason.ServerFull));
                Enqueue(connectionId, reject, DeliveryMode.ReliableOrdered, disconnectAfter: true);
                return;
            }

            Vec2 spawn = _config.Spawn;
            int characterId = _nextCharacterId++;

            if (control.Character != null)
            {
                characterId = control.Character.Id;
                spawn = previousPosition ?? ResolveSpawn(zone, control.Character);
            }

            PlayerEntity player = zone.SpawnPlayer(
                characterId,
                spawn,
                _config.PlayerHalfSize,
                _config.InputBufferCapacity);

            player.ConnectionId = connectionId;

            _sessions[connectionId] = new Session(
                connectionId,
                player.Id,
                control.AccountId,
                control.AccountName,
                control.Character);

            _connectionToEntity[connectionId] = player.Id;
            Interlocked.Increment(ref _playersJoined);

            byte[] welcome = MessageCodec.EncodeWelcome(new WelcomeMessage(
                ProtocolConstants.Version,
                (ushort)SimulationConstants.TickRate,
                player.Id,
                unchecked((uint)zone.TickCount),
                control.Hello.ClientToken,
                player.Position.X,
                player.Position.Y,
                _config.PlayerSpeed,
                player.HalfSize.X,
                player.HalfSize.Y,
                _mapHash));

            Enqueue(connectionId, welcome, DeliveryMode.ReliableOrdered, disconnectAfter: false);
        }

        /// <summary>
        /// Retira la sesión anterior de la misma cuenta: guarda, quita su entidad y le avisa con
        /// <see cref="RejectReason.LoggedInElsewhere"/>. Devuelve su posición si el personaje es el
        /// mismo que va a entrar, para no aparecer en la posición vieja de la base de datos.
        /// </summary>
        private Vec2? ReplaceSession(int previousConnectionId, int characterId)
        {
            if (!_sessions.TryGetValue(previousConnectionId, out Session? previous))
            {
                return null;
            }

            Vec2? position = null;

            if (characterId > 0 &&
                previous.Character != null &&
                previous.Character.Id == characterId &&
                _zone.Entities.TryGet(previous.EntityId, out Entity? entity) &&
                entity is PlayerEntity player)
            {
                position = player.Position;
            }

            RemoveSession(previousConnectionId, RejectReason.LoggedInElsewhere);
            return position;
        }

        private void RemovePlayer(int connectionId) => RemoveSession(connectionId, null);

        private void RemoveSession(int connectionId, RejectReason? rejectReason)
        {
            if (!_sessions.TryGetValue(connectionId, out Session? session))
            {
                // Puede ser una conexión que reservó cuenta y se fue antes de que la simulación
                // procesara el alta. Hay que soltar la reserva igualmente.
                ReleaseConnection(connectionId);
                return;
            }

            if (_zone.Entities.TryGet(session.EntityId, out Entity? entity) && entity is PlayerEntity player)
            {
                SaveSession(session, player, _zone);
            }

            _sessions.Remove(connectionId);
            _zone.RemoveEntity(session.EntityId);
            _connectionToEntity.TryRemove(connectionId, out _);
            ReleaseConnection(connectionId);
            Interlocked.Increment(ref _playersLeft);

            if (rejectReason.HasValue)
            {
                byte[] bytes = MessageCodec.EncodeReject(new RejectMessage(rejectReason.Value));
                Enqueue(connectionId, bytes, DeliveryMode.ReliableOrdered, disconnectAfter: true);
            }
        }

        /// <summary>
        /// Elige el punto de aparición. Una posición guardada solo vale si es del mismo mapa y
        /// zona y la caja cabe: si el mapa cambió, es preferible reaparecer a quedar atrapado.
        /// </summary>
        private Vec2 ResolveSpawn(Zone zone, CharacterRecord character)
        {
            if (character.ZoneId != zone.Id || !MapHashMatches(character.MapHash))
            {
                return _config.Spawn;
            }

            return zone.Map.IsBoxWalkable(character.Position, _config.PlayerHalfSize)
                ? character.Position
                : _config.Spawn;
        }

        private void SaveAllPlayers(Zone zone)
        {
            foreach (KeyValuePair<int, Session> pair in _sessions)
            {
                Session session = pair.Value;

                if (zone.Entities.TryGet(session.EntityId, out Entity? entity) && entity is PlayerEntity player)
                {
                    SaveSession(session, player, zone);
                }
            }
        }

        private void SaveSession(Session session, PlayerEntity player, Zone zone)
        {
            if (_saves == null || session.Character == null)
            {
                return;
            }

            session.Character = session.Character.WithPosition(zone.Id, player.Position, _mapHash);
            _saves.Enqueue(session.Character);
        }

        private void ReleaseConnection(int connectionId)
        {
            if (_connectionToAccount.TryRemove(connectionId, out int accountId))
            {
                // Solo se suelta si la reserva sigue siendo de esta conexión: la baja tardía de la
                // conexión antigua no debe quitarle la cuenta a la nueva.
                _onlineAccounts.TryRemove(new KeyValuePair<int, int>(accountId, connectionId));
            }
        }

        private byte[] BuildSnapshot(Zone zone, PlayerEntity recipient)
        {
            zone.CollectVisibleEntities(recipient, _visibleBuffer);

            int available = _visibleBuffer.Count;

            if (available > ProtocolConstants.MaxEntitiesPerSnapshot)
            {
                // El área de interés mantiene la lista corta. Si aun así se supera el tope (por
                // ejemplo, una zona llena de PNJ), se recorta y se cuenta en vez de romper el tick.
                // El destinatario va primero, así que nunca se cae a sí mismo.
                Interlocked.Increment(ref _snapshotsTruncated);
                available = ProtocolConstants.MaxEntitiesPerSnapshot;
            }

            EntityState[] states = available == 0
                ? Array.Empty<EntityState>()
                : new EntityState[available];

            for (int i = 0; i < available; i++)
            {
                Entity entity = _visibleBuffer[i];
                var player = entity as PlayerEntity;

                states[i] = new EntityState(
                    entity.Id,
                    ProtocolConstants.QuantizePosition(entity.Position.X),
                    ProtocolConstants.QuantizePosition(entity.Position.Y),
                    player?.DirectionX ?? 0,
                    player?.DirectionY ?? 0,
                    entity.Kind);
            }

            byte[] bytes = MessageCodec.EncodeSnapshot(new SnapshotMessage(
                unchecked((uint)zone.TickCount),
                recipient.LastProcessedSequence,
                states));

            Interlocked.Add(ref _entitiesSent, available);
            Interlocked.Add(ref _snapshotBytes, bytes.Length);
            return bytes;
        }

        private void Enqueue(int connectionId, byte[] data, DeliveryMode mode, bool disconnectAfter)
        {
            // Un mensaje fiable no se tira nunca: son pocos (saludo y rechazo). Los snapshots sí,
            // porque el siguiente reemplaza al anterior.
            if (mode != DeliveryMode.ReliableOrdered && _outbound.Count >= _config.MaxQueuedOutboundMessages)
            {
                Interlocked.Increment(ref _droppedOutbound);
                return;
            }

            _outbound.Enqueue(new OutboundMessage(connectionId, data, mode, disconnectAfter));
        }

        // ---------------------------------------------------------------- tipos internos

        private enum ControlEventKind : byte
        {
            Join = 0,
            Leave = 1,
        }

        /// <summary>Qué hacer con un mensaje según la frecuencia con la que llega.</summary>
        private enum RateLimitVerdict : byte
        {
            Allow = 0,
            Drop = 1,
            Disconnect = 2,
        }

        /// <summary>
        /// Corte apuntado después de enviar un <c>Reject</c>. Se guarda el motivo para el
        /// transporte y si el corte ya se pidió, para no repetirlo mientras llega el aviso de
        /// desconexión real.
        /// </summary>
        private readonly struct PendingDisconnect
        {
            public PendingDisconnect(long deadline, DisconnectReason reason)
                : this(deadline, reason, false)
            {
            }

            private PendingDisconnect(long deadline, DisconnectReason reason, bool disconnectIssued)
            {
                Deadline = deadline;
                Reason = reason;
                DisconnectIssued = disconnectIssued;
            }

            public long Deadline { get; }

            public DisconnectReason Reason { get; }

            public bool DisconnectIssued { get; }

            public PendingDisconnect Issued() => new PendingDisconnect(Deadline, Reason, true);
        }

        private readonly struct ControlEvent
        {
            private ControlEvent(
                ControlEventKind kind,
                int connectionId,
                HelloMessage hello,
                int accountId,
                string accountName,
                CharacterRecord? character,
                int replacesConnectionId)
            {
                Kind = kind;
                ConnectionId = connectionId;
                Hello = hello;
                AccountId = accountId;
                AccountName = accountName;
                Character = character;
                ReplacesConnectionId = replacesConnectionId;
            }

            public ControlEventKind Kind { get; }

            public int ConnectionId { get; }

            public HelloMessage Hello { get; }

            public int AccountId { get; }

            public string AccountName { get; }

            /// <summary>Personaje cargado de la base de datos; <c>null</c> en el modo sin persistencia.</summary>
            public CharacterRecord? Character { get; }

            /// <summary>
            /// Conexión anterior de la misma cuenta (hay que echarla), o <see cref="NoConnection"/>
            /// si no había.
            /// </summary>
            public int ReplacesConnectionId { get; }

            public static ControlEvent Join(
                int connectionId,
                HelloMessage hello,
                int accountId,
                string accountName,
                CharacterRecord? character,
                int replacesConnectionId = NoConnection) =>
                new ControlEvent(
                    ControlEventKind.Join,
                    connectionId,
                    hello,
                    accountId,
                    accountName,
                    character,
                    replacesConnectionId);

            public static ControlEvent Leave(int connectionId) =>
                new ControlEvent(
                    ControlEventKind.Leave,
                    connectionId,
                    default,
                    0,
                    string.Empty,
                    null,
                    NoConnection);
        }

        private readonly struct OutboundMessage
        {
            public OutboundMessage(int connectionId, byte[] data, DeliveryMode mode, bool disconnectAfter)
            {
                ConnectionId = connectionId;
                Data = data;
                Mode = mode;
                DisconnectAfter = disconnectAfter;
            }

            public int ConnectionId { get; }

            public byte[] Data { get; }

            public DeliveryMode Mode { get; }

            /// <summary>Si hay que cerrar la conexión después de enviar (rechazo por aforo).</summary>
            public bool DisconnectAfter { get; }
        }

        private sealed class Session
        {
            public Session(
                int connectionId,
                int entityId,
                int accountId,
                string accountName,
                CharacterRecord? character)
            {
                ConnectionId = connectionId;
                EntityId = entityId;
                AccountId = accountId;
                AccountName = accountName ?? string.Empty;
                Character = character;
            }

            public int ConnectionId { get; }

            public int EntityId { get; }

            public int AccountId { get; }

            public string AccountName { get; }

            /// <summary>Última foto conocida del personaje; se actualiza al guardar.</summary>
            public CharacterRecord? Character { get; set; }
        }
    }

    /// <summary>Contadores de red para los informes.</summary>
    public readonly struct NetworkStats
    {
        public NetworkStats(
            int connections,
            long messagesReceived,
            long bytesReceived,
            long messagesRejected,
            long handshakesRejected,
            long rateLimited,
            long rateLimitedDisconnects,
            long droppedOutbound,
            long playersJoined,
            long playersLeft,
            long snapshotsSent,
            long snapshotsTruncated,
            long entitiesSent,
            long snapshotBytes,
            long loginsSucceeded,
            long loginsRejected,
            long charactersSaved,
            long saveFailures,
            int pendingSaves,
            string? saveLastError = null)
        {
            Connections = connections;
            MessagesReceived = messagesReceived;
            BytesReceived = bytesReceived;
            MessagesRejected = messagesRejected;
            HandshakesRejected = handshakesRejected;
            RateLimited = rateLimited;
            RateLimitedDisconnects = rateLimitedDisconnects;
            DroppedOutbound = droppedOutbound;
            PlayersJoined = playersJoined;
            PlayersLeft = playersLeft;
            SnapshotsSent = snapshotsSent;
            SnapshotsTruncated = snapshotsTruncated;
            EntitiesSent = entitiesSent;
            SnapshotBytes = snapshotBytes;
            LoginsSucceeded = loginsSucceeded;
            LoginsRejected = loginsRejected;
            CharactersSaved = charactersSaved;
            SaveFailures = saveFailures;
            PendingSaves = pendingSaves;
            SaveLastError = saveLastError;
        }

        public int Connections { get; }

        public long MessagesReceived { get; }

        /// <summary>Bytes de carga útil recibidos (lo que llega a <c>OnReceived</c>).</summary>
        public long BytesReceived { get; }

        public long MessagesRejected { get; }

        public long HandshakesRejected { get; }

        public long RateLimited { get; }

        /// <summary>Conexiones cerradas por insistir por encima del límite de frecuencia.</summary>
        public long RateLimitedDisconnects { get; }

        public long DroppedOutbound { get; }

        public long PlayersJoined { get; }

        public long PlayersLeft { get; }

        public long SnapshotsSent { get; }

        public long SnapshotsTruncated { get; }

        /// <summary>Suma de entidades incluidas en todos los snapshots generados.</summary>
        public long EntitiesSent { get; }

        /// <summary>Bytes de snapshot generados (cabecera, lista de entidades y relleno).</summary>
        public long SnapshotBytes { get; }

        /// <summary>Cuentas autenticadas (o registradas) correctamente.</summary>
        public long LoginsSucceeded { get; }

        /// <summary>Saludos rechazados por credenciales, invitación o personaje en uso.</summary>
        public long LoginsRejected { get; }

        /// <summary>Personajes escritos por el hilo de guardado.</summary>
        public long CharactersSaved { get; }

        /// <summary>Intentos de guardado que fallaron.</summary>
        public long SaveFailures { get; }

        /// <summary>Personajes esperando a que SQLite los escriba.</summary>
        public int PendingSaves { get; }

        /// <summary>Último error de guardado, si lo hubo.</summary>
        public string? SaveLastError { get; }
    }
}
