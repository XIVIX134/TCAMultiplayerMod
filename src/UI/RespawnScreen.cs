using System;
using UnityEngine;
using UnityEngine.UI;
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

        // Airfield button tracking for highlight
        private Button[] _airfieldButtons;

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
                _airfieldButtons = null;

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
            bg.GetComponent<Image>().color = new Color(0, 0, 0, 0.75f);
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

            // Panel background
            var panelBg = _contentRoot.AddComponent<Image>();
            panelBg.color = new Color(0.08f, 0.08f, 0.12f, 0.95f);

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

            // Airfield selection header
            UIFactory.CreateNativeText("SELECT AIRFIELD:", _contentRoot.transform, 18, TextAlignmentOptions.Left);

            // Airfield list (scrollable area with grid)
            var afContainer = new GameObject("AirfieldContainer", typeof(RectTransform));
            afContainer.transform.SetParent(_contentRoot.transform, false);

            var afLayout = afContainer.AddComponent<VerticalLayoutGroup>();
            afLayout.childControlHeight = false;
            afLayout.childControlWidth = true;
            afLayout.childForceExpandHeight = false;
            afLayout.childForceExpandWidth = true;
            afLayout.spacing = 5;

            var afLE = afContainer.AddComponent<LayoutElement>();
            afLE.preferredHeight = 200;
            afLE.flexibleHeight = 1;

            if (_airfieldNames.Length > 0)
            {
                _airfieldButtons = new Button[_airfieldNames.Length];
                for (int i = 0; i < _airfieldNames.Length; i++)
                {
                    int idx = i; // Capture for closure
                    var btn = UIFactory.CreateNativeButton(_airfieldNames[i], afContainer.transform, 38);
                    if (btn != null)
                    {
                        _airfieldButtons[i] = btn;
                        btn.onClick.AddListener(() => SelectAirfield(idx));

                        // Highlight default selection
                        if (i == _selectedAirfieldIndex)
                        {
                            var img = btn.GetComponent<Image>();
                            if (img != null) img.color = Color.cyan;
                        }
                    }
                }
            }
            else
            {
                UIFactory.CreateNativeText("No airfields found", afContainer.transform, 16);
            }

            // Spacer
            CreateSpacer(_contentRoot.transform, 10);

            // Spawn type - read-only buttons showing host's selection
            UIFactory.CreateNativeText("Spawn Type (Host Selection):", _contentRoot.transform, 18, TextAlignmentOptions.Left);
            
            var stRow = new GameObject("SpawnTypeRow", typeof(RectTransform));
            stRow.transform.SetParent(_contentRoot.transform, false);
            var stLayout = stRow.AddComponent<HorizontalLayoutGroup>();
            stLayout.childControlHeight = true;
            stLayout.childControlWidth = true;
            stLayout.childForceExpandHeight = true;
            stLayout.childForceExpandWidth = true;
            stLayout.spacing = 8;
            var stLE = stRow.AddComponent<LayoutElement>();
            stLE.preferredHeight = 42;

            var currentSpawnType = Plugin.Instance?.Lobby?.SpawnType ?? LobbySpawnType.Runway;
            var spawnTypeNames = new[] { "Air (300m)", "Runway", "Ramp" };
            
            for (int i = 0; i < spawnTypeNames.Length; i++)
            {
                var spawnType = (LobbySpawnType)i;
                var btn = UIFactory.CreateNativeButton(spawnTypeNames[i], stRow.transform, 40);
                if (btn != null)
                {
                    btn.interactable = false; // Read-only
                    
                    var img = btn.GetComponent<Image>();
                    if (img != null)
                    {
                        if (spawnType == currentSpawnType)
                        {
                            // Highlight the host's selection
                            img.color = new Color(0.0f, 0.7f, 0.8f, 1f); // Cyan for selected
                        }
                        else
                        {
                            // Darker gray for unselected
                            img.color = new Color(0.3f, 0.3f, 0.3f, 1f);
                        }
                    }
                }
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
            }
        }

        private void SelectAirfield(int index)
        {
            _selectedAirfieldIndex = index;

            // Update button highlights
            if (_airfieldButtons != null)
            {
                for (int i = 0; i < _airfieldButtons.Length; i++)
                {
                    if (_airfieldButtons[i] == null) continue;
                    var img = _airfieldButtons[i].GetComponent<Image>();
                    if (img != null)
                    {
                        img.color = (i == index) ? Color.cyan : new Color(0.2f, 0.2f, 0.2f, 1f);
                    }
                }
            }

            // Also set in lobby manager so the network knows
            if (_airfieldNames.Length > index)
            {
                Plugin.Instance?.Lobby?.SetLocalAirfield(_airfieldNames[index]);
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
