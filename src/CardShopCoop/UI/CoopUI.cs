using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BepInEx.Configuration;
using CardShopCoop.Net;
using CardShopCoop.Net.Networking;
using CardShopCoop.Modules.Catalog;
using CardShopCoop.Modules.Grading;
using CardShopCoop.Modules.Hud;
using CardShopCoop.Modules.Presence;
using CardShopCoop.Modules.Shop;
using CardShopCoop.Runtime;
// NO `using Steamworks;` HERE, AND NEVER AGAIN - see the same note in CoopCore.cs. OnGUI
// is the worst possible place for a type-load fault (DrawNone runs every frame on the main
// menu), so nothing Steam-typed may appear in this file - a lobby id included.
using UnityEngine;
using CardShopCoop.Api;

namespace CardShopCoop.UI
{
    /// <summary>Small IMGUI window: host / join / status. Toggled with the UI key.
    /// Presentation is driven by <see cref="CoopTheme"/> (cozy card-shop paper + teal).</summary>
    public class CoopUI
    {
        public bool Visible = true;
        public static bool TextFieldFocused;

        private Rect _win = new(24f, 96f, 400f, 10f);
        private int _fpsShown = -1;
        private string _versionWithFps;
        private string _nameField;
        private readonly Dictionary<string, FieldValues> _fieldValues = new();

        // Delayed hover tooltips: which control the pointer is resting on, since when, and the box
        // the last qualifying hover parked for the end of the window.
        private const float TooltipDelaySeconds = 3f;

        // Glyph buttons: one character in a square button the same height as every other button in
        // the panel (ButtonSecondary fixes that height), so a row of field actions stays compact.
        private const float IconButtonWidth = 26f;

        // The single Start/Stop control in a container header: a square glyph button, sized to sit in
        // line with the button height the theme fixes for every other control.
        private const float HeaderIconWidth = 30f;
        private string _tipKey;
        private float _tipSince;
        private string _pendingTipText;
        private Vector2 _pendingTipAt;
        private readonly HashSet<string> _expanded = new();
        private readonly Dictionary<string, BackendAvailability> _availability = new();
        private readonly Dictionary<string, Task<BackendAvailability>> _availabilityTasks = new();
        private readonly HashSet<string> _autoExpanded = new();
        private float _availabilityPolledAt;
        private bool _characterOpen = true;
        private enum CoopTab
        {
            Session,
            Character,
            Settings,
            Hidden,
        }
        private CoopTab _tab = CoopTab.Session;
        private Vector2 _sessionScroll;
        private Vector2 _characterScroll;
        private Vector2 _settingsScroll;
        private Vector2 _hiddenScroll;
        private bool _characterSnapshotReady;
        private bool _characterFemale;
        private readonly List<string> _presetNames = new();
        private int _presetSelection;
        private readonly List<int> _presetSelectionBuffer = new(1) { 0 };
        private int _characterSnapshotGeneration = -1;
        private CC.CharacterCustomization _characterCustomizer;
        private readonly List<List<string>> _hairOptions = new();
        private readonly List<int> _hairSelections = new();
        private readonly List<Color> _hairColors = new();
        private readonly List<List<string>> _apparelOptions = new();
        private readonly List<List<string>> _apparelNames = new();
        private readonly List<int> _apparelSelections = new();
        private readonly List<int> _apparelMaterials = new();
        private readonly List<Color> _apparelColors = new();
        private int _colorEditorKind = -1;
        private int _colorEditorSlot = -1;
        private Texture2D _svPickerTexture;
        private Texture2D _huePickerTexture;
        private float _pickerTextureHue = -1f;
        private int _colorDragTarget;
        private readonly List<string> _floatNames = new();
        private readonly List<float> _floatValues = new();

        // LATCHED COPIES OF THE INVITE FIELDS, and the reason they exist is IMGUI's two-pass
        // model rather than anything about threads. GUILayout matches the Layout pass against
        // the Repaint pass BY CONTROL INDEX: if a field changes value between the two passes
        // and that value decides whether a row is drawn, the counts disagree and Unity throws
        // "GUILayout: Mismatched LayoutGroup" over the whole window. CoopCore now publishes
        // these on the main thread, which makes a mid-frame change unlikely rather than
        // impossible (a queued publish drains in Update, and OnGUI can run more than once per
        // Update). Latching in Layout only - the same trick _lanIps already uses - makes both
        // passes draw from one immutable snapshot, so the question cannot arise.
        // The networking tab probes each backend's availability at most every couple of seconds and
        // caches the answer - never once per frame (see DrawBackendGroup).
        private const float AvailabilityRefreshSeconds = 2f;
        private const float MaxWindowHeight = 720f;
        // Keep the complete window clear of the screen edges so a resize cannot push the
        // footer off-screen. The window's own height is set from this, not from its children.
        private const float WindowScreenMargin = 16f;
        // The scroll viewport measured on the last Repaint. WindowFn seeds the shared min-height
        // wrapper from it so the session's last panel can fill the tab, without guessing the
        // header/tabs/footer size.
        private float _viewportHeight;
        /// <summary>The width the scroll content was actually given, measured on the previous
        /// Repaint. Controls whose text can be long must be given an explicit width from THIS, not
        /// from their content, or they widen the whole panel.</summary>
        private float _viewportWidth = 240f;

        // OnGUI runs 2+ times per frame; GUIStyle construction and string interpolation there
        // is steady per-frame garbage. Styles/textures live in CoopTheme (built once, cached);
        // the HUD display strings - and the GUIContent wrappers the pills measure - are rebuilt
        // only when their source line changes.
        private string _errorSeen, _errorText; private GUIContent _errorGc;
        private string _enumRestoreMsg; // outcome line under the enum-lend notice
        private string _hostTimeSeen, _hostTimeText; private GUIContent _hostTimeGc;
        private string _registerSeen, _registerText; private GUIContent _registerGc;
        private string _nextDayWaitSeen, _nextDayWaitText; private GUIContent _nextDayWaitGc;
        private string _endOfDayPendingSeen, _endOfDayPendingText;
        private GUIContent _endOfDayPendingGc;
        private KeyCode _toggleKeySeen = KeyCode.None;
        private string _toggleHint;

        // The failure-alert popup (CoopAlert). The rect is seeded when the alert first appears and
        // then tracked from GUILayout.Window so it stays where it was placed.
        private Rect _alertWin = new(0f, 0f, 460f, 10f);
        private bool _alertShown;

        public void Draw(CoopCore core, ICoopTransport net)
        {
            CoopTheme.EnsureBuilt();
            if (_nameField == null)
            {
                _nameField = core.EffectivePlayerName;
            }

            // ---- HUD overlays (outside the window) ----
            if (!Visible)
            {
                // a hidden window cannot have a focused coop_ field, but IMGUI keeps the
                // last focus NAME alive after the window stops drawing - without this
                // reset, closing the window right after typing left TextFieldFocused
                // stuck TRUE and silently ate the serve key for the rest of the session
                TextFieldFocused = false;
                if (core.ErrorLine.Length > 0)
                {
                    if (core.ErrorLine != _errorSeen)
                    {
                        _errorSeen = core.ErrorLine;
                        _errorText = $"<size=13><color=#ff5a4a>CO-OP: {core.ErrorLine}</color></size>";
                        _errorGc = new GUIContent(_errorText);
                    }
                    var errSize = CoopTheme.PillSize(CoopTheme.HudPill, _errorGc, 660f);
                    GUI.Label(new Rect(8f, Screen.height - errSize.y - 6f, errSize.x, errSize.y),
                        _errorGc, CoopTheme.HudPill);
                }
            }
            if (CoopCore.Role == CoopRole.Client && HudApi.HostTimeText.Length > 0)
            {
                if (HudApi.HostTimeText != _hostTimeSeen)
                {
                    _hostTimeSeen = HudApi.HostTimeText;
                    _hostTimeText = $"<color=#ffd54a>{HudApi.HostTimeText} - co-op</color>";
                    _hostTimeGc = new GUIContent(_hostTimeText);
                }
                var sz = CoopTheme.PillSize(CoopTheme.HudPill, _hostTimeGc, 440f);
                GUI.Label(new Rect((Screen.width - sz.x) / 2f, 4f, sz.x, sz.y), _hostTimeGc, CoopTheme.HudPill);
            }
            if (HudApi.ToastText.Length > 0)
            {
                DrawHudPill(HudApi.ToastText, "#8ef58a", Screen.height * 0.63f,
                    ref _registerSeen, ref _registerText, ref _registerGc);
            }
            if (HudApi.NextDayWaitActive && HudApi.NextDayWaitText.Length > 0)
            {
                DrawHudPill(HudApi.NextDayWaitText, "#ffd54a", Screen.height * 0.70f,
                    ref _nextDayWaitSeen, ref _nextDayWaitText, ref _nextDayWaitGc);
            }
            // The pending-open notice and the ready-gate notice share this anchor by design:
            // a guest whose recap never opened cannot have pressed Next Day, so the two states
            // are mutually exclusive and only one line can ever be up.
            if (HudApi.EndOfDayPendingActive && HudApi.EndOfDayPendingText.Length > 0)
            {
                DrawHudPill(HudApi.EndOfDayPendingText, "#ffd54a", Screen.height * 0.70f,
                    ref _endOfDayPendingSeen, ref _endOfDayPendingText, ref _endOfDayPendingGc);
            }
            if (!Visible)
            {
                PresenceApi.SetCharacterPreview(false);
                DrawAlert();
                return;
            }

            // The window sets its own height here rather than deriving it from its children: cap
            // it at MaxWindowHeight and at the room the game window actually has, then hand that
            // to GUILayout.Window. The content panel below simply expands into what is left, so
            // the full window (header, tabs, content, footer) always fits after Alt+Enter.
            _win.width = Mathf.Min(400f, Mathf.Max(1f, Screen.width - 32f));
            _win.height = TargetWindowHeight();
            _win.x = Mathf.Clamp(_win.x, 0f, Mathf.Max(0f, Screen.width - _win.width));
            _win.y = Mathf.Clamp(_win.y, 0f, Mathf.Max(0f, Screen.height - _win.height));
            CoopTheme.DrawWindowShadow(_win); // soft drop shadow behind the window (screen space)
            _win = GUILayout.Window(867530, _win, id => WindowFn(core, net), "", CoopTheme.Window,
                GUILayout.Width(_win.width), GUILayout.Height(_win.height));
            // Clamp again after layout: the layout option should already hold the size, but the
            // pre-call clamp only had the previous frame's height.
            _win.x = Mathf.Clamp(_win.x, 0f, Mathf.Max(0f, Screen.width - _win.width));
            _win.y = Mathf.Clamp(_win.y, 0f, Mathf.Max(0f, Screen.height - _win.height));

            // Drawn LAST so it sits above the window and every HUD pill; the same call also runs
            // on the hidden path above, because an error the player must see cannot depend on the
            // co-op window being open.
            DrawAlert();
        }

