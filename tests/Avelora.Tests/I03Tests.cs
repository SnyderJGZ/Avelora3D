using System;
using System.Collections.Generic;
using Avelora.Server.Entities;
using Avelora.Server.Npcs;
using Avelora.Server.Simulation;
using Avelora.Shared;
using Avelora.Shared.Maps;
using Xunit;

namespace Avelora.Tests
{
    /// <summary>
    /// Pruebas de I-03: datos nuevos de los PNJ, máquina de estados de la IA, golpes, vida, muerte,
    /// reaparición y regeneración del jugador, y determinismo con un jugador que se acerca.
    /// </summary>
    public class I03Tests
    {
        private static PlayerVitalsSettings Vitals(
            int maxHealth = 100,
            float spawnX = 5f,
            float spawnY = 5f,
            double respawnSeconds = 0.5d,
            double regenPerSecond = 0d,
            double regenDelaySeconds = 0d) =>
            new PlayerVitalsSettings(
                maxHealth,
                new Vec2(spawnX, spawnY),
                respawnSeconds,
                regenPerSecond,
                regenDelaySeconds);

        private static NpcType Orc(
            float aggroRadius = 4f,
            float leashRadius = 8f,
            int attackDamage = 8,
            float attackRange = 1f,
            float attackCooldownSeconds = 1.5f,
            int maxHealth = 100,
            float x = 10f,
            float y = 10f) =>
            new NpcType
            {
                Id = 2001,
                Name = "Orco",
                Speed = 2f,
                HalfWidth = 0.35f,
                HalfHeight = 0.35f,
                MaxHealth = maxHealth,
                RespawnSeconds = 10f,
                AggroRadius = aggroRadius,
                LeashRadius = leashRadius,
                AttackDamage = attackDamage,
                AttackRange = attackRange,
                AttackCooldownSeconds = attackCooldownSeconds,
            };

        private static NpcCatalog Catalog(
            NpcType type,
            float spawnX = 10f,
            float spawnY = 10f,
            int count = 1,
            float wanderRadius = 1f) =>
            new NpcCatalog
            {
                Types = new List<NpcType> { type },
                Spawners = new List<NpcSpawner>
                {
                    new NpcSpawner
                    {
                        TypeId = type.Id,
                        X = spawnX,
                        Y = spawnY,
                        Count = count,
                        WanderRadius = wanderRadius,
                    },
                },
            };

        private static Zone NewZone(
            NpcCatalog? npcs = null,
            PlayerVitalsSettings? vitals = null,
            int seed = 12345,
            MapCollisionGrid? map = null,
            int combatDamage = 10,
            float combatRange = 5f,
            float combatTolerance = 0.5f,
            double combatCooldownSeconds = 0.8d) =>
            new Zone(
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
                seed,
                new AttackQueue(256),
                new CombatSettings(combatDamage, combatRange, combatTolerance, combatCooldownSeconds),
                vitals);

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

        private static void Tick(Zone zone, int times)
        {
            for (int i = 0; i < times; i++)
            {
                zone.Tick();
            }
        }

        // ------------------------------------------------------------------ datos

        [Theory]
        [InlineData(-0.1f, 8f, 8, 1f, 1.5f)]
        [InlineData(20.1f, 8f, 8, 1f, 1.5f)]
        [InlineData(float.NaN, 8f, 8, 1f, 1.5f)]
        [InlineData(float.PositiveInfinity, 8f, 8, 1f, 1.5f)]
        public void Un_Aggro_Imposible_No_Valida(
            float aggroRadius,
            float leashRadius,
            int attackDamage,
            float attackRange,
            float attackCooldownSeconds)
        {
            NpcType type = Orc(aggroRadius, leashRadius, attackDamage, attackRange, attackCooldownSeconds);

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => Catalog(type).Validate(MapCollisionGrid.AllWalkable(20, 20)));

            Assert.Contains("aggroRadius", error.Message);
        }

