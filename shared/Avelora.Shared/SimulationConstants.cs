#nullable enable

namespace Avelora.Shared
{
    /// <summary>
    /// Parámetros de simulación que cliente y servidor <b>deben</b> compartir.
    /// </summary>
    /// <remarks>
    /// El <i>dt</i> es el corazón del determinismo: si el cliente predice con un paso distinto al
    /// del servidor, la reconciliación corregiría en cada snapshot y el movimiento se vería a
    /// tirones (guía del núcleo §5.1 y §5.3). Por eso el valor vive aquí una sola vez y lo usan
    /// los dos lados, en vez de repetir el número en cada proyecto.
    /// </remarks>
    public static class SimulationConstants
    {
        /// <summary>Ticks por segundo de la simulación (guía del núcleo §2.2).</summary>
        public const int TickRate = 20;

        /// <summary>Paso de tiempo fijo en segundos: <c>1 / TickRate</c>.</summary>
        public const float FixedDt = 1f / TickRate;

        /// <summary>Duración de un tick en milisegundos.</summary>
        public const double StepMs = 1000.0 / TickRate;
    }
}
