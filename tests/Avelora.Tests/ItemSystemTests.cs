using System.Collections.Generic;
using System.Linq;
using Avelora.Server.Entities;
using Avelora.Server.Items;
using Avelora.Server.Npcs;
using Avelora.Server.Progression;
using Avelora.Server.Simulation;
using Avelora.Shared;
using Avelora.Shared.Protocol;
using Xunit;

namespace Avelora.Tests
{
    public sealed class ItemSystemTests
    {
        private static ItemCatalog Items() => new ItemCatalog
        {
            Items = new List<ItemType>
            {
                new ItemType { Id = 3001, Name = "Colmillo", Kind = "material", MaxStack = 5 },
                new ItemType { Id = 3002, Name = "Poción", Kind = "potion", MaxStack = 10, Heal = 30 },
                new ItemType { Id = 3101, Name = "Espada corta", Kind = "weapon", MaxStack = 1, Damage = 14, Range = 1.2f, CooldownSeconds = 0.8d },
                new ItemType { Id = 3102, Name = "Hacha de orco", Kind = "weapon", MaxStack = 1, Damage = 22, Range = 1.3f, CooldownSeconds = 1.2d },
                new ItemType { Id = 3301, Name = "Casco de cuero", Kind = "armor", Slot = "head", MaxStack = 1, Defense = 2 },
                new ItemType { Id = 3302, Name = "Casco de hierro", Kind = "armor", Slot = "head", MaxStack = 1, Defense = 4, MagicResist = 1 },
                new ItemType { Id = 3308, Name = "Anillo de vigor", Kind = "armor", Slot = "ring", MaxStack = 1, Vit = 2 },
            },
        };

        private static NpcCatalog Npcs(int count, params LootEntry[] loot)
        {
            var catalog = new NpcCatalog
            {
                Types = new List<NpcType>
                {
                    new NpcType
                    {
                        Id = 2001,
                        Name = "Orco",
                        Speed = 2,
                        HalfWidth = 0.35f,
                        HalfHeight = 0.35f,
                        MaxHealth = 30,
                        Experience = 20,
                        Level = 1,
                        RespawnSeconds = 10,
                        AggroRadius = 0,
                        LeashRadius = 8,
                        AttackDamage = 8,
                        AttackRange = 1,
                        AttackCooldownSeconds = 1.5f,
                        Loot = new List<LootEntry>(loot),
                    },
                },
                Spawners = new List<NpcSpawner>(),
            };
            if (count > 0)
            {
                catalog.Spawners.Add(new NpcSpawner
                {
                    TypeId = 2001, X = 5, Y = 5, Count = count, WanderRadius = 2,
                });
            }

            return catalog;
        }

        private static LootEntry Loot(int itemId = 3001, double chance = 1, int min = 1, int max = 3) =>
            new LootEntry { ItemId = itemId, Chance = chance, Min = min, Max = max };

        private static Zone NewZone(
            int seed = 12345,
            NpcCatalog? npcs = null,
            int maxGroundItems = 500,
            int slots = 4,
            long despawnTicks = 10,
            long cooldownTicks = 2,
            LevelTable? levels = null)
        {
            return new Zone(
                1,
                MapCollisionGrid.AllWalkable(20, 20),
                new EntityStore(),
                new EntityIdAllocator(),
                new InputQueue(64),
                new MovementSystem(4, SimulationConstants.FixedDt),
                128,
                4,
                new InterestSettings(12, 8, 1),
                npcs,
                seed,
                items: Items(),
                itemActions: new ItemActionQueue(64),
                inventorySlots: slots,
                maxGroundItems: maxGroundItems,
                groundItemTicks: despawnTicks,
                pickupRange: 1.5f,
                pickupRangeTolerance: 0.5f,
                potionCooldownTicks: cooldownTicks,
                levels: levels);
        }

        private static PlayerEntity SpawnPlayer(Zone zone, float x = 5, float y = 5) =>
            zone.SpawnPlayer(1, new Vec2(x, y), new Vec2(0.4f, 0.4f), 8);

        private static GroundItemEntity[] GroundItems(Zone zone) =>
            zone.Entities.All.OfType<GroundItemEntity>().ToArray();

