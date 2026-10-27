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
    /// <summary>
    /// Genera el mapa «Valle» (80×60) con el arte de Tiny Swords: isla con costa, río con dos
    /// puentes, lago, aldea de arena en el centro, bosque al oeste y mesetas de roca al este.
    /// </summary>
    /// <remarks>
    /// Determinista (semilla fija): volver a generarlo da la misma rejilla y la misma huella. La
    /// colisión va en el Tilemap <c>Collision</c> con un tile invisible; el resto de capas solo
    /// dibujan. Al terminar exporta <c>avelora-valle.avmap</c> junto a la carpeta Assets, que es el
    /// archivo que carga el servidor (se copia a <c>maps/</c> del repositorio).
    /// </remarks>
    public static class ValleyMapGenerator
    {
        private const int MinX = -40;
        private const int MinY = -30;
        private const int Width = 80;
        private const int Height = 60;
        private const int Seed = 2610;
        private const string ArtFolder = "Assets/Avelora/Art/Map";
        private const string TileFolder = "Assets/Avelora/Tiles/Map";

        /// <summary>Casilla de aparición: centro de la plaza de la aldea.</summary>
        public static readonly Vector2 Spawn = new Vector2(-6.5f, 0.5f);

        // Claros y zonas de enemigos (centro y radio sin árboles). Los spawners de npcs.json usan
        // estos mismos centros.
        private static readonly Vector3Int[] Clearings =
        {
            new Vector3Int(-21, 0, 4), new Vector3Int(-30, 9, 4), new Vector3Int(-27, -11, 4),
            new Vector3Int(-8, -14, 4), new Vector3Int(2, 10, 4), new Vector3Int(3, -10, 4),
            new Vector3Int(22, 4, 4), new Vector3Int(29, 12, 4), new Vector3Int(25, 15, 3),
            new Vector3Int(31, -3, 4),
        };

        private static bool[,] _land;
        private static bool[,] _sand;
        private static bool[,] _rock;
        private static bool[,] _cliff;
        private static bool[,] _tree;
        private static bool[,] _bridge;
        private static bool[,] _waterRock;
        private static int[] _riverX;
        private static System.Random _rng;

        [MenuItem("Avelora/Generar mapa «Valle»")]
        public static void Generate()
        {
            _rng = new System.Random(Seed);
            SetupImporters();
            BuildLayout();
            Paint();
            ExportAndReport();
        }

        // ------------------------------------------------------------------ diseño lógico

        private static void BuildLayout()
        {
            _land = new bool[Width, Height];
            _sand = new bool[Width, Height];
            _rock = new bool[Width, Height];
            _cliff = new bool[Width, Height];
            _tree = new bool[Width, Height];
            _bridge = new bool[Width, Height];
            _waterRock = new bool[Width, Height];

            // Isla: elipse con la costa deformada por ruido. Dos casillas de mar en todo el borde.
            ForEach((x, y) =>
            {
                float nx = x / 36f;
                float ny = y / 26f;
                float d = Mathf.Sqrt((nx * nx) + (ny * ny)) + ((Noise(x, y, 7f, 1) - 0.5f) * 0.35f);
                bool margin = x < MinX + 2 || x >= MinX + Width - 2 || y < MinY + 2 || y >= MinY + Height - 2;
                Set(_land, x, y, d < 1f && !margin);
            });

            // Río de norte a sur hasta el lago del sudeste.
            _riverX = new int[Height];
            int[] keyY = { 29, 20, 12, 4, -4, -11, -16 };
            int[] keyX = { 10, 12, 9, 12, 14, 18, 21 };
            for (int y = MinY; y < MinY + Height; y++)
            {
                _riverX[y - MinY] = Mathf.RoundToInt(Interpolate(keyY, keyX, y) + ((Noise(0, y, 4f, 2) - 0.5f) * 2f));
            }

            for (int y = -16; y < MinY + Height; y++)
            {
                int cx = _riverX[y - MinY];
                for (int x = cx - 1; x <= cx + 1; x++)
                {
                    Set(_land, x, y, false);
                }
            }

            Ellipse(23, -18, 7f, 5f, (x, y) => Set(_land, x, y, false));
            Ellipse(-24, 11, 2.5f, 2f, (x, y) => Set(_land, x, y, false)); // estanque del bosque

            // Aldea: plaza ovalada de arena. Caminos sinuosos de dos casillas; los tramos de los
            // puentes van rectos (sin ondulación) para que el puente sea horizontal.
            Ellipse(-7, 0, 5f, 4f, (x, y) => Set(_sand, x, y, true));
            int r0 = RiverAt(0);
            int r16 = RiverAt(16);
            Road(true, -3, 0, 2, 2, r0 - 3, 0);                        // este, hasta el primer puente
            Road(false, r0 - 3, 0, r0 + 3, 0);                         // primer puente
            Road(true, r0 + 3, 0, 20, 3, 22, 4);                       // tierras altas
            Road(true, 20, 3, 26, 8, 29, 12);
            Road(true, 22, 4, 28, -1, 31, -3);
            Road(true, -7, 3, -5, 9, 0, 13, r16 - 3, 16);              // norte, hasta el segundo puente
            Road(false, r16 - 3, 16, r16 + 3, 16);                     // segundo puente
            Road(true, r16 + 3, 16, 20, 17, 25, 15);
            Road(true, -11, 0, -16, -1, -21, 0, -26, 4, -30, 9);       // oeste, por el bosque
            Road(true, -21, 0, -24, -5, -27, -11);                     // ramal al suroeste
            Road(true, -7, -3, -9, -8, -8, -14, -6, -20);              // sur, hasta la playa
            Road(true, 2, 2, 4, -4, 3, -10);                           // prado del sur

            // Puentes: las casillas de río por las que pasa un camino.
            ForEach((x, y) =>
            {
                if (!Get(_land, x, y) && Get(_sand, x, y) && IsRiver(x, y))
                {
                    Set(_bridge, x, y, true);
                }
            });

            // Playa: tierra pegada al agua (y algo más en la costa del mar, con ruido).
            ForEach((x, y) =>
            {
                if (!Get(_land, x, y))
                {
                    Set(_sand, x, y, false);
                    return;
                }

                if (NearWater(x, y, 1))
                {
                    Set(_sand, x, y, true);
                }
            });

            // Limpieza: sin islotes de hierba en la arena ni huecos de arena sueltos en la hierba.
            for (int pass = 0; pass < 3; pass++)
            {
                ForEach((x, y) =>
                {
                    if (!Get(_land, x, y))
                    {
                        return;
                    }

                    int grass = (GrassMask(x - 1, y) ? 1 : 0) + (GrassMask(x + 1, y) ? 1 : 0) +
                        (GrassMask(x, y - 1) ? 1 : 0) + (GrassMask(x, y + 1) ? 1 : 0);
                    if (!Get(_sand, x, y) && grass <= 1)
                    {
                        Set(_sand, x, y, true);
                    }
                    else if (Get(_sand, x, y) && grass >= 3 && !NearWater(x, y, 1))
                    {
                        Set(_sand, x, y, false);
                    }
                });
            }

            // Mesetas de roca al este del río, sin tocar caminos, claros ni orillas.
            ForEach((x, y) =>
            {
                if (x > RiverAt(y) + 3 && y > -12 && Get(_land, x, y) && !Get(_sand, x, y) &&
                    !NearClearing(x, y, 1) && !NearSand(x, y, 1) && Noise(x, y, 8f, 4) > 0.42f)
                {
                    Set(_rock, x, y, true);
                }
            });

            // Sin mesetas de una sola fila: el arte necesita al menos dos filas de superficie.
            for (int pass = 0; pass < 4; pass++)
            {
                ForEach((x, y) =>
                {
                    if (Get(_rock, x, y) && (!Get(_rock, x, y + 1) && !Get(_rock, x, y - 1)))
                    {
                        Set(_rock, x, y, false);
                    }
                });
            }

            // El acantilado ocupa la casilla bajo el borde inferior de la meseta.
            ForEach((x, y) =>
            {
                if (Get(_rock, x, y) && !Get(_rock, x, y - 1) && Get(_land, x, y - 1) && !Get(_sand, x, y - 1))
                {
                    Set(_cliff, x, y - 1, true);
                }
            });

            // Árboles: bosque denso al oeste, sueltos en el resto; nunca en caminos ni junto a ellos.
            ForEach((x, y) =>
            {
                if (!Get(_land, x, y) || Get(_sand, x, y) || Get(_rock, x, y) || Get(_cliff, x, y) ||
                    NearSand(x, y, 1) || NearClearing(x, y, 1) || NearRock(x, y))
                {
                    return;
                }

                bool forest = x < -13 && Noise(x, y, 6f, 5) > 0.32f;
                bool grove = y > 15 && x < RiverAt(y) - 3 && Noise(x, y, 5f, 6) > 0.55f;
                double chance = forest ? 0.72 : grove ? 0.45 : (Distance(x, y, -7, 0) < 11f ? 0.0 : 0.04);
                if (_rng.NextDouble() < chance)
                {
                    Set(_tree, x, y, true);
                }
            });

            // Rocas en el lago y en el mar cercano a la costa.
            ForEach((x, y) =>
            {
                if (!Get(_land, x, y) && !Get(_bridge, x, y) && !IsRiver(x, y) &&
                    NearLand(x, y, 3) && !NearLand(x, y, 1) && _rng.NextDouble() < 0.025)
                {
                    Set(_waterRock, x, y, true);
                }
            });
        }

        /// <summary>
        /// Camino de dos casillas por los puntos dados (x0, y0, x1, y1…). Con <paramref name="wobble"/>,
        /// cada tramo se curva con ruido y vuelve al punto exacto en sus extremos.
        /// </summary>
        private static void Road(bool wobble, params int[] points)
        {
            for (int i = 0; i + 3 < points.Length; i += 2)
            {
                float ax = points[i], ay = points[i + 1], bx = points[i + 2], by = points[i + 3];
                float length = Mathf.Max(1f, Mathf.Sqrt(((bx - ax) * (bx - ax)) + ((by - ay) * (by - ay))));
                float px = -(by - ay) / length;
                float py = (bx - ax) / length;
                int steps = Mathf.CeilToInt(length * 3f);
                bool horizontal = Mathf.Abs(bx - ax) >= Mathf.Abs(by - ay);
                for (int s = 0; s <= steps; s++)
                {
                    float t = (float)s / steps;
                    float bend = wobble ? (Noise(Mathf.RoundToInt(ax + bx), s, 6f, 7 + i) - 0.5f) * 5f * Mathf.Sin(t * Mathf.PI) : 0f;
                    int cx = Mathf.FloorToInt(Mathf.Lerp(ax, bx, t) + (px * bend));
                    int cy = Mathf.FloorToInt(Mathf.Lerp(ay, by, t) + (py * bend));
                    // Dos casillas en perpendicular a la dirección del tramo.
                    Set(_sand, cx, cy, true);
                    Set(_sand, horizontal ? cx : cx + 1, horizontal ? cy + 1 : cy, true);
                }
            }
        }

        // ------------------------------------------------------------------ pintado

        private static void Paint()
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

            Transform props = grid.transform.Find("Props");
            if (props != null)
            {
                UnityEngine.Object.DestroyImmediate(props.gameObject);
            }

            props = new GameObject("Props").transform;
            props.SetParent(grid.transform, false);

            TileBase block = BlockTile();
            TileBase waterTile = TileFor("Water");
            TileBase foamTile = TileFor("Foam");

            // El agua se extiende más allá de la rejilla para que la cámara no vea el vacío.
            for (int y = MinY - 12; y < MinY + Height + 12; y++)
            {
                for (int x = MinX - 16; x < MinX + Width + 16; x++)
                {
                    water.SetTile(new Vector3Int(x, y, 0), waterTile);
                }
            }

            int trees = 0;
            int decos = 0;
            ForEach((x, y) =>
            {
                var cell = new Vector3Int(x, y, 0);
                bool land = Get(_land, x, y);

                if (land)
                {
                    if (NearWater(x, y, 1))
                    {
                        foam.SetTile(cell, foamTile);
                    }

                    sand.SetTile(cell, TileFor("Sand_" + AutoTile(_land, x, y)));

                    if (!Get(_sand, x, y))
                    {
                        floor.SetTile(cell, TileFor("Grass_" + AutoTile(GrassMask, x, y)));
                    }
                }

                if (Get(_rock, x, y))
                {
                    rock.SetTile(cell, TileFor("Rock_" + RockTile(x, y)));
                }
                else if (Get(_cliff, x, y))
                {
                    rock.SetTile(cell, TileFor("Rock_" + Column(_rock, x, y + 1) + "3"));
                }

                if (Get(_bridge, x, y))
                {
                    string piece = !Get(_bridge, x - 1, y) ? "00" : (!Get(_bridge, x + 1, y) ? "20" : "10");
                    bridge.SetTile(cell, TileFor("Bridge_" + piece));
                }

                if (Get(_tree, x, y))
                {
                    Prop(props, "Tree_" + _rng.Next(6), new Vector2(x + 0.5f, y + 0.25f), 20);
                    trees++;
                }

                if (Get(_waterRock, x, y))
                {
                    Prop(props, "WaterRock_" + (1 + _rng.Next(4)), new Vector2(x + 0.5f, y + 0.5f), -6);
                }

                if (land && !Get(_sand, x, y) && !Get(_tree, x, y) && !Get(_rock, x, y) &&
                    !Get(_cliff, x, y) && !NearSand(x, y, 1))
                {
                    string decoName = PickDeco(x, y);
                    if (decoName != null)
                    {
                        deco.SetTile(cell, TileFor(decoName));
                        decos++;
                    }
                }

                if (IsBlocked(x, y))
                {
                    collision.SetTile(cell, block);
                }
            });

            // Atrezo de la aldea.
            Prop(props, "Deco_16", new Vector2(-1.5f, 2.6f), 20);
            Prop(props, "Deco_17", new Vector2(-10.5f, 3.4f), 20);

            GameObject player = GameObject.Find("Player");
            if (player != null)
            {
                player.transform.position = new Vector3(Spawn.x, Spawn.y, 0f);
            }

            EditorSceneManager.MarkSceneDirty(grid.gameObject.scene);
            EditorSceneManager.SaveScene(grid.gameObject.scene);
            Debug.Log("[Avelora] Mapa «Valle» pintado: " + trees + " árboles, " + decos + " adornos.");
        }

        private static string PickDeco(int x, int y)
        {
            double roll = _rng.NextDouble();
            if (roll > 0.07)
            {
                return null;
            }

            int pick = _rng.Next(100);
            if (x < -14)
            {
                // Bosque: setas, matas y hierbas.
                return pick < 30 ? Deco(1 + _rng.Next(3)) : (pick < 70 ? Deco(7 + _rng.Next(3)) : Deco(10 + _rng.Next(2)));
            }

            if (x > RiverAt(y) + 2)
            {
                // Tierras altas: piedras, hierbas y algún hueso.
                return pick < 50 ? Deco(4 + _rng.Next(3)) : (pick < 85 ? Deco(10 + _rng.Next(2)) : Deco(14 + _rng.Next(2)));
            }

            if (Distance(x, y, -7, 0) < 12f)
            {
                // Alrededor de la aldea: calabazas y matas.
                return pick < 45 ? Deco(12 + _rng.Next(2)) : Deco(7 + _rng.Next(3));
            }

            return pick < 60 ? Deco(7 + _rng.Next(5)) : Deco(4 + _rng.Next(3));
        }

        private static string Deco(int n) => "Deco_" + n.ToString("D2");

        private static bool IsBlocked(int x, int y)
        {
            bool water = !Get(_land, x, y) && !Get(_bridge, x, y);
            return water || Get(_rock, x, y) || Get(_cliff, x, y) || Get(_tree, x, y) || Get(_waterRock, x, y);
        }

        private static bool GrassMask(int x, int y) => Get(_land, x, y) && !Get(_sand, x, y);

        /// <summary>Columna y fila del autotile 4×4 de Tiny Swords según los vecinos ortogonales.</summary>
        private static string AutoTile(Func<int, int, bool> mask, int x, int y)
        {
            bool w = mask(x - 1, y);
            bool e = mask(x + 1, y);
            bool n = mask(x, y + 1);
            bool s = mask(x, y - 1);
            int col = w && e ? 1 : (!w && !e ? 3 : (!w ? 0 : 2));
            int row = n && s ? 1 : (!n && !s ? 3 : (!n ? 0 : 2));
            return col.ToString() + row;
        }

        private static string AutoTile(bool[,] mask, int x, int y) => AutoTile((a, b) => Get(mask, a, b), x, y);

        private static string RockTile(int x, int y)
        {
            bool n = Get(_rock, x, y + 1);
            bool s = Get(_rock, x, y - 1);
            int row = n && s ? 1 : (!n ? 0 : 2);
            return Column(_rock, x, y) + row.ToString();
        }

        private static string Column(bool[,] mask, int x, int y)
        {
            bool w = Get(mask, x - 1, y);
            bool e = Get(mask, x + 1, y);
            return (w && e ? 1 : (!w && !e ? 3 : (!w ? 0 : 2))).ToString();
        }

        // ------------------------------------------------------------------ assets

        private static void SetupImporters()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { ArtFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                string name = Path.GetFileNameWithoutExtension(path);
                Vector2 pivot = name.StartsWith("Tree_") ? new Vector2(0.5f, 0.12f)
                    : (name == "Deco_16" || name == "Deco_17") ? new Vector2(0.5f, 0.1f)
                    : new Vector2(0.5f, 0.5f);

                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);
                bool changed = importer.textureType != TextureImporterType.Sprite ||
                    importer.spriteImportMode != SpriteImportMode.Single ||
                    !Mathf.Approximately(importer.spritePixelsPerUnit, 64f) ||
                    importer.filterMode != FilterMode.Point ||
                    importer.textureCompression != TextureImporterCompression.Uncompressed ||
                    settings.spriteAlignment != (int)SpriteAlignment.Custom ||
                    settings.spritePivot != pivot;

                if (!changed)
                {
                    continue;
                }

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

        private static readonly Dictionary<string, TileBase> Tiles = new Dictionary<string, TileBase>();

        private static TileBase TileFor(string spriteName)
        {
            if (Tiles.TryGetValue(spriteName, out TileBase cached) && cached != null)
            {
                return cached;
            }

            string tilePath = TileFolder + "/" + spriteName + ".asset";
            var tile = AssetDatabase.LoadAssetAtPath<Tile>(tilePath);
            if (tile == null)
            {
                Sprite sprite = SpriteFor(spriteName);
                tile = ScriptableObject.CreateInstance<Tile>();
                tile.sprite = sprite;
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
            string path = ArtFolder + "/" + name + ".png";
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

        // ------------------------------------------------------------------ exportación

        private static void ExportAndReport()
        {
            Tilemap collision = GameObject.Find("Collision").GetComponent<Tilemap>();
            MapCollisionGrid grid = TilemapCollision.Build(collision);
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "avelora-valle.avmap"));
            MapFile.Save(path, grid);

            // Comprobación: cuánto suelo se alcanza a pie desde la aparición y si cada claro lo está.
            var reachable = new bool[Width, Height];
            var queue = new Queue<Vector2Int>();
            var start = new Vector2Int(Mathf.FloorToInt(Spawn.x), Mathf.FloorToInt(Spawn.y));
            queue.Enqueue(start);
            Set(reachable, start.x, start.y, true);
            int count = 0;
            while (queue.Count > 0)
            {
                Vector2Int c = queue.Dequeue();
                count++;
                foreach (Vector2Int d in new[] { Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right })
                {
                    Vector2Int n = c + d;
                    if (Inside(n.x, n.y) && !Get(reachable, n.x, n.y) && !IsBlocked(n.x, n.y))
                    {
                        Set(reachable, n.x, n.y, true);
                        queue.Enqueue(n);
                    }
                }
            }

            int walkable = 0;
            ForEach((x, y) => { if (!IsBlocked(x, y)) walkable++; });

            var unreachable = new List<string>();
            foreach (Vector3Int c in Clearings)
            {
                if (!Get(reachable, c.x, c.y))
                {
                    unreachable.Add(c.x + "," + c.y);
                }
            }

            Debug.Log(
                "[Avelora] avelora-valle.avmap: " + grid.Width + "x" + grid.Height + " origen (" + grid.OriginX + "," +
                grid.OriginY + "), huella " + MapFile.ComputeContentHash(grid) + ". Suelo " + walkable +
                ", alcanzable " + count + ". Claros sin acceso: " + (unreachable.Count == 0 ? "ninguno" : string.Join(" ", unreachable)) +
                "\n-> " + path);
        }

        // ------------------------------------------------------------------ utilidades

        private static bool Inside(int x, int y) => x >= MinX && x < MinX + Width && y >= MinY && y < MinY + Height;

        private static bool Get(bool[,] a, int x, int y) => Inside(x, y) && a[x - MinX, y - MinY];

        private static void Set(bool[,] a, int x, int y, bool value)
        {
            if (Inside(x, y))
            {
                a[x - MinX, y - MinY] = value;
            }
        }

        private static void ForEach(Action<int, int> action)
        {
            for (int y = MinY; y < MinY + Height; y++)
            {
                for (int x = MinX; x < MinX + Width; x++)
                {
                    action(x, y);
                }
            }
        }

        private static void Rect(int x0, int y0, int x1, int y1, Action<int, int> action)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    action(x, y);
                }
            }
        }

        private static void Ellipse(int cx, int cy, float rx, float ry, Action<int, int> action)
        {
            for (int y = cy - (int)ry - 1; y <= cy + (int)ry + 1; y++)
            {
                for (int x = cx - (int)rx - 1; x <= cx + (int)rx + 1; x++)
                {
                    float dx = (x - cx) / rx;
                    float dy = (y - cy) / ry;
                    if ((dx * dx) + (dy * dy) <= 1f)
                    {
                        action(x, y);
                    }
                }
            }
        }

        private static int RiverAt(int y) => Inside(MinX, y) ? _riverX[y - MinY] : 0;

        private static bool IsRiver(int x, int y) => y >= -16 && Math.Abs(x - RiverAt(y)) <= 1;

        private static bool NearWater(int x, int y, int r) => Near(x, y, r, (a, b) => Inside(a, b) && !Get(_land, a, b));

        private static bool NearLand(int x, int y, int r) => Near(x, y, r, (a, b) => Get(_land, a, b));

        private static bool NearSand(int x, int y, int r) => Near(x, y, r, (a, b) => Get(_sand, a, b));

        private static bool NearRock(int x, int y) => Near(x, y, 1, (a, b) => Get(_rock, a, b) || Get(_cliff, a, b));

        private static bool NearClearing(int x, int y, int margin)
        {
            foreach (Vector3Int c in Clearings)
            {
                if (Distance(x, y, c.x, c.y) <= c.z + margin)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Near(int x, int y, int r, Func<int, int, bool> test)
        {
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if ((dx != 0 || dy != 0) && test(x + dx, y + dy))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static float Distance(int x, int y, int cx, int cy) => Mathf.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy)));

        private static float Interpolate(int[] keys, int[] values, int y)
        {
            // keys va de mayor a menor.
            if (y >= keys[0]) return values[0];
            for (int i = 1; i < keys.Length; i++)
            {
                if (y >= keys[i])
                {
                    float t = (float)(y - keys[i]) / (keys[i - 1] - keys[i]);
                    return Mathf.Lerp(values[i], values[i - 1], t);
                }
            }

            return values[values.Length - 1];
        }

        /// <summary>Ruido de valor determinista en [0, 1], interpolado entre puntos de una red.</summary>
        private static float Noise(int x, int y, float scale, int channel)
        {
            float fx = x / scale;
            float fy = y / scale;
            int x0 = Mathf.FloorToInt(fx);
            int y0 = Mathf.FloorToInt(fy);
            float tx = Smooth(fx - x0);
            float ty = Smooth(fy - y0);
            float a = Mathf.Lerp(Hash(x0, y0, channel), Hash(x0 + 1, y0, channel), tx);
            float b = Mathf.Lerp(Hash(x0, y0 + 1, channel), Hash(x0 + 1, y0 + 1, channel), tx);
            return Mathf.Lerp(a, b, ty);
        }

        private static float Smooth(float t) => t * t * (3f - (2f * t));

        private static float Hash(int x, int y, int channel)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + channel * 2147483647 + Seed * 144665);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0xFFFFFF;
            }
        }
    }
}
