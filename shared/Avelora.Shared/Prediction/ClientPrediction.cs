#nullable enable
using System;
using System.Collections.Generic;
using Avelora.Shared.Protocol;

namespace Avelora.Shared.Prediction
{
    /// <summary>
    /// Predicción del jugador local: aplica cada intención en el acto, guarda las que el servidor
    /// todavía no ha confirmado y las vuelve a aplicar cuando llega un snapshot (plan §5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// El cliente y el servidor ejecutan <b>el mismo</b> <see cref="MovementSimulator"/> con el
    /// mismo <c>dt</c>, la misma velocidad y el mismo mapa. Mientras no haya correcciones, la
    /// posición predicha coincide con la del servidor. Cuando un snapshot confirma parte de los
    /// inputs, se descartan los confirmados y se reaplican los pendientes desde la posición
    /// autoritativa: así el jugador ve su personaje al instante y, aun así, la verdad del servidor
    /// manda.
    /// </para>
    /// <para>
    /// La diferencia de cuantización (hasta <see cref="ProtocolConstants.MaxQuantizationError"/>
    /// por eje) se ignora: corregirla en cada snapshot haría vibrar al personaje. Los errores
    /// pequeños de verdad (un input perdido, por ejemplo) se absorben con un deslizamiento visual
    /// corto; los grandes (una pared, un teletransporte) se saltan.
    /// </para>
    /// <para>
    /// El dibujo interpola entre los dos últimos pasos fijos (<see cref="Render"/>), igual que
    /// hacía el controlador de la Etapa A: la simulación sigue siendo discreta a 20 Hz, pero se
    /// ve continua.
    /// </para>
    /// </remarks>
    public sealed class ClientPrediction
    {
        /// <summary>Velocidad a la que se absorbe una corrección pequeña, en tiles por segundo.</summary>
        public const float DefaultCorrectionSpeed = 10f;

        /// <summary>
        /// Distancia a partir de la cual la corrección se aplica de golpe en vez de deslizarse.
        /// Un paso mide 0,2 tiles; medio tile separa un error normal de uno grave.
        /// </summary>
        public const float DefaultSnapDistance = 0.5f;

        /// <summary>Tope de inputs sin confirmar. A 20 Hz son 6,4 s: de sobra para cualquier RTT.</summary>
        public const int DefaultMaxPendingInputs = 128;

        private struct PendingInput
        {
            public PendingInput(uint sequence, sbyte directionX, sbyte directionY)
            {
                Sequence = sequence;
                DirectionX = directionX;
                DirectionY = directionY;
            }

            public uint Sequence { get; }

            public sbyte DirectionX { get; }

            public sbyte DirectionY { get; }
        }

        private readonly MapCollisionGrid _map;
        private readonly Vec2 _halfSize;
        private readonly float _speed;
        private readonly float _dt;
        private readonly int _maxPendingInputs;
        private readonly float _correctionSpeed;
        private readonly float _snapDistance;
        private readonly List<PendingInput> _pending;

        private uint _lastSequence;
        private Vec2 _visualOffset;

        public ClientPrediction(
            MapCollisionGrid map,
            Vec2 halfSize,
            float speed,
            float dt,
            int maxPendingInputs = DefaultMaxPendingInputs,
            float correctionSpeed = DefaultCorrectionSpeed,
            float snapDistance = DefaultSnapDistance)
        {
            if (map is null)
            {
                throw new ArgumentNullException(nameof(map));
            }

            if (speed < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(speed), speed, "La velocidad no puede ser negativa.");
            }

            if (dt <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(dt), dt, "El paso de simulación debe ser positivo.");
            }

