#nullable enable
using System;
using System.Collections.Generic;
using Avelora.Server.Entities;
using Avelora.Server.Items;
using Avelora.Server.Npcs;
using Avelora.Shared;

namespace Avelora.Server.Simulation
{
        /// <summary>Resuelve botín, inventario, consumibles y equipo (I-04/I-05/I-14).</summary>
    public sealed class ItemSystem
    {
        private readonly ItemCatalog _catalog;
        private readonly Random _random;
        private readonly int _maxGroundItems;
        private readonly long _groundItemTicks;
        private readonly float _pickupRange;
        private readonly long _potionCooldownTicks;
        private readonly List<GroundItemEntity> _expired = new List<GroundItemEntity>(32);
        private int _groundItemCount;

        public ItemSystem(
            ItemCatalog catalog,
            Random random,
            int maxGroundItems,
            long groundItemTicks,
            float pickupRange,
            float rangeTolerance,
            long potionCooldownTicks)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _random = random ?? throw new ArgumentNullException(nameof(random));
            _maxGroundItems = maxGroundItems;
            _groundItemTicks = groundItemTicks;
            _pickupRange = pickupRange + rangeTolerance;
            _potionCooldownTicks = potionCooldownTicks;
        }

        public long ItemsDropped { get; private set; }
        public long ItemsPickedUp { get; private set; }
        public long ItemsExpired { get; private set; }
        public long ItemsDiscarded { get; private set; }
        public long RejectedPickupDead { get; private set; }
        public long RejectedPickupItem { get; private set; }
        public long RejectedPickupDistance { get; private set; }
        public long RejectedPickupFull { get; private set; }
        public long RejectedUseDead { get; private set; }
        public long RejectedUseItem { get; private set; }
        public long RejectedUseFull { get; private set; }
        public long RejectedUseCooldown { get; private set; }
        public long PotionsUsed { get; private set; }
        public long Equipped { get; private set; }
        public long Unequipped { get; private set; }
        public long RejectedEquipDead { get; private set; }
        public long RejectedEquipItem { get; private set; }
        public long RejectedEquipFull { get; private set; }
        public long RejectedEquipEmpty { get; private set; }
        public long EquipmentSwaps { get; private set; }
        public long Moved { get; private set; }
        public long Split { get; private set; }
        public long Merged { get; private set; }
        public long Swapped { get; private set; }
        public long DestroyedFromInventory { get; private set; }
        public long RejectedEquipmentDestination { get; private set; }
        public long RejectedEquipmentSlot { get; private set; }
        public long RejectedUnequipEmpty { get; private set; }
        public long RejectedMoveDead { get; private set; }
        public long RejectedMoveSlot { get; private set; }
        public long RejectedMoveSameSlot { get; private set; }
        public long RejectedMoveEmpty { get; private set; }
        public long RejectedMoveCount { get; private set; }
        public long RejectedMoveItem { get; private set; }
        public long RejectedMoveFull { get; private set; }
        public long RejectedDestroyDead { get; private set; }
        public long RejectedDestroySlot { get; private set; }
        public long RejectedDestroyEmpty { get; private set; }
        public long RejectedDestroyCount { get; private set; }
        public long RejectedDestroyItem { get; private set; }
        public int GroundItemCount => _groundItemCount;

        /// <summary>Procesa caducidades y acciones en el orden en que llegaron.</summary>
        public void Tick(Zone zone, List<ItemAction> actions, long tick, StatsSystem? stats = null)
        {
            _expired.Clear();
            for (int i = 0; i < zone.Entities.Count; i++)
            {
                if (zone.Entities[i] is GroundItemEntity item && tick >= item.DespawnTick)
                {
                    _expired.Add(item);
                }
            }

            for (int i = 0; i < _expired.Count; i++)
            {
                if (zone.RemoveEntity(_expired[i].Id))
                {
                    _groundItemCount--;
                    ItemsExpired++;
                }
            }

            for (int i = 0; i < actions.Count; i++)
            {
                ItemAction action = actions[i];
                if (action.Kind == ItemActionKind.PickUp)
                {
                    ResolvePickup(zone, action.PlayerEntityId, action.ItemEntityId);
                }
                else if (action.Kind == ItemActionKind.UseItem)
                {
                    ResolveUseItem(zone, action.PlayerEntityId, action.Slot, tick);
                }
                else if (action.Kind == ItemActionKind.Equip)
                {
                    ResolveEquip(zone, action.PlayerEntityId, action.Slot, stats);
                }
                else if (action.Kind == ItemActionKind.Unequip)
                {
                    ResolveUnequip(zone, action.PlayerEntityId, action.Slot, action.TargetSlot, stats);
                }
                else if (action.Kind == ItemActionKind.AllocatePoints || action.Kind == ItemActionKind.ResetPoints)
                {
                    stats?.ProcessAction(zone, action);
                }
                else if (action.Kind == ItemActionKind.MoveItem)
                {
                    ResolveMoveItem(zone, action.PlayerEntityId, action.Slot, action.TargetSlot, action.Count);
                }
                else if (action.Kind == ItemActionKind.DestroyItem)
                {
                    ResolveDestroyItem(zone, action.PlayerEntityId, action.Slot, action.Count);
                }
            }
        }

