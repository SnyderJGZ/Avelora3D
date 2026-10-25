#nullable enable
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Avelora.Net;
using Avelora.Server.Configuration;
using Avelora.Server.Diagnostics;
using Avelora.Server.Entities;
using Avelora.Server.Net;
using Avelora.Server.Items;
using Avelora.Server.Npcs;
using Avelora.Server.Persistence;
using Avelora.Server.Security;
using Avelora.Server.Simulation;
using Avelora.Shared;
using Avelora.Shared.Maps;

namespace Avelora.Server
{
    /// <summary>
    /// Arranque del servidor: lee la configuración, carga el mapa, monta la zona, abre el socket y
    /// pone a correr el bucle de ticks.
    /// </summary>
    /// <remarks>
    /// Este archivo solo <b>compone</b> piezas; no simula nada. Es a propósito: cuando lleguen la
    /// base de datos y las zonas múltiples, lo que cambia es el montaje, no el tick
    /// (plan del servidor §3, «costuras para crecer»).
    ///
    /// Uso:
    /// <code>
    /// dotnet run --project server/Avelora.Server                 (toma server.json)
    /// dotnet run --project server/Avelora.Server -- mi.json      (otra configuración)
    /// dotnet run --project server/Avelora.Server -- --seconds 10 (se para solo a los 10 s)
    /// </code>
    /// </remarks>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (!TryParseArguments(
                args,
                out string? configArgument,
                out double? secondsOverride,
                out bool checkDatabase,
                out string? parseError))
            {
                ConsoleReporter.WriteError(parseError!);
                return 1;
            }

            string configPath;
            ServerConfig config;

            try
            {
                configPath = ServerConfig.ResolveConfigPath(configArgument);
                config = ServerConfig.Load(configPath);
            }
            catch (Exception ex) when (ex is IOException || ex is System.Text.Json.JsonException || ex is InvalidOperationException)
            {
                ConsoleReporter.WriteError("No se pudo leer la configuración: " + ex.Message);
                return 1;
            }

            if (secondsOverride.HasValue)
            {
                config.RunSeconds = secondsOverride.Value;
            }

            // --check-db no arranca el servidor: comprueba la base de esta configuración y sale.
            if (checkDatabase)
            {
                return CheckDatabase(configPath, config);
            }

            string mapPath;
            MapCollisionGrid map;

            try
            {
                mapPath = config.ResolveMapPath(configPath);
                map = MapFile.Load(mapPath);
            }
            catch (MapFormatException ex)
            {
                ConsoleReporter.WriteError("El mapa no es válido: " + ex.Message);
                return 2;
            }
            catch (IOException ex)
            {
                ConsoleReporter.WriteError("No se pudo leer el mapa: " + ex.Message);
                return 2;
            }

            byte[] mapHash = MapFile.ComputeContentHashBytes(map);
            string mapHashHex = MapFile.ToHexString(mapHash);

            // Objetos (I-04): el catálogo se valida al arrancar y también sirve para comprobar las
            // referencias del botín de npcs.json. Una ruta vacía desactiva el catálogo.
            ItemCatalog items;
            try
            {
                items = string.IsNullOrWhiteSpace(config.ItemsPath)
                    ? new ItemCatalog()
                    : ItemCatalog.Load(config.ResolveItemsPath(configPath));
                items.Validate();
            }
            catch (Exception ex) when (
                ex is IOException || ex is System.Text.Json.JsonException || ex is InvalidOperationException)
            {
                ConsoleReporter.WriteError("No se pudieron cargar los objetos: " + ex.Message);
                return 2;
            }

            // Enemigos (I-01). Con npcsPath vacío la zona se queda sin PNJ. Con ruta, el archivo
            // tiene que existir y ser válido contra el mapa y el catálogo de objetos.
            NpcCatalog? npcs = null;

            if (!string.IsNullOrWhiteSpace(config.NpcsPath))
            {
                try
                {
                    string npcPath = config.ResolveNpcsPath(configPath);
                    npcs = NpcCatalog.Load(npcPath);
                    npcs.Validate(map, items);
                }
                catch (Exception ex) when (
                    ex is IOException ||
                    ex is System.Text.Json.JsonException ||
                    ex is InvalidOperationException)
                {
                    ConsoleReporter.WriteError("No se pudieron cargar los enemigos: " + ex.Message);
                    return 2;
                }
            }

            // Persistencia (Etapa F): una base SQLite y un hilo de guardado. La zona y el tick no
            // saben nada de SQLite; solo reciben personajes ya cargados y entregan fotos.
            IPlayerRepository? repository = null;
            CharacterSaveService? saves = null;
            string? databasePath = null;

            if (config.PersistenceEnabled)
            {
                try
                {
                    databasePath = config.ResolveDatabasePath(configPath);
                    repository = new SqlitePlayerRepository(
                        databasePath,
                        items: items,
                        inventorySlots: config.InventorySlots);
                    repository.Initialize();
                    saves = new CharacterSaveService(repository);
                }
                catch (Exception ex) when (
                    ex is IOException ||
                    ex is UnauthorizedAccessException ||
                    ex is InvalidOperationException ||
                    ex is Microsoft.Data.Sqlite.SqliteException)
                {
                    saves?.Dispose();
                    repository?.Dispose();
                    ConsoleReporter.WriteError("No se pudo abrir la persistencia: " + ex.Message);
                    return 3;
                }
            }

            using var repositoryScope = repository;
            using var savesScope = saves;

            // Clave del saludo (H-02). Con persistencia se guarda en data/server-key.pem y se
            // reutiliza entre arranques; sin ella se genera una efímera. La huella impresa es la
            // que el cliente puede fijar para no aceptar otra clave.
            ServerKey serverKey;

            try
            {
                serverKey = ServerKey.LoadOrCreate(
                    config.PersistenceEnabled ? config.ResolveServerKeyPath(configPath) : string.Empty,
                    config.PersistenceEnabled);
            }
            catch (Exception ex) when (
                ex is IOException ||
                ex is UnauthorizedAccessException ||
                ex is InvalidOperationException ||
                ex is CryptographicException)
            {
                ConsoleReporter.WriteError("No se pudo preparar la clave del servidor: " + ex.Message);
                return 3;
            }

            using var serverKeyScope = serverKey;

            // Montaje de la zona. La zona nace vacía: los jugadores entran por la red, no por código.
            var zone = new Zone(
                config.ZoneId,
                map,
                new EntityStore(),
                new EntityIdAllocator(),
                new InputQueue(config.MaxQueuedIntents),
                new MovementSystem(config.PlayerSpeed, SimulationConstants.FixedDt),
                config.MaxIntentsPerTick,
                config.MaxIntentsPerPlayerPerTick,
                new InterestSettings(config.InterestRadius, config.InterestCellSize, config.InterestHysteresis),
                npcs,
                config.NpcSeed,
                new AttackQueue(config.MaxQueuedAttacks),
                new CombatSettings(
                    config.PlayerAttackDamage,
                    config.PlayerAttackRange,
                    config.AttackRangeTolerance,
                    config.PlayerAttackCooldownSeconds),
                config.PlayerVitals,
                items,
                new ItemActionQueue(config.MaxQueuedItemActions),
                config.InventorySlots,
                config.MaxGroundItems,
                (long)Math.Ceiling(config.GroundItemSeconds * SimulationConstants.TickRate),
                config.PickupRange,
                config.AttackRangeTolerance,
                (long)Math.Ceiling(config.PotionCooldownSeconds * SimulationConstants.TickRate));

            try
            {
                config.ValidateSpawn(map);
            }
            catch (InvalidOperationException ex)
            {
                ConsoleReporter.WriteError(ex.Message);
                return 2;
            }

            using var network = new NetworkServer(
                zone,
                config,
                new LiteNetLibServerTransport(config.MaxConnections, config.MaxConnectionsPerIp),
                mapHash,
                repository,
                saves,
                config.RegistrationInvitationCode,
                serverKey.Rsa);

            try
            {
                network.Start();
            }
            catch (InvalidOperationException ex)
            {
                ConsoleReporter.WriteError(ex.Message);
                return 3;
            }

            ConsoleReporter.WriteStartup(
                configPath,
                mapPath,
                map,
                mapHashHex,
                config,
                network.Port,
                databasePath,
                network.KeyFingerprint,
                npcs);

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                // Ctrl+C no mata el proceso: se pide el paro y se deja cerrar en orden, para poder
                // imprimir el resumen y no perder lo que se había medido.
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            // Cerrar la consola o apagar el proceso también debe guardar. En Windows, SIGHUP es el
            // cierre de la ventana (el sistema concede unos 5 s) y SIGTERM el apagado. Los registros
            // viven hasta el final de Main; si se soltaran, el paro volvería al comportamiento
            // brusco por defecto.
            int signalStop = 0;

            // Al volver del manejador de SIGHUP, Windows termina el proceso: el manejador espera a
            // que Main guarde e imprima el resumen. Se deja sin disponer porque puede haber un
            // manejador esperando en el mismo instante en que Main acaba.
            var shutdownCompleted = new ManualResetEventSlim(false);

            using PosixSignalRegistration sigterm = PosixSignalRegistration.Create(
                PosixSignal.SIGTERM,
                context =>
                {
                    context.Cancel = true;
                    Interlocked.Exchange(ref signalStop, 1);
                    cancellation.Cancel();
                    shutdownCompleted.Wait(TimeSpan.FromSeconds(4));
                });

            using PosixSignalRegistration sighup = PosixSignalRegistration.Create(
                PosixSignal.SIGHUP,
                context =>
                {
                    context.Cancel = true;
                    Interlocked.Exchange(ref signalStop, 1);
                    cancellation.Cancel();
                    shutdownCompleted.Wait(TimeSpan.FromSeconds(4));
                });

            var core = new ServerCore(zone, SimulationConstants.StepMs, config.MetricsWindowTicks)
            {
                Observer = network,
            };

            core.Start(cancellation.Token);

            var stopwatch = Stopwatch.StartNew();
            double nextReportAt = config.ReportIntervalSeconds;

            while (!cancellation.IsCancellationRequested)
            {
                Thread.Sleep(50);

                double elapsed = stopwatch.Elapsed.TotalSeconds;
                if (elapsed >= nextReportAt)
                {
                    ConsoleReporter.WriteReport(elapsed, zone, core.Metrics, network.Snapshot());
                    nextReportAt += config.ReportIntervalSeconds;
                }

                if (config.RunSeconds > 0d && elapsed >= config.RunSeconds)
                {
                    cancellation.Cancel();
                }
            }

            double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;

            // Parar en orden: primero la simulación (para que nadie toque el mundo), después la red
            // y solo al final el guardado. Si la simulación no terminó a tiempo, recorrer sus
            // sesiones sería una carrera: es preferible perder lo más reciente a corromperlo.
            bool simulationStopped = core.Join(TimeSpan.FromSeconds(2));

            network.Stop();

            if (simulationStopped)
            {
                network.SaveAll(zone);
            }
            else
            {
                ConsoleReporter.WriteError(
                    "El hilo de simulación no terminó a tiempo: no se guardan los jugadores conectados.");
            }

            // Cerrar la ventana o apagar solo da unos 5 s en total: si la parada vino por una de
            // esas señales, se recorta la espera del guardado para intentar llegar a imprimir el
            // resumen. Ctrl+C y el final por --seconds conservan los 5 s.
            TimeSpan flushTimeout = signalStop != 0
                ? TimeSpan.FromSeconds(3)
                : TimeSpan.FromSeconds(5);

            if (saves != null && !saves.Flush(flushTimeout))
            {
                ConsoleReporter.WriteError(
                    "Quedaron " + saves.Pending + " personaje(s) sin guardar al cerrar.");
            }

            // Se mide antes de cerrar: el tiempo de parada no debe contar como ticks perdidos.
            ConsoleReporter.WriteSummary(elapsedSeconds, zone, core.Metrics, network.Snapshot(), 0);

            // Despierta a los manejadores de SIGHUP/SIGTERM: el resumen y el guardado ya están
            // hechos y, en el cierre de ventana, al devolver el manejador Windows mata el proceso.
            shutdownCompleted.Set();
            return 0;
        }

        /// <summary>
        /// <c>--check-db</c>: abre la base de la configuración en solo lectura, ejecuta
        /// <c>integrity_check</c> y <c>foreign_key_check</c> e imprime el recuento de cuentas y
        /// personajes. Devuelve 0 si todo está bien y 1 si no.
        /// </summary>
        private static int CheckDatabase(string configPath, ServerConfig config)
        {
            Console.WriteLine("Avelora · comprobación de la base de datos");
            Console.WriteLine("  Configuración : " + configPath);

            if (!config.PersistenceEnabled)
            {
                ConsoleReporter.WriteError(
                    "La persistencia está desactivada en esta configuración: no hay base que comprobar.");
                return 1;
            }

            string databasePath;

            try
            {
                databasePath = config.ResolveDatabasePath(configPath);
            }
            catch (InvalidOperationException ex)
            {
                ConsoleReporter.WriteError(ex.Message);
                return 1;
            }

            DatabaseCheckResult result = DatabaseCheck.Run(databasePath);

            Console.WriteLine("  Base          : " + result.DatabasePath);

            if (result.Error != null)
            {
                ConsoleReporter.WriteError("No se pudo comprobar la base: " + result.Error);
                return 1;
            }

            Console.WriteLine("  Integridad    : " + DescribeLines(result.IntegrityMessages));

            if (result.ForeignKeyViolations.Count == 0)
            {
                Console.WriteLine("  Claves ajenas : ok (0 filas huérfanas)");
            }
            else
            {
                Console.WriteLine(
                    "  Claves ajenas : " + result.ForeignKeyViolations.Count + " fila(s) huérfana(s)");

                for (int i = 0; i < result.ForeignKeyViolations.Count; i++)
                {
                    Console.WriteLine("                  " + result.ForeignKeyViolations[i]);
                }
            }

            Console.WriteLine("  Cuentas       : " + result.Accounts);
            Console.WriteLine("  Personajes    : " + result.Characters);
            Console.WriteLine("  Resultado     : " + (result.Ok ? "correcta" : "CON ERRORES"));

            return result.Ok ? 0 : 1;
        }

        private static string DescribeLines(System.Collections.Generic.IReadOnlyList<string> lines) =>
            lines.Count == 0 ? "(sin respuesta)" : string.Join("; ", lines);

        /// <summary>
        /// Lee los argumentos: el primero que no empiece por <c>--</c> es la configuración,
        /// <c>--seconds N</c> fija la duración y <c>--check-db</c> comprueba la base y sale.
        /// </summary>
        private static bool TryParseArguments(
            string[] args,
            out string? configArgument,
            out double? secondsOverride,
            out bool checkDatabase,
            out string? error)
        {
            configArgument = null;
            secondsOverride = null;
            checkDatabase = false;
            error = null;

            for (int i = 0; i < args.Length; i++)
            {
                string argument = args[i];

                if (string.Equals(argument, "--check-db", StringComparison.Ordinal))
                {
                    checkDatabase = true;
                    continue;
                }

                if (string.Equals(argument, "--seconds", StringComparison.Ordinal))
                {
                    if (i + 1 >= args.Length)
                    {
                        error = "--seconds necesita un número de segundos.";
                        return false;
                    }

                    if (!double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds < 0d)
                    {
                        error = "No entiendo la duración '" + args[i + 1] + "'.";
                        return false;
                    }

                    secondsOverride = seconds;
                    i++;
                    continue;
                }

                if (argument.StartsWith("--", StringComparison.Ordinal))
                {
                    error = "Opción desconocida: " + argument;
                    return false;
                }

                configArgument = argument;
            }

            return true;
        }
    }
}
