using System;

namespace TCAMultiplayer.Networking
{
    /// <summary>
    /// Common interface for network transports.
    /// Allows switching between Steam P2P and Direct UDP connections.
    /// </summary>
    public interface INetworkTransport
    {
        /// <summary>
        /// Name of this transport for display purposes
        /// </summary>
        string Name { get; }
        
        /// <summary>
        /// Whether we are currently connected to a peer
        /// </summary>
        bool IsConnected { get; }
        
        /// <summary>
        /// Whether we are hosting (server mode)
        /// </summary>
        bool IsHost { get; }
        
        /// <summary>
        /// Start hosting on the specified port
        /// </summary>
        void StartHost(int port);
        
        /// <summary>
        /// Connect to a remote host
        /// </summary>
        void Connect(string address, int port);
        
        /// <summary>
        /// Disconnect from current session
        /// </summary>
        void Disconnect();
        
        /// <summary>
        /// Send data to the connected peer(s)
        /// </summary>
        void Send(byte[] data, bool reliable = true);
        
        /// <summary>
        /// Process incoming messages - call this every frame
        /// </summary>
        void Update();
        
        /// <summary>
        /// Shutdown the transport
        /// </summary>
        void Shutdown();
        
        /// <summary>
        /// Called when a peer connects
        /// </summary>
        event Action<ulong> OnPeerConnected;
        
        /// <summary>
        /// Called when a peer disconnects
        /// </summary>
        event Action<ulong> OnPeerDisconnected;
        
        /// <summary>
        /// Called when data is received from a peer
        /// </summary>
        event Action<ulong, byte[]> OnDataReceived;
    }
}
