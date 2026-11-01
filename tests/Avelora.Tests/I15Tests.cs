using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avelora.Server.Entities;
using Avelora.Server.Npcs;
using Avelora.Server.Items;
using Avelora.Server.Progression;
using Avelora.Server.Simulation;
using Avelora.Server.Zones;
using Avelora.Shared;
using Avelora.Shared.Maps;
using Avelora.Shared.Protocol;
using Xunit;

namespace Avelora.Tests
{
    public sealed class I15Tests
    {
        private static ZoneDefinition Definition(int id, string kind, params ZoneRectDefinition[] rects) => new ZoneDefinition
        {
            Id = id,
            Name = "Zona de prueba " + id,
            Kind = kind,
            Rects = rects.ToList(),
        };

        private static ZoneRectDefinition Rect(int x, int y, int w = 1, int h = 1) =>
            new ZoneRectDefinition { X = x, Y = y, W = w, H = h };

        private static ZoneMap SafeZone(MapCollisionGrid map, int x = 10, int y = 4, int w = 4, int h = 4) =>
            ZoneMap.FromDefinitions(new[] { Definition(1, "safe", Rect(x, y, w, h)) }, map);

        private static Zone NewZone(
            ZoneMap? zones = null,
            NpcCatalog? npcs = null,
            PlayerVitalsSettings? vitals = null,
            MapCollisionGrid? map = null,
            ItemCatalog? items = null) => new Zone(
                1,
                map ?? MapCollisionGrid.AllWalkable(30, 20),
                new EntityStore(),
                new EntityIdAllocator(),
                new InputQueue(64),
                new MovementSystem(4, SimulationConstants.FixedDt),
                128,
                4,
                new InterestSettings(20, 8, 0),
                npcs,
                npcSeed: 17,
                attacks: new AttackQueue(64),
                combat: new CombatSettings(1),
                playerVitals: vitals,
                items: items,
                classes: ClassCatalog.Default,
                zones: zones);

        private static NpcCatalog NpcData(float x, float y, float speed, float aggroRadius, float wanderRadius) => new NpcCatalog
        {
            Types = new List<NpcType>
            {
                new NpcType
                {
                    Id = 2001,
                    Name = "Tipo de prueba",
                    Speed = speed,
                    HalfWidth = 0.3f,
                    HalfHeight = 0.3f,
                    MaxHealth = 100,
                    Experience = 20,
                    Level = 1,
                    RespawnSeconds = 20,
                    AggroRadius = aggroRadius,
                    LeashRadius = 20,
                    AttackDamage = 1,
                    AttackRange = 1,
                    AttackCooldownSeconds = 1,
                    Ranks = false,
                    Loot = new List<LootEntry>
                    {
                        new LootEntry { ItemId = 3001, Chance = 1, Min = 1, Max = 1 },
                    },
                },
            },
            Spawners = new List<NpcSpawner>
            {
                new NpcSpawner { TypeId = 2001, X = x, Y = y, Count = 1, WanderRadius = wanderRadius },
            },
        };

        private static void AssertZonesError(Action action, int zoneId)
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(action);
            Assert.StartsWith("zones.json: ", error.Message);
            Assert.Contains("zona " + zoneId, error.Message);
        }

        [Fact]
        public void ZoneMap_Empty_Y_Sin_Definiciones_Son_Normal()
        {
            MapCollisionGrid map = MapCollisionGrid.AllWalkable(20, 20);
            ZoneMap empty = ZoneMap.FromDefinitions(Array.Empty<ZoneDefinition>(), map);

            foreach (Vec2 point in new[] { new Vec2(0, 0), new Vec2(4.5f, 8.5f), new Vec2(-10, 100) })
            {
                Assert.Equal((ushort)0, ZoneMap.Empty.ZoneIdAt(point.X, point.Y));
                Assert.Equal(ZoneKind.Normal, ZoneMap.Empty.KindAt(point.X, point.Y));
                Assert.Equal((ushort)0, empty.ZoneIdAt(point.X, point.Y));
                Assert.Equal(ZoneKind.Normal, empty.KindAt(point.X, point.Y));
            }
        }

