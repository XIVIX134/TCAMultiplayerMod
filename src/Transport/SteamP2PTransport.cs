using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Steamworks;
using Steamworks.Data;
using TCAMultiplayer.Core;

namespace TCAMultiplayer.Transport
{
    /// <summary>
    /// Steam P2P transport using Facepunch.Steamworks legacy SteamNetworking API.
    /// <para>
    /// Host creates a Steam lobby for discovery; clients join via SteamId.
    /// NAT traversal and relay fallback are handled automatically by Steam.
    /// No background receive thread is needed — <see cref="Update"/> polls for
    /// incoming packets each frame via <see cref="SteamNetworking.IsP2PPacketAvailable"/>.
    /// </para>
    /// <para>
    /// PeerId mapping: SteamId.Value (ulong) is used directly as the peerId.
    /// The host's peerId is always 1; clients use their own SteamId.Value.
    /// </para>
    /// </summary>
    public sealed class SteamP2PTransport : ITransport
    {
        private const string Tag = "STEAM";

        // ── P2P channels ─────────────────────────────────────────────
        private const int CH_UNRELIABLE = 0;
        private const int CH_RELIABLE   = 1;

        // ── ITransport events ────────────────────────────────────────
        public event Action<ulong, byte[]> OnDataReceived;
        public event Action<ulong> OnPeerConnected;
        public event Action<ulong> OnPeerDisconnected;

        // ── ITransport properties ────────────────────────────────────
        public bool IsHost { get; private set; }
        public bool IsConnected { get; private set; }
        public ulong LocalPeerId { get; private set; }
        public IReadOnlyCollection<ulong> ConnectedPeers => _connectedPeerIds;

        // ── Configuration ────────────────────────────────────────────
        private readonly TransportConfig _config;

        // ── State ────────────────────────────────────────────────────
        private volatile bool _isRunning;
        private bool _disposed;

        // ── Peer tracking ────────────────────────────────────────────
        // SteamId.Value → peerId mapping (for host, peerId != SteamId.Value; host = 1)
        // For clients, host peerId is always 1.
        private readonly ConcurrentDictionary<ulong, SteamId> _peerSteamIds
            = new ConcurrentDictionary<ulong, SteamId>();
        private readonly Dictionary<ulong, ulong> _steamIdToPeerId
            = new Dictionary<ulong, ulong>();
        private readonly HashSet<ulong> _connectedPeerIds = new HashSet<ulong>();
        private readonly Dictionary<ulong, long> _lastReceivedMs = new Dictionary<ulong, long>();
        private ulong _nextPeerId = 2; // Host is always 1; clients start at 2

        // ── Event queue (for main-thread dispatch) ───────────────────
        private readonly ConcurrentQueue<TransportEvent> _eventQueue
            = new ConcurrentQueue<TransportEvent>();

        // ── Lobby ────────────────────────────────────────────────────
        private Lobby? _currentLobby;

        // ── Host SteamId (client-side: the host we're connecting to) ─
        private SteamId _hostSteamId;

        // ── Keepalive timing ─────────────────────────────────────────
        private long _lastKeepaliveMs;
        private readonly Stopwatch _clock = new Stopwatch();

        // ── Saved delegates for event cleanup ────────────────────────
        private Action<SteamId> _onP2PSessionRequest;
        private Action<SteamId, P2PSessionError> _onP2PConnectionFailed;
        private Action<Lobby, Friend> _onLobbyMemberJoined;
        private Action<Lobby, Friend> _onLobbyMemberLeave;
        private Action<Lobby, Friend> _onLobbyMemberDisconnected;

        public SteamP2PTransport(TransportConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _clock.Start();
        }

        // ═══════════════════════════════════════════════════════════════
        // ITransport — Lifecycle
        // ═══════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public void StartHost(int port)
        {
            ThrowIfSteamNotReady();

            if (_isRunning)
            {
                Log.Warning(Tag, "StartHost called while already running");
                return;
            }

            IsHost = true;
            IsConnected = true;
            LocalPeerId = 1;
            _isRunning = true;
            _lastKeepaliveMs = _clock.ElapsedMilliseconds;

            // Enable Steam relay fallback for NAT traversal
            SteamNetworking.AllowP2PPacketRelay(true);

            SubscribeSteamEvents();
            CreateLobbyAsync();

            Log.Info(Tag, $"Hosting Steam P2P session (SteamId: {SteamClient.SteamId})");
        }