        [Fact]
        public void Botin_Chance_Uno_Crea_Cantidad_Valida_Y_Es_Determinista()
        {
            Zone first = NewZone(seed: 777, npcs: Npcs(2, Loot(min: 2, max: 3)));
            Zone second = NewZone(seed: 777, npcs: Npcs(2, Loot(min: 2, max: 3)));
            foreach (Zone zone in new[] { first, second })
            {
                for (int i = 0; i < zone.Entities.Count; i++)
                {
                    if (zone.Entities[i] is NpcEntity npc)
                    {
                        npc.Action = EntityAction.Dead;
                        npc.Health = 0;
                    }
                }

                zone.Tick();
            }

            GroundItemEntity[] a = GroundItems(first);
            GroundItemEntity[] b = GroundItems(second);
            Assert.Equal(2, a.Length);
            Assert.Equal(2, b.Length);
            for (int i = 0; i < a.Length; i++)
            {
                Assert.InRange(a[i].Count, 2, 3);
                Assert.InRange(a[i].Position.X, 4.6f, 5.4f);
                Assert.InRange(a[i].Position.Y, 4.6f, 5.4f);
                Assert.Equal(a[i].ItemId, b[i].ItemId);
                Assert.Equal(a[i].Count, b[i].Count);
                Assert.Equal(a[i].Position, b[i].Position);
            }
        }

        [Fact]
        public void Rango_Repite_La_Tabla_De_Botin_Y_Respeta_El_Tope_De_Suelo()
        {
            NpcCatalog catalog = Npcs(1, Loot(chance: 1));
            catalog.Ranks = new List<NpcRank>
            {
                new NpcRank
                {
                    Id = 1,
                    Name = "Rango",
                    Chance = 1,
                    Health = 1,
                    Damage = 1,
                    Experience = 1,
                    LootRolls = 2,
                },
            };
            Zone zone = NewZone(npcs: catalog, maxGroundItems: 1);
            NpcEntity npc = Assert.IsType<NpcEntity>(zone.Entities[0]);
            Assert.Equal(2, npc.LootRolls);
            npc.Action = EntityAction.Dead;
            npc.Health = 0;

            zone.Tick();

            Assert.Single(GroundItems(zone));
            Assert.Equal(1, zone.ItemsDropped);
            Assert.Equal(1, zone.ItemsDiscarded);
        }

        [Fact]
        public void Tope_De_Suelo_Descarta_Nuevos_Y_Caduca_En_El_Tick_Exacto()
        {
            Zone zone = NewZone(
                npcs: Npcs(2, Loot()),
                maxGroundItems: 1,
                despawnTicks: 2);
            foreach (Entity entity in zone.Entities.All)
            {
                if (entity is NpcEntity npc)
                {
                    npc.Action = EntityAction.Dead;
                }
            }

            zone.Tick();
            Assert.Equal(1, zone.GroundItemCount);
            Assert.Equal(1, zone.ItemsDropped);
            Assert.Equal(1, zone.ItemsDiscarded);
            zone.Tick();
            Assert.Equal(1, zone.GroundItemCount);
            zone.Tick();
            Assert.Equal(0, zone.GroundItemCount);
            Assert.Equal(1, zone.ItemsExpired);
        }

        [Fact]
        public void Un_Pnj_Muerto_Deja_Un_Item_Visible_En_El_Area_De_Interes()
        {
            Zone zone = NewZone(npcs: Npcs(1, Loot(chance: 1)));
            PlayerEntity player = SpawnPlayer(zone);
            NpcEntity npc = Assert.IsType<NpcEntity>(zone.Entities.All[0]);
            npc.Action = EntityAction.Dead;
            npc.Health = 0;

            zone.Tick();

            var visible = new List<Entity>();
            zone.CollectVisibleEntities(player, visible);
            GroundItemEntity ground = Assert.IsType<GroundItemEntity>(
                Assert.Single(visible, entity => entity is GroundItemEntity));
            Assert.Equal(EntityKind.Item, ground.Kind);
            Assert.Equal(3001, ground.ItemId);
        }

        [Fact]
        public void Recoger_Rellena_Primero_La_Pila_Y_Luego_Los_Huecos()
        {
            Zone zone = NewZone(slots: 3);
            PlayerEntity player = SpawnPlayer(zone);
            player.Inventory[0] = new InventorySlot(3001, 3);
            GroundItemEntity item = AddGroundItem(zone, 3001, 7, new Vec2(5.5f, 5));

            Assert.True(zone.ItemActions!.TryEnqueue(ItemAction.PickUp(player.Id, item.Id)));
            zone.Tick();

            Assert.Equal(new InventorySlot(3001, 5), player.Inventory[0]);
            Assert.Equal(new InventorySlot(3001, 5), player.Inventory[1]);
            Assert.Equal(1, zone.ItemsPickedUp);
            Assert.False(zone.Entities.TryGet(item.Id, out _));
        }

