using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TCAMultiplayer.Compatibility;
using TCAMultiplayer.Core;
using TCAMultiplayer.Game;
using TCAMultiplayer.Transport;
using ConnectionManager = TCAMultiplayer.Core.ConnectionManager;

namespace TCAMultiplayer.UI
{
    /// <summary>
    /// Multiplayer front-end built from native Tiny Combat Arena UI clones.
    /// It follows the game's menu pattern: show one root, wire buttons, rebuild
    /// page content, then hide the root when returning to the main menu.
    /// </summary>
    public class MultiplayerMenu : MonoBehaviour
    {
        private const string Tag = "MP-MENU";
        private const string Green = "#00FF40";
        private const string DimGreen = "#007A28";
        private const string ModSyncWarning =
            "Before syncing, TCAMP will back up your current Mods folder to TCAMP_ModBackups in the game root.\n\n" +
            "Syncing from the host will overwrite changed mod files and remove extra sync-safe files in your Mods folder.\n\n" +
            "Executable and plugin files are blocked from sync and will not be copied.";
        private static readonly Color ModSyncPrimaryFill = new Color(0f, 0.34f, 0.10f, 0.95f);
        private static readonly Color ModSyncPrimaryHover = new Color(0f, 0.58f, 0.17f, 1f);
        private static readonly Color ModSyncPrimaryPressed = new Color(0f, 0.24f, 0.08f, 1f);
        private static readonly Color ModSyncSecondaryFill = new Color(0f, 0f, 0f, 0.38f);
        private static readonly Color ModSyncSecondaryHover = new Color(0f, 0.18f, 0.06f, 0.65f);
        private static readonly Color ModSyncSecondaryPressed = new Color(0f, 0.10f, 0.04f, 0.8f);
        private static readonly Color ModSyncDisabledFill = new Color(0f, 0.05f, 0.02f, 0.45f);

        private enum Screen { MainMenu, HostSetup, DirectConnect, SteamLobbyBrowser, Lobby }
        private Screen _currentScreen = Screen.MainMenu;

        private ConnectionManager _connection;
        private LobbyManager _lobby;

        private Canvas _canvas;
        private GameObject _root;
        private GameObject _contentRoot;
        private GameObject _lastSelected;
        private bool _visible;
        private bool _isRefreshing;
        private bool _refreshPending;
        private bool _nativeDialogOpen;
        private CursorLockMode _previousCursorLockState;
        private string _statusMessage = "";
        private Func<string> _externalStatusProvider;
        private ModManifestCollector _modManifest;
        private ModManifestCollector.ModMismatchInfo _modMismatch;
        private bool _modSyncInProgress;
        private int _modSyncReceived;
        private int _modSyncTotal;
        private float _lastModSyncUiRefreshTime;

        public Action OnMenuClosed;
        public bool IsVisible => _visible;

        private string _username;
        private string _connectIP;
        private string _connectPort;
        private string _hostName;
        private string _hostPort;
        private int _transportIdx;

        private Steamworks.Data.Lobby[] _lobbyResults;
        private bool _refreshingLobbies;

        public void Init(ConnectionManager connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _connection.OnStatusMessage -= OnConnectionStatusMessage;
            _connection.OnStatusMessage += OnConnectionStatusMessage;
            _statusMessage = _connection.StatusMessage ?? "";
            UIFactory.NativeDialogActiveChanged -= OnNativeDialogActiveChanged;
            UIFactory.NativeDialogActiveChanged += OnNativeDialogActiveChanged;
        }

        public void SetLobby(LobbyManager lobby)
        {
            if (_lobby != null) _lobby.OnLobbyStateChanged -= OnLobbyStateChanged;
            _lobby = lobby;
            if (_lobby != null) _lobby.OnLobbyStateChanged += OnLobbyStateChanged;
        }

        public void SetExternalStatusProvider(Func<string> statusProvider)
        {
            _externalStatusProvider = statusProvider;
        }

        public void RefreshIfVisible()
        {
            if (_visible)
                RefreshUI();
        }

        public void SetModManifest(ModManifestCollector modManifest)
        {
            if (_modManifest != null)
            {
                _modManifest.OnCompatibilityMismatch -= OnModCompatibilityMismatch;
                _modManifest.OnCompatibilityAccepted -= OnModCompatibilityAccepted;
                _modManifest.OnSyncStatus -= OnModSyncStatus;
            }

            _modManifest = modManifest;
            if (_modManifest != null)
            {
                _modManifest.OnCompatibilityMismatch += OnModCompatibilityMismatch;
                _modManifest.OnCompatibilityAccepted += OnModCompatibilityAccepted;
                _modManifest.OnSyncStatus += OnModSyncStatus;
            }
        }

        public void HandleSessionEnded()
        {
            SetLobby(null);
            SetModManifest(null);
            _modMismatch = null;
            ResetModSyncState();
            _currentScreen = Screen.MainMenu;

            if (!_visible)
            {
                if (_contentRoot != null)
                    ClearChildren(_contentRoot.transform);
                return;
            }

            LoadDefaults();
            RefreshUI();
            Log.Info(Tag, "Session ended; reset to multiplayer menu");
        }

        public void ToggleMenu()
        {
            if (!_visible)
            {
                if (_canvas == null) SetupUI();
                LoadDefaults();
                _currentScreen = _connection != null && _connection.IsConnected ? Screen.Lobby : Screen.MainMenu;
                _root.SetActive(true);
                _canvas.enabled = true;
                _visible = true;
                UnlockCursorForMenu();
                EnsureEventSystem();
                RefreshUI();
            }
            else
            {
                CloseMenu();
            }
        }

        public void ShowLobby()
        {
            if (_canvas == null) SetupUI();
            LoadDefaults();
            _currentScreen = Screen.Lobby;
            _root.SetActive(true);
            _canvas.enabled = true;
            _visible = true;
            UnlockCursorForMenu();
            EnsureEventSystem();
            RefreshUI();
            Log.Info(Tag, "Showing lobby");
        }

        private void Update()
        {
            if (!_visible) return;

            if (Cursor.lockState != CursorLockMode.None || !Cursor.visible)
                UnlockCursorForMenu(false);

            if (_nativeDialogOpen)
                return;

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (_currentScreen == Screen.MainMenu || _currentScreen == Screen.Lobby)
                    CloseMenu();
                else if (_currentScreen == Screen.SteamLobbyBrowser)
                    SetScreen(Screen.MainMenu);
                else
                    SetScreen(Screen.MainMenu);
            }
        }

        private void OnDestroy()
        {
            if (_lobby != null) _lobby.OnLobbyStateChanged -= OnLobbyStateChanged;
            if (_connection != null) _connection.OnStatusMessage -= OnConnectionStatusMessage;
            UIFactory.NativeDialogActiveChanged -= OnNativeDialogActiveChanged;
        }

        private void OnLobbyStateChanged()
        {
            if (_visible && _currentScreen == Screen.Lobby)
                RefreshUI();
        }

        private void OnConnectionStatusMessage(string message)
        {
            _statusMessage = message ?? "";
            if (_visible)
                RefreshUI();
        }

        private void OnModCompatibilityMismatch(ModManifestCollector.ModMismatchInfo info)
        {
            _modMismatch = info;
            _modSyncInProgress = false;
            _currentScreen = Screen.Lobby;
            ShowLobby();
        }

        private void OnModCompatibilityAccepted()
        {
            _modMismatch = null;
            ResetModSyncState();
            if (_visible)
                RefreshUI();
        }

        private void OnModSyncStatus(string message)
        {
            _statusMessage = message ?? "";
            if (UpdateModSyncProgress(message))
            {
                if (_visible && Time.unscaledTime - _lastModSyncUiRefreshTime >= 0.2f)
                {
                    _lastModSyncUiRefreshTime = Time.unscaledTime;
                    RefreshUI();
                }
                return;
            }

            if (!string.IsNullOrWhiteSpace(message))
                _modSyncInProgress = true;

            if (_visible)
                RefreshUI();
        }

        private void OnNativeDialogActiveChanged(bool active)
        {
            _nativeDialogOpen = active;

            if (_canvas != null)
            {
                _canvas.enabled = _visible && !active;

                var raycaster = _canvas.GetComponent<GraphicRaycaster>();
                if (raycaster != null)
                    raycaster.enabled = !active;
            }
        }

        private void LoadDefaults()
        {
            _username = ModConfig.Username?.Value ?? "Player";
            _connectIP = ModConfig.LastIP?.Value ?? "127.0.0.1";
            _connectPort = ModConfig.LastPort?.Value ?? "7777";
            _hostName = ModConfig.HostServerName?.Value ?? "TCA Server";
            _hostPort = ModConfig.HostPort?.Value ?? "7777";
            _transportIdx = ModConfig.GetTransportType() == Core.TransportType.SteamLobby ? 1 : 0;
        }