        /// <summary>Draws the one-shot failure alert (see <see cref="CoopAlert"/>) as a centered
        /// popup window with a Dismiss button. It is sized and placed once, when it first appears,
        /// and then only clamped to the screen - it is not a drag handle, so it stays put.</summary>
        private void DrawAlert()
        {
            if (!CoopAlert.Active)
            {
                _alertShown = false;
                return;
            }

            if (!_alertShown)
            {
                _alertShown = true;
                // Same width discipline as the main window: never wider than the screen it has to
                // fit on, so a small resolution cannot push the alert off the right edge. The seed
                // height only shapes the very first frame's shadow - the real height comes back
                // from GUILayout.Window once it has measured the labels.
                _alertWin.width = Mathf.Min(460f, Mathf.Max(1f, Screen.width - 80f));
                _alertWin.height = 160f;
                _alertWin.x = (Screen.width - _alertWin.width) * 0.5f;
                _alertWin.y = Mathf.Max(24f, Screen.height * 0.32f);
            }

            _alertWin.x = Mathf.Clamp(_alertWin.x, 0f, Mathf.Max(0f, Screen.width - _alertWin.width));
            _alertWin.y = Mathf.Clamp(_alertWin.y, 0f, Mathf.Max(0f, Screen.height - _alertWin.height));
            CoopTheme.DrawWindowShadow(_alertWin);
            _alertWin = GUILayout.Window(867531, _alertWin, id => AlertWindowFn(), "",
                CoopTheme.Window, GUILayout.Width(_alertWin.width));
            // Clamp again after layout, exactly like the main window: the pre-call clamp only had
            // the previous frame's height, so a wrapped label that grows the window is corrected
            // here instead of leaving the alert hanging off the screen.
            _alertWin.x = Mathf.Clamp(_alertWin.x, 0f, Mathf.Max(0f, Screen.width - _alertWin.width));
            _alertWin.y = Mathf.Clamp(_alertWin.y, 0f, Mathf.Max(0f, Screen.height - _alertWin.height));
        }

        private void AlertWindowFn()
        {
            CoopTheme.DrawWindowChrome(new Rect(0f, 0f, _alertWin.width, _alertWin.height),
                CoopAlert.Header, "");
            GUILayout.Label(CoopAlert.Title, CoopTheme.LabelWrap);
            if (CoopAlert.RepeatCount > 1)
            {
                GUILayout.Label("This has happened " + CoopAlert.RepeatCount + " times.",
                    CoopTheme.LabelDim);
            }
            if (CoopAlert.Detail.Length > 0)
            {
                GUILayout.Space(4f);
                GUILayout.Label(CoopAlert.Detail, CoopTheme.LabelDimWrap);
            }
            GUILayout.Space(8f);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Dismiss", CoopTheme.ButtonSecondary, GUILayout.Width(90f)))
            {
                CoopAlert.Dismiss();
            }
            GUILayout.EndHorizontal();
        }

        /// <summary>Draws one horizontally centered persistent HUD pill. OnGUI runs at least twice
        /// per frame and matching layout/repaint passes must see the same control set, so each
        /// line latches its source text and the GUIContent is rebuilt only when that source
        /// changes; the styles/textures stay cached in <see cref="CoopTheme"/>.</summary>
        private static void DrawHudPill(string source, string color, float y,
            ref string seen, ref string cached, ref GUIContent content)
        {
            if (source != seen)
            {
                seen = source;
                cached = $"<size=18><color={color}>{source}</color></size>";
                content = new GUIContent(cached);
            }

            var size = CoopTheme.PillSize(CoopTheme.HudPillBig, content, 740f);
            GUI.Label(new Rect((Screen.width - size.x) / 2f, y, size.x, size.y), content,
                CoopTheme.HudPillBig);
        }

        private void WindowFn(CoopCore core, ICoopTransport net)
        {
            CoopTheme.EnsureBuilt();
            var fps = Mathf.RoundToInt((float)Util.FrameCounter.Fps);
            if (_versionWithFps == null || fps != _fpsShown)
            {
                _fpsShown = fps;
                _versionWithFps = "v" + CoopPlugin.Version + "   " + fps + " FPS";
            }
            CoopTheme.DrawWindowChrome(new Rect(0f, 0f, _win.width, _win.height),
                CoopPlugin.Name.ToUpperInvariant(), _versionWithFps);
            CoopTheme.DrawConnectionIndicator(new Rect(_win.width - 30f, 9f, 12f, 12f),
                GetConnectionState(core, net));

            // A role change can leave the CHARACTER tab selected while it is no longer
            // available (disconnect); normalize before drawing so the highlight, the selected
            // content, and the scroll offset all agree.
            if (_tab == CoopTab.Character && CoopCore.Role == CoopRole.None)
            {
                _tab = CoopTab.Session;
            }

            if (_tab == CoopTab.Hidden && !HiddenCategoryVisible())
            {
                _tab = CoopTab.Session;
            }

            DrawTabs(core);
            PresenceApi.SetCharacterPreview(_tab == CoopTab.Character && CoopCore.Role != CoopRole.None);

            // The window height is set on GUILayout.Window itself, so nothing here measures the
            // header, tabs or footer. The content panel expands into whatever the window has
            // left, and the single scroll view fills the panel. Every tab shares this one scroll
            // view (nested scroll views are a layout hazard) with a separate offset per tab;
            // zero-padding keeps the overflow calculation exact, so the vertical bar only
            // appears when the content actually overflows.
            GUILayout.BeginVertical(CoopTheme.ContentPanel, GUILayout.ExpandHeight(true));
            var scroll = _tab == CoopTab.Character ? _characterScroll
                : _tab == CoopTab.Settings ? _settingsScroll
                : _tab == CoopTab.Hidden ? _hiddenScroll : _sessionScroll;
            scroll = GUILayout.BeginScrollView(scroll, CoopTheme.ScrollView, GUILayout.ExpandHeight(true));
            // Everything in the scroll view shares this minimum-height wrapper, seeded from the
            // viewport measured on the previous Repaint. A short tab therefore fills the space
            // (its last panel can expand) while a tall one grows past it and scrolls.
            //
            // There is deliberately NO error banner above the tabs any more. It said PROBLEM plus the
            // core's last error, which meant a failure to JOIN appeared at the top of the HOSTING tab
            // and vice versa - the error carried no sense of which half produced it. Failures now show
            // only inside the backend section they belong to (BackendError, keyed by mode), and the
            // HUD pill still reports them when the window is hidden.
            GUILayout.BeginVertical(CoopTheme.ScrollContent, GUILayout.MinHeight(_viewportHeight));
            if (_tab == CoopTab.Character && CoopCore.Role != CoopRole.None)
            {
                DrawCharacterSelector(core);
            }
            else if (_tab == CoopTab.Settings)
            {
                DrawSettings(core);
            }
            else if (_tab == CoopTab.Hidden)
            {
                DrawHidden(core);
            }
            else
            {
                DrawSession(core, net);
            }

            GUILayout.EndVertical();
            GUILayout.EndScrollView();
            // Remember the viewport the scroll view actually received so the next pass can size
            // the wrapper above. Measured on Repaint: during Layout the rect is not usable yet.
            // Guarded so a bogus value can never inflate the content into a permanent scrollbar.
            if (Event.current.type == EventType.Repaint)
            {
                var measured = GUILayoutUtility.GetLastRect();
                if (measured.height > 0f && measured.height <= _win.height)
                {
                    _viewportHeight = measured.height;
                }

                // Width for the same reason: layout gives a control a MINIMUM width equal to its
                // content, so a long value (a reach code) widens the panel unless it is given an
                // explicit width - and that width has to come from the panel, not the content.
                // Reject a width bigger than the window: that IS the widened case we are fixing,
                // so keep the last good one instead of latching the overflow.
                if (measured.width > 0f && measured.width <= _win.width)
                {
                    _viewportWidth = measured.width;
                }
            }
            if (_tab == CoopTab.Character)
            {
                _characterScroll = scroll;
            }
            else if (_tab == CoopTab.Settings)
            {
                _settingsScroll = scroll;
            }
            else if (_tab == CoopTab.Hidden)
            {
                _hiddenScroll = scroll;
            }
            else
            {
                _sessionScroll = scroll;
            }

            GUILayout.EndVertical();

            DrawPendingTooltip();
            FinishWindow();
        }

