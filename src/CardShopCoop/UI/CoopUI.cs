using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CardShopCoop.Net;
// NO `using Steamworks;` HERE, AND NEVER AGAIN - see the same note in CoopCore.cs. OnGUI
// is the worst possible place for a type-load fault (DrawNone runs every frame on the main
// menu), so every lobby id in this file is a plain ulong and lobby rows are Net.LobbyRow.
using UnityEngine;

namespace CardShopCoop.UI
{
    /// <summary>Small IMGUI window: host / join / status. Toggled with the UI key.
    /// Presentation is driven by <see cref="CoopTheme"/> (cozy card-shop paper + teal).</summary>
    public class CoopUI
    {
        public bool Visible = true;
        public static bool TextFieldFocused;

        private Rect _win = new Rect(24f, 96f, 400f, 10f);
        private string _ipField;
        private string _nameField;
        private string _lanIps;
        private string _lanIpsOther;
        private bool _revealIp;
        private string _inviteField = "";
        private bool _inviteCopied;
        private string _inviteSeen;
        private string _lanPwField = "";

        // LATCHED COPIES OF THE INVITE FIELDS, and the reason they exist is IMGUI's two-pass
        // model rather than anything about threads. GUILayout matches the Layout pass against
        // the Repaint pass BY CONTROL INDEX: if a field changes value between the two passes
        // and that value decides whether a row is drawn, the counts disagree and Unity throws
        // "GUILayout: Mismatched LayoutGroup" over the whole window. CoopCore now publishes
        // these on the main thread, which makes a mid-frame change unlikely rather than
        // impossible (a queued publish drains in Update, and OnGUI can run more than once per
        // Update). Latching in Layout only - the same trick _lanIps already uses - makes both
        // passes draw from one immutable snapshot, so the question cannot arise.
        private InviteState _invState;
        private string _invCode, _invReason, _invPassword;
        private int _invPortState;

        // lobby browser + host options
        private bool _browserOpen;
        private string _searchField = "";
        private int _page;
        private bool _publicLobby;
        private string _lobbyNameField = "";
        private string _hostPwField = "";
        private string _joinPwField = "";
        private ulong _pwPromptLobby; // 0 = no password prompt open
        private const int PageSize = 6;

        // OnGUI runs 2+ times per frame; GUIStyle construction and string interpolation there
        // is steady per-frame garbage. Styles/textures live in CoopTheme (built once, cached);
        // the HUD display strings - and the GUIContent wrappers the pills measure - are rebuilt
        // only when their source line changes.
        private KeyCode _hintKeySeen;
        private string _hintText; private GUIContent _hintGc;
        private string _errorSeen, _errorText; private GUIContent _errorGc;
        private string _enumRestoreMsg; // outcome line under the enum-lend notice
        private string _hostTimeSeen, _hostTimeText; private GUIContent _hostTimeGc;
        private string _promptSeen, _promptText; private GUIContent _promptGc;
        private string _registerSeen, _registerText; private GUIContent _registerGc;

        /// <summary>Lower = more likely the real home-LAN address.</summary>
        private static int IpRank(string ip)
        {
            if (ip.StartsWith("192.168.")) return 0;             // classic home router
            if (ip.StartsWith("10.")) return 1;                   // some routers/VPNs
            if (ip.StartsWith("172.")) return 2;                  // usually WSL/Hyper-V/Docker
            return 3;
        }