        private void SetupUI()
        {
            _canvas = gameObject.GetComponent<Canvas>() ?? gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 40;

            var scaler = gameObject.GetComponent<CanvasScaler>() ?? gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            if (gameObject.GetComponent<GraphicRaycaster>() == null)
                gameObject.AddComponent<GraphicRaycaster>();

            _root = new GameObject("MultiplayerMenuRoot", typeof(RectTransform));
            _root.transform.SetParent(transform, false);
            var rootRect = _root.GetComponent<RectTransform>();
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;

            var overlay = new GameObject("Background", typeof(RectTransform), typeof(Image));
            overlay.transform.SetParent(_root.transform, false);
            var overlayRect = overlay.GetComponent<RectTransform>();
            overlayRect.anchorMin = Vector2.zero;
            overlayRect.anchorMax = Vector2.one;
            overlayRect.offsetMin = Vector2.zero;
            overlayRect.offsetMax = Vector2.zero;
            var overlayImage = overlay.GetComponent<Image>();
            overlayImage.color = new Color(0f, 0f, 0f, 0.85f);
            overlayImage.raycastTarget = false;

            _contentRoot = new GameObject("Content", typeof(RectTransform));
            _contentRoot.transform.SetParent(_root.transform, false);
            _contentRoot.GetComponent<RectTransform>().anchorMin = Vector2.zero;
            _contentRoot.GetComponent<RectTransform>().anchorMax = Vector2.one;
            _contentRoot.GetComponent<RectTransform>().offsetMin = Vector2.zero;
            _contentRoot.GetComponent<RectTransform>().offsetMax = Vector2.zero;

            _root.SetActive(false);
            _canvas.enabled = false;
        }

        private void SetScreen(Screen screen)
        {
            _currentScreen = screen;
            RefreshUI();
        }

        private void RefreshUI()
        {
            if (_contentRoot == null) return;
            if (_isRefreshing)
            {
                _refreshPending = true;
                return;
            }

            _isRefreshing = true;
            _refreshPending = false;
            _lastSelected = null;

            ClearChildren(_contentRoot.transform);

            switch (_currentScreen)
            {
                case Screen.MainMenu: DrawMainMenu(); break;
                case Screen.HostSetup: DrawHostSetup(); break;
                case Screen.DirectConnect: DrawDirectConnect(); break;
                case Screen.SteamLobbyBrowser: DrawSteamLobbyBrowser(); break;
                case Screen.Lobby: DrawLobby(); break;
            }

            SelectFirstAvailable();

            _isRefreshing = false;
            if (_refreshPending)
            {
                _refreshPending = false;
                RefreshUI();
            }
        }

        private static void ClearChildren(Transform parent)
        {
            var children = new List<GameObject>();
            foreach (Transform child in parent)
                children.Add(child.gameObject);
            foreach (var child in children)
            {
                child.transform.SetParent(null);
                Destroy(child);
            }
        }

        // ── Main Pages ─────────────────────────────────────────────────

        private void DrawMainMenu()
        {
            var panel = CreateMenuPanel(860f, 580f);
            AddHeader(panel.transform, "MULTIPLAYER", "Host a session or connect to another pilot.");
            DrawStatusMessage(panel.transform);

            UIFactory.CreateLabelInputRow("Pilot >", _username, panel.transform, val =>
            {
                _username = val;
                if (ModConfig.Username != null) ModConfig.Username.Value = val;
            }, 130f);

            UIFactory.CreateSpacer(panel.transform, 8);

            // ── Steam ────────────────────────────────────────────────────
            UIFactory.CreateNativeText($"<color={DimGreen}>── STEAM LOBBY ──</color>", panel.transform, 16, TextAlignmentOptions.Center);

            var steamRow = UIFactory.CreateHorizontalGroup(panel.transform, 10);
            steamRow.GetComponent<HorizontalLayoutGroup>().childForceExpandWidth = true;

            var hostSteam = Track(UIFactory.CreateNativeButton("HOST STEAM", steamRow.transform, 48));
            if (hostSteam != null)
                hostSteam.onClick.AddListener(() =>
                {
                    _transportIdx = 1;
                    SetScreen(Screen.HostSetup);
                });

            var browseLobbies = Track(UIFactory.CreateNativeButton("BROWSE LOBBIES", steamRow.transform, 48));
            if (browseLobbies != null)
                browseLobbies.onClick.AddListener(() =>
                {
                    _transportIdx = 1;
                    SetScreen(Screen.SteamLobbyBrowser);
                });

            UIFactory.CreateSpacer(panel.transform, 8);

            // ── Direct ───────────────────────────────────────────────────
            UIFactory.CreateNativeText($"<color={DimGreen}>── DIRECT IP ──</color>", panel.transform, 16, TextAlignmentOptions.Center);

            var directRow = UIFactory.CreateHorizontalGroup(panel.transform, 10);
            directRow.GetComponent<HorizontalLayoutGroup>().childForceExpandWidth = true;

            var hostDirect = Track(UIFactory.CreateNativeButton("HOST DIRECT", directRow.transform, 48));
            if (hostDirect != null)
                hostDirect.onClick.AddListener(() =>
                {
                    _transportIdx = 0;
                    SetScreen(Screen.HostSetup);
                });

            var directConnect = Track(UIFactory.CreateNativeButton("DIRECT CONNECT", directRow.transform, 48));
            if (directConnect != null)
                directConnect.onClick.AddListener(() =>
                {
                    _transportIdx = 0;
                    SetScreen(Screen.DirectConnect);
                });

            // Force all four host/browse buttons to the same explicit width,
            // derived from panel width (860) minus padding (28*2=56) minus spacing (10) / 2
            float btnWidth = 397f;
            foreach (var btn in new[] { hostSteam, browseLobbies, hostDirect, directConnect })
            {
                if (btn != null)
                    UIFactory.SetLayoutWidth(btn.gameObject, btnWidth, btnWidth);
            }

            UIFactory.CreateSpacer(panel.transform, 14, 1f);

            Track(UIFactory.CreateNativeButton("MAIN MENU", panel.transform, 48))
                ?.onClick.AddListener(CloseMenu);
        }

        private void DrawHostSetup()
        {
            bool isSteam = _transportIdx == 1;
            float height = isSteam ? 520f : 500f;
            var panel = CreateMenuPanel(960f, height);
            AddHeader(panel.transform, "HOST GAME", "Create a lobby and wait for peers to join.");
            DrawStatusMessage(panel.transform);

            UIFactory.CreateLabelInputRow("Server Name >", _hostName, panel.transform, val =>
            {
                _hostName = val;
                if (ModConfig.HostServerName != null) ModConfig.HostServerName.Value = val;
            }, 200f);

            if (!isSteam)
            {
                UIFactory.CreateLabelInputRow("Port >", _hostPort, panel.transform, val =>
                {
                    _hostPort = val;
                    if (ModConfig.HostPort != null) ModConfig.HostPort.Value = val;
                }, 200f);
            }

            DrawHostSetupOptions(panel.transform, isSteam);

            UIFactory.CreateSpacer(panel.transform, 18, 1f);

            Track(UIFactory.CreateNativeButton("START SERVER", panel.transform, 48))
                ?.onClick.AddListener(StartHost);

            Track(UIFactory.CreateNativeButton("BACK", panel.transform, 48))
                ?.onClick.AddListener(() => SetScreen(Screen.MainMenu));
        }

