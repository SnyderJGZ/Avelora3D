#nullable enable
using System;
using System.Collections.Generic;
using Avelora.Server.Entities;
using Avelora.Server.Items;
using Avelora.Server.Progression;

namespace Avelora.Server.Simulation
{
    /// <summary>Resuelve el reparto y el reajuste de atributos dentro del hilo de simulación (I-09).</summary>
    public sealed class StatsSystem
    {
        private readonly LevelTable _levels;
        private readonly ClassCatalog _classes;
        private readonly ItemCatalog? _items;

        public StatsSystem(LevelTable levels, ClassCatalog classes, ItemCatalog? items)
        {
            _levels = levels ?? throw new ArgumentNullException(nameof(levels));
            _classes = classes ?? throw new ArgumentNullException(nameof(classes));
            _items = items;
        }

        public long Allocations { get; private set; }
        public long PointsAllocated { get; private set; }
        public long RejectedAllocateDead { get; private set; }
        public long RejectedAllocateEmpty { get; private set; }
        public long RejectedAllocateExcess { get; private set; }
        public long Resets { get; private set; }
        public long RejectedResetDead { get; private set; }
        public long RejectedResetEmpty { get; private set; }
        public long RejectedResetItem { get; private set; }

        /// <summary>Procesa solo las intenciones de atributos de la cola común de acciones fiables.</summary>
        public void Tick(Zone zone, List<ItemAction> actions)
        {
            for (int i = 0; i < actions.Count; i++)
            {
                ProcessAction(zone, actions[i]);
            }
        }

        /// <summary>Resuelve una intención de atributos en su posición dentro de la cola compartida.</summary>
        public void ProcessAction(Zone zone, in ItemAction action)
        {
            if (action.Kind == ItemActionKind.AllocatePoints)
            {
                Allocate(zone, action);
            }
            else if (action.Kind == ItemActionKind.ResetPoints)
            {
                Reset(zone, action.PlayerEntityId);
            }
        }

        private void Allocate(Zone zone, ItemAction action)
        {
            if (!TryGetLivingPlayer(zone, action.PlayerEntityId, out PlayerEntity player))
            {
                RejectedAllocateDead++;
                return;
            }

            long total = (long)action.Str + action.Dex + action.Int + action.Vit;
            if (action.Str < 0 || action.Dex < 0 || action.Int < 0 || action.Vit < 0)
            {
                RejectedAllocateExcess++;
                return;
            }

            if (total == 0)
            {
                RejectedAllocateEmpty++;
                return;
            }

            int freePoints = CharacterStats.GetFreePoints(
                _levels.PointsPerLevel,
                player.Level,
                player.AllocStr,
                player.AllocDex,
                player.AllocInt,
                player.AllocVit);
            if (total > freePoints)
            {
                RejectedAllocateExcess++;
                return;
            }

            int previousMaxHealth = player.MaxHealth;
            int allocStr = checked(player.AllocStr + action.Str);
            int allocDex = checked(player.AllocDex + action.Dex);
            int allocInt = checked(player.AllocInt + action.Int);
            int allocVit = checked(player.AllocVit + action.Vit);
            DerivedCharacterStats derived = CharacterStats.Calculate(
                _classes, player.ClassId, player.Level, allocStr, allocDex, allocInt, allocVit,
                _items, player.Equipment);

            player.AllocStr = allocStr;
            player.AllocDex = allocDex;
            player.AllocInt = allocInt;
            player.AllocVit = allocVit;
            player.MaxHealth = derived.MaxHealth;
            player.Health = (int)Math.Min(
                derived.MaxHealth,
                (long)player.Health + (derived.MaxHealth - previousMaxHealth));
            player.MaxMana = derived.MaxResource;
            player.Mana = Math.Min(player.Mana, player.MaxMana);

            Allocations++;
            PointsAllocated += total;
            zone.NotifyStatsChanged(player);
        }

        private void Reset(Zone zone, int playerId)
        {
            if (!TryGetLivingPlayer(zone, playerId, out PlayerEntity player))
            {
                RejectedResetDead++;
                return;
            }

            if (player.AllocStr == 0 && player.AllocDex == 0 && player.AllocInt == 0 && player.AllocVit == 0)
            {
                RejectedResetEmpty++;
                return;
            }

            bool consumesStone = player.Level > _levels.FreeResetMaxLevel;
            if (consumesStone && !TryConsumeResetItem(player))
            {
                RejectedResetItem++;
                return;
            }

            DerivedCharacterStats derived = CharacterStats.Calculate(
                _classes, player.ClassId, player.Level, 0, 0, 0, 0, _items, player.Equipment);
            player.AllocStr = 0;
            player.AllocDex = 0;
            player.AllocInt = 0;
            player.AllocVit = 0;
            player.MaxHealth = derived.MaxHealth;
            player.Health = Math.Min(player.Health, player.MaxHealth);
            player.MaxMana = derived.MaxResource;
            player.Mana = Math.Min(player.Mana, player.MaxMana);

            Resets++;
            if (consumesStone)
            {
                zone.NotifyInventoryChanged(player);
            }

            zone.NotifyStatsChanged(player);
        }

        private bool TryConsumeResetItem(PlayerEntity player)
        {
            if (_items?.Find(_levels.ResetItemId) == null)
            {
                return false;
            }

            for (int i = 0; i < player.Inventory.Length; i++)
            {
                InventorySlot slot = player.Inventory[i];
                if (slot.ItemId != _levels.ResetItemId || slot.Count == 0)
                {
                    continue;
                }

                slot.Count--;
                if (slot.Count == 0)
                {
                    slot.ItemId = 0;
                }

                player.Inventory[i] = slot;
                return true;
            }

            return false;
        }

        /// <summary>Recalcula límites al cambiar equipo sin curar al aumentar vida o recurso máximos.</summary>
        public void RecalculateAfterEquipmentChange(PlayerEntity player)
        {
            DerivedCharacterStats derived = CharacterStats.Calculate(
                _classes,
                player.ClassId,
                player.Level,
                player.AllocStr,
                player.AllocDex,
                player.AllocInt,
                player.AllocVit,
                _items,
                player.Equipment);
            player.MaxHealth = derived.MaxHealth;
            player.Health = Math.Min(player.Health, player.MaxHealth);
            player.MaxMana = derived.MaxResource;
            player.Mana = Math.Min(player.Mana, player.MaxMana);
        }

        private static bool TryGetLivingPlayer(Zone zone, int playerId, out PlayerEntity player)
        {
            if (zone.Entities.TryGet(playerId, out Entity? entity) &&
                entity is PlayerEntity candidate && !candidate.IsDead && candidate.Health > 0)
            {
                player = candidate;
                return true;
            }

            player = null!;
            return false;
        }
    }
}
