using BepInEx;
using BepInEx.Logging;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using TCAMultiplayer.Networking;
using TCAMultiplayer.UI;
using TCAMultiplayer.Game;
using System;
using Cysharp.Threading.Tasks;
using UnityEngine.SceneManagement;
using Falcon.Database;
using Falcon.Game2;
using Falcon.UI;
using Falcon.World;

namespace TCAMultiplayer
{
    [BepInPlugin(PluginInfo.GUID, PluginInfo.NAME, PluginInfo.VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        public static ConfigEntry<bool> VerboseAll { get; private set; }
        public static ConfigEntry<bool> VerboseNetworking { get; private set; }
        public static ConfigEntry<bool> VerboseTransport { get; private set; }
        public static ConfigEntry<bool> VerbosePackets { get; private set; }
        public static ConfigEntry<bool> VerbosePatches { get; private set; }
        public static ConfigEntry<bool> VerbosePlayer { get; private set; }
        public static ConfigEntry<bool> VerboseDamage { get; private set; }
        public static ConfigEntry<bool> VerboseWeapons { get; private set; }
        public static ConfigEntry<bool> VerboseInterpolation { get; private set; }
        public static ConfigEntry<bool> VerboseReflection { get; private set; }
        public static ConfigEntry<float> LogIntervalSeconds { get; private set; }
        public static ConfigEntry<int> PacketLogSampleRate { get; private set; }
        public static ConfigEntry<int> HighFreqLogSampleRate { get; private set; }

        public static ConfigEntry<string> ConfigUsername { get; private set; }
        public static ConfigEntry<string> ConfigLastIP { get; private set; }
        public static ConfigEntry<string> ConfigLastPort { get; private set; }
        public static ConfigEntry<string> ConfigHostName { get; private set; }
        public static ConfigEntry<string> ConfigHostPort { get; private set; }
        
        public static ConfigEntry<string> ConfigLocalAircraft { get; private set; }
        public static ConfigEntry<string> ConfigLocalLoadout { get; private set; }
        public static ConfigEntry<string> ConfigLocalAirfield { get; private set; }
        
        // Host settings
        public static ConfigEntry<int> ConfigHostSpawnType { get; private set; }
        public static ConfigEntry<int> ConfigHostTimeOfDay { get; private set; }
        public static ConfigEntry<bool> ConfigHostAircraftCollisions { get; private set; }

        public NetworkManager Network { get; private set; }
        public GameStateMachine GameState { get; private set; }

        // Lobby system components (ALL UI is native Canvas+TMP — see LobbyUI.cs header)
        public LobbyManager Lobby { get; private set; }
        public LanDiscovery Discovery { get; private set; }
        public SpawnManager Spawner { get; private set; }
        public ScoreTracker Scores { get; private set; }
        public ScoreboardHUD ScoreboardHUD { get; private set; }
        public RespawnScreen RespawnUI { get; private set; }

        private Harmony _harmony;
        private GameObject _runnerObject;
        private bool _isReturningToLobby;
        private bool _isSpawningLocal;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            
            // Initialize instance logger FIRST before any other logging
            InstanceLogger.Initialize();
            
            BindLoggingConfig();

            Log.LogInfo("===========================================");
            Log.LogInfo("  TCA Multiplayer v" + PluginInfo.VERSION);
            Log.LogInfo("  Loading...");
            Log.LogInfo("===========================================");

            try
            {
                Log.LogInfo("Initializing state...");
                GameState = new GameStateMachine();

                Log.LogInfo("Initializing network...");
                Network = new NetworkManager();

                Log.LogInfo("Initializing lobby system...");
                Lobby = new LobbyManager();
                Discovery = new LanDiscovery();
                Spawner = new SpawnManager();
                Scores = new ScoreTracker();
                ScoreboardHUD = new ScoreboardHUD();

                Log.LogInfo("Initializing mod manifest collector...");
                ModCompatibility.ModManifestCollector.Initialize();

                // Wire up lobby events (native UI handles its own wiring via MultiplayerMenu)
                SetupLobbyEvents();

                Log.LogInfo("Applying Harmony patches...");
                _harmony = new Harmony(PluginInfo.GUID);

                try
                {
                    _harmony.PatchAll();
                    Log.LogInfo("Harmony patches applied successfully");
                    
                    // Apply manual patches for WorldDestruction (needs type resolution from Assembly-CSharp)
                    Patches.WorldDestructionPatches.ApplyPatches(_harmony);
                    
                    // Apply manual patches for Explosion sync (Munition.Explode postfix)
                    Patches.ExplosionPatches.ApplyPatches(_harmony);
                    
                    // Apply manual patches for Aircraft destruction VFX sync
                    Patches.AircraftDestructionPatches.ApplyPatches(_harmony);
                }
                catch (Exception ex)
                {
                    Log.LogError($"Harmony patching failed: {ex.Message}");
                }

                Log.LogInfo("Creating plugin runner...");
                _runnerObject = new GameObject("TCAMultiplayer_Runner");
                GameObject.DontDestroyOnLoad(_runnerObject);
                _runnerObject.hideFlags = HideFlags.HideAndDontSave;
                _runnerObject.AddComponent<PluginRunner>();

                // Initialize combat sync
                Player.RealCombatSync.Initialize();

                // Initialize aircraft collision manager
                var collisionManager = _runnerObject.AddComponent<Player.AircraftCollisionManager>();
                collisionManager.SetRemoteAircraftManager(Network.RemoteAircraftManager);
                Log.LogInfo("Aircraft collision manager initialized");

                // Create native respawn screen (attached to persistent runner)
                RespawnUI = _runnerObject.AddComponent<RespawnScreen>();
                RespawnUI.Initialize();

                // Wire respawn events (must be after RespawnUI is created)
                RespawnUI.OnRespawnRequested += (airfield, spawnType) =>
                {
                    Log.LogInfo($"[Plugin] Native respawn: airfield={airfield} spawnType={spawnType}");

                    if (string.IsNullOrEmpty(airfield))
                    {
                        var airfields = AirfieldHelper.GetAirfieldNames();
                        if (airfields.Length > 0) airfield = airfields[0];
                    }

                    string aircraftName = Lobby?.LocalSelectedAircraft ?? "AV8B";
                    Lobby?.SendRespawnRequest();
                    bool success = Spawner?.SpawnPlayerAtAirfield(airfield, aircraftName, spawnType) ?? false;

                    if (success)
                    {
                        RespawnUI?.Hide();
                        HandleLocalRespawnCompleted();
                    }
                    else
                    {
                        Log.LogError("[Plugin] Native respawn failed!");
                    }
                };

                Log.LogInfo("===========================================");
                Log.LogInfo("TCA Multiplayer loaded successfully!");
                Log.LogInfo("Click MULTIPLAYER on the main menu to open the lobby");
                Log.LogInfo("===========================================");
            }
            catch (Exception ex)
            {
                Log.LogError($"FATAL ERROR: {ex.Message}");
                Log.LogError($"Stack: {ex.StackTrace}");
            }
        }

        private void OnDestroy()
        {
            try
            {
                TeardownLobbyEvents();
                InstanceLogger.Shutdown();
                _harmony?.UnpatchSelf();
                Network?.Shutdown();
                Discovery?.Dispose();
                if (_runnerObject != null) GameObject.Destroy(_runnerObject);
            }
            catch (Exception ex)
            {
                Log.LogError($"Cleanup error: {ex.Message}");
            }
        }

        /// <summary>
        /// Set up event connections between lobby components
        /// </summary>
        private void SetupLobbyEvents()
        {
            // All host/join/leave/ready/airfield/start actions are handled directly
            // by MultiplayerMenu.cs (native Canvas UI). No IMGUI event wiring needed.

            // Lobby events -> scene loading / spawning
            // Use named methods instead of lambdas so they can be unsubscribed.
            Lobby.OnGameStarting += OnGameStarting;
            Lobby.OnAllPlayersLoaded += OnAllPlayersLoaded;
            Lobby.OnSpawnPlayers += OnSpawnPlayers;
            Spawner.OnPlayerDied += OnPlayerDied;
        }

        /// <summary>
        /// Unsubscribe events wired in SetupLobbyEvents. Call during cleanup/dispose.
        /// </summary>
        private void TeardownLobbyEvents()
        {
            if (Lobby != null)
            {
                Lobby.OnGameStarting -= OnGameStarting;
                Lobby.OnAllPlayersLoaded -= OnAllPlayersLoaded;
                Lobby.OnSpawnPlayers -= OnSpawnPlayers;
            }
            if (Spawner != null)
            {
                Spawner.OnPlayerDied -= OnPlayerDied;
            }
        }

        private async void OnGameStarting()
        {
            Log.LogInfo("[Plugin] Game starting - loading scenes...");
            bool canStartLoading = GameState?.StartLoading() ?? false;
            if (!canStartLoading)
            {
                Log.LogWarning($"[Plugin] Ignoring duplicate game-start request in state {GameState?.CurrentState}");
                return;
            }

            try
            {
                // Ensure stale airfield references from previous flights are discarded.
                AirfieldHelper.ClearCache();
                string mapName = Lobby?.MapName;
                if (string.IsNullOrWhiteSpace(mapName))
                {
                    mapName = "ActionIsland";
                }

                // Defensive cleanup: if an earlier flow left duplicate gameplay scenes loaded,
                // clear them before starting a new sortie.
                await UnloadAllScenesByName("FlightGame");
                if (!string.Equals(mapName, "FlightGame", StringComparison.OrdinalIgnoreCase))
                {
                    await UnloadAllScenesByName(mapName);
                }

                // 1. Load FlightGame scene (Core game logic)
                Log.LogInfo("[Plugin] Loading FlightGame scene...");
                await SceneManager.LoadSceneAsync("FlightGame", LoadSceneMode.Additive);

                // 2. Load Map
                Log.LogInfo($"[Plugin] Loading Map: {mapName}...");
                string sceneName = mapName;

                await SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);

                // 3. Set Active Scene
                Scene flightScene = SceneManager.GetSceneByName("FlightGame");
                if (flightScene.IsValid())
                {
                    SceneManager.SetActiveScene(flightScene);
                }

                // 4. Wait for FlightGame instance
                Log.LogInfo("[Plugin] Waiting for FlightGame initialization...");
                float timeout = Time.time + 10f;
                FlightGame flightGameInstance = null;
                while (flightGameInstance == null && Time.time < timeout)
                {
                    flightGameInstance = FlightGame.Instance;
                    if (flightGameInstance == null)
                    {
                        await UniTask.Yield();
                    }
                }

                if (flightGameInstance == null)
                {
                    Log.LogError("[Plugin] FlightGame.Instance timed out!");
                    return;
                }

                Log.LogInfo("[Plugin] FlightGame initialized successfully");

                // Notify ready
                GameState?.OnLoadingComplete();
                Lobby?.SetLocalLoaded();
                Lobby?.SendLoadingComplete();
            }
            catch (Exception ex)
            {
                Log.LogError($"[Plugin] Loading error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void OnAllPlayersLoaded()
        {
            if (Lobby.IsHost)
            {
                Log.LogInfo("[Plugin] All players loaded - sending spawn signal");
                Lobby?.SendSpawnPlayers();
                Lobby?.HandleSpawnPlayers();
            }
        }

        private void OnSpawnPlayers()
        {
            if (GameState?.CurrentState == Networking.GameState.InGame)
            {
                Log.LogInfo("[Plugin] Ignoring spawn signal while already InGame");
                return;
            }

            Log.LogInfo("[Plugin] Spawn signal received");
            if (GameState?.CurrentState != Networking.GameState.Spawning)
            {
                bool started = GameState?.StartSpawning() ?? false;
                if (!started && GameState?.CurrentState != Networking.GameState.Spawning)
                {
                    Log.LogWarning($"[Plugin] Ignoring spawn signal in state {GameState?.CurrentState}");
                    return;
                }
            }

            DoSpawn();
        }

        private void OnPlayerDied()
        {
            if (GameState?.CurrentState == Networking.GameState.InGame)
            {
                GameState?.OnPlayerDied();
            }
            RespawnUI?.Show();
        }

        /// <summary>
        /// Execute player spawn
        /// </summary>
        private void DoSpawn()
        {
            if (_isSpawningLocal)
            {
                Log.LogWarning("[Plugin] DoSpawn ignored: spawn already in progress");
                return;
            }

            _isSpawningLocal = true;
            try
            {
                string airfield = Lobby?.LocalSelectedAirfield;
                if (string.IsNullOrEmpty(airfield))
                {
                    var airfields = AirfieldHelper.GetAirfieldNames();
                    if (airfields.Length > 0) airfield = airfields[0];
                }

                string aircraft = Lobby?.LocalSelectedAircraft ?? "AV8B";
                Log.LogInfo($"[Plugin] Spawning at {airfield} with type {Lobby?.SpawnType} aircraft={aircraft}");
                bool spawnSuccess = false;
                try
                {
                    spawnSuccess = Spawner?.SpawnPlayerAtAirfield(airfield, aircraft, Lobby?.SpawnType ?? LobbySpawnType.Runway) ?? false;
                }
                catch (Exception ex)
                {
                    Log.LogError($"[Plugin] Spawn exception: {ex.Message}");
                }

                if (!spawnSuccess)
                {
                    Log.LogError("[Plugin] Spawn failed - staying in current state");
                    return;
                }

                // Apply Time of Day
                try
                {
                    var timeOfDay = Lobby?.SelectedTimeOfDay ?? TimeOfDay.Morning;
                    var env = UnityEngine.Object.FindObjectOfType<Falcon.World.Environment>();
                    if (env != null)
                    {
                        env.SetTimeOfDayPreset(timeOfDay);
                        Log.LogInfo($"[Plugin] Set time of day: {timeOfDay}");
                    }
                    else
                    {
                        Log.LogWarning("[Plugin] Environment singleton not found — cannot set time of day");
                    }
                }
                catch (Exception ex)
                {
                    Log.LogWarning($"[Plugin] Failed to set time of day: {ex.Message}");
                }

                GameState?.OnSpawnComplete();

                // Ensure ScoreTracker has all players registered with correct names at game start
                RegisterAllPlayersInScoreTracker();
            }
            finally
            {
                _isSpawningLocal = false;
            }
        }

        private void HandleLocalRespawnCompleted()
        {
            if (GameState == null) return;

            if (GameState.CurrentState == Networking.GameState.Respawning)
            {
                GameState.OnRespawned();
                return;
            }

            // Defensive fallback: if this was actually part of initial synchronized spawn flow.
            if (GameState.CurrentState == Networking.GameState.Spawning)
            {
                GameState.OnSpawnComplete();
            }
        }

        /// <summary>
        /// Register all known lobby players in ScoreTracker with their real names.
        /// Called at spawn time when all player info is available.
        /// </summary>
        private void RegisterAllPlayersInScoreTracker()
        {
            if (Scores == null || Lobby == null) return;

            foreach (var kvp in Lobby.Players)
            {
                string name = kvp.Value.PlayerName ?? $"Player {kvp.Key}";
                Scores.RegisterPlayer(kvp.Key, name);
                Log.LogInfo($"[Plugin] ScoreTracker registered: {name} (PeerId: {kvp.Key})");
            }
        }

        /// <summary>
        /// Host-only entry point used by sortie end patches.
        /// Sends a synchronized lobby-return command, then runs local return flow.
        /// </summary>
        public void RequestReturnToLobbyAsHost(string source)
        {
            if (GameState?.IsHost != true)
            {
                Log?.LogWarning("[Plugin] RequestReturnToLobbyAsHost ignored: local player is not host");
                return;
            }

            if (_isReturningToLobby) return;

            Log?.LogInfo($"[Plugin] Host requested return to lobby (source={source ?? "unknown"})");
            Network?.SendPacket(PacketType.LobbyReturnToLobby, null, reliable: true);
            ReturnToLobbySequence(source ?? "host").Forget();
        }

        /// <summary>
        /// Called when a client receives the host's return-to-lobby packet.
        /// </summary>
        public void HandleHostRequestedReturnToLobby(string source)
        {
            if (_isReturningToLobby) return;
            Log?.LogInfo($"[Plugin] Host requested return to lobby (source={source ?? "network"})");
            ReturnToLobbySequence(source ?? "network").Forget();
        }

        private async UniTaskVoid ReturnToLobbySequence(string source)
        {
            if (_isReturningToLobby) return;
            _isReturningToLobby = true;

            try
            {
                // Let the menu task that triggered this complete cleanly before scene teardown.
                await UniTask.Yield(PlayerLoopTiming.Update);

                Log?.LogInfo($"[Plugin] Returning to multiplayer lobby... source={source}");

                // Ensure we leave flight in an unpaused/unblocked state.
                if (Falcon.GamePause.IsPaused) Falcon.GamePause.ResumeGame();
                Time.timeScale = 1f;
                AudioListener.pause = false;
                TinyCursor.LockState = CursorLockMode.None;

                RespawnUI?.Hide();
                Spawner?.Reset();
                Scores?.Reset();
                Network?.RemoteAircraftManager?.Cleanup();
                Player.RealCombatSync.Cleanup();
                Patches.FlightGamePatches.ClearCache();

                string mapSceneName = Lobby?.MapName;
                string runtimeMapName = AirfieldHelper.GetCurrentMapName();
                if (string.Equals(runtimeMapName, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    runtimeMapName = null;
                }

                string preferredMapSceneName = !string.IsNullOrWhiteSpace(runtimeMapName)
                    ? runtimeMapName
                    : mapSceneName;
                EnsureSafeActiveSceneBeforeUnload(preferredMapSceneName);

                await UnloadAllScenesByName("FlightGame");
                await UnloadAllScenesByName(preferredMapSceneName);
                if (!string.IsNullOrWhiteSpace(mapSceneName) &&
                    !string.Equals(mapSceneName, preferredMapSceneName, StringComparison.OrdinalIgnoreCase))
                {
                    await UnloadAllScenesByName(mapSceneName);
                }

                Lobby?.ResetForNewGame();
                AirfieldHelper.ClearCache();
                _isSpawningLocal = false;

                if (GameState != null)
                {
                    var targetState = GameState.IsHost
                        ? Networking.GameState.HostingLobby
                        : Networking.GameState.ClientLobby;

                    if (GameState.CurrentState != targetState)
                    {
                        bool transitioned = GameState.TransitionTo(targetState);
                        if (!transitioned)
                        {
                            Log?.LogWarning($"[Plugin] Failed to transition back to lobby state from {GameState.CurrentState}");
                        }
                    }
                }

                // Host should immediately broadcast fresh lobby state after reset.
                if (Lobby?.IsHost == true)
                {
                    Lobby.BroadcastLobbyState();
                }

                // Re-open multiplayer lobby UI so players can launch the next sortie.
                if (MultiplayerMenu.Instance == null)
                {
                    MultiplayerMenu.CreateAndRun().Forget();
                }
                else
                {
                    MultiplayerMenu.Instance.SetScreen(LobbyScreen.Lobby);
                }
            }
            catch (Exception ex)
            {
                Log?.LogError($"[Plugin] ReturnToLobbySequence error: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                _isReturningToLobby = false;
            }
        }

        private void EnsureSafeActiveSceneBeforeUnload(string mapSceneName)
        {
            Scene active = SceneManager.GetActiveScene();
            if (!active.IsValid() || !active.isLoaded) return;

            bool isFlightOrMapScene = string.Equals(active.name, "FlightGame", StringComparison.OrdinalIgnoreCase) ||
                                      (!string.IsNullOrWhiteSpace(mapSceneName) &&
                                       string.Equals(active.name, mapSceneName, StringComparison.OrdinalIgnoreCase));
            if (!isFlightOrMapScene) return;

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene candidate = SceneManager.GetSceneAt(i);
                if (!candidate.IsValid() || !candidate.isLoaded) continue;
                if (string.Equals(candidate.name, "FlightGame", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(mapSceneName) &&
                    string.Equals(candidate.name, mapSceneName, StringComparison.OrdinalIgnoreCase)) continue;

                SceneManager.SetActiveScene(candidate);
                return;
            }
        }

        private static async UniTask UnloadAllScenesByName(string sceneName)
        {
            if (string.IsNullOrWhiteSpace(sceneName)) return;

            int unloadedCount = 0;
            int safety = 0;
            while (safety++ < 32)
            {
                Scene sceneToUnload = default;
                bool found = false;
                for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
                {
                    Scene candidate = SceneManager.GetSceneAt(i);
                    if (!candidate.IsValid() || !candidate.isLoaded) continue;
                    if (!string.Equals(candidate.name, sceneName, StringComparison.OrdinalIgnoreCase)) continue;

                    sceneToUnload = candidate;
                    found = true;
                    break;
                }

                if (!found)
                {
                    break;
                }

                AsyncOperation unloadOp = SceneManager.UnloadSceneAsync(sceneToUnload);
                if (unloadOp == null)
                {
                    break;
                }

                await unloadOp.ToUniTask();
                unloadedCount++;
            }

            if (unloadedCount > 0)
            {
                Log?.LogInfo($"[Plugin] Unloaded {unloadedCount} instance(s) of scene '{sceneName}'");
            }
        }

        private void BindLoggingConfig()
        {
            ConfigUsername = Config.Bind("Player", "Username", "Player", "Your multiplayer username.");
            ConfigLastIP = Config.Bind("Network", "LastIP", "127.0.0.1", "Last connected IP address.");
            ConfigLastPort = Config.Bind("Network", "LastPort", NetworkConfig.DEFAULT_PORT_STRING, "Last connected port.");
            ConfigHostName = Config.Bind("Host", "ServerName", "TCA Server", "Your server's name.");
            ConfigHostPort = Config.Bind("Host", "Port", NetworkConfig.DEFAULT_PORT_STRING, "Port to host on.");
            ConfigHostSpawnType = Config.Bind("Host", "SpawnType", 1, "0=Air, 1=Runway, 2=Ramp");
            ConfigHostTimeOfDay = Config.Bind("Host", "TimeOfDay", 1, "0=Dawn, 1=Morning, 2=Noon, 3=Afternoon, 4=Evening, 5=Night");
            ConfigHostAircraftCollisions = Config.Bind("Host", "AircraftCollisions", true, "Enable aircraft collisions");
            
            ConfigLocalAircraft = Config.Bind("Player", "LastAircraft", "AV8B", "Last selected aircraft ID");
            ConfigLocalLoadout = Config.Bind("Player", "LastLoadout", "Clean", "Last selected loadout name");
            ConfigLocalAirfield = Config.Bind("Player", "LastAirfield", "", "Last selected airfield name");

            VerboseAll = Config.Bind("Logging", "VerboseAll", true, "Enable verbose logs across the mod.");
            VerboseNetworking = Config.Bind("Logging", "VerboseNetworking", true, "Extra logs for network state handling.");
            VerboseTransport = Config.Bind("Logging", "VerboseTransport", true, "Extra logs for transport send/receive.");
            VerbosePackets = Config.Bind("Logging", "VerbosePackets", true, "Extra logs for packet send/receive details.");
            VerbosePatches = Config.Bind("Logging", "VerbosePatches", true, "Extra logs for Harmony patch flows.");
            VerbosePlayer = Config.Bind("Logging", "VerbosePlayer", true, "Extra logs for remote player visuals.");
            VerboseDamage = Config.Bind("Logging", "VerboseDamage", true, "Extra logs for damage flows.");
            VerboseWeapons = Config.Bind("Logging", "VerboseWeapons", true, "Extra logs for weapon/missile flows.");
            VerboseInterpolation = Config.Bind("Logging", "VerboseInterpolation", true, "Extra logs for interpolation/extrapolation.");
            VerboseReflection = Config.Bind("Logging", "VerboseReflection", true, "Extra logs for reflection scans.");

            LogIntervalSeconds = Config.Bind("Logging", "LogIntervalSeconds", 1.0f, "Throttle interval (seconds) for periodic logs.");
            PacketLogSampleRate = Config.Bind("Logging", "PacketLogSampleRate", 60, "Log every Nth packet send/receive.");
            HighFreqLogSampleRate = Config.Bind("Logging", "HighFreqLogSampleRate", 30, "Log every Nth high-frequency update.");
        }
    }

    public class PluginRunner : MonoBehaviour
    {
        private int _updateCount = 0;
        private float _lastLogTime = 0f;

        // GUI style with rich text support (used by status HUD)
        private GUIStyle _richTextStyle;
        private void Start()
        {
            Plugin.Log?.LogInfo("[PluginRunner] Started!");
        }

        private void Update()
        {
            _updateCount++;

            // Periodic logging
            if (Time.unscaledTime - _lastLogTime > 5f)
            {
                _lastLogTime = Time.unscaledTime;
                var net = Plugin.Instance?.Network;
                Plugin.Log?.LogInfo($"[PluginRunner] Updates:{_updateCount} Sent:{net?.PacketsSent ?? 0} Recv:{net?.PacketsReceived ?? 0}");
            }

            // Multiplayer must not freeze when local pause is opened.
            // Keep gameplay simulation running while we are in active game/respawn states.
            // We must also call GamePause.ResumeGame() because the native camera system
            // checks GamePause.IsPaused (not Time.timeScale) and stops updating — causing
            // the 3rd-person camera to freeze while the game world continues.
            if (Plugin.Instance?.GameState?.IsInGame == true)
            {
                if (Falcon.GamePause.IsPaused)
                {
                    Falcon.GamePause.ResumeGame();
                }

                if (Mathf.Abs(Time.timeScale - 1f) > 0.001f)
                {
                    Time.timeScale = 1f;
                }

                if (AudioListener.pause)
                {
                    AudioListener.pause = false;
                }
            }

            Plugin.Instance?.Network?.Update();
            Plugin.Instance?.Discovery?.Update();
            Plugin.Instance?.Lobby?.Update();

            // Periodic cleanup to prevent memory leaks
            Player.RealCombatSync.PeriodicCleanup();
            Plugin.Instance?.GameState?.Update();
            Plugin.Instance?.ScoreboardHUD?.Update();
        }

        private void LateUpdate()
        {
            Plugin.Instance?.Network?.LateUpdate();
        }

        private void FixedUpdate()
        {
            Plugin.Instance?.Network?.FixedUpdate();
        }

        private void OnGUI()
        {
            try
            {
                // Initialize rich text style once
                if (_richTextStyle == null)
                {
                    _richTextStyle = new GUIStyle(GUI.skin.label);
                    _richTextStyle.richText = true;
                }

                var gameState = Plugin.Instance?.GameState;
                var lobby = Plugin.Instance?.Lobby;

                // Corner status indicator
                string statusText = "Disconnected";
                string statusColor = "white";

                if (gameState != null)
                {
                    switch (gameState.CurrentState)
                    {
                        case Networking.GameState.HostingLobby:
                            statusText = $"Hosting ({lobby?.PlayerCount ?? 0} players)";
                            statusColor = "lime";
                            break;
                        case Networking.GameState.ClientLobby:
                            statusText = "In Lobby";
                            statusColor = "cyan";
                            break;
                        case Networking.GameState.Connecting:
                            statusText = "Connecting...";
                            statusColor = "yellow";
                            break;
                        case Networking.GameState.Loading:
                        case Networking.GameState.WaitingForPlayers:
                            statusText = "Loading...";
                            statusColor = "yellow";
                            break;
                        case Networking.GameState.Spawning:
                            statusText = "Spawning...";
                            statusColor = "yellow";
                            break;
                        case Networking.GameState.InGame:
                            statusText = "In Game";
                            statusColor = "lime";
                            break;
                        case Networking.GameState.Respawning:
                            statusText = "Respawning";
                            statusColor = "orange";
                            break;
                    }
                }

                GUI.Label(new Rect(Screen.width - 250, 10, 240, 20),
                    $"<color={statusColor}>TCA MP - {statusText}</color>", _richTextStyle);

                // Draw scoreboard HUD (kill counter, kill feed, TAB scoreboard)
                Plugin.Instance?.ScoreboardHUD?.OnGUI();
            }
            catch (Exception ex)
            {
                GUI.Label(new Rect(10, 10, 400, 50), $"UI Error: {ex.Message}");
            }
        }
    }

    public static class PluginInfo
    {
        public const string GUID = "com.modder.tcamultiplayer";
        public const string NAME = "TCA Multiplayer";
        public const string VERSION = "0.2.2";
    }
}