        [Fact]
        public void ZoneMap_Respeta_Bordes_Inferiores_Y_Superiores_De_Cada_Casilla()
        {
            ZoneMap zones = ZoneMap.FromDefinitions(new[]
            {
                Definition(4, "safe", Rect(2, 3, 4, 2)),
            }, MapCollisionGrid.AllWalkable(20, 20));

            Assert.Equal(ZoneKind.Safe, zones.KindAt(2f, 3f));
            Assert.Equal(ZoneKind.Safe, zones.KindAt(5.999f, 4.999f));
            Assert.Equal((ushort)4, zones.ZoneIdAt(5.999f, 4.999f));
            Assert.Equal(ZoneKind.Normal, zones.KindAt(6f, 4f));
            Assert.Equal(ZoneKind.Normal, zones.KindAt(1.999f, 3.5f));
            Assert.Equal(ZoneKind.Normal, zones.KindAt(2.5f, 5f));
        }

        [Fact]
        public void ZoneMap_Solapes_Priorizan_Safe_Y_Con_Igual_Prioridad_Gana_El_Primero()
        {
            ZoneMap zones = ZoneMap.FromDefinitions(new[]
            {
                Definition(1, "pvp", Rect(2, 2, 4, 2)),
                Definition(2, "safe", Rect(3, 2, 2, 2)),
                Definition(3, "pvp", Rect(8, 2, 3, 2)),
                Definition(4, "pvp", Rect(9, 2, 3, 2)),
            }, MapCollisionGrid.AllWalkable(20, 20));

            Assert.Equal((ushort)2, zones.ZoneIdAt(3.5f, 2.5f));
            Assert.Equal(ZoneKind.Safe, zones.KindAt(3.5f, 2.5f));
            Assert.Equal((ushort)1, zones.ZoneIdAt(2.5f, 2.5f));
            Assert.Equal((ushort)3, zones.ZoneIdAt(8.5f, 2.5f));
            Assert.Equal((ushort)3, zones.ZoneIdAt(9.5f, 2.5f));
            Assert.Equal(ZoneKind.Pvp, zones.KindAt(11.5f, 2.5f));
        }

        [Fact]
        public void ZoneMap_Consulta_Usa_Origen_Negativo_Y_Fuera_Del_Mapa_Es_Normal()
        {
            var map = new MapCollisionGrid(20, 20, new bool[400], -10, -10);
            ZoneMap zones = ZoneMap.FromDefinitions(new[]
            {
                Definition(5, "safe", Rect(-10, -10, 2, 2)),
            }, map);

            Assert.Equal(ZoneKind.Safe, zones.KindAt(-10f, -10f));
            Assert.Equal((ushort)5, zones.ZoneIdAt(-9.1f, -9.1f));
            Assert.Equal(ZoneKind.Normal, zones.KindAt(-11f, -10f));
            Assert.Equal(ZoneKind.Normal, zones.KindAt(10f, 10f));
        }

        [Fact]
        public void ZoneMap_Valida_Id_Repetido_Nombre_Y_Tipo()
        {
            MapCollisionGrid map = MapCollisionGrid.AllWalkable(20, 20);
            AssertZonesError(() => ZoneMap.FromDefinitions(new[] { Definition(0, "safe", Rect(1, 1)) }, map), 0);
            AssertZonesError(() => ZoneMap.FromDefinitions(new[] { Definition(-1, "safe", Rect(1, 1)) }, map), -1);
            AssertZonesError(() => ZoneMap.FromDefinitions(new[] { Definition(65536, "safe", Rect(1, 1)) }, map), 65536);
            AssertZonesError(() => ZoneMap.FromDefinitions(new[]
            {
                Definition(2, "safe", Rect(1, 1)), Definition(2, "pvp", Rect(2, 2)),
            }, map), 2);

            ZoneDefinition blankName = Definition(3, "safe", Rect(1, 1));
            blankName.Name = "  ";
            AssertZonesError(() => ZoneMap.FromDefinitions(new[] { blankName }, map), 3);
            AssertZonesError(() => ZoneMap.FromDefinitions(new[] { Definition(4, "normal", Rect(1, 1)) }, map), 4);
            AssertZonesError(() => ZoneMap.FromDefinitions(new[] { Definition(5, "Safe", Rect(1, 1)) }, map), 5);
        }

