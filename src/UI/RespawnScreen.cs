using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using TCAMultiplayer.Core;
using TCAMultiplayer.Game;

namespace TCAMultiplayer.UI
{
    /// <summary>
    /// Native Canvas+TextMeshPro respawn screen shown when the player dies.
    /// Uses UIFactory for native game look: quiet dark panel, native text, and
    /// the same button clone style used by the game's pause/loadout dialogs.
    /// </summary>
    public class RespawnScreen : MonoBehaviour
    {
        private const string Tag = "RESPAWN-UI";
        private const string Green = "#00FF40";

        private RespawnManager _respawnManager;
        private SpawnManager _spawnManager;
        private GameSession _session;
        private LobbyManager _lobby;

        private GameObject _canvas;
        private GameObject _contentRoot;
        private TextMeshProUGUI _scoreText;
        private Button _respawnButton;
        private Button _aircraftButton;

        private bool _active;
        private bool _selectorOpen;

        // ── Initialization ──────────────────────────────────────────

        public void Init(RespawnManager respawnManager, SpawnManager spawnManager,
                         GameSession session, LobbyManager lobby)
        {
            _respawnManager = respawnManager;
            _spawnManager = spawnManager;
            _session = session;
            _lobby = lobby;

            if (_respawnManager != null)
            {
                _respawnManager.OnDeathDetected += Show;
                _respawnManager.OnRespawnReady += EnableRespawnButton;
            }
        }

        // ── Show / Hide ─────────────────────────────────────────────

        public void Show()
        {
            // If canvas exists, just re-show with refreshed data
            if (_canvas != null)
            {
                _canvas.SetActive(true);
                RefreshScore();
                UnlockCursor();
                _active = true;
                return;
            }

            Log.Info(Tag, "Showing native respawn screen");

            UnlockCursor();
            BuildUI();
            _active = true;
        }

        public void Hide()
        {
            _active = false;

            if (_canvas != null)
            {
                UnityEngine.Object.Destroy(_canvas);
                _canvas = null;
                _contentRoot = null;
                _scoreText = null;
                _respawnButton = null;
                _aircraftButton = null;
                _selectorOpen = false;

                // Re-lock cursor for flight mode
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;

                Log.Info(Tag, "Hidden respawn screen");
            }
        }

        public bool IsVisible => _canvas != null && _active;

        // ── Update ──────────────────────────────────────────────────

        private void Update()
        {
            if (!_active || _canvas == null) return;

            // Force cursor visible while respawn screen is up (game may re-lock it)
            if (Cursor.lockState != CursorLockMode.None || !Cursor.visible)
            {
                UnlockCursor();
            }
            RefreshScore();

            // Respawn shortcut: keyboard R or controller submit (Xbox A / PlayStation X)
            if (IsRespawnInputPressed())
            {
                DoRespawn();
            }
        }

        private static bool IsRespawnInputPressed()
        {
            if (Input.GetKeyDown(KeyCode.R)) return true;

            // Legacy Unity mapping: JoystickButton0 is controller submit on common layouts
            if (Input.GetKeyDown(KeyCode.JoystickButton0)) return true;

            // Fallback to Input Manager "Submit" binding if available
            try
            {
                if (Input.GetButtonDown("Submit")) return true;
            }
            catch { }

            return false;
        }

        // ── Cursor Management ───────────────────────────────────────

        private static void UnlockCursor()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        // ── Respawn Action ──────────────────────────────────────────

        private void DoRespawn()
        {
            if (_selectorOpen) return;
            if (_respawnButton != null && !_respawnButton.interactable) return;

            Log.Info(Tag, "Respawn requested");
            _respawnManager?.RequestRespawn();
            Hide();
        }

        // ── Aircraft / Loadout Selection ────────────────────────────

        private static string GetAircraftButtonText(string aircraft, string loadout)
        {
            if (string.IsNullOrEmpty(aircraft)) aircraft = "F-16C";
            if (string.IsNullOrEmpty(loadout)) loadout = "Clean";
            return $"CHANGE AIRCRAFT > {aircraft} - {loadout}";
        }

        private void OpenAircraftSelector()
        {
            if (_selectorOpen) return;

            var local = _session?.GetLocalPlayer();
            string curAircraft = local?.SelectedAircraft ?? ModConfig.LastAircraft?.Value ?? "F-16C";
            string curLoadout = local?.SelectedLoadout ?? ModConfig.LastLoadout?.Value ?? "Clean";
            var aircraftNames = LoadoutHelper.GetAircraftNames();
            if (aircraftNames == null || aircraftNames.Count == 0)
            {
                Log.Warning(Tag, "No aircraft names available for selector");
                return;
            }

            // Hide the respawn overlay while the native selector is up so the
            // dialog isn't buried under this canvas (sortingOrder 1000)
            _selectorOpen = true;
            _canvas?.SetActive(false);

            UIFactory.ShowNativeLoadoutSelector(curAircraft, aircraftNames, curLoadout, "",
                (aircraft, loadout, ammoBelt) =>
                {
                    _selectorOpen = false;
                    if (_canvas != null && _active) _canvas.SetActive(true);

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

                    var pl = _session?.GetLocalPlayer();
                    if (_aircraftButton != null)
                    {
                        var label = _aircraftButton.GetComponentInChildren<TextMeshProUGUI>();
                        if (label != null)
                            label.text = GetAircraftButtonText(pl?.SelectedAircraft, pl?.SelectedLoadout);
                    }
                    Log.Info(Tag, $"Next-life selection: {pl?.SelectedAircraft} / {pl?.SelectedLoadout}");
                });
        }

