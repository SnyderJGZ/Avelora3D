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
            Spawners = new List<NpcSpawner>(spawners),
        };

        private static NpcRank Rank(
            byte id = 1,
            double chance = 1d,
            double health = 1d,
            double damage = 1d,
            double experience = 1d,
            int lootRolls = 1) => new NpcRank
        {
            Id = id,
            Name = "Rango " + id,
            Chance = chance,
            Health = health,
            Damage = damage,
            Experience = experience,
            LootRolls = lootRolls,
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
    { ""id"": 2001, ""name"": ""Orco"", ""speed"": 2.0, ""halfWidth"": 0.35, ""halfHeight"": 0.35, ""maxHealth"": 30, ""experience"": 20, ""level"": 1, ""respawnSeconds"": 10, ""aggroRadius"": 4.0, ""leashRadius"": 8.0, ""attackDamage"": 8, ""attackRange"": 1.0, ""attackCooldownSeconds"": 1.5 }
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
        public void Rangos_Se_Cargan_Desde_Json_Y_Son_Opcionales()
        {
            string json = """
                { "ranks": [{ "id": 1, "name": "Especial", "chance": 0.25,
                    "health": 2.5, "damage": 1.5, "experience": 3, "lootRolls": 2 }],
                  "types": [{ "id": 2001, "name": "Enemigo", "speed": 2, "halfWidth": 0.35,
                    "halfHeight": 0.35, "maxHealth": 30, "experience": 20, "level": 1,
                    "respawnSeconds": 10, "aggroRadius": 0, "leashRadius": 8,
                    "attackDamage": 8, "attackRange": 1, "attackCooldownSeconds": 1.5,
                    "ranks": false }],
                  "spawners": [] }
                """;
            string path = WriteTempJson(json);
            try
            {
                NpcCatalog catalog = NpcCatalog.Load(path);
                NpcRank rank = Assert.Single(catalog.Ranks);
                Assert.Equal((byte)1, rank.Id);
                Assert.Equal("Especial", rank.Name);
                Assert.Equal(0.25, rank.Chance);
                Assert.Equal(2.5, rank.Health);
                Assert.Equal(2, rank.LootRolls);
                Assert.False(catalog.Types[0].Ranks);
                catalog.Validate(MapCollisionGrid.AllWalkable(20, 20));
            }
            finally
            {
                File.Delete(path);
            }

            string withoutRanks = WriteTempJson("{ \"types\": [], \"spawners\": [] }");
            try
            {
                NpcCatalog catalog = NpcCatalog.Load(withoutRanks);
                Assert.Empty(catalog.Ranks);
                Assert.True(Catalog().Types[0].Ranks);
                catalog.Validate(MapCollisionGrid.AllWalkable(20, 20));
            }
            finally
            {
                File.Delete(withoutRanks);
            }
        }

        [Fact]
        public void Rangos_Rechazan_Ids_Probabilidades_Multiplicadores_Y_Tiradas_Invalidos()
        {
            NpcCatalog catalog = Catalog();
            catalog.Ranks = new List<NpcRank> { Rank(id: 1), Rank(id: 1) };
            AssertRankInvalid(catalog, "repetido");

            catalog = Catalog();
            catalog.Ranks = new List<NpcRank> { Rank(id: 0) };
            AssertRankInvalid(catalog, "entre 1 y 3");

            catalog = Catalog();
            catalog.Ranks = new List<NpcRank> { Rank(id: 4) };
            AssertRankInvalid(catalog, "entre 1 y 3");

            catalog = Catalog();
            catalog.Ranks = new List<NpcRank> { Rank(id: 1, chance: 0.6), Rank(id: 2, chance: 0.5) };
            AssertRankInvalid(catalog, "suma");

            foreach (double chance in new[] { -0.01, 1.01, double.NaN, double.PositiveInfinity })
            {
                catalog = Catalog();
                catalog.Ranks = new List<NpcRank> { Rank(chance: chance) };
                AssertRankInvalid(catalog, "chance");
            }

            foreach (double multiplier in new[] { 0.99, 20.01, double.NaN, double.PositiveInfinity })
            {
                catalog = Catalog();
                catalog.Ranks = new List<NpcRank> { Rank(health: multiplier) };
                AssertRankInvalid(catalog, "health");

                catalog = Catalog();
                catalog.Ranks = new List<NpcRank> { Rank(damage: multiplier) };
                AssertRankInvalid(catalog, "damage");

                catalog = Catalog();
                catalog.Ranks = new List<NpcRank> { Rank(experience: multiplier) };
                AssertRankInvalid(catalog, "experience");
            }

            foreach (int rolls in new[] { 0, 6 })
            {
                catalog = Catalog();
                catalog.Ranks = new List<NpcRank> { Rank(lootRolls: rolls) };
                AssertRankInvalid(catalog, "lootRolls");
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(25)]
        public void Rango_Exige_Nombre_De_Uno_A_24_Caracteres(int nameLength)
        {
            NpcCatalog catalog = Catalog();
            catalog.Ranks = new List<NpcRank> { Rank() };
            catalog.Ranks[0].Name = new string('x', nameLength);

            AssertRankInvalid(catalog, "name");
        }

        private static void AssertRankInvalid(NpcCatalog catalog, string expected)
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => catalog.Validate(MapCollisionGrid.AllWalkable(20, 20)));
            Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Aparicion_Aplica_Multiplicadores_Y_Deja_El_Rango_Normal_Implicito()
        {
            NpcCatalog catalog = Catalog(Spawner(5f, 5f, 1, 2f));
            catalog.Types[0].AttackDamage = 3;
            catalog.Ranks = new List<NpcRank>
            {
                Rank(chance: 1, health: 2.5, damage: 1.5, experience: 3),
            };

            Zone zone = NewZone(MapCollisionGrid.AllWalkable(20, 20), catalog);
            NpcEntity npc = Assert.IsType<NpcEntity>(zone.Entities[0]);

            Assert.Equal((byte)1, npc.Rank);
            Assert.Equal(75, npc.MaxHealth);
            Assert.Equal(5, npc.AttackDamage);
            Assert.Equal(60, npc.ExperienceReward);
            Assert.Equal(1, npc.LootRolls);

            NpcCatalog noRanks = Catalog(Spawner(5f, 5f, 1, 2f));
            Zone normalZone = NewZone(MapCollisionGrid.AllWalkable(20, 20), noRanks);
            Assert.Equal((byte)0, Assert.IsType<NpcEntity>(normalZone.Entities[0]).Rank);
        }

        [Fact]
        public void Tipo_Con_Ranks_False_Nunca_Recibe_Un_Rango()
        {
            NpcCatalog catalog = Catalog(Spawner(5f, 5f, 10, 2f));
            catalog.Types[0].Ranks = false;
            catalog.Ranks = new List<NpcRank> { Rank(chance: 1) };

            Zone zone = NewZone(MapCollisionGrid.AllWalkable(20, 20), catalog);

            for (int i = 0; i < zone.Entities.Count; i++)
            {
                Assert.Equal((byte)0, Assert.IsType<NpcEntity>(zone.Entities[i]).Rank);
            }
        }

        [Fact]
        public void Reparto_De_Rangos_Con_Semilla_Fija_Se_Aproxima_A_Las_Probabilidades()
        {
            NpcCatalog catalog = Catalog(Spawner(5f, 5f, 10000, 2f));
            catalog.Ranks = new List<NpcRank> { Rank(id: 2, chance: 0.02), Rank(id: 1, chance: 0.08) };
            Zone zone = NewZone(MapCollisionGrid.AllWalkable(20, 20), catalog, seed: 7351);
            int firstRank = 0;
            int secondRank = 0;

            for (int i = 0; i < zone.Entities.Count; i++)
            {
                byte rank = Assert.IsType<NpcEntity>(zone.Entities[i]).Rank;
                if (rank == 1) firstRank++;
                if (rank == 2) secondRank++;
            }

            Assert.InRange(firstRank, 700, 900);
            Assert.InRange(secondRank, 100, 300);
        }

        [Fact]
        public void La_Reaparicion_Vuelve_A_Tirar_El_Rango_Y_Actualiza_Las_Metricas()
        {
            NpcCatalog catalog = Catalog(Spawner(5f, 5f, 1, 2f));
            catalog.Ranks = new List<NpcRank>
            {
                Rank(id: 1, chance: 0.5),
                Rank(id: 2, chance: 0.5),
            };
            bool observedDifferentRank = false;

            for (int seed = 1; seed <= 16 && !observedDifferentRank; seed++)
            {
                Zone zone = NewZone(MapCollisionGrid.AllWalkable(20, 20), catalog, seed);
                NpcEntity initial = Assert.IsType<NpcEntity>(zone.Entities[0]);
                initial.Action = EntityAction.Dead;
                initial.Health = 0;
                zone.Tick();

                for (int tick = 0; tick < 200; tick++)
                {
                    zone.Tick();
                }

                NpcEntity respawned = Assert.IsType<NpcEntity>(Assert.Single(zone.Entities.All));
                observedDifferentRank = initial.Rank != respawned.Rank;
                if (observedDifferentRank)
                {
                    Assert.Equal(2, zone.RankAppearances(1) + zone.RankAppearances(2));
                    Assert.Equal(1, zone.RankDeaths(initial.Rank));
                    Assert.Equal(2, zone.EliteAppearances);
                    Assert.Equal(1, zone.EliteDeaths);
                }
            }

            Assert.True(observedDifferentRank, "La reaparición debe efectuar un nuevo sorteo de rango.");
        }

        [Theory]
        [InlineData(0, 0, 0, "level")]
        [InlineData(256, 0, 0, "level")]
        [InlineData(1, -1, 0, "defense")]
        [InlineData(1, 0, 100001, "magicResist")]
        public void Level_Y_Defensas_De_Pnj_Validan_Sus_Rangos(
            int level,
            int defense,
            int magicResist,
            string expected)
        {
            NpcCatalog catalog = Catalog();
            catalog.Types[0].Level = level;
            catalog.Types[0].Defense = defense;
            catalog.Types[0].MagicResist = magicResist;

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => catalog.Validate(MapCollisionGrid.AllWalkable(20, 20)));
            Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Npc_Json_Sin_Nivel_Obligatorio_No_Valida()
        {
            string path = WriteTempJson("""
                { "types": [
                  { "id": 2001, "name": "Orco", "speed": 2, "halfWidth": 0.35, "halfHeight": 0.35,
                    "maxHealth": 30, "experience": 20, "respawnSeconds": 10, "aggroRadius": 0,
                    "leashRadius": 8, "attackDamage": 8, "attackRange": 1, "attackCooldownSeconds": 1.5 }
                ], "spawners": [] }
                """);
            try
            {
                NpcCatalog catalog = NpcCatalog.Load(path);
                InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                    () => catalog.Validate(MapCollisionGrid.AllWalkable(20, 20)));
                Assert.Contains("level", error.Message);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Theory]
        [InlineData("\"experience\": 0", true)]
        [InlineData("\"experience\": -1", true)]
        [InlineData("\"experience\": 1000001", true)]
        [InlineData("", false)]
        public void Experience_De_Pnj_Ausente_O_Fuera_De_Rango_No_Valida(string experienceField, bool includeField)
        {
            string experience = includeField ? ", " + experienceField : string.Empty;
            string path = WriteTempJson("""
                { "types": [
                  { "id": 2001, "name": "Orco", "speed": 2, "halfWidth": 0.35, "halfHeight": 0.35,
                    "maxHealth": 30EXPERIENCE_FIELD, "level": 1, "respawnSeconds": 10, "aggroRadius": 0,
                    "leashRadius": 8, "attackDamage": 8, "attackRange": 1, "attackCooldownSeconds": 1.5 }
                ], "spawners": [] }
                """.Replace("EXPERIENCE_FIELD", experience));

            try
            {
                NpcCatalog catalog = NpcCatalog.Load(path);
                if (experienceField == "\"experience\": 0")
                {
                    catalog.Validate(MapCollisionGrid.AllWalkable(20, 20));
                }
                else
                {
                    Assert.Throws<InvalidOperationException>(() => catalog.Validate(MapCollisionGrid.AllWalkable(20, 20)));
                }
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

        [Theory]
        [InlineData(0)]
        [InlineData(65536)]
        public void Un_Id_De_Tipo_Que_No_Cabe_En_El_Snapshot_No_Valida(int id)
        {
            NpcCatalog catalog = Catalog(Spawner(3f, 3f, 1, 2f, typeId: id));
            catalog.Types[0].Id = id;

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => catalog.Validate(MapCollisionGrid.AllWalkable(20, 20)));

            Assert.Contains("entre 1 y 65535", error.Message);
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