        [Fact]
        public void ZoneMap_Valida_Limites_De_Cantidad_De_Zonas_Y_Rectangulos()
        {
            MapCollisionGrid map = MapCollisionGrid.AllWalkable(20, 20);
            AssertZonesError(() => ZoneMap.FromDefinitions(new[] { Definition(1, "safe") }, map), 1);

            var tooManyRects = new ZoneRectDefinition[17];
            for (int i = 0; i < tooManyRects.Length; i++) tooManyRects[i] = Rect(0, 0);
            AssertZonesError(() => ZoneMap.FromDefinitions(new[] { Definition(2, "safe", tooManyRects) }, map), 2);

            var tooManyZones = new ZoneDefinition[ProtocolConstants.MaxZones + 1];
            for (int i = 0; i < tooManyZones.Length; i++) tooManyZones[i] = Definition(i + 1, "safe", Rect(0, 0));
            InvalidOperationException manyZones = Assert.Throws<InvalidOperationException>(() =>
                ZoneMap.FromDefinitions(tooManyZones, map));
            Assert.StartsWith("zones.json: ", manyZones.Message);
            Assert.Contains("zona 33", manyZones.Message);

            var tooManyTotal = new ZoneDefinition[9];
            for (int i = 0; i < 8; i++)
            {
                var rects = new ZoneRectDefinition[16];
                for (int r = 0; r < rects.Length; r++) rects[r] = Rect(0, 0);
                tooManyTotal[i] = Definition(i + 1, "safe", rects);
            }
            tooManyTotal[8] = Definition(9, "safe", Rect(0, 0));
            AssertZonesError(() => ZoneMap.FromDefinitions(tooManyTotal, map), 9);
        }

        [Fact]
        public void ZoneMap_Rechaza_Rectangulos_Vacios_No_Positive_Y_Fuera_De_Cada_Borde()
        {
            var map = new MapCollisionGrid(10, 10, new bool[100], -5, -5);
            AssertZonesError(() => ZoneMap.FromDefinitions(new[] { Definition(1, "safe", Rect(0, 0, 0, 1)) }, map), 1);
            AssertZonesError(() => ZoneMap.FromDefinitions(new[] { Definition(2, "safe", Rect(0, 0, 1, 0)) }, map), 2);
            AssertZonesError(() => ZoneMap.FromDefinitions(new[] { Definition(3, "safe", Rect(-6, 0)) }, map), 3);
            AssertZonesError(() => ZoneMap.FromDefinitions(new[] { Definition(4, "safe", Rect(0, -6)) }, map), 4);
            AssertZonesError(() => ZoneMap.FromDefinitions(new[] { Definition(5, "safe", Rect(4, 0, 2, 1)) }, map), 5);
            AssertZonesError(() => ZoneMap.FromDefinitions(new[] { Definition(6, "safe", Rect(0, 4, 1, 2)) }, map), 6);
            AssertZonesError(() => ZoneMap.FromDefinitions(new[] { Definition(7, "safe", Rect(0, 0, 32768, 1)) }, map), 7);
            AssertZonesError(() => ZoneMap.FromDefinitions(new[] { Definition(8, "safe", Rect(32768, 0)) }, map), 8);
            AssertZonesError(() => ZoneMap.FromDefinitions(new[] { Definition(9, "safe", Rect(0, -32769)) }, map), 9);
        }

