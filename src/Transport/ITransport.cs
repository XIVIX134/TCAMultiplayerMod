using System;
using System.Collections.Generic;

namespace TCAMultiplayer.Transport
{
    /// <summary>
    /// Transport abstraction for multiplayer networking.
    /// Supports N peers, reliable/unreliable messaging, and both host and client modes.
    /// 
    /// THREADING MODEL:
    /// - Concrete implementations receive data on a background thread
    /// - Received data goes into a ConcurrentQueue internally
    /// - Update() MUST be called from the main Unity thread to drain the queue
    /// - All events (OnDataReceived, OnPeerConnected, OnPeerDisconnected) fire on the main thread during Update()
    /// - Send/Broadcast may be called from any thread
    /// </summary>
    public interface ITransport : IDisposable
    {
        /// <summary>Fired on main thread when data arrives from a peer. Args: (peerId, data)</summary>
        event Action<ulong, byte[]> OnDataReceived;

        /// <summary>Fired on main thread when a new peer connects. Args: (peerId)</summary>
        event Action<ulong> OnPeerConnected;

        /// <summary>Fired on main thread when a peer disconnects. Args: (peerId)</summary>
        event Action<ulong> OnPeerDisconnected;

        /// <summary>True if this transport is hosting (server mode)</summary>
        bool IsHost { get; }

        /// <summary>True if connected (as host or client)</summary>
        bool IsConnected { get; }

        /// <summary>Local peer ID assigned during connection</summary>
        ulong LocalPeerId { get; }

        /// <summary>Set of currently connected peer IDs</summary>
        IReadOnlyCollection<ulong> ConnectedPeers { get; }

        /// <summary>
        /// Start hosting on the specified port.
        /// Host is automatically assigned PeerId = 1.
        /// </summary>
        void StartHost(int port);

        /// <summary>
        /// Connect to a host at the given address and port.
        /// PeerId is assigned by the host during handshake.
        /// </summary>
        void Connect(string address, int port);

        /// <summary>
        /// Disconnect from the session, cleaning up all resources.
        /// </summary>
        void Disconnect();

        /// <summary>
        /// Disconnect one peer. Hosts use this to reject a specific client
        /// without ending the whole session. Clients may only target peer 1.
        /// </summary>
        void DisconnectPeer(ulong peerId);

        /// <summary>
        /// Send data to a specific peer.
        /// Thread-safe — may be called from any thread.
        /// </summary>
        /// <param name="peerId">Target peer</param>
        /// <param name="data">Raw packet bytes</param>
        /// <param name="reliable">If true, guaranteed delivery with ordering</param>
        void Send(ulong peerId, byte[] data, bool reliable);

        /// <summary>
        /// Broadcast data to all connected peers.
        /// Thread-safe — may be called from any thread.
        /// </summary>
        /// <param name="data">Raw packet bytes</param>
        /// <param name="reliable">If true, guaranteed delivery with ordering</param>
        /// <param name="except">Optional peer to exclude from broadcast (typically the sender)</param>
        void Broadcast(byte[] data, bool reliable, ulong? except = null);

        /// <summary>
        /// MUST be called every frame from the main Unity thread.
        /// Drains the receive queue and fires events.
        /// Also handles keepalive and timeout checks.
        /// </summary>
        void Update();
    }
}
