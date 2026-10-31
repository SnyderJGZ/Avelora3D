using System;
using Avelora.Shared;
using Xunit;

namespace Avelora.Tests
{
    public class MapCollisionGridTests
    {
        // Rejilla 3x3 con un único tile bloqueado en el centro (1,1).
        private static MapCollisionGrid MapaConCentroBloqueado() => MapCollisionGrid.FromRows(new[]
        {
            "...", // y = 0
            ".#.", // y = 1
            "...", // y = 2
        });

        [Fact]
        public void Marca_Correctamente_Los_Tiles_Bloqueados()
        {
            var map = MapaConCentroBloqueado();

            Assert.True(map.IsTileBlocked(1, 1));
            Assert.False(map.IsTileBlocked(0, 0));
            Assert.False(map.IsTileBlocked(2, 2));
        }

        [Fact]
        public void Fuera_De_La_Rejilla_Cuenta_Como_Bloqueado()
        {
            var map = MapaConCentroBloqueado();

            Assert.True(map.IsTileBlocked(-1, 1));
            Assert.True(map.IsTileBlocked(1, -1));
            Assert.True(map.IsTileBlocked(3, 1));
            Assert.True(map.IsTileBlocked(1, 3));
        }

        [Fact]
        public void Caja_Pequena_En_Tile_Libre_Es_Transitable()
        {
            var map = MapaConCentroBloqueado();

            Assert.True(map.IsBoxWalkable(new Vec2(0.5f, 0.5f), new Vec2(0.4f, 0.4f)));
        }

        [Fact]
        public void Caja_Que_Toca_Un_Tile_Bloqueado_No_Es_Transitable()
        {
            var map = MapaConCentroBloqueado();

            // Centro en el centro del tile libre (0.5,0.5) pero con semiancho 0.6:
            // la caja llega hasta 1.1, dentro del tile bloqueado (1,1).
            Assert.False(map.IsBoxWalkable(new Vec2(0.5f, 0.5f), new Vec2(0.6f, 0.6f)));
        }

        [Fact]
        public void Caja_Que_Cruza_Varios_Tiles_Comprueba_Todos()
        {
            // Solo el tile (2,0) está bloqueado; una caja ancha que lo abarque debe fallar.
            var map = MapCollisionGrid.FromRows(new[]
            {
                "..#",
                "...",
            });

            Assert.False(map.IsBoxWalkable(new Vec2(1.2f, 0.5f), new Vec2(1f, 0.4f)));
            Assert.True(map.IsBoxWalkable(new Vec2(0.5f, 0.5f), new Vec2(0.4f, 0.4f)));
        }

