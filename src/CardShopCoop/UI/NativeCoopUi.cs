using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using CardShopCoop.Api;
using HarmonyLib;
using I2.Loc;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace CardShopCoop.UI
{
    internal static class NativeCoopUi
    {
        internal static readonly Tint Purple = new Tint(new Color(0.3f, 0.13f, 0.52f), new Color(0.9f, 0.79f, 1f), new Color(0.62f, 0.38f, 0.96f));

        internal static readonly Tint Green = new Tint(new Color(0.25f, 0.6f, 0f), new Color(0.98f, 1f, 0.74f), new Color(0.74f, 0.92f, 0f));

        internal static readonly Tint Blue = new Tint(new Color(0.12f, 0.31f, 0.54f), new Color(0.35f, 0.91f, 1f), new Color(0.09f, 0.66f, 1f));

        internal static readonly Tint Orange = new Tint(new Color(0.54f, 0.2f, 0.12f), new Color(1f, 0.89f, 0.35f), new Color(1f, 0.41f, 0.09f));

        internal static readonly Tint Grey = new Tint(new Color(0.42f, 0.42f, 0.42f), new Color(0.83f, 0.83f, 0.83f), new Color(0.63f, 0.63f, 0.63f));

        internal static readonly Color PanelEdge = new Color(0.13f, 0.09f, 0.17f);

        internal static readonly Color PanelFill = new Color(0.22f, 0.16f, 0.26f);

        internal static readonly Color RowFill = new Color(0.3f, 0.23f, 0.36f);

        internal static readonly Color WellFill = new Color(0.17f, 0.12f, 0.21f);

        internal static readonly Color InputText = Color.white;

        internal static readonly Color Dim = new Color(0.8f, 0.76f, 0.86f);

        internal static readonly Color Gold = new Color(1f, 0.83f, 0.23f);

        internal static readonly Color Bad = new Color(1f, 0.56f, 0.45f);

        internal static readonly Color Good = new Color(0.62f, 0.95f, 0.55f);

        internal static Sprite Rounded;

        internal static Sprite White;

        internal static TMP_FontAsset Font;

        internal static Material TitleMaterial;

        private static GameObject _holder;

        private static GameObject _buttonTemplate;

        private static TextMeshProUGUI _textTemplate;

        private static FieldInfo _noWarn;

        private static bool _noWarnTried;

        private static Texture2D _eyeOpen;

        private static Texture2D _eyeShut;

        private static Material _inputMaterial;

        internal static bool HasTemplate => _buttonTemplate != null;

        internal static bool MuteTmp()
        {
            try
            {
                if (!_noWarnTried)
                {
                    _noWarnTried = true;
                    _noWarn = AccessTools.Field(typeof(TMP_Settings), "m_warningsDisabled");
                }
                TMP_Settings instance = TMP_Settings.instance;
                if (instance == null || _noWarn == null)
                {
                    return true;
                }
                bool result = (bool)_noWarn.GetValue(instance);
                _noWarn.SetValue(instance, true);
                return result;
            }
            catch (Exception ex)
            {
                CoopLog.WarnOnce("coop.ui.tmp-mute",
                    "coop: could not mute TMP warnings for UI cloning: " + ex.Message);
                _noWarn = null;
                return true;
            }
        }

        internal static void UnmuteTmp(bool was)
        {
            if (was || _noWarn == null)
            {
                return;
            }
            try
            {
                TMP_Settings instance = TMP_Settings.instance;
                if (instance != null)
                {
                    _noWarn.SetValue(instance, false);
                }
            }
            catch (Exception ex)
            {
                CoopLog.WarnOnce("coop.ui.tmp-unmute",
                    "coop: could not restore TMP warnings after UI cloning: " + ex.Message);
            }
        }

        internal static Texture2D Eye(bool shut)
        {
            Texture2D tex = shut ? _eyeShut : _eyeOpen;
            if (tex != null)
            {
                return tex;
            }
            tex = new Texture2D(64, 64, TextureFormat.RGBA32, false, false);
            tex.name = shut ? "NativeCoopUi_EyeShut" : "NativeCoopUi_EyeOpen";
            tex.wrapMode = TextureWrapMode.Clamp;
            Color[] array = new Color[4096];
            float num = 0.034375f;
            for (int i = 0; i < 64; i++)
            {
                for (int j = 0; j < 64; j++)
                {
                    float num2 = ((float)j + 0.5f) / 64f * 2f - 1f;
                    float num3 = ((float)i + 0.5f) / 64f * 2f - 1f;
                    float num4 = num2 / 0.92f;
                    float num5 = (!(Mathf.Abs(num4) <= 1f))
                        ? Mathf.Sqrt((Mathf.Abs(num2) - 0.92f) * (Mathf.Abs(num2) - 0.92f) + num3 * num3)
                        : (Mathf.Abs(Mathf.Abs(num3) - 0.5f * (1f - num4 * num4)) / Mathf.Sqrt(1f + num4 * num4));
                    float num6 = Mathf.Clamp01((0.075f - num5) / num + 0.5f);
                    float num7 = Mathf.Sqrt(num2 * num2 + num3 * num3);
                    num6 = Mathf.Max(num6, Mathf.Clamp01((0.24f - num7) / num + 0.5f));
                    if (shut)
                    {
                        float num8 = Mathf.Abs(num2 - num3) * 0.7071f;
                        if (Mathf.Abs(num2 + num3) * 0.7071f < 1.15f)
                        {
                            num6 *= Mathf.Clamp01((num8 - 0.16f) / num + 0.5f);
                            num6 = Mathf.Max(num6, Mathf.Clamp01((0.08f - num8) / num + 0.5f));
                        }
                    }
                    array[i * 64 + j] = new Color(1f, 1f, 1f, num6);
                }
            }
            tex.SetPixels(array);
            tex.Apply(false, true);
            if (shut)
            {
                _eyeShut = tex;
            }
            else
            {
                _eyeOpen = tex;
            }
            return tex;
        }

        internal static RawImage IconOn(CoopButton b, float px)
        {
            if (b == null)
            {
                return null;
            }
            Transform parent = b.Label != null ? b.Label.transform : (Transform)b.Rt;
            GameObject go = new GameObject("NativeCoopUi_Icon", typeof(RectTransform));
            RectTransform rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            Vector2 center = new Vector2(0.5f, 0.5f);
            rt.anchorMax = center;
            rt.anchorMin = center;
            rt.pivot = new Vector2(0.5f, 0.5f);
            float num = b.Rt.parent != null ? Mathf.Abs(b.Rt.parent.lossyScale.x) : 1f;
            float num2 = Mathf.Max(0.05f, Mathf.Abs(parent.lossyScale.x) / Mathf.Max(1E-05f, num));
            rt.sizeDelta = new Vector2(px / num2, px / num2);
            rt.anchoredPosition = Vector2.zero;
            RawImage img = go.AddComponent<RawImage>();
            img.raycastTarget = false;
            img.color = Color.white;
            return img;
        }

        internal static void Shutdown()
        {
            if (_eyeOpen != null)
            {
                UnityEngine.Object.Destroy(_eyeOpen);
            }
            if (_eyeShut != null)
            {
                UnityEngine.Object.Destroy(_eyeShut);
            }
            _eyeOpen = null;
            _eyeShut = null;
            if (_holder != null)
            {
                UnityEngine.Object.Destroy(_holder);
            }
            _holder = null;
            _buttonTemplate = null;
            _textTemplate = null;
            if (_inputMaterial != null)
            {
                UnityEngine.Object.Destroy(_inputMaterial);
            }
            _inputMaterial = null;
        }

        private static Transform Holder()
        {
            if (_holder != null)
            {
                return _holder.transform;
            }
            _holder = new GameObject("NativeCoopUi_UiTemplates", typeof(RectTransform));
            _holder.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(_holder);
            return _holder.transform;
        }

        internal static void CaptureTemplate(GameObject vanillaButton)
        {
            if (_buttonTemplate != null || vanillaButton == null)
            {
                return;
            }
            bool was = MuteTmp();
            GameObject go;
            try
            {
                go = UnityEngine.Object.Instantiate(vanillaButton, Holder(), false);
            }
            finally
            {
                UnmuteTmp(was);
            }
            go.name = "NativeCoopUi_ButtonTemplate";
            Localize[] componentsInChildren = go.GetComponentsInChildren<Localize>(true);
            for (int i = 0; i < componentsInChildren.Length; i++)
            {
                UnityEngine.Object.DestroyImmediate(componentsInChildren[i]);
            }
            Transform borderPart = Part(go.transform, "BGBorder");
            Image borderImg = borderPart != null ? borderPart.GetComponent<Image>() : null;
            if (borderImg != null && borderImg.sprite != null)
            {
                Rounded = borderImg.sprite;
            }
            Transform raycastPart = Part(go.transform, "BtnRaycast");
            Image raycastImg = raycastPart != null ? raycastPart.GetComponent<Image>() : null;
            if (raycastImg != null && raycastImg.sprite != null)
            {
                White = raycastImg.sprite;
            }
            TextMeshProUGUI label = LabelOf(go.transform);
            if (label != null)
            {
                Font = label.font;
                TitleMaterial = label.fontSharedMaterial;
            }
            if (Font == null)
            {
                FontLocalizationQuickPatcher instance = FontLocalizationQuickPatcher.m_Instance;
                if (instance != null)
                {
                    Font = instance.m_MainFont;
                }
            }
            _buttonTemplate = go;
            ManualLogSource log = CoopPlugin.Log;
            if (log != null)
            {
                log.LogInfo("coop: menu button template taken from " + vanillaButton.name + " (font " + (Font != null ? Font.name : "none") + ")");
            }
        }

        internal static Transform Part(Transform root, string name)
        {
            Transform t = root.Find("AnimGrp/BGBarGrp/" + name);
            if (t != null)
            {
                return t;
            }
            return FindDeep(root, name);
        }

        internal static TextMeshProUGUI LabelOf(Transform root)
        {
            Transform t = root.Find("AnimGrp/BGBarGrp/BtnImageGrp/Text");
            if (t == null)
            {
                t = FindDeep(root, "Text");
            }
            if (t == null)
            {
                return null;
            }
            return t.GetComponent<TextMeshProUGUI>();
        }

        internal static Transform FindDeep(Transform t, string name)
        {
            if (t == null)
            {
                return null;
            }
            if (t.name == name)
            {
                return t;
            }
            for (int i = 0; i < t.childCount; i++)
            {
                Transform child = FindDeep(t.GetChild(i), name);
                if (child != null)
                {
                    return child;
                }
            }
            return null;
        }

        internal static void Paint(Transform root, Tint tint)
        {
            SetColor(Part(root, "BGBorder"), tint.Border);
            SetColor(Part(root, "BGHighlight"), tint.Highlight);
            SetColor(Part(root, "BGMidtone"), tint.Mid);
        }

        private static void SetColor(Transform t, Color c)
        {
            if (t != null)
            {
                Image component = t.GetComponent<Image>();
                if (component != null)
                {
                    component.color = c;
                }
            }
        }

        internal static CoopButton Restyle(GameObject go, string label, Tint tint, UnityAction onClick)
        {
            CoopButton coopButton = new CoopButton
            {
                Go = go,
                Rt = (RectTransform)go.transform
            };
            Localize[] componentsInChildren = go.GetComponentsInChildren<Localize>(true);
            for (int i = 0; i < componentsInChildren.Length; i++)
            {
                UnityEngine.Object.DestroyImmediate(componentsInChildren[i]);
            }
            Paint(go.transform, tint);
            coopButton.Label = LabelOf(go.transform);
            coopButton.SetLabel(label);
            Transform raycast = Part(go.transform, "BtnRaycast");
            coopButton.Btn = raycast != null ? raycast.GetComponent<Button>() : null;
            if (coopButton.Btn == null)
            {
                coopButton.Btn = go.GetComponentInChildren<Button>(true);
            }
            if (coopButton.Btn != null)
            {
                coopButton.Btn.onClick = new Button.ButtonClickedEvent();
                coopButton.Btn.onClick.AddListener(Tap);
                if (onClick != null)
                {
                    coopButton.Btn.onClick.AddListener(onClick);
                }
                coopButton.Btn.interactable = true;
            }
            coopButton.Ctrl = go.GetComponent<ControllerButton>();
            if (coopButton.Ctrl != null && coopButton.Btn != null)
            {
                coopButton.Ctrl.m_Button = coopButton.Btn;
            }
            return coopButton;
        }

        private static void Tap()
        {
            try
            {
                SoundManager.GenericLightTap(1f, 1f);
            }
            catch (Exception ex)
            {
                CoopLog.WarnOnce("coop.ui.tap",
                    "coop: UI tap sound failed: " + ex.Message);
            }
        }

        internal static CoopButton Button(Transform parent, string label, Tint tint, UnityAction onClick, float x, float y, float w, float h, float textPx)
        {
            if (_buttonTemplate == null)
            {
                return null;
            }
            bool was = MuteTmp();
            GameObject go;
            try
            {
                go = UnityEngine.Object.Instantiate(_buttonTemplate, parent, false);
            }
            finally
            {
                UnmuteTmp(was);
            }
            go.name = "NativeCoopUi_Btn";
            CoopButton coopButton = Restyle(go, label, tint, onClick);
            float num = Mathf.Clamp(h / 120f, 0.3f, 0.6f);
            if (h / num + 136f < 224f)
            {
                num = h / 88f;
            }
            float num2 = h / num + 136f;
            RectTransform rt = coopButton.Rt;
            Vector2 topLeft = new Vector2(0f, 1f);
            rt.anchorMax = topLeft;
            rt.anchorMin = topLeft;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.localScale = new Vector3(num, num, 1f);
            rt.sizeDelta = new Vector2(w / num + 36f, num2 - 74f);
            rt.anchoredPosition = new Vector2(x + w * 0.5f, 0f - (y + h * 0.5f));
            Transform bgBar = go.transform.Find("AnimGrp/BGBarGrp");
            if (bgBar != null)
            {
                RectTransform bgRt = (RectTransform)bgBar;
                bgRt.sizeDelta = new Vector2(bgRt.sizeDelta.x, num2);
            }
            if (coopButton.Label != null)
            {
                coopButton.Label.enableAutoSizing = false;
                coopButton.Label.fontSize = textPx / num;
                coopButton.Label.enableWordWrapping = false;
                coopButton.Label.overflowMode = TextOverflowModes.Overflow;
                coopButton.Label.alignment = TextAlignmentOptions.Center;
            }
            return coopButton;
        }

        /// <summary>A classic checkbox row: a square box with a tick, the field label beside it, and
        /// the whole row as the click target. Kept separate from <see cref="Button"/> because a
        /// toggle is not an action - it owns a boolean, and drawing it as a button invited the
        /// enabled/disabled colouring that made it look like "Copy password" next to it.</summary>
        internal static Button Checkbox(Transform parent, bool isOn, string label, float x, float y,
            float width, float height, float textPx, UnityAction onClick)
        {
            const float BoxSize = 26f;

            // The transparent row image is the hit area, so clicking the label toggles as well.
            Image hit = Flat(parent, x, y, width, height, new Color(0f, 0f, 0f, 0f));
            hit.raycastTarget = true;
            var button = hit.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = hit;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(Tap);
            if (onClick != null)
            {
                button.onClick.AddListener(onClick);
            }

            float boxY = y + (height - BoxSize) * 0.5f;
            Flat(parent, x, boxY, BoxSize, BoxSize, isOn ? Purple.Highlight : Dim);
            Flat(parent, x + 2f, boxY + 2f, BoxSize - 4f, BoxSize - 4f,
                isOn ? Purple.Border : PanelFill);
            if (isOn)
            {
                // Prefer a real tick; fall back to the font's X only when it has no tick glyph.
                string mark = Font != null && Font.HasCharacter('\u2713') ? "\u2713" : "X";
                Text(parent, mark, 18f, x, boxY, BoxSize, BoxSize, Color.white,
                    TextAlignmentOptions.Center, wrap: false, title: false);
            }

            Text(parent, label, textPx, x + BoxSize + 12f, y, width - BoxSize - 12f, height,
                Color.white, TextAlignmentOptions.MidlineLeft, wrap: false, title: false);
            return button;
        }

        internal static RectTransform Node(Transform parent, string name, float x, float y, float w, float h)
        {
            RectTransform rt = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rt.SetParent(parent, false);
            TopLeft(rt, x, y, w, h);
            return rt;
        }

        internal static void TopLeft(RectTransform rt, float x, float y, float w, float h)
        {
            Vector2 topLeft = new Vector2(0f, 1f);
            rt.anchorMax = topLeft;
            rt.anchorMin = topLeft;
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, 0f - y);
            rt.sizeDelta = new Vector2(w, h);
        }

        internal static void Stretch(RectTransform rt, float left, float top, float right, float bottom)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(0f - right, 0f - top);
        }

        internal static Image Panel(Transform parent, float x, float y, float w, float h, Color color, float density)
        {
            GameObject go = new GameObject("NativeCoopUi_Panel", typeof(RectTransform), typeof(Image));
            RectTransform rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            Image component = go.GetComponent<Image>();
            component.raycastTarget = false;
            component.color = color;
            if (Rounded != null)
            {
                float num = Mathf.Max(density, 0.5f);
                component.sprite = Rounded;
                component.type = Image.Type.Sliced;
                component.pixelsPerUnitMultiplier = num;
                TopLeft(rt, x - 68f / num, y - 77f / num, w + 136f / num, h + 136f / num);
            }
            else
            {
                if (White != null)
                {
                    component.sprite = White;
                }
                TopLeft(rt, x, y, w, h);
            }
            return component;
        }

        internal static Image Flat(Transform parent, float x, float y, float w, float h, Color color)
        {
            GameObject go = new GameObject("NativeCoopUi_Flat", typeof(RectTransform), typeof(Image));
            RectTransform rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            TopLeft(rt, x, y, w, h);
            Image component = go.GetComponent<Image>();
            if (White != null)
            {
                component.sprite = White;
            }
            component.color = color;
            component.raycastTarget = false;
            return component;
        }

        private static TextMeshProUGUI TextTemplate()
        {
            if (_textTemplate != null)
            {
                return _textTemplate;
            }
            bool was = MuteTmp();
            TextMeshProUGUI component;
            try
            {
                GameObject go = new GameObject("NativeCoopUi_TextTemplate", typeof(RectTransform), typeof(TextMeshProUGUI));
                go.transform.SetParent(Holder(), false);
                component = go.GetComponent<TextMeshProUGUI>();
                if (Font != null)
                {
                    component.font = Font;
                }
            }
            finally
            {
                UnmuteTmp(was);
            }
            component.enableAutoSizing = false;
            component.richText = false;
            component.raycastTarget = false;
            component.overflowMode = TextOverflowModes.Overflow;
            component.margin = Vector4.zero;
            component.text = "";
            TopLeft(component.rectTransform, 0f, 0f, 10f, 10f);
            _textTemplate = component;
            return component;
        }

        internal static TextMeshProUGUI Text(Transform parent, string text, float size, float x, float y, float w, float h, Color color, TextAlignmentOptions align, bool wrap, bool title)
        {
            TextMeshProUGUI tmpl = TextTemplate();
            bool was = MuteTmp();
            TextMeshProUGUI label;
            try
            {
                label = UnityEngine.Object.Instantiate(tmpl, parent, false);
            }
            finally
            {
                UnmuteTmp(was);
            }
            label.name = "NativeCoopUi_Text";
            RectTransform rectTransform = label.rectTransform;
            rectTransform.anchoredPosition = new Vector2(x, 0f - y);
            rectTransform.sizeDelta = new Vector2(w, h);
            if (title && TitleMaterial != null)
            {
                label.fontSharedMaterial = TitleMaterial;
            }
            label.fontSize = size;
            label.color = color;
            label.alignment = align;
            label.enableWordWrapping = wrap;
            if (!wrap)
            {
                label.overflowMode = TextOverflowModes.Ellipsis;
            }
            label.text = text ?? "";
            return label;
        }

        internal static void Set(TextMeshProUGUI t, string text)
        {
            if (t != null)
            {
                text = text ?? "";
                if (t.text != text)
                {
                    t.text = text;
                }
            }
        }

        internal static void Show(Component c, bool on)
        {
            if (c != null && c.gameObject.activeSelf != on)
            {
                c.gameObject.SetActive(on);
            }
        }

        internal static void Show(GameObject g, bool on)
        {
            if (g != null && g.activeSelf != on)
            {
                g.SetActive(on);
            }
        }

        internal static TMP_InputField Input(Transform parent, string placeholder, float x, float y, float w, float h, float size, int limit, TMP_InputField.ContentType type)
        {
            GameObject go = new GameObject("NativeCoopUi_Input", typeof(RectTransform));
            go.SetActive(false);
            RectTransform rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            TopLeft(rt, x, y, w, h);
            Image bgImg = go.AddComponent<Image>();
            if (White != null)
            {
                bgImg.sprite = White;
            }
            bgImg.color = new Color(1f, 1f, 1f, 0f);
            // Explicit two-layer field: a black outer frame and a dark inner fill. The cloned
            // vanilla border sprite has a transparent centre, so tinting it alone let the dark
            // panel behind show through. The entered text is white with a black outline.
            Flat(go.transform, 0f, 0f, w, h, Color.black).name = "Border";
            Flat(go.transform, 2f, 2f, w - 4f, h - 4f, WellFill).name = "BG";
            RectTransform textArea = Node(go.transform, "TextArea", 0f, 0f, w, h);
            Stretch(textArea, 18f, 4f, 18f, 4f);
            textArea.gameObject.AddComponent<RectMask2D>();
            TextMeshProUGUI phText = Text(textArea, placeholder, size, 0f, 0f, w, h, new Color(0.68f, 0.64f, 0.76f), TextAlignmentOptions.MidlineLeft, wrap: false, title: false);
            Stretch(phText.rectTransform, 0f, 0f, 0f, 0f);
            phText.fontStyle = FontStyles.Italic;
            TextMeshProUGUI inputText = Text(textArea, "", size, 0f, 0f, w, h, InputText, TextAlignmentOptions.MidlineLeft, wrap: false, title: false);
            Stretch(inputText.rectTransform, 0f, 0f, 0f, 0f);
            inputText.overflowMode = TextOverflowModes.Overflow;
            ApplyInputStyle(inputText);
            TMP_InputField field = go.AddComponent<TMP_InputField>();
            field.textViewport = textArea;
            field.textComponent = inputText;
            field.placeholder = phText;
            field.targetGraphic = bgImg;
            field.characterLimit = limit;
            field.contentType = type;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.customCaretColor = true;
            field.caretColor = InputText;
            field.caretWidth = 3;
            field.selectionColor = new Color(0.62f, 0.38f, 0.96f, 0.45f);
            field.richText = false;
            go.SetActive(true);
            return field;
        }

        private static void ApplyInputStyle(TextMeshProUGUI text)
        {
            if (text == null)
            {
                return;
            }

            text.color = InputText;
            Material material = InputMaterial(text.fontSharedMaterial);
            if (material != null)
            {
                text.fontSharedMaterial = material;
            }
        }

        private static Material InputMaterial(Material source)
        {
            if (_inputMaterial != null)
            {
                return _inputMaterial;
            }
            if (source == null)
            {
                return null;
            }

            _inputMaterial = new Material(source) { name = "NativeCoopUi_InputMaterial" };
            _inputMaterial.EnableKeyword("OUTLINE_ON");
            if (_inputMaterial.HasProperty("_FaceColor"))
            {
                _inputMaterial.SetColor("_FaceColor", Color.white);
            }
            if (_inputMaterial.HasProperty("_OutlineColor"))
            {
                _inputMaterial.SetColor("_OutlineColor", Color.black);
            }
            if (_inputMaterial.HasProperty("_OutlineWidth"))
            {
                _inputMaterial.SetFloat("_OutlineWidth", 0.2f);
            }
            return _inputMaterial;
        }

        internal static ControllerButton NavFor(GameObject go, Button button, TMP_InputField keyboard)
        {
            bool activeSelf = go.activeSelf;
            if (activeSelf)
            {
                go.SetActive(false);
            }
            ControllerButton ctrl = go.AddComponent<ControllerButton>();
            ctrl.m_OverlayButton = new List<Button>();
            ctrl.m_OverlayButtonHighlight = new List<GameObject>();
            ctrl.m_Button = button;
            ctrl.m_RectTransformOffsetMultiplier = 0f;
            if (keyboard != null)
            {
                ctrl.m_EnableVirtualKeyboard = true;
                ctrl.m_InitVirtualKeyboardWithInputString = true;
                ctrl.m_VirtualKeyboardInputText = keyboard;
            }
            if (activeSelf)
            {
                go.SetActive(true);
            }
            return ctrl;
        }
    }
}
