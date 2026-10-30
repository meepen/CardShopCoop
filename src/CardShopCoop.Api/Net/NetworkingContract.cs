using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using CardShopCoop.Net.Connection;
using CardShopCoop.Net.Datagrams;

namespace CardShopCoop.Net.Networking
{
    /// <summary>
    /// The values a backend can ask the player for. One shared id space, so the co-op window can
    /// collect a typed dictionary without knowing which backend it is driving.
    /// </summary>
    public enum FieldId
    {
        IpAddress,  // join target: the host's address, "ip" or "ip:port"
        Port,       // host: the local UDP port to listen on
        Password,   // the session password (the host sets it, a joiner presents it)
        Lobby,      // join target: a lobby selected from the browser
        LobbyName,  // host: the advertised name
        MaxPlayers, // host: session size
        Public,     // host: advertise publicly
        AutoForward, // host: let the mod ask the router to open the port (UPnP/NAT-PMP/PCP)
    }

    /// <summary>How the UI renders a field.</summary>
    public enum FieldKind
    {
        Text,
        Port,
        Password,
        Toggle,
        Number,
    }

    public enum ConnectMode
    {
        Host,
        Join,
    }

    /// <summary>Which phase of a session a backend action belongs to. This is a DIFFERENT axis from
    /// <see cref="BackendAction.Mode"/>: Mode is the host/join SIDE, Phase is before-vs-after the
    /// session starts. The window draws the actions matching the view it is drawing, so a button that
    /// needs a live session is never offered before one exists - and it cannot appear or vanish
    /// partway through a pass, because the phase is a fixed declaration rather than live state.</summary>
    public enum ActionPhase
    {
        /// <summary>Offered only in the options view, before the session starts.</summary>
        BeforeStart,
        /// <summary>Offered only while the backend is actually carrying the session.</summary>
        Live,
        /// <summary>Offered in both views.</summary>
        Any,
    }

    /// <summary>
    /// Opaque handle for one Connect attempt and the session it becomes, allocated by the
    /// runtime. The backend receives it in Connect and passes it back to every callback; the
    /// runtime ignores anything addressed to an attempt it has moved past. This is NOT a
    /// per-peer session id - this identifies the local side's connection attempt.
    /// </summary>
    public readonly struct ConnectionId : IEquatable<ConnectionId>
    {
        internal ConnectionId(long value)
        {
            Value = value;
        }

        public long Value
        {
            get;
        }

        public bool IsValid => Value != 0;

        public bool Equals(ConnectionId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is ConnectionId other && Equals(other);

        public override int GetHashCode() => Value.GetHashCode();

        public static bool operator ==(ConnectionId left, ConnectionId right) => left.Equals(right);

        public static bool operator !=(ConnectionId left, ConnectionId right) => !left.Equals(right);

        public override string ToString() => Value.ToString();
    }

    /// <summary>
    /// The values a backend asked for, keyed by <see cref="FieldId"/>. A named type rather than a
    /// raw dictionary: the shape is discoverable, and the stringly-typed values are read through
    /// typed helpers instead of every backend re-parsing "1"/"true" by hand.
    /// </summary>
    public sealed class FieldValues : IEnumerable<KeyValuePair<FieldId, string>>
    {
        private readonly Dictionary<FieldId, string> _values = new Dictionary<FieldId, string>();

        public string this[FieldId id]
        {
            get => _values.TryGetValue(id, out var value) ? (value ?? "") : "";
            set => _values[id] = value ?? "";
        }

        public int Count => _values.Count;

        public bool TryGetValue(FieldId id, out string value) => _values.TryGetValue(id, out value);

        public bool Contains(FieldId id) => _values.ContainsKey(id);

        public bool GetBool(FieldId id)
            => _values.TryGetValue(id, out var value)
                && (value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));

        public void SetBool(FieldId id, bool value) => this[id] = value ? "1" : "0";

        public int GetInt(FieldId id, int fallback = 0)
            => _values.TryGetValue(id, out var value)
                && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var parsed)
                    ? parsed
                    : fallback;

