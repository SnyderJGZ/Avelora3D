#nullable enable
using System;
using System.Collections.Generic;
using Avelora.Server.Entities;
using Avelora.Shared;

namespace Avelora.Server.Simulation
{
    /// <summary>
    /// Una zona: el mundo simulado (guía del núcleo §6). Hoy es un mapa, sus entidades y el área
    /// de interés; mañana añadirá enemigos y demás sin cambiar el tick.
    /// </summary>
    /// <remarks>
    /// El tick de la zona tiene tres partes y las tres importan:
    /// <list type="number">
    /// <item><b>Leer</b> lo que ha llegado por la cola (sin bloqueos: la red ya ha hecho su parte).</item>
    /// <item><b>Simular</b> un paso fijo con las reglas del servidor.</item>
    /// <item><b>Enviar</b> el estado — llega en la Etapa C, pero el hueco ya está reservado aquí:
    /// preparar y encolar los snapshots no cambiará la forma de este método.</item>
    /// </list>
    ///
    /// Todo lo de dentro corre en el hilo de simulación, y solo en él (guía del núcleo §2.1): por
    /// eso no hay ningún <c>lock</c>.
    /// </remarks>
    public sealed class Zone
    {
        private readonly MovementSystem _movement;
        private readonly InterestSystem _interest;

        // Buffers reutilizados en cada tick: reservar memoria dentro del bucle acabaría provocando
        // recolecciones en mal momento (plan del servidor §9).
        private readonly List<MoveIntent> _inbox = new List<MoveIntent>(256);
        private readonly Dictionary<int, int> _acceptedPerPlayer = new Dictionary<int, int>();

        private readonly int _maxIntentsPerTick;
        private readonly int _maxIntentsPerPlayerPerTick;

        private long _tickCount;
        private PlayerInfo[] _players = Array.Empty<PlayerInfo>();

        public Zone(
            int id,
            MapCollisionGrid map,
            EntityStore entities,
            EntityIdAllocator ids,
            InputQueue inputs,
            MovementSystem movement,
            int maxIntentsPerTick,
            int maxIntentsPerPlayerPerTick,
            InterestSettings? interest = null)
        {
            if (map is null)
            {
                throw new ArgumentNullException(nameof(map));
            }

            if (entities is null)
            {
                throw new ArgumentNullException(nameof(entities));
            }

            if (ids is null)
            {
                throw new ArgumentNullException(nameof(ids));
            }

            if (inputs is null)
            {
                throw new ArgumentNullException(nameof(inputs));
            }

            if (movement is null)
            {
                throw new ArgumentNullException(nameof(movement));
            }

            if (maxIntentsPerTick < 1 || maxIntentsPerPlayerPerTick < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxIntentsPerTick),
                    "Los topes de intenciones por tick deben ser >= 1.");
            }

            Id = id;
            Map = map;
            Entities = entities;
            Ids = ids;
            Inputs = inputs;
            _movement = movement;

            InterestSettings settings = interest ?? InterestSettings.Default;
            _interest = new InterestSystem(new EntitySpatialGrid(settings.CellSize), settings);

