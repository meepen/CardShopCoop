using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
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

        internal static bool IsOpen;

        private static bool _openedPause;

        private static bool _navPushed;

        private static float _nextRefresh;

        private static float _hintUntil;

        private static string _hint = "";
        private static string _localIp;

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

        private static GameObject _sessGrp;

        private static GameObject _charGrp;

        private static GameObject _advancedGrp;

        private static GameObject _statusGrp;

        private static CoopButton _charGenderBtn;

        private static TextMeshProUGUI _charPresetText;

        private static CoopButton _charPreviewBtn;

        private static CoopButton _charUndoBtn;

        private static CoopButton _charRedoBtn;

        private static int _presetIndex = 0;

        private static bool _previewActive = false;

        private static GameObject _steamJoinGrp;

        private static GameObject _directGrp;

        private static GameObject _hostDirectGrp;

        private static TextMeshProUGUI _idLine;

        private static TextMeshProUGUI _playersHead;

        private static TextMeshProUGUI _status;

        private static TextMeshProUGUI _problem;

        private static readonly PlayerRow[] Players = new PlayerRow[8];

        private static TMP_InputField _ipInput;

        private static TMP_InputField _portInput;

        private static TMP_InputField _steamLobbyInput;

        private static TMP_InputField _hostPort;

        private static CoopButton _paste;

        private static CoopButton _joinDirect;

        private static CoopButton _joinSteam;

        private static CoopButton _browseSteam;

        private static CoopButton _steamInvite;

        private static CoopButton _steamCopyLobby;

        private static CoopButton _cancel;

        private static CoopButton _copy;

        private static CoopButton _hostDirect;

        private static CoopButton _hostSteam;

        private static CoopButton _stop;

        private static CoopButton _back;

        private static CoopButton _crossplayBtn;

        private static CoopButton _upnpBtn;

        private static CoopButton _avatarsBtn;

        private static TextMeshProUGUI _sendRateText;

        private static CoopButton _rateDownBtn;

        private static CoopButton _rateUpBtn;

        private static TMP_InputField _advNameInput;

        private static INetworkingBackend SteamBackend => NetworkingBackends.All().FirstOrDefault(b => b.Id == "steam");

        private static INetworkingBackend IpBackend => NetworkingBackends.All().FirstOrDefault(b => b.Id == "ip");

        private static bool HasSteam => SteamBackend != null;

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
            ShowTab(ScreenTab.Session);
            Refresh();
            PushNav();
            return true;
        }

        internal static void Close()
        {
            if (!IsOpen)
            {
                return;
            }
            IsOpen = false;
            _currentTab = ScreenTab.Session;
            if (_previewActive)
            {
                _previewActive = false;
                PresenceApi.SetCharacterPreview(active: false);
            }
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
            _openedPause = false;
            _navPushed = false;
            IsOpen = false;
            _currentTab = ScreenTab.Session;
            if (_previewActive)
            {
                _previewActive = false;
                PresenceApi.SetCharacterPreview(active: false);
            }
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
            else if (Time.unscaledTime >= _nextRefresh)
            {
                Refresh();
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
            try
            {
                PopNav();
            }
            catch (Exception ex)
            {
                CoopPlugin.Log?.LogWarning("coop: close controller navigation failed: " + ex.Message);
            }
            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
            }
            _root = null;
            _localIp = null;
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

        private static ControllerButton StartButton()
        {
            if (CoopMenus.AtTitle)
            {
                if (_joinDirect != null && _joinDirect.Go.activeInHierarchy)
                {
                    return _joinDirect.Ctrl;
                }
                if (_joinSteam != null && _joinSteam.Go.activeInHierarchy)
                {
                    return _joinSteam.Ctrl;
                }
            }
            else
            {
                if (_hostSteam != null && _hostSteam.Go.activeInHierarchy)
                {
                    return _hostSteam.Ctrl;
                }
                if (_hostDirect != null && _hostDirect.Go.activeInHierarchy)
                {
                    return _hostDirect.Ctrl;
                }
                if (_stop != null && _stop.Go.activeInHierarchy)
                {
                    return _stop.Ctrl;
                }
            }
            return _back?.Ctrl;
        }

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
            BuildNav();
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
            if (tab != ScreenTab.Character && _previewActive)
            {
                _previewActive = false;
                PresenceApi.SetCharacterPreview(active: false);
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
            RectTransform directRt = NativeCoopUi.Node(joinRt, "Direct", 0f, 0f, 1040f, 980f);
            _directGrp = directRt.gameObject;
            NativeCoopUi.Text(directRt, "Direct Connection (IP / LAN / Xbox App)", 26f, 40f, 180f, 960f, 34f, NativeCoopUi.Gold, TextAlignmentOptions.Left, wrap: false, title: true);
            NativeCoopUi.Text(directRt, "Enter host IP and Port, or click Paste to paste from clipboard / Game Bar.", 18f, 40f, 216f, 960f, 26f, NativeCoopUi.Dim, TextAlignmentOptions.Left, wrap: false, title: false);
            _ipInput = NativeCoopUi.Input(directRt, "Host IP address (e.g. 192.168.1.50)", 40f, 250f, 540f, 50f, 22f, 64, TMP_InputField.ContentType.Standard);
            _ipInput.text = string.IsNullOrEmpty(CoopPlugin.LastJoinIP.Value) ? "127.0.0.1" : CoopPlugin.LastJoinIP.Value;
            _portInput = NativeCoopUi.Input(directRt, "Port", 600f, 250f, 140f, 50f, 22f, 6, TMP_InputField.ContentType.IntegerNumber);
            _portInput.text = CoopPlugin.Port.Value.ToString();
            _paste = NativeCoopUi.Button(directRt, "Paste", NativeCoopUi.Blue, OnPaste, 760f, 250f, 100f, 50f, 22f);
            _joinDirect = NativeCoopUi.Button(directRt, "Join Direct", NativeCoopUi.Green, OnJoinDirect, 880f, 250f, 120f, 50f, 22f);
            RectTransform steamRt = NativeCoopUi.Node(joinRt, "SteamJoin", 0f, 0f, 1040f, 980f);
            _steamJoinGrp = steamRt.gameObject;
            NativeCoopUi.Text(steamRt, "Steam Lobby Connection", 26f, 40f, 330f, 960f, 34f, NativeCoopUi.Gold, TextAlignmentOptions.Left, wrap: false, title: true);
            NativeCoopUi.Text(steamRt, "Enter a Steam Lobby ID or browse public lobbies.", 18f, 40f, 366f, 960f, 26f, NativeCoopUi.Dim, TextAlignmentOptions.Left, wrap: false, title: false);
            _steamLobbyInput = NativeCoopUi.Input(steamRt, "Steam Lobby ID (e.g. 10977524...)", 40f, 400f, 540f, 50f, 22f, 32, TMP_InputField.ContentType.Standard);
            _joinSteam = NativeCoopUi.Button(steamRt, "Join Lobby", NativeCoopUi.Green, OnJoinSteam, 600f, 400f, 180f, 50f, 22f);
            _browseSteam = NativeCoopUi.Button(steamRt, "Browse Lobbies", NativeCoopUi.Blue, OnBrowseSteam, 800f, 400f, 200f, 50f, 22f);
        }

        private static void BuildSession(Transform p)
        {
            RectTransform sessRt = NativeCoopUi.Node(p, "Session", 0f, 0f, 1040f, 980f);
            _sessGrp = sessRt.gameObject;
            _idLine = NativeCoopUi.Text(sessRt, "", 22f, 40f, 180f, 480f, 42f, Color.white, TextAlignmentOptions.Left, wrap: false, title: false);
            _copy = NativeCoopUi.Button(sessRt, "Copy IP:Port", NativeCoopUi.Blue, OnCopy, 540f, 180f, 180f, 44f, 22f);
            _steamCopyLobby = NativeCoopUi.Button(sessRt, "Copy Lobby ID", NativeCoopUi.Blue, OnSteamCopyLobby, 740f, 180f, 200f, 44f, 22f);
            RectTransform hostDirectRt = NativeCoopUi.Node(sessRt, "HostDirectGrp", 0f, 0f, 1040f, 980f);
            _hostDirectGrp = hostDirectRt.gameObject;
            _hostPort = NativeCoopUi.Input(hostDirectRt, "Port", 40f, 236f, 140f, 50f, 22f, 6, TMP_InputField.ContentType.IntegerNumber);
            _hostPort.text = CoopPlugin.Port.Value.ToString();
            _hostDirect = NativeCoopUi.Button(hostDirectRt, "Host Direct (LAN / Xbox)", NativeCoopUi.Green, OnHostDirect, 200f, 236f, 380f, 50f, 22f);
            _hostSteam = NativeCoopUi.Button(hostDirectRt, "Host on Steam", NativeCoopUi.Green, OnHostSteam, 600f, 236f, 380f, 50f, 22f);
            _steamInvite = NativeCoopUi.Button(sessRt, "Invite (Steam Overlay)", NativeCoopUi.Purple, OnSteamInvite, 40f, 236f, 400f, 50f, 22f);
            _stop = NativeCoopUi.Button(sessRt, "Stop hosting", NativeCoopUi.Orange, OnStop, 460f, 236f, 400f, 50f, 22f);
            _playersHead = NativeCoopUi.Text(sessRt, "Players", 26f, 40f, 305f, 700f, 34f, NativeCoopUi.Gold, TextAlignmentOptions.Left, wrap: false, title: true);
            for (int i = 0; i < 8; i++)
            {
                int index = i;
                PlayerRow playerRow = new PlayerRow();
                RectTransform rowRt = NativeCoopUi.Node(sessRt, "Player" + i, 40f, 348f + (float)i * 44f, 960f, 40f);
                playerRow.Grp = rowRt.gameObject;
                NativeCoopUi.Panel(rowRt, 0f, 0f, 960f, 40f, NativeCoopUi.RowFill, 4f);
                playerRow.Name = NativeCoopUi.Text(rowRt, "", 22f, 20f, 0f, 500f, 40f, Color.white, TextAlignmentOptions.Left, wrap: false, title: false);
                playerRow.Info = NativeCoopUi.Text(rowRt, "", 18f, 540f, 0f, 260f, 40f, NativeCoopUi.Dim, TextAlignmentOptions.Left, wrap: false, title: false);
                playerRow.Kick = NativeCoopUi.Button(rowRt, "Kick", NativeCoopUi.Orange, () => OnKick(index), 820f, 4f, 120f, 32f, 18f);
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
            NativeCoopUi.Text(advRt, "Auto Port Forward (UPnP):", 22f, 40f, 310f, 540f, 40f, Color.white, TextAlignmentOptions.Left, wrap: false, title: false);
            _upnpBtn = NativeCoopUi.Button(advRt, CoopPlugin.AutoPortForward.Value ? "ENABLED" : "DISABLED", CoopPlugin.AutoPortForward.Value ? NativeCoopUi.Green : NativeCoopUi.Orange, OnToggleUpnp, 620f, 310f, 220f, 40f, 20f);
            NativeCoopUi.Text(advRt, "Automatically forwards the host port on your home router when hosting.", 17f, 40f, 352f, 960f, 24f, NativeCoopUi.Dim, TextAlignmentOptions.Left, wrap: false, title: false);
            NativeCoopUi.Text(advRt, "Show Walking Avatars:", 22f, 40f, 390f, 540f, 40f, Color.white, TextAlignmentOptions.Left, wrap: false, title: false);
            _avatarsBtn = NativeCoopUi.Button(advRt, CoopPlugin.AvatarsEnabled.Value ? "ENABLED" : "DISABLED", CoopPlugin.AvatarsEnabled.Value ? NativeCoopUi.Green : NativeCoopUi.Orange, OnToggleAvatars, 620f, 390f, 220f, 40f, 20f);
            NativeCoopUi.Text(advRt, "Displays friends as 3D walking character models inside your shop.", 17f, 40f, 432f, 960f, 24f, NativeCoopUi.Dim, TextAlignmentOptions.Left, wrap: false, title: false);
            _sendRateText = NativeCoopUi.Text(advRt, $"Network Send Rate: {CoopPlugin.SendRateHz.Value:F0} Hz", 22f, 40f, 470f, 540f, 40f, Color.white, TextAlignmentOptions.Left, wrap: false, title: false);
            _rateDownBtn = NativeCoopUi.Button(advRt, "- 5 Hz", NativeCoopUi.Blue, OnRateDown, 620f, 470f, 105f, 40f, 20f);
            _rateUpBtn = NativeCoopUi.Button(advRt, "+ 5 Hz", NativeCoopUi.Blue, OnRateUp, 735f, 470f, 105f, 40f, 20f);
            NativeCoopUi.Text(advRt, "Position update frequency per second (15 Hz is standard).", 17f, 40f, 512f, 960f, 24f, NativeCoopUi.Dim, TextAlignmentOptions.Left, wrap: false, title: false);
            NativeCoopUi.Text(advRt, "Custom Display Name:", 22f, 40f, 550f, 320f, 42f, Color.white, TextAlignmentOptions.Left, wrap: false, title: false);
            _advNameInput = NativeCoopUi.Input(advRt, "Name", 370f, 550f, 280f, 42f, 20f, 24, TMP_InputField.ContentType.Standard);
            _advNameInput.text = CoopPlugin.NameOverride.Value ? CoopPlugin.PlayerName.Value : "";
            NativeCoopUi.Button(advRt, "Save Name", NativeCoopUi.Green, OnSaveName, 665f, 550f, 175f, 42f, 20f);
            NativeCoopUi.Text(advRt, "Leave empty to use your platform name (Xbox Gamertag / Steam).", 17f, 40f, 594f, 960f, 24f, NativeCoopUi.Dim, TextAlignmentOptions.Left, wrap: false, title: false);
            RectTransform diagRt = NativeCoopUi.Node(advRt, "DiagWell", 40f, 640f, 960f, 140f);
            NativeCoopUi.Panel(diagRt, 0f, 0f, 960f, 140f, NativeCoopUi.WellFill, 2.6f);
            NativeCoopUi.Text(diagRt, "Diagnostic Tools & Full Console", 20f, 20f, 12f, 920f, 32f, NativeCoopUi.Dim, TextAlignmentOptions.Left, wrap: false, title: false);
            NativeCoopUi.Button(diagRt, "Open Full Debug Window (IMGUI)", NativeCoopUi.Grey, OnToggleAdvanced, 20f, 50f, 440f, 48f, 20f);
            NativeCoopUi.Button(diagRt, "Reset Defaults", NativeCoopUi.Orange, OnResetDefaults, 480f, 50f, 220f, 48f, 20f);
            NativeCoopUi.Text(diagRt, "Full debug window contains raw packet counters and network logs.", 16f, 20f, 104f, 920f, 26f, NativeCoopUi.Dim, TextAlignmentOptions.Left, wrap: false, title: false);
        }

        private static void BuildCharacter(Transform p)
        {
            RectTransform charRt = NativeCoopUi.Node(p, "Character", 0f, 0f, 1040f, 980f);
            _charGrp = charRt.gameObject;
            _charGrp.SetActive(false);
            NativeCoopUi.Text(charRt, "Personalizzazione Personaggio & Avatar 3D", 28f, 40f, 180f, 960f, 36f, NativeCoopUi.Gold, TextAlignmentOptions.Left, wrap: false, title: true);
            NativeCoopUi.Text(charRt, "Modello Avatar / Genere:", 22f, 40f, 230f, 400f, 40f, Color.white, TextAlignmentOptions.Left, wrap: false, title: false);
            _charGenderBtn = NativeCoopUi.Button(charRt, "Maschile", NativeCoopUi.Blue, OnToggleCharGender, 460f, 230f, 300f, 40f, 20f);
            NativeCoopUi.Text(charRt, "Cambia il corpo base tra modello maschile e femminile.", 17f, 40f, 272f, 960f, 24f, NativeCoopUi.Dim, TextAlignmentOptions.Left, wrap: false, title: false);
            NativeCoopUi.Text(charRt, "Preset Rapidi:", 22f, 40f, 310f, 240f, 40f, Color.white, TextAlignmentOptions.Left, wrap: false, title: false);
            NativeCoopUi.Button(charRt, "< Prec", NativeCoopUi.Blue, OnCharPrevPreset, 300f, 310f, 90f, 40f, 20f);
            _charPresetText = NativeCoopUi.Text(charRt, "Default", 20f, 400f, 310f, 260f, 40f, Color.yellow, TextAlignmentOptions.Center, wrap: false, title: false);
            NativeCoopUi.Button(charRt, "Succ >", NativeCoopUi.Blue, OnCharNextPreset, 670f, 310f, 90f, 40f, 20f);
            NativeCoopUi.Button(charRt, "Applica", NativeCoopUi.Green, OnCharApplyPreset, 770f, 310f, 120f, 40f, 20f);
            NativeCoopUi.Text(charRt, "Applica stili predefiniti di viso, capelli e abbigliamento.", 17f, 40f, 352f, 960f, 24f, NativeCoopUi.Dim, TextAlignmentOptions.Left, wrap: false, title: false);
            NativeCoopUi.Text(charRt, "Anteprima 3D in Gioco:", 22f, 40f, 390f, 400f, 40f, Color.white, TextAlignmentOptions.Left, wrap: false, title: false);
            _charPreviewBtn = NativeCoopUi.Button(charRt, "ANTEPRIMA: NO", NativeCoopUi.Orange, OnToggleCharPreview, 460f, 390f, 300f, 40f, 20f);
            NativeCoopUi.Text(charRt, "Genera l'avatar 3D davanti alla telecamera durante la configurazione.", 17f, 40f, 432f, 960f, 24f, NativeCoopUi.Dim, TextAlignmentOptions.Left, wrap: false, title: false);
            NativeCoopUi.Text(charRt, "Cronologia Modifiche:", 22f, 40f, 470f, 300f, 40f, Color.white, TextAlignmentOptions.Left, wrap: false, title: false);
            _charUndoBtn = NativeCoopUi.Button(charRt, "Annulla (Undo)", NativeCoopUi.Blue, OnCharUndo, 360f, 470f, 180f, 40f, 20f);
            _charRedoBtn = NativeCoopUi.Button(charRt, "Ripristina (Redo)", NativeCoopUi.Blue, OnCharRedo, 560f, 470f, 180f, 40f, 20f);
            NativeCoopUi.Text(charRt, "Annulla o ripristina le ultime modifiche estetiche apportate.", 17f, 40f, 512f, 960f, 24f, NativeCoopUi.Dim, TextAlignmentOptions.Left, wrap: false, title: false);
            RectTransform wellRt = NativeCoopUi.Node(charRt, "CharWell", 40f, 560f, 960f, 160f);
            NativeCoopUi.Panel(wellRt, 0f, 0f, 960f, 160f, NativeCoopUi.WellFill, 2.6f);
            NativeCoopUi.Text(wellRt, "Editor Aspetto & Guardaroba Completo", 22f, 20f, 16f, 920f, 34f, NativeCoopUi.Gold, TextAlignmentOptions.Left, wrap: false, title: false);
            NativeCoopUi.Text(wellRt, "Accedi a tutte le acconciature, tinte per capelli, guardaroba abiti, carnagione e slider del viso.", 17f, 20f, 52f, 920f, 28f, NativeCoopUi.Dim, TextAlignmentOptions.Left, wrap: false, title: false);
            NativeCoopUi.Button(wellRt, "Apri Editor Completo (Capelli / Vestiti / Colori)", NativeCoopUi.Purple, OnOpenFullCustomizer, 20f, 92f, 560f, 52f, 22f);
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
            if (_joinDirect != null && _paste != null)
            {
                Row(_nav, _paste.Ctrl, _joinDirect.Ctrl);
            }
            if (_joinSteam != null)
            {
                Row(_nav, _joinSteam.Ctrl);
            }
            if (_hostDirect != null && _hostSteam != null)
            {
                Row(_nav, _hostDirect.Ctrl, _hostSteam.Ctrl);
            }
            if (_stop != null)
            {
                Row(_nav, _stop.Ctrl);
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
            _nextRefresh = Time.unscaledTime + 0.35f;
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
            bool flag = CoopCore.Role != CoopRole.None;
            NativeCoopUi.Set(_title, "Join Co-op");
            NativeCoopUi.Set(_sub, flag ? "Connecting to shop..." : ("Logged in as: " + PlayerName));
            if (_joinDirect != null)
            {
                _joinDirect.Show(!flag);
            }
            if (_paste != null)
            {
                _paste.Show(!flag);
            }
            bool hasSteam = HasSteam;
            NativeCoopUi.Show(_steamJoinGrp, hasSteam);
            if (_joinSteam != null)
            {
                _joinSteam.Show(hasSteam && !flag);
            }
            if (_browseSteam != null)
            {
                _browseSteam.Show(hasSteam && !flag);
            }
            if (_cancel != null)
            {
                _cancel.Show(flag);
            }
        }

        private static void RefreshSession()
        {
            bool flag = CoopCore.Role == CoopRole.Host;
            bool flag2 = CoopCore.Role == CoopRole.Client;
            bool inShop = CoopMenus.InShop;
            string text = flag ? "Hosting Co-op" : (flag2 ? "Co-op Session" : "Host Co-op");
            NativeCoopUi.Set(_title, text);
            NativeCoopUi.Set(_sub, flag ? "Friends can join using Direct IP, Xbox Game Bar, or Steam." : (flag2 ? "Playing in host's shop." : (inShop ? ("Logged in as: " + PlayerName + " | Host your shop for friends.") : "Load or start your shop first to host.")));
            int num = ParsePort(_hostPort);
            string localIP = LocalIP;
            NativeCoopUi.Set(_idLine, "Your Local IP: " + localIP + ":" + num);
            bool flag3 = !flag && !flag2 && inShop;
            NativeCoopUi.Show(_hostDirectGrp, flag3);
            if (_hostDirect != null)
            {
                _hostDirect.Show(flag3);
            }
            if (_hostSteam != null)
            {
                _hostSteam.Show(flag3 && HasSteam);
            }
            bool on = flag || flag2;
            if (_stop != null)
            {
                _stop.Show(on);
                _stop.SetLabel(flag ? "Stop hosting" : "Leave shop");
            }
            if (_copy != null)
            {
                _copy.Show(flag || flag3);
            }
            bool on2 = flag && HasSteam;
            if (_steamInvite != null)
            {
                _steamInvite.Show(on2);
            }
            if (_steamCopyLobby != null)
            {
                _steamCopyLobby.Show(on2);
            }
            IReadOnlyList<PresencePlayer> sessionPlayers = PresenceApi.SessionPlayers;
            int num2 = 0;
            if (sessionPlayers != null)
            {
                for (int i = 0; i < sessionPlayers.Count; i++)
                {
                    if (num2 >= 8)
                    {
                        break;
                    }
                    PresencePlayer presencePlayer = sessionPlayers[i];
                    string name = presencePlayer.Name + (presencePlayer.IsSelf ? " (you)" : "");
                    string info = presencePlayer.IsHost ? "Host" : ("Player #" + presencePlayer.Id);
                    SetPlayer(num2++, name, info, (flag && !presencePlayer.IsSelf) ? presencePlayer.Id : (-1));
                }
            }
            for (int j = num2; j < 8; j++)
            {
                NativeCoopUi.Show(Players[j].Grp, on: false);
                Players[j].Conn = -1;
            }
            NativeCoopUi.Set(_playersHead, num2 > 0 ? ("Players (" + num2 + ")") : "Players");
            if (_cancel != null)
            {
                _cancel.Show(on: false);
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
            if (_upnpBtn != null && CoopPlugin.AutoPortForward != null)
            {
                bool value2 = CoopPlugin.AutoPortForward.Value;
                _upnpBtn.SetLabel(value2 ? "ENABLED" : "DISABLED");
                _upnpBtn.SetStyle(value2 ? NativeCoopUi.Green : NativeCoopUi.Orange);
            }
            if (_avatarsBtn != null && CoopPlugin.AvatarsEnabled != null)
            {
                bool value3 = CoopPlugin.AvatarsEnabled.Value;
                _avatarsBtn.SetLabel(value3 ? "ENABLED" : "DISABLED");
                _avatarsBtn.SetStyle(value3 ? NativeCoopUi.Green : NativeCoopUi.Orange);
            }
            if (_sendRateText != null && CoopPlugin.SendRateHz != null)
            {
                NativeCoopUi.Set(_sendRateText, $"Network Send Rate: {CoopPlugin.SendRateHz.Value:F0} Hz");
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

        private static void RefreshStatus()
        {
            string text = Time.unscaledTime < _hintUntil ? _hint : (CoopCore.Instance != null ? CoopCore.Instance.StatusLine : "");
            NativeCoopUi.Set(_status, text ?? "");
            string text2 = CoopCore.Instance != null ? CoopCore.Instance.ErrorLine : "";
            NativeCoopUi.Set(_problem, text2 ?? "");
        }

        private static void Hint(string text)
        {
            _hint = text;
            _hintUntil = Time.unscaledTime + 5f;
            RefreshStatus();
        }

        private static int ParsePort(TMP_InputField f)
        {
            if (f != null && int.TryParse(f.text.Trim(), out var result) && result > 0 && result <= 65535)
            {
                return result;
            }
            if (CoopPlugin.Port == null)
            {
                return 27886;
            }
            return CoopPlugin.Port.Value;
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

        private static string LocalIP
        {
            get
            {
                if (!string.IsNullOrEmpty(_localIp))
                {
                    return _localIp;
                }

                var ip = NetHelpers.LocalIPv4ForGateway() ?? NetHelpers.LocalIPv4();
                _localIp = string.IsNullOrEmpty(ip) ? "127.0.0.1" : ip;
                return _localIp;
            }
        }

        private static void OnPaste()
        {
            string text = (GUIUtility.systemCopyBuffer ?? "").Trim();
            if (text.Contains(":"))
            {
                string[] array = text.Split(':');
                if (_ipInput != null)
                {
                    _ipInput.text = array[0].Trim();
                }
                if (array.Length > 1 && _portInput != null && int.TryParse(array[1].Trim(), out var result))
                {
                    _portInput.text = result.ToString();
                }
                Hint("Host IP:Port pasted: " + text);
            }
            else if (text.Length > 0)
            {
                if (_ipInput != null)
                {
                    _ipInput.text = text;
                }
                Hint("Host IP pasted: " + text);
            }
            else
            {
                Hint("Clipboard is empty.");
            }
        }

        private static void OnCopy()
        {
            int num = ParsePort(_hostPort);
            GUIUtility.systemCopyBuffer = LocalIP + ":" + num;
            Hint("Host IP:Port (" + GUIUtility.systemCopyBuffer + ") copied to clipboard! Share it in Xbox App / Game Bar.");
        }

        private static void OnJoinDirect()
        {
            string text = _ipInput != null ? _ipInput.text.Trim() : "127.0.0.1";
            int num = ParsePort(_portInput);
            if (string.IsNullOrEmpty(text))
            {
                Hint("Please enter a valid IP address.");
                return;
            }
            if (CoopPlugin.LastJoinIP != null)
            {
                CoopPlugin.LastJoinIP.Value = text;
            }
            INetworkingBackend ipBackend = IpBackend;
            if (ipBackend == null)
            {
                Hint("IP networking backend not available.");
                return;
            }
            FieldValues fieldValues = new FieldValues();
            fieldValues[FieldId.IpAddress] = text;
            fieldValues[FieldId.Port] = num.ToString();
            CoopCore.Instance?.ConnectBackend(ipBackend, ConnectMode.Join, fieldValues);
            Hint("Connecting to " + text + ":" + num + "...");
            Refresh();
        }

        private static void OnJoinSteam()
        {
            string text = _steamLobbyInput != null ? _steamLobbyInput.text.Trim() : "";
            if (string.IsNullOrEmpty(text))
            {
                Hint("Please enter a Steam Lobby ID.");
                return;
            }
            INetworkingBackend steamBackend = SteamBackend;
            if (steamBackend == null)
            {
                Hint("Steam networking backend not available.");
                return;
            }
            FieldValues fieldValues = new FieldValues();
            fieldValues[FieldId.Lobby] = text;
            steamBackend.Configure(fieldValues);
            CoopCore.Instance?.ConnectBackend(steamBackend, ConnectMode.Join, fieldValues);
            Hint("Joining Steam Lobby " + text + "...");
            Refresh();
        }

        private static void OnBrowseSteam()
        {
            INetworkingBackend steamBackend = SteamBackend;
            if (steamBackend != null)
            {
                BackendAction backendAction = steamBackend.Actions.FirstOrDefault(a => a.Id == "servers");
                if (backendAction != null)
                {
                    backendAction.Invoke();
                    return;
                }
            }
            Hint("Steam server browser not available.");
        }

        private static void OnSteamInvite()
        {
            INetworkingBackend steamBackend = SteamBackend;
            if (steamBackend != null)
            {
                BackendAction backendAction = steamBackend.Actions.FirstOrDefault(a => a.Id == "invite");
                if (backendAction != null)
                {
                    backendAction.Invoke();
                    return;
                }
            }
            Hint("Steam invite overlay not available.");
        }

        private static void OnSteamCopyLobby()
        {
            INetworkingBackend steamBackend = SteamBackend;
            if (steamBackend != null)
            {
                BackendAction backendAction = steamBackend.Actions.FirstOrDefault(a => a.Id == "copyLobbyId");
                if (backendAction != null)
                {
                    backendAction.Invoke();
                    Hint("Steam Lobby ID copied to clipboard!");
                    return;
                }
            }
            Hint("Steam lobby id not available.");
        }

        private static void OnCancel()
        {
            CoopCore.Instance?.Disconnect();
            Hint("Cancelled connection.");
            Refresh();
        }

        private static void OnHostDirect()
        {
            int value = ParsePort(_hostPort);
            if (CoopPlugin.Port != null)
            {
                CoopPlugin.Port.Value = value;
            }
            INetworkingBackend ipBackend = IpBackend;
            if (ipBackend == null)
            {
                Hint("IP networking backend not available.");
                return;
            }
            FieldValues fieldValues = new FieldValues();
            fieldValues[FieldId.Port] = value.ToString();
            fieldValues[FieldId.AutoForward] = (CoopPlugin.AutoPortForward != null && CoopPlugin.AutoPortForward.Value) ? "1" : "0";
            CoopCore.Instance?.ConnectBackend(ipBackend, ConnectMode.Host, fieldValues);
            Hint("Hosting Direct on port " + value + "...");
            Refresh();
        }

        private static void OnHostSteam()
        {
            INetworkingBackend steamBackend = SteamBackend;
            if (steamBackend == null)
            {
                Hint("Steam networking backend not available.");
                return;
            }
            FieldValues values = new FieldValues();
            steamBackend.Configure(values);
            CoopCore.Instance?.ConnectBackend(steamBackend, ConnectMode.Host, values);
            Hint("Hosting on Steam...");
            Refresh();
        }

        private static void OnStop()
        {
            CoopCore.Instance?.Disconnect();
            Hint("Disconnected.");
            Refresh();
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

        private static void OnToggleUpnp()
        {
            if (CoopPlugin.AutoPortForward != null)
            {
                CoopPlugin.AutoPortForward.Value = !CoopPlugin.AutoPortForward.Value;
                RefreshAdvanced();
                Hint("Auto Port Forward (UPnP): " + (CoopPlugin.AutoPortForward.Value ? "ENABLED" : "DISABLED"));
            }
        }

        private static void OnToggleAvatars()
        {
            if (CoopPlugin.AvatarsEnabled != null)
            {
                CoopPlugin.AvatarsEnabled.Value = !CoopPlugin.AvatarsEnabled.Value;
                RefreshAdvanced();
                Hint("Walking Avatars: " + (CoopPlugin.AvatarsEnabled.Value ? "ENABLED" : "DISABLED"));
            }
        }

        private static void OnRateDown()
        {
            if (CoopPlugin.SendRateHz != null)
            {
                float value = Mathf.Max(5f, CoopPlugin.SendRateHz.Value - 5f);
                CoopPlugin.SendRateHz.Value = value;
                RefreshAdvanced();
                Hint("Send Rate: " + value + " Hz");
            }
        }

        private static void OnRateUp()
        {
            if (CoopPlugin.SendRateHz != null)
            {
                float value = Mathf.Min(60f, CoopPlugin.SendRateHz.Value + 5f);
                CoopPlugin.SendRateHz.Value = value;
                RefreshAdvanced();
                Hint("Send Rate: " + value + " Hz");
            }
        }

        private static void OnSaveName()
        {
            if (_advNameInput != null)
            {
                string text = _advNameInput.text.Trim();
                if (CoopPlugin.PlayerName != null)
                {
                    CoopPlugin.PlayerName.Value = text;
                }
                if (CoopPlugin.NameOverride != null)
                {
                    CoopPlugin.NameOverride.Value = !string.IsNullOrEmpty(text);
                }
                Hint(string.IsNullOrEmpty(text) ? "Display name reset to platform default." : ("Display name saved: " + text));
            }
        }

        private static void OnResetDefaults()
        {
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
            RefreshAdvanced();
            Hint("Advanced settings reset to default values.");
        }

        private static void OnToggleAdvanced()
        {
            if (CoopCore.Instance != null)
            {
                CoopCore.Instance.ToggleLegacyUI();
            }
        }

        private static void OnToggleCharGender()
        {
            bool female = PresenceApi.GetLocalPlayerModel().Female;
            PresenceApi.SetLocalPlayerModel(!female, 0);
            RefreshCharacter();
            Hint("Modello avatar: " + (!female ? "Femminile" : "Maschile"));
        }

        private static void OnCharPrevPreset()
        {
            List<string> localPresetNames = PresenceApi.GetLocalPresetNames();
            if (localPresetNames.Count != 0)
            {
                _presetIndex = (_presetIndex - 1 + localPresetNames.Count) % localPresetNames.Count;
                if (_charPresetText != null)
                {
                    _charPresetText.text = localPresetNames[_presetIndex];
                }
            }
        }

        private static void OnCharNextPreset()
        {
            List<string> localPresetNames = PresenceApi.GetLocalPresetNames();
            if (localPresetNames.Count != 0)
            {
                _presetIndex = (_presetIndex + 1) % localPresetNames.Count;
                if (_charPresetText != null)
                {
                    _charPresetText.text = localPresetNames[_presetIndex];
                }
            }
        }

        private static void OnCharApplyPreset()
        {
            List<string> localPresetNames = PresenceApi.GetLocalPresetNames();
            if (localPresetNames.Count != 0 && _presetIndex >= 0 && _presetIndex < localPresetNames.Count)
            {
                PresenceApi.ApplyLocalPreset(localPresetNames[_presetIndex]);
                RefreshCharacter();
                Hint("Applicato preset: " + localPresetNames[_presetIndex]);
            }
        }

        private static void OnToggleCharPreview()
        {
            _previewActive = !_previewActive;
            PresenceApi.SetCharacterPreview(_previewActive);
            if (_charPreviewBtn != null)
            {
                _charPreviewBtn.SetLabel(_previewActive ? "ANTEPRIMA: ATTIVA" : "ANTEPRIMA: NO");
                _charPreviewBtn.SetStyle(_previewActive ? NativeCoopUi.Green : NativeCoopUi.Orange);
            }
            Hint("Anteprima 3D: " + (_previewActive ? "ATTIVA" : "DISATTIVATA"));
        }

        private static void OnCharUndo()
        {
            if (PresenceApi.UndoPlayerModel())
            {
                RefreshCharacter();
                Hint("Modifica annullata.");
            }
            else
            {
                Hint("Nessuna azione da annullare.");
            }
        }

        private static void OnCharRedo()
        {
            if (PresenceApi.RedoPlayerModel())
            {
                RefreshCharacter();
                Hint("Modifica ripristinata.");
            }
            else
            {
                Hint("Nessuna azione da ripristinare.");
            }
        }

        private static void OnOpenFullCustomizer()
        {
            PresenceApi.SetCharacterPreview(active: true);
            _previewActive = true;
            CoopCore.Instance?.OpenCharacterCustomizer();
        }

        private static void RefreshCharacter()
        {
            PresenceModelEntry localPlayerModel = PresenceApi.GetLocalPlayerModel();
            if (_charGenderBtn != null)
            {
                _charGenderBtn.SetLabel(localPlayerModel.Female ? "Femminile" : "Maschile");
                _charGenderBtn.SetStyle(localPlayerModel.Female ? NativeCoopUi.Purple : NativeCoopUi.Blue);
            }
            List<string> localPresetNames = PresenceApi.GetLocalPresetNames();
            if (localPresetNames.Count > 0)
            {
                if (_presetIndex < 0 || _presetIndex >= localPresetNames.Count)
                {
                    _presetIndex = 0;
                }
                if (_charPresetText != null)
                {
                    _charPresetText.text = localPresetNames[_presetIndex];
                }
            }
            if (_charPreviewBtn != null)
            {
                _charPreviewBtn.SetLabel(_previewActive ? "ANTEPRIMA: ATTIVA" : "ANTEPRIMA: NO");
                _charPreviewBtn.SetStyle(_previewActive ? NativeCoopUi.Green : NativeCoopUi.Orange);
            }
        }
    }
}
