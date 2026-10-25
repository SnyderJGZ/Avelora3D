using System;
using System.IO;
using Avelora.Server.Entities;
using Avelora.Server.Progression;
using Xunit;

namespace Avelora.Tests
{
    public sealed class ProgressionTests
    {
        private static string WriteLevels(string json)
        {
            string path = Path.Combine(Path.GetTempPath(), "avelora-levels-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, json);
            return path;
        }

        [Fact]
        public void Levels_Json_Valido_Carga_Limites_Y_Formulas()
        {
            string path = WriteLevels("""
                { "healthPerLevel": 15, "damagePerLevel": 1, "experienceToNext": [100, 150] }
                """);

            try
            {
                LevelTable table = LevelTable.Load(path);
                Assert.Equal(3, table.MaxLevel);
                Assert.Equal(100, table.GetExperienceToNext(1));
                Assert.Equal(0, table.GetExperienceToNext(3));
                Assert.Equal(130, table.GetMaxHealth(100, 3));
                Assert.Equal(16, table.GetAttackDamage(14, 3));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Theory]
        [InlineData("{ \"healthPerLevel\": -1, \"damagePerLevel\": 1, \"experienceToNext\": [100] }")]
        [InlineData("{ \"healthPerLevel\": 10001, \"damagePerLevel\": 1, \"experienceToNext\": [100] }")]
        [InlineData("{ \"healthPerLevel\": 1, \"damagePerLevel\": -1, \"experienceToNext\": [100] }")]
        [InlineData("{ \"healthPerLevel\": 1, \"damagePerLevel\": 10001, \"experienceToNext\": [100] }")]
        [InlineData("{ \"healthPerLevel\": 1, \"damagePerLevel\": 1, \"experienceToNext\": [] }")]
        [InlineData("{ \"healthPerLevel\": 1, \"damagePerLevel\": 1, \"experienceToNext\": [0] }")]
        [InlineData("{ \"healthPerLevel\": 1, \"damagePerLevel\": 1, \"experienceToNext\": [10000001] }")]
        public void Levels_Json_Con_Cualquier_Campo_Fuera_De_Rango_Se_Rechaza(string json)
        {
            string path = WriteLevels(json);
            try
            {
                Assert.Throws<InvalidOperationException>(() => LevelTable.Load(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Levels_Json_Sin_Un_Campo_Obligatorio_Se_Rechaza()
        {
            string path = WriteLevels("{ \"healthPerLevel\": 1, \"experienceToNext\": [100] }");
            try
            {
                Assert.Throws<InvalidOperationException>(() => LevelTable.Load(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Theory]
        [InlineData("{ \"damagePerLevel\": 1, \"experienceToNext\": [100] }")]
        [InlineData("{ \"healthPerLevel\": 1, \"damagePerLevel\": 1 }")]
        [InlineData("{ \"healthPerLevel\": 1, \"damagePerLevel\": 1, \"experienceToNext\": null }")]
        public void Levels_Json_Rechaza_Cada_Campo_Obligatorio_Ausente_O_Nulo(string json)
        {
            string path = WriteLevels(json);
            try
            {
                Assert.Throws<InvalidOperationException>(() => LevelTable.Load(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Levels_Json_Admite_Los_Extremos_Y_Rechaza_Mas_De_254_Umbrales()
        {
            var limits = new LevelTable
            {
                HealthPerLevel = 0,
                DamagePerLevel = 10000,
                ExperienceToNext = new[] { 1, 10_000_000 },
            };
            limits.Validate();

            limits.ExperienceToNext = new int[255];
            Array.Fill(limits.ExperienceToNext, 1);
            Assert.Throws<InvalidOperationException>(limits.Validate);
        }

        [Fact]
        public void Experiencia_Con_Umbral_Exacto_Y_Sobrante_Suben_Y_Conservan_El_Resto()
        {
            var table = new LevelTable
            {
                HealthPerLevel = 15,
                DamagePerLevel = 1,
                ExperienceToNext = new[] { 100, 150, 225 },
            };
            var player = new PlayerEntity(1, 1, 8) { MaxHealth = 100, Health = 20 };

            Assert.Equal(1, table.AddExperience(player, 100, 100));
            Assert.Equal(2, player.Level);
            Assert.Equal(0, player.Experience);
            Assert.Equal(115, player.MaxHealth);
            Assert.Equal(115, player.Health);

            Assert.Equal(0, table.AddExperience(player, 149, 100));
            Assert.Equal(149, player.Experience);
            Assert.Equal(1, table.AddExperience(player, 10, 100));
            Assert.Equal(3, player.Level);
            Assert.Equal(9, player.Experience);
            Assert.Equal(130, player.Health);
        }

        [Fact]
        public void Una_Muerte_Puede_Dar_Varias_Subidas_Y_En_El_Maximo_No_Se_Acumula()
        {
            var table = new LevelTable
            {
                HealthPerLevel = 15,
                DamagePerLevel = 1,
                ExperienceToNext = new[] { 100, 150 },
            };
            var player = new PlayerEntity(1, 1, 8) { MaxHealth = 100, Health = 1 };

            Assert.Equal(2, table.AddExperience(player, 400, 100));
            Assert.Equal(3, player.Level);
            Assert.Equal(0, player.Experience);
            Assert.Equal(130, player.MaxHealth);
            Assert.Equal(130, player.Health);
            Assert.Equal(0, table.AddExperience(player, 50, 100));
            Assert.Equal(0, player.Experience);
        }
    }
}
