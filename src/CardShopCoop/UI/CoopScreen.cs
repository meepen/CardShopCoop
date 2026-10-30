using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Logging;
using CardShopCoop.Api;
using CardShopCoop.Diagnostics;
using CardShopCoop.Modules.Presence;
using CardShopCoop.Net.Networking;
using CardShopCoop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace CardShopCoop.UI
{
    internal static class CoopScreen
    {
        private sealed class PlayerRow
        {
            internal GameObject Grp;

            internal TextMeshProUGUI Name;

            internal TextMeshProUGUI Info;

            internal CoopButton Kick;

            internal int Conn = -1;
        }

        private enum ScreenTab
        {
            Session,
            Character,
            Advanced
        }

        /// <summary>Sub-views of the in-game hosting container. Hosting owns the transport
        /// controls; Players owns the roster, so neither one covers the other up.</summary>
        private enum SessionPane
        {
            Hosting,
            Players
        }

        /// <summary>Which builder last filled the session form area. Changing builders (for
        /// example a pre-host form becoming the hosting pane) must clear the cached signature.</summary>
        private enum HostFormMode
        {
            None,
            Form,
            Hosting
        }

        /// <summary>The connection forms' scrolling viewport. The backend sections are stacked top
        /// to bottom inside <see cref="Content"/>; their height comes from what each section
        /// actually draws, and <see cref="Bar"/> appears only when that exceeds the viewport.</summary>
        private sealed class FormArea
        {
            internal GameObject Root;

            internal RectTransform Content;

            internal ScrollRect Scroll;

            internal Scrollbar Bar;

            internal string Signature;
        }

        private const float FormX = 40f;
        private const float FormY = 200f;
        private const float FormWidth = 960f;
        private const float FormHeight = 500f;
        private const float ScrollbarWidth = 12f;
        private const float ScrollbarGap = 6f;
        private const float FormContentWidth = FormWidth - ScrollbarWidth - ScrollbarGap;
        private const float PlayersPaneHeight = 520f;
        private const float ClientPlayersY = 360f;
        private const string DiscordUrl = "https://discord.gg/UQwXwV5Qrg";

        /// <summary>The full character prefab only exists in a loaded shop scene. The title
        /// fallback is the limited CC_Dummy body, and loading the shop additively at the title is
        /// not safe under EnhancedPrefabLoader, so keep the editor disabled until a shop exists.
        /// In a live shop the editor clones the real prefab without any extra scene load.</summary>
        private static bool CharacterEditorAvailable => CoopMenus.InShop;

        internal static bool IsOpen;

        private static bool _openedPause;

        private static bool _navPushed;

        private static string _hint = "";
        private static int _hintGeneration;
        private static bool _eventsSubscribed;

        private static bool _virtualKeyboardBackOwned;

        private static int _scene = -1;

        private static ScreenTab _currentTab = ScreenTab.Session;

        private static GameObject _root;

        private static ControllerScreenUIExtension _nav;

        private static TextMeshProUGUI _title;

        private static TextMeshProUGUI _sub;

        private static TextMeshProUGUI _build;

        private static CoopButton _tabSessionBtn;

        private static CoopButton _tabCharBtn;

        private static CoopButton _tabAdvancedBtn;

        private static GameObject _joinGrp;

        private static FormArea _joinForm;

        private static GameObject _sessGrp;

        private static FormArea _hostForm;

        private static GameObject _hostLiveRoot;

        private static CoopButton _hostingTabBtn;

        private static CoopButton _playersTabBtn;

        private static RectTransform _playersPane;

        private static SessionPane _sessionPane = SessionPane.Hosting;

        private static HostFormMode _hostFormMode = HostFormMode.None;

        private static GameObject _charGrp;

        private static GameObject _advancedGrp;

        private static GameObject _statusGrp;

        private static GameObject _confirmGrp;

        private static GameObject _creditsGrp;

        private static TextMeshProUGUI _playersHead;

        private static TextMeshProUGUI _status;

        private static TextMeshProUGUI _problem;

        private static readonly PlayerRow[] Players = new PlayerRow[8];

        private static CoopButton _cancel;

        private static CoopButton _back;

        private static CoopButton _creditsBtn;

        private static CoopButton _confirmResetBtn;

        private static CoopButton _confirmCancelBtn;

        private static CoopButton _creditsDiscordBtn;

        private static CoopButton _creditsCloseBtn;

        private static CoopButton _crossplayBtn;

        private static CoopButton _nsfwBtn;

        private static TMP_InputField _advNameInput;

        internal static void Toggle()
        {
            if (IsOpen)
            {
                Close();
            }
            else
            {
                Open(quiet: false);
            }
        }

        internal static void Open(bool quiet)
        {
            TryOpen(quiet);
        }

        private static bool TryOpen(bool quiet)
        {
            if (IsOpen)
            {
                Refresh();
                return true;
            }
            bool openedPause = false;
            if (CoopMenus.AtTitle)
            {
                if (!NativeCoopUi.HasTemplate)
                {
                    return false;
                }
            }
            else
            {
                var instance = SceneRef<CGameManager>.Get();
                if (instance == null || !instance.m_IsGameLevel || !CoopMenus.InShop)
                {
                    return false;
                }
                if (!CoopMenus.PauseOpen)
                {
                    if (quiet)
                    {
                        return false;
                    }
                    if (!CoopMenus.CanOpenPause())
                    {
                        return false;
                    }
                    PauseScreen.OpenScreen();
                    if (!CoopMenus.PauseOpen)
                    {
                        return false;
                    }
                    openedPause = true;
                }
                CoopMenus.EnsurePause();
                if (!NativeCoopUi.HasTemplate)
                {
                    return false;
                }
            }
            try
            {
                if (_root == null)
                {
                    Build();
                }
            }
            catch (Exception ex)
            {
                ManualLogSource log = CoopPlugin.Log;
                if (log != null)
                {
                    log.LogError("coop: co-op screen could not be built: " + ex);
                }
                if (_root != null)
                {
                    UnityEngine.Object.Destroy(_root);
                }
                _root = null;
                return false;
            }
            _openedPause = openedPause;
            IsOpen = true;
            _currentTab = ScreenTab.Session;
            _root.SetActive(true);
            Subscribe();
            ShowTab(ScreenTab.Session);
            Refresh();
            PushNav();
            return true;
        }

        private static void Subscribe()
        {
            if (_eventsSubscribed)
            {
                return;
            }

            _eventsSubscribed = true;
            CoopCore.StateChanged += OnSessionChanged;
            PresenceApi.RosterChanged += OnSessionChanged;
        }

        private static void Unsubscribe()
        {
            if (!_eventsSubscribed)
            {
                return;
            }

            _eventsSubscribed = false;
            CoopCore.StateChanged -= OnSessionChanged;
            PresenceApi.RosterChanged -= OnSessionChanged;
        }

        private static void OnSessionChanged()
        {
            if (!IsOpen)
            {
                return;
            }

            try
            {
                Refresh();
            }
            catch (Exception ex)
            {
                CoopLog.WarnOnce("coop.screen.refresh",
                    "coop: could not refresh the co-op screen: " + ex);
            }
        }

        internal static void Close()
        {
            if (!IsOpen)
            {
                return;
            }
            CommitName();
            CancelResetDefaults();
            CloseCredits();
            IsOpen = false;
            _currentTab = ScreenTab.Session;
            _sessionPane = SessionPane.Hosting;
            _hostFormMode = HostFormMode.None;
            _virtualKeyboardBackOwned = false;
            Unsubscribe();
            PopNav();
            if (_root != null)
            {
                _root.SetActive(false);
            }
            if (_openedPause)
            {
                _openedPause = false;
                if (CoopMenus.PauseOpen)
                {
                    PauseScreen.CloseScreen();
                }
            }
            // The window never touches the cursor. SessionInputRuntime releases the UI-mode flag
            // and gameplay input on the next reconcile; the title/pause cursor state is preserved.
            CoopCharacterPrefabPreloader.ReleaseEditorLease();
        }

        internal static bool CloseFromPause()
        {
            bool openedPause = _openedPause;
            _openedPause = false;
            Close();
            return openedPause;
        }

        private static void ForceClose()
        {
            CommitName();
            _openedPause = false;
            _navPushed = false;
            CancelResetDefaults();
            CloseCredits();
            IsOpen = false;
            _currentTab = ScreenTab.Session;
            _sessionPane = SessionPane.Hosting;
            _hostFormMode = HostFormMode.None;
            _virtualKeyboardBackOwned = false;
            Unsubscribe();
            if (_root != null)
            {
                _root.SetActive(false);
            }
            CoopCharacterPrefabPreloader.ReleaseEditorLease();
        }

        internal static void Tick()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            int handle = activeScene.handle;
            if (handle != _scene)
            {
                _scene = handle;
                if (IsOpen)
                {
                    ForceClose();
                }
            }
            if (!IsOpen)
            {
                return;
            }
            TrackVirtualKeyboardBack();
            if (_creditsGrp != null && _creditsGrp.activeSelf)
            {
                if (Input.GetKeyDown(KeyCode.Escape) || ControllerBack())
                {
                    CloseCredits();
                }
                return;
            }
            if (_confirmGrp != null && _confirmGrp.activeSelf)
            {
                if (Input.GetKeyDown(KeyCode.Escape) || ControllerBack())
                {
                    CancelResetDefaults();
                }
                return;
            }
            if (Input.GetKeyDown(KeyCode.Escape) || ControllerBack())
            {
                if (_currentTab != ScreenTab.Session)
                {
                    ShowTab(ScreenTab.Session);
                }
                else
                {
                    Close();
                }
            }
        }

        /// <summary>A controller-opened text field puts the game's virtual keyboard on top as its own
        /// controller screen. Remember that it owned a MenuBack press until the button is released,
        /// so the same press cannot also close/switch the co-op screen if the keyboard closes
        /// before this screen's Tick runs that frame.</summary>
        private static void TrackVirtualKeyboardBack()
        {
            if (GameInstance.m_IsVirtualKeyboardActive)
            {
                _virtualKeyboardBackOwned = true;
                return;
            }
            if (_virtualKeyboardBackOwned && !InputManager.GetKeyHoldAction(EGameAction.MenuBack))
            {
                _virtualKeyboardBackOwned = false;
            }
        }

        private static bool ControllerBack()
        {
            InputManager val = Live<InputManager>.Get();
            if (val == null || !val.m_IsControllerActive || _virtualKeyboardBackOwned)
            {
                return false;
            }
            // A child screen (the virtual keyboard opened by a text field) registers its own
            // ControllerScreenUIExtension. While it is on top, MenuBack belongs to that screen;
            // handling it here as well would close/switch the co-op screen under the keyboard.
            if (_navPushed)
            {
                ControllerScreenUIExtManager manager = Live<ControllerScreenUIExtManager>.Get();
                if (manager != null
                    && !ReferenceEquals(manager.m_CurrentCtrlScreenUIExt, _nav))
                {
                    return false;
                }
            }
            return InputManager.GetKeyDownAction(EGameAction.MenuBack);
        }

        internal static void Destroy()
        {
            CancelResetDefaults();
            _sessionPane = SessionPane.Hosting;
            _hostFormMode = HostFormMode.None;
            _virtualKeyboardBackOwned = false;
            Unsubscribe();
            try
            {
                PopNav();
            }
            catch (Exception ex)
            {
                CoopPlugin.Log?.LogWarning("coop: close controller navigation failed: " + ex.Message);
            }
            CoopCharacterEditor.Shutdown();
            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
            }
            _root = null;
            NativeCoopUi.Shutdown();
        }

        private static void PushNav()
        {
            if (_nav == null || Live<ControllerScreenUIExtManager>.Get() == null)
            {
                return;
            }
            try
            {
                _nav.m_StartControllerButton = StartButton();
                _nav.m_StartControllerButtonAlt = _back?.Ctrl;
                // Tick owns MenuBack so tab/overlay handling happens in one place. If the game's
                // extension also invoked the Back button here, one MenuBack press would run both
                // handlers and close twice.
                _nav.m_CloseScreenBtn = null;
                ControllerScreenUIExtManager.OnOpenScreen(_nav);
                _navPushed = true;
            }
            catch (Exception ex)
            {
                ManualLogSource log = CoopPlugin.Log;
                if (log != null)
                {
                    log.LogWarning("coop: controller support for co-op screen: " + ex.Message);
                }
            }
        }

        private static void PopNav()
        {
            if (!_navPushed)
            {
                return;
            }
            _navPushed = false;
            if (_nav == null || Live<ControllerScreenUIExtManager>.Get() == null)
            {
                return;
            }
            try
            {
                if (_nav.m_CurrentControllerButton != null)
                {
                    _nav.m_CurrentControllerButton.OnSelectionDeactivate();
                }
                ControllerScreenUIExtManager.OnCloseScreen(_nav);
            }
            catch (Exception ex)
            {
                ManualLogSource log = CoopPlugin.Log;
                if (log != null)
                {
                    log.LogWarning("coop: controller support for co-op screen: " + ex.Message);
                }
            }
        }

        private static ControllerButton StartButton() => _back?.Ctrl;

        private static void Build()
        {
            _root = new GameObject("CardShopCoop_Screen", typeof(RectTransform));
            _root.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(_root);
            Canvas canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30010;
            canvas.additionalShaderChannels = (AdditionalCanvasShaderChannels)((int)canvas.additionalShaderChannels | 0x19);
            CanvasScaler scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 1f;
            _root.AddComponent<GraphicRaycaster>();
            RectTransform dimRt = NativeCoopUi.Node(_root.transform, "Dim", 0f, 0f, 0f, 0f);
            NativeCoopUi.Stretch(dimRt, 0f, 0f, 0f, 0f);
            Image dimImg = dimRt.gameObject.AddComponent<Image>();
            if (NativeCoopUi.White != null)
            {
                dimImg.sprite = NativeCoopUi.White;
            }
            dimImg.color = new Color(0f, 0f, 0f, 0.82f);
            dimImg.raycastTarget = true;
            RectTransform panelRt = (RectTransform)new GameObject("Panel", typeof(RectTransform)).transform;
            panelRt.SetParent(_root.transform, false);
            Vector2 center = new Vector2(0.5f, 0.5f);
            panelRt.anchorMax = center;
            panelRt.anchorMin = center;
            panelRt.pivot = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(1040f, 980f);
            panelRt.anchoredPosition = Vector2.zero;
            NativeCoopUi.Panel(panelRt, -8f, -8f, 1056f, 996f, NativeCoopUi.PanelEdge, 1.2f);
            NativeCoopUi.Panel(panelRt, 0f, 0f, 1040f, 980f, NativeCoopUi.PanelFill, 1.2f);
            _title = NativeCoopUi.Text(panelRt, "", 46f, 0f, 16f, 1040f, 58f, Color.white, TextAlignmentOptions.Center, wrap: false, title: true);
            _sub = NativeCoopUi.Text(panelRt, "", 20f, 60f, 76f, 920f, 30f, NativeCoopUi.Dim, TextAlignmentOptions.Center, wrap: false, title: false);
            _build = NativeCoopUi.Text(panelRt, "CardShopCoop Community V " + CoopPlugin.Version, 16f, 680f, 14f, 340f, 22f, NativeCoopUi.Dim, TextAlignmentOptions.Right, wrap: false, title: false);
            _tabSessionBtn = NativeCoopUi.Button(panelRt, "Co-op Session", NativeCoopUi.Purple, () => ShowTab(ScreenTab.Session), 40f, 116f, 305f, 46f, 22f);
            _tabCharBtn = NativeCoopUi.Button(panelRt, "Character", NativeCoopUi.Grey, () => ShowTab(ScreenTab.Character), 365f, 116f, 305f, 46f, 22f);
            _tabAdvancedBtn = NativeCoopUi.Button(panelRt, "Advanced Options", NativeCoopUi.Grey, () => ShowTab(ScreenTab.Advanced), 690f, 116f, 310f, 46f, 22f);
            RefreshCharacterTabAvailability();
            BuildJoin(panelRt);
            BuildSession(panelRt);
            BuildCharacter(panelRt);
            BuildAdvanced(panelRt);
            BuildStatus(panelRt);
            _back = NativeCoopUi.Button(panelRt, "Back", NativeCoopUi.Grey, OnBackClicked, 700f, 900f, 300f, 56f, 28f);
            _cancel = NativeCoopUi.Button(panelRt, "Cancel joining", NativeCoopUi.Orange, OnCancel, 40f, 900f, 300f, 56f, 26f);
            _creditsBtn = NativeCoopUi.Button(panelRt, "Credits", NativeCoopUi.Blue,
                ShowCredits, 40f, 900f, 300f, 56f, 26f);
            _creditsBtn?.Show(false);
            BuildResetConfirmation(panelRt);
            BuildCredits(panelRt);
            BuildNav();
        }

        private static void BuildResetConfirmation(Transform parent)
        {
            RectTransform grp = NativeCoopUi.Node(parent, "ConfirmReset", 0f, 0f, 1040f, 980f);
            _confirmGrp = grp.gameObject;
            Image blocker = grp.gameObject.AddComponent<Image>();
            if (NativeCoopUi.White != null)
            {
                blocker.sprite = NativeCoopUi.White;
            }
            blocker.color = new Color(0f, 0f, 0f, 0.55f);
            blocker.raycastTarget = true;

            RectTransform box = NativeCoopUi.Node(grp, "Box", 170f, 350f, 700f, 280f);
            NativeCoopUi.Panel(box, 0f, 0f, 700f, 280f, NativeCoopUi.PanelFill, 2.2f);
            NativeCoopUi.Text(box, "Reset all options?", 28f, 30f, 28f, 640f, 40f,
                NativeCoopUi.Gold, TextAlignmentOptions.Left, wrap: false, title: true);
            NativeCoopUi.Text(box,
                "This resets the co-op options and your display name to their defaults.",
                18f, 30f, 84f, 640f, 60f, Color.white, TextAlignmentOptions.Left,
                wrap: true, title: false);
            _confirmResetBtn = NativeCoopUi.Button(box, "Reset", NativeCoopUi.Orange,
                ConfirmResetDefaults, 30f, 190f, 220f, 50f, 22f);
            _confirmCancelBtn = NativeCoopUi.Button(box, "Cancel", NativeCoopUi.Grey,
                CancelResetDefaults, 270f, 190f, 220f, 50f, 22f);
            _confirmGrp.SetActive(false);
        }

        private static void BuildCredits(Transform parent)
        {
            RectTransform grp = NativeCoopUi.Node(parent, "Credits", 0f, 0f, 1040f, 980f);
            _creditsGrp = grp.gameObject;
            Image blocker = grp.gameObject.AddComponent<Image>();
            if (NativeCoopUi.White != null)
            {
                blocker.sprite = NativeCoopUi.White;
            }
            blocker.color = new Color(0f, 0f, 0f, 0.62f);
            blocker.raycastTarget = true;

            RectTransform box = NativeCoopUi.Node(grp, "Box", 140f, 180f, 760f, 620f);
            NativeCoopUi.Panel(box, 0f, 0f, 760f, 620f, NativeCoopUi.PanelFill, 2.2f);
            NativeCoopUi.Text(box, "Credits", 34f, 32f, 24f, 696f, 44f,
                NativeCoopUi.Gold, TextAlignmentOptions.Left, wrap: false, title: true);
            NativeCoopUi.Text(box, "CardShopCoopCommunity", 20f, 32f, 74f, 696f, 30f,
                NativeCoopUi.Dim, TextAlignmentOptions.Left, wrap: false, title: false);

            var y = 132f;
            CreditRow(box, "Creator", "DeliriumPulse", ref y);
            CreditRow(box, "Current maintainer", "Meepen (since 1.2.0)", ref y);
            CreditRow(box, "Native UI update", "ilvincio_tv", ref y);
            CreditRow(box, "Bug reports & testing", "Various Discord community members", ref y);
            NativeCoopUi.Text(box,
                "Thanks to everyone in the Discord who tested builds and reported bugs.",
                18f, 32f, y + 6f, 696f, 56f, NativeCoopUi.Dim, TextAlignmentOptions.TopLeft,
                wrap: true, title: false);
            NativeCoopUi.Text(box, DiscordUrl, 16f, 32f, 466f, 696f, 24f,
                NativeCoopUi.Dim, TextAlignmentOptions.Left, wrap: false, title: false);
            _creditsDiscordBtn = NativeCoopUi.Button(box, "Discord", NativeCoopUi.Blue,
                OpenDiscord, 32f, 516f, 240f, 52f, 20f);
            _creditsCloseBtn = NativeCoopUi.Button(box, "Close", NativeCoopUi.Grey,
                CloseCredits, 286f, 516f, 240f, 52f, 20f);
            _creditsGrp.SetActive(false);
        }

        private static void CreditRow(Transform parent, string role, string name, ref float y)
        {
            NativeCoopUi.Text(parent, role, 18f, 32f, y, 220f, 32f,
                NativeCoopUi.Dim, TextAlignmentOptions.MidlineLeft, wrap: false, title: false);
            NativeCoopUi.Text(parent, name, 20f, 260f, y, 468f, 32f,
                Color.white, TextAlignmentOptions.MidlineLeft, wrap: false, title: false);
            y += 56f;
        }

        private static void RefreshCharacterTabAvailability()
        {
            bool available = CharacterEditorAvailable;
            _tabCharBtn?.SetInteractable(available);
            _tabCharBtn?.SetLabel("Character");
        }

        private static void ShowTab(ScreenTab tab)
        {
            RefreshCharacterTabAvailability();
            if (tab == ScreenTab.Character && !CharacterEditorAvailable)
            {
                Hint("Load or start a shop before editing your character.");
                tab = ScreenTab.Session;
            }

            _currentTab = tab;
            CloseCredits();
            _cancel?.Show(on: false);
            _creditsBtn?.Show(tab == ScreenTab.Advanced);
            if (_tabSessionBtn != null)
            {
                _tabSessionBtn.SetStyle(tab == ScreenTab.Session ? NativeCoopUi.Purple : NativeCoopUi.Grey);
            }
            if (_tabCharBtn != null)
            {
                _tabCharBtn.SetStyle(tab == ScreenTab.Character ? NativeCoopUi.Purple : NativeCoopUi.Grey);
            }
            if (_tabAdvancedBtn != null)
            {
                _tabAdvancedBtn.SetStyle(tab == ScreenTab.Advanced ? NativeCoopUi.Purple : NativeCoopUi.Grey);
            }
            switch (tab)
            {
                case ScreenTab.Advanced:
                    NativeCoopUi.Show(_joinGrp, on: false);
                    NativeCoopUi.Show(_sessGrp, on: false);
                    NativeCoopUi.Show(_charGrp, on: false);
                    NativeCoopUi.Show(_statusGrp, on: false);
                    NativeCoopUi.Show(_advancedGrp, on: true);
                    RefreshAdvanced();
                    RebuildNav();
                    return;
                case ScreenTab.Character:
                    NativeCoopUi.Show(_joinGrp, on: false);
                    NativeCoopUi.Show(_sessGrp, on: false);
                    NativeCoopUi.Show(_advancedGrp, on: false);
                    NativeCoopUi.Show(_statusGrp, on: false);
                    NativeCoopUi.Show(_charGrp, on: true);
                    RefreshCharacter();
                    RebuildNav();
                    return;
            }
            NativeCoopUi.Show(_advancedGrp, on: false);
            NativeCoopUi.Show(_charGrp, on: false);
            NativeCoopUi.Show(_statusGrp, on: true);
            // Coming back to the session tab after an arbitrary interval: rebuild from the live
            // state rather than trusting a cached form/content signature captured before a
            // session may have started or stopped.
            if (_hostForm != null)
            {
                _hostForm.Signature = null;
            }
            if (_joinForm != null)
            {
                _joinForm.Signature = null;
            }
            bool atTitle = CoopMenus.AtTitle;
            NativeCoopUi.Show(_joinGrp, atTitle);
            NativeCoopUi.Show(_sessGrp, !atTitle);
            if (atTitle)
            {
                RefreshJoin();
            }
            else
            {
                RefreshSession();
            }
            // The status well is hidden on the other tabs, so a change that arrived while one of
            // them was open was deliberately not drawn. Draw the current status on the way back in.
            RefreshStatus();
            RebuildNav();
        }

        private static void BuildJoin(Transform p)
        {
            RectTransform joinRt = NativeCoopUi.Node(p, "Join", 0f, 0f, 1040f, 980f);
            _joinGrp = joinRt.gameObject;
            _joinForm = BuildFormArea(joinRt, "BackendForms");
        }

        private static void BuildSession(Transform p)
        {
            RectTransform sessRt = NativeCoopUi.Node(p, "Session", 0f, 0f, 1040f, 980f);
            _sessGrp = sessRt.gameObject;
            _hostForm = BuildFormArea(sessRt, "BackendForms");
            _hostLiveRoot = NativeCoopUi.Node(sessRt, "LiveBackend", FormX, FormY,
                FormWidth, 200f).gameObject;
            _hostLiveRoot.SetActive(false);

            // Two sub-tabs at the top of the in-game session container. They stay hidden until the
            // player is actually hosting; the pre-host form and the client view keep the full
            // container to themselves.
            _hostingTabBtn = NativeCoopUi.Button(sessRt, "Hosting", NativeCoopUi.Purple,
                () => ShowSessionPane(SessionPane.Hosting), 40f, 166f, 470f, 30f, 17f);
            _playersTabBtn = NativeCoopUi.Button(sessRt, "Players", NativeCoopUi.Grey,
                () => ShowSessionPane(SessionPane.Players), 522f, 166f, 470f, 30f, 17f);
            _hostingTabBtn?.Show(false);
            _playersTabBtn?.Show(false);

            _playersPane = NativeCoopUi.Node(sessRt, "PlayersPane", 40f, 360f, 960f, 520f);
            _playersHead = NativeCoopUi.Text(_playersPane, "Players", 26f, 0f, 0f, 700f, 34f, NativeCoopUi.Gold, TextAlignmentOptions.Left, wrap: false, title: true);
            for (int i = 0; i < 8; i++)
            {
                int index = i;
                PlayerRow playerRow = new PlayerRow();
                RectTransform rowRt = NativeCoopUi.Node(_playersPane, "Player" + i, 0f, 40f + (float)i * 40f, 960f, 36f);
                playerRow.Grp = rowRt.gameObject;
                NativeCoopUi.Panel(rowRt, 0f, 0f, 960f, 36f, NativeCoopUi.RowFill, 4f);
                playerRow.Name = NativeCoopUi.Text(rowRt, "", 22f, 20f, 0f, 500f, 36f, Color.white, TextAlignmentOptions.Left, wrap: false, title: false);
                playerRow.Info = NativeCoopUi.Text(rowRt, "", 18f, 540f, 0f, 260f, 36f, NativeCoopUi.Dim, TextAlignmentOptions.Left, wrap: false, title: false);
                playerRow.Kick = NativeCoopUi.Button(rowRt, "Kick", NativeCoopUi.Orange, () => OnKick(index), 820f, 2f, 120f, 32f, 18f);
                Players[i] = playerRow;
            }
        }

        private static void BuildAdvanced(Transform p)
        {
            RectTransform advRt = NativeCoopUi.Node(p, "Advanced", 0f, 0f, 1040f, 980f);
            _advancedGrp = advRt.gameObject;
            _advancedGrp.SetActive(false);
            NativeCoopUi.Text(advRt, "Advanced Co-op Configuration", 28f, 40f, 180f, 960f, 36f, NativeCoopUi.Gold, TextAlignmentOptions.Left, wrap: false, title: true);
            NativeCoopUi.Text(advRt, "Crossplay (Steam <-> Xbox Game Pass):", 22f, 40f, 230f, 540f, 40f, Color.white, TextAlignmentOptions.Left, wrap: false, title: false);
            _crossplayBtn = NativeCoopUi.Button(advRt, CoopPlugin.AllowCrossBuildJoin.Value ? "ENABLED" : "DISABLED", CoopPlugin.AllowCrossBuildJoin.Value ? NativeCoopUi.Green : NativeCoopUi.Orange, OnToggleCrossplay, 620f, 230f, 220f, 40f, 20f);
            NativeCoopUi.Text(advRt, "Enables connection between the Steam and Xbox / Game Pass versions of the game.", 17f, 40f, 272f, 960f, 24f, NativeCoopUi.Dim, TextAlignmentOptions.Left, wrap: false, title: false);
            NativeCoopUi.Text(advRt, "NSFW Appearance:", 22f, 40f, 320f, 420f, 40f, Color.white, TextAlignmentOptions.Left, wrap: false, title: false);
            _nsfwBtn = NativeCoopUi.Button(advRt, CoopPlugin.AllowNsfw.Value ? "ENABLED" : "DISABLED", CoopPlugin.AllowNsfw.Value ? NativeCoopUi.Green : NativeCoopUi.Orange, OnToggleNsfw, 620f, 320f, 220f, 40f, 20f);
            NativeCoopUi.Text(advRt, "Allow nude/NSFW appearances. When off, the Character tab hides the Nude options.", 17f, 40f, 362f, 960f, 24f, NativeCoopUi.Dim, TextAlignmentOptions.Left, wrap: false, title: false);
            NativeCoopUi.Text(advRt, "Custom Display Name:", 22f, 40f, 410f, 320f, 42f, Color.white, TextAlignmentOptions.Left, wrap: false, title: false);
            _advNameInput = NativeCoopUi.Input(advRt, "Name", 370f, 410f, 280f, 42f, 20f, 24, TMP_InputField.ContentType.Standard);
            _advNameInput.text = CoopPlugin.NameOverride.Value ? CoopPlugin.PlayerName.Value : "";
            _advNameInput.onEndEdit.AddListener(OnNameCommitted);
            NativeCoopUi.Text(advRt, "Leave empty to use your platform name (Xbox Gamertag / Steam). Saved when you leave the field.", 17f, 40f, 452f, 960f, 24f, NativeCoopUi.Dim, TextAlignmentOptions.Left, wrap: false, title: false);
            RectTransform diagRt = NativeCoopUi.Node(advRt, "DiagWell", 40f, 500f, 960f, 140f);
            NativeCoopUi.Panel(diagRt, 0f, 0f, 960f, 140f, NativeCoopUi.WellFill, 2.6f);
            NativeCoopUi.Text(diagRt, "Diagnostic Tools", 20f, 20f, 12f, 920f, 32f, NativeCoopUi.Dim, TextAlignmentOptions.Left, wrap: false, title: false);
            NativeCoopUi.Button(diagRt, "Copy Diagnostics", NativeCoopUi.Grey, OnCopyDiagnostics, 20f, 50f, 300f, 48f, 20f);
            NativeCoopUi.Button(diagRt, "Reset Defaults", NativeCoopUi.Orange, OnResetDefaults, 340f, 50f, 220f, 48f, 20f);
            NativeCoopUi.Text(diagRt, "Copies the diagnostic report (.md) and this process's log (.log) to the clipboard.", 16f, 20f, 104f, 920f, 26f, NativeCoopUi.Dim, TextAlignmentOptions.Left, wrap: false, title: false);
        }

        private static void BuildCharacter(Transform p)
        {
            RectTransform charRt = NativeCoopUi.Node(p, "Character", 0f, 0f, 1040f, 980f);
            _charGrp = charRt.gameObject;
            _charGrp.SetActive(false);
            // The character model is loaded only when the Character tab is actually enabled,
            // not when the co-op window is built.
            CoopCharacterEditor.Build(charRt, Hint);
        }

        private static void BuildStatus(Transform parent)
        {
            RectTransform statusRt = NativeCoopUi.Node(parent, "Status", 40f, 730f, 960f, 140f);
            _statusGrp = statusRt.gameObject;
            NativeCoopUi.Panel(statusRt, 0f, 0f, 960f, 140f, NativeCoopUi.WellFill, 2.6f);
            _status = NativeCoopUi.Text(statusRt, "", 22f, 20f, 12f, 920f, 44f, Color.white, TextAlignmentOptions.Left, wrap: true, title: false);
            _problem = NativeCoopUi.Text(statusRt, "", 20f, 20f, 60f, 920f, 70f, NativeCoopUi.Bad, TextAlignmentOptions.Left, wrap: true, title: false);
        }

        private static void BuildNav()
        {
            _nav = _root.AddComponent<ControllerScreenUIExtension>();
            _nav.m_ControllerBtnColumnList = new List<ControllerBtnList>();
            _nav.m_CtrlBtnXChangeMethodList = new List<ECtrlBtnXChangeMethod>();
            // Rows are vertical lists of controls. Keeping left/right inside the current row is what
            // makes left/right safe for the character editor's sliders: they adjust on the press,
            // and the selection stays on the slider instead of jumping to the next row. Up/down is
            // still the way to change rows.
            _nav.m_LoopWithinRowOnly = true;
            RebuildNav();
        }

        /// <summary>Rebuilds the controller-navigation rows from the controls that are actually live
        /// right now. Backend forms, the players roster, and the character editor all create and
        /// destroy controls at runtime, so a fixed row list could never cover them. Controls are
        /// grouped into rows by their world-space Y (the same visual line), then ordered left to
        /// right; rows are ordered top to bottom. Call this after any activation/rebuild, never from
        /// an update loop.</summary>
        internal static void RebuildNav()
        {
            if (_nav == null)
            {
                return;
            }
            if (_nav.m_ControllerBtnColumnList == null)
            {
                _nav.m_ControllerBtnColumnList = new List<ControllerBtnList>();
            }
            if (_nav.m_CtrlBtnXChangeMethodList == null)
            {
                _nav.m_CtrlBtnXChangeMethodList = new List<ECtrlBtnXChangeMethod>();
            }
            _nav.m_ControllerBtnColumnList.Clear();
            _nav.m_CtrlBtnXChangeMethodList.Clear();

            // A modal owns the controller while it is open. Register only its live controls and
            // prefer the safe dismiss action so a controller cannot confirm a destructive reset by
            // accident.
            if (_confirmGrp != null && _confirmGrp.activeSelf)
            {
                AppendContentRows(_confirmGrp);
                PreferNav(_confirmCancelBtn?.Ctrl);
                SyncNavSelection();
                return;
            }
            if (_creditsGrp != null && _creditsGrp.activeSelf)
            {
                AppendContentRows(_creditsGrp);
                PreferNav(_creditsCloseBtn?.Ctrl);
                SyncNavSelection();
                return;
            }

            var tabs = new List<ControllerButton>();
            AddVisibleControl(tabs, _tabSessionBtn);
            AddVisibleControl(tabs, _tabCharBtn);
            AddVisibleControl(tabs, _tabAdvancedBtn);
            AddNavRow(tabs);

            AppendContentRows(VisibleContentGroup());

            var footer = new List<ControllerButton>();
            AddVisibleControl(footer, _cancel);
            AddVisibleControl(footer, _creditsBtn);
            AddVisibleControl(footer, _back);
            AddNavRow(footer);

            SyncNavSelection();
        }

        private static GameObject VisibleContentGroup()
        {
            if (_currentTab == ScreenTab.Advanced)
            {
                return _advancedGrp;
            }
            if (_currentTab == ScreenTab.Character)
            {
                return _charGrp;
            }
            return CoopMenus.AtTitle ? _joinGrp : _sessGrp;
        }

        private static void AppendContentRows(GameObject group)
        {
            if (group == null || !group.activeInHierarchy)
            {
                return;
            }

            ControllerButton[] found = group.GetComponentsInChildren<ControllerButton>(true);
            var controls = new List<ControllerButton>();
            for (int i = 0; i < found.Length; i++)
            {
                ControllerButton ctrl = found[i];
                if (IsNavigable(ctrl) && !controls.Contains(ctrl))
                {
                    controls.Add(ctrl);
                }
            }
            AppendRowsByY(controls);
        }

        private sealed class NavRowBand
        {
            internal float Y;

            internal readonly List<ControllerButton> Controls = new List<ControllerButton>();
        }

        private static void AppendRowsByY(List<ControllerButton> controls)
        {
            if (controls == null || controls.Count == 0)
            {
                return;
            }

            var bands = new List<NavRowBand>();
            for (int i = 0; i < controls.Count; i++)
            {
                ControllerButton ctrl = controls[i];
                RectTransform rt = ctrl.transform as RectTransform;
                if (rt == null)
                {
                    continue;
                }

                float y = NavCenter(ctrl).y;
                NavRowBand band = null;
                for (int j = 0; j < bands.Count; j++)
                {
                    if (Mathf.Abs(bands[j].Y - y) <= 8f)
                    {
                        band = bands[j];
                        break;
                    }
                }
                if (band == null)
                {
                    band = new NavRowBand { Y = y };
                    bands.Add(band);
                }
                band.Controls.Add(ctrl);
            }

            bands.Sort((a, b) => b.Y.CompareTo(a.Y));
            for (int i = 0; i < bands.Count; i++)
            {
                NavRowBand band = bands[i];
                if (band.Controls.Count == 0)
                {
                    continue;
                }
                band.Controls.Sort((a, b) => NavX(a).CompareTo(NavX(b)));
                AddNavRow(band.Controls);
            }
        }

        private static float NavX(ControllerButton ctrl) => NavCenter(ctrl).x;

        /// <summary>The visual centre of a control in world space. transform.position is the pivot,
        /// which differs per widget (buttons are centre-pivoted, inputs and checkbox rows are
        /// top-left-pivoted), so grouping on it would split one visual line into several rows.</summary>
        private static Vector2 NavCenter(ControllerButton ctrl)
        {
            RectTransform rt = ctrl.transform as RectTransform;
            if (rt == null)
            {
                return Vector2.zero;
            }
            Vector3 center = rt.TransformPoint(rt.rect.center);
            return new Vector2(center.x, center.y);
        }

        private static bool IsNavigable(ControllerButton ctrl)
        {
            // IsActive() dereferences these collections unconditionally, and OnPressConfirm()
            // dereferences m_Button when there is no virtual keyboard. Filtering here means a row
            // can never contain a control that would NRE the game's navigation loop.
            if (ctrl == null)
            {
                return false;
            }
            if (!ctrl.gameObject.activeInHierarchy)
            {
                return false;
            }
            if (ctrl.m_OverlayButton == null || ctrl.m_OverlayButtonHighlight == null)
            {
                CoopLog.WarnOnce("coop.ui.nav.overlay-lists",
                    "coop: controller navigation skipped a control with missing overlay lists: "
                    + ctrl.name);
                return false;
            }
            if (ctrl.m_EnableVirtualKeyboard)
            {
                return true;
            }
            if (ctrl.m_Button != null && ctrl.m_Button.interactable)
            {
                return true;
            }
            // The game invokes the first active overlay button directly and never checks
            // interactable, so only expose live overlay actions.
            for (int i = 0; i < ctrl.m_OverlayButton.Count; i++)
            {
                Button overlay = ctrl.m_OverlayButton[i];
                if (overlay != null && overlay.gameObject.activeInHierarchy && overlay.interactable)
                {
                    return true;
                }
            }
            return false;
        }

        private static void AddVisibleControl(List<ControllerButton> row, CoopButton button)
        {
            if (button == null || button.Go == null || !button.Go.activeInHierarchy)
            {
                return;
            }
            if (IsNavigable(button.Ctrl) && !row.Contains(button.Ctrl))
            {
                row.Add(button.Ctrl);
            }
        }

        private static void AddNavRow(List<ControllerButton> row)
        {
            if (_nav == null || row == null || row.Count == 0)
            {
                return;
            }
            _nav.m_ControllerBtnColumnList.Add(new ControllerBtnList
            {
                rowList = row
            });
            _nav.m_CtrlBtnXChangeMethodList.Add(ECtrlBtnXChangeMethod.Normal);
        }

        private static void PreferNav(ControllerButton preferred)
        {
            if (_nav == null || preferred == null)
            {
                return;
            }

            for (int y = 0; y < _nav.m_ControllerBtnColumnList.Count; y++)
            {
                List<ControllerButton> row = _nav.m_ControllerBtnColumnList[y].rowList;
                int index = row.IndexOf(preferred);
                if (index < 0)
                {
                    continue;
                }

                if (index != 0)
                {
                    row.RemoveAt(index);
                    row.Insert(0, preferred);
                }
                _nav.m_CurrentCtrlBtnYIndex = y;
                _nav.m_CurrentCtrlBtnXIndex = 0;
                if (_navPushed)
                {
                    ControllerButton current = _nav.m_CurrentControllerButton;
                    if (current != null && !ReferenceEquals(current, preferred))
                    {
                        current.OnSelectionDeactivate();
                    }
                    _nav.m_CurrentControllerButton = preferred;
                    ControllerScreenUIExtManager manager =
                        Live<ControllerScreenUIExtManager>.Get();
                    if (manager != null
                        && ReferenceEquals(manager.m_CurrentCtrlScreenUIExt, _nav)
                        && manager.m_IsControllerActive)
                    {
                        preferred.OnSelectionActive();
                    }
                }
                return;
            }
        }

        private static void SyncNavSelection()
        {
            int rowCount = _nav.m_ControllerBtnColumnList.Count;
            if (rowCount == 0)
            {
                _nav.m_CurrentCtrlBtnXIndex = 0;
                _nav.m_CurrentCtrlBtnYIndex = 0;
                return;
            }

            ControllerButton current = _nav.m_CurrentControllerButton;
            int currentY = -1;
            int currentX = -1;
            if (current != null)
            {
                for (int y = 0; y < rowCount && currentY < 0; y++)
                {
                    List<ControllerButton> row = _nav.m_ControllerBtnColumnList[y].rowList;
                    for (int x = 0; x < row.Count; x++)
                    {
                        if (ReferenceEquals(row[x], current))
                        {
                            currentY = y;
                            currentX = x;
                            break;
                        }
                    }
                }
            }

            if (currentY >= 0)
            {
                // The selected control survived; keep the indices pointing at its new position
                // after rows were re-sorted.
                _nav.m_CurrentCtrlBtnYIndex = currentY;
                _nav.m_CurrentCtrlBtnXIndex = currentX;
                return;
            }

            int nextY = Mathf.Clamp(_nav.m_CurrentCtrlBtnYIndex, 0, rowCount - 1);
            int columnCount = _nav.m_ControllerBtnColumnList[nextY].rowList.Count;
            _nav.m_CurrentCtrlBtnYIndex = nextY;
            _nav.m_CurrentCtrlBtnXIndex = Mathf.Clamp(_nav.m_CurrentCtrlBtnXIndex, 0,
                Mathf.Max(0, columnCount - 1));
            if (!_navPushed)
            {
                return;
            }

            // The previously selected control was hidden or is about to be destroyed by a form or
            // character-page rebuild. Reselect now: Destroy() is deferred to the end of the frame,
            // so this moves the game's selector highlight off the doomed button before it dies.
            ControllerButton target = _nav.m_ControllerBtnColumnList[nextY].rowList[
                _nav.m_CurrentCtrlBtnXIndex];
            if (ReferenceEquals(current, target))
            {
                return;
            }
            if (current != null)
            {
                current.OnSelectionDeactivate();
            }
            _nav.m_CurrentControllerButton = target;

            ControllerScreenUIExtManager manager = Live<ControllerScreenUIExtManager>.Get();
            if (manager == null || !ReferenceEquals(manager.m_CurrentCtrlScreenUIExt, _nav))
            {
                return;
            }
            if (manager.m_IsControllerActive)
            {
                target.OnSelectionActive();
            }
            else
            {
                // The controller is idle (mouse was used), so OnSelectionActive would decline to
                // touch the selector. Keep its rect under a live control so a later rebuild cannot
                // destroy it along with the old button.
                ReparentControllerSelector(manager, target.transform);
            }
        }

        private static void ReparentControllerSelector(ControllerScreenUIExtManager manager,
            Transform target)
        {
            ControllerSelectorUIGrp selector = manager.m_ControllerSelectorUIGrp;
            if (selector == null || selector.m_Rect == null || target == null)
            {
                return;
            }
            selector.m_Rect.SetParent(target, false);
        }

        private static void Refresh()
        {
            RefreshCharacterTabAvailability();
            if (_currentTab == ScreenTab.Character && !CharacterEditorAvailable)
            {
                ShowTab(ScreenTab.Session);
                return;
            }
            if (_currentTab == ScreenTab.Advanced)
            {
                RefreshAdvanced();
                RebuildNav();
                return;
            }
            if (_currentTab == ScreenTab.Character)
            {
                RefreshCharacter();
                RebuildNav();
                return;
            }
            bool atTitle = CoopMenus.AtTitle;
            NativeCoopUi.Show(_joinGrp, atTitle);
            NativeCoopUi.Show(_sessGrp, !atTitle);
            if (atTitle)
            {
                RefreshJoin();
            }
            else
            {
                RefreshSession();
            }
            RefreshStatus();
            RebuildNav();
        }

        private static void RefreshJoin()
        {
            bool connecting = CoopCore.Role != CoopRole.None;
            NativeCoopUi.Set(_title, "Join Co-op");
            NativeCoopUi.Set(_sub, connecting
                ? "Connecting to shop..."
                : "Logged in as: " + PlayerName);
            ShowSessionTabs(on: false);
            RefreshBackendForms(_joinForm, ConnectMode.Join, hosting: false);
            _cancel?.Show(connecting);
            RebuildNav();
        }

        private static void RefreshSession()
        {
            bool hosting = CoopCore.Role == CoopRole.Host;
            bool client = CoopCore.Role == CoopRole.Client;
            bool none = CoopCore.Role == CoopRole.None;
            bool inShop = CoopMenus.InShop;
            string title = hosting ? "Hosting Co-op" : client ? "Co-op Session" : "Host Co-op";
            NativeCoopUi.Set(_title, title);
            NativeCoopUi.Set(_sub, hosting
                ? "Friends can join using the configured connection options."
                : client
                    ? "Playing in host's shop."
                    : inShop
                        ? "Logged in as: " + PlayerName + " | Host your shop for friends."
                        : "Load or start your shop first to host.");

            // Choose the pane BEFORE styling the tabs: entering the hosting view resets to
            // Hosting below, and the active-tab colour must match the content drawn this frame.
            if (hosting && inShop && _hostFormMode != HostFormMode.Hosting)
            {
                _sessionPane = SessionPane.Hosting;
            }
            ShowSessionTabs(hosting && inShop);

            if (none || !inShop)
            {
                SetHostFormMode(HostFormMode.Form);
                NativeCoopUi.Show(_hostLiveRoot, on: false);
                ClearPlayers();
                RefreshBackendForms(_hostForm, ConnectMode.Host, hosting: false);
                _cancel?.Show(on: false);
                RebuildNav();
                return;
            }

            if (hosting)
            {
                if (_hostFormMode != HostFormMode.Hosting)
                {
                    // Entering the hosting view (first host, or back from a non-shop state):
                    // start on Hosting so the fresh session's transport state is visible.
                    SetHostFormMode(HostFormMode.Hosting);
                    _sessionPane = SessionPane.Hosting;
                }

                NativeCoopUi.Show(_hostLiveRoot, on: false);
                if (_sessionPane == SessionPane.Hosting)
                {
                    NativeCoopUi.Show(_hostForm.Root, on: true);
                    // Keep the roster count on the Players tab current even while it is hidden.
                    RefreshPlayers(show: false, kick: true, top: FormY);
                    RefreshBackendForms(_hostForm, ConnectMode.Host, hosting: true);
                }
                else
                {
                    NativeCoopUi.Show(_hostForm.Root, on: false);
                    // A few pixels below the sub-tab row, so the roster heading does not sit under
                    // the tabs' visual bounds.
                    RefreshPlayers(show: true, kick: true, top: FormY + 8f);
                }

                _cancel?.Show(on: false);
                RebuildNav();
                return;
            }

            // Client in the shop: one live transport plus the roster, with no hosting controls.
            SetHostFormMode(HostFormMode.None);
            NativeCoopUi.Show(_hostForm.Root, on: false);
            NativeCoopUi.Show(_hostLiveRoot, on: true);
            ClearChildren(_hostLiveRoot);
            CoopBackendOptions.BuildLive(_hostLiveRoot.transform, FormWidth);
            RefreshPlayers(show: true, kick: false, top: ClientPlayersY);
            _cancel?.Show(on: false);
            RebuildNav();
        }

        private static void SetHostFormMode(HostFormMode mode)
        {
            if (_hostFormMode == mode)
            {
                return;
            }

            _hostFormMode = mode;
            _hostForm.Signature = null;
        }

        private static void ShowSessionPane(SessionPane pane)
        {
            _sessionPane = pane;
            if (IsOpen)
            {
                RefreshSession();
                RebuildNav();
            }
        }

        private static void ShowSessionTabs(bool on)
        {
            _hostingTabBtn?.Show(on);
            _playersTabBtn?.Show(on);
            if (!on)
            {
                return;
            }

            _hostingTabBtn?.SetStyle(_sessionPane == SessionPane.Hosting
                ? NativeCoopUi.Purple : NativeCoopUi.Grey);
            _playersTabBtn?.SetStyle(_sessionPane == SessionPane.Players
                ? NativeCoopUi.Purple : NativeCoopUi.Grey);
        }

        private static void RefreshPlayers(bool show, bool kick, float top)
        {
            IReadOnlyList<PresencePlayer> sessionPlayers = PresenceApi.SessionPlayers;
            int count = 0;
            if (sessionPlayers != null)
            {
                for (int i = 0; i < sessionPlayers.Count && count < Players.Length; i++)
                {
                    PresencePlayer presencePlayer = sessionPlayers[i];
                    string name = presencePlayer.Name + (presencePlayer.IsSelf ? " (you)" : "");
                    string info = presencePlayer.IsHost ? "Host" : ("Player #" + presencePlayer.Id);
                    SetPlayer(count++, name, info, (kick && !presencePlayer.IsSelf)
                        ? presencePlayer.Id : (-1));
                }
            }
            for (int j = count; j < Players.Length; j++)
            {
                NativeCoopUi.Show(Players[j].Grp, on: false);
                Players[j].Conn = -1;
            }
            string header = count > 0 ? "Players (" + count + ")" : "Players";
            NativeCoopUi.Set(_playersHead, header);
            _playersTabBtn?.SetLabel(header);
            if (show)
            {
                NativeCoopUi.TopLeft(_playersPane, FormX, top, FormWidth, PlayersPaneHeight);
                NativeCoopUi.Show(_playersPane, on: true);
            }
            else
            {
                NativeCoopUi.Show(_playersPane, on: false);
            }
        }

        private static void ClearPlayers()
        {
            NativeCoopUi.Show(_playersPane, on: false);
            for (var i = 0; i < Players.Length; i++)
            {
                NativeCoopUi.Show(Players[i].Grp, on: false);
                Players[i].Conn = -1;
            }
        }

        private static void RefreshBackendForms(FormArea area, ConnectMode mode, bool hosting)
        {
            if (area == null)
            {
                return;
            }

            var next = CoopBackendOptions.Signature(mode);
            if (next == area.Signature && area.Content.childCount > 0)
            {
                NativeCoopUi.Show(area.Root, on: true);
                return;
            }

            // Keep the player where they were when only a toggle/field changed; a section can grow
            // or shrink, and clamping is handled by ScrollRect.
            var scroll = area.Content.childCount > 0 ? area.Scroll.verticalNormalizedPosition : 1f;
            area.Signature = next;
            ClearChildren(area.Content.gameObject);
            NativeCoopUi.Show(area.Root, on: true);
            var height = hosting
                ? CoopBackendOptions.BuildHosting(area.Content, FormContentWidth, Hint, Refresh)
                : CoopBackendOptions.Build(area.Content, mode, FormContentWidth, Hint, Refresh);
            var contentHeight = Mathf.Max(FormHeight, height + 8f);
            area.Content.sizeDelta = new Vector2(0f, contentHeight);
            Canvas.ForceUpdateCanvases();
            if (area.Bar != null)
            {
                area.Bar.size = Mathf.Clamp01(FormHeight / contentHeight);
            }
            area.Scroll.verticalNormalizedPosition = scroll;
        }

        /// <summary>Builds a vertical scroll area: a masked viewport, the backend sections as its
        /// content, and an auto-hiding scrollbar on the right. The content is anchored to the top
        /// and stretched to the viewport's width, so every backend section spans the full panel.</summary>
        private static FormArea BuildFormArea(Transform parent, string name)
        {
            RectTransform root = NativeCoopUi.Node(parent, name, FormX, FormY, FormWidth, FormHeight);
            var area = new FormArea { Root = root.gameObject };

            var scroll = root.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 40f;
            scroll.inertia = true;
            scroll.decelerationRate = 0.135f;
            area.Scroll = scroll;

            RectTransform viewport = NativeCoopUi.Node(root, "Viewport", 0f, 0f,
                FormContentWidth, FormHeight);
            NativeCoopUi.Stretch(viewport, 0f, 0f, ScrollbarWidth + ScrollbarGap, 0f);
            Image hitArea = viewport.gameObject.AddComponent<Image>();
            if (NativeCoopUi.White != null)
            {
                hitArea.sprite = NativeCoopUi.White;
            }
            hitArea.color = new Color(0f, 0f, 0f, 0.001f);
            hitArea.raycastTarget = true;
            viewport.gameObject.AddComponent<RectMask2D>();
            scroll.viewport = viewport;

            var content = (RectTransform)new GameObject("Content", typeof(RectTransform)).transform;
            content.SetParent(viewport, false);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0f, FormHeight);
            area.Content = content;
            scroll.content = content;

            RectTransform bar = NativeCoopUi.Node(root, "Scrollbar",
                FormWidth - ScrollbarWidth, 0f, ScrollbarWidth, FormHeight);
            Image track = bar.gameObject.AddComponent<Image>();
            if (NativeCoopUi.White != null)
            {
                track.sprite = NativeCoopUi.White;
            }
            track.color = new Color(0.14f, 0.09f, 0.18f);
            track.raycastTarget = true;
            bar.gameObject.AddComponent<RectMask2D>();

            RectTransform sliding = NativeCoopUi.Node(bar, "SlidingArea", 0f, 0f,
                ScrollbarWidth, FormHeight);
            NativeCoopUi.Stretch(sliding, 0f, 0f, 0f, 0f);
            RectTransform handle = NativeCoopUi.Node(sliding, "Handle", 0f, 0f,
                ScrollbarWidth, FormHeight);
            NativeCoopUi.Stretch(handle, 0f, 0f, 0f, 0f);
            Image handleImage = handle.gameObject.AddComponent<Image>();
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
            area.Bar = scrollbar;
            scroll.verticalScrollbar = scrollbar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            scroll.verticalScrollbarSpacing = 0f;

            return area;
        }

        private static void ClearChildren(GameObject root)
        {
            if (root == null)
            {
                return;
            }

            for (var i = root.transform.childCount - 1; i >= 0; i--)
            {
                var child = root.transform.GetChild(i).gameObject;
                child.SetActive(false);
                UnityEngine.Object.Destroy(child);
            }
        }

        private static void RefreshAdvanced()
        {
            if (_crossplayBtn != null && CoopPlugin.AllowCrossBuildJoin != null)
            {
                bool value = CoopPlugin.AllowCrossBuildJoin.Value;
                _crossplayBtn.SetLabel(value ? "ENABLED" : "DISABLED");
                _crossplayBtn.SetStyle(value ? NativeCoopUi.Green : NativeCoopUi.Orange);
            }
            if (_nsfwBtn != null && CoopPlugin.AllowNsfw != null)
            {
                bool value = CoopPlugin.AllowNsfw.Value;
                _nsfwBtn.SetLabel(value ? "ENABLED" : "DISABLED");
                _nsfwBtn.SetStyle(value ? NativeCoopUi.Green : NativeCoopUi.Orange);
            }
        }

        private static void SetPlayer(int i, string name, string info, int conn)
        {
            PlayerRow obj = Players[i];
            NativeCoopUi.Show(obj.Grp, on: true);
            NativeCoopUi.Set(obj.Name, name);
            NativeCoopUi.Set(obj.Info, info);
            obj.Conn = conn;
            obj.Kick.Show(conn >= 0);
        }

        private static string PlayerName
        {
            get
            {
                var name = CoopCore.Instance?.EffectivePlayerName;
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = CoopPlugin.PlayerName?.Value;
                }

                return string.IsNullOrWhiteSpace(name) ? "Player" : name;
            }
        }

        private static void OnCancel()
        {
            CoopCore.Instance?.Disconnect();
            Hint("Cancelled connection.");
            Refresh();
        }

        private static void RefreshStatus()
        {
            string text = string.IsNullOrEmpty(_hint)
                ? (CoopCore.Instance != null ? CoopCore.Instance.StatusLine : "")
                : _hint;
            NativeCoopUi.Set(_status, text ?? "");
            string text2 = CoopCore.Instance != null ? CoopCore.Instance.ErrorLine : "";
            NativeCoopUi.Set(_problem, text2 ?? "");
        }

        private static void Hint(string text)
        {
            _hint = text;
            var generation = ++_hintGeneration;
            RefreshStatus();
            var core = CoopCore.Instance;
            if (core != null)
            {
                core.StartCoroutine(ClearHint(generation));
            }
        }

        private static IEnumerator ClearHint(int generation)
        {
            yield return new WaitForSecondsRealtime(5f);
            if (generation != _hintGeneration)
            {
                yield break;
            }

            _hint = "";
            if (IsOpen)
            {
                RefreshStatus();
            }
        }

        private static void OnKick(int index)
        {
            if (index >= 0 && index < Players.Length)
            {
                int conn = Players[index].Conn;
                if (conn >= 0)
                {
                    CoopCore.Instance?.KickPeer(conn);
                    Hint("Kicked peer " + conn);
                    Refresh();
                }
            }
        }

        private static void OnBackClicked()
        {
            if (_currentTab != ScreenTab.Session)
            {
                ShowTab(ScreenTab.Session);
            }
            else
            {
                Close();
            }
        }

        private static void OnToggleCrossplay()
        {
            if (CoopPlugin.AllowCrossBuildJoin != null)
            {
                CoopPlugin.AllowCrossBuildJoin.Value = !CoopPlugin.AllowCrossBuildJoin.Value;
                RefreshAdvanced();
                Hint("Crossplay (Steam <-> Xbox): " + (CoopPlugin.AllowCrossBuildJoin.Value ? "ENABLED" : "DISABLED"));
            }
        }

        private static void OnToggleNsfw()
        {
            if (CoopPlugin.AllowNsfw == null)
            {
                return;
            }

            bool allowed = !CoopPlugin.AllowNsfw.Value;
            if (PresenceService.Active != null)
            {
                PresenceApi.SetNsfwAllowed(allowed);
            }
            else
            {
                CoopPlugin.AllowNsfw.Value = allowed;
            }
            CoopCharacterEditor.SettingsChanged();
            RefreshAdvanced();
            Hint("NSFW appearances: " + (allowed ? "ENABLED" : "DISABLED"));
        }

        private static void OnNameCommitted(string _) => CommitName();

        private static void CommitName()
        {
            if (_advNameInput == null)
            {
                return;
            }

            string text = _advNameInput.text.Trim();
            bool overrideName = !string.IsNullOrEmpty(text);
            bool wasOverride = CoopPlugin.NameOverride != null && CoopPlugin.NameOverride.Value;
            string previous = CoopPlugin.PlayerName?.Value ?? "";
            if (previous == text && wasOverride == overrideName)
            {
                return;
            }

            if (CoopPlugin.PlayerName != null)
            {
                CoopPlugin.PlayerName.Value = text;
            }
            if (CoopPlugin.NameOverride != null)
            {
                CoopPlugin.NameOverride.Value = overrideName;
            }
            Hint(overrideName
                ? "Display name saved: " + text
                : "Display name reset to platform default.");
        }

        private static void OnResetDefaults()
        {
            if (_confirmGrp == null)
            {
                return;
            }

            _confirmGrp.SetActive(true);
            _confirmGrp.transform.SetAsLastSibling();
            RebuildNav();
        }

        private static void CancelResetDefaults()
        {
            if (_confirmGrp != null)
            {
                _confirmGrp.SetActive(false);
            }
            RebuildNav();
        }

        private static void ConfirmResetDefaults()
        {
            if (_confirmGrp != null)
            {
                _confirmGrp.SetActive(false);
            }

            if (CoopPlugin.AllowCrossBuildJoin != null)
            {
                CoopPlugin.AllowCrossBuildJoin.Value = true;
            }
            if (CoopPlugin.AutoPortForward != null)
            {
                CoopPlugin.AutoPortForward.Value = true;
            }
            if (CoopPlugin.SendRateHz != null)
            {
                CoopPlugin.SendRateHz.Value = 15f;
            }
            if (CoopPlugin.PlayerName != null)
            {
                CoopPlugin.PlayerName.Value = "";
            }
            if (CoopPlugin.NameOverride != null)
            {
                CoopPlugin.NameOverride.Value = false;
            }
            if (CoopPlugin.AllowNsfw != null)
            {
                CoopPlugin.AllowNsfw.Value = false;
            }
            // Presence may own the effective value while a session is live; route the reset
            // through the same setter the toggle uses so the character editor and avatars refresh.
            PresenceApi.SetNsfwAllowed(false);
            if (_advNameInput != null)
            {
                _advNameInput.text = "";
            }
            CoopBackendOptions.ResetValues();
            RefreshAdvanced();
            Hint("Advanced settings reset to default values.");
            RebuildNav();
        }

        private static void ShowCredits()
        {
            if (_creditsGrp == null)
            {
                return;
            }

            _creditsGrp.SetActive(true);
            _creditsGrp.transform.SetAsLastSibling();
            RebuildNav();
        }

        private static void CloseCredits()
        {
            if (_creditsGrp != null)
            {
                _creditsGrp.SetActive(false);
            }
            RebuildNav();
        }

        private static void OpenDiscord()
        {
            try
            {
                Application.OpenURL(DiscordUrl);
            }
            catch (Exception error)
            {
                CoopPlugin.Log?.LogWarning(
                    "coop: could not open the Discord invite in a browser: " + error.Message);
                GUIUtility.systemCopyBuffer = DiscordUrl;
                Hint("Could not open the browser; the Discord invite was copied to the clipboard.");
            }
        }

        private static void OnCopyDiagnostics()
        {
            DiagnosticsReport.TryCopyBundle(out var message);
            Hint(message);
        }

        private static void RefreshCharacter() => CoopCharacterEditor.Refresh();

    }
}
