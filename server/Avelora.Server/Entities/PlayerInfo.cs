#nullable enable
namespace Avelora.Server.Entities
{
    /// <summary>
    /// Copia inmutable de un jugador para quien mire el mundo desde fuera del hilo de simulación
    /// (informes por consola, paneles). Evita que un hilo recorra la lista de entidades mientras
    /// otro la modifica.
    /// </summary>
    public readonly struct PlayerInfo
    {
        public PlayerInfo(
            int id,
            int characterId,
            int connectionId,
            float x,
            float y,
            sbyte directionX,
            sbyte directionY,
            uint lastProcessedSequence)
        {
            Id = id;
            CharacterId = characterId;
            ConnectionId = connectionId;
            X = x;
            Y = y;
            DirectionX = directionX;
            DirectionY = directionY;
            LastProcessedSequence = lastProcessedSequence;
        }

        public int Id { get; }

        public int CharacterId { get; }

        public int ConnectionId { get; }

        public float X { get; }

        public float Y { get; }

        public sbyte DirectionX { get; }

        public sbyte DirectionY { get; }

        public uint LastProcessedSequence { get; }
    }
}
