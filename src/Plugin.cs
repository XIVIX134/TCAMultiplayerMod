using BepInEx;
using Cysharp.Threading.Tasks;
using Falcon.Constants;
using Falcon.UI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Falcon.UniversalAircraft;
using TCAMultiplayer.Core;
using TCAMultiplayer.Transport;
using TCAMultiplayer.Game;
using TCAMultiplayer.Combat;
using TCAMultiplayer.Patches;
using TCAMultiplayer.Sync;
using System.Reflection;
using System;
using TCAMultiplayer.Protocol;
using TCAMultiplayer.UI;
using TCAMultiplayer.Compatibility;
using TCAMultiplayer.Updating;

namespace TCAMultiplayer
{
    /// <summary>
    /// BepInEx plugin entry point. ONLY bootstraps and wires components.
    /// No business logic — just creation and disposal.
    /// </summary>
    [BepInPlugin(PluginMetadata.Guid, PluginMetadata.Name, PluginMetadata.Version)]
    public class Plugin : BaseUnityPlugin
    {
        private const string Tag = "PLUGIN";
        private Harmony _harmony;
        private PluginRunner _runner;

        private void Awake()
        {
            Log.Init(Logger);
            ModConfig.Bind(Config);
            Log.Info(Tag, $"TCAMP v{PluginMetadata.Version} initializing...");

            _harmony = new Harmony("com.tcamp.mod");
            _harmony.PatchAll(typeof(Plugin).Assembly);
            Log.Info(Tag, "Harmony patches applied");

            var runnerGo = new GameObject("TCAMP_Runner");
            DontDestroyOnLoad(runnerGo);
            _runner = runnerGo.AddComponent<PluginRunner>();

            Log.Info(Tag, "TCAMP initialized");
        }

        private void OnDestroy()
        {
            _runner?.Shutdown();
            _harmony?.UnpatchSelf();
            Log.Info(Tag, "TCAMP shutdown");
        }
    }

    /// <summary>
    /// Persistent MonoBehaviour that drives the entire multiplayer Update loop.
    /// Created by Plugin, survives scene loads via DontDestroyOnLoad.
    /// </summary>
    public class PluginRunner : MonoBehaviour
    {
        private const string Tag = "RUNNER";

        // ── Always-alive infrastructure ──────────────────────────────────
        private ITransport _transport;
        private ConnectionManager _connection;
        private FloatingOriginService _originService;
        private AircraftSpawner _aircraftSpawner;
        private GameEventBridge _eventBridge;

        // ── Session-scoped (created in OnSessionCreated, disposed in TeardownSession) ──
        private GameSession _activeSession;
        private LobbyManager _lobby;
        private SpawnManager _spawnManager;
        private RespawnManager _respawnManager;
        private ScoreTracker _scoreTracker;
        private DeathEventCoordinator _deathCoordinator;
        private RemoteAircraftManager _remoteManager;
        private HostPacketRelay _hostRelay;
        private LocalAircraftStateReader _stateReader;
        private ModManifestCollector _modManifest;
        private bool _pendingManifestSend;
        private string _loadedMapSceneName;
        private LoadingScreen _loadingScreen;
        private float _lastDiagnosticsLogTime;

        // Combat sync
        private RadarSyncSystem _radarSync;
        private MissileSyncSystem _missileSync;
        private BombSyncSystem _bombSync;
        private GunSyncSystem _gunSync;
        private DamageSyncSystem _damageSync;
        private ExplosionSyncSystem _explosionSync;
        private CollisionSyncManager _collisionSync;

        // UI
        private MultiplayerMenu _menu;
        private ScoreboardHUD _scoreboard;
        private RespawnScreen _respawnScreen;
        private Falcon.RewiredInputActionList _pauseMenuInput;
        private bool _mpPauseMenuOpen;

        private Falcon.Game2.MainMenu _nativeMainMenu;
        private bool _suppressNativeMainMenuRestore;
        private bool _tearingDown;
        private readonly object _updaterLock = new object();
        private ModUpdater _updater;
        private string _updaterStatusMessage = "";
        private bool _updaterStatusDirty;
        private ModUpdateAvailable _pendingUpdateAvailable;
        private ModUpdateResult _pendingUpdateNotice;
        private bool _updateAvailablePromptShown;
        private bool _updateNoticeShown;
        private LiveTestOptions _liveTest;
        private bool _liveSessionStarted;
        private bool _liveDefaultsApplied;
        private bool _liveReadySet;
        private bool _liveStartRequested;
        private float _liveNextActionTime;
        private float _liveNextGearToggleTime;
        private float _liveNextLockToggleTime;
        private bool _liveLockEngaged;

        // ── Startup ─────────────────────────────────────────────────────

        private void Start()
        {
            _liveTest = LiveTestOptions.FromCommandLine();

            var config = new TransportConfig
            {
                MaxConnections = GameSession.ClampMaxPlayersTotal(ModConfig.HostMaxPlayersTotal?.Value ?? 8) - 1,
                LocalBindAddress = ModConfig.LocalBindAddress?.Value ?? "",
                AutoVpnBind = true,
                ModVersion = PluginMetadata.Version
            };
            ApplyBandwidthPreset(config);
            _transport = new DirectUdpTransport(config);

            _connection = new ConnectionManager(_transport, config);
            _connection.OnSessionCreated += OnSessionCreated;
            _connection.OnSessionEnded += OnSessionEnded;
            _originService = new FloatingOriginService();
            _aircraftSpawner = new AircraftSpawner(_originService);
            _eventBridge = new GameEventBridge();

            // UI (always available — independent of session)
            _menu = gameObject.AddComponent<MultiplayerMenu>();
            _menu.Init(_connection);
            _menu.SetExternalStatusProvider(() => _updaterStatusMessage);
            _menu.OnMenuClosed = () =>
            {
                if (_suppressNativeMainMenuRestore)
                    return;

                // Restore native main menu buttons (reverse of ShowMainMenuUI(false))
                SetNativeMainMenuUIVisible(true);
            };
            _scoreboard = gameObject.AddComponent<ScoreboardHUD>();
            _respawnScreen = gameObject.AddComponent<RespawnScreen>();
            StartUpdater();

            // Wire main menu button → multiplayer menu + hide native main menu UI
            MainMenuPatch.OnMultiplayerClicked = mainMenu =>
            {
                _nativeMainMenu = mainMenu;
                // Use the game's own method to hide buttons while keeping 3D background cameras
                // This matches how GameLogic hides MainMenu for QMB/Settings/Mods
                SetNativeMainMenuUIVisible(false);
                _menu.ToggleMenu();
            };

            Log.Info(Tag, "PluginRunner started — press F8 for multiplayer menu");
            if (_liveTest.Enabled)
            {
                _liveNextActionTime = Time.time + _liveTest.StartDelaySeconds;
                Log.Info("LIVE-TEST",
                    $"Enabled: role={_liveTest.Role}, autostart={_liveTest.AutoStart}, ready={_liveTest.Ready}, " +
                    $"startGame={_liveTest.StartGame}, address={_liveTest.Address}, port={_liveTest.Port}");
            }
        }

        // ── Update (called every frame) ─────────────────────────────────

        private float _lastStateSendTime;
        private float _stateSendAccumulator;
        private float _adaptiveStateSendRateHz = DefaultStateSendRateHz;
        private float _nextNetworkQualityUpdateTime;
        private long _lastReliableRetransmits;
        private long _lastReliableDrops;
        private const float DefaultStateSendRateHz = 60f;
        private const float MinStateSendRateHz = 10f;
        private const float MaxStateSendRateHz = 120f;
        private const float LowBandwidthStateSendRateHz = 20f;
        private const float PoorQualityStateSendRateHz = 30f;
        private const float BadQualityStateSendRateHz = 20f;

        private void Update()
        {
            ProcessUpdaterUi();
            _connection?.Update(Time.deltaTime);
            DriveLiveTest();
            DriveLiveGearCycle();
            DriveLiveLockCycle();

            if (_activeSession == null) return;

            if (_pendingManifestSend && !_activeSession.IsHost && _connection.IsConnected && _activeSession.LocalPeerId != 0)
            {
                _pendingManifestSend = false;
                _modManifest?.SendManifest();
            }

            _modManifest?.Update(Time.deltaTime);
            _lobby?.Update();
            _respawnManager?.Update();

            var localAircraft = _spawnManager?.LocalAircraft;

            _radarSync?.Update();
            _missileSync?.Update();
            _bombSync?.Update();
            LogSessionDiagnostics(localAircraft);
            UpdateAdaptiveNetworkQuality();

            if (localAircraft != null)
            {
                _gunSync?.Update(localAircraft);
                _collisionSync?.Update(localAircraft);
            }
        }

