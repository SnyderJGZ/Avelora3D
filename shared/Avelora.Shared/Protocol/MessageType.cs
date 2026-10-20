#nullable enable
namespace Avelora.Shared.Protocol
{
    /// <summary>
    /// Primer byte de cada mensaje (plan del servidor §4). Los tipos de cliente y de servidor no
    /// se solapan a propósito: si un mensaje llega en la dirección equivocada, el lector lo rechaza
    /// sin ambigüedad.
    /// </summary>
    public enum MessageType : byte
    {
        /// <summary>Cliente → servidor: saludo con versión, tick y huella del mapa.</summary>
        Hello = 1,

        /// <summary>Cliente → servidor: intención de movimiento de un tick.</summary>
        MoveInput = 2,

        /// <summary>Servidor → cliente: aceptación del saludo con la identidad y el punto de aparición.</summary>
        Welcome = 10,

        /// <summary>Servidor → cliente: motivo por el que no se acepta la conexión.</summary>
        Reject = 11,

        /// <summary>Servidor → cliente: estado del mundo en un tick.</summary>
        Snapshot = 12,
    }
}
