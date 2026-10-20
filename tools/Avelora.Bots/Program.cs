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
    /// dotnet run --project tools/Avelora.Bots -- --host 127.0.0.1 --port 7777 --bots 10
    /// </code>
    /// </remarks>
    internal static class Program
    {
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

            Console.WriteLine("Avelora · bots (Etapa F: cuentas y persistencia)");
            Console.WriteLine("  Servidor : " + options.Host + ":" + options.Port);
            Console.WriteLine("  Bots     : " + options.Bots + " durante " + Invariant(options.Seconds) + " s");
            Console.WriteLine("  Cuentas  : " + options.AccountPrefix + "1.." + options.AccountPrefix + options.Bots +
                " (invitación '" + options.InvitationCode + "')");
            Console.WriteLine("  Mapa     : " + mapPath + "  huella " + ShortHash(MapFile.ToHexString(mapHash)));
            Console.WriteLine();

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(options.Seconds + 10d));
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
                    options.InvitationCode);
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

            Console.WriteLine();
            Console.WriteLine("Resumen");

            int ok = 0;
            long snapshots = 0;

            for (int i = 0; i < bots.Count; i++)
            {
                BotClient bot = bots[i];
                snapshots += bot.Snapshots;

                if (bot.EntityId > 0)
                {
                    ok++;
                }

                Console.WriteLine(
                    "  bot " + bot.Index + ": " + bot.Status.PadRight(12) +
                    " cuenta=" + bot.AccountName +
                    " ent=" + (bot.EntityId > 0 ? bot.EntityId.ToString(CultureInfo.InvariantCulture) : "-") +
                    "  snapshots=" + bot.Snapshots +
                    "  pos=(" + Invariant(bot.X) + "; " + Invariant(bot.Y) + ")" +
                    "  inputs=" + bot.Sequence);
            }

            Console.WriteLine(
                "  " + ok + "/" + bots.Count + " conectados, " + snapshots + " snapshots recibidos en total");
            return ok == bots.Count ? 0 : 3;
        }

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
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bots) || bots < 1 || bots > 64)
                        {
                            error = "Número de bots inválido: " + value;
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

        private sealed class Options
        {
            public string Host { get; set; } = "127.0.0.1";

            public int Port { get; set; } = 7777;

            public int Bots { get; set; } = 1;

            public double Seconds { get; set; } = 10d;

            public int Seed { get; set; } = 20261019;

            public string MapPath { get; set; } = "maps/avelora-etapa-a.avmap";

            public string AccountPrefix { get; set; } = "bot";

            public string Password { get; set; } = "avelora-dev";

            public string InvitationCode { get; set; } = "avelora-dev";
        }
    }
}
