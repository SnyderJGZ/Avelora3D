using Avelora.Shared;
using UnityEngine;

namespace Avelora.Client
{
    /// <summary>
    /// Jugador local de la Etapa A: todavía <b>sin red</b>.
    /// </summary>
    /// <remarks>
    /// Lee la dirección de las teclas y avanza con <see cref="MovementSimulator"/>, que es el
    /// mismo código que usará el servidor para decidir la verdad. La simulación va a paso fijo
    /// (20 Hz, igual que el futuro tick del servidor) y lo que se dibuja se interpola entre los
    /// dos últimos pasos para que no se vea a saltos (guía del núcleo §5.1 y §5.2).
    ///
    /// Cuando llegue la Etapa D, este mismo bucle será la predicción: los inputs se guardarán con
    /// su número de secuencia y se reconciliarán contra los snapshots del servidor.
    /// </remarks>
    [RequireComponent(typeof(AveloraMap))]
    public sealed class LocalPlayerController : MonoBehaviour
    {
        /// <summary>
        /// Paso de simulación. Debe coincidir con el tick del servidor, así que el valor sale de
        /// <see cref="SimulationConstants"/>: cambiarlo en un solo sitio (la librería compartida)
        /// evita que cliente y servidor se desincronicen sin que nadie se dé cuenta.
        /// </summary>
        public const float FixedDt = SimulationConstants.FixedDt;

        /// <summary>Tope anti-espiral: nunca acumulamos más de esto en un fotograma.</summary>
        private const double MaxFrameSeconds = 0.25;

        [Header("Movimiento")]
        [Tooltip("Velocidad en unidades (tiles) por segundo. La impone el servidor; el cliente no la manda.")]
        [SerializeField] private float speed = 4f;

        [Tooltip("Semiancho y semialto de la caja de colisión.")]
        [SerializeField] private Vector2 halfSize = new Vector2(0.4f, 0.4f);

        [Header("Entrada de pruebas (sin teclado)")]
        [Tooltip("Si está activo, ignora el teclado y usa debugDirX/debugDirY (para pruebas automáticas).")]
        [SerializeField] private bool useDebugInput;
        [SerializeField] private int debugDirX;
        [SerializeField] private int debugDirY;

        private AveloraMap _map;
        private Vector2 _current;
        private Vector2 _previous;
        private double _accumulator;

        /// <summary>Posición simulada actual, en unidades de mundo (la "verdad" local).</summary>
        public Vector2 Position => _current;

        /// <summary>Caja de colisión del jugador.</summary>
        public Vector2 HalfSize => halfSize;

        /// <summary>Pasos en los que el eje X no pudo avanzar porque había una pared.</summary>
        public int BlockedXSteps { get; private set; }

        /// <summary>Pasos en los que el eje Y no pudo avanzar porque había una pared.</summary>
        public int BlockedYSteps { get; private set; }

        public float Speed
        {
            get => speed;
            set => speed = value;
        }

        /// <inheritdoc cref="useDebugInput"/>
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
        }

        private void Start()
        {
            _current = _previous = new Vector2(transform.position.x, transform.position.y);
            _accumulator = 0d;
        }

        private void Update()
        {
            MapCollisionGrid grid = _map != null ? _map.Grid : null;
            if (grid == null)
            {
                return;
            }

            double frame = Time.deltaTime;
            if (frame > MaxFrameSeconds)
            {
                frame = MaxFrameSeconds;
            }

            _accumulator += frame;

            ReadDirection(out sbyte dirX, out sbyte dirY);
            var half = new Vec2(halfSize.x, halfSize.y);

            while (_accumulator >= FixedDt)
            {
                StepOnce(dirX, dirY, grid, half);
                _accumulator -= FixedDt;
            }

            Render();
        }

        /// <summary>
        /// Avanza <paramref name="steps"/> pasos fijos con la dirección dada, sin depender del
        /// reloj. Es una costura para pruebas automáticas (y para reproducir una secuencia exacta
        /// de inputs cuando llegue el servidor). Devuelve la posición resultante.
        /// </summary>
        public Vector2 SimulateSteps(int steps, int dirX, int dirY)
        {
            MapCollisionGrid grid = _map != null ? _map.Grid : null;
            if (grid == null)
            {
                return _current;
            }

            var half = new Vec2(halfSize.x, halfSize.y);
            sbyte dx = (sbyte)Mathf.Clamp(dirX, -1, 1);
            sbyte dy = (sbyte)Mathf.Clamp(dirY, -1, 1);

            for (int i = 0; i < steps; i++)
            {
                StepOnce(dx, dy, grid, half);
            }

            // El reloj vuelve a empezar: lo dibujado es exactamente lo simulado.
            _previous = _current;
            _accumulator = 0d;
            Render();
            return _current;
        }

        private void StepOnce(sbyte dirX, sbyte dirY, MapCollisionGrid grid, Vec2 half)
        {
            _previous = _current;

            Vec2 next = MovementSimulator.Step(
                new Vec2(_current.x, _current.y),
                dirX,
                dirY,
                speed,
                FixedDt,
                half,
                grid);

            if (dirX != 0 && Mathf.Approximately(next.X, _current.x))
            {
                BlockedXSteps++;
            }

            if (dirY != 0 && Mathf.Approximately(next.Y, _current.y))
            {
                BlockedYSteps++;
            }

            _current = new Vector2(next.X, next.Y);
        }

        /// <summary>
        /// Dirección de este paso. Es la única lectura de teclado del controlador: cuando exista un
        /// InputManager (Etapa D) se sustituye aquí por la intención local, sin tocar el bucle.
        /// </summary>
        private void ReadDirection(out sbyte dirX, out sbyte dirY)
        {
            if (useDebugInput)
            {
                dirX = (sbyte)Mathf.Clamp(debugDirX, -1, 1);
                dirY = (sbyte)Mathf.Clamp(debugDirY, -1, 1);
                return;
            }

            KeyboardDirection.Read(out dirX, out dirY);
        }

        private void Render()
        {
            // Interpola entre los dos últimos pasos fijos: la simulación sigue siendo discreta,
            // pero el dibujo es continuo (guía del núcleo §5.1).
            float t = (float)(_accumulator / FixedDt);
            Vector2 pos = Vector2.Lerp(_previous, _current, t);
            transform.position = new Vector3(pos.x, pos.y, transform.position.z);
        }

        private void OnDrawGizmosSelected()
        {
            Vector2 p = Application.isPlaying ? _current : new Vector2(transform.position.x, transform.position.y);
            Gizmos.color = new Color(1f, 0.6f, 0.1f);
            Gizmos.DrawWireCube(
                new Vector3(p.x, p.y, 0f),
                new Vector3(halfSize.x * 2f, halfSize.y * 2f, 0f));
        }
    }
}
