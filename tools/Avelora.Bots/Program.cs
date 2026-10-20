#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Avelora.Shared;
using Avelora.Shared.Maps;

namespace Avelora.Bots
{
    /// <summary>
    /// Lanza varios clientes sin ventana contra un servidor Avelora y resume lo que ha pasado.
    /// </summary>
    /// <remarks>
    /// Uso:
    /// <code>
    /// dotnet run --project tools/Avelora.Bots
    /// dotnet run --project tools/Avelora.Bots -- --bots 3 --seconds 10
    /// dotnet run --project tools/Avelora.Bots -- --bots 50 --ramp-ms 200 --behavior crowd
    /// dotnet run --project tools/Avelora.Bots -- --host 127.0.0.1 --port 7777 --bots 10
    /// </code>
    /// </remarks>
    internal static class Program
    {
        /// <summary>Tope de bots: la prueba de carga del plan pide 30–50 y se deja margen hasta 100.</summary>
        private const int MaxBots = 100;

        private static int Main(string[] args)
        {
            if (!TryParseArguments(args, out Options options, out string? error))
            {
                Console.Error.WriteLine("ERROR: " + error);
                return 1;
            }

            string mapPath;
            MapCollisionGrid map;

            try
            {
                mapPath = ResolveMapPath(options.MapPath);
                map = MapFile.Load(mapPath);
            }
            catch (Exception ex) when (ex is IOException || ex is MapFormatException)
            {
                Console.Error.WriteLine("ERROR: no se pudo cargar el mapa '" + options.MapPath + "': " + ex.Message);
                return 2;
            }

            byte[] mapHash = MapFile.ComputeContentHashBytes(map);

            Console.WriteLine("Avelora · bots (Etapa G: prueba de carga)");
            Console.WriteLine("  Servidor : " + options.Host + ":" + options.Port);
            Console.WriteLine("  Bots     : " + options.Bots + " durante " + Invariant(options.Seconds) + " s" +
                " (rampa " + options.RampMs + " ms, " + BehaviorName(options.Behavior) + ")");
            Console.WriteLine("  Reintento: hasta " + options.Retries + " vez/veces si el login se limita; " +
                "saludo esperado " + options.WelcomeTimeoutMs + " ms por intento");
            Console.WriteLine("  Cuentas  : " + options.AccountPrefix + "1.." + options.AccountPrefix + options.Bots +
                " (invitación '" + options.InvitationCode + "')");
            Console.WriteLine("  Mapa     : " + mapPath + "  huella " + ShortHash(MapFile.ToHexString(mapHash)));
            Console.WriteLine();

            // El margen cubre la rampa y lo que puede tardar un reintento; las esperas de cada
            // bot ya tienen su propio tope, así que esto solo evita que la prueba no acabe nunca.
            double rampSeconds = (options.Bots - 1) * options.RampMs / 1000d;
            double retrySeconds = options.Retries * ((options.WelcomeTimeoutMs / 1000d) + 5d);
            using var cancellation = new CancellationTokenSource(
                TimeSpan.FromSeconds(options.Seconds + rampSeconds + retrySeconds + 30d));

            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            int runMs = (int)Math.Round(options.Seconds * 1000d, MidpointRounding.AwayFromZero);
            var bots = new List<BotClient>(options.Bots);
            var threads = new List<Thread>(options.Bots);

            for (int i = 1; i <= options.Bots; i++)
            {
                var bot = new BotClient(
                    i,
                    options.Host,
                    options.Port,
                    mapHash,
                    options.Seed,
                    runMs,
                    options.AccountPrefix,
                    options.Password,
                    options.InvitationCode,
                    startDelayMs: (i - 1) * options.RampMs,
                    retries: options.Retries,
                    welcomeTimeoutMs: options.WelcomeTimeoutMs,
                    behavior: options.Behavior);
                bots.Add(bot);

                var thread = new Thread(() => RunBot(bot, cancellation.Token))
                {
                    Name = "avelora-bot-" + i,
                    IsBackground = true,
                };

                threads.Add(thread);
                thread.Start();
            }

            for (int i = 0; i < threads.Count; i++)
            {
                threads[i].Join();
            }

            for (int i = 0; i < bots.Count; i++)
            {
                bots[i].Dispose();
            }

            return WriteSummary(bots, options);
        }

