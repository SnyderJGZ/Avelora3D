#nullable enable
using System;
using Avelora.Shared;

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
            int spawnerIndex)
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

            TypeId = typeId;
            Speed = speed;
            Home = home;
            WanderRadius = wanderRadius;
            NextDirectionChangeTick = nextDirectionChangeTick;
            MaxHealth = maxHealth;
            RespawnTicks = respawnTicks;
            SpawnerIndex = spawnerIndex;
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
        public byte HealthPercent
        {
            get
            {
                if (Health <= 0)
                {
                    return 0;
                }

                int percent = ((100 * Health) + MaxHealth - 1) / MaxHealth;
                return (byte)(percent > 100 ? 100 : percent);
            }
        }
    }
}
