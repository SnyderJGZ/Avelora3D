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

        [Header("Cuenta (Etapa F)")]
        [Tooltip("Cuenta registrada en el servidor. La primera vez se crea con el código de invitación.")]
        [SerializeField] private string accountName = "dev";

        [Tooltip("Contraseña de la cuenta. Viaja sin cifrar sobre UDP: úsala solo en localhost hasta la Etapa H.")]
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
        private int _lastDirectionX;
        private long _inputsSent;
        private long _snapshotsReceived;
        private bool _connecting;
        private bool _welcomeReceived;
        private bool _disconnectRequested;
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
            _lastServerTick = 0;
            _lastProcessedSequence = 0;
            _entityId = 0;
            _accumulator = 0;
            _lastReject = null;
            _welcomeReceived = false;
            _disconnectRequested = false;
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

            byte[] bytes = MessageCodec.EncodeMoveInput(new MoveInputMessage(sequence, directionX, directionY));
            _transport.Send(bytes, bytes.Length, DeliveryMode.Sequenced);
            _inputsSent++;
        }

        private void OnTransportConnected()
        {
            if (_transport == null || _map == null || _map.ContentHashBytes == null)
            {
                _status = "conexión sin huella de mapa";
                _disconnectRequested = true;
                return;
            }

            string account = accountName == null ? string.Empty : accountName.Trim();
            string character = string.IsNullOrWhiteSpace(characterName)
                ? account
                : characterName.Trim();

            var hello = new HelloMessage(
                ProtocolConstants.Version,
                (ushort)SimulationConstants.TickRate,
                _map.ContentHashBytes,
                _clientToken,
                account,
                password,
                character,
                invitationCode);

            byte[] bytes = MessageCodec.EncodeHello(hello);
            _transport.Send(bytes, bytes.Length, DeliveryMode.ReliableOrdered);
            _status = "saludando...";
        }

        private void OnTransportDisconnected(DisconnectReason reason)
        {
            _connecting = false;
            _welcomeReceived = false;
            _disconnectRequested = true;

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

                if (_prediction != null)
                {
                    _prediction.Reconcile(authoritative, snapshot.LastProcessedInputSequence);
                }

                break;
            }
        }

        private void RenderLocal()
        {
            float alpha = (float)(_accumulator / SimulationConstants.FixedDt);
            Vec2 position = _prediction.Render(alpha);

            transform.position = new Vector3(position.X, position.Y, transform.position.z);

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
                    view = CreateRemoteView(state.Id);
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

        private RemotePlayerView CreateRemoteView(int entityId)
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
                    return "cuenta o contraseña incorrectas";
                case RejectReason.RegistrationClosed:
                    return "cuenta inexistente o invitación no válida";
                case RejectReason.CharacterInUse:
                    return "ese personaje ya está conectado";
                case RejectReason.LoggedInElsewhere:
                    return "tu cuenta se ha conectado desde otro sitio";
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
            }

            GUI.Box(new Rect(10f, 10f, 540f, 112f), GUIContent.none);
            GUI.Label(
                new Rect(20f, 16f, 520f, 100f),
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
        }
    }
}
