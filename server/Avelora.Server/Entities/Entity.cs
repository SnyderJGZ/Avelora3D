#nullable enable
using Avelora.Shared;

namespace Avelora.Server.Entities
{
    /// <summary>
    /// Todo lo que existe en el mundo: jugadores, y mañana enemigos o PNJ (guía del núcleo §3.2).
    /// </summary>
    /// <remarks>
    /// Es una clase (no un struct) porque las entidades se referencian desde varios sitios y
    /// porque el estado de un jugador puede crecer con las etapas. Los datos viven en el hilo de
    /// simulación y <b>solo</b> ese hilo los toca: los demás hilos se comunican por colas
    /// (guía del núcleo §2.1).
    /// </remarks>
    public class Entity
    {
        public Entity(int id, EntityKind kind)
        {
            Id = id;
            Kind = kind;
        }

        /// <summary>Identificador único dentro del servidor. Lo asigna el servidor, nunca el cliente.</summary>
        public int Id { get; }

        /// <summary>Tipo de entidad.</summary>
        public EntityKind Kind { get; }

        /// <summary>Zona en la que vive la entidad (en el MVP, la única).</summary>
        public int MapId { get; set; }

        /// <summary>Posición en unidades de mundo (1 unidad = 1 tile).</summary>
        public Vec2 Position { get; set; }

        /// <summary>Semiancho y semialto de la caja de colisión.</summary>
        public Vec2 HalfSize { get; set; }

        /// <summary>
        /// Celda de la grilla espacial, para el área de interés. La mantiene
        /// <see cref="EntitySpatialGrid"/>; <see cref="NoCell"/> significa «sin indexar».
        /// </summary>
        public int CellX { get; set; } = NoCell;

        /// <inheritdoc cref="CellX"/>
        public int CellY { get; set; } = NoCell;

        /// <summary>Valor de <see cref="CellX"/> y <see cref="CellY"/> cuando la entidad no está indexada.</summary>
        public const int NoCell = int.MinValue;

        public override string ToString() =>
            "Entidad#" + Id + " " + Kind + " en " + Position;
    }
}
