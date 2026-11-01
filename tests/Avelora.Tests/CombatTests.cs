using System;
using System.Collections.Generic;
using Avelora.Server.Entities;
using Avelora.Server.Items;
using Avelora.Server.Npcs;
using Avelora.Server.Progression;
using Avelora.Server.Simulation;
using Avelora.Shared;
using Avelora.Shared.Maps;
using Xunit;

namespace Avelora.Tests
{
    /// <summary>
    /// Pruebas de I-02 en el servidor: validación de ataques, muerte, reaparición y determinismo.
    /// </summary>
    public class CombatTests
    {
        private const float Range = 1.2f;
        private const float Tolerance = 0.5f;
        private const double CooldownSeconds = 0.8d;

        private static Zone NewZone(
            NpcCatalog? npcs = null,
            int seed = 12345,
            float range = Range,
            float tolerance = Tolerance,
            double cooldownSeconds = CooldownSeconds,
            ItemCatalog? items = null,
            LevelTable? levels = null,
            ClassCatalog? classes = null)
        {
            classes ??= ClassCatalog.Default;
            classes.Find(1)!.Unarmed!.Range = range;
            classes.Find(1)!.Unarmed!.CooldownSeconds = cooldownSeconds;
            return new Zone(
                1,
                MapCollisionGrid.AllWalkable(40, 40),
                new EntityStore(),
                new EntityIdAllocator(),
                new InputQueue(256),
                new MovementSystem(4f, SimulationConstants.FixedDt),
                128,
                4,
                new InterestSettings(40f, 8f, 0f),
                npcs,
                seed,
                new AttackQueue(256),
                new CombatSettings(tolerance),
                items: items,
                levels: levels,
                classes: classes);
        }

        private static ItemCatalog WeaponCatalog() => new ItemCatalog
        {
            Items = new List<ItemType>
            {
                new ItemType { Id = 3101, Name = "Espada corta", Kind = "weapon", WeaponType = "Espada", MaxStack = 1, Damage = 14, Range = 1.2f, CooldownSeconds = 0.8d },
                new ItemType { Id = 3102, Name = "Hacha de orco", Kind = "weapon", WeaponType = "Hacha", MaxStack = 1, Damage = 22, Range = 1.3f, CooldownSeconds = 1.2d },
                new ItemType { Id = 3104, Name = "Bastón de roble", Kind = "weapon", DamageType = "magical", Damage = 10, Range = 5f, CooldownSeconds = 1.0d, WeaponType = "Bastón", MaxStack = 1 },
            },
        };