        [Theory]
        [InlineData(-0.1f)]
        [InlineData(40.1f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void Una_Correa_Imposible_No_Valida(float leashRadius)
        {
            NpcType type = Orc(leashRadius: leashRadius);

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => Catalog(type).Validate(MapCollisionGrid.AllWalkable(20, 20)));

            Assert.Contains("leashRadius", error.Message);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(100001)]
        public void Un_Dano_De_Pnj_Imposible_No_Valida(int attackDamage)
        {
            NpcType type = Orc(attackDamage: attackDamage);

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => Catalog(type).Validate(MapCollisionGrid.AllWalkable(20, 20)));

            Assert.Contains("attackDamage", error.Message);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(5.1f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void Un_Alcance_De_Pnj_Imposible_No_Valida(float attackRange)
        {
            NpcType type = Orc(attackRange: attackRange);

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => Catalog(type).Validate(MapCollisionGrid.AllWalkable(20, 20)));

            Assert.Contains("attackRange", error.Message);
        }

        [Theory]
        [InlineData(0.1f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void Una_Recarga_De_Pnj_Imposible_No_Valida(float attackCooldownSeconds)
        {
            NpcType type = Orc(attackCooldownSeconds: attackCooldownSeconds);

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => Catalog(type).Validate(MapCollisionGrid.AllWalkable(20, 20)));

            Assert.Contains("attackCooldownSeconds", error.Message);
        }

        [Fact]
        public void La_Correa_No_Puede_Ser_Menor_Que_El_Deambular_Del_Spawner()
        {
            NpcType type = Orc(leashRadius: 2f);

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => Catalog(type, wanderRadius: 3f).Validate(MapCollisionGrid.AllWalkable(20, 20)));

            Assert.Contains("correa", error.Message);
            Assert.Contains("leashRadius", error.Message);
        }

        // ------------------------------------------------------------------ IA

        [Fact]
        public void Wander_Pasa_A_Chase_Con_Un_Jugador_Dentro_Del_Aggro()
        {
            NpcCatalog catalog = Catalog(Orc(aggroRadius: 4f));
            Zone zone = NewZone(catalog, Vitals());
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(12f, 10f), new Vec2(0.4f, 0.4f), 8);

            Tick(zone, 1);

            NpcEntity npc = OnlyNpc(zone);
            Assert.Equal(NpcAiState.Chase, npc.AiState);
            Assert.Equal(player.Id, npc.TargetEntityId);
        }

        [Fact]
        public void Wander_No_Cambia_Fuera_Del_Aggro()
        {
            NpcCatalog catalog = Catalog(Orc(aggroRadius: 4f));
            Zone zone = NewZone(catalog, Vitals());
            zone.SpawnPlayer(1, new Vec2(16f, 10f), new Vec2(0.4f, 0.4f), 8);

            Tick(zone, 10);

            Assert.Equal(NpcAiState.Wander, OnlyNpc(zone).AiState);
        }

        [Fact]
        public void Un_Aggro_Cero_No_Persigue_Pero_Se_Defiende_Al_Recibir_Dano()
        {
            NpcCatalog catalog = Catalog(Orc(aggroRadius: 0f, attackRange: 1f));
            Zone zone = NewZone(catalog, Vitals());
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(13f, 10f), new Vec2(0.4f, 0.4f), 8);

            Tick(zone, 20);

            NpcEntity npc = OnlyNpc(zone);
            Assert.Equal(NpcAiState.Wander, npc.AiState);

            // A 3 tiles sigue fuera del alcance del golpe, así que el aggro por daño se ve como
            // Chase y no como Attack.
            zone.Attacks!.TryEnqueue(new AttackIntent(player.Id, npc.Id));
            Tick(zone, 1);

