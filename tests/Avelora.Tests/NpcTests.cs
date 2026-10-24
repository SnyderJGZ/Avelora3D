using System;
using System.Collections.Generic;
using System.IO;
using Avelora.Server.Entities;
using Avelora.Server.Npcs;
using Avelora.Server.Simulation;
using Avelora.Shared;
using Avelora.Shared.Maps;
using Xunit;

namespace Avelora.Tests
{
    /// <summary>
    /// Pruebas de I-01: carga de <c>npcs.json</c>, aparición, IA de deambular y correa.
    /// </summary>
    public class NpcTests
    {
        private static string MapPath =>
            Path.Combine(AppContext.BaseDirectory, "maps", "avelora-etapa-a.avmap");

        // Mapa de 20x10 con borde y dos tabiques interiores, para que la IA choque con paredes.
        private static MapCollisionGrid WallMap() => MapCollisionGrid.FromRows(new[]
        {
            "####################",
            "#..................#",
            "#..................#",
            "#...####...........#",
            "#..................#",
            "#..................#",
            "#.........####.....#",
            "#..................#",
            "#..................#",
            "####################",
        });

        private static NpcSpawner Spawner(float x, float y, int count, float radius, int typeId = 2001) =>
            new NpcSpawner
            {
                TypeId = typeId,
                X = x,
                Y = y,
                Count = count,
                WanderRadius = radius,
            };

