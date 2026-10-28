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
    public sealed class NetworkClient : MonoBehaviour
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

        [Tooltip("Personaje a cargar. Vacío usa el mismo nombre que la cuenta.")]
        [SerializeField] private string characterName = "";

        [Tooltip("Código para registrar la cuenta si todavía no existe. Vacío = no registrar.")]
        [SerializeField] private string invitationCode = "avelora-dev";

        [Tooltip("Al empezar, conecta y desactiva el controlador local de la Etapa A.")]
        [SerializeField] private bool connectOnStart = true;

        [Tooltip("Tiempo máximo para recibir el Welcome antes de cerrar el socket.")]
        [SerializeField] private float welcomeTimeoutSeconds = 6f;

        [Header("Predicción e interpolación")]
        [Tooltip("Retraso con el que se dibuja al resto: 100 ms son dos snapshots a 20 Hz.")]
        [SerializeField] private float interpolationDelay = 0.1f;

        [SerializeField] private Color remotePlayerColor = new Color(0.45f, 0.8f, 1f, 1f);

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
        private ushort _equippedItemId;

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
        private Rect _statsPanel;

        // Catálogo que manda el servidor tras el Welcome (I-05): nombres, tipos, armas, alcance para
        // recoger y los valores de los puños. Hasta que llega, no se ataca ni se recoge.
        private readonly Dictionary<ushort, ItemCatalogEntry> _catalog = new Dictionary<ushort, ItemCatalogEntry>();
        private bool _catalogReceived;
        private float _pickupRange;
        private float _fistRange;
        private float _fistCooldownSeconds;
        private ushort[] _inventoryCounts = new ushort[0];

        // Arte de la interfaz (Resources/GUI). Los iconos van por id de objeto (GUI/Items/<id>):
        // el servidor manda los datos y el cliente solo pone la imagen; null = sin icono todavía.
        private readonly Dictionary<ushort, Texture2D> _itemIcons = new Dictionary<ushort, Texture2D>();
        private Texture2D _slotTexture;
        private GUIStyle _panelStyle;
        private GUIStyle _ribbonStyle;
        private GUIStyle _barStyle;
        private GUIStyle _panelTextStyle;
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
            _offlineController = GetComponent<LocalPlayerController>();
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
            _inventoryCounts = new ushort[0];
            _equippedItemId = 0;
            _catalog.Clear();
            _catalogReceived = false;
            _accumulator = 0;
            _lastReject = null;
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

            // Un clic en el panel de atributos es para sus botones, no para elegir objetivo.
            bool overPanel = mouse != null && _showStats &&
                _statsPanel.Contains(new Vector2(mouse.position.ReadValue().x, Screen.height - mouse.position.ReadValue().y));

            if (mouse != null && cam != null && mouse.leftButton.wasPressedThisFrame && !overPanel)
            {
                Vector2 screen = mouse.position.ReadValue();
                Vector3 world = cam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, -cam.transform.position.z));
                int picked = PickEntityAt(world, EntityKind.Npc, PickRadius);
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
                ShowHint("sin objetivo: haz clic en un enemigo");
                return;
            }

            Vector2 delta = (Vector2)(target.transform.position - transform.position);
            CurrentWeapon(out float attackRange, out float attackCooldown);
            if (delta.sqrMagnitude > attackRange * attackRange)
            {
                // En automático no es un error: ApproachTarget ya está caminando hacia él.
                if (!_autoAttack)
                {
                    ShowHint("fuera de alcance");
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

            // 1–9: equipa el arma de ese hueco o bebe la poción; U guarda el arma (I-05b).
            for (int n = 0; n < 9; n++)
            {
                if (keyboard[(Key)((int)Key.Digit1 + n)].wasPressedThisFrame)
                {
                    UseSlot(n);
                }
            }

            if (keyboard.uKey.wasPressedThisFrame)
            {
                if (_equippedItemId == 0)
                {
                    ShowHint("sin arma equipada");
                }
                else
                {
                    byte[] bytes = MessageCodec.EncodeUnequip(new UnequipMessage());
                    _transport.Send(bytes, bytes.Length, DeliveryMode.ReliableOrdered);
                }
            }
        }

        /// <summary>Equipa el arma del hueco o bebe su poción; el servidor valida.</summary>
        private void UseSlot(int slot)
        {
            if (slot >= _inventoryIds.Length || !_catalog.TryGetValue(_inventoryIds[slot], out ItemCatalogEntry item))
            {
                ShowHint("hueco vacío");
                return;
            }

            byte[] bytes;
            if (item.Kind == ItemKind.Weapon)
            {
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
        /// Dirección hacia el objetivo del ataque automático (I-06c), o cero si ya está a alcance. Va
        /// por el MoveInput de siempre: el servidor mueve y valida igual que con el teclado.
        /// </summary>
        // shortcut: camina en línea recta y se queda contra la pared si hay una en medio; añadir
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
            if (delta.sqrMagnitude <= stop * stop)
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

        /// <summary>Alcance y recarga del arma equipada, o de los puños si no hay (catálogo del servidor).</summary>
        private void CurrentWeapon(out float range, out float cooldownSeconds)
        {
            if (_catalog.TryGetValue(_equippedItemId, out ItemCatalogEntry weapon) && weapon.Kind == ItemKind.Weapon)
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

        private RemotePlayerView SelectedTarget()
        {
            if (_selectedNpcId == 0 ||
                !_remoteViews.TryGetValue(_selectedNpcId, out RemotePlayerView view) ||
                view == null || view.IsDead)
            {
                return null;
            }

            return view;
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
            string character = string.IsNullOrWhiteSpace(characterName)
                ? account
                : characterName.Trim();

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
                encrypted,
                character);

            byte[] bytes = MessageCodec.EncodeHello(hello);
            _transport.Send(bytes, bytes.Length, DeliveryMode.ReliableOrdered);
            _status = "saludando...";
        }

        private void OnTransportDisconnected(DisconnectReason reason)
        {
            _connecting = false;
            _welcomeReceived = false;
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
                        _equippedItemId = inventory.EquippedItemId;
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
                        System.Array.Clear(_pendingPoints, 0, _pendingPoints.Length);
                    }

                    break;

                case MessageType.ItemCatalog:
                    if (_welcomeReceived &&
                        MessageCodec.TryReadItemCatalog(buffer, offset, length, out ItemCatalogMessage catalog))
                    {
                        _catalog.Clear();
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
            }
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
            _connecting = false;
            _lastReject = null;
            _accumulator = 0;
            _lastDirectionX = 0;

            var halfSize = new Vec2(welcome.HalfWidth, welcome.HalfHeight);
            _prediction = new ClientPrediction(
                _map.Grid,
                halfSize,
                welcome.Speed,
                SimulationConstants.FixedDt);
            _prediction.Reset(new Vec2(welcome.SpawnX, welcome.SpawnY));
            ClearInputHistory();
            _welcomeReceived = true;

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
                Debug.Log("[Avelora] Entidad remota " + id + " retirada.", this);
            }
        }

        private NpcLook FindNpcLook(ushort typeId)
        {
            for (int i = 0; i < npcLooks.Length; i++)
            {
                if (npcLooks[i] != null && npcLooks[i].typeId == typeId)
                {
                    return npcLooks[i];
                }
            }

            return npcLooks.Length > 0 ? npcLooks[0] : null;
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

            Sprite sprite = _localRenderer != null ? _localRenderer.sprite : null;
            Material material = _localRenderer != null ? _localRenderer.sharedMaterial : null;
            int sortingLayer = _localRenderer != null ? _localRenderer.sortingLayerID : 0;
            int sortingOrder = _localRenderer != null ? _localRenderer.sortingOrder : 0;

            view.Initialize(
                entityId,
                sprite,
                material,
                sortingLayer,
                sortingOrder,
                remotePlayerColor,
                npcColor);

            if (kind == EntityKind.Item)
            {
                // shortcut: sin arte de objetos todavía; un cuadrado pequeño con el color de su clase.
                _catalog.TryGetValue(typeId, out ItemCatalogEntry item);
                view.SetItemLook(item.Kind == ItemKind.Potion ? new Color(0.9f, 0.2f, 0.3f, 1f)
                    : item.Kind == ItemKind.Weapon ? new Color(0.55f, 0.7f, 0.95f, 1f)
                    : new Color(0.95f, 0.9f, 0.75f, 1f));
            }

            NpcLook look = kind == EntityKind.Npc ? FindNpcLook(typeId) : null;
            if (look != null)
            {
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

        private void OnGUI()
        {
            if (!showOverlay)
            {
                return;
            }

            if (_overlayStyle == null)
            {
                _overlayStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 13,
                    wordWrap = true,
                };
                _overlayStyle.normal.textColor = Color.white;
                _chatLineStyle = new GUIStyle(_overlayStyle) { wordWrap = false, clipping = TextClipping.Clip };
            }

            string combat = "Objetivo " + (_selectedNpcId != 0 ? _selectedNpcId.ToString() : "ninguno") +
                (_autoAttack ? " (auto · Esc cancela)" : string.Empty) +
                " · Ataques " + _attacksSent +
                (Time.unscaledTime < _combatHintUntil ? " · " + _combatHint : string.Empty);

            GUI.Box(new Rect(10f, 10f, 540f, 130f), GUIContent.none);
            GUI.Label(
                new Rect(20f, 16f, 520f, 118f),
                combat + "\n" +
                "Avelora · " + host + ":" + port + "\n" +
                _status + "\n" +
                "Entidad " + _entityId +
                " · Pendientes " + PendingInputs +
                " · Snapshots " + _snapshotsReceived +
                " · Correcciones " + Corrections +
                " · Saltos " + Snaps + "\n" +
                "Input " + LastInputSequence +
                " · Confirmado " + LastProcessedInputSequence +
                " · Tick " + _lastServerTick +
                " · Remotos " + RemoteViewCount +
                (_transport == null ? " · Pulsa R para reconectar" : string.Empty),
                _overlayStyle);

            if (_welcomeReceived)
            {
                DrawOwnHealth();
                DrawChat();
            }
        }

        /// <summary>
        /// Últimos mensajes de la zona abajo a la izquierda y caja para escribir (I-07b). Las teclas
        /// se leen aquí, antes que el TextField, para que Enter y Esc no los consuma él.
        /// </summary>
        private void DrawChat()
        {
            Event e = Event.current;
            bool keyDown = e.type == EventType.KeyDown;
            bool enter = keyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter);

            if (!_chatOpen && enter)
            {
                _chatOpen = true;
                _chatDraft = string.Empty;
                e.Use();
            }
            else if (_chatOpen && enter)
            {
                SendChat(_chatDraft);
                _chatOpen = false;
                e.Use();
            }
            else if (_chatOpen && keyDown && e.keyCode == KeyCode.Escape)
            {
                _chatOpen = false;
                e.Use();
            }

            const float width = 420f;
            const float lineHeight = 18f;
            float logHeight = (ChatLogLines * lineHeight) + 10f;
            var log = new Rect(10f, Screen.height - 100f - logHeight, width, logHeight);

            if (_chatLog.Count > 0 || _chatOpen)
            {
                GUI.Box(log, GUIContent.none);
                float y = log.yMax - 5f - (_chatLog.Count * lineHeight);
                for (int i = 0; i < _chatLog.Count; i++)
                {
                    GUI.Label(new Rect(log.x + 6f, y + (i * lineHeight), width - 12f, lineHeight), _chatLog[i], _chatLineStyle);
                }
            }

            if (_chatOpen)
            {
                GUI.SetNextControlName(ChatControl);
                _chatDraft = GUI.TextField(new Rect(log.x, log.yMax + 4f, width, 22f), _chatDraft, ProtocolConstants.MaxChatBytes);
                GUI.FocusControl(ChatControl);
            }
            else if (_chatLog.Count == 0)
            {
                GUI.Label(new Rect(log.x, log.yMax - 20f, width, 20f), "Enter: chat", _overlayStyle);
            }
        }

        /// <summary>El servidor valida y limita el ritmo; aquí solo se evita mandar lo que seguro rechazaría.</summary>
        private void SendChat(string text)
        {
            text = text.Trim();
            if (text.Length == 0 || _transport == null)
            {
                return;
            }

            if (System.Text.Encoding.UTF8.GetByteCount(text) > ProtocolConstants.MaxChatBytes)
            {
                ShowHint("mensaje demasiado largo");
                return;
            }

            byte[] bytes = MessageCodec.EncodeChatSend(new ChatSendMessage(text));
            _transport.Send(bytes, bytes.Length, DeliveryMode.ReliableOrdered);
        }

        /// <summary>
        /// Inventario a la derecha (I-04b): rejilla de huecos con el icono de cada objeto, la
        /// cantidad abajo a la derecha y el número de tecla arriba a la izquierda; el nombre sale al
        /// pasar el ratón. I lo oculta.
        /// </summary>
        private void DrawInventory()
        {
            const int columns = 5;
            const float cell = 48f;
            const float gap = 4f;
            const float pad = 18f;
            const float header = 74f;

            EnsureGuiArt();

            int rows = Mathf.Max(1, (_inventoryIds.Length + columns - 1) / columns);
            float width = (pad * 2f) + (columns * cell) + ((columns - 1) * gap);
            float height = header + (rows * (cell + gap)) + pad;
            var box = new Rect(Screen.width - width - 10f, 24f, width, height);
            GUI.Box(box, GUIContent.none, _panelStyle);
            GUI.Box(new Rect(box.x + 40f, box.y - 18f, width - 80f, 44f), "Inventario (I)", _ribbonStyle);

            string weapon = _catalog.TryGetValue(_equippedItemId, out ItemCatalogEntry equipped)
                ? equipped.Name + " (daño " + equipped.Damage + ")"
                : "puños";
            GUI.Label(
                new Rect(box.x + pad, box.y + 28f, width - (pad * 2f), header - 28f),
                "Arma: " + weapon + "\nE recoger · Q poción · 1-9 usar · U guardar · C atributos",
                _panelTextStyle);

            string hovered = null;
            Vector2 mouse = Event.current.mousePosition;

            for (int i = 0; i < _inventoryIds.Length; i++)
            {
                var slot = new Rect(
                    box.x + pad + ((i % columns) * (cell + gap)),
                    box.y + header + ((i / columns) * (cell + gap)),
                    cell,
                    cell);
                ushort id = _inventoryIds[i];
                Texture2D icon = ItemIcon(id);

                if (icon != null)
                {
                    GUI.DrawTexture(slot, icon);
                }
                else if (_slotTexture != null)
                {
                    GUI.DrawTexture(slot, _slotTexture);
                }
                else
                {
                    GUI.Box(slot, GUIContent.none);
                }

                if (i < 9)
                {
                    GUI.Label(new Rect(slot.x + 3f, slot.y, cell, 18f), (i + 1).ToString(), _overlayStyle);
                }

                if (id == 0)
                {
                    continue;
                }

                string name = _catalog.TryGetValue(id, out ItemCatalogEntry entry) ? entry.Name : "Objeto " + id;

                if (icon == null)
                {
                    // Sin arte todavía: las primeras letras del nombre en el centro del hueco.
                    GUI.Label(new Rect(slot.x + 6f, slot.y + 15f, cell - 6f, 20f), name.Substring(0, Mathf.Min(4, name.Length)), _overlayStyle);
                }

                if (_inventoryCounts[i] > 1)
                {
                    GUI.Label(new Rect(slot.x + cell - 24f, slot.y + cell - 19f, 24f, 18f), _inventoryCounts[i].ToString(), _overlayStyle);
                }

                if (slot.Contains(mouse))
                {
                    hovered = name;
                }
            }

            if (hovered != null)
            {
                GUI.Box(new Rect(mouse.x - 170f, mouse.y + 12f, 170f, 34f), GUIContent.none, _panelStyle);
                GUI.Label(new Rect(mouse.x - 160f, mouse.y + 19f, 150f, 22f), hovered, _panelTextStyle);
            }
        }

        /// <summary>
        /// Panel de atributos (I-09b), arriba a la izquierda. «+» aparta un punto; Confirmar manda el
        /// reparto y el servidor decide. Todos los números vienen del último Stats.
        /// </summary>
        private void DrawStats()
        {
            const float width = 270f;
            const float pad = 18f;
            const float row = 22f;

            EnsureGuiArt();

            int pending = _pendingPoints[0] + _pendingPoints[1] + _pendingPoints[2] + _pendingPoints[3];
            int free = _stats.FreePoints - pending;
            bool anyAllocated = _stats.AllocStr + _stats.AllocDex + _stats.AllocInt + _stats.AllocVit > 0;

            _statsPanel = new Rect(10f, 168f, width, 370f); // bajo el recuadro de depuración
            GUI.Box(_statsPanel, GUIContent.none, _panelStyle);
            GUI.Box(new Rect(_statsPanel.x + 40f, _statsPanel.y - 18f, width - 80f, 44f), "Personaje (C)", _ribbonStyle);

            float x = _statsPanel.x + pad;
            float y = _statsPanel.y + 34f;
            GUI.Label(new Rect(x, y, width, row), "Clase " + _stats.ClassId + " · Nivel " + _stats.Level, _panelTextStyle);
            y += row;
            GUI.Label(new Rect(x, y, width, row), "Puntos libres: " + free, _panelTextStyle);
            y += row + 4f;

            string[] names = { "Fuerza", "Destreza", "Inteligencia", "Vitalidad" };
            ushort[] totals = { _stats.Str, _stats.Dex, _stats.Int, _stats.Vit };
            ushort[] allocated = { _stats.AllocStr, _stats.AllocDex, _stats.AllocInt, _stats.AllocVit };
            for (int i = 0; i < 4; i++)
            {
                string pendingText = _pendingPoints[i] > 0 ? "  +" + _pendingPoints[i] : string.Empty;
                GUI.Label(
                    new Rect(x, y, 190f, row),
                    names[i] + " " + (totals[i] + _pendingPoints[i]) + " (" + allocated[i] + " rep.)" + pendingText,
                    _panelTextStyle);

                GUI.enabled = free > 0 && !_ownDead;
                if (GUI.Button(new Rect(x + 200f, y, 30f, row - 2f), "+"))
                {
                    _pendingPoints[i]++;
                }

                GUI.enabled = true;
                y += row;
            }

            y += 4f;
            GUI.enabled = pending > 0 && !_ownDead;
            if (GUI.Button(new Rect(x, y, 112f, row + 2f), "Confirmar"))
            {
                SendAllocatePoints();
            }

            if (GUI.Button(new Rect(x + 118f, y, 112f, row + 2f), "Cancelar"))
            {
                System.Array.Clear(_pendingPoints, 0, _pendingPoints.Length);
            }

            y += row + 6f;
            GUI.enabled = anyAllocated && pending == 0 && !_ownDead;
            if (GUI.Button(new Rect(x, y, 230f, row + 2f), _stats.FreeReset != 0 ? "Reajustar (gratis)" : "Reajustar (piedra de reajuste)"))
            {
                byte[] bytes = MessageCodec.EncodeResetPoints(new ResetPointsMessage());
                _transport.Send(bytes, bytes.Length, DeliveryMode.ReliableOrdered);
            }

            GUI.enabled = true;
            y += row + 10f;

            string damage = _stats.AttackDamage + (_stats.DamageType == 1 ? " mágico" : " físico");
            GUI.Label(
                new Rect(x, y, width - (pad * 2f), 110f),
                "Vida máxima: " + _stats.MaxHealth +
                "\nDaño: " + damage +
                "\nDefensa: " + _stats.Defense + " · Res. mágica: " + _stats.MagicResist +
                "\nRecurso: " + _stats.Resource + "/" + _stats.MaxResource,
                _panelTextStyle);
        }

        private void SendAllocatePoints()
        {
            byte[] bytes = MessageCodec.EncodeAllocatePoints(new AllocatePointsMessage(
                (byte)_pendingPoints[0],
                (byte)_pendingPoints[1],
                (byte)_pendingPoints[2],
                (byte)_pendingPoints[3]));
            _transport.Send(bytes, bytes.Length, DeliveryMode.ReliableOrdered);

            // Se limpia al llegar el Stats de respuesta; si el servidor lo rechaza, no llega ninguno y
            // el jugador vuelve a ver sus puntos libres reales al limpiar aquí.
            System.Array.Clear(_pendingPoints, 0, _pendingPoints.Length);
        }

        /// <summary>
        /// Carga una vez el arte de Resources/GUI (pack Tiny Swords UI). Los bordes son los píxeles que
        /// no se estiran al dibujar en 9 partes.
        /// </summary>
        private void EnsureGuiArt()
        {
            if (_panelStyle != null)
            {
                return;
            }

            _slotTexture = Resources.Load<Texture2D>("GUI/Slot");
            _panelStyle = new GUIStyle { border = new RectOffset(24, 24, 24, 24) };
            _panelStyle.normal.background = Resources.Load<Texture2D>("GUI/Panel");
            _barStyle = new GUIStyle { border = new RectOffset(24, 24, 0, 0) };
            _barStyle.normal.background = Resources.Load<Texture2D>("GUI/Bar");
            _ribbonStyle = new GUIStyle
            {
                border = new RectOffset(64, 64, 0, 0),
                alignment = TextAnchor.MiddleCenter,
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                contentOffset = new Vector2(0f, -4f),
            };
            _ribbonStyle.normal.background = Resources.Load<Texture2D>("GUI/Ribbon");
            _ribbonStyle.normal.textColor = new Color(0.25f, 0.16f, 0.06f);
            _panelTextStyle = new GUIStyle(_overlayStyle);
            _panelTextStyle.normal.textColor = new Color(0.25f, 0.16f, 0.06f);
        }

        private Texture2D ItemIcon(ushort itemId)
        {
            if (itemId == 0)
            {
                return null;
            }

            if (!_itemIcons.TryGetValue(itemId, out Texture2D icon))
            {
                icon = Resources.Load<Texture2D>("GUI/Items/" + itemId);
                _itemIcons[itemId] = icon;
            }

            return icon;
        }

        /// <summary>Barra de vida propia abajo al centro y aviso de muerte con cuenta atrás (I-03b).</summary>
        private void DrawOwnHealth()
        {
            const float width = 300f;
            const float height = 16f;
            var back = new Rect((Screen.width - width) / 2f, Screen.height - 50f, width, height);

            // Marco de madera tallada alrededor de las dos barras.
            EnsureGuiArt();
            GUI.Box(new Rect(back.x - 16f, back.y - 12f, width + 32f, height + 33f), GUIContent.none, _barStyle);

            Color previous = GUI.color;
            GUI.color = new Color(0.1f, 0.1f, 0.1f, 0.85f);
            GUI.DrawTexture(back, Texture2D.whiteTexture);
            GUI.color = new Color(0.8f, 0.15f, 0.15f, 1f);
            GUI.DrawTexture(new Rect(back.x, back.y, width * _ownHealthPercent / 100f, height), Texture2D.whiteTexture);
            // Barra de experiencia (I-06) justo debajo; llena en el nivel máximo.
            var xp = new Rect(back.x, back.y + height + 3f, width, 6f);
            GUI.color = new Color(0.1f, 0.1f, 0.1f, 0.85f);
            GUI.DrawTexture(xp, Texture2D.whiteTexture);
            GUI.color = new Color(0.95f, 0.8f, 0.2f, 1f);
            float xpFill = _experienceToNext == 0 ? 1f : (float)_experience / _experienceToNext;
            GUI.DrawTexture(new Rect(xp.x, xp.y, width * xpFill, xp.height), Texture2D.whiteTexture);
            GUI.color = previous;

            // El snapshot solo trae el porcentaje; la vida absoluta es aproximada.
            string health = _maxHealth > 0
                ? "Vida " + Mathf.RoundToInt(_maxHealth * _ownHealthPercent / 100f) + "/" + _maxHealth
                : "Vida " + _ownHealthPercent + " %";
            string level = _level > 0
                ? "Nv " + _level + " · " + (_experienceToNext == 0 ? "máx." : _experience + "/" + _experienceToNext + " XP") + " · "
                : string.Empty;
            GUI.Label(new Rect(back.x + 6f, back.y - 1f, width, height + 2f), level + health, _overlayStyle);

            if (_showInventory)
            {
                DrawInventory();
            }

            if (_showStats && _statsReceived)
            {
                DrawStats();
            }

            if (_ownDead)
            {
                float left = Mathf.Max(0f, RespawnSeconds - (Time.unscaledTime - _ownDeathAt));
                var box = new Rect((Screen.width - 320f) / 2f, Screen.height / 2f - 30f, 320f, 60f);
                GUI.Box(box, GUIContent.none);
                GUI.Label(new Rect(box.x + 16f, box.y + 10f, 300f, 44f),
                    "Has muerto. Reapareces en " + Mathf.CeilToInt(left) + " s.", _overlayStyle);
            }
        }
    }
}
