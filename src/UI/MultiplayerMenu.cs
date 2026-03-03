using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Cysharp.Threading.Tasks;
using Falcon.Game2;
using Falcon.World;
using TCAMultiplayer.Networking;
using TCAMultiplayer.Game;

namespace TCAMultiplayer.UI
{
    public class MultiplayerMenu : MonoBehaviour
    {
        public static MultiplayerMenu Instance { get; private set; }

        private bool _isCloseRequested;
        private LobbyScreen _currentScreen = LobbyScreen.MainMenu;
        private GameObject _contentRoot;
        private GameObject _backgroundPanel;
        private string _connectIP;
        private string _connectPort;
        private string _hostName;
        private string _hostPort;
        private string _username;
        private bool _isConnecting = false;
        private string _connectionError = "";
        private bool _isRefreshing = false;
        private bool _refreshPending = false;
        private bool _isManuallyDisconnecting = false;

        public static async UniTask CreateAndRun()
        {
            // Guard against creating duplicate menu instances (e.g. double-click,
            // or ReturnToLobby racing with an existing menu).
            if (Instance != null) return;

            var go = new GameObject("MultiplayerMenu", typeof(RectTransform));
            var menu = go.AddComponent<MultiplayerMenu>();
            await menu.RunMenu();
        }

        private void OnEnable()
        {
            if (LobbyManager.Instance != null)
            {
                LobbyManager.Instance.OnLobbyStateChanged += OnLobbyStateChanged;
                LobbyManager.Instance.OnSpawnPlayers += OnSpawnPlayers;
            }
            if (Plugin.Instance?.Network != null)
            {
                Plugin.Instance.Network.OnPeerConnected += OnPeerConnected;
                Plugin.Instance.Network.OnPeerDisconnected += OnPeerDisconnected;
            }
        }

        private void OnDisable()
        {
            if (LobbyManager.Instance != null)
            {
                LobbyManager.Instance.OnLobbyStateChanged -= OnLobbyStateChanged;
                LobbyManager.Instance.OnSpawnPlayers -= OnSpawnPlayers;
            }
            if (Plugin.Instance?.Network != null)
            {
                Plugin.Instance.Network.OnPeerConnected -= OnPeerConnected;
                Plugin.Instance.Network.OnPeerDisconnected -= OnPeerDisconnected;
            }
            // Clear singleton reference so CreateAndRun guard works correctly
            if (Instance == this) Instance = null;
        }

        private void OnPeerConnected(ulong peerId)
        {
            if (_currentScreen == LobbyScreen.DirectConnect || _currentScreen == LobbyScreen.Browse)
            {
                SetScreen(LobbyScreen.Lobby);
            }
            else
            {
                RefreshUI();
            }
        }

        private void OnPeerDisconnected(ulong peerId)
        {
            // If we're the host, a client leaving should NOT kick us out of the lobby.
            // Only clients should return to MainMenu when they lose connection to the host.
            bool isHost = Plugin.Instance?.GameState?.IsHost ?? false;

            if (isHost)
            {
                // Host stays in lobby, just refresh UI to update player list
                RefreshUI();
            }
            else if (_currentScreen == LobbyScreen.Lobby && !_isManuallyDisconnecting)
            {
                SetScreen(LobbyScreen.MainMenu);
            }
            else if (!_isManuallyDisconnecting)
            {
                RefreshUI();
            }
            // Reset the flag after handling the disconnect
            _isManuallyDisconnecting = false;
        }

        private void OnSpawnPlayers()
        {
            // Close menu when game starts
            _isCloseRequested = true;
        }

        private void Update()
        {
            // Fallback: Check if spawn happened but event missed
            if (!_isCloseRequested && SpawnManager.Instance != null && SpawnManager.Instance.IsSpawned)
            {
                _isCloseRequested = true;
            }
        }

