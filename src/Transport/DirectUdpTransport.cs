using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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
    public sealed class DirectUdpTransport : ITransport
    {
        private const string Tag = "UDP";

        // ── Packet types (first byte of every packet) ────────────────
        private const byte PKT_DATA        = 0x00;
        private const byte PKT_CONNECT     = 0x01;
        private const byte PKT_CONNECT_ACK = 0x02;
        private const byte PKT_DISCONNECT  = 0x03;
        private const byte PKT_PING        = 0x04;
        private const byte PKT_PONG        = 0x05;

        /// <summary>Magic bytes to validate CONNECT packets: ASCII "TCAM".</summary>
        private static readonly byte[] ConnectMagic = { 0x54, 0x43, 0x41, 0x4D };

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
        private readonly HashSet<ulong> _connectedPeerIds = new HashSet<ulong>();
        private readonly Dictionary<ulong, long> _lastReceivedMs = new Dictionary<ulong, long>();
        // Peers past TimeoutSeconds but inside the reconnect grace window. They stay
        // registered (keepalives keep flowing) until the grace expires or data resumes.
        private readonly HashSet<ulong> _lostPeers = new HashSet<ulong>();
        private ulong _nextPeerId = 2; // Host is always 1; clients start at 2

        // ── Cross-thread receive queue ───────────────────────────────
        private readonly ConcurrentQueue<(IPEndPoint Sender, byte[] Data)> _receiveQueue
            = new ConcurrentQueue<(IPEndPoint, byte[])>();

        // ── Client-side handshake state ──────────────────────────────
        private volatile bool _waitingForConnection;
        private IPEndPoint _hostEndpoint;
        private long _connectSentMs;

        // ── Keepalive timing (Stopwatch for thread-safe, Unity-independent time) ─
        private long _lastKeepaliveMs;
        private readonly Stopwatch _clock = new Stopwatch();

        // ── Disposal ─────────────────────────────────────────────────
        private bool _disposed;

        public DirectUdpTransport(TransportConfig config)
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
            if (_isRunning)
            {
                Log.Warning(Tag, "StartHost called while already running");
                return;
            }

            try
            {
                _udpClient = new UdpClient(port);
                IsHost = true;
                IsConnected = true; // Host is always "connected" once listening
                LocalPeerId = 1;
                _isRunning = true;
                _lastKeepaliveMs = _clock.ElapsedMilliseconds;

                StartReceiveThread();
                Log.Info(Tag, $"Hosting on port {port}");
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
                _udpClient = new UdpClient(0); // OS-assigned local port
                _hostEndpoint = new IPEndPoint(IPAddress.Parse(address), port);
                IsHost = false;
                IsConnected = false;
                LocalPeerId = 0; // Assigned by host in CONNECT_ACK
                _isRunning = true;
                _waitingForConnection = true;
                _lastKeepaliveMs = _clock.ElapsedMilliseconds;

                StartReceiveThread();
                SendConnectPacket();

                Log.Info(Tag, $"Connecting to {address}:{port}");
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

            // Frame: [PKT_DATA][reliable flag][payload]
            var packet = new byte[2 + data.Length];
            packet[0] = PKT_DATA;
            packet[1] = reliable ? (byte)1 : (byte)0;
            Buffer.BlockCopy(data, 0, packet, 2, data.Length);

            SendRawTo(packet, endpoint);
        }

        /// <inheritdoc />
        public void Broadcast(byte[] data, bool reliable, ulong? except = null)
        {
            if (data == null || data.Length == 0) return;

            // Frame packet once, send to all
            var packet = new byte[2 + data.Length];
            packet[0] = PKT_DATA;
            packet[1] = reliable ? (byte)1 : (byte)0;
            Buffer.BlockCopy(data, 0, packet, 2, data.Length);

            if (IsHost)
            {
                // ConcurrentDictionary enumeration is snapshot-safe
                foreach (var kvp in _peerEndpoints)
                {
                    if (except.HasValue && kvp.Key == except.Value) continue;
                    SendRawTo(packet, kvp.Value);
                }
            }
            else
            {
                // Client only knows the host
                if (except.HasValue && except.Value == 1) return;
                if (_hostEndpoint != null)
                    SendRawTo(packet, _hostEndpoint);
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
                    SendConnectPacket();
                }
            }

            // 3. Keepalive pings + timeout checks
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
        // Protocol — Packet processing (main thread only)
        // ═══════════════════════════════════════════════════════════════

        private void ProcessRawPacket(IPEndPoint sender, byte[] data)
        {
            if (data == null || data.Length == 0) return;

            byte pktType = data[0];

            // Update last-received timestamp for already-known peers
            string key = EndpointKey(sender);
            if (_endpointToPeer.TryGetValue(key, out ulong knownPeerId))
            {
                _lastReceivedMs[knownPeerId] = _clock.ElapsedMilliseconds;

                // Data resumed from a peer inside the reconnect grace window
                if (_lostPeers.Remove(knownPeerId))
                {
                    Log.Info(Tag, $"Peer {knownPeerId} reconnected after dropout");
                }
            }

            switch (pktType)
            {
                case PKT_CONNECT:     HandleConnect(sender, data);    break;
                case PKT_CONNECT_ACK: HandleConnectAck(sender, data); break;
                case PKT_DISCONNECT:  HandleDisconnect(sender);       break;
                case PKT_DATA:        HandleData(sender, data);       break;
                case PKT_PING:        HandlePing(sender);             break;
                case PKT_PONG:        /* Timestamp already updated */ break;
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

            // Validate: [PKT_CONNECT][magic 4 bytes][optional name bytes...]
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

            string key = EndpointKey(sender);

            // Duplicate CONNECT from already-connected endpoint → re-ACK (lost ACK recovery)
            if (_endpointToPeer.TryGetValue(key, out ulong existingId))
            {
                Log.Debug(Tag, $"Re-ACK CONNECT for existing peer {existingId}");
                _lastReceivedMs[existingId] = _clock.ElapsedMilliseconds;
                SendConnectAck(existingId, sender);
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
            AddPeer(newPeerId, sender);
            SendConnectAck(newPeerId, sender);

            Log.Info(Tag, $"Peer {newPeerId} connected from {sender}");
            OnPeerConnected?.Invoke(newPeerId);
        }

        /// <summary>
        /// Client-side: receive assigned peer ID from host, complete handshake.
        /// </summary>
        private void HandleConnectAck(IPEndPoint sender, byte[] data)
        {
            if (IsHost || !_waitingForConnection) return;

            // [PKT_CONNECT_ACK][peerId 8 bytes LE]
            if (data.Length < 9)
            {
                Log.Debug(Tag, "CONNECT_ACK too short");
                return;
            }

            ulong assignedId = ReadUInt64LE(data, 1);

            LocalPeerId = assignedId;
            IsConnected = true;
            _waitingForConnection = false;

            // Register host as peer 1
            AddPeer(1, sender);

            Log.Info(Tag, $"Connected to host, assigned peer ID {assignedId}");
            OnPeerConnected?.Invoke(1);
        }

        /// <summary>
        /// Remote peer sent explicit DISCONNECT.
        /// </summary>
        private void HandleDisconnect(IPEndPoint sender)
        {
            string key = EndpointKey(sender);
            if (!_endpointToPeer.TryGetValue(key, out ulong peerId)) return;

            RemovePeer(peerId);
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
        /// Respond to PING with PONG immediately.
        /// </summary>
        private void HandlePing(IPEndPoint sender)
        {
            SendRawTo(new byte[] { PKT_PONG }, sender);
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
                var ping = new byte[] { PKT_PING };
                foreach (var kvp in _peerEndpoints)
                {
                    SendRawTo(ping, kvp.Value);
                }

                // Client: also re-send CONNECT while the host is lost. A re-ACK
                // refreshes the host's view of us (and re-punches NAT) even if
                // the host briefly timed us out.
                if (!IsHost && _lostPeers.Contains(1) && _hostEndpoint != null)
                {
                    SendConnectPacket();
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

        // ═══════════════════════════════════════════════════════════════
        // Peer management (main thread only)
        // ═══════════════════════════════════════════════════════════════

        private void AddPeer(ulong peerId, IPEndPoint endpoint)
        {
            string key = EndpointKey(endpoint);
            _peerEndpoints[peerId] = endpoint;  // ConcurrentDictionary — thread-safe write
            _endpointToPeer[key] = peerId;
            _connectedPeerIds.Add(peerId);
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
            _lostPeers.Remove(peerId);
        }

        // ═══════════════════════════════════════════════════════════════
        // Socket I/O
        // ═══════════════════════════════════════════════════════════════

        private void SendConnectPacket()
        {
            // [PKT_CONNECT][magic 4 bytes]
            var packet = new byte[1 + ConnectMagic.Length];
            packet[0] = PKT_CONNECT;
            Buffer.BlockCopy(ConnectMagic, 0, packet, 1, ConnectMagic.Length);

            SendRawTo(packet, _hostEndpoint);
            _connectSentMs = _clock.ElapsedMilliseconds;
        }

        private void SendConnectAck(ulong peerId, IPEndPoint target)
        {
            // [PKT_CONNECT_ACK][peerId 8 bytes little-endian]
            var packet = new byte[9];
            packet[0] = PKT_CONNECT_ACK;
            WriteUInt64LE(packet, 1, peerId);
            SendRawTo(packet, target);
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

            try
            {
                client.Send(data, data.Length, target);
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
            _connectedPeerIds.Clear();
            _lastReceivedMs.Clear();
            _lostPeers.Clear();
            _hostEndpoint = null;
            LocalPeerId = 0;

            // Drain any remaining queued packets
            while (_receiveQueue.TryDequeue(out _)) { }
        }

        // ═══════════════════════════════════════════════════════════════
        // Binary helpers (little-endian ulong for peer IDs)
        // ═══════════════════════════════════════════════════════════════

        private static string EndpointKey(IPEndPoint ep) => $"{ep.Address}:{ep.Port}";

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
