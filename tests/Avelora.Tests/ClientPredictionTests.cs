using System;
using Avelora.Shared;
using Avelora.Shared.Maps;
using Avelora.Shared.Prediction;
using Avelora.Shared.Protocol;
using Xunit;

namespace Avelora.Tests
{
    public class ClientPredictionTests
    {
        private static readonly Vec2 Spawn = new Vec2(2.5f, 2.5f);
        private static readonly Vec2 HalfSize = new Vec2(0.4f, 0.4f);

        private static MapCollisionGrid MapaLibre()
        {
            return new MapCollisionGrid(10, 10, new bool[100]);
        }

        private static ClientPrediction NuevaPrediccion()
        {
            var prediction = new ClientPrediction(
                MapaLibre(),
                HalfSize,
                4f,
                SimulationConstants.FixedDt);
            prediction.Reset(Spawn);
            return prediction;
        }

        private static void CasiIgual(float expected, float actual, float tolerance = 1e-4f)
        {
            Assert.True(
                MathF.Abs(expected - actual) <= tolerance,
                "Esperado " + expected + ", obtenido " + actual + ".");
        }

        [Fact]
        public void Un_Input_Es_Un_Paso_Exacto()
        {
            ClientPrediction prediction = NuevaPrediccion();

            uint sequence = prediction.AddInput(1, 0);

            Assert.Equal(1u, sequence);
            Assert.Equal(1u, prediction.LastSequence);
            Assert.Equal(1, prediction.PendingCount);
            CasiIgual(2.7f, prediction.Position.X);
            CasiIgual(2.5f, prediction.Position.Y);
        }

        [Fact]
        public void En_Una_Esquina_Reaplica_Desde_La_Prediccion_Exacta_Y_No_Desde_La_Cuantizada()
        {
            // Caso real de la carga I-03c: la caja toca a la vez x = 0 e y = 1 junto al tabique.
            // Cuantizada (-0,40625; 1,40625) queda 1/128 fuera de la esquina y el reaplicado avanzaba
            // en diagonal donde el servidor, con la posición exacta, choca.
            var prediction = new ClientPrediction(
                MapFile.Load(System.IO.Path.Combine(AppContext.BaseDirectory, "maps", "avelora-etapa-a.avmap")),
                HalfSize,
                4f,
                SimulationConstants.FixedDt);
            // Un pelo bajo y = 1,4 (el float que dejó la carga): el borde inferior roza la fila del
            // tabique y frena el avance en x; la base cuantizada, 1/128 más arriba, ya no.
            prediction.Reset(new Vec2(-0.4f, 1.3999999f));

            prediction.AddInput(0, 0);
            Vec2 acked = prediction.Position;
            prediction.AddInput(1, 1);
            Assert.Equal(-0.4f, prediction.Position.X);

            Assert.False(prediction.Reconcile(
                new Vec2(
                    ProtocolConstants.DequantizePosition(ProtocolConstants.QuantizePosition(acked.X)),
                    ProtocolConstants.DequantizePosition(ProtocolConstants.QuantizePosition(acked.Y))),
                1u));
            Assert.Equal(0, prediction.Corrections);
        }

        [Fact]
        public void Tolera_Dos_Medios_Pasos_De_Cuantizacion_Y_Corrige_Por_Encima()
        {
            // Base cuantizada (medio paso) más una predicción que ya salió de otra base cuantizada
            // (otro medio): hasta 1/64 es ruido; más allá, un error real (I-03c).
            float half = ProtocolConstants.MaxQuantizationError;

            ClientPrediction dentro = NuevaPrediccion();
            Assert.False(dentro.Reconcile(new Vec2(Spawn.X + (1.9f * half), Spawn.Y), 0u));

            ClientPrediction fuera = NuevaPrediccion();
            Assert.True(fuera.Reconcile(new Vec2(Spawn.X + (2.5f * half), Spawn.Y), 0u));
        }

        [Fact]
        public void Teleport_Conserva_La_Secuencia_Y_No_Cuenta_Correccion()
        {
            ClientPrediction prediction = NuevaPrediccion();
            prediction.AddInput(0, 0);
            prediction.AddInput(0, 0);

            var respawn = new Vec2(7.5f, 7.5f);
            prediction.Teleport(respawn);

            // El servidor confirmó el 1.º y ya puso al jugador en el punto de reaparición.
            Assert.False(prediction.Reconcile(respawn, 1u));
            Assert.Equal(3u, prediction.AddInput(0, 0));
            CasiIgual(7.5f, prediction.Position.X);
        }

