using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using TCAMultiplayer.Networking;
using TCAMultiplayer.Game;

namespace TCAMultiplayer.UI
{
    /// <summary>
    /// Native Canvas+TextMeshPro respawn screen shown when the player dies.
    /// Matches the MultiplayerMenu/UIFactory style.
    /// </summary>
    public class RespawnScreen : MonoBehaviour
    {
        public static RespawnScreen Instance { get; private set; }

        private GameObject _canvas;
        private GameObject _contentRoot;

        // Selection state
        private int _selectedAirfieldIndex = 0;
        private string[] _airfieldNames = new string[0];
        private int _selectedLoadoutIndex = 0;
        private string[] _loadoutNames = new string[0];

        // Selector buttons (replaced TMP_Dropdown with native popup selectors)
        private Button _loadoutSelector;
        private Button _airfieldSelector;

        // Events
        public event Action<string, LobbySpawnType> OnRespawnRequested;

        public void Initialize()
        {
            Instance = this;
        }

        /// <summary>
        /// Show the respawn screen overlay.
        /// Creates the Canvas fresh each time (destroyed on hide).
        /// </summary>
        public void Show()
        {
            if (_canvas != null) return; // Already visible

            Plugin.Log?.LogInfo("[RespawnScreen] Showing native respawn screen");

            // Unlock and show cursor so player can interact with the UI
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            // Refresh airfield list
            _airfieldNames = AirfieldHelper.GetAirfieldNames();
            if (_airfieldNames == null) _airfieldNames = new string[0];
            
            // Initialize to the player's currently selected airfield from lobby
            var lobby = Plugin.Instance?.Lobby;
            string currentAirfield = lobby?.LocalSelectedAirfield;
            _selectedAirfieldIndex = 0;
            if (!string.IsNullOrEmpty(currentAirfield) && _airfieldNames != null)
            {
                for (int i = 0; i < _airfieldNames.Length; i++)
                {
                    if (_airfieldNames[i] == currentAirfield)
                    {
                        _selectedAirfieldIndex = i;
                        break;
                    }
                }
            }

            BuildUI();
        }

        /// <summary>
        /// Hide and destroy the respawn screen overlay.
        /// </summary>
        public void Hide()
        {
            if (_canvas != null)
            {
                Destroy(_canvas);
                _canvas = null;
                _contentRoot = null;
                _loadoutSelector = null;
                _airfieldSelector = null;

                // Re-lock cursor for flight mode
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;

                Plugin.Log?.LogInfo("[RespawnScreen] Hidden respawn screen");
            }
        }

        public bool IsVisible => _canvas != null;

        private void Update()
        {
            if (_canvas == null) return;

            // Force cursor visible while respawn screen is up (game may re-lock it)
            if (Cursor.lockState != CursorLockMode.None || !Cursor.visible)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }

            // R key = respawn shortcut
            if (Input.GetKeyDown(KeyCode.R))
            {
                DoRespawn();
            }
        }

        private void OnDestroy()
        {
            Hide();
            if (Instance == this) Instance = null;
        }

