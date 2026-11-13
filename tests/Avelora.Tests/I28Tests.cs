using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
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

namespace Avelora.Tests
{
    public sealed class I28Tests
    {
        private static string Data(string file) => Path.Combine(AppContext.BaseDirectory, file);
        private static ClassCatalog Classes() => ClassCatalog.Load(Data("classes.json"));
        private static ItemCatalog Items() => ItemCatalog.Load(Data("items.json"));

        [Fact]
        public void Critico_Y_Prisa_Usan_Destreza_Efectiva_Y_Topes()
        {
            ClassCatalog classes = Classes();
            Assert.Equal(840, CombatMath.CritChancePb(classes, 1, 4));
            Assert.Equal(4000, CombatMath.CritChancePb(classes, 1, 10000));
            Assert.Equal(330, CombatMath.CritChancePb(classes, 4, 3));
            Assert.Equal(60, CombatMath.HastePb(classes, 4));
            Assert.Equal(3000, CombatMath.HastePb(classes, 10000));
        }

        [Fact]
        public void Recarga_Aplica_Prisa_Con_Techo_Y_Minimo()
        {
            Assert.Equal(20, CombatMath.AttackCooldownTicks(1.0, 0, 8));
            Assert.Equal(19, CombatMath.AttackCooldownTicks(1.0, 555, 8));
            Assert.Equal(16, CombatMath.AttackCooldownTicks(0.8, 60, 8));
            Assert.Equal(16, CombatMath.AttackCooldownTicks(1.0, 3000, 8));
            Assert.Equal(8, CombatMath.AttackCooldownTicks(0.4, 3000, 8));
            Assert.Throws<ArgumentOutOfRangeException>(() => CombatMath.AttackCooldownTicks(1.0, -1, 8));
        }

        [Fact]
        public void Danio_Critico_PvP_Y_Esperado_Respeta_El_Orden_Entero()
        {
            Assert.Equal(16, CombatMath.CritDamage(11, 150));
            Assert.Equal(1, CombatMath.PvpDamage(1, 60));
            Assert.Equal(6, CombatMath.PvpDamage(10, 60));
            Assert.Equal(11.42, CombatMath.ExpectedDamage(11, 840, 150), 2);
            Assert.Equal(6.252, CombatMath.ExpectedHit(11, 840, 150, 0, 60), 3);
        }

        [Fact]
        public void Catalogos_Validan_Parametros_Nuevos_Y_Mantienen_Defectos_Neutros()
        {
            string classesJson = File.ReadAllText(Data("classes.json"));
            foreach ((string field, int value) in new[]
            {
                ("critPerDex", 1001), ("critCap", 10001), ("critMultiplier", 99),
                ("hastePerDex", -1), ("hasteCap", 10001), ("minAttackTicks", 0),
                ("pvpDamagePercent", 0), ("pvpDamagePercent", 101),
            })
            {
                JsonObject json = JsonNode.Parse(classesJson)!.AsObject();
                json[field] = value;
                AssertJsonError(json.ToJsonString(), path => ClassCatalog.Load(path), field);
            }

            JsonObject badCritBase = JsonNode.Parse(classesJson)!.AsObject();
            badCritBase["classes"]![0]!["critBase"] = 10001;
            AssertJsonError(badCritBase.ToJsonString(), path => ClassCatalog.Load(path), "critBase");

            string skillsJson = File.ReadAllText(Data("skills.json"));
            JsonObject badCooldown = JsonNode.Parse(skillsJson)!.AsObject();
            badCooldown["globalCooldownSeconds"] = 11;
            AssertJsonError(badCooldown.ToJsonString(), path => SkillCatalog.Load(path, Classes()), "globalCooldownSeconds");

            JsonObject legacyClasses = JsonNode.Parse(classesJson)!.AsObject();
            foreach (string field in new[] { "critPerDex", "critCap", "critMultiplier", "hastePerDex", "hasteCap", "minAttackTicks", "pvpDamagePercent" })
                legacyClasses.Remove(field);
            foreach (JsonNode? character in legacyClasses["classes"]!.AsArray())
                character!.AsObject().Remove("critBase");
            ClassCatalog catalog = LoadClassesJson(legacyClasses.ToJsonString());
            Assert.Equal(0, CombatMath.CritChancePb(catalog, 1, 4));
            Assert.Equal(0, CombatMath.HastePb(catalog, 4));
            Assert.Equal(100, catalog.PvpDamagePercent);

            JsonObject legacySkills = JsonNode.Parse(skillsJson)!.AsObject();
            legacySkills.Remove("globalCooldownSeconds");
            SkillCatalog skills = LoadSkillsJson(legacySkills.ToJsonString(), catalog);
            Assert.Equal(0, skills.GlobalCooldownTicks);
        }

