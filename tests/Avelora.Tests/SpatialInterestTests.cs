using System.Collections.Generic;
using Avelora.Server.Entities;
using Avelora.Server.Simulation;
using Avelora.Shared;
using Xunit;

namespace Avelora.Tests
{
    /// <summary>
    /// Pruebas de la grilla espacial y del filtro de área de interés, sin red ni tick.
    /// </summary>
    public class SpatialInterestTests
    {
        private static Entity At(int id, float x, float y) =>
            new Entity(id, EntityKind.Player)
            {
                Position = new Vec2(x, y),
            };

        private static PlayerEntity Viewer(int id, float x, float y) =>
            new PlayerEntity(id, id, inputBufferCapacity: 8)
            {
                Position = new Vec2(x, y),
            };

        [Fact]
        public void La_Grilla_Indexa_Por_Celda_Y_Consulta_Por_Radio()
        {
            var grid = new EntitySpatialGrid(4f);
            Entity a = At(1, 0f, 0f);
            Entity b = At(2, 3f, 4f);
            Entity c = At(3, 10f, 0f);

            grid.Add(a);
            grid.Add(b);
            grid.Add(c);

            Assert.Equal(3, grid.CellCount);
            Assert.Equal(0, a.CellX);
            Assert.Equal(0, a.CellY);
            Assert.Equal(0, b.CellX);
            Assert.Equal(1, b.CellY);
            Assert.Equal(2, c.CellX);
            Assert.Equal(0, c.CellY);

            var results = new List<Entity>();
            grid.Query(0f, 0f, 5f, results);

            Assert.Contains(a, results);
            Assert.Contains(b, results);
            Assert.DoesNotContain(c, results);
        }

        [Fact]
        public void La_Consulta_Vacia_La_Lista_Reutilizada()
        {
            var grid = new EntitySpatialGrid(4f);
            grid.Add(At(1, 0f, 0f));

            var results = new List<Entity> { At(99, 20f, 20f) };
            grid.Query(0f, 0f, 1f, results);

            Assert.Single(results);
            Assert.Equal(1, results[0].Id);
        }

        [Fact]
        public void Mover_Una_Entidad_La_Reindexa_Sin_Duplicarla()
        {
            var grid = new EntitySpatialGrid(4f);
            Entity entity = At(1, 0f, 0f);
            grid.Add(entity);

            entity.Position = new Vec2(10f, 10f);
            grid.Update(entity);

            Assert.Equal(2, entity.CellX);
            Assert.Equal(2, entity.CellY);
            Assert.Equal(1, grid.CellCount);

            var results = new List<Entity>();
            grid.Query(10f, 10f, 0.5f, results);
            Assert.Single(results);
            Assert.Same(entity, results[0]);
        }

        [Fact]
        public void Update_Indexa_Una_Entidad_Que_No_Estaba()
        {
            var grid = new EntitySpatialGrid(4f);
            Entity entity = At(1, 2f, 2f);

            grid.Update(entity);

            Assert.Equal(0, entity.CellX);
            Assert.Equal(0, entity.CellY);
            Assert.Equal(1, grid.CellCount);
        }

        [Fact]
        public void Quitar_Una_Entidad_Limpia_Su_Celda()
        {
            var grid = new EntitySpatialGrid(4f);
            Entity entity = At(1, 0f, 0f);
            grid.Add(entity);

            grid.Remove(entity);

            Assert.Equal(Entity.NoCell, entity.CellX);
            Assert.Equal(Entity.NoCell, entity.CellY);
            Assert.Equal(0, grid.CellCount);

            var results = new List<Entity>();
            grid.Query(0f, 0f, 5f, results);
            Assert.Empty(results);
        }

        [Fact]
        public void Las_Coordenadas_Negativas_No_Se_Mezclan_Con_Las_Positivas()
        {
            var grid = new EntitySpatialGrid(2f);
            Entity negative = At(1, -1.5f, -0.5f);
            Entity sameCell = At(2, -1.1f, -1.1f);
            Entity positive = At(3, 0f, 0f);

            grid.Add(negative);
            grid.Add(sameCell);
            grid.Add(positive);

            Assert.Equal(-1, negative.CellX);
            Assert.Equal(-1, negative.CellY);
            Assert.Equal(-1, sameCell.CellX);
            Assert.Equal(-1, sameCell.CellY);
            Assert.Equal(2, grid.CellCount);

            var results = new List<Entity>();
            grid.Query(-1.5f, -0.5f, 0.5f, results);
            Assert.Contains(negative, results);
            Assert.DoesNotContain(sameCell, results);
            Assert.DoesNotContain(positive, results);
        }

