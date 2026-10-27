#nullable enable
using System;

namespace Avelora.Server.Simulation
{
    /// <summary>
    /// Reglas de combate de los jugadores que no dependen de la clase ni del arma (I-02). El daño,
    /// el alcance y la recarga salen de <c>classes.json</c> o del arma equipada (I-08). Se valida en
    /// <see cref="Avelora.Server.Configuration.ServerConfig"/> al arrancar; aquí solo se comprueba
    /// lo mínimo por si se construye por otra vía.
    /// </summary>
    public readonly struct CombatSettings
    {
        public CombatSettings(float rangeTolerance)
        {
            if (rangeTolerance < 0f || float.IsNaN(rangeTolerance) || float.IsInfinity(rangeTolerance))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(rangeTolerance),
                    rangeTolerance,
                    "La tolerancia no puede ser negativa.");
            }

            RangeTolerance = rangeTolerance;
        }

        /// <summary>Margen que se suma al alcance para compensar la interpolación del cliente.</summary>
        public float RangeTolerance { get; }
    }
}
