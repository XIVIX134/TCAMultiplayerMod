using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Falcon.Game2;
using Falcon.Game2.UI;
using Cysharp.Threading.Tasks;

namespace TCAMultiplayer.UI
{
    /// <summary>
    /// UI factory that clones the game's native button prefab for ALL widgets
    /// (dropdowns, toggles, input fields) so everything matches the game's look.
    /// </summary>
    public static class UIFactory
    {
        private static Button _buttonPrefab;
        private static TextMeshProUGUI _textPrefab;
        private static Sprite _panelSprite;
        private static Image.Type _panelImageType;
        private static Color _panelColor;
        private static bool _hasPanelStyle;

        public static bool HasPrefabs => _buttonPrefab != null && _textPrefab != null;

        public static void Initialize(MainMenu mainMenu)
        {
            if (HasPrefabs) return;

            if (mainMenu != null)
            {
                try
                {
                    var arenaButton = (Button)typeof(MainMenu)
                        .GetField("ArenaButton", System.Reflection.BindingFlags.NonPublic |
                                                System.Reflection.BindingFlags.Instance |
                                                System.Reflection.BindingFlags.Public)
                        ?.GetValue(mainMenu);

                    if (arenaButton != null)
                    {
                        _buttonPrefab = arenaButton;
                        _textPrefab = arenaButton.GetComponentInChildren<TextMeshProUGUI>();
                        Plugin.Log?.LogInfo("[UIFactory] Captured native button prefab from MainMenu");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"[UIFactory] Failed to get ArenaButton: {ex.Message}");
                }
            }

            // Fallback: search scene
            if (_buttonPrefab == null)
            {
                var allButtons = UnityEngine.Object.FindObjectsByType<Button>(FindObjectsSortMode.None);
                foreach (var btn in allButtons)
                {
                    if (btn.GetComponentInParent<Canvas>() != null &&
                        btn.GetComponentInChildren<TextMeshProUGUI>() != null)
                    {
                        _buttonPrefab = btn;
                        _textPrefab = btn.GetComponentInChildren<TextMeshProUGUI>();
                        Plugin.Log?.LogInfo($"[UIFactory] Found button via scene search: {btn.name}");
                        break;
                    }
                }
            }

            // Capture QMB panel sprite for native panel styling
            // The QMB uses a GenericSmoothBox sliced sprite for its border (the "Outline" child).
            // We reuse the same sprite shape but with a dark tint for our panel background.
            if (!_hasPanelStyle)
            {
                try
                {
                    var qmb = UnityEngine.Object.FindFirstObjectByType<QuickMissionBuilder>(FindObjectsInactive.Include);
                    if (qmb?.UIRoot != null)
                    {
                        var childImages = qmb.UIRoot.GetComponentsInChildren<Image>(true);
                        foreach (var img in childImages)
                        {
                            if (img.sprite != null && img.type == Image.Type.Sliced)
                            {
                                _panelSprite = img.sprite;
                                _panelImageType = img.type;
                                // Use a dark tinted version of the game's green — matches game feel
                                _panelColor = new Color(0f, 0.08f, 0.03f, 0.95f);
                                _hasPanelStyle = true;
                                Plugin.Log?.LogInfo($"[UIFactory] Captured QMB panel sprite: {img.name}, sprite={_panelSprite.name}, using dark tint");
                                break;
                            }
                        }
                    }

                    if (!_hasPanelStyle)
                    {
                        Plugin.Log?.LogWarning("[UIFactory] Could not find QMB panel sprite — will use fallback dark panel");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"[UIFactory] Failed to capture QMB panel sprite: {ex.Message}");
                }
            }
        }

        // =====================================================================
        // CORE: Button and Text (native clones)
        // =====================================================================

