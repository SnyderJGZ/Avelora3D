#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using Avelora.Net;
using Avelora.Shared;
using Avelora.Shared.Protocol;

namespace Avelora.Bots
{
    /// <summary>
    /// Cliente sin ventana que juega por la red real: conecta, saluda, manda intenciones a 20 Hz y
    /// lee snapshots. Sirve para ver la Etapa C funcionando y será la base de la Etapa G.
    /// </summary>
    /// <remarks>
    /// No conoce el servidor: usa exactamente el mismo protocolo y el mismo transporte que usará el
    /// cliente de Unity. Si el bot se mueve, la costura cliente-servidor está bien.
    /// </remarks>
    public sealed class BotClient : IDisposable
    {
        /// <summary>Un input cada 50 ms, igual que el tick del servidor.</summary>
        private const int InputIntervalMs = 50;

        /// <summary>Cuánto puede esperar al saludo antes de darse por vencido.</summary>
        private const int WelcomeTimeoutMs = 5000;

        private readonly IClientTransport _transport;
        private readonly string _host;
        private readonly int _port;
        private readonly byte[] _mapHash;
        private readonly uint _clientToken;
        private readonly Random _random;
        private readonly int _runMs;
        private readonly string _accountName;
        private readonly string _password;
        private readonly string _invitationCode;

        private bool _connected;
        private bool _helloSent;
        private bool _welcomed;
        private bool _rejected;
        private bool _disconnected;

        private uint _sequence;
        private sbyte _directionX;
        private sbyte _directionY;
        private long _nextInputAt;
        private long _nextDirectionChangeAt;
        private long _startedAt;

        private long _snapshots;
        private float _x;
        private float _y;

        public BotClient(
            int index,
            string host,
            int port,
            byte[] mapHash,
            int seed,
            int runMs,
            string accountPrefix,
            string password,
            string invitationCode)
        {
            Index = index;
            _host = host;
            _port = port;
            _mapHash = mapHash;
            _runMs = runMs;
            _accountName = accountPrefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _password = password;
            _invitationCode = invitationCode;
            _random = new Random(seed + (index * 7919));
            _clientToken = (uint)(seed + (index * 104729));
            _transport = new LiteNetLibClientTransport();

            _transport.Connected += OnConnected;
            _transport.Disconnected += OnDisconnected;
            _transport.Received += OnReceived;
        }

        public int Index { get; }

        public string AccountName => _accountName;

        public int EntityId { get; private set; } = -1;

        public string Status { get; private set; } = "iniciando";

        public string? RejectReason { get; private set; }

        public long Snapshots => Interlocked.Read(ref _snapshots);

        public float X => _x;

        public float Y => _y;

        public uint Sequence => _sequence;

        /// <summary>Conecta y juega durante el tiempo pedido. Bloquea el hilo que lo llama.</summary>
        public void Run(CancellationToken cancellationToken)
        {
            _startedAt = Environment.TickCount64;
            _nextInputAt = _startedAt;
            _nextDirectionChangeAt = _startedAt;
            PickDirection();

            try
            {
                _transport.Connect(_host, _port);
            }
            catch (Exception ex)
            {
                Status = "no se pudo conectar: " + ex.Message;
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                _transport.Poll();

                long now = Environment.TickCount64;

                if (_connected && !_helloSent)
                {
                    SendHello();
                }

                if (_rejected)
                {
                    Status = "rechazado (" + RejectReason + ")";
                    break;
                }

                if (_disconnected && !_welcomed)
                {
                    Status = "conexión cerrada antes del saludo";
                    break;
                }

                if (_welcomed)
                {
                    if (now >= _nextInputAt)
                    {
                        if (now >= _nextDirectionChangeAt)
                        {
                            PickDirection();
                        }

                        SendMoveInput();

                        _nextInputAt += InputIntervalMs;
                        if (_nextInputAt < now)
                        {
                            // Si el hilo se durmió de más, no acumular una ráfaga de inputs viejos.
                            _nextInputAt = now + InputIntervalMs;
                        }
                    }
                }
                else if (now - _startedAt > WelcomeTimeoutMs)
                {
                    Status = "sin respuesta del servidor";
                    break;
                }

                if (now - _startedAt >= _runMs)
                {
                    if (_welcomed)
                    {
                        Status = "terminado";
                    }

                    break;
                }

                Thread.Sleep(2);
            }

            _transport.Disconnect();
        }

        public void Dispose() => _transport.Dispose();

        private void OnConnected()
        {
            _connected = true;
        }

        private void OnDisconnected(DisconnectReason reason)
        {
            _disconnected = true;
        }

        private void SendHello()
        {
            _helloSent = true;
            byte[] hello = MessageCodec.EncodeHello(new HelloMessage(
                ProtocolConstants.Version,
                (ushort)SimulationConstants.TickRate,
                _mapHash,
                _clientToken,
                _accountName,
                _password,
                _accountName,
                _invitationCode));

            _transport.Send(hello, hello.Length, DeliveryMode.ReliableOrdered);
        }

        private void SendMoveInput()
        {
            _sequence++;
            byte[] input = MessageCodec.EncodeMoveInput(new MoveInputMessage(_sequence, _directionX, _directionY));

            // Secuenciado: si un input se pierde, el siguiente lo reemplaza; no se reintenta.
            _transport.Send(input, input.Length, DeliveryMode.Sequenced);
        }

        private void OnReceived(int connectionId, byte[] buffer, int offset, int length)
        {
            if (MessageCodec.TryReadWelcome(buffer, offset, length, out WelcomeMessage welcome))
            {
                _welcomed = true;
                EntityId = welcome.EntityId;
                _x = welcome.SpawnX;
                _y = welcome.SpawnY;
                Status = "conectado";
                return;
            }

            if (MessageCodec.TryReadReject(buffer, offset, length, out RejectMessage reject))
            {
                _rejected = true;
                RejectReason = reject.Reason.ToString();
                return;
            }

            if (MessageCodec.TryReadSnapshot(buffer, offset, length, out SnapshotMessage snapshot))
            {
                Interlocked.Increment(ref _snapshots);

                for (int i = 0; i < snapshot.Entities.Length; i++)
                {
                    if (snapshot.Entities[i].Id == EntityId)
                    {
                        _x = ProtocolConstants.DequantizePosition(snapshot.Entities[i].X);
                        _y = ProtocolConstants.DequantizePosition(snapshot.Entities[i].Y);
                        break;
                    }
                }
            }
        }

        private void PickDirection()
        {
            _directionX = (sbyte)_random.Next(-1, 2);
            _directionY = (sbyte)_random.Next(-1, 2);
            _nextDirectionChangeAt = Environment.TickCount64 + _random.Next(800, 2200);
        }
    }
}
