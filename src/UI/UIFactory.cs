using System;
using System.Collections.Generic;
using System.Reflection;
using Cysharp.Threading.Tasks;
using Falcon.Game2;
using Falcon.Game2.UI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TCAMultiplayer.Core;

namespace TCAMultiplayer.UI
{
    /// <summary>
    /// Small runtime UI factory built around the game's own menu prefabs.
    /// The goal is to clone native buttons/text and only synthesize controls
    /// the base game does not expose, such as direct-connect text fields.
    /// </summary>
    public static class UIFactory
    {
        private const string Tag = "UI-FACTORY";
        private static readonly Color NativeGreen = new Color(0f, 1f, 0.25f, 1f);
        private static readonly Color NativeDimGreen = new Color(0f, 0.48f, 0.16f, 1f);

        private static Button _buttonPrefab;
        private static TextMeshProUGUI _textPrefab;
        private static Sprite _panelSprite;
        private static Image.Type _panelImageType = Image.Type.Sliced;
        private static Color _panelFillColor = new Color(0f, 0f, 0f, 0.68f);
        private static Color _panelOutlineColor = NativeGreen;
        private static Color _accentColor = NativeGreen;
        private static Color _mutedTextColor = NativeDimGreen;
        private static ColorBlock _buttonColors = ColorBlock.defaultColorBlock;
        private static bool _hasPanelStyle;
        private static MapSelectorDialog _cachedMapSelector;
        private static RearmRefuelDialog _cachedLoadoutSelector;

        public static bool HasPrefabs => _buttonPrefab != null && _textPrefab != null;
        public static Color AccentColor => _accentColor;
        public static Color MutedTextColor => _mutedTextColor;
        public static event Action<bool> NativeDialogActiveChanged;

        public static void Initialize(MainMenu mainMenu)
        {
            if (HasPrefabs && _hasPanelStyle) return;

            CaptureQuickMissionStyle();
            CaptureMainMenuButton(mainMenu);

            if (_buttonPrefab == null)
                CaptureAnySceneButton();

            if (_buttonPrefab != null)
            {
                _buttonColors = _buttonPrefab.colors;
                if (_textPrefab == null)
                    _textPrefab = _buttonPrefab.GetComponentInChildren<TextMeshProUGUI>(true);
                Log.Info(Tag, $"Native button style ready: {_buttonPrefab.name}");
            }
            else
            {
                Log.Warning(Tag, "No native button prefab found; using synthetic fallback controls");
            }

            if (_textPrefab != null)
                _textPrefab.color = NativeGreen;
        }

