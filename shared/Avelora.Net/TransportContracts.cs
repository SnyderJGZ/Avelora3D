#nullable enable
using System;

namespace Avelora.Net
{
    /// <summary>Cómo viaja un mensaje (plan del servidor §4).</summary>
    public enum DeliveryMode : byte
    {
        /// <summary>Sin garantías: se puede perder, duplicar o llegar desordenado.</summary>
        Unreliable = 0,

        /// <summary>Sin garantías, pero se descartan los paquetes más viejos que uno ya recibido.</summary>
        Sequenced = 1,

        /// <summary>Llega y en orden: para el saludo y todo lo que no se puede perder.</summary>
        ReliableOrdered = 2,
    }

    /// <summary>Por qué se cortó una conexión (versión propia, sin depender de la librería).</summary>
    public enum DisconnectReason : byte
    {
        Unknown = 0,
        Timeout = 1,
        Rejected = 2,
        ServerFull = 3,
        LocalShutdown = 4,
        ConnectionFailed = 5,
        RemoteClosed = 6,
    }

    /// <summary>Se abrió una conexión.</summary>
    public delegate void TransportConnectedHandler(int connectionId);

    /// <summary>Se cerró una conexión.</summary>
    public delegate void TransportDisconnectedHandler(int connectionId, DisconnectReason reason);

    /// <summary>
    /// Llegó un mensaje. <paramref name="buffer"/> solo es válido durante la llamada: si hay que
    /// guardarlo, se copia. Leerlo en el acto evita una copia por paquete (plan del servidor §4).
    /// </summary>
    public delegate void TransportMessageHandler(int connectionId, byte[] buffer, int offset, int length);

    /// <summary>
    /// Transporte del servidor. La lógica de sesiones solo ve esto: cambiar UDP por otra cosa no
    /// toca el tick ni el protocolo (plan del servidor §4, «oculto tras una interfaz»).
    /// </summary>
    /// <remarks>
    /// No es seguro llamarlo desde varios hilos: todos los métodos y eventos pertenecen al hilo
    /// que llama a <see cref="Poll"/>. El servidor lo ejecuta en su propio hilo de red.
    /// </remarks>
    public interface IServerTransport : IDisposable
    {
        /// <summary>Puerto local. Es el asignado por el sistema si se pidió el 0.</summary>
        int Port { get; }

        /// <summary>Conexiones abiertas ahora mismo.</summary>
        int ConnectionCount { get; }

        /// <summary>Dirección IP remota de una conexión abierta, o cadena vacía si no existe.</summary>
        string GetRemoteAddress(int connectionId);

        /// <summary>Abre el socket y empieza a escuchar.</summary>
        void Start(int port);

        /// <summary>Cierra el socket y todas las conexiones.</summary>
        void Stop();

        /// <summary>Procesa lo que haya pendiente sin bloquear.</summary>
        void Poll();

        /// <summary>Envía un mensaje a una conexión.</summary>
        void Send(int connectionId, byte[] data, int length, DeliveryMode mode);

        /// <summary>Cierra una conexión, avisando al otro lado.</summary>
        void Disconnect(int connectionId, DisconnectReason reason);

        event TransportConnectedHandler? Connected;

        event TransportDisconnectedHandler? Disconnected;

        event TransportMessageHandler? Received;
    }

    /// <summary>Transporte del cliente. Misma idea que <see cref="IServerTransport"/>, del otro lado.</summary>
    public interface IClientTransport : IDisposable
    {
        /// <summary>Hay conexión establecida y saludada a nivel de transporte.</summary>
        bool IsConnected { get; }

        /// <summary>Empieza a conectar. El resultado llega por <see cref="Connected"/> al hacer <see cref="Poll"/>.</summary>
        void Connect(string host, int port);

        /// <summary>Procesa lo que haya pendiente sin bloquear.</summary>
        void Poll();

        void Send(byte[] data, int length, DeliveryMode mode);

        /// <summary>Cierra la conexión y libera el socket.</summary>
        void Disconnect();

        event Action? Connected;

        event Action<DisconnectReason>? Disconnected;

        /// <summary>Mensajes del servidor. El identificador de conexión es siempre 0.</summary>
        event TransportMessageHandler? Received;
    }
}
