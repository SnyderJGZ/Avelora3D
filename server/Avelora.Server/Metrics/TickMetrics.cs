#nullable enable
using System;

namespace Avelora.Server.Metrics
{
    /// <summary>Foto de las métricas de tick en un instante.</summary>
    /// <remarks>
    /// Es una <c>readonly struct</c> y se pide pocas veces (cada pocos segundos, al informar), no
    /// en cada tick: así ordenar para sacar el p99 no molesta a la simulación.
    /// </remarks>
    public readonly struct TickMetricsSnapshot
    {
        public TickMetricsSnapshot(
            long totalTicks,
            long overBudgetTicks,
            double maxTickMs,
            double meanTickMs,
            double p99TickMs,
            int windowTicks,
            double discardedMs,
            double budgetMs)
        {
            TotalTicks = totalTicks;
            OverBudgetTicks = overBudgetTicks;
            MaxTickMs = maxTickMs;
            MeanTickMs = meanTickMs;
            P99TickMs = p99TickMs;
            WindowTicks = windowTicks;
            DiscardedMs = discardedMs;
            BudgetMs = budgetMs;
        }

        /// <summary>Ticks ejecutados desde el arranque.</summary>
        public long TotalTicks { get; }

        /// <summary>Ticks que pasaron del presupuesto desde el arranque.</summary>
        public long OverBudgetTicks { get; }

        /// <summary>Peor tick desde el arranque, en milisegundos.</summary>
        public double MaxTickMs { get; }

        /// <summary>Media de la ventana, en milisegundos.</summary>
        public double MeanTickMs { get; }

        /// <summary>Percentil 99 de la ventana, en milisegundos.</summary>
        public double P99TickMs { get; }

        /// <summary>Ticks que contiene la ventana.</summary>
        public int WindowTicks { get; }

        /// <summary>Tiempo descartado por el tope anti-espiral, en milisegundos.</summary>
        public double DiscardedMs { get; }

        /// <summary>Presupuesto por tick, en milisegundos (la duración del paso).</summary>
        public double BudgetMs { get; }
    }

    /// <summary>
    /// Métricas del bucle de ticks: media, p99 y cuántos ticks se pasan del presupuesto
    /// (guía del núcleo §11.1).
    /// </summary>
    /// <remarks>
    /// Guarda los últimos <c>windowTicks</c> tiempos en un búfer circular: memoria fija, sin
    /// reservas dentro del tick. La media y el p99 salen de la ventana (lo reciente importa más que
    /// el promedio de toda la vida del proceso), mientras que el peor tick y el contador de
    /// sobrepasos son acumulados, porque un pico aislado no debe perderse.
    ///
    /// El presupuesto es la duración del tick (50 ms a 20 Hz): pasarse significa que el servidor no
    /// llega a tiempo y que la simulación empieza a acumular retraso, que es justo lo que este
    /// contador avisa (criterio de la Etapa H: p99 &lt; 50 ms).
    /// </remarks>
    public sealed class TickMetrics
    {
        private readonly double[] _window;
        private readonly double[] _sortScratch;
        private readonly double _budgetMs;

        private int _next;
        private int _count;
        private double _windowSum;
        private long _total;
        private double _maxTickMs;

        public TickMetrics(int windowTicks, double budgetMs)
        {
            if (windowTicks < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(windowTicks), windowTicks, "La ventana debe ser >= 1.");
            }

            if (budgetMs <= 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(budgetMs), budgetMs, "El presupuesto debe ser positivo.");
            }

            _window = new double[windowTicks];
            _sortScratch = new double[windowTicks];
            _budgetMs = budgetMs;
        }

        /// <summary>Ticks que superaron el presupuesto desde el arranque.</summary>
        public long OverBudgetTicks { get; private set; }

        /// <summary>Peor tick desde el arranque, en milisegundos.</summary>
        public double MaxTickMs => _maxTickMs;

        /// <summary>Tiempo descartado por el tope anti-espiral, en milisegundos.</summary>
        public double DiscardedMs { get; private set; }

        /// <summary>Anota la duración de un tick, en milisegundos.</summary>
        public void Record(double tickMs)
        {
            if (_count == _window.Length)
            {
                _windowSum -= _window[_next];
            }
            else
            {
                _count++;
            }

            _window[_next] = tickMs;
            _windowSum += tickMs;
            _next = (_next + 1) % _window.Length;

            _total++;
            if (tickMs > _maxTickMs)
            {
                _maxTickMs = tickMs;
            }

            if (tickMs > _budgetMs)
            {
                OverBudgetTicks++;
            }
        }

        /// <summary>Anota cuánto tiempo se ha descartado por el tope anti-espiral.</summary>
        public void RecordDiscardedMs(double discardedMs) => DiscardedMs = discardedMs;

        /// <summary>Calcula la foto actual. Reserva memoria, así que se llama al informar, no en el tick.</summary>
        public TickMetricsSnapshot Snapshot()
        {
            double mean = 0d;
            double p99 = 0d;

            if (_count > 0)
            {
                // Mientras la ventana no se ha llenado, las posiciones válidas son 0.._count-1;
                // una vez llena, lo son todas. En ambos casos basta con copiar las _count primeras.
                Array.Copy(_window, _sortScratch, _count);
                Array.Sort(_sortScratch, 0, _count);

                int index = (int)Math.Ceiling(0.99 * _count) - 1;
                if (index < 0)
                {
                    index = 0;
                }

                p99 = _sortScratch[index];
                mean = _windowSum / _count;
            }

            return new TickMetricsSnapshot(
                _total,
                OverBudgetTicks,
                _maxTickMs,
                mean,
                p99,
                _count,
                DiscardedMs,
                _budgetMs);
        }
    }
}
