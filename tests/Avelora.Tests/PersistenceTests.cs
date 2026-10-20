using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// Pruebas de la Etapa F: cuentas, personajes y guardado/carga de la posición.
    /// </summary>
    public class PersistenceTests : IDisposable
    {
        private static string MapPath =>
            Path.Combine(AppContext.BaseDirectory, "maps", "avelora-etapa-a.avmap");

        private readonly string _folder;

        public PersistenceTests()
        {
            _folder = Path.Combine(
                Path.GetTempPath(),
                "avelora-persistence-tests-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(_folder);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_folder, recursive: true);
            }
            catch (IOException)
            {
                // SQLite puede tardar un instante en soltar el archivo en Windows; no debe fallar
                // la prueba por eso.
            }
        }

        private string DatabasePath => Path.Combine(_folder, "avelora.db");

        [Fact]
        public void La_Cuenta_Se_Registra_Y_Se_Verifica_Sin_Distinguir_Mayusculas()
        {
            using var repository = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000);
            repository.Initialize();

            AccountLoginResult registered = repository.Login(
                "Cuenta",
                "secreto1",
                "avelora-dev",
                "avelora-dev");

            Assert.True(registered.Success);
            Assert.True(registered.AccountId > 0);

            AccountLoginResult wrong = repository.Login(
                "cuenta",
                "otra-clave",
                "avelora-dev",
                "avelora-dev");

            Assert.False(wrong.Success);
            Assert.Equal(RejectReason.InvalidCredentials, wrong.Failure);

            AccountLoginResult again = repository.Login(
                "CUENTA",
                "secreto1",
                string.Empty,
                "avelora-dev");

            Assert.True(again.Success);
            Assert.Equal(registered.AccountId, again.AccountId);
        }

        [Fact]
        public void El_Registro_Cerrado_No_Crea_Cuentas()
        {
            using var repository = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000);
            repository.Initialize();

            AccountLoginResult closed = repository.Login("nadie", "secreto1", "lo-que-sea", string.Empty);
            AccountLoginResult wrongCode = repository.Login("nadie", "secreto1", "otra", "avelora-dev");

            Assert.False(closed.Success);
            Assert.Equal(RejectReason.RegistrationClosed, closed.Failure);
            Assert.False(wrongCode.Success);
            Assert.Equal(RejectReason.RegistrationClosed, wrongCode.Failure);
        }

        [Fact]
        public void El_Personaje_Se_Guarda_Y_Se_Recupera_Al_Reabrir_La_Base()
        {
            byte[] mapHash = NewHash(3);
            int accountId;

            using (var repository = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000))
            {
                repository.Initialize();

                AccountLoginResult login = repository.Login(
                    "cuenta",
                    "secreto1",
                    "avelora-dev",
                    "avelora-dev");

                Assert.True(login.Success);
                accountId = login.AccountId;

                CharacterRecord created = repository.GetOrCreateCharacter(
                    accountId,
                    "Heroe",
                    1,
                    new Vec2(-1.5f, -2.5f),
                    mapHash);

                Assert.Equal(1, created.Level);
                Assert.Equal(new Vec2(-1.5f, -2.5f), created.Position);

                repository.SaveCharacter(created.WithPosition(
                    1,
                    new Vec2(3.25f, -4.5f),
                    NewHash(9)));
            }

            using (var reopened = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000))
            {
                reopened.Initialize();

                AccountLoginResult login = reopened.Login(
                    "cuenta",
                    "secreto1",
                    string.Empty,
                    string.Empty);

                Assert.True(login.Success);
                Assert.Equal(accountId, login.AccountId);

                CharacterRecord loaded = reopened.GetOrCreateCharacter(
                    login.AccountId,
                    "heroe",
                    1,
                    new Vec2(-1.5f, -2.5f),
                    mapHash);

                Assert.Equal("Heroe", loaded.Name);
                Assert.Equal(new Vec2(3.25f, -4.5f), loaded.Position);
                Assert.Equal(NewHash(9), loaded.MapHash);
                Assert.Equal(1, loaded.ZoneId);
            }
        }

        [Fact]
        public void La_Cola_Coalesce_Posiciones_Y_Guarda_La_Ultima()
        {
            var repository = new RecordingRepository();
            repository.Gate.Reset();

            using var saves = new CharacterSaveService(repository);

            Vec2 last = default;

            for (int i = 0; i < 5; i++)
            {
                last = new Vec2(i, -i);
                saves.Enqueue(NewCharacter(i + 1, last));
            }

            Assert.True(saves.Pending >= 1);
            repository.Gate.Set();

            Assert.True(saves.Flush(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, saves.Pending);
            Assert.True(repository.Saved.Count >= 1);
            Assert.Equal(last, repository.Saved[repository.Saved.Count - 1].Position);
            Assert.Equal(0L, saves.Failed);
        }

        [Fact]
        public void El_Servidor_Carga_La_Posicion_Guardada_Al_Reconectar()
        {
            using var harness = new ServerHarness(DatabasePath);

            harness.Connect(1);
            harness.Handshake(1, "dev", "avelora-dev", "dev");
            harness.Pump(1);

            Assert.Equal(1, harness.Zone.Entities.Count);

            PlayerEntity first = (PlayerEntity)harness.Zone.Entities.All[0];
            first.Position = new Vec2(2.5f, -3.5f);

            harness.Transport.RaiseDisconnected(1);
            harness.Server.BeforeTick(harness.Zone);
            Assert.True(harness.Saves.Flush(TimeSpan.FromSeconds(5)));

            harness.Connect(2);
            harness.Handshake(2, "dev", "avelora-dev", "dev");
            harness.Pump(2);

            WelcomeMessage welcome = harness.LastWelcome(2);

            Assert.Equal(2.5f, welcome.SpawnX);
            Assert.Equal(-3.5f, welcome.SpawnY);
            Assert.Equal(1, harness.Zone.Entities.Count);
            Assert.True(harness.Saves.Saved >= 1);
        }

        [Fact]
        public void Una_Contrasena_Incorrecta_Se_Rechaza_Sin_Crear_Entidad()
        {
            using var harness = new ServerHarness(DatabasePath);

            harness.Connect(1);
            harness.Handshake(1, "dev", "avelora-dev", "dev");
            harness.Pump(1);

            harness.Transport.RaiseDisconnected(1);
            harness.Server.BeforeTick(harness.Zone);
            Assert.Equal(0, harness.Zone.Entities.Count);

            harness.Connect(2);
            harness.Handshake(2, "dev", "otra-clave", "dev");
            harness.Pump(2);

            Assert.Equal(0, harness.Zone.Entities.Count);
            Assert.True(harness.LastReject(2, out RejectMessage reject));
            Assert.Equal(RejectReason.InvalidCredentials, reject.Reason);
            Assert.Contains(2, harness.Transport.DisconnectedConnections);
        }

        [Fact]
        public void La_Misma_Cuenta_Desde_Dos_Conexiones_Echa_A_La_Primera()
        {
            using var harness = new ServerHarness(DatabasePath);

            harness.Connect(1);
            harness.Handshake(1, "dev", "avelora-dev", "dev");
            harness.Pump(1);

            PlayerEntity first = (PlayerEntity)harness.Zone.Entities.All[0];
            first.Position = new Vec2(2.5f, -3.5f);

            // Misma cuenta y mismo personaje desde otra conexión: la nueva gana.
            harness.Connect(2);
            harness.Handshake(2, "dev", "avelora-dev", "dev");
            harness.Pump(2);

            // La primera se va con motivo y se cierra; queda una sola entidad, en la posición viva.
            Assert.True(harness.LastReject(1, out RejectMessage reject));
            Assert.Equal(RejectReason.LoggedInElsewhere, reject.Reason);
            Assert.Contains(1, harness.Transport.DisconnectedConnections);

            Assert.Equal(1, harness.Zone.Entities.Count);
            WelcomeMessage welcome = harness.LastWelcome(2);
            Assert.Equal(2.5f, welcome.SpawnX);
            Assert.Equal(-3.5f, welcome.SpawnY);

            // La baja tardía de la conexión antigua no suelta la reserva de la nueva: si la
            // soltara, la tercera conexión entraría sin echar a nadie y habría dos sesiones.
            harness.Transport.RaiseDisconnected(1);
            harness.Server.BeforeTick(harness.Zone);

            PlayerEntity second = (PlayerEntity)harness.Zone.Entities.Get(welcome.EntityId)!;
            second.Position = new Vec2(4.5f, 1.5f);

            harness.Connect(3);
            harness.Handshake(3, "dev", "avelora-dev", "dev");
            harness.Pump(3);

            Assert.True(harness.LastReject(2, out RejectMessage secondReject));
            Assert.Equal(RejectReason.LoggedInElsewhere, secondReject.Reason);
            Assert.Equal(1, harness.Zone.Entities.Count);

            WelcomeMessage third = harness.LastWelcome(3);
            Assert.Equal(4.5f, third.SpawnX);
            Assert.Equal(1.5f, third.SpawnY);
        }

        private static byte[] NewHash(byte seed)
        {
            var hash = new byte[ProtocolConstants.MapHashSize];

            for (int i = 0; i < hash.Length; i++)
            {
                hash[i] = (byte)(seed + i);
            }

            return hash;
        }

        private static CharacterRecord NewCharacter(int id, Vec2 position)
        {
            DateTime now = DateTime.UtcNow;

            return new CharacterRecord(
                id,
                1,
                "personaje" + id,
                1,
                0,
                100,
                100,
                1,
                position,
                NewHash(1),
                now,
                now);
        }

        private sealed class ServerHarness : IDisposable
        {
            public ServerHarness(string databasePath)
            {
                Config = new ServerConfig
                {
                    HandshakeTimeoutSeconds = 60d,
                    PersistenceEnabled = true,
                    DatabasePath = databasePath,
                    SaveIntervalSeconds = 60d,
                };

                MapCollisionGrid map = MapFile.Load(MapPath);

                Zone = new Zone(
                    1,
                    map,
                    new EntityStore(),
                    new EntityIdAllocator(),
                    new InputQueue(Config.MaxQueuedIntents),
                    new MovementSystem(Config.PlayerSpeed, SimulationConstants.FixedDt),
                    Config.MaxIntentsPerTick,
                    Config.MaxIntentsPerPlayerPerTick,
                    new InterestSettings(Config.InterestRadius, Config.InterestCellSize, Config.InterestHysteresis));

                MapHash = MapFile.ComputeContentHashBytes(map);
                Transport = new FakeServerTransport();

                // Pocas iteraciones: estas pruebas no miden PBKDF2, solo el flujo de login.
                Repository = new SqlitePlayerRepository(databasePath, passwordIterations: 1_000);
                Repository.Initialize();
                Saves = new CharacterSaveService(Repository);
                Server = new NetworkServer(
                    Zone,
                    Config,
                    Transport,
                    MapHash,
                    Repository,
                    Saves,
                    Config.RegistrationInvitationCode);
            }

            public ServerConfig Config { get; }

            public Zone Zone { get; }

            public byte[] MapHash { get; }

            public FakeServerTransport Transport { get; }

            public SqlitePlayerRepository Repository { get; }

            public CharacterSaveService Saves { get; }

            public NetworkServer Server { get; }

            public void Connect(int connectionId) => Transport.RaiseConnected(connectionId);

            public void Handshake(int connectionId, string account, string password, string character)
            {
                var hello = new HelloMessage(
                    ProtocolConstants.Version,
                    (ushort)SimulationConstants.TickRate,
                    MapHash,
                    1000u + (uint)connectionId,
                    account,
                    password,
                    character,
                    Config.RegistrationInvitationCode);

                Transport.RaiseReceived(connectionId, MessageCodec.EncodeHello(hello));
            }

            /// <summary>
            /// Una vuelta de los dos hilos hasta que la conexión reciba el desenlace de su saludo
            /// (<c>Welcome</c> o <c>Reject</c>). El login ya no es síncrono: corre en su hilo y hay
            /// que esperar a que la red recoja el resultado.
            /// </summary>
            public void Pump(int connectionId)
            {
                long deadline = Environment.TickCount64 + 5_000;

                while (true)
                {
                    Server.BeforeTick(Zone);
                    Server.PollOnce();

                    if (HasOutcome(connectionId))
                    {
                        return;
                    }

                    if (Environment.TickCount64 >= deadline)
                    {
                        throw new TimeoutException(
                            "La conexión " + connectionId + " no recibió Welcome ni Reject a tiempo.");
                    }

                    Thread.Sleep(1);
                }
            }

            private bool HasOutcome(int connectionId)
            {
                for (int i = 0; i < Transport.Sent.Count; i++)
                {
                    SentMessage sent = Transport.Sent[i];

                    if (sent.ConnectionId != connectionId)
                    {
                        continue;
                    }

                    if (MessageCodec.TryReadWelcome(sent.Data, 0, sent.Data.Length, out _) ||
                        MessageCodec.TryReadReject(sent.Data, 0, sent.Data.Length, out _))
                    {
                        return true;
                    }
                }

                return false;
            }

            public WelcomeMessage LastWelcome(int connectionId)
            {
                for (int i = Transport.Sent.Count - 1; i >= 0; i--)
                {
                    SentMessage sent = Transport.Sent[i];

                    if (sent.ConnectionId == connectionId &&
                        MessageCodec.TryReadWelcome(sent.Data, 0, sent.Data.Length, out WelcomeMessage welcome))
                    {
                        return welcome;
                    }
                }

                throw new InvalidOperationException("La conexión " + connectionId + " no recibió Welcome.");
            }

            public bool LastReject(int connectionId, out RejectMessage reject)
            {
                for (int i = Transport.Sent.Count - 1; i >= 0; i--)
                {
                    SentMessage sent = Transport.Sent[i];

                    if (sent.ConnectionId == connectionId &&
                        MessageCodec.TryReadReject(sent.Data, 0, sent.Data.Length, out reject))
                    {
                        return true;
                    }
                }

                reject = default;
                return false;
            }

            public void Dispose()
            {
                Server.Dispose();
                Saves.Dispose();
                Repository.Dispose();
            }
        }

        private sealed class RecordingRepository : IPlayerRepository
        {
            private readonly object _gate = new object();

            public ManualResetEventSlim Gate { get; } = new ManualResetEventSlim(true);

            public List<CharacterRecord> Saved { get; } = new List<CharacterRecord>();

            public void Initialize()
            {
            }

            public AccountLoginResult Login(
                string accountName,
                string password,
                string invitationCode,
                string registrationCode) =>
                throw new NotSupportedException();

            public CharacterRecord GetOrCreateCharacter(
                int accountId,
                string characterName,
                int zoneId,
                Vec2 spawn,
                byte[] mapHash) =>
                throw new NotSupportedException();

            public void SaveCharacter(CharacterRecord character)
            {
                Gate.Wait();

                lock (_gate)
                {
                    Saved.Add(character);
                }
            }

            public void Dispose()
            {
            }
        }

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

            public void Disconnect(int connectionId, DisconnectReason reason) =>
                DisconnectedConnections.Add(connectionId);

            public void Dispose()
            {
            }

            public void RaiseConnected(int connectionId) => Connected?.Invoke(connectionId);

            public void RaiseDisconnected(int connectionId) =>
                Disconnected?.Invoke(connectionId, DisconnectReason.RemoteClosed);

            public void RaiseReceived(int connectionId, byte[] data) =>
                Received?.Invoke(connectionId, data, 0, data.Length);
        }

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
