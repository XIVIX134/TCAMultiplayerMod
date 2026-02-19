using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using TCAMultiplayer;

namespace TCAMultiplayer.Networking
{
    /// <summary>
    /// Pending reliable packet awaiting ACK
    /// </summary>
    internal class PendingReliablePacket
    {
        public uint SequenceNumber;
        public byte[] Data;
        public float SendTime;
        public int RetryCount;
        public IPEndPoint Target;
    }

    /// <summary>
    /// Direct UDP transport for local/LAN testing.
    /// Allows running two game instances on the same PC without Steam.
    /// Implements reliable delivery with ACK/retransmit for important packets.
    /// </summary>
    public class DirectTransport : INetworkTransport
    {
        public string Name => "Direct UDP";
        public bool IsConnected => _isConnected;
        public bool IsHost => _isHost;

        public event Action<ulong> OnPeerConnected;
        public event Action<ulong> OnPeerDisconnected;
        public event Action<ulong, byte[]> OnDataReceived;

        private UdpClient _udpClient;
        private readonly object _udpLock = new object();
        private Thread _receiveThread;
        private bool _isRunning;
        private bool _isConnected;
        private bool _isHost;
        private IPEndPoint _remoteEndPoint;
        private int _localPort;

        private readonly Queue<(IPEndPoint, byte[])> _incomingQueue = new Queue<(IPEndPoint, byte[])>();
        private readonly object _queueLock = new object();

        // Simple peer ID - for direct connect, host is 1, client is 2
        private const ulong HOST_ID = 1;
        private const ulong CLIENT_ID = 2;

        // Connection handshake
        private const byte MSG_CONNECT = 0xC0;
        private const byte MSG_CONNECT_ACK = 0xC1;
        private const byte MSG_DISCONNECT = 0xD0;
        private const byte MSG_DATA = 0xDA;
        private const byte MSG_PING = 0xF0;
        private const byte MSG_PONG = 0xF1;
        // Reliable delivery
        private const byte MSG_RELIABLE_DATA = 0xDB;
        private const byte MSG_ACK = 0xA0;

        private float _lastPingTime;
        private float _lastPongTime;
        private bool _waitingForConnection;

        // Reliable delivery system
        private uint _nextSequenceNumber = 1;
        private readonly Dictionary<uint, PendingReliablePacket> _pendingAcks = new Dictionary<uint, PendingReliablePacket>();
        private readonly object _reliableLock = new object();
        private readonly HashSet<uint> _receivedSequences = new HashSet<uint>(); // Dedup incoming
        // Constants now in NetworkConfig

