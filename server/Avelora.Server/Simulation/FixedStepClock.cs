#nullable enable
using System;

namespace Avelora.Server.Simulation
{
    /// <summary>
    /// Acumulador de tiempo que decide cuántos pasos fijos toca ejecutar (guía del núcleo §2.2).
    /// </summary>
    /// <remarks>
    /// Está separado del reloj de verdad a propósito: recibe «cuánto ha pasado» y devuelve «cuántos
    /// pasos», sin leer la hora por su cuenta. Eso permite probar el comportamiento incómodo
    /// (tirones, varias vueltas perdidas, acumulación) sin depender del tiempo real.
    ///
    /// El paso queda fijo por mucho que varíe el tiempo entre vueltas: sin esto, el movimiento del
    /// servidor no coincidiría con la predicción del cliente (guía del núcleo §5.1).
    /// <b>No</b> usa <c>float</c> a propósito: acumular milisegundos en coma flotante de 32 bits
    /// acaba comiendo o regalando ticks.
    /// </remarks>
    public sealed class FixedStepClock
    {
        /// <summary>
        /// Tope anti-espiral: nunca se acumula más de esto por vuelta. Si el proceso se quedó sin
        /// CPU o el equipo se suspendió, no tiene sentido «recuperar» el tiempo perdido ejecutando
        /// cientos de ticks de golpe: se descarta y se sigue (guía del núcleo §2.2).
        /// </summary>
        public const double MaxCatchUpMs = 250.0;

        private readonly double _stepMs;
        private double _accumulatedMs;

        public FixedStepClock(double stepMs)
        {
            if (stepMs <= 0d || double.IsNaN(stepMs))
            {
                throw new ArgumentOutOfRangeException(nameof(stepMs), stepMs, "El paso debe ser positivo.");
            }

            _stepMs = stepMs;
        }

        /// <summary>Duración de un paso, en milisegundos.</summary>
        public double StepMs => _stepMs;

        /// <summary>Tiempo acumulado que todavía no llega a un paso entero.</summary>
        public double AccumulatedMs => _accumulatedMs;

        /// <summary>Tiempo descartado por el tope anti-espiral desde el arranque.</summary>
        public double DiscardedMs { get; private set; }

        /// <summary>Cuánto falta hasta el siguiente paso, en milisegundos.</summary>
        public double PendingMs => _stepMs - _accumulatedMs;

        /// <summary>
        /// Añade el tiempo transcurrido y devuelve cuántos pasos hay que ejecutar.
        /// </summary>
        /// <param name="elapsedMs">Milisegundos desde la última llamada. Los valores negativos se ignoran.</param>
        public int Advance(double elapsedMs)
        {
            if (elapsedMs > 0d)
            {
                if (elapsedMs > MaxCatchUpMs)
                {
                    DiscardedMs += elapsedMs - MaxCatchUpMs;
                    elapsedMs = MaxCatchUpMs;
                }

                _accumulatedMs += elapsedMs;
            }

            int steps = 0;
            while (_accumulatedMs >= _stepMs)
            {
                _accumulatedMs -= _stepMs;
                steps++;
            }

            return steps;
        }

        /// <summary>Olvida el tiempo acumulado (por ejemplo, tras una pausa larga controlada).</summary>
        public void Reset()
        {
            _accumulatedMs = 0d;
        }
    }
}
