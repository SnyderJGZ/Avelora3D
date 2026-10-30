#nullable enable
using Avelora.Server.Persistence;
using Xunit;

namespace Avelora.Tests
{
    public sealed class CharacterNameValidationTests
    {
        [Theory]
        [InlineData("Ana")]
        [InlineData("Ána")]
        [InlineData("Díaz Ñandú")]
        [InlineData("Ana 2")]
        public void Nombre_de_personaje_admite_letras_espanolas_y_espacios_sueltos(string name)
        {
            Assert.True(LoginValidation.IsValidCharacterName(name));
        }

        [Theory]
        [InlineData("  Ana ")]
        [InlineData("An  a")]
        [InlineData("2Ana")]
        [InlineData("Al")]
        [InlineData("AnaAnaAnaAnaAnaAnaA")]
        [InlineData("Ana_1")]
        [InlineData("Admin")]
        [InlineData("SERVIDOR")]
        public void Nombre_de_personaje_rechaza_formas_invalidas_y_reservadas(string name)
        {
            Assert.False(LoginValidation.IsValidCharacterName(name));
        }

        [Fact]
        public void Nombre_valido_se_recorta_sin_partir_utf8_al_limite_de_chat()
        {
            string normalized = LoginValidation.NormalizeCharacterName(new string('á', 16));

            Assert.True(System.Text.Encoding.UTF8.GetByteCount(normalized) <=
                Avelora.Shared.Protocol.ProtocolConstants.MaxChatNameBytes);
            Assert.True(LoginValidation.IsValidCharacterName(normalized));
            Assert.False(LoginValidation.IsValidCharacterName(
                LoginValidation.NormalizeCharacterName(new string('á', 17))));
        }

        [Theory]
        [InlineData("Ána", "ána")]
        [InlineData("ANA", "ana")]
        public void Clave_de_nombre_no_distingue_mayusculas_incluidas_tildes(string name, string expected)
        {
            Assert.Equal(expected, LoginValidation.CharacterNameKey(name));
        }
    }
}