        private void EnableRespawnButton()
        {
            if (_respawnButton != null) _respawnButton.interactable = true;
            Log.Info(Tag, "Respawn available");
        }

        // ── Score Refresh ───────────────────────────────────────────

        private void RefreshScore()
        {
            var local = _session?.GetLocalPlayer();
            if (local != null && _scoreText != null)
            {
                _scoreText.text = $"Kills > <color={Green}>{local.Kills}</color>    Deaths > <color={Green}>{local.Deaths}</color>";
            }
        }

        // ── UI Construction ─────────────────────────────────────────

        private void BuildUI()
        {
            // Create Canvas (DontDestroyOnLoad, above everything)
            _canvas = new GameObject("RespawnScreen_Canvas", typeof(RectTransform));
            UnityEngine.Object.DontDestroyOnLoad(_canvas);
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

            // Full-screen dark overlay background
            var bg = new GameObject("Background", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(_canvas.transform, false);
            bg.GetComponent<Image>().color = new Color(0, 0, 0, 0.64f);
            var bgRect = bg.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.sizeDelta = Vector2.zero;

            // Centered content panel
            _contentRoot = new GameObject("Content", typeof(RectTransform));
            _contentRoot.transform.SetParent(_canvas.transform, false);
            var contentRect = _contentRoot.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0.5f, 0.5f);
            contentRect.anchorMax = new Vector2(0.5f, 0.5f);
            contentRect.pivot = new Vector2(0.5f, 0.5f);
            contentRect.sizeDelta = new Vector2(500, 350);

            // VerticalLayoutGroup
            var layout = _contentRoot.AddComponent<VerticalLayoutGroup>();
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            layout.spacing = 12;
            layout.padding = new RectOffset(40, 40, 40, 40);
            layout.childAlignment = TextAnchor.UpperCenter;

            // Panel background (native QMB style)
            var panelBg = _contentRoot.AddComponent<Image>();
            panelBg.color = UIFactory.GetPanelFillColor();
            UIFactory.AddNativeBorder(_contentRoot);

            UIFactory.CreateNativeText("AIRCRAFT DESTROYED >", _contentRoot.transform, 30, TextAlignmentOptions.Left);

            // Spacer
            CreateSpacer(_contentRoot.transform, 8);

            // Score display (kills / deaths)
            var local = _session?.GetLocalPlayer();
            string scoreStr = local != null
                ? $"Kills > <color={Green}>{local.Kills}</color>    Deaths > <color={Green}>{local.Deaths}</color>"
                : $"Kills > <color={Green}>0</color>    Deaths > <color={Green}>0</color>";
            _scoreText = UIFactory.CreateNativeText(scoreStr, _contentRoot.transform, 20, TextAlignmentOptions.Left);

            // Spacer
            CreateSpacer(_contentRoot.transform, 16);

            // Aircraft / loadout for the next life (native selector dialog)
            var local2 = _session?.GetLocalPlayer();
            _aircraftButton = UIFactory.CreateNativeButton(
                GetAircraftButtonText(local2?.SelectedAircraft, local2?.SelectedLoadout),
                _contentRoot.transform, 40);
            if (_aircraftButton != null)
                _aircraftButton.onClick.AddListener(OpenAircraftSelector);

            CreateSpacer(_contentRoot.transform, 8);

            // Respawn button (big, green)
            _respawnButton = UIFactory.CreateNativeButton("RESPAWN >>>", _contentRoot.transform, 60);
            if (_respawnButton != null)
            {
                _respawnButton.onClick.AddListener(DoRespawn);
                _respawnButton.interactable = false; // Disabled until cooldown elapses

                // Auto-select for controller support
                if (EventSystem.current != null)
                {
                    EventSystem.current.SetSelectedGameObject(null);
                    EventSystem.current.SetSelectedGameObject(_respawnButton.gameObject);
                }
            }
        }

        private static void CreateSpacer(Transform parent, float height)
        {
            var spacer = new GameObject("Spacer", typeof(RectTransform), typeof(LayoutElement));
            spacer.transform.SetParent(parent, false);
            spacer.GetComponent<LayoutElement>().minHeight = height;
            spacer.GetComponent<LayoutElement>().preferredHeight = height;
        }

        // ── Cleanup ─────────────────────────────────────────────────

        private void OnDestroy()
        {
            if (_respawnManager != null)
            {
                _respawnManager.OnDeathDetected -= Show;
                _respawnManager.OnRespawnReady -= EnableRespawnButton;
            }
            if (_canvas != null) UnityEngine.Object.Destroy(_canvas);
        }
    }
}