        private void OnLobbyStateChanged()
        {
            // Only refresh when on the Lobby screen. HostSetup has no dynamic lobby content
            // and refreshing it mid-click (e.g. from CreateLobby) destroys the clicked button
            // via DestroyImmediate, which aborts the rest of the onClick handler.
            if (_currentScreen == LobbyScreen.Lobby)
                RefreshUI();
        }

        public async UniTask RunMenu()
        {
            this.gameObject.SetActive(true);
            _isCloseRequested = false;
            
            // Load defaults from config
            _username = Plugin.ConfigUsername?.Value ?? "Player";
            _connectIP = Plugin.ConfigLastIP?.Value ?? "127.0.0.1";
            _connectPort = Plugin.ConfigLastPort?.Value ?? NetworkConfig.DEFAULT_PORT_STRING;
            _hostName = Plugin.ConfigHostName?.Value ?? "TCA Server";
            _hostPort = Plugin.ConfigHostPort?.Value ?? NetworkConfig.DEFAULT_PORT_STRING;

            LobbyManager.Instance?.SetLocalPlayerName(_username);

            bool alreadyInLobby = Plugin.Instance?.GameState?.IsInLobby ?? false;
            SetScreen(alreadyInLobby ? LobbyScreen.Lobby : LobbyScreen.MainMenu);
            await UniTask.WaitUntil(() => _isCloseRequested, PlayerLoopTiming.Update, this.GetCancellationTokenOnDestroy());
            this.gameObject.SetActive(false);
            Destroy(this.gameObject);
        }

        public void Close()
        {
            _isCloseRequested = true;
            var mainMenu = UnityEngine.Object.FindObjectOfType<MainMenu>();
            if (mainMenu != null) mainMenu.ShowMainMenuUI(true);
        }

        public void SetScreen(LobbyScreen screen)
        {
            _currentScreen = screen;
            RefreshUI();
        }

        private void Awake()
        {
            Instance = this;
            SetupUI();
        }

        private void SetupUI()
        {
            var canvas = gameObject.GetComponent<Canvas>() ?? gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 999;
            var scaler = gameObject.GetComponent<CanvasScaler>() ?? gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            if (gameObject.GetComponent<GraphicRaycaster>() == null) gameObject.AddComponent<GraphicRaycaster>();

            // Full-screen dim overlay behind the panel
            _backgroundPanel = new GameObject("Background", typeof(RectTransform), typeof(Image));
            _backgroundPanel.transform.SetParent(transform, false);
            var bgImage = _backgroundPanel.GetComponent<Image>();
            bgImage.color = new Color(0, 0, 0, 0.85f);
            var bgRect = _backgroundPanel.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.sizeDelta = Vector2.zero;

            // Centered native panel using the QMB's green-bordered sprite
            var panelGo = UIFactory.CreateNativePanel(transform);
            var panelRect = panelGo.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.sizeDelta = new Vector2(800, 800);

            // Content root sits inside the native panel
            _contentRoot = new GameObject("Content", typeof(RectTransform));
            _contentRoot.transform.SetParent(panelGo.transform, false);
            var contentRect = _contentRoot.GetComponent<RectTransform>();
            contentRect.anchorMin = Vector2.zero;
            contentRect.anchorMax = Vector2.one;
            contentRect.offsetMin = Vector2.zero;
            contentRect.offsetMax = Vector2.zero;

            var layout = _contentRoot.AddComponent<VerticalLayoutGroup>();
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            layout.spacing = 15;
            layout.padding = new RectOffset(50, 50, 50, 50);
            layout.childAlignment = TextAnchor.UpperCenter;
        }

