#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Avelora.Server.Simulation
{
    /// <summary>Cola concurrente y acotada entre red y simulación para acciones de objetos.</summary>
    public sealed class ItemActionQueue
    {
        private readonly ConcurrentQueue<ItemAction> _queue = new ConcurrentQueue<ItemAction>();
        private readonly int _maxQueued;
        private long _rejectedQueueFull;

        public ItemActionQueue(int maxQueued)
        {
            if (maxQueued < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxQueued));
            }

            _maxQueued = maxQueued;
        }

        public int Pending => _queue.Count;
        public int MaxQueued => _maxQueued;
        public long RejectedQueueFull => Interlocked.Read(ref _rejectedQueueFull);

        public bool TryEnqueue(in ItemAction action)
        {
            if (_queue.Count >= _maxQueued)
            {
                Interlocked.Increment(ref _rejectedQueueFull);
                return false;
            }

            _queue.Enqueue(action);
            return true;
        }

        public int DrainInto(List<ItemAction> destination)
        {
            if (destination is null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            destination.Clear();
            while (_queue.TryDequeue(out ItemAction action))
            {
                destination.Add(action);
            }

            return destination.Count;
        }
    }
}
