#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Avelora.Net;
using Avelora.Server.Configuration;
using Avelora.Server.Entities;
using Avelora.Server.Persistence;
using Avelora.Server.Progression;
using Avelora.Server.Simulation;
using Avelora.Server.Zones;
using Avelora.Server.Items;
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
        private readonly InMemoryPlayerRepository? _memoryRepository;
        private readonly CharacterSaveService? _saves;
        private readonly LoginWorker? _login;
        private readonly string _registrationCode;
        private readonly long _saveEveryTicks;
        private readonly RSA _serverKey;
        private readonly bool _ownsServerKey;
        private readonly Func<long> _clock;
        private readonly byte[]? _zoneList;
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
        private readonly ConcurrentQueue<CharacterRecord> _memorySaves = new ConcurrentQueue<CharacterRecord>();
        private readonly ConcurrentDictionary<int, int> _connectionToEntity = new ConcurrentDictionary<int, int>();
        private readonly ConcurrentDictionary<int, int> _connectionToAccount = new ConcurrentDictionary<int, int>();
        private readonly ConcurrentDictionary<int, string> _connectionToCharacterName =
            new ConcurrentDictionary<int, string>();
        private readonly ConcurrentDictionary<int, int> _onlineAccounts = new ConcurrentDictionary<int, int>();

        // Solo hilo de red.
        private readonly Dictionary<int, long> _handshakeDeadlines = new Dictionary<int, long>();
        private readonly Dictionary<int, byte[]> _challengeNonces = new Dictionary<int, byte[]>();
        private readonly Dictionary<int, long> _connectionGenerations = new Dictionary<int, long>();
        private readonly Dictionary<int, PendingDisconnect> _pendingDisconnects =
            new Dictionary<int, PendingDisconnect>();
        private readonly Dictionary<int, HelloMessage> _pendingLogins = new Dictionary<int, HelloMessage>();
        private readonly Dictionary<int, CharacterSelectState> _characterSelections =
            new Dictionary<int, CharacterSelectState>();
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
        private readonly Dictionary<int, ChatTokenBucket> _chatBuckets = new Dictionary<int, ChatTokenBucket>();

        /// <summary>Segundos, dentro del minuto actual, en los que cada conexión superó el límite.</summary>
        private readonly Dictionary<int, int> _secondsOverLimit = new Dictionary<int, int>();

        /// <summary>Último segundo en el que se contó ese exceso, para sumar una sola vez por segundo.</summary>
        private readonly Dictionary<int, long> _lastOverLimitSecond = new Dictionary<int, long>();

        private long _currentSecond;
        private long _currentMinute;
        private long _nextConnectionGeneration;

        /// <summary>
        /// Minuto de la ventana del límite de frecuencia. Va aparte de <see cref="_currentMinute"/>
        /// (la de los logins fallidos) para que limpiar una no borre la otra.
        /// </summary>
        private long _currentRateMinute;

        // Solo hilo de simulación.
        private readonly Dictionary<int, Session> _sessions = new Dictionary<int, Session>();
        private readonly List<Entity> _visibleBuffer = new List<Entity>(64);
        private readonly List<Entity> _deferredItems = new List<Entity>(64);
        private readonly List<CombatEventEntry> _combatEventEntries = new List<CombatEventEntry>(64);
        private readonly List<StatusEventEntry> _statusEventEntries = new List<StatusEventEntry>(64);

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
        private long _chatReceived;
        private long _chatDelivered;
        private long _chatRejectedInvalid;
        private long _chatRejectedLong;
        private long _chatRejectedFast;
        private long _combatEventMessages;
        private long _combatEventEntriesSent;
        private long _combatEventBytes;
        private long _combatEventsTrimmed;
        private long _lootGainedMessages;
        private long _lootGainedBytes;
        private long _zoneChangedMessages;
        private long _zoneChangedBytes;
        private long _pvpStatusMessages;
        private long _pvpStatusBytes;
        private long _pvpRejectedMessages;
        private long _skillCatalogBytes;
        private long _skillUsedMessages;
        private long _skillRejectedMessages;
        private long _resourceMessages;
        private long _resourceBytes;
        private long _statusMessages;
        private long _statusBytes;
        private long _statusEventMessages;
        private long _statusEventBytes;

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
            _memoryRepository = repository == null
                ? new InMemoryPlayerRepository(zone.Classes, config.FirstCharacterId)
                : null;
            _memoryRepository?.Initialize();
            _saves = saves;
            _registrationCode = registrationCode ?? string.Empty;

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
            _zone.InventoryChanged += OnInventoryChanged;
            _zone.ProgressChanged += OnProgressChanged;
            _zone.LootGained += OnLootGained;
            _zone.ZoneChanged += OnZoneChanged;
            _zone.PvpStatusChanged += OnPvpStatusChanged;
            _zone.PvpModeRejected += OnPvpModeRejected;
            _zone.SkillUsed += OnSkillUsed;
            _zone.SkillRejected += OnSkillRejected;
            _zone.ResourceChanged += OnResourceChanged;
            _zone.StatusEffectsChanged += OnStatusEffectsChanged;
            _zone.StatsChanged += OnStatsChanged;
            if (_zone.Zones.Zones.Length > 0)
            {
                _zoneList = MessageCodec.EncodeZoneList(new ZoneListMessage(_zone.Zones.Zones));
            }
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
            DrainMemorySaves();
            ExpireCharacterSelections();
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
                else if (control.Kind == ControlEventKind.Eject)
                {
                    RemoveSession(control.ConnectionId, RejectReason.LoggedInElsewhere);
                }
                else
                {
                    RemovePlayer(control.ConnectionId);
                }
            }

            // El guardado periódico se engancha al hilo de simulación, después de aplicar altas y
            // bajas. Encolar una foto es barato; escribir en SQLite lo hace el hilo de guardado.
            if ((_saves != null || _memoryRepository != null) && zone.TickCount >= _nextSaveTick)
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
            if (_saves != null || _memoryRepository != null)
            {
                SaveAllPlayers(zone);
            }
        }

        public void AfterTick(Zone zone)
        {
            if (_sessions.Count == 0)
            {
                zone.CombatEvents.Clear();
                zone.StatusEvents.Clear();
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
                SendCombatEvents(zone, recipient, session.ConnectionId);
                SendStatusEvents(zone, recipient, session.ConnectionId);
            }

            zone.CombatEvents.Clear();
            zone.StatusEvents.Clear();
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
            Interlocked.Read(ref _chatReceived),
            Interlocked.Read(ref _chatDelivered),
            Interlocked.Read(ref _chatRejectedInvalid),
            Interlocked.Read(ref _chatRejectedLong),
            Interlocked.Read(ref _chatRejectedFast),
            _saves?.LastError,
            Interlocked.Read(ref _combatEventMessages),
            Interlocked.Read(ref _combatEventEntriesSent),
            Interlocked.Read(ref _combatEventBytes),
            Interlocked.Read(ref _combatEventsTrimmed),
            Interlocked.Read(ref _lootGainedMessages),
            Interlocked.Read(ref _lootGainedBytes),
            Interlocked.Read(ref _zoneChangedMessages),
            Interlocked.Read(ref _zoneChangedBytes),
            Interlocked.Read(ref _pvpStatusMessages),
            Interlocked.Read(ref _pvpStatusBytes),
            Interlocked.Read(ref _pvpRejectedMessages),
            Interlocked.Read(ref _skillCatalogBytes),
            Interlocked.Read(ref _skillUsedMessages),
            Interlocked.Read(ref _skillRejectedMessages),
            Interlocked.Read(ref _resourceMessages),
            Interlocked.Read(ref _resourceBytes),
            Interlocked.Read(ref _statusMessages),
            Interlocked.Read(ref _statusBytes),
            Interlocked.Read(ref _statusEventMessages),
            Interlocked.Read(ref _statusEventBytes));

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
            _connectionGenerations[connectionId] = ++_nextConnectionGeneration;
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
            _connectionGenerations.Remove(connectionId);
            _pendingDisconnects.Remove(connectionId);
            _messagesThisSecond.Remove(connectionId);
            _chatBuckets.Remove(connectionId);
            _challengeNonces.Remove(connectionId);
            _secondsOverLimit.Remove(connectionId);
            _lastOverLimitSecond.Remove(connectionId);

            // Si estaba autenticándose, su resultado ya no interesa. Nada que reservar: la
            // conexión se ha ido antes de que el login terminara.
            _pendingLogins.Remove(connectionId);
            _characterSelections.Remove(connectionId);

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

            if (_characterSelections.ContainsKey(connectionId))
            {
                RateLimitVerdict selectionVerdict = CheckRate(connectionId);
                if (selectionVerdict == RateLimitVerdict.Disconnect)
                {
                    Interlocked.Increment(ref _rateLimited);
                    Interlocked.Increment(ref _rateLimitedDisconnects);
                    byte[] reject = MessageCodec.EncodeReject(new RejectMessage(RejectReason.RateLimited));
                    _transport.Send(connectionId, reject, reject.Length, DeliveryMode.ReliableOrdered);
                    ScheduleDisconnect(connectionId, DisconnectReason.Rejected);
                    return;
                }

                if (selectionVerdict == RateLimitVerdict.Drop)
                {
                    Interlocked.Increment(ref _rateLimited);
                    return;
                }

                HandleCharacterSelectionMessage(connectionId, buffer, offset, length);
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

            if (MessageCodec.TryReadAttack(buffer, offset, length, out AttackMessage attack))
            {
                // El objetivo lo valida la simulación: aquí solo se cruza la frontera. Si la zona
                // no tiene combate configurado, el mensaje no tiene a dónde ir y se cuenta como
                // rechazado.
                if (_zone.Attacks != null)
                {
                    _zone.Attacks.TryEnqueue(new AttackIntent(entityId, attack.TargetId));
                }
                else
                {
                    Interlocked.Increment(ref _messagesRejected);
                }

                return;
            }

            if (MessageCodec.TryReadUseSkill(buffer, offset, length, out UseSkillMessage useSkill))
            {
                // Las habilidades van por la misma cola que el ataque básico (I-16), con el mismo
                // límite de ritmo y de cola.
                if (_zone.Attacks != null)
                {
                    _zone.Attacks.TryEnqueue(new AttackIntent(entityId, useSkill.TargetId, useSkill.SkillId));
                }
                else
                {
                    Interlocked.Increment(ref _messagesRejected);
                }

                return;
            }

            if (buffer[offset] == (byte)MessageType.ChatSend)
            {
                HandleChatSend(connectionId, entityId, buffer, offset, length);
                return;
            }

            if (MessageCodec.TryReadPickUp(buffer, offset, length, out PickUpMessage pickup))
            {
                if (_zone.ItemActions != null)
                {
                    _zone.ItemActions.TryEnqueue(ItemAction.PickUp(entityId, pickup.EntityId));
                }
                else
                {
                    Interlocked.Increment(ref _messagesRejected);
                }

                return;
            }

            if (MessageCodec.TryReadUseItem(buffer, offset, length, out UseItemMessage useItem))
            {
                if (_zone.ItemActions != null)
                {
                    _zone.ItemActions.TryEnqueue(ItemAction.UseItem(entityId, useItem.Slot));
                }
                else
                {
                    Interlocked.Increment(ref _messagesRejected);
                }

                return;
            }

            if (MessageCodec.TryReadEquip(buffer, offset, length, out EquipMessage equip))
            {
                if (_zone.ItemActions != null)
                {
                    _zone.ItemActions.TryEnqueue(ItemAction.Equip(entityId, equip.Slot));
                }
                else
                {
                    Interlocked.Increment(ref _messagesRejected);
                }

                return;
            }

            if (MessageCodec.TryReadUnequip(buffer, offset, length, out UnequipMessage unequip))
            {
                if (_zone.ItemActions != null)
                {
                    _zone.ItemActions.TryEnqueue(ItemAction.Unequip(
                        entityId, unequip.EquipSlot, unequip.TargetSlot));
                }
                else
                {
                    Interlocked.Increment(ref _messagesRejected);
                }

                return;
            }

            if (MessageCodec.TryReadMoveItem(buffer, offset, length, out MoveItemMessage moveItem))
            {
                if (_zone.ItemActions != null)
                {
                    _zone.ItemActions.TryEnqueue(ItemAction.MoveItem(
                        entityId, moveItem.FromSlot, moveItem.ToSlot, moveItem.Count));
                }
                else
                {
                    Interlocked.Increment(ref _messagesRejected);
                }

                return;
            }

            if (MessageCodec.TryReadDestroyItem(buffer, offset, length, out DestroyItemMessage destroyItem))
            {
                if (_zone.ItemActions != null)
                {
                    _zone.ItemActions.TryEnqueue(ItemAction.DestroyItem(entityId, destroyItem.Slot, destroyItem.Count));
                }
                else
                {
                    Interlocked.Increment(ref _messagesRejected);
                }

                return;
            }

            if (MessageCodec.TryReadAllocatePoints(buffer, offset, length, out AllocatePointsMessage allocation))
            {
                if (_zone.ItemActions != null)
                {
                    _zone.ItemActions.TryEnqueue(ItemAction.AllocatePoints(
                        entityId, allocation.Str, allocation.Dex, allocation.Int, allocation.Vit));
                }
                else
                {
                    Interlocked.Increment(ref _messagesRejected);
                }

                return;
            }

            if (MessageCodec.TryReadResetPoints(buffer, offset, length, out _))
            {
                if (_zone.ItemActions != null)
                {
                    _zone.ItemActions.TryEnqueue(ItemAction.ResetPoints(entityId));
                }
                else
                {
                    Interlocked.Increment(ref _messagesRejected);
                }

                return;
            }

            if (MessageCodec.TryReadSetPvpMode(buffer, offset, length, out SetPvpModeMessage pvpMode))
            {
                if (_zone.ItemActions != null)
                {
                    _zone.ItemActions.TryEnqueue(ItemAction.SetPvpMode(entityId, pvpMode.Mode));
                }
                else
                {
                    Interlocked.Increment(ref _messagesRejected);
                }

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

            if (!_connectionGenerations.TryGetValue(connectionId, out long connectionGeneration))
            {
                Interlocked.Increment(ref _messagesRejected);
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

            string accountName = LoginValidation.NormalizeAccount(hello.AccountName);
            if (!LoginValidation.IsValidAccountName(accountName))
            {
                Interlocked.Increment(ref _loginsRejected);
                RejectHandshake(connectionId, RejectReason.InvalidCredentials);
                return;
            }

            if (_repository == null)
            {
                _handshakeDeadlines.Remove(connectionId);
                AccountLoginResult localLogin = _memoryRepository!.Login(
                    accountName,
                    string.Empty,
                    string.Empty,
                    string.Empty);
                int replaced = ReserveAccount(localLogin.AccountId, connectionId);
                _connectionToAccount[connectionId] = localLogin.AccountId;
                Interlocked.Increment(ref _loginsSucceeded);
                HandleReplacedConnection(replaced);
                BeginCharacterSelection(
                    connectionId,
                    connectionGeneration,
                    hello,
                    localLogin.AccountId,
                    accountName,
                    _memoryRepository.ListCharacters(localLogin.AccountId));
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
                connectionGeneration,
                accountName,
                hello.EncryptedSecrets,
                nonce,
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

                if (result.Operation != CharacterOperationKind.Login)
                {
                    HandleCharacterOperationResult(result);
                    continue;
                }

                if (!_connectionGenerations.TryGetValue(result.ConnectionId, out long currentGeneration) ||
                    currentGeneration != result.ConnectionGeneration)
                {
                    // LiteNetLib puede reutilizar el id mientras el hilo de login aún calcula.
                    // El resultado pertenece a una conexión cerrada, nunca a la nueva.
                    continue;
                }

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

                // La cuenta queda reservada desde la pantalla de selección, no solo al entrar al mundo.
                int replacedConnectionId = ReserveAccount(result.AccountId, result.ConnectionId);
                _connectionToAccount[result.ConnectionId] = result.AccountId;
                Interlocked.Increment(ref _loginsSucceeded);
                HandleReplacedConnection(replacedConnectionId);
                BeginCharacterSelection(
                    result.ConnectionId,
                    result.ConnectionGeneration,
                    hello,
                    result.AccountId,
                    result.AccountName,
                    result.Characters);
            }
        }

        private void BeginCharacterSelection(
            int connectionId,
            long connectionGeneration,
            HelloMessage hello,
            int accountId,
            string accountName,
            CharacterRecord[] characters)
        {
            long timeoutMs = (long)Math.Ceiling(_config.CharacterSelectTimeoutSeconds * 1000d);
            _characterSelections[connectionId] = new CharacterSelectState(
                connectionId,
                connectionGeneration,
                hello,
                accountId,
                accountName,
                _clock() + timeoutMs);
            EnqueueCharacterList(connectionId, characters);
        }

        private void EnqueueCharacterList(int connectionId, CharacterRecord[] characters)
        {
            CharacterListEntry[] entries = new CharacterListEntry[characters.Length];
            for (int i = 0; i < characters.Length; i++)
            {
                CharacterRecord character = characters[i];
                entries[i] = new CharacterListEntry(
                    character.Id,
                    character.Name,
                    checked((byte)character.ClassId),
                    checked((byte)character.Level));
            }

            List<CharacterClass> definitions = _zone.Classes.Classes!;
            var classes = new CharacterClassEntry[definitions.Count];
            for (int i = 0; i < definitions.Count; i++)
            {
                CharacterClass definition = definitions[i];
                classes[i] = new CharacterClassEntry(checked((byte)definition.Id), definition.Name);
            }

            byte[] bytes = MessageCodec.EncodeCharacterList(new CharacterListMessage(
                checked((byte)_config.MaxCharactersPerAccount),
                entries,
                classes));
            Enqueue(connectionId, bytes, DeliveryMode.ReliableOrdered, disconnectAfter: false);
        }

        private void HandleCharacterOperationResult(LoginResult result)
        {
            if (!_characterSelections.TryGetValue(result.ConnectionId, out CharacterSelectState? state) ||
                state.AccountId != result.AccountId ||
                state.ConnectionGeneration != result.ConnectionGeneration ||
                !_connectionGenerations.TryGetValue(result.ConnectionId, out long currentGeneration) ||
                currentGeneration != result.ConnectionGeneration)
            {
                return;
            }

            if (state.Deadline <= _clock())
            {
                ExpireCharacterSelection(result.ConnectionId);
                return;
            }

            state.OperationPending = false;
            if (result.Error != null)
            {
                Console.Error.WriteLine("ERROR: no se pudo operar con el personaje: " + result.Error);
                EnqueueCharacterRejected(result.ConnectionId, RejectReason.InternalError);
                return;
            }

            if (!result.Success)
            {
                EnqueueCharacterRejected(result.ConnectionId, result.Failure);
                return;
            }

            if (result.Operation == CharacterOperationKind.Select && result.Character != null)
            {
                _characterSelections.Remove(result.ConnectionId);
                _control.Enqueue(ControlEvent.Join(
                    result.ConnectionId,
                    state.Hello,
                    state.AccountId,
                    state.AccountName,
                    result.Character));
                return;
            }

            if (result.Operation == CharacterOperationKind.Create ||
                result.Operation == CharacterOperationKind.Delete)
            {
                EnqueueCharacterList(result.ConnectionId, result.Characters);
                return;
            }

            EnqueueCharacterRejected(result.ConnectionId, RejectReason.CharacterNotFound);
        }

        private void HandleReplacedConnection(int connectionId)
        {
            if (connectionId == NoConnection)
            {
                return;
            }

            if (_characterSelections.Remove(connectionId))
            {
                byte[] reject = MessageCodec.EncodeReject(new RejectMessage(RejectReason.LoggedInElsewhere));
                Enqueue(connectionId, reject, DeliveryMode.ReliableOrdered, disconnectAfter: true);
                return;
            }

            _control.Enqueue(ControlEvent.Eject(connectionId));
        }

        private void HandleCharacterSelectionMessage(int connectionId, byte[] buffer, int offset, int length)
        {
            if (!_characterSelections.TryGetValue(connectionId, out CharacterSelectState? state))
            {
                Interlocked.Increment(ref _messagesRejected);
                return;
            }

            if (state.Deadline <= _clock())
            {
                ExpireCharacterSelection(connectionId);
                return;
            }

            if (state.OperationPending)
            {
                Interlocked.Increment(ref _messagesRejected);
                return;
            }

            if (MessageCodec.TryReadCreateCharacter(buffer, offset, length, out CreateCharacterMessage create))
            {
                string name = LoginValidation.NormalizeCharacterName(create.Name);
                if (!LoginValidation.IsValidCharacterName(name))
                {
                    EnqueueCharacterRejected(connectionId, RejectReason.NameInvalid);
                    return;
                }

                if (_zone.Classes.Find(create.ClassId) == null)
                {
                    EnqueueCharacterRejected(connectionId, RejectReason.ClassUnknown);
                    return;
                }

                if (_repository == null)
                {
                    CharacterCreateResult created = _memoryRepository!.CreateCharacter(
                        state.AccountId,
                        name,
                        create.ClassId,
                        _config.MaxCharactersPerAccount,
                        _zone.Id,
                        _config.Spawn,
                        _mapHash);
                    if (created.Success)
                    {
                        EnqueueCharacterList(connectionId, _memoryRepository.ListCharacters(state.AccountId));
                    }
                    else
                    {
                        EnqueueCharacterRejected(connectionId, created.Failure);
                    }

                    return;
                }

                EnqueueCharacterOperation(state, CharacterOperationKind.Create, name, create.ClassId, 0);
                return;
            }

            if (MessageCodec.TryReadDeleteCharacter(buffer, offset, length, out DeleteCharacterMessage delete))
            {
                if (_repository == null)
                {
                    RejectReason failure = _memoryRepository!.DeleteCharacter(
                        state.AccountId,
                        delete.CharacterId,
                        delete.ConfirmationName);
                    if (failure == RejectReason.Unknown)
                    {
                        EnqueueCharacterList(connectionId, _memoryRepository.ListCharacters(state.AccountId));
                    }
                    else
                    {
                        EnqueueCharacterRejected(connectionId, failure);
                    }

                    return;
                }

                EnqueueCharacterOperation(
                    state,
                    CharacterOperationKind.Delete,
                    delete.ConfirmationName,
                    0,
                    delete.CharacterId);
                return;
            }

            if (MessageCodec.TryReadSelectCharacter(buffer, offset, length, out SelectCharacterMessage select))
            {
                if (_repository == null)
                {
                    if (!_memoryRepository!.TryGetCharacter(state.AccountId, select.CharacterId, out CharacterRecord? character) ||
                        character == null)
                    {
                        EnqueueCharacterRejected(connectionId, RejectReason.CharacterNotFound);
                        return;
                    }

                    _characterSelections.Remove(connectionId);
                    _control.Enqueue(ControlEvent.Join(
                        connectionId,
                        state.Hello,
                        state.AccountId,
                        state.AccountName,
                        character));
                    return;
                }

                EnqueueCharacterOperation(state, CharacterOperationKind.Select, string.Empty, 0, select.CharacterId);
                return;
            }

            // Durante la selección no se admiten acciones del juego ni otros tipos de mensaje.
            Interlocked.Increment(ref _messagesRejected);
        }

        private void EnqueueCharacterOperation(
            CharacterSelectState state,
            CharacterOperationKind operation,
            string name,
            byte classId,
            int characterId)
        {
            if (_login == null)
            {
                Interlocked.Increment(ref _messagesRejected);
                return;
            }

            state.OperationPending = true;
            _login.EnqueueCharacterOperation(new CharacterOperationRequest(
                state.ConnectionId,
                state.ConnectionGeneration,
                state.AccountId,
                state.AccountName,
                operation,
                name,
                classId,
                characterId,
                _config.MaxCharactersPerAccount,
                _zone.Id,
                _config.Spawn,
                _mapHash));
        }

        private void EnqueueCharacterRejected(int connectionId, RejectReason reason)
        {
            Interlocked.Increment(ref _messagesRejected);
            byte[] bytes = MessageCodec.EncodeCharacterRejected(new CharacterRejectedMessage(reason));
            Enqueue(connectionId, bytes, DeliveryMode.ReliableOrdered, disconnectAfter: false);
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

        private void ExpireCharacterSelections()
        {
            if (_characterSelections.Count == 0)
            {
                return;
            }

            long now = _clock();
            List<int>? expired = null;
            foreach (KeyValuePair<int, CharacterSelectState> pair in _characterSelections)
            {
                if (pair.Value.Deadline <= now)
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
                ExpireCharacterSelection(expired[i]);
            }
        }

        private void ExpireCharacterSelection(int connectionId)
        {
            if (!_characterSelections.Remove(connectionId))
            {
                return;
            }

            byte[] bytes = MessageCodec.EncodeReject(new RejectMessage(RejectReason.Timeout));
            _transport.Send(connectionId, bytes, bytes.Length, DeliveryMode.ReliableOrdered);
            ScheduleDisconnect(connectionId, DisconnectReason.Timeout);
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

            // El login leyó la base en su hilo, quizá antes de que se escribiera el guardado de la
            // sesión anterior (la que se acaba de echar, o una que salió hace un momento). La última
            // foto encolada es la buena: posición, vida e inventario vivos.
            CharacterRecord character = control.Character!;
            if (_saves != null && _saves.TryGetLatest(character.Id, out CharacterRecord latest))
            {
                character = latest;
            }

            if (_sessions.Count >= _config.MaxPlayers)
            {
                ReleaseConnection(connectionId);

                byte[] reject = MessageCodec.EncodeReject(new RejectMessage(RejectReason.ServerFull));
                Enqueue(connectionId, reject, DeliveryMode.ReliableOrdered, disconnectAfter: true);
                return;
            }

            Vec2 spawn = ResolveSpawn(zone, character);

            PlayerEntity player = zone.SpawnPlayer(
                character.Id,
                spawn,
                _config.PlayerHalfSize,
                _config.InputBufferCapacity,
                character.Health,
                character.Level,
                character.Experience,
                character.ClassId,
                character.AllocStr,
                character.AllocDex,
                character.AllocInt,
                character.AllocVit,
                character.Mana,
                character.Gold);
            player.PvpMode = character.PvpMode == 1 ? PvpMode.Pvp : PvpMode.Pve;
            player.PvpForcedTicksLeft = Math.Clamp(character.PvpForcedSeconds, 0, ushort.MaxValue) *
                SimulationConstants.TickRate;

            for (int i = 0; i < character.InventoryItems.Length; i++)
            {
                InventoryItemRecord saved = character.InventoryItems[i];
                if (saved.Slot >= 0 && saved.Slot < player.Inventory.Length)
                {
                    player.Inventory[saved.Slot] = new InventorySlot(
                        (ushort)saved.ItemId,
                        (ushort)saved.Count);
                }
            }

            Array.Copy(character.Equipment, player.Equipment, ProtocolConstants.EquipSlotCount);
            player.CharacterName = character.Name;
            zone.EnforceEquipment(player);
            zone.RecalculateEquipmentStats(player);
            // SpawnPlayer recortó la vida a la máxima sin equipo; con el equipo puesto vale la guardada.
            if (character.Health > 0)
            {
                player.Health = Math.Min(character.Health, player.MaxHealth);
            }

            if (character.Mana >= 0)
            {
                player.Mana = Math.Min(character.Mana, player.MaxMana);
            }

            player.ConnectionId = connectionId;

            string characterName = character.Name;
            characterName = TruncateUtf8(characterName, ProtocolConstants.MaxChatNameBytes);

            _sessions[connectionId] = new Session(
                connectionId,
                player.Id,
                control.AccountId,
                control.AccountName,
                character);

            _connectionToEntity[connectionId] = player.Id;
            _connectionToCharacterName[connectionId] = characterName;
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
            if (_zoneList != null)
            {
                Enqueue(connectionId, (byte[])_zoneList.Clone(), DeliveryMode.ReliableOrdered, disconnectAfter: false);
            }

            byte[][] itemCatalogChunks = EncodeItemCatalog(_zone, _config, player);
            for (int i = 0; i < itemCatalogChunks.Length; i++)
            {
                Enqueue(connectionId, itemCatalogChunks[i], DeliveryMode.ReliableOrdered, disconnectAfter: false);
            }

            if (_zone.Skills != null)
            {
                byte[] skillCatalog = EncodeSkillCatalog(_zone, player);
                Enqueue(connectionId, skillCatalog, DeliveryMode.ReliableOrdered, disconnectAfter: false);
                Interlocked.Add(ref _skillCatalogBytes, skillCatalog.Length);
            }

            Enqueue(connectionId, EncodeInventory(player), DeliveryMode.ReliableOrdered, disconnectAfter: false);
            Enqueue(connectionId, EncodeProgress(player), DeliveryMode.ReliableOrdered, disconnectAfter: false);
            Enqueue(connectionId, EncodeStats(player), DeliveryMode.ReliableOrdered, disconnectAfter: false);
        }

        private void RemovePlayer(int connectionId) => RemoveSession(connectionId, null);

        private void RemoveSession(int connectionId, RejectReason? rejectReason)
        {
            if (!_sessions.TryGetValue(connectionId, out Session? session))
            {
                // Puede ser una conexión que reservó cuenta y se fue antes de que la simulación
                // procesara el alta. Hay que soltar la reserva igualmente.
                ReleaseConnection(connectionId);
                if (rejectReason.HasValue)
                {
                    byte[] bytes = MessageCodec.EncodeReject(new RejectMessage(rejectReason.Value));
                    Enqueue(connectionId, bytes, DeliveryMode.ReliableOrdered, disconnectAfter: true);
                }

                return;
            }

            if (_zone.Entities.TryGet(session.EntityId, out Entity? entity) && entity is PlayerEntity player)
            {
                SaveSession(session, player, _zone);
            }

            _sessions.Remove(connectionId);
            _zone.RemoveEntity(session.EntityId);
            _connectionToEntity.TryRemove(connectionId, out _);
            _connectionToCharacterName.TryRemove(connectionId, out _);
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
            if (session.Character == null || (_saves == null && _memoryRepository == null))
            {
                return;
            }

            // Un jugador muerto se guarda vivo, con la vida llena y en el spawn: al volver a
            // entrar no debe aparecer tendido (I-03).
            int health;
            Vec2 position;

            if (player.IsDead)
            {
                health = player.MaxHealth;
                position = _config.Spawn;
            }
            else
            {
                health = player.Health;
                position = player.Position;
            }

            var inventory = new List<InventoryItemRecord>(player.Inventory.Length);
            for (int i = 0; i < player.Inventory.Length; i++)
            {
                InventorySlot slot = player.Inventory[i];
                if (slot.ItemId != 0 && slot.Count > 0)
                {
                    inventory.Add(new InventoryItemRecord(i, slot.ItemId, slot.Count));
                }
            }

            session.Character = session.Character with
            {
                ZoneId = zone.Id,
                Position = position,
                MapHash = _mapHash,
                Health = health,
                Mana = player.Mana,
                InventoryItems = inventory.ToArray(),
                Equipment = player.Equipment,
                ClassId = player.ClassId,
                AllocStr = player.AllocStr,
                AllocDex = player.AllocDex,
                AllocInt = player.AllocInt,
                AllocVit = player.AllocVit,
                Level = player.Level,
                Experience = player.Experience,
                Gold = player.Gold,
                PvpMode = (int)player.PvpMode,
                PvpForcedSeconds = (player.PvpForcedTicksLeft + SimulationConstants.TickRate - 1) /
                    SimulationConstants.TickRate,
                UpdatedUtc = DateTime.UtcNow,
            };
            if (_saves != null)
            {
                _saves.Enqueue(session.Character);
            }
            else
            {
                _memorySaves.Enqueue(session.Character);
            }
        }

        private void DrainMemorySaves()
        {
            if (_memoryRepository == null)
            {
                return;
            }

            while (_memorySaves.TryDequeue(out CharacterRecord? character))
            {
                _memoryRepository.SaveCharacter(character);
            }
        }

        private void OnInventoryChanged(PlayerEntity player)
        {
            if (_sessions.TryGetValue(player.ConnectionId, out Session? session) &&
                session.EntityId == player.Id)
            {
                Enqueue(player.ConnectionId, EncodeInventory(player), DeliveryMode.ReliableOrdered, disconnectAfter: false);
            }
        }

        private void OnProgressChanged(PlayerEntity player)
        {
            if (_sessions.TryGetValue(player.ConnectionId, out Session? session) &&
                session.EntityId == player.Id)
            {
                Enqueue(
                    player.ConnectionId,
                    EncodeProgress(player),
                    DeliveryMode.ReliableOrdered,
                    disconnectAfter: false);
            }
        }

        private void OnLootGained(PlayerEntity player, LootGainedMessage message)
        {
            if (_sessions.TryGetValue(player.ConnectionId, out Session? session) &&
                session.EntityId == player.Id)
            {
                byte[] bytes = MessageCodec.EncodeLootGained(message);
                Enqueue(player.ConnectionId, bytes, DeliveryMode.ReliableOrdered, disconnectAfter: false);
                Interlocked.Increment(ref _lootGainedMessages);
                Interlocked.Add(ref _lootGainedBytes, bytes.Length);
            }
        }

        private void OnZoneChanged(PlayerEntity player, ZoneChangedMessage message)
        {
            if (_sessions.TryGetValue(player.ConnectionId, out Session? session) &&
                session.EntityId == player.Id)
            {
                byte[] bytes = MessageCodec.EncodeZoneChanged(message);
                Enqueue(player.ConnectionId, bytes, DeliveryMode.ReliableOrdered, disconnectAfter: false);
                Interlocked.Increment(ref _zoneChangedMessages);
                Interlocked.Add(ref _zoneChangedBytes, bytes.Length);
            }
        }

        private void OnPvpStatusChanged(PlayerEntity player, PvpStatusMessage message)
        {
            if (_sessions.TryGetValue(player.ConnectionId, out Session? session) && session.EntityId == player.Id)
            {
                byte[] bytes = MessageCodec.EncodePvpStatus(message);
                Enqueue(player.ConnectionId, bytes, DeliveryMode.ReliableOrdered, disconnectAfter: false);
                Interlocked.Increment(ref _pvpStatusMessages);
                Interlocked.Add(ref _pvpStatusBytes, bytes.Length);
            }
        }

        private void OnPvpModeRejected(PlayerEntity player, PvpModeRejectReason reason)
        {
            if (_sessions.TryGetValue(player.ConnectionId, out Session? session) && session.EntityId == player.Id)
            {
                byte[] bytes = MessageCodec.EncodePvpModeRejected(new PvpModeRejectedMessage(reason));
                Enqueue(player.ConnectionId, bytes, DeliveryMode.ReliableOrdered, disconnectAfter: false);
                Interlocked.Increment(ref _pvpRejectedMessages);
            }
        }

        private void OnSkillUsed(PlayerEntity player, ushort skillId, int targetId)
        {
            if (_sessions.TryGetValue(player.ConnectionId, out Session? session) && session.EntityId == player.Id)
            {
                byte[] bytes = MessageCodec.EncodeSkillUsed(new SkillUsedMessage(skillId, targetId));
                Enqueue(player.ConnectionId, bytes, DeliveryMode.ReliableOrdered, disconnectAfter: false);
                Interlocked.Increment(ref _skillUsedMessages);
            }
        }

        private void OnSkillRejected(PlayerEntity player, ushort skillId, SkillRejectReason reason)
        {
            if (_sessions.TryGetValue(player.ConnectionId, out Session? session) && session.EntityId == player.Id)
            {
                byte[] bytes = MessageCodec.EncodeSkillRejected(new SkillRejectedMessage(skillId, reason));
                Enqueue(player.ConnectionId, bytes, DeliveryMode.ReliableOrdered, disconnectAfter: false);
                Interlocked.Increment(ref _skillRejectedMessages);
            }
        }

        private void OnResourceChanged(PlayerEntity player)
        {
            if (_sessions.TryGetValue(player.ConnectionId, out Session? session) && session.EntityId == player.Id)
            {
                byte[] bytes = MessageCodec.EncodeResourceStatus(new ResourceStatusMessage(
                    checked((ushort)player.Mana),
                    checked((ushort)player.MaxMana)));
                Enqueue(player.ConnectionId, bytes, DeliveryMode.ReliableOrdered, disconnectAfter: false);
                Interlocked.Increment(ref _resourceMessages);
                Interlocked.Add(ref _resourceBytes, bytes.Length);
            }
        }

        private void OnStatusEffectsChanged(PlayerEntity player)
        {
            if (_sessions.TryGetValue(player.ConnectionId, out Session? session) && session.EntityId == player.Id)
            {
                var entries = new List<StatusEffectEntry>(ProtocolConstants.MaxStatusEffects);
                long tick = _zone.TickCount;
                for (int i = 0; i < ProtocolConstants.MaxStatusEffects; i++)
                {
                    StatusKind kind = (StatusKind)(i + 1);
                    if (!player.Statuses.IsActive(kind)) continue;
                    ActiveStatus status = player.Statuses.Get(kind);
                    ushort remainingTicks = (ushort)Math.Clamp(status.ExpiresAtTick - tick, 1, ushort.MaxValue);
                    ushort amount = (ushort)Math.Min(status.Amount, ushort.MaxValue);
                    entries.Add(new StatusEffectEntry(kind, status.SkillId, remainingTicks, amount));
                }
                byte[] bytes = MessageCodec.EncodeStatusEffects(new StatusEffectsMessage(
                    (byte)player.Statuses.SpeedPercent, entries.ToArray()));
                Enqueue(player.ConnectionId, bytes, DeliveryMode.ReliableOrdered, disconnectAfter: false);
                Interlocked.Increment(ref _statusMessages);
                Interlocked.Add(ref _statusBytes, bytes.Length);
            }
        }

        private void OnStatsChanged(PlayerEntity player)
        {
            if (_sessions.TryGetValue(player.ConnectionId, out Session? session) &&
                session.EntityId == player.Id)
            {
                Enqueue(player.ConnectionId, EncodeStats(player), DeliveryMode.ReliableOrdered, disconnectAfter: false);
            }
        }

        private byte[] EncodeProgress(PlayerEntity player)
        {
            int threshold = _zone.Levels.GetExperienceToNext(player.Level);
            return MessageCodec.EncodeProgress(new ProgressMessage(
                checked((byte)player.Level),
                checked((uint)player.Experience),
                checked((uint)threshold),
                checked((uint)player.MaxHealth)));
        }

        private byte[] EncodeStats(PlayerEntity player)
        {
            DerivedCharacterStats derived = CharacterStats.Calculate(
                _zone.Classes,
                player.ClassId,
                player.Level,
                player.AllocStr,
                player.AllocDex,
                player.AllocInt,
                player.AllocVit,
                _zone.Items,
                player.Equipment);
            ItemType? weapon = _zone.Items?.Find(player.EquippedWeaponId);
            int? weaponDamage = weapon?.IsWeapon == true
                ? weapon.Damage
                : (int?)null;
            string? weaponDamageType = weapon?.IsWeapon == true
                ? weapon.DamageType
                : null;
            int attackDamage = CharacterStats.GetAttackDamage(
                _zone.Classes,
                player.ClassId,
                player.Level,
                player.AllocStr,
                player.AllocDex,
                player.AllocInt,
                player.AllocVit,
                weaponDamage,
                out string damageType,
                _zone.Items,
                player.Equipment,
                weaponDamageType);
            int bonus = player.Statuses.Bonus;
            attackDamage += bonus;

            return MessageCodec.EncodeStats(new StatsMessage(
                checked((byte)player.ClassId),
                checked((byte)player.Level),
                checked((ushort)derived.Str),
                checked((ushort)derived.Dex),
                checked((ushort)derived.Int),
                checked((ushort)derived.Vit),
                checked((ushort)player.AllocStr),
                checked((ushort)player.AllocDex),
                checked((ushort)player.AllocInt),
                checked((ushort)player.AllocVit),
                checked((ushort)CharacterStats.GetFreePoints(
                    _zone.Levels.PointsPerLevel,
                    player.Level,
                    player.AllocStr,
                    player.AllocDex,
                    player.AllocInt,
                    player.AllocVit)),
                checked((uint)derived.MaxHealth),
                checked((ushort)attackDamage),
                damageType == "magical" ? (byte)1 : (byte)0,
                checked((ushort)(derived.Defense + bonus)),
                checked((ushort)(derived.MagicResist + bonus)),
                checked((ushort)player.Mana),
                checked((ushort)player.MaxMana),
                player.Level <= _zone.Levels.FreeResetMaxLevel ? (byte)1 : (byte)0));
        }

        /// <summary>
        /// Catálogo de habilidades de la clase del personaje (I-16), en el orden del JSON. Una
        /// clase sin habilidades recibe la lista vacía.
        /// </summary>
        private static byte[] EncodeSkillCatalog(Zone zone, PlayerEntity player)
        {
            SkillCatalog catalog = zone.Skills!;
            CharacterClass? characterClass = zone.Classes.Find(player.ClassId);
            string resourceName = characterClass?.Resource?.Name ?? string.Empty;
            List<SkillCatalogEntry> entries = new List<SkillCatalogEntry>(ProtocolConstants.MaxSkillsPerClass);

            if (catalog.Skills != null)
            {
                for (int i = 0; i < catalog.Skills.Count; i++)
                {
                    SkillDefinition skill = catalog.Skills[i];
                    if (skill.ClassId != player.ClassId)
                    {
                        continue;
                    }

                    entries.Add(new SkillCatalogEntry(
                        (ushort)skill.Id,
                        checked((byte)skill.RequiredLevel),
                        skill.DamageType == "magical" ? (byte)1 : (byte)0,
                        checked((ushort)skill.ResourceCost),
                        checked((ushort)skill.CooldownTicks),
                        skill.Range,
                        checked((ushort)skill.Damage),
                        skill.Name.Trim(), skill.IsHeal ? (byte)1 : skill.Effect == "none" ? (byte)2 : (byte)0,
                        skill.Radius, skill.StatusKind, checked((ushort)skill.StatusTicks),
                        checked((ushort)skill.StatusAmount)));
                }
            }

            return MessageCodec.EncodeSkillCatalog(new SkillCatalogMessage(resourceName, entries.ToArray()));
        }

        private void HandleChatSend(int connectionId, int senderEntityId, byte[] buffer, int offset, int length)
        {
            if (!MessageCodec.TryReadChatSend(buffer, offset, length, out ChatSendMessage request))
            {
                if (IsOverlongChatSend(buffer, offset, length))
                {
                    Interlocked.Increment(ref _chatRejectedLong);
                }
                else
                {
                    Interlocked.Increment(ref _chatRejectedInvalid);
                }

                Interlocked.Increment(ref _messagesRejected);
                return;
            }

            ChatTextValidationResult validation = ChatTextValidator.Validate(
                request.Text,
                _config.ChatMaxBytes,
                out string text);
            if (validation == ChatTextValidationResult.Invalid)
            {
                Interlocked.Increment(ref _chatRejectedInvalid);
                Interlocked.Increment(ref _messagesRejected);
                return;
            }

            if (validation == ChatTextValidationResult.TooLong)
            {
                Interlocked.Increment(ref _chatRejectedLong);
                Interlocked.Increment(ref _messagesRejected);
                return;
            }

            if (!_connectionToCharacterName.TryGetValue(connectionId, out string? senderName))
            {
                Interlocked.Increment(ref _chatRejectedInvalid);
                Interlocked.Increment(ref _messagesRejected);
                return;
            }

            if (!_chatBuckets.TryGetValue(connectionId, out ChatTokenBucket? bucket))
            {
                bucket = new ChatTokenBucket(_config.ChatBurst, _zone.TickCount);
                _chatBuckets.Add(connectionId, bucket);
            }

            if (!bucket.TryConsume(_zone.TickCount, _config.ChatBurst, _config.ChatPerSecond))
            {
                Interlocked.Increment(ref _chatRejectedFast);
                Interlocked.Increment(ref _messagesRejected);
                return;
            }

            byte[] encoded = MessageCodec.EncodeChat(new ChatMessage(senderEntityId, senderName, text));
            Interlocked.Increment(ref _chatReceived);

            // Cada entrada de esta tabla corresponde a un jugador de esta zona. No se filtra por
            // área de interés: el chat de zona llega también a jugadores lejos del emisor.
            foreach (KeyValuePair<int, int> recipient in _connectionToEntity)
            {
                if (_connectionToCharacterName.ContainsKey(recipient.Key))
                {
                    Enqueue(recipient.Key, encoded, DeliveryMode.ReliableOrdered, disconnectAfter: false);
                    Interlocked.Increment(ref _chatDelivered);
                }
            }
        }

        private static bool IsOverlongChatSend(byte[] buffer, int offset, int length)
        {
            return buffer != null && offset >= 0 && length >= 3 &&
                offset <= buffer.Length - 3 &&
                buffer[offset] == (byte)MessageType.ChatSend &&
                (buffer[offset + 1] | (buffer[offset + 2] << 8)) > ProtocolConstants.MaxChatBytes;
        }

        private static string TruncateUtf8(string value, int maxBytes)
        {
            var result = new StringBuilder(Math.Min(value.Length, maxBytes));
            int bytes = 0;
            foreach (Rune rune in value.EnumerateRunes())
            {
                if (bytes + rune.Utf8SequenceLength > maxBytes)
                {
                    break;
                }

                result.Append(rune.ToString());
                bytes += rune.Utf8SequenceLength;
            }

            return result.ToString();
        }

        internal static void ValidateItemCatalogEncoding(Zone zone, ServerConfig config)
        {
            var defaultPlayer = new PlayerEntity(0, 0, config.MaxQueuedIntents, config.InventorySlots);
            EncodeItemCatalog(zone, config, defaultPlayer);
        }

        private static byte[][] EncodeItemCatalog(Zone zone, ServerConfig config, PlayerEntity player)
        {
            // El ataque sin arma depende de la clase (I-08): el cliente lo usa para acercarse y
            // marcar el ritmo del ataque automático, así que tiene que ser el mismo que valida el combate.
            UnarmedAttack unarmed = zone.Classes.Find(player.ClassId)!.Unarmed!;
            CharacterClass characterClass = zone.Classes.Find(player.ClassId)!;
            int unarmedDamage = CharacterStats.GetAttackDamage(
                zone.Classes,
                player.ClassId,
                player.Level,
                player.AllocStr,
                player.AllocDex,
                player.AllocInt,
                player.AllocVit,
                null,
                out _,
                zone.Items,
                player.Equipment);

            ItemCatalog? catalog = zone.Items;
            var entries = new ItemCatalogEntry[catalog?.Items.Count ?? 0];
            for (int i = 0; i < entries.Length; i++)
            {
                ItemType item = catalog!.Items[i];
                ItemKind kind = item.IsPotion
                    ? ItemKind.Potion
                    : item.IsWeapon ? ItemKind.Weapon
                    : item.IsArmor ? ItemKind.Armor : ItemKind.Material;
                item.TryGetEquipSlot(out byte equipSlot);
                entries[i] = new ItemCatalogEntry(
                    (ushort)item.Id,
                    kind,
                    (ushort)item.MaxStack,
                    item.IsPotion ? (uint)item.Heal : 0u,
                    item.IsWeapon ? (uint)item.Damage : 0u,
                    item.IsWeapon ? item.Range : 0f,
                    item.IsWeapon ? (float)item.CooldownSeconds : 0f,
                    item.Name,
                    equipSlot,
                    checked((ushort)item.Defense),
                    checked((ushort)item.MagicResist),
                    checked((byte)item.Str),
                    checked((byte)item.Dex),
                    checked((byte)item.Int),
                    checked((byte)item.Vit),
                    item.IsWeapon ? item.WeaponType : string.Empty,
                    item.IsTwoHanded,
                    checked((byte)item.RequiredLevel),
                    !item.IsWeapon || EquipRules.Check(item, characterClass, int.MaxValue) != EquipCheck.WrongClass);
            }

            return MessageCodec.EncodeItemCatalogChunks(new ItemCatalogMessage(
                config.PickupRange,
                (uint)unarmedDamage,
                unarmed.Range,
                (float)unarmed.CooldownSeconds,
                entries));
        }

        private static byte[] EncodeInventory(PlayerEntity player)
        {
            int count = player.Inventory.Length;
            var itemIds = new ushort[count];
            var amounts = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                itemIds[i] = player.Inventory[i].ItemId;
                amounts[i] = player.Inventory[i].Count;
            }

            return MessageCodec.EncodeInventory(new InventoryMessage(
                (byte)count, itemIds, amounts, player.Equipment, (uint)player.Gold));
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

                // Los objetos del suelo pasan al final, sin cambiar el orden del resto: si sobra
                // algo, que sea botín y no el orco que está pegando al jugador (auditoría).
                _deferredItems.Clear();
                int kept = 0;
                for (int i = 0; i < _visibleBuffer.Count; i++)
                {
                    Entity entity = _visibleBuffer[i];
                    if (entity.Kind == EntityKind.Item)
                    {
                        _deferredItems.Add(entity);
                    }
                    else
                    {
                        _visibleBuffer[kept++] = entity;
                    }
                }

                for (int i = 0; i < _deferredItems.Count; i++)
                {
                    _visibleBuffer[kept++] = _deferredItems[i];
                }
            }

            EntityState[] states = available == 0
                ? Array.Empty<EntityState>()
                : new EntityState[available];

            for (int i = 0; i < available; i++)
            {
                Entity entity = _visibleBuffer[i];

                // Un PNJ herido manda su vida en porcentaje; un jugador, la suya (I-03). La misma
                // regla de redondeo para ambos: reutiliza EntityHealth.Percent.
                byte healthPercent = entity is NpcEntity npc
                    ? npc.HealthPercent
                    : entity is PlayerEntity player
                        ? player.HealthPercent
                        : (byte)100;

                states[i] = new EntityState(
                    entity.Id,
                    ProtocolConstants.QuantizePosition(entity.Position.X),
                    ProtocolConstants.QuantizePosition(entity.Position.Y),
                    entity.DirectionX,
                    entity.DirectionY,
                    entity.Kind,
                    healthPercent,
                    entity.Action,
                    entity is NpcEntity typed ? (ushort)typed.TypeId :
                    entity is PlayerEntity classed ? checked((ushort)classed.ClassId) :
                    entity is GroundItemEntity ground ? (ushort)ground.ItemId : (ushort)0,
                    entity is NpcEntity ranked ? ranked.Rank : (byte)0,
                    entity is PlayerEntity pvpPlayer && pvpPlayer.PvpEffective);
            }

            byte[] bytes = MessageCodec.EncodeSnapshot(new SnapshotMessage(
                unchecked((uint)zone.TickCount),
                recipient.LastProcessedSequence,
                states));

            Interlocked.Add(ref _entitiesSent, available);
            Interlocked.Add(ref _snapshotBytes, bytes.Length);
            return bytes;
        }

        private void SendCombatEvents(Zone zone, PlayerEntity recipient, int connectionId)
        {
            CombatEventBuffer events = zone.CombatEvents;
            if (events.Count == 0)
            {
                return;
            }

            _combatEventEntries.Clear();
            for (int i = 0; i < events.Count; i++)
            {
                CombatEventEntry entry = events[i];
                if (entry.AttackerId == recipient.Id || entry.TargetId == recipient.Id ||
                    IsVisible(entry.AttackerId) || IsVisible(entry.TargetId))
                {
                    _combatEventEntries.Add(entry);
                }
            }

            int count = _combatEventEntries.Count;
            if (count == 0)
            {
                return;
            }

            if (count > ProtocolConstants.MaxCombatEventsPerMessage)
            {
                Interlocked.Add(ref _combatEventsTrimmed, count - ProtocolConstants.MaxCombatEventsPerMessage);
                count = ProtocolConstants.MaxCombatEventsPerMessage;
            }

            var entries = new CombatEventEntry[count];
            for (int i = 0; i < count; i++)
            {
                entries[i] = _combatEventEntries[i];
            }

            byte[] bytes = MessageCodec.EncodeCombatEvent(new CombatEventMessage(entries));
            Enqueue(connectionId, bytes, DeliveryMode.Unreliable, disconnectAfter: false);
            Interlocked.Increment(ref _combatEventMessages);
            Interlocked.Add(ref _combatEventEntriesSent, count);
            Interlocked.Add(ref _combatEventBytes, bytes.Length);
        }

        private void SendStatusEvents(Zone zone, PlayerEntity recipient, int connectionId)
        {
            List<StatusEventEntry> events = zone.StatusEvents;
            if (events.Count == 0) return;
            _statusEventEntries.Clear();
            for (int i = 0; i < events.Count; i++)
            {
                StatusEventEntry entry = events[i];
                if (entry.EntityId == recipient.Id || IsVisible(entry.EntityId))
                    _statusEventEntries.Add(entry);
            }
            int count = _statusEventEntries.Count;
            if (count == 0) return;
            if (count > ProtocolConstants.MaxStatusEventsPerMessage)
                count = ProtocolConstants.MaxStatusEventsPerMessage;
            var entries = new StatusEventEntry[count];
            for (int i = 0; i < count; i++) entries[i] = _statusEventEntries[i];
            byte[] bytes = MessageCodec.EncodeStatusEvent(new StatusEventMessage(entries));
            Enqueue(connectionId, bytes, DeliveryMode.ReliableOrdered, disconnectAfter: false);
            Interlocked.Increment(ref _statusEventMessages);
            Interlocked.Add(ref _statusEventBytes, bytes.Length);
        }

        private bool IsVisible(int entityId)
        {
            for (int i = 0; i < _visibleBuffer.Count; i++)
            {
                if (_visibleBuffer[i].Id == entityId)
                {
                    return true;
                }
            }

            return false;
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
            Eject = 2,
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

            /// <summary>Personaje cargado de la base de datos; nulo en eventos que no sean Join.</summary>
            public CharacterRecord? Character { get; }

            public static ControlEvent Join(
                int connectionId,
                HelloMessage hello,
                int accountId,
                string accountName,
                CharacterRecord character) =>
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

            public static ControlEvent Eject(int connectionId) =>
                new ControlEvent(
                    ControlEventKind.Eject,
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

        /// <summary>Estado de una cuenta autenticada que aún no seleccionó un personaje.</summary>
        private sealed class CharacterSelectState
        {
            public CharacterSelectState(
                int connectionId,
                long connectionGeneration,
                HelloMessage hello,
                int accountId,
                string accountName,
                long deadline)
            {
                ConnectionId = connectionId;
                ConnectionGeneration = connectionGeneration;
                Hello = hello;
                AccountId = accountId;
                AccountName = accountName ?? string.Empty;
                Deadline = deadline;
            }

            public int ConnectionId { get; }
            public long ConnectionGeneration { get; }
            public HelloMessage Hello { get; }
            public int AccountId { get; }
            public string AccountName { get; }
            public long Deadline { get; }
            public bool OperationPending { get; set; }
        }

        /// <summary>Cubo de fichas que repone usando los ticks publicados por la zona.</summary>
        private sealed class ChatTokenBucket
        {
            private double _tokens;
            private long _lastTick;

            public ChatTokenBucket(int capacity, long tick)
            {
                _tokens = capacity;
                _lastTick = tick;
            }

            public bool TryConsume(long tick, int capacity, double refillPerSecond)
            {
                long elapsedTicks = tick - _lastTick;
                if (elapsedTicks > 0)
                {
                    _tokens = Math.Min(
                        capacity,
                        _tokens + (elapsedTicks * refillPerSecond / SimulationConstants.TickRate));
                    _lastTick = tick;
                }

                if (_tokens < 1d)
                {
                    return false;
                }

                _tokens -= 1d;
                return true;
            }
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
            long chatReceived,
            long chatDelivered,
            long chatRejectedInvalid,
            long chatRejectedLong,
            long chatRejectedFast,
            string? saveLastError = null,
            long combatEventMessages = 0,
            long combatEventEntries = 0,
            long combatEventBytes = 0,
            long combatEventsTrimmed = 0,
            long lootGainedMessages = 0,
            long lootGainedBytes = 0,
            long zoneChangedMessages = 0,
            long zoneChangedBytes = 0,
            long pvpStatusMessages = 0,
            long pvpStatusBytes = 0,
            long pvpRejectedMessages = 0,
            long skillCatalogBytes = 0,
            long skillUsedMessages = 0,
             long skillRejectedMessages = 0,
             long resourceMessages = 0,
             long resourceBytes = 0,
             long statusMessages = 0,
             long statusBytes = 0,
             long statusEventMessages = 0,
             long statusEventBytes = 0)
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
            ChatReceived = chatReceived;
            ChatDelivered = chatDelivered;
            ChatRejectedInvalid = chatRejectedInvalid;
            ChatRejectedLong = chatRejectedLong;
            ChatRejectedFast = chatRejectedFast;
            CombatEventMessages = combatEventMessages;
            CombatEventEntries = combatEventEntries;
            CombatEventBytes = combatEventBytes;
            CombatEventsTrimmed = combatEventsTrimmed;
            LootGainedMessages = lootGainedMessages;
            LootGainedBytes = lootGainedBytes;
            ZoneChangedMessages = zoneChangedMessages;
            ZoneChangedBytes = zoneChangedBytes;
            PvpStatusMessages = pvpStatusMessages;
            PvpStatusBytes = pvpStatusBytes;
            PvpRejectedMessages = pvpRejectedMessages;
            SkillCatalogBytes = skillCatalogBytes;
            SkillUsedMessages = skillUsedMessages;
            SkillRejectedMessages = skillRejectedMessages;
            ResourceMessages = resourceMessages;
            ResourceBytes = resourceBytes;
            StatusMessages = statusMessages;
            StatusBytes = statusBytes;
            StatusEventMessages = statusEventMessages;
            StatusEventBytes = statusEventBytes;
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

        public long ChatReceived { get; }
        public long ChatDelivered { get; }
        public long ChatRejectedInvalid { get; }
        public long ChatRejectedLong { get; }
        public long ChatRejectedFast { get; }

        public long CombatEventMessages { get; }
        public long CombatEventEntries { get; }
        public long CombatEventBytes { get; }
        public long CombatEventsTrimmed { get; }

        public long LootGainedMessages { get; }
        public long LootGainedBytes { get; }

        public long ZoneChangedMessages { get; }
        public long ZoneChangedBytes { get; }

        public long PvpStatusMessages { get; }
        public long PvpStatusBytes { get; }
        public long PvpRejectedMessages { get; }

        /// <summary>Bytes del catálogo de habilidades enviados al entrar (I-16).</summary>
        public long SkillCatalogBytes { get; }

        /// <summary>Confirmaciones de habilidad enviadas (I-16).</summary>
        public long SkillUsedMessages { get; }

        /// <summary>Rechazos de habilidad enviados (I-16).</summary>
        public long SkillRejectedMessages { get; }

        /// <summary>Mensajes de estado del recurso enviados (I-16).</summary>
        public long ResourceMessages { get; }

        /// <summary>Bytes de los mensajes de estado del recurso (I-16).</summary>
        public long ResourceBytes { get; }

        /// <summary>Mensajes propios de estados temporales enviados (I-18).</summary>
        public long StatusMessages { get; }

        /// <summary>Bytes de mensajes propios de estados temporales (I-18).</summary>
        public long StatusBytes { get; }

        /// <summary>Mensajes de cambios de estado para observadores (I-18).</summary>
        public long StatusEventMessages { get; }

        /// <summary>Bytes de cambios de estado para observadores (I-18).</summary>
        public long StatusEventBytes { get; }

        /// <summary>Último error de guardado, si lo hubo.</summary>
        public string? SaveLastError { get; }
    }
}