        private static void CaptureMainMenuButton(MainMenu mainMenu)
        {
            if (_buttonPrefab != null || mainMenu == null) return;

            try
            {
                var field = typeof(MainMenu).GetField("ArenaButton",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                _buttonPrefab = field?.GetValue(mainMenu) as Button;
                _textPrefab = _buttonPrefab?.GetComponentInChildren<TextMeshProUGUI>(true);
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Failed to capture MainMenu button: {ex.Message}");
            }
        }

        private static void CaptureQuickMissionStyle()
        {
            try
            {
                var qmb = UnityEngine.Object.FindFirstObjectByType<QuickMissionBuilder>(FindObjectsInactive.Include);
                if (qmb == null) return;

                if (_textPrefab == null && qmb.Description != null)
                    _textPrefab = qmb.Description;

                CaptureQuickMissionButton(qmb);
                CacheQuickMissionDialogs(qmb);

                if (qmb.UIRoot == null || _hasPanelStyle) return;

                var images = qmb.UIRoot.GetComponentsInChildren<Image>(true);
                foreach (var img in images)
                {
                    if (img == null || img.sprite == null || img.type != Image.Type.Sliced)
                        continue;

                    _panelSprite = img.sprite;
                    _panelImageType = img.type;
                    _panelOutlineColor = NativeGreen;
                    _hasPanelStyle = true;
                    Log.Info(Tag, $"Captured native sliced UI sprite: {img.name}");
                    break;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Failed to capture QuickMission UI style: {ex.Message}");
            }
        }

        private static void CaptureQuickMissionButton(QuickMissionBuilder qmb)
        {
            if (qmb == null) return;

            var fields = new[] { "MapButton", "LoadoutButton", "MissionButton", "TimeOfDayButton", "FlyButton", "BackButton" };
            foreach (var fieldName in fields)
            {
                var button = GetPrivateField<Button>(qmb, fieldName);
                if (button == null || button.GetComponentInChildren<TextMeshProUGUI>(true) == null)
                    continue;

                _buttonPrefab = button;
                _textPrefab = button.GetComponentInChildren<TextMeshProUGUI>(true);
                return;
            }
        }

        private static void CacheQuickMissionDialogs(QuickMissionBuilder qmb)
        {
            if (qmb == null) return;

            if (_cachedMapSelector == null)
                _cachedMapSelector = GetPrivateField<MapSelectorDialog>(qmb, "MapSelector");

            if (_cachedDialog == null)
                _cachedDialog = GetPrivateField<MultiSelectDialog>(qmb, "FlexibleSelector");

            if (_cachedLoadoutSelector == null)
                _cachedLoadoutSelector = GetPrivateField<RearmRefuelDialog>(qmb, "LoadoutSelector");
        }

        private static T GetPrivateField<T>(object instance, string fieldName) where T : class
        {
            if (instance == null || string.IsNullOrEmpty(fieldName)) return null;

            var field = instance.GetType().GetField(fieldName,
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            return field?.GetValue(instance) as T;
        }

        private static void CaptureAnySceneButton()
        {
            try
            {
                var allButtons = UnityEngine.Object.FindObjectsByType<Button>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                foreach (var btn in allButtons)
                {
                    if (btn == null || btn.GetComponentInChildren<TextMeshProUGUI>(true) == null)
                        continue;

                    _buttonPrefab = btn;
                    _textPrefab = btn.GetComponentInChildren<TextMeshProUGUI>(true);
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Fallback button search failed: {ex.Message}");
            }
        }

        // ── Core Controls ───────────────────────────────────────────────

        public static Button CreateNativeButton(string label, Transform parent, float height = 50)
        {
            GameObject go;
            Button button;

            if (_buttonPrefab != null)
            {
                go = UnityEngine.Object.Instantiate(_buttonPrefab.gameObject, parent, false);
                button = go.GetComponent<Button>() ?? go.AddComponent<Button>();
            }
            else
            {
                go = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(Button));
                go.transform.SetParent(parent, false);
                button = go.GetComponent<Button>();
                go.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.65f);
                AddNativeBorder(go, 0.8f);
                AddButtonTextChild(go.transform);
            }

            go.name = label + " Button";
            go.SetActive(true);

            button.onClick.RemoveAllListeners();
            button.colors = _buttonColors;

            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;

            TintButtonText(button);
            SetButtonText(button, label);
            return button;
        }

        private static void TintButtonText(Button button)
        {
            if (button == null) return;

            foreach (var tmp in button.GetComponentsInChildren<TextMeshProUGUI>(true))
                tmp.color = NativeGreen;

            var image = button.GetComponent<Image>();
            if (image != null)
                image.raycastTarget = true;
        }

        public static void SetButtonText(Button button, string text)
        {
            if (button == null) return;

            var tmp = button.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp == null)
                tmp = AddButtonTextChild(button.transform);

            tmp.richText = true;
            tmp.parseCtrlCharacters = true;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = NativeGreen;
            tmp.text = text ?? string.Empty;
        }

        private static void StyleDestructiveButton(Button button)
        {
            if (button == null) return;

            var normal = new Color(0.42f, 0f, 0f, 0.95f);
            var hover = new Color(0.72f, 0.04f, 0.04f, 1f);
            var pressed = new Color(0.28f, 0f, 0f, 1f);
            var text = new Color(1f, 0.22f, 0.22f, 1f);

            var colors = button.colors;
            colors.normalColor = normal;
            colors.highlightedColor = hover;
            colors.selectedColor = hover;
            colors.pressedColor = pressed;
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            var image = button.GetComponent<Image>();
            if (image != null)
                image.color = normal;

            foreach (var tmp in button.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                tmp.color = text;
                tmp.fontStyle |= FontStyles.Bold;
            }
        }

        public static TextMeshProUGUI CreateNativeText(
            string text,
            Transform parent,
            float fontSize = 24,
            TextAlignmentOptions alignment = TextAlignmentOptions.Center)
        {
            GameObject go;
            TextMeshProUGUI tmp;

            if (_textPrefab != null)
            {
                go = UnityEngine.Object.Instantiate(_textPrefab.gameObject, parent, false);
                tmp = go.GetComponent<TextMeshProUGUI>() ?? go.AddComponent<TextMeshProUGUI>();
            }
            else
            {
                go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
                go.transform.SetParent(parent, false);
                tmp = go.GetComponent<TextMeshProUGUI>();
            }

            go.name = "Text";
            tmp.richText = true;
            tmp.parseCtrlCharacters = true;
            tmp.text = text ?? string.Empty;
            tmp.fontSize = fontSize;
            tmp.alignment = alignment;
            tmp.color = NativeGreen;
            tmp.textWrappingMode = TextWrappingModes.Normal;
            tmp.overflowMode = TextOverflowModes.Ellipsis;

            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.minHeight = Mathf.Max(24f, fontSize + 8f);
            le.preferredHeight = Mathf.Max(24f, fontSize + 8f);

            return tmp;
        }

        private static TextMeshProUGUI AddButtonTextChild(Transform parent)
        {
            var textGo = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            textGo.transform.SetParent(parent, false);
            var rect = textGo.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(10f, 0f);
            rect.offsetMax = new Vector2(-10f, 0f);

            var tmp = textGo.GetComponent<TextMeshProUGUI>();
            tmp.fontSize = 20f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = NativeGreen;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            return tmp;
        }

        // ── Layout Helpers ──────────────────────────────────────────────

        public static GameObject CreateVerticalGroup(Transform parent, float spacing = 10, int padding = 0)
        {
            var go = new GameObject("VerticalGroup", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            layout.padding = new RectOffset(padding, padding, padding, padding);

            var le = go.AddComponent<LayoutElement>();
            le.flexibleWidth = 1f;

            return go;
        }

        public static GameObject CreateHorizontalGroup(Transform parent, float spacing = 12, int padding = 0)
        {
            var go = new GameObject("HorizontalGroup", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var layout = go.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = spacing;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = false;
            layout.padding = new RectOffset(padding, padding, padding, padding);

            var le = go.AddComponent<LayoutElement>();
            le.flexibleWidth = 1f;

            return go;
        }

        public static GameObject CreateHorizontalRow(Transform parent, float height = 40, float spacing = 10)
        {
            var row = CreateHorizontalGroup(parent, spacing);
            row.name = "Row";

            var le = row.GetComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;

            var layout = row.GetComponent<HorizontalLayoutGroup>();
            layout.childForceExpandHeight = true;

            return row;
        }

        public static void SetLayoutWidth(GameObject go, float minWidth, float preferredWidth, float flexibleWidth = 0f)
        {
            if (go == null) return;
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.minWidth = minWidth;
            le.preferredWidth = preferredWidth;
            le.flexibleWidth = flexibleWidth;
        }

        public static void SetFlexible(GameObject go, float width = 1f, float height = 0f)
        {
            if (go == null) return;
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.flexibleWidth = width;
            le.flexibleHeight = height;
        }

        public static GameObject CreateSpacer(Transform parent, float height = 12f, float flexibleHeight = 0f)
        {
            var spacer = new GameObject("Spacer", typeof(RectTransform), typeof(LayoutElement));
            spacer.transform.SetParent(parent, false);
            var le = spacer.GetComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
            le.flexibleHeight = flexibleHeight;
            return spacer;
        }

        public static GameObject CreateDivider(Transform parent, float alpha = 0.2f)
        {
            var divider = new GameObject("Divider", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            divider.transform.SetParent(parent, false);
            divider.GetComponent<Image>().color = new Color(NativeGreen.r, NativeGreen.g, NativeGreen.b, alpha);
            var le = divider.GetComponent<LayoutElement>();
            le.minHeight = 1f;
            le.preferredHeight = 1f;
            return divider;
        }

        // ── Panels ──────────────────────────────────────────────────────

        public static GameObject CreateNativePanel(Transform parent, int padding = 24, float spacing = 10)
        {
            var go = new GameObject("NativePanel", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);

            var bg = go.GetComponent<Image>();
            bg.color = _panelFillColor;
            bg.raycastTarget = true;

            AddNativeBorder(go);

            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.spacing = spacing;
            layout.padding = new RectOffset(padding, padding, padding, padding);

            var le = go.AddComponent<LayoutElement>();
            le.flexibleWidth = 1f;

            return go;
        }

        public static Color GetPanelFillColor() => _panelFillColor;

        public static void AddNativeBorder(GameObject panel, float alpha = 1f)
        {
            if (panel == null) return;

            if (_hasPanelStyle && _panelSprite != null)
            {
                var outline = new GameObject("Outline", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
                outline.transform.SetParent(panel.transform, false);
                outline.GetComponent<LayoutElement>().ignoreLayout = true;
                var rect = outline.GetComponent<RectTransform>();
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
                var img = outline.GetComponent<Image>();
                img.sprite = _panelSprite;
                img.type = _panelImageType;
                img.color = new Color(_panelOutlineColor.r, _panelOutlineColor.g, _panelOutlineColor.b,
                    Mathf.Clamp01(_panelOutlineColor.a * alpha));
                img.raycastTarget = false;
            }
            else
            {
                var outline = panel.AddComponent<Outline>();
                outline.effectColor = new Color(NativeGreen.r, NativeGreen.g, NativeGreen.b, 0.85f * alpha);
                outline.effectDistance = new Vector2(1f, -1f);
            }
        }

        public static void AddGreenBorder(GameObject panel, float alpha = 1f)
        {
            // Kept for older callers. The implementation now uses the game's
            // captured border sprite/color instead of forcing a custom green tint.
            AddNativeBorder(panel, alpha);
        }

        // ── Input Fields ────────────────────────────────────────────────

        public static TMP_InputField CreateNativeInputField(
            string initialValue,
            Transform parent,
            UnityEngine.Events.UnityAction<string> onValueChanged)
        {
            GameObject go;
            ColorBlock colors = _buttonColors;

            if (_buttonPrefab != null)
            {
                go = UnityEngine.Object.Instantiate(_buttonPrefab.gameObject, parent, false);
                var oldButton = go.GetComponent<Button>();
                if (oldButton != null)
                {
                    colors = oldButton.colors;
                    UnityEngine.Object.DestroyImmediate(oldButton);
                }
            }
            else
            {
                go = new GameObject("InputField", typeof(RectTransform), typeof(Image));
                go.transform.SetParent(parent, false);
                go.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.62f);
            }

            go.name = "InputField";
            foreach (var text in go.GetComponentsInChildren<TextMeshProUGUI>(true))
                UnityEngine.Object.DestroyImmediate(text.gameObject);

            var cloneLayout = go.GetComponent<LayoutGroup>();
            if (cloneLayout != null) UnityEngine.Object.DestroyImmediate(cloneLayout);

            var image = go.GetComponent<Image>() ?? go.AddComponent<Image>();
            image.color = new Color(0f, 0f, 0f, 0.58f);

            AddNativeBorder(go, 0.65f);

            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.minHeight = 40f;
            le.preferredHeight = 40f;

            var textArea = new GameObject("TextArea", typeof(RectTransform), typeof(RectMask2D), typeof(LayoutElement));
            textArea.transform.SetParent(go.transform, false);
            textArea.GetComponent<LayoutElement>().ignoreLayout = true;
            var taRect = textArea.GetComponent<RectTransform>();
            taRect.anchorMin = Vector2.zero;
            taRect.anchorMax = Vector2.one;
            taRect.offsetMin = new Vector2(12f, 2f);
            taRect.offsetMax = new Vector2(-12f, -2f);

            var placeholder = CreateNativeText("", textArea.transform, 18, TextAlignmentOptions.MidlineLeft);
            placeholder.name = "Placeholder";
            placeholder.text = "";
            placeholder.color = new Color(NativeDimGreen.r, NativeDimGreen.g, NativeDimGreen.b, 0.55f);
            Stretch(placeholder.rectTransform);

            var textComponent = CreateNativeText("", textArea.transform, 18, TextAlignmentOptions.MidlineLeft);
            textComponent.name = "Text";
            textComponent.color = NativeGreen;
            textComponent.textWrappingMode = TextWrappingModes.NoWrap;
            textComponent.overflowMode = TextOverflowModes.Overflow;
            Stretch(textComponent.rectTransform);

            var input = go.AddComponent<TMP_InputField>();
            input.colors = colors;
            input.targetGraphic = image;
            input.textViewport = taRect;
            input.textComponent = textComponent;
            input.placeholder = placeholder;
            input.customCaretColor = true;
            input.caretColor = _accentColor;
            input.caretWidth = 2;
            input.caretBlinkRate = 0.85f;
            input.selectionColor = new Color(_accentColor.r, _accentColor.g, _accentColor.b, 0.28f);
            input.richText = false;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.text = initialValue ?? string.Empty;
            input.ForceLabelUpdate();
            if (onValueChanged != null)
                input.onValueChanged.AddListener(onValueChanged);

            input.onSelect.AddListener(_ => SetPlayerAircraftActive(false));
            input.onDeselect.AddListener(_ => SetPlayerAircraftActive(true));

            return input;
        }

        public static void CreateLabelInputRow(
            string label,
            string initialValue,
            Transform parent,
            UnityEngine.Events.UnityAction<string> onValueChanged,
            float labelWidth = 170f)
        {
            var row = CreateHorizontalRow(parent, 40, 10);
            var lbl = CreateNativeText(label, row.transform, 18, TextAlignmentOptions.MidlineLeft);
            SetLayoutWidth(lbl.gameObject, labelWidth, labelWidth);

            var input = CreateNativeInputField(initialValue, row.transform, onValueChanged);
            SetFlexible(input.gameObject);
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void SetPlayerAircraftActive(bool active)
        {
            try
            {
                var player = Falcon.UniversalAircraft.UniAircraft.Player;
                if (player != null) player.gameObject.SetActive(active);
            }
            catch
            {
                // Menu text entry can happen outside flight; no action required.
            }
        }

        // ── Selectors ───────────────────────────────────────────────────

        private static MultiSelectDialog _cachedDialog;

        public static MultiSelectDialog GetNativeDialog()
        {
            if (_cachedDialog != null) return _cachedDialog;

            var qmb = UnityEngine.Object.FindFirstObjectByType<QuickMissionBuilder>(FindObjectsInactive.Include);
            if (qmb != null)
            {
                _cachedDialog = GetPrivateField<MultiSelectDialog>(qmb, "FlexibleSelector");
                if (_cachedDialog != null)
                    return _cachedDialog;
            }

            var all = UnityEngine.Object.FindObjectsByType<MultiSelectDialog>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (all.Length > 0)
                _cachedDialog = all[0];

            return _cachedDialog;
        }

        public static MapSelectorDialog GetNativeMapSelector()
        {
            if (_cachedMapSelector != null) return _cachedMapSelector;

            var qmb = UnityEngine.Object.FindFirstObjectByType<QuickMissionBuilder>(FindObjectsInactive.Include);
            if (qmb != null)
            {
                _cachedMapSelector = GetPrivateField<MapSelectorDialog>(qmb, "MapSelector");
                if (_cachedMapSelector != null)
                    return _cachedMapSelector;
            }

            var all = UnityEngine.Object.FindObjectsByType<MapSelectorDialog>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (all.Length > 0)
                _cachedMapSelector = all[0];

            return _cachedMapSelector;
        }

        public static RearmRefuelDialog GetNativeLoadoutSelector()
        {
            if (_cachedLoadoutSelector != null) return _cachedLoadoutSelector;

            var qmb = UnityEngine.Object.FindFirstObjectByType<QuickMissionBuilder>(FindObjectsInactive.Include);
            if (qmb != null)
            {
                _cachedLoadoutSelector = GetPrivateField<RearmRefuelDialog>(qmb, "LoadoutSelector");
                if (_cachedLoadoutSelector != null)
                    return _cachedLoadoutSelector;
            }

            var all = UnityEngine.Object.FindObjectsByType<RearmRefuelDialog>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (all.Length > 0)
                _cachedLoadoutSelector = all[0];

            return _cachedLoadoutSelector;
        }

        public static void ShowNativeMapSelector(string currentMap, Action<string> onSelected)
        {
            ShowNativeMapSelectorAsync(currentMap, onSelected).Forget();
        }

        public static void ShowNativeLoadoutSelector(
            string aircraft,
            List<string> selectableAircraft,
            string loadout,
            string ammoBelt,
            Action<string, string, string> onSelected)
        {
            ShowNativeLoadoutSelectorAsync(
                aircraft, selectableAircraft, loadout, ammoBelt, onSelected).Forget();
        }

        public static void ShowConfirmDialog(
            string title,
            string message,
            string confirmLabel,
            string cancelLabel,
            Action onConfirmed,
            Action onCancelled = null,
            bool destructive = false)
        {
            EnsureEventSystem();
            NativeDialogActiveChanged?.Invoke(true);

            GameObject canvasGo = null;
            try
            {
                canvasGo = new GameObject(
                    "TCAMP_ConfirmDialog",
                    typeof(RectTransform),
                    typeof(Canvas),
                    typeof(CanvasScaler),
                    typeof(GraphicRaycaster));
                var canvasRect = canvasGo.GetComponent<RectTransform>();
                Stretch(canvasRect);

                var canvas = canvasGo.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.overrideSorting = true;
                canvas.sortingOrder = 1200;

                var scaler = canvasGo.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = 0.5f;

                var blocker = new GameObject("Blocker", typeof(RectTransform), typeof(Image));
                blocker.transform.SetParent(canvasGo.transform, false);
                Stretch(blocker.GetComponent<RectTransform>());
                var blockerImage = blocker.GetComponent<Image>();
                blockerImage.color = new Color(0f, 0f, 0f, 0.72f);
                blockerImage.raycastTarget = true;

                var panel = CreateNativePanel(blocker.transform, 24, 12);
                panel.name = "ConfirmPanel";
                var panelRect = panel.GetComponent<RectTransform>();
                panelRect.anchorMin = new Vector2(0.5f, 0.5f);
                panelRect.anchorMax = new Vector2(0.5f, 0.5f);
                panelRect.pivot = new Vector2(0.5f, 0.5f);
                panelRect.anchoredPosition = Vector2.zero;
                panelRect.sizeDelta = new Vector2(640f, 320f);

                string warningColor = destructive ? "#FF3838" : "#007A28";
                string titleText = destructive
                    ? $"<color={warningColor}>{title ?? "CONFIRM"}</color>"
                    : title ?? "CONFIRM";
                CreateNativeText(titleText, panel.transform, 24, TextAlignmentOptions.Center);
                CreateDivider(panel.transform, 0.18f);

                var body = CreateNativeText(
                    $"<color={warningColor}>{message ?? string.Empty}</color>",
                    panel.transform, 17, TextAlignmentOptions.Center);
                var bodyLayout = body.GetComponent<LayoutElement>();
                bodyLayout.minHeight = 126f;
                bodyLayout.preferredHeight = 126f;

                CreateSpacer(panel.transform, 4, 1f);

                var actions = CreateHorizontalRow(panel.transform, 52, 12);
                actions.GetComponent<HorizontalLayoutGroup>().childForceExpandWidth = true;

                var confirm = CreateNativeButton(confirmLabel ?? "CONFIRM", actions.transform, 52);
                SetFlexible(confirm.gameObject);
                if (destructive)
                    StyleDestructiveButton(confirm);
                var cancel = CreateNativeButton(cancelLabel ?? "CANCEL", actions.transform, 52);
                SetFlexible(cancel.gameObject);

                bool closed = false;
                void Close(bool confirmed)
                {
                    if (closed) return;
                    closed = true;
                    if (canvasGo != null)
                        UnityEngine.Object.Destroy(canvasGo);
                    NativeDialogActiveChanged?.Invoke(false);

                    if (confirmed)
                        onConfirmed?.Invoke();
                    else
                        onCancelled?.Invoke();
                }

                confirm.onClick.AddListener(() => Close(true));
                cancel.onClick.AddListener(() => Close(false));
            }
            catch (Exception ex)
            {
                if (canvasGo != null)
                    UnityEngine.Object.Destroy(canvasGo);
                NativeDialogActiveChanged?.Invoke(false);
                Log.Error(Tag, $"Confirm dialog error: {ex}");
            }
        }

        private static async UniTask ShowNativeMapSelectorAsync(string currentMap, Action<string> onSelected)
        {
            var dialog = GetNativeMapSelector();
            if (dialog == null)
            {
                Log.Warning(Tag, "No native MapSelectorDialog available");
                return;
            }

            using (new DialogCanvasScope(dialog.transform))
            {
                try
                {
                    string selected = await dialog.ShowMapSelectDialog(currentMap);
                    if (!string.IsNullOrEmpty(selected))
                        onSelected?.Invoke(selected);
                }
                catch (Exception ex)
                {
                    Log.Error(Tag, $"Map selector error: {ex}");
                }
            }
        }

        private static async UniTask ShowNativeLoadoutSelectorAsync(
            string aircraft,
            List<string> selectableAircraft,
            string loadout,
            string ammoBelt,
            Action<string, string, string> onSelected)
        {
            var dialog = GetNativeLoadoutSelector();
            if (dialog == null)
            {
                Log.Warning(Tag, "No native RearmRefuelDialog available");
                return;
            }

            if (selectableAircraft == null || selectableAircraft.Count == 0)
                selectableAircraft = new List<string> { aircraft };

            if (string.IsNullOrEmpty(aircraft))
                aircraft = selectableAircraft[0];

            HideNestedLoadoutDialogs(dialog);
            var nestedSelector = GetPrivateField<MultiSelectDialog>(dialog, "MultiSelect");
            var nestedConfirm = GetPrivateField<ModalConfirmDialog>(dialog, "ModalConfirm");

            using (new NestedDialogFrontScope(
                nestedSelector != null ? nestedSelector.transform : null,
                nestedConfirm != null ? nestedConfirm.transform : null))
            using (new DialogCanvasScope(dialog.transform))
            {
                try
                {
                    var result = await dialog.RunLoadoutSelectorMap(
                        aircraft, selectableAircraft, loadout ?? string.Empty, ammoBelt ?? string.Empty);
                    if (result.Action == RearmRefuelDialog.Action.Resume)
                        onSelected?.Invoke(result.Aircraft, result.Loadout, result.AmmoBelt);
                }
                catch (Exception ex)
                {
                    Log.Error(Tag, $"Loadout selector error: {ex}");
                }
            }
        }

        private sealed class NestedDialogFrontScope : IDisposable
        {
            private readonly List<NestedDialogState> _states = new List<NestedDialogState>();

            public NestedDialogFrontScope(params Transform[] dialogs)
            {
                foreach (var dialog in dialogs)
                {
                    if (dialog == null)
                        continue;

                    var state = new NestedDialogState(dialog);
                    _states.Add(state);
                    state.BringToFront();
                }
            }

            public void Dispose()
            {
                for (int i = _states.Count - 1; i >= 0; i--)
                    _states[i].Restore();
            }
        }

        private sealed class NestedDialogState
        {
            private readonly Transform _transform;
            private readonly Transform _parent;
            private readonly int _siblingIndex;
            private readonly Canvas _canvas;
            private readonly bool _hadCanvas;
            private readonly bool _canvasEnabled;
            private readonly bool _overrideSorting;
            private readonly int _sortingOrder;
            private readonly GraphicRaycaster _raycaster;
            private readonly bool _hadRaycaster;

            public NestedDialogState(Transform transform)
            {
                _transform = transform;
                _parent = transform != null ? transform.parent : null;
                _siblingIndex = transform != null ? transform.GetSiblingIndex() : 0;

                if (transform == null) return;

                _canvas = transform.GetComponent<Canvas>();
                _hadCanvas = _canvas != null;
                if (_canvas == null)
                    _canvas = transform.gameObject.AddComponent<Canvas>();

                _canvasEnabled = _canvas.enabled;
                _overrideSorting = _canvas.overrideSorting;
                _sortingOrder = _canvas.sortingOrder;

                _raycaster = transform.GetComponent<GraphicRaycaster>();
                _hadRaycaster = _raycaster != null;
                if (_raycaster == null)
                    _raycaster = transform.gameObject.AddComponent<GraphicRaycaster>();
            }

            public void BringToFront()
            {
                if (_transform == null) return;

                _canvas.enabled = true;
                _canvas.overrideSorting = true;
                _canvas.sortingOrder = 1001;
                _transform.SetAsLastSibling();
            }

            public void Restore()
            {
                if (_transform == null) return;

                if (_parent != null)
                {
                    _transform.SetParent(_parent, false);
                    _transform.SetSiblingIndex(_siblingIndex);
                }

                if (_canvas != null)
                {
                    _canvas.enabled = _canvasEnabled;
                    _canvas.overrideSorting = _overrideSorting;
                    _canvas.sortingOrder = _sortingOrder;
                    if (!_hadCanvas)
                        UnityEngine.Object.Destroy(_canvas);
                }

                if (_raycaster != null && !_hadRaycaster)
                    UnityEngine.Object.Destroy(_raycaster);
            }
        }

        private static void HideNestedLoadoutDialogs(RearmRefuelDialog dialog)
        {
            if (dialog == null) return;

            var nestedSelector = GetPrivateField<MultiSelectDialog>(dialog, "MultiSelect");
            if (nestedSelector != null && nestedSelector.gameObject.activeSelf)
                nestedSelector.gameObject.SetActive(false);

            var nestedConfirm = GetPrivateField<ModalConfirmDialog>(dialog, "ModalConfirm");
            if (nestedConfirm != null && nestedConfirm.gameObject.activeSelf)
                nestedConfirm.gameObject.SetActive(false);
        }

        public static Button CreateLabeledSelector(
            string label,
            List<string> options,
            int selectedIndex,
            Transform parent,
            Action<int> onSelected,
            float height = 40,
            float labelWidth = 170f)
        {
            var row = CreateHorizontalRow(parent, height, 10);
            var lbl = CreateNativeText(label, row.transform, 18, TextAlignmentOptions.MidlineLeft);
            SetLayoutWidth(lbl.gameObject, labelWidth, labelWidth);

            string currentValue = options != null && selectedIndex >= 0 && selectedIndex < options.Count
                ? options[selectedIndex]
                : "";
            var btn = CreateNativeButton(currentValue, row.transform, height);
            SetFlexible(btn.gameObject);
            btn.interactable = onSelected != null;

            if (onSelected != null)
            {
                string popupTitle = label.TrimEnd(':', ' ');
                btn.onClick.AddListener(() =>
                    ShowNativeSelectionAsync(popupTitle, options, currentValue, idx =>
                    {
                        onSelected(idx);
                        // Update button text to reflect new selection
                        if (idx >= 0 && idx < options.Count)
                            SetButtonText(btn, options[idx]);
                    }).Forget());
            }

            return btn;
        }

        public static Button CreateLabeledButton(
            string label,
            string value,
            Transform parent,
            Action onClick,
            float height = 40,
            float labelWidth = 170f)
        {
            var row = CreateHorizontalRow(parent, height, 10);
            var lbl = CreateNativeText(label, row.transform, 18, TextAlignmentOptions.MidlineLeft);
            SetLayoutWidth(lbl.gameObject, labelWidth, labelWidth);

            var btn = CreateNativeButton(value ?? string.Empty, row.transform, height);
            SetFlexible(btn.gameObject);
            btn.interactable = onClick != null;
            if (onClick != null)
                btn.onClick.AddListener(() => onClick());
            return btn;
        }

        private static async UniTask ShowNativeSelectionAsync(
            string title,
            List<string> options,
            string currentValue,
            Action<int> onSelected)
        {
            if (options == null || options.Count == 0) return;

            var dialog = GetNativeDialog();
            if (dialog == null)
            {
                Log.Warning(Tag, "No native MultiSelectDialog available");
                return;
            }

            using (new DialogCanvasScope(dialog.transform))
            {
                try
                {
                    string result = await dialog.ShowSelectionDialog(title, (IEnumerable<string>)options, currentValue);
                    int index = options.IndexOf(result);
                    if (index >= 0)
                        onSelected?.Invoke(index);
                }
                catch (Exception ex)
                {
                    Log.Error(Tag, $"Selection dialog error: {ex}");
                }
            }
        }

        private sealed class DialogCanvasScope : IDisposable
        {
            private readonly List<GameObjectState> _gameObjects = new List<GameObjectState>();
            private readonly List<CanvasState> _canvases = new List<CanvasState>();
            private readonly List<CanvasGroupState> _canvasGroups = new List<CanvasGroupState>();

            public DialogCanvasScope(params Transform[] dialogs)
            {
                NativeDialogActiveChanged?.Invoke(true);
                EnsureEventSystem();

                foreach (var dialog in dialogs)
                {
                    if (dialog == null)
                        continue;

                    ActivateNativeHierarchy(dialog);
                    RaiseNativeCanvases(dialog);
                    PrepareCanvasGroups(dialog);
                    PrepareSelectableGraphics(dialog);
                }
            }

            public void Dispose()
            {
                foreach (var state in _canvasGroups)
                    state.Restore();

                foreach (var state in _canvases)
                    state.Restore();

                for (int i = _gameObjects.Count - 1; i >= 0; i--)
                    _gameObjects[i].Restore();

                NativeDialogActiveChanged?.Invoke(false);
            }

            private void ActivateNativeHierarchy(Transform dialog)
            {
                var current = dialog;
                while (current != null)
                {
                    var go = current.gameObject;
                    if (!HasGameObjectState(go))
                        _gameObjects.Add(new GameObjectState(go));

                    if (!go.activeSelf)
                        go.SetActive(true);

                    current = current.parent;
                }
            }

            private void PrepareCanvasGroups(Transform dialog)
            {
                foreach (var group in dialog.GetComponentsInChildren<CanvasGroup>(true))
                {
                    _canvasGroups.Add(new CanvasGroupState(group));
                    group.interactable = true;
                    group.blocksRaycasts = true;
                    group.ignoreParentGroups = false;
                }
            }

            private static void PrepareSelectableGraphics(Transform dialog)
            {
                foreach (var selectable in dialog.GetComponentsInChildren<Selectable>(true))
                {
                    if (selectable == null) continue;

                    var graphic = selectable.targetGraphic;
                    if (graphic != null)
                        graphic.raycastTarget = true;

                    var image = selectable.GetComponent<Image>();
                    if (image != null)
                        image.raycastTarget = true;
                }
            }

            private void RaiseNativeCanvases(Transform dialog)
            {
                foreach (var canvas in dialog.GetComponentsInParent<Canvas>(true))
                {
                    if (canvas == null || HasCanvasState(canvas))
                        continue;

                    _canvases.Add(new CanvasState(canvas));
                    canvas.enabled = true;
                    canvas.overrideSorting = true;
                    canvas.sortingOrder = 1000;
                }
            }

            private bool HasGameObjectState(GameObject go)
            {
                foreach (var state in _gameObjects)
                {
                    if (state.GameObject == go)
                        return true;
                }

                return false;
            }

            private bool HasCanvasState(Canvas canvas)
            {
                foreach (var state in _canvases)
                {
                    if (state.Canvas == canvas)
                        return true;
                }

                return false;
            }
        }

        private readonly struct GameObjectState
        {
            public readonly GameObject GameObject;
            private readonly bool _activeSelf;

            public GameObjectState(GameObject go)
            {
                GameObject = go;
                _activeSelf = go != null && go.activeSelf;
            }

            public void Restore()
            {
                if (GameObject != null)
                    GameObject.SetActive(_activeSelf);
            }
        }

        private readonly struct CanvasState
        {
            public readonly Canvas Canvas;
            private readonly bool _enabled;
            private readonly bool _overrideSorting;
            private readonly int _sortingOrder;

            public CanvasState(Canvas canvas)
            {
                Canvas = canvas;
                _enabled = canvas != null && canvas.enabled;
                _overrideSorting = canvas != null && canvas.overrideSorting;
                _sortingOrder = canvas != null ? canvas.sortingOrder : 0;
            }

            public void Restore()
            {
                if (Canvas == null) return;
                Canvas.enabled = _enabled;
                Canvas.overrideSorting = _overrideSorting;
                Canvas.sortingOrder = _sortingOrder;
            }
        }

        private readonly struct CanvasGroupState
        {
            private readonly CanvasGroup _group;
            private readonly bool _interactable;
            private readonly bool _blocksRaycasts;
            private readonly bool _ignoreParentGroups;

            public CanvasGroupState(CanvasGroup group)
            {
                _group = group;
                _interactable = group != null && group.interactable;
                _blocksRaycasts = group != null && group.blocksRaycasts;
                _ignoreParentGroups = group != null && group.ignoreParentGroups;
            }

            public void Restore()
            {
                if (_group == null) return;
                _group.interactable = _interactable;
                _group.blocksRaycasts = _blocksRaycasts;
                _group.ignoreParentGroups = _ignoreParentGroups;
            }
        }

        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null)
                return;

            var eventSystemGo = new GameObject("TCAMP_EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            UnityEngine.Object.DontDestroyOnLoad(eventSystemGo);
            eventSystemGo.hideFlags = HideFlags.HideAndDontSave;
        }

        // ── Toggles ─────────────────────────────────────────────────────

        public static Toggle CreateNativeToggle(bool isOn, Transform parent, float height = 40)
        {
            var button = CreateNativeButton(isOn ? "ON" : "OFF", parent, height);
            var go = button.gameObject;
            var colors = button.colors;
            var img = go.GetComponent<Image>();
            UnityEngine.Object.DestroyImmediate(button);

            var toggle = go.AddComponent<Toggle>();
            toggle.colors = colors;
            toggle.targetGraphic = img;
            toggle.isOn = isOn;

            void UpdateVisual(bool on)
            {
                var tmp = go.GetComponentInChildren<TextMeshProUGUI>(true);
                if (tmp == null) return;
                tmp.text = on ? "ON" : "OFF";
                tmp.color = on ? _accentColor : _mutedTextColor;
            }

            UpdateVisual(isOn);
            toggle.onValueChanged.AddListener(UpdateVisual);
            return toggle;
        }

        public static Toggle CreateLabeledToggle(
            string label,
            bool isOn,
            Transform parent,
            float height = 40,
            float labelWidth = 170f)
        {
            var row = CreateHorizontalRow(parent, height, 10);
            var lbl = CreateNativeText(label, row.transform, 18, TextAlignmentOptions.MidlineLeft);
            SetLayoutWidth(lbl.gameObject, labelWidth, labelWidth);

            var toggle = CreateNativeToggle(isOn, row.transform, height);
            SetLayoutWidth(toggle.gameObject, 96f, 120f);
            return toggle;
        }
    }
}
