#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Avelora.Server.Npcs;
using Avelora.Shared;
using Avelora.Shared.Protocol;

namespace Avelora.Server.Zones
{
    /// <summary>Rectángulo de casillas del archivo zones.json.</summary>
    public sealed class ZoneRectDefinition
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int W { get; set; }
        public int H { get; set; }
    }

    /// <summary>Definición de una zona del archivo zones.json.</summary>
    public sealed class ZoneDefinition
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Kind { get; set; }
        public List<ZoneRectDefinition>? Rects { get; set; } = new List<ZoneRectDefinition>();
    }

    internal sealed class ZoneMapDocument
    {
        public List<ZoneDefinition>? Zones { get; set; } = new List<ZoneDefinition>();
    }

    /// <summary>Índice precalculado de zonas del mapa y colisión exclusiva para la IA (I-15).</summary>
    public sealed class ZoneMap
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        public static readonly ZoneMap Empty = new ZoneMap(
            Array.Empty<ZoneInfo>(), Array.Empty<byte>(), 0, 0, 0, 0, 0, false);

        private readonly byte[] _tileZone;
        private readonly int _originX;
        private readonly int _originY;
        private readonly int _width;
        private readonly int _height;
        private readonly bool _hasSafeZones;

        private ZoneMap(
            ZoneInfo[] zones,
            byte[] tileZone,
            int originX,
            int originY,
            int width,
            int height,
            int rectCount,
            bool hasSafeZones)
        {
            Zones = zones;
            _tileZone = tileZone;
            _originX = originX;
            _originY = originY;
            _width = width;
            _height = height;
            RectCount = rectCount;
            _hasSafeZones = hasSafeZones;
        }

        /// <summary>Zonas en el orden del archivo, preparadas para ZoneList.</summary>
        public ZoneInfo[] Zones { get; }

        /// <summary>Total de rectángulos declarados.</summary>
        public int RectCount { get; }

        /// <summary>Lee y valida zones.json contra las dimensiones y el origen del mapa.</summary>
        public static ZoneMap Load(string path, MapCollisionGrid map)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("La ruta de zones.json no puede estar vacía.", nameof(path));
            }

            if (map is null)
            {
                throw new ArgumentNullException(nameof(map));
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "No se encuentra el archivo de zonas '" + path + "'. Corrige zonesPath o déjalo vacío para jugar sin zonas.",
                    path);
            }

            string json = File.ReadAllText(path);
            ZoneMapDocument? document = JsonSerializer.Deserialize<ZoneMapDocument>(json, JsonOptions);
            if (document == null)
            {
                throw new InvalidOperationException("zones.json está vacío o no contiene un objeto.");
            }

            return FromDefinitions(document.Zones ?? new List<ZoneDefinition>(), map);
        }

        /// <summary>Valida las definiciones y construye la rejilla de consulta en O(1).</summary>
        public static ZoneMap FromDefinitions(IReadOnlyList<ZoneDefinition> zones, MapCollisionGrid map)
        {
            if (zones is null)
            {
                throw new ArgumentNullException(nameof(zones));
            }

            if (map is null)
            {
                throw new ArgumentNullException(nameof(map));
            }

            if (zones.Count > ProtocolConstants.MaxZones)
            {
                int id = zones[ProtocolConstants.MaxZones]?.Id ?? 0;
                throw Error("la zona " + id + " excede el máximo de " + ProtocolConstants.MaxZones + " zonas.");
            }

            var ids = new HashSet<int>();
            var infos = new ZoneInfo[zones.Count];
            int totalRects = 0;
            bool hasSafeZones = false;

            for (int i = 0; i < zones.Count; i++)
            {
                ZoneDefinition? definition = zones[i];
                int id = definition?.Id ?? 0;
                if (definition == null || id < 1 || id > ushort.MaxValue)
                {
                    throw Error("la zona " + id + " tiene un id fuera del rango 1..65535.");
                }

                if (!ids.Add(id))
                {
                    throw Error("la zona " + id + " repite un id.");
                }

                if (string.IsNullOrWhiteSpace(definition.Name))
                {
                    throw Error("la zona " + id + " no tiene name.");
                }

                ZoneKind kind;
                if (string.Equals(definition.Kind, "safe", StringComparison.Ordinal))
                {
                    kind = ZoneKind.Safe;
                    hasSafeZones = true;
                }
                else if (string.Equals(definition.Kind, "pvp", StringComparison.Ordinal))
                {
                    kind = ZoneKind.Pvp;
                }
                else
                {
                    throw Error("la zona " + id + " tiene kind inválido; debe ser safe o pvp.");
                }

                List<ZoneRectDefinition>? definitions = definition.Rects;
                int count = definitions?.Count ?? 0;
                if (count < 1 || count > ProtocolConstants.MaxZoneRectsPerZone)
                {
                    throw Error("la zona " + id + " debe tener entre 1 y " +
                        ProtocolConstants.MaxZoneRectsPerZone + " rectángulos.");
                }

                totalRects += count;
                if (totalRects > ProtocolConstants.MaxZoneRects)
                {
                    throw Error("la zona " + id + " hace que se excedan los " +
                        ProtocolConstants.MaxZoneRects + " rectángulos totales.");
                }

                var rects = new ZoneRect[count];
                for (int r = 0; r < count; r++)
                {
                    ZoneRectDefinition? rect = definitions![r];
                    if (rect == null || rect.W < 1 || rect.H < 1 ||
                        rect.X < short.MinValue || rect.X > short.MaxValue ||
                        rect.Y < short.MinValue || rect.Y > short.MaxValue ||
                        rect.W > short.MaxValue || rect.H > short.MaxValue)
                    {
                        throw Error("la zona " + id + " tiene un rectángulo con coordenadas inválidas.");
                    }

                    long right = (long)rect.X + rect.W;
                    long top = (long)rect.Y + rect.H;
                    long mapRight = (long)map.OriginX + map.Width;
                    long mapTop = (long)map.OriginY + map.Height;
                    if (rect.X < map.OriginX || rect.Y < map.OriginY || right > mapRight || top > mapTop)
                    {
                        throw Error("la zona " + id + " tiene un rectángulo fuera del mapa.");
                    }

                    rects[r] = new ZoneRect((short)rect.X, (short)rect.Y, (short)rect.W, (short)rect.H);
                }

                infos[i] = new ZoneInfo((ushort)id, kind, rects);
            }

            var tileZone = new byte[map.Width * map.Height];
            for (int i = 0; i < infos.Length; i++)
            {
                ZoneInfo zone = infos[i];
                int priority = Priority(zone.Kind);
                for (int r = 0; r < zone.Rects.Length; r++)
                {
                    ZoneRect rect = zone.Rects[r];
                    for (int y = rect.Y; y < rect.Y + rect.Height; y++)
                    {
                        int row = (y - map.OriginY) * map.Width;
                        for (int x = rect.X; x < rect.X + rect.Width; x++)
                        {
                            int tile = row + x - map.OriginX;
                            byte existing = tileZone[tile];
                            if (existing == 0 || priority > Priority(infos[existing - 1].Kind))
                            {
                                tileZone[tile] = (byte)(i + 1);
                            }
                        }
                    }
                }
            }

            return new ZoneMap(
                infos, tileZone, map.OriginX, map.OriginY, map.Width, map.Height, totalRects, hasSafeZones);
        }

        /// <summary>Id de la zona en la casilla que contiene el punto; 0 significa normal.</summary>
        public ushort ZoneIdAt(float x, float y)
        {
            int tile = TileIndexAt(x, y);
            return tile < 0 ? (ushort)0 : Zones[_tileZone[tile] - 1].Id;
        }

        /// <summary>Tipo de la zona en la casilla que contiene el punto.</summary>
        public ZoneKind KindAt(float x, float y)
        {
            int tile = TileIndexAt(x, y);
            return tile < 0 ? ZoneKind.Normal : Zones[_tileZone[tile] - 1].Kind;
        }

        /// <summary>Crea una rejilla donde las casillas seguras también bloquean a los PNJ.</summary>
        public MapCollisionGrid BuildNpcCollision(MapCollisionGrid map)
        {
            if (map is null)
            {
                throw new ArgumentNullException(nameof(map));
            }

            if (!_hasSafeZones)
            {
                return map;
            }

            var blocked = new bool[map.Width * map.Height];
            for (int y = 0; y < map.Height; y++)
            {
                int tileY = map.OriginY + y;
                int row = y * map.Width;
                for (int x = 0; x < map.Width; x++)
                {
                    int tileX = map.OriginX + x;
                    int index = row + x;
                    blocked[index] = map.IsTileBlocked(tileX, tileY) || IsSafeTile(tileX, tileY);
                }
            }

            return new MapCollisionGrid(map.Width, map.Height, blocked, map.OriginX, map.OriginY);
        }

        /// <summary>Impide que el radio de deambular de un spawner alcance una zona segura.</summary>
        public void ValidateSpawners(NpcCatalog npcs)
        {
            if (npcs is null)
            {
                throw new ArgumentNullException(nameof(npcs));
            }

            if (npcs.Spawners == null)
            {
                return;
            }

            for (int i = 0; i < npcs.Spawners.Count; i++)
            {
                NpcSpawner spawner = npcs.Spawners[i];
                NpcType? type = npcs.FindType(spawner.TypeId);
                if (type == null)
                {
                    continue;
                }

                float radius = spawner.WanderRadius + Math.Max(type.HalfWidth, type.HalfHeight);
                int minX = (int)Math.Floor(spawner.X - radius);
                int maxX = (int)Math.Floor(spawner.X + radius);
                int minY = (int)Math.Floor(spawner.Y - radius);
                int maxY = (int)Math.Floor(spawner.Y + radius);
                for (int y = minY; y <= maxY; y++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        if (TryGetTileZone(x, y, out ZoneInfo zone) && zone.Kind == ZoneKind.Safe)
                        {
                            throw Error("el spawner " + (i + 1) + " (tipo " + spawner.TypeId +
                                ") toca la zona segura " + zone.Id + "; muévelo o cambia la zona.");
                        }
                    }
                }
            }
        }

        private bool IsSafeTile(int x, int y) =>
            TryGetTileZone(x, y, out ZoneInfo zone) && zone.Kind == ZoneKind.Safe;

        private bool TryGetTileZone(int x, int y, out ZoneInfo zone)
        {
            long tx = (long)x - _originX;
            long ty = (long)y - _originY;
            if (tx < 0 || ty < 0 || tx >= _width || ty >= _height)
            {
                zone = default;
                return false;
            }

            byte index = _tileZone[(int)(ty * _width + tx)];
            if (index == 0)
            {
                zone = default;
                return false;
            }

            zone = Zones[index - 1];
            return true;
        }

        private int TileIndexAt(float x, float y)
        {
            if (_tileZone.Length == 0 || float.IsNaN(x) || float.IsNaN(y) ||
                float.IsInfinity(x) || float.IsInfinity(y))
            {
                return -1;
            }

            double tileX = Math.Floor(x);
            double tileY = Math.Floor(y);
            if (tileX < int.MinValue || tileX > int.MaxValue || tileY < int.MinValue || tileY > int.MaxValue)
            {
                return -1;
            }

            long tx = (long)(int)tileX - _originX;
            long ty = (long)(int)tileY - _originY;
            if (tx < 0 || ty < 0 || tx >= _width || ty >= _height)
            {
                return -1;
            }

            int index = (int)(ty * _width + tx);
            return _tileZone[index] == 0 ? -1 : index;
        }

        private static int Priority(ZoneKind kind) => kind == ZoneKind.Safe ? 2 : 1;

        private static InvalidOperationException Error(string message) =>
            new InvalidOperationException("zones.json: " + message);
    }
}
