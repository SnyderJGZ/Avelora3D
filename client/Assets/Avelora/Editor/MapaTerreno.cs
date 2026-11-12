using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Avelora.EditorTools
{
    /// <summary>
    /// Terreno «nativo» en rejilla doble (dual grid). Cada terreno tiene un atlas de 16 × 16 casillas
    /// (columna = variante de textura, fila = máscara de esquinas) generado por tools/arte/tileset.py.
    /// </summary>
    /// <remarks>
    /// Las capas visuales van desplazadas media casilla: la casilla visual (x, y) tiene sus esquinas en
    /// los centros de las celdas lógicas (x−1..x, y−1..y). Cada capa pinta su terreno donde la esquina
    /// es de ese terreno o de uno superior, así que basta con 16 máscaras por terreno para cualquier
    /// combinación. La colisión no cambia: sigue en la rejilla lógica.
    /// </remarks>
    public static class MapaTerreno
    {
        public const int Agua = 0;
        public const int Tierra = 1;
        public const int Hierba = 2;
        public const int Empedrado = 3;

        private const string ArtFolder = "Assets/Avelora/Art/Terreno";
        private const string TileFolder = "Assets/Avelora/Tiles/Terreno";
        private static readonly string[] Nombres = { "agua", "tierra", "hierba", "empedrado" };
        private const int Celda = 64;
        private const int Variantes = 16;

        /// <summary>True si los cuatro atlas existen: si no, el mapa sigue con las piezas sueltas.</summary>
        public static bool Disponible()
        {
            for (int t = 0; t < Nombres.Length; t++)
            {
                if (AssetDatabase.LoadAssetAtPath<Texture2D>(AtlasPath(t)) == null) return false;
            }

            return true;
        }

        /// <summary>
        /// Pinta las cuatro capas. <paramref name="terreno"/> da el terreno de cada celda lógica
        /// (x, y) del rectángulo [origenX, origenX + ancho) × [origenY, origenY + alto).
        /// </summary>
        public static void Pintar(Grid grid, int origenX, int origenY, int ancho, int alto, Func<int, int, int> terreno)
        {
            for (int t = 0; t < Nombres.Length; t++)
            {
                TileBase[] tiles = Tiles(t);
                Tilemap capa = Capa(grid, t);
                capa.ClearAllTiles();
                var celdas = new List<Vector3Int>();
                var piezas = new List<TileBase>();
                for (int y = origenY; y <= origenY + alto; y++)
                {
                    for (int x = origenX; x <= origenX + ancho; x++)
                    {
                        int mascara = 0;
                        if (Valor(terreno, x - 1, y, origenX, origenY, ancho, alto) >= t) mascara |= 8;
                        if (Valor(terreno, x, y, origenX, origenY, ancho, alto) >= t) mascara |= 4;
                        if (Valor(terreno, x - 1, y - 1, origenX, origenY, ancho, alto) >= t) mascara |= 2;
                        if (Valor(terreno, x, y - 1, origenX, origenY, ancho, alto) >= t) mascara |= 1;
                        if (mascara == 0) continue;
                        int variante = (x & 3) + 4 * (y & 3);
                        celdas.Add(new Vector3Int(x, y, 0));
                        piezas.Add(tiles[mascara * Variantes + variante]);
                    }
                }

                capa.SetTiles(celdas.ToArray(), piezas.ToArray());
            }
        }

        // Fuera del mapa se repite el borde: así el terreno no se corta en el límite.
        private static int Valor(Func<int, int, int> terreno, int x, int y, int origenX, int origenY, int ancho, int alto) =>
            terreno(Mathf.Clamp(x, origenX, origenX + ancho - 1), Mathf.Clamp(y, origenY, origenY + alto - 1));

        private static Tilemap Capa(Grid grid, int t)
        {
            string nombre = "Terreno_" + t + "_" + Nombres[t];
            Transform existente = grid.transform.Find(nombre);
            GameObject go = existente != null ? existente.gameObject : new GameObject(nombre, typeof(Tilemap), typeof(TilemapRenderer));
            go.transform.SetParent(grid.transform, false);
            // Media casilla: las esquinas de cada casilla visual caen en los centros de las celdas lógicas.
            go.transform.localPosition = new Vector3(-0.5f, -0.5f, 0f);
            go.GetComponent<TilemapRenderer>().sortingOrder = -40 + t;
            return go.GetComponent<Tilemap>();
        }

        private static string AtlasPath(int t) => ArtFolder + "/Terreno_" + t + "_" + Nombres[t] + ".png";

        /// <summary>
        /// Las 256 casillas del atlas como Tile, guardadas como sub-assets de un único .asset por terreno
        /// (índice = máscara × 16 + variante; la máscara 0 queda vacía).
        /// </summary>
        private static TileBase[] Tiles(int t)
        {
            string path = AtlasPath(t);
            PrepararTextura(path);
            var textura = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            string assetPath = TileFolder + "/Terreno_" + Nombres[t] + ".asset";
            var tiles = new TileBase[16 * Variantes];
            // Se rehace siempre: si el atlas se reimporta, los sprites guardados pierden su textura.
            System.IO.Directory.CreateDirectory(TileFolder);
            AssetDatabase.DeleteAsset(assetPath);
            // La primera casilla es el asset principal; el resto y todos los sprites van dentro.
            Tile contenedor = null;
            for (int mascara = 1; mascara < 16; mascara++)
            {
                for (int variante = 0; variante < Variantes; variante++)
                {
                    // El atlas pone la máscara 0 arriba; en la textura de Unity la fila 0 es la de abajo.
                    var rect = new Rect(variante * Celda, (15 - mascara) * Celda, Celda, Celda);
                    Sprite sprite = Sprite.Create(textura, rect, new Vector2(0.5f, 0.5f), Celda, 0, SpriteMeshType.FullRect);
                    sprite.name = Nombres[t] + "_" + mascara + "_" + variante;
                    var tile = ScriptableObject.CreateInstance<Tile>();
                    tile.name = sprite.name;
                    tile.sprite = sprite;
                    tile.colliderType = Tile.ColliderType.None;
                    if (contenedor == null)
                    {
                        contenedor = tile;
                        AssetDatabase.CreateAsset(tile, assetPath);
                    }
                    else
                    {
                        AssetDatabase.AddObjectToAsset(tile, contenedor);
                    }

                    AssetDatabase.AddObjectToAsset(sprite, contenedor);
                    tiles[mascara * Variantes + variante] = tile;
                }
            }

            AssetDatabase.SaveAssets();
            return tiles;
        }

        private static void PrepararTextura(string path)
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            if (importer.textureType == TextureImporterType.Default && importer.filterMode == FilterMode.Point &&
                !importer.mipmapEnabled && importer.textureCompression == TextureImporterCompression.Uncompressed &&
                importer.npotScale == TextureImporterNPOTScale.None)
            {
                return;
            }

            importer.textureType = TextureImporterType.Default;
            importer.filterMode = FilterMode.Point;
            importer.mipmapEnabled = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.alphaIsTransparency = true;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
        }
    }
}
