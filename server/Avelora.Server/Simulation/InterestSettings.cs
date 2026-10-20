#nullable enable
using System;

namespace Avelora.Server.Simulation
{
    /// <summary>
    /// Parámetros del área de interés de una zona: qué entidades recibe cada jugador.
    /// </summary>
    /// <remarks>
    /// El radio se mide en tiles desde la posición del jugador. Una entidad que ya estaba
    /// visible permanece hasta el <see cref="ExitRadius"/>; una nueva tiene que entrar dentro de
    /// <see cref="Radius"/>. Esa histéresis evita que una entidad parada justo en el borde
    /// aparezca y desaparezca en ticks alternos.
    /// </remarks>
    public sealed class InterestSettings
    {
        /// <summary>Radio de entrada por defecto, en tiles. Cubre de sobra la vista actual (19×13).</summary>
        public const float DefaultRadius = 12f;

        /// <summary>Lado de celda por defecto de la grilla espacial, en tiles.</summary>
        public const float DefaultCellSize = 8f;

        /// <summary>Margen de salida por defecto, en tiles.</summary>
        public const float DefaultHysteresis = 1f;

        /// <summary>Configuración por defecto, usada cuando una zona no recibe otra.</summary>
        public static InterestSettings Default { get; } = new InterestSettings(
            DefaultRadius,
            DefaultCellSize,
            DefaultHysteresis);

        public InterestSettings(float radius, float cellSize, float hysteresis)
        {
            if (!IsFinite(radius) || radius <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(radius), radius, "El radio de interés debe ser positivo.");
            }

            if (!IsFinite(cellSize) || cellSize <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "La celda espacial debe ser positiva.");
            }

            if (!IsFinite(hysteresis) || hysteresis < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(hysteresis), hysteresis, "La histéresis no puede ser negativa.");
            }

            if (!IsFinite(radius + hysteresis))
            {
                throw new ArgumentOutOfRangeException(nameof(hysteresis), hysteresis, "El radio de salida no es representable.");
            }

            Radius = radius;
            CellSize = cellSize;
            Hysteresis = hysteresis;
        }

        /// <summary>Radio de entrada: una entidad nueva se ve si está a esta distancia o menos.</summary>
        public float Radius { get; }

        /// <summary>Lado de cada celda de la grilla espacial.</summary>
        public float CellSize { get; }

        /// <summary>Margen extra que conserva visible a una entidad que ya lo estaba.</summary>
        public float Hysteresis { get; }

        /// <summary>Radio de salida: <see cref="Radius"/> + <see cref="Hysteresis"/>.</summary>
        public float ExitRadius => Radius + Hysteresis;

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
