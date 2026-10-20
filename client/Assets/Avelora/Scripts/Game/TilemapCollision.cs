using Avelora.Shared;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Avelora.Client
{
    /// <summary>
    /// Puente entre el Tilemap de Unity y la rejilla de colisión compartida (guía del núcleo §6.3).
    /// </summary>
    /// <remarks>
    /// La usan tanto el cliente en tiempo de ejecución (<see cref="AveloraMap"/>) como el
    /// exportador del Editor, para que el mapa que se juega y el <c>.avmap</c> que carga el
    /// servidor se construyan con exactamente la misma lógica.
    ///
    /// Este archivo vive en el cliente y no en <c>Avelora.Shared</c> porque depende de
    /// <c>UnityEngine.Tilemaps</c>, y la librería compartida no puede depender de Unity.
    /// </remarks>
    public static class TilemapCollision
    {
        /// <summary>
        /// Construye la rejilla. Cuenta como bloqueado todo tile cuyo <c>ColliderType</c> no sea
        /// <c>None</c> (así se pinta la colisión con las herramientas normales del Tilemap).
        /// </summary>
        public static MapCollisionGrid Build(Tilemap tilemap) => Build(tilemap, out _);

        /// <inheritdoc cref="Build(Tilemap)"/>
        /// <param name="bounds">
        /// Región del Tilemap que se exportó. Su mínimo (<c>bounds.min</c>) es el origen de la
        /// rejilla: un Tilemap puede tener celdas negativas y la rejilla guarda el desplazamiento
        /// para que las coordenadas de tile absolutas sigan funcionando (guía del núcleo §6.3).
        /// </param>
        public static MapCollisionGrid Build(Tilemap tilemap, out BoundsInt bounds)
        {
            if (tilemap == null)
            {
                throw new System.ArgumentNullException(nameof(tilemap));
            }

            // Los límites se calculan sin llamar a CompressBounds(): comprimir modifica el
            // componente y deja la escena marcada como modificada solo por exportar o dibujar
            // los gizmos. La región obtenida es la misma que deja CompressBounds().
            bounds = ComputeUsedBounds(tilemap);

            int width = Mathf.Max(1, bounds.size.x);
            int height = Mathf.Max(1, bounds.size.y);
            var blocked = new bool[width * height];
            BoundsInt cells = tilemap.cellBounds;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // La celda de la rejilla (x,y) es el tile (bounds.xMin + x, bounds.yMin + y).
                    int tileX = bounds.xMin + x;
                    int tileY = bounds.yMin + y;
                    bool solid = false;

                    // Si hay varias capas en Z se proyectan en la misma celda 2D: basta con que
                    // una bloquee para que la celda quede bloqueada.
                    for (int z = cells.zMin; z < cells.zMax && !solid; z++)
                    {
                        solid = tilemap.GetColliderType(new Vector3Int(tileX, tileY, z)) != Tile.ColliderType.None;
                    }

                    blocked[(y * width) + x] = solid;
                }
            }

            return new MapCollisionGrid(width, height, blocked, bounds.xMin, bounds.yMin);
        }

        /// <summary>
        /// Región ocupada por tiles, calculada sin modificar el Tilemap. Equivale a
        /// <c>CompressBounds()</c> seguido de <c>cellBounds</c>, pero sin efectos secundarios.
        /// </summary>
        private static BoundsInt ComputeUsedBounds(Tilemap tilemap)
        {
            BoundsInt cells = tilemap.cellBounds;
            int minX = int.MaxValue;
            int minY = int.MaxValue;
            int maxX = int.MinValue;
            int maxY = int.MinValue;

            for (int z = cells.zMin; z < cells.zMax; z++)
            {
                for (int y = cells.yMin; y < cells.yMax; y++)
                {
                    for (int x = cells.xMin; x < cells.xMax; x++)
                    {
                        if (!tilemap.HasTile(new Vector3Int(x, y, z)))
                        {
                            continue;
                        }

                        if (x < minX) { minX = x; }
                        if (y < minY) { minY = y; }
                        if (x > maxX) { maxX = x; }
                        if (y > maxY) { maxY = y; }
                    }
                }
            }

            if (minX == int.MaxValue)
            {
                // Tilemap vacío: una celda para no crear una rejilla de tamaño cero.
                return new BoundsInt(cells.xMin, cells.yMin, 0, 1, 1, 1);
            }

            return new BoundsInt(minX, minY, 0, maxX - minX + 1, maxY - minY + 1, 1);
        }
    }
}
