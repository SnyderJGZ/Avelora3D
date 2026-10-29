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
                runMs: 500,
                accountPrefix: "bot",
                password: "clave",
                invitationCode: "invitacion",
                behavior: BotBehavior.Fight,
                transportFactory: () => transport);

            bot.Run(CancellationToken.None);

            Assert.True(transport.EquipSent);
            Assert.True(transport.AttackSent,
                "El bot debe usar el alcance del hacha equipada, no el de los puños. ataques=" + bot.SentAttacks +
                ", arma=" + transport.EquipSent + ", armadura=" + transport.ArmorEquipSent +
                ", equipadas=" + bot.ArmorsEquipped + ", pos=" + bot.X + ";" + bot.Y);
            Assert.True(transport.AllocateSent, "El bot debe repartir los puntos según el atributo base más alto.");
            Assert.True(transport.ArmorEquipSent, "El bot debe equipar la armadura recogida si el hueco está vacío.");
            Assert.Equal(1, bot.Equipped);
            Assert.Equal(1, bot.ArmorsEquipped);
            Assert.Equal((byte)3, bot.Level);
            Assert.Equal(2, bot.LevelUps);
            Assert.Equal((byte)3, bot.MaxLevelReached);
            Assert.Equal(3, bot.PointsAllocated);
            Assert.True(bot.StatsReceived >= 3);
            Assert.Equal((byte)1, bot.ClassId);
        }

        [Fact]
        public void Inventory_Recoge_Botin_Y_Envia_Acciones_Aleatorias_Sin_Contarse_Como_Abusivo()
        {
            var transport = new InventoryBehaviorTransport();
            using var bot = new BotClient(
                2,
                "127.0.0.1",
                7777,
                new byte[ProtocolConstants.MapHashSize],
                seed: 1714,
                runMs: 1100,
                accountPrefix: "bot-inventory",
                password: "clave",
                invitationCode: "invitacion",
                behavior: BotBehavior.Inventory,
                transportFactory: () => transport);

            bot.Run(CancellationToken.None);

            Assert.False(BotClient.IsAbusive(BotBehavior.Inventory));
            Assert.True(transport.PickupSent);
            Assert.True(transport.InventoryActionCount >= 1);
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
        public bool ArmorEquipSent { get; private set; }
        public bool AttackSent { get; private set; }
        public bool AllocateSent { get; private set; }
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
                            new ItemCatalogEntry(3101, ItemKind.Weapon, 1, 0, 14, 1.2f, 0.8f, "Espada corta", 0),
                            new ItemCatalogEntry(3102, ItemKind.Weapon, 1, 0, 22, 1.3f, 1.2f, "Hacha de orco", 0),
                            new ItemCatalogEntry(3301, ItemKind.Armor, 1, 0, 0, 0, 0, "Casco", 2, 2),
                        })));
                    _incoming.Enqueue(MessageCodec.EncodeInventory(
                        new InventoryMessage(2, new ushort[] { 3102, 3301 }, new ushort[] { 1, 1 })));
                    _incoming.Enqueue(MessageCodec.EncodeProgress(new ProgressMessage(1, 0, 100, 100)));
                    _incoming.Enqueue(MessageCodec.EncodeStats(new StatsMessage(
                        1, 1, 4, 3, 1, 10, 0, 0, 0, 0, 3, 100, 12, 0, 0, 0, 100, 100, 1)));
                }
                else if (data[0] == (byte)MessageType.AllocatePoints)
                {
                    AllocateSent = MessageCodec.TryReadAllocatePoints(data, 0, length, out AllocatePointsMessage points) &&
                        points.Str == 2 && points.Dex == 0 && points.Int == 0 && points.Vit == 1;
                    _incoming.Enqueue(MessageCodec.EncodeStats(new StatsMessage(
                        1, 1, 6, 3, 1, 11, 2, 0, 0, 1, 0, 105, 13, 0, 0, 0, 100, 100, 1)));
                }
                else if (data[0] == (byte)MessageType.Equip)
                {
                    bool valid = MessageCodec.TryReadEquip(data, 0, length, out EquipMessage equip);
                    EquipSent |= valid && equip.Slot == 0;
                    ArmorEquipSent |= valid && equip.Slot == 1;
                    var equipment = new ushort[ProtocolConstants.EquipSlotCount];
                    equipment[0] = 3102;
                    if (valid && equip.Slot == 0)
                    {
                        _incoming.Enqueue(MessageCodec.EncodeInventory(new InventoryMessage(
                            2, new ushort[] { 0, 3301 }, new ushort[] { 0, 1 }, equipment)));
                    }
                    else if (valid && equip.Slot == 1)
                    {
                        equipment[(int)EquipSlot.Head] = 3301;
                        _incoming.Enqueue(MessageCodec.EncodeInventory(new InventoryMessage(
                            2, new ushort[] { 0, 0 }, new ushort[] { 0, 0 }, equipment)));
                        _incoming.Enqueue(MessageCodec.EncodeProgress(new ProgressMessage(3, 0, 0, 130)));
                        _incoming.Enqueue(MessageCodec.EncodeStats(new StatsMessage(
                            1, 3, 4, 3, 1, 10, 0, 0, 0, 0, 0, 100, 24, 0, 0, 0, 100, 100, 1)));
                        _incoming.Enqueue(MessageCodec.EncodeSnapshot(new SnapshotMessage(
                            1,
                            0,
                            new[]
                            {
                                new EntityState(42, 0, 0, 0, 0, EntityKind.Player, 100, EntityAction.None),
                                new EntityState(100, 80, 0, 0, 0, EntityKind.Npc, 100, EntityAction.None, 2002),
                            })));
                    }
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

        private sealed class InventoryBehaviorTransport : IClientTransport
        {
            private readonly Queue<byte[]> _incoming = new Queue<byte[]>();
            private readonly byte[] _nonce = new byte[HelloSecrets.NonceSize];
            private readonly RSAParameters _publicKey;
            private bool _connectPending;
            private bool _connected;

            public InventoryBehaviorTransport()
            {
                using RSA rsa = RSA.Create(2048);
                _publicKey = rsa.ExportParameters(includePrivateParameters: false);
            }

            public bool PickupSent { get; private set; }
            public int InventoryActionCount { get; private set; }
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
                        ProtocolConstants.Version, (ushort)SimulationConstants.TickRate, 42, 0, 0,
                        0, 0, 4, 0.4f, 0.4f, new byte[ProtocolConstants.MapHashSize])));
                    _incoming.Enqueue(MessageCodec.EncodeItemCatalog(new ItemCatalogMessage(
                        1.5f, 10, 1.2f, 0.8f,
                        new[] { new ItemCatalogEntry(3001, ItemKind.Material, 99, 0, 0, 0, 0, "Colmillo") })));
                    _incoming.Enqueue(MessageCodec.EncodeInventory(new InventoryMessage(
                        1, new ushort[] { 3001 }, new ushort[] { 2 })));
                    _incoming.Enqueue(MessageCodec.EncodeSnapshot(new SnapshotMessage(
                        1,
                        0,
                        new[]
                        {
                            new EntityState(42, 0, 0, 0, 0, EntityKind.Player, 100, EntityAction.None),
                            new EntityState(90, 0, 0, 0, 0, EntityKind.Item, 100, EntityAction.None, 3001),
                        })));
                }
                else if (data[0] == (byte)MessageType.PickUp)
                {
                    PickupSent = MessageCodec.TryReadPickUp(data, 0, length, out PickUpMessage pickup) &&
                        pickup.EntityId == 90;
                }
                else if (data[0] == (byte)MessageType.MoveItem ||
                    data[0] == (byte)MessageType.Equip ||
                    data[0] == (byte)MessageType.Unequip ||
                    data[0] == (byte)MessageType.DropItem ||
                    data[0] == (byte)MessageType.DestroyItem)
                {
                    InventoryActionCount++;
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

            public void Dispose() { }

            private void Deliver(byte[] message) => Received?.Invoke(0, message, 0, message.Length);
        }
    }
}