        [Fact]
        public void Recoger_No_Cabe_Entero_No_Modifica_El_Inventario()
        {
            Zone zone = NewZone(slots: 1);
            PlayerEntity player = SpawnPlayer(zone);
            player.Inventory[0] = new InventorySlot(3001, 4);
            GroundItemEntity item = AddGroundItem(zone, 3001, 2, new Vec2(5.5f, 5));
            zone.ItemActions!.TryEnqueue(ItemAction.PickUp(player.Id, item.Id));

            zone.Tick();

            Assert.Equal(new InventorySlot(3001, 4), player.Inventory[0]);
            Assert.True(zone.Entities.TryGet(item.Id, out _));
            Assert.Equal(1, zone.RejectedPickupFull);
        }

        [Fact]
        public void Dos_Jugadores_Que_Piden_El_Mismo_Objeto_Ganan_Por_Orden_De_Cola()
        {
            Zone zone = NewZone(slots: 1);
            PlayerEntity first = SpawnPlayer(zone);
            PlayerEntity second = zone.SpawnPlayer(2, new Vec2(5.1f, 5), new Vec2(0.4f, 0.4f), 8);
            GroundItemEntity item = AddGroundItem(zone, 3001, 1, new Vec2(5, 5));
            zone.ItemActions!.TryEnqueue(ItemAction.PickUp(first.Id, item.Id));
            zone.ItemActions.TryEnqueue(ItemAction.PickUp(second.Id, item.Id));

            zone.Tick();

            Assert.Equal((ushort)3001, first.Inventory[0].ItemId);
            Assert.Equal((ushort)0, second.Inventory[0].ItemId);
            Assert.Equal(1, zone.ItemsPickedUp);
            Assert.Equal(1, zone.RejectedPickupItem);
        }

        [Fact]
        public void Recoger_Rechaza_Jugador_Muerto_Objeto_Inexistente_Y_Distancia()
        {
            Zone zone = NewZone(slots: 2);
            PlayerEntity player = SpawnPlayer(zone);
            GroundItemEntity item = AddGroundItem(zone, 3001, 1, new Vec2(5, 5));
            player.Action = EntityAction.Dead;
            zone.ItemActions!.TryEnqueue(ItemAction.PickUp(player.Id, item.Id));
            zone.Tick();
            player.Action = EntityAction.None;
            zone.ItemActions.TryEnqueue(ItemAction.PickUp(player.Id, int.MaxValue));
            zone.Tick();
            Entity impostor = new Entity(zone.Ids.Allocate(), EntityKind.Item) { Position = new Vec2(5, 5) };
            zone.AddEntity(impostor);
            zone.ItemActions.TryEnqueue(ItemAction.PickUp(player.Id, impostor.Id));
            zone.Tick();
            GroundItemEntity far = AddGroundItem(zone, 3001, 1, new Vec2(15, 15));
            zone.ItemActions.TryEnqueue(ItemAction.PickUp(player.Id, far.Id));
            zone.Tick();

            Assert.Equal(1, zone.RejectedPickupDead);
            Assert.Equal(2, zone.RejectedPickupItem);
            Assert.Equal(1, zone.RejectedPickupDistance);
        }

        [Fact]
        public void Usar_Pocion_Cura_Sin_Superar_Maximo_Y_Vacia_El_Ultimo_Hueco()
        {
            Zone zone = NewZone(slots: 2);
            PlayerEntity player = SpawnPlayer(zone);
            player.Health = 80;
            player.Inventory[1] = new InventorySlot(3002, 1);
            zone.ItemActions!.TryEnqueue(ItemAction.UseItem(player.Id, 1));

            zone.Tick();

            Assert.Equal(100, player.Health);
            Assert.Equal(new InventorySlot(0, 0), player.Inventory[1]);
            Assert.Equal(1, zone.PotionsUsed);
        }