        [Fact]
        public void CharacterStats_Expone_Critico_Y_Prisa_Con_Equipo_En_Destreza()
        {
            DerivedCharacterStats stats = CharacterStats.Calculate(Classes(), 3, 5, 0, 15, 0, 0);
            Assert.Equal(1010, stats.CritChancePb);
            Assert.Equal(315, stats.HastePb);
        }

        [Fact]
        public void Ataque_Basico_Resuelve_Critico_Y_Marca_El_Evento()
        {
            var critical = CombatFixture(random: new FixedRandom(0));
            Hit(critical.Combat, critical.Entities, critical.Player, critical.Npc, 0);
            Assert.Equal(991, critical.Npc.Health);
            Assert.True((critical.Events[0].Flags & CombatEventFlags.Critical) != 0);

            var ordinary = CombatFixture(random: new FixedRandom(9999));
            Hit(ordinary.Combat, ordinary.Entities, ordinary.Player, ordinary.Npc, 0);
            Assert.Equal(994, ordinary.Npc.Health);
            Assert.False((ordinary.Events[0].Flags & CombatEventFlags.Critical) != 0);
        }

        [Fact]
        public void Habilidad_De_Area_Tira_Critico_Por_Cada_Objetivo()
        {
            ClassCatalog classes = Classes();
            SkillCatalog skills = Catalog(classes, new SkillDefinition
            {
                Id = 5001, Name = "Área de prueba", ClassId = 1, RequiredLevel = 1,
                ResourceCost = 0, CooldownSeconds = 1, Range = 3, Damage = 6,
                DamageType = "physical", Radius = 2, MaxTargets = 3,
            });
            var fixture = CombatFixture(skills, new FixedRandom(0, 9999));
            NpcEntity other = AddNpc(fixture.Entities, 101, new Vec2(9.5f, 10));

            Use(fixture.Combat, fixture.Entities, fixture.Player, fixture.Npc, 5001, 0);

            Assert.Equal(991, fixture.Npc.Health);
            Assert.Equal(994, other.Health);
            Assert.True((fixture.Events[0].Flags & CombatEventFlags.Critical) != 0);
            Assert.False((fixture.Events[1].Flags & CombatEventFlags.Critical) != 0);
        }

        [Fact]
        public void Cura_Puede_Criticar_Pero_Escudo_No_Consume_Tirada()
        {
            ClassCatalog classes = Classes();
            SkillCatalog skills = Catalog(classes,
                new SkillDefinition
                {
                    Id = 5002, Name = "Cura de prueba", ClassId = 4, RequiredLevel = 1,
                    ResourceCost = 0, CooldownSeconds = 1, Range = 3, Damage = 10,
                    DamageType = "magical", Effect = "heal",
                },
                new SkillDefinition
                {
                    Id = 5003, Name = "Escudo de prueba", ClassId = 2, RequiredLevel = 1,
                    ResourceCost = 0, CooldownSeconds = 1, Range = 3, Damage = 0,
                    DamageType = "magical", Effect = "none", Status = "shield",
                    StatusSeconds = 10, StatusAmount = 25,
                });
            var heal = CombatFixture(skills, new FixedRandom(0), classId: 4);
            heal.Player.MaxHealth = 100;
            heal.Player.Health = 50;
            Use(heal.Combat, heal.Entities, heal.Player, heal.Player, 5002, 0);
            Assert.Equal(66, heal.Player.Health);
            Assert.Equal(CombatEventFlags.Heal | CombatEventFlags.Critical, heal.Events[0].Flags);

            var shieldRandom = new FixedRandom(0);
            var shield = CombatFixture(skills, shieldRandom, classId: 2);
            Use(shield.Combat, shield.Entities, shield.Player, shield.Player, 5003, 0);
            Assert.Equal(CombatMath.GetSkillAmount(classes, 2, 1, 0, 0, 0, 0,
                25, 0, null), shield.Player.Statuses.Get(StatusKind.Shield).Amount);
            Assert.Equal(0, shieldRandom.Calls);
        }

