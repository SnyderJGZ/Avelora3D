using System;
using System.Collections.Generic;
using System.IO;
using Avelora.Server.Entities;
using Avelora.Server.Npcs;
using Avelora.Server.Progression;
using Avelora.Server.Simulation;
using Avelora.Shared;
using Xunit;

namespace Avelora.Tests
{
    public sealed class I11Tests
    {
        private static string WriteLevels(string json)
        {
            string path = Path.Combine(Path.GetTempPath(), "avelora-levels-i11-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, json);
            return path;
        }

        private static string ValidLevels(string deathPenalty = "") =>
            "{ \"pointsPerLevel\": 3, " + deathPenalty +
            "\"experienceToNext\": [100, 150, 225, 340, 500, 750, 1100, 1650, 2500, 3500, 4700, 6200, " +
            "8000, 10000, 12500, 15500, 19000, 23000, 27500, 32500, 38000, 44000, 51000, 59000] }";

        private static Zone NewZone(
            LevelTable levels,
            bool withNpc = true,
            PlayerVitalsSettings? vitals = null)
        {
            NpcCatalog? npcs = withNpc
                ? new NpcCatalog
                {
                    Types = new List<NpcType>
                    {
                        new NpcType
                        {
                            Id = 2001,
                            Name = "Orco",
                            Speed = 0f,
                            HalfWidth = 0.35f,
                            HalfHeight = 0.35f,
                            MaxHealth = 100,
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
                        new NpcSpawner { TypeId = 2001, X = 10f, Y = 10f, Count = 1, WanderRadius = 1f },
                    },
                }
                : null;

            return new Zone(
                1,
                MapCollisionGrid.AllWalkable(40, 40),
                new EntityStore(),
                new EntityIdAllocator(),
                new InputQueue(256),
                new MovementSystem(4f, SimulationConstants.FixedDt),
                128,
                4,
                new InterestSettings(40f, 8f, 0f),
                npcs,
                12345,
                new AttackQueue(256),
                new CombatSettings(0.5f),
                vitals ?? new PlayerVitalsSettings(new Vec2(5f, 5f), 60d, 0d, 0d),
                levels: levels);
        }

        private static PlayerEntity KillPlayerWithNpc(Zone zone, int level = 1, int experience = 50)
        {
            PlayerEntity player = zone.SpawnPlayer(
                1, new Vec2(10.5f, 10f), new Vec2(0.4f, 0.4f), 8, level: level, experience: experience);
            for (int i = 0; i < 3; i++)
            {
                zone.Tick();
            }

            Assert.Equal(EntityAction.Dead, player.Action);
            return player;
        }

        [Fact]
        public void El_Porcentaje_De_Penalizacion_Por_Defecto_Es_Diez_Aunque_Falte_En_Json()
        {
            string path = WriteLevels(ValidLevels());
            try
            {
                Assert.Equal(10, LevelTable.Load(path).DeathPenaltyPercent);
                Assert.Equal(10, LevelTable.Default.DeathPenaltyPercent);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(101)]
        public void Rechaza_Porcentaje_De_Penalizacion_Fuera_De_Rango(int percent)
        {
            string path = WriteLevels(ValidLevels("\"deathPenaltyPercent\": " + percent + ", "));
            try
            {
                InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => LevelTable.Load(path));
                Assert.Equal("levels.json: deathPenaltyPercent debe estar entre 0 y 100.", error.Message);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(100)]
        public void Acepta_Extremos_Del_Porcentaje_De_Penalizacion(int percent)
        {
            string path = WriteLevels(ValidLevels("\"deathPenaltyPercent\": " + percent + ", "));
            try
            {
                Assert.Equal(percent, LevelTable.Load(path).DeathPenaltyPercent);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Theory]
        [InlineData(1, 50, 10, 10)]
        [InlineData(1, 5, 10, 5)]
        [InlineData(3, 50, 10, 22)]
        [InlineData(1, 50, 0, 0)]
        [InlineData(25, 50, 10, 0)]
        public void GetDeathPenalty_Respeta_Experiencia_Porcentaje_Y_Nivel_Maximo(
            int level,
            int experience,
            int percent,
            int expected)
        {
            var table = new LevelTable
            {
                DeathPenaltyPercent = percent,
                ExperienceToNext = new[]
                {
                    100, 150, 225, 340, 500, 750, 1100, 1650, 2500, 3500, 4700, 6200,
                    8000, 10000, 12500, 15500, 19000, 23000, 27500, 32500, 38000, 44000, 51000, 59000,
                },
            };
            var player = new PlayerEntity(1, 1, 8) { Level = level, Experience = experience };

            Assert.Equal(expected, table.GetDeathPenalty(player));
            Assert.Equal(level, player.Level);
        }

        [Fact]
        public void Una_Muerte_Por_Pnj_Resta_Experiencia_Sin_Bajar_De_Nivel_Y_Suma_Metricas()
        {
            LevelTable levels = LevelTable.Default;
            Zone zone = NewZone(levels);

            PlayerEntity player = KillPlayerWithNpc(zone);

            Assert.Equal(1, player.Level);
            Assert.Equal(40, player.Experience);
            Assert.Equal(1L, zone.PenalizedDeaths);
            Assert.Equal(10L, zone.ExperienceLost);
        }

        [Fact]
        public void Perdida_Mayor_Que_La_Experiencia_Deja_Cero_Y_No_Baja_De_Nivel()
        {
            Zone zone = NewZone(LevelTable.Default);

            PlayerEntity player = KillPlayerWithNpc(zone, experience: 3);

            Assert.Equal(1, player.Level);
            Assert.Equal(0, player.Experience);
            Assert.Equal(1L, zone.PenalizedDeaths);
            Assert.Equal(3L, zone.ExperienceLost);
        }

        [Fact]
        public void El_Nivel_Maximo_No_Pierde_Experiencia_Ni_Cuenta_Penalizacion()
        {
            Zone zone = NewZone(LevelTable.Default);

            PlayerEntity player = KillPlayerWithNpc(zone, level: 25);

            Assert.Equal(0, player.Experience);
            Assert.Equal(0L, zone.PenalizedDeaths);
            Assert.Equal(0L, zone.ExperienceLost);
        }

        [Fact]
        public void Porcentaje_Cero_No_Resta_Experiencia()
        {
            var levels = new LevelTable
            {
                DeathPenaltyPercent = 0,
                ExperienceToNext = LevelTable.Default.ExperienceToNext,
            };
            Zone zone = NewZone(levels);

            PlayerEntity player = KillPlayerWithNpc(zone);

            Assert.Equal(50, player.Experience);
            Assert.Equal(0L, zone.PenalizedDeaths);
            Assert.Equal(0L, zone.ExperienceLost);
        }

        [Fact]
        public void Muerte_Sin_Causa_De_Pnj_No_Penaliza()
        {
            Zone zone = NewZone(LevelTable.Default, withNpc: false);
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(5f, 5f), new Vec2(0.4f, 0.4f), 8, experience: 50);
            player.Health = 0;

            zone.Tick();

            Assert.Equal(50, player.Experience);
            Assert.Equal(0L, zone.PenalizedDeaths);
            Assert.Equal(0L, zone.ExperienceLost);
        }

        [Fact]
        public void Muchos_Ticks_Muerto_Solo_Aplican_Una_Penalizacion()
        {
            Zone zone = NewZone(LevelTable.Default);
            PlayerEntity player = KillPlayerWithNpc(zone);

            for (int i = 0; i < 100; i++)
            {
                zone.Tick();
            }

            Assert.Equal(40, player.Experience);
            Assert.Equal(1L, zone.PenalizedDeaths);
            Assert.Equal(10L, zone.ExperienceLost);
        }

        [Fact]
        public void El_Cambio_De_Progreso_Por_Penalizacion_Se_Publica_En_El_Tick_Siguiente()
        {
            Zone zone = NewZone(LevelTable.Default);
            int? publishedExperience = null;
            zone.ProgressChanged += player => publishedExperience = player.Experience;
            PlayerEntity player = KillPlayerWithNpc(zone);

            Assert.Null(publishedExperience);
            zone.Tick();

            Assert.Equal(40, publishedExperience);
            Assert.Equal(40, player.Experience);
        }
    }
}
