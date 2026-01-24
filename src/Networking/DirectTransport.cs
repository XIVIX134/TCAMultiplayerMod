using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using TCAMultiplayer;

namespace TCAMultiplayer.Networking
{
    /// <summary>
    /// Direct UDP transport for local/LAN testing.
    /// Allows running two game instances on the same PC without Steam.
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
        
        private float _lastPingTime;
        private float _lastPongTime;
        private bool _waitingForConnection;

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
            
            // Wrap data with header
            byte[] packet = new byte[data.Length + 1];
            packet[0] = MSG_DATA;
            Array.Copy(data, 0, packet, 1, data.Length);
            
            SendRaw(packet, _remoteEndPoint);
        }

        private void SendRaw(byte[] data, IPEndPoint endPoint)
        {
            try
            {
                _udpClient?.Send(data, data.Length, endPoint);
                
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
                        // Accept connection
                        _remoteEndPoint = sender;
                        _isConnected = true;
                        SendRaw(new byte[] { MSG_CONNECT_ACK }, sender);
                        
                        Plugin.Log.LogInfo($"DirectTransport: Client connected from {sender}");
                        OnPeerConnected?.Invoke(CLIENT_ID);
                        Plugin.Instance.State.ConnectionStatus = ConnectionStatus.Hosting;
                        Plugin.Instance.State.ConnectedPlayerCount = 1;
                    }
                    break;
                    
                case MSG_CONNECT_ACK:
                    if (!_isHost && _waitingForConnection)
                    {
                        _isConnected = true;
                        _waitingForConnection = false;
                        
                        Plugin.Log.LogInfo("DirectTransport: Connected to host");
                        OnPeerConnected?.Invoke(HOST_ID);
                        Plugin.Instance.State.ConnectionStatus = ConnectionStatus.Connected;
                    }
                    break;
                    
                case MSG_DISCONNECT:
                    if (_isConnected)
                    {
                        ulong peerId = _isHost ? CLIENT_ID : HOST_ID;
                        _isConnected = false;
                        _remoteEndPoint = null;
                        
                        Plugin.Log.LogInfo("DirectTransport: Peer disconnected");
                        OnPeerDisconnected?.Invoke(peerId);
                        Plugin.Instance.State.ConnectionStatus = _isHost ? ConnectionStatus.Hosting : ConnectionStatus.Disconnected;
                        Plugin.Instance.State.ConnectedPlayerCount = 0;
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
                    IPEndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = _udpClient?.Receive(ref sender);
                    
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
                    Plugin.Log.LogWarning($"DirectTransport: Receive error: {ex.Message}");
                }
            }
        }

        public void Shutdown()
        {
            _isRunning = false;
            _isConnected = false;
            
            try
            {
                _udpClient?.Close();
                _udpClient = null;
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
