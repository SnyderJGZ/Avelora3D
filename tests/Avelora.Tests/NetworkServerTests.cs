using System;
using System.Collections.Generic;
using System.IO;
using Avelora.Net;
using Avelora.Server.Configuration;
using Avelora.Server.Entities;
using Avelora.Server.Items;
using Avelora.Server.Net;
using Avelora.Server.Npcs;
using Avelora.Server.Simulation;
using Avelora.Shared;
using Avelora.Shared.Maps;
using Avelora.Shared.Protocol;
using Xunit;

namespace Avelora.Tests
{
    /// <summary>
    /// Pruebas de la capa de sesiones sin sockets: se sustituye el transporte por uno falso y se
    /// llaman los mismos métodos que llamarían los dos hilos reales.
    /// </summary>
    public class NetworkServerTests
    {
        private static string MapPath =>
            Path.Combine(AppContext.BaseDirectory, "maps", "avelora-etapa-a.avmap");

        private static Zone NewZone(ServerConfig config, bool withItems = false, NpcCatalog? npcs = null)
        {
            MapCollisionGrid map = MapFile.Load(MapPath);
            var items = withItems
                ? new ItemCatalog
                {
                    Items = new List<ItemType>
                    {
                        new ItemType { Id = 3001, Name = "Colmillo", Kind = "material", MaxStack = 99 },
                        new ItemType { Id = 3002, Name = "Poción", Kind = "potion", MaxStack = 10, Heal = 30 },
                        new ItemType { Id = 3101, Name = "Espada corta", Kind = "weapon", MaxStack = 1, Damage = 14, Range = 1.2f, CooldownSeconds = 0.8d },
                    },
                }
                : null;

            return new Zone(
                1,
                map,
                new EntityStore(),
                new EntityIdAllocator(),
                new InputQueue(config.MaxQueuedIntents),
                new MovementSystem(config.PlayerSpeed, SimulationConstants.FixedDt),
                config.MaxIntentsPerTick,
                config.MaxIntentsPerPlayerPerTick,
                new InterestSettings(config.InterestRadius, config.InterestCellSize, config.InterestHysteresis),
                npcs: npcs,
                npcSeed: 12345,
                attacks: new AttackQueue(config.MaxQueuedAttacks),
                combat: new CombatSettings(
                    config.PlayerAttackDamage,
                    config.PlayerAttackRange,
                    config.AttackRangeTolerance,
                    config.PlayerAttackCooldownSeconds),
                playerVitals: config.PlayerVitals,
                items: items,
                itemActions: withItems ? new ItemActionQueue(config.MaxQueuedItemActions) : null,
                inventorySlots: config.InventorySlots,
                maxGroundItems: config.MaxGroundItems,
                groundItemTicks: (long)Math.Ceiling(config.GroundItemSeconds * SimulationConstants.TickRate),
                pickupRange: config.PickupRange,
                pickupRangeTolerance: config.AttackRangeTolerance,
                potionCooldownTicks: (long)Math.Ceiling(config.PotionCooldownSeconds * SimulationConstants.TickRate));
        }

        private sealed class Harness : IDisposable
        {
            private readonly Dictionary<int, byte[]> _challengeNonces = new Dictionary<int, byte[]>();

            public Harness(
                Action<ServerConfig>? configure = null,
                Func<long>? clock = null,
                bool withItems = false,
                NpcCatalog? npcs = null)
            {
                Config = new ServerConfig { HandshakeTimeoutSeconds = 60d };
                configure?.Invoke(Config);
                Zone = NewZone(Config, withItems, npcs);
                MapHash = MapFile.ComputeContentHashBytes(Zone.Map);
                Transport = new FakeServerTransport();
                Server = new NetworkServer(Zone, Config, Transport, MapHash, clock: clock);
            }

            public ServerConfig Config { get; }

            public Zone Zone { get; }

            public byte[] MapHash { get; }

            public FakeServerTransport Transport { get; }

            public NetworkServer Server { get; }

            /// <summary>
            /// Simula la conexión y guarda el nonce del <c>Challenge</c> que manda el servidor. El
            /// desafío se retira de <see cref="FakeServerTransport.Sent"/> para que las pruebas
            /// sigan contando solo lo que ocurre después del saludo.
            /// </summary>
            public void Connect(int connectionId)
            {
                Transport.RaiseConnected(connectionId);

                for (int i = Transport.Sent.Count - 1; i >= 0; i--)
                {
                    SentMessage sent = Transport.Sent[i];

                    if (sent.ConnectionId == connectionId &&
                        MessageCodec.TryReadChallenge(sent.Data, 0, sent.Data.Length, out ChallengeMessage challenge))
                    {
                        _challengeNonces[connectionId] = challenge.Nonce;
                        Transport.Sent.RemoveAt(i);
                        return;
                    }
                }

                throw new InvalidOperationException(
                    "La conexión " + connectionId + " no recibió el desafío del servidor.");
            }

            public void Handshake(
                int connectionId,
                ushort? version = null,
                byte[]? mapHash = null,
                string characterName = "dev")
            {
                byte[] encrypted = HelloSecrets.Encrypt(
                    Server.PublicKey,
                    _challengeNonces[connectionId],
                    "avelora-dev",
                    "avelora-dev");

                var hello = new HelloMessage(
                    version ?? ProtocolConstants.Version,
                    (ushort)SimulationConstants.TickRate,
                    mapHash ?? MapHash,
                    4242u + (uint)connectionId,
                    "dev",
                    encrypted,
                    characterName);

                Transport.RaiseReceived(connectionId, MessageCodec.EncodeHello(hello));
            }

            /// <summary>Una vuelta de los dos hilos: altas/bajas y luego envío de lo encolado.</summary>
            public void Pump()
            {
                Server.BeforeTick(Zone);
                Server.PollOnce();
            }

            public void Dispose() => Server.Dispose();
        }

