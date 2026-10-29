#nullable enable

namespace Avelora.Shared.Inventory
{
    /// <summary>Qué pedir al servidor al soltar un objeto arrastrado (I-14b).</summary>
    public enum DragResult : byte
    {
        /// <summary>Nada: mismo hueco, sin objeto, de equipo a equipo o suelto sobre un panel.</summary>
        None = 0,

        /// <summary>Entre huecos del inventario: mover, juntar, intercambiar o dividir.</summary>
        Move = 1,

        /// <summary>Del inventario a su hueco de equipo.</summary>
        Equip = 2,

        /// <summary>Del equipo a un hueco del inventario.</summary>
        Unequip = 3,

        /// <summary>Del inventario a fuera de los paneles: preguntar cuántos destruir (I-21).</summary>
        Destroy = 4,

        /// <summary>Del inventario a un hueco de equipo que no es el suyo.</summary>
        WrongSlot = 5,
    }

    /// <summary>
    /// Reglas del arrastre del cliente, sin Unity para poder probarlas. Un «origen» o «destino» es un
    /// hueco del inventario (0…) o del equipo (<see cref="EquipSource"/> + <see cref="EquipSlot"/>);
    /// -1 es «ningún hueco». El servidor valida todo igualmente.
    /// </summary>
    public static class InventoryDrag
    {
        public const int EquipSource = 100;

        /// <summary>
        /// Decide qué pedir. <paramref name="itemEquipSlot"/> es el hueco de equipo del objeto
        /// arrastrado (255 si no se equipa).
        /// </summary>
        public static DragResult Resolve(int from, int to, bool overPanel, ushort itemId, byte itemEquipSlot)
        {
            if (itemId == 0 || from < 0 || to == from)
            {
                return DragResult.None;
            }

            bool fromEquipment = from >= EquipSource;
            if (to < 0)
            {
                return overPanel || fromEquipment ? DragResult.None : DragResult.Destroy;
            }

            bool toEquipment = to >= EquipSource;
            if (fromEquipment)
            {
                return toEquipment ? DragResult.None : DragResult.Unequip;
            }

            if (!toEquipment)
            {
                return DragResult.Move;
            }

            return itemEquipSlot == to - EquipSource ? DragResult.Equip : DragResult.WrongSlot;
        }

        /// <summary>Cantidad para <c>MoveItem</c>: con Shift, media pila; si no, 0 (toda).</summary>
        public static ushort MoveCount(ushort stackCount, bool half) =>
            half && stackCount > 1 ? (ushort)(stackCount / 2) : (ushort)0;

        /// <summary>Recorta la cantidad elegida a destruir a 1…pila.</summary>
        public static ushort ClampDestroyCount(int requested, ushort stackCount) =>
            (ushort)(requested < 1 ? 1 : requested > stackCount ? stackCount : requested);
    }
}
