using System;
using System.IO;
using Avelora.Shared;
using Avelora.Shared.Maps;
using Xunit;

namespace Avelora.Tests
{
    public class MapFileTests
    {
        private static MapCollisionGrid Mapa() => MapCollisionGrid.FromRows(new[]
        {
            "....", // y = 0
            ".##.", // y = 1
            "..#.", // y = 2
            "....", // y = 3
        });

        private static void AfirmarMismoMapa(MapCollisionGrid esperado, MapCollisionGrid real)
        {
            Assert.Equal(esperado.Width, real.Width);
            Assert.Equal(esperado.Height, real.Height);
            Assert.Equal(esperado.OriginX, real.OriginX);
            Assert.Equal(esperado.OriginY, real.OriginY);

            for (int y = 0; y < esperado.Height; y++)
            {
                for (int x = 0; x < esperado.Width; x++)
                {
                    Assert.Equal(esperado.IsTileBlocked(x, y), real.IsTileBlocked(x, y));
                }
            }
        }

        [Fact]
        public void Ida_Y_Vuelta_Conserva_La_Rejilla()
        {
            var original = Mapa();

            var recovered = MapFile.Deserialize(MapFile.Serialize(original));

            AfirmarMismoMapa(original, recovered);
        }

        [Fact]
        public void Ida_Y_Vuelta_Conserva_El_Origen()
        {
            var original = new MapCollisionGrid(4, 4, new bool[16], originX: -8, originY: 5);

            var recovered = MapFile.Deserialize(MapFile.Serialize(original));

            Assert.Equal(-8, recovered.OriginX);
            Assert.Equal(5, recovered.OriginY);
        }

        [Fact]
        public void La_Huella_Es_Estable_Y_Distingue_Mapas()
        {
            string a = MapFile.ComputeContentHash(Mapa());
            string b = MapFile.ComputeContentHash(Mapa());

            Assert.Equal(a, b); // mismo contenido -> misma huella

            var distinto = MapCollisionGrid.FromRows(new[]
            {
                "....",
                ".##.",
                ".##.",
                "....",
            });
            Assert.NotEqual(a, MapFile.ComputeContentHash(distinto));

            // La huella es hexadecimal de 32 bytes.
            Assert.Equal(64, a.Length);
        }

        [Fact]
        public void Rechaza_Marca_Incorrecta()
        {
            var data = MapFile.Serialize(Mapa());
            data[0] = (byte)'X';

            Assert.Throws<MapFormatException>(() => MapFile.Deserialize(data));
        }

        [Fact]
        public void Rechaza_Version_Desconocida()
        {
            var data = MapFile.Serialize(Mapa());
            data[4] = 99; // sube la versión de formato
            data[5] = 0;

            var ex = Assert.Throws<MapFormatException>(() => MapFile.Deserialize(data));
            Assert.Contains("Versión", ex.Message);
        }

        [Fact]
        public void Rechaza_Archivo_Truncado()
        {
            var data = MapFile.Serialize(Mapa());
            var truncado = new byte[data.Length - 1];
            Array.Copy(data, truncado, truncado.Length);

            Assert.Throws<MapFormatException>(() => MapFile.Deserialize(truncado));
        }

        [Fact]
        public void Rechaza_Archivo_Demasiado_Corto()
        {
            Assert.Throws<MapFormatException>(() => MapFile.Deserialize(new byte[10]));
        }

        [Fact]
        public void Rechaza_Payload_Corrupto()
        {
            var data = MapFile.Serialize(Mapa());
            data[MapFile.HeaderSize] ^= 0xFF; // altera un byte del mapa de bits

            var ex = Assert.Throws<MapFormatException>(() => MapFile.Deserialize(data));
            Assert.Contains("SHA-256", ex.Message);
        }

        [Fact]
        public void Rechaza_Dimensiones_Invalidas()
        {
            var data = MapFile.Serialize(Mapa());
            data[8] = 0; // ancho = 0
            data[9] = 0;
            data[10] = 0;
            data[11] = 0;

            var ex = Assert.Throws<MapFormatException>(() => MapFile.Deserialize(data));
            Assert.Contains("Dimensiones", ex.Message);
        }

        [Theory]
        [InlineData(1, 1, 1)]
        [InlineData(8, 1, 1)]
        [InlineData(9, 1, 2)]
        [InlineData(16, 1, 2)]
        [InlineData(17, 1, 3)]
        [InlineData(4, 4, 2)]
        public void PayloadSize_Empaqueta_Un_Bit_Por_Tile(int width, int height, int expected)
        {
            Assert.Equal(expected, MapFile.PayloadSize(width, height));
        }

        [Fact]
        public void Guarda_Y_Carga_Desde_Disco()
        {
            var original = Mapa();
            string path = Path.Combine(Path.GetTempPath(), "avelora-" + Guid.NewGuid().ToString("N") + MapFile.Extension);

            try
            {
                MapFile.Save(path, original);
                Assert.True(File.Exists(path));

                var cargado = MapFile.Load(path);
                AfirmarMismoMapa(original, cargado);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
