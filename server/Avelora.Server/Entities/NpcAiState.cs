#nullable enable
namespace Avelora.Server.Entities
{
    /// <summary>
    /// Estado de la IA de un enemigo (I-03). Vive en el hilo de simulación y no viaja en el
    /// protocolo: el cliente solo ve posición y acción.
    /// </summary>
    public enum NpcAiState : byte
    {
        /// <summary>Deambula alrededor de su casa y busca jugadores dentro de <see cref="NpcEntity.AggroRadius"/>.</summary>
        Wander = 0,

        /// <summary>Camina en línea recta hacia su objetivo.</summary>
        Chase = 1,

        /// <summary>Quieto, mirando al objetivo y golpeando cuando toca la recarga.</summary>
        Attack = 2,

        /// <summary>Vuelve a casa e ignora a los jugadores; al llegar recupera la vida llena.</summary>
        Return = 3,
    }
}
