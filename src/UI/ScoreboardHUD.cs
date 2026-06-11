using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Falcon.Game2.UI.HUD;
using TCAMultiplayer.Core;
using TCAMultiplayer.Game;

namespace TCAMultiplayer.UI
{
    /// <summary>
    /// In-game HUD overlay: compact K/D counter, kill feed with fade, full TAB scoreboard.
    /// Uses UIFactory native panels/text colors so it sits beside the game's HUD
    /// instead of looking like a separate debug overlay.
    /// </summary>
    public class ScoreboardHUD : MonoBehaviour
    {
        private const string Tag = "SCOREBOARD";
        private const int MaxKillFeedEntries = 6;
        private const float KillFeedFadeDuration = 8f;
        private const string Green = "#00FF40";

        private GameSession _session;
        private ScoreTracker _scoreTracker;
        private GameObject _canvasGo;
        private GameObject _compactPanel;
        private TextMeshProUGUI _killsText;
        private TextMeshProUGUI _deathsText;
        private Transform _compactHudParent;
        private GameHUD _compactHud;
        private GameObject _killFeedPanel;
        private readonly List<KillFeedUIEntry> _killFeedEntries = new List<KillFeedUIEntry>();
        private GameObject _scoreboardPanel;
        private GameObject _scoreboardPlayerContainer;
        private readonly List<ScoreboardRowUI> _scoreboardRows = new List<ScoreboardRowUI>();
        private bool _showFullScoreboard;
        private bool _uiBuilt;

        public void Init(GameSession session, ScoreTracker scoreTracker)
        {
            _session = session;
            _scoreTracker = scoreTracker;
        }

        private void Update()
        {
            _showFullScoreboard = Input.GetKey(KeyCode.Tab);
            if (_session == null || _scoreTracker == null) return;
            var state = _session.StateMachine.CurrentState;
            if (!ShouldShowHud(state))
            {
                if (_uiBuilt) HideAll();
                return;
            }

            if (!_uiBuilt) BuildUI();
            SetOverlaySorting(state == GameState.Respawning);
            ShowAll();
            if (state == GameState.Respawning)
                AttachCompactToOverlayCanvas();
            else
                EnsureCompactNativeHud();
            UpdateCompactScore();
            UpdateKillFeed();
            UpdateFullScoreboard();
        }

        private static bool ShouldShowHud(GameState state)
        {
            return state == GameState.InGame || state == GameState.Respawning;
        }

        // ── UI Construction ─────────────────────────────────────────

        private void BuildUI()
        {
            if (_uiBuilt) return;
            _canvasGo = new GameObject("ScoreboardHUD_Canvas");
            Object.DontDestroyOnLoad(_canvasGo);
            _canvasGo.hideFlags = HideFlags.HideAndDontSave;
            var canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = _canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            // No GraphicRaycaster — HUD is display-only
            BuildCompactPanel();
            BuildKillFeedPanel();
            BuildScoreboardPanel();
            _uiBuilt = true;
        }

        private void BuildCompactPanel()
        {
            _compactPanel = new GameObject("TCAMP_HUDScore", typeof(RectTransform));
            _compactPanel.SetActive(false);
            var rect = _compactPanel.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -18f);
            rect.sizeDelta = new Vector2(420f, 34f);

            var layout = _compactPanel.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 8f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;

            _killsText = CreateHudScoreChip(_compactPanel.transform, new Color(0f, 0.22f, 0.08f, 0.68f));
            _deathsText = CreateHudScoreChip(_compactPanel.transform, new Color(0.22f, 0.03f, 0.03f, 0.68f));

            ConfigureCompactText(_killsText, GetKillTextColor());
            ConfigureCompactText(_deathsText, GetDeathTextColor());
            SetCompactScoreText(0, 0);
            AttachCompactToNativeHud();
        }

