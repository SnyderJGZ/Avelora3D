using System.Collections.Generic;
using System.IO;
using Avelora.Bots;
using Xunit;

namespace Avelora.Tests
{
    /// <summary>
    /// Pruebas de la comparación traza ↔ Welcome de G-05, sin red: la misma clase que usa
    /// <c>caida.ps1</c> a través de <c>--compare-trace</c>.
    /// </summary>
    public class TraceCompareTests
    {
        [Fact]
        public void La_Ultima_Coincidencia_Da_La_Perdida_Y_La_Cuenta_Queda_Ok()
        {
            var trace = new List<TraceSample>
            {
                new TraceSample(10_000, "bot1", -1.5f, -2.5f),
                new TraceSample(20_000, "bot1", 0.2f, -1.0f),
                new TraceSample(55_000, "bot1", 1.5f, -2.0f),
                new TraceSample(56_000, "bot1", 2.0f, -2.0f),
            };

            var welcomes = new List<WelcomeSpawn>
            {
                new WelcomeSpawn("bot1", 1.5f, -2.0f),
            };

            TraceComparison comparison = TraceComparer.Compare(
                trace,
                welcomes,
                deathUtcMs: 60_000,
                maxLossSeconds: 65d);

            Assert.True(comparison.AllOk);
            Assert.Equal(1, comparison.OkCount);

            TraceLossRow row = comparison.Rows[0];
            Assert.True(row.Ok);
            Assert.Equal(55_000, row.MatchedUtcMs);
            Assert.Equal(5d, row.LossSeconds!.Value, 3);
        }

        [Fact]
        public void Sin_Coincidencia_La_Cuenta_Falla()
        {
            var trace = new List<TraceSample>
            {
                new TraceSample(10_000, "bot1", -1.5f, -2.5f),
                new TraceSample(20_000, "bot1", 0.2f, -1.0f),
                new TraceSample(55_000, "bot1", 1.5f, -2.0f),
            };

            // El servidor guardó otra posición (por ejemplo, la de otra cuenta).
            var welcomes = new List<WelcomeSpawn>
            {
                new WelcomeSpawn("bot1", 4.5f, 3.5f),
            };

            TraceComparison comparison = TraceComparer.Compare(
                trace,
                welcomes,
                deathUtcMs: 60_000,
                maxLossSeconds: 65d);

            Assert.False(comparison.AllOk);

            TraceLossRow row = comparison.Rows[0];
            Assert.False(row.Ok);
            Assert.Null(row.LossSeconds);
            Assert.Null(row.MatchedUtcMs);
        }

        [Fact]
        public void Una_Coincidencia_Demasiado_Vieja_Supera_El_Tope()
        {
            var trace = new List<TraceSample>
            {
                new TraceSample(10_000, "bot1", 1.5f, -2.0f),
                new TraceSample(50_000, "bot1", 3.5f, 1.0f),
            };

            var welcomes = new List<WelcomeSpawn>
            {
                new WelcomeSpawn("bot1", 1.5f, -2.0f),
            };

            TraceComparison comparison = TraceComparer.Compare(
                trace,
                welcomes,
                deathUtcMs: 150_000,
                maxLossSeconds: 65d);

            Assert.False(comparison.AllOk);

            TraceLossRow row = comparison.Rows[0];
            Assert.False(row.Ok);
            Assert.Equal(140d, row.LossSeconds!.Value, 3);
        }

        [Fact]
        public void Solo_Cuentan_Las_Muestras_Anteriores_A_La_Muerte()
        {
            var trace = new List<TraceSample>
            {
                new TraceSample(30_000, "bot1", 2f, 2f),
                new TraceSample(70_000, "bot1", 1.5f, -2.0f),
            };

            var welcomes = new List<WelcomeSpawn>
            {
                new WelcomeSpawn("bot1", 1.5f, -2.0f),
            };

            TraceComparison comparison = TraceComparer.Compare(
                trace,
                welcomes,
                deathUtcMs: 60_000,
                maxLossSeconds: 65d);

            Assert.Null(comparison.Rows[0].LossSeconds);
            Assert.False(comparison.Rows[0].Ok);
        }

        [Fact]
        public void La_Traza_Y_Los_Welcome_Se_Leen_De_Sus_Archivos()
        {
            string tracePath = Path.GetTempFileName();
            string welcomePath = Path.GetTempFileName();

            try
            {
                File.WriteAllText(
                    tracePath,
                    "10000;bot1;-1.50000;-2.50000\n" +
                    "11000;bot1;0.20000;-1.00000\n" +
                    "basura sin campos\n");

                File.WriteAllText(
                    welcomePath,
                    "bot1;-1.50000;-2.50000\n");

                List<TraceSample> trace = TraceComparer.ReadTrace(tracePath);
                List<WelcomeSpawn> welcomes = TraceComparer.ReadWelcomes(welcomePath);

                Assert.Equal(2, trace.Count);
                Assert.Single(welcomes);
                Assert.Equal("bot1", welcomes[0].Account);
                Assert.Equal(-1.5f, welcomes[0].X);
                Assert.Equal(-2.5f, welcomes[0].Y);
            }
            finally
            {
                File.Delete(tracePath);
                File.Delete(welcomePath);
            }
        }

        [Fact]
        public void La_Tabla_Muestra_Ok_Y_Fallo_Por_Cuenta()
        {
            var trace = new List<TraceSample>
            {
                new TraceSample(55_000, "bot1", 1f, 1f),
            };

            var welcomes = new List<WelcomeSpawn>
            {
                new WelcomeSpawn("bot1", 1f, 1f),
                new WelcomeSpawn("bot2", 9f, 9f),
            };

            TraceComparison comparison = TraceComparer.Compare(trace, welcomes, 60_000, 65d);

            using var writer = new StringWriter();

            TraceComparer.WriteTable(writer, comparison);
            string text = writer.ToString();

            Assert.Contains("bot1", text);
            Assert.Contains("OK", text);
            Assert.Contains("bot2", text);
            Assert.Contains("FALLO", text);
            Assert.Contains("Cuentas OK : 1/2", text);
        }
    }
}
