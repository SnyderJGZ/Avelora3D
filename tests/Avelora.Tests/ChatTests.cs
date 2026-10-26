using System.Linq;
using Avelora.Bots;
using Avelora.Server.Configuration;
using Avelora.Server.Net;
using Xunit;

namespace Avelora.Tests
{
    public class ChatTests
    {
        [Fact]
        public void Chat_Recorta_Espacios_Y_Valida_Limites_UTF8_Multibyte()
        {
            Assert.Equal(ChatTextValidationResult.Valid,
                ChatTextValidator.Validate("  hola  ", 120, out string trimmed));
            Assert.Equal("hola", trimmed);

            string exactly120Bytes = string.Concat(Enumerable.Repeat("\U0001F600", 30));
            Assert.Equal(ChatTextValidationResult.Valid,
                ChatTextValidator.Validate(exactly120Bytes, 120, out string exact));
            Assert.Equal(exactly120Bytes, exact);

            string tooLong = exactly120Bytes + "a";
            Assert.Equal(ChatTextValidationResult.TooLong,
                ChatTextValidator.Validate(tooLong, 120, out _));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("hola\nque tal")]
        [InlineData("hola\u200B")]
        [InlineData("hola\u202E")]
        public void Chat_Rechaza_Vacio_Y_Caracteres_De_Control_O_Formato(string text)
        {
            Assert.Equal(ChatTextValidationResult.Invalid,
                ChatTextValidator.Validate(text, 120, out _));
        }

        [Fact]
        public void Configuracion_Chat_Valida_Los_Rangos_Especificados()
        {
            var defaults = new ServerConfig();
            defaults.Validate();
            Assert.Equal(120, defaults.ChatMaxBytes);
            Assert.Equal(3, defaults.ChatBurst);
            Assert.Equal(1d, defaults.ChatPerSecond);

            Assert.Throws<System.InvalidOperationException>(
                new ServerConfig { ChatMaxBytes = 0 }.Validate);
            Assert.Throws<System.InvalidOperationException>(
                new ServerConfig { ChatMaxBytes = 256 }.Validate);
            Assert.Throws<System.InvalidOperationException>(
                new ServerConfig { ChatBurst = 0 }.Validate);
            Assert.Throws<System.InvalidOperationException>(
                new ServerConfig { ChatBurst = 21 }.Validate);
            Assert.Throws<System.InvalidOperationException>(
                new ServerConfig { ChatPerSecond = 0.09 }.Validate);
            Assert.Throws<System.InvalidOperationException>(
                new ServerConfig { ChatPerSecond = 10.01 }.Validate);
            Assert.Throws<System.InvalidOperationException>(
                new ServerConfig { ChatPerSecond = double.NaN }.Validate);

            var validLimits = new ServerConfig { ChatMaxBytes = 1, ChatBurst = 1, ChatPerSecond = 0.1 };
            validLimits.Validate();
        }

        [Fact]
        public void El_Comportamiento_Chat_Se_Agrega_Sin_Renumerar_Los_Existentes()
        {
            Assert.Equal(7, (int)BotBehavior.Fight);
            Assert.Equal(8, (int)BotBehavior.Chat);
        }
    }
}
