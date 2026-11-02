#nullable enable
using System;
using Avelora.Shared.Protocol;

namespace Avelora.Server.Entities
{
    /// <summary>Estado temporal activo de una entidad (I-18).</summary>
    public struct ActiveStatus
    {
        public ushort SkillId;
        public int SourceId;
        public int Amount;
        public long ExpiresAtTick;
    }

    /// <summary>Estados temporales de una entidad: uno por tipo, como mucho cuatro (I-18).</summary>
    public sealed class StatusEffects
    {
        private readonly ActiveStatus[] _slots = new ActiveStatus[ProtocolConstants.MaxStatusEffects];

        /// <summary>Versión que aumenta cuando cambia el estado visible al dueño.</summary>
        public int Version { get; private set; }

        /// <summary>Indica si el tipo tiene un estado activo.</summary>
        public bool IsActive(StatusKind kind)
        {
            if (!TryIndex(kind, out int index)) return false;
            return _slots[index].ExpiresAtTick > 0;
        }

        /// <summary>Obtiene el estado del tipo indicado.</summary>
        public ActiveStatus Get(StatusKind kind)
        {
            if (!TryIndex(kind, out int index))
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "El tipo de estado debe estar entre 1 y 4.");
            return _slots[index];
        }

        /// <summary>Máscara con un bit por cada tipo activo.</summary>
        public byte Mask
        {
            get
            {
                byte mask = 0;
                for (int i = 0; i < _slots.Length; i++)
                    if (_slots[i].ExpiresAtTick > 0) mask |= (byte)(1 << i);
                return mask;
            }
        }

        /// <summary>Porcentaje de velocidad efectivo.</summary>
        public int SpeedPercent => IsActive(StatusKind.Slow) ? 100 - _slots[(int)StatusKind.Slow - 1].Amount : 100;

        /// <summary>Bonus temporal de daño y defensa.</summary>
        public int Bonus => IsActive(StatusKind.Bonus) ? _slots[(int)StatusKind.Bonus - 1].Amount : 0;

        /// <summary>Aplica o refresca el tipo si no es más débil que el activo.</summary>
        public bool Apply(StatusKind kind, ushort skillId, int sourceId, int amount, long expiresAtTick)
        {
            if (!TryIndex(kind, out int index))
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "El tipo de estado debe estar entre 1 y 4.");
            if (expiresAtTick <= 0)
                throw new ArgumentOutOfRangeException(nameof(expiresAtTick), expiresAtTick, "La caducidad debe ser positiva.");

            ActiveStatus current = _slots[index];
            if (current.ExpiresAtTick > 0 && amount < current.Amount) return false;
            _slots[index] = new ActiveStatus
            {
                SkillId = skillId,
                SourceId = sourceId,
                Amount = amount,
                ExpiresAtTick = expiresAtTick,
            };
            Version++;
            return true;
        }

        /// <summary>Absorbe daño con el escudo y lo elimina al agotarse.</summary>
        public int Absorb(int damage)
        {
            if (!IsActive(StatusKind.Shield) || damage <= 0) return 0;
            int index = (int)StatusKind.Shield - 1;
            ActiveStatus shield = _slots[index];
            int taken = Math.Min(damage, shield.Amount);
            shield.Amount -= taken;
            if (shield.Amount == 0) _slots[index] = default;
            else _slots[index] = shield;
            Version++;
            return taken;
        }

        /// <summary>Quita un tipo de estado. Devuelve si había uno activo.</summary>
        public bool Remove(StatusKind kind)
        {
            if (!TryIndex(kind, out int index))
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "El tipo de estado debe estar entre 1 y 4.");
            if (_slots[index].ExpiresAtTick <= 0) return false;
            _slots[index] = default;
            Version++;
            return true;
        }

        /// <summary>Quita todos los estados caducados en este tick.</summary>
        public int Expire(long tick)
        {
            int removed = 0;
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].ExpiresAtTick > 0 && tick >= _slots[i].ExpiresAtTick)
                {
                    _slots[i] = default;
                    removed++;
                }
            }
            if (removed > 0) Version++;
            return removed;
        }

        /// <summary>Quita todos los estados activos.</summary>
        public int Clear()
        {
            int removed = 0;
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].ExpiresAtTick <= 0) continue;
                _slots[i] = default;
                removed++;
            }
            if (removed > 0) Version++;
            return removed;
        }

        private static bool TryIndex(StatusKind kind, out int index)
        {
            index = (int)kind - 1;
            return index >= 0 && index < ProtocolConstants.MaxStatusEffects;
        }
    }
}