        private void RefreshUI()
        {
            // Re-entrancy guard: DrawLobby() can trigger OnLobbyStateChanged (e.g. via
            // auto-selecting an airfield which calls SetLocalAirfield), which calls RefreshUI()
            // WHILE DrawLobby() is still executing. Without this guard, the re-entrant call
            // redraws the full UI, then the original DrawLobby() continues appending duplicate
            // elements. Instead, defer the refresh until the current draw completes.
            if (_isRefreshing)
            {
                _refreshPending = true;
                return;
            }

            _isRefreshing = true;
            _refreshPending = false;

            // Detach children from parent FIRST (removes from layout immediately),
            // then Destroy them (deferred — avoids killing a button mid-onClick).
            // This prevents phantom clicks on pending-destroy buttons.
            var toDestroy = new System.Collections.Generic.List<GameObject>();
            foreach (Transform child in _contentRoot.transform)
                toDestroy.Add(child.gameObject);
            foreach (var go in toDestroy)
            {
                go.transform.SetParent(null);  // Remove from layout immediately
                Destroy(go);                     // Clean up at end of frame
            }

            switch (_currentScreen)
            {
                case LobbyScreen.MainMenu: DrawMainMenu(); break;
                case LobbyScreen.HostSetup: DrawHostSetup(); break;
                case LobbyScreen.Browse: DrawBrowse(); break;
                case LobbyScreen.DirectConnect: DrawDirectConnect(); break;
                case LobbyScreen.Lobby: DrawLobby(); break;
            }

            _isRefreshing = false;

            // If a refresh was requested while we were drawing, do it now
            if (_refreshPending)
            {
                _refreshPending = false;
                RefreshUI();
            }
        }

        private void DrawMainMenu()
        {
            UIFactory.CreateNativeText("MULTIPLAYER", _contentRoot.transform, 48);
            UIFactory.CreateLabelInputRow("Username:", _username, _contentRoot.transform, (val) => {
                _username = val;
                if (Plugin.ConfigUsername != null) Plugin.ConfigUsername.Value = val;
                Plugin.Instance?.Config?.Save();
                LobbyManager.Instance?.SetLocalPlayerName(val);
            });
            UIFactory.CreateNativeButton("HOST GAME", _contentRoot.transform).onClick.AddListener(() => SetScreen(LobbyScreen.HostSetup));
            UIFactory.CreateNativeButton("BROWSE LAN", _contentRoot.transform).onClick.AddListener(() => SetScreen(LobbyScreen.Browse));
            UIFactory.CreateNativeButton("DIRECT CONNECT", _contentRoot.transform).onClick.AddListener(() => SetScreen(LobbyScreen.DirectConnect));

            var spacer = new GameObject("Spacer", typeof(RectTransform), typeof(LayoutElement));
            spacer.transform.SetParent(_contentRoot.transform, false);
            spacer.GetComponent<LayoutElement>().flexibleHeight = 1;

            UIFactory.CreateNativeButton("BACK", _contentRoot.transform).onClick.AddListener(Close);
        }

        private void DrawHostSetup()
        {
            UIFactory.CreateNativeText("HOST SETTINGS", _contentRoot.transform, 36);
            UIFactory.CreateLabelInputRow("Server Name:", _hostName, _contentRoot.transform, val => {
                _hostName = val;
                if (Plugin.ConfigHostName != null) Plugin.ConfigHostName.Value = val;
                Plugin.Instance?.Config?.Save();
            });
            UIFactory.CreateLabelInputRow("Port:", _hostPort, _contentRoot.transform, val => {
                _hostPort = val;
                if (Plugin.ConfigHostPort != null) Plugin.ConfigHostPort.Value = val;
                Plugin.Instance?.Config?.Save();
            });

            UIFactory.CreateNativeButton("START SERVER", _contentRoot.transform).onClick.AddListener(() => {
                int.TryParse(_hostPort, out int port);

                // Reset transport state before re-hosting.
                Plugin.Instance?.Network?.Disconnect();
                Plugin.Instance?.Discovery?.StopBroadcasting();
                Plugin.Instance?.Discovery?.StopListening();
                Plugin.Instance?.GameState?.StartHosting(port, _hostName);
                Plugin.Instance?.Network?.StartHost(port);

                // CRITICAL: Switch screen to Lobby BEFORE CreateLobby, because CreateLobby
                // fires OnLobbyStateChanged → BroadcastLobbyState, and the resulting network
                // events can trigger OnPeerDisconnected/OnPeerConnected which call RefreshUI.
                // With deferred Destroy(), the old HostSetup BACK button is still alive and
                // Unity's event system can register a phantom click on it (due to layout shift),
                // which calls SetScreen(MainMenu) and aborts the rest of this handler.
                SetScreen(LobbyScreen.Lobby);

                Plugin.Instance.Lobby?.CreateLobby(Plugin.Instance.Network.LocalPeerId, _hostName);
                Plugin.Instance.Discovery?.StartBroadcasting(_hostName, port, "ActionIsland", 1, 8);
                Plugin.Log?.LogInfo($"[MultiplayerMenu] Hosting started on port {port}. Players={Plugin.Instance?.Lobby?.PlayerCount}");
            });
            UIFactory.CreateNativeButton("BACK", _contentRoot.transform).onClick.AddListener(() => SetScreen(LobbyScreen.MainMenu));
        }

