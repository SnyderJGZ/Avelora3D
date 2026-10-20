#nullable enable
namespace Avelora.Shared
{
    /// <summary>
    /// Comparación de números de secuencia de 32 bits que dan la vuelta.
    /// </summary>
    /// <remarks>
    /// Comparar con <c>&lt;</c> falla al pasar de <c>uint.MaxValue</c> a 0. Restar y mirar el signo
    /// del resultado es lo correcto: a 20 inputs por segundo la vuelta tarda 6,8 años, pero el
    /// código no debería tener una bomba de relojería.
    ///
    /// El precio es que solo distingue «más nuevo» dentro de media vuelta (2^31 secuencias): una
    /// diferencia mayor es ambigua y se considera vieja. Para una sesión de juego es de sobra.
    /// </remarks>
    public static class SequenceMath
    {
        /// <summary>¿<paramref name="candidate"/> es posterior a <paramref name="reference"/>?</summary>
        public static bool IsNewer(uint candidate, uint reference) =>
            unchecked((int)(candidate - reference)) > 0;
    }
}
