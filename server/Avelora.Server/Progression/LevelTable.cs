#nullable enable
using System;
using System.IO;
using System.Text.Json;
using Avelora.Server.Entities;
using Avelora.Server.Items;

namespace Avelora.Server.Progression
{
    /// <summary>Tabla única de umbrales de experiencia y puntos por nivel (I-08).</summary>
    public sealed class LevelTable
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        /// <summary>Reglas de nivel predeterminadas para zonas y repositorios aislados.</summary>
        public static LevelTable Default => new LevelTable
        {
            PointsPerLevel = 3,
            FreeResetMaxLevel = 10,
            ResetItemId = 3201,
            ExperienceToNext = new[]
            {
                100, 150, 225, 340, 500, 750, 1100, 1650, 2500, 3500, 4700, 6200,
                8000, 10000, 12500, 15500, 19000, 23000, 27500, 32500, 38000, 44000, 51000, 59000,
            },
        };

        /// <summary>Puntos repartibles que se ganan en cada nivel, incluido el primero.</summary>
        public int PointsPerLevel { get; set; } = -1;

        /// <summary>Nivel máximo hasta el que el reajuste no consume la piedra (I-09).</summary>
        public int FreeResetMaxLevel { get; set; } = 10;

        /// <summary>Objeto que consume el reajuste por encima del nivel gratuito (I-09).</summary>
        public int ResetItemId { get; set; } = 3201;

        /// <summary>Umbrales para pasar del nivel índice+1 al siguiente.</summary>
        public int[]? ExperienceToNext { get; set; }

        /// <summary>Nivel máximo, uno más que el número de umbrales.</summary>
        public int MaxLevel => (ExperienceToNext?.Length ?? 0) + 1;

        /// <summary>Carga y valida el archivo de niveles.</summary>
        public static LevelTable Load(string path, ItemCatalog? items = null)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("La ruta de levels.json no puede estar vacía.", nameof(path));
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException("No se encuentra el archivo de niveles '" + path + "'.", path);
            }

            string json = File.ReadAllText(path);
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty property in document.RootElement.EnumerateObject())
                    {
                        if (string.Equals(property.Name, "healthPerLevel", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(property.Name, "damagePerLevel", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException(
                                "levels.json: " + property.Name + " se trasladó a classes.json y ya no se admite.");
                        }
                    }
                }
            }

            LevelTable table = JsonSerializer.Deserialize<LevelTable>(json, JsonOptions)
                ?? throw new InvalidOperationException("levels.json está vacío o no contiene un objeto.");
            table.Validate(items);
            return table;
        }

        /// <summary>Valida todos los campos de la tabla.</summary>
        public void Validate(ItemCatalog? items = null)
        {
            if (PointsPerLevel < 0 || PointsPerLevel > 100)
            {
                throw new InvalidOperationException("levels.json: pointsPerLevel debe estar entre 0 y 100.");
            }

            if (ExperienceToNext == null || ExperienceToNext.Length != 24)
            {
                throw new InvalidOperationException("levels.json: experienceToNext debe tener exactamente 24 valores (nivel máximo 25).");
            }

            if (FreeResetMaxLevel < 1 || FreeResetMaxLevel > byte.MaxValue)
            {
                throw new InvalidOperationException("levels.json: freeResetMaxLevel debe estar entre 1 y 255.");
            }

            if (ResetItemId < 1 || ResetItemId > ushort.MaxValue)
            {
                throw new InvalidOperationException("levels.json: resetItemId debe estar entre 1 y 65535.");
            }

            if (items != null && items.Find(ResetItemId) == null)
            {
                throw new InvalidOperationException(
                    "levels.json: resetItemId " + ResetItemId + " no existe en items.json.");
            }

            for (int i = 0; i < ExperienceToNext.Length; i++)
            {
                if (ExperienceToNext[i] < 1 || ExperienceToNext[i] > 10_000_000)
                {
                    throw new InvalidOperationException(
                        "levels.json: experienceToNext[" + i + "] debe estar entre 1 y 10000000.");
                }
            }
        }

        /// <summary>Umbral del nivel actual, o cero cuando ya se alcanzó el máximo.</summary>
        public int GetExperienceToNext(int level)
        {
            RequireLevel(level);
            return level == MaxLevel ? 0 : ExperienceToNext![level - 1];
        }

        /// <summary>
        /// Aplica experiencia ganada al jugador. Devuelve los niveles subidos y rellena la vida en
        /// cada subida; el sobrante se conserva salvo al alcanzar el nivel máximo.
        /// </summary>
        public int AddExperience(PlayerEntity player, int amount, ClassCatalog classes, ItemCatalog? items = null)
        {
            if (player == null)
            {
                throw new ArgumentNullException(nameof(player));
            }

            if (classes == null)
            {
                throw new ArgumentNullException(nameof(classes));
            }

            RequireLevel(player.Level);
            if (player.Level == MaxLevel || amount <= 0)
            {
                if (player.Level == MaxLevel)
                {
                    player.Experience = 0;
                }

                return 0;
            }

            long experience = (long)player.Experience + amount;
            int levelsGained = 0;
            while (player.Level < MaxLevel && experience >= GetExperienceToNext(player.Level))
            {
                experience -= GetExperienceToNext(player.Level);
                player.Level++;
                DerivedCharacterStats stats = CharacterStats.Calculate(
                    classes,
                    player.ClassId,
                    player.Level,
                    player.AllocStr,
                    player.AllocDex,
                    player.AllocInt,
                    player.AllocVit,
                    items,
                    player.Equipment);
                player.MaxHealth = stats.MaxHealth;
                player.MaxMana = stats.MaxResource;
                player.Health = player.MaxHealth;
                levelsGained++;
            }

            player.Experience = player.Level == MaxLevel ? 0 : checked((int)experience);
            player.ProgressChanged = true;
            player.StatsChanged |= levelsGained > 0;
            return levelsGained;
        }

        private void RequireLevel(int level)
        {
            if (ExperienceToNext == null || level < 1 || level > MaxLevel)
            {
                throw new ArgumentOutOfRangeException(nameof(level));
            }
        }
    }
}