        [Fact]
        public void PvP_Reduce_Danio_Tras_Mitigar_Pero_No_La_Cura()
        {
            var fixture = CombatFixture(random: new FixedRandom(9999), classId: 1);
            PlayerEntity victim = AddPlayer(fixture.Entities, 2, 1, new Vec2(9, 10));
            fixture.Player.PvpMode = PvpMode.Pvp;
            victim.PvpMode = PvpMode.Pvp;
            int raw = CombatMath.GetAttackDamage(Classes(), 1, 1, 0, 0, 0, 0, null, out _);

            Hit(fixture.Combat, fixture.Entities, fixture.Player, victim, 0);

            Assert.Equal(500 - CombatMath.PvpDamage(CombatMath.MitigateDamage(raw, 0), 60), victim.Health);

            ClassCatalog classes = Classes();
            SkillCatalog skills = Catalog(classes, new SkillDefinition
            {
                Id = 5004, Name = "Cura aliada", ClassId = 4, RequiredLevel = 1,
                ResourceCost = 0, CooldownSeconds = 1, Range = 3, Damage = 10,
                DamageType = "magical", Effect = "heal",
            });
            var healer = CombatFixture(skills, new FixedRandom(9999), classId: 4);
            PlayerEntity ally = AddPlayer(healer.Entities, 2, 1, new Vec2(9, 10));
            healer.Player.PvpMode = PvpMode.Pvp;
            ally.PvpMode = PvpMode.Pvp;
            ally.Health = 50;
            ally.MaxHealth = 100;
            Use(healer.Combat, healer.Entities, healer.Player, ally, 5004, 0);
            Assert.Equal(61, ally.Health);
        }

        [Fact]
        public void Prisa_Reduce_La_Recarga_Basica_Sin_Cambiar_La_Habilidad()
        {
            var hasted = CombatFixture(random: new FixedRandom(9999), classId: 3, weaponId: 3103);
            hasted.Player.AllocDex = 40;
            Hit(hasted.Combat, hasted.Entities, hasted.Player, hasted.Npc, 0);
            Hit(hasted.Combat, hasted.Entities, hasted.Player, hasted.Npc, 18);
            Assert.Equal(1, hasted.Combat.RejectedCooldown);
            Hit(hasted.Combat, hasted.Entities, hasted.Player, hasted.Npc, 19);
            Assert.Equal(2, hasted.Combat.AttacksOk);

            var normal = CombatFixture(random: new FixedRandom(9999), classId: 3, weaponId: 3103);
            Hit(normal.Combat, normal.Entities, normal.Player, normal.Npc, 0);
            Hit(normal.Combat, normal.Entities, normal.Player, normal.Npc, 19);
            Assert.Equal(1, normal.Combat.RejectedCooldown);
            Hit(normal.Combat, normal.Entities, normal.Player, normal.Npc, 20);
            Assert.Equal(2, normal.Combat.AttacksOk);
        }

        [Fact]
        public void Golpe_De_Pnj_No_Aplica_Critico_Ni_Reduccion_PvP()
        {
            ClassCatalog classes = Classes();
            ItemCatalog items = Items();
            MapCollisionGrid map = MapCollisionGrid.AllWalkable(30, 20);
            var entities = new EntityStore();
            PlayerEntity player = AddPlayer(entities, 1, 1, new Vec2(10, 10));
            player.PvpMode = PvpMode.Pvp;
            player.Health = 500;
            var npc = new NpcEntity(100, 9001, 0, new Vec2(9.5f, 10), 0, long.MaxValue,
                100, 200, -1, aggroRadius: 4, leashRadius: 8, attackDamage: 20,
                attackRange: 1, attackCooldownTicks: 30)
            {
                Position = new Vec2(9.5f, 10),
                AiState = NpcAiState.Attack,
                TargetEntityId = player.Id,
                NextAttackTick = 0,
            };
            entities.Add(npc);
            var random = new FixedRandom(0);
            var npcs = new NpcSystem(map, random, new EntitySpatialGrid(8), classes, items);
            npcs.Tick(entities, new List<NpcEntity>());

            int defense = CharacterStats.Calculate(classes, player.ClassId, player.Level,
                player.AllocStr, player.AllocDex, player.AllocInt, player.AllocVit,
                items, player.Equipment).Defense;
            Assert.Equal(500 - CombatMath.MitigateDamage(npc.AttackDamage, defense), player.Health);
            Assert.Equal(0, random.Calls);
        }

