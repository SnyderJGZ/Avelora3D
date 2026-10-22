#nullable enable
using System;
using System.Globalization;
using System.Text;
using Avelora.Server.Configuration;
using Avelora.Server.Entities;
using Avelora.Server.Metrics;
using Avelora.Server.Net;
using Avelora.Server.Simulation;
using Avelora.Shared;
using Avelora.Shared.Protocol;

namespace Avelora.Server.Diagnostics
{
    /// <summary>
    /// Informes por consola: arranque, marca periódica y resumen final (guía del núcleo §11.1).
    /// </summary>
    /// <remarks>
    /// Todo lo que sale por aquí es para una persona. Por eso usa números con separador decimal
    /// fijo y no depende de la configuración regional: los informes deben poder compararse entre
    /// máquinas y entre ejecuciones.
    ///
    /// Los jugadores se leen de <see cref="Zone.Players"/> (una copia publicada por el tick), no del
    /// almacén de entidades: este hilo no puede recorrer lo que la simulación está modificando.
    /// </remarks>
    public static class ConsoleReporter
    {
        /// <summary>Escribe la cabecera de arranque con la configuración efectiva.</summary>
        public static void WriteStartup(
            string configPath,
            string mapPath,
            MapCollisionGrid map,
            string mapHash,
            ServerConfig config,
            int port,
            string? databasePath,
            string serverKeyFingerprint)
        {
            Console.WriteLine("Avelora · servidor autoritativo (Etapa F: persistencia)");
            Console.WriteLine("  Configuración : " + configPath);
            Console.WriteLine("  Mapa          : " + mapPath);
            Console.WriteLine(
                "  Rejilla       : " + map.Width + "x" + map.Height +
                " (origen " + map.OriginX + "," + map.OriginY + ")  huella " + ShortHash(mapHash));
            Console.WriteLine(
                "  Persistencia  : " + (databasePath == null
                    ? "desactivada"
                    : "SQLite " + databasePath + " (guardado cada " +
                      Invariant(config.SaveIntervalSeconds, "0.#") + " s)"));
            Console.WriteLine(
                "  Tick          : " + config.TickRate + " Hz (paso fijo " +
                Invariant(SimulationConstants.FixedDt, "0.####") + " s = " +
                Invariant(SimulationConstants.StepMs, "0.###") + " ms)");
            Console.WriteLine(
                "  Clave         : RSA-2048, huella " + serverKeyFingerprint);
            Console.WriteLine(
                "  Red           : UDP 0.0.0.0:" + port + ", hasta " + config.MaxPlayers +
                " jugadores (" + config.MaxConnections + " conexiones, " +
                config.MaxConnectionsPerIp + " por IP)");
            Console.WriteLine(
                "  Aparición     : (" + Invariant(config.SpawnX, "0.##") + "; " + Invariant(config.SpawnY, "0.##") +
                ")  medio=(" + Invariant(config.PlayerHalfWidth, "0.##") + "; " +
                Invariant(config.PlayerHalfHeight, "0.##") + ")  velocidad=" +
                Invariant(config.PlayerSpeed, "0.##") + "/s");
            Console.WriteLine(
                "  Protocolo     : v" + ProtocolConstants.Version + ", snapshot cada " + config.SnapshotEveryTicks +
                " tick(s), " + config.MaxMessagesPerSecondPerConnection + " msgs/s por conexión, saludo en " +
                Invariant(config.HandshakeTimeoutSeconds, "0.#") + " s, " +
                "inundación sostenida " + config.MaxSecondsOverMessageLimit + " s por minuto");
            Console.WriteLine(
                "  Interés       : radio " + Invariant(config.InterestRadius, "0.##") +
                " tiles, celda " + Invariant(config.InterestCellSize, "0.##") +
                ", histéresis " + Invariant(config.InterestHysteresis, "0.##") + " tiles");
            Console.WriteLine(
                "  Límites       : " + config.MaxIntentsPerTick + " inputs/tick, " +
                config.MaxIntentsPerPlayerPerTick + "/jugador/tick, cola de jugador " +
                config.InputBufferCapacity + ", cola de entrada " + config.MaxQueuedIntents +
                ", cola de salida " + config.MaxQueuedOutboundMessages);
            Console.WriteLine(
                "  Informes      : ventana de " + config.MetricsWindowTicks + " ticks cada " +
                Invariant(config.ReportIntervalSeconds, "0.#") + " s");
            Console.WriteLine();
        }

