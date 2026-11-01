#nullable enable
using Avelora.Shared;

namespace Avelora.Server.Entities
{
    /// <summary>Objeto apilado que existe temporalmente como entidad en el suelo (I-04).</summary>
    public sealed class GroundItemEntity : Entity
    {
        public GroundItemEntity(int id, int itemId, int count, long despawnTick)
            : base(id, EntityKind.Item)
        {
            ItemId = itemId;
            Count = count;
            DespawnTick = despawnTick;
        }

        /// <summary>Id del tipo de objeto de <c>items.json</c>.</summary>
        public int ItemId { get; }

        /// <summary>Unidades que contiene esta entidad.</summary>
        public int Count { get; }

        /// <summary>Primer tick en el que deja de existir.</summary>
        public long DespawnTick { get; }

        /// <summary>Dueño temporal de un objeto caído por inventario lleno; -1 significa sin reserva.</summary>
        public int OwnerPlayerId { get; init; } = -1;

        /// <summary>Primer tick en el que otro jugador puede recoger el objeto.</summary>
        public long ReservedUntilTick { get; init; }
    }
}
