#nullable enable
using System;
using Avelora.Shared;

namespace Avelora.Server.Simulation
{
    /// <summary>
    /// Reglas de vida del jugador (I-03), ya convertidas a unidades de simulación. Se validan en
    /// <see cref="Avelora.Server.Configuration.ServerConfig"/> al arrancar; aquí solo se comprueba
    /// lo mínimo por si se construye por otra vía.
    /// </summary>
    /// <remarks>
    /// El punto de reaparición viaja con las reglas a propósito: la simulación no lee
    /// <c>ServerConfig</c>. <see cref="Zone"/> recibe esta estructura al construirse.
    /// </remarks>
    public readonly struct PlayerVitalsSettings
    {
        public PlayerVitalsSettings(
            Vec2 spawn,
            double respawnSeconds,
            double regenPerSecond,
            double regenDelaySeconds)
        {
            if (!(respawnSeconds >= 0.5d) ||
                respawnSeconds > 600d ||
                double.IsNaN(respawnSeconds) ||
                double.IsInfinity(respawnSeconds))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(respawnSeconds),
                    respawnSeconds,
                    "La reaparición debe estar entre 0,5 y 600 segundos.");
            }

            if (regenPerSecond < 0d || double.IsNaN(regenPerSecond) || double.IsInfinity(regenPerSecond))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(regenPerSecond),
                    regenPerSecond,
                    "La regeneración no puede ser negativa.");
            }

            if (regenDelaySeconds < 0d ||
                double.IsNaN(regenDelaySeconds) ||
                double.IsInfinity(regenDelaySeconds))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(regenDelaySeconds),
                    regenDelaySeconds,
                    "El retraso de regeneración no puede ser negativo.");
            }

            Spawn = spawn;
            RegenPerSecond = regenPerSecond;
            RespawnTicks = Math.Max(1L, (long)Math.Ceiling(respawnSeconds * SimulationConstants.TickRate));
            RegenDelayTicks = (long)Math.Ceiling(regenDelaySeconds * SimulationConstants.TickRate);
        }

        /// <summary>Punto al que vuelve un jugador muerto cuando reaparece.</summary>
        public Vec2 Spawn { get; }

        /// <summary>Vida recuperada por segundo cuando toca regenerar.</summary>
        public double RegenPerSecond { get; }

        /// <summary>Ticks que deben pasar desde el último daño antes de regenerar.</summary>
        public long RegenDelayTicks { get; }

        /// <summary>Ticks que un jugador permanece muerto antes de reaparecer.</summary>
        public long RespawnTicks { get; }
    }
}
