using System;
using System.Collections.Generic;
using CardShopCoop.Modules.Presence;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace CardShopCoop.UI
{
    /// <summary>
    /// The native (UGUI) character editor used by the co-op screen's Character tab.
    ///
    /// <para>It renders a live in-panel preview from a <see cref="RenderTexture"/>, works without an
    /// active session (the title screen included), and edits either the presence service's model or
    /// the same persisted model directly. All controls are game/UGUI controls; no IMGUI is used.</para>
    /// </summary>
    internal sealed class CoopCharacterEditor : MonoBehaviour
    {
        private enum Page
        {
            Face,
            Body,
            Hair,
            Outfits,
            Colors,
            Presets,
        }

        private const float RootWidth = 1040f;
        private const float RootHeight = 980f;
        private const float LeftX = 40f;
        private const float LeftY = 240f;
        private const float LeftWidth = 360f;
        private const float LeftHeight = 608f;
        private const float PageX = 420f;
        private const float PageY = 240f;
        private const float ViewportY = 286f;
        private const float ViewportHeight = 560f;
        private const float ViewportWidth = 580f;
        private const float ScrollbarX = 1004f;

        private static readonly Page[] PageOrder =
        {
            Page.Face,
            Page.Body,
            Page.Hair,
            Page.Outfits,
            Page.Colors,
            Page.Presets,
        };

        private static readonly string[] PageLabels =
        {
            "Face",
            "Body",
            "Hair",
            "Outfits",
            "Colors",
            "Presets",
        };

        private static CoopCharacterEditor _current;

        private Action<string> _hint;
        private CoopCharacterPreview _preview;
        private ICharacterEditorBackend _backend;
        private bool _serviceBackend;
        private bool _built;
        private bool _subscribed;
        private bool _nsfwSelfChange;
        private bool _quittingSubscribed;
        private int _sceneHandle = int.MinValue;
        private Page _page = Page.Face;
        private readonly Dictionary<Page, float> _scrollPositions = new();
        private string _expandedColor;
        private string _lastPreviewSignature;
        private string _sessionKey;
        private RectTransform _root;
        private RectTransform _viewport;
        private RectTransform _content;
        private ScrollRect _scroll;
        private RawImage _previewImage;
        private TextMeshProUGUI _modelText;
        private TextMeshProUGUI _statusText;
        private TextMeshProUGUI _hintText;
        private CoopButton _genderButton;
        private CoopButton _nsfwButton;
        private CoopButton _undoButton;
        private CoopButton _redoButton;
        private readonly CoopButton[] _pageButtons = new CoopButton[PageOrder.Length];

        /// <summary>Builds the editor inside <paramref name="parent"/>. The returned object is the
        /// editor group; it can be hidden/shown with the Character tab.</summary>
        internal static GameObject Build(Transform parent, Action<string> hint)
        {
            if (_current != null)
            {
                _current._hint = hint;
                if (_current.isActiveAndEnabled)
                {
                    _current.RefreshAll();
                }
                return _current.gameObject;
            }

            var root = NativeCoopUi.Node(parent, "CharacterEditor", 0f, 0f, RootWidth, RootHeight);
            var backgroundRect = NativeCoopUi.Node(root, "CharacterEditorBackground", 0f, 172f,
                RootWidth, 710f);
            var background = backgroundRect.gameObject.AddComponent<Image>();
            if (NativeCoopUi.White != null)
            {
                background.sprite = NativeCoopUi.White;
            }
            background.color = NativeCoopUi.PanelFill;
            background.raycastTarget = true;

            var editor = root.gameObject.AddComponent<CoopCharacterEditor>();
            _current = editor;
            editor.Initialize(root, hint);
            return root.gameObject;
        }

        internal static void Refresh()
        {
            if (_current != null && _current.isActiveAndEnabled)
            {
                _current.RefreshAll();
            }
        }

        internal static void Shutdown()
        {
            if (_current != null)
            {
                _current.Save();
            }
            CoopCharacterEditorWidgets.ShutdownTextures();
        }

        private void Initialize(RectTransform root, Action<string> hint)
        {
            _root = root;
            _hint = hint;
            _preview = new CoopCharacterPreview();

            NativeCoopUi.Text(root, "Character & 3D Avatar Customization", 28f, 40f, 180f, 760f,
                34f, NativeCoopUi.Gold, TextAlignmentOptions.Left, wrap: false, title: true);
            _statusText = NativeCoopUi.Text(root, "", 16f, 40f, 214f, 960f, 22f, NativeCoopUi.Dim,
                TextAlignmentOptions.Left, wrap: false, title: false);

            BuildPreviewPanel();
            BuildPageTabs();
            BuildViewport();
            BuildScrollbar();

            _hintText = NativeCoopUi.Text(root, "Changes are saved for your co-op character.", 16f,
                40f, 854f, 960f, 36f, NativeCoopUi.Dim, TextAlignmentOptions.TopLeft, wrap: true,
                title: false);

            _built = true;
            BuildPage();
            if (isActiveAndEnabled)
            {
                OnEnable();
            }
        }

        private void BuildPreviewPanel()
        {
            var panel = NativeCoopUi.Node(_root, "PreviewPanel", LeftX, LeftY, LeftWidth,
                LeftHeight);
            NativeCoopUi.Panel(panel, 0f, 0f, LeftWidth, LeftHeight, NativeCoopUi.WellFill, 2.4f);
            NativeCoopUi.Text(panel, "LIVE PREVIEW", 20f, 16f, 12f, 328f, 26f, NativeCoopUi.Gold,
                TextAlignmentOptions.Left, wrap: false, title: true);

            var previewObject = new GameObject("PreviewImage", typeof(RectTransform),
                typeof(RawImage));
            var previewRect = (RectTransform)previewObject.transform;
            previewRect.SetParent(panel, false);
            NativeCoopUi.TopLeft(previewRect, 16f, 42f, 328f, 430f);
            _previewImage = previewObject.GetComponent<RawImage>();
            _previewImage.raycastTarget = false;
            _previewImage.color = Color.white;

            _modelText = NativeCoopUi.Text(panel, "Model -", 16f, 16f, 478f, 328f, 22f,
                NativeCoopUi.Dim, TextAlignmentOptions.Left, wrap: false, title: false);

            _genderButton = NativeCoopUi.Button(panel, "Gender: Female", NativeCoopUi.Purple,
                OnToggleGender, 16f, 504f, 156f, 38f, 18f);
            _nsfwButton = NativeCoopUi.Button(panel, "NSFW: OFF", NativeCoopUi.Orange,
                OnToggleNsfw, 188f, 504f, 156f, 38f, 18f);
            _undoButton = NativeCoopUi.Button(panel, "Undo", NativeCoopUi.Blue, OnUndo, 16f,
                548f, 156f, 34f, 18f);
            _redoButton = NativeCoopUi.Button(panel, "Redo", NativeCoopUi.Blue, OnRedo, 188f,
                548f, 156f, 34f, 18f);
        }

        private void BuildPageTabs()
        {
            for (var i = 0; i < PageOrder.Length; i++)
            {
                var page = PageOrder[i];
                _pageButtons[i] = NativeCoopUi.Button(_root, PageLabels[i], NativeCoopUi.Grey,
                    () => ShowPage(page), 425f + i * 95f, PageY, 90f, 38f, 18f);
            }
        }

        private void BuildViewport()
        {
            var viewport = NativeCoopUi.Node(_root, "Viewport", PageX, ViewportY, ViewportWidth,
                ViewportHeight);
            _viewport = viewport;
            var hitArea = viewport.gameObject.AddComponent<Image>();
            if (NativeCoopUi.White != null)
            {
                hitArea.sprite = NativeCoopUi.White;
            }
            hitArea.color = new Color(0f, 0f, 0f, 0.001f);
            hitArea.raycastTarget = true;
            viewport.gameObject.AddComponent<RectMask2D>();

            _scroll = viewport.gameObject.AddComponent<ScrollRect>();
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 30f;
            _scroll.inertia = true;
            _scroll.decelerationRate = 0.135f;
            _scroll.viewport = viewport;

            _content = CreateContent();
        }

        private void BuildScrollbar()
        {
            var bar = NativeCoopUi.Node(_root, "Scrollbar", ScrollbarX, ViewportY, 12f,
                ViewportHeight);
            var track = bar.gameObject.AddComponent<Image>();
            if (NativeCoopUi.White != null)
            {
                track.sprite = NativeCoopUi.White;
            }
            track.color = new Color(0.14f, 0.09f, 0.18f);
            track.raycastTarget = true;

            var sliding = NativeCoopUi.Node(bar, "SlidingArea", 0f, 0f, 12f, ViewportHeight);
            var handle = NativeCoopUi.Node(sliding, "Handle", 0f, 0f, 12f, ViewportHeight);
            var handleImage = handle.gameObject.AddComponent<Image>();
            if (NativeCoopUi.White != null)
            {
                handleImage.sprite = NativeCoopUi.White;
            }
            handleImage.color = new Color(0.62f, 0.38f, 0.96f);
            handleImage.raycastTarget = true;

            var scrollbar = bar.gameObject.AddComponent<Scrollbar>();
            scrollbar.handleRect = handle;
            scrollbar.targetGraphic = handleImage;
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            scrollbar.size = 1f;
            scrollbar.value = 1f;

            _scroll.verticalScrollbar = scrollbar;
            _scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            _scroll.verticalScrollbarSpacing = 2f;
        }

        private void OnEnable()
        {
            if (!_built)
            {
                return;
            }

            Subscribe();
            _preview?.Show(true);
            var scene = SceneManager.GetActiveScene().handle;
            if (_sceneHandle != scene)
            {
                _sceneHandle = scene;
                if (_backend != null && !_serviceBackend)
                {
                    // Re-read the persisted model after a scene change. This also upgrades a
                    // title-screen CC_Dummy preview once the game world (and its real character
                    // prefab) has loaded. Same-scene tab switches keep the undo history.
                    _backend.RefreshModel();
                }
            }
            else if (_backend != null && !_serviceBackend && _preview != null
                && _preview.Customization == null)
            {
                // A transient preview failure is retried on the next explicit tab open, not on
                // every refresh.
                _backend.RefreshModel();
            }
            RefreshAll();
        }

        private void OnDisable()
        {
            if (!_built)
            {
                return;
            }

            Save();
            _preview?.Show(false);
            Unsubscribe();
        }

        private void OnDestroy()
        {
            Save();
            Unsubscribe();
            _preview?.Destroy();
            _preview = null;
            _backend = null;
            if (ReferenceEquals(_current, this))
            {
                _current = null;
                CoopCharacterEditorWidgets.ShutdownTextures();
            }
        }

        private void Subscribe()
        {
            if (_subscribed)
            {
                return;
            }

            _subscribed = true;
            CoopCore.StateChanged += OnSessionChanged;
            if (CoopPlugin.AllowNsfw != null)
            {
                CoopPlugin.AllowNsfw.SettingChanged += OnNsfwSettingChanged;
            }
            if (!_quittingSubscribed)
            {
                _quittingSubscribed = true;
                Application.quitting += OnQuitting;
            }
        }

        private void Unsubscribe()
        {
            if (_subscribed)
            {
                _subscribed = false;
                CoopCore.StateChanged -= OnSessionChanged;
                if (CoopPlugin.AllowNsfw != null)
                {
                    CoopPlugin.AllowNsfw.SettingChanged -= OnNsfwSettingChanged;
                }
            }

            if (_quittingSubscribed)
            {
                _quittingSubscribed = false;
                Application.quitting -= OnQuitting;
            }
        }

        private void OnSessionChanged()
        {
            if (!_built || !isActiveAndEnabled)
            {
                return;
            }

            var key = PresenceLocalEditorApi.HasActiveService + ":" + PresenceApi.EditorGeneration
                + ":" + PresenceApi.PlayerModelGeneration + ":" + CoopPlugin.AllowNsfw.Value;
            if (_sessionKey == key)
            {
                return;
            }
            _sessionKey = key;
            RefreshAll();
        }

        private void OnNsfwSettingChanged(object sender, EventArgs args)
        {
            if (!_built || !isActiveAndEnabled || _nsfwSelfChange)
            {
                return;
            }

            if (_backend != null && !_serviceBackend)
            {
                // A setting changed from another screen (the legacy settings tab) must also
                // re-apply the local model's nude guard, not just repaint the option list.
                _backend.RefreshModel();
            }
            RefreshAll();
        }

        private void OnQuitting()
        {
            Save();
        }

        private void Save()
        {
            _backend?.Save();
        }

        private void RefreshAll()
        {
            if (!_built)
            {
                return;
            }

            // The stage must be active before a body is created: CharacterCustomization.Initialize
            // collects active SkinnedMeshRenderers and the modal/game pause state does not affect
            // the preview camera.
            _preview?.Show(true);
            var serviceActive = PresenceLocalEditorApi.HasActiveService;
            _sessionKey = serviceActive + ":" + PresenceApi.EditorGeneration + ":"
                + PresenceApi.PlayerModelGeneration + ":" + CoopPlugin.AllowNsfw.Value;
            if (_backend == null || serviceActive != _serviceBackend)
            {
                Save();
                _serviceBackend = serviceActive;
                _backend = serviceActive
                    ? new ServiceCharacterEditorBackend()
                    : new LocalCharacterEditorBackend(_preview);
                _backend.RefreshModel();
                _lastPreviewSignature = null;
            }
            else if (_serviceBackend)
            {
                _backend.RefreshModel();
            }

            var model = _backend.Model;
            if (_serviceBackend && model != null)
            {
                var signature = (model.Female ? "F" : "M") + ":" + model.ModelIndex + ":"
                    + CoopPlugin.AllowNsfw.Value + ":" + model.CustomizationJson;
                if (signature != _lastPreviewSignature)
                {
                    _lastPreviewSignature = signature;
                    _preview.SetModel(model);
                }
            }

            if (_previewImage != null)
            {
                _previewImage.texture = _preview.Texture;
            }

            var available = _backend.Available;
            var editable = _backend.Editable;
            var female = model?.Female ?? false;
            var modelIndex = model?.ModelIndex ?? 0;

            if (_genderButton != null)
            {
                _genderButton.SetLabel(female ? "Gender: Female" : "Gender: Male");
                _genderButton.SetStyle(female ? NativeCoopUi.Purple : NativeCoopUi.Blue);
                _genderButton.SetInteractable(editable);
            }
            if (_nsfwButton != null)
            {
                var allowed = CoopPlugin.AllowNsfw.Value;
                _nsfwButton.SetLabel(allowed ? "NSFW: ON" : "NSFW: OFF");
                _nsfwButton.SetStyle(allowed ? NativeCoopUi.Green : NativeCoopUi.Orange);
                _nsfwButton.SetInteractable(available);
            }
            _undoButton?.SetInteractable(editable && _backend.CanUndo);
            _redoButton?.SetInteractable(editable && _backend.CanRedo);

            if (_modelText != null)
            {
                _modelText.text = "Model: " + (female ? "Female" : "Male") + " "
                    + (modelIndex + 1) + (editable ? "" : "  (preview only)");
            }

            if (_statusText != null)
            {
                if (!available)
                {
                    _statusText.color = NativeCoopUi.Bad;
                    _statusText.text = "Character editor unavailable: no preview body could be "
                        + "built on this screen.";
                }
                else if (!editable)
                {
                    _statusText.color = NativeCoopUi.Gold;
                    _statusText.text = "Limited wardrobe data: your saved character is safe. Open "
                        + "this tab in a loaded game once to unlock every option.";
                }
                else if (_preview.Limited)
                {
                    _statusText.color = NativeCoopUi.Gold;
                    _statusText.text = "Limited preview body; the saved character is untouched.";
                }
                else
                {
                    _statusText.color = NativeCoopUi.Dim;
                    _statusText.text = serviceActive
                        ? "Editing your co-op character (session sync is live)."
                        : "Editing your co-op character (works without a session).";
                }
            }

            RefreshPageTabs();
            if (_scroll != null)
            {
                _scrollPositions[_page] = _scroll.verticalNormalizedPosition;
            }
            BuildPage();
        }

        private void RefreshPageTabs()
        {
            for (var i = 0; i < _pageButtons.Length; i++)
            {
                _pageButtons[i]?.SetStyle(PageOrder[i] == _page
                    ? NativeCoopUi.Purple : NativeCoopUi.Grey);
            }
        }

        private void ShowPage(Page page)
        {
            if (_page == page)
            {
                return;
            }

            Save();
            if (_scroll != null)
            {
                _scrollPositions[_page] = _scroll.verticalNormalizedPosition;
            }
            _page = page;
            _expandedColor = null;
            RefreshPageTabs();
            BuildPage();
        }

        private void ToggleColor(string key)
        {
            Save();
            if (_scroll != null)
            {
                _scrollPositions[_page] = _scroll.verticalNormalizedPosition;
            }
            _expandedColor = _expandedColor == key ? null : key;
            BuildPage();
        }

        private void RebuildPage()
        {
            if (_scroll != null)
            {
                _scrollPositions[_page] = _scroll.verticalNormalizedPosition;
            }
            BuildPage();
        }

        private RectTransform CreateContent()
        {
            var content = (RectTransform)new GameObject("Content", typeof(RectTransform)).transform;
            content.SetParent(_viewport, false);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;
            return content;
        }

        private void BuildPage()
        {
            if (!_built || _viewport == null || _scroll == null)
            {
                return;
            }

            if (_content != null)
            {
                _content.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(_content.gameObject);
            }
            _content = CreateContent();
            _scroll.content = _content;

            var y = 0f;
            if (_backend == null)
            {
                CoopCharacterEditorWidgets.Section(_content, ref y, "CHARACTER EDITOR");
                CoopCharacterEditorWidgets.Note(_content, ref y,
                    "Preparing the character editor...", 30f);
            }
            else if (!_backend.Available)
            {
                CoopCharacterEditorWidgets.Section(_content, ref y, "CHARACTER EDITOR");
                CoopCharacterEditorWidgets.Note(_content, ref y,
                    "A preview body could not be built on this screen. Start or load a game once, "
                    + "then reopen this tab. Your saved character has not been changed.", 76f);
            }
            else if (!_backend.Editable)
            {
                CoopCharacterEditorWidgets.Section(_content, ref y, "LIMITED PREVIEW");
                CoopCharacterEditorWidgets.Note(_content, ref y,
                    "This screen has only the game's limited character dummy loaded, so the full "
                    + "wardrobe cannot be read or written here. The saved character is untouched. "
                    + "Open this tab once in a loaded game; the full editor stays available at the "
                    + "title screen afterward.", 96f);
            }
            else
            {
                switch (_page)
                {
                    case Page.Face:
                        BuildFacePage(ref y);
                        break;
                    case Page.Body:
                        BuildBodyPage(ref y);
                        break;
                    case Page.Hair:
                        BuildHairPage(ref y);
                        break;
                    case Page.Outfits:
                        BuildOutfitsPage(ref y);
                        break;
                    case Page.Colors:
                        BuildColorsPage(ref y);
                        break;
                    case Page.Presets:
                        BuildPresetsPage(ref y);
                        break;
                }
            }

            _content.sizeDelta = new Vector2(0f, Mathf.Max(ViewportHeight, y + 8f));
            var normalized = 1f;
            if (_scrollPositions.TryGetValue(_page, out var saved))
            {
                normalized = saved;
            }
            _scroll.verticalNormalizedPosition = normalized;
            RefreshPageTabs();
        }

        private void BuildFacePage(ref float y)
        {
            var customization = _backend.Customization;
            var model = _backend.Model;
            var data = customization?.StoredCharacterData;
            var names = CoopCharacterEditorModel.BlendshapeNames(customization, model.Female);
            if (names.Count == 0)
            {
                CoopCharacterEditorWidgets.Section(_content, ref y, "FACE");
                CoopCharacterEditorWidgets.Note(_content, ref y,
                    "This body has no face sliders.", 30f);
                return;
            }

            AddBlendshapeGroup(ref y, names, data, "Jaw & Face", IsJawGroup);
            AddBlendshapeGroup(ref y, names, data, "Eyes", IsEyeGroup);
            AddBlendshapeGroup(ref y, names, data, "Brows", IsBrowGroup);
            AddBlendshapeGroup(ref y, names, data, "Nose", IsNoseGroup);
            AddBlendshapeGroup(ref y, names, data, "Mouth", IsMouthGroup);
            AddBlendshapeGroup(ref y, names, data, "Other", name => !IsJawGroup(name)
                && !IsEyeGroup(name) && !IsBrowGroup(name) && !IsNoseGroup(name)
                && !IsMouthGroup(name));
        }

        private void AddBlendshapeGroup(ref float y, List<string> names, CC.CC_CharacterData data,
            string label, Func<string, bool> predicate)
        {
            var group = new List<string>();
            for (var i = 0; i < names.Count; i++)
            {
                if (predicate(names[i]))
                {
                    group.Add(names[i]);
                }
            }
            if (group.Count == 0)
            {
                return;
            }

            CoopCharacterEditorWidgets.Section(_content, ref y, label.ToUpperInvariant());
            for (var i = 0; i < group.Count; i++)
            {
                var name = group[i];
                var value = CoopCharacterEditorModel.ReadFloat(data?.Blendshapes, name, 0f);
                CoopCharacterEditorWidgets.SliderRow(_content, ref y, SliderLabel(name), 0f, 1f,
                    value, next => OnBlendshape(name, next), Save);
            }
        }

        private void BuildBodyPage(ref float y)
        {
            var customization = _backend.Customization;
            var model = _backend.Model;
            var data = customization?.StoredCharacterData;
            var names = CoopCharacterEditorModel.FloatNames(customization, model.Female);
            if (names.Count == 0)
            {
                CoopCharacterEditorWidgets.Section(_content, ref y, "BODY");
                CoopCharacterEditorWidgets.Note(_content, ref y,
                    "This body has no body sliders.", 30f);
                return;
            }

            CoopCharacterEditorWidgets.Section(_content, ref y, "BODY");
            for (var i = 0; i < names.Count; i++)
            {
                var name = names[i];
                var isScale = name == "Height" || name == "Width";
                var value = CoopCharacterEditorModel.ReadFloat(data?.FloatProperties, name,
                    isScale ? 1f : 0f);
                var min = isScale ? 0.5f : 0f;
                var max = isScale ? 1.5f : 1f;
                CoopCharacterEditorWidgets.SliderRow(_content, ref y,
                    CoopCharacterEditorModel.Prettify(name, name), min, max, value,
                    next => OnFloat(name, next), Save);
            }

            CoopCharacterEditorWidgets.Note(_content, ref y,
                "Height and width adjust the body scale. Other body values are 0 to 1.", 44f);
        }

        private void BuildHairPage(ref float y)
        {
            var customization = _backend.Customization;
            var model = _backend.Model;
            var data = customization?.StoredCharacterData;
            var slots = PresenceLocalEditorApi.DressableHairSlots(customization);
            if (slots == 0)
            {
                CoopCharacterEditorWidgets.Section(_content, ref y, "HAIR");
                CoopCharacterEditorWidgets.Note(_content, ref y,
                    "This body has no hair slots.", 30f);
                return;
            }

            CoopCharacterEditorWidgets.Section(_content, ref y, "HAIR & BROWS");
            for (var slot = 0; slot < slots; slot++)
            {
                List<string> display;
                List<string> names;
                CoopCharacterEditorModel.HairChoices(customization, slot, out display, out names);
                var current = CoopCharacterEditorModel.HairName(data, slot);
                var selected = CoopCharacterEditorModel.FindChoice(names, current, emptyIsFirst: true);
                selected = Mathf.Clamp(selected, 0, Mathf.Max(0, names.Count - 1));
                var color = CoopCharacterEditorModel.HairColor(data, slot);
                var key = "hair:" + slot;
                var currentSlot = slot;
                var currentNames = names;
                Image swatch = null;
                swatch = CoopCharacterEditorWidgets.CycleRow(_content, ref y,
                    CoopCharacterEditorModel.HairLabel(customization, slot), display, selected,
                    index => OnHairSelected(currentSlot, currentNames, index), showColor: true,
                    color, () => ToggleColor(key));
                if (_expandedColor == key)
                {
                    var swatchImage = swatch;
                    CoopCharacterEditorWidgets.ColorPicker(_content, ref y, color,
                        next => OnHairColor(currentSlot, next, swatchImage), Save);
                }
            }

            CoopCharacterEditorWidgets.Note(_content, ref y,
                "Choose a style, then use the colored swatch to change its tint.", 30f);
        }

        private void BuildOutfitsPage(ref float y)
        {
            var customization = _backend.Customization;
            var model = _backend.Model;
            var data = customization?.StoredCharacterData;
            var slots = PresenceLocalEditorApi.DressableApparelSlots(customization);
            if (slots == 0)
            {
                CoopCharacterEditorWidgets.Section(_content, ref y, "OUTFITS");
                CoopCharacterEditorWidgets.Note(_content, ref y,
                    "This body has no outfit slots.", 30f);
                return;
            }

            CoopCharacterEditorWidgets.Section(_content, ref y, "OUTFITS");
            for (var slot = 0; slot < slots; slot++)
            {
                List<string> display;
                List<string> choices;
                CoopCharacterEditorModel.ApparelChoices(customization, slot, out display,
                    out choices);
                if (display.Count == 0)
                {
                    CoopCharacterEditorWidgets.PlainLabel(_content, ref y,
                        CoopCharacterEditorModel.ApparelLabel(customization, slot)
                        + ": no items available.", 28f);
                    continue;
                }

                var currentName = CoopCharacterEditorModel.ApparelName(data, slot);
                var selected = Mathf.Max(0, CoopCharacterEditorModel.FindChoice(choices,
                    currentName, emptyIsFirst: CoopPlugin.AllowNsfw.Value));
                selected = Mathf.Clamp(selected, 0, choices.Count - 1);
                var material = CoopCharacterEditorModel.ApparelMaterial(data, slot);
                var color = CoopCharacterEditorModel.ApparelTint(data, slot);
                var key = "apparel:" + slot;
                var currentSlot = slot;
                var currentChoices = choices;
                var swatchImage = CoopCharacterEditorWidgets.CycleRow(_content, ref y,
                    CoopCharacterEditorModel.ApparelLabel(customization, slot), display, selected,
                    index => OnApparelSelected(currentSlot, currentChoices, index), showColor: true,
                    color, () => ToggleColor(key));
                if (_expandedColor == key)
                {
                    CoopCharacterEditorWidgets.ColorPicker(_content, ref y, color,
                        next => OnApparelTint(currentSlot, next, swatchImage), Save);
                }

                var selectedName = choices[selected];
                var materialCount = CoopCharacterEditorModel.MaterialCount(customization, slot,
                    selectedName);
                if (materialCount > 1 && !string.IsNullOrEmpty(selectedName))
                {
                    selected = Mathf.Clamp(material, 0, materialCount - 1);
                    var materials = new List<string>();
                    for (var i = 0; i < materialCount; i++)
                    {
                        materials.Add((i + 1).ToString());
                    }
                    var currentMaterialName = selectedName;
                    var currentMaterialSlot = slot;
                    CoopCharacterEditorWidgets.CycleRow(_content, ref y, "Material", materials,
                        selected, index => OnMaterialSelected(currentMaterialSlot,
                            currentMaterialName, index), showColor: false, color: Color.white,
                        onColor: null);
                }
            }

            CoopCharacterEditorWidgets.Note(_content, ref y,
                "The Nude option appears only while NSFW appearances are allowed.", 30f);
        }

        private void BuildColorsPage(ref float y)
        {
            var customization = _backend.Customization;
            var model = _backend.Model;
            var data = customization?.StoredCharacterData;
            var names = CoopCharacterEditorModel.ColorNames(customization, model.Female);
            var hasSkinTone = names.Contains(CoopCharacterEditorModel.SkinTintProperty)
                || CoopCharacterEditorModel.FindProperty(data?.ColorProperties,
                    CoopCharacterEditorModel.SkinTintProperty) != null;

            if (names.Count == 0 && !hasSkinTone)
            {
                CoopCharacterEditorWidgets.Section(_content, ref y, "COLORS");
                CoopCharacterEditorWidgets.Note(_content, ref y,
                    "This body has no color options.", 30f);
                return;
            }

            CoopCharacterEditorWidgets.Section(_content, ref y, "SKIN & FACE COLORS");
            if (!hasSkinTone)
            {
                names.Add(CoopCharacterEditorModel.SkinTintProperty);
            }

            for (var i = 0; i < names.Count; i++)
            {
                var name = names[i];
                var color = CoopCharacterEditorModel.ReadColor(data?.ColorProperties, name,
                    Color.white);
                var key = "color:" + name;
                var swatch = CoopCharacterEditorWidgets.ColorRow(_content, ref y,
                    CoopCharacterEditorModel.ColorLabel(name), color, () => ToggleColor(key));
                if (_expandedColor == key)
                {
                    var currentName = name;
                    var swatchImage = swatch;
                    CoopCharacterEditorWidgets.ColorPicker(_content, ref y, color,
                        next => OnColor(currentName, next, swatchImage), Save);
                }
            }

            var skinTones = CoopCharacterEditorModel.SkinToneSwatches(customization, model.Female);
            if (skinTones.Count > 0)
            {
                CoopCharacterEditorWidgets.Section(_content, ref y, "SKIN TONES");
                var row = NativeCoopUi.Node(_content, "SkinTones", 0f, y,
                    CoopCharacterEditorWidgets.ContentWidth, 40f);
                var count = Mathf.Min(12, skinTones.Count);
                for (var i = 0; i < count; i++)
                {
                    var color = skinTones[i];
                    CoopCharacterEditorWidgets.Swatch(row, i * 42f, 4f, 34f, color,
                        () => OnSkinTone(color));
                }
                y += 46f;
            }

            var textures = CoopCharacterEditorModel.TextureValues(customization, model.Female,
                CoopCharacterEditorModel.SkinTextureProperty);
            var usableTextures = new List<string>();
            for (var i = 0; i < textures.Count; i++)
            {
                if (Resources.Load<Texture2D>(textures[i]) != null)
                {
                    usableTextures.Add(textures[i]);
                }
            }
            if (usableTextures.Count > 1)
            {
                CoopCharacterEditorWidgets.Section(_content, ref y, "SKIN TEXTURE");
                var currentTexture = CoopCharacterEditorModel.ReadTexture(data?.TextureProperties,
                    CoopCharacterEditorModel.SkinTextureProperty);
                var selected = usableTextures.IndexOf(currentTexture);
                if (selected < 0)
                {
                    selected = 0;
                }
                var display = new List<string>();
                for (var i = 0; i < usableTextures.Count; i++)
                {
                    display.Add(CoopCharacterEditorModel.Prettify(usableTextures[i],
                        usableTextures[i]));
                }
                var values = usableTextures;
                CoopCharacterEditorWidgets.CycleRow(_content, ref y, "Skin texture", display,
                    selected, index => OnTexture(CoopCharacterEditorModel.SkinTextureProperty,
                        values[index]), showColor: false, color: Color.white, onColor: null);
            }

            CoopCharacterEditorWidgets.Note(_content, ref y,
                "Skin tone, eye color, and lip color are part of the game's own character data.",
                44f);
        }

        private void BuildPresetsPage(ref float y)
        {
            var names = _backend.PresetNames;
            CoopCharacterEditorWidgets.Section(_content, ref y, "BASE MODELS");
            if (names.Count == 0)
            {
                CoopCharacterEditorWidgets.Note(_content, ref y,
                    "No base models were found for this body.", 30f);
                return;
            }

            var model = _backend.Model;
            var currentIndex = model?.ModelIndex ?? 0;
            var x = 0f;
            var column = 0;
            for (var i = 0; i < names.Count; i++)
            {
                var name = names[i];
                var index = CoopCharacterEditorModel.ParseModelIndex(name);
                var style = index == currentIndex ? NativeCoopUi.Green : NativeCoopUi.Blue;
                NativeCoopUi.Button(_content, name, style, () => OnApplyPreset(name), x, y, 128f,
                    38f, 17f);
                column++;
                if (column == 4)
                {
                    column = 0;
                    x = 0f;
                    y += 44f;
                }
                else
                {
                    x += 134f;
                }
            }
            if (column != 0)
            {
                y += 44f;
            }

            CoopCharacterEditorWidgets.Note(_content, ref y,
                "Applying a base model replaces the full look. Use Undo if you change your mind.",
                44f);
        }

        private void OnToggleGender()
        {
            if (_backend == null || !_backend.Editable)
            {
                return;
            }

            var female = _backend.Model?.Female == true;
            _backend.SetGender(!female);
            _backend.Save();
            RefreshAll();
            Hint("Avatar model: " + (!female ? "Female" : "Male"));
        }

        private void OnToggleNsfw()
        {
            if (_backend == null || !_backend.Available)
            {
                return;
            }

            var allowed = !CoopPlugin.AllowNsfw.Value;
            _nsfwSelfChange = true;
            try
            {
                _backend.SetNsfw(allowed);
            }
            finally
            {
                _nsfwSelfChange = false;
            }
            _backend.Save();
            RefreshAll();
            Hint("NSFW appearances: " + (allowed ? "ON" : "OFF"));
        }

        private void OnUndo()
        {
            if (_backend == null || !_backend.Editable)
            {
                return;
            }

            if (_backend.Undo())
            {
                _backend.Save();
                RefreshAll();
                Hint("Change undone.");
            }
            else
            {
                Hint("Nothing to undo.");
            }
        }

        private void OnRedo()
        {
            if (_backend == null || !_backend.Editable)
            {
                return;
            }

            if (_backend.Redo())
            {
                _backend.Save();
                RefreshAll();
                Hint("Change redone.");
            }
            else
            {
                Hint("Nothing to redo.");
            }
        }

        private void OnApplyPreset(string name)
        {
            if (_backend == null || !_backend.Editable)
            {
                return;
            }

            _backend.ApplyPreset(name);
            _backend.Save();
            RefreshAll();
            Hint("Applied preset: " + name);
        }

        private void OnBlendshape(string name, float value)
        {
            if (_backend == null || !_backend.Editable)
            {
                return;
            }

            _backend.SetBlendshape(name, value);
            if (!ReferenceEquals(_backend.Customization, _preview.Customization))
            {
                _preview.ApplyLiveBlendshape(name, value);
            }
        }

        private void OnFloat(string name, float value)
        {
            if (_backend == null || !_backend.Editable)
            {
                return;
            }

            _backend.SetFloat(name, value);
            if (!ReferenceEquals(_backend.Customization, _preview.Customization))
            {
                _preview.ApplyLiveFloat(name, value);
            }
        }

        private void OnColor(string name, Color color, Image swatch)
        {
            if (_backend == null || !_backend.Editable)
            {
                return;
            }

            _backend.SetColor(name, color);
            if (!ReferenceEquals(_backend.Customization, _preview.Customization))
            {
                _preview.ApplyLiveColor(name, color);
            }
            if (swatch != null)
            {
                swatch.color = color;
            }
        }

        private void OnTexture(string name, string value)
        {
            if (_backend == null || !_backend.Editable)
            {
                return;
            }

            _backend.SetTexture(name, value);
            if (!ReferenceEquals(_backend.Customization, _preview.Customization))
            {
                _preview.ApplyLiveTexture(name, value);
            }
            _backend.Save();
            RebuildPage();
        }

        private void OnHairSelected(int slot, List<string> names, int index)
        {
            if (_backend == null || !_backend.Editable || index < 0 || index >= names.Count)
            {
                return;
            }

            _backend.SetHair(slot, names[index]);
            _backend.Save();
            RefreshAll();
        }

        private void OnHairColor(int slot, Color color, Image swatch)
        {
            if (_backend == null || !_backend.Editable)
            {
                return;
            }

            _backend.SetHairColor(slot, color);
            if (!ReferenceEquals(_backend.Customization, _preview.Customization))
            {
                _preview.ApplyLiveHairColor(slot, color);
            }
            if (swatch != null)
            {
                swatch.color = color;
            }
        }

        private void OnApparelSelected(int slot, List<string> names, int index)
        {
            if (_backend == null || !_backend.Editable || index < 0 || index >= names.Count)
            {
                return;
            }

            var model = _backend.Model;
            var material = model == null ? 0 : CoopCharacterEditorModel.ApparelMaterial(
                _backend.Customization?.StoredCharacterData, slot);
            _backend.SetApparel(slot, names[index], material);
            _backend.Save();
            RefreshAll();
        }

        private void OnMaterialSelected(int slot, string name, int material)
        {
            if (_backend == null || !_backend.Editable)
            {
                return;
            }

            _backend.SetApparel(slot, name, material);
            _backend.Save();
            RefreshAll();
        }

        private void OnApparelTint(int slot, Color color, Image swatch)
        {
            if (_backend == null || !_backend.Editable)
            {
                return;
            }

            _backend.SetApparelTint(slot, color);
            if (!ReferenceEquals(_backend.Customization, _preview.Customization))
            {
                _preview.ApplyLiveApparelTint(slot, color);
            }
            if (swatch != null)
            {
                swatch.color = color;
            }
        }

        private void OnSkinTone(Color color)
        {
            if (_backend == null || !_backend.Editable)
            {
                return;
            }

            _backend.SetColor(CoopCharacterEditorModel.SkinTintProperty, color);
            if (!ReferenceEquals(_backend.Customization, _preview.Customization))
            {
                _preview.ApplyLiveColor(CoopCharacterEditorModel.SkinTintProperty, color);
            }
            _backend.Save();
            RebuildPage();
            Hint("Skin tone updated.");
        }

        private void Hint(string text)
        {
            if (_hintText != null)
            {
                _hintText.text = text ?? "";
            }
            _hint?.Invoke(text);
        }

        private static bool IsJawGroup(string name)
        {
            return Has(name, "jaw") || Has(name, "chin") || Has(name, "underbite")
                || Has(name, "face");
        }

        private static bool IsEyeGroup(string name)
        {
            return Has(name, "eye");
        }

        private static bool IsBrowGroup(string name)
        {
            return Has(name, "brow");
        }

        private static bool IsNoseGroup(string name)
        {
            return Has(name, "nose");
        }

        private static bool IsMouthGroup(string name)
        {
            return Has(name, "mouth") || Has(name, "lip");
        }

        private static bool Has(string name, string part)
        {
            return name != null && name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string SliderLabel(string name)
        {
            if (name == null)
            {
                return "";
            }

            if (name.StartsWith("mod_", StringComparison.Ordinal))
            {
                return CoopCharacterEditorModel.Prettify(name.Substring(4), name);
            }
            if (name.StartsWith("shp_", StringComparison.Ordinal))
            {
                return "Shape " + CoopCharacterEditorModel.Prettify(name.Substring(4), name);
            }
            return CoopCharacterEditorModel.Prettify(name, name);
        }
    }
}
