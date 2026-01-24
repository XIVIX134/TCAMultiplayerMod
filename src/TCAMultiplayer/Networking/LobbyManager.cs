using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TCAMultiplayer.Networking
{
    /// <summary>
    /// Manages multiplayer lobby state, player list, ready status, and game start synchronization
    /// </summary>
    public class LobbyManager
    {
        // Singleton
        public static LobbyManager Instance { get; private set; }
        
        // Lobby state
        public bool IsInLobby { get; private set; }
        public bool IsHost { get; private set; }
        public string HostName { get; private set; } = "Host";
        public string MapName { get; private set; } = "ActionIsland";
        public LobbySpawnType SpawnType { get; private set; } = LobbySpawnType.Runway;
        public bool GameStarted { get; private set; }
        public bool GameLoading { get; private set; }
        
        // Players
        private Dictionary<ulong, LobbyPlayerInfo> _players = new Dictionary<ulong, LobbyPlayerInfo>();
        public IReadOnlyDictionary<ulong, LobbyPlayerInfo> Players => _players;
        
        // Local player info
        public ulong LocalPeerId { get; private set; }
        public string LocalPlayerName { get; private set; } = "Player";
        public string LocalSelectedAirfield { get; private set; } = "";
        public string LocalSelectedAircraft { get; private set; } = "AV8B";
        public bool LocalIsReady { get; private set; }
        public bool LocalIsLoaded { get; private set; }
        
        // Events
        public event Action OnLobbyStateChanged;
        public event Action<ulong, string> OnPlayerJoined;
        public event Action<ulong> OnPlayerLeft;
        public event Action<ulong, bool> OnPlayerReadyChanged;
        public event Action OnAllPlayersReady;
        public event Action OnGameStarting;
        public event Action OnAllPlayersLoaded;
        public event Action OnSpawnPlayers;
        
        // Random name generator
        private static readonly string[] _adjectives = { "Red", "Blue", "Swift", "Ace", "Wild", "Iron", "Shadow", "Storm", "Thunder", "Ghost" };
        private static readonly string[] _nouns = { "Falcon", "Eagle", "Hawk", "Viper", "Wolf", "Fox", "Pilot", "Wing", "Jet", "Arrow" };
        private static System.Random _random = new System.Random();
        
        // State broadcast timer (host only)
        private float _lastStateBroadcast = 0f;
        private const float STATE_BROADCAST_INTERVAL = 1.0f;
        
        public LobbyManager()
        {
            Instance = this;
            LocalPlayerName = GenerateRandomName();
        }
        
        /// <summary>
        /// Generate a random player name
        /// </summary>
        public static string GenerateRandomName()
        {
            string adj = _adjectives[_random.Next(_adjectives.Length)];
            string noun = _nouns[_random.Next(_nouns.Length)];
            int num = _random.Next(1, 100);
            return $"{adj}{noun}{num}";
        }
        
        /// <summary>
        /// Create a new lobby as host
        /// </summary>
        public void CreateLobby(ulong hostPeerId, string hostName = null)
        {
            IsInLobby = true;
            IsHost = true;
            LocalPeerId = hostPeerId;
            HostName = hostName ?? LocalPlayerName;
            GameStarted = false;
            GameLoading = false;
            
            _players.Clear();
            
            // Add host as first player
            var hostPlayer = new LobbyPlayerInfo
            {
                PeerId = hostPeerId,
                PlayerName = HostName,
                SelectedAirfield = LocalSelectedAirfield,
                SelectedAircraft = LocalSelectedAircraft,
                IsReady = false,
                IsLoaded = false,
                IsHost = true
            };
            _players[hostPeerId] = hostPlayer;
            
            Plugin.Log?.LogInfo($"[LobbyManager] Created lobby as host: {HostName}");
            OnLobbyStateChanged?.Invoke();
        }
        
        /// <summary>
        /// Join an existing lobby as client
        /// </summary>
        public void JoinLobby(ulong localPeerId)
        {
            IsInLobby = true;
            IsHost = false;
            LocalPeerId = localPeerId;
            GameStarted = false;
            GameLoading = false;
            
            _players.Clear();
            
            Plugin.Log?.LogInfo($"[LobbyManager] Joined lobby as client: {LocalPlayerName}");
            OnLobbyStateChanged?.Invoke();
        }
        
        /// <summary>
        /// Leave the current lobby
        /// </summary>
        public void LeaveLobby()
        {
            IsInLobby = false;
            IsHost = false;
            GameStarted = false;
            GameLoading = false;
            _players.Clear();
            
            Plugin.Log?.LogInfo("[LobbyManager] Left lobby");
            OnLobbyStateChanged?.Invoke();
        }
        
        /// <summary>
        /// Update from received lobby state (client only)
        /// </summary>
        public void UpdateFromLobbyState(LobbyStatePacket packet)
        {
            if (IsHost) return; // Host doesn't receive state updates
            
            HostName = packet.HostName;
            MapName = packet.MapName;
            SpawnType = packet.SpawnType;
            GameStarted = packet.GameStarted;
            GameLoading = packet.GameLoading;
            
            // Update player list
            var newPlayers = new Dictionary<ulong, LobbyPlayerInfo>();
            if (packet.Players != null)
            {
                foreach (var player in packet.Players)
                {
                    newPlayers[player.PeerId] = player;
                }
            }
            
            // Check for new/removed players
            foreach (var kvp in newPlayers)
            {
                if (!_players.ContainsKey(kvp.Key))
                {
                    OnPlayerJoined?.Invoke(kvp.Key, kvp.Value.PlayerName);
                }
            }
            foreach (var kvp in _players)
            {
                if (!newPlayers.ContainsKey(kvp.Key))
                {
                    OnPlayerLeft?.Invoke(kvp.Key);
                }
            }
            
            _players = newPlayers;
            OnLobbyStateChanged?.Invoke();
        }
        
        /// <summary>
        /// Handle player joined (host only)
        /// </summary>
        public void HandlePlayerJoined(ulong peerId, string playerName)
        {
            if (!IsHost) return;
            
            if (_players.ContainsKey(peerId))
            {
                Plugin.Log?.LogWarning($"[LobbyManager] Player {peerId} already in lobby, updating info");
                _players[peerId].PlayerName = playerName;
                OnLobbyStateChanged?.Invoke();
                return;
            }
            
            var player = new LobbyPlayerInfo
            {
                PeerId = peerId,
                PlayerName = playerName,
                SelectedAirfield = "",
                SelectedAircraft = "AV8B",
                IsReady = false,
                IsLoaded = false,
                IsHost = false
            };
            _players[peerId] = player;
            
            Plugin.Log?.LogInfo($"[LobbyManager] Player joined: {playerName} ({peerId})");
            OnPlayerJoined?.Invoke(peerId, playerName);
            OnLobbyStateChanged?.Invoke();
        }
        
        /// <summary>
        /// Handle player left
        /// </summary>
        public void HandlePlayerLeft(ulong peerId)
        {
            if (_players.ContainsKey(peerId))
            {
                Plugin.Log?.LogInfo($"[LobbyManager] Player left: {_players[peerId].PlayerName} ({peerId})");
                _players.Remove(peerId);
                OnPlayerLeft?.Invoke(peerId);
                OnLobbyStateChanged?.Invoke();
            }
        }
        
        /// <summary>
        /// Set local player ready state
        /// </summary>
        public void SetLocalReady(bool isReady)
        {
            LocalIsReady = isReady;
            
            if (_players.ContainsKey(LocalPeerId))
            {
                _players[LocalPeerId].IsReady = isReady;
            }
            
            Plugin.Log?.LogInfo($"[LobbyManager] Local ready: {isReady}");
            OnPlayerReadyChanged?.Invoke(LocalPeerId, isReady);
            OnLobbyStateChanged?.Invoke();
            
            CheckAllPlayersReady();
        }
        
        /// <summary>
        /// Handle remote player ready state change
        /// </summary>
        public void HandlePlayerReady(ulong peerId, bool isReady)
        {
            if (_players.ContainsKey(peerId))
            {
                _players[peerId].IsReady = isReady;
                Plugin.Log?.LogInfo($"[LobbyManager] Player {_players[peerId].PlayerName} ready: {isReady}");
                OnPlayerReadyChanged?.Invoke(peerId, isReady);
                OnLobbyStateChanged?.Invoke();
                
                CheckAllPlayersReady();
            }
        }
        
        /// <summary>
        /// Check if all players are ready
        /// </summary>
        private void CheckAllPlayersReady()
        {
            if (!IsHost) return;
            if (_players.Count < 2) return; // Need at least 2 players
            
            bool allReady = _players.Values.All(p => p.IsReady);
            if (allReady)
            {
                Plugin.Log?.LogInfo("[LobbyManager] All players ready!");
                OnAllPlayersReady?.Invoke();
            }
        }
        
        /// <summary>
        /// Set local airfield selection
        /// </summary>
        public void SetLocalAirfield(string airfieldName)
        {
            LocalSelectedAirfield = airfieldName;
            
            if (_players.ContainsKey(LocalPeerId))
            {
                _players[LocalPeerId].SelectedAirfield = airfieldName;
            }
            
            Plugin.Log?.LogInfo($"[LobbyManager] Local airfield: {airfieldName}");
            OnLobbyStateChanged?.Invoke();
        }
        
        /// <summary>
        /// Handle remote player airfield selection
        /// </summary>
        public void HandlePlayerAirfieldSelect(ulong peerId, string airfieldName)
        {
            if (_players.ContainsKey(peerId))
            {
                _players[peerId].SelectedAirfield = airfieldName;
                Plugin.Log?.LogInfo($"[LobbyManager] Player {_players[peerId].PlayerName} selected airfield: {airfieldName}");
                OnLobbyStateChanged?.Invoke();
            }
        }
        
        /// <summary>
        /// Set local aircraft selection
        /// </summary>
        public void SetLocalAircraft(string aircraftName)
        {
            LocalSelectedAircraft = aircraftName;
            
            if (_players.ContainsKey(LocalPeerId))
            {
                _players[LocalPeerId].SelectedAircraft = aircraftName;
            }
            
            Plugin.Log?.LogInfo($"[LobbyManager] Local aircraft: {aircraftName}");
            OnLobbyStateChanged?.Invoke();
        }
        
        /// <summary>
        /// Set spawn settings (host only)
        /// </summary>
        public void SetSpawnSettings(LobbySpawnType spawnType, string mapName = null)
        {
            if (!IsHost) return;
            
            SpawnType = spawnType;
            if (!string.IsNullOrEmpty(mapName))
            {
                MapName = mapName;
            }
            
            Plugin.Log?.LogInfo($"[LobbyManager] Spawn settings: {spawnType} on {MapName}");
            Plugin.Instance?.Network?.SendLobbySpawnSettings(SpawnType, MapName);
            OnLobbyStateChanged?.Invoke();
        }
        
        /// <summary>
        /// Start the game (host only)
        /// </summary>
        public void StartGame()
        {
            if (!IsHost)
            {
                Plugin.Log?.LogWarning("[LobbyManager] Only host can start game");
                return;
            }
            
            // Check all players ready
            if (!_players.Values.All(p => p.IsReady))
            {
                Plugin.Log?.LogWarning("[LobbyManager] Not all players ready");
                return;
            }
            
            GameLoading = true;
            Plugin.Log?.LogInfo("[LobbyManager] Starting game...");
            OnGameStarting?.Invoke();
            OnLobbyStateChanged?.Invoke();
        }
        
        /// <summary>
        /// Handle game start from host (client only)
        /// </summary>
        public void HandleGameStart(string mapName, LobbySpawnType spawnType)
        {
            MapName = mapName;
            SpawnType = spawnType;
            GameLoading = true;
            
            Plugin.Log?.LogInfo($"[LobbyManager] Game starting: {mapName}, spawn: {spawnType}");
            OnGameStarting?.Invoke();
            OnLobbyStateChanged?.Invoke();
        }
        
        /// <summary>
        /// Set local loading complete
        /// </summary>
        public void SetLocalLoaded()
        {
            LocalIsLoaded = true;
            
            if (_players.ContainsKey(LocalPeerId))
            {
                _players[LocalPeerId].IsLoaded = true;
            }
            
            Plugin.Log?.LogInfo("[LobbyManager] Local loading complete");
            OnLobbyStateChanged?.Invoke();
            
            CheckAllPlayersLoaded();
        }
        
        /// <summary>
        /// Handle remote player loading complete
        /// </summary>
        public void HandlePlayerLoaded(ulong peerId)
        {
            if (_players.ContainsKey(peerId))
            {
                _players[peerId].IsLoaded = true;
                Plugin.Log?.LogInfo($"[LobbyManager] Player {_players[peerId].PlayerName} loaded");
                OnLobbyStateChanged?.Invoke();
                
                CheckAllPlayersLoaded();
            }
        }
        
        /// <summary>
        /// Check if all players are loaded (host only)
        /// </summary>
        private void CheckAllPlayersLoaded()
        {
            if (!IsHost) return;
            if (!GameLoading) return;
            
            bool allLoaded = _players.Values.All(p => p.IsLoaded);
            if (allLoaded)
            {
                Plugin.Log?.LogInfo("[LobbyManager] All players loaded! Spawning...");
                GameStarted = true;
                GameLoading = false;
                OnAllPlayersLoaded?.Invoke();
            }
        }
        
        /// <summary>
        /// Handle spawn players signal
        /// </summary>
        public void HandleSpawnPlayers()
        {
            GameStarted = true;
            GameLoading = false;
            Plugin.Log?.LogInfo("[LobbyManager] Spawning players!");
            OnSpawnPlayers?.Invoke();
        }
        
        /// <summary>
        /// Get the current lobby state for broadcasting
        /// </summary>
        public LobbyStatePacket GetLobbyStatePacket()
        {
            return new LobbyStatePacket
            {
                HostName = HostName,
                MapName = MapName,
                SpawnType = SpawnType,
                GameStarted = GameStarted,
                GameLoading = GameLoading,
                Players = _players.Values.ToArray()
            };
        }
        
        /// <summary>
        /// Update (call from main update loop)
        /// </summary>
        public void Update()
        {
            if (!IsInLobby) return;
            
            // Host broadcasts state periodically
            if (IsHost && Time.time - _lastStateBroadcast > STATE_BROADCAST_INTERVAL)
            {
                _lastStateBroadcast = Time.time;
                Plugin.Instance?.Network?.BroadcastLobbyState();
            }
        }
        
        /// <summary>
        /// Get player info by peer ID
        /// </summary>
        public LobbyPlayerInfo GetPlayer(ulong peerId)
        {
            return _players.TryGetValue(peerId, out var player) ? player : null;
        }
        
        /// <summary>
        /// Get number of players in lobby
        /// </summary>
        public int PlayerCount => _players.Count;
        
        /// <summary>
        /// Check if all players are ready
        /// </summary>
        public bool AreAllPlayersReady => _players.Count >= 1 && _players.Values.All(p => p.IsReady);
        
        /// <summary>
        /// Check if all players are loaded
        /// </summary>
        public bool AreAllPlayersLoaded => _players.Values.All(p => p.IsLoaded);
        
        /// <summary>
        /// Reset for new game
        /// </summary>
        public void ResetForNewGame()
        {
            GameStarted = false;
            GameLoading = false;
            LocalIsLoaded = false;
            
            foreach (var player in _players.Values)
            {
                player.IsReady = false;
                player.IsLoaded = false;
            }
            
            OnLobbyStateChanged?.Invoke();
        }
    }
}
