#nullable enable
using System;
using Avelora.Shared;
using Avelora.Shared.Protocol;

namespace Avelora.Server.Entities
{
    /// <summary>
    /// Enemigo del mundo (I-01 e I-02). Deambula mientras está vivo y, cuando su vida llega a 0,
    /// queda tendido unos ticks antes de que su spawner lo reponga.
    /// </summary>
    /// <remarks>
    /// Como cualquier <see cref="Entity"/>, vive en el hilo de simulación y viaja en el snapshot
    /// (<see cref="EntityKind.Npc"/>) con su vida en porcentaje y su acción. Además de la posición y
    /// la caja, guarda lo que necesita la IA y el combate: su tipo, su velocidad, el punto de su
    /// spawner, el radio que no debe abandonar, su vida y su tiempo de reaparición.
    /// </remarks>
    public sealed class NpcEntity : Entity
    {
        /// <summary>Ticks que el cadáver permanece en la zona antes de retirarse (1,5 s a 20 Hz).</summary>
        public const int DeathAnimationTicks = 30;

        public NpcEntity(
            int id,
            int typeId,
            float speed,
            Vec2 home,
            float wanderRadius,
            long nextDirectionChangeTick,
            int maxHealth,
            long respawnTicks,
            int spawnerIndex,
            float aggroRadius = 0f,
            float leashRadius = 40f,
            int attackDamage = 1,
            float attackRange = 1f,
            long attackCooldownTicks = 30,
            int experienceReward = 0,
            int level = 1,
            int defense = 0,
            int magicResist = 0,
            byte rank = 0,
            int lootRolls = 1)
            : base(id, EntityKind.Npc)
        {
            if (maxHealth < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxHealth), maxHealth, "La vida máxima debe ser >= 1.");
            }

