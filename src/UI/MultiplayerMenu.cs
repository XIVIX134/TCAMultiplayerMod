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
            // If we're the host, a client leaving should NOT kick us out of the lobby.
            // Only clients should return to MainMenu when they lose connection to the host.
            bool isHost = Plugin.Instance?.GameState?.IsHost ?? false;

            if (isHost)
            {
                // Host stays in lobby, just refresh UI to update player list
                RefreshUI();
            }
            else if (_currentScreen == LobbyScreen.Lobby)
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
            UIFactory.CreateLabelInputRow("Username:", _username, _contentRoot.transform, (val) => {
                _username = val;
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

            // Mod Sync Status (client only — host always compatible with itself)
            if (!isHost && lobby != null)
            {
                string syncText;
                switch (lobby.ModSyncStatus)
                {
                    case LobbyManager.ModSyncState.Checking:
                        syncText = "<color=yellow>MOD SYNC: Checking compatibility...</color>";
                        break;
                    case LobbyManager.ModSyncState.Compatible:
                        syncText = "<color=green>MOD SYNC: Compatible \u2713</color>";
                        break;
                    case LobbyManager.ModSyncState.Incompatible:
                        syncText = $"<color=red>MOD SYNC: Incompatible \u2717</color>";
                        break;
                    default:
                        syncText = "<color=#888888>MOD SYNC: Not checked</color>";
                        break;
                }
                UIFactory.CreateNativeText(syncText, _contentRoot.transform, 16);

                // Show detailed error if incompatible
                if (lobby.ModSyncStatus == LobbyManager.ModSyncState.Incompatible && !string.IsNullOrEmpty(lobby.ModSyncError))
                {
                    var errorGroup = UIFactory.CreateVerticalGroup(_contentRoot.transform, 2, 5);
                    errorGroup.AddComponent<Image>().color = new Color(0.5f, 0, 0, 0.3f);
                    UIFactory.CreateNativeText($"<color=#FF6666><size=14>{lobby.ModSyncError}</size></color>", errorGroup.transform, 14, TextAlignmentOptions.Left);
                }
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
            
            string spawnLabel = isHost ? "Spawn Type:" : "Spawn Type: <size=70%><color=#888888>[HOST]</color></size>";
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
            
            string timeLabel = isHost ? "Time:" : "Time: <size=70%><color=#888888>[HOST]</color></size>";
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
                UIFactory.CreateNativeText($"<color=#888888>[HOST]</color> Aircraft Collisions: {(lobby.AircraftCollisionsEnabled ? "<color=green>ON</color>" : "<color=red>OFF</color>")}", _contentRoot.transform, 18);
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
