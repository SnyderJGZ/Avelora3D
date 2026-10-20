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

            // Eje X primero; si choca, se descarta solo el avance en X y se conserva el de Y.
            Vec2 next = new Vec2(pos.X + (dirX * step), pos.Y);
            if (map.IsBoxWalkable(next, halfSize))
            {
                pos = next;
            }

            // Eje Y sobre el resultado del eje X (permite deslizarse por las paredes).
            next = new Vec2(pos.X, pos.Y + (dirY * step));
            if (map.IsBoxWalkable(next, halfSize))
            {
                pos = next;
            }

            return pos;
        }

        /// <summary>
        /// ¿Es <paramref name="dirX"/>/<paramref name="dirY"/> una dirección de entrada válida?
        /// Solo se admiten -1, 0 y 1 (guía del núcleo §4.3: direcciones discretas, sin flotantes).
        /// </summary>
        public static bool IsValidDirection(sbyte dirX, sbyte dirY) =>
            dirX >= -1 && dirX <= 1 && dirY >= -1 && dirY <= 1;
    }
}
