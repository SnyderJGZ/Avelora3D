using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
    /// <summary>Pruebas de I-16: catálogo de habilidades, uso, recarga, recurso y regeneración.</summary>
    public sealed class I16Tests
    {
        [Fact]
        public void Config_Habilidades_Usa_Ruta_Por_Defecto_Y_Resuelve_Desde_El_Archivo()
        {
            var config = new Avelora.Server.Configuration.ServerConfig();
            Assert.Equal("skills.json", config.SkillsPath);
            string directory = Path.Combine(Path.GetTempPath(), "avelora-config");
            string file = Path.Combine(directory, "server.json");
            Assert.Equal(Path.Combine(directory, "skills.json"), config.ResolveSkillsPath(file));
            config.SkillsPath = Path.Combine("datos", "habilidades.json");
            Assert.Equal(Path.Combine(directory, "datos", "habilidades.json"), config.ResolveSkillsPath(file));
            config.SkillsPath = "";
            Assert.Equal("", config.ResolveSkillsPath(file));
        }

        private const ushort WarriorSkill = 4001;
        private const ushort MageSkill = 4002;

        private static string RepoFile(string name) => Path.Combine(AppContext.BaseDirectory, name);

        private sealed class TempJsonFile : IDisposable
        {
            public TempJsonFile(string content)
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "avelora-i16-" + Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(Path, content);
            }

            public string Path { get; }

            public void Dispose()
            {
                try
                {
                    File.Delete(Path);
                }
                catch (IOException)
                {
                }
            }
        }

        private static string SkillJson(
            int id = 4001,
            string name = "Golpe",
            int classId = 1,
            int level = 1,
            int cost = 30,
            string cooldown = "6",
            string range = "1.2",
            int damage = 22,
            string type = "physical")
        {
            return "{ \"id\": " + id.ToString(CultureInfo.InvariantCulture) +
                   ", \"name\": \"" + name + "\"" +
                   ", \"classId\": " + classId.ToString(CultureInfo.InvariantCulture) +
                   ", \"requiredLevel\": " + level.ToString(CultureInfo.InvariantCulture) +
                   ", \"resourceCost\": " + cost.ToString(CultureInfo.InvariantCulture) +
                   ", \"cooldownSeconds\": " + cooldown +
                   ", \"range\": " + range +
                   ", \"damage\": " + damage.ToString(CultureInfo.InvariantCulture) +
                   ", \"damageType\": \"" + type + "\" }";
        }

        private static string SkillsJson(string skills) => "{\"skills\":[" + skills + "]}";

        private static SkillDefinition Skill(
            int id = 4001,
            string name = "Golpe",
            int classId = 1,
            int level = 1,
            int cost = 30,
            double cooldown = 6,
            float range = 1.2f,
            int damage = 22,
            string type = "physical")
        {
            return new SkillDefinition
            {
                Id = id,
                Name = name,
                ClassId = classId,
                RequiredLevel = level,
                ResourceCost = cost,
                CooldownSeconds = cooldown,
                Range = range,
                Damage = damage,
                DamageType = type,
            };
        }

        private static SkillCatalog Catalog(ClassCatalog classes, params SkillDefinition[] skills)
        {
            var catalog = new SkillCatalog { Skills = new List<SkillDefinition>(skills) };
            catalog.Validate(classes);
            return catalog;
        }

        private static ZoneMap SafeZones(MapCollisionGrid map) => ZoneMap.FromDefinitions(new[]
        {
            new ZoneDefinition
            {
                Id = 1,
                Name = "Segura",
                Kind = "safe",
                Rects = new List<ZoneRectDefinition> { new ZoneRectDefinition { X = 10, Y = 4, W = 4, H = 4 } },
            },
        }, map);

        private static Zone NewZone(
            SkillCatalog? skills = null,
            MapCollisionGrid? map = null,
            ClassCatalog? classes = null,
            NpcCatalog? npcs = null,
            ZoneMap? zones = null)
        {
            return new Zone(
                1,
                map ?? MapCollisionGrid.AllWalkable(30, 20),
                new EntityStore(),
                new EntityIdAllocator(),
                new InputQueue(128),
                new MovementSystem(4f, SimulationConstants.FixedDt),
                128,
                4,
                new InterestSettings(30f, 8f, 0f),
                npcs,
                12345,
                new AttackQueue(128),
                new CombatSettings(1f),
                items: null,
                itemActions: new ItemActionQueue(64),
                classes: classes,
                zones: zones,
                pvpModeChangeTicks: 20,
                pvpModeChangeFromPvpTicks: 40,
                pvpForcedTicks: 40,
                skills: skills);
        }

        private static NpcEntity AddNpc(
            Zone zone,
            int id,
            Vec2 position,
            int health = 1000,
            int defense = 0,
            int magicResist = 0)
        {
            var npc = new NpcEntity(
                id,
                2001,
                0f,
                position,
                0f,
                nextDirectionChangeTick: long.MaxValue,
                health,
                200,
                spawnerIndex: -1,
                defense: defense,
                magicResist: magicResist)
            {
                Position = position,
                HalfSize = new Vec2(0.35f, 0.35f),
            };

            zone.AddEntity(npc);
            return npc;
        }

        private static void Use(Zone zone, PlayerEntity player, int targetId, ushort skillId) =>
            Assert.True(zone.Attacks!.TryEnqueue(new AttackIntent(player.Id, targetId, skillId)));

        private static int ExpectedDamage(
            ClassCatalog classes,
            PlayerEntity player,
            int baseDamage,
            int defenseOrResistance)
        {
            DerivedCharacterStats stats = CharacterStats.Calculate(
                classes, player.ClassId, player.Level, player.AllocStr, player.AllocDex, player.AllocInt, player.AllocVit);
            CharacterClass characterClass = classes.Find(player.ClassId)!;
            int primary = characterClass.Primary == "str"
                ? stats.Str
                : characterClass.Primary == "dex"
                    ? stats.Dex
                    : stats.Int;
            int bruto = baseDamage * (100 + primary * classes.PowerPerPoint) / 100;
            return CharacterStats.MitigateDamage(bruto, defenseOrResistance);
        }

        private static NpcCatalog SingleNpcCatalog(int maxHealth = 20)
        {
            return new NpcCatalog
            {
                Types = new List<NpcType>
                {
                    new NpcType
                    {
                        Id = 2001,
                        Name = "Orco",
                        Speed = 0f,
                        HalfWidth = 0.35f,
                        HalfHeight = 0.35f,
                        MaxHealth = maxHealth,
                        Experience = 20,
                        Level = 1,
                        RespawnSeconds = 10f,
                        AggroRadius = 0f,
                        LeashRadius = 8f,
                        AttackDamage = 1,
                        AttackRange = 1f,
                        AttackCooldownSeconds = 1.5f,
                    },
                },
                Spawners = new List<NpcSpawner>
                {
                    new NpcSpawner
                    {
                        TypeId = 2001,
                        X = 10.5f,
                        Y = 10.5f,
                        Count = 1,
                        WanderRadius = 0f,
                    },
                },
            };
        }

        private static NpcEntity OnlyNpc(Zone zone)
        {
            for (int i = 0; i < zone.Entities.Count; i++)
            {
                if (zone.Entities[i] is NpcEntity npc)
                {
                    return npc;
                }
            }

            throw new InvalidOperationException("La zona no tiene ningún PNJ.");
        }

        // ---------------------------------------------------------------- Catálogo

        [Fact]
        public void Skills_Json_Del_Repo_Tiene_Una_Habilidad_Por_Clase_Y_Carga_El_Id()
        {
            SkillCatalog catalog = SkillCatalog.Load(RepoFile("skills.json"), ClassCatalog.Default);

            Assert.Equal(10, catalog.Skills!.Count);
            for (int classId = 1; classId <= 4; classId++)
            {
                Assert.Contains(catalog.Skills, skill => skill.ClassId == classId);
            }

            SkillDefinition skill = Assert.IsType<SkillDefinition>(catalog.Find(4001));
            Assert.Equal("Golpe poderoso", skill.Name);
            Assert.Equal(120, skill.CooldownTicks);
            Assert.Null(catalog.Find(9999));
        }

        [Fact]
        public void La_Recarga_Se_Redondea_Hacia_Arriba_En_Ticks()
        {
            using var file = new TempJsonFile(SkillsJson(SkillJson(cooldown: "0.06")));
            SkillCatalog catalog = SkillCatalog.Load(file.Path, ClassCatalog.Default);

            Assert.Equal(2, catalog.Find(4001)!.CooldownTicks);
        }

        [Fact]
        public void Skills_Json_Invalido_Se_Rechaza()
        {
            string[] invalid =
            {
                SkillsJson(string.Empty),
                SkillsJson(SkillJson(id: 0)),
                SkillsJson(SkillJson(id: 4001) + "," + SkillJson(id: 4001)),
                SkillsJson(SkillJson(name: string.Empty)),
                SkillsJson(SkillJson(name: new string('a', ProtocolConstants.MaxSkillNameBytes + 1))),
                SkillsJson(SkillJson(classId: 9)),
                SkillsJson(SkillJson(level: 0)),
                SkillsJson(SkillJson(cost: -1)),
                SkillsJson(SkillJson(cost: 10001)),
                SkillsJson(SkillJson(cooldown: "0.04")),
                SkillsJson(SkillJson(cooldown: "601")),
                SkillsJson(SkillJson(range: "0.4")),
                SkillsJson(SkillJson(range: "20.1")),
                SkillsJson(SkillJson(damage: 10001)),
                SkillsJson(SkillJson(type: "fire")),
                SkillsJson(string.Join(",", Enumerable.Range(0, ProtocolConstants.MaxSkillsPerClass + 1)
                    .Select(i => SkillJson(id: 5000 + i)))),
            };

            foreach (string json in invalid)
            {
                using var file = new TempJsonFile(json);
                Assert.Throws<InvalidOperationException>(() => SkillCatalog.Load(file.Path, ClassCatalog.Default));
            }
        }

        [Fact]
        public void Una_Recarga_No_Finita_Se_Rechaza()
        {
            ClassCatalog classes = ClassCatalog.Default;
            foreach (double cooldown in new[] { double.NaN, double.PositiveInfinity })
            {
                var catalog = new SkillCatalog
                {
                    Skills = new List<SkillDefinition> { Skill(cooldown: cooldown) },
                };
                Assert.Throws<InvalidOperationException>(() => catalog.Validate(classes));
            }
        }

        [Fact]
        public void Nombre_De_Recurso_Vacio_En_Clase_Sin_Habilidades_Se_Rechaza()
        {
            using var file = new TempJsonFile(File.ReadAllText(RepoFile("classes.json"))
                .Replace("\"name\": \"Energía\"", "\"name\": \"\""));
            ClassCatalog classes = ClassCatalog.Load(file.Path);
            Assert.NotEqual(3, Skill().ClassId);
            var catalog = new SkillCatalog { Skills = new List<SkillDefinition> { Skill() } };
            Assert.Throws<InvalidOperationException>(() => catalog.Validate(classes));
        }

        [Fact]
        public void Nombre_De_Recurso_Largo_En_Clase_Sin_Habilidades_Se_Rechaza()
        {
            using var file = new TempJsonFile(File.ReadAllText(RepoFile("classes.json"))
                .Replace("\"name\": \"Energía\"", "\"name\": \"" + new string('a', 33) + "\""));
            ClassCatalog classes = ClassCatalog.Load(file.Path);
            Assert.NotEqual(3, Skill().ClassId);
            var catalog = new SkillCatalog { Skills = new List<SkillDefinition> { Skill() } };
            Assert.Throws<InvalidOperationException>(() => catalog.Validate(classes));
        }

        [Fact]
        public void Nombre_De_Habilidad_Con_Surrogate_Suelto_Se_Rechaza()
        {
            var catalog = new SkillCatalog { Skills = new List<SkillDefinition> { Skill(name: "a\ud800") } };
            Assert.Throws<InvalidOperationException>(() => catalog.Validate(ClassCatalog.Default));
        }

        [Fact]
        public void Nombre_De_Recurso_Con_Surrogate_Suelto_Se_Rechaza()
        {
            ClassCatalog classes = ClassCatalog.Default;
            classes.Find(3)!.Resource!.Name = "a\ud800";
            var catalog = new SkillCatalog { Skills = new List<SkillDefinition> { Skill() } };
            Assert.Throws<InvalidOperationException>(() => catalog.Validate(classes));
        }

        [Theory]
        [InlineData(65536, 0)]
        [InlineData(65535, 1)]
        [InlineData(65000, 10)]
        public void Recurso_Maximo_Fuera_De_Ushort_Se_Rechaza(int baseResource, int perInt)
        {
            ClassCatalog classes = ClassCatalog.Default;
            classes.Find(1)!.Resource!.Base = baseResource;
            classes.Find(1)!.Resource!.PerInt = perInt;
            Assert.Throws<InvalidOperationException>(() => classes.Validate());
        }

        [Fact]
        public void Recurso_Maximo_Usa_Los_Niveles_Configurados_Y_Admite_El_Limite()
        {
            string original = File.ReadAllText(RepoFile("classes.json"));
            using var file = new TempJsonFile(original.Replace("\"name\": \"Furia\", \"base\": 100, \"perInt\": 0", "\"name\": \"Furia\", \"base\": 65459, \"perInt\": 1"));
            ClassCatalog classes = ClassCatalog.Load(file.Path);
            Assert.Equal(65535, CharacterStats.Calculate(classes, 1, 25, 0, 0, 75, 0).MaxResource);
            LevelTable levels = LevelTable.Default;
            levels.PointsPerLevel = 4;
            Assert.Throws<InvalidOperationException>(() => ClassCatalog.Load(file.Path, levels));
        }

        [Fact]
        public void Regen_Per_Second_Ausente_Es_Cero_Y_Los_Extremos_Se_Rechazan()
        {
            string original = File.ReadAllText(RepoFile("classes.json"));

            using (var file = new TempJsonFile(original.Replace(", \"regenPerSecond\": 4", string.Empty)))
            {
                ClassCatalog classes = ClassCatalog.Load(file.Path);
                Assert.Equal(0, classes.Find(1)!.Resource!.RegenPerSecond);
                Assert.Equal(3, classes.Find(2)!.Resource!.RegenPerSecond);
            }

            foreach (string value in new[] { "-1", "1001" })
            {
                using var file = new TempJsonFile(
                    original.Replace("\"regenPerSecond\": 4", "\"regenPerSecond\": " + value));
                Assert.Throws<InvalidOperationException>(() => ClassCatalog.Load(file.Path));
            }
        }

        // ---------------------------------------------------------------- Uso

        [Fact]
        public void Habilidad_Fisica_Aplica_Dano_Mitigado_Gasta_Recurso_Y_Marca_Botin()
        {
            Zone zone = NewZone(skills: SkillCatalog.Load(RepoFile("skills.json"), ClassCatalog.Default));
            PlayerEntity warrior = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity npc = AddNpc(zone, 100, new Vec2(11f, 10f), health: 1000, defense: 50);
            var used = new List<ushort>();
            zone.SkillUsed += (_, id, _) => used.Add(id);
            int manaBefore = warrior.Mana;

            Use(zone, warrior, npc.Id, WarriorSkill);
            zone.Tick();

            int expected = ExpectedDamage(zone.Classes, warrior, 6 + 6 * 150 / 100, 50);
            Assert.Equal(1000 - expected, npc.Health);
            Assert.Equal(manaBefore - 30, warrior.Mana);
            Assert.Equal(new[] { WarriorSkill }, used);
            Assert.Equal(1, zone.SkillsUsed);
            Assert.Equal(warrior.Id, npc.LootOwnerId);
            Assert.Equal(NpcAiState.Chase, npc.AiState);
            Assert.Equal(warrior.Id, npc.LastAttackerPlayerId);
            Assert.Equal(1, zone.CombatEvents.Count);
            Assert.Equal(CombatEventFlags.None, zone.CombatEvents[0].Flags);
        }

        [Fact]
        public void Habilidad_Magica_Usa_Resistencia_Y_Marca_Ranged_Y_Magical()
        {
            Zone zone = NewZone(skills: SkillCatalog.Load(RepoFile("skills.json"), ClassCatalog.Default));
            PlayerEntity mage = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8, classId: 2);
            NpcEntity npc = AddNpc(zone, 100, new Vec2(15f, 10f), health: 1000, defense: 0, magicResist: 100);

            Use(zone, mage, npc.Id, MageSkill);
            zone.Tick();

            int expected = ExpectedDamage(zone.Classes, mage, 5 + 8 * 100 / 100, 100);
            Assert.Equal(1000 - expected, npc.Health);
            Assert.Equal(CombatEventFlags.Ranged | CombatEventFlags.Magical, zone.CombatEvents[0].Flags);
        }

        [Fact]
        public void Una_Habilidad_Que_Mata_Fija_Killer_Y_Da_Experiencia_Como_El_Ataque()
        {
            Zone zone = NewZone(
                skills: SkillCatalog.Load(RepoFile("skills.json"), ClassCatalog.Default),
                npcs: SingleNpcCatalog());
            PlayerEntity warrior = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity npc = OnlyNpc(zone);
            npc.Health = 1;

            Use(zone, warrior, npc.Id, WarriorSkill);
            zone.Tick();

            Assert.Equal(warrior.Id, npc.KillerPlayerId);
            Assert.Equal(20, warrior.Experience);
            Assert.Equal(CombatEventFlags.Killed, zone.CombatEvents[0].Flags);
        }

        [Fact]
        public void Ataque_Y_Habilidad_En_El_Mismo_Tick_Se_Aceptan_Y_No_Comparten_Recarga()
        {
            Zone zone = NewZone(skills: SkillCatalog.Load(RepoFile("skills.json"), ClassCatalog.Default));
            PlayerEntity warrior = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity npc = AddNpc(zone, 100, new Vec2(11f, 10f), health: 10000);

            Assert.True(zone.Attacks!.TryEnqueue(new AttackIntent(warrior.Id, npc.Id)));
            Use(zone, warrior, npc.Id, WarriorSkill);
            zone.Tick();

            Assert.Equal(1, zone.AttacksOk);
            Assert.Equal(1, zone.SkillsUsed);
            Assert.Equal(2, zone.CombatEvents.Count);
            Assert.Equal(0L, warrior.LastAttackTick);

            // La recarga del ataque básico se mide con LastAttackTick; la habilidad no lo toca.
            warrior.LastAttackTick = 77;
            Use(zone, warrior, npc.Id, WarriorSkill);
            zone.Tick();
            Assert.Equal(1, zone.SkillsUsed);
            Assert.Equal(1, zone.SkillsRejected(SkillRejectReason.Cooldown));
            Assert.Equal(77, warrior.LastAttackTick);
        }

        [Fact]
        public void La_Recarga_De_La_Habilidad_Dura_Sus_Ticks()
        {
            Zone zone = NewZone(skills: SkillCatalog.Load(RepoFile("skills.json"), ClassCatalog.Default));
            PlayerEntity warrior = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity npc = AddNpc(zone, 100, new Vec2(11f, 10f), health: 10000);
            warrior.Mana = 10000;

            Use(zone, warrior, npc.Id, WarriorSkill);
            zone.Tick();
            Assert.Equal(1, zone.SkillsUsed);

            for (int i = 1; i < 119; i++)
            {
                zone.Tick();
            }

            Use(zone, warrior, npc.Id, WarriorSkill);
            zone.Tick();
            Assert.Equal(1, zone.SkillsUsed);
            Assert.Equal(1, zone.SkillsRejected(SkillRejectReason.Cooldown));

            Use(zone, warrior, npc.Id, WarriorSkill);
            zone.Tick();
            Assert.Equal(2, zone.SkillsUsed);
            // El contador es acumulado: el rechazo del tick anterior permanece.
            Assert.Equal(1, zone.SkillsRejected(SkillRejectReason.Cooldown));
        }

        // ---------------------------------------------------------------- Rechazos

        [Fact]
        public void Habilidad_Desconocida_Y_Zona_Sin_Catalogo_No_Gastan_Nada()
        {
            Zone zone = NewZone(skills: SkillCatalog.Load(RepoFile("skills.json"), ClassCatalog.Default));
            PlayerEntity warrior = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity npc = AddNpc(zone, 100, new Vec2(11f, 10f));
            int manaBefore = warrior.Mana;

            Use(zone, warrior, npc.Id, 9999);
            zone.Tick();

            Assert.Equal(1, zone.SkillsRejected(SkillRejectReason.UnknownSkill));
            Assert.Equal(manaBefore, warrior.Mana);
            Assert.Equal(1000, npc.Health);
            Assert.Empty(warrior.SkillReadyTicks);

            Zone noSkills = NewZone();
            PlayerEntity bare = noSkills.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity bareNpc = AddNpc(noSkills, 100, new Vec2(11f, 10f));
            Use(noSkills, bare, bareNpc.Id, WarriorSkill);
            noSkills.Tick();
            Assert.Equal(1, noSkills.SkillsRejected(SkillRejectReason.UnknownSkill));
        }

        [Fact]
        public void Habilidad_De_Otra_Clase_Se_Rechaza()
        {
            Zone zone = NewZone(skills: SkillCatalog.Load(RepoFile("skills.json"), ClassCatalog.Default));
            PlayerEntity warrior = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity npc = AddNpc(zone, 100, new Vec2(11f, 10f));
            int manaBefore = warrior.Mana;

            Use(zone, warrior, npc.Id, MageSkill);
            zone.Tick();

            Assert.Equal(1, zone.SkillsRejected(SkillRejectReason.WrongClass));
            Assert.Equal(manaBefore, warrior.Mana);
            Assert.Equal(1000, npc.Health);
        }

        [Fact]
        public void Nivel_Insuficiente_Se_Rechaza_Y_Al_Llegar_Se_Acepta()
        {
            ClassCatalog classes = ClassCatalog.Default;
            SkillCatalog catalog = Catalog(
                classes,
                Skill(id: 4101, name: "Golpe alto", classId: 1, level: 5, damage: 10, cooldown: 1, range: 1.2f));
            Zone zone = NewZone(skills: catalog, classes: classes);
            PlayerEntity warrior = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity npc = AddNpc(zone, 100, new Vec2(11f, 10f));

            Use(zone, warrior, npc.Id, 4101);
            zone.Tick();
            Assert.Equal(1, zone.SkillsRejected(SkillRejectReason.LevelTooLow));
            Assert.Equal(1000, npc.Health);

            warrior.Level = 5;
            warrior.Mana = 1000;
            Use(zone, warrior, npc.Id, 4101);
            zone.Tick();
            Assert.Equal(1, zone.SkillsUsed);
            Assert.True(npc.Health < 1000);
        }

        [Fact]
        public void Lanzador_Muerto_Se_Rechaza()
        {
            Zone zone = NewZone(skills: SkillCatalog.Load(RepoFile("skills.json"), ClassCatalog.Default));
            PlayerEntity warrior = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity npc = AddNpc(zone, 100, new Vec2(11f, 10f));
            warrior.Action = EntityAction.Dead;
            int manaBefore = warrior.Mana;

            Use(zone, warrior, npc.Id, WarriorSkill);
            zone.Tick();

            Assert.Equal(1, zone.SkillsRejected(SkillRejectReason.Dead));
            Assert.Equal(manaBefore, warrior.Mana);
            Assert.Equal(1000, npc.Health);
        }

        [Fact]
        public void Objetivo_Invalido_Cubre_Inexistente_Uno_Mismo_Muerto_Y_En_Regreso()
        {
            Zone zone = NewZone(skills: SkillCatalog.Load(RepoFile("skills.json"), ClassCatalog.Default));
            PlayerEntity warrior = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity npc = AddNpc(zone, 100, new Vec2(11f, 10f));
            int manaBefore = warrior.Mana;

            Use(zone, warrior, 99999, WarriorSkill);
            zone.Tick();
            Use(zone, warrior, warrior.Id, WarriorSkill);
            zone.Tick();
            npc.Action = EntityAction.Dead;
            Use(zone, warrior, npc.Id, WarriorSkill);
            zone.Tick();
            npc.Action = EntityAction.None;
            npc.AiState = NpcAiState.Return;
            Use(zone, warrior, npc.Id, WarriorSkill);
            zone.Tick();

            Assert.Equal(4, zone.SkillsRejected(SkillRejectReason.InvalidTarget));
            Assert.Equal(manaBefore, warrior.Mana);
            Assert.Equal(1000, npc.Health);
            Assert.Empty(warrior.SkillReadyTicks);
        }

        [Fact]
        public void Zona_Segura_Rechaza_Al_Lanzador_O_Al_Objetivo()
        {
            MapCollisionGrid map = MapCollisionGrid.AllWalkable(30, 20);
            Zone zone = NewZone(
                skills: SkillCatalog.Load(RepoFile("skills.json"), ClassCatalog.Default),
                map: map,
                zones: SafeZones(map));
            PlayerEntity inside = zone.SpawnPlayer(1, new Vec2(11f, 5f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity outside = AddNpc(zone, 100, new Vec2(20f, 5f));

            Use(zone, inside, outside.Id, WarriorSkill);
            zone.Tick();
            Assert.Equal(1, zone.SkillsRejected(SkillRejectReason.SafeZone));

            PlayerEntity outsidePlayer = zone.SpawnPlayer(2, new Vec2(20f, 5f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity insideNpc = AddNpc(zone, 101, new Vec2(11.5f, 5f));
            Use(zone, outsidePlayer, insideNpc.Id, WarriorSkill);
            zone.Tick();
            Assert.Equal(2, zone.SkillsRejected(SkillRejectReason.SafeZone));
        }

        [Fact]
        public void Fuera_De_Alcance_Se_Rechaza()
        {
            Zone zone = NewZone(skills: SkillCatalog.Load(RepoFile("skills.json"), ClassCatalog.Default));
            PlayerEntity warrior = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity npc = AddNpc(zone, 100, new Vec2(15f, 10f));

            Use(zone, warrior, npc.Id, WarriorSkill);
            zone.Tick();

            Assert.Equal(1, zone.SkillsRejected(SkillRejectReason.OutOfRange));
            Assert.Equal(1000, npc.Health);
        }

        [Fact]
        public void Sin_Linea_De_Vision_Se_Rechaza()
        {
            MapCollisionGrid map = MapCollisionGrid.FromRows(new[]
            {
                "....#....", "....#....", "....#....", "....#....", "....#....",
            });
            Zone zone = NewZone(
                skills: SkillCatalog.Load(RepoFile("skills.json"), ClassCatalog.Default),
                map: map);
            PlayerEntity mage = zone.SpawnPlayer(1, new Vec2(1.5f, 2.5f), new Vec2(0.4f, 0.4f), 8, classId: 2);
            NpcEntity npc = AddNpc(zone, 100, new Vec2(6.5f, 2.5f));

            Use(zone, mage, npc.Id, MageSkill);
            zone.Tick();

            Assert.Equal(1, zone.SkillsRejected(SkillRejectReason.NoLineOfSight));
            Assert.Equal(1000, npc.Health);
        }

        [Fact]
        public void Recurso_Insuficiente_Se_Rechaza_Sin_Recarga()
        {
            Zone zone = NewZone(skills: SkillCatalog.Load(RepoFile("skills.json"), ClassCatalog.Default));
            PlayerEntity warrior = zone.SpawnPlayer(
                1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8, initialMana: 10);
            NpcEntity npc = AddNpc(zone, 100, new Vec2(11f, 10f));

            // Consume la regeneración del tick 0 para medir que el rechazo no gasta nada.
            zone.Tick();
            warrior.Mana = 10;

            Use(zone, warrior, npc.Id, WarriorSkill);
            zone.Tick();

            Assert.Equal(1, zone.SkillsRejected(SkillRejectReason.NotEnoughResource));
            Assert.Equal(10, warrior.Mana);
            Assert.Empty(warrior.SkillReadyTicks);
            Assert.Equal(1000, npc.Health);
        }

        // ---------------------------------------------------------------- PvP

        [Fact]
        public void Una_Habilidad_Pvp_Usa_La_Defensa_Del_Objetivo_Y_Cuenta_La_Muerte()
        {
            Zone zone = NewZone(skills: SkillCatalog.Load(RepoFile("skills.json"), ClassCatalog.Default));
            PlayerEntity attacker = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            PlayerEntity victim = zone.SpawnPlayer(
                2, new Vec2(11f, 10f), new Vec2(0.4f, 0.4f), 8, initialHealth: 1, classId: 2);
            attacker.PvpMode = PvpMode.Pvp;
            victim.PvpMode = PvpMode.Pvp;

            Use(zone, attacker, victim.Id, WarriorSkill);
            zone.Tick();

            Assert.Equal(1, zone.SkillsUsed);
            Assert.Equal(0, victim.Health);
            Assert.Equal(1, zone.PvpKills);
            Assert.Equal(EntityAction.Dead, victim.Action);
        }

        [Fact]
        public void PvP_No_Permitido_Si_Alguno_Es_Pve()
        {
            Zone zone = NewZone(skills: SkillCatalog.Load(RepoFile("skills.json"), ClassCatalog.Default));
            PlayerEntity attacker = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            PlayerEntity victim = zone.SpawnPlayer(2, new Vec2(11f, 10f), new Vec2(0.4f, 0.4f), 8);

            Use(zone, attacker, victim.Id, WarriorSkill);
            zone.Tick();
            Assert.Equal(1, zone.SkillsRejected(SkillRejectReason.PvpNotAllowed));

            attacker.PvpMode = PvpMode.Pvp;
            Use(zone, attacker, victim.Id, WarriorSkill);
            zone.Tick();
            Assert.Equal(2, zone.SkillsRejected(SkillRejectReason.PvpNotAllowed));
            Assert.Equal(victim.MaxHealth, victim.Health);
        }

        // ---------------------------------------------------------------- Recurso

        [Fact]
        public void La_Regeneracion_Suma_Una_Vez_Por_Segundo_Y_No_Pasa_Del_Maximo()
        {
            Zone zone = NewZone(skills: SkillCatalog.Load(RepoFile("skills.json"), ClassCatalog.Default));
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8, initialMana: 0);

            zone.Tick();
            Assert.Equal(4, player.Mana);
            for (int i = 1; i < 20; i++)
            {
                zone.Tick();
            }

            Assert.Equal(4, player.Mana);
            zone.Tick();
            Assert.Equal(8, player.Mana);

            player.Mana = player.MaxMana - 1;
            for (int i = 0; i < 20; i++)
            {
                zone.Tick();
            }

            Assert.Equal(player.MaxMana, player.Mana);
        }

        [Fact]
        public void Un_Jugador_Muerto_No_Regenera_Y_Sin_Catalogo_Tampoco()
        {
            Zone zone = NewZone(skills: SkillCatalog.Load(RepoFile("skills.json"), ClassCatalog.Default));
            PlayerEntity dead = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8, initialMana: 0);
            dead.Action = EntityAction.Dead;
            for (int i = 0; i < 40; i++)
            {
                zone.Tick();
            }

            Assert.Equal(0, dead.Mana);

            Zone noSkills = NewZone();
            PlayerEntity bare = noSkills.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8, initialMana: 0);
            for (int i = 0; i < 40; i++)
            {
                noSkills.Tick();
            }

            Assert.Equal(0, bare.Mana);
        }

        [Fact]
        public void Resource_Changed_Emite_Solo_Cuando_Cambia_El_Recurso_O_El_Maximo()
        {
            ClassCatalog classes = ClassCatalog.Default;
            Zone zone = NewZone(skills: SkillCatalog.Load(RepoFile("skills.json"), classes), classes: classes);
            PlayerEntity player = zone.SpawnPlayer(
                1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8, classId: 2, initialMana: 10);
            var changes = new List<int>();
            zone.ResourceChanged += changed => changes.Add(changed.Mana);
            int maxBefore = player.MaxMana;

            zone.Tick();
            Assert.Equal(new[] { 13 }, changes);

            for (int i = 1; i < 20; i++)
            {
                zone.Tick();
            }

            Assert.Single(changes);

            zone.Tick();
            Assert.Equal(new[] { 13, 16 }, changes);

            Assert.True(zone.ItemActions!.TryEnqueue(ItemAction.AllocatePoints(player.Id, 0, 0, 1, 0)));
            zone.Tick();
            Assert.Equal(3, changes.Count);
            Assert.True(player.MaxMana > maxBefore);
            Assert.Equal(16, player.Mana);
        }

        [Fact]
        public void Sin_Catalogo_Resource_Changed_Nunca_Se_Emite()
        {
            Zone zone = NewZone();
            zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8, initialMana: 0);
            int changes = 0;
            zone.ResourceChanged += _ => changes++;

            for (int i = 0; i < 50; i++)
            {
                zone.Tick();
            }

            Assert.Equal(0, changes);
        }
    }
}
