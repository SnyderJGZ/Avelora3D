using System;
using System.IO;
using Avelora.Server.Items;
using Avelora.Server.Progression;
using Xunit;

namespace Avelora.Tests.Balance
{
    public sealed class CombatMathTests
    {
        [Fact]
        public void GetAttackDamage_Coincide_Con_CharacterStats_En_La_Rejilla_De_Clases()
        {
            string basePath = AppContext.BaseDirectory;
            ClassCatalog classes = ClassCatalog.Load(Path.Combine(basePath, "classes.json"));
            ItemCatalog items = ItemCatalog.Load(Path.Combine(basePath, "items.json"));
            int[] classIds = { 1, 2, 3, 4 };
            int[] levels = { 1, 5, 10, 25 };
            int[] weaponIds = { 3101, 3104, 3103, 3105 };

            for (int classIndex = 0; classIndex < classIds.Length; classIndex++)
            {
                for (int levelIndex = 0; levelIndex < levels.Length; levelIndex++)
                {
                    for (int allocationKind = 0; allocationKind < 2; allocationKind++)
                    {
                        int points = allocationKind == 0 ? 0 : levels[levelIndex] * 3;
                        for (int weaponKind = 0; weaponKind < 2; weaponKind++)
                        {
                            ItemType? weapon = weaponKind == 0 ? null : items.Find(weaponIds[classIndex]);
                            int? weaponDamage = weapon?.Damage;
                            string? weaponType = weapon?.DamageType;
                            string primary = classes.Find(classIds[classIndex])!.Primary;
                            int actual = CombatMath.GetAttackDamage(
                                classes, classIds[classIndex], levels[levelIndex],
                                primary == "str" ? points : 0,
                                primary == "dex" ? points : 0,
                                primary == "int" ? points : 0,
                                0, weaponDamage, out string actualType, items, null, weaponType);
                            int expected = CharacterStats.GetAttackDamage(
                                classes, classIds[classIndex], levels[levelIndex],
                                classes.Find(classIds[classIndex])!.Primary == "str" ? points : 0,
                                classes.Find(classIds[classIndex])!.Primary == "dex" ? points : 0,
                                classes.Find(classIds[classIndex])!.Primary == "int" ? points : 0,
                                0, weaponDamage, out string expectedType, items, null, weaponType);

                            Assert.Equal(expected, actual);
                            Assert.Equal(expectedType, actualType);
                        }
                    }
                }
            }
        }

        [Fact]
        public void MitigateDamage_Coincide_Con_El_Envoltorio_Y_Valida_Limites()
        {
            int[] defenses = { 0, 7, 100, 100_000 };
            for (int damage = 0; damage <= 50; damage++)
            {
                for (int i = 0; i < defenses.Length; i++)
                {
                    Assert.Equal(CharacterStats.MitigateDamage(damage, defenses[i]),
                        CombatMath.MitigateDamage(damage, defenses[i]));
                }
            }

            Assert.Throws<ArgumentOutOfRangeException>(() => CombatMath.MitigateDamage(-1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => CombatMath.MitigateDamage(1, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => CombatMath.MitigateDamage(1, 100_001));
        }

        [Theory]
        [InlineData(0.8, 16)]
        [InlineData(1.5, 30)]
        [InlineData(0.01, 1)]
        [InlineData(1.0, 20)]
        public void AttackCooldownTicks_Redondea_Al_Siguiente_Tick(double seconds, long expected)
        {
            Assert.Equal(expected, CombatMath.AttackCooldownTicks(seconds));
        }
    }
}
