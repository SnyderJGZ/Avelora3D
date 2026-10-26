using System;
using System.IO;
using Avelora.Server.Entities;
using Avelora.Server.Progression;
using Xunit;

namespace Avelora.Tests
{
    public sealed class CharacterClassesTests
    {
        private const string ClassesJson = """
            {
              "baseHealth": 50,
              "damagePerPoint": 0.5,
              "classes": [
                { "id": 1, "name": "Guerrero", "primary": "str", "base": { "str": 4, "dex": 3, "int": 1, "vit": 10 }, "healthPerVit": 5, "defense": 0, "magicResist": 0, "resource": { "name": "Furia", "base": 100, "perInt": 0 }, "unarmed": { "damage": 10, "damageType": "physical", "range": 1.2, "cooldownSeconds": 0.8 } },
                { "id": 2, "name": "Mago", "primary": "int", "base": { "str": 1, "dex": 3, "int": 8, "vit": 6 }, "healthPerVit": 5, "defense": 0, "magicResist": 0, "resource": { "name": "Maná", "base": 60, "perInt": 4 }, "unarmed": { "damage": 10, "damageType": "magical", "range": 1.2, "cooldownSeconds": 1.0 } },
                { "id": 3, "name": "Ranger", "primary": "dex", "base": { "str": 2, "dex": 6, "int": 2, "vit": 8 }, "healthPerVit": 5, "defense": 0, "magicResist": 0, "resource": { "name": "Energía", "base": 100, "perInt": 0 }, "unarmed": { "damage": 10, "damageType": "physical", "range": 1.2, "cooldownSeconds": 0.8 } },
                { "id": 4, "name": "Sanador", "primary": "int", "base": { "str": 1, "dex": 2, "int": 7, "vit": 8 }, "healthPerVit": 5, "defense": 0, "magicResist": 0, "resource": { "name": "Maná", "base": 60, "perInt": 4 }, "unarmed": { "damage": 10, "damageType": "magical", "range": 1.2, "cooldownSeconds": 1.0 } }
              ]
            }
            """;

