using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using Avelora.Bots;
using Avelora.Net;
using Avelora.Shared;
using Avelora.Shared.Protocol;
using Xunit;

namespace Avelora.Tests
{
    public sealed class BotItemCatalogTests
    {
        [Fact]
        public void Fight_Equipa_El_Arma_Mas_Fuerte_Segun_El_Catalogo_Del_Servidor()
        {
            var transport = new CatalogTransport();
            using var bot = new BotClient(
                1,
                "127.0.0.1",
                7777,
                new byte[ProtocolConstants.MapHashSize],
                seed: 17,
                runMs: 150,
                accountPrefix: "bot",
                password: "clave",
                invitationCode: "invitacion",
                behavior: BotBehavior.Fight,
                transportFactory: () => transport);

            bot.Run(CancellationToken.None);

            Assert.True(transport.EquipSent);
            Assert.True(transport.AttackSent, "El bot debe usar el alcance del hacha equipada, no el de los puños.");
            Assert.Equal(1, bot.Equipped);
        }

        private sealed class CatalogTransport : IClientTransport
        {
            private readonly Queue<byte[]> _incoming = new Queue<byte[]>();
            private readonly byte[] _nonce = new byte[HelloSecrets.NonceSize];
            private readonly RSAParameters _publicKey;
            private bool _connectPending;
            private bool _connected;

            public CatalogTransport()
            {
                using RSA rsa = RSA.Create(2048);
                _publicKey = rsa.ExportParameters(includePrivateParameters: false);
            }

            public bool EquipSent { get; private set; }
            public bool AttackSent { get; private set; }
            public bool IsConnected => _connected;
            public event Action? Connected;
            public event Action<DisconnectReason>? Disconnected;
            public event TransportMessageHandler? Received;

            public void Connect(string host, int port) => _connectPending = true;

            public void Poll()
            {
                if (_connectPending)
                {
                    _connectPending = false;
                    _connected = true;
                    Connected?.Invoke();
                    Deliver(MessageCodec.EncodeChallenge(new ChallengeMessage(
                        _nonce, _publicKey.Modulus!, _publicKey.Exponent!)));
                    return;
                }

                if (_incoming.Count > 0)
                {
                    Deliver(_incoming.Dequeue());
                }
            }

            public void Send(byte[] data, int length, DeliveryMode mode)
            {
                if (length == 0)
                {
                    return;
                }

                if (data[0] == (byte)MessageType.Hello)
                {
                    _incoming.Enqueue(MessageCodec.EncodeWelcome(new WelcomeMessage(
                        ProtocolConstants.Version,
                        (ushort)SimulationConstants.TickRate,
                        42,
                        0,
                        0,
                        -1.5f,
                        -2.5f,
                        4f,
                        0.4f,
                        0.4f,
                        new byte[ProtocolConstants.MapHashSize])));
                    _incoming.Enqueue(MessageCodec.EncodeItemCatalog(new ItemCatalogMessage(
                        1.5f,
                        10,
                        1.2f,
                        0.8f,
                        new[]
                        {
                            new ItemCatalogEntry(3101, ItemKind.Weapon, 1, 0, 14, 1.2f, 0.8f, "Espada corta"),
                            new ItemCatalogEntry(3102, ItemKind.Weapon, 1, 0, 22, 1.3f, 1.2f, "Hacha de orco"),
                        })));
                    _incoming.Enqueue(MessageCodec.EncodeInventory(
                        new InventoryMessage(1, new ushort[] { 3102 }, new ushort[] { 1 })));
                }
                else if (data[0] == (byte)MessageType.Equip)
                {
                    EquipSent = MessageCodec.TryReadEquip(data, 0, length, out EquipMessage equip) && equip.Slot == 0;
                    _incoming.Enqueue(MessageCodec.EncodeInventory(
                        new InventoryMessage(1, new ushort[] { 0 }, new ushort[] { 0 }, 3102)));
                    _incoming.Enqueue(MessageCodec.EncodeSnapshot(new SnapshotMessage(
                        1,
                        0,
                        new[]
                        {
                            new EntityState(42, 0, 0, 0, 0, EntityKind.Player, 100, EntityAction.None),
                            new EntityState(100, 80, 0, 0, 0, EntityKind.Npc, 100, EntityAction.None, 2002),
                        })));
                }
                else if (data[0] == (byte)MessageType.Attack)
                {
                    AttackSent = MessageCodec.TryReadAttack(data, 0, length, out AttackMessage attack) && attack.TargetId == 100;
                }
            }

            public void Disconnect()
            {
                if (_connected)
                {
                    _connected = false;
                    Disconnected?.Invoke(DisconnectReason.LocalShutdown);
                }
            }

            public void Dispose()
            {
            }

            private void Deliver(byte[] message) => Received?.Invoke(0, message, 0, message.Length);
        }
    }
}
