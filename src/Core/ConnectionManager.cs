using System;
using System.Collections.Generic;
using System.Linq;
using TCAMultiplayer.Protocol;
using TCAMultiplayer.Transport;

namespace TCAMultiplayer.Core
{
    /// <summary>
    /// Glue between transport, protocol, and session state.
    /// Wires ITransport events → ReliabilityLayer → PacketRouter → GameSession.
    ///
    /// Lifecycle:
    ///   HostSession()  → transport.StartHost() + session → HostingLobby
    ///   JoinSession()  → transport.Connect()   + session → ClientLobby
    ///   Disconnect()   → transport.Disconnect() + session.Dispose() → Disconnected
    ///
    /// Update() must be called every frame from Plugin's MonoBehaviour.
    ///
    /// <para><b>Connection timeout:</b> When a client joins, a timeout timer
    /// starts. If the host doesn't send a Welcome packet within
    /// <see cref="ConnectionTimeoutSeconds"/>, the client automatically
    /// disconnects and fires <see cref="OnConnectionFailed"/> with a reason.</para>
    /// </summary>
    public class ConnectionManager : IDisposable
    {
        private const string Tag = "CONN";

        /// <summary>
        /// Seconds to wait for a Welcome packet from the host before
        /// giving up and disconnecting. 0 disables the timeout.
        /// </summary>
        private const float ConnectionTimeoutSeconds = 15f;

        private ITransport _transport;
        private readonly TransportConfig _config;
        private ReliabilityLayer _reliability;
        private readonly PacketRouter _router;

        private GameSession _session;
        private bool _disposed;

        // ── Connection timeout (client-side) ──────────────────────────
        private float _connectionElapsed;
        private bool _waitingForWelcome;

        // ── Public accessors ─────────────────────────────────────────────

        /// <summary>Current session, or null when disconnected.</summary>
        public GameSession Session => _session;

        /// <summary>Fired when a new GameSession is created (after HostSession or JoinSession).</summary>
        public event Action<GameSession> OnSessionCreated;

        /// <summary>Fired after the current GameSession has ended and packet handlers were cleared.</summary>
        public event Action OnSessionEnded;

        /// <summary>Fired after a peer leaves the current session.</summary>
        public event Action<ulong> OnPeerLeft;

        /// <summary>
        /// Fired when a client-side connection attempt fails (e.g., timeout
        /// waiting for host Welcome). The string argument is a human-readable
        /// reason.
        /// </summary>
        public event Action<string> OnConnectionFailed;


        /// <summary>Packet router for handler registration.</summary>
        public PacketRouter Router => _router;

        /// <summary>True when a GameSession exists, even if transport connection is still handshaking.</summary>
        public bool HasSession => _session != null;

        /// <summary>True if the transport is currently connected.</summary>
        public bool IsConnected => _transport?.IsConnected ?? false;

        /// <summary>Peer IDs currently connected at the transport level.</summary>
        public IReadOnlyCollection<ulong> ConnectedPeers
            => _transport?.ConnectedPeers?.ToArray() ?? Array.Empty<ulong>();

        /// <summary>Transport configuration backing the current connection.</summary>
        public TransportConfig Config => _config;

        /// <summary>True if the local peer is hosting.</summary>
        public bool IsHost => _transport?.IsHost ?? false;

        /// <summary>Current transport instance.</summary>
        public ITransport Transport => _transport;

        // ── Constructor ──────────────────────────────────────────────────

        public ConnectionManager(ITransport transport, TransportConfig config = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _config = config ?? new TransportConfig();

            _router = new PacketRouter();
            _reliability = new ReliabilityLayer(_transport, _config);

            // Wire transport events
            _transport.OnPeerConnected += HandlePeerConnected;
            _transport.OnPeerDisconnected += HandlePeerDisconnected;
            _transport.OnDataReceived += HandleDataReceived;

            // Wire reliability → router
            _reliability.OnDataReady += HandleDataReady;
        }

        /// <summary>
        /// Switch to a different transport. Disconnects current transport,
        /// unwires events, creates new reliability layer, and wires new transport.
        /// </summary>
        public void SetTransport(ITransport newTransport)
        {
            if (newTransport == null)
                throw new ArgumentNullException(nameof(newTransport));

            if (_transport == newTransport)
                return;

            // Disconnect and cleanup current transport
            if (_transport != null)
            {
                _transport.OnPeerConnected -= HandlePeerConnected;
                _transport.OnPeerDisconnected -= HandlePeerDisconnected;
                _transport.OnDataReceived -= HandleDataReceived;
                _transport.Disconnect();
            }

            // Clear reliability layer and router (similar to Disconnect)
            _reliability?.Clear();
            _router.Clear();

            _transport = newTransport;
            _reliability = new ReliabilityLayer(_transport, _config);

            // Wire new transport events
            _transport.OnPeerConnected += HandlePeerConnected;
            _transport.OnPeerDisconnected += HandlePeerDisconnected;
            _transport.OnDataReceived += HandleDataReceived;

            // Wire reliability → router
            _reliability.OnDataReady += HandleDataReady;

            Log.Info(Tag, $"Switched to transport: {_transport.GetType().Name}");
        }