        private void DrawHostSetupOptions(Transform parent, bool isSteam)
        {
            Track(UIFactory.CreateLabeledSelector(
                "Player Limit >", BuildPlayerLimitOptions(), MaxPlayersIndex(ModConfig.HostMaxPlayersTotal?.Value ?? 8), parent,
                idx =>
                {
                    if (ModConfig.HostMaxPlayersTotal != null)
                        ModConfig.HostMaxPlayersTotal.Value = PlayerLimitFromIndex(idx);
                    RefreshUI();
                }, labelWidth: 200f));

            if (isSteam)
            {
                string curLobbyType = ModConfig.HostSteamLobbyType?.Value ?? "Public";
                int lobbyTypeIdx = string.Equals(curLobbyType, "FriendsOnly", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                Track(UIFactory.CreateLabeledSelector(
                    "Visibility >", new List<string> { "Public", "Friends Only" }, lobbyTypeIdx, parent,
                    idx =>
                    {
                        string val = idx == 1 ? "FriendsOnly" : "Public";
                        if (ModConfig.HostSteamLobbyType != null)
                            ModConfig.HostSteamLobbyType.Value = val;
                        ModConfig.Save();
                    }, labelWidth: 200f));
            }
        }

        private void DrawDirectConnect()
        {
            var panel = CreateMenuPanel(960f, 430f);
            AddHeader(panel.transform, "DIRECT CONNECT", "Join a lobby by address.");
            DrawStatusMessage(panel.transform);

            UIFactory.CreateLabelInputRow("Address >", _connectIP, panel.transform, val =>
            {
                _connectIP = val;
                if (ModConfig.LastIP != null) ModConfig.LastIP.Value = val;
            });

            UIFactory.CreateLabelInputRow("Port >", _connectPort, panel.transform, val =>
            {
                _connectPort = val;
                if (ModConfig.LastPort != null) ModConfig.LastPort.Value = val;
            });

            UIFactory.CreateSpacer(panel.transform, 18, 1f);

            Track(UIFactory.CreateNativeButton("CONNECT", panel.transform, 48))
                ?.onClick.AddListener(StartJoin);

            Track(UIFactory.CreateNativeButton("BACK", panel.transform, 48))
                ?.onClick.AddListener(() => SetScreen(Screen.MainMenu));
        }

        /// <summary>
        /// Browse available Steam lobbies filtered for TCAMP games.
        /// Auto-refreshes on first entry; shows server name, map, player count, and a JOIN button.
        /// </summary>
        private void DrawSteamLobbyBrowser()
        {
            Log.Info(Tag, "Drawing Steam lobby browser screen");

            if (_lobbyResults == null && !_refreshingLobbies)
            {
                Log.Info(Tag, "No cached results — triggering lobby refresh");
                _ = RefreshLobbyList();
            }

            // Build the panel directly with explicit layout instead of CreateScreen,
            // because the lobby list needs a scrollable region between fixed top/bottom rows.
            var panel = CreateMenuPanel(1200f, 640f);
            AddHeader(panel.transform, "STEAM LOBBIES", "Browse available multiplayer sessions.");

            // ── Top row: REFRESH button (fixed 48px at top) ──────────
            var topRow = UIFactory.CreateHorizontalGroup(panel.transform, 10);
            var topRowLe = topRow.GetComponent<LayoutElement>();
            topRowLe.minHeight = 48f;
            topRowLe.preferredHeight = 48f;
            topRowLe.flexibleHeight = 0f;
            topRow.GetComponent<HorizontalLayoutGroup>().childForceExpandWidth = true;

            var refresh = AddRowButton(topRow.transform, "REFRESH");
            if (refresh != null)
            {
                refresh.interactable = !_refreshingLobbies;
                refresh.onClick.AddListener(async () =>
                {
                    await RefreshLobbyList();
                    // Defer to next frame — avoids NRE from destroying UI tree
                    // while async callback is still on the stack.
                    if (_visible && _currentScreen == Screen.SteamLobbyBrowser)
                        Invoke(nameof(RefreshUI), 0f);
                });
            }

            // ── Bottom row: BACK button (fixed 48px at bottom) ───────
            var bottomRow = UIFactory.CreateHorizontalGroup(panel.transform, 10);
            var bottomRowLe = bottomRow.GetComponent<LayoutElement>();
            bottomRowLe.minHeight = 48f;
            bottomRowLe.preferredHeight = 48f;
            bottomRowLe.flexibleHeight = 0f;
            bottomRow.GetComponent<HorizontalLayoutGroup>().childForceExpandWidth = true;

            AddRowButton(bottomRow.transform, "BACK")
                ?.onClick.AddListener(() => SetScreen(Screen.MainMenu));

            // ── Middle area: lobby list (fills all remaining space) ───
            if (_refreshingLobbies)
            {
                var msg = UIFactory.CreateNativeText(
                    $"<color={DimGreen}>Searching for lobbies...</color>",
                    panel.transform, 18, TextAlignmentOptions.Center);
                msg.GetComponent<LayoutElement>().flexibleHeight = 1f;
            }
            else if (_lobbyResults == null || _lobbyResults.Length == 0)
            {
                var msg = UIFactory.CreateNativeText(
                    $"<color={DimGreen}>No lobbies found. Host a game first, then refresh.</color>",
                    panel.transform, 18, TextAlignmentOptions.Center);
                msg.GetComponent<LayoutElement>().flexibleHeight = 1f;
            }
            else
            {
                // Scrollable lobby list — direct child of the panel VLG
                var scroll = new GameObject("LobbyScroll", typeof(RectTransform), typeof(ScrollRect), typeof(Mask));
                scroll.transform.SetParent(panel.transform, false);
                var scrollLe = scroll.AddComponent<LayoutElement>();
                scrollLe.flexibleHeight = 1f;
                scrollLe.minHeight = 0f;
                scrollLe.preferredHeight = 0f;
                var scrollRt = scroll.GetComponent<RectTransform>();
                scrollRt.anchorMin = Vector2.zero;
                scrollRt.anchorMax = Vector2.one;
                scrollRt.offsetMin = Vector2.zero;
                scrollRt.offsetMax = Vector2.zero;

                var scrollContent = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup));
                scrollContent.transform.SetParent(scroll.transform, false);
                var scrollContentRect = scrollContent.GetComponent<RectTransform>();
                scrollContentRect.anchorMin = new Vector2(0, 1);
                scrollContentRect.anchorMax = new Vector2(1, 1);
                scrollContentRect.pivot = new Vector2(0.5f, 1);
                scrollContentRect.sizeDelta = new Vector2(0, _lobbyResults.Length * 70f);

                var vlg = scrollContent.GetComponent<VerticalLayoutGroup>();
                vlg.childForceExpandWidth = true;
                vlg.childForceExpandHeight = false;
                vlg.childControlWidth = true;
                vlg.childControlHeight = true;
                vlg.spacing = 6f;
                vlg.padding = new RectOffset(6, 6, 6, 6);

                scroll.GetComponent<ScrollRect>().content = scrollContentRect;
                scroll.GetComponent<ScrollRect>().movementType = ScrollRect.MovementType.Clamped;

                foreach (var lobby in _lobbyResults)
                {
                    DrawLobbyEntry(scrollContent.transform, lobby);
                }
            }