        /// <summary>The SESSION tab body: custom-card-database notices, the role-specific
        /// host/join/status panels, and the graded-adopt prompt. The shared min-height wrapper in
        /// WindowFn lets the last panel expand to fill the tab container.</summary>
        private void DrawSession(CoopCore core, ICoopTransport net)
        {
            // The restore OUTCOME lives out here, NOT inside the lend block below. A successful
            // restore (and the no-backup branch, which also clears the marker) makes
            // EnumLendState() return null, so a message drawn inside that block would be drawn
            // for zero frames - the button appeared to do nothing at all. Out here it stays put
            // until the player closes the window.
            if (_enumRestoreMsg != null)
            {
                GUILayout.BeginHorizontal();
                CoopTheme.Chip("CARD DATABASE", CoopTheme.ChipWarn);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Label(_enumRestoreMsg, CoopTheme.LabelWarn);
                GUILayout.Space(4f);
            }

            // Custom-card database on loan: a mismatched-enum join replaced the machine-global
            // registry with the HOST's copy (with a backup). Until it is restored, the player's
            // own modded SOLO saves fail to load ("data lost") - the exact field report this
            // notice exists for. Show the warning + one-click restore whenever the marker says
            // the on-disk registry is the host's.
            var lend = CatalogApi.EnumLendState();
            if (lend != null)
            {
                GUILayout.BeginHorizontal();
                CoopTheme.Chip("CARD DATABASE", CoopTheme.ChipWarn);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Label(lend, CoopTheme.LabelWarn);
                // (the restore outcome is rendered above, outside this block, because a
                // successful restore removes the very banner it would otherwise sit under)
                // restoring mid-session would break the CURRENT co-op world's custom cards;
                // only offer it when not connected
                if (CoopCore.Role == CoopRole.None
                    && GUILayout.Button("Restore MY card database (for solo saves - restart after)", CoopTheme.ButtonDanger))
                {
                    CatalogApi.RestoreEnumBackup(out _enumRestoreMsg);
                }
                GUILayout.Space(4f);
            }

            switch (CoopCore.Role)
            {
                case CoopRole.None:
                    DrawNone(core);
                    break;
                case CoopRole.Host:
                    // The session roster first, so the host can see who is in and remove anyone.
                    DrawHostPlayers(core);
                    // No separate hosting view: the tab stays the backend list. The container of the
                    // backend you started swaps its options for that backend's connection info, and
                    // the others stay usable so another can be opened or closed on its own.
                    DrawConnectBody(core, ConnectMode.Host);
                    break;
                case CoopRole.Client:
                    DrawClient(core);
                    break;
            }

            // Out here rather than inside the three role branches so it reaches all of them with
            // one call: the Steam host path returns early from DrawHost, and Role None simply has
            // no offers to draw.
            DrawGradedAdopt(core);
        }

        /// <summary>Shared footer for every tab: latch text-field focus, show the
        /// toggle-key hint below the content panel, and keep the header draggable.</summary>
        private void FinishWindow()
        {
            var focused = GUI.GetNameOfFocusedControl();
            TextFieldFocused = focused != null && focused.StartsWith("coop_");
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label(ToggleHintText(), CoopTheme.LabelDim);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
        }

        /// <summary>Cached "Press &lt;key&gt; to toggle this window" line; rebuilt only when
        /// the configured key changes because OnGUI runs multiple times per frame.</summary>
        private string ToggleHintText()
        {
            var key = CoopPlugin.UiToggleKey != null ? CoopPlugin.UiToggleKey.Value : KeyCode.None;
            if (_toggleHint == null || key != _toggleKeySeen)
            {
                _toggleKeySeen = key;
                _toggleHint = key == KeyCode.None
                    ? "Press the toggle key to toggle this window"
                    : $"Press {key} to toggle this window";
            }
            return _toggleHint;
        }

        /// <summary>The complete outer window height: capped at <see cref="MaxWindowHeight"/>
        /// and at the space the game window actually has, so the whole window (header, tabs,
        /// content and footer) always fits after the game switches display mode.</summary>
        private static float TargetWindowHeight()
        {
            // Never larger than the screen, and never a cap smaller than the floor we hand to
            // Clamp; on any real resolution the screen term comfortably dominates.
            var max = Mathf.Max(1f, Mathf.Min(MaxWindowHeight, Screen.height - WindowScreenMargin));
            var min = Mathf.Min(320f, max);
            return Mathf.Clamp(Screen.height * 0.72f, min, max);
        }

