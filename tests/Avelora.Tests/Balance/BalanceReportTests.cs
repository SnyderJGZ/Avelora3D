using System;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using Avelora.Shared;
using Avelora.Shared.Protocol;
using Avelora.Balance;
using Avelora.Server.Items;
using Avelora.Server.Npcs;
using Avelora.Server.Progression;
using Xunit;
using Xunit.Abstractions;

namespace Avelora.Tests.Balance
{
    public sealed class BalanceReportTests
    {
        private readonly ITestOutputHelper _output;

        public BalanceReportTests(ITestOutputHelper output) => _output = output;

        [Theory]
        [InlineData(1, 1)] [InlineData(1, 5)] [InlineData(1, 10)] [InlineData(1, 25)]
        [InlineData(2, 1)] [InlineData(2, 5)] [InlineData(2, 10)] [InlineData(2, 25)]
        [InlineData(3, 1)] [InlineData(3, 5)] [InlineData(3, 10)] [InlineData(3, 25)]
        [InlineData(4, 1)] [InlineData(4, 5)] [InlineData(4, 10)] [InlineData(4, 25)]
        public void Modo_Informe_Registra_Dps_Ttk_Y_Vida_Perdida(int classId, int level)
        {
            CombatSimulationResult result = CreateSimulator().Simulate(classId, level, "puro", "ninguno", false);
            Assert.True(result.DamagePerSecond > 0 && double.IsFinite(result.DamagePerSecond));
            Assert.All(result.Npcs, npc => Assert.True(npc.TimeToKillSeconds is null || npc.TimeToKillSeconds > 0));
            _output.WriteLine("{0} nivel {1}: DPS={2}; TTK/vida perdida:", result.ClassName, level,
                result.DamagePerSecond.ToString("0.0", CultureInfo.InvariantCulture));
            foreach (NpcCombatResult npc in result.Npcs)
                _output.WriteLine("  {0}: {1} s, {2:0.0}%", npc.NpcName,
                    npc.TimeToKillSeconds?.ToString("0.0", CultureInfo.InvariantCulture) ?? "n/d", npc.HealthLostPercent);
        }

        [Fact]
        public void Guerrero_Nivel_Uno_Reproduce_Dps_De_Referencia()
        {
            double dps = CreateSimulator().Simulate(1, 1, "puro", "ninguno", false).DamagePerSecond;
            Assert.InRange(dps, 13.70, 13.80);
        }

        [Theory]
        [InlineData(1, 1)] [InlineData(1, 25)]
        [InlineData(2, 1)] [InlineData(2, 25)]
        [InlineData(3, 1)] [InlineData(3, 25)]
        [InlineData(4, 1)] [InlineData(4, 25)]
        public void Sin_Habilidades_Dps_Es_Dano_Bruto_Por_Recarga(int classId, int level)
        {
            string basePath = AppContext.BaseDirectory;
            ClassCatalog classes = ClassCatalog.Load(Path.Combine(basePath, "classes.json"));
            ItemCatalog items = ItemCatalog.Load(Path.Combine(basePath, "items.json"));
            LevelTable levels = LevelTable.Load(Path.Combine(basePath, "levels.json"), items);
            CharacterClass clase = classes.Find(classId)!;
            ItemType? arma = null;
            foreach (ItemType item in items.Items)
                if (item.IsWeapon && clase.WeaponTypes!.Contains(item.WeaponType))
                {
                    if (item.RequiredLevel <= level) arma = item;
                    break;
                }
            var equipo = new ushort[ProtocolConstants.EquipSlotCount];
            if (arma != null) equipo[(int)EquipSlot.Weapon] = (ushort)arma.Id;
            int puntos = levels.PointsPerLevel * level;
            int damage = CombatMath.GetAttackDamage(classes, classId, level,
                clase.Primary == "str" ? puntos : 0, clase.Primary == "dex" ? puntos : 0,
                clase.Primary == "int" ? puntos : 0, 0, arma?.Damage, out _, items, equipo, arma?.DamageType);
            long cooldown = CombatMath.AttackCooldownTicks(arma?.CooldownSeconds ?? clase.Unarmed!.CooldownSeconds);
            var result = CreateSimulator().Simulate(classId, level, "puro", "ninguno", false);
            Assert.Equal(damage * (double)SimulationConstants.TickRate / cooldown, result.DamagePerSecond, 6);
        }