        [Fact]
        public void Al_Saludar_Se_Crea_El_Jugador_Y_Llega_El_Welcome()
        {
            using var harness = new Harness();

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            Assert.Equal(1, harness.Zone.Entities.Count);
            Assert.Equal(4, harness.Transport.Sent.Count);

            SentMessage sent = harness.Transport.Sent[0];
            Assert.Equal(1, sent.ConnectionId);
            Assert.True(MessageCodec.TryReadWelcome(sent.Data, 0, sent.Data.Length, out WelcomeMessage welcome));

            Assert.Equal(ProtocolConstants.Version, welcome.ProtocolVersion);
            Assert.Equal((ushort)SimulationConstants.TickRate, welcome.TickRate);
            Assert.Equal(1, welcome.EntityId);
            Assert.Equal(4243u, welcome.ClientToken);
            Assert.Equal(-1.5f, welcome.SpawnX);
            Assert.Equal(-2.5f, welcome.SpawnY);
            Assert.Equal(harness.Config.PlayerSpeed, welcome.Speed);
            Assert.Equal(harness.Config.PlayerHalfWidth, welcome.HalfWidth);
            Assert.Equal(harness.Config.PlayerHalfHeight, welcome.HalfHeight);
            Assert.Equal(harness.MapHash, welcome.MapHash);
            Assert.True(MessageCodec.TryReadItemCatalog(
                harness.Transport.Sent[1].Data,
                0,
                harness.Transport.Sent[1].Data.Length,
                out ItemCatalogMessage catalog));
            Assert.Equal(harness.Config.PickupRange, catalog.PickupRange);
            Assert.Equal((uint)harness.Config.PlayerAttackDamage, catalog.FistDamage);
            Assert.Empty(catalog.Items);
            Assert.True(MessageCodec.TryReadInventory(
                harness.Transport.Sent[2].Data,
                0,
                harness.Transport.Sent[2].Data.Length,
                out InventoryMessage inventory));
            Assert.Equal((byte)20, inventory.SlotCount);
            Assert.All(inventory.ItemIds, id => Assert.Equal((ushort)0, id));
            Assert.True(MessageCodec.TryReadProgress(
                harness.Transport.Sent[3].Data,
                0,
                harness.Transport.Sent[3].Data.Length,
                out ProgressMessage progress));
            Assert.Equal((byte)1, progress.Level);
            Assert.Equal(0u, progress.Experience);
            Assert.Equal(100u, progress.ExperienceToNext);
            Assert.Equal(100u, progress.MaxHealth);
            Assert.Empty(harness.Transport.DisconnectedConnections);
        }

        [Fact]
        public void Tras_Una_Muerte_Que_Da_Experiencia_Se_Envia_Progress_Nuevo()
        {
            var npcs = new NpcCatalog
            {
                Types = new List<NpcType>
                {
                    new NpcType
                    {
                        Id = 2001,
                        Name = "Orco",
                        Speed = 2f,
                        HalfWidth = 0.35f,
                        HalfHeight = 0.35f,
                        MaxHealth = 10,
                        Experience = 20,
                        Level = 1,
                        RespawnSeconds = 10f,
                        AggroRadius = 0f,
                        LeashRadius = 8f,
                        AttackDamage = 1,
                        AttackRange = 1f,
                        AttackCooldownSeconds = 1.5f,
                    },
                },
                Spawners = new List<NpcSpawner>
                {
                    new NpcSpawner { TypeId = 2001, X = -1.5f, Y = -1.5f, Count = 1, WanderRadius = 1f },
                },
            };

            using var harness = new Harness(npcs: npcs);
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();
            PlayerEntity player = Assert.IsType<PlayerEntity>(
                harness.Zone.Entities[harness.Zone.Entities.Count - 1]);

            harness.Transport.Sent.Clear();
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeAttack(
                new AttackMessage(harness.Zone.Entities[0].Id == player.Id
                    ? harness.Zone.Entities[1].Id
                    : harness.Zone.Entities[0].Id)));
            harness.Zone.Tick();
            harness.Server.PollOnce();

            SentMessage progressSent = Assert.Single(harness.Transport.Sent,
                sent => MessageCodec.TryReadProgress(sent.Data, 0, sent.Data.Length, out _));
            Assert.True(MessageCodec.TryReadProgress(
                progressSent.Data, 0, progressSent.Data.Length, out ProgressMessage progress));
            Assert.Equal(20u, progress.Experience);
            Assert.Equal(100u, progress.ExperienceToNext);
            Assert.Equal(1, harness.Zone.NpcDeaths);
        }

        [Fact]
        public void El_Chat_Se_Entrega_A_Toda_La_Zona_Incluido_El_Emisor()
        {
            using var harness = new Harness(config =>
            {
                config.InterestRadius = 1f;
                config.InterestCellSize = 2f;
                config.InterestHysteresis = 0f;
            });
            harness.Connect(1);
            harness.Handshake(1, characterName: "Alicia");
            harness.Pump();
            harness.Connect(2);
            harness.Handshake(2, characterName: "Bruno");
            harness.Pump();

            ((PlayerEntity)harness.Zone.Entities.Get(1)!).Position = new Vec2(-7f, 5f);
            ((PlayerEntity)harness.Zone.Entities.Get(2)!).Position = new Vec2(7f, -5f);
            harness.Zone.Tick();
            ((PlayerEntity)harness.Zone.Entities.Get(1)!).Action = EntityAction.Dead;
            harness.Transport.Sent.Clear();

            harness.Transport.RaiseReceived(1, MessageCodec.EncodeChatSend(new ChatSendMessage("  Hola  ")));
            harness.Server.PollOnce();

            List<SentMessage> messages = harness.Transport.Sent.FindAll(sent =>
                MessageCodec.TryReadChat(sent.Data, 0, sent.Data.Length, out _));
            Assert.Equal(2, messages.Count);
            Assert.All(messages, sent =>
            {
                Assert.True(MessageCodec.TryReadChat(sent.Data, 0, sent.Data.Length, out ChatMessage chat));
                Assert.Equal(1, chat.SenderEntityId);
                Assert.Equal("Alicia", chat.SenderName);
                Assert.Equal("Hola", chat.Text);
                Assert.Equal(DeliveryMode.ReliableOrdered, sent.Mode);
            });
            Assert.Equal(1, harness.Server.Snapshot().ChatReceived);
            Assert.Equal(2, harness.Server.Snapshot().ChatDelivered);
        }

        [Fact]
        public void El_Nombre_Del_Emisor_Se_Trunca_Sin_Partir_UTF8()
        {
            using var harness = new Harness();
            harness.Connect(1);
            harness.Handshake(1, characterName: "😀😀😀😀😀😀😀");
            harness.Pump();
            harness.Transport.Sent.Clear();

            harness.Transport.RaiseReceived(1, MessageCodec.EncodeChatSend(new ChatSendMessage("hola")));
            harness.Server.PollOnce();

            SentMessage sent = Assert.Single(harness.Transport.Sent);
            Assert.True(MessageCodec.TryReadChat(sent.Data, 0, sent.Data.Length, out ChatMessage chat));
            Assert.Equal("😀😀😀😀😀😀", chat.SenderName);
        }

