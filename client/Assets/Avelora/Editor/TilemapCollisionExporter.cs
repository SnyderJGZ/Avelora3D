using Avelora.Client;
using Avelora.Shared;
using Avelora.Shared.Maps;
using UnityEditor;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Avelora.EditorTools
{
    /// <summary>
    /// Exporta la rejilla de colisión de un Tilemap a un archivo <c>.avmap</c>
    /// (Etapa A: «la exportación del mapa desde el Tilemap»).
    /// </summary>
    /// <remarks>
    /// El servidor nunca lee escenas de Unity: carga el <c>.avmap</c> generado aquí. Como usa
    /// <see cref="TilemapCollision.Build"/>, el mismo código que el cliente en tiempo de
    /// ejecución, el mapa jugado y el exportado no pueden divergir.
    /// </remarks>
    public static class TilemapCollisionExporter
    {
        [MenuItem("Avelora/Exportar colisión del Tilemap (.avmap)…")]
        public static void ExportSelectedTilemap()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                EditorUtility.DisplayDialog("Avelora", "Selecciona un GameObject con un componente Tilemap.", "Vale");
                return;
            }

            Tilemap tilemap = selected.GetComponent<Tilemap>();
            if (tilemap == null)
            {
                EditorUtility.DisplayDialog("Avelora", "El GameObject seleccionado no tiene un Tilemap.", "Vale");
                return;
            }

            MapCollisionGrid grid = TilemapCollision.Build(tilemap);

            string path = EditorUtility.SaveFilePanel(
                "Guardar colisión del Tilemap",
                Application.dataPath,
                tilemap.name + MapFile.Extension,
                MapFile.Extension.TrimStart('.'));

            if (string.IsNullOrEmpty(path))
            {
                return; // cancelado
            }

            MapFile.Save(path, grid);

            Debug.Log(
                "[Avelora] Colisión exportada: " + grid.Width + "x" + grid.Height +
                " celdas, origen (" + grid.OriginX + ", " + grid.OriginY + "), huella " +
                MapFile.ComputeContentHash(grid) + "\n-> " + path);

            EditorUtility.RevealInFinder(path);
        }
    }
}
