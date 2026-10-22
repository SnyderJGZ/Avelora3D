#nullable enable
using System;

namespace Avelora.Shared
{
    /// <summary>
    /// Simulador de movimiento compartido: <b>la misma función</b> corre en el cliente (para
    /// predecir) y en el servidor (para decidir la verdad) — guía del núcleo §5.1.
    /// </summary>
    /// <remarks>
    /// Reglas que hay que respetar para que ambos lados coincidan:
    /// <list type="bullet">
    /// <item>El <i>dt</i> es constante (1 / tick rate), nunca calculado del reloj.</item>
    /// <item>La velocidad la impone el servidor; el cliente nunca la envía.</item>
    /// <item>La normalización de la diagonal se hace <b>aquí</b>, no en el cliente.</item>
    /// <item>Se mueve cada eje por separado, para deslizarse por las paredes en vez de
    /// quedarse pegado.</item>
    /// <item>Si el paso completo no cabe, se avanza <b>solo hasta el borde</b> del tile que
    /// bloquea: el jugador toca la pared en vez de quedarse hasta un paso (0,2 tiles) antes.
    /// Así el servidor (posición exacta) y el cliente (base cuantizada a 1/64 de tile) acaban en
    /// el mismo borde y la reconciliación no inventa correcciones al rozar una pared (G-04).</item>
    /// </list>
    /// Las operaciones son simples (sumas, multiplicaciones y una raíz) para favorecer el
    /// determinismo entre plataformas (plan del servidor §5, «detalle técnico a vigilar»).
    /// </remarks>
    public static class MovementSimulator
    {
        /// <summary>
        /// Avanza la posición un paso fijo.
        /// </summary>
        /// <param name="pos">Posición actual.</param>
        /// <param name="dirX">Dirección en X: -1, 0 o 1.</param>
        /// <param name="dirY">Dirección en Y: -1, 0 o 1.</param>
        /// <param name="speed">Velocidad en unidades por segundo (viene de datos del servidor).</param>
        /// <param name="dt">Paso de tiempo fijo en segundos.</param>
        /// <param name="halfSize">Semiancho y semialto de la caja de colisión.</param>
        /// <param name="map">Matriz de colisión del mapa.</param>
        /// <returns>La posición resultante tras el paso.</returns>
        public static Vec2 Step(
            Vec2 pos,
            sbyte dirX,
            sbyte dirY,
            float speed,
            float dt,
            Vec2 halfSize,
            MapCollisionGrid map)
        {
            if (map is null)
            {
                throw new ArgumentNullException(nameof(map));
            }

            float len = MathF.Sqrt((dirX * dirX) + (dirY * dirY));
            if (len == 0f)
            {
                // Sin dirección no hay movimiento (el jugador se queda quieto).
                return pos;
            }

            // Normaliza la diagonal: moverse en diagonal no es más rápido que en recto.
            float step = speed * dt / len;

            // Eje X primero; si choca, se avanza hasta la pared y se conserva el avance en Y.
            pos = MoveAlongX(pos, dirX * step, halfSize, map);

            // Eje Y sobre el resultado del eje X (permite deslizarse por las paredes).
            return MoveAlongY(pos, dirY * step, halfSize, map);
        }

        /// <summary>
        /// ¿Es <paramref name="dirX"/>/<paramref name="dirY"/> una dirección de entrada válida?
        /// Solo se admiten -1, 0 y 1 (guía del núcleo §4.3: direcciones discretas, sin flotantes).
        /// </summary>
        public static bool IsValidDirection(sbyte dirX, sbyte dirY) =>
            dirX >= -1 && dirX <= 1 && dirY >= -1 && dirY <= 1;

