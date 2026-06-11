using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TCAMultiplayer.Core;
using TCAMultiplayer.Game;

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

        private enum Screen { MainMenu, HostSetup, DirectConnect, Lobby }
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
        private bool _previousCursorVisible;

        public Action OnMenuClosed;
        public bool IsVisible => _visible;

        private string _username;
        private string _connectIP;
        private string _connectPort;
        private string _hostName;
        private string _hostPort;

        public void Init(ConnectionManager connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            UIFactory.NativeDialogActiveChanged -= OnNativeDialogActiveChanged;
            UIFactory.NativeDialogActiveChanged += OnNativeDialogActiveChanged;
        }

        public void SetLobby(LobbyManager lobby)
        {
            if (_lobby != null) _lobby.OnLobbyStateChanged -= OnLobbyStateChanged;
            _lobby = lobby;
            if (_lobby != null) _lobby.OnLobbyStateChanged += OnLobbyStateChanged;
        }

        public void HandleSessionEnded()
        {
            SetLobby(null);
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
                else
                    SetScreen(Screen.MainMenu);
            }
        }

        private void OnDestroy()
        {
            if (_lobby != null) _lobby.OnLobbyStateChanged -= OnLobbyStateChanged;
            UIFactory.NativeDialogActiveChanged -= OnNativeDialogActiveChanged;
        }

        private void OnLobbyStateChanged()
        {
            if (_visible && _currentScreen == Screen.Lobby)
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
            overlayImage.color = new Color(0f, 0f, 0f, 0.72f);
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
            var panel = CreateMenuPanel(620f, 470f);
            AddHeader(panel.transform, "MULTIPLAYER", "Host a direct session or connect to another pilot.");

            UIFactory.CreateLabelInputRow("Pilot >", _username, panel.transform, val =>
            {
                _username = val;
                if (ModConfig.Username != null) ModConfig.Username.Value = val;
            }, 130f);

            UIFactory.CreateSpacer(panel.transform, 8);

            Track(UIFactory.CreateNativeButton("HOST GAME", panel.transform, 52))
                ?.onClick.AddListener(() => SetScreen(Screen.HostSetup));

            Track(UIFactory.CreateNativeButton("DIRECT CONNECT", panel.transform, 52))
                ?.onClick.AddListener(() => SetScreen(Screen.DirectConnect));

            UIFactory.CreateSpacer(panel.transform, 14, 1f);

            Track(UIFactory.CreateNativeButton("MAIN MENU", panel.transform, 46))
                ?.onClick.AddListener(CloseMenu);
        }

        private void DrawHostSetup()
        {
            var panel = CreateMenuPanel(680f, 500f);
            AddHeader(panel.transform, "HOST GAME", "Create a lobby and wait for peers to join.");

            UIFactory.CreateLabelInputRow("Server >", _hostName, panel.transform, val =>
            {
                _hostName = val;
                if (ModConfig.HostServerName != null) ModConfig.HostServerName.Value = val;
            });

            UIFactory.CreateLabelInputRow("Port >", _hostPort, panel.transform, val =>
            {
                _hostPort = val;
                if (ModConfig.HostPort != null) ModConfig.HostPort.Value = val;
            });

            DrawHostSetupOptions(panel.transform);

            UIFactory.CreateSpacer(panel.transform, 18, 1f);

            Track(UIFactory.CreateNativeButton("START SERVER", panel.transform, 54))
                ?.onClick.AddListener(StartHost);

            Track(UIFactory.CreateNativeButton("BACK", panel.transform, 46))
                ?.onClick.AddListener(() => SetScreen(Screen.MainMenu));
        }

        private void DrawHostSetupOptions(Transform parent)
        {
            Track(UIFactory.CreateLabeledSelector(
                "Player Limit >", BuildPlayerLimitOptions(), MaxPlayersIndex(ModConfig.HostMaxPlayersTotal?.Value ?? 8), parent,
                idx =>
                {
                    if (ModConfig.HostMaxPlayersTotal != null)
                        ModConfig.HostMaxPlayersTotal.Value = PlayerLimitFromIndex(idx);
                    RefreshUI();
                }));
        }

        private void DrawDirectConnect()
        {
            var panel = CreateMenuPanel(660f, 430f);
            AddHeader(panel.transform, "DIRECT CONNECT", "Join a lobby by address.");

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

            Track(UIFactory.CreateNativeButton("CONNECT", panel.transform, 54))
                ?.onClick.AddListener(StartJoin);

            Track(UIFactory.CreateNativeButton("BACK", panel.transform, 46))
                ?.onClick.AddListener(() => SetScreen(Screen.MainMenu));
        }

        private void DrawLobby()
        {
            var session = _connection?.Session;
            bool isHost = _connection?.IsHost ?? false;
            EnsureLobbyDefaults(session);

            var shell = CreateMenuPanel(1120f, 800f);
            AddHeader(shell.transform, "MULTIPLAYER LOBBY", BuildLobbySubtitle(session, isHost));

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
            var ready = Track(UIFactory.CreateNativeButton(localReady ? "NOT READY" : "READY", buttons.transform, 50));
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
                var start = Track(UIFactory.CreateNativeButton("START GAME", buttons.transform, 50));
                if (start != null)
                {
                    start.interactable = AllRequiredPlayersReady(session);
                    start.onClick.AddListener(() => _lobby?.StartGame());
                }
            }

            Track(UIFactory.CreateNativeButton("LEAVE", buttons.transform, 50))
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
                string warningText = ShowTeamSelection(session)
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
            var status = UIFactory.CreateNativeText(stateText, row.transform, 16, TextAlignmentOptions.MidlineRight);
            UIFactory.SetLayoutWidth(status.gameObject, 82f, 82f);

            string aircraft = string.IsNullOrEmpty(player.SelectedAircraft) ? "No aircraft" : player.SelectedAircraft;
            string airfield = string.IsNullOrEmpty(player.SelectedAirfield) ? "No airfield" : player.SelectedAirfield;
            string detail = $"{aircraft}  /  {airfield}";
            if (showTeam)
            {
                string team = player.Team == MultiplayerTeam.None ? "No team" : GetTeamDisplayName(player.Team);
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
            if (_connection == null) return;

            int.TryParse(_hostPort, out int port);
            if (port <= 0) port = 7777;

            try
            {
                ApplyHostConfigToTransport();
                _connection.HostSession(_hostName, port);
                var local = _connection.Session?.GetLocalPlayer();
                if (local != null) local.PlayerName = _username;
                SetScreen(Screen.Lobby);
                Log.Info(Tag, $"Hosting on port {port}");
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"Host failed: {ex}");
            }
        }

        private void StartJoin()
        {
            if (_connection == null) return;

            int.TryParse(_connectPort, out int port);
            if (port <= 0) port = 7777;

            try
            {
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
                _previousCursorVisible = Cursor.visible;
            }

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void RestoreCursor()
        {
            Cursor.lockState = _previousCursorLockState;
            Cursor.visible = _previousCursorVisible;
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
                && !string.IsNullOrEmpty(local.SelectedAircraft)
                && !string.IsNullOrEmpty(local.SelectedAirfield)
                && (!ShowTeamSelection(session)
                    || local.Team != MultiplayerTeam.None);
        }

        private static bool AllRequiredPlayersReady(GameSession session)
        {
            if (session == null) return false;
            foreach (var player in session.Players.Values)
            {
                if (player.IsHost) continue;
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
