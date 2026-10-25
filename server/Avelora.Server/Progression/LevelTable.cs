#nullable enable
using System;
using System.IO;
using System.Text.Json;
using Avelora.Server.Entities;

namespace Avelora.Server.Progression
{
    /// <summary>Tabla única de umbrales y fórmulas de nivel, vida y daño (I-06).</summary>
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
            HealthPerLevel = 15,
            DamagePerLevel = 1,
            ExperienceToNext = new[] { 100, 150, 225, 340, 500, 750, 1100, 1650, 2500 },
        };

        /// <summary>Vida máxima adicional por cada nivel por encima del primero.</summary>
        public int HealthPerLevel { get; set; } = -1;

        /// <summary>Daño adicional por cada nivel por encima del primero.</summary>
        public int DamagePerLevel { get; set; } = -1;

        /// <summary>Umbrales para pasar del nivel índice+1 al siguiente.</summary>
        public int[]? ExperienceToNext { get; set; }

        /// <summary>Nivel máximo, uno más que el número de umbrales.</summary>
        public int MaxLevel => (ExperienceToNext?.Length ?? 0) + 1;

        /// <summary>Carga y valida el archivo de niveles.</summary>
        public static LevelTable Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("La ruta de levels.json no puede estar vacía.", nameof(path));
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException("No se encuentra el archivo de niveles '" + path + "'.", path);
            }

            LevelTable table = JsonSerializer.Deserialize<LevelTable>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidOperationException("levels.json está vacío o no contiene un objeto.");
            table.Validate();
            return table;
        }

        /// <summary>Valida todos los campos de la tabla.</summary>
        public void Validate()
        {
            if (HealthPerLevel < 0 || HealthPerLevel > 10000)
            {
                throw new InvalidOperationException("levels.json: healthPerLevel debe estar entre 0 y 10000.");
            }

            if (DamagePerLevel < 0 || DamagePerLevel > 10000)
            {
                throw new InvalidOperationException("levels.json: damagePerLevel debe estar entre 0 y 10000.");
            }

            if (ExperienceToNext == null || ExperienceToNext.Length < 1 || ExperienceToNext.Length > 254)
            {
                throw new InvalidOperationException("levels.json: experienceToNext debe tener entre 1 y 254 valores.");
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

        /// <summary>Calcula la vida máxima: vida base + (nivel − 1) × healthPerLevel.</summary>
        public int GetMaxHealth(int baseMaxHealth, int level)
        {
            RequireLevel(level);
            if (baseMaxHealth < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(baseMaxHealth));
            }

            return checked(baseMaxHealth + ((level - 1) * HealthPerLevel));
        }

        /// <summary>Calcula el daño: daño base + (nivel − 1) × damagePerLevel.</summary>
        public int GetAttackDamage(int baseDamage, int level)
        {
            RequireLevel(level);
            if (baseDamage < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(baseDamage));
            }

            return checked(baseDamage + ((level - 1) * DamagePerLevel));
        }

        /// <summary>
        /// Aplica experiencia ganada al jugador. Devuelve los niveles subidos y rellena la vida en
        /// cada subida; el sobrante se conserva salvo al alcanzar el nivel máximo.
        /// </summary>
        public int AddExperience(PlayerEntity player, int amount, int baseMaxHealth)
        {
            if (player == null)
            {
                throw new ArgumentNullException(nameof(player));
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
                player.MaxHealth = GetMaxHealth(baseMaxHealth, player.Level);
                player.Health = player.MaxHealth;
                levelsGained++;
            }

            player.Experience = player.Level == MaxLevel ? 0 : checked((int)experience);
            player.ProgressChanged = true;
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