        private static NpcCatalog Catalog(params NpcSpawner[] spawners) => new NpcCatalog
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
                    MaxHealth = 30,
                    RespawnSeconds = 10f,
                    AggroRadius = 0f,
                    LeashRadius = 8f,
                    AttackDamage = 1,
                    AttackRange = 1f,
                    AttackCooldownSeconds = 1.5f,
                },
            },
            Spawners = new List<NpcSpawner>(spawners),
        };

        private static Zone NewZone(MapCollisionGrid map, NpcCatalog? npcs, int seed = 12345) =>
            new Zone(
                1,
                map,
                new EntityStore(),
                new EntityIdAllocator(),
                new InputQueue(64),
                new MovementSystem(4f, SimulationConstants.FixedDt),
                128,
                4,
                new InterestSettings(12f, 8f, 1f),
                npcs,
                seed);

        private static string WriteTempJson(string json)
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                "avelora-npcs-" + Guid.NewGuid().ToString("N") + ".json");

            File.WriteAllText(path, json);
            return path;
        }

        [Fact]
        public void Un_Npcs_Valido_Se_Carga_Y_Valida_Contra_El_Mapa()
        {
            string path = WriteTempJson(@"{
  ""types"": [
    { ""id"": 2001, ""name"": ""Orco"", ""speed"": 2.0, ""halfWidth"": 0.35, ""halfHeight"": 0.35, ""maxHealth"": 30, ""respawnSeconds"": 10, ""aggroRadius"": 4.0, ""leashRadius"": 8.0, ""attackDamage"": 8, ""attackRange"": 1.0, ""attackCooldownSeconds"": 1.5 }
  ],
  ""spawners"": [
    { ""typeId"": 2001, ""x"": 3.5, ""y"": 1.5, ""count"": 3, ""wanderRadius"": 3.0 },
    { ""typeId"": 2001, ""x"": -5.5, ""y"": 2.5, ""count"": 2, ""wanderRadius"": 2.5 }
  ]
}");

            try
            {
                NpcCatalog catalog = NpcCatalog.Load(path);

                Assert.Single(catalog.Types);
                Assert.Equal(2, catalog.Spawners.Count);
                Assert.Equal(5, catalog.TotalEnemies);
                Assert.Equal("Orco", catalog.Types[0].Name);
                Assert.Equal(2001, catalog.FindType(2001)!.Id);
                Assert.Equal(30, catalog.FindType(2001)!.MaxHealth);
                Assert.Equal(10f, catalog.FindType(2001)!.RespawnSeconds);
                Assert.Equal(200L, catalog.FindType(2001)!.RespawnTicks);

                catalog.Validate(MapFile.Load(MapPath));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Un_TypeId_Inexistente_No_Valida()
        {
            NpcCatalog catalog = Catalog(Spawner(3f, 3f, 1, 2f, typeId: 9999));

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => catalog.Validate(MapCollisionGrid.AllWalkable(20, 20)));

            Assert.Contains("9999", error.Message);
            Assert.Contains("no existe", error.Message);
        }

        [Fact]
        public void Un_Spawner_Dentro_De_Una_Pared_No_Valida()
        {
            // En el mapa real, x = 0 es un tabique de y = -6 a y = 0.
            NpcCatalog catalog = Catalog(Spawner(0f, -3f, 1, 2f));

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => catalog.Validate(MapFile.Load(MapPath)));

            Assert.Contains("pared", error.Message);
            Assert.Contains("(0, -3)", error.Message);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(51)]
        public void Un_Count_Fuera_De_Rango_No_Valida(int count)
        {
            NpcCatalog catalog = Catalog(Spawner(3f, 3f, count, 2f));

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => catalog.Validate(MapCollisionGrid.AllWalkable(20, 20)));

            Assert.Contains("count", error.Message);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(10.1f)]
        public void Una_Velocidad_Imposible_No_Valida(float speed)
        {
            NpcCatalog catalog = Catalog(Spawner(3f, 3f, 1, 2f));
            catalog.Types[0].Speed = speed;

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => catalog.Validate(MapCollisionGrid.AllWalkable(20, 20)));

            Assert.Contains("speed", error.Message);
        }

        [Fact]
        public void El_Total_De_Enemigos_No_Pasa_De_500()
        {
            var spawners = new List<NpcSpawner>();

            for (int i = 0; i < 11; i++)
            {
                spawners.Add(Spawner(3f + (i * 3f), 3f, 50, 1f));
            }

            var catalog = Catalog(spawners.ToArray());

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => catalog.Validate(MapCollisionGrid.AllWalkable(100, 20)));

            Assert.Contains("500", error.Message);
        }

        [Fact]
        public void La_Zona_Crea_La_Suma_De_Los_Count_Sin_Chocar_Con_Los_Jugadores()
        {
            NpcCatalog catalog = Catalog(
                Spawner(5f, 5f, 3, 2f),
                Spawner(15f, 15f, 2, 2f));

            Zone zone = NewZone(MapCollisionGrid.AllWalkable(40, 40), catalog);

            Assert.Equal(5, zone.Entities.CountOf(EntityKind.Npc));

            var ids = new HashSet<int>();

            for (int i = 0; i < zone.Entities.Count; i++)
            {
                NpcEntity npc = Assert.IsType<NpcEntity>(zone.Entities[i]);
                Assert.Equal(EntityKind.Npc, npc.Kind);
                Assert.True(ids.Add(npc.Id), "Id de PNJ repetido: " + npc.Id);
            }

            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(8f, 8f), new Vec2(0.4f, 0.4f), 8);

            Assert.DoesNotContain(player.Id, ids);
            Assert.Equal(6, zone.Entities.Count);
        }

        [Fact]
        public void Los_Enemigos_Nunca_Atraviesan_Paredes()
        {
            MapCollisionGrid map = WallMap();
            NpcCatalog catalog = Catalog(
                Spawner(2f, 2f, 2, 3f),
                Spawner(12f, 5f, 2, 3f));

            Zone zone = NewZone(map, catalog);

            for (int tick = 0; tick < 2000; tick++)
            {
                zone.Tick();

                for (int i = 0; i < zone.Entities.Count; i++)
                {
                    if (zone.Entities[i] is NpcEntity npc)
                    {
                        Assert.True(
                            map.IsBoxWalkable(npc.Position, npc.HalfSize),
                            "El PNJ " + npc.Id + " entró en una pared en el tick " + tick +
                            ": " + npc.Position);
                    }
                }
            }
        }

        [Fact]
        public void Dos_Zonas_Con_La_Misma_Semilla_Terminan_En_Las_Mismas_Posiciones()
        {
            MapCollisionGrid map = WallMap();
            NpcCatalog catalog = Catalog(
                Spawner(2f, 2f, 3, 3f),
                Spawner(12f, 5f, 3, 3f));

            Zone first = NewZone(map, catalog, seed: 777);
            Zone second = NewZone(map, catalog, seed: 777);

            for (int tick = 0; tick < 500; tick++)
            {
                first.Tick();
                second.Tick();
            }

            Assert.Equal(first.Entities.Count, second.Entities.Count);

            for (int i = 0; i < first.Entities.Count; i++)
            {
                NpcEntity a = Assert.IsType<NpcEntity>(first.Entities[i]);
                NpcEntity b = Assert.IsType<NpcEntity>(second.Entities[i]);

                Assert.Equal(a.Id, b.Id);
                Assert.Equal(a.DirectionX, b.DirectionX);
                Assert.Equal(a.DirectionY, b.DirectionY);
                Assert.Equal(a.Position, b.Position);
            }
        }

        [Fact]
        public void Tras_2000_Ticks_Nadie_Esta_Fuera_De_La_Correa()
        {
            MapCollisionGrid map = WallMap();
            NpcCatalog catalog = Catalog(
                Spawner(2f, 2f, 2, 2.5f),
                Spawner(12f, 5f, 2, 2.5f));

            Zone zone = NewZone(map, catalog);

            for (int tick = 0; tick < 2000; tick++)
            {
                zone.Tick();
            }

            for (int i = 0; i < zone.Entities.Count; i++)
            {
                NpcEntity npc = Assert.IsType<NpcEntity>(zone.Entities[i]);
                float distance = (npc.Position - npc.Home).Length;

                Assert.True(
                    distance <= npc.WanderRadius + 1f,
                    "El PNJ " + npc.Id + " está a " + distance + " de su casa (radio " +
                    npc.WanderRadius + ").");
            }
        }
    }
}
