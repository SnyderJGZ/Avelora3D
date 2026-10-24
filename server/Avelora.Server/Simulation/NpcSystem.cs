#nullable enable
using System;
using System.Collections.Generic;
using Avelora.Server.Entities;
using Avelora.Server.Npcs;
using Avelora.Shared;

namespace Avelora.Server.Simulation
{
    /// <summary>
    /// IA de los enemigos: deambular, muerte y reaparición (I-01 e I-02).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deambular: dirección aleatoria cada pocos ticks, con vuelta a casa cuando se alejan
    /// demasiado, y movimiento con el mismo <see cref="MovementSimulator"/> que usan los jugadores.
    /// </para>
    /// <para>
    /// Un único <see cref="System.Random"/>, sembrado con <c>ServerConfig.NpcSeed</c>: con la misma
    /// semilla y los mismos datos, dos zonas producen exactamente las mismas posiciones y las mismas
    /// reapariciones. Por eso no se usa <see cref="Random.Shared"/> ni el reloj.
    /// </para>
    /// <para>
    /// Solo se toca desde el hilo de simulación, dentro de <see cref="Zone.Tick"/>, justo después de
    /// mover a los jugadores y del combate, y antes de reindexar el área de interés. No reserva
    /// memoria por tick.
    /// </para>
    /// </remarks>
    public sealed class NpcSystem
    {
        /// <summary>Ticks mínimos antes de volver a elegir dirección.</summary>
        public const int MinChangeTicks = 16;

        /// <summary>Ticks máximos antes de volver a elegir dirección.</summary>
        public const int MaxChangeTicks = 50;

        // Las 8 direcciones discretas, en el mismo formato que el input de un jugador.
        private static readonly sbyte[] DirectionX = { 0, 1, 1, 1, 0, -1, -1, -1 };
        private static readonly sbyte[] DirectionY = { 1, 1, 0, -1, -1, -1, 0, 1 };

        private readonly MapCollisionGrid _map;
        private readonly System.Random _random;
        private long _tick;

        private SpawnerState[] _spawners = Array.Empty<SpawnerState>();
        private EntityIdAllocator? _ids;
        private int _zoneId;

        private long _deaths;
        private long _respawns;

        public NpcSystem(MapCollisionGrid map, int seed)
        {
            _map = map ?? throw new ArgumentNullException(nameof(map));
            _random = new System.Random(seed);
        }

        /// <summary>Ticks de IA ejecutados. Es el reloj con el que se programan cambios y reapariciones.</summary>
        public long TickCount => _tick;

        /// <summary>Enemigos que han muerto desde el arranque.</summary>
        public long Deaths => _deaths;

        /// <summary>Enemigos que han reaparecido desde el arranque.</summary>
        public long Respawns => _respawns;