        [Fact]
        public void ZoneMap_Load_Valida_Archivo_Inexistente_Y_JSON_Nulo()
        {
            MapCollisionGrid map = MapCollisionGrid.AllWalkable(10, 10);
            Assert.Throws<ArgumentException>(() => ZoneMap.Load(" ", map));
            FileNotFoundException missing = Assert.Throws<FileNotFoundException>(() => ZoneMap.Load("no-existe-zones.json", map));
            Assert.Contains("Corrige zonesPath o déjalo vacío", missing.Message);

            string path = Path.Combine(Path.GetTempPath(), "avelora-zones-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, "null");
                InvalidOperationException empty = Assert.Throws<InvalidOperationException>(() => ZoneMap.Load(path, map));
                Assert.Equal("zones.json está vacío o no contiene un objeto.", empty.Message);
                File.WriteAllText(path, "{}");
                Assert.Empty(ZoneMap.Load(path, map).Zones);
                File.WriteAllText(path, "{ \"zones\": null }");
                Assert.Empty(ZoneMap.Load(path, map).Zones);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void ZoneMap_BuildNpcCollision_Bloquea_Solo_Las_Casillas_Safe()
        {
            var source = new MapCollisionGrid(4, 3, new[]
            {
                false, false, false, false,
                false, true,  false, false,
                false, false, false, false,
            }, -2, -1);
            ZoneMap zones = ZoneMap.FromDefinitions(new[]
            {
                Definition(1, "safe", Rect(-1, 0, 2, 2)),
                Definition(2, "pvp", Rect(0, 0, 1, 1)),
            }, source);

            MapCollisionGrid collision = zones.BuildNpcCollision(source);
            Assert.NotSame(source, collision);
            Assert.True(collision.IsTileBlocked(-1, 0));
            Assert.True(collision.IsTileBlocked(0, 0));
            Assert.True(collision.IsTileBlocked(-1, 1));
            Assert.True(collision.IsTileBlocked(-1, 0));
            Assert.False(collision.IsTileBlocked(1, 0));
            Assert.True(collision.IsTileBlocked(-1, 0));

            ZoneMap pvpOnly = ZoneMap.FromDefinitions(new[] { Definition(3, "pvp", Rect(-1, 0)) }, source);
            Assert.Same(source, pvpOnly.BuildNpcCollision(source));
        }

        [Fact]
        public void ZoneMap_ValidateSpawners_Rechaza_Radio_Que_Toca_La_Zona_Segura()
        {
            ZoneMap zones = ZoneMap.FromDefinitions(new[]
            {
                Definition(7, "safe", Rect(5, 5, 2, 2)),
            }, MapCollisionGrid.AllWalkable(20, 20));
            var npcs = new NpcCatalog
            {
                Types = new List<NpcType>
                {
                    new NpcType { Id = 2001, HalfWidth = 0.35f, HalfHeight = 0.5f },
                },
                Spawners = new List<NpcSpawner>
                {
                    new NpcSpawner { TypeId = 2001, X = 3.5f, Y = 5.5f, WanderRadius = 1.1f, Count = 1 },
                },
            };

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => zones.ValidateSpawners(npcs));
            Assert.Equal("zones.json: el spawner 1 (tipo 2001) toca la zona segura 7; muévelo o cambia la zona.", error.Message);

            npcs.Spawners[0].X = 3.4f;
            npcs.Spawners[0].WanderRadius = 1f;
            zones.ValidateSpawners(npcs);
        }

        [Fact]
        public void Datos_Reales_De_Zonas_Coinciden_Con_Carga_Y_Sus_Spawns_Son_Validos()
        {
            string basePath = AppContext.BaseDirectory;
            MapCollisionGrid map = MapFile.Load(Path.Combine(basePath, "maps", "avelora-valle.avmap"));
            ZoneMap zones = ZoneMap.Load(Path.Combine(basePath, "zones.json"), map);
            ItemCatalog items = ItemCatalog.Load(Path.Combine(basePath, "items.json"));
            NpcCatalog npcs = NpcCatalog.Load(Path.Combine(basePath, "npcs.json"));

            npcs.Validate(map, items);
            zones.ValidateSpawners(npcs);
            Assert.Equal(ZoneKind.Safe, zones.KindAt(-6.5f, 0.5f));

            foreach (ZoneInfo zone in zones.Zones.Where(value => value.Kind == ZoneKind.Pvp))
            {
                int total = 0;
                int walkable = 0;
                foreach (ZoneRect rect in zone.Rects)
                {
                    for (int y = rect.Y; y < rect.Y + rect.Height; y++)
                    {
                        for (int x = rect.X; x < rect.X + rect.Width; x++)
                        {
                            total++;
                            if (!map.IsTileBlocked(x, y)) walkable++;
                        }
                    }
                }

                Assert.True(walkable * 2 >= total, "La zona PvP " + zone.Id + " tiene menos de la mitad de casillas transitables.");
            }

            string loadZones = File.ReadAllText(Path.Combine(basePath, "zones.json"));
            string cargoZones = File.ReadAllText(Path.GetFullPath(Path.Combine(basePath, "..", "..", "..", "..", "..", "tools", "carga", "zones-carga.json")));
            Assert.Equal(loadZones, cargoZones);
        }

        [Fact]
        public void Zone_Emite_Cambio_Solo_Al_Entrar_Y_Salir_De_Una_Zona()
        {
            MapCollisionGrid map = MapCollisionGrid.AllWalkable(30, 20);
            Zone zone = NewZone(SafeZone(map));
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(10.5f, 5.5f), new Vec2(0.3f, 0.3f), 8);
            zone.SpawnPlayer(2, new Vec2(20.5f, 5.5f), new Vec2(0.3f, 0.3f), 8);
            var changes = new List<ZoneChangedMessage>();
            zone.ZoneChanged += (_, message) => changes.Add(message);

            zone.Tick();
            Assert.Equal((ushort)1, player.ZoneId);
            Assert.Equal(ZoneKind.Safe, player.ZoneKind);
            Assert.Single(changes);
            Assert.Equal(new ZoneChangedMessage(1, ZoneKind.Safe).ZoneId, changes[0].ZoneId);

            zone.Tick();
            Assert.Single(changes);
            player.Position = new Vec2(15.5f, 5.5f);
            zone.Tick();
            Assert.Equal((ushort)0, player.ZoneId);
            Assert.Equal(ZoneKind.Normal, player.ZoneKind);
            Assert.Equal(2, changes.Count);
            Assert.Equal((ushort)0, changes[1].ZoneId);
            Assert.Equal(ZoneKind.Normal, changes[1].Kind);
            Assert.Equal(2, zone.ZoneChanges);
            Assert.Equal(1, zone.SafeZoneEntries);
        }