        [Fact]
        public void La_Diagonal_No_Es_Mas_Rapida()
        {
            ClientPrediction prediction = NuevaPrediccion();

            prediction.AddInput(1, 1);

            Vec2 movement = prediction.Position - Spawn;
            CasiIgual(4f * SimulationConstants.FixedDt, movement.Length);
            CasiIgual(0.14142136f, prediction.Position.X - Spawn.X);
            CasiIgual(0.14142136f, prediction.Position.Y - Spawn.Y);
        }

        [Fact]
        public void Sin_Direccion_No_Mueve_Pero_Consume_Secuencia()
        {
            ClientPrediction prediction = NuevaPrediccion();

            uint sequence = prediction.AddInput(0, 0);

            Assert.Equal(1u, sequence);
            Assert.Equal(Spawn, prediction.Position);
            Assert.Equal(1, prediction.PendingCount);
        }

        [Fact]
        public void La_Reconciliacion_Descarta_Lo_Confirmado_Y_Reaplica_Lo_Pendiente()
        {
            ClientPrediction prediction = NuevaPrediccion();
            prediction.AddInput(1, 0);
            prediction.AddInput(1, 0);
            prediction.AddInput(1, 0);

            // El servidor solo ha aplicado el primero: tras reaplicar los otros dos, la posición
            // predicha debe ser exactamente la misma que antes del snapshot.
            Vec2 server = MovementSimulator.Step(
                Spawn, 1, 0, 4f, SimulationConstants.FixedDt, HalfSize, MapaLibre());

            bool corrected = prediction.Reconcile(server, 1);

            Assert.False(corrected);
            Assert.Equal(2, prediction.PendingCount);
            CasiIgual(3.1f, prediction.Position.X);
        }

        [Fact]
        public void La_Reconciliacion_Con_Error_Pequeno_Desliza_Sin_Saltar()
        {
            ClientPrediction prediction = NuevaPrediccion();
            prediction.AddInput(1, 0);
            CasiIgual(2.7f, prediction.Position.X);

            bool corrected = prediction.Reconcile(new Vec2(2.6f, 2.5f), 1);

            Assert.True(corrected);
            Assert.Equal(0, prediction.PendingCount);
            Assert.Equal(1, prediction.Corrections);
            Assert.Equal(0, prediction.Snaps);
            CasiIgual(2.6f, prediction.Position.X);

            // El dibujo sigue donde estaba y se va acercando a la verdad del servidor.
            CasiIgual(2.7f, prediction.Render(1f).X);
            prediction.AdvanceVisual(0.005f);
            CasiIgual(2.65f, prediction.Render(1f).X);
            prediction.AdvanceVisual(1f);
            CasiIgual(2.6f, prediction.Render(1f).X);
            Assert.Equal(Vec2.Zero, prediction.VisualOffset);
        }

        [Fact]
        public void La_Reconciliacion_Con_Error_Grande_Salta()
        {
            ClientPrediction prediction = NuevaPrediccion();
            prediction.AddInput(1, 0);

            bool corrected = prediction.Reconcile(new Vec2(4.5f, 2.5f), 1);

            Assert.True(corrected);
            Assert.Equal(0, prediction.Corrections);
            Assert.Equal(1, prediction.Snaps);
            CasiIgual(4.5f, prediction.Position.X);
            Assert.Equal(Vec2.Zero, prediction.VisualOffset);
        }

        [Fact]
        public void El_Ruido_De_Cuantizacion_No_Se_Corrige()
        {
            ClientPrediction prediction = NuevaPrediccion();
            prediction.AddInput(1, 0);

            var server = new Vec2(
                prediction.Position.X + ProtocolConstants.MaxQuantizationError,
                prediction.Position.Y + ProtocolConstants.MaxQuantizationError);

            bool corrected = prediction.Reconcile(server, 1);

            Assert.False(corrected);
            Assert.Equal(0, prediction.Corrections);
            Assert.Equal(0, prediction.Snaps);
            CasiIgual(2.7f, prediction.Position.X);
            Assert.Equal(Vec2.Zero, prediction.VisualOffset);
        }

        [Fact]
        public void Render_Interpola_Entre_Los_Dos_Ultimos_Pasos()
        {
            ClientPrediction prediction = NuevaPrediccion();
            prediction.AddInput(1, 0);

            CasiIgual(2.5f, prediction.Render(0f).X);
            CasiIgual(2.6f, prediction.Render(0.5f).X);
            CasiIgual(2.7f, prediction.Render(1f).X);
            CasiIgual(2.7f, prediction.Render(2f).X);
            CasiIgual(2.5f, prediction.Render(-1f).X);
        }

