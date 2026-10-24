#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Avelora.Server.Simulation
{
    /// <summary>
    /// Cola de ataques: la frontera entre el hilo de red y el hilo de simulación para el combate
    /// (I-02). Es el mismo patrón que <see cref="InputQueue"/>.
    /// </summary>
    /// <remarks>
    /// Sin bloqueos: la red encola y el tick vacía. Tiene tope para que un cliente que inunde de
    /// ataques pierda mensajes en vez de hacer crecer la memoria sin límite (plan del servidor §7).
    /// </remarks>
    public sealed class AttackQueue
    {
        private readonly ConcurrentQueue<AttackIntent> _queue = new ConcurrentQueue<AttackIntent>();
        private readonly int _maxQueued;

        private long _received;
        private long _rejectedQueueFull;

        public AttackQueue(int maxQueued)
        {
            if (maxQueued < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxQueued),
                    maxQueued,
                    "El tope de la cola de ataques debe ser >= 1.");
            }

            _maxQueued = maxQueued;
        }

        /// <summary>Tope de ataques encolados a la vez.</summary>
        public int MaxQueued => _maxQueued;

        /// <summary>Ataques que esperan a ser procesados.</summary>
        public int Pending => _queue.Count;

        /// <summary>Ataques aceptados desde el arranque.</summary>
        public long Received => Interlocked.Read(ref _received);

        /// <summary>Ataques descartados porque la cola estaba llena.</summary>
        public long RejectedQueueFull => Interlocked.Read(ref _rejectedQueueFull);

        /// <summary>Intenta encolar un ataque. Puede llamarse desde cualquier hilo.</summary>
        /// <returns><c>true</c> si quedó en la cola; <c>false</c> si se descartó por llena.</returns>
        public bool TryEnqueue(in AttackIntent intent)
        {
            if (_queue.Count >= _maxQueued)
            {
                Interlocked.Increment(ref _rejectedQueueFull);
                return false;
            }

            _queue.Enqueue(intent);
            Interlocked.Increment(ref _received);
            return true;
        }

        /// <summary>
        /// Saca todos los ataques pendientes a <paramref name="destination"/> (que se vacía antes) y
        /// devuelve cuántos sacó. Lo llama solo el hilo de simulación.
        /// </summary>
        public int DrainInto(List<AttackIntent> destination)
        {
            if (destination is null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            destination.Clear();

            while (_queue.TryDequeue(out AttackIntent intent))
            {
                destination.Add(intent);
            }

            return destination.Count;
        }
    }
}