        private void DrawTabs(CoopCore core)
        {
            GUILayout.BeginHorizontal();
            GUI.enabled = _tab != CoopTab.Session;
            if (GUILayout.Button("SESSION", _tab == CoopTab.Session ? CoopTheme.TabSelected : CoopTheme.Tab,
                GUILayout.Width(88f)))
            {
                _tab = CoopTab.Session;
            }

            GUI.enabled = CoopCore.Role != CoopRole.None && _tab != CoopTab.Character;
            if (GUILayout.Button("CHARACTER", _tab == CoopTab.Character ? CoopTheme.TabSelected : CoopTheme.Tab,
                GUILayout.Width(104f)))
            {
                _tab = CoopTab.Character;
            }

            GUI.enabled = _tab != CoopTab.Settings;
            if (GUILayout.Button("SETTINGS", _tab == CoopTab.Settings ? CoopTheme.TabSelected : CoopTheme.Tab,
                GUILayout.Width(96f)))
            {
                _tab = CoopTab.Settings;
            }

            if (HiddenCategoryVisible())
            {
                GUI.enabled = _tab != CoopTab.Hidden;
                if (GUILayout.Button("HIDDEN", _tab == CoopTab.Hidden ? CoopTheme.TabSelected : CoopTheme.Tab,
                    GUILayout.Width(88f)))
                {
                    _tab = CoopTab.Hidden;
                }
            }
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        /// <summary>The SETTINGS tab: appearance, live logging switches, and the artificial
        /// latency test sliders. Every control writes straight to its ConfigEntry, which
        /// persists to disk, so the tab is a front-end for the config file rather than a
        /// second source of truth.</summary>
        private void DrawSettings(CoopCore core)
        {
            GUILayout.Label("APPEARANCE", CoopTheme.SectionHeader);
            var allowNsfw = GUILayout.Toggle(CoopPlugin.AllowNsfw.Value,
                CoopPlugin.AllowNsfw.Value ? "NSFW allowed" : "NSFW hidden", CoopTheme.Toggle);
            if (allowNsfw != CoopPlugin.AllowNsfw.Value)
            {
                PresenceApi.SetNsfwAllowed(allowNsfw);
            }

            GUILayout.Space(8f);

            GUILayout.Label("JOINING", CoopTheme.SectionHeader);
            DrawConfigToggle(CoopPlugin.AllowCrossBuildJoin,
                "Let a joiner on a DIFFERENT game build in (host only - risky)");
            GUILayout.Space(8f);

            GUILayout.Label("LOGGING", CoopTheme.SectionHeader);
            DrawConfigToggle(CoopPlugin.PerfDebug, "Unity hitch profiling and stage timing (PerfDebug)");
            GUILayout.Space(8f);

            GUILayout.Label("LATENCY TESTING", CoopTheme.SectionHeader);
            DrawConfigIntSlider(CoopPlugin.ArtificialLagMs, 0, 2000, "Artificial lag");
            DrawConfigIntSlider(CoopPlugin.ArtificialJitterMs, 0, 1000, "Jitter");
            GUILayout.Space(8f);

            // LabelDimWrap, not LabelDim: this line is wider than the panel, and a non-wrapping
            // label would push the whole scroll content out horizontally instead of folding onto a
            // second row.
            GUILayout.Label("Settings are saved to BepInEx/config/dev.meepen.cardshopcoop.cfg.", CoopTheme.LabelDimWrap);
        }

        private static bool HiddenCategoryVisible()
        {
            return CoopPlugin.ShowHiddenCategory != null && CoopPlugin.ShowHiddenCategory.Value;
        }

        private static void DrawHidden(CoopCore core)
        {
            DrawConfigToggle(CoopPlugin.ShowHiddenCategory, "Show hidden category");
            DrawConfigToggle(CoopPlugin.EnableGameCheatMenu, "Enable game cheat menu");
            // Bound only on builds with the entitlement gate (Game Pass), so this draws nothing on
            // the Steam build.
            DrawConfigToggle(CoopPlugin.ForceDeluxeFurniture, "Force deluxe furniture");
            GUILayout.Space(8f);

            DrawModuleToggles();
        }

        /// <summary>The diagnostic module checkboxes. Moved to the HIDDEN tab because turning a
        /// module off is a support action, not a normal setting. Every control writes straight to
        /// its ConfigEntry, which persists to disk, so this is a front-end for the config file
        /// rather than a second source of truth.</summary>
        private static void DrawModuleToggles()
        {
            GUILayout.Label("MODULES (DIAGNOSTIC)", CoopTheme.SectionHeader);
            GUILayout.Label("Uncheck a module to turn it off entirely - its patches, handlers and "
                + "per-frame work stop. Use this to find which module costs frame rate. Applies "
                + "after rejoining (persistent modules after a restart); leave the essentials on so "
                + "you can still connect.", CoopTheme.LabelDimWrap);
            foreach (var module in ModuleCatalog.Names())
            {
                var entry = ModuleCatalog.Entry(module);
                if (entry == null)
                {
                    continue;
                }

                var enabled = GUILayout.Toggle(entry.Value, " " + module, CoopTheme.Toggle);
                if (enabled != entry.Value)
                {
                    entry.Value = enabled; // ConfigEntry writes the file itself
                }
            }
        }

        private static void DrawConfigToggle(ConfigEntry<bool> entry, string label)
        {
            if (entry == null)
            {
                return;
            }

            var value = GUILayout.Toggle(entry.Value, " " + label, CoopTheme.Toggle);
            if (value != entry.Value)
            {
                entry.Value = value; // ConfigEntry writes the file itself
            }
        }

        private static void DrawConfigIntSlider(ConfigEntry<int> entry, int min, int max, string label)
        {
            if (entry == null)
            {
                return;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label(label, CoopTheme.LabelDim, GUILayout.Width(130f));
            var value = Mathf.RoundToInt(GUILayout.HorizontalSlider(entry.Value, min, max));
            GUILayout.Label(value + " ms", CoopTheme.LabelDim, GUILayout.Width(56f));
            GUILayout.EndHorizontal();
            if (value != entry.Value)
            {
                entry.Value = value; // ConfigEntry writes the file itself
            }
        }

        private static int GetConnectionState(CoopCore core, ICoopTransport net)
        {
            if (CoopCore.Role == CoopRole.Host)
            {
                return (net?.ConnectionCount ?? 0) > 0 ? 2 : 1;
            }

            if (CoopCore.Role == CoopRole.Client)
            {
                var status = core.StatusLine ?? "";
                return status.IndexOf("Connecting", StringComparison.OrdinalIgnoreCase) >= 0
                    || status.IndexOf("loading", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 2;
            }
            return 0;
        }

        private void DrawCharacterSelector(CoopCore core)
        {
            if (Event.current.type == EventType.Layout
                && (!_characterSnapshotReady || _characterSnapshotGeneration != PresenceApi.PlayerModelGeneration))
            {
                var model = PresenceApi.GetLocalPlayerModel();
                _characterFemale = model.Female;
                _presetNames.Clear();
                _presetNames.AddRange(PresenceApi.GetLocalPresetNames());
                _presetSelection = _presetNames.FindIndex(x => x == (_characterFemale ? "Female" : "Male") + model.ModelIndex);
                if (_presetSelection < 0)
                {
                    _presetSelection = 0;
                }

                _presetSelectionBuffer[0] = _presetSelection;
                _characterCustomizer = PresenceApi.GetLocalCustomization();
                CacheWardrobeControls();
                _characterSnapshotReady = true;
                _characterSnapshotGeneration = PresenceApi.PlayerModelGeneration;
            }
            if (!_characterSnapshotReady)
            {
                return;
            }

            GUILayout.BeginVertical(CoopTheme.SectionBox);
            GUILayout.BeginHorizontal();
            CoopTheme.Chip("MY CHARACTER", CoopTheme.ChipInfo);
            GUILayout.FlexibleSpace();
            _characterOpen = GUILayout.Toggle(_characterOpen, _characterOpen ? "▲" : "▼", CoopTheme.Toggle, GUILayout.Width(28f));
            GUILayout.EndHorizontal();
            if (_characterOpen)
            {
                GUILayout.BeginHorizontal();
                GUI.enabled = PresenceApi.CanUndoPlayerModel;
                if (GUILayout.Button("Undo", CoopTheme.ButtonSecondary, GUILayout.Width(58f)))
                {
                    PresenceApi.UndoPlayerModel();
                }

                GUI.enabled = PresenceApi.CanRedoPlayerModel;
                if (GUILayout.Button("Redo", CoopTheme.ButtonSecondary, GUILayout.Width(58f)))
                {
                    PresenceApi.RedoPlayerModel();
                }

                GUI.enabled = true;
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Label("Gender", CoopTheme.Label, GUILayout.Width(56f));
                if (GUILayout.Button(_characterFemale ? "Female" : "Male", CoopTheme.ButtonSecondary))
                {
                    _characterFemale = !_characterFemale;
                    PresenceApi.SetLocalPlayerModel(_characterFemale, 0);
                }
                GUILayout.EndHorizontal();
                if (_presetNames.Count > 0)
                {
                    DrawCycleRow("Preset", _presetNames, _presetSelectionBuffer, 0,
                        () => { _presetSelection = _presetSelectionBuffer[0]; PresenceApi.ApplyLocalPreset(_presetNames[_presetSelection]); });
                }
                else
                {
                    GUILayout.Label("No presets found for this gender.", CoopTheme.LabelDimWrap);
                }

                DrawWardrobeControls(core);
            }
            GUILayout.EndVertical();
            GUILayout.Space(4f);
        }

        private void CacheWardrobeControls()
        {
            _hairOptions.Clear();
            _hairSelections.Clear();
            _hairColors.Clear();
            _apparelOptions.Clear();
            _apparelNames.Clear();
            _apparelSelections.Clear();
            _apparelMaterials.Clear();
            _apparelColors.Clear();
            _floatNames.Clear();
            _floatValues.Clear();
            if (_characterCustomizer == null)
            {
                return;
            }

            var data = _characterCustomizer.StoredCharacterData;
            if (data == null)
            {
                return;
            }

            for (var slot = 0; slot < _characterCustomizer.HairTables.Count; slot++)
            {
                var options = new List<string> { "None" };
                var table = _characterCustomizer.HairTables[slot];
                if (table != null && table.Hairstyles != null)
                {
                    foreach (var hair in table.Hairstyles)
                    {
                        options.Add(hair.Name ?? "(unnamed)");
                    }
                }

                _hairOptions.Add(options);
                var current = slot < data.HairNames.Count ? data.HairNames[slot] : "";
                var selected = options.FindIndex(x => x == current);
                _hairSelections.Add(selected < 0 ? 0 : selected);
                _hairColors.Add(ReadHairColor(data, slot));
            }

            for (var slot = 0; slot < _characterCustomizer.ApparelTables.Count; slot++)
            {
                var options = new List<string>();
                var names = new List<string>();
                // The Nude slot is only offered while NSFW is allowed; otherwise the model
                // is guaranteed clothed, so the empty option must not be selectable.
                if (CoopPlugin.AllowNsfw.Value)
                {
                    options.Add("Nude");
                    names.Add("");
                }
                var table = _characterCustomizer.ApparelTables[slot];
                if (table != null && table.Items != null)
                {
                    foreach (var item in table.Items)
                    {
                        options.Add(string.IsNullOrEmpty(item.DisplayName) ? (item.Name ?? "(unnamed)") : item.DisplayName);
                        names.Add(item.Name ?? "");
                    }
                }

                _apparelOptions.Add(options);
                _apparelNames.Add(names);
                var current = slot < data.ApparelNames.Count ? data.ApparelNames[slot] : "";
                var selected = names.IndexOf(current);
                _apparelSelections.Add(selected < 0 ? 0 : selected);
                _apparelMaterials.Add(slot < data.ApparelMaterials.Count ? Mathf.Max(0, data.ApparelMaterials[slot]) : 0);
                _apparelColors.Add(ReadApparelTint(data, slot));
            }

            if (data.FloatProperties != null)
            {
                foreach (var property in data.FloatProperties)
                {
                    if (property == null || string.IsNullOrEmpty(property.propertyName))
                    {
                        continue;
                    }

                    _floatNames.Add(property.propertyName);
                    _floatValues.Add(property.floatValue);
                }
            }
        }

        private void DrawWardrobeControls(CoopCore core)
        {
            if (_characterCustomizer == null)
            {
                return;
            }

            if (_hairOptions.Count > 0 || _apparelOptions.Count > 0)
            {
                GUILayout.Label("WARDROBE", CoopTheme.SectionHeader);
            }

            for (var slot = 0; slot < _hairOptions.Count; slot++)
            {
                DrawCycleColorRow("Hair " + (slot + 1), _hairOptions[slot], _hairSelections, slot,
                    _hairColors[slot], 0, slot, color => { _hairColors[slot] = color; PresenceApi.SetLocalHairColor(slot, color); },
                    () => ApplyHair(core, slot));
            }

            for (var slot = 0; slot < _apparelOptions.Count; slot++)
            {
                // With NSFW off a slot with no real items offers nothing selectable.
                if (_apparelOptions[slot].Count == 0)
                {
                    continue;
                }

                var label = _characterCustomizer.ApparelTables[slot] != null
                    && !string.IsNullOrEmpty(_characterCustomizer.ApparelTables[slot].Label)
                    ? _characterCustomizer.ApparelTables[slot].Label : "Apparel " + (slot + 1);
                DrawCycleColorRow(label, _apparelOptions[slot], _apparelSelections, slot,
                    _apparelColors[slot], 1, slot, color => { _apparelColors[slot] = color; PresenceApi.SetLocalApparelTint(slot, color); },
                    () => ApplyApparel(core, slot));
                var selectedApparel = _apparelNames[slot][_apparelSelections[slot]];
                if (!string.IsNullOrEmpty(selectedApparel))
                {
                    var table = _characterCustomizer.ApparelTables[slot];
                    // When the Nude slot is offered it occupies index 0, so the item index
                    // is offset by one; with NSFW off the list starts at the first real item.
                    var itemIndex = _apparelSelections[slot] - (CoopPlugin.AllowNsfw.Value ? 1 : 0);
                    var materialCount = table != null && itemIndex >= 0 && itemIndex < table.Items.Count
                        && table.Items[itemIndex].Materials != null ? table.Items[itemIndex].Materials.Count : 0;
                    if (materialCount > 1)
                    {
                        DrawNumberRow("Material", materialCount, _apparelMaterials, slot,
                            () => ApplyApparel(core, slot));
                    }
                }
            }
            for (var i = 0; i < _floatNames.Count; i++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(_floatNames[i], CoopTheme.LabelDim, GUILayout.Width(130f));
                var min = (_floatNames[i] == "Height" || _floatNames[i] == "Width") ? 0.5f : 0f;
                var max = (_floatNames[i] == "Height" || _floatNames[i] == "Width") ? 1.5f : 1f;
                var value = GUILayout.HorizontalSlider(_floatValues[i], min, max);
                GUILayout.Label(value.ToString("0.00"), CoopTheme.LabelDim, GUILayout.Width(36f));
                GUILayout.EndHorizontal();
                if (!Mathf.Approximately(value, _floatValues[i]))
                {
                    _floatValues[i] = value;
                    var prop = new CC.CC_Property { propertyName = _floatNames[i], floatValue = value };
                    _characterCustomizer.setFloatProperty(prop, true);
                    PresenceApi.CommitLocalCustomization();
                }
            }
        }

        private void DrawCycleRow(string label, List<string> options, List<int> selections, int index,
            Action apply)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, CoopTheme.LabelDim, GUILayout.Width(86f));
            if (GUILayout.Button("<", CoopTheme.ButtonSecondary, GUILayout.Width(24f)))
            {
                selections[index] = (selections[index] + options.Count - 1) % options.Count;
                apply();
            }
            GUILayout.Label(options[selections[index]], CoopTheme.Label, GUILayout.ExpandWidth(true));
            if (GUILayout.Button(">", CoopTheme.ButtonSecondary, GUILayout.Width(24f)))
            {
                selections[index] = (selections[index] + 1) % options.Count;
                apply();
            }
            GUILayout.EndHorizontal();
        }

        private void DrawCycleColorRow(string label, List<string> options, List<int> selections, int index,
            Color color, int kind, int slot, Action<Color> applyColor, Action applySelection)
        {
            var expanded = _colorEditorKind == kind && _colorEditorSlot == slot;
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, CoopTheme.LabelDim, GUILayout.Width(86f));
            if (GUILayout.Button("<", CoopTheme.ButtonSecondary, GUILayout.Width(24f)))
            {
                selections[index] = (selections[index] + options.Count - 1) % options.Count;
                applySelection();
            }
            GUILayout.Label(options[selections[index]], CoopTheme.Label, GUILayout.ExpandWidth(true));
            if (GUILayout.Button(">", CoopTheme.ButtonSecondary, GUILayout.Width(24f)))
            {
                selections[index] = (selections[index] + 1) % options.Count;
                applySelection();
            }
            var previous = GUI.backgroundColor;
            GUI.backgroundColor = color;
            if (GUILayout.Button(" ", CoopTheme.ButtonSecondary, GUILayout.Width(24f)))
            {
                _colorEditorKind = expanded ? -1 : kind;
                _colorEditorSlot = expanded ? -1 : slot;
                if (expanded)
                {
                    _colorDragTarget = 0;
                }
            }
            GUI.backgroundColor = previous;
            GUILayout.EndHorizontal();
            if (!expanded)
            {
                return;
            }

            Color.RGBToHSV(color, out var hue, out var saturation, out var value);
            DrawColorPicker(ref hue, ref saturation, ref value, applyColor);
        }