        // ── Lifecycle ────────────────────────────────────────────────────

        /// <summary>
        /// Start hosting a session on the specified port.
        /// Creates a GameSession, starts the transport, and transitions to HostingLobby.
        /// </summary>
        public void HostSession(string hostName, int port)
        {
            ThrowIfDisposed();

            if (_session != null)
            {
                Log.Warning(Tag, "Already in a session — disconnect first");
                return;
            }

            _reliability.Clear();
            _transport.StartHost(port);

            _session = new GameSession(isHost: true);
            _session.LocalPeerId = 1; // Host is always peer 1 regardless of transport
            _session.HostName = hostName ?? "Host";
            _session.StateMachine.TryTransition(GameState.HostingLobby);

            // Register the host as the first player
            _session.AddPlayer(_session.LocalPeerId, hostName ?? "Host");

            Log.Info(Tag, $"Hosting session \"{hostName}\" on port {port} (peerId={_session.LocalPeerId})");
            OnSessionCreated?.Invoke(_session);
        }

        /// <summary>
        /// Join an existing session at the given address and port.
        /// Creates a GameSession, connects the transport, and transitions to ClientLobby.
        /// The local player is added once the connection handshake completes (via OnPeerConnected).
        /// A connection timeout starts; if the host doesn't respond within
        /// <see cref="ConnectionTimeoutSeconds"/>, the attempt is aborted.
        /// </summary>
        public void JoinSession(string address, int port)
        {
            ThrowIfDisposed();

            if (_session != null)
            {
                Log.Warning(Tag, "Already in a session — disconnect first");
                return;
            }

            _reliability.Clear();
            _transport.Connect(address, port);

            _session = new GameSession(isHost: false);
            _session.LocalPeerId = _transport.LocalPeerId;
            _session.StateMachine.TryTransition(GameState.ClientLobby);

            // Start connection timeout timer
            _connectionElapsed = 0f;
            _waitingForWelcome = ConnectionTimeoutSeconds > 0f;

            Log.Info(Tag, $"Joining session at {address}:{port} (peerId={_session.LocalPeerId})");
            OnSessionCreated?.Invoke(_session);
        }

        /// <summary>
        /// Disconnect from the current session.
        /// Transitions to Disconnected, disposes the session, and disconnects the transport.
        /// Safe to call when already disconnected.
        /// </summary>
        public void Disconnect()
        {
            if (_disposed) return;

            bool hadSession = _session != null;

            if (_session != null)
            {
                _session.StateMachine.TryTransition(GameState.Disconnected);
                _session.Dispose();
                _session = null;
            }

            _transport.Disconnect();

            _reliability.Clear();
            _router.Clear();

            if (hadSession)
                OnSessionEnded?.Invoke();

            Log.Info(Tag, "Disconnected");
        }

        // ── Update loop ─────────────────────────────────────────────────

        /// <summary>
        /// Must be called every frame from Plugin's MonoBehaviour.
        /// Drains the transport receive queue, retransmits pending reliable packets,
        /// and checks for client connection timeout.
        /// </summary>
        public void Update(float deltaTime)
        {
            if (_disposed) return;

            // Drain receive queue — fires OnDataReceived/OnPeerConnected/OnPeerDisconnected
            _transport.Update();

            // Retransmit pending reliable packets
            _reliability.Update(deltaTime);

            // Client connection timeout: disconnect if Welcome not received in time.
            // Only applies during initial connection (ClientLobby state).
            if (_waitingForWelcome && _session != null && !_session.IsHost)
            {
                // Disable timeout once past initial lobby connection
                if (_session.StateMachine.CurrentState != GameState.ClientLobby)
                {
                    _waitingForWelcome = false;
                }
                else
                {
                    _connectionElapsed += deltaTime;
                    if (_connectionElapsed > ConnectionTimeoutSeconds)
                    {
                        _waitingForWelcome = false;
                        string reason = $"Connection timed out after {ConnectionTimeoutSeconds:F0}s — " +
                            "host did not respond. Check that the host is running and reachable.";
                        Log.Warning(Tag, reason);
                        OnConnectionFailed?.Invoke(reason);
                        Disconnect();
                    }
                }
            }
        }

        // ── Send helpers (delegate to reliability layer) ────────────────

        /// <summary>Send data reliably to a specific peer.</summary>
        public void SendReliable(ulong peerId, byte[] data)
        {
            ThrowIfDisposed();
            _reliability.SendReliable(peerId, data);
        }

