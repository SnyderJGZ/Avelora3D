#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Avelora.Net;
using Avelora.Shared;
using Avelora.Shared.Prediction;
using Avelora.Shared.Protocol;

namespace Avelora.Bots
{
    /// <summary>Cómo se mueve (o cómo abusa) un bot durante la prueba de carga.</summary>
    public enum BotBehavior
    {
        /// <summary>Cambia de dirección al azar, como un jugador que pasea.</summary>
        Random = 0,

        /// <summary>Manda siempre <c>(0, 0)</c>: todos se quedan donde aparecen (peor caso del área de interés).</summary>
        Crowd = 1,

        /// <summary>Busca al enemigo vivo más cercano, va hacia él y lo ataca (I-02).</summary>
        Fight = 7,

        /// <summary>Inunda con <c>MoveInput</c> válidos a 200/s (10 por intervalo de 50 ms).</summary>
        Flood = 2,

        /// <summary>Mezcla inputs normales con datagramas basura cinco veces por segundo.</summary>
        Garbage = 3,

        /// <summary>Manda direcciones fuera de rango además de sus inputs normales.</summary>
        Speed = 4,

        /// <summary>Repite secuencias viejas además de sus inputs normales.</summary>
        Replay = 5,

        /// <summary>Saluda con la contraseña incorrecta, una y otra vez, sin rampa de reintento.</summary>
        BadLogin = 6,

        /// <summary>Se mueve al azar y manda mensajes cortos al chat de la zona (I-07).</summary>
        Chat = 8,

        /// <summary>Recoge objetos y genera acciones aleatorias sobre el inventario (I-14).</summary>
        Inventory = 9,
    }

    /// <summary>
    /// Cliente sin ventana que juega por la red real: conecta, saluda, manda intenciones a 20 Hz y
    /// lee snapshots. Sirve para ver la Etapa C funcionando y es la base de la Etapa G.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No conoce el servidor: usa exactamente el mismo protocolo y el mismo transporte que usará el
    /// cliente de Unity. Si el bot se mueve, la costura cliente-servidor está bien.
    /// </para>
    /// <para>
    /// Para la prueba de carga: el bot puede arrancar con retraso, reintentar cuando el servidor le
    /// contesta <c>RateLimited</c> (con un transporte nuevo), quedarse quieto
    /// (<see cref="BotBehavior.Crowd"/>) y medir su tiempo hasta el <c>Welcome</c> y el hueco máximo
    /// entre snapshots. Desde G-02 también puede abusar (<see cref="BotBehavior.Flood"/>,
    /// <see cref="BotBehavior.Garbage"/>, <see cref="BotBehavior.Speed"/>,
    /// <see cref="BotBehavior.Replay"/> y <see cref="BotBehavior.BadLogin"/>) y contar lo que envía
    /// de cada tipo para cruzarlo con los contadores del servidor.
    /// </para>
    /// </remarks>
    public sealed class BotClient : IDisposable
    {
        /// <summary>Un input cada 50 ms, igual que el tick del servidor.</summary>
        private const int InputIntervalMs = 50;

        /// <summary>
        /// Archivos compartidos por todos los bots del proceso (traza e informe de <c>Welcome</c>,
        /// G-05): un escritor por ruta, abierto una vez, para no abrir el archivo en cada línea.
        /// </summary>
        private static readonly Dictionary<string, StreamWriter> SharedWriters =
            new Dictionary<string, StreamWriter>(StringComparer.OrdinalIgnoreCase);

        /// <summary>En <c>flood</c>, inputs válidos por intervalo (10 × 20 = 200/s).</summary>
        private const int FloodInputsPerInterval = 10;

        /// <summary>En <c>garbage</c>, un datagrama basura cada 200 ms (5/s).</summary>
        private const int GarbageIntervalMs = 200;

        /// <summary>En <c>speed</c>, una dirección fuera de rango por intervalo de 50 ms.</summary>
        private const int InvalidDirectionIntervalMs = 50;

        /// <summary>En <c>replay</c>, una ráfaga de secuencias viejas cada 200 ms.</summary>
        private const int ReplayIntervalMs = 200;

        /// <summary>Intentos de login fallido de un <c>badlogin</c> antes de darse por vencido.</summary>
        private const int MaxBadLoginAttempts = 10;

        /// <summary>
        /// Alcance con el que el bot decide que ya está cerca. Es el valor por defecto del servidor
        /// (I-02); el servidor suma además su tolerancia de interpolación.
        /// </summary>
        private const float AttackRange = 1.2f;
        private const ushort DefaultPotionItemId = 3002;

        /// <summary>Margen que se suma a la recarga al mandar ataques, para no llegar justos al tick.</summary>
        private const int AttackSlackMs = 50;

        /// <summary>Para avisar una sola vez por proceso de que no se configuró la huella esperada.</summary>
        private static int _missingFingerprintWarned;

        /// <summary>Pausa entre intentos fallidos: no es la rampa de G-01, solo evita llenar la cola de logins.</summary>
        private const int BadLoginPauseMs = 250;

        /// <summary>Cuánto se espera entre reintentos, multiplicado por el número de intento.</summary>
        private const int RetryDelayMs = 1000;

        /// <summary>Direcciones fuera de rango que usa el modo <c>speed</c>.</summary>
        private static readonly sbyte[,] InvalidDirections =
        {
            { 5, 0 },
            { 0, -128 },
            { 127, 1 },
            { -2, 0 },
            { 0, 3 },
        };

        private readonly Func<IClientTransport> _transportFactory;
        private readonly MapCollisionGrid? _map;
        private readonly string _host;
        private readonly int _port;
        private readonly byte[] _mapHash;
        private readonly int _inputRedundancy;
        private readonly uint _clientToken;
        private readonly Random _random;
        private readonly int _runMs;
        private readonly int _startDelayMs;
        private readonly int _maxRetries;
        private readonly int _welcomeTimeoutMs;
        private readonly BotBehavior _behavior;
        private readonly string _accountName;
        private readonly string _password;
        private readonly string _invitationCode;
        private readonly string _serverKeyFingerprint;
        private readonly bool _logCorrections;
        private readonly string? _tracePositionsPath;
        private readonly string? _reportWelcomePath;
        private readonly int _attackCooldownMs;
        private readonly bool _pvpEnabled;
        private float _pickupRange;
        private HelloIntent _intent = HelloIntent.Register;

        private IClientTransport? _transport;

        // Estado del intento en curso; se reinicia en cada intento.
        private bool _connected;
        private bool _helloSent;
        private bool _welcomed;
        private bool _challengeReceived;
        private bool _keyMismatch;
        private byte[]? _challengeNonce;
        private RSAParameters _serverPublicKey;
        private byte[]? _helloBytes;
        private bool _disconnected;
        private RejectReason? _reject;
        private long _attemptStartedAt;
        private long _welcomedAt;
        private long _lastSnapshotAt;

        private uint _sequence;
        private sbyte _directionX;
        private sbyte _directionY;
        private long _nextInputAt;

        // Últimos inputs enviados, del más nuevo al más viejo, para la redundancia de G-03.
        private readonly uint[] _recentSequences = new uint[ProtocolConstants.MaxInputsPerMessage];
        private readonly sbyte[] _recentX = new sbyte[ProtocolConstants.MaxInputsPerMessage];
        private readonly sbyte[] _recentY = new sbyte[ProtocolConstants.MaxInputsPerMessage];
        private readonly bool[] _recentValid = new bool[ProtocolConstants.MaxInputsPerMessage];
        private int _recentCount;

        // Predicción del jugador local (solo modo random), como la del cliente de Unity.
        private ClientPrediction? _prediction;
        private Vec2 _halfSize;
        private long _corrections;
        private long _snaps;

        private long _nextDirectionChangeAt;
        private long _nextGarbageAt;
        private long _nextInvalidDirectionAt;
        private long _nextReplayAt;
        private long _nextChatAt;
        private long _nextInventoryActionAt;
        private int _garbageKind;
        private int _invalidDirectionKind;

        private long _snapshots;
        private float _x;
        private float _y;
        private float _speed;
        private float _welcomeSpeed;
        private uint _lastSnapshotTick;
        private bool _hasPosition;
        private float _travelled;

        // Lo que mandó el bot, por tipo. Es la otra mitad de la tabla del informe.
        private long _sentValidInputs;
        private long _sentInvalidInputs;
        private long _sentReplayInputs;
        private long _sentGarbage;
        private long _sentHellos;
        private readonly SortedDictionary<RejectReason, int> _rejectCounts =
            new SortedDictionary<RejectReason, int>();

        // Vista de los enemigos del último snapshot (modo fight) y ataques enviados (I-02).
        private readonly Dictionary<int, NpcView> _npcs = new Dictionary<int, NpcView>(64);
        private readonly HashSet<int> _deadNpcs = new HashSet<int>();
        private readonly HashSet<int> _rankedNpcIds = new HashSet<int>();
        private long _nextAttackAt;
        private long _sentAttacks;
        private long _sentRangedAttacks;
        private long _attacksSkippedLineOfSight;
        private long _combatEventsReceived;
        private long _lootMessages;
        private long _lootExperience;
        private long _lootGold;
        private long _lootItems;
        private long _lootOnGround;
        private long _zoneLists;
        private long _zoneChanges;
        private long _safeZoneEntries;
        private long _pvpZoneEntries;
        private long _pvpStatuses;
        private long _pvpRejects;
        private long _pvpModeRequests;
        private readonly bool _useSkills;
        private SkillCatalogEntry[] _skillEntries = Array.Empty<SkillCatalogEntry>();
        private long[] _skillReadyAt = Array.Empty<long>();
        private long _globalSkillReadyAt;
        private int _globalCooldownMs;
        private long _healsSent;
        private long _statusSkillsSent;
        private long _statusMessages;
        private long _slowMessages;
        private ushort _resource;
        private long _skillsSent;
        private long _skillsOk;
        private long _skillsRejected;
        private long _resourceMessages;
        private bool _pvpModeRequested;
        private PvpStatusMessage _lastPvpStatus;
        private long _sentChat;
        private long _receivedChat;
        private long _deathsSeen;
        private long _elitesSeen;

        // Vida propia (I-03): el snapshot trae la acción del jugador local. Mientras está muerto
        // manda movimiento cero y no ataca; al volver, reinicia la predicción en la posición nueva.
        private EntityAction _ownAction = EntityAction.None;
        private bool _ownDead;
        private long _ownDeaths;
        private long _ownRespawns;
        private byte _ownHealthPercent = 100;
        private uint _lastAttackTick;
        private ushort[] _inventoryItemIds = Array.Empty<ushort>();
        private ushort[] _inventoryCounts = Array.Empty<ushort>();
        private ushort[] _equipmentItemIds = new ushort[ProtocolConstants.EquipSlotCount];
        private readonly Dictionary<ushort, ItemCatalogEntry> _itemCatalog = new Dictionary<ushort, ItemCatalogEntry>();
        private ushort _equippedItemId;
        private ushort _pendingEquipItemId;
        private byte _pendingEquipSlot;
        private long _armorsEquipped;
        private long _equipOmittedRequirements;
        private float _fistRange = AttackRange;
        private float _fistCooldownSeconds = 0.8f;
        private SnapshotMessage _pendingFightSnapshot;
        private bool _itemDecisionPending;
        private long _sentPickups;
        private long _sentPotions;
        private long _equipped;
        private byte _level = 1;
        private uint _gold;
        private bool _hasProgress;
        private long _levelUps;
        private byte _maxLevelReached = 1;
        private long _pointsAllocated;
        private long _statsReceived;
        private byte _classId;
        private ushort _lastFreePoints;
        private ushort _pendingAllocationPoints;
        private int _primaryAttribute = -1;
        private bool _hasStats;
        private long _charactersCreated;
        private long _characterRejects;
        private bool _creatingCharacter;
        private bool _selectionSent;
        private string _characterNameAttempt = string.Empty;
        private byte _lastCharacterClassId;