        public static Button CreateNativeButton(string label, Transform parent, float height = 50)
        {
            if (_buttonPrefab == null)
            {
                // Try to re-initialize if prefabs were lost during scene transitions
                var mainMenu = UnityEngine.Object.FindFirstObjectByType<Falcon.Game2.MainMenu>();
                if (mainMenu != null)
                {
                    Initialize(mainMenu);
                }
                
                if (_buttonPrefab == null)
                {
                    Plugin.Log?.LogError("[UIFactory] Unable to create button - no prefab available");
                    return null;
                }
            }

            var go = UnityEngine.Object.Instantiate(_buttonPrefab.gameObject, parent);
            go.name = label + " Button";

            var le = go.GetComponent<LayoutElement>();
            if (le == null) le = go.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;

            var button = go.GetComponent<Button>();
            button.onClick.RemoveAllListeners();

            // Add green outline border overlay (matching native QMB style)
            if (_hasPanelStyle && _panelSprite != null)
            {
                var outline = new GameObject("Outline", typeof(RectTransform), typeof(Image));
                outline.transform.SetParent(go.transform, false);
                var outlineLE = outline.AddComponent<LayoutElement>();
                outlineLE.ignoreLayout = true; // Prevent button's internal layout from positioning this
                var outlineRect = outline.GetComponent<RectTransform>();
                outlineRect.anchorMin = Vector2.zero;
                outlineRect.anchorMax = Vector2.one;
                outlineRect.offsetMin = Vector2.zero;
                outlineRect.offsetMax = Vector2.zero;
                var outlineImg = outline.GetComponent<Image>();
                outlineImg.sprite = _panelSprite;
                outlineImg.type = _panelImageType;
                outlineImg.color = new Color(0f, 1f, 0.39f, 0.6f); // Semi-transparent green border
                outlineImg.raycastTarget = false;
            }

            var text = go.GetComponentInChildren<TextMeshProUGUI>();
            if (text != null)
            {
                text.richText = true;
                text.parseCtrlCharacters = true;
                text.text = label;
            }

            return button;
        }

        public static TextMeshProUGUI CreateNativeText(string text, Transform parent, float fontSize = 24, TextAlignmentOptions alignment = TextAlignmentOptions.Center)
        {
            if (_textPrefab == null) return null;

            var go = UnityEngine.Object.Instantiate(_textPrefab.gameObject, parent);
            go.name = "Text: " + text;

            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.richText = true;
            tmp.parseCtrlCharacters = true;
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.alignment = alignment;

            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.minHeight = fontSize + 10;
            le.preferredHeight = fontSize + 10;

            return tmp;
        }

        // =====================================================================
        // LAYOUT helpers
        // =====================================================================

