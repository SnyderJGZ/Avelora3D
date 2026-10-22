using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Avelora.Net;
using Avelora.Server.Configuration;
using Avelora.Server.Entities;
using Avelora.Server.Net;
using Avelora.Server.Persistence;
using Avelora.Server.Simulation;
using Avelora.Shared;
using Avelora.Shared.Maps;
using Avelora.Shared.Protocol;
using Xunit;

namespace Avelora.Tests
{
    /// <summary>
    /// Prueba de punta a punta por UDP de verdad: el servidor abre un socket efímero y un cliente
    /// LiteNetLib hace el saludo, manda intenciones y recibe snapshots.
    /// </summary>
    /// <remarks>
    /// El hilo de red del servidor es el real. El papel de hilo de simulación lo hace la prueba,
    /// llamando a <see cref="NetworkServer.BeforeTick"/>, <see cref="Zone.Tick"/> y
    /// <see cref="NetworkServer.AfterTick"/> a 20 Hz: así se comprueba exactamente la costura que
    /// usa el servidor de verdad, sin depender del reloj del sistema.
    /// </remarks>
    public class NetworkIntegrationTests
    {
        private static string MapPath =>
            Path.Combine(AppContext.BaseDirectory, "maps", "avelora-etapa-a.avmap");

        [Fact]
        public void El_Cliente_Habla_Con_El_Servidor_Por_Udp()
        {
            var config = new ServerConfig
            {
                Port = 0,
                MaxPlayers = 2,
                HandshakeTimeoutSeconds = 10d,
            };

            MapCollisionGrid map = MapFile.Load(MapPath);

            var zone = new Zone(
                1,
                map,
                new EntityStore(),
                new EntityIdAllocator(),
                new InputQueue(config.MaxQueuedIntents),
                new MovementSystem(config.PlayerSpeed, SimulationConstants.FixedDt),
                config.MaxIntentsPerTick,
                config.MaxIntentsPerPlayerPerTick);

            byte[] mapHash = MapFile.ComputeContentHashBytes(map);

            using var server = new NetworkServer(
                zone,
                config,
                new LiteNetLibServerTransport(config.MaxConnections),
                mapHash);

            server.Start();
            Assert.True(server.Port > 0, "El socket debería haber quedado a la escucha.");

            using var client = new LiteNetLibClientTransport();

            bool gotWelcome = false;
            WelcomeMessage welcome = default;
            RejectMessage reject = default;
            bool gotReject = false;
            var snapshots = new List<SnapshotMessage>();

            client.Received += (connectionId, buffer, offset, length) =>
            {
                if (MessageCodec.TryReadWelcome(buffer, offset, length, out WelcomeMessage decodedWelcome))
                {
                    welcome = decodedWelcome;
                    gotWelcome = true;
                    return;
                }

                if (MessageCodec.TryReadReject(buffer, offset, length, out RejectMessage decodedReject))
                {
                    reject = decodedReject;
                    gotReject = true;
                    return;
                }

                if (MessageCodec.TryReadSnapshot(buffer, offset, length, out SnapshotMessage snapshot))
                {
                    snapshots.Add(snapshot);
                }
            };

            client.Connect("127.0.0.1", server.Port);

            byte[] helloBytes = MessageCodec.EncodeHello(new HelloMessage(
                ProtocolConstants.Version,
                (ushort)SimulationConstants.TickRate,
                mapHash,
                7u));

            var clock = Stopwatch.StartNew();
            bool helloSent = false;
            uint sequence = 0;
            double nextInputAt = 0d;
            double nextTickAt = 0d;

            while (clock.Elapsed.TotalMilliseconds < 4000d)
            {
                client.Poll();

                if (client.IsConnected && !helloSent)
                {
                    client.Send(helloBytes, helloBytes.Length, DeliveryMode.ReliableOrdered);
                    helloSent = true;
                }

                double now = clock.Elapsed.TotalMilliseconds;

                if (gotWelcome && now >= nextInputAt)
                {
                    sequence++;
                    byte[] input = MessageCodec.EncodeMoveInput(new MoveInputMessage(sequence, 1, 0));
                    client.Send(input, input.Length, DeliveryMode.Sequenced);
                    nextInputAt += SimulationConstants.StepMs;
                }

                if (now >= nextTickAt)
                {
                    server.BeforeTick(zone);
                    zone.Tick();
                    server.AfterTick(zone);

                    nextTickAt += SimulationConstants.StepMs;
                    if (nextTickAt < now)
                    {
                        nextTickAt = now + SimulationConstants.StepMs;
                    }
                }

                if (gotWelcome && snapshots.Count >= 5 && now >= 1000d)
                {
                    break;
                }

                if (gotReject)
                {
                    break;
                }

                Thread.Sleep(2);
            }

            Assert.False(gotReject, "El servidor rechazó el saludo: " + reject.Reason + ".");
            Assert.True(helloSent, "La conexión UDP no llegó a establecerse.");
            Assert.True(gotWelcome, "El servidor no respondió al saludo.");

            Assert.Equal(ProtocolConstants.Version, welcome.ProtocolVersion);
            Assert.Equal((ushort)SimulationConstants.TickRate, welcome.TickRate);
            Assert.True(welcome.EntityId > 0);
            Assert.Equal(mapHash, welcome.MapHash);
            Assert.Equal(config.PlayerSpeed, welcome.Speed);
            Assert.Equal(config.PlayerHalfWidth, welcome.HalfWidth);

            Assert.NotEmpty(snapshots);
            Assert.Contains(
                snapshots,
                snapshot => Array.Exists(snapshot.Entities, entity => entity.Id == welcome.EntityId));

            // La posición del último snapshot debe reflejar el movimiento hacia la derecha (hasta
            // topar con el tabique, que está en x = 0).
            bool found = false;
            float lastX = 0f;

            for (int i = snapshots.Count - 1; i >= 0 && !found; i--)
            {
                for (int j = 0; j < snapshots[i].Entities.Length; j++)
                {
                    if (snapshots[i].Entities[j].Id == welcome.EntityId)
                    {
                        lastX = ProtocolConstants.DequantizePosition(snapshots[i].Entities[j].X);
                        found = true;
                        break;
                    }
                }
            }

            Assert.True(found, "Ningún snapshot contenía al jugador recién conectado.");
            Assert.True(
                lastX > welcome.SpawnX + 0.1f,
                "El jugador debería haberse movido a la derecha; x=" + lastX.ToString("0.000") + ".");

            // Al desconectar, la simulación debe dar de baja la entidad.
            client.Disconnect();

            var wait = Stopwatch.StartNew();
            while (wait.Elapsed.TotalMilliseconds < 2000d && zone.Entities.Count > 0)
            {
                server.BeforeTick(zone);
                Thread.Sleep(10);
            }

            Assert.Equal(0, zone.Entities.Count);
            Assert.Equal(1, server.Snapshot().PlayersJoined);
            Assert.Equal(1, server.Snapshot().PlayersLeft);
        }

