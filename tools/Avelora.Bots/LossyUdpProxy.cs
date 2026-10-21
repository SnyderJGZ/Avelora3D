#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Avelora.Bots
{
    /// <summary>
    /// Proxy UDP de «mala red» para la prueba de carga: pérdida, retardo y jitter en los dos
    /// sentidos (plan del servidor §10).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Escucha en <c>127.0.0.1</c> y, por cada dirección de cliente nueva, abre <b>un socket
    /// propio</b> hacia el servidor real. Así el servidor ve una conexión por bot, como si no
    /// hubiera proxy, y el handshake y los timeouts de LiteNetLib se comportan igual que en una
    /// red con pérdidas.
    /// </para>
    /// <para>
    /// No usa la simulación de LiteNetLib a propósito: en el paquete NuGet suele venir compilada
    /// fuera y solo afectaría a un extremo. Aquí se aplica a cada <b>datagrama</b> de los dos
    /// sentidos, antes de reenviarlo:
    /// </para>
    /// <list type="bullet">
    /// <item>se pierde con probabilidad <see cref="Loss"/>;</item>
    /// <item>se entrega tras <c>latencyMs ± jitterMs</c>, elegido con una distribución uniforme,
    /// así que con jitter puede llegar reordenado (que es lo que se quiere probar).</item>
    /// </list>
    /// <para>
    /// Todos los aleatorios salen de un <see cref="Random"/> con la semilla pedida: dos pasadas con
    /// la misma semilla y el mismo tráfico pierden y retrasan lo mismo.
    /// </para>
    /// <para>
    /// Dos hilos: uno recibe de todos los sockets, y otro es el hilo de entrega, que mantiene una
    /// cola ordenada por instante de salida. Los contadores son la evidencia que usan las pruebas.
    /// </para>
    /// </remarks>
    public sealed class LossyUdpProxy : IDisposable
    {
        /// <summary>Tamaño de un datagrama UDP (el mayor que se puede recibir de una vez).</summary>
        private const int BufferSize = 65536;

        private readonly string _serverHost;
        private readonly int _serverPort;
        private readonly int _listenPort;
        private readonly double _loss;
        private readonly int _latencyMs;
        private readonly int _jitterMs;
        private readonly Random _random;
        private readonly IPEndPoint _server;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private readonly Dictionary<string, Link> _links = new Dictionary<string, Link>(StringComparer.Ordinal);
        private readonly PriorityQueue<PendingDatagram, long> _outgoing =
            new PriorityQueue<PendingDatagram, long>();
        private readonly object _outgoingSync = new object();
        private readonly byte[] _buffer = new byte[BufferSize];

        private Socket? _front;
        private Thread? _receiveThread;
        private Thread? _deliveryThread;
        private volatile bool _stopping;

        private long _clientDatagrams;
        private long _serverDatagrams;
        private long _dropped;
        private long _forwardedToServer;
        private long _forwardedToClient;

        /// <param name="serverHost">Servidor real al que se reenvía.</param>
        /// <param name="serverPort">Puerto del servidor real.</param>
        /// <param name="loss">Probabilidad de perder cada datagrama, en <c>[0, 1)</c>.</param>
        /// <param name="latencyMs">Retardo base en milisegundos.</param>
        /// <param name="jitterMs">Variación uniforme <c>±jitterMs</c> sobre el retardo base.</param>
        /// <param name="seed">Semilla del generador aleatorio (la misma de los bots).</param>
        /// <param name="listenPort">Puerto local; <c>0</c> pide uno libre al sistema.</param>
        public LossyUdpProxy(
            string serverHost,
            int serverPort,
            double loss,
            int latencyMs,
            int jitterMs,
            int seed,
            int listenPort = 0)
        {
            if (string.IsNullOrWhiteSpace(serverHost))
            {
                throw new ArgumentException("Hace falta el host del servidor.", nameof(serverHost));
            }

            if (serverPort < 1 || serverPort > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(serverPort));
            }

            if (double.IsNaN(loss) || loss < 0d || loss >= 1d)
            {
                throw new ArgumentOutOfRangeException(nameof(loss), loss, "La pérdida debe cumplir 0 <= P < 1.");
            }

            if (latencyMs < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(latencyMs));
            }

            if (jitterMs < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(jitterMs));
            }

            if (listenPort < 0 || listenPort > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(listenPort));
            }

            _serverHost = serverHost;
            _serverPort = serverPort;
            _loss = loss;
            _latencyMs = latencyMs;
            _jitterMs = jitterMs;
            _listenPort = listenPort;
            _random = new Random(seed);
            _server = Resolve(serverHost, serverPort);
        }

        /// <summary>Puerto local del proxy. Solo es válido después de <see cref="Start"/>.</summary>
        public int Port { get; private set; }

        /// <summary>Servidor real al que reenvía.</summary>
        public string ServerEndPoint => _serverHost + ":" + _serverPort;

        /// <summary>Datagramas recibidos de los bots.</summary>
        public long ClientDatagrams => Interlocked.Read(ref _clientDatagrams);

        /// <summary>Datagramas recibidos del servidor.</summary>
        public long ServerDatagrams => Interlocked.Read(ref _serverDatagrams);

        /// <summary>Datagramas perdidos a propósito.</summary>
        public long Dropped => Interlocked.Read(ref _dropped);

        /// <summary>Datagramas entregados al servidor.</summary>
        public long ForwardedToServer => Interlocked.Read(ref _forwardedToServer);

        /// <summary>Datagramas entregados a los bots.</summary>
        public long ForwardedToClient => Interlocked.Read(ref _forwardedToClient);

        /// <summary>Datagramas esperando su instante de salida.</summary>
        public int Pending
        {
            get
            {
                lock (_outgoingSync)
                {
                    return _outgoing.Count;
                }
            }
        }

        /// <summary>Abre el socket local y arranca los dos hilos.</summary>
        public void Start()
        {
            if (_front != null)
            {
                throw new InvalidOperationException("El proxy ya está en marcha.");
            }

            var front = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                ReceiveBufferSize = 1 << 20,
                SendBufferSize = 1 << 20,
            };

            front.Bind(new IPEndPoint(IPAddress.Loopback, _listenPort));
            _front = front;
            Port = ((IPEndPoint)front.LocalEndPoint!).Port;

            _receiveThread = new Thread(ReceiveLoop)
            {
                Name = "avelora-proxy-rx",
                IsBackground = true,
            };

            _deliveryThread = new Thread(DeliveryLoop)
            {
                Name = "avelora-proxy-tx",
                IsBackground = true,
            };

            _receiveThread.Start();
            _deliveryThread.Start();
        }

        public void Dispose()
        {
            if (_stopping)
            {
                return;
            }

            _stopping = true;
            _receiveThread?.Join(TimeSpan.FromSeconds(2));
            _deliveryThread?.Join(TimeSpan.FromSeconds(2));
            _receiveThread = null;
            _deliveryThread = null;

            _front?.Dispose();
            _front = null;

            foreach (Link link in _links.Values)
            {
                link.Upstream.Dispose();
            }

            _links.Clear();

            lock (_outgoingSync)
            {
                _outgoing.Clear();
            }
        }

        // ---------------------------------------------------------------- hilo de recepción

        private void ReceiveLoop()
        {
            while (!_stopping)
            {
                bool worked = false;

                try
                {
                    worked |= PumpFront();
                    worked |= PumpUpstreams();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if (!worked)
                {
                    // Sin trabajo: ceder el turno. Es una herramienta, no el camino caliente del
                    // servidor; un milisegundo de espera no cambia la medida.
                    Thread.Sleep(1);
                }
            }
        }

        /// <summary>Vacía lo que llegue de los bots.</summary>
        private bool PumpFront()
        {
            Socket? front = _front;
            if (front == null)
            {
                return false;
            }

            bool worked = false;

            while (!_stopping && front.Available > 0)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int length;

                try
                {
                    length = front.ReceiveFrom(_buffer, ref from);
                }
                catch (SocketException)
                {
                    break;
                }

                if (length < 0)
                {
                    continue;
                }

                worked = true;
                Interlocked.Increment(ref _clientDatagrams);

                Link link = GetOrCreateLink((IPEndPoint)from);
                Schedule(link.Upstream, _server, toServer: true, length);
            }

            return worked;
        }

        /// <summary>Vacía lo que llegue del servidor (un socket por bot).</summary>
        private bool PumpUpstreams()
        {
            bool worked = false;

            foreach (Link link in _links.Values)
            {
                Socket upstream = link.Upstream;

                while (!_stopping && upstream.Available > 0)
                {
                    int length;

                    try
                    {
                        length = upstream.Receive(_buffer);
                    }
                    catch (SocketException)
                    {
                        break;
                    }

                    if (length < 0)
                    {
                        continue;
                    }

                    worked = true;
                    Interlocked.Increment(ref _serverDatagrams);

                    Socket? front = _front;
                    if (front == null)
                    {
                        continue;
                    }

                    Schedule(front, link.Client, toServer: false, length);
                }
            }

            return worked;
        }

        /// <summary>
        /// Devuelve la conexión del cliente, creándola la primera vez. Cada cliente tiene su propio
        /// socket hacia el servidor: el servidor ve una conexión por bot, no una compartida.
        /// </summary>
        private Link GetOrCreateLink(IPEndPoint client)
        {
            string key = client.Address + ":" + client.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (_links.TryGetValue(key, out Link? existing))
            {
                return existing;
            }

            var upstream = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                ReceiveBufferSize = 1 << 20,
                SendBufferSize = 1 << 20,
            };

            // Cualquier interfaz: el servidor real puede estar en otra máquina; con 127.0.0.1
            // el resultado es el mismo y el origen sigue siendo loopback.
            upstream.Bind(new IPEndPoint(IPAddress.Any, 0));

            var link = new Link(upstream, client);
            _links.Add(key, link);
            return link;
        }

        // ---------------------------------------------------------------- hilo de entrega

        private void DeliveryLoop()
        {
            while (!_stopping)
            {
                bool worked = DeliverDue();

                if (!worked)
                {
                    Thread.Sleep(1);
                }
            }
        }

        /// <summary>Entrega los datagramas cuyo instante de salida ya ha llegado.</summary>
        private bool DeliverDue()
        {
            bool worked = false;

            while (!_stopping)
            {
                PendingDatagram pending;

                lock (_outgoingSync)
                {
                    if (_outgoing.Count == 0)
                    {
                        break;
                    }

                    if (!_outgoing.TryPeek(out pending, out long due))
                    {
                        break;
                    }

                    if (due > _clock.ElapsedMilliseconds)
                    {
                        break;
                    }

                    _outgoing.Dequeue();
                }

                try
                {
                    pending.Target.SendTo(pending.Data, pending.Destination);
                }
                catch (SocketException)
                {
                    // Un datagrama perdido en el sistema es exactamente lo que esta herramienta
                    // simula; el proxy no puede hacer nada más con él.
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return worked;
                }

                if (pending.ToServer)
                {
                    Interlocked.Increment(ref _forwardedToServer);
                }
                else
                {
                    Interlocked.Increment(ref _forwardedToClient);
                }

                worked = true;
            }

            return worked;
        }

        /// <summary>
        /// Decide el destino de un datagrama: se pierde o se encola para su instante de salida. La
        /// pérdida y el jitter se aplican por datagrama, así que dos pasadas con la misma semilla
        /// y el mismo tráfico dan exactamente el mismo resultado.
        /// </summary>
        private void Schedule(Socket target, EndPoint destination, bool toServer, int length)
        {
            if (_loss > 0d && _random.NextDouble() < _loss)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }

            int delay = _latencyMs;

            if (_jitterMs > 0)
            {
                delay += _random.Next(-_jitterMs, _jitterMs + 1);
            }

            if (delay < 0)
            {
                delay = 0;
            }

            var copy = new byte[length];
            if (length > 0)
            {
                Buffer.BlockCopy(_buffer, 0, copy, 0, length);
            }

            long due = _clock.ElapsedMilliseconds + delay;

            lock (_outgoingSync)
            {
                _outgoing.Enqueue(new PendingDatagram(target, destination, copy, toServer), due);
            }
        }

        private static IPEndPoint Resolve(string host, int port)
        {
            if (IPAddress.TryParse(host, out IPAddress? parsed))
            {
                return new IPEndPoint(parsed, port);
            }

            IPAddress[] addresses = Dns.GetHostAddresses(host);

            for (int i = 0; i < addresses.Length; i++)
            {
                if (addresses[i].AddressFamily == AddressFamily.InterNetwork)
                {
                    return new IPEndPoint(addresses[i], port);
                }
            }

            throw new InvalidOperationException(
                "No se pudo resolver '" + host + "' a una dirección IPv4 para el proxy.");
        }

        private sealed class Link
        {
            public Link(Socket upstream, IPEndPoint client)
            {
                Upstream = upstream;
                Client = client;
            }

            public Socket Upstream { get; }

            public IPEndPoint Client { get; }
        }

        private readonly struct PendingDatagram
        {
            public PendingDatagram(Socket target, EndPoint destination, byte[] data, bool toServer)
            {
                Target = target;
                Destination = destination;
                Data = data;
                ToServer = toServer;
            }

            public Socket Target { get; }

            public EndPoint Destination { get; }

            public byte[] Data { get; }

            /// <summary>Si va hacia el servidor (para los contadores).</summary>
            public bool ToServer { get; }
        }
    }
}
