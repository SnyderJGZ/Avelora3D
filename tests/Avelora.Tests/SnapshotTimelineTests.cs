using System;
using Avelora.Shared;
using Avelora.Shared.Prediction;
using Avelora.Shared.Protocol;
using Xunit;

namespace Avelora.Tests
{
    public class SnapshotTimelineTests
    {
        private static void CasiIgual(float expected, float actual, float tolerance = 1e-4f)
        {
            Assert.True(
                MathF.Abs(expected - actual) <= tolerance,
                "Esperado " + expected + ", obtenido " + actual + ".");
        }

        private static SnapshotMessage Snapshot(uint tick, params EntityState[] entities)
        {
            return new SnapshotMessage(tick, tick, entities);
        }

        private static EntityState Entity(
            int id,
            float x,
            float y,
            EntityKind kind = EntityKind.Player)
        {
            return new EntityState(
                id,
                ProtocolConstants.QuantizePosition(x),
                ProtocolConstants.QuantizePosition(y),
                0,
                0,
                kind);
        }

        [Fact]
        public void Interpola_Entre_Dos_Snapshots()
        {
            var timeline = new SnapshotTimeline();
            timeline.Add(Snapshot(1, Entity(1, 0f, 0f)), 10.0);
            timeline.Add(Snapshot(2, Entity(1, 1f, 0f)), 10.05);

            Assert.True(timeline.TrySample(1, 10.025, out EntitySample sample));
            CasiIgual(0.5f, sample.Position.X);
            CasiIgual(0f, sample.Position.Y);
        }

        [Fact]
        public void Antes_Del_Primer_Snapshot_Usa_El_Primero()
        {
            var timeline = new SnapshotTimeline();
            timeline.Add(Snapshot(1, Entity(1, 2f, 3f)), 10.0);
            timeline.Add(Snapshot(2, Entity(1, 4f, 5f)), 10.05);

            Assert.True(timeline.TrySample(1, 9.0, out EntitySample sample));
            CasiIgual(2f, sample.Position.X);
            CasiIgual(3f, sample.Position.Y);
        }

        [Fact]
        public void Extrapola_Un_Poco_Y_Despues_Se_Detiene()
        {
            var timeline = new SnapshotTimeline(maxHistorySeconds: 1.0, maxExtrapolationSeconds: 0.1);

            // 13/64 es exacto en la rejilla del snapshot: así la prueba no confunde el error de
            // cuantización con un fallo de interpolación.
            timeline.Add(Snapshot(1, Entity(1, 0f, 0f)), 10.0);
            timeline.Add(Snapshot(2, Entity(1, 13f / 64f, 0f)), 10.05);

            Assert.True(timeline.TrySample(1, 10.1, out EntitySample extrapolated));
            CasiIgual(26f / 64f, extrapolated.Position.X);

            Assert.True(timeline.TrySample(1, 10.5, out EntitySample frozen));
            CasiIgual(13f / 64f, frozen.Position.X);
        }

        [Fact]
        public void Una_Entidad_Que_Falta_En_Un_Snapshot_Usa_El_Otro()
        {
            var timeline = new SnapshotTimeline();
            timeline.Add(Snapshot(1, Entity(1, 0f, 0f)), 10.0);
            timeline.Add(Snapshot(2, Entity(2, 1f, 1f)), 10.05);

            Assert.True(timeline.TrySample(1, 10.025, out EntitySample first));
            CasiIgual(0f, first.Position.X);

            Assert.True(timeline.TrySample(2, 10.025, out EntitySample second));
            CasiIgual(1f, second.Position.X);

            Assert.False(timeline.TrySample(3, 10.025, out _));
        }

        [Fact]
        public void Un_Snapshot_Viejo_Se_Ignora()
        {
            var timeline = new SnapshotTimeline();
            Assert.True(timeline.Add(Snapshot(2, Entity(1, 1f, 0f)), 10.0));
            Assert.False(timeline.Add(Snapshot(1, Entity(1, 0f, 0f)), 10.05));

            Assert.Equal(1, timeline.Count);
            Assert.Equal(2u, timeline.Latest.ServerTick);
        }

        [Fact]
        public void La_Historia_Se_Recorta()
        {
            // Tiempos exactos en binario para que el borde del historial no dependa del redondeo.
            var timeline = new SnapshotTimeline(maxHistorySeconds: 0.25);
            timeline.Add(Snapshot(1, Entity(1, 0f, 0f)), 10.0);
            timeline.Add(Snapshot(2, Entity(1, 0f, 0f)), 10.125);
            timeline.Add(Snapshot(3, Entity(1, 0f, 0f)), 10.25);
            timeline.Add(Snapshot(4, Entity(1, 0f, 0f)), 10.375);

            Assert.Equal(3, timeline.Count);
        }

        [Fact]
        public void No_Hay_Muestras_Sin_Snapshots()
        {
            var timeline = new SnapshotTimeline();

            Assert.False(timeline.HasSnapshots);
            Assert.False(timeline.TrySample(1, 10.0, out _));
        }

        [Fact]
        public void La_Muestra_Incluye_Direccion_Y_Tipo()
        {
            var timeline = new SnapshotTimeline();
            timeline.Add(
                Snapshot(1, new EntityState(7, 0, 0, -1, 1, EntityKind.Npc)),
                10.0);

            Assert.True(timeline.TrySample(7, 10.0, out EntitySample sample));
            Assert.Equal(-1, sample.DirectionX);
            Assert.Equal(1, sample.DirectionY);
            Assert.Equal(EntityKind.Npc, sample.Kind);
        }
    }
}
