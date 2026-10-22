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

            // El avance en X choca con la pared: se acerca hasta tocarla (centro 1 − 0,4) y no
            // se queda a un paso de ella.
            Assert.Equal(0.6f, result.X, 4);
            // El avance en Y sí se aplica entero: el personaje se desliza pared arriba.
            Assert.Equal(2.5f + (Speed * Dt / 1.4142135f), result.Y, 4);
            Assert.True(map.IsBoxWalkable(result, Half));
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

            // Se queda pegado al borde del tile bloqueado, no a un paso de él.
            Assert.Equal(0.6f, pos.X, 4);
            Assert.True(pos.X < 1f);
            Assert.True(map.IsBoxWalkable(pos, Half));
        }

        [Fact]
        public void Se_Acerca_Hasta_La_Pared_En_X_Aunque_El_Paso_No_Quepa()
        {
            var map = MapCollisionGrid.FromRows(new[]
            {
                ".#",
                ".#",
                ".#",
            });

            // Paso de 0,25 y solo 0,10 hasta la pared (centro máximo 0,6).
            var result = MovementSimulator.Step(new Vec2(0.5f, 1f), 1, 0, Speed, Dt, Half, map);

            Assert.Equal(0.6f, result.X, 4);
            Assert.Equal(1f, result.Y, 5);
            Assert.True(result.X < 0.5f + (Speed * Dt), "No puede avanzar más que el paso.");
            Assert.True(map.IsBoxWalkable(result, Half));
        }

        [Fact]
        public void Se_Acerca_Hasta_La_Pared_En_Y_Aunque_El_Paso_No_Quepa()
        {
            var map = MapCollisionGrid.FromRows(new[]
            {
                "...",
                "...",
                "###",
            });

            // El techo está en la fila 2: el centro máximo es 2 − 0,4 = 1,6.
            var result = MovementSimulator.Step(new Vec2(1.5f, 1.5f), 0, 1, Speed, Dt, Half, map);

            Assert.Equal(1.5f, result.X, 5);
            Assert.Equal(1.6f, result.Y, 4);
            Assert.True(result.Y < 1.5f + (Speed * Dt), "No puede avanzar más que el paso.");
            Assert.True(map.IsBoxWalkable(result, Half));
        }

        [Fact]
        public void En_Diagonal_Se_Acerca_A_La_Esquina_Sin_Atravesarla()
        {
            // Esquina interior: libre hasta x = 2 e y = 2.
            var map = MapCollisionGrid.FromRows(new[]
            {
                "..##",
                "..##",
                "####",
                "####",
            });

            var result = MovementSimulator.Step(new Vec2(1.5f, 1.5f), 1, 1, Speed, Dt, Half, map);

            Assert.Equal(1.6f, result.X, 4);
            Assert.Equal(1.6f, result.Y, 4);
            Assert.True(map.IsBoxWalkable(result, Half));
        }

        [Fact]
        public void Pegado_A_La_Pared_No_Se_Mete_En_Ella()
        {
            var map = MapCollisionGrid.FromRows(new[]
            {
                ".#",
                ".#",
                ".#",
            });

            Vec2 glued = MovementSimulator.Step(new Vec2(0.5f, 1f), 1, 0, Speed, Dt, Half, map);
            Vec2 result = MovementSimulator.Step(glued, 1, 0, Speed, Dt, Half, map);

            Assert.Equal(glued, result);
            Assert.True(map.IsBoxWalkable(result, Half));
        }

        [Fact]
        public void Se_Acerca_Hasta_La_Pared_En_X_Hacia_La_Izquierda_Sin_Entrar()
        {
            // Columna 0 bloqueada: el centro mínimo es 0 + 1 + 0,4 = 1,4.
            var map = MapCollisionGrid.FromRows(new[]
            {
                "#..",
                "#..",
                "#..",
            });

            var result = MovementSimulator.Step(new Vec2(1.5f, 1f), -1, 0, Speed, Dt, Half, map);

            Assert.Equal(1.4f, result.X, 4);
            Assert.True(result.X >= 1.4f, "El borde izquierdo no puede quedar dentro del tile.");
            Assert.True(map.IsBoxWalkable(result, Half));
        }

        [Fact]
        public void Se_Acerca_Hasta_La_Pared_En_Y_Hacia_Abajo_Sin_Entrar()
        {
            // Fila 0 bloqueada: el centro mínimo es 0 + 1 + 0,4 = 1,4.
            var map = MapCollisionGrid.FromRows(new[]
            {
                "###",
                "...",
                "...",
            });

            var result = MovementSimulator.Step(new Vec2(1f, 1.5f), 0, -1, Speed, Dt, Half, map);

            Assert.Equal(1.4f, result.Y, 4);
            Assert.True(result.Y >= 1.4f, "El borde inferior no puede quedar dentro del tile.");
            Assert.True(map.IsBoxWalkable(result, Half));
        }

        [Fact]
        public void Una_Base_Que_Solapa_La_Pared_No_Se_Queda_Atrapada()
        {
            var map = MapCollisionGrid.FromRows(new[]
            {
                "...#",
                "...#",
                "...#",
            });

            // Centro 2,62: la caja (hasta 3,02) ya invade el tile bloqueado, que empieza en 3.
            var overlapping = new Vec2(2.62f, 1f);
            Assert.False(map.IsBoxWalkable(overlapping, Half));

            // Empujar hacia la pared ni lo mete más ni lo deja salir.
            Vec2 intoWall = MovementSimulator.Step(overlapping, 1, 0, Speed, Dt, Half, map);
            Assert.Equal(overlapping, intoWall);

            // Empujar al otro lado reduce el solape: el paso se acepta y lo libera.
            Vec2 away = MovementSimulator.Step(overlapping, -1, 0, Speed, Dt, Half, map);
            Assert.Equal(2.37f, away.X, 4);
            Assert.True(map.IsBoxWalkable(away, Half));
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