        /// <summary>
        /// Avanza el eje X: el paso completo si cabe, o hasta el borde del tile que lo bloquea.
        /// </summary>
        private static Vec2 MoveAlongX(Vec2 pos, float delta, Vec2 half, MapCollisionGrid map)
        {
            if (delta == 0f)
            {
                return pos;
            }

            var target = new Vec2(pos.X + delta, pos.Y);
            if (map.IsBoxWalkable(target, half))
            {
                return target;
            }

            int overlap = CountBlockedTiles(map, pos, half);
            if (overlap > 0)
            {
                // La caja ya solapa una pared (una base cuantizada puede quedar 1/128 dentro).
                // No hay un borde del que fiarse ni se inventa un retroceso: se acepta el paso
                // solo si reduce el solape, que es justo el movimiento que libera al jugador.
                return CountBlockedTiles(map, target, half) < overlap ? target : pos;
            }

            int minRow = (int)MathF.Floor(pos.Y - half.Y);
            int maxRow = (int)MathF.Floor(pos.Y + half.Y);

            if (delta > 0f)
            {
                int last = (int)MathF.Floor(target.X + half.X);

                for (int column = (int)MathF.Floor(pos.X + half.X); column <= last; column++)
                {
                    if (!ColumnBlocked(map, column, minRow, maxRow))
                    {
                        continue;
                    }

                    // El borde izquierdo del tile bloqueado es el límite; el centro se queda un
                    // paso de máquina antes para que el floor() de la caja no incluya el tile.
                    float limit = BelowTileEdge(column - half.X, map, pos, half, horizontal: true);
                    float x = MathF.Min(target.X, limit);

                    return x > pos.X ? new Vec2(x, pos.Y) : pos;
                }

                return pos;
            }

            int leftmost = (int)MathF.Floor(target.X - half.X);

            for (int column = (int)MathF.Floor(pos.X - half.X); column >= leftmost; column--)
            {
                if (!ColumnBlocked(map, column, minRow, maxRow))
                {
                    continue;
                }

                // Al ir hacia la izquierda el límite es el borde derecho del tile bloqueado, y
                // ese valor ya deja la caja fuera (el tile ocupa [x, x+1)). El redondeo puede
                // dejarla una ulp dentro, así que se sube lo justo hasta que sea transitable.
                float limit = AboveTileEdge(column + 1f + half.X, map, pos, half, horizontal: true);
                float x = MathF.Max(target.X, limit);

                return x < pos.X ? new Vec2(x, pos.Y) : pos;
            }

            return pos;
        }

        /// <summary>
        /// Avanza el eje Y: el paso completo si cabe, o hasta el borde del tile que lo bloquea.
        /// </summary>
        private static Vec2 MoveAlongY(Vec2 pos, float delta, Vec2 half, MapCollisionGrid map)
        {
            if (delta == 0f)
            {
                return pos;
            }

            var target = new Vec2(pos.X, pos.Y + delta);
            if (map.IsBoxWalkable(target, half))
            {
                return target;
            }

            int overlap = CountBlockedTiles(map, pos, half);
            if (overlap > 0)
            {
                return CountBlockedTiles(map, target, half) < overlap ? target : pos;
            }

            int minColumn = (int)MathF.Floor(pos.X - half.X);
            int maxColumn = (int)MathF.Floor(pos.X + half.X);

            if (delta > 0f)
            {
                int last = (int)MathF.Floor(target.Y + half.Y);

                for (int row = (int)MathF.Floor(pos.Y + half.Y); row <= last; row++)
                {
                    if (!RowBlocked(map, row, minColumn, maxColumn))
                    {
                        continue;
                    }

                    float limit = BelowTileEdge(row - half.Y, map, pos, half, horizontal: false);
                    float y = MathF.Min(target.Y, limit);

                    return y > pos.Y ? new Vec2(pos.X, y) : pos;
                }

                return pos;
            }

            int bottomMost = (int)MathF.Floor(target.Y - half.Y);

            for (int row = (int)MathF.Floor(pos.Y - half.Y); row >= bottomMost; row--)
            {
                if (!RowBlocked(map, row, minColumn, maxColumn))
                {
                    continue;
                }

                float limit = row + 1f + half.Y;
                limit = AboveTileEdge(limit, map, pos, half, horizontal: false);
                float y = MathF.Max(target.Y, limit);

                return y < pos.Y ? new Vec2(pos.X, y) : pos;
            }

            return pos;
        }

