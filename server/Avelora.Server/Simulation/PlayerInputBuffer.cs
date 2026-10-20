#nullable enable
using System;
using Avelora.Shared;

namespace Avelora.Server.Simulation
{
    /// <summary>Resultado de aceptar un input en la cola de un jugador.</summary>
    public enum InputAcceptResult
    {
        /// <summary>Aceptado.</summary>
        Accepted = 0,

        /// <summary>Descartado: la secuencia es igual o más vieja que la última aceptada (duplicado o desordenado).</summary>
        OutOfOrder = 1,

        /// <summary>Descartado: la cola del jugador ya estaba llena.</summary>
        BufferFull = 2,
    }

    /// <summary>
    /// Cola de inputs de un jugador: tamaño fijo, FIFO y sin reservas de memoria.
    /// </summary>
    /// <remarks>
    /// El servidor aplica <b>un input por tick</b>: es lo que hace que un input del cliente
    /// equivalga a un paso en el servidor y que la reconciliación de la Etapa D encaje sin trucos
    /// (guía del núcleo §5.3). Por eso hace falta un margen pequeño: si los paquetes llegan
    /// agrupados por el jitter de la red, el segundo input espera al tick siguiente en vez de
    /// perderse. Pasado ese margen, el exceso se descarta —un cliente no puede mover a su personaje
    /// más rápido inundando con inputs (guía del núcleo §5.5)—.
    ///
    /// Los repetidos y los que llegan fuera de orden se rechazan comparando con la <b>última
    /// secuencia aceptada</b> (no con la última procesada). Así la cola siempre avanza hacia
    /// adelante y su orden de salida coincide con el orden en que el cliente generó los inputs.
    /// </remarks>
    public sealed class PlayerInputBuffer
    {
        private readonly MoveIntent[] _items;
        private int _head;
        private int _count;

        public PlayerInputBuffer(int capacity)
        {
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "La capacidad debe ser >= 1.");
            }

            _items = new MoveIntent[capacity];
        }

        /// <summary>Cuántos inputs caben.</summary>
        public int Capacity => _items.Length;

        /// <summary>Cuántos inputs esperan a ser aplicados.</summary>
        public int Count => _count;

        /// <summary>La secuencia más alta aceptada hasta ahora (0 = ninguna; la primera es 1).</summary>
        public uint LastAcceptedSequence { get; private set; }

        /// <summary>Intenta aceptar un input al final de la cola.</summary>
        public InputAcceptResult TryAccept(in MoveIntent intent)
        {
            // Con SequenceMath y no con <=: la secuencia es uint y da la vuelta. Comparar
            // directamente rechazaría todos los inputs durante 6,8 años a partir de la vuelta.
            if (!SequenceMath.IsNewer(intent.Sequence, LastAcceptedSequence))
            {
                return InputAcceptResult.OutOfOrder;
            }

            if (_count == _items.Length)
            {
                return InputAcceptResult.BufferFull;
            }

            _items[(_head + _count) % _items.Length] = intent;
            _count++;
            LastAcceptedSequence = intent.Sequence;
            return InputAcceptResult.Accepted;
        }

        /// <summary>Saca el input más antiguo.</summary>
        public bool TryDequeue(out MoveIntent intent)
        {
            if (_count == 0)
            {
                intent = default;
                return false;
            }

            intent = _items[_head];
            _head = (_head + 1) % _items.Length;
            _count--;
            return true;
        }

        /// <summary>Vacía la cola (por ejemplo, al reconectar el mismo jugador).</summary>
        public void Clear()
        {
            _head = 0;
            _count = 0;
            LastAcceptedSequence = 0;
        }
    }
}