            if (respawnTicks < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(respawnTicks), respawnTicks, "La reaparición debe ser >= 1 tick.");
            }

            if (aggroRadius < 0f || float.IsNaN(aggroRadius) || float.IsInfinity(aggroRadius))
            {
                throw new ArgumentOutOfRangeException(nameof(aggroRadius), aggroRadius, "El radio de aggro no puede ser negativo.");
            }

            if (leashRadius < 0f || float.IsNaN(leashRadius) || float.IsInfinity(leashRadius))
            {
                throw new ArgumentOutOfRangeException(nameof(leashRadius), leashRadius, "La correa no puede ser negativa.");
            }

            if (attackDamage < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(attackDamage), attackDamage, "El daño debe ser >= 1.");
            }

            if (!(attackRange > 0f) || float.IsNaN(attackRange) || float.IsInfinity(attackRange))
            {
                throw new ArgumentOutOfRangeException(nameof(attackRange), attackRange, "El alcance debe ser positivo.");
            }

            if (attackCooldownTicks < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(attackCooldownTicks),
                    attackCooldownTicks,
                    "La recarga debe ser >= 1 tick.");
            }

            if (experienceReward < 0 || experienceReward > 20_000_000)
            {
                throw new ArgumentOutOfRangeException(nameof(experienceReward));
            }

            if (rank > ProtocolConstants.MaxEntityRank)
            {
                throw new ArgumentOutOfRangeException(nameof(rank));
            }

            if (lootRolls < 1 || lootRolls > 5)
            {
                throw new ArgumentOutOfRangeException(nameof(lootRolls));
            }

            if (level < 1 || level > byte.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(level));
            }

            if (defense < 0 || defense > 100_000 || magicResist < 0 || magicResist > 100_000)
            {
                throw new ArgumentOutOfRangeException(nameof(defense));
            }

            TypeId = typeId;
            Speed = speed;
            Home = home;
            WanderRadius = wanderRadius;
            NextDirectionChangeTick = nextDirectionChangeTick;
            MaxHealth = maxHealth;
            RespawnTicks = respawnTicks;
            SpawnerIndex = spawnerIndex;
            AggroRadius = aggroRadius;
            LeashRadius = leashRadius;
            AttackDamage = attackDamage;
            AttackRange = attackRange;
            AttackCooldownTicks = attackCooldownTicks;
            ExperienceReward = experienceReward;
            Level = level;
            Defense = defense;
            MagicResist = magicResist;
            Rank = rank;
            LootRolls = lootRolls;
            Health = maxHealth;
        }

        /// <summary>Tipo de enemigo de <c>npcs.json</c>.</summary>
        public int TypeId { get; }

        /// <summary>Velocidad de deambular, en unidades por segundo.</summary>
        public float Speed { get; }

        /// <summary>Punto del spawner alrededor del cual deambula.</summary>
        public Vec2 Home { get; }

        /// <summary>Distancia máxima a <see cref="Home"/> antes de volver.</summary>
        public float WanderRadius { get; }

        /// <summary>Radio en el que un jugador vivo hace que el PNJ pase a perseguirlo (0 = pasivo).</summary>
        public float AggroRadius { get; }

        /// <summary>Distancia máxima a <see cref="Home"/> mientras persigue; al pasarla vuelve.</summary>
        public float LeashRadius { get; }

        /// <summary>Vida que quita cada golpe del PNJ.</summary>
        public int AttackDamage { get; }

        /// <summary>Alcance del golpe, en tiles, medido entre centros.</summary>
        public float AttackRange { get; }

        /// <summary>Ticks mínimos entre dos golpes del PNJ.</summary>
        public long AttackCooldownTicks { get; }

        /// <summary>Experiencia que otorga al morir.</summary>
        public int ExperienceReward { get; }

        /// <summary>Nivel del tipo de enemigo.</summary>
        public int Level { get; }

        /// <summary>Defensa física del enemigo.</summary>
        public int Defense { get; }

        /// <summary>Resistencia mágica del enemigo.</summary>
        public int MagicResist { get; }

        /// <summary>Rango declarado en <c>npcs.json</c>; 0 representa el rango normal implícito.</summary>
        public byte Rank { get; }

        /// <summary>Número de veces que se tira la tabla de botín al morir.</summary>
        public int LootRolls { get; }

        /// <summary>Id del jugador que dio el golpe mortal, o -1 si no murió por combate de jugador.</summary>
        public int KillerPlayerId { get; internal set; } = -1;

        /// <summary>Estado actual de la IA (I-03).</summary>
        public NpcAiState AiState { get; set; } = NpcAiState.Wander;

        /// <summary>Jugador al que persigue o golpea; <c>-1</c> si no tiene objetivo.</summary>
        public int TargetEntityId { get; set; } = -1;

        /// <summary>Tick en el que puede volver a golpear.</summary>
        public long NextAttackTick { get; set; }

        /// <summary>
        /// Tick de simulación en el que la IA vuelve a elegir dirección. La dirección actual vive en
        /// <see cref="Entity.DirectionX"/>/<see cref="Entity.DirectionY"/>.
        /// </summary>
        public long NextDirectionChangeTick { get; set; }

        /// <summary>Vida máxima del tipo.</summary>
        public int MaxHealth { get; }

        /// <summary>Vida actual. La baja <see cref="Avelora.Server.Simulation.CombatSystem"/> y nunca pasa de 0.</summary>
        public int Health { get; set; }

        /// <summary>Ticks que tarda el spawner en reponerlo tras la muerte.</summary>
        public long RespawnTicks { get; }

        /// <summary>Índice del spawner que lo creó, o <c>-1</c> si se creó por otra vía (pruebas).</summary>
        public int SpawnerIndex { get; }

        /// <summary>
        /// Tick en el que la IA detectó la muerte; <c>-1</c> mientras está vivo. Marca cuánto
        /// cadáver queda y cuándo toca reponerlo.
        /// </summary>
        public long DeathTick { get; internal set; } = -1;

        /// <summary>Si la IA ya ha anotado la muerte de este PNJ (una sola vez).</summary>
        internal bool DeathScheduled { get; set; }

        /// <summary>
        /// Vida restante en porcentaje, redondeando hacia arriba: un PNJ con vida &gt; 0 nunca manda
        /// 0 al cliente (I-02).
        /// </summary>
        public byte HealthPercent => EntityHealth.Percent(Health, MaxHealth);
    }
}
