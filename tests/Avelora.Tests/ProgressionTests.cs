using System;
using System.IO;
using Avelora.Server.Entities;
using Avelora.Server.Items;
using Avelora.Server.Progression;
using Xunit;

namespace Avelora.Tests
{
    public sealed class ProgressionTests
    {
        private static string Thresholds(int count = 24, int first = 100)
        {
            var values = new string[count];
            Array.Fill(values, "100");
            if (count > 0) values[0] = first.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return "[" + string.Join(", ", values) + "]";
        }

        private static string ValidLevels(int points = 3, string? thresholds = null) =>
            "{ \"pointsPerLevel\": " + points + ", \"experienceToNext\": " +
            (thresholds ?? Thresholds()) + " }";

        private static string WriteLevels(string json)
        {
            string path = Path.Combine(Path.GetTempPath(), "avelora-levels-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, json);
            return path;
        }

        [Fact]
        public void Levels_Json_Valido_Carga_Limites_Y_Formulas()
        {
            string path = WriteLevels(ValidLevels(thresholds: Thresholds(first: 150)));

            try
            {
                LevelTable table = LevelTable.Load(path);
                Assert.Equal(25, table.MaxLevel);
                Assert.Equal(150, table.GetExperienceToNext(1));
                Assert.Equal(0, table.GetExperienceToNext(25));
                Assert.Equal(3, table.PointsPerLevel);
                Assert.Equal(10, table.FreeResetMaxLevel);
                Assert.Equal(3201, table.ResetItemId);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(101)]
        public void Levels_Json_Rechaza_PointsPerLevel_Fuera_De_Rango(int points)
        {
            string path = WriteLevels(ValidLevels(points));
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
        [InlineData(0)]
        [InlineData(256)]
        public void Levels_Json_Rechaza_FreeResetMaxLevel_Fuera_De_Rango(int level)
        {
            string path = WriteLevels(ValidLevels().Replace("\"experienceToNext\"", "\"freeResetMaxLevel\": " + level + ", \"experienceToNext\""));
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
        [InlineData(0)]
        [InlineData(65536)]
        public void Levels_Json_Rechaza_ResetItemId_Fuera_De_Rango(int itemId)
        {
            string path = WriteLevels(ValidLevels().Replace("\"experienceToNext\"", "\"resetItemId\": " + itemId + ", \"experienceToNext\""));
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
        public void Levels_Json_Exige_Que_El_Objeto_De_Reajuste_Exista_En_El_Catalogo()
        {
            string path = WriteLevels(ValidLevels());
            try
            {
                var items = new ItemCatalog { Items = new System.Collections.Generic.List<ItemType>() };
                InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => LevelTable.Load(path, items));
                Assert.Contains("resetItemId 3201", error.Message);
                Assert.Contains("items.json", error.Message);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Levels_Json_Rechaza_Umbrales_Que_No_Son_24_O_Estan_Fuera_De_Rango()
        {
            Assert.Throws<InvalidOperationException>(() => LoadLevels(ValidLevels(thresholds: Thresholds(23))));
            Assert.Throws<InvalidOperationException>(() => LoadLevels(ValidLevels(thresholds: Thresholds(25))));
            Assert.Throws<InvalidOperationException>(() => LoadLevels(ValidLevels(thresholds: Thresholds(first: 0))));
            Assert.Throws<InvalidOperationException>(() => LoadLevels(ValidLevels(thresholds: Thresholds(first: 10_000_001))));
        }

        private static LevelTable LoadLevels(string json)
        {
            string path = WriteLevels(json);
            try
            {
                return LevelTable.Load(path);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Levels_Json_Sin_Un_Campo_Obligatorio_Se_Rechaza()
        {
            string path = WriteLevels("{ \"pointsPerLevel\": 3 }");
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
        [InlineData("{ \"experienceToNext\": [100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100] }")]
        [InlineData("{ \"pointsPerLevel\": 3 }")]
        [InlineData("{ \"pointsPerLevel\": 3, \"experienceToNext\": null }")]
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
                PointsPerLevel = 0,
                ExperienceToNext = new int[24],
            };
            Array.Fill(limits.ExperienceToNext, 1);
            limits.Validate();

            limits.ExperienceToNext[0] = 10_000_000;
            limits.PointsPerLevel = 100;
            limits.Validate();

            limits.ExperienceToNext = new int[255];
            Array.Fill(limits.ExperienceToNext, 1);
            Assert.Throws<InvalidOperationException>(() => limits.Validate());
        }

        [Fact]
        public void Experiencia_Con_Umbral_Exacto_Y_Sobrante_Suben_Y_Conservan_El_Resto()
        {
            var table = new LevelTable
            {
                ExperienceToNext = new[] { 100, 150, 225 },
            };
            var player = new PlayerEntity(1, 1, 8) { MaxHealth = 100, Health = 20 };

            Assert.Equal(1, table.AddExperience(player, 100, ClassCatalog.Default));
            Assert.Equal(2, player.Level);
            Assert.Equal(0, player.Experience);
            Assert.Equal(100, player.MaxHealth);
            Assert.Equal(100, player.Health);

            Assert.Equal(0, table.AddExperience(player, 149, ClassCatalog.Default));
            Assert.Equal(149, player.Experience);
            Assert.Equal(1, table.AddExperience(player, 10, ClassCatalog.Default));
            Assert.Equal(3, player.Level);
            Assert.Equal(9, player.Experience);
            Assert.Equal(100, player.Health);
        }

        [Fact]
        public void Una_Muerte_Puede_Dar_Varias_Subidas_Y_En_El_Maximo_No_Se_Acumula()
        {
            var table = new LevelTable
            {
                ExperienceToNext = new[] { 100, 150 },
            };
            var player = new PlayerEntity(1, 1, 8) { MaxHealth = 100, Health = 1 };

            Assert.Equal(2, table.AddExperience(player, 400, ClassCatalog.Default));
            Assert.Equal(3, player.Level);
            Assert.Equal(0, player.Experience);
            Assert.Equal(100, player.MaxHealth);
            Assert.Equal(100, player.Health);
            Assert.Equal(0, table.AddExperience(player, 50, ClassCatalog.Default));
            Assert.Equal(0, player.Experience);
        }

        [Fact]
        public void Tabla_Por_Defecto_Llega_Al_Nivel_25_Y_No_Acopia_Experiencia_Al_Maximo()
        {
            LevelTable table = LevelTable.Default;
            var player = new PlayerEntity(1, 1, 8) { Health = 1 };
            int total = 0;
            for (int i = 0; i < table.ExperienceToNext!.Length; i++) total += table.ExperienceToNext[i];

            Assert.Equal(25, table.MaxLevel);
            Assert.Equal(24, table.ExperienceToNext.Length);
            Assert.Equal(24, table.AddExperience(player, total, ClassCatalog.Default));
            Assert.Equal(25, player.Level);
            Assert.Equal(0, player.Experience);
            Assert.Equal(player.MaxHealth, player.Health);
            Assert.Equal(0, table.AddExperience(player, 500, ClassCatalog.Default));
            Assert.Equal(0, player.Experience);
        }

        [Theory]
        [InlineData("healthPerLevel")]
        [InlineData("damagePerLevel")]
        public void Levels_Json_Indica_Que_La_Vida_Y_El_Daño_Se_Trasladaron_A_Classes(string field)
        {
            string path = WriteLevels(ValidLevels(thresholds: Thresholds())
                .Replace("\"experienceToNext\"", "\"" + field + "\": 1, \"experienceToNext\""));
            try
            {
                InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => LevelTable.Load(path));
                Assert.Contains("classes.json", error.Message);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
