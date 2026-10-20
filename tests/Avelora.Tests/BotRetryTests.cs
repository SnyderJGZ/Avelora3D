using System;
using System.Collections.Generic;
using System.Threading;
using Avelora.Bots;
using Avelora.Net;
using Avelora.Shared;
using Avelora.Shared.Protocol;
using Xunit;

namespace Avelora.Tests
{
    /// <summary>
    /// Pruebas del reintento de G-01: un bot que recibe <c>RateLimited</c> debe cerrar, esperar y
    /// volver a conectar con un transporte nuevo; cualquier otro rechazo es final.
    /// </summary>
    /// <remarks>
    /// Se prueba <see cref="BotClient"/> con un transporte guionizado en vez de un servidor real:
    /// provocar un <c>RateLimited</c> de verdad exigiría llenar la cola de logins o agotar los
    /// intentos fallidos de una cuenta, y eso haría la prueba lenta y frágil. El guion reproduce
    /// exactamente los bytes que manda el servidor.
    /// </remarks>
    public class BotRetryTests
    {
        [Fact]
        public void El_Bot_Reintenta_Tras_RateLimited_Y_Acaba_Entrando()
        {
            var transports = new List<ScriptedTransport>();

            using var bot = new BotClient(
                1,
                "127.0.0.1",
                7777,
                NewMapHash(),
                seed: 2026,
                runMs: 200,
                accountPrefix: "bot",
                password: "clave",
                invitationCode: "invitacion",
                startDelayMs: 0,
                retries: 3,
                welcomeTimeoutMs: 2000,
                behavior: BotBehavior.Crowd,
                transportFactory: () =>
                {
                    // El primer transporte recibe RateLimited; el segundo ya entra.
                    var transport = new ScriptedTransport(
                        transports.Count == 0 ? ScriptedTransport.Mode.RateLimited : ScriptedTransport.Mode.Welcome);
                    transports.Add(transport);
                    return transport;
                });

            bot.Run(CancellationToken.None);

            Assert.Equal(2, transports.Count);
            Assert.Equal(1, bot.Retries);
            Assert.True(bot.EntityId > 0, "El bot debería haber recibido el Welcome del segundo intento.");
            Assert.Equal("terminado", bot.Status);
            Assert.Null(bot.RejectReasonText);
            Assert.True(bot.WelcomeMs >= 1000, "El Welcome llega tras la espera de 1000 ms del reintento.");
        }

        [Fact]
        public void Un_Rechazo_Que_No_Es_RateLimited_No_Se_Reintenta()
        {
            var transports = new List<ScriptedTransport>();

            using var bot = new BotClient(
                2,
                "127.0.0.1",
                7777,
                NewMapHash(),
                seed: 2026,
                runMs: 200,
                accountPrefix: "bot",
                password: "clave",
                invitationCode: "invitacion",
                startDelayMs: 0,
                retries: 3,
                welcomeTimeoutMs: 2000,
                behavior: BotBehavior.Random,
                transportFactory: () =>
                {
                    var transport = new ScriptedTransport(ScriptedTransport.Mode.InvalidCredentials);
                    transports.Add(transport);
                    return transport;
                });

            bot.Run(CancellationToken.None);

            Assert.Single(transports);
            Assert.Equal(0, bot.Retries);
            Assert.Equal(-1, bot.EntityId);
            Assert.Equal("InvalidCredentials", bot.RejectReasonText);
        }

        private static byte[] NewMapHash() => new byte[ProtocolConstants.MapHashSize];

        /// <summary>Transporte falso: en el primer mensaje contesta lo que diga el guion.</summary>
        private sealed class ScriptedTransport : IClientTransport
        {
            public enum Mode
            {
                Welcome,
                RateLimited,
                InvalidCredentials,
            }

            private const int EntityId = 42;

            private readonly Mode _mode;
            private bool _connected;
            private bool _connectPending;
            private bool _helloAnswered;
            private byte[]? _pending;

            public ScriptedTransport(Mode mode)
            {
                _mode = mode;
            }

            public bool IsConnected => _connected;

            public event Action? Connected;

            public event Action<DisconnectReason>? Disconnected;

            public event TransportMessageHandler? Received;

            public void Connect(string host, int port)
            {
                _connectPending = true;
            }

            public void Poll()
            {
                if (_connectPending)
                {
                    _connectPending = false;
                    _connected = true;
                    Connected?.Invoke();
                    return;
                }

                if (_pending == null)
                {
                    return;
                }

                byte[] data = _pending;
                _pending = null;
                Received?.Invoke(0, data, 0, data.Length);
            }

            public void Send(byte[] data, int length, DeliveryMode mode)
            {
                if (_helloAnswered)
                {
                    return;
                }

                // El bot ya mandó el saludo: la respuesta se entrega en el siguiente Poll.
                _helloAnswered = true;
                _pending = _mode switch
                {
                    Mode.Welcome => MessageCodec.EncodeWelcome(new WelcomeMessage(
                        ProtocolConstants.Version,
                        (ushort)SimulationConstants.TickRate,
                        EntityId,
                        0u,
                        0u,
                        -1.5f,
                        -2.5f,
                        4f,
                        0.4f,
                        0.4f,
                        new byte[ProtocolConstants.MapHashSize])),
                    Mode.RateLimited => MessageCodec.EncodeReject(new RejectMessage(RejectReason.RateLimited)),
                    _ => MessageCodec.EncodeReject(new RejectMessage(RejectReason.InvalidCredentials)),
                };
            }

            public void Disconnect()
            {
                if (!_connected)
                {
                    return;
                }

                _connected = false;
                Disconnected?.Invoke(DisconnectReason.LocalShutdown);
            }

            public void Dispose()
            {
            }
        }
    }
}
