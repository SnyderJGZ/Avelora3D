#nullable enable
using System;
using Avelora.Server.Entities;
using Avelora.Server.Npcs;
using Avelora.Shared;

namespace Avelora.Server.Simulation
{
    /// <summary>
    /// IA de deambular de los enemigos (I-01): dirección aleatoria cada pocos ticks, con vuelta a
    /// casa cuando se alejan demasiado, y movimiento con el mismo <see cref="MovementSimulator"/>
    /// que usan los jugadores.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Un único <see cref="System.Random"/>, sembrado con <c>ServerConfig.NpcSeed</c>: con la misma
    /// semilla y los mismos datos, dos zonas producen exactamente las mismas posiciones. Por eso no
    /// se usa <see cref="Random.Shared"/> ni el reloj.
    /// </para>
    /// <para>
    /// Solo se toca desde el hilo de simulación, dentro de <see cref="Zone.Tick"/>, justo después de
    /// mover a los jugadores y antes de reindexar el área de interés. No reserva memoria por tick.
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

        public NpcSystem(MapCollisionGrid map, int seed)
        {
            _map = map ?? throw new ArgumentNullException(nameof(map));
            _random = new System.Random(seed);
        }

        /// <summary>Ticks de IA ejecutados. Es el reloj con el que se programan los cambios.</summary>
        public long TickCount => _tick;

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

                for (int i = 0; i < spawner.Count; i++)
                {
                    var npc = new NpcEntity(
                        ids.Allocate(),
                        type.Id,
                        type.Speed,
                        spawner.Position,
                        spawner.WanderRadius,
                        _tick + _random.Next(MinChangeTicks, MaxChangeTicks + 1))
                    {
                        MapId = zoneId,
                        Position = spawner.Position,
                        HalfSize = type.HalfSize,
                    };

                    entities.Add(npc);
                }
            }
        }

        /// <summary>Avanza un paso de IA y movimiento a cada enemigo del almacén.</summary>
        public void Tick(EntityStore entities)
        {
            if (entities is null)
            {
                throw new ArgumentNullException(nameof(entities));
            }

            for (int i = 0; i < entities.Count; i++)
            {
                if (entities[i] is NpcEntity npc)
                {
                    Step(npc);
                }
            }

            _tick++;
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
    }
}
