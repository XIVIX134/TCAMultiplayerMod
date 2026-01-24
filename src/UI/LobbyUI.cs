using System;
using System.Collections.Generic;
using UnityEngine;
using TCAMultiplayer.Networking;

namespace TCAMultiplayer.UI
{
    /// <summary>
    /// Lobby screen states
    /// </summary>
    public enum LobbyScreen
    {
        None,
        MainMenu,       // Host/Join/Browse
        HostSetup,      // Configure and create lobby
        Browse,         // Browse LAN games
        DirectConnect,  // Enter IP to connect
        Lobby,          // In lobby, waiting for players
        Loading,        // Loading map
        InGame,         // Game in progress
        Respawn         // Dead, waiting to respawn
    }
    
    /// <summary>
    /// IMGUI-based lobby user interface
    /// </summary>
    public class LobbyUI
    {
        // Singleton
        public static LobbyUI Instance { get; private set; }
        
        // Current screen
        public LobbyScreen CurrentScreen { get; private set; } = LobbyScreen.None;
        
        // UI state
        private bool _showUI = false;
        private Vector2 _playerListScroll;
        private Vector2 _gameListScroll;
        
        // Input fields
        private string _hostName = "";
        private string _connectIP = "127.0.0.1";
        private string _connectPort = "7777";
        private string _hostPort = "7777";
        private int _selectedSpawnType = 1; // 0=Air, 1=Runway, 2=Ramp
        private int _selectedAirfieldIndex = 0;
        
        // Cached data
        private string[] _airfieldNames = new string[0];
        private string[] _spawnTypeNames = { "Air (300m)", "Runway", "Ramp" };
        
        // Styling
        private GUIStyle _titleStyle;
        private GUIStyle _headerStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _buttonStyle;
        private GUIStyle _boxStyle;
        private bool _stylesInitialized = false;
        
        // Window dimensions
        private Rect _windowRect = new Rect(100, 100, 500, 600);
        private const float WINDOW_WIDTH = 500f;
        private const float WINDOW_HEIGHT = 600f;
        
        // References
        private LobbyManager _lobbyManager;
        private LanDiscovery _lanDiscovery;
        
        // Events
        public event Action<string, int> OnHostGame;        // hostName, port
        public event Action<string, int> OnJoinGame;        // ip, port
        public event Action OnLeaveLobby;
        public event Action OnReadyToggle;
        public event Action<string> OnAirfieldSelected;     // airfieldName
        public event Action<LobbySpawnType> OnSpawnTypeChanged;
        public event Action OnStartGame;
        public event Action OnRespawnRequest;
        
        public LobbyUI()
        {
            Instance = this;
            _hostName = LobbyManager.GenerateRandomName();
        }
        
        /// <summary>
        /// Initialize with references
        /// </summary>
        public void Initialize(LobbyManager lobbyManager, LanDiscovery lanDiscovery)
        {
            _lobbyManager = lobbyManager;
            _lanDiscovery = lanDiscovery;
            
            // Subscribe to lobby events
            if (_lobbyManager != null)
            {
                _lobbyManager.OnLobbyStateChanged += OnLobbyStateChanged;
                _lobbyManager.OnGameStarting += OnGameStarting;
                _lobbyManager.OnSpawnPlayers += OnSpawnPlayersReceived;
            }
        }
        
        /// <summary>
        /// Set available airfields for selection
        /// </summary>
        public void SetAirfields(string[] airfieldNames)
        {
            _airfieldNames = airfieldNames ?? new string[0];
            _selectedAirfieldIndex = 0;
        }
        
        /// <summary>
        /// Show the lobby UI
        /// </summary>
        public void Show()
        {
            _showUI = true;
            if (CurrentScreen == LobbyScreen.None)
            {
                CurrentScreen = LobbyScreen.MainMenu;
            }
        }
        
        /// <summary>
        /// Hide the lobby UI
        /// </summary>
        public void Hide()
        {
            _showUI = false;
        }
        
        /// <summary>
        /// Toggle visibility
        /// </summary>
        public void Toggle()
        {
            if (_showUI)
                Hide();
            else
                Show();
        }
        
