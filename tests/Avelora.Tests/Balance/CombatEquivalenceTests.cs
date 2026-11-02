using System.Collections.Generic;
using System.IO;
using Avelora.Balance;
using Avelora.Server.Entities;
using Avelora.Server.Items;
using Avelora.Server.Npcs;
using Avelora.Server.Progression;
using Avelora.Server.Simulation;
using Avelora.Server.Zones;
using Avelora.Shared;
using Avelora.Shared.Maps;
using Avelora.Shared.Protocol;
using Xunit;

namespace Avelora.Tests.Balance
{
    public sealed class CombatEquivalenceTests
    {
        private static SkillDefinition Skill(int id, int classId) => new SkillDefinition
        {
            Id = id, Name = "Prueba", ClassId = classId, RequiredLevel = 1, ResourceCost = 1,
            CooldownSeconds = 2, Range = 1, Damage = 1, DamageType = "physical",
        };

        private static SkillCatalog Catalog(ClassCatalog classes, params SkillDefinition[] skills)
        {
            var catalog = new SkillCatalog { Skills = new List<SkillDefinition>(skills) };
            catalog.Validate(classes);
            return catalog;
        }

        private static Zone NewZone(ClassCatalog classes, SkillCatalog skills, ItemCatalog items) => new Zone(
            1, MapCollisionGrid.AllWalkable(30, 20), new EntityStore(), new EntityIdAllocator(),
            new InputQueue(128), new MovementSystem(4f, SimulationConstants.FixedDt), 128, 4,
            new InterestSettings(30f, 8f, 0f), null, 12345, new AttackQueue(128),
            new CombatSettings(1f), items: items, itemActions: new ItemActionQueue(64),
            classes: classes, skills: skills);

        private static NpcEntity AddNpc(Zone zone, int id) 
        {
            var npc = new NpcEntity(id, 2001, 0f, new Vec2(9, 10), 0f,
                nextDirectionChangeTick: long.MaxValue, 10000, 200, spawnerIndex: -1,
                defense: 0, magicResist: 0)
            {
                Position = new Vec2(9, 10),
                HalfSize = new Vec2(0.35f, 0.35f),
            };
            zone.AddEntity(npc);
            return npc;
        }

        private static void Use(Zone zone, PlayerEntity player, int targetId) =>
            Assert.True(zone.Attacks!.TryEnqueue(new AttackIntent(player.Id, targetId, 0)));

        private static int ExpectedDamage(ClassCatalog classes, PlayerEntity player, ItemCatalog items) =>
            CharacterStats.GetAttackDamage(classes, player.ClassId, player.Level, player.AllocStr,
                player.AllocDex, player.AllocInt, player.AllocVit, items.Find(player.EquippedWeaponId)?.Damage,
                out _, items, player.Equipment, items.Find(player.EquippedWeaponId)?.DamageType);

        [Theory]
        [InlineData(1, 1)]
        [InlineData(1, 10)]
        [InlineData(2, 1)]
        [InlineData(2, 10)]
        [InlineData(3, 1)]
        [InlineData(3, 10)]
        [InlineData(4, 1)]
        [InlineData(4, 10)]
        public void Ataque_Basico_Conserva_Dano_Y_Recarga_Entre_Servidor_Y_Simulador(int classId, int level)
        {
            string basePath = AppContext.BaseDirectory;
            ClassCatalog classes = ClassCatalog.Load(Path.Combine(basePath, "classes.json"));
            ItemCatalog items = ItemCatalog.Load(Path.Combine(basePath, "items.json"));
            SkillCatalog skills = SkillCatalog.Load(Path.Combine(basePath, "skills.json"), classes);
            NpcCatalog npcs = NpcCatalog.Load(Path.Combine(basePath, "npcs.json"));
            LevelTable levels = LevelTable.Load(Path.Combine(basePath, "levels.json"), items);
            var simulator = new CombatSimulator(classes, items, skills, npcs, levels);
            CombatSimulationResult simulated = simulator.Simulate(classId, level, "puro", "ninguno", false);

            Zone zone = NewZone(classes, Catalog(classes, Skill(5001, classId)), items);
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(8, 10), new Vec2(0.4f, 0.4f), 8,
                classId: (byte)classId);
            player.Level = level;
            switch (classId)
            {
                case 1:
                    player.EquippedWeaponId = 3101;
                    break;
                case 2:
                    if (level >= 1) player.EquippedWeaponId = 3104;
                    break;
                case 3:
                    player.EquippedWeaponId = 3103;
                    break;
                case 4:
                    if (level >= 5) player.EquippedWeaponId = 3105;
                    break;
            }
            int points = level * levels.PointsPerLevel;
            CharacterClass characterClass = classes.Find(classId)!;
            player.AllocStr = characterClass.Primary == "str" ? points : 0;
            player.AllocDex = characterClass.Primary == "dex" ? points : 0;
            player.AllocInt = characterClass.Primary == "int" ? points : 0;
            int expectedDamage = ExpectedDamage(classes, player, items);
            NpcEntity npc = AddNpc(zone, 100);

            Use(zone, player, npc.Id);
            zone.Tick();
            Assert.Equal(expectedDamage, 10000 - npc.Health);
            Assert.Equal(simulated.FirstHitDamage, 10000 - npc.Health);
            Assert.Equal(0, player.LastAttackTick);

            for (int tick = 1; tick < simulated.BasicCooldownTicks; tick++)
            {
                Use(zone, player, npc.Id);
                zone.Tick();
                Assert.Equal(expectedDamage, 10000 - npc.Health);
            }
            Use(zone, player, npc.Id);
            zone.Tick();
            Assert.Equal(expectedDamage * 2, 10000 - npc.Health);
            Assert.Equal(simulated.BasicCooldownTicks, player.LastAttackTick);
        }
    }
}
