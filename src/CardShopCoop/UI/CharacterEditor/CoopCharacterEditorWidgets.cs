using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.UI;

namespace CardShopCoop.UI
{
    /// <summary>Small UGUI building blocks for the character editor. They follow the native screen's
    /// top-left layout and reuse <see cref="NativeCoopUi"/> for text, panels, and buttons.</summary>
    internal static class CoopCharacterEditorWidgets
    {
        internal const float ContentWidth = 544f;
        internal const float RowHeight = 38f;
        internal const float SmallRowHeight = 32f;

        private static readonly Color TrackColor = new Color(0.14f, 0.09f, 0.18f);
        private static readonly Color FillColor = new Color(0.62f, 0.38f, 0.96f);
        private static readonly Color SwatchBorder = new Color(0.06f, 0.04f, 0.08f);

        internal static void Section(Transform parent, ref float y, string text)
        {
            NativeCoopUi.Text(parent, text, 20f, 0f, y, ContentWidth, 28f, NativeCoopUi.Gold,
                TextAlignmentOptions.Left, wrap: false, title: true);
            y += 30f;
        }

        internal static TextMeshProUGUI Note(Transform parent, ref float y, string text,
            float height)
        {
            var label = NativeCoopUi.Text(parent, text, 17f, 0f, y, ContentWidth, height,
                NativeCoopUi.Dim, TextAlignmentOptions.TopLeft, wrap: true, title: false);
            y += height + 6f;
            return label;
        }

        internal static TextMeshProUGUI PlainLabel(Transform parent, ref float y, string text,
            float height)
        {
            var label = NativeCoopUi.Text(parent, text, 18f, 0f, y, ContentWidth, height,
                Color.white, TextAlignmentOptions.MidlineLeft, wrap: false, title: false);
            y += height + 4f;
            return label;
        }

        internal static CoopButton WideButton(Transform parent, ref float y, string label, Tint tint,
            UnityAction onClick, float height)
        {
            var button = NativeCoopUi.Button(parent, label, tint, onClick, 0f, y, ContentWidth,
                height, 20f);
            y += height + 6f;
            return button;
        }

        internal static Image CycleRow(Transform parent, ref float y, string label,
            IReadOnlyList<string> options, int selected, Action<int> onStep, bool showColor,
            Color color, Action onColor)
        {
            var row = NativeCoopUi.Node(parent, "CycleRow", 0f, y, ContentWidth, RowHeight);
            NativeCoopUi.Text(row, label, 18f, 0f, 0f, 146f, RowHeight, NativeCoopUi.Dim,
                TextAlignmentOptions.MidlineLeft, wrap: false, title: false);

            var count = options != null ? options.Count : 0;
            var previous = NativeCoopUi.Button(row, "<", NativeCoopUi.Blue, () =>
                Step(options, selected, -1, onStep), 150f, 4f, 34f, 30f, 20f);
            previous?.SetInteractable(count > 1);

            var text = count == 0 ? "-" : options[Mathf.Clamp(selected, 0, count - 1)];
            NativeCoopUi.Text(row, text, 18f, 190f, 0f, 244f, RowHeight, Color.white,
                TextAlignmentOptions.Center, wrap: false, title: false);

            var next = NativeCoopUi.Button(row, ">", NativeCoopUi.Blue, () =>
                Step(options, selected, 1, onStep), 440f, 4f, 34f, 30f, 20f);
            next?.SetInteractable(count > 1);

            Image swatch = null;
            if (showColor)
            {
                swatch = Swatch(row, 492f, 3f, 32f, color, onColor);
            }

            y += RowHeight + 4f;
            return swatch;
        }

        internal static Slider SliderRow(Transform parent, ref float y, string label, float min,
            float max, float value, Action<float> onChanged, Action onCommit)
        {
            var row = NativeCoopUi.Node(parent, "SliderRow", 0f, y, ContentWidth, RowHeight);
            NativeCoopUi.Text(row, label, 18f, 0f, 0f, 146f, RowHeight, NativeCoopUi.Dim,
                TextAlignmentOptions.MidlineLeft, wrap: false, title: false);

            var valueLabel = NativeCoopUi.Text(row, value.ToString("0.00"), 17f, 476f, 0f, 68f,
                RowHeight, Color.white, TextAlignmentOptions.MidlineRight, wrap: false, title: false);

            var slider = BuildSlider(row, 150f, 7f, 316f, 24f, min, max, value);
            slider.onValueChanged.AddListener(next =>
            {
                NativeCoopUi.Set(valueLabel, next.ToString("0.00"));
                onChanged?.Invoke(next);
            });
            if (onCommit != null)
            {
                var commit = slider.gameObject.AddComponent<CoopSliderCommit>();
                commit.Commit = onCommit;
            }
            AttachSliderNav(slider, onCommit);

            y += RowHeight + 2f;
            return slider;
        }

