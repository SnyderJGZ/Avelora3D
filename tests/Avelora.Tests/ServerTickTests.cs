using System;
using Avelora.Server.Metrics;
using Avelora.Server.Simulation;
using Avelora.Shared;
using Xunit;

namespace Avelora.Tests
{
    public class FixedStepClockTests
    {
        private static readonly double Step = SimulationConstants.StepMs;

        [Fact]
        public void A_20_Hz_Toca_Un_Paso_Cada_50_Ms()
        {
            var clock = new FixedStepClock(Step);

            Assert.Equal(0, clock.Advance(0d));
            Assert.Equal(0, clock.Advance(25d));
            Assert.Equal(0, clock.Advance(24d));
            Assert.Equal(1, clock.Advance(1d));
            Assert.Equal(0, clock.Advance(25d));
            Assert.Equal(1, clock.Advance(25d));
        }

        [Fact]
        public void Los_Restos_Se_Acumulan_En_Vez_De_Perderse()
        {
            var clock = new FixedStepClock(Step);

            // Diecinueve vueltas de 5 ms no llegan a un paso (95 ms)...
            int steps = 0;
            for (int i = 0; i < 19; i++)
            {
                steps += clock.Advance(2.5d);
            }

            Assert.Equal(0, steps);

            // ...y la vigésima lo completa, sin haber perdido nada por el camino.
            Assert.Equal(1, clock.Advance(2.5d));
            Assert.Equal(Step, clock.PendingMs, 9);
        }

        [Fact]
        public void Un_Tiron_Largo_No_Provoca_Una_Espiral_De_Ticks()
        {
            // El equipo se durmió 5 segundos: no se recuperan 100 ticks de golpe.
            var clock = new FixedStepClock(Step);

            int steps = clock.Advance(5000d);

            Assert.Equal((int)(FixedStepClock.MaxCatchUpMs / Step), steps);
            Assert.Equal(5000d - FixedStepClock.MaxCatchUpMs, clock.DiscardedMs, 6);
        }

        [Fact]
        public void El_Tiempo_Negativo_Se_Ignora()
        {
            var clock = new FixedStepClock(Step);
            clock.Advance(30d);

            Assert.Equal(0, clock.Advance(-10d));
            Assert.Equal(1, clock.Advance(20d));
        }

        [Fact]
        public void El_Paso_Debe_Ser_Positivo()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new FixedStepClock(0d));
            Assert.Throws<ArgumentOutOfRangeException>(() => new FixedStepClock(-1d));
            Assert.Throws<ArgumentOutOfRangeException>(() => new FixedStepClock(double.NaN));
        }

        [Fact]
        public void Reset_Olvida_Lo_Acumulado()
        {
            var clock = new FixedStepClock(Step);
            clock.Advance(40d);

            clock.Reset();

            Assert.Equal(0, clock.Advance(40d));
        }
    }

    public class TickMetricsTests
    {
        [Fact]
        public void Cuenta_Los_Ticks_Y_Los_Que_Se_Pasan_De_Presupuesto()
        {
            var metrics = new TickMetrics(windowTicks: 10, budgetMs: 50d);

            metrics.Record(10d);
            metrics.Record(20d);
            metrics.Record(60d);
            metrics.Record(49.9d);
            metrics.Record(50.1d);

            TickMetricsSnapshot snapshot = metrics.Snapshot();

            Assert.Equal(5, snapshot.TotalTicks);
            Assert.Equal(2, snapshot.OverBudgetTicks);
            Assert.Equal(60d, snapshot.MaxTickMs, 6);
            Assert.Equal(50d, snapshot.BudgetMs, 6);
        }

        [Fact]
        public void La_Media_Y_El_P99_Salen_De_La_Ventana()
        {
            var metrics = new TickMetrics(windowTicks: 100, budgetMs: 50d);

            for (int i = 1; i <= 100; i++)
            {
                metrics.Record(i);
            }

            TickMetricsSnapshot snapshot = metrics.Snapshot();

            Assert.Equal(100, snapshot.WindowTicks);
            Assert.Equal(50.5d, snapshot.MeanTickMs, 6);
            Assert.Equal(99d, snapshot.P99TickMs, 6);
            Assert.Equal(100d, snapshot.MaxTickMs, 6);
        }

        [Fact]
        public void La_Ventana_No_Crece_Y_Solo_Guarda_Lo_Ultimo()
        {
            var metrics = new TickMetrics(windowTicks: 4, budgetMs: 50d);

            for (int i = 0; i < 1000; i++)
            {
                metrics.Record(1d);
            }

            // Solo el último valor, muy distinto, debe notarse en la ventana...
            metrics.Record(9d);

            TickMetricsSnapshot snapshot = metrics.Snapshot();

            Assert.Equal(4, snapshot.WindowTicks);
            Assert.Equal(1001, snapshot.TotalTicks);

            // ...pero el peor tick de toda la vida del proceso no se olvida.
            Assert.Equal(9d, snapshot.MaxTickMs, 6);
        }

        [Fact]
        public void El_Primer_Tick_No_Se_Pierde()
        {
            var metrics = new TickMetrics(windowTicks: 10, budgetMs: 50d);

            metrics.Record(7d);

            Assert.Equal(1, metrics.Snapshot().WindowTicks);
            Assert.Equal(7d, metrics.Snapshot().MeanTickMs, 6);
            Assert.Equal(7d, metrics.Snapshot().P99TickMs, 6);
        }

        [Fact]
        public void Sin_Ticks_La_Foto_Esta_Vacia_Pero_Es_Valida()
        {
            var snapshot = new TickMetrics(windowTicks: 10, budgetMs: 50d).Snapshot();

            Assert.Equal(0, snapshot.TotalTicks);
            Assert.Equal(0, snapshot.WindowTicks);
            Assert.Equal(0d, snapshot.MeanTickMs, 6);
            Assert.Equal(0d, snapshot.P99TickMs, 6);
        }

        [Fact]
        public void El_Tiempo_Descartado_Se_Anota()
        {
            var metrics = new TickMetrics(windowTicks: 10, budgetMs: 50d);

            metrics.RecordDiscardedMs(123.5d);

            Assert.Equal(123.5d, metrics.Snapshot().DiscardedMs, 6);
        }

        [Fact]
        public void La_Ventana_Y_El_Presupuesto_Deben_Tener_Sentido()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TickMetrics(0, 50d));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TickMetrics(10, 0d));
        }
    }
}