        [Fact]
        public void Zone_Detecta_La_Zona_Segura_En_El_Mismo_Tick_Que_La_Reaparicion()
        {
            MapCollisionGrid map = MapCollisionGrid.AllWalkable(30, 20);
            var vitals = new PlayerVitalsSettings(new Vec2(10.5f, 5.5f), 0.5, 0, 0);
            Zone zone = NewZone(SafeZone(map), vitals: vitals);
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(5.5f, 5.5f), new Vec2(0.3f, 0.3f), 8);
            player.Health = 0;
            player.Action = EntityAction.Dead;
            var changes = new List<ZoneChangedMessage>();
            zone.ZoneChanged += (_, message) => changes.Add(message);

            for (int i = 0; i <= 10; i++) zone.Tick();

            Assert.Equal(new Vec2(10.5f, 5.5f), player.Position);
            Assert.Equal((ushort)1, player.ZoneId);
            Assert.Equal(ZoneKind.Safe, Assert.Single(changes).Kind);
        }

        [Fact]
        public void Zone_Sin_Mapa_De_Zonas_No_Emite_Eventos()
        {
            Zone zone = NewZone();
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(5.5f, 5.5f), new Vec2(0.3f, 0.3f), 8);
            int emitted = 0;
            zone.ZoneChanged += (_, _) => emitted++;

            for (int i = 0; i < 50; i++)
            {
                player.Position = new Vec2(5.5f + i, 5.5f);
                zone.Tick();
            }

            Assert.Equal(0, emitted);
            Assert.Equal(0, zone.ZoneChanges);
            Assert.Equal(0, zone.SafeZoneEntries);
        }

        [Fact]
        public void Npc_Cancela_La_Persecucion_Y_El_Ataque_Al_Entrar_El_Jugador_En_Safe()
        {
            MapCollisionGrid map = MapCollisionGrid.AllWalkable(30, 20);
            ZoneMap zones = SafeZone(map);
            Zone zone = NewZone(zones, NpcData(7.5f, 5.5f, 2, 8, 3), map: map);
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(9f, 5.5f), new Vec2(0.3f, 0.3f), 8);
            NpcEntity npc = Assert.IsType<NpcEntity>(zone.Entities[0]);

            zone.Tick();
            Assert.Equal(NpcAiState.Chase, npc.AiState);
            Assert.Equal(player.Id, npc.TargetEntityId);
            player.Position = new Vec2(10.5f, 5.5f);
            zone.Tick();
            Assert.Equal(NpcAiState.Return, npc.AiState);
            Assert.Equal(-1, npc.TargetEntityId);
            Assert.Equal(1, zone.NpcAggroCancelledSafeZone);

            npc.AiState = NpcAiState.Attack;
            npc.TargetEntityId = player.Id;
            npc.NextAttackTick = 0;
            int health = player.Health;
            zone.Tick();
            Assert.Equal(health, player.Health);
            Assert.Equal(NpcAiState.Return, npc.AiState);
            Assert.Equal(2, zone.NpcAggroCancelledSafeZone);
        }

        [Fact]
        public void Npc_No_Adquiere_Aggro_De_Jugadores_Que_Estan_En_Safe()
        {
            MapCollisionGrid map = MapCollisionGrid.AllWalkable(30, 20);
            Zone zone = NewZone(SafeZone(map), NpcData(8.5f, 5.5f, 0, 12, 3), map: map);
            zone.SpawnPlayer(1, new Vec2(10.5f, 5.5f), new Vec2(0.3f, 0.3f), 8);
            NpcEntity npc = Assert.IsType<NpcEntity>(zone.Entities[0]);

            zone.Tick();

            Assert.Equal(NpcAiState.Wander, npc.AiState);
            Assert.Equal(-1, npc.TargetEntityId);
        }

        [Fact]
        public void Npc_Que_Persigue_No_Mete_Ninguna_Esquina_De_Su_Caja_En_Safe()
        {
            MapCollisionGrid map = MapCollisionGrid.AllWalkable(30, 20);
            ZoneMap zones = SafeZone(map, 10, 0, 2, 20);
            Zone zone = NewZone(zones, NpcData(8.5f, 8.5f, 4, 10, 2), map: map);
            zone.SpawnPlayer(1, new Vec2(12.5f, 8.5f), new Vec2(0.3f, 0.3f), 8);
            NpcEntity npc = Assert.IsType<NpcEntity>(zone.Entities[0]);

            for (int i = 0; i < 200; i++)
            {
                zone.Tick();
                float left = npc.Position.X - npc.HalfSize.X;
                float right = npc.Position.X + npc.HalfSize.X;
                float bottom = npc.Position.Y - npc.HalfSize.Y;
                float top = npc.Position.Y + npc.HalfSize.Y;
                Assert.NotEqual(ZoneKind.Safe, zones.KindAt(left, bottom));
                Assert.NotEqual(ZoneKind.Safe, zones.KindAt(left, top));
                Assert.NotEqual(ZoneKind.Safe, zones.KindAt(right, bottom));
                Assert.NotEqual(ZoneKind.Safe, zones.KindAt(right, top));
            }
        }

        [Fact]
        public void Combat_Rechaza_Ataques_Desde_O_Hacia_Safe_Sin_Consumir_Recarga()
        {
            MapCollisionGrid map = MapCollisionGrid.AllWalkable(30, 20);
            Zone zone = NewZone(SafeZone(map));
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(10.5f, 5.5f), new Vec2(0.3f, 0.3f), 8);
            var npc = new NpcEntity(500, 2001, 0, new Vec2(9.5f, 5.5f), 2, 0, 100, 100, -1)
            {
                Position = new Vec2(9.5f, 5.5f),
                HalfSize = new Vec2(0.3f, 0.3f),
            };
            zone.AddEntity(npc);
            player.LastAttackTick = -1;
            Assert.True(zone.Attacks!.TryEnqueue(new AttackIntent(player.Id, npc.Id)));

            zone.Tick();

            Assert.Equal(1, zone.RejectedAttackSafeZone);
            Assert.Equal(100, npc.Health);
            player.Position = new Vec2(9.5f, 5.5f);
            Assert.True(zone.Attacks.TryEnqueue(new AttackIntent(player.Id, npc.Id)));
            zone.Tick();
            Assert.Equal(1, zone.AttacksOk);
            Assert.True(npc.Health < 100);

            var safeTarget = new NpcEntity(501, 2001, 0, new Vec2(10.5f, 5.5f), 2, 0, 100, 100, -1)
            {
                Position = new Vec2(10.5f, 5.5f),
                HalfSize = new Vec2(0.3f, 0.3f),
            };
            zone.AddEntity(safeTarget);
            Assert.True(zone.Attacks.TryEnqueue(new AttackIntent(player.Id, safeTarget.Id)));
            zone.Tick();
            Assert.Equal(2, zone.RejectedAttackSafeZone);
            Assert.Equal(100, safeTarget.Health);
        }

        [Fact]
        public void Combat_La_Zona_Segura_Precede_El_Cooldown_Y_El_Siguiente_Ataque_Tras_Salir_Se_Acepta()
        {
            MapCollisionGrid map = MapCollisionGrid.AllWalkable(30, 20);
            Zone zone = NewZone(SafeZone(map));
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(8.5f, 5.5f), new Vec2(0.3f, 0.3f), 8);
            var npc = new NpcEntity(500, 2001, 0, new Vec2(9.5f, 5.5f), 2, 0, 1000, 100, -1)
            {
                Position = new Vec2(9.5f, 5.5f),
                HalfSize = new Vec2(0.3f, 0.3f),
            };
            zone.AddEntity(npc);
            player.LastAttackTick = -1;
            Assert.True(zone.Attacks!.TryEnqueue(new AttackIntent(player.Id, npc.Id)));
            zone.Tick();
            Assert.Equal(1, zone.AttacksOk);

            player.Position = new Vec2(10.5f, 5.5f);
            for (int i = 0; i < 29; i++) zone.Tick();
            Assert.True(zone.Attacks.TryEnqueue(new AttackIntent(player.Id, npc.Id)));
            zone.Tick();
            Assert.Equal(1, zone.RejectedAttackSafeZone);

            player.Position = new Vec2(8.5f, 5.5f);
            Assert.True(zone.Attacks.TryEnqueue(new AttackIntent(player.Id, npc.Id)));
            zone.Tick();
            Assert.Equal(2, zone.AttacksOk);
        }

        [Fact]
        public void El_Dueno_Del_Botin_Recibe_Experiencia_Aunque_Este_En_Safe()
        {
            MapCollisionGrid map = MapCollisionGrid.AllWalkable(30, 20);
            var items = new ItemCatalog
            {
                Items = new List<ItemType>
                {
                    new ItemType { Id = 3001, Name = "Objeto de prueba", Kind = "material", MaxStack = 99 },
                },
            };
            Zone zone = NewZone(SafeZone(map), NpcData(8.5f, 5.5f, 0, 0, 2), map: map, items: items);
            PlayerEntity owner = zone.SpawnPlayer(1, new Vec2(8.5f, 5.5f), new Vec2(0.3f, 0.3f), 8);
            PlayerEntity killer = zone.SpawnPlayer(2, new Vec2(8.5f, 5.5f), new Vec2(0.3f, 0.3f), 8);
            NpcEntity npc = Assert.IsType<NpcEntity>(zone.Entities[0]);
            LootGainedMessage loot = default;
            PlayerEntity? recipient = null;
            zone.LootGained += (player, message) => { recipient = player; loot = message; };

            Assert.True(zone.Attacks!.TryEnqueue(new AttackIntent(owner.Id, npc.Id)));
            zone.Tick();
            Assert.Equal(owner.Id, npc.LootOwnerId);
            owner.Position = new Vec2(10.5f, 5.5f);
            npc.Health = 1;
            killer.LastAttackTick = -1;
            Assert.True(zone.Attacks.TryEnqueue(new AttackIntent(killer.Id, npc.Id)));
            zone.Tick();

            Assert.Same(owner, recipient);
            Assert.True(owner.Experience > 0);
            Assert.True(loot.Experience > 0);
            LootItemEntry gained = Assert.Single(loot.Items);
            Assert.Equal((ushort)3001, gained.ItemId);
            Assert.False(gained.OnGround);
        }
    }
}