        [Fact]
        public void Pocion_En_Nivel_Alto_No_Supera_La_Vida_Maxima_Crecida()
        {
            var levels = new LevelTable
            {
                ExperienceToNext = new[] { 100, 150 },
            };
            Zone zone = NewZone(slots: 1, levels: levels);
            PlayerEntity player = zone.SpawnPlayer(
                1, new Vec2(5f, 5f), new Vec2(0.4f, 0.4f), 8, level: 3);
            player.Health = 90;
            player.Inventory[0] = new InventorySlot(3002, 1);
            zone.ItemActions!.TryEnqueue(ItemAction.UseItem(player.Id, 0));

            zone.Tick();

            Assert.Equal(100, player.MaxHealth);
            Assert.Equal(100, player.Health);
        }

        [Fact]
        public void Usar_Pocion_Rechaza_Objeto_Vida_Llena_Y_Recarga()
        {
            Zone zone = NewZone(slots: 2, cooldownTicks: 3);
            PlayerEntity player = SpawnPlayer(zone);
            player.Inventory[0] = new InventorySlot(3001, 1);
            player.Inventory[1] = new InventorySlot(3002, 2);
            zone.ItemActions!.TryEnqueue(ItemAction.UseItem(player.Id, 0));
            zone.Tick();
            zone.ItemActions.TryEnqueue(ItemAction.UseItem(player.Id, 1));
            zone.Tick();
            player.Health = 50;
            zone.ItemActions.TryEnqueue(ItemAction.UseItem(player.Id, 1));
            zone.Tick();
            zone.ItemActions.TryEnqueue(ItemAction.UseItem(player.Id, 1));
            zone.Tick();

            Assert.Equal(80, player.Health);
            Assert.Equal(1, zone.RejectedUseItem);
            Assert.Equal(1, zone.RejectedUseFull);
            Assert.Equal(1, zone.RejectedUseCooldown);
            Assert.Equal((ushort)1, player.Inventory[1].Count);
        }

        [Fact]
        public void Un_Jugador_Muerto_No_Puede_Usar_Pociones()
        {
            Zone zone = NewZone(slots: 1);
            PlayerEntity player = SpawnPlayer(zone);
            player.Action = EntityAction.Dead;
            player.Inventory[0] = new InventorySlot(3002, 1);
            zone.ItemActions!.TryEnqueue(ItemAction.UseItem(player.Id, 0));

            zone.Tick();

            Assert.Equal(1, zone.RejectedUseDead);
            Assert.Equal((ushort)1, player.Inventory[0].Count);
        }

        [Fact]
        public void Equipar_Intercambia_El_Arma_Anterior_En_El_Mismo_Hueco()
        {
            Zone zone = NewZone(slots: 2);
            PlayerEntity player = SpawnPlayer(zone);
            player.EquippedWeaponId = 3101;
            player.Inventory[1] = new InventorySlot(3102, 1);

            zone.ItemActions!.TryEnqueue(ItemAction.Equip(player.Id, 1));
            zone.Tick();

            Assert.Equal((ushort)3102, player.EquippedWeaponId);
            Assert.Equal(new InventorySlot(3101, 1), player.Inventory[1]);
            Assert.Equal(1, zone.ItemsEquipped);
            Assert.Equal(0, zone.RejectedEquipItem);
        }

        [Fact]
        public void Equipar_Arma_Sin_Arma_Anterior_Vacia_El_Hueco()
        {
            Zone zone = NewZone(slots: 1);
            PlayerEntity player = SpawnPlayer(zone);
            player.Inventory[0] = new InventorySlot(3101, 1);
            zone.ItemActions!.TryEnqueue(ItemAction.Equip(player.Id, 0));

            zone.Tick();

            Assert.Equal((ushort)3101, player.EquippedWeaponId);
            Assert.Equal(new InventorySlot(0, 0), player.Inventory[0]);
        }

        [Fact]
        public void Equipar_Rechaza_Jugador_Muerto()
        {
            Zone zone = NewZone(slots: 1);
            PlayerEntity player = SpawnPlayer(zone);
            player.Action = EntityAction.Dead;
            player.Inventory[0] = new InventorySlot(3101, 1);
            zone.ItemActions!.TryEnqueue(ItemAction.Equip(player.Id, 0));

            zone.Tick();

            Assert.Equal(1, zone.RejectedEquipDead);
            Assert.Equal((ushort)0, player.EquippedWeaponId);
            Assert.Equal((ushort)3101, player.Inventory[0].ItemId);
        }

