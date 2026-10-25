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
        public PlayerEntity(int id, int characterId, int inputBufferCapacity, int inventorySlots = 20)
            : base(id, EntityKind.Player)
        {
            CharacterId = characterId;
            Input = new PlayerInputBuffer(inputBufferCapacity);
            Inventory = new InventorySlot[inventorySlots];
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

        /// <summary>Huecos de inventario, modificados solo por el hilo de simulación.</summary>
        public InventorySlot[] Inventory { get; }

        /// <summary>Identificador del arma equipada; 0 significa combatir con los puños.</summary>
        public ushort EquippedWeaponId { get; set; }

        /// <summary>Tick del último uso válido de poción; -1 si todavía no ha usado ninguna.</summary>
        public long LastPotionTick { get; set; } = -1;

        /// <summary>
        /// Tick del último ataque <b>válido</b> (I-02); <c>-1</c> si aún no ha atacado. La recarga
        /// se mide contra este valor, siempre en ticks de la zona.
        /// </summary>
        public long LastAttackTick { get; set; } = -1;

        /// <summary>Vida máxima por defecto cuando la zona no configura reglas de vida (I-03).</summary>
        public const int DefaultMaxHealth = 100;

        /// <summary>Vida máxima del jugador. La fija la zona al crearlo (I-03).</summary>
        public int MaxHealth { get; set; } = DefaultMaxHealth;

        /// <summary>Vida actual. Nunca baja de 0 (I-03).</summary>
        public int Health { get; set; } = DefaultMaxHealth;

        /// <summary>
        /// Tick del último daño recibido; <c>-1</c> si aún no le han pegado. Marca cuándo puede
        /// empezar la regeneración.
        /// </summary>
        public long LastDamageTick { get; set; } = -1;

        /// <summary>Tick en el que murió; <c>-1</c> mientras está vivo.</summary>
        public long DeathTick { get; set; } = -1;

        /// <summary>Si el jugador está muerto (el snapshot manda <see cref="EntityAction.Dead"/>).</summary>
        public bool IsDead => Action == EntityAction.Dead;

        /// <summary>Acumulador fraccionario de la regeneración por ticks.</summary>
        public double RegenAccumulator { get; set; }

        /// <summary>Si la muerte de este jugador ya se contó (una sola vez).</summary>
        internal bool DeathCounted { get; set; }

        /// <summary>
        /// Vida restante en porcentaje, con la misma regla que los PNJ: redondeo hacia arriba y 0
        /// solo si está muerto (I-03).
        /// </summary>
        public byte HealthPercent => EntityHealth.Percent(Health, MaxHealth);

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
