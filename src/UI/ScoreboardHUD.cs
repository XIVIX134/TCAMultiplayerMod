using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using TCAMultiplayer.Game;
using TCAMultiplayer.Networking;

namespace TCAMultiplayer.UI
{
    /// <summary>
    /// In-game HUD overlay showing kill/death scoreboard and kill feed.
    /// Uses native Unity Canvas + TextMeshPro UI (matching MultiplayerMenu/UIFactory style).
    ///
    /// Always shows a compact K/D counter in the top-left corner.
    /// Hold TAB to show the full scoreboard.
    /// Kill feed appears on the right side for a few seconds after each kill.
    /// </summary>
    public class ScoreboardHUD
    {
        public static ScoreboardHUD Instance { get; private set; }

        // Root canvas for the entire HUD
        private GameObject _canvasGo;
        private Canvas _canvas;

        // Compact K/D panel (top-left, always visible)
        private GameObject _compactPanel;
        private TextMeshProUGUI _compactText;

        // Kill feed panel (top-right)
        private GameObject _killFeedPanel;
        private List<KillFeedUIEntry> _killFeedEntries = new List<KillFeedUIEntry>();
        private const int MAX_KILL_FEED_ENTRIES = 6;

        // Full scoreboard (center, TAB held)
        private GameObject _scoreboardPanel;
        private TextMeshProUGUI _scoreboardTitle;
        private GameObject _scoreboardHeaderRow;
        private GameObject _scoreboardPlayerContainer;
        private List<ScoreboardRowUI> _scoreboardRows = new List<ScoreboardRowUI>();

        // State
        private bool _showFullScoreboard;
        private bool _uiBuilt;

        public ScoreboardHUD()
        {
            Instance = this;
        }

        /// <summary>
        /// Call from PluginRunner.Update() to detect TAB key and update UI.
        /// </summary>
        public void Update()
        {
            _showFullScoreboard = Input.GetKey(KeyCode.Tab);

            if (ScoreTracker.Instance == null) return;
            var gameState = Plugin.Instance?.GameState;
            if (gameState == null || gameState.CurrentState != GameState.InGame)
            {
                if (_uiBuilt) HideAll();
                return;
            }

            if (!_uiBuilt) BuildUI();

            ShowAll();
            UpdateCompactScore();
            UpdateFullScoreboard();
        }

        /// <summary>
        /// OnGUI is now a no-op. All drawing is done via native Canvas UI.
        /// Kept for backwards compatibility with PluginRunner.OnGUI() call.
        /// </summary>
        public void OnGUI()
        {
            // No-op — native UI handles everything now
        }

        // ================================================================
        // UI CONSTRUCTION
        // ================================================================

        private void BuildUI()
        {
            if (_uiBuilt) return;

            // Root canvas — ScreenSpaceOverlay, high sort order so it draws over game UI
            _canvasGo = new GameObject("ScoreboardHUD_Canvas");
            Object.DontDestroyOnLoad(_canvasGo);
            _canvasGo.hideFlags = HideFlags.HideAndDontSave;

            _canvas = _canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;

            var scaler = _canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            // No GraphicRaycaster — HUD is non-interactive (display only)

            BuildCompactPanel();
            BuildKillFeedPanel();
            BuildScoreboardPanel();

            _uiBuilt = true;
        }

        /// <summary>
        /// Compact K/D counter — top-left, always visible during InGame.
        /// </summary>
        private void BuildCompactPanel()
        {
            _compactPanel = new GameObject("CompactKD", typeof(RectTransform));
            _compactPanel.transform.SetParent(_canvasGo.transform, false);

            var rect = _compactPanel.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);  // top-left
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(10f, -55f); // Below the TCA MP status line
            rect.sizeDelta = new Vector2(140f, 38f);

            // Background (matching native QMB panel style)
            var bgImage = _compactPanel.AddComponent<Image>();
            bgImage.color = UIFactory.GetPanelFillColor();
            UIFactory.AddGreenBorder(_compactPanel, 0.6f);

            // Text
            var textGo = new GameObject("KDText", typeof(RectTransform));
            textGo.transform.SetParent(_compactPanel.transform, false);

            var textRect = textGo.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(8f, 0f);
            textRect.offsetMax = new Vector2(-8f, 0f);

            _compactText = textGo.AddComponent<TextMeshProUGUI>();
            _compactText.richText = true;
            _compactText.fontSize = 22f;
            _compactText.fontStyle = FontStyles.Bold;
            _compactText.alignment = TextAlignmentOptions.MidlineLeft;
            _compactText.text = "<color=#00FF00>K:0</color>  <color=#FF4444>D:0</color>";
            _compactText.enableWordWrapping = false;
            _compactText.overflowMode = TextOverflowModes.Overflow;
        }

