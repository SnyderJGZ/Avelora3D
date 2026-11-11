#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using LiteNetLib;

namespace Avelora.Net
{
    /// <summary>
    /// Transporte de servidor sobre LiteNetLib (UDP con canales fiables).
    /// </summary>
    /// <remarks>
    /// LiteNetLib no es segura entre hilos, así que <b>todo</b> —eventos incluidos— ocurre dentro de
    /// <see cref="Poll"/>. El servidor la llama desde un único hilo de red; la simulación habla con
    /// ella por colas (plan del servidor §2.1 y §4).
    ///
    /// La clave de conexión no es un secreto: solo evita que un escaneo de puertos abra una sesión
    /// por accidente. La autenticación de verdad llega en la Etapa F.
    /// </remarks>
    public sealed class LiteNetLibServerTransport : IServerTransport
    {
        /// <summary>Clave que debe traer el paquete de conexión.</summary>
        public const string ConnectionKey = "avelora-v1";

        private readonly EventBasedNetListener _listener = new EventBasedNetListener();
        private readonly NetManager _manager;
        private readonly Dictionary<int, NetPeer> _peers = new Dictionary<int, NetPeer>();
        private readonly int _maxConnections;
        private readonly int _maxConnectionsPerIp;
        private bool _started;

        public LiteNetLibServerTransport(int maxConnections, int maxConnectionsPerIp = 4)
        {
            if (maxConnections < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxConnections), "Debe caber al menos una conexión.");
            }

            if (maxConnectionsPerIp < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxConnectionsPerIp), "Debe caber al menos una conexión por IP.");
            }

            _maxConnections = maxConnections;
            _maxConnectionsPerIp = maxConnectionsPerIp;
            _manager = new NetManager(_listener)
            {
                AutoRecycle = true,
                UpdateTime = 10,
                UnconnectedMessagesEnabled = false,
                IPv6Enabled = false,
            };

            _listener.ConnectionRequestEvent += OnConnectionRequest;
            _listener.PeerConnectedEvent += OnPeerConnected;
            _listener.PeerDisconnectedEvent += OnPeerDisconnected;
            _listener.NetworkReceiveEvent += OnNetworkReceive;
        }

        public int Port => _manager.LocalPort;

        public int ConnectionCount => _manager.ConnectedPeersCount;

        public string GetRemoteAddress(int connectionId) =>
            _peers.TryGetValue(connectionId, out NetPeer peer) ? peer.Address.ToString() : string.Empty;

        public event TransportConnectedHandler? Connected;

        public event TransportDisconnectedHandler? Disconnected;

        public event TransportMessageHandler? Received;

        public void Start(int port)
        {
            if (_started)
            {
                throw new InvalidOperationException("El transporte ya está en marcha.");
            }

            if (!_manager.Start(port))
            {
                throw new InvalidOperationException(
                    "No se pudo abrir el puerto UDP " + port + ". ¿Está ocupado por otro proceso?");
            }

            _started = true;
        }

        public void Stop()
        {
            if (!_started)
            {
                return;
            }

            _manager.Stop();
            _peers.Clear();
            _started = false;
        }

        public void Poll()
        {
            if (_started)
            {
                _manager.PollEvents();
            }
        }

        public void Send(int connectionId, byte[] data, int length, DeliveryMode mode)
        {
            if (_peers.TryGetValue(connectionId, out NetPeer peer) && peer.ConnectionState == ConnectionState.Connected)
            {
                peer.Send(data, 0, length, ToDeliveryMethod(mode));
            }
        }

        public void Disconnect(int connectionId, DisconnectReason reason)
        {
            if (_peers.TryGetValue(connectionId, out NetPeer peer))
            {
                // Sin datos de desconexión a propósito: el motivo de juego ya viajó en su mensaje
                // (Reject) y el cliente no debe fiarse de texto libre.
                peer.Disconnect();
            }
        }

        public void Dispose() => Stop();

        private void OnConnectionRequest(ConnectionRequest request)
        {
            if (_manager.ConnectedPeersCount >= _maxConnections)
            {
                request.Reject();
                return;
            }

            // Varias personas pueden compartir una IP (una casa, un router), pero una sola máquina
            // no puede acaparar las conexiones. Recorrer _peers basta: el máximo es pequeño.
            if (CountPeersFrom(request.RemoteEndPoint.Address) >= _maxConnectionsPerIp)
            {
                request.Reject();
                return;
            }

            request.AcceptIfKey(ConnectionKey);
        }

        private int CountPeersFrom(IPAddress address)
        {
            int count = 0;

            foreach (KeyValuePair<int, NetPeer> pair in _peers)
            {
                if (pair.Value.Address.Equals(address))
                {
                    count++;
                }
            }

            return count;
        }

        private void OnPeerConnected(NetPeer peer)
        {
            _peers[peer.Id] = peer;
            Connected?.Invoke(peer.Id);
        }

        private void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            _peers.Remove(peer.Id);
            Disconnected?.Invoke(peer.Id, ToDisconnectReason(disconnectInfo.Reason));
        }

        private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            // El búfer de LiteNetLib solo vale durante este evento: quien lo necesite después, copia.
            Received?.Invoke(peer.Id, reader.RawData, reader.UserDataOffset, reader.UserDataSize);
        }

        private static DeliveryMethod ToDeliveryMethod(DeliveryMode mode)
        {
            switch (mode)
            {
                case DeliveryMode.ReliableOrdered:
                    return DeliveryMethod.ReliableOrdered;
                case DeliveryMode.Sequenced:
                    return DeliveryMethod.Sequenced;
                default:
                    return DeliveryMethod.Unreliable;
            }
        }

        private static DisconnectReason ToDisconnectReason(LiteNetLib.DisconnectReason reason)
        {
            switch (reason)
            {
                case LiteNetLib.DisconnectReason.Timeout:
                    return DisconnectReason.Timeout;
                case LiteNetLib.DisconnectReason.ConnectionFailed:
                    return DisconnectReason.ConnectionFailed;
                case LiteNetLib.DisconnectReason.ConnectionRejected:
                    return DisconnectReason.Rejected;
                case LiteNetLib.DisconnectReason.RemoteConnectionClose:
                    return DisconnectReason.RemoteClosed;
                case LiteNetLib.DisconnectReason.DisconnectPeerCalled:
                    return DisconnectReason.LocalShutdown;
                default:
                    return DisconnectReason.Unknown;
            }
        }
    }
}