        private void BuildKillFeedPanel()
        {
            _killFeedPanel = new GameObject("KillFeed", typeof(RectTransform));
            _killFeedPanel.transform.SetParent(_canvasGo.transform, false);
            var rect = _killFeedPanel.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-10f, -55f);
            rect.sizeDelta = new Vector2(380f, MaxKillFeedEntries * 26f);
            var layout = _killFeedPanel.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 3f;
            layout.childControlHeight = false;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            layout.childAlignment = TextAnchor.UpperRight;
            for (int i = 0; i < MaxKillFeedEntries; i++)
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
            root.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 24f);
            var bg = root.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.52f);
            var cg = root.AddComponent<CanvasGroup>();
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
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode = TextOverflowModes.Truncate;
            return new KillFeedUIEntry { Root = root, Text = tmp, CanvasGroup = cg, Background = bg };
        }

        private void BuildScoreboardPanel()
        {
            _scoreboardPanel = new GameObject("Scoreboard", typeof(RectTransform));
            _scoreboardPanel.transform.SetParent(_canvasGo.transform, false);
            var rect = _scoreboardPanel.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(400f, 300f);
            _scoreboardPanel.AddComponent<Image>().color = UIFactory.GetPanelFillColor();
            UIFactory.AddNativeBorder(_scoreboardPanel);
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
            var titleTmp = titleGo.AddComponent<TextMeshProUGUI>();
            titleTmp.richText = true;
            titleTmp.text = "SCOREBOARD >";
            titleTmp.fontSize = 20f;
            titleTmp.fontStyle = FontStyles.Bold;
            titleTmp.alignment = TextAlignmentOptions.Center;
            titleTmp.color = UIFactory.AccentColor;

            // Header row
            CreateScoreboardRow(_scoreboardPanel.transform, "Player", "Kills", "Deaths", isHeader: true);

            // Player container
            _scoreboardPlayerContainer = new GameObject("Players", typeof(RectTransform));
            _scoreboardPlayerContainer.transform.SetParent(_scoreboardPanel.transform, false);
            var cl = _scoreboardPlayerContainer.AddComponent<VerticalLayoutGroup>();
            cl.spacing = 1f;
            cl.childControlHeight = false;
            cl.childControlWidth = true;
            cl.childForceExpandHeight = false;
            cl.childForceExpandWidth = true;
            _scoreboardPlayerContainer.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _scoreboardPlayerContainer.AddComponent<LayoutElement>().flexibleHeight = 1f;
            _scoreboardPanel.SetActive(false);
        }

        private GameObject CreateScoreboardRow(Transform parent, string nameText,
            string killsText, string deathsText, bool isHeader = false, bool isLocal = false)
        {
            var row = new GameObject("Row", typeof(RectTransform));
            row.transform.SetParent(parent, false);
            var rowLe = row.AddComponent<LayoutElement>();
            rowLe.minHeight = 28f;
            rowLe.preferredHeight = 28f;
            if (isLocal)
                row.AddComponent<Image>().color = new Color(0f, 0.15f, 0.06f, 0.5f);
            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 5f;
            hlg.childControlHeight = true;
            hlg.childControlWidth = true;
            hlg.childForceExpandHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.padding = new RectOffset(5, 5, 0, 0);
            float fontSize = isHeader ? 14f : 15f;
            Color textColor = isHeader ? UIFactory.MutedTextColor : UIFactory.AccentColor;

            // Name column (flexible)
            var nameGo = new GameObject("Name", typeof(RectTransform));
            nameGo.transform.SetParent(row.transform, false);
            var nameTmp = nameGo.AddComponent<TextMeshProUGUI>();
            nameTmp.richText = true;
            nameTmp.text = nameText;
            nameTmp.fontSize = fontSize;
            nameTmp.alignment = TextAlignmentOptions.MidlineLeft;
            nameTmp.color = textColor;
            nameTmp.textWrappingMode = TextWrappingModes.NoWrap;
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
            killsTmp.color = isHeader ? textColor : UIFactory.AccentColor;
            killsTmp.textWrappingMode = TextWrappingModes.NoWrap;
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
            deathsTmp.color = isHeader ? textColor : UIFactory.AccentColor;
            deathsTmp.textWrappingMode = TextWrappingModes.NoWrap;
            var deathsLe = deathsGo.AddComponent<LayoutElement>();
            deathsLe.preferredWidth = 65f;
            deathsLe.minWidth = 65f;
            return row;
        }

        // ── UI Update ────────────────────────────────────────────────

        private void UpdateCompactScore()
        {
            if (_killsText == null || _deathsText == null) return;

            var local = _session.GetLocalPlayer();
            int kills = local?.Kills ?? 0;
            int deaths = local?.Deaths ?? 0;
            ConfigureCompactText(_killsText, GetKillTextColor());
            ConfigureCompactText(_deathsText, GetDeathTextColor());
            SetCompactScoreText(kills, deaths);
        }

        private void EnsureCompactNativeHud()
        {
            if (_compactPanel == null) return;
            if (AttachCompactToNativeHud()) return;

            if (_compactPanel.transform.parent != _canvasGo.transform)
                _compactPanel.transform.SetParent(_canvasGo.transform, false);
            _compactPanel.SetActive(false);
        }

        private void AttachCompactToOverlayCanvas()
        {
            if (_compactPanel == null || _canvasGo == null) return;

            if (_compactPanel.transform.parent != _canvasGo.transform)
                _compactPanel.transform.SetParent(_canvasGo.transform, false);

            ConfigureCompactRect();
            _compactPanel.transform.SetAsLastSibling();
            _compactPanel.SetActive(true);
        }

        private bool AttachCompactToNativeHud()
        {
            var hud = GameHUD.Instance;
            if (hud == null) return false;

            var parent = GetNativeHudCanvasRoot(hud);
            if (parent == null) return false;

            if (_compactHud == hud && _compactHudParent == parent && _compactPanel.transform.parent == parent)
                return true;

            _compactHud = hud;
            _compactHudParent = parent;
            _compactPanel.transform.SetParent(parent, false);
            _compactPanel.transform.SetAsLastSibling();

            ConfigureCompactRect();

            _compactPanel.SetActive(true);
            ConfigureCompactText(_killsText, GetKillTextColor());
            ConfigureCompactText(_deathsText, GetDeathTextColor());

            return true;
        }

        private void ConfigureCompactRect()
        {
            var rect = _compactPanel.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -18f);
            rect.sizeDelta = new Vector2(420f, 34f);
        }

        private void SetOverlaySorting(bool aboveRespawnScreen)
        {
            var canvas = _canvasGo != null ? _canvasGo.GetComponent<Canvas>() : null;
            if (canvas != null)
                canvas.sortingOrder = aboveRespawnScreen ? 1100 : 100;
        }

        private static RectTransform GetNativeHudCanvasRoot(GameHUD hud)
        {
            var field = typeof(GameHUD).GetField("Canvas",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var canvas = field?.GetValue(hud) as Canvas;
            return canvas != null ? canvas.transform as RectTransform : null;
        }

        private static TextMeshProUGUI CreateNativeHudText(Transform parent)
        {
            var prefab = GetNativeHudTextPrefab();
            TextMeshProUGUI tmp;
            if (prefab != null)
            {
                tmp = Object.Instantiate(prefab, parent, false);
                tmp.gameObject.name = "MPScoreText";
            }
            else
            {
                var go = new GameObject("MPScoreText", typeof(RectTransform), typeof(TextMeshProUGUI));
                go.transform.SetParent(parent, false);
                tmp = go.GetComponent<TextMeshProUGUI>();
            }

            var rect = tmp.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return tmp;
        }

        private static TextMeshProUGUI GetNativeHudTextPrefab()
        {
            var hud = GameHUD.Instance;
            if (hud == null) return null;

            foreach (var fieldName in new[] { "AirspeedMode", "SelectedWeaponText", "GunStaticText", "GunAmmoText" })
            {
                var field = typeof(GameHUD).GetField(fieldName,
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var tmp = field?.GetValue(hud) as TextMeshProUGUI;
                if (tmp != null) return tmp;
            }

            return null;
        }

        private TextMeshProUGUI CreateHudScoreChip(Transform parent, Color backgroundColor)
        {
            var chip = new GameObject("ScoreChip", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            chip.transform.SetParent(parent, false);

            var image = chip.GetComponent<Image>();
            image.color = backgroundColor;
            image.raycastTarget = false;

            var le = chip.GetComponent<LayoutElement>();
            le.minWidth = 190f;
            le.preferredWidth = 190f;
            le.minHeight = 34f;
            le.preferredHeight = 34f;

            return CreateNativeHudText(chip.transform);
        }

        private void ConfigureCompactText(TextMeshProUGUI text, Color color)
        {
            if (text == null) return;

            text.richText = true;
            text.fontSize = 21f;
            text.fontStyle = FontStyles.Normal;
            text.alignment = TextAlignmentOptions.Center;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;
            text.raycastTarget = false;
            text.color = color;
        }

        private void SetCompactScoreText(int kills, int deaths)
        {
            if (_killsText != null)
                _killsText.text = $"Kills  {kills:00}";
            if (_deathsText != null)
                _deathsText.text = $"Deaths  {deaths:00}";
        }

        private static Color GetKillTextColor()
        {
            try
            {
                var color = GameHUD.HudColorSet.BaseColor;
                if (color.maxColorComponent > 0.05f)
                {
                    color.a = 1f;
                    return color;
                }
            }
            catch
            {
                // HUD may not be initialized during menu/test transitions.
            }

            var fallback = UIFactory.AccentColor;
            fallback.a = 1f;
            return fallback;
        }

        private static Color GetDeathTextColor()
        {
            try
            {
                var color = GameHUD.InstrumentColorSet.LockedColor;
                if (color.maxColorComponent > 0.05f)
                {
                    color.a = 1f;
                    return color;
                }
            }
            catch
            {
                // HUD may not be initialized during menu/test transitions.
            }

            return new Color(1f, 0.25f, 0.2f, 1f);
        }

        private void UpdateKillFeed()
        {
            var feed = _scoreTracker.KillFeed;
            for (int i = 0; i < _killFeedEntries.Count; i++)
            {
                var uiEntry = _killFeedEntries[i];
                if (i < feed.Count)
                {
                    var data = feed[i];
                    float age = Time.time - data.Timestamp;
                    float alpha = Mathf.Clamp01(1f - (age / KillFeedFadeDuration));
                    uiEntry.Root.SetActive(alpha > 0.01f);
                    uiEntry.CanvasGroup.alpha = alpha;
                    uiEntry.Background.color = new Color(0f, 0f, 0f, 0.52f * alpha);
                    uiEntry.Text.text = data.IsSystemMessage
                        ? $"<color={Green}>{data.Message}</color>"
                        : $"<color={Green}>{data.KillerName}</color> [{data.WeaponName}] <color={Green}>{data.VictimName}</color>";
                }
                else
                {
                    uiEntry.Root.SetActive(false);
                }
            }
        }

        private void UpdateFullScoreboard()
        {
            if (!_showFullScoreboard)
            {
                if (_scoreboardPanel.activeSelf) _scoreboardPanel.SetActive(false);
                return;
            }
            if (!_scoreboardPanel.activeSelf) _scoreboardPanel.SetActive(true);
            var scores = _scoreTracker.GetSortedScoreboard();
            ulong localId = _session.LocalPeerId;

            if (_scoreboardRows.Count != scores.Count)
            {
                foreach (var row in _scoreboardRows) Object.Destroy(row.Root);
                _scoreboardRows.Clear();
                for (int i = 0; i < scores.Count; i++)
                {
                    var s = scores[i];
                    bool isLocal = s.PeerId == localId;
                    var rowGo = CreateScoreboardRow(
                        _scoreboardPlayerContainer.transform,
                        isLocal ? $"<color={Green}>{s.PlayerName}</color>" : s.PlayerName,
                        s.Kills.ToString(), s.Deaths.ToString(),
                        isHeader: false, isLocal: isLocal);
                    _scoreboardRows.Add(new ScoreboardRowUI
                    {
                        Root = rowGo,
                        NameText = rowGo.transform.Find("Name")?.GetComponent<TextMeshProUGUI>(),
                        KillsText = rowGo.transform.Find("Kills")?.GetComponent<TextMeshProUGUI>(),
                        DeathsText = rowGo.transform.Find("Deaths")?.GetComponent<TextMeshProUGUI>(),
                        PeerId = s.PeerId
                    });
                }
            }
            else
            {
                for (int i = 0; i < scores.Count && i < _scoreboardRows.Count; i++)
                {
                    var s = scores[i];
                    var row = _scoreboardRows[i];
                    bool isLocal = s.PeerId == localId;
                    if (row.NameText != null)
                        row.NameText.text = isLocal ? $"<color={Green}>{s.PlayerName}</color>" : s.PlayerName;
                    if (row.KillsText != null) row.KillsText.text = s.Kills.ToString();
                    if (row.DeathsText != null) row.DeathsText.text = s.Deaths.ToString();
                }
            }
        }

        private void ShowAll()
        {
            if (_canvasGo != null && !_canvasGo.activeSelf) _canvasGo.SetActive(true);
        }

        private void HideAll()
        {
            if (_canvasGo != null && _canvasGo.activeSelf) _canvasGo.SetActive(false);
            if (_compactPanel != null && _compactPanel.activeSelf) _compactPanel.SetActive(false);
        }

        private void OnDestroy()
        {
            if (_compactPanel != null) { Object.Destroy(_compactPanel); _compactPanel = null; }
            if (_canvasGo != null) { Object.Destroy(_canvasGo); _canvasGo = null; }
            _uiBuilt = false;
            _killFeedEntries.Clear();
            _scoreboardRows.Clear();
        }

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
            public TextMeshProUGUI NameText, KillsText, DeathsText;
            public ulong PeerId;
        }
    }
}
