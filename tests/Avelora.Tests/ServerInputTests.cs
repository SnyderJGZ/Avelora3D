using System.Collections.Generic;
using Avelora.Server.Simulation;
using Xunit;

namespace Avelora.Tests
{
    public class InputQueueTests
    {
        [Fact]
        public void Acepta_Y_Devuelve_En_Orden_De_Llegada()
        {
            var queue = new InputQueue(8);

            Assert.Equal(InputQueueResult.Accepted, queue.TryEnqueue(new MoveIntent(1, 1, 1, 0)));
            Assert.Equal(InputQueueResult.Accepted, queue.TryEnqueue(new MoveIntent(1, 2, 0, 1)));
            Assert.Equal(2, queue.Pending);

            var destination = new List<MoveIntent>();
            Assert.Equal(2, queue.DrainInto(destination, 10));

            Assert.Equal(1u, destination[0].Sequence);
            Assert.Equal(2u, destination[1].Sequence);
            Assert.Equal(0, queue.Pending);
            Assert.Equal(2, queue.Received);
        }

        [Fact]
        public void Rechaza_Direcciones_Que_No_Son_Menos_Uno_Cero_O_Uno()
        {
            var queue = new InputQueue(8);

            Assert.Equal(InputQueueResult.InvalidDirection, queue.TryEnqueue(new MoveIntent(1, 1, 5, 0)));
            Assert.Equal(InputQueueResult.InvalidDirection, queue.TryEnqueue(new MoveIntent(1, 2, 0, -3)));
            Assert.Equal(0, queue.Pending);
            Assert.Equal(2, queue.RejectedInvalidDirection);
            Assert.Equal(0, queue.Received);
        }

        [Fact]
        public void Cuando_Se_Llena_Descarta_En_Vez_De_Crecer()
        {
            var queue = new InputQueue(3);

            Assert.Equal(InputQueueResult.Accepted, queue.TryEnqueue(new MoveIntent(1, 1, 1, 0)));
            Assert.Equal(InputQueueResult.Accepted, queue.TryEnqueue(new MoveIntent(1, 2, 1, 0)));
            Assert.Equal(InputQueueResult.Accepted, queue.TryEnqueue(new MoveIntent(1, 3, 1, 0)));
            Assert.Equal(InputQueueResult.QueueFull, queue.TryEnqueue(new MoveIntent(1, 4, 1, 0)));

            Assert.Equal(3, queue.Pending);
            Assert.Equal(1, queue.RejectedQueueFull);

            // Al vaciarse vuelve a admitir.
            var destination = new List<MoveIntent>();
            queue.DrainInto(destination, 10);
            Assert.Equal(InputQueueResult.Accepted, queue.TryEnqueue(new MoveIntent(1, 5, 1, 0)));
        }

        [Fact]
        public void Vacia_Como_Mucho_Lo_Que_Se_Le_Pide_Y_Deja_El_Resto()
        {
            var queue = new InputQueue(8);
            for (uint i = 1; i <= 5; i++)
            {
                queue.TryEnqueue(new MoveIntent(1, i, 1, 0));
            }

            var destination = new List<MoveIntent>();
            Assert.Equal(2, queue.DrainInto(destination, 2));
            Assert.Equal(2, destination.Count);
            Assert.Equal(3, queue.Pending);

            Assert.Equal(3, queue.DrainInto(destination, 10));
            Assert.Equal(0, queue.Pending);
        }

        [Fact]
        public void Vacia_El_Destino_Antes_De_Llenarlo()
        {
            var queue = new InputQueue(8);
            queue.TryEnqueue(new MoveIntent(1, 1, 1, 0));

            var destination = new List<MoveIntent> { new MoveIntent(99, 99, 0, 0) };
            queue.DrainInto(destination, 10);

            Assert.Single(destination);
            Assert.Equal(1, destination[0].EntityId);
        }
    }