        /// <summary>Tira el botín de un enemigo cuando su muerte se registra en este tick.</summary>
        public void DropLoot(Zone zone, NpcEntity npc, long tick)
        {
            if (zone.Npcs?.FindType(npc.TypeId) is not NpcType type || type.Loot == null)
            {
                return;
            }

            for (int roll = 0; roll < npc.LootRolls; roll++)
            {
                for (int i = 0; i < type.Loot.Count; i++)
                {
                    LootEntry loot = type.Loot[i];
                    if (_random.NextDouble() >= loot.Chance)
                    {
                        continue;
                    }

                    if (_groundItemCount >= _maxGroundItems)
                    {
                        ItemsDiscarded++;
                        continue;
                    }

                    int count = _random.Next(loot.Min, loot.Max + 1);
                    var item = new GroundItemEntity(
                        zone.Ids.Allocate(),
                        loot.ItemId,
                        count,
                        tick + _groundItemTicks)
                    {
                        MapId = zone.Id,
                        Position = new Vec2(
                            npc.Position.X + ((float)_random.NextDouble() * 0.8f) - 0.4f,
                            npc.Position.Y + ((float)_random.NextDouble() * 0.8f) - 0.4f),
                    };

                    zone.AddEntity(item);
                    _groundItemCount++;
                    ItemsDropped++;
                }
            }
        }

        private void ResolvePickup(Zone zone, int playerId, int itemId)
        {
            if (!zone.Entities.TryGet(playerId, out Entity? playerEntity) ||
                playerEntity is not PlayerEntity player || player.IsDead || player.Health <= 0)
            {
                RejectedPickupDead++;
                return;
            }

            if (!zone.Entities.TryGet(itemId, out Entity? itemEntity) || itemEntity is not GroundItemEntity item)
            {
                RejectedPickupItem++;
                return;
            }

            float dx = player.Position.X - item.Position.X;
            float dy = player.Position.Y - item.Position.Y;
            if ((dx * dx) + (dy * dy) > _pickupRange * _pickupRange)
            {
                RejectedPickupDistance++;
                return;
            }

            ItemType? itemType = _catalog.Find(item.ItemId);
            if (itemType == null || !CanFit(player.Inventory, item.ItemId, item.Count, itemType.MaxStack))
            {
                RejectedPickupFull++;
                return;
            }

            AddToInventory(player.Inventory, item.ItemId, item.Count, itemType.MaxStack);
            zone.RemoveEntity(item.Id);
            _groundItemCount--;
            ItemsPickedUp++;
            zone.NotifyInventoryChanged(player);
        }

        private void ResolveUseItem(Zone zone, int playerId, byte slotIndex, long tick)
        {
            if (!zone.Entities.TryGet(playerId, out Entity? playerEntity) ||
                playerEntity is not PlayerEntity player || player.IsDead || player.Health <= 0)
            {
                RejectedUseDead++;
                return;
            }

            if (slotIndex >= player.Inventory.Length || player.Inventory[slotIndex].ItemId == 0 ||
                _catalog.Find(player.Inventory[slotIndex].ItemId) is not ItemType item || !item.IsPotion)
            {
                RejectedUseItem++;
                return;
            }

            if (player.Health >= player.MaxHealth)
            {
                RejectedUseFull++;
                return;
            }

            if (player.LastPotionTick >= 0 && tick - player.LastPotionTick < _potionCooldownTicks)
            {
                RejectedUseCooldown++;
                return;
            }

            player.Health = Math.Min(player.MaxHealth, player.Health + item.Heal);
            player.LastPotionTick = tick;
            InventorySlot slot = player.Inventory[slotIndex];
            slot.Count--;
            if (slot.Count == 0)
            {
                slot.ItemId = 0;
            }

            player.Inventory[slotIndex] = slot;
            PotionsUsed++;
            zone.NotifyInventoryChanged(player);
        }

