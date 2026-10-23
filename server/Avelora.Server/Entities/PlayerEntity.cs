#nullable enable
using System.Collections.Generic;
using Avelora.Server.Simulation;
using Avelora.Shared;

namespace Avelora.Server.Entities
{
    /// <summary>
    /// Jugador conectado (o simulado por código, como en la Etapa B) — guía del núcleo §3.2.
    /// </summary>
    /// <remarks>
    /// Además de los datos de <see cref="Entity"/>, guarda lo propio de una sesión: a qué personaje
    /// y a qué conexión pertenece, cuál fue el último input aplicado (la base de la reconciliación
    /// de la Etapa D) y su cola de inputs.
    ///
    /// La cola vive aquí, y no en un diccionario aparte, para que el tick la tenga a mano sin
    /// ninguna búsqueda: el bucle se recorre por índice y cada jugador ya trae lo que necesita.
    /// </remarks>
    public sealed class PlayerEntity : Entity
    {
        public PlayerEntity(int id, int characterId, int inputBufferCapacity)
            : base(id, EntityKind.Player)
        {
            CharacterId = characterId;
            Input = new PlayerInputBuffer(inputBufferCapacity);
        }

        /// <summary>Identificador del personaje (en la Etapa F vendrá de la base de datos).</summary>
        public int CharacterId { get; }

        /// <summary>Conexión asociada. <c>-1</c> significa «sin red»: un jugador simulado por código.</summary>
        public int ConnectionId { get; set; } = -1;

        /// <summary>
        /// Último input aplicado. Se enviará al cliente en cada snapshot para que sepa desde dónde
        /// volver a simular lo que aún no ha confirmado el servidor (guía del núcleo §5.3).
        /// </summary>
        public uint LastProcessedSequence { get; set; }

        /// <summary>Inputs recibidos por la red que esperan a ser aplicados, uno por tick.</summary>
        public PlayerInputBuffer Input { get; }

        /// <summary>
        /// Entidades que este jugador recibió en el último snapshot. La usa el área de interés
        /// para conservar un margen antes de retirar una entidad del borde (histéresis).
        /// </summary>
        public HashSet<int> VisibleEntityIds { get; } = new HashSet<int>();

        /// <summary>
        /// Conjunto de trabajo del próximo snapshot. Se reutiliza para no reservar memoria en el
        /// tick; lo rellena y lo intercambia el <see cref="Simulation.InterestSystem"/>.
        /// </summary>
        internal HashSet<int> NextVisibleEntityIds { get; } = new HashSet<int>();
    }
}
