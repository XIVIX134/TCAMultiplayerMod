using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Cysharp.Threading.Tasks;
using Falcon.Game2;
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

        private string _connectIP = "127.0.0.1";
        private string _connectPort = NetworkConfig.DEFAULT_PORT_STRING;
        private string _hostName = "TCA Server";
        private string _hostPort = NetworkConfig.DEFAULT_PORT_STRING;
        private string _username = "Player";

        public static async UniTask CreateAndRun()
        {
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
            if (_currentScreen == LobbyScreen.Lobby)
            {
                SetScreen(LobbyScreen.MainMenu);
            }
            else
            {
                RefreshUI();
            }
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
            if (_currentScreen == LobbyScreen.Lobby || _currentScreen == LobbyScreen.HostSetup)
                RefreshUI();
        }

        public async UniTask RunMenu()
        {
            this.gameObject.SetActive(true);
            _isCloseRequested = false;
            _username = LobbyManager.Instance?.LocalPlayerName ?? "Player";
            SetScreen(LobbyScreen.MainMenu);
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

            _backgroundPanel = new GameObject("Background", typeof(RectTransform), typeof(Image));
            _backgroundPanel.transform.SetParent(transform, false);
            var bgImage = _backgroundPanel.GetComponent<Image>();
            bgImage.color = new Color(0, 0, 0, 0.85f);
            var bgRect = _backgroundPanel.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.sizeDelta = Vector2.zero;

            _contentRoot = new GameObject("Content", typeof(RectTransform));
            _contentRoot.transform.SetParent(transform, false);
            var contentRect = _contentRoot.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0.5f, 0.5f);
            contentRect.anchorMax = new Vector2(0.5f, 0.5f);
            contentRect.pivot = new Vector2(0.5f, 0.5f);
            contentRect.sizeDelta = new Vector2(800, 800);

            var layout = _contentRoot.AddComponent<VerticalLayoutGroup>();
            layout.childControlHeight = true; // Control height to prevent overlap
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            layout.spacing = 15;
            layout.padding = new RectOffset(50, 50, 50, 50);
            layout.childAlignment = TextAnchor.UpperCenter;
        }

        private void RefreshUI()
        {
            foreach (Transform child in _contentRoot.transform) Destroy(child.gameObject);

            switch (_currentScreen)
            {
                case LobbyScreen.MainMenu: DrawMainMenu(); break;
                case LobbyScreen.HostSetup: DrawHostSetup(); break;
                case LobbyScreen.Browse: DrawBrowse(); break;
                case LobbyScreen.DirectConnect: DrawDirectConnect(); break;
                case LobbyScreen.Lobby: DrawLobby(); break;
            }
        }

        private void DrawMainMenu()
        {
            UIFactory.CreateNativeText("MULTIPLAYER", _contentRoot.transform, 48);
            UIFactory.CreateLabelInputRow("Username:", _username, _contentRoot.transform, (val) => _username = val);
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
            UIFactory.CreateLabelInputRow("Server Name:", _hostName, _contentRoot.transform, val => _hostName = val);
            UIFactory.CreateLabelInputRow("Port:", _hostPort, _contentRoot.transform, val => _hostPort = val);

            UIFactory.CreateNativeButton("START SERVER", _contentRoot.transform).onClick.AddListener(() => {
                int.TryParse(_hostPort, out int port);
                Plugin.Instance?.GameState?.StartHosting(port, _hostName);
                Plugin.Instance?.Network?.StartHost(port);
                Plugin.Instance.Lobby?.CreateLobby(Plugin.Instance.Network.LocalPeerId, _hostName);
                Plugin.Instance.Discovery?.StartBroadcasting(_hostName, port, "ActionIsland", 1, 8);
                SetScreen(LobbyScreen.Lobby);
            });
            UIFactory.CreateNativeButton("BACK", _contentRoot.transform).onClick.AddListener(() => SetScreen(LobbyScreen.MainMenu));
        }

        private void DrawLobby()
        {
            var lobby = LobbyManager.Instance;
            bool isHost = lobby?.IsHost ?? false;
            UIFactory.CreateNativeText(isHost ? "LOBBY (HOST)" : "LOBBY (CLIENT)", _contentRoot.transform, 36);

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

            UIFactory.CreateNativeText("Select Airfield:", _contentRoot.transform, 18, TextAlignmentOptions.Left);
            var afGroup = new GameObject("AFGrid", typeof(RectTransform));
            afGroup.transform.SetParent(_contentRoot.transform, false);
            
            var grid = afGroup.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(340, 45);
            grid.spacing = new Vector2(10, 10);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 2;

            // Add LayoutElement to ensure the VerticalLayoutGroup sees the height
            var le = afGroup.AddComponent<LayoutElement>();
            
            // Add ContentSizeFitter to calculate the size
            var fitter = afGroup.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            var names = AirfieldHelper.GetAirfieldNames();
            Plugin.Log?.LogInfo($"[MultiplayerMenu] Drawing lobby airfield grid. Name count: {names?.Length ?? 0}");

            if (names == null || names.Length == 0)
            {
                UIFactory.CreateNativeText("<color=red>No airfields found! (Enter Arena Map first)</color>", _contentRoot.transform, 16);
            }
            else
            {
                int buttonsCreated = 0;
                string selectedAirfield = lobby?.LocalSelectedAirfield;
                
                foreach (var name in names)
                {
                    var btn = UIFactory.CreateNativeButton(name, afGroup.transform, 40);
                    if (btn != null)
                    {
                        buttonsCreated++;
                        
                        // Highlight the selected airfield
                        bool isSelected = (name == selectedAirfield);
                        var img = btn.GetComponent<Image>();
                        if (img != null)
                        {
                            if (isSelected)
                            {
                                img.color = Color.cyan; // Bright cyan for selected
                            }
                            else
                            {
                                img.color = new Color(0.2f, 0.2f, 0.2f, 1f); // Dark gray for unselected
                            }
                        }
                        
                        btn.onClick.AddListener(() => {
                            lobby?.SetLocalAirfield(name);
                            Plugin.Instance.Lobby?.SendAirfieldSelect(name);
                            RefreshUI();
                        });
                    }
                    else
                    {
                        Plugin.Log?.LogWarning($"[MultiplayerMenu] Failed to create button for airfield: {name} (UIFactory returned null)");
                    }
                }
                Plugin.Log?.LogInfo($"[MultiplayerMenu] Created {buttonsCreated} airfield buttons.");
            }

            // Spawn Type Selection
            UIFactory.CreateNativeText("Spawn Type:", _contentRoot.transform, 18, TextAlignmentOptions.Left);
            
            var stRow = new GameObject("SpawnTypeRow", typeof(RectTransform));
            stRow.transform.SetParent(_contentRoot.transform, false);
            var stLayout = stRow.AddComponent<HorizontalLayoutGroup>();
            stLayout.childControlHeight = true;
            stLayout.childControlWidth = true;
            stLayout.childForceExpandHeight = true;
            stLayout.childForceExpandWidth = true;
            stLayout.spacing = 8;
            var stLE = stRow.AddComponent<LayoutElement>();
            stLE.preferredHeight = 45;

            var spawnTypeNames = new[] { "Air (300m)", "Runway", "Ramp" };
            var currentSpawnType = lobby?.SpawnType ?? LobbySpawnType.Runway;
            
            for (int i = 0; i < spawnTypeNames.Length; i++)
            {
                var spawnType = (LobbySpawnType)i;
                var btn = UIFactory.CreateNativeButton(spawnTypeNames[i], stRow.transform, 42);
                if (btn != null)
                {
                    // Highlight the currently selected spawn type
                    if (spawnType == currentSpawnType)
                    {
                        var img = btn.GetComponent<Image>();
                        if (img != null) img.color = Color.cyan;
                    }
                    
                    if (isHost)
                    {
                        // Only host can change spawn type
                        int idx = i; // Capture for closure
                        btn.onClick.AddListener(() => {
                            lobby?.SetSpawnSettings((LobbySpawnType)idx);
                            RefreshUI();
                        });
                    }
                    else
                    {
                        // Client sees read-only buttons (no interaction)
                        btn.interactable = false;
                        // Keep the highlight color for the selected one
                        if (spawnType == currentSpawnType)
                        {
                            var img = btn.GetComponent<Image>();
                            if (img != null) img.color = new Color(0.0f, 0.7f, 0.8f, 1f); // Slightly different cyan for read-only
                        }
                        else
                        {
                            var img = btn.GetComponent<Image>();
                            if (img != null) img.color = new Color(0.3f, 0.3f, 0.3f, 1f); // Darker gray for unselected read-only
                        }
                    }
                }
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
                start.interactable = lobby.AreAllPlayersReady;
                start.onClick.AddListener(() => {
                    Plugin.Log?.LogInfo("[MultiplayerMenu] Starting game...");
                    lobby.StartGame();
                    Plugin.Instance.Lobby?.SendStartGame(lobby.MapName, lobby.SpawnType);
                });
            }

            UIFactory.CreateNativeButton("LEAVE", _contentRoot.transform).onClick.AddListener(() => {
                Plugin.Instance?.GameState?.Disconnect();
                Plugin.Instance?.Network?.Disconnect();
                Plugin.Instance?.Discovery?.StopBroadcasting();
                Plugin.Instance?.Lobby?.LeaveLobby();
                SetScreen(LobbyScreen.MainMenu);
            });
        }

        private void DrawDirectConnect()
        {
            UIFactory.CreateNativeText("DIRECT CONNECT", _contentRoot.transform, 36);
            UIFactory.CreateLabelInputRow("IP:", _connectIP, _contentRoot.transform, v => _connectIP = v);
            UIFactory.CreateLabelInputRow("Port:", _connectPort, _contentRoot.transform, v => _connectPort = v);

            var spacer = new GameObject("Spacer", typeof(RectTransform), typeof(LayoutElement));
            spacer.transform.SetParent(_contentRoot.transform, false);
            spacer.GetComponent<LayoutElement>().flexibleHeight = 1;

            UIFactory.CreateNativeButton("CONNECT", _contentRoot.transform).onClick.AddListener(() => {
                int.TryParse(_connectPort, out int p);

                // ... logging ...
                if (Plugin.Instance == null) { /* ... */ return; }
                /* ... */

                Plugin.Log?.LogInfo($"[MultiplayerMenu] Direct connect to {_connectIP}:{p}");
                Plugin.Instance.GameState?.StartConnecting(_connectIP, p);
                Plugin.Instance.Network.StartClient(_connectIP, p);
                
                // Don't transition immediately - wait for OnPeerConnected callback
                RefreshUI();
            });
            UIFactory.CreateNativeButton("BACK", _contentRoot.transform).onClick.AddListener(() => SetScreen(LobbyScreen.MainMenu));
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
                    btn.onClick.AddListener(() => {
                        // Defensive logging to trace connection issues
                        if (Plugin.Instance == null)
                        {
                            Plugin.Log?.LogError("[MultiplayerMenu] Plugin.Instance is NULL! Cannot connect.");
                            return;
                        }
                        if (Plugin.Instance.GameState == null)
                        {
                            Plugin.Log?.LogError("[MultiplayerMenu] GameState is NULL! Cannot track connection state.");
                        }
                        if (Plugin.Instance.Network == null)
                        {
                            Plugin.Log?.LogError("[MultiplayerMenu] Network is NULL! Cannot connect.");
                            return;
                        }

                        Plugin.Log?.LogInfo($"[MultiplayerMenu] LAN browse - joining game: {game.IPAddress}:{game.Port}");
                        Plugin.Log?.LogInfo($"[MultiplayerMenu] GameState before connect: {Plugin.Instance.GameState?.CurrentState}");

                        bool stateStarted = Plugin.Instance.GameState?.StartConnecting(game.IPAddress, game.Port) ?? false;
                        Plugin.Log?.LogInfo($"[MultiplayerMenu] StartConnecting result: {stateStarted}, state now: {Plugin.Instance.GameState?.CurrentState}");

                        Plugin.Instance.Network.StartClient(game.IPAddress, game.Port);
                        
                        // Don't transition immediately - wait for OnPeerConnected callback
                        // (same fix as DirectConnect screen)
                        RefreshUI();
                    });
                }
            }

            UIFactory.CreateNativeButton("BACK", _contentRoot.transform).onClick.AddListener(() => {
                Plugin.Instance?.Discovery?.StopListening();
                SetScreen(LobbyScreen.MainMenu);
            });
        }
    }
}
