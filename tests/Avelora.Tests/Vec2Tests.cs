using System.Globalization;
using Avelora.Shared;
using Xunit;

namespace Avelora.Tests
{
    public class Vec2Tests
    {
        [Fact]
        public void Suma_Resta_Y_Escala_Componente_A_Componente()
        {
            var a = new Vec2(2f, 3f);
            var b = new Vec2(-1f, 4f);

            Assert.Equal(new Vec2(1f, 7f), a + b);
            Assert.Equal(new Vec2(3f, -1f), a - b);
            Assert.Equal(new Vec2(4f, 6f), a * 2f);
            Assert.Equal(new Vec2(4f, 6f), 2f * a);
            Assert.Equal(new Vec2(1f, 1.5f), a / 2f);
            Assert.Equal(new Vec2(-2f, -3f), -a);
        }

        [Fact]
        public void Longitud_Es_La_Del_Teorema_De_Pitagoras()
        {
            var v = new Vec2(3f, 4f);

            Assert.Equal(25f, v.LengthSquared);
            Assert.Equal(5f, v.Length, 3);
        }

        [Fact]
        public void Normalized_Devuelve_Vector_Unitario()
        {
            var unit = new Vec2(10f, 0f).Normalized();

            Assert.Equal(1f, unit.Length, 3);
            Assert.Equal(new Vec2(1f, 0f), unit);
        }

        [Fact]
        public void Normalized_De_Vector_Nulo_Es_Nulo()
        {
            Assert.Equal(Vec2.Zero, Vec2.Zero.Normalized());
        }

        [Fact]
        public void Igualdad_Es_Por_Valor()
        {
            var a = new Vec2(1.5f, -2.5f);
            var b = new Vec2(1.5f, -2.5f);

            Assert.True(a == b);
            Assert.False(a != b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.True(a.Equals((object)b));
        }

        [Fact]
        public void Distintos_Valores_No_Son_Iguales()
        {
            Assert.True(new Vec2(1f, 0f) != new Vec2(0f, 1f));
            Assert.False(new Vec2(1f, 0f).Equals("no soy un Vec2"));
        }

        [Fact]
        public void ToString_Usa_Punto_Decimal_Con_Cualquier_Cultura()
        {
            CultureInfo previous = CultureInfo.CurrentCulture;
            var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            culture.NumberFormat.NumberDecimalSeparator = ",";

            try
            {
                CultureInfo.CurrentCulture = culture;

                // Si dependiera de la cultura saldría «(1,5, -2,25)», que rompe registros y
                // comparaciones entre máquinas.
                Assert.Equal("(1.5, -2.25)", new Vec2(1.5f, -2.25f).ToString());
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }
    }
}
