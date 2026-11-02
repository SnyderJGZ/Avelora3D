using System;
using System.Collections.Generic;
using System.IO;
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
    public sealed class I18Tests
    {
        private static SkillDefinition Skill(
            int id, string name, int classId, int cost, double cooldown, float range,
            int damage, string type, string effect = "damage", string status = "",
            double statusSeconds = 0, int statusAmount = 0)
        {
            return new SkillDefinition
            {
                Id = id, Name = name, ClassId = classId, RequiredLevel = 1,
                ResourceCost = cost, CooldownSeconds = cooldown, Range = range,
                Damage = damage, DamageType = type, Effect = effect, Status = status,
                StatusSeconds = statusSeconds, StatusAmount = statusAmount,
            };
        }

        private static SkillCatalog Catalog(ClassCatalog classes, params SkillDefinition[] skills)
        {
            var catalog = new SkillCatalog { Skills = new List<SkillDefinition>(skills) };
            catalog.Validate(classes);
            return catalog;
        }

        private static SkillDefinition[] AllSkills() => new[]
        {
            Skill(4002, "Proyectil arcano", 2, 20, 4, 6, 18, "magical"),
            Skill(4004, "Castigo", 4, 20, 5, 5, 14, "magical"),
            Skill(4007, "Provocar", 1, 20, 10, 5, 0, "physical", "none", "taunt", 4, 0),
            Skill(4008, "Rayo helado", 2, 25, 7, 6, 10, "magical", status: "slow", statusSeconds: 3, statusAmount: 40),
            Skill(4009, "Escudo arcano", 2, 30, 15, 6, 0, "magical", "none", "shield", 10, 25),
            Skill(4010, "Bendición", 4, 30, 20, 6, 0, "magical", "none", "bonus", 15, 4),
        };

        private static Zone NewZone(
            SkillCatalog? skills = null,
            MapCollisionGrid? map = null,
            ClassCatalog? classes = null,
            NpcCatalog? npcs = null,
            ZoneMap? zones = null)
        {
            return new Zone(
                1, map ?? MapCollisionGrid.AllWalkable(30, 20), new EntityStore(), new EntityIdAllocator(),
                new InputQueue(128), new MovementSystem(4f, SimulationConstants.FixedDt), 128, 4,
                new InterestSettings(30f, 8f, 0f), npcs, 12345, new AttackQueue(128),
                new CombatSettings(1f), items: null, itemActions: new ItemActionQueue(64),
                classes: classes, zones: zones, pvpModeChangeTicks: 20, pvpModeChangeFromPvpTicks: 40,
                pvpForcedTicks: 40, skills: skills);
        }

        private static Zone NewSkillZone(MapCollisionGrid? map = null, ZoneMap? zones = null,
            NpcCatalog? npcs = null) => NewZone(Catalog(ClassCatalog.Default, AllSkills()), map, npcs: npcs, zones: zones);

        private static PlayerEntity AddPlayer(Zone zone, int connectionId, byte classId, Vec2 position)
        {
            PlayerEntity player = zone.SpawnPlayer(connectionId, position, new Vec2(0.4f, 0.4f), 8, classId: classId);
            return player;
        }

        private static NpcEntity AddNpc(Zone zone, int id, Vec2 position, int health = 1000,
            int defense = 0, int magicResist = 0)
        {
            var npc = new NpcEntity(id, 2001, 0f, position, 0f, long.MaxValue, health, 200, -1,
                defense: defense, magicResist: magicResist)
            {
                Position = position,
                HalfSize = new Vec2(0.35f, 0.35f),
            };
            zone.AddEntity(npc);
            return npc;
        }

        private static NpcCatalog NpcData(float x, float y, float speed, float leashRadius, int count = 1) => new NpcCatalog
        {
            Types = new List<NpcType>
            {
                new NpcType
                {
                    Id = 2001, Name = "Tipo de prueba", Speed = speed,
                    HalfWidth = 0.3f, HalfHeight = 0.3f, MaxHealth = 1000,
                    Experience = 20, Level = 1, RespawnSeconds = 20,
                    AggroRadius = 0, LeashRadius = leashRadius, AttackDamage = 20,
                    AttackRange = 1, AttackCooldownSeconds = 1, Ranks = false,
                    Loot = new List<LootEntry> { new LootEntry { ItemId = 3001, Chance = 1, Min = 1, Max = 1 } },
                },
            },
            Spawners = new List<NpcSpawner>
            {
                new NpcSpawner { TypeId = 2001, X = x, Y = y, Count = count, WanderRadius = 0 },
            },
        };

        private static NpcEntity Npc(Zone zone, int index = 0)
        {
            int found = 0;
            for (int i = 0; i < zone.Entities.Count; i++)
                if (zone.Entities[i] is NpcEntity npc && found++ == index) return npc;
            throw new InvalidOperationException("La zona no tiene ese PNJ.");
        }

        private static void Use(Zone zone, PlayerEntity player, int targetId, ushort skillId) =>
            Assert.True(zone.Attacks!.TryEnqueue(new AttackIntent(player.Id, targetId, skillId)));

        private static ZoneMap SafeZones(MapCollisionGrid map) => ZoneMap.FromDefinitions(new[]
        {
            new ZoneDefinition
            {
                Id = 1, Name = "Segura", Kind = "safe",
                Rects = new List<ZoneRectDefinition> { new ZoneRectDefinition { X = 10, Y = 4, W = 4, H = 4 } },
            },
        }, map);

        private static int ExpectedDamage(ClassCatalog classes, PlayerEntity player, int baseDamage, int defense)
        {
            DerivedCharacterStats stats = CharacterStats.Calculate(classes, player.ClassId, player.Level,
                player.AllocStr, player.AllocDex, player.AllocInt, player.AllocVit);
            CharacterClass characterClass = classes.Find(player.ClassId)!;
            int primary = characterClass.Primary == "str" ? stats.Str : characterClass.Primary == "dex" ? stats.Dex : stats.Int;
            int bruto = baseDamage * (100 + primary * classes.PowerPerPoint) / 100;
            return CharacterStats.MitigateDamage(bruto, defense);
        }

        [Fact]
        public void Catalogo_Del_Repo_Incluye_Los_Cuatro_Estados()
        {
            SkillCatalog catalog = SkillCatalog.Load(Path.Combine(AppContext.BaseDirectory, "skills.json"), ClassCatalog.Default);
            Assert.Equal(10, catalog.Skills!.Count);
            Assert.Equal(80, catalog.Find(4007)!.StatusTicks);
            Assert.Equal(60, catalog.Find(4008)!.StatusTicks);
            Assert.Equal(StatusKind.Slow, catalog.Find(4008)!.StatusKind);
            Assert.True(catalog.Find(4008)!.IsDamage);
            Assert.Equal(40, catalog.Find(4008)!.StatusAmount);
            Assert.Equal(200, catalog.Find(4009)!.StatusTicks);
            Assert.Equal(300, catalog.Find(4010)!.StatusTicks);
            Assert.True(catalog.Find(4009)!.IsBeneficial);
            Assert.True(catalog.Find(4010)!.IsBeneficial);
        }

        [Theory]
        [InlineData("status")]
        [InlineData("none-sin-status")]
        [InlineData("none-con-daño")]
        [InlineData("cura-con-slow")]
        [InlineData("escudo-daño")]
        [InlineData("taunt-daño")]
        [InlineData("slow-9")]
        [InlineData("slow-81")]
        [InlineData("duración-04")]
        [InlineData("duración-31")]
        [InlineData("duración-sin-estado")]
        [InlineData("área")]
        [InlineData("bonus-0")]
        [InlineData("taunt-5")]
        public void Catalogo_Rechaza_Definiciones_De_Estado_Invalidas(string caso)
        {
            SkillDefinition skill = Skill(4999, "Prueba", 1, 1, 1, 5, 10, "physical");
            switch (caso)
            {
                case "status": skill.Status = "stun"; skill.StatusSeconds = 2; skill.StatusAmount = 20; break;
                case "none-sin-status": skill.Effect = "none"; skill.Damage = 0; break;
                case "none-con-daño": skill.Effect = "none"; skill.Damage = 5; skill.Status = "taunt"; skill.StatusSeconds = 2; break;
                case "cura-con-slow": skill.Effect = "heal"; skill.Status = "slow"; skill.StatusSeconds = 2; skill.StatusAmount = 40; break;
                case "escudo-daño": skill.Status = "shield"; skill.StatusSeconds = 2; skill.StatusAmount = 10; break;
                case "taunt-daño": skill.Status = "taunt"; skill.StatusSeconds = 2; break;
                case "slow-9": skill.Status = "slow"; skill.StatusSeconds = 2; skill.StatusAmount = 9; break;
                case "slow-81": skill.Status = "slow"; skill.StatusSeconds = 2; skill.StatusAmount = 81; break;
                case "duración-04": skill.Status = "slow"; skill.StatusSeconds = 0.4; skill.StatusAmount = 40; break;
                case "duración-31": skill.Status = "slow"; skill.StatusSeconds = 31; skill.StatusAmount = 40; break;
                case "duración-sin-estado": skill.StatusSeconds = 3; break;
                case "área": skill.Status = "slow"; skill.StatusSeconds = 2; skill.StatusAmount = 40; skill.Radius = 2; skill.MaxTargets = 3; break;
                case "bonus-0": skill.Effect = "none"; skill.Damage = 0; skill.Status = "bonus"; skill.StatusSeconds = 2; break;
                case "taunt-5": skill.Effect = "none"; skill.Damage = 0; skill.Status = "taunt"; skill.StatusSeconds = 2; skill.StatusAmount = 5; break;
            }
            Assert.Throws<InvalidOperationException>(() => Catalog(ClassCatalog.Default, skill));
        }

        [Fact]
        public void StatusEffects_Caduca_En_El_Tick_Inclusivo_Y_Recalcula_Mascara_Y_Velocidad()
        {
            var statuses = new StatusEffects();
            Assert.True(statuses.Apply(StatusKind.Slow, 4008, 3, 40, 160));
            Assert.True(statuses.IsActive(StatusKind.Slow));
            Assert.Equal(60, statuses.SpeedPercent);
            Assert.Equal((byte)4, statuses.Mask);
            Assert.Equal(0, statuses.Expire(159));
            int version = statuses.Version;
            Assert.Equal(1, statuses.Expire(160));
            Assert.False(statuses.IsActive(StatusKind.Slow));
            Assert.Equal(100, statuses.SpeedPercent);
            Assert.Equal((byte)0, statuses.Mask);
            Assert.True(statuses.Version > version);
        }

        [Fact]
        public void StatusEffects_Mantiene_El_Mas_Fuerte_Y_Admite_Uno_Por_Tipo()
        {
            var statuses = new StatusEffects();
            Assert.True(statuses.Apply(StatusKind.Slow, 4008, 1, 60, 100));
            Assert.False(statuses.Apply(StatusKind.Slow, 4008, 2, 40, 200));
            Assert.Equal(100, statuses.Get(StatusKind.Slow).ExpiresAtTick);
            Assert.Equal(1, statuses.Get(StatusKind.Slow).SourceId);
            Assert.True(statuses.Apply(StatusKind.Slow, 4008, 2, 60, 200));
            Assert.Equal(200, statuses.Get(StatusKind.Slow).ExpiresAtTick);
            Assert.Equal(2, statuses.Get(StatusKind.Slow).SourceId);
            Assert.True(statuses.Apply(StatusKind.Bonus, 4010, 1, 4, 300));
            Assert.True(statuses.Apply(StatusKind.Shield, 4009, 1, 10, 300));
            Assert.True(statuses.Apply(StatusKind.Taunt, 4007, 1, 0, 300));
            Assert.Equal((byte)15, statuses.Mask);
            Assert.Throws<ArgumentOutOfRangeException>(() => statuses.Apply(StatusKind.None, 1, 1, 1, 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => statuses.Apply(StatusKind.Slow, 1, 1, 1, 0));
        }

        [Fact]
        public void StatusEffects_Absorbe_Daño_Parcial_Y_Agota_El_Escudo()
        {
            var statuses = new StatusEffects();
            Assert.True(statuses.Apply(StatusKind.Shield, 4009, 2, 10, 100));
            Assert.Equal(4, statuses.Absorb(4));
            Assert.Equal(6, statuses.Get(StatusKind.Shield).Amount);
            Assert.Equal(6, statuses.Absorb(10));
            Assert.False(statuses.IsActive(StatusKind.Shield));
            Assert.Equal(0, statuses.Absorb(5));
            Assert.Equal(0, statuses.Absorb(-1));
        }

        [Fact]
        public void Escudo_En_Pvp_Absorbe_Tras_Mitigar_Y_Emite_Eventos_Separados()
        {
            Zone zone = NewSkillZone();
            PlayerEntity caster = AddPlayer(zone, 1, 2, new Vec2(8, 10));
            PlayerEntity victim = AddPlayer(zone, 2, 1, new Vec2(10, 10));
            caster.PvpMode = victim.PvpMode = PvpMode.Pvp;
            DerivedCharacterStats victimStats = CharacterStats.Calculate(zone.Classes, victim.ClassId, victim.Level,
                victim.AllocStr, victim.AllocDex, victim.AllocInt, victim.AllocVit);
            int mitigated = ExpectedDamage(zone.Classes, caster, 10, victimStats.MagicResist);
            Assert.True(mitigated > 5);
            Assert.True(victim.Statuses.Apply(StatusKind.Shield, 4009, victim.Id, 5, 1000));
            Use(zone, caster, victim.Id, 4008);
            zone.Tick();
            Assert.Equal(victim.MaxHealth - (mitigated - 5), victim.Health);
            Assert.False(victim.Statuses.IsActive(StatusKind.Shield));
            Assert.Equal(2, zone.CombatEvents.Count);
            Assert.Equal(CombatEventFlags.Absorbed, zone.CombatEvents[0].Flags);
            Assert.Equal((ushort)5, zone.CombatEvents[0].Damage);
            Assert.Equal((ushort)(mitigated - 5), zone.CombatEvents[1].Damage);

            Zone second = NewSkillZone();
            PlayerEntity secondCaster = AddPlayer(second, 3, 2, new Vec2(8, 10));
            PlayerEntity secondVictim = AddPlayer(second, 4, 1, new Vec2(10, 10));
            secondCaster.PvpMode = secondVictim.PvpMode = PvpMode.Pvp;
            DerivedCharacterStats secondStats = CharacterStats.Calculate(second.Classes, secondVictim.ClassId,
                secondVictim.Level, secondVictim.AllocStr, secondVictim.AllocDex, secondVictim.AllocInt, secondVictim.AllocVit);
            int secondDamage = ExpectedDamage(second.Classes, secondCaster, 10, secondStats.MagicResist);
            Assert.True(secondVictim.Statuses.Apply(StatusKind.Shield, 4009, secondVictim.Id, secondDamage + 7, 1000));
            int health = secondVictim.Health;
            Use(second, secondCaster, secondVictim.Id, 4008);
            second.Tick();
            Assert.Equal(health, secondVictim.Health);
            Assert.Equal(7, secondVictim.Statuses.Get(StatusKind.Shield).Amount);
            Assert.Equal(1, second.CombatEvents.Count);
            Assert.Equal(CombatEventFlags.Absorbed, second.CombatEvents[0].Flags);
            Assert.Equal((ushort)secondDamage, second.CombatEvents[0].Damage);
        }

        [Fact]
        public void Ataque_De_Pnj_Se_Absorbe_Con_Escudo_Y_Cuenta_Daño_Mitigado()
        {
            Zone zone = NewSkillZone(npcs: NpcData(10, 10, 0, 20));
            NpcEntity npc = Npc(zone);
            PlayerEntity player = AddPlayer(zone, 1, 1, new Vec2(10.5f, 10));
            npc.AiState = NpcAiState.Attack;
            npc.TargetEntityId = player.Id;
            npc.NextAttackTick = 0;
            DerivedCharacterStats stats = CharacterStats.Calculate(zone.Classes, player.ClassId, player.Level,
                player.AllocStr, player.AllocDex, player.AllocInt, player.AllocVit);
            int mitigated = CharacterStats.MitigateDamage(npc.AttackDamage, stats.Defense);
            int health = player.Health;
            Assert.True(player.Statuses.Apply(StatusKind.Shield, 4009, player.Id, 1000, 1000));
            zone.Tick();
            Assert.Equal(health, player.Health);
            Assert.Equal(mitigated, zone.ShieldAbsorbed);
            Assert.Equal(1, zone.CombatEvents.Count);
            Assert.Equal(CombatEventFlags.Absorbed, zone.CombatEvents[0].Flags);
        }

        [Fact]
        public void Bonus_Aumenta_Daño_Y_Defensa_Y_Publica_Cambios_Propios()
        {
            Zone zone = NewSkillZone();
            PlayerEntity healer = AddPlayer(zone, 1, 4, new Vec2(8, 10));
            int statsChanged = 0;
            int statusChanged = 0;
            zone.StatsChanged += _ => statsChanged++;
            zone.StatusEffectsChanged += _ => statusChanged++;
            Use(zone, healer, healer.Id, 4010);
            zone.Tick();
            Assert.Equal(4, healer.Statuses.Bonus);
            Assert.Equal(1, statsChanged);
            Assert.Equal(1, statusChanged);

            NpcEntity npc = AddNpc(zone, 1000, new Vec2(10, 10));
            Use(zone, healer, npc.Id, 4004);
            zone.Tick();
            Assert.Equal(1000 - ExpectedDamage(zone.Classes, healer, 18, 0), npc.Health);

            Zone pvpZone = NewSkillZone();
            PlayerEntity attacker = AddPlayer(pvpZone, 2, 2, new Vec2(8, 10));
            PlayerEntity blessed = AddPlayer(pvpZone, 3, 4, new Vec2(10, 10));
            attacker.PvpMode = blessed.PvpMode = PvpMode.Pvp;
            DerivedCharacterStats blessedStats = CharacterStats.Calculate(pvpZone.Classes, blessed.ClassId,
                blessed.Level, blessed.AllocStr, blessed.AllocDex, blessed.AllocInt, blessed.AllocVit);
            Assert.True(blessed.Statuses.Apply(StatusKind.Bonus, 4010, blessed.Id, 4, 1000));
            Use(pvpZone, attacker, blessed.Id, 4002);
            pvpZone.Tick();
            Assert.Equal(blessed.MaxHealth - ExpectedDamage(pvpZone.Classes, attacker, 18,
                blessedStats.MagicResist + 4), blessed.Health);
        }

        [Fact]
        public void Objetivos_Beneficiosos_Y_Dañinos_Respetan_Pvp_Alcance_Zona_Segura_Y_Taunt()
        {
            Zone zone = NewSkillZone();
            PlayerEntity healer = AddPlayer(zone, 1, 4, new Vec2(8, 10));
            PlayerEntity pvpTarget = AddPlayer(zone, 2, 4, new Vec2(9, 10));
            pvpTarget.PvpMode = PvpMode.Pvp;
            int mana = healer.Mana;
            Use(zone, healer, pvpTarget.Id, 4010);
            zone.Tick();
            Assert.Equal(1, zone.SkillsRejected(SkillRejectReason.PvpNotAllowed));
            Assert.Equal(mana, healer.Mana);

            Zone npcTargetZone = NewSkillZone();
            PlayerEntity healer2 = AddPlayer(npcTargetZone, 3, 4, new Vec2(8, 10));
            NpcEntity npcTarget = AddNpc(npcTargetZone, 1000, new Vec2(9, 10));
            Use(npcTargetZone, healer2, npcTarget.Id, 4010);
            npcTargetZone.Tick();
            Assert.Equal(1, npcTargetZone.SkillsRejected(SkillRejectReason.InvalidTarget));

            MapCollisionGrid map = MapCollisionGrid.AllWalkable(30, 20);
            Zone safeZone = NewSkillZone(map, SafeZones(map));
            PlayerEntity safeCaster = AddPlayer(safeZone, 4, 4, new Vec2(11, 5));
            PlayerEntity safeTarget = AddPlayer(safeZone, 5, 4, new Vec2(12, 5));
            Use(safeZone, safeCaster, safeTarget.Id, 4010);
            safeZone.Tick();
            Assert.True(safeTarget.Statuses.IsActive(StatusKind.Bonus));

            Zone rangeZone = NewSkillZone();
            PlayerEntity farCaster = AddPlayer(rangeZone, 6, 4, new Vec2(8, 10));
            PlayerEntity farTarget = AddPlayer(rangeZone, 7, 4, new Vec2(15.1f, 10));
            int farMana = farCaster.Mana;
            Use(rangeZone, farCaster, farTarget.Id, 4010);
            rangeZone.Tick();
            Assert.Equal(1, rangeZone.SkillsRejected(SkillRejectReason.OutOfRange));
            Assert.Equal(farMana, farCaster.Mana);

            Zone harmfulPvp = NewSkillZone();
            PlayerEntity pvpCaster = AddPlayer(harmfulPvp, 8, 2, new Vec2(8, 10));
            PlayerEntity pveVictim = AddPlayer(harmfulPvp, 9, 1, new Vec2(9, 10));
            pvpCaster.PvpMode = PvpMode.Pvp;
            Use(harmfulPvp, pvpCaster, pveVictim.Id, 4008);
            harmfulPvp.Tick();
            Assert.Equal(1, harmfulPvp.SkillsRejected(SkillRejectReason.PvpNotAllowed));
            Assert.False(pveVictim.Statuses.IsActive(StatusKind.Slow));

            Zone harmfulSafe = NewSkillZone(map, SafeZones(map));
            PlayerEntity safeHarmCaster = AddPlayer(harmfulSafe, 10, 2, new Vec2(8, 5));
            NpcEntity safeNpc = AddNpc(harmfulSafe, 1001, new Vec2(11, 5));
            Use(harmfulSafe, safeHarmCaster, safeNpc.Id, 4008);
            harmfulSafe.Tick();
            Assert.Equal(1, harmfulSafe.SkillsRejected(SkillRejectReason.SafeZone));

            Zone tauntPlayerZone = NewSkillZone();
            PlayerEntity taunter = AddPlayer(tauntPlayerZone, 11, 1, new Vec2(8, 10));
            PlayerEntity playerTarget = AddPlayer(tauntPlayerZone, 12, 2, new Vec2(9, 10));
            taunter.PvpMode = playerTarget.PvpMode = PvpMode.Pvp;
            int tauntMana = taunter.Mana;
            Use(tauntPlayerZone, taunter, playerTarget.Id, 4007);
            tauntPlayerZone.Tick();
            Assert.Equal(1, tauntPlayerZone.SkillsRejected(SkillRejectReason.InvalidTarget));
            Assert.Equal(tauntMana, taunter.Mana);
        }

        [Fact]
        public void Slow_Se_Acumula_Por_El_Mas_Fuerte_Y_La_Habilidad_Ignorada_Se_Cobra()
        {
            Zone zone = NewSkillZone();
            PlayerEntity firstCaster = AddPlayer(zone, 1, 2, new Vec2(8, 10));
            PlayerEntity secondCaster = AddPlayer(zone, 2, 2, new Vec2(10, 8));
            NpcEntity npc = AddNpc(zone, 1000, new Vec2(10, 10));
            Use(zone, firstCaster, npc.Id, 4008);
            zone.Tick();
            for (int i = 0; i < 4; i++) zone.Tick();
            Use(zone, secondCaster, npc.Id, 4008);
            zone.Tick();
            Assert.Equal(2, zone.StatusesApplied(StatusKind.Slow));
            Assert.Equal(secondCaster.Id, npc.Statuses.Get(StatusKind.Slow).SourceId);
            Assert.Equal(65, npc.Statuses.Get(StatusKind.Slow).ExpiresAtTick);

            Zone weakZone = NewSkillZone();
            PlayerEntity caster = AddPlayer(weakZone, 3, 2, new Vec2(8, 10));
            NpcEntity weakNpc = AddNpc(weakZone, 1001, new Vec2(10, 10));
            Assert.True(weakNpc.Statuses.Apply(StatusKind.Slow, 4008, 99, 60, 1000));
            int mana = caster.Mana;
            Use(weakZone, caster, weakNpc.Id, 4008);
            weakZone.Tick();
            Assert.Equal(1, weakZone.StatusesIgnoredWeaker);
            Assert.Equal(60, weakNpc.Statuses.Get(StatusKind.Slow).Amount);
            Assert.Equal(99, weakNpc.Statuses.Get(StatusKind.Slow).SourceId);
            Assert.Equal(mana - 25, caster.Mana);
        }

        [Fact]
        public void Slow_En_Zona_Caduca_En_T_mas_60()
        {
            Zone zone = NewSkillZone();
            PlayerEntity caster = AddPlayer(zone, 1, 2, new Vec2(8, 10));
            NpcEntity npc = AddNpc(zone, 1000, new Vec2(10, 10));
            Use(zone, caster, npc.Id, 4008);
            zone.Tick();
            Assert.True(npc.Statuses.IsActive(StatusKind.Slow));
            Assert.Equal(60, npc.Statuses.Get(StatusKind.Slow).ExpiresAtTick);
            for (int i = 0; i < 59; i++) zone.Tick();
            Assert.True(npc.Statuses.IsActive(StatusKind.Slow));
            Assert.Equal(0, zone.StatusesExpired);
            zone.Tick();
            Assert.False(npc.Statuses.IsActive(StatusKind.Slow));
            Assert.Equal(1, zone.StatusesExpired);
        }

        [Fact]
        public void La_Muerte_Limpia_Bonus_Escudo_Y_Slow_Al_Principio_Del_Tick()
        {
            Zone zone = NewSkillZone();
            PlayerEntity player = AddPlayer(zone, 1, 4, new Vec2(8, 10));
            Assert.True(player.Statuses.Apply(StatusKind.Bonus, 4010, player.Id, 4, 1000));
            Assert.True(player.Statuses.Apply(StatusKind.Shield, 4009, player.Id, 10, 1000));
            Assert.True(player.Statuses.Apply(StatusKind.Slow, 4008, player.Id, 40, 1000));
            player.Health = 0;
            player.Action = EntityAction.Dead;
            zone.Tick();
            Assert.Equal((byte)0, player.Statuses.Mask);
            Assert.Equal(100, player.Statuses.SpeedPercent);
            Assert.Equal(0, player.Statuses.Bonus);
            Assert.Equal(3, zone.StatusesClearedByDeath);
        }

        [Fact]
        public void Slow_Reduce_La_Velocidad_De_Los_Pnj_En_El_Servidor()
        {
            Zone zone = NewSkillZone(npcs: NpcData(10, 10, 2, 30, count: 2));
            PlayerEntity target = AddPlayer(zone, 1, 1, new Vec2(25, 10));
            NpcEntity normal = Npc(zone, 0);
            NpcEntity slow = Npc(zone, 1);
            normal.Position = new Vec2(10, 10);
            slow.Position = new Vec2(10, 12);
            normal.AiState = slow.AiState = NpcAiState.Chase;
            normal.TargetEntityId = slow.TargetEntityId = target.Id;
            Assert.True(slow.Statuses.Apply(StatusKind.Slow, 4008, target.Id, 40, 1000));
            Vec2 expected = MovementSimulator.Step(slow.Position, 1, -1, 2f * 60 / 100f,
                SimulationConstants.FixedDt, slow.HalfSize, zone.Map);
            zone.Tick();
            Assert.Equal(expected, slow.Position);
            Assert.NotEqual(new Vec2(10, 10), normal.Position);
        }

        [Fact]
        public void Slow_Reduce_La_Velocidad_Del_Jugador_En_El_Servidor()
        {
            Zone zone = NewSkillZone();
            PlayerEntity player = AddPlayer(zone, 1, 1, new Vec2(8, 10));
            Assert.True(player.Statuses.Apply(StatusKind.Slow, 4008, 2, 40, 1000));
            Vec2 expected = MovementSimulator.Step(player.Position, 1, 0, 4f * 60 / 100f,
                SimulationConstants.FixedDt, player.HalfSize, zone.Map);
            Assert.Equal(InputQueueResult.Accepted, zone.Inputs.TryEnqueue(new MoveIntent(player.Id, 1, 1, 0)));
            zone.Tick();
            Assert.Equal(expected, player.Position);
        }

        [Fact]
        public void Provocar_Fija_El_Objetivo_Y_Al_Caducar_No_Restaura_El_Anterior()
        {
            Zone zone = NewSkillZone(npcs: NpcData(10, 10, 2, 20));
            PlayerEntity taunter = AddPlayer(zone, 1, 1, new Vec2(8, 10));
            taunter.Health = 100000;
            PlayerEntity previousTarget = AddPlayer(zone, 2, 1, new Vec2(12, 10));
            NpcEntity npc = Npc(zone);
            npc.AiState = NpcAiState.Chase;
            npc.TargetEntityId = previousTarget.Id;
            Use(zone, taunter, npc.Id, 4007);
            zone.Tick();
            Assert.Equal(NpcAiState.Chase, npc.AiState);
            Assert.Equal(taunter.Id, npc.TargetEntityId);
            Assert.True(npc.Statuses.IsActive(StatusKind.Taunt));
            Assert.Equal(taunter.Id, npc.Statuses.Get(StatusKind.Taunt).SourceId);
            for (int i = 0; i < 80; i++) zone.Tick();
            Assert.False(npc.Statuses.IsActive(StatusKind.Taunt));
            Assert.Equal(taunter.Id, npc.TargetEntityId);
        }

        [Fact]
        public void Provocar_Se_Rompe_Al_Entrar_En_Return_Y_Se_Rechaza_Si_Ya_Esta_En_Return()
        {
            Zone leashZone = NewSkillZone(npcs: NpcData(10, 10, 0, 5));
            PlayerEntity taunter = AddPlayer(leashZone, 1, 1, new Vec2(8, 10));
            NpcEntity npc = Npc(leashZone);
            Use(leashZone, taunter, npc.Id, 4007);
            leashZone.Tick();
            npc.Position = new Vec2(16, 10);
            leashZone.Tick();
            Assert.Equal(NpcAiState.Return, npc.AiState);
            leashZone.Tick();
            Assert.False(npc.Statuses.IsActive(StatusKind.Taunt));
            Assert.Equal(1, leashZone.TauntsBroken);

            MapCollisionGrid map = MapCollisionGrid.AllWalkable(30, 20);
            Zone safeZone = NewSkillZone(map, SafeZones(map), NpcData(9, 5, 0, 20));
            PlayerEntity safeTaunter = AddPlayer(safeZone, 2, 1, new Vec2(8, 5));
            NpcEntity safeNpc = Npc(safeZone);
            Use(safeZone, safeTaunter, safeNpc.Id, 4007);
            safeZone.Tick();
            safeTaunter.Position = new Vec2(11, 5);
            safeZone.Tick();
            Assert.Equal(NpcAiState.Return, safeNpc.AiState);
            safeZone.Tick();
            Assert.False(safeNpc.Statuses.IsActive(StatusKind.Taunt));
            Assert.Equal(1, safeZone.TauntsBroken);

            Zone alreadyReturning = NewSkillZone();
            PlayerEntity caster = AddPlayer(alreadyReturning, 3, 1, new Vec2(8, 10));
            NpcEntity returning = AddNpc(alreadyReturning, 1000, new Vec2(10, 10));
            returning.AiState = NpcAiState.Return;
            int mana = caster.Mana;
            Use(alreadyReturning, caster, returning.Id, 4007);
            alreadyReturning.Tick();
            Assert.Equal(1, alreadyReturning.SkillsRejected(SkillRejectReason.InvalidTarget));
            Assert.Equal(mana, caster.Mana);
        }
    }
}