        private void DrawLobby()
        {
            var lobby = LobbyManager.Instance;
            bool isHost = lobby?.IsHost ?? false;
            UIFactory.CreateNativeText("LOBBY", _contentRoot.transform, 36);

            var playerGroup = UIFactory.CreateVerticalGroup(_contentRoot.transform, 5, 10);
            playerGroup.AddComponent<Image>().color = new Color(1, 1, 1, 0.05f);

            if (lobby != null)
            {
                var pList = new List<LobbyPlayerInfo>(lobby.Players.Values);
                foreach (var p in pList)
                {
                    string statusText = p.IsReady ? "[READY]" : "[WAIT]";
                    Color statusColor = p.IsReady ? Color.green : Color.yellow;
                    
                    var playerText = UIFactory.CreateNativeText($"{p.PlayerName} | {p.SelectedAirfield} | {statusText}", playerGroup.transform, 20, TextAlignmentOptions.Left);
                    
                    // Manually color just the status part by finding its position
                    if (playerText != null)
                    {
                        // Force the entire text to use vertex coloring
                        playerText.enableVertexGradient = false;
                        playerText.color = Color.white;
                        
                        // Use ForceMeshUpdate to apply vertex colors
                        playerText.ForceMeshUpdate();
                        
                        var textInfo = playerText.textInfo;
                        if (textInfo != null && textInfo.characterCount > 0)
                        {
                            int statusIndex = playerText.text.LastIndexOf(statusText);
                            if (statusIndex >= 0)
                            {
                                for (int i = 0; i < textInfo.characterCount; i++)
                                {
                                    if (i >= statusIndex && i < statusIndex + statusText.Length)
                                    {
                                        if (!textInfo.characterInfo[i].isVisible) continue;
                                        
                                        int materialIndex = textInfo.characterInfo[i].materialReferenceIndex;
                                        int vertexIndex = textInfo.characterInfo[i].vertexIndex;
                                        
                                        Color32[] newVertexColors = textInfo.meshInfo[materialIndex].colors32;
                                        newVertexColors[vertexIndex + 0] = statusColor;
                                        newVertexColors[vertexIndex + 1] = statusColor;
                                        newVertexColors[vertexIndex + 2] = statusColor;
                                        newVertexColors[vertexIndex + 3] = statusColor;
                                    }
                                }
                                playerText.UpdateVertexData(TMP_VertexDataUpdateFlags.Colors32);
                            }
                        }
                    }
                }
            }

            // Mod Sync Status (client only, and ONLY if desynced or checking)
            if (!isHost && lobby != null)
            {
                if (lobby.ModSyncStatus == LobbyManager.ModSyncState.Checking)
                {
                    UIFactory.CreateNativeText("<color=yellow>MOD SYNC: Checking compatibility...</color>", _contentRoot.transform, 16);
                }
                else if (lobby.ModSyncStatus == LobbyManager.ModSyncState.Incompatible)
                {
                    UIFactory.CreateNativeText($"<color=red>MOD SYNC: Incompatible \u2717</color>", _contentRoot.transform, 16);
                    if (!string.IsNullOrEmpty(lobby.ModSyncError))
                    {
                        var errorGroup = UIFactory.CreateVerticalGroup(_contentRoot.transform, 2, 5);
                        errorGroup.AddComponent<Image>().color = new Color(0.5f, 0, 0, 0.3f);
                        UIFactory.CreateNativeText($"<color=#FF6666><size=14>{lobby.ModSyncError}</size></color>", errorGroup.transform, 14, TextAlignmentOptions.Left);
                    }
                }
                // Don't show anything if compatible
            }

            // Aircraft Selection (Lobby only - not available mid-match)
            var aircraftNames = LoadoutHelper.GetAircraftNames();
            string selectedAircraft = lobby?.LocalSelectedAircraft;
            
            if (aircraftNames == null || aircraftNames.Count == 0)
            {
                UIFactory.CreateNativeText("<color=red>No aircraft found! (Enter Arena Map first)</color>", _contentRoot.transform, 16);
            }
            else
            {
                // Build display names list
                var aircraftDisplayNames = new List<string>();
                int selectedAircraftIndex = 0;
                for (int i = 0; i < aircraftNames.Count; i++)
                {
                    string displayName = LoadoutHelper.GetAircraftDisplayName(aircraftNames[i]);
                    aircraftDisplayNames.Add(displayName);
                    if (aircraftNames[i] == selectedAircraft)
                    {
                        selectedAircraftIndex = i;
                    }
                }
                
                UIFactory.CreateLabeledSelector("Aircraft:", aircraftDisplayNames, selectedAircraftIndex, _contentRoot.transform, (index) => {
                    lobby?.SetLocalAircraft(aircraftNames[index]);
                    Plugin.Instance.Lobby?.SendAircraftSelect(aircraftNames[index]);
                    var defaultLoadout = LoadoutHelper.GetDefaultLoadoutForAircraft(aircraftNames[index]);
                    lobby?.SetLocalLoadout(defaultLoadout);
                    Plugin.Instance.Lobby?.SendLoadoutSelect(defaultLoadout);
                    RefreshUI();
                });
            }

            // Loadout Selection
            var loadoutNames = LoadoutHelper.GetLoadoutNamesForAircraft(selectedAircraft);
            string selectedLoadout = lobby?.LocalSelectedLoadout;
            
            if (loadoutNames == null || loadoutNames.Count == 0)
            {
                UIFactory.CreateNativeText("<color=yellow>No loadouts found for " + selectedAircraft + "</color>", _contentRoot.transform, 16);
            }
            else
            {
                int selectedLoadoutIndex = 0;
                for (int i = 0; i < loadoutNames.Count; i++)
                {
                    if (loadoutNames[i] == selectedLoadout)
                    {
                        selectedLoadoutIndex = i;
                        break;
                    }
                }
                
                UIFactory.CreateLabeledSelector("Loadout:", loadoutNames, selectedLoadoutIndex, _contentRoot.transform, (index) => {
                    lobby?.SetLocalLoadout(loadoutNames[index]);
                    Plugin.Instance.Lobby?.SendLoadoutSelect(loadoutNames[index]);
                    RefreshUI();
                });
            }

            // Airfield Selection
            var names = AirfieldHelper.GetAirfieldNames();
            Plugin.Log?.LogInfo($"[MultiplayerMenu] Drawing lobby airfield dropdown. Name count: {names?.Length ?? 0}");

            if (names == null || names.Length == 0)
            {
                UIFactory.CreateNativeText("<color=red>No airfields found! (Enter Arena Map first)</color>", _contentRoot.transform, 16);
            }
            else
            {
                string selectedAirfield = lobby?.LocalSelectedAirfield;
                int selectedAirfieldIndex = 0;
                var airfieldList = new List<string>(names);
                
                // Auto-select first airfield if none selected
                if (string.IsNullOrEmpty(selectedAirfield) && airfieldList.Count > 0)
                {
                    selectedAirfield = airfieldList[0];
                    lobby?.SetLocalAirfield(selectedAirfield);
                    Plugin.Instance.Lobby?.SendAirfieldSelect(selectedAirfield);
                }

                for (int i = 0; i < airfieldList.Count; i++)
                {
                    if (airfieldList[i] == selectedAirfield)
                    {
                        selectedAirfieldIndex = i;
                        break;
                    }
                }
                
                UIFactory.CreateLabeledSelector("Airfield:", airfieldList, selectedAirfieldIndex, _contentRoot.transform, (index) => {
                    lobby?.SetLocalAirfield(airfieldList[index]);
                    Plugin.Instance.Lobby?.SendAirfieldSelect(airfieldList[index]);
                    RefreshUI();
                });
            }

            // Spawn Type Selection
            var spawnTypeNames = new List<string> { "Air (300m)", "Runway", "Ramp" };
            var currentSpawnType = lobby?.SpawnType ?? LobbySpawnType.Runway;
            int spawnTypeIndex = (int)currentSpawnType;
            
            string spawnLabel = "Spawn Type:";
            var spawnBtn = UIFactory.CreateLabeledSelector(spawnLabel, spawnTypeNames, spawnTypeIndex, _contentRoot.transform,
                isHost ? (Action<int>)((index) => {
                    lobby?.SetSpawnSettings((LobbySpawnType)index);
                    RefreshUI();
                }) : null);
            if (spawnBtn != null && !isHost)
            {
                spawnBtn.interactable = false;
            }

            // Time of Day (Host only)
            var timeNames = new List<string> { "Dawn", "Morning", "Noon", "Afternoon", "Evening", "Night" };
            var currentTime = lobby?.SelectedTimeOfDay ?? TimeOfDay.Morning;
            int timeIndex = (int)currentTime;
            
            string timeLabel = "Time:";
            var timeBtn = UIFactory.CreateLabeledSelector(timeLabel, timeNames, timeIndex, _contentRoot.transform,
                isHost ? (Action<int>)((index) => {
                    lobby?.SetTimeOfDay((TimeOfDay)index);
                    RefreshUI();
                }) : null);
            if (timeBtn != null && !isHost)
            {
                timeBtn.interactable = false;
            }

            // Aircraft Collisions Toggle (Host only)
            if (isHost)
            {
                var collisionsToggle = UIFactory.CreateLabeledToggle("Aircraft Collisions:", lobby.AircraftCollisionsEnabled, _contentRoot.transform);
                if (collisionsToggle != null)
                {
                    collisionsToggle.onValueChanged.AddListener((enabled) => {
                        lobby?.SetAircraftCollisionsEnabled(enabled);
                        RefreshUI();
                    });
                }
            }
            else
            {
                // Client sees read-only label
                UIFactory.CreateNativeText($"Aircraft Collisions: {(lobby.AircraftCollisionsEnabled ? "<color=green>ON</color>" : "<color=red>OFF</color>")}", _contentRoot.transform, 18);
            }

            var spacer = new GameObject("Spacer", typeof(RectTransform), typeof(LayoutElement));
            spacer.transform.SetParent(_contentRoot.transform, false);
            spacer.GetComponent<LayoutElement>().flexibleHeight = 1;

            var readyBtn = UIFactory.CreateNativeButton(lobby.LocalIsReady ? "NOT READY" : "READY", _contentRoot.transform);
            
            // Disable READY if no airfield selected
            bool hasAirfield = !string.IsNullOrEmpty(lobby.LocalSelectedAirfield);
            readyBtn.interactable = hasAirfield;
            
            readyBtn.GetComponent<Image>().color = !hasAirfield ? Color.gray : (lobby.LocalIsReady ? Color.red : Color.green);
            
            if (!hasAirfield)
            {
                UIFactory.CreateNativeText("<color=orange>SELECT AN AIRFIELD TO READY UP</color>", _contentRoot.transform, 14);
            }

            readyBtn.onClick.AddListener(() => {
                lobby.SetLocalReady(!lobby.LocalIsReady);
                Plugin.Instance.Lobby?.SendPlayerReady(lobby.LocalIsReady);
                RefreshUI();
            });

            if (isHost)
            {
                var start = UIFactory.CreateNativeButton("START GAME", _contentRoot.transform);
                start.interactable = lobby.AreAllPlayersReady && !lobby.GameLoading && !lobby.GameStarted;
                start.onClick.AddListener(() => {
                    Plugin.Log?.LogInfo("[MultiplayerMenu] Starting game...");
                    bool started = lobby.StartGame();
                    if (started)
                    {
                        Plugin.Instance.Lobby?.SendStartGame(lobby.MapName, lobby.SpawnType);
                    }
                });
            }

            UIFactory.CreateNativeButton("LEAVE", _contentRoot.transform).onClick.AddListener(() => {
                _isManuallyDisconnecting = true;
                Plugin.Instance?.Network?.Disconnect();
                Plugin.Instance?.GameState?.Disconnect();
                Plugin.Instance?.Discovery?.StopBroadcasting();
                Plugin.Instance?.Discovery?.StopListening();
                Plugin.Instance?.Lobby?.LeaveLobby();
                SetScreen(LobbyScreen.MainMenu);
            });
        }