        internal static Image ColorRow(Transform parent, ref float y, string label, Color color,
            Action onEdit)
        {
            var row = NativeCoopUi.Node(parent, "ColorRow", 0f, y, ContentWidth, RowHeight);
            NativeCoopUi.Text(row, label, 18f, 0f, 0f, 146f, RowHeight, NativeCoopUi.Dim,
                TextAlignmentOptions.MidlineLeft, wrap: false, title: false);
            var swatch = Swatch(row, 150f, 3f, 32f, color, onEdit);
            NativeCoopUi.Text(row, "#" + ColorUtility.ToHtmlStringRGBA(color), 16f, 190f, 0f,
                236f, RowHeight, Color.white, TextAlignmentOptions.MidlineLeft, wrap: false,
                title: false);
            NativeCoopUi.Button(row, "Change", NativeCoopUi.Blue, () => onEdit(), 440f, 4f, 104f,
                30f, 17f);
            y += RowHeight + 4f;
            return swatch;
        }

        internal static Image Swatch(Transform parent, float x, float y, float size, Color color,
            Action onClick)
        {
            var outer = NativeCoopUi.Flat(parent, x, y, size, size, SwatchBorder);
            outer.raycastTarget = true;
            var inner = NativeCoopUi.Flat(outer.transform, 2f, 2f, size - 4f, size - 4f, color);

            if (onClick != null)
            {
                var button = outer.gameObject.AddComponent<Button>();
                button.targetGraphic = outer;
                button.transition = Selectable.Transition.None;
                button.onClick.AddListener(() => onClick());
                NativeCoopUi.NavFor(outer.gameObject, button, null);
            }
            return inner;
        }

        internal static CoopColorPicker ColorPicker(Transform parent, ref float y, Color color,
            Action<Color> onChanged, Action onCommit)
        {
            var picker = new CoopColorPicker();
            picker.Build(parent, ref y, color, onChanged, onCommit);
            return picker;
        }

        internal static Slider BuildSlider(Transform parent, float x, float y, float width,
            float height, float min, float max, float value)
        {
            var root = NativeCoopUi.Node(parent, "Slider", x, y, width, height);
            var slider = root.gameObject.AddComponent<Slider>();

            var hitArea = NativeCoopUi.Flat(root, 0f, 0f, width, height,
                new Color(0f, 0f, 0f, 0.001f));
            hitArea.raycastTarget = true;

            var track = NativeCoopUi.Flat(root, 0f, height * 0.5f - 3f, width, 6f, TrackColor);
            track.raycastTarget = true;

            var fillArea = NativeCoopUi.Node(root, "FillArea", 0f, height * 0.5f - 3f, width, 6f);
            var fill = NativeCoopUi.Flat(fillArea, 0f, 0f, width, 6f, FillColor);
            fill.rectTransform.anchorMin = Vector2.zero;
            fill.rectTransform.anchorMax = Vector2.one;
            fill.rectTransform.offsetMin = Vector2.zero;
            fill.rectTransform.offsetMax = Vector2.zero;

            var handleArea = NativeCoopUi.Node(root, "HandleArea", 0f, 0f, width, height);
            var handle = NativeCoopUi.Flat(handleArea, 0f, 0f, 12f, height - 4f, Color.white);
            handle.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            handle.rectTransform.anchorMin = new Vector2(0f, 0f);
            handle.rectTransform.anchorMax = new Vector2(0f, 1f);
            handle.rectTransform.sizeDelta = new Vector2(12f, -4f);
            handle.raycastTarget = true;

            slider.fillRect = fill.rectTransform;
            slider.handleRect = handle.rectTransform;
            slider.targetGraphic = handle;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = false;
            slider.navigation = new Navigation { mode = Navigation.Mode.None };
            slider.SetValueWithoutNotify(Mathf.Clamp(value, min, max));
            return slider;
        }