        [Fact]
        public void Sin_Habilidades_Recarga_No_Divisora_No_Sesga_Dps()
        {
            string basePath = AppContext.BaseDirectory;
            ClassCatalog classes = ClassCatalog.Load(Path.Combine(basePath, "classes.json"));
            ItemCatalog items = ItemCatalog.Load(Path.Combine(basePath, "items.json"));
            items.Find(3101)!.CooldownSeconds = 0.35f;
            var simulator = new CombatSimulator(classes, items,
                SkillCatalog.Load(Path.Combine(basePath, "skills.json"), classes),
                NpcCatalog.Load(Path.Combine(basePath, "npcs.json")),
                LevelTable.Load(Path.Combine(basePath, "levels.json"), items));
            var result = simulator.Simulate(1, 1, "puro", "ninguno", false);
            Assert.Equal(7, result.BasicCooldownTicks);
            Assert.NotEqual(0, 60 * SimulationConstants.TickRate % result.BasicCooldownTicks);
            int damage = CombatMath.GetAttackDamage(classes, 1, 1, 3, 0, 0, 0, 10, out _, items);
            Assert.Equal(damage * (double)SimulationConstants.TickRate / 7, result.DamagePerSecond, 6);
        }

        [Fact]
        public void Derrota_No_Produce_Ttk_Y_Markdown_Indica_No_Disponible()
        {
            string directory = Path.Combine(Path.GetTempPath(), "avelora-balance-" + Guid.NewGuid());
            Directory.CreateDirectory(directory);
            try
            {
                foreach (string file in new[] { "classes.json", "items.json", "levels.json", "skills.json", "npcs.json" })
                    File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(directory, file));
                string path = Path.Combine(directory, "npcs.json");
                JsonNode catalogo = JsonNode.Parse(File.ReadAllText(path))!;
                catalogo["types"]![1]!["maxHealth"] = 100000;
                catalogo["types"]![1]!["attackDamage"] = 10000;
                File.WriteAllText(path, catalogo.ToJsonString());
                ClassCatalog classes = ClassCatalog.Load(Path.Combine(directory, "classes.json"));
                ItemCatalog items = ItemCatalog.Load(Path.Combine(directory, "items.json"));
                var simulator = new CombatSimulator(classes, items,
                    SkillCatalog.Load(Path.Combine(directory, "skills.json"), classes),
                    NpcCatalog.Load(path), LevelTable.Load(Path.Combine(directory, "levels.json"), items));
                var result = simulator.Simulate(1, 1, "puro", "ninguno", false);
                Assert.False(result.Npcs[1].Defeated);
                Assert.Null(result.Npcs[1].TimeToKillSeconds);
                Assert.True(result.Npcs[0].Defeated);
                Assert.True(result.Npcs[0].TimeToKillSeconds > 0);
                using var output = new StringWriter();
                Assert.Equal(0, Program.Run(new[] { "--datos", directory, "--niveles", "1", "--sin-habilidades" }, output));
                Assert.Contains("| Guerrero | 1 | Orco guerrero | n/d | 100.0 |", output.ToString());
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void Tiempo_Agotado_No_Produce_Ttk()
        {
            var npcs = new NpcCatalog();
            npcs.Types.Add(new NpcType
            {
                Id = 2001, Name = "Resistente", MaxHealth = 30, Defense = 100000,
                MagicResist = 100000, AttackDamage = 1, AttackCooldownSeconds = 1000
            });
            ItemCatalog items = ItemCatalog.Load(Path.Combine(AppContext.BaseDirectory, "items.json"));
            items.Find(3101)!.CooldownSeconds = 2f;
            var result = CreateSimulator(npcs, items).Simulate(1, 1, "puro", "ninguno", false);
            Assert.False(result.Npcs[0].Defeated);
            Assert.Null(result.Npcs[0].TimeToKillSeconds);
            Assert.InRange(result.Npcs[0].HealthLostPercent, 0, 99);
        }

        private static CombatSimulator CreateSimulator(NpcCatalog? npcs = null, ItemCatalog? customItems = null)
        {
            string basePath = AppContext.BaseDirectory;
            ClassCatalog classes = ClassCatalog.Load(Path.Combine(basePath, "classes.json"));
            ItemCatalog items = customItems ?? ItemCatalog.Load(Path.Combine(basePath, "items.json"));
            LevelTable levels = LevelTable.Load(Path.Combine(basePath, "levels.json"), items);
            return new CombatSimulator(classes, items,
                SkillCatalog.Load(Path.Combine(basePath, "skills.json"), classes),
                npcs ?? NpcCatalog.Load(Path.Combine(basePath, "npcs.json")), levels);
        }
    }
}
