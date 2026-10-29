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
        private Rect _statsPanel;
        private Rect _inventoryPanel;
        private Rect _hotbarPanel;

        // Arte oscuro de la interfaz (Resources/GUI/Dark) y sus estilos de texto.
        private Texture2D _darkWindow;
        private Texture2D _darkSlot;
        private Texture2D _darkSlotFill;
        private Texture2D _darkHotbar;
        private Texture2D _darkCharacter;
        private Texture2D _darkTooltip;
        private readonly Texture2D[] _statIcons = new Texture2D[9];
        private GUIStyle _darkTextStyle;
        private GUIStyle _darkRightStyle;
        private GUIStyle _darkSmallStyle;
        private GUIStyle _darkTitleStyle;
        private GUIStyle _darkButtonStyle;

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
        private GUIStyle _barStyle;
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
            System.Array.Clear(_equipment, 0, _equipment.Length);
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

            // Un clic sobre un panel es para la interfaz, no para elegir objetivo.
            bool overPanel = false;
            if (mouse != null)
            {
                var gui = new Vector2(mouse.position.ReadValue().x, Screen.height - mouse.position.ReadValue().y);
                overPanel = (_showStats && _statsPanel.Contains(gui)) ||
                    (_showInventory && _inventoryPanel.Contains(gui)) ||
                    _hotbarPanel.Contains(gui) ||
                    (_discardSlot >= 0 && _discardDialog.Contains(gui));
            }

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

            // El 0 es el décimo hueco de la barra rápida.
            if (keyboard.digit0Key.wasPressedThisFrame && _inventoryIds.Length > 9)
            {
                UseSlot(9);
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
            byte[] bytes = MessageCodec.EncodeUnequip(new UnequipMessage((byte)equipSlot, (byte)targetSlot));
            _transport.Send(bytes, bytes.Length, DeliveryMode.ReliableOrdered);
        }

        private void SendMoveItem(int from, int to, int count)
        {
            byte[] bytes = MessageCodec.EncodeMoveItem(new MoveItemMessage((byte)from, (byte)to, (ushort)count));
            _transport.Send(bytes, bytes.Length, DeliveryMode.ReliableOrdered);
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
                        _equipment = inventory.Equipment;
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
                // Sin texto enriquecido: un <size=400> escrito en el chat taparía la pantalla de todos.
                _chatLineStyle = new GUIStyle(_overlayStyle) { wordWrap = false, clipping = TextClipping.Clip, richText = false };
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
                _dropTarget = -1;
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

        // Medidas en píxeles de las imágenes de Resources/GUI/Dark (recortadas de Art/GUI/Inventario y
        // Art/GUI/panel_stats): todo se dibuja escalado y se coloca con estas coordenadas.
        private const float WindowWidth = 838f, WindowHeight = 711f;
        private const float CharacterWidth = 916f, CharacterHeight = 791f;
        private const float TooltipWidth = 476f, TooltipHeight = 555f;
        private const float HotbarWidth = 1466f, HotbarHeight = 152f;

        /// <summary>Rectángulo de la imagen original (en píxeles) colocado sobre un panel escalado.</summary>
        private static Rect Part(Rect panel, float scale, float x, float y, float width, float height) =>
            new Rect(panel.x + (x * scale), panel.y + (y * scale), width * scale, height * scale);

        /// <summary>GUI.Button que solo responde al clic izquierdo (IMGUI acepta cualquier botón del ratón).</summary>
        private static bool LeftButton(Rect rect, GUIContent content, GUIStyle style) =>
            GUI.Button(rect, content, style) && Event.current.button == 0;

        private static bool LeftButton(Rect rect, string text, GUIStyle style) =>
            LeftButton(rect, new GUIContent(text), style);

        // Paneles movibles (como en Lineage 2): se arrastran con el clic izquierdo por la barra del
        // título y la posición se guarda en PlayerPrefs. 0 = ninguno, 1 = personaje, 2 = inventario.
        private int _draggedPanel;
        private Vector2 _dragOffset;
        private Vector2? _statsPosition;
        private Vector2? _inventoryPosition;

        /// <summary>
        /// Coloca un panel: en su sitio guardado (o el de por defecto) y, si se arrastra por la barra
        /// del título (alto <paramref name="titleHeight"/>, sin la X de la derecha), lo mueve sin
        /// dejar que se salga de la pantalla.
        /// </summary>
        private Rect PlacePanel(int id, ref Vector2? position, Vector2 fallback, Vector2 size, float titleHeight)
        {
            string key = "avelora.panel" + id;
            if (position == null)
            {
                position = PlayerPrefs.HasKey(key + ".x")
                    ? new Vector2(PlayerPrefs.GetFloat(key + ".x"), PlayerPrefs.GetFloat(key + ".y"))
                    : fallback;
            }

            Event e = Event.current;
            var title = new Rect(position.Value.x, position.Value.y, size.x - titleHeight - 10f, titleHeight);
            if (e.type == EventType.MouseDown && e.button == 0 && title.Contains(e.mousePosition))
            {
                _draggedPanel = id;
                _dragOffset = e.mousePosition - position.Value;
                e.Use();
            }
            else if (_draggedPanel == id && e.type == EventType.MouseDrag)
            {
                position = e.mousePosition - _dragOffset;
                e.Use();
            }
            else if (_draggedPanel == id && e.rawType == EventType.MouseUp)
            {
                _draggedPanel = 0;
                PlayerPrefs.SetFloat(key + ".x", position.Value.x);
                PlayerPrefs.SetFloat(key + ".y", position.Value.y);
            }

            // Siempre entero en pantalla, también si la ventana se achica después de moverlo.
            position = new Vector2(
                Mathf.Clamp(position.Value.x, 0f, Mathf.Max(0f, Screen.width - size.x)),
                Mathf.Clamp(position.Value.y, 0f, Mathf.Max(0f, Screen.height - size.y)));
            return new Rect(position.Value, size);
        }

        /// <summary>
        /// Inventario (I): ventana con la rejilla de 5 columnas; cantidad abajo a la derecha y la ficha
        /// del objeto al pasar el ratón. La X o la tecla I lo cierran.
        /// </summary>
        private void DrawInventory()
        {
            const int columns = 5;
            const float scale = 0.62f;

            EnsureGuiArt();

            // Por defecto, centrado en la mitad derecha de la pantalla (el personaje va en la izquierda).
            float panelWidth = WindowWidth * scale;
            _inventoryPanel = PlacePanel(
                2,
                ref _inventoryPosition,
                new Vector2((Screen.width * 0.5f) + Mathf.Max(10f, ((Screen.width * 0.5f) - panelWidth) / 2f), 80f),
                new Vector2(panelWidth, WindowHeight * scale),
                75f * scale);
            GUI.DrawTexture(_inventoryPanel, _darkWindow);
            if (LeftButton(Part(_inventoryPanel, scale, 775f, 12f, 50f, 50f), GUIContent.none, GUIStyle.none))
            {
                _showInventory = false;
            }

            // Zona útil de la ventana: x 17–827, y 75–695 en la imagen original.
            Rect content = Part(_inventoryPanel, scale, 17f, 75f, 810f, 620f);
            int rows = Mathf.Max(1, (_inventoryIds.Length + columns - 1) / columns);
            // Abajo quedan 34 px para la línea de ayuda.
            float cell = Mathf.Min((content.width - 40f) / columns, (content.height - 50f - ((rows - 1) * 6f)) / rows);
            float gap = 6f;
            float gridWidth = (columns * cell) + ((columns - 1) * gap);
            float left = content.x + ((content.width - gridWidth) / 2f);
            float top = content.y + 10f;

            ushort hovered = 0;
            Vector2 mouse = Event.current.mousePosition;
            for (int i = 0; i < _inventoryIds.Length; i++)
            {
                var slot = new Rect(left + ((i % columns) * (cell + gap)), top + ((i / columns) * (cell + gap)), cell, cell);
                SlotInput(slot, i, _inventoryIds[i]);
                if (DrawItemSlot(slot, _inventoryIds[i], _inventoryCounts[i]) && slot.Contains(mouse))
                {
                    hovered = _inventoryIds[i];
                }
            }

            GUI.Label(
                new Rect(content.x + 12f, content.yMax - 26f, content.width - 24f, 20f),
                "Arrastra para mover (Shift: media pila) · doble clic usar · clic dcho. equipar · fuera: tirar",
                _darkSmallStyle);

            if (hovered != 0)
            {
                DrawItemTooltip(hovered, mouse);
            }
        }

        /// <summary>
        /// Barra rápida abajo al centro: los 10 primeros huecos del inventario con las teclas 1–0.
        /// </summary>
        private void DrawHotbar()
        {
            EnsureGuiArt();

            const float scale = 0.42f; // tamaño fijo: el texto no escala, así que los paneles tampoco
            _hotbarPanel = new Rect((Screen.width - (HotbarWidth * scale)) / 2f, Screen.height - (HotbarHeight * scale) - 8f, HotbarWidth * scale, HotbarHeight * scale);
            GUI.DrawTexture(_hotbarPanel, _darkHotbar);

            ushort hovered = 0;
            Vector2 mouse = Event.current.mousePosition;
            int count = Mathf.Min(10, _inventoryIds.Length);
            for (int i = 0; i < count; i++)
            {
                // Huecos de 123×120 cada 132 px desde x = 77, y = 16 en la imagen original.
                Rect slot = Part(_hotbarPanel, scale, 77f + (i * 132f) + 13.5f, 16f + 12f, 96f, 96f);
                SlotInput(slot, i, _inventoryIds[i]);
                if (DrawItemSlot(slot, _inventoryIds[i], _inventoryCounts[i], drawFrame: false) && slot.Contains(mouse))
                {
                    hovered = _inventoryIds[i];
                }
            }

            if (hovered != 0)
            {
                DrawItemTooltip(hovered, mouse);
            }
        }

        // Inventario interactivo (I-14b). Un «origen» es un hueco del inventario (0…) o del equipo
        // (EquipSource + EquipSlot). El cliente solo pide; el servidor valida y manda el Inventory nuevo.
        private const int EquipSource = 100;
        private int _pressSource = -1;
        private Vector2 _pressAt;
        private bool _dragging;
        private int _dropTarget = -1;
        private int _discardSlot = -1;
        private ushort _discardItemId;
        private Rect _discardDialog;

        /// <summary>
        /// Clics sobre un hueco: izquierdo empieza a arrastrar (doble clic usa o quita), derecho equipa
        /// o quita. Además apunta qué hueco hay bajo el ratón para cuando se suelte lo arrastrado.
        /// </summary>
        private void SlotInput(Rect rect, int source, ushort id)
        {
            Event e = Event.current;
            if (!rect.Contains(e.mousePosition))
            {
                return;
            }

            _dropTarget = source;
            if (e.type != EventType.MouseDown || id == 0 || _transport == null)
            {
                return;
            }

            if (e.button == 0 && e.clickCount == 2)
            {
                _pressSource = -1;
                ActivateSlot(source);
                e.Use();
            }
            else if (e.button == 0)
            {
                _pressSource = source;
                _pressAt = e.mousePosition;
                e.Use();
            }
            else if (e.button == 1)
            {
                if (source >= EquipSource ||
                    (_catalog.TryGetValue(id, out ItemCatalogEntry item) && (item.Kind == ItemKind.Weapon || item.Kind == ItemKind.Armor)))
                {
                    ActivateSlot(source);
                }
                else
                {
                    ShowHint("no se equipa");
                }

                e.Use();
            }
        }

        /// <summary>Hueco del inventario: usar o equipar. Hueco del equipo: devolverlo al inventario.</summary>
        private void ActivateSlot(int source)
        {
            if (source >= EquipSource)
            {
                SendUnequip(source - EquipSource, byte.MaxValue);
            }
            else
            {
                UseSlot(source);
            }
        }

        /// <summary>
        /// Arrastre en curso: dibuja el icono bajo el ratón y, al soltar, pide mover, dividir (Shift),
        /// equipar o quitar. Soltar fuera de los paneles abre el aviso de tirar o destruir.
        /// </summary>
        private void HandleDrag()
        {
            Event e = Event.current;
            if (_pressSource < 0)
            {
                return;
            }

            if (!_dragging && e.type == EventType.MouseDrag && (e.mousePosition - _pressAt).sqrMagnitude > 36f)
            {
                _dragging = true;
            }

            int from = _pressSource;
            ushort id = from >= EquipSource ? _equipment[from - EquipSource] : from < _inventoryIds.Length ? _inventoryIds[from] : (ushort)0;
            if (_dragging && e.type == EventType.Repaint && id != 0)
            {
                DrawItemSlot(new Rect(e.mousePosition.x - 24f, e.mousePosition.y - 24f, 48f, 48f), id, 1, drawFrame: false);
            }

            if (e.rawType != EventType.MouseUp || e.button != 0)
            {
                return;
            }

            _pressSource = -1;
            if (!_dragging)
            {
                return;
            }

            _dragging = false;
            int to = _dropTarget;
            if (id == 0 || to == from || _transport == null)
            {
                return;
            }

            if (to < 0)
            {
                bool overPanel = (_showStats && _statsPanel.Contains(e.mousePosition)) ||
                    (_showInventory && _inventoryPanel.Contains(e.mousePosition)) ||
                    _hotbarPanel.Contains(e.mousePosition);
                if (!overPanel && from < EquipSource)
                {
                    _discardSlot = from;
                    _discardItemId = id;
                }
            }
            else if (from < EquipSource && to < EquipSource)
            {
                ushort count = _inventoryCounts[from];
                SendMoveItem(from, to, e.shift && count > 1 ? count / 2 : 0);
            }
            else if (from < EquipSource)
            {
                if (_catalog.TryGetValue(id, out ItemCatalogEntry item) && item.EquipSlot == to - EquipSource)
                {
                    UseSlot(from);
                }
                else
                {
                    ShowHint("no va en ese hueco");
                }
            }
            else if (to < EquipSource)
            {
                SendUnequip(from - EquipSource, to);
            }
        }

        /// <summary>Aviso al soltar un objeto fuera de los paneles: tirarlo al suelo o destruirlo.</summary>
        private void DrawDiscardDialog()
        {
            // Si el hueco cambió mientras tanto (lo recogió otro mensaje), se olvida.
            if (_discardSlot >= _inventoryIds.Length || _inventoryIds[_discardSlot] != _discardItemId)
            {
                _discardSlot = -1;
                return;
            }

            string name = _catalog.TryGetValue(_discardItemId, out ItemCatalogEntry item) ? item.Name : "Objeto " + _discardItemId;
            ushort count = _inventoryCounts[_discardSlot];
            _discardDialog = new Rect((Screen.width - 360f) / 2f, (Screen.height / 2f) - 60f, 360f, 110f);
            GUI.DrawTexture(_discardDialog, _darkSlotFill);
            GUI.Label(
                new Rect(_discardDialog.x + 14f, _discardDialog.y + 12f, 332f, 30f),
                "¿Qué hacer con " + name + (count > 1 ? " ×" + count : string.Empty) + "?",
                _darkTextStyle);

            byte[] bytes = null;
            if (LeftButton(new Rect(_discardDialog.x + 14f, _discardDialog.y + 60f, 120f, 34f), "Tirar al suelo", _darkButtonStyle))
            {
                bytes = MessageCodec.EncodeDropItem(new DropItemMessage((byte)_discardSlot, 0));
            }

            if (LeftButton(new Rect(_discardDialog.x + 142f, _discardDialog.y + 60f, 100f, 34f), "Destruir", _darkButtonStyle))
            {
                bytes = MessageCodec.EncodeDestroyItem(new DestroyItemMessage((byte)_discardSlot, 0));
            }

            if (LeftButton(new Rect(_discardDialog.x + 250f, _discardDialog.y + 60f, 96f, 34f), "Cancelar", _darkButtonStyle))
            {
                _discardSlot = -1;
            }

            if (bytes != null && _transport != null)
            {
                _transport.Send(bytes, bytes.Length, DeliveryMode.ReliableOrdered);
                _discardSlot = -1;
            }
        }

        /// <summary>Dibuja un hueco con su objeto. Devuelve si hay objeto.</summary>
        private bool DrawItemSlot(Rect slot, ushort id, ushort count, bool drawFrame = true)
        {
            if (drawFrame)
            {
                GUI.DrawTexture(slot, _darkSlot);
            }

            if (id == 0)
            {
                return false;
            }

            Rect inner = new Rect(slot.x + (slot.width * 0.16f), slot.y + (slot.height * 0.16f), slot.width * 0.68f, slot.height * 0.68f);
            Texture2D icon = ItemIcon(id);
            string name = _catalog.TryGetValue(id, out ItemCatalogEntry entry) ? entry.Name : "Objeto " + id;
            if (icon != null)
            {
                GUI.DrawTexture(inner, icon, ScaleMode.ScaleToFit);
            }
            else
            {
                // Sin arte todavía: las primeras letras del nombre.
                GUI.Label(inner, name.Substring(0, Mathf.Min(4, name.Length)), _darkSmallStyle);
            }

            if (count > 1)
            {
                GUI.Label(new Rect(slot.x, slot.yMax - 20f, slot.width - 6f, 18f), count.ToString(), _darkRightStyle);
            }

            return true;
        }

        /// <summary>Ficha del objeto (panel de tooltip): icono, nombre, tipo y sus números.</summary>
        private void DrawItemTooltip(ushort id, Vector2 mouse)
        {
            if (_dragging || !_catalog.TryGetValue(id, out ItemCatalogEntry item))
            {
                return;
            }

            const float scale = 0.55f;
            float width = TooltipWidth * scale;
            float height = TooltipHeight * scale;
            float x = mouse.x + 16f + width > Screen.width ? mouse.x - width - 16f : mouse.x + 16f;
            float y = Mathf.Min(mouse.y + 12f, Screen.height - height - 4f);
            var panel = new Rect(x, y, width, height);
            GUI.DrawTexture(panel, _darkTooltip);

            Texture2D icon = ItemIcon(id);
            if (icon != null)
            {
                GUI.DrawTexture(Part(panel, scale, 33f, 55f, 104f, 110f), _darkSlotFill);
                GUI.DrawTexture(Part(panel, scale, 45f, 67f, 80f, 86f), icon, ScaleMode.ScaleToFit);
            }

            string kind = item.Kind == ItemKind.Weapon ? "Arma"
                : item.Kind == ItemKind.Armor ? EquipSlotNames[item.EquipSlot]
                : item.Kind == ItemKind.Potion ? "Poción" : "Material";
            GUI.Label(Part(panel, scale, 155f, 62f, 300f, 50f), item.Name, _darkTitleStyle);
            GUI.Label(Part(panel, scale, 155f, 112f, 300f, 40f), kind, _darkSmallStyle);

            var lines = new List<KeyValuePair<string, string>>();
            if (item.Kind == ItemKind.Weapon)
            {
                lines.Add(new KeyValuePair<string, string>("Daño", item.Damage.ToString()));
                lines.Add(new KeyValuePair<string, string>("Alcance", item.Range.ToString("0.0")));
                lines.Add(new KeyValuePair<string, string>("Recarga", item.CooldownSeconds.ToString("0.0") + " s"));
            }
            else if (item.Kind == ItemKind.Potion)
            {
                lines.Add(new KeyValuePair<string, string>("Cura", item.Heal + " de vida"));
            }

            AddStatLine(lines, "Defensa", item.Defense);
            AddStatLine(lines, "Res. mágica", item.MagicResist);
            AddStatLine(lines, "Fuerza", item.Str);
            AddStatLine(lines, "Destreza", item.Dex);
            AddStatLine(lines, "Inteligencia", item.Int);
            AddStatLine(lines, "Vitalidad", item.Vit);
            if (item.MaxStack > 1)
            {
                lines.Add(new KeyValuePair<string, string>("Pila máxima", item.MaxStack.ToString()));
            }

            for (int i = 0; i < lines.Count && i < 9; i++)
            {
                Rect row = Part(panel, scale, 33f, 192f + (i * 32f), 410f, 30f);
                GUI.Label(row, lines[i].Key, _darkTextStyle);
                GUI.Label(row, lines[i].Value, _darkRightStyle);
            }
        }

        private static void AddStatLine(List<KeyValuePair<string, string>> lines, string label, int value)
        {
            if (value != 0)
            {
                lines.Add(new KeyValuePair<string, string>(label, "+" + value));
            }
        }

        // En el orden de EquipSlot (I-14).
        private static readonly string[] EquipSlotNames =
        {
            "Arma", "Escudo / mano secundaria", "Casco", "Armadura", "Pantalones",
            "Guantes", "Botas", "Anillo", "Collar", "Accesorio",
        };

        // Huecos del panel de personaje (91×91 en la imagen original), en el orden de EquipSlot. El
        // arte trae nueve; el accesorio va debajo del arma, donde no hay dibujo.
        private static readonly Vector2[] EquipSlotPositions =
        {
            new Vector2(328f, 449f), new Vector2(328f, 340f), new Vector2(44f, 123f), new Vector2(44f, 231f),
            new Vector2(44f, 340f), new Vector2(44f, 449f), new Vector2(44f, 557f), new Vector2(328f, 231f),
            new Vector2(328f, 123f), new Vector2(328f, 557f),
        };

        /// <summary>
        /// Panel del personaje (C, I-09b): equipo a la izquierda, nivel, experiencia, vida y números a
        /// la derecha, y abajo el reparto de puntos. «+» aparta un punto; Confirmar lo manda y el
        /// servidor decide. Todos los números vienen del último Stats.
        /// </summary>
        private void DrawStats()
        {
            EnsureGuiArt();

            // Tamaño fijo; por defecto, centrado en la mitad izquierda de la pantalla.
            const float scale = 0.62f;
            float panelWidth = CharacterWidth * scale;
            _statsPanel = PlacePanel(
                1,
                ref _statsPosition,
                new Vector2(Mathf.Max(10f, ((Screen.width * 0.5f) - panelWidth) / 2f), 80f),
                new Vector2(panelWidth, CharacterHeight * scale),
                75f * scale);
            GUI.DrawTexture(_statsPanel, _darkCharacter);
            if (LeftButton(Part(_statsPanel, scale, 848f, 17f, 51f, 49f), GUIContent.none, GUIStyle.none))
            {
                _showStats = false;
            }

            Vector2 mouse = Event.current.mousePosition;

            // Huecos de equipo: el objeto equipado tapa la silueta dibujada.
            ushort hoveredEquipment = 0;
            for (int e = 0; e < _equipment.Length && e < EquipSlotPositions.Length; e++)
            {
                Rect equipSlot = Part(_statsPanel, scale, EquipSlotPositions[e].x, EquipSlotPositions[e].y, 91f, 91f);
                SlotInput(equipSlot, EquipSource + e, _equipment[e]);
                if (e == (int)EquipSlot.Accessory)
                {
                    GUI.DrawTexture(equipSlot, _darkSlot);
                }

                if (_equipment[e] != 0)
                {
                    GUI.DrawTexture(new Rect(equipSlot.x + 3f, equipSlot.y + 3f, equipSlot.width - 6f, equipSlot.height - 6f), _darkSlotFill);
                    DrawItemSlot(equipSlot, _equipment[e], 1, drawFrame: false);
                    if (equipSlot.Contains(mouse))
                    {
                        hoveredEquipment = _equipment[e];
                    }
                }
            }

            // Columna derecha: «NIVEL» y «EXP» vienen pintados; aquí van los números y el relleno.
            GUI.Label(Part(_statsPanel, scale, 565f, 104f, 120f, 30f), _stats.Level.ToString(), _darkTitleStyle);
            // Primera barra: vida (con su valor en la fila de NIVEL); segunda: experiencia (fila de EXP).
            int health = Mathf.RoundToInt(_stats.MaxHealth * _ownHealthPercent / 100f);
            GUI.Label(Part(_statsPanel, scale, 620f, 104f, 243f, 30f), "Vida " + health + " / " + _stats.MaxHealth, _darkRightStyle);
            FillBar(Part(_statsPanel, scale, 479f, 144f, 384f, 10f), _ownHealthPercent / 100f, new Color(0.8f, 0.15f, 0.15f, 1f));
            GUI.Label(
                Part(_statsPanel, scale, 560f, 168f, 303f, 26f),
                _experienceToNext == 0 ? "máx." : _experience + " / " + _experienceToNext,
                _darkRightStyle);
            float xpFill = _experienceToNext == 0 ? 1f : (float)_experience / _experienceToNext;
            FillBar(Part(_statsPanel, scale, 479f, 205f, 384f, 10f), xpFill, new Color(0.95f, 0.8f, 0.2f, 1f));

            int pending = _pendingPoints[0] + _pendingPoints[1] + _pendingPoints[2] + _pendingPoints[3];
            int free = _stats.FreePoints - pending;
            float y = 232f;
            const float line = 38f;
            StatLine(scale, y, 0, "Vida máxima", _stats.MaxHealth.ToString());
            StatLine(scale, y += line, 1, "Recurso", _stats.Resource + " / " + _stats.MaxResource);
            StatLine(scale, y += line, 2, "Daño", _stats.AttackDamage + (_stats.DamageType == 1 ? " mágico" : " físico"));
            StatLine(scale, y += line, 3, "Defensa", _stats.Defense.ToString());
            StatLine(scale, y += line, 8, "Res. mágica", _stats.MagicResist.ToString());

            string[] names = { "Fuerza", "Destreza", "Inteligencia", "Vitalidad" };
            ushort[] totals = { _stats.Str, _stats.Dex, _stats.Int, _stats.Vit };
            y += line + 8f;
            for (int i = 0; i < 4; i++, y += line)
            {
                Rect row = Part(_statsPanel, scale, 479f, y, 330f, 34f);
                GUI.Label(row, names[i], _darkTextStyle);
                GUI.Label(row, (totals[i] + _pendingPoints[i]) + (_pendingPoints[i] > 0 ? "  (+" + _pendingPoints[i] + ")" : string.Empty), _darkRightStyle);

                GUI.enabled = free > 0 && !_ownDead;
                if (LeftButton(Part(_statsPanel, scale, 825f, y + 2f, 38f, 30f), "+", _darkButtonStyle))
                {
                    _pendingPoints[i]++;
                }

                GUI.enabled = true;
            }

            // Recuadro inferior (x 31–886, y 673–773): puntos libres y botones.
            GUI.Label(Part(_statsPanel, scale, 45f, 686f, 270f, 34f), "Clase " + _stats.ClassId + " · Libres: " + free, _darkTextStyle);
            bool anyAllocated = _stats.AllocStr + _stats.AllocDex + _stats.AllocInt + _stats.AllocVit > 0;
            GUI.enabled = pending > 0 && !_ownDead;
            if (LeftButton(Part(_statsPanel, scale, 330f, 682f, 170f, 40f), "Confirmar", _darkButtonStyle))
            {
                SendAllocatePoints();
            }

            if (LeftButton(Part(_statsPanel, scale, 510f, 682f, 160f, 40f), "Cancelar", _darkButtonStyle))
            {
                System.Array.Clear(_pendingPoints, 0, _pendingPoints.Length);
            }

            GUI.enabled = anyAllocated && pending == 0 && !_ownDead;
            if (LeftButton(Part(_statsPanel, scale, 680f, 682f, 195f, 40f), _stats.FreeReset != 0 ? "Reajustar" : "Reajustar (piedra)", _darkButtonStyle))
            {
                byte[] bytes = MessageCodec.EncodeResetPoints(new ResetPointsMessage());
                _transport.Send(bytes, bytes.Length, DeliveryMode.ReliableOrdered);
            }

            GUI.enabled = true;
            GUI.Label(
                Part(_statsPanel, scale, 45f, 728f, 830f, 34f),
                _stats.FreeReset != 0 ? "Reajuste gratis hasta el nivel 10" : "Reajustar cuesta una piedra de reajuste",
                _darkSmallStyle);

            if (hoveredEquipment != 0)
            {
                DrawItemTooltip(hoveredEquipment, mouse);
            }
        }

        private void StatLine(float scale, float y, int icon, string label, string value)
        {
            GUI.DrawTexture(Part(_statsPanel, scale, 479f, y + 2f, 30f, 30f), _statIcons[icon], ScaleMode.ScaleToFit);
            Rect row = Part(_statsPanel, scale, 520f, y, 343f, 34f);
            GUI.Label(row, label, _darkTextStyle);
            GUI.Label(row, value, _darkRightStyle);
        }

        private static void FillBar(Rect bar, float fill, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(fill), bar.height), Texture2D.whiteTexture);
            GUI.color = previous;
        }

        private void SendAllocatePoints()
        {
            byte[] bytes = MessageCodec.EncodeAllocatePoints(new AllocatePointsMessage(
                (byte)_pendingPoints[0],
                (byte)_pendingPoints[1],
                (byte)_pendingPoints[2],
                (byte)_pendingPoints[3]));
            _transport.Send(bytes, bytes.Length, DeliveryMode.ReliableOrdered);

            // Si el servidor lo acepta llega un Stats con los números nuevos; si lo rechaza, al limpiar
            // aquí el jugador vuelve a ver sus puntos libres reales.
            System.Array.Clear(_pendingPoints, 0, _pendingPoints.Length);
        }

        /// <summary>
        /// Carga una vez el arte de Resources/GUI: el marco de la barra de vida (Tiny Swords, dibujado en
        /// 9 partes) y las piezas oscuras de Resources/GUI/Dark.
        /// </summary>
        private void EnsureGuiArt()
        {
            if (_barStyle != null)
            {
                return;
            }

            _barStyle = new GUIStyle { border = new RectOffset(24, 24, 0, 0) };
            _barStyle.normal.background = Resources.Load<Texture2D>("GUI/Bar");

            _darkWindow = Resources.Load<Texture2D>("GUI/Dark/Window");
            _darkSlot = Resources.Load<Texture2D>("GUI/Dark/Slot");
            _darkHotbar = Resources.Load<Texture2D>("GUI/Dark/Hotbar");
            _darkCharacter = Resources.Load<Texture2D>("GUI/Dark/Character");
            _darkTooltip = Resources.Load<Texture2D>("GUI/Dark/Tooltip");
            for (int i = 0; i < _statIcons.Length; i++)
            {
                _statIcons[i] = Resources.Load<Texture2D>("GUI/Dark/StatIcon_" + i);
            }

            // Fondo liso del color de los huecos, para tapar las siluetas pintadas del panel.
            _darkSlotFill = new Texture2D(1, 1);
            _darkSlotFill.SetPixel(0, 0, new Color(0.09f, 0.11f, 0.13f, 1f));
            _darkSlotFill.Apply();

            var light = new Color(0.86f, 0.88f, 0.9f);
            _darkTextStyle = new GUIStyle(_overlayStyle) { fontSize = 14, wordWrap = false, alignment = TextAnchor.MiddleLeft };
            _darkTextStyle.normal.textColor = light;
            _darkRightStyle = new GUIStyle(_darkTextStyle) { alignment = TextAnchor.MiddleRight };
            _darkSmallStyle = new GUIStyle(_darkTextStyle) { fontSize = 11, wordWrap = true };
            _darkSmallStyle.normal.textColor = new Color(0.68f, 0.71f, 0.75f);
            _darkTitleStyle = new GUIStyle(_darkTextStyle) { fontSize = 16, fontStyle = FontStyle.Bold };
            // Botón plano con borde de 1 px: el hueco de 121 px no cabe en 9 partes en un botón de 20 px.
            _darkButtonStyle = new GUIStyle
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                border = new RectOffset(1, 1, 1, 1),
                padding = new RectOffset(2, 2, 0, 0),
            };
            _darkButtonStyle.normal.background = FlatButton(new Color(0.15f, 0.18f, 0.22f), new Color(0.55f, 0.58f, 0.63f));
            _darkButtonStyle.hover.background = FlatButton(new Color(0.22f, 0.26f, 0.31f), new Color(0.8f, 0.82f, 0.86f));
            _darkButtonStyle.active.background = FlatButton(new Color(0.1f, 0.12f, 0.15f), new Color(0.95f, 0.8f, 0.2f));
            _darkButtonStyle.normal.textColor = light;
            _darkButtonStyle.hover.textColor = Color.white;
            _darkButtonStyle.active.textColor = new Color(0.95f, 0.8f, 0.2f);
        }

        private static Texture2D FlatButton(Color fill, Color edge)
        {
            var texture = new Texture2D(3, 3) { filterMode = FilterMode.Point };
            for (int y = 0; y < 3; y++)
            {
                for (int x = 0; x < 3; x++)
                {
                    texture.SetPixel(x, y, x == 1 && y == 1 ? fill : edge);
                }
            }

            texture.Apply();
            return texture;
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
            // Encima de la barra rápida (unos 64 px de alto abajo del todo).
            var back = new Rect((Screen.width - width) / 2f, Screen.height - 112f, width, height);

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

            DrawHotbar();

            if (_showStats && _statsReceived)
            {
                DrawStats();
            }

            if (_showInventory)
            {
                DrawInventory();
            }

            HandleDrag();
            if (_discardSlot >= 0)
            {
                DrawDiscardDialog();
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
