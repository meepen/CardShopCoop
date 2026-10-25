using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using CardShopCoop.Net;
using CardShopCoop.Net.Datagrams;
using CardShopCoop.Net.Networking;
using CardShopCoop.Api;
using UnityEngine;

namespace CardShopCoop.Steam
{
    /// <summary>
    /// The Steam transport as a networking backend: a Steam lobby plus its Steam Networking
    /// Sockets datagram bearer. It owns the Steamworks bridge, so no Steam type leaks any
    /// further; the co-op window drives it through <see cref="INetworkingBackend"/> alone.
    ///
    /// Hosting and joining are ASYNC on Steam (lobby creation and entry complete through
    /// callbacks), so <see cref="Connect"/> returns its handle immediately and the outcome arrives
    /// through the bridge callbacks, addressed to that handle.
    /// </summary>
    public sealed class SteamNetworkingBackend
        : INetworkingBackend, INetworkingBackendDiagnostics, INetworkingBackendNotifications
    {
        private const string Section = "Steam";

        private readonly ISteamBridge _bridge;
        private readonly ConfigFile _config;
        private readonly object _gate = new();
        private readonly Dictionary<string, ulong> _lobbyIds = new(StringComparer.Ordinal);

        private ConnectionId _attempt;
        private IBackendConnectCallbacks _callbacks;
        private IDatagramTransport _datagrams;
        private bool _live;
        private string _lobbyId = "";
        /// <summary>The lobby id WE are currently hosting, captured when the lobby goes live.
        /// Distinct from <see cref="_lobbyId"/>, which is only ever the join target the player
        /// typed or pasted. Zero while not hosting.</summary>
        private ulong _hostedLobby;
        private volatile bool _busy;
        private volatile string _step;
        private bool _isHost;

        public SteamNetworkingBackend(ISteamBridge bridge)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _config = new ConfigFile(
                Path.Combine(Paths.ConfigPath, "dev.meepen.cardshopcoop.steam.cfg"), true);

            _bridge.OnLobbyLive = lobby =>
            {
                // Remember our own hosted lobby id so the host can copy it: the Steam overlay
                // invite can fail to reach a friend, and the raw id is the fallback they paste
                // into the join box (or pass to +connect_lobby).
                _hostedLobby = lobby;
                _live = true;
                _busy = false;
                // The lobby id and busy state are what the host panel draws its copy/invite
                // controls from; they changed before Connected() reports the session.
                NotifyStateChanged();
                Connected();
            };
            _bridge.OnConnectedToHost = transport =>
            {
                if (ReferenceEquals(transport, _datagrams))
                {
                    _live = true;
                    _busy = false;
                    NotifyStateChanged();
                    Connected();
                }
            };
            _bridge.OnJoinFailed = (_, _, transport, error) =>
            {
                if (ReferenceEquals(transport, _datagrams))
                {
                    Fail(error, retryable: true);
                }
            };
            _bridge.OnSessionFailed = (transport, error) =>
            {
                if (ReferenceEquals(transport, _datagrams))
                {
                    Fail(error, retryable: false);
                }
            };
            // OnError is the bridge's catch-all and fires for UNRELATED, non-fatal conditions too -
            // notably "Steam lobby list failed" from a lobby-browser refresh. It must not be able to
            // drop a live session, only to fail an attempt that is still connecting.
            _bridge.OnError = error =>
            {
                if (_live)
                {
                    CoopPlugin.Log?.LogWarning("Steam (non-fatal): " + error);
                    return;
                }

                Fail(error, retryable: false);
            };

            // The player accepted a Steam invite from the overlay: hand the lobby id up as a join
            // request. The runtime decides whether a session can start and does the actual join.
            _bridge.OnInviteAccepted = lobby => JoinRequested?.Invoke(this,
                Values(FieldId.Lobby, lobby.ToString(CultureInfo.InvariantCulture)));
        }

        private static FieldValues Values(FieldId field, string value)
        {
            var values = new FieldValues();
            values[field] = value;
            return values;
        }

        public string Id => "steam";

        public string DisplayName => "Steam";

        public ulong LocalUserId => _bridge.LocalSteamId;

        public string LocalUserName => _bridge.LocalPersonaName ?? "";

        public event Action<INetworkingBackend, FieldValues> JoinRequested;

        /// <summary>Raised when the lobby/connection state the panel draws changes outside a
        /// session event: lobby creation started, the lobby went live, or the lobby was left.
        /// <see cref="ConnectionInfo.Busy"/> and the host's "Copy lobby id" depend on it.</summary>
        public event Action<INetworkingBackend> StateChanged;