        private static NpcEntity AddNpc(
            Zone zone,
            int id,
            Vec2 position,
            int maxHealth = 30,
            long respawnTicks = 200,
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
                maxHealth,
                respawnTicks,
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

        private static NpcCatalog Catalog(int count = 1, float speed = 0f, int maxHealth = 30, float respawnSeconds = 10f)
        {
            return new NpcCatalog
            {
                Types = new List<NpcType>
                {
                    new NpcType
                    {
                        Id = 2001,
                        Name = "Orco",
                        Speed = speed,
                        HalfWidth = 0.35f,
                        HalfHeight = 0.35f,
                        MaxHealth = maxHealth,
                        Experience = 20,
                        Level = 1,
                        RespawnSeconds = respawnSeconds,
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
                        Count = count,
                        WanderRadius = 1f,
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

        private static void Attack(Zone zone, PlayerEntity player, NpcEntity npc) =>
            zone.Attacks!.TryEnqueue(new AttackIntent(player.Id, npc.Id));

        private static void Attack(Zone zone, int attackerId, int targetId) =>
            zone.Attacks!.TryEnqueue(new AttackIntent(attackerId, targetId));

        [Fact]
        public void Un_Ataque_De_Una_Entidad_Que_No_Es_Jugador_No_Hace_Nada()
        {
            Zone zone = NewZone();
            NpcEntity npc = AddNpc(zone, 100, new Vec2(10f, 10f));

            Attack(zone, npc.Id, npc.Id);
            zone.Tick();

            Assert.Equal(30, npc.Health);
            Assert.Equal(0, zone.AttacksOk);
            Assert.Equal(0, zone.RejectedAttackTarget);
            Assert.Equal(0, zone.RejectedAttackDistance);
            Assert.Equal(0, zone.RejectedAttackCooldown);
        }

        [Fact]
        public void Un_Objetivo_Inexistente_O_Que_No_Es_Pnj_Se_Rechaza()
        {
            Zone zone = NewZone();
            PlayerEntity attacker = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            PlayerEntity other = zone.SpawnPlayer(2, new Vec2(10.5f, 10f), new Vec2(0.4f, 0.4f), 8);

            Attack(zone, attacker.Id, 9999);
            zone.Tick();
            Assert.Equal(1, zone.RejectedAttackTarget);

            Attack(zone, attacker.Id, other.Id);
            zone.Tick();
            Assert.Equal(1, zone.RejectedAttackTarget);
            Assert.Equal(1, zone.RejectedPvpAttackerPve);
            Assert.Equal(0, zone.AttacksOk);
        }

        [Fact]
        public void Un_Objetivo_Muerto_Se_Rechaza()
        {
            Zone zone = NewZone();
            PlayerEntity attacker = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity npc = AddNpc(zone, 100, new Vec2(10.5f, 10f));
            npc.Health = 0;
            npc.Action = EntityAction.Dead;

            Attack(zone, attacker, npc);
            zone.Tick();

            Assert.Equal(1, zone.RejectedAttackTarget);
            Assert.Equal(0, zone.AttacksOk);
            Assert.Equal(EntityAction.Dead, npc.Action);
        }

        [Fact]
        public void El_Borde_Exacto_Del_Alcance_Se_Acepta_Y_Un_Poco_Mas_Se_Rechaza()
        {
            // 1,25 + 0,5 = 1,75 exactamente: en la rejilla de 1/64 no hay error de coma flotante.
            Zone inside = NewZone(range: 1.25f);
            PlayerEntity attacker = inside.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity target = AddNpc(inside, 100, new Vec2(10f + (112f / 64f), 10f));

            Attack(inside, attacker, target);
            inside.Tick();

            Assert.Equal(1, inside.AttacksOk);
            Assert.Equal(0, inside.RejectedAttackDistance);
            Assert.Equal(18, target.Health);

            Zone outside = NewZone(range: 1.25f);
            PlayerEntity farAttacker = outside.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity farTarget = AddNpc(outside, 100, new Vec2(10f + (113f / 64f), 10f));

            Attack(outside, farAttacker, farTarget);
            outside.Tick();

            Assert.Equal(0, outside.AttacksOk);
            Assert.Equal(1, outside.RejectedAttackDistance);
            Assert.Equal(30, farTarget.Health);
        }

        [Fact]
        public void La_Recarga_Se_Mide_En_Ticks_De_La_Zona()
        {
            Zone zone = NewZone(range: 5f, tolerance: 0f);
            PlayerEntity attacker = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity npc = AddNpc(zone, 100, new Vec2(11f, 10f), maxHealth: 1000);

            // Tick 0: primer ataque válido. La recarga es ceil(0,8 × 20) = 16 ticks.
            Attack(zone, attacker, npc);
            zone.Tick();
            Assert.Equal(1, zone.AttacksOk);
            Assert.Equal(EntityAction.Attacking, attacker.Action);

            // Ticks 1..15: todavía no. En el tick 16 sí.
            for (int tick = 1; tick <= 15; tick++)
            {
                Attack(zone, attacker, npc);
                zone.Tick();
            }

            Assert.Equal(15, zone.RejectedAttackCooldown);

            Attack(zone, attacker, npc);
            zone.Tick();

            Assert.Equal(2, zone.AttacksOk);
            Assert.Equal(976, npc.Health);
        }

        [Fact]
        public void Espada_Hace_14_Da_Alcance_1_2_Mas_Tolerancia_Y_Recarga_16_Ticks()
        {
            Zone zone = NewZone(range: 1f, tolerance: 0.8f, items: WeaponCatalog());
            PlayerEntity attacker = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            attacker.EquippedWeaponId = 3101;
            NpcEntity npc = AddNpc(zone, 100, new Vec2(11f, 10f), maxHealth: 1000);

            Attack(zone, attacker, npc);
            zone.Tick();
            Assert.Equal(984, npc.Health);
            Assert.Equal(1, zone.AttacksWithWeapon);

            for (int tick = 1; tick <= 15; tick++)
            {
                Attack(zone, attacker, npc);
                zone.Tick();
            }

            Assert.Equal(15, zone.RejectedAttackCooldown);
            Attack(zone, attacker, npc);
            zone.Tick();
            Assert.Equal(968, npc.Health);
            Assert.Equal(2, zone.AttacksWithWeapon);
        }

        [Fact]
        public void Hacha_Hace_22_Y_Recarga_24_Ticks()
        {
            Zone zone = NewZone(range: 1f, tolerance: 0.7f, items: WeaponCatalog());
            PlayerEntity attacker = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            attacker.EquippedWeaponId = 3102;
            NpcEntity npc = AddNpc(zone, 100, new Vec2(11f, 10f), maxHealth: 1000);

            Attack(zone, attacker, npc);
            zone.Tick();
            Assert.Equal(976, npc.Health);

            for (int tick = 1; tick <= 23; tick++)
            {
                Attack(zone, attacker, npc);
                zone.Tick();
            }

            Assert.Equal(23, zone.RejectedAttackCooldown);
            Attack(zone, attacker, npc);
            zone.Tick();
            Assert.Equal(952, npc.Health);
            Assert.Equal(2, zone.AttacksWithWeapon);
        }

        [Fact]
        public void Nivel_Tres_Suma_Dos_De_Dano_Tanto_A_La_Espada_Como_A_Los_Punos()
        {
            var levels = new LevelTable
            {
                ExperienceToNext = new[] { 100, 150 },
            };

            Zone armedZone = NewZone(
                range: 1f,
                tolerance: 0.8f,
                items: WeaponCatalog(),
                levels: levels);
            PlayerEntity armed = armedZone.SpawnPlayer(
                1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8, level: 3);
            armed.EquippedWeaponId = 3101;
            NpcEntity swordTarget = AddNpc(armedZone, 100, new Vec2(11f, 10f), maxHealth: 1000);
            Attack(armedZone, armed, swordTarget);
            armedZone.Tick();
            Assert.Equal(984, swordTarget.Health);

            Zone fistsZone = NewZone(levels: levels);
            PlayerEntity fists = fistsZone.SpawnPlayer(
                1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8, level: 3);
            NpcEntity fistTarget = AddNpc(fistsZone, 100, new Vec2(11f, 10f), maxHealth: 1000);
            Attack(fistsZone, fists, fistTarget);
            fistsZone.Tick();
            Assert.Equal(988, fistTarget.Health);
        }

        [Fact]
        public void El_Dueño_De_Botin_Recibe_Experiencia_Aunque_Otro_De_El_Golpe_Mortal()
        {
            Zone zone = NewZone(Catalog(maxHealth: 20));
            PlayerEntity first = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            PlayerEntity killer = zone.SpawnPlayer(2, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity npc = OnlyNpc(zone);

            Attack(zone, first, npc);
            Attack(zone, killer, npc);
            zone.Tick();

            Assert.Equal(20, first.Experience);
            Assert.Equal(0, killer.Experience);
            Assert.Equal(killer.Id, npc.KillerPlayerId);
            Assert.Equal(20, zone.ExperienceGranted);
            Assert.Equal(0, zone.LevelUps);
        }

        [Fact]
        public void La_Experiencia_Del_Rango_Se_Multiplica_Antes_Del_Escalado_Por_Nivel()
        {
            NpcCatalog catalog = Catalog(maxHealth: 20);
            catalog.Ranks = new List<NpcRank>
            {
                new NpcRank
                {
                    Id = 1,
                    Name = "Rango",
                    Chance = 1,
                    Health = 1,
                    Damage = 1,
                    Experience = 3,
                    LootRolls = 1,
                },
            };
            Zone zone = NewZone(catalog);
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity npc = OnlyNpc(zone);
            npc.Health = 1;

            Attack(zone, player, npc);
            zone.Tick();

            Assert.Equal((byte)1, npc.Rank);
            Assert.Equal(60, player.Experience);
            Assert.Equal(60, zone.ExperienceGranted);
        }

        [Fact]
        public void Muertes_De_Jugadores_Distintos_En_Un_Tick_Publican_Un_Cambio_Por_Jugador()
        {
            Zone zone = NewZone(Catalog(count: 2, maxHealth: 10));
            PlayerEntity first = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            PlayerEntity second = zone.SpawnPlayer(2, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity? firstNpc = null;
            NpcEntity? secondNpc = null;

            for (int i = 0; i < zone.Entities.Count; i++)
            {
                if (zone.Entities[i] is NpcEntity npc)
                {
                    if (firstNpc == null)
                    {
                        firstNpc = npc;
                    }
                    else
                    {
                        secondNpc = npc;
                    }
                }
            }

            int firstMessages = 0;
            int secondMessages = 0;
            zone.ProgressChanged += player =>
            {
                if (player.Id == first.Id) firstMessages++;
                if (player.Id == second.Id) secondMessages++;
            };
            Attack(zone, first, firstNpc!);
            Attack(zone, second, secondNpc!);

            zone.Tick();

            Assert.Equal(20, first.Experience);
            Assert.Equal(20, second.Experience);
            Assert.Equal(40, zone.ExperienceGranted);
            Assert.Equal(1, firstMessages);
            Assert.Equal(1, secondMessages);
        }

        [Theory]
        [InlineData(3101, 0.8f)]
        [InlineData(3102, 0.7f)]
        public void El_Borde_Exacto_De_Cada_Arma_Mas_Tolerancia_Se_Acepta(ushort weaponId, float tolerance)
        {
            Zone zone = NewZone(range: 0.5f, tolerance: tolerance, items: WeaponCatalog());
            PlayerEntity attacker = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            attacker.EquippedWeaponId = weaponId;
            NpcEntity npc = AddNpc(zone, 100, new Vec2(12f, 10f), maxHealth: 1000);

            Attack(zone, attacker, npc);
            zone.Tick();

            Assert.Equal(1, zone.AttacksOk);
            Assert.Equal(0, zone.RejectedAttackDistance);
        }

        [Fact]
        public void Jugador_Sin_Arma_Aplica_Daño_Base_Mas_Atributo_Principal()
        {
            Zone zone = NewZone(range: 1.2f, tolerance: 0.5f, cooldownSeconds: 0.8d, items: WeaponCatalog());
            PlayerEntity attacker = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity npc = AddNpc(zone, 100, new Vec2(11f, 10f), maxHealth: 1000);

            Attack(zone, attacker, npc);
            zone.Tick();

            Assert.Equal(988, npc.Health);
            Assert.Equal(0, zone.AttacksWithWeapon);
        }

        [Fact]
        public void Ataques_Fisicos_Y_Magicos_Usan_La_Defensa_Y_Resistencia_Correspondientes()
        {
            Zone physicalZone = NewZone();
            PlayerEntity warrior = physicalZone.SpawnPlayer(
                1, new Vec2(10, 10), new Vec2(0.4f, 0.4f), 8);
            NpcEntity armored = AddNpc(physicalZone, 100, new Vec2(11, 10), defense: 20);
            Attack(physicalZone, warrior, armored);
            physicalZone.Tick();
            Assert.Equal(20, armored.Health); // floor(12 × 100 / 120) = 10

            Zone magicalZone = NewZone();
            PlayerEntity mage = magicalZone.SpawnPlayer(
                1, new Vec2(10, 10), new Vec2(0.4f, 0.4f), 8, classId: 2);
            NpcEntity resistant = AddNpc(
                magicalZone, 100, new Vec2(11, 10), magicResist: 20);
            Attack(magicalZone, mage, resistant);
            magicalZone.Tick();
            Assert.Equal(19, resistant.Health); // floor(14 × 100 / 120) = 11
        }

        [Fact]
        public void Baston_Mitigado_Por_Resistencia_Magica_Y_No_Por_Defensa()
        {
            ItemCatalog items = WeaponCatalog();
            ItemType staff = Assert.IsType<ItemType>(items.Find(3104));
            ClassCatalog classes = ClassCatalog.Default;
            int raw = CharacterStats.GetAttackDamage(
                classes, 2, 3, 0, 0, 0, 0, staff.Damage, out _, items,
                weaponDamageType: staff.DamageType);

            Zone physicalZone = NewZone(items: items, classes: classes);
            PlayerEntity firstMage = physicalZone.SpawnPlayer(
                1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8, level: 3, classId: 2);
            firstMage.EquippedWeaponId = 3104;
            NpcEntity armored = AddNpc(physicalZone, 100, new Vec2(11f, 10f), maxHealth: 1000, defense: 20);
            Attack(physicalZone, firstMage, armored);
            physicalZone.Tick();

            Assert.Equal(1000 - raw, armored.Health);

            Zone magicalZone = NewZone(items: items, classes: classes);
            PlayerEntity secondMage = magicalZone.SpawnPlayer(
                2, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8, level: 3, classId: 2);
            secondMage.EquippedWeaponId = 3104;
            NpcEntity resistant = AddNpc(magicalZone, 101, new Vec2(11f, 10f), maxHealth: 1000, magicResist: 20);
            Attack(magicalZone, secondMage, resistant);
            magicalZone.Tick();

            Assert.Equal(1000 - CharacterStats.MitigateDamage(raw, 20), resistant.Health);
        }

        [Fact]
        public void La_Accion_Attacking_Dura_Seis_Ticks()
        {
            Zone zone = NewZone(range: 5f, tolerance: 0f);
            PlayerEntity attacker = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity npc = AddNpc(zone, 100, new Vec2(11f, 10f), maxHealth: 1000);

            Attack(zone, attacker, npc);
            zone.Tick();

            for (int tick = 1; tick <= 5; tick++)
            {
                zone.Tick();
                Assert.Equal(EntityAction.Attacking, attacker.Action);
            }

            zone.Tick();
            Assert.Equal(EntityAction.None, attacker.Action);
        }

        [Fact]
        public void Un_Orco_De_30_De_Vida_Muere_Al_Tercer_Golpe_Y_Desaparece_A_Los_30_Ticks()
        {
            Zone zone = NewZone(Catalog());
            PlayerEntity attacker = zone.SpawnPlayer(1, new Vec2(11f, 10.5f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity npc = OnlyNpc(zone);
            int originalId = npc.Id;

            Assert.Equal(30, npc.Health);

            Attack(zone, attacker, npc);
            zone.Tick();
            Assert.Equal(18, npc.Health);
            Assert.NotEqual(EntityAction.Dead, npc.Action);

            Advance(zone, 15);
            Attack(zone, attacker, npc);
            zone.Tick();
            Assert.Equal(6, npc.Health);
            Assert.NotEqual(EntityAction.Dead, npc.Action);

            Advance(zone, 15);
            Attack(zone, attacker, npc);
            zone.Tick();

            // Tercer golpe justo: 30 − 10 − 10 − 10 = 0.
            Assert.Equal(0, npc.Health);
            Assert.Equal(EntityAction.Dead, npc.Action);
            Assert.Equal(0, npc.HealthPercent);
            Assert.Equal(1, zone.NpcDeaths);

            // El tick en que se anotó la muerte, ya contado por la zona.
            long tickAfterDeath = zone.TickCount;

            // Muerto: no se mueve y ya no se puede atacar.
            Vec2 corpse = npc.Position;
            Advance(zone, 5);
            Assert.Equal(corpse, npc.Position);

            Attack(zone, attacker, npc);
            zone.Tick();
            Assert.Equal(1, zone.RejectedAttackTarget);

            // El cadáver sigue 30 ticks en la zona desde que la IA anotó la muerte.
            int ticksUntilRemoved = 0;

            while (zone.Entities.CountOf(EntityKind.Npc) > 0 && ticksUntilRemoved < 60)
            {
                zone.Tick();
                ticksUntilRemoved++;
            }

            Assert.Equal(NpcEntity.DeathAnimationTicks, zone.TickCount - tickAfterDeath);
            Assert.Equal(0, zone.Entities.CountOf(EntityKind.Npc));

            // A los 200 ticks de la muerte reaparece: id nuevo, vida llena y en su casa.
            int ticksUntilRespawn = 0;

            while (zone.NpcRespawns == 0 && ticksUntilRespawn < 400)
            {
                zone.Tick();
                ticksUntilRespawn++;
                Assert.True(
                    zone.Entities.CountOf(EntityKind.Npc) <= 1,
                    "El spawner superó su count de 1.");
            }

            Assert.Equal(1, zone.NpcRespawns);
            Assert.Equal(200 - NpcEntity.DeathAnimationTicks, ticksUntilRespawn);

            NpcEntity respawned = OnlyNpc(zone);
            Assert.NotEqual(originalId, respawned.Id);
            Assert.True(respawned.Id > originalId);
            Assert.Equal(30, respawned.Health);
            Assert.Equal(100, respawned.HealthPercent);
            Assert.Equal(EntityAction.None, respawned.Action);
            Assert.Equal(respawned.Home, respawned.Position);
        }

        [Fact]
        public void El_Spawner_Nunca_Supera_Su_Count()
        {
            Zone zone = NewZone(Catalog(count: 2));
            PlayerEntity attacker = zone.SpawnPlayer(1, new Vec2(11f, 10.5f), new Vec2(0.4f, 0.4f), 8);

            for (int tick = 0; tick < 600; tick++)
            {
                if (tick % 16 == 0)
                {
                    NpcEntity? target = FindAliveNpc(zone);

                    if (target != null)
                    {
                        Attack(zone, attacker, target);
                    }
                }

                zone.Tick();
                Assert.True(
                    zone.Entities.CountOf(EntityKind.Npc) <= 2,
                    "En el tick " + tick + " hay más PNJ que el count del spawner.");
            }

            Assert.True(zone.NpcDeaths > 0);
            Assert.True(zone.NpcRespawns > 0);
        }

        [Fact]
        public void La_Misma_Semilla_Y_Los_Mismos_Ataques_Dan_El_Mismo_Estado()
        {
            Zone first = NewZone(Catalog(count: 2, speed: 2f), seed: 777);
            Zone second = NewZone(Catalog(count: 2, speed: 2f), seed: 777);

            PlayerEntity firstPlayer = first.SpawnPlayer(1, new Vec2(11f, 10.5f), new Vec2(0.4f, 0.4f), 8);
            PlayerEntity secondPlayer = second.SpawnPlayer(1, new Vec2(11f, 10.5f), new Vec2(0.4f, 0.4f), 8);

            for (int tick = 0; tick < 200; tick++)
            {
                if (tick % 16 == 0)
                {
                    NpcEntity? firstTarget = FindAliveNpc(first);
                    NpcEntity? secondTarget = FindAliveNpc(second);

                    if (firstTarget != null && secondTarget != null)
                    {
                        Attack(first, firstPlayer, firstTarget);
                        Attack(second, secondPlayer, secondTarget);
                    }
                }

                first.Tick();
                second.Tick();

                Assert.Equal(first.Entities.Count, second.Entities.Count);
                Assert.Equal(first.AttacksOk, second.AttacksOk);
                Assert.Equal(first.RejectedAttackDistance, second.RejectedAttackDistance);
                Assert.Equal(first.NpcDeaths, second.NpcDeaths);
                Assert.Equal(first.NpcRespawns, second.NpcRespawns);

                for (int i = 0; i < first.Entities.Count; i++)
                {
                    Entity a = first.Entities[i];
                    Entity b = second.Entities[i];

                    Assert.Equal(a.GetType(), b.GetType());
                    Assert.Equal(a.Id, b.Id);
                    Assert.Equal(a.Action, b.Action);
                    Assert.Equal(a.Position, b.Position);
                    Assert.Equal(a.DirectionX, b.DirectionX);
                    Assert.Equal(a.DirectionY, b.DirectionY);

                    if (a is NpcEntity npcA)
                    {
                        NpcEntity npcB = Assert.IsType<NpcEntity>(b);
                        Assert.Equal(npcA.Health, npcB.Health);
                    }
                }
            }
        }

        private static NpcEntity? FindAliveNpc(Zone zone)
        {
            for (int i = 0; i < zone.Entities.Count; i++)
            {
                if (zone.Entities[i] is NpcEntity npc && npc.Action != EntityAction.Dead)
                {
                    return npc;
                }
            }

            return null;
        }

        private static void Advance(Zone zone, int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                zone.Tick();
            }
        }
    }
}
