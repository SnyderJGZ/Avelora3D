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
using Microsoft.Data.Sqlite;
using Xunit;

namespace Avelora.Tests
{
    /// <summary>
    /// Pruebas del endurecimiento del login de la Etapa F: iteraciones, hilo propio, topes de
    /// intentos y visibilidad de los errores de guardado.
    /// </summary>
    public class LoginHardeningTests : IDisposable
    {
        private static string MapPath =>
            Path.Combine(AppContext.BaseDirectory, "maps", "avelora-etapa-a.avmap");

        private readonly string _folder;

        public LoginHardeningTests()
        {
            _folder = Path.Combine(
                Path.GetTempPath(),
                "avelora-login-tests-" + Guid.NewGuid().ToString("N"));

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
                // SQLite puede tardar un instante en soltar el archivo en Windows.
            }
        }

        private string DatabasePath => Path.Combine(_folder, "avelora.db");

        // ---------------------------------------------------------------- 1 y 2: PBKDF2

        [Fact]
        public void Una_Cuenta_Nueva_Guarda_Seiscientas_Mil_Iteraciones()
        {
            using (var repository = new SqlitePlayerRepository(DatabasePath))
            {
                repository.Initialize();

                Assert.True(repository.Login("cuenta", "secreto1", "avelora-dev", "avelora-dev").Success);
            }

            Assert.Equal(600_000, ReadIterations(DatabasePath, "cuenta"));

            // Y la contraseña se sigue verificando con ese número de iteraciones.
            using (var reopened = new SqlitePlayerRepository(DatabasePath))
            {
                reopened.Initialize();
                Assert.True(reopened.Login("cuenta", "secreto1", string.Empty, string.Empty).Success);
                Assert.False(reopened.Login("cuenta", "otra-clave", string.Empty, string.Empty).Success);
            }
        }

        [Fact]
        public void Una_Cuenta_Con_Menos_Iteraciones_Se_Rehasha_Al_Entrar()
        {
            using (var old = new SqlitePlayerRepository(DatabasePath, passwordIterations: 1_000))
            {
                old.Initialize();
                Assert.True(old.Login("cuenta", "secreto1", "avelora-dev", "avelora-dev").Success);
            }

            Assert.Equal(1_000, ReadIterations(DatabasePath, "cuenta"));

            using (var upgraded = new SqlitePlayerRepository(DatabasePath, passwordIterations: 2_000))
            {
                upgraded.Initialize();
                Assert.True(upgraded.Login("cuenta", "secreto1", string.Empty, string.Empty).Success);
            }

            Assert.Equal(2_000, ReadIterations(DatabasePath, "cuenta"));

            using (var check = new SqlitePlayerRepository(DatabasePath, passwordIterations: 2_000))
            {
                check.Initialize();
                Assert.True(check.Login("cuenta", "secreto1", string.Empty, string.Empty).Success);
                Assert.False(check.Login("cuenta", "otra-clave", string.Empty, string.Empty).Success);
            }
        }

        // ---------------------------------------------------------------- 3: hilo de login

        [Fact]
        public void La_Red_No_Se_Bloquea_Mientras_El_Login_Tarda()
        {
            var repository = new BlockingRepository();

            try
            {
                using var harness = new Harness(repository);

                harness.Connect(1);
                harness.Handshake(1, "cuenta1");

                // El repositorio está bloqueado dentro de Login: si esto fuera síncrono, PollOnce
                // no volvería nunca.
                var stopwatch = Stopwatch.StartNew();
                harness.Server.PollOnce();
                stopwatch.Stop();

                Assert.True(
                    stopwatch.ElapsedMilliseconds < 1_000,
                    "PollOnce se quedó esperando al login (" + stopwatch.ElapsedMilliseconds + " ms).");

                // Otro saludo se procesa igual: se valida y se encola, sin rechazo.
                harness.Connect(2);
                harness.Handshake(2, "cuenta2");
                harness.Server.PollOnce();

                Assert.False(harness.HasReject(1));
                Assert.False(harness.HasReject(2));

                repository.Gate.Set();

                harness.Pump(1);
                harness.Pump(2);

                Assert.Equal(2, harness.Zone.Entities.Count);
                Assert.Equal(2, harness.Server.Snapshot().LoginsSucceeded);
            }
            finally
            {
                repository.Gate.Set();
                repository.Dispose();
            }
        }

