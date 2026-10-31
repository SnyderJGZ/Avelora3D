#nullable enable
using System;

namespace Avelora.Shared
{
    /// <summary>
    /// Matriz de colisión del mapa: una rejilla de tiles donde cada celda está libre o bloqueada.
    /// </summary>
    /// <remarks>
    /// El servidor <b>no</b> ejecuta un motor de física (Box2D/PhysX); usa esta matriz más cajas
    /// AABB (Avelora, plan del servidor §5 y guía del núcleo §6). Cliente y servidor cargan
    /// <b>exactamente el mismo</b> mapa exportado desde el Tilemap, de modo que la predicción
    /// del cliente y la verdad del servidor coinciden.
    ///
    /// El origen (<see cref="OriginX"/>/<see cref="OriginY"/>) guarda la coordenada de tile de
    /// la celda <c>(0, 0)</c> de la rejilla. Sirve para mapas cuyo Tilemap tiene celdas
    /// negativas: la exportación las desplaza a (0,0) y anota aquí el desplazamiento aplicado
    /// (guía del núcleo §6.3). Las consultas usan <b>coordenadas de tile absolutas</b>, así que
    /// el resto del código no tiene que saber nada del desplazamiento.
    /// </remarks>
    public sealed class MapCollisionGrid
    {
        private readonly bool[] _blocked;

        public MapCollisionGrid(int width, int height, bool[] blocked, int originX = 0, int originY = 0)
        {
            if (width <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width), width, "El ancho debe ser positivo.");
            }