        [Fact]
        public void Recarga_Global_Rechaza_Otra_Habilidad_Hasta_El_Tick_Configurado()
        {
            ClassCatalog classes = Classes();
            var skills = Catalog(classes,
                Skill(5005, 1), Skill(5006, 1));
            skills.GlobalCooldownSeconds = 1;
            skills.Validate(classes);
            var fixture = CombatFixture(skills, new FixedRandom(9999));
            SkillRejectReason? rejected = null;
            fixture.Combat.SkillRejected += (_, _, reason) => rejected = reason;

            fixture.Combat.Tick(fixture.Entities, new List<AttackIntent>
            {
                new AttackIntent(fixture.Player.Id, fixture.Npc.Id, 5005),
                new AttackIntent(fixture.Player.Id, fixture.Npc.Id, 5006),
            }, 0);
            Assert.Equal(SkillRejectReason.Cooldown, rejected);
            fixture.Combat.Tick(fixture.Entities,
                new List<AttackIntent> { new AttackIntent(fixture.Player.Id, fixture.Npc.Id, 5006) }, 20);
            Assert.Equal(2, fixture.Combat.SkillsUsed);

            SkillCatalog noGlobal = Catalog(classes, Skill(5007, 1), Skill(5008, 1));
            var noCooldown = CombatFixture(noGlobal, new FixedRandom(9999));
            noCooldown.Combat.Tick(noCooldown.Entities, new List<AttackIntent>
            {
                new AttackIntent(noCooldown.Player.Id, noCooldown.Npc.Id, 5007),
                new AttackIntent(noCooldown.Player.Id, noCooldown.Npc.Id, 5008),
            }, 0);
            Assert.Equal(2, noCooldown.Combat.SkillsUsed);
        }

        [Fact]
        public void Stats_V24_Contiene_Critico_Y_Prisa_Y_Flags_Criticos()
        {
            Assert.Equal((ushort)26, ProtocolConstants.Version);
            var stats = new StatsMessage(1, 1, 4, 3, 1, 10, 0, 0, 0, 0, 3, 100,
                6, 0, 2, 0, 840, 60, 100, 100, 1);
            byte[] bytes = MessageCodec.EncodeStats(stats);
            Assert.Equal(41, bytes.Length);
            Assert.True(MessageCodec.TryReadStats(bytes, 0, bytes.Length, out StatsMessage decoded));
            Assert.Equal((ushort)840, decoded.CritChance);
            Assert.Equal((ushort)60, decoded.Haste);
            Assert.False(MessageCodec.TryReadStats(bytes, 0, 40, out _));
            byte[] extra = new byte[42];
            Array.Copy(bytes, extra, bytes.Length);
            Assert.False(MessageCodec.TryReadStats(extra, 0, extra.Length, out _));
            Assert.Equal((byte)32, (byte)CombatEventFlags.Critical);

            byte[] eventBytes = MessageCodec.EncodeCombatEvent(new CombatEventMessage(new[]
            {
                new CombatEventEntry(1, 2, 9, CombatEventFlags.Critical),
            }));
            Assert.True(MessageCodec.TryReadCombatEvent(eventBytes, 0, eventBytes.Length, out CombatEventMessage decodedEvent));
            Assert.Equal(CombatEventFlags.Critical, decodedEvent.Entries[0].Flags);
            eventBytes[eventBytes.Length - 1] = 64;
            Assert.False(MessageCodec.TryReadCombatEvent(eventBytes, 0, eventBytes.Length, out _));
        }

        [Fact]
        public void Semilla_De_Combate_Reproduce_La_Secuencia_De_Criticos()
        {
            bool[] first = CritSequence(7);
            Assert.Equal(first, CritSequence(7));
            Assert.NotEqual(string.Join("", first), string.Join("", CritSequence(8)));
        }

