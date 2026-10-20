#nullable enable
using System;
using LiteNetLib;

namespace Avelora.Net
{
    /// <summary>
    /// Transporte de cliente sobre LiteNetLib. Lo usan los bots de la Etapa C y, en la Etapa D, el
    /// cliente de Unity: por eso vive en una librería compartida y no en el servidor.
    /// </summary>
    /// <remarks>
    /// Igual que el servidor, no es seguro entre hilos: se llama a <see cref="Poll"/> desde un solo
    /// hilo y los eventos llegan dentro de esa llamada.
    /// </remarks>
    public sealed class LiteNetLibClientTransport : IClientTransport
    {
        private readonly EventBasedNetListener _listener = new EventBasedNetListener();
        private readonly NetManager _manager;
        private NetPeer? _peer;
        private bool _started;

        public LiteNetLibClientTransport()
        {
            _manager = new NetManager(_listener)
            {
                AutoRecycle = true,
                UpdateTime = 10,
                UnconnectedMessagesEnabled = false,
                IPv6Enabled = false,
            };

            _listener.PeerConnectedEvent += OnPeerConnected;
            _listener.PeerDisconnectedEvent += OnPeerDisconnected;
            _listener.NetworkReceiveEvent += OnNetworkReceive;
        }

        public bool IsConnected => _peer != null && _peer.ConnectionState == ConnectionState.Connected;

        public event Action? Connected;

        public event Action<DisconnectReason>? Disconnected;

        public event TransportMessageHandler? Received;

        public void Connect(string host, int port)
        {
            if (_started)
            {
                throw new InvalidOperationException("El cliente ya está en marcha.");
            }

            if (!_manager.Start())
            {
                throw new InvalidOperationException("No se pudo abrir el socket UDP del cliente.");
            }

            _started = true;
            _peer = _manager.Connect(host, port, LiteNetLibServerTransport.ConnectionKey);
        }

        public void Poll()
        {
            if (_started)
            {
                _manager.PollEvents();
            }
        }

        public void Send(byte[] data, int length, DeliveryMode mode)
        {
            if (_peer != null && _peer.ConnectionState == ConnectionState.Connected)
            {
                _peer.Send(data, 0, length, ToDeliveryMethod(mode));
            }
        }

        public void Disconnect()
        {
            if (!_started)
            {
                return;
            }

            _manager.Stop();
            _peer = null;
            _started = false;
        }

        public void Dispose() => Disconnect();

        private void OnPeerConnected(NetPeer peer)
        {
            _peer = peer;
            Connected?.Invoke();
        }

        private void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            _peer = null;
            Disconnected?.Invoke(ToDisconnectReason(disconnectInfo.Reason));
        }

        private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            Received?.Invoke(0, reader.RawData, reader.UserDataOffset, reader.UserDataSize);
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
