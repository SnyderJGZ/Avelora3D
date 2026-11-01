using System;
using System.Collections.Generic;
using System.Linq;
using Avelora.Server.Entities;
using Avelora.Server.Configuration;
using Avelora.Server.Items;
using Avelora.Server.Npcs;
using Avelora.Server.Progression;
using Avelora.Server.Simulation;
using Avelora.Shared;
using Avelora.Shared.Protocol;
using Xunit;

namespace Avelora.Tests
{
    public sealed class I25Tests
    {
        private static ClassCatalog Classes() => ClassCatalog.Load(
            System.IO.Path.Combine(AppContext.BaseDirectory, "classes.json"));

        private static ItemCatalog Items() => new ItemCatalog
        {
            Items = new List<ItemType>
            {
                new ItemType { Id = 3001, Name = "Material", Kind = "material", MaxStack = 99 },
            },
        };

        private static NpcCatalog Npcs(int goldMin = 0, int goldMax = 0) => new NpcCatalog
        {
            Types = new List<NpcType>
            {
                new NpcType
                {
                    Id = 2001,
                    Name = "PNJ de prueba",
                    Speed = 0,
                    HalfWidth = 0.35f,
                    HalfHeight = 0.35f,
                    MaxHealth = 100,
                    Experience = 20,
                    Level = 1,
                    RespawnSeconds = 10,
                    AggroRadius = 0,
                    LeashRadius = 8,
                    AttackDamage = 1,
                    AttackRange = 1,
                    AttackCooldownSeconds = 1.5f,
                    GoldMin = goldMin,
                    GoldMax = goldMax,
                    Loot = new List<LootEntry> { new LootEntry { ItemId = 3001, Chance = 1, Min = 1, Max = 1 } },
                },
            },
            Spawners = new List<NpcSpawner>
            {
                new NpcSpawner { TypeId = 2001, X = 5, Y = 5, Count = 1, WanderRadius = 1 },
            },
        };

        private static NpcCatalog CatalogForValidation(int goldMin, int goldMax, int lootCount)
        {
            var entries = new List<LootEntry>();
            for (int i = 0; i < lootCount; i++)
            {
                int id = i + 1;
                entries.Add(new LootEntry { ItemId = id, Chance = 1, Min = 1, Max = 1 });
            }

            return new NpcCatalog
            {
                Types = new List<NpcType>
                {
                    new NpcType
                    {
                        Id = 2001, Name = "PNJ de validación", Speed = 1, HalfWidth = 0.35f,
                        HalfHeight = 0.35f, MaxHealth = 30, Experience = 10, Level = 1,
                        RespawnSeconds = 10, AggroRadius = 0, LeashRadius = 8,
                        AttackDamage = 1, AttackRange = 1, AttackCooldownSeconds = 1,
                        GoldMin = goldMin, GoldMax = goldMax, Loot = entries,
                    },
                },
                Spawners = new List<NpcSpawner>(),
            };
        }

        private static Zone NewZone(NpcCatalog? npcs = null, long reserveTicks = 600, long tagTicks = 200) => new Zone(
            1,
            MapCollisionGrid.AllWalkable(20, 20),
            new EntityStore(),
            new EntityIdAllocator(),
            new InputQueue(64),
            new MovementSystem(4, SimulationConstants.FixedDt),
            128,
            4,
            new InterestSettings(20, 8, 0),
            npcs ?? Npcs(),
            attacks: new AttackQueue(64),
            combat: new CombatSettings(0, tagTicks),
            items: Items(),
            itemActions: new ItemActionQueue(64),
            inventorySlots: 4,
            groundItemTicks: 1200,
            classes: Classes(),
            lootReserveTicks: reserveTicks);

        private static PlayerEntity Player(Zone zone, int characterId, int x = 5) =>
            zone.SpawnPlayer(characterId, new Vec2(x, 5), new Vec2(0.4f, 0.4f), 8);

        private static NpcEntity Npc(Zone zone) => Assert.IsType<NpcEntity>(
            zone.Entities.All.First(entity => entity is NpcEntity));

        private static void EnqueueAttack(Zone zone, PlayerEntity attacker, NpcEntity target)
        {
            attacker.LastAttackTick = -1;
            Assert.True(zone.Attacks!.TryEnqueue(new AttackIntent(attacker.Id, target.Id)));
        }

