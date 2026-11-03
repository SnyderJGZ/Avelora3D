#nullable enable
using System;

namespace Avelora.Shared.Protocol
{
    /// <summary>
    /// Constantes del protocolo binario compartido por cliente y servidor (plan del servidor §4).
    /// </summary>
    /// <remarks>
    /// Subir <see cref="Version"/> cuando cambie el formato de cualquier mensaje: el saludo compara
    /// versiones y rechaza clientes desactualizados antes de que puedan tocar el mundo.
    /// </remarks>
    public static class ProtocolConstants
    {
        /// <summary>
        /// Versión del protocolo. Cambia con el formato, no con el contenido de los mensajes.
        /// La Etapa F sube a 2 porque el saludo incorpora las credenciales y el personaje; G-03 a 3
        /// porque el <c>MoveInput</c> lleva la dirección de los inputs anteriores (redundancia);
        /// H-02 a 4 porque el servidor manda un <c>Challenge</c> y el <c>Hello</c> lleva las
        /// credenciales cifradas en vez de en claro. I-02 a 5 porque aparece el <c>Attack</c> y
        /// <c>EntityState</c> gana vida y acción (2 bytes más). I-03c a 6 porque <c>EntityState</c> gana
        /// el tipo de PNJ (2 bytes más) para que el cliente distinga un enemigo de otro. I-04 a 7
        /// porque aparecen <c>PickUp</c>, <c>UseItem</c>, <c>Inventory</c> y <c>EntityKind.Item</c>;
        /// I-05 a 8 porque se añaden <c>Equip</c>, <c>Unequip</c>, <c>ItemCatalog</c> y el arma equipada;
        /// I-06 a 9 por el mensaje <c>Progress</c>; I-07 a 10 por <c>ChatSend</c> y <c>Chat</c>;
        /// I-09 a 11 por <c>AllocatePoints</c>, <c>ResetPoints</c> y <c>Stats</c>; I-14 a 12
        /// por los mensajes y datos de equipo e inventario interactivo; I-20 a 13 porque el byte
        /// de acción de <c>EntityState</c> también transporta el rango de los PNJ; I-21 a 14 por
        /// trocear <c>ItemCatalog</c> y retirar <c>DropItem</c>; I-10 a 15 por el saludo sin
        /// personaje y la selección posterior; I-12 a 16 por los metadatos de armas por clase y nivel;
        /// I-13 a 17 por CombatEvent, I-25 a 18 por el oro del inventario y LootGained, I-15 a 19
        /// por ZoneList y ZoneChanged e I-15p a 20 por el estado y los mensajes de modo PvP;
        /// I-16 a 21 por UseSkill, SkillCatalog, SkillUsed, SkillRejected y ResourceStatus; I-17 a 22 por áreas,
        /// curación y objetivo en SkillUsed; I-18 a 23 por estados temporales y StatusEffects/StatusEvent.
        /// </summary>
        public const ushort Version = 24;

        /// <summary>Máximo de estados activos de tipos distintos por entidad (I-18).</summary>
        public const int MaxStatusEffects = 4;

        /// <summary>Máximo de entidades notificadas en un mensaje de cambios de estado (I-18).</summary>
        public const int MaxStatusEventsPerMessage = 64;

        /// <summary>Máximo de zonas de mapa anunciadas al entrar (I-15).</summary>
        public const int MaxZones = 32;

        /// <summary>Máximo de rectángulos de una zona (I-15).</summary>
        public const int MaxZoneRectsPerZone = 16;

        /// <summary>Máximo total de rectángulos anunciados en ZoneList (I-15).</summary>
        public const int MaxZoneRects = 128;

        /// <summary>Máximo de eventos de combate en un mensaje (I-13).</summary>
        public const int MaxCombatEventsPerMessage = 64;

        /// <summary>Máximo de objetos que informa un mensaje LootGained (I-25).</summary>
        public const int MaxLootItemsPerMessage = 32;

        /// <summary>Máximo de oro que puede tener un personaje (I-25).</summary>
        public const uint MaxGold = 1_000_000_000;

        /// <summary>Máximo de personajes que puede transportar una lista por cuenta (I-10).</summary>
        public const int MaxCharactersPerAccount = 10;

        /// <summary>Rango máximo representable en los dos bits asignados en el snapshot.</summary>
        public const byte MaxEntityRank = 3;

        /// <summary>Número de huecos de equipo definidos por el protocolo.</summary>
        public const int EquipSlotCount = 10;

        /// <summary>Máximo de huecos de inventario que admite el protocolo.</summary>
        public const int MaxInventorySlots = 60;

