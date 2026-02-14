using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Falcon.Game2;

namespace TCAMultiplayer.UI
{
    public static class UIFactory
    {
        private static Button _buttonPrefab;
        private static TextMeshProUGUI _textPrefab;
        private static TMP_Dropdown _dropdownPrefab;

        public static void Initialize(MainMenu mainMenu)
        {
            if (mainMenu != null)
            {
                var arenaButton = (Button)typeof(MainMenu).GetField("ArenaButton", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(mainMenu);
                if (arenaButton != null)
                {
                    _buttonPrefab = arenaButton;
                    _textPrefab = arenaButton.GetComponentInChildren<TextMeshProUGUI>();
                }
            }
            
            // Try to find an existing dropdown in the scene to use as prefab
            var existingDropdown = Object.FindObjectOfType<TMP_Dropdown>();
            if (existingDropdown != null)
            {
                _dropdownPrefab = existingDropdown;
                Plugin.Log?.LogInfo("[UIFactory] Found existing TMP_Dropdown to use as prefab");
            }
        }

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

        public static Button CreateNativeButton(string label, Transform parent, float height = 50)
        {
            if (_buttonPrefab == null) return null;

            var go = Object.Instantiate(_buttonPrefab.gameObject, parent);
            go.name = label + " Button";
            
            var le = go.GetComponent<LayoutElement>();
            if (le == null) le = go.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;

            var button = go.GetComponent<Button>();
            button.onClick.RemoveAllListeners();

            var text = go.GetComponentInChildren<TextMeshProUGUI>();
            if (text != null)
            {
                text.richText = true;
                text.parseCtrlCharacters = true;
                text.text = label; // Set text AFTER enabling richText
            }

            return button;
        }

        public static TextMeshProUGUI CreateNativeText(string text, Transform parent, float fontSize = 24, TextAlignmentOptions alignment = TextAlignmentOptions.Center)
        {
            if (_textPrefab == null) return null;

            var go = Object.Instantiate(_textPrefab.gameObject, parent);
            go.name = "Text: " + text;
            
            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.richText = true;
            tmp.parseCtrlCharacters = true;
            tmp.text = text; // Set text AFTER enabling richText
            tmp.fontSize = fontSize;
            tmp.alignment = alignment;
            
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = fontSize + 10;
            le.preferredHeight = fontSize + 10;

            return tmp;
        }

        public static TMP_InputField CreateNativeInputField(string initialValue, Transform parent, UnityEngine.Events.UnityAction<string> onValueChanged)
        {
            var go = new GameObject("InputField", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var img = go.AddComponent<Image>();
            img.color = new Color(0.1f, 0.1f, 0.1f, 0.8f);

            var input = go.AddComponent<TMP_InputField>();
            
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = 40;
            le.preferredHeight = 40;

            var textArea = new GameObject("TextArea", typeof(RectTransform));
            textArea.transform.SetParent(go.transform, false);
            var taRect = textArea.GetComponent<RectTransform>();
            taRect.anchorMin = Vector2.zero;
            taRect.anchorMax = Vector2.one;
            taRect.offsetMin = new Vector2(10, 5);
            taRect.offsetMax = new Vector2(-10, -5);

            if (_textPrefab != null)
            {
                var placeholder = Object.Instantiate(_textPrefab.gameObject, textArea.transform);
                placeholder.name = "Placeholder";
                var pText = placeholder.GetComponent<TextMeshProUGUI>();
                pText.text = "Enter text...";
                pText.fontSize = 18;
                pText.color = new Color(0.5f, 0.5f, 0.5f, 0.5f);
                pText.alignment = TextAlignmentOptions.Left;
                input.placeholder = pText;
                
                var text = Object.Instantiate(_textPrefab.gameObject, textArea.transform);
                text.name = "Text";
                var tText = text.GetComponent<TextMeshProUGUI>();
                tText.text = "";
                tText.fontSize = 18;
                tText.color = Color.white;
                tText.alignment = TextAlignmentOptions.Left;
                input.textComponent = tText;
            }

            input.text = initialValue;
            input.onValueChanged.AddListener(onValueChanged);
            input.onSelect.AddListener((_) => { if (Falcon.UniversalAircraft.UniAircraft.Player != null) Falcon.UniversalAircraft.UniAircraft.Player.gameObject.SetActive(false); });
            input.onDeselect.AddListener((_) => { if (Falcon.UniversalAircraft.UniAircraft.Player != null) Falcon.UniversalAircraft.UniAircraft.Player.gameObject.SetActive(true); });

            return input;
        }

        public static void CreateLabelInputRow(string label, string initialValue, Transform parent, UnityEngine.Events.UnityAction<string> onValueChanged)
        {
            var row = new GameObject("Row", typeof(RectTransform));
            row.transform.SetParent(parent, false);
            
            var layout = row.AddComponent<HorizontalLayoutGroup>();
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = true;
            layout.childForceExpandWidth = false;
            layout.spacing = 10;
            
            var le = row.AddComponent<LayoutElement>();
            le.minHeight = 40;
            le.preferredHeight = 40;
            
            var lbl = CreateNativeText(label, row.transform, 18, TextAlignmentOptions.Left);
            var lblLe = lbl.GetComponent<LayoutElement>();
            lblLe.preferredWidth = 150;
            
            var input = CreateNativeInputField(initialValue, row.transform, onValueChanged);
            var inputLe = input.GetComponent<LayoutElement>();
            inputLe.flexibleWidth = 1;
        }

        /// <summary>
        /// Creates a dropdown with label for compact selection UI.
        /// Uses an existing game dropdown as prefab if available, otherwise creates from scratch.
        /// </summary>
        public static TMP_Dropdown CreateNativeDropdown(List<string> options, int selectedIndex, Transform parent, float height = 40)
        {
            TMP_Dropdown dropdown;
            
            // Try to use existing dropdown as prefab
            if (_dropdownPrefab != null)
            {
                var go = Object.Instantiate(_dropdownPrefab.gameObject, parent);
                go.name = "Dropdown";
                dropdown = go.GetComponent<TMP_Dropdown>();
                
                // Clear existing options and set new ones
                dropdown.ClearOptions();
                dropdown.AddOptions(options);
                dropdown.value = selectedIndex;
                
                // Add layout element
                var layoutEl = go.GetComponent<LayoutElement>();
                if (layoutEl == null) layoutEl = go.AddComponent<LayoutElement>();
                layoutEl.minHeight = height;
                layoutEl.preferredHeight = height;
                layoutEl.flexibleWidth = 1;
                
                return dropdown;
            }
            
            // Fallback: Create dropdown from scratch
            var fallbackGo = new GameObject("Dropdown", typeof(RectTransform));
            fallbackGo.transform.SetParent(parent, false);
            
            // Background image
            var img = fallbackGo.AddComponent<Image>();
            img.color = new Color(0.15f, 0.15f, 0.15f, 1f);
            
            // Layout element
            var le = fallbackGo.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
            le.flexibleWidth = 1;
            
            // Dropdown component
            dropdown = fallbackGo.AddComponent<TMP_Dropdown>();
            
            // Create template for dropdown list
            var template = CreateDropdownTemplate(fallbackGo.transform);
            dropdown.template = template.GetComponent<RectTransform>();
            
            // Create caption text area
            var captionArea = new GameObject("CaptionArea", typeof(RectTransform));
            captionArea.transform.SetParent(fallbackGo.transform, false);
            var capRect = captionArea.GetComponent<RectTransform>();
            capRect.anchorMin = Vector2.zero;
            capRect.anchorMax = Vector2.one;
            capRect.offsetMin = new Vector2(10, 2);
            capRect.offsetMax = new Vector2(-25, -2);
            
            if (_textPrefab != null)
            {
                var captionText = Object.Instantiate(_textPrefab.gameObject, captionArea.transform);
                captionText.name = "CaptionText";
                var capTmp = captionText.GetComponent<TextMeshProUGUI>();
                capTmp.text = "";
                capTmp.fontSize = 16;
                capTmp.alignment = TextAlignmentOptions.Left;
                capTmp.color = Color.white;
                dropdown.captionText = capTmp;
            }
            
            // Create arrow
            var arrow = new GameObject("Arrow", typeof(RectTransform), typeof(Image));
            arrow.transform.SetParent(fallbackGo.transform, false);
            var arrowRect = arrow.GetComponent<RectTransform>();
            arrowRect.anchorMin = new Vector2(1, 0.5f);
            arrowRect.anchorMax = new Vector2(1, 0.5f);
            arrowRect.sizeDelta = new Vector2(20, 20);
            arrowRect.anchoredPosition = new Vector2(-15, 0);
            var arrowImg = arrow.GetComponent<Image>();
            arrowImg.color = Color.white;
            
            // Set options
            dropdown.options = new List<TMP_Dropdown.OptionData>();
            foreach (var option in options)
            {
                dropdown.options.Add(new TMP_Dropdown.OptionData(option));
            }
            
            dropdown.value = selectedIndex;
            
            // Set itemText from template (find the Item Label in the template)
            var itemLabel = template.GetComponentInChildren<TextMeshProUGUI>();
            if (itemLabel != null)
            {
                dropdown.itemText = itemLabel;
            }
            
            return dropdown;
        }
        
        private static GameObject CreateDropdownTemplate(Transform parent)
        {
            var template = new GameObject("Template", typeof(RectTransform));
            template.transform.SetParent(parent, false);
            
            var templateRect = template.GetComponent<RectTransform>();
            templateRect.anchorMin = new Vector2(0, 0);
            templateRect.anchorMax = new Vector2(1, 0);
            templateRect.pivot = new Vector2(0.5f, 1);
            templateRect.anchoredPosition = new Vector2(0, -2);
            templateRect.sizeDelta = new Vector2(0, 150);
            
            var templateImg = template.AddComponent<Image>();
            templateImg.color = new Color(0.1f, 0.1f, 0.1f, 0.95f);
            
            var templateCanvasGroup = template.AddComponent<CanvasGroup>();
            templateCanvasGroup.ignoreParentGroups = true;
            
            // Scroll rect
            var scrollRect = template.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 30;
            
            // Viewport
            var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(Mask));
            viewport.transform.SetParent(template.transform, false);
            var vpRect = viewport.GetComponent<RectTransform>();
            vpRect.anchorMin = Vector2.zero;
            vpRect.anchorMax = Vector2.one;
            vpRect.offsetMin = new Vector2(1, 1);
            vpRect.offsetMax = new Vector2(-1, -1);
            var vpImg = viewport.GetComponent<Image>();
            vpImg.color = new Color(0.1f, 0.1f, 0.1f, 0.01f);
            var vpMask = viewport.GetComponent<Mask>();
            vpMask.showMaskGraphic = false;
            
            scrollRect.viewport = vpRect;
            
            // Content
            var content = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            content.transform.SetParent(viewport.transform, false);
            var contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = new Vector2(1, 1);
            contentRect.pivot = new Vector2(0.5f, 1);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = new Vector2(0, 0);
            
            var contentLayout = content.GetComponent<VerticalLayoutGroup>();
            contentLayout.spacing = 4;
            contentLayout.childControlHeight = false;
            contentLayout.childControlWidth = true;
            contentLayout.childForceExpandHeight = false;
            contentLayout.childForceExpandWidth = true;
            contentLayout.padding = new RectOffset(5, 5, 5, 5);
            
            var contentFitter = content.GetComponent<ContentSizeFitter>();
            contentFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            contentFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            
            scrollRect.content = contentRect;
            
            // Item template - this is the prefab for each item
            var item = new GameObject("Item", typeof(RectTransform));
            item.transform.SetParent(content.transform, false);
            
            var itemRect = item.GetComponent<RectTransform>();
            itemRect.anchorMin = new Vector2(0, 0);
            itemRect.anchorMax = new Vector2(1, 0);
            itemRect.pivot = new Vector2(0.5f, 0.5f);
            itemRect.sizeDelta = new Vector2(0, 30);
            
            // Add layout element to enforce height
            var itemLE = item.AddComponent<LayoutElement>();
            itemLE.minHeight = 30;
            itemLE.preferredHeight = 30;
            
            var itemToggle = item.AddComponent<Toggle>();
            
            var itemBg = item.AddComponent<Image>();
            itemBg.color = new Color(0.2f, 0.2f, 0.2f, 1f);
            itemToggle.targetGraphic = itemBg;
            
            // Item check mark
            var check = new GameObject("Item Check", typeof(RectTransform), typeof(Image));
            check.transform.SetParent(item.transform, false);
            var checkRect = check.GetComponent<RectTransform>();
            checkRect.anchorMin = new Vector2(0, 0.5f);
            checkRect.anchorMax = new Vector2(0, 0.5f);
            checkRect.pivot = new Vector2(0, 0.5f);
            checkRect.sizeDelta = new Vector2(16, 16);
            checkRect.anchoredPosition = new Vector2(5, 0);
            var checkImg = check.GetComponent<Image>();
            checkImg.color = Color.cyan;
            itemToggle.graphic = checkImg;
            
            // Item label
            if (_textPrefab != null)
            {
                var itemLabel = Object.Instantiate(_textPrefab.gameObject, item.transform);
                itemLabel.name = "Item Label";
                var labelRect = itemLabel.GetComponent<RectTransform>();
                labelRect.anchorMin = new Vector2(0, 0);
                labelRect.anchorMax = new Vector2(1, 1);
                labelRect.pivot = new Vector2(0.5f, 0.5f);
                labelRect.offsetMin = new Vector2(25, 2);
                labelRect.offsetMax = new Vector2(-5, -2);
                var labelTmp = itemLabel.GetComponent<TextMeshProUGUI>();
                labelTmp.text = "Option";
                labelTmp.fontSize = 14;
                labelTmp.alignment = TextAlignmentOptions.Left;
                labelTmp.color = Color.white;
            }
            
            template.SetActive(false);
            return template;
        }
        
        /// <summary>
        /// Creates a labeled dropdown row for compact UI
        /// </summary>
        public static TMP_Dropdown CreateLabeledDropdown(string label, List<string> options, int selectedIndex, Transform parent, float height = 40)
        {
            var row = new GameObject("DropdownRow", typeof(RectTransform));
            row.transform.SetParent(parent, false);
            
            var layout = row.AddComponent<HorizontalLayoutGroup>();
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = true;
            layout.childForceExpandWidth = false;
            layout.spacing = 10;
            
            var le = row.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
            
            var lbl = CreateNativeText(label, row.transform, 16, TextAlignmentOptions.Left);
            var lblLe = lbl.GetComponent<LayoutElement>();
            lblLe.preferredWidth = 120;
            lblLe.minWidth = 120;
            
            var dropdown = CreateNativeDropdown(options, selectedIndex, row.transform, height);
            var ddLe = dropdown.GetComponent<LayoutElement>();
            ddLe.flexibleWidth = 1;
            
            return dropdown;
        }
    }
}