        [Fact]
        public void Resultado_De_Login_Antiguo_No_Autentica_Una_Conexion_Con_Id_Reutilizado()
        {
            var repository = new BlockingRepository();

            try
            {
                using var harness = new Harness(repository);

                harness.Connect(1);
                harness.Handshake(1, "cuenta1");
                harness.Server.PollOnce();
                Assert.True(repository.Started.Wait(5_000), "El primer login no empezó.");

                harness.Transport.RaiseDisconnected(1);
                harness.Connect(1);
                harness.Handshake(1, "cuenta2");
                harness.Server.PollOnce();

                repository.Gate.Set();
                Assert.True(repository.AllFinished.Wait(5_000), "No terminaron los dos logins.");
                harness.Pump(1);

                PlayerEntity player = Assert.IsType<PlayerEntity>(Assert.Single(harness.Zone.Entities.All));
                Assert.Equal(2, player.CharacterId);
            }
            finally
            {
                repository.Gate.Set();
                repository.Dispose();
            }
        }

        // ---------------------------------------------------------------- 4: tope de cola

        [Fact]
        public void Con_La_Cola_De_Login_Llena_Se_Rechaza_Con_RateLimited()
        {
            var repository = new BlockingRepository();

            try
            {
                using var harness = new Harness(repository, config =>
                {
                    config.MaxPendingLogins = 1;
                    config.MaxPendingLoginsPerIp = 1;
                });

                harness.Connect(1);
                harness.Handshake(1, "cuenta1");
                harness.Server.PollOnce();

                Assert.True(repository.Started.Wait(5_000), "El login de la primera conexión no arrancó.");

                harness.Connect(2);
                harness.Handshake(2, "cuenta2");
                harness.Server.PollOnce();

                Assert.True(harness.HasReject(2, out RejectMessage reject));
                Assert.Equal(RejectReason.RateLimited, reject.Reason);

                // No se gasta ni un hash para el que sobra: su cuenta nunca llega al repositorio.
                Assert.DoesNotContain("cuenta2", repository.Accounts);

                repository.Gate.Set();
                harness.Pump(1);
                Assert.Equal(1, harness.Zone.Entities.Count);
            }
            finally
            {
                repository.Gate.Set();
                repository.Dispose();
            }
        }

        [Fact]
        public void Desconectar_No_Libera_El_Tope_De_Logins_En_Vuelo()
        {
            var repository = new BlockingRepository();

            try
            {
                using var harness = new Harness(repository, config => config.MaxPendingLogins = 2);

                // Dos conexiones saludan y se van enseguida: sus peticiones siguen en el hilo de
                // login (una calculándose y otra en la cola).
                harness.Connect(1);
                harness.Handshake(1, "cuenta1");
                harness.Connect(2);
                harness.Handshake(2, "cuenta2");
                harness.Server.PollOnce();

                Assert.True(repository.Started.Wait(5_000), "El login de la primera conexión no arrancó.");

                harness.Transport.RaiseDisconnected(1);
                harness.Transport.RaiseDisconnected(2);

                // El tope cuenta peticiones en vuelo, no conexiones abiertas: la tercera no entra
                // aunque las dos primeras ya no estén conectadas.
                harness.Connect(3);
                harness.Handshake(3, "cuenta3");
                harness.Server.PollOnce();

                Assert.True(harness.HasReject(3, out RejectMessage reject));
                Assert.Equal(RejectReason.RateLimited, reject.Reason);
                Assert.DoesNotContain("cuenta3", repository.Accounts);

                // Al terminar los dos logins, la red saca sus resultados y el tope vuelve a estar
                // libre: la cuarta conexión ya entra.
                repository.Gate.Set();
                Assert.True(
                    repository.AllFinished.Wait(5_000),
                    "El hilo de login no terminó las dos peticiones.");

                harness.Server.PollOnce();

                harness.Connect(4);
                harness.Handshake(4, "cuenta4");
                harness.Pump(4);

                Assert.True(harness.HasWelcome(4));
                Assert.Equal(1, harness.Zone.Entities.Count);
                Assert.Equal(1, harness.Server.Snapshot().LoginsSucceeded);
            }
            finally
            {
                repository.Gate.Set();
                repository.Dispose();
            }
        }