        private void ResolveEquip(Zone zone, int playerId, byte slotIndex, StatsSystem? stats)
        {
            if (!TryGetLivingPlayer(zone, playerId, out PlayerEntity player))
            {
                RejectedEquipDead++;
                return;
            }

            if (slotIndex >= player.Inventory.Length)
            {
                RejectedEquipEmpty++;
                return;
            }

            InventorySlot slot = player.Inventory[slotIndex];
            if (slot.ItemId == 0 || slot.Count == 0)
            {
                RejectedEquipEmpty++;
                return;
            }

            ItemType? item = _catalog.Find(slot.ItemId);
            if (item == null || !item.TryGetEquipSlot(out byte equipSlot) || slot.Count != 1)
            {
                RejectedEquipItem++;
                return;
            }

            ushort previousItemId = player.Equipment[equipSlot];
            player.Equipment[equipSlot] = slot.ItemId;
            player.Inventory[slotIndex] = previousItemId == 0
                ? new InventorySlot(0, 0)
                : new InventorySlot(previousItemId, 1);
            Equipped++;
            if (previousItemId != 0)
            {
                EquipmentSwaps++;
            }

            stats?.RecalculateAfterEquipmentChange(player);
            zone.NotifyInventoryChanged(player);
            zone.NotifyStatsChanged(player);
        }

        private void ResolveUnequip(Zone zone, int playerId, byte equipSlot, byte targetSlot, StatsSystem? stats)
        {
            if (!TryGetLivingPlayer(zone, playerId, out PlayerEntity player))
            {
                RejectedEquipDead++;
                return;
            }

            if (equipSlot >= player.Equipment.Length)
            {
                RejectedEquipmentSlot++;
                return;
            }

            ushort equippedItemId = player.Equipment[equipSlot];
            if (equippedItemId == 0)
            {
                RejectedUnequipEmpty++;
                return;
            }

            int destination = targetSlot;
            if (targetSlot == byte.MaxValue)
            {
                destination = FindFirstEmptySlot(player.Inventory);
                if (destination < 0)
                {
                    RejectedEquipFull++;
                    return;
                }
            }
            else if (targetSlot >= player.Inventory.Length)
            {
                RejectedEquipmentDestination++;
                return;
            }

            InventorySlot target = player.Inventory[destination];
            if (target.ItemId == 0)
            {
                player.Inventory[destination] = new InventorySlot(equippedItemId, 1);
                player.Equipment[equipSlot] = 0;
            }
            else
            {
                ItemType? targetType = _catalog.Find(target.ItemId);
                if (target.Count != 1 || targetType == null ||
                    !targetType.TryGetEquipSlot(out byte targetEquipSlot) || targetEquipSlot != equipSlot)
                {
                    RejectedEquipmentDestination++;
                    return;
                }

                player.Equipment[equipSlot] = target.ItemId;
                player.Inventory[destination] = new InventorySlot(equippedItemId, 1);
                EquipmentSwaps++;
            }

            Unequipped++;
            stats?.RecalculateAfterEquipmentChange(player);
            zone.NotifyInventoryChanged(player);
            zone.NotifyStatsChanged(player);
        }

