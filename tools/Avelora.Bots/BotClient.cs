#nullable enable
using System;
using System.Threading;
using Avelora.Net;
using Avelora.Shared;
using Avelora.Shared.Protocol;

namespace Avelora.Bots
{
    /// <summary>Cómo se mueve un bot durante la prueba de carga.</summary>
    public enum BotBehavior
    {
        /// <summary>Cambia de dirección al azar, como un jugador que pasea.</summary>
        Random = 0,

        /// <summary>Manda siempre <c>(0, 0)</c>: todos se quedan donde aparecen (peor caso del área de interés).</summary>
        Crowd = 1,
    }

    /// <summary>
    /// Cliente sin ventana que juega por la red real: conecta, saluda, manda intenciones a 20 Hz y
    /// lee snapshots. Sirve para ver la Etapa C funcionando y es la base de la Etapa G.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No conoce el servidor: usa exactamente el mismo protocolo y el mismo transporte que usará el
    /// cliente de Unity. Si el bot se mueve, la costura cliente-servidor está bien.
    /// </para>
    /// <para>
    /// Para la prueba de carga (G-01): el bot puede arrancar con retraso, reintentar cuando el
    /// servidor le contesta <c>RateLimited</c> (con un transporte nuevo), quedarse quieto
    /// (<see cref="BotBehavior.Crowd"/>) y medir su tiempo hasta el <c>Welcome</c> y el hueco máximo
    /// entre snapshots.
    /// </para>
    /// </remarks>
    public sealed class BotClient : IDisposable
    {
        /// <summary>Un input cada 50 ms, igual que el tick del servidor.</summary>
        private const int InputIntervalMs = 50;

        /// <summary>Cuánto se espera entre reintentos, multiplicado por el número de intento.</summary>
        private const int RetryDelayMs = 1000;

        private readonly Func<IClientTransport> _transportFactory;
        private readonly string _host;
        private readonly int _port;
        private readonly byte[] _mapHash;
        private readonly uint _clientToken;
        private readonly Random _random;
        private readonly int _runMs;
        private readonly int _startDelayMs;
        private readonly int _maxRetries;
        private readonly int _welcomeTimeoutMs;
        private readonly BotBehavior _behavior;
        private readonly string _accountName;
        private readonly string _password;
        private readonly string _invitationCode;

        private IClientTransport? _transport;

        // Estado del intento en curso; se reinicia en cada intento.
        private bool _connected;
        private bool _helloSent;
        private bool _welcomed;
        private bool _disconnected;
        private RejectReason? _reject;
        private long _attemptStartedAt;
        private long _welcomedAt;
        private long _lastSnapshotAt;

        private uint _sequence;
        private sbyte _directionX;
        private sbyte _directionY;
        private long _nextInputAt;
        private long _nextDirectionChangeAt;

        private long _snapshots;
        private float _x;
        private float _y;

        // Mediciones de toda la vida del bot.
        private long _firstAttemptAt = -1;
        private int _retries;
        private long _welcomeMs = -1;
        private long _playedMs;
        private long _maxSnapshotGapMs;

        public BotClient(
            int index,
            string host,
            int port,
            byte[] mapHash,
            int seed,
            int runMs,
            string accountPrefix,
            string password,
            string invitationCode,
            int startDelayMs = 0,
            int retries = 5,
            int welcomeTimeoutMs = 15000,
            BotBehavior behavior = BotBehavior.Random,
            Func<IClientTransport>? transportFactory = null)
        {
            if (startDelayMs < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(startDelayMs));
            }

            if (retries < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(retries));
            }

