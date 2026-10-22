#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Avelora.Bots
{
    /// <summary>Una muestra de la traza de un bot: instante UTC, cuenta y posición del snapshot.</summary>
    public readonly struct TraceSample
    {
        public TraceSample(long utcMs, string account, float x, float y)
        {
            UtcMs = utcMs;
            Account = account;
            X = x;
            Y = y;
        }

        public long UtcMs { get; }

        public string Account { get; }

        public float X { get; }

        public float Y { get; }
    }

    /// <summary>La posición con la que el servidor hizo aparecer a una cuenta (la guardada).</summary>
    public readonly struct WelcomeSpawn
    {
        public WelcomeSpawn(string account, float x, float y)
        {
            Account = account;
            X = x;
            Y = y;
        }

        public string Account { get; }

        public float X { get; }

        public float Y { get; }
    }

    /// <summary>Resultado de la comparación para una cuenta.</summary>
    public sealed class TraceLossRow
    {
        public TraceLossRow(
            string account,
            long? matchedUtcMs,
            double? lossSeconds,
            float nearestDistance,
            double? nearestLossSeconds)
        {
            Account = account;
            MatchedUtcMs = matchedUtcMs;
            LossSeconds = lossSeconds;
            NearestDistance = nearestDistance;
            NearestLossSeconds = nearestLossSeconds;
        }

        public string Account { get; }

        /// <summary>Último instante de la traza que coincide con el <c>Welcome</c>; <c>null</c> si no hay.</summary>
        public long? MatchedUtcMs { get; }

        /// <summary>Segundos entre la coincidencia y la muerte del servidor; <c>null</c> si no hay.</summary>
        public double? LossSeconds { get; }

        /// <summary>Distancia (peor eje, en tiles) de la muestra más cercana antes de la muerte.</summary>
        public float NearestDistance { get; }

        /// <summary>Pérdida que se mediría con esa muestra más cercana (solo diagnóstico).</summary>
        public double? NearestLossSeconds { get; }

        public bool Ok { get; internal set; }
    }

    /// <summary>Tabla y resumen de pérdidas tras matar y reiniciar el servidor.</summary>
    public sealed class TraceComparison
    {
        public TraceComparison(IReadOnlyList<TraceLossRow> rows, double maxLossSeconds)
        {
            Rows = rows;
            MaxLossSeconds = maxLossSeconds;
        }

        public IReadOnlyList<TraceLossRow> Rows { get; }

        /// <summary>Tope que marca el plan: <c>saveIntervalSeconds</c> + 5 s.</summary>
        public double MaxLossSeconds { get; }

        public int OkCount
        {
            get
            {
                int ok = 0;
                for (int i = 0; i < Rows.Count; i++)
                {
                    if (Rows[i].Ok)
                    {
                        ok++;
                    }
                }

                return ok;
            }
        }

        public bool AllOk => Rows.Count > 0 && OkCount == Rows.Count;
    }

    /// <summary>
    /// Compara la traza de posiciones de los bots con la posición guardada que devuelve el
    /// <c>Welcome</c> después de reiniciar el servidor (G-05, punto 2.5).
    /// </summary>
    /// <remarks>
    /// Formato de la traza: <c>instante_utc_ms;cuenta;x;y</c>, una línea por bot y segundo. Formato
    /// del <c>Welcome</c>: <c>cuenta;spawnX;spawnY</c>. La coincidencia se busca con un margen de
    /// 1/64 de tile en cada eje, que es la cuantización de los snapshots.
    /// </remarks>
    public static class TraceComparer
    {
        /// <summary>Margen de coincidencia: una unidad de cuantización de posición.</summary>
        public const float MatchTolerance = 1f / 64f;

        /// <summary>Lee la traza agregada de los bots. Ignora líneas vacías o mal formadas.</summary>
        public static List<TraceSample> ReadTrace(string path)
        {
            var samples = new List<TraceSample>();

            foreach (string line in File.ReadLines(path))
            {
                string[] fields = line.Split(';');

                if (fields.Length != 4 ||
                    !long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long utcMs) ||
                    !float.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
                    !float.TryParse(fields[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
                {
                    continue;
                }

                samples.Add(new TraceSample(utcMs, fields[1].Trim(), x, y));
            }

            return samples;
        }

        /// <summary>Lee los <c>Welcome</c> anotados por los bots. Si una cuenta repite, vale el último.</summary>
        public static List<WelcomeSpawn> ReadWelcomes(string path)
        {
            var byAccount = new Dictionary<string, WelcomeSpawn>(StringComparer.Ordinal);
            var order = new List<string>();

            foreach (string line in File.ReadLines(path))
            {
                string[] fields = line.Split(';');

                if (fields.Length != 3 ||
                    !float.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
                    !float.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
                {
                    continue;
                }

                string account = fields[0].Trim();

                if (!byAccount.ContainsKey(account))
                {
                    order.Add(account);
                }

                byAccount[account] = new WelcomeSpawn(account, x, y);
            }

            var welcomes = new List<WelcomeSpawn>(order.Count);

            for (int i = 0; i < order.Count; i++)
            {
                welcomes.Add(byAccount[order[i]]);
            }

            return welcomes;
        }

        /// <summary>
        /// Para cada cuenta, busca el último instante de la traza (antes de la muerte) cuya posición
        /// coincide con la del <c>Welcome</c>. La pérdida es lo que separa ese instante de la muerte.
        /// </summary>
        public static TraceComparison Compare(
            IReadOnlyList<TraceSample> trace,
            IReadOnlyList<WelcomeSpawn> welcomes,
            long deathUtcMs,
            double maxLossSeconds)
        {
            if (maxLossSeconds <= 0d || double.IsNaN(maxLossSeconds))
            {
                throw new ArgumentOutOfRangeException(nameof(maxLossSeconds));
            }

            var rows = new List<TraceLossRow>(welcomes.Count);

            for (int w = 0; w < welcomes.Count; w++)
            {
                WelcomeSpawn welcome = welcomes[w];
                long? matchedAt = null;

                float nearest = float.MaxValue;
                long nearestAt = 0;
                bool hasNearest = false;

                for (int i = 0; i < trace.Count; i++)
                {
                    TraceSample sample = trace[i];

                    if (!string.Equals(sample.Account, welcome.Account, StringComparison.Ordinal) ||
                        sample.UtcMs > deathUtcMs)
                    {
                        continue;
                    }

                    float distance = Math.Max(MathF.Abs(sample.X - welcome.X), MathF.Abs(sample.Y - welcome.Y));

                    if (distance <= MatchTolerance && (!matchedAt.HasValue || sample.UtcMs > matchedAt.Value))
                    {
                        matchedAt = sample.UtcMs;
                    }

                    if (distance < nearest)
                    {
                        nearest = distance;
                        nearestAt = sample.UtcMs;
                        hasNearest = true;
                    }
                }

                double? loss = matchedAt.HasValue ? (deathUtcMs - matchedAt.Value) / 1000d : (double?)null;
                double? nearestLoss = hasNearest ? (deathUtcMs - nearestAt) / 1000d : (double?)null;

                var row = new TraceLossRow(
                    welcome.Account,
                    matchedAt,
                    loss,
                    hasNearest ? nearest : float.NaN,
                    nearestLoss);

                row.Ok = loss.HasValue && loss.Value >= 0d && loss.Value <= maxLossSeconds;
                rows.Add(row);
            }

            return new TraceComparison(rows, maxLossSeconds);
        }

        /// <summary>Imprime la tabla por cuenta y el resumen en el escritor indicado.</summary>
        public static void WriteTable(TextWriter writer, TraceComparison comparison)
        {
            if (writer is null)
            {
                throw new ArgumentNullException(nameof(writer));
            }

            writer.WriteLine("  cuenta          pérdida(s)   estado");
            writer.WriteLine("  --------------  -----------  ------");

            double total = 0d;
            double maximum = 0d;
            int measured = 0;

            for (int i = 0; i < comparison.Rows.Count; i++)
            {
                TraceLossRow row = comparison.Rows[i];
                string loss;

                if (row.LossSeconds.HasValue)
                {
                    loss = row.LossSeconds.Value.ToString("0.0", CultureInfo.InvariantCulture);
                    total += row.LossSeconds.Value;
                    measured++;

                    if (row.LossSeconds.Value > maximum)
                    {
                        maximum = row.LossSeconds.Value;
                    }
                }
                else
                {
                    loss = "sin coincidencia";
                }

                writer.WriteLine(
                    "  " + row.Account.PadRight(14) + "  " + loss.PadLeft(11) + "  " +
                    (row.Ok ? "OK" : "FALLO"));
            }

            writer.WriteLine();
            writer.WriteLine(
                "  Cuentas OK : " + comparison.OkCount + "/" + comparison.Rows.Count +
                " (tope " + comparison.MaxLossSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s)");
            writer.WriteLine(
                "  Pérdida    : máxima " + maximum.ToString("0.0", CultureInfo.InvariantCulture) + " s, media " +
                (measured == 0 ? "-" : (total / measured).ToString("0.0", CultureInfo.InvariantCulture) + " s") +
                " (" + measured + " con coincidencia)");

            if (!comparison.AllOk)
            {
                writer.WriteLine("  Fallos:");
                for (int i = 0; i < comparison.Rows.Count; i++)
                {
                    TraceLossRow row = comparison.Rows[i];

                    if (row.Ok)
                    {
                        continue;
                    }

                    writer.Write("    " + row.Account + ": ");

                    if (!row.LossSeconds.HasValue)
                    {
                        writer.Write(
                            "ninguna muestra coincide; la más cercana estaba a " +
                            row.NearestDistance.ToString("0.00000", CultureInfo.InvariantCulture) + " tiles");

                        if (row.NearestLossSeconds.HasValue)
                        {
                            writer.Write(
                                " (" +
                                row.NearestLossSeconds.Value.ToString("0.0", CultureInfo.InvariantCulture) +
                                " s antes de la muerte)");
                        }
                    }
                    else
                    {
                        writer.Write(
                            "pérdida de " + row.LossSeconds.Value.ToString("0.0", CultureInfo.InvariantCulture) +
                            " s, por encima del tope");
                    }

                    writer.WriteLine();
                }
            }
        }
    }
}
