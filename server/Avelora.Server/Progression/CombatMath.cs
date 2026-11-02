#nullable enable
using System;
using Avelora.Server.Items;
using Avelora.Shared;
using Avelora.Shared.Protocol;

namespace Avelora.Server.Progression
{
    /// <summary>Fórmulas de daño, mitigación y recarga compartidas por el servidor y el simulador.</summary>
    public static class CombatMath
    {
        /// <summary>Calcula el daño bruto de un ataque con las estadísticas y el arma indicados.</summary>
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
            string? weaponDamageType = null,
            int bonus = 0)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            if (bonus < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bonus));
            }

            CharacterClass characterClass = CharacterStats.RequireClass(catalog, classId);
            DerivedCharacterStats stats = CharacterStats.Calculate(
                catalog, classId, level, allocStr, allocDex, allocInt, allocVit, items, equipment);
            int baseDamage = checked((weaponDamage ?? characterClass.Unarmed!.Damage) + bonus);
            damageType = weaponDamage.HasValue
                ? weaponDamageType ?? "physical"
                : characterClass.Unarmed!.DamageType;

            int primary = characterClass.Primary == "str"
                ? stats.Str
                : characterClass.Primary == "dex"
                    ? stats.Dex
                    : stats.Int;
            return checked((int)((long)baseDamage * (100L + (long)primary * catalog.PowerPerPoint) / 100L));
        }

        /// <summary>Calcula el daño de una habilidad según su potencia y el porcentaje del arma.</summary>
        public static int GetSkillAmount(
            ClassCatalog catalog,
            int classId,
            int level,
            int allocStr,
            int allocDex,
            int allocInt,
            int allocVit,
            int skillAmount,
            int weaponPercent,
            int? weaponDamage,
            ItemCatalog? items = null,
            ushort[]? equipment = null,
            int bonus = 0)
        {
            CharacterClass characterClass = CharacterStats.RequireClass(catalog, classId);
            int weaponPart = checked((weaponDamage ?? characterClass.Unarmed!.Damage) * weaponPercent) / 100;
            return GetAttackDamage(catalog, classId, level, allocStr, allocDex, allocInt, allocVit,
                checked(skillAmount + weaponPart), out _, items, equipment, null, bonus);
        }

        /// <summary>Aplica la mitigación entera definida por la defensa o resistencia elegida.</summary>
        public static int MitigateDamage(int damage, int defenseOrResistance)
        {
            if (damage < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(damage));
            }

            if (defenseOrResistance < 0 || defenseOrResistance > 100_000)
            {
                throw new ArgumentOutOfRangeException(nameof(defenseOrResistance));
            }

            long mitigated = checked((long)damage * 100L) / (100L + defenseOrResistance);
            return (int)Math.Max(1L, mitigated);
        }

        /// <summary>Convierte segundos de recarga a ticks, redondeando hacia arriba y con mínimo de uno.</summary>
        public static long AttackCooldownTicks(double cooldownSeconds) =>
            Math.Max(1L, (long)Math.Ceiling(cooldownSeconds * SimulationConstants.TickRate));
    }
}