        /// <summary>Lets the controller adjust a horizontal slider with left/right while keeping the
        /// mouse drag path untouched. ControllerButton requires a Button for IsActive()/confirm, so
        /// the slider gets a zero-size, no-op confirm button; the real actions are the left/right
        /// events. This is deliberately not an overlay: adding one would swallow slider dragging.</summary>
        internal static void AttachSliderNav(Slider slider, Action onCommit)
        {
            if (slider == null)
            {
                return;
            }

            var confirmRoot = NativeCoopUi.Node(slider.transform, "ControllerConfirm", 0f, 0f, 0f, 0f);
            var confirm = confirmRoot.gameObject.AddComponent<Button>();
            confirm.transition = Selectable.Transition.None;
            confirm.navigation = new Navigation { mode = Navigation.Mode.None };

            var ctrl = NativeCoopUi.NavFor(slider.gameObject, confirm, null);
            if (ctrl == null)
            {
                return;
            }

            var step = Mathf.Max(0.01f, (slider.maxValue - slider.minValue) / 20f);
            if (ctrl.m_OnPressLeftEvent == null)
            {
                ctrl.m_OnPressLeftEvent = new UnityEvent();
            }
            if (ctrl.m_OnPressRightEvent == null)
            {
                ctrl.m_OnPressRightEvent = new UnityEvent();
            }
            ctrl.m_OnPressLeftEvent.AddListener(() => AdjustSlider(slider, -step, onCommit));
            ctrl.m_OnPressRightEvent.AddListener(() => AdjustSlider(slider, step, onCommit));
        }

        private static void AdjustSlider(Slider slider, float delta, Action onCommit)
        {
            if (slider == null)
            {
                return;
            }

            var next = Mathf.Clamp(slider.value + delta, slider.minValue, slider.maxValue);
            if (Mathf.Approximately(next, slider.value))
            {
                return;
            }
            slider.value = next;
            onCommit?.Invoke();
        }

        internal static void ShutdownTextures()
        {
            CoopColorPicker.ShutdownTextures();
        }

        private static void Step(IReadOnlyList<string> options, int selected, int delta,
            Action<int> onStep)
        {
            if (options == null || options.Count == 0 || onStep == null)
            {
                return;
            }

            var next = (Mathf.Clamp(selected, 0, options.Count - 1) + delta + options.Count)
                % options.Count;
            onStep(next);
        }
    }

    /// <summary>Commits a continuous slider edit when the pointer is released, so the editor writes
    /// the persisted model once per gesture instead of once per value change.</summary>
    internal sealed class CoopSliderCommit : MonoBehaviour, IPointerUpHandler, IEndDragHandler
    {
        internal Action Commit;

        public void OnPointerUp(PointerEventData eventData) => Commit?.Invoke();

        public void OnEndDrag(PointerEventData eventData) => Commit?.Invoke();
    }

    /// <summary>Normalizes pointer input over a UGUI graphic to a 0..1 rectangle coordinate.</summary>
    internal sealed class CoopPointerArea : MonoBehaviour, IPointerDownHandler, IDragHandler,
        IPointerUpHandler, IEndDragHandler
    {
        internal Action<Vector2> Changed;
        internal Action Began;
        internal Action Released;

        public void OnPointerDown(PointerEventData eventData)
        {
            Began?.Invoke();
            Report(eventData);
        }

        public void OnDrag(PointerEventData eventData)
        {
            Report(eventData);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            Released?.Invoke();
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            Released?.Invoke();
        }

        private void Report(PointerEventData eventData)
        {
            var rt = (RectTransform)transform;
            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, eventData.position,
                eventData.pressEventCamera, out local))
            {
                return;
            }

