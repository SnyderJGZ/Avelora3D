using System;
using System.Collections.Generic;
using System.IO;
using Avelora.Net;
using Avelora.Server.Configuration;
using Avelora.Server.Entities;
using Avelora.Server.Net;
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

        private static Zone NewZone(ServerConfig config)
        {
            MapCollisionGrid map = MapFile.Load(MapPath);

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
                npcs: null,
                npcSeed: 12345,
                attacks: new AttackQueue(config.MaxQueuedAttacks),
                combat: new CombatSettings(
                    config.PlayerAttackDamage,
                    config.PlayerAttackRange,
                    config.AttackRangeTolerance,
                    config.PlayerAttackCooldownSeconds),
                playerVitals: config.PlayerVitals);
        }

        private sealed class Harness : IDisposable
        {
            private readonly Dictionary<int, byte[]> _challengeNonces = new Dictionary<int, byte[]>();

            public Harness(Action<ServerConfig>? configure = null, Func<long>? clock = null)
            {
                Config = new ServerConfig { HandshakeTimeoutSeconds = 60d };
                configure?.Invoke(Config);
                Zone = NewZone(Config);
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

            public void Handshake(int connectionId, ushort? version = null, byte[]? mapHash = null)
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
                    "dev");

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
            Assert.Single(harness.Transport.Sent);

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
            Assert.Empty(harness.Transport.DisconnectedConnections);
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

            // El ataque cruza la red y el servidor lo resuelve en el tick siguiente: 11/30 = 37 %.
            harness.Transport.RaiseReceived(1, MessageCodec.EncodeAttack(new AttackMessage(100)));
            SendSnapshots(harness);

            SnapshotMessage afterAttack = SnapshotFor(harness, 1);
            EntityState hit = Assert.Single(afterAttack.Entities, e => e.Id == 100);
            Assert.Equal((byte)37, hit.HealthPercent);
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
                Sent.Add(new SentMessage(connectionId, copy));
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
            public SentMessage(int connectionId, byte[] data)
            {
                ConnectionId = connectionId;
                Data = data;
            }

            public int ConnectionId { get; }

            public byte[] Data { get; }
        }
    }
}
