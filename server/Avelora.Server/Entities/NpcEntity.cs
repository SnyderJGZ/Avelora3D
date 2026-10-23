#nullable enable
using Avelora.Shared;

namespace Avelora.Server.Entities
{
    /// <summary>
    /// Enemigo o PNJ del mundo (I-01). Todavía no tiene vida ni combate: solo deambula.
    /// </summary>
    /// <remarks>
    /// Como cualquier <see cref="Entity"/>, vive en el hilo de simulación y viaja en el snapshot
    /// (<see cref="EntityKind.Npc"/>). Además de la posición y la caja, guarda lo que necesita la
    /// IA: su tipo, su velocidad, el punto de su spawner y el radio que no debe abandonar.
    /// </remarks>
    public sealed class NpcEntity : Entity
    {
        public NpcEntity(
            int id,
            int typeId,
            float speed,
            Vec2 home,
            float wanderRadius,
            long nextDirectionChangeTick)
            : base(id, EntityKind.Npc)
        {
            TypeId = typeId;
            Speed = speed;
            Home = home;
            WanderRadius = wanderRadius;
            NextDirectionChangeTick = nextDirectionChangeTick;
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
    }
}
