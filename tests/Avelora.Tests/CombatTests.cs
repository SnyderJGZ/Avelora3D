using System;
using System.Collections.Generic;
using Avelora.Server.Entities;
using Avelora.Server.Items;
using Avelora.Server.Npcs;
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
        private const int Damage = 10;
        private const float Range = 1.2f;
        private const float Tolerance = 0.5f;
        private const double CooldownSeconds = 0.8d;

        private static Zone NewZone(
            NpcCatalog? npcs = null,
            int seed = 12345,
            int damage = Damage,
            float range = Range,
            float tolerance = Tolerance,
            double cooldownSeconds = CooldownSeconds,
            ItemCatalog? items = null)
        {
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
                new CombatSettings(damage, range, tolerance, cooldownSeconds),
                items: items);
        }

        private static ItemCatalog WeaponCatalog() => new ItemCatalog
        {
            Items = new List<ItemType>
            {
                new ItemType { Id = 3101, Name = "Espada corta", Kind = "weapon", MaxStack = 1, Damage = 14, Range = 1.2f, CooldownSeconds = 0.8d },
                new ItemType { Id = 3102, Name = "Hacha de orco", Kind = "weapon", MaxStack = 1, Damage = 22, Range = 1.3f, CooldownSeconds = 1.2d },
            },
        };

        private static NpcEntity AddNpc(Zone zone, int id, Vec2 position, int maxHealth = 30, long respawnTicks = 200)
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
                spawnerIndex: -1)
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
            Assert.Equal(2, zone.RejectedAttackTarget);
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
            Assert.Equal(20, target.Health);

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
            Assert.Equal(980, npc.Health);
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
            Assert.Equal(986, npc.Health);
            Assert.Equal(1, zone.AttacksWithWeapon);

            for (int tick = 1; tick <= 15; tick++)
            {
                Attack(zone, attacker, npc);
                zone.Tick();
            }

            Assert.Equal(15, zone.RejectedAttackCooldown);
            Attack(zone, attacker, npc);
            zone.Tick();
            Assert.Equal(972, npc.Health);
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
            Assert.Equal(978, npc.Health);

            for (int tick = 1; tick <= 23; tick++)
            {
                Attack(zone, attacker, npc);
                zone.Tick();
            }

            Assert.Equal(23, zone.RejectedAttackCooldown);
            Attack(zone, attacker, npc);
            zone.Tick();
            Assert.Equal(956, npc.Health);
            Assert.Equal(2, zone.AttacksWithWeapon);
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
        public void Jugador_Sin_Arma_Sigue_Usando_Las_Reglas_De_Punos()
        {
            Zone zone = NewZone(damage: 10, range: 1.2f, tolerance: 0.5f, cooldownSeconds: 0.8d, items: WeaponCatalog());
            PlayerEntity attacker = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity npc = AddNpc(zone, 100, new Vec2(11f, 10f), maxHealth: 1000);

            Attack(zone, attacker, npc);
            zone.Tick();

            Assert.Equal(990, npc.Health);
            Assert.Equal(0, zone.AttacksWithWeapon);
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
            Assert.Equal(20, npc.Health);
            Assert.NotEqual(EntityAction.Dead, npc.Action);

            Advance(zone, 15);
            Attack(zone, attacker, npc);
            zone.Tick();
            Assert.Equal(10, npc.Health);
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