        /// <inheritdoc />
        public void Connect(string address, int port)
        {
            ThrowIfSteamNotReady();

            if (_isRunning)
            {
                Log.Warning(Tag, "Connect called while already running");
                return;
            }

            // Address is the host's SteamId as a string
            if (!ulong.TryParse(address, out ulong hostId))
            {
                throw new ArgumentException(
                    $"Address must be a valid SteamId (ulong). Got: '{address}'", nameof(address));
            }

            _hostSteamId = hostId;
            IsHost = false;
            IsConnected = false;
            LocalPeerId = SteamClient.SteamId.Value;
            _isRunning = true;
            _lastKeepaliveMs = _clock.ElapsedMilliseconds;

            // Enable Steam relay fallback for NAT traversal
            SteamNetworking.AllowP2PPacketRelay(true);

            SubscribeSteamEvents();

            // Initiate P2P connection by sending an empty reliable packet to the host.
            // Steam will fire OnP2PSessionRequest on the host side.
            SteamNetworking.SendP2PPacket(
                _hostSteamId, new byte[] { 0x01 }, 1, CH_RELIABLE, P2PSend.Reliable);

            Log.Info(Tag, $"Connecting to Steam host {hostId}");
        }

        /// <inheritdoc />
        public void Disconnect()
        {
            if (!_isRunning) return;

            Shutdown();
            Log.Info(Tag, "Disconnected from Steam P2P session");
        }

        /// <inheritdoc />
        public void DisconnectPeer(ulong peerId)
        {
            if (!_isRunning || peerId == 0)
                return;

            if (IsHost)
            {
                if (!_peerSteamIds.TryGetValue(peerId, out var steamId))
                    return;

                SteamNetworking.SendP2PPacket(
                    steamId, new byte[] { 0x02 }, 1, CH_RELIABLE, P2PSend.Reliable);
                SteamNetworking.CloseP2PSessionWithUser(steamId);
                RemovePeer(peerId);
                _eventQueue.Enqueue(TransportEvent.PeerDisconnected(peerId));
                return;
            }

            if (peerId == 1)
                Disconnect();
        }

        // ═══════════════════════════════════════════════════════════════
        // ITransport — Send / Broadcast (thread-safe)
        // ═══════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public void Send(ulong peerId, byte[] data, bool reliable)
        {
            if (data == null || data.Length == 0) return;

            SteamId target;

            if (IsHost)
            {
                if (!_peerSteamIds.TryGetValue(peerId, out target))
                {
                    Log.Debug(Tag, $"Send: unknown peer {peerId}");
                    return;
                }
            }
            else
            {
                if (peerId != 1)
                {
                    Log.Debug(Tag, $"Client can only send to host (peer 1), got {peerId}");
                    return;
                }
                target = _hostSteamId;
            }

            int channel = reliable ? CH_RELIABLE : CH_UNRELIABLE;
            P2PSend sendType = reliable ? P2PSend.Reliable : P2PSend.UnreliableNoDelay;

            if (!SteamNetworking.SendP2PPacket(target, data, data.Length, channel, sendType))
            {
                Log.Debug(Tag, $"SendP2PPacket to {target} failed");
            }
        }

