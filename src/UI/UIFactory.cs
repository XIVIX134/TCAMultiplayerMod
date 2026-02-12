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
        private static TMP_InputField _inputFieldPrefab;

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
    }
}
