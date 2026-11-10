using System;
using System.Globalization;
using System.IO;
using Avelora.Balance;
using Avelora.Server.Items;
using Avelora.Server.Npcs;
using Avelora.Server.Progression;
using Avelora.Shared.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Avelora.Tests.Balance
{
    public sealed class BalanceObjectivesTests
    {
        private static readonly int[] ClassIds = { 1, 2, 3, 4 };
        private readonly ITestOutputHelper _output;

        public BalanceObjectivesTests(ITestOutputHelper output) => _output = output;

        private static CombatSimulator Simulator(NpcCatalog? npcs = null)
        {
            string path = AppContext.BaseDirectory;
            ClassCatalog classes = ClassCatalog.Load(Path.Combine(path, "classes.json"));
            ItemCatalog items = ItemCatalog.Load(Path.Combine(path, "items.json"));
            SkillCatalog skills = SkillCatalog.Load(Path.Combine(path, "skills.json"), classes);
            LevelTable levels = LevelTable.Load(Path.Combine(path, "levels.json"), items);
            return new CombatSimulator(classes, items, skills,
                npcs ?? NpcCatalog.Load(Path.Combine(path, "npcs.json")), levels);
        }

        [Theory]
        [InlineData(1, "puro")]
        [InlineData(1, "equilibrado")]
        [InlineData(5, "puro")]
        [InlineData(5, "equilibrado")]
        [InlineData(10, "puro")]
        [InlineData(10, "equilibrado")]
        [InlineData(20, "puro")]
        [InlineData(20, "equilibrado")]
        public void Objetivo_3_Dps_Coincide_Con_La_Tabla_Y_Respeta_Las_Bandas(int level, string build)
        {
            CombatSimulator simulator = Simulator();
            ExpectedRow expected = Expected(level, build);
            var results = new CombatSimulationResult[4];
            for (int i = 0; i < ClassIds.Length; i++)
            {
                results[i] = simulator.Simulate(ClassIds[i], level, build, "franja", false);
                Assert.Equal(expected.Crit[i], results[i].CritChancePb);
                Assert.Equal(expected.Haste[i], results[i].HastePb);
                Assert.Equal(expected.Cooldown[i], results[i].BasicCooldownTicks);
                Assert.InRange(results[i].DamagePerSecond, expected.Dps[i] - 0.03, expected.Dps[i] + 0.03);
                _output.WriteLine("Nivel {0}, {1}, clase {2}: DPS={3:0.00} (esperado {4:0.00}), crit={5}, prisa={6}, recarga={7}",
                    level, build, results[i].ClassName, results[i].DamagePerSecond,
                    expected.Dps[i], results[i].CritChancePb, results[i].HastePb,
                    results[i].BasicCooldownTicks);
            }

            double average = (results[0].DamagePerSecond + results[1].DamagePerSecond + results[2].DamagePerSecond) / 3d;
            for (int i = 0; i < 3; i++)
                Assert.InRange(results[i].DamagePerSecond, average * 0.9, average * 1.1);
            Assert.InRange(results[3].DamagePerSecond / average, 0.65, 0.80);
            _output.WriteLine("Nivel {0}, {1}: media guerrero/mago/ranger={2:0.00}; sanador/media={3:0.0}%",
                level, build, average, results[3].DamagePerSecond / average * 100d);
        }

        [Theory]
        [InlineData(1, 5.65, 31.9)]
        [InlineData(2, 4.45, 39.1)]
        [InlineData(3, 5.05, 42.5)]
        public void Objetivo_1_Orco_De_Nivel_Uno_Coincide_Con_La_Referencia(
            int classId, double expectedTtk, double expectedLost)
        {
            CombatSimulationResult result = Simulator().Simulate(classId, 1, "equilibrado", "franja", false);
            NpcCombatResult npc = FindNpc(result, 2001);
            Assert.InRange(npc.TimeToKillSeconds!.Value, expectedTtk - 0.1, expectedTtk + 0.1);
            Assert.InRange(npc.HealthLostPercent, expectedLost - 0.5, expectedLost + 0.5);
            Assert.InRange(npc.TimeToKillSeconds.Value, 4.0, 6.0);
            Assert.InRange(npc.HealthLostPercent, 30, 50);
            WriteNpc(result, npc, expectedTtk, expectedLost);
        }

        [Theory]
        [InlineData(1, 4.85, 33.7)]
        [InlineData(2, 4.45, 43.2)]
        [InlineData(3, 5.05, 43.6)]
        public void Objetivo_1_Referencia_De_Nivel_Cinco_Coincide_Con_La_Tabla(
            int classId, double expectedTtk, double expectedLost)
        {
            var npcs = new NpcCatalog
            {
                Types =
                {
                    new NpcType
                    {
                        Id = 9005, Name = "Referencia nivel 5", Level = 5, MaxHealth = 105,
                        AttackDamage = 22, AttackCooldownSeconds = 1.5f, Defense = 5, MagicResist = 0,
                    },
                },
            };
            CombatSimulationResult result = Simulator(npcs).Simulate(classId, 5, "equilibrado", "franja", false);
            NpcCombatResult npc = FindNpc(result, 9005);
            Assert.InRange(npc.TimeToKillSeconds!.Value, expectedTtk - 0.1, expectedTtk + 0.1);
            Assert.InRange(npc.HealthLostPercent, expectedLost - 0.5, expectedLost + 0.5);
            Assert.InRange(npc.TimeToKillSeconds.Value, 4.0, 6.0);
            Assert.InRange(npc.HealthLostPercent, 30, 50);
            WriteNpc(result, npc, expectedTtk, expectedLost);
        }

        [Fact]
        public void Objetivo_1_Niveles_10_Y_20_Informe()
        {
            var npcs = new NpcCatalog
            {
                Types =
                {
                    new NpcType { Id = 9010, Name = "Referencia nivel 10", Level = 10,
                        MaxHealth = 147, AttackDamage = 34, AttackCooldownSeconds = 1.5f, Defense = 10 },
                    new NpcType { Id = 9020, Name = "Referencia nivel 20", Level = 20,
                        MaxHealth = 289, AttackDamage = 66, AttackCooldownSeconds = 1.5f, Defense = 20 },
                },
            };
            CombatSimulator simulator = Simulator(npcs);
            foreach (int level in new[] { 10, 20 })
            {
                int npcId = level == 10 ? 9010 : 9020;
                foreach (int classId in new[] { 1, 2, 3 })
                {
                    CombatSimulationResult result = simulator.Simulate(classId, level, "equilibrado", "franja", false);
                    NpcCombatResult npc = FindNpc(result, npcId);
                    Assert.True(npc.TimeToKillSeconds is null || npc.TimeToKillSeconds > 0);
                    _output.WriteLine("Solo informe: nivel {0}, {1}: DPS={2:0.00}, TTK={3}, vida perdida={4:0.0}%",
                        level, result.ClassName, result.DamagePerSecond,
                        npc.TimeToKillSeconds?.ToString("0.00", CultureInfo.InvariantCulture) ?? "sin matar",
                        npc.HealthLostPercent);
                }
            }
        }

        [Fact]
        public void Objetivo_7_Duelos_Informe()
        {
            CombatSimulator simulator = Simulator();
            for (int levelIndex = 0; levelIndex < 4; levelIndex++)
            {
                int level = new[] { 1, 5, 10, 20 }[levelIndex];
                for (int aIndex = 0; aIndex < ClassIds.Length; aIndex++)
                {
                    for (int bIndex = aIndex + 1; bIndex < ClassIds.Length; bIndex++)
                    {
                        int a = ClassIds[aIndex];
                        int b = ClassIds[bIndex];
                        DuelResult duel = simulator.SimulateDuel(a, b, level, "equilibrado", "franja");
                        DuelResult reverse = simulator.SimulateDuel(b, a, level, "equilibrado", "franja");
                        Assert.Equal(duel.SecondsToFirstKill, reverse.SecondsToFirstKill, 9);
                        Assert.Equal(duel.WinnerClassId, reverse.WinnerClassId);
                        if (level == 1 && a == 1 && b == 2)
                        {
                            Assert.Equal(11.25, duel.SecondsToFirstKill, 2);
                            Assert.Equal(1, duel.WinnerClassId);
                        }
                        if (level == 1 && a == 1 && b == 3)
                        {
                            Assert.Equal(14.45, duel.SecondsToFirstKill, 2);
                            Assert.Equal(1, duel.WinnerClassId);
                        }
                        if (level == 1 && a == 2 && b == 3)
                        {
                            Assert.Equal(12.05, duel.SecondsToFirstKill, 2);
                            Assert.Equal(3, duel.WinnerClassId);
                        }
                        _output.WriteLine("| {0} | {1} | {2} | {3} | {4:0.00} | {5:0.00} |",
                            level, a, b, duel.WinnerClassId, duel.SecondsToFirstKill, duel.KillTimeRatio);
                    }
                }
            }

            DuelResult firstDuel = simulator.SimulateDuel(1, 2, 1, "equilibrado", "franja");
            Assert.Equal(11.25, firstDuel.SecondsToFirstKill, 2);
            Assert.Equal(1, firstDuel.WinnerClassId);
            Assert.Equal(6.252, firstDuel.ExpectedHitA, 3);
            Assert.Equal(9.336, firstDuel.ExpectedHitB, 3);
            Assert.Equal(0, simulator.SimulateDuel(1, 1, 1, "equilibrado", "franja").WinnerClassId);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(5)]
        [InlineData(10)]
        [InlineData(20)]
        public void Objetivo_8_Rotacion(int level)
        {
            CombatSimulator simulator = Simulator();
            foreach (int classId in ClassIds)
            {
                RotationResult rotation = simulator.MeasureRotation(classId, level, "equilibrado", "franja");
                foreach (SkillShare skill in rotation.Skills)
                {
                    string status = skill.Percent < 10d || skill.Percent > 35d ? "FUERA DE BANDA" : "OK";
                    _output.WriteLine("| {0} | {1} | {2} | {3} | {4:0.00}% | {5} |",
                        level, classId, skill.Name, skill.Casts, skill.Percent, status);
                    if (level == 20 && skill.SkillId == 4016)
                    {
                        // Lluvia de flechas: área medida contra un solo objetivo; con daño 40, arma 400 % y recarga 45 s (topes de §9.1) no llega al 10 %; decisión del usuario.
                        Assert.InRange(skill.Percent, 8d, 35d);
                    }
                    else
                    {
                        Assert.InRange(skill.Percent, 10d, 35d);
                    }
                }
            }
        }

        [Fact]
        public void Objetivo_7_Duelos_Con_Habilidades_Informe()
        {
            CombatSimulator simulator = Simulator();
            foreach (int level in new[] { 1, 5, 10, 20 })
                for (int aIndex = 0; aIndex < ClassIds.Length; aIndex++)
                    for (int bIndex = aIndex + 1; bIndex < ClassIds.Length; bIndex++)
                    {
                        DuelSeriesResult duel = simulator.SimulateDuels(ClassIds[aIndex], ClassIds[bIndex],
                            level, "equilibrado", "franja", 200, 1);
                        _output.WriteLine("| {0} | {1} | {2} | {3} | {4} | {5} | {6:0.00} | {7:0.00} |",
                            level, ClassIds[aIndex], ClassIds[bIndex], duel.WinsA, duel.WinsB,
                            duel.Draws, duel.MeanSeconds, duel.MaxWinRate);
                    }
        }

        private static ExpectedRow Expected(int level, string build)
        {
            if (level == 1 && build == "puro") return new ExpectedRow(
                new[] { 14.28, 15.16, 14.63, 11.16 }, new[] { 840, 840, 900, 330 },
                new[] { 60, 60, 150, 45 }, new long[] { 16, 22, 20, 20 });
            if (level == 1) return new ExpectedRow(
                new[] { 14.28, 15.16, 14.62, 10.16 }, new[] { 840, 840, 890, 330 },
                new[] { 60, 60, 135, 45 }, new long[] { 16, 22, 20, 20 });
            if (level == 5 && build == "puro") return new ExpectedRow(
                new[] { 21.67, 20.84, 21.02, 16.26 }, new[] { 840, 840, 1020, 330 },
                new[] { 60, 60, 330, 45 }, new long[] { 24, 22, 20, 20 });
            if (level == 5) return new ExpectedRow(
                new[] { 19.94, 19.85, 18.87, 15.23 }, new[] { 840, 840, 970, 330 },
                new[] { 60, 60, 255, 45 }, new long[] { 24, 22, 20, 20 });
            if (level == 10 && build == "puro") return new ExpectedRow(
                new[] { 26.05, 25.54, 26.74, 19.30 }, new[] { 840, 840, 1170, 330 },
                new[] { 60, 60, 555, 45 }, new long[] { 24, 22, 19, 20 });
            if (level == 10) return new ExpectedRow(
                new[] { 23.41, 22.73, 22.07, 17.26 }, new[] { 840, 840, 1070, 330 },
                new[] { 60, 60, 405, 45 }, new long[] { 24, 22, 20, 20 });
            if (level == 20 && build == "puro") return new ExpectedRow(
                new[] { 35.57, 34.10, 36.16, 26.43 }, new[] { 840, 840, 1470, 330 },
                new[] { 60, 60, 1005, 45 }, new long[] { 24, 22, 19, 20 });
            return new ExpectedRow(new[] { 29.52, 28.42, 30.16, 21.33 },
                new[] { 840, 840, 1270, 330 }, new[] { 60, 60, 705, 45 },
                new long[] { 24, 22, 19, 20 });
        }

        private static NpcCombatResult FindNpc(CombatSimulationResult result, int id)
        {
            foreach (NpcCombatResult npc in result.Npcs)
                if (npc.NpcId == id) return npc;
            throw new InvalidOperationException("Falta el PNJ de referencia " + id + ".");
        }

        private void WriteNpc(CombatSimulationResult result, NpcCombatResult npc,
            double expectedTtk, double expectedLost) =>
            _output.WriteLine("Objetivo 1 nivel {0}, {1}: TTK={2:0.00} s (esperado {3:0.00}), vida perdida={4:0.0}% (esperado {5:0.0}%)",
                result.Level, result.ClassName, npc.TimeToKillSeconds, expectedTtk,
                npc.HealthLostPercent, expectedLost);

        private sealed class ExpectedRow
        {
            public ExpectedRow(double[] dps, int[] crit, int[] haste, long[] cooldown)
            {
                Dps = dps; Crit = crit; Haste = haste; Cooldown = cooldown;
            }
            public double[] Dps { get; }
            public int[] Crit { get; }
            public int[] Haste { get; }
            public long[] Cooldown { get; }
        }
    }
}
