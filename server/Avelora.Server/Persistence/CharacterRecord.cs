#nullable enable
using System;
using Avelora.Shared;
using Avelora.Shared.Protocol;

namespace Avelora.Server.Persistence
{
    /// <summary>
    /// Foto inmutable de un personaje en el momento de guardarlo (plan del servidor §8).
    /// </summary>
    /// <remarks>
    /// La cola de guardado se queda con esta copia, no con la entidad viva: la simulación puede
    /// seguir moviendo al jugador mientras SQLite escribe, sin carreras ni bloqueos dentro del tick.
    /// </remarks>
    public sealed class CharacterRecord
    {
        public CharacterRecord(
            int id,
            int accountId,
            string name,
            int level,
            int experience,
            int health,
            int mana,
            int zoneId,
            Vec2 position,
            byte[] mapHash,
            DateTime createdUtc,
            DateTime updatedUtc)
        {
            if (id <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(id), "El personaje debe tener un Id positivo.");
            }

            if (accountId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(accountId), "La cuenta debe tener un Id positivo.");
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("El personaje necesita un nombre.", nameof(name));
            }

            if (mapHash is null || mapHash.Length != ProtocolConstants.MapHashSize)
            {
                throw new ArgumentException(
                    "La huella del mapa debe tener " + ProtocolConstants.MapHashSize + " bytes.",
                    nameof(mapHash));
            }

            Id = id;
            AccountId = accountId;
            Name = name;
            Level = level;
            Experience = experience;
            Health = health;
            Mana = mana;
            ZoneId = zoneId;
            Position = position;
            MapHash = (byte[])mapHash.Clone();
            CreatedUtc = createdUtc;
            UpdatedUtc = updatedUtc;
        }

        public int Id { get; }

        public int AccountId { get; }

        public string Name { get; }

        public int Level { get; }

        public int Experience { get; }

        public int Health { get; }

        public int Mana { get; }

        public int ZoneId { get; }

        public Vec2 Position { get; }

        /// <summary>Mapa en el que se guardó; si el servidor carga otro, la posición no se reutiliza.</summary>
        public byte[] MapHash { get; }

        public DateTime CreatedUtc { get; }

        public DateTime UpdatedUtc { get; }

        /// <summary>Copia el personaje con una posición, zona y mapa nuevos, dejando el resto igual.</summary>
        public CharacterRecord WithPosition(int zoneId, Vec2 position, byte[] mapHash) =>
            new CharacterRecord(
                Id,
                AccountId,
                Name,
                Level,
                Experience,
                Health,
                Mana,
                zoneId,
                position,
                mapHash,
                CreatedUtc,
                DateTime.UtcNow);

        /// <summary>
        /// Copia el personaje con posición, zona, mapa y vida nuevos (I-03). La usa el guardado
        /// para que la vida viva llegue a SQLite y para normalizar a un jugador muerto.
        /// </summary>
        public CharacterRecord WithState(int zoneId, Vec2 position, byte[] mapHash, int health) =>
            new CharacterRecord(
                Id,
                AccountId,
                Name,
                Level,
                Experience,
                health,
                Mana,
                zoneId,
                position,
                mapHash,
                CreatedUtc,
                DateTime.UtcNow);
    }
}