        [Fact]
        public void El_Radio_Exacto_Cuenta_Como_Dentro()
        {
            var grid = new EntitySpatialGrid(4f);
            Entity entity = At(1, 4f, 0f);
            grid.Add(entity);

            var results = new List<Entity>();
            grid.Query(3.9f, 0f, 0.1f, results);

            Assert.Single(results);
            Assert.Same(entity, results[0]);
        }

        [Fact]
        public void El_Area_De_Interes_Incluye_Al_Jugador_Y_Lo_Cercano()
        {
            var grid = new EntitySpatialGrid(2f);
            PlayerEntity viewer = Viewer(1, 0f, 0f);
            Entity near = At(2, 1f, 0f);
            Entity far = At(3, 3f, 0f);
            grid.Add(viewer);
            grid.Add(near);
            grid.Add(far);

            var interest = new InterestSystem(grid, new InterestSettings(2f, 2f, 0.5f));
            var results = new List<Entity>();

            interest.CollectVisible(viewer, results);

            Assert.Equal(2, results.Count);
            Assert.Same(viewer, results[0]);
            Assert.Contains(near, results);
            Assert.DoesNotContain(far, results);
            Assert.Contains(1, viewer.VisibleEntityIds);
            Assert.Contains(2, viewer.VisibleEntityIds);
            Assert.DoesNotContain(3, viewer.VisibleEntityIds);
        }

        [Fact]
        public void La_Histeresis_Conserva_Lo_Que_Ya_Estaba_Y_Exige_Entrar_Para_Volver()
        {
            var grid = new EntitySpatialGrid(2f);
            PlayerEntity viewer = Viewer(1, 0f, 0f);
            Entity near = At(2, 1f, 0f);
            Entity far = At(3, 3f, 0f);
            grid.Add(viewer);
            grid.Add(near);
            grid.Add(far);

            var interest = new InterestSystem(grid, new InterestSettings(2f, 2f, 0.5f));
            var results = new List<Entity>();

            interest.CollectVisible(viewer, results);
            Assert.Contains(near, results);

            // 2.4 está fuera del radio de entrada (2.0) pero dentro del de salida (2.5).
            near.Position = new Vec2(2.4f, 0f);
            grid.Update(near);
            interest.CollectVisible(viewer, results);
            Assert.Contains(near, results);

            // 2.6 supera el radio de salida: se retira.
            near.Position = new Vec2(2.6f, 0f);
            grid.Update(near);
            interest.CollectVisible(viewer, results);
            Assert.DoesNotContain(near, results);

            // Lejos estaba fuera. Al acercarse a 1.9 entra por el radio de entrada.
            far.Position = new Vec2(1.9f, 0f);
            grid.Update(far);
            interest.CollectVisible(viewer, results);
            Assert.Contains(far, results);
        }

        [Fact]
        public void El_Jugador_Local_Se_Incluye_Siempre()
        {
            var grid = new EntitySpatialGrid(1f);
            PlayerEntity viewer = Viewer(1, 0f, 0f);
            grid.Add(viewer);

            var interest = new InterestSystem(grid, new InterestSettings(0.1f, 1f, 0f));
            var results = new List<Entity>();

            interest.CollectVisible(viewer, results);

            Assert.Single(results);
            Assert.Same(viewer, results[0]);
            Assert.Contains(1, viewer.VisibleEntityIds);
        }

        [Fact]
        public void Quitar_Una_Entidad_La_Saca_Tambien_De_Los_Visibles()
        {
            var grid = new EntitySpatialGrid(2f);
            PlayerEntity viewer = Viewer(1, 0f, 0f);
            Entity near = At(2, 1f, 0f);
            grid.Add(viewer);
            grid.Add(near);

            var interest = new InterestSystem(grid, new InterestSettings(2f, 2f, 0.5f));
            var results = new List<Entity>();

            interest.CollectVisible(viewer, results);
            Assert.Contains(near, results);

            interest.Remove(near);
            interest.CollectVisible(viewer, results);

            Assert.Single(results);
            Assert.DoesNotContain(2, viewer.VisibleEntityIds);
        }
    }
}