        /// <summary>Escribe una marca de seguimiento.</summary>
        public static void WriteReport(double elapsedSeconds, Zone zone, TickMetrics metrics, NetworkStats network)
        {
            TickMetricsSnapshot snapshot = metrics.Snapshot();

            Console.WriteLine(
                "[t=" + Invariant(elapsedSeconds, "0.0").PadLeft(6) + "s] " +
                "tick=" + zone.TickCount +
                "  jug=" + zone.Players.Count +
                "  tickMs media=" + Invariant(snapshot.MeanTickMs, "0.000") +
                " p99=" + Invariant(snapshot.P99TickMs, "0.000") +
                " max=" + Invariant(snapshot.MaxTickMs, "0.000") +
                "  sobre_" + Invariant(snapshot.BudgetMs, "0") + "ms=" + snapshot.OverBudgetTicks +
                "  descartado=" + Invariant(snapshot.DiscardedMs, "0.0") + "ms");

            Console.WriteLine(
                "            red: conn=" + network.Connections +
                " msgs=" + network.MessagesReceived +
                " rech=" + network.MessagesRejected +
                " saludo=" + network.HandshakesRejected +
                " limite=" + network.RateLimited +
                " cortes=" + network.RateLimitedDisconnects +
                " cola=" + network.DroppedOutbound +
                " snaps=" + network.SnapshotsSent +
                " ents=" + network.EntitiesSent +
                " enviados=" + network.SnapshotBytes +
                " recibidos=" + network.BytesReceived +
                "  inputs ok=" + zone.Inputs.Received +
                " redundantes=" + zone.RedundantDuplicates +
                " fuera_de_orden=" + zone.RejectedOutOfOrder +
                " buffer_lleno=" + zone.RejectedBufferFull +
                " tope_jugador=" + zone.RejectedPerPlayerLimit +
                " ajena=" + zone.RejectedUnknownEntity +
                " direccion=" + zone.Inputs.RejectedInvalidDirection);

            Console.WriteLine(
                "            cuenta: login_ok=" + network.LoginsSucceeded +
                " login_rech=" + network.LoginsRejected +
                " guardados=" + network.CharactersSaved +
                " fallos=" + network.SaveFailures +
                " pendientes=" + network.PendingSaves);

            if (network.SaveFailures > 0 && !string.IsNullOrEmpty(network.SaveLastError))
            {
                Console.WriteLine("            error de guardado: " + network.SaveLastError);
            }

            Console.WriteLine(
                "            memoria: GC " + GC.CollectionCount(0) + "/" + GC.CollectionCount(1) + "/" +
                GC.CollectionCount(2) + "  mem=" + Invariant(GC.GetTotalMemory(false) / 1048576.0, "0.0") + "MB");

            // Ver a los jugadores moverse es la forma más rápida de saber que el tick hace algo.
            Console.WriteLine("            jugadores: " + DescribePlayers(zone));
        }

        /// <summary>Posición y último input aplicado de los jugadores de la zona.</summary>
        private static string DescribePlayers(Zone zone)
        {
            const int MaxShown = 4;

            var text = new StringBuilder();
            int shown = 0;
            int total = zone.Players.Count;

            for (int i = 0; i < total; i++)
            {
                if (shown >= MaxShown)
                {
                    break;
                }

                PlayerInfo player = zone.Players[i];

                if (shown > 0)
                {
                    text.Append("  ");
                }

                text.Append("ent").Append(player.Id)
                    .Append(" pos=(").Append(Invariant(player.X, "0.000")).Append("; ")
                    .Append(Invariant(player.Y, "0.000")).Append(')')
                    .Append(" dir=(").Append(player.DirectionX).Append(',').Append(player.DirectionY).Append(')')
                    .Append(" seq=").Append(player.LastProcessedSequence);
                shown++;
            }

            if (total > shown)
            {
                text.Append("  (+").Append(total - shown).Append(" más)");
            }

            return total == 0 ? "(ninguno)" : text.ToString();
        }