        private static int WriteSummary(List<BotClient> bots, Options options)
        {
            Console.WriteLine();
            Console.WriteLine("Resumen");

            int ok = 0;
            long snapshots = 0;
            int retryingBots = 0;
            int retries = 0;
            long worstGap = 0;
            double snapshotsPerSecond = 0d;
            var welcomeTimes = new List<long>(bots.Count);
            var rejects = new SortedDictionary<string, int>(StringComparer.Ordinal);

            for (int i = 0; i < bots.Count; i++)
            {
                BotClient bot = bots[i];
                snapshots += bot.Snapshots;
                retries += bot.Retries;

                if (bot.Retries > 0)
                {
                    retryingBots++;
                }

                if (bot.MaxSnapshotGapMs > worstGap)
                {
                    worstGap = bot.MaxSnapshotGapMs;
                }

                if (bot.EntityId > 0)
                {
                    ok++;
                    welcomeTimes.Add(bot.WelcomeMs);
                    snapshotsPerSecond += bot.SnapshotsPerSecond;
                }
                else
                {
                    string reason = bot.RejectReasonText ?? bot.Status;
                    rejects.TryGetValue(reason, out int count);
                    rejects[reason] = count + 1;
                }

                Console.WriteLine(
                    "  bot " + bot.Index + ": " + bot.Status.PadRight(14) +
                    " cuenta=" + bot.AccountName +
                    " ent=" + (bot.EntityId > 0 ? bot.EntityId.ToString(CultureInfo.InvariantCulture) : "-") +
                    " reint=" + bot.Retries +
                    " welcome=" + (bot.WelcomeMs >= 0 ? bot.WelcomeMs + "ms" : "-") +
                    " snap/s=" + Invariant(bot.SnapshotsPerSecond) +
                    " hueco=" + bot.MaxSnapshotGapMs + "ms" +
                    " pos=(" + Invariant(bot.X) + "; " + Invariant(bot.Y) + ")" +
                    " inputs=" + bot.Sequence +
                    " snapshots=" + bot.Snapshots);
            }

            welcomeTimes.Sort();
            double meanPerBot = ok == 0 ? 0d : snapshotsPerSecond / ok;
            long p50 = welcomeTimes.Count == 0 ? -1 : welcomeTimes[(welcomeTimes.Count - 1) / 2];
            long maxWelcome = welcomeTimes.Count == 0 ? -1 : welcomeTimes[welcomeTimes.Count - 1];

            Console.WriteLine();
            Console.WriteLine("  Conectados : " + ok + "/" + bots.Count);
            Console.WriteLine(
                "  Reintentos : " + retries + " en " + retryingBots + " bot(s) (tope " + options.Retries + "/bot)");
            Console.WriteLine("  Rechazos   : " + DescribeRejects(rejects));
            Console.WriteLine(
                "  Welcome    : p50 " + (p50 >= 0 ? p50 + " ms" : "-") +
                ", máximo " + (maxWelcome >= 0 ? maxWelcome + " ms" : "-") +
                " (" + welcomeTimes.Count + " bot(s))");
            Console.WriteLine("  Snapshots  : " + Invariant(meanPerBot) + "/s por bot de media, " +
                snapshots + " en total");
            Console.WriteLine("  Peor hueco : " + worstGap + " ms entre snapshots");
            return ok == bots.Count ? 0 : 3;
        }

        private static string DescribeRejects(SortedDictionary<string, int> rejects)
        {
            if (rejects.Count == 0)
            {
                return "ninguno";
            }

            var text = new System.Text.StringBuilder();
            foreach (KeyValuePair<string, int> pair in rejects)
            {
                if (text.Length > 0)
                {
                    text.Append(", ");
                }

                text.Append(pair.Key).Append('=').Append(pair.Value);
            }

            return text.ToString();
        }

        private static string BehaviorName(BotBehavior behavior) =>
            behavior == BotBehavior.Crowd ? "todos en el mismo punto" : "direcciones al azar";

        private static void RunBot(BotClient bot, CancellationToken cancellationToken)
        {
            try
            {
                bot.Run(cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  bot " + bot.Index + ": error inesperado: " + ex.Message);
            }

            Console.WriteLine("  [bot " + bot.Index + "] " + bot.Status + " ent=" + bot.EntityId);
        }

        private static string ResolveMapPath(string requested)
        {
            if (Path.IsPathRooted(requested))
            {
                return requested;
            }

            string inCurrent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, requested));
            if (File.Exists(inCurrent))
            {
                return inCurrent;
            }

