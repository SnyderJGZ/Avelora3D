using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Avelora.Bots;
using Xunit;

namespace Avelora.Tests
{
    /// <summary>
    /// Pruebas del proxy de mala red de G-02: reenvío en los dos sentidos, pérdida con una semilla
    /// fija y retardo mínimo.
    /// </summary>
    /// <remarks>
    /// Se usan sockets UDP de verdad en <c>127.0.0.1</c> con puertos efímeros: el proxy es una
    /// herramienta y lo que hay que comprobar es el camino real, no una simulación paralela.
    /// </remarks>
    public class LossyUdpProxyTests
    {
        [Fact]
        public void Sin_Perdida_Ni_Retardo_Reenvia_En_Los_Dos_Sentidos()
        {
            using var server = new FakeUdpServer(echo: true);
            using var proxy = new LossyUdpProxy("127.0.0.1", server.Port, 0d, 0, 0, seed: 2026);
            proxy.Start();

            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            client.ReceiveTimeout = 3000;

            byte[] payload = { 1, 2, 3, 4, 5 };
            var destination = new IPEndPoint(IPAddress.Loopback, proxy.Port);
            client.SendTo(payload, destination);

            byte[] received = ReceiveOne(client, payload.Length);

            Assert.Equal(payload, received);
            Assert.Equal(1, proxy.ClientDatagrams);
            Assert.Equal(1, proxy.ForwardedToServer);
            Assert.Equal(1, proxy.ServerDatagrams);

            // El proxy cuenta la entrega después de SendTo: el eco puede llegar antes que el contador.
            Assert.True(WaitUntil(() => proxy.ForwardedToClient == 1, TimeSpan.FromSeconds(2)));
            Assert.Equal(0, proxy.Dropped);
        }

        [Fact]
        public void Con_Perdida_De_La_Mitad_Y_Semilla_Fija_Pasan_Entre_400_Y_600_De_1000()
        {
            using var server = new FakeUdpServer(echo: false);
            using var proxy = new LossyUdpProxy("127.0.0.1", server.Port, 0.5d, 0, 0, seed: 4242);
            proxy.Start();

            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            var destination = new IPEndPoint(IPAddress.Loopback, proxy.Port);
            byte[] payload = { 9, 9, 9, 9 };

            SendPaced(client, destination, payload, count: 1000, millisecondsBetween: 0.2d);

            Assert.True(
                WaitUntil(() => proxy.ClientDatagrams >= 1000, TimeSpan.FromSeconds(10)),
                "El proxy no recibió los 1000 datagramas (llegaron " + proxy.ClientDatagrams + ").");

            // Espera a que la cola de entrega se vacíe y a que el destino consuma lo enviado.
            Assert.True(
                WaitUntil(() => proxy.Pending == 0, TimeSpan.FromSeconds(10)),
                "La cola de entrega del proxy no se vació.");
            Thread.Sleep(200);

            long forwarded = proxy.ForwardedToServer;
            int delivered = server.Received;

            Assert.InRange(delivered, 400, 600);
            Assert.Equal(1000 - forwarded, proxy.Dropped);
        }

        [Fact]
        public void Con_Retardo_De_50_Ms_Ningun_Datagrama_Llega_Antes_De_45_Ms()
        {
            using var server = new FakeUdpServer(echo: false);
            using var proxy = new LossyUdpProxy("127.0.0.1", server.Port, 0d, latencyMs: 50, jitterMs: 0, seed: 7);
            proxy.Start();

            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            var destination = new IPEndPoint(IPAddress.Loopback, proxy.Port);

            long sentAt = Stopwatch.GetTimestamp();
            client.SendTo(new byte[] { 4, 2 }, destination);

            Assert.True(
                WaitUntil(() => server.Received >= 1, TimeSpan.FromSeconds(5)),
                "El datagrama no llegó al servidor falso.");

            long[] arrivals = server.Arrivals;
            Assert.Single(arrivals);

            double elapsedMs = (arrivals[0] - sentAt) * 1000d / Stopwatch.Frequency;

            Assert.True(
                elapsedMs >= 45d,
                "El datagrama llegó a los " + elapsedMs.ToString("0.0") + " ms; con 50 ms de retardo no debería bajar de 45.");
        }

        [Fact]
        public void Una_Perdida_Fuera_De_Rango_Se_Rechaza()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new LossyUdpProxy("127.0.0.1", 7777, 1d, 0, 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LossyUdpProxy("127.0.0.1", 7777, -0.1d, 0, 0, 1));
        }

        private static byte[] ReceiveOne(Socket socket, int expectedLength)
        {
            var buffer = new byte[Math.Max(expectedLength, 64)];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int length = socket.ReceiveFrom(buffer, ref from);

            var result = new byte[length];
            Buffer.BlockCopy(buffer, 0, result, 0, length);
            return result;
        }

        /// <summary>
        /// Envía con una pausa corta entre datagramas para no desbordar el búfer del sistema; la
        /// prueba mide la pérdida del proxy, no la del kernel.
        /// </summary>
        private static void SendPaced(Socket socket, EndPoint destination, byte[] payload, int count, double millisecondsBetween)
        {
            var clock = Stopwatch.StartNew();

            for (int i = 0; i < count; i++)
            {
                socket.SendTo(payload, destination);

                double target = clock.Elapsed.TotalMilliseconds + millisecondsBetween;
                while (clock.Elapsed.TotalMilliseconds < target)
                {
                    Thread.SpinWait(100);
                }
            }
        }

        private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
        {
            var clock = Stopwatch.StartNew();

            while (clock.Elapsed < timeout)
            {
                if (condition())
                {
                    return true;
                }

                Thread.Sleep(5);
            }

            return condition();
        }

        /// <summary>Servidor UDP mínimo: cuenta lo que llega, apunta el instante y, si toca, hace eco.</summary>
        private sealed class FakeUdpServer : IDisposable
        {
            private readonly Socket _socket;
            private readonly Thread _thread;
            private readonly bool _echo;
            private readonly List<long> _arrivals = new List<long>();
            private volatile bool _stopping;
            private int _received;

            public FakeUdpServer(bool echo)
            {
                _echo = echo;
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                {
                    ReceiveBufferSize = 1 << 20,
                };

                _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;

                _thread = new Thread(Run)
                {
                    Name = "avelora-fake-udp",
                    IsBackground = true,
                };

                _thread.Start();
            }

            public int Port { get; }

            public int Received => Volatile.Read(ref _received);

            public long[] Arrivals
            {
                get
                {
                    lock (_arrivals)
                    {
                        return _arrivals.ToArray();
                    }
                }
            }

            public void Dispose()
            {
                _stopping = true;
                _socket.Dispose();
                _thread.Join(TimeSpan.FromSeconds(2));
            }

            private void Run()
            {
                var buffer = new byte[2048];

                while (!_stopping)
                {
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);

                    try
                    {
                        int length = _socket.ReceiveFrom(buffer, ref remote);

                        lock (_arrivals)
                        {
                            _arrivals.Add(Stopwatch.GetTimestamp());
                        }

                        Interlocked.Increment(ref _received);

                        if (_echo)
                        {
                            _socket.SendTo(buffer, 0, length, SocketFlags.None, remote);
                        }
                    }
                    catch (SocketException)
                    {
                        // Socket cerrado al terminar la prueba.
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                }
            }
        }
    }
}
