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

        /// <summary>Cliente → servidor: crear un personaje (I-10).</summary>
        CreateCharacter = 23,

        /// <summary>Cliente → servidor: borrar un personaje con confirmación (I-10).</summary>
        DeleteCharacter = 24,

        /// <summary>Cliente → servidor: seleccionar un personaje (I-10).</summary>
        SelectCharacter = 25,

        /// <summary>Servidor → cliente: personajes de la cuenta y clases disponibles (I-10).</summary>
        CharacterList = 26,

        /// <summary>Servidor → cliente: rechazo de una acción de personaje (I-10).</summary>
        CharacterRejected = 27,

        /// <summary>Servidor → cliente: eventos cosméticos de combate del snapshot (I-13).</summary>
        CombatEvent = 28,

        /// <summary>Servidor → cliente: experiencia, oro y objetos obtenidos al matar un PNJ (I-25).</summary>
        LootGained = 29,

        /// <summary>Servidor → cliente: zonas del mapa disponibles (I-15).</summary>
        ZoneList = 30,

        /// <summary>Servidor → cliente: cambio de zona del jugador (I-15).</summary>
        ZoneChanged = 31,

        /// <summary>Cliente → servidor: elegir modo PvE o PvP (I-15p).</summary>
        SetPvpMode = 32,

        /// <summary>Servidor → cliente: estado propio del modo y los temporizadores PvP (I-15p).</summary>
        PvpStatus = 33,

        /// <summary>Servidor → cliente: motivo de rechazo del cambio de modo PvP (I-15p).</summary>
        PvpModeRejected = 34,

        /// <summary>Cliente → servidor: usar una habilidad (I-16).</summary>
        UseSkill = 35,

        /// <summary>Servidor → cliente: catálogo de habilidades de la clase del personaje (I-16).</summary>
        SkillCatalog = 36,

        /// <summary>Servidor → cliente: confirmación de uso de habilidad (I-16).</summary>
        SkillUsed = 37,

        /// <summary>Servidor → cliente: rechazo de uso de habilidad (I-16).</summary>
        SkillRejected = 38,

        /// <summary>Servidor → cliente: estado del recurso (actual y máximo) (I-16).</summary>
        ResourceStatus = 39,

        /// <summary>Servidor → cliente: estados y velocidad del propio jugador (I-18).</summary>
        StatusEffects = 40,

        /// <summary>Servidor → cliente: máscara de estados de entidades visibles (I-18).</summary>
        StatusEvent = 41,
    }
}
