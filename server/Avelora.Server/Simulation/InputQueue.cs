#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Avelora.Shared;

namespace Avelora.Server.Simulation
{
    /// <summary>Resultado de intentar encolar una intención.</summary>
    public enum InputQueueResult
    {
        /// <summary>Aceptada y en cola.</summary>
        Accepted = 0,

        /// <summary>Rechazada: la dirección no es -1, 0 o 1.</summary>
        InvalidDirection = 1,

        /// <summary>Rechazada: la cola está llena.</summary>
        QueueFull = 2,
    }

    /// <summary>
    /// Cola de entrada de intenciones: la frontera entre el hilo de red y el hilo de simulación
    /// (guía del núcleo §2.1).
    /// </summary>
    /// <remarks>
    /// Es una <see cref="ConcurrentQueue{T}"/> sin bloqueos: quien recibe paquetes encola y el
    /// tick vacía. Así el tick nunca espera a la red y no hacen falta <c>lock</c> ni colecciones
    /// compartidas dentro de la simulación.
    ///
    /// La cola tiene tope: si un cliente inunda, se descarta en vez de crecer sin límite. Es la
    /// misma idea que el tope de jugadores o de tamaño de paquete (plan del servidor §7): mejor
    /// perder un input que quedarse sin memoria.
    ///
    /// Aquí se valida la dirección, en la frontera, antes de que nada llegue a la simulación.
    /// </remarks>
    public sealed class InputQueue
    {
        private readonly ConcurrentQueue<MoveIntent> _queue = new ConcurrentQueue<MoveIntent>();
        private readonly int _maxQueued;

        private long _received;
        private long _rejectedInvalid;
        private long _rejectedQueueFull;

        public InputQueue(int maxQueued)
        {
            if (maxQueued < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxQueued), maxQueued, "El tope de la cola debe ser >= 1.");
            }

            _maxQueued = maxQueued;
        }

        /// <summary>Tope de intenciones encoladas a la vez.</summary>
        public int MaxQueued => _maxQueued;

        /// <summary>Intenciones que esperan a ser procesadas.</summary>
        public int Pending => _queue.Count;

        /// <summary>Intenciones aceptadas desde el arranque.</summary>
        public long Received => Interlocked.Read(ref _received);

        /// <summary>Intenciones rechazadas por dirección inválida.</summary>
        public long RejectedInvalidDirection => Interlocked.Read(ref _rejectedInvalid);

        /// <summary>Intenciones rechazadas porque la cola estaba llena.</summary>
        public long RejectedQueueFull => Interlocked.Read(ref _rejectedQueueFull);

        /// <summary>
        /// Intenta encolar una intención. Puede llamarse desde cualquier hilo.
        /// </summary>
        public InputQueueResult TryEnqueue(in MoveIntent intent)
        {
            if (!MovementSimulator.IsValidDirection(intent.DirectionX, intent.DirectionY))
            {
                Interlocked.Increment(ref _rejectedInvalid);
                return InputQueueResult.InvalidDirection;
            }

            if (_queue.Count >= _maxQueued)
            {
                Interlocked.Increment(ref _rejectedQueueFull);
                return InputQueueResult.QueueFull;
            }

            _queue.Enqueue(intent);
            Interlocked.Increment(ref _received);
            return InputQueueResult.Accepted;
        }

        /// <summary>
        /// Saca hasta <paramref name="maxToTake"/> intenciones, en orden de llegada, y las añade a
        /// <paramref name="destination"/> (que se vacía antes). Lo llama solo el hilo de simulación.
        /// </summary>
        public int DrainInto(List<MoveIntent> destination, int maxToTake)
        {
            if (destination is null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            destination.Clear();

            int taken = 0;
            while (taken < maxToTake && _queue.TryDequeue(out MoveIntent intent))
            {
                destination.Add(intent);
                taken++;
            }

            return taken;
        }
    }
}