    public class PlayerInputBufferTests
    {
        [Fact]
        public void Se_Vacia_En_Orden_Y_No_Guarda_Basura()
        {
            var buffer = new PlayerInputBuffer(4);

            Assert.Equal(InputAcceptResult.Accepted, buffer.TryAccept(new MoveIntent(1, 1, 1, 0)));
            Assert.Equal(InputAcceptResult.Accepted, buffer.TryAccept(new MoveIntent(1, 2, -1, 0)));
            Assert.Equal(2, buffer.Count);

            Assert.True(buffer.TryDequeue(out MoveIntent first));
            Assert.Equal(1u, first.Sequence);
            Assert.True(buffer.TryDequeue(out MoveIntent second));
            Assert.Equal(2u, second.Sequence);
            Assert.False(buffer.TryDequeue(out _));
        }

        [Fact]
        public void Descarta_Repetidos_Y_Desordenados()
        {
            var buffer = new PlayerInputBuffer(4);

            Assert.Equal(InputAcceptResult.Accepted, buffer.TryAccept(new MoveIntent(1, 5, 1, 0)));
            Assert.Equal(InputAcceptResult.OutOfOrder, buffer.TryAccept(new MoveIntent(1, 5, 1, 0)));
            Assert.Equal(InputAcceptResult.OutOfOrder, buffer.TryAccept(new MoveIntent(1, 4, 1, 0)));
            Assert.Equal(1, buffer.Count);
            Assert.Equal(5u, buffer.LastAcceptedSequence);
        }

        [Fact]
        public void Cuando_Se_Llena_Rechaza_El_Exceso()
        {
            var buffer = new PlayerInputBuffer(2);

            Assert.Equal(InputAcceptResult.Accepted, buffer.TryAccept(new MoveIntent(1, 1, 1, 0)));
            Assert.Equal(InputAcceptResult.Accepted, buffer.TryAccept(new MoveIntent(1, 2, 1, 0)));
            Assert.Equal(InputAcceptResult.BufferFull, buffer.TryAccept(new MoveIntent(1, 3, 1, 0)));

            Assert.Equal(2, buffer.Count);

            // Al soltar una entrada vuelve a haber sitio.
            buffer.TryDequeue(out _);
            Assert.Equal(InputAcceptResult.Accepted, buffer.TryAccept(new MoveIntent(1, 3, 1, 0)));
        }

        [Fact]
        public void La_Secuencia_Cero_Se_Descarta_Porque_Significa_Ninguna()
        {
            // El 0 se usa como «ninguna secuencia»: una intención con secuencia 0 debe descartarse.
            var buffer = new PlayerInputBuffer(4);

            Assert.Equal(InputAcceptResult.OutOfOrder, buffer.TryAccept(new MoveIntent(1, 0, 1, 0)));
            Assert.Equal(0, buffer.Count);
        }

        [Fact]
        public void La_Vuelta_Al_Mundo_Se_Avanza_Sin_Reservar()
        {
            // Da más vueltas que capacidad: el anillo debe reutilizar posiciones sin crecer.
            var buffer = new PlayerInputBuffer(3);
            uint sequence = 0;

            for (int i = 0; i < 100; i++)
            {
                sequence++;
                Assert.Equal(InputAcceptResult.Accepted, buffer.TryAccept(new MoveIntent(1, sequence, 1, 0)));
                Assert.True(buffer.TryDequeue(out MoveIntent intent));
                Assert.Equal(sequence, intent.Sequence);
            }

            Assert.Equal(0, buffer.Count);
            Assert.Equal(3, buffer.Capacity);
        }

        [Fact]
        public void Clear_Deja_La_Cola_Lista_Para_Otra_Sesion()
        {
            var buffer = new PlayerInputBuffer(4);
            buffer.TryAccept(new MoveIntent(1, 10, 1, 0));

            buffer.Clear();

            Assert.Equal(0, buffer.Count);
            Assert.Equal(0u, buffer.LastAcceptedSequence);
            Assert.Equal(InputAcceptResult.Accepted, buffer.TryAccept(new MoveIntent(1, 1, 1, 0)));
        }
    }
}