        [Fact]
        public void Una_IP_No_Pasa_Del_Tope_De_Logins_En_Vuelo_Aunque_Cambie_De_Cuenta()
        {
            var repository = new BlockingRepository();

            try
            {
                using var harness = new Harness(repository, config => config.MaxPendingLoginsPerIp = 2);

                for (int id = 1; id <= 3; id++)
                {
                    harness.Transport.Addresses[id] = "10.1.1.1";
                    harness.Connect(id);
                    harness.Handshake(id, "aa" + id);
                }

                harness.Server.PollOnce();

                Assert.True(repository.Started.Wait(5_000), "El login de la primera conexión no arrancó.");
                Assert.True(harness.HasReject(3, out RejectMessage reject));
                Assert.Equal(RejectReason.RateLimited, reject.Reason);
                Assert.DoesNotContain("aa3", repository.Accounts);

                repository.Gate.Set();
                harness.Pump(1);
            }
            finally
            {
                repository.Gate.Set();
                repository.Dispose();
            }
        }

        [Fact]
        public void Otra_IP_Entra_Con_La_Primera_En_El_Tope()
        {
            var repository = new BlockingRepository();

            try
            {
                using var harness = new Harness(repository, config => config.MaxPendingLoginsPerIp = 2);

                harness.Transport.Addresses[1] = "10.1.1.1";
                harness.Transport.Addresses[2] = "10.1.1.1";
                harness.Transport.Addresses[3] = "10.2.2.2";
                harness.Connect(1);
                harness.Handshake(1, "aa1");
                harness.Connect(2);
                harness.Handshake(2, "aa2");
                harness.Connect(3);
                harness.Handshake(3, "aa3");
                harness.Server.PollOnce();

                Assert.True(repository.Started.Wait(5_000), "El login de la primera conexión no arrancó.");
                Assert.False(harness.HasReject(3, out _));

                repository.Gate.Set();
                harness.Pump(3);
                Assert.Contains("aa3", repository.Accounts);
            }
            finally
            {
                repository.Gate.Set();
                repository.Dispose();
            }
        }

        [Fact]
        public void El_Hueco_Por_IP_Se_Libera_Al_Recoger_El_Resultado()
        {
            var repository = new BlockingRepository();

            try
            {
                using var harness = new Harness(repository, config => config.MaxPendingLoginsPerIp = 1);

                harness.Transport.Addresses[1] = "10.1.1.1";
                harness.Connect(1);
                harness.Handshake(1, "aa1");
                harness.Server.PollOnce();
                Assert.True(repository.Started.Wait(5_000), "El login de la primera conexión no arrancó.");

                harness.Transport.RaiseDisconnected(1);
                harness.Transport.Addresses[2] = "10.1.1.1";
                harness.Connect(2);
                harness.Handshake(2, "aa2");
                harness.Server.PollOnce();
                Assert.True(harness.HasReject(2, out RejectMessage reject));
                Assert.Equal(RejectReason.RateLimited, reject.Reason);

                // Este login de otra IP permite esperar a que el hilo procese también el resultado
                // de la conexión desconectada antes de comprobar que se liberó su hueco.
                repository.Gate.Set();
                harness.Transport.Addresses[3] = "10.2.2.2";
                harness.Connect(3);
                harness.Handshake(3, "aa3");
                harness.Pump(3);

                harness.Transport.Addresses[4] = "10.1.1.1";
                harness.Connect(4);
                harness.Handshake(4, "aa4");
                harness.Pump(4);
                Assert.False(harness.HasReject(4, out _));
            }
            finally
            {
                repository.Gate.Set();
                repository.Dispose();
            }
        }

        // ---------------------------------------------------------------- 5: fallos por cuenta

        [Fact]
        public void Tras_Los_Fallos_Permitidos_El_Siguiente_Intento_Se_Rechaza_Sin_Login()
        {
            WaitForStableMinute();

            var repository = new CountingRepository();
            using var harness = new Harness(repository, config => config.MaxFailedLoginsPerAccountPerMinute = 2);

            harness.Connect(1);
            harness.Handshake(1, "dev", "mala-clave");
            harness.Pump(1);

            harness.Connect(2);
            harness.Handshake(2, "dev", "mala-clave");
            harness.Pump(2);

            Assert.True(harness.HasReject(1, out RejectMessage first));
            Assert.Equal(RejectReason.InvalidCredentials, first.Reason);
            Assert.True(harness.HasReject(2, out RejectMessage second));
            Assert.Equal(RejectReason.InvalidCredentials, second.Reason);
            Assert.Equal(2, repository.Calls);

            harness.Connect(3);
            harness.Handshake(3, "dev", "mala-clave");
            harness.Pump(3);

            Assert.True(harness.HasReject(3, out RejectMessage third));
            Assert.Equal(RejectReason.RateLimited, third.Reason);

            // El límite corta antes del repositorio: ni un intento más.
            Assert.Equal(2, repository.Calls);
        }

