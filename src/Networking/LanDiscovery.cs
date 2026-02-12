using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace TCAMultiplayer.Networking
{
    /// <summary>
    /// Information about a discovered LAN game
    /// </summary>
    public class DiscoveredGame
    {
        public string HostName;
        public string IPAddress;
        public int Port;
        public int PlayerCount;
        public int MaxPlayers;
        public string MapName;
        public float LastSeen;
        
        public bool IsStale => Time.unscaledTime - LastSeen > 5f;
    }
    
    /// <summary>
    /// LAN discovery system using UDP broadcast
    /// Hosts broadcast their presence, clients listen for broadcasts
    /// </summary>
    public class LanDiscovery : IDisposable
    {
        // Singleton
        public static LanDiscovery Instance { get; private set; }
        
        // Discovery settings
        public const int DISCOVERY_PORT = 7776;
        public const float BROADCAST_INTERVAL = 1.0f;
        public const float CLEANUP_INTERVAL = 5.0f;
        
        // Protocol magic bytes to identify our broadcasts
        private static readonly byte[] MAGIC = Encoding.ASCII.GetBytes("TCAM");
        private const byte PROTOCOL_VERSION = 1;
        
        // State
        private UdpClient _udpClient;
        private Thread _listenerThread;
        private bool _isRunning;
        private bool _isBroadcasting;
        private float _lastBroadcast;
        private float _lastCleanup;
        
        // Host info (when broadcasting)
        private string _hostName = "TCA Game";
        private int _gamePort = NetworkConfig.DEFAULT_PORT;
        private int _playerCount = 1;
        private int _maxPlayers = 8;
        private string _mapName = "ActionIsland";
        
        // Discovered games
        private Dictionary<string, DiscoveredGame> _discoveredGames = new Dictionary<string, DiscoveredGame>();
        public IReadOnlyDictionary<string, DiscoveredGame> DiscoveredGames => _discoveredGames;
        
        // Events
        public event Action<DiscoveredGame> OnGameDiscovered;
        public event Action<string> OnGameLost;
        
        public LanDiscovery()
        {
            Instance = this;
        }
        
        /// <summary>
        /// Start broadcasting (host mode)
        /// </summary>
        public void StartBroadcasting(string hostName, int gamePort, string mapName, int playerCount = 1, int maxPlayers = 8)
        {
            _hostName = hostName;
            _gamePort = gamePort;
            _mapName = mapName;
            _playerCount = playerCount;
            _maxPlayers = maxPlayers;
            _isBroadcasting = true;
            
            StartListener();
            
            Plugin.Log?.LogInfo($"[LanDiscovery] Started broadcasting: {hostName} on port {gamePort}");
        }
        
        /// <summary>
        /// Update host info while broadcasting
        /// </summary>
        public void UpdateHostInfo(int playerCount, string mapName = null)
        {
            _playerCount = playerCount;
            if (!string.IsNullOrEmpty(mapName))
            {
                _mapName = mapName;
            }
        }
        
        /// <summary>
        /// Stop broadcasting
        /// </summary>
        public void StopBroadcasting()
        {
            _isBroadcasting = false;
            Plugin.Log?.LogInfo("[LanDiscovery] Stopped broadcasting");
        }
        
        /// <summary>
        /// Start listening for broadcasts (client mode)
        /// </summary>
        public void StartListening()
        {
            _discoveredGames.Clear();
            StartListener();
            Plugin.Log?.LogInfo("[LanDiscovery] Started listening for LAN games");
        }
        
        /// <summary>
        /// Stop listening
        /// </summary>
        public void StopListening()
        {
            StopListener();
            Plugin.Log?.LogInfo("[LanDiscovery] Stopped listening");
        }
        
        /// <summary>
        /// Start the UDP listener thread
        /// </summary>
        private void StartListener()
        {
            if (_isRunning) return;
            
            try
            {
                _udpClient = new UdpClient();
                _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, DISCOVERY_PORT));
                _udpClient.EnableBroadcast = true;
                
                _isRunning = true;
                _listenerThread = new Thread(ListenerLoop)
                {
                    IsBackground = true,
                    Name = "LanDiscovery"
                };
                _listenerThread.Start();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[LanDiscovery] Failed to start: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Stop the UDP listener
        /// </summary>
        private void StopListener()
        {
            _isRunning = false;
            _isBroadcasting = false;
            
            try
            {
                _udpClient?.Close();
                _udpClient = null;
            }
            catch { }
            
            _listenerThread = null;
        }
        
        /// <summary>
        /// Listener thread loop
        /// </summary>
        private void ListenerLoop()
        {
            while (_isRunning)
            {
                try
                {
                    if (_udpClient == null) break;
                    
                    // Check for incoming data
                    if (_udpClient.Client.Available > 0)
                    {
                        IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                        byte[] data = _udpClient.Receive(ref remoteEP);
                        
                        if (data != null && data.Length > 0)
                        {
                            ProcessReceivedData(data, remoteEP);
                        }
                    }
                    
                    Thread.Sleep(50);
                }
                catch (SocketException)
                {
                    // Socket closed, exit
                    break;
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"[LanDiscovery] Listener error: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Process received broadcast data
        /// </summary>
        private void ProcessReceivedData(byte[] data, IPEndPoint sender)
        {
            try
            {
                // Check minimum size and magic bytes
                if (data.Length < MAGIC.Length + 1) return;
                
                for (int i = 0; i < MAGIC.Length; i++)
                {
                    if (data[i] != MAGIC[i]) return;
                }
                
                // Check protocol version
                if (data[MAGIC.Length] != PROTOCOL_VERSION) return;
                
                // Parse the broadcast
                int offset = MAGIC.Length + 1;
                
                // Read host name (length-prefixed)
                int hostNameLen = data[offset++];
                string hostName = Encoding.UTF8.GetString(data, offset, hostNameLen);
                offset += hostNameLen;
                
                // Read game port
                int gamePort = BitConverter.ToUInt16(data, offset);
                offset += 2;
                
                // Read player count
                int playerCount = data[offset++];
                int maxPlayers = data[offset++];
                
                // Read map name (length-prefixed)
                int mapNameLen = data[offset++];
                string mapName = Encoding.UTF8.GetString(data, offset, mapNameLen);
                
                // Create/update discovered game
                string key = $"{sender.Address}:{gamePort}";
                
                lock (_discoveredGames)
                {
                    bool isNew = !_discoveredGames.ContainsKey(key);
                    
                    var game = new DiscoveredGame
                    {
                        HostName = hostName,
                        IPAddress = sender.Address.ToString(),
                        Port = gamePort,
                        PlayerCount = playerCount,
                        MaxPlayers = maxPlayers,
                        MapName = mapName,
                        LastSeen = Time.unscaledTime
                    };
                    
                    _discoveredGames[key] = game;
                    
                    if (isNew)
                    {
                        // Queue event for main thread
                        _pendingDiscovered.Enqueue(game);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[LanDiscovery] Parse error: {ex.Message}");
            }
        }
        
        // Queue for main thread events
        private Queue<DiscoveredGame> _pendingDiscovered = new Queue<DiscoveredGame>();
        private Queue<string> _pendingLost = new Queue<string>();
        
        /// <summary>
        /// Update (call from main thread)
        /// </summary>
        public void Update()
        {
            // Broadcast if hosting
            if (_isBroadcasting && Time.unscaledTime - _lastBroadcast > BROADCAST_INTERVAL)
            {
                _lastBroadcast = Time.unscaledTime;
                SendBroadcast();
            }
            
            // Process pending events
            while (_pendingDiscovered.Count > 0)
            {
                var game = _pendingDiscovered.Dequeue();
                OnGameDiscovered?.Invoke(game);
            }
            
            while (_pendingLost.Count > 0)
            {
                var key = _pendingLost.Dequeue();
                OnGameLost?.Invoke(key);
            }
            
            // Cleanup stale games
            if (Time.unscaledTime - _lastCleanup > CLEANUP_INTERVAL)
            {
                _lastCleanup = Time.unscaledTime;
                CleanupStaleGames();
            }
        }
        
        /// <summary>
        /// Send broadcast packet
        /// </summary>
        private void SendBroadcast()
        {
            if (_udpClient == null) return;
            
            try
            {
                byte[] hostNameBytes = Encoding.UTF8.GetBytes(_hostName);
                byte[] mapNameBytes = Encoding.UTF8.GetBytes(_mapName);
                
                // Build packet
                int size = MAGIC.Length + 1 + 1 + hostNameBytes.Length + 2 + 2 + 1 + mapNameBytes.Length;
                byte[] packet = new byte[size];
                int offset = 0;
                
                // Magic bytes
                Array.Copy(MAGIC, 0, packet, offset, MAGIC.Length);
                offset += MAGIC.Length;
                
                // Protocol version
                packet[offset++] = PROTOCOL_VERSION;
                
                // Host name (length-prefixed)
                packet[offset++] = (byte)hostNameBytes.Length;
                Array.Copy(hostNameBytes, 0, packet, offset, hostNameBytes.Length);
                offset += hostNameBytes.Length;
                
                // Game port
                byte[] portBytes = BitConverter.GetBytes((ushort)_gamePort);
                Array.Copy(portBytes, 0, packet, offset, 2);
                offset += 2;
                
                // Player count
                packet[offset++] = (byte)_playerCount;
                packet[offset++] = (byte)_maxPlayers;
                
                // Map name (length-prefixed)
                packet[offset++] = (byte)mapNameBytes.Length;
                Array.Copy(mapNameBytes, 0, packet, offset, mapNameBytes.Length);
                
                // Send broadcast
                IPEndPoint broadcastEP = new IPEndPoint(IPAddress.Broadcast, DISCOVERY_PORT);
                _udpClient.Send(packet, packet.Length, broadcastEP);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[LanDiscovery] Broadcast error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Clean up games that haven't been seen recently
        /// </summary>
        private void CleanupStaleGames()
        {
            lock (_discoveredGames)
            {
                var keysToRemove = new List<string>();
                
                foreach (var kvp in _discoveredGames)
                {
                    if (kvp.Value.IsStale)
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }
                
                foreach (var key in keysToRemove)
                {
                    _discoveredGames.Remove(key);
                    _pendingLost.Enqueue(key);
                }
            }
        }
        
        /// <summary>
        /// Get list of discovered games
        /// </summary>
        public List<DiscoveredGame> GetDiscoveredGames()
        {
            lock (_discoveredGames)
            {
                return new List<DiscoveredGame>(_discoveredGames.Values);
            }
        }
        
        /// <summary>
        /// Clear discovered games list
        /// </summary>
        public void ClearDiscoveredGames()
        {
            lock (_discoveredGames)
            {
                _discoveredGames.Clear();
            }
        }
        
        /// <summary>
        /// Dispose resources
        /// </summary>
        public void Dispose()
        {
            StopListener();
        }
    }
}
