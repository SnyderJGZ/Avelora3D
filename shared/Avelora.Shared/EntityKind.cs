#nullable enable
namespace Avelora.Shared
{
    /// <summary>
    /// Tipo de entidad. Vive en la librería compartida porque <b>viaja en el snapshot</b>: el
    /// cliente necesita saber si lo que dibuja es un jugador o un PNJ.
    /// </summary>
    /// <remarks>
    /// Está en un <c>byte</c> para que ocupe exactamente un byte en el mensaje binario, sin
    /// conversiones en el camino caliente (guía del núcleo §4.2).
    /// </remarks>
    public enum EntityKind : byte
    {
        /// <summary>Jugador controlado por una persona (o por un bot, que se conecta igual).</summary>
        Player = 0,

        /// <summary>Enemigo o PNJ. Reservado para etapas posteriores.</summary>
        Npc = 1,
    }
}
