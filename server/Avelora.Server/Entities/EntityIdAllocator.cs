#nullable enable
using System;

namespace Avelora.Server.Entities
{
    /// <summary>
    /// Reparte los <see cref="Entity.Id"/> del servidor (guía del núcleo §3.1).
    /// </summary>
    /// <remarks>
    /// Los identificadores <b>no se reutilizan</b>: si un jugador se va y su Id volviera a
    /// repartirse enseguida, otro cliente que aún lo tuviera en pantalla confundiría la entidad
    /// nueva con la vieja. Con un contador creciente (y <see cref="int"/> de sobra) el problema
    /// desaparece sin necesidad de llevar registro de los liberados.
    ///
    /// Lo usa el hilo de simulación; no está pensado para llamarse desde varios hilos a la vez.
    /// </remarks>
    public sealed class EntityIdAllocator
    {
        /// <summary>Primer identificador repartido. El 0 se deja libre para poder usarlo como «ninguno».</summary>
        public const int FirstId = 1;

        private int _next = FirstId;

        /// <summary>El último Id repartido, o 0 si todavía no se ha repartido ninguno.</summary>
        public int LastAllocated { get; private set; }

        /// <summary>Cuántos identificadores se han repartido.</summary>
        public int Issued => _next - FirstId;

        /// <summary>Devuelve el siguiente identificador libre.</summary>
        /// <exception cref="InvalidOperationException">Si se agotó el rango de <see cref="int"/>.</exception>
        public int Allocate()
        {
            if (_next < FirstId)
            {
                throw new InvalidOperationException("Se agotaron los identificadores de entidad.");
            }

            LastAllocated = _next;
            _next = _next == int.MaxValue ? 0 : _next + 1;
            return LastAllocated;
        }
    }
}
