#nullable enable
using System;
using Avelora.Server.Items;

namespace Avelora.Server.Progression
{
    /// <summary>Único sitio que deriva atributos, vida, recurso, daño y mitigación (I-08).</summary>
    public static class CharacterStats
    {
        public static DerivedCharacterStats Calculate(
            ClassCatalog catalog,
            int classId,
            int level,
            int allocStr,
            int allocDex,
            int allocInt,
            int allocVit,
            ItemCatalog? items = null,
            ushort[]? equipment = null)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            if (level < 1 || level > byte.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(level));
            }

            if (allocStr < 0 || allocDex < 0 || allocInt < 0 || allocVit < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(allocStr), "Los puntos repartidos no pueden ser negativos.");
            }

            CharacterClass characterClass = RequireClass(catalog, classId);
            CharacterAttributes baseAttributes = characterClass.Base!;
            int str = checked(baseAttributes.Str + allocStr);
            int dex = checked(baseAttributes.Dex + allocDex);
            int intelligence = checked(baseAttributes.Int + allocInt);
            int vit = checked(baseAttributes.Vit + allocVit);

            int defense = checked(characterClass.Defense + checked(level * characterClass.DefensePerLevel));
            int magicResist = checked(characterClass.MagicResist + checked(level * characterClass.MagicResistPerLevel));
            int moveSpeed = 0;
            if (equipment != null)
            {
                if (equipment.Length != Avelora.Shared.Protocol.ProtocolConstants.EquipSlotCount)
                {
                    throw new ArgumentException("El equipo debe contener exactamente diez huecos.", nameof(equipment));
                }

                for (int i = 0; i < equipment.Length; i++)
                {
                    ItemType? item = equipment[i] == 0 ? null : items?.Find(equipment[i]);
                    if (item == null)
                    {
                        continue;
                    }

                    str = checked(str + item.Str);
                    dex = checked(dex + item.Dex);
                    intelligence = checked(intelligence + item.Int);
                    vit = checked(vit + item.Vit);
                    defense = checked(defense + item.Defense);
                    magicResist = checked(magicResist + item.MagicResist);
                    moveSpeed = checked(moveSpeed + item.MoveSpeed);
                }
            }

            int maxHealth = checked(catalog.BaseHealth + checked(level * characterClass.HealthPerLevel) +
                checked(vit * characterClass.HealthPerVit));
            int maxResource = checked(characterClass.Resource!.Base + checked(intelligence * characterClass.Resource.PerInt));

            return new DerivedCharacterStats(
                str,
                dex,
                intelligence,
                vit,
                maxHealth,
                maxResource,
                defense,
                magicResist,
                CombatMath.CritChancePb(catalog, classId, dex),
                CombatMath.HastePb(catalog, dex),
                CombatMath.MoveSpeedPercent(moveSpeed, catalog.MoveCap));
        }

        /// <summary>Daño bruto de un ataque sin arma o de un arma física.</summary>
        public static int GetAttackDamage(
            ClassCatalog catalog,
            int classId,
            int level,
            int allocStr,
            int allocDex,
            int allocInt,
            int allocVit,
            int? weaponDamage,
            out string damageType,
            ItemCatalog? items = null,
            ushort[]? equipment = null,
            string? weaponDamageType = null)
        {
            // delega en CombatMath; se mantiene por compatibilidad con las pruebas
            return CombatMath.GetAttackDamage(catalog, classId, level, allocStr, allocDex, allocInt, allocVit,
                weaponDamage, out damageType, items, equipment, weaponDamageType);
        }

        /// <summary>Aplica la mitigación entera definida por la defensa o resistencia elegida.</summary>
        public static int MitigateDamage(int damage, int defenseOrResistance)
        {
            // delega en CombatMath; se mantiene por compatibilidad con las pruebas
            return CombatMath.MitigateDamage(damage, defenseOrResistance);
        }

        /// <summary>Puntos libres derivados; nunca se almacenan como una columna.</summary>
        public static int GetFreePoints(int pointsPerLevel, int level, int allocStr, int allocDex, int allocInt, int allocVit)
        {
            long earned = checked((long)pointsPerLevel * level);
            long allocated = (long)allocStr + allocDex + allocInt + allocVit;
            return checked((int)(earned - allocated));
        }

        /// <summary>Comprueba si los puntos guardados respetan el total ganado al nivel indicado.</summary>
        public static bool AreAllocationsValid(
            int pointsPerLevel,
            int level,
            int allocStr,
            int allocDex,
            int allocInt,
            int allocVit)
        {
            if (pointsPerLevel < 0 || level < 1 || allocStr < 0 || allocDex < 0 || allocInt < 0 || allocVit < 0)
            {
                return false;
            }

            long allocated = (long)allocStr + allocDex + allocInt + allocVit;
            return allocated <= (long)pointsPerLevel * level;
        }

        /// <summary>Normaliza la recompensa de un PNJ por diferencia de nivel, en décimas enteras.</summary>
        public static int ScaleExperience(int experience, int npcLevel, int playerLevel)
        {
            if (experience < 0 || npcLevel < 1 || playerLevel < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(experience));
            }

            if (experience == 0)
            {
                return 0;
            }

            long tenths = Math.Clamp(10L + npcLevel - playerLevel, 1L, 13L);
            long scaled = checked((long)experience * tenths) / 10L;
            return (int)Math.Max(1L, scaled);
        }

        internal static CharacterClass RequireClass(ClassCatalog catalog, int classId) =>
            catalog.Find(classId) ?? throw new InvalidOperationException(
                "No existe la clase " + classId + " en classes.json.");
    }

    /// <summary>Estadísticas derivadas de la clase y los puntos repartidos.</summary>
    public readonly struct DerivedCharacterStats
    {
        public DerivedCharacterStats(
            int str,
            int dex,
            int intelligence,
            int vit,
            int maxHealth,
            int maxResource,
            int defense,
            int magicResist,
            int critChancePb = 0,
            int hastePb = 0,
            int moveSpeedPercent = 100)
        {
            Str = str;
            Dex = dex;
            Int = intelligence;
            Vit = vit;
            MaxHealth = maxHealth;
            MaxResource = maxResource;
            Defense = defense;
            MagicResist = magicResist;
            CritChancePb = critChancePb;
            HastePb = hastePb;
            MoveSpeedPercent = moveSpeedPercent;
        }

        public int Str { get; }
        public int Dex { get; }
        public int Int { get; }
        public int Vit { get; }
        public int MaxHealth { get; }
        public int MaxResource { get; }
        public int Defense { get; }
        public int MagicResist { get; }
        /// <summary>Probabilidad crítica en puntos básicos.</summary>
        public int CritChancePb { get; }
        /// <summary>Prisa en puntos básicos.</summary>
        public int HastePb { get; }
        /// <summary>Porcentaje de velocidad de movimiento aportado por el equipo, con el tope de clase aplicado.</summary>
        public int MoveSpeedPercent { get; }
    }
}