        [Fact]
        public void Equipar_Rechaza_Hueco_Vacio_Fuera_De_Rango_Y_Objeto_Que_No_Es_Arma()
        {
            Zone zone = NewZone(slots: 2);
            PlayerEntity player = SpawnPlayer(zone);
            player.Inventory[0] = new InventorySlot(3001, 1);
            zone.ItemActions!.TryEnqueue(ItemAction.Equip(player.Id, 0));
            zone.ItemActions.TryEnqueue(ItemAction.Equip(player.Id, 1));
            zone.ItemActions.TryEnqueue(ItemAction.Equip(player.Id, 2));

            zone.Tick();

            Assert.Equal(1, zone.RejectedEquipItem);
            Assert.Equal(2, zone.RejectedEquipEmpty);
            Assert.Equal((ushort)0, player.EquippedWeaponId);
            Assert.Equal((ushort)3001, player.Inventory[0].ItemId);
        }

        [Fact]
        public void Desequipar_Guarda_El_Arma_En_El_Primer_Hueco_Libre()
        {
            Zone zone = NewZone(slots: 3);
            PlayerEntity player = SpawnPlayer(zone);
            player.EquippedWeaponId = 3102;
            player.Inventory[0] = new InventorySlot(3001, 1);
            player.Inventory[2] = new InventorySlot(3002, 1);
            zone.ItemActions!.TryEnqueue(ItemAction.Unequip(player.Id, (byte)EquipSlot.Weapon, byte.MaxValue));

            zone.Tick();

            Assert.Equal((ushort)0, player.EquippedWeaponId);
            Assert.Equal(new InventorySlot(3102, 1), player.Inventory[1]);
            Assert.Equal(1, zone.ItemsUnequipped);
        }

        [Fact]
        public void Desequipar_Rechaza_Inventario_Lleno_Y_Sin_Arma()
        {
            Zone emptyZone = NewZone(slots: 1);
            PlayerEntity emptyPlayer = SpawnPlayer(emptyZone);
            emptyZone.ItemActions!.TryEnqueue(ItemAction.Unequip(emptyPlayer.Id, (byte)EquipSlot.Weapon, byte.MaxValue));
            emptyZone.Tick();
            Assert.Equal(1, emptyZone.RejectedUnequipEmpty);

            Zone fullZone = NewZone(slots: 1);
            PlayerEntity fullPlayer = SpawnPlayer(fullZone);
            fullPlayer.EquippedWeaponId = 3101;
            fullPlayer.Inventory[0] = new InventorySlot(3001, 1);
            fullZone.ItemActions!.TryEnqueue(ItemAction.Unequip(fullPlayer.Id, (byte)EquipSlot.Weapon, byte.MaxValue));
            fullZone.Tick();
            Assert.Equal(1, fullZone.RejectedEquipFull);
            Assert.Equal((ushort)3101, fullPlayer.EquippedWeaponId);
            Assert.Equal((ushort)3001, fullPlayer.Inventory[0].ItemId);
        }

        [Fact]
        public void Equipar_Armadura_Intercambia_Con_El_Mismo_Hueco_Aunque_El_Inventario_Este_Lleno()
        {
            Zone zone = NewZone(slots: 1);
            PlayerEntity player = SpawnPlayer(zone);
            player.Equipment[(int)EquipSlot.Head] = 3301;
            player.Inventory[0] = new InventorySlot(3302, 1);

            zone.ItemActions!.TryEnqueue(ItemAction.Equip(player.Id, 0));
            zone.Tick();

            Assert.Equal((ushort)3302, player.Equipment[(int)EquipSlot.Head]);
            Assert.Equal(new InventorySlot(3301, 1), player.Inventory[0]);
            Assert.Equal(1, zone.EquipmentSwaps);
            Assert.Equal(1, zone.ItemsEquipped);
        }