            if (welcomeTimeoutMs <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(welcomeTimeoutMs));
            }

            Index = index;
            _host = host;
            _port = port;
            _mapHash = mapHash;
            _runMs = runMs;
            _startDelayMs = startDelayMs;
            _maxRetries = retries;
            _welcomeTimeoutMs = welcomeTimeoutMs;
            _behavior = behavior;
            _accountName = accountPrefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _password = password;
            _invitationCode = invitationCode;
            _random = new Random(seed + (index * 7919));
            _clientToken = (uint)(seed + (index * 104729));

            // Inyectable para las pruebas: el bot real usa LiteNetLib, como el cliente de Unity.
            _transportFactory = transportFactory ?? (() => new LiteNetLibClientTransport());
        }

        public int Index { get; }

        public string AccountName => _accountName;

        public int EntityId { get; private set; } = -1;

        public string Status { get; private set; } = "iniciando";

        /// <summary>Motivo del rechazo final, o <c>null</c> si el bot llegó a entrar o no fue rechazado.</summary>
        public string? RejectReasonText { get; private set; }

        public long Snapshots => Interlocked.Read(ref _snapshots);

        /// <summary>Milisegundos desde el primer intento hasta el <c>Welcome</c>; <c>-1</c> si no llegó.</summary>
        public long WelcomeMs => _welcomeMs;

        /// <summary>Reintentos hechos por <c>RateLimited</c>.</summary>
        public int Retries => _retries;

        /// <summary>Hueco máximo, en ms, entre dos snapshots consecutivos después del <c>Welcome</c>.</summary>
        public long MaxSnapshotGapMs => _maxSnapshotGapMs;

        /// <summary>Snapshots por segundo que recibió mientras jugaba.</summary>
        public double SnapshotsPerSecond =>
            _playedMs <= 0 ? 0d : Snapshots * 1000d / _playedMs;

        public float X => _x;

        public float Y => _y;

        public uint Sequence => _sequence;

        /// <summary>Conecta y juega durante el tiempo pedido. Bloquea el hilo que lo llama.</summary>
        public void Run(CancellationToken cancellationToken)
        {
            if (!Sleep(_startDelayMs, cancellationToken))
            {
                Status = "cancelado antes de empezar";
                return;
            }

            while (true)
            {
                AttemptResult result = PlayOnce(cancellationToken);

                if (result == AttemptResult.Finished)
                {
                    Status = "terminado";
                    break;
                }

                if (result == AttemptResult.Cancelled)
                {
                    Status = "cancelado";
                    break;
                }

                if (result == AttemptResult.RateLimited)
                {
                    // El servidor no puede atender el login ahora: transporte nuevo y espera
                    // creciente. No se reutiliza la conexión porque la que rechaza se cierra.
                    DiscardTransport();

                    if (_retries >= _maxRetries)
                    {
                        Status = "rechazado (RateLimited)";
                        RejectReasonText = RejectReason.RateLimited.ToString();
                        break;
                    }

                    _retries++;
                    Status = "reintentando (" + _retries + "/" + _maxRetries + ")";

                    if (!Sleep(RetryDelayMs * _retries, cancellationToken))
                    {
                        Status = "cancelado";
                        break;
                    }

                    continue;
                }

                // Cualquier otro rechazo o fallo es final: reintentar no arreglaría un mapa
                // distinto, una contraseña mal o un servidor que no contesta.
                break;
            }

            _transport?.Disconnect();
        }

        public void Dispose()
        {
            DiscardTransport();
        }

        /// <summary>Un intento completo: conectar, saludar y jugar hasta que toque salir.</summary>
        private AttemptResult PlayOnce(CancellationToken cancellationToken)
        {
            DiscardTransport();

            _connected = false;
            _helloSent = false;
            _welcomed = false;
            _disconnected = false;
            _reject = null;
            _attemptStartedAt = Environment.TickCount64;
            _nextInputAt = _attemptStartedAt;
            _nextDirectionChangeAt = _attemptStartedAt;
            PickDirection();

            if (_firstAttemptAt < 0)
            {
                _firstAttemptAt = _attemptStartedAt;
            }

            IClientTransport transport;

            try
            {
                transport = _transportFactory();
            }
            catch (Exception ex)
            {
                Status = "no se pudo crear el transporte: " + ex.Message;
                return AttemptResult.Failed;
            }

            _transport = transport;
            transport.Connected += OnConnected;
            transport.Disconnected += OnDisconnected;
            transport.Received += OnReceived;

            try
            {
                transport.Connect(_host, _port);
            }
            catch (Exception ex)
            {
                Status = "no se pudo conectar: " + ex.Message;
                return AttemptResult.Failed;
            }

            while (true)
            {
                transport.Poll();

                long now = Environment.TickCount64;

                if (_connected && !_helloSent)
                {
                    SendHello();
                }

                if (_reject.HasValue)
                {
                    RejectReason reason = _reject.Value;

                    if (reason == RejectReason.RateLimited)
                    {
                        return AttemptResult.RateLimited;
                    }

                    Status = "rechazado (" + reason + ")";
                    RejectReasonText = reason.ToString();
                    return AttemptResult.RejectedFinally;
                }

                if (_disconnected && !_welcomed)
                {
                    Status = "conexión cerrada antes del saludo";
                    return AttemptResult.Failed;
                }

                if (_welcomed)
                {
                    if (now - _welcomedAt >= _runMs)
                    {
                        _playedMs = now - _welcomedAt;
                        return AttemptResult.Finished;
                    }

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
                else if (now - _attemptStartedAt >= _welcomeTimeoutMs)
                {
                    Status = "sin respuesta del servidor";
                    return AttemptResult.Failed;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return AttemptResult.Cancelled;
                }

                Thread.Sleep(2);
            }
        }

        private void DiscardTransport()
        {
            IClientTransport? transport = _transport;
            if (transport == null)
            {
                return;
            }

            _transport = null;

            try
            {
                transport.Disconnect();
            }
            catch (Exception)
            {
                // Un transporte ya caído no debe impedir el reintento.
            }

            transport.Connected -= OnConnected;
            transport.Disconnected -= OnDisconnected;
            transport.Received -= OnReceived;
            transport.Dispose();
        }

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

            _transport!.Send(hello, hello.Length, DeliveryMode.ReliableOrdered);
        }

        private void SendMoveInput()
        {
            _sequence++;
            byte[] input = MessageCodec.EncodeMoveInput(new MoveInputMessage(_sequence, _directionX, _directionY));

            // Secuenciado: si un input se pierde, el siguiente lo reemplaza; no se reintenta.
            _transport!.Send(input, input.Length, DeliveryMode.Sequenced);
        }

        private void OnReceived(int connectionId, byte[] buffer, int offset, int length)
        {
            if (MessageCodec.TryReadWelcome(buffer, offset, length, out WelcomeMessage welcome))
            {
                long now = Environment.TickCount64;

                _welcomed = true;
                _welcomedAt = now;
                _lastSnapshotAt = now;
                _welcomeMs = now - _firstAttemptAt;
                EntityId = welcome.EntityId;
                _x = welcome.SpawnX;
                _y = welcome.SpawnY;
                Status = "conectado";
                return;
            }

            if (MessageCodec.TryReadReject(buffer, offset, length, out RejectMessage reject))
            {
                _reject = reject.Reason;
                return;
            }

            if (MessageCodec.TryReadSnapshot(buffer, offset, length, out SnapshotMessage snapshot))
            {
                Interlocked.Increment(ref _snapshots);

                if (_welcomed)
                {
                    long now = Environment.TickCount64;
                    long gap = now - _lastSnapshotAt;
                    if (gap > _maxSnapshotGapMs)
                    {
                        _maxSnapshotGapMs = gap;
                    }

                    _lastSnapshotAt = now;
                }

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
            if (_behavior == BotBehavior.Crowd)
            {
                // Sin dirección: el servidor no mueve al jugador y todos se apilan en el spawn.
                _directionX = 0;
                _directionY = 0;
                _nextDirectionChangeAt = long.MaxValue;
                return;
            }

            _directionX = (sbyte)_random.Next(-1, 2);
            _directionY = (sbyte)_random.Next(-1, 2);
            _nextDirectionChangeAt = Environment.TickCount64 + _random.Next(800, 2200);
        }

        /// <summary>Espera <paramref name="milliseconds"/> sin dormir de golpe, para poder cancelar.</summary>
        private static bool Sleep(int milliseconds, CancellationToken cancellationToken)
        {
            long deadline = Environment.TickCount64 + milliseconds;

            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    return true;
                }

                cancellationToken.WaitHandle.WaitOne((int)Math.Min(remaining, 50));
            }
        }

        private enum AttemptResult
        {
            Finished,
            RateLimited,
            RejectedFinally,
            Failed,
            Cancelled,
        }
    }
}