        /// <summary>Escribe el resumen final de la ejecución.</summary>
        public static void WriteSummary(
            double elapsedSeconds,
            Zone zone,
            TickMetrics metrics,
            NetworkStats network,
            int exitCode)
        {
            TickMetricsSnapshot snapshot = metrics.Snapshot();
            double expected = elapsedSeconds * SimulationConstants.TickRate;

            Console.WriteLine();
            Console.WriteLine("Resumen");
            Console.WriteLine("  Duración        : " + Invariant(elapsedSeconds, "0.0") + " s");
            Console.WriteLine(
                "  Ticks           : " + snapshot.TotalTicks + " (esperados ~" + Invariant(expected, "0") +
                ", desviación " + Invariant(Percent(snapshot.TotalTicks, expected), "0.00") + " %)");
            Console.WriteLine(
                "  Duración de tick: media " + Invariant(snapshot.MeanTickMs, "0.000") + " ms, p99 " +
                Invariant(snapshot.P99TickMs, "0.000") + " ms, peor " + Invariant(snapshot.MaxTickMs, "0.000") + " ms");
            Console.WriteLine(
                "  Presupuesto     : " + snapshot.OverBudgetTicks + " ticks por encima de " +
                Invariant(snapshot.BudgetMs, "0") + " ms, " + Invariant(snapshot.DiscardedMs, "0.0") +
                " ms descartados por el tope anti-espiral");
            Console.WriteLine(
                "  Red             : " + network.PlayersJoined + " altas, " + network.PlayersLeft + " bajas, " +
                network.MessagesReceived + " mensajes, " + network.MessagesRejected + " rechazados, " +
                network.HandshakesRejected + " saludos rechazados, " + network.RateLimited + " limitados (" +
                network.RateLimitedDisconnects + " cortes por abuso), " +
                network.SnapshotsSent + " snapshots, " + network.EntitiesSent + " entidades, " +
                network.SnapshotBytes + " bytes enviados, " + network.BytesReceived +
                " bytes recibidos, " + network.DroppedOutbound + " descartados en cola");
            Console.WriteLine(
                "  Inputs          : " + zone.Inputs.Received + " ok, " +
                zone.RedundantDuplicates + " redundantes, " + zone.RejectedOutOfOrder +
                " fuera de orden, " + zone.RejectedPerPlayerLimit + " por tope de jugador, " +
                zone.RejectedBufferFull + " por búfer lleno");
            Console.WriteLine(
                "  Persistencia    : " + network.LoginsSucceeded + " login(s) correctos, " +
                network.LoginsRejected + " rechazados, " + network.CharactersSaved +
                " personaje(s) guardados, " + network.SaveFailures + " fallos, " +
                network.PendingSaves + " pendiente(s)" +
                (network.SaveFailures > 0 && !string.IsNullOrEmpty(network.SaveLastError)
                    ? ", último error: " + network.SaveLastError
                    : string.Empty));
            Console.WriteLine("  Jugadores       : " + zone.Players.Count);
            Console.WriteLine("  Código de salida: " + exitCode);
        }

        /// <summary>Escribe un error en color.</summary>
        public static void WriteError(string message)
        {
            ConsoleColor previous = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine("ERROR: " + message);
            Console.ForegroundColor = previous;
        }

        /// <summary>Primeros 16 caracteres de la huella, que bastan para comparar visualmente.</summary>
        public static string ShortHash(string hash) =>
            hash.Length <= 16 ? hash : hash.Substring(0, 16) + "…";

        private static string Invariant(double value, string format) =>
            value.ToString(format, CultureInfo.InvariantCulture);

        private static double Percent(double actual, double expected) =>
            expected <= 0d ? 0d : ((actual - expected) / expected) * 100d;
    }
}