        [Fact]
        public void Desequipar_A_Hueco_Con_Objeto_Del_Mismo_Hueco_Intercambia_Y_Otro_Objeto_Rechaza()
        {
            Zone zone = NewZone(slots: 2);
            PlayerEntity player = SpawnPlayer(zone);
            player.Equipment[(int)EquipSlot.Head] = 3301;
            player.Inventory[0] = new InventorySlot(3302, 1);
            player.Inventory[1] = new InventorySlot(3001, 1);

            zone.ItemActions!.TryEnqueue(ItemAction.Unequip(player.Id, (byte)EquipSlot.Head, 0));
            zone.ItemActions.TryEnqueue(ItemAction.Unequip(player.Id, (byte)EquipSlot.Head, 1));
            zone.Tick();

            Assert.Equal((ushort)3302, player.Equipment[(int)EquipSlot.Head]);
            Assert.Equal(new InventorySlot(3301, 1), player.Inventory[0]);
            Assert.Equal(new InventorySlot(3001, 1), player.Inventory[1]);
            Assert.Equal(1, zone.RejectedEquipmentDestination);
            Assert.Equal(1, zone.ItemsUnequipped);
        }

        [Fact]
        public void Bonus_De_VIT_Al_Equipar_No_Cura_Y_Al_Desequipar_Recorta_Vida()
        {
            Zone zone = NewZone(slots: 1);
            PlayerEntity player = SpawnPlayer(zone);
            player.Health = 70;
            player.Inventory[0] = new InventorySlot(3308, 1);

            zone.ItemActions!.TryEnqueue(ItemAction.Equip(player.Id, 0));
            zone.Tick();
            Assert.Equal(110, player.MaxHealth);
            Assert.Equal(70, player.Health);
            Assert.Equal(12, ReadStats(zone, player).Vit);

            player.Health = 108;
            zone.ItemActions!.TryEnqueue(ItemAction.Unequip(player.Id, (byte)EquipSlot.Ring, byte.MaxValue));
            zone.Tick();
            Assert.Equal(100, player.MaxHealth);
            Assert.Equal(100, player.Health);
        }

        [Fact]
        public void Armadura_Reduce_El_Daño_Que_Inflige_Un_Pnj()
        {
            Zone zone = NewZone(npcs: Npcs(1));
            PlayerEntity player = SpawnPlayer(zone);
            player.Inventory[0] = new InventorySlot(3301, 1);
            zone.ItemActions!.TryEnqueue(ItemAction.Equip(player.Id, 0));
            zone.Tick();
            player.Health = 100;
            NpcEntity npc = Assert.IsType<NpcEntity>(zone.Entities[0]);
            npc.AiState = NpcAiState.Attack;
            npc.TargetEntityId = player.Id;

            zone.Tick();

            Assert.Equal(93, player.Health);
        }

        [Fact]
        public void MoveItem_Mueve_A_Hueco_Vacio_Y_Divide_Una_Pila()
        {
            Zone zone = NewZone(slots: 3);
            PlayerEntity player = SpawnPlayer(zone);
            player.Inventory[0] = new InventorySlot(3001, 5);

            zone.ItemActions!.TryEnqueue(ItemAction.MoveItem(player.Id, 0, 1, 2));
            zone.Tick();
            Assert.Equal(new InventorySlot(3001, 3), player.Inventory[0]);
            Assert.Equal(new InventorySlot(3001, 2), player.Inventory[1]);
            Assert.Equal(1, zone.ItemsSplit);

            zone.ItemActions.TryEnqueue(ItemAction.MoveItem(player.Id, 1, 2, 0));
            zone.Tick();
            Assert.Equal(new InventorySlot(0, 0), player.Inventory[1]);
            Assert.Equal(new InventorySlot(3001, 2), player.Inventory[2]);
            Assert.Equal(2, zone.ItemsMoved);
        }

        [Fact]
        public void MoveItem_Junta_Hasta_MaxStack_Y_Deja_El_Sobrante_En_Origen()
        {
            Zone zone = NewZone(slots: 2);
            PlayerEntity player = SpawnPlayer(zone);
            player.Inventory[0] = new InventorySlot(3001, 4);
            player.Inventory[1] = new InventorySlot(3001, 4);

            zone.ItemActions!.TryEnqueue(ItemAction.MoveItem(player.Id, 0, 1, 0));
            zone.Tick();

            Assert.Equal(new InventorySlot(3001, 3), player.Inventory[0]);
            Assert.Equal(new InventorySlot(3001, 5), player.Inventory[1]);
            Assert.Equal(1, zone.ItemsMerged);
        }