        public void Draw(CoopCore core, ICoopTransport net)
        {
            CoopTheme.EnsureBuilt();
            if (_ipField == null) _ipField = CoopPlugin.LastJoinIP.Value;
            if (_nameField == null) _nameField = CoopPlugin.PlayerName.Value;

            // ---- HUD overlays (outside the window) ----
            if (!Visible)
            {
                // a hidden window cannot have a focused coop_ field, but IMGUI keeps the
                // last focus NAME alive after the window stops drawing - without this
                // reset, closing the window right after typing left TextFieldFocused
                // stuck TRUE and silently ate the serve key for the rest of the session
                TextFieldFocused = false;
                // little always-on hint (bottom-left)
                if (_hintText == null || _hintKeySeen != CoopPlugin.UiToggleKey.Value)
                {
                    _hintKeySeen = CoopPlugin.UiToggleKey.Value;
                    _hintText = $"<size=11><color=#9fd3ff>CardShopCoop: {_hintKeySeen} for co-op</color></size>";
                    _hintGc = new GUIContent(_hintText);
                }
                Vector2 hintSize = CoopTheme.PillSize(CoopTheme.HudPill, _hintGc, 440f);
                GUI.Label(new Rect(8f, Screen.height - hintSize.y - 6f, hintSize.x, hintSize.y), _hintGc, CoopTheme.HudPill);

                if (core.ErrorLine.Length > 0)
                {
                    if (core.ErrorLine != _errorSeen)
                    {
                        _errorSeen = core.ErrorLine;
                        _errorText = $"<size=13><color=#ff5a4a>CO-OP: {core.ErrorLine}</color></size>";
                        _errorGc = new GUIContent(_errorText);
                    }
                    Vector2 errSize = CoopTheme.PillSize(CoopTheme.HudPill, _errorGc, 660f);
                    GUI.Label(new Rect(8f, Screen.height - hintSize.y - 6f - errSize.y - 6f, errSize.x, errSize.y),
                        _errorGc, CoopTheme.HudPill);
                }
            }
            if (CoopCore.Role == CoopRole.Client && core.HostTimeLine.Length > 0)
            {
                if (core.HostTimeLine != _hostTimeSeen)
                {
                    _hostTimeSeen = core.HostTimeLine;
                    _hostTimeText = $"<color=#ffd54a>{core.HostTimeLine} - co-op</color>";
                    _hostTimeGc = new GUIContent(_hostTimeText);
                }
                Vector2 sz = CoopTheme.PillSize(CoopTheme.HudPill, _hostTimeGc, 440f);
                GUI.Label(new Rect((Screen.width - sz.x) / 2f, 4f, sz.x, sz.y), _hostTimeGc, CoopTheme.HudPill);
            }
            if (core.RegisterLine.Length > 0 || core.PromptLine.Length > 0)
            {
                if (core.PromptLine.Length > 0)
                {
                    if (core.PromptLine != _promptSeen)
                    {
                        _promptSeen = core.PromptLine;
                        _promptText = $"<size=17><color=#7ecbff>{core.PromptLine}</color></size>";
                        _promptGc = new GUIContent(_promptText);
                    }
                    Vector2 sz = CoopTheme.PillSize(CoopTheme.HudPillBig, _promptGc, 640f);
                    GUI.Label(new Rect((Screen.width - sz.x) / 2f, Screen.height * 0.58f, sz.x, sz.y),
                        _promptGc, CoopTheme.HudPillBig);
                }
                if (core.RegisterLine.Length > 0)
                {
                    if (core.RegisterLine != _registerSeen)
                    {
                        _registerSeen = core.RegisterLine;
                        _registerText = $"<size=18><color=#8ef58a>{core.RegisterLine}</color></size>";
                        _registerGc = new GUIContent(_registerText);
                    }
                    Vector2 sz = CoopTheme.PillSize(CoopTheme.HudPillBig, _registerGc, 740f);
                    GUI.Label(new Rect((Screen.width - sz.x) / 2f, Screen.height * 0.63f, sz.x, sz.y),
                        _registerGc, CoopTheme.HudPillBig);
                }
            }
            if (!Visible) return;

            CoopTheme.DrawWindowShadow(_win); // soft drop shadow behind the window (screen space)
            _win = GUILayout.Window(867530, _win, id => WindowFn(core, net), "", CoopTheme.Window);
        }

        private void WindowFn(CoopCore core, ICoopTransport net)
        {
            CoopTheme.EnsureBuilt();
            CoopTheme.DrawWindowChrome(new Rect(0f, 0f, _win.width, _win.height),
                "CARD SHOP CO-OP", "v" + CoopPlugin.Version);

            DrawStatusRow(core, net);

            if (core.ErrorLine.Length > 0)
            {
                GUILayout.BeginHorizontal();
                CoopTheme.Chip("PROBLEM", CoopTheme.ChipDanger);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Label(core.ErrorLine, CoopTheme.LabelDanger);
            }

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
            string lend = CoopCore.EnumLendState();
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
                    Util.ModParity.RestoreEnumBackup(out _enumRestoreMsg);
                }
                GUILayout.Space(4f);
            }