        private void DrawColorPicker(ref float hue, ref float saturation, ref float value, Action<Color> applyColor)
        {
            EnsureColorPickerTextures();
            if (!Mathf.Approximately(_pickerTextureHue, hue))
            {
                RebuildSaturationValueTexture(hue);
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Color", CoopTheme.LabelDim, GUILayout.Width(86f));
            var svRect = GUILayoutUtility.GetRect(150f, 92f, GUILayout.Width(150f), GUILayout.Height(92f));
            var hueRect = GUILayoutUtility.GetRect(18f, 92f, GUILayout.Width(18f), GUILayout.Height(92f));
            GUI.DrawTexture(svRect, _svPickerTexture, ScaleMode.StretchToFill, false);
            GUI.DrawTexture(hueRect, _huePickerTexture, ScaleMode.StretchToFill, false);
            DrawPickerMarker(new Rect(svRect.x + saturation * svRect.width - 4f,
                svRect.y + (1f - value) * svRect.height - 4f, 8f, 8f), Color.white);
            DrawPickerMarker(new Rect(hueRect.x - 2f, hueRect.y + (1f - hue) * hueRect.height - 2f,
                hueRect.width + 4f, 4f), Color.black);
            GUILayout.EndHorizontal();

            var e = Event.current;
            var mousePosition = GUIUtility.GUIToScreenPoint(e.mousePosition);
            var svScreenPosition = GUIUtility.GUIToScreenPoint(svRect.position);
            var hueScreenPosition = GUIUtility.GUIToScreenPoint(hueRect.position);
            Rect svScreenRect = new(svScreenPosition.x, svScreenPosition.y, svRect.width, svRect.height);
            Rect hueScreenRect = new(hueScreenPosition.x, hueScreenPosition.y, hueRect.width, hueRect.height);
            var changed = false;
            if (e.type == EventType.MouseDown)
            {
                if (svScreenRect.Contains(mousePosition))
                {
                    _colorDragTarget = 1;
                    saturation = Mathf.Clamp01((mousePosition.x - svScreenRect.x) / svScreenRect.width);
                    value = Mathf.Clamp01(1f - (mousePosition.y - svScreenRect.y) / svScreenRect.height);
                    changed = true;
                    e.Use();
                }
                else if (hueScreenRect.Contains(mousePosition))
                {
                    _colorDragTarget = 2;
                    hue = Mathf.Clamp01(1f - (mousePosition.y - hueScreenRect.y) / hueScreenRect.height);
                    changed = true;
                    e.Use();
                }
            }
            else if (e.type == EventType.MouseDrag && _colorDragTarget != 0)
            {
                if (_colorDragTarget == 1)
                {
                    saturation = Mathf.Clamp01((mousePosition.x - svScreenRect.x) / svScreenRect.width);
                    value = Mathf.Clamp01(1f - (mousePosition.y - svScreenRect.y) / svScreenRect.height);
                }
                else
                {
                    hue = Mathf.Clamp01(1f - (mousePosition.y - hueScreenRect.y) / hueScreenRect.height);
                }

                changed = true;
                e.Use();
            }
            else if (e.type == EventType.MouseUp)
            {
                _colorDragTarget = 0;
            }

            if (changed)
            {
                applyColor(Color.HSVToRGB(hue, saturation, value));
            }
        }

        private void EnsureColorPickerTextures()
        {
            if (_svPickerTexture == null)
            {
                _svPickerTexture = new Texture2D(96, 64, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    // Process-lifetime like CoopTheme's textures: HideAndDontSave keeps Unity from
                    // destroying them on a scene load (which would dangle the cached reference).
                    hideFlags = HideFlags.HideAndDontSave
                };
            }
            if (_huePickerTexture == null)
            {
                _huePickerTexture = new Texture2D(1, 96, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave
                };
                // Top of the strip is the highest hue, matching the marker and click handler, which
                // both read the bar top-down (hue 1 at the top). The saturation/value box uses the
                // same convention for its value axis.
                for (var y = 0; y < 96; y++)
                {
                    _huePickerTexture.SetPixel(0, y, Color.HSVToRGB(y / 95f, 1f, 1f));
                }

                _huePickerTexture.Apply();
            }
        }

        private void RebuildSaturationValueTexture(float hue)
        {
            _pickerTextureHue = hue;
            for (var y = 0; y < 64; y++)
            {
                for (var x = 0; x < 96; x++)
                {
                    _svPickerTexture.SetPixel(x, y, Color.HSVToRGB(hue, x / 95f, y / 63f));
                }
            }

            _svPickerTexture.Apply();
        }

        private static void DrawPickerMarker(Rect rect, Color color)
        {
            var previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        private static Color ReadHairColor(CC.CC_CharacterData data, int slot)
        {
            if (data != null && data.HairColor != null && slot >= 0 && slot < data.HairColor.Count)
            {
                Color color;
                if (ColorUtility.TryParseHtmlString("#" + data.HairColor[slot].stringValue, out color))
                {
                    return color;
                }
            }
            return Color.white;
        }

        private static Color ReadApparelTint(CC.CC_CharacterData data, int slot)
        {
            if (data != null && data.ColorProperties != null)
            {
                var key = "CardShopCoop.ApparelTint." + slot;
                foreach (var property in data.ColorProperties)
                {
                    if (property == null || property.propertyName != key)
                    {
                        continue;
                    }

                    Color color;
                    if (ColorUtility.TryParseHtmlString("#" + property.stringValue, out color))
                    {
                        return color;
                    }
                }
            }
            return Color.white;
        }

        private void DrawNumberRow(string label, int count, List<int> values, int index, Action apply)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, CoopTheme.LabelDim, GUILayout.Width(86f));
            if (GUILayout.Button("<", CoopTheme.ButtonSecondary, GUILayout.Width(24f)))
            {
                values[index] = (values[index] + count - 1) % count;
                apply();
            }
            GUILayout.Label((values[index] + 1).ToString(), CoopTheme.Label, GUILayout.ExpandWidth(true));
            if (GUILayout.Button(">", CoopTheme.ButtonSecondary, GUILayout.Width(24f)))
            {
                values[index] = (values[index] + 1) % count;
                apply();
            }
            GUILayout.EndHorizontal();
        }

        private void ApplyHair(CoopCore core, int slot)
        {
            if (_characterCustomizer == null)
            {
                return;
            }

            if (_hairSelections[slot] == 0)
            {
                PresenceApi.ClearLocalHair(slot);
            }
            else
            {
                _characterCustomizer.setHairByName(_hairOptions[slot][_hairSelections[slot]], slot);
                PresenceApi.CommitLocalCustomization();
            }
        }

        private void ApplyApparel(CoopCore core, int slot)
        {
            if (_characterCustomizer == null)
            {
                return;
            }

            var name = _apparelNames[slot][_apparelSelections[slot]];
            if (string.IsNullOrEmpty(name))
            {
                PresenceApi.ClearLocalApparel(slot);
            }
            else
            {
                var material = Mathf.Max(0, _apparelMaterials[slot]);
                _characterCustomizer.setApparelByName(name, slot, material);
                PresenceApi.CommitLocalCustomization();
            }
        }

