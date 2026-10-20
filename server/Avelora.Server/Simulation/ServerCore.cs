#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using Avelora.Server.Metrics;

namespace Avelora.Server.Simulation
{
    /// <summary>
    /// El bucle principal del servidor: un hilo, paso fijo, sin esperar a nadie
    /// (guía del núcleo §2.2).
    /// </summary>
    /// <remarks>
    /// Estructura de cada vuelta:
    /// <list type="number">
    /// <item>Mirar cuánto ha pasado de verdad (reloj monótono).</item>
    /// <item>Preguntar al <see cref="FixedStepClock"/> cuántos pasos fijos tocan.</item>
    /// <item>Ejecutar esos pasos, midiendo cuánto tarda cada uno.</item>
    /// <item>Si no tocaba ningún paso, dormir o girar un poco para no quemar CPU.</item>
    /// </list>
    ///
    /// <b>Un solo hilo</b> para la simulación: eso es lo que permite que dentro del tick no haya
    /// ningún <c>lock</c> ni colección concurrente (guía del núcleo §2.1). Lo que llega de la red
    /// entra por colas sin bloqueo, y lo que sale irá por colas por conexión.
    /// </remarks>
    public sealed class ServerCore
    {
        /// <summary>Por debajo de esto, dormir; por encima, girar. Dormir siempre costaría precisión.</summary>
        private const double SpinThresholdMs = 3.0;

        private readonly Zone _zone;
        private readonly FixedStepClock _clock;
        private readonly TickMetrics _metrics;
        private Thread? _thread;

        public ServerCore(Zone zone, double stepMs, int metricsWindowTicks)
        {
            _zone = zone ?? throw new ArgumentNullException(nameof(zone));
            _clock = new FixedStepClock(stepMs);
            _metrics = new TickMetrics(metricsWindowTicks, stepMs);
        }

        /// <summary>Zona que se simula.</summary>
        public Zone Zone => _zone;

        /// <summary>Métricas de duración de tick.</summary>
        public TickMetrics Metrics => _metrics;

        /// <summary>Acumulador de tiempo del bucle.</summary>
        public FixedStepClock Clock => _clock;

        /// <summary>
        /// Se llama al principio y al final de cada tick, en el hilo de simulación. La red lo usa
        /// para dar de alta jugadores y publicar snapshots (Etapa C).
        /// </summary>
        public ITickObserver? Observer { get; set; }

        /// <summary>Arranca el hilo de simulación.</summary>
        public void Start(CancellationToken cancellationToken)
        {
            if (_thread != null)
            {
                throw new InvalidOperationException("El bucle ya se ha arrancado.");
            }

            _thread = new Thread(() => Run(cancellationToken))
            {
                Name = "avelora-sim",
                IsBackground = true,
            };

            _thread.Start();
        }

        /// <summary>Espera a que el hilo termine.</summary>
        public void Join(TimeSpan timeout)
        {
            _thread?.Join(timeout);
        }

        private void Run(CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            double previousMs = stopwatch.Elapsed.TotalMilliseconds;

            while (!cancellationToken.IsCancellationRequested)
            {
                double nowMs = stopwatch.Elapsed.TotalMilliseconds;
                int steps = _clock.Advance(nowMs - previousMs);
                previousMs = nowMs;

                for (int i = 0; i < steps; i++)
                {
                    // La medida abarca el tick entero: altas de jugadores, lectura de intenciones,
                    // simulación y publicación del estado. Es el trabajo real de la vuelta.
                    double tickStartMs = stopwatch.Elapsed.TotalMilliseconds;
                    Observer?.BeforeTick(_zone);
                    _zone.Tick();
                    Observer?.AfterTick(_zone);
                    _metrics.Record(stopwatch.Elapsed.TotalMilliseconds - tickStartMs);
                }

                if (steps == 0)
                {
                    WaitForNextStep();
                }
            }

            _metrics.RecordDiscardedMs(_clock.DiscardedMs);
        }

        /// <summary>
        /// Espera a que llegue el siguiente paso sin gastar CPU de más. Cerca del objetivo gira
        /// (precisión) y lejos duerme (economía): dormir siempre añadiría la resolución del
        /// temporizador del sistema a cada tick, y girar siempre quemaría un núcleo entero.
        /// </summary>
        private void WaitForNextStep()
        {
            if (_clock.PendingMs > SpinThresholdMs)
            {
                Thread.Sleep(1);
            }
            else
            {
                Thread.SpinWait(200);
            }
        }
    }
}
