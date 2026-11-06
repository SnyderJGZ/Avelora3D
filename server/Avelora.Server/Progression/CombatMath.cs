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

        /// <summary>Probabilidad crítica en puntos básicos según la destreza efectiva.</summary>
        public static int CritChancePb(ClassCatalog catalog, int classId, int dex)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            CharacterClass characterClass = CharacterStats.RequireClass(catalog, classId);
            return Math.Min(catalog.CritCap, checked(characterClass.CritBase + checked(dex * catalog.CritPerDex)));
        }

        /// <summary>Prisa en puntos básicos según la destreza efectiva.</summary>
        public static int HastePb(ClassCatalog catalog, int dex)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            return Math.Min(catalog.HasteCap, checked(dex * catalog.HastePerDex));
        }

        /// <summary>Calcula el porcentaje de velocidad del equipo después de aplicar el tope de clase.</summary>
        public static int MoveSpeedPercent(int equipMoveSpeed, int moveCap) =>
            Math.Min(moveCap, checked(100 + equipMoveSpeed));

        /// <summary>Convierte la recarga del arma a ticks después de aplicar la prisa y el mínimo.</summary>
        public static long AttackCooldownTicks(double cooldownSeconds, int hastePb, int minTicks)
        {
            if (hastePb < 0) throw new ArgumentOutOfRangeException(nameof(hastePb));
            long baseTicks = AttackCooldownTicks(cooldownSeconds);
            long denominator = 10000L + hastePb;
            long adjusted = checked((checked(baseTicks * 10000L) + denominator - 1L) / denominator);
            return Math.Max(minTicks, adjusted);
        }

        /// <summary>Calcula el daño crítico con redondeo entero hacia abajo.</summary>
        public static int CritDamage(int rawDamage, int multiplierPercent) =>
            checked((int)(checked((long)rawDamage * multiplierPercent) / 100L));

        /// <summary>Aplica el porcentaje de daño PvP con mínimo de un punto.</summary>
        public static int PvpDamage(int mitigated, int pvpPercent) =>
            (int)Math.Max(1L, checked((long)mitigated * pvpPercent) / 100L);

        /// <summary>Calcula el daño medio de un golpe antes de mitigar.</summary>
        public static double ExpectedDamage(int rawDamage, int critChancePb, int multiplierPercent) =>
            rawDamage + critChancePb / 10000.0 * (CritDamage(rawDamage, multiplierPercent) - rawDamage);

        /// <summary>Calcula el daño esperado de un golpe tras mitigación y, si procede, reducción PvP.</summary>
        public static double ExpectedHit(int rawDamage, int critChancePb, int multiplierPercent,
            int defense, int pvpPercent = 100)
        {
            int normal = MitigateDamage(rawDamage, defense);
            int critical = MitigateDamage(CritDamage(rawDamage, multiplierPercent), defense);
            if (pvpPercent < 100)
            {
                normal = PvpDamage(normal, pvpPercent);
                critical = PvpDamage(critical, pvpPercent);
            }
            double probability = critChancePb / 10000.0;
            return (1d - probability) * normal + probability * critical;
        }
    }
}