            Assert.Equal(NpcAiState.Chase, npc.AiState);
            Assert.Equal(player.Id, npc.TargetEntityId);
        }

        [Fact]
        public void Chase_Pasa_A_Attack_Al_Entrar_En_Alcance()
        {
            NpcCatalog catalog = Catalog(Orc(aggroRadius: 4f, attackRange: 1f));
            Zone zone = NewZone(catalog, Vitals());
            zone.SpawnPlayer(1, new Vec2(10.5f, 10f), new Vec2(0.4f, 0.4f), 8);

            Tick(zone, 1);
            Assert.Equal(NpcAiState.Chase, OnlyNpc(zone).AiState);

            Tick(zone, 1);
            Assert.Equal(NpcAiState.Attack, OnlyNpc(zone).AiState);
        }

        [Fact]
        public void Chase_Pasa_A_Return_Al_Pasar_La_Correa()
        {
            NpcCatalog catalog = Catalog(Orc(aggroRadius: 4f, leashRadius: 3f));
            Zone zone = NewZone(catalog, Vitals());
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(10.5f, 10f), new Vec2(0.4f, 0.4f), 8);

            NpcEntity npc = OnlyNpc(zone);
            npc.AiState = NpcAiState.Chase;
            npc.TargetEntityId = player.Id;
            npc.Position = npc.Home + new Vec2(5f, 0f);

            Tick(zone, 1);

            Assert.Equal(NpcAiState.Return, npc.AiState);
            Assert.Equal(-1, npc.TargetEntityId);
        }

        [Fact]
        public void Return_Pasa_A_Wander_En_Casa_Con_La_Vida_Llena()
        {
            NpcCatalog catalog = Catalog(Orc(aggroRadius: 4f));
            Zone zone = NewZone(catalog, Vitals());

            NpcEntity npc = OnlyNpc(zone);
            npc.AiState = NpcAiState.Return;
            npc.Health = 1;
            npc.Position = npc.Home + new Vec2(0.1f, 0f);

            Tick(zone, 1);

            Assert.Equal(NpcAiState.Wander, npc.AiState);
            Assert.Equal(npc.MaxHealth, npc.Health);
            Assert.Equal(-1, npc.TargetEntityId);
        }

        [Fact]
        public void En_Un_Empate_De_Distancia_Gana_El_Jugador_Con_Id_Menor()
        {
            NpcCatalog catalog = Catalog(Orc(aggroRadius: 4f));
            Zone zone = NewZone(catalog, Vitals());
            PlayerEntity first = zone.SpawnPlayer(1, new Vec2(12f, 10f), new Vec2(0.4f, 0.4f), 8);
            PlayerEntity second = zone.SpawnPlayer(2, new Vec2(8f, 10f), new Vec2(0.4f, 0.4f), 8);

            Tick(zone, 1);

            NpcEntity npc = OnlyNpc(zone);
            Assert.True(first.Id < second.Id);
            Assert.Equal(first.Id, npc.TargetEntityId);
        }

        // ------------------------------------------------------------------ golpes

        [Fact]
        public void El_Primer_Golpe_Sale_Al_Tick_Siguiente_Y_Los_Demas_Cada_30()
        {
            NpcCatalog catalog = Catalog(Orc(attackDamage: 8, attackRange: 1f, attackCooldownSeconds: 1.5f));
            Zone zone = NewZone(catalog, Vitals(regenPerSecond: 0d));
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(10.5f, 10f), new Vec2(0.4f, 0.4f), 8);

            Tick(zone, 1); // Wander -> Chase
            Tick(zone, 1); // Chase -> Attack; el primer golpe se programa al tick siguiente
            Assert.Equal(100, player.Health);

            Tick(zone, 1); // golpe en el tick 2
            Assert.Equal(92, player.Health);
            Assert.Equal(EntityAction.Attacking, OnlyNpc(zone).Action);

            // 29 ticks después todavía no ha vuelto a golpear: la recarga es de 30 exactos.
            Tick(zone, 29);
            Assert.Equal(92, player.Health);

            Tick(zone, 1);
            Assert.Equal(84, player.Health);
        }

        [Fact]
        public void El_Dano_Se_Resta_Con_Minimo_Cero_Y_Mata()
        {
            NpcCatalog catalog = Catalog(Orc(attackDamage: 8, attackRange: 1f, attackCooldownSeconds: 1.5f));
            Zone zone = NewZone(catalog, Vitals());
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(10.5f, 10f), new Vec2(0.4f, 0.4f), 8);
            player.Health = 5;

            Tick(zone, 3);

            Assert.Equal(0, player.Health);
            Assert.Equal(EntityAction.Dead, player.Action);
            Assert.True(player.DeathTick >= 0);
        }

        // ------------------------------------------------------------------ jugador

        [Fact]
        public void Muerto_No_Se_Mueve_Pero_Consume_Inputs_Y_No_Puede_Atacar()
        {
            Zone zone = NewZone(vitals: Vitals(respawnSeconds: 10d));
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            player.Health = 0;

            Tick(zone, 1);
            Assert.Equal(EntityAction.Dead, player.Action);

            Vec2 before = player.Position;
            zone.Inputs.TryEnqueue(new MoveIntent(player.Id, 7, 1, 0));
            Tick(zone, 1);

            Assert.Equal(before, player.Position);
            Assert.Equal(7u, player.LastProcessedSequence);

            zone.Attacks!.TryEnqueue(new AttackIntent(player.Id, 999));
            Tick(zone, 1);

            Assert.Equal(1L, zone.RejectedAttackDead);
        }

        [Fact]
        public void Reaparece_A_Los_Ticks_Configurados_En_El_Spawn_Con_La_Vida_Llena()
        {
            var vitals = new PlayerVitalsSettings(
                100,
                new Vec2(5f, 5f),
                0.5d,
                0d,
                0d);

            Zone zone = NewZone(vitals: vitals);
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            player.Health = 0;

            Tick(zone, 1);
            Assert.Equal(EntityAction.Dead, player.Action);

            // 0,5 s a 20 Hz = 10 ticks; el primero ya cuenta desde DeathTick.
            Tick(zone, 9);
            Assert.Equal(EntityAction.Dead, player.Action);

            Tick(zone, 1);
            Assert.Equal(EntityAction.None, player.Action);
            Assert.Equal(new Vec2(5f, 5f), player.Position);
            Assert.Equal(100, player.Health);
            Assert.Equal(-1L, player.DeathTick);
        }

        [Fact]
        public void Regenera_Solo_Pasado_El_Retraso_Y_Nunca_Por_Encima_Del_Maximo()
        {
            var vitals = new PlayerVitalsSettings(
                100,
                new Vec2(5f, 5f),
                5d,
                100d,
                0.05d);

            Zone zone = NewZone(vitals: vitals);
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(10f, 10f), new Vec2(0.4f, 0.4f), 8);
            player.Health = 99;
            player.LastDamageTick = 0;

            // Tick 0: aún no ha pasado el retraso de un tick.
            Tick(zone, 1);
            Assert.Equal(99, player.Health);

            // Tick 1: ya regenera; con 100/s suma 5 y el tope lo deja en 100.
            Tick(zone, 1);
            Assert.Equal(100, player.Health);

            // Sigue lleno: no se pasa del máximo.
            Tick(zone, 5);
            Assert.Equal(100, player.Health);
        }

        // ------------------------------------------------------------------ determinismo

        [Fact]
        public void Dos_Zonas_Con_La_Misma_Semilla_Y_Los_Mismos_Inputs_Dan_El_Mismo_Estado()
        {
            NpcCatalog catalog = Catalog(Orc(aggroRadius: 4f, leashRadius: 8f));
            Zone first = NewZone(catalog, Vitals(respawnSeconds: 5d), seed: 777);
            Zone second = NewZone(catalog, Vitals(respawnSeconds: 5d), seed: 777);

            PlayerEntity firstPlayer = first.SpawnPlayer(1, new Vec2(5f, 10f), new Vec2(0.4f, 0.4f), 8);
            PlayerEntity secondPlayer = second.SpawnPlayer(1, new Vec2(5f, 10f), new Vec2(0.4f, 0.4f), 8);

            for (uint sequence = 1; sequence <= 400; sequence++)
            {
                // El jugador camina hacia el orco: el PNJ lo detecta, lo persigue y lo golpea.
                first.Inputs.TryEnqueue(new MoveIntent(firstPlayer.Id, sequence, 1, 0));
                second.Inputs.TryEnqueue(new MoveIntent(secondPlayer.Id, sequence, 1, 0));
                first.Tick();
                second.Tick();
            }

            Assert.Equal(first.Entities.Count, second.Entities.Count);

            for (int i = 0; i < first.Entities.Count; i++)
            {
                Entity a = first.Entities[i];
                Entity b = second.Entities[i];

                Assert.Equal(a.Id, b.Id);
                Assert.Equal(a.Kind, b.Kind);
                Assert.Equal(a.Position, b.Position);
                Assert.Equal(a.DirectionX, b.DirectionX);
                Assert.Equal(a.DirectionY, b.DirectionY);
                Assert.Equal(a.Action, b.Action);

                if (a is PlayerEntity playerA)
                {
                    PlayerEntity playerB = Assert.IsType<PlayerEntity>(b);
                    Assert.Equal(playerA.Health, playerB.Health);
                    Assert.Equal(playerA.LastProcessedSequence, playerB.LastProcessedSequence);
                    Assert.Equal(playerA.DeathTick, playerB.DeathTick);
                }
                else if (a is NpcEntity npcA)
                {
                    NpcEntity npcB = Assert.IsType<NpcEntity>(b);
                    Assert.Equal(npcA.AiState, npcB.AiState);
                    Assert.Equal(npcA.TargetEntityId, npcB.TargetEntityId);
                    Assert.Equal(npcA.Health, npcB.Health);
                    Assert.Equal(npcA.NextAttackTick, npcB.NextAttackTick);
                }
            }
        }
    }
}
