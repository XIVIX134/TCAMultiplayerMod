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
    ///
    /// <para><b>Architecture:</b> The host creates a Steam lobby for discovery;
    /// clients join via the host's SteamId extracted from lobby metadata.
    /// NAT traversal and relay fallback are handled automatically by Steam's
    /// relay network — no port forwarding is required.</para>
    ///
    /// <para><b>Threading model:</b> All public methods are safe to call from
    /// the main Unity thread. <see cref="Send"/> and <see cref="Broadcast"/>
    /// are also safe from background threads because they delegate to
    /// <c>SteamNetworking.SendP2PPacket</c>, which is thread-safe.
    /// Incoming packets are polled in <see cref="Update"/> on the main thread
    /// via <see cref="SteamNetworking.IsP2PPacketAvailable"/>.</para>
    ///
    /// <para><b>PeerId mapping:</b> The host's peerId is always <c>1</c>.
    /// Clients use their own <c>SteamId.Value</c> as their peerId.
    /// The mapping is maintained in <see cref="_peerSteamIds"/> (peerId → SteamId)
    /// and <see cref="_steamIdToPeerId"/> (SteamId.Value → peerId).</para>
    ///
    /// <para><b>Lobby lifecycle:</b> The host creates a lobby in
    /// <see cref="StartHost"/> and sets metadata (<c>game</c>, <c>name</c>,
    /// <c>version</c>, <c>host_steamid</c>, <c>map</c>) before making it
    /// visible. Clients query the lobby list via
    /// <see cref="SteamMatchmaking.LobbyList"/> and extract the host SteamId
    /// from the <c>host_steamid</c> metadata key to connect.</para>
    /// </summary>
    public sealed class SteamP2PTransport : ITransport
    {
        private const string Tag = "STEAM";

        // ── P2P channels ─────────────────────────────────────────────
        // Channel 0: unreliable (state updates, keepalive)
        // Channel 1: reliable (lobby packets, handshake, chat)
        private const int CH_UNRELIABLE = 0;
        private const int CH_RELIABLE   = 1;

        // ── Protocol markers ─────────────────────────────────────────
        // Single-byte markers used to identify special packets in PollIncomingPackets.
        // These are distinct from application data to avoid accidental collisions.
        private const byte MARKER_HANDSHAKE = 0x01;
        private const byte MARKER_KEEPALIVE  = 0x02;
        private const byte MARKER_DISCONNECT = 0x03;

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

        // ── Peer tracking (main-thread only) ─────────────────────────
        // These dictionaries are only accessed from the main Unity thread
        // (Update, AcceptNewPeer, RemovePeer, Send, Broadcast). No locking
        // is needed because all mutations happen in Update() or event
        // handlers that run on the main thread.
        private readonly Dictionary<ulong, SteamId> _peerSteamIds = new Dictionary<ulong, SteamId>();
        private readonly Dictionary<ulong, ulong> _steamIdToPeerId = new Dictionary<ulong, ulong>();
        private readonly HashSet<ulong> _connectedPeerIds = new HashSet<ulong>();
        private readonly Dictionary<ulong, long> _lastReceivedMs = new Dictionary<ulong, long>();
        private ulong _nextPeerId = 2; // Host is always 1; clients start at 2

        // ── Event queue (for main-thread dispatch) ───────────────────
        private readonly ConcurrentQueue<TransportEvent> _eventQueue
            = new ConcurrentQueue<TransportEvent>();

        // ── Lobby ────────────────────────────────────────────────────
        private Lobby? _currentLobby;
        private string _currentMapName;

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

            // Enable Steam relay fallback for NAT traversal.
            // This allows connections through Steam's relay network when
            // direct P2P fails (symmetric NAT, firewalls, etc.).
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

            // Address is the host's SteamId as a string (from lobby metadata)
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

            // Initiate P2P connection by sending a handshake packet to the host.
            // Steam will fire OnP2PSessionRequest on the host side, which calls
            // AcceptP2PSessionWithUser to establish the bidirectional channel.
            bool sent = SteamNetworking.SendP2PPacket(
                _hostSteamId, new byte[] { MARKER_HANDSHAKE }, 1, CH_RELIABLE, P2PSend.Reliable);
            Log.Info(Tag, $"Sent handshake to {hostId}: {sent}");

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
                    steamId, new byte[] { MARKER_DISCONNECT }, 1, CH_RELIABLE, P2PSend.Reliable);
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
                // Client can only send to the host (peerId 1)
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
            // Read all available packets on this channel.
            // SteamNetworking.IsP2PPacketAvailable returns the count of
            // queued packets; we drain them all to avoid queue buildup.
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
                        // The OnP2PSessionRequest handler should have accepted
                        // them first. If we get data from an unknown peer,
                        // accept them now as a fallback.
                        Log.Debug(Tag, $"Host received data from unknown peer {senderSteamId}, accepting...");
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
                            Log.Info(Tag, "Client received data from host — marking connected");
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

                // Update last-received timestamp for timeout detection
                if (_lastReceivedMs.ContainsKey(peerId))
                    _lastReceivedMs[peerId] = _clock.ElapsedMilliseconds;

                // Explicit disconnect signal (e.g. host kicking a client). Tear
                // the peer down now instead of waiting for the keepalive timeout.
                if (p.Data != null && p.Data.Length == 1 && p.Data[0] == MARKER_DISCONNECT)
                {
                    Log.Info(Tag, $"Received disconnect marker from {senderSteamId} (peer {peerId})");
                    if (_peerSteamIds.TryGetValue(peerId, out SteamId dcSteamId))
                        SteamNetworking.CloseP2PSessionWithUser(dcSteamId);
                    RemovePeer(peerId);
                    if (!IsHost && peerId == 1)
                        IsConnected = false;
                    _eventQueue.Enqueue(TransportEvent.PeerDisconnected(peerId));
                    continue;
                }

                // Skip protocol markers — they're not application data.
                // MARKER_HANDSHAKE (0x01): initial connection trigger
                // MARKER_KEEPALIVE (0x02): keepalive ping
                if (p.Data != null && p.Data.Length == 1
                    && (p.Data[0] == MARKER_HANDSHAKE || p.Data[0] == MARKER_KEEPALIVE))
                {
                    Log.Debug(Tag, $"Received marker 0x{p.Data[0]:X2} from {senderSteamId} (peer {peerId})");
                    continue;
                }

                // Deliver payload to the reliability layer / packet router
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
            _onLobbyMemberDisconnected = OnSteamLobbyMemberLeave;

            SteamNetworking.OnP2PSessionRequest = _onP2PSessionRequest;
            SteamNetworking.OnP2PConnectionFailed = _onP2PConnectionFailed;
            SteamMatchmaking.OnLobbyMemberJoined += _onLobbyMemberJoined;
            SteamMatchmaking.OnLobbyMemberLeave += _onLobbyMemberLeave;
            SteamMatchmaking.OnLobbyMemberDisconnected += _onLobbyMemberDisconnected;
        }

        private void UnsubscribeSteamEvents()
        {
            if (SteamNetworking.OnP2PSessionRequest == _onP2PSessionRequest)
                SteamNetworking.OnP2PSessionRequest = null;
            if (SteamNetworking.OnP2PConnectionFailed == _onP2PConnectionFailed)
                SteamNetworking.OnP2PConnectionFailed = null;

            SteamMatchmaking.OnLobbyMemberJoined -= _onLobbyMemberJoined;
            SteamMatchmaking.OnLobbyMemberLeave -= _onLobbyMemberLeave;
            SteamMatchmaking.OnLobbyMemberDisconnected -= _onLobbyMemberDisconnected;
        }

        /// <summary>
        /// Called by Steam when a remote peer wants to establish a P2P session.
        /// The host accepts all peers (up to MaxConnections); clients only
        /// accept sessions from the known host.
        /// </summary>
        private void OnSteamP2PSessionRequest(SteamId remoteSteamId)
        {
            if (!_isRunning) return;

            if (IsHost)
            {
                // Enforce max connections
                if (_config.MaxConnections > 0 && _connectedPeerIds.Count >= _config.MaxConnections)
                {
                    Log.Warning(Tag, $"Rejecting P2P session from {remoteSteamId}: " +
                        $"max connections ({_config.MaxConnections}) reached");
                    SteamNetworking.CloseP2PSessionWithUser(remoteSteamId);
                    return;
                }

                bool accepted = SteamNetworking.AcceptP2PSessionWithUser(remoteSteamId);
                Log.Info(Tag, $"Host AcceptP2PSessionWithUser({remoteSteamId}) = {accepted}");
                if (!accepted)
                {
                    Log.Error(Tag, $"Failed to accept P2P session from {remoteSteamId}");
                    return;
                }

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
                    bool accepted = SteamNetworking.AcceptP2PSessionWithUser(remoteSteamId);
                    Log.Info(Tag, $"Client AcceptP2PSessionWithUser({remoteSteamId}) = {accepted}");
                    if (!accepted)
                    {
                        Log.Error(Tag, $"Failed to accept P2P session from host {remoteSteamId}");
                        return;
                    }
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

            // Send connection ACK so the client knows it's connected
            SteamNetworking.SendP2PPacket(
                remoteSteamId, new byte[] { MARKER_HANDSHAKE }, 1, CH_RELIABLE, P2PSend.Reliable);
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
            if (_peerSteamIds.TryGetValue(peerId, out SteamId steamId))
            {
                _steamIdToPeerId.Remove(steamId.Value);
            }
            _peerSteamIds.Remove(peerId);
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
            long keepaliveIntervalMs = (long)(_config.KeepaliveInterval * 1000f);

            // 1. Send keepalive pings if interval elapsed
            if (nowMs - _lastKeepaliveMs > keepaliveIntervalMs)
            {
                _lastKeepaliveMs = nowMs;
                byte[] keepalive = new byte[] { MARKER_KEEPALIVE };
                foreach (var peerId in _connectedPeerIds)
                {
                    if (IsHost)
                    {
                        if (_peerSteamIds.TryGetValue(peerId, out SteamId steamId))
                        {
                            SteamNetworking.SendP2PPacket(
                                steamId, keepalive, keepalive.Length,
                                CH_UNRELIABLE, P2PSend.UnreliableNoDelay);
                        }
                    }
                    else if (peerId == 1)
                    {
                        SteamNetworking.SendP2PPacket(
                            _hostSteamId, keepalive, keepalive.Length,
                            CH_UNRELIABLE, P2PSend.UnreliableNoDelay);
                    }
                }
            }

            // 2. Check for timed-out peers
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

        /// <summary>
        /// Create a Steam lobby for game discovery. The lobby is created
        /// asynchronously; metadata is set before making it visible so
        /// it's fully searchable the moment it appears in the listing.
        /// </summary>
        private async void CreateLobbyAsync()
        {
            try
            {
                // Check if transport was disposed/switched while awaiting
                if (!_isRunning)
                {
                    Log.Debug(Tag, "CreateLobbyAsync cancelled — transport no longer running");
                    return;
                }

                int maxMembers = _config.MaxConnections > 0 ? _config.MaxConnections + 1 : 8;
                Log.Info(Tag, $"Requesting Steam lobby creation with maxMembers={maxMembers}...");
                var lobby = await SteamMatchmaking.CreateLobbyAsync(maxMembers);

                // Check again after await
                if (!_isRunning)
                {
                    Log.Debug(Tag, "CreateLobbyAsync cancelled after await — transport no longer running");
                    if (lobby.HasValue)
                    {
                        try { lobby.Value.Leave(); } catch { }
                    }
                    return;
                }

                if (!lobby.HasValue)
                {
                    Log.Error(Tag, "SteamMatchmaking.CreateLobbyAsync returned null — lobby creation failed");
                    return;
                }

                _currentLobby = lobby.Value;
                Log.Info(Tag, $"Lobby created: id={_currentLobby.Value.Id}, " +
                    $"owner={_currentLobby.Value.Owner.Name} (SteamId={_currentLobby.Value.Owner.Id})");

                SetLobbyMetadata();
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"Failed to create Steam lobby: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Set all lobby metadata keys. Called after lobby creation and
        /// whenever settings change so the lobby browser always shows
        /// current information.
        /// </summary>
        private void SetLobbyMetadata()
        {
            if (!_currentLobby.HasValue) return;

            string serverName = ModConfig.HostServerName?.Value ?? "TCA Server";
            string hostSteamId = SteamClient.SteamId.Value.ToString();
            string hostName = SteamClient.Name ?? "Host";
            string version = "1.0";

            _currentLobby.Value.SetData("game", "TCAMP");
            _currentLobby.Value.SetData("name", serverName);
            _currentLobby.Value.SetData("host_name", hostName);
            _currentLobby.Value.SetData("version", version);
            _currentLobby.Value.SetData("host_steamid", hostSteamId);

            // Set map if known (the lobby browser displays this)
            if (!string.IsNullOrEmpty(_currentMapName))
                _currentLobby.Value.SetData("map", _currentMapName);

            // Apply lobby visibility from config
            string lobbyType = ModConfig.HostSteamLobbyType?.Value?.Trim() ?? "Public";
            if (string.Equals(lobbyType, "FriendsOnly", StringComparison.OrdinalIgnoreCase))
            {
                _currentLobby.Value.SetFriendsOnly();
                Log.Info(Tag, $"Lobby {_currentLobby.Value.Id} set to FriendsOnly");
            }
            else
            {
                _currentLobby.Value.SetPublic();
                Log.Info(Tag, $"Lobby {_currentLobby.Value.Id} set to Public");
            }

            _currentLobby.Value.SetJoinable(true);

            Log.Info(Tag, $"Lobby metadata set: game=TCAMP, name={serverName}, " +
                $"map={_currentMapName ?? "<not set>"}, host_steamid={hostSteamId}, " +
                $"type={lobbyType}, joinable=true, " +
                $"members={_currentLobby.Value.MemberCount}/{_currentLobby.Value.MaxMembers}");
        }

        /// <summary>
        /// Update the lobby's <c>map</c> metadata when the host changes maps.
        /// This ensures the lobby browser always shows the current map.
        /// </summary>
        public void UpdateLobbyMap(string mapName)
        {
            _currentMapName = mapName;
            if (!_currentLobby.HasValue) return;

            try
            {
                _currentLobby.Value.SetData("map", mapName ?? "");
                Log.Debug(Tag, $"Lobby map metadata updated: {mapName}");
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Failed to update lobby map metadata: {ex.Message}");
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
                catch (Exception ex) { Log.Debug(Tag, $"CloseP2PSessionWithUser error: {ex.Message}"); }
            }

            // Leave lobby
            if (_currentLobby.HasValue)
            {
                try { _currentLobby.Value.Leave(); }
                catch (Exception ex) { Log.Debug(Tag, $"Lobby leave error: {ex.Message}"); }
                _currentLobby = null;
            }

            // Clear all peer state
            _peerSteamIds.Clear();
            _steamIdToPeerId.Clear();
            _connectedPeerIds.Clear();
            _lastReceivedMs.Clear();
            _nextPeerId = 2;
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
