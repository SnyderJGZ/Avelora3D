using Avelora.Shared;
using Xunit;

namespace Avelora.Tests
{
    public class MovementSimulatorTests
    {
        private const float Speed = 5f;   // unidades por segundo
        private const float Dt = 0.05f;   // 20 ticks/s
        private static readonly Vec2 Half = new Vec2(0.4f, 0.4f);

        [Fact]
        public void Sin_Direccion_No_Se_Mueve()
        {
            var map = MapCollisionGrid.AllWalkable(8, 8);
            var pos = new Vec2(3f, 3f);

            var result = MovementSimulator.Step(pos, 0, 0, Speed, Dt, Half, map);

            Assert.Equal(pos, result);
        }

        [Fact]
        public void Avanza_Exactamente_Speed_Por_Dt_En_Recto()
        {
            var map = MapCollisionGrid.AllWalkable(8, 8);
            var pos = new Vec2(3f, 3f);

            var result = MovementSimulator.Step(pos, 1, 0, Speed, Dt, Half, map);

            Assert.Equal(3.25f, result.X, 5);
            Assert.Equal(3f, result.Y, 5);
        }

        [Fact]
        public void La_Diagonal_Se_Normaliza_Y_No_Es_Mas_Rapida()
        {
            var map = MapCollisionGrid.AllWalkable(8, 8);
            var start = new Vec2(3f, 3f);

            var result = MovementSimulator.Step(start, 1, 1, Speed, Dt, Half, map);

            // La distancia recorrida es la misma que en recto: speed * dt.
            float traveled = (result - start).Length;
            Assert.Equal(Speed * Dt, traveled, 5);
        }

        [Fact]
        public void Una_Pared_Frena_Un_Eje_Pero_Deja_Deslizar_El_Otro()
        {
            // Columna x = 1 bloqueada de arriba abajo.
            var map = MapCollisionGrid.FromRows(new[]
            {
                ".#.",
                ".#.",
                ".#.",
                ".#.",
                ".#.",
            });

            var start = new Vec2(0.5f, 2.5f);
            var result = MovementSimulator.Step(start, 1, 1, Speed, Dt, Half, map);

            // El avance en X choca con la pared: se descarta.
            Assert.Equal(0.5f, result.X, 5);
            // El avance en Y sí se aplica: el personaje se desliza pared arriba.
            Assert.Equal(2.5f + (Speed * Dt / 1.4142135f), result.Y, 4);
        }

        [Fact]
        public void Contra_Una_Pared_Frontal_Se_Queda_Quieto()
        {
            // Dos columnas: libre en x=0, bloqueada en x=1.
            var map = MapCollisionGrid.FromRows(new[]
            {
                ".#",
                ".#",
                ".#",
            });

            var pos = new Vec2(0.5f, 1.5f);

            // Muchos pasos empujando hacia la pared: nunca la atraviesa.
            for (int i = 0; i < 100; i++)
            {
                pos = MovementSimulator.Step(pos, 1, 0, Speed, Dt, Half, map);
            }

            Assert.Equal(0.5f, pos.X, 5);
            Assert.True(pos.X < 1f);
        }

        [Fact]
        public void El_Resultado_Es_Determinista()
        {
            var map = MapCollisionGrid.FromRows(new[]
            {
                "....",
                ".#..",
                "..#.",
                "....",
            });

            var start = new Vec2(0.5f, 0.5f);

            var a = MovementSimulator.Step(start, 1, 1, Speed, Dt, Half, map);
            var b = MovementSimulator.Step(start, 1, 1, Speed, Dt, Half, map);

            Assert.Equal(a, b);
        }

        [Theory]
        [InlineData(-1, 0, true)]
        [InlineData(0, 0, true)]
        [InlineData(1, -1, true)]
        [InlineData(2, 0, false)]
        [InlineData(0, -3, false)]
        public void IsValidDirection_Acepta_Solo_Menos1_Cero_O_Uno(int dx, int dy, bool valid)
        {
            Assert.Equal(valid, MovementSimulator.IsValidDirection((sbyte)dx, (sbyte)dy));
        }
    }
}
