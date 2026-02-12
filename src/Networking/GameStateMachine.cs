using System;
using UnityEngine;

namespace TCAMultiplayer.Networking
{
    /// <summary>
    /// All possible states in the multiplayer game flow.
    /// This is the SINGLE SOURCE OF TRUTH for game state.
    /// </summary>
    public enum GameState
    {
        /// <summary>Not connected to any game</summary>
        Disconnected,

        /// <summary>Hosting a game, waiting in lobby</summary>
        HostingLobby,

        /// <summary>Attempting to connect to a host</summary>
        Connecting,

        /// <summary>Connected as client, in lobby</summary>
        ClientLobby,

        /// <summary>All players ready, loading scenes</summary>
        Loading,

        /// <summary>Scenes loaded, waiting for all players</summary>
        WaitingForPlayers,

        /// <summary>All players loaded, spawning aircraft</summary>
        Spawning,

        /// <summary>In active gameplay</summary>
        InGame,

        /// <summary>Player died, showing respawn screen</summary>
        Respawning
    }

    /// <summary>
    /// Manages game state transitions with validation.
    /// Consolidates IsHost, LocalPeerId, and connection state into a single class.
    /// </summary>
    public class GameStateMachine
    {
        public static GameStateMachine Instance { get; private set; }

        // Core state
        public GameState CurrentState { get; private set; } = GameState.Disconnected;
        public GameState PreviousState { get; private set; } = GameState.Disconnected;

        // Identity (single source of truth)
        public bool IsHost { get; private set; }
        public ulong LocalPeerId { get; private set; }
        public string LocalPlayerName { get; set; } = "Player";

        // Connection info
        public string HostAddress { get; private set; }
        public int HostPort { get; private set; }

        // Timing for race condition prevention
        public float StateChangeTime { get; private set; }
        public float LoadingStartTime { get; private set; }
        public float SpawningStartTime { get; private set; }

        // Timeouts (configurable)
        public float ConnectionTimeout { get; set; } = 10f;
        public float LoadingTimeout { get; set; } = 30f;
        public float SpawnTimeout { get; set; } = 15f;

        // Events
        public event Action<GameState, GameState> OnStateChanged;
        public event Action OnConnectionFailed;
        public event Action OnLoadingTimeout;
        public event Action OnSpawnTimeout;

        public GameStateMachine()
        {
            Instance = this;
        }

        #region State Queries

        public bool IsInLobby => CurrentState == GameState.HostingLobby || CurrentState == GameState.ClientLobby;
        public bool IsConnected => CurrentState != GameState.Disconnected && CurrentState != GameState.Connecting;
        public bool IsInGame => CurrentState == GameState.InGame || CurrentState == GameState.Respawning;
        public bool IsLoading => CurrentState == GameState.Loading || CurrentState == GameState.WaitingForPlayers;

        public float TimeSinceStateChange => Time.time - StateChangeTime;

        #endregion

        #region State Transitions

        /// <summary>
        /// Transition to a new state with validation.
        /// Returns true if transition was valid and executed.
        /// </summary>
        public bool TransitionTo(GameState newState)
        {
            if (!IsValidTransition(CurrentState, newState))
            {
                Plugin.Log?.LogWarning($"[GameStateMachine] Invalid transition: {CurrentState} -> {newState}");
                return false;
            }

            PreviousState = CurrentState;
            CurrentState = newState;
            StateChangeTime = Time.time;

            // Track specific timestamps
            if (newState == GameState.Loading)
                LoadingStartTime = Time.time;
            if (newState == GameState.Spawning)
                SpawningStartTime = Time.time;

            Plugin.Log?.LogInfo($"[GameStateMachine] State: {PreviousState} -> {CurrentState}");
            OnStateChanged?.Invoke(PreviousState, CurrentState);

            return true;
        }

