using System;
using System.Collections.Generic;
using Avelora.Server.Entities;
using Avelora.Server.Items;
using Avelora.Server.Npcs;
using Avelora.Server.Progression;
using Avelora.Server.Simulation;
using Avelora.Shared;
using Avelora.Shared.Protocol;
using Xunit;

namespace Avelora.Tests
{
    public sealed class I13Tests
    {
        private static Zone NewZone(
            float range = 1.2f,
            float tolerance = 0.5f,
            MapCollisionGrid? map = null,
            ItemCatalog? items = null,
            ClassCatalog? classes = null,
            NpcCatalog? npcs = null)
        {
            classes ??= ClassCatalog.Load(System.IO.Path.Combine(AppContext.BaseDirectory, "classes.json"));
            classes.Find(1)!.Unarmed!.Range = range;
            return new Zone(
                1,
                map ?? MapCollisionGrid.AllWalkable(40, 40),
                new EntityStore(),
                new EntityIdAllocator(),
                new InputQueue(256),
                new MovementSystem(4f, SimulationConstants.FixedDt),
                128,
                4,
                new InterestSettings(40f, 8f, 0f),
                npcs,
                attacks: new AttackQueue(256),
                combat: new CombatSettings(tolerance),
                items: items,
                classes: classes);
        }

        private static NpcEntity AddNpc(Zone zone, int id, Vec2 position, int health = 100)
        {
            var npc = new NpcEntity(
                id, 2001, 0f, position, 0f, long.MaxValue, health, 200, -1,
                defense: 0, magicResist: 0)
            {
                Position = position,
                HalfSize = new Vec2(0.35f, 0.35f),
            };
            zone.AddEntity(npc);
            return npc;
        }

        private static void Attack(Zone zone, PlayerEntity player, NpcEntity npc) =>
            zone.Attacks!.TryEnqueue(new AttackIntent(player.Id, npc.Id));

