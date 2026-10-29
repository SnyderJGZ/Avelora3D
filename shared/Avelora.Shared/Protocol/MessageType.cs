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

        /// <summary>Cliente → servidor: intención de atacar a una entidad (I-02).</summary>
        Attack = 3,

        /// <summary>Cliente → servidor: recoger un objeto del suelo (I-04).</summary>
        PickUp = 4,

        /// <summary>Cliente → servidor: usar un objeto del inventario (I-04).</summary>
        UseItem = 5,

        /// <summary>Cliente → servidor: equipar el arma de un hueco (I-05).</summary>
        Equip = 6,

        /// <summary>Cliente → servidor: guardar el arma equipada en el inventario (I-05).</summary>
        Unequip = 7,

        /// <summary>Cliente → servidor: enviar texto al chat de la zona (I-07).</summary>
        ChatSend = 8,

        /// <summary>Cliente → servidor: añadir puntos a los atributos (I-09).</summary>
        AllocatePoints = 9,

        /// <summary>Servidor → cliente: aceptación del saludo con la identidad y el punto de aparición.</summary>
        Welcome = 10,

        /// <summary>Servidor → cliente: motivo por el que no se acepta la conexión.</summary>
        Reject = 11,

        /// <summary>Servidor → cliente: estado del mundo en un tick.</summary>
        Snapshot = 12,

        /// <summary>
        /// Servidor → cliente: nonce y clave pública con la que cifrar el saludo (protocolo v4).
        /// Se envía nada más conectar, antes de que el cliente mande <see cref="Hello"/>.
        /// </summary>
        Challenge = 13,

        /// <summary>Servidor → cliente: inventario completo (I-04).</summary>
        Inventory = 14,

        /// <summary>Servidor → cliente: catálogo y reglas de objetos (I-05).</summary>
        ItemCatalog = 15,

        /// <summary>Servidor → cliente: nivel, experiencia y vida máxima (I-06).</summary>
        Progress = 16,

        /// <summary>Servidor → cliente: mensaje del chat de la zona (I-07).</summary>
        Chat = 17,

        /// <summary>Servidor → cliente: atributos y estadísticas actuales (I-09).</summary>
        Stats = 18,

        /// <summary>Cliente → servidor: devolver todos los puntos repartidos (I-09).</summary>
        ResetPoints = 19,

        /// <summary>Cliente → servidor: mover, dividir, juntar o intercambiar objetos (I-14).</summary>
        MoveItem = 20,

        // El tipo 21 se retiró en I-21. No reutilizarlo para evitar interpretar clientes antiguos.

        /// <summary>Cliente → servidor: destruir objetos explícitamente (I-14).</summary>
        DestroyItem = 22,
    }
}
