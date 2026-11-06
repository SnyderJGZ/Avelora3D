using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Avelora.Net;
using Avelora.Server.Configuration;
using Avelora.Server.Entities;
using Avelora.Server.Items;
using Avelora.Server.Net;
using Avelora.Server.Npcs;
using Avelora.Server.Persistence;
using Avelora.Server.Progression;
using Avelora.Server.Simulation;
using Avelora.Server.Zones;
using Avelora.Shared;
using Avelora.Shared.Maps;
using Avelora.Shared.Protocol;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Avelora.Tests
{
    /// <summary>
    /// Pruebas de la capa de sesiones sin sockets: se sustituye el transporte por uno falso y se
    /// llaman los mismos métodos que llamarían los dos hilos reales.
    /// </summary>
    public class NetworkServerTests
    {
        private static string MapPath =>
            Path.Combine(AppContext.BaseDirectory, "maps", "avelora-etapa-a.avmap");

        private static int QuerySchemaVersion(string database)
        {
            using var connection = new SqliteConnection("Data Source=" + database);
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static Zone NewZone(ServerConfig config, bool withItems = false, NpcCatalog? npcs = null, ItemCatalog? customItems = null, ClassCatalog? classes = null, ZoneMap? zones = null, SkillCatalog? skills = null)
        {
            MapCollisionGrid map = MapFile.Load(MapPath);
            var items = withItems
                ? new ItemCatalog
                {
                    Items = new List<ItemType>
                    {
                        new ItemType { Id = 3001, Name = "Colmillo", Kind = "material", MaxStack = 99 },
                        new ItemType { Id = 3002, Name = "Poción", Kind = "potion", MaxStack = 10, Heal = 30 },
                        new ItemType { Id = 3101, Name = "Espada corta", Kind = "weapon", WeaponType = "Espada", MaxStack = 1, Damage = 14, Range = 1.2f, CooldownSeconds = 0.8d },
                        new ItemType { Id = 3201, Name = "Piedra de reajuste", Kind = "material", MaxStack = 10 },
                        new ItemType { Id = 3301, Name = "Casco de cuero", Kind = "armor", Slot = "head", MaxStack = 1, Defense = 2 },
                    },
                }
                : null;

            return new Zone(
                1,
                map,
                new EntityStore(),
                new EntityIdAllocator(),
                new InputQueue(config.MaxQueuedIntents),
                new MovementSystem(config.PlayerSpeed, SimulationConstants.FixedDt),
                config.MaxIntentsPerTick,
                config.MaxIntentsPerPlayerPerTick,
                new InterestSettings(config.InterestRadius, config.InterestCellSize, config.InterestHysteresis),
                npcs: npcs,
                npcSeed: 12345,
                attacks: new AttackQueue(config.MaxQueuedAttacks),
                combat: new CombatSettings(config.AttackRangeTolerance),
                playerVitals: config.PlayerVitals,
                items: customItems ?? items,
                classes: classes,
                itemActions: new ItemActionQueue(config.MaxQueuedItemActions),
                inventorySlots: config.InventorySlots,
                maxGroundItems: config.MaxGroundItems,
                groundItemTicks: (long)Math.Ceiling(config.GroundItemSeconds * SimulationConstants.TickRate),
                pickupRange: config.PickupRange,
                pickupRangeTolerance: config.AttackRangeTolerance,
                potionCooldownTicks: (long)Math.Ceiling(config.PotionCooldownSeconds * SimulationConstants.TickRate),
                zones: zones,
                pvpModeChangeTicks: (int)Math.Ceiling(config.PvpModeChangeSeconds * SimulationConstants.TickRate),
                pvpModeChangeFromPvpTicks: (int)Math.Ceiling(
                    config.PvpModeChangeFromPvpSeconds * SimulationConstants.TickRate),
                pvpForcedTicks: (int)Math.Ceiling(config.PvpForcedSeconds * SimulationConstants.TickRate),
                skills: skills);
        }

        [Fact]
        public void Network_Envia_ZoneList_Tras_Welcome_Y_ZoneChanged_Solo_Al_Jugador_Que_Cruza()
        {
            var config = new ServerConfig();
            int spawnTileX = (int)Math.Floor(config.SpawnX);
            int spawnTileY = (int)Math.Floor(config.SpawnY);
            MapCollisionGrid map = MapFile.Load(MapPath);
            ZoneMap zones = ZoneMap.FromDefinitions(new[]
            {
                new ZoneDefinition
                {
                    Id = 1,
                    Name = "Zona de red",
                    Kind = "safe",
                    Rects = new List<ZoneRectDefinition>
                    {
                        new ZoneRectDefinition { X = spawnTileX, Y = spawnTileY, W = 1, H = 1 },
                    },
                },
            }, map);

            using var harness = new Harness(zones: zones);
            harness.Connect(1);
            harness.Handshake(1);
            harness.Connect(2);
            harness.Handshake(2);
            harness.Pump();

            foreach (int connectionId in new[] { 1, 2 })
            {
                int welcomeIndex = harness.Transport.Sent.FindIndex(sent => sent.ConnectionId == connectionId &&
                    MessageCodec.TryReadWelcome(sent.Data, 0, sent.Data.Length, out _));
                int listIndex = harness.Transport.Sent.FindIndex(sent => sent.ConnectionId == connectionId &&
                    MessageCodec.TryReadZoneList(sent.Data, 0, sent.Data.Length, out ZoneListMessage list) && list.Zones.Length == 1);
                Assert.True(welcomeIndex >= 0);
                Assert.Equal(welcomeIndex + 1, listIndex);
            }

            harness.Transport.Sent.Clear();
            harness.Zone.Tick();
            harness.Server.PollOnce();
            Assert.Equal(2, harness.Server.Snapshot().ZoneChangedMessages);

            harness.Transport.Sent.Clear();
            Assert.True(harness.Zone.Entities.TryGet(1, out Entity? firstEntity));
            PlayerEntity first = Assert.IsType<PlayerEntity>(firstEntity);
            first.Position = new Vec2(config.SpawnX + 2, config.SpawnY);
            harness.Zone.Tick();
            harness.Server.PollOnce();

            SentMessage change = Assert.Single(harness.Transport.Sent.FindAll(sent =>
                MessageCodec.TryReadZoneChanged(sent.Data, 0, sent.Data.Length, out _)));
            Assert.Equal(1, change.ConnectionId);
            Assert.True(MessageCodec.TryReadZoneChanged(change.Data, 0, change.Data.Length, out ZoneChangedMessage changed));
            Assert.Equal((ushort)0, changed.ZoneId);
            Assert.Equal(3, harness.Server.Snapshot().ZoneChangedMessages);
        }

        [Fact]
        public void Network_Sin_Zonas_No_Envia_ZoneList()
        {
            using var harness = new Harness();
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            Assert.DoesNotContain(harness.Transport.Sent, sent =>
                MessageCodec.TryReadZoneList(sent.Data, 0, sent.Data.Length, out _));
        }

        [Fact]
        public void Network_Envia_Estado_Pvp_Rechazo_Y_Bit_Pvp_En_Snapshot()
        {
            var config = new ServerConfig();
            MapCollisionGrid map = MapFile.Load(MapPath);
            int spawnX = (int)Math.Floor(config.SpawnX);
            int spawnY = (int)Math.Floor(config.SpawnY);
            ZoneMap zones = ZoneMap.FromDefinitions(new[]
            {
                new ZoneDefinition
                {
                    Id = 1,
                    Name = "Segura de red",
                    Kind = "safe",
                    Rects = new List<ZoneRectDefinition>
                    {
                        new ZoneRectDefinition { X = spawnX, Y = spawnY, W = 1, H = 1 },
                    },
                },
                new ZoneDefinition
                {
                    Id = 2,
                    Name = "Arena de red",
                    Kind = "pvp",
                    Rects = new List<ZoneRectDefinition>
                    {
                        new ZoneRectDefinition { X = spawnX + 3, Y = spawnY, W = 1, H = 1 },
                    },
                },
            }, map);

            using var harness = new Harness(zones: zones);
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();
            harness.Transport.Sent.Clear();
            harness.Zone.Tick();
            harness.Server.PollOnce();
            Assert.Contains(harness.Transport.Sent, sent => sent.ConnectionId == 1 &&
                MessageCodec.TryReadPvpStatus(sent.Data, 0, sent.Data.Length, out _));

            PlayerEntity first = Assert.IsType<PlayerEntity>(harness.Zone.Entities.Get(1));
            first.Position = new Vec2(spawnX + 1.5f, spawnY + 0.5f);
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeSetPvpMode(
                new SetPvpModeMessage(PvpMode.Pvp)));
            harness.Transport.Sent.Clear();
            harness.Zone.Tick();
            harness.Server.PollOnce();
            SentMessage rejected = Assert.Single(harness.Transport.Sent.FindAll(sent => sent.ConnectionId == 1 &&
                MessageCodec.TryReadPvpModeRejected(sent.Data, 0, sent.Data.Length, out _)));
            Assert.True(MessageCodec.TryReadPvpModeRejected(
                rejected.Data, 0, rejected.Data.Length, out PvpModeRejectedMessage rejection));
            Assert.Equal(PvpModeRejectReason.NotInSafeZone, rejection.Reason);

            harness.Connect(2);
            harness.Handshake(2);
            harness.Pump();
            PlayerEntity second = Assert.IsType<PlayerEntity>(harness.Zone.Entities.Get(2));
            second.Position = new Vec2(spawnX + 3.5f, spawnY + 0.5f);
            harness.Transport.Sent.Clear();
            harness.Zone.Tick();
            harness.Server.AfterTick(harness.Zone);
            harness.Server.PollOnce();
            EntityState other = Assert.Single(SnapshotFor(harness, 1).Entities, entity => entity.Id == second.Id);
            Assert.True(other.Pvp);
            NetworkStats stats = harness.Server.Snapshot();
            Assert.True(stats.PvpStatusMessages > 0);
            Assert.True(stats.PvpStatusBytes >= 8);
            Assert.Equal(1, stats.PvpRejectedMessages);
        }

        [Fact]
        public void Catalogo_De_Items_Marca_Las_Armas_Permitidas_Segun_La_Clase_De_Cada_Jugador()
        {
            using var harness = new Harness(withItems: true);
            harness.Connect(1);
            harness.Handshake(1, classId: 1);
            harness.Connect(2);
            harness.Handshake(2, classId: 2);
            harness.Pump();

            bool FindPermission(int connectionId)
            {
                for (int i = 0; i < harness.Transport.Sent.Count; i++)
                {
                    SentMessage sent = harness.Transport.Sent[i];
                    if (sent.ConnectionId == connectionId &&
                        MessageCodec.TryReadItemCatalog(sent.Data, 0, sent.Data.Length, out ItemCatalogMessage catalog))
                    {
                        for (int j = 0; j < catalog.Items.Length; j++)
                        {
                            if (catalog.Items[j].Id == 3101) return catalog.Items[j].UsableByClass;
                        }
                    }
                }

                throw new InvalidOperationException("El catálogo no contenía el objeto esperado.");
            }

            Assert.True(FindPermission(1));
            Assert.False(FindPermission(2));
        }

        [Fact]
        public void Al_Seleccionar_Personaje_Se_Corrige_El_Equipo_Antiguo_Antes_De_Enviar_Inventario()
        {
            var equipment = new ushort[ProtocolConstants.EquipSlotCount];
            equipment[(int)EquipSlot.Weapon] = 3101;
            var character = new CharacterRecord(
                1, 1, "PersonajeGuardado", 1, 0, 100, 100, 1,
                new Vec2(-1.5f, -2.5f), new byte[ProtocolConstants.MapHashSize],
                DateTime.UtcNow, DateTime.UtcNow, classId: 2, equipment: equipment);
            var repository = new FixedCharacterRepository(character);
            using var saves = new CharacterSaveService(repository);
            using var harness = new Harness(
                withItems: true, repository: repository, saves: saves);
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            Assert.Equal(1, harness.Zone.EquipmentCorrectedOnLoad);
            Assert.Equal(0, harness.Zone.ItemsEquipped);
            InventoryMessage inventory = Assert.Single(harness.Transport.Sent
                .Where(sent => sent.ConnectionId == 1 &&
                    MessageCodec.TryReadInventory(sent.Data, 0, sent.Data.Length, out _))
                .Select(sent =>
                {
                    Assert.True(MessageCodec.TryReadInventory(sent.Data, 0, sent.Data.Length, out InventoryMessage decoded));
                    return decoded;
                }));
            Assert.Equal((ushort)3101, inventory.ItemIds[0]);
            Assert.Equal((ushort)0, inventory.Equipment[(int)EquipSlot.Weapon]);
        }

        [Theory]
        [InlineData("nivel")]
        [InlineData("dosmanos")]
        [InlineData("lleno")]
        public void Seleccionar_Personaje_Entrega_Equipo_Corregido_Y_Conserva_Objetos(string caso)
        {
            var classes = new ClassCatalog
            {
                BaseHealth = 50, PowerPerPoint = 0,
                Classes = new List<CharacterClass>
                {
                    new CharacterClass
                    {
                        Id = 1, Name = "Clase inventada", Primary = "str",
                        Base = new CharacterAttributes { Str = 1, Dex = 1, Int = 1, Vit = 1 },
                        HealthPerVit = 1,
                        Resource = new CharacterResource { Name = "Recurso", Base = 0, PerInt = 0 },
                        Unarmed = new UnarmedAttack { Damage = 1, DamageType = "physical", Range = 1, CooldownSeconds = 1 },
                        WeaponTypes = new List<string> { "TipoA" },
                    },
                },
            };
            var items = new ItemCatalog
            {
                Items = new List<ItemType>
                {
                    new ItemType { Id = 4101, Name = "Arma inventada", Kind = "weapon", MaxStack = 1,
                        WeaponType = caso == "lleno" ? "TipoB" : "TipoA",
                        RequiredLevel = caso == "nivel" ? 2 : 1,
                        Handedness = caso == "dosmanos" ? "two" : "one",
                        Damage = 10, Range = 1, CooldownSeconds = 1 },
                    new ItemType { Id = 4201, Name = "Escudo inventado", Kind = "armor", Slot = "offhand", MaxStack = 1 },
                    new ItemType { Id = 4301, Name = "Material inventado", Kind = "material", MaxStack = 99 },
                },
            };
            int slots = new ServerConfig().InventorySlots;
            var storedInventory = caso == "lleno"
                ? Enumerable.Range(0, slots).Select(i => new InventoryItemRecord(i, 4301, 1)).ToArray()
                : Array.Empty<InventoryItemRecord>();
            var equipment = new ushort[ProtocolConstants.EquipSlotCount];
            equipment[(int)EquipSlot.Weapon] = 4101;
            if (caso == "dosmanos") equipment[(int)EquipSlot.Offhand] = 4201;
            var character = new CharacterRecord(
                1, 1, "PersonajeInventado", 1, 0, 50, 0, 1,
                new Vec2(-1.5f, -2.5f), new byte[ProtocolConstants.MapHashSize],
                DateTime.UtcNow, DateTime.UtcNow, inventoryItems: storedInventory, classId: 1, equipment: equipment);
            var repository = new FixedCharacterRepository(character);
            using var saves = new CharacterSaveService(repository);
            using var harness = new Harness(repository: repository, saves: saves, customItems: items, classes: classes);
            var stderr = new StringWriter();
            TextWriter previousError = Console.Error;
            try
            {
                Console.SetError(stderr);
                harness.Connect(1);
                harness.Handshake(1);
                harness.Pump();
            }
            finally
            {
                Console.SetError(previousError);
            }

            InventoryMessage inventory = Assert.Single(harness.Transport.Sent
                .Where(sent => sent.ConnectionId == 1 &&
                    MessageCodec.TryReadInventory(sent.Data, 0, sent.Data.Length, out _))
                .Select(sent =>
                {
                    Assert.True(MessageCodec.TryReadInventory(sent.Data, 0, sent.Data.Length, out InventoryMessage decoded));
                    return decoded;
                }));
            Assert.Equal(0, harness.Zone.ItemsEquipped);
            Assert.Equal(0, harness.Zone.ItemsUnequipped);
            Assert.Equal(caso == "lleno" ? 0 : 1, harness.Zone.EquipmentCorrectedOnLoad);
            Assert.Equal(caso == "lleno" ? 1 : 0, harness.Zone.EquipmentLoadWarnings);
            Assert.Equal(caso == "nivel" ? (ushort)0 : (ushort)4101, inventory.Equipment[(int)EquipSlot.Weapon]);
            Assert.Equal((ushort)0, inventory.Equipment[(int)EquipSlot.Offhand]);
            if (caso == "lleno")
            {
                Assert.Equal(slots, inventory.ItemIds.Length);
                Assert.All(inventory.ItemIds, id => Assert.Equal((ushort)4301, id));
                Assert.All(inventory.Counts, count => Assert.Equal((ushort)1, count));
                Assert.Contains("AVISO", stderr.ToString());
                Assert.Contains(character.Name, stderr.ToString());
                Assert.Contains("4101", stderr.ToString());
                Assert.DoesNotContain("contrase?a", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("clave", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("avelora-dev", stderr.ToString());
            }
            else
            {
                Assert.Equal(caso == "nivel" ? (ushort)4101 : (ushort)4201, inventory.ItemIds[0]);
                Assert.Equal((ushort)1, inventory.Counts[0]);
            }
        }

        [Fact]
        public void Catalogo_Real_Mide_Trozos_Al_Entrar()
        {
            var items = ItemCatalog.Load(Path.Combine(AppContext.BaseDirectory, "items.json"));
            var classes = ClassCatalog.Load(Path.Combine(AppContext.BaseDirectory, "classes.json"));
            using var harness = new Harness(customItems: items, classes: classes);
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();
            var chunks = harness.Transport.Sent.Where(sent => sent.ConnectionId == 1 &&
                MessageCodec.TryReadItemCatalog(sent.Data, 0, sent.Data.Length, out _)).ToArray();

            Assert.Equal(23, items.Items.Count);
            SentMessage chunk = Assert.Single(chunks);
            Assert.Equal(1253, chunk.Data.Length);
            Assert.Equal(147, ProtocolConstants.MaxMessageSize - chunk.Data.Length);
        }


        private static SkillCatalog CargarHabilidades() => SkillCatalog.Load(
            Path.Combine(AppContext.BaseDirectory, "skills.json"), ClassCatalog.Default);

        [Fact]
        public void Habilidades_Entrada_Filtra_Clase_Y_Recurso_Solo_Al_Dueno()
        {
            using var harness = new Harness(classes: ClassCatalog.Default, skills: CargarHabilidades());
            harness.Connect(1);
            harness.Handshake(1, classId: 2);
            harness.Pump();
            SentMessage sent = Assert.Single(harness.Transport.Sent,
                s => s.Data[0] == (byte)MessageType.SkillCatalog);
            Assert.Equal(1, sent.ConnectionId);
            Assert.Equal(DeliveryMode.ReliableOrdered, sent.Mode);
            Assert.True(MessageCodec.TryReadSkillCatalog(sent.Data, 0, sent.Data.Length, out var catalog));
            Assert.Equal(Assert.IsType<CharacterResource>(ClassCatalog.Default.Find(2)!.Resource).Name, catalog.ResourceName);
            Assert.Equal(new ushort[] { 4002, 4005, 4008, 4009 }, catalog.Skills.Select(s => s.Id));
            harness.Transport.Sent.Clear();
            harness.Zone.Tick();
            harness.Server.PollOnce();
            SentMessage resource = Assert.Single(harness.Transport.Sent,
                s => s.Data[0] == (byte)MessageType.ResourceStatus);
            Assert.Equal(1, resource.ConnectionId);
            Assert.Equal(DeliveryMode.ReliableOrdered, resource.Mode);
            Assert.True(MessageCodec.TryReadResourceStatus(resource.Data, 0, resource.Data.Length, out var status));
            var player = Assert.IsType<PlayerEntity>(harness.Zone.Entities[harness.Zone.Entities.Count - 1]);
            Assert.Equal(player.Mana, status.Current);
            Assert.Equal(player.MaxMana, status.Max);
        }

        [Fact]
        public void Habilidad_Desconocida_En_Red_Devuelve_Rechazo_Al_Dueno()
        {
            using var harness = new Harness(classes: ClassCatalog.Default, skills: CargarHabilidades());
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();
            harness.Transport.Sent.Clear();
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeUseSkill(new UseSkillMessage(65000, 100)));
            harness.Server.PollOnce();
            Assert.Equal(0, harness.Zone.SkillsRejected(SkillRejectReason.UnknownSkill));
            harness.Zone.Tick();
            harness.Server.PollOnce();
            SentMessage sent = Assert.Single(harness.Transport.Sent,
                s => s.Data[0] == (byte)MessageType.SkillRejected);
            Assert.Equal(1, sent.ConnectionId);
            Assert.Equal(DeliveryMode.ReliableOrdered, sent.Mode);
            Assert.True(MessageCodec.TryReadSkillRejected(sent.Data, 0, sent.Data.Length, out var rejected));
            Assert.Equal((ushort)65000, rejected.SkillId);
            Assert.Equal(SkillRejectReason.UnknownSkill, rejected.Reason);
        }

        [Fact]
        public void Habilidad_En_Red_Dana_Pnj_Y_Envia_Aceptacion_Y_Recurso_Gastado()
        {
            using var harness = new Harness(classes: ClassCatalog.Default, skills: CargarHabilidades());
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();
            var player = Assert.IsType<PlayerEntity>(harness.Zone.Entities[harness.Zone.Entities.Count - 1]);
            var npc = new NpcEntity(100, 2001, 0f, player.Position, 0f,
                long.MaxValue, 1000, 200, spawnerIndex: -1) { Position = player.Position };
            harness.Zone.AddEntity(npc);
            harness.Zone.Tick();
            harness.Server.PollOnce();
            int mana = player.Mana;
            harness.Transport.Sent.Clear();
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeUseSkill(new UseSkillMessage(4001, npc.Id)));
            harness.Server.PollOnce();
            Assert.Equal(1000, npc.Health);
            harness.Zone.Tick();
            harness.Server.PollOnce();
            Assert.True(npc.Health < 1000);
            SentMessage used = Assert.Single(harness.Transport.Sent, s => s.Data[0] == (byte)MessageType.SkillUsed);
            Assert.Equal(1, used.ConnectionId);
            Assert.Equal(DeliveryMode.ReliableOrdered, used.Mode);
            Assert.True(MessageCodec.TryReadSkillUsed(used.Data, 0, used.Data.Length, out var accepted));
            Assert.Equal((ushort)4001, accepted.SkillId);
            SentMessage resource = Assert.Single(harness.Transport.Sent, s => s.Data[0] == (byte)MessageType.ResourceStatus);
            Assert.Equal(1, resource.ConnectionId);
            Assert.Equal(DeliveryMode.ReliableOrdered, resource.Mode);
            Assert.True(MessageCodec.TryReadResourceStatus(resource.Data, 0, resource.Data.Length, out var status));
            Assert.Equal(mana - CargarHabilidades().Find(4001)!.ResourceCost, status.Current);
        }

        [Fact]
        public void Cura_Propia_En_Red_Confirma_Objetivo_Y_Evento_Heal()
        {
            using var harness = new Harness(config => config.SnapshotEveryTicks=1, classes: ClassCatalog.Default, skills: CargarHabilidades());
            harness.Connect(1);harness.Handshake(1,classId:4);harness.Pump();
            var p=Assert.IsType<PlayerEntity>(harness.Zone.Entities[harness.Zone.Entities.Count-1]);
            p.ClassId=4;p.Mana=100;p.Health=20;
            harness.Transport.Sent.Clear();
            harness.Transport.RaiseReceived(1,MessageCodec.EncodeUseSkill(new UseSkillMessage(4006,p.Id)));
            harness.Server.PollOnce();harness.Zone.Tick();harness.Server.AfterTick(harness.Zone);harness.Server.PollOnce();
            var sent=Assert.Single(harness.Transport.Sent,s=>s.Data[0]==(byte)MessageType.SkillUsed);
            Assert.True(MessageCodec.TryReadSkillUsed(sent.Data,0,sent.Data.Length,out var used));
            Assert.Equal(4006,used.SkillId);Assert.Equal(p.Id,used.TargetId);
            Assert.Contains(harness.Transport.Sent,s=>MessageCodec.TryReadCombatEvent(s.Data,0,s.Data.Length,out var ev) &&
                ev.Entries.Any(e=>e.TargetId==p.Id && e.Flags==CombatEventFlags.Heal));
        }

        [Fact]
        public void Bonus_En_Red_Envia_Estado_Propio_Stats_Y_Mascara_A_Observadores()
        {
            using var harness = new Harness(config => config.SnapshotEveryTicks = 1,
                classes: ClassCatalog.Default, skills: CargarHabilidades());
            harness.Connect(1);
            harness.Handshake(1, classId: 4);
            harness.Pump();
            harness.Connect(2);
            harness.Handshake(2, classId: 1);
            harness.Pump();
            PlayerEntity? owner = null;
            PlayerEntity? observer = null;
            for (int i = 0; i < harness.Zone.Entities.Count; i++)
            {
                if (harness.Zone.Entities[i] is not PlayerEntity player) continue;
                if (player.ConnectionId == 1) owner = player;
                else if (player.ConnectionId == 2) observer = player;
            }
            Assert.NotNull(owner);
            Assert.NotNull(observer);
            PlayerEntity ownerPlayer = owner!;
            PlayerEntity observerPlayer = observer!;
            observerPlayer.Position = ownerPlayer.Position;
            harness.Transport.Sent.Clear();
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeUseSkill(new UseSkillMessage(4010, ownerPlayer.Id)));
            harness.Server.PollOnce();
            harness.Zone.Tick();
            harness.Server.AfterTick(harness.Zone);
            harness.Server.PollOnce();

            SentMessage[] ownStatuses = harness.Transport.Sent.Where(sent => sent.ConnectionId == 1 &&
                MessageCodec.TryReadStatusEffects(sent.Data, 0, sent.Data.Length, out _)).ToArray();
            Assert.Equal(2, ownStatuses.Length);
            SentMessage own = ownStatuses[ownStatuses.Length - 1];
            Assert.All(ownStatuses, status => Assert.Equal(DeliveryMode.ReliableOrdered, status.Mode));
            Assert.True(MessageCodec.TryReadStatusEffects(own.Data, 0, own.Data.Length, out StatusEffectsMessage ownStatus));
            Assert.Equal((byte)100, ownStatus.SpeedPercent);
            Assert.Equal(StatusKind.Bonus, Assert.Single(ownStatus.Effects).Kind);
            Assert.Equal((ushort)4, ownStatus.Effects[0].Amount);

            SentMessage observed = Assert.Single(harness.Transport.Sent, sent => sent.ConnectionId == 2 &&
                MessageCodec.TryReadStatusEvent(sent.Data, 0, sent.Data.Length, out _));
            Assert.Equal(DeliveryMode.ReliableOrdered, observed.Mode);
            Assert.True(MessageCodec.TryReadStatusEvent(observed.Data, 0, observed.Data.Length, out StatusEventMessage statusEvent));
            Assert.Contains(statusEvent.Entries, entry => entry.EntityId == ownerPlayer.Id && entry.Mask == 1);

            SentMessage statsMessage = Assert.Single(harness.Transport.Sent, sent => sent.ConnectionId == 1 &&
                MessageCodec.TryReadStats(sent.Data, 0, sent.Data.Length, out _));
            Assert.True(MessageCodec.TryReadStats(statsMessage.Data, 0, statsMessage.Data.Length, out StatsMessage stats));
            int baseDamage = CharacterStats.GetAttackDamage(harness.Zone.Classes, ownerPlayer.ClassId, ownerPlayer.Level,
                ownerPlayer.AllocStr, ownerPlayer.AllocDex, ownerPlayer.AllocInt, ownerPlayer.AllocVit, null, out _, harness.Zone.Items,
                ownerPlayer.Equipment, null);
            Assert.Equal(checked((ushort)(baseDamage + 4)), stats.AttackDamage);
            NetworkStats network = harness.Server.Snapshot();
            Assert.Equal(4, network.StatusMessages);
            Assert.True(network.StatusBytes >= ownStatuses[0].Data.Length + ownStatuses[1].Data.Length);
            Assert.True(network.StatusEventMessages >= 1);
            Assert.True(network.StatusEventBytes >= observed.Data.Length);
        }

        [Fact]
        public void Sin_Catalogo_No_Se_Envian_Habilidades_Ni_Recurso_En_Red()
        {
            using var harness = new Harness(classes: ClassCatalog.Default);
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();
            for (int i = 0; i < 50; i++) harness.Zone.Tick();
            harness.Server.PollOnce();
            Assert.DoesNotContain(harness.Transport.Sent, s =>
                s.Data[0] == (byte)MessageType.SkillCatalog || s.Data[0] == (byte)MessageType.ResourceStatus);
        }

        [Fact]
        public void Recurso_Gastado_Se_Persiste_Y_La_Recarga_No_Sobrevive_A_Reconexion()
        {
            using var repository = new InMemoryPlayerRepository(ClassCatalog.Default, 1);
            repository.Initialize();
            using var saves = new CharacterSaveService(repository);
            using var harness = new Harness(repository: repository, saves: saves,
                classes: ClassCatalog.Default, skills: CargarHabilidades());
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();
            var first = Assert.IsType<PlayerEntity>(harness.Zone.Entities[harness.Zone.Entities.Count - 1]);
            var npc = new NpcEntity(100, 2001, 0f, first.Position, 0f,
                long.MaxValue, 1000, 200, spawnerIndex: -1) { Position = first.Position };
            harness.Zone.AddEntity(npc);
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeUseSkill(new UseSkillMessage(4001, npc.Id)));
            harness.Server.PollOnce();
            harness.Zone.Tick();
            Assert.Equal(1, harness.Zone.SkillsUsed);
            int mana = first.Mana;
            long ready = first.SkillReadyTicks[4001];
            harness.Transport.RaiseDisconnected(1);
            harness.Server.PollOnce();
            harness.Server.BeforeTick(harness.Zone);
            harness.Zone.Tick();
            harness.Server.PollOnce();
            Assert.True(saves.Flush(TimeSpan.FromSeconds(2)));
            Assert.Equal(mana, Assert.Single(repository.ListCharacters(1)).Mana);
            harness.Connect(2);
            harness.Handshake(2);
            harness.Pump();
            var reconnected = Assert.IsType<PlayerEntity>(harness.Zone.Entities[harness.Zone.Entities.Count - 1]);
            Assert.Equal(mana, reconnected.Mana);
            Assert.Empty(reconnected.SkillReadyTicks);
            harness.Transport.RaiseReceived(2, MessageCodec.EncodeUseSkill(new UseSkillMessage(4001, npc.Id)));
            harness.Server.PollOnce();
            harness.Zone.Tick();
            Assert.Equal(2, harness.Zone.SkillsUsed);
            Assert.True(reconnected.SkillReadyTicks[4001] < ready + 120);
            Assert.Equal(0, harness.Zone.SkillsRejected(SkillRejectReason.Cooldown));
        }

        private sealed class Harness : IDisposable
        {
            private readonly Dictionary<int, byte[]> _challengeNonces = new Dictionary<int, byte[]>();
            private readonly Dictionary<int, string> _selectionNames = new Dictionary<int, string>();
            private readonly Dictionary<int, byte> _selectionClasses = new Dictionary<int, byte>();
            private readonly HashSet<int> _automaticCharactersCreated = new HashSet<int>();
            private readonly HashSet<int> _automaticSelectionsSent = new HashSet<int>();
            private readonly HashSet<int> _pendingAutomaticHandshakes = new HashSet<int>();
            private readonly bool _persistent;

            public Harness(
                Action<ServerConfig>? configure = null,
                Func<long>? clock = null,
                bool withItems = false,
                NpcCatalog? npcs = null,
                IPlayerRepository? repository = null,
                CharacterSaveService? saves = null, ItemCatalog? customItems = null, ClassCatalog? classes = null,
                ZoneMap? zones = null, SkillCatalog? skills = null)
            {
                _persistent = repository != null;
                Config = new ServerConfig { HandshakeTimeoutSeconds = 60d };
                configure?.Invoke(Config);
                Zone = NewZone(Config, withItems, npcs, customItems, classes, zones, skills);
                MapHash = MapFile.ComputeContentHashBytes(Zone.Map);
                Transport = new FakeServerTransport();
                Server = new NetworkServer(
                    Zone,
                    Config,
                    Transport,
                    MapHash,
                    repository,
                    saves,
                    repository == null ? null : "avelora-dev",
                    clock: clock);
            }

            public ServerConfig Config { get; }

            public Zone Zone { get; }

            public byte[] MapHash { get; }

            public FakeServerTransport Transport { get; }

            public NetworkServer Server { get; }

            /// <summary>
            /// Simula la conexión y guarda el nonce del <c>Challenge</c> que manda el servidor. El
            /// desafío se retira de <see cref="FakeServerTransport.Sent"/> para que las pruebas
            /// sigan contando solo lo que ocurre después del saludo.
            /// </summary>
            public void Connect(int connectionId)
            {
                Transport.RaiseConnected(connectionId);

                for (int i = Transport.Sent.Count - 1; i >= 0; i--)
                {
                    SentMessage sent = Transport.Sent[i];

                    if (sent.ConnectionId == connectionId &&
                        MessageCodec.TryReadChallenge(sent.Data, 0, sent.Data.Length, out ChallengeMessage challenge))
                    {
                        _challengeNonces[connectionId] = challenge.Nonce;
                        Transport.Sent.RemoveAt(i);
                        return;
                    }
                }

                throw new InvalidOperationException(
                    "La conexión " + connectionId + " no recibió el desafío del servidor.");
            }

            public void Handshake(
                int connectionId,
                ushort? version = null,
                byte[]? mapHash = null,
                string characterName = "dev",
                byte classId = 1,
                bool automaticallySelect = true,
                string? accountNameOverride = null)
            {
                string accountName = accountNameOverride ?? (_persistent
                    ? "dev"
                    : "dev" + connectionId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                byte[] encrypted = HelloSecrets.Encrypt(
                    Server.PublicKey,
                    _challengeNonces[connectionId],
                    "avelora-dev",
                    "avelora-dev");

                var hello = new HelloMessage(
                    version ?? ProtocolConstants.Version,
                    (ushort)SimulationConstants.TickRate,
                    mapHash ?? MapHash,
                    4242u + (uint)connectionId,
                    accountName,
                    encrypted);

                Transport.RaiseReceived(connectionId, MessageCodec.EncodeHello(hello));
                if (!automaticallySelect)
                {
                    return;
                }

                _selectionNames[connectionId] = _persistent || characterName != "dev"
                    ? characterName
                    : "dev" + connectionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _selectionClasses[connectionId] = classId;
                _pendingAutomaticHandshakes.Add(connectionId);
            }

            /// <summary>Alterna red y simulación hasta completar los saludos pendientes.</summary>
            public void Pump()
            {
                long deadline = Environment.TickCount64 + 5_000;
                while (_pendingAutomaticHandshakes.Count > 0)
                {
                    Server.BeforeTick(Zone);
                    Server.PollOnce();
                    ProcessCharacterLists();

                    var completed = new List<int>();
                    foreach (int connectionId in _pendingAutomaticHandshakes)
                    {
                        if (HasOutcome(connectionId))
                        {
                            completed.Add(connectionId);
                        }
                    }

                    for (int i = 0; i < completed.Count; i++)
                    {
                        if (TryCharacterReject(completed[i], out CharacterRejectedMessage characterReject))
                        {
                            throw new InvalidOperationException(
                                "El flujo automático rechazó el personaje de " + completed[i] + ": " + characterReject.Reason);
                        }

                        _pendingAutomaticHandshakes.Remove(completed[i]);
                        RemoveCharacterMessages(completed[i]);
                    }

                    if (_pendingAutomaticHandshakes.Count == 0)
                    {
                        return;
                    }

                    if (Environment.TickCount64 >= deadline)
                    {
                        throw new TimeoutException("No se completó el flujo automático de selección de personaje.");
                    }

                    Thread.Sleep(1);
                }

                Server.BeforeTick(Zone);
                Server.PollOnce();
            }

            public CharacterListMessage WaitForCharacterList(int connectionId)
            {
                long deadline = Environment.TickCount64 + 5_000;
                while (Environment.TickCount64 < deadline)
                {
                    Server.PollOnce();
                    for (int i = Transport.Sent.Count - 1; i >= 0; i--)
                    {
                        SentMessage sent = Transport.Sent[i];
                        if (sent.ConnectionId == connectionId &&
                            MessageCodec.TryReadCharacterList(sent.Data, 0, sent.Data.Length, out CharacterListMessage list))
                        {
                            return list;
                        }
                    }

                    Thread.Sleep(1);
                }

                throw new TimeoutException("No se recibió CharacterList para la conexión " + connectionId + ".");
            }

            private void ProcessCharacterLists()
            {
                for (int i = 0; i < Transport.Sent.Count; i++)
                {
                    SentMessage sent = Transport.Sent[i];
                    if (!_pendingAutomaticHandshakes.Contains(sent.ConnectionId) ||
                        !MessageCodec.TryReadCharacterList(sent.Data, 0, sent.Data.Length, out CharacterListMessage list))
                    {
                        continue;
                    }

                    if (list.Characters.Length > 0)
                    {
                        if (_automaticSelectionsSent.Add(sent.ConnectionId))
                        {
                            byte[] select = MessageCodec.EncodeSelectCharacter(
                                new SelectCharacterMessage(list.Characters[0].CharacterId));
                            Transport.RaiseReceived(sent.ConnectionId, select);
                        }
                    }
                    else if (_automaticCharactersCreated.Add(sent.ConnectionId))
                    {
                        byte[] create = MessageCodec.EncodeCreateCharacter(new CreateCharacterMessage(
                            _selectionNames[sent.ConnectionId],
                            classId: _selectionClasses[sent.ConnectionId]));
                        Transport.RaiseReceived(sent.ConnectionId, create);
                    }
                }
            }

            private bool HasOutcome(int connectionId)
            {
                for (int i = 0; i < Transport.Sent.Count; i++)
                {
                    SentMessage sent = Transport.Sent[i];
                    if (sent.ConnectionId == connectionId &&
                        (MessageCodec.TryReadWelcome(sent.Data, 0, sent.Data.Length, out _) ||
                         MessageCodec.TryReadReject(sent.Data, 0, sent.Data.Length, out _) ||
                         MessageCodec.TryReadCharacterRejected(sent.Data, 0, sent.Data.Length, out _)))
                    {
                        return true;
                    }
                }

                return false;
            }

            private bool TryCharacterReject(int connectionId, out CharacterRejectedMessage reject)
            {
                for (int i = Transport.Sent.Count - 1; i >= 0; i--)
                {
                    SentMessage sent = Transport.Sent[i];
                    if (sent.ConnectionId == connectionId &&
                        MessageCodec.TryReadCharacterRejected(sent.Data, 0, sent.Data.Length, out reject))
                    {
                        return true;
                    }
                }

                reject = default;
                return false;
            }

            private void RemoveCharacterMessages(int connectionId)
            {
                for (int i = Transport.Sent.Count - 1; i >= 0; i--)
                {
                    SentMessage sent = Transport.Sent[i];
                    if (sent.ConnectionId == connectionId &&
                        (MessageCodec.TryReadCharacterList(sent.Data, 0, sent.Data.Length, out _) ||
                         MessageCodec.TryReadCharacterRejected(sent.Data, 0, sent.Data.Length, out _)))
                    {
                        Transport.Sent.RemoveAt(i);
                    }
                }
            }

            public void Dispose() => Server.Dispose();
        }

        [Fact]
        public void Al_Saludar_Se_Crea_El_Jugador_Y_Llega_El_Welcome()
        {
            using var harness = new Harness();

            // Valores que no coinciden con nada de server.json: el catálogo tiene que sacar el
            // ataque sin arma de la clase del jugador, que es lo que valida el combate.
            harness.Zone.Classes.Find(1)!.Unarmed!.Range = 1.7f;
            harness.Zone.Classes.Find(1)!.Unarmed!.CooldownSeconds = 1.3d;

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            Assert.Equal(1, harness.Zone.Entities.Count);
            Assert.Equal(6, harness.Transport.Sent.Count);

            SentMessage sent = harness.Transport.Sent[0];
            Assert.Equal(1, sent.ConnectionId);
            Assert.True(MessageCodec.TryReadWelcome(sent.Data, 0, sent.Data.Length, out WelcomeMessage welcome));

            Assert.Equal(ProtocolConstants.Version, welcome.ProtocolVersion);
            Assert.Equal((ushort)SimulationConstants.TickRate, welcome.TickRate);
            Assert.Equal(1, welcome.EntityId);
            Assert.Equal(4243u, welcome.ClientToken);
            Assert.Equal(-1.5f, welcome.SpawnX);
            Assert.Equal(-2.5f, welcome.SpawnY);
            Assert.Equal(harness.Config.PlayerSpeed, welcome.Speed);
            Assert.Equal(harness.Config.PlayerHalfWidth, welcome.HalfWidth);
            Assert.Equal(harness.Config.PlayerHalfHeight, welcome.HalfHeight);
            Assert.Equal(harness.MapHash, welcome.MapHash);
            Assert.True(MessageCodec.TryReadItemCatalog(
                harness.Transport.Sent[1].Data,
                0,
                harness.Transport.Sent[1].Data.Length,
                out ItemCatalogMessage catalog));
            Assert.Equal(harness.Config.PickupRange, catalog.PickupRange);
            Assert.Equal(6u, catalog.FistDamage); // unarmed: ⌊6 × (100 + 4 × 2) / 100⌋
            Assert.Equal(1.7f, catalog.FistRange);
            Assert.Equal(1.3f, catalog.FistCooldownSeconds);
            Assert.Empty(catalog.Items);
            Assert.True(MessageCodec.TryReadInventory(
                harness.Transport.Sent[2].Data,
                0,
                harness.Transport.Sent[2].Data.Length,
                out InventoryMessage inventory));
            Assert.Equal((byte)20, inventory.SlotCount);
            Assert.All(inventory.ItemIds, id => Assert.Equal((ushort)0, id));
            Assert.True(MessageCodec.TryReadProgress(
                harness.Transport.Sent[3].Data,
                0,
                harness.Transport.Sent[3].Data.Length,
                out ProgressMessage progress));
            Assert.Equal((byte)1, progress.Level);
            Assert.Equal(0u, progress.Experience);
            Assert.Equal(100u, progress.ExperienceToNext);
            Assert.Equal(120u, progress.MaxHealth);
            Assert.True(MessageCodec.TryReadStats(
                harness.Transport.Sent[4].Data,
                0,
                harness.Transport.Sent[4].Data.Length,
                out StatsMessage stats));
            Assert.Equal((byte)1, stats.ClassId);
            Assert.Equal((byte)1, stats.Level);
            Assert.Equal((ushort)3, stats.FreePoints);
            Assert.Equal((uint)120, stats.MaxHealth);
            Assert.Equal((ushort)6, stats.AttackDamage);
            Assert.Equal((byte)0, stats.DamageType);
            Assert.Equal((byte)1, stats.FreeReset);
            Assert.Empty(harness.Transport.DisconnectedConnections);
        }

        [Fact]
        public void Seleccion_Exige_Personaje_Rechaza_Acciones_De_Juego_Y_Expone_ClassId()
        {
            using var harness = new Harness();
            harness.Connect(1);
            harness.Handshake(1, automaticallySelect: false);

            CharacterListMessage initial = harness.WaitForCharacterList(1);
            Assert.Empty(initial.Characters);
            Assert.Equal(3, initial.MaxCharacters);
            Assert.Equal(4, initial.Classes.Length);

            harness.Transport.RaiseReceived(1, MessageCodec.EncodeMoveInput(new MoveInputMessage(1, 1, 0)));
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeSelectCharacter(new SelectCharacterMessage(99)));
            harness.Server.PollOnce();

            Assert.Empty(harness.Zone.Entities.All);
            Assert.Equal(2, harness.Server.Snapshot().MessagesRejected);
            Assert.Contains(harness.Transport.Sent, sent =>
                sent.ConnectionId == 1 &&
                MessageCodec.TryReadCharacterRejected(sent.Data, 0, sent.Data.Length, out CharacterRejectedMessage rejected) &&
                rejected.Reason == RejectReason.CharacterNotFound);

            harness.Transport.RaiseReceived(1, MessageCodec.EncodeCreateCharacter(
                new CreateCharacterMessage("Mago", 255)));
            harness.Server.PollOnce();
            Assert.Contains(harness.Transport.Sent, sent =>
                sent.ConnectionId == 1 &&
                MessageCodec.TryReadCharacterRejected(sent.Data, 0, sent.Data.Length, out CharacterRejectedMessage rejected) &&
                rejected.Reason == RejectReason.ClassUnknown);

            harness.Transport.RaiseReceived(1, MessageCodec.EncodeCreateCharacter(
                new CreateCharacterMessage("Mago", 2)));
            harness.Server.PollOnce();
            CharacterListMessage created = harness.WaitForCharacterList(1);
            CharacterListEntry character = Assert.Single(created.Characters);
            Assert.Equal("Mago", character.Name);
            Assert.Equal((byte)2, character.ClassId);

            harness.Transport.RaiseReceived(1, MessageCodec.EncodeDeleteCharacter(
                new DeleteCharacterMessage(character.CharacterId, "mago")));
            harness.Server.PollOnce();
            Assert.Contains(harness.Transport.Sent, sent =>
                sent.ConnectionId == 1 &&
                MessageCodec.TryReadCharacterRejected(sent.Data, 0, sent.Data.Length, out CharacterRejectedMessage rejected) &&
                rejected.Reason == RejectReason.ConfirmMismatch);

            harness.Transport.RaiseReceived(1, MessageCodec.EncodeSelectCharacter(
                new SelectCharacterMessage(character.CharacterId)));
            harness.Server.PollOnce();
            harness.Server.BeforeTick(harness.Zone);
            harness.Server.PollOnce();

            PlayerEntity player = Assert.IsType<PlayerEntity>(Assert.Single(harness.Zone.Entities.All));
            Assert.Equal(2, player.ClassId);
            Assert.Contains(harness.Transport.Sent, sent =>
                sent.ConnectionId == 1 &&
                MessageCodec.TryReadWelcome(sent.Data, 0, sent.Data.Length, out _));

            harness.Zone.Tick();
            harness.Server.AfterTick(harness.Zone);
            harness.Server.PollOnce();
            SnapshotMessage snapshot = SnapshotFor(harness, 1);
            EntityState playerState = Assert.Single(snapshot.Entities, entity => entity.Id == player.Id);
            Assert.Equal((ushort)2, playerState.TypeId);
            Assert.Empty(harness.Transport.DisconnectedConnections);
        }

        [Fact]
        public void La_Seleccion_De_Personaje_Tiene_Un_Tope_De_Tiempo()
        {
            long now = 1000;
            using var harness = new Harness(
                config => config.CharacterSelectTimeoutSeconds = 1d,
                clock: () => now);
            harness.Connect(1);
            harness.Handshake(1, automaticallySelect: false);
            Assert.Empty(harness.WaitForCharacterList(1).Characters);

            now += 1001;
            harness.Server.PollOnce();

            Assert.Contains(harness.Transport.Sent, sent =>
                sent.ConnectionId == 1 &&
                MessageCodec.TryReadReject(sent.Data, 0, sent.Data.Length, out RejectMessage reject) &&
                reject.Reason == RejectReason.Timeout);
            Assert.Empty(harness.Zone.Entities.All);
        }

        [Fact]
        public void Seleccion_Que_Termina_Despues_Del_Plazo_No_Entra_Al_Mundo()
        {
            long now = 1000;
            var repository = new BlockingSelectRepository();
            try
            {
                using var saves = new CharacterSaveService(repository);
                using var harness = new Harness(
                    config => config.CharacterSelectTimeoutSeconds = 1d,
                    clock: () => now,
                    repository: repository,
                    saves: saves);

                harness.Connect(1);
                harness.Handshake(1, automaticallySelect: false);
                CharacterListMessage list = harness.WaitForCharacterList(1);
                CharacterListEntry character = Assert.Single(list.Characters);

                harness.Transport.RaiseReceived(1, MessageCodec.EncodeSelectCharacter(
                    new SelectCharacterMessage(character.CharacterId)));
                harness.Server.PollOnce();
                Assert.True(repository.SelectionStarted.Wait(5_000), "La selección no llegó al repositorio.");

                now += 1001;
                repository.SelectionGate.Set();
                Assert.True(repository.SelectionFinished.Wait(5_000), "La selección no terminó.");

                harness.Server.PollOnce();
                harness.Server.BeforeTick(harness.Zone);
                harness.Server.PollOnce();

                Assert.Empty(harness.Zone.Entities.All);
                Assert.Contains(harness.Transport.Sent, sent =>
                    sent.ConnectionId == 1 &&
                    MessageCodec.TryReadReject(sent.Data, 0, sent.Data.Length, out RejectMessage reject) &&
                    reject.Reason == RejectReason.Timeout);
                Assert.DoesNotContain(harness.Transport.Sent, sent =>
                    sent.ConnectionId == 1 &&
                    MessageCodec.TryReadWelcome(sent.Data, 0, sent.Data.Length, out _));
            }
            finally
            {
                repository.SelectionGate.Set();
                repository.Dispose();
            }
        }

        [Fact]
        public void Seleccion_En_Cola_Se_Procesa_Aunque_El_Tope_De_Logins_Este_Lleno()
        {
            var repository = new BlockingLoginRepository();
            try
            {
                using var saves = new CharacterSaveService(repository);
                using var harness = new Harness(
                    config => config.MaxPendingLogins = 1,
                    repository: repository,
                    saves: saves);

                harness.Connect(1);
                harness.Handshake(1, automaticallySelect: false, accountNameOverride: "jugador");
                CharacterListMessage list = harness.WaitForCharacterList(1);
                CharacterListEntry character = Assert.Single(list.Characters);

                harness.Connect(2);
                harness.Handshake(2, automaticallySelect: false, accountNameOverride: "bloqueo");
                harness.Server.PollOnce();
                Assert.True(repository.BlockingLoginStarted.Wait(5_000), "El segundo login no bloqueó el hilo.");

                harness.Transport.RaiseReceived(1, MessageCodec.EncodeSelectCharacter(
                    new SelectCharacterMessage(character.CharacterId)));
                harness.Server.PollOnce();

                repository.BlockingLoginGate.Set();
                Assert.True(repository.BlockingLoginFinished.Wait(5_000), "El segundo login no terminó.");

                bool HasWelcome()
                {
                    for (int i = 0; i < harness.Transport.Sent.Count; i++)
                    {
                        SentMessage sent = harness.Transport.Sent[i];
                        if (sent.ConnectionId == 1 &&
                            MessageCodec.TryReadWelcome(sent.Data, 0, sent.Data.Length, out _))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                long deadline = Environment.TickCount64 + 5_000;
                while (Environment.TickCount64 < deadline && !HasWelcome())
                {
                    harness.Server.PollOnce();
                    harness.Server.BeforeTick(harness.Zone);
                    harness.Server.PollOnce();
                    Thread.Sleep(1);
                }

                Assert.True(HasWelcome(), "La selección encolada no recibió Welcome.");
            }
            finally
            {
                repository.BlockingLoginGate.Set();
                repository.Dispose();
            }
        }

        [Fact]
        public void Una_Cuenta_Nueva_Echa_A_La_Conexion_Que_Sigue_En_Seleccion()
        {
            using var harness = new Harness();
            harness.Connect(1);
            harness.Handshake(1, automaticallySelect: false, accountNameOverride: "compartida");
            Assert.Empty(harness.WaitForCharacterList(1).Characters);

            harness.Connect(2);
            harness.Handshake(2, automaticallySelect: false, accountNameOverride: "compartida");
            Assert.Empty(harness.WaitForCharacterList(2).Characters);

            Assert.Contains(harness.Transport.Sent, sent =>
                sent.ConnectionId == 1 &&
                MessageCodec.TryReadReject(sent.Data, 0, sent.Data.Length, out RejectMessage reject) &&
                reject.Reason == RejectReason.LoggedInElsewhere);

            harness.Transport.RaiseReceived(2, MessageCodec.EncodeCreateCharacter(
                new CreateCharacterMessage("Duo", 1)));
            harness.Server.PollOnce();
            CharacterListEntry character = Assert.Single(harness.WaitForCharacterList(2).Characters);
            harness.Transport.RaiseReceived(2, MessageCodec.EncodeSelectCharacter(
                new SelectCharacterMessage(character.CharacterId)));
            harness.Server.PollOnce();
            harness.Server.BeforeTick(harness.Zone);
            harness.Server.PollOnce();

            Assert.Single(harness.Zone.Entities.All);
            Assert.Contains(harness.Transport.Sent, sent =>
                sent.ConnectionId == 2 &&
                MessageCodec.TryReadWelcome(sent.Data, 0, sent.Data.Length, out _));
        }

        [Fact]
        public void Al_Saludar_El_Catalogo_Grande_Llega_Troceado_Entre_Welcome_E_Inventory()
        {
            using var harness = new Harness(withItems: true);
            for (int i = 0; i < 80; i++)
            {
                harness.Zone.Items!.Items.Add(new ItemType
                {
                    Id = 4000 + i,
                    Name = new string('x', 48),
                    Kind = "material",
                    MaxStack = 99,
                });
            }

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            Assert.True(MessageCodec.TryReadWelcome(
                harness.Transport.Sent[0].Data, 0, harness.Transport.Sent[0].Data.Length, out _));
            int messageIndex = 1;
            int chunkCount = 0;
            int itemCount = 0;
            while (harness.Transport.Sent[messageIndex].Data[0] == (byte)MessageType.ItemCatalog)
            {
                byte[] data = harness.Transport.Sent[messageIndex].Data;
                Assert.True(data.Length <= ProtocolConstants.MaxMessageSize);
                Assert.True(MessageCodec.TryReadItemCatalog(data, 0, data.Length, out ItemCatalogMessage chunk));
                itemCount += chunk.Items.Length;
                chunkCount++;
                messageIndex++;
            }

            Assert.True(chunkCount > 1);
            Assert.Equal(85, itemCount);
            Assert.True(MessageCodec.TryReadInventory(
                harness.Transport.Sent[messageIndex].Data, 0,
                harness.Transport.Sent[messageIndex].Data.Length, out _));
            Assert.True(MessageCodec.TryReadProgress(
                harness.Transport.Sent[messageIndex + 1].Data, 0,
                harness.Transport.Sent[messageIndex + 1].Data.Length, out _));
            Assert.True(MessageCodec.TryReadStats(
                harness.Transport.Sent[messageIndex + 2].Data, 0,
                harness.Transport.Sent[messageIndex + 2].Data.Length, out _));
            Assert.True(MessageCodec.TryReadStatusEffects(
                harness.Transport.Sent[messageIndex + 3].Data, 0,
                harness.Transport.Sent[messageIndex + 3].Data.Length, out _));
            Assert.Equal(messageIndex + 4, harness.Transport.Sent.Count);
        }

        [Fact]
        public void Dueño_Recibe_Inventory_Y_Luego_LootGained_Fiable_Y_El_Otro_Jugador_No()
        {
            var npcs = new NpcCatalog
            {
                Types = new List<NpcType>
                {
                    new NpcType
                    {
                        Id = 2001, Name = "PNJ de red", Speed = 0, HalfWidth = 0.35f,
                        HalfHeight = 0.35f, MaxHealth = 30, Experience = 20, Level = 1,
                        RespawnSeconds = 10, AggroRadius = 0, LeashRadius = 8,
                        AttackDamage = 1, AttackRange = 1, AttackCooldownSeconds = 1,
                        GoldMin = 2, GoldMax = 2,
                        Loot = new List<LootEntry> { new LootEntry { ItemId = 3001, Chance = 1, Min = 1, Max = 1 } },
                    },
                },
                Spawners = new List<NpcSpawner>
                {
                    new NpcSpawner { TypeId = 2001, X = -1.5f, Y = -2.5f, Count = 1, WanderRadius = 1 },
                },
            };
            using var harness = new Harness(withItems: true, npcs: npcs);
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();
            harness.Connect(2);
            harness.Handshake(2);
            harness.Pump();
            harness.Transport.Sent.Clear();

            NpcEntity npc = Assert.IsType<NpcEntity>(harness.Zone.Entities.All.Single(entity => entity is NpcEntity));
            npc.Health = 1;
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeAttack(new AttackMessage(npc.Id)));
            harness.Server.PollOnce();
            harness.Zone.Tick();
            harness.Server.PollOnce();

            int inventoryIndex = harness.Transport.Sent.FindIndex(sent => sent.ConnectionId == 1 &&
                MessageCodec.TryReadInventory(sent.Data, 0, sent.Data.Length, out InventoryMessage inventory) &&
                inventory.Gold == 2 && inventory.ItemIds.Contains((ushort)3001));
            int lootIndex = harness.Transport.Sent.FindIndex(sent => sent.ConnectionId == 1 &&
                MessageCodec.TryReadLootGained(sent.Data, 0, sent.Data.Length, out LootGainedMessage loot) &&
                loot.Gold == 2 && loot.Experience > 0 && loot.Items.Length == 1);

            Assert.True(inventoryIndex >= 0, $"loot={lootIndex}, ataques={harness.Zone.AttacksOk}, rechObj={harness.Zone.RejectedAttackTarget}, rechDist={harness.Zone.RejectedAttackDistance}, hp={npc.Health}, owner={npc.LootOwnerId}, sinDueño={harness.Zone.NpcDeathsWithoutOwner}, mensajes={string.Join(',', harness.Transport.Sent.Select(s => s.Data[0]))}");
            Assert.True(lootIndex > inventoryIndex);
            Assert.Equal(DeliveryMode.ReliableOrdered, harness.Transport.Sent[lootIndex].Mode);
            Assert.DoesNotContain(harness.Transport.Sent, sent => sent.ConnectionId == 2 &&
                MessageCodec.TryReadLootGained(sent.Data, 0, sent.Data.Length, out _));
            NetworkStats stats = harness.Server.Snapshot();
            Assert.Equal(1, stats.LootGainedMessages);
            Assert.Equal(harness.Transport.Sent[lootIndex].Data.Length, stats.LootGainedBytes);
        }

        [Fact]
        public void Repartir_Puntos_Se_Resuelve_En_El_Tick_Y_Devuelve_Stats()
        {
            using var harness = new Harness();
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();
            harness.Transport.Sent.Clear();

            harness.Transport.RaiseReceived(1, MessageCodec.EncodeAllocatePoints(
                new AllocatePointsMessage(0, 0, 0, 2)));
            harness.Server.PollOnce();
            Assert.Equal(0, harness.Zone.StatsAllocations);
            harness.Zone.Tick();
            harness.Server.PollOnce();

            SentMessage sent = Assert.Single(harness.Transport.Sent, message =>
                MessageCodec.TryReadStats(message.Data, 0, message.Data.Length, out _));
            Assert.Equal(2, harness.Transport.Sent.Count);
            Assert.True(MessageCodec.TryReadStats(sent.Data, 0, sent.Data.Length, out StatsMessage stats));
            Assert.Contains(harness.Transport.Sent, message =>
                MessageCodec.TryReadStatusEffects(message.Data, 0, message.Data.Length, out _));
            Assert.Equal((ushort)2, stats.AllocVit);
            Assert.Equal((ushort)1, stats.FreePoints);
            Assert.Equal((uint)132, stats.MaxHealth);
            Assert.Equal(1, harness.Zone.StatsAllocations);
            Assert.Equal(2, harness.Zone.StatsPointsAllocated);
        }

        [Fact]
        public void Tras_Una_Muerte_Que_Da_Experiencia_Se_Envia_Progress_Nuevo()
        {
            var npcs = new NpcCatalog
            {
                Types = new List<NpcType>
                {
                    new NpcType
                    {
                        Id = 2001,
                        Name = "Orco",
                        Speed = 2f,
                        HalfWidth = 0.35f,
                        HalfHeight = 0.35f,
                        MaxHealth = 5,
                        Experience = 20,
                        Level = 1,
                        RespawnSeconds = 10f,
                        AggroRadius = 0f,
                        LeashRadius = 8f,
                        AttackDamage = 1,
                        AttackRange = 1f,
                        AttackCooldownSeconds = 1.5f,
                    },
                },
                Spawners = new List<NpcSpawner>
                {
                    new NpcSpawner { TypeId = 2001, X = -1.5f, Y = -1.5f, Count = 1, WanderRadius = 1f },
                },
            };

            using var harness = new Harness(npcs: npcs);
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();
            PlayerEntity player = Assert.IsType<PlayerEntity>(
                harness.Zone.Entities[harness.Zone.Entities.Count - 1]);
            player.Experience = 90;

            harness.Transport.Sent.Clear();
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeAttack(
                new AttackMessage(harness.Zone.Entities[0].Id == player.Id
                    ? harness.Zone.Entities[1].Id
                    : harness.Zone.Entities[0].Id)));
            harness.Zone.Tick();
            harness.Server.PollOnce();

            SentMessage progressSent = Assert.Single(harness.Transport.Sent,
                sent => MessageCodec.TryReadProgress(sent.Data, 0, sent.Data.Length, out _));
            Assert.True(MessageCodec.TryReadProgress(
                progressSent.Data, 0, progressSent.Data.Length, out ProgressMessage progress));
            Assert.Equal(10u, progress.Experience);
            Assert.Equal(150u, progress.ExperienceToNext);
            Assert.Contains(harness.Transport.Sent, sent =>
                MessageCodec.TryReadStats(sent.Data, 0, sent.Data.Length, out StatsMessage stats) && stats.Level == 2);
            Assert.Equal(1, harness.Zone.NpcDeaths);
        }

        [Fact]
        public void Tras_Una_Muerte_Penalizada_Se_Envia_Progress_Con_La_Experiencia_Nueva()
        {
            var npcs = new NpcCatalog
            {
                Types = new List<NpcType>
                {
                    new NpcType
                    {
                        Id = 2001,
                        Name = "Orco",
                        Speed = 2f,
                        HalfWidth = 0.35f,
                        HalfHeight = 0.35f,
                        MaxHealth = 10,
                        Experience = 20,
                        Level = 1,
                        RespawnSeconds = 10f,
                        AggroRadius = 4f,
                        LeashRadius = 8f,
                        AttackDamage = 1000,
                        AttackRange = 1.5f,
                        AttackCooldownSeconds = 1.5f,
                    },
                },
                Spawners = new List<NpcSpawner>
                {
                    new NpcSpawner { TypeId = 2001, X = -1.5f, Y = -2.5f, Count = 1, WanderRadius = 1f },
                },
            };

            using var harness = new Harness(npcs: npcs);
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();
            PlayerEntity player = Assert.IsType<PlayerEntity>(harness.Zone.Entities[harness.Zone.Entities.Count - 1]);
            player.Experience = 50;
            player.Health = 1;
            harness.Transport.Sent.Clear();

            harness.Zone.Tick();
            harness.Server.PollOnce();
            harness.Zone.Tick();
            harness.Server.PollOnce();
            harness.Zone.Tick();
            harness.Server.PollOnce();

            Assert.Equal(40, player.Experience);
            Assert.DoesNotContain(harness.Transport.Sent, sent =>
                MessageCodec.TryReadProgress(sent.Data, 0, sent.Data.Length, out _));

            // Zone publica ProgressChanged antes de correr PlayerVitalsSystem; el cambio se envía
            // en la siguiente vuelta, cuando ya contiene la XP penalizada.
            harness.Zone.Tick();
            harness.Server.PollOnce();

            SentMessage progressSent = Assert.Single(harness.Transport.Sent,
                sent => MessageCodec.TryReadProgress(sent.Data, 0, sent.Data.Length, out _));
            Assert.True(MessageCodec.TryReadProgress(
                progressSent.Data, 0, progressSent.Data.Length, out ProgressMessage progress));
            Assert.Equal(40u, progress.Experience);
            Assert.Equal(1L, harness.Zone.PenalizedDeaths);
            Assert.Equal(10L, harness.Zone.ExperienceLost);
        }

        [Fact]
        public void Equipar_Actualiza_Stats_Con_El_Daño_Del_Arma()
        {
            using var harness = new Harness(withItems: true);
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();
            PlayerEntity player = Assert.IsType<PlayerEntity>(harness.Zone.Entities[harness.Zone.Entities.Count - 1]);
            player.Inventory[0] = new InventorySlot(3101, 1);
            harness.Transport.Sent.Clear();

            harness.Transport.RaiseReceived(1, MessageCodec.EncodeEquip(new EquipMessage(0)));
            harness.Server.PollOnce();
            harness.Zone.Tick();
            harness.Server.PollOnce();

            Assert.Contains(harness.Transport.Sent, sent => MessageCodec.TryReadInventory(
                sent.Data, 0, sent.Data.Length, out InventoryMessage inventory) &&
                inventory.Equipment[(int)EquipSlot.Weapon] == 3101);
            Assert.Contains(harness.Transport.Sent, sent => MessageCodec.TryReadStats(
                sent.Data, 0, sent.Data.Length, out StatsMessage stats) && stats.AttackDamage == 15);

            harness.Transport.Sent.Clear();
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeUnequip(new UnequipMessage()));
            harness.Server.PollOnce();
            harness.Zone.Tick();
            harness.Server.PollOnce();

            Assert.Contains(harness.Transport.Sent, sent => MessageCodec.TryReadInventory(
                sent.Data, 0, sent.Data.Length, out InventoryMessage inventory) &&
                inventory.Equipment[(int)EquipSlot.Weapon] == 0);
            Assert.Contains(harness.Transport.Sent, sent => MessageCodec.TryReadStats(
                sent.Data, 0, sent.Data.Length, out StatsMessage stats) && stats.AttackDamage == 6);
        }

        [Fact]
        public void Equipar_Y_Desequipar_Botas_Publica_StatusEffects_Con_La_Velocidad_Total()
        {
            ItemCatalog items = ItemCatalog.Load(Path.Combine(AppContext.BaseDirectory, "items.json"));
            using var harness = new Harness(customItems: items);
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();
            PlayerEntity player = Assert.IsType<PlayerEntity>(harness.Zone.Entities[harness.Zone.Entities.Count - 1]);
            player.Inventory[0] = new InventorySlot(3306, 1);
            harness.Transport.Sent.Clear();

            harness.Transport.RaiseReceived(1, MessageCodec.EncodeEquip(new EquipMessage(0)));
            harness.Server.PollOnce();
            harness.Zone.Tick();
            harness.Server.PollOnce();

            SentMessage equipped = Assert.Single(harness.Transport.Sent, sent =>
                MessageCodec.TryReadStatusEffects(sent.Data, 0, sent.Data.Length, out _));
            Assert.True(MessageCodec.TryReadStatusEffects(
                equipped.Data, 0, equipped.Data.Length, out StatusEffectsMessage equippedStatus));
            Assert.Equal((byte)105, equippedStatus.SpeedPercent);
            Assert.Empty(equippedStatus.Effects);

            harness.Transport.Sent.Clear();
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeUnequip(
                new UnequipMessage((byte)EquipSlot.Feet, byte.MaxValue)));
            harness.Server.PollOnce();
            harness.Zone.Tick();
            harness.Server.PollOnce();

            SentMessage unequipped = Assert.Single(harness.Transport.Sent, sent =>
                MessageCodec.TryReadStatusEffects(sent.Data, 0, sent.Data.Length, out _));
            Assert.True(MessageCodec.TryReadStatusEffects(
                unequipped.Data, 0, unequipped.Data.Length, out StatusEffectsMessage unequippedStatus));
            Assert.Equal((byte)100, unequippedStatus.SpeedPercent);
        }

        [Fact]
        public void Alta_Con_Botas_Publica_StatusEffects_Despues_De_Stats()
        {
            ushort[] equipment = new ushort[ProtocolConstants.EquipSlotCount];
            equipment[(int)EquipSlot.Feet] = 3306;
            DateTime now = DateTime.UtcNow;
            var character = new CharacterRecord(
                1, 1, "dev", 1, 0, 100, 100, 1, new Vec2(-1.5f, -2.5f),
                new byte[ProtocolConstants.MapHashSize], now, now, equipment: equipment);
            ItemCatalog items = ItemCatalog.Load(Path.Combine(AppContext.BaseDirectory, "items.json"));
            using var repository = new FixedCharacterRepository(character);
            using var saves = new CharacterSaveService(repository);
            using var harness = new Harness(customItems: items, repository: repository, saves: saves);
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            SentMessage stats = Assert.Single(harness.Transport.Sent, sent =>
                MessageCodec.TryReadStats(sent.Data, 0, sent.Data.Length, out _));
            SentMessage status = Assert.Single(harness.Transport.Sent, sent =>
                MessageCodec.TryReadStatusEffects(sent.Data, 0, sent.Data.Length, out _));
            Assert.True(MessageCodec.TryReadStatusEffects(
                status.Data, 0, status.Data.Length, out StatusEffectsMessage statusMessage));
            Assert.Equal((byte)105, statusMessage.SpeedPercent);
            Assert.Empty(statusMessage.Effects);
            Assert.True(harness.Transport.Sent.IndexOf(stats) < harness.Transport.Sent.IndexOf(status));
        }

        [Fact]
        public void Stats_Con_Baston_Equipado_Indica_Daño_Magico()
        {
            ItemCatalog items = ItemCatalog.Load(Path.Combine(AppContext.BaseDirectory, "items.json"));
            using var harness = new Harness(customItems: items);
            harness.Connect(1);
            harness.Handshake(1, classId: 2);
            harness.Pump();
            PlayerEntity player = Assert.IsType<PlayerEntity>(harness.Zone.Entities[harness.Zone.Entities.Count - 1]);
            player.Level = 3;
            player.Inventory[0] = new InventorySlot(3104, 1);
            harness.Transport.Sent.Clear();

            harness.Transport.RaiseReceived(1, MessageCodec.EncodeEquip(new EquipMessage(0)));
            harness.Server.PollOnce();
            harness.Zone.Tick();
            harness.Server.PollOnce();

            Assert.Equal((ushort)3104, player.EquippedWeaponId);
            Assert.Contains(harness.Transport.Sent, sent => MessageCodec.TryReadStats(
                sent.Data, 0, sent.Data.Length, out StatsMessage stats) && stats.DamageType == 1);
        }

        [Fact]
        public void Reajuste_Con_Piedra_Envia_Inventario_Y_Stats()
        {
            using var harness = new Harness(withItems: true);
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();
            PlayerEntity player = Assert.IsType<PlayerEntity>(harness.Zone.Entities[harness.Zone.Entities.Count - 1]);
            player.Level = 11;
            player.AllocStr = 1;
            player.Inventory[0] = new InventorySlot(3201, 1);
            harness.Transport.Sent.Clear();

            harness.Transport.RaiseReceived(1, MessageCodec.EncodeResetPoints(new ResetPointsMessage()));
            harness.Server.PollOnce();
            harness.Zone.Tick();
            harness.Server.PollOnce();

            Assert.Equal(0, player.AllocStr);
            Assert.Contains(harness.Transport.Sent, sent => MessageCodec.TryReadInventory(
                sent.Data, 0, sent.Data.Length, out InventoryMessage inventory) && inventory.ItemIds[0] == 0);
            Assert.Contains(harness.Transport.Sent, sent => MessageCodec.TryReadStats(
                sent.Data, 0, sent.Data.Length, out StatsMessage stats) && stats.Level == 11);
        }

        [Fact]
        public void Reparto_Se_Persiste_Al_Desconectar_Y_Reconectar_De_Inmediato()
        {
            using var folder = new TemporaryFolder();
            string database = Path.Combine(folder.Path, "avelora.db");
            MapCollisionGrid map = MapFile.Load(MapPath);
            byte[] mapHash = MapFile.ComputeContentHashBytes(map);
            ItemCatalog items = new ItemCatalog
            {
                Items = new List<ItemType>
                {
                    new ItemType { Id = 3201, Name = "Piedra de reajuste", Kind = "material", MaxStack = 10 },
                },
            };

            using var repository = new SqlitePlayerRepository(
                database,
                passwordIterations: 1_000,
                items: items,
                levels: LevelTable.Default,
                classes: Avelora.Server.Progression.ClassCatalog.Default);
            repository.Initialize();
            AccountLoginResult account = repository.Login("dev", "avelora-dev", "avelora-dev", "avelora-dev");
            Assert.True(account.Success);
            CharacterRecord character = repository.CreateCharacter(
                account.AccountId,
                "dev",
                1,
                3,
                1,
                new Vec2(-1.5f, -2.5f),
                mapHash).Character!;
            repository.SaveCharacter(character);

            using var saves = new CharacterSaveService(repository);
            using var harness = new Harness(repository: repository, saves: saves);
            harness.Connect(1);
            harness.Handshake(1);
            for (int i = 0; i < 100 && harness.Zone.Players.Count == 0; i++)
            {
                harness.Pump();
                harness.Zone.Tick();
                Thread.Sleep(5);
            }

            PlayerEntity firstPlayer = Assert.IsType<PlayerEntity>(harness.Zone.Entities[harness.Zone.Entities.Count - 1]);
            Assert.Equal(ZoneKind.Normal, firstPlayer.ZoneKind);
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeAllocatePoints(
                new AllocatePointsMessage(2, 0, 0, 1)));
            harness.Server.PollOnce();
            harness.Zone.Tick();
            firstPlayer.PvpMode = PvpMode.Pvp;
            firstPlayer.PvpForcedTicksLeft = 501;

            harness.Transport.RaiseDisconnected(1);
            harness.Server.PollOnce();
            harness.Server.BeforeTick(harness.Zone);
            harness.Zone.Tick();
            Assert.True(saves.TryGetLatest(firstPlayer.CharacterId, out CharacterRecord saved));
            Assert.Equal(2, saved.AllocStr);
            Assert.Equal(1, saved.AllocVit);
            Assert.Equal(1, saved.PvpMode);
            Assert.Equal(26, saved.PvpForcedSeconds);

            harness.Connect(2);
            harness.Handshake(2);
            for (int i = 0; i < 100 && harness.Zone.Players.Count == 0; i++)
            {
                harness.Pump();
                harness.Zone.Tick();
                Thread.Sleep(5);
            }

            PlayerEntity reconnected = Assert.IsType<PlayerEntity>(harness.Zone.Entities[harness.Zone.Entities.Count - 1]);
            Assert.Equal(2, reconnected.AllocStr);
            Assert.Equal(1, reconnected.AllocVit);
            Assert.Equal(PvpMode.Pvp, reconnected.PvpMode);
            // El forzado se restaura a 520 ticks; el tick de conexión ya ha consumido uno.
            Assert.Equal((26 * SimulationConstants.TickRate) - 1, reconnected.PvpForcedTicksLeft);
            Assert.True(saves.Flush(TimeSpan.FromSeconds(2)));
            Assert.Equal(SqlitePlayerRepository.SchemaVersion, QuerySchemaVersion(database));
        }

        [Fact]
        public void Modo_Y_Forzado_Pvp_Se_Conservan_Al_Reconectar_Con_Repositorio_En_Memoria()
        {
            using var repository = new InMemoryPlayerRepository(ClassCatalog.Default, 1);
            repository.Initialize();
            using var saves = new CharacterSaveService(repository);
            using var harness = new Harness(repository: repository, saves: saves);
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            PlayerEntity first = Assert.IsType<PlayerEntity>(harness.Zone.Entities.Get(1));
            Assert.Equal(ZoneKind.Normal, first.ZoneKind);
            first.PvpMode = PvpMode.Pvp;
            first.PvpForcedTicksLeft = 501;
            harness.Transport.RaiseDisconnected(1);
            harness.Server.PollOnce();
            harness.Server.BeforeTick(harness.Zone);
            harness.Zone.Tick();
            harness.Server.PollOnce();

            Assert.True(saves.Flush(TimeSpan.FromSeconds(2)));
            CharacterRecord saved = Assert.Single(repository.ListCharacters(1));
            Assert.Equal(1, saved.PvpMode);
            Assert.Equal(26, saved.PvpForcedSeconds);

            harness.Connect(2);
            harness.Handshake(2);
            harness.Pump();
            PlayerEntity reconnected = Assert.IsType<PlayerEntity>(harness.Zone.Entities.Get(2));
            Assert.Equal(PvpMode.Pvp, reconnected.PvpMode);
            Assert.Equal(26 * SimulationConstants.TickRate, reconnected.PvpForcedTicksLeft);
        }

        [Fact]
        public void Un_Jugador_Que_Se_Desconecta_En_La_Ciudad_Guarda_Forzado_Cero()
        {
            var config = new ServerConfig();
            MapCollisionGrid map = MapFile.Load(MapPath);
            int spawnX = (int)Math.Floor(config.SpawnX);
            int spawnY = (int)Math.Floor(config.SpawnY);
            ZoneMap zones = ZoneMap.FromDefinitions(new[]
            {
                new ZoneDefinition
                {
                    Id = 1,
                    Name = "Ciudad de persistencia",
                    Kind = "safe",
                    Rects = new List<ZoneRectDefinition>
                    {
                        new ZoneRectDefinition { X = spawnX, Y = spawnY, W = 1, H = 1 },
                    },
                },
            }, map);
            Assert.Equal(ZoneKind.Safe, zones.KindAt(config.SpawnX, config.SpawnY));

            using var repository = new InMemoryPlayerRepository(ClassCatalog.Default, 1);
            repository.Initialize();
            using var saves = new CharacterSaveService(repository);
            using var harness = new Harness(repository: repository, saves: saves, zones: zones);
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            PlayerEntity first = Assert.IsType<PlayerEntity>(harness.Zone.Entities.Get(1));
            first.PvpMode = PvpMode.Pvp;
            first.PvpForcedTicksLeft = 501;
            harness.Zone.Tick();
            Assert.Equal(ZoneKind.Safe, first.ZoneKind);
            Assert.Equal(0, first.PvpForcedTicksLeft);

            harness.Transport.RaiseDisconnected(1);
            harness.Server.PollOnce();
            harness.Server.BeforeTick(harness.Zone);
            harness.Zone.Tick();
            harness.Server.PollOnce();
            Assert.True(saves.Flush(TimeSpan.FromSeconds(2)));
            CharacterRecord saved = Assert.Single(repository.ListCharacters(1));
            Assert.Equal(0, saved.PvpForcedSeconds);

            harness.Connect(2);
            harness.Handshake(2);
            harness.Pump();
            PlayerEntity reconnected = Assert.IsType<PlayerEntity>(harness.Zone.Entities.Get(2));
            Assert.Equal(0, reconnected.PvpForcedTicksLeft);
            Assert.Equal(PvpMode.Pvp, reconnected.PvpMode);
        }

        [Fact]
        public void El_Chat_Se_Entrega_A_Toda_La_Zona_Incluido_El_Emisor()
        {
            using var harness = new Harness(config =>
            {
                config.InterestRadius = 1f;
                config.InterestCellSize = 2f;
                config.InterestHysteresis = 0f;
            });
            harness.Connect(1);
            harness.Handshake(1, characterName: "Alicia");
            harness.Pump();
            harness.Connect(2);
            harness.Handshake(2, characterName: "Bruno");
            harness.Pump();

            Assert.True(harness.Zone.Entities.Count >= 2,
                string.Join(",", harness.Transport.Sent.Select(sent =>
                    MessageCodec.TryReadReject(sent.Data, 0, sent.Data.Length, out RejectMessage reject)
                        ? sent.ConnectionId + ":" + reject.Reason
                        : sent.ConnectionId + ":" + (sent.Data.Length > 0 ? sent.Data[0].ToString() : "empty"))));

            ((PlayerEntity)harness.Zone.Entities.Get(1)!).Position = new Vec2(-7f, 5f);
            ((PlayerEntity)harness.Zone.Entities.Get(2)!).Position = new Vec2(7f, -5f);
            harness.Zone.Tick();
            ((PlayerEntity)harness.Zone.Entities.Get(1)!).Action = EntityAction.Dead;
            harness.Transport.Sent.Clear();

            harness.Transport.RaiseReceived(1, MessageCodec.EncodeChatSend(new ChatSendMessage("  Hola  ")));
            harness.Server.PollOnce();

            List<SentMessage> messages = harness.Transport.Sent.FindAll(sent =>
                MessageCodec.TryReadChat(sent.Data, 0, sent.Data.Length, out _));
            Assert.Equal(2, messages.Count);
            Assert.All(messages, sent =>
            {
                Assert.True(MessageCodec.TryReadChat(sent.Data, 0, sent.Data.Length, out ChatMessage chat));
                Assert.Equal(1, chat.SenderEntityId);
                Assert.Equal("Alicia", chat.SenderName);
                Assert.Equal("Hola", chat.Text);
                Assert.Equal(DeliveryMode.ReliableOrdered, sent.Mode);
            });
            Assert.Equal(1, harness.Server.Snapshot().ChatReceived);
            Assert.Equal(2, harness.Server.Snapshot().ChatDelivered);
        }

        [Fact]
        public void El_Nombre_Del_Emisor_Se_Trunca_Sin_Partir_UTF8()
        {
            using var harness = new Harness();
            harness.Connect(1);
            harness.Handshake(1, characterName: new string('á', 16));
            harness.Pump();
            harness.Transport.Sent.Clear();

            harness.Transport.RaiseReceived(1, MessageCodec.EncodeChatSend(new ChatSendMessage("hola")));
            harness.Server.PollOnce();

            SentMessage sent = Assert.Single(harness.Transport.Sent);
            Assert.True(MessageCodec.TryReadChat(sent.Data, 0, sent.Data.Length, out ChatMessage chat));
            Assert.Equal(new string('á', 12), chat.SenderName);
        }

        [Fact]
        public void El_Chat_Rechaza_Texto_Invalido_Largo_Y_UTF8_Roto_Sin_Enviar()
        {
            using var harness = new Harness();
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();
            harness.Transport.Sent.Clear();

            harness.Transport.RaiseReceived(1, MessageCodec.EncodeChatSend(new ChatSendMessage("   ")));
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeChatSend(new ChatSendMessage("hola\nque tal")));
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeChatSend(new ChatSendMessage("hola\u200B")));
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeChatSend(new ChatSendMessage("hola\u202E")));

            byte[] tooLong = new byte[3 + 121];
            tooLong[0] = (byte)MessageType.ChatSend;
            tooLong[1] = 121;
            harness.Transport.RaiseReceived(1, tooLong);
            harness.Transport.RaiseReceived(1, new byte[] { (byte)MessageType.ChatSend, 1, 0, 0xFF });
            harness.Server.PollOnce();

            Assert.Empty(harness.Transport.Sent);
            NetworkStats stats = harness.Server.Snapshot();
            Assert.Equal(0, stats.ChatReceived);
            Assert.Equal(0, stats.ChatDelivered);
            Assert.Equal(5, stats.ChatRejectedInvalid);
            Assert.Equal(1, stats.ChatRejectedLong);
        }

        [Fact]
        public void El_Chat_Limita_Rafaga_Y_Repone_Una_Ficha_Tras_Un_Segundo_De_Ticks()
        {
            using var harness = new Harness();
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();
            harness.Transport.Sent.Clear();

            byte[] chat = MessageCodec.EncodeChatSend(new ChatSendMessage("hola"));
            harness.Transport.RaiseReceived(1, chat);
            harness.Transport.RaiseReceived(1, chat);
            harness.Transport.RaiseReceived(1, chat);
            harness.Transport.RaiseReceived(1, chat);

            Assert.Equal(1, harness.Server.Snapshot().ChatRejectedFast);
            for (int i = 0; i < SimulationConstants.TickRate; i++)
            {
                harness.Zone.Tick();
            }

            harness.Transport.RaiseReceived(1, chat);
            harness.Server.PollOnce();

            NetworkStats stats = harness.Server.Snapshot();
            Assert.Equal(4, stats.ChatReceived);
            Assert.Equal(4, stats.ChatDelivered);
            Assert.Equal(1, stats.ChatRejectedFast);
        }

        [Fact]
        public void La_Red_Encola_Recoger_Y_Usar_Y_Envía_El_Inventario_Completo()
        {
            using var harness = new Harness(withItems: true);
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            Assert.True(MessageCodec.TryReadWelcome(
                harness.Transport.Sent[0].Data, 0, harness.Transport.Sent[0].Data.Length, out _));
            Assert.True(MessageCodec.TryReadItemCatalog(
                harness.Transport.Sent[1].Data, 0, harness.Transport.Sent[1].Data.Length, out ItemCatalogMessage catalog));
            Assert.Contains(catalog.Items, item => item.Id == 3101 && item.Kind == ItemKind.Weapon && item.Damage == 14);
            Assert.True(MessageCodec.TryReadInventory(
                harness.Transport.Sent[2].Data, 0, harness.Transport.Sent[2].Data.Length, out _));

            PlayerEntity player = Assert.IsType<PlayerEntity>(harness.Zone.Entities.Get(1));
            var ground = new GroundItemEntity(
                harness.Zone.Ids.Allocate(), 3001, 2, 100)
            {
                Position = player.Position,
            };
            harness.Zone.AddEntity(ground);
            harness.Transport.RaiseReceived(1, MessageCodec.EncodePickUp(new PickUpMessage(ground.Id)));
            harness.Zone.Tick();
            Assert.Equal((ushort)2, player.Inventory[0].Count);
            Assert.False(harness.Zone.Entities.TryGet(ground.Id, out _));

            harness.Transport.Sent.Clear();
            harness.Server.PollOnce();
            Assert.Contains(harness.Transport.Sent, sent =>
                MessageCodec.TryReadInventory(sent.Data, 0, sent.Data.Length, out InventoryMessage inventory) &&
                inventory.SlotCount == 20 && inventory.ItemIds[0] == 3001 && inventory.Counts[0] == 2);

            player.Health = 50;
            player.Inventory[0] = new InventorySlot(3002, 1);
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeUseItem(new UseItemMessage(0)));
            harness.Zone.Tick();
            Assert.Equal(80, player.Health);
            Assert.Equal((ushort)0, player.Inventory[0].ItemId);

            harness.Transport.Sent.Clear();
            harness.Server.PollOnce();
            Assert.Contains(harness.Transport.Sent, sent =>
                MessageCodec.TryReadInventory(sent.Data, 0, sent.Data.Length, out InventoryMessage inventory) &&
                inventory.ItemIds[0] == 0 && inventory.Counts[0] == 0);
        }

        [Fact]
        public void La_Red_Encola_Equipar_Y_El_Inventario_Informa_El_Arma_Equipada()
        {
            using var harness = new Harness(withItems: true);
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            PlayerEntity player = Assert.IsType<PlayerEntity>(harness.Zone.Entities.Get(1));
            player.Inventory[0] = new InventorySlot(3101, 1);
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeEquip(new EquipMessage(0)));
            harness.Zone.Tick();

            Assert.Equal((ushort)3101, player.EquippedWeaponId);
            Assert.Equal(new InventorySlot(0, 0), player.Inventory[0]);

            harness.Transport.Sent.Clear();
            harness.Server.PollOnce();
            SentMessage inventorySent = Assert.Single(harness.Transport.Sent,
                sent => MessageCodec.TryReadInventory(sent.Data, 0, sent.Data.Length, out _));
            Assert.True(MessageCodec.TryReadInventory(
                inventorySent.Data, 0, inventorySent.Data.Length, out InventoryMessage inventory));
            Assert.Equal((ushort)3101, inventory.Equipment[(int)EquipSlot.Weapon]);
        }

        [Fact]
        public void La_Red_Encola_Mover_Destruir_Equipar_Y_Desequipar_En_La_Cola_Del_Tick()
        {
            using var harness = new Harness(withItems: true);
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();
            PlayerEntity player = Assert.IsType<PlayerEntity>(harness.Zone.Entities.Get(1));
            player.Inventory[0] = new InventorySlot(3001, 4);
            player.Inventory[1] = new InventorySlot(3001, 2);
            player.Inventory[2] = new InventorySlot(3301, 1);

            harness.Transport.RaiseReceived(1, MessageCodec.EncodeMoveItem(new MoveItemMessage(0, 1, 0)));
            harness.Server.PollOnce();
            harness.Zone.Tick();
            Assert.Equal(new InventorySlot(0, 0), player.Inventory[0]);
            Assert.Equal(new InventorySlot(3001, 6), player.Inventory[1]);

            harness.Transport.RaiseReceived(1, MessageCodec.EncodeDestroyItem(new DestroyItemMessage(1, 2)));
            harness.Server.PollOnce();
            harness.Zone.Tick();
            Assert.Equal(new InventorySlot(3001, 4), player.Inventory[1]);

            harness.Transport.RaiseReceived(1, MessageCodec.EncodeEquip(new EquipMessage(2)));
            harness.Server.PollOnce();
            harness.Zone.Tick();
            Assert.Equal((ushort)3301, player.Equipment[(int)EquipSlot.Head]);
            Assert.Equal(1, harness.Zone.ItemsEquipped);

            harness.Transport.RaiseReceived(1, MessageCodec.EncodeUnequip(
                new UnequipMessage((byte)EquipSlot.Head, byte.MaxValue)));
            harness.Server.PollOnce();
            harness.Zone.Tick();
            Assert.Equal((ushort)0, player.Equipment[(int)EquipSlot.Head]);
            Assert.Equal(1, harness.Zone.ItemsUnequipped);
            Assert.Equal(1, harness.Zone.ItemsDestroyed);
            Assert.Equal(1, harness.Zone.ItemsMoved);
        }

        [Fact]
        public void Tipo_21_Retirado_Se_Rechaza_Sin_Cambiar_El_Inventario()
        {
            using var harness = new Harness(withItems: true);
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();
            PlayerEntity player = Assert.IsType<PlayerEntity>(harness.Zone.Entities.Get(1));
            player.Inventory[0] = new InventorySlot(3001, 5);

            harness.Transport.RaiseReceived(1, new byte[] { 21, 0, 1, 0 });
            harness.Server.PollOnce();

            Assert.Equal(new InventorySlot(3001, 5), player.Inventory[0]);
            Assert.Equal(0, harness.Zone.GroundItemCount);
            Assert.Equal(1, harness.Server.Snapshot().MessagesRejected);
        }

        [Fact]
        public void Una_Version_Distinta_Se_Rechaza()
        {
            using var harness = new Harness(config => config.RejectDisconnectDelayMilliseconds = 0);

            harness.Connect(1);
            harness.Handshake(1, version: (ushort)(ProtocolConstants.Version + 1));

            Assert.Equal(0, harness.Zone.Entities.Count);
            Assert.Single(harness.Transport.Sent);
            Assert.True(MessageCodec.TryReadReject(
                harness.Transport.Sent[0].Data,
                0,
                harness.Transport.Sent[0].Data.Length,
                out RejectMessage reject));
            Assert.Equal(RejectReason.ProtocolVersion, reject.Reason);

            // El rechazo sale en la llamada, pero el corte espera a que venza el retardo.
            harness.Server.PollOnce();
            Assert.Contains(1, harness.Transport.DisconnectedConnections);
        }

        [Fact]
        public void El_Rechazo_De_Saludo_No_Corta_En_La_Misma_Vuelta()
        {
            using var harness = new Harness(config => config.RejectDisconnectDelayMilliseconds = 0);

            harness.Connect(1);
            harness.Handshake(1, version: (ushort)(ProtocolConstants.Version + 1));

            // El motivo ya viajó…
            SentMessage sent = harness.Transport.Sent[0];
            Assert.True(MessageCodec.TryReadReject(sent.Data, 0, sent.Data.Length, out RejectMessage reject));
            Assert.Equal(RejectReason.ProtocolVersion, reject.Reason);

            // …pero el corte todavía no se ha pedido.
            Assert.Empty(harness.Transport.DisconnectedConnections);

            // Con retardo 0 vence en la vuelta siguiente, después de haber podido enviar el Reject.
            harness.Server.PollOnce();
            Assert.Equal(new[] { 1 }, harness.Transport.DisconnectedConnections);
        }

        [Fact]
        public void Un_Mapa_Distinto_Se_Rechaza()
        {
            using var harness = new Harness();

            var otherHash = new byte[ProtocolConstants.MapHashSize];
            Array.Copy(harness.MapHash, otherHash, otherHash.Length);
            otherHash[0] ^= 0xFF;

            harness.Connect(1);
            harness.Handshake(1, mapHash: otherHash);

            Assert.Equal(0, harness.Zone.Entities.Count);
            Assert.True(MessageCodec.TryReadReject(
                harness.Transport.Sent[0].Data,
                0,
                harness.Transport.Sent[0].Data.Length,
                out RejectMessage reject));
            Assert.Equal(RejectReason.MapMismatch, reject.Reason);
        }

        [Fact]
        public void Un_Saludo_Mal_Formado_Se_Rechaza()
        {
            using var harness = new Harness(config => config.RejectDisconnectDelayMilliseconds = 0);

            harness.Connect(1);
            harness.Transport.RaiseReceived(1, new byte[] { (byte)MessageType.Hello, 1, 2, 3 });

            Assert.Equal(0, harness.Zone.Entities.Count);
            Assert.True(MessageCodec.TryReadReject(
                harness.Transport.Sent[0].Data,
                0,
                harness.Transport.Sent[0].Data.Length,
                out RejectMessage reject));
            Assert.Equal(RejectReason.MalformedMessage, reject.Reason);

            harness.Server.PollOnce();
            Assert.Contains(1, harness.Transport.DisconnectedConnections);
        }

        [Fact]
        public void Los_Inputs_Mueven_Al_Jugador_Solo_Al_Simular()
        {
            using var harness = new Harness();

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            harness.Zone.Tick();
            Assert.Equal(-1.5f, harness.Zone.Players[0].X);

            harness.Transport.RaiseReceived(1, MessageCodec.EncodeMoveInput(new MoveInputMessage(1u, 1, 0)));

            // Encolar no mueve a nadie: el movimiento ocurre en el tick.
            Assert.Equal(-1.5f, harness.Zone.Players[0].X);

            harness.Zone.Tick();

            Assert.True(harness.Zone.Players[0].X > -1.5f);
            Assert.Equal(1u, harness.Zone.Players[0].LastProcessedSequence);
        }

        [Fact]
        public void Un_Input_Con_Direccion_Invalida_No_Llega_Al_Jugador()
        {
            using var harness = new Harness();

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            // El códec rechaza la dirección (5 no es -1, 0 ni 1) antes de encolarla.
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeMoveInput(new MoveInputMessage(1u, 5, 0)));
            harness.Zone.Tick();

            Assert.True(harness.Server.Snapshot().MessagesRejected >= 1);
            Assert.Equal(0, harness.Zone.Inputs.Pending);
            Assert.Equal(0, harness.Zone.Inputs.RejectedInvalidDirection);
            Assert.Equal(-1.5f, harness.Zone.Players[0].X);
        }

        [Fact]
        public void Una_Conexion_Sin_Saludo_No_Puede_Mover_Nada()
        {
            using var harness = new Harness();

            harness.Connect(1);
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeMoveInput(new MoveInputMessage(1u, 1, 0)));

            Assert.Equal(0, harness.Zone.Inputs.Pending);

            // Antes del saludo, cualquier mensaje es un saludo mal formado: se rechaza y se cierra.
            Assert.True(harness.Server.Snapshot().HandshakesRejected >= 1);
            Assert.Equal(0, harness.Zone.Entities.Count);
        }

        [Fact]
        public void La_Desconexion_Quita_Al_Jugador()
        {
            using var harness = new Harness();

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();
            Assert.Equal(1, harness.Zone.Entities.Count);

            harness.Transport.RaiseDisconnected(1);
            harness.Server.BeforeTick(harness.Zone);

            Assert.Equal(0, harness.Zone.Entities.Count);
            Assert.Equal(1, harness.Server.Snapshot().PlayersLeft);
        }

        [Fact]
        public void La_Zona_Llena_Rechaza_Con_Motivo()
        {
            using var harness = new Harness(config =>
            {
                config.MaxPlayers = 1;
                config.RejectDisconnectDelayMilliseconds = 0;
            });

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            harness.Connect(2);
            harness.Handshake(2);
            harness.Pump();

            Assert.Equal(1, harness.Zone.Entities.Count);

            SentMessage last = harness.Transport.Sent[harness.Transport.Sent.Count - 1];
            Assert.Equal(2, last.ConnectionId);
            Assert.True(MessageCodec.TryReadReject(last.Data, 0, last.Data.Length, out RejectMessage reject));
            Assert.Equal(RejectReason.ServerFull, reject.Reason);

            harness.Server.PollOnce();
            Assert.Contains(2, harness.Transport.DisconnectedConnections);
        }

        [Fact]
        public void El_Snapshot_Lleva_La_Posicion_Cuantizada()
        {
            using var harness = new Harness();

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            harness.Transport.Sent.Clear();
            harness.Server.AfterTick(harness.Zone);
            harness.Server.PollOnce();

            Assert.Single(harness.Transport.Sent);

            SentMessage sent = harness.Transport.Sent[0];
            Assert.True(MessageCodec.TryReadSnapshot(sent.Data, 0, sent.Data.Length, out SnapshotMessage snapshot));
            Assert.Single(snapshot.Entities);
            Assert.Equal(ProtocolConstants.QuantizePosition(-1.5f), snapshot.Entities[0].X);
            Assert.Equal(ProtocolConstants.QuantizePosition(-2.5f), snapshot.Entities[0].Y);
            Assert.Equal(EntityKind.Player, snapshot.Entities[0].Kind);
        }

        [Fact]
        public void El_Area_De_Interes_Filtra_Los_Snapshots_Y_Respeta_La_Histeresis()
        {
            using var harness = new Harness(config =>
            {
                config.InterestRadius = 2f;
                config.InterestCellSize = 2f;
                config.InterestHysteresis = 0.5f;
            });

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            harness.Connect(2);
            harness.Handshake(2);
            harness.Pump();

            PlayerEntity p1 = (PlayerEntity)harness.Zone.Entities.Get(1)!;
            PlayerEntity p2 = (PlayerEntity)harness.Zone.Entities.Get(2)!;

            // Lejos: cada uno se ve solo a sí mismo.
            p1.Position = new Vec2(-7f, 5f);
            p2.Position = new Vec2(7f, -5f);
            SendSnapshots(harness);

            SnapshotMessage s1 = SnapshotFor(harness, 1);
            SnapshotMessage s2 = SnapshotFor(harness, 2);
            Assert.Single(s1.Entities);
            Assert.Equal(1, s1.Entities[0].Id);
            Assert.Single(s2.Entities);
            Assert.Equal(2, s2.Entities[0].Id);

            // A distancia 2 (radio de entrada) aparece en ambos.
            p2.Position = new Vec2(-5f, 5f);
            SendSnapshots(harness);

            s1 = SnapshotFor(harness, 1);
            s2 = SnapshotFor(harness, 2);
            Assert.Equal(2, s1.Entities.Length);
            Assert.Equal(2, s2.Entities.Length);
            Assert.Contains(s1.Entities, e => e.Id == 2);
            Assert.Contains(s2.Entities, e => e.Id == 1);

            // A 2.4, fuera del radio de entrada pero dentro de la histéresis (2.5): sigue visible.
            p2.Position = new Vec2(-4.6f, 5f);
            SendSnapshots(harness);

            s1 = SnapshotFor(harness, 1);
            Assert.Equal(2, s1.Entities.Length);

            // A 2.6 ya supera el radio de salida: desaparece.
            p2.Position = new Vec2(-4.4f, 5f);
            SendSnapshots(harness);

            s1 = SnapshotFor(harness, 1);
            Assert.Single(s1.Entities);
            Assert.Equal(1, s1.Entities[0].Id);
        }

        [Fact]
        public void El_Snapshot_Lleva_Los_Pnjs_Cercanos_Con_Su_Direccion()
        {
            using var harness = new Harness(config =>
            {
                config.InterestRadius = 3f;
                config.InterestCellSize = 2f;
                config.InterestHysteresis = 0f;
            });

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            harness.Connect(2);
            harness.Handshake(2);
            harness.Pump();

            PlayerEntity near = (PlayerEntity)harness.Zone.Entities.Get(1)!;
            PlayerEntity far = (PlayerEntity)harness.Zone.Entities.Get(2)!;
            near.Position = new Vec2(0f, 5f);
            far.Position = new Vec2(-6f, -4f);

            var npc = new NpcEntity(
                100,
                2001,
                2f,
                new Vec2(1f, 5f),
                2f,
                nextDirectionChangeTick: 10_000,
                maxHealth: 30,
                respawnTicks: 200,
                spawnerIndex: -1)
            {
                Position = new Vec2(1f, 5f),
                HalfSize = new Vec2(0.35f, 0.35f),
                DirectionX = 1,
                DirectionY = -1,
            };

            harness.Zone.AddEntity(npc);
            SendSnapshots(harness);

            SnapshotMessage nearSnapshot = SnapshotFor(harness, 1);
            EntityState state = Assert.Single(nearSnapshot.Entities, e => e.Id == 100);
            Assert.Equal(EntityKind.Npc, state.Kind);
            Assert.Equal(1, state.DirectionX);
            Assert.Equal(-1, state.DirectionY);

            SnapshotMessage farSnapshot = SnapshotFor(harness, 2);
            Assert.DoesNotContain(farSnapshot.Entities, e => e.Id == 100);
        }

        [Fact]
        public void El_Snapshot_Lleva_La_Vida_Y_La_Accion()
        {
            using var harness = new Harness(config =>
            {
                config.InterestRadius = 6f;
                config.InterestCellSize = 2f;
                config.InterestHysteresis = 0f;
            });

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            PlayerEntity player = (PlayerEntity)harness.Zone.Entities.Get(1)!;
            player.Position = new Vec2(0f, 5f);

            var npc = new NpcEntity(
                100,
                2001,
                0f,
                new Vec2(1f, 5f),
                2f,
                nextDirectionChangeTick: long.MaxValue,
                maxHealth: 30,
                respawnTicks: 200,
                spawnerIndex: -1)
            {
                Position = new Vec2(1f, 5f),
                HalfSize = new Vec2(0.35f, 0.35f),
            };

            // Un orco herido: 21/30 = 70 % exacto.
            npc.Health = 21;
            harness.Zone.AddEntity(npc);

            SendSnapshots(harness);

            EntityState wounded = Assert.Single(SnapshotFor(harness, 1).Entities, e => e.Id == 100);
            Assert.Equal(EntityKind.Npc, wounded.Kind);
            Assert.Equal((byte)70, wounded.HealthPercent);
            Assert.Equal(EntityAction.None, wounded.Action);

            // El ataque cruza la red; el golpe sin arma deja 15/30 = 50 %.
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeAttack(new AttackMessage(100)));
            SendSnapshots(harness);

            SnapshotMessage afterAttack = SnapshotFor(harness, 1);
            EntityState hit = Assert.Single(afterAttack.Entities, e => e.Id == 100);
            Assert.Equal((byte)50, hit.HealthPercent);
            Assert.Equal(EntityAction.None, hit.Action);

            EntityState attacker = Assert.Single(afterAttack.Entities, e => e.Id == player.Id);
            Assert.Equal((byte)100, attacker.HealthPercent);
            Assert.Equal(EntityAction.Attacking, attacker.Action);

            // Un orco muerto manda 0 % y la acción Dead.
            npc.Health = 0;
            npc.Action = EntityAction.Dead;
            SendSnapshots(harness);

            EntityState dead = Assert.Single(SnapshotFor(harness, 1).Entities, e => e.Id == 100);
            Assert.Equal((byte)0, dead.HealthPercent);
            Assert.Equal(EntityAction.Dead, dead.Action);
        }

        [Fact]
        public void El_Snapshot_Lleva_La_Vida_Y_La_Accion_Del_Jugador_Y_El_Ataque_Del_Orco()
        {
            using var harness = new Harness(config =>
            {
                config.InterestRadius = 6f;
                config.InterestCellSize = 2f;
                config.InterestHysteresis = 0f;
            });

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            PlayerEntity player = (PlayerEntity)harness.Zone.Entities.Get(1)!;
            player.Position = new Vec2(0f, 5f);

            var npc = new NpcEntity(
                100,
                2001,
                0f,
                new Vec2(0.5f, 5f),
                2f,
                nextDirectionChangeTick: long.MaxValue,
                maxHealth: 30,
                respawnTicks: 200,
                spawnerIndex: -1,
                aggroRadius: 4f,
                leashRadius: 8f,
                attackDamage: 8,
                attackRange: 1f,
                attackCooldownTicks: 30)
            {
                Position = new Vec2(0.5f, 5f),
                HalfSize = new Vec2(0.35f, 0.35f),
            };

            harness.Zone.AddEntity(npc);

            // La zona de este arnés no tiene catálogo de PNJ, así que la IA no corre: esta prueba
            // solo comprueba el contenido del snapshot. La transición a ataque y el golpe se
            // cubren en I03Tests. Los valores se fijan después del tick para que el combate no
            // borre la acción Attacking.
            harness.Zone.Tick();

            player.Health = 92;
            npc.Action = EntityAction.Attacking;

            harness.Transport.Sent.Clear();
            harness.Server.AfterTick(harness.Zone);
            harness.Server.PollOnce();

            SnapshotMessage snapshot = SnapshotFor(harness, 1);
            EntityState hurt = Assert.Single(snapshot.Entities, e => e.Id == player.Id);
            Assert.Equal((byte)77, hurt.HealthPercent);
            Assert.Equal(EntityAction.None, hurt.Action);

            EntityState attacker = Assert.Single(snapshot.Entities, e => e.Id == 100);
            Assert.Equal(EntityAction.Attacking, attacker.Action);
            Assert.Equal((byte)100, attacker.HealthPercent);

            // Un jugador muerto manda 0 % y Dead.
            player.Health = 0;
            player.Action = EntityAction.Dead;
            harness.Zone.Tick();

            harness.Transport.Sent.Clear();
            harness.Server.AfterTick(harness.Zone);
            harness.Server.PollOnce();

            EntityState dead = Assert.Single(SnapshotFor(harness, 1).Entities, e => e.Id == player.Id);
            Assert.Equal((byte)0, dead.HealthPercent);
            Assert.Equal(EntityAction.Dead, dead.Action);
        }

        [Fact]
        public void El_Snapshot_Lleva_El_ClassId_Del_Jugador_Y_El_TypeId_Del_Orco()
        {
            using var harness = new Harness(config =>
            {
                config.InterestRadius = 6f;
                config.InterestCellSize = 2f;
                config.InterestHysteresis = 0f;
            });

            harness.Connect(1);
            harness.Handshake(1, characterName: "Mago", classId: 2);
            harness.Pump();

            PlayerEntity player = (PlayerEntity)harness.Zone.Entities.Get(1)!;
            player.Position = new Vec2(0f, 5f);

            // Orco guerrero (tipo 2002) más lento, más duro y pega más fuerte.
            var orc2002 = new NpcEntity(
                100,
                2002,
                0f,
                new Vec2(0.5f, 5f),
                2f,
                nextDirectionChangeTick: long.MaxValue,
                maxHealth: 60,
                respawnTicks: 400,
                spawnerIndex: -1,
                aggroRadius: 5f,
                leashRadius: 8f,
                attackDamage: 14,
                attackRange: 1f,
                attackCooldownTicks: 40,
                rank: 1)
            {
                Position = new Vec2(0.5f, 5f),
                HalfSize = new Vec2(0.35f, 0.35f),
            };

            harness.Zone.AddEntity(orc2002);
            harness.Zone.Tick();

            harness.Transport.Sent.Clear();
            harness.Server.AfterTick(harness.Zone);
            harness.Server.PollOnce();

            SnapshotMessage snapshot = SnapshotFor(harness, 1);

            // El jugador manda classId = 2 en TypeId.
            EntityState playerState = Assert.Single(snapshot.Entities, e => e.Id == player.Id);
            Assert.Equal(EntityKind.Player, playerState.Kind);
            Assert.Equal((ushort)2, playerState.TypeId);
            Assert.Equal((byte)0, playerState.Rank);

            // El orco 2002 manda su tipo en TypeId.
            EntityState orcState = Assert.Single(snapshot.Entities, e => e.Id == 100);
            Assert.Equal(EntityKind.Npc, orcState.Kind);
            Assert.Equal((ushort)2002, orcState.TypeId);
            Assert.Equal((byte)1, orcState.Rank);
        }

        [Fact]
        public void Al_Recortar_El_Snapshot_Se_Quitan_Objetos_Antes_Que_Enemigos()
        {
            // Auditoría: el recorte se quedaba con los primeros de la rejilla; con mucho botín en
            // el suelo podía caerse el orco que pega al jugador.
            using var harness = new Harness(config => config.InterestHysteresis = 0f);

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            PlayerEntity player = (PlayerEntity)harness.Zone.Entities.Get(1)!;
            player.Position = new Vec2(0f, 5f);

            for (int i = 0; i < ProtocolConstants.MaxEntitiesPerSnapshot + 10; i++)
            {
                harness.Zone.AddEntity(new GroundItemEntity(1000 + i, 3001, 1, long.MaxValue)
                {
                    Position = new Vec2(0.5f, 5f),
                });
            }

            // El orco llega el último a la misma celda que el botín.
            var orc = new NpcEntity(
                100,
                2001,
                0f,
                new Vec2(0.5f, 5f),
                2f,
                nextDirectionChangeTick: long.MaxValue,
                maxHealth: 30,
                respawnTicks: 400,
                spawnerIndex: -1)
            {
                Position = new Vec2(0.5f, 5f),
                HalfSize = new Vec2(0.35f, 0.35f),
            };
            harness.Zone.AddEntity(orc);
            harness.Zone.Tick();

            harness.Transport.Sent.Clear();
            harness.Server.AfterTick(harness.Zone);
            harness.Server.PollOnce();

            SnapshotMessage snapshot = SnapshotFor(harness, 1);
            Assert.Equal(ProtocolConstants.MaxEntitiesPerSnapshot, snapshot.Entities.Length);
            Assert.Contains(snapshot.Entities, e => e.Id == player.Id);
            Assert.Contains(snapshot.Entities, e => e.Id == 100);
        }

        [Fact]
        public void El_Jugador_Local_Se_Incluye_Aunque_El_Radio_Sea_Minimo()
        {
            using var harness = new Harness(config =>
            {
                config.InterestRadius = 0.1f;
                config.InterestCellSize = 1f;
                config.InterestHysteresis = 0f;
            });

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            harness.Connect(2);
            harness.Handshake(2);
            harness.Pump();

            PlayerEntity p1 = (PlayerEntity)harness.Zone.Entities.Get(1)!;
            PlayerEntity p2 = (PlayerEntity)harness.Zone.Entities.Get(2)!;
            p1.Position = new Vec2(-7f, 5f);
            p2.Position = new Vec2(-6f, 5f);
            SendSnapshots(harness);

            SnapshotMessage s1 = SnapshotFor(harness, 1);
            SnapshotMessage s2 = SnapshotFor(harness, 2);

            Assert.Single(s1.Entities);
            Assert.Equal(1, s1.Entities[0].Id);
            Assert.Single(s2.Entities);
            Assert.Equal(2, s2.Entities[0].Id);
        }

        [Fact]
        public void El_Area_De_Interes_Cuenta_Entidades_Y_Bytes_En_Las_Metricas()
        {
            using var harness = new Harness(config =>
            {
                config.InterestRadius = 2f;
                config.InterestCellSize = 2f;
                config.InterestHysteresis = 0.5f;
            });

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();
            harness.Connect(2);
            harness.Handshake(2);
            harness.Pump();

            PlayerEntity p1 = (PlayerEntity)harness.Zone.Entities.Get(1)!;
            PlayerEntity p2 = (PlayerEntity)harness.Zone.Entities.Get(2)!;
            p1.Position = new Vec2(-7f, 5f);
            p2.Position = new Vec2(7f, -5f);
            SendSnapshots(harness);

            NetworkStats stats = harness.Server.Snapshot();

            Assert.Equal(2, stats.SnapshotsSent);
            Assert.Equal(2, stats.EntitiesSent);
            Assert.True(stats.SnapshotBytes > 0);
            Assert.Equal(0, stats.SnapshotsTruncated);
        }

        [Fact]
        public void CombatEvent_Se_Agrupa_Con_El_Snapshot_Y_Solo_Llega_A_Los_Interesados()
        {
            using var harness = new Harness(config =>
            {
                config.SnapshotEveryTicks = 2;
                config.InterestRadius = 12f;
                config.InterestHysteresis = 0f;
            });
            for (int connection = 1; connection <= 3; connection++)
            {
                harness.Connect(connection);
                harness.Handshake(connection);
                harness.Pump();
            }

            PlayerEntity attacker = (PlayerEntity)harness.Zone.Entities.Get(1)!;
            PlayerEntity nearby = (PlayerEntity)harness.Zone.Entities.Get(2)!;
            PlayerEntity far = (PlayerEntity)harness.Zone.Entities.Get(3)!;
            attacker.Position = new Vec2(-7f, 5f);
            nearby.Position = new Vec2(-6f, 5f);
            far.Position = new Vec2(18f, 18f);
            var npc = new NpcEntity(100, 2001, 0f, attacker.Position, 0f, long.MaxValue, 100, 200, -1)
            {
                Position = attacker.Position,
                HalfSize = new Vec2(0.35f, 0.35f),
            };
            harness.Zone.AddEntity(npc);
            harness.Transport.Sent.Clear();
            harness.Zone.Attacks!.TryEnqueue(new AttackIntent(attacker.Id, npc.Id));

            harness.Zone.Tick();
            harness.Server.AfterTick(harness.Zone);
            harness.Server.PollOnce();
            Assert.Equal(1, harness.Zone.CombatEvents.Count);
            Assert.DoesNotContain(harness.Transport.Sent, sent =>
                MessageCodec.TryReadCombatEvent(sent.Data, 0, sent.Data.Length, out _));

            harness.Zone.Tick();
            harness.Server.AfterTick(harness.Zone);
            harness.Server.PollOnce();

            for (int connection = 1; connection <= 3; connection++)
            {
                SentMessage[] messages = harness.Transport.Sent.FindAll(sent =>
                    sent.ConnectionId == connection &&
                    MessageCodec.TryReadCombatEvent(sent.Data, 0, sent.Data.Length, out _)).ToArray();
                if (connection == 3)
                {
                    Assert.Empty(messages);
                    continue;
                }

                SentMessage sentEvent = Assert.Single(messages);
                Assert.Equal(DeliveryMode.Unreliable, sentEvent.Mode);
                Assert.True(MessageCodec.TryReadCombatEvent(
                    sentEvent.Data, 0, sentEvent.Data.Length, out CombatEventMessage decoded));
                CombatEventEntry entry = Assert.Single(decoded.Entries);
                Assert.Equal(attacker.Id, entry.AttackerId);
                Assert.Equal(npc.Id, entry.TargetId);
            }

            Assert.Equal(0, harness.Zone.CombatEvents.Count);
            NetworkStats stats = harness.Server.Snapshot();
            Assert.Equal(2, stats.CombatEventMessages);
            Assert.Equal(2, stats.CombatEventEntries);
            Assert.Equal(26, stats.CombatEventBytes);
            Assert.Equal(0, stats.CombatEventsTrimmed);

            harness.Transport.Sent.Clear();
            harness.Zone.Tick();
            harness.Server.AfterTick(harness.Zone);
            harness.Server.PollOnce();
            harness.Zone.Tick();
            harness.Server.AfterTick(harness.Zone);
            harness.Server.PollOnce();
            Assert.DoesNotContain(harness.Transport.Sent, sent =>
                MessageCodec.TryReadCombatEvent(sent.Data, 0, sent.Data.Length, out _));
        }

        [Fact]
        public void Sin_Sesiones_AfterTick_Vacia_Los_CombatEvents()
        {
            using var harness = new Harness();
            PlayerEntity attacker = harness.Zone.SpawnPlayer(
                1, harness.Config.Spawn, harness.Config.PlayerHalfSize, harness.Config.InputBufferCapacity);
            var npc = new NpcEntity(100, 2001, 0f, attacker.Position, 0f, long.MaxValue, 100, 200, -1)
            {
                Position = attacker.Position,
                HalfSize = new Vec2(0.35f, 0.35f),
            };
            harness.Zone.AddEntity(npc);
            harness.Zone.Attacks!.TryEnqueue(new AttackIntent(attacker.Id, npc.Id));
            harness.Zone.Tick();
            Assert.Equal(1, harness.Zone.CombatEvents.Count);

            harness.Server.AfterTick(harness.Zone);

            Assert.Equal(0, harness.Zone.CombatEvents.Count);
        }

        [Fact]
        public void Los_Bytes_Recibidos_Cuentan_La_Carga_Util()
        {
            using var harness = new Harness();

            harness.Connect(1);

            var hello = new HelloMessage(
                ProtocolConstants.Version,
                (ushort)SimulationConstants.TickRate,
                harness.MapHash,
                4242u + 1u);

            byte[] helloBytes = MessageCodec.EncodeHello(hello);
            harness.Transport.RaiseReceived(1, helloBytes);
            harness.Pump();

            Assert.Equal(helloBytes.Length, harness.Server.Snapshot().BytesReceived);

            // Un input válido suma su datagrama…
            byte[] input = MessageCodec.EncodeMoveInput(new MoveInputMessage(1u, 1, 0));
            harness.Transport.RaiseReceived(1, input);
            Assert.Equal(helloBytes.Length + input.Length, harness.Server.Snapshot().BytesReceived);

            // …y uno rechazado también: el coste de recibirlo ya lo pagó la red.
            byte[] garbage = { 0x7F, 0x01, 0x02 };
            harness.Transport.RaiseReceived(1, garbage);
            Assert.Equal(
                helloBytes.Length + input.Length + garbage.Length,
                harness.Server.Snapshot().BytesReceived);
        }

        [Fact]
        public void El_Limite_De_Mensajes_Por_Segundo_Descarta_El_Exceso()
        {
            using var harness = new Harness(config => config.MaxMessagesPerSecondPerConnection = 20);

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            WaitForNewSecond();

            for (int i = 0; i < 40; i++)
            {
                harness.Transport.RaiseReceived(1, MessageCodec.EncodeMoveInput(new MoveInputMessage((uint)(i + 1), 1, 0)));
            }

            Assert.True(
                harness.Server.Snapshot().RateLimited > 0,
                "Un cliente que manda más de lo permitido debe ver mensajes descartados.");

            // Descartar no es cortar: un pico por jitter no debe echar a nadie.
            Assert.Equal(0, harness.Server.Snapshot().RateLimitedDisconnects);
            Assert.Empty(harness.Transport.DisconnectedConnections);
        }

        [Fact]
        public void Insistir_Por_Encima_Del_Limite_Desconecta()
        {
            using var harness = new Harness(config =>
            {
                config.MaxMessagesPerSecondPerConnection = 20;
                config.RejectDisconnectDelayMilliseconds = 0;
            });

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            WaitForNewSecond();

            // 20 aceptados + 40 descartados: el 61.º ya es tres veces el tope.
            for (int i = 0; i < 61; i++)
            {
                harness.Transport.RaiseReceived(1, MessageCodec.EncodeMoveInput(new MoveInputMessage((uint)(i + 1), 1, 0)));
            }

            Assert.Equal(1, harness.Server.Snapshot().RateLimitedDisconnects);

            harness.Server.PollOnce();
            Assert.Contains(1, harness.Transport.DisconnectedConnections);

            SentMessage last = harness.Transport.Sent[harness.Transport.Sent.Count - 1];
            Assert.True(MessageCodec.TryReadReject(last.Data, 0, last.Data.Length, out RejectMessage reject));
            Assert.Equal(RejectReason.RateLimited, reject.Reason);
        }

        [Fact]
        public void Inundar_Durante_Cinco_Segundos_Desconecta_Y_Cuatro_No()
        {
            // Superar el tope de forma sostenida no puede dejar la sesión abierta para siempre.
            Assert.False(FloodForSeconds(4), "Cuatro segundos por encima del tope no deben cortar la sesión.");
            Assert.True(FloodForSeconds(5), "Cinco segundos por encima del tope deben cortar la sesión.");
        }

        /// <summary>
        /// Simula <paramref name="seconds"/> segundos mandando el límite + 1 mensajes por segundo.
        /// Devuelve si el servidor desconectó por inundación sostenida.
        /// </summary>
        private static bool FloodForSeconds(int seconds)
        {
            // Un minuto del reloj inyectado lejos de un cambio de minuto, para que la ventana no
            // se reinicie a mitad de la prueba.
            long now = 1_000_000;

            using var harness = new Harness(
                config =>
                {
                    config.MaxMessagesPerSecondPerConnection = 5;
                    config.MaxSecondsOverMessageLimit = 5;
                    config.RejectDisconnectDelayMilliseconds = 0;
                },
                () => now);

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            uint sequence = 0;

            for (int second = 0; second < seconds; second++)
            {
                for (int i = 0; i < 6; i++)
                {
                    sequence++;
                    harness.Transport.RaiseReceived(
                        1,
                        MessageCodec.EncodeMoveInput(new MoveInputMessage(sequence, 1, 0)));
                }

                if (harness.Server.Snapshot().RateLimitedDisconnects > 0)
                {
                    return true;
                }

                now += 1000;
            }

            return harness.Server.Snapshot().RateLimitedDisconnects > 0;
        }

        [Fact]
        public void Un_Input_Con_Redundancia_Encola_Solo_Los_Nuevos_Y_Cuenta_Las_Copias()
        {
            using var harness = new Harness();

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            SendMoveInput(harness, 1, new MoveInputMessage(1u, 1, 0));
            harness.Zone.Tick();
            Assert.Equal(1u, harness.Zone.Players[0].LastProcessedSequence);

            // n = 4 con base en la secuencia 4: la 1 ya estaba y las nuevas son 2, 3 y 4.
            SendMoveInput(harness, 1, new MoveInputMessage(4u, 4, 1, 0, 1, 0, 1, 0, 1, 0));
            harness.Zone.Tick();

            Assert.Equal(5, harness.Zone.Inputs.Received);
            Assert.Equal(1, harness.Zone.RedundantDuplicates);
            Assert.Equal(0, harness.Zone.RejectedOutOfOrder);
            Assert.Equal(4u, ((PlayerEntity)harness.Zone.Entities.Get(1)!).Input.LastAcceptedSequence);

            // Un input por tick: el primero se aplica ya y los demás esperan.
            Assert.Equal(2u, harness.Zone.Players[0].LastProcessedSequence);
            harness.Zone.Tick();
            harness.Zone.Tick();
            Assert.Equal(4u, harness.Zone.Players[0].LastProcessedSequence);
        }

        [Fact]
        public void Si_Se_Pierde_Un_Mensaje_El_Siguiente_Rellena_El_Hueco()
        {
            using var harness = new Harness();

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            SendMoveInput(harness, 1, new MoveInputMessage(1u, 1, 0));
            harness.Zone.Tick();
            Assert.Equal(1u, harness.Zone.Players[0].LastProcessedSequence);

            // El mensaje de la secuencia 2 se pierde. El 3 lleva la 2 (más vieja) y la 3 (nueva),
            // en ese orden: el servidor debe aplicar las dos, una por tick.
            SendMoveInput(harness, 1, new MoveInputMessage(3u, 2, 1, 0, 0, 1, 0, 0, 0, 0));

            harness.Zone.Tick();
            Assert.Equal(2u, harness.Zone.Players[0].LastProcessedSequence);

            harness.Zone.Tick();
            Assert.Equal(3u, harness.Zone.Players[0].LastProcessedSequence);

            Assert.Equal(0, harness.Zone.RedundantDuplicates);
            Assert.Equal(0, harness.Zone.RejectedOutOfOrder);
            Assert.Equal(0, harness.Zone.RejectedPerPlayerLimit);
        }

        [Fact]
        public void Las_Copias_Redundantes_No_Gastan_El_Tope_Por_Jugador()
        {
            using var harness = new Harness(config => config.MaxIntentsPerPlayerPerTick = 2);

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            SendMoveInput(harness, 1, new MoveInputMessage(1u, 1, 0));
            harness.Zone.Tick();

            SendMoveInput(harness, 1, new MoveInputMessage(4u, 4, 1, 0, 1, 0, 1, 0, 1, 0));
            harness.Zone.Tick();

            // La 1 es una copia y no gasta plaza; 2 y 3 se aceptan; 4 topa. Si la copia gastara
            // tope, solo cabría una nueva y el tope subiría a dos.
            Assert.Equal(1, harness.Zone.RedundantDuplicates);
            Assert.Equal(1, harness.Zone.RejectedPerPlayerLimit);
            Assert.Equal(0, harness.Zone.RejectedOutOfOrder);
            Assert.Equal(3u, ((PlayerEntity)harness.Zone.Entities.Get(1)!).Input.LastAcceptedSequence);
        }

        private static void SendMoveInput(Harness harness, int connectionId, in MoveInputMessage message)
        {
            byte[] bytes = MessageCodec.EncodeMoveInput(message);
            harness.Transport.RaiseReceived(connectionId, bytes);
        }

        /// <summary>
        /// Espera a empezar un segundo nuevo: el contador de frecuencia se reinicia cada segundo y
        /// una ráfaga a caballo entre dos falsearía la prueba.
        /// </summary>
        private static void WaitForNewSecond()
        {
            long second = Environment.TickCount64 / 1000;

            while (Environment.TickCount64 / 1000 == second)
            {
                System.Threading.Thread.Sleep(1);
            }
        }

        /// <summary>Simula un tick y entrega los snapshots que genere la red.</summary>
        private static void SendSnapshots(Harness harness)
        {
            harness.Zone.Tick();
            harness.Transport.Sent.Clear();
            harness.Server.AfterTick(harness.Zone);
            harness.Server.PollOnce();
        }

        /// <summary>Busca el snapshot que recibió una conexión concreta.</summary>
        private static SnapshotMessage SnapshotFor(Harness harness, int connectionId)
        {
            for (int i = 0; i < harness.Transport.Sent.Count; i++)
            {
                SentMessage sent = harness.Transport.Sent[i];

                if (sent.ConnectionId == connectionId &&
                    MessageCodec.TryReadSnapshot(sent.Data, 0, sent.Data.Length, out SnapshotMessage snapshot))
                {
                    return snapshot;
                }
            }

            throw new InvalidOperationException("La conexión " + connectionId + " no recibió ningún snapshot.");
        }

        [Fact]
        public void Fallo_Al_Crear_Rechaza_Y_Permite_Otro_Mensaje()
        {
            using var repository = new FailingCreateRepository();
            using var saves = new CharacterSaveService(repository);
            using var harness = new Harness(repository: repository, saves: saves);
            harness.Connect(1);
            harness.Handshake(1, automaticallySelect: false);
            harness.WaitForCharacterList(1);
            harness.Transport.Sent.Clear();
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeCreateCharacter(new CreateCharacterMessage("Ana", 1)));

            long deadline = Environment.TickCount64 + 5_000;
            while (harness.Transport.Sent.Count == 0 && Environment.TickCount64 < deadline)
            {
                harness.Server.PollOnce();
                Thread.Sleep(1);
            }

            Assert.Contains(harness.Transport.Sent, sent =>
                MessageCodec.TryReadCharacterRejected(sent.Data, 0, sent.Data.Length, out CharacterRejectedMessage rejected) &&
                rejected.Reason == RejectReason.InternalError);
            Assert.Empty(harness.Transport.DisconnectedConnections);
            harness.Transport.Sent.Clear();
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeCreateCharacter(new CreateCharacterMessage("Ana", 255)));
            harness.Server.PollOnce();
            Assert.Contains(harness.Transport.Sent, sent =>
                MessageCodec.TryReadCharacterRejected(sent.Data, 0, sent.Data.Length, out CharacterRejectedMessage rejected) &&
                rejected.Reason == RejectReason.ClassUnknown);
            Assert.Empty(harness.Transport.DisconnectedConnections);
        }

        [Fact]
        public void Borrar_Antes_De_Procesar_La_Expulsion_No_Resucita_El_Personaje()
        {
            using var harness = new Harness();
            harness.Connect(1);
            harness.Handshake(1, characterName: "Ana", accountNameOverride: "compartida");
            harness.Pump();
            Assert.Single(harness.Zone.Entities.All);
            harness.Connect(2);
            harness.Handshake(2, automaticallySelect: false, accountNameOverride: "compartida");
            CharacterListEntry character = Assert.Single(harness.WaitForCharacterList(2).Characters);
            harness.Transport.Sent.Clear();
            harness.Transport.RaiseReceived(2, MessageCodec.EncodeDeleteCharacter(
                new DeleteCharacterMessage(character.CharacterId, character.Name)));
            Assert.Empty(harness.WaitForCharacterList(2).Characters);
            Assert.Single(harness.Zone.Entities.All);

            harness.Pump();

            Assert.Empty(harness.Zone.Entities.All);
            harness.Connect(3);
            harness.Handshake(3, automaticallySelect: false, accountNameOverride: "compartida");
            Assert.Empty(harness.WaitForCharacterList(3).Characters);
            harness.Transport.Sent.Clear();
            harness.Transport.RaiseReceived(3, MessageCodec.EncodeSelectCharacter(new SelectCharacterMessage(character.CharacterId)));
            harness.Server.PollOnce();
            Assert.Contains(harness.Transport.Sent, sent =>
                MessageCodec.TryReadCharacterRejected(sent.Data, 0, sent.Data.Length, out CharacterRejectedMessage rejected) &&
                rejected.Reason == RejectReason.CharacterNotFound);
        }

        [Fact]
        public void Limite_Reducido_Permite_Listar_Seleccionar_Y_Borrar_Pero_No_Crear()
        {
            using var harness = new Harness();
            harness.Connect(1);
            harness.Handshake(1, automaticallySelect: false, accountNameOverride: "compartida");
            harness.WaitForCharacterList(1);
            foreach (string name in new[] { "Ana", "Juan", "Luis" })
            {
                harness.Transport.Sent.Clear();
                harness.Transport.RaiseReceived(1, MessageCodec.EncodeCreateCharacter(new CreateCharacterMessage(name, 1)));
                harness.WaitForCharacterList(1);
            }

            harness.Config.MaxCharactersPerAccount = 2;
            harness.Connect(2);
            harness.Handshake(2, automaticallySelect: false, accountNameOverride: "compartida");
            CharacterListMessage list = harness.WaitForCharacterList(2);
            Assert.Equal(2, list.MaxCharacters);
            Assert.Equal(3, list.Characters.Length);
            harness.Transport.Sent.Clear();
            harness.Transport.RaiseReceived(2, MessageCodec.EncodeCreateCharacter(new CreateCharacterMessage("Pedro", 1)));
            harness.Server.PollOnce();
            Assert.Contains(harness.Transport.Sent, sent =>
                sent.ConnectionId == 2 &&
                MessageCodec.TryReadCharacterRejected(sent.Data, 0, sent.Data.Length, out CharacterRejectedMessage rejected) &&
                rejected.Reason == RejectReason.CharacterLimit);
            harness.Transport.RaiseReceived(2, MessageCodec.EncodeSelectCharacter(
                new SelectCharacterMessage(list.Characters[2].CharacterId)));
            harness.Pump();
            Assert.Contains(harness.Transport.Sent, sent => sent.ConnectionId == 2 &&
                MessageCodec.TryReadWelcome(sent.Data, 0, sent.Data.Length, out _));

            harness.Connect(3);
            harness.Handshake(3, automaticallySelect: false, accountNameOverride: "compartida");
            Assert.Equal(3, harness.WaitForCharacterList(3).Characters.Length);
            harness.Pump();
            for (int i = 2; i >= 0; i--)
            {
                harness.Transport.Sent.Clear();
                harness.Transport.RaiseReceived(3, MessageCodec.EncodeDeleteCharacter(
                    new DeleteCharacterMessage(list.Characters[i].CharacterId, list.Characters[i].Name)));
                Assert.Equal(i, harness.WaitForCharacterList(3).Characters.Length);
            }
        }

        private sealed class FailingCreateRepository : IPlayerRepository
        {
            public void Initialize() { }
            public AccountLoginResult Login(string accountName, string password, string invitationCode, string registrationCode) =>
                AccountLoginResult.Ok(1);
            public CharacterCreateResult CreateCharacter(int accountId, string name, byte classId,
                int maxCharacters, int zoneId, Vec2 spawn, byte[] mapHash) =>
                throw new InvalidOperationException("Fallo de creación simulado.");
            public void SaveCharacter(CharacterRecord character) { }
            public void Dispose() { }
        }

        private sealed class FixedCharacterRepository : IPlayerRepository
        {
            private readonly CharacterRecord _character;

            public FixedCharacterRepository(CharacterRecord character) => _character = character;

            public void Initialize() { }

            public AccountLoginResult Login(string accountName, string password, string invitationCode, string registrationCode) =>
                AccountLoginResult.Ok(_character.AccountId);

            public CharacterRecord[] ListCharacters(int accountId) =>
                accountId == _character.AccountId ? new[] { _character } : Array.Empty<CharacterRecord>();

            public bool TryGetCharacter(int accountId, int characterId, out CharacterRecord? character)
            {
                character = accountId == _character.AccountId && characterId == _character.Id ? _character : null;
                return character != null;
            }

            public void SaveCharacter(CharacterRecord character) { }

            public void Dispose() { }
        }

        private sealed class BlockingSelectRepository : IPlayerRepository
        {
            private readonly CharacterRecord _character = new CharacterRecord(
                1,
                1,
                "Jugador",
                1,
                0,
                100,
                100,
                1,
                new Vec2(-1.5f, -2.5f),
                new byte[ProtocolConstants.MapHashSize],
                DateTime.UtcNow,
                DateTime.UtcNow);

            public ManualResetEventSlim SelectionStarted { get; } = new ManualResetEventSlim(false);
            public ManualResetEventSlim SelectionGate { get; } = new ManualResetEventSlim(false);
            public ManualResetEventSlim SelectionFinished { get; } = new ManualResetEventSlim(false);

            public void Initialize()
            {
            }

            public AccountLoginResult Login(
                string accountName,
                string password,
                string invitationCode,
                string registrationCode) => AccountLoginResult.Ok(1);

            public CharacterRecord[] ListCharacters(int accountId) =>
                accountId == _character.AccountId ? new[] { _character } : Array.Empty<CharacterRecord>();

            public bool TryGetCharacter(int accountId, int characterId, out CharacterRecord? character)
            {
                SelectionStarted.Set();
                SelectionGate.Wait(TimeSpan.FromSeconds(10));
                SelectionFinished.Set();
                character = accountId == _character.AccountId && characterId == _character.Id
                    ? _character
                    : null;
                return character != null;
            }

            public void SaveCharacter(CharacterRecord character)
            {
            }

            public void Dispose()
            {
                SelectionStarted.Dispose();
                SelectionGate.Dispose();
                SelectionFinished.Dispose();
            }
        }

        private sealed class BlockingLoginRepository : IPlayerRepository
        {
            private readonly CharacterRecord _character = new CharacterRecord(
                1,
                1,
                "Jugador",
                1,
                0,
                100,
                100,
                1,
                new Vec2(-1.5f, -2.5f),
                new byte[ProtocolConstants.MapHashSize],
                DateTime.UtcNow,
                DateTime.UtcNow);

            public ManualResetEventSlim BlockingLoginStarted { get; } = new ManualResetEventSlim(false);
            public ManualResetEventSlim BlockingLoginGate { get; } = new ManualResetEventSlim(false);
            public ManualResetEventSlim BlockingLoginFinished { get; } = new ManualResetEventSlim(false);

            public void Initialize()
            {
            }

            public AccountLoginResult Login(
                string accountName,
                string password,
                string invitationCode,
                string registrationCode)
            {
                if (accountName == "bloqueo")
                {
                    BlockingLoginStarted.Set();
                    BlockingLoginGate.Wait(TimeSpan.FromSeconds(10));
                    BlockingLoginFinished.Set();
                    return AccountLoginResult.Ok(2);
                }

                return AccountLoginResult.Ok(1);
            }

            public CharacterRecord[] ListCharacters(int accountId) =>
                accountId == _character.AccountId ? new[] { _character } : Array.Empty<CharacterRecord>();

            public bool TryGetCharacter(int accountId, int characterId, out CharacterRecord? character)
            {
                character = accountId == _character.AccountId && characterId == _character.Id ? _character : null;
                return character != null;
            }

            public void SaveCharacter(CharacterRecord character)
            {
            }

            public void Dispose()
            {
                BlockingLoginStarted.Dispose();
                BlockingLoginGate.Dispose();
                BlockingLoginFinished.Dispose();
            }
        }

        /// <summary>Transporte en memoria: los eventos los dispara la prueba, no un socket.</summary>
        private sealed class FakeServerTransport : IServerTransport
        {
            public List<SentMessage> Sent { get; } = new List<SentMessage>();

            public List<int> DisconnectedConnections { get; } = new List<int>();

            public int Port => 0;

            public int ConnectionCount => 0;

            public event TransportConnectedHandler? Connected;

            public event TransportDisconnectedHandler? Disconnected;

            public event TransportMessageHandler? Received;

            public void Start(int port)
            {
            }

            public void Stop()
            {
            }

            public void Poll()
            {
            }

            public void Send(int connectionId, byte[] data, int length, DeliveryMode mode)
            {
                var copy = new byte[length];
                Array.Copy(data, copy, length);
                Sent.Add(new SentMessage(connectionId, copy, mode));
            }

            public void Disconnect(int connectionId, DisconnectReason reason) => DisconnectedConnections.Add(connectionId);

            public void Dispose()
            {
            }

            public void RaiseConnected(int connectionId) => Connected?.Invoke(connectionId);

            public void RaiseDisconnected(int connectionId) =>
                Disconnected?.Invoke(connectionId, DisconnectReason.RemoteClosed);

            public void RaiseReceived(int connectionId, byte[] data) =>
                Received?.Invoke(connectionId, data, 0, data.Length);
        }

        private sealed class TemporaryFolder : IDisposable
        {
            public TemporaryFolder()
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "avelora-i09-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public void Dispose()
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch (IOException)
                {
                    // SQLite podría tardar un instante en liberar el archivo en Windows.
                }
            }
        }

        /// <summary>Un mensaje tal como lo entregó el transporte falso.</summary>
        private readonly struct SentMessage
        {
            public SentMessage(int connectionId, byte[] data, DeliveryMode mode)
            {
                ConnectionId = connectionId;
                Data = data;
                Mode = mode;
            }

            public int ConnectionId { get; }

            public byte[] Data { get; }

            public DeliveryMode Mode { get; }
        }
    }
}