        [Fact]
        public void Ataque_A_Distancia_Aplica_Alcance_Y_Tolerancia_Y_Cuenta_Solo_El_Acertado()
        {
            Zone zone = NewZone(range: 6f);
            PlayerEntity first = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            PlayerEntity second = zone.SpawnPlayer(2, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            PlayerEntity third = zone.SpawnPlayer(3, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity atRange = AddNpc(zone, 100, new Vec2(16f, 10f));
            NpcEntity withinTolerance = AddNpc(zone, 101, new Vec2(16.4f, 10f));
            NpcEntity outsideTolerance = AddNpc(zone, 102, new Vec2(16.6f, 10f));

            Attack(zone, first, atRange);
            Attack(zone, second, withinTolerance);
            Attack(zone, third, outsideTolerance);
            zone.Tick();

            Assert.Equal(2, zone.AttacksOk);
            Assert.Equal(1, zone.RejectedAttackDistance);
            Assert.Equal(2, zone.AttacksRanged);
            Assert.Equal(100, outsideTolerance.Health);
        }

        [Fact]
        public void Pared_Rechaza_Sin_Consumir_Recarga_Y_Sin_Mapa_Se_Mantiene_Compatibilidad()
        {
            MapCollisionGrid map = MapCollisionGrid.FromRows(new[]
            {
                "....#....", "....#....", "....#....", "....#....", "....#....",
            });
            Zone zone = NewZone(range: 6f, tolerance: 0.5f, map: map);
            PlayerEntity attacker = zone.SpawnPlayer(1, new Vec2(1.5f, 2.5f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity behindWall = AddNpc(zone, 100, new Vec2(6.5f, 2.5f));
            NpcEntity withView = AddNpc(zone, 101, new Vec2(2.5f, 3.5f));
            Attack(zone, attacker, behindWall);
            Attack(zone, attacker, withView);
            zone.Tick();

            Assert.Equal(1, zone.RejectedAttackLineOfSight);
            Assert.Equal(0, zone.RejectedAttackCooldown);
            Assert.Equal(100, behindWall.Health);
            Assert.True(withView.Health < 100);
            Assert.Equal(1, zone.AttacksOk);
            Assert.Equal(1, zone.CombatEvents.Count);

            var entities = new EntityStore();
            var noMapAttacker = new PlayerEntity(1, 1, 8, 20)
            {
                Position = new Vec2(1.5f, 2.5f),
                HalfSize = new Vec2(0.4f, 0.4f),
            };
            var noMapTarget = new NpcEntity(2, 2001, 0f, new Vec2(6.5f, 2.5f), 0f, 0, 100, 200, -1)
            {
                Position = new Vec2(6.5f, 2.5f),
                HalfSize = new Vec2(0.35f, 0.35f),
            };
            entities.Add(noMapAttacker);
            entities.Add(noMapTarget);
            ClassCatalog noMapClasses = ClassCatalog.Load(System.IO.Path.Combine(AppContext.BaseDirectory, "classes.json"));
            noMapClasses.Find(1)!.Unarmed!.Range = 6f;
            var withoutMap = new CombatSystem(new CombatSettings(0.5f), classes: noMapClasses);
            withoutMap.Tick(entities, new List<AttackIntent> { new AttackIntent(1, 2) }, 0);
            Assert.Equal(1, withoutMap.AttacksOk);
            Assert.Equal(0, withoutMap.RejectedLineOfSight);
        }

        [Fact]
        public void Pnj_En_Regreso_No_Recibe_Dano_Ni_Genera_Evento()
        {
            Zone zone = NewZone();
            PlayerEntity attacker = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity target = AddNpc(zone, 100, new Vec2(11f, 10f));
            target.AiState = NpcAiState.Return;

            Attack(zone, attacker, target);
            zone.Tick();

            Assert.Equal(1, zone.RejectedAttackEvading);
            Assert.Equal(100, target.Health);
            Assert.Equal(0, zone.CombatEvents.Count);
        }

        [Fact]
        public void CombatEvents_Incluye_Dano_Mitigado_Banderas_Y_Muerte()
        {
            ClassCatalog classes = ClassCatalog.Load(System.IO.Path.Combine(AppContext.BaseDirectory, "classes.json"));
            classes.Find(2)!.Unarmed!.Range = 6f;
            Zone zone = NewZone(range: 1.2f, classes: classes);
            PlayerEntity mage = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8, classId: 2);
            PlayerEntity warrior = zone.SpawnPlayer(2, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity magicalTarget = AddNpc(zone, 100, new Vec2(11f, 10f), health: 1000);
            NpcEntity killedTarget = AddNpc(zone, 101, new Vec2(11f, 10.5f), health: 1);

            Attack(zone, mage, magicalTarget);
            Attack(zone, warrior, killedTarget);
            zone.Tick();

            Assert.Equal(2, zone.CombatEvents.Count);
            CombatEventEntry magic = zone.CombatEvents[0];
            Assert.Equal(mage.Id, magic.AttackerId);
            Assert.Equal(magicalTarget.Id, magic.TargetId);
            Assert.Equal(magicalTarget.Health, 1000 - magic.Damage);
            Assert.Equal(CombatEventFlags.Ranged | CombatEventFlags.Magical, magic.Flags);

            CombatEventEntry killed = zone.CombatEvents[1];
            Assert.Equal(warrior.Id, killed.AttackerId);
            Assert.Equal(killedTarget.Id, killed.TargetId);
            Assert.True(killed.Damage > 1);
            Assert.Equal(CombatEventFlags.Killed, killed.Flags);
            Assert.Equal(1, zone.AttacksRanged);
        }

        [Fact]
        public void CombatEventBuffer_Cuenta_Desbordamiento_Y_Clear_No_Borra_Descartes()
        {
            var buffer = new CombatEventBuffer();
            var entry = new CombatEventEntry(1, 2, 3, CombatEventFlags.None);
            for (int i = 0; i < 257; i++)
            {
                buffer.Add(entry);
            }

            Assert.Equal(256, buffer.Count);
            Assert.Equal(1, buffer.Dropped);
            buffer.Clear();
            Assert.Equal(0, buffer.Count);
            Assert.Equal(1, buffer.Dropped);
        }

        [Fact]
        public void Pnj_Atascado_Abandona_Persecucion_Y_El_Regreso_Se_Fuerza_A_Casa()
        {
            MapCollisionGrid map = MapCollisionGrid.FromRows(new[]
            {
                "....#....", "....#....", "....#....", "....#....", "....#....",
            });
            Zone chaseZone = NewNpcZone(map, new Vec2(1.5f, 2.5f));
            PlayerEntity player = chaseZone.SpawnPlayer(1, new Vec2(7.5f, 2.5f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity chaser = Assert.IsType<NpcEntity>(chaseZone.Entities[0]);
            chaser.AiState = NpcAiState.Chase;
            chaser.TargetEntityId = player.Id;
            int stuckTicks = 0;
            for (int i = 0; i < 200 && chaseZone.NpcChaseGiveUps == 0; i++)
            {
                Vec2 before = chaser.Position;
                bool wantedToMove = chaser.DirectionX != 0 || chaser.DirectionY != 0;
                chaseZone.Tick();
                stuckTicks = wantedToMove && chaser.Position == before ? stuckTicks + 1
                    : chaser.Position == before ? stuckTicks : 0;
            }

            Assert.Equal(NpcAiState.Return, chaser.AiState);
            Assert.Equal(1, chaseZone.NpcChaseGiveUps);
            Assert.True(stuckTicks >= NpcSystem.ChaseStuckTicks);

            Zone returnZone = NewNpcZone(map, new Vec2(7.5f, 2.5f));
            NpcEntity returning = Assert.IsType<NpcEntity>(returnZone.Entities[0]);
            returning.Position = new Vec2(1.5f, 2.5f);
            returning.Health = 5;
            returning.AiState = NpcAiState.Return;
            for (int i = 0; i < 200 && returnZone.NpcReturnTeleports == 0; i++)
            {
                returnZone.Tick();
            }

            Assert.Equal(1, returnZone.NpcReturnTeleports);
            Assert.Equal(returning.Home, returning.Position);
            returnZone.Tick();
            Assert.Equal(NpcAiState.Wander, returning.AiState);
            Assert.Equal(returning.MaxHealth, returning.Health);
        }

        [Fact]
        public void Deslizar_Junto_A_La_Pared_No_Cuenta_Como_Atasco_Y_Golpe_De_Pnj_Genera_Evento()
        {
            MapCollisionGrid map = MapCollisionGrid.FromRows(new[]
            {
                "....#....", "....#....", "....#....", "....#....", "....#....",
            });
            Zone slideZone = NewNpcZone(map, new Vec2(1.5f, 2.5f));
            NpcEntity slider = Assert.IsType<NpcEntity>(slideZone.Entities[0]);
            slider.Position = new Vec2(3.5f, 2f);
            slider.AiState = NpcAiState.Wander;
            slider.DirectionX = 1;
            slider.DirectionY = 1;
            slider.NextDirectionChangeTick = long.MaxValue;
            slideZone.Tick();
            Assert.Equal(0, slider.StuckTicks);
            Assert.True(slider.Position.Y > 2f);

            Zone attackZone = NewNpcZone(MapCollisionGrid.AllWalkable(20, 20), new Vec2(5f, 5f));
            PlayerEntity player = attackZone.SpawnPlayer(1, new Vec2(5.5f, 5f), new Vec2(0.4f, 0.4f), 8);
            NpcEntity npc = Assert.IsType<NpcEntity>(attackZone.Entities[0]);
            npc.AiState = NpcAiState.Attack;
            npc.TargetEntityId = player.Id;
            npc.NextAttackTick = 0;
            attackZone.Tick();

            CombatEventEntry entry = Assert.Single(new[] { attackZone.CombatEvents[0] });
            Assert.Equal(npc.Id, entry.AttackerId);
            Assert.Equal(player.Id, entry.TargetId);
            Assert.Equal(CombatEventFlags.None, entry.Flags);
        }

        private static Zone NewNpcZone(MapCollisionGrid map, Vec2 home)
        {
            var npcs = new NpcCatalog
            {
                Types = new List<NpcType>
                {
                    new NpcType
                    {
                        Id = 2001, Name = "Enemigo", Speed = 2f, HalfWidth = 0.35f, HalfHeight = 0.35f,
                        MaxHealth = 30, Experience = 0, Level = 1, RespawnSeconds = 10f,
                        AggroRadius = 0f, LeashRadius = 20f, AttackDamage = 1, AttackRange = 1f,
                        AttackCooldownSeconds = 1f,
                    },
                },
                Spawners = new List<NpcSpawner>
                {
                    new NpcSpawner { TypeId = 2001, X = home.X, Y = home.Y, Count = 1, WanderRadius = 1f },
                },
            };
            return NewZone(map: map, npcs: npcs);
        }
    }
}
