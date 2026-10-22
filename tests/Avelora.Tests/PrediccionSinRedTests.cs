using System;
using System.IO;
using System.Text;
using Avelora.Server.Configuration;
using Avelora.Shared;
using Avelora.Shared.Maps;
using Avelora.Shared.Prediction;
using Avelora.Shared.Protocol;
using Xunit;

namespace Avelora.Tests
{
    /// <summary>
    /// G-04: reconciliación contra la posición cuantizada del snapshot sin pérdida de paquetes.
    /// </summary>
    /// <remarks>
    /// Simula un servidor con la posición <b>exacta</b> y un cliente que predice con
    /// <see cref="ClientPrediction"/> y reconcilia contra la posición <b>cuantizada</b> a 1/64 de
    /// tile, con 1 a 3 inputs pendientes, sobre el mapa real y con la misma velocidad y caja que el
    /// servidor de carga. No hay pérdida ni desorden: cualquier corrección que aparezca aquí es
    /// ruido de reconciliación amplificado por el roce con las paredes, no un problema de red.
    /// </remarks>
    public class PrediccionSinRedTests
    {
        private const float Speed = 4f;
        private static readonly Vec2 Half = new Vec2(0.4f, 0.4f);

        /// <summary>Pasos simulados: 300 s a 20 Hz, de sobra para recorrer la sala entera.</summary>
        private const int Steps = 6000;

        private static string MapPath =>
            Path.Combine(AppContext.BaseDirectory, "maps", "avelora-etapa-a.avmap");

        private static Vec2 Quantize(Vec2 position) => new Vec2(
            ProtocolConstants.DequantizePosition(ProtocolConstants.QuantizePosition(position.X)),
            ProtocolConstants.DequantizePosition(ProtocolConstants.QuantizePosition(position.Y)));

        [Fact]
        public void Sin_Red_La_Reconciliacion_No_Corrige_Al_Rozar_Las_Paredes()
        {
            MapCollisionGrid map = MapFile.Load(MapPath);
            Vec2 spawn = new ServerConfig().Spawn;

            var prediction = new ClientPrediction(map, Half, Speed, SimulationConstants.FixedDt);
            prediction.Reset(spawn);

            // Posición exacta del servidor después de aplicar la secuencia s (índice s).
            var server = new Vec2[Steps + 1];
            Vec2 serverPosition = spawn;

            var random = new Random(20261020);
            sbyte directionX = 0;
            sbyte directionY = 0;
            int nextDirectionChange = 0;

            var examples = new StringBuilder();
            int exampleCount = 0;

            for (int tick = 0; tick < Steps; tick++)
            {
                if (tick >= nextDirectionChange)
                {
                    directionX = (sbyte)random.Next(-1, 2);
                    directionY = (sbyte)random.Next(-1, 2);
                    nextDirectionChange = tick + 20 + random.Next(30);
                }

                uint sequence = prediction.AddInput(directionX, directionY);
                serverPosition = MovementSimulator.Step(
                    serverPosition,
                    directionX,
                    directionY,
                    Speed,
                    SimulationConstants.FixedDt,
                    Half,
                    map);

                server[sequence] = serverPosition;

                // El snapshot llega 1, 2 o 3 ticks tarde: el cliente reaplica 1..3 pendientes desde
                // la base cuantizada, que es justo donde se rompía la igualdad con el servidor.
                int lag = 1 + (tick % 3);
                if (tick < lag)
                {
                    continue;
                }

                uint confirmed = sequence - (uint)lag;
                Vec2 basePosition = Quantize(server[confirmed]);

                long before = prediction.Corrections + prediction.Snaps;
                prediction.Reconcile(basePosition, confirmed);

                if (prediction.Corrections + prediction.Snaps > before && exampleCount < 20)
                {
                    exampleCount++;
                    examples.Append("  seq=").Append(confirmed)
                        .Append(" pend=").Append(prediction.LastCorrectionPendingInputs)
                        .Append(" base=(").Append(basePosition.X.ToString("0.00000"))
                        .Append("; ").Append(basePosition.Y.ToString("0.00000"))
                        .Append(") baseOk=").Append(map.IsBoxWalkable(basePosition, Half))
                        .Append(" delta=(").Append(prediction.LastCorrectionDelta.X.ToString("0.00000"))
                        .Append("; ").Append(prediction.LastCorrectionDelta.Y.ToString("0.00000"))
                        .Append(")\n");
                }
            }

            long corrections = prediction.Corrections + prediction.Snaps;

            Assert.True(
                corrections == 0,
                "Sin red no debería haber correcciones y hay " + corrections +
                " (suaves " + prediction.Corrections + ", saltos " + prediction.Snaps +
                "). Primeras:\n" + examples);
        }

        [Fact]
        public void Un_Input_Pendiente_Contra_La_Pared_Desde_La_Base_Cuantizada_No_Corrige()
        {
            // Columna x = 4 bloqueada: la caja (semiancho 0,4) toca la pared con el centro en 3,6.
            MapCollisionGrid map = MapCollisionGrid.FromRows(new[]
            {
                "....#.",
                "....#.",
                "....#.",
                "....#.",
                "....#.",
            });

            var prediction = new ClientPrediction(map, Half, Speed, SimulationConstants.FixedDt);
            prediction.Reset(new Vec2(3.4f, 2.5f));

            // Un input lleva al jugador justo al borde de la pared; el siguiente empuja contra ella.
            prediction.AddInput(1, 0);
            prediction.AddInput(1, 0);

            Vec2 server = MovementSimulator.Step(
                MovementSimulator.Step(new Vec2(3.4f, 2.5f), 1, 0, Speed, SimulationConstants.FixedDt, Half, map),
                1,
                0,
                Speed,
                SimulationConstants.FixedDt,
                Half,
                map);

            // El servidor confirma solo el primer input: el segundo queda pendiente y se reaplica
            // desde la posición cuantizada del snapshot, que está un poco antes de la pared.
            prediction.Reconcile(Quantize(server), 1);

            Assert.Equal(0, prediction.Corrections);
            Assert.Equal(0, prediction.Snaps);
            Assert.Equal(server.X, prediction.Position.X, 5);
            Assert.True(map.IsBoxWalkable(prediction.Position, Half));
        }
    }
}
