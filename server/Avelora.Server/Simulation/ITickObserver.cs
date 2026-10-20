#nullable enable
namespace Avelora.Server.Simulation
{
    /// <summary>
    /// Se engancha al tick sin formar parte de él. La red lo usa para crear y quitar jugadores
    /// antes de simular y para publicar el estado después (plan del servidor §3).
    /// </summary>
    /// <remarks>
    /// Ambos métodos se llaman <b>en el hilo de simulación</b>. Es la costura que evita que la red
    /// toque el mundo: recibe paquetes en su hilo y los entrega por colas; aquí, ya en el hilo
    /// bueno, se aplican.
    /// </remarks>
    public interface ITickObserver
    {
        /// <summary>
        /// Antes de simular el tick: altas y bajas de jugadores pedidas por la red. Debe ser
        /// barato: solo vacía la cola de eventos de control.
        /// </summary>
        void BeforeTick(Zone zone);

        /// <summary>Después de simular: leer el mundo y encolar el estado para cada cliente.</summary>
        void AfterTick(Zone zone);
    }
}