        [Fact]
        public void Botin_Usa_El_Hueco_Cero_Aunque_El_Resto_Este_Lleno()
        {
            Zone zone = NewZone();
            PlayerEntity owner = Player(zone, 1);
            for (int i = 1; i < owner.Inventory.Length; i++)
                owner.Inventory[i] = new InventorySlot(4000, 1);
            NpcEntity npc = Npc(zone);
            npc.Health = 1;
            EnqueueAttack(zone, owner, npc);
            zone.Tick();
            Assert.Equal(0, zone.GroundItemCount);
            Assert.Equal((ushort)3001, owner.Inventory[0].ItemId);
        }

        [Fact]
        public void CombatSettings_Usa_200_Ticks_Y_Rechaza_Timeout_Invalido()
        {
            Assert.Equal(200, new CombatSettings(0).LootTagTimeoutTicks);
            Assert.Throws<ArgumentOutOfRangeException>(() => new CombatSettings(0, 0));
        }

        [Fact]
        public void Dueño_Se_Mantiene_Hasta_200_Ticks_Y_Los_Golpes_Propios_Renuevan_La_Marca()
        {
            var entities = new EntityStore();
            var first = new PlayerEntity(1, 1, 8) { Position = new Vec2(5, 5) };
            var second = new PlayerEntity(2, 2, 8) { Position = new Vec2(5, 5) };
            var npc = new NpcEntity(3, 2001, 0, new Vec2(5, 5), 1, 0, 10000, 100, -1)
            {
                Position = new Vec2(5, 5),
            };
            entities.Add(first);
            entities.Add(second);
            entities.Add(npc);
            var combat = new CombatSystem(new CombatSettings(1));

            void Hit(PlayerEntity player, long tick)
            {
                player.LastAttackTick = -1;
                combat.Tick(entities, new List<AttackIntent> { new AttackIntent(player.Id, npc.Id) }, tick);
                Assert.True(combat.AttacksOk > 0, $"ataques={combat.AttacksOk}, objetivo={combat.RejectedTarget}, distancia={combat.RejectedDistance}, muerto={combat.RejectedDead}");
            }

            Hit(first, 10);
            Assert.Equal(first.Id, npc.LootOwnerId);
            Hit(second, 209);
            Assert.Equal(first.Id, npc.LootOwnerId);
            Assert.Equal(0, combat.LootOwnerChanges);
            Hit(second, 210);
            Assert.Equal(second.Id, npc.LootOwnerId);
            Assert.Equal(1, combat.LootOwnerChanges);

            npc = new NpcEntity(4, 2001, 0, new Vec2(5, 5), 1, 0, 10000, 100, -1)
            {
                Position = new Vec2(5, 5),
            };
            entities.Add(npc);
            Hit(first, 400);
            Hit(first, 550);
            Hit(second, 700);
            Assert.Equal(first.Id, npc.LootOwnerId);
        }

        [Fact]
        public void Dueño_Ausente_Permite_Nueva_Marca_Y_Return_Limpia_La_Marca()
        {
            var entities = new EntityStore();
            var first = new PlayerEntity(1, 1, 8) { Position = new Vec2(5, 5) };
            var second = new PlayerEntity(2, 2, 8) { Position = new Vec2(5, 5) };
            var npc = new NpcEntity(3, 2001, 0, new Vec2(5, 5), 1, 0, 10000, 100, -1)
            {
                Position = new Vec2(5, 5),
            };
            entities.Add(first);
            entities.Add(second);
            entities.Add(npc);
            var combat = new CombatSystem(new CombatSettings(1));
            combat.Tick(entities, new List<AttackIntent> { new AttackIntent(first.Id, npc.Id) }, 1);
            entities.Remove(first.Id);
            second.LastAttackTick = -1;
            combat.Tick(entities, new List<AttackIntent> { new AttackIntent(second.Id, npc.Id) }, 2);
            Assert.True(combat.AttacksOk > 0, $"ataques={combat.AttacksOk}, objetivo={combat.RejectedTarget}, distancia={combat.RejectedDistance}, muerto={combat.RejectedDead}");
            Assert.Equal(second.Id, npc.LootOwnerId);
            Assert.Equal(1, combat.LootOwnerChanges);

            Zone zone = NewZone();
            PlayerEntity owner = Player(zone, 1);
            NpcEntity returning = Npc(zone);
            EnqueueAttack(zone, owner, returning);
            zone.Tick();
            Assert.Equal(owner.Id, returning.LootOwnerId);
            returning.Position = returning.Home;
            returning.AiState = NpcAiState.Return;
            zone.Tick();
            Assert.Equal(-1, returning.LootOwnerId);
            Assert.Equal(-1, returning.LastAttackerPlayerId);
        }