        private void NotifyStateChanged() => StateChanged?.Invoke(this);

        public Task<BackendAvailability> GetAvailabilityAsync()
        {
            try
            {
                return Task.FromResult(_bridge.SteamAvailable()
                    ? BackendAvailability.Yes
                    : BackendAvailability.No("Steam isn't running"));
            }
            catch (Exception error)
            {
                return Task.FromResult(BackendAvailability.No(error.Message));
            }
        }

        // ------------------------------------------------------------------ config

        public void Configure(FieldValues values)
        {
            if (values == null)
            {
                return;
            }

            if (values.TryGetValue(FieldId.LobbyName, out var name))
            {
                _config.Bind(Section, "LobbyName", "", "Name of the lobby you host.").Value = name ?? "";
            }
            if (values.TryGetValue(FieldId.MaxPlayers, out var max)
                && int.TryParse(max, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                _config.Bind(Section, "MaxPlayers", 4, "Maximum players in your lobby (2-250).").Value = n;
            }
            if (values.TryGetValue(FieldId.Public, out var pub))
            {
                _config.Bind(Section, "Public", false, "Advertise your lobby in the public browser.").Value
                    = pub == "1" || string.Equals(pub, "true", StringComparison.OrdinalIgnoreCase);
            }
            if (values.TryGetValue(FieldId.Lobby, out var lobby))
            {
                // The join target the player typed or pasted. Not a config key on purpose: it is one
                // session's id, meaningless next launch (and a stale id would silently be wrong).
                _lobbyId = (lobby ?? "").Trim();
            }

            _config.Save();
        }

        private string LobbyName => _config.Bind(Section, "LobbyName", "", "Name of the lobby you host.").Value;
        private bool Public
            => _config.Bind(Section, "Public", false, "Advertise your lobby in the public browser.").Value;
        private int MaxPlayers
            => Math.Max(SteamLobbyLimits.MinPlayers, Math.Min(SteamLobbyLimits.MaxPlayers,
                _config.Bind(Section, "MaxPlayers", 4, "Maximum players in your lobby (2-250).").Value));

        public IReadOnlyList<BackendField> GetFields(ConnectMode mode)
        {
            if (mode == ConnectMode.Host)
            {
                var host = new List<BackendField>();

                // The lobby NAME only ever labels a row in Steam's lobby list, and that list returns
                // public lobbies only - a friends-only lobby is reached through the overlay invite,
                // where no name is shown. So the name is only asked for while it can actually be
                // seen, and the toggle below decides that. Conditional fields are safe here for the
                // same reason the IP backend's are: the window freezes this list once per frame, so
                // Layout and Repaint cannot disagree - the row appears on the next frame.
                if (Public)
                {
                    host.Add(new BackendField(FieldId.LobbyName, "Lobby name", FieldKind.Text, LobbyName));
                }

                host.Add(new BackendField(FieldId.MaxPlayers, "Players", FieldKind.Number,
                    MaxPlayers.ToString(CultureInfo.InvariantCulture)));
                // "List in public browser" is exactly what decides the lobby type: Steam's lobby
                // list only ever returns k_ELobbyTypePublic lobbies, so an unlisted host has to be
                // k_ELobbyTypeFriendsOnly - still joinable by Steam friends, invites and
                // +connect_lobby, just never published to any browser (ours included).
                host.Add(new BackendField(FieldId.Public, "List in public browser", FieldKind.Toggle,
                    Public ? "1" : "0",
                    tooltip: "Publish this lobby to Steam's public lobby list so strangers can find "
                        + "it. Off keeps it friends-only: still joinable by invite, just never listed."));
                return host;
            }

            // The lobby id, typed or pasted. Steam exposes no lobby-list window to point at (see the
            // Browse action), so this id is the one thing a joiner must be given.
            return new[]
            {
                new BackendField(FieldId.Lobby, "Steam lobby id", FieldKind.Text,
                    _lobbyId, required: true,
                    tooltip: "The numeric Steam lobby id of the host you are joining.")
                {
                    Sensitive = true,
                },
            };
        }

        /// <summary>The support-report view of this backend. The hosted lobby id is listed but
        /// marked sensitive: it is an invite credential, so a report must censor it completely.</summary>
        public IReadOnlyList<BackendDiagnosticField> GetDiagnosticFields(ConnectMode mode)
        {
            if (mode == ConnectMode.Host)
            {
                var fields = new List<BackendDiagnosticField>
                {
                    new BackendDiagnosticField("Backend", Id),
                    new BackendDiagnosticField("Lobby name", LobbyName),
                    new BackendDiagnosticField("Players",
                        MaxPlayers.ToString(CultureInfo.InvariantCulture)),
                    new BackendDiagnosticField("List in public browser", Public ? "yes" : "no"),
                    new BackendDiagnosticField("Lobby id", HostedLobbyId, sensitive: true),
                };
                return fields;
            }

            return new[]
            {
                new BackendDiagnosticField("Backend", Id),
                new BackendDiagnosticField("Lobby id", _lobbyId, sensitive: true),
            };
        }

        // ------------------------------------------------------------------ connect / disconnect

        public void Connect(ConnectionId attempt, ConnectMode mode, FieldValues values,
            IBackendConnectCallbacks callbacks)
        {
            if (attempt.Value == 0)
            {
                throw new ArgumentException("A live connection attempt is required.", nameof(attempt));
            }
            if (callbacks == null)
            {
                throw new ArgumentNullException(nameof(callbacks));
            }

            lock (_gate)
            {
                _attempt = attempt;
                _callbacks = callbacks;
                _isHost = mode == ConnectMode.Host;
                _live = false;
                _hostedLobby = 0;
                _datagrams = null;
            }

            _step = _isHost ? "Creating Steam lobby\u2026" : "Entering Steam lobby\u2026";
            _busy = true;
            // The session is not live yet, but the panel now reads "creating/entering lobby" and
            // the start button must be blocked until this goes false again.
            NotifyStateChanged();

            try
            {
                EnsureAvailable();

                // The bearer is created before Host/Join so the runtime can start the reliable
                // session immediately; the bridge binds the Steam sockets bearer once the lobby
                // supplies the authorization context. The runtime owns the bearer from
                // BearerReady on.
                var bearer = _bridge.CreateDatagramTransport();
                lock (_gate)
                {
                    _datagrams = bearer;
                }

                callbacks.BearerReady(attempt, bearer);

                if (_isHost)
                {
                    // See GetFields: the "List in browser" toggle is the lobby type.
                    _bridge.Host(Public, LobbyName, false, MaxPlayers);
                }
                else
                {
                    _bridge.Join(ResolveLobby(values));
                }
            }
            catch (Exception error)
            {
                Fail(error.Message, retryable: false);
            }
        }

        private void EnsureAvailable()
        {
            if (!_bridge.SteamAvailable())
            {
                throw new InvalidOperationException("Steam isn't running.");
            }
        }

        private static ulong ResolveLobby(FieldValues values)
        {
            // The Choice field's VALUE is the raw lobby id (see GetFields), so this is a plain parse.
            var raw = values != null && values.TryGetValue(FieldId.Lobby, out var v) ? (v ?? "") : "";
            if (ulong.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var parsed))
            {
                return parsed;
            }

            throw new InvalidOperationException("Pick a lobby from the list first.");
        }

