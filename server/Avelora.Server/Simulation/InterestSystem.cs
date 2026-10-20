#nullable enable
using System;
using System.Collections.Generic;
using Avelora.Server.Entities;

namespace Avelora.Server.Simulation
{
    /// <summary>
    /// Decide qué entidades ve cada jugador y mantiene la histéresis del borde.
    /// </summary>
    /// <remarks>
    /// El conjunto de visibles se guarda en el propio <see cref="PlayerEntity"/> porque es estado
    /// por jugador y solo lo toca el hilo de simulación. En cada snapshot:
    /// <list type="number">
    /// <item>se consulta la grilla con el radio de salida (el mayor);</item>
    /// <item>una entidad que ya estaba visible se conserva mientras no pase del radio de salida;</item>
    /// <item>una entidad nueva solo entra si está dentro del radio de entrada;</item>
    /// <item>el jugador local se incluye siempre, aunque su propia celda no exista.</item>
    /// </list>
    ///
    /// El resultado se escribe en una lista reutilizada: no se reserva memoria por snapshot.
    /// </remarks>
    public sealed class InterestSystem
    {
        private readonly EntitySpatialGrid _grid;
        private readonly List<Entity> _candidates = new List<Entity>(64);

        public InterestSystem(EntitySpatialGrid grid, InterestSettings settings)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>Grilla espacial que indexa las entidades de la zona.</summary>
        public EntitySpatialGrid Grid => _grid;

        /// <summary>Radios y tamaño de celda efectivos.</summary>
        public InterestSettings Settings { get; }

        /// <summary>Índice una entidad nueva en la grilla.</summary>
        public void Add(Entity entity) => _grid.Add(entity);

        /// <summary>Retira una entidad de la grilla.</summary>
        public void Remove(Entity entity) => _grid.Remove(entity);

        /// <summary>Reindexa una entidad que puede haber cambiado de celda.</summary>
        public void Update(Entity entity) => _grid.Update(entity);

        /// <summary>
        /// Rellena <paramref name="results"/> con el jugador y las entidades visibles desde él.
        /// La lista se vacía antes; el jugador local siempre es el primero.
        /// </summary>
        public void CollectVisible(PlayerEntity viewer, List<Entity> results)
        {
            if (viewer is null)
            {
                throw new ArgumentNullException(nameof(viewer));
            }

            if (results is null)
            {
                throw new ArgumentNullException(nameof(results));
            }

            float radius = Settings.Radius;
            float exitRadius = Settings.ExitRadius;
            float radiusSquared = radius * radius;
            float exitRadiusSquared = exitRadius * exitRadius;

            _grid.Query(viewer.Position.X, viewer.Position.Y, exitRadius, _candidates);

            results.Clear();
            results.Add(viewer);

            HashSet<int> visible = viewer.VisibleEntityIds;
            HashSet<int> next = viewer.NextVisibleEntityIds;
            next.Clear();
            next.Add(viewer.Id);

            for (int i = 0; i < _candidates.Count; i++)
            {
                Entity candidate = _candidates[i];

                if (candidate.Id == viewer.Id)
                {
                    continue;
                }

                float dx = candidate.Position.X - viewer.Position.X;
                float dy = candidate.Position.Y - viewer.Position.Y;
                float distanceSquared = (dx * dx) + (dy * dy);
                bool wasVisible = visible.Contains(candidate.Id);

                // Una entidad que ya estaba visible tiene margen extra antes de desaparecer.
                if (distanceSquared <= (wasVisible ? exitRadiusSquared : radiusSquared))
                {
                    results.Add(candidate);
                    next.Add(candidate.Id);
                }
            }

            // Se reemplaza el conjunto anterior sin reservar otro HashSet.
            visible.Clear();
            foreach (int id in next)
            {
                visible.Add(id);
            }
        }
    }
}