        private bool ShouldSendAircraftState(float deltaTime)
        {
            bool lowBandwidth = ModConfig.LowBandwidthMode?.Value ?? false;
            float configuredRate = ModConfig.StateSendRateHz?.Value ?? DefaultStateSendRateHz;
            float rateHz = Mathf.Min(configuredRate, _adaptiveStateSendRateHz);
            if (lowBandwidth)
                rateHz = Mathf.Min(rateHz, LowBandwidthStateSendRateHz);
            rateHz = Mathf.Clamp(rateHz, MinStateSendRateHz, MaxStateSendRateHz);
            float interval = 1f / rateHz;

            if (_lastStateSendTime <= 0f)
            {
                _stateSendAccumulator = 0f;
                return true;
            }

            _stateSendAccumulator += Mathf.Min(deltaTime, 0.25f);
            if (_stateSendAccumulator < interval)
                return false;

            _stateSendAccumulator %= interval;
            return true;
        }

        private void StartUpdater()
        {
            if (ModConfig.CheckForUpdatesOnLaunch?.Value != true)
            {
                Log.Info("UPDATER", "Update check disabled by config");
                return;
            }

            _updater = new ModUpdater(new UpdateSettings
            {
                LatestReleaseApiUrl = ModConfig.UpdateApiUrl?.Value,
                CurrentVersion = PluginMetadata.Version,
                CurrentPluginPath = Assembly.GetExecutingAssembly().Location,
                TimeoutMilliseconds = 15000
            });
            _updater.OnStatusChanged += HandleUpdaterStatusChanged;
            _updater.OnUpdateAvailable += HandleUpdateAvailable;
            _updater.OnUpdateReady += HandleUpdateReady;
            _updater.CheckForUpdatesAsync().Forget();
        }

        private void HandleUpdaterStatusChanged(string message)
        {
            lock (_updaterLock)
            {
                _updaterStatusMessage = message ?? "";
                _updaterStatusDirty = true;
            }
        }

        private void HandleUpdateAvailable(ModUpdateAvailable update)
        {
            lock (_updaterLock)
            {
                _pendingUpdateAvailable = update;
                _updaterStatusMessage = update != null
                    ? $"TCAMP {update.TagName} is available"
                    : "";
                _updaterStatusDirty = true;
            }
        }

        private void HandleUpdateReady(ModUpdateResult result)
        {
            lock (_updaterLock)
            {
                _pendingUpdateNotice = result;
                _updaterStatusMessage = result?.Message ?? "";
                _updaterStatusDirty = true;
            }
        }

        private void ProcessUpdaterUi()
        {
            bool refreshMenu;
            ModUpdateAvailable available;
            ModUpdateResult notice;
            lock (_updaterLock)
            {
                refreshMenu = _updaterStatusDirty;
                _updaterStatusDirty = false;
                available = _pendingUpdateAvailable;
                notice = _pendingUpdateNotice;
            }

            if (refreshMenu)
                _menu?.RefreshIfVisible();

            if (!_updateAvailablePromptShown && available != null && UIFactory.HasPrefabs && _activeSession == null)
            {
                _updateAvailablePromptShown = true;
                string current = string.IsNullOrWhiteSpace(available.CurrentVersion)
                    ? PluginMetadata.Version
                    : available.CurrentVersion;
                string intro = available.IsNewerVersion
                    ? "There is a newer version of TCAMP available."
                    : "There is an updated TCAMP build available.";
                string availableMessage =
                    $"{intro}\n\nCurrent: v{current}\nLatest: {available.TagName}\n\nUpdate now?";
                UIFactory.ShowConfirmDialog(
                    "TCAMP UPDATE AVAILABLE",
                    availableMessage,
                    "UPDATE NOW",
                    "NOT NOW",
                    () => _updater?.DownloadAndStageUpdateAsync(available).Forget(),
                    () => SetUpdaterStatus("Update skipped"));
                return;
            }

            if (_updateNoticeShown || notice == null || !UIFactory.HasPrefabs || _activeSession != null)
                return;

            _updateNoticeShown = true;
            string message = $"TCAMP {notice.TagName} downloaded and verified."
                + "\n\nRestart now to apply it. The game will close, replace the plugin, and reopen automatically.";
            UIFactory.ShowConfirmDialog(
                "TCAMP UPDATE READY",
                message,
                "RESTART NOW",
                "LATER",
                () => RestartForUpdate(notice),
                () => SetUpdaterStatus("Update staged; restart later to apply"),
                destructive: true);
        }

        private void RestartForUpdate(ModUpdateResult notice)
        {
            if (_updater?.TryApplyStagedUpdateAndRestart(notice) == true)
                Application.Quit();
        }

        private void SetUpdaterStatus(string message)
        {
            lock (_updaterLock)
            {
                _updaterStatusMessage = message ?? "";
                _updaterStatusDirty = true;
            }
        }

        private void DriveLiveTest()
        {
            if (_liveTest == null || !_liveTest.Enabled || Time.time < _liveNextActionTime) return;

            if (_liveTest.AutoStart && !_liveSessionStarted)
            {
                StartLiveSession();
                return;
            }

            if (_activeSession == null || _lobby == null) return;

            if (!_liveDefaultsApplied)
            {
                _liveDefaultsApplied = ApplyLiveTestDefaults();
                if (_liveDefaultsApplied)
                    _liveNextActionTime = Time.time + 0.5f;
                return;
            }

            if (_liveTest.Ready && !_liveReadySet)
            {
                var local = _activeSession.GetLocalPlayer();
                if (local == null) return;

                if (!local.IsReady)
                    _lobby.ToggleReady();

                _liveReadySet = true;
                _liveNextActionTime = Time.time + 0.5f;
                Log.Info("LIVE-TEST", "Local player marked ready");
                return;
            }

            if (_liveTest.StartGame && _activeSession.IsHost && !_liveStartRequested)
            {
                if (_activeSession.StateMachine.CurrentState != GameState.HostingLobby) return;
                if (!RemotePlayersReady()) return;

                _lobby.StartGame();
                _liveStartRequested = true;
                Log.Info("LIVE-TEST", "Requested game start");
            }
        }

