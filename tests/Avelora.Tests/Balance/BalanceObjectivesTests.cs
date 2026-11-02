using System;
using System.Globalization;
using System.IO;
using Avelora.Balance;
using Avelora.Server.Items;
using Avelora.Server.Npcs;
using Avelora.Server.Progression;
using Xunit;
using Xunit.Abstractions;

namespace Avelora.Tests.Balance
{
    public sealed class BalanceObjectivesTests
    {
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
        [InlineData(1)]
        [InlineData(5)]
        public void Objetivo_3_Dps_De_Clase_Queda_En_La_Banda_Por_Nivel(int level)
        {
            CombatSimulator simulator = Simulator();
            CombatSimulationResult warrior = simulator.Simulate(1, level, "equilibrado", "franja", false);
            CombatSimulationResult mage = simulator.Simulate(2, level, "equilibrado", "franja", false);
            CombatSimulationResult ranger = simulator.Simulate(3, level, "equilibrado", "franja", false);
            CombatSimulationResult healer = simulator.Simulate(4, level, "equilibrado", "franja", false);
            double average = (warrior.DamagePerSecond + mage.DamagePerSecond + ranger.DamagePerSecond) / 3d;

            foreach (CombatSimulationResult result in new[] { warrior, mage, ranger })
                Assert.InRange(result.DamagePerSecond, average * 0.9, average * 1.1);
            Assert.InRange(healer.DamagePerSecond / average, 0.65, 0.80);

            _output.WriteLine("Nivel {0}: Guerrero {1:0.00}; Mago {2:0.00}; Ranger {3:0.00}; Sanador {4:0.00}; media {5:0.00}",
                level, warrior.DamagePerSecond, mage.DamagePerSecond, ranger.DamagePerSecond,
                healer.DamagePerSecond, average);
        }

        [Fact]
        public void Objetivo_1_Orco_De_Nivel_Uno_Queda_En_Las_Bandas_De_Ttk_Y_Vida_Perdida()
        {
            CombatSimulator simulator = Simulator();
            foreach (int classId in new[] { 1, 2, 3 })
            {
                CombatSimulationResult result = simulator.Simulate(classId, 1, "equilibrado", "franja", false);
                NpcCombatResult npc = FindNpc(result, 2001);
                Assert.InRange(npc.TimeToKillSeconds!.Value, 4.0, 6.0);
                Assert.InRange(npc.HealthLostPercent, 30, 50);
                WriteNpc(result, npc);
            }
        }

        [Fact]
        public void Objetivo_1_Referencia_De_Nivel_Cinco_Queda_En_Las_Bandas_De_Ttk_Y_Vida_Perdida()
        {
            NpcCatalog npcs = new NpcCatalog
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
            CombatSimulator simulator = Simulator(npcs);
            foreach (int classId in new[] { 1, 2, 3 })
            {
                CombatSimulationResult result = simulator.Simulate(classId, 5, "equilibrado", "franja", false);
                NpcCombatResult npc = FindNpc(result, 9005);
                Assert.InRange(npc.TimeToKillSeconds!.Value, 4.0, 6.0);
                Assert.InRange(npc.HealthLostPercent, 30, 50);
                WriteNpc(result, npc);
            }
        }

        private static NpcCombatResult FindNpc(CombatSimulationResult result, int id)
        {
            foreach (NpcCombatResult npc in result.Npcs)
                if (npc.NpcId == id) return npc;
            throw new InvalidOperationException("Falta el PNJ de referencia " + id + ".");
        }

        private void WriteNpc(CombatSimulationResult result, NpcCombatResult npc) =>
            _output.WriteLine("Nivel {0} clase {1}: DPS={2}; {3} TTK={4} s, vida perdida={5:0.0}%",
                result.Level, result.ClassName,
                result.DamagePerSecond.ToString("0.00", CultureInfo.InvariantCulture), npc.NpcName,
                npc.TimeToKillSeconds?.ToString("0.0", CultureInfo.InvariantCulture) ?? "n/d", npc.HealthLostPercent);
    }
}
