using System;
using System.Collections.Generic;
using Avelora.Net;
using Avelora.Shared;
using Avelora.Shared.Prediction;
using Avelora.Shared.Protocol;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Avelora.Client
{
    /// <summary>
    /// Cliente de red de la Etapa D: saluda al servidor, predice al jugador local, reconcilia con
    /// los snapshots e interpola al resto de entidades.
    /// </summary>
    /// <remarks>
    /// <para>
    /// El cliente <b>solo manda intenciones</b> (dirección y número de secuencia). La posición, la
    /// velocidad y el tamaño de la caja vienen del <see cref="WelcomeMessage"/> del servidor. El
    /// movimiento se calcula con el mismo <see cref="MovementSimulator"/> y el mismo
    /// <see cref="SimulationConstants.FixedDt"/> que el servidor.
    /// </para>
    /// <para>
    /// El transporte se sondea en <see cref="Update"/>, en el hilo principal: LiteNetLib no es
    /// segura entre hilos y todos sus eventos llegan dentro de <c>Poll</c>. El bucle de predicción
    /// va a paso fijo (20 Hz) y el dibujo interpola entre los dos últimos pasos.
    /// </para>
    /// <para>
    /// Este componente sustituye al <see cref="LocalPlayerController"/> de la Etapa A cuando
    /// <c>connectOnStart</c> está activo. Con la red desactivada, el controlador local sigue
    /// funcionando como antes.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(AveloraMap))]
    public sealed partial class NetworkClient : MonoBehaviour
    {
        /// <summary>Si un frame dura más, se descarta el exceso: evita una avalancha de inputs tras un parón.</summary>
        private const double MaxFrameSeconds = 0.25;

        [Header("Servidor")]
        [SerializeField] private string host = "127.0.0.1";

        [SerializeField] private int port = ProtocolConstants.DefaultPort;

        [Tooltip("Huella SHA-256 esperada de la clave pública del servidor (64 hex). Vacía = aceptar la que llegue y avisar por consola; es el modo de desarrollo.")]
        [SerializeField] private string serverKeyFingerprint = string.Empty;

        [Header("Cuenta (Etapa F)")]
        [Tooltip("Cuenta registrada en el servidor. La primera vez se crea con el código de invitación.")]
        [SerializeField] private string accountName = "dev";

        [Tooltip("Contraseña de la cuenta. Viaja cifrada con RSA-OAEP-SHA1 dentro del saludo (protocolo v4).")]
        [SerializeField] private string password = "avelora-dev";

        [Tooltip("Código para registrar la cuenta si todavía no existe. Vacío = no registrar.")]
        [SerializeField] private string invitationCode = "avelora-dev";

        [Tooltip("Al empezar, conecta y desactiva el controlador local de la Etapa A.")]
        [SerializeField] private bool connectOnStart = true;

        [Tooltip("Tiempo máximo para recibir el Welcome antes de cerrar el socket.")]
        [SerializeField] private float welcomeTimeoutSeconds = 6f;

        [Header("Predicción e interpolación")]
        [Tooltip("Retraso con el que se dibuja al resto: 100 ms son dos snapshots a 20 Hz.")]
        [SerializeField] private float interpolationDelay = 0.1f;

        [SerializeField] private Color npcColor = new Color(1f, 0.6f, 0.2f, 1f);

        // Aspecto de cada tipo de PNJ por su id de npcs.json (I-03c). Un tipo que no está en la
        // lista usa el primero; sin ninguno, los PNJ se dibujan como un cuadrado teñido con npcColor.
        [SerializeField] private NpcLook[] npcLooks = new NpcLook[0];

        [Tooltip("La cámara principal sigue al jugador local (mapas más grandes que la pantalla).")]
        [SerializeField] private bool cameraFollowsPlayer = true;

        // shortcut: copia de playerRespawnSeconds de server.json, solo para la cuenta atrás del aviso;
        // si el servidor la cambia, el aviso se equivoca pero la reaparición la sigue decidiendo él.
        private const float RespawnSeconds = 5f;

        private const float HintSeconds = 1.5f;
        private const float PickRadius = 0.5f;
        // 8 ticks es minAttackTicks de classes.json.
        private const float MinAttackTicks = 8f;

        // Doble clic (I-06c): ventana entre clics, margen para pararse dentro del alcance aunque la
        // posición del servidor vaya un poco por detrás, y zona muerta por eje para no zigzaguear.
        private const float DoubleClickSeconds = 0.35f;
        private const float ApproachFraction = 0.85f;
        private const float AxisDeadZone = 0.15f;

        // Chat de zona (I-07b): Enter abre la caja, Enter envía, Esc cancela.
        private const int ChatLogLines = 8;
        private const string ChatControl = "avelora-chat";
        private readonly List<string> _chatLog = new List<string>(ChatLogLines + 1);
        private GUIStyle _chatLineStyle;
        private bool _chatOpen;
        private string _chatDraft = string.Empty;

        private bool _autoAttack;
        private int _lastClickId;
        private float _lastClickAt;

        private int _selectedNpcId;
        private float _nextAttackAt;
        private string _combatHint = string.Empty;
        private float _combatHintUntil;
        private int _attacksSent;

        private struct CombatProjectile
        {
            public int AttackerId;
            public int TargetId;
            public Vector3 From;
            public float StartTime;
            public float Duration;
            public ushort Damage;
            public CombatEventFlags Flags;
        }

        private struct AreaFlash
        {
            public Vector3 Center;
            public float Radius;
            public float StartTime;
            public Color Color;
        }

        private struct DamageNumber
        {
            public Vector3 Position;
            public float StartTime;
            public string Text;
            public bool OnSelf;
            public bool Heal;
            public bool Absorbed;
            public bool Critical;
        }

        private struct LootText
        {
            public string Text;
            public Color Color;
            public float StartTime;
            public int Line;
        }

        private readonly List<CombatProjectile> _projectiles = new List<CombatProjectile>(32);
        private readonly List<AreaFlash> _areaFlashes = new List<AreaFlash>(8);
        private readonly List<DamageNumber> _damageNumbers = new List<DamageNumber>(32);
        private readonly List<LootText> _lootTexts = new List<LootText>(16);

        [Header("Diagnóstico")]
        [SerializeField] private bool showOverlay = true;

        [Tooltip("Para pruebas automáticas: ignora el teclado y usa debugDirX/debugDirY.")]
        [SerializeField] private bool useDebugInput;

        [SerializeField] private int debugDirX;
        [SerializeField] private int debugDirY;

        private readonly SnapshotTimeline _timeline = new SnapshotTimeline();
        private readonly Dictionary<int, RemotePlayerView> _remoteViews = new Dictionary<int, RemotePlayerView>();
        private readonly HashSet<int> _aliveIds = new HashSet<int>();
        private readonly List<int> _deadIds = new List<int>();

        // Últimas direcciones enviadas, la más nueva primero: el mensaje lleva hasta cuatro inputs
        // contiguos para que una pérdida de UDP no sea un paso perdido. Se crea una vez, sin reservas
        // por frame.
        private readonly sbyte[] _inputHistoryX = new sbyte[ProtocolConstants.MaxInputsPerMessage];
        private readonly sbyte[] _inputHistoryY = new sbyte[ProtocolConstants.MaxInputsPerMessage];

        private AveloraMap _map;
        private SpriteRenderer _localRenderer;
        private LocalPlayerController _offlineController;
        private IClientTransport _transport;
        private ClientPrediction _prediction;
        private Transform _remoteRoot;
        private GUIStyle _overlayStyle;

        private double _accumulator;
        private double _connectStartedAt;
        private uint _clientToken;
        private uint _lastServerTick;
        private uint _lastProcessedSequence;
        private Vec2 _lastAuthoritative;
        private int _entityId;
        private ushort[] _inventoryIds = new ushort[0];
        private uint _gold;
        private ZoneInfo[] _zones = new ZoneInfo[0];
        private ushort _zoneId;
        private ZoneKind _zoneKind = ZoneKind.Normal;
        private SkillCatalogMessage _skillCatalog;
        private bool _skillCatalogReceived;
        private int[] _skillDisplayOrder = new int[0];
        private readonly StatusEffectEntry[] _ownStatuses = new StatusEffectEntry[ProtocolConstants.MaxStatusEffects];
        private int _ownStatusCount;
        private float _ownStatusReceivedAt;
        private float _welcomeSpeed;
        private readonly Dictionary<int, byte> _statusMasks = new Dictionary<int, byte>();
        private ResourceStatusMessage _resource;
        private bool _resourceReceived;
        private readonly Dictionary<ushort, float> _skillReadyAt = new Dictionary<ushort, float>();
        private enum ShortcutKind { Empty, Item, Skill }
        private struct HotbarShortcut
        {
            public ShortcutKind Kind;
            public ushort Id;
            public HotbarShortcut(ShortcutKind kind, ushort id) { Kind = kind; Id = id; }
        }
        private readonly HotbarShortcut[] _hotbar = new HotbarShortcut[10];
        private bool _showSkills;
        private PvpStatusMessage _pvpStatus;
        private bool _pvpStatusReceived;
        private float _pvpChangeReadyAt;
        private float _pvpForcedEndsAt;
        private Rect _pvpButtonRect;
        private Texture2D _minimapTexture;
        private bool _minimapDirty = true;
        // Objeto equipado por hueco, en el orden de EquipSlot (I-14); 0 = vacío.
        private ushort[] _equipment = new ushort[ProtocolConstants.EquipSlotCount];

        // Progreso que manda el servidor (I-06): nivel, experiencia y vida máxima del nivel.
        private byte _level;
        private uint _experience;
        private uint _experienceToNext;
        private uint _maxHealth;

        // Atributos que manda el servidor (I-09). El reparto pendiente vive solo en el cliente hasta
        // que se confirma: el servidor valida el total y contesta con otro Stats.
        private StatsMessage _stats;
        private bool _statsReceived;
        private bool _showStats;
        private readonly int[] _pendingPoints = new int[4];

        // Catálogo que manda el servidor tras el Welcome (I-05): nombres, tipos, armas, alcance para
        // recoger y los valores de los puños. Hasta que llega, no se ataca ni se recoge.
        private readonly Dictionary<ushort, ItemCatalogEntry> _catalog = new Dictionary<ushort, ItemCatalogEntry>();
        private bool _catalogReceived;
        private float _pickupRange;
        private float _fistRange;
        private float _fistCooldownSeconds;
        private ushort[] _inventoryCounts = new ushort[0];

        private bool _showInventory = true;
        private byte _ownHealthPercent = 100;
        private bool _ownDead;
        private float _ownDeathAt;
        private int _lastDirectionX;
        private int _inputHistoryCount;
        private long _inputsSent;
        private long _snapshotsReceived;
        private bool _connecting;
        private bool _welcomeReceived;
        private bool _disconnectRequested;
        private bool _challengeReceived;
        private bool _fingerprintWarned;
        private byte[] _challengeNonce;
        private System.Security.Cryptography.RSAParameters _serverPublicKey;
        private string _status = "sin conectar";
        private RejectReason? _lastReject;
        private CharacterListMessage _characterList;
        private bool _characterListReceived;
        private bool _selectionActive;
        private bool _characterOperationPending;
        private bool _characterSelectionPending;
        private bool _confirmDelete;
        private int _selectedCharacterId;
        private byte _selectedClassId;
        private byte _createClassId;
        private string _newCharacterName = string.Empty;
        private string _deleteConfirmationName = string.Empty;
        private Vector2 _characterScroll;
        private ClassLook _classLook;

        /// <summary>Estado legible para el overlay y las pruebas automáticas.</summary>
        public string Status => _status;

        public bool IsWelcomed => _welcomeReceived;

        public int EntityId => _entityId;

        public int PendingInputs => _prediction != null ? _prediction.PendingCount : 0;

        public long InputsSent => _inputsSent;

        public long SnapshotsReceived => _snapshotsReceived;

        public long Corrections => _prediction != null ? _prediction.Corrections : 0;

        public long Snaps => _prediction != null ? _prediction.Snaps : 0;

        public int RemoteViewCount => _remoteViews.Count;

        public uint LastInputSequence => _prediction != null ? _prediction.LastSequence : 0;

        public uint LastProcessedInputSequence => _lastProcessedSequence;

        public uint LastServerTick => _lastServerTick;

        public Vec2 PredictedPosition => _prediction != null ? _prediction.Position : Vec2.Zero;

        public Vec2 LastAuthoritativePosition => _lastAuthoritative;

        /// <summary>Costura para las pruebas de Play: sustituye el teclado por una dirección fija.</summary>
        public bool UseDebugInput
        {
            get => useDebugInput;
            set => useDebugInput = value;
        }

        /// <inheritdoc cref="debugDirX"/>
        public int DebugDirX
        {
            get => debugDirX;
            set => debugDirX = value;
        }

        /// <inheritdoc cref="debugDirY"/>
        public int DebugDirY
        {
            get => debugDirY;
            set => debugDirY = value;
        }

        private void Awake()
        {
            _map = GetComponent<AveloraMap>();
            _localRenderer = GetComponent<SpriteRenderer>();
            // Sprite base de la escena: lo copian PNJ y objetos sin arte propio aunque el jugador cambie al de su clase.
            _baseSprite = _localRenderer != null ? _localRenderer.sprite : null;
            _offlineController = GetComponent<LocalPlayerController>();
            _classLook = ClassLook.Load();
        }

        private void Start()
        {
            if (!connectOnStart)
            {
                _status = "red desactivada (controlador local)";
                return;
            }

            if (_offlineController != null)
            {
                // El controlador de la Etapa A movería el Transform por su cuenta: ahora manda la
                // predicción reconciliada.
                _offlineController.enabled = false;
            }

            Connect();
        }

        private void OnDestroy()
        {
            CleanupTransport();
            ClearRemoteViews();

            if (_remoteRoot != null)
            {
                Destroy(_remoteRoot.gameObject);
                _remoteRoot = null;
            }

            if (_minimapTexture != null)
            {
                Destroy(_minimapTexture);
                _minimapTexture = null;
            }
        }

        /// <summary>Abre una conexión nueva. Se puede llamar con R tras un rechazo o un timeout.</summary>
        public void Connect()
        {
            if (_transport != null || _welcomeReceived)
            {
                return;
            }

            if (_map == null || _map.Grid == null || _map.ContentHashBytes == null)
            {
                _status = "mapa sin construir: no se puede conectar";
                return;
            }

            if (_offlineController != null)
            {
                // También al reconectar con R: nunca debe haber dos cosas moviendo el Transform.
                _offlineController.enabled = false;
            }

            _timeline.Clear();
            ClearRemoteViews();
            _prediction = null;
            _snapshotsReceived = 0;
            _inputsSent = 0;
            ClearInputHistory();
            _lastServerTick = 0;
            _lastProcessedSequence = 0;
            _entityId = 0;
            _ownHealthPercent = 100;
            _ownDead = false;
            _inventoryIds = new ushort[0];
            _gold = 0;
            _zones = new ZoneInfo[0];
            _zoneId = 0;
            _zoneKind = ZoneKind.Normal;
            _skillCatalog = default;
            _skillCatalogReceived = false;
            _ownStatusCount = 0;
            _statusMasks.Clear();
            _resource = default;
            _resourceReceived = false;
            _skillReadyAt.Clear();
            Array.Clear(_hotbar, 0, _hotbar.Length);
            _shortcutDrag = default;
            _shortcutDragging = false;
            _shortcutDragFrom = -1;
            _showSkills = false;
            _pvpStatus = default;
            _pvpStatusReceived = false;
            _minimapDirty = true;
            _inventoryCounts = new ushort[0];
            System.Array.Clear(_equipment, 0, _equipment.Length);
            _catalog.Clear();
            _catalogReceived = false;
            _accumulator = 0;
            _lastReject = null;
            _characterList = default;
            _characterListReceived = false;
            _selectionActive = false;
            _characterOperationPending = false;
            _characterSelectionPending = false;
            _confirmDelete = false;
            _selectedCharacterId = 0;
            _selectedClassId = 0;
            _createClassId = 0;
            _welcomeReceived = false;
            _disconnectRequested = false;
            _challengeReceived = false;
            _challengeNonce = null;
            _clientToken = (uint)UnityEngine.Random.Range(1, int.MaxValue);
            _connectStartedAt = Time.unscaledTimeAsDouble;
            _connecting = true;
            _status = "conectando a " + host + ":" + port;

            var transport = new LiteNetLibClientTransport();
            transport.Connected += OnTransportConnected;
            transport.Disconnected += OnTransportDisconnected;
            transport.Received += OnMessageReceived;
            _transport = transport;

            try
            {
                transport.Connect(host, port);
            }
            catch (Exception exception)
            {
                _status = "error al abrir el socket: " + exception.Message;
                _connecting = false;
                CleanupTransport();
            }
        }

        private void Update()
        {
            if (_transport != null)
            {
                _transport.Poll();

                // Cerrar el socket dentro de un evento de LiteNetLib sería tocar la colección que
                // está recorriendo; se hace al volver de Poll.
                if (_disconnectRequested)
                {
                    _disconnectRequested = false;
                    CleanupTransport();
                }
            }

            UpdateCombatProjectiles();

            if (!_welcomeReceived)
            {
                if (_transport == null &&
                    Keyboard.current != null &&
                    Keyboard.current.rKey.wasPressedThisFrame)
                {
                    Connect();
                }

                if (_connecting &&
                    Time.unscaledTimeAsDouble - _connectStartedAt > welcomeTimeoutSeconds)
                {
                    _status = "sin respuesta del servidor (tiempo de espera)";
                    _connecting = false;
                    CleanupTransport();
                }

                return;
            }

            // Muerto (I-03b): ni ataques ni movimiento. Se siguen mandando inputs a cero para que la
            // secuencia avance igual que en el servidor, que los consume sin moverlo.
            // Con la caja de chat abierta (I-07b) las teclas son texto: ni combate, ni objetos, ni
            // inventario, ni movimiento.
            if (!_ownDead && _catalogReceived && !_chatOpen)
            {
                HandleCombatInput();
                HandleItemInput();
            }

            if (!_chatOpen && Keyboard.current != null && Keyboard.current.iKey.wasPressedThisFrame)
            {
                _showInventory = !_showInventory;
            }

            if (!_chatOpen && Keyboard.current != null && Keyboard.current.cKey.wasPressedThisFrame)
            {
                _showStats = !_showStats;
            }

            if (!_chatOpen && Keyboard.current != null && Keyboard.current.kKey.wasPressedThisFrame)
            {
                _showSkills = !_showSkills;
            }

            double frame = Time.deltaTime;
            if (frame > MaxFrameSeconds)
            {
                frame = MaxFrameSeconds;
            }
            else if (frame < 0d)
            {
                frame = 0d;
            }

            _accumulator += frame;

            ReadDirection(out sbyte directionX, out sbyte directionY);

            if (_chatOpen)
            {
                directionX = 0;
                directionY = 0;
            }

            if (_ownDead)
            {
                directionX = 0;
                directionY = 0;
                _autoAttack = false;
            }
            else if (directionX != 0 || directionY != 0)
            {
                // Moverse a mano cancela el ataque automático.
                _autoAttack = false;
            }
            else if (_autoAttack)
            {
                ApproachTarget(out directionX, out directionY);
            }

            while (_accumulator >= SimulationConstants.FixedDt)
            {
                uint sequence = _prediction.AddInput(directionX, directionY);
                SendMoveInput(sequence, directionX, directionY);

                if (directionX != 0)
                {
                    _lastDirectionX = directionX;
                }

                _accumulator -= SimulationConstants.FixedDt;
            }

            _prediction.AdvanceVisual((float)frame);
            RenderLocal();
            RenderRemotes(Time.unscaledTimeAsDouble);
        }

        private void ReadDirection(out sbyte directionX, out sbyte directionY)
        {
            if (useDebugInput)
            {
                directionX = (sbyte)Mathf.Clamp(debugDirX, -1, 1);
                directionY = (sbyte)Mathf.Clamp(debugDirY, -1, 1);
                return;
            }

            KeyboardDirection.Read(out directionX, out directionY);
        }

        private void SendMoveInput(uint sequence, sbyte directionX, sbyte directionY)
        {
            if (_transport == null)
            {
                return;
            }

            // La dirección nueva es la más reciente: el códec espera dir0 = sequence y dir_i =
            // sequence - i.
            if (_inputHistoryCount < ProtocolConstants.MaxInputsPerMessage)
            {
                _inputHistoryCount++;
            }

            for (int i = _inputHistoryCount - 1; i > 0; i--)
            {
                _inputHistoryX[i] = _inputHistoryX[i - 1];
                _inputHistoryY[i] = _inputHistoryY[i - 1];
            }

            _inputHistoryX[0] = directionX;
            _inputHistoryY[0] = directionY;

            // Las secuencias empiezan en 1 y el códec rechaza sequence < n; así también se cubre la
            // vuelta del uint.
            int count = _inputHistoryCount;
            if ((uint)count > sequence)
            {
                count = (int)sequence;
            }

            // El canal Sequenced descarta lo que llega desordenado (G-02): repetir los inputs
            // anteriores rellena los huecos que deja un datagrama perdido.
            byte[] bytes = MessageCodec.EncodeMoveInput(new MoveInputMessage(
                sequence,
                count,
                _inputHistoryX[0],
                _inputHistoryY[0],
                _inputHistoryX[1],
                _inputHistoryY[1],
                _inputHistoryX[2],
                _inputHistoryY[2],
                _inputHistoryX[3],
                _inputHistoryY[3]));

            _transport.Send(bytes, bytes.Length, DeliveryMode.Sequenced);
            _inputsSent++;
        }

        /// <summary>
        /// Clic izquierdo selecciona el PNJ vivo bajo el ratón (o quita la selección); Espacio
        /// pide atacarlo (I-02b). Doble clic en un PNJ lo persigue y lo ataca solo hasta que muere,
        /// o hasta moverse, pulsar Esc o elegir otro objetivo (I-06c). El servidor valida todo: aquí
        /// solo se evita mandar lo que seguro rechazaría.
        /// </summary>
        private void HandleCombatInput()
        {
            Mouse mouse = Mouse.current;
            Camera cam = Camera.main;
            Keyboard keyboard = Keyboard.current;

            // Un clic sobre un panel es para la interfaz, no para elegir objetivo.
            bool overPanel = false;
            if (mouse != null)
            {
                var gui = new Vector2(mouse.position.ReadValue().x, Screen.height - mouse.position.ReadValue().y);
                overPanel = (_showStats && _statsPanel.Contains(gui)) ||
                    (_showInventory && _inventoryPanel.Contains(gui)) ||
                    (_showSkills && _skillsPanel.Contains(gui)) ||
                    _hotbarPanel.Contains(gui) ||
                    (_discardSlot >= 0 && _discardDialog.Contains(gui)) ||
                    _pvpButtonRect.Contains(gui);
            }

            if (mouse != null && cam != null && mouse.leftButton.wasPressedThisFrame && !overPanel)
            {
                Vector2 screen = mouse.position.ReadValue();
                Vector3 world = cam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, -cam.transform.position.z));
                int picked = PickEntityAt(world, EntityKind.Npc, PickRadius);
                if (picked == 0)
                {
                    picked = PickPvpPlayerAt(world, PickRadius);
                }

                bool doubleClick = picked != 0 && picked == _lastClickId &&
                    Time.unscaledTime - _lastClickAt <= DoubleClickSeconds;
                _lastClickId = picked;
                _lastClickAt = Time.unscaledTime;

                if (picked != _selectedNpcId)
                {
                    _autoAttack = false;
                }

                Select(picked);
                _autoAttack |= doubleClick;
            }

            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            {
                _autoAttack = false;
            }

            RemotePlayerView target = SelectedTarget();
            if (target == null && _selectedNpcId != 0)
            {
                Select(0);
            }

            if (target == null)
            {
                _autoAttack = false;
            }

            bool wantsAttack = _autoAttack || (keyboard != null && keyboard.spaceKey.isPressed);
            if (!wantsAttack || Time.unscaledTime < _nextAttackAt)
            {
                return;
            }

            if (target == null)
            {
                ShowHint("sin objetivo: haz clic en un enemigo o jugador PvP");
                return;
            }

            Vector2 delta = (Vector2)(target.transform.position - transform.position);
            CurrentWeapon(out float attackRange, out float attackCooldown);
            float baseTicks = Mathf.Ceil(attackCooldown * SimulationConstants.TickRate - 0.001f);
            float hasteTicks = Mathf.Ceil(baseTicks * 10000f / (10000f + _stats.Haste) - 0.001f);
            attackCooldown = Mathf.Max(MinAttackTicks, hasteTicks) / SimulationConstants.TickRate;
            if (delta.sqrMagnitude > attackRange * attackRange)
            {
                // En automático no es un error: ApproachTarget ya está caminando hacia él.
                if (!_autoAttack)
                {
                    ShowHint("fuera de alcance");
                }

                return;
            }

            if (!HasLineOfSightTo(target))
            {
                if (!_autoAttack)
                {
                    ShowHint("sin línea de visión");
                }

                return;
            }

            byte[] bytes = MessageCodec.EncodeAttack(new AttackMessage(target.EntityId));
            _transport.Send(bytes, bytes.Length, DeliveryMode.ReliableOrdered);
            _attacksSent++;
            _nextAttackAt = Time.unscaledTime + attackCooldown;
        }

        /// <summary>
        /// E recoge el objeto del suelo más cercano a alcance; Q bebe la primera poción (I-04b). El
        /// servidor valida todo: aquí solo se evita mandar lo que seguro rechazaría.
        /// </summary>
        private void HandleItemInput()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null || _transport == null)
            {
                return;
            }

            if (keyboard.pKey.wasPressedThisFrame && _pvpStatus.Change != PvpChangeState.Unavailable)
            {
                RequestPvpModeToggle();
            }

            if (keyboard.eKey.wasPressedThisFrame)
            {
                int item = PickEntityAt(transform.position, EntityKind.Item, _pickupRange);
                if (item == 0)
                {
                    ShowHint("nada que recoger");
                }
                else
                {
                    byte[] bytes = MessageCodec.EncodePickUp(new PickUpMessage(item));
                    _transport.Send(bytes, bytes.Length, DeliveryMode.ReliableOrdered);
                }
            }

            if (keyboard.qKey.wasPressedThisFrame)
            {
                int slot = FirstSlotOfKind(ItemKind.Potion);
                if (slot < 0)
                {
                    ShowHint("sin pociones");
                }
                else
                {
                    UseSlot(slot);
                }
            }

            // 1–9 y 0: hueco n de la barra: objeto o habilidad (I-16b).
            for (int n = 0; n < 9; n++)
            {
                if (keyboard[(Key)((int)Key.Digit1 + n)].wasPressedThisFrame)
                {
                    PressHotbar(n);
                }
            }

            // El 0 es el décimo hueco de la barra rápida.
            if (keyboard.digit0Key.wasPressedThisFrame)
            {
                PressHotbar(9);
            }

            if (keyboard.uKey.wasPressedThisFrame)
            {
                if (_equipment[(int)EquipSlot.Weapon] == 0)
                {
                    ShowHint("sin arma equipada");
                }
                else
                {
                    SendUnequip((int)EquipSlot.Weapon, byte.MaxValue);
                }
            }
        }

        private void SendUnequip(int equipSlot, int targetSlot)
        {
            if (targetSlot != byte.MaxValue && targetSlot >= 0 && targetSlot < _inventoryIds.Length &&
                _catalog.TryGetValue(_inventoryIds[targetSlot], out ItemCatalogEntry item) &&
                (item.Kind == ItemKind.Weapon || item.Kind == ItemKind.Armor) &&
                !CanEquipHere(targetSlot, out string reason))
            {
                ShowHint(reason);
                return;
            }

            byte[] bytes = MessageCodec.EncodeUnequip(new UnequipMessage((byte)equipSlot, (byte)targetSlot));
            _transport.Send(bytes, bytes.Length, DeliveryMode.ReliableOrdered);
        }

        private void SendMoveItem(int from, int to, int count)
        {
            byte[] bytes = MessageCodec.EncodeMoveItem(new MoveItemMessage((byte)from, (byte)to, (ushort)count));
            _transport.Send(bytes, bytes.Length, DeliveryMode.ReliableOrdered);
        }

        private void PlaceHotbarShortcut(int slot, int from, HotbarShortcut shortcut)
        {
            HotbarShortcut previous = _hotbar[slot];
            _hotbar[slot] = shortcut;
            if (from >= 0) _hotbar[from] = previous;
            SaveHotbar();
        }

        private bool TryGetSkill(ushort id, out SkillCatalogEntry skill)
        {
            SkillCatalogEntry[] skills = _skillCatalog.Skills;
            if (skills != null)
            {
                for (int i = 0; i < skills.Length; i++)
                {
                    if (skills[i].Id == id)
                    {
                        skill = skills[i];
                        return true;
                    }
                }
            }

            skill = default;
            return false;
        }

        private float SkillSecondsLeft(ushort id)
        {
            return _skillReadyAt.TryGetValue(id, out float readyAt)
                ? Mathf.Max(0f, readyAt - Time.unscaledTime) : 0f;
        }

        private void LoadHotbar()
        {
            if (_selectedCharacterId == 0) return;
            for (int i = 0; i < _hotbar.Length; i++)
            {
                string key = "avelora.hotbar." + _selectedCharacterId + "." + i;
                // El cero guardado evita volver a importar una habilidad retirada.
                if (!PlayerPrefs.HasKey(key))
                {
                    int oldId = PlayerPrefs.GetInt("avelora.skillbar." + _selectedCharacterId + "." + i, 0);
                    if (oldId > 0 && oldId <= ushort.MaxValue)
                        _hotbar[i] = new HotbarShortcut(ShortcutKind.Skill, (ushort)oldId);
                    continue;
                }
                int value = PlayerPrefs.GetInt(key, 0);
                int kind = value >> 16;
                ushort id = (ushort)(value & 0xffff);
                _hotbar[i] = id != 0 && (kind == (int)ShortcutKind.Item || kind == (int)ShortcutKind.Skill)
                    ? new HotbarShortcut((ShortcutKind)kind, id) : default;
            }
            SaveHotbar();
        }

        private void SaveHotbar()
        {
            if (_selectedCharacterId == 0) return;
            for (int i = 0; i < _hotbar.Length; i++)
            {
                PlayerPrefs.SetInt("avelora.hotbar." + _selectedCharacterId + "." + i,
                    ((int)_hotbar[i].Kind << 16) | _hotbar[i].Id);
                PlayerPrefs.DeleteKey("avelora.skillbar." + _selectedCharacterId + "." + i);
            }
            PlayerPrefs.Save();
        }

        private int ResourceCurrent => _resourceReceived ? _resource.Current : _stats.Resource;
        private int ResourceMax => _resourceReceived ? _resource.Max : _stats.MaxResource;

        private void PressHotbar(int n)
        {
            HotbarShortcut shortcut = _hotbar[n];
            if (shortcut.Kind == ShortcutKind.Skill && TryGetSkill(shortcut.Id, out SkillCatalogEntry skill))
                TryUseSkill(skill, SelectedTarget());
            else if (shortcut.Kind == ShortcutKind.Item)
            {
                for (int i = 0; i < _inventoryIds.Length; i++)
                {
                    if (_inventoryIds[i] != shortcut.Id || _inventoryCounts[i] == 0) continue;
                    UseSlot(i);
                    return;
                }
                ShowHint("No te quedan");
            }
        }

        private int HotbarItemCount(ushort id)
        {
            int count = 0;
            for (int i = 0; i < _inventoryIds.Length; i++)
                if (_inventoryIds[i] == id) count += _inventoryCounts[i];
            return count;
        }

        private void TryUseSkill(SkillCatalogEntry skill, RemotePlayerView target)
        {
            if (_transport == null) return;
            if (_level < skill.RequiredLevel)
            {
                ShowHint("nivel insuficiente");
                return;
            }
            if (skill.AreaCenter == 1 || (skill.Effect == 1 && skill.Radius > 0f))
            {
                if (!SkillReady(skill)) return;
                byte[] selfBytes = MessageCodec.EncodeUseSkill(new UseSkillMessage(skill.Id, _entityId));
                _transport.Send(selfBytes, selfBytes.Length, DeliveryMode.ReliableOrdered);
                return;
            }
            if (IsFriendly(skill) && (target == null || target.Kind != EntityKind.Player))
            {
                if (!SkillReady(skill)) return;
                byte[] selfBytes = MessageCodec.EncodeUseSkill(new UseSkillMessage(skill.Id, _entityId));
                _transport.Send(selfBytes, selfBytes.Length, DeliveryMode.ReliableOrdered);
                return;
            }
            if (target == null)
            {
                ShowHint("sin objetivo: haz clic en un enemigo o jugador PvP");
                return;
            }
            if (skill.StatusKind == StatusKind.Taunt && target.Kind != EntityKind.Npc)
            {
                ShowHint("provocar solo vale con enemigos");
                return;
            }
            if (!SkillReady(skill)) return;
            Vector2 delta = (Vector2)(target.transform.position - transform.position);
            if (delta.sqrMagnitude > skill.Range * skill.Range)
            {
                ShowHint("fuera de alcance");
                return;
            }
            if (!HasLineOfSightTo(target))
            {
                ShowHint("sin línea de visión");
                return;
            }
            byte[] bytes = MessageCodec.EncodeUseSkill(new UseSkillMessage(skill.Id, target.EntityId));
            _transport.Send(bytes, bytes.Length, DeliveryMode.ReliableOrdered);
        }

        private static bool IsFriendly(SkillCatalogEntry skill) => skill.AreaCenter == 1 || skill.Effect == 1 ||
            skill.StatusKind == StatusKind.Bonus || skill.StatusKind == StatusKind.Shield;

        private bool SkillReady(SkillCatalogEntry skill)
        {
            float seconds = SkillSecondsLeft(skill.Id);
            if (seconds > 0f)
            {
                ShowHint("recarga: " + Mathf.CeilToInt(seconds) + " s");
                return false;
            }
            if (ResourceCurrent < skill.ResourceCost)
            {
                ShowHint("sin " + _skillCatalog.ResourceName + " suficiente");
                return false;
            }
            return true;
        }

        private string SkillRejectText(SkillRejectReason reason)
        {
            switch (reason)
            {
                case SkillRejectReason.UnknownSkill: return "habilidad desconocida";
                case SkillRejectReason.WrongClass: return "habilidad de otra clase";
                case SkillRejectReason.LevelTooLow: return "nivel insuficiente";
                case SkillRejectReason.Dead: return "estás muerto";
                case SkillRejectReason.InvalidTarget: return "objetivo no válido";
                case SkillRejectReason.SafeZone: return "zona segura: no se puede atacar";
                case SkillRejectReason.PvpNotAllowed: return "PvP no permitido con ese objetivo";
                case SkillRejectReason.OutOfRange: return "fuera de alcance";
                case SkillRejectReason.NoLineOfSight: return "sin línea de visión";
                case SkillRejectReason.Cooldown: return "habilidad en recarga";
                case SkillRejectReason.NotEnoughResource: return "sin " + _skillCatalog.ResourceName + " suficiente";
                case SkillRejectReason.FullHealth: return "ya tiene la vida llena";
                default: return "habilidad rechazada";
            }
        }

        /// <summary>Equipa el arma o armadura del hueco o bebe su poción; el servidor valida.</summary>
        private void UseSlot(int slot)
        {
            if (slot >= _inventoryIds.Length || !_catalog.TryGetValue(_inventoryIds[slot], out ItemCatalogEntry item))
            {
                ShowHint("hueco vacío");
                return;
            }

            byte[] bytes;
            if (item.Kind == ItemKind.Weapon || item.Kind == ItemKind.Armor)
            {
                if (!CanEquipHere(slot, out string reason))
                {
                    ShowHint(reason);
                    return;
                }

                bytes = MessageCodec.EncodeEquip(new EquipMessage((byte)slot));
            }
            else if (item.Kind == ItemKind.Potion)
            {
                if (_ownHealthPercent >= 100)
                {
                    ShowHint("vida llena");
                    return;
                }

                bytes = MessageCodec.EncodeUseItem(new UseItemMessage((byte)slot));
            }
            else
            {
                ShowHint(item.Name + " no se usa");
                return;
            }

            _transport.Send(bytes, bytes.Length, DeliveryMode.ReliableOrdered);
        }

        private int FirstSlotOfKind(ItemKind kind)
        {
            for (int i = 0; i < _inventoryIds.Length; i++)
            {
                if (_catalog.TryGetValue(_inventoryIds[i], out ItemCatalogEntry item) && item.Kind == kind)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Dirección hacia el objetivo del ataque automático (I-06c), o cero si ya está a alcance y
        /// hay línea de visión. Va por el MoveInput de siempre: el servidor mueve y valida igual que
        /// con el teclado.
        /// </summary>
        // shortcut: camina en línea recta y se queda contra la pared o detrás de una pared sin vista; añadir
        // búsqueda de caminos sobre la rejilla del mapa si en las salas grandes molesta.
        private void ApproachTarget(out sbyte directionX, out sbyte directionY)
        {
            directionX = 0;
            directionY = 0;

            RemotePlayerView target = SelectedTarget();
            if (target == null)
            {
                return;
            }

            Vector2 delta = (Vector2)(target.transform.position - transform.position);
            CurrentWeapon(out float range, out _);
            float stop = range * ApproachFraction;
            if (delta.sqrMagnitude <= stop * stop && HasLineOfSightTo(target))
            {
                return;
            }

            if (Mathf.Abs(delta.x) > AxisDeadZone)
            {
                directionX = (sbyte)(delta.x > 0f ? 1 : -1);
            }

            if (Mathf.Abs(delta.y) > AxisDeadZone)
            {
                directionY = (sbyte)(delta.y > 0f ? 1 : -1);
            }
        }

        private bool HasLineOfSightTo(RemotePlayerView target)
        {
            return _map == null || _map.Grid == null || _map.Grid.HasLineOfSight(
                new Vec2(transform.position.x, transform.position.y),
                new Vec2(target.transform.position.x, target.transform.position.y));
        }

        /// <summary>Alcance y recarga del arma equipada, o de los puños si no hay (catálogo del servidor).</summary>
        private void CurrentWeapon(out float range, out float cooldownSeconds)
        {
            if (_catalog.TryGetValue(_equipment[(int)EquipSlot.Weapon], out ItemCatalogEntry weapon) && weapon.Kind == ItemKind.Weapon)
            {
                range = weapon.Range;
                cooldownSeconds = weapon.CooldownSeconds;
                return;
            }

            range = _fistRange;
            cooldownSeconds = _fistCooldownSeconds;
        }

        private int PickEntityAt(Vector3 world, EntityKind kind, float radius)
        {
            int best = 0;
            float bestDistance = radius * radius;

            foreach (KeyValuePair<int, RemotePlayerView> pair in _remoteViews)
            {
                RemotePlayerView view = pair.Value;
                if (view == null || view.Kind != kind || view.IsDead)
                {
                    continue;
                }

                Vector2 delta = (Vector2)(view.transform.position - world);
                float distance = delta.sqrMagnitude;
                if (distance <= bestDistance)
                {
                    best = pair.Key;
                    bestDistance = distance;
                }
            }

            return best;
        }

        private int PickPvpPlayerAt(Vector3 world, float radius)
        {
            int best = 0;
            float bestDistance = radius * radius;

            foreach (KeyValuePair<int, RemotePlayerView> pair in _remoteViews)
            {
                RemotePlayerView view = pair.Value;
                if (pair.Key == _entityId || view == null || view.Kind != EntityKind.Player || !view.Pvp || view.IsDead)
                {
                    continue;
                }

                Vector2 delta = (Vector2)(view.transform.position - world);
                float distance = delta.sqrMagnitude;
                if (distance <= bestDistance)
                {
                    best = pair.Key;
                    bestDistance = distance;
                }
            }

            return best;
        }

        private RemotePlayerView SelectedTarget()
        {
            if (_selectedNpcId == 0 ||
                !_remoteViews.TryGetValue(_selectedNpcId, out RemotePlayerView view) ||
                view == null || view.IsDead)
            {
                return null;
            }

            if (view.Kind == EntityKind.Player && !view.Pvp)
            {
                return null;
            }

            return view;
        }

        private void RequestPvpModeToggle()
        {
            if (_transport == null || !_pvpStatusReceived)
            {
                return;
            }

            PvpMode mode = _pvpStatus.Mode == PvpMode.Pvp ? PvpMode.Pve : PvpMode.Pvp;
            byte[] bytes = MessageCodec.EncodeSetPvpMode(new SetPvpModeMessage(mode));
            _transport.Send(bytes, bytes.Length, DeliveryMode.ReliableOrdered);
        }

        private void Select(int entityId)
        {
            if (_selectedNpcId != 0 &&
                _remoteViews.TryGetValue(_selectedNpcId, out RemotePlayerView previous) &&
                previous != null)
            {
                previous.SetSelected(false);
            }

            _selectedNpcId = entityId;

            if (entityId != 0 && _remoteViews.TryGetValue(entityId, out RemotePlayerView next) && next != null)
            {
                next.SetSelected(true);
            }
        }

        private void ShowHint(string hint)
        {
            _combatHint = hint;
            _combatHintUntil = Time.unscaledTime + HintSeconds;
        }

        /// <summary>Vacía el historial al empezar una conexión: no se mezclan secuencias de otra sesión.</summary>
        private void ClearInputHistory()
        {
            _inputHistoryCount = 0;

            for (int i = 0; i < _inputHistoryX.Length; i++)
            {
                _inputHistoryX[i] = 0;
                _inputHistoryY[i] = 0;
            }
        }

        private void OnTransportConnected()
        {
            if (_transport == null || _map == null || _map.ContentHashBytes == null)
            {
                _status = "conexión sin huella de mapa";
                _disconnectRequested = true;
                return;
            }

            // Protocolo v4: el servidor manda primero un Challenge con su clave pública y un nonce.
            // Hasta recibirlo no se puede cifrar el saludo.
            _challengeReceived = false;
            _challengeNonce = null;
            _status = "esperando desafío...";
        }

        /// <summary>
        /// Comprueba la huella del servidor (si está configurada) y manda el saludo con las
        /// credenciales cifradas y el nonce de esta conexión.
        /// </summary>
        private void HandleChallenge(ChallengeMessage challenge)
        {
            if (_transport == null || _challengeReceived)
            {
                return;
            }

            _serverPublicKey = HelloSecrets.ToPublicKey(challenge.Modulus, challenge.Exponent);
            string fingerprint = HelloSecrets.Fingerprint(_serverPublicKey);
            string expected = serverKeyFingerprint == null ? string.Empty : serverKeyFingerprint.Trim();

            if (expected.Length > 0)
            {
                if (!string.Equals(expected, fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    // Solo se corta aquí: es la única forma de no hablar con un servidor impostor.
                    _status = "la clave del servidor no coincide";
                    _disconnectRequested = true;
                    return;
                }
            }
            else if (!_fingerprintWarned)
            {
                _fingerprintWarned = true;
                Debug.LogWarning(
                    "[Avelora] serverKeyFingerprint está vacío: se acepta el servidor con huella " +
                    fingerprint + " (modo desarrollo).",
                    this);
            }

            _challengeNonce = challenge.Nonce;
            _challengeReceived = true;
            SendHello();
        }

        private void SendHello()
        {
            if (_transport == null || _map == null || _map.ContentHashBytes == null || _challengeNonce == null)
            {
                return;
            }

            string account = accountName == null ? string.Empty : accountName.Trim();

            byte[] encrypted = HelloSecrets.Encrypt(
                _serverPublicKey,
                _challengeNonce,
                password,
                invitationCode);

            var hello = new HelloMessage(
                ProtocolConstants.Version,
                (ushort)SimulationConstants.TickRate,
                _map.ContentHashBytes,
                _clientToken,
                account,
                encrypted);

            byte[] bytes = MessageCodec.EncodeHello(hello);
            _transport.Send(bytes, bytes.Length, DeliveryMode.ReliableOrdered);
            _status = "saludando...";
        }

        private void OnTransportDisconnected(DisconnectReason reason)
        {
            _connecting = false;
            _welcomeReceived = false;
            _selectionActive = false;
            _characterListReceived = false;
            _characterOperationPending = false;
            _characterSelectionPending = false;
            _disconnectRequested = true;
            _challengeReceived = false;
            _challengeNonce = null;

            if (_lastReject.HasValue)
            {
                _status = "rechazado: " + DescribeReject(_lastReject.Value);
            }
            else
            {
                _status = "desconectado: " + DescribeDisconnect(reason);
            }
        }

        private void OnMessageReceived(int connectionId, byte[] buffer, int offset, int length)
        {
            if (length < 1)
            {
                return;
            }

            var type = (MessageType)buffer[offset];

            switch (type)
            {
                case MessageType.Challenge:
                    if (MessageCodec.TryReadChallenge(buffer, offset, length, out ChallengeMessage challenge))
                    {
                        HandleChallenge(challenge);
                    }

                    break;

                case MessageType.Welcome:
                    if (MessageCodec.TryReadWelcome(buffer, offset, length, out WelcomeMessage welcome))
                    {
                        HandleWelcome(welcome);
                    }

                    break;

                case MessageType.CharacterList:
                    if (!_welcomeReceived &&
                        MessageCodec.TryReadCharacterList(buffer, offset, length, out CharacterListMessage characterList))
                    {
                        HandleCharacterList(characterList);
                    }

                    break;

                case MessageType.CharacterRejected:
                    if (!_welcomeReceived &&
                        MessageCodec.TryReadCharacterRejected(buffer, offset, length, out CharacterRejectedMessage characterRejected))
                    {
                        _characterOperationPending = false;
                        _characterSelectionPending = false;
                        _status = DescribeCharacterReject(characterRejected.Reason);
                    }

                    break;

                case MessageType.Reject:
                    if (MessageCodec.TryReadReject(buffer, offset, length, out RejectMessage reject))
                    {
                        _lastReject = reject.Reason;
                        _status = "rechazado: " + DescribeReject(reject.Reason);
                        _disconnectRequested = true;
                    }

                    break;

                case MessageType.Inventory:
                    if (_welcomeReceived &&
                        MessageCodec.TryReadInventory(buffer, offset, length, out InventoryMessage inventory))
                    {
                        _inventoryIds = inventory.ItemIds;
                        _inventoryCounts = inventory.Counts;
                        _equipment = inventory.Equipment;
                        _gold = inventory.Gold;
                    }

                    break;

                case MessageType.Chat:
                    if (_welcomeReceived &&
                        MessageCodec.TryReadChat(buffer, offset, length, out ChatMessage chat))
                    {
                        _chatLog.Add(chat.SenderName + ": " + chat.Text);
                        if (_chatLog.Count > ChatLogLines)
                        {
                            _chatLog.RemoveAt(0);
                        }
                    }

                    break;

                case MessageType.Progress:
                    if (_welcomeReceived &&
                        MessageCodec.TryReadProgress(buffer, offset, length, out ProgressMessage progress))
                    {
                        if (_level != 0 && progress.Level > _level)
                        {
                            ShowHint("¡Subes al nivel " + progress.Level + "!");
                        }

                        _level = progress.Level;
                        _experience = progress.Experience;
                        _experienceToNext = progress.ExperienceToNext;
                        _maxHealth = progress.MaxHealth;
                    }

                    break;

                case MessageType.Stats:
                    if (_welcomeReceived &&
                        MessageCodec.TryReadStats(buffer, offset, length, out StatsMessage stats))
                    {
                        if (_statsReceived && stats.FreePoints > _stats.FreePoints)
                        {
                            ShowHint("Puntos de atributo libres: " + stats.FreePoints + " (C)");
                        }

                        _stats = stats;
                        _statsReceived = true;

                        // Repartir VIT o reajustar cambia la vida máxima sin subir de nivel (sin Progress).
                        _maxHealth = stats.MaxHealth;
                        System.Array.Clear(_pendingPoints, 0, _pendingPoints.Length);
                    }

                    break;

                case MessageType.ItemCatalog:
                    if (_welcomeReceived &&
                        MessageCodec.TryReadItemCatalog(buffer, offset, length, out ItemCatalogMessage catalog))
                    {
                        // El catálogo llega en trozos (I-21): cada uno añade; se vació al conectar.
                        for (int i = 0; i < catalog.Items.Length; i++)
                        {
                            _catalog[catalog.Items[i].Id] = catalog.Items[i];
                        }

                        _pickupRange = catalog.PickupRange;
                        _fistRange = catalog.FistRange;
                        _fistCooldownSeconds = catalog.FistCooldownSeconds;
                        _catalogReceived = true;
                    }

                    break;

                case MessageType.Snapshot:
                    if (_welcomeReceived &&
                        MessageCodec.TryReadSnapshot(buffer, offset, length, out SnapshotMessage snapshot))
                    {
                        HandleSnapshot(snapshot);
                    }

                    break;

                case MessageType.CombatEvent:
                    if (_welcomeReceived &&
                        MessageCodec.TryReadCombatEvent(buffer, offset, length, out CombatEventMessage combatEvent))
                    {
                        HandleCombatEvent(combatEvent);
                    }

                    break;

                case MessageType.LootGained:
                    if (_welcomeReceived &&
                        MessageCodec.TryReadLootGained(buffer, offset, length, out LootGainedMessage loot))
                    {
                        HandleLootGained(loot);
                    }

                    break;

                case MessageType.ZoneList:
                    if (_welcomeReceived &&
                        MessageCodec.TryReadZoneList(buffer, offset, length, out ZoneListMessage zoneList))
                    {
                        _zones = zoneList.Zones;
                        _minimapDirty = true;
                    }

                    break;

                case MessageType.ZoneChanged:
                    if (_welcomeReceived &&
                        MessageCodec.TryReadZoneChanged(buffer, offset, length, out ZoneChangedMessage zoneChanged))
                    {
                        _zoneId = zoneChanged.ZoneId;
                        _zoneKind = zoneChanged.Kind;
                    }

                    break;

                case MessageType.PvpStatus:
                    if (_welcomeReceived &&
                        MessageCodec.TryReadPvpStatus(buffer, offset, length, out PvpStatusMessage pvpStatus))
                    {
                        _pvpStatus = pvpStatus;
                        _pvpStatusReceived = true;
                        _pvpChangeReadyAt = Time.unscaledTime + pvpStatus.ChangeSeconds;
                        _pvpForcedEndsAt = Time.unscaledTime + pvpStatus.ForcedSeconds;
                    }

                    break;

                case MessageType.SkillCatalog:
                    if (_welcomeReceived && MessageCodec.TryReadSkillCatalog(buffer, offset, length, out SkillCatalogMessage skillCatalog))
                    {
                        _skillCatalog = skillCatalog;
                        _skillCatalogReceived = true;
                        RemoveMissingSkillShortcuts();
                    }
                    break;

                case MessageType.ResourceStatus:
                    if (_welcomeReceived && MessageCodec.TryReadResourceStatus(buffer, offset, length, out ResourceStatusMessage resource))
                    {
                        _resource = resource;
                        _resourceReceived = true;
                    }
                    break;

                case MessageType.StatusEffects:
                    if (_welcomeReceived && MessageCodec.TryReadStatusEffects(buffer, offset, length, out StatusEffectsMessage statusEffects))
                    {
                        StatusEffectEntry[] effects = statusEffects.Effects;
                        _ownStatusCount = Mathf.Min(effects.Length, _ownStatuses.Length);
                        Array.Copy(effects, _ownStatuses, _ownStatusCount);
                        _ownStatusReceivedAt = Time.unscaledTime;
                        if (_prediction != null)
                        {
                            _prediction.SetSpeed(statusEffects.SpeedPercent == 100
                                ? _welcomeSpeed
                                : _welcomeSpeed * statusEffects.SpeedPercent / 100f);
                        }
                    }
                    break;

                case MessageType.StatusEvent:
                    if (_welcomeReceived && MessageCodec.TryReadStatusEvent(buffer, offset, length, out StatusEventMessage statusEvent))
                    {
                        StatusEventEntry[] entries = statusEvent.Entries;
                        for (int i = 0; i < entries.Length; i++)
                        {
                            StatusEventEntry statusEntry = entries[i];
                            if (statusEntry.Mask == 0) _statusMasks.Remove(statusEntry.EntityId);
                            else _statusMasks[statusEntry.EntityId] = statusEntry.Mask;
                        }
                    }
                    break;

                case MessageType.SkillUsed:
                    if (_welcomeReceived && MessageCodec.TryReadSkillUsed(buffer, offset, length, out SkillUsedMessage skillUsed) &&
                        TryGetSkill(skillUsed.SkillId, out SkillCatalogEntry entry))
                    {
                        _skillReadyAt[entry.Id] = Time.unscaledTime + entry.CooldownTicks / (float)SimulationConstants.TickRate;
                        if (entry.Radius > 0f && TryGetEntityPosition(skillUsed.TargetId, out Vector3 center))
                        {
                            if (_areaFlashes.Count == 8) _areaFlashes.RemoveAt(0);
                            _areaFlashes.Add(new AreaFlash
                            {
                                Center = center,
                                Radius = entry.Radius,
                                StartTime = Time.unscaledTime,
                                Color = entry.Effect == 1
                                    ? new Color(0.45f, 1f, 0.55f)
                                    : entry.Effect == 0 && entry.AreaCenter == 1
                                        ? new Color(1f, 0.35f, 0.3f)
                                        : new Color(1f, 0.55f, 0.2f),
                            });
                        }
                    }
                    break;

                case MessageType.SkillRejected:
                    if (_welcomeReceived && MessageCodec.TryReadSkillRejected(buffer, offset, length, out SkillRejectedMessage skillRejected))
                    {
                        ShowHint(SkillRejectText(skillRejected.Reason));
                    }
                    break;

                case MessageType.PvpModeRejected:
                    if (_welcomeReceived &&
                        MessageCodec.TryReadPvpModeRejected(buffer, offset, length, out PvpModeRejectedMessage rejected))
                    {
                        ShowHint(rejected.Reason == PvpModeRejectReason.NotInSafeZone
                            ? "el modo solo se cambia en zona segura"
                            : "aún no: espera en la zona segura");
                    }

                    break;
            }
        }

        private void HandleCombatEvent(CombatEventMessage combatEvent)
        {
            CombatEventEntry[] entries = combatEvent.Entries;
            if (entries == null)
            {
                return;
            }

            for (int i = 0; i < entries.Length; i++)
            {
                CombatEventEntry entry = entries[i];
                if (!TryGetEntityPosition(entry.TargetId, out Vector3 targetPosition))
                {
                    continue;
                }

                if ((entry.Flags & CombatEventFlags.Heal) == 0 &&
                    (entry.Flags & CombatEventFlags.Absorbed) == 0 &&
                    (entry.Flags & CombatEventFlags.Ranged) != 0 &&
                    TryGetEntityPosition(entry.AttackerId, out Vector3 attackerPosition))
                {
                    if (_projectiles.Count == 32)
                    {
                        _projectiles.RemoveAt(0);
                    }

                    float distance = Vector3.Distance(attackerPosition, targetPosition);
                    _projectiles.Add(new CombatProjectile
                    {
                        AttackerId = entry.AttackerId,
                        TargetId = entry.TargetId,
                        From = attackerPosition,
                        StartTime = Time.unscaledTime,
                        Duration = Mathf.Clamp(distance / 20f, 0.08f, 0.3f),
                        Damage = entry.Damage,
                        Flags = entry.Flags,
                    });
                }
                else
                {
                    AddDamageNumber(entry.TargetId, entry.Damage, entry.Flags, targetPosition);
                }
            }
        }

        private bool TryGetEntityPosition(int entityId, out Vector3 position)
        {
            if (entityId == _entityId)
            {
                position = transform.position;
                return true;
            }

            if (_remoteViews.TryGetValue(entityId, out RemotePlayerView view) && view != null)
            {
                position = view.transform.position;
                return true;
            }

            position = default;
            return false;
        }

        private void RemoveMissingSkillShortcuts()
        {
            bool changed = false;
            for (int i = 0; i < _hotbar.Length; i++)
            {
                HotbarShortcut shortcut = _hotbar[i];
                if (shortcut.Kind != ShortcutKind.Skill || TryGetSkill(shortcut.Id, out _)) continue;
                _hotbar[i] = default;
                changed = true;
            }

            if (changed) SaveHotbar();
        }

        private void UpdateCombatProjectiles()
        {
            for (int i = _projectiles.Count - 1; i >= 0; i--)
            {
                CombatProjectile projectile = _projectiles[i];
                if (!TryGetEntityPosition(projectile.TargetId, out Vector3 targetPosition))
                {
                    _projectiles.RemoveAt(i);
                    continue;
                }

                if (Time.unscaledTime - projectile.StartTime >= projectile.Duration)
                {
                    _projectiles.RemoveAt(i);
                    AddDamageNumber(projectile.TargetId, projectile.Damage, projectile.Flags, targetPosition);
                }
            }
        }

        private void AddDamageNumber(int targetId, ushort damage, CombatEventFlags flags, Vector3 position)
        {
            if (_damageNumbers.Count == 32)
            {
                _damageNumbers.RemoveAt(0);
            }

            _damageNumbers.Add(new DamageNumber
            {
                Position = position,
                StartTime = Time.unscaledTime,
                Text = (flags & CombatEventFlags.Heal) != 0 ? "+" + damage
                    : (flags & CombatEventFlags.Absorbed) != 0 ? "(" + damage + ")"
                    : damage.ToString() + ((flags & CombatEventFlags.Killed) != 0 ? "!" : string.Empty),
                OnSelf = targetId == _entityId,
                Heal = (flags & CombatEventFlags.Heal) != 0,
                Absorbed = (flags & CombatEventFlags.Absorbed) != 0,
                Critical = (flags & CombatEventFlags.Critical) != 0,
            });
        }

        private void HandleLootGained(LootGainedMessage loot)
        {
            float startTime = Time.unscaledTime;
            int line = 0;

            if (loot.Experience > 0)
            {
                AddLootText("+" + loot.Experience + " exp", new Color(0.6f, 0.85f, 1f), startTime, line++);
            }

            if (loot.Gold > 0)
            {
                AddLootText("+" + loot.Gold + " oro", new Color(1f, 0.85f, 0.25f), startTime, line++);
            }

            LootItemEntry[] items = loot.Items;
            for (int groundPass = 0; groundPass < 2; groundPass++)
            {
                bool onGround = groundPass == 1;
                for (int i = 0; i < items.Length; i++)
                {
                    LootItemEntry item = items[i];
                    if (item.OnGround != onGround)
                    {
                        continue;
                    }

                    string name = _catalog.TryGetValue(item.ItemId, out ItemCatalogEntry entry)
                        ? entry.Name
                        : "Objeto " + item.ItemId;
                    string count = item.Count > 1 ? " x" + item.Count : string.Empty;
                    string text = onGround
                        ? "Inventario lleno: " + name + count + " al suelo"
                        : name + count;
                    Color color = onGround ? new Color(1f, 0.45f, 0.35f) : Color.white;
                    AddLootText(text, color, startTime, line++);
                }
            }
        }

        private void AddLootText(string text, Color color, float startTime, int line)
        {
            if (_lootTexts.Count == 16)
            {
                _lootTexts.RemoveAt(0);
            }

            _lootTexts.Add(new LootText
            {
                Text = text,
                Color = color,
                StartTime = startTime,
                Line = line,
            });
        }

        private void HandleWelcome(WelcomeMessage welcome)
        {
            if (_welcomeReceived)
            {
                return;
            }

            if (welcome.ProtocolVersion != ProtocolConstants.Version)
            {
                RejectLocally(RejectReason.ProtocolVersion);
                return;
            }

            if (welcome.TickRate != SimulationConstants.TickRate)
            {
                RejectLocally(RejectReason.TickRate);
                return;
            }

            if (welcome.ClientToken != _clientToken)
            {
                _status = "saludo ajeno: token distinto";
                _disconnectRequested = true;
                return;
            }

            if (!MapHashMatches(welcome.MapHash))
            {
                RejectLocally(RejectReason.MapMismatch);
                return;
            }

            _entityId = welcome.EntityId;
            _welcomeSpeed = welcome.Speed;
            _connecting = false;
            _selectionActive = false;
            _characterSelectionPending = false;
            _lastReject = null;
            _accumulator = 0;
            _lastDirectionX = 0;
            _zones = new ZoneInfo[0];
            _zoneId = 0;
            _zoneKind = ZoneKind.Normal;
            _skillCatalog = default;
            _skillCatalogReceived = false;
            _ownStatusCount = 0;
            _statusMasks.Clear();
            _resource = default;
            _resourceReceived = false;
            _skillReadyAt.Clear();
            Array.Clear(_hotbar, 0, _hotbar.Length);
            _shortcutDrag = default;
            _shortcutDragging = false;
            _shortcutDragFrom = -1;
            _showSkills = false;
            LoadHotbar();
            _pvpStatus = default;
            _pvpStatusReceived = false;
            _minimapDirty = true;

            var halfSize = new Vec2(welcome.HalfWidth, welcome.HalfHeight);
            _prediction = new ClientPrediction(
                _map.Grid,
                halfSize,
                welcome.Speed,
                SimulationConstants.FixedDt);
            _prediction.Reset(new Vec2(welcome.SpawnX, welcome.SpawnY));
            ClearInputHistory();
            _welcomeReceived = true;

            if (_localRenderer != null)
            {
                Sprite classSprite = ClassSprite(_selectedClassId);
                _localRenderer.sprite = classSprite != null ? classSprite : _baseSprite;
                _localRenderer.color = classSprite != null ? Color.white : ClassColor(_selectedClassId);
            }

            transform.position = new Vector3(welcome.SpawnX, welcome.SpawnY, transform.position.z);
            _status = "conectado (entidad " + _entityId + ")";

            Debug.Log(
                "[Avelora] Conectado como entidad " + _entityId + " en (" +
                welcome.SpawnX + ", " + welcome.SpawnY + ") con huella " +
                _map.ContentHash + ".",
                this);
        }

        private void HandleSnapshot(SnapshotMessage snapshot)
        {
            if (!_timeline.Add(snapshot, Time.unscaledTimeAsDouble))
            {
                return;
            }

            _snapshotsReceived++;
            _lastServerTick = snapshot.ServerTick;

            EntityState[] entities = snapshot.Entities;
            if (entities == null)
            {
                return;
            }

            for (int i = 0; i < entities.Length; i++)
            {
                EntityState state = entities[i];

                if (state.Id != _entityId)
                {
                    continue;
                }

                var authoritative = new Vec2(
                    ProtocolConstants.DequantizePosition(state.X),
                    ProtocolConstants.DequantizePosition(state.Y));

                _lastAuthoritative = authoritative;
                _lastProcessedSequence = snapshot.LastProcessedInputSequence;
                _ownHealthPercent = state.HealthPercent;

                bool dead = state.Action == EntityAction.Dead;
                if (dead && !_ownDead)
                {
                    _ownDeathAt = Time.unscaledTime;
                    Select(0);
                }

                if (_prediction != null)
                {
                    // Al reaparecer el servidor ya lo puso en el spawn: Teleport y no Reset, que
                    // reiniciaría la secuencia y el servidor descartaría los inputs siguientes.
                    if (!dead && _ownDead)
                    {
                        _prediction.Teleport(authoritative);
                    }

                    _prediction.Reconcile(authoritative, snapshot.LastProcessedInputSequence);
                }

                _ownDead = dead;
                break;
            }
        }

        private void RenderLocal()
        {
            float alpha = (float)(_accumulator / SimulationConstants.FixedDt);
            Vec2 position = _prediction.Render(alpha);

            transform.position = new Vector3(position.X, position.Y, transform.position.z);

            Camera cam = Camera.main;
            if (cameraFollowsPlayer && cam != null)
            {
                cam.transform.position = new Vector3(position.X, position.Y, cam.transform.position.z);
            }

            if (_localRenderer != null && _lastDirectionX != 0)
            {
                _localRenderer.flipX = _lastDirectionX < 0;
            }
        }

        private void RenderRemotes(double now)
        {
            if (!_timeline.HasSnapshots)
            {
                return;
            }

            EntityState[] entities = _timeline.Latest.Entities;
            if (entities == null)
            {
                return;
            }

            double renderTime = now - interpolationDelay;
            _aliveIds.Clear();

            for (int i = 0; i < entities.Length; i++)
            {
                EntityState state = entities[i];

                if (state.Id == _entityId)
                {
                    continue;
                }

                if (!_timeline.TrySample(state.Id, renderTime, out EntitySample sample))
                {
                    continue;
                }

                _aliveIds.Add(state.Id);

                if (!_remoteViews.TryGetValue(state.Id, out RemotePlayerView view) || view == null)
                {
                    view = CreateRemoteView(state.Id, state.Kind, state.TypeId);
                    _remoteViews[state.Id] = view;
                }

                view.SetRank(state.Rank);
                view.Pvp = state.Pvp;
                if (sample.Kind == EntityKind.Player)
                {
                    view.SetPlayerLook(ClassSprite((byte)sample.TypeId), ClassColor((byte)sample.TypeId));
                }

                view.Apply(sample);
            }

            // Los que ya no están en el último snapshot se han ido: se retiran sus vistas.
            _deadIds.Clear();
            foreach (KeyValuePair<int, RemotePlayerView> pair in _remoteViews)
            {
                if (!_aliveIds.Contains(pair.Key))
                {
                    _deadIds.Add(pair.Key);
                }
            }

            for (int i = 0; i < _deadIds.Count; i++)
            {
                int id = _deadIds[i];

                if (_remoteViews.TryGetValue(id, out RemotePlayerView view) && view != null)
                {
                    Destroy(view.gameObject);
                }

                _remoteViews.Remove(id);
                _statusMasks.Remove(id);
                Debug.Log("[Avelora] Entidad remota " + id + " retirada.", this);
            }
        }

        private NpcLook FindNpcLook(ushort typeId, out bool usesFallback)
        {
            for (int i = 0; i < npcLooks.Length; i++)
            {
                if (npcLooks[i] != null && npcLooks[i].typeId == typeId)
                {
                    usesFallback = false;
                    return npcLooks[i];
                }
            }

            usesFallback = npcLooks.Length > 0 && npcLooks[0] != null;
            return usesFallback ? npcLooks[0] : null;
        }

        private Sprite _baseSprite;

        // Sprites fijos de personaje (Resources/Characters/<clase>, Resources/Npcs/<tipo>), 1,2 casillas de alto;
        // null = sin arte.
        private readonly Dictionary<string, Sprite> _figureSprites = new Dictionary<string, Sprite>();

        private Sprite ClassSprite(byte classId) => FigureSprite("Characters/" + classId);

        private Sprite FigureSprite(string path)
        {
            if (_figureSprites.TryGetValue(path, out Sprite sprite)) return sprite;
            Texture2D texture = Resources.Load<Texture2D>(path);
            sprite = texture == null ? null : Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f), texture.height / 1.2f);
            _figureSprites[path] = sprite;
            return sprite;
        }

        // Sprites de objetos en el suelo, uno por id, hechos del icono del inventario (60 % de una casilla).
        private readonly Dictionary<ushort, Sprite> _groundItemSprites = new Dictionary<ushort, Sprite>();

        private Sprite GroundItemSprite(ushort itemId)
        {
            if (_groundItemSprites.TryGetValue(itemId, out Sprite sprite)) return sprite;
            Texture2D icon = ItemIcon(itemId);
            sprite = icon == null ? null : Sprite.Create(icon, new Rect(0f, 0f, icon.width, icon.height),
                new Vector2(0.5f, 0.5f), icon.width / 0.6f);
            _groundItemSprites[itemId] = sprite;
            return sprite;
        }

        private RemotePlayerView CreateRemoteView(int entityId, EntityKind kind, ushort typeId)
        {
            if (_remoteRoot == null)
            {
                _remoteRoot = new GameObject("RemotePlayers").transform;
            }

            var go = new GameObject("Entity_" + entityId);
            go.transform.SetParent(_remoteRoot, false);

            var view = go.AddComponent<RemotePlayerView>();

            Sprite sprite = _baseSprite;
            Material material = _localRenderer != null ? _localRenderer.sharedMaterial : null;
            int sortingLayer = _localRenderer != null ? _localRenderer.sortingLayerID : 0;
            int sortingOrder = _localRenderer != null ? _localRenderer.sortingOrder : 0;

            view.Initialize(
                entityId,
                sprite,
                material,
                sortingLayer,
                sortingOrder,
                Color.gray,
                npcColor);

            if (kind == EntityKind.Player)
            {
                view.SetPlayerLook(ClassSprite((byte)typeId), ClassColor((byte)typeId));
            }

            if (kind == EntityKind.Item)
            {
                Sprite icon = GroundItemSprite(typeId);
                if (icon != null)
                {
                    view.SetItemSprite(icon);
                }
                else
                {
                    // Sin icono para ese id: cuadrado pequeño con el color de su clase.
                    _catalog.TryGetValue(typeId, out ItemCatalogEntry item);
                    view.SetItemLook(item.Kind == ItemKind.Potion ? new Color(0.9f, 0.2f, 0.3f, 1f)
                        : item.Kind == ItemKind.Weapon ? new Color(0.55f, 0.7f, 0.95f, 1f)
                        : new Color(0.95f, 0.9f, 0.75f, 1f));
                }
            }

            bool usesFallback = false;
            NpcLook look = kind == EntityKind.Npc ? FindNpcLook(typeId, out usesFallback) : null;
            // Sin hojas propias pero con sprite fijo en Resources/Npcs: mejor eso que el orco base teñido.
            Sprite npcSprite = kind == EntityKind.Npc && (look == null || usesFallback) ? FigureSprite("Npcs/" + typeId) : null;
            if (npcSprite != null)
            {
                view.SetNpcStaticSprite(npcSprite);
            }
            else if (look != null)
            {
                if (usesFallback)
                {
                    float hue = Mathf.Repeat(typeId * 0.618034f, 1f);
                    view.SetNpcTint(Color.HSVToRGB(hue, 0.45f, 1f));
                }

                view.SetNpcSheets(look.idle, look.walk, look.hurt, look.death, look.attack);
            }

            if (sprite == null)
            {
                Debug.LogWarning(
                    "[Avelora] La entidad remota " + entityId + " no tiene sprite: el jugador local no tiene SpriteRenderer con sprite.",
                    this);
            }

            Debug.Log("[Avelora] Entidad remota " + entityId + " creada.", this);
            return view;
        }

        private bool MapHashMatches(byte[] hash)
        {
            if (hash == null || _map == null || _map.ContentHashBytes == null)
            {
                return false;
            }

            if (hash.Length != _map.ContentHashBytes.Length)
            {
                return false;
            }

            for (int i = 0; i < hash.Length; i++)
            {
                if (hash[i] != _map.ContentHashBytes[i])
                {
                    return false;
                }
            }

            return true;
        }

        private void RejectLocally(RejectReason reason)
        {
            _lastReject = reason;
            _status = "rechazado: " + DescribeReject(reason);
            _disconnectRequested = true;
        }

        private void CleanupTransport()
        {
            IClientTransport transport = _transport;
            _transport = null;

            _connecting = false;
            _welcomeReceived = false;
            _challengeReceived = false;
            _challengeNonce = null;
            _accumulator = 0;

            if (transport != null)
            {
                transport.Connected -= OnTransportConnected;
                transport.Disconnected -= OnTransportDisconnected;
                transport.Received -= OnMessageReceived;
                transport.Dispose();
            }

            ClearRemoteViews();

            // No se borra _prediction: tras desconectar interesa seguir viendo la última posición
            // y las estadísticas. Connect() lo reemplaza al recibir el próximo Welcome.
        }

        private void ClearRemoteViews()
        {
            foreach (RemotePlayerView view in _remoteViews.Values)
            {
                if (view != null)
                {
                    Destroy(view.gameObject);
                }
            }

            _remoteViews.Clear();
            _projectiles.Clear();
            _damageNumbers.Clear();
            _lootTexts.Clear();
        }

        private static string DescribeReject(RejectReason reason)
        {
            switch (reason)
            {
                case RejectReason.ProtocolVersion:
                    return "versión del protocolo distinta";
                case RejectReason.TickRate:
                    return "tick rate distinto";
                case RejectReason.MapMismatch:
                    return "mapa distinto (huella)";
                case RejectReason.ServerFull:
                    return "servidor lleno";
                case RejectReason.MalformedMessage:
                    return "mensaje mal formado";
                case RejectReason.Timeout:
                    return "tiempo de saludo agotado";
                case RejectReason.RateLimited:
                    return "demasiados mensajes";
                case RejectReason.InvalidCredentials:
                    return "cuenta, contraseña o invitación incorrectas";
                case RejectReason.RegistrationClosed:
                    return "cuenta inexistente o invitación no válida";
                case RejectReason.CharacterInUse:
                    return "ese personaje ya está conectado";
                case RejectReason.LoggedInElsewhere:
                    return "tu cuenta se ha conectado desde otro sitio";
                case RejectReason.CharacterLimit:
                    return "la cuenta ya tiene el máximo de personajes";
                default:
                    return "motivo desconocido";
            }
        }

        private Color ClassColor(byte classId) => _classLook != null ? _classLook.ColorFor(classId) : Color.gray;

        private void HandleCharacterList(CharacterListMessage characterList)
        {
            int previousSelection = _selectedCharacterId;
            _characterList = characterList;
            _characterListReceived = true;
            _selectionActive = true;
            _connecting = false;
            _characterOperationPending = false;
            _characterSelectionPending = false;
            _confirmDelete = false;
            _status = "Selecciona un personaje o crea uno nuevo";

            CharacterListEntry[] characters = _characterList.Characters;
            _selectedCharacterId = 0;
            for (int i = 0; i < characters.Length; i++)
            {
                if (characters[i].CharacterId == previousSelection)
                {
                    _selectedCharacterId = previousSelection;
                    break;
                }
            }

            if (_selectedCharacterId == 0 && characters.Length > 0)
            {
                _selectedCharacterId = characters[0].CharacterId;
            }

            CharacterClassEntry[] classes = _characterList.Classes;
            bool createClassExists = false;
            for (int i = 0; i < classes.Length; i++)
            {
                if (classes[i].ClassId == _createClassId)
                {
                    createClassExists = true;
                    break;
                }
            }

            if (!createClassExists)
            {
                _createClassId = classes.Length > 0 ? classes[0].ClassId : (byte)0;
            }
        }

        private void SelectCharacter(int characterId)
        {
            if (_transport == null || _characterSelectionPending || _characterOperationPending)
            {
                return;
            }

            CharacterListEntry[] characters = _characterList.Characters;
            for (int i = 0; i < characters.Length; i++)
            {
                if (characters[i].CharacterId != characterId)
                {
                    continue;
                }

                _selectedCharacterId = characterId;
                _selectedClassId = characters[i].ClassId;
                _characterSelectionPending = true;
                _status = "Entrando con " + characters[i].Name + "…";
                byte[] bytes = MessageCodec.EncodeSelectCharacter(new SelectCharacterMessage(characterId));
                _transport.Send(bytes, bytes.Length, DeliveryMode.ReliableOrdered);
                return;
            }
        }

        private void CreateCharacter(byte classId)
        {
            if (_transport == null || _characterOperationPending)
            {
                return;
            }

            string trimmedName = _newCharacterName.Trim();
            if (System.Text.Encoding.UTF8.GetByteCount(trimmedName) > ProtocolConstants.MaxCharacterNameBytes)
            {
                _status = "Nombre demasiado largo";
                return;
            }

            if (trimmedName.Length < 3 || trimmedName.Length > 16)
            {
                _status = "El nombre debe tener entre 3 y 16 caracteres";
                return;
            }

            _status = "Creando personaje…";
            byte[] bytes = MessageCodec.EncodeCreateCharacter(new CreateCharacterMessage(trimmedName, classId));
            _transport.Send(bytes, bytes.Length, DeliveryMode.ReliableOrdered);
            _characterOperationPending = true;
        }

        private void DeleteCharacter(int characterId, string confirmationName)
        {
            if (_transport == null || _characterOperationPending)
            {
                return;
            }

            string trimmedName = confirmationName.Trim();
            if (System.Text.Encoding.UTF8.GetByteCount(trimmedName) > ProtocolConstants.MaxCharacterNameBytes)
            {
                _status = "Nombre demasiado largo";
                return;
            }

            _confirmDelete = false;
            _status = "Borrando personaje…";
            byte[] bytes = MessageCodec.EncodeDeleteCharacter(new DeleteCharacterMessage(characterId, trimmedName));
            _transport.Send(bytes, bytes.Length, DeliveryMode.ReliableOrdered);
            _characterOperationPending = true;
        }

        private static string DescribeCharacterReject(RejectReason reason)
        {
            switch (reason)
            {
                case RejectReason.NameTaken:
                    return "Ese nombre ya existe";
                case RejectReason.NameInvalid:
                    return "Nombre no válido: 3–16 letras, dígitos o espacios sueltos; empieza por letra";
                case RejectReason.CharacterLimit:
                    return "Ya tienes el máximo de personajes";
                case RejectReason.CharacterNotFound:
                case RejectReason.ClassUnknown:
                case RejectReason.ConfirmMismatch:
                    return "El nombre no coincide";
                default:
                    return "No se pudo completar la operación del personaje";
            }
        }

        private static string DescribeDisconnect(DisconnectReason reason)
        {
            switch (reason)
            {
                case DisconnectReason.Timeout:
                    return "tiempo de espera";
                case DisconnectReason.Rejected:
                    return "el servidor rechazó la conexión";
                case DisconnectReason.ServerFull:
                    return "servidor lleno";
                case DisconnectReason.ConnectionFailed:
                    return "no se pudo conectar";
                case DisconnectReason.RemoteClosed:
                    return "el servidor cerró la conexión";
                case DisconnectReason.LocalShutdown:
                    return "cerrada por el cliente";
                default:
                    return "desconocida";
            }
        }
    }
}