            switch (CoopCore.Role)
            {
                case CoopRole.None: DrawNone(core); break;
                case CoopRole.Host: DrawHost(core, net); break;
                case CoopRole.Client: DrawClient(core); break;
            }

            string focused = GUI.GetNameOfFocusedControl();
            TextFieldFocused = focused != null && focused.StartsWith("coop_");

            GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
        }

        /// <summary>Status chip (colored by state) + the raw StatusLine kept verbatim beside it
        /// (people paste that line into bug reports).</summary>
        private void DrawStatusRow(CoopCore core, ICoopTransport net)
        {
            ClassifyStatus(core, net, out GUIStyle chip, out string chipText);
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
            chip = null; text = null;
            string s = core.StatusLine ?? "";
            switch (CoopCore.Role)
            {
                case CoopRole.Host:
                    if ((net?.ConnectionCount ?? 0) > 0) { chip = CoopTheme.ChipSuccess; text = "HOSTING"; }
                    else { chip = CoopTheme.ChipInfo; text = "WAITING"; }
                    break;
                case CoopRole.Client:
                    if (Has(s, "download") || Has(s, "loading") || Has(s, "requesting")
                        || Has(s, "received") || Has(s, "Joining") || Has(s, "Connecting"))
                    { chip = CoopTheme.ChipInfo; text = "CONNECTING"; }
                    else { chip = CoopTheme.ChipSuccess; text = "CONNECTED"; }
                    break;
                default: // None
                    if (Has(s, "Joining") || Has(s, "Creating") || Has(s, "Connecting"))
                    { chip = CoopTheme.ChipInfo; text = "CONNECTING"; }
                    break;
            }
        }

        private static bool Has(string hay, string needle)
            => hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        private void DrawNone(CoopCore core)
        {
            if (_browserOpen) { DrawBrowser(core); return; }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Your name:", CoopTheme.Label, GUILayout.Width(72f));
            GUI.SetNextControlName("coop_name");
            string newName = GUILayout.TextField(_nameField, 16, CoopTheme.TextField);
            if (newName != _nameField)
            {
                _nameField = newName;
                if (newName.Trim().Length > 0) CoopPlugin.PlayerName.Value = newName.Trim();
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4f);

            // HOST section
            GUILayout.BeginVertical(CoopTheme.SectionBox);
            GUILayout.Label("HOST YOUR SHOP", CoopTheme.SectionHeader);
            GUILayout.Label("Load your shop first.", CoopTheme.LabelDim);
            _publicLobby = GUILayout.Toggle(_publicLobby, " public lobby (shows in the browser)", CoopTheme.Toggle);
            if (_publicLobby)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Lobby name:", CoopTheme.Label, GUILayout.Width(80f));
                GUI.SetNextControlName("coop_lobbyname");
                _lobbyNameField = GUILayout.TextField(_lobbyNameField, 28, CoopTheme.TextField);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Label("Password:", CoopTheme.Label, GUILayout.Width(80f));
                GUI.SetNextControlName("coop_hostpw");
                _hostPwField = GUILayout.TextField(_hostPwField, 20, CoopTheme.TextField);
                GUILayout.Label("<size=10>(blank = open)</size>", CoopTheme.LabelDim, GUILayout.Width(80f));
                GUILayout.EndHorizontal();
            }
            // core.Steam == null means this build has NO Steamworks assembly (Game Pass /
            // DRM-free), so Steam can never work and there is nothing the player could do
            // about it. Hide the control entirely rather than leaving a dead button that
            // answers with an error - and promote LAN to the full-width primary so the row
            // doesn't look like something failed to draw.
            GUILayout.BeginHorizontal();
            if (core.Steam != null)
            {
                if (GUILayout.Button("Host via Steam", CoopTheme.ButtonPrimary))
                    core.StartHostingSteam(_publicLobby, _lobbyNameField, _publicLobby ? _hostPwField : "");
                if (GUILayout.Button("Host via LAN", CoopTheme.ButtonSecondary, GUILayout.Width(110f)))
                    core.StartHosting();
            }
            else
            {
                if (GUILayout.Button("Host via LAN", CoopTheme.ButtonPrimary))
                    core.StartHosting();
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            CoopTheme.Divider();

            // JOIN section
            GUILayout.BeginVertical(CoopTheme.SectionBox);
            GUILayout.Label("JOIN A FRIEND", CoopTheme.SectionHeader);
            GUILayout.Label("Stay on the main menu.", CoopTheme.LabelDim);
            // Lobby browser + invite hint are Steam-only. On a Steamworks-less build point
            // the player at the thing that DOES work here (the IP field two rows down)
            // instead of showing an error about something they cannot install.
            if (core.Steam != null)
            {
                if (GUILayout.Button("Browse public lobbies", CoopTheme.ButtonPrimary))
                {
                    _browserOpen = true;
                    _page = 0;
                    _pwPromptLobby = 0;
                    core.Steam.RefreshList();
                }
                GUILayout.Label("<size=11>Steam friends: just accept the host's invite.</size>", CoopTheme.LabelDim);
            }
            else
            {
                GUILayout.Label("<size=11>This build has no Steam support - join by IP below.</size>", CoopTheme.LabelDim);
            }

            // wrong-password retry for invites into protected lobbies
            if (core.ErrorLine == "wrong password" && core.LastFailedLobby != 0UL)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Password:", CoopTheme.Label, GUILayout.Width(80f));
                GUI.SetNextControlName("coop_joinpw");
                _joinPwField = GUILayout.TextField(_joinPwField, 20, CoopTheme.TextField);
                if (GUILayout.Button("Retry", CoopTheme.ButtonPrimary, GUILayout.Width(64f)))
                    core.JoinSteam(core.LastFailedLobby, _joinPwField);
                GUILayout.EndHorizontal();
            }

            // IP + optional session password. The password field is here because a LAN host
            // now generates one by default (CoopPlugin.AutoLanPassword): a friend who PASTES
            // the invite code never sees it, but a friend who TYPES the address has to have
            // somewhere to put the eight characters the host read out. Blank is still correct
            // for a host that turned the password off, so nothing about the old flow breaks.
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("coop_ip");
            _ipField = GUILayout.TextField(_ipField, 24, CoopTheme.TextField);
            GUILayout.Label("<size=11>pw</size>", CoopTheme.LabelDim, GUILayout.Width(20f));
            GUI.SetNextControlName("coop_lanpw");
            _lanPwField = GUILayout.TextField(_lanPwField, 20, CoopTheme.TextField, GUILayout.Width(84f));
            if (GUILayout.Button("Join LAN", CoopTheme.ButtonPrimary, GUILayout.Width(84f)))
                // Trimmed and folded to upper case HERE, not in Join and not in the host's
                // password gate. A LAN host's password can only ever be the generated one
                // (there is no field anywhere that sets a custom one), and that is eight
                // characters of an uppercase-only alphabet - so folding what was TYPED can
                // never turn a wrong password into a right one, while it does spare a player
                // who typed what they heard in lower case an inexplicable "wrong password".
                // The gate itself stays exactly comparing, because a STEAM lobby password is
                // player-chosen and its case is the player's business.
                core.Join(_ipField, CoopPlugin.Port.Value, _lanPwField.Trim().ToUpperInvariant());
            GUILayout.EndHorizontal();

            // The invite code is just the IP row with the address, the port and the password
            // folded into one pasteable string - so it sits directly under it rather than in a
            // section of its own.
            //
            // DELIBERATELY VISIBLE ON STEAM BUILDS TOO, unlike the lobby browser above. A code
            // is a plain address + port + password: nothing in it is Steam-shaped, and the host
            // who produced it may well be on Game Pass, where Steam invites do not exist. A
            // Steam player joining a Game Pass host over IP is exactly the case this row is
            // for, so it is not gated on core.Steam.
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("coop_invite");
            _inviteField = GUILayout.TextField(_inviteField, InviteCode.MaxCodeLength, CoopTheme.TextField);
            if (GUILayout.Button("Join by code", CoopTheme.ButtonPrimary, GUILayout.Width(100f)))
            {
                if (InviteCode.TryParse(_inviteField, out string codeIp, out int codePort, out string codePw))
                {
                    _ipField = codeIp; // keep the plain IP row in step with what we just used
                    core.Join(codeIp, codePort, codePw);
                }
                else
                {
                    core.ErrorLine = "that invite code doesn't look right - check for typos";
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("<size=11>Paste your friend's invite code above - it carries their address, port and session password, so leave the pw box empty when you use one. Typing their IP instead? Then put the password they read you in the pw box.</size>", CoopTheme.LabelWrap);
            GUILayout.Label($"<size=11>LAN port {CoopPlugin.Port.Value} - all players need this mod + the same mods.</size>", CoopTheme.LabelDim);
            GUILayout.EndVertical();
        }

        private void DrawHost(CoopCore core, ICoopTransport net)
        {
            if (core.IsSteamSession)
            {
                GUILayout.Label("Hosting through Steam - no IPs needed.", CoopTheme.Label);
                if (GUILayout.Button("Invite friend  (Steam overlay)", CoopTheme.ButtonPrimary))
                    core.OpenSteamInvite();
                int scount = net?.ConnectionCount ?? 0;
                GUILayout.Label(scount == 0 ? "Waiting for your invite to be accepted..." : PlayersLine(core), CoopTheme.Label);
                if (GUILayout.Button("Wave  (" + CoopPlugin.EmoteKey.Value + ")", CoopTheme.ButtonSecondary)) core.SendEmote();
                if (GUILayout.Button("Stop hosting", CoopTheme.ButtonDanger)) core.Disconnect();
                return;
            }
            if (_lanIps == null)
            {
                var ips = LocalIPv4s();
                // home-router addresses first; virtual/VPN adapters are unreachable
                ips.Sort((a, b) => IpRank(a).CompareTo(IpRank(b)));
                _lanIps = ips.Count > 0 ? ips[0] : "(no LAN address found)";
                _lanIpsOther = ips.Count > 1 ? string.Join("  ", ips.GetRange(1, ips.Count - 1)) : "";
            }
            GUILayout.Label("Give this to the other PC:", CoopTheme.Label);
            if (!_revealIp)
            {
                if (GUILayout.Button("click to show IP  (hidden for streams)", CoopTheme.ButtonSecondary))
                    _revealIp = true;
            }
            else
            {
                GUILayout.Label($"<b><size=16>{_lanIps}</size></b>  (port {CoopPlugin.Port.Value})", CoopTheme.Label);
                if (_lanIpsOther.Length > 0)
                    GUILayout.Label($"<size=10>(other adapters, usually wrong: {_lanIpsOther})</size>", CoopTheme.LabelDim);
            }
            DrawInvite(core);
            int count = net?.ConnectionCount ?? 0;
            GUILayout.Label(count == 0 ? "Waiting for a player..." : PlayersLine(core), CoopTheme.Label);
            if (GUILayout.Button("Wave  (" + CoopPlugin.EmoteKey.Value + ")", CoopTheme.ButtonSecondary)) core.SendEmote();
            if (GUILayout.Button("Stop hosting", CoopTheme.ButtonDanger)) core.Disconnect();
        }

        /// <summary>The invite-code block in the LAN host panel: one copy button, one honest
        /// status line, and one dim line about the router. Only ever drawn for a LAN session -
        /// DrawHost returns before this for Steam, where the overlay invite already does the
        /// job and an address-bearing code would just be a second way to do the same thing.
        ///
        /// The code EMBEDS the address, so it obeys the same stream-safety rule as the IP two
        /// rows up: the text is printed only once the player has clicked to reveal. Copying is
        /// always allowed - the clipboard isn't on camera.</summary>
        private void DrawInvite(CoopCore core)
        {
            // LATCH FIRST, BEFORE THE EARLY RETURN. Everything below - including whether this
            // block draws at all - reads the latched copies, so the snapshot has to be taken
            // ahead of the first decision that depends on it. Latch after the return and a
            // panel that starts in Off never updates its own copy and stays blank forever.
            if (Event.current.type == EventType.Layout)
            {
                _invState = core.InviteStatus;
                _invCode = core.InviteCodeText;
                _invReason = core.InviteReason;
                _invPortState = core.PortForwardState;
                _invPassword = core.HostPassword;
            }

            if (_invState == InviteState.Off) return;

            string code = _invCode;
            // A new code (new session, or the worker just finished) is a code nobody has
            // copied yet - without this the button would still read "Copied!" for the next
            // person's code.
            if (!ReferenceEquals(code, _inviteSeen)) { _inviteSeen = code; _inviteCopied = false; }

            GUILayout.BeginHorizontal();
            GUI.enabled = code != null;
            if (GUILayout.Button(_inviteCopied ? "Copied!" : "Copy invite code", CoopTheme.ButtonSecondary, GUILayout.Width(150f)))
            {
                GUIUtility.systemCopyBuffer = code;
                _inviteCopied = true;
            }
            GUI.enabled = true;
            GUILayout.Label(InviteStatusText(_invState), CoopTheme.LabelDim);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (_revealIp && code != null)
                GUILayout.Label($"<size=11>{code}</size>", CoopTheme.LabelWrap);

            // THE SESSION PASSWORD, printed even while the IP is hidden - and that is not an
            // oversight in the stream-safety rule but the shape of it. The rule protects the
            // ADDRESS, because an address is what lets a stranger reach this PC at all; the
            // password on its own opens nothing, and a friend typing the IP by hand needs to be
            // read it. It sits here, under the code, because that is where it already lives:
            // anyone who used the code has supplied it without knowing it exists.
            if (!string.IsNullOrEmpty(_invPassword))
                GUILayout.Label($"<size=11>session password: <b>{_invPassword}</b>  (already inside the invite code - only needed if they type your IP by hand)</size>",
                    CoopTheme.LabelWrap);

            // Say plainly what a LAN-only code is and isn't. It is NOT a failure - it is the
            // code that has always worked for the PC in the next room - so it is worded as a
            // limit rather than an error, and it names the reason so the player can fix it.
            if (_invState == InviteState.LanOnly)
            {
                string why = _invReason;
                GUILayout.Label("<size=11>LAN-only code - " + (why != null ? why : "couldn't reach the internet resolver")
                    + ". It carries your LAN address, so it still works for someone in this house.</size>",
                    CoopTheme.LabelWrap);
            }

            // UPnP success means the router ACCEPTED the request, which is not the same as the
            // port being reachable (a second router, or the ISP's own NAT, still gets a vote).
            // Never promise more than that.
            if (_invPortState == 1)
                GUILayout.Label("<size=10>port forward requested on your router automatically</size>", CoopTheme.LabelDim);
            else if (_invPortState == 2)
                // Say what to DO about it, not just that it happened: forwarding the port is a
                // router-admin job this mod cannot do, and the other player hosting instead is
                // the answer for anyone who isn't going to go find their router password.
                // Kept short AND wrapped: the long form measured wider than this 400px panel,
                // and LabelDim doesn't wrap, so the tail was clipped off-screen - the half that
                // carried the advice. LabelDimWrap is LabelDim + wordWrap.
                GUILayout.Label($"<size=10>router declined - forward port {CoopPlugin.Port.Value} manually, or let the other player host</size>", CoopTheme.LabelDimWrap);
        }

        private static string InviteStatusText(InviteState state)
        {
            switch (state)
            {
                case InviteState.Resolving: return "<size=11>resolving public address...</size>";
                case InviteState.Ready: return "<size=11>code ready (internet)</size>";
                case InviteState.LanOnly: return "<size=11>LAN-only code</size>";
                default: return "";
            }
        }

        private void DrawClient(CoopCore core)
        {
            GUILayout.Label(PlayersLine(core), CoopTheme.Label);
            GUILayout.Label($"<size=11>You're playing in the host's shop. At the register, click the customer's items to scan them, then click to take payment and give change ({CoopPlugin.ServeKey.Value} also works). Your own saves are protected.</size>",
                CoopTheme.LabelWrap);
            if (GUILayout.Button("Wave  (" + CoopPlugin.EmoteKey.Value + ")", CoopTheme.ButtonSecondary)) core.SendEmote();
            if (GUILayout.Button("Leave session", CoopTheme.ButtonDanger)) core.Disconnect();
        }

        private void DrawBrowser(CoopCore core)
        {
            // Belt and braces: the only door into the browser is already hidden when Steam
            // is absent, but every line below dereferences core.Steam, so refuse to draw at
            // all rather than trust that. Closes the panel silently - there is no error to
            // report, this build simply has no lobbies.
            if (core.Steam == null) { _browserOpen = false; return; }

            GUILayout.BeginHorizontal();
            GUILayout.Label("PUBLIC LOBBIES", CoopTheme.SectionHeader);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(core.Steam.ListRefreshing ? "..." : "Refresh", CoopTheme.ButtonPrimary, GUILayout.Width(72f)))
                core.Steam.RefreshList();
            if (GUILayout.Button("Back", CoopTheme.ButtonSecondary, GUILayout.Width(54f)))
            {
                _browserOpen = false;
                _pwPromptLobby = 0;
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Search:", CoopTheme.Label, GUILayout.Width(52f));
            GUI.SetNextControlName("coop_search");
            string s = GUILayout.TextField(_searchField, 24, CoopTheme.TextField);
            if (s != _searchField) { _searchField = s; _page = 0; }
            GUILayout.EndHorizontal();

            // Net.LobbyRow, NOT SteamLobby.LobbyRow: the latter carries a CSteamID field, so
            // even as a local generic argument here it would take this whole method down on
            // a build with no Steamworks assembly.
            var filtered = new List<LobbyRow>();
            foreach (var row in core.Steam.Lobbies)
                if (_searchField.Length == 0
                    || (row.Name ?? "").IndexOf(_searchField, StringComparison.OrdinalIgnoreCase) >= 0)
                    filtered.Add(row);

            int pages = Mathf.Max(1, (filtered.Count + PageSize - 1) / PageSize);
            _page = Mathf.Clamp(_page, 0, pages - 1);

            if (filtered.Count == 0)
            {
                GUILayout.Label(core.Steam.ListRefreshing ? "Searching..." : "No lobbies found - hit Refresh, or host one!", CoopTheme.LabelDim);
            }
            for (int i = _page * PageSize; i < filtered.Count && i < (_page + 1) * PageSize; i++)
            {
                var row = filtered[i];
                bool verOk = row.Ver == CoopPlugin.Version;
                GUILayout.BeginHorizontal(((i & 1) == 0) ? CoopTheme.RowEven : CoopTheme.RowOdd);
                GUILayout.Label((row.HasPw ? "[pw] " : "") + row.Name, CoopTheme.LabelBold, GUILayout.ExpandWidth(false));
                GUILayout.FlexibleSpace();
                string right = $"{row.Players}/{row.Max}" + (verOk ? "" : $"  <size=10>v{row.Ver}</size>");
                GUILayout.Label(right, CoopTheme.LabelDim, GUILayout.ExpandWidth(false));
                GUILayout.Space(6f);
                GUI.enabled = verOk;
                if (GUILayout.Button("Join", CoopTheme.ButtonPrimary, GUILayout.Width(56f)))
                {
                    if (row.HasPw) { _pwPromptLobby = row.Id; _joinPwField = ""; }
                    else { core.JoinSteam(row.Id, ""); _browserOpen = false; }
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();

                if (_pwPromptLobby == row.Id)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Password:", CoopTheme.Label, GUILayout.Width(70f));
                    GUI.SetNextControlName("coop_joinpw");
                    _joinPwField = GUILayout.TextField(_joinPwField, 20, CoopTheme.TextField);
                    if (GUILayout.Button("Go", CoopTheme.ButtonPrimary, GUILayout.Width(44f)))
                    {
                        core.JoinSteam(row.Id, _joinPwField);
                        _browserOpen = false;
                        _pwPromptLobby = 0;
                    }
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.BeginHorizontal();
            GUI.enabled = _page > 0;
            if (GUILayout.Button("< Prev", CoopTheme.ButtonSecondary, GUILayout.Width(64f))) _page--;
            GUI.enabled = _page < pages - 1;
            if (GUILayout.Button("Next >", CoopTheme.ButtonSecondary, GUILayout.Width(64f))) _page++;
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            GUILayout.Label($"<size=11>page {_page + 1}/{pages} - {filtered.Count} lobbies</size>", CoopTheme.LabelDim);
            GUILayout.EndHorizontal();
        }

        private static string PlayersLine(CoopCore core)
        {
            if (core.PeerNames.Count == 0) return "Linked.";
            var names = new List<string>(core.PeerNames.Values);
            return "Playing with: " + string.Join(", ", names);
        }

        private static List<string> LocalIPv4s()
        {
            var result = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string s = addr.Address.ToString();
                        if (s.StartsWith("169.254")) continue; // link-local noise
                        result.Add(s);
                    }
                }
            }
            catch { }
            if (result.Count == 0) result.Add("(no LAN address found)");
            return result;
        }
    }
}
