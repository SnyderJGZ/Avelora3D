using System;
using System.Collections.Generic;
using System.IO;
using Avelora.Client;
using Avelora.Shared;
using Avelora.Shared.Maps;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Avelora.EditorTools
{
    /// <summary>Pinta y exporta el resultado de MapaCiudadLayout; no contiene decisiones de diseño.</summary>
    public static class MapaCiudadGenerator
    {
        private const string ArtFolder = "Assets/Avelora/Art/Mapa v2/Generado";
        private const string LegacyArtFolder = "Assets/Avelora/Art/Map";
        private const string TileFolder = "Assets/Avelora/Tiles/Map";
        private static readonly Dictionary<string, TileBase> Tiles = new Dictionary<string, TileBase>();

        [MenuItem("Avelora/Generar mapa «Ciudad»")]
        public static void Generate()
        {
            MapaCiudadLayout.Resultado layout = MapaCiudadLayout.Generar();
            SetupImporters();
            Paint(layout);
            ExportAndReport(layout);
        }

        private static void Paint(MapaCiudadLayout.Resultado layout)
        {
            Grid grid = UnityEngine.Object.FindAnyObjectByType<Grid>();
            if (grid == null)
            {
                throw new InvalidOperationException("La escena no tiene Grid.");
            }

            Tilemap water = Layer(grid, "Water", -30);
            Tilemap foam = Layer(grid, "Foam", -25);
            Tilemap sand = Layer(grid, "Sand", -20);
            Tilemap floor = Layer(grid, "Floor", -10);
            Tilemap deco = Layer(grid, "Deco", -9);
            Tilemap rock = Layer(grid, "Rock", -8);
            Tilemap bridge = Layer(grid, "Bridge", -7);
            Tilemap collision = Layer(grid, "Collision", 0);
            // El diseño nuevo no usa las capas antiguas de la costa ni de las mesetas.
            foam.ClearAllTiles();
            sand.ClearAllTiles();
            rock.ClearAllTiles();
            bridge.ClearAllTiles();

            Transform previousProps = grid.transform.Find("Props");
            if (previousProps != null)
            {
                UnityEngine.Object.DestroyImmediate(previousProps.gameObject);
            }

            Transform props = new GameObject("Props").transform;
            props.SetParent(grid.transform, false);
            TileBase block = BlockTile();
            var agua = TileFor(MapaCiudadLayout.Piezas.Agua.Archivo);

            for (int y = layout.OrigenY; y < layout.OrigenY + layout.Alto; y++)
            {
                for (int x = layout.OrigenX; x < layout.OrigenX + layout.Ancho; x++)
                {
                    int i = layout.Indice(x, y);
                    var cell = new Vector3Int(x, y, 0);
                    string nombreSuelo = layout.Suelo[i];
                    if (nombreSuelo == MapaCiudadLayout.Piezas.Agua.Archivo)
                    {
                        water.SetTile(cell, agua);
                    }
                    else
                    {
                        floor.SetTile(cell, TileFor(nombreSuelo));
                    }

                    string nombreCamino = layout.Camino[i];
                    if (nombreCamino != null)
                    {
                        deco.SetTile(cell, TileFor(nombreCamino));
                    }

                    if (layout.Bloqueado[i])
                    {
                        collision.SetTile(cell, block);
                    }
                }
            }

            foreach (MapaCiudadLayout.Objeto objeto in layout.Objetos)
            {
                var posicion = new Vector2(objeto.X + (objeto.Ancho / 2f), objeto.Y + (objeto.Alto / 2f));
                Prop(props, objeto.Archivo, posicion, 1000 - objeto.Y);
            }

            EditorSceneManager.MarkSceneDirty(grid.gameObject.scene);
            Debug.Log("[Avelora] Mapa «Ciudad» pintado: " + layout.Objetos.Count +
                " objetos; la escena queda marcada como modificada para guardarla desde Unity.");
        }

        private static void SetupImporters()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { ArtFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                var pivot = new Vector2(0.5f, 0.5f);
                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);
                bool changed = importer.textureType != TextureImporterType.Sprite ||
                    importer.spriteImportMode != SpriteImportMode.Single ||
                    !Mathf.Approximately(importer.spritePixelsPerUnit, 64f) ||
                    importer.filterMode != FilterMode.Point ||
                    importer.textureCompression != TextureImporterCompression.Uncompressed ||
                    settings.spriteAlignment != (int)SpriteAlignment.Custom || settings.spritePivot != pivot;
                if (!changed) continue;

                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spritePixelsPerUnit = 64f;
                importer.filterMode = FilterMode.Point;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.mipmapEnabled = false;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.ReadTextureSettings(settings);
                settings.spriteAlignment = (int)SpriteAlignment.Custom;
                settings.spritePivot = pivot;
                settings.spriteMeshType = SpriteMeshType.FullRect;
                importer.SetTextureSettings(settings);
                importer.SaveAndReimport();
            }

            if (!AssetDatabase.IsValidFolder(TileFolder))
            {
                AssetDatabase.CreateFolder("Assets/Avelora/Tiles", "Map");
            }
        }

        private static TileBase TileFor(string spriteName)
        {
            if (Tiles.TryGetValue(spriteName, out TileBase cached) && cached != null) return cached;

            string tilePath = TileFolder + "/" + spriteName + ".asset";
            var tile = AssetDatabase.LoadAssetAtPath<Tile>(tilePath);
            if (tile == null)
            {
                tile = ScriptableObject.CreateInstance<Tile>();
                tile.sprite = SpriteFor(spriteName);
                tile.colliderType = Tile.ColliderType.None;
                AssetDatabase.CreateAsset(tile, tilePath);
            }
            Tiles[spriteName] = tile;
            return tile;
        }

        private static TileBase BlockTile()
        {
            string tilePath = TileFolder + "/Block.asset";
            var tile = AssetDatabase.LoadAssetAtPath<Tile>(tilePath);
            if (tile == null)
            {
                tile = ScriptableObject.CreateInstance<Tile>();
                tile.colliderType = Tile.ColliderType.Grid;
                AssetDatabase.CreateAsset(tile, tilePath);
            }
            return tile;
        }

        private static Sprite SpriteFor(string name)
        {
            string carpeta = name == MapaCiudadLayout.Piezas.Agua.Archivo ? LegacyArtFolder : ArtFolder;
            // Las piezas de J-03 ya traen «.png»; el agua vieja («Water») no.
            string path = carpeta + "/" + Path.GetFileNameWithoutExtension(name) + ".png";
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null)
            {
                throw new InvalidOperationException("No hay sprite en " + path);
            }
            return sprite;
        }

        private static Tilemap Layer(Grid grid, string name, int order)
        {
            Transform child = grid.transform.Find(name);
            GameObject go = child != null
                ? child.gameObject
                : new GameObject(name, typeof(Tilemap), typeof(TilemapRenderer));
            go.transform.SetParent(grid.transform, false);
            var tilemap = go.GetComponent<Tilemap>();
            tilemap.ClearAllTiles();
            go.GetComponent<TilemapRenderer>().sortingOrder = order;
            return tilemap;
        }

        private static void Prop(Transform parent, string spriteName, Vector2 position, int order)
        {
            var go = new GameObject(spriteName);
            go.transform.SetParent(parent, false);
            go.transform.position = position;
            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = SpriteFor(spriteName);
            renderer.sortingOrder = order;
        }

        private static void ExportAndReport(MapaCiudadLayout.Resultado layout)
        {
            Tilemap collision = GameObject.Find("Collision").GetComponent<Tilemap>();
            MapCollisionGrid map = TilemapCollision.Build(collision);
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "avelora-ciudad.avmap"));
            MapFile.Save(path, map);
            Debug.Log("[Avelora] avelora-ciudad.avmap: " + map.Width + "x" + map.Height +
                " origen (" + map.OriginX + "," + map.OriginY + "), huella " + MapFile.ComputeContentHash(map) +
                ". Casillas alcanzables " + layout.CasillasAlcanzables + ".\n-> " + path);
        }
    }
}
