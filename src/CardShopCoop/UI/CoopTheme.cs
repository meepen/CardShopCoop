using System;
using UnityEngine;

namespace CardShopCoop.UI
{
    /// <summary>
    /// Cozy "card-shop paper + teal" IMGUI theme for the co-op window and its HUD overlays.
    ///
    /// Everything here is built ONCE (lazily, inside OnGUI where GUI.skin and the graphics
    /// device are valid) and cached. Textures are generated at runtime with analytic
    /// anti-aliased rounded corners and 9-sliced via GUIStyle.border so they scale crisp at
    /// any size. They are marked HideFlags.HideAndDontSave so they survive scene loads and are
    /// never touched by Resources.UnloadUnusedAssets - we never rebuild them per frame.
    ///
    /// Public surface used by CoopUI:
    ///   CoopTheme.EnsureBuilt();                       // call early in OnGUI
    ///   CoopTheme.Window / Label / ButtonSecondary / ...  // GUIStyle fields
    ///   CoopTheme.DrawWindowShadow(Rect win);          // soft drop shadow (screen space)
    ///   CoopTheme.DrawWindowChrome(Rect r, title, ver);// header strip + title + version
    ///   CoopTheme.Divider();                           // 1px horizontal rule
    ///   CoopTheme.Chip(text, chipStyle);               // a pill
    ///   CoopTheme.PillSize(style, content, maxW);      // sized rect for a HUD pill
    /// </summary>
    public static class CoopTheme
    {
        // ---- palette (warm card-shop paper + teal) --------------------------------------
        public static readonly Color Panel = Rgb(27, 29, 33, 0.98f);
        public static readonly Color PanelBorder = Rgb(62, 67, 75);
        public static readonly Color HeaderBg = Rgb(31, 52, 55);
        public static readonly Color HeaderText = Rgb(232, 236, 239);
        public static readonly Color Text = Rgb(229, 233, 236);
        public static readonly Color TextDim = Rgb(156, 164, 171);
        public static readonly Color SectionBg = Rgb(37, 40, 45, 0.96f);
        public static readonly Color SectionBorder = Rgb(58, 63, 70);
        public static readonly Color Primary = Rgb(62, 158, 140);        // #3E9E8C
        public static readonly Color PrimaryHover = Rgb(71, 178, 158);        // #47B29E
        public static readonly Color Secondary = Rgb(48, 52, 58);
        public static readonly Color SecondaryHover = Rgb(59, 64, 71);
        public static readonly Color SecondaryBorder = Rgb(74, 80, 88);
        public static readonly Color Danger = Rgb(218, 91, 78);
        public static readonly Color Warn = Rgb(226, 165, 72);
        public static readonly Color Success = Rgb(88, 190, 109);
        public static readonly Color DividerCol = Rgb(62, 67, 75);
        public static readonly Color FieldBg = Rgb(46, 50, 56);
        public static readonly Color FieldBorder = Rgb(72, 78, 86);
        public static readonly Color HudBg = Rgb(31, 29, 26, 0.78f);  // #1F1D1A

        // ---- built styles (all created once in EnsureBuilt) -----------------------------
        public static GUIStyle Window, HeaderStrip, Header, HeaderVersion, SectionHeader;
        public static GUIStyle Label, LabelDim, LabelDimWrap, LabelBold, LabelWrap, LabelDanger, LabelWarn;
        public static GUIStyle SectionBox, ContentPanel, ScrollView, ScrollContent, Toggle, Tab, TabSelected;
        public static GUIStyle Tooltip;

        /// <summary>Font used for button glyphs, resolved from <see cref="IconFontNames"/> against the
        /// fonts actually installed. Null when this machine has none of them, in which case
        /// <see cref="IconGlyph"/> hands out nothing and callers fall back to words.</summary>
        public static Font IconFont;

        /// <summary>Small square button that draws a single glyph, for field actions that would
        /// otherwise be a word each.</summary>
        public static GUIStyle IconButton;
        public static GUIStyle ButtonSecondary, ButtonDanger;

        /// <summary>Twins of the buttons above with the right margin removed, for the LAST control in
        /// a row. A margin is invisible space, so a trailing one leaves that control's right edge short
        /// of the inputs it is meant to line up with.</summary>
        public static GUIStyle ButtonSecondaryFlush, IconButtonFlush;

        /// <summary>The start/stop twins of <see cref="IconButton"/>: a glyph button in the success
        /// colour for starting and the danger colour for stopping, so the one control in a container
        /// header reads at a glance.</summary>
        public static GUIStyle IconButtonStart, IconButtonDanger;
        public static GUIStyle TextField;
        public static GUIStyle ChipSuccess, ChipWarn, ChipInfo;
        public static GUIStyle HudPill, HudPillBig;

