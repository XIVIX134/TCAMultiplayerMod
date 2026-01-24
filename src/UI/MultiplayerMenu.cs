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
        private string _connectPort = "7777";
        private string _hostName = "TCA Server";
        private string _hostPort = "7777";
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
                LobbyManager.Instance.OnLobbyStateChanged += OnLobbyStateChanged;
        }

        private void OnDisable()
        {
            if (LobbyManager.Instance != null)
                LobbyManager.Instance.OnLobbyStateChanged -= OnLobbyStateChanged;
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
            layout.childControlHeight = false;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            layout.spacing = 20;
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
                Plugin.Instance?.Network?.StartHost(port);
                Plugin.Instance.State.ConnectionStatus = ConnectionStatus.Hosting;
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
                    string status = p.IsReady ? "<color=lime>[READY]</color>" : "<color=yellow>[WAIT]</color>";
                    UIFactory.CreateNativeText($"{p.PlayerName} | {p.SelectedAirfield} | {status}", playerGroup.transform, 20, TextAlignmentOptions.Left);
                }
            }

            UIFactory.CreateNativeText("Select Airfield:", _contentRoot.transform, 18, TextAlignmentOptions.Left);
            var afGroup = new GameObject("AFGrid", typeof(RectTransform)).transform;
            afGroup.SetParent(_contentRoot.transform, false);
            var grid = afGroup.gameObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(220, 40);
            grid.spacing = new Vector2(10, 10);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 3;

            foreach (var name in AirfieldHelper.GetAirfieldNames())
            {
                var btn = UIFactory.CreateNativeButton(name, afGroup, 40);
                if (lobby?.LocalSelectedAirfield == name) btn.GetComponent<Image>().color = Color.cyan;
                btn.onClick.AddListener(() => {
                    lobby?.SetLocalAirfield(name);
                    Plugin.Instance.Network?.SendLobbyAirfieldSelect(name);
                    RefreshUI();
                });
            }

            var spacer = new GameObject("Spacer", typeof(RectTransform), typeof(LayoutElement));
            spacer.transform.SetParent(_contentRoot.transform, false);
            spacer.GetComponent<LayoutElement>().flexibleHeight = 1;

            var readyBtn = UIFactory.CreateNativeButton(lobby.LocalIsReady ? "NOT READY" : "READY", _contentRoot.transform);
            readyBtn.GetComponent<Image>().color = lobby.LocalIsReady ? Color.red : Color.green;
            readyBtn.onClick.AddListener(() => {
                lobby.SetLocalReady(!lobby.LocalIsReady);
                Plugin.Instance.Network?.SendLobbyPlayerReady(lobby.LocalIsReady);
                RefreshUI();
            });

            if (isHost)
            {
                var start = UIFactory.CreateNativeButton("START GAME", _contentRoot.transform);
                start.interactable = lobby.AreAllPlayersReady;
                start.onClick.AddListener(() => {
                    Plugin.Log?.LogInfo("[MultiplayerMenu] Starting game...");
                    lobby.StartGame();
                    Plugin.Instance.Network?.SendLobbyStartGame(lobby.MapName, lobby.SpawnType);
                });
            }

            UIFactory.CreateNativeButton("LEAVE", _contentRoot.transform).onClick.AddListener(() => {
                Plugin.Instance?.Network?.Disconnect();
                Plugin.Instance.Discovery?.StopBroadcasting();
                Plugin.Instance.Lobby?.LeaveLobby();
                Plugin.Instance.State.ConnectionStatus = ConnectionStatus.Disconnected;
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
                Plugin.Instance?.Network?.StartClient(_connectIP, p);
                SetScreen(LobbyScreen.Lobby);
            });
            UIFactory.CreateNativeButton("BACK", _contentRoot.transform).onClick.AddListener(() => SetScreen(LobbyScreen.MainMenu));
        }

        private void DrawBrowse()
        {
            UIFactory.CreateNativeText("LAN BROWSER", _contentRoot.transform, 36);
            
            var listContainer = UIFactory.CreateVerticalGroup(_contentRoot.transform, 5, 0);
            listContainer.GetComponent<LayoutElement>().flexibleHeight = 1;
            
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
                        Plugin.Log?.LogInfo($"[MultiplayerMenu] Joining game: {game.IPAddress}:{game.Port}");
                        Plugin.Instance?.Network?.StartClient(game.IPAddress, game.Port);
                        Plugin.Instance.State.ConnectionStatus = ConnectionStatus.Connecting;
                        SetScreen(LobbyScreen.Lobby);
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