            // Don't auto-select any button in the lobby browser
            _lastSelected = null;
        }

        /// <summary>Draw a single lobby row with name, map, player count, version, and a JOIN button.</summary>
        private void DrawLobbyEntry(Transform parent, Steamworks.Data.Lobby lobby)
        {
            // Safely extract lobby metadata — lobby.Owner may throw if the
            // owner left or Steam hasn't fully propagated the data yet.
            string name;
            try { name = lobby.GetData("name"); } catch { name = null; }
            if (string.IsNullOrEmpty(name))
            {
                try { name = lobby.Owner.Name; } catch { }
            }
            if (string.IsNullOrEmpty(name)) name = "Unknown";

            string map;
            try { map = lobby.GetData("map"); } catch { map = null; }
            string version;
            try { version = lobby.GetData("version"); } catch { version = null; }
            if (string.IsNullOrEmpty(version)) version = "?";

            int players = lobby.MemberCount;
            int maxPlayers = lobby.MaxMembers;

            string hostSteamId;
            try { hostSteamId = lobby.GetData("host_steamid"); } catch { hostSteamId = null; }

            // Format map display — show "Loading..." if map metadata isn't set yet
            string mapDisplay = string.IsNullOrEmpty(map) ? "Loading..." : map;

            var entry = UIFactory.CreateNativePanel(parent, 8, 6);
            entry.GetComponent<LayoutElement>().preferredHeight = 60f;

            var row = UIFactory.CreateHorizontalGroup(entry.transform, 8);

            var info = UIFactory.CreateNativeText(
                $"<color={Green}>{name}</color>\n" +
                $"<color={DimGreen}>{mapDisplay}  |  {players}/{maxPlayers} pilots  |  v{version}</color>",
                row.transform, 16, TextAlignmentOptions.MidlineLeft);
            UIFactory.SetFlexible(info.gameObject);

            var joinBtn = Track(UIFactory.CreateNativeButton("JOIN", row.transform, 48));
            if (joinBtn != null)
            {
                // Disable JOIN if: lobby is full, no host_steamid, or it's our own lobby
                bool isOwnLobby = false;
                if (!string.IsNullOrEmpty(hostSteamId) && SteamClient.IsValid)
                {
                    try { isOwnLobby = hostSteamId == SteamClient.SteamId.Value.ToString(); }
                    catch { /* SteamId access failed — treat as not own lobby */ }
                }

                joinBtn.interactable = players < maxPlayers
                    && !string.IsNullOrEmpty(hostSteamId)
                    && !isOwnLobby;
                joinBtn.onClick.AddListener(() =>
                {
                    JoinSteamLobby(hostSteamId, lobby);
                });
            }
        }

        /// <summary>
        /// Query Steam for TCAMP lobbies via the lobby-list API.
        /// Filters for lobbies with <c>game=TCAMP</c> metadata and at
        /// least one free slot. Results are cached in <see cref="_lobbyResults"/>
        /// for display by <see cref="DrawSteamLobbyBrowser"/>.
        /// </summary>
        private async Task RefreshLobbyList()
        {
            if (_refreshingLobbies)
            {
                Log.Debug(Tag, "RefreshLobbyList skipped — already refreshing");
                return;
            }

            Log.Info(Tag, "=== Steam Lobby Browser Refresh ===");

            if (!SteamClient.IsValid)
            {
                Log.Error(Tag, "Steam is not initialized (SteamClient.IsValid=false). " +
                    "Cannot browse lobbies. Launch the game through Steam.");
                _lobbyResults = Array.Empty<Steamworks.Data.Lobby>();
                _refreshingLobbies = false;
                RefreshUI();
                return;
            }

            Log.Info(Tag, $"SteamClient: IsValid=true, SteamId={SteamClient.SteamId}, " +
                $"Name={SteamClient.Name}, AppId={SteamClient.AppId}");

            _refreshingLobbies = true;
            _lobbyResults = null;

            try
            {
                var query = SteamMatchmaking.LobbyList
                    .WithKeyValue("game", "TCAMP")
                    .WithSlotsAvailable(1)
                    .FilterDistanceWorldwide()
                    .WithMaxResults(50);

                Log.Debug(Tag, "Calling RequestAsync() on lobby query...");
                var stopwatch = new System.Diagnostics.Stopwatch();
                stopwatch.Start();
                var lobbies = await query.RequestAsync();
                stopwatch.Stop();

                _lobbyResults = lobbies ?? Array.Empty<Steamworks.Data.Lobby>();
                Log.Info(Tag, $"Query returned in {stopwatch.ElapsedMilliseconds}ms — " +
                    $"found {_lobbyResults.Length} lobbies");

                if (_lobbyResults.Length == 0)
                {
                    Log.Info(Tag, "No TCAMP lobbies found. Possible causes:");
                    Log.Info(Tag, "  - No host is running with 'game'='TCAMP' metadata set");
                    Log.Info(Tag, "  - Host's lobby is FriendsOnly and you are not on their friends list");
                    Log.Info(Tag, "  - Host created the lobby but metadata hasn't propagated yet (wait ~5s and refresh)");
                    Log.Info(Tag, "  - Steam datacenter propagation delay (try refreshing in 10-30s)");
                    Log.Info(Tag, "  - Both client and host are the same Steam account (can't join own lobby via browser)");
                }
                else
                {
                    foreach (var lobby in _lobbyResults)
                    {
                        string name = lobby.GetData("name") ?? "Unknown";
                        string map = lobby.GetData("map") ?? "not-set";
                        string version = lobby.GetData("version") ?? "?";
                        string hostId = lobby.GetData("host_steamid") ?? "not-set";
                        Log.Info(Tag, $"  Lobby: id={lobby.Id}, name='{name}', map='{map}', " +
                            $"version='{version}', host_steamid='{hostId}', " +
                            $"members={lobby.MemberCount}/{lobby.MaxMembers}, " +
                            $"owner={lobby.Owner.Name} (id={lobby.Owner.Id})");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"Failed to list lobbies: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                _lobbyResults = Array.Empty<Steamworks.Data.Lobby>();
            }
            finally
            {
                _refreshingLobbies = false;
            }
        }

        /// <summary>
        /// Join a Steam lobby by extracting the host's SteamId from its metadata.
        /// Validates the lobby's game version before joining and provides
        /// specific error messages for common failure scenarios.
        /// </summary>
        private void JoinSteamLobby(string hostSteamId, Steamworks.Data.Lobby lobby)
        {
            try
            {
                // Validate the host SteamId is present and parseable
                if (string.IsNullOrEmpty(hostSteamId))
                {
                    Log.Error(Tag, "Cannot join lobby: host_steamid metadata is missing. " +
                        "The host may not have initialized the lobby correctly.");
                    return;
                }

                if (!ulong.TryParse(hostSteamId, out ulong steamId))
                {
                    Log.Error(Tag, $"Cannot join lobby: host_steamid '{hostSteamId}' is not a valid SteamId.");
                    return;
                }

                // Check version compatibility
                string remoteVersion = lobby.GetData("version") ?? "";
                string localVersion = "1.0"; // Must match the version set in SteamP2PTransport.SetLobbyMetadata
                if (!string.IsNullOrEmpty(remoteVersion) && remoteVersion != localVersion)
                {
                    Log.Warning(Tag, $"Lobby version mismatch: host has '{remoteVersion}', " +
                        $"local is '{localVersion}'. Join may fail if the protocol differs.");
                }

                // Check if the lobby is full
                if (lobby.MemberCount >= lobby.MaxMembers)
                {
                    Log.Warning(Tag, $"Lobby is full: {lobby.MemberCount}/{lobby.MaxMembers}. " +
                        "The join button should have been disabled.");
                    return;
                }

                // Check if we're trying to join our own lobby
                if (steamId == SteamClient.SteamId.Value)
                {
                    Log.Warning(Tag, "Cannot join your own lobby via the browser. " +
                        "You are already the host.");
                    return;
                }

                // Ensure we're using SteamP2PTransport for Steam lobbies
                if (!(_connection.Config is null) && _transportIdx == 1)
                {
                    var steamTransport = new SteamP2PTransport(_connection.Config);
                    _connection.SetTransport(steamTransport);
                }

                _connection.JoinSession(steamId.ToString(), 0);
                var local = _connection.Session?.GetLocalPlayer();
                if (local != null) local.PlayerName = _username;
                SetScreen(Screen.Lobby);
                Log.Info(Tag, $"Joining Steam lobby {lobby.Id} (host: {hostSteamId}, " +
                    $"map={lobby.GetData("map") ?? "unknown"}, " +
                    $"version={remoteVersion})");
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"Failed to join Steam lobby: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void DrawLobby()
        {
            var session = _connection?.Session;
            bool isHost = _connection?.IsHost ?? false;
            EnsureLobbyDefaults(session);

            var shell = CreateMenuPanel(1200f, 860f);
            AddHeader(shell.transform, "MULTIPLAYER LOBBY", BuildLobbySubtitle(session, isHost));
            DrawStatusMessage(shell.transform);

            if (!isHost && (_modMismatch != null || _modSyncInProgress))
            {
                DrawModSyncPanel(shell.transform);
                return;
            }

            var body = UIFactory.CreateHorizontalGroup(shell.transform, 16);
            UIFactory.SetFlexible(body, 1f, 1f);
            body.GetComponent<LayoutElement>().minHeight = 500f;

            var roster = UIFactory.CreateNativePanel(body.transform, 18, 7);
            roster.name = "Roster";
            UIFactory.SetLayoutWidth(roster, 370f, 370f);
            DrawRoster(roster.transform, session);

            var setup = UIFactory.CreateNativePanel(body.transform, 18, 7);
            setup.name = "Setup";
            UIFactory.SetFlexible(setup, 1f, 1f);
            DrawLobbySetup(setup.transform, session, isHost);

            UIFactory.CreateDivider(shell.transform);

            var buttons = UIFactory.CreateHorizontalGroup(shell.transform, 12);
            buttons.GetComponent<HorizontalLayoutGroup>().childForceExpandWidth = true;

            bool gameInProgress = _lobby?.HostGameInProgress ?? false;
            bool localReady = session?.GetLocalPlayer()?.IsReady ?? false;
            bool canReady = CanReady(session) && !gameInProgress;
            var ready = Track(UIFactory.CreateNativeButton(localReady ? "NOT READY" : "READY", buttons.transform, 48));
            if (ready != null)
            {
                ready.interactable = canReady;
                ready.onClick.AddListener(() =>
                {
                    _lobby?.ToggleReady();
                    RefreshUI();
                });
            }

            if (isHost)
            {
                var start = Track(UIFactory.CreateNativeButton("START GAME", buttons.transform, 48));
                if (start != null)
                {
                    start.interactable = AllRequiredPlayersReady(session);
                    start.onClick.AddListener(() => _lobby?.StartGame());
                }
            }

            Track(UIFactory.CreateNativeButton("LEAVE", buttons.transform, 48))
                ?.onClick.AddListener(() =>
                {
                    _connection?.Disconnect();
                    CloseMenu();
                });

            if (gameInProgress)
            {
                var banner = UIFactory.CreateNativeText(
                    $"<color={Green}>GAME IN PROGRESS — WAITING FOR HOST TO RETURN TO LOBBY.</color>",
                    shell.transform, 16, TextAlignmentOptions.Center);
                banner.GetComponent<LayoutElement>().preferredHeight = 26f;
            }
            else if (!canReady)
            {
                string warningText;
                if (HasUnverifiedPeers(session))
                    warningText = isHost
                        ? "WAITING FOR PEER MOD VERIFICATION."
                        : "WAITING FOR MOD VERIFICATION.";
                else
                    warningText = ShowTeamSelection(session)
                        ? "SELECT AIRCRAFT, AIRFIELD, AND TEAM BEFORE READYING."
                        : "SELECT AIRCRAFT AND AIRFIELD BEFORE READYING.";
                var warning = UIFactory.CreateNativeText(
                    $"<color={Green}>{warningText}</color>",
                    shell.transform, 16, TextAlignmentOptions.Center);
                warning.GetComponent<LayoutElement>().preferredHeight = 26f;
            }
        }

        // ── Lobby Drawing ───────────────────────────────────────────────

        private void DrawRoster(Transform parent, GameSession session)
        {
            UIFactory.CreateNativeText("PILOTS >", parent, 20, TextAlignmentOptions.Left);
            UIFactory.CreateDivider(parent, 0.16f);

            if (session == null || session.Players.Count == 0)
            {
                UIFactory.CreateNativeText($"<color={DimGreen}>NO PILOTS CONNECTED.</color>", parent, 17, TextAlignmentOptions.Left);
                return;
            }

            bool showTeams = ShowTeamSelection(session);
            foreach (var player in session.Players.Values.OrderByDescending(p => p.IsHost).ThenBy(p => p.PlayerName))
            {
                DrawPlayerEntry(parent, player, showTeams);
            }
        }

        private void DrawPlayerEntry(Transform parent, PlayerInfo player, bool showTeam)
        {
            var entry = UIFactory.CreateNativePanel(parent, 12, 4);
            entry.name = "PilotEntry";
            entry.GetComponent<Image>().color = player.IsReady
                ? new Color(0f, 0.16f, 0.07f, 0.58f)
                : new Color(0f, 0f, 0f, 0.38f);

            var row = UIFactory.CreateHorizontalGroup(entry.transform, 8);
            var name = player.PlayerName ?? "Player";
            var title = player.IsHost
                ? $"{name}  <color={DimGreen}>[HOST]</color>"
                : name;
            var nameText = UIFactory.CreateNativeText(title, row.transform, 18, TextAlignmentOptions.MidlineLeft);
            UIFactory.SetFlexible(nameText.gameObject);

            var stateText = player.IsReady ? $"<color={Green}>READY</color>" : $"<color={DimGreen}>WAIT</color>";
            if (!player.IsHost && player.IsModSyncing)
                stateText = $"<color={DimGreen}>SYNC</color>";
            else if (!player.IsHost && !player.IsModsVerified)
                stateText = $"<color={DimGreen}>VERIFY</color>";
            var status = UIFactory.CreateNativeText(stateText, row.transform, 16, TextAlignmentOptions.MidlineRight);
            UIFactory.SetLayoutWidth(status.gameObject, 96f, 96f);

            string aircraft = string.IsNullOrEmpty(player.SelectedAircraft) ? "No aircraft" : player.SelectedAircraft;
            string airfield = string.IsNullOrEmpty(player.SelectedAirfield) ? "No airfield" : player.SelectedAirfield;
            string detail = $"{aircraft}  /  {airfield}";
            if (!player.IsHost && player.IsModSyncing)
                detail = "Syncing Mods folder with host";
            else if (!player.IsHost && !player.IsModsVerified)
                detail = "Checking Mods folder";
            if (showTeam)
            {
                string team = player.Team == MultiplayerTeam.None ? "No team" : GetTeamDisplayName(player.Team);
                if (player.IsModsVerified)
                    detail = $"{detail}  /  {team}";
            }
            UIFactory.CreateNativeText(
                $"<color={DimGreen}>{detail}</color>",
                entry.transform, 14, TextAlignmentOptions.Left);
        }

        private void DrawLobbySetup(Transform parent, GameSession session, bool isHost)
        {
            UIFactory.CreateNativeText("MISSION SETUP >", parent, 20, TextAlignmentOptions.Left);
            UIFactory.CreateDivider(parent, 0.16f);

            var mapNames = MapHelper.GetSelectableMapNames();
            string curMap = session?.MapName ?? MapHelper.GetDefaultMapName();
            int selectedMapIdx = IndexOfOrZero(mapNames, curMap);
            var mapDisplayNames = mapNames.Select(MapHelper.GetMapDisplayName).ToList();
            var mapButton = Track(UIFactory.CreateLabeledSelector(
                "Map >", mapDisplayNames, selectedMapIdx, parent,
                isHost ? idx =>
                {
                    _lobby?.SetMap(mapNames[idx]);
                    if (ModConfig.LastAirfield != null)
                        ModConfig.LastAirfield.Value = MapHelper.GetDefaultAirfieldName(mapNames[idx]);
                    RefreshUI();
                } : (Action<int>)null));
            if (mapButton != null)
            {
                if (isHost)
                {
                    mapButton.onClick.RemoveAllListeners();
                    mapButton.onClick.AddListener(() =>
                    {
                        UIFactory.ShowNativeMapSelector(curMap, selectedMap =>
                        {
                            _lobby?.SetMap(selectedMap);
                            if (ModConfig.LastAirfield != null)
                                ModConfig.LastAirfield.Value = MapHelper.GetDefaultAirfieldName(selectedMap);
                            RefreshUI();
                        });
                    });
                }
                else
                {
                    mapButton.interactable = false;
                }
            }

            var spawnTypes = new List<string> { "Air Start", "Runway", "Ramp" };
            int spawnIdx = Mathf.Clamp((int)(session?.SpawnType ?? Core.LobbySpawnType.Runway), 0, spawnTypes.Count - 1);
            var spawnButton = Track(UIFactory.CreateLabeledSelector(
                "Start >", spawnTypes, spawnIdx, parent,
                isHost ? idx =>
                {
                    _lobby?.SetSpawnType((Core.LobbySpawnType)idx);
                    if (ModConfig.HostSpawnType != null) ModConfig.HostSpawnType.Value = idx;
                    RefreshUI();
                } : (Action<int>)null));
            if (spawnButton != null && !isHost) spawnButton.interactable = false;

            var times = new List<string> { "Dawn", "Morning", "Noon", "Afternoon", "Evening", "Night" };
            int timeIdx = Mathf.Clamp((int)(session?.TimeOfDay ?? TimeOfDaySetting.Morning), 0, times.Count - 1);
            var timeButton = Track(UIFactory.CreateLabeledSelector(
                "Time >", times, timeIdx, parent,
                isHost ? idx =>
                {
                    _lobby?.SetTimeOfDay((TimeOfDaySetting)idx);
                    if (ModConfig.HostTimeOfDay != null) ModConfig.HostTimeOfDay.Value = idx;
                } : (Action<int>)null));
            if (timeButton != null && !isHost) timeButton.interactable = false;

            var modes = BuildModeOptions();
            int modeIdx = ModeIndex(session?.GameMode ?? MultiplayerGameMode.FreeForAllDogfight);
            var modeButton = Track(UIFactory.CreateLabeledSelector(
                "Mode >", modes, modeIdx, parent,
                isHost ? idx =>
                {
                    var mode = (MultiplayerGameMode)idx;
                    _lobby?.SetGameMode(mode);
                    if (ModConfig.HostGameMode != null) ModConfig.HostGameMode.Value = idx;
                    RefreshUI();
                } : (Action<int>)null));
            if (modeButton != null && !isHost) modeButton.interactable = false;

            if (ShowTeamCountSelection(session))
            {
                var teamCounts = BuildTeamCountOptions(session.PlayerCount);
                int teamCountIdx = TeamCountIndex(session?.TeamCount ?? 2, session.PlayerCount);
                var teamCountButton = Track(UIFactory.CreateLabeledSelector(
                    "Teams >", teamCounts, teamCountIdx, parent,
                    isHost ? idx =>
                    {
                        int teamCount = TeamCountFromIndex(idx, session.PlayerCount);
                        _lobby?.SetTeamCount(teamCount);
                        if (ModConfig.HostTeamCount != null) ModConfig.HostTeamCount.Value = teamCount;
                        RefreshUI();
                    } : (Action<int>)null));
                if (teamCountButton != null && !isHost) teamCountButton.interactable = false;

                DrawTeamSelector(parent, session);
            }

            if (isHost)
            {
                var toggle = UIFactory.CreateLabeledToggle(
                    "Collisions >", session?.AircraftCollisionsEnabled ?? false, parent);
                if (toggle != null)
                {
                    toggle.onValueChanged.AddListener(v =>
                    {
                        _lobby?.SetCollisions(v);
                        if (ModConfig.HostAircraftCollisions != null)
                            ModConfig.HostAircraftCollisions.Value = v;
                    });
                }
            }
            else
            {
                UIFactory.CreateNativeText(
                    $"Collisions > <color={Green}>{((session?.AircraftCollisionsEnabled ?? false) ? "ON" : "OFF")}</color>",
                    parent, 17, TextAlignmentOptions.Left);
            }

            DrawAircraftSelectors(parent, session, curMap);
        }

        private void DrawTeamSelector(Transform parent, GameSession session)
        {
            var local = session?.GetLocalPlayer();
            if (local == null) return;

            var teams = new List<string>();
            int teamCount = GameSession.ClampTeamCount(session.TeamCount);
            for (int i = 1; i <= teamCount; i++)
                teams.Add(GetTeamDisplayName((MultiplayerTeam)i));

            int selectedTeamIdx = local.Team == MultiplayerTeam.None
                ? 0
                : Mathf.Clamp((int)local.Team - 1, 0, teams.Count - 1);

            Track(UIFactory.CreateLabeledSelector("Team >", teams, selectedTeamIdx, parent, idx =>
            {
                _lobby?.SetLocalTeam((MultiplayerTeam)(idx + 1));
                RefreshUI();
            }));
        }

        private void DrawAircraftSelectors(Transform parent, GameSession session, string curMap)
        {
            string curAircraft = session?.GetLocalPlayer()?.SelectedAircraft
                ?? ModConfig.LastAircraft?.Value ?? "AV8B";
            var aircraftNames = LoadoutHelper.GetAircraftNames();

            string curLoadout = session?.GetLocalPlayer()?.SelectedLoadout
                ?? ModConfig.LastLoadout?.Value ?? "Clean";
            if (aircraftNames.Count > 0)
            {
                var loadoutButton = Track(UIFactory.CreateLabeledButton(
                    "Aircraft / Loadout >", GetLoadoutButtonText(curAircraft, curLoadout), parent, () =>
                {
                    UIFactory.ShowNativeLoadoutSelector(curAircraft, aircraftNames, curLoadout, "", (aircraft, loadout, ammoBelt) =>
                    {
                        if (!string.IsNullOrEmpty(aircraft))
                        {
                            _lobby?.SetAircraft(aircraft);
                            if (ModConfig.LastAircraft != null) ModConfig.LastAircraft.Value = aircraft;
                        }

                        if (!string.IsNullOrEmpty(loadout))
                        {
                            _lobby?.SetLoadout(loadout);
                            if (ModConfig.LastLoadout != null) ModConfig.LastLoadout.Value = loadout;
                        }

                        RefreshUI();
                    });
                }));
            }

            string curAirfield = session?.GetLocalPlayer()?.SelectedAirfield
                ?? ModConfig.LastAirfield?.Value ?? "";
            var airfieldList = MapHelper.GetAirfieldNames(curMap);
            if (airfieldList.Count > 0)
            {
                if (!MapHelper.IsAirfieldOnMap(curMap, curAirfield))
                    curAirfield = MapHelper.GetDefaultAirfieldName(curMap);
                var airfieldDisplayNames = airfieldList.Select(a => MapHelper.GetAirfieldDisplayName(curMap, a)).ToList();
                int selectedAirfieldIdx = IndexOfOrZero(airfieldList, curAirfield);
                Track(UIFactory.CreateLabeledSelector("Airfield >", airfieldDisplayNames, selectedAirfieldIdx, parent, idx =>
                {
                    _lobby?.SetAirfield(airfieldList[idx]);
                    if (ModConfig.LastAirfield != null) ModConfig.LastAirfield.Value = airfieldList[idx];
                }));
            }
            else
            {
                UIFactory.CreateNativeText($"<color={Green}>NO AIRFIELDS FOUND.</color>", parent, 16, TextAlignmentOptions.Left);
            }
        }

        // ── Actions ────────────────────────────────────────────────────

        private void StartHost()
        {
            if (_connection == null)
            {
                Log.Error(Tag, "StartHost called but ConnectionManager is null");
                return;
            }

            int port = 7777;
            if (_transportIdx == 0)
            {
                int.TryParse(_hostPort, out port);
                if (port <= 0) port = 7777;
            }

            // Log the state before hosting
            string mode = _transportIdx == 1 ? "SteamLobby" : "DirectIP";
            string lobbyType = "";
            if (_transportIdx == 1)
            {
                lobbyType = ModConfig.HostSteamLobbyType?.Value?.Trim() ?? "Public";
                Log.Info(Tag, $"=== Starting Steam host ===");
                Log.Info(Tag, $"Transport: SteamLobby, serverName={_hostName}, lobbyType={lobbyType}");
                Log.Info(Tag, $"Config: MaxPlayers={ModConfig.HostMaxPlayersTotal?.Value ?? 8}, GameMode={ModConfig.HostGameMode?.Value ?? 0}");
                Log.Info(Tag, $"SteamClient: IsValid={SteamClient.IsValid}, SteamId={SteamClient.SteamId}");

                // Ensure we're using SteamP2PTransport for Steam lobbies
                if (!(_connection.Config is null))
                {
                    var steamTransport = new SteamP2PTransport(_connection.Config);
                    _connection.SetTransport(steamTransport);
                }
            }
            else
            {
                Log.Info(Tag, $"=== Starting DirectIP host ===");
                Log.Info(Tag, $"Transport: DirectIP, serverName={_hostName}, port={port}");

                // Ensure we're using DirectUdpTransport for DirectIP
                if (!(_connection.Config is null))
                {
                    var directTransport = new DirectUdpTransport(_connection.Config);
                    _connection.SetTransport(directTransport);
                }
            }

            try
            {
                ApplyHostConfigToTransport();
                ApplyNetworkConfigToTransport();
                _connection.HostSession(_hostName, port);
                var local = _connection.Session?.GetLocalPlayer();
                if (local != null) local.PlayerName = _username;
                SetScreen(Screen.Lobby);
                Log.Info(Tag, $"Host session created: mode={mode}, name={_hostName}, port={port}");
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"Host failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void StartJoin()
        {
            if (_connection == null) return;

            int.TryParse(_connectPort, out int port);
            if (port <= 0) port = 7777;

            // Ensure we're using DirectUdpTransport for direct connections
            if (_transportIdx == 0 && !(_connection.Config is null))
            {
                var directTransport = new DirectUdpTransport(_connection.Config);
                _connection.SetTransport(directTransport);
            }

            try
            {
                ApplyNetworkConfigToTransport();
                _connection.JoinSession(_connectIP, port);
                var local = _connection.Session?.GetLocalPlayer();
                if (local != null) local.PlayerName = _username;
                SetScreen(Screen.Lobby);
                Log.Info(Tag, $"Joining {_connectIP}:{port}");
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"Join failed: {ex}");
            }
        }

        public void CloseMenu()
        {
            if (_root != null) _root.SetActive(false);
            if (_canvas != null) _canvas.enabled = false;
            _visible = false;
            _currentScreen = _connection != null && _connection.IsConnected ? Screen.Lobby : Screen.MainMenu;
            RestoreCursor();
            OnMenuClosed?.Invoke();
        }

        // ── Helpers ────────────────────────────────────────────────────

        private GameObject CreateMenuPanel(float width, float height)
        {
            var panel = UIFactory.CreateNativePanel(_contentRoot.transform, 28, 12);
            var rect = panel.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(width, height);
            return panel;
        }

        // ── Unified Layout Helpers ────────────────────────────────────────

        /// <summary>
        /// Creates a standard screen panel with header, content area, and optional bottom button row.
        /// Content area fills remaining space. Returns (panel, contentRoot, bottomRow).
        /// </summary>
        private (GameObject panel, GameObject contentRoot, GameObject bottomRow) CreateScreen(
            float width, float height, string title, string subtitle = "")
        {
            var panel = CreateMenuPanel(width, height);
            AddHeader(panel.transform, title, subtitle);

            // Content area - fills remaining space
            var contentRoot = UIFactory.CreateVerticalGroup(panel.transform, 10);
            var contentLe = contentRoot.GetComponent<LayoutElement>();
            contentLe.flexibleHeight = 1f;
            contentLe.minHeight = 0f;
            contentLe.preferredHeight = 0f;

            // Bottom button row (fixed height) - optional
            var bottomRow = UIFactory.CreateHorizontalGroup(panel.transform, 10);
            bottomRow.GetComponent<HorizontalLayoutGroup>().childForceExpandWidth = true;
            var bottomRowLe = bottomRow.GetComponent<LayoutElement>();
            bottomRowLe.minHeight = 48f;
            bottomRowLe.preferredHeight = 48f;
            bottomRowLe.flexibleHeight = 0f;

            return (panel, contentRoot, bottomRow);
        }

        /// <summary>Adds a fixed-height button to a horizontal row.</summary>
        private Button AddRowButton(Transform row, string label, float width = 397f)
        {
            var btn = Track(UIFactory.CreateNativeButton(label, row, 48));
            if (btn != null)
                UIFactory.SetLayoutWidth(btn.gameObject, width, width);
            return btn;
        }

        private void AddHeader(Transform parent, string title, string subtitle)
        {
            UIFactory.CreateNativeText(title, parent, 28, TextAlignmentOptions.Left);
            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                var desc = UIFactory.CreateNativeText(
                    $"<color={DimGreen}>{subtitle}</color>", parent, 16, TextAlignmentOptions.Left);
                desc.GetComponent<LayoutElement>().preferredHeight = 30f;
            }
            UIFactory.CreateDivider(parent, 0.18f);
        }

        private void DrawModSyncPanel(Transform parent)
        {
            UIFactory.CreateSpacer(parent, 8);

            var panel = UIFactory.CreateNativePanel(parent, 22, 10);
            UIFactory.SetFlexible(panel, 1f, 1f);

            string title = _modSyncInProgress ? "SYNCING MOD FILES" : "MOD FILES DO NOT MATCH";
            UIFactory.CreateNativeText($"<color={Green}>{title}</color>", panel.transform, 22, TextAlignmentOptions.Center);
            UIFactory.CreateDivider(panel.transform, 0.14f);

            if (_modSyncInProgress)
            {
                DrawModSyncProgress(panel.transform);
                UIFactory.CreateSpacer(panel.transform, 12, 1f);
                var cancel = Track(UIFactory.CreateNativeButton("CANCEL JOIN", panel.transform, 46));
                if (cancel != null)
                {
                    cancel.onClick.AddListener(() =>
                    {
                        _connection?.Disconnect();
                        ResetModSyncState();
                        SetScreen(Screen.MainMenu);
                    });
                }
                return;
            }

            string summary = BuildModMismatchSummary(_modMismatch);
            var detailText = UIFactory.CreateNativeText(
                $"<color={DimGreen}>{summary}</color>",
                panel.transform, 16, TextAlignmentOptions.Center);
            detailText.GetComponent<LayoutElement>().preferredHeight = 72f;

            bool canSync = _modMismatch?.CanSync == true;
            string note = canSync
                ? "Your Mods folder can be updated from the host."
                : "This mismatch includes blocked files. Match the host manually.";
            var noteText = UIFactory.CreateNativeText(
                $"<color={DimGreen}>{note}</color>",
                panel.transform, 15, TextAlignmentOptions.Center);
            noteText.GetComponent<LayoutElement>().preferredHeight = 30f;

            UIFactory.CreateSpacer(panel.transform, 8, 1f);

            var actions = UIFactory.CreateHorizontalRow(panel.transform, 52, 12);
            actions.GetComponent<HorizontalLayoutGroup>().childForceExpandWidth = true;

            var sync = Track(UIFactory.CreateNativeButton(canSync ? "SYNC FROM HOST" : "SYNC UNAVAILABLE", actions.transform, 52));
            if (sync != null)
            {
                UIFactory.SetFlexible(sync.gameObject);
                sync.interactable = canSync;
                StyleModSyncPrimaryAction(sync, canSync);
                sync.onClick.AddListener(ShowModSyncWarning);
            }

            var cancelButton = Track(UIFactory.CreateNativeButton("CANCEL JOIN", actions.transform, 52));
            if (cancelButton != null)
            {
                UIFactory.SetFlexible(cancelButton.gameObject);
                StyleModSyncSecondaryAction(cancelButton);
                cancelButton.onClick.AddListener(() =>
                {
                    _modMismatch = null;
                    ResetModSyncState();
                    _connection?.Disconnect();
                    SetScreen(Screen.MainMenu);
                });
            }
        }

        private static void StyleModSyncPrimaryAction(Button button, bool enabled)
        {
            if (button == null) return;

            var colors = button.colors;
            colors.normalColor = enabled ? ModSyncPrimaryFill : ModSyncDisabledFill;
            colors.highlightedColor = enabled ? ModSyncPrimaryHover : ModSyncDisabledFill;
            colors.selectedColor = enabled ? ModSyncPrimaryHover : ModSyncDisabledFill;
            colors.pressedColor = enabled ? ModSyncPrimaryPressed : ModSyncDisabledFill;
            colors.disabledColor = ModSyncDisabledFill;
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            var image = button.GetComponent<Image>();
            if (image != null)
                image.color = enabled ? ModSyncPrimaryFill : ModSyncDisabledFill;

            foreach (var tmp in button.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                tmp.color = enabled ? UIFactory.AccentColor : UIFactory.MutedTextColor;
                tmp.fontStyle |= FontStyles.Bold;
            }
        }

        private static void StyleModSyncSecondaryAction(Button button)
        {
            if (button == null) return;

            var colors = button.colors;
            colors.normalColor = ModSyncSecondaryFill;
            colors.highlightedColor = ModSyncSecondaryHover;
            colors.selectedColor = ModSyncSecondaryHover;
            colors.pressedColor = ModSyncSecondaryPressed;
            colors.disabledColor = ModSyncDisabledFill;
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            var image = button.GetComponent<Image>();
            if (image != null)
                image.color = ModSyncSecondaryFill;

            foreach (var tmp in button.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                tmp.color = UIFactory.MutedTextColor;
                tmp.fontStyle = FontStyles.Normal;
            }
        }

        private void DrawModSyncProgress(Transform parent)
        {
            string progress = _modSyncTotal > 0
                ? $"{_modSyncReceived}/{_modSyncTotal}"
                : "Preparing transfer";
            var status = UIFactory.CreateNativeText(
                $"<color={Green}>{progress}</color>",
                parent, 34, TextAlignmentOptions.Center);
            status.GetComponent<LayoutElement>().preferredHeight = 54f;

            float pct = _modSyncTotal > 0
                ? Mathf.Clamp01((float)_modSyncReceived / Mathf.Max(1, _modSyncTotal))
                : 0f;
            DrawProgressBar(parent, pct);

            string detail = string.IsNullOrWhiteSpace(_statusMessage)
                ? "Receiving files from host..."
                : _statusMessage;
            var detailText = UIFactory.CreateNativeText(
                $"<color={DimGreen}>{detail}</color>",
                parent, 16, TextAlignmentOptions.Center);
            detailText.GetComponent<LayoutElement>().preferredHeight = 52f;
        }

        private static void DrawProgressBar(Transform parent, float pct)
        {
            var outer = new GameObject("ModSyncProgress", typeof(RectTransform), typeof(Image));
            outer.transform.SetParent(parent, false);
            outer.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.7f);
            UIFactory.AddNativeBorder(outer, 0.8f);
            var outerLayout = outer.AddComponent<LayoutElement>();
            outerLayout.minHeight = 24f;
            outerLayout.preferredHeight = 24f;

            var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fill.transform.SetParent(outer.transform, false);
            fill.GetComponent<Image>().color = new Color(0f, 1f, 0.25f, 0.55f);
            var rect = fill.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = new Vector2(pct, 1f);
            rect.offsetMin = new Vector2(2f, 2f);
            rect.offsetMax = new Vector2(-2f, -2f);
        }

        private void DrawStatusMessage(Transform parent)
        {
            string message = !string.IsNullOrWhiteSpace(_statusMessage)
                ? _statusMessage
                : _externalStatusProvider?.Invoke();
            if (string.IsNullOrWhiteSpace(message))
                return;

            var text = UIFactory.CreateNativeText(
                $"<color={Green}>{message}</color>",
                parent, 15, TextAlignmentOptions.Left);
            text.GetComponent<LayoutElement>().preferredHeight = 24f;
        }

        private bool UpdateModSyncProgress(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            const string prefix = "Receiving mod sync ";
            int start = message.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return false;

            start += prefix.Length;
            int slash = message.IndexOf('/', start);
            if (slash < 0)
                return false;

            int end = message.IndexOf('.', slash);
            if (end < 0)
                end = message.Length;

            if (!int.TryParse(message.Substring(start, slash - start), out int received)
                || !int.TryParse(message.Substring(slash + 1, end - slash - 1), out int total))
            {
                return false;
            }

            _modSyncInProgress = true;
            _modSyncReceived = Mathf.Max(0, received);
            _modSyncTotal = Mathf.Max(0, total);
            return true;
        }

        private void ResetModSyncState()
        {
            _modSyncInProgress = false;
            _modSyncReceived = 0;
            _modSyncTotal = 0;
            _lastModSyncUiRefreshTime = 0f;
        }

        private void ShowModSyncWarning()
        {
            if (_modSyncInProgress || _modMismatch?.CanSync != true)
                return;

            UIFactory.ShowConfirmDialog(
                "SYNC MODS FROM HOST?",
                ModSyncWarning,
                "SYNC ANYWAY",
                "CANCEL",
                StartModSyncFromHost,
                destructive: true);
        }

        private void StartModSyncFromHost()
        {
            if (_modSyncInProgress || _modManifest == null)
                return;

            _modSyncInProgress = true;
            _modSyncReceived = 0;
            _modSyncTotal = 0;
            _statusMessage = "Requesting mod sync from host...";
            _modMismatch = null;
            _modManifest.RequestSyncFromHost();
            RefreshUI();
        }

        private static string BuildModMismatchSummary(ModManifestCollector.ModMismatchInfo info)
        {
            var diff = info?.Diff;
            if (diff == null)
                return info?.Reason ?? "Host and client Mods folders differ.";

            var parts = new List<string>();
            if (diff.Missing.Count > 0)
                parts.Add($"{diff.Missing.Count} missing");
            if (diff.Changed.Count > 0)
                parts.Add($"{diff.Changed.Count} changed");
            if (diff.Extra.Count > 0)
                parts.Add($"{diff.Extra.Count} extra");
            if (diff.Unsyncable.Count > 0)
                parts.Add($"{diff.Unsyncable.Count} blocked");

            return parts.Count == 0
                ? "Host and client Mods folders differ."
                : "Mods folder differs: " + string.Join(", ", parts.ToArray()) + ".";
        }

        private Button Track(Button button)
        {
            if (button != null && _lastSelected == null && button.interactable)
                _lastSelected = button.gameObject;
            return button;
        }

        private void SelectFirstAvailable()
        {
            if (EventSystem.current == null) return;

            var selected = _lastSelected;
            var selectable = selected != null ? selected.GetComponent<Selectable>() : null;
            if (selectable == null || !selectable.IsInteractable())
            {
                selected = null;
                foreach (var candidate in _contentRoot.GetComponentsInChildren<Selectable>(true))
                {
                    if (candidate != null && candidate.gameObject.activeInHierarchy && candidate.IsInteractable())
                    {
                        selected = candidate.gameObject;
                        break;
                    }
                }
            }

            if (selected == null) return;
            EventSystem.current.SetSelectedGameObject(null);
            EventSystem.current.SetSelectedGameObject(selected);
        }

        private void UnlockCursorForMenu(bool capturePrevious = true)
        {
            if (capturePrevious)
            {
                _previousCursorLockState = Cursor.lockState;
            }

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void RestoreCursor()
        {
            // Restore the lock state only; visible stays true like the native
            // game (Locked hides the pointer on its own). Never re-lock when
            // there is no flight to return to — that left menus cursorless.
            bool inFlight = _connection?.Session?.StateMachine.CurrentState == GameState.InGame;
            Cursor.lockState = inFlight ? _previousCursorLockState : CursorLockMode.None;
            Cursor.visible = true;
        }

        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null)
                return;

            var eventSystemGo = new GameObject("TCAMP_EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            DontDestroyOnLoad(eventSystemGo);
            eventSystemGo.hideFlags = HideFlags.HideAndDontSave;
        }

        private static int IndexOfOrZero(IList<string> values, string current)
        {
            if (values == null || values.Count == 0) return 0;
            int index = values.IndexOf(current);
            return index >= 0 ? index : 0;
        }

        private static List<string> BuildPlayerLimitOptions()
        {
            var values = new List<string>();
            for (int i = 2; i <= 8; i++)
                values.Add($"{i} Players");
            return values;
        }

        private static int MaxPlayersIndex(int maxPlayers)
        {
            return Mathf.Clamp(GameSession.ClampMaxPlayersTotal(maxPlayers) - 2, 0, 6);
        }

        private static int PlayerLimitFromIndex(int index)
        {
            return GameSession.ClampMaxPlayersTotal(Mathf.Clamp(index, 0, 6) + 2);
        }

        private static List<string> BuildModeOptions()
        {
            return new List<string> { "FFA Dogfight", "Team Dogfight" };
        }

        private static int ModeIndex(MultiplayerGameMode mode)
        {
            return Mathf.Clamp((int)mode, 0, 1);
        }

        private static List<string> BuildTeamCountOptions(int playerCount)
        {
            int maxTeams = Mathf.Clamp(playerCount, 2, 4);
            var values = new List<string>();
            for (int i = maxTeams; i >= 2; i--)
                values.Add($"{i} Teams");
            return values;
        }

        private static int TeamCountIndex(int teamCount, int playerCount)
        {
            int maxTeams = Mathf.Clamp(playerCount, 2, 4);
            int clampedTeamCount = GameSession.ClampTeamCountForPlayers(teamCount, playerCount);
            return Mathf.Clamp(maxTeams - clampedTeamCount, 0, maxTeams - 2);
        }

        private static int TeamCountFromIndex(int index, int playerCount)
        {
            int maxTeams = Mathf.Clamp(playerCount, 2, 4);
            return GameSession.ClampTeamCountForPlayers(maxTeams - Mathf.Clamp(index, 0, maxTeams - 2), playerCount);
        }

        private static bool ShowTeamCountSelection(GameSession session)
        {
            return session != null
                && session.GameMode == MultiplayerGameMode.TeamDogfight
                && session.PlayerCount >= 3;
        }

        private static bool ShowTeamSelection(GameSession session)
        {
            return session != null
                && session.GameMode == MultiplayerGameMode.TeamDogfight
                && session.PlayerCount >= 3;
        }

        private void ApplyHostConfigToTransport()
        {
            if (_connection?.Config == null) return;
            int maxPlayers = GameSession.ClampMaxPlayersTotal(ModConfig.HostMaxPlayersTotal?.Value ?? 8);
            _connection.Config.MaxConnections = Math.Max(0, maxPlayers - 1);
        }

        private void ApplyNetworkConfigToTransport()
        {
            if (_connection?.Config == null) return;

            _connection.Config.LocalBindAddress = ModConfig.LocalBindAddress?.Value ?? "";
            _connection.Config.AutoVpnBind = true;
            _connection.Config.ModVersion = PluginMetadata.Version;

            bool lowBandwidth = ModConfig.LowBandwidthMode?.Value ?? false;
            if (lowBandwidth)
            {
                _connection.Config.KeepaliveInterval = 1.0f;
                _connection.Config.TimeoutSeconds = 20.0f;
                _connection.Config.ReconnectGraceSeconds = 90.0f;
                _connection.Config.EndpointRefreshInterval = 3.0f;
                _connection.Config.RetransmitInterval = 0.35f;
                _connection.Config.MaxRetransmitAttempts = 180;
                _connection.Config.MaxReliableRetransmitsPerUpdate = 4;
            }
            else
            {
                _connection.Config.KeepaliveInterval = 2.0f;
                _connection.Config.TimeoutSeconds = 10.0f;
                _connection.Config.ReconnectGraceSeconds = 30.0f;
                _connection.Config.EndpointRefreshInterval = 5.0f;
                _connection.Config.RetransmitInterval = 0.25f;
                _connection.Config.MaxRetransmitAttempts = 120;
                _connection.Config.MaxReliableRetransmitsPerUpdate = 8;
            }

            ModConfig.Save();
        }

        private static string BuildLobbySubtitle(GameSession session, bool isHost)
        {
            if (session == null) return "Waiting for lobby state.";
            string map = MapHelper.GetMapDisplayName(session.MapName ?? MapHelper.GetDefaultMapName());
            string role = isHost ? "Host" : "Client";
            string mode = session.GameMode == MultiplayerGameMode.TeamDogfight
                ? $"Team Dogfight ({GameSession.ClampTeamCountForPlayers(session.TeamCount, session.PlayerCount)} teams)"
                : "FFA Dogfight";
            return $"{role} / {session.HostName ?? "Server"} / {map} / {mode} / {session.PlayerCount}/{session.MaxPlayersTotal} pilots";
        }

        private static string GetLoadoutButtonText(string aircraftName, string loadoutName)
        {
            if (string.IsNullOrEmpty(aircraftName))
                aircraftName = "AV8B";
            if (string.IsNullOrEmpty(loadoutName))
                loadoutName = "Clean";
            return $"{aircraftName} - {loadoutName}";
        }

        private void EnsureLobbyDefaults(GameSession session)
        {
            var local = session?.GetLocalPlayer();
            if (local == null) return;

            if (string.IsNullOrEmpty(local.SelectedAircraft) && !string.IsNullOrEmpty(ModConfig.LastAircraft?.Value))
                _lobby?.SetAircraft(ModConfig.LastAircraft.Value);

            if (string.IsNullOrEmpty(local.SelectedLoadout) && !string.IsNullOrEmpty(ModConfig.LastLoadout?.Value))
                _lobby?.SetLoadout(ModConfig.LastLoadout.Value);

            string currentMap = session?.MapName ?? MapHelper.GetDefaultMapName();
            if (string.IsNullOrEmpty(local.SelectedAirfield)
                && !string.IsNullOrEmpty(ModConfig.LastAirfield?.Value)
                && MapHelper.IsAirfieldOnMap(currentMap, ModConfig.LastAirfield.Value))
            {
                _lobby?.SetAirfield(ModConfig.LastAirfield.Value);
            }

            if (string.IsNullOrEmpty(local.SelectedAirfield))
            {
                string defaultAirfield = MapHelper.GetDefaultAirfieldName(currentMap);
                if (!string.IsNullOrEmpty(defaultAirfield))
                    _lobby?.SetAirfield(defaultAirfield);
            }
        }

        private static bool CanReady(GameSession session)
        {
            var local = session?.GetLocalPlayer();
            return local != null
                && local.IsModsVerified
                && !string.IsNullOrEmpty(local.SelectedAircraft)
                && !string.IsNullOrEmpty(local.SelectedAirfield)
                && (!ShowTeamSelection(session)
                    || local.Team != MultiplayerTeam.None);
        }

        private static bool HasUnverifiedPeers(GameSession session)
        {
            if (session == null)
                return false;

            foreach (var player in session.Players.Values)
                if (!player.IsHost && !player.IsModsVerified)
                    return true;

            return false;
        }

        private static bool AllRequiredPlayersReady(GameSession session)
        {
            if (session == null) return false;
            foreach (var player in session.Players.Values)
            {
                if (player.IsHost) continue;
                if (!player.IsModsVerified) return false;
                if (!player.IsReady) return false;
                if (ShowTeamSelection(session)
                    && player.Team == MultiplayerTeam.None) return false;
            }
            return true;
        }

        private static string GetTeamDisplayName(MultiplayerTeam team)
        {
            switch (team)
            {
                case MultiplayerTeam.Team1: return "Team 1";
                case MultiplayerTeam.Team2: return "Team 2";
                case MultiplayerTeam.Team3: return "Team 3";
                case MultiplayerTeam.Team4: return "Team 4";
                default: return "No team";
            }
        }
    }
}