        private void ResolveMoveItem(Zone zone, int playerId, byte fromSlot, byte toSlot, ushort requestedCount)
        {
            if (!TryGetLivingPlayer(zone, playerId, out PlayerEntity player))
            {
                RejectedMoveDead++;
                return;
            }

            if (fromSlot >= player.Inventory.Length || toSlot >= player.Inventory.Length)
            {
                RejectedMoveSlot++;
                return;
            }

            if (fromSlot == toSlot)
            {
                RejectedMoveSameSlot++;
                return;
            }

            InventorySlot source = player.Inventory[fromSlot];
            InventorySlot destination = player.Inventory[toSlot];
            if (source.ItemId == 0 || source.Count == 0)
            {
                RejectedMoveEmpty++;
                return;
            }

            ItemType? sourceType = _catalog.Find(source.ItemId);
            if (sourceType == null || source.Count > sourceType.MaxStack)
            {
                RejectedMoveItem++;
                return;
            }

            int count = requestedCount == 0 ? source.Count : requestedCount;
            if (count > source.Count)
            {
                RejectedMoveCount++;
                return;
            }

            if (destination.ItemId == 0)
            {
                destination = new InventorySlot(source.ItemId, (ushort)count);
                source.Count = (ushort)(source.Count - count);
                if (source.Count == 0) source.ItemId = 0;
                player.Inventory[fromSlot] = source;
                player.Inventory[toSlot] = destination;
                if (count < source.Count + count)
                {
                    Split++;
                }
            }
            else if (destination.ItemId == source.ItemId)
            {
                if (destination.Count > sourceType.MaxStack)
                {
                    RejectedMoveItem++;
                    return;
                }

                int transferred = Math.Min(count, sourceType.MaxStack - destination.Count);
                if (transferred <= 0)
                {
                    RejectedMoveFull++;
                    return;
                }

                source.Count = (ushort)(source.Count - transferred);
                if (source.Count == 0) source.ItemId = 0;
                destination.Count = (ushort)(destination.Count + transferred);
                player.Inventory[fromSlot] = source;
                player.Inventory[toSlot] = destination;
                Merged++;
            }
            else
            {
                if (requestedCount != 0 && requestedCount != source.Count)
                {
                    RejectedMoveCount++;
                    return;
                }

                ItemType? destinationType = _catalog.Find(destination.ItemId);
                if (destinationType == null || destination.Count == 0 || destination.Count > destinationType.MaxStack)
                {
                    RejectedMoveItem++;
                    return;
                }

                player.Inventory[fromSlot] = destination;
                player.Inventory[toSlot] = source;
                Swapped++;
            }

            Moved++;
            zone.NotifyInventoryChanged(player);
        }

        private void ResolveDestroyItem(Zone zone, int playerId, byte slotIndex, ushort requestedCount)
        {
            if (!TryGetLivingPlayer(zone, playerId, out PlayerEntity player))
            {
                RejectedDestroyDead++;
                return;
            }

            if (slotIndex >= player.Inventory.Length)
            {
                RejectedDestroySlot++;
                return;
            }

            InventorySlot source = player.Inventory[slotIndex];
            if (source.ItemId == 0 || source.Count == 0)
            {
                RejectedDestroyEmpty++;
                return;
            }

            ItemType? item = _catalog.Find(source.ItemId);
            if (item == null || source.Count > item.MaxStack)
            {
                RejectedDestroyItem++;
                return;
            }

            int count = requestedCount == 0 ? source.Count : requestedCount;
            if (count > source.Count)
            {
                RejectedDestroyCount++;
                return;
            }

            source.Count = (ushort)(source.Count - count);
            if (source.Count == 0) source.ItemId = 0;
            player.Inventory[slotIndex] = source;
            DestroyedFromInventory++;
            zone.NotifyInventoryChanged(player);
        }

        private static int FindFirstEmptySlot(InventorySlot[] inventory)
        {
            for (int i = 0; i < inventory.Length; i++)
            {
                if (inventory[i].ItemId == 0 && inventory[i].Count == 0)
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool TryGetLivingPlayer(Zone zone, int playerId, out PlayerEntity player)
        {
            if (zone.Entities.TryGet(playerId, out Entity? entity) && entity is PlayerEntity candidate &&
                !candidate.IsDead && candidate.Health > 0)
            {
                player = candidate;
                return true;
            }

            player = null!;
            return false;
        }

        private static bool CanFit(InventorySlot[] inventory, int itemId, int count, int maxStack)
        {
            int remaining = count;
            for (int i = 0; i < inventory.Length; i++)
            {
                if (inventory[i].ItemId == itemId)
                {
                    remaining -= Math.Max(0, maxStack - inventory[i].Count);
                }
            }

            for (int i = 0; i < inventory.Length; i++)
            {
                if (inventory[i].ItemId == 0)
                {
                    remaining -= maxStack;
                }
            }

            return remaining <= 0;
        }

        private static void AddToInventory(InventorySlot[] inventory, int itemId, int count, int maxStack)
        {
            int remaining = count;
            for (int i = 0; i < inventory.Length && remaining > 0; i++)
            {
                if (inventory[i].ItemId != itemId || inventory[i].Count >= maxStack)
                {
                    continue;
                }

                InventorySlot slot = inventory[i];
                int added = Math.Min(remaining, maxStack - slot.Count);
                slot.Count = (ushort)(slot.Count + added);
                inventory[i] = slot;
                remaining -= added;
            }

            for (int i = 0; i < inventory.Length && remaining > 0; i++)
            {
                if (inventory[i].ItemId != 0)
                {
                    continue;
                }

                int added = Math.Min(remaining, maxStack);
                inventory[i] = new InventorySlot((ushort)itemId, (ushort)added);
                remaining -= added;
            }
        }
    }
}
