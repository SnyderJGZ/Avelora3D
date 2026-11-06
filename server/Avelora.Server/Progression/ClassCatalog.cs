#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Avelora.Server.Items;
using Avelora.Shared.Protocol;

namespace Avelora.Server.Progression
{
    /// <summary>Catálogo de clases de personaje cargado desde <c>classes.json</c>.</summary>
    public sealed class ClassCatalog
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        /// <summary>Valores iniciales usados por las zonas de prueba sin configuración explícita.</summary>
        public static ClassCatalog Default => Load(Path.Combine(AppContext.BaseDirectory, "classes.json"));

        public int BaseHealth { get; set; } = -1;
        public int PowerPerPoint { get; set; } = -1;
        public int MoveCap { get; set; } = 100;
        public int CritPerDex { get; set; }
        public int CritCap { get; set; } = 4000;
        public int CritMultiplier { get; set; } = 150;
        public int HastePerDex { get; set; }
        public int HasteCap { get; set; } = 3000;
        public int MinAttackTicks { get; set; } = 8;
        public int PvpDamagePercent { get; set; } = 100;
        public List<CharacterClass>? Classes { get; set; }

        /// <summary>Carga y valida el catálogo de clases.</summary>
        public static ClassCatalog Load(string path, LevelTable? levels = null)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("La ruta de classes.json no puede estar vacía.", nameof(path));
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException("No se encuentra el archivo de clases '" + path + "'.", path);
            }

            ClassCatalog catalog = JsonSerializer.Deserialize<ClassCatalog>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidOperationException("classes.json está vacío o no contiene un objeto.");
            catalog.Validate(levels);
            return catalog;
        }

        /// <summary>Valida las reglas comunes y cada definición de clase.</summary>
        public void Validate(LevelTable? levels = null)
        {
            if (BaseHealth < 1 || BaseHealth > 100_000)
            {
                throw new InvalidOperationException("classes.json: baseHealth debe estar entre 1 y 100000.");
            }

            if (PowerPerPoint < 0 || PowerPerPoint > 100)
            {
                throw new InvalidOperationException("classes.json: powerPerPoint debe estar entre 0 y 100.");
            }

            if (MoveCap < 100 || MoveCap > 200)
            {
                throw new InvalidOperationException("classes.json: moveCap debe estar entre 100 y 200.");
            }

            ValidateCatalogValue(CritPerDex, 0, 1000, "critPerDex");
            ValidateCatalogValue(CritCap, 0, 10000, "critCap");
            ValidateCatalogValue(CritMultiplier, 100, 1000, "critMultiplier");
            ValidateCatalogValue(HastePerDex, 0, 1000, "hastePerDex");
            ValidateCatalogValue(HasteCap, 0, 10000, "hasteCap");
            ValidateCatalogValue(MinAttackTicks, 1, 200, "minAttackTicks");
            ValidateCatalogValue(PvpDamagePercent, 1, 100, "pvpDamagePercent");

            if (Classes == null || Classes.Count == 0)
            {
                throw new InvalidOperationException("classes.json: classes debe contener al menos una clase.");
            }

            levels ??= LevelTable.Default;
            bool hasWarrior = false;
            for (int i = 0; i < Classes.Count; i++)
            {
                CharacterClass? characterClass = Classes[i];
                string where = "classes[" + i + "]";
                if (characterClass == null)
                {
                    throw new InvalidOperationException("classes.json: " + where + " está vacío.");
                }

                if (characterClass.Id < 1 || characterClass.Id > byte.MaxValue)
                {
                    throw new InvalidOperationException("classes.json: " + where + ".id debe estar entre 1 y 255.");
                }

                for (int j = 0; j < i; j++)
                {
                    if (Classes[j] != null && Classes[j]!.Id == characterClass.Id)
                    {
                        throw new InvalidOperationException(
                            "classes.json: el id " + characterClass.Id + " está repetido.");
                    }
                }

                hasWarrior |= characterClass.Id == 1;
                if (string.IsNullOrWhiteSpace(characterClass.Name) ||
                    Encoding.UTF8.GetByteCount(characterClass.Name) > 24)
                {
                    throw new InvalidOperationException(
                        "classes.json: " + where + ".name no puede estar vacío y debe ocupar como máximo 24 bytes UTF-8.");
                }

                if (characterClass.Primary != "str" && characterClass.Primary != "dex" &&
                    characterClass.Primary != "int")
                {
                    throw new InvalidOperationException(
                        "classes.json: " + where + ".primary debe ser str, dex o int.");
                }

                ValidateAttributes(characterClass.Base, where + ".base");
                if (characterClass.HealthPerVit < 1 || characterClass.HealthPerVit > 1000)
                {
                    throw new InvalidOperationException(
                        "classes.json: " + where + ".healthPerVit debe estar entre 1 y 1000.");
                }

                if (!InRange(characterClass.CritBase, 0, 10000))
                {
                    throw new InvalidOperationException(
                        "classes.json: " + where + ".critBase debe estar entre 0 y 10000.");
                }

                if (characterClass.HealthPerLevel < 0 || characterClass.HealthPerLevel > 1000)
                {
                    throw new InvalidOperationException(
                        "classes.json: " + where + ".healthPerLevel debe estar entre 0 y 1000.");
                }

                if (characterClass.DefensePerLevel < 0 || characterClass.DefensePerLevel > 1000)
                {
                    throw new InvalidOperationException(
                        "classes.json: " + where + ".defensePerLevel debe estar entre 0 y 1000.");
                }

                if (characterClass.MagicResistPerLevel < 0 || characterClass.MagicResistPerLevel > 1000)
                {
                    throw new InvalidOperationException(
                        "classes.json: " + where + ".magicResistPerLevel debe estar entre 0 y 1000.");
                }

                ValidateDefense(characterClass.Defense, where + ".defense");
                ValidateDefense(characterClass.MagicResist, where + ".magicResist");
                ValidateResource(characterClass.Resource, where + ".resource");
                // Todos los puntos ganados (incluido nivel 1) pueden asignarse a inteligencia.
                long maxInt = characterClass.Base!.Int + (long)levels.PointsPerLevel * levels.MaxLevel;
                long maxResource = characterClass.Resource!.Base + maxInt * characterClass.Resource.PerInt;
                if (maxResource > ushort.MaxValue)
                {
                    throw new InvalidOperationException(
                        "classes.json: " + where + ".resource puede alcanzar " + maxResource + " al nivel máximo y el tope es 65535.");
                }
                ValidateUnarmed(characterClass.Unarmed, where + ".unarmed");
                ValidateWeaponTypes(characterClass.WeaponTypes, where + ".weaponTypes");
            }

            if (!hasWarrior)
            {
                throw new InvalidOperationException("classes.json: debe existir la clase 1.");
            }

            int maxCharacterListSize = 4 +
                ProtocolConstants.MaxCharactersPerAccount * (8 + ProtocolConstants.MaxCharacterNameBytes);
            for (int i = 0; i < Classes.Count; i++)
            {
                maxCharacterListSize += 3 + Encoding.UTF8.GetByteCount(Classes[i]!.Name);
            }

            if (maxCharacterListSize > ProtocolConstants.MaxMessageSize)
            {
                throw new InvalidOperationException(
                    "classes.json: CharacterList podría ocupar " + maxCharacterListSize +
                    " bytes y superar el tope de " + ProtocolConstants.MaxMessageSize + ".");
            }
        }

        /// <summary>Comprueba que cada tipo admitido por una clase lo use algún arma del catálogo.</summary>
        public void ValidateWeaponTypes(ItemCatalog items)
        {
            if (items == null)
            {
                throw new ArgumentNullException(nameof(items));
            }

            if (Classes == null)
            {
                return;
            }

            for (int i = 0; i < Classes.Count; i++)
            {
                CharacterClass characterClass = Classes[i]!;
                for (int typeIndex = 0; typeIndex < characterClass.WeaponTypes!.Count; typeIndex++)
                {
                    string weaponType = characterClass.WeaponTypes[typeIndex];
                    bool found = false;
                    for (int itemIndex = 0; itemIndex < items.Items.Count; itemIndex++)
                    {
                        ItemType item = items.Items[itemIndex];
                        if (item != null && item.IsWeapon &&
                            string.Equals(item.WeaponType, weaponType, StringComparison.OrdinalIgnoreCase))
                        {
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                    {
                        throw new InvalidOperationException(
                            "classes.json: classes[" + i + "].weaponTypes contiene '" + weaponType + "', que ningún arma de items.json usa.");
                    }
                }
            }
        }

        /// <summary>Busca una clase por id.</summary>
        public CharacterClass? Find(int id)
        {
            if (Classes == null)
            {
                return null;
            }

            for (int i = 0; i < Classes.Count; i++)
            {
                if (Classes[i] != null && Classes[i]!.Id == id)
                {
                    return Classes[i];
                }
            }

            return null;
        }

        private static void ValidateAttributes(CharacterAttributes? attributes, string where)
        {
            if (attributes == null || !InRange(attributes.Str, 0, 1000) ||
                !InRange(attributes.Dex, 0, 1000) || !InRange(attributes.Int, 0, 1000) ||
                !InRange(attributes.Vit, 0, 1000))
            {
                throw new InvalidOperationException(
                    "classes.json: " + where + " requiere str, dex, int y vit entre 0 y 1000.");
            }
        }

        private static void ValidateDefense(int value, string where)
        {
            if (!InRange(value, 0, 100_000))
            {
                throw new InvalidOperationException("classes.json: " + where + " debe estar entre 0 y 100000.");
            }
        }

        private static void ValidateResource(CharacterResource? resource, string where)
        {
            if (resource == null || !InRange(resource.Base, 0, 100_000) ||
                !InRange(resource.PerInt, 0, 1000) ||
                !InRange(resource.RegenPerSecond, 0, 1000))
            {
                throw new InvalidOperationException(
                    "classes.json: " + where + ".base debe estar entre 0 y 100000, perInt entre 0 y 1000 y regenPerSecond entre 0 y 1000.");
            }
        }

        private static void ValidateUnarmed(UnarmedAttack? unarmed, string where)
        {
            if (unarmed == null || !InRange(unarmed.Damage, 0, 10_000))
            {
                throw new InvalidOperationException("classes.json: " + where + ".damage debe estar entre 0 y 10000.");
            }

            if (unarmed.DamageType != "physical" && unarmed.DamageType != "magical")
            {
                throw new InvalidOperationException(
                    "classes.json: " + where + ".damageType debe ser physical o magical.");
            }

            if (!(unarmed.Range >= 0.1f) || unarmed.Range > 5f ||
                float.IsNaN(unarmed.Range) || float.IsInfinity(unarmed.Range))
            {
                throw new InvalidOperationException("classes.json: " + where + ".range debe estar entre 0,1 y 5.");
            }

            if (!(unarmed.CooldownSeconds >= 0.05d) || unarmed.CooldownSeconds > 60d ||
                double.IsNaN(unarmed.CooldownSeconds) || double.IsInfinity(unarmed.CooldownSeconds))
            {
                throw new InvalidOperationException(
                    "classes.json: " + where + ".cooldownSeconds debe estar entre 0,05 y 60.");
            }
        }

        private static void ValidateWeaponTypes(List<string>? weaponTypes, string where)
        {
            if (weaponTypes == null)
            {
                throw new InvalidOperationException("classes.json: " + where + " es obligatorio.");
            }

            if (weaponTypes.Count > 8)
            {
                throw new InvalidOperationException("classes.json: " + where + " no puede contener más de 8 tipos.");
            }

            for (int i = 0; i < weaponTypes.Count; i++)
            {
                string? weaponType = weaponTypes[i];
                int bytes = string.IsNullOrEmpty(weaponType) ? 0 : Encoding.UTF8.GetByteCount(weaponType);
                if (bytes < 1 || bytes > 16 ||
                    !string.Equals(weaponType, weaponType!.Trim(), StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "classes.json: " + where + "[" + i + "] debe ocupar de 1 a 16 bytes UTF-8 y no tener espacios en los bordes.");
                }

                for (int previous = 0; previous < i; previous++)
                {
                    if (string.Equals(weaponTypes[previous], weaponType, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("classes.json: " + where + " contiene tipos repetidos.");
                    }
                }
            }
        }

        private static bool InRange(int value, int min, int max) => value >= min && value <= max;

        private static void ValidateCatalogValue(int value, int min, int max, string field)
        {
            if (!InRange(value, min, max))
            {
                throw new InvalidOperationException(
                    "classes.json: " + field + " debe estar entre " + min + " y " + max + ".");
            }
        }
    }

    public sealed class CharacterClass
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Primary { get; set; } = string.Empty;
        public CharacterAttributes? Base { get; set; }
        public int HealthPerLevel { get; set; }
        public int HealthPerVit { get; set; } = -1;
        public int CritBase { get; set; }
        public int Defense { get; set; }
        public int DefensePerLevel { get; set; }
        public int MagicResist { get; set; }
        public int MagicResistPerLevel { get; set; }
        public CharacterResource? Resource { get; set; }
        public UnarmedAttack? Unarmed { get; set; }
        public List<string>? WeaponTypes { get; set; }
    }

    public sealed class CharacterAttributes
    {
        public int Str { get; set; } = -1;
        public int Dex { get; set; } = -1;
        public int Int { get; set; } = -1;
        public int Vit { get; set; } = -1;
    }

    public sealed class CharacterResource
    {
        public string Name { get; set; } = string.Empty;
        public int Base { get; set; } = -1;
        public int PerInt { get; set; } = -1;
        public int RegenPerSecond { get; set; } = 0;
    }

    public sealed class UnarmedAttack
    {
        public int Damage { get; set; } = -1;
        public string DamageType { get; set; } = string.Empty;
        public float Range { get; set; } = float.NaN;
        public double CooldownSeconds { get; set; } = double.NaN;
    }
}