            var normalized = Rect.PointToNormalized(rt.rect, local);
            normalized.x = Mathf.Clamp01(normalized.x);
            normalized.y = Mathf.Clamp01(normalized.y);
            Changed?.Invoke(normalized);
            eventData.Use();
        }
    }

    /// <summary>A compact HSV picker (saturation/value square + hue strip) built from UGUI
    /// graphics, keeping the same two-panel model without any immediate-mode drawing.</summary>
    internal sealed class CoopColorPicker
    {
        private const float SvWidth = 160f;
        private const float SvHeight = 96f;
        private const float HueWidth = 20f;

        private static Texture2D _svTexture;
        private static Texture2D _hueTexture;
        private static float _svHue = -1f;

        private RawImage _svImage;
        private RawImage _hueImage;
        private Slider _valueSlider;
        private RectTransform _svMarker;
        private RectTransform _hueMarker;
        private Image _swatch;
        private TextMeshProUGUI _hex;
        private float _hue;
        private float _saturation;
        private float _value;
        private Action<Color> _onChanged;
        private Action _onCommit;

        internal void Build(Transform parent, ref float y, Color color, Action<Color> onChanged,
            Action onCommit)
        {
            _onChanged = onChanged;
            _onCommit = onCommit;
            Color.RGBToHSV(color, out _hue, out _saturation, out _value);

            EnsureTextures();
            RebuildSvTexture(_hue);

            NativeCoopUi.Text(parent, "Color", 18f, 0f, y, 146f, 22f,
                NativeCoopUi.Dim, TextAlignmentOptions.MidlineLeft, wrap: false, title: false);

            _svImage = MakeRawImage(parent, 150f, y, SvWidth, SvHeight);
            _hueImage = MakeRawImage(parent, 318f, y, HueWidth, SvHeight);
            _svMarker = MakeMarker(_svImage.rectTransform, 8f, Color.white);
            _hueMarker = MakeMarker(_hueImage.rectTransform, 4f, Color.black);

            _swatch = CoopCharacterEditorWidgets.Swatch(parent, 350f, y, 42f, color, null);
            _hex = NativeCoopUi.Text(parent, "", 16f, 350f, y + 48f, 150f, 22f, Color.white,
                TextAlignmentOptions.Left, wrap: false, title: false);

            var svArea = _svImage.gameObject.AddComponent<CoopPointerArea>();
            svArea.Began = () => { };
            svArea.Changed = normalized =>
            {
                _saturation = normalized.x;
                _value = normalized.y;
                Apply();
            };
            svArea.Released = () => _onCommit?.Invoke();

            var hueArea = _hueImage.gameObject.AddComponent<CoopPointerArea>();
            hueArea.Began = () => { };
            hueArea.Changed = normalized =>
            {
                if (Mathf.Approximately(_hue, normalized.y))
                {
                    return;
                }
                _hue = normalized.y;
                RebuildSvTexture(_hue);
                Apply();
            };
            hueArea.Released = () => _onCommit?.Invoke();

            AttachNav(_svImage, hue: false);
            AttachNav(_hueImage, hue: true);

            // The SV square's vertical axis is the only HSV component left without a directional
            // controller event, so expose it as a normal slider too. It shares _value with the
            // square and is kept in sync in Apply/SetColor.
            NativeCoopUi.Text(parent, "Value", 16f, 0f, y + SvHeight + 4f, 146f, 20f,
                NativeCoopUi.Dim, TextAlignmentOptions.MidlineLeft, wrap: false, title: false);
            _valueSlider = CoopCharacterEditorWidgets.BuildSlider(parent, 150f,
                y + SvHeight + 4f, SvWidth + HueWidth + 16f, 20f, 0f, 1f, _value);
            _valueSlider.onValueChanged.AddListener(next =>
            {
                _value = Mathf.Clamp01(next);
                Apply();
            });
            var valueCommit = _valueSlider.gameObject.AddComponent<CoopSliderCommit>();
            valueCommit.Commit = _onCommit;
            CoopCharacterEditorWidgets.AttachSliderNav(_valueSlider, _onCommit);

            SetColor(color);
            y += SvHeight + 8f + 26f;
        }

        /// <summary>Lets a controller step the saturation (SV square) or hue (strip) with
        /// left/right while the pointer path remains untouched. The SV value axis is exposed as a
        /// normal slider below the square, so all three HSV components are controller-reachable.</summary>
        private void AttachNav(RawImage area, bool hue)
        {
            if (area == null)
            {
                return;
            }

            var confirmRoot = NativeCoopUi.Node(area.transform, "ControllerConfirm", 0f, 0f, 0f, 0f);
            var confirm = confirmRoot.gameObject.AddComponent<Button>();
            confirm.transition = Selectable.Transition.None;
            confirm.navigation = new Navigation { mode = Navigation.Mode.None };

            var ctrl = NativeCoopUi.NavFor(area.gameObject, confirm, null);
            if (ctrl == null)
            {
                return;
            }

            ctrl.m_CanScrollerSlide = false;
            ctrl.m_OnPressLeftEvent ??= new UnityEvent();
            ctrl.m_OnPressRightEvent ??= new UnityEvent();
            ctrl.m_OnPressLeftEvent.AddListener(() => StepNav(hue, -0.05f));
            ctrl.m_OnPressRightEvent.AddListener(() => StepNav(hue, 0.05f));
        }

        private void StepNav(bool hue, float delta)
        {
            if (hue)
            {
                _hue = Mathf.Clamp01(_hue + delta);
                RebuildSvTexture(_hue);
            }
            else
            {
                _saturation = Mathf.Clamp01(_saturation + delta);
            }

            Apply();
            _onCommit?.Invoke();
        }

        internal void SetColor(Color color)
        {
            Color.RGBToHSV(color, out _hue, out _saturation, out _value);
            RebuildSvTexture(_hue);
            UpdateMarkers();
            if (_valueSlider != null)
            {
                _valueSlider.SetValueWithoutNotify(_value);
            }
            if (_swatch != null)
            {
                _swatch.color = color;
            }
            if (_hex != null)
            {
                NativeCoopUi.Set(_hex, "#" + ColorUtility.ToHtmlStringRGBA(color));
            }
        }

        private void Apply()
        {
            var color = Color.HSVToRGB(_hue, _saturation, _value);
            UpdateMarkers();
            if (_valueSlider != null)
            {
                _valueSlider.SetValueWithoutNotify(_value);
            }
            if (_swatch != null)
            {
                _swatch.color = color;
            }
            if (_hex != null)
            {
                NativeCoopUi.Set(_hex, "#" + ColorUtility.ToHtmlStringRGBA(color));
            }
            _onChanged?.Invoke(color);
        }

        private void UpdateMarkers()
        {
            if (_svMarker != null)
            {
                NativeCoopUi.TopLeft(_svMarker, _saturation * SvWidth - 4f,
                    (1f - _value) * SvHeight - 4f, 8f, 8f);
            }
            if (_hueMarker != null)
            {
                NativeCoopUi.TopLeft(_hueMarker, -2f, (1f - _hue) * SvHeight - 2f,
                    HueWidth + 4f, 4f);
            }
        }

        private static RawImage MakeRawImage(Transform parent, float x, float y, float width,
            float height)
        {
            var go = new GameObject("ColorArea", typeof(RectTransform), typeof(RawImage));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            NativeCoopUi.TopLeft(rt, x, y, width, height);
            var image = go.GetComponent<RawImage>();
            image.raycastTarget = true;
            return image;
        }

        private static RectTransform MakeMarker(RectTransform parent, float height, Color color)
        {
            var go = new GameObject("Marker", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            if (NativeCoopUi.White != null)
            {
                image.sprite = NativeCoopUi.White;
            }
            image.color = color;
            image.raycastTarget = false;
            rt.sizeDelta = new Vector2(6f, height);

            if (color == Color.white)
            {
                var outline = go.AddComponent<Outline>();
                outline.effectColor = Color.black;
                outline.effectDistance = new Vector2(1f, 1f);
            }
            return rt;
        }

        private static void EnsureTextures()
        {
            if (_svTexture == null)
            {
                _svTexture = new Texture2D(96, 64, TextureFormat.RGBA32, false, false)
                {
                    name = "CoopCharacterEditorSv",
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave,
                };
            }
            if (_hueTexture == null)
            {
                _hueTexture = new Texture2D(1, 96, TextureFormat.RGBA32, false, false)
                {
                    name = "CoopCharacterEditorHue",
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                for (var y = 0; y < 96; y++)
                {
                    _hueTexture.SetPixel(0, y, Color.HSVToRGB(y / 95f, 1f, 1f));
                }
                _hueTexture.Apply(false, false);
            }
        }

        private void RebuildSvTexture(float hue)
        {
            EnsureTextures();
            if (Mathf.Abs(_svHue - hue) > 0.0001f)
            {
                for (var y = 0; y < 64; y++)
                {
                    for (var x = 0; x < 96; x++)
                    {
                        _svTexture.SetPixel(x, y, Color.HSVToRGB(hue, x / 95f, y / 63f));
                    }
                }
                _svTexture.Apply(false, false);
                _svHue = hue;
            }

            if (_svImage != null)
            {
                _svImage.texture = _svTexture;
            }
            if (_hueImage != null)
            {
                _hueImage.texture = _hueTexture;
            }
        }

        internal static void ShutdownTextures()
        {
            if (_svTexture != null)
            {
                UnityEngine.Object.Destroy(_svTexture);
            }
            if (_hueTexture != null)
            {
                UnityEngine.Object.Destroy(_hueTexture);
            }
            _svTexture = null;
            _hueTexture = null;
            _svHue = -1f;
        }
    }
}