            _maxIntentsPerTick = maxIntentsPerTick;
            _maxIntentsPerPlayerPerTick = maxIntentsPerPlayerPerTick;
        }

        /// <summary>Identificador de la zona.</summary>
        public int Id { get; }

        /// <summary>Rejilla de colisión compartida con el cliente.</summary>
        public MapCollisionGrid Map { get; }

        /// <summary>Entidades vivas.</summary>
        public EntityStore Entities { get; }

        /// <summary>Reparte identificadores de entidad.</summary>
        public EntityIdAllocator Ids { get; }

        /// <summary>Cola de entrada compartida con el hilo de red.</summary>
        public InputQueue Inputs { get; }

        /// <summary>Parámetros del área de interés efectivos en esta zona.</summary>
        public InterestSettings Interest => _interest.Settings;

        /// <summary>Ticks completados (seguro de leer desde otros hilos).</summary>
        public long TickCount => System.Threading.Interlocked.Read(ref _tickCount);

        /// <summary>
        /// Copia inmutable de los jugadores tal como estaban al final del último tick. Es lo que
        /// pueden leer los informes desde otro hilo sin tocar el almacén de entidades.
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<PlayerInfo> Players =>
            System.Threading.Volatile.Read(ref _players);

        /// <summary>Inputs descartados por llegar con secuencia repetida o vieja.</summary>
        public long RejectedOutOfOrder { get; private set; }

        /// <summary>Inputs descartados porque la cola del jugador estaba llena.</summary>
        public long RejectedBufferFull { get; private set; }

        /// <summary>Inputs descartados por el tope de intenciones por jugador y tick.</summary>
        public long RejectedPerPlayerLimit { get; private set; }

        /// <summary>Inputs descartados porque no existe la entidad a la que apuntan.</summary>
        public long RejectedUnknownEntity { get; private set; }

        /// <summary>Añade un jugador a la zona y devuelve su entidad.</summary>
        /// <exception cref="InvalidOperationException">
        /// La caja de la entidad no cabe en <paramref name="position"/>: nacería dentro de una pared.
        /// </exception>
        public PlayerEntity SpawnPlayer(int characterId, Vec2 position, Vec2 halfSize, int inputBufferCapacity)
        {
            // Invariante de la zona: una entidad nunca aparece solapando una pared. Step rechaza
            // cualquier movimiento hacia una posición no caminable, así que nacer atrapado no
            // tendría salida. Program ya valida el spawn al arrancar; esto cierra la puerta para
            // cualquier otra vía de creación (pruebas, futuras reapariciones, otro anfitrión).
            if (!Map.IsBoxWalkable(position, halfSize))
            {
                throw new InvalidOperationException(
                    "No se puede crear una entidad en " + position + ": su caja se solapa con una pared.");
            }

            var player = new PlayerEntity(Ids.Allocate(), characterId, inputBufferCapacity)
            {
                MapId = Id,
                Position = position,
                HalfSize = halfSize,
            };

            Entities.Add(player);
            _interest.Add(player);
            return player;
        }

        /// <summary>
        /// Añade una entidad ya construida (PNJ, ítem en el suelo o jugador de prueba) y la indexa
        /// para el área de interés. La creación de jugadores debe pasar por <see cref="SpawnPlayer"/>,
        /// que además valida que la caja no nazca dentro de una pared.
        /// </summary>
        public void AddEntity(Entity entity)
        {
            if (entity is null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            entity.MapId = Id;
            Entities.Add(entity);
            _interest.Add(entity);
        }

        /// <summary>Quita una entidad del almacén y de la grilla espacial.</summary>
        public bool RemoveEntity(int id)
        {
            if (!Entities.TryGet(id, out Entity? entity))
            {
                return false;
            }

            _interest.Remove(entity);
            return Entities.Remove(id);
        }

        /// <summary>
        /// Escribe las entidades que ve <paramref name="viewer"/>: el jugador local siempre, más
        /// las que estén dentro de su área de interés. La lista se reutiliza entre snapshots.
        /// </summary>
        public void CollectVisibleEntities(PlayerEntity viewer, List<Entity> results) =>
            _interest.CollectVisible(viewer, results);

        /// <summary>Un tick: leer intenciones, simular el mundo y publicar el estado.</summary>
        public void Tick()
        {
            DrainInputs();
            _movement.Tick(Entities, Map);

            // Las posiciones ya son las del tick: se reindexa lo que haya cambiado de celda. Es
            // O(n) con n = entidades vivas y deja la grilla lista para los snapshots de AfterTick.
            for (int i = 0; i < Entities.Count; i++)
            {
                _interest.Update(Entities[i]);
            }

            // 3. Publicar el estado: la red (ITickObserver.AfterTick) lee esta copia y encola los
            //    snapshots. El tick no cambia de forma cuando cambie el transporte.
            PublishPlayers();

            System.Threading.Interlocked.Increment(ref _tickCount);
        }

        /// <summary>
        /// Toma una foto de los jugadores para los informes. Se reserva una vez por tick y se
        /// publica de golpe, así nadie ve media lista.
        /// </summary>
        private void PublishPlayers()
        {
            int count = 0;
            for (int i = 0; i < Entities.Count; i++)
            {
                if (Entities[i] is PlayerEntity)
                {
                    count++;
                }
            }

            if (count == 0)
            {
                System.Threading.Volatile.Write(ref _players, Array.Empty<PlayerInfo>());
                return;
            }

            var players = new PlayerInfo[count];
            int at = 0;

            for (int i = 0; i < Entities.Count; i++)
            {
                if (Entities[i] is PlayerEntity player)
                {
                    players[at++] = new PlayerInfo(
                        player.Id,
                        player.CharacterId,
                        player.ConnectionId,
                        player.Position.X,
                        player.Position.Y,
                        player.DirectionX,
                        player.DirectionY,
                        player.LastProcessedSequence);
                }
            }

            System.Threading.Volatile.Write(ref _players, players);
        }

        /// <summary>
        /// Pasa las intenciones de la cola concurrente a la cola de cada jugador, aplicando los
        /// topes. La validación de dirección se hizo al encolar (frontera de red).
        /// </summary>
        private void DrainInputs()
        {
            _acceptedPerPlayer.Clear();
            Inputs.DrainInto(_inbox, _maxIntentsPerTick);

            for (int i = 0; i < _inbox.Count; i++)
            {
                MoveIntent intent = _inbox[i];

                if (!Entities.TryGet(intent.EntityId, out Entity? entity) || !(entity is PlayerEntity player))
                {
                    RejectedUnknownEntity++;
                    continue;
                }

                _acceptedPerPlayer.TryGetValue(intent.EntityId, out int alreadyAccepted);
                if (alreadyAccepted >= _maxIntentsPerPlayerPerTick)
                {
                    RejectedPerPlayerLimit++;
                    continue;
                }

                _acceptedPerPlayer[intent.EntityId] = alreadyAccepted + 1;

                InputAcceptResult result = player.Input.TryAccept(intent);
                switch (result)
                {
                    case InputAcceptResult.Accepted:
                        break;
                    case InputAcceptResult.OutOfOrder:
                        RejectedOutOfOrder++;
                        break;
                    case InputAcceptResult.BufferFull:
                        RejectedBufferFull++;
                        break;
                }
            }
        }
    }
}