        /// <summary>
        /// Baja <paramref name="limit"/> los pasos de máquina necesarios para que la caja deje de
        /// incluir el tile bloqueado cuyo borde es <paramref name="limit"/>. Tocar el borde con el
        /// <c>floor()</c> de <see cref="MapCollisionGrid.IsBoxWalkable"/> todavía cuenta como
        /// dentro, así que el valor exacto no vale.
        /// </summary>
        /// <param name="limit">Centro candidato: borde del tile menos el semiancho de la caja.</param>
        /// <param name="map">Matriz de colisión.</param>
        /// <param name="pos">Posición actual (el otro eje no cambia).</param>
        /// <param name="half">Semiancho y semialto de la caja.</param>
        /// <param name="horizontal">Cierto si se está moviendo en X; falso si es en Y.</param>
        private static float BelowTileEdge(
            float limit,
            MapCollisionGrid map,
            Vec2 pos,
            Vec2 half,
            bool horizontal)
        {
            for (int i = 0; i < 4; i++)
            {
                Vec2 candidate = horizontal
                    ? new Vec2(limit, pos.Y)
                    : new Vec2(pos.X, limit);

                if (map.IsBoxWalkable(candidate, half))
                {
                    return limit;
                }

                limit = PreviousFloat(limit);
            }

            return limit;
        }

        /// <summary>
        /// Sube <paramref name="limit"/> los pasos de máquina necesarios para que la caja deje de
        /// incluir el tile bloqueado. Es el caso simétrico de <see cref="BelowTileEdge"/> cuando se
        /// avanza hacia la izquierda o hacia abajo: el borde calculado puede quedar una ulp dentro
        /// por el redondeo de <c>borde + semiancho − semiancho</c>.
        /// </summary>
        /// <param name="limit">Centro candidato: borde del tile más el semiancho de la caja.</param>
        /// <param name="map">Matriz de colisión.</param>
        /// <param name="pos">Posición actual (el otro eje no cambia).</param>
        /// <param name="half">Semiancho y semialto de la caja.</param>
        /// <param name="horizontal">Cierto si se está moviendo en X; falso si es en Y.</param>
        private static float AboveTileEdge(
            float limit,
            MapCollisionGrid map,
            Vec2 pos,
            Vec2 half,
            bool horizontal)
        {
            for (int i = 0; i < 4; i++)
            {
                Vec2 candidate = horizontal
                    ? new Vec2(limit, pos.Y)
                    : new Vec2(pos.X, limit);

                if (map.IsBoxWalkable(candidate, half))
                {
                    return limit;
                }

                limit = NextFloat(limit);
            }

            return limit;
        }

        /// <summary>El menor <c>float</c> mayor que <paramref name="value"/>.</summary>
        private static float NextFloat(float value)
        {
            if (value == 0f || float.IsNaN(value) || float.IsInfinity(value))
            {
                return value;
            }

            int bits = BitConverter.SingleToInt32Bits(value);
            bits = value > 0f ? bits + 1 : bits - 1;
            return BitConverter.Int32BitsToSingle(bits);
        }

        /// <summary>El mayor <c>float</c> menor que <paramref name="value"/>.</summary>
        private static float PreviousFloat(float value)
        {
            if (value == 0f || float.IsNaN(value) || float.IsInfinity(value))
            {
                return value;
            }

            int bits = BitConverter.SingleToInt32Bits(value);
            bits = value > 0f ? bits - 1 : bits + 1;
            return BitConverter.Int32BitsToSingle(bits);
        }

        private static int CountBlockedTiles(MapCollisionGrid map, Vec2 center, Vec2 half)
        {
            int minX = (int)MathF.Floor(center.X - half.X);
            int maxX = (int)MathF.Floor(center.X + half.X);
            int minY = (int)MathF.Floor(center.Y - half.Y);
            int maxY = (int)MathF.Floor(center.Y + half.Y);
            int blocked = 0;

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (map.IsTileBlocked(x, y))
                    {
                        blocked++;
                    }
                }
            }

            return blocked;
        }

        private static bool ColumnBlocked(MapCollisionGrid map, int column, int minRow, int maxRow)
        {
            for (int row = minRow; row <= maxRow; row++)
            {
                if (map.IsTileBlocked(column, row))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool RowBlocked(MapCollisionGrid map, int row, int minColumn, int maxColumn)
        {
            for (int column = minColumn; column <= maxColumn; column++)
            {
                if (map.IsTileBlocked(column, row))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