        /// <summary>
        /// Set current screen
        /// </summary>
        public void SetScreen(LobbyScreen screen)
        {
            CurrentScreen = screen;
            
            // Start/stop LAN discovery based on screen
            if (screen == LobbyScreen.Browse)
            {
                _lanDiscovery?.StartListening();
            }
            else if (screen != LobbyScreen.Browse)
            {
                _lanDiscovery?.StopListening();
            }
        }
        
        /// <summary>
        /// Show respawn screen
        /// </summary>
        public void ShowRespawnScreen()
        {
            CurrentScreen = LobbyScreen.Respawn;
            _showUI = true;
        }
        
        /// <summary>
        /// Hide respawn screen
        /// </summary>
        public void HideRespawnScreen()
        {
            if (CurrentScreen == LobbyScreen.Respawn)
            {
                CurrentScreen = LobbyScreen.InGame;
                _showUI = false;
            }
        }
        
        private void OnLobbyStateChanged()
        {
            // Update UI state from lobby
        }
        
        private void OnGameStarting()
        {
            CurrentScreen = LobbyScreen.Loading;
        }
        
        private void OnSpawnPlayersReceived()
        {
            CurrentScreen = LobbyScreen.InGame;
            _showUI = false;
        }
        
        /// <summary>
        /// Initialize GUI styles
        /// </summary>
        private void InitStyles()
        {
            if (_stylesInitialized) return;
            
            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 24,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            
            _headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold
            };
            
            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                richText = true
            };
            