        [Fact]
        public void MoveItem_Intercambia_Pilas_Y_Rechaza_Intercambio_Parcial_Sin_Cambios()
        {
            Zone zone = NewZone(slots: 2);
            PlayerEntity player = SpawnPlayer(zone);
            player.Inventory[0] = new InventorySlot(3001, 2);
            player.Inventory[1] = new InventorySlot(3002, 3);

            zone.ItemActions!.TryEnqueue(ItemAction.MoveItem(player.Id, 0, 1, 1));
            zone.Tick();
            Assert.Equal(new InventorySlot(3001, 2), player.Inventory[0]);
            Assert.Equal(new InventorySlot(3002, 3), player.Inventory[1]);
            Assert.Equal(1, zone.RejectedMoveCount);

            zone.ItemActions.TryEnqueue(ItemAction.MoveItem(player.Id, 0, 1, 0));
            zone.Tick();
            Assert.Equal(new InventorySlot(3002, 3), player.Inventory[0]);
            Assert.Equal(new InventorySlot(3001, 2), player.Inventory[1]);
            Assert.Equal(1, zone.ItemsSwapped);
        }

        [Fact]
        public void MoveItem_Rechaza_Mismo_Hueco_Vacio_Fuera_De_Rango_Y_Cantidad_Excesiva()
        {
            Zone zone = NewZone(slots: 2);
            PlayerEntity player = SpawnPlayer(zone);
            player.Inventory[0] = new InventorySlot(3001, 2);

            zone.ItemActions!.TryEnqueue(ItemAction.MoveItem(player.Id, 0, 0, 0));
            zone.ItemActions.TryEnqueue(ItemAction.MoveItem(player.Id, 2, 0, 0));
            zone.ItemActions.TryEnqueue(ItemAction.MoveItem(player.Id, 1, 0, 0));
            zone.ItemActions.TryEnqueue(ItemAction.MoveItem(player.Id, 0, 1, 3));
            zone.Tick();

            Assert.Equal(1, zone.RejectedMoveSameSlot);
            Assert.Equal(1, zone.RejectedMoveSlot);
            Assert.Equal(1, zone.RejectedMoveEmpty);
            Assert.Equal(1, zone.RejectedMoveCount);
            Assert.Equal(new InventorySlot(3001, 2), player.Inventory[0]);
            Assert.Equal(new InventorySlot(0, 0), player.Inventory[1]);
        }

        [Fact]
        public void DropItem_Crea_Botin_Recogible_Y_El_Tope_Rechaza_Sin_Perdida()
        {
            Zone zone = NewZone(slots: 1, maxGroundItems: 1);
            PlayerEntity player = SpawnPlayer(zone);
            player.Inventory[0] = new InventorySlot(3001, 5);

            zone.ItemActions!.TryEnqueue(ItemAction.DropItem(player.Id, 0, 2));
            zone.Tick();
            GroundItemEntity dropped = Assert.Single(GroundItems(zone));
            Assert.Equal(3001, dropped.ItemId);
            Assert.Equal(2, dropped.Count);
            Assert.Equal(new InventorySlot(3001, 3), player.Inventory[0]);

            zone.ItemActions.TryEnqueue(ItemAction.DropItem(player.Id, 0, 0));
            zone.Tick();
            Assert.Equal(1, zone.RejectedDropFull);
            Assert.Equal(new InventorySlot(3001, 3), player.Inventory[0]);

            zone.ItemActions.TryEnqueue(ItemAction.PickUp(player.Id, dropped.Id));
            zone.Tick();
            Assert.Equal(new InventorySlot(3001, 5), player.Inventory[0]);
            Assert.False(zone.Entities.TryGet(dropped.Id, out _));
        }

        [Fact]
        public void DestroyItem_Destruye_Parcial_Y_Totalmente()
        {
            Zone zone = NewZone(slots: 1);
            PlayerEntity player = SpawnPlayer(zone);
            player.Inventory[0] = new InventorySlot(3001, 5);

            zone.ItemActions!.TryEnqueue(ItemAction.DestroyItem(player.Id, 0, 2));
            zone.Tick();
            Assert.Equal(new InventorySlot(3001, 3), player.Inventory[0]);

            zone.ItemActions.TryEnqueue(ItemAction.DestroyItem(player.Id, 0, 0));
            zone.Tick();
            Assert.Equal(new InventorySlot(0, 0), player.Inventory[0]);
            Assert.Equal(2, zone.ItemsDestroyed);
        }