        [Fact]
        public void Botin_Y_Experiencia_Van_Al_Dueño_Aunque_Otro_De_El_Golpe_Final()
        {
            Zone zone = NewZone(Npcs(goldMin: 2, goldMax: 2));
            PlayerEntity owner = Player(zone, 1);
            PlayerEntity killer = Player(zone, 2);
            NpcEntity npc = Npc(zone);
            npc.Health = 11;
            LootGainedMessage gained = default;
            PlayerEntity? recipient = null;
            zone.LootGained += (player, message) => { recipient = player; gained = message; };

            EnqueueAttack(zone, owner, npc);
            zone.Tick();
            EnqueueAttack(zone, killer, npc);
            zone.Tick();

            Assert.Equal(owner.Id, recipient!.Id);
            Assert.Equal(1, owner.Inventory.Count(slot => slot.ItemId == 3001));
            Assert.Equal(0, killer.Inventory.Count(slot => slot.ItemId == 3001));
            Assert.Equal(20, owner.Experience);
            Assert.Equal(0, killer.Experience);
            Assert.Equal(2, owner.Gold);
            Assert.Equal(2u, gained.Gold);
            Assert.Single(gained.Items);
            Assert.False(gained.Items[0].OnGround);
            Assert.Equal(1, zone.ItemsLootedDirect);
            Assert.Equal(0, zone.GroundItemCount);
        }

        [Fact]
        public void Oro_Se_Satura_Y_El_Mensaje_Informa_Solo_La_Suma_Real()
        {
            Zone zone = NewZone(Npcs(goldMin: 5, goldMax: 5));
            PlayerEntity owner = Player(zone, 1);
            owner.Gold = PlayerEntity.MaxGold - 1;
            NpcEntity npc = Npc(zone);
            npc.Health = 1;
            LootGainedMessage gained = default;
            zone.LootGained += (_, message) => gained = message;

            EnqueueAttack(zone, owner, npc);
            zone.Tick();

            Assert.Equal(PlayerEntity.MaxGold, owner.Gold);
            Assert.Equal(1u, gained.Gold);
            Assert.Equal(1L, zone.GoldGranted);
        }

        [Fact]
        public void Muerte_Sin_Dueño_No_Entrega_Botin_Oro_Ni_Experiencia()
        {
            Zone zone = NewZone(Npcs(goldMin: 5, goldMax: 5));
            NpcEntity npc = Npc(zone);
            npc.Health = 0;
            npc.Action = EntityAction.Dead;
            zone.Tick();

            Assert.Equal(1, zone.NpcDeathsWithoutOwner);
            Assert.Equal(0, zone.GoldGranted);
            Assert.Equal(0, zone.ExperienceGranted);
            Assert.Equal(0, zone.ItemsDropped);
        }

        [Fact]
        public void Si_El_Dueño_Se_Fue_El_Ultimo_Atacante_Presente_Recibe_Todo()
        {
            Zone zone = NewZone(Npcs(goldMin: 2, goldMax: 2));
            PlayerEntity formerOwner = Player(zone, 1);
            PlayerEntity lastAttacker = Player(zone, 2);
            NpcEntity npc = Npc(zone);
            npc.Health = 100;
            EnqueueAttack(zone, formerOwner, npc);
            zone.Tick();
            Assert.Equal(formerOwner.Id, npc.LootOwnerId);
            Assert.True(zone.RemoveEntity(formerOwner.Id));

            npc.Health = 1;
            LootGainedMessage gained = default;
            PlayerEntity? recipient = null;
            zone.LootGained += (player, message) => { recipient = player; gained = message; };
            EnqueueAttack(zone, lastAttacker, npc);
            zone.Tick();

            Assert.Equal(lastAttacker.Id, recipient!.Id);
            Assert.Equal(2, lastAttacker.Gold);
            Assert.Equal(20, lastAttacker.Experience);
            Assert.Equal(2u, gained.Gold);
            Assert.Single(gained.Items);
        }

