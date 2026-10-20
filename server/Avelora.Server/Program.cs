#nullable enable
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using Avelora.Net;
using Avelora.Server.Configuration;
using Avelora.Server.Diagnostics;
using Avelora.Server.Entities;
using Avelora.Server.Net;
using Avelora.Server.Persistence;
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
            if (!TryParseArguments(args, out string? configArgument, out double? secondsOverride, out string? parseError))
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
                    repository = new SqlitePlayerRepository(databasePath);
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
                new InterestSettings(config.InterestRadius, config.InterestCellSize, config.InterestHysteresis));

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
                new LiteNetLibServerTransport(config.MaxConnections),
                mapHash,
                repository,
                saves,
                config.RegistrationInvitationCode);

            try
            {
                network.Start();
            }
            catch (InvalidOperationException ex)
            {
                ConsoleReporter.WriteError(ex.Message);
                return 3;
            }

            ConsoleReporter.WriteStartup(configPath, mapPath, map, mapHashHex, config, network.Port, databasePath);

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                // Ctrl+C no mata el proceso: se pide el paro y se deja cerrar en orden, para poder
                // imprimir el resumen y no perder lo que se había medido.
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

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

            core.Join(TimeSpan.FromSeconds(2));

            // El hilo de simulación ya no toca el mundo: se puede recorrer para guardar a todos los
            // que seguían conectados cuando llegó la orden de parar.
            network.SaveAll(zone);
            saves?.Flush(TimeSpan.FromSeconds(5));
            network.Stop();

            // Se mide antes de cerrar: el tiempo de parada no debe contar como ticks perdidos.
            ConsoleReporter.WriteSummary(elapsedSeconds, zone, core.Metrics, network.Snapshot(), 0);
            return 0;
        }

        /// <summary>
        /// Lee los argumentos: el primero que no empiece por <c>--</c> es la configuración y
        /// <c>--seconds N</c> fija la duración (útil para pruebas automáticas).
        /// </summary>
        private static bool TryParseArguments(
            string[] args,
            out string? configArgument,
            out double? secondsOverride,
            out string? error)
        {
            configArgument = null;
            secondsOverride = null;
            error = null;

            for (int i = 0; i < args.Length; i++)
            {
                string argument = args[i];

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
