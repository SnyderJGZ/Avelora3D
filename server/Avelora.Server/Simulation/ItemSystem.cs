#nullable enable
using System;
using System.Collections.Generic;
using Avelora.Server.Entities;
using Avelora.Server.Items;
using Avelora.Server.Npcs;
using Avelora.Shared;

namespace Avelora.Server.Simulation
{
    /// <summary>Resuelve botín, inventario, pociones y equipo de armas (I-04/I-05).</summary>
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
                    ResolveEquip(zone, action.PlayerEntityId, action.Slot);
                }
                else if (action.Kind == ItemActionKind.Unequip)
                {
                    ResolveUnequip(zone, action.PlayerEntityId);
                }
                else if (action.Kind == ItemActionKind.AllocatePoints || action.Kind == ItemActionKind.ResetPoints)
                {
                    stats?.ProcessAction(zone, action);
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

        private void ResolveEquip(Zone zone, int playerId, byte slotIndex)
        {
            if (!zone.Entities.TryGet(playerId, out Entity? playerEntity) ||
                playerEntity is not PlayerEntity player || player.IsDead || player.Health <= 0)
            {
                RejectedEquipDead++;
                return;
            }

            if (slotIndex >= player.Inventory.Length ||
                player.Inventory[slotIndex].ItemId == 0 ||
                _catalog.Find(player.Inventory[slotIndex].ItemId) is not ItemType weapon ||
                !weapon.IsWeapon)
            {
                RejectedEquipItem++;
                return;
            }

            InventorySlot slot = player.Inventory[slotIndex];
            ushort previousWeaponId = player.EquippedWeaponId;
            player.EquippedWeaponId = slot.ItemId;
            player.Inventory[slotIndex] = previousWeaponId == 0
                ? new InventorySlot(0, 0)
                : new InventorySlot(previousWeaponId, 1);
            Equipped++;
            zone.NotifyInventoryChanged(player);
            zone.NotifyStatsChanged(player);
        }

        private void ResolveUnequip(Zone zone, int playerId)
        {
            if (!zone.Entities.TryGet(playerId, out Entity? playerEntity) || playerEntity is not PlayerEntity player)
            {
                return;
            }

            if (player.EquippedWeaponId == 0)
            {
                RejectedEquipEmpty++;
                return;
            }

            int freeSlot = -1;
            for (int i = 0; i < player.Inventory.Length; i++)
            {
                if (player.Inventory[i].ItemId == 0)
                {
                    freeSlot = i;
                    break;
                }
            }

            if (freeSlot < 0)
            {
                RejectedEquipFull++;
                return;
            }

            player.Inventory[freeSlot] = new InventorySlot(player.EquippedWeaponId, 1);
            player.EquippedWeaponId = 0;
            Unequipped++;
            zone.NotifyInventoryChanged(player);
            zone.NotifyStatsChanged(player);
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