        [Fact]
        public void Botin_Caido_Se_Reserva_Al_Dueño_Y_Puede_Recogerse_Al_Vencer_La_Reserva()
        {
            Zone zone = NewZone();
            PlayerEntity owner = Player(zone, 1);
            PlayerEntity other = Player(zone, 2);
            for (int i = 0; i < owner.Inventory.Length; i++)
            {
                owner.Inventory[i] = new InventorySlot((ushort)(4000 + i), 1);
            }

            NpcEntity npc = Npc(zone);
            npc.Health = 1;
            LootGainedMessage gained = default;
            zone.LootGained += (_, message) => gained = message;
            EnqueueAttack(zone, owner, npc);
            zone.Tick();
            GroundItemEntity item = Assert.IsType<GroundItemEntity>(
                zone.Entities.All.Single(entity => entity is GroundItemEntity));
            Assert.Equal(owner.Id, item.OwnerPlayerId);
            Assert.Equal(600, item.ReservedUntilTick);
            Assert.Equal(1, zone.ItemsLootedToGround);
            Assert.True(gained.Items[0].OnGround);

            Assert.True(zone.ItemActions!.TryEnqueue(ItemAction.PickUp(other.Id, item.Id)));
            zone.Tick();
            Assert.Equal(1, zone.RejectedPickupReserved);
            Assert.True(zone.Entities.TryGet(item.Id, out _));

            while (zone.TickCount < item.ReservedUntilTick)
            {
                zone.Tick();
            }

            Assert.True(zone.ItemActions.TryEnqueue(ItemAction.PickUp(other.Id, item.Id)));
            zone.Tick();
            Assert.False(zone.Entities.TryGet(item.Id, out _));
            Assert.Equal(1, zone.ItemsPickedUp);
        }

        [Fact]
        public void Dueño_Puede_Recoger_Durante_La_Reserva_Tras_Liberar_Un_Hueco()
        {
            Zone zone = NewZone();
            PlayerEntity owner = Player(zone, 1);
            for (int i = 0; i < owner.Inventory.Length; i++)
            {
                owner.Inventory[i] = new InventorySlot((ushort)(4000 + i), 1);
            }

            NpcEntity npc = Npc(zone);
            npc.Health = 1;
            EnqueueAttack(zone, owner, npc);
            zone.Tick();
            GroundItemEntity item = Assert.IsType<GroundItemEntity>(
                zone.Entities.All.Single(entity => entity is GroundItemEntity));
            owner.Inventory[0] = default;

            Assert.True(zone.ItemActions!.TryEnqueue(ItemAction.PickUp(owner.Id, item.Id)));
            zone.Tick();
            Assert.False(zone.Entities.TryGet(item.Id, out _));
            Assert.Equal(1, zone.ItemsPickedUp);
            Assert.Equal(0, zone.RejectedPickupReserved);
        }

        [Fact]
        public void Elite_Tira_Oro_Y_Objetos_Por_Cada_Roll_Y_Fusiona_Las_Lineas()
        {
            NpcCatalog catalog = Npcs(goldMin: 2, goldMax: 2);
            catalog.Ranks = new List<NpcRank>
            {
                new NpcRank
                {
                    Id = 1, Name = "Rango de prueba", Chance = 1, Health = 1,
                    Damage = 1, Experience = 1, LootRolls = 3,
                },
            };
            Zone zone = NewZone(catalog);
            PlayerEntity owner = Player(zone, 1);
            NpcEntity npc = Npc(zone);
            npc.Health = 1;
            LootGainedMessage gained = default;
            zone.LootGained += (_, message) => gained = message;

            EnqueueAttack(zone, owner, npc);
            zone.Tick();

            Assert.Equal(6, owner.Gold);
            Assert.Equal(6u, gained.Gold);
            Assert.Single(gained.Items);
            Assert.Equal((ushort)3, gained.Items[0].Count);
            Assert.Equal(3, zone.ItemsLootedDirect);
        }

        [Fact]
        public void NpcCatalog_Rechaza_Oro_Fuera_De_Rango_Y_Mas_De_16_Entradas()
        {
            MapCollisionGrid map = MapCollisionGrid.AllWalkable(20, 20);
            Assert.Contains("goldMin=2, goldMax=1", Assert.Throws<InvalidOperationException>(
                () => CatalogForValidation(2, 1, 0).Validate(map)).Message);
            Assert.Contains("goldMin=-1", Assert.Throws<InvalidOperationException>(
                () => CatalogForValidation(-1, 1, 0).Validate(map)).Message);
            Assert.Contains("goldMax=10001", Assert.Throws<InvalidOperationException>(
                () => CatalogForValidation(0, 10001, 0).Validate(map)).Message);
            NpcCatalog tooMany = CatalogForValidation(0, 0, 17);
            Assert.Contains("17 entradas de loot; máximo 16", Assert.Throws<InvalidOperationException>(
                () => tooMany.Validate(map, ItemCatalogForLoot())).Message);
        }

        private static ItemCatalog ItemCatalogForLoot()
        {
            var items = new ItemCatalog { Items = new List<ItemType>() };
            for (int i = 1; i <= 17; i++)
            {
                items.Items.Add(new ItemType { Id = i, Name = "Item " + i, Kind = "material", MaxStack = 1 });
            }

            return items;
        }
    }
}
