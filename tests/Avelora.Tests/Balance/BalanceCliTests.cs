using System.IO;
using Avelora.Balance;
using Xunit;

namespace Avelora.Tests.Balance
{
    public sealed class BalanceCliTests
    {
        [Fact]
        public void Run_Imprime_Tablas_Markdown_Con_Los_Argumentos_Minimos()
        {
            using var output = new StringWriter();
            int exitCode = Program.Run(new[] { "--niveles", "1", "--build", "puro", "--equipo", "ninguno" }, output);

            Assert.Equal(0, exitCode);
            Assert.Contains("| --- |", output.ToString());
            Assert.Contains("| Clase | Nivel | DPS |", output.ToString());
        }

        [Fact]
        public void Run_Desconocido_Devuelve_2_Sin_Traza()
        {
            using var output = new StringWriter();
            int exitCode = Program.Run(new[] { "--no-existe" }, output);

            Assert.Equal(2, exitCode);
            Assert.Contains("Argumento desconocido: --no-existe", output.ToString());
            Assert.DoesNotContain(" at ", output.ToString());
        }
    }
}
