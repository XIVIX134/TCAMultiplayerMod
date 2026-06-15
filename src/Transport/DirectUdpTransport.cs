using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using TCAMultiplayer.Core;

namespace TCAMultiplayer.Transport
{
    /// <summary>
    /// Thread-safe, N-player direct UDP transport.
    /// <para>
    /// Host listens on a UDP port; clients connect via IP:port.
    /// A background receive thread enqueues raw packets into a <see cref="ConcurrentQueue{T}"/>;
    /// <see cref="Update"/> drains the queue on the main thread and fires events.
    /// <see cref="Send"/> / <see cref="Broadcast"/> may be called from any thread.
    /// </para>
    /// <para>
    /// Reliability (ACK/retransmit) is NOT implemented here — that is T10's responsibility.
    /// The <c>reliable</c> flag is stored in packet headers for future use.
    /// </para>
    /// </summary>
    public sealed class DirectUdpTransport : ITransport, ITransportDiagnostics
    {
        private const string Tag = "UDP";

        // ── Packet types (first byte of every packet) ────────────────
        private const byte PKT_DATA        = 0x00;
        private const byte PKT_CONNECT     = 0x01;
        private const byte PKT_CONNECT_ACK = 0x02;
        private const byte PKT_DISCONNECT  = 0x03;
        private const byte PKT_PING        = 0x04;
        private const byte PKT_PONG        = 0x05;
        private const byte PKT_DATA_V2     = 0x06;
        private const byte PKT_CONNECT_REJECT = 0x07;

        /// <summary>Magic bytes to validate CONNECT packets: ASCII "TCAM".</summary>
        private static readonly byte[] ConnectMagic = { 0x54, 0x43, 0x41, 0x4D };
        private const byte LegacyProtocolVersion = 1;
        private const byte ConnectProtocolVersion = 2;
        private const int ConnectHeaderSize = 1 + 4 + 1 + 8; // type + magic + protocol version + client token
        private const int ConnectAckSize = 1 + 8 + 8;        // type + assigned peer ID + echoed token

        // ── ITransport events ────────────────────────────────────────
        public event Action<ulong, byte[]> OnDataReceived;
        public event Action<ulong> OnPeerConnected;
        public event Action<ulong> OnPeerDisconnected;
        public event Action<string> OnStatusChanged;

        // ── ITransport properties ────────────────────────────────────
        public bool IsHost { get; private set; }
        public bool IsConnected { get; private set; }
        public ulong LocalPeerId { get; private set; }
        public IReadOnlyCollection<ulong> ConnectedPeers => _connectedPeerIds;
        public string StatusMessage { get; private set; } = "";

        // ── Configuration ────────────────────────────────────────────
        private readonly TransportConfig _config;
        private readonly ulong _clientToken;

        // ── Socket & threading ───────────────────────────────────────
        private UdpClient _udpClient;
        private Thread _receiveThread;
        private volatile bool _isRunning;

        // ── Peer tracking ────────────────────────────────────────────
        // ConcurrentDictionary for peer→endpoint: read from any thread (Send/Broadcast),
        // written on main thread (Update). Lock-free TryGetValue for the send hot-path.
        private readonly ConcurrentDictionary<ulong, IPEndPoint> _peerEndpoints
            = new ConcurrentDictionary<ulong, IPEndPoint>();

        // Regular collections below are main-thread-only (accessed exclusively from Update).
        private readonly Dictionary<string, ulong> _endpointToPeer = new Dictionary<string, ulong>();
        private readonly ConcurrentDictionary<ulong, byte> _peerProtocolVersions =
            new ConcurrentDictionary<ulong, byte>();
        private readonly Dictionary<ulong, ulong> _peerTokens = new Dictionary<ulong, ulong>();
        private readonly Dictionary<ulong, ulong> _tokenToPeer = new Dictionary<ulong, ulong>();
        private readonly HashSet<ulong> _connectedPeerIds = new HashSet<ulong>();
        private readonly Dictionary<ulong, long> _lastReceivedMs = new Dictionary<ulong, long>();
        private readonly Dictionary<ulong, float> _lastRttMs = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, float> _smoothedRttMs = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, uint> _nextPingIds = new Dictionary<ulong, uint>();
        private readonly Dictionary<ulong, Dictionary<uint, long>> _pendingPingSentMs =
            new Dictionary<ulong, Dictionary<uint, long>>();
        // Peers past TimeoutSeconds but inside the reconnect grace window. They stay
        // registered (keepalives keep flowing) until the grace expires or data resumes.
        private readonly HashSet<ulong> _lostPeers = new HashSet<ulong>();
        private ulong _nextPeerId = 2; // Host is always 1; clients start at 2

        // ── Diagnostics ───────────────────────────────────────────────
        private long _lastAnyReceiveMs;
        private long _metricsWindowStartedMs;
        private long _windowPacketsSent;
        private long _windowPacketsReceived;
        private long _windowBytesSent;
        private long _windowBytesReceived;
        private int _lastWindowPacketsSent;
        private int _lastWindowPacketsReceived;
        private float _lastOutgoingKbps;
        private float _lastIncomingKbps;
        private bool _hasReceivedPacket;
        private string _routeDescription = "OS default route";

        // ── Cross-thread receive queue ───────────────────────────────
        private readonly ConcurrentQueue<(IPEndPoint Sender, byte[] Data)> _receiveQueue
            = new ConcurrentQueue<(IPEndPoint, byte[])>();

        // ── Client-side handshake state ──────────────────────────────
        private volatile bool _waitingForConnection;
        private IPEndPoint _hostEndpoint;
        private long _connectSentMs;
        private long _connectStartedMs;
        private long _lastConnectLogMs;
        private int _connectAttemptCount;

        // ── Keepalive timing (Stopwatch for thread-safe, Unity-independent time) ─
        private long _lastKeepaliveMs;
        private long _lastEndpointRefreshMs;
        private readonly Stopwatch _clock = new Stopwatch();

        // ── Disposal ─────────────────────────────────────────────────
        private bool _disposed;

        public DirectUdpTransport(TransportConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _clientToken = _config.ClientToken != 0 ? _config.ClientToken : GenerateClientToken();
            _clock.Start();
        }

        // ═══════════════════════════════════════════════════════════════
        // ITransport — Lifecycle
        // ═══════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public void StartHost(int port)
        {
            if (_isRunning)
            {
                Log.Warning(Tag, "StartHost called while already running");
                return;
            }

            try
            {
                _udpClient = CreateUdpClient(port);
                IsHost = true;
                IsConnected = true; // Host is always "connected" once listening
                LocalPeerId = 1;
                _isRunning = true;
                _lastKeepaliveMs = _clock.ElapsedMilliseconds;
                _lastEndpointRefreshMs = _lastKeepaliveMs;
                _lastAnyReceiveMs = _lastKeepaliveMs;
                _metricsWindowStartedMs = _lastKeepaliveMs;

                StartReceiveThread();
                SetStatus($"Hosting on {GetLocalEndpointText()}");
                Log.Info(Tag, $"Hosting on {GetLocalEndpointText()} ({_routeDescription})");
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"Failed to start host on port {port}: {ex.Message}");
                throw;
            }
        }