            _buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 14
            };
            
            _boxStyle = new GUIStyle(GUI.skin.box);
            
            _stylesInitialized = true;
        }
        
        /// <summary>
        /// Draw the UI (call from OnGUI)
        /// </summary>
        public void OnGUI()
        {
            if (!_showUI) return;
            
            InitStyles();
            
            // Center window
            _windowRect.x = (Screen.width - WINDOW_WIDTH) / 2;
            _windowRect.y = (Screen.height - WINDOW_HEIGHT) / 2;
            _windowRect.width = WINDOW_WIDTH;
            _windowRect.height = WINDOW_HEIGHT;
            
            // Draw window
            GUI.Box(_windowRect, "");
            GUILayout.BeginArea(_windowRect);
            
            switch (CurrentScreen)
            {
                case LobbyScreen.MainMenu:
                    DrawMainMenu();
                    break;
                case LobbyScreen.HostSetup:
                    DrawHostSetup();
                    break;
                case LobbyScreen.Browse:
                    DrawBrowse();
                    break;
                case LobbyScreen.DirectConnect:
                    DrawDirectConnect();
                    break;
                case LobbyScreen.Lobby:
                    DrawLobby();
                    break;
                case LobbyScreen.Loading:
                    DrawLoading();
                    break;
                case LobbyScreen.Respawn:
                    DrawRespawn();
                    break;
            }
            
            GUILayout.EndArea();
        }
        
        /// <summary>
        /// Draw main menu screen
        /// </summary>
        private void DrawMainMenu()
        {
            GUILayout.Space(20);
            GUILayout.Label("TCA Multiplayer", _titleStyle);
            GUILayout.Space(30);
            
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.BeginVertical(GUILayout.Width(300));
            
            if (GUILayout.Button("Host Game", _buttonStyle, GUILayout.Height(50)))
            {
                CurrentScreen = LobbyScreen.HostSetup;
            }
            
            GUILayout.Space(10);
            
            if (GUILayout.Button("Browse LAN Games", _buttonStyle, GUILayout.Height(50)))
            {
                CurrentScreen = LobbyScreen.Browse;
                _lanDiscovery?.StartListening();
            }
            
            GUILayout.Space(10);
            
            if (GUILayout.Button("Direct Connect", _buttonStyle, GUILayout.Height(50)))
            {
                CurrentScreen = LobbyScreen.DirectConnect;
            }
            
            GUILayout.Space(30);
            
            if (GUILayout.Button("Close", _buttonStyle, GUILayout.Height(40)))
            {
                Hide();
            }
            
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }
        
        /// <summary>
        /// Draw host setup screen
        /// </summary>
        private void DrawHostSetup()
        {
            GUILayout.Space(20);
            GUILayout.Label("Host Game", _titleStyle);
            GUILayout.Space(20);
            
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.BeginVertical(GUILayout.Width(350));
            
            // Host name
            GUILayout.Label("Host Name:", _headerStyle);
            _hostName = GUILayout.TextField(_hostName, GUILayout.Height(30));
            
            GUILayout.Space(10);
            
            // Port
            GUILayout.Label("Port:", _headerStyle);
            _hostPort = GUILayout.TextField(_hostPort, GUILayout.Height(30));
            
            GUILayout.Space(10);
            
            // Spawn type
            GUILayout.Label("Spawn Type:", _headerStyle);
            _selectedSpawnType = GUILayout.SelectionGrid(_selectedSpawnType, _spawnTypeNames, 3, GUILayout.Height(40));
            
            GUILayout.Space(30);
            
            if (GUILayout.Button("Create Lobby", _buttonStyle, GUILayout.Height(50)))
            {
                int port = 7777;
                int.TryParse(_hostPort, out port);
                OnHostGame?.Invoke(_hostName, port);
                CurrentScreen = LobbyScreen.Lobby;
            }
            
            GUILayout.Space(10);
            
            if (GUILayout.Button("Back", _buttonStyle, GUILayout.Height(40)))
            {
                CurrentScreen = LobbyScreen.MainMenu;
            }
            
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }
        
        /// <summary>
        /// Draw browse LAN games screen
        /// </summary>
        private void DrawBrowse()
        {
            GUILayout.Space(20);
            GUILayout.Label("Browse LAN Games", _titleStyle);
            GUILayout.Space(20);
            
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.BeginVertical(GUILayout.Width(450));
            
            // Game list
            GUILayout.Label("Available Games:", _headerStyle);
            
            _gameListScroll = GUILayout.BeginScrollView(_gameListScroll, _boxStyle, GUILayout.Height(300));
            
            var games = _lanDiscovery?.GetDiscoveredGames() ?? new List<DiscoveredGame>();
            
            if (games.Count == 0)
            {
                GUILayout.Label("Searching for games...", _labelStyle);
                GUILayout.Label("<i>Make sure the host is on the same network</i>", _labelStyle);
            }
            else
            {
                foreach (var game in games)
                {
                    GUILayout.BeginHorizontal(_boxStyle);
                    GUILayout.BeginVertical();
                    GUILayout.Label($"<b>{game.HostName}</b>", _labelStyle);
                    GUILayout.Label($"{game.IPAddress}:{game.Port} | {game.PlayerCount}/{game.MaxPlayers} players | {game.MapName}", _labelStyle);
                    GUILayout.EndVertical();
                    
                    if (GUILayout.Button("Join", GUILayout.Width(80), GUILayout.Height(40)))
                    {
                        OnJoinGame?.Invoke(game.IPAddress, game.Port);
                        CurrentScreen = LobbyScreen.Lobby;
                    }
                    GUILayout.EndHorizontal();
                    GUILayout.Space(5);
                }
            }
            
            GUILayout.EndScrollView();
            
            GUILayout.Space(10);
            
            if (GUILayout.Button("Refresh", _buttonStyle, GUILayout.Height(35)))
            {
                _lanDiscovery?.ClearDiscoveredGames();
            }
            
            GUILayout.Space(10);
            
            if (GUILayout.Button("Back", _buttonStyle, GUILayout.Height(40)))
            {
                _lanDiscovery?.StopListening();
                CurrentScreen = LobbyScreen.MainMenu;
            }
            
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }
        
        /// <summary>
        /// Draw direct connect screen
        /// </summary>
        private void DrawDirectConnect()
        {
            GUILayout.Space(20);
            GUILayout.Label("Direct Connect", _titleStyle);
            GUILayout.Space(20);
            
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.BeginVertical(GUILayout.Width(350));
            
            // IP address
            GUILayout.Label("IP Address:", _headerStyle);
            _connectIP = GUILayout.TextField(_connectIP, GUILayout.Height(30));
            
            GUILayout.Space(10);
            
            // Port
            GUILayout.Label("Port:", _headerStyle);
            _connectPort = GUILayout.TextField(_connectPort, GUILayout.Height(30));
            
            GUILayout.Space(30);
            
            if (GUILayout.Button("Connect", _buttonStyle, GUILayout.Height(50)))
            {
                int port = 7777;
                int.TryParse(_connectPort, out port);
                OnJoinGame?.Invoke(_connectIP, port);
                CurrentScreen = LobbyScreen.Lobby;
            }
            
            GUILayout.Space(10);
            
            if (GUILayout.Button("Back", _buttonStyle, GUILayout.Height(40)))
            {
                CurrentScreen = LobbyScreen.MainMenu;
            }
            
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }
        
        /// <summary>
        /// Draw lobby screen
        /// </summary>
        private void DrawLobby()
        {
            GUILayout.Space(20);
            
            bool isHost = _lobbyManager?.IsHost ?? false;
            string title = isHost ? "Lobby (Host)" : "Lobby";
            GUILayout.Label(title, _titleStyle);
            
            GUILayout.Space(10);
            
            // Host info
            if (_lobbyManager != null)
            {
                GUILayout.Label($"Host: {_lobbyManager.HostName}", _labelStyle);
                GUILayout.Label($"Map: {_lobbyManager.MapName}", _labelStyle);
                GUILayout.Label($"Spawn: {_spawnTypeNames[(int)_lobbyManager.SpawnType]}", _labelStyle);
            }
            
            GUILayout.Space(10);
            
            // Player list
            GUILayout.Label("Players:", _headerStyle);
            
            _playerListScroll = GUILayout.BeginScrollView(_playerListScroll, _boxStyle, GUILayout.Height(200));
            
            if (_lobbyManager != null)
            {
                foreach (var player in _lobbyManager.Players.Values)
                {
                    string status = player.IsReady ? "<color=lime>[READY]</color>" : "<color=yellow>[NOT READY]</color>";
                    string hostTag = player.IsHost ? " (Host)" : "";
                    string airfield = string.IsNullOrEmpty(player.SelectedAirfield) ? "No airfield" : player.SelectedAirfield;
                    
                    GUILayout.BeginHorizontal(_boxStyle);
                    GUILayout.Label($"{player.PlayerName}{hostTag}", _labelStyle, GUILayout.Width(150));
                    GUILayout.Label(airfield, _labelStyle, GUILayout.Width(120));
                    GUILayout.Label(status, _labelStyle);
                    GUILayout.EndHorizontal();
                }
            }
            
            GUILayout.EndScrollView();
            
            GUILayout.Space(10);
            
            // Airfield selection
            GUILayout.Label("Select Airfield:", _headerStyle);
            if (_airfieldNames.Length > 0)
            {
                int newIndex = GUILayout.SelectionGrid(_selectedAirfieldIndex, _airfieldNames, 3, GUILayout.Height(60));
                if (newIndex != _selectedAirfieldIndex)
                {
                    _selectedAirfieldIndex = newIndex;
                    OnAirfieldSelected?.Invoke(_airfieldNames[_selectedAirfieldIndex]);
                }
            }
            else
            {
                GUILayout.Label("<i>Loading airfields...</i>", _labelStyle);
            }
            
            GUILayout.Space(10);
            
            // Host-only controls
            if (isHost)
            {
                GUILayout.Label("Spawn Type (Host):", _headerStyle);
                int newSpawnType = GUILayout.SelectionGrid(_selectedSpawnType, _spawnTypeNames, 3, GUILayout.Height(35));
                if (newSpawnType != _selectedSpawnType)
                {
                    _selectedSpawnType = newSpawnType;
                    OnSpawnTypeChanged?.Invoke((LobbySpawnType)_selectedSpawnType);
                }
                
                GUILayout.Space(10);
            }
            
            // Ready button
            bool isReady = _lobbyManager?.LocalIsReady ?? false;
            string readyText = isReady ? "Not Ready" : "Ready";
            Color originalColor = GUI.backgroundColor;
            GUI.backgroundColor = isReady ? Color.red : Color.green;
            
            if (GUILayout.Button(readyText, _buttonStyle, GUILayout.Height(45)))
            {
                OnReadyToggle?.Invoke();
            }
            
            GUI.backgroundColor = originalColor;
            
            GUILayout.Space(5);
            
            // Start game button (host only, all ready)
            if (isHost)
            {
                bool allReady = _lobbyManager?.AreAllPlayersReady ?? false;
                GUI.enabled = allReady;
                
                if (GUILayout.Button("Start Game", _buttonStyle, GUILayout.Height(45)))
                {
                    OnStartGame?.Invoke();
                }
                
                GUI.enabled = true;
                
                if (!allReady && _lobbyManager?.PlayerCount >= 2)
                {
                    GUILayout.Label("<color=yellow>Waiting for all players to ready up...</color>", _labelStyle);
                }
                else if (_lobbyManager?.PlayerCount < 2)
                {
                    GUILayout.Label("<color=yellow>Waiting for more players...</color>", _labelStyle);
                }
            }
            
            GUILayout.Space(5);
            
            // Leave button
            if (GUILayout.Button("Leave Lobby", _buttonStyle, GUILayout.Height(35)))
            {
                OnLeaveLobby?.Invoke();
                CurrentScreen = LobbyScreen.MainMenu;
            }
        }
        
        /// <summary>
        /// Draw loading screen
        /// </summary>
        private void DrawLoading()
        {
            GUILayout.Space(20);
            GUILayout.Label("Loading...", _titleStyle);
            GUILayout.Space(30);
            
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.BeginVertical(GUILayout.Width(400));
            
            GUILayout.Label("Loading map and waiting for all players...", _labelStyle);
            
            GUILayout.Space(20);
            
            // Show player loading status
            if (_lobbyManager != null)
            {
                foreach (var player in _lobbyManager.Players.Values)
                {
                    string status = player.IsLoaded ? "<color=lime>[LOADED]</color>" : "<color=yellow>[LOADING...]</color>";
                    GUILayout.Label($"{player.PlayerName}: {status}", _labelStyle);
                }
            }
            
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }
        
        /// <summary>
        /// Draw respawn screen
        /// </summary>
        private void DrawRespawn()
        {
            // Semi-transparent background
            GUI.color = new Color(1, 1, 1, 0.8f);
            
            GUILayout.Space(Screen.height / 3);
            
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.BeginVertical(GUILayout.Width(400));
            
            GUI.color = Color.white;
            
            GUILayout.Label("You have been destroyed!", _titleStyle);
            GUILayout.Space(20);
            
            GUILayout.Label("<size=20>Press <b>R</b> to respawn</size>", _labelStyle);
            GUILayout.Space(10);
            
            string airfield = _lobbyManager?.LocalSelectedAirfield ?? "your airfield";
            GUILayout.Label($"You will respawn at: {airfield}", _labelStyle);
            
            GUILayout.Space(20);
            
            if (GUILayout.Button("Respawn (R)", _buttonStyle, GUILayout.Height(50)))
            {
                OnRespawnRequest?.Invoke();
            }
            
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }
        
        /// <summary>
        /// Check for respawn key press
        /// </summary>
        public void Update()
        {
            if (CurrentScreen == LobbyScreen.Respawn)
            {
                if (Input.GetKeyDown(KeyCode.R))
                {
                    OnRespawnRequest?.Invoke();
                }
            }
        }
        
        /// <summary>
        /// Get selected spawn type
        /// </summary>
        public LobbySpawnType GetSelectedSpawnType()
        {
            return (LobbySpawnType)_selectedSpawnType;
        }
        
        /// <summary>
        /// Get selected airfield
        /// </summary>
        public string GetSelectedAirfield()
        {
            if (_airfieldNames.Length > 0 && _selectedAirfieldIndex < _airfieldNames.Length)
            {
                return _airfieldNames[_selectedAirfieldIndex];
            }
            return "";
        }
    }
}