        private static bool[] CritSequence(int seed)
        {
            var fixture = CombatFixture(random: null, seed: seed, npcHealth: 100000);
            var results = new bool[50];
            for (int i = 0; i < results.Length; i++)
            {
                Hit(fixture.Combat, fixture.Entities, fixture.Player, fixture.Npc, i * 16L);
                results[i] = (fixture.Events[i].Flags & CombatEventFlags.Critical) != 0;
            }
            return results;
        }

        private static SkillDefinition Skill(int id, int classId) => new SkillDefinition
        {
            Id = id, Name = "Habilidad de prueba", ClassId = classId, RequiredLevel = 1,
            ResourceCost = 0, CooldownSeconds = 1, Range = 3, Damage = 6, DamageType = "physical",
        };

        private static SkillCatalog Catalog(ClassCatalog classes, params SkillDefinition[] skills)
        {
            var catalog = new SkillCatalog { Skills = new List<SkillDefinition>(skills) };
            catalog.Validate(classes);
            return catalog;
        }

        private static (CombatSystem Combat, EntityStore Entities, PlayerEntity Player,
            NpcEntity Npc, CombatEventBuffer Events) CombatFixture(SkillCatalog? skills = null,
            Random? random = null, int classId = 1, ushort weaponId = 0, long seed = 0, int npcHealth = 1000)
        {
            ClassCatalog classes = Classes();
            ItemCatalog items = Items();
            var entities = new EntityStore();
            PlayerEntity player = AddPlayer(entities, 1, classId, new Vec2(8, 10));
            player.EquippedWeaponId = weaponId;
            NpcEntity npc = AddNpc(entities, 100, new Vec2(9, 10), npcHealth);
            var events = new CombatEventBuffer();
            var combat = new CombatSystem(new CombatSettings(1, combatSeed: checked((int)seed)), items,
                classes, events: events, skills: skills, random: random);
            return (combat, entities, player, npc, events);
        }

        private static PlayerEntity AddPlayer(EntityStore entities, int id, int classId, Vec2 position)
        {
            var player = new PlayerEntity(id, id, 8) { ClassId = classId, Position = position,
                HalfSize = new Vec2(0.4f, 0.4f), MaxHealth = 500, Health = 500, Mana = 100, MaxMana = 100 };
            entities.Add(player);
            return player;
        }

        private static NpcEntity AddNpc(EntityStore entities, int id, Vec2 position, int health = 1000)
        {
            var npc = new NpcEntity(id, 9000, 0, position, 0, long.MaxValue, health, 200, -1)
            {
                Position = position,
                HalfSize = new Vec2(0.35f, 0.35f),
            };
            entities.Add(npc);
            return npc;
        }

        private static void Hit(CombatSystem combat, EntityStore entities, PlayerEntity player, Entity target, long tick) =>
            combat.Tick(entities, new List<AttackIntent> { new AttackIntent(player.Id, target.Id) }, tick);

        private static void Use(CombatSystem combat, EntityStore entities, PlayerEntity player,
            Entity target, ushort skillId, long tick) =>
            combat.Tick(entities, new List<AttackIntent> { new AttackIntent(player.Id, target.Id, skillId) }, tick);

        private static ClassCatalog LoadClassesJson(string json) => LoadJson(json, path => ClassCatalog.Load(path));

        private static SkillCatalog LoadSkillsJson(string json, ClassCatalog classes) =>
            LoadJson(json, path => SkillCatalog.Load(path, classes));

        private static T LoadJson<T>(string json, Func<string, T> load)
        {
            string path = Path.Combine(Path.GetTempPath(), "avelora-i28-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, json);
            try { return load(path); }
            finally { File.Delete(path); }
        }

        private static void AssertJsonError(string json, Action<string> validate, string field)
        {
            string path = Path.Combine(Path.GetTempPath(), "avelora-i28-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, json);
            try
            {
                InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => validate(path));
                Assert.Contains(field, error.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally { File.Delete(path); }
        }

        private sealed class FixedRandom : Random
        {
            private readonly Queue<int> _values;
            private int _last;
            public FixedRandom(params int[] values)
            {
                _values = new Queue<int>(values);
                if (values.Length > 0) _last = values[values.Length - 1];
            }
            public int Calls { get; private set; }
            public override int Next(int maxValue)
            {
                Calls++;
                if (_values.Count > 0) _last = _values.Dequeue();
                return _last;
            }
        }
    }
}
