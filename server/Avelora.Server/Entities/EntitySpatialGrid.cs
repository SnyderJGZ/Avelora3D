#nullable enable
using System;
using System.Collections.Generic;

namespace Avelora.Server.Entities
{
    /// <summary>
    /// Grilla espacial para responder «qué entidades hay cerca» sin recorrer el mundo entero.
    /// </summary>
    /// <remarks>
    /// Cada entidad vive en una única celda (la de su posición actual) y guarda sus coordenadas en
    /// <see cref="Entity.CellX"/> y <see cref="Entity.CellY"/> para que moverla sea O(1). Las celdas
    /// se crean bajo demanda en un diccionario: no se reserva una matriz para todo el mapa, algo
    /// importante cuando el mapa crezca a 4096×4096 y solo haya diez jugadores.
    ///
    /// La usa solo el hilo de simulación. <see cref="Query"/> recibe una lista reutilizable para no
    /// reservar memoria en cada snapshot (plan del servidor §9).
    /// </remarks>
    public sealed class EntitySpatialGrid
    {
        private readonly Dictionary<long, List<Entity>> _cells = new Dictionary<long, List<Entity>>();
        private readonly float _cellSize;

        public EntitySpatialGrid(float cellSize)
        {
            if (!IsFinite(cellSize) || cellSize <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "La celda espacial debe ser positiva.");
            }

            _cellSize = cellSize;
        }

        /// <summary>Lado de cada celda, en tiles.</summary>
        public float CellSize => _cellSize;

        /// <summary>Celdas ocupadas ahora mismo (las vacías se retiran).</summary>
        public int CellCount => _cells.Count;

        /// <summary>Índice la entidad en la celda de su posición actual.</summary>
        public void Add(Entity entity)
        {
            if (entity is null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            if (!IsFinite(entity.Position.X) || !IsFinite(entity.Position.Y))
            {
                throw new ArgumentException("La posición de la entidad no es un número finito.", nameof(entity));
            }

            if (entity.CellX != Entity.NoCell)
            {
                // La misma entidad no puede estar en dos celdas: se retira de la anterior.
                Remove(entity);
            }

            int cellX = CellOf(entity.Position.X);
            int cellY = CellOf(entity.Position.Y);
            long key = CellKey(cellX, cellY);

            if (!_cells.TryGetValue(key, out List<Entity>? bucket))
            {
                bucket = new List<Entity>(4);
                _cells.Add(key, bucket);
            }

            bucket.Add(entity);
            entity.CellX = cellX;
            entity.CellY = cellY;
        }

        /// <summary>Retira la entidad de su celda. Si no estaba indexada, no hace nada.</summary>
        public void Remove(Entity entity)
        {
            if (entity is null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            if (entity.CellX == Entity.NoCell)
            {
                return;
            }

            long key = CellKey(entity.CellX, entity.CellY);

            if (_cells.TryGetValue(key, out List<Entity>? bucket))
            {
                for (int i = 0; i < bucket.Count; i++)
                {
                    if (ReferenceEquals(bucket[i], entity))
                    {
                        int last = bucket.Count - 1;
                        bucket[i] = bucket[last];
                        bucket.RemoveAt(last);

                        if (bucket.Count == 0)
                        {
                            _cells.Remove(key);
                        }

                        break;
                    }
                }
            }

            entity.CellX = Entity.NoCell;
            entity.CellY = Entity.NoCell;
        }

        /// <summary>
        /// Reindexa la entidad si ha cambiado de celda. Se llama una vez por tick para todas las
        /// entidades: mover una entidad es O(1) y este recorrido es O(n) con n = entidades vivas.
        /// </summary>
        public void Update(Entity entity)
        {
            if (entity is null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            if (entity.CellX == Entity.NoCell)
            {
                Add(entity);
                return;
            }

            int cellX = CellOf(entity.Position.X);
            int cellY = CellOf(entity.Position.Y);

            if (cellX == entity.CellX && cellY == entity.CellY)
            {
                return;
            }

            Remove(entity);
            Add(entity);
        }

        /// <summary>
        /// Escribe en <paramref name="results"/> las entidades cuyo centro está a
        /// <paramref name="radius"/> tiles o menos de (<paramref name="x"/>, <paramref name="y"/>).
        /// La lista se vacía antes de rellenarla y no se reserva memoria por consulta.
        /// </summary>
        public void Query(float x, float y, float radius, List<Entity> results)
        {
            if (results is null)
            {
                throw new ArgumentNullException(nameof(results));
            }

            if (!IsFinite(x) || !IsFinite(y))
            {
                throw new ArgumentOutOfRangeException(nameof(x), "El centro de la consulta no es un número finito.");
            }

            if (!IsFinite(radius) || radius < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(radius), radius, "El radio de consulta no puede ser negativo.");
            }

            results.Clear();

            int minCellX = CellOf(x - radius);
            int maxCellX = CellOf(x + radius);
            int minCellY = CellOf(y - radius);
            int maxCellY = CellOf(y + radius);
            float radiusSquared = radius * radius;

            for (int cellX = minCellX; cellX <= maxCellX; cellX++)
            {
                for (int cellY = minCellY; cellY <= maxCellY; cellY++)
                {
                    if (!_cells.TryGetValue(CellKey(cellX, cellY), out List<Entity>? bucket))
                    {
                        continue;
                    }

                    for (int i = 0; i < bucket.Count; i++)
                    {
                        Entity entity = bucket[i];
                        float dx = entity.Position.X - x;
                        float dy = entity.Position.Y - y;

                        if ((dx * dx) + (dy * dy) <= radiusSquared)
                        {
                            results.Add(entity);
                        }
                    }
                }
            }
        }

        /// <summary>Vacía la grilla. No reindexa las entidades: se usa al descartar la zona entera.</summary>
        public void Clear() => _cells.Clear();

        private int CellOf(float coordinate) => (int)MathF.Floor(coordinate / _cellSize);

        private static long CellKey(int cellX, int cellY) => ((long)cellX << 32) | (uint)cellY;

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
