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

        public NetworkManager Network { get; private set; }
        public GameStateMachine GameState { get; private set; }

        // Lobby system components
        public LobbyManager Lobby { get; private set; }
        public LanDiscovery Discovery { get; private set; }
        public LobbyUI LobbyUI { get; private set; }
        public SpawnManager Spawner { get; private set; }

        private Harmony _harmony;
        private GameObject _runnerObject;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
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
                LobbyUI = new LobbyUI();
                Spawner = new SpawnManager();

                // Initialize lobby UI with references
                LobbyUI.Initialize(Lobby, Discovery);

                // Wire up lobby events
                SetupLobbyEvents();

                Log.LogInfo("Applying Harmony patches...");
                _harmony = new Harmony(PluginInfo.GUID);

                try
                {
                    _harmony.PatchAll();
                    Log.LogInfo("Harmony patches applied successfully");
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

                Log.LogInfo("===========================================");
                Log.LogInfo("TCA Multiplayer loaded successfully!");
                Log.LogInfo("Press F8 to toggle multiplayer menu");
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
            // LobbyUI -> Network/Lobby
            LobbyUI.OnHostGame += (hostName, port) =>
            {
                Log.LogInfo($"[Plugin] Hosting game: {hostName} on port {port}");
                GameState?.StartHosting(port, hostName);
                Network?.StartHost(port);
                Lobby?.CreateLobby(Network.LocalPeerId, hostName);
                Discovery?.StartBroadcasting(hostName, port, "ActionIsland", 1, 8);
            };

            LobbyUI.OnJoinGame += (ip, port) =>
            {
                Log.LogInfo($"[Plugin] Joining game: {ip}:{port}");
                GameState?.StartConnecting(ip, port);
                Network?.StartClient(ip, port);
            };

            LobbyUI.OnLeaveLobby += () =>
            {
                Log.LogInfo("[Plugin] Leaving lobby");
                GameState?.Disconnect();
                Network?.Disconnect();
                Discovery?.StopBroadcasting();
                Discovery?.StopListening();
                Lobby?.LeaveLobby();
            };

            LobbyUI.OnReadyToggle += () =>
            {
                bool newReady = !Lobby.LocalIsReady;
                Lobby?.SetLocalReady(newReady);
                Lobby?.SendPlayerReady(newReady);
            };

            LobbyUI.OnAirfieldSelected += (airfieldName) =>
            {
                Lobby?.SetLocalAirfield(airfieldName);
                Lobby?.SendAirfieldSelect(airfieldName);
            };

            LobbyUI.OnSpawnTypeChanged += (spawnType) =>
            {
                // SetSpawnSettings already sends the packet via LobbyManager
                Lobby?.SetSpawnSettings(spawnType);
            };

            LobbyUI.OnStartGame += () =>
            {
                Log.LogInfo("[Plugin] Starting game");
                Lobby?.StartGame();
                Lobby?.SendStartGame(Lobby.MapName, Lobby.SpawnType);
            };

            LobbyUI.OnRespawnRequest += () =>
            {
                Log.LogInfo("[Plugin] Respawn requested");
                Lobby?.SendRespawnRequest();
                // Respawn will be handled by SpawnManager
                Spawner?.Respawn();
                LobbyUI?.HideRespawnScreen();
            };

            // Lobby events -> UI/Network
            Lobby.OnGameStarting += async () =>
            {
                Log.LogInfo("[Plugin] Game starting - loading scenes...");
                GameState?.StartLoading();
                LobbyUI?.SetScreen(LobbyScreen.Loading);

                try
                {
                    // 1. Load FlightGame scene (Core game logic)
                    Log.LogInfo("[Plugin] Loading FlightGame scene...");
                    await SceneManager.LoadSceneAsync("FlightGame", LoadSceneMode.Additive);

                    // 2. Load Map
                    string mapName = Lobby.MapName;
                    Log.LogInfo($"[Plugin] Loading Map: {mapName}...");

                    // Try to get map data to find correct scene name
                    string sceneName = mapName;
                    /*
                    try
                    {
                        var mapData = GameDataMaps.GetByName(mapName);
                        if (mapData != null) sceneName = mapData.SceneName;
                    }
                    catch (Exception ex)
                    {
                        Log.LogWarning($"[Plugin] Could not get map data for {mapName}, using name as scene: {ex.Message}");
                    }
                    */

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
                        return; // Exit early - can't proceed without FlightGame
                    }

                    Log.LogInfo("[Plugin] FlightGame initialized successfully");

                    // Notify ready - OnLoadingComplete first to set state before SetLocalLoaded triggers OnAllPlayersLoaded
                    GameState?.OnLoadingComplete();
                    Lobby?.SetLocalLoaded();
                    Lobby?.SendLoadingComplete();
                }
                catch (Exception ex)
                {
                    Log.LogError($"[Plugin] Loading error: {ex.Message}\n{ex.StackTrace}");
                }
            };

            Lobby.OnAllPlayersLoaded += () =>
            {
                GameState?.StartSpawning();
                if (Lobby.IsHost)
                {
                    Log.LogInfo("[Plugin] All players loaded - sending spawn signal");
                    Lobby?.SendSpawnPlayers();
                    DoSpawn();
                }
            };

            Lobby.OnSpawnPlayers += () =>
            {
                Log.LogInfo("[Plugin] Spawn signal received");
                GameState?.StartSpawning();
                DoSpawn();
            };

            // SpawnManager events
            Spawner.OnPlayerDied += () =>
            {
                LobbyUI?.ShowRespawnScreen();
            };
        }

        /// <summary>
        /// Execute player spawn
        /// </summary>
        private void DoSpawn()
        {
            string airfield = Lobby?.LocalSelectedAirfield;
            if (string.IsNullOrEmpty(airfield))
            {
                var airfields = AirfieldHelper.GetAirfieldNames();
                if (airfields.Length > 0) airfield = airfields[0];
            }

            Log.LogInfo($"[Plugin] Spawning at {airfield} with type {Lobby?.SpawnType}");
            Spawner?.SpawnPlayerAtAirfield(airfield, "AV8B", Lobby?.SpawnType ?? LobbySpawnType.Runway);
            LobbyUI?.SetScreen(LobbyScreen.InGame);
            GameState?.OnSpawnComplete();
            LobbyUI?.Hide();
        }

        private void BindLoggingConfig()
        {
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
        private bool _showDebugUI = false;
        private int _updateCount = 0;
        private float _lastLogTime = 0f;
        private float _lastAirfieldRefresh = 0f;

        // GUI style with rich text support
        private GUIStyle _richTextStyle;
        private GUIStyle _richTextStyleBold;

        private void Start()
        {
            Plugin.Log?.LogInfo("[PluginRunner] Started!");

            // Initialize lobby UI - Don't show IMGUI by default, use MultiplayerMenu
            // Plugin.Instance?.LobbyUI?.Show();
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

            // Refresh airfield list periodically when in lobby
            if (Time.unscaledTime - _lastAirfieldRefresh > 2f)
            {
                _lastAirfieldRefresh = Time.unscaledTime;
                if (Plugin.Instance?.LobbyUI?.CurrentScreen == LobbyScreen.Lobby)
                {
                    var airfields = AirfieldHelper.GetAirfieldNames();
                    if (airfields.Length > 0)
                    {
                        Plugin.Instance.LobbyUI.SetAirfields(airfields);
                    }
                }
            }

            try
            {
                if (Input.GetKeyDown(KeyCode.F7))
                {
                    _showDebugUI = !_showDebugUI;
                    Plugin.Log?.LogInfo($"Debug UI toggled: {(_showDebugUI ? "Open" : "Closed")}");
                }
            }
            catch { }

            Plugin.Instance?.Network?.Update();
            Plugin.Instance?.Discovery?.Update();
            Plugin.Instance?.Lobby?.Update();

            // Periodic cleanup to prevent memory leaks
            Player.RealCombatSync.PeriodicCleanup();
            Plugin.Instance?.GameState?.Update();
        }

        private void OnGUI()
        {
            try
            {
                // Initialize rich text styles once
                if (_richTextStyle == null)
                {
                    _richTextStyle = new GUIStyle(GUI.skin.label);
                    _richTextStyle.richText = true;

                    _richTextStyleBold = new GUIStyle(GUI.skin.label);
                    _richTextStyleBold.richText = true;
                    _richTextStyleBold.fontStyle = FontStyle.Bold;
                }

                var gameState = Plugin.Instance?.GameState;
                var lobby = Plugin.Instance?.Lobby;

                // Corner indicator
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
                    $"<color={statusColor}>TCA MP v{PluginInfo.VERSION} - {statusText}</color>", _richTextStyle);
                GUI.Label(new Rect(Screen.width - 250, 30, 240, 20),
                    "<color=#888888>F8: Menu | F7: Debug</color>", _richTextStyle);

                if (_showDebugUI)
                {
                    DrawDebugPanel();
                }
            }
            catch (Exception ex)
            {
                GUI.Label(new Rect(10, 10, 400, 50), $"UI Error: {ex.Message}");
            }
        }

        private void DrawDebugPanel()
        {
            var network = Plugin.Instance?.Network;
            var lobby = Plugin.Instance?.Lobby;

            // Debug panel in corner
            GUI.Box(new Rect(10, 10, 300, 350), "");
            GUILayout.BeginArea(new Rect(20, 20, 280, 330));

            GUILayout.Label($"<b>Debug Panel</b>", _richTextStyle);
            GUILayout.Space(5);

            // Connection status
            GUILayout.Label($"<b>State:</b> {Plugin.Instance?.GameState?.CurrentState}", _richTextStyle);
            GUILayout.Label($"<b>Transport:</b> {network?.CurrentTransportName ?? "None"}", _richTextStyle);

            if (lobby?.IsInLobby == true)
            {
                GUILayout.Label($"<b>Lobby:</b> {(lobby.IsHost ? "Host" : "Client")}", _richTextStyle);
                GUILayout.Label($"<b>Players:</b> {lobby.PlayerCount}", _richTextStyle);
                GUILayout.Label($"<b>All Ready:</b> {lobby.AreAllPlayersReady}", _richTextStyle);
            }

            GUILayout.Space(10);
            GUILayout.Label("<b>=== Network Stats ===</b>", _richTextStyle);
            GUILayout.Label($"Packets Sent: {network?.PacketsSent ?? 0}");
            GUILayout.Label($"Packets Received: {network?.PacketsReceived ?? 0}");

            GUILayout.Space(5);
            GUILayout.Label($"States Sent: {network?.AircraftStatesSent ?? 0}");
            GUILayout.Label($"States Received: {network?.AircraftStatesReceived ?? 0}");
            GUILayout.Label($"Remote Aircraft: {(network?.HasRemoteAircraft == true ? "<color=lime>YES</color>" : "<color=red>NO</color>")}", _richTextStyle);

            if (network?.HasRemoteAircraft == true)
            {
                var remotePos = network.LastRemotePosition;
                GUILayout.Label($"Remote: ({remotePos.x:F0}, {remotePos.y:F0}, {remotePos.z:F0})");
                GUILayout.Label($"Distance: {network.DistanceToRemote:F0}m");
            }

            GUILayout.Space(10);
            GUILayout.Label($"<b>Update Count:</b> {_updateCount}", _richTextStyle);

            // FloatingOrigin debug
            try
            {
                bool isAvail = Networking.FloatingOriginHelper.IsAvailable;
                GUILayout.Label($"FloatOrigin: {(isAvail ? "<color=lime>OK</color>" : "<color=red>N/A</color>")}", _richTextStyle);
            }
            catch
            {
                GUILayout.Label("FloatOrigin: <color=red>ERROR</color>", _richTextStyle);
            }

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close Debug")) _showDebugUI = false;

            GUILayout.EndArea();
        }
    }

    public static class PluginInfo
    {
        public const string GUID = "com.modder.tcamultiplayer";
        public const string NAME = "TCA Multiplayer";
        public const string VERSION = "0.2.2";
    }
}
