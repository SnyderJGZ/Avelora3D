#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private readonly string _registrationCode;
        private readonly long _saveEveryTicks;
        private long _nextSaveTick;

        /// <summary>
        /// Cuántas veces el tope se tolera antes de cortar. Los inputs van a 20/s y el tope es 90:
        /// un pico de reenvío no corta, pero insistir sí.
        /// </summary>
        private const int RateLimitDisconnectFactor = 3;

        // Frontera entre hilos.
        private readonly ConcurrentQueue<ControlEvent> _control = new ConcurrentQueue<ControlEvent>();
        private readonly ConcurrentQueue<OutboundMessage> _outbound = new ConcurrentQueue<OutboundMessage>();
        private readonly ConcurrentDictionary<int, int> _connectionToEntity = new ConcurrentDictionary<int, int>();
        private readonly ConcurrentDictionary<int, int> _connectionToCharacter = new ConcurrentDictionary<int, int>();
        private readonly ConcurrentDictionary<int, int> _onlineCharacters = new ConcurrentDictionary<int, int>();

        // Solo hilo de red.
        private readonly Dictionary<int, long> _handshakeDeadlines = new Dictionary<int, long>();
        private readonly Dictionary<int, int> _messagesThisSecond = new Dictionary<int, int>();
        private long _currentSecond;

        // Solo hilo de simulación.
        private readonly Dictionary<int, Session> _sessions = new Dictionary<int, Session>();
        private readonly List<Entity> _visibleBuffer = new List<Entity>(64);
        private int _nextCharacterId;

        private Thread? _netThread;
        private volatile bool _stopping;

        private long _messagesReceived;
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
            string? registrationCode = null)
        {
            _zone = zone ?? throw new ArgumentNullException(nameof(zone));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));

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

            _transport.Connected += OnConnected;
            _transport.Disconnected += OnDisconnected;
            _transport.Received += OnReceived;
        }

        /// <summary>Puerto UDP efectivo.</summary>
        public int Port => _transport.Port;

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
        }

        public void Dispose() => Stop();

        /// <summary>
        /// Una vuelta del hilo de red: procesar eventos, vaciar la cola de salida y expirar los
        /// saludos que no llegan. Es público para poder probarlo sin arrancar el hilo.
        /// </summary>
        public void PollOnce()
        {
            _transport.Poll();
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
            _saves?.Pending ?? 0);

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
        }

        private void OnDisconnected(int connectionId, DisconnectReason reason)
        {
            _handshakeDeadlines.Remove(connectionId);
            _messagesThisSecond.Remove(connectionId);

            // La baja se aplica en el hilo de simulación: aquí solo se avisa.
            _control.Enqueue(ControlEvent.Leave(connectionId));
        }

        private void OnReceived(int connectionId, byte[] buffer, int offset, int length)
        {
            Interlocked.Increment(ref _messagesReceived);

            if (length <= 0 || length > ProtocolConstants.MaxMessageSize)
            {
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
                _transport.Disconnect(connectionId, DisconnectReason.Rejected);
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

            if (!MovementSimulator.IsValidDirection(input.DirectionX, input.DirectionY))
            {
                // El códec ya lo rechaza, pero se comprueba también aquí: la red no debe encolar
                // direcciones que la simulación no sabe interpretar.
                Interlocked.Increment(ref _messagesRejected);
                return;
            }

            _zone.Inputs.TryEnqueue(new MoveIntent(entityId, input.Sequence, input.DirectionX, input.DirectionY));
        }

        private void HandleHello(int connectionId, byte[] buffer, int offset, int length)
        {
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

            if (_repository == null)
            {
                _handshakeDeadlines.Remove(connectionId);

                // Modo sin persistencia (pruebas o configuración explícita): la identidad es local.
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

            try
            {
                AccountLoginResult login = _repository.Login(
                    accountName,
                    hello.Password,
                    hello.InvitationCode,
                    _registrationCode);

                if (!login.Success)
                {
                    Interlocked.Increment(ref _loginsRejected);
                    RejectHandshake(connectionId, login.Failure);
                    return;
                }

                CharacterRecord character = _repository.GetOrCreateCharacter(
                    login.AccountId,
                    characterName,
                    _zone.Id,
                    _config.Spawn,
                    _mapHash);

                // Dos conexiones no pueden llevar el mismo personaje: una pisaría el guardado de
                // la otra. La reserva se libera al procesar la baja.
                if (!_onlineCharacters.TryAdd(character.Id, connectionId))
                {
                    Interlocked.Increment(ref _loginsRejected);
                    RejectHandshake(connectionId, RejectReason.CharacterInUse);
                    return;
                }

                _connectionToCharacter[connectionId] = character.Id;
                _handshakeDeadlines.Remove(connectionId);
                Interlocked.Increment(ref _loginsSucceeded);

                // El alta la hace la simulación: aquí todavía no se toca el mundo.
                _control.Enqueue(ControlEvent.Join(
                    connectionId,
                    hello,
                    login.AccountId,
                    accountName,
                    character));
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _loginsRejected);
                Console.Error.WriteLine("ERROR: no se pudo autenticar la cuenta: " + ex.Message);
                RejectHandshake(connectionId, RejectReason.Unknown);
            }
        }

        private void RejectHandshake(int connectionId, RejectReason reason)
        {
            Interlocked.Increment(ref _handshakesRejected);

            _handshakeDeadlines.Remove(connectionId);
            _messagesThisSecond.Remove(connectionId);

            byte[] bytes = MessageCodec.EncodeReject(new RejectMessage(reason));
            _transport.Send(connectionId, bytes, bytes.Length, DeliveryMode.ReliableOrdered);
            _transport.Disconnect(connectionId, DisconnectReason.Rejected);
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
                _transport.Disconnect(connectionId, DisconnectReason.Timeout);
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
                    _transport.Disconnect(message.ConnectionId, DisconnectReason.Rejected);
                }
            }
        }

        /// <summary>
        /// Tope de mensajes por conexión y segundo: un cliente no puede inundar la cola. Hasta el
        /// tope se acepta; por encima se descarta; a partir de <see cref="RateLimitDisconnectFactor"/>
        /// veces el tope se considera abuso y se cierra la conexión.
        /// </summary>
        private RateLimitVerdict CheckRate(int connectionId)
        {
            long second = Environment.TickCount64 / 1000;

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
                _messagesThisSecond.Remove(connectionId);
                return RateLimitVerdict.Disconnect;
            }

            return count > limit ? RateLimitVerdict.Drop : RateLimitVerdict.Allow;
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
                spawn = ResolveSpawn(zone, control.Character);
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

        private void RemovePlayer(int connectionId)
        {
            if (!_sessions.TryGetValue(connectionId, out Session? session))
            {
                // Puede ser una conexión que reservó personaje y se fue antes de que la simulación
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
            if (_connectionToCharacter.TryRemove(connectionId, out int characterId))
            {
                _onlineCharacters.TryRemove(characterId, out _);
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

        private readonly struct ControlEvent
        {
            private ControlEvent(
                ControlEventKind kind,
                int connectionId,
                HelloMessage hello,
                int accountId,
                string accountName,
                CharacterRecord? character)
            {
                Kind = kind;
                ConnectionId = connectionId;
                Hello = hello;
                AccountId = accountId;
                AccountName = accountName;
                Character = character;
            }

            public ControlEventKind Kind { get; }

            public int ConnectionId { get; }

            public HelloMessage Hello { get; }

            public int AccountId { get; }

            public string AccountName { get; }

            /// <summary>Personaje cargado de la base de datos; <c>null</c> en el modo sin persistencia.</summary>
            public CharacterRecord? Character { get; }

            public static ControlEvent Join(
                int connectionId,
                HelloMessage hello,
                int accountId,
                string accountName,
                CharacterRecord? character) =>
                new ControlEvent(
                    ControlEventKind.Join,
                    connectionId,
                    hello,
                    accountId,
                    accountName,
                    character);

            public static ControlEvent Leave(int connectionId) =>
                new ControlEvent(
                    ControlEventKind.Leave,
                    connectionId,
                    default,
                    0,
                    string.Empty,
                    null);
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
            int pendingSaves)
        {
            Connections = connections;
            MessagesReceived = messagesReceived;
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
        }

        public int Connections { get; }

        public long MessagesReceived { get; }

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
    }
}