        // ---- cached textures ------------------------------------------------------------
        private static Texture2D _panelTex, _headerTex, _shadowTex, _fieldTex, _fieldFocusTex,
            _sectionTex, _contentTex, _tabTex, _tabHoverTex, _tabSelectedTex, _dotOffTex, _dotWaitTex, _dotOkTex,
            _dividerTex, _hudTex;
        private static Texture2D _secondaryTex, _secondaryHoverTex, _secondaryActiveTex;
        private static Texture2D _dangerTex, _dangerHoverTex, _dangerActiveTex;
        private static Texture2D _successTex, _successHoverTex, _successActiveTex;
        private static Texture2D _chipSuccessTex, _chipWarnTex, _chipInfoTex;
        private static Texture2D _crownTex;   // baked crown for the host's roster row

        private static bool _built;

        // corner radii / texture sizes (small; 9-sliced up crisp)
        private const int WinTex = 28, WinRadius = 10;
        private const int CtlTex = 16, CtlRadius = 6;
        private const int SecTex = 20, SecRadius = 8;
        private const int PillTex = 20, PillRadius = 8;
        private const int HudTexSz = 32, HudRadius = 14;
        public const int CrownSize = 18;   // baked crown icon for the host's roster row

        public static void EnsureBuilt()
        {
            if (_built)
            {
                return;
            }

            _built = true;

            // ---- textures -----------------------------------------------------------
            _panelTex = MakeRounded(WinTex, WinTex, WinRadius, Panel, PanelBorder, 1);
            _headerTex = MakeRoundedTop(WinTex, WinTex, WinRadius, HeaderBg);
            _shadowTex = MakeShadow(40, 40, 12, 0.35f, 4f);
            _fieldTex = MakeRounded(CtlTex, CtlTex, CtlRadius, FieldBg, FieldBorder, 1);
            _fieldFocusTex = MakeRounded(CtlTex, CtlTex, CtlRadius, FieldBg, Primary, 1);
            _sectionTex = MakeRounded(SecTex, SecTex, SecRadius, SectionBg, SectionBorder, 1);
            _contentTex = MakeRoundedBottom(SecTex, SecTex, SecRadius, Panel, PanelBorder, 1);
            _tabTex = MakeRoundedTop(SecTex, SecTex, SecRadius, Secondary);
            _tabHoverTex = MakeRoundedTop(SecTex, SecTex, SecRadius, SecondaryHover);
            _tabSelectedTex = MakeRoundedTop(SecTex, SecTex, SecRadius, Primary);
            _dotOffTex = MakeRounded(16, 16, 8, TextDim, TextDim, 0);
            _dotWaitTex = MakeRounded(16, 16, 8, Warn, Warn, 0);
            _dotOkTex = MakeRounded(16, 16, 8, Success, Success, 0);
            _crownTex = MakeCrown(CrownSize, Warn);
            _dividerTex = Solid(DividerCol);
            _hudTex = MakeRounded(HudTexSz, HudTexSz, HudRadius, HudBg, HudBg, 0);

            _secondaryTex = MakeRounded(CtlTex, CtlTex, CtlRadius, Secondary, SecondaryBorder, 1);
            _secondaryHoverTex = MakeRounded(CtlTex, CtlTex, CtlRadius, SecondaryHover, SecondaryBorder, 1);
            _secondaryActiveTex = MakeRounded(CtlTex, CtlTex, CtlRadius, Mul(Secondary, 0.94f), SecondaryBorder, 1);

            _dangerTex = MakeRounded(CtlTex, CtlTex, CtlRadius, Danger, Mul(Danger, 0.85f), 1);
            _dangerHoverTex = MakeRounded(CtlTex, CtlTex, CtlRadius, Lerp(Danger, Color.white, 0.10f), Mul(Danger, 0.85f), 1);
            _dangerActiveTex = MakeRounded(CtlTex, CtlTex, CtlRadius, Mul(Danger, 0.88f), Mul(Danger, 0.80f), 1);

            _successTex = MakeRounded(CtlTex, CtlTex, CtlRadius, Success, Mul(Success, 0.85f), 1);
            _successHoverTex = MakeRounded(CtlTex, CtlTex, CtlRadius, Lerp(Success, Color.white, 0.10f), Mul(Success, 0.85f), 1);
            _successActiveTex = MakeRounded(CtlTex, CtlTex, CtlRadius, Mul(Success, 0.88f), Mul(Success, 0.80f), 1);

            _chipSuccessTex = MakeRounded(PillTex, PillTex, PillRadius, WithA(Success, 0.18f), WithA(Success, 0.55f), 1);
            _chipWarnTex = MakeRounded(PillTex, PillTex, PillRadius, WithA(Warn, 0.18f), WithA(Warn, 0.55f), 1);
            _chipInfoTex = MakeRounded(PillTex, PillTex, PillRadius, WithA(Primary, 0.18f), WithA(Primary, 0.55f), 1);

            // ---- styles -------------------------------------------------------------
            Window = new GUIStyle(GUI.skin.window)
            {
                richText = true,
                padding = new RectOffset(14, 14, 40, 12), // top 40 leaves room for the header strip
                border = new RectOffset(WinRadius, WinRadius, WinRadius, WinRadius)
            };
            Window.normal.background = _panelTex;
            Window.onNormal.background = _panelTex;
            Window.focused.background = _panelTex;
            Window.onFocused.background = _panelTex;
            Window.hover.background = _panelTex;
            Window.active.background = _panelTex;
            Window.normal.textColor = Clear();      // title is empty; we draw our own
            Window.onNormal.textColor = Clear();

            HeaderStrip = new GUIStyle
            {
                border = new RectOffset(WinRadius, WinRadius, WinRadius, 3)
            };
            HeaderStrip.normal.background = _headerTex;

            Header = new GUIStyle(GUI.skin.label)
            {
                richText = true,
                fontStyle = FontStyle.Bold,
                fontSize = 15,
                alignment = TextAnchor.MiddleLeft
            };
            Header.normal.textColor = HeaderText;

            HeaderVersion = new GUIStyle(GUI.skin.label)
            {
                richText = true,
                fontSize = 11,
                alignment = TextAnchor.MiddleRight
            };
            HeaderVersion.normal.textColor = WithA(HeaderText, 0.80f);

            SectionHeader = new GUIStyle(GUI.skin.label)
            {
                richText = true,
                fontStyle = FontStyle.Bold,
                fontSize = 12,
                margin = new RectOffset(0, 0, 0, 4)
            };
            SectionHeader.normal.textColor = PrimaryHover;

            Label = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 12 };
            Label.normal.textColor = Text;

