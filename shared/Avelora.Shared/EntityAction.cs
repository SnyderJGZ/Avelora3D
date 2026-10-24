#nullable enable
namespace Avelora.Shared
{
    /// <summary>
    /// Estado visible de una entidad dentro del snapshot (I-02). Dice qué animación debe mostrar el
    /// cliente, no qué está pasando por dentro.
    /// </summary>
    /// <remarks>
    /// Viaja como un <c>byte</c> para que ocupe exactamente un byte en el mensaje binario, igual que
    /// <see cref="EntityKind"/>. El servidor sigue decidiendo todo: el cliente solo dibuja.
    /// </remarks>
    public enum EntityAction : byte
    {
        /// <summary>Sin acción especial: la entidad se mueve o está quieta.</summary>
        None = 0,

        /// <summary>La entidad acaba de atacar; se mantiene unos ticks para que se vea el golpe.</summary>
        Attacking = 1,

        /// <summary>La entidad ha muerto: no se mueve, no se puede atacar y desaparecerá.</summary>
        Dead = 2,
    }
}