        /// <summary>Status chip (colored by state) + the raw StatusLine kept verbatim beside it
        /// (people paste that line into bug reports).</summary>
        private void DrawStatusRow(CoopCore core, ICoopTransport net)
        {
            ClassifyStatus(core, net, out var chip, out var chipText);
            GUILayout.BeginHorizontal();
            if (chip != null)
            {
                CoopTheme.Chip(chipText, chip);
                GUILayout.Space(6f);
                GUILayout.Label(core.StatusLine, CoopTheme.LabelDim);
            }
            else
            {
                GUILayout.Label(core.StatusLine, CoopTheme.Label);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        /// <summary>success when hosting-with-players or connected, info when waiting/connecting,
        /// plain (null chip) otherwise.</summary>
        private static void ClassifyStatus(CoopCore core, ICoopTransport net, out GUIStyle chip, out string text)
        {
            chip = null;
            text = null;
            var s = core.StatusLine ?? "";
            switch (CoopCore.Role)
            {
                case CoopRole.Host:
                    if ((net?.ConnectionCount ?? 0) > 0)
                    {
                        chip = CoopTheme.ChipSuccess;
                        text = "HOSTING";
                    }
                    else
                    {
                        chip = CoopTheme.ChipInfo;
                        text = "WAITING";
                    }
                    break;
                case CoopRole.Client:
                    if (Has(s, "download") || Has(s, "loading") || Has(s, "requesting")
                        || Has(s, "received") || Has(s, "Joining") || Has(s, "Connecting"))
                    {
                        chip = CoopTheme.ChipInfo;
                        text = "CONNECTING";
                    }
                    else
                    {
                        chip = CoopTheme.ChipSuccess;
                        text = "CONNECTED";
                    }
                    break;
                default: // None
                    if (Has(s, "Joining") || Has(s, "Creating") || Has(s, "Connecting"))
                    {
                        chip = CoopTheme.ChipInfo;
                        text = "CONNECTING";
                    }
                    break;
            }
        }

        private static bool Has(string hay, string needle)
            => hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>No session yet. Which half of the window is offered follows the game state, not a
        /// toggle: a loaded save means you are HOSTING (the shop is live), the title screen means you
        /// are JOINING. The old manual host/join toggle is gone.</summary>
        private void DrawNone(CoopCore core)
            => DrawConnectBody(core, core.InSave ? ConnectMode.Host : ConnectMode.Join);

        /// <summary>
        /// The connect tab: the player's name and every backend's own container, in the mode the game
        /// state dictates. Starting a session does NOT replace any of this - the container of
        /// the backend you started swaps its options for that backend's connection info, and the rest
        /// stay usable, so a second backend can be opened or closed on its own.
        /// </summary>
        private void DrawConnectBody(CoopCore core, ConnectMode mode)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Your name:", CoopTheme.Label, GUILayout.Width(72f));
            GUI.SetNextControlName("coop_name");
            var newName = GUILayout.TextField(_nameField, 16, CoopTheme.TextField,
                GUILayout.Width(FieldWidth(72f)));
            var nameFocused = string.Equals(GUI.GetNameOfFocusedControl(), "coop_name",
                StringComparison.Ordinal);
            if (newName != _nameField)
            {
                _nameField = newName;
                var trimmed = newName.Trim();
                if (trimmed.Length > 0)
                {
                    CoopPlugin.PlayerName.Value = trimmed;
                    if (!CoopPlugin.NameOverride.Value)
                    {
                        CoopPlugin.NameOverride.Value = true;
                    }
                }
                else if (CoopPlugin.NameOverride.Value)
                {
                    // Emptying the box means "go back to my platform name". Drop the override now,
                    // but leave the box empty until the player clicks away - refilling it mid-edit
                    // would fight them. The branch below repopulates it once focus is lost.
                    CoopPlugin.NameOverride.Value = false;
                }
            }
            else if (!CoopPlugin.NameOverride.Value && !nameFocused)
            {
                // Back on the platform name and no longer being edited: refill the box after a
                // clear, and let it fill in the moment the Xbox gamertag or Steam persona arrives.
                var effective = core.EffectivePlayerName;
                if (!string.Equals(_nameField, effective, StringComparison.Ordinal))
                {
                    _nameField = effective;
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4f);

            if (Event.current.type == EventType.Layout)
            {
                SnapshotHosting(core);
                if (Time.unscaledTime - _availabilityPolledAt > AvailabilityRefreshSeconds)
                {
                    _availabilityPolledAt = Time.unscaledTime;
                    // Re-probe, but KEEP the last known answer: clearing it would flip every group to
                    // "checking..." and collapse it for a frame or two, which reads as flicker.
                    _availabilityTasks.Clear();
                }
            }

            var backends = core.Backends;
            if (backends.Count == 0)
            {
                GUILayout.Label("No transports available.", CoopTheme.LabelDim);
                return;
            }

            DrawBackendGroups(core, mode);

            if (CoopCore.Role == CoopRole.Host)
            {
                DrawSessionActions(core);
            }
        }

        /// <summary>Which backends are carrying the session, and what they publish, snapped once per
        /// frame. The backends write that info from their own worker threads, so reading it live in
        /// Repaint after Layout read it could change a container's contents between the two passes -
        /// the Mismatched LayoutGroup this snapshot exists to prevent.</summary>
        private void SnapshotHosting(CoopCore core)
        {
            _hostInfo.Clear();
            _backendInfo.Clear();
            _fields.Clear();
            foreach (var backend in core.Backends)
            {
                var info = backend.ConnectionInfo;

                // EVERY backend's info, not just the hosting ones: the header shows a start's current
                // step and whether it is still working, and both live here.
                _backendInfo[backend.Id] = info;

                if (info.Active && info.IsHost)
                {
                    _hostInfo[backend.Id] = info;
                }

                // The field list itself, frozen here too: a backend decides which fields it needs from
                // state it publishes on a worker thread (the router's answer), so a list read live
                // could differ between Layout and Repaint and that is the Mismatched LayoutGroup.
                _fields[backend.Id + "|" + ConnectMode.Host] = backend.GetFields(ConnectMode.Host);
                _fields[backend.Id + "|" + ConnectMode.Join] = backend.GetFields(ConnectMode.Join);
            }
        }

        /// <summary>Whether drawing the fields changed any of them - a choice or a toggle counts as much
        /// as typed text.</summary>
        private static bool ValuesChanged(Dictionary<FieldId, string> before, FieldValues values)
        {
            if (before.Count != values.Count)
            {
                return true;
            }

            foreach (var pair in values)
            {
                if (!before.TryGetValue(pair.Key, out var was)
                    || !string.Equals(was, pair.Value, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>This frame's snapshot of a backend's fields - see <see cref="SnapshotHosting"/>.</summary>
        private IReadOnlyList<BackendField> FieldsFor(INetworkingBackend backend, ConnectMode mode)
            => _fields.TryGetValue(backend.Id + "|" + mode, out var fields)
                ? fields
                : backend.GetFields(mode);

        /// <summary>
        /// Every backend's own section, in the given mode. Drawn both before hosting starts and again
        /// while hosting, so a shop already reachable over one backend can be opened on another (LAN
        /// and Steam friends at once) and either can be closed on its own - only the section you start
        /// changes, not the list.
        /// </summary>
        private void DrawBackendGroups(CoopCore core, ConnectMode mode)
        {
            foreach (var backend in core.Backends)
            {
                DrawBackendGroup(core, backend, mode);
            }
        }

        /// <summary>One collapsible backend group: a header with its availability (a spinner while
        /// probing, a cross plus the reason when unusable), and - when available and expanded - the
        /// fields for the given mode and its own Start/Join/Stop button.</summary>
        private void DrawBackendGroup(CoopCore core, INetworkingBackend backend, ConnectMode mode)
        {
            var id = backend.Id;
            if (Event.current.type == EventType.Layout)
            {
                RefreshAvailability(backend);
            }

            var known = _availability.TryGetValue(id, out var cached);
            var available = known && cached.Available;

            // Expansion is the player's state alone. It used to require "available", so any probe
            // in flight collapsed the group - that was the flicker.
            var expanded = _expanded.Contains(id);

            GUILayout.BeginVertical(CoopTheme.SectionBox);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button((expanded ? "\u25bc " : "\u25b6 ") + backend.DisplayName,
                    CoopTheme.LabelBold))
            {
                if (expanded)
                {
                    _expanded.Remove(id);
                }
                else
                {
                    _expanded.Add(id);
                }
            }

            GUILayout.FlexibleSpace();

            // WHERE THE START HAS GOT TO, right-aligned in the header. The text and the busy flag come
            // from the per-frame snapshot, because a backend publishes them from its own worker thread.
            ConnectionInfo progress = default;
            var busy = known && _backendInfo.TryGetValue(id, out progress) && progress.Busy;
            if (busy && !string.IsNullOrEmpty(progress.Summary))
            {
                GUILayout.Label(progress.Summary, CoopTheme.Label);
                GUILayout.Space(4f);
            }

            // The action lives where the status tick used to be: one Start/Stop for this container, so
            // the body holds only the fields, or the connection info once it is carrying.
            if (!known)
            {
                GUILayout.Label("checking\u2026", CoopTheme.LabelDim);
            }
            else if (!available)
            {
                GUILayout.Label("\u2715 " + (string.IsNullOrEmpty(cached.Reason)
                    ? "unavailable" : cached.Reason), CoopTheme.LabelDim,
                    GUILayout.Width(FieldWidth(150f)));
            }
            else if (_hostInfo.ContainsKey(id))
            {
                if (GUILayout.Button(CoopTheme.IconGlyph("stop") ?? "Stop",
                        CoopTheme.IconButtonDanger, GUILayout.Width(HeaderIconWidth)))
                {
                    core.StopHosting(backend);
                }

                DelayedTooltip("backend-stop:" + id, "Stop hosting on " + backend.DisplayName
                    + " - players who joined through it disconnect.");
            }
            else
            {
                var label = mode == ConnectMode.Host ? "Start hosting on " : "Join through ";

                // Greyed while the backend is working, until it is ready or has failed: pressing again
                // mid-start can only fight the start that is already running. The step it is on is
                // printed in the header just above.
                GUI.enabled = !busy;
                var play = GUILayout.Button(CoopTheme.IconGlyph("play") ?? (mode == ConnectMode.Host
                            ? "Start" : "Join"),
                        CoopTheme.IconButtonStart, GUILayout.Width(HeaderIconWidth));
                GUI.enabled = true;
                if (play)
                {
                    var values = ValueBuffer(backend.Id, FieldsFor(backend, mode));

                    // No address has to be built before starting any more: the host just listens on
                    // its port and a joiner dials the address it typed, so the button connects
                    // straight away for every backend.
                    backend.Configure(values);
                    core.ConnectBackend(backend, mode, values);
                }

                DelayedTooltip("backend-start:" + id, busy
                    ? "Working: " + progress.Summary
                    : label + backend.DisplayName);
            }

            GUILayout.EndHorizontal();

            if (expanded)
            {
                if (available)
                {
                    DrawBackendBody(core, backend, mode);
                }
                else
                {
                    GUILayout.Label("<size=11>Not available right now.</size>", CoopTheme.LabelDim);
                }
            }

            GUILayout.EndVertical();
        }

        private void DrawBackendBody(CoopCore core, INetworkingBackend backend, ConnectMode mode)
        {
            var fields = FieldsFor(backend, mode);
            if (mode == ConnectMode.Host && _hostInfo.TryGetValue(backend.Id, out var hosted))
            {
                // This container is carrying the session, so its options are gone and its connection
                // info takes their place: where the shop is reachable, the password to hand out, and
                // the way to close just this one backend.
                DrawHostingInfo(core, backend, hosted);
                return;
            }

            var values = ValueBuffer(backend.Id, fields);

            // Field-bound actions belong ON their field's row (a small trailing button); the rest
            // are plain panel buttons.
            var actions = backend.Actions;
            Dictionary<FieldId, List<BackendAction>> fieldActions = null;
            var panelActions = new List<BackendAction>();
            for (var i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action.Mode.HasValue && action.Mode.Value != mode)
                {
                    continue; // this button belongs to the other mode
                }

                if (action.Phase == ActionPhase.Live)
                {
                    // The options view is by definition before the session starts, so a live-only
                    // action (Invite friend, the host's copy buttons) has nothing to act on yet. The
                    // live view draws it instead.
                    continue;
                }

                if (!action.Field.HasValue)
                {
                    panelActions.Add(action);
                    continue;
                }

                fieldActions = fieldActions ?? new Dictionary<FieldId, List<BackendAction>>();
                if (!fieldActions.TryGetValue(action.Field.Value, out var list))
                {
                    fieldActions[action.Field.Value] = list = new List<BackendAction>();
                }

                list.Add(action);
            }

            // Push field edits to the backend as they happen. It used to only see them on Start, so
            // anything that runs earlier - an availability probe, the public-address lookup - acted
            // on whatever the last Start had set, which is how a changed port was ignored.
            var before = new Dictionary<FieldId, string>();
            foreach (var pair in values)
            {
                before[pair.Key] = pair.Value;
            }
            foreach (var field in fields)
            {
                List<BackendAction> inline = null;
                fieldActions?.TryGetValue(field.Id, out inline);
                DrawBackendField(backend.Id, values, field, inline);
            }

            if (ValuesChanged(before, values))
            {
                backend.Configure(values);
            }

            var failure = core.BackendError(backend, mode);
            if (!string.IsNullOrEmpty(failure))
            {
                // The header carries the button, so a failed start returns the container to baseline
                // and the reason lives here, under it.
                GUILayout.Label(failure, CoopTheme.LabelDanger);
            }

            foreach (var action in panelActions)
            {
                DrawActionButton(action, false);
                DelayedTooltip("panel:" + backend.Id + ":" + action.Id, ActionTip(action));
            }
        }

        /// <summary>Draws one backend action and fires it when clicked. An action whose icon the UI
        /// can draw becomes a glyph button (its words move into the hover tooltip); otherwise it is the
        /// label as usual, so a machine without an icon font loses nothing but the picture. A row's
        /// last action uses the flush style, whose missing right margin is what lets it line up with
        /// the inputs above it.</summary>
        private static void DrawActionButton(BackendAction action, bool last)
        {
            var glyph = CoopTheme.IconGlyph(action.Icon);
            var style = ActionStyle(action, last);
            GUI.enabled = action.Enabled;
            // Width only for the glyph: the style's own fixed height (shared with every other button in
            // the panel) is what keeps these squares in line with the row they sit in.
            var clicked = glyph != null
                ? GUILayout.Button(glyph, style, GUILayout.Width(IconButtonWidth))
                : GUILayout.Button(action.Label, style, GUILayout.ExpandWidth(false));
            if (clicked)
            {
                action.Invoke?.Invoke();
            }

            GUI.enabled = true;
        }

        private static GUIStyle ActionStyle(BackendAction action, bool last)
        {
            if (CoopTheme.IconGlyph(action.Icon) != null)
            {
                return last ? CoopTheme.IconButtonFlush : CoopTheme.IconButton;
            }

            return last ? CoopTheme.ButtonSecondaryFlush : CoopTheme.ButtonSecondary;
        }

        /// <summary>What one action button takes across a row, margins included - this has to match
        /// what layout really consumes, or the field beside it is sized wrong.</summary>
        private static float ActionWidth(BackendAction action, bool last)
        {
            var style = ActionStyle(action, last);
            return CoopTheme.IconGlyph(action.Icon) != null
                ? IconButtonWidth + style.margin.horizontal
                : style.CalcSize(new GUIContent(action.Label)).x + style.margin.horizontal;
        }

        private void DrawBackendField(string backendId, FieldValues values, BackendField field,
            IReadOnlyList<BackendAction> inlineActions)
        {
            var trailing = InlineActionsWidth(inlineActions);

            values.TryGetValue(field.Id, out var current);
            current ??= field.Value ?? "";

            GUILayout.BeginHorizontal();
            GUILayout.Label(field.Label + ":", CoopTheme.Label, GUILayout.Width(96f));
            DelayedTooltip("label:" + backendId + ":" + field.Id, field.Tooltip);
            var updated = current;
            switch (field.Kind)
            {
                case FieldKind.Toggle:
                    var on = current == "1"
                        || string.Equals(current, "true", StringComparison.OrdinalIgnoreCase);
                    updated = GUILayout.Toggle(on, " on", CoopTheme.Toggle) ? "1" : "0";
                    break;
                case FieldKind.Password:
                    updated = GUILayout.PasswordField(current, '*', 32, CoopTheme.TextField,
                        GUILayout.Width(FieldWidth(96f + trailing)));
                    break;
                default:
                    updated = GUILayout.TextField(current, 64, CoopTheme.TextField,
                        GUILayout.Width(FieldWidth(96f + trailing)));
                    break;
            }

            DrawInlineActions(backendId, field, inlineActions);
            GUILayout.EndHorizontal();
            if (!string.Equals(updated, current, StringComparison.Ordinal))
            {
                values[field.Id] = updated;
            }
        }

        /// <summary>Width the inline action buttons for a field will take, so the field can shrink to
        /// leave room for them instead of pushing them out of the panel. This must be exactly what
        /// layout consumes (margins included, and the last button's right margin already removed by its
        /// flush style) - over-reserving here is what left the last button short of the right edge the
        /// fields above it end on.</summary>
        private static float InlineActionsWidth(IReadOnlyList<BackendAction> actions)
        {
            if (actions == null || actions.Count == 0)
            {
                return 0f;
            }

            var width = 0f;
            for (var i = 0; i < actions.Count; i++)
            {
                width += ActionWidth(actions[i], i == actions.Count - 1);
            }

            return width;
        }

        /// <summary>The small trailing buttons a field owns (Generate / Copy for a reach code).</summary>
        private void DrawInlineActions(string backendId, BackendField field,
            IReadOnlyList<BackendAction> actions)
        {
            if (actions == null)
            {
                return;
            }

            for (var i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                DrawActionButton(action, i == actions.Count - 1);
                DelayedTooltip("field:" + backendId + ":" + field.Id + ":" + action.Id, ActionTip(action));
            }
        }

        /// <summary>The hover text for an action. Once the words are replaced by a glyph the label
        /// only survives here, so it stays in front of the detail.</summary>
        private static string ActionTip(BackendAction action)
        {
            if (CoopTheme.IconGlyph(action.Icon) == null)
            {
                return action.Tooltip;
            }

            return string.IsNullOrEmpty(action.Tooltip)
                ? action.Label
                : action.Label + "\n" + action.Tooltip;
        }

        /// <summary>Hover help that only appears once the pointer has RESTED on the control for a few
        /// seconds: IMGUI's own tooltip pops instantly and covers the panel while the player is just
        /// moving across it. Call immediately after drawing the control; the box is drawn at the very
        /// END of the window so no later control paints over it.</summary>
        private void DelayedTooltip(string key, string text)
        {
            if (string.IsNullOrEmpty(text) || Event.current.type != EventType.Repaint)
            {
                return;
            }

            var rect = GUILayoutUtility.GetLastRect();
            if (!rect.Contains(Event.current.mousePosition))
            {
                if (_tipKey == key)
                {
                    _tipKey = null;
                }

                return;
            }

            if (_tipKey != key)
            {
                _tipKey = key;
                _tipSince = Time.unscaledTime;
                return;
            }

            if (Time.unscaledTime - _tipSince < TooltipDelaySeconds)
            {
                return;
            }

            _pendingTipText = text;
            _pendingTipAt = Event.current.mousePosition;
        }

        /// <summary>Draws the tooltip the last DelayedTooltip call parked, at the end of the window.</summary>
        private void DrawPendingTooltip()
        {
            var text = _pendingTipText;
            _pendingTipText = null;
            if (string.IsNullOrEmpty(text) || Event.current.type != EventType.Repaint)
            {
                return;
            }

            const float TipWidth = 230f;
            var style = CoopTheme.Tooltip;
            var height = style.CalcHeight(new GUIContent(text), TipWidth) + 2f;
            // _pendingTipAt is Event.current.mousePosition inside the window callback, so it is
            // window-local GUI space - clamp against the window WIDTH, not the screen-space xMax.
            var x = Mathf.Min(_pendingTipAt.x + 14f, _win.width - TipWidth - 12f);
            var y = _pendingTipAt.y + 18f;
            GUI.Box(new Rect(x, y, TipWidth, height), text, style);
        }

        /// <summary>
        /// Width for a control that shares its row with a labelled prefix. It comes from the
        /// measured panel (_viewportWidth), NEVER from the control's own content - layout gives a
        /// control a minimum width equal to its content, which is what made a long reach code
        /// widen the whole panel however wide ExpandWidth was told to be.
        /// </summary>
        private float FieldWidth(float labelWidth)
            => Mathf.Max(60f, _viewportWidth - labelWidth - 30f);

        private FieldValues ValueBuffer(string id, IReadOnlyList<BackendField> fields)
        {
            if (!_fieldValues.TryGetValue(id, out var values))
            {
                values = new FieldValues();
                _fieldValues[id] = values;
            }

            foreach (var field in fields)
            {
                if (!values.Contains(field.Id))
                {
                    values[field.Id] = field.Value ?? "";
                }
            }

            return values;
        }

        /// <summary>Probes a backend at most once per refresh window and keeps the LAST KNOWN answer
        /// in between, so the header never flickers back to "checking". The first time a backend
        /// comes back ready it is auto-expanded, so its fields are there without a click; a later
        /// manual collapse sticks.</summary>
        private void RefreshAvailability(INetworkingBackend backend)
        {
            var id = backend.Id;
            if (_availabilityTasks.TryGetValue(id, out var task))
            {
                if (!task.IsCompleted)
                {
                    return;
                }

                _availabilityTasks.Remove(id);
                ApplyAvailability(backend, task.Status == TaskStatus.RanToCompletion
                    ? task.Result
                    : BackendAvailability.No("probe failed"));
                return;
            }

            try
            {
                _availabilityTasks[id] = backend.GetAvailabilityAsync();
            }
            catch (Exception error)
            {
                ApplyAvailability(backend, BackendAvailability.No(error.Message));
            }
        }

        private void ApplyAvailability(INetworkingBackend backend, BackendAvailability result)
        {
            var id = backend.Id;
            _availability[id] = result;

            if (result.Available && _autoExpanded.Add(id))
            {
                _expanded.Add(id);
            }
        }

        /// <summary>
        /// What a container shows once its backend is carrying the session: that backend's own
        /// connection info in place of its options. The options have already been applied, so the
        /// container's job now is to say where the shop is reachable, hand the password over, and let
        /// this one backend be closed on its own. There is deliberately no separate hosting view.
        /// </summary>
        private void DrawHostingInfo(CoopCore core, INetworkingBackend backend, ConnectionInfo info)
        {
            // The container header already carries the backend's name, so this is just the info.
            GUILayout.Label(info.Summary, CoopTheme.Label);

            if (!string.IsNullOrEmpty(info.Password))
            {
                GUILayout.Label("<size=11>session password: <b>" + info.Password + "</b></size>",
                    CoopTheme.LabelDim);
            }

            if (!string.IsNullOrEmpty(info.Warning))
            {
                GUILayout.Label("<size=11>" + info.Warning + "</size>", CoopTheme.LabelDimWrap);
            }

            if (info.ForwardState == ForwardState.Mapped)
            {
                GUILayout.Label("<size=10>your router opened the port</size>", CoopTheme.LabelDim);
            }
            else if (info.ForwardState == ForwardState.Declined)
            {
                GUILayout.Label("<size=10>your router wouldn't open the port - forward it yourself</size>",
                    CoopTheme.LabelDim);
            }

            foreach (var action in backend.Actions)
            {
                if (action.Mode.HasValue && action.Mode.Value != ConnectMode.Host)
                {
                    continue; // join-side button; this panel is hosting
                }

                if (action.Phase == ActionPhase.BeforeStart || action.Field.HasValue)
                {
                    continue; // options-only, or drawn on its own field's row
                }

                GUI.enabled = action.Enabled;
                if (GUILayout.Button(action.Label, CoopTheme.ButtonSecondary))
                {
                    action.Invoke?.Invoke();
                }

                GUI.enabled = true;
            }
        }

        /// <summary>The two controls that belong to the session rather than to any single backend,
        /// drawn under the containers so there is no hosting view above them.</summary>
        private void DrawSessionActions(CoopCore core)
        {
            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Wave  (" + CoopPlugin.EmoteKey.Value + ")", CoopTheme.ButtonSecondary))
            {
                PresenceApi.SendEmote();
            }

            if (GUILayout.Button("Stop hosting everything", CoopTheme.ButtonDanger))
            {
                core.Disconnect();
            }

            GUILayout.EndHorizontal();
        }

        /// <summary>Which backends are carrying the session, and what they publish, snapped once per
        /// frame by <see cref="SnapshotHosting"/>, along with the field lists themselves.</summary>
        private readonly Dictionary<string, ConnectionInfo> _hostInfo = new();

        /// <summary>Every backend's <see cref="ConnectionInfo"/> for this frame, hosting or not: the
        /// container header reads a start's current step and its busy flag from here.</summary>
        private readonly Dictionary<string, ConnectionInfo> _backendInfo = new();

        private readonly Dictionary<string, IReadOnlyList<BackendField>> _fields = new();

        private void DrawClient(CoopCore core)
        {
            GUILayout.BeginVertical(CoopTheme.SectionBox, GUILayout.ExpandHeight(true));
            GUILayout.Label("IN SESSION", CoopTheme.SectionHeader);

            if (ShopApi.TryGetShopName(out var shopName))
            {
                GUILayout.Label("Shop: <b>" + SafeLabel(shopName) + "</b>", CoopTheme.Label);
            }

            DrawHostVersion(core);

            // Snapshot on the layout pass: PresenceApi.SessionPlayers allocates, and the control set
            // must be identical between Layout and Repaint.
            if (Event.current.type == EventType.Layout)
            {
                _clientPlayers.Clear();
                _clientPlayers.AddRange(PresenceApi.SessionPlayers);
            }

            GUILayout.Label("Players", CoopTheme.LabelDim);
            for (var i = 0; i < _clientPlayers.Count; i++)
            {
                DrawPlayerRow(_clientPlayers[i], core, allowKick: false);
            }

            GUILayout.Space(4f);
            if (GUILayout.Button("Wave  (" + CoopPlugin.EmoteKey.Value + ")", CoopTheme.ButtonSecondary))
            {
                PresenceApi.SendEmote();
            }

            if (GUILayout.Button("Leave session", CoopTheme.ButtonDanger))
            {
                core.Disconnect();
            }

            GUILayout.EndVertical();
        }

        /// <summary>This frame's roster, latched on the layout pass (the source allocates).</summary>
        private readonly List<PresencePlayer> _clientPlayers = new();
        private readonly List<PresencePlayer> _hostPlayers = new();

        /// <summary>The HOST's session view: everyone connected, with a Kick button per client.
        /// Drawn at the top of the host's Session tab.</summary>
        private void DrawHostPlayers(CoopCore core)
        {
            if (Event.current.type == EventType.Layout)
            {
                _hostPlayers.Clear();
                _hostPlayers.AddRange(PresenceApi.SessionPlayers);
            }

            GUILayout.BeginVertical(CoopTheme.SectionBox);
            GUILayout.Label("PLAYERS", CoopTheme.SectionHeader);
            if (_hostPlayers.Count <= 1)
            {
                GUILayout.Label("Waiting for players\u2026", CoopTheme.LabelDim);
            }

            for (var i = 0; i < _hostPlayers.Count; i++)
            {
                DrawPlayerRow(_hostPlayers[i], core, allowKick: true);
            }

            GUILayout.EndVertical();
        }

        /// <summary>The host's build, as advertised in its Welcome: the mod version (always equal to
        /// ours - the handshake enforces it) and the game/Unity build (which CAN differ on a
        /// cross-build join, and is the interesting fact).</summary>
        private static void DrawHostVersion(CoopCore core)
        {
            var plugin = core.HostVersion;
            var game = core.HostGameVersion;
            var unity = core.HostUnityVersion;
            if (string.IsNullOrEmpty(plugin) && string.IsNullOrEmpty(game))
            {
                return;
            }

            var text = "Host version: " + (string.IsNullOrEmpty(plugin) ? "?" : "v" + plugin);
            if (!string.IsNullOrEmpty(game))
            {
                text += "   game " + game;
            }
            if (!string.IsNullOrEmpty(unity))
            {
                text += " / Unity " + unity;
            }

            GUILayout.Label(SafeLabel(text), CoopTheme.LabelDim);
        }

        /// <summary>One roster row: the baked crown for the host, the name (with "(you)" for this
        /// player) and, for a client, the build it advertised, plus on the host's side a Kick button.
        /// Every row reserves the crown's width so the names line up.</summary>
        private static void DrawPlayerRow(PresencePlayer player, CoopCore core, bool allowKick)
        {
            GUILayout.BeginHorizontal();
            DrawCrown(player.IsHost);

            GUILayout.Label(SafeLabel(player.Name ?? "Player") + (player.IsSelf ? "  (you)" : ""),
                CoopTheme.Label);

            // The build rides the same line as the name, so a row is one line per player.
            var build = BuildLineFor(core, player);
            if (!string.IsNullOrEmpty(build))
            {
                GUILayout.Label(build, CoopTheme.LabelDim);
            }

            GUILayout.FlexibleSpace();

            // A host may kick any client; the host itself and this player are never kickable.
            if (allowKick && !player.IsHost && !player.IsSelf
                && GUILayout.Button("Kick", CoopTheme.ButtonDanger, GUILayout.Width(56f)))
            {
                core.KickPeer(player.Id);
            }

            GUILayout.EndHorizontal();
        }

        /// <summary>The build line for one roster row, or null. Only a CLIENT row carries one: the
        /// build a client advertised in its Hello is known to the host, and the HOST row's build is
        /// shown separately by the client's "Host version" line.</summary>
        private static string BuildLineFor(CoopCore core, PresencePlayer player)
        {
            if (player.IsHost)
            {
                return null;
            }

            return core.TryGetPeerBuild(player.Id, out var build)
                ? BuildLine(build.Version, build.GameVersion)
                : null;
        }

        private static string BuildLine(string version, string game)
        {
            // Compact, for an inline row: "v2.0.7 · 1.00".
            var parts = new List<string>(2);
            if (!string.IsNullOrEmpty(version))
            {
                parts.Add(SafeLabel("v" + version));
            }
            if (!string.IsNullOrEmpty(game))
            {
                parts.Add(SafeLabel(game));
            }

            return parts.Count == 0 ? null : string.Join(" \u00b7 ", parts);
        }

        /// <summary>IMGUI labels render rich text, and a shop or player name can come off the wire
        /// (the rename stream, the presence roster, a peer's advertised build), so markup must never
        /// be injectable. Angle brackets are the only markup delimiter, so they are swapped for
        /// lookalikes - the text still reads the same and cannot open a tag.</summary>
        private static string SafeLabel(string text)
            => string.IsNullOrEmpty(text) ? text : text.Replace('<', '\u2039').Replace('>', '\u203a');

        /// <summary>The baked crown on the host's row. EVERY row reserves the same box - a layout
        /// control, not Space - so the names line up exactly; a non-host row just leaves it empty.</summary>
        private static void DrawCrown(bool isHost)
        {
            var rect = GUILayoutUtility.GetRect(CoopTheme.CrownSize, CoopTheme.CrownSize,
                GUILayout.Width(CoopTheme.CrownSize), GUILayout.Height(CoopTheme.CrownSize));
            if (isHost && CoopTheme.CrownTexture != null && Event.current.type == EventType.Repaint)
            {
                GUI.DrawTexture(rect, CoopTheme.CrownTexture, ScaleMode.ScaleToFit, true);
            }
        }
        /// <summary>Graded-album repair offers, latched in the layout pass (see below).</summary>
        private readonly List<GradingOffer> _adoptOffers = new();

        private void DrawGradedAdopt(CoopCore core)
        {
            if (Event.current.type == EventType.Layout)
            {
                _adoptOffers.Clear();
                _adoptOffers.AddRange(GradingUiApi.Offers);
            }
            if (_adoptOffers.Count == 0)
            {
                return;
            }

            CoopTheme.Divider();
            GUILayout.BeginVertical(CoopTheme.SectionBox);
            GUILayout.BeginHorizontal();
            CoopTheme.Chip("GRADED ALBUM", CoopTheme.ChipWarn);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            // Must state the guard EXACTLY as GradedAdopt enforces it: refusal is on certificate
            // PRESENCE, not on "a different card". Promising the narrower rule invited the press
            // that duplicated a card this PC already held under Grading Overhaul's FAKE flag.
            GUILayout.Label("<size=11>Only missing graded cards will be added; existing certificates are skipped.</size>",
                CoopTheme.LabelWrap);
            for (var i = 0; i < _adoptOffers.Count; i++)
            {
                var o = _adoptOffers[i];
                if (GUILayout.Button($"Adopt {o.Count} graded card(s) {o.Who} has that you don't", CoopTheme.ButtonSecondary))
                {
                    GradingUiApi.Adopt(o.ConnectionId);
                }
            }
            GUILayout.EndVertical();
        }
    }
}




