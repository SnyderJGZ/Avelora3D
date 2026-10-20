#nullable enable
namespace Avelora.Server.Simulation
{
    /// <summary>
    /// Una intención de movimiento: lo único que un cliente puede pedir (guía del núcleo §4.3).
    /// </summary>
    /// <remarks>
    /// Fíjate en lo que <b>no</b> lleva: ni posición, ni velocidad, ni tiempo. El cliente solo dice
    /// «quiero ir hacia aquí» y con qué número de secuencia; el servidor decide si eso es posible y
    /// cuánto avanza. Es la traducción directa del modelo autoritativo (plan del servidor §1).
    ///
    /// Es un <c>readonly struct</c> para que encolar y sacar intenciones no genere basura.
    /// La primera secuencia de cada cliente es <b>1</b> (el 0 queda como «ninguna»).
    /// </remarks>
    public readonly struct MoveIntent
    {
        public MoveIntent(int entityId, uint sequence, sbyte directionX, sbyte directionY)
        {
            EntityId = entityId;
            Sequence = sequence;
            DirectionX = directionX;
            DirectionY = directionY;
        }

        /// <summary>Entidad que pide moverse.</summary>
        public int EntityId { get; }

        /// <summary>Número de secuencia del input, para descartar repetidos y viejos.</summary>
        public uint Sequence { get; }

        /// <summary>Dirección en X: -1, 0 o 1.</summary>
        public sbyte DirectionX { get; }

        /// <summary>Dirección en Y: -1, 0 o 1.</summary>
        public sbyte DirectionY { get; }

        public override string ToString() => "input#" + Sequence + " ent=" + EntityId + " dir=(" + DirectionX + "," + DirectionY + ")";
    }
}