        /// <summary>
        /// Live-test helper: periodically toggles the local plane's landing gear so a
        /// 2-instance auto-flight test exercises the gear sync chain end-to-end.
        /// Enabled with --tca-live-gear-cycle=&lt;seconds&gt;.
        /// </summary>
        private void DriveLiveGearCycle()
        {
            if (_liveTest == null || !_liveTest.Enabled || _liveTest.GearCycleSeconds <= 0f)
                return;
            if (_activeSession == null)
                return;

            var player = UniAircraft.Player;
            if (player == null || player.LandingGear == null)
                return;

            if (_liveNextGearToggleTime <= 0f)
            {
                // First toggle one full cycle after entering flight.
                _liveNextGearToggleTime = Time.time + _liveTest.GearCycleSeconds;
                return;
            }

            if (Time.time < _liveNextGearToggleTime)
                return;

            _liveNextGearToggleTime = Time.time + _liveTest.GearCycleSeconds;
            try
            {
                player.LandingGear.ToggleGear();
                Log.Info("LIVE-TEST", $"Toggled local gear -> lowered={player.LandingGear.IsGearLowered}");
            }
            catch (Exception ex)
            {
                Log.Warning("LIVE-TEST", $"Gear toggle failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Live-test helper: alternately locks/unlocks the local radar onto the first
        /// remote clone, so a 2-instance test exercises radar lock + RWR warning sync.
        /// Enabled with --tca-live-lock-cycle=&lt;seconds&gt;.
        /// </summary>
        private void DriveLiveLockCycle()
        {
            if (_liveTest == null || !_liveTest.Enabled || _liveTest.LockCycleSeconds <= 0f)
                return;
            if (_activeSession == null || _remoteManager == null)
                return;

            var player = UniAircraft.Player;
            if (player == null || player.Radar == null)
                return;

            if (_liveNextLockToggleTime <= 0f)
            {
                // First action one full cycle after entering flight.
                _liveNextLockToggleTime = Time.time + _liveTest.LockCycleSeconds;
                return;
            }

            try
            {
                // While engaged, re-assert the lock if the native radar dropped it
                // (target died/respawned). Keeps the victim's RWR warning observable.
                if (_liveLockEngaged && player.Radar.LockedTarget == null)
                    TryLiveLockFirstRemote(player, "re-assert");

                if (Time.time < _liveNextLockToggleTime)
                    return;

                _liveNextLockToggleTime = Time.time + _liveTest.LockCycleSeconds;
                if (_liveLockEngaged)
                {
                    player.Radar.UnlockTarget();
                    _liveLockEngaged = false;
                    Log.Info("LIVE-TEST", "Radar unlocked");
                }
                else
                {
                    TryLiveLockFirstRemote(player, "cycle");
                }
            }
            catch (Exception ex)
            {
                Log.Warning("LIVE-TEST", $"Lock cycle failed: {ex.Message}");
            }
        }

        private void TryLiveLockFirstRemote(UniAircraft player, string reason)
        {
            foreach (var peerId in _remoteManager.GetAllPeerIds())
            {
                var aircraft = _remoteManager.GetAircraft(peerId);
                if (aircraft == null) continue;
                var target = aircraft.GetComponentInChildren<Falcon.Targeting.Target>();
                if (target == null || !target.IsTargetable) continue;

                if (!player.Radar.IsActive)
                    player.Radar.SetActive(true);
                bool locked = player.Radar.LockTarget(target, false);
                _liveLockEngaged = locked;
                Log.Info("LIVE-TEST", $"Radar lock ({reason}) onto peer {peerId} clone: success={locked}");
                break;
            }
        }

        private void StartLiveSession()
        {
            try
            {
                SetNativeMainMenuUIVisible(false);
                ApplyBandwidthPreset(_connection?.Config);

                if (_liveTest.Role == LiveTestRole.Host)
                {
                    string hostName = ModConfig.HostServerName?.Value ?? "TCA Server";
                    _connection.HostSession(hostName, _liveTest.Port);
                    _menu?.ShowLobby();
                    _liveSessionStarted = true;
                    _liveNextActionTime = Time.time + 1f;
                    Log.Info("LIVE-TEST", $"Started host session on port {_liveTest.Port}");
                }
                else if (_liveTest.Role == LiveTestRole.Client)
                {
                    _connection.JoinSession(_liveTest.Address, _liveTest.Port);
                    _menu?.ShowLobby();
                    _liveSessionStarted = true;
                    _liveNextActionTime = Time.time + 1f;
                    Log.Info("LIVE-TEST", $"Started client session to {_liveTest.Address}:{_liveTest.Port}");
                }
                else
                {
                    Log.Warning("LIVE-TEST", "Autostart requested without a host/client role");
                    _liveSessionStarted = true;
                }
            }
            catch (System.Exception ex)
            {
                _liveSessionStarted = true;
                Log.Error("LIVE-TEST", $"Autostart failed: {ex}");
            }
        }

        private bool ApplyLiveTestDefaults()
        {
            var local = _activeSession.GetLocalPlayer();
            if (local == null) return false;

            string username = ModConfig.Username?.Value;
            if (!string.IsNullOrWhiteSpace(username))
                local.PlayerName = username;

            if (_activeSession.IsHost && string.IsNullOrWhiteSpace(_activeSession.MapName))
                _lobby.SetMap(MapHelper.GetDefaultMapName());

            string mapName = _activeSession.MapName ?? MapHelper.GetDefaultMapName();

            if (string.IsNullOrWhiteSpace(local.SelectedAircraft))
            {
                string aircraft = ModConfig.LastAircraft?.Value;
                if (string.IsNullOrWhiteSpace(aircraft))
                    aircraft = "AV8B";
                _lobby.SetAircraft(LoadoutHelper.ResolveAvailableAircraft(aircraft));
            }
            else if (!LoadoutHelper.IsAircraftAvailable(local.SelectedAircraft))
            {
                _lobby.SetAircraft(local.SelectedAircraft);
            }

            if (string.IsNullOrWhiteSpace(local.SelectedLoadout))
            {
                string loadout = ModConfig.LastLoadout?.Value;
                if (string.IsNullOrWhiteSpace(loadout))
                {
                    string aircraft = local.SelectedAircraft ?? ModConfig.LastAircraft?.Value ?? "AV8B";
                    loadout = LoadoutHelper.GetDefaultLoadoutForAircraft(aircraft);
                }
                _lobby.SetLoadout(string.IsNullOrWhiteSpace(loadout) ? "Clean" : loadout);
            }
            else
            {
                string resolvedLoadout = LoadoutHelper.ResolveLoadoutForAircraft(local.SelectedAircraft, local.SelectedLoadout);
                if (!string.Equals(local.SelectedLoadout, resolvedLoadout, StringComparison.Ordinal))
                    _lobby.SetLoadout(resolvedLoadout);
            }

            if (string.IsNullOrWhiteSpace(local.SelectedAirfield)
                || !MapHelper.IsAirfieldOnMap(mapName, local.SelectedAirfield))
            {
                string airfield = ModConfig.LastAirfield?.Value;
                if (string.IsNullOrWhiteSpace(airfield) || !MapHelper.IsAirfieldOnMap(mapName, airfield))
                    airfield = MapHelper.GetDefaultAirfieldName(mapName);
                if (!string.IsNullOrWhiteSpace(airfield))
                    _lobby.SetAirfield(airfield);
            }

            Log.Info("LIVE-TEST",
                $"Defaults applied: player={local.PlayerName}, aircraft={local.SelectedAircraft}, " +
                $"loadout={local.SelectedLoadout}, airfield={local.SelectedAirfield}");
            return true;
        }

        private static void ApplyBandwidthPreset(TransportConfig config)
        {
            if (config == null) return;

            config.LocalBindAddress = ModConfig.LocalBindAddress?.Value ?? "";
            config.AutoVpnBind = true;
            config.ModVersion = PluginMetadata.Version;
            bool lowBandwidth = ModConfig.LowBandwidthMode?.Value ?? false;
            if (lowBandwidth)
            {
                config.KeepaliveInterval = 1.0f;
                config.TimeoutSeconds = 20.0f;
                config.ReconnectGraceSeconds = 90.0f;
                config.EndpointRefreshInterval = 3.0f;
                config.RetransmitInterval = 0.35f;
                config.MaxRetransmitAttempts = 180;
                config.MaxReliableRetransmitsPerUpdate = 4;
            }
            else
            {
                config.KeepaliveInterval = 2.0f;
                config.TimeoutSeconds = 10.0f;
                config.ReconnectGraceSeconds = 30.0f;
                config.EndpointRefreshInterval = 5.0f;
                config.RetransmitInterval = 0.25f;
                config.MaxRetransmitAttempts = 120;
                config.MaxReliableRetransmitsPerUpdate = 8;
            }
        }

        private void UpdateAdaptiveNetworkQuality()
        {
            if (_connection == null || _activeSession == null)
                return;
            if (Time.time < _nextNetworkQualityUpdateTime)
                return;

            _nextNetworkQualityUpdateTime = Time.time + 2f;

            var quality = _connection.GetNetworkQuality();
            var reliability = _connection.GetReliabilityStats();
            long retransmitDelta = Math.Max(0L, reliability.ReliableRetransmitted - _lastReliableRetransmits);
            long dropDelta = Math.Max(0L, reliability.ReliableDropped - _lastReliableDrops);
            _lastReliableRetransmits = reliability.ReliableRetransmitted;
            _lastReliableDrops = reliability.ReliableDropped;

            float configuredRate = ModConfig.StateSendRateHz?.Value ?? DefaultStateSendRateHz;
            float targetRate = Mathf.Clamp(configuredRate, MinStateSendRateHz, MaxStateSendRateHz);
            bool lowBandwidth = ModConfig.LowBandwidthMode?.Value ?? false;

            if (lowBandwidth)
            {
                targetRate = Mathf.Min(targetRate, LowBandwidthStateSendRateHz);
            }
            else if (dropDelta > 0 || reliability.PendingCount >= 12 || retransmitDelta >= 16
                || quality.SmoothedRttMs >= 450f || quality.SecondsSinceLastReceive >= 3.5f)
            {
                targetRate = Mathf.Min(targetRate, BadQualityStateSendRateHz);
            }
            else if (reliability.PendingCount >= 5 || retransmitDelta >= 6
                || quality.SmoothedRttMs >= 220f || quality.SecondsSinceLastReceive >= 2.0f)
            {
                targetRate = Mathf.Min(targetRate, PoorQualityStateSendRateHz);
            }

            targetRate = Mathf.Clamp(targetRate, MinStateSendRateHz, MaxStateSendRateHz);
            if (Mathf.Abs(targetRate - _adaptiveStateSendRateHz) < 0.5f)
                return;

            _adaptiveStateSendRateHz = targetRate;
            Log.Info(Tag,
                $"Adaptive send rate -> {_adaptiveStateSendRateHz:0} Hz " +
                $"(rtt={quality.SmoothedRttMs:0}ms, pending={reliability.PendingCount}, " +
                $"retries+{retransmitDelta}, drops+{dropDelta}, route={quality.RouteDescription})");
        }

        private bool RemotePlayersReady()
        {
            bool hasRemote = false;
            foreach (var player in _activeSession.Players.Values)
            {
                if (player.PeerId == _activeSession.LocalPeerId) continue;
                hasRemote = true;
                if (!player.IsReady) return false;
            }
            return hasRemote;
        }

        private void LogSessionDiagnostics(UniAircraft localAircraft)
        {
            if (Time.time - _lastDiagnosticsLogTime < 5f) return;
            _lastDiagnosticsLogTime = Time.time;

            try
            {
                int peerCount = _connection?.ConnectedPeers?.Count ?? 0;
                int pendingReliable = 0;
                if (_connection?.ConnectedPeers != null)
                {
                    foreach (var peerId in _connection.ConnectedPeers)
                        pendingReliable += _connection.GetReliablePendingCount(peerId);
                }

                var state = _activeSession.StateMachine.CurrentState;
                string mapName = Falcon.Game2.GameLogic.Instance?.LoadedMap?.MapName ?? "<none>";
                string flight = Falcon.Game2.FlightGame.Instance != null ? "yes" : "no";
                int remoteCount = _remoteManager?.PeerCount ?? 0;
                Log.Info(Tag,
                    $"MP status: state={state}, peers={peerCount}, reliablePending={pendingReliable}, " +
                    $"map={mapName}, flightGame={flight}, localAircraft={(localAircraft != null ? localAircraft.name : "<none>")}, remotes={remoteCount}");
            }
            catch (System.Exception ex)
            {
                Log.Warning(Tag, $"Diagnostics failed: {ex.Message}");
            }
        }

        private void OnSessionEnded()
        {
            TeardownSession();
        }

        private void FixedUpdate()
        {
            // Gun bullet spawning must run at physics rate for consistent tracers
            _gunSync?.FixedUpdate();
            SendLocalAircraftState(Time.fixedDeltaTime);
        }

        private void SendLocalAircraftState(float deltaTime)
        {
            if (_activeSession == null || _stateReader == null || _transport == null)
                return;

            var localAircraft = _spawnManager?.LocalAircraft;
            if (localAircraft == null)
                return;

            // Sample and send from the physics tick so packet positions match the
            // cadence at which the native aircraft simulation actually moves.
            if (!ShouldSendAircraftState(deltaTime))
                return;

            _lastStateSendTime = Time.time;
            var statePacket = _stateReader.ReadState(localAircraft, _activeSession.LocalPeerId, Time.fixedTime);

            // Gear flag edges are rare and important — log them so sender-side flag
            // corruption (the cause of remote gear cycling on its own) is visible.
            if (statePacket.GearDown != _lastSentGearDown)
            {
                _lastSentGearDown = statePacket.GearDown;
                Log.Info(Tag, $"[STATE-SEND] GearDown flag -> {statePacket.GearDown} " +
                              $"(native={localAircraft.LandingGear?.IsGearLowered.ToString() ?? "<null>"})");
            }

            var payload = PacketSerializer.SerializeAircraftState(statePacket);
            var frame = PacketSerializer.Serialize(PacketType.AircraftState, payload);
            _transport.Broadcast(frame, reliable: false);
        }

        private bool _lastSentGearDown = true;

        // ── Session lifecycle ───────────────────────────────────────────

        /// <summary>
        /// Called by ConnectionManager when HostSession() or JoinSession() creates a session.
        /// Wires up ALL game systems. Previous session is torn down first.
        /// </summary>
        public void OnSessionCreated(GameSession session)
        {
            // Tear down previous session if any (host→disconnect→host scenario)
            if (_activeSession != null)
            {
                Log.Warning(Tag, "Tearing down previous session before creating new one");
                TeardownSession();
            }

            _activeSession = session;
            _lastDiagnosticsLogTime = 0f;
            var router = _connection.Router;
            _connection.OnPeerLeft += HandlePeerLeft;
            session.OnStateChanged += HandleSessionStateChanged;

            // Restore host settings from ModConfig
            if (session.IsHost)
            {
                session.SpawnType = (Core.LobbySpawnType)(ModConfig.HostSpawnType?.Value ?? 1);
                session.TimeOfDay = (Core.TimeOfDaySetting)(ModConfig.HostTimeOfDay?.Value ?? 1);
                session.AircraftCollisionsEnabled = ModConfig.HostAircraftCollisions?.Value ?? true;
                session.MaxPlayersTotal = GameSession.ClampMaxPlayersTotal(ModConfig.HostMaxPlayersTotal?.Value ?? 8);
                session.TeamCount = GameSession.ClampTeamCount(ModConfig.HostTeamCount?.Value ?? 2);
                session.GameMode = (Core.MultiplayerGameMode)Mathf.Clamp(
                    ModConfig.HostGameMode?.Value ?? 0,
                    0,
                    1);
                _connection.Config.MaxConnections = Math.Max(0, session.MaxPlayersTotal - 1);
            }
            _hostRelay = new HostPacketRelay(session, _connection, router);
            // Game flow managers
            _lobby = new LobbyManager(session, _connection, router);
            _lobby.OnAllPlayersLoaded += OnAllPlayersLoaded;
            _lobby.OnGameStarting += OnGameStarting;

            _eventBridge.Subscribe();
            _aircraftSpawner.IsFriendlyPeer = peerId => session.ArePlayersOnSameTeam(session.LocalPeerId, peerId);
            _remoteManager = new RemoteAircraftManager(session, _aircraftSpawner, _originService);
            _stateReader = new LocalAircraftStateReader(_originService);
            _modManifest = new ModManifestCollector(session, _connection, router);
            _modManifest.OnCompatibilityAccepted += HandleModCompatibilityAccepted;
            _modManifest.OnCompatibilityMismatch += HandleModCompatibilityMismatch;
            _modManifest.OnSyncStatus += HandleModSyncStatus;
            _modManifest.OnCompatibilityStateChanged += HandleModCompatibilityStateChanged;

            // Register AircraftState packet handler — routes incoming state to RemoteAircraftManager
            router.Register(PacketType.AircraftState, HandleAircraftStateRaw);

            // Handle AircraftChanged — update player info when a peer changes aircraft (e.g., respawn)
            router.Register(PacketType.AircraftChanged, HandleAircraftChangedRaw);
            _spawnManager = new SpawnManager(session, _aircraftSpawner, _originService, _eventBridge);
            _respawnManager = new RespawnManager(session, _connection, router, _remoteManager);

            // Wire death detection: SpawnManager detects destruction → RespawnManager starts cooldown → UI shows
            _spawnManager.OnPlayerDied += () => _respawnManager?.HandleLocalDeath(null);

            // Wire respawn completion: when respawn is approved, spawn a new aircraft
            _respawnManager.OnPeerRespawned += HandlePeerRespawned;
            _scoreTracker = new ScoreTracker(session, router, connection: _connection);
            _scoreTracker.OnKillConfirmed += HandleKillConfirmed;
            _scoreTracker.OnDeathConfirmed += HandleDeathConfirmed;

            // Combat sync systems
            _radarSync = new RadarSyncSystem(session, _connection, router, _remoteManager,
                () => _spawnManager?.LocalAircraft);
            _missileSync = new MissileSyncSystem(session, _connection, router, _remoteManager, _originService);
            _gunSync = new GunSyncSystem(session, _connection, router, _remoteManager);
            _damageSync = new DamageSyncSystem(session, _connection, router, _remoteManager, _originService,
                () => _spawnManager?.LocalAircraft);
            _explosionSync = new ExplosionSyncSystem(session, _connection, router, _eventBridge, _originService, _remoteManager);
            _collisionSync = new CollisionSyncManager(session, _connection, router, _remoteManager, _originService,
                () => _spawnManager?.LocalAircraft);
            _bombSync = new BombSyncSystem(session, _connection, router, _remoteManager, _originService);
            _deathCoordinator = new DeathEventCoordinator(
                session,
                _remoteManager,
                _eventBridge,
                () => _spawnManager?.LocalAircraft,
                GetCurrentLocalLifeId,
                _scoreTracker.HandleLocalDeathReport);
            _damageSync.OnRemoteDamageApplied += _deathCoordinator.RememberRemoteDamage;

            // Wire UI to session-scoped systems
            _menu.SetLobby(_lobby);
            _menu.SetModManifest(_modManifest);
            _scoreboard.Init(session, _scoreTracker);
            _respawnScreen.Init(_respawnManager, _spawnManager, session, _lobby);

            _pendingManifestSend = !session.IsHost;

            // Wire Harmony patch delegates
            DamagePatch.IsRemoteClone = d => _remoteManager?.IsRemoteCloneDamageable(d) ?? false;
            DamagePatch.IsRemoteSourceTarget = t => _remoteManager?.IsRemoteTarget(t) ?? false;
            DamagePatch.AllowRemoteCloneNativeDamage =
                (d, source) => _remoteManager?.ShouldAllowRemoteDeathSequenceDamage(d, source) ?? false;
            SortieEndPatch.IsMultiplayerSession = () => session.StateMachine.CurrentState != GameState.Disconnected;
            SortieEndPatch.IsHost = () => session.IsHost;
            SortieEndPatch.OnRequestReturnToLobby = _ => _lobby?.ReturnToLobby();
            SortieEndPatch.OnRequestLeaveSession = _ => _connection?.Disconnect();
            GamePausePatch.IsMultiplayerSession = () => session.StateMachine.CurrentState != GameState.Disconnected;
            EnvironmentPatch.IsMultiplayerSession = () => session.StateMachine.CurrentState != GameState.Disconnected;
            EnvironmentPatch.GetDeterministicSeed = () => 42;
            FireControlPatch.IsRemote = fc =>
            {
                if (fc == null || _remoteManager == null) return false;
                var aircraft = fc.GetComponentInParent<Falcon.UniversalAircraft.UniAircraft>();
                var target = aircraft != null
                    ? aircraft.GetComponentInChildren<Falcon.Targeting.Target>()
                    : fc.GetComponentInParent<Falcon.Targeting.Target>();
                return target != null && _remoteManager.IsRemoteTarget(target);
            };
            FireControlPatch.GetRemoteFiring = fc =>
            {
                if (fc == null || _gunSync == null) return false;
                var aircraft = fc.GetComponentInParent<Falcon.UniversalAircraft.UniAircraft>();
                var target = aircraft != null
                    ? aircraft.GetComponentInChildren<Falcon.Targeting.Target>()
                    : fc.GetComponentInParent<Falcon.Targeting.Target>();
                return target != null && _gunSync.IsRemoteFiring(target);
            };
            FireControlPatch.ConfigureRemoteGun = fc => _gunSync?.ConfigureRemoteGunSafety(fc);

            Log.Info(Tag, "Session systems wired up");
        }

        private void CloseMultiplayerMenuForGameplay()
        {
            _suppressNativeMainMenuRestore = true;
            try
            {
                _menu?.CloseMenu();
            }
            finally
            {
                _suppressNativeMainMenuRestore = false;
            }
        }

        private Falcon.Game2.MainMenu ResolveNativeMainMenu()
        {
            if (_nativeMainMenu != null)
                return _nativeMainMenu;

            _nativeMainMenu = UnityEngine.Object.FindFirstObjectByType<Falcon.Game2.MainMenu>(
                UnityEngine.FindObjectsInactive.Include);
            if (_nativeMainMenu != null)
                Log.Info(Tag, "Captured native MainMenu instance");

            return _nativeMainMenu;
        }

        private void SetNativeMainMenuUIVisible(bool visible)
        {
            var mainMenu = ResolveNativeMainMenu();
            if (mainMenu == null) return;

            mainMenu.ShowMainMenuUI(visible);
        }

        private void SetNativeMainMenuVisible(bool visible)
        {
            var mainMenu = ResolveNativeMainMenu();
            if (mainMenu == null) return;

            mainMenu.ShowMainMenu(visible);
        }

        private async void OnGameStarting(string mapName)
        {
            Log.Info(Tag, $"Game starting on map: {mapName}");

            try
            {
                // Hide the multiplayer menu without restoring the native main menu buttons.
                CloseMultiplayerMenuForGameplay();

                // Fully hide native MainMenu (cameras + UI) during gameplay
                // Matches GameLogic.StartQuickMission pattern: ShowMainMenu(false) when entering flight
                SetNativeMainMenuVisible(false);

                await WaitForFlightGameTeardown("before new game start");

                string resolvedMapName = string.IsNullOrWhiteSpace(mapName)
                    ? MapHelper.GetDefaultMapName()
                    : mapName;

                _loadingScreen = await LoadMapViaGameLogic(resolvedMapName);
                if (_loadingScreen == null)
                {
                    Log.Error(Tag, $"Failed to load multiplayer map '{resolvedMapName}'");
                    _connection?.Disconnect();
                    return;
                }

                Log.Info(Tag, "Loading FlightGame scene...");
                var loadOp = UnityEngine.SceneManagement.SceneManager.LoadSceneAsync(GameScenes.G2FlightGame, UnityEngine.SceneManagement.LoadSceneMode.Additive);
                float simulatedLoading = 0f;
                while (!loadOp.isDone || simulatedLoading < 1f)
                {
                    simulatedLoading += Time.deltaTime * 2f;
                    _loadingScreen.SetProgress(loadOp.progress, "FLIGHT");
                    await UniTask.Yield();
                }
                _loadingScreen.SetProgress(1f, "FLIGHT");

                var flightScene = UnityEngine.SceneManagement.SceneManager.GetSceneByName(GameScenes.G2FlightGame);
                if (flightScene.IsValid())
                    UnityEngine.SceneManagement.SceneManager.SetActiveScene(flightScene);

                Log.Info(Tag, "Waiting for FlightGame initialization...");
                float timeout = Time.realtimeSinceStartup + 10f;
                while (!IsFlightGameInstanceInScene(flightScene) && Time.realtimeSinceStartup < timeout)
                    await UniTask.Yield();

                if (!IsFlightGameInstanceInScene(flightScene))
                {
                    Log.Error(Tag, "FlightGame.Instance timed out!");
                    _connection?.Disconnect();
                    return;
                }

                ApplySessionEnvironment();

                Log.Info(Tag, "FlightGame initialized — sending loading complete");
                _lobby?.SendLoadingComplete();
            }
            catch (System.Exception ex)
            {
                Log.Error(Tag, $"Scene loading failed: {ex}");
                _connection?.Disconnect();
            }
        }

        private async UniTask<LoadingScreen> LoadMapViaGameLogic(string mapName)
        {
            var gameLogic = Falcon.Game2.GameLogic.Instance;
            if (gameLogic == null)
            {
                Log.Error(Tag, "GameLogic.Instance is null — cannot load multiplayer map");
                return null;
            }

            var mapData = Falcon.GameDataMaps.GetByName(mapName);
            if (mapData == null)
            {
                Log.Error(Tag, $"Unknown map data '{mapName}'");
                return null;
            }

            Log.Info(Tag, $"Loading map data '{mapName}' via GameLogic.LoadMap -> scene '{mapData.SceneName}'");
            var loadMapMethod = typeof(Falcon.Game2.GameLogic).GetMethod("LoadMap", BindingFlags.NonPublic | BindingFlags.Instance);
            if (loadMapMethod == null)
            {
                Log.Error(Tag, "GameLogic.LoadMap method not found");
                return null;
            }

            var taskObj = loadMapMethod.Invoke(gameLogic, new object[] { mapName });
            var loadingScreen = await (UniTask<LoadingScreen>)taskObj;
            _loadedMapSceneName = mapData.SceneName;

            var loadedMap = gameLogic.LoadedMap;
            if (loadedMap == null || loadedMap.MapName != mapName)
            {
                Log.Error(Tag, $"Map load verification failed: expected '{mapName}', got '{loadedMap?.MapName ?? "<null>"}'");
                loadingScreen?.Close(true).Forget();
                return null;
            }

            Log.Info(Tag, $"Map initialized: {loadedMap.MapName} ({loadedMap.DisplayName}), airfields={loadedMap.Airfields?.Count ?? 0}");
            return loadingScreen;
        }

        private void ApplySessionEnvironment()
        {
            try
            {
                if (_activeSession == null) return;
                var env = Falcon.World.Environment.Instance;
                if (env == null) return;

                env.SetTimeOfDayPreset((Falcon.World.TimeOfDay)(int)_activeSession.TimeOfDay);
                env.Timescale = 0f;
                Log.Info(Tag, $"Environment applied: {_activeSession.TimeOfDay}, timescale=0");
            }
            catch (System.Exception ex)
            {
                Log.Warning(Tag, $"Failed to apply session environment: {ex.Message}");
            }
        }

        private void OnAllPlayersLoaded()
        {
            Log.Info(Tag, "All players loaded — spawning local aircraft");
            var aircraft = _spawnManager?.SpawnLocalPlayer();
            if (aircraft == null)
            {
                HandleLocalSpawnFailure("Failed to spawn local aircraft");
                return;
            }

            // CRITICAL: Tell FlightGame this is the player's aircraft.
            // Without this, the game stays in spectator/free camera mode.
            var flightGame = Falcon.Game2.FlightGame.Instance;
            if (flightGame == null)
            {
                HandleLocalSpawnFailure("FlightGame.Instance is null — cannot attach player to aircraft");
                return;
            }

            flightGame.SetNewPlayerAircraft(aircraft);
            Log.Info(Tag, "FlightGame.SetNewPlayerAircraft — camera + controls attached");

            // Reset latched flight input from any previous session/life
            // (native StartFlight does this; we bypass it). Prevents a
            // stale IsEjecting flag from instantly ejecting the new aircraft.
            ResetPlayerFlightInput(flightGame);

            // Initialize flight state that StartFlight() normally provides
            // We bypass StartFlight() (it's private and runs an async game loop)
            // but we need its critical side effects:

            // 1. CRITICAL: Set FloatingOrigin reference so origin shifts happen
            // Without this, player at 31km from origin → float precision loss → cockpit jitter
            if (flightGame.FloatingOrigin != null)
            {
                flightGame.FloatingOrigin.ReferenceObject = aircraft.transform;
                Log.Info(Tag, "FloatingOrigin.ReferenceObject set to player aircraft");
            }

            // 2. Set game mode to Freeflight (FFA sandbox — full combat, no AI waves or objectives)
            // Mode has private setter — minimal reflection justified for critical correctness
            try
            {
                // Standard reflection fails for private setters in Unity Mono.
                // Use Harmony's Traverse which is purpose-built for this.
                HarmonyLib.Traverse.Create(flightGame).Property("Mode").SetValue(Falcon.Game2.FlightGame.FlightType.Freeflight);
                Log.Info(Tag, $"FlightGame.Mode set to Freeflight (verify: {flightGame.Mode})");
            }
            catch (System.Exception ex)
            {
                Log.Warning(Tag, $"Failed to set FlightGame.Mode: {ex.Message}");
            }

            // 3. Set IsMissionInProgress so game systems function correctly
            try
            {
                var missionProp = typeof(Falcon.Game2.FlightGame).GetProperty("IsMissionInProgress",
                    BindingFlags.Public | BindingFlags.Instance);
                missionProp?.SetValue(flightGame, true);
                Log.Info(Tag, "FlightGame.IsMissionInProgress set to true");
            }
            catch (System.Exception ex)
            {
                Log.Warning(Tag, $"Failed to set IsMissionInProgress: {ex.Message}");
            }

            // 4. Ensure weapon input is not blocked
            if (flightGame.PlayerInput != null)
            {
                flightGame.PlayerInput.IsInputBlockedFromGame = false;
                Log.Info(Tag, "PlayerInput.IsInputBlockedFromGame set to false");
            }

            // 4b. Register the pause-menu keys (native StartFlight does
            // this, but we bypass it). Esc opens the native pause menu;
            // GamePausePatch keeps the simulation running underneath.
            RegisterPauseMenuInput();

            // 5. Initialize flight state that StartFlight() normally provides
            // We bypass StartFlight() (private + runs async loop) but need its side effects
            try
            {
                // Hide arena strategic target icons/attrition UI (StartFlight calls this)
                var setIconsMethod = typeof(Falcon.Game2.FlightGame).GetMethod("SetArenaIconsVisible",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                setIconsMethod?.Invoke(flightGame, new object[] { false, true });

                // Tell HUD we're NOT in Arena mode
                var gameHud = Falcon.Game2.UI.HUD.GameHUD.Instance;
                if (gameHud != null)
                {
                    gameHud.StartFlight(false); // false = not Arena
                    Log.Info(Tag, "GameHUD.StartFlight(false) called");
                }
            }
            catch (System.Exception ex)
            {
                Log.Warning(Tag, $"Failed to init flight state: {ex.Message}");
            }

            _loadingScreen?.Close(true).Forget();
            _loadingScreen = null;

            _activeSession?.StateMachine.TryTransition(GameState.InGame);

            // Lock the cursor for flight (native StartFlight callers do this —
            // see QuickMissionGame.RunQuickMission — but we bypass that path).
            // Locked auto-hides the pointer; visible stays true so menus that
            // only set LockState=None (the native pattern) show the cursor.
            TinyCursor.LockState = CursorLockMode.Locked;
            Cursor.visible = true;

            Log.Info(Tag, $"Local player spawned: {aircraft.name}");
        }

        private void HandleLocalSpawnFailure(string reason)
        {
            Log.Error(Tag, reason);
            _loadingScreen?.Close(true).Forget();
            _loadingScreen = null;

            if (_activeSession?.IsHost == true)
            {
                _lobby?.ReturnToLobby();
            }
            else
            {
                _connection?.Disconnect();
            }
        }

        /// <summary>
        /// Reacts to session state transitions. The lobby return path
        /// (host Esc → "Return To Lobby", or the LobbyReturnToLobby packet on
        /// clients) only flips the state machine — the flight teardown lives
        /// here so both host and clients leave the flight the same way.
        /// </summary>
        private void HandleSessionStateChanged(GameState oldState, GameState newState)
        {
            bool leftGameplay = oldState == GameState.Loading
                || oldState == GameState.Spawning
                || oldState == GameState.InGame
                || oldState == GameState.Respawning;

            if (newState == GameState.ReturningToLobby
                || (leftGameplay && (newState == GameState.HostingLobby || newState == GameState.ClientLobby)))
            {
                CleanupFlightForLobbyReturn();
            }
        }

        /// <summary>
        /// Tear down the flight portion of the session while keeping the
        /// session itself alive: despawn aircraft, unload the FlightGame
        /// scene (map stays as lobby background, like TeardownSession), and
        /// bring the lobby UI back with an unlocked cursor.
        /// </summary>
        private void CleanupFlightForLobbyReturn()
        {
            Log.Info(Tag, "Returning to lobby — tearing down flight");

            UnregisterPauseMenuInput();
            _respawnScreen?.HideForSessionEnd();

            _spawnManager?.CleanupForLobbyReturn();
            _remoteManager?.RemoveAllPeers();
            _originService?.Reset();

            try
            {
                var flightGame = Falcon.Game2.FlightGame.Instance;
                if (flightGame != null)
                {
                    // Same flight-state cleanup as TeardownSession: prevent
                    // NREs in FixedUpdate while the scene unloads underneath
                    if (flightGame.FloatingOrigin != null)
                        flightGame.FloatingOrigin.ReferenceObject = null;

                    try
                    {
                        var missionProp = typeof(Falcon.Game2.FlightGame).GetProperty("IsMissionInProgress",
                            BindingFlags.Public | BindingFlags.Instance);
                        missionProp?.SetValue(flightGame, false);
                    }
                    catch { }
                }
            }
            catch (System.Exception ex)
            {
                Log.Warning(Tag, $"Flight state cleanup error on lobby return: {ex.Message}");
            }

            try
            {
                _loadingScreen?.Close(true).Forget();
                _loadingScreen = null;

                UnloadFlightGameScene("lobby return").Forget();
            }
            catch (System.Exception ex)
            {
                Log.Warning(Tag, $"FlightGame unload error on lobby return: {ex.Message}");
            }

            // Restore the native main menu cameras (hidden by OnGameStarting)
            // so the lobby has its 3D background again, but keep the native
            // menu buttons hidden underneath the multiplayer lobby UI.
            SetNativeMainMenuVisible(true);
            SetNativeMainMenuUIVisible(false);

            // Back to the lobby UI with a usable cursor
            TinyCursor.LockState = CursorLockMode.None;
            Cursor.visible = true;
            _menu?.ShowLobby();
        }

        private void HandlePeerRespawned(ulong peerId, string aircraftType)
        {
            _scoreTracker?.MarkPlayerRespawned(peerId);

            // Only handle local player respawns here; remote respawns handled by RespawnManager
            if (_activeSession == null || peerId != _activeSession.LocalPeerId) return;

            _deathCoordinator?.MarkLocalRespawned();

            Log.Info(Tag, $"Local respawn triggered — spawning new aircraft");

            var aircraft = _spawnManager?.SpawnLocalPlayer();
            if (aircraft == null)
            {
                Log.Error(Tag, "Failed to respawn local aircraft!");
                return;
            }

            var flightGame = Falcon.Game2.FlightGame.Instance;
            if (flightGame != null)
            {
                flightGame.SetNewPlayerAircraft(aircraft);
                Log.Info(Tag, "Re-attached camera and controls after respawn");

                // Reset latched flight input. FlightGame.Update copies
                // PlayerInput.FlightInput into the aircraft every frame, and
                // IsEjecting stays true after an ejection — without this reset
                // the respawned aircraft ejects again on its first frame.
                ResetPlayerFlightInput(flightGame);

                // Re-set floating origin reference for the new aircraft
                if (flightGame.FloatingOrigin != null)
                {
                    flightGame.FloatingOrigin.ReferenceObject = aircraft.transform;
                }

                // Ensure input is not blocked
                if (flightGame.PlayerInput != null)
                {
                    flightGame.PlayerInput.IsInputBlockedFromGame = false;
                }
            }

            // Back in flight — re-lock the cursor (same as initial spawn)
            TinyCursor.LockState = CursorLockMode.Locked;
            Cursor.visible = true;
        }

        /// <summary>
        /// Reset the persistent PlayerInput state to fresh-spawn defaults, the
        /// same way native StartFlight()/SpawnPlayerAtAirfield() do. Clears the
        /// latched FlightInput.IsEjecting flag plus stale throttle/flaps/brakes
        /// from the previous life.
        /// </summary>
        private void ResetPlayerFlightInput(Falcon.Game2.FlightGame flightGame)
        {
            var playerInput = flightGame?.PlayerInput;
            if (playerInput == null) return;

            bool isAirStart = _activeSession?.SpawnType == Core.LobbySpawnType.InAir;

            // Clears eject press/hold latches and resets FlightInput/WeaponInput
            playerInput.Reset();

            var freshInput = new Falcon.Controls.FlightInput(isEngineOn: true);
            freshInput.Throttle = isAirStart ? 0.8f : 0f;
            freshInput.AreFlapsRequested = !isAirStart;
            playerInput.FlightInput = freshInput;

            Log.Info(Tag, $"PlayerInput reset (airStart={isAirStart}) — eject latch cleared");
        }

        // ── Multiplayer pause menu (Esc) ────────────────────────────────────

        /// <summary>
        /// Register Esc/menu keys to open the native pause menu during a
        /// multiplayer flight. Mirrors the registration that the bypassed
        /// native StartFlight() would normally perform (actions 57 OpenMenu
        /// and 126 EscapeMenu).
        /// </summary>
        private void RegisterPauseMenuInput()
        {
            UnregisterPauseMenuInput();
            _pauseMenuInput = new Falcon.RewiredInputActionList();
            _pauseMenuInput.RegisterButtonJustPressed(_ => TryOpenPauseMenu(), 57);
            _pauseMenuInput.RegisterButtonJustPressed(_ => TryOpenPauseMenu(), 126);
            Log.Info(Tag, "Pause menu input registered (Esc)");
        }

        private void UnregisterPauseMenuInput()
        {
            _pauseMenuInput?.UnregisterAllActions();
            _pauseMenuInput = null;
            _mpPauseMenuOpen = false;
        }

        private static bool IsFlightGameSceneLoaded()
        {
            var flightScene = UnityEngine.SceneManagement.SceneManager.GetSceneByName(GameScenes.G2FlightGame);
            return flightScene.IsValid() && flightScene.isLoaded;
        }

        private static bool IsFlightGameInstanceAlive()
        {
            return Falcon.Game2.FlightGame.Instance != null;
        }

        private static bool IsFlightGameInstanceInScene(UnityEngine.SceneManagement.Scene scene)
        {
            var flightGame = Falcon.Game2.FlightGame.Instance;
            return flightGame != null
                && scene.IsValid()
                && flightGame.gameObject.scene == scene;
        }

        private async UniTask WaitForFlightGameTeardown(string reason, float timeoutSeconds = 10f)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;

            while ((IsFlightGameSceneLoaded() || IsFlightGameInstanceAlive())
                && Time.realtimeSinceStartup < deadline)
            {
                await UniTask.Yield();
            }

            if (IsFlightGameSceneLoaded() || IsFlightGameInstanceAlive())
            {
                Log.Warning(Tag,
                    $"Timed out waiting for FlightGame teardown ({reason}); " +
                    $"sceneLoaded={IsFlightGameSceneLoaded()}, instanceAlive={IsFlightGameInstanceAlive()}");
            }
        }

        private async UniTask UnloadFlightGameScene(string reason)
        {
            try
            {
                var flightScene = UnityEngine.SceneManagement.SceneManager.GetSceneByName(GameScenes.G2FlightGame);
                if (flightScene.IsValid() && flightScene.isLoaded)
                {
                    Log.Info(Tag, $"Unloading FlightGame scene ({reason})");
                    var unloadOp = UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(flightScene);
                    while (unloadOp != null && !unloadOp.isDone)
                        await UniTask.Yield();
                }
            }
            catch (System.Exception ex)
            {
                Log.Warning(Tag, $"FlightGame unload error ({reason}): {ex.Message}");
            }

            await WaitForFlightGameTeardown(reason);
        }

        private void TryOpenPauseMenu()
        {
            if (_mpPauseMenuOpen) return;

            var state = _activeSession?.StateMachine.CurrentState;
            if (state != GameState.InGame && state != GameState.Respawning) return;

            var flightGame = Falcon.Game2.FlightGame.Instance;
            if (flightGame == null || flightGame.PauseMenu == null) return;

            ShowMultiplayerPauseMenu(flightGame).Forget();
        }

        /// <summary>
        /// Open the native pause menu without pausing the simulation
        /// (GamePausePatch suppresses the time freeze). SortieEndPatch routes
        /// the finish button: host → return-to-lobby broadcast for everyone,
        /// client → disconnect self.
        /// </summary>
        private async UniTaskVoid ShowMultiplayerPauseMenu(Falcon.Game2.FlightGame flightGame)
        {
            _mpPauseMenuOpen = true;
            try
            {
                if (flightGame.PlayerInput != null)
                    flightGame.PlayerInput.IsInputBlockedFromGame = true;
                TinyCursor.LockState = CursorLockMode.None;

                string exitText = _activeSession?.IsHost == true ? "Return To Lobby" : "Leave Session";
                var result = await flightGame.PauseMenu.ShowPauseMenu(exitText, true);

                if (result == Falcon.Game2.UI.PauseMenu.Result.PhotoMode)
                    flightGame.Cameras?.StartPhotoMode();

                if (flightGame.PlayerInput != null)
                    flightGame.PlayerInput.IsInputBlockedFromGame = false;

                // Only re-lock the cursor in normal flight; the respawn screen
                // and lobby UI manage the cursor themselves.
                if (_activeSession?.StateMachine.CurrentState == GameState.InGame)
                    TinyCursor.LockState = CursorLockMode.Locked;
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Pause menu failed: {ex.Message}");
                var fg = Falcon.Game2.FlightGame.Instance;
                if (fg != null && fg.PlayerInput != null)
                    fg.PlayerInput.IsInputBlockedFromGame = false;
            }
            finally
            {
                _mpPauseMenuOpen = false;
            }
        }

        private void HandleKillConfirmed(KillConfirmPacket packet)
        {
            if (_activeSession == null) return;
            if (packet.VictimId != _activeSession.LocalPeerId)
            {
                _gunSync?.SetPeerFiring(packet.VictimId, false);
            }
        }

        private void HandleDeathConfirmed(AircraftDestroyedPacket packet)
        {
            if (_activeSession == null) return;
            if (packet.VictimId != _activeSession.LocalPeerId)
            {
                _gunSync?.SetPeerFiring(packet.VictimId, false);
            }
        }

        private uint GetCurrentLocalLifeId()
        {
            var session = _activeSession;
            var player = session?.GetLocalPlayer();
            if (session == null || player == null)
                return 0;

            return player.LifeId != 0
                ? player.LifeId
                : session.BeginPlayerLife(player.PeerId);
        }

        private void HandleAircraftStateRaw(ulong fromPeerId, byte[] data)
        {
            // Only track remote aircraft during gameplay. After a return to
            // lobby, peers still mid-flight keep sending state for a moment —
            // without this gate those packets resurrect clones into the lobby.
            var state = _activeSession?.StateMachine.CurrentState;
            if (state != GameState.Spawning
                && state != GameState.InGame
                && state != GameState.Respawning)
                return;

            var (_, payload) = PacketSerializer.Deserialize(data);
            if (payload == null) return;
            var packet = PacketSerializer.DeserializeAircraftState(payload);
            ulong ownerPeerId = packet.PlayerId != 0 ? packet.PlayerId : fromPeerId;
            if (_activeSession != null
                && _activeSession.IsHost
                && fromPeerId != _activeSession.LocalPeerId
                && ownerPeerId != fromPeerId)
            {
                Log.Warning(Tag, $"Rejected AircraftState from peer {fromPeerId} for peer {ownerPeerId}");
                return;
            }
            if (ownerPeerId == _activeSession?.LocalPeerId) return;
            _remoteManager?.HandleStatePacket(ownerPeerId, packet);
        }

        private void HandleAircraftChangedRaw(ulong fromPeerId, byte[] data)
        {
            var (_, payload) = PacketSerializer.Deserialize(data);
            if (payload == null) return;
            var packet = PacketSerializer.DeserializeAircraftChanged(payload);
            if (_activeSession != null
                && _activeSession.IsHost
                && fromPeerId != _activeSession.LocalPeerId
                && packet.PlayerId != fromPeerId)
            {
                Log.Warning(Tag, $"Rejected AircraftChanged from peer {fromPeerId} for peer {packet.PlayerId}");
                return;
            }
            var player = _activeSession?.GetPlayer(packet.PlayerId);
            if (player != null)
            {
                player.SelectedAircraft = packet.AircraftType;
                player.IsAlive = packet.IsAlive;
                Log.Info(Tag, $"Peer {packet.PlayerId} aircraft changed to {packet.AircraftType}");
            }
        }

        private void HandlePeerLeft(ulong peerId)
        {
            _radarSync?.CleanupPeerRadar(peerId);
            _missileSync?.CleanupPeerMissiles(peerId);
            _bombSync?.CleanupPeerBombs(peerId);
            _gunSync?.RemovePeer(peerId);
            _remoteManager?.RemovePeer(peerId);
            Log.Info(Tag, $"Cleaned up disconnected peer {peerId}");
        }

        private void HandleModCompatibilityAccepted()
        {
            if (_activeSession?.IsHost == true)
                return;

            var local = _activeSession?.GetLocalPlayer();
            if (local != null)
                local.IsModsVerified = true;

            Log.Info(Tag, "Mod compatibility accepted by host");
            _connection?.SetStatusMessage("Mods verified");
            _lobby?.AnnounceLocalPlayer();
        }

        private void HandleModCompatibilityMismatch(ModManifestCollector.ModMismatchInfo info)
        {
            string reason = info?.Reason ?? "Mod files mismatch";
            Log.Warning(Tag, $"Mod compatibility rejected by host: {reason}");
            _connection?.SetStatusMessage("Mod mismatch - sync or cancel");
        }

        private void HandleModSyncStatus(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                Log.Info(Tag, message);
                _connection?.SetStatusMessage(message);
            }
        }

        private void HandleModCompatibilityStateChanged()
        {
            _lobby?.PublishLobbyState();
            _menu?.RefreshIfVisible();
        }

        /// <summary>
        /// Tear down all session-scoped systems. Called on disconnect or before creating a new session.
        /// </summary>
        private void TeardownSession()
        {
            if (_tearingDown) return;  // Prevent re-entrant teardown
            _tearingDown = true;
            try
            {
            if (_connection != null)
            {
                _connection.OnPeerLeft -= HandlePeerLeft;
                _connection.Router.Unregister(PacketType.AircraftState, HandleAircraftStateRaw);
                _connection.Router.Unregister(PacketType.AircraftChanged, HandleAircraftChangedRaw);
            }

            if (_activeSession != null)
                _activeSession.OnStateChanged -= HandleSessionStateChanged;

            // Clean up flight state to prevent freeze when returning to menu
            try
            {
                var flightGame = Falcon.Game2.FlightGame.Instance;
                if (flightGame != null)
                {
                    // Null out FloatingOrigin.ReferenceObject to prevent NRE in FixedUpdate
                    if (flightGame.FloatingOrigin != null)
                        flightGame.FloatingOrigin.ReferenceObject = null;

                    // Reset IsMissionInProgress
                    try
                    {
                        var missionProp = typeof(Falcon.Game2.FlightGame).GetProperty("IsMissionInProgress",
                            BindingFlags.Public | BindingFlags.Instance);
                        missionProp?.SetValue(flightGame, false);
                    }
                    catch { }
                }
            }
            catch (System.Exception ex)
            {
                Log.Warning(Tag, $"Flight state cleanup error: {ex.Message}");
            }

            // Combat sync
            _hostRelay?.Dispose(); _hostRelay = null;
            _collisionSync?.Dispose(); _collisionSync = null;
            _explosionSync?.Dispose(); _explosionSync = null;
            if (_damageSync != null && _deathCoordinator != null)
                _damageSync.OnRemoteDamageApplied -= _deathCoordinator.RememberRemoteDamage;
            _deathCoordinator?.Dispose(); _deathCoordinator = null;
            _damageSync?.Dispose(); _damageSync = null;
            _gunSync?.Dispose(); _gunSync = null;
            _bombSync?.Dispose(); _bombSync = null;
            _missileSync?.Dispose(); _missileSync = null;
            _radarSync?.Dispose(); _radarSync = null;

            // Game flow
            if (_modManifest != null)
            {
                _modManifest.OnCompatibilityAccepted -= HandleModCompatibilityAccepted;
                _modManifest.OnCompatibilityMismatch -= HandleModCompatibilityMismatch;
                _modManifest.OnSyncStatus -= HandleModSyncStatus;
                _modManifest.OnCompatibilityStateChanged -= HandleModCompatibilityStateChanged;
                _modManifest.Dispose();
                _modManifest = null;
            }
            _pendingManifestSend = false;
            if (_aircraftSpawner != null)
                _aircraftSpawner.IsFriendlyPeer = null;
            if (_scoreTracker != null)
            {
                _scoreTracker.OnKillConfirmed -= HandleKillConfirmed;
                _scoreTracker.OnDeathConfirmed -= HandleDeathConfirmed;
            }
            _scoreTracker?.Dispose(); _scoreTracker = null;
            // Unsubscribe respawn handler before disposing managers
            if (_respawnManager != null)
            {
                _respawnManager.OnPeerRespawned -= HandlePeerRespawned;
            }

            _respawnManager?.Dispose(); _respawnManager = null;
            _spawnManager?.Dispose(); _spawnManager = null;
            if (_lobby != null)
            {
                _lobby.OnAllPlayersLoaded -= OnAllPlayersLoaded;
                _lobby.OnGameStarting -= OnGameStarting;
                _lobby.Dispose();
                _lobby = null;
            }
            _remoteManager?.Dispose(); _remoteManager = null;
            _stateReader = null;
            _originService?.Reset();

            // Clear UI references to disposed systems and prevent stale lobby UI after host loss.
            _menu?.HandleSessionEnded();
            _respawnScreen?.HideForSessionEnd();

            // Clear Harmony delegates
            DamagePatch.IsRemoteClone = null;
            DamagePatch.IsRemoteSourceTarget = null;
            DamagePatch.AllowRemoteCloneNativeDamage = null;
            DamagePatch.NetworkDamageDepth = 0;
            SortieEndPatch.IsMultiplayerSession = null;
            SortieEndPatch.IsHost = null;
            SortieEndPatch.OnRequestReturnToLobby = null;
            SortieEndPatch.OnRequestLeaveSession = null;
            GamePausePatch.IsMultiplayerSession = null;
            UnregisterPauseMenuInput();
            EnvironmentPatch.IsMultiplayerSession = null;
            EnvironmentPatch.GetDeterministicSeed = null;
            FireControlPatch.IsRemote = null;
            FireControlPatch.GetRemoteFiring = null;
            FireControlPatch.ConfigureRemoteGun = null;

            _activeSession = null;
            Log.Info(Tag, "Session torn down");

            // Restore native main menu when returning from game
            SetNativeMainMenuVisible(true);
            if (_menu?.IsVisible == true)
                SetNativeMainMenuUIVisible(false);

            // Back in menus — unlock the cursor and clear any stale hidden state
            // (a death/respawn cycle may have left it locked or invisible)
            TinyCursor.LockState = CursorLockMode.None;
            Cursor.visible = true;

            // Unload FlightGame and map scenes that were loaded additively
            try
            {
                _loadingScreen?.Close(true).Forget();
                _loadingScreen = null;

                var flightScene = UnityEngine.SceneManagement.SceneManager.GetSceneByName(GameScenes.G2FlightGame);
                if (flightScene.IsValid() && flightScene.isLoaded)
                {
                    UnloadFlightGameScene("session teardown").Forget();
                }

                var gameLogic = Falcon.Game2.GameLogic.Instance;
                if (gameLogic != null && gameLogic.LoadedMap != null)
                {
                    // Leave the active map scene loaded for the native main-menu cameras. The next
                    // multiplayer start calls GameLogic.LoadMap, which unloads/replaces it correctly.
                    Log.Info(Tag, $"Keeping map scene loaded for menu background: {gameLogic.LoadedMap.MapName}");
                }
                _loadedMapSceneName = null;
            }
            catch (System.Exception ex)
            {
                Log.Warning(Tag, $"Scene unload error: {ex.Message}");
            }
            }
            finally
            {
                _tearingDown = false;
            }
        }

        // ── Shutdown ────────────────────────────────────────────────────

        public void Shutdown()
        {
            TeardownSession();

            _eventBridge?.Dispose();
            _aircraftSpawner?.Dispose();
            _originService?.Dispose();
            if (_updater != null)
            {
                _updater.OnStatusChanged -= HandleUpdaterStatusChanged;
                _updater.OnUpdateAvailable -= HandleUpdateAvailable;
                _updater.OnUpdateReady -= HandleUpdateReady;
            }
            if (_connection != null)
            {
                _connection.OnSessionCreated -= OnSessionCreated;
                _connection.OnSessionEnded -= OnSessionEnded;
            }
            _connection?.Dispose();
            _transport?.Dispose();

            Log.Info(Tag, "All systems shut down");
        }

        private void OnDestroy()
        {
            Shutdown();
        }

        private enum LiveTestRole { None, Host, Client }

        private sealed class LiveTestOptions
        {
            public bool Enabled;
            public bool AutoStart;
            public bool Ready;
            public bool StartGame;
            public LiveTestRole Role = LiveTestRole.None;
            public string Address = "127.0.0.1";
            public int Port = 7777;
            public float StartDelaySeconds = 2f;
            // When > 0, toggles the local plane's landing gear every N seconds in
            // flight, so a 2-instance test exercises the gear sync chain end-to-end.
            public float GearCycleSeconds;
            // When > 0, alternately locks/unlocks the local radar onto the first remote
            // clone every N seconds, so a 2-instance test exercises the radar lock /
            // RWR threat warning sync chain end-to-end.
            public float LockCycleSeconds;

            public static LiveTestOptions FromCommandLine()
            {
                var options = new LiveTestOptions();
                foreach (var arg in System.Environment.GetCommandLineArgs())
                {
                    if (!arg.StartsWith("--tca-live", System.StringComparison.OrdinalIgnoreCase))
                        continue;

                    options.Enabled = true;
                    if (EqualsArg(arg, "--tca-live-autostart"))
                        options.AutoStart = true;
                    else if (EqualsArg(arg, "--tca-live-ready"))
                        options.Ready = true;
                    else if (EqualsArg(arg, "--tca-live-start-game"))
                        options.StartGame = true;
                    else if (TryGetValue(arg, "--tca-live-role", out var role))
                    {
                        if (role.Equals("host", System.StringComparison.OrdinalIgnoreCase))
                            options.Role = LiveTestRole.Host;
                        else if (role.Equals("client", System.StringComparison.OrdinalIgnoreCase))
                            options.Role = LiveTestRole.Client;
                    }
                    else if (TryGetValue(arg, "--tca-live-address", out var address))
                    {
                        if (!string.IsNullOrWhiteSpace(address))
                            options.Address = address;
                    }
                    else if (TryGetValue(arg, "--tca-live-port", out var portText))
                    {
                        if (int.TryParse(portText, out int port) && port > 0)
                            options.Port = port;
                    }
                    else if (TryGetValue(arg, "--tca-live-start-delay", out var delayText))
                    {
                        if (float.TryParse(delayText, out float delay) && delay >= 0f)
                            options.StartDelaySeconds = delay;
                    }
                    else if (TryGetValue(arg, "--tca-live-gear-cycle", out var gearCycleText))
                    {
                        if (float.TryParse(gearCycleText, out float gearCycle) && gearCycle > 0f)
                            options.GearCycleSeconds = gearCycle;
                    }
                    else if (TryGetValue(arg, "--tca-live-lock-cycle", out var lockCycleText))
                    {
                        if (float.TryParse(lockCycleText, out float lockCycle) && lockCycle > 0f)
                            options.LockCycleSeconds = lockCycle;
                    }
                }

                return options;
            }

            private static bool EqualsArg(string arg, string name)
            {
                return arg.Equals(name, System.StringComparison.OrdinalIgnoreCase);
            }

            private static bool TryGetValue(string arg, string name, out string value)
            {
                value = null;
                string prefix = name + "=";
                if (!arg.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                    return false;
                value = arg.Substring(prefix.Length);
                return true;
            }
        }
    }
}
