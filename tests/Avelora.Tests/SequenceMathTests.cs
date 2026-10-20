using Avelora.Shared;
using Xunit;

namespace Avelora.Tests
{
    public class SequenceMathTests
    {
        [Theory]
        [InlineData(2u, 1u, true)]
        [InlineData(1u, 2u, false)]
        [InlineData(1u, 1u, false)]
        [InlineData(0u, uint.MaxValue, true)]
        [InlineData(uint.MaxValue, 0u, false)]
        [InlineData(100u, 99u, true)]
        public void IsNewer_Comprende_La_Vuelta_De_Uint(uint candidate, uint reference, bool expected)
        {
            Assert.Equal(expected, SequenceMath.IsNewer(candidate, reference));
        }

        [Fact]
        public void Media_Vuelta_Es_Ambigua_Y_Se_Trata_Como_Vieja()
        {
            Assert.False(SequenceMath.IsNewer(0x80000000u, 0u));
            Assert.False(SequenceMath.IsNewer(0u, 0x80000000u));
        }
    }
}
