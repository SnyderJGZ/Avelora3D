#nullable enable
using System;

namespace Avelora.Shared.Protocol
{
    /// <summary>Un mensaje no cumple el formato: llega corto, largo o con un tipo que no toca.</summary>
    /// <remarks>
    /// Solo se lanza mientras se lee un paquete. Las funciones <c>TryRead*</c> del códec la capturan
    /// y devuelven <c>false</c>: en la frontera de red, «formato inválido» es un resultado normal,
    /// no una excepción que deba propagarse.
    /// </remarks>
    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string message)
            : base(message)
        {
        }
    }
}