        [Fact]
        public void El_Limite_De_Fallos_No_Distingue_Mayusculas()
        {
            WaitForStableMinute();

            var repository = new CountingRepository();
            using var harness = new Harness(repository, config => config.MaxFailedLoginsPerAccountPerMinute = 5);

            string[] variantes = { "dev", "Dev", "DEV", "dev", "Dev" };

            for (int i = 0; i < variantes.Length; i++)
            {
                int connectionId = i + 1;
                harness.Connect(connectionId);
                harness.Handshake(connectionId, variantes[i], "mala-clave");
                harness.Pump(connectionId);

                Assert.True(harness.HasReject(connectionId, out RejectMessage reject));
                Assert.Equal(RejectReason.InvalidCredentials, reject.Reason);
            }

            Assert.Equal(5, repository.Calls);

            // Cualquier variante de mayúsculas encuentra la misma cuenta agotada.
            harness.Connect(6);
            harness.Handshake(6, "DeV", "mala-clave");
            harness.Pump(6);

            Assert.True(harness.HasReject(6, out RejectMessage limited));
            Assert.Equal(RejectReason.RateLimited, limited.Reason);
            Assert.Equal(5, repository.Calls);
        }

        [Fact]
        public void Los_Saludos_En_Vuelo_Cuentan_Para_El_Limite_Por_Cuenta()
        {
            WaitForStableMinute();

            var repository = new CountingRepository();
            using var harness = new Harness(repository, config => config.MaxFailedLoginsPerAccountPerMinute = 5);

            // Cinco saludos seguidos sin recoger ningún resultado: aunque el repositorio aún no
            // haya contestado, ya ocupan el cupo de la cuenta.
            for (int i = 1; i <= 5; i++)
            {
                harness.Connect(i);
                harness.Handshake(i, "dev", "mala-clave");
                Assert.False(harness.HasReject(i));
            }

            harness.Connect(6);
            harness.Handshake(6, "dev", "mala-clave");

            Assert.True(harness.HasReject(6, out RejectMessage reject));
            Assert.Equal(RejectReason.RateLimited, reject.Reason);

            // El sexto no llegó al repositorio; los cinco primeros sí.
            harness.Pump(6);

            // El descifrado y PBKDF2 corren en el hilo de login: se espera a que termine los cinco
            // sin debilitar la aserción (el sexto no debe llegar nunca).
            harness.PumpUntil(() => repository.Calls >= 5);
            Assert.Equal(5, repository.Calls);
        }

        [Fact]
        public void Un_Saludo_Reenviado_En_Otra_Conexion_Se_Rechaza_Sin_Llegar_Al_Repositorio()
        {
            var repository = new CountingRepository();
            using var harness = new Harness(repository, config => config.RejectDisconnectDelayMilliseconds = 0);

            harness.Connect(1);
            byte[] hello = harness.HandshakeBytes(1, "dev", "secreto1");

            // La conexión 1 se va sin mandar el saludo; el bloque cifrado (con su nonce) se reenvía
            // tal cual por la conexión 2, que tiene otro desafío.
            harness.Transport.RaiseDisconnected(1);

            harness.Connect(2);
            harness.Transport.RaiseReceived(2, hello);
            harness.Pump(2);

            Assert.True(harness.HasReject(2, out RejectMessage reject));
            Assert.Equal(RejectReason.InvalidCredentials, reject.Reason);
            Assert.Equal(0, repository.Calls);
        }