        [Fact]
        public void Mil_Acciones_Aleatorias_No_Duplican_Ni_Pierden_Objetos()
        {
            Zone zone = NewZone(slots: 8);
            PlayerEntity player = SpawnPlayer(zone);
            player.Inventory[0] = new InventorySlot(3001, 5);
            player.Inventory[1] = new InventorySlot(3001, 4);
            player.Inventory[2] = new InventorySlot(3002, 3);
            player.Inventory[3] = new InventorySlot(3101, 1);
            player.Inventory[4] = new InventorySlot(3102, 1);
            player.Inventory[5] = new InventorySlot(3301, 1);
            player.Inventory[6] = new InventorySlot(3302, 1);
            player.Equipment[(int)EquipSlot.Weapon] = 3101;
            player.Equipment[(int)EquipSlot.Head] = 3301;
            var random = new System.Random(14014);

            for (int i = 0; i < 2000; i++)
            {
                Dictionary<int, int> before = CountItems(player);
                int action = random.Next(3);
                if (action == 0)
                {
                    zone.ItemActions!.TryEnqueue(ItemAction.MoveItem(
                        player.Id, (byte)random.Next(player.Inventory.Length), (byte)random.Next(player.Inventory.Length),
                        (ushort)random.Next(0, 7)));
                }
                else if (action == 1)
                {
                    zone.ItemActions!.TryEnqueue(ItemAction.Equip(player.Id, (byte)random.Next(player.Inventory.Length)));
                }
                else
                {
                    byte equipSlot = (byte)random.Next(ProtocolConstants.EquipSlotCount);
                    byte targetSlot = random.Next(2) == 0 ? byte.MaxValue : (byte)random.Next(player.Inventory.Length);
                    zone.ItemActions!.TryEnqueue(ItemAction.Unequip(player.Id, equipSlot, targetSlot));
                }

                zone.Tick();
                Assert.Equal(before.OrderBy(pair => pair.Key), CountItems(player).OrderBy(pair => pair.Key));
                AssertValidStacks(player);
            }
        }

        private static DerivedCharacterStats ReadStats(Zone zone, PlayerEntity player)
        {
            return CharacterStats.Calculate(
                zone.Classes, player.ClassId, player.Level,
                player.AllocStr, player.AllocDex, player.AllocInt, player.AllocVit,
                zone.Items, player.Equipment);
        }

        private static Dictionary<int, int> CountItems(PlayerEntity player)
        {
            var counts = new Dictionary<int, int>();
            for (int i = 0; i < player.Inventory.Length; i++)
            {
                AddCount(counts, player.Inventory[i].ItemId, player.Inventory[i].Count);
            }

            for (int i = 0; i < player.Equipment.Length; i++)
            {
                AddCount(counts, player.Equipment[i], player.Equipment[i] == 0 ? 0 : 1);
            }

            return counts;
        }

        private static void AddCount(Dictionary<int, int> counts, int itemId, int count)
        {
            if (itemId == 0)
            {
                Assert.Equal(0, count);
                return;
            }

            counts.TryGetValue(itemId, out int existing);
            counts[itemId] = existing + count;
        }

        private static void AssertValidStacks(PlayerEntity player)
        {
            ItemCatalog catalog = Items();
            for (int i = 0; i < player.Inventory.Length; i++)
            {
                InventorySlot slot = player.Inventory[i];
                Assert.Equal(slot.ItemId == 0, slot.Count == 0);
                if (slot.ItemId != 0)
                {
                    Assert.True(slot.Count <= catalog.Find(slot.ItemId)!.MaxStack);
                }
            }

            for (int i = 0; i < player.Equipment.Length; i++)
            {
                ushort itemId = player.Equipment[i];
                if (itemId != 0)
                {
                    Assert.True(catalog.Find(itemId)!.TryGetEquipSlot(out byte slot));
                    Assert.Equal((byte)i, slot);
                }
            }
        }

        [Fact]
        public void Cola_De_Acciones_De_Objeto_Tiene_Tope()
        {
            var queue = new ItemActionQueue(1);
            Assert.True(queue.TryEnqueue(ItemAction.UseItem(1, 0)));
            Assert.False(queue.TryEnqueue(ItemAction.UseItem(1, 0)));
            Assert.Equal(1, queue.RejectedQueueFull);
        }

        private static GroundItemEntity AddGroundItem(Zone zone, int itemId, int count, Vec2 position)
        {
            var item = new GroundItemEntity(zone.Ids.Allocate(), itemId, count, 100)
            {
                Position = position,
            };
            zone.AddEntity(item);
            return item;
        }
    }
}
