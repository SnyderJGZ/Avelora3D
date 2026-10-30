#nullable enable
using System;
using Avelora.Server.Entities;
using Avelora.Server.Progression;
using Avelora.Shared;

namespace Avelora.Server.Simulation
{
    /// <summary>
    /// Muertes, reapariciones y regeneración de los jugadores (I-03).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corre en el hilo de simulación, dentro de <see cref="Zone.Tick"/>, después de la IA de los
    /// enemigos: un golpe de este tick puede matar al jugador y el sistema lo ve en el mismo tick.
    /// </para>
    /// <para>
    /// La regeneración se aplica por ticks con un acumulador fraccionario: no se usa ningún reloj.
    /// Solo se acumula cuando el jugador está vivo, no está lleno y ha pasado el retraso desde el
    /// último daño. La vida se suma en puntos enteros y nunca pasa del máximo.
    /// </para>
    /// </remarks>
    public sealed class PlayerVitalsSystem
    {
        private readonly PlayerVitalsSettings _settings;
        private readonly LevelTable _levels;

        public PlayerVitalsSystem(PlayerVitalsSettings settings, LevelTable levels)
        {
            _settings = settings;
            _levels = levels ?? throw new ArgumentNullException(nameof(levels));
        }

        /// <summary>Reglas efectivas.</summary>
        public PlayerVitalsSettings Settings => _settings;

        /// <summary>Jugadores que han muerto desde el arranque.</summary>
        public long Deaths { get; private set; }

        /// <summary>Jugadores que han reaparecido desde el arranque.</summary>
        public long Respawns { get; private set; }

        /// <summary>Muertes que han aplicado una pérdida de experiencia.</summary>
        public long PenalizedDeaths { get; private set; }

        /// <summary>Experiencia perdida en muertes penalizadas.</summary>
        public long ExperienceLost { get; private set; }

        /// <summary>Marca las muertes, repone a quien toca y regenera a los vivos.</summary>
        public void Tick(EntityStore entities, long tick)
        {
            if (entities is null)
            {
                throw new ArgumentNullException(nameof(entities));
            }

            for (int i = 0; i < entities.Count; i++)
            {
                if (!(entities[i] is PlayerEntity player))
                {
                    continue;
                }

                if (player.Health <= 0 || player.Action == EntityAction.Dead)
                {
                    MarkDeath(player, tick);

                    if (tick - player.DeathTick >= _settings.RespawnTicks)
                    {
                        Respawn(player);
                    }

                    continue;
                }

                Regenerate(player, tick);
            }
        }

        private void MarkDeath(PlayerEntity player, long tick)
        {
            if (player.Action != EntityAction.Dead)
            {
                player.Action = EntityAction.Dead;
            }

            if (player.DeathTick < 0)
            {
                player.DeathTick = tick;
            }

            if (player.DeathCounted)
            {
                player.KilledByNpc = false;
                return;
            }

            player.DeathCounted = true;
            player.DirectionX = 0;
            player.DirectionY = 0;
            player.RegenAccumulator = 0d;
            Deaths++;

            if (player.KilledByNpc)
            {
                int loss = _levels.GetDeathPenalty(player);
                if (loss > 0)
                {
                    player.Experience -= loss;
                    player.ProgressChanged = true;
                    PenalizedDeaths++;
                    ExperienceLost += loss;
                }
            }
            // Muerte por jugador o sin causa: no penaliza.
            player.KilledByNpc = false;
        }

        private void Respawn(PlayerEntity player)
        {
            player.Position = _settings.Spawn;
            player.Health = player.MaxHealth;
            player.Action = EntityAction.None;
            player.DirectionX = 0;
            player.DirectionY = 0;
            player.LastAttackTick = -1;
            player.LastDamageTick = -1;
            player.RegenAccumulator = 0d;
            player.DeathTick = -1;
            player.DeathCounted = false;
            player.KilledByNpc = false;
            Respawns++;
        }

        private void Regenerate(PlayerEntity player, long tick)
        {
            if (player.Health >= player.MaxHealth)
            {
                player.RegenAccumulator = 0d;
                return;
            }

            if (_settings.RegenPerSecond <= 0d)
            {
                return;
            }

            if (player.LastDamageTick >= 0 && tick - player.LastDamageTick < _settings.RegenDelayTicks)
            {
                return;
            }

            player.RegenAccumulator += _settings.RegenPerSecond * SimulationConstants.FixedDt;

            int whole = (int)player.RegenAccumulator;
            if (whole <= 0)
            {
                return;
            }

            player.RegenAccumulator -= whole;
            player.Health = Math.Min(player.MaxHealth, player.Health + whole);
        }
    }
}