        private void DrawDirectConnect()
        {
            UIFactory.CreateNativeText("DIRECT CONNECT", _contentRoot.transform, 36);
            UIFactory.CreateLabelInputRow("IP:", _connectIP, _contentRoot.transform, v => {
                _connectIP = v;
                if (Plugin.ConfigLastIP != null) Plugin.ConfigLastIP.Value = v;
                Plugin.Instance?.Config?.Save();
            });
            UIFactory.CreateLabelInputRow("Port:", _connectPort, _contentRoot.transform, v => {
                _connectPort = v;
                if (Plugin.ConfigLastPort != null) Plugin.ConfigLastPort.Value = v;
                Plugin.Instance?.Config?.Save();
            });

            var spacer = new GameObject("Spacer", typeof(RectTransform), typeof(LayoutElement));
            spacer.transform.SetParent(_contentRoot.transform, false);
            spacer.GetComponent<LayoutElement>().flexibleHeight = 1;

            if (!string.IsNullOrEmpty(_connectionError))
            {
                UIFactory.CreateNativeText($"<color=red>{_connectionError}</color>", _contentRoot.transform, 18);
            }

            if (_isConnecting)
            {
                var connectBtn = UIFactory.CreateNativeButton("CONNECTING...", _contentRoot.transform);
                connectBtn.interactable = false;
            }
            else
            {
                UIFactory.CreateNativeButton("CONNECT", _contentRoot.transform).onClick.AddListener(async () => {
                    int.TryParse(_connectPort, out int p);

                    if (Plugin.Instance == null) return;

                    Plugin.Log?.LogInfo($"[MultiplayerMenu] Direct connect to {_connectIP}:{p}");
                    // Ensure reconnect starts from a fresh transport instance state.
                    Plugin.Instance.Network.Disconnect();
                    Plugin.Instance.GameState?.StartConnecting(_connectIP, p);
                    Plugin.Instance.Network.StartClient(_connectIP, p);
                    
                    _isConnecting = true;
                    _connectionError = "";
                    RefreshUI();

                    // Wait for connection or timeout
                    float timeout = 5f;
                    float elapsed = 0f;
                    while (elapsed < timeout && _isConnecting)
                    {
                        if (Plugin.Instance.Network.IsConnected)
                        {
                            _isConnecting = false;
                            // SetScreen(LobbyScreen.Lobby) handled by OnPeerConnected
                            return; 
                        }
                        await UniTask.Delay(100);
                        elapsed += 0.1f;
                    }

                    // If we get here, we timed out
                    if (_isConnecting)
                    {
                        Plugin.Log?.LogWarning($"[MultiplayerMenu] Connection to {_connectIP}:{p} timed out.");
                        Plugin.Instance.Network.Disconnect();
                        Plugin.Instance.GameState?.Disconnect();
                        _isConnecting = false;
                        _connectionError = "Connection timed out";
                        RefreshUI();
                    }
                });
            }
            UIFactory.CreateNativeButton("BACK", _contentRoot.transform).onClick.AddListener(() => {
                _isConnecting = false;
                _connectionError = "";
                SetScreen(LobbyScreen.MainMenu);
            });
        }