            LabelDim = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 11 };
            LabelDim.normal.textColor = TextDim;

            LabelBold = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 12, fontStyle = FontStyle.Bold };
            LabelBold.normal.textColor = Text;

            LabelWrap = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 12, wordWrap = true };
            LabelWrap.normal.textColor = Text;

            // LabelDim, but wrapping. LabelDim has no wordWrap, so a dim line longer than the
            // 400px panel is simply clipped at the edge instead of running onto a second row -
            // fine for the short status blips it was built for, wrong for anything that has to
            // give the player an instruction (see the UPnP-declined hint in CoopUI).
            LabelDimWrap = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 11, wordWrap = true };
            LabelDimWrap.normal.textColor = TextDim;

            LabelDanger = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 12, wordWrap = true };
            LabelDanger.normal.textColor = Danger;

            LabelWarn = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 12, wordWrap = true };
            LabelWarn.normal.textColor = Warn;

            SectionBox = new GUIStyle(GUI.skin.box)
            {
                border = new RectOffset(SecRadius, SecRadius, SecRadius, SecRadius),
                padding = new RectOffset(10, 10, 10, 10),
                margin = new RectOffset(0, 0, 4, 4)
            };
            SectionBox.normal.background = _sectionTex;

            // Hover tooltips (shown by the UI only after the pointer has rested on a control).
            Tooltip = new GUIStyle(GUI.skin.box)
            {
                richText = true,
                fontSize = 11,
                wordWrap = true,
                padding = new RectOffset(6, 6, 5, 5),
                border = new RectOffset(SecRadius, SecRadius, SecRadius, SecRadius)
            };
            Tooltip.normal.background = _sectionTex;
            Tooltip.normal.textColor = Text;

            ContentPanel = new GUIStyle(GUI.skin.box)
            {
                border = new RectOffset(SecRadius, SecRadius, SecRadius, SecRadius),
                padding = new RectOffset(10, 10, 10, 10),
                margin = new RectOffset(0, 0, 0, 4)
            };
            ContentPanel.normal.background = _contentTex;

            // Keep the scroll group's overflow calculation equal to the actual content height.
            // Unity's default scrollView padding and GUIStyle.none's child-margin bubbling can
            // otherwise make a fixed-height content group appear a few pixels too tall.
            ScrollView = new GUIStyle(GUI.skin.scrollView)
            {
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0)
            };
            ScrollContent = new GUIStyle
            {
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0)
            };

            Tab = MakeTab(_tabTex, _tabHoverTex, TextDim);
            TabSelected = MakeTab(_tabSelectedTex, _tabSelectedTex, Color.white);

            // wordWrap: a wrapping label's minimum layout width is its longest WORD, not the whole
            // line. Without it a long checkbox label (the cross-build join opt-in on the SETTINGS
            // tab is the one that exposed this) gives the toggle a minimum width equal to the full
            // text, which in turn widens the shared scroll content past the viewport and clips the
            // whole panel. Wrapping keeps the toggle inside the panel instead.
            Toggle = new GUIStyle(GUI.skin.toggle) { richText = true, fontSize = 12, wordWrap = true };
            SetTextColorAllStates(Toggle, Text);

            ButtonSecondary = MakeButton(_secondaryTex, _secondaryHoverTex, _secondaryActiveTex, Text);
            ButtonDanger = MakeButton(_dangerTex, _dangerHoverTex, _dangerActiveTex, Color.white);

            // Action icons. These are button STYLES, so they must be cloned from a button that
            // already exists - cloning ButtonSecondary before it was built produced an unstyled
            // (default-constructed) style with no background and no text colour, which is exactly
            // what a bare, wrongly-coloured glyph in the row looks like.
            //
            // The glyph itself needs a font the IMGUI default does not have: Arial covers none of
            // these code points, and Unity cannot tell us at runtime whether a font covers a glyph
            // (Font.HasCharacter returns true for every dynamic font), so guessing a font would be a
            // silent missing-glyph box. If no icon font is installed, IconGlyph hands out nothing and
            // the buttons say their words instead.
            IconFont = FindIconFont();
            if (IconFont == null)
            {
                // Once, at startup: from here on the buttons quietly read as words, and this is the
                // answer to "why are there no icons".
                CoopPlugin.Log?.LogInfo("No icon font installed (tried "
                    + string.Join(", ", IconFontNames)
                    + "); backend action buttons will show their labels instead of icons.");
            }
            else
            {
                CoopPlugin.Log?.LogInfo("Backend action icons will use " + IconFont.name + ".");
            }

            IconButton = new GUIStyle(ButtonSecondary)
            {
                fontSize = IconGlyphSize,
                fontStyle = FontStyle.Normal, // an icon font has no bold face to ask for
                padding = new RectOffset(0, 0, 0, 0)
            };
            if (IconFont != null)
            {
                IconButton.font = IconFont;
                CentreGlyphInk(IconButton, GlyphRefresh);
            }

            ButtonSecondaryFlush = new GUIStyle(ButtonSecondary)
            {
                margin = new RectOffset(ButtonSecondary.margin.left, 0,
                    ButtonSecondary.margin.top, ButtonSecondary.margin.bottom)
            };
            IconButtonFlush = new GUIStyle(IconButton)
            {
                margin = new RectOffset(IconButton.margin.left, 0,
                    IconButton.margin.top, IconButton.margin.bottom)
            };
            IconButtonDanger = new GUIStyle(ButtonDanger)
            {
                fontSize = IconGlyphSize,
                fontStyle = FontStyle.Normal,
                padding = new RectOffset(0, 0, 0, 0)
            };

            // Green for go, so a container header reads at a glance: play green, stop red.
            var success = MakeButton(_successTex, _successHoverTex, _successActiveTex, Color.white);
            IconButtonStart = new GUIStyle(success)
            {
                fontSize = IconGlyphSize,
                fontStyle = FontStyle.Normal,
                padding = new RectOffset(0, 0, 0, 0)
            };
            if (IconFont != null)
            {
                IconButtonDanger.font = IconFont;
                CentreGlyphInk(IconButtonDanger, GlyphStop);
                IconButtonStart.font = IconFont;
                CentreGlyphInk(IconButtonStart, GlyphPlay);
            }

            TextField = new GUIStyle(GUI.skin.textField)
            {
                richText = false, // user types literal text; never parse it as markup
                fontSize = 12,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(6, 6, 4, 4),
                border = new RectOffset(CtlRadius, CtlRadius, CtlRadius, CtlRadius)
            };
            TextField.normal.background = _fieldTex;
            TextField.hover.background = _fieldTex;
            TextField.focused.background = _fieldFocusTex;
            SetTextColorAllStates(TextField, Text);

            ChipSuccess = MakeChip(_chipSuccessTex, Success);
            ChipWarn = MakeChip(_chipWarnTex, Mul(Warn, 0.92f));
            // Info chips use the teal accent like the other chips use their hue; the old
            // HeaderBg text was near-black on a near-black background and unreadable.
            ChipInfo = MakeChip(_chipInfoTex, PrimaryHover);

            HudPill = new GUIStyle
            {
                richText = true,
                wordWrap = true,
                fontSize = 13,
                alignment = TextAnchor.MiddleCenter,
                border = new RectOffset(HudRadius, HudRadius, HudRadius, HudRadius),
                // vertical padding keeps the pill >= 2*radius tall so the 9-slice corners never squish
                padding = new RectOffset(14, 14, 9, 9)
            };
            HudPill.normal.background = _hudTex;
            HudPill.normal.textColor = Color.white;

            HudPillBig = new GUIStyle(HudPill)
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(18, 18, 10, 10)
            };
            HudPillBig.normal.background = _hudTex;
            HudPillBig.normal.textColor = Color.white;
        }

        // --------------------------------------------------------------------------------
        // Public draw helpers
        // --------------------------------------------------------------------------------

        /// <summary>Soft rounded drop shadow, offset (3,4) behind the window. MUST be drawn in
        /// screen space (from Draw, before GUILayout.Window) - a shadow drawn inside the window
        /// callback would be clipped to the window rect and never show the offset sliver.</summary>
        public static void DrawWindowShadow(Rect win)
        {
            if (_shadowTex == null)
            {
                return;
            }

            var r = new Rect(win.x + 3f, win.y + 4f, win.width, win.height);
            GUI.Box(r, GUIContent.none, HeaderShadow());
        }

        private static GUIStyle _shadowStyle;
        private static GUIStyle HeaderShadow()
        {
            if (_shadowStyle == null)
            {
                _shadowStyle = new GUIStyle { border = new RectOffset(16, 16, 16, 16) };
                _shadowStyle.normal.background = _shadowTex;
            }
            return _shadowStyle;
        }

        private static GUIContent _titleGc, _versionGc;

        /// <summary>Header strip (rounded-top teal bar), left-aligned title, right-aligned
        /// version. Called INSIDE the window callback with r = (0,0,width,height) in local
        /// coordinates.</summary>
        public static void DrawWindowChrome(Rect r, string title, string version)
        {
            const float stripH = 30f;
            GUI.Box(new Rect(0f, 0f, r.width, stripH), GUIContent.none, HeaderStrip);

            if (_titleGc == null || _titleGc.text != title)
            {
                _titleGc = new GUIContent(title);
            }

            GUI.Label(new Rect(13f, 5f, r.width - 26f, 20f), _titleGc, Header);

            if (_versionGc == null || _versionGc.text != version)
            {
                _versionGc = new GUIContent(version);
            }
            // Keep the version immediately left of the connection indicator in the window
            // header instead of letting the indicator sit on top of the version text.
            GUI.Label(new Rect(13f, 6f, r.width - 52f, 18f), _versionGc, HeaderVersion);
        }

        /// <summary>1px horizontal rule across the current layout width.</summary>
        public static void Divider()
        {
            GUILayout.Space(3f);
            var r = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true), GUILayout.Height(1f));
            if (Event.current.type == EventType.Repaint)
            {
                GUI.DrawTexture(r, _dividerTex);
            }

            GUILayout.Space(3f);
        }

        /// <summary>A pill: a small tinted, rounded label that hugs its content.</summary>
        public static void Chip(string text, GUIStyle chipStyle)
        {
            GUILayout.Label(text, chipStyle, GUILayout.ExpandWidth(false));
        }

        /// <summary>Content-sized dimensions for a HUD pill, wrapping to maxW if the text is
        /// wider than that.</summary>
        public static Vector2 PillSize(GUIStyle style, GUIContent content, float maxW)
        {
            var s = style.CalcSize(content);
            if (s.x > maxW)
            {
                s.x = maxW;
                s.y = style.CalcHeight(content, maxW);
            }
            return s;
        }

        // --------------------------------------------------------------------------------
        // Style factories
        // --------------------------------------------------------------------------------

        private static GUIStyle MakeButton(Texture2D bg, Texture2D hover, Texture2D active, Color textColor)
        {
            var s = new GUIStyle(GUI.skin.button)
            {
                richText = true,
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                fixedHeight = 26f,
                padding = new RectOffset(10, 10, 4, 4),
                margin = new RectOffset(2, 2, 3, 3),
                border = new RectOffset(CtlRadius, CtlRadius, CtlRadius, CtlRadius)
            };
            s.normal.background = bg;
            s.normal.textColor = textColor;
            s.hover.background = hover;
            s.hover.textColor = textColor;
            s.active.background = active;
            s.active.textColor = textColor;
            s.focused.background = bg;
            s.focused.textColor = textColor;
            s.onNormal.background = bg;
            s.onNormal.textColor = textColor;
            s.onHover.background = hover;
            s.onHover.textColor = textColor;
            s.onActive.background = active;
            s.onActive.textColor = textColor;
            s.onFocused.background = bg;
            s.onFocused.textColor = textColor;
            return s;
        }

        /// <summary>Small title-bar connection indicator: 0 offline, 1 waiting/connecting,
        /// 2 connected/hosting.</summary>
        public static void DrawConnectionIndicator(Rect rect, int state)
        {
            var texture = state >= 2 ? _dotOkTex : state == 1 ? _dotWaitTex : _dotOffTex;
            if (texture != null)
            {
                GUI.DrawTexture(rect, texture, ScaleMode.StretchToFill, true);
            }
        }

        private static GUIStyle MakeTab(Texture2D bg, Texture2D hover, Color textColor)
        {
            var s = new GUIStyle(GUI.skin.button)
            {
                richText = false,
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                fixedHeight = 30f,
                padding = new RectOffset(14, 14, 4, 4),
                margin = new RectOffset(0, 2, 2, 0),
                border = new RectOffset(SecRadius, SecRadius, SecRadius, 0)
            };
            s.normal.background = bg;
            s.normal.textColor = textColor;
            s.hover.background = hover;
            s.hover.textColor = textColor;
            s.active.background = hover;
            s.active.textColor = textColor;
            s.focused.background = bg;
            s.focused.textColor = textColor;
            return s;
        }

        private static GUIStyle MakeChip(Texture2D bg, Color textColor)
        {
            var s = new GUIStyle(GUI.skin.label)
            {
                richText = true,
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(8, 8, 3, 3),
                margin = new RectOffset(0, 0, 2, 2),
                border = new RectOffset(PillRadius, PillRadius, PillRadius, PillRadius)
            };
            s.normal.background = bg;
            s.normal.textColor = textColor;
            return s;
        }

        private static void SetTextColorAllStates(GUIStyle s, Color c)
        {
            s.normal.textColor = c;
            s.hover.textColor = c;
            s.active.textColor = c;
            s.focused.textColor = c;
            s.onNormal.textColor = c;
            s.onHover.textColor = c;
            s.onActive.textColor = c;
            s.onFocused.textColor = c;
        }

        // --------------------------------------------------------------------------------
        // Texture generation (analytic anti-aliased rounded rects)
        // --------------------------------------------------------------------------------

        /// <summary>Rounded rect with all four corners rounded. Per-pixel alpha comes from the
        /// signed distance to the rounded-rect edge (1px smoothstep), so the corners are
        /// anti-aliased. Built small and 9-sliced by the caller via GUIStyle.border.</summary>
        public static Texture2D MakeRounded(int w, int h, int radius, Color fill, Color border, int borderWidth)
        {
            return MakeCore(w, h, radius, radius, radius, radius, fill, border, borderWidth);
        }

        /// <summary>Rounded TOP corners only (square bottom) - the header strip sits flush on
        /// the window's straight header line while echoing the rounded panel top.</summary>
        private static Texture2D MakeRoundedTop(int w, int h, int radius, Color fill)
        {
            return MakeCore(w, h, radius, radius, 0, 0, fill, fill, 0);
        }

        private static Texture2D MakeRoundedBottom(int w, int h, int radius, Color fill, Color border, int borderWidth)
        {
            return MakeCore(w, h, 0, 0, radius, radius, fill, border, borderWidth);
        }

        private static Texture2D MakeCore(int w, int h, float rTL, float rTR, float rBR, float rBL,
            Color fill, Color border, float bw)
        {
            var px = new Color[w * h];
            float bx = w * 0.5f, by = h * 0.5f;
            for (var srow = 0; srow < h; srow++)   // srow 0 = top on screen
            {
                var py = (srow + 0.5f) - by;     // py > 0 => toward bottom
                var arrayRow = h - 1 - srow;        // Texture2D row 0 is bottom
                for (var col = 0; col < w; col++)
                {
                    var pxpos = (col + 0.5f) - bx;
                    var d = SdRoundBox(pxpos, py, bx, by, rTL, rTR, rBR, rBL);
                    var aOuter = Coverage(d);
                    var aInner = bw > 0f ? Coverage(d + bw) : aOuter;
                    var borderFrac = Mathf.Max(0f, aOuter - aInner);
                    var fillFrac = aInner;
                    var outA = fill.a * fillFrac + border.a * borderFrac;
                    Color outC;
                    if (outA <= 0.0001f)
                    {
                        outC = new Color(fill.r, fill.g, fill.b, 0f);
                    }
                    else
                    {
                        var wr = fill.r * fill.a * fillFrac + border.r * border.a * borderFrac;
                        var wg = fill.g * fill.a * fillFrac + border.g * border.a * borderFrac;
                        var wb = fill.b * fill.a * fillFrac + border.b * border.a * borderFrac;
                        outC = new Color(wr / outA, wg / outA, wb / outA, outA);
                    }
                    px[arrayRow * w + col] = outC;
                }
            }
            return Bake(w, h, px);
        }

        /// <summary>Soft rounded shadow: black, fading over a ~2*blur px band. The shape is inset
        /// by <paramref name="blur"/> so the outer band fades to fully transparent at the texture
        /// edge - a real soft halo rather than a hard-clipped one. 9-slice with border radius+blur.</summary>
        public static Texture2D MakeShadow(int w, int h, int radius, float maxAlpha, float blur)
        {
            var px = new Color[w * h];
            float cx = w * 0.5f, cy = h * 0.5f;
            float bx = cx - blur, by = cy - blur; // inset half-size leaves a blurred transparent margin
            for (var srow = 0; srow < h; srow++)
            {
                var py = (srow + 0.5f) - cy;
                var arrayRow = h - 1 - srow;
                for (var col = 0; col < w; col++)
                {
                    var pxpos = (col + 0.5f) - cx;
                    var d = SdRoundBox(pxpos, py, bx, by, radius, radius, radius, radius);
                    var a = maxAlpha * (1f - SmoothStep01(-blur, blur, d));
                    px[arrayRow * w + col] = new Color(0f, 0f, 0f, a);
                }
            }
            return Bake(w, h, px);
        }

        private static Texture2D Solid(Color c)
        {
            return Bake(1, 1, new[] { c });
        }

        // ---- action glyphs ---------------------------------------------------------------

        private const int IconGlyphSize = 16;

        /// <summary>Fonts tried, in order, for button glyphs. Both are Windows icon fonts that map the
        /// same code points (E72C refresh, E8C8 copy): Segoe MDL2 Assets ships on every Windows 10
        /// desktop, Segoe Fluent Icons on Windows 11 (and Windows 10 via updates). The name is looked
        /// up in the installed fonts before use - CreateDynamicFontFromOSFont quietly falls back to the
        /// default font when a name is missing, and that would draw a missing-glyph box, not an icon.</summary>
        private static readonly string[] IconFontNames = { "Segoe Fluent Icons", "Segoe MDL2 Assets" };

        // Code points, checked against both font files above. They are absent from Arial, which is
        // what IMGUI uses by default, so the font is chosen rather than assumed.
        private const string GlyphRefresh = "\uE72C"; // refresh arrow - generate / regenerate
        private const string GlyphCopy = "\uE8C8";    // stacked sheets - copy
        private const string GlyphPlay = "\uE768";    // play triangle - start
        private const string GlyphStop = "\uE71A";    // stop square - stop

        /// <summary>The first icon font actually installed, or null when this machine has none
        /// (Proton, a trimmed Windows image, or someone who removed them).</summary>
        private static Font FindIconFont()
        {
            var installed = Font.GetOSInstalledFontNames();
            foreach (var wanted in IconFontNames)
            {
                foreach (var name in installed)
                {
                    if (!string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var font = Font.CreateDynamicFontFromOSFont(wanted, 16);
                    if (font == null)
                    {
                        continue;
                    }

                    font.hideFlags = HideFlags.HideAndDontSave; // survive scene loads
                    return font;
                }
            }

            return null;
        }

        /// <summary>An icon font draws its glyph sitting on the text baseline, so MiddleCenter centres
        /// the LINE BOX (ascent plus descent) and the descent space the glyph never uses pushes the
        /// icon visually high. Ask the font where the glyph's ink actually is and pad the style so the
        /// ink, not the line box, lands in the middle of the button.</summary>
        private static void CentreGlyphInk(GUIStyle style, string glyph)
        {
            if (string.IsNullOrEmpty(glyph))
            {
                return;
            }

            IconFont.RequestCharactersInTexture(glyph, IconGlyphSize, FontStyle.Normal);
            if (!IconFont.GetCharacterInfo(glyph[0], out var info, IconGlyphSize)
                || IconFont.ascent <= 0)
            {
                // No usable metrics: MiddleCenter is a reasonable fallback, and a nudge computed from
                // numbers we do not trust would be worse than none.
                return;
            }

            // Font has no descent property; it is whatever the line height has above the ascent.
            var descent = Mathf.Max(0, IconFont.lineHeight - IconFont.ascent);

            // Where the baseline sits relative to the line box centre, minus how far the ink's own
            // centre sits above the baseline. Positive means the glyph renders low and must come up.
            var inkMid = (info.minY + info.maxY) * 0.5f;
            var shift = Mathf.RoundToInt((IconFont.ascent - descent) * 0.5f - inkMid);
            if (shift == 0)
            {
                return;
            }

            if (Mathf.Abs(shift) > IconGlyphSize)
            {
                // Metrics that far out mean we read them in units we do not understand; leaving the
                // glyph where it lands beats shoving it off the button.
                CoopPlugin.Log?.LogWarning("Icon font metrics for " + IconFont.name + " look wrong ("
                    + shift + "px at " + IconGlyphSize + "px); leaving the glyph un-nudged.");
                return;
            }

            // Padding insets the CONTENT, so a top inset moves the glyph down and a bottom inset up.
            style.padding = shift > 0
                ? new RectOffset(0, 0, 0, shift)
                : new RectOffset(0, 0, -shift, 0);
        }

        /// <summary>The glyph for a named action icon, or null when the UI has no artwork for that name
        /// or this machine has no icon font to draw it with. Null means "use the action's words" - a
        /// caller falls back rather than drawing a box.</summary>
        public static string IconGlyph(string icon)
        {
            if (IconFont == null || string.IsNullOrEmpty(icon))
            {
                return null;
            }

            return icon switch
            {
                "refresh" => GlyphRefresh,
                "copy" => GlyphCopy,
                "play" => GlyphPlay,
                "stop" => GlyphStop,
                _ => null,
            };
        }

        /// <summary>The baked crown used to mark the host in the roster. Null only before the theme
        /// has been built; callers guard on that.</summary>
        public static Texture2D CrownTexture => _crownTex;

        /// <summary>A small crown silhouette, filled from a normalized polygon with 4x4 supersampling
        /// so the edges read cleanly at this size. No icon font carries a crown, so it is baked.</summary>
        private static Texture2D MakeCrown(int size, Color fill)
        {
            // Normalized outline (y DOWN): left peak, valley, centre peak, valley, right peak, then
            // down the right side, across the base and back up the left.
            var poly = new[]
            {
                new Vector2(0.06f, 0.30f), new Vector2(0.28f, 0.58f), new Vector2(0.50f, 0.14f),
                new Vector2(0.72f, 0.58f), new Vector2(0.94f, 0.30f), new Vector2(0.94f, 0.90f),
                new Vector2(0.06f, 0.90f),
            };

            var px = new Color[size * size];
            const int Ss = 4;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var hits = 0;
                    for (var sy = 0; sy < Ss; sy++)
                    {
                        for (var sx = 0; sx < Ss; sx++)
                        {
                            var u = (x + (sx + 0.5f) / Ss) / size;
                            // Unity textures are bottom-up, so flip y to keep the crown upright.
                            var v = 1f - (y + (sy + 0.5f) / Ss) / size;
                            if (InPolygon(poly, u, v))
                            {
                                hits++;
                            }
                        }
                    }

                    var a = (float)hits / (Ss * Ss);
                    px[y * size + x] = new Color(fill.r, fill.g, fill.b, fill.a * a);
                }
            }

            return Bake(size, size, px);
        }

        /// <summary>Even-odd point-in-polygon (ray cast to the left).</summary>
        private static bool InPolygon(Vector2[] poly, float x, float y)
        {
            var inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                if ((poly[i].y > y) != (poly[j].y > y)
                    && x < (poly[j].x - poly[i].x) * (y - poly[i].y) / (poly[j].y - poly[i].y)
                        + poly[i].x)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        private static Texture2D Bake(int w, int h, Color[] px)
        {
            var tex = new Texture2D(w, h, TextureFormat.ARGB32, false)
            {
                hideFlags = HideFlags.HideAndDontSave, // survive scene loads, never auto-unloaded
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            tex.SetPixels(px);
            tex.Apply(false, true); // upload + drop the CPU copy (we never read it again)
            return tex;
        }

        // signed distance to a rounded box with per-corner radius (iq's rounded box)
        private static float SdRoundBox(float px, float py, float bx, float by,
            float rTL, float rTR, float rBR, float rBL)
        {
            float r;
            if (px > 0f)
            {
                r = (py > 0f) ? rBR : rTR;
            }
            else
            {
                r = (py > 0f) ? rBL : rTL;
            }

            var qx = Mathf.Abs(px) - bx + r;
            var qy = Mathf.Abs(py) - by + r;
            float ox = Mathf.Max(qx, 0f), oy = Mathf.Max(qy, 0f);
            var outside = Mathf.Sqrt(ox * ox + oy * oy);
            var inside = Mathf.Min(Mathf.Max(qx, qy), 0f);
            return outside + inside - r;
        }

        // 1px anti-aliased coverage of a half-plane at signed distance d (negative = inside)
        private static float Coverage(float d)
        {
            return 1f - SmoothStep01(-0.5f, 0.5f, d);
        }

        private static float SmoothStep01(float edge0, float edge1, float x)
        {
            var t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        // ---- color helpers --------------------------------------------------------------
        private static Color Rgb(int r, int g, int b, float a = 1f)
            => new(r / 255f, g / 255f, b / 255f, a);
        private static Color Mul(Color c, float m) => new(c.r * m, c.g * m, c.b * m, c.a);
        private static Color WithA(Color c, float a) => new(c.r, c.g, c.b, a);
        private static Color Lerp(Color a, Color b, float t) => Color.Lerp(a, b, t);
        private static Color Clear() => new(0f, 0f, 0f, 0f);
    }
}