        [Fact]
        public void El_Chat_Rechaza_Texto_Invalido_Largo_Y_UTF8_Roto_Sin_Enviar()
        {
            using var harness = new Harness();
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();
            harness.Transport.Sent.Clear();

            harness.Transport.RaiseReceived(1, MessageCodec.EncodeChatSend(new ChatSendMessage("   ")));
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeChatSend(new ChatSendMessage("hola\nque tal")));
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeChatSend(new ChatSendMessage("hola\u200B")));
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeChatSend(new ChatSendMessage("hola\u202E")));

            byte[] tooLong = new byte[3 + 121];
            tooLong[0] = (byte)MessageType.ChatSend;
            tooLong[1] = 121;
            harness.Transport.RaiseReceived(1, tooLong);
            harness.Transport.RaiseReceived(1, new byte[] { (byte)MessageType.ChatSend, 1, 0, 0xFF });
            harness.Server.PollOnce();

            Assert.Empty(harness.Transport.Sent);
            NetworkStats stats = harness.Server.Snapshot();
            Assert.Equal(0, stats.ChatReceived);
            Assert.Equal(0, stats.ChatDelivered);
            Assert.Equal(5, stats.ChatRejectedInvalid);
            Assert.Equal(1, stats.ChatRejectedLong);
        }

        [Fact]
        public void El_Chat_Limita_Rafaga_Y_Repone_Una_Ficha_Tras_Un_Segundo_De_Ticks()
        {
            using var harness = new Harness();
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();
            harness.Transport.Sent.Clear();

            byte[] chat = MessageCodec.EncodeChatSend(new ChatSendMessage("hola"));
            harness.Transport.RaiseReceived(1, chat);
            harness.Transport.RaiseReceived(1, chat);
            harness.Transport.RaiseReceived(1, chat);
            harness.Transport.RaiseReceived(1, chat);

            Assert.Equal(1, harness.Server.Snapshot().ChatRejectedFast);
            for (int i = 0; i < SimulationConstants.TickRate; i++)
            {
                harness.Zone.Tick();
            }

            harness.Transport.RaiseReceived(1, chat);
            harness.Server.PollOnce();

            NetworkStats stats = harness.Server.Snapshot();
            Assert.Equal(4, stats.ChatReceived);
            Assert.Equal(4, stats.ChatDelivered);
            Assert.Equal(1, stats.ChatRejectedFast);
        }

        [Fact]
        public void La_Red_Encola_Recoger_Y_Usar_Y_Envía_El_Inventario_Completo()
        {
            using var harness = new Harness(withItems: true);
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            Assert.True(MessageCodec.TryReadWelcome(
                harness.Transport.Sent[0].Data, 0, harness.Transport.Sent[0].Data.Length, out _));
            Assert.True(MessageCodec.TryReadItemCatalog(
                harness.Transport.Sent[1].Data, 0, harness.Transport.Sent[1].Data.Length, out ItemCatalogMessage catalog));
            Assert.Contains(catalog.Items, item => item.Id == 3101 && item.Kind == ItemKind.Weapon && item.Damage == 14);
            Assert.True(MessageCodec.TryReadInventory(
                harness.Transport.Sent[2].Data, 0, harness.Transport.Sent[2].Data.Length, out _));

            PlayerEntity player = Assert.IsType<PlayerEntity>(harness.Zone.Entities.Get(1));
            var ground = new GroundItemEntity(
                harness.Zone.Ids.Allocate(), 3001, 2, 100)
            {
                Position = player.Position,
            };
            harness.Zone.AddEntity(ground);
            harness.Transport.RaiseReceived(1, MessageCodec.EncodePickUp(new PickUpMessage(ground.Id)));
            harness.Zone.Tick();
            Assert.Equal((ushort)2, player.Inventory[0].Count);
            Assert.False(harness.Zone.Entities.TryGet(ground.Id, out _));

            harness.Transport.Sent.Clear();
            harness.Server.PollOnce();
            Assert.Contains(harness.Transport.Sent, sent =>
                MessageCodec.TryReadInventory(sent.Data, 0, sent.Data.Length, out InventoryMessage inventory) &&
                inventory.SlotCount == 20 && inventory.ItemIds[0] == 3001 && inventory.Counts[0] == 2);

            player.Health = 50;
            player.Inventory[0] = new InventorySlot(3002, 1);
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeUseItem(new UseItemMessage(0)));
            harness.Zone.Tick();
            Assert.Equal(80, player.Health);
            Assert.Equal((ushort)0, player.Inventory[0].ItemId);

            harness.Transport.Sent.Clear();
            harness.Server.PollOnce();
            Assert.Contains(harness.Transport.Sent, sent =>
                MessageCodec.TryReadInventory(sent.Data, 0, sent.Data.Length, out InventoryMessage inventory) &&
                inventory.ItemIds[0] == 0 && inventory.Counts[0] == 0);
        }

        [Fact]
        public void La_Red_Encola_Equipar_Y_El_Inventario_Informa_El_Arma_Equipada()
        {
            using var harness = new Harness(withItems: true);
            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            PlayerEntity player = Assert.IsType<PlayerEntity>(harness.Zone.Entities.Get(1));
            player.Inventory[0] = new InventorySlot(3101, 1);
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeEquip(new EquipMessage(0)));
            harness.Zone.Tick();

            Assert.Equal((ushort)3101, player.EquippedWeaponId);
            Assert.Equal(new InventorySlot(0, 0), player.Inventory[0]);

            harness.Transport.Sent.Clear();
            harness.Server.PollOnce();
            SentMessage inventorySent = Assert.Single(harness.Transport.Sent,
                sent => MessageCodec.TryReadInventory(sent.Data, 0, sent.Data.Length, out _));
            Assert.True(MessageCodec.TryReadInventory(
                inventorySent.Data, 0, inventorySent.Data.Length, out InventoryMessage inventory));
            Assert.Equal((ushort)3101, inventory.EquippedItemId);
        }

        [Fact]
        public void Una_Version_Distinta_Se_Rechaza()
        {
            using var harness = new Harness(config => config.RejectDisconnectDelayMilliseconds = 0);

            harness.Connect(1);
            harness.Handshake(1, version: (ushort)(ProtocolConstants.Version + 1));

            Assert.Equal(0, harness.Zone.Entities.Count);
            Assert.Single(harness.Transport.Sent);
            Assert.True(MessageCodec.TryReadReject(
                harness.Transport.Sent[0].Data,
                0,
                harness.Transport.Sent[0].Data.Length,
                out RejectMessage reject));
            Assert.Equal(RejectReason.ProtocolVersion, reject.Reason);

            // El rechazo sale en la llamada, pero el corte espera a que venza el retardo.
            harness.Server.PollOnce();
            Assert.Contains(1, harness.Transport.DisconnectedConnections);
        }

        [Fact]
        public void El_Rechazo_De_Saludo_No_Corta_En_La_Misma_Vuelta()
        {
            using var harness = new Harness(config => config.RejectDisconnectDelayMilliseconds = 0);

            harness.Connect(1);
            harness.Handshake(1, version: (ushort)(ProtocolConstants.Version + 1));

            // El motivo ya viajó…
            SentMessage sent = harness.Transport.Sent[0];
            Assert.True(MessageCodec.TryReadReject(sent.Data, 0, sent.Data.Length, out RejectMessage reject));
            Assert.Equal(RejectReason.ProtocolVersion, reject.Reason);

            // …pero el corte todavía no se ha pedido.
            Assert.Empty(harness.Transport.DisconnectedConnections);

            // Con retardo 0 vence en la vuelta siguiente, después de haber podido enviar el Reject.
            harness.Server.PollOnce();
            Assert.Equal(new[] { 1 }, harness.Transport.DisconnectedConnections);
        }

        [Fact]
        public void Un_Mapa_Distinto_Se_Rechaza()
        {
            using var harness = new Harness();

            var otherHash = new byte[ProtocolConstants.MapHashSize];
            Array.Copy(harness.MapHash, otherHash, otherHash.Length);
            otherHash[0] ^= 0xFF;

            harness.Connect(1);
            harness.Handshake(1, mapHash: otherHash);

            Assert.Equal(0, harness.Zone.Entities.Count);
            Assert.True(MessageCodec.TryReadReject(
                harness.Transport.Sent[0].Data,
                0,
                harness.Transport.Sent[0].Data.Length,
                out RejectMessage reject));
            Assert.Equal(RejectReason.MapMismatch, reject.Reason);
        }

        [Fact]
        public void Un_Saludo_Mal_Formado_Se_Rechaza()
        {
            using var harness = new Harness(config => config.RejectDisconnectDelayMilliseconds = 0);

            harness.Connect(1);
            harness.Transport.RaiseReceived(1, new byte[] { (byte)MessageType.Hello, 1, 2, 3 });

            Assert.Equal(0, harness.Zone.Entities.Count);
            Assert.True(MessageCodec.TryReadReject(
                harness.Transport.Sent[0].Data,
                0,
                harness.Transport.Sent[0].Data.Length,
                out RejectMessage reject));
            Assert.Equal(RejectReason.MalformedMessage, reject.Reason);

            harness.Server.PollOnce();
            Assert.Contains(1, harness.Transport.DisconnectedConnections);
        }

        [Fact]
        public void Los_Inputs_Mueven_Al_Jugador_Solo_Al_Simular()
        {
            using var harness = new Harness();

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            harness.Zone.Tick();
            Assert.Equal(-1.5f, harness.Zone.Players[0].X);

            harness.Transport.RaiseReceived(1, MessageCodec.EncodeMoveInput(new MoveInputMessage(1u, 1, 0)));

            // Encolar no mueve a nadie: el movimiento ocurre en el tick.
            Assert.Equal(-1.5f, harness.Zone.Players[0].X);

            harness.Zone.Tick();

            Assert.True(harness.Zone.Players[0].X > -1.5f);
            Assert.Equal(1u, harness.Zone.Players[0].LastProcessedSequence);
        }

        [Fact]
        public void Un_Input_Con_Direccion_Invalida_No_Llega_Al_Jugador()
        {
            using var harness = new Harness();

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            // El códec rechaza la dirección (5 no es -1, 0 ni 1) antes de encolarla.
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeMoveInput(new MoveInputMessage(1u, 5, 0)));
            harness.Zone.Tick();

            Assert.True(harness.Server.Snapshot().MessagesRejected >= 1);
            Assert.Equal(0, harness.Zone.Inputs.Pending);
            Assert.Equal(0, harness.Zone.Inputs.RejectedInvalidDirection);
            Assert.Equal(-1.5f, harness.Zone.Players[0].X);
        }

        [Fact]
        public void Una_Conexion_Sin_Saludo_No_Puede_Mover_Nada()
        {
            using var harness = new Harness();

            harness.Connect(1);
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeMoveInput(new MoveInputMessage(1u, 1, 0)));

            Assert.Equal(0, harness.Zone.Inputs.Pending);

            // Antes del saludo, cualquier mensaje es un saludo mal formado: se rechaza y se cierra.
            Assert.True(harness.Server.Snapshot().HandshakesRejected >= 1);
            Assert.Equal(0, harness.Zone.Entities.Count);
        }

        [Fact]
        public void La_Desconexion_Quita_Al_Jugador()
        {
            using var harness = new Harness();

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();
            Assert.Equal(1, harness.Zone.Entities.Count);

            harness.Transport.RaiseDisconnected(1);
            harness.Server.BeforeTick(harness.Zone);

            Assert.Equal(0, harness.Zone.Entities.Count);
            Assert.Equal(1, harness.Server.Snapshot().PlayersLeft);
        }

        [Fact]
        public void La_Zona_Llena_Rechaza_Con_Motivo()
        {
            using var harness = new Harness(config =>
            {
                config.MaxPlayers = 1;
                config.RejectDisconnectDelayMilliseconds = 0;
            });

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            harness.Connect(2);
            harness.Handshake(2);
            harness.Pump();

            Assert.Equal(1, harness.Zone.Entities.Count);

            SentMessage last = harness.Transport.Sent[harness.Transport.Sent.Count - 1];
            Assert.Equal(2, last.ConnectionId);
            Assert.True(MessageCodec.TryReadReject(last.Data, 0, last.Data.Length, out RejectMessage reject));
            Assert.Equal(RejectReason.ServerFull, reject.Reason);

            harness.Server.PollOnce();
            Assert.Contains(2, harness.Transport.DisconnectedConnections);
        }

        [Fact]
        public void El_Snapshot_Lleva_La_Posicion_Cuantizada()
        {
            using var harness = new Harness();

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            harness.Transport.Sent.Clear();
            harness.Server.AfterTick(harness.Zone);
            harness.Server.PollOnce();

            Assert.Single(harness.Transport.Sent);

            SentMessage sent = harness.Transport.Sent[0];
            Assert.True(MessageCodec.TryReadSnapshot(sent.Data, 0, sent.Data.Length, out SnapshotMessage snapshot));
            Assert.Single(snapshot.Entities);
            Assert.Equal(ProtocolConstants.QuantizePosition(-1.5f), snapshot.Entities[0].X);
            Assert.Equal(ProtocolConstants.QuantizePosition(-2.5f), snapshot.Entities[0].Y);
            Assert.Equal(EntityKind.Player, snapshot.Entities[0].Kind);
        }

        [Fact]
        public void El_Area_De_Interes_Filtra_Los_Snapshots_Y_Respeta_La_Histeresis()
        {
            using var harness = new Harness(config =>
            {
                config.InterestRadius = 2f;
                config.InterestCellSize = 2f;
                config.InterestHysteresis = 0.5f;
            });

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            harness.Connect(2);
            harness.Handshake(2);
            harness.Pump();

            PlayerEntity p1 = (PlayerEntity)harness.Zone.Entities.Get(1)!;
            PlayerEntity p2 = (PlayerEntity)harness.Zone.Entities.Get(2)!;

            // Lejos: cada uno se ve solo a sí mismo.
            p1.Position = new Vec2(-7f, 5f);
            p2.Position = new Vec2(7f, -5f);
            SendSnapshots(harness);

            SnapshotMessage s1 = SnapshotFor(harness, 1);
            SnapshotMessage s2 = SnapshotFor(harness, 2);
            Assert.Single(s1.Entities);
            Assert.Equal(1, s1.Entities[0].Id);
            Assert.Single(s2.Entities);
            Assert.Equal(2, s2.Entities[0].Id);

            // A distancia 2 (radio de entrada) aparece en ambos.
            p2.Position = new Vec2(-5f, 5f);
            SendSnapshots(harness);

            s1 = SnapshotFor(harness, 1);
            s2 = SnapshotFor(harness, 2);
            Assert.Equal(2, s1.Entities.Length);
            Assert.Equal(2, s2.Entities.Length);
            Assert.Contains(s1.Entities, e => e.Id == 2);
            Assert.Contains(s2.Entities, e => e.Id == 1);

            // A 2.4, fuera del radio de entrada pero dentro de la histéresis (2.5): sigue visible.
            p2.Position = new Vec2(-4.6f, 5f);
            SendSnapshots(harness);

            s1 = SnapshotFor(harness, 1);
            Assert.Equal(2, s1.Entities.Length);

            // A 2.6 ya supera el radio de salida: desaparece.
            p2.Position = new Vec2(-4.4f, 5f);
            SendSnapshots(harness);

            s1 = SnapshotFor(harness, 1);
            Assert.Single(s1.Entities);
            Assert.Equal(1, s1.Entities[0].Id);
        }

        [Fact]
        public void El_Snapshot_Lleva_Los_Pnjs_Cercanos_Con_Su_Direccion()
        {
            using var harness = new Harness(config =>
            {
                config.InterestRadius = 3f;
                config.InterestCellSize = 2f;
                config.InterestHysteresis = 0f;
            });

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            harness.Connect(2);
            harness.Handshake(2);
            harness.Pump();

            PlayerEntity near = (PlayerEntity)harness.Zone.Entities.Get(1)!;
            PlayerEntity far = (PlayerEntity)harness.Zone.Entities.Get(2)!;
            near.Position = new Vec2(0f, 5f);
            far.Position = new Vec2(-6f, -4f);

            var npc = new NpcEntity(
                100,
                2001,
                2f,
                new Vec2(1f, 5f),
                2f,
                nextDirectionChangeTick: 10_000,
                maxHealth: 30,
                respawnTicks: 200,
                spawnerIndex: -1)
            {
                Position = new Vec2(1f, 5f),
                HalfSize = new Vec2(0.35f, 0.35f),
                DirectionX = 1,
                DirectionY = -1,
            };

            harness.Zone.AddEntity(npc);
            SendSnapshots(harness);

            SnapshotMessage nearSnapshot = SnapshotFor(harness, 1);
            EntityState state = Assert.Single(nearSnapshot.Entities, e => e.Id == 100);
            Assert.Equal(EntityKind.Npc, state.Kind);
            Assert.Equal(1, state.DirectionX);
            Assert.Equal(-1, state.DirectionY);

            SnapshotMessage farSnapshot = SnapshotFor(harness, 2);
            Assert.DoesNotContain(farSnapshot.Entities, e => e.Id == 100);
        }

        [Fact]
        public void El_Snapshot_Lleva_La_Vida_Y_La_Accion()
        {
            using var harness = new Harness(config =>
            {
                config.InterestRadius = 6f;
                config.InterestCellSize = 2f;
                config.InterestHysteresis = 0f;
            });

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            PlayerEntity player = (PlayerEntity)harness.Zone.Entities.Get(1)!;
            player.Position = new Vec2(0f, 5f);

            var npc = new NpcEntity(
                100,
                2001,
                0f,
                new Vec2(1f, 5f),
                2f,
                nextDirectionChangeTick: long.MaxValue,
                maxHealth: 30,
                respawnTicks: 200,
                spawnerIndex: -1)
            {
                Position = new Vec2(1f, 5f),
                HalfSize = new Vec2(0.35f, 0.35f),
            };

            // Un orco herido: 21/30 = 70 % exacto.
            npc.Health = 21;
            harness.Zone.AddEntity(npc);

            SendSnapshots(harness);

            EntityState wounded = Assert.Single(SnapshotFor(harness, 1).Entities, e => e.Id == 100);
            Assert.Equal(EntityKind.Npc, wounded.Kind);
            Assert.Equal((byte)70, wounded.HealthPercent);
            Assert.Equal(EntityAction.None, wounded.Action);

            // El ataque cruza la red; los puños del guerrero hacen 12 y dejan 9/30 = 30 %.
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeAttack(new AttackMessage(100)));
            SendSnapshots(harness);

            SnapshotMessage afterAttack = SnapshotFor(harness, 1);
            EntityState hit = Assert.Single(afterAttack.Entities, e => e.Id == 100);
            Assert.Equal((byte)30, hit.HealthPercent);
            Assert.Equal(EntityAction.None, hit.Action);

            EntityState attacker = Assert.Single(afterAttack.Entities, e => e.Id == player.Id);
            Assert.Equal((byte)100, attacker.HealthPercent);
            Assert.Equal(EntityAction.Attacking, attacker.Action);

            // Un orco muerto manda 0 % y la acción Dead.
            npc.Health = 0;
            npc.Action = EntityAction.Dead;
            SendSnapshots(harness);

            EntityState dead = Assert.Single(SnapshotFor(harness, 1).Entities, e => e.Id == 100);
            Assert.Equal((byte)0, dead.HealthPercent);
            Assert.Equal(EntityAction.Dead, dead.Action);
        }

        [Fact]
        public void El_Snapshot_Lleva_La_Vida_Y_La_Accion_Del_Jugador_Y_El_Ataque_Del_Orco()
        {
            using var harness = new Harness(config =>
            {
                config.InterestRadius = 6f;
                config.InterestCellSize = 2f;
                config.InterestHysteresis = 0f;
            });

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            PlayerEntity player = (PlayerEntity)harness.Zone.Entities.Get(1)!;
            player.Position = new Vec2(0f, 5f);

            var npc = new NpcEntity(
                100,
                2001,
                0f,
                new Vec2(0.5f, 5f),
                2f,
                nextDirectionChangeTick: long.MaxValue,
                maxHealth: 30,
                respawnTicks: 200,
                spawnerIndex: -1,
                aggroRadius: 4f,
                leashRadius: 8f,
                attackDamage: 8,
                attackRange: 1f,
                attackCooldownTicks: 30)
            {
                Position = new Vec2(0.5f, 5f),
                HalfSize = new Vec2(0.35f, 0.35f),
            };

            harness.Zone.AddEntity(npc);

            // La zona de este arnés no tiene catálogo de PNJ, así que la IA no corre: esta prueba
            // solo comprueba el contenido del snapshot. La transición a ataque y el golpe se
            // cubren en I03Tests. Los valores se fijan después del tick para que el combate no
            // borre la acción Attacking.
            harness.Zone.Tick();

            player.Health = 92;
            npc.Action = EntityAction.Attacking;

            harness.Transport.Sent.Clear();
            harness.Server.AfterTick(harness.Zone);
            harness.Server.PollOnce();

            SnapshotMessage snapshot = SnapshotFor(harness, 1);
            EntityState hurt = Assert.Single(snapshot.Entities, e => e.Id == player.Id);
            Assert.Equal((byte)92, hurt.HealthPercent);
            Assert.Equal(EntityAction.None, hurt.Action);

            EntityState attacker = Assert.Single(snapshot.Entities, e => e.Id == 100);
            Assert.Equal(EntityAction.Attacking, attacker.Action);
            Assert.Equal((byte)100, attacker.HealthPercent);

            // Un jugador muerto manda 0 % y Dead.
            player.Health = 0;
            player.Action = EntityAction.Dead;
            harness.Zone.Tick();

            harness.Transport.Sent.Clear();
            harness.Server.AfterTick(harness.Zone);
            harness.Server.PollOnce();

            EntityState dead = Assert.Single(SnapshotFor(harness, 1).Entities, e => e.Id == player.Id);
            Assert.Equal((byte)0, dead.HealthPercent);
            Assert.Equal(EntityAction.Dead, dead.Action);
        }

        [Fact]
        public void El_Snapshot_Lleva_El_TypeId_Jugador_Cero_Y_Orco_2002()
        {
            using var harness = new Harness(config =>
            {
                config.InterestRadius = 6f;
                config.InterestCellSize = 2f;
                config.InterestHysteresis = 0f;
            });

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            PlayerEntity player = (PlayerEntity)harness.Zone.Entities.Get(1)!;
            player.Position = new Vec2(0f, 5f);

            // Orco guerrero (tipo 2002) más lento, más duro y pega más fuerte.
            var orc2002 = new NpcEntity(
                100,
                2002,
                0f,
                new Vec2(0.5f, 5f),
                2f,
                nextDirectionChangeTick: long.MaxValue,
                maxHealth: 60,
                respawnTicks: 400,
                spawnerIndex: -1,
                aggroRadius: 5f,
                leashRadius: 8f,
                attackDamage: 14,
                attackRange: 1f,
                attackCooldownTicks: 40)
            {
                Position = new Vec2(0.5f, 5f),
                HalfSize = new Vec2(0.35f, 0.35f),
            };

            harness.Zone.AddEntity(orc2002);
            harness.Zone.Tick();

            harness.Transport.Sent.Clear();
            harness.Server.AfterTick(harness.Zone);
            harness.Server.PollOnce();

            SnapshotMessage snapshot = SnapshotFor(harness, 1);

            // El jugador siempre manda TypeId = 0.
            EntityState playerState = Assert.Single(snapshot.Entities, e => e.Id == player.Id);
            Assert.Equal(EntityKind.Player, playerState.Kind);
            Assert.Equal((ushort)0, playerState.TypeId);

            // El orco 2002 manda su tipo en TypeId.
            EntityState orcState = Assert.Single(snapshot.Entities, e => e.Id == 100);
            Assert.Equal(EntityKind.Npc, orcState.Kind);
            Assert.Equal((ushort)2002, orcState.TypeId);
        }

        [Fact]
        public void Al_Recortar_El_Snapshot_Se_Quitan_Objetos_Antes_Que_Enemigos()
        {
            // Auditoría: el recorte se quedaba con los primeros de la rejilla; con mucho botín en
            // el suelo podía caerse el orco que pega al jugador.
            using var harness = new Harness(config => config.InterestHysteresis = 0f);

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            PlayerEntity player = (PlayerEntity)harness.Zone.Entities.Get(1)!;
            player.Position = new Vec2(0f, 5f);

            for (int i = 0; i < ProtocolConstants.MaxEntitiesPerSnapshot + 10; i++)
            {
                harness.Zone.AddEntity(new GroundItemEntity(1000 + i, 3001, 1, long.MaxValue)
                {
                    Position = new Vec2(0.5f, 5f),
                });
            }

            // El orco llega el último a la misma celda que el botín.
            var orc = new NpcEntity(
                100,
                2001,
                0f,
                new Vec2(0.5f, 5f),
                2f,
                nextDirectionChangeTick: long.MaxValue,
                maxHealth: 30,
                respawnTicks: 400,
                spawnerIndex: -1)
            {
                Position = new Vec2(0.5f, 5f),
                HalfSize = new Vec2(0.35f, 0.35f),
            };
            harness.Zone.AddEntity(orc);
            harness.Zone.Tick();

            harness.Transport.Sent.Clear();
            harness.Server.AfterTick(harness.Zone);
            harness.Server.PollOnce();

            SnapshotMessage snapshot = SnapshotFor(harness, 1);
            Assert.Equal(ProtocolConstants.MaxEntitiesPerSnapshot, snapshot.Entities.Length);
            Assert.Contains(snapshot.Entities, e => e.Id == player.Id);
            Assert.Contains(snapshot.Entities, e => e.Id == 100);
        }

        [Fact]
        public void El_Jugador_Local_Se_Incluye_Aunque_El_Radio_Sea_Minimo()
        {
            using var harness = new Harness(config =>
            {
                config.InterestRadius = 0.1f;
                config.InterestCellSize = 1f;
                config.InterestHysteresis = 0f;
            });

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            harness.Connect(2);
            harness.Handshake(2);
            harness.Pump();

            PlayerEntity p1 = (PlayerEntity)harness.Zone.Entities.Get(1)!;
            PlayerEntity p2 = (PlayerEntity)harness.Zone.Entities.Get(2)!;
            p1.Position = new Vec2(-7f, 5f);
            p2.Position = new Vec2(-6f, 5f);
            SendSnapshots(harness);

            SnapshotMessage s1 = SnapshotFor(harness, 1);
            SnapshotMessage s2 = SnapshotFor(harness, 2);

            Assert.Single(s1.Entities);
            Assert.Equal(1, s1.Entities[0].Id);
            Assert.Single(s2.Entities);
            Assert.Equal(2, s2.Entities[0].Id);
        }

        [Fact]
        public void El_Area_De_Interes_Cuenta_Entidades_Y_Bytes_En_Las_Metricas()
        {
            using var harness = new Harness(config =>
            {
                config.InterestRadius = 2f;
                config.InterestCellSize = 2f;
                config.InterestHysteresis = 0.5f;
            });

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();
            harness.Connect(2);
            harness.Handshake(2);
            harness.Pump();

            PlayerEntity p1 = (PlayerEntity)harness.Zone.Entities.Get(1)!;
            PlayerEntity p2 = (PlayerEntity)harness.Zone.Entities.Get(2)!;
            p1.Position = new Vec2(-7f, 5f);
            p2.Position = new Vec2(7f, -5f);
            SendSnapshots(harness);

            NetworkStats stats = harness.Server.Snapshot();

            Assert.Equal(2, stats.SnapshotsSent);
            Assert.Equal(2, stats.EntitiesSent);
            Assert.True(stats.SnapshotBytes > 0);
            Assert.Equal(0, stats.SnapshotsTruncated);
        }

        [Fact]
        public void Los_Bytes_Recibidos_Cuentan_La_Carga_Util()
        {
            using var harness = new Harness();

            harness.Connect(1);

            var hello = new HelloMessage(
                ProtocolConstants.Version,
                (ushort)SimulationConstants.TickRate,
                harness.MapHash,
                4242u + 1u);

            byte[] helloBytes = MessageCodec.EncodeHello(hello);
            harness.Transport.RaiseReceived(1, helloBytes);
            harness.Pump();

            Assert.Equal(helloBytes.Length, harness.Server.Snapshot().BytesReceived);

            // Un input válido suma su datagrama…
            byte[] input = MessageCodec.EncodeMoveInput(new MoveInputMessage(1u, 1, 0));
            harness.Transport.RaiseReceived(1, input);
            Assert.Equal(helloBytes.Length + input.Length, harness.Server.Snapshot().BytesReceived);

            // …y uno rechazado también: el coste de recibirlo ya lo pagó la red.
            byte[] garbage = { 0x7F, 0x01, 0x02 };
            harness.Transport.RaiseReceived(1, garbage);
            Assert.Equal(
                helloBytes.Length + input.Length + garbage.Length,
                harness.Server.Snapshot().BytesReceived);
        }

        [Fact]
        public void El_Limite_De_Mensajes_Por_Segundo_Descarta_El_Exceso()
        {
            using var harness = new Harness(config => config.MaxMessagesPerSecondPerConnection = 20);

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            WaitForNewSecond();

            for (int i = 0; i < 40; i++)
            {
                harness.Transport.RaiseReceived(1, MessageCodec.EncodeMoveInput(new MoveInputMessage((uint)(i + 1), 1, 0)));
            }

            Assert.True(
                harness.Server.Snapshot().RateLimited > 0,
                "Un cliente que manda más de lo permitido debe ver mensajes descartados.");

            // Descartar no es cortar: un pico por jitter no debe echar a nadie.
            Assert.Equal(0, harness.Server.Snapshot().RateLimitedDisconnects);
            Assert.Empty(harness.Transport.DisconnectedConnections);
        }

        [Fact]
        public void Insistir_Por_Encima_Del_Limite_Desconecta()
        {
            using var harness = new Harness(config =>
            {
                config.MaxMessagesPerSecondPerConnection = 20;
                config.RejectDisconnectDelayMilliseconds = 0;
            });

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            WaitForNewSecond();

            // 20 aceptados + 40 descartados: el 61.º ya es tres veces el tope.
            for (int i = 0; i < 61; i++)
            {
                harness.Transport.RaiseReceived(1, MessageCodec.EncodeMoveInput(new MoveInputMessage((uint)(i + 1), 1, 0)));
            }

            Assert.Equal(1, harness.Server.Snapshot().RateLimitedDisconnects);

            harness.Server.PollOnce();
            Assert.Contains(1, harness.Transport.DisconnectedConnections);

            SentMessage last = harness.Transport.Sent[harness.Transport.Sent.Count - 1];
            Assert.True(MessageCodec.TryReadReject(last.Data, 0, last.Data.Length, out RejectMessage reject));
            Assert.Equal(RejectReason.RateLimited, reject.Reason);
        }

        [Fact]
        public void Inundar_Durante_Cinco_Segundos_Desconecta_Y_Cuatro_No()
        {
            // Superar el tope de forma sostenida no puede dejar la sesión abierta para siempre.
            Assert.False(FloodForSeconds(4), "Cuatro segundos por encima del tope no deben cortar la sesión.");
            Assert.True(FloodForSeconds(5), "Cinco segundos por encima del tope deben cortar la sesión.");
        }

        /// <summary>
        /// Simula <paramref name="seconds"/> segundos mandando el límite + 1 mensajes por segundo.
        /// Devuelve si el servidor desconectó por inundación sostenida.
        /// </summary>
        private static bool FloodForSeconds(int seconds)
        {
            // Un minuto del reloj inyectado lejos de un cambio de minuto, para que la ventana no
            // se reinicie a mitad de la prueba.
            long now = 1_000_000;

            using var harness = new Harness(
                config =>
                {
                    config.MaxMessagesPerSecondPerConnection = 5;
                    config.MaxSecondsOverMessageLimit = 5;
                    config.RejectDisconnectDelayMilliseconds = 0;
                },
                () => now);

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            uint sequence = 0;

            for (int second = 0; second < seconds; second++)
            {
                for (int i = 0; i < 6; i++)
                {
                    sequence++;
                    harness.Transport.RaiseReceived(
                        1,
                        MessageCodec.EncodeMoveInput(new MoveInputMessage(sequence, 1, 0)));
                }

                if (harness.Server.Snapshot().RateLimitedDisconnects > 0)
                {
                    return true;
                }

                now += 1000;
            }

            return harness.Server.Snapshot().RateLimitedDisconnects > 0;
        }

        [Fact]
        public void Un_Input_Con_Redundancia_Encola_Solo_Los_Nuevos_Y_Cuenta_Las_Copias()
        {
            using var harness = new Harness();

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            SendMoveInput(harness, 1, new MoveInputMessage(1u, 1, 0));
            harness.Zone.Tick();
            Assert.Equal(1u, harness.Zone.Players[0].LastProcessedSequence);

            // n = 4 con base en la secuencia 4: la 1 ya estaba y las nuevas son 2, 3 y 4.
            SendMoveInput(harness, 1, new MoveInputMessage(4u, 4, 1, 0, 1, 0, 1, 0, 1, 0));
            harness.Zone.Tick();

            Assert.Equal(5, harness.Zone.Inputs.Received);
            Assert.Equal(1, harness.Zone.RedundantDuplicates);
            Assert.Equal(0, harness.Zone.RejectedOutOfOrder);
            Assert.Equal(4u, ((PlayerEntity)harness.Zone.Entities.Get(1)!).Input.LastAcceptedSequence);

            // Un input por tick: el primero se aplica ya y los demás esperan.
            Assert.Equal(2u, harness.Zone.Players[0].LastProcessedSequence);
            harness.Zone.Tick();
            harness.Zone.Tick();
            Assert.Equal(4u, harness.Zone.Players[0].LastProcessedSequence);
        }

        [Fact]
        public void Si_Se_Pierde_Un_Mensaje_El_Siguiente_Rellena_El_Hueco()
        {
            using var harness = new Harness();

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            SendMoveInput(harness, 1, new MoveInputMessage(1u, 1, 0));
            harness.Zone.Tick();
            Assert.Equal(1u, harness.Zone.Players[0].LastProcessedSequence);

            // El mensaje de la secuencia 2 se pierde. El 3 lleva la 2 (más vieja) y la 3 (nueva),
            // en ese orden: el servidor debe aplicar las dos, una por tick.
            SendMoveInput(harness, 1, new MoveInputMessage(3u, 2, 1, 0, 0, 1, 0, 0, 0, 0));

            harness.Zone.Tick();
            Assert.Equal(2u, harness.Zone.Players[0].LastProcessedSequence);

            harness.Zone.Tick();
            Assert.Equal(3u, harness.Zone.Players[0].LastProcessedSequence);

            Assert.Equal(0, harness.Zone.RedundantDuplicates);
            Assert.Equal(0, harness.Zone.RejectedOutOfOrder);
            Assert.Equal(0, harness.Zone.RejectedPerPlayerLimit);
        }

        [Fact]
        public void Las_Copias_Redundantes_No_Gastan_El_Tope_Por_Jugador()
        {
            using var harness = new Harness(config => config.MaxIntentsPerPlayerPerTick = 2);

            harness.Connect(1);
            harness.Handshake(1);
            harness.Pump();

            SendMoveInput(harness, 1, new MoveInputMessage(1u, 1, 0));
            harness.Zone.Tick();

            SendMoveInput(harness, 1, new MoveInputMessage(4u, 4, 1, 0, 1, 0, 1, 0, 1, 0));
            harness.Zone.Tick();

            // La 1 es una copia y no gasta plaza; 2 y 3 se aceptan; 4 topa. Si la copia gastara
            // tope, solo cabría una nueva y el tope subiría a dos.
            Assert.Equal(1, harness.Zone.RedundantDuplicates);
            Assert.Equal(1, harness.Zone.RejectedPerPlayerLimit);
            Assert.Equal(0, harness.Zone.RejectedOutOfOrder);
            Assert.Equal(3u, ((PlayerEntity)harness.Zone.Entities.Get(1)!).Input.LastAcceptedSequence);
        }

        private static void SendMoveInput(Harness harness, int connectionId, in MoveInputMessage message)
        {
            byte[] bytes = MessageCodec.EncodeMoveInput(message);
            harness.Transport.RaiseReceived(connectionId, bytes);
        }

        /// <summary>
        /// Espera a empezar un segundo nuevo: el contador de frecuencia se reinicia cada segundo y
        /// una ráfaga a caballo entre dos falsearía la prueba.
        /// </summary>
        private static void WaitForNewSecond()
        {
            long second = Environment.TickCount64 / 1000;

            while (Environment.TickCount64 / 1000 == second)
            {
                System.Threading.Thread.Sleep(1);
            }
        }

        /// <summary>Simula un tick y entrega los snapshots que genere la red.</summary>
        private static void SendSnapshots(Harness harness)
        {
            harness.Zone.Tick();
            harness.Transport.Sent.Clear();
            harness.Server.AfterTick(harness.Zone);
            harness.Server.PollOnce();
        }

        /// <summary>Busca el snapshot que recibió una conexión concreta.</summary>
        private static SnapshotMessage SnapshotFor(Harness harness, int connectionId)
        {
            for (int i = 0; i < harness.Transport.Sent.Count; i++)
            {
                SentMessage sent = harness.Transport.Sent[i];

                if (sent.ConnectionId == connectionId &&
                    MessageCodec.TryReadSnapshot(sent.Data, 0, sent.Data.Length, out SnapshotMessage snapshot))
                {
                    return snapshot;
                }
            }

            throw new InvalidOperationException("La conexión " + connectionId + " no recibió ningún snapshot.");
        }

        /// <summary>Transporte en memoria: los eventos los dispara la prueba, no un socket.</summary>
        private sealed class FakeServerTransport : IServerTransport
        {
            public List<SentMessage> Sent { get; } = new List<SentMessage>();

            public List<int> DisconnectedConnections { get; } = new List<int>();

            public int Port => 0;

            public int ConnectionCount => 0;

            public event TransportConnectedHandler? Connected;

            public event TransportDisconnectedHandler? Disconnected;

            public event TransportMessageHandler? Received;

            public void Start(int port)
            {
            }

            public void Stop()
            {
            }

            public void Poll()
            {
            }

            public void Send(int connectionId, byte[] data, int length, DeliveryMode mode)
            {
                var copy = new byte[length];
                Array.Copy(data, copy, length);
                Sent.Add(new SentMessage(connectionId, copy, mode));
            }

            public void Disconnect(int connectionId, DisconnectReason reason) => DisconnectedConnections.Add(connectionId);

            public void Dispose()
            {
            }

            public void RaiseConnected(int connectionId) => Connected?.Invoke(connectionId);

            public void RaiseDisconnected(int connectionId) =>
                Disconnected?.Invoke(connectionId, DisconnectReason.RemoteClosed);

            public void RaiseReceived(int connectionId, byte[] data) =>
                Received?.Invoke(connectionId, data, 0, data.Length);
        }

        /// <summary>Un mensaje tal como lo entregó el transporte falso.</summary>
        private readonly struct SentMessage
        {
            public SentMessage(int connectionId, byte[] data, DeliveryMode mode)
            {
                ConnectionId = connectionId;
                Data = data;
                Mode = mode;
            }

            public int ConnectionId { get; }

            public byte[] Data { get; }

            public DeliveryMode Mode { get; }
        }
    }
}