        public void StartHost(int port)
        {
            if (_isRunning) return;

            try
            {
                _localPort = port;
                _udpClient = new UdpClient(port);
                _isHost = true;
                _isRunning = true;
                _isConnected = false;

                StartReceiveThread();

                Plugin.Log.LogInfo($"DirectTransport: Hosting on port {port}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"DirectTransport: Failed to start host: {ex.Message}");
            }
        }

        public void Connect(string address, int port)
        {
            if (_isRunning) return;

            try
            {
                // Use random local port for client
                _udpClient = new UdpClient(0);
                _remoteEndPoint = new IPEndPoint(IPAddress.Parse(address), port);
                _isHost = false;
                _isRunning = true;
                _isConnected = false;
                _waitingForConnection = true;

                StartReceiveThread();

                // Send connection request
                SendRaw(new byte[] { MSG_CONNECT }, _remoteEndPoint);

                Plugin.Log.LogInfo($"DirectTransport: Connecting to {address}:{port}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"DirectTransport: Failed to connect: {ex.Message}");
            }
        }

        public void Disconnect()
        {
            if (!_isRunning) return;

            if (_isConnected && _remoteEndPoint != null)
            {
                try
                {
                    SendRaw(new byte[] { MSG_DISCONNECT }, _remoteEndPoint);
                }
                catch { }
            }

            _isConnected = false;
            Shutdown();

            Plugin.Log.LogInfo("DirectTransport: Disconnected");
        }

        public void DisconnectPeer(ulong peerId)
        {
            if (!_isRunning || !_isHost) return;

            // Send disconnect message to the peer
            if (_isConnected && _remoteEndPoint != null)
            {
                try
                {
                    SendRaw(new byte[] { MSG_DISCONNECT }, _remoteEndPoint);
                }
                catch { }
            }

            // Reset connection state but keep the host socket alive
            _isConnected = false;
            _remoteEndPoint = null;

            lock (_reliableLock)
            {
                _pendingAcks.Clear();
                _receivedSequences.Clear();
                _nextSequenceNumber = 1;
            }

            Plugin.Log.LogInfo($"DirectTransport: Kicked peer {peerId}, host still listening");
            OnPeerDisconnected?.Invoke(peerId);
        }

        public void Send(byte[] data, bool reliable = true)
        {
            if (!_isConnected || _remoteEndPoint == null)
            {
                if (LogHelper.IsEnabled(LogCategory.Transport) &&
                    LogHelper.ShouldLogInterval("DirectTransport.Send.NotConnected", LogHelper.DefaultIntervalSeconds))
                {
                    LogHelper.Info(LogCategory.Transport, "[DirectTransport] Send dropped: not connected");
                }
                return;
            }

            if (LogHelper.IsEnabled(LogCategory.Transport) &&
                LogHelper.ShouldSample("DirectTransport.Send", LogHelper.PacketSampleRate))
            {
                LogHelper.Info(LogCategory.Transport,
                    $"[DirectTransport] Send {data.Length} bytes reliable={reliable} to {_remoteEndPoint}");
            }

            if (reliable)
            {
                // Reliable: wrap with sequence number and queue for ACK
                SendReliable(data, _remoteEndPoint);
            }
            else
            {
                // Unreliable: wrap data with header and send immediately
                byte[] packet = new byte[data.Length + 1];
                packet[0] = MSG_DATA;
                Array.Copy(data, 0, packet, 1, data.Length);
                SendRaw(packet, _remoteEndPoint);
            }
        }

        private void SendReliable(byte[] data, IPEndPoint target)
        {
            uint seqNum;
            lock (_reliableLock)
            {
                seqNum = _nextSequenceNumber++;
            }

            // Packet format: [MSG_RELIABLE_DATA][4-byte seq][payload]
            byte[] packet = new byte[1 + 4 + data.Length];
            packet[0] = MSG_RELIABLE_DATA;
            packet[1] = (byte)(seqNum & 0xFF);
            packet[2] = (byte)((seqNum >> 8) & 0xFF);
            packet[3] = (byte)((seqNum >> 16) & 0xFF);
            packet[4] = (byte)((seqNum >> 24) & 0xFF);
            Array.Copy(data, 0, packet, 5, data.Length);

            var pending = new PendingReliablePacket
            {
                SequenceNumber = seqNum,
                Data = packet,
                SendTime = UnityEngine.Time.time,
                RetryCount = 0,
                Target = target
            };

            lock (_reliableLock)
            {
                _pendingAcks[seqNum] = pending;
            }

            SendRaw(packet, target);

            if (LogHelper.IsEnabled(LogCategory.Transport))
            {
                LogHelper.Info(LogCategory.Transport,
                    $"[DirectTransport] Sent reliable seq={seqNum} bytes={data.Length}");
            }
        }

        private void SendAck(uint sequenceNumber, IPEndPoint target)
        {
            // ACK format: [MSG_ACK][4-byte seq]
            byte[] ackPacket = new byte[5];
            ackPacket[0] = MSG_ACK;
            ackPacket[1] = (byte)(sequenceNumber & 0xFF);
            ackPacket[2] = (byte)((sequenceNumber >> 8) & 0xFF);
            ackPacket[3] = (byte)((sequenceNumber >> 16) & 0xFF);
            ackPacket[4] = (byte)((sequenceNumber >> 24) & 0xFF);

            SendRaw(ackPacket, target);
        }

        private void SendRaw(byte[] data, IPEndPoint endPoint)
        {
            try
            {
                lock (_udpLock)
                {
                    _udpClient?.Send(data, data.Length, endPoint);
                }

                if (LogHelper.IsEnabled(LogCategory.Transport) &&
                    LogHelper.ShouldSample("DirectTransport.SendRaw", LogHelper.PacketSampleRate))
                {
                    LogHelper.Info(LogCategory.Transport,
                        $"[DirectTransport] SendRaw {data.Length} bytes to {endPoint}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"DirectTransport: Send failed: {ex.Message}");
            }
        }

        public void Update()
        {
            if (!_isRunning) return;

            // Process queued messages on main thread
            lock (_queueLock)
            {
                while (_incomingQueue.Count > 0)
                {
                    var (sender, data) = _incomingQueue.Dequeue();
                    ProcessMessage(sender, data);
                }
            }

            // Process reliable packet retransmission
            ProcessReliableRetransmit();

            // Process keepalive
            ProcessKeepalive();
        }

        private void ProcessReliableRetransmit()
        {
            if (!_isConnected) return;

            float now = UnityEngine.Time.time;
            List<uint> toRemove = null;

            lock (_reliableLock)
            {
                foreach (var kvp in _pendingAcks)
                {
                    var pending = kvp.Value;
                    float elapsed = now - pending.SendTime;

                    if (elapsed >= NetworkConfig.RELIABLE_RETRY_TIMEOUT)
                    {
                        if (pending.RetryCount >= NetworkConfig.RELIABLE_MAX_RETRIES)
                        {
                            // Max retries exceeded - remove and log
                            Plugin.Log.LogWarning($"[DirectTransport] Reliable packet seq={pending.SequenceNumber} dropped after {NetworkConfig.RELIABLE_MAX_RETRIES} retries");
                            if (toRemove == null) toRemove = new List<uint>();
                            toRemove.Add(kvp.Key);
                        }
                        else
                        {
                            // Retransmit
                            pending.RetryCount++;
                            pending.SendTime = now;
                            SendRaw(pending.Data, pending.Target);

                            if (LogHelper.IsEnabled(LogCategory.Transport))
                            {
                                LogHelper.Info(LogCategory.Transport,
                                    $"[DirectTransport] Retransmit seq={pending.SequenceNumber} attempt={pending.RetryCount}");
                            }
                        }
                    }
                }

                if (toRemove != null)
                {
                    foreach (var seq in toRemove)
                    {
                        _pendingAcks.Remove(seq);
                    }
                }
            }
        }

        private void ProcessKeepalive()
        {
            if (!_isConnected || _remoteEndPoint == null) return;

            float now = UnityEngine.Time.time;

            // Send ping if needed
            if (now - _lastPingTime >= NetworkConfig.KEEPALIVE_PING_INTERVAL)
            {
                _lastPingTime = now;
                SendRaw(new byte[] { MSG_PING }, _remoteEndPoint);
            }

            // Check for timeout
            if (_lastPongTime > 0 && now - _lastPongTime > NetworkConfig.KEEPALIVE_TIMEOUT)
            {
                Plugin.Log.LogWarning("[DirectTransport] Keepalive timeout - disconnecting");
                ulong peerId = _isHost ? CLIENT_ID : HOST_ID;
                _isConnected = false;
                _remoteEndPoint = null;

                // Reset reliable state so next connection starts fresh
                lock (_reliableLock)
                {
                    _pendingAcks.Clear();
                    _receivedSequences.Clear();
                    _nextSequenceNumber = 1;
                }

                OnPeerDisconnected?.Invoke(peerId);
            }
        }

        private void ProcessMessage(IPEndPoint sender, byte[] data)
        {
            if (data.Length == 0) return;

            byte msgType = data[0];

            switch (msgType)
            {
                case MSG_CONNECT:
                    if (_isHost && !_isConnected)
                    {
                        // Accept connection — reset reliable state for the new peer
                        lock (_reliableLock)
                        {
                            _pendingAcks.Clear();
                            _receivedSequences.Clear();
                            _nextSequenceNumber = 1;
                        }

                        _remoteEndPoint = sender;
                        _isConnected = true;
                        _lastPongTime = UnityEngine.Time.time; // Initialize keepalive
                        _lastPingTime = UnityEngine.Time.time;
                        SendRaw(new byte[] { MSG_CONNECT_ACK }, sender);

                        Plugin.Log.LogInfo($"DirectTransport: Client connected from {sender}");
                        OnPeerConnected?.Invoke(CLIENT_ID);
                    }
                    break;

                case MSG_CONNECT_ACK:
                    if (!_isHost && _waitingForConnection)
                    {
                        _isConnected = true;
                        _waitingForConnection = false;
                        _lastPongTime = UnityEngine.Time.time; // Initialize keepalive
                        _lastPingTime = UnityEngine.Time.time;

                        Plugin.Log.LogInfo("DirectTransport: Connected to host");
                        OnPeerConnected?.Invoke(HOST_ID);
                    }
                    break;

                case MSG_DISCONNECT:
                    if (_isConnected)
                    {
                        ulong peerId = _isHost ? CLIENT_ID : HOST_ID;
                        _isConnected = false;
                        _remoteEndPoint = null;

                        // Reset reliable state so next connection starts fresh
                        lock (_reliableLock)
                        {
                            _pendingAcks.Clear();
                            _receivedSequences.Clear();
                            _nextSequenceNumber = 1;
                        }

                        Plugin.Log.LogInfo("DirectTransport: Peer disconnected");
                        OnPeerDisconnected?.Invoke(peerId);
                    }
                    break;

                case MSG_DATA:
                    if (_isConnected && data.Length > 1)
                    {
                        // Extract actual data (skip header)
                        byte[] payload = new byte[data.Length - 1];
                        Array.Copy(data, 1, payload, 0, payload.Length);

                        if (LogHelper.IsEnabled(LogCategory.Transport) &&
                            LogHelper.ShouldSample("DirectTransport.ReceiveData", LogHelper.PacketSampleRate))
                        {
                            LogHelper.Info(LogCategory.Transport,
                                $"[DirectTransport] Recv MSG_DATA {payload.Length} bytes from {sender}");
                        }

                        ulong peerId = _isHost ? CLIENT_ID : HOST_ID;
                        OnDataReceived?.Invoke(peerId, payload);
                    }
                    break;

                case MSG_RELIABLE_DATA:
                    if (data.Length > 5)
                    {
                        // Extract sequence number
                        uint seqNum = (uint)(data[1] | (data[2] << 8) | (data[3] << 16) | (data[4] << 24));

                        // Send ACK immediately
                        SendAck(seqNum, sender);

                        // Check for duplicate
                        bool isDuplicate;
                        lock (_reliableLock)
                        {
                            isDuplicate = _receivedSequences.Contains(seqNum);
                            if (!isDuplicate)
                            {
                                _receivedSequences.Add(seqNum);

                                // Prevent memory bloat - clear old sequences periodically
                                if (_receivedSequences.Count > NetworkConfig.MAX_RECEIVED_SEQUENCES)
                                {
                                    _receivedSequences.Clear();
                                }
                            }
                        }

                        if (!isDuplicate)
                        {
                            // Extract payload (skip header + seq)
                            byte[] payload = new byte[data.Length - 5];
                            Array.Copy(data, 5, payload, 0, payload.Length);

                            if (LogHelper.IsEnabled(LogCategory.Transport))
                            {
                                LogHelper.Info(LogCategory.Transport,
                                    $"[DirectTransport] Recv reliable seq={seqNum} bytes={payload.Length}");
                            }

                            ulong peerId = _isHost ? CLIENT_ID : HOST_ID;
                            OnDataReceived?.Invoke(peerId, payload);
                        }
                        else if (LogHelper.IsEnabled(LogCategory.Transport))
                        {
                            LogHelper.Info(LogCategory.Transport,
                                $"[DirectTransport] Duplicate reliable seq={seqNum} ignored");
                        }
                    }
                    break;

                case MSG_ACK:
                    if (data.Length >= 5)
                    {
                        uint seqNum = (uint)(data[1] | (data[2] << 8) | (data[3] << 16) | (data[4] << 24));

                        lock (_reliableLock)
                        {
                            if (_pendingAcks.Remove(seqNum))
                            {
                                if (LogHelper.IsEnabled(LogCategory.Transport))
                                {
                                    LogHelper.Info(LogCategory.Transport,
                                        $"[DirectTransport] ACK received seq={seqNum}");
                                }
                            }
                        }
                    }
                    break;

                case MSG_PING:
                    SendRaw(new byte[] { MSG_PONG }, sender);
                    break;

                case MSG_PONG:
                    _lastPongTime = UnityEngine.Time.time;
                    break;
            }
        }

        private void StartReceiveThread()
        {
            _receiveThread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = "DirectTransport_Receive"
            };
            _receiveThread.Start();
        }

        private void ReceiveLoop()
        {
            while (_isRunning)
            {
                try
                {
                    // Store local reference to avoid race condition during shutdown
                    UdpClient client;
                    lock (_udpLock)
                    {
                        client = _udpClient;
                    }

                    if (client == null || !_isRunning)
                        break;

                    IPEndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = client.Receive(ref sender);

                    if (data != null && data.Length > 0)
                    {
                        lock (_queueLock)
                        {
                            _incomingQueue.Enqueue((sender, data));
                        }
                    }
                }
                catch (SocketException)
                {
                    // Socket closed, exit thread
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_isRunning) // Only log if we're still supposed to be running
                    {
                        Plugin.Log.LogWarning($"DirectTransport: Receive error: {ex.Message}");
                    }
                }
            }
        }

        public void Shutdown()
        {
            _isRunning = false;
            _isConnected = false;

            // Clear reliable packet state
            lock (_reliableLock)
            {
                _pendingAcks.Clear();
                _receivedSequences.Clear();
                _nextSequenceNumber = 1;
            }

            try
            {
                lock (_udpLock)
                {
                    _udpClient?.Close();
                    _udpClient = null;
                }
            }
            catch { }

            try
            {
                _receiveThread?.Join(100);
            }
            catch { }

            lock (_queueLock)
            {
                _incomingQueue.Clear();
            }
        }
    }
}