        [Fact]
        public void Un_Login_Correcto_Deja_El_Contador_En_Vuelo_A_Cero()
        {
            var repository = new BlockingRepository();
            repository.Gate.Set();

            using var harness = new Harness(repository, config => config.MaxFailedLoginsPerAccountPerMinute = 1);

            harness.Connect(1);
            harness.Handshake(1, "dev");
            harness.Pump(1);
            Assert.True(harness.HasWelcome(1));
            Assert.False(harness.HasReject(1));

            // Si el contador en vuelo no se descontara al recoger el resultado, el segundo login
            // de la misma cuenta se rechazaría aunque no hubiera ningún fallo.
            harness.Connect(2);
            harness.Handshake(2, "dev");
            harness.Pump(2);
            Assert.True(harness.HasWelcome(2));
            Assert.False(harness.HasReject(2));
        }

        [Fact]
        public void Una_Invitacion_Incorrecta_De_Cuenta_Inexistente_No_Gasta_El_Cupo()
        {
            WaitForStableMinute();

            var repository = new UnknownAccountRepository();
            using var harness = new Harness(repository, config => config.MaxFailedLoginsPerAccountPerMinute = 1);

            for (int i = 1; i <= 3; i++)
            {
                harness.Connect(i);
                harness.Handshake(i, "fantasma", "mala-clave");
                harness.Pump(i);

                Assert.True(harness.HasReject(i, out RejectMessage reject));
                Assert.Equal(RejectReason.InvalidCredentials, reject.Reason);
            }

            Assert.Equal(3, repository.Calls);
        }

        // ---------------------------------------------------------------- 7: errores de guardado

        [Fact]
        public void Un_Guardado_Que_Falla_Se_Reintenta_Y_Acaba_Guardado()
        {
            var repository = new FlakyRepository(failures: 1);
            using var saves = new CharacterSaveService(repository);

            saves.Enqueue(NewCharacter(7));

            Assert.True(saves.Flush(TimeSpan.FromSeconds(5)));
            Assert.Equal(1L, saves.Saved);
            Assert.Equal(1L, saves.Failed);
            Assert.Equal(0, saves.Pending);
            Assert.Single(repository.Saved);
        }

        [Fact]
        public void Una_Foto_Nueva_Reemplaza_A_La_Que_Esperaba_Reintento()
        {
            var repository = new FlakyRepository(failures: 1);
            using var saves = new CharacterSaveService(repository);

            saves.Enqueue(NewCharacter(7, new Vec2(1f, 2f)));

            long deadline = Environment.TickCount64 + 5_000;
            while (repository.Attempts < 1 && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(10);
            }

            Assert.True(repository.Attempts >= 1, "El primer intento de guardado no llegó a fallar.");

            // La foto vieja está esperando el reintento: la nueva del mismo personaje debe ganar.
            saves.Enqueue(NewCharacter(7, new Vec2(9f, -9f)));

            Assert.True(saves.Flush(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, saves.Pending);
            Assert.Single(repository.Saved);
            Assert.Equal(new Vec2(9f, -9f), repository.Saved[0].Position);
        }

        [Fact]
        public void Un_Fallo_De_Guardado_Deja_LastError_Y_Cuenta_El_Fallo()
        {
            using var saves = new CharacterSaveService(new FailingRepository());

            saves.Enqueue(NewCharacter(7));

            // El servicio reintenta en vez de descartar la foto: si el fallo es permanente, el
            // Flush se agota y la foto sigue pendiente.
            Assert.False(saves.Flush(TimeSpan.FromSeconds(2)));
            Assert.True(saves.Failed >= 1);
            Assert.Contains("disco lleno", saves.LastError);
        }

        // ---------------------------------------------------------------- utilidades

        /// <summary>Lee las iteraciones tal y como quedaron en la tabla, no lo que diga el código.</summary>
        private static int ReadIterations(string databasePath, string accountName)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            };

            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT password_iterations FROM accounts WHERE name = @name COLLATE NOCASE;";
            command.Parameters.AddWithValue("@name", accountName);

