using System;
using Avelora.Shared;
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
    }
}