        /// <summary>
        /// Coloca los enemigos de <paramref name="catalog"/> en el almacén, con Ids nuevos de
        /// <paramref name="ids"/>. Todos los de un spawner nacen en su punto: no chocan entre sí.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Un spawner nace dentro de una pared o apunta a un tipo inexistente. La carga ya lo
        /// valida; esto cierra la puerta a otras vías de creación (pruebas, futuras zonas).
        /// </exception>
        public void Spawn(NpcCatalog catalog, int zoneId, EntityStore entities, EntityIdAllocator ids)
        {
            if (catalog is null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            if (entities is null)
            {
                throw new ArgumentNullException(nameof(entities));
            }

            if (ids is null)
            {
                throw new ArgumentNullException(nameof(ids));
            }

            _ids = ids;
            _zoneId = zoneId;
            _spawners = new SpawnerState[catalog.Spawners.Count];

            for (int s = 0; s < catalog.Spawners.Count; s++)
            {
                NpcSpawner spawner = catalog.Spawners[s];
                NpcType? type = catalog.FindType(spawner.TypeId);

                if (type == null)
                {
                    throw new InvalidOperationException(
                        "El spawner " + (s + 1) + " apunta al tipo " + spawner.TypeId + ", que no existe.");
                }

                if (!_map.IsBoxWalkable(spawner.Position, type.HalfSize))
                {
                    throw new InvalidOperationException(
                        "El spawner " + (s + 1) + " nace en " + spawner.Position +
                        ", dentro de una pared: la caja del tipo " + type.Id + " no cabe.");
                }

                var state = new SpawnerState(spawner, type) { Alive = spawner.Count };
                _spawners[s] = state;

                for (int i = 0; i < spawner.Count; i++)
                {
                    entities.Add(CreateNpc(state, s));
                }
            }
        }

        /// <summary>
        /// Avanza un paso de IA y movimiento a cada enemigo vivo, repone los que tocan y anota en
        /// <paramref name="removed"/> los cadáveres que ya han cumplido su tiempo.
        /// </summary>
        /// <remarks>
        /// Este método no toca el almacén para quitar: la zona es quien retira del almacén y del
        /// área de interés, en el mismo tick y antes de reindexar (así el snapshot ya no los ve).
        /// </remarks>
        public void Tick(EntityStore entities, List<NpcEntity> removed)
        {
            if (entities is null)
            {
                throw new ArgumentNullException(nameof(entities));
            }

            if (removed is null)
            {
                throw new ArgumentNullException(nameof(removed));
            }

            removed.Clear();

            // Las reapariciones se crean antes de recorrer: entran este mismo tick y la IA las
            // tratará como a cualquier otro enemigo.
            RespawnDue(entities);

            for (int i = 0; i < entities.Count; i++)
            {
                if (!(entities[i] is NpcEntity npc))
                {
                    continue;
                }

                if (npc.Action == EntityAction.Dead)
                {
                    if (!npc.DeathScheduled)
                    {
                        RegisterDeath(npc);
                    }

                    if (_tick - npc.DeathTick >= NpcEntity.DeathAnimationTicks)
                    {
                        removed.Add(npc);
                    }

                    continue;
                }

                Step(npc);
            }

            _tick++;
        }

        /// <summary>Anota la muerte una sola vez y programa la reaparición de su spawner.</summary>
        private void RegisterDeath(NpcEntity npc)
        {
            npc.DeathScheduled = true;
            npc.DeathTick = _tick;
            _deaths++;

            if (npc.SpawnerIndex < 0 || npc.SpawnerIndex >= _spawners.Length)
            {
                return;
            }

            SpawnerState state = _spawners[npc.SpawnerIndex];

            // Mientras el cadáver siga en la zona ya cuenta como «pendiente»: vivos + pendientes
            // nunca supera el count del spawner. Si la reaparición pedida fuera más corta que la
            // animación de muerte, se espera a que el cadáver desaparezca.
            state.Alive--;
            state.Pending++;
            state.RespawnAt.Add(_tick + Math.Max(npc.RespawnTicks, NpcEntity.DeathAnimationTicks));
        }

        /// <summary>Crea los enemigos cuyo plazo de reaparición ha vencido, en orden de muerte.</summary>
        private void RespawnDue(EntityStore entities)
        {
            for (int s = 0; s < _spawners.Length; s++)
            {
                SpawnerState state = _spawners[s];
                List<long> pending = state.RespawnAt;

                if (pending.Count == 0)
                {
                    continue;
                }

                int write = 0;

                for (int i = 0; i < pending.Count; i++)
                {
                    if (pending[i] <= _tick)
                    {
                        entities.Add(CreateNpc(state, s));
                        state.Pending--;
                        state.Alive++;
                        _respawns++;
                    }
                    else
                    {
                        pending[write++] = pending[i];
                    }
                }

                if (write < pending.Count)
                {
                    pending.RemoveRange(write, pending.Count - write);
                }
            }
        }

        /// <summary>Crea un enemigo nuevo del spawner, en su punto y con la vida llena.</summary>
        private NpcEntity CreateNpc(SpawnerState state, int spawnerIndex)
        {
            if (_ids == null)
            {
                throw new InvalidOperationException(
                    "No se puede crear un enemigo sin haber llamado antes a Spawn.");
            }

            return new NpcEntity(
                _ids.Allocate(),
                state.Type.Id,
                state.Type.Speed,
                state.Spawner.Position,
                state.Spawner.WanderRadius,
                _tick + _random.Next(MinChangeTicks, MaxChangeTicks + 1),
                state.Type.MaxHealth,
                state.Type.RespawnTicks,
                spawnerIndex)
            {
                MapId = _zoneId,
                Position = state.Spawner.Position,
                HalfSize = state.Type.HalfSize,
            };
        }

        private void Step(NpcEntity npc)
        {
            if (IsOutsideLeash(npc))
            {
                // Correa: en cuanto se pasa del radio, vuelve hacia su casa y vuelve a decidir al
                // tick siguiente. Comprobarlo cada tick (y no solo al cambiar de dirección) es lo
                // que garantiza que no se aleje más de un paso del radio.
                npc.DirectionX = Sign(npc.Home.X - npc.Position.X);
                npc.DirectionY = Sign(npc.Home.Y - npc.Position.Y);
                npc.NextDirectionChangeTick = _tick + 1;
            }
            else if (_tick >= npc.NextDirectionChangeTick)
            {
                ChooseRandomDirection(npc);
                npc.NextDirectionChangeTick =
                    _tick + _random.Next(MinChangeTicks, MaxChangeTicks + 1);
            }

            Vec2 before = npc.Position;
            npc.Position = MovementSimulator.Step(
                npc.Position,
                npc.DirectionX,
                npc.DirectionY,
                npc.Speed,
                SimulationConstants.FixedDt,
                npc.HalfSize,
                _map);

            // Quería moverse y la pared lo dejó clavado: se prueba otra dirección al tick siguiente
            // en vez de quedarse empujando la pared hasta el próximo cambio.
            if ((npc.DirectionX != 0 || npc.DirectionY != 0) &&
                npc.Position.X == before.X &&
                npc.Position.Y == before.Y)
            {
                npc.NextDirectionChangeTick = _tick + 1;
            }
        }

        private bool IsOutsideLeash(NpcEntity npc)
        {
            float dx = npc.Position.X - npc.Home.X;
            float dy = npc.Position.Y - npc.Home.Y;
            return (dx * dx) + (dy * dy) > npc.WanderRadius * npc.WanderRadius;
        }

        /// <summary>Quieto con probabilidad 1/3; si no, una de las 8 direcciones.</summary>
        private void ChooseRandomDirection(NpcEntity npc)
        {
            if (_random.Next(3) == 0)
            {
                npc.DirectionX = 0;
                npc.DirectionY = 0;
                return;
            }

            int index = _random.Next(DirectionX.Length);
            npc.DirectionX = DirectionX[index];
            npc.DirectionY = DirectionY[index];
        }

        private static sbyte Sign(float value) =>
            value > 0f ? (sbyte)1 : value < 0f ? (sbyte)-1 : (sbyte)0;

        /// <summary>Lo que el spawner sabe de sus enemigos: cuántos viven y cuántos esperan turno.</summary>
        private sealed class SpawnerState
        {
            public SpawnerState(NpcSpawner spawner, NpcType type)
            {
                Spawner = spawner;
                Type = type;
            }

            public NpcSpawner Spawner { get; }

            public NpcType Type { get; }

            /// <summary>Enemigos vivos en la zona.</summary>
            public int Alive { get; set; }

            /// <summary>Reapariciones apuntadas, incluidos los cadáveres que aún se ven.</summary>
            public int Pending { get; set; }

            /// <summary>Ticks en los que toca reponer, en orden de muerte.</summary>
            public List<long> RespawnAt { get; } = new List<long>();
        }
    }
}