            return Convert.ToInt32(command.ExecuteScalar());
        }

        /// <summary>
        /// Duerme hasta empezar un minuto «tranquilo» si estamos a punto de cambiar de minuto: la
        /// ventana de fallos se reinicia con el minuto, y una prueba a caballo entre dos sería falsa.
        /// </summary>
        private static void WaitForStableMinute()
        {
            if (Environment.TickCount64 % 60_000 > 58_000)
            {
                long minute = Environment.TickCount64 / 60_000;

                while (Environment.TickCount64 / 60_000 == minute)
                {
                    Thread.Sleep(10);
                }
            }
        }

        private static CharacterRecord NewCharacter(int id, Vec2 position = default)
        {
            DateTime now = DateTime.UtcNow;
            var hash = new byte[ProtocolConstants.MapHashSize];

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
                hash,
                now,
                now);
        }

        // ---------------------------------------------------------------- dobles

        /// <summary>Repositorio que se queda dentro de Login hasta que la prueba abre la puerta.</summary>
        private sealed class BlockingRepository : IPlayerRepository
        {
            private readonly object _gate = new object();
            private readonly List<string> _accounts = new List<string>();
            private readonly Dictionary<int, List<CharacterRecord>> _characters = new Dictionary<int, List<CharacterRecord>>();
            private int _finished;

            public ManualResetEventSlim Gate { get; } = new ManualResetEventSlim(false);

            /// <summary>Se pone cuando el primer Login empieza de verdad.</summary>
            public ManualResetEventSlim Started { get; } = new ManualResetEventSlim(false);

            /// <summary>Se pone cuando el segundo Login ha vuelto (sus resultados están saliendo).</summary>
            public ManualResetEventSlim AllFinished { get; } = new ManualResetEventSlim(false);

            public IReadOnlyList<string> Accounts
            {
                get
                {
                    lock (_gate)
                    {
                        return new List<string>(_accounts);
                    }
                }
            }

            public void Initialize()
            {
            }

            public AccountLoginResult Login(
                string accountName,
                string password,
                string invitationCode,
                string registrationCode)
            {
                int accountId;
                lock (_gate)
                {
                    _accounts.Add(accountName);
                    accountId = _accounts.Count;
                    _characters[accountId] = new List<CharacterRecord>();
                }

                Started.Set();
                Gate.Wait(TimeSpan.FromSeconds(10));

                if (Interlocked.Increment(ref _finished) >= 2)
                {
                    AllFinished.Set();
                }

                return AccountLoginResult.Ok(accountId);
            }

            public CharacterRecord[] ListCharacters(int accountId)
            {
                lock (_gate)
                {
                    return _characters.TryGetValue(accountId, out List<CharacterRecord>? characters)
                        ? characters.ToArray()
                        : Array.Empty<CharacterRecord>();
                }
            }

            public CharacterCreateResult CreateCharacter(
                int accountId,
                string name,
                byte classId,
                int maxCharacters,
                int zoneId,
                Vec2 spawn,
                byte[] mapHash)
            {
                var character = new CharacterRecord(
                    accountId,
                    accountId,
                    name,
                    1,
                    0,
                    100,
                    100,
                    zoneId,
                    spawn,
                    mapHash,
                    DateTime.UtcNow,
                    DateTime.UtcNow,
                    classId: classId);
                lock (_gate)
                {
                    _characters[accountId].Add(character);
                }

                return CharacterCreateResult.Created(character);
            }

            public bool TryGetCharacter(int accountId, int characterId, out CharacterRecord? character)
            {
                lock (_gate)
                {
                    if (_characters.TryGetValue(accountId, out List<CharacterRecord>? characters))
                    {
                        character = characters.Find(candidate => candidate.Id == characterId);
                        return character != null;
                    }
                }

                character = null;
                return false;
            }

            public void SaveCharacter(CharacterRecord character)
            {
            }

            public void Dispose()
            {
            }
        }

        /// <summary>Repositorio que rechaza cualquier contraseña y cuenta las llamadas.</summary>
        private sealed class CountingRepository : IPlayerRepository
        {
            private int _calls;

            public int Calls => Volatile.Read(ref _calls);

            public void Initialize()
            {
            }

            public AccountLoginResult Login(
                string accountName,
                string password,
                string invitationCode,
                string registrationCode)
            {
                Interlocked.Increment(ref _calls);
                return AccountLoginResult.Failed(
                    RejectReason.InvalidCredentials,
                    countsAsFailedAttempt: true);
            }

            public void SaveCharacter(CharacterRecord character) => throw new NotSupportedException();

            public void Dispose()
            {
            }
        }

        /// <summary>
        /// Repositorio que responde «credenciales inválidas» sin decir que la cuenta exista: es lo
        /// que hace SQLite cuando el nombre no está y la invitación no vale.
        /// </summary>
        private sealed class UnknownAccountRepository : IPlayerRepository
        {
            private int _calls;

            public int Calls => Volatile.Read(ref _calls);

            public void Initialize()
            {
            }

            public AccountLoginResult Login(
                string accountName,
                string password,
                string invitationCode,
                string registrationCode)
            {
                Interlocked.Increment(ref _calls);
                return AccountLoginResult.Failed(RejectReason.InvalidCredentials);
            }

            public void SaveCharacter(CharacterRecord character) => throw new NotSupportedException();

            public void Dispose()
            {
            }
        }

        /// <summary>Repositorio que falla al guardar las primeras veces y después funciona.</summary>
        private sealed class FlakyRepository : IPlayerRepository
        {
            private readonly object _gate = new object();
            private readonly List<CharacterRecord> _saved = new List<CharacterRecord>();
            private int _failuresLeft;
            private int _attempts;

            public FlakyRepository(int failures)
            {
                _failuresLeft = failures;
            }

            public int Attempts => Volatile.Read(ref _attempts);

            public IReadOnlyList<CharacterRecord> Saved
            {
                get
                {
                    lock (_gate)
                    {
                        return new List<CharacterRecord>(_saved);
                    }
                }
            }

            public void Initialize()
            {
            }

            public AccountLoginResult Login(
                string accountName,
                string password,
                string invitationCode,
                string registrationCode) =>
                throw new NotSupportedException();

            public void SaveCharacter(CharacterRecord character)
            {
                Interlocked.Increment(ref _attempts);

                if (Interlocked.Decrement(ref _failuresLeft) >= 0)
                {
                    throw new IOException("disco lleno");
                }

                lock (_gate)
                {
                    _saved.Add(character);
                }
            }

            public void Dispose()
            {
            }
        }

        /// <summary>Repositorio que falla al guardar, como un disco lleno.</summary>
        private sealed class FailingRepository : IPlayerRepository
        {
            public void Initialize()
            {
            }

            public AccountLoginResult Login(
                string accountName,
                string password,
                string invitationCode,
                string registrationCode) =>
                throw new NotSupportedException();

            public void SaveCharacter(CharacterRecord character) =>
                throw new IOException("disco lleno");

            public void Dispose()
            {
            }
        }

        private sealed class Harness : IDisposable
        {
            private readonly Dictionary<int, byte[]> _challengeNonces = new Dictionary<int, byte[]>();
            private readonly HashSet<int> _creationRequestsSent = new HashSet<int>();
            private readonly HashSet<int> _selectionRequestsSent = new HashSet<int>();

            public Harness(IPlayerRepository repository, Action<ServerConfig>? configure = null)
            {
                Config = new ServerConfig { HandshakeTimeoutSeconds = 60d };
                configure?.Invoke(Config);

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
                Saves = new CharacterSaveService(repository);
                Server = new NetworkServer(
                    Zone,
                    Config,
                    Transport,
                    MapHash,
                    repository,
                    Saves,
                    Config.RegistrationInvitationCode);
            }

            public ServerConfig Config { get; }

            public Zone Zone { get; }

            public byte[] MapHash { get; }

            public FakeServerTransport Transport { get; }

            public CharacterSaveService Saves { get; }

            public NetworkServer Server { get; }

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

            public void Handshake(int connectionId, string account, string password = "secreto1")
            {
                Transport.RaiseReceived(connectionId, HandshakeBytes(connectionId, account, password));
            }

            /// <summary>Codifica un saludo cifrado con el nonce del desafío de esa conexión.</summary>
            public byte[] HandshakeBytes(int connectionId, string account, string password = "secreto1")
            {
                byte[] encrypted = HelloSecrets.Encrypt(
                    Server.PublicKey,
                    _challengeNonces[connectionId],
                    password,
                    Config.RegistrationInvitationCode);

                var hello = new HelloMessage(
                    ProtocolConstants.Version,
                    (ushort)SimulationConstants.TickRate,
                    MapHash,
                    1000u + (uint)connectionId,
                    account,
                    encrypted);

                return MessageCodec.EncodeHello(hello);
            }

            /// <summary>Alterna los dos hilos hasta que la conexión reciba su desenlace.</summary>
            public void Pump(int connectionId)
            {
                string characterName = "bot" + connectionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                long deadline = Environment.TickCount64 + 5_000;

                while (true)
                {
                    Server.BeforeTick(Zone);
                    Server.PollOnce();

                    for (int i = 0; i < Transport.Sent.Count; i++)
                    {
                        SentMessage sent = Transport.Sent[i];
                        if (sent.ConnectionId != connectionId ||
                            !MessageCodec.TryReadCharacterList(sent.Data, 0, sent.Data.Length, out CharacterListMessage list))
                        {
                            continue;
                        }

                        if (list.Characters.Length > 0 && _selectionRequestsSent.Add(connectionId))
                        {
                            byte[] select = MessageCodec.EncodeSelectCharacter(
                                new SelectCharacterMessage(list.Characters[0].CharacterId));
                            Transport.RaiseReceived(connectionId, select);
                        }
                        else if (list.Characters.Length == 0 && list.Classes.Length > 0 &&
                            _creationRequestsSent.Add(connectionId))
                        {
                            byte[] create = MessageCodec.EncodeCreateCharacter(
                                new CreateCharacterMessage(characterName, list.Classes[0].ClassId));
                            Transport.RaiseReceived(connectionId, create);
                        }
                    }

                    if (HasWelcome(connectionId) || HasReject(connectionId) || HasCharacterRejected(connectionId))
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

            /// <summary>Alterna los dos hilos hasta que se cumpla una condición, con tope de tiempo.</summary>
            public void PumpUntil(Func<bool> condition)
            {
                long deadline = Environment.TickCount64 + 5_000;

                while (!condition())
                {
                    if (Environment.TickCount64 >= deadline)
                    {
                        throw new TimeoutException("La condición no se cumplió a tiempo.");
                    }

                    Server.BeforeTick(Zone);
                    Server.PollOnce();
                    PumpCharacterLists();
                    Thread.Sleep(1);
                }
            }

            private void PumpCharacterLists()
            {
                for (int i = 0; i < Transport.Sent.Count; i++)
                {
                    SentMessage sent = Transport.Sent[i];
                    if (MessageCodec.TryReadCharacterList(sent.Data, 0, sent.Data.Length, out CharacterListMessage list))
                    {
                        if (list.Characters.Length > 0 && _selectionRequestsSent.Add(sent.ConnectionId))
                        {
                            byte[] select = MessageCodec.EncodeSelectCharacter(
                                new SelectCharacterMessage(list.Characters[0].CharacterId));
                            Transport.RaiseReceived(sent.ConnectionId, select);
                        }
                        else if (list.Characters.Length == 0 && list.Classes.Length > 0 &&
                            _creationRequestsSent.Add(sent.ConnectionId))
                        {
                            byte[] create = MessageCodec.EncodeCreateCharacter(new CreateCharacterMessage(
                                "bot" + sent.ConnectionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                list.Classes[0].ClassId));
                            Transport.RaiseReceived(sent.ConnectionId, create);
                        }
                    }
                }
            }

            private bool HasCharacterRejected(int connectionId)
            {
                for (int i = 0; i < Transport.Sent.Count; i++)
                {
                    SentMessage sent = Transport.Sent[i];
                    if (sent.ConnectionId == connectionId &&
                        MessageCodec.TryReadCharacterRejected(sent.Data, 0, sent.Data.Length, out _))
                    {
                        return true;
                    }
                }

                return false;
            }

            public bool HasWelcome(int connectionId)
            {
                for (int i = 0; i < Transport.Sent.Count; i++)
                {
                    SentMessage sent = Transport.Sent[i];

                    if (sent.ConnectionId == connectionId &&
                        MessageCodec.TryReadWelcome(sent.Data, 0, sent.Data.Length, out _))
                    {
                        return true;
                    }
                }

                return false;
            }

            public bool HasReject(int connectionId) => HasReject(connectionId, out _);

            public bool HasReject(int connectionId, out RejectMessage reject)
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
            }
        }

        private sealed class FakeServerTransport : IServerTransport
        {
            public List<SentMessage> Sent { get; } = new List<SentMessage>();

            public List<int> DisconnectedConnections { get; } = new List<int>();

            public Dictionary<int, string> Addresses { get; } = new Dictionary<int, string>();

            public int Port => 0;

            public int ConnectionCount => 0;

            public string GetRemoteAddress(int connectionId) =>
                Addresses.TryGetValue(connectionId, out string? address) ? address : "10.0.0." + connectionId;

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