            if (halfSize.X < 0f || halfSize.Y < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(halfSize), halfSize, "La caja no puede tener tamaño negativo.");
            }

            if (maxPendingInputs < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPendingInputs), maxPendingInputs, "Debe caber al menos un input.");
            }

            if (correctionSpeed <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(correctionSpeed), correctionSpeed, "La velocidad de corrección debe ser positiva.");
            }

            if (snapDistance <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(snapDistance), snapDistance, "La distancia de salto debe ser positiva.");
            }

            _map = map;
            _halfSize = halfSize;
            _speed = speed;
            _dt = dt;
            _maxPendingInputs = maxPendingInputs;
            _correctionSpeed = correctionSpeed;
            _snapDistance = snapDistance;
            _pending = new List<PendingInput>(maxPendingInputs);
        }

        /// <summary>Posición predicha más reciente (la que debería tener el servidor tras estos inputs).</summary>
        public Vec2 Position { get; private set; }

        /// <summary>Posición predicha antes del último paso; base de la interpolación del dibujo.</summary>
        public Vec2 PreviousPosition { get; private set; }

        /// <summary>Desplazamiento temporal que se suma al dibujo para absorber una corrección.</summary>
        public Vec2 VisualOffset => _visualOffset;

        /// <summary>Último número de secuencia generado (0 = ninguno).</summary>
        public uint LastSequence => _lastSequence;

        /// <summary>Inputs enviados que el servidor todavía no ha confirmado.</summary>
        public int PendingCount => _pending.Count;

        /// <summary>Correcciones suaves aplicadas desde el último <see cref="Reset"/>.</summary>
        public long Corrections { get; private set; }

        /// <summary>Correcciones grandes aplicadas de golpe desde el último <see cref="Reset"/>.</summary>
        public long Snaps { get; private set; }

        /// <summary>Inputs que se descartaron por llenarse el búfer (solo pasa si no llegan snapshots).</summary>
        public long DroppedInputs { get; private set; }

        /// <summary>Vuelve a empezar en <paramref name="position"/> (aparición o reconexión).</summary>
        public void Reset(Vec2 position)
        {
            Position = position;
            PreviousPosition = position;
            _visualOffset = Vec2.Zero;
            _lastSequence = 0;
            _pending.Clear();
        }

        /// <summary>
        /// Genera el siguiente input, lo aplica en el acto y lo guarda hasta que el servidor lo
        /// confirme. Devuelve el número de secuencia que hay que mandar.
        /// </summary>
        public uint AddInput(sbyte directionX, sbyte directionY)
        {
            if (!MovementSimulator.IsValidDirection(directionX, directionY))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(directionX),
                    "La dirección debe ser -1, 0 o 1 en cada eje.");
            }

            unchecked
            {
                _lastSequence++;
            }

            // El 0 significa «ninguna secuencia» en el protocolo: al dar la vuelta se salta.
            if (_lastSequence == 0)
            {
                _lastSequence = 1;
            }

            PreviousPosition = Position;
            Position = MovementSimulator.Step(
                Position,
                directionX,
                directionY,
                _speed,
                _dt,
                _halfSize,
                _map);

            _pending.Add(new PendingInput(_lastSequence, directionX, directionY));

            if (_pending.Count > _maxPendingInputs)
            {
                // Sin snapshots no se puede reconciliar; se conserva la ventana más reciente y se
                // deja que la próxima posición autoritativa enderece el rumbo.
                _pending.RemoveAt(0);
                DroppedInputs++;
            }

            return _lastSequence;
        }

        /// <summary>
        /// Reconcilia contra la verdad del servidor: descarta los inputs confirmados, reaplica los
        /// pendientes y decide si la diferencia se absorbe deslizando o de golpe.
        /// </summary>
        /// <param name="authoritativePosition">Posición del jugador en el snapshot (descuantizada).</param>
        /// <param name="lastProcessedInputSequence">Último input que el servidor aplicó a este jugador.</param>
        /// <returns><c>true</c> si la posición predicha cambió.</returns>
        public bool Reconcile(Vec2 authoritativePosition, uint lastProcessedInputSequence)
        {
            while (_pending.Count > 0 &&
                   !SequenceMath.IsNewer(_pending[0].Sequence, lastProcessedInputSequence))
            {
                _pending.RemoveAt(0);
            }

            Vec2 previous = authoritativePosition;
            Vec2 current = authoritativePosition;

            for (int i = 0; i < _pending.Count; i++)
            {
                PendingInput input = _pending[i];
                previous = current;
                current = MovementSimulator.Step(
                    current,
                    input.DirectionX,
                    input.DirectionY,
                    _speed,
                    _dt,
                    _halfSize,
                    _map);
            }

            if (_pending.Count == 0)
            {
                previous = current;
            }

            Vec2 delta = current - Position;

            // El servidor cuantiza a 1/64 de tile: medio paso por eje es ruido, no un error. Si se
            // corrigiera, el personaje vibraría con cada snapshot (plan §5 y constantes).
            const float Epsilon = 1e-5f;
            float tolerance = ProtocolConstants.MaxQuantizationError + Epsilon;
            if (MathF.Abs(delta.X) <= tolerance && MathF.Abs(delta.Y) <= tolerance)
            {
                return false;
            }

            Vec2 visualBefore = Position + _visualOffset;
            Position = current;
            PreviousPosition = previous;

            if (delta.Length > _snapDistance)
            {
                // Error grande: se acepta el salto. Suavizarlo parecería un deslizamiento por el
                // mapa y taparía un problema real (pared, teletransporte, trampa).
                _visualOffset = Vec2.Zero;
                Snaps++;
            }
            else
            {
                // La diferencia se reparte en unos milisegundos: el personaje llega a la posición
                // autoritativa sin que se note el tirón.
                _visualOffset = visualBefore - current;
                Corrections++;
            }

            return true;
        }

        /// <summary>Desvanece la corrección visual. Se llama una vez por frame, no por tick.</summary>
        public void AdvanceVisual(float dt)
        {
            if (dt <= 0f)
            {
                return;
            }

            float length = _visualOffset.Length;
            float maxStep = _correctionSpeed * dt;

            if (length <= maxStep)
            {
                _visualOffset = Vec2.Zero;
                return;
            }

            _visualOffset *= (length - maxStep) / length;
        }

        /// <summary>
        /// Posición que se dibuja. <paramref name="alpha"/> es la fracción del paso fijo que ya ha
        /// transcurrido (0 = recién dado el último paso, 1 = a punto del siguiente).
        /// </summary>
        public Vec2 Render(float alpha)
        {
            if (alpha < 0f)
            {
                alpha = 0f;
            }
            else if (alpha > 1f)
            {
                alpha = 1f;
            }

            Vec2 interpolated = PreviousPosition + ((Position - PreviousPosition) * alpha);
            return interpolated + _visualOffset;
        }
    }
}
