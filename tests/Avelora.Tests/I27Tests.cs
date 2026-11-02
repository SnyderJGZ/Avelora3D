using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Avelora.Server.Entities;
using Avelora.Server.Items;
using Avelora.Server.Npcs;
using Avelora.Server.Progression;
using Avelora.Server.Simulation;
using Avelora.Shared;
using Avelora.Shared.Maps;
using Avelora.Shared.Protocol;
using Xunit;

namespace Avelora.Tests
{
    public sealed class I27Tests
    {
        private static string Data(string file) => Path.Combine(AppContext.BaseDirectory, file);
        private static ClassCatalog Classes() => ClassCatalog.Load(Data("classes.json"));
        private static ItemCatalog Items() => ItemCatalog.Load(Data("items.json"));

        private static void AssertJsonError(string json, Action<string> validate, string field)
        {
            string temp = Path.Combine(Path.GetTempPath(), "avelora-i27-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(temp, json);
            try
            {
                InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => validate(temp));
                Assert.Contains(field, error.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                File.Delete(temp);
            }
        }

        [Theory]
        [InlineData(1, 1, 2, 0, 0, 1, 126)]
        [InlineData(2, 1, 0, 0, 2, 1, 84)]
        [InlineData(1, 5, 10, 0, 0, 5, 190)]
        [InlineData(2, 5, 0, 0, 10, 5, 124)]
        public void Vida_Maxima_Incluye_La_Vida_Por_Nivel_Y_Vit(
            int classId, int level, int allocStr, int allocDex, int allocInt, int allocVit, int expected)
        {
            DerivedCharacterStats stats = CharacterStats.Calculate(
                Classes(), classId, level, allocStr, allocDex, allocInt, allocVit);

            Assert.Equal(expected, stats.MaxHealth);
        }

        [Theory]
        [InlineData(1, 10, 0)]
        [InlineData(2, 0, 10)]
        [InlineData(3, 5, 5)]
        [InlineData(4, 5, 10)]
        public void Defensa_Y_Resistencia_Incluyen_El_Nivel(int classId, int defense, int magicResist)
        {
            DerivedCharacterStats stats = CharacterStats.Calculate(Classes(), classId, 5, 0, 0, 0, 0);

            Assert.Equal(defense, stats.Defense);
            Assert.Equal(magicResist, stats.MagicResist);
        }

        [Fact]
        public void Daño_Basico_Aplica_Multiplicador_Y_Bonus_Antes_De_El()
        {
            ClassCatalog classes = Classes();

            Assert.Equal(11, CombatMath.GetAttackDamage(classes, 1, 1, 2, 0, 0, 0, 10, out _));
            Assert.Equal(15, CombatMath.GetAttackDamage(classes, 1, 1, 2, 0, 0, 0, 10, out _, bonus: 4));
            Assert.Equal(6, CombatMath.GetAttackDamage(classes, 1, 1, 2, 0, 0, 0, null, out _));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CombatMath.GetAttackDamage(classes, 1, 1, 2, 0, 0, 0, 10, out _, bonus: -1));
        }

        [Fact]
        public void Habilidad_Combina_Daño_De_Habilidad_Arma_Y_Atributo()
        {
            ClassCatalog classes = Classes();

            Assert.Equal(23, CombatMath.GetSkillAmount(classes, 1, 1, 2, 0, 0, 0, 6, 150, 10));
            Assert.Equal(16, CombatMath.GetSkillAmount(classes, 1, 1, 2, 0, 0, 0, 6, 150, null));
            Assert.Equal(22, CombatMath.GetSkillAmount(classes, 1, 1, 2, 0, 0, 0, 20, 0, 10));
        }

        [Fact]
        public void Catalogos_Rechazan_Power_Level_WeaponPercent_Y_DamageType_Fuera_De_Rango()
        {
            string classesJson = File.ReadAllText(Data("classes.json"));
            JsonObject classes = JsonNode.Parse(classesJson)!.AsObject();
            classes.Remove("powerPerPoint");
            AssertJsonError(classes.ToJsonString(), path => ClassCatalog.Load(path), "powerPerPoint");

            classes = JsonNode.Parse(classesJson)!.AsObject();
            classes["powerPerPoint"] = 101;
            AssertJsonError(classes.ToJsonString(), path => ClassCatalog.Load(path), "powerPerPoint");

            classes = JsonNode.Parse(classesJson)!.AsObject();
            classes["classes"]![0]!["healthPerLevel"] = -1;
            AssertJsonError(classes.ToJsonString(), path => ClassCatalog.Load(path), "healthPerLevel");

            classes = JsonNode.Parse(classesJson)!.AsObject();
            classes["classes"]![0]!["defensePerLevel"] = 1001;
            AssertJsonError(classes.ToJsonString(), path => ClassCatalog.Load(path), "defensePerLevel");

            string skillsJson = File.ReadAllText(Data("skills.json"));
            foreach (int value in new[] { -1, 1001 })
            {
                JsonObject skills = JsonNode.Parse(skillsJson)!.AsObject();
                skills["skills"]![0]!["weaponPercent"] = value;
                AssertJsonError(skills.ToJsonString(),
                    path => SkillCatalog.Load(path, Classes()), "weaponPercent");
            }

            string npcsJson = File.ReadAllText(Data("npcs.json"));
            JsonObject npcs = JsonNode.Parse(npcsJson)!.AsObject();
            npcs["types"]![0]!["damageType"] = "fire";
            AssertJsonError(npcs.ToJsonString(), path =>
            {
                NpcCatalog catalog = NpcCatalog.Load(path);
                catalog.Validate(MapCollisionGrid.AllWalkable(80, 80), Items());
            }, "damageType");
        }

