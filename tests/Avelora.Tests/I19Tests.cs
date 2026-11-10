using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avelora.Balance;
using Avelora.Bots;
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
    public sealed class I19Tests
    {
        private static string Data(string file) => Path.Combine(AppContext.BaseDirectory, file);
        private static ClassCatalog Classes() => ClassCatalog.Load(Data("classes.json"));
        private static ItemCatalog Items() => ItemCatalog.Load(Data("items.json"));

        [Fact]
        public void Datos_Repo_Tiene_Las_Habilidades_Y_Niveles_De_I19()
        {
            SkillCatalog catalog = SkillCatalog.Load(Data("skills.json"), Classes());
            List<SkillDefinition> skills = catalog.Skills!;
            Assert.Null(catalog.Find(4002));
            Assert.Equal(new[] { 4001, 4007, 4011, 4012, 4008, 4009, 4005, 4013,
                4003, 4014, 4015, 4016, 4006, 4004, 4010, 4017, 4018 },
                skills.Select(skill => skill.Id));
            Assert.Equal(new[] { 1, 5, 10, 20, 1, 5, 10, 20, 1, 5, 10, 20, 1, 1, 5, 10, 20 },
                skills.Select(skill => skill.RequiredLevel));
            Assert.Equal(new[] { 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 4 },
                skills.Select(skill => skill.ClassId));
            for (int classId = 1; classId <= 4; classId++)
                Assert.Equal(classId == 4 ? 5 : 4, skills.Count(skill => skill.ClassId == classId));
            foreach (int id in new[] { 4012, 4013, 4016, 4018 })
                Assert.InRange(catalog.Find(id)!.CooldownSeconds, 45, 90);
            Assert.Equal(1, catalog.Find(4001)!.Hits);
            Assert.False(catalog.Find(4001)!.AreaSelf);
        }

        [Theory]
        [InlineData("\"hits\":0", "hits")]
        [InlineData("\"hits\":6", "hits")]
        [InlineData("\"hits\":2,\"radius\":2.5,\"maxTargets\":2", "hits")]
        [InlineData("\"hits\":2,\"effect\":\"heal\"", "hits")]
        [InlineData("\"critBonus\":-1", "critBonus")]
        [InlineData("\"critBonus\":501", "critBonus")]
        [InlineData("\"critBonus\":10,\"effect\":\"none\",\"status\":\"bonus\"", "critBonus")]
        [InlineData("\"areaCenter\":\"foo\"", "areaCenter")]
        [InlineData("\"areaCenter\":\"self\"", "areaCenter")]
        [InlineData("\"areaCenter\":\"self\",\"status\":\"slow\",\"statusSeconds\":3,\"statusAmount\":40", "areaCenter")]
        [InlineData("\"effect\":\"heal\",\"radius\":4,\"maxTargets\":2", "areaCenter")]
        [InlineData("\"effect\":\"heal\",\"status\":\"slow\",\"statusSeconds\":3,\"statusAmount\":40", "status")]
        [InlineData("\"effect\":\"heal\",\"status\":\"shield\",\"statusSeconds\":3,\"statusAmount\":5", "status")]
        public void Catalogo_Rechaza_Campos_Nuevos_Invalidos(string fields, string expectedField)
        {
            string json = "{\"skills\":[{\"id\":5001,\"name\":\"prueba\",\"classId\":1,\"requiredLevel\":1," +
                "\"resourceCost\":1,\"cooldownSeconds\":1,\"range\":3,\"damage\":6,\"damageType\":\"physical\"," + fields + "}]}";
            string path = Path.Combine(Path.GetTempPath(), "avelora-i19-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, json);
            try
            {
                InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => SkillCatalog.Load(path, Classes()));
                Assert.Contains(expectedField, error.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Catalogo_Legado_Usa_Los_Valores_Predeterminados_Neutros()
        {
            SkillCatalog catalog = LoadSkill("\"effect\":\"damage\"");
            Assert.Equal(1, catalog.Find(5001)!.Hits);
            Assert.Equal(0, catalog.Find(5001)!.CritBonus);
            Assert.False(catalog.Find(5001)!.AreaSelf);
        }

        [Fact]
        public void Nivel_Minimo_De_Habilidad_Se_Valida_Con_Datos_Del_Repo()
        {
            SkillCatalog skills = SkillCatalog.Load(Data("skills.json"), Classes());
            var low = CombatFixture(skills);
            low.Player.Level = 9;
            SkillRejectReason? reason = null;
            low.Combat.SkillRejected += (_, _, rejected) => reason = rejected;
            Use(low, int.MaxValue, 4011);
            Assert.Equal(SkillRejectReason.LevelTooLow, reason);

            var ready = CombatFixture(skills);
            ready.Player.Level = 10;
            Use(ready, int.MaxValue, 4011);
            Assert.Equal(1, ready.Combat.SkillsUsed);
        }

        [Fact]
        public void Golpes_Multiples_Critican_Por_Golpe_Y_Paran_Al_Matar()
        {
            SkillDefinition skill = NewSkill(hits: 3, damage: 10);
            var fixture = CombatFixture(Catalog(Classes(), skill), new SequenceRandom(0, 9999, 0));
            int raw = SkillRaw(fixture.Player, skill);
            Use(fixture, fixture.Npc.Id, 5001);
            int expected = CombatMath.MitigateDamage(CombatMath.CritDamage(raw, 150), fixture.Npc.Defense) * 2 +
                CombatMath.MitigateDamage(raw, fixture.Npc.Defense);
            Assert.Equal(1000 - expected, fixture.Npc.Health);
            Assert.Equal(3, fixture.Events.Count);
            Assert.True((fixture.Events[0].Flags & CombatEventFlags.Critical) != 0);
            Assert.False((fixture.Events[1].Flags & CombatEventFlags.Critical) != 0);
            Assert.True((fixture.Events[2].Flags & CombatEventFlags.Critical) != 0);

            SkillDefinition ordinarySkill = NewSkill(hits: 3, damage: 10);
            var ordinary = CombatFixture(Catalog(Classes(), ordinarySkill), new FixedRandom(9999));
            int normalRaw = SkillRaw(ordinary.Player, ordinarySkill);
            Use(ordinary, ordinary.Npc.Id, 5001);
            Assert.Equal(1000 - 3 * CombatMath.MitigateDamage(normalRaw, ordinary.Npc.Defense), ordinary.Npc.Health);

            var dying = CombatFixture(Catalog(Classes(), NewSkill(hits: 3, damage: 1)), new FixedRandom(9999), npcHealth: 2);
            Use(dying, dying.Npc.Id, 5001);
            Assert.Equal(2, dying.Events.Count);
        }

        [Fact]
        public void CritBonus_Afecta_Habilidad_Pero_No_Ataque_Basico()
        {
            SkillDefinition skill = NewSkill(critBonus: 50, damage: 10);
            var skillFixture = CombatFixture(Catalog(Classes(), skill), new FixedRandom(0));
            int skillRaw = SkillRaw(skillFixture.Player, skill);
            Use(skillFixture, skillFixture.Npc.Id, 5001);
            Assert.Equal(1000 - CombatMath.MitigateDamage(CombatMath.CritDamage(skillRaw, 200), 0),
                skillFixture.Npc.Health);

            var basicFixture = CombatFixture(random: new FixedRandom(0));
            int basicRaw = CombatMath.GetAttackDamage(Classes(), 1, basicFixture.Player.Level,
                0, 0, 0, 0, null, out _, Items(), basicFixture.Player.Equipment);
            Hit(basicFixture);
            Assert.Equal(1000 - CombatMath.MitigateDamage(CombatMath.CritDamage(basicRaw, 150), 0),
                basicFixture.Npc.Health);
        }

        [Fact]
        public void Area_Propia_Ignora_Objetivo_Y_Elige_Los_Mas_Cercanos()
        {
            SkillDefinition skill = NewSkill(radius: 2, maxTargets: 2, areaCenter: "self", damage: 6,
                resourceCost: 10);
            var fixture = CombatFixture(Catalog(Classes(), skill), new FixedRandom(9999));
            NpcEntity near = AddNpc(fixture.Entities, 101, new Vec2(11.5f, 10));
            NpcEntity far = AddNpc(fixture.Entities, 102, new Vec2(11.8f, 10));
            NpcEntity outside = AddNpc(fixture.Entities, 103, new Vec2(12.1f, 10));
            int mana = fixture.Player.Mana;
            int casterHealth = fixture.Player.Health;
            int usedTarget = 0;
            fixture.Combat.SkillUsed += (_, _, targetId) => usedTarget = targetId;

            Use(fixture, fixture.Player.Id, 5001);

            Assert.True(fixture.Npc.Health < 1000);
            Assert.True(near.Health < 1000);
            Assert.Equal(1000, far.Health);
            Assert.Equal(1000, outside.Health);
            Assert.Equal(casterHealth, fixture.Player.Health);
            Assert.Equal(mana - 10, fixture.Player.Mana);
            Assert.Equal(fixture.Player.Id, usedTarget);

            var missingTarget = CombatFixture(Catalog(Classes(), skill), new FixedRandom(9999));
            Use(missingTarget, int.MaxValue, 5001);
            Assert.True(missingTarget.Npc.Health < 1000);

            var empty = CombatFixture(Catalog(Classes(), skill), new FixedRandom(9999));
            empty.Npc.Position = new Vec2(20, 20);
            int beforeMana = empty.Player.Mana;
            SkillRejectReason? reason = null;
            empty.Combat.SkillRejected += (_, _, rejected) => reason = rejected;
            Use(empty, 987654, 5001);
            Assert.Equal(SkillRejectReason.InvalidTarget, reason);
            Assert.Equal(beforeMana, empty.Player.Mana);
            Assert.Empty(empty.Player.SkillReadyTicks);
        }

        [Fact]
        public void Estado_Propio_Ignora_El_Objetivo_Indicado()
        {
            SkillDefinition skill = NewSkill(effect: "none", status: "bonus", statusSeconds: 5,
                statusAmount: 4, areaCenter: "self", damage: 0);
            var fixture = CombatFixture(Catalog(Classes(), skill));
            PlayerEntity ally = AddPlayer(fixture.Entities, 2, new Vec2(8, 11));
            Use(fixture, ally.Id, 5001);
            Assert.Equal(4, fixture.Player.Statuses.Bonus);
            Assert.Equal(0, ally.Statuses.Bonus);
        }

        [Fact]
        public void Cura_En_Area_Cura_Al_Lanzador_Y_Aliados_Validos_Y_Rechaza_Vida_Llena()
        {
            SkillDefinition skill = NewSkill(effect: "heal", radius: 4, maxTargets: 6,
                areaCenter: "self", damage: 10, weaponPercent: 0, resourceCost: 25);
            var fixture = CombatFixture(Catalog(Classes(), skill), new FixedRandom(9999));
            PlayerEntity near = AddPlayer(fixture.Entities, 2, new Vec2(11, 10));
            PlayerEntity outside = AddPlayer(fixture.Entities, 3, new Vec2(15, 10));
            NpcEntity npc = AddNpc(fixture.Entities, 101, new Vec2(9, 10));
            fixture.Player.Health = 495;
            near.Health = 495;
            outside.Health = 495;
            Use(fixture, int.MaxValue, 5001);
            Assert.Equal(500, fixture.Player.Health);
            Assert.Equal(500, near.Health);
            Assert.Equal(495, outside.Health);
            Assert.Equal(1000, npc.Health);
            Assert.Equal(2, fixture.Combat.HealsDone);
            Assert.Equal(75, fixture.Player.Mana);

            var full = CombatFixture(Catalog(Classes(), skill));
            int mana = full.Player.Mana;
            SkillRejectReason? reason = null;
            full.Combat.SkillRejected += (_, _, rejected) => reason = rejected;
            Use(full, full.Npc.Id, 5001);
            Assert.Equal(SkillRejectReason.FullHealth, reason);
            Assert.Equal(mana, full.Player.Mana);

            var pvp = CombatFixture(Catalog(Classes(), skill), new FixedRandom(9999));
            PlayerEntity marked = AddPlayer(pvp.Entities, 2, new Vec2(11, 10));
            marked.Health = 495;
            marked.PvpMode = PvpMode.Pvp;
            pvp.Player.Health = 495;
            Use(pvp, int.MaxValue, 5001);
            Assert.Equal(500, pvp.Player.Health);
            Assert.Equal(495, marked.Health);
        }

        [Fact]
        public void Cura_En_Area_Aplica_Critico_Y_Escudo_Aun_Con_Vida_Llena()
        {
            SkillDefinition skill = NewSkill(effect: "heal", radius: 4, maxTargets: 6,
                areaCenter: "self", damage: 10, weaponPercent: 0, status: "shield",
                statusSeconds: 5, statusAmount: 5, resourceCost: 25);
            var fixture = CombatFixture(Catalog(Classes(), skill), new FixedRandom(0, 0));
            PlayerEntity ally = AddPlayer(fixture.Entities, 2, new Vec2(11, 10));
            Use(fixture, int.MaxValue, 5001);
            Assert.Equal(5, fixture.Player.Statuses.Get(StatusKind.Shield).Amount);
            Assert.Equal(5, ally.Statuses.Get(StatusKind.Shield).Amount);
            Assert.Equal(0, fixture.Events.Count);
            Assert.Equal(75, fixture.Player.Mana);

            var critical = CombatFixture(Catalog(Classes(), skill), new FixedRandom(0));
            PlayerEntity injured = AddPlayer(critical.Entities, 2, new Vec2(11, 10));
            critical.Player.Health = 490;
            injured.Health = 490;
            Use(critical, int.MaxValue, 5001);
            Assert.Equal(500, critical.Player.Health);
            Assert.Equal(500, injured.Health);
            Assert.Equal(2, critical.Events.Count);
            Assert.All(new[] { critical.Events[0], critical.Events[1] }, entry =>
                Assert.Equal(CombatEventFlags.Heal | CombatEventFlags.Critical, entry.Flags));
        }

        [Fact]
        public void Catalogo_De_Protocolo_Incluye_Campos_De_I19_Y_V25()
        {
            Assert.Equal((ushort)25, ProtocolConstants.Version);
            var message = new SkillCatalogMessage("recurso", new[]
            {
                new SkillCatalogEntry(5001, 1, 0, 10, 20, 3, 6, "Prueba", hits: 3,
                    areaCenter: 1, critBonus: 50),
            }, 20);
            byte[] bytes = MessageCodec.EncodeSkillCatalog(message);
            Assert.True(MessageCodec.TryReadSkillCatalog(bytes, 0, bytes.Length, out SkillCatalogMessage decoded));
            Assert.Equal(20, decoded.GlobalCooldownTicks);
            Assert.Equal((byte)3, decoded.Skills[0].Hits);
            Assert.Equal((byte)1, decoded.Skills[0].AreaCenter);
            Assert.Equal((ushort)50, decoded.Skills[0].CritBonus);
        }

        [Fact]
        public void Rotacion_Bot_Es_Generica_Y_Respeta_Nivel_Cura_Recarga_Global_Y_Alcance()
        {
            SkillCatalogEntry[] skills =
            {
                BotSkill(1, 1, 0, 0, StatusKind.None, 0, 5),
                BotSkill(2, 5, 0, 0, StatusKind.None, 0, 5),
                BotSkill(3, 1, 1, 0, StatusKind.None, 0, 5),
                BotSkill(6, 5, 1, 0, StatusKind.None, 0, 5),
                BotSkill(4, 5, 0, 1, StatusKind.None, 3, 5),
                BotSkill(5, 5, 2, 0, StatusKind.Taunt, 0, 5),
            };
            long[] ready = new long[skills.Length];
            Assert.Equal(0, BotSkillRotation.Pick(skills, 1, 100, 100, ready, 0, 1, false, 80));
            Assert.Equal(1, BotSkillRotation.Pick(skills, 5, 100, 100, ready, 0, 1, false, 80));
            Assert.Equal(3, BotSkillRotation.Pick(skills, 5, 100, 100, ready, 0, 1, false, 60));
            Assert.Equal(-1, BotSkillRotation.Pick(skills, 5, 100, 100, ready, 101, 1, false, 60));
            Assert.Equal(1, BotSkillRotation.Pick(new[] { skills[4], skills[0] }, 5, 100, 100,
                new long[2], 0, 3.1f, false, 80));
            Assert.Equal(1, BotSkillRotation.Pick(new[] { skills[5], skills[0] }, 5, 100, 100,
                new long[2], 0, 1, true, 80));
            Assert.Equal(-1, BotSkillRotation.Pick(new[] { skills[0] }, 5, 100, 100,
                new long[1], 0, float.NaN, false, 80));
        }

        [Fact]
        public void Simulador_Mide_Rotacion_Y_Duelos_Deterministas()
        {
            CombatSimulator simulator = Simulator();
            Assert.Equal(33, CombatMath.ExpectedSkillDamage(10, 3, 1000, 150, 50), 9);
            RotationResult rotation = simulator.MeasureRotation(3, 10, "equilibrado", "franja");
            Assert.Equal(3, rotation.Skills.Count);
            Assert.Equal(100, rotation.Skills.Sum(skill => skill.Percent) +
                rotation.BasicDamage * 100 / rotation.TotalDamage, 2);
            DuelSeriesResult first = simulator.SimulateDuels(4, 1, 1, "equilibrado", "franja", 10, 7);
            DuelSeriesResult second = simulator.SimulateDuels(4, 1, 1, "equilibrado", "franja", 10, 7);
            Assert.Equal(first.WinsA, second.WinsA);
            Assert.Equal(first.WinsB, second.WinsB);
            Assert.Equal(first.Draws, second.Draws);
            Assert.Equal(10, first.WinsA + first.WinsB + first.Draws);
            Assert.True(first.HealsA > 0);
        }

        private static SkillCatalog LoadSkill(string fields)
        {
            string json = "{\"skills\":[{\"id\":5001,\"name\":\"prueba\",\"classId\":1,\"requiredLevel\":1," +
                "\"resourceCost\":1,\"cooldownSeconds\":1,\"range\":3,\"damage\":6,\"damageType\":\"physical\"," + fields + "}]}";
            string path = Path.Combine(Path.GetTempPath(), "avelora-i19-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, json);
            try { return SkillCatalog.Load(path, Classes()); }
            finally { File.Delete(path); }
        }

        private static CombatSimulator Simulator()
        {
            ClassCatalog classes = Classes();
            ItemCatalog items = Items();
            return new CombatSimulator(classes, items, SkillCatalog.Load(Data("skills.json"), classes),
                NpcCatalog.Load(Data("npcs.json")), LevelTable.Load(Data("levels.json"), items));
        }

        private static SkillDefinition NewSkill(int hits = 1, int damage = 6, int critBonus = 0,
            float radius = 0, int maxTargets = 0, string areaCenter = "target", string effect = "damage",
            string status = "", double statusSeconds = 0, int statusAmount = 0, int weaponPercent = 0,
            int resourceCost = 0) =>
            new SkillDefinition { Id = 5001, Name = "prueba", ClassId = 1, RequiredLevel = 1,
                ResourceCost = resourceCost, CooldownSeconds = 1, Range = 3, Damage = damage,
                WeaponPercent = weaponPercent, DamageType = "physical", Hits = hits,
                CritBonus = critBonus, Radius = radius, MaxTargets = maxTargets,
                AreaCenter = areaCenter, Effect = effect, Status = status,
                StatusSeconds = statusSeconds, StatusAmount = statusAmount };

        private static SkillCatalog Catalog(ClassCatalog classes, params SkillDefinition[] definitions)
        {
            var catalog = new SkillCatalog { Skills = new List<SkillDefinition>(definitions) };
            catalog.Validate(classes);
            return catalog;
        }

        private static (CombatSystem Combat, EntityStore Entities, PlayerEntity Player,
            NpcEntity Npc, CombatEventBuffer Events) CombatFixture(SkillCatalog? skills = null,
            Random? random = null, int npcHealth = 1000)
        {
            ClassCatalog classes = Classes();
            ItemCatalog items = Items();
            var entities = new EntityStore();
            PlayerEntity player = AddPlayer(entities, 1, new Vec2(10, 10));
            NpcEntity npc = AddNpc(entities, 100, new Vec2(11, 10), npcHealth);
            var events = new CombatEventBuffer();
            var combat = new CombatSystem(new CombatSettings(1), items, classes, events: events,
                skills: skills, random: random ?? new FixedRandom(9999));
            return (combat, entities, player, npc, events);
        }

        private static PlayerEntity AddPlayer(EntityStore entities, int id, Vec2 position)
        {
            var player = new PlayerEntity(id, id, 8) { ClassId = 1, Position = position,
                HalfSize = new Vec2(0.4f, 0.4f), MaxHealth = 500, Health = 500, Mana = 100, MaxMana = 100 };
            entities.Add(player);
            return player;
        }

        private static NpcEntity AddNpc(EntityStore entities, int id, Vec2 position, int health = 1000)
        {
            var npc = new NpcEntity(id, 9000, 0, position, 0, long.MaxValue, health, 200, -1)
            { Position = position, HalfSize = new Vec2(0.35f, 0.35f) };
            entities.Add(npc);
            return npc;
        }

        private static int SkillRaw(PlayerEntity player, SkillDefinition skill) =>
            CombatMath.GetSkillAmount(Classes(), player.ClassId, player.Level, player.AllocStr,
                player.AllocDex, player.AllocInt, player.AllocVit, skill.Damage, skill.WeaponPercent,
                null, Items(), player.Equipment);

        private static void Use((CombatSystem Combat, EntityStore Entities, PlayerEntity Player,
            NpcEntity Npc, CombatEventBuffer Events) fixture, int targetId, ushort skillId) =>
            fixture.Combat.Tick(fixture.Entities,
                new List<AttackIntent> { new AttackIntent(fixture.Player.Id, targetId, skillId) }, 0);

        private static void Hit((CombatSystem Combat, EntityStore Entities, PlayerEntity Player,
            NpcEntity Npc, CombatEventBuffer Events) fixture) =>
            fixture.Combat.Tick(fixture.Entities,
                new List<AttackIntent> { new AttackIntent(fixture.Player.Id, fixture.Npc.Id) }, 0);

        private static SkillCatalogEntry BotSkill(ushort id, byte level, byte effect, byte areaCenter,
            StatusKind status, float radius, float range) =>
            new SkillCatalogEntry(id, level, 0, 1, 20, range, 5, "skill" + id, effect, radius,
                status, status == StatusKind.None ? (ushort)0 : (ushort)20,
                status == StatusKind.None ? (ushort)0 : (ushort)1, areaCenter: areaCenter);

        private sealed class FixedRandom : Random
        {
            private readonly int[] _values;
            private int _index;
            public FixedRandom(params int[] values) => _values = values;
            public override int Next(int maxValue) => Math.Min(_values[Math.Min(_index++, _values.Length - 1)], maxValue - 1);
        }

        private sealed class SequenceRandom : Random
        {
            private readonly Queue<int> _values;
            private int _last;
            public SequenceRandom(params int[] values)
            {
                _values = new Queue<int>(values);
                if (values.Length > 0) _last = values[values.Length - 1];
            }
            public override int Next(int maxValue)
            {
                if (_values.Count > 0) _last = _values.Dequeue();
                return Math.Min(_last, maxValue - 1);
            }
        }
    }
}