        [Fact]
        public void Reset_Vuelve_Al_Origen_Y_Borra_Lo_Pendiente()
        {
            ClientPrediction prediction = NuevaPrediccion();
            prediction.AddInput(1, 0);

            prediction.Reset(Spawn);

            Assert.Equal(Spawn, prediction.Position);
            Assert.Equal(Spawn, prediction.PreviousPosition);
            Assert.Equal(0u, prediction.LastSequence);
            Assert.Equal(0, prediction.PendingCount);
            Assert.Equal(Vec2.Zero, prediction.VisualOffset);
        }

        [Fact]
        public void El_Buffer_Acotado_Descarta_Lo_Mas_Viejo_Si_No_Llegan_Snapshots()
        {
            var prediction = new ClientPrediction(
                MapaLibre(),
                HalfSize,
                4f,
                SimulationConstants.FixedDt,
                maxPendingInputs: 2);
            prediction.Reset(Spawn);

            prediction.AddInput(1, 0);
            prediction.AddInput(1, 0);
            prediction.AddInput(1, 0);

            Assert.Equal(2, prediction.PendingCount);
            Assert.Equal(1, prediction.DroppedInputs);
            Assert.Equal(3u, prediction.LastSequence);
        }

        [Fact]
        public void Una_Direccion_Invalida_Es_Un_Error_De_Programacion()
        {
            ClientPrediction prediction = NuevaPrediccion();

            Assert.Throws<ArgumentOutOfRangeException>(() => prediction.AddInput(2, 0));
        }

        [Fact]
        public void SetSpeed_Valida_Y_Afecta_Los_Inputs_Nuevos()
        {
            ClientPrediction prediction = NuevaPrediccion();
            Assert.Throws<ArgumentOutOfRangeException>(() => prediction.SetSpeed(-1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => prediction.SetSpeed(float.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => prediction.SetSpeed(float.PositiveInfinity));
            prediction.SetSpeed(2f);
            prediction.AddInput(1, 0);
            Assert.Equal(2f, prediction.Speed);
            CasiIgual(Spawn.X + 2f * SimulationConstants.FixedDt, prediction.Position.X);
            CasiIgual(Spawn.Y, prediction.Position.Y);
        }

        [Fact]
        public void Ralentizacion_Retardada_Reconcilia_Dos_Cambios_Sin_Saltos()
        {
            const int delay = 3;
            var map = MapCollisionGrid.AllWalkable(1000, 10);
            var prediction = new ClientPrediction(map, HalfSize, 4f, SimulationConstants.FixedDt);
            Vec2 start = new Vec2(2.5f, 2.5f);
            prediction.Reset(start);
            Vec2 server = start;
            float serverSpeed = 4f;
            float correctionLimit = 1.6f * (delay + 1) * SimulationConstants.FixedDt +
                2 * ProtocolConstants.MaxQuantizationError + 1e-4f;
            long correctionsBeforeSteadyState = 0;

            for (int i = 1; i <= 120; i++)
            {
                prediction.AddInput(1, 0);
                if (i > delay)
                {
                    uint sequence = (uint)(i - delay);
                    if (sequence == 40)
                    {
                        serverSpeed = 2.4f;
                        prediction.SetSpeed(serverSpeed);
                    }
                    else if (sequence == 80)
                    {
                        serverSpeed = 4f;
                        prediction.SetSpeed(serverSpeed);
                    }

                    server = MovementSimulator.Step(server, 1, 0, serverSpeed,
                        SimulationConstants.FixedDt, HalfSize, map);
                    Vec2 quantized = new Vec2(
                        ProtocolConstants.DequantizePosition(ProtocolConstants.QuantizePosition(server.X)),
                        ProtocolConstants.DequantizePosition(ProtocolConstants.QuantizePosition(server.Y)));
                    long correctionsBefore = prediction.Corrections;
                    prediction.Reconcile(quantized, sequence);
                    if (prediction.Corrections > correctionsBefore)
                    {
                        Assert.Equal(correctionsBefore + 1, prediction.Corrections);
                        Assert.True(MathF.Abs(prediction.LastCorrectionDelta.X) <= correctionLimit);
                        Assert.True(MathF.Abs(prediction.LastCorrectionDelta.Y) <= correctionLimit);
                    }
                }
                if (i == 90) correctionsBeforeSteadyState = prediction.Corrections;
            }

            Assert.Equal(2, prediction.Corrections);
            Assert.Equal(0, prediction.Snaps);
            Assert.Equal(correctionsBeforeSteadyState, prediction.Corrections);
        }
    }
}