            if (height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(height), height, "El alto debe ser positivo.");
            }

            if (blocked is null)
            {
                throw new ArgumentNullException(nameof(blocked));
            }

            if (blocked.Length != (long)width * height)
            {
                throw new ArgumentException(
                    "El tamaño de 'blocked' (" + blocked.Length + ") no coincide con width*height (" + ((long)width * height) + ").",
                    nameof(blocked));
            }

            Width = width;
            Height = height;
            OriginX = originX;
            OriginY = originY;
            _blocked = blocked;
        }

        public int Width { get; }

        public int Height { get; }

        /// <summary>Coordenada X del tile que ocupa la celda (0,0) de la rejilla.</summary>
        public int OriginX { get; }

        /// <summary>Coordenada Y del tile que ocupa la celda (0,0) de la rejilla.</summary>
        public int OriginY { get; }

        public int TileCount => Width * Height;

        /// <summary>Buffer plano (fila a fila: <c>y * Width + x</c>). Uso interno de la capa de mapas.</summary>
        internal bool[] BlockedTiles => _blocked;

        /// <summary>
        /// ¿Está bloqueado el tile <paramref name="tileX"/>,<paramref name="tileY"/>?
        /// </summary>
        /// <remarks>
        /// Las coordenadas son <b>absolutas</b>: las mismas que las del Tilemap de Unity y, en
        /// general, las del mundo (1 unidad = 1 tile). El origen de la rejilla se resta aquí,
        /// de modo que un mapa exportado con celdas negativas funciona sin que el cliente ni el
        /// servidor tengan que desplazar nada (guía del núcleo §6.3).
        ///
        /// Fuera de la rejilla cuenta como bloqueado, para que nadie salga del mapa por un borde
        /// (guía del núcleo §6.2).
        /// </remarks>
        public bool IsTileBlocked(int tileX, int tileY)
        {
            long x = (long)tileX - OriginX;
            long y = (long)tileY - OriginY;
            if (x < 0 || y < 0 || x >= Width || y >= Height)
            {
                return true;
            }

            return _blocked[(int)((y * Width) + x)];
        }

        /// <summary>¿Hay vista libre del centro <paramref name="from"/> al centro <paramref name="to"/>?</summary>
        public bool HasLineOfSight(Vec2 from, Vec2 to)
        {
            if (float.IsNaN(from.X) || float.IsInfinity(from.X) ||
                float.IsNaN(from.Y) || float.IsInfinity(from.Y) ||
                float.IsNaN(to.X) || float.IsInfinity(to.X) ||
                float.IsNaN(to.Y) || float.IsInfinity(to.Y))
            {
                return false;
            }

            double startX = Math.Floor(from.X);
            double startY = Math.Floor(from.Y);
            double endX = Math.Floor(to.X);
            double endY = Math.Floor(to.Y);
            if (startX < int.MinValue || startX > int.MaxValue ||
                startY < int.MinValue || startY > int.MaxValue ||
                endX < int.MinValue || endX > int.MaxValue ||
                endY < int.MinValue || endY > int.MaxValue)
            {
                return false;
            }

            int x = (int)startX;
            int y = (int)startY;
            int endTileX = (int)endX;
            int endTileY = (int)endY;
            if (IsTileBlocked(x, y))
            {
                return false;
            }

            double dx = (double)to.X - from.X;
            double dy = (double)to.Y - from.Y;
            int stepX = dx > 0d ? 1 : dx < 0d ? -1 : 0;
            int stepY = dy > 0d ? 1 : dy < 0d ? -1 : 0;
            double tDeltaX = stepX != 0 ? 1d / Math.Abs(dx) : double.PositiveInfinity;
            double tDeltaY = stepY != 0 ? 1d / Math.Abs(dy) : double.PositiveInfinity;
            double tMaxX = stepX > 0 ? (x + 1d - from.X) / dx
                : stepX < 0 ? (from.X - x) / -dx
                : double.PositiveInfinity;
            double tMaxY = stepY > 0 ? (y + 1d - from.Y) / dy
                : stepY < 0 ? (from.Y - y) / -dy
                : double.PositiveInfinity;

            while (x != endTileX || y != endTileY)
            {
                if (x == endTileX)
                {
                    y += stepY;
                    tMaxY += tDeltaY;
                }
                else if (y == endTileY)
                {
                    x += stepX;
                    tMaxX += tDeltaX;
                }
                else if (tMaxX < tMaxY)
                {
                    x += stepX;
                    tMaxX += tDeltaX;
                }
                else if (tMaxY < tMaxX)
                {
                    y += stepY;
                    tMaxY += tDeltaY;
                }
                else
                {
                    // La regla conservadora evita disparar por la rendija diagonal entre dos paredes.
                    if (IsTileBlocked(x + stepX, y) || IsTileBlocked(x, y + stepY))
                    {
                        return false;
                    }

                    x += stepX;
                    y += stepY;
                    tMaxX += tDeltaX;
                    tMaxY += tDeltaY;
                }

                if (IsTileBlocked(x, y))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Comprueba <b>todos</b> los tiles que toca la caja (AABB) centrada en <paramref name="center"/>
        /// con semiancho/semialto <paramref name="half"/>. No basta con el punto central
        /// (guía del núcleo §6.2).
        /// </summary>
        public bool IsBoxWalkable(Vec2 center, Vec2 half)
        {
            int minX = (int)MathF.Floor(center.X - half.X);
            int maxX = (int)MathF.Floor(center.X + half.X);
            int minY = (int)MathF.Floor(center.Y - half.Y);
            int maxY = (int)MathF.Floor(center.Y + half.Y);

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (IsTileBlocked(x, y))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// ¿Se solapan dos cajas AABB? Se usa para contacto y alcance entre entidades
        /// (guía del núcleo §6.4).
        /// </summary>
        public static bool Overlaps(Vec2 a, Vec2 aHalf, Vec2 b, Vec2 bHalf) =>
            MathF.Abs(a.X - b.X) <= aHalf.X + bHalf.X &&
            MathF.Abs(a.Y - b.Y) <= aHalf.Y + bHalf.Y;

        /// <summary>Crea un mapa totalmente transitable (útil en pruebas y escenas vacías).</summary>
        public static MapCollisionGrid AllWalkable(int width, int height) =>
            new MapCollisionGrid(width, height, new bool[width * height]);

        /// <summary>
        /// Construye la rejilla desde filas de texto, cómodo para pruebas y herramientas:
        /// <c>rows[y]</c> es la fila <c>y</c> y <c>rows[y][x]</c> la columna <c>x</c>.
        /// El carácter <paramref name="blockedChar"/> marca tile bloqueado; cualquier otro, libre.
        /// </summary>
        public static MapCollisionGrid FromRows(string[] rows, char blockedChar = '#')
        {
            if (rows is null || rows.Length == 0)
            {
                throw new ArgumentException("Se necesita al menos una fila.", nameof(rows));
            }

            int height = rows.Length;
            int width = rows[0].Length;
            var blocked = new bool[width * height];

            for (int y = 0; y < height; y++)
            {
                if (rows[y].Length != width)
                {
                    throw new ArgumentException("Todas las filas deben tener la misma longitud.", nameof(rows));
                }

                for (int x = 0; x < width; x++)
                {
                    blocked[(y * width) + x] = rows[y][x] == blockedChar;
                }
            }

            return new MapCollisionGrid(width, height, blocked);
        }
    }
}