        // Mediciones de toda la vida del bot.
        private long _firstAttemptAt = -1;
        private int _retries;
        private long _welcomeMs = -1;
        private long _playedMs;
        private long _maxSnapshotGapMs;

        public BotClient(
            int index,
            string host,
            int port,
            byte[] mapHash,
            int seed,
            int runMs,
            string accountPrefix,
            string password,
            string invitationCode,
            int startDelayMs = 0,
            int retries = 5,
            int welcomeTimeoutMs = 15000,
            BotBehavior behavior = BotBehavior.Random,
            int attackCooldownMs = 800,
            Func<IClientTransport>? transportFactory = null,
            MapCollisionGrid? map = null,
            int inputRedundancy = ProtocolConstants.MaxInputsPerMessage,
            bool logCorrections = false,
            string? tracePositionsPath = null,
            string? reportWelcomePath = null,
            string? serverKeyFingerprint = null,
            float pickupRange = 1.5f,
            bool pvp = false,
            bool useSkills = false)
        {
            if (startDelayMs < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(startDelayMs));
            }

            if (retries < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(retries));
            }

            if (welcomeTimeoutMs <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(welcomeTimeoutMs));
            }

            if (inputRedundancy < 1 || inputRedundancy > ProtocolConstants.MaxInputsPerMessage)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(inputRedundancy),
                    "La redundancia debe estar entre 1 y " + ProtocolConstants.MaxInputsPerMessage + ".");
            }

            if (attackCooldownMs < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(attackCooldownMs),
                    attackCooldownMs,
                    "La recarga de ataque no puede ser negativa.");
            }

            Index = index;
            _map = map;
            _inputRedundancy = inputRedundancy;
            _useSkills = useSkills;
            _host = host;
            _port = port;
            _mapHash = mapHash;
            _runMs = runMs;
            _startDelayMs = startDelayMs;
            _maxRetries = retries;
            _welcomeTimeoutMs = welcomeTimeoutMs;
            _behavior = behavior;
            _attackCooldownMs = attackCooldownMs;
            _pvpEnabled = pvp;
            _pickupRange = pickupRange;
            _logCorrections = logCorrections;
            _tracePositionsPath = tracePositionsPath;
            _reportWelcomePath = reportWelcomePath;
            _accountName = accountPrefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _password = password;
            _invitationCode = invitationCode;
            _serverKeyFingerprint = (serverKeyFingerprint ?? string.Empty).Trim();
            _random = new Random(seed + (index * 7919));
            _clientToken = (uint)(seed + (index * 104729));

            // Inyectable para las pruebas: el bot real usa LiteNetLib, como el cliente de Unity.
            _transportFactory = transportFactory ?? (() => new LiteNetLibClientTransport());
        }

        public int Index { get; }

        public string AccountName => _accountName;

        public int EntityId { get; private set; } = -1;

        public string Status { get; private set; } = "iniciando";

        /// <summary>Motivo del rechazo final, o <c>null</c> si el bot llegó a entrar o no fue rechazado.</summary>
        public string? RejectReasonText { get; private set; }

        public BotBehavior Behavior => _behavior;

        /// <summary>
        /// En modo <c>replay</c>, si el servidor rechazó un saludo reenviado en otra conexión.
        /// <c>null</c> si no se llegó a comprobar.
        /// </summary>
        public bool? ReplayedHelloRejected { get; private set; }

        public long Snapshots => Interlocked.Read(ref _snapshots);

        /// <summary>Redundancia configurada: cuántos inputs viajan en cada mensaje normal.</summary>
        public int InputRedundancy => _inputRedundancy;

        /// <summary>Correcciones suaves que la predicción habría aplicado al jugador local.</summary>
        public long Corrections => _corrections;

        /// <summary>Saltos grandes que la predicción habría aplicado al jugador local.</summary>
        public long Snaps => _snaps;

        /// <summary>Milisegundos desde el primer intento hasta el <c>Welcome</c>; <c>-1</c> si no llegó.</summary>
        public long WelcomeMs => _welcomeMs;

        /// <summary>Reintentos hechos por <c>RateLimited</c>.</summary>
        public int Retries => _retries;

        public long CharactersCreated => Interlocked.Read(ref _charactersCreated);

        public long CharacterRejects => Interlocked.Read(ref _characterRejects);

        /// <summary>Hueco máximo, en ms, entre dos snapshots consecutivos después del <c>Welcome</c>.</summary>
        public long MaxSnapshotGapMs => _maxSnapshotGapMs;

        /// <summary>Snapshots por segundo que recibió mientras jugaba.</summary>
        public double SnapshotsPerSecond =>
            _playedMs <= 0 ? 0d : Snapshots * 1000d / _playedMs;

        public float X => _x;

        public float Y => _y;

        public uint Sequence => _sequence;

        /// <summary>Velocidad que impuso el servidor en el <c>Welcome</c>.</summary>
        public float Speed => _speed;

        /// <summary>Milisegundos que estuvo jugando (desde el <c>Welcome</c> hasta el final).</summary>
        public long PlayedMs => _playedMs;

        /// <summary>Distancia recorrida según sus propios snapshots, en tiles.</summary>
        public float TravelledDistance => _travelled;

        /// <summary>Inputs válidos enviados (cada uno es un paso en el servidor).</summary>
        public long SentValidInputs => Interlocked.Read(ref _sentValidInputs);

        /// <summary>Inputs con dirección fuera de rango enviados (modo <c>speed</c>).</summary>
        public long SentInvalidInputs => Interlocked.Read(ref _sentInvalidInputs);

        /// <summary>Secuencias viejas o repetidas enviadas (modo <c>replay</c>).</summary>
        public long SentReplayInputs => Interlocked.Read(ref _sentReplayInputs);

        /// <summary>Datagramas basura enviados (modo <c>garbage</c>).</summary>
        public long SentGarbage => Interlocked.Read(ref _sentGarbage);

        /// <summary>Saludos enviados (modo <c>badlogin</c> y reintentos).</summary>
        public long SentHellos => Interlocked.Read(ref _sentHellos);

        /// <summary>Ataques enviados (modo <c>fight</c>, I-02).</summary>
        public long SentAttacks => Interlocked.Read(ref _sentAttacks);

        public long SentRangedAttacks => Interlocked.Read(ref _sentRangedAttacks);

        public long AttacksSkippedLineOfSight => Interlocked.Read(ref _attacksSkippedLineOfSight);

        public long CombatEventsReceived => Interlocked.Read(ref _combatEventsReceived);

        public long LootMessages => Interlocked.Read(ref _lootMessages);
        public long LootExperience => Interlocked.Read(ref _lootExperience);
        public long LootGold => Interlocked.Read(ref _lootGold);
        public long LootItems => Interlocked.Read(ref _lootItems);
        public long LootOnGround => Interlocked.Read(ref _lootOnGround);
        public long ZoneLists => Interlocked.Read(ref _zoneLists);
        public long ZoneChanges => Interlocked.Read(ref _zoneChanges);
        public long SafeZoneEntries => Interlocked.Read(ref _safeZoneEntries);
        public long PvpZoneEntries => Interlocked.Read(ref _pvpZoneEntries);
        public long PvpStatuses => Interlocked.Read(ref _pvpStatuses);
        public long PvpRejects => Interlocked.Read(ref _pvpRejects);
        public long PvpModeRequests => Interlocked.Read(ref _pvpModeRequests);
        public long HealsSent => Interlocked.Read(ref _healsSent);
        public long StatusSkillsSent => Interlocked.Read(ref _statusSkillsSent);
        public long StatusMessages => Interlocked.Read(ref _statusMessages);
        public long SlowMessages => Interlocked.Read(ref _slowMessages);
        public long SkillsSent => Interlocked.Read(ref _skillsSent);
        public long SkillsOk => Interlocked.Read(ref _skillsOk);
        public long SkillsRejected => Interlocked.Read(ref _skillsRejected);
        public long ResourceMessages => Interlocked.Read(ref _resourceMessages);
        public PvpStatusMessage LastPvpStatus => _lastPvpStatus;
        public uint Gold => _gold;

        /// <summary>Mensajes de chat enviados por el comportamiento <c>chat</c> (I-07).</summary>
        public long SentChat => Interlocked.Read(ref _sentChat);

        /// <summary>Mensajes de chat recibidos por el comportamiento <c>chat</c> (I-07).</summary>
        public long ReceivedChat => Interlocked.Read(ref _receivedChat);

        /// <summary>Muertes de enemigos vistas en los snapshots (modo <c>fight</c>, I-02).</summary>
        public long DeathsSeen => Interlocked.Read(ref _deathsSeen);

        /// <summary>Entidades distintas de rango distinto de 0 vistas en snapshots.</summary>
        public long ElitesSeen => Interlocked.Read(ref _elitesSeen);

        /// <summary>Muertes propias vistas en los snapshots (I-03).</summary>
        public long OwnDeaths => Interlocked.Read(ref _ownDeaths);

        /// <summary>Reapariciones propias vistas en los snapshots (I-03).</summary>
        public long OwnRespawns => Interlocked.Read(ref _ownRespawns);

        /// <summary>Intenciones de recoger enviadas por el bot fight.</summary>
        public long SentPickups => Interlocked.Read(ref _sentPickups);

        /// <summary>Intenciones de usar pociones enviadas por el bot fight.</summary>
        public long SentPotions => Interlocked.Read(ref _sentPotions);

        /// <summary>Armas que el servidor confirmó como equipadas durante la sesión.</summary>
        public long Equipped => Interlocked.Read(ref _equipped);

        /// <summary>Armaduras confirmadas por el servidor como equipadas durante la sesión.</summary>
        public long ArmorsEquipped => Interlocked.Read(ref _armorsEquipped);

        /// <summary>Candidatos de equipo omitidos por clase, nivel o compatibilidad de manos.</summary>
        public long EquipOmittedRequirements => Interlocked.Read(ref _equipOmittedRequirements);

        /// <summary>Nivel que el servidor confirmó en el último mensaje Progress.</summary>
        public byte Level => _level;

        /// <summary>Subidas de nivel confirmadas durante la sesión.</summary>
        public long LevelUps => Interlocked.Read(ref _levelUps);

        /// <summary>Nivel máximo confirmado por el servidor.</summary>
        public byte MaxLevelReached => _maxLevelReached;

        /// <summary>Puntos cuya asignación confirmó el servidor en Stats.</summary>
        public long PointsAllocated => Interlocked.Read(ref _pointsAllocated);

        /// <summary>Mensajes Stats procesados por el bot.</summary>
        public long StatsReceived => Interlocked.Read(ref _statsReceived);

        public byte ClassId => _classId;

        /// <summary>¿Su comportamiento es de abuso (no es un bot normal de carga)?</summary>
        public static bool IsAbusive(BotBehavior behavior) =>
            behavior == BotBehavior.Flood || behavior == BotBehavior.Garbage ||
            behavior == BotBehavior.Speed || behavior == BotBehavior.Replay ||
            behavior == BotBehavior.BadLogin;

        /// <summary>Suma los <c>Reject</c> recibidos a un acumulador, para el resumen de la ejecución.</summary>
        public void AccumulateRejectCounts(SortedDictionary<string, int> totals)
        {
            foreach (KeyValuePair<RejectReason, int> pair in _rejectCounts)
            {
                string reason = pair.Key.ToString();
                totals.TryGetValue(reason, out int previous);
                totals[reason] = previous + pair.Value;
            }
        }

        /// <summary>Resumen de los <c>Reject</c> recibidos, por motivo: <c>InvalidCredentials=5, RateLimited=1</c>.</summary>
        public string DescribeRejectResponses()
        {
            if (_rejectCounts.Count == 0)
            {
                return "ninguna";
            }

            var text = new StringBuilder();

            foreach (KeyValuePair<RejectReason, int> pair in _rejectCounts)
            {
                if (text.Length > 0)
                {
                    text.Append(", ");
                }

                text.Append(pair.Key).Append('=').Append(pair.Value);
            }

            return text.ToString();
        }

        /// <summary>Texto del rechazo para la consola; el resumen sigue usando el nombre del enum.</summary>
        private static string DescribeReject(RejectReason reason) =>
            reason switch
            {
                RejectReason.InvalidCredentials => "cuenta, contraseña o invitación incorrectas",
                RejectReason.AccountTaken => "la cuenta ya existe",
                RejectReason.CharacterLimit => "la cuenta ya tiene el máximo de personajes",
                RejectReason.RateLimited => "demasiadas peticiones",
                _ => reason.ToString(),
            };

        /// <summary>Conecta y juega durante el tiempo pedido. Bloquea el hilo que lo llama.</summary>
        public void Run(CancellationToken cancellationToken)
        {
            if (!Sleep(_startDelayMs, cancellationToken))
            {
                Status = "cancelado antes de empezar";
                return;
            }

            if (_behavior == BotBehavior.BadLogin)
            {
                RunBadLogin(cancellationToken);
                _transport?.Disconnect();
                return;
            }

            while (true)
            {
                AttemptResult result = PlayOnce(cancellationToken);

                if (result == AttemptResult.Finished)
                {
                    Status = "terminado";
                    break;
                }

                if (result == AttemptResult.Cancelled)
                {
                    Status = "cancelado";
                    break;
                }

                if (result == AttemptResult.RetryLogin)
                {
                    DiscardTransport();
                    continue;
                }

                if (result == AttemptResult.RateLimited)
                {
                    // El servidor no puede atender el login ahora: transporte nuevo y espera
                    // creciente. No se reutiliza la conexión porque la que rechaza se cierra.
                    DiscardTransport();

                    if (_retries >= _maxRetries)
                    {
                        Status = "rechazado (RateLimited)";
                        RejectReasonText = RejectReason.RateLimited.ToString();
                        break;
                    }

                    _retries++;
                    Status = "reintentando (" + _retries + "/" + _maxRetries + ")";

                    if (!Sleep(RetryDelayMs * _retries, cancellationToken))
                    {
                        Status = "cancelado";
                        break;
                    }

                    continue;
                }

                // Cualquier otro rechazo o fallo es final: reintentar no arreglaría un mapa
                // distinto, una contraseña mal o un servidor que no contesta.
                break;
            }

            _transport?.Disconnect();

            if (_behavior == BotBehavior.Replay && _helloBytes != null)
            {
                // H-02: un saludo reenviado en otra conexión lleva el nonce de la anterior, así que
                // el servidor debe rechazarlo sin llegar a la base de datos.
                ReplayedHelloRejected = VerifyReplayedHelloRejected(cancellationToken);
            }
        }

        public void Dispose()
        {
            DiscardTransport();
        }

        /// <summary>Un intento completo: conectar, saludar y jugar hasta que toque salir.</summary>
        private AttemptResult PlayOnce(CancellationToken cancellationToken)
        {
            DiscardTransport();

            ResetAttemptState();
            _attemptStartedAt = Environment.TickCount64;
            _nextInputAt = _attemptStartedAt;
            _nextDirectionChangeAt = _attemptStartedAt;
            _nextGarbageAt = _attemptStartedAt;
            _nextInvalidDirectionAt = _attemptStartedAt;
            _nextReplayAt = _attemptStartedAt;
            _nextChatAt = _attemptStartedAt;
            _nextAttackAt = _attemptStartedAt;
            _npcs.Clear();
            _lastSnapshotTick = 0;
            _hasPosition = false;
            PickDirection();

            if (_firstAttemptAt < 0)
            {
                _firstAttemptAt = _attemptStartedAt;
            }

            IClientTransport transport;

            try
            {
                transport = _transportFactory();
            }
            catch (Exception ex)
            {
                Status = "no se pudo crear el transporte: " + ex.Message;
                return AttemptResult.Failed;
            }

            _transport = transport;
            transport.Connected += OnConnected;
            transport.Disconnected += OnDisconnected;
            transport.Received += OnReceived;

            try
            {
                transport.Connect(_host, _port);
            }
            catch (Exception ex)
            {
                Status = "no se pudo conectar: " + ex.Message;
                return AttemptResult.Failed;
            }

            while (true)
            {
                transport.Poll();

                long now = Environment.TickCount64;

                if (_connected && _challengeReceived && !_helloSent)
                {
                    SendHello(_password);
                }

                if (_keyMismatch)
                {
                    Status = "la clave del servidor no coincide";
                    return AttemptResult.Failed;
                }

                if (_reject.HasValue)
                {
                    RejectReason reason = _reject.Value;

                    if (reason == RejectReason.AccountTaken && _intent == HelloIntent.Register)
                    {
                        _intent = HelloIntent.Login;
                        return AttemptResult.RetryLogin;
                    }

                    if (reason == RejectReason.RateLimited)
                    {
                        return AttemptResult.RateLimited;
                    }

                    Status = "rechazado (" + DescribeReject(reason) + ")";
                    RejectReasonText = reason.ToString();
                    return AttemptResult.RejectedFinally;
                }

                if (_disconnected && !_welcomed)
                {
                    Status = "conexión cerrada antes del saludo";
                    return AttemptResult.Failed;
                }

                if (_welcomed)
                {
                    if (now - _welcomedAt >= _runMs)
                    {
                        _playedMs = now - _welcomedAt;
                        return AttemptResult.Finished;
                    }

                    if (now >= _nextInputAt)
                    {
                        if (_ownDead)
                        {
                            // Muerto: el servidor consume los inputs pero no mueve. Se mandan a
                            // cero para que la predicción local tampoco se mueva (I-03).
                            _directionX = 0;
                            _directionY = 0;
                        }
                        else if (_pvpEnabled && !_pvpModeRequested && _lastPvpStatus.Mode == PvpMode.Pve &&
                                 _lastPvpStatus.Change == PvpChangeState.Waiting)
                        {
                            // Bot PvP: quieto en zona segura hasta poder pedir el modo (salir reinicia la espera).
                            _directionX = 0;
                            _directionY = 0;
                        }
                        else if (_behavior == BotBehavior.Fight || _behavior == BotBehavior.Inventory)
                        {
                            UpdateFightDirection();
                        }
                        else if (now >= _nextDirectionChangeAt)
                        {
                            PickDirection();
                        }

                        SendMoveInputs();

                        _nextInputAt += InputIntervalMs;
                        if (_nextInputAt < now)
                        {
                            // Si el hilo se durmió de más, no acumular una ráfaga de inputs viejos.
                            _nextInputAt = now + InputIntervalMs;
                        }
                    }

                    if ((_behavior == BotBehavior.Fight || _behavior == BotBehavior.Inventory) && !_ownDead && now >= _nextAttackAt)
                    {
                        SendAttackIfInRange(now);
                    }

                    if ((_behavior == BotBehavior.Fight || _behavior == BotBehavior.Inventory) &&
                        !_ownDead && _itemDecisionPending)
                    {
                        UpdateItemActions(_pendingFightSnapshot);
                        SendSelfHealIfNeeded(now);
                        _itemDecisionPending = false;
                    }

                    if (_behavior == BotBehavior.Inventory && !_ownDead && now >= _nextInventoryActionAt)
                    {
                        SendRandomInventoryAction();
                        _nextInventoryActionAt = now + 500;
                    }

                    if (_behavior == BotBehavior.Chat && now >= _nextChatAt)
                    {
                        SendChat();
                        _nextChatAt = now + 4000 + _random.Next(2001);
                    }

                    // Los abusos van en su propio temporizador: el bot sigue moviéndose normal
                    // salvo en flood, donde los inputs válidos ya van a 200/s.
                    if (_behavior == BotBehavior.Garbage && now >= _nextGarbageAt)
                    {
                        SendGarbage();
                        _nextGarbageAt += GarbageIntervalMs;
                    }

                    if (_behavior == BotBehavior.Speed && now >= _nextInvalidDirectionAt)
                    {
                        SendInvalidDirection();
                        _nextInvalidDirectionAt += InvalidDirectionIntervalMs;
                    }

                    if (_behavior == BotBehavior.Replay && now >= _nextReplayAt)
                    {
                        SendReplayedSequences();
                        _nextReplayAt += ReplayIntervalMs;
                    }
                }
                else if (now - _attemptStartedAt >= _welcomeTimeoutMs)
                {
                    Status = "sin respuesta del servidor";
                    return AttemptResult.Failed;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return AttemptResult.Cancelled;
                }

                Thread.Sleep(2);
            }
        }

        /// <summary>
        /// Modo <c>badlogin</c>: prepara la cuenta y luego falla la contraseña una y otra vez, sin
        /// la rampa de G-01, hasta ver el <c>RateLimited</c> del servidor.
        /// </summary>
        /// <remarks>
        /// En una base nueva, una cuenta que no existe se <b>registra</b> con un saludo correcto: no
        /// habría un fallo que cuente para el límite sino un alta. Si ya existe, cambia a login. Por eso
        /// el primer saludo es correcto y los demás llevan la contraseña
        /// mal a propósito. Esa preparación se cuenta aparte y se ve en el resumen.
        /// </remarks>
        private void RunBadLogin(CancellationToken cancellationToken)
        {
            if (_firstAttemptAt < 0)
            {
                _firstAttemptAt = Environment.TickCount64;
            }

            bool prepared = false;

            for (int attempt = 1; attempt <= _maxRetries + 2 && !cancellationToken.IsCancellationRequested; attempt++)
            {
                LoginOutcome outcome = AttemptLogin(cancellationToken, _password, _welcomeTimeoutMs);

                if (outcome.Kind == LoginOutcomeKind.Welcomed)
                {
                    prepared = true;
                    break;
                }

                if (outcome.Kind == LoginOutcomeKind.Rejected &&
                    outcome.Reason == RejectReason.AccountTaken &&
                    _intent == HelloIntent.Register)
                {
                    _intent = HelloIntent.Login;
                    continue;
                }

                if (outcome.Kind == LoginOutcomeKind.Rejected && outcome.Reason == RejectReason.RateLimited)
                {
                    // Puede ser el tope global de logins en vuelo: se espera y se reintenta.
                    if (!Sleep(RetryDelayMs, cancellationToken))
                    {
                        break;
                    }

                    continue;
                }

                break;
            }

            if (!prepared)
            {
                Status = "no se pudo preparar la cuenta";
                return;
            }

            // La preparación entra al mundo un instante y se va; el bot no es un jugador
            // conectado al final, así que el resumen no debe contarlo como tal.
            EntityId = -1;

            _intent = HelloIntent.Login;

            string wrongPassword = _password + "-incorrecta";
            RejectReason lastReject = RejectReason.Unknown;

            for (int attempt = 1; attempt <= MaxBadLoginAttempts && !cancellationToken.IsCancellationRequested; attempt++)
            {
                LoginOutcome outcome = AttemptLogin(cancellationToken, wrongPassword, 5000);

                if (outcome.Kind == LoginOutcomeKind.Welcomed)
                {
                    Status = "entró con contraseña incorrecta (el servidor falló)";
                    return;
                }

                if (outcome.Kind == LoginOutcomeKind.Rejected)
                {
                    lastReject = outcome.Reason;

                    if (outcome.Reason == RejectReason.RateLimited)
                    {
                        Status = "rechazado (RateLimited)";
                        RejectReasonText = RejectReason.RateLimited.ToString();
                        return;
                    }
                }

                if (!Sleep(BadLoginPauseMs, cancellationToken))
                {
                    Status = "cancelado";
                    return;
                }
            }

            Status = "rechazado (" + DescribeReject(lastReject) + ")";
            RejectReasonText = lastReject.ToString();
        }

        /// <summary>Un intento de login suelto: conectar, saludar y esperar <c>Welcome</c> o <c>Reject</c>.</summary>
        private LoginOutcome AttemptLogin(CancellationToken cancellationToken, string password, int timeoutMs)
        {
            DiscardTransport();
            ResetAttemptState();

            IClientTransport transport;

            try
            {
                transport = _transportFactory();
            }
            catch (Exception ex)
            {
                Status = "no se pudo crear el transporte: " + ex.Message;
                return LoginOutcome.Failed();
            }

            _transport = transport;
            transport.Connected += OnConnected;
            transport.Disconnected += OnDisconnected;
            transport.Received += OnReceived;

            try
            {
                transport.Connect(_host, _port);
            }
            catch (Exception ex)
            {
                Status = "no se pudo conectar: " + ex.Message;
                return LoginOutcome.Failed();
            }

            long deadline = Environment.TickCount64 + timeoutMs;

            while (Environment.TickCount64 < deadline && !cancellationToken.IsCancellationRequested)
            {
                transport.Poll();

                if (_connected && _challengeReceived && !_helloSent)
                {
                    SendHello(password);
                }

                if (_keyMismatch)
                {
                    DiscardTransport();
                    Status = "la clave del servidor no coincide";
                    return LoginOutcome.Failed();
                }

                if (_reject.HasValue)
                {
                    RejectReason reason = _reject.Value;
                    DiscardTransport();
                    return LoginOutcome.Rejected(reason);
                }

                if (_welcomed)
                {
                    DiscardTransport();
                    return LoginOutcome.Welcomed();
                }

                if (_disconnected)
                {
                    DiscardTransport();
                    return LoginOutcome.Failed();
                }

                Thread.Sleep(2);
            }

            DiscardTransport();
            Status = "sin respuesta del servidor";
            return LoginOutcome.Failed();
        }

        private void ResetAttemptState()
        {
            _connected = false;
            _helloSent = false;
            _welcomed = false;
            _disconnected = false;
            _reject = null;
            _challengeReceived = false;
            _keyMismatch = false;
            _challengeNonce = null;
            _creatingCharacter = false;
            _selectionSent = false;

            // La sesión es nueva: los inputs del intento anterior no sirven como redundancia.
            _recentCount = 0;
            _prediction = null;

            // La vida es de la sesión: al reconectar se vuelve a mirar el primer snapshot.
            _ownAction = EntityAction.None;
            _ownDead = false;
            _ownHealthPercent = 100;
            _lastAttackTick = 0;
            _inventoryItemIds = Array.Empty<ushort>();
            _inventoryCounts = Array.Empty<ushort>();
            _gold = 0;
            _equipmentItemIds = new ushort[ProtocolConstants.EquipSlotCount];
            _itemCatalog.Clear();
            _equippedItemId = 0;
            _pendingEquipItemId = 0;
            _pendingEquipSlot = 0;
            _fistRange = AttackRange;
            _fistCooldownSeconds = _attackCooldownMs / 1000f;
            _itemDecisionPending = false;
            _level = 1;
            _hasProgress = false;
            _hasStats = false;
            _classId = 0;
            _skillEntries = Array.Empty<SkillCatalogEntry>();
            _skillReadyAt = Array.Empty<long>();
            _globalSkillReadyAt = 0;
            _globalCooldownMs = 0;
            _resource = 0;
            _lastFreePoints = 0;
            _pendingAllocationPoints = 0;
            _primaryAttribute = -1;
            _nextChatAt = 0;
        }

        private void DiscardTransport()
        {
            IClientTransport? transport = _transport;
            if (transport == null)
            {
                return;
            }

            _transport = null;

            try
            {
                transport.Disconnect();
            }
            catch (Exception)
            {
                // Un transporte ya caído no debe impedir el reintento.
            }

            transport.Connected -= OnConnected;
            transport.Disconnected -= OnDisconnected;
            transport.Received -= OnReceived;
            transport.Dispose();
        }

        private void OnConnected()
        {
            _connected = true;
        }

        private void OnDisconnected(DisconnectReason reason)
        {
            _disconnected = true;
        }

        private void SendHello(string password)
        {
            _helloSent = true;
            Interlocked.Increment(ref _sentHellos);

            // Protocolo v4: la contraseña y la invitación van cifradas con la clave pública del
            // desafío. El nonce ata el saludo a esta conexión.
            byte[] encrypted = HelloSecrets.Encrypt(
                _serverPublicKey,
                _challengeNonce!,
                password,
                _invitationCode);

            byte[] hello = MessageCodec.EncodeHello(new HelloMessage(
                ProtocolConstants.Version,
                (ushort)SimulationConstants.TickRate,
                _mapHash,
                _clientToken,
                _accountName,
                encrypted,
                _intent));

            // Se guarda el saludo tal cual para que el modo replay lo reenvíe en otra conexión.
            _helloBytes = hello;
            _transport!.Send(hello, hello.Length, DeliveryMode.ReliableOrdered);
        }

        private void SendCreateCharacter(byte classId)
        {
            if (string.IsNullOrEmpty(_characterNameAttempt))
            {
                _characterNameAttempt = "bot" + Index.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            _creatingCharacter = true;
            byte[] create = MessageCodec.EncodeCreateCharacter(
                new CreateCharacterMessage(_characterNameAttempt, classId));
            _transport!.Send(create, create.Length, DeliveryMode.ReliableOrdered);
        }

        /// <summary>
        /// Comprueba la huella del servidor y guarda el nonce del desafío. Si la huella configurada
        /// no coincide, corta: es la única forma de detectar un servidor impostor.
        /// </summary>
        private void HandleChallenge(in ChallengeMessage challenge)
        {
            _serverPublicKey = HelloSecrets.ToPublicKey(challenge.Modulus, challenge.Exponent);
            string fingerprint = HelloSecrets.Fingerprint(_serverPublicKey);

            if (_serverKeyFingerprint.Length > 0)
            {
                if (!string.Equals(_serverKeyFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    _keyMismatch = true;
                    Status = "la clave del servidor no coincide";
                    _transport?.Disconnect();
                    return;
                }
            }
            else
            {
                WarnMissingFingerprint(fingerprint);
            }

            _challengeNonce = challenge.Nonce;
            _challengeReceived = true;
        }

        /// <summary>
        /// Avisa una sola vez por proceso: sin <c>--server-key</c> se acepta la clave recibida, pero
        /// se deja su huella para poder configurarla la próxima vez.
        /// </summary>
        private static void WarnMissingFingerprint(string fingerprint)
        {
            if (Interlocked.Exchange(ref _missingFingerprintWarned, 1) == 0)
            {
                Console.WriteLine(
                    "AVISO: no se configuró --server-key; se acepta el servidor con huella " + fingerprint + ".");
            }
        }

        /// <summary>
        /// Reenvía el saludo de la sesión anterior en una conexión nueva (modo <c>replay</c>, H-02):
        /// lleva el nonce viejo, así que el servidor debe rechazarlo sin llegar al repositorio.
        /// Devuelve <c>true</c> si el servidor rechazó el saludo reenviado.
        /// </summary>
        private bool VerifyReplayedHelloRejected(CancellationToken cancellationToken)
        {
            byte[] hello = _helloBytes!;

            DiscardTransport();

            _connected = false;
            _helloSent = false;
            _welcomed = false;
            _disconnected = false;
            _reject = null;
            _challengeReceived = false;
            _keyMismatch = false;
            _challengeNonce = null;

            IClientTransport transport;

            try
            {
                transport = _transportFactory();
            }
            catch (Exception)
            {
                return false;
            }

            _transport = transport;
            transport.Connected += OnConnected;
            transport.Disconnected += OnDisconnected;
            transport.Received += OnReceived;

            try
            {
                transport.Connect(_host, _port);
            }
            catch (Exception)
            {
                DiscardTransport();
                return false;
            }

            long deadline = Environment.TickCount64 + _welcomeTimeoutMs;
            bool replayed = false;

            while (Environment.TickCount64 < deadline && !cancellationToken.IsCancellationRequested)
            {
                transport.Poll();

                // No se manda el saludo nuevo: en cuanto llega el desafío, se reenvía el viejo.
                if (_challengeReceived && !replayed)
                {
                    replayed = true;
                    Interlocked.Increment(ref _sentHellos);
                    transport.Send(hello, hello.Length, DeliveryMode.ReliableOrdered);
                }

                if (_reject.HasValue)
                {
                    bool rejected = !_welcomed;
                    DiscardTransport();
                    return rejected;
                }

                if (_welcomed || _disconnected)
                {
                    DiscardTransport();
                    return false;
                }

                Thread.Sleep(2);
            }

            DiscardTransport();
            return false;
        }

        /// <summary>
        /// Manda los inputs válidos del intervalo: uno normal, diez si es <c>flood</c>. Cada
        /// mensaje lleva los <see cref="InputRedundancy"/> inputs más recientes (G-03) y, en modo
        /// <c>random</c>, primero lo aplica a la predicción local.
        /// </summary>
        private void SendMoveInputs()
        {
            int count = _behavior == BotBehavior.Flood ? FloodInputsPerInterval : 1;

            for (int i = 0; i < count; i++)
            {
                sbyte directionX = _directionX;
                sbyte directionY = _directionY;

                uint sequence;

                if (_prediction != null)
                {
                    // El mismo camino que el cliente de Unity: predicir primero y mandar la
                    // secuencia que devuelve la predicción.
                    sequence = _prediction.AddInput(directionX, directionY);
                    _sequence = sequence;
                }
                else
                {
                    _sequence++;
                    sequence = _sequence;
                }

                RememberInput(sequence, directionX, directionY, isValid: true);

                byte[] input = EncodeRedundantInput(sequence);

                // Secuenciado: si un input se pierde, el siguiente lo reemplaza; no se reintenta.
                _transport!.Send(input, input.Length, DeliveryMode.Sequenced);
                Interlocked.Increment(ref _sentValidInputs);
            }
        }

        /// <summary>
        /// Manda una intención con la dirección fuera de rango. El códec del bot no la valida (solo
        /// escribe los bytes): la rechaza el servidor al deserializar. No entra en el historial de
        /// redundancia, porque un mensaje con cualquier dirección inválida se rechaza entero.
        /// </summary>
        private void SendInvalidDirection()
        {
            int index = _invalidDirectionKind % (InvalidDirections.GetLength(0));
            _invalidDirectionKind++;

            sbyte directionX = InvalidDirections[index, 0];
            sbyte directionY = InvalidDirections[index, 1];

            _sequence++;
            uint sequence = _sequence;

            RememberInput(sequence, directionX, directionY, isValid: false);

            byte[] input = MessageCodec.EncodeMoveInput(
                new MoveInputMessage(sequence, directionX, directionY));

            _transport!.Send(input, input.Length, DeliveryMode.Sequenced);
            Interlocked.Increment(ref _sentInvalidInputs);
        }

        /// <summary>Manda un mensaje de un solo input sin tocar el historial (modo <c>replay</c>).</summary>
        private void SendRawMoveInput(uint sequence, sbyte directionX, sbyte directionY)
        {
            byte[] input = MessageCodec.EncodeMoveInput(
                new MoveInputMessage(sequence, directionX, directionY));

            _transport!.Send(input, input.Length, DeliveryMode.Sequenced);
            Interlocked.Increment(ref _sentReplayInputs);
        }

        /// <summary>Guarda el input como el más reciente y desplaza los anteriores.</summary>
        private void RememberInput(uint sequence, sbyte directionX, sbyte directionY, bool isValid)
        {
            for (int i = _recentSequences.Length - 1; i > 0; i--)
            {
                _recentSequences[i] = _recentSequences[i - 1];
                _recentX[i] = _recentX[i - 1];
                _recentY[i] = _recentY[i - 1];
                _recentValid[i] = _recentValid[i - 1];
            }

            _recentSequences[0] = sequence;
            _recentX[0] = directionX;
            _recentY[0] = directionY;
            _recentValid[0] = isValid;

            if (_recentCount < _recentSequences.Length)
            {
                _recentCount++;
            }
        }

        /// <summary>
        /// Codifica el input más nuevo con las copias anteriores que correspondan a secuencias
        /// contiguas y válidas. Si hubo un input inválido o no contiguo, se corta ahí: el par
        /// <c>i</c> siempre debe ser la secuencia <c>Sequence - i</c>.
        /// </summary>
        private byte[] EncodeRedundantInput(uint newestSequence)
        {
            int count = Math.Min(_inputRedundancy, _recentCount);

            if (count > 0 && _recentSequences[0] != newestSequence)
            {
                count = 0;
            }

            for (int i = 1; i < count; i++)
            {
                if (!_recentValid[i] || _recentSequences[i] != newestSequence - (uint)i)
                {
                    count = i;
                    break;
                }
            }

            // La secuencia más nueva no puede ser menor que n (el protocolo lo rechaza).
            while (count > 1 && newestSequence < (uint)count)
            {
                count--;
            }

            sbyte dir1X = 0;
            sbyte dir1Y = 0;
            sbyte dir2X = 0;
            sbyte dir2Y = 0;
            sbyte dir3X = 0;
            sbyte dir3Y = 0;

            if (count > 1)
            {
                dir1X = _recentX[1];
                dir1Y = _recentY[1];
            }

            if (count > 2)
            {
                dir2X = _recentX[2];
                dir2Y = _recentY[2];
            }

            if (count > 3)
            {
                dir3X = _recentX[3];
                dir3Y = _recentY[3];
            }

            return MessageCodec.EncodeMoveInput(new MoveInputMessage(
                newestSequence,
                count,
                _recentX[0],
                _recentY[0],
                dir1X,
                dir1Y,
                dir2X,
                dir2Y,
                dir3X,
                dir3Y));
        }

        /// <summary>Repite secuencias ya usadas: duplicadas y muy viejas, sin mover la posición.</summary>
        private void SendReplayedSequences()
        {
            if (_sequence <= 0)
            {
                return;
            }

            // Tres saltos hacia atrás: una repetida, una vieja y la primera de todas.
            SendRawMoveInput(_sequence, _directionX, _directionY);
            SendRawMoveInput(_sequence > 30 ? _sequence - 30 : 1, _directionX, _directionY);
            SendRawMoveInput(1, _directionX, _directionY);
        }

        /// <summary>
        /// Manda un datagrama que el servidor no puede interpretar. Se rota entre tipo desconocido,
        /// truncado, longitud de más y bytes al azar; siempre por <see cref="IClientTransport.Send"/>
        /// con bytes crudos, sin tocar el códec compartido.
        /// </summary>
        private void SendGarbage()
        {
            byte[] garbage;
            int kind = _garbageKind % 4;
            _garbageKind++;

            switch (kind)
            {
                case 0:
                    // Tipo que no existe en el protocolo.
                    garbage = new byte[] { 0x7F, 0x01, 0x02, 0x03 };
                    break;

                case 1:
                    // MoveInput truncado: 3 de sus bytes de cabecera.
                    garbage = new byte[]
                    {
                        (byte)MessageType.MoveInput,
                        (byte)(_sequence & 0xFF),
                        (byte)((_sequence >> 8) & 0xFF),
                    };
                    break;

                case 2:
                {
                    // Longitud de más: un MoveInput válido con dos bytes de propina.
                    byte[] valid = MessageCodec.EncodeMoveInput(
                        new MoveInputMessage(_sequence, 1, 0));

                    garbage = new byte[valid.Length + 2];
                    Buffer.BlockCopy(valid, 0, garbage, 0, valid.Length);
                    garbage[valid.Length] = 0xAA;
                    garbage[valid.Length + 1] = 0xBB;
                    break;
                }

                default:
                    // Bytes al azar. Como no hay saludo de por medio, el servidor los rechaza al
                    // intentar leerlos como MoveInput.
                    garbage = new byte[16 + _random.Next(32)];
                    _random.NextBytes(garbage);
                    break;
            }

            _transport!.Send(garbage, garbage.Length, DeliveryMode.Sequenced);
            Interlocked.Increment(ref _sentGarbage);
        }

        private void OnReceived(int connectionId, byte[] buffer, int offset, int length)
        {
            if (MessageCodec.TryReadPvpStatus(buffer, offset, length, out PvpStatusMessage pvpStatus))
            {
                _lastPvpStatus = pvpStatus;
                Interlocked.Increment(ref _pvpStatuses);
                if (_pvpEnabled && !_pvpModeRequested &&
                    pvpStatus.Change == PvpChangeState.Ready && pvpStatus.Mode == PvpMode.Pve)
                {
                    byte[] request = MessageCodec.EncodeSetPvpMode(new SetPvpModeMessage(PvpMode.Pvp));
                    _transport!.Send(request, request.Length, DeliveryMode.ReliableOrdered);
                    _pvpModeRequested = true;
                    Interlocked.Increment(ref _pvpModeRequests);
                }

                return;
            }

            if (MessageCodec.TryReadPvpModeRejected(buffer, offset, length, out _))
            {
                Interlocked.Increment(ref _pvpRejects);
                return;
            }

            if (MessageCodec.TryReadSkillCatalog(buffer, offset, length, out SkillCatalogMessage skillCatalog))
            {
                _skillEntries = skillCatalog.Skills;
                _skillReadyAt = new long[_skillEntries.Length];
                _globalCooldownMs = skillCatalog.GlobalCooldownTicks * 1000 / SimulationConstants.TickRate;
                _globalSkillReadyAt = 0;

                return;
            }

            if (MessageCodec.TryReadResourceStatus(buffer, offset, length, out ResourceStatusMessage resource))
            {
                _resource = resource.Current;
                Interlocked.Increment(ref _resourceMessages);
                return;
            }

            if (MessageCodec.TryReadStatusEffects(buffer, offset, length, out StatusEffectsMessage statuses))
            {
                Interlocked.Increment(ref _statusMessages);
                if (statuses.SpeedPercent < 100) Interlocked.Increment(ref _slowMessages);
                float speed = statuses.SpeedPercent == 100
                    ? _welcomeSpeed
                    : _welcomeSpeed * statuses.SpeedPercent / 100f;
                _speed = speed;
                _prediction?.SetSpeed(speed);
                return;
            }

            if (MessageCodec.TryReadStatusEvent(buffer, offset, length, out _)) return;

            if (MessageCodec.TryReadSkillUsed(buffer, offset, length, out _))
            {
                Interlocked.Increment(ref _skillsOk);
                return;
            }

            if (MessageCodec.TryReadSkillRejected(buffer, offset, length, out _))
            {
                Interlocked.Increment(ref _skillsRejected);
                return;
            }

            if (MessageCodec.TryReadZoneList(buffer, offset, length, out _))
            {
                Interlocked.Increment(ref _zoneLists);
                return;
            }

            if (MessageCodec.TryReadZoneChanged(buffer, offset, length, out ZoneChangedMessage zone))
            {
                Interlocked.Increment(ref _zoneChanges);
                if (zone.Kind == ZoneKind.Safe)
                {
                    Interlocked.Increment(ref _safeZoneEntries);
                }
                else if (zone.Kind == ZoneKind.Pvp)
                {
                    Interlocked.Increment(ref _pvpZoneEntries);
                }

                return;
            }

            if (MessageCodec.TryReadLootGained(buffer, offset, length, out LootGainedMessage loot))
            {
                Interlocked.Increment(ref _lootMessages);
                Interlocked.Add(ref _lootExperience, loot.Experience);
                Interlocked.Add(ref _lootGold, loot.Gold);
                for (int i = 0; i < loot.Items.Length; i++)
                {
                    if (loot.Items[i].OnGround)
                    {
                        Interlocked.Add(ref _lootOnGround, loot.Items[i].Count);
                    }
                    else
                    {
                        Interlocked.Add(ref _lootItems, loot.Items[i].Count);
                    }
                }

                return;
            }

            if (MessageCodec.TryReadCombatEvent(buffer, offset, length, out _))
            {
                Interlocked.Increment(ref _combatEventsReceived);
                return;
            }

            if (MessageCodec.TryReadChallenge(buffer, offset, length, out ChallengeMessage challenge))
            {
                HandleChallenge(challenge);
                return;
            }

            if (MessageCodec.TryReadWelcome(buffer, offset, length, out WelcomeMessage welcome))
            {
                long now = Environment.TickCount64;

                _itemCatalog.Clear();
                _welcomed = true;
                _welcomedAt = now;
                _lastSnapshotAt = now;
                if (_behavior == BotBehavior.Chat)
                {
                    _nextChatAt = now + 4000 + _random.Next(2001);
                }
                if (_behavior == BotBehavior.Inventory)
                {
                    _nextInventoryActionAt = now + 500;
                }
                _welcomeMs = now - _firstAttemptAt;
                EntityId = welcome.EntityId;
                _x = welcome.SpawnX;
                _y = welcome.SpawnY;
                _speed = welcome.Speed;
                _welcomeSpeed = welcome.Speed;
                _hasPosition = true;
                Status = "conectado";

                // Posición guardada con la que el servidor hace aparecer al personaje (G-05).
                if (_reportWelcomePath != null)
                {
                    AppendSharedLine(
                        _reportWelcomePath,
                        _accountName + ";" + Fixed(welcome.SpawnX) + ";" + Fixed(welcome.SpawnY));
                }

                // Igual que el cliente de Unity: si el bot es un jugador normal, predice su
                // posición y mide las correcciones que le costaría la red. El modo fight también
                // predice: se mueve como un jugador que persigue.
                if ((_behavior == BotBehavior.Random || _behavior == BotBehavior.Fight ||
                    _behavior == BotBehavior.Inventory ||
                    _behavior == BotBehavior.Chat) && _map != null)
                {
                    _halfSize = new Vec2(welcome.HalfWidth, welcome.HalfHeight);

                    _prediction = new ClientPrediction(
                        _map,
                        _halfSize,
                        welcome.Speed,
                        SimulationConstants.FixedDt);

                    _prediction.Reset(new Vec2(welcome.SpawnX, welcome.SpawnY));
                }

                return;
            }

            if (MessageCodec.TryReadReject(buffer, offset, length, out RejectMessage reject))
            {
                _reject = reject.Reason;

                if (reject.Reason != RejectReason.AccountTaken)
                {
                    _rejectCounts.TryGetValue(reject.Reason, out int count);
                    _rejectCounts[reject.Reason] = count + 1;
                }
                return;
            }

            if (MessageCodec.TryReadCharacterRejected(
                buffer, offset, length, out CharacterRejectedMessage characterRejected))
            {
                Interlocked.Increment(ref _characterRejects);
                if (characterRejected.Reason == RejectReason.NameTaken && !_selectionSent)
                {
                    _characterNameAttempt += "x";
                    if (_characterNameAttempt.Length <= 16)
                    {
                        SendCreateCharacter(_lastCharacterClassId);
                        return;
                    }
                }

                Status = "personaje rechazado (" + characterRejected.Reason + ")";
                RejectReasonText = characterRejected.Reason.ToString();
                return;
            }

            if (MessageCodec.TryReadCharacterList(buffer, offset, length, out CharacterListMessage characterList))
            {
                if (_creatingCharacter)
                {
                    for (int i = 0; i < characterList.Characters.Length; i++)
                    {
                        if (characterList.Characters[i].Name == _characterNameAttempt)
                        {
                            Interlocked.Increment(ref _charactersCreated);
                            break;
                        }
                    }

                    _creatingCharacter = false;
                }

                if (_selectionSent)
                {
                    return;
                }

                if (characterList.Characters.Length > 0)
                {
                    _selectionSent = true;
                    byte[] select = MessageCodec.EncodeSelectCharacter(
                        new SelectCharacterMessage(characterList.Characters[0].CharacterId));
                    _transport!.Send(select, select.Length, DeliveryMode.ReliableOrdered);
                    return;
                }

                if (characterList.Classes.Length == 0)
                {
                    Status = "el servidor no ofrece clases";
                    return;
                }

                if (string.IsNullOrEmpty(_characterNameAttempt))
                {
                    _characterNameAttempt = "bot" + Index.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                }

                CharacterClassEntry chosenClass = characterList.Classes[_random.Next(characterList.Classes.Length)];
                _lastCharacterClassId = chosenClass.ClassId;
                SendCreateCharacter(chosenClass.ClassId);
                return;
            }

            if (MessageCodec.TryReadItemCatalog(buffer, offset, length, out ItemCatalogMessage catalog))
            {
                for (int i = 0; i < catalog.Items.Length; i++)
                {
                    _itemCatalog[catalog.Items[i].Id] = catalog.Items[i];
                }

                _pickupRange = catalog.PickupRange;
                _fistRange = catalog.FistRange;
                _fistCooldownSeconds = catalog.FistCooldownSeconds;
                return;
            }

            if (MessageCodec.TryReadInventory(buffer, offset, length, out InventoryMessage inventory))
            {
                _inventoryItemIds = inventory.ItemIds;
                _inventoryCounts = inventory.Counts;
                _equipmentItemIds = inventory.Equipment;
                _equippedItemId = _equipmentItemIds[0];
                _gold = inventory.Gold;

                if (_pendingEquipItemId != 0)
                {
                    if (_pendingEquipSlot < _equipmentItemIds.Length &&
                        _equipmentItemIds[_pendingEquipSlot] == _pendingEquipItemId)
                    {
                        if (_pendingEquipSlot == 0)
                        {
                            Interlocked.Increment(ref _equipped);
                        }
                        else
                        {
                            Interlocked.Increment(ref _armorsEquipped);
                        }
                    }

                    _pendingEquipItemId = 0;
                }

                if (_behavior == BotBehavior.Fight)
                {
                    TryEquipBetterWeapon();
                    TryEquipArmor();
                }

                return;
            }

            if (MessageCodec.TryReadProgress(buffer, offset, length, out ProgressMessage progress))
            {
                if (_hasProgress && progress.Level > _level)
                {
                    Interlocked.Add(ref _levelUps, progress.Level - _level);
                }

                _level = progress.Level;
                _hasProgress = true;
                if (progress.Level > _maxLevelReached)
                {
                    _maxLevelReached = progress.Level;
                }

                return;
            }

            if (MessageCodec.TryReadStats(buffer, offset, length, out StatsMessage stats))
            {
                Interlocked.Increment(ref _statsReceived);
                _classId = stats.ClassId;

                if (_hasStats && stats.FreePoints < _lastFreePoints)
                {
                    Interlocked.Add(ref _pointsAllocated, _lastFreePoints - stats.FreePoints);
                }

                if (_pendingAllocationPoints > 0 && stats.FreePoints < _pendingAllocationPoints)
                {
                    _pendingAllocationPoints = 0;
                }

                _lastFreePoints = stats.FreePoints;
                _hasStats = true;

                if (_primaryAttribute < 0 && stats.AllocStr == 0 && stats.AllocDex == 0 &&
                    stats.AllocInt == 0 && stats.AllocVit == 0)
                {
                    _primaryAttribute = stats.Str >= stats.Dex && stats.Str >= stats.Int
                        ? 0
                        : stats.Dex >= stats.Int ? 1 : 2;
                }

                if (_behavior == BotBehavior.Fight && stats.FreePoints > 0 && _pendingAllocationPoints == 0)
                {
                    SendAllocatePoints(stats.FreePoints);
                }

                return;
            }

            if (MessageCodec.TryReadChat(buffer, offset, length, out _))
            {
                Interlocked.Increment(ref _receivedChat);
                return;
            }

            if (MessageCodec.TryReadSnapshot(buffer, offset, length, out SnapshotMessage snapshot))
            {
                Interlocked.Increment(ref _snapshots);

                if (_welcomed)
                {
                    long now = Environment.TickCount64;
                    long gap = now - _lastSnapshotAt;
                    if (gap > _maxSnapshotGapMs)
                    {
                        _maxSnapshotGapMs = gap;
                    }

                    _lastSnapshotAt = now;
                }

                // Con jitter los snapshots pueden llegar reordenados: un estado viejo no debe
                // pisar la posición ni contar como un viaje de ida y vuelta del personaje.
                if (_lastSnapshotTick != 0 && !SequenceMath.IsNewer(snapshot.ServerTick, _lastSnapshotTick))
                {
                    return;
                }

                _lastSnapshotTick = snapshot.ServerTick;

                if (_behavior == BotBehavior.Fight || _behavior == BotBehavior.Inventory)
                {
                    UpdateNpcView(snapshot);
                }

                for (int i = 0; i < snapshot.Entities.Length; i++)
                {
                    if (snapshot.Entities[i].Id == EntityId)
                    {
                        float x = ProtocolConstants.DequantizePosition(snapshot.Entities[i].X);
                        float y = ProtocolConstants.DequantizePosition(snapshot.Entities[i].Y);

                        if (_hasPosition)
                        {
                            float dx = x - _x;
                            float dy = y - _y;
                            _travelled += MathF.Sqrt((dx * dx) + (dy * dy));
                        }

                        _x = x;
                        _y = y;
                        _hasPosition = true;

                        // Vida propia (I-03): el snapshot manda Dead mientras dura la muerte. Al
                        // volver a vivo, la posición es la del spawn y la predicción debe partir
                        // de ahí, no contar el salto como una corrección enorme.
                        EntityAction previousAction = _ownAction;
                        _ownAction = snapshot.Entities[i].Action;
                        _ownHealthPercent = snapshot.Entities[i].HealthPercent;

                        if (_ownAction == EntityAction.Dead)
                        {
                            if (previousAction != EntityAction.Dead)
                            {
                                Interlocked.Increment(ref _ownDeaths);
                            }

                            _ownDead = true;
                        }
                        else if (_ownDead)
                        {
                            _ownDead = false;
                            Interlocked.Increment(ref _ownRespawns);
                            _prediction?.Teleport(new Vec2(x, y));
                        }

                        // Traza de caídas (G-05): cada snapshot. El servidor guarda la posición de
                        // un tick, que es la de un snapshot; a 1 Hz casi nunca coincidía.
                        if (_tracePositionsPath != null)
                        {
                            AppendSharedLine(
                                _tracePositionsPath,
                                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture) +
                                ";" + _accountName + ";" + Fixed(x) + ";" + Fixed(y));
                        }

                        if (_prediction != null)
                        {
                            bool corrected = _prediction.Reconcile(
                                new Vec2(x, y),
                                snapshot.LastProcessedInputSequence);

                            _corrections = _prediction.Corrections;
                            _snaps = _prediction.Snaps;

                            if (corrected && _logCorrections)
                            {
                                LogCorrection(
                                    snapshot.LastProcessedInputSequence,
                                    x,
                                    y);
                            }
                        }

                        break;
                    }
                }

                if ((_behavior == BotBehavior.Fight || _behavior == BotBehavior.Inventory) && !_ownDead)
                {
                    _pendingFightSnapshot = snapshot;
                    _itemDecisionPending = true;
                }
                else if (_ownDead)
                {
                    _itemDecisionPending = false;
                }
            }
        }

        /// <summary>
        /// Deja una línea por cada corrección de predicción (solo con <c>--log-corrections</c>): bot,
        /// secuencia confirmada, inputs pendientes reaplicados, base cuantizada, si esa base es
        /// transitable, delta aplicado y distancia a la pared más cercana en cada eje.
        /// </summary>
        private void LogCorrection(uint sequence, float baseX, float baseY)
        {
            var basePosition = new Vec2(baseX, baseY);
            Vec2 delta = _prediction!.LastCorrectionDelta;

            bool baseWalkable = _map!.IsBoxWalkable(basePosition, _halfSize);

            float wallX = NearestWallOnX(_map, basePosition, _halfSize, out string sideX);
            float wallY = NearestWallOnY(_map, basePosition, _halfSize, out string sideY);

            var pending = new System.Text.StringBuilder();
            for (int i = 0; i < _prediction.PendingCount; i++)
            {
                _prediction.GetPendingInput(i, out uint pendingSequence, out sbyte pendingX, out sbyte pendingY);
                if (i > 0)
                {
                    pending.Append('|');
                }

                pending.Append(pendingSequence).Append(':').Append(pendingX).Append(',').Append(pendingY);
            }

            Console.WriteLine(
                "  [corrección] bot=" + Index +
                " seq=" + sequence +
                " pend=" + _prediction.LastCorrectionPendingInputs +
                " pendientes=" + (pending.Length == 0 ? "-" : pending.ToString()) +
                " base=(" + Fixed(baseX) + "; " + Fixed(baseY) + ")" +
                " baseOk=" + (baseWalkable ? "sí" : "no") +
                " pred=(" + Fixed(_prediction.LastCorrectionPredictedPosition.X) + "; " +
                Fixed(_prediction.LastCorrectionPredictedPosition.Y) + ")" +
                " replay=(" + Fixed(_prediction.LastCorrectionReplayedPosition.X) + "; " +
                Fixed(_prediction.LastCorrectionReplayedPosition.Y) + ")" +
                " delta=(" + Fixed(delta.X) + "; " + Fixed(delta.Y) + ")" +
                " paredX=" + Fixed(wallX) + "/" + sideX +
                " paredY=" + Fixed(wallY) + "/" + sideY);
        }

        /// <summary>Distancia de la caja a la columna bloqueada más cercana en X.</summary>
        private static float NearestWallOnX(MapCollisionGrid map, Vec2 center, Vec2 half, out string side)
        {
            int minY = (int)MathF.Floor(center.Y - half.Y);
            int maxY = (int)MathF.Floor(center.Y + half.Y);

            float left = center.X - half.X;
            float right = center.X + half.X;

            float best = float.MaxValue;
            side = "der";

            for (int column = (int)MathF.Floor(right); column <= map.OriginX + map.Width; column++)
            {
                if (!ColumnBlocked(map, column, minY, maxY))
                {
                    continue;
                }

                best = column - right;
                break;
            }

            for (int column = (int)MathF.Floor(left); column >= map.OriginX - 1; column--)
            {
                if (!ColumnBlocked(map, column, minY, maxY))
                {
                    continue;
                }

                float distance = left - (column + 1f);
                if (distance < best)
                {
                    best = distance;
                    side = "izq";
                }

                break;
            }

            return best < 0f ? 0f : best;
        }

        /// <summary>Distancia de la caja a la fila bloqueada más cercana en Y.</summary>
        private static float NearestWallOnY(MapCollisionGrid map, Vec2 center, Vec2 half, out string side)
        {
            int minX = (int)MathF.Floor(center.X - half.X);
            int maxX = (int)MathF.Floor(center.X + half.X);

            float bottom = center.Y - half.Y;
            float top = center.Y + half.Y;

            float best = float.MaxValue;
            side = "arriba";

            for (int row = (int)MathF.Floor(top); row <= map.OriginY + map.Height; row++)
            {
                if (!RowBlocked(map, row, minX, maxX))
                {
                    continue;
                }

                best = row - top;
                break;
            }

            for (int row = (int)MathF.Floor(bottom); row >= map.OriginY - 1; row--)
            {
                if (!RowBlocked(map, row, minX, maxX))
                {
                    continue;
                }

                float distance = bottom - (row + 1f);
                if (distance < best)
                {
                    best = distance;
                    side = "abajo";
                }

                break;
            }

            return best < 0f ? 0f : best;
        }

        private static bool ColumnBlocked(MapCollisionGrid map, int column, int minRow, int maxRow)
        {
            for (int row = minRow; row <= maxRow; row++)
            {
                if (map.IsTileBlocked(column, row))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool RowBlocked(MapCollisionGrid map, int row, int minColumn, int maxColumn)
        {
            for (int column = minColumn; column <= maxColumn; column++)
            {
                if (map.IsTileBlocked(column, row))
                {
                    return true;
                }
            }

            return false;
        }

        private static string Fixed(float value) =>
            value.ToString("0.00000", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Añade una línea a un archivo compartido por los bots del proceso.</summary>
        private static void AppendSharedLine(string path, string line)
        {
            lock (SharedWriters)
            {
                if (!SharedWriters.TryGetValue(path, out StreamWriter? writer))
                {
                    // AutoFlush: cada línea llega al disco aunque el proceso acabe sin cerrar nada.
                    writer = new StreamWriter(path, append: true) { AutoFlush = true, NewLine = "\n" };
                    SharedWriters[path] = writer;
                }

                writer.WriteLine(line);
            }
        }

        private void PickDirection()
        {
            if (_behavior == BotBehavior.Crowd)
            {
                // Sin dirección: el servidor no mueve al jugador y todos se apilan en el spawn.
                _directionX = 0;
                _directionY = 0;
                _nextDirectionChangeAt = long.MaxValue;
                return;
            }

            _directionX = (sbyte)_random.Next(-1, 2);
            _directionY = (sbyte)_random.Next(-1, 2);
            _nextDirectionChangeAt = Environment.TickCount64 + _random.Next(800, 2200);
        }

        /// <summary>
        /// Guarda los enemigos del snapshot y cuenta las muertes nuevas: un enemigo que aparece por
        /// primera vez como <see cref="EntityAction.Dead"/> es una muerte vista (I-02).
        /// </summary>
        private void UpdateNpcView(in SnapshotMessage snapshot)
        {
            // La vista es la del último snapshot: se vacía para no perseguir enemigos que ya no
            // están en el área de interés.
            _npcs.Clear();

            EntityState[] entities = snapshot.Entities;

            for (int i = 0; i < entities.Length; i++)
            {
                EntityState state = entities[i];

                if (state.Kind == EntityKind.Player)
                {
                    if (_pvpEnabled && state.Id != EntityId && state.Pvp && state.Action != EntityAction.Dead)
                    {
                        _npcs[state.Id] = new NpcView(
                            ProtocolConstants.DequantizePosition(state.X),
                            ProtocolConstants.DequantizePosition(state.Y),
                            state.Action,
                            player: true);
                    }

                    continue;
                }

                if (state.Kind != EntityKind.Npc)
                {
                    continue;
                }

                if (state.Rank > 0 && _rankedNpcIds.Add(state.Id))
                {
                    Interlocked.Increment(ref _elitesSeen);
                }

                _npcs[state.Id] = new NpcView(
                    ProtocolConstants.DequantizePosition(state.X),
                    ProtocolConstants.DequantizePosition(state.Y),
                    state.Action,
                    player: false);

                if (state.Action == EntityAction.Dead && _deadNpcs.Add(state.Id))
                {
                    Interlocked.Increment(ref _deathsSeen);
                }
            }
        }

        /// <summary>
        /// Camina hacia el enemigo vivo más cercano; si ya está a alcance y tiene vista, se para. Sin ninguno a la
        /// vista pasea como <c>random</c> hasta encontrarlo: en el mapa grande el spawn no tiene
        /// enemigos dentro del área de interés (J-01).
        /// </summary>
        private void UpdateFightDirection()
        {
            if (FindNearestAlive(out float dx, out float dy, out float distanceSq) < 0)
            {
                if (Environment.TickCount64 >= _nextDirectionChangeAt)
                {
                    PickDirection();
                }

                return;
            }

            // Al ver un enemigo deja de pasear: la próxima vez que lo pierda elige rumbo nuevo.
            _nextDirectionChangeAt = 0;

            float range = CurrentAttackRange;
            bool inRange = distanceSq <= range * range;
            bool hasLineOfSight = _map == null || _map.HasLineOfSight(
                new Vec2(_x, _y), new Vec2(_x + dx, _y + dy));
            if (inRange && hasLineOfSight)
            {
                _directionX = 0;
                _directionY = 0;
                return;
            }

            _directionX = Sign(dx);
            _directionY = Sign(dy);
        }

        /// <summary>Manda un ataque al enemigo vivo más cercano si está a alcance y hay vista.</summary>
        private void SendAttackIfInRange(long now)
        {
            int target = FindNearestAlive(out float dx, out float dy, out float distanceSq);

            float range = CurrentAttackRange;
            if (target < 0 || distanceSq > range * range)
            {
                return;
            }

            Vec2 self = new Vec2(_x, _y);
            Vec2 targetPosition = new Vec2(_x + dx, _y + dy);
            if (_map != null && !_map.HasLineOfSight(self, targetPosition))
            {
                Interlocked.Increment(ref _attacksSkippedLineOfSight);
                _nextAttackAt = now + CurrentAttackCooldownMs + AttackSlackMs;
                return;
            }

            byte[] attack = MessageCodec.EncodeAttack(new AttackMessage(target));
            _transport!.Send(attack, attack.Length, DeliveryMode.ReliableOrdered);
            Interlocked.Increment(ref _sentAttacks);
            if (CurrentAttackRange > 2f) // Mismo umbral que CombatSystem.RangedMinRange (I-13).
            {
                Interlocked.Increment(ref _sentRangedAttacks);
            }
            _lastAttackTick = _lastSnapshotTick;

            if (_useSkills)
            {
                int selected = BotSkillRotation.Pick(_skillEntries, checked((byte)_level), _resource, now,
                    _skillReadyAt, _globalSkillReadyAt, MathF.Sqrt(distanceSq), _npcs[target].Player,
                    _ownHealthPercent);
                if (selected >= 0)
                {
                    SkillCatalogEntry skill = _skillEntries[selected];
                    bool selfTarget = skill.Effect == 1 ||
                        (skill.Effect == 2 && (skill.StatusKind == StatusKind.Bonus || skill.StatusKind == StatusKind.Shield)) ||
                        skill.AreaCenter == 1;
                    byte[] useSkill = MessageCodec.EncodeUseSkill(new UseSkillMessage(skill.Id,
                        selfTarget ? EntityId : target));
                    _transport!.Send(useSkill, useSkill.Length, DeliveryMode.ReliableOrdered);
                    if (skill.Effect == 0) Interlocked.Increment(ref _skillsSent);
                    else if (skill.Effect == 2) Interlocked.Increment(ref _statusSkillsSent);
                    else if (skill.Effect == 1) Interlocked.Increment(ref _healsSent);
                    _skillReadyAt[selected] = now + skill.CooldownTicks * 1000 / SimulationConstants.TickRate + AttackSlackMs;
                    _globalSkillReadyAt = now + _globalCooldownMs + AttackSlackMs;
                }
            }

            // La recarga del bot es la del servidor más un margen: si saliera justo en el tick,
            // un jitter mínimo la haría llegar antes de tiempo y contaría como rechazo.
            _nextAttackAt = now + CurrentAttackCooldownMs + AttackSlackMs;
        }

        private void SendChat()
        {
            byte[] chat = MessageCodec.EncodeChatSend(new ChatSendMessage("Hola-" + Index));
            _transport!.Send(chat, chat.Length, DeliveryMode.ReliableOrdered);
            Interlocked.Increment(ref _sentChat);
        }

        /// <summary>Reparte los puntos nuevos entre el atributo base principal y VIT.</summary>
        private void SendAllocatePoints(ushort freePoints)
        {
            if (_primaryAttribute < 0 || _transport == null)
            {
                return;
            }

            int primary = (freePoints + 1) / 2;
            int vit = freePoints / 2;
            if (primary > byte.MaxValue || vit > byte.MaxValue)
            {
                return;
            }

            byte str = _primaryAttribute == 0 ? (byte)primary : (byte)0;
            byte dex = _primaryAttribute == 1 ? (byte)primary : (byte)0;
            byte intelligence = _primaryAttribute == 2 ? (byte)primary : (byte)0;
            byte[] message = MessageCodec.EncodeAllocatePoints(
                new AllocatePointsMessage(str, dex, intelligence, (byte)vit));
            _transport.Send(message, message.Length, DeliveryMode.ReliableOrdered);
            _pendingAllocationPoints = freePoints;
        }

        /// <summary>Usa la cura propia bajo el 70 % de vida, con recurso y recarga suficientes.</summary>
        private void SendSelfHealIfNeeded(long now)
        {
            if (!_useSkills || EntityId <= 0) return;
            int selected = BotSkillRotation.Pick(_skillEntries, checked((byte)_level), _resource, now,
                _skillReadyAt, _globalSkillReadyAt, float.NaN, false, _ownHealthPercent);
            if (selected < 0) return;
            SkillCatalogEntry skill = _skillEntries[selected];
            byte[] bytes = MessageCodec.EncodeUseSkill(new UseSkillMessage(skill.Id, EntityId));
            _transport!.Send(bytes, bytes.Length, DeliveryMode.ReliableOrdered);
            Interlocked.Increment(ref _healsSent);
            _skillReadyAt[selected] = now + skill.CooldownTicks * 1000 / SimulationConstants.TickRate + AttackSlackMs;
            _globalSkillReadyAt = now + _globalCooldownMs + AttackSlackMs;
        }

        /// <summary>Recoge un objeto cercano y usa una poción si la vida bajó del 40 %.</summary>
        private void UpdateItemActions(in SnapshotMessage snapshot)
        {
            if (_ownHealthPercent < 40)
            {
                int slots = Math.Min(_inventoryItemIds.Length, _inventoryCounts.Length);
                for (int slot = 0; slot < slots; slot++)
                {
                    if (_inventoryItemIds[slot] == DefaultPotionItemId && _inventoryCounts[slot] > 0)
                    {
                        byte[] use = MessageCodec.EncodeUseItem(new UseItemMessage((byte)slot));
                        _transport!.Send(use, use.Length, DeliveryMode.ReliableOrdered);
                        Interlocked.Increment(ref _sentPotions);
                        break;
                    }
                }
            }

            if (snapshot.ServerTick == _lastAttackTick)
            {
                return;
            }

            for (int i = 0; i < snapshot.Entities.Length; i++)
            {
                EntityState state = snapshot.Entities[i];
                if (state.Kind != EntityKind.Item)
                {
                    continue;
                }

                float dx = ProtocolConstants.DequantizePosition(state.X) - _x;
                float dy = ProtocolConstants.DequantizePosition(state.Y) - _y;
                if ((dx * dx) + (dy * dy) > _pickupRange * _pickupRange)
                {
                    continue;
                }

                byte[] pickup = MessageCodec.EncodePickUp(new PickUpMessage(state.Id));
                _transport!.Send(pickup, pickup.Length, DeliveryMode.ReliableOrdered);
                Interlocked.Increment(ref _sentPickups);
                break;
            }
        }

        private float CurrentAttackRange =>
            _itemCatalog.TryGetValue(_equippedItemId, out ItemCatalogEntry weapon) && weapon.Kind == ItemKind.Weapon
                ? weapon.Range
                : _fistRange;

        private int CurrentAttackCooldownMs =>
            (int)Math.Ceiling(
                (_itemCatalog.TryGetValue(_equippedItemId, out ItemCatalogEntry weapon) && weapon.Kind == ItemKind.Weapon
                    ? weapon.CooldownSeconds
                    : _fistCooldownSeconds) * 1000f);

        /// <summary>Solicita el arma de mayor daño que supere a la equipada actualmente.</summary>
        private void TryEquipBetterWeapon()
        {
            if (_pendingEquipItemId != 0 || _transport == null)
            {
                return;
            }

            uint equippedDamage = _itemCatalog.TryGetValue(_equippedItemId, out ItemCatalogEntry equipped) &&
                equipped.Kind == ItemKind.Weapon
                    ? equipped.Damage
                    // FistDamage ya lleva el bono de atributo y item.Damage no: compararlos falsea la elección.
                    : 0u;
            uint bestDamage = equippedDamage;
            byte bestSlot = 0;
            ushort bestItemId = 0;
            int slots = Math.Min(_inventoryItemIds.Length, _inventoryCounts.Length);

            for (int i = 0; i < slots; i++)
            {
                ushort itemId = _inventoryItemIds[i];
                if (_inventoryCounts[i] == 0 ||
                    !_itemCatalog.TryGetValue(itemId, out ItemCatalogEntry item) ||
                    item.Kind != ItemKind.Weapon)
                {
                    continue;
                }

                if (!item.UsableByClass || _level < item.RequiredLevel ||
                    (item.TwoHanded && _equipmentItemIds[(int)EquipSlot.Offhand] != 0 && !HasFreeInventorySlot()))
                {
                    Interlocked.Increment(ref _equipOmittedRequirements);
                    continue;
                }

                if (item.Damage <= bestDamage)
                {
                    continue;
                }

                bestDamage = item.Damage;
                bestSlot = (byte)i;
                bestItemId = itemId;
            }

            if (bestItemId == 0)
            {
                return;
            }

            byte[] equip = MessageCodec.EncodeEquip(new EquipMessage(bestSlot));
            _transport.Send(equip, equip.Length, DeliveryMode.ReliableOrdered);
            _pendingEquipItemId = bestItemId;
            _pendingEquipSlot = 0;
        }

        /// <summary>Equipa la primera armadura que encuentre en un hueco de equipo vacío.</summary>
        private void TryEquipArmor()
        {
            if (_pendingEquipItemId != 0 || _transport == null)
            {
                return;
            }

            int slots = Math.Min(_inventoryItemIds.Length, _inventoryCounts.Length);
            for (int i = 0; i < slots; i++)
            {
                ushort itemId = _inventoryItemIds[i];
                if (_inventoryCounts[i] == 0 ||
                    !_itemCatalog.TryGetValue(itemId, out ItemCatalogEntry item))
                {
                    continue;
                }

                if (item.Kind != ItemKind.Armor ||
                    item.EquipSlot >= _equipmentItemIds.Length ||
                    _equipmentItemIds[item.EquipSlot] != 0)
                {
                    continue;
                }

                if (_level < item.RequiredLevel ||
                    (item.EquipSlot == (byte)EquipSlot.Offhand &&
                     _itemCatalog.TryGetValue(_equipmentItemIds[(int)EquipSlot.Weapon], out ItemCatalogEntry weapon) &&
                     weapon.TwoHanded))
                {
                    Interlocked.Increment(ref _equipOmittedRequirements);
                    continue;
                }

                byte[] equip = MessageCodec.EncodeEquip(new EquipMessage((byte)i));
                _transport.Send(equip, equip.Length, DeliveryMode.ReliableOrdered);
                _pendingEquipItemId = itemId;
                _pendingEquipSlot = item.EquipSlot;
                return;
            }
        }

        private bool HasFreeInventorySlot()
        {
            int slots = Math.Min(_inventoryItemIds.Length, _inventoryCounts.Length);
            for (int i = 0; i < slots; i++)
            {
                if (_inventoryItemIds[i] == 0 && _inventoryCounts[i] == 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Envía una mutación de inventario al azar cada medio segundo (I-14).</summary>
        private void SendRandomInventoryAction()
        {
            if (_transport == null)
            {
                return;
            }

            int slots = Math.Max(1, Math.Min(_inventoryItemIds.Length, _inventoryCounts.Length));
            byte first = (byte)_random.Next(slots);
            byte second = (byte)_random.Next(slots);
            ushort count = _random.Next(3) == 0 ? (ushort)0 : (ushort)_random.Next(1, 11);
            byte[] bytes;

            switch (_random.Next(4))
            {
                case 0:
                    bytes = MessageCodec.EncodeMoveItem(new MoveItemMessage(first, second, count));
                    break;
                case 1:
                    bytes = MessageCodec.EncodeEquip(new EquipMessage(first));
                    break;
                case 2:
                    byte equipSlot = (byte)_random.Next(ProtocolConstants.EquipSlotCount);
                    byte targetSlot = _random.Next(3) == 0
                        ? ProtocolConstants.FirstFreeInventorySlot
                        : first;
                    bytes = MessageCodec.EncodeUnequip(new UnequipMessage(equipSlot, targetSlot));
                    break;
                default:
                    bytes = MessageCodec.EncodeDestroyItem(new DestroyItemMessage(first, count));
                    break;
            }

            _transport.Send(bytes, bytes.Length, DeliveryMode.ReliableOrdered);
        }

        /// <summary>Enemigo vivo más cercano de la vista; devuelve su id y el vector hacia él.</summary>
        private int FindNearestAlive(out float dx, out float dy, out float distanceSq)
        {
            int best = -1;
            dx = 0f;
            dy = 0f;
            distanceSq = float.MaxValue;

            foreach (KeyValuePair<int, NpcView> pair in _npcs)
            {
                NpcView npc = pair.Value;

                if (npc.Action == EntityAction.Dead)
                {
                    continue;
                }

                float candidateX = npc.X - _x;
                float candidateY = npc.Y - _y;
                float candidateDistanceSq = (candidateX * candidateX) + (candidateY * candidateY);

                if (candidateDistanceSq < distanceSq)
                {
                    distanceSq = candidateDistanceSq;
                    dx = candidateX;
                    dy = candidateY;
                    best = pair.Key;
                }
            }

            return best;
        }

        private static sbyte Sign(float value) =>
            value > 0f ? (sbyte)1 : value < 0f ? (sbyte)-1 : (sbyte)0;

        /// <summary>Espera <paramref name="milliseconds"/> sin dormir de golpe, para poder cancelar.</summary>
        private static bool Sleep(int milliseconds, CancellationToken cancellationToken)
        {
            long deadline = Environment.TickCount64 + milliseconds;

            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    return true;
                }

                cancellationToken.WaitHandle.WaitOne((int)Math.Min(remaining, 50));
            }
        }

        /// <summary>Lo que el bot sabe de una entidad atacable por el último snapshot.</summary>
        private readonly struct NpcView
        {
            public NpcView(float x, float y, EntityAction action, bool player)
            {
                X = x;
                Y = y;
                Action = action;
                Player = player;
            }

            public float X { get; }

            public float Y { get; }

            public EntityAction Action { get; }

            public bool Player { get; }
        }

        private enum AttemptResult
        {
            Finished,
            RateLimited,
            RejectedFinally,
            Failed,
            Cancelled,
            RetryLogin,
        }

        private enum LoginOutcomeKind
        {
            Welcomed,
            Rejected,
            Failed,
        }

        private readonly struct LoginOutcome
        {
            private LoginOutcome(LoginOutcomeKind kind, RejectReason reason)
            {
                Kind = kind;
                Reason = reason;
            }

            public LoginOutcomeKind Kind { get; }

            public RejectReason Reason { get; }

            public static LoginOutcome Welcomed() => new LoginOutcome(LoginOutcomeKind.Welcomed, RejectReason.Unknown);

            public static LoginOutcome Rejected(RejectReason reason) => new LoginOutcome(LoginOutcomeKind.Rejected, reason);

            public static LoginOutcome Failed() => new LoginOutcome(LoginOutcomeKind.Failed, RejectReason.Unknown);
        }
    }
}
