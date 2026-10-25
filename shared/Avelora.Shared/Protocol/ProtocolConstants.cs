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
        /// I-06 a 9 por el mensaje <c>Progress</c>.
        /// </summary>
        public const ushort Version = 9;

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

        /// <summary>Tope de bytes UTF-8 del nombre de personaje que viaja en el saludo.</summary>
        public const int MaxCharacterNameBytes = 32;

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
