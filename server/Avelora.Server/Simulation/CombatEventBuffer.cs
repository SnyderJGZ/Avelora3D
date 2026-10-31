#nullable enable
using System;
using Avelora.Shared.Protocol;

namespace Avelora.Server.Simulation
{
    /// <summary>Búfer reutilizable de eventos de combate generados durante los ticks.</summary>
    public sealed class CombatEventBuffer
    {
        public const int Capacity = 256;

        private readonly CombatEventEntry[] _entries = new CombatEventEntry[Capacity];

        public int Count { get; private set; }

        public long Dropped { get; private set; }

        public CombatEventEntry this[int index] =>
            index >= 0 && index < Count
                ? _entries[index]
                : throw new ArgumentOutOfRangeException(nameof(index));

        public void Add(in CombatEventEntry entry)
        {
            if (Count == Capacity)
            {
                Dropped++;
                return;
            }

            _entries[Count++] = entry;
        }

        public void Clear() => Count = 0;
    }
}
