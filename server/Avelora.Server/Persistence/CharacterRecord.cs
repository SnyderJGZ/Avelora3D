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
    public sealed record CharacterRecord
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
            DateTime updatedUtc,
            InventoryItemRecord[]? inventoryItems = null,
            int classId = 1,
            int allocStr = 0,
            int allocDex = 0,
            int allocInt = 0,
            int allocVit = 0,
            ushort[]? equipment = null)
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
            InventoryItems = inventoryItems == null
                ? Array.Empty<InventoryItemRecord>()
                : (InventoryItemRecord[])inventoryItems.Clone();
            Equipment = equipment == null
                ? new ushort[ProtocolConstants.EquipSlotCount]
                : (ushort[])equipment.Clone();
            if (Equipment.Length != ProtocolConstants.EquipSlotCount)
            {
                throw new ArgumentException("El equipo debe contener exactamente diez huecos.", nameof(equipment));
            }

            ClassId = classId;
            AllocStr = allocStr;
            AllocDex = allocDex;
            AllocInt = allocInt;
            AllocVit = allocVit;
        }

        public int Id { get; init; }

        public int AccountId { get; init; }

        public string Name { get; init; }

        public int Level { get; init; }

        public int Experience { get; init; }

        public int Health { get; init; }

        public int Mana { get; init; }

        public int ZoneId { get; init; }

        public Vec2 Position { get; init; }

        /// <summary>Mapa en el que se guardó; si el servidor carga otro, la posición no se reutiliza.</summary>
        public byte[] MapHash { get; init; }

        public DateTime CreatedUtc { get; init; }

        public DateTime UpdatedUtc { get; init; }

        /// <summary>Contenido de inventario guardado por ranura.</summary>
        public InventoryItemRecord[] InventoryItems { get; init; }

        /// <summary>Ids equipados por hueco; se copia para que la foto sea inmutable.</summary>
        public ushort[] Equipment { get; init; }

        public int ClassId { get; init; }
        public int AllocStr { get; init; }
        public int AllocDex { get; init; }
        public int AllocInt { get; init; }
        public int AllocVit { get; init; }
    }

    /// <summary>Registro inmutable de un objeto guardado en una ranura.</summary>
    public readonly struct InventoryItemRecord
    {
        public InventoryItemRecord(int slot, int itemId, int count)
        {
            Slot = slot;
            ItemId = itemId;
            Count = count;
        }

        public int Slot { get; }
        public int ItemId { get; }
        public int Count { get; }
    }
}
