#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Avelora.Shared;
using Avelora.Shared.Maps;
using Avelora.Shared.Protocol;

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
    /// dotnet run --project tools/Avelora.Bots -- --bots 20 --latency-ms 80 --jitter-ms 20 --loss 0.02
    /// dotnet run --project tools/Avelora.Bots -- --bots 20 --latency-ms 80 --jitter-ms 20 --loss 0.02 --input-redundancy 1
    /// dotnet run --project tools/Avelora.Bots -- --bots 20 --log-corrections
    /// dotnet run --project tools/Avelora.Bots -- --bots 5 --behavior flood --account-prefix abuso
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

            // Modo de análisis de caídas: no lanza bots ni toca la red, solo compara los archivos
            // que dejó tools/carga/caida.ps1 (G-05). Es la misma lógica que prueban los xUnit.
            if (options.CompareTrace)
            {
                return RunCompareTraces(options);
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

            // Mala red: si hay pérdida, retardo o jitter, los bots hablan con un proxy local que
            // reenvía al servidor real. Sin esas opciones, todo funciona como hasta ahora.
            LossyUdpProxy? proxy = null;
            string botHost = options.Host;
            int botPort = options.Port;
            bool lossy = options.LatencyMs > 0 || options.JitterMs > 0 || options.Loss > 0d;

            if (lossy)
            {
                try
                {
                    proxy = new LossyUdpProxy(
                        options.Host,
                        options.Port,
                        options.Loss,
                        options.LatencyMs,
                        options.JitterMs,
                        options.Seed);

                    proxy.Start();
                    botHost = "127.0.0.1";
                    botPort = proxy.Port;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("ERROR: no se pudo arrancar el proxy de mala red: " + ex.Message);
                    proxy?.Dispose();
                    return 4;
                }
            }

            using (proxy)
            {
                Console.WriteLine("Avelora · bots (Etapa G: prueba de carga)");
                Console.WriteLine("  Servidor : " + options.Host + ":" + options.Port);
                Console.WriteLine("  Bots     : " + options.Bots + " durante " + Invariant(options.Seconds) + " s" +
                    " (rampa " + options.RampMs + " ms, " + BehaviorName(options.Behavior) + ")");

                if (proxy != null)
                {
                    Console.WriteLine("  Mala red : retardo " + options.LatencyMs + " ms ± " + options.JitterMs +
                        " ms, pérdida " + Fraction(options.Loss) +
                        " (proxy 127.0.0.1:" + proxy.Port + " → " + options.Host + ":" + options.Port + ")");
                }

                Console.WriteLine("  Reintento: hasta " + options.Retries + " vez/veces si el login se limita; " +
                    "saludo esperado " + options.WelcomeTimeoutMs + " ms por intento");
                Console.WriteLine("  Redundancia: " + options.InputRedundancy + " input(s) por mensaje" +
                    (options.InputRedundancy == 1 ? " (sin redundancia, como antes)" : string.Empty));

                Console.WriteLine("  Clave    : " +
                    (string.IsNullOrEmpty(options.ServerKeyFingerprint)
                        ? "sin huella esperada (modo desarrollo, se acepta la del servidor)"
                        : options.ServerKeyFingerprint));

                if (options.LogCorrections)
                {
                    Console.WriteLine("  Correcciones: se registra cada una (--log-corrections)");
                }

                if (!string.IsNullOrEmpty(options.TracePositionsPath))
                {
                    Console.WriteLine("  Traza       : " + options.TracePositionsPath + " (1 muestra/s por bot)");
                }

                if (!string.IsNullOrEmpty(options.ReportWelcomePath))
                {
                    Console.WriteLine("  Welcome     : " + options.ReportWelcomePath + " (posición guardada por cuenta)");
                }
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
                        botHost,
                        botPort,
                        mapHash,
                        options.Seed,
                        runMs,
                        options.AccountPrefix,
                        options.Password,
                        options.InvitationCode,
                        startDelayMs: (i - 1) * options.RampMs,
                        retries: options.Retries,
                        welcomeTimeoutMs: options.WelcomeTimeoutMs,
                        behavior: options.Behavior,
                        attackCooldownMs: options.AttackCooldownMs,
                        map: map,
                        inputRedundancy: options.InputRedundancy,
                        logCorrections: options.LogCorrections,
                        tracePositionsPath: options.TracePositionsPath,
                        reportWelcomePath: options.ReportWelcomePath,
                        serverKeyFingerprint: options.ServerKeyFingerprint);
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

                if (proxy != null)
                {
                    Console.WriteLine();
                    Console.WriteLine("  Proxy    : " + proxy.ClientDatagrams + " datagramas de bots, " +
                        proxy.ServerDatagrams + " del servidor, " + proxy.Dropped + " perdidos a propósito " +
                        "(" + proxy.ForwardedToServer + " al servidor, " + proxy.ForwardedToClient + " a los bots)");
                }

                return WriteSummary(bots, options);
            }
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
            long corrections = 0;
            long snaps = 0;
            long playedMs = 0;
            long sentAttacks = 0;
            long sentRangedAttacks = 0;
            long attacksSkippedLineOfSight = 0;
            long combatEventsReceived = 0;
            long deathsSeen = 0;
            long elitesSeen = 0;
            long ownDeaths = 0;
            long ownRespawns = 0;
            long sentPickups = 0;
            long sentPotions = 0;
            long equipped = 0;
            long armorsEquipped = 0;
            long equipOmittedRequirements = 0;
            long pointsAllocated = 0;
            long statsReceived = 0;
            long charactersCreated = 0;
            long characterRejects = 0;
            long levelUps = 0;
            byte maxLevelReached = 1;
            var welcomeTimes = new List<long>(bots.Count);
            var rejects = new SortedDictionary<string, int>(StringComparer.Ordinal);

            long sentValid = 0;
            long sentInvalid = 0;
            long sentReplay = 0;
            long sentGarbage = 0;
            long sentHellos = 0;
            long sentChat = 0;
            long receivedChat = 0;
            var responses = new SortedDictionary<string, int>(StringComparer.Ordinal);

            bool abusive = BotClient.IsAbusive(options.Behavior);

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

                corrections += bot.Corrections;
                snaps += bot.Snaps;
                sentAttacks += bot.SentAttacks;
                sentRangedAttacks += bot.SentRangedAttacks;
                attacksSkippedLineOfSight += bot.AttacksSkippedLineOfSight;
                combatEventsReceived += bot.CombatEventsReceived;
                deathsSeen += bot.DeathsSeen;
                elitesSeen += bot.ElitesSeen;
                ownDeaths += bot.OwnDeaths;
                ownRespawns += bot.OwnRespawns;
                sentPickups += bot.SentPickups;
                sentPotions += bot.SentPotions;
                equipped += bot.Equipped;
                armorsEquipped += bot.ArmorsEquipped;
                equipOmittedRequirements += bot.EquipOmittedRequirements;
                pointsAllocated += bot.PointsAllocated;
                statsReceived += bot.StatsReceived;
                charactersCreated += bot.CharactersCreated;
                characterRejects += bot.CharacterRejects;
                levelUps += bot.LevelUps;
                sentChat += bot.SentChat;
                receivedChat += bot.ReceivedChat;
                if (bot.MaxLevelReached > maxLevelReached)
                {
                    maxLevelReached = bot.MaxLevelReached;
                }

                if (bot.EntityId > 0)
                {
                    ok++;
                    welcomeTimes.Add(bot.WelcomeMs);
                    snapshotsPerSecond += bot.SnapshotsPerSecond;
                    playedMs += bot.PlayedMs;
                }
                else
                {
                    string reason = bot.RejectReasonText ?? bot.Status;
                    rejects.TryGetValue(reason, out int count);
                    rejects[reason] = count + 1;
                }

                if (abusive)
                {
                    sentValid += bot.SentValidInputs;
                    sentInvalid += bot.SentInvalidInputs;
                    sentReplay += bot.SentReplayInputs;
                    sentGarbage += bot.SentGarbage;
                    sentHellos += bot.SentHellos;
                    bot.AccumulateRejectCounts(responses);
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
                    " corr=" + bot.Corrections +
                    " saltos=" + bot.Snaps +
                    " muertes_propias=" + bot.OwnDeaths +
                    " reapariciones=" + bot.OwnRespawns +
                     " nivel=" + bot.Level +
                     " subidas_nivel=" + bot.LevelUps +
                     " nivel_max=" + bot.MaxLevelReached +
                     " puntos_repartidos=" + bot.PointsAllocated +
                     " stats_recibidos=" + bot.StatsReceived +
                     " personajes_creados=" + bot.CharactersCreated +
                     " rechazos_personaje=" + bot.CharacterRejects +
                     " snapshots=" + bot.Snapshots);

                if (options.Behavior == BotBehavior.Fight)
                {
                    Console.WriteLine(
                        "      combate: ataques=" + bot.SentAttacks +
                        " ataques_distancia_enviados=" + bot.SentRangedAttacks +
                        " ataques_omitidos_vision=" + bot.AttacksSkippedLineOfSight +
                        " combate_recibidos=" + bot.CombatEventsReceived +
                        " muertes_vistas=" + bot.DeathsSeen +
                        " elites_vistos=" + bot.ElitesSeen +
                        " recogidas=" + bot.SentPickups +
                        " pociones=" + bot.SentPotions +
                        " equipados=" + bot.Equipped +
                        " armaduras_equipadas=" + bot.ArmorsEquipped +
                        " equip_omitidos_req=" + bot.EquipOmittedRequirements +
                        " puntos_repartidos=" + bot.PointsAllocated +
                        " stats_recibidos=" + bot.StatsReceived);
                }

                if (options.Behavior == BotBehavior.Chat)
                {
                    Console.WriteLine(
                        "      chat: chat_enviados=" + bot.SentChat +
                        " chat_recibidos=" + bot.ReceivedChat);
                }

                if (abusive)
                {
                    Console.WriteLine(
                        "      abuso: validos=" + bot.SentValidInputs +
                        " invalidos=" + bot.SentInvalidInputs +
                        " replay=" + bot.SentReplayInputs +
                        " basura=" + bot.SentGarbage +
                        " hellos=" + bot.SentHellos +
                        " respuestas=[" + bot.DescribeRejectResponses() + "]" +
                        " distancia=" + Invariant(bot.TravelledDistance) + " tiles");
                }
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
            Console.WriteLine("  Personajes : personajes_creados=" + charactersCreated +
                ", rechazos_personaje=" + characterRejects);
            Console.WriteLine(
                "  Welcome    : p50 " + (p50 >= 0 ? p50 + " ms" : "-") +
                ", máximo " + (maxWelcome >= 0 ? maxWelcome + " ms" : "-") +
                " (" + welcomeTimes.Count + " bot(s))");
            Console.WriteLine("  Snapshots  : " + Invariant(meanPerBot) + "/s por bot de media, " +
                snapshots + " en total");
            Console.WriteLine("  Peor hueco : " + worstGap + " ms entre snapshots");

            double minutes = playedMs / 60000d;
            Console.WriteLine(
                "  Predicción : " + corrections + " correcciones, " + snaps + " saltos" +
                (minutes > 0d
                    ? " (" + Invariant(corrections / minutes) + "/bot/min, " + Invariant(snaps / minutes) + "/bot/min)"
                    : " (sin tiempo jugado)"));

            Console.WriteLine(
                "  Jugador    : " + ownDeaths + " muertes propias, " +
                ownRespawns + " reapariciones");

            if (options.Behavior == BotBehavior.Fight)
            {
                Console.WriteLine(
                    "  Combate    : " + sentAttacks + " ataques enviados, " +
                    "ataques_distancia_enviados=" + sentRangedAttacks + ", " +
                    "ataques_omitidos_vision=" + attacksSkippedLineOfSight + ", " +
                    "combate_recibidos=" + combatEventsReceived + ", " +
                    deathsSeen + " muertes vistas, elites_vistos=" + elitesSeen + ", " +
                    sentPickups + " recogidas, " +
                    sentPotions + " pociones usadas, " + equipped + " armas equipadas, " +
                    armorsEquipped + " armaduras_equipadas, " +
                    "equip_omitidos_req=" + equipOmittedRequirements + ", " +
                    "puntos_repartidos=" + pointsAllocated + ", stats_recibidos=" + statsReceived + ", " +
                    "subidas_nivel=" + levelUps + ", nivel_max=" + maxLevelReached);
            }

            if (options.Behavior == BotBehavior.Chat)
            {
                Console.WriteLine(
                    "  Chat       : chat_enviados=" + sentChat +
                    " chat_recibidos=" + receivedChat);
            }

            if (abusive)
            {
                Console.WriteLine(
                    "  Enviado    : " + sentValid + " inputs válidos, " + sentInvalid +
                    " con dirección fuera de rango, " + sentReplay + " secuencias viejas, " +
                    sentGarbage + " datagramas basura, " + sentHellos + " saludos");
                Console.WriteLine("  Respuestas : " + DescribeCounts(responses));

                if (options.Behavior == BotBehavior.Speed)
                {
                    Console.WriteLine("  Velocidad  : " + DescribeSpeedCheck(bots));
                }

                if (options.Behavior == BotBehavior.Replay)
                {
                    int checkedBots = 0;
                    int replayedRejected = 0;

                    for (int i = 0; i < bots.Count; i++)
                    {
                        bool? rejected = bots[i].ReplayedHelloRejected;

                        if (rejected.HasValue)
                        {
                            checkedBots++;

                            if (rejected.Value)
                            {
                                replayedRejected++;
                            }
                        }
                    }

                    Console.WriteLine(
                        "  Replay     : saludo reenviado rechazado en " + replayedRejected + "/" +
                        checkedBots + " bot(s)");
                }
            }

            return ok == bots.Count ? 0 : 3;
        }

        /// <summary>Compara la distancia recorrida con el máximo que permite la velocidad del servidor.</summary>
        private static string DescribeSpeedCheck(List<BotClient> bots)
        {
            const float Margin = 1f;
            int exceeded = 0;
            int measured = 0;
            float worst = 0f;

            for (int i = 0; i < bots.Count; i++)
            {
                BotClient bot = bots[i];
                if (bot.Speed <= 0f || bot.PlayedMs <= 0)
                {
                    continue;
                }

                measured++;
                float maximum = (bot.Speed * bot.PlayedMs / 1000f) + Margin;

                if (bot.TravelledDistance > maximum)
                {
                    exceeded++;

                    if (bot.TravelledDistance - maximum > worst)
                    {
                        worst = bot.TravelledDistance - maximum;
                    }
                }
            }

            return measured == 0
                ? "sin datos (ningún bot llegó a jugar)"
                : exceeded == 0
                    ? "todos dentro del máximo (velocidad × tiempo + 1 tile) en " + measured + " bot(s)"
                    : exceeded + " de " + measured + " bot(s) por encima del máximo (peor exceso " +
                      Invariant(worst) + " tiles)";
        }

        private static string DescribeCounts(SortedDictionary<string, int> counts)
        {
            if (counts.Count == 0)
            {
                return "ninguna";
            }

            var text = new System.Text.StringBuilder();

            foreach (KeyValuePair<string, int> pair in counts)
            {
                if (text.Length > 0)
                {
                    text.Append(", ");
                }

                text.Append(pair.Key).Append('=').Append(pair.Value);
            }

            return text.ToString();
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
            behavior switch
            {
                BotBehavior.Crowd => "todos en el mismo punto",
                BotBehavior.Flood => "flood (200 inputs/s)",
                BotBehavior.Garbage => "garbage (basura a 5/s)",
                BotBehavior.Speed => "speed (direcciones fuera de rango)",
                BotBehavior.Replay => "replay (secuencias viejas)",
                BotBehavior.BadLogin => "badlogin (contraseña incorrecta)",
                BotBehavior.Chat => "chat (mensajes de zona)",
                BotBehavior.Inventory => "inventory (pelea como fight y manda acciones de inventario al azar)",
                _ => "direcciones al azar",
            };

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

        /// <summary>
        /// Compara la traza de posiciones con los <c>Welcome</c> tras la caída (G-05). Imprime la
        /// tabla por cuenta y devuelve 0 si todas cumplen el tope y 1 si alguna falla.
        /// </summary>
        private static int RunCompareTraces(Options options)
        {
            if (string.IsNullOrWhiteSpace(options.TracePath))
            {
                Console.Error.WriteLine("ERROR: --compare-trace necesita --trace <archivo>.");
                return 1;
            }

            if (string.IsNullOrWhiteSpace(options.WelcomePath))
            {
                Console.Error.WriteLine("ERROR: --compare-trace necesita --welcome <archivo>.");
                return 1;
            }

            if (!options.DeathUtcMs.HasValue)
            {
                Console.Error.WriteLine("ERROR: --compare-trace necesita --death-ms <instante UTC en ms>.");
                return 1;
            }

            if (!options.MaxLossSeconds.HasValue || options.MaxLossSeconds.Value <= 0d)
            {
                Console.Error.WriteLine("ERROR: --compare-trace necesita --max-loss <segundos>.");
                return 1;
            }

            if (!File.Exists(options.TracePath))
            {
                Console.Error.WriteLine("ERROR: no existe la traza '" + options.TracePath + "'.");
                return 1;
            }

            if (!File.Exists(options.WelcomePath))
            {
                Console.Error.WriteLine("ERROR: no existe el archivo de Welcome '" + options.WelcomePath + "'.");
                return 1;
            }

            List<TraceSample> trace = TraceComparer.ReadTrace(options.TracePath);
            List<WelcomeSpawn> welcomes = TraceComparer.ReadWelcomes(options.WelcomePath);
            TraceComparison comparison = TraceComparer.Compare(
                trace,
                welcomes,
                options.DeathUtcMs.Value,
                options.MaxLossSeconds.Value);

            Console.WriteLine("Avelora · comparación de trazas (caída del servidor)");
            Console.WriteLine("  Traza   : " + options.TracePath + " (" + trace.Count + " muestras)");
            Console.WriteLine("  Welcome : " + options.WelcomePath + " (" + welcomes.Count + " cuenta(s))");
            Console.WriteLine(
                "  Muerte  : " + options.DeathUtcMs.Value + " ms UTC; margen " +
                TraceComparer.MatchTolerance.ToString("0.00000", CultureInfo.InvariantCulture) + " tiles");
            Console.WriteLine();

            TraceComparer.WriteTable(Console.Out, comparison);

            return comparison.AllOk ? 0 : 1;
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

        private static string Fraction(double value) =>
            value.ToString("0.###", CultureInfo.InvariantCulture);

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

                if (string.Equals(argument, "--log-corrections", StringComparison.OrdinalIgnoreCase))
                {
                    // Opción sin valor: no se consume el argumento siguiente.
                    options.LogCorrections = true;
                    continue;
                }

                if (string.Equals(argument, "--compare-trace", StringComparison.OrdinalIgnoreCase))
                {
                    options.CompareTrace = true;
                    continue;
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

                    case "--latency-ms":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int latencyMs) || latencyMs < 0)
                        {
                            error = "Retardo inválido: " + value + " (>= 0)";
                            return false;
                        }

                        options.LatencyMs = latencyMs;
                        break;

                    case "--jitter-ms":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int jitterMs) || jitterMs < 0)
                        {
                            error = "Jitter inválido: " + value + " (>= 0)";
                            return false;
                        }

                        options.JitterMs = jitterMs;
                        break;

                    case "--loss":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double loss) ||
                            loss < 0d || loss >= 1d)
                        {
                            error = "Pérdida inválida: " + value + " (0 <= P < 1)";
                            return false;
                        }

                        options.Loss = loss;
                        break;

                    case "--input-redundancy":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int inputRedundancy) ||
                            inputRedundancy < 1 ||
                            inputRedundancy > ProtocolConstants.MaxInputsPerMessage)
                        {
                            error = "Redundancia inválida: " + value + " (1.." + ProtocolConstants.MaxInputsPerMessage + ")";
                            return false;
                        }

                        options.InputRedundancy = inputRedundancy;
                        break;

                    case "--behavior":
                        if (!TryParseBehavior(value, out BotBehavior behavior))
                        {
                            error = "Comportamiento desconocido: " + value +
                                " (random|crowd|fight|flood|garbage|speed|replay|badlogin|chat|inventory)";
                            return false;
                        }

                        options.Behavior = behavior;
                        break;

                    case "--attack-cooldown-ms":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int attackCooldownMs) ||
                            attackCooldownMs < 0)
                        {
                            error = "Recarga de ataque inválida: " + value + " (>= 0 ms)";
                            return false;
                        }

                        options.AttackCooldownMs = attackCooldownMs;
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

                    case "--trace-positions":
                        options.TracePositionsPath = value;
                        break;

                    case "--report-welcome":
                        options.ReportWelcomePath = value;
                        break;

                    case "--trace":
                        options.TracePath = value;
                        break;

                    case "--welcome":
                        options.WelcomePath = value;
                        break;

                    case "--death-ms":
                        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long deathUtcMs) || deathUtcMs <= 0)
                        {
                            error = "Instante de muerte inválido: " + value;
                            return false;
                        }

                        options.DeathUtcMs = deathUtcMs;
                        break;

                    case "--max-loss":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double maxLoss) || maxLoss <= 0d)
                        {
                            error = "Pérdida máxima inválida: " + value;
                            return false;
                        }

                        options.MaxLossSeconds = maxLoss;
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

                    case "--server-key":
                        if (!IsHex(value) || value.Length == 0)
                        {
                            error = "La huella del servidor debe ser hexadecimal (64 caracteres).";
                            return false;
                        }

                        options.ServerKeyFingerprint = value;
                        break;

                    default:
                        error = "Opción desconocida: " + argument;
                        return false;
                }
            }

            return true;
        }

        private static bool IsHex(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];

                if (!((c >= '0' && c <= '9') ||
                      (c >= 'a' && c <= 'f') ||
                      (c >= 'A' && c <= 'F')))
                {
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

            if (string.Equals(value, "fight", StringComparison.OrdinalIgnoreCase))
            {
                behavior = BotBehavior.Fight;
                return true;
            }

            if (string.Equals(value, "flood", StringComparison.OrdinalIgnoreCase))
            {
                behavior = BotBehavior.Flood;
                return true;
            }

            if (string.Equals(value, "garbage", StringComparison.OrdinalIgnoreCase))
            {
                behavior = BotBehavior.Garbage;
                return true;
            }

            if (string.Equals(value, "speed", StringComparison.OrdinalIgnoreCase))
            {
                behavior = BotBehavior.Speed;
                return true;
            }

            if (string.Equals(value, "replay", StringComparison.OrdinalIgnoreCase))
            {
                behavior = BotBehavior.Replay;
                return true;
            }

            if (string.Equals(value, "badlogin", StringComparison.OrdinalIgnoreCase))
            {
                behavior = BotBehavior.BadLogin;
                return true;
            }

            if (string.Equals(value, "chat", StringComparison.OrdinalIgnoreCase))
            {
                behavior = BotBehavior.Chat;
                return true;
            }

            if (string.Equals(value, "inventory", StringComparison.OrdinalIgnoreCase))
            {
                behavior = BotBehavior.Inventory;
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

            /// <summary>Recarga entre ataques del modo <c>fight</c>; el servidor usa 800 ms por defecto.</summary>
            public int AttackCooldownMs { get; set; } = 800;

            public int Seed { get; set; } = 20261019;

            public string MapPath { get; set; } = "maps/avelora-valle.avmap";

            public string AccountPrefix { get; set; } = "bot";

            public string Password { get; set; } = "avelora-dev";

            public string InvitationCode { get; set; } = "avelora-dev";

            public string ServerKeyFingerprint { get; set; } = string.Empty;

            public int LatencyMs { get; set; }

            public int JitterMs { get; set; }

            public double Loss { get; set; }

            public int InputRedundancy { get; set; } = ProtocolConstants.MaxInputsPerMessage;

            public bool LogCorrections { get; set; }

            public string? TracePositionsPath { get; set; }

            public string? ReportWelcomePath { get; set; }

            public bool CompareTrace { get; set; }

            public string? TracePath { get; set; }

            public string? WelcomePath { get; set; }

            public long? DeathUtcMs { get; set; }

            public double? MaxLossSeconds { get; set; }
        }
    }
}
