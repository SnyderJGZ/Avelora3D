#nullable enable
using System;
using System.Collections.Generic;
using Avelora.Server.Entities;
using Avelora.Shared;
using Avelora.Shared.Protocol;

namespace Avelora.Server.Simulation
{
    /// <summary>Resuelve el modo PvP elegido, el forzado y sus temporizadores (I-15p).</summary>
    public sealed class PvpSystem
    {
        private readonly int _modeChangeTicks;
        private readonly int _forcedTicks;

        public PvpSystem(int modeChangeTicks, int forcedTicks)
        {
            if (modeChangeTicks < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(modeChangeTicks));
            }

            if (forcedTicks < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(forcedTicks));
            }

            _modeChangeTicks = modeChangeTicks;
            _forcedTicks = forcedTicks;
        }

        public event Action<PlayerEntity, PvpStatusMessage>? StatusChanged;
        public event Action<PlayerEntity, PvpModeRejectReason>? ModeRejected;

        public long ModeChanges { get; private set; }
        public long ModeUnchanged { get; private set; }
        public long RejectedNotInSafeZone { get; private set; }
        public long RejectedTooSoon { get; private set; }
        public long StatusMessages { get; private set; }

        public void Tick(EntityStore entities, List<ItemAction> actions, bool sendStatus)
        {
            if (entities is null)
            {
                throw new ArgumentNullException(nameof(entities));
            }

            if (actions is null)
            {
                throw new ArgumentNullException(nameof(actions));
            }

            for (int i = 0; i < entities.Count; i++)
            {
                if (!(entities[i] is PlayerEntity player))
                {
                    continue;
                }

                if (player.ZoneKind == ZoneKind.Pvp)
                {
                    player.PvpForcedTicksLeft = _forcedTicks;
                }
                else if (player.PvpForcedTicksLeft > 0)
                {
                    player.PvpForcedTicksLeft--;
                }

                if (player.ZoneKind == ZoneKind.Safe)
                {
                    if (player.SafeZoneTicks < _modeChangeTicks)
                    {
                        player.SafeZoneTicks++;
                    }
                }
                else
                {
                    player.SafeZoneTicks = 0;
                }
            }

            for (int i = 0; i < actions.Count; i++)
            {
                ItemAction action = actions[i];
                if (action.Kind != ItemActionKind.SetPvpMode ||
                    !entities.TryGet(action.PlayerEntityId, out Entity? entity) ||
                    !(entity is PlayerEntity player) || action.Slot > (byte)PvpMode.Pvp)
                {
                    continue;
                }

                PvpMode mode = (PvpMode)action.Slot;
                if (mode == player.PvpMode)
                {
                    ModeUnchanged++;
                    continue;
                }

                if (player.ZoneKind != ZoneKind.Safe)
                {
                    RejectedNotInSafeZone++;
                    ModeRejected?.Invoke(player, PvpModeRejectReason.NotInSafeZone);
                    continue;
                }

                if (player.SafeZoneTicks < _modeChangeTicks)
                {
                    RejectedTooSoon++;
                    ModeRejected?.Invoke(player, PvpModeRejectReason.TooSoon);
                    continue;
                }

                player.PvpMode = mode;
                ModeChanges++;
            }

            if (!sendStatus)
            {
                return;
            }

            for (int i = 0; i < entities.Count; i++)
            {
                if (!(entities[i] is PlayerEntity player))
                {
                    continue;
                }

                PvpForcedState forced = player.ZoneKind == ZoneKind.Pvp
                    ? PvpForcedState.InZone
                    : player.PvpForcedTicksLeft > 0 ? PvpForcedState.Timer : PvpForcedState.None;
                PvpChangeState change = player.ZoneKind != ZoneKind.Safe
                    ? PvpChangeState.Unavailable
                    : player.SafeZoneTicks >= _modeChangeTicks ? PvpChangeState.Ready : PvpChangeState.Waiting;
                byte key = (byte)((byte)player.PvpMode | ((byte)forced << 1) | ((byte)change << 3));
                if (key == player.PvpStatusKey)
                {
                    continue;
                }

                player.PvpStatusKey = key;
                StatusMessages++;
                StatusChanged?.Invoke(player, BuildStatus(player, _modeChangeTicks));
            }
        }

        public static PvpStatusMessage BuildStatus(PlayerEntity player, int modeChangeTicks)
        {
            PvpForcedState forced = player.ZoneKind == ZoneKind.Pvp
                ? PvpForcedState.InZone
                : player.PvpForcedTicksLeft > 0 ? PvpForcedState.Timer : PvpForcedState.None;
            PvpChangeState change = player.ZoneKind != ZoneKind.Safe
                ? PvpChangeState.Unavailable
                : player.SafeZoneTicks >= modeChangeTicks ? PvpChangeState.Ready : PvpChangeState.Waiting;
            ushort forcedSeconds = forced == PvpForcedState.Timer
                ? ToSeconds(player.PvpForcedTicksLeft)
                : (ushort)0;
            ushort changeSeconds = change == PvpChangeState.Waiting
                ? ToSeconds(Math.Max(0, modeChangeTicks - player.SafeZoneTicks))
                : (ushort)0;
            return new PvpStatusMessage(player.PvpMode, forced, change, forcedSeconds, changeSeconds);
        }

        private static ushort ToSeconds(int ticks)
        {
            long seconds = (ticks + (long)SimulationConstants.TickRate - 1) / SimulationConstants.TickRate;
            return (ushort)Math.Min(seconds, ushort.MaxValue);
        }
    }
}