        /// <summary>
        /// Kill feed panel — top-right, shows recent kills with fade.
        /// </summary>
        private void BuildKillFeedPanel()
        {
            _killFeedPanel = new GameObject("KillFeed", typeof(RectTransform));
            _killFeedPanel.transform.SetParent(_canvasGo.transform, false);

            var rect = _killFeedPanel.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 1f); // top-right
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-10f, -55f);
            rect.sizeDelta = new Vector2(380f, MAX_KILL_FEED_ENTRIES * 26f);

            var layout = _killFeedPanel.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 3f;
            layout.childControlHeight = false;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            layout.childAlignment = TextAnchor.UpperRight;
            layout.padding = new RectOffset(0, 0, 0, 0);

            // Pre-create pool of kill feed entries
            for (int i = 0; i < MAX_KILL_FEED_ENTRIES; i++)
            {
                var entry = CreateKillFeedEntry(_killFeedPanel.transform);
                entry.Root.SetActive(false);
                _killFeedEntries.Add(entry);
            }
        }

        private KillFeedUIEntry CreateKillFeedEntry(Transform parent)
        {
            var root = new GameObject("KillFeedEntry", typeof(RectTransform));
            root.transform.SetParent(parent, false);

            var rootRect = root.GetComponent<RectTransform>();
            rootRect.sizeDelta = new Vector2(0f, 24f);

            // Background (dark green tint matching game style)
            var bg = root.AddComponent<Image>();
            bg.color = new Color(0f, 0.04f, 0.02f, 0.7f);

            // CanvasGroup for alpha fade
            var cg = root.AddComponent<CanvasGroup>();

            // Text
            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(root.transform, false);

            var textRect = textGo.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(5f, 0f);
            textRect.offsetMax = new Vector2(-5f, 0f);

            var le = root.AddComponent<LayoutElement>();
            le.minHeight = 24f;
            le.preferredHeight = 24f;

            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.richText = true;
            tmp.fontSize = 14f;
            tmp.alignment = TextAlignmentOptions.MidlineRight;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Truncate;

            return new KillFeedUIEntry { Root = root, Text = tmp, CanvasGroup = cg, Background = bg };
        }

        /// <summary>
        /// Full scoreboard panel — center, shown when TAB is held.
        /// </summary>
        private void BuildScoreboardPanel()
        {
            _scoreboardPanel = new GameObject("Scoreboard", typeof(RectTransform));
            _scoreboardPanel.transform.SetParent(_canvasGo.transform, false);

            var rect = _scoreboardPanel.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f); // center
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(400f, 300f); // Will resize dynamically

            // Background (matching native QMB panel style)
            var bg = _scoreboardPanel.AddComponent<Image>();
            bg.color = UIFactory.GetPanelFillColor();
            UIFactory.AddGreenBorder(_scoreboardPanel);

            // Vertical layout for the whole scoreboard
            var layout = _scoreboardPanel.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 2f;
            layout.childControlHeight = false;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            layout.padding = new RectOffset(10, 10, 10, 10);

            var fitter = _scoreboardPanel.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            // Title
            var titleGo = new GameObject("Title", typeof(RectTransform));
            titleGo.transform.SetParent(_scoreboardPanel.transform, false);
            var titleLe = titleGo.AddComponent<LayoutElement>();
            titleLe.minHeight = 36f;
            titleLe.preferredHeight = 36f;

            _scoreboardTitle = titleGo.AddComponent<TextMeshProUGUI>();
            _scoreboardTitle.richText = true;
            _scoreboardTitle.text = "SCOREBOARD";
            _scoreboardTitle.fontSize = 20f;
            _scoreboardTitle.fontStyle = FontStyles.Bold;
            _scoreboardTitle.alignment = TextAlignmentOptions.Center;
            _scoreboardTitle.color = Color.white;

            // Header row
            _scoreboardHeaderRow = CreateScoreboardRow(_scoreboardPanel.transform, "Player", "Kills", "Deaths", isHeader: true);

            // Player container
            _scoreboardPlayerContainer = new GameObject("Players", typeof(RectTransform));
            _scoreboardPlayerContainer.transform.SetParent(_scoreboardPanel.transform, false);

            var containerLayout = _scoreboardPlayerContainer.AddComponent<VerticalLayoutGroup>();
            containerLayout.spacing = 1f;
            containerLayout.childControlHeight = false;
            containerLayout.childControlWidth = true;
            containerLayout.childForceExpandHeight = false;
            containerLayout.childForceExpandWidth = true;

            var containerFitter = _scoreboardPlayerContainer.AddComponent<ContentSizeFitter>();
            containerFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var containerLe = _scoreboardPlayerContainer.AddComponent<LayoutElement>();
            containerLe.flexibleHeight = 1f;

            _scoreboardPanel.SetActive(false);
        }

        private GameObject CreateScoreboardRow(Transform parent, string nameText, string killsText, string deathsText, bool isHeader = false, bool isLocal = false)
        {
            var row = new GameObject("Row", typeof(RectTransform));
            row.transform.SetParent(parent, false);

            var rowLe = row.AddComponent<LayoutElement>();
            rowLe.minHeight = 28f;
            rowLe.preferredHeight = 28f;

            // Highlight for local player
            if (isLocal)
            {
                var rowBg = row.AddComponent<Image>();
                rowBg.color = new Color(0f, 0.15f, 0.06f, 0.5f);
            }

            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 5f;
            hlg.childControlHeight = true;
            hlg.childControlWidth = true;
            hlg.childForceExpandHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.padding = new RectOffset(5, 5, 0, 0);

            float fontSize = isHeader ? 14f : 15f;
            Color textColor = isHeader ? new Color(0.67f, 0.67f, 0.67f) : Color.white;

            // Name column (flexible width)
            var nameGo = new GameObject("Name", typeof(RectTransform));
            nameGo.transform.SetParent(row.transform, false);
            var nameTmp = nameGo.AddComponent<TextMeshProUGUI>();
            nameTmp.richText = true;
            nameTmp.text = nameText;
            nameTmp.fontSize = fontSize;
            nameTmp.alignment = TextAlignmentOptions.MidlineLeft;
            nameTmp.color = textColor;
            nameTmp.enableWordWrapping = false;
            nameTmp.overflowMode = TextOverflowModes.Ellipsis;
            var nameLe = nameGo.AddComponent<LayoutElement>();
            nameLe.flexibleWidth = 1f;
            nameLe.minWidth = 100f;

            // Kills column
            var killsGo = new GameObject("Kills", typeof(RectTransform));
            killsGo.transform.SetParent(row.transform, false);
            var killsTmp = killsGo.AddComponent<TextMeshProUGUI>();
            killsTmp.richText = true;
            killsTmp.text = killsText;
            killsTmp.fontSize = fontSize;
            killsTmp.fontStyle = isHeader ? FontStyles.Normal : FontStyles.Bold;
            killsTmp.alignment = TextAlignmentOptions.Center;
            killsTmp.color = isHeader ? textColor : new Color(0.4f, 1f, 0.4f); // lime for values
            killsTmp.enableWordWrapping = false;
            var killsLe = killsGo.AddComponent<LayoutElement>();
            killsLe.preferredWidth = 65f;
            killsLe.minWidth = 65f;

            // Deaths column
            var deathsGo = new GameObject("Deaths", typeof(RectTransform));
            deathsGo.transform.SetParent(row.transform, false);
            var deathsTmp = deathsGo.AddComponent<TextMeshProUGUI>();
            deathsTmp.richText = true;
            deathsTmp.text = deathsText;
            deathsTmp.fontSize = fontSize;
            deathsTmp.fontStyle = isHeader ? FontStyles.Normal : FontStyles.Bold;
            deathsTmp.alignment = TextAlignmentOptions.Center;
            deathsTmp.color = isHeader ? textColor : new Color(1f, 0.3f, 0.3f); // red for values
            deathsTmp.enableWordWrapping = false;
            var deathsLe = deathsGo.AddComponent<LayoutElement>();
            deathsLe.preferredWidth = 65f;
            deathsLe.minWidth = 65f;

            return row;
        }

        // ================================================================
        // UI UPDATE (per-frame)
        // ================================================================

        private void UpdateCompactScore()
        {
            ulong localId = Plugin.Instance?.Network?.GetLocalPlayerId() ?? 0;
            var score = localId != 0 ? ScoreTracker.Instance.GetPlayerScore(localId) : null;
            int kills = score?.Kills ?? 0;
            int deaths = score?.Deaths ?? 0;

            _compactText.text = $"<color=#00FF00>K:{kills}</color>  <color=#FF4444>D:{deaths}</color>";
        }

        private void UpdateKillFeed()
        {
            var feed = ScoreTracker.Instance.GetActiveKillFeed();

            for (int i = 0; i < _killFeedEntries.Count; i++)
            {
                var uiEntry = _killFeedEntries[i];

                if (i < feed.Count)
                {
                    var data = feed[i];
                    float age = Time.time - data.Timestamp;
                    float alpha = Mathf.Clamp01(1f - (age / 8f)); // Fade out over 8s

                    uiEntry.Root.SetActive(true);
                    uiEntry.CanvasGroup.alpha = alpha;
                    uiEntry.Background.color = new Color(0f, 0.04f, 0.02f, 0.7f * alpha);
                    uiEntry.Text.text = $"<color=#00FF00>{data.KillerName}</color> [{data.WeaponName}] <color=#FF4444>{data.VictimName}</color>";
                }
                else
                {
                    uiEntry.Root.SetActive(false);
                }
            }
        }

        private void UpdateFullScoreboard()
        {
            if (_showFullScoreboard)
            {
                if (!_scoreboardPanel.activeSelf)
                    _scoreboardPanel.SetActive(true);

                var scores = ScoreTracker.Instance.GetScores();
                ulong localId = Plugin.Instance?.Network?.GetLocalPlayerId() ?? 0;

                // Rebuild rows if count changed
                if (_scoreboardRows.Count != scores.Count)
                {
                    // Clear existing
                    foreach (var row in _scoreboardRows)
                        Object.Destroy(row.Root);
                    _scoreboardRows.Clear();

                    // Create new rows
                    for (int i = 0; i < scores.Count; i++)
                    {
                        var s = scores[i];
                        bool isLocal = s.PeerId == localId;
                        var rowGo = CreateScoreboardRow(
                            _scoreboardPlayerContainer.transform,
                            isLocal ? $"<color=#00FFFF>{s.PlayerName}</color>" : s.PlayerName,
                            s.Kills.ToString(),
                            s.Deaths.ToString(),
                            isHeader: false,
                            isLocal: isLocal
                        );

                        var nameText = rowGo.transform.Find("Name")?.GetComponent<TextMeshProUGUI>();
                        var killsText = rowGo.transform.Find("Kills")?.GetComponent<TextMeshProUGUI>();
                        var deathsText = rowGo.transform.Find("Deaths")?.GetComponent<TextMeshProUGUI>();

                        _scoreboardRows.Add(new ScoreboardRowUI
                        {
                            Root = rowGo,
                            NameText = nameText,
                            KillsText = killsText,
                            DeathsText = deathsText,
                            PeerId = s.PeerId
                        });
                    }
                }
                else
                {
                    // Update existing rows
                    for (int i = 0; i < scores.Count && i < _scoreboardRows.Count; i++)
                    {
                        var s = scores[i];
                        var row = _scoreboardRows[i];
                        bool isLocal = s.PeerId == localId;

                        if (row.NameText != null)
                            row.NameText.text = isLocal ? $"<color=#00FFFF>{s.PlayerName}</color>" : s.PlayerName;
                        if (row.KillsText != null)
                            row.KillsText.text = s.Kills.ToString();
                        if (row.DeathsText != null)
                            row.DeathsText.text = s.Deaths.ToString();
                    }
                }
            }
            else
            {
                if (_scoreboardPanel.activeSelf)
                    _scoreboardPanel.SetActive(false);
            }
        }

        // ================================================================
        // VISIBILITY
        // ================================================================

        private void ShowAll()
        {
            if (_canvasGo != null && !_canvasGo.activeSelf)
                _canvasGo.SetActive(true);
        }

        private void HideAll()
        {
            if (_canvasGo != null && _canvasGo.activeSelf)
                _canvasGo.SetActive(false);
        }

        /// <summary>
        /// Destroy all UI objects (call on cleanup).
        /// </summary>
        public void Destroy()
        {
            if (_canvasGo != null)
            {
                Object.Destroy(_canvasGo);
                _canvasGo = null;
            }
            _uiBuilt = false;
            _killFeedEntries.Clear();
            _scoreboardRows.Clear();
        }

        // ================================================================
        // INNER TYPES
        // ================================================================

        private class KillFeedUIEntry
        {
            public GameObject Root;
            public TextMeshProUGUI Text;
            public CanvasGroup CanvasGroup;
            public Image Background;
        }

        private class ScoreboardRowUI
        {
            public GameObject Root;
            public TextMeshProUGUI NameText;
            public TextMeshProUGUI KillsText;
            public TextMeshProUGUI DeathsText;
            public ulong PeerId;
        }
    }
}
