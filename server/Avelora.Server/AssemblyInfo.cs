using System.Runtime.CompilerServices;

// Las pruebas comprueban estado interno de simulación que no debe exponerse al cliente
// (por ejemplo, las recargas de habilidad, que no viajan ni se guardan).
[assembly: InternalsVisibleTo("Avelora.Tests")]
