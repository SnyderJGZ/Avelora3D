using System.IO;
using Avelora.Shared;
using Avelora.Shared.Maps;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Avelora.Client
{
    /// <summary>
    /// Construye la rejilla de colisión a partir del Tilemap y la pone a disposición del cliente.
    /// </summary>
    /// <remarks>
    /// El cliente no "decide" la colisión: usa el mismo <see cref="MapCollisionGrid"/> que el
    /// servidor cargará del archivo <c>.avmap</c>. Aquí se construye directamente desde el
    /// Tilemap (que es la fuente del archivo exportado), de modo que jugar en el Editor no
    /// depende de exportar nada a mano.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class AveloraMap : MonoBehaviour
    {
        [Tooltip("Tilemap del que se lee la colisión. Si se deja vacío se busca el Tilemap de colisión de la escena.")]
        [SerializeField] private Tilemap tilemap;

        [Tooltip("Escribe en la consola la rejilla construida al arrancar.")]
        [SerializeField] private bool debugMode;

        [Tooltip("Al arrancar, guarda la rejilla como .avmap en StreamingAssets/Maps (el archivo que carga el servidor).")]
        [SerializeField] private bool exportOnAwake;

        [Tooltip("Nombre del archivo al exportar (sin extensión).")]
        [SerializeField] private string exportName = "avelora-etapa-a";

        /// <summary>Rejilla de colisión compartida. Es <c>null</c> hasta que corre <c>Awake</c>.</summary>
        public MapCollisionGrid Grid { get; private set; }

        /// <summary>Huella del mapa, para que cliente y servidor detecten desajustes.</summary>
        public string ContentHash { get; private set; } = "(sin construir)";

        /// <summary>
        /// La misma huella en sus 32 bytes: es lo que viaja en el saludo (el texto es para leerla
        /// en consola). Es <c>null</c> hasta que corre <c>Awake</c>.
        /// </summary>
        public byte[] ContentHashBytes { get; private set; }

        private void Awake()
        {
            if (tilemap == null)
            {
                tilemap = FindCollisionTilemap();
            }

            if (tilemap == null)
            {
                Debug.LogError("[Avelora] No hay ningún Tilemap en la escena: no se puede construir la colisión.", this);
                enabled = false;
                return;
            }

            Grid = TilemapCollision.Build(tilemap);
            ContentHashBytes = MapFile.ComputeContentHashBytes(Grid);
            ContentHash = MapFile.ToHexString(ContentHashBytes);

            if (debugMode)
            {
                Debug.Log(
                    "[Avelora] Rejilla de colisión " + Grid.Width + "x" + Grid.Height +
                    " (origen " + Grid.OriginX + "," + Grid.OriginY + "), huella " + ContentHash,
                    this);
            }

            if (exportOnAwake)
            {
                Export();
            }
        }

        /// <summary>Guarda la rejilla como <c>.avmap</c> en <c>StreamingAssets/Maps</c>.</summary>
        [ContextMenu("Exportar .avmap")]
        public void Export()
        {
            if (Grid == null)
            {
                Debug.LogWarning("[Avelora] Todavía no hay rejilla que exportar.", this);
                return;
            }

            string dir = Path.Combine(Application.streamingAssetsPath, "Maps");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, exportName + MapFile.Extension);
            MapFile.Save(path, Grid);
            Debug.Log("[Avelora] Mapa exportado: " + path, this);
        }

        /// <summary>
        /// Busca el Tilemap de colisión cuando el campo está vacío. Prefiere el que tiene un
        /// <see cref="TilemapCollider2D"/> (la forma habitual de pintar colisión) y, si no, el
        /// primero con algún tile que bloquee. Así un Tilemap decorativo de suelo no se toma
        /// por la rejilla de colisión.
        /// </summary>
        private static Tilemap FindCollisionTilemap()
        {
            Tilemap[] candidates = FindObjectsByType<Tilemap>();
            Tilemap firstWithBlockingTiles = null;

            for (int i = 0; i < candidates.Length; i++)
            {
                Tilemap candidate = candidates[i];

                if (candidate.GetComponent<TilemapCollider2D>() != null)
                {
                    return candidate;
                }

                if (firstWithBlockingTiles == null && HasBlockingTiles(candidate))
                {
                    firstWithBlockingTiles = candidate;
                }
            }

            return firstWithBlockingTiles != null
                ? firstWithBlockingTiles
                : (candidates.Length > 0 ? candidates[0] : null);
        }

        private static bool HasBlockingTiles(Tilemap map)
        {
            BoundsInt cells = map.cellBounds;

            for (int z = cells.zMin; z < cells.zMax; z++)
            {
                for (int y = cells.yMin; y < cells.yMax; y++)
                {
                    for (int x = cells.xMin; x < cells.xMax; x++)
                    {
                        if (map.GetColliderType(new Vector3Int(x, y, z)) != Tile.ColliderType.None)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private void OnDrawGizmosSelected()
        {
            Tilemap source = tilemap != null ? tilemap : FindCollisionTilemap();
            if (source == null)
            {
                return;
            }

            Gizmos.color = new Color(1f, 0.25f, 0.25f, 0.35f);

            // Nunca se comprime el Tilemap aquí: dibujar los gizmos no debe modificar la escena.
            // En Play se usa la rejilla ya construida; en edición, los límites actuales.
            BoundsInt bounds = Grid != null
                ? new BoundsInt(Grid.OriginX, Grid.OriginY, 0, Grid.Width, Grid.Height, 1)
                : source.cellBounds;

            for (int y = bounds.yMin; y < bounds.yMax; y++)
            {
                for (int x = bounds.xMin; x < bounds.xMax; x++)
                {
                    if (source.GetColliderType(new Vector3Int(x, y, 0)) != Tile.ColliderType.None)
                    {
                        Gizmos.DrawCube(new Vector3(x + 0.5f, y + 0.5f, 0f), Vector3.one);
                    }
                }
            }
        }
    }
}