        /// <summary>Send data unreliably to a specific peer.</summary>
        public void SendUnreliable(ulong peerId, byte[] data)
        {
            ThrowIfDisposed();
            _reliability.SendUnreliable(peerId, data);
        }

        /// <summary>Broadcast data reliably to all connected peers.</summary>
        public void BroadcastReliable(byte[] data, ulong? except = null)
        {
            ThrowIfDisposed();
            _reliability.BroadcastReliable(data, except);
        }

        /// <summary>Broadcast data unreliably to all connected peers.</summary>
        public void BroadcastUnreliable(byte[] data, ulong? except = null)
        {
            ThrowIfDisposed();
            _reliability.BroadcastUnreliable(data, except);
        }

        /// <summary>Number of reliable packets still awaiting ACK for a peer.</summary>
        public int GetReliablePendingCount(ulong peerId)
        {
            return _reliability.GetPendingCount(peerId);
        }

        // ── Dispose ─────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;

            Disconnect();
            _disposed = true;

            // Unwire all event subscriptions
            _transport.OnPeerConnected -= HandlePeerConnected;
            _transport.OnPeerDisconnected -= HandlePeerDisconnected;
            _transport.OnDataReceived -= HandleDataReceived;
            _reliability.OnDataReady -= HandleDataReady;
            OnSessionCreated = null;
            OnSessionEnded = null;
            OnPeerLeft = null;
            OnConnectionFailed = null;
        }

        // ── Event handlers (private) ────────────────────────────────────

        private void HandlePeerConnected(ulong peerId)
        {
            if (_session == null) return;

            // On CLIENT: transport assigns our real peer ID during handshake.
            // Update session's LocalPeerId and add ourselves to the roster.
            if (!_session.IsHost && _session.LocalPeerId == 0 && _transport.LocalPeerId != 0)
            {
                _session.LocalPeerId = _transport.LocalPeerId;
                var username = ModConfig.Username?.Value ?? "Player";
                _session.AddPlayer(_session.LocalPeerId, username);
                _waitingForWelcome = false; // Connection succeeded — cancel timeout
                Log.Info(Tag, $"Client assigned peer ID {_session.LocalPeerId}");
            }

            // For Steam transport, LocalPeerId is set upfront (SteamID), so the above
            // condition never triggers. Cancel timeout when host (peer 1) connects.
            if (!_session.IsHost && peerId == 1)
            {
                _waitingForWelcome = false;
                Log.Debug(Tag, "Host connected — cancelling connection timeout");
            }

            // Add the remote peer (host sees client, client sees host)
            _session.AddPlayer(peerId, $"Peer_{peerId}");
            Log.Info(Tag, $"Peer {peerId} connected");

            // HOST: send Welcome packet with the new peer's assigned ID
            if (_session.IsHost)
            {
                try
                {
                    var welcomePkt = new Protocol.LobbyWelcomePacket
                    {
                        AssignedPeerId = peerId,
                        HostName = _session.HostName ?? "Host"
                    };
                    var payload = Protocol.PacketSerializer.SerializeLobbyWelcome(welcomePkt);
                    var frame = Protocol.PacketSerializer.Serialize(Protocol.PacketType.LobbyWelcome, payload);
                    SendReliable(peerId, frame);
                    Log.Info(Tag, $"Sent Welcome to peer {peerId}");
                }
                catch (System.Exception ex)
                {
                    Log.Error(Tag, $"Failed to send Welcome: {ex.Message}");
                }
            }
        }

        private void HandlePeerDisconnected(ulong peerId)
        {
            if (_session == null) return;

            if (!_session.IsHost && peerId == 1)
            {
                _reliability.RemovePeer(peerId);
                Log.Warning(Tag, "Host disconnected — ending client session");
                OnPeerLeft?.Invoke(peerId);
                Disconnect();
                return;
            }

            _session.RemovePlayer(peerId);
            _reliability.RemovePeer(peerId);
            Log.Info(Tag, $"Peer {peerId} disconnected");
            OnPeerLeft?.Invoke(peerId);
        }

        /// <summary>
        /// Raw transport data → reliability layer.
        /// The reliability layer strips framing and fires OnDataReady for application data.
        /// </summary>
        private void HandleDataReceived(ulong peerId, byte[] data)
        {
            _reliability.HandleReceived(peerId, data);
        }

        /// <summary>
        /// Processed application data → packet router.
        /// Router dispatches to registered handlers by PacketType (first byte).
        /// </summary>
        private void HandleDataReady(ulong peerId, byte[] data)
        {
            _router.Route(peerId, data);
        }

        // ── Guards ──────────────────────────────────────────────────────

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ConnectionManager));
        }
    }
}