        private void BuildUI()
        {
            // Create Canvas (same setup as MultiplayerMenu)
            _canvas = new GameObject("RespawnScreen_Canvas", typeof(RectTransform));
            DontDestroyOnLoad(_canvas);
            _canvas.hideFlags = HideFlags.HideAndDontSave;

            var canvas = _canvas.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 1000; // Above everything

            var scaler = _canvas.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            _canvas.AddComponent<GraphicRaycaster>();

            // Semi-transparent background
            var bg = new GameObject("Background", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(_canvas.transform, false);
            bg.GetComponent<Image>().color = new Color(0, 0, 0, 0.85f);
            var bgRect = bg.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.sizeDelta = Vector2.zero;

            // Content panel (centered)
            _contentRoot = new GameObject("Content", typeof(RectTransform));
            _contentRoot.transform.SetParent(_canvas.transform, false);
            var contentRect = _contentRoot.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0.5f, 0.5f);
            contentRect.anchorMax = new Vector2(0.5f, 0.5f);
            contentRect.pivot = new Vector2(0.5f, 0.5f);
            contentRect.sizeDelta = new Vector2(600, 700);

            var layout = _contentRoot.AddComponent<VerticalLayoutGroup>();
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            layout.spacing = 12;
            layout.padding = new RectOffset(40, 40, 30, 30);
            layout.childAlignment = TextAnchor.UpperCenter;

            // Panel background (matching native QMB panel style)
            var panelBg = _contentRoot.AddComponent<Image>();
            panelBg.color = UIFactory.GetPanelFillColor();
            UIFactory.AddGreenBorder(_contentRoot);

            // Title
            UIFactory.CreateNativeText("AIRCRAFT DESTROYED", _contentRoot.transform, 36);

            // Score display
            var localScore = ScoreTracker.Instance?.GetPlayerScore(
                Plugin.Instance?.Network?.LocalPeerId ?? 0);
            if (localScore != null)
            {
                UIFactory.CreateNativeText(
                    $"Kills: {localScore.Kills}  |  Deaths: {localScore.Deaths}",
                    _contentRoot.transform, 20);
            }

            // Spacer
            CreateSpacer(_contentRoot.transform, 10);

            // Current Aircraft (read-only - no aircraft selection mid-match)
            var currentAircraft = Plugin.Instance?.Lobby?.LocalSelectedAircraft ?? "AV8B";
            var aircraftDisplayName = LoadoutHelper.GetAircraftDisplayName(currentAircraft);
            UIFactory.CreateNativeText($"Aircraft: {aircraftDisplayName}", _contentRoot.transform, 20, TextAlignmentOptions.Left);
            
            // Get loadouts for current aircraft
            var loadoutList = LoadoutHelper.GetLoadoutNamesForAircraft(currentAircraft);
            _loadoutNames = loadoutList?.ToArray() ?? new string[0];
            
            // Initialize to current selection
            string currentLoadout = Plugin.Instance?.Lobby?.LocalSelectedLoadout ?? "Clean";
            _selectedLoadoutIndex = 0;
            for (int i = 0; i < _loadoutNames.Length; i++)
            {
                if (_loadoutNames[i] == currentLoadout)
                {
                    _selectedLoadoutIndex = i;
                    break;
                }
            }
            
            // Loadout dropdown
            if (_loadoutNames.Length > 0)
            {
                var loadoutOptions = new List<string>(_loadoutNames);
                _loadoutSelector = UIFactory.CreateLabeledSelector("Loadout:", loadoutOptions, _selectedLoadoutIndex, _contentRoot.transform, (index) => {
                    _selectedLoadoutIndex = index;
                    if (_loadoutNames.Length > index)
                    {
                        Plugin.Instance?.Lobby?.SetLocalLoadout(_loadoutNames[index]);
                    }
                });
            }
            else
            {
                UIFactory.CreateNativeText("No loadouts found", _contentRoot.transform, 16);
            }

            // Spacer
            CreateSpacer(_contentRoot.transform, 10);

            // Airfield dropdown
            if (_airfieldNames.Length > 0)
            {
                var airfieldOptions = new List<string>(_airfieldNames);
                _airfieldSelector = UIFactory.CreateLabeledSelector("Airfield:", airfieldOptions, _selectedAirfieldIndex, _contentRoot.transform, (index) => {
                    _selectedAirfieldIndex = index;
                    if (_airfieldNames.Length > index)
                    {
                        Plugin.Instance?.Lobby?.SetLocalAirfield(_airfieldNames[index]);
                    }
                });
            }
            else
            {
                UIFactory.CreateNativeText("No airfields found", _contentRoot.transform, 16);
            }

            // Spacer
            CreateSpacer(_contentRoot.transform, 10);

            // Spawn type - read-only dropdown showing host's selection
            var currentSpawnType = Plugin.Instance?.Lobby?.SpawnType ?? LobbySpawnType.Runway;
            var spawnTypeNames = new List<string> { "Air (300m)", "Runway", "Ramp" };
            int spawnTypeIndex = (int)currentSpawnType;
            
            var spawnBtn = UIFactory.CreateLabeledSelector("Spawn Type:", spawnTypeNames, spawnTypeIndex, _contentRoot.transform, null);
            if (spawnBtn != null)
            {
                spawnBtn.interactable = false; // Read-only
            }

            // Spacer
            CreateSpacer(_contentRoot.transform, 15);

            // Respawn button (big, green)
            var respawnBtn = UIFactory.CreateNativeButton("RESPAWN  (R)", _contentRoot.transform, 60);
            if (respawnBtn != null)
            {
                var img = respawnBtn.GetComponent<Image>();
                if (img != null) img.color = new Color(0.2f, 0.7f, 0.2f, 1f);
                respawnBtn.onClick.AddListener(DoRespawn);

                // Auto-select for controller support
                if (EventSystem.current != null)
                {
                    EventSystem.current.SetSelectedGameObject(null);
                    EventSystem.current.SetSelectedGameObject(respawnBtn.gameObject);
                }
            }
        }

        private void DoRespawn()
        {
            string airfield = (_airfieldNames.Length > _selectedAirfieldIndex)
                ? _airfieldNames[_selectedAirfieldIndex]
                : null;

            if (string.IsNullOrEmpty(airfield))
            {
                // Fallback
                var names = AirfieldHelper.GetAirfieldNames();
                if (names != null && names.Length > 0) airfield = names[0];
            }

            // Use the spawn type set by the host
            var spawnType = Plugin.Instance?.Lobby?.SpawnType ?? LobbySpawnType.Runway;

            Plugin.Log?.LogInfo($"[RespawnScreen] Respawn requested: airfield={airfield} spawnType={spawnType}");

            if (OnRespawnRequested == null)
            {
                Plugin.Log?.LogError("[RespawnScreen] OnRespawnRequested has NO subscribers! Falling back to direct spawn.");
                // Direct fallback: call SpawnManager directly
                Plugin.Instance?.Lobby?.SendRespawnRequest();
                bool success = Game.SpawnManager.Instance?.SpawnPlayerAtAirfield(airfield, "AV8B", spawnType) ?? false;
                if (success)
                {
                    Hide();
                    Plugin.Instance?.GameState?.OnSpawnComplete();
                }
                else
                {
                    Plugin.Log?.LogError("[RespawnScreen] Direct fallback respawn ALSO failed!");
                }
                return;
            }

            OnRespawnRequested?.Invoke(airfield, spawnType);
        }

        private static void CreateSpacer(Transform parent, float height)
        {
            var spacer = new GameObject("Spacer", typeof(RectTransform), typeof(LayoutElement));
            spacer.transform.SetParent(parent, false);
            spacer.GetComponent<LayoutElement>().minHeight = height;
            spacer.GetComponent<LayoutElement>().preferredHeight = height;
        }
    }
}
