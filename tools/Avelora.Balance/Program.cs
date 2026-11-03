#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Avelora.Server.Items;
using Avelora.Server.Npcs;
using Avelora.Server.Progression;

namespace Avelora.Balance
{
    /// <summary>Punto de entrada de la herramienta de informes de equilibrio.</summary>
    public static class Program
    {
        /// <summary>Ejecuta el CLI y devuelve su código de salida.</summary>
        public static int Run(string[] args, TextWriter output)
        {
            if (args == null)
                throw new ArgumentNullException(nameof(args));
            if (output == null)
                throw new ArgumentNullException(nameof(output));

            if (!TryParse(args, output, out Options options))
                return 2;
            if (options.Help)
            {
                output.WriteLine("Uso: Avelora.Balance [--datos carpeta] [--niveles 1,5,10,25] [--build puro|equilibrado] [--equipo ninguno|franja] [--sin-habilidades] [--duelos] [--salida archivo]");
                return 0;
            }

            try
            {
                string dataDirectory = options.DataDirectory ?? FindDataDirectory();
                ClassCatalog classes = ClassCatalog.Load(Path.Combine(dataDirectory, "classes.json"));
                ItemCatalog items = ItemCatalog.Load(Path.Combine(dataDirectory, "items.json"));
                LevelTable levels = LevelTable.Load(Path.Combine(dataDirectory, "levels.json"), items);
                SkillCatalog skills = SkillCatalog.Load(Path.Combine(dataDirectory, "skills.json"), classes);
                NpcCatalog npcs = NpcCatalog.Load(Path.Combine(dataDirectory, "npcs.json"));
                var simulator = new CombatSimulator(classes, items, skills, npcs, levels);
                string report = CreateReport(classes, simulator, options);
                output.Write(report);
                if (options.OutputFile != null)
                    File.WriteAllText(options.OutputFile, report, new UTF8Encoding(false));
                return 0;
            }
            catch (Exception exception)
            {
                output.WriteLine("Error: " + exception.Message);
                return 2;
            }
        }

        private static bool TryParse(string[] args, TextWriter output, out Options options)
        {
            options = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string argument = args[i];
                if (argument == "--sin-habilidades")
                {
                    options.IncludeSkills = false;
                    continue;
                }
                if (argument == "--duelos")
                {
                    options.IncludeDuels = true;
                    continue;
                }
                if (argument == "--help")
                {
                    options.Help = true;
                    continue;
                }
                if (argument != "--datos" && argument != "--niveles" && argument != "--build" &&
                    argument != "--equipo" && argument != "--salida")
                {
                    output.WriteLine("Argumento desconocido: " + argument);
                    return false;
                }
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    output.WriteLine("Falta el valor para " + argument + ".");
                    return false;
                }

