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
        public bool IsInLobby => GameStateMachine.Instance?.IsInLobby ?? false;
        public bool IsHost => GameStateMachine.Instance?.IsHost ?? false;
        public string HostName { get; private set; } = "Host";
        public string MapName { get; private set; } = "ActionIsland";
        public LobbySpawnType SpawnType { get; private set; } = LobbySpawnType.Runway;
        public bool GameStarted { get; private set; }
        public bool GameLoading { get; private set; }
        public bool SoloModeEnabled { get; set; } = true;

        // Players
        private Dictionary<ulong, LobbyPlayerInfo> _players = new Dictionary<ulong, LobbyPlayerInfo>();
        public IReadOnlyDictionary<ulong, LobbyPlayerInfo> Players => _players;

        // Local player info
        public ulong LocalPeerId { get; private set; }
        public string LocalPlayerName { get; private set; } = "Player";
        public string LocalSelectedAirfield { get; private set; } = "";
        public string LocalSelectedAircraft { get; private set; } = "AV8B";
        public string LocalSelectedLoadout { get; private set; } = "Clean";
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
        // STATE_BROADCAST_INTERVAL now in NetworkConfig
        private int _lastBroadcastHash = 0;

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
            LocalPeerId = hostPeerId;
            HostName = hostName ?? LocalPlayerName;
            GameStarted = false;
            GameLoading = false;

            _players.Clear();

            // Add host as first player
            // NOTE: PlayerName should be LocalPlayerName (the host's player name),
            // NOT HostName (the server name). These are separate concepts.
            var hostPlayer = new LobbyPlayerInfo
            {
                PeerId = hostPeerId,
                PlayerName = LocalPlayerName,  // Fixed: Use player name, not server name
                SelectedAirfield = LocalSelectedAirfield,
                SelectedAircraft = LocalSelectedAircraft,
                IsReady = false,
                IsLoaded = false,
                IsHost = GameStateMachine.Instance?.IsHost ?? false
            };
            _players[hostPeerId] = hostPlayer;

            Plugin.Log?.LogInfo($"[LobbyManager] Created lobby as host: ServerName={HostName}, PlayerName={LocalPlayerName}");
            OnLobbyStateChanged?.Invoke();
        }

        /// <summary>
        /// Join an existing lobby as client
        /// </summary>
        public void JoinLobby(ulong localPeerId)
        {
            LocalPeerId = localPeerId;
            GameStarted = false;
            GameLoading = false;

            _players.Clear();

            // Add local player to the players dictionary so UI shows correctly
            var localPlayer = new LobbyPlayerInfo
            {
                PeerId = localPeerId,
                PlayerName = LocalPlayerName,
                SelectedAirfield = LocalSelectedAirfield,
                SelectedAircraft = LocalSelectedAircraft,
                IsReady = LocalIsReady,
                IsLoaded = LocalIsLoaded,
                IsHost = false // Client is never the host
            };
            _players[localPeerId] = localPlayer;

            Plugin.Log?.LogInfo($"[LobbyManager] Joined lobby as client: {LocalPlayerName} (PeerId: {localPeerId})");
            OnLobbyStateChanged?.Invoke();
        }

        /// <summary>
        /// Leave the current lobby
        /// </summary>
        public void LeaveLobby()
        {
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
                IsHost = false // Joining players are never the host
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

            int minPlayers = SoloModeEnabled ? 1 : 2;
            if (_players.Count < minPlayers)
            {
                if (SoloModeEnabled && _players.Count == 0)
                {
                    Plugin.Log?.LogWarning("[LobbyManager] No players in lobby");
                }
                return;
            }

            bool allReady = _players.Values.All(p => p.IsReady);
            if (allReady)
            {
                if (SoloModeEnabled && _players.Count == 1)
                {
                    Plugin.Log?.LogInfo("[LobbyManager] Solo mode - single player ready!");
                }
                else
                {
                    Plugin.Log?.LogInfo("[LobbyManager] All players ready!");
                }
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
        /// Set local loadout selection
        /// </summary>
        public void SetLocalLoadout(string loadoutName)
        {
            LocalSelectedLoadout = loadoutName;

            if (_players.ContainsKey(LocalPeerId))
            {
                _players[LocalPeerId].SelectedLoadout = loadoutName;
            }

            Plugin.Log?.LogInfo($"[LobbyManager] Local loadout: {loadoutName}");
            OnLobbyStateChanged?.Invoke();
        }

        /// <summary>
        /// Handle remote player loadout selection
        /// </summary>
        public void HandlePlayerLoadoutSelect(ulong peerId, string loadoutName)
        {
            if (_players.ContainsKey(peerId))
            {
                _players[peerId].SelectedLoadout = loadoutName;
                Plugin.Log?.LogInfo($"[LobbyManager] Player {_players[peerId].PlayerName} selected loadout: {loadoutName}");
                OnLobbyStateChanged?.Invoke();
            }
        }

        /// <summary>
        /// Handle remote player aircraft selection
        /// </summary>
        public void HandlePlayerAircraftSelect(ulong peerId, string aircraftName)
        {
            if (_players.ContainsKey(peerId))
            {
                _players[peerId].SelectedAircraft = aircraftName;
                Plugin.Log?.LogInfo($"[LobbyManager] Player {_players[peerId].PlayerName} selected aircraft: {aircraftName}");
                OnLobbyStateChanged?.Invoke();
            }
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
            SendSpawnSettings(SpawnType, MapName);
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
            if (IsHost && Time.time - _lastStateBroadcast > NetworkConfig.LOBBY_BROADCAST_INTERVAL)
            {
                _lastStateBroadcast = Time.time;
                BroadcastLobbyState();
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

        #region Packet Sending Methods

        /// <summary>
        /// Helper to get the network manager for sending packets.
        /// </summary>
        private NetworkManager Network => Plugin.Instance?.Network;

        /// <summary>
        /// Send lobby state broadcast (host only)
        /// </summary>
        public void SendLobbyState(LobbyStatePacket packet)
        {
            try
            {
                var data = PacketSerializer.SerializeLobbyState(packet);
                Network?.SendPacket(PacketType.LobbyState, data, reliable: true);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[LobbyManager] SendLobbyState error: {ex.Message}");
            }
        }

        /// <summary>
        /// Send player ready state
        /// </summary>
        public void SendPlayerReady(bool isReady)
        {
            try
            {
                var packet = new LobbyPlayerReadyPacket { PeerId = LocalPeerId, IsReady = isReady };
                var data = PacketSerializer.SerializeLobbyPlayerReady(packet);
                Network?.SendPacket(PacketType.LobbyPlayerReady, data, reliable: true);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[LobbyManager] SendPlayerReady error: {ex.Message}");
            }
        }

        /// <summary>
        /// Send airfield selection
        /// </summary>
        public void SendAirfieldSelect(string airfieldName)
        {
            try
            {
                var packet = new LobbyAirfieldSelectPacket { PeerId = LocalPeerId, AirfieldName = airfieldName };
                var data = PacketSerializer.SerializeLobbyAirfieldSelect(packet);
                Network?.SendPacket(PacketType.LobbyAirfieldSelect, data, reliable: true);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[LobbyManager] SendAirfieldSelect error: {ex.Message}");
            }
        }

        /// <summary>
        /// Send aircraft selection
        /// </summary>
        public void SendAircraftSelect(string aircraftName)
        {
            try
            {
                var packet = new LobbyAircraftSelectPacket { PeerId = LocalPeerId, AircraftName = aircraftName };
                var data = PacketSerializer.SerializeLobbyAircraftSelect(packet);
                Network?.SendPacket(PacketType.AircraftSelect, data, reliable: true);
                Plugin.Log?.LogInfo($"[LobbyManager] Sent aircraft select: {aircraftName}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[LobbyManager] SendAircraftSelect error: {ex.Message}");
            }
        }

        /// <summary>
        /// Send loadout selection
        /// </summary>
        public void SendLoadoutSelect(string loadoutName)
        {
            try
            {
                var packet = new LobbyLoadoutSelectPacket { PeerId = LocalPeerId, LoadoutName = loadoutName };
                var data = PacketSerializer.SerializeLobbyLoadoutSelect(packet);
                Network?.SendPacket(PacketType.LoadoutSelect, data, reliable: true);
                Plugin.Log?.LogInfo($"[LobbyManager] Sent loadout select: {loadoutName}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[LobbyManager] SendLoadoutSelect error: {ex.Message}");
            }
        }

        /// <summary>
        /// Send spawn settings (host only)
        /// </summary>
        public void SendSpawnSettings(LobbySpawnType spawnType, string mapName)
        {
            try
            {
                var packet = new LobbySpawnSettingsPacket { SpawnType = spawnType, MapName = mapName ?? "ActionIsland" };
                var data = PacketSerializer.SerializeLobbySpawnSettings(packet);
                Network?.SendPacket(PacketType.LobbySpawnSettings, data, reliable: true);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[LobbyManager] SendSpawnSettings error: {ex.Message}");
            }
        }

        /// <summary>
        /// Send start game (host only)
        /// </summary>
        public void SendStartGame(string mapName, LobbySpawnType spawnType)
        {
            try
            {
                var packet = new LobbyStartGamePacket { MapName = mapName ?? "ActionIsland", SpawnType = spawnType };
                var data = PacketSerializer.SerializeLobbyStartGame(packet);
                Network?.SendPacket(PacketType.LobbyStartGame, data, reliable: true);
                Plugin.Log?.LogInfo($"[LobbyManager] Sent start game: {mapName}, {spawnType}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[LobbyManager] SendStartGame error: {ex.Message}");
            }
        }

        /// <summary>
        /// Send loading complete notification
        /// </summary>
        public void SendLoadingComplete()
        {
            try
            {
                var packet = new LobbyLoadingCompletePacket { PeerId = LocalPeerId };
                var data = PacketSerializer.SerializeLobbyLoadingComplete(packet);
                Network?.SendPacket(PacketType.LobbyLoadingComplete, data, reliable: true);
                Plugin.Log?.LogInfo("[LobbyManager] Sent loading complete");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[LobbyManager] SendLoadingComplete error: {ex.Message}");
            }
        }

        /// <summary>
        /// Send spawn players signal (host only)
        /// </summary>
        public void SendSpawnPlayers()
        {
            try
            {
                var packet = new LobbySpawnPlayersPacket { Timestamp = Time.time };
                var data = PacketSerializer.SerializeLobbySpawnPlayers(packet);
                Network?.SendPacket(PacketType.LobbySpawnPlayers, data, reliable: true);
                Plugin.Log?.LogInfo("[LobbyManager] Sent spawn players signal");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[LobbyManager] SendSpawnPlayers error: {ex.Message}");
            }
        }

        /// <summary>
        /// Send respawn request
        /// </summary>
        public void SendRespawnRequest()
        {
            try
            {
                var packet = new LobbyRespawnRequestPacket { PeerId = LocalPeerId };
                var data = PacketSerializer.SerializeLobbyRespawnRequest(packet);
                Network?.SendPacket(PacketType.LobbyRespawnRequest, data, reliable: true);
                Plugin.Log?.LogInfo("[LobbyManager] Sent respawn request");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[LobbyManager] SendRespawnRequest error: {ex.Message}");
            }
        }

        /// <summary>
        /// Broadcast lobby state to all clients (host calls this periodically)
        /// Only sends if state has changed since last broadcast.
        /// </summary>
        public void BroadcastLobbyState()
        {
            if (!IsHost) return;

            int currentHash = ComputeLobbyStateHash();
            if (currentHash == _lastBroadcastHash) return;

            _lastBroadcastHash = currentHash;
            SendLobbyState(GetLobbyStatePacket());
        }

        /// <summary>
        /// Compute a hash of the current lobby state for dirty-checking.
        /// </summary>
        private int ComputeLobbyStateHash()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (HostName?.GetHashCode() ?? 0);
                hash = hash * 31 + (MapName?.GetHashCode() ?? 0);
                hash = hash * 31 + (int)SpawnType;
                hash = hash * 31 + (GameStarted ? 1 : 0);
                hash = hash * 31 + (GameLoading ? 1 : 0);
                hash = hash * 31 + _players.Count;

                foreach (var kvp in _players)
                {
                    var p = kvp.Value;
                    hash = hash * 31 + (int)p.PeerId;
                    hash = hash * 31 + (p.PlayerName?.GetHashCode() ?? 0);
                    hash = hash * 31 + (p.SelectedAirfield?.GetHashCode() ?? 0);
                    hash = hash * 31 + (p.SelectedAircraft?.GetHashCode() ?? 0);
                    hash = hash * 31 + (p.IsReady ? 1 : 0);
                    hash = hash * 31 + (p.IsLoaded ? 1 : 0);
                    hash = hash * 31 + (p.IsHost ? 1 : 0);
                }

                return hash;
            }
        }

        #endregion
    }
}
