#nullable enable
namespace Avelora.Server.Simulation
{
    /// <summary>Tipo de acción de objeto que llega desde una conexión.</summary>
    public enum ItemActionKind : byte
    {
        PickUp = 0,
        UseItem = 1,
        Equip = 2,
        Unequip = 3,
    }

    /// <summary>Intención validada sintácticamente en la red y pendiente de resolver en el tick.</summary>
    public readonly struct ItemAction
    {
        private ItemAction(ItemActionKind kind, int playerEntityId, int itemEntityId, byte slot)
        {
            Kind = kind;
            PlayerEntityId = playerEntityId;
            ItemEntityId = itemEntityId;
            Slot = slot;
        }

        public ItemActionKind Kind { get; }
        public int PlayerEntityId { get; }
        public int ItemEntityId { get; }
        public byte Slot { get; }

        public static ItemAction PickUp(int playerEntityId, int itemEntityId) =>
            new ItemAction(ItemActionKind.PickUp, playerEntityId, itemEntityId, 0);

        public static ItemAction UseItem(int playerEntityId, byte slot) =>
            new ItemAction(ItemActionKind.UseItem, playerEntityId, 0, slot);

        public static ItemAction Equip(int playerEntityId, byte slot) =>
            new ItemAction(ItemActionKind.Equip, playerEntityId, 0, slot);

        public static ItemAction Unequip(int playerEntityId) =>
            new ItemAction(ItemActionKind.Unequip, playerEntityId, 0, 0);
    }
}
