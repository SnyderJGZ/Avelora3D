#nullable enable
using System;
using Avelora.Shared;

namespace Avelora.Server.Simulation
{
    /// <summary>
    /// Reglas de combate de los jugadores (I-02), ya convertidas a unidades de simulación. Se
    /// valida en <see cref="Avelora.Server.Configuration.ServerConfig"/> al arrancar; aquí solo se
    /// comprueba lo mínimo por si se construye por otra vía.
    /// </summary>
    public readonly struct CombatSettings
    {
        public CombatSettings(int damage, float range, float rangeTolerance, double cooldownSeconds)
        {
            if (damage < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(damage), damage, "El daño debe ser >= 1.");
            }

            if (!(range > 0f) || float.IsNaN(range) || float.IsInfinity(range))
            {
                throw new ArgumentOutOfRangeException(nameof(range), range, "El alcance debe ser positivo.");
            }

            if (rangeTolerance < 0f || float.IsNaN(rangeTolerance) || float.IsInfinity(rangeTolerance))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(rangeTolerance),
                    rangeTolerance,
                    "La tolerancia no puede ser negativa.");
            }

            if (!(cooldownSeconds > 0d) ||
                double.IsNaN(cooldownSeconds) ||
                double.IsInfinity(cooldownSeconds))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cooldownSeconds),
                    cooldownSeconds,
                    "El tiempo de recarga debe ser positivo.");
            }

            Damage = damage;
            Range = range;
            RangeTolerance = rangeTolerance;

            // La recarga se mide en ticks: redondeo hacia arriba para no dejar pasar dos ataques en
            // el mismo tick por un decimal de menos.
            CooldownTicks = Math.Max(
                1L,
                (long)Math.Ceiling(cooldownSeconds * SimulationConstants.TickRate));
        }

        /// <summary>Vida que quita cada ataque válido.</summary>
        public int Damage { get; }

        /// <summary>Alcance entre centros, en tiles.</summary>
        public float Range { get; }

        /// <summary>Margen que se suma al alcance para compensar la interpolación del cliente.</summary>
        public float RangeTolerance { get; }

        /// <summary>Ticks mínimos entre dos ataques válidos del mismo jugador.</summary>
        public long CooldownTicks { get; }

        /// <summary>Alcance efectivo: <see cref="Range"/> + <see cref="RangeTolerance"/>.</summary>
        public float EffectiveRange => Range + RangeTolerance;
    }
}