        public void Disconnect(ConnectionId attempt)
        {
            lock (_gate)
            {
                if (!_attempt.IsValid || _attempt != attempt)
                {
                    return;
                }

                _attempt = default;
                _callbacks = null;
                _datagrams = null;
                _live = false;
            }

            _busy = false;
            _hostedLobby = 0;
            try
            {
                _bridge.Leave();
            }
            catch (Exception e) { CoopLog.Swallow(e); }

            // Leaving is backend-owned state: a multi-backend host can close Steam while still
            // hosting on IP, and no session event fires for that.
            NotifyStateChanged();

            // Do NOT stop or dispose the bearer here. The runtime owns it and tears it down on the
            // pump thread; this method only releases the backend-owned Steam lobby.
        }

        /// <summary>Reports a fully live attempt. The runtime ignores a callback whose attempt is no
        /// longer current.</summary>
        private void Connected()
        {
            IBackendConnectCallbacks callbacks;
            ConnectionId attempt;
            lock (_gate)
            {
                if (!_attempt.IsValid || _callbacks == null)
                {
                    return;
                }

                attempt = _attempt;
                callbacks = _callbacks;
            }

            callbacks.Connected(attempt);
        }

        /// <summary>Reports a failed attempt to the runtime. The runtime ignores a callback whose
        /// attempt is no longer current, and calls <see cref="Disconnect"/> for the current one.</summary>
        private void Fail(string reason, bool retryable)
        {
            IBackendConnectCallbacks callbacks;
            ConnectionId attempt;
            lock (_gate)
            {
                if (!_attempt.IsValid || _callbacks == null)
                {
                    CoopPlugin.Log?.LogWarning("Steam: " + reason);
                    return;
                }

                attempt = _attempt;
                callbacks = _callbacks;
            }

            callbacks.Failed(attempt, reason, retryable);
        }

