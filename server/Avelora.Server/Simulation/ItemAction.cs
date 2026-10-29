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
        AllocatePoints = 4,
        ResetPoints = 5,
        MoveItem = 6,
        DestroyItem = 7,
    }

    /// <summary>Intención validada sintácticamente en la red y pendiente de resolver en el tick.</summary>
    public readonly struct ItemAction
    {
        private ItemAction(
            ItemActionKind kind,
            int playerEntityId,
            int itemEntityId,
            byte slot,
            byte targetSlot,
            ushort count,
            int str,
            int dex,
            int intelligence,
            int vit)
        {
            Kind = kind;
            PlayerEntityId = playerEntityId;
            ItemEntityId = itemEntityId;
            Slot = slot;
            TargetSlot = targetSlot;
            Count = count;
            Str = str;
            Dex = dex;
            Int = intelligence;
            Vit = vit;
        }

        public ItemActionKind Kind { get; }
        public int PlayerEntityId { get; }
        public int ItemEntityId { get; }
        public byte Slot { get; }
        public byte TargetSlot { get; }
        public ushort Count { get; }
        public int Str { get; }
        public int Dex { get; }
        public int Int { get; }
        public int Vit { get; }

        public static ItemAction PickUp(int playerEntityId, int itemEntityId) =>
            new ItemAction(ItemActionKind.PickUp, playerEntityId, itemEntityId, 0, 0, 0, 0, 0, 0, 0);

        public static ItemAction UseItem(int playerEntityId, byte slot) =>
            new ItemAction(ItemActionKind.UseItem, playerEntityId, 0, slot, 0, 0, 0, 0, 0, 0);

        public static ItemAction Equip(int playerEntityId, byte slot) =>
            new ItemAction(ItemActionKind.Equip, playerEntityId, 0, slot, 0, 0, 0, 0, 0, 0);

        public static ItemAction Unequip(int playerEntityId, byte equipSlot, byte targetSlot) =>
            new ItemAction(ItemActionKind.Unequip, playerEntityId, 0, equipSlot, targetSlot, 0, 0, 0, 0, 0);

        public static ItemAction MoveItem(int playerEntityId, byte fromSlot, byte toSlot, ushort count) =>
            new ItemAction(ItemActionKind.MoveItem, playerEntityId, 0, fromSlot, toSlot, count, 0, 0, 0, 0);

        public static ItemAction DestroyItem(int playerEntityId, byte slot, ushort count) =>
            new ItemAction(ItemActionKind.DestroyItem, playerEntityId, 0, slot, 0, count, 0, 0, 0, 0);

        public static ItemAction AllocatePoints(int playerEntityId, int str, int dex, int intelligence, int vit) =>
            new ItemAction(ItemActionKind.AllocatePoints, playerEntityId, 0, 0, 0, 0, str, dex, intelligence, vit);

        public static ItemAction ResetPoints(int playerEntityId) =>
            new ItemAction(ItemActionKind.ResetPoints, playerEntityId, 0, 0, 0, 0, 0, 0, 0, 0);
    }
}