        /// <inheritdoc />
        public void Connect(string address, int port)
        {
            if (_isRunning)
            {
                Log.Warning(Tag, "Connect called while already running");
                return;
            }

            try
            {
                _hostEndpoint = ResolveRemoteEndPoint(address, port);
                _udpClient = CreateUdpClient(0, _hostEndpoint.Address); // OS-assigned local port
                IsHost = false;
                IsConnected = false;
                LocalPeerId = 0; // Assigned by host in CONNECT_ACK
                _isRunning = true;
                _waitingForConnection = true;
                _lastKeepaliveMs = _clock.ElapsedMilliseconds;
                _lastEndpointRefreshMs = _lastKeepaliveMs;
                _lastAnyReceiveMs = _lastKeepaliveMs;
                _metricsWindowStartedMs = _lastKeepaliveMs;
                _connectStartedMs = _lastKeepaliveMs;
                _lastConnectLogMs = _connectStartedMs;
                _connectAttemptCount = 0;

                StartReceiveThread();
                SendConnectPacket(countAsConnectAttempt: true);

                SetStatus($"Connecting to {_hostEndpoint.Address}:{_hostEndpoint.Port}");
                Log.Info(Tag, $"Connecting from {GetLocalEndpointText()} to {_hostEndpoint} " +
                              $"({_routeDescription}, clientToken=0x{_clientToken:X16})");
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"Failed to connect to {address}:{port}: {ex.Message}");
                throw;
            }
        }

        /// <inheritdoc />
        public void Disconnect()
        {
            if (!_isRunning) return;

            // Notify all peers before shutting down
            var disconnectPacket = new byte[] { PKT_DISCONNECT };
            if (IsHost)
            {
                foreach (var kvp in _peerEndpoints)
                {
                    SendRawTo(disconnectPacket, kvp.Value);
                }
            }
            else if (_hostEndpoint != null)
            {
                SendRawTo(disconnectPacket, _hostEndpoint);
            }

            Shutdown();
            Log.Info(Tag, "Disconnected");
        }

        /// <inheritdoc />
        public void DisconnectPeer(ulong peerId)
        {
            if (!_isRunning || peerId == 0)
                return;

            if (IsHost)
            {
                if (!_peerEndpoints.TryGetValue(peerId, out var endpoint))
                    return;

                SendRawTo(new byte[] { PKT_DISCONNECT }, endpoint);
                RemovePeer(peerId);
                SetStatus($"Peer {peerId} disconnected");
                OnPeerDisconnected?.Invoke(peerId);
                return;
            }

            if (peerId == 1)
                Disconnect();
        }

        // ═══════════════════════════════════════════════════════════════
        // ITransport — Send / Broadcast (thread-safe, lock-free)
        // ═══════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public void Send(ulong peerId, byte[] data, bool reliable)
        {
            if (data == null || data.Length == 0) return;

            IPEndPoint endpoint;
            if (IsHost)
            {
                // Lock-free lookup via ConcurrentDictionary
                if (!_peerEndpoints.TryGetValue(peerId, out endpoint))
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
                endpoint = _hostEndpoint;
                if (endpoint == null) return;
            }

            SendRawTo(BuildDataPacket(peerId, data, reliable), endpoint);
        }

        /// <inheritdoc />
        public void Broadcast(byte[] data, bool reliable, ulong? except = null)
        {
            if (data == null || data.Length == 0) return;

            if (IsHost)
            {
                // ConcurrentDictionary enumeration is snapshot-safe
                foreach (var kvp in _peerEndpoints)
                {
                    if (except.HasValue && kvp.Key == except.Value) continue;
                    var packet = BuildDataPacket(kvp.Key, data, reliable);
                    SendRawTo(packet, kvp.Value);
                }
            }
            else
            {
                // Client only knows the host
                if (except.HasValue && except.Value == 1) return;
                if (_hostEndpoint != null)
                {
                    var packet = BuildDataPacket(1, data, reliable);
                    SendRawTo(packet, _hostEndpoint);
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // ITransport — Update (main thread only)
        // ═══════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public void Update()
        {
            if (!_isRunning) return;

            // 1. Drain receive queue → process protocol on main thread
            while (_receiveQueue.TryDequeue(out var item))
            {
                ProcessRawPacket(item.Sender, item.Data);
            }

            // 2. Client: retry CONNECT if still waiting for ACK
            if (_waitingForConnection && !IsConnected)
            {
                long nowMs = _clock.ElapsedMilliseconds;
                if (nowMs - _connectSentMs > 1000) // Retry every 1 second
                {
                    SendConnectPacket(countAsConnectAttempt: true);
                }

                if (nowMs - _lastConnectLogMs >= 5000)
                {
                    _lastConnectLogMs = nowMs;
                    float elapsedSeconds = (nowMs - _connectStartedMs) / 1000f;
                    SetStatus($"No UDP response from {_hostEndpoint.Address}:{_hostEndpoint.Port}");
                    Log.Warning(Tag, $"Still waiting for CONNECT_ACK from {_hostEndpoint} " +
                                     $"after {elapsedSeconds:0}s ({_connectAttemptCount} attempts). " +
                                     $"Local socket is {GetLocalEndpointText()} via {_routeDescription}.");
                }
            }

            // 3. Keepalive pings + timeout checks
            ProcessKeepalive();
            ProcessEndpointRefresh();
            UpdateMetricsWindow();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
        }

        public NetworkQualitySnapshot GetNetworkQuality()
        {
            UpdateMetricsWindow();

            float rtt = 0f;
            float lastRtt = 0f;
            foreach (float value in _smoothedRttMs.Values)
                rtt = Math.Max(rtt, value);
            foreach (float value in _lastRttMs.Values)
                lastRtt = Math.Max(lastRtt, value);

            long nowMs = _clock.ElapsedMilliseconds;
            float secondsSinceReceive = _lastAnyReceiveMs > 0
                ? (float)Math.Max(0.0, (nowMs - _lastAnyReceiveMs) / 1000.0)
                : float.PositiveInfinity;

            return new NetworkQualitySnapshot
            {
                HasData = IsConnected || _connectedPeerIds.Count > 0 || _hasReceivedPacket,
                SmoothedRttMs = rtt,
                LastRttMs = lastRtt,
                SecondsSinceLastReceive = secondsSinceReceive,
                ConnectedPeerCount = _connectedPeerIds.Count,
                RecentPacketsSent = _lastWindowPacketsSent,
                RecentPacketsReceived = _lastWindowPacketsReceived,
                RecentOutgoingKbps = _lastOutgoingKbps,
                RecentIncomingKbps = _lastIncomingKbps,
                LocalEndpoint = GetLocalEndpointText(),
                RemoteEndpoint = _hostEndpoint?.ToString() ?? "",
                RouteDescription = _routeDescription ?? "",
                StatusMessage = StatusMessage ?? ""
            };
        }

        // ═══════════════════════════════════════════════════════════════
        // Protocol — Packet processing (main thread only)
        // ═══════════════════════════════════════════════════════════════

        private void ProcessRawPacket(IPEndPoint sender, byte[] data)
        {
            if (data == null || data.Length == 0) return;

            Interlocked.Increment(ref _windowPacketsReceived);
            Interlocked.Add(ref _windowBytesReceived, data.Length);
            _hasReceivedPacket = true;
            _lastAnyReceiveMs = _clock.ElapsedMilliseconds;

            byte pktType = data[0];

            if (IsHost && pktType == PKT_DATA_V2)
            {
                TryMapTokenizedPacket(sender, data);
            }

            // Update last-received timestamp for already-known peers
            string key = EndpointKey(sender);
            bool senderKnown = _endpointToPeer.TryGetValue(key, out ulong knownPeerId);
            if (senderKnown)
            {
                _lastReceivedMs[knownPeerId] = _clock.ElapsedMilliseconds;

                // Data resumed from a peer inside the reconnect grace window
                if (_lostPeers.Remove(knownPeerId))
                {
                    SetStatus($"Peer {knownPeerId} recovered");
                    Log.Info(Tag, $"Peer {knownPeerId} reconnected after dropout");
                }
            }

            switch (pktType)
            {
                case PKT_CONNECT:     HandleConnect(sender, data);    break;
                case PKT_CONNECT_ACK: HandleConnectAck(sender, data); break;
                case PKT_CONNECT_REJECT: HandleConnectReject(sender, data); break;
                case PKT_DISCONNECT:  HandleDisconnect(sender);       break;
                case PKT_DATA:        HandleData(sender, data);       break;
                case PKT_DATA_V2:     HandleDataV2(sender, data);     break;
                case PKT_PING:        HandlePing(sender, data);       break;
                case PKT_PONG:        if (senderKnown) HandlePong(knownPeerId, data); break;
                default:
                    Log.Debug(Tag, $"Unknown packet type 0x{pktType:X2} from {sender}");
                    break;
            }
        }

        /// <summary>
        /// Host-side: validate CONNECT, assign peer ID, send ACK, fire event.
        /// Handles duplicate CONNECTs from the same endpoint (re-ACK).
        /// </summary>
        private void HandleConnect(IPEndPoint sender, byte[] data)
        {
            if (!IsHost) return;

            // Validate:
            //   v1 legacy: [PKT_CONNECT][magic 4 bytes]
            //   v2:        [PKT_CONNECT][magic 4 bytes][version][client token 8 bytes][mod version UTF-8]
            if (data.Length < 1 + ConnectMagic.Length)
            {
                Log.Debug(Tag, $"CONNECT too short from {sender}");
                return;
            }
            for (int i = 0; i < ConnectMagic.Length; i++)
            {
                if (data[1 + i] != ConnectMagic[i])
                {
                    Log.Debug(Tag, $"Invalid CONNECT magic from {sender}");
                    return;
                }
            }

            bool isV2Connect = data.Length >= ConnectHeaderSize
                && data[1 + ConnectMagic.Length] == ConnectProtocolVersion;
            byte protocolVersion = isV2Connect ? ConnectProtocolVersion : LegacyProtocolVersion;

            ulong clientToken = 0;
            if (isV2Connect)
                clientToken = ReadUInt64LE(data, 1 + ConnectMagic.Length + 1);
            string clientModVersion = isV2Connect
                ? ReadUtf8Suffix(data, ConnectHeaderSize)
                : "";

            if (TryBuildVersionMismatch(_config.ModVersion, clientModVersion, out string rejectReason))
            {
                Log.Warning(Tag, $"Rejecting {sender}: {rejectReason}");
                SendConnectReject(rejectReason, sender);
                return;
            }

            string key = EndpointKey(sender);

            // Duplicate CONNECT from already-connected endpoint → re-ACK (lost ACK recovery)
            if (_endpointToPeer.TryGetValue(key, out ulong existingId))
            {
                Log.Debug(Tag, $"Re-ACK CONNECT for existing peer {existingId}");
                _lastReceivedMs[existingId] = _clock.ElapsedMilliseconds;
                RegisterPeerProtocol(existingId, protocolVersion);
                RegisterPeerToken(existingId, clientToken);
                SendConnectAck(existingId, clientToken, sender, protocolVersion);
                return;
            }

            // Same client token from a new endpoint: NAT/VPN route migrated.
            // Keep the original peer ID and update the send target instead of
            // creating a ghost peer whose reliable packets will never ACK.
            if (clientToken != 0 && _tokenToPeer.TryGetValue(clientToken, out ulong tokenPeerId))
            {
                UpdatePeerEndpoint(tokenPeerId, sender, "CONNECT token migration");
                RegisterPeerProtocol(tokenPeerId, ConnectProtocolVersion);
                _lastReceivedMs[tokenPeerId] = _clock.ElapsedMilliseconds;
                SendConnectAck(tokenPeerId, clientToken, sender, ConnectProtocolVersion);
                return;
            }

            // Enforce max connections
            if (_config.MaxConnections > 0 && _connectedPeerIds.Count >= _config.MaxConnections)
            {
                Log.Warning(Tag, $"Rejecting {sender}: max connections ({_config.MaxConnections}) reached");
                SendRawTo(new byte[] { PKT_DISCONNECT }, sender);
                return;
            }

            // Accept: assign next peer ID, register, ACK, fire event
            ulong newPeerId = _nextPeerId++;
            AddPeer(newPeerId, sender, clientToken, protocolVersion);
            SendConnectAck(newPeerId, clientToken, sender, protocolVersion);

            SetStatus($"Peer {newPeerId} connected");
            Log.Info(Tag, $"Peer {newPeerId} connected from {sender}" +
                          (clientToken != 0 ? $" (token=0x{clientToken:X16})" : " (legacy handshake)"));
            OnPeerConnected?.Invoke(newPeerId);
        }

        /// <summary>
        /// Client-side: receive assigned peer ID from host, complete handshake.
        /// </summary>
        private void HandleConnectAck(IPEndPoint sender, byte[] data)
        {
            if (IsHost) return;

            // v1 legacy: [PKT_CONNECT_ACK][peerId 8 bytes LE]
            // v2:        [PKT_CONNECT_ACK][peerId 8 bytes LE][client token 8 bytes LE][host mod version UTF-8]
            if (data.Length < 9)
            {
                Log.Debug(Tag, "CONNECT_ACK too short");
                return;
            }

            ulong assignedId = ReadUInt64LE(data, 1);
            byte protocolVersion = data.Length >= ConnectAckSize
                ? ConnectProtocolVersion
                : LegacyProtocolVersion;
            if (data.Length >= ConnectAckSize)
            {
                ulong ackToken = ReadUInt64LE(data, 9);
                if (ackToken != 0 && ackToken != _clientToken)
                {
                    Log.Debug(Tag, $"Ignoring CONNECT_ACK for token 0x{ackToken:X16}; " +
                                   $"local token is 0x{_clientToken:X16}");
                    return;
                }
            }

            string hostModVersion = protocolVersion >= ConnectProtocolVersion
                ? ReadUtf8Suffix(data, ConnectAckSize)
                : "";
            if (TryBuildVersionMismatch(hostModVersion, _config.ModVersion, out string rejectReason))
            {
                HandleConnectRejected(sender, rejectReason);
                return;
            }

            if (!_waitingForConnection && IsConnected)
            {
                RegisterPeerProtocol(1, protocolVersion);
                UpdatePeerEndpoint(1, sender, "CONNECT_ACK refresh");
                _lastReceivedMs[1] = _clock.ElapsedMilliseconds;
                return;
            }

            if (!_waitingForConnection) return;

            LocalPeerId = assignedId;
            IsConnected = true;
            _waitingForConnection = false;

            // Register host as peer 1
            AddPeer(1, sender, 0, protocolVersion);

            SetStatus($"Connected to host at {sender}");
            Log.Info(Tag, $"Connected to host at {sender}, assigned peer ID {assignedId}");
            OnPeerConnected?.Invoke(1);
        }

        private void HandleConnectReject(IPEndPoint sender, byte[] data)
        {
            if (IsHost) return;
            string reason = ReadUtf8Suffix(data, 1);
            HandleConnectRejected(sender, reason);
        }

        private void HandleConnectRejected(IPEndPoint sender, string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                reason = "Connection rejected by host";

            _waitingForConnection = false;
            IsConnected = false;
            SetStatus(reason);
            Log.Warning(Tag, $"Connection rejected by {sender}: {reason}");

            OnPeerDisconnected?.Invoke(1);
            Shutdown();
            SetStatus(reason);
        }

        /// <summary>
        /// Remote peer sent explicit DISCONNECT.
        /// </summary>
        private void HandleDisconnect(IPEndPoint sender)
        {
            string key = EndpointKey(sender);
            if (!_endpointToPeer.TryGetValue(key, out ulong peerId)) return;

            RemovePeer(peerId);
            SetStatus(peerId == 1 && !IsHost ? "Host disconnected" : $"Peer {peerId} disconnected");
            Log.Info(Tag, $"Peer {peerId} disconnected");
            OnPeerDisconnected?.Invoke(peerId);

            // If we're a client and the host disconnected, we're no longer connected
            if (!IsHost && peerId == 1)
                IsConnected = false;
        }

        /// <summary>
        /// Data packet from a known peer — strip framing and deliver payload.
        /// </summary>
        private void HandleData(IPEndPoint sender, byte[] data)
        {
            // [PKT_DATA][reliable flag byte][payload...]
            if (data.Length < 3) return; // Need type + flag + at least 1 payload byte

            string key = EndpointKey(sender);
            if (!_endpointToPeer.TryGetValue(key, out ulong peerId)) return;

            // data[1] = reliable flag — noted in header but no special handling (T10)
            var payload = new byte[data.Length - 2];
            Buffer.BlockCopy(data, 2, payload, 0, payload.Length);

            OnDataReceived?.Invoke(peerId, payload);
        }

        /// <summary>
        /// Tokenized data frame. The token lets the host preserve peer identity
        /// when a VPN/TUN/NAT path changes the sender's UDP endpoint.
        /// </summary>
        private void HandleDataV2(IPEndPoint sender, byte[] data)
        {
            // [PKT_DATA_V2][source token 8 bytes][reliable flag byte][payload...]
            if (data.Length < 11) return;

            string key = EndpointKey(sender);
            if (!_endpointToPeer.TryGetValue(key, out ulong peerId)) return;

            var payload = new byte[data.Length - 10];
            Buffer.BlockCopy(data, 10, payload, 0, payload.Length);

            OnDataReceived?.Invoke(peerId, payload);
        }

        /// <summary>
        /// Respond to PING with PONG immediately.
        /// </summary>
        private void HandlePing(IPEndPoint sender, byte[] data)
        {
            if (data != null && data.Length >= 5)
            {
                var pong = new byte[5];
                pong[0] = PKT_PONG;
                Buffer.BlockCopy(data, 1, pong, 1, 4);
                SendRawTo(pong, sender);
                return;
            }

            SendRawTo(new byte[] { PKT_PONG }, sender);
        }

        private void HandlePong(ulong peerId, byte[] data)
        {
            if (data == null || data.Length < 5)
                return;

            uint pingId = ReadUInt32LE(data, 1);
            if (!_pendingPingSentMs.TryGetValue(peerId, out var pending))
                return;
            if (!pending.TryGetValue(pingId, out long sentMs))
                return;

            pending.Remove(pingId);
            float rttMs = (float)Math.Max(0.0, _clock.ElapsedMilliseconds - sentMs);
            _lastRttMs[peerId] = rttMs;
            if (_smoothedRttMs.TryGetValue(peerId, out float old))
                _smoothedRttMs[peerId] = old + (rttMs - old) * 0.2f;
            else
                _smoothedRttMs[peerId] = rttMs;
        }

        // ═══════════════════════════════════════════════════════════════
        // Keepalive & timeout (main thread)
        // ═══════════════════════════════════════════════════════════════

        private void ProcessKeepalive()
        {
            if (_connectedPeerIds.Count == 0) return;

            long nowMs = _clock.ElapsedMilliseconds;
            long keepaliveMs = (long)(_config.KeepaliveInterval * 1000f);
            long timeoutMs = (long)(_config.TimeoutSeconds * 1000f);
            long graceMs = (long)(_config.ReconnectGraceSeconds * 1000f);

            // Send PING to all peers at the configured interval.
            // Lost peers stay in _peerEndpoints, so pings double as reconnect probes.
            if (nowMs - _lastKeepaliveMs >= keepaliveMs)
            {
                _lastKeepaliveMs = nowMs;
                foreach (var kvp in _peerEndpoints)
                {
                    SendRawTo(BuildPingPacket(kvp.Key, nowMs), kvp.Value);
                }

                // Client: also re-send CONNECT while the host is lost. A re-ACK
                // refreshes the host's view of us (and re-punches NAT) even if
                // the host briefly timed us out.
                if (!IsHost && _lostPeers.Contains(1) && _hostEndpoint != null)
                {
                    SendConnectPacket(countAsConnectAttempt: false);
                }
            }

            // Check for timed-out peers
            List<ulong> timedOut = null;
            foreach (var kvp in _lastReceivedMs)
            {
                long elapsed = nowMs - kvp.Value;
                if (elapsed <= timeoutMs) continue;

                // First timeout: enter the reconnect grace window instead of
                // dropping the peer. Data resuming clears this (ProcessRawPacket).
                if (elapsed <= timeoutMs + graceMs)
                {
                    if (_lostPeers.Add(kvp.Key))
                    {
                        SetStatus($"Peer {kvp.Key} has stopped responding");
                        Log.Warning(Tag, $"Peer {kvp.Key} silent for {_config.TimeoutSeconds}s — " +
                                         $"retrying for up to {_config.ReconnectGraceSeconds}s before disconnecting");
                    }
                    continue;
                }

                if (timedOut == null) timedOut = new List<ulong>();
                timedOut.Add(kvp.Key);
            }

            if (timedOut != null)
            {
                foreach (ulong peerId in timedOut)
                {
                    SetStatus(peerId == 1 && !IsHost ? "Host timed out" : $"Peer {peerId} timed out");
                    Log.Warning(Tag, $"Peer {peerId} timed out " +
                                     $"({_config.TimeoutSeconds}s + {_config.ReconnectGraceSeconds}s grace, no data)");
                    _lostPeers.Remove(peerId);
                    RemovePeer(peerId);

                    if (!IsHost && peerId == 1)
                        IsConnected = false;

                    OnPeerDisconnected?.Invoke(peerId);
                }
            }
        }

        private void ProcessEndpointRefresh()
        {
            if (IsHost || !IsConnected || _hostEndpoint == null)
                return;
            if (_config.EndpointRefreshInterval <= 0f)
                return;

            long nowMs = _clock.ElapsedMilliseconds;
            long intervalMs = (long)(_config.EndpointRefreshInterval * 1000f);
            if (nowMs - _lastEndpointRefreshMs < intervalMs)
                return;

            _lastEndpointRefreshMs = nowMs;
            SendConnectPacket(countAsConnectAttempt: false);
        }

        private byte[] BuildPingPacket(ulong peerId, long nowMs)
        {
            if (!_nextPingIds.TryGetValue(peerId, out uint pingId))
                pingId = 0;
            _nextPingIds[peerId] = pingId + 1;

            if (!_pendingPingSentMs.TryGetValue(peerId, out var pending))
            {
                pending = new Dictionary<uint, long>();
                _pendingPingSentMs[peerId] = pending;
            }
            pending[pingId] = nowMs;

            if (pending.Count > 16)
            {
                var oldest = new List<uint>();
                foreach (var kvp in pending)
                {
                    if (nowMs - kvp.Value > 30000)
                        oldest.Add(kvp.Key);
                }
                foreach (uint id in oldest)
                    pending.Remove(id);
            }

            var ping = new byte[5];
            ping[0] = PKT_PING;
            WriteUInt32LE(ping, 1, pingId);
            return ping;
        }

        // ═══════════════════════════════════════════════════════════════
        // Peer management (main thread only)
        // ═══════════════════════════════════════════════════════════════

        private void AddPeer(ulong peerId, IPEndPoint endpoint, ulong clientToken, byte protocolVersion)
        {
            string key = EndpointKey(endpoint);
            _peerEndpoints[peerId] = endpoint;  // ConcurrentDictionary — thread-safe write
            _endpointToPeer[key] = peerId;
            _connectedPeerIds.Add(peerId);
            _lastReceivedMs[peerId] = _clock.ElapsedMilliseconds;
            RegisterPeerProtocol(peerId, protocolVersion);
            RegisterPeerToken(peerId, clientToken);
        }

        private void RegisterPeerProtocol(ulong peerId, byte protocolVersion)
        {
            _peerProtocolVersions[peerId] = protocolVersion >= ConnectProtocolVersion
                ? ConnectProtocolVersion
                : LegacyProtocolVersion;
        }

        private void RegisterPeerToken(ulong peerId, ulong clientToken)
        {
            if (clientToken == 0) return;

            if (_peerTokens.TryGetValue(peerId, out ulong oldToken) && oldToken != clientToken)
                _tokenToPeer.Remove(oldToken);

            _peerTokens[peerId] = clientToken;
            _tokenToPeer[clientToken] = peerId;
        }

        private void UpdatePeerEndpoint(ulong peerId, IPEndPoint endpoint, string reason)
        {
            string newKey = EndpointKey(endpoint);
            if (_peerEndpoints.TryGetValue(peerId, out var oldEndpoint))
            {
                string oldKey = EndpointKey(oldEndpoint);
                if (oldKey == newKey) return;

                _endpointToPeer.Remove(oldKey);
                Log.Info(Tag, $"Peer {peerId} endpoint changed {oldEndpoint} -> {endpoint} ({reason})");
            }
            else
            {
                Log.Info(Tag, $"Peer {peerId} endpoint set to {endpoint} ({reason})");
            }

            _peerEndpoints[peerId] = endpoint;
            _endpointToPeer[newKey] = peerId;
            _connectedPeerIds.Add(peerId);
        }

        private void TryMapTokenizedPacket(IPEndPoint sender, byte[] data)
        {
            if (data == null || data.Length < 10) return;
            string key = EndpointKey(sender);
            if (_endpointToPeer.ContainsKey(key)) return;

            ulong sourceToken = ReadUInt64LE(data, 1);
            if (sourceToken == 0) return;
            if (!_tokenToPeer.TryGetValue(sourceToken, out ulong peerId)) return;

            UpdatePeerEndpoint(peerId, sender, "tokenized data migration");
            _lastReceivedMs[peerId] = _clock.ElapsedMilliseconds;
        }

        private void RemovePeer(ulong peerId)
        {
            if (_peerEndpoints.TryRemove(peerId, out var ep))
            {
                _endpointToPeer.Remove(EndpointKey(ep));
            }
            _connectedPeerIds.Remove(peerId);
            _lastReceivedMs.Remove(peerId);
            _peerProtocolVersions.TryRemove(peerId, out _);
            _lostPeers.Remove(peerId);
            _lastRttMs.Remove(peerId);
            _smoothedRttMs.Remove(peerId);
            _nextPingIds.Remove(peerId);
            _pendingPingSentMs.Remove(peerId);
            if (_peerTokens.TryGetValue(peerId, out ulong token))
            {
                _peerTokens.Remove(peerId);
                _tokenToPeer.Remove(token);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Socket I/O
        // ═══════════════════════════════════════════════════════════════

        private void SendConnectPacket(bool countAsConnectAttempt)
        {
            // [PKT_CONNECT][magic 4 bytes][protocol version][client token 8 bytes][mod version UTF-8]
            var modVersionBytes = Encoding.UTF8.GetBytes(_config.ModVersion ?? "");
            var packet = new byte[ConnectHeaderSize + modVersionBytes.Length];
            packet[0] = PKT_CONNECT;
            Buffer.BlockCopy(ConnectMagic, 0, packet, 1, ConnectMagic.Length);
            packet[1 + ConnectMagic.Length] = ConnectProtocolVersion;
            WriteUInt64LE(packet, 1 + ConnectMagic.Length + 1, _clientToken);
            if (modVersionBytes.Length > 0)
                Buffer.BlockCopy(modVersionBytes, 0, packet, ConnectHeaderSize, modVersionBytes.Length);

            SendRawTo(packet, _hostEndpoint);
            _connectSentMs = _clock.ElapsedMilliseconds;
            if (countAsConnectAttempt)
                _connectAttemptCount++;
        }

        private void SendConnectAck(ulong peerId, ulong clientToken, IPEndPoint target, byte protocolVersion)
        {
            if (protocolVersion < ConnectProtocolVersion)
            {
                // Legacy ACK: [PKT_CONNECT_ACK][peerId 8 bytes little-endian]
                var legacyPacket = new byte[9];
                legacyPacket[0] = PKT_CONNECT_ACK;
                WriteUInt64LE(legacyPacket, 1, peerId);
                SendRawTo(legacyPacket, target);
                return;
            }

            // [PKT_CONNECT_ACK][peerId 8 bytes little-endian][client token 8 bytes][host mod version UTF-8]
            var modVersionBytes = Encoding.UTF8.GetBytes(_config.ModVersion ?? "");
            var packet = new byte[ConnectAckSize + modVersionBytes.Length];
            packet[0] = PKT_CONNECT_ACK;
            WriteUInt64LE(packet, 1, peerId);
            WriteUInt64LE(packet, 9, clientToken);
            if (modVersionBytes.Length > 0)
                Buffer.BlockCopy(modVersionBytes, 0, packet, ConnectAckSize, modVersionBytes.Length);
            SendRawTo(packet, target);
        }

        private void SendConnectReject(string reason, IPEndPoint target)
        {
            var reasonBytes = Encoding.UTF8.GetBytes(reason ?? "Connection rejected by host");
            var packet = new byte[1 + reasonBytes.Length];
            packet[0] = PKT_CONNECT_REJECT;
            if (reasonBytes.Length > 0)
                Buffer.BlockCopy(reasonBytes, 0, packet, 1, reasonBytes.Length);
            SendRawTo(packet, target);
        }

        private byte[] BuildDataPacket(ulong peerId, byte[] data, bool reliable)
        {
            if (!_peerProtocolVersions.TryGetValue(peerId, out byte protocolVersion)
                || protocolVersion < ConnectProtocolVersion)
            {
                // Legacy frame: [PKT_DATA][reliable flag][payload]
                var legacyPacket = new byte[2 + data.Length];
                legacyPacket[0] = PKT_DATA;
                legacyPacket[1] = reliable ? (byte)1 : (byte)0;
                Buffer.BlockCopy(data, 0, legacyPacket, 2, data.Length);
                return legacyPacket;
            }

            // [PKT_DATA_V2][source token 8 bytes][reliable flag][payload]
            var packet = new byte[10 + data.Length];
            packet[0] = PKT_DATA_V2;
            WriteUInt64LE(packet, 1, IsHost ? 0UL : _clientToken);
            packet[9] = reliable ? (byte)1 : (byte)0;
            Buffer.BlockCopy(data, 0, packet, 10, data.Length);
            return packet;
        }

        private UdpClient CreateUdpClient(int port, IPAddress remoteAddress = null)
        {
            var bindAddressText = _config.LocalBindAddress;
            IPAddress bindAddress = IPAddress.Any;
            _routeDescription = port > 0 ? "listening on all IPv4 adapters" : "OS default route";
            string ignoredManualBind = null;
            bool manualBindAccepted = false;

            if (!string.IsNullOrWhiteSpace(bindAddressText))
            {
                if (!IPAddress.TryParse(bindAddressText.Trim(), out var configuredBindAddress))
                    throw new FormatException($"Invalid LocalBindAddress '{bindAddressText}'");

                if (configuredBindAddress.AddressFamily != AddressFamily.InterNetwork)
                    throw new NotSupportedException($"Only IPv4 bind addresses are supported: {configuredBindAddress}");

                if (ShouldUseManualBindAddress(configuredBindAddress, remoteAddress, out string ignoreReason))
                {
                    bindAddress = configuredBindAddress;
                    manualBindAccepted = true;
                    _routeDescription = IPAddress.Any.Equals(bindAddress)
                        ? "manual all-IPv4 bind"
                        : $"manual local bind {bindAddress}";
                }
                else
                {
                    ignoredManualBind = $"ignored LocalBindAddress {configuredBindAddress}: {ignoreReason}";
                    Log.Warning(Tag, $"Ignoring LocalBindAddress {configuredBindAddress} for remote " +
                                     $"{(remoteAddress != null ? remoteAddress.ToString() : "<host>")}: {ignoreReason}");
                }
            }

            if (!manualBindAccepted && IPAddress.Any.Equals(bindAddress) && _config.AutoVpnBind && remoteAddress != null
                && TryChooseLocalAddressForRemote(remoteAddress, out var selectedAddress, out var routeDescription))
            {
                bindAddress = selectedAddress;
                _routeDescription = routeDescription;
                Log.Info(Tag, $"Auto route selected local adapter IP {bindAddress} for remote {remoteAddress} ({routeDescription})");
            }

            if (!string.IsNullOrWhiteSpace(ignoredManualBind))
                _routeDescription = $"{_routeDescription}; {ignoredManualBind}";

            var client = new UdpClient(new IPEndPoint(bindAddress, port));
            try
            {
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
            }
            catch
            {
                // Some platforms reject this after bind. It is only a best-effort hardening knob.
            }
            return client;
        }

        private static bool ShouldUseManualBindAddress(
            IPAddress bindAddress,
            IPAddress remoteAddress,
            out string ignoreReason)
        {
            ignoreReason = "";

            if (bindAddress == null)
            {
                ignoreReason = "no bind address was provided";
                return false;
            }

            if (IPAddress.Any.Equals(bindAddress))
                return true;

            if (remoteAddress == null)
            {
                if (IPAddress.IsLoopback(bindAddress))
                    return true;

                if (FindLocalAddressCandidate(bindAddress) != null)
                    return true;

                ignoreReason = "address is not assigned to an active IPv4 adapter";
                return false;
            }

            bool bindLoopback = IPAddress.IsLoopback(bindAddress);
            bool remoteLoopback = IPAddress.IsLoopback(remoteAddress);

            if (remoteLoopback)
            {
                if (bindLoopback)
                    return true;

                ignoreReason = "loopback targets must use the loopback adapter";
                return false;
            }

            if (bindLoopback)
            {
                ignoreReason = "loopback bind cannot reach a non-loopback target";
                return false;
            }

            LocalAddressCandidate bindCandidate = FindLocalAddressCandidate(bindAddress);
            if (bindCandidate == null)
            {
                ignoreReason = "address is not assigned to an active IPv4 adapter";
                return false;
            }

            if (remoteAddress == null)
                return true;

            byte[] bindBytes = bindAddress.GetAddressBytes();
            byte[] remoteBytes = remoteAddress.GetAddressBytes();
            bool bindSpecialVpn = IsSpecialVpnAddress(bindBytes);
            bool remoteSpecialVpn = IsSpecialVpnAddress(remoteBytes);

            if (remoteSpecialVpn)
            {
                if (!bindSpecialVpn)
                {
                    ignoreReason = "remote is a VPN-style address but bind address is not";
                    return false;
                }

                if (bindBytes[0] != remoteBytes[0])
                {
                    ignoreReason = "remote and bind address are on different VPN address families";
                    return false;
                }

                return true;
            }

            if (bindSpecialVpn)
            {
                ignoreReason = "bind address is a VPN-style address but remote is not";
                return false;
            }

            if (IsRfc1918Address(remoteBytes))
            {
                if (!IsRfc1918Address(bindBytes))
                {
                    ignoreReason = "remote is a private LAN address but bind address is not";
                    return false;
                }

                if (bindCandidate.Mask != null && IsSameSubnet(bindAddress, remoteAddress, bindCandidate.Mask))
                    return true;

                ignoreReason = "bind address is not on the same LAN subnet as the remote";
                return false;
            }

            return true;
        }

        private static LocalAddressCandidate FindLocalAddressCandidate(IPAddress address)
        {
            if (address == null)
                return null;

            foreach (var candidate in GetLocalAddressCandidates())
            {
                if (candidate.Address.Equals(address))
                    return candidate;
            }

            return null;
        }

        private static bool TryChooseLocalAddressForRemote(
            IPAddress remoteAddress,
            out IPAddress address,
            out string description)
        {
            address = null;
            description = null;

            if (remoteAddress == null || remoteAddress.AddressFamily != AddressFamily.InterNetwork)
                return false;

            if (IPAddress.IsLoopback(remoteAddress))
            {
                address = IPAddress.Loopback;
                description = "loopback route";
                return true;
            }

            var candidates = GetLocalAddressCandidates();
            if (candidates.Count == 0)
                return false;

            byte[] remoteBytes = remoteAddress.GetAddressBytes();
            bool remoteSpecialVpn = IsSpecialVpnAddress(remoteBytes);
            bool remoteRfc1918 = IsRfc1918Address(remoteBytes);

            LocalAddressCandidate best = null;
            int bestScore = 0;

            foreach (var candidate in candidates)
            {
                byte[] localBytes = candidate.Address.GetAddressBytes();
                int score = 0;

                if (candidate.Mask != null && IsSameSubnet(candidate.Address, remoteAddress, candidate.Mask))
                {
                    score = 1000 + candidate.MaskBits;
                }

                if (remoteSpecialVpn && IsSpecialVpnAddress(localBytes))
                {
                    score = Math.Max(score, 650);
                    if (localBytes[0] == remoteBytes[0])
                        score += 75;
                }
                else if (remoteRfc1918 && candidate.VpnNamed && IsRfc1918Address(localBytes)
                    && localBytes[0] == remoteBytes[0])
                {
                    score = Math.Max(score, 450);
                }

                if (score <= 0)
                    continue;

                if (candidate.VpnNamed)
                    score += 20;
                if (candidate.AdapterName.IndexOf("virtual", StringComparison.OrdinalIgnoreCase) >= 0)
                    score += 5;

                if (score > bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }

            if (best == null)
                return false;

            address = best.Address;
            description = $"matched route on {best.AdapterName}";
            return true;
        }

        private static List<LocalAddressCandidate> GetLocalAddressCandidates()
        {
            var candidates = new List<LocalAddressCandidate>();

            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up)
                        continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    string name = ((nic.Name ?? "") + " " + (nic.Description ?? "")).Trim();
                    bool vpnNamed = IsVpnNamedAdapter(name);

                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        var candidate = unicast.Address;
                        if (candidate.AddressFamily != AddressFamily.InterNetwork)
                            continue;
                        if (IPAddress.IsLoopback(candidate))
                            continue;

                        var mask = unicast.IPv4Mask;
                        candidates.Add(new LocalAddressCandidate
                        {
                            Address = candidate,
                            Mask = mask,
                            MaskBits = CountMaskBits(mask),
                            AdapterName = string.IsNullOrWhiteSpace(name) ? nic.Id : name,
                            VpnNamed = vpnNamed
                        });
                    }
                }
            }
            catch
            {
                return candidates;
            }

            return candidates;
        }

        private static bool IsVpnNamedAdapter(string name)
        {
            return ContainsIgnoreCase(name, "radmin")
                || ContainsIgnoreCase(name, "hamachi")
                || ContainsIgnoreCase(name, "tailscale")
                || ContainsIgnoreCase(name, "zerotier")
                || ContainsIgnoreCase(name, "wireguard")
                || ContainsIgnoreCase(name, "openvpn")
                || ContainsIgnoreCase(name, "wintun")
                || ContainsIgnoreCase(name, "tap")
                || ContainsIgnoreCase(name, "vpn");
        }

        private static bool ContainsIgnoreCase(string haystack, string needle)
        {
            return haystack?.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsSpecialVpnAddress(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 2) return false;
            return bytes[0] == 25
                || bytes[0] == 26
                || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127);
        }

        private static bool IsPrivateAddress(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 2) return false;
            return IsRfc1918Address(bytes)
                || bytes[0] == 26;
        }

        private static bool IsRfc1918Address(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 2) return false;
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168);
        }

        private static bool IsSameSubnet(IPAddress local, IPAddress remote, IPAddress mask)
        {
            if (local == null || remote == null || mask == null)
                return false;

            byte[] localBytes = local.GetAddressBytes();
            byte[] remoteBytes = remote.GetAddressBytes();
            byte[] maskBytes = mask.GetAddressBytes();
            if (localBytes.Length != 4 || remoteBytes.Length != 4 || maskBytes.Length != 4)
                return false;

            for (int i = 0; i < 4; i++)
            {
                if ((localBytes[i] & maskBytes[i]) != (remoteBytes[i] & maskBytes[i]))
                    return false;
            }

            return true;
        }

        private static int CountMaskBits(IPAddress mask)
        {
            if (mask == null)
                return 0;

            int bits = 0;
            byte[] bytes = mask.GetAddressBytes();
            for (int i = 0; i < bytes.Length; i++)
            {
                byte value = bytes[i];
                for (int bit = 7; bit >= 0; bit--)
                {
                    if ((value & (1 << bit)) != 0)
                        bits++;
                }
            }
            return bits;
        }

        private static IPEndPoint ResolveRemoteEndPoint(string address, int port)
        {
            if (IPAddress.TryParse(address, out var ipAddress))
                return new IPEndPoint(ipAddress, port);

            var addresses = Dns.GetHostAddresses(address);
            foreach (var candidate in addresses)
            {
                if (candidate.AddressFamily == AddressFamily.InterNetwork)
                    return new IPEndPoint(candidate, port);
            }

            throw new SocketException((int)SocketError.HostNotFound);
        }

        /// <summary>
        /// Raw UDP send. Thread-safe — <see cref="UdpClient.Send(byte[], int, IPEndPoint)"/>
        /// supports concurrent calls. No locks on the send hot-path.
        /// Failures are logged at debug level and swallowed.
        /// </summary>
        private void SendRawTo(byte[] data, IPEndPoint target)
        {
            // Capture local reference: _udpClient may be nulled by Shutdown on another thread
            var client = _udpClient;
            if (client == null) return;
            if (data == null || target == null) return;
            if (_config.MaxPacketSize > 0 && data != null && data.Length > _config.MaxPacketSize)
            {
                Log.Warning(Tag, $"Sending {data.Length} byte UDP packet to {target}, " +
                                 $"above MaxPacketSize={_config.MaxPacketSize}; VPN/TUN links may drop fragments");
            }

            try
            {
                client.Send(data, data.Length, target);
                Interlocked.Increment(ref _windowPacketsSent);
                Interlocked.Add(ref _windowBytesSent, data.Length);
            }
            catch (ObjectDisposedException)
            {
                // Socket closed during shutdown — expected, no log needed
            }
            catch (SocketException ex)
            {
                Log.Debug(Tag, $"Send to {target} failed: {ex.SocketErrorCode}");
            }
        }

        private void UpdateMetricsWindow()
        {
            long nowMs = _clock.ElapsedMilliseconds;
            if (_metricsWindowStartedMs <= 0)
                _metricsWindowStartedMs = nowMs;

            long elapsedMs = nowMs - _metricsWindowStartedMs;
            if (elapsedMs < 1000)
                return;

            long sentPackets = Interlocked.Exchange(ref _windowPacketsSent, 0);
            long receivedPackets = Interlocked.Exchange(ref _windowPacketsReceived, 0);
            long sentBytes = Interlocked.Exchange(ref _windowBytesSent, 0);
            long receivedBytes = Interlocked.Exchange(ref _windowBytesReceived, 0);
            float seconds = Math.Max(0.001f, elapsedMs / 1000f);

            _lastWindowPacketsSent = sentPackets > int.MaxValue ? int.MaxValue : (int)sentPackets;
            _lastWindowPacketsReceived = receivedPackets > int.MaxValue ? int.MaxValue : (int)receivedPackets;
            _lastOutgoingKbps = (sentBytes * 8f) / seconds / 1000f;
            _lastIncomingKbps = (receivedBytes * 8f) / seconds / 1000f;
            _metricsWindowStartedMs = nowMs;
        }

        // ── Receive thread ───────────────────────────────────────────

        private void StartReceiveThread()
        {
            _receiveThread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = "DirectUdpTransport_Recv"
            };
            _receiveThread.Start();
        }

        /// <summary>
        /// Background receive loop. Enqueues raw (sender, data) tuples
        /// into <see cref="_receiveQueue"/> for main-thread processing.
        /// Exits cleanly when socket is closed or <see cref="_isRunning"/> is false.
        /// </summary>
        private void ReceiveLoop()
        {
            while (_isRunning)
            {
                try
                {
                    var client = _udpClient;
                    if (client == null || !_isRunning) break;

                    var sender = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = client.Receive(ref sender);

                    if (data != null && data.Length > 0)
                    {
                        _receiveQueue.Enqueue((sender, data));
                    }
                }
                catch (SocketException)
                {
                    break; // Socket closed — exit cleanly
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                        Log.Warning(Tag, $"Receive error: {ex.Message}");
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Shutdown
        // ═══════════════════════════════════════════════════════════════

        private void Shutdown()
        {
            _isRunning = false;
            IsConnected = false;
            _waitingForConnection = false;

            // Close socket — this unblocks the blocking Receive() call in ReceiveLoop
            try { _udpClient?.Close(); }
            catch { /* best-effort */ }
            _udpClient = null;

            // Wait briefly for receive thread to exit
            try { _receiveThread?.Join(500); }
            catch { /* best-effort */ }
            _receiveThread = null;

            // Clear all peer state
            _peerEndpoints.Clear();
            _endpointToPeer.Clear();
            _peerProtocolVersions.Clear();
            _peerTokens.Clear();
            _tokenToPeer.Clear();
            _connectedPeerIds.Clear();
            _lastReceivedMs.Clear();
            _lastRttMs.Clear();
            _smoothedRttMs.Clear();
            _nextPingIds.Clear();
            _pendingPingSentMs.Clear();
            _lostPeers.Clear();
            _hostEndpoint = null;
            _hasReceivedPacket = false;
            LocalPeerId = 0;

            // Drain any remaining queued packets
            while (_receiveQueue.TryDequeue(out _)) { }
        }

        // ═══════════════════════════════════════════════════════════════
        // Binary helpers (little-endian ulong for peer IDs)
        // ═══════════════════════════════════════════════════════════════

        private static string EndpointKey(IPEndPoint ep) => $"{ep.Address}:{ep.Port}";

        private static string ReadUtf8Suffix(byte[] data, int offset)
        {
            if (data == null || offset < 0 || data.Length <= offset)
                return "";
            try
            {
                return Encoding.UTF8.GetString(data, offset, data.Length - offset) ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static bool TryBuildVersionMismatch(string hostVersion, string clientVersion, out string reason)
        {
            reason = "";
            if (string.IsNullOrWhiteSpace(hostVersion) || string.IsNullOrWhiteSpace(clientVersion))
                return false;

            if (string.Equals(hostVersion ?? "", clientVersion ?? "", StringComparison.Ordinal))
                return false;

            string shownHostVersion = string.IsNullOrWhiteSpace(hostVersion) ? "unknown/old build" : hostVersion;
            string shownClientVersion = string.IsNullOrWhiteSpace(clientVersion) ? "unknown/old build" : clientVersion;
            reason = $"TCAMP version mismatch — host {shownHostVersion}, client {shownClientVersion}";
            return true;
        }

        private void SetStatus(string message)
        {
            message = message ?? string.Empty;
            if (message == StatusMessage)
                return;

            StatusMessage = message;
            OnStatusChanged?.Invoke(message);
        }

        private sealed class LocalAddressCandidate
        {
            public IPAddress Address;
            public IPAddress Mask;
            public int MaskBits;
            public string AdapterName;
            public bool VpnNamed;
        }

        private string GetLocalEndpointText()
        {
            try
            {
                return _udpClient?.Client?.LocalEndPoint?.ToString() ?? "<unbound>";
            }
            catch
            {
                return "<unavailable>";
            }
        }

        private static ulong GenerateClientToken()
        {
            var bytes = new byte[8];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            ulong token = ReadUInt64LE(bytes, 0);
            return token == 0 ? 1UL : token;
        }

        private static void WriteUInt32LE(byte[] buf, int offset, uint value)
        {
            buf[offset]     = (byte)value;
            buf[offset + 1] = (byte)(value >> 8);
            buf[offset + 2] = (byte)(value >> 16);
            buf[offset + 3] = (byte)(value >> 24);
        }

        private static uint ReadUInt32LE(byte[] buf, int offset)
        {
            return (uint)buf[offset]
                 | ((uint)buf[offset + 1] << 8)
                 | ((uint)buf[offset + 2] << 16)
                 | ((uint)buf[offset + 3] << 24);
        }

        private static void WriteUInt64LE(byte[] buf, int offset, ulong value)
        {
            buf[offset]     = (byte)(value);
            buf[offset + 1] = (byte)(value >> 8);
            buf[offset + 2] = (byte)(value >> 16);
            buf[offset + 3] = (byte)(value >> 24);
            buf[offset + 4] = (byte)(value >> 32);
            buf[offset + 5] = (byte)(value >> 40);
            buf[offset + 6] = (byte)(value >> 48);
            buf[offset + 7] = (byte)(value >> 56);
        }

        private static ulong ReadUInt64LE(byte[] buf, int offset)
        {
            return (ulong)buf[offset]
                 | ((ulong)buf[offset + 1] << 8)
                 | ((ulong)buf[offset + 2] << 16)
                 | ((ulong)buf[offset + 3] << 24)
                 | ((ulong)buf[offset + 4] << 32)
                 | ((ulong)buf[offset + 5] << 40)
                 | ((ulong)buf[offset + 6] << 48)
                 | ((ulong)buf[offset + 7] << 56);
        }
    }
}