        /// <summary>
        /// Check if a state transition is valid.
        /// </summary>
        private bool IsValidTransition(GameState from, GameState to)
        {
            // Always allow transition to Disconnected (reset)
            if (to == GameState.Disconnected)
                return true;

            return (from, to) switch
            {
                // From Disconnected
                (GameState.Disconnected, GameState.HostingLobby) => true,
                (GameState.Disconnected, GameState.Connecting) => true,

                // From Connecting
                (GameState.Connecting, GameState.ClientLobby) => true,
                (GameState.Connecting, GameState.Disconnected) => true, // Connection failed

                // From Lobby states
                (GameState.HostingLobby, GameState.Loading) => true,
                (GameState.ClientLobby, GameState.Loading) => true,

                // From Loading
                (GameState.Loading, GameState.WaitingForPlayers) => true,
                (GameState.Loading, GameState.Disconnected) => true, // Loading failed

                // From WaitingForPlayers
                (GameState.WaitingForPlayers, GameState.Spawning) => true,
                (GameState.WaitingForPlayers, GameState.Disconnected) => true, // Timeout

                // From Spawning
                (GameState.Spawning, GameState.InGame) => true,
                (GameState.Spawning, GameState.Disconnected) => true, // Spawn failed

                // From InGame
                (GameState.InGame, GameState.Respawning) => true,
                (GameState.InGame, GameState.Disconnected) => true, // Disconnect

                // From Respawning
                (GameState.Respawning, GameState.InGame) => true, // Respawned
                (GameState.Respawning, GameState.Disconnected) => true, // Leave

                _ => false
            };
        }

        #endregion

        #region Host/Join Actions

        /// <summary>
        /// Start hosting a game.
        /// </summary>
        public bool StartHosting(int port, string playerName = null)
        {
            Plugin.Log?.LogInfo($"[GameStateMachine] StartHosting called. CurrentState={CurrentState}, port={port}, playerName={playerName ?? "null"}");

            if (!TransitionTo(GameState.HostingLobby))
            {
                Plugin.Log?.LogError($"[GameStateMachine] StartHosting FAILED - could not transition from {CurrentState} to HostingLobby");
                return false;
            }

            IsHost = true;
            LocalPeerId = 1; // Host is always peer 1
            HostPort = port;
            HostAddress = "localhost";
            if (!string.IsNullOrEmpty(playerName))
                LocalPlayerName = playerName;

            Plugin.Log?.LogInfo($"[GameStateMachine] Started hosting on port {port} as {LocalPlayerName}");
            return true;
        }

        /// <summary>
        /// Start connecting to a host.
        /// </summary>
        public bool StartConnecting(string address, int port, string playerName = null)
        {
            Plugin.Log?.LogInfo($"[GameStateMachine] StartConnecting called. CurrentState={CurrentState}, address={address}, port={port}, playerName={playerName ?? "null"}");

            if (!TransitionTo(GameState.Connecting))
            {
                Plugin.Log?.LogError($"[GameStateMachine] StartConnecting FAILED - could not transition from {CurrentState} to Connecting");
                return false;
            }

            IsHost = false;
            LocalPeerId = 0; // Will be assigned by host
            HostAddress = address;
            HostPort = port;
            if (!string.IsNullOrEmpty(playerName))
                LocalPlayerName = playerName;

            Plugin.Log?.LogInfo($"[GameStateMachine] Connecting to {address}:{port} as {LocalPlayerName}");
            return true;
        }

        /// <summary>
        /// Called when connection to host is established.
        /// </summary>
        public bool OnConnected(ulong assignedPeerId)
        {
            Plugin.Log?.LogInfo($"[GameStateMachine] OnConnected called. CurrentState={CurrentState}, assignedPeerId={assignedPeerId}");

            if (CurrentState != GameState.Connecting)
            {
                Plugin.Log?.LogWarning($"[GameStateMachine] OnConnected called in wrong state: {CurrentState} (expected Connecting)");
                return false;
            }

            LocalPeerId = assignedPeerId;

            if (!TransitionTo(GameState.ClientLobby))
            {
                Plugin.Log?.LogError($"[GameStateMachine] OnConnected FAILED - could not transition from {CurrentState} to ClientLobby");
                return false;
            }

            Plugin.Log?.LogInfo($"[GameStateMachine] Connected! Assigned peer ID: {assignedPeerId}, now in state: {CurrentState}");
            return true;
        }

