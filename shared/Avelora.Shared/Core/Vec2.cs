#nullable enable
using System;

namespace Avelora.Shared
{
    /// <summary>
    /// Vector 2D de posición o desplazamiento.
    /// </summary>
    /// <remarks>
    /// Es un tipo de valor (<c>readonly struct</c>): no crea basura en el heap, algo crítico
    /// porque este tipo se usa dentro del bucle de ticks (Avelora, plan del servidor §9:
    /// «dentro del bucle de ticks, evitar crear objetos nuevos en cada pasada»).
    ///
    /// Vive en <c>Avelora.Shared</c> porque cliente y servidor deben calcular con él
    /// exactamente igual (guía del núcleo §1.3, «regla de oro»).
    /// </remarks>
    public readonly struct Vec2 : IEquatable<Vec2>
    {
        public readonly float X;
        public readonly float Y;

        public Vec2(float x, float y)
        {
            X = x;
            Y = y;
        }

        /// <summary>Vector nulo (0, 0). No mueve a nadie.</summary>
        public static readonly Vec2 Zero = new Vec2(0f, 0f);

        public float LengthSquared => (X * X) + (Y * Y);

        public float Length => MathF.Sqrt(LengthSquared);

        /// <summary>Devuelve una copia normalizada. Si el vector es nulo devuelve <see cref="Zero"/>.</summary>
        public Vec2 Normalized()
        {
            float len = Length;
            return len == 0f ? Zero : new Vec2(X / len, Y / len);
        }

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);

        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);

        public static Vec2 operator *(Vec2 a, float scalar) => new Vec2(a.X * scalar, a.Y * scalar);

        public static Vec2 operator *(float scalar, Vec2 a) => new Vec2(a.X * scalar, a.Y * scalar);

        public static Vec2 operator /(Vec2 a, float scalar) => new Vec2(a.X / scalar, a.Y / scalar);

        public static Vec2 operator -(Vec2 a) => new Vec2(-a.X, -a.Y);

        public static bool operator ==(Vec2 a, Vec2 b) => a.Equals(b);

        public static bool operator !=(Vec2 a, Vec2 b) => !a.Equals(b);

        public bool Equals(Vec2 other) => X == other.X && Y == other.Y;

        public override bool Equals(object? obj) => obj is Vec2 other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (X.GetHashCode() * 397) ^ Y.GetHashCode();
            }
        }

        public override string ToString() =>
            "(" + X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) +
            ", " + Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ")";
    }
}