        public IEnumerator<KeyValuePair<FieldId, string>> GetEnumerator()
            => _values.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => _values.GetEnumerator();
    }

    /// <summary>One requested value plus everything the UI needs to render and prefill it.</summary>
    public sealed class BackendField
    {
        public BackendField(FieldId id, string label, FieldKind kind,
            string value = "", bool required = false, string tooltip = null)
        {
            Id = id;
            Label = label ?? id.ToString();
            Kind = kind;
            Value = value ?? "";
            Required = required;
            Tooltip = tooltip;
        }

        public FieldId Id
        {
            get;
        }
        public string Label
        {
            get;
        }
        public FieldKind Kind
        {
            get;
        }

        /// <summary>The current value (the last Configure, or the backend's default). The UI shows
        /// it and hands edits back through the same dictionary the next Configure receives.</summary>
        public string Value
        {
            get;
        }

        public bool Required
        {
            get;
        }

        /// <summary>Hover help, shown by the UI only after the pointer rests on the field for a few
        /// seconds; null when there is nothing worth saying.</summary>
        public string Tooltip
        {
            get;
        }

        /// <summary>True when the value is private enough that a support report must never print it
        /// (a password, a home IP, a lobby id). The UI still edits it normally; diagnostics replace
        /// the value entirely rather than partially masking it. A backend marks its own field by
        /// setting this when it builds the field, leaving the constructor signature unchanged for
        /// transports compiled against an earlier API build.</summary>
        public bool Sensitive
        {
            get; set;
        }
    }

    /// <summary>One line of backend-owned hosting information for a support report. Unlike a
    /// <see cref="BackendField"/>, this is not editable and can carry values the window deliberately
    /// never draws - a public address or a hosted lobby id - so each entry states whether it is
    /// sensitive and must be censored by the report.</summary>
    public sealed class BackendDiagnosticField
    {
        public BackendDiagnosticField(string key, string value, bool sensitive = false)
        {
            Key = key ?? "";
            Value = value ?? "";
            Sensitive = sensitive;
        }

        public string Key
        {
            get;
        }
        public string Value
        {
            get;
        }
        public bool Sensitive
        {
            get;
        }
    }

    /// <summary>
    /// Optional companion to <see cref="INetworkingBackend"/> for diagnostic reporting. The
    /// co-op window falls back to <see cref="INetworkingBackend.GetFields"/> when a backend does
    /// not implement this, so adding it is never required of a third-party transport. Values are
    /// read on the Unity thread only, from the same snapshot the window draws.
    /// </summary>
    public interface INetworkingBackendDiagnostics
    {
        /// <summary>Backend-owned hosting/join details for a support report, in display order.
        /// Every entry is marked sensitive or not; sensitive values are never written to the
        /// report.</summary>
        IReadOnlyList<BackendDiagnosticField> GetDiagnosticFields(ConnectMode mode);
    }

    /// <summary>
    /// Optional companion to <see cref="INetworkingBackend"/> for state a backend resolves on its
    /// own AFTER the session event that started it: the host's public address lookup finishing, a
    /// lobby becoming live, an availability probe completing.
    ///
    /// The co-op window is event driven and re-reads <see cref="INetworkingBackend.ConnectionInfo"/>
    /// and <see cref="INetworkingBackend.Actions"/> when it redraws, so a button whose enabled state
    /// comes from either would otherwise stay grey until some unrelated session event happened. The
    /// IP backend's "Copy public address" is the canonical case: the address arrives seconds after
    /// hosting starts, on a worker thread.
    ///
    /// A backend that has no such state needs no implementation; the runtime treats this interface
    /// as optional, exactly like <see cref="INetworkingBackendDiagnostics"/>.
    /// </summary>
    public interface INetworkingBackendNotifications
    {
        /// <summary>Raised when <see cref="INetworkingBackend.ConnectionInfo"/> or
        /// <see cref="INetworkingBackend.Actions"/> would now read differently. May be raised from
        /// any thread; the runtime marshals it to the Unity thread before notifying the window.
        /// Implementations must not raise it for ordinary configuration edits - the window already
        /// holds those values - and must be safe to raise after the session has ended.</summary>
        event Action<INetworkingBackend> StateChanged;
    }

    /// <summary>A backend-specific button (invite dialog, refresh lobbies, copy code...).</summary>
    public sealed class BackendAction
    {
        public BackendAction(string id, string label, Action invoke, bool enabled = true,
            string tooltip = null, FieldId? field = null, string icon = null,
            ConnectMode? mode = null, ActionPhase phase = ActionPhase.Any)
        {
            Id = id;
            Label = label ?? id;
            Invoke = invoke;
            Enabled = enabled;
            Tooltip = tooltip;
            Field = field;
            Icon = icon;
            Mode = mode;
            Phase = phase;
        }

        public string Id
        {
            get;
        }
        public string Label
        {
            get;
        }
        public Action Invoke
        {
            get;
        }
        public bool Enabled
        {
            get;
        }

        /// <summary>The one mode this action is offered in, or null for both. Host-only and join-only
        /// buttons genuinely exist: inviting friends is meaningless on the join side, and refreshing a
        /// lobby list is meaningless on the host side. The window draws neither section's buttons in
        /// the other mode.</summary>
        public ConnectMode? Mode
        {
            get;
        }

        /// <summary>Which phase the action is offered in (see <see cref="ActionPhase"/>). The options
        /// view draws every action except <see cref="ActionPhase.Live"/>; the live view draws every
        /// action except <see cref="ActionPhase.BeforeStart"/>. "Invite friend" is the original
        /// <see cref="ActionPhase.Live"/> case: before hosting starts there is no lobby to invite
        /// anyone to.</summary>
        public ActionPhase Phase
        {
            get;
        }

        /// <summary>Shown only once the pointer has RESTED on the control for a few seconds (see the
        /// UI) - an instant tooltip covers the panel while the player is just moving across it.</summary>
        public string Tooltip
        {
            get;
        }

        /// <summary>The field this action belongs to, so the UI draws it ON that field's row as a
        /// small trailing button instead of in the list below. Null = a plain panel button.</summary>
        public FieldId? Field
        {
            get;
        }

        /// <summary>Optional artwork, named rather than supplied: the UI owns how an action is drawn
        /// ("refresh" and "copy" today; an unknown name falls back to the label). A name rather than
        /// a texture keeps this contract free of Unity types, and lets the UI substitute words when
        /// it cannot draw the artwork on the machine it is running on.</summary>
        public string Icon
        {
            get;
        }
    }

    /// <summary>The outcome of an availability probe: usable now, or not and why. The reason is
    /// what the co-op window shows beside the cross, so it must be player-readable and specific
    /// ("Steam isn't running", not "unavailable").</summary>
    public readonly struct BackendAvailability
    {
        public BackendAvailability(bool available, string reason = null)
        {
            Available = available;
            Reason = reason;
        }

        public bool Available
        {
            get;
        }

        /// <summary>Why the backend is not usable, when <see cref="Available"/> is false; null or
        /// empty otherwise.</summary>
        public string Reason
        {
            get;
        }

        public static BackendAvailability Yes => new BackendAvailability(true);

        public static BackendAvailability No(string reason) => new BackendAvailability(false, reason);
    }

    /// <summary>How far automatic port opening got, for the host panel.</summary>
    public enum ForwardState
    {
        /// <summary>No attempt was made (the player turned it off), or nothing has happened yet.</summary>
        None,
        /// <summary>The gateway accepted the mapping request.</summary>
        Mapped,
        /// <summary>No router answered, or the gateway refused.</summary>
        Declined,
    }

    /// <summary>What the session panel shows for the backend that is currently driving.</summary>
    public readonly struct ConnectionInfo
    {
        public ConnectionInfo(bool active, bool isHost, string summary,
            ForwardState forwardState = ForwardState.None,
            string warning = null, string password = null, bool busy = false)
        {
            Active = active;
            IsHost = isHost;
            Summary = summary;
            ForwardState = forwardState;
            Warning = warning;
            Password = password;
            Busy = busy;
        }

        public bool Active
        {
            get;
        }
        public bool IsHost
        {
            get;
        }

        /// <summary>A short, non-sensitive status line. An address that could leak a home
        /// connection on stream is never put here - it travels through an action's clipboard.</summary>
        public string Summary
        {
            get;
        }

        public string Warning
        {
            get;
        }

        /// <summary>How far the host's automatic port opening got, so the panel can say whether the
        /// router opened the port or the player has to forward it. Always <see cref="ForwardState.None"/>
        /// for a backend that does not touch a router (Steam, or a joiner).</summary>
        public ForwardState ForwardState
        {
            get;
        }

        /// <summary>The session password a joiner must present, when this backend uses one; null or
        /// empty when the backend authenticates its peers itself.</summary>
        public string Password
        {
            get;
        }

        /// <summary>True while this backend is WORKING - asking STUN for the public address, creating
        /// or entering a lobby - and <see cref="Summary"/> is the step it
        /// is on. The window prints that step in the container's header and greys its play button
        /// until this goes false again, which happens when the start is ready or has FAILED. A failure
        /// is reported in the container's own body, so the button coming back is not the only signal.</summary>
        public bool Busy
        {
            get;
        }
    }

    /// <summary>
    /// One way to connect two machines, and everything the co-op window needs to drive and
    /// describe it. A backend owns its configuration, connect flow, and a datagram bearer. The
    /// runtime owns the reliable session, peer identity, the peer-id band, and authorization; a
    /// backend must not create or manage any of those.
    ///
    /// This is a public extension surface: a transport ships in its own assembly and registers
    /// itself with the runtime at plugin load. Keep implementations free of any type that can
    /// fail to load on a build the transport does not support.
    /// </summary>
    public interface INetworkingBackend
    {
        /// <summary>Stable id, e.g. "ip" or "steam".</summary>
        string Id
        {
            get;
        }

        /// <summary>Player-facing name, e.g. "IP / LAN" or "Steam".</summary>
        string DisplayName
        {
            get;
        }

        /// <summary>Probe whether this backend can start a session right now, and if not, why. The
        /// reason is shown beside the group's cross in the co-op window. Async because a real probe
        /// can touch the platform; the two questions are separate and neither substitutes for the
        /// other:
        /// <list type="bullet">
        /// <item>the runtime decides which backends EXIST at startup (which transports registered),
        /// not from this;</item>
        /// <item>this answers "is the backend usable right now", which the UI caches and
        /// refreshes rather than calling per frame.</item>
        /// </list>
        /// Implementations must return quickly and never throw - an unusable backend returns a
        /// completed <see cref="BackendAvailability.No(string)"/> carrying the reason.</summary>
        Task<BackendAvailability> GetAvailabilityAsync();

        /// <summary>Apply remembered values and persist them. Never touches the network. Called as
        /// the player edits fields, and once at startup with the backend's own defaults.</summary>
        void Configure(FieldValues values);

        /// <summary>The fields this backend wants for <paramref name="mode"/>, prefilled from the
        /// current configuration. The UI renders one control per field, in order.</summary>
        IReadOnlyList<BackendField> GetFields(ConnectMode mode);

        /// <summary>
        /// Begin a session. The runtime allocates <paramref name="attempt"/> before this call and
        /// passes the same value to every callback; use it verbatim. The backend creates its
        /// datagram bearer and calls <see cref="IBackendConnectCallbacks.BearerReady"/> when the
        /// bearer is live, <see cref="IBackendConnectCallbacks.Connected"/> when the player-facing
        /// attempt is fully live, or <see cref="IBackendConnectCallbacks.Failed"/> when it cannot
        /// start. A rejected attempt never throws. <paramref name="values"/> is merged over the
        /// configured ones.
        /// </summary>
        void Connect(ConnectionId attempt, ConnectMode mode, FieldValues values,
            IBackendConnectCallbacks callbacks);

        /// <summary>End the session behind <paramref name="attempt"/> and release backend-owned
        /// resources (leave a lobby, remove a router mapping). The runtime owns and disposes the
        /// datagram bearer. Ignored if the attempt is no longer current.</summary>
        void Disconnect(ConnectionId attempt);

        /// <summary>Backend-specific buttons for the current view (may be empty).</summary>
        IReadOnlyList<BackendAction> Actions
        {
            get;
        }

        /// <summary>What to show while this backend owns the session.</summary>
        ConnectionInfo ConnectionInfo
        {
            get;
        }

        /// <summary>Local identity for the join handshake: Steam id, or 0 for IP.</summary>
        ulong LocalUserId
        {
            get;
        }

        /// <summary>The platform-provided local display name (Steam persona), or an EMPTY string when
        /// this backend has no platform name. The runtime uses a non-empty value as the default name;
        /// a backend must not answer with the configured or machine name, or it would mask the real
        /// platform name (the Xbox gamertag on Game Pass) and the configured name on LAN.</summary>
        string LocalUserName
        {
            get;
        }

        /// <summary>Raised when a peer's incoming invite was accepted out of band (Steam's overlay
        /// "Join game"): the runtime should start a CLIENT session on this backend with these
        /// values. The runtime enqueues the join onto the Unity thread.</summary>
        event Action<INetworkingBackend, FieldValues> JoinRequested;
    }

    /// <summary>
    /// The callbacks a backend uses to hand its datagram bearer to the runtime, report that the
    /// attempt is fully live, and report session-level failure. They may be invoked synchronously
    /// from <see cref="INetworkingBackend.Connect"/> or later from a platform thread; the runtime
    /// marshals them and ignores a callback whose attempt is no longer current.
    /// </summary>
    public interface IBackendConnectCallbacks
    {
        /// <summary>The backend has a live bearer. The runtime takes ownership from here: it
        /// wraps the bearer in the runtime's reliable session, assigns the peer-id band, starts
        /// it, and disposes it on disconnect. The backend must not start, stop, send on, or dispose
        /// the bearer after this call. A second call for the same attempt is a protocol error.</summary>
        void BearerReady(ConnectionId attempt, IDatagramTransport bearer);

        /// <summary>The attempt is fully live: the player-facing connect flow succeeded. A Steam
        /// backend calls this when the lobby is live or the host connection is established; a
        /// direct backend calls it once its bearer is ready. The runtime clears the backend error
        /// and reports the session as connected.</summary>
        void Connected(ConnectionId attempt);

        /// <summary>The attempt failed before or after its bearer became usable. A failure after
        /// <see cref="BearerReady"/> ends the session; <paramref name="retryable"/> is advisory
        /// for the UI.</summary>
        void Failed(ConnectionId attempt, string reason, bool retryable = false);
    }
}
