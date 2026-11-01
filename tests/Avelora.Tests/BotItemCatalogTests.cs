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
        public void Bot_Contabiliza_Listas_Y_Cambios_De_Zona_Recibidos()
        {
            var transport = new SightTransport(sendZoneTelemetry: true);
            using var bot = new BotClient(
                1, "127.0.0.1", 7777, new byte[ProtocolConstants.MapHashSize],
                seed: 17, runMs: 500, accountPrefix: "bot-zone", password: "clave",
                invitationCode: "invitacion", behavior: BotBehavior.Random,
                transportFactory: () => transport);

            bot.Run(CancellationToken.None);

            Assert.Equal(1, bot.ZoneLists);
            Assert.Equal(3, bot.ZoneChanges);
            Assert.Equal(1, bot.SafeZoneEntries);
            Assert.Equal(1, bot.PvpZoneEntries);
        }

        [Fact]
        public void Bot_Cuenta_Estados_Y_Rechazos_Pvp_Y_Pide_Modo_Al_Estar_Listo()
        {
            var transport = new SightTransport(sendPvpTelemetry: true);
            using var bot = new BotClient(
                1, "127.0.0.1", 7777, new byte[ProtocolConstants.MapHashSize],
                seed: 17, runMs: 300, accountPrefix: "bot-pvp", password: "clave",
                invitationCode: "invitacion", behavior: BotBehavior.Random,
                transportFactory: () => transport, pvp: true);

            bot.Run(CancellationToken.None);

            Assert.Equal(1, bot.PvpStatuses);
            Assert.Equal(1, bot.PvpRejects);
            Assert.Equal(1, bot.PvpModeRequests);
            Assert.True(transport.PvpModeSent);
        }

        [Fact]
        public void Fight_Pvp_Espera_Quieto_Mientras_Cuenta_La_Zona_Segura()
        {
            var transport = new SightTransport(sendPvpWaiting: true);
            using var bot = new BotClient(
                1, "127.0.0.1", 7777, new byte[ProtocolConstants.MapHashSize],
                seed: 17, runMs: 300, accountPrefix: "bot-pvp", password: "clave",
                invitationCode: "invitacion", behavior: BotBehavior.Random,
                transportFactory: () => transport, pvp: true);

            bot.Run(CancellationToken.None);

            Assert.Equal(0, transport.PositiveMoveInputs);
            Assert.Equal(0, bot.PvpModeRequests);
        }

        [Fact]
        public void Fight_Sin_Pvp_No_Espera_Por_Estado_Pvp()
        {
            var transport = new SightTransport(sendPvpWaiting: true);
            using var bot = new BotClient(
                1, "127.0.0.1", 7777, new byte[ProtocolConstants.MapHashSize],
                seed: 17, runMs: 300, accountPrefix: "bot-pvp", password: "clave",
                invitationCode: "invitacion", behavior: BotBehavior.Random,
                transportFactory: () => transport, pvp: false);

            bot.Run(CancellationToken.None);

            Assert.True(transport.PositiveMoveInputs > 0);
        }

        [Fact]
        public void Fight_Pvp_Incluye_Enemigo_Jugador_Pvp_Visible()
        {
            var transport = new SightTransport(sendPvpPlayer: true);
            using var bot = new BotClient(
                1, "127.0.0.1", 7777, new byte[ProtocolConstants.MapHashSize],
                seed: 17, runMs: 300, accountPrefix: "bot-pvp", password: "clave",
                invitationCode: "invitacion", behavior: BotBehavior.Fight,
                transportFactory: () => transport, pvp: true);

            bot.Run(CancellationToken.None);

            Assert.Equal(43, transport.LastAttackTarget);
        }

        [Fact]
        public void Fight_Equipa_Arma_Aunque_FistDamage_Con_Bono_Supere_Su_Daño_Base()
        {
            var transport = new ArmaConBonoTransport();
            using var bot = new BotClient(
                1, "127.0.0.1", 7777, new byte[ProtocolConstants.MapHashSize],
                seed: 17, runMs: 500, accountPrefix: "bot", password: "clave",
                invitationCode: "invitacion", behavior: BotBehavior.Fight,
                transportFactory: () => transport);

            bot.Run(CancellationToken.None);

            Assert.True(transport.EquipEnviado);
            Assert.Equal(1, bot.Equipped);
        }

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
            Assert.True(bot.EquipOmittedRequirements >= 2);
            Assert.Equal(1, bot.ArmorsEquipped);
            Assert.Equal((byte)3, bot.Level);
            Assert.Equal(2, bot.LevelUps);
            Assert.Equal((byte)3, bot.MaxLevelReached);
            Assert.Equal(3, bot.PointsAllocated);
            Assert.True(bot.StatsReceived >= 3);
            Assert.Equal((byte)1, bot.ClassId);
            Assert.Equal(1, bot.ElitesSeen);
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
            Assert.Equal(1, bot.LootMessages);
            Assert.Equal(10, bot.LootExperience);
            Assert.Equal(20, bot.LootGold);
            Assert.Equal(2, bot.LootItems);
            Assert.Equal(3, bot.LootOnGround);
            Assert.Equal(123456u, bot.Gold);
        }

        [Fact]
        public void Fight_No_Equipa_Arma_Dos_Manos_Con_Offhand_Y_Sin_Hueco_Libre()
        {
            var transport = new CatalogTransport(blockTwoHanded: true);
            using var bot = new BotClient(
                3, "127.0.0.1", 7777, new byte[ProtocolConstants.MapHashSize],
                seed: 19, runMs: 400, accountPrefix: "bot", password: "clave", invitationCode: "invitacion",
                behavior: BotBehavior.Fight, transportFactory: () => transport);

            bot.Run(CancellationToken.None);

            Assert.False(transport.EquipSent);
            Assert.True(bot.EquipOmittedRequirements > 0);
        }

        [Theory]
        [InlineData(true, false, -1, 1)]
        [InlineData(false, false, 0, 0)]
        [InlineData(true, true, 1, 1)]
        public void Fight_Respeta_Offhand_Y_Continua_Con_Otra_Armadura(
            bool dosManos, bool conCasco, int slotEsperado, long omitidos)
        {
            var transport = new CatalogTransport(armorScenario: true, twoHanded: dosManos, withHead: conCasco);
            using var bot = new BotClient(
                4, "127.0.0.1", 7777, new byte[ProtocolConstants.MapHashSize],
                seed: 19, runMs: 250, accountPrefix: "bot", password: "clave", invitationCode: "invitacion",
                behavior: BotBehavior.Fight, transportFactory: () => transport);

            bot.Run(CancellationToken.None);

            Assert.Equal(slotEsperado, transport.RequestedArmorSlot);
            Assert.Equal(omitidos, bot.EquipOmittedRequirements);
            if (slotEsperado < 0)
                Assert.Equal((ushort)0, (ushort)typeof(BotClient).GetField("_pendingEquipItemId",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(bot)!);
        }

        [Fact]
        public void Fight_Con_Pared_No_Ataca_Y_Sigue_Caminando_Hasta_Tener_Vista()
        {
            MapCollisionGrid map = MapCollisionGrid.FromRows(new[]
            {
                "...#....", "...#....", "...#....", "...#....", "...#....",
            });
            var transport = new SightTransport();
            using var bot = NewSightBot(map, transport);

            bot.Run(CancellationToken.None);

            Assert.Equal(0, transport.AttacksSent);
            Assert.True(transport.PositiveMoveInputs > 0);
            Assert.True(bot.AttacksSkippedLineOfSight > 0);
        }

        [Fact]
        public void Fight_Con_Vista_Se_Pára_Ataca_Y_Procesa_CombatEvent()
        {
            var transport = new SightTransport();
            using var bot = NewSightBot(MapCollisionGrid.AllWalkable(8, 5), transport);

            bot.Run(CancellationToken.None);

            Assert.True(transport.AttacksSent > 0);
            Assert.Equal(0, transport.PositiveMoveInputs);
            Assert.Equal(0, bot.AttacksSkippedLineOfSight);
            Assert.Equal(1, bot.CombatEventsReceived);
        }

        private static BotClient NewSightBot(MapCollisionGrid map, SightTransport transport) => new BotClient(
            5, "127.0.0.1", 7777, new byte[ProtocolConstants.MapHashSize], seed: 71, runMs: 250,
            accountPrefix: "bot-sight", password: "clave", invitationCode: "invitacion",
            behavior: BotBehavior.Fight, transportFactory: () => transport, map: map);

        [Fact]
        public void Bot_Cuenta_Mensajes_De_Habilidades_Y_Recurso_Codificados()
        {
            var transport = new SightTransport(sendSkillTelemetry: true);
            using var bot = new BotClient(1, "127.0.0.1", 7777, new byte[ProtocolConstants.MapHashSize],
                seed: 17, runMs: 300, accountPrefix: "bot", password: "clave",
                invitationCode: "invitacion", behavior: BotBehavior.Random, transportFactory: () => transport);
            bot.Run(CancellationToken.None);
            Assert.Equal(2, bot.SkillsOk);
            Assert.Equal(1, bot.SkillsRejected);
            Assert.Equal(2, bot.ResourceMessages);
        }

        private sealed class SightTransport : IClientTransport
        {
            private readonly Queue<byte[]> _incoming = new Queue<byte[]>();
            private readonly byte[] _nonce = new byte[HelloSecrets.NonceSize];
            private readonly RSAParameters _publicKey;
            private bool _connectPending;
            private bool _connected;
            private readonly bool _sendZoneTelemetry;
            private readonly bool _sendSkillTelemetry;
            private readonly bool _sendPvpTelemetry;
            private readonly bool _sendPvpPlayer;
            private readonly bool _sendPvpWaiting;

            public SightTransport(
                bool sendZoneTelemetry = false,
                bool sendPvpTelemetry = false,
                bool sendPvpPlayer = false,
                bool sendPvpWaiting = false, bool sendSkillTelemetry = false)
            {
                _sendZoneTelemetry = sendZoneTelemetry;
                _sendPvpTelemetry = sendPvpTelemetry;
                _sendPvpPlayer = sendPvpPlayer;
                _sendPvpWaiting = sendPvpWaiting;
                _sendSkillTelemetry = sendSkillTelemetry;
                using RSA rsa = RSA.Create(2048);
                _publicKey = rsa.ExportParameters(includePrivateParameters: false);
            }

            public bool IsConnected => _connected;
            public int AttacksSent { get; private set; }
            public int PositiveMoveInputs { get; private set; }
            public bool PvpModeSent { get; private set; }
            public int LastAttackTarget { get; private set; } = -1;
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

                while (_incoming.Count > 0)
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
                        1.5f, 2.5f, 4f, 0.4f, 0.4f, new byte[ProtocolConstants.MapHashSize])));
                    if (_sendPvpWaiting)
                    {
                        _incoming.Enqueue(MessageCodec.EncodePvpStatus(new PvpStatusMessage(
                            PvpMode.Pve, PvpForcedState.None, PvpChangeState.Waiting, 0, 5)));
                    }
                    if (_sendZoneTelemetry)
                    {
                        _incoming.Enqueue(MessageCodec.EncodeZoneList(new ZoneListMessage(new[]
                        {
                            new ZoneInfo(1, ZoneKind.Safe, new[] { new ZoneRect(0, 0, 1, 1) }),
                            new ZoneInfo(2, ZoneKind.Pvp, new[] { new ZoneRect(2, 0, 1, 1) }),
                        })));
                        _incoming.Enqueue(MessageCodec.EncodeZoneChanged(new ZoneChangedMessage(1, ZoneKind.Safe)));
                        _incoming.Enqueue(MessageCodec.EncodeZoneChanged(new ZoneChangedMessage(2, ZoneKind.Pvp)));
                        _incoming.Enqueue(MessageCodec.EncodeZoneChanged(new ZoneChangedMessage(0, ZoneKind.Normal)));
                    }

                    if (_sendSkillTelemetry)
                    {
                        _incoming.Enqueue(MessageCodec.EncodeSkillUsed(new SkillUsedMessage(4001, 1)));
                        _incoming.Enqueue(MessageCodec.EncodeSkillUsed(new SkillUsedMessage(4001, 1)));
                        _incoming.Enqueue(MessageCodec.EncodeSkillRejected(new SkillRejectedMessage(4001, SkillRejectReason.Cooldown)));
                        _incoming.Enqueue(MessageCodec.EncodeResourceStatus(new ResourceStatusMessage(30, 100)));
                        _incoming.Enqueue(MessageCodec.EncodeResourceStatus(new ResourceStatusMessage(34, 100)));
                    }
                    if (_sendPvpTelemetry)
                    {
                        _incoming.Enqueue(MessageCodec.EncodePvpStatus(new PvpStatusMessage(
                            PvpMode.Pve, PvpForcedState.None, PvpChangeState.Ready, 0, 0)));
                        _incoming.Enqueue(MessageCodec.EncodePvpModeRejected(
                            new PvpModeRejectedMessage(PvpModeRejectReason.NotInSafeZone)));
                    }

                    _incoming.Enqueue(MessageCodec.EncodeItemCatalog(new ItemCatalogMessage(
                        1.5f, 10, 6f, 0.8f, Array.Empty<ItemCatalogEntry>())));
                    _incoming.Enqueue(MessageCodec.EncodeInventory(new InventoryMessage(
                        0, Array.Empty<ushort>(), Array.Empty<ushort>())));
                    _incoming.Enqueue(MessageCodec.EncodeSnapshot(new SnapshotMessage(1, 0,
                        _sendPvpPlayer
                            ? new[]
                            {
                                new EntityState(42, 96, 160, 0, 0, EntityKind.Player, 100, EntityAction.None),
                                new EntityState(43, 112, 160, 0, 0, EntityKind.Player, 100, EntityAction.None, pvp: true),
                                new EntityState(100, 352, 160, 0, 0, EntityKind.Npc, 100, EntityAction.None, 2001),
                            }
                            : new[]
                            {
                                new EntityState(42, 96, 160, 0, 0, EntityKind.Player, 100, EntityAction.None),
                                new EntityState(100, 352, 160, 0, 0, EntityKind.Npc, 100, EntityAction.None, 2001),
                            })));
                    _incoming.Enqueue(MessageCodec.EncodeCombatEvent(new CombatEventMessage(new[]
                    {
                        new CombatEventEntry(42, 100, 10, CombatEventFlags.Ranged),
                    })));
                }
                else if (data[0] == (byte)MessageType.Attack)
                {
                    AttacksSent++;
                    if (MessageCodec.TryReadAttack(data, 0, length, out AttackMessage attack))
                    {
                        LastAttackTarget = attack.TargetId;
                    }
                }
                else if (MessageCodec.TryReadSetPvpMode(data, 0, length, out _))
                {
                    PvpModeSent = true;
                }
                else if (MessageCodec.TryReadMoveInput(data, 0, length, out MoveInputMessage input) &&
                    input.TryGetDirection(0, out sbyte directionX, out _) && directionX > 0)
                {
                    PositiveMoveInputs++;
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

        private sealed class ArmaConBonoTransport : IClientTransport
        {
            private readonly Queue<byte[]> _incoming = new Queue<byte[]>();
            private readonly byte[] _nonce = new byte[HelloSecrets.NonceSize];
            private readonly RSAParameters _publicKey;
            private bool _connectPending;
            private bool _connected;

            public ArmaConBonoTransport()
            {
                using RSA rsa = RSA.Create(2048);
                _publicKey = rsa.ExportParameters(includePrivateParameters: false);
            }

            public bool IsConnected => _connected;
            public bool EquipEnviado { get; private set; }
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

                while (_incoming.Count > 0)
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
                        1.5f, 2.5f, 4f, 0.4f, 0.4f, new byte[ProtocolConstants.MapHashSize])));
                    _incoming.Enqueue(MessageCodec.EncodeItemCatalog(new ItemCatalogMessage(
                        1.5f, 13, 1.2f, 0.8f, new[]
                        {
                            new ItemCatalogEntry(4101, ItemKind.Weapon, 1, 0, 12, 6f, 0.8f,
                                "Arma ficticia", 0, weaponType: "TipoA", usableByClass: true,
                                requiredLevel: 1, twoHanded: false),
                        })));
                    _incoming.Enqueue(MessageCodec.EncodeInventory(new InventoryMessage(
                        1, new ushort[] { 4101 }, new ushort[] { 1 })));
                }
                else if (data[0] == (byte)MessageType.Equip)
                {
                    Assert.True(MessageCodec.TryReadEquip(data, 0, length, out EquipMessage equip));
                    Assert.Equal((byte)0, equip.Slot);
                    EquipEnviado = true;
                    var equipment = new ushort[ProtocolConstants.EquipSlotCount];
                    equipment[(int)EquipSlot.Weapon] = 4101;
                    _incoming.Enqueue(MessageCodec.EncodeInventory(new InventoryMessage(
                        1, new ushort[] { 0 }, new ushort[] { 0 }, equipment)));
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

        private sealed class CatalogTransport : IClientTransport
        {
            private readonly Queue<byte[]> _incoming = new Queue<byte[]>();
            private readonly byte[] _nonce = new byte[HelloSecrets.NonceSize];
            private readonly RSAParameters _publicKey;
            private bool _connectPending;
            private bool _connected;

            private readonly bool _blockTwoHanded;

            private readonly bool _armorScenario;
            private readonly bool _twoHanded;
            private readonly bool _withHead;
            public int RequestedArmorSlot { get; private set; } = -1;

            public CatalogTransport(bool blockTwoHanded = false, bool armorScenario = false,
                bool twoHanded = false, bool withHead = false)
            {
                _blockTwoHanded = blockTwoHanded;
                _armorScenario = armorScenario;
                _twoHanded = twoHanded;
                _withHead = withHead;
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
                    if (_armorScenario)
                    {
                        var equipment = new ushort[ProtocolConstants.EquipSlotCount];
                        equipment[(int)EquipSlot.Weapon] = 4101;
                        _incoming.Enqueue(MessageCodec.EncodeItemCatalog(new ItemCatalogMessage(
                            1.5f, 10, 1.2f, 0.8f, new[]
                            {
                                new ItemCatalogEntry(4101, ItemKind.Weapon, 1, 0, 50, 2, 1, "Arma ficticia", 0,
                                    weaponType: "TipoA", twoHanded: _twoHanded),
                                new ItemCatalogEntry(4201, ItemKind.Armor, 1, 0, 0, 0, 0, "Escudo ficticio", (byte)EquipSlot.Offhand),
                                new ItemCatalogEntry(4202, ItemKind.Armor, 1, 0, 0, 0, 0, "Casco ficticio", (byte)EquipSlot.Head),
                            })));
                        _incoming.Enqueue(MessageCodec.EncodeInventory(new InventoryMessage(
                            2, new ushort[] { 4201, _withHead ? (ushort)4202 : (ushort)0 },
                            new ushort[] { 1, _withHead ? (ushort)1 : (ushort)0 }, equipment)));
                        return;
                    }
                    if (_blockTwoHanded)
                    {
                        var equipment = new ushort[ProtocolConstants.EquipSlotCount];
                        equipment[(int)EquipSlot.Offhand] = 4201;
                        _incoming.Enqueue(MessageCodec.EncodeItemCatalog(new ItemCatalogMessage(
                            1.5f, 10, 1.2f, 0.8f,
                            new[]
                            {
                                new ItemCatalogEntry(4101, ItemKind.Weapon, 1, 0, 50, 2, 1, "Dos manos", 0,
                                    weaponType: "ClaseA", twoHanded: true),
                                new ItemCatalogEntry(4201, ItemKind.Armor, 1, 0, 0, 0, 0, "Offhand", 1),
                            })));
                        _incoming.Enqueue(MessageCodec.EncodeInventory(
                            new InventoryMessage(1, new ushort[] { 4101 }, new ushort[] { 1 }, equipment)));
                        _incoming.Enqueue(MessageCodec.EncodeProgress(new ProgressMessage(1, 0, 100, 100)));
                        return;
                    }

                    _incoming.Enqueue(MessageCodec.EncodeItemCatalog(new ItemCatalogMessage(
                        1.5f,
                        10,
                        1.2f,
                        0.8f,
                        new[]
                        {
                            new ItemCatalogEntry(3102, ItemKind.Weapon, 1, 0, 22, 1.2f, 1.2f, "Arma ajena", 0, weaponType: "ClaseB", usableByClass: false),
                            new ItemCatalogEntry(3103, ItemKind.Weapon, 1, 0, 50, 2.5f, 1.2f, "Arma de nivel alto", 0, weaponType: "ClaseA", requiredLevel: 3),
                        })));
                    _incoming.Enqueue(MessageCodec.EncodeItemCatalog(new ItemCatalogMessage(
                        1.5f,
                        10,
                        1.2f,
                        0.8f,
                        new[]
                        {
                            new ItemCatalogEntry(3101, ItemKind.Weapon, 1, 0, 14, 3.0f, 0.8f, "Arma permitida", 0, weaponType: "ClaseA", usableByClass: true),
                            new ItemCatalogEntry(3301, ItemKind.Armor, 1, 0, 0, 0, 0, "Casco", 2, 2),
                        })));
                    _incoming.Enqueue(MessageCodec.EncodeInventory(new InventoryMessage(
                        4, new ushort[] { 3102, 3103, 3101, 3301 }, new ushort[] { 1, 1, 1, 1 })));
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
                    if (_armorScenario)
                    {
                        Assert.True(MessageCodec.TryReadEquip(data, 0, length, out EquipMessage armor));
                        RequestedArmorSlot = armor.Slot;
                        return;
                    }
                    bool valid = MessageCodec.TryReadEquip(data, 0, length, out EquipMessage equip);
                    EquipSent |= valid && equip.Slot == 2;
                    ArmorEquipSent |= valid && equip.Slot == 3;
                    var equipment = new ushort[ProtocolConstants.EquipSlotCount];
                    equipment[0] = 3101;
                    if (valid && equip.Slot == 2)
                    {
                        _incoming.Enqueue(MessageCodec.EncodeInventory(new InventoryMessage(
                            4, new ushort[] { 3102, 3103, 0, 3301 }, new ushort[] { 1, 1, 0, 1 }, equipment)));
                    }
                    else if (valid && equip.Slot == 3)
                    {
                        equipment[(int)EquipSlot.Head] = 3301;
                        _incoming.Enqueue(MessageCodec.EncodeInventory(new InventoryMessage(
                            4, new ushort[] { 3102, 3103, 0, 0 }, new ushort[] { 1, 1, 0, 0 }, equipment)));
                        _incoming.Enqueue(MessageCodec.EncodeProgress(new ProgressMessage(3, 0, 0, 130)));
                        _incoming.Enqueue(MessageCodec.EncodeStats(new StatsMessage(
                            1, 3, 4, 3, 1, 10, 0, 0, 0, 0, 0, 100, 24, 0, 0, 0, 100, 100, 1)));
                        EntityState[] states =
                        {
                            new EntityState(42, 0, 0, 0, 0, EntityKind.Player, 100, EntityAction.None),
                            new EntityState(100, 80, 0, 0, 0, EntityKind.Npc, 100, EntityAction.None, 2002, rank: 1),
                        };
                        _incoming.Enqueue(MessageCodec.EncodeSnapshot(new SnapshotMessage(1, 0, states)));
                        _incoming.Enqueue(MessageCodec.EncodeSnapshot(new SnapshotMessage(2, 0, states)));
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
                        1, new ushort[] { 3001 }, new ushort[] { 2 },
                        new ushort[ProtocolConstants.EquipSlotCount], 123456)));
                    _incoming.Enqueue(MessageCodec.EncodeLootGained(new LootGainedMessage(
                        10, 20, new[]
                        {
                            new LootItemEntry(3001, 2, false),
                            new LootItemEntry(3002, 3, true),
                        })));
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