        [Fact]
        public void AllWalkable_No_Tiene_Nada_Bloqueado()
        {
            var map = MapCollisionGrid.AllWalkable(4, 4);

            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    Assert.False(map.IsTileBlocked(x, y));
                }
            }
        }

        [Fact]
        public void FromRows_Exige_Filas_Del_Mismo_Ancho()
        {
            Assert.Throws<ArgumentException>(() => MapCollisionGrid.FromRows(new[] { "...", ".." }));
        }

        [Fact]
        public void Constructor_Rechaza_Tamano_Incoherente()
        {
            Assert.Throws<ArgumentException>(() => new MapCollisionGrid(2, 2, new bool[3]));
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(1, 0)]
        [InlineData(-1, 1)]
        public void Constructor_Rechaza_Dimensiones_No_Positivas(int width, int height)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new MapCollisionGrid(width, height, new bool[Math.Max(width, 0) * Math.Max(height, 0)]));
        }

        [Fact]
        public void Overlaps_Detecta_Contacto_Entre_Cajas()
        {
            var a = new Vec2(0f, 0f);
            var half = new Vec2(0.5f, 0.5f);

            // Los bordes se tocan justo (1.0 == 0.5 + 0.5): cuentan como solape.
            Assert.True(MapCollisionGrid.Overlaps(a, half, new Vec2(1f, 0f), half));
            Assert.True(MapCollisionGrid.Overlaps(a, half, new Vec2(0.6f, 0f), half));
            // Separadas más que la suma de semianchos: no hay solape.
            Assert.False(MapCollisionGrid.Overlaps(a, half, new Vec2(1.5f, 0f), half));
            // Solape solo en Y tampoco basta si X está lejos.
            Assert.False(MapCollisionGrid.Overlaps(a, half, new Vec2(2f, 0.25f), half));
        }

        [Fact]
        public void Guarda_El_Origen_Del_Mapa()
        {
            var map = new MapCollisionGrid(2, 2, new bool[4], originX: -8, originY: -4);

            Assert.Equal(-8, map.OriginX);
            Assert.Equal(-4, map.OriginY);
            Assert.Equal(4, map.TileCount);
        }

        [Fact]
        public void Las_Consultas_Usan_Coordenadas_Absolutas_Con_Origen_Negativo()
        {
            // Rejilla 3x3 cuyo tile (0,0) es el tile absoluto (-8, 5).
            var blocked = new bool[9];
            blocked[(1 * 3) + 1] = true; // el centro -> tile absoluto (-7, 6)
            var map = new MapCollisionGrid(3, 3, blocked, -8, 5);

            Assert.True(map.IsTileBlocked(-7, 6));
            Assert.False(map.IsTileBlocked(-8, 5));
            Assert.False(map.IsTileBlocked(-6, 7));

            // Justo fuera de la rejilla -> bloqueado.
            Assert.True(map.IsTileBlocked(-9, 5));
            Assert.True(map.IsTileBlocked(-8, 8));
        }

        [Fact]
        public void IsBoxWalkable_Respeta_El_Origen_En_Coordenadas_De_Mundo()
        {
            // Rejilla 4x4 con origen (-2,-2): todo libre excepto el tile absoluto (0,0).
            var blocked = new bool[16];
            blocked[((0 - -2) * 4) + (0 - -2)] = true;
            var map = new MapCollisionGrid(4, 4, blocked, -2, -2);

            var half = new Vec2(0.4f, 0.4f);
            Assert.True(map.IsBoxWalkable(new Vec2(-1.5f, -1.5f), half)); // tile (-2,-2): libre
            Assert.False(map.IsBoxWalkable(new Vec2(0.5f, 0.5f), half));  // dentro del tile (0,0): bloqueado
        }

        [Fact]
        public void HasLineOfSight_Recorre_Celdas_Y_Respeta_Paredes_Y_Esquinas()
        {
            MapCollisionGrid open = MapCollisionGrid.FromRows(new[] { "........", "........", "........", "........", "........" });
            Assert.True(open.HasLineOfSight(new Vec2(0.5f, 0.5f), new Vec2(6.5f, 3.5f)));

            MapCollisionGrid crossed = MapCollisionGrid.FromRows(new[] { "........", "..#.....", "........", "........", "........" });
            MapCollisionGrid nearby = MapCollisionGrid.FromRows(new[] { "........", "........", "..#.....", "........", "........" });
            Assert.False(crossed.HasLineOfSight(new Vec2(0.5f, 0.5f), new Vec2(6.5f, 3.5f)));
            Assert.True(nearby.HasLineOfSight(new Vec2(0.5f, 0.5f), new Vec2(6.5f, 3.5f)));

            MapCollisionGrid middle = MapCollisionGrid.FromRows(new[] { ".....", "..#..", "....." });
            Assert.False(middle.HasLineOfSight(new Vec2(0.5f, 1.5f), new Vec2(4.5f, 1.5f)));
            Assert.True(middle.HasLineOfSight(new Vec2(0.5f, 0.5f), new Vec2(4.5f, 0.5f)));

            Assert.True(open.HasLineOfSight(new Vec2(0.5f, 0.5f), new Vec2(0.5f, 0.5f)));
            Assert.False(crossed.HasLineOfSight(new Vec2(2.5f, 1.5f), new Vec2(2.5f, 1.5f)));
            Assert.False(crossed.HasLineOfSight(new Vec2(2.5f, 1.5f), new Vec2(0.5f, 0.5f)));
            Assert.False(crossed.HasLineOfSight(new Vec2(0.5f, 0.5f), new Vec2(2.5f, 1.5f)));
            Assert.False(open.HasLineOfSight(new Vec2(0.5f, 0.5f), new Vec2(8.5f, 0.5f)));

            MapCollisionGrid vertical = MapCollisionGrid.FromRows(new[] { "...", ".#.", "..." });
            Assert.False(vertical.HasLineOfSight(new Vec2(1.5f, 0.5f), new Vec2(1.5f, 2.5f)));
            Assert.True(open.HasLineOfSight(new Vec2(0.5f, 0.5f), new Vec2(0.5f, 3.5f)));

            MapCollisionGrid cornerWall = MapCollisionGrid.FromRows(new[] { "#.", ".." });
            Vec2 lowerLeft = new Vec2(0.5f, 1.5f);
            Vec2 upperRight = new Vec2(1.5f, 0.5f);
            Assert.False(cornerWall.HasLineOfSight(lowerLeft, upperRight));
            Assert.False(cornerWall.HasLineOfSight(upperRight, lowerLeft));
            MapCollisionGrid clearCorner = MapCollisionGrid.FromRows(new[] { "..", ".." });
            Assert.True(clearCorner.HasLineOfSight(lowerLeft, upperRight));
            MapCollisionGrid diagonalSlit = MapCollisionGrid.FromRows(new[] { ".#", "#." });
            Assert.False(diagonalSlit.HasLineOfSight(new Vec2(0.5f, 0.5f), new Vec2(1.5f, 1.5f)));
            Assert.False(diagonalSlit.HasLineOfSight(new Vec2(1.5f, 1.5f), new Vec2(0.5f, 0.5f)));
            Assert.True(cornerWall.HasLineOfSight(new Vec2(0.5f, 1.5f), new Vec2(1.5f, 0.6f)));
            Assert.True(cornerWall.HasLineOfSight(new Vec2(1.5f, 0.6f), new Vec2(0.5f, 1.5f)));
            Assert.False(cornerWall.HasLineOfSight(new Vec2(0.5f, 1.4f), new Vec2(1.5f, 0.5f)));
            Assert.False(cornerWall.HasLineOfSight(new Vec2(1.5f, 0.5f), new Vec2(0.5f, 1.4f)));

            var blocked = new bool[6];
            blocked[3] = true; // x absoluto 0, origen de rejilla -3.
            var negativeOrigin = new MapCollisionGrid(6, 1, blocked, originX: -3);
            Assert.False(negativeOrigin.HasLineOfSight(new Vec2(-2.5f, 0.5f), new Vec2(2.5f, 0.5f)));
            Assert.True(new MapCollisionGrid(6, 1, new bool[6], originX: -3).HasLineOfSight(
                new Vec2(-2.5f, 0.5f), new Vec2(2.5f, 0.5f)));

            Assert.False(cornerWall.HasLineOfSight(new Vec2(1.5f, 0.5f), new Vec2(0.5f, 1.5f)));
            Assert.False(middle.HasLineOfSight(new Vec2(4.5f, 1.5f), new Vec2(0.5f, 1.5f)));
            Assert.False(crossed.HasLineOfSight(new Vec2(6.5f, 3.5f), new Vec2(0.5f, 0.5f)));
        }
    }
}