        public static GameObject CreateVerticalGroup(Transform parent, float spacing = 10, int padding = 20)
        {
            var go = new GameObject("VerticalGroup", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.childControlHeight = false;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            layout.padding = new RectOffset(padding, padding, padding, padding);

            var fitter = go.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            return go;
        }

        private static GameObject CreateHorizontalRow(Transform parent, float height = 40, float spacing = 10)
        {
            var row = new GameObject("Row", typeof(RectTransform));
            row.transform.SetParent(parent, false);

            var layout = row.AddComponent<HorizontalLayoutGroup>();
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = true;
            layout.childForceExpandWidth = false;
            layout.spacing = spacing;

            var le = row.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;

            return row;
        }

        // =====================================================================
        // PANEL (native QMB panel style)
        // =====================================================================

        /// <summary>
        /// Creates a panel using the game's native QMB panel sprite.
        /// Dark background + green border overlay, just like the game's QMB.
        /// Falls back to a semi-transparent dark panel if the sprite wasn't captured.
        /// </summary>
        public static GameObject CreateNativePanel(Transform parent)
        {
            var go = new GameObject("NativePanel", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);

            var bgImg = go.GetComponent<Image>();
            // Solid near-black background fill (no sprite = solid rectangle, not the
            // GenericSmoothBox sliced sprite which has a transparent center)
            bgImg.color = _hasPanelStyle ? _panelColor : new Color(0, 0, 0, 0.85f);

            if (_hasPanelStyle && _panelSprite != null)
            {
                // Green border overlay using the sliced sprite (transparent center, opaque borders)
                var outline = new GameObject("Outline", typeof(RectTransform), typeof(Image));
                outline.transform.SetParent(go.transform, false);
                var outlineRect = outline.GetComponent<RectTransform>();
                outlineRect.anchorMin = Vector2.zero;
                outlineRect.anchorMax = Vector2.one;
                outlineRect.offsetMin = Vector2.zero;
                outlineRect.offsetMax = Vector2.zero;
                var outlineImg = outline.GetComponent<Image>();
                outlineImg.sprite = _panelSprite;
                outlineImg.type = _panelImageType;
                outlineImg.color = new Color(0f, 1f, 0.39f, 1f); // Game's green
                outlineImg.raycastTarget = false;
            }

            return go;
        }

        /// <summary>
        /// The panel fill color (near-black with green tint). Exposed so other UI
        /// classes (ScoreboardHUD, RespawnScreen) can match the menu's visual style.
        /// </summary>
        public static Color GetPanelFillColor() => _hasPanelStyle ? _panelColor : new Color(0, 0, 0, 0.85f);

        /// <summary>
        /// Adds a green border overlay to any existing panel using the native QMB
        /// GenericSmoothBox sprite. The panel should already have an Image for the fill.
        /// Uses ignoreLayout so it works inside LayoutGroups.
        /// </summary>
        public static void AddGreenBorder(GameObject panel, float alpha = 1f)
        {
            if (!_hasPanelStyle || _panelSprite == null || panel == null) return;

            var outline = new GameObject("Outline", typeof(RectTransform), typeof(Image));
            outline.transform.SetParent(panel.transform, false);
            var outlineLE = outline.AddComponent<LayoutElement>();
            outlineLE.ignoreLayout = true;
            var outlineRect = outline.GetComponent<RectTransform>();
            outlineRect.anchorMin = Vector2.zero;
            outlineRect.anchorMax = Vector2.one;
            outlineRect.offsetMin = Vector2.zero;
            outlineRect.offsetMax = Vector2.zero;
            var outlineImg = outline.GetComponent<Image>();
            outlineImg.sprite = _panelSprite;
            outlineImg.type = _panelImageType;
            outlineImg.color = new Color(0f, 1f, 0.39f, alpha);
            outlineImg.raycastTarget = false;
        }

        // =====================================================================
        // INPUT FIELD (uses button clone as background)
        // =====================================================================

        public static TMP_InputField CreateNativeInputField(string initialValue, Transform parent, UnityEngine.Events.UnityAction<string> onValueChanged)
        {
            if (_buttonPrefab == null) return null;

            // Clone a button to get its background look
            var go = UnityEngine.Object.Instantiate(_buttonPrefab.gameObject, parent);
            go.name = "InputField";

            // Capture ColorBlock from Button BEFORE destroying it (Button tints the Image)
            var oldButton = go.GetComponent<Button>();
            ColorBlock oldColors = oldButton != null ? oldButton.colors : ColorBlock.defaultColorBlock;
            if (oldButton != null) UnityEngine.Object.DestroyImmediate(oldButton);

            // Darken the input field background (keep original button sprite for proper sizing)
            var bgImg = go.GetComponent<Image>();
            if (bgImg != null) bgImg.color = new Color(0f, 0.05f, 0.02f, 0.9f);

            // Add green outline border overlay (matching native QMB style)
            if (_hasPanelStyle && _panelSprite != null)
            {
                var outline = new GameObject("Outline", typeof(RectTransform), typeof(Image));
                outline.transform.SetParent(go.transform, false);
                outline.transform.SetAsFirstSibling(); // Behind text content
                var outlineLE = outline.AddComponent<LayoutElement>();
                outlineLE.ignoreLayout = true; // Prevent button clone's internal layout from positioning this
                var outlineRect = outline.GetComponent<RectTransform>();
                outlineRect.anchorMin = Vector2.zero;
                outlineRect.anchorMax = Vector2.one;
                outlineRect.offsetMin = Vector2.zero;
                outlineRect.offsetMax = Vector2.zero;
                var outlineImg = outline.GetComponent<Image>();
                outlineImg.sprite = _panelSprite;
                outlineImg.type = _panelImageType;
                outlineImg.color = new Color(0f, 1f, 0.39f, 0.6f); // Semi-transparent green border
                outlineImg.raycastTarget = false;
            }

            var le = go.GetComponent<LayoutElement>();
            if (le == null) le = go.AddComponent<LayoutElement>();
            le.minHeight = 40;
            le.preferredHeight = 40;

            // Remove existing TMP text children from the button clone
            var existingTexts = go.GetComponentsInChildren<TextMeshProUGUI>();
            foreach (var et in existingTexts)
                UnityEngine.Object.DestroyImmediate(et.gameObject);

            // Strip any LayoutGroup from the button clone — it interferes with
            // anchor-based child positioning (textArea, outline, caret)
            var cloneLayout = go.GetComponent<LayoutGroup>();
            if (cloneLayout != null) UnityEngine.Object.DestroyImmediate(cloneLayout);

            // Create text area with RectMask2D (required for TMP_InputField caret + selection rendering)
            var textArea = new GameObject("TextArea", typeof(RectTransform));
            textArea.transform.SetParent(go.transform, false);
            var taRect = textArea.GetComponent<RectTransform>();
            taRect.anchorMin = Vector2.zero;
            taRect.anchorMax = Vector2.one;
            taRect.offsetMin = new Vector2(10, 2);
            taRect.offsetMax = new Vector2(-10, -2);
            textArea.AddComponent<RectMask2D>();
            // Ensure the text area is not managed by any remaining layout
            var taLE = textArea.AddComponent<LayoutElement>();
            taLE.ignoreLayout = true;

            // Build all child objects BEFORE adding TMP_InputField so its OnEnable
            // finds textComponent and textViewport already wired
            TextMeshProUGUI pText = null;
            TextMeshProUGUI tText = null;

            if (_textPrefab != null)
            {
                var placeholder = UnityEngine.Object.Instantiate(_textPrefab.gameObject, textArea.transform);
                placeholder.name = "Placeholder";
                var pRect = placeholder.GetComponent<RectTransform>();
                pRect.anchorMin = Vector2.zero;
                pRect.anchorMax = Vector2.one;
                pRect.offsetMin = Vector2.zero;
                pRect.offsetMax = Vector2.zero;
                pText = placeholder.GetComponent<TextMeshProUGUI>();
                pText.text = "Enter text...";
                pText.fontSize = 18;
                pText.color = new Color(0.5f, 0.5f, 0.5f, 0.5f);
                pText.alignment = TextAlignmentOptions.Left;
                pText.enableWordWrapping = false;
                pText.overflowMode = TextOverflowModes.Overflow;

                var text = UnityEngine.Object.Instantiate(_textPrefab.gameObject, textArea.transform);
                text.name = "Text";
                var tRect = text.GetComponent<RectTransform>();
                tRect.anchorMin = Vector2.zero;
                tRect.anchorMax = Vector2.one;
                tRect.offsetMin = Vector2.zero;
                tRect.offsetMax = Vector2.zero;
                tText = text.GetComponent<TextMeshProUGUI>();
                tText.text = "";
                tText.fontSize = 18;
                tText.color = Color.white;
                tText.alignment = TextAlignmentOptions.Left;
                tText.enableWordWrapping = false;
                tText.overflowMode = TextOverflowModes.Overflow;
            }

            // Now add TMP_InputField and wire everything
            var input = go.AddComponent<TMP_InputField>();
            input.textViewport = taRect;
            input.textComponent = tText;
            input.placeholder = pText;
            if (bgImg != null) input.targetGraphic = bgImg;

            // Configure caret (cursor) and selection highlight
            input.customCaretColor = true;
            input.caretColor = new Color(0f, 1f, 0.39f, 1f); // Game's green
            input.caretWidth = 2;
            input.caretBlinkRate = 0.85f;
            input.selectionColor = new Color(0f, 1f, 0.39f, 0.25f); // Translucent green
            input.richText = false;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.ForceLabelUpdate();

            input.text = initialValue;
            input.onValueChanged.AddListener(onValueChanged);
            input.onSelect.AddListener((_) => {
                try { if (Falcon.UniversalAircraft.UniAircraft.Player != null) Falcon.UniversalAircraft.UniAircraft.Player.gameObject.SetActive(false); } catch { }
            });
            input.onDeselect.AddListener((_) => {
                try { if (Falcon.UniversalAircraft.UniAircraft.Player != null) Falcon.UniversalAircraft.UniAircraft.Player.gameObject.SetActive(true); } catch { }
            });

            return input;
        }

        public static void CreateLabelInputRow(string label, string initialValue, Transform parent, UnityEngine.Events.UnityAction<string> onValueChanged)
        {
            var row = CreateHorizontalRow(parent, 40, 10);

            var lbl = CreateNativeText(label, row.transform, 18, TextAlignmentOptions.Left);
            if (lbl != null)
            {
                var lblLe = lbl.GetComponent<LayoutElement>();
                lblLe.preferredWidth = 150;
            }

            var input = CreateNativeInputField(initialValue, row.transform, onValueChanged);
            if (input != null)
            {
                var inputLe = input.GetComponent<LayoutElement>();
                inputLe.flexibleWidth = 1;
            }
        }

        // =====================================================================
        // SELECTOR (reuses game's native MultiSelectDialog)
        // =====================================================================

        private static MultiSelectDialog _cachedDialog;

        /// <summary>
        /// Finds the game's native MultiSelectDialog (the FlexibleSelector from QuickMissionBuilder).
        /// Caches the reference for reuse.
        /// </summary>
        public static MultiSelectDialog GetNativeDialog()
        {
            if (_cachedDialog != null) return _cachedDialog;

            // Try via QuickMissionBuilder.FlexibleSelector (most reliable)
            var qmb = UnityEngine.Object.FindFirstObjectByType<QuickMissionBuilder>(FindObjectsInactive.Include);
            if (qmb != null)
            {
                var field = typeof(QuickMissionBuilder).GetField("FlexibleSelector",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public);
                _cachedDialog = field?.GetValue(qmb) as MultiSelectDialog;
                if (_cachedDialog != null)
                {
                    Plugin.Log?.LogInfo($"[UIFactory] Found native MultiSelectDialog via QMB: {_cachedDialog.name}");
                    return _cachedDialog;
                }
            }

            // Fallback: direct search (includes inactive GameObjects)
            var all = UnityEngine.Object.FindObjectsByType<MultiSelectDialog>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (all.Length > 0)
            {
                _cachedDialog = all[0];
                Plugin.Log?.LogInfo($"[UIFactory] Found native MultiSelectDialog via search: {_cachedDialog.name}");
            }
            else
            {
                Plugin.Log?.LogWarning("[UIFactory] Could not find native MultiSelectDialog in scene");
            }

            return _cachedDialog;
        }

        /// <summary>
        /// Creates a labeled selector row: [LABEL:] [BUTTON showing current value].
        /// Clicking the button opens the game's native MultiSelectDialog popup.
        /// Returns the button so callers can disable it (e.g. read-only for clients).
        /// </summary>
        public static Button CreateLabeledSelector(string label, List<string> options, int selectedIndex, Transform parent, Action<int> onSelected, float height = 40)
        {
            var row = CreateHorizontalRow(parent, height, 10);

            var lbl = CreateNativeText(label, row.transform, 18, TextAlignmentOptions.Left);
            if (lbl != null)
            {
                var lblLe = lbl.GetComponent<LayoutElement>();
                lblLe.preferredWidth = 140;
                lblLe.minWidth = 140;
            }

            string currentValue = (options != null && options.Count > selectedIndex && selectedIndex >= 0)
                ? options[selectedIndex] : "";
            var btn = CreateNativeButton(currentValue, row.transform, height);
            if (btn != null)
            {
                var btnLe = btn.GetComponent<LayoutElement>();
                btnLe.flexibleWidth = 1;

                string popupTitle = label.TrimEnd(':', ' ');
                btn.onClick.AddListener(() => {
                    ShowNativeSelectionAsync(popupTitle, options, currentValue, onSelected).Forget();
                });
            }

            return btn;
        }

        /// <summary>
        /// Opens the game's native MultiSelectDialog and invokes the callback with the selected index.
        /// Uses async UniTask internally; callers fire-and-forget via .Forget().
        /// </summary>
        private static async UniTask ShowNativeSelectionAsync(string title, List<string> options, string currentValue, Action<int> onSelected)
        {
            if (options == null || options.Count == 0)
            {
                Plugin.Log?.LogWarning("[UIFactory] Cannot show selection dialog with empty options");
                return;
            }

            var dialog = GetNativeDialog();
            if (dialog == null)
            {
                Plugin.Log?.LogWarning("[UIFactory] No native dialog available for selection");
                return;
            }

            Plugin.Log?.LogInfo($"[UIFactory] Opening selection dialog: '{title}' with {options.Count} options");

            Canvas dialogCanvas = null;
            int originalSortingOrder = 0;
            Transform originalParent = null;
            int originalSiblingIndex = 0;
            
            try
            {
                // Find the Canvas on the dialog or its parent
                dialogCanvas = dialog.GetComponent<Canvas>();
                if (dialogCanvas == null)
                {
                    dialogCanvas = dialog.GetComponentInParent<Canvas>();
                }
                
                // Store original values
                if (dialogCanvas != null)
                {
                    originalSortingOrder = dialogCanvas.sortingOrder;
                    dialogCanvas.sortingOrder = 1000; // Above our menu (999)
                    Plugin.Log?.LogInfo($"[UIFactory] Set dialog sortingOrder from {originalSortingOrder} to 1000");
                }
                
                // Move to root so it's not clipped by our Canvas transform hierarchy
                originalParent = dialog.transform.parent;
                originalSiblingIndex = dialog.transform.GetSiblingIndex();
                dialog.transform.SetParent(null, false);

                string result = await dialog.ShowSelectionDialog(title, (IEnumerable<string>)options, currentValue);
                
                int index = options.IndexOf(result);
                if (index >= 0)
                {
                    Plugin.Log?.LogInfo($"[UIFactory] Selection result: '{result}' at index {index}");
                    onSelected?.Invoke(index);
                }
                else
                {
                    Plugin.Log?.LogInfo($"[UIFactory] Selection cancelled or invalid result: '{result}'");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[UIFactory] Selection dialog error: {ex}");
            }
            finally
            {
                // Restore original state
                if (originalParent != null)
                {
                    dialog.transform.SetParent(originalParent, false);
                    dialog.transform.SetSiblingIndex(originalSiblingIndex);
                }
                
                if (dialogCanvas != null)
                {
                    dialogCanvas.sortingOrder = originalSortingOrder;
                    Plugin.Log?.LogInfo($"[UIFactory] Restored dialog sortingOrder to {originalSortingOrder}");
                }
            }
        }

        // =====================================================================
        // TOGGLE (native button clone showing ON / OFF)
        // =====================================================================

        /// <summary>
        /// Creates a toggle as a native button clone that shows "ON" or "OFF" text.
        /// Looks exactly like a game button but toggles state on click.
        /// </summary>
        public static Toggle CreateNativeToggle(bool isOn, Transform parent, float height = 40)
        {
            if (_buttonPrefab == null) return null;

            var go = UnityEngine.Object.Instantiate(_buttonPrefab.gameObject, parent);
            go.name = "Toggle";

            // Capture ColorBlock from Button BEFORE destroying it (Button tints the Image)
            var oldButton = go.GetComponent<Button>();
            ColorBlock oldColors = oldButton != null ? oldButton.colors : ColorBlock.defaultColorBlock;
            if (oldButton != null) UnityEngine.Object.DestroyImmediate(oldButton);

            var le = go.GetComponent<LayoutElement>();
            if (le == null) le = go.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;

            var text = go.GetComponentInChildren<TextMeshProUGUI>();

            // Add Toggle component and restore captured ColorBlock
            var toggle = go.AddComponent<Toggle>();
            toggle.colors = oldColors;
            var img = go.GetComponent<Image>();
            if (img != null) toggle.targetGraphic = img;
            toggle.isOn = isOn;

            // Visual state update
            Action<bool> updateVisual = (on) =>
            {
                if (text != null)
                {
                    text.text = on ? "ON" : "OFF";
                    text.color = on ? new Color(0.3f, 1f, 0.5f, 1f) : new Color(0.8f, 0.3f, 0.3f, 1f);
                }
            };

            updateVisual(isOn);
            toggle.onValueChanged.AddListener((on) => updateVisual(on));

            return toggle;
        }

        /// <summary>
        /// Creates a labeled toggle row: [LABEL:] [ON/OFF button]
        /// </summary>
        public static Toggle CreateLabeledToggle(string label, bool isOn, Transform parent, float height = 40)
        {
            var row = CreateHorizontalRow(parent, height, 10);

            var lbl = CreateNativeText(label, row.transform, 18, TextAlignmentOptions.Left);
            if (lbl != null)
            {
                var lblLe = lbl.GetComponent<LayoutElement>();
                lblLe.preferredWidth = 140;
                lblLe.minWidth = 140;
                lblLe.flexibleWidth = 1;
            }

            var toggle = CreateNativeToggle(isOn, row.transform, height);
            if (toggle != null)
            {
                var toggleLe = toggle.GetComponent<LayoutElement>();
                toggleLe.preferredWidth = 120;
                toggleLe.minWidth = 80;
            }

            return toggle;
        }
    }
}
