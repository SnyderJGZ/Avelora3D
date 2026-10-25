#nullable enable
namespace Avelora.Server.Entities
{
    /// <summary>Contenido mutable de un hueco del inventario, propiedad del hilo de simulación.</summary>
    public struct InventorySlot
    {
        public InventorySlot(ushort itemId, ushort count)
        {
            ItemId = itemId;
            Count = count;
        }

        /// <summary>Identificador del objeto; 0 significa vacío.</summary>
        public ushort ItemId { get; set; }

        /// <summary>Unidades de la pila; 0 si el hueco está vacío.</summary>
        public ushort Count { get; set; }
    }
}