                string value = args[++i];
                switch (argument)
                {
                    case "--datos":
                        options.DataDirectory = value;
                        break;
                    case "--niveles":
                        options.Levels.Clear();
                        if (!TryParseLevels(value, options.Levels))
                        {
                            output.WriteLine("Niveles inválidos: use una lista separada por comas entre 1 y 25.");
                            return false;
                        }
                        break;
                    case "--build":
                        if (value != "puro" && value != "equilibrado")
                        {
                            output.WriteLine("Build inválido: use puro o equilibrado.");
                            return false;
                        }
                        options.Build = value;
                        break;
                    case "--equipo":
                        if (value != "ninguno" && value != "franja")
                        {
                            output.WriteLine("Equipo inválido: use ninguno o franja.");
                            return false;
                        }
                        options.Equipment = value;
                        break;
                    case "--salida":
                        options.OutputFile = value;
                        break;
                }
            }
            return true;
        }

        private static bool TryParseLevels(string value, List<int> levels)
        {
            string[] values = value.Split(',');
            for (int i = 0; i < values.Length; i++)
            {
                if (!int.TryParse(values[i], NumberStyles.None, CultureInfo.InvariantCulture, out int level) ||
                    level < 1 || level > 25)
                    return false;
                levels.Add(level);
            }
            return levels.Count > 0;
        }

        private static string FindDataDirectory()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Avelora.slnx")))
                    return Path.Combine(directory.FullName, "server", "Avelora.Server");
                directory = directory.Parent;
            }
            throw new DirectoryNotFoundException("No se encontró Avelora.slnx desde el directorio del ejecutable; indique --datos.");
        }

        private static string CreateReport(ClassCatalog classes, CombatSimulator simulator, Options options)
        {
            var results = new List<CombatSimulationResult>();
            for (int classIndex = 0; classIndex < classes.Classes!.Count; classIndex++)
            {
                CharacterClass characterClass = classes.Classes[classIndex];
                for (int levelIndex = 0; levelIndex < options.Levels.Count; levelIndex++)
                {
                    results.Add(simulator.Simulate(characterClass.Id, options.Levels[levelIndex],
                        options.Build, options.Equipment, options.IncludeSkills));
                }
            }

            var report = new StringBuilder();
            report.AppendLine("## DPS de objetivo único");
            report.AppendLine();
            report.AppendLine("| Clase | Nivel | DPS |");
            report.AppendLine("| --- | ---: | ---: |");
            for (int i = 0; i < results.Count; i++)
            {
                CombatSimulationResult result = results[i];
                report.Append("| ").Append(result.ClassName).Append(" | ").Append(result.Level)
                    .Append(" | ").Append(FormatDps(result.DamagePerSecond)).AppendLine(" |");
            }
            report.AppendLine();
            report.AppendLine("## Combate contra PNJ");
            report.AppendLine();
            report.AppendLine("| Clase | Nivel | PNJ | TTK (s) | Vida perdida (%) |");
            report.AppendLine("| --- | ---: | --- | ---: | ---: |");
            for (int i = 0; i < results.Count; i++)
            {
                CombatSimulationResult result = results[i];
                for (int npcIndex = 0; npcIndex < result.Npcs.Count; npcIndex++)
                {
                    NpcCombatResult npc = result.Npcs[npcIndex];
                    report.Append("| ").Append(result.ClassName).Append(" | ").Append(result.Level)
                        .Append(" | ").Append(npc.NpcName).Append(" | ").Append(npc.TimeToKillSeconds is double ttk ? Format(ttk) : "n/d")
                        .Append(" | ").Append(Format(npc.HealthLostPercent)).AppendLine(" |");
                }
            }
            if (options.IncludeDuels)
            {
                report.AppendLine();
                report.AppendLine("## Duelos (valor esperado, ataque básico)");
                report.AppendLine();
                report.AppendLine("| Nivel | A | B | Gana | Primer muerto (s) | Ratio |");
                report.AppendLine("| ---: | --- | --- | --- | ---: | ---: |");
                for (int levelIndex = 0; levelIndex < options.Levels.Count; levelIndex++)
                {
                    int level = options.Levels[levelIndex];
                    for (int aIndex = 0; aIndex < classes.Classes!.Count; aIndex++)
                    {
                        CharacterClass a = classes.Classes[aIndex];
                        for (int bIndex = aIndex + 1; bIndex < classes.Classes.Count; bIndex++)
                        {
                            CharacterClass b = classes.Classes[bIndex];
                            DuelResult duel = simulator.SimulateDuel(a.Id, b.Id, level,
                                options.Build, options.Equipment);
                            string winner = duel.WinnerClassId == 0 ? "Empate" :
                                classes.Find(duel.WinnerClassId)!.Name;
                            report.Append("| ").Append(level).Append(" | ").Append(a.Name).Append(" | ")
                                .Append(b.Name).Append(" | ").Append(winner).Append(" | ")
                                .Append(Format(duel.SecondsToFirstKill)).Append(" | ")
                                .Append(Format(duel.KillTimeRatio)).AppendLine(" |");
                        }
                    }
                }
            }
            return report.ToString();
        }

        private static string FormatDps(double value) =>
            Math.Round(value, 1, MidpointRounding.ToEven).ToString("0.0", CultureInfo.InvariantCulture);

        private static string Format(double value) =>
            Math.Round(value, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture);

        private sealed class Options
        {
            public string? DataDirectory { get; set; }
            public List<int> Levels { get; } = new List<int> { 1, 5, 10, 25 };
            public string Build { get; set; } = "puro";
            public string Equipment { get; set; } = "ninguno";
            public string? OutputFile { get; set; }
            public bool IncludeSkills { get; set; } = true;
            public bool IncludeDuels { get; set; }
            public bool Help { get; set; }
        }

        /// <summary>Invoca el método comprobable <see cref="Run"/> con la consola estándar.</summary>
        public static int Main(string[] args) => Run(args, Console.Out);
    }
}