        // ------------------------------------------------------------------ presentation

        /// <summary>The lobby id we are currently hosting, or "" when not hosting. Read by the
        /// host's Copy lobby id button; never the join target (<see cref="_lobbyId"/>).</summary>
        private string HostedLobbyId => _hostedLobby == 0
            ? ""
            : _hostedLobby.ToString(CultureInfo.InvariantCulture);

        public IReadOnlyList<BackendAction> Actions => BuildActions();

        private IReadOnlyList<BackendAction> BuildActions()
        {
            return new[]
            {
                // Invite belongs to a lobby that exists (Hosting) AND to a live session (Phase.Live):
                // before the lobby is up there is nobody to invite. Browse belongs to the side that
                // has a lobby list (Joining). The window filters on both Mode and Phase, so the join
                // area no longer offers to invite anyone, the options view no longer offers a button
                // that needs a live session, and the hosting panel no longer offers to refresh a list
                // it does not show.
                new BackendAction("invite", "Invite friend  (Steam overlay)",
                    () => _bridge.OpenInviteDialog(), mode: ConnectMode.Host, phase: ActionPhase.Live),
                // The overlay invite is the normal path, but it can fail to reach a friend (their
                // overlay closed, they accepted at a bad moment, or the invite never arrived). The
                // lobby id is the fallback: the friend pastes it into the join box, or launches with
                // +connect_lobby. Sits directly under Invite friend, host-only and live-only, the
                // same as the id it hands out - before the lobby exists there is nothing to copy.
                new BackendAction("copyLobbyId", "Copy lobby id",
                    () =>
                    {
                        var id = HostedLobbyId;
                        if (!string.IsNullOrEmpty(id))
                        {
                            GUIUtility.systemCopyBuffer = id;
                        }
                    },
                    enabled: !string.IsNullOrEmpty(HostedLobbyId),
                    tooltip: "Copy this lobby's numeric id. A friend can paste it into the Steam "
                        + "lobby id box (or launch with +connect_lobby) if the overlay invite "
                        + "doesn't reach them.",
                    icon: "copy",
                    mode: ConnectMode.Host, phase: ActionPhase.Live),
                // Connect sits ON the lobby-id row, so the id and the button that uses it are read
                // together. It goes through the core's normal start path rather than Join directly,
                // so a Connect press is the same thing as the container's Join button.
                new BackendAction("connect", "Connect", SubmitJoin,
                    tooltip: "Join the lobby id on this row.",
                    field: FieldId.Lobby, mode: ConnectMode.Join),
                // Steam's OWN browser, opened in the overlay: its undocumented "servers" dialog opens
                // the Game Servers window, which lists game servers rather than Steam lobbies. It is
                // the only browser Steam exposes, so it sits beside the id box instead of replacing
                // it - the id box is what actually joins.
                new BackendAction("servers", "Browse Steam lobbies",
                    () => _bridge.OpenGameServers(),
                    tooltip: "Open Steam's own browser in the overlay (its Game Servers window).",
                    mode: ConnectMode.Join),
            };
        }

        /// <summary>Joins the pasted lobby id through <see cref="CoopCore.ConnectBackend"/> - the
        /// same entry point the window's header button and the command-line join use - so the
        /// pending/abort bookkeeping and the error reporting are identical to any other start.</summary>
        private void SubmitJoin()
        {
            var core = CoopCore.Instance;
            if (core == null)
            {
                return;
            }

            var values = Values(FieldId.Lobby, _lobbyId);
            Configure(values);
            core.ConnectBackend(this, ConnectMode.Join, values);
        }

        public ConnectionInfo ConnectionInfo => Describe();

        private ConnectionInfo Describe()
        {
            bool active;
            lock (_gate)
            {
                active = _attempt.IsValid;
            }

            var busy = _busy;
            var summary = busy ? _step
                : !active ? "Not connected"
                : _isHost ? "Hosting through Steam - use Invite friend" : "In session";
            return new ConnectionInfo(active, _isHost, summary, busy: busy);
        }
    }
}