        /// <summary>
        /// Con persistencia, la misma cuenta desde otra conexión echa a la primera. El
        /// <c>Reject</c> viaja fiable y el corte sale con retardo: el cliente debe recibir el
        /// motivo <b>antes</b> de su evento <c>Disconnected</c>, no perderlo.
        /// </summary>
        [Fact]
        public void El_Echado_Por_Otra_Sesion_Entrega_El_Reject_Antes_De_Cortar()
        {
            string folder = Path.Combine(
                Path.GetTempPath(),
                "avelora-f01d-tests-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(folder);
            string databasePath = Path.Combine(folder, "avelora.db");

            try
            {
                var config = new ServerConfig
                {
                    Port = 0,
                    MaxPlayers = 2,
                    HandshakeTimeoutSeconds = 10d,
                    PersistenceEnabled = true,
                    DatabasePath = databasePath,
                    RegistrationInvitationCode = "avelora-dev",
                    SaveIntervalSeconds = 60d,
                };

                MapCollisionGrid map = MapFile.Load(MapPath);

                var zone = new Zone(
                    1,
                    map,
                    new EntityStore(),
                    new EntityIdAllocator(),
                    new InputQueue(config.MaxQueuedIntents),
                    new MovementSystem(config.PlayerSpeed, SimulationConstants.FixedDt),
                    config.MaxIntentsPerTick,
                    config.MaxIntentsPerPlayerPerTick);

                byte[] mapHash = MapFile.ComputeContentHashBytes(map);

                // Iteraciones bajas: esta prueba no mide PBKDF2, solo el orden de los eventos.
                using var repository = new SqlitePlayerRepository(databasePath, passwordIterations: 1_000);
                repository.Initialize();

                using var saves = new CharacterSaveService(repository);

                using var server = new NetworkServer(
                    zone,
                    config,
                    new LiteNetLibServerTransport(config.MaxConnections),
                    mapHash,
                    repository,
                    saves,
                    config.RegistrationInvitationCode);

                server.Start();
                Assert.True(server.Port > 0, "El socket debería haber quedado a la escucha.");

                using var first = new LiteNetLibClientTransport();
                using var second = new LiteNetLibClientTransport();

                var firstEvents = new List<string>();
                RejectMessage firstReject = default;
                bool firstGotReject = false;
                bool firstGotWelcome = false;
                bool secondGotWelcome = false;

                first.Received += (connectionId, buffer, offset, length) =>
                {
                    if (MessageCodec.TryReadReject(buffer, offset, length, out RejectMessage reject))
                    {
                        firstReject = reject;
                        firstGotReject = true;
                        firstEvents.Add("reject");
                        return;
                    }

                    if (MessageCodec.TryReadWelcome(buffer, offset, length, out _))
                    {
                        firstGotWelcome = true;
                    }
                };

                first.Disconnected += reason => firstEvents.Add("disconnected");

                second.Received += (connectionId, buffer, offset, length) =>
                {
                    if (MessageCodec.TryReadWelcome(buffer, offset, length, out _))
                    {
                        secondGotWelcome = true;
                    }
                };

                byte[] firstHello = MessageCodec.EncodeHello(new HelloMessage(
                    ProtocolConstants.Version,
                    (ushort)SimulationConstants.TickRate,
                    mapHash,
                    11u,
                    "cuenta",
                    "secreto1",
                    "heroe",
                    "avelora-dev"));

                byte[] secondHello = MessageCodec.EncodeHello(new HelloMessage(
                    ProtocolConstants.Version,
                    (ushort)SimulationConstants.TickRate,
                    mapHash,
                    12u,
                    "cuenta",
                    "secreto1",
                    "heroe",
                    "avelora-dev"));

                first.Connect("127.0.0.1", server.Port);

                bool sentFirstHello = false;
                bool secondConnected = false;
                bool sentSecondHello = false;

                var clock = Stopwatch.StartNew();
                double nextTickAt = 0d;

                while (clock.Elapsed.TotalMilliseconds < 8000d)
                {
                    first.Poll();
                    second.Poll();

                    if (first.IsConnected && !sentFirstHello)
                    {
                        first.Send(firstHello, firstHello.Length, DeliveryMode.ReliableOrdered);
                        sentFirstHello = true;
                    }

                    if (firstGotWelcome && !secondConnected)
                    {
                        second.Connect("127.0.0.1", server.Port);
                        secondConnected = true;
                    }

                    if (secondConnected && second.IsConnected && !sentSecondHello)
                    {
                        second.Send(secondHello, secondHello.Length, DeliveryMode.ReliableOrdered);
                        sentSecondHello = true;
                    }

                    double now = clock.Elapsed.TotalMilliseconds;

                    if (now >= nextTickAt)
                    {
                        server.BeforeTick(zone);
                        zone.Tick();
                        server.AfterTick(zone);

                        nextTickAt += SimulationConstants.StepMs;
                        if (nextTickAt < now)
                        {
                            nextTickAt = now + SimulationConstants.StepMs;
                        }
                    }

                    if (firstEvents.Contains("disconnected"))
                    {
                        break;
                    }

                    Thread.Sleep(2);
                }

                Assert.True(firstGotWelcome, "El primer cliente no recibió Welcome.");
                Assert.True(secondGotWelcome, "El segundo cliente no recibió Welcome (la misma cuenta debería entrar).");
                Assert.True(firstGotReject, "El primer cliente no recibió el Reject de LoggedInElsewhere.");
                Assert.Equal(RejectReason.LoggedInElsewhere, firstReject.Reason);

                int rejectIndex = firstEvents.IndexOf("reject");
                int disconnectIndex = firstEvents.IndexOf("disconnected");

                Assert.True(disconnectIndex >= 0, "El primer cliente no llegó a desconectarse.");
                Assert.True(
                    rejectIndex >= 0 && rejectIndex < disconnectIndex,
                    "El Reject debe llegar antes del evento Disconnected; orden observado: " +
                    string.Join(", ", firstEvents) + ".");
            }
            finally
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (IOException)
                {
                    // SQLite puede tardar un instante en soltar el archivo en Windows.
                }
            }
        }
    }
}