        private static ClassCatalog Load(string json = ClassesJson)
        {
            string path = Path.Combine(Path.GetTempPath(), "avelora-classes-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, json);
            try
            {
                return ClassCatalog.Load(path);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Classes_Json_Carga_Las_Cuatro_Clases_Sin_Enum_De_Clases()
        {
            ClassCatalog catalog = Load();

            Assert.NotNull(catalog.Classes);
            Assert.Equal(4, catalog.Classes!.Count);
            Assert.Equal("Guerrero", catalog.Find(1)!.Name);
            Assert.Equal("Sanador", catalog.Find(4)!.Name);
            Assert.Null(catalog.Find(5));
        }

        [Theory]
        [InlineData("\"baseHealth\": 50", "\"baseHealth\": 0", "baseHealth")]
        [InlineData("\"baseHealth\": 50", "\"baseHealth\": 100001", "baseHealth")]
        [InlineData("\"damagePerPoint\": 0.5", "\"damagePerPoint\": 101", "damagePerPoint")]
        [InlineData("\"damagePerPoint\": 0.5", "\"damagePerPoint\": -0.1", "damagePerPoint")]
        [InlineData("\"id\": 2", "\"id\": 256", "id")]
        [InlineData("\"id\": 2", "\"id\": 0", "id")]
        [InlineData("\"primary\": \"str\"", "\"primary\": \"vit\"", "primary")]
        [InlineData("\"str\": 4", "\"str\": 1001", "str")]
        [InlineData("\"dex\": 3", "\"dex\": -1", "dex")]
        [InlineData("\"int\": 1", "\"int\": 1001", "int")]
        [InlineData("\"vit\": 10", "\"vit\": -1", "vit")]
        [InlineData("\"healthPerVit\": 5", "\"healthPerVit\": 0", "healthPerVit")]
        [InlineData("\"healthPerVit\": 5", "\"healthPerVit\": 1001", "healthPerVit")]
        [InlineData("\"defense\": 0", "\"defense\": 100001", "defense")]
        [InlineData("\"defense\": 0", "\"defense\": -1", "defense")]
        [InlineData("\"magicResist\": 0", "\"magicResist\": -1", "magicResist")]
        [InlineData("\"magicResist\": 0", "\"magicResist\": 100001", "magicResist")]
        [InlineData("\"base\": 100", "\"base\": 100001", "resource.base")]
        [InlineData("\"base\": 100", "\"base\": -1", "resource.base")]
        [InlineData("\"perInt\": 0", "\"perInt\": 1001", "perInt")]
        [InlineData("\"perInt\": 0", "\"perInt\": -1", "perInt")]
        [InlineData("\"damage\": 10", "\"damage\": 10001", "damage")]
        [InlineData("\"damage\": 10", "\"damage\": -1", "damage")]
        [InlineData("\"damageType\": \"physical\"", "\"damageType\": \"fire\"", "damageType")]
        [InlineData("\"range\": 1.2", "\"range\": 0.09", "range")]
        [InlineData("\"range\": 1.2", "\"range\": 5.1", "range")]
        [InlineData("\"cooldownSeconds\": 0.8", "\"cooldownSeconds\": 0.04", "cooldownSeconds")]
        [InlineData("\"cooldownSeconds\": 0.8", "\"cooldownSeconds\": 60.1", "cooldownSeconds")]
        public void Classes_Json_Rechaza_Campos_Fuera_De_Rango(string original, string replacement, string expected)
        {
            string json = ClassesJson.Replace(original, replacement);
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Load(json));
            Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Classes_Json_Rechaza_Id_Repetido_Y_Si_Falta_La_Clase_1()
        {
            string repeated = ClassesJson.Replace("\"id\": 2", "\"id\": 1");
            Assert.Contains("repetido", Assert.Throws<InvalidOperationException>(() => Load(repeated)).Message);

            string withoutWarrior = ClassesJson.Replace("\"id\": 1, \"name\": \"Guerrero\"", "\"id\": 5, \"name\": \"Otra\"");
            Assert.Contains("clase 1", Assert.Throws<InvalidOperationException>(() => Load(withoutWarrior)).Message);
        }

        [Fact]
        public void Classes_Json_Exige_Al_Menos_Una_Clase_Y_Acepta_Id_255()
        {
            Assert.Contains(
                "al menos una clase",
                Assert.Throws<InvalidOperationException>(() => Load(
                    "{ \"baseHealth\": 50, \"damagePerPoint\": 0.5, \"classes\": [] }")).Message);

            ClassCatalog catalog = Load(ClassesJson.Replace("\"id\": 4", "\"id\": 255"));
            Assert.NotNull(catalog.Find(255));
        }

        [Fact]
        public void Classes_Json_Rechaza_Base_Vacia_Y_Nombre_Mayor_De_24_Bytes_UTF8()
        {
            string missingBase = ClassesJson.Replace(
                "\"base\": { \"str\": 4, \"dex\": 3, \"int\": 1, \"vit\": 10 }, ",
                string.Empty);
            Assert.Contains("base", Assert.Throws<InvalidOperationException>(() => Load(missingBase)).Message);

            string longName = ClassesJson.Replace("Guerrero", "á" + new string('a', 23));
            Assert.Contains("24 bytes", Assert.Throws<InvalidOperationException>(() => Load(longName)).Message);
            string emptyName = ClassesJson.Replace("\"name\": \"Guerrero\"", "\"name\": \"\"");
            Assert.Contains("name", Assert.Throws<InvalidOperationException>(() => Load(emptyName)).Message);
        }

        [Fact]
        public void Classes_Json_Inexistente_Indica_El_Archivo()
        {
            string path = Path.Combine(Path.GetTempPath(), "no-existe-" + Guid.NewGuid().ToString("N") + ".json");
            Assert.Throws<FileNotFoundException>(() => ClassCatalog.Load(path));
        }

        [Theory]
        [InlineData(1, 100, 12, 100)]
        [InlineData(2, 80, 14, 92)]
        [InlineData(3, 90, 13, 100)]
        [InlineData(4, 90, 13, 88)]
        public void Atributos_Derivados_Respetan_Vida_Daño_Y_Recurso_Por_Clase(
            int classId,
            int maxHealth,
            int unarmedDamage,
            int maxResource)
        {
            ClassCatalog catalog = Load();
            DerivedCharacterStats stats = CharacterStats.Calculate(catalog, classId, 1, 0, 0, 0, 0);
            int damage = CharacterStats.GetAttackDamage(
                catalog, classId, 1, 0, 0, 0, 0, weaponDamage: null, out string damageType);

            Assert.Equal(maxHealth, stats.MaxHealth);
            Assert.Equal(unarmedDamage, damage);
            Assert.Equal(maxResource, stats.MaxResource);
            Assert.Equal(classId is 2 or 4 ? "magical" : "physical", damageType);
        }

        [Fact]
        public void Atributos_Repartidos_Aumentan_Vida_Y_Daño_Y_La_Espada_Escala_Con_El_Principal()
        {
            ClassCatalog catalog = Load();
            var vitalityPlayer = new PlayerEntity(1, 1, 8) { ClassId = 1, AllocVit = 4 };
            DerivedCharacterStats vitality = CharacterStats.Calculate(
                catalog,
                vitalityPlayer.ClassId,
                vitalityPlayer.Level,
                vitalityPlayer.AllocStr,
                vitalityPlayer.AllocDex,
                vitalityPlayer.AllocInt,
                vitalityPlayer.AllocVit);
            var strengthPlayer = new PlayerEntity(2, 2, 8) { ClassId = 1, AllocStr = 4 };
            int strengthDamage = CharacterStats.GetAttackDamage(
                catalog,
                strengthPlayer.ClassId,
                strengthPlayer.Level,
                strengthPlayer.AllocStr,
                strengthPlayer.AllocDex,
                strengthPlayer.AllocInt,
                strengthPlayer.AllocVit,
                weaponDamage: null,
                out _);
            int swordDamage = CharacterStats.GetAttackDamage(
                catalog, 1, 1, 0, 0, 0, 0, weaponDamage: 14, out string damageType);

            Assert.Equal(120, vitality.MaxHealth);
            Assert.Equal(14, strengthDamage);
            Assert.Equal(16, swordDamage);
            Assert.Equal("physical", damageType);
        }

        [Theory]
        [InlineData(10, 0, 10)]
        [InlineData(10, 20, 8)]
        [InlineData(10, 100, 5)]
        [InlineData(1, 100000, 1)]
        public void Mitigacion_Usa_Enteros_Y_Mantiene_Daño_Minimo(int damage, int defense, int expected)
        {
            Assert.Equal(expected, CharacterStats.MitigateDamage(damage, defense));
        }

        [Theory]
        [InlineData(20, 3, 1, 24)]
        [InlineData(20, 1, 1, 20)]
        [InlineData(20, 1, 6, 10)]
        [InlineData(20, 1, 21, 2)]
        [InlineData(1, 1, 21, 1)]
        [InlineData(0, 1, 1, 0)]
        [InlineData(20, 30, 1, 26)]
        public void Experiencia_De_Pnj_Se_Escala_Por_Diferencia_De_Nivel_Con_Decimas_Enteras(
            int experience,
            int npcLevel,
            int playerLevel,
            int expected)
        {
            Assert.Equal(expected, CharacterStats.ScaleExperience(experience, npcLevel, playerLevel));
        }

        [Fact]
        public void Puntos_Libres_Se_Derivan_Y_La_Asignacion_Se_Valida_Contra_El_Nivel()
        {
            Assert.Equal(8, CharacterStats.GetFreePoints(3, 5, 2, 1, 3, 1));
            Assert.True(CharacterStats.AreAllocationsValid(3, 2, 3, 1, 1, 1));
            Assert.False(CharacterStats.AreAllocationsValid(3, 2, 4, 1, 1, 1));
            Assert.False(CharacterStats.AreAllocationsValid(3, 2, -1, 0, 0, 0));
        }
    }
}
