#nullable enable
using System;
using System.Collections.Generic;
using Avelora.Server.Entities;
using Avelora.Server.Npcs;
using Avelora.Shared;

namespace Avelora.Server.Simulation
{
    /// <summary>
    /// Una zona: el mundo simulado (guía del núcleo §6). Hoy es un mapa, sus entidades (jugadores
    /// y enemigos), el combate y el área de interés.
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
    /// El orden dentro de la simulación es fijo (I-02): mover jugadores, resolver sus ataques y
    /// después mover a los enemigos. Así un golpe se aplica sobre la posición que el jugador acaba
    /// de alcanzar y una muerte detiene al enemigo en el mismo tick.
    ///
    /// Todo lo de dentro corre en el hilo de simulación, y solo en él (guía del núcleo §2.1): por
    /// eso no hay ningún <c>lock</c>.
    /// </remarks>
    public sealed class Zone
    {
        private readonly MovementSystem _movement;
        private readonly InterestSystem _interest;
        private readonly NpcSystem? _npcs;
        private readonly CombatSystem? _combat;

        // Buffers reutilizados en cada tick: reservar memoria dentro del bucle acabaría provocando
        // recolecciones en mal momento (plan del servidor §9).
        private readonly List<MoveIntent> _inbox = new List<MoveIntent>(256);
        private readonly List<AttackIntent> _attackInbox = new List<AttackIntent>(256);
        private readonly List<NpcEntity> _deadNpcs = new List<NpcEntity>(16);
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
            InterestSettings? interest = null,
            NpcCatalog? npcs = null,
            int npcSeed = 12345,
            AttackQueue? attacks = null,
            CombatSettings? combat = null)
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

            // Ataques y reglas de combate van juntos: con uno sin el otro la cola se llenaría sin
            // que nadie la vaciara. Program pasa ambos; las zonas sin combate no pasan ninguno.
            if ((attacks == null) != (combat == null))
            {
                throw new ArgumentException(
                    "La cola de ataques y las reglas de combate se configuran juntas o no se configuran.");
            }

            Id = id;
            Map = map;
            Entities = entities;
            Ids = ids;
            Inputs = inputs;
            Attacks = attacks;
            _movement = movement;

            if (attacks != null && combat.HasValue)
            {
                _combat = new CombatSystem(combat.Value);
            }

            InterestSettings settings = interest ?? InterestSettings.Default;
            _interest = new InterestSystem(new EntitySpatialGrid(settings.CellSize), settings);

            _maxIntentsPerTick = maxIntentsPerTick;
            _maxIntentsPerPlayerPerTick = maxIntentsPerPlayerPerTick;

            // Enemigos (I-01): se colocan al nacer la zona, antes de que haya red. Si no hay datos
            // o no hay spawners, la zona se queda sin PNJ y el tick no gana ni una rama.
            if (npcs != null && npcs.Spawners != null && npcs.Spawners.Count > 0)
            {
                _npcs = new NpcSystem(map, npcSeed);
                _npcs.Spawn(npcs, Id, Entities, Ids);

                for (int i = 0; i < Entities.Count; i++)
                {
                    _interest.Add(Entities[i]);
                }
            }
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

        /// <summary>
        /// Cola de ataques compartida con el hilo de red, o <c>null</c> si la zona no tiene combate
        /// (por ejemplo, en pruebas de movimiento).
        /// </summary>
        public AttackQueue? Attacks { get; }

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

        /// <summary>
        /// Copias redundantes que el búfer ya tenía: llegaron antes en otro mensaje (G-03). Se
        /// cuentan aparte para que <see cref="RejectedOutOfOrder"/> siga midiendo lo de siempre.
        /// </summary>
        public long RedundantDuplicates { get; private set; }

        /// <summary>Inputs descartados porque la cola del jugador estaba llena.</summary>
        public long RejectedBufferFull { get; private set; }

        /// <summary>Inputs descartados por el tope de intenciones por jugador y tick.</summary>
        public long RejectedPerPlayerLimit { get; private set; }

        /// <summary>Inputs descartados porque no existe la entidad a la que apuntan.</summary>
        public long RejectedUnknownEntity { get; private set; }

        /// <summary>Ataques válidos desde el arranque (I-02).</summary>
        public long AttacksOk => _combat?.AttacksOk ?? 0;

        /// <summary>Ataques rechazados porque el objetivo no existía, no era un PNJ o estaba muerto.</summary>
        public long RejectedAttackTarget => _combat?.RejectedTarget ?? 0;

        /// <summary>Ataques rechazados porque el objetivo estaba fuera de alcance.</summary>
        public long RejectedAttackDistance => _combat?.RejectedDistance ?? 0;

        /// <summary>Ataques rechazados porque el atacante aún no había recargado.</summary>
        public long RejectedAttackCooldown => _combat?.RejectedCooldown ?? 0;

        /// <summary>Enemigos muertos desde el arranque (I-02).</summary>
        public long NpcDeaths => _npcs?.Deaths ?? 0;

        /// <summary>Enemigos que han reaparecido desde el arranque (I-02).</summary>
        public long NpcRespawns => _npcs?.Respawns ?? 0;

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

            // Combate (I-02): después de mover a los jugadores (el ataque usa su posición nueva y
            // la del enemigo antes de que se mueva) y antes de la IA (un enemigo muerto no da un
            // paso más en este tick).
            if (_combat != null)
            {
                Attacks!.DrainInto(_attackInbox);
                _combat.Tick(Entities, _attackInbox, _tickCount);
            }

            // Los enemigos se mueven después de los jugadores y antes de reindexar: así el área de
            // interés y los snapshots de este mismo tick ya ven su posición nueva.
            if (_npcs != null)
            {
                _npcs.Tick(Entities, _deadNpcs);

                // Los cadáveres que ya cumplieron su animación salen aquí, con el almacén y la
                // grilla todavía en el hilo de simulación y antes de reindexar.
                for (int i = 0; i < _deadNpcs.Count; i++)
                {
                    RemoveEntity(_deadNpcs[i].Id);
                }
            }

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

                // Las copias redundantes son lo normal desde G-03: el input ya llegó en un mensaje
                // anterior. Se clasifican antes del tope porque no gastan una plaza: el tope cuenta
                // solo las intenciones que el búfer acepta de verdad.
                if (!SequenceMath.IsNewer(intent.Sequence, player.Input.LastAcceptedSequence))
                {
                    if (intent.IsRedundant)
                    {
                        RedundantDuplicates++;
                    }
                    else
                    {
                        RejectedOutOfOrder++;
                    }

                    continue;
                }

                _acceptedPerPlayer.TryGetValue(intent.EntityId, out int alreadyAccepted);
                if (alreadyAccepted >= _maxIntentsPerPlayerPerTick)
                {
                    RejectedPerPlayerLimit++;
                    continue;
                }

                InputAcceptResult result = player.Input.TryAccept(intent);
                switch (result)
                {
                    case InputAcceptResult.Accepted:
                        _acceptedPerPlayer[intent.EntityId] = alreadyAccepted + 1;
                        break;
                    case InputAcceptResult.OutOfOrder:
                        // No puede pasar (ya se comprobó), pero el interruptor cubre el futuro.
                        if (intent.IsRedundant)
                        {
                            RedundantDuplicates++;
                        }
                        else
                        {
                            RejectedOutOfOrder++;
                        }

                        break;
                    case InputAcceptResult.BufferFull:
                        RejectedBufferFull++;
                        break;
                }
            }
        }
    }
}