        [Theory]
        [InlineData("physical", 10)]
        [InlineData("magical", 40)]
        public void Golpe_De_Pnj_Se_Mitiga_Con_Defensa_O_Resistencia_Segun_Tipo(string damageType, int expectedDefense)
        {
            ClassCatalog classes = Classes();
            CharacterClass warrior = classes.Find(1)!;
            warrior.Defense = 8;
            warrior.MagicResist = 40;
            NpcType type = new NpcType
            {
                Id = 9001, Name = "Prueba", Speed = 0, HalfWidth = 0.35f, HalfHeight = 0.35f,
                MaxHealth = 100, Experience = 1, Level = 1, Defense = 0, MagicResist = 0,
                DamageType = damageType, RespawnSeconds = 10, AggroRadius = 4, LeashRadius = 8,
                AttackDamage = 20, AttackRange = 1, AttackCooldownSeconds = 1.5f,
            };
            var npcs = new NpcCatalog
            {
                Types = new List<NpcType> { type },
                Spawners = new List<NpcSpawner>
                {
                    new NpcSpawner { TypeId = type.Id, X = 10, Y = 10, Count = 1, WanderRadius = 0 },
                },
            };
            Zone zone = NewZone(classes, Items(), npcs: npcs);
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(10.5f, 10), new Vec2(0.4f, 0.4f), 8);
            player.Health = 500;

            Tick(zone, 3);

            Assert.Equal(500 - CombatMath.MitigateDamage(20, expectedDefense), player.Health);
        }

        [Fact]
        public void Golpe_Basico_Del_Servidor_Aplica_Bonus_Antes_Del_Multiplicador()
        {
            ClassCatalog classes = Classes();
            ItemCatalog items = Items();
            Zone zone = NewZone(classes, items);
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(8, 10), new Vec2(0.4f, 0.4f), 8);
            player.EquippedWeaponId = 3101;
            Assert.True(player.Statuses.Apply(StatusKind.Bonus, 4010, player.Id, 4, 1000));
            NpcEntity npc = AddNpc(zone, 100, new Vec2(9, 10));

            Use(zone, player, npc.Id, 0);
            zone.Tick();

            Assert.Equal(1000 - 15, npc.Health);
        }

        [Fact]
        public void Habilidad_Del_Servidor_Usa_WeaponPercent_Y_El_Daño_Del_Arma_Equipada()
        {
            ClassCatalog classes = Classes();
            ItemCatalog items = Items();
            SkillCatalog skills = new SkillCatalog
            {
                Skills = new List<SkillDefinition>
                {
                    new SkillDefinition
                    {
                        Id = 5001, Name = "Prueba", ClassId = 1, RequiredLevel = 1, ResourceCost = 1,
                        CooldownSeconds = 2, Range = 1.2f, Damage = 6, DamageType = "physical", WeaponPercent = 150,
                    },
                },
            };
            skills.Validate(classes);
            Zone zone = NewZone(classes, items, skills);
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(8, 10), new Vec2(0.4f, 0.4f), 8);
            player.AllocStr = 2;
            player.EquippedWeaponId = 3101;
            NpcEntity npc = AddNpc(zone, 100, new Vec2(9, 10));
            int expected = CombatMath.GetSkillAmount(classes, 1, player.Level, 2, 0, 0, 0,
                6, 150, items.Find(3101)!.Damage, items, player.Equipment);

            Use(zone, player, npc.Id, 5001);
            zone.Tick();

            Assert.Equal(1000 - expected, npc.Health);
        }

        private static Zone NewZone(ClassCatalog classes, ItemCatalog items,
            SkillCatalog? skills = null, NpcCatalog? npcs = null) => new Zone(
                1, MapCollisionGrid.AllWalkable(30, 20), new EntityStore(), new EntityIdAllocator(),
                new InputQueue(128), new MovementSystem(4f, SimulationConstants.FixedDt), 128, 4,
                new InterestSettings(30f, 8f, 0f), npcs, 12345, new AttackQueue(128),
                new CombatSettings(1f), items: items, itemActions: new ItemActionQueue(64),
                classes: classes, skills: skills);

        private static NpcEntity AddNpc(Zone zone, int id, Vec2 position)
        {
            var npc = new NpcEntity(id, 9000, 0, position, 0, long.MaxValue, 1000, 200, -1)
            {
                Position = position,
                HalfSize = new Vec2(0.35f, 0.35f),
            };
            zone.AddEntity(npc);
            return npc;
        }

        private static void Use(Zone zone, PlayerEntity player, int target, ushort skill = 0) =>
            Assert.True(zone.Attacks!.TryEnqueue(new AttackIntent(player.Id, target, skill)));

        private static void Tick(Zone zone, int count)
        {
            for (int i = 0; i < count; i++) zone.Tick();
        }
    }
}