            string besideExecutable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, requested));
            if (File.Exists(besideExecutable))
            {
                return besideExecutable;
            }

            throw new FileNotFoundException("No se encuentra el mapa '" + requested + "'.");
        }

        private static string Invariant(double value) =>
            value.ToString("0.000", CultureInfo.InvariantCulture);

        private static string ShortHash(string hash) =>
            hash.Length <= 16 ? hash : hash.Substring(0, 16) + "…";

        private static bool TryParseArguments(string[] args, out Options options, out string? error)
        {
            options = new Options();
            error = null;

            for (int i = 0; i < args.Length; i++)
            {
                string argument = args[i];

                if (!argument.StartsWith("--", StringComparison.Ordinal))
                {
                    error = "Argumento inesperado: " + argument;
                    return false;
                }

                if (i + 1 >= args.Length)
                {
                    error = argument + " necesita un valor.";
                    return false;
                }

                string value = args[++i];

                switch (argument)
                {
                    case "--host":
                        options.Host = value;
                        break;

                    case "--port":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) || port < 1 || port > 65535)
                        {
                            error = "Puerto inválido: " + value;
                            return false;
                        }

                        options.Port = port;
                        break;

                    case "--bots":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bots) || bots < 1 || bots > MaxBots)
                        {
                            error = "Número de bots inválido: " + value + " (1.." + MaxBots + ")";
                            return false;
                        }

                        options.Bots = bots;
                        break;

                    case "--seconds":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds <= 0d)
                        {
                            error = "Duración inválida: " + value;
                            return false;
                        }

                        options.Seconds = seconds;
                        break;

                    case "--ramp-ms":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rampMs) || rampMs < 0)
                        {
                            error = "Rampa inválida: " + value;
                            return false;
                        }

                        options.RampMs = rampMs;
                        break;

                    case "--retries":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int retries) || retries < 0)
                        {
                            error = "Número de reintentos inválido: " + value;
                            return false;
                        }

                        options.Retries = retries;
                        break;

                    case "--welcome-timeout-ms":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int welcomeTimeoutMs) || welcomeTimeoutMs < 1)
                        {
                            error = "Tiempo de espera del saludo inválido: " + value;
                            return false;
                        }

                        options.WelcomeTimeoutMs = welcomeTimeoutMs;
                        break;

                    case "--behavior":
                        if (!TryParseBehavior(value, out BotBehavior behavior))
                        {
                            error = "Comportamiento desconocido: " + value + " (random|crowd)";
                            return false;
                        }

                        options.Behavior = behavior;
                        break;

                    case "--seed":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed))
                        {
                            error = "Semilla inválida: " + value;
                            return false;
                        }

                        options.Seed = seed;
                        break;

                    case "--map":
                        options.MapPath = value;
                        break;

                    case "--account-prefix":
                        if (string.IsNullOrWhiteSpace(value) || value.Length > 28)
                        {
                            error = "El prefijo de cuenta debe tener entre 1 y 28 caracteres.";
                            return false;
                        }

                        options.AccountPrefix = value;
                        break;

                    case "--password":
                        if (string.IsNullOrWhiteSpace(value))
                        {
                            error = "La contraseña no puede estar vacía.";
                            return false;
                        }

                        options.Password = value;
                        break;

                    case "--invitation":
                        options.InvitationCode = value;
                        break;

                    default:
                        error = "Opción desconocida: " + argument;
                        return false;
                }
            }

            return true;
        }

        private static bool TryParseBehavior(string value, out BotBehavior behavior)
        {
            if (string.Equals(value, "random", StringComparison.OrdinalIgnoreCase))
            {
                behavior = BotBehavior.Random;
                return true;
            }

            if (string.Equals(value, "crowd", StringComparison.OrdinalIgnoreCase))
            {
                behavior = BotBehavior.Crowd;
                return true;
            }

            behavior = BotBehavior.Random;
            return false;
        }

        private sealed class Options
        {
            public string Host { get; set; } = "127.0.0.1";

            public int Port { get; set; } = 7777;

            public int Bots { get; set; } = 1;

            public double Seconds { get; set; } = 10d;

            public int RampMs { get; set; } = 200;

            public int Retries { get; set; } = 5;

            public int WelcomeTimeoutMs { get; set; } = 15000;

            public BotBehavior Behavior { get; set; } = BotBehavior.Random;

            public int Seed { get; set; } = 20261019;

            public string MapPath { get; set; } = "maps/avelora-etapa-a.avmap";

            public string AccountPrefix { get; set; } = "bot";

            public string Password { get; set; } = "avelora-dev";

            public string InvitationCode { get; set; } = "avelora-dev";
        }
    }
}