        private void DrawBrowse()
        {
            UIFactory.CreateNativeText("LAN BROWSER", _contentRoot.transform, 36);

            var listContainer = UIFactory.CreateVerticalGroup(_contentRoot.transform, 5, 0);
            
            // Adjust layout to make room for BACK button
            var le = listContainer.GetComponent<LayoutElement>();
            if (le == null) le = listContainer.AddComponent<LayoutElement>();
            le.flexibleHeight = 1;
            le.minHeight = 400;

            var games = Plugin.Instance?.Discovery?.GetDiscoveredGames() ?? new List<DiscoveredGame>();

            if (games.Count == 0)
            {
                UIFactory.CreateNativeText("Searching...", listContainer.transform, 18);
            }
            else
            {
                foreach (var game in games)
                {
                    var btn = UIFactory.CreateNativeButton($"{game.HostName} ({game.IPAddress})", listContainer.transform, 40);
                    btn.onClick.AddListener(async () => {
                        if (_isConnecting) return; // Prevent multiple clicks
                        
                        // Defensive logging to trace connection issues
                        if (Plugin.Instance == null)
                        {
                            Plugin.Log?.LogError("[MultiplayerMenu] Plugin.Instance is NULL! Cannot connect.");
                            return;
                        }
                        
                        Plugin.Log?.LogInfo($"[MultiplayerMenu] LAN browse - joining game: {game.IPAddress}:{game.Port}");
                        // Ensure reconnect starts from a fresh transport instance state.
                        Plugin.Instance.Network.Disconnect();
                        Plugin.Instance.GameState?.StartConnecting(game.IPAddress, game.Port);
                        Plugin.Instance.Network.StartClient(game.IPAddress, game.Port);
                        
                        _isConnecting = true;
                        _connectionError = "";
                        btn.GetComponentInChildren<TextMeshProUGUI>().text = "CONNECTING...";
                        btn.interactable = false;

                        // Wait for connection or timeout
                        float timeout = 5f;
                        float elapsed = 0f;
                        while (elapsed < timeout && _isConnecting)
                        {
                            if (Plugin.Instance.Network.IsConnected)
                            {
                                _isConnecting = false;
                                return; 
                            }
                            await UniTask.Delay(100);
                            elapsed += 0.1f;
                        }

                        // If we get here, we timed out
                        if (_isConnecting)
                        {
                            Plugin.Log?.LogWarning($"[MultiplayerMenu] LAN Connection to {game.IPAddress}:{game.Port} timed out.");
                            Plugin.Instance.Network.Disconnect();
                            Plugin.Instance.GameState?.Disconnect();
                            _isConnecting = false;
                            _connectionError = "Connection timed out";
                            RefreshUI();
                        }
                    });
                }
            }

            if (!string.IsNullOrEmpty(_connectionError))
            {
                UIFactory.CreateNativeText($"<color=red>{_connectionError}</color>", listContainer.transform, 18);
            }

            UIFactory.CreateNativeButton("BACK", _contentRoot.transform).onClick.AddListener(() => {
                _isConnecting = false;
                _connectionError = "";
                Plugin.Instance?.Discovery?.StopListening();
                SetScreen(LobbyScreen.MainMenu);
            });
        }
    }
}
