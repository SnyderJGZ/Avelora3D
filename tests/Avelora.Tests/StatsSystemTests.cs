using System.Collections.Generic;
using Avelora.Server.Entities;
using Avelora.Server.Items;
using Avelora.Server.Progression;
using Avelora.Server.Simulation;
using Avelora.Shared;
using Xunit;

namespace Avelora.Tests
{
    public sealed class StatsSystemTests
    {
        private static ItemCatalog Items() => new ItemCatalog
        {
            Items = new List<ItemType>
            {
                new ItemType { Id = 3201, Name = "Piedra de reajuste", Kind = "material", MaxStack = 10 },
            },
        };

        private static Zone NewZone(ItemCatalog? items = null, LevelTable? levels = null) => new Zone(
            1,
            MapCollisionGrid.AllWalkable(20, 20),
            new EntityStore(),
            new EntityIdAllocator(),
            new InputQueue(32),
            new MovementSystem(4, SimulationConstants.FixedDt),
            32,
            4,
            items: items,
            itemActions: new ItemActionQueue(32),
            levels: levels ?? LevelTable.Default);

        private static PlayerEntity Spawn(Zone zone, int level = 1) => zone.SpawnPlayer(
            1, new Vec2(5, 5), new Vec2(0.4f, 0.4f), 8, level: level);

        [Fact]
        public void Repartir_VIT_Aumenta_Vida_Maxima_Y_Actual_En_La_Misma_Cantidad()
        {
            Zone zone = NewZone(Items());
            PlayerEntity player = Spawn(zone);
            player.Health = 80;
            int originalMaxHealth = player.MaxHealth;
            int statsChanged = 0;
            zone.StatsChanged += _ => statsChanged++;

            zone.ItemActions!.TryEnqueue(ItemAction.AllocatePoints(player.Id, 0, 0, 0, 2));
            zone.Tick();

            Assert.Equal(originalMaxHealth + 12, player.MaxHealth);
            Assert.Equal(92, player.Health);
            Assert.Equal(1, CharacterStats.GetFreePoints(3, 1, player.AllocStr, player.AllocDex, player.AllocInt, player.AllocVit));
            Assert.Equal(1, zone.StatsAllocations);
            Assert.Equal(2, zone.StatsPointsAllocated);
            Assert.Equal(1, statsChanged);
        }

        [Fact]
        public void Reparto_Excesivo_Vacio_Desbordado_Y_Muerto_Se_Rechaza_Sin_Cambios_Parciales()
        {
            Zone zone = NewZone(Items());
            PlayerEntity player = Spawn(zone);
            int originalMaxHealth = player.MaxHealth;

            zone.ItemActions!.TryEnqueue(ItemAction.AllocatePoints(player.Id, 4, 0, 0, 0));
            zone.ItemActions.TryEnqueue(ItemAction.AllocatePoints(player.Id, 0, 0, 0, 0));
            zone.ItemActions.TryEnqueue(ItemAction.AllocatePoints(player.Id, int.MaxValue, int.MaxValue, 0, 0));
            zone.ItemActions.TryEnqueue(ItemAction.AllocatePoints(player.Id, -1, 1, 0, 0));
            zone.Tick();
            player.Action = EntityAction.Dead;
            zone.ItemActions.TryEnqueue(ItemAction.AllocatePoints(player.Id, 1, 0, 0, 0));
            zone.Tick();

            Assert.Equal(0, player.AllocStr);
            Assert.Equal(0, player.AllocDex);
            Assert.Equal(originalMaxHealth, player.MaxHealth);
            Assert.Equal(0, zone.StatsAllocations);
            Assert.Equal(0, zone.StatsPointsAllocated);
            Assert.Equal(1, zone.RejectedStatsEmpty);
            Assert.Equal(3, zone.RejectedStatsExcess);
            Assert.Equal(1, zone.RejectedStatsDead);
        }

        [Fact]
        public void Reajuste_Gratis_Hasta_El_Nivel_Configurable_Recorta_Vida_Sin_Curar()
        {
            LevelTable levels = LevelTable.Default;
            levels.FreeResetMaxLevel = 10;
            Zone zone = NewZone(Items(), levels);
            PlayerEntity player = Spawn(zone, level: 10);
            player.AllocVit = 2;
            player.MaxHealth += 10;
            player.Health = 75;

            zone.ItemActions!.TryEnqueue(ItemAction.ResetPoints(player.Id));
            zone.Tick();

            Assert.Equal(0, player.AllocVit);
            Assert.Equal(210, player.MaxHealth);
            Assert.Equal(75, player.Health);
            Assert.Equal(1, zone.PointResets);
            Assert.Equal(0, zone.RejectedResetItem);
        }

        [Fact]
        public void Reajuste_Desde_Nivel_Once_Requiere_Y_Consume_Una_Piedra_Y_Notifica_Inventario()
        {
            Zone zone = NewZone(Items());
            PlayerEntity player = Spawn(zone, level: 11);
            player.AllocVit = 2;
            player.MaxHealth += 10;
            player.Health = player.MaxHealth;
            int inventoryChanges = 0;
            zone.InventoryChanged += _ => inventoryChanges++;

            zone.ItemActions!.TryEnqueue(ItemAction.ResetPoints(player.Id));
            zone.Tick();
            Assert.Equal(1, zone.RejectedResetItem);
            Assert.Equal(2, player.AllocVit);

            player.Inventory[0] = new InventorySlot(3201, 2);
            zone.ItemActions.TryEnqueue(ItemAction.ResetPoints(player.Id));
            zone.Tick();

            Assert.Equal(0, player.AllocVit);
            Assert.Equal(new InventorySlot(3201, 1), player.Inventory[0]);
            Assert.Equal(220, player.MaxHealth);
            Assert.Equal(220, player.Health);
            Assert.Equal(1, inventoryChanges);
            Assert.Equal(1, zone.PointResets);
        }

        [Fact]
        public void Reajuste_Sin_Puntos_Y_Muerto_Se_Rechaza()
        {
            Zone zone = NewZone(Items());
            PlayerEntity player = Spawn(zone);
            zone.ItemActions!.TryEnqueue(ItemAction.ResetPoints(player.Id));
            zone.Tick();
            player.AllocStr = 1;
            player.Action = EntityAction.Dead;
            zone.ItemActions.TryEnqueue(ItemAction.ResetPoints(player.Id));
            zone.Tick();

            Assert.Equal(1, zone.RejectedResetEmpty);
            Assert.Equal(1, zone.RejectedResetDead);
            Assert.Equal(0, zone.PointResets);
            Assert.Equal(1, player.AllocStr);
        }
    }
}
