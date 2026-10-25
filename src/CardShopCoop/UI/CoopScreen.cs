using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using CardShopCoop.Api;
using CardShopCoop.Diagnostics;
using CardShopCoop.Modules.Presence;
using CardShopCoop.Net;
using CardShopCoop.Net.Networking;
using CardShopCoop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
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

        internal static bool IsOpen;

        private static bool _openedPause;

        private static bool _navPushed;

        private static string _hint = "";
        private static int _hintGeneration;
        private static bool _eventsSubscribed;

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

        private static bool _hostFormsLive;

        private static GameObject _charGrp;

        private static GameObject _advancedGrp;

        private static GameObject _statusGrp;

        private static GameObject _confirmGrp;

        private static TextMeshProUGUI _playersHead;

        private static TextMeshProUGUI _status;

        private static TextMeshProUGUI _problem;

        private static readonly PlayerRow[] Players = new PlayerRow[8];

        private static CoopButton _cancel;

        private static CoopButton _back;

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
            IsOpen = false;
            _currentTab = ScreenTab.Session;
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
            else if (CoopCore.InGameplayScene())
            {
                InteractionPlayerController val = Live<InteractionPlayerController>.Get();
                if (val != null && !CoopMenus.PauseOpen)
                {
                    val.ExitUIMode();
                    val.HideCursor();
                }
            }
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
            IsOpen = false;
            _currentTab = ScreenTab.Session;
            Unsubscribe();
            if (_root != null)
            {
                _root.SetActive(false);
            }
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

        private static bool ControllerBack()
        {
            InputManager val = Live<InputManager>.Get();
            if (val == null || !val.m_IsControllerActive)
            {
                return false;
            }
            return InputManager.GetKeyDownAction(EGameAction.MenuBack);
        }

        internal static void Destroy()
        {
            CancelResetDefaults();
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
                _nav.m_CloseScreenBtn = CoopMenus.AtTitle ? null : _back?.Btn;
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
            BuildJoin(panelRt);
            BuildSession(panelRt);
            BuildCharacter(panelRt);
            BuildAdvanced(panelRt);
            BuildStatus(panelRt);
            _back = NativeCoopUi.Button(panelRt, "Back", NativeCoopUi.Grey, OnBackClicked, 700f, 900f, 300f, 56f, 28f);
            _cancel = NativeCoopUi.Button(panelRt, "Cancel joining", NativeCoopUi.Orange, OnCancel, 40f, 900f, 300f, 56f, 26f);
            BuildResetConfirmation(panelRt);
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
            NativeCoopUi.Button(box, "Reset", NativeCoopUi.Orange, ConfirmResetDefaults,
                30f, 190f, 220f, 50f, 22f);
            NativeCoopUi.Button(box, "Cancel", NativeCoopUi.Grey, CancelResetDefaults,
                270f, 190f, 220f, 50f, 22f);
            _confirmGrp.SetActive(false);
        }

        private static void ShowTab(ScreenTab tab)
        {
            _currentTab = tab;
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
                    return;
                case ScreenTab.Character:
                    NativeCoopUi.Show(_joinGrp, on: false);
                    NativeCoopUi.Show(_sessGrp, on: false);
                    NativeCoopUi.Show(_advancedGrp, on: false);
                    NativeCoopUi.Show(_statusGrp, on: false);
                    NativeCoopUi.Show(_charGrp, on: true);
                    RefreshCharacter();
                    return;
            }
            NativeCoopUi.Show(_advancedGrp, on: false);
            NativeCoopUi.Show(_charGrp, on: false);
            NativeCoopUi.Show(_statusGrp, on: true);
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
            _playersHead = NativeCoopUi.Text(sessRt, "Players", 26f, 40f, 360f, 700f, 34f, NativeCoopUi.Gold, TextAlignmentOptions.Left, wrap: false, title: true);
            for (int i = 0; i < 8; i++)
            {
                int index = i;
                PlayerRow playerRow = new PlayerRow();
                RectTransform rowRt = NativeCoopUi.Node(sessRt, "Player" + i, 40f, 400f + (float)i * 40f, 960f, 36f);
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
            NativeCoopUi.Text(advRt, "Enables connection between Steam (Unity 2021) and Xbox App (Unity 6).", 17f, 40f, 272f, 960f, 24f, NativeCoopUi.Dim, TextAlignmentOptions.Left, wrap: false, title: false);
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
            CoopCharacterPrefabPreloader.EnsureStarted();
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
            if (_tabSessionBtn != null && _tabCharBtn != null && _tabAdvancedBtn != null)
            {
                Row(_nav, _tabSessionBtn.Ctrl, _tabCharBtn.Ctrl, _tabAdvancedBtn.Ctrl);
            }
            if (_back != null)
            {
                Row(_nav, _back.Ctrl);
            }
        }

        private static void Row(ControllerScreenUIExtension ext, params ControllerButton[] buttons)
        {
            List<ControllerButton> list = new List<ControllerButton>();
            for (int i = 0; i < buttons.Length; i++)
            {
                if (buttons[i] != null)
                {
                    list.Add(buttons[i]);
                }
            }
            if (list.Count > 0)
            {
                ext.m_ControllerBtnColumnList.Add(new ControllerBtnList
                {
                    rowList = list
                });
                ext.m_CtrlBtnXChangeMethodList.Add(ECtrlBtnXChangeMethod.Normal);
            }
        }

        private static void Refresh()
        {
            if (_currentTab == ScreenTab.Advanced)
            {
                RefreshAdvanced();
                return;
            }
            if (_currentTab == ScreenTab.Character)
            {
                RefreshCharacter();
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
        }

        private static void RefreshJoin()
        {
            bool connecting = CoopCore.Role != CoopRole.None;
            NativeCoopUi.Set(_title, "Join Co-op");
            NativeCoopUi.Set(_sub, connecting
                ? "Connecting to shop..."
                : "Logged in as: " + PlayerName);
            RefreshBackendForms(_joinForm, ConnectMode.Join);
            _cancel?.Show(connecting);
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

            if (none || !inShop)
            {
                if (_hostFormsLive)
                {
                    _hostFormsLive = false;
                    _hostForm.Signature = null;
                }
                NativeCoopUi.Show(_hostLiveRoot, on: false);
                RefreshBackendForms(_hostForm, ConnectMode.Host);
                NativeCoopUi.Show(_playersHead, on: false);
                for (var i = 0; i < Players.Length; i++)
                {
                    NativeCoopUi.Show(Players[i].Grp, on: false);
                    Players[i].Conn = -1;
                }
                _cancel?.Show(on: false);
                return;
            }

            _hostFormsLive = true;
            _hostForm.Signature = null;
            NativeCoopUi.Show(_hostForm.Root, on: false);
            NativeCoopUi.Show(_hostLiveRoot, on: true);
            ClearChildren(_hostLiveRoot);
            CoopBackendOptions.BuildLive(_hostLiveRoot.transform, Hint);
            NativeCoopUi.Show(_playersHead, on: true);

            IReadOnlyList<PresencePlayer> sessionPlayers = PresenceApi.SessionPlayers;
            int count = 0;
            if (sessionPlayers != null)
            {
                for (int i = 0; i < sessionPlayers.Count; i++)
                {
                    if (count >= 8)
                    {
                        break;
                    }
                    PresencePlayer presencePlayer = sessionPlayers[i];
                    string name = presencePlayer.Name + (presencePlayer.IsSelf ? " (you)" : "");
                    string info = presencePlayer.IsHost ? "Host" : ("Player #" + presencePlayer.Id);
                    SetPlayer(count++, name, info, (hosting && !presencePlayer.IsSelf)
                        ? presencePlayer.Id : (-1));
                }
            }
            for (int j = count; j < 8; j++)
            {
                NativeCoopUi.Show(Players[j].Grp, on: false);
                Players[j].Conn = -1;
            }
            NativeCoopUi.Set(_playersHead, count > 0 ? ("Players (" + count + ")") : "Players");
            _cancel?.Show(on: false);
        }

        private static void RefreshBackendForms(FormArea area, ConnectMode mode)
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
            var height = CoopBackendOptions.Build(area.Content, mode, FormContentWidth, Hint,
                Refresh);
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
        }

        private static void CancelResetDefaults()
        {
            if (_confirmGrp != null)
            {
                _confirmGrp.SetActive(false);
            }
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
            if (CoopPlugin.AvatarsEnabled != null)
            {
                CoopPlugin.AvatarsEnabled.Value = true;
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
            if (_advNameInput != null)
            {
                _advNameInput.text = "";
            }
            CoopBackendOptions.ResetValues();
            RefreshAdvanced();
            Hint("Advanced settings reset to default values.");
        }

        private static void OnCopyDiagnostics()
        {
            DiagnosticsReport.TryCopyBundle(out var message);
            Hint(message);
        }

        private static void RefreshCharacter() => CoopCharacterEditor.Refresh();

    }
}