        /// <summary>Destino de desequipar que selecciona el primer hueco libre.</summary>
        public const byte FirstFreeInventorySlot = byte.MaxValue;

        /// <summary>Máximo de bytes UTF-8 del texto de chat (I-07).</summary>
        public const int MaxChatBytes = 120;

        /// <summary>Máximo de bytes UTF-8 del nombre del emisor en chat (I-07).</summary>
        public const int MaxChatNameBytes = 24;

        /// <summary>
        /// Tope de inputs que puede llevar un <c>MoveInput</c>: el más nuevo y hasta tres copias
        /// redundantes de los anteriores (8 a 14 bytes).
        /// </summary>
        public const int MaxInputsPerMessage = 4;

        /// <summary>
        /// Tamaño máximo de un mensaje de aplicación. LiteNetLib ya limita el paquete UDP, pero un
        /// tope propio impide que un cliente hostil reserve memoria de más (plan del servidor §7).
        /// </summary>
        public const int MaxMessageSize = 1400;

        /// <summary>Tamaño de la huella SHA-256 del mapa.</summary>
        public const int MapHashSize = 32;

        /// <summary>Tope de bytes UTF-8 del nombre de cuenta que viaja en el saludo.</summary>
        public const int MaxAccountNameBytes = 32;

        /// <summary>Tope de bytes UTF-8 de la contraseña que viaja en el saludo.</summary>
        public const int MaxPasswordBytes = 128;

        /// <summary>Máximo de bytes UTF-8 del nombre de personaje (I-10).</summary>
        public const int MaxCharacterNameBytes = 32;

        /// <summary>Máximo de habilidades por clase (I-16).</summary>
        public const int MaxSkillsPerClass = 16;

        /// <summary>Máximo de bytes UTF-8 del nombre de habilidad (I-16).</summary>
        public const int MaxSkillNameBytes = 32;

        /// <summary>Tope de bytes UTF-8 del código de invitación que viaja en el saludo.</summary>
        public const int MaxInvitationCodeBytes = 64;

        /// <summary>
        /// Unidades de posición por tile en el snapshot. Las posiciones viajan como <c>short</c>:
        /// 1/64 de tile basta para dibujar y ocupa la mitad que un <c>float</c> (guía §4.4).
        /// </summary>
        public const int PositionScale = 64;

        /// <summary>
        /// Mayor coordenada representable en un snapshot: <c>short.MaxValue / PositionScale</c>
        /// (≈ 512 tiles). Un mapa puede ser mucho mayor (hasta 4096×4096), así que una entidad
        /// fuera de ese radio se satura en el borde. No es un problema hoy (19×13) y dejará de
        /// serlo con el área de interés de la Etapa E, que solo manda lo que está cerca.
        /// </summary>
        public const float MaxPositionTiles = short.MaxValue / (float)PositionScale;

        /// <summary>
        /// Error máximo que introduce cuantizar una posición: medio paso de la rejilla
        /// (1/128 de tile ≈ 8 mm). La reconciliación de la Etapa D no debe corregir por debajo de
        /// este margen o el jugador vibraría con cada snapshot.
        /// </summary>
        public const float MaxQuantizationError = 0.5f / PositionScale;

        /// <summary>
        /// Tope de entidades en un snapshot. El paquete va por el canal secuenciado, que LiteNetLib
        /// no fragmenta: su máximo es 1020 B. La cabecera ocupa 11 B y cada entidad 15 B (I-03c),
        /// así que 11 + 67·15 = 1016 B cabe y 11 + 68·15 = 1031 B no (lanzaría
        /// TooBigPacketException en el hilo de red).
        /// </summary>
        public const int MaxEntitiesPerSnapshot = 67;

        /// <summary>Puerto UDP por defecto.</summary>
        public const int DefaultPort = 7777;

        /// <summary>
        /// Convierte una coordenada de mundo a la unidad del snapshot, saturando en vez de dar la
        /// vuelta: una posición absurda se recorta, nunca se convierte en otra válida al azar.
        /// </summary>
        public static short QuantizePosition(float value)
        {
            if (float.IsNaN(value))
            {
                return 0;
            }

            float scaled = MathF.Round(value * PositionScale, MidpointRounding.AwayFromZero);

            if (scaled >= short.MaxValue)
            {
                return short.MaxValue;
            }

            if (scaled <= short.MinValue)
            {
                return short.MinValue;
            }

            return (short)scaled;
        }

        /// <summary>Deshace <see cref="QuantizePosition"/>.</summary>
        public static float DequantizePosition(short value) => value / (float)PositionScale;
    }
}