        /// <summary>
        /// Disconnect and reset state.
        /// </summary>
        public void Disconnect()
        {
            Plugin.Log?.LogInfo($"[GameStateMachine] Disconnect called. CurrentState={CurrentState}, IsHost={IsHost}, LocalPeerId={LocalPeerId}");

            bool transitioned = TransitionTo(GameState.Disconnected);
            if (!transitioned)
            {
                Plugin.Log?.LogWarning($"[GameStateMachine] Disconnect transition returned false (but Disconnected should always be valid)");
            }

            IsHost = false;
            LocalPeerId = 0;
            HostAddress = null;
            HostPort = 0;

            Plugin.Log?.LogInfo($"[GameStateMachine] Disconnected. State reset complete.");
        }

        #endregion

        #region Game Flow Actions

        /// <summary>
        /// Start loading the game (host initiates, clients follow).
        /// </summary>
        public bool StartLoading()
        {
            if (!IsInLobby)
            {
                Plugin.Log?.LogWarning($"[GameStateMachine] Cannot start loading from state: {CurrentState}");
                return false;
            }

            return TransitionTo(GameState.Loading);
        }

        /// <summary>
        /// Local loading complete, waiting for other players.
        /// </summary>
        public bool OnLoadingComplete()
        {
            if (CurrentState != GameState.Loading)
            {
                Plugin.Log?.LogWarning($"[GameStateMachine] OnLoadingComplete called in wrong state: {CurrentState}");
                return false;
            }

            return TransitionTo(GameState.WaitingForPlayers);
        }

        /// <summary>
        /// All players loaded, start spawning.
        /// </summary>
        public bool StartSpawning()
        {
            if (CurrentState != GameState.WaitingForPlayers)
            {
                Plugin.Log?.LogWarning($"[GameStateMachine] Cannot start spawning from state: {CurrentState}");
                return false;
            }

            return TransitionTo(GameState.Spawning);
        }

        /// <summary>
        /// Spawning complete, enter gameplay.
        /// </summary>
        public bool OnSpawnComplete()
        {
            if (CurrentState != GameState.Spawning)
            {
                Plugin.Log?.LogWarning($"[GameStateMachine] OnSpawnComplete called in wrong state: {CurrentState}");
                return false;
            }

            return TransitionTo(GameState.InGame);
        }

        /// <summary>
        /// Player died, show respawn screen.
        /// </summary>
        public bool OnPlayerDied()
        {
            if (CurrentState != GameState.InGame)
            {
                Plugin.Log?.LogWarning($"[GameStateMachine] OnPlayerDied called in wrong state: {CurrentState}");
                return false;
            }

            return TransitionTo(GameState.Respawning);
        }

        /// <summary>
        /// Player respawned, back to gameplay.
        /// </summary>
        public bool OnRespawned()
        {
            if (CurrentState != GameState.Respawning)
            {
                Plugin.Log?.LogWarning($"[GameStateMachine] OnRespawned called in wrong state: {CurrentState}");
                return false;
            }

            return TransitionTo(GameState.InGame);
        }

        #endregion

        #region Update (Timeout Checks)

        /// <summary>
        /// Call from main update loop to check for timeouts.
        /// </summary>
        public void Update()
        {
            switch (CurrentState)
            {
                case GameState.Connecting:
                    if (TimeSinceStateChange > ConnectionTimeout)
                    {
                        Plugin.Log?.LogWarning("[GameStateMachine] Connection timeout!");
                        OnConnectionFailed?.Invoke();
                        Disconnect();
                    }
                    break;

                case GameState.Loading:
                case GameState.WaitingForPlayers:
                    if (Time.time - LoadingStartTime > LoadingTimeout)
                    {
                        Plugin.Log?.LogWarning("[GameStateMachine] Loading timeout!");
                        OnLoadingTimeout?.Invoke();
                        // Don't auto-disconnect, let the user decide
                    }
                    break;

                case GameState.Spawning:
                    if (Time.time - SpawningStartTime > SpawnTimeout)
                    {
                        Plugin.Log?.LogWarning("[GameStateMachine] Spawn timeout!");
                        OnSpawnTimeout?.Invoke();
                        // Don't auto-disconnect, let the user decide
                    }
                    break;
            }
        }

        #endregion

        #region Debug

        public string GetDebugInfo()
        {
            return $"State: {CurrentState} | Host: {IsHost} | PeerId: {LocalPeerId} | " +
                   $"Time in state: {TimeSinceStateChange:F1}s";
        }

        #endregion
    }
}