        /// <inheritdoc />
        public void Broadcast(byte[] data, bool reliable, ulong? except = null)
        {
            if (data == null || data.Length == 0) return;

            int channel = reliable ? CH_RELIABLE : CH_UNRELIABLE;
            P2PSend sendType = reliable ? P2PSend.Reliable : P2PSend.UnreliableNoDelay;

            if (IsHost)
            {
                foreach (var kvp in _peerSteamIds)
                {
                    if (except.HasValue && kvp.Key == except.Value) continue;
                    SteamNetworking.SendP2PPacket(kvp.Value, data, data.Length, channel, sendType);
                }
            }
            else
            {
                // Client only knows the host
                if (except.HasValue && except.Value == 1) return;
                SteamNetworking.SendP2PPacket(_hostSteamId, data, data.Length, channel, sendType);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // ITransport — Update (main thread only)
        // ═══════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public void Update()
        {
            if (!_isRunning) return;

            // 1. Poll all Steam P2P channels for incoming packets
            PollIncomingPackets(CH_UNRELIABLE);
            PollIncomingPackets(CH_RELIABLE);

            // 2. Drain event queue → fire callbacks on main thread
            while (_eventQueue.TryDequeue(out var evt))
            {
                switch (evt.Type)
                {
                    case TransportEventType.DataReceived:
                        OnDataReceived?.Invoke(evt.PeerId, evt.Data);
                        break;
                    case TransportEventType.PeerConnected:
                        OnPeerConnected?.Invoke(evt.PeerId);
                        break;
                    case TransportEventType.PeerDisconnected:
                        OnPeerDisconnected?.Invoke(evt.PeerId);
                        break;
                }
            }

            // 3. Keepalive / timeout checks
            ProcessKeepalive();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
        }

        // ═══════════════════════════════════════════════════════════════
        // Packet polling (main thread)
        // ═══════════════════════════════════════════════════════════════

        private void PollIncomingPackets(int channel)
        {
            // Read all available packets on this channel
            while (SteamNetworking.IsP2PPacketAvailable(channel))
            {
                var packet = SteamNetworking.ReadP2PPacket(channel);
                if (packet == null) break;

                P2Packet p = packet.Value;
                ulong senderSteamId = p.SteamId.Value;

                // Map SteamId → peerId
                ulong peerId;
                if (IsHost)
                {
                    if (!_steamIdToPeerId.TryGetValue(senderSteamId, out peerId))
                    {
                        // Unknown sender — could be initial connection packet.
                        // The OnP2PSessionRequest handler should have accepted them first.
                        // If we get data from an unknown peer, accept them now.
                        AcceptNewPeer(p.SteamId);
                        if (!_steamIdToPeerId.TryGetValue(senderSteamId, out peerId))
                            continue; // Still unknown — skip
                    }
                }
                else
                {
                    // Client: only expect data from host
                    if (senderSteamId == _hostSteamId.Value)
                    {
                        peerId = 1;

                        // Complete connection if we haven't yet
                        if (!IsConnected)
                        {
                            IsConnected = true;
                            AddPeer(1, _hostSteamId);
                            _eventQueue.Enqueue(TransportEvent.PeerConnected(1));
                        }
                    }
                    else
                    {
                        continue; // Ignore data from non-host
                    }
                }

                // Update last-received timestamp
                if (_lastReceivedMs.ContainsKey(peerId))
                    _lastReceivedMs[peerId] = _clock.ElapsedMilliseconds;

                // Skip the initial connection byte (0x01) — it's just a handshake trigger
                if (p.Data != null && p.Data.Length == 1 && p.Data[0] == 0x01)
                    continue;

                // Deliver payload
                if (p.Data != null && p.Data.Length > 0)
                {
                    _eventQueue.Enqueue(TransportEvent.DataReceived(peerId, p.Data));
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Steam event handlers
        // ═══════════════════════════════════════════════════════════════

        private void SubscribeSteamEvents()
        {
            _onP2PSessionRequest = OnSteamP2PSessionRequest;
            _onP2PConnectionFailed = OnSteamP2PConnectionFailed;
            _onLobbyMemberJoined = OnSteamLobbyMemberJoined;
            _onLobbyMemberLeave = OnSteamLobbyMemberLeave;
            _onLobbyMemberDisconnected = OnSteamLobbyMemberLeave; // Same handler

            SteamNetworking.OnP2PSessionRequest = _onP2PSessionRequest;
            SteamNetworking.OnP2PConnectionFailed = _onP2PConnectionFailed;
            SteamMatchmaking.OnLobbyMemberJoined += _onLobbyMemberJoined;
            SteamMatchmaking.OnLobbyMemberLeave += _onLobbyMemberLeave;
            SteamMatchmaking.OnLobbyMemberDisconnected += _onLobbyMemberDisconnected;
        }

        private void UnsubscribeSteamEvents()
        {
            // OnP2PSessionRequest is a simple delegate (not event), null it out
            if (SteamNetworking.OnP2PSessionRequest == _onP2PSessionRequest)
                SteamNetworking.OnP2PSessionRequest = null;
            if (SteamNetworking.OnP2PConnectionFailed == _onP2PConnectionFailed)
                SteamNetworking.OnP2PConnectionFailed = null;

            SteamMatchmaking.OnLobbyMemberJoined -= _onLobbyMemberJoined;
            SteamMatchmaking.OnLobbyMemberLeave -= _onLobbyMemberLeave;
            SteamMatchmaking.OnLobbyMemberDisconnected -= _onLobbyMemberDisconnected;
        }

        private void OnSteamP2PSessionRequest(SteamId remoteSteamId)
        {
            if (!_isRunning) return;

            if (IsHost)
            {
                // Enforce max connections
                if (_config.MaxConnections > 0 && _connectedPeerIds.Count >= _config.MaxConnections)
                {
                    Log.Warning(Tag, $"Rejecting P2P session from {remoteSteamId}: max connections ({_config.MaxConnections}) reached");
                    SteamNetworking.CloseP2PSessionWithUser(remoteSteamId);
                    return;
                }

                SteamNetworking.AcceptP2PSessionWithUser(remoteSteamId);
                Log.Debug(Tag, $"Accepted P2P session request from {remoteSteamId}");

                // Register peer if not already known
                if (!_steamIdToPeerId.ContainsKey(remoteSteamId.Value))
                {
                    AcceptNewPeer(remoteSteamId);
                }
            }
            else
            {
                // Client: only accept sessions from our host
                if (remoteSteamId.Value == _hostSteamId.Value)
                {
                    SteamNetworking.AcceptP2PSessionWithUser(remoteSteamId);
                    Log.Debug(Tag, $"Accepted P2P session from host {remoteSteamId}");
                }
                else
                {
                    Log.Debug(Tag, $"Rejected P2P session from unknown {remoteSteamId}");
                    SteamNetworking.CloseP2PSessionWithUser(remoteSteamId);
                }
            }
        }

        private void OnSteamP2PConnectionFailed(SteamId remoteSteamId, P2PSessionError error)
        {
            Log.Warning(Tag, $"P2P connection failed with {remoteSteamId}: {error}");

            ulong steamIdValue = remoteSteamId.Value;
            if (_steamIdToPeerId.TryGetValue(steamIdValue, out ulong peerId))
            {
                RemovePeer(peerId);
                _eventQueue.Enqueue(TransportEvent.PeerDisconnected(peerId));

                if (!IsHost && peerId == 1)
                    IsConnected = false;
            }
        }

        private void OnSteamLobbyMemberJoined(Lobby lobby, Friend friend)
        {
            if (!_isRunning) return;
            if (_currentLobby == null || lobby.Id != _currentLobby.Value.Id) return;

            Log.Info(Tag, $"Lobby member joined: {friend.Name} ({friend.Id})");
        }

        private void OnSteamLobbyMemberLeave(Lobby lobby, Friend friend)
        {
            if (!_isRunning) return;
            if (_currentLobby == null || lobby.Id != _currentLobby.Value.Id) return;

            ulong steamIdValue = friend.Id.Value;
            if (_steamIdToPeerId.TryGetValue(steamIdValue, out ulong peerId))
            {
                Log.Info(Tag, $"Lobby member left: {friend.Name} ({friend.Id}), peer {peerId}");
                SteamNetworking.CloseP2PSessionWithUser(friend.Id);
                RemovePeer(peerId);
                _eventQueue.Enqueue(TransportEvent.PeerDisconnected(peerId));

                if (!IsHost && peerId == 1)
                    IsConnected = false;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Peer management (main thread only)
        // ═══════════════════════════════════════════════════════════════

        private void AcceptNewPeer(SteamId remoteSteamId)
        {
            if (_steamIdToPeerId.ContainsKey(remoteSteamId.Value)) return;

            ulong peerId = _nextPeerId++;
            AddPeer(peerId, remoteSteamId);

            Log.Info(Tag, $"Peer {peerId} connected (SteamId: {remoteSteamId})");
            _eventQueue.Enqueue(TransportEvent.PeerConnected(peerId));

            // Send connection ACK: host replies with a packet so client knows it's connected
            SteamNetworking.SendP2PPacket(
                remoteSteamId, new byte[] { 0x01 }, 1, CH_RELIABLE, P2PSend.Reliable);
        }

        private void AddPeer(ulong peerId, SteamId steamId)
        {
            _peerSteamIds[peerId] = steamId;
            _steamIdToPeerId[steamId.Value] = peerId;
            _connectedPeerIds.Add(peerId);
            _lastReceivedMs[peerId] = _clock.ElapsedMilliseconds;
        }

        private void RemovePeer(ulong peerId)
        {
            if (_peerSteamIds.TryRemove(peerId, out SteamId steamId))
            {
                _steamIdToPeerId.Remove(steamId.Value);
            }
            _connectedPeerIds.Remove(peerId);
            _lastReceivedMs.Remove(peerId);
        }

        // ═══════════════════════════════════════════════════════════════
        // Keepalive & timeout (main thread)
        // ═══════════════════════════════════════════════════════════════

        private void ProcessKeepalive()
        {
            if (_connectedPeerIds.Count == 0) return;

            long nowMs = _clock.ElapsedMilliseconds;
            long timeoutMs = (long)(_config.TimeoutSeconds * 1000f);

            // Check for timed-out peers
            List<ulong> timedOut = null;
            foreach (var kvp in _lastReceivedMs)
            {
                if (nowMs - kvp.Value > timeoutMs)
                {
                    if (timedOut == null) timedOut = new List<ulong>();
                    timedOut.Add(kvp.Key);
                }
            }

            if (timedOut != null)
            {
                foreach (ulong peerId in timedOut)
                {
                    Log.Warning(Tag, $"Peer {peerId} timed out ({_config.TimeoutSeconds}s no data)");

                    // Close the P2P session
                    if (_peerSteamIds.TryGetValue(peerId, out SteamId steamId))
                    {
                        SteamNetworking.CloseP2PSessionWithUser(steamId);
                    }

                    RemovePeer(peerId);

                    if (!IsHost && peerId == 1)
                        IsConnected = false;

                    OnPeerDisconnected?.Invoke(peerId);
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Lobby management
        // ═══════════════════════════════════════════════════════════════

        private async void CreateLobbyAsync()
        {
            try
            {
                int maxMembers = _config.MaxConnections > 0 ? _config.MaxConnections + 1 : 8;
                var lobby = await SteamMatchmaking.CreateLobbyAsync(maxMembers);

                if (!lobby.HasValue)
                {
                    Log.Error(Tag, "Failed to create Steam lobby");
                    return;
                }

                _currentLobby = lobby.Value;
                _currentLobby.Value.SetPublic();
                _currentLobby.Value.SetJoinable(true);

                // Store metadata for discovery
                _currentLobby.Value.SetData("game", "TCAMP");
                _currentLobby.Value.SetData("version", "1.0");
                _currentLobby.Value.SetData("host_steamid", SteamClient.SteamId.Value.ToString());

                Log.Info(Tag, $"Steam lobby created: {_currentLobby.Value.Id}");
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"Failed to create Steam lobby: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Shutdown
        // ═══════════════════════════════════════════════════════════════

        private void Shutdown()
        {
            _isRunning = false;
            IsConnected = false;

            UnsubscribeSteamEvents();

            // Close all P2P sessions
            foreach (var kvp in _peerSteamIds)
            {
                try { SteamNetworking.CloseP2PSessionWithUser(kvp.Value); }
                catch { /* best-effort */ }
            }

            // Leave lobby
            if (_currentLobby.HasValue)
            {
                try { _currentLobby.Value.Leave(); }
                catch { /* best-effort */ }
                _currentLobby = null;
            }

            // Clear all peer state
            _peerSteamIds.Clear();
            _steamIdToPeerId.Clear();
            _connectedPeerIds.Clear();
            _lastReceivedMs.Clear();
            LocalPeerId = 0;

            // Drain any remaining queued events
            while (_eventQueue.TryDequeue(out _)) { }
        }

        // ═══════════════════════════════════════════════════════════════
        // Helpers
        // ═══════════════════════════════════════════════════════════════

        private static void ThrowIfSteamNotReady()
        {
            if (!SteamClient.IsValid)
            {
                throw new InvalidOperationException(
                    "Steam is not initialized. Ensure SteamClient.Init() has been called " +
                    "before using SteamP2PTransport. The game must be launched through Steam.");
            }
        }
    }
}
