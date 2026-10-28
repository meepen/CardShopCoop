using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using CardShopCoop.Modules.Catalog;
using CardShopCoop.Modules.ExtendedInformation;
using CardShopCoop.Modules.GameObjectIdentity;
using CardShopCoop.Modules.Actions;
using CardShopCoop.Modules.Hud;
using CardShopCoop.Modules.Presence;
using CardShopCoop.Modules.SaveTransfer;
using CardShopCoop.Modules.SessionInput;
using CardShopCoop.Modules.World;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Net.Datagrams;
using CardShopCoop.Net.Kcp;
using CardShopCoop.Net.Messages;
using CardShopCoop.Net.Networking;
using CardShopCoop.Net.Protocol;
using CardShopCoop.Runtime;
using PeerConnection = CardShopCoop.Net.Connection.PeerConnection;
using UnityEngine;
using UnityEngine.SceneManagement;
using CardShopCoop.Api;

namespace CardShopCoop
{
    public enum CoopRole
    {
        None,
        Host,
        Client
    }

    /// <summary>What a peer advertised in its Hello: the mod version (always equal to ours - the
    /// handshake enforces it) and the game/Unity build (which can differ on a cross-build join).</summary>
    public readonly struct CoopPeerBuild
    {
        public CoopPeerBuild(string version, string gameVersion, string unityVersion)
        {
            Version = version;
            GameVersion = gameVersion;
            UnityVersion = unityVersion;
        }

        public string Version
        {
            get;
        }
        public string GameVersion
        {
            get;
        }
        public string UnityVersion
        {
            get;
        }
    }

    /// <summary>
    /// Owns session admission, the authenticated control handshake, transport sequencing, and
    /// the bounded main-thread dispatch loop. Feature state and feature messages belong to the
    /// module runtime created at the session boundary.
    /// </summary>
    public partial class CoopCore : MonoBehaviour
    {
        public static CoopCore Instance
        {
            get; private set;
        }

        public static CoopRole Role { get; private set; } = CoopRole.None;

        /// <summary>Raised when the session's role, status text, error text or backend set
        /// changes. UI listens instead of polling the fields.</summary>
        public static event Action StateChanged;

        private string _statusLine = "Not connected";
        private string _errorLine = "";

        public string StatusLine
        {
            get => _statusLine;
            private set
            {
                var next = value ?? "";
                if (string.Equals(_statusLine, next, StringComparison.Ordinal))
                {
                    return;
                }

                _statusLine = next;
                StateChanged?.Invoke();
            }
        }

        public string ErrorLine
        {
            get => _errorLine;
            set
            {
                var next = value ?? "";
                if (string.Equals(_errorLine, next, StringComparison.Ordinal))
                {
                    return;
                }

                _errorLine = next;
                StateChanged?.Invoke();
            }
        }

        /// <summary>Why starting or joining on this backend last failed, for the section the player
        /// actually pressed. Keyed by backend AND mode, because the window draws the two modes as
        /// separate areas: a failure to JOIN must not appear as a problem with HOSTING, which is what
        /// a backend-only key did. Empty when that mode has nothing to report.</summary>
        public string BackendError(INetworkingBackend backend, ConnectMode mode)
        {
            if (backend == null)
            {
                return "";
            }

            lock (_sessionLock)
            {
                return _backendErrors.TryGetValue(BackendErrorKey(backend, mode), out var reason)
                    ? reason
                    : "";
            }
        }

        private void NoteBackendError(INetworkingBackend backend, ConnectMode mode, string reason)
        {
            if (backend == null || string.IsNullOrEmpty(reason))
            {
                return;
            }

            lock (_sessionLock)
            {
                _backendErrors[BackendErrorKey(backend, mode)] = reason;
            }
        }

        private void ClearBackendError(INetworkingBackend backend, ConnectMode mode)
        {
            if (backend == null)
            {
                return;
            }

            lock (_sessionLock)
            {
                _backendErrors.Remove(BackendErrorKey(backend, mode));
            }
        }

        private static string BackendErrorKey(INetworkingBackend backend, ConnectMode mode)
            => backend.Id + (mode == ConnectMode.Host ? "|host" : "|join");
        public string EffectivePlayerName => PresenceApi.LocalPlayerName;

        public ICoopTransport Net
        {
            get; private set;
        }
        /// <summary>Every networking backend this process offers: the built-in IP category plus any
        /// transport that registered with the runtime. The co-op window lists these.
        ///
        /// REBUILT WHEN THE SET CHANGES, NOT ONCE AT STARTUP. A transport registers from its own
        /// plugin's Awake, which BepInEx runs after this core plugin's - the Steam transport is
        /// registered roughly 20 ms later. A list captured once in Awake would name only "ip", and
        /// the window would offer no Steam option at all with nothing on screen to say why.</summary>
        public IReadOnlyList<INetworkingBackend> Backends
        {
            get
            {
                var version = NetworkingBackends.Version;
                if (_backendsVersion != version)
                {
                    _backends = NetworkingBackends.All();
                    _backendsVersion = version;
                }

                return _backends;
            }
        }

        /// <summary>The session password a joiner must present, when the active backend uses one
        /// (Direct IP / LAN); empty for a backend that authenticates itself (Steam).</summary>
        public string HostPassword = "";

        /// <summary>CLIENT ONLY: what the host advertised in its Welcome, so the connected panel can
        /// name the build it joined. Empty while not connected.</summary>
        private string _hostVersion = "";
        private string _hostGameVersion = "";
        private string _hostUnityVersion = "";

        public string HostVersion => _hostVersion;
        public string HostGameVersion => _hostGameVersion;
        public string HostUnityVersion => _hostUnityVersion;

        /// <summary>HOST ONLY: what each joined client advertised in its Hello, keyed by its
        /// connection id, so the window can show the build a client is running.</summary>
        private readonly Dictionary<int, CoopPeerBuild> _peerBuilds = new();

        public bool TryGetPeerBuild(int connectionId, out CoopPeerBuild build)
            => _peerBuilds.TryGetValue(connectionId, out build);

        public string LastDisconnectCode { get; private set; } = "";
        public string LastDisconnectReason { get; private set; } = "";
        public bool LastDisconnectRetryable
        {
            get; private set;
        }

        private IReadOnlyList<INetworkingBackend> _backends = Array.Empty<INetworkingBackend>();
        private int _backendsVersion = -1;
        private INetworkingBackend _activeBackend;
        private ConnectMode _activeMode = ConnectMode.Host;

        /// <summary>Join attempts (see <see cref="CheckJoinAttempts"/>) and the current window.</summary>
        private const int JoinAttempts = 5;
        private const float JoinAttemptSeconds = 5f;
        private int _joinAttempt;
        private float _joinAttemptAt;
        private readonly Dictionary<INetworkingBackend, ConnectionId> _attempts = new();
        private readonly Dictionary<INetworkingBackend, ICoopTransport> _children = new();
        private long _nextAttempt;

        /// <summary>Those two are per session; this one deliberately outlives it. A failed start ends
        /// the session and resets the view to baseline, and the reason has to still be there to read
        /// in the section that failed - it is cleared when that backend is next attempted, or when it
        /// succeeds.</summary>
        private readonly Dictionary<string, string> _backendErrors = new();
        private readonly HashSet<INetworkingBackend> _wired = new();
        private int _wiredBackendsVersion = -1;
        private readonly object _sessionLock = new();
        private CoopTransportGroup _group;
        private ICoopTransport _pump;
        private CoopRuntimeContext _liveRuntimeContext;
        private CoopRuntimeBehaviour _runtime;
        private PersistentRuntimeBehaviour _persistentRuntime;
        private readonly MessageRouter _messageRouter = new();
        private readonly MainThreadDispatcher _dispatcher = new();
        private bool _shutdownCompleted;
        private bool _externalModsDiscovered;
        private bool _sessionInGame;
        private bool _worldReady;

        /// <summary>True while the game's own loading screen is up - a scene or save load. Set by
        /// the <see cref="Util.GameLoadingGuard"/> hook on LoadingScreen.OpenScreen and cleared by
        /// CEventPlayer_FinishHideLoadingScreen, with GameInstance.m_HasFinishHideLoadingScreen as
        /// a backstop (see <see cref="UpdateRunInBackground"/>).</summary>
        private bool _gameLoading;

        /// <summary>The latest invite accepted while a load was in flight. The load's destination
        /// decides whether it can be joined (Title) or must be declined (a shop), so it is held
        /// until the load finishes.</summary>
        private INetworkingBackend _deferredInviteBackend;
        private FieldValues _deferredInviteValues;
        private int _localConnectionId = -1;
        private int _sessionGeneration = 1;
        private ulong _autoJoinSteamLobby;
        private int _autoHostSlot = -1;
        private string _autoJoinIp;
        private int _autoPhase;
        private float _autoTimer;
        private string _joinPassword = "";
        private static int _unityThreadId;

        private readonly List<InMsg> _dispatchBuffer = new(64);
        private readonly HashSet<int> _dispatchRejectedConnections = new();
        private bool _dispatchBacklogWarned;
        private const int DispatchBudget = 256;
        private const int DispatchBacklogCap = DispatchBudget * 8;
        private const int MaxDisconnectResultsPerFrame = 64;

        internal static int SessionGeneration
        {
            get
            {
                var core = Instance;
                return core == null ? -1 : Volatile.Read(ref core._sessionGeneration);
            }
        }

        internal static int LocalConnectionId => Instance == null ? -1 : Instance._localConnectionId;

        /// <summary>The live session context, or null when no session is active. Read by the
        /// public <see cref="CoopApi"/> binding.</summary>
        internal CoopRuntimeContext LiveContext => _liveRuntimeContext;

        internal static bool IsSessionGeneration(int generation)
        {
            var core = Instance;
            return core != null && generation >= 0
                && Volatile.Read(ref core._sessionGeneration) == generation;
        }

        private void Awake()
        {
            Instance = this;
            _unityThreadId = Thread.CurrentThread.ManagedThreadId;
            MainThreadGuard.Capture();
            _messageRouter.RegisterCoreAttributedHandlers(this);
            _messageRouter.HandlerFailed += OnHandlerFailed;
            _dispatcher.Start();
            PresenceApi.ConfigureIdentity(ResolvePlayerName);
            SaveTransferApi.ConfigureCore(
                () => Role,
                () => SessionGeneration,
                IsSessionGeneration,
                action => _dispatcher.TryEnqueue("save-transfer", action),
                fullyJoinedEmitter: () => Send(1, new FullyJoinedMessage()),
                failureReporter: reason =>
                {
                    var text = reason ?? "world transfer failed";
                    if (!_dispatcher.TryEnqueue("save-transfer-failure", () =>
                    {
                        ErrorLine = text;
                        SetStatus(text);
                    }))
                    {
                        CoopPlugin.Log.LogError("coop: could not publish save-transfer failure on the main thread: "
                            + text);
                    }
                },
                transferFailure: (connection, reason) => Net?.GracefulDisconnect(connection,
                    new DisconnectInfo("world transfer failed: " + reason, false,
                        "transfer_failed", true, ConnectionState.Transferring)));

            UI.CoopMenus.Init();
            _persistentRuntime = gameObject.AddComponent<PersistentRuntimeBehaviour>();
            _persistentRuntime.InitializePersistent(new CoopRuntimeContext(
                _messageRouter, InGameLevel, Broadcast,
                Send, ConnectionIds, PresenceApi.PeerName,
                (text, seconds) => SetStatus(text), () => SaveTransferApi.PreloadHold,
                null, null, Connections));
            SceneManager.sceneLoaded += OnSceneLoaded;
            Util.GameLoadingGuard.Install();

            ReadCommandLine();
            _backends = NetworkingBackends.All();
            _backendsVersion = NetworkingBackends.Version;
            NetworkingBackends.LogSet();

            RegisterEventListeners();
            CatalogApi.EnumLendState();
        }

        /// <summary>
        /// Attaches the co-op event subscriptions to the live game event manager. The game's
        /// CEventManager is a scene object and the manager reachable at plugin load can be
        /// destroyed by the first scene change; re-adding through the static AddListener is
        /// idempotent per manager instance, so the subscription always lives on the instance that
        /// is actually dispatching. Without this, GameDataFinishLoaded never reaches the host gate
        /// and hosting reports "wait for your shop to finish loading" forever.
        /// </summary>
        private void RegisterEventListeners()
        {
            CEventManager.AddListener<CEventPlayer_OnOpenCardPack>(OnLocalPackOpened);
            CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnGameDataFinishLoaded);
            CEventManager.AddListener<CEventPlayer_FinishHideLoadingScreen>(OnGameLoadingFinished);
            var manager = CEventManager.Instance;
            if (manager != null)
            {
                CoopPlugin.Log.LogInfo("[events] co-op listeners attached to CEventManager '"
                    + manager.gameObject.name + "'");
            }
        }

        private void ReadCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i] ?? "";
                if (arg.StartsWith("-coopautohost=", StringComparison.Ordinal)
                    && int.TryParse(arg.Substring(14), out var slot))
                {
                    _autoHostSlot = slot;
                }
                else if (arg.StartsWith("-coopautojoin=", StringComparison.Ordinal))
                {
                    _autoJoinIp = arg.Substring(14);
                }
                else if (arg == "+connect_lobby" && i + 1 < args.Length
                    && ulong.TryParse(args[i + 1], out var lobby))
                {
                    _autoJoinSteamLobby = lobby;
                }
            }
        }

        /// <summary>
        /// Starts or extends a session on <paramref name="backend"/>. A HOST may connect several
        /// backends at once (Steam friends and LAN friends in one shop), each with its own handle;
        /// a CLIENT joins through exactly one. The backend reports the outcome through its own
        /// callback, addressed to the handle it returned.
        /// </summary>
        public void ConnectBackend(INetworkingBackend backend, ConnectMode mode,
            FieldValues values)
        {
            ErrorLine = "";
            ClearDisconnectMetadata();
            // Remember which half of the window this attempt belongs to. Every error filed for it goes
            // under that mode, including the asynchronous ones that arrive after this method returns.
            _activeMode = mode;
            ClearBackendError(backend, mode);
            if (backend == null)
            {
                ErrorLine = "No transport selected.";
                return;
            }

            var hosting = mode == ConnectMode.Host;
            if (Role == CoopRole.None)
            {
                if (hosting)
                {
                    if (!TryAdmitHostStart(out var admissionError))
                    {
                        ErrorLine = admissionError;
                        NoteBackendError(backend, mode, admissionError);
                        return;
                    }
                }
                else if (!CanJoinFromHere())
                {
                    ErrorLine = "Join from the main menu (Title screen).";
                    NoteBackendError(backend, mode, ErrorLine);
                    return;
                }

                BeginSession(hosting ? CoopRole.Host : CoopRole.Client);
            }
            else if (!hosting || Role != CoopRole.Host)
            {
                ErrorLine = "Already in a session.";
                NoteBackendError(backend, mode, ErrorLine);
                return;
            }

            EnsureBackendWired(backend);

            // A backend carries exactly one attempt. Release a superseded attempt before starting
            // a new one; otherwise its bearer/lobby would be orphaned because the new handle
            // replaces it in _attempts.
            ConnectionId previous;
            lock (_sessionLock)
            {
                _attempts.TryGetValue(backend, out previous);
            }
            if (previous.IsValid)
            {
                try
                {
                    backend.Disconnect(previous);
                }
                catch (Exception error) { CoopLog.Swallow(error); }
                RemoveBackendChild(backend);
                lock (_sessionLock)
                {
                    _attempts.Remove(backend);
                }
            }

            if (hosting)
            {
                if (values != null && values.TryGetValue(FieldId.Password, out var hostPassword))
                {
                    HostPassword = hostPassword ?? "";
                }
            }
            else
            {
                // The JOIN password is read from the backend's own fields, not from the raw form
                // values: a pasted invite/reach code carries the password, and the backend unfolds
                // it into its Password field in Configure. The raw field would still be blank.
                _joinPassword = FieldValue(backend, ConnectMode.Join, FieldId.Password);
            }

            var attempt = new ConnectionId(Interlocked.Increment(ref _nextAttempt));
            lock (_sessionLock)
            {
                _attempts[backend] = attempt;
            }

            if (!hosting || _activeBackend == null)
            {
                _activeBackend = backend;
            }

            SetStatus(hosting ? "Hosting..." : "Connecting...");

            // Capture the generation NOW: a backend whose connect is asynchronous (Steam's lobby
            // entry) can report back after this session has ended and a new one has started, and
            // that late result must not touch the new session. The attempt handle is stored before
            // Connect, so a synchronous BearerReady/Failed is correlated correctly.
            var generation = SessionGeneration;
            var callbacks = new BackendCallbacks(this, backend, attempt, generation);
            try
            {
                backend.Connect(attempt, mode, values, callbacks);
                callbacks.ConnectReturned();
            }
            catch (Exception error)
            {
                // A backend that threw while connecting is a failed ADDITION, not necessarily the
                // end of the session - see OnBackendFailed, which ends it only when nothing is left.
                callbacks.ConnectReturned();
                if (IsCurrentAttempt(backend, attempt))
                {
                    OnBackendFailed(backend, attempt, "Could not connect: " + error.Message, false);
                }
            }
        }

        public void Disconnect()
        {
            ClearDisconnectMetadata();
            Shutdown("disconnected");
        }

        public void SendEmote() => PresenceApi.SendEmote();

        private void BeginSession(CoopRole role)
        {
            _shutdownCompleted = false;
            _deferredInviteBackend = null;
            _deferredInviteValues = null;
            _dispatcher.Start();
            Interlocked.Increment(ref _sessionGeneration);
            Role = role;
            StateChanged?.Invoke();
            _localConnectionId = role == CoopRole.Host ? 0 : -1;
            _joinAttempt = 0;
            _joinAttemptAt = Time.unscaledTime + JoinAttemptSeconds;
            if (role == CoopRole.Client)
            {
                SaveTransferApi.SetGuestBorrowedWorld(true);
            }

            // The runtime - and with it EVERY module's message handlers, which each behaviour binds in
            // its OnEnable - must exist BEFORE the transport starts, because starting the transport is
            // what freezes the protocol session (KcpSessionManager.Start acquires the catalog lease).
            // A handler registered after that freeze is parked in _pendingHandlers and is NOT
            // dispatchable for the whole session, which is what produced "no handler bound" for every
            // module message and eventually overflowed the client's inbound queue.
            //
            // This must NOT wait for Net to exist: at this point the transport has not been created yet,
            // and waiting is precisely the ordering that broke. Behaviours bind handlers on activation
            // and only send from their own ticks, by which time the transport is there.
            ActivateLiveModuleHooks();
            ExtendedInformationCatalog.BeginSession();

            // Persistent behaviours (SessionInput, debug hooks, external UI/input) outlive a
            // session, so the live runtime above cannot deliver their start event. The persistent
            // registry gets it here, and Shutdown sends the matching stop.
            _persistentRuntime?.SessionStarted();
        }

        /// <summary>
        /// A join is "attempting" while the session exists and no peer has answered. Nothing else ends it:
        /// the transport keeps probing happily, so a friend who never made it through leaves the player
        /// on CONNECTING forever with no reason given. After <see cref="JoinAttempts"/> windows of
        /// <see cref="JoinAttemptSeconds"/> the attempt is failed, which is what surfaces the reason and
        /// lets the UI reveal whatever the player needs to change.
        /// </summary>
        private void CheckJoinAttempts()
        {
            if (Role != CoopRole.Client || Net == null || Net.ConnectionCount > 0)
            {
                return;
            }

            var now = Time.unscaledTime;
            if (now < _joinAttemptAt)
            {
                return;
            }

            _joinAttempt++;
            if (_joinAttempt < JoinAttempts)
            {
                _joinAttemptAt = now + JoinAttemptSeconds;
                CoopPlugin.Log.LogInfo("coop: still trying to reach the host (attempt "
                    + _joinAttempt + " of " + JoinAttempts + ")");
                return;
            }

            AbortSessionStart("no answer from your friend's game after "
                + (JoinAttempts * JoinAttemptSeconds) + "s ("
                + JoinAttempts + " attempts).");
        }

        // ------------------------------------------------------------------ backend wiring

        /// <summary>Subscribes to a backend's long-lived events once: an out-of-band join request,
        /// and - when the backend implements the optional notification companion - presentation
        /// state that changes outside a session event. The connect attempt itself is delivered
        /// through the per-attempt <see cref="IBackendConnectCallbacks"/>, so there is no
        /// long-lived transport event to wire here.</summary>
        private void EnsureBackendWired(INetworkingBackend backend)
        {
            lock (_sessionLock)
            {
                if (!_wired.Add(backend))
                {
                    return;
                }
            }

            backend.JoinRequested += (requesting, values) => OnBackendJoinRequested(requesting, values);
            if (backend is INetworkingBackendNotifications notifications)
            {
                notifications.StateChanged += OnBackendStateChanged;
            }
        }

        /// <summary>A backend resolved presentation state on its own (the IP host's public address
        /// arriving, a Steam lobby going live). The window re-reads ConnectionInfo/Actions from the
        /// StateChanged event, and a backend may raise this on a worker thread, so queue it.
        ///
        /// ALWAYS QUEUE, even when the caller is already on the Unity thread: a subscriber that
        /// throws must not propagate back into the backend's Connect/Disconnect (which would abort
        /// a mid-start transport and leak its bearer). The dispatcher is the boundary that logs and
        /// contains such a failure.</summary>
        private void OnBackendStateChanged(INetworkingBackend backend)
        {
            if (!TryEnqueueMainThread(() => StateChanged?.Invoke()))
            {
                CoopPlugin.Log?.LogWarning("coop: could not queue a state change from '"
                    + backend.Id + "' on the Unity thread");
            }
        }

        /// <summary>
        /// Subscribes Core to each backend's events the moment the backend exists, rather than only
        /// when a session first starts. The Steam transport registers itself from its own plugin's
        /// Awake, which BepInEx runs after this core plugin's, and a Steam invite can arrive at any
        /// moment - including before the player has ever hosted or joined this run. Wiring only
        /// inside <see cref="ConnectBackend"/> left <see cref="INetworkingBackend.JoinRequested"/>
        /// with no subscriber until a first session, so accepting an invite did nothing at all. The
        /// Backends getter refreshes the cached list when the set changes; the version guard keeps
        /// this to one wiring pass per change.
        /// </summary>
        private void EnsureBackendsWired()
        {
            var backends = Backends;
            if (_wiredBackendsVersion == _backendsVersion)
            {
                return;
            }

            foreach (var backend in backends)
            {
                EnsureBackendWired(backend);
            }

            // Advance the guard only after the whole set is wired, so a backend whose subscription
            // threw leaves the version stale and the remaining backends are retried next frame
            // rather than being skipped for the rest of the run.
            _wiredBackendsVersion = _backendsVersion;
            StateChanged?.Invoke();
        }

        /// <summary>
        /// A backend received an out-of-band join request the player accepted (a Steam invite).
        /// Session admission belongs to the runtime, so this just starts a client session - on the
        /// Unity thread, which is where ConnectBackend must run.
        /// </summary>
        private void OnBackendJoinRequested(INetworkingBackend backend, FieldValues values)
        {
            if (!TryEnqueueMainThread(() => HandleInviteOnMainThread(backend, values)))
            {
                CoopPlugin.Log.LogError(
                    "coop: could not queue the inbound join request on the Unity thread");
            }
        }

        /// <summary>
        /// Runs an accepted invite on the Unity thread, where the role and scene state are safe to
        /// read and where <see cref="ConnectBackend"/> must run. An invite can only ever start a
        /// client session; when that is impossible the player is told why instead of the request
        /// being dropped silently.
        /// </summary>
        private void HandleInviteOnMainThread(INetworkingBackend backend, FieldValues values)
        {
            if (Role == CoopRole.None && _gameLoading)
            {
                // Title -> shop and shop -> Title look identical during the load. Defer until the
                // destination is known: joining a load in flight would start a session that
                // OnSceneLoaded immediately kills.
                _deferredInviteBackend = backend;
                _deferredInviteValues = values;
                CoopPlugin.Log.LogInfo("deferring an inbound join request until the load finishes");
                return;
            }

            if (!CanJoinFromHere())
            {
                // Joining borrows the host's world, which only replaces the world cleanly from the
                // Title screen. A load in flight (Title -> shop) is equally unsafe, and its
                // destination is a shop rather than the menu.
                CoopPlugin.Log.LogInfo("declining an inbound join request - not at the title screen");
                ShowError("CO-OP INVITE",
                    "Join from the main menu to accept this invite. Return to the Title screen "
                    + "(Back to menu), then ask your friend to invite you again.");
                return;
            }

            // A join that is still connecting has no session to protect. Steam has already
            // consumed the invite, so refusing it would make the player ask for another one even
            // though they never got into a game. Tear the pending attempt down the same way the
            // Leave button does, then start the new one below.
            if (Role == CoopRole.Client && (Net == null || Net.ConnectionCount == 0))
            {
                CoopPlugin.Log.LogInfo("replacing the pending join with a new invite");
                Disconnect();
            }

            if (Role != CoopRole.None)
            {
                CoopPlugin.Log.LogInfo("ignoring an inbound join request - already in a session");
                var hosting = Role == CoopRole.Host;
                ShowError("CO-OP INVITE", hosting
                    ? "You're already hosting a co-op session. Stop hosting before accepting a friend's invite."
                    : "You're already in a co-op session. Leave the session before accepting a friend's invite.");
                return;
            }

            ConnectBackend(backend, ConnectMode.Join, values);
        }

        private void OnBackendConnected(INetworkingBackend backend)
        {
            ClearBackendError(backend, _activeMode);

            // A LAN host generates its session password inside the backend; adopt it here so the
            // join handshake compares against the real one.
            var info = backend.ConnectionInfo;
            if (Role == CoopRole.Host && !string.IsNullOrEmpty(info.Password))
            {
                HostPassword = info.Password;
            }

            ErrorLine = "";
            SetStatus(Role == CoopRole.Host ? "Hosting - waiting for a player..." : "Connected");
        }

        private void OnBackendTransportReady(INetworkingBackend backend, ConnectionId attempt,
            ICoopTransport transport, int band)
        {
            if (transport == null)
            {
                RemoveBackendChild(backend);
                return;
            }

            if (!IsCurrentAttempt(backend, attempt))
            {
                // The attempt was replaced before its bearer reached the pump. Dispose the
                // unstarted transport rather than leaving it alive with no owner.
                try
                {
                    transport.Dispose();
                }
                catch (Exception error) { CoopLog.Swallow(error); }
                return;
            }

            // A second bearer for the same backend replaces the old child. Remove any superseded
            // child first: its Stop/Dispose is queued on the pump thread.
            RemoveBackendChild(backend);

            lock (_sessionLock)
            {
                _children[backend] = transport;
            }

            if (_group == null)
            {
                _group = new CoopTransportGroup();
            }

            _group.Add(transport, band);
            if (_pump == null)
            {
                _pump = NetworkPump.Wrap(_group);
                Net = _pump;
                ((ICoopStartable)_pump).Start();
                if (Role != CoopRole.None && _runtime == null)
                {
                    ActivateLiveModuleHooks();
                }
            }
        }

        private bool IsCurrentAttempt(INetworkingBackend backend, ConnectionId attempt)
        {
            lock (_sessionLock)
            {
                return _attempts.TryGetValue(backend, out var current) && current == attempt;
            }
        }

        private void HandleBearerReady(INetworkingBackend backend, ConnectionId attempt,
            IDatagramTransport bearer, int generation)
        {
            if (!IsSessionGeneration(generation) || !IsCurrentAttempt(backend, attempt))
            {
                try
                {
                    bearer?.Dispose();
                }
                catch (Exception error) { CoopLog.Swallow(error); }
                return;
            }

            if (bearer == null)
            {
                OnBackendFailed(backend, attempt, "the backend produced no bearer", false);
                return;
            }

            var isHost = Role == CoopRole.Host;
            var band = isHost ? NetworkingBackends.BandOf(backend) : 1;
            KcpSessionManager kcp;
            try
            {
                kcp = new KcpSessionManager(bearer, isHost);
                kcp.SetConnectionIdBand(band, band + (isHost ? NetworkingBands.Size : 1));
                kcp.PeerConnected += OnBackendPeerConnected;
            }
            catch (Exception error)
            {
                try
                {
                    bearer.Dispose();
                }
                catch (Exception disposeError) { CoopLog.Swallow(disposeError); }
                OnBackendFailed(backend, attempt, "transport startup failed: " + error.Message, false);
                return;
            }

            OnBackendTransportReady(backend, attempt, LagTransport.Wrap(kcp), band);
        }

        private void HandleBackendConnected(INetworkingBackend backend, ConnectionId attempt,
            int generation)
        {
            if (!IsSessionGeneration(generation) || !IsCurrentAttempt(backend, attempt))
            {
                return;
            }

            OnBackendConnected(backend);
        }

        private void HandleBackendFailed(INetworkingBackend backend, ConnectionId attempt,
            string reason, bool retryable, int generation)
        {
            if (!IsSessionGeneration(generation) || !IsCurrentAttempt(backend, attempt))
            {
                return;
            }

            OnBackendFailed(backend, attempt, reason, retryable);
        }

        /// <summary>Marshals a backend callback to the Unity thread. Callbacks may arrive on a
        /// platform thread (Steam), and the session/Unity objects must only be touched here.</summary>
        private void RunOnMainThread(Action action)
        {
            if (action == null)
            {
                return;
            }

            if (Thread.CurrentThread.ManagedThreadId == _unityThreadId)
            {
                action();
                return;
            }

            if (!TryEnqueueMainThread(action))
            {
                CoopPlugin.Log?.LogError(
                    "coop: could not queue a backend callback on the Unity thread");
            }
        }

        /// <summary>Per-attempt callbacks. Synchronous callbacks from Connect are queued until
        /// Connect returns, then marshalled to the Unity thread and checked against the current
        /// attempt and session generation.</summary>
        private sealed class BackendCallbacks : IBackendConnectCallbacks
        {
            private readonly CoopCore _core;
            private readonly INetworkingBackend _backend;
            private readonly ConnectionId _attempt;
            private readonly int _generation;
            private readonly object _gate = new();
            private readonly Queue<Action> _pending = new();
            private bool _connectReturned;

            public BackendCallbacks(CoopCore core, INetworkingBackend backend,
                ConnectionId attempt, int generation)
            {
                _core = core;
                _backend = backend;
                _attempt = attempt;
                _generation = generation;
            }

            public void BearerReady(ConnectionId attempt, IDatagramTransport bearer)
            {
                if (attempt != _attempt)
                {
                    return;
                }

                Dispatch(() => _core.HandleBearerReady(_backend, _attempt, bearer, _generation));
            }

            public void Connected(ConnectionId attempt)
            {
                if (attempt != _attempt)
                {
                    return;
                }

                Dispatch(() => _core.HandleBackendConnected(_backend, _attempt, _generation));
            }

            public void Failed(ConnectionId attempt, string reason, bool retryable = false)
            {
                if (attempt != _attempt)
                {
                    return;
                }

                Dispatch(() => _core.HandleBackendFailed(
                    _backend, _attempt, reason, retryable, _generation));
            }

            public void ConnectReturned()
            {
                Action[] pending;
                lock (_gate)
                {
                    _connectReturned = true;
                    if (_pending.Count == 0)
                    {
                        return;
                    }

                    pending = _pending.ToArray();
                    _pending.Clear();
                }

                _core.RunOnMainThread(() =>
                {
                    for (var i = 0; i < pending.Length; i++)
                    {
                        pending[i]();
                    }
                });
            }

            private void Dispatch(Action action)
            {
                lock (_gate)
                {
                    if (!_connectReturned)
                    {
                        _pending.Enqueue(action);
                        return;
                    }
                }

                _core.RunOnMainThread(action);
            }
        }

        private void RemoveBackendChild(INetworkingBackend backend)
        {
            ICoopTransport child;
            lock (_sessionLock)
            {
                if (!_children.TryGetValue(backend, out child))
                {
                    return;
                }
                _children.Remove(backend);
            }

            _group?.Remove(child);
        }

        /// <summary>
        /// Stops hosting on ONE backend: its listeners go down and the peers that came in through it
        /// disconnect, while the session and every other hosting backend keep running. A shop can be
        /// reachable over LAN and Steam friends at once, and either can be closed on its own.
        ///
        /// The last one ends the session instead, because a host with no transport is just a dead
        /// session - the modules and the runtime would keep running with nothing to send over.
        /// </summary>
        public void StopHosting(INetworkingBackend backend)
        {
            if (backend == null)
            {
                return;
            }

            if (Role != CoopRole.Host)
            {
                // Nothing to stop: this is not hosting. Say so rather than returning silently, because
                // the window draws this container's Stop from the BACKEND's own active flag - so the two
                // disagreeing is exactly the case worth seeing in a log.
                CoopPlugin.Log.LogInfo("stop ignored for " + backend.Id + ": role is " + Role);
                return;
            }

            var last = false;
            ConnectionId connection;
            lock (_sessionLock)
            {
                if (!_attempts.TryGetValue(backend, out connection))
                {
                    // The backend says it is hosting - that is why the button is there - but this
                    // session has no connection recorded for it. Log the disagreement: it is the only
                    // way to tell a stale backend flag from a Stop press that did nothing.
                    CoopPlugin.Log.LogInfo("stop ignored for " + backend.Id
                        + ": it is not carrying this session (" + _attempts.Count
                        + " backend(s) are)");
                    return;
                }

                last = _attempts.Count <= 1;
                if (!last)
                {
                    _attempts.Remove(backend);
                }
            }

            if (last)
            {
                Disconnect();
                return;
            }

            // The runtime owns the transport and tears it down through the group; the backend only
            // releases its own lobby/mapping state. Remove this backend's child while the others stay.
            backend.Disconnect(connection);
            RemoveBackendChild(backend);
            // The backend may not implement the optional notification interface, and no session
            // event fires for "one of several hosts stopped", so invalidate the window here too.
            StateChanged?.Invoke();
        }

        /// <summary>HOST ONLY: remove one client from the session, telling it why. The host and every
        /// other client keep playing; only the named peer is dropped. Guarded on the role because
        /// this is a public API: a client calling it would otherwise drop its OWN host link (its only
        /// connection) and misreport it as a kick.</summary>
        public void KickPeer(int connectionId)
        {
            if (Role != CoopRole.Host || connectionId <= 0)
            {
                CoopPlugin.Log.LogWarning("kick ignored: role=" + Role + " connection=" + connectionId);
                return;
            }

            var connection = ConnectionFor(connectionId);
            if (connection == null || Net == null)
            {
                CoopPlugin.Log.LogWarning("kick ignored: no live connection " + connectionId);
                return;
            }

            CoopPlugin.Log.LogInfo("kicking peer " + connectionId);
            Net.GracefulDisconnect(connection,
                new DisconnectInfo("the host removed you from the session.", false, "kicked",
                    false, connection.State));
        }

        private void OnBackendPeerConnected(PeerConnection connection)
        {
            if (connection == null || Net == null || Role != CoopRole.Client)
            {
                return;
            }

            // The transport raises this from its pump thread, which is the network thread.
            // SendHello reads UnityEngine.Application and does catalog file/reflection work, so it
            // must stay on the Unity thread rather than blocking the network pump.
            if (Thread.CurrentThread.ManagedThreadId == _unityThreadId)
            {
                SendHello();
            }
            else if (!TryEnqueueMainThread(SendHello))
            {
                CoopPlugin.Log.LogError(
                    "coop: could not queue the application Hello on the Unity thread");
            }
        }

        private void OnBackendFailed(INetworkingBackend backend, ConnectionId attempt,
            string reason, bool retryable)
        {
            var text = reason ?? "connection failed";

            var remaining = 0;
            lock (_sessionLock)
            {
                if (!_attempts.TryGetValue(backend, out var current) || current != attempt)
                {
                    return;
                }

                _attempts.Remove(backend);
                remaining = _attempts.Count;
            }

            // File it against the backend that produced it, in the mode it was started in, so it lands
            // in that backend's own section instead of reading as a session-wide problem.
            ErrorLine = text;
            NoteBackendError(backend, _activeMode, text);
            CoopPlugin.Log.LogError("coop: " + (backend?.Id ?? "session") + " failed: " + text);

            // The backend owns only its lobby/backend state now; the runtime owns the bearer and
            // disposes it through the pump. Disconnect releases the backend-owned state.
            try
            {
                backend.Disconnect(attempt);
            }
            catch (Exception error) { CoopLog.Swallow(error); }

            RemoveBackendChild(backend);

            // The session only ends when nothing is left to carry it. A backend that failed while
            // another one is still hosting is an addition that did not work, not the end of anything.
            if (Role == CoopRole.Client || remaining == 0)
            {
                Shutdown(text);
            }
            else
            {
                CoopPlugin.Log.LogWarning("transport '" + backend.Id + "' dropped: " + reason
                    + (retryable ? " (retryable)" : ""));
                // The session lives on through another backend, so there is no Shutdown event to
                // refresh the failed backend's panel with its now-inactive state.
                StateChanged?.Invoke();
            }
        }

        private void ActivateLiveModuleHooks()
        {
            // Deliberately logged BEFORE the early return: the question this answers is whether the
            // request arrives at all, with what role, and whether a transport exists yet. A client can
            // only bind its module handlers once this has actually created a runtime, and every previous
            // guess about that ordering has been wrong, so the path now says what it saw.
            CoopPlugin.Log.LogInfo("co-op runtime activation: role=" + Role
                + ", runtime=" + (_runtime == null ? "none" : _runtime.GetType().Name)
                + ", net=" + (Net == null ? "none" : "up"));

            if (_runtime != null)
            {
                return;
            }

            var context = new CoopRuntimeContext(_messageRouter, InGameLevel,
                Broadcast, Send, ConnectionIds,
                PresenceApi.PeerName, (text, seconds) => SetStatus(text),
                () => SaveTransferApi.PreloadHold,
                (connectionId, info) =>
                {
                    var connection = ConnectionFor(connectionId);
                    if (connection != null)
                    {
                        Net?.GracefulDisconnect(connection, info);
                    }
                },
                Relay, Connections);
            _liveRuntimeContext = context;
            _runtime = Role == CoopRole.Host
                ? gameObject.AddComponent<ServerRuntimeBehaviour>()
                : gameObject.AddComponent<ClientRuntimeBehaviour>();
            try
            {
                _runtime.Initialize(context);
            }
            catch
            {
                _liveRuntimeContext = null;
                Destroy(_runtime);
                _runtime = null;
                throw;
            }
        }

        private void DeactivateLiveModuleHooks()
        {
            if (_runtime == null)
            {
                return;
            }

            var runtime = _runtime;
            _runtime = null;
            try
            {
                runtime.Shutdown();
            }
            catch (Exception error)
            {
                // Never let a feature teardown skip the destroyed-component cleanup or unwind
                // the caller's shutdown sequence.
                CoopPlugin.Log?.LogError("Co-op runtime shutdown failed: " + error);
            }
            finally
            {
                Destroy(runtime);
            }
        }

        private void AbortSessionStart(string reason, INetworkingBackend backend = null)
        {
            ErrorLine = reason ?? "session could not start";
            NoteBackendError(backend, _activeMode, ErrorLine);
            CoopPlugin.Log.LogError("coop: " + ErrorLine);
            Shutdown(ErrorLine);
        }

        private void SendHello()
        {
            var catalog = CatalogHandshake.BuildHelloData();
            var messageCatalog = GetFrozenMessageCatalog();
            var hello = new HelloMessage
            {
                WireVersion = Msg.WireVersion,
                Version = CoopPlugin.Version,
                PlayerName = EffectivePlayerName,
                SteamId = _activeBackend == null ? 0UL : _activeBackend.LocalUserId,
                Password = _joinPassword,
                PluginHash = Util.ModParity.PluginHash(),
                EnumHash = catalog.EnumHash,
                CardsHash = catalog.CardsHash,
                PluginList = Util.ModParity.PluginList(),
                CardsList = catalog.CardsList,
                EnumIdentity = catalog.EnumIdentity,
                GameVersion = Application.version ?? "",
                UnityVersion = Application.unityVersion ?? "",
                MessageCatalog = messageCatalog,
                MessageReliability = GetFrozenMessageReliability(messageCatalog),
                ExtendedInformationCatalog = GetFrozenExtendedInformationCatalog(),
            };
            var connection = ConnectionFor(1);
            if (connection == null || Net is not ICoopHandshakeTransport handshake)
            {
                throw new InvalidOperationException("could not send the application Hello: no live handshake transport");
            }
            handshake.SendHandshake(connection, hello);
        }

        private static List<string> GetFrozenMessageCatalog()
        {
            var snapshot = MessageRegistry.CurrentSnapshot;
            var orderedNames = snapshot.OrderedWireNames;
            var names = new List<string>(orderedNames.Count);
            for (var i = 0; i < orderedNames.Count; i++)
            {
                var name = orderedNames[i];
                if (name == null)
                {
                    throw new InvalidOperationException("the frozen message registry contains a DTO without a Type.FullName");
                }

                names.Add(name);
            }

            return names;
        }

        private static List<string> GetFrozenExtendedInformationCatalog()
            => new(ExtendedInformationCatalog.Keys);

        private static byte[] GetFrozenMessageReliability(IReadOnlyList<string> messageCatalog)
        {
            if (messageCatalog == null)
            {
                throw new ArgumentNullException(nameof(messageCatalog));
            }

            var result = new byte[(messageCatalog.Count + 7) / 8];
            var snapshot = MessageRegistry.CurrentSnapshot;
            for (var i = 0; i < messageCatalog.Count; i++)
            {
                if (!snapshot.TryGet(messageCatalog[i], out var descriptor))
                {
                    throw new InvalidOperationException("the frozen message registry is missing "
                        + messageCatalog[i]);
                }

                if (descriptor.Reliability == Reliability.Transient)
                {
                    result[i / 8] |= (byte)(1 << (i & 7));
                }
            }

            return result;
        }

        private static bool TryValidateMessageReliability(byte[] peerBits,
            byte[] expectedBits, int messageCount, out string reason)
        {
            if (peerBits == null)
            {
                reason = "message reliability bitset is missing";
                return false;
            }

            var expectedLength = (messageCount + 7) / 8;
            if (peerBits.Length != expectedLength)
            {
                reason = "message reliability bitset length differs (peer " + peerBits.Length
                    + ", local " + expectedLength + ")";
                return false;
            }

            if (expectedBits == null || expectedBits.Length != expectedLength)
            {
                throw new InvalidOperationException("local message reliability bitset is invalid");
            }

            if (expectedLength > 0 && (messageCount & 7) != 0)
            {
                var unusedMask = (byte)(0xFF << (messageCount & 7));
                if ((peerBits[expectedLength - 1] & unusedMask) != 0)
                {
                    reason = "message reliability bitset has non-zero unused tail bits";
                    return false;
                }
                if ((expectedBits[expectedLength - 1] & unusedMask) != 0)
                {
                    throw new InvalidOperationException(
                        "local message reliability bitset has non-zero unused tail bits");
                }
            }

            for (var i = 0; i < expectedLength; i++)
            {
                if (peerBits[i] != expectedBits[i])
                {
                    reason = "message reliability differs at byte " + i;
                    return false;
                }
            }

            reason = null;
            return true;
        }

        private static bool TryValidateMessageCatalog(IList<string> peerCatalog,
            IReadOnlyList<string> expectedCatalog, out string reason)
            => TryValidateCatalog(peerCatalog, expectedCatalog, "message", out reason);

        /// <summary>
        /// Exact, ordinal, order-sensitive comparison shared by the message catalog and the
        /// extended-information catalog. Both are identities, so a peer that is missing, has an
        /// extra, or ordered a key differently is refused rather than silently diverging.
        /// </summary>
        private static bool TryValidateCatalog(IList<string> peerCatalog,
            IReadOnlyList<string> expectedCatalog, string label, out string reason)
        {
            if (peerCatalog == null)
            {
                reason = label + " catalog is missing";
                return false;
            }

            if (peerCatalog.Count != expectedCatalog.Count)
            {
                reason = label + " catalog count differs (peer " + peerCatalog.Count
                    + ", local " + expectedCatalog.Count + ")";
                return false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < peerCatalog.Count; i++)
            {
                var peerName = peerCatalog[i];
                if (peerName == null)
                {
                    reason = label + " catalog contains a null name at index " + i;
                    return false;
                }
                if (!seen.Add(peerName))
                {
                    reason = label + " catalog contains a duplicate name at index " + i;
                    return false;
                }
            }

            for (var i = 0; i < expectedCatalog.Count; i++)
            {
                if (!string.Equals(peerCatalog[i], expectedCatalog[i], StringComparison.Ordinal))
                {
                    reason = label + " catalog differs at index " + i + " (peer '"
                        + BoundedMessageName(peerCatalog[i]) + "', local '"
                        + BoundedMessageName(expectedCatalog[i]) + "')";
                    return false;
                }
            }

            reason = null;
            return true;
        }

        private static string BoundedMessageName(string name)
        {
            const int MaxNameInReason = 96;
            if (name == null)
            {
                return "<null>";
            }

            return name.Length <= MaxNameInReason
                ? name : name.Substring(0, MaxNameInReason) + "...";
        }

        private void ActivateMessageIdsBeforeTransfer(ICoopTransport transport,
            PeerConnection connection, int generation)
        {
            // The transport owns its pump thread and marshals activation onto it, blocking until
            // the transition is complete. Callers (Unity or worker threads) can invoke it
            // directly; there is no dispatcher hop or self-deadlock to work around.
            if (!IsSessionGeneration(generation))
            {
                throw new InvalidOperationException(
                    "session ended before message-id activation");
            }
            transport.ActivateMessageIds(connection);
        }

        [MessageHandler]
        private void HandleHello(MessageContext context, HelloMessage hello)
        {
            var connection = context?.Connection;
            if (Role != CoopRole.Host || connection == null || hello == null
                || !ExpectControlState(connection, "Hello", ConnectionState.Handshaking))
            {
                return;
            }

            var name = hello.PlayerName ?? "";
            if (hello.WireVersion != Msg.WireVersion)
            {
                RejectConn(connection, "wire protocol mismatch - host uses protocol " + Msg.WireVersion);
                return;
            }
            if (!string.Equals(hello.Version, CoopPlugin.Version, StringComparison.Ordinal))
            {
                RejectConn(connection, "version mismatch - host runs " + CoopPlugin.Version);
                return;
            }
            if (!string.Equals(hello.Password ?? "", HostPassword, StringComparison.Ordinal))
            {
                RejectConn(connection, "wrong password", true);
                return;
            }
            if ((hello.GameVersion ?? "") != (Application.version ?? "")
                || (hello.UnityVersion ?? "") != (Application.unityVersion ?? ""))
            {
                if (!CoopPlugin.AllowCrossBuildJoin.Value)
                {
                    RejectConn(connection, "your GAME build doesn't match the host's", false);
                    return;
                }
                CoopPlugin.Log.LogWarning("AllowCrossBuildJoin is ON - accepting " + name
                    + " across game builds; save compatibility is not guaranteed.");
            }
            if (!string.Equals(hello.PluginHash, Util.ModParity.PluginHash(), StringComparison.Ordinal))
            {
                // Name the offending mods so both players can fix the mismatch. The same
                // bounded text is logged by RejectConn on the host and travels over the wire
                // to the client, which logs and displays it via its normal disconnect path.
                RejectConn(connection, Util.ModParity.DescribeMismatch(
                    hello.PluginList, DisconnectInfo.MaxDetailLength));
                return;
            }

            var messageCatalog = GetFrozenMessageCatalog();
            if (!TryValidateMessageCatalog(hello.MessageCatalog, messageCatalog, out var messageCatalogReason))
            {
                CoopPlugin.Log.LogWarning("message catalog handshake rejected for connection "
                    + connection.Id + ": " + messageCatalogReason);
                RejectConn(connection, "message catalog mismatch: " + messageCatalogReason);
                return;
            }
            var messageReliability = GetFrozenMessageReliability(messageCatalog);
            if (!TryValidateMessageReliability(hello.MessageReliability, messageReliability,
                messageCatalog.Count, out var messageReliabilityReason))
            {
                CoopPlugin.Log.LogWarning("message reliability handshake rejected for connection "
                    + connection.Id + ": " + messageReliabilityReason);
                RejectConn(connection, "message reliability mismatch: " + messageReliabilityReason);
                return;
            }

            var extendedInformationCatalog = GetFrozenExtendedInformationCatalog();
            if (!TryValidateCatalog(hello.ExtendedInformationCatalog, extendedInformationCatalog,
                "extended-information", out var extendedInformationReason))
            {
                CoopPlugin.Log.LogWarning("extended-information catalog handshake rejected for "
                    + "connection " + connection.Id + ": " + extendedInformationReason);
                RejectConn(connection, "extended-information catalog mismatch: "
                    + extendedInformationReason);
                return;
            }

            if (!CatalogHandshake.IsIdentityUsable(hello.EnumIdentity))
            {
                CoopPlugin.Log.LogWarning("catalog handshake rejected connection " + connection.Id
                    + ": invalid or empty client enum identity.");
                RejectConn(connection, "invalid custom-content catalog from client");
                return;
            }

            var enumValidation = CatalogHandshake.ValidateEnums(name, hello.EnumHash,
                hello.EnumIdentity);
            if (!enumValidation.HashesAreConsistent)
            {
                var reason = enumValidation.HashFailureReason
                    ?? "the catalog hash did not match the decoded catalog";
                CoopPlugin.Log.LogWarning("catalog handshake rejected connection " + connection.Id
                    + ": " + reason);
                RejectConn(connection, "invalid custom-card catalog: " + reason);
                return;
            }
            if (enumValidation.Conflicts.Count > 0)
            {
                CoopPlugin.Log.LogWarning("catalog handshake rejected connection " + connection.Id
                    + ": custom-content name set differs (" + enumValidation.Conflicts.Count
                    + " entries).");
                RejectConn(connection,
                    CatalogHandshake.DescribeEnumKeysMismatch(enumValidation.Conflicts));
                return;
            }
            if (!CatalogHandshake.TryValidateCards(hello.CardsHash, hello.CardsList, out var cardReason))
            {
                RejectConn(connection, cardReason);
                return;
            }

            var peerName = ResolvePeerName(hello.SteamId, name);
            PresenceApi.SetPeerName(connection.Id, peerName);
            PresenceApi.ApplyName(connection);
            // Remember the client's enum identity for the cross-build pre-send gate: it is the
            // only place the host learns which object types this peer can represent.
            CatalogIdMap.SetPeerIdentity(hello.EnumIdentity);
            if (!connection.TryTransition(ConnectionState.Transferring))
            {
                CoopPlugin.Log.LogWarning("Ignoring Hello from connection in invalid phase " + connection.State);
                return;
            }

            // Only once the connection is actually admitted: this branch does NOT disconnect, so a
            // write before the transition would linger until the handshake timeout reaps the peer.
            _peerBuilds[connection.Id] = new CoopPeerBuild(
                hello.Version ?? "", hello.GameVersion ?? "", hello.UnityVersion ?? "");

            try
            {
                _runtime?.ClientJoined(connection);
            }
            catch (Exception error)
            {
                // Feature callbacks are isolated from the control transition.
                CoopPlugin.Log.LogError("Feature ClientJoined callbacks failed: " + error);
            }

            SetStatus("Hosting - " + peerName + " joined!");
            CoopPlugin.Log.LogInfo(peerName + " passed admission; starting world transfer");
            var blobs = CatalogHandshake.BuildWelcomeBlobs();
            var transport = Net;
            var transferGeneration = SessionGeneration;
            if (transport == null)
            {
                RejectConn(connection, "transport disappeared before world transfer");
                return;
            }
            if (!SaveTransferApi.TryAuthorizeAndSend(connection, offer =>
            {
                if (transport is not ICoopHandshakeTransport handshake)
                {
                    throw new InvalidOperationException(
                        "could not send Welcome: no live handshake transport");
                }
                handshake.SendHandshake(connection, new WelcomeMessage
                {
                    WireVersion = Msg.WireVersion,
                    Version = CoopPlugin.Version,
                    HostName = EffectivePlayerName,
                    GameVersion = Application.version ?? "",
                    UnityVersion = Application.unityVersion ?? "",
                    SteamId = _activeBackend == null ? 0UL : _activeBackend.LocalUserId,
                    SaveLength = offer.SaveLength,
                    BundleLength = offer.BundleLength,
                    SidecarsComplete = offer.SidecarsComplete,
                    SidecarWarning = offer.SidecarWarning,
                    SelfId = connection.Id,
                    HostEnumIdentity = blobs.EnumIdentity,
                    HostCardsBlob = blobs.CardsBlob,
                    MessageCatalog = new List<string>(messageCatalog),
                    MessageReliability = (byte[])messageReliability.Clone(),
                    ExtendedInformationCatalog = GetFrozenExtendedInformationCatalog(),
                });
                return true;
            }, () =>
            {
                ActivateMessageIdsBeforeTransfer(transport, connection, transferGeneration);
            }))
            {
                RejectConn(connection, "could not start the world transfer");
            }
        }

        [MessageHandler]
        private void HandleWelcome(MessageContext context, WelcomeMessage welcome)
        {
            var connection = context?.Connection;
            if (Role != CoopRole.Client || connection == null || welcome == null
                || !ExpectControlState(connection, "Welcome", ConnectionState.Handshaking))
            {
                return;
            }
            if (welcome.WireVersion != Msg.WireVersion
                || !string.Equals(welcome.Version, CoopPlugin.Version, StringComparison.Ordinal))
            {
                Shutdown("plugin or wire version mismatch");
                return;
            }

            var localMessageCatalog = GetFrozenMessageCatalog();
            if (!TryValidateMessageCatalog(welcome.MessageCatalog, localMessageCatalog,
                out var messageCatalogReason))
            {
                CoopPlugin.Log.LogWarning("message catalog handshake rejected from host: "
                    + messageCatalogReason);
                Shutdown("message catalog mismatch: " + messageCatalogReason);
                return;
            }
            var localMessageReliability = GetFrozenMessageReliability(localMessageCatalog);
            if (!TryValidateMessageReliability(welcome.MessageReliability, localMessageReliability,
                localMessageCatalog.Count, out var messageReliabilityReason))
            {
                CoopPlugin.Log.LogWarning("message reliability handshake rejected from host: "
                    + messageReliabilityReason);
                Shutdown("message reliability mismatch: " + messageReliabilityReason);
                return;
            }

            var localExtendedInformationCatalog = GetFrozenExtendedInformationCatalog();
            if (!TryValidateCatalog(welcome.ExtendedInformationCatalog,
                localExtendedInformationCatalog, "extended-information",
                out var extendedInformationReason))
            {
                CoopPlugin.Log.LogWarning("extended-information catalog handshake rejected from "
                    + "host: " + extendedInformationReason);
                Shutdown("extended-information catalog mismatch: " + extendedInformationReason);
                return;
            }
            Net.ActivateMessageIds(connection);

            var hostIdentityValid = CatalogHandshake.IsIdentityUsable(welcome.HostEnumIdentity);
            var cardBlob = CatalogHandshake.ReadBlob(welcome.HostCardsBlob);
            if (!hostIdentityValid || !cardBlob.IsValid)
            {
                var invalid = hostIdentityValid
                    ? "host CardsBlob: " + cardBlob.FailureReason
                    : "host enum identity is invalid or empty";
                CoopPlugin.Log.LogError("catalog handshake received an invalid host blob: " + invalid);
                Shutdown("host sent an invalid custom-card catalog");
                return;
            }
            CatalogIdMap.Build(welcome.HostEnumIdentity);
            _localConnectionId = welcome.SelfId;
            // Record what the host advertised, so the connected panel can name the build joined.
            _hostVersion = welcome.Version ?? "";
            _hostGameVersion = welcome.GameVersion ?? "";
            _hostUnityVersion = welcome.UnityVersion ?? "";
            var hostName = ResolvePeerName(welcome.SteamId, welcome.HostName);
            PresenceApi.SetPeerName(1, hostName);
            PresenceApi.ApplyName(ConnectionFor(1));
            if (!SaveTransferApi.TryBeginClientTransfer(welcome.SaveLength,
                welcome.BundleLength, welcome.SidecarsComplete, welcome.SidecarWarning))
            {
                Shutdown("invalid world transfer authorization");
                return;
            }
            if (!connection.TryTransition(ConnectionState.Transferring))
            {
                Shutdown("invalid handshake phase");
                return;
            }

            try
            {
                _runtime?.ClientJoined(connection);
            }
            catch (Exception error)
            {
                // Feature callbacks are isolated from the control transition.
                CoopPlugin.Log.LogError("Feature ClientJoined callbacks failed: " + error);
            }

            SetStatus("Downloading " + (PresenceApi.PeerName(1) ?? "host") + "'s shop...");
        }

        [MessageHandler]
        private void HandleBye(MessageContext context, ByeMessage message)
        {
            var connection = context?.Connection;
            if (!ExpectControlState(connection, "Bye", ConnectionState.Handshaking,
                ConnectionState.Transferring, ConnectionState.FullyJoined))
            {
                return;
            }

            var reason = string.IsNullOrEmpty(message?.Reason)
                ? "the host ended the session" : message.Reason;
            if (Role == CoopRole.Client)
            {
                ErrorLine = reason;
                Shutdown("rejected: " + reason);
            }
            else
            {
                Net?.Kick(connection);
            }
        }

        private static readonly HashSet<Type> WarnedNonActionMessages = new();

        /// <summary>Submits a client ActionMessage through the action ledger. A message that
        /// overrides neither OnApply nor OnUndo is still a plain message: warn once (a public
        /// integration can easily misread the optional-override contract) and let it send
        /// untracked instead of throwing on the gameplay path.</summary>
        private bool TrySubmitClientAction(INetMessage message)
        {
            if (Role != CoopRole.Client || !ActionLedger.IsActive
                || message is not ActionMessage action || action.ActionId != Guid.Empty)
            {
                return false;
            }
            if (ActionLedger.IsAction(action))
            {
                // The action id is allocated and recorded before the message leaves, so a plain
                // module only has to send the command.
                ActionLedger.Submit(action);
                return true;
            }
            if (WarnedNonActionMessages.Add(action.GetType()))
            {
                CoopPlugin.Log.LogWarning("ActionMessage "
                    + action.GetType().FullName + " overrides neither OnApply nor OnUndo; sending "
                    + "it as an ordinary message (no action id, no rollback).");
            }
            return false;
        }

        private void Send(int connectionId, INetMessage message)
        {
            if (TrySubmitClientAction(message))
            {
                return;
            }

            var connection = ConnectionFor(connectionId);
            if (connection == null || Net == null)
            {
                CoopPlugin.Log.LogError("application send failed: no live connection "
                    + connectionId + " for message " + message?.GetType().FullName);
                throw new InvalidOperationException("application send has no live connection "
                    + connectionId + " for message " + message?.GetType().FullName);
            }

            Net.Send(connection, message);
        }

        private void Broadcast(INetMessage message)
        {
            if (TrySubmitClientAction(message))
            {
                return;
            }

            if (Net == null)
            {
                CoopPlugin.Log.LogError("application broadcast failed: no live transport for message "
                    + message?.GetType().FullName);
                throw new InvalidOperationException("application broadcast has no live transport for message "
                    + message?.GetType().FullName);
            }
            Net.Broadcast(message);
        }

        /// <summary>Fans a host message out to every ready peer except the originating one.
        /// Routed through the internal relay seam so it shares Broadcast's recipient admission
        /// instead of a per-connection send that would disconnect a joining peer.</summary>
        private void Relay(int exceptConnectionId, INetMessage message)
        {
            if (Net == null)
            {
                CoopPlugin.Log.LogError("application relay failed: no live transport for message "
                    + message?.GetType().FullName);
                throw new InvalidOperationException("application relay has no live transport for message "
                    + message?.GetType().FullName);
            }
            if (Net is not ICoopRelayTransport relay)
            {
                throw new InvalidOperationException("the live transport has no relay seam for message "
                    + message?.GetType().FullName);
            }
            relay.Relay(message, exceptConnectionId);
        }

        private PeerConnection ConnectionFor(int id)
        {
            if (Net == null)
            {
                return null;
            }
            foreach (var connection in Net.Connections)
            {
                if (connection.Id == id)
                {
                    return connection;
                }
            }
            return null;
        }

        private IReadOnlyList<int> ConnectionIds()
        {
            return Net == null ? Array.Empty<int>() : Net.Connections.Select(c => c.Id).ToArray();
        }

        private IReadOnlyList<PeerConnection> Connections()
            => Net == null ? Array.Empty<PeerConnection>() : Net.Connections;

        private void RejectConn(PeerConnection connection, string reason, bool retryable = false)
        {
            if (connection == null)
            {
                return;
            }
            CoopPlugin.Log.LogWarning("rejected connection " + connection.Id + ": " + reason);
            Net?.GracefulDisconnect(connection,
                new DisconnectInfo(reason, false, "rejected", retryable, ConnectionState.Handshaking));
        }

        private void Shutdown(string reason, DisconnectInfo detail = null)
        {
            if (_shutdownCompleted)
            {
                return;
            }
            _shutdownCompleted = true;
            Interlocked.Increment(ref _sessionGeneration);
            _dispatcher.InvalidateEpoch();

            if (Net != null)
            {
                foreach (var connection in Net.Connections)
                {
                    Net.GracefulDisconnect(connection, detail
                        ?? new DisconnectInfo(reason ?? "session ended", false, "shutdown", true));
                }
                Net.Stop();
                while (Net.TryDequeueDisconnect(out var ended))
                {
                    _runtime?.ClientDisconnected(ended.Connection, ended.Disconnect);
                }
            }

            try
            {
                DeactivateLiveModuleHooks();
            }
            finally
            {
                try
                {
                    // SessionInput and debug hooks are persistent by design, so the live runtime
                    // shutdown above cannot deliver their session-stop event. BeginSession sent the
                    // matching start; send the stop here before Role is reset, while retaining the
                    // process-lifetime runtime. The registry is idempotent, so the later
                    // runtime.Shutdown at plugin unload does not fire it a second time.
                    _persistentRuntime?.SessionStopped();
                }
                finally
                {
                    TearDownTransport();
                    _liveRuntimeContext = null;
                }
            }

            SaveTransferApi.ClearBorrowedWorldIfSafe(InGameLevel());
            SaveTransferRuntimeReset();
            PresenceApi.ClearPeerNames();
            _peerBuilds.Clear();
            HudApi.Clear();
            CatalogIdMap.Clear();
            ExtendedInformationCatalog.EndSession();
            ExtendedInformationModule.ResetAll();
            WorldHostBehaviour.ResetCardState();
            WorldClientBehaviour.ResetCardState();
            SceneRef.ClearAll();
            _dispatchBuffer.Clear();
            _dispatchRejectedConnections.Clear();
            _deferredInviteBackend = null;
            _deferredInviteValues = null;
            _localConnectionId = -1;
            _sessionInGame = false;
            HostPassword = "";
            _joinPassword = "";
            _hostVersion = "";
            _hostGameVersion = "";
            _hostUnityVersion = "";
            // runInBackground is deliberately NOT reset here: UpdateRunInBackground owns it now,
            // and a load that is still in progress when the session ends (a world reload) must keep
            // the game running until the loading screen is gone.
            Role = CoopRole.None;
            StateChanged?.Invoke();
            SetStatus(reason == null ? "Not connected" : "Not connected (" + reason + ")");
        }

        private static void SaveTransferRuntimeReset()
        {
            SaveTransferRuntime.ResetForShutdown();
        }

        /// <summary>
        /// Keeps the game running while its window is unfocused, for exactly two reasons:
        ///
        ///  - A co-op session is live (<see cref="Role"/> != None): a host waiting on its address
        ///    build or its lobby, and a client still joining, are exactly the moments the player
        ///    has switched away, and pausing there stalls the very start they are waiting on.
        ///  - The game's own loading screen is up and NO session is live: the game never lowers its
        ///    run-in-background default at runtime (CGameManager.changeFramerate is dead code that
        ///    is never started), so an unfocused window would otherwise pause the player and stall
        ///    the scene or save load the player is waiting on. This is skipped while a session is
        ///    live only because the first reason already holds the value true.
        ///
        /// Otherwise the idle behavior applies and the window pauses when unfocused - what co-op
        /// has always done outside a session. The value is RECOMPUTED, not captured and restored:
        /// the engine default is whatever Unity's Player Settings says (the game never changes it),
        /// so capturing "the value before the load" would faithfully restore that default and pin
        /// the game to running in the background even when idle.
        /// </summary>
        private void UpdateRunInBackground()
        {
            // The game sets this true only once the loading screen has fully faded out, and back
            // false on the next OpenScreen. It is the ground truth for "the load is over", used to
            // clear the start flag in case the finish event landed on a manager the scene change
            // replaced and never reached us.
            if (_gameLoading && GameInstance.m_HasFinishHideLoadingScreen)
            {
                _gameLoading = false;
                ProcessDeferredInvite();
            }

            Application.runInBackground = Role != CoopRole.None || _gameLoading;
        }

        private void Update()
        {
            Util.FrameCounter.Tick();
            // First frame: every BepInEx plugin (including dependents that load after us) now has
            // an instance, so the dependency-driven external integration scan is complete.
            if (!_externalModsDiscovered)
            {
                _externalModsDiscovered = true;
                ExternalCoopMods.Instance.EnsureDiscovered();
                _persistentRuntime?.IncludeExternalPersistentBehaviours();
                ExternalCoopMods.Instance.NotifyPersistentScanComplete();
            }
            // Attach to every backend's events as soon as the backend is offered, so an out-of-band
            // join request (a Steam invite) is heard even before the player has started a session.
            EnsureBackendsWired();
            Util.PerfProbe.BeginFrame();
            Util.PerfProbe.FlushThreadMetrics();
            using (Util.PerfProbe.Sample("core.dispatcher"))
            {
                _dispatcher.Drain();
            }
            using (Util.PerfProbe.Sample("core.auto-tick"))
            {
                AutoTick(Time.deltaTime);
            }

            CheckJoinAttempts();

            UI.CoopScreen.Tick();
            UI.CoopHud.Tick();
            SessionInputRuntime.SetWindowModal(UI.CoopScreen.IsOpen && InGameplayScene());
            if (Role != CoopRole.None && Input.GetKeyDown(CoopPlugin.EmoteKey.Value)
                && !NativeTextInputFocused())
            {
                SendEmote();
            }

            if (SaveTransferApi.GuestBorrowedWorld && Role == CoopRole.None && !InGameLevel())
            {
                SaveTransferApi.ClearBorrowedWorldIfSafe(false);
            }
            UpdateRunInBackground();

            if (Net == null)
            {
                return;
            }
            if (!_sessionInGame && InGameLevel())
            {
                _sessionInGame = true;
                CoopPlugin.Log.LogInfo("co-op session world gate opened");
            }
            using (Util.PerfProbe.Sample("core.disconnect-drain"))
            {
                DrainDisconnects();
            }
            if (SaveTransferApi.PreloadHold)
            {
                return;
            }
            // Decode and dispatch one message at a time. Entity-bearing DTO converters resolve
            // live scene objects during decode, so a spawn handler must run before the next
            // message that references the spawned object is decoded.
            using (Util.PerfProbe.Sample("core.pump-incoming"))
            {
                for (var i = 0; i < DispatchBudget && Net != null; i++)
                {
                    if (_dispatchBuffer.Count == 0)
                    {
                        AdmitIncoming(1);
                    }
                    if (_dispatchBuffer.Count == 0)
                    {
                        break;
                    }

                    DispatchIncoming(1);
                }
            }
            if (Net == null)
            {
                return;
            }

        }

        private void DrainDisconnects()
        {
            var drained = 0;
            while (drained++ < MaxDisconnectResultsPerFrame
                && Net != null && Net.TryDequeueDisconnect(out var disconnected))
            {
                var id = disconnected.Connection.Id;
                _runtime?.ClientDisconnected(disconnected.Connection, disconnected.Disconnect);
                PresenceApi.RemovePeer(id);
                _peerBuilds.Remove(id);
                if (Role == CoopRole.Client)
                {
                    var detail = disconnected.Disconnect;
                    RememberDisconnect(detail);
                    ErrorLine = detail == null
                        ? "Lost connection to the host."
                        : "Host disconnected (" + detail.Code + "): " + detail.Reason;
                    CoopPlugin.Log.LogInfo("host disconnected: " + (detail?.Code ?? "?")
                        + " / " + (detail?.Reason ?? "no detail"));
                    Shutdown(detail?.Reason ?? "host connection lost", detail);
                    return;
                }

                // The host keeps hosting; forget the departed peer so the cross-build furniture
                // gate cannot keep blocking local purchases against a peer that is gone.
                CatalogIdMap.ClearPeerIdentity();
                CoopPlugin.Log.LogInfo("peer " + id + " disconnected: "
                    + (disconnected.Disconnect?.Code ?? "?") + " / "
                    + (disconnected.Disconnect?.Reason ?? "no detail"));
                SetStatus(Net.ConnectionCount == 0
                    ? "Hosting - waiting for a player..."
                    : "Hosting - " + Net.ConnectionCount + " player(s)");
            }
        }

        private bool TryDequeueNext(out InMsg message)
        {
            if (Net is IWireFrameTransport wire && wire.TryDequeueWireFrame(out var frame))
            {
                if (!Msg.TryDecodeFrame(frame, out message))
                {
                    Net.GracefulDisconnect(frame.Connection, new DisconnectInfo(
                        "invalid application frame", false, "protocol_error", false,
                        frame.Connection?.State ?? ConnectionState.Disconnected));
                    return false;
                }

                return true;
            }

            return Net.TryDequeueIncoming(out message);
        }

        private void AdmitIncoming(int max)
        {
            var admitted = 0;
            while (admitted++ < max
                && Net != null && TryDequeueNext(out var message))
            {
                if (message.Connection != null
                    && _dispatchRejectedConnections.Contains(message.Connection.Id))
                {
                    continue;
                }

                if (_dispatchBuffer.Count < DispatchBacklogCap)
                {
                    _dispatchBuffer.Add(message);
                    continue;
                }

                // Only explicitly transient traffic may be evicted at this boundary. A reliable
                // frame is already decoded and has been admitted by the transport; dropping it
                // here would make the peer's state diverge without any recovery signal.
                var drop = FindBufferedTransient();
                if (drop >= 0)
                {
                    var dropped = _dispatchBuffer[drop];
                    _dispatchBuffer.RemoveAt(drop);
                    LogDroppedTransient(dropped);
                    _dispatchBuffer.Add(message);
                    continue;
                }

                if (IsReliable(message))
                {
                    RejectDispatchOverload(message);
                }
                else
                {
                    LogDroppedTransient(message);
                }
            }
            if (_dispatchBuffer.Count < DispatchBacklogCap / 2)
            {
                _dispatchBacklogWarned = false;
            }
            Util.PerfProbe.RecordQueueDepth("core.dispatch", _dispatchBuffer.Count, DispatchBacklogCap);
        }

        private void DispatchIncoming(int max)
        {
            var consumed = 0;
            var units = 0;
            var dispatched = 0;
            for (var i = 0; i < _dispatchBuffer.Count; i++)
            {
                if (dispatched >= max)
                {
                    break;
                }

                var message = _dispatchBuffer[i];
                if (message.Message == null)
                {
                    consumed = i + 1;
                    continue;
                }
                var cost = DispatchCost(message);
                if (dispatched > 0 && units + cost > DispatchBudget)
                {
                    break;
                }
                dispatched++;
                units += cost;
                try
                {
                    Dispatch(message);
                }
                catch (Exception error)
                {
                    if (IsReliable(message))
                    {
                        RequestReliableDispatchRecovery(message, error);
                        consumed = i + 1;
                        break;
                    }

                    CoopPlugin.Log.LogWarning("transient dispatch conn=" + message.Connection?.Id
                        + " type=" + message.MessageType?.FullName + " failed; dropping message: "
                        + error);
                }
                consumed = i + 1;
                if (Net == null)
                {
                    break;
                }
            }
            if (consumed >= _dispatchBuffer.Count)
            {
                _dispatchBuffer.Clear();
            }
            else if (consumed > 0)
            {
                _dispatchBuffer.RemoveRange(0, consumed);
            }
            Util.PerfProbe.RecordQueueDepth("core.dispatch", _dispatchBuffer.Count, DispatchBacklogCap);
        }

        private bool Dispatch(InMsg message)
        {
            if (message.Connection == null || Net == null
                || _dispatchRejectedConnections.Contains(message.Connection.Id)
                || !IsActiveConnection(message.Connection))
            {
                return false;
            }
            var context = new MessageContext
            {
                Connection = message.Connection,
                InGame = InGameLevel(),
                WorkCost = DispatchCost(message),
                Transport = Net,
            };

            // The router owns the pre-authentication control path as well as the authenticated
            // application path. A named frame is allowed before compact-id activation only when
            // its core-owned handler and the connection phase both authorize it.
            var outcome = _messageRouter.DispatchCore(context, message.Message);
            if (outcome == DispatchOutcome.Handled)
            {
                return true;
            }
            // A pre-activation frame with no handler or an unauthorized one is a peer violation.
            // A handler that RAN and threw is not: it is our own bug, and the router has already
            // discarded the message and kept the session, so it must not be reported as a peer
            // protocol violation and disconnected.
            if (outcome == DispatchOutcome.Rejected && message.ReceivedBeforeMessageIdActivation)
            {
                Net.GracefulDisconnect(message.Connection, new DisconnectInfo(
                    "non-handshake message received before catalog activation", false,
                    "protocol_violation", false, message.Connection.State));
            }
            return false;
        }

        private static int DispatchCost(InMsg message)
        {
            if (message.Message is CardDeltaBatchMessage batch)
            {
                var count = batch.Deltas?.Count ?? 0;
                return Math.Max(1, Math.Min(count, WorldCardInteraction.CardDeltaBatchMax));
            }
            if (message.Message is Modules.Pricing.PricingDeltaBatchMessage pricing)
            {
                var count = (pricing.Items?.Count ?? 0) + (pricing.Cards?.Count ?? 0);
                return Math.Max(1, Math.Min(count,
                    Modules.Pricing.PricingHostBehaviour.PricingDeltaBatchMax));
            }
            return 1;
        }

        private int FindBufferedTransient()
        {
            for (var i = 0; i < _dispatchBuffer.Count; i++)
            {
                if (IsTransient(_dispatchBuffer[i]))
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool IsReliable(InMsg message)
        {
            if (message.Message == null)
            {
                return true;
            }

            if (!MessageRegistry.CurrentSnapshot.TryGet(message.MessageType, out var descriptor))
            {
                // Decoded messages should always have a descriptor. Treat a broken registry as
                // reliable rather than allowing an unknown frame to bypass overload protection.
                return true;
            }

            return descriptor.Reliability == Reliability.Reliable;
        }

        private static bool IsTransient(InMsg message)
        {
            return message.Message != null
                && MessageRegistry.CurrentSnapshot.TryGet(message.MessageType, out var descriptor)
                && descriptor.Reliability == Reliability.Transient;
        }

        private void RejectDispatchOverload(InMsg message)
        {
            var connection = message.Connection;
            var typeName = message.MessageType?.FullName ?? "<unknown>";
            if (connection == null)
            {
                CoopPlugin.Log.LogError("dispatch overload rejected reliable " + typeName
                    + " without a peer connection");
                return;
            }

            if (!_dispatchRejectedConnections.Add(connection.Id))
            {
                return;
            }

            var reason = "dispatch overload: reliable backlog exhausted; reconnect to recover";
            CoopPlugin.Log.LogError("dispatch overload on connection " + connection.Id
                + ": rejecting reliable " + typeName + " (capacity " + DispatchBacklogCap + ")");
            Net?.GracefulDisconnect(connection,
                new DisconnectInfo(reason, false, "dispatch_overload", true, connection.State));
        }

        private void LogDroppedTransient(InMsg message)
        {
            if (_dispatchBacklogWarned)
            {
                return;
            }

            _dispatchBacklogWarned = true;
            CoopPlugin.Log.LogWarning("dispatch backlog cap reached; dropping transient "
                + (message.MessageType?.FullName ?? "<unknown>") + " from connection "
                + message.Connection?.Id);
        }

        private void RequestReliableDispatchRecovery(InMsg message, Exception error)
        {
            MarkDispatchConnectionRejected(message.Connection);
            CoopPlugin.Log.LogError("reliable dispatch conn=" + message.Connection?.Id + " type="
                + message.MessageType?.FullName + " failed; requesting session recovery: " + error);
            if (message.Connection != null)
            {
                Net?.GracefulDisconnect(message.Connection,
                    new DisconnectInfo("reliable dispatch failed; session recovery required", false,
                        "dispatch_failed", true, message.Connection.State));
            }
        }

        private void MarkDispatchConnectionRejected(PeerConnection connection)
        {
            if (connection != null)
            {
                _dispatchRejectedConnections.Add(connection.Id);
            }
        }

        private bool IsActiveConnection(PeerConnection connection)
        {
            foreach (var active in Net?.Connections ?? Array.Empty<PeerConnection>())
            {
                if (ReferenceEquals(active, connection))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool ExpectControlState(PeerConnection connection, string control,
            params ConnectionState[] expected)
        {
            if (connection == null || expected.All(state => connection.State != state))
            {
                CoopPlugin.Log.LogWarning("Ignoring late/duplicate " + control + " from connection "
                    + connection?.Id + " in phase " + connection?.State);
                return false;
            }
            return true;
        }

        private static string ResolvePeerName(ulong steamId, string wireName)
        {
            return string.IsNullOrWhiteSpace(wireName) ? "Player" : wireName;
        }

        private void SetStatus(string status)
        {
            StatusLine = status ?? "";
            HudApi.SetStatusLine(StatusLine, 8f);
        }

        /// <summary>Publishes a failure/notice through the native co-op status/error surface.
        /// The line is also logged so support still has it.</summary>
        private void ShowError(string header, string detail)
        {
            var text = string.IsNullOrEmpty(detail) ? header : header + ": " + detail;
            ErrorLine = text;
            SetStatus(header);
            CoopPlugin.Log.LogWarning("coop: " + text);
        }

        /// <summary>Raised by the router on the main thread when a reliable handler threw and its
        /// message was discarded. The session is still up, so the player is told rather than being
        /// disconnected: the status/error surface names the message and the exception, and a
        /// repeat replaces the same line instead of stacking popups.</summary>
        private void OnHandlerFailed(HandlerFailure failure)
        {
            if (failure == null)
            {
                return;
            }

            var error = failure.Error;
            var detail = (failure.WireName ?? "<unknown>") + " from connection "
                + (failure.Context?.ConnectionId ?? 0) + ": "
                + (error == null ? "unknown error" : error.GetType().Name + ": " + error.Message);
            ShowError("CO-OP ERROR",
                "A co-op message could not be processed and was skipped. The session continues. "
                + detail);
        }

        private void RememberDisconnect(DisconnectInfo detail)
        {
            if (detail == null)
            {
                return;
            }
            LastDisconnectCode = detail.Code;
            LastDisconnectReason = detail.Reason;
            LastDisconnectRetryable = detail.Retryable;
        }

        private void ClearDisconnectMetadata()
        {
            LastDisconnectCode = "";
            LastDisconnectReason = "";
            LastDisconnectRetryable = false;
        }

        private void OnLocalPackOpened(CEventPlayer_OnOpenCardPack eventData)
        {
            if (Role != CoopRole.None && Net?.ConnectionCount > 0)
            {
                PresenceApi.SendActivity((EItemType)eventData.m_PackIndex);
            }
        }

        private void OnGameDataFinishLoaded(CEventPlayer_GameDataFinishLoaded _)
        {
            _worldReady = true;
            CoopPlugin.Log.LogInfo("co-op: shop data finished loading; hosting is now admitted");
        }

        /// <summary>Called by the <see cref="Util.GameLoadingGuard"/> hook on the game's
        /// LoadingScreen.OpenScreen. Marks the start of a scene or save load; the end arrives as
        /// CEventPlayer_FinishHideLoadingScreen (see <see cref="OnGameLoadingFinished"/>).</summary>
        internal static void NotifyGameLoadingStarted()
        {
            var core = Instance;
            if (core != null)
            {
                core._gameLoading = true;
            }
        }

        private void OnGameLoadingFinished(CEventPlayer_FinishHideLoadingScreen _)
        {
            _gameLoading = false;
            ProcessDeferredInvite();
        }

        /// <summary>Applies the invite held across a load, once the destination is known. At the
        /// Title screen this joins; in a shop the normal admission guard declines it.</summary>
        private void ProcessDeferredInvite()
        {
            if (_deferredInviteBackend == null || _gameLoading)
            {
                return;
            }

            var backend = _deferredInviteBackend;
            var values = _deferredInviteValues;
            _deferredInviteBackend = null;
            _deferredInviteValues = null;
            HandleInviteOnMainThread(backend, values);
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode == LoadSceneMode.Additive)
            {
                // Only the character-prefab preload loads a scene additively. It is not the
                // player's world: it must not reset world/identity state, and it must never make
                // an active session look like the player reloaded their shop.
                return;
            }

            _worldReady = false;
            GameObjectIdentityApi.Reset();
            // The game can rebuild its event manager between scenes; make sure the co-op
            // subscriptions follow the instance that is dispatching.
            RegisterEventListeners();
            SceneRef.NotifySceneLoaded(scene);

            if (_gameLoading && scene.name == "Title"
                && LoadingScreen.m_Instance != null
                && !LoadingScreen.m_Instance.gameObject.activeInHierarchy)
            {
                // ShelfManager.CheckLoadCorrect deactivates the LoadingScreen before it can run
                // FinishLoading's fade-out, so neither the finish event nor the static flag ever
                // arrives. The Title scene is the end of that recovery path; clear the guard.
                CoopPlugin.Log.LogWarning(
                    "coop: loading screen was torn down before it finished; clearing the load guard");
                _gameLoading = false;
                ProcessDeferredInvite();
            }

            if (scene.name == "Title" && Role == CoopRole.Client && Net != null)
            {
                Shutdown("left the session");
                return;
            }
            if (scene.name != "Title" && Role != CoopRole.None && Net != null
                && !SaveTransferApi.ClientReloading)
            {
                Shutdown("left the session (world reloaded)");
            }
        }

        private bool TryAdmitHostStart(out string error)
        {
            var scene = SceneManager.GetActiveScene();
            var manager = GameManager();
            var gameLevel = manager != null && manager.m_IsGameLevel;
            var ready = _worldReady;
            var borrowed = SaveTransferApi.GuestBorrowedWorld;
            if (gameLevel && ready && !borrowed)
            {
                error = null;
                return true;
            }

            var shelf = SceneRef<ShelfManager>.Get();
            error = borrowed
                ? "Leave the joined shop and load your own shop before hosting."
                : !gameLevel
                    ? "Load your shop first, then host."
                    : "Wait for your shop to finish loading, then host.";
            CoopPlugin.Log.LogWarning("host start rejected: scene=" + scene.name
                + " manager=" + (manager == null ? "<null>" : manager.GetType().FullName)
                + " game-level=" + gameLevel + " ready=" + ready + " borrowed=" + borrowed
                + " shelf-data-loaded=" + (shelf != null && shelf.m_FinishLoadingObjectData)
                + " reason=" + error);
            return false;
        }

        /// <summary>
        /// The game manager, preferring the game's own static (assigned in Awake, never
        /// fabricating a placeholder) over the scene-ref cache.
        /// </summary>
        private static CGameManager GameManager()
        {
            var manager = CGameManager.m_Instance;
            return manager != null ? manager : SceneRef<CGameManager>.Get();
        }

        /// <summary>True once a save is loaded (a gameplay level is active). This is what decides
        /// whether the co-op window offers HOST (in a save) or JOIN (at the title screen), replacing
        /// the old manual host/join toggle.
        ///
        /// Deliberately the SAME flag <see cref="TryAdmitHostStart"/> admits a host on: the window
        /// must not offer HOST in a state hosting would then refuse. In the documented case where
        /// m_IsGameLevel stays false after an aborted level load, the shop is live but hosting is
        /// unavailable anyway, so JOIN is the honest half to show - changing THAT is a
        /// host-admission question (InGameplayScene), not a display one.</summary>
        public bool InSave => InGameLevel();

        private bool InGameLevel()
        {
            var manager = GameManager();
            return manager != null && manager.m_IsGameLevel;
        }

        /// <summary>
        /// True while the active scene is a gameplay level rather than the title screen. The
        /// co-op window must own the cursor and block gameplay input wherever a shop is live;
        /// scene identity is the reliable ground truth because CGameManager.m_IsGameLevel stays
        /// false for the rest of the run when the game's level-load handler aborts.
        /// </summary>
        internal static bool InGameplayScene()
        {
            var scene = SceneManager.GetActiveScene();
            return scene.isLoaded && scene.name != "Title";
        }

        /// <summary>Joining replaces the local world with the host's, which is only safe from the
        /// Title screen with no load in flight. Scene identity is the ground truth for a live
        /// shop; _gameLoading covers the Title -> shop window before the new scene is active.</summary>
        private bool CanJoinFromHere() => !InGameplayScene() && !_gameLoading;

        private void AutoTick(float deltaTime)
        {
            if (_autoPhase >= 99 || (_autoHostSlot < 0 && _autoJoinIp == null
                && _autoJoinSteamLobby == 0))
            {
                return;
            }
            _autoTimer += deltaTime;
            if (_autoHostSlot >= 0)
            {
                if (_autoPhase == 0 && _autoTimer > 6f && !InGameLevel())
                {
                    SaveTransferApi.ForceLoadSlot(_autoHostSlot);
                    _autoPhase = 1;
                    _autoTimer = 0f;
                }
                else if (_autoPhase == 1 && InGameLevel() && _worldReady)
                {
                    _autoPhase = 2;
                    _autoTimer = 0f;
                }
                else if (_autoPhase == 2 && _autoTimer > 3f)
                {
                    AutoHost();
                    _autoPhase = 99;
                }
            }
            else if (_autoJoinIp != null && _autoPhase == 0 && _autoTimer > 10f && !InGameLevel())
            {
                AutoJoinIp(_autoJoinIp);
                _autoPhase = 99;
            }
            else if (_autoJoinSteamLobby != 0 && _autoPhase == 0 && _autoTimer > 10f && !InGameLevel())
            {
                AutoJoinSteam(_autoJoinSteamLobby);
                _autoPhase = 99;
            }
        }

        /// <summary>Reads a field's current value straight from the backend, so a value the backend
        /// derived (a password unfolded from a pasted code) is used rather than the raw form.</summary>
        private static string FieldValue(INetworkingBackend backend, ConnectMode mode, FieldId field)
        {
            IReadOnlyList<BackendField> fields;
            try
            {
                fields = backend.GetFields(mode);
            }
            catch (Exception error)
            {
                CoopLog.Swallow(error);
                return "";
            }

            for (var i = 0; i < fields.Count; i++)
            {
                if (fields[i].Id == field)
                {
                    return fields[i].Value ?? "";
                }
            }

            return "";
        }

        private INetworkingBackend BackendById(string id)
        {
            foreach (var backend in Backends)
            {
                if (backend.Id == id)
                {
                    return backend;
                }
            }

            return null;
        }

        private void AutoHost()
        {
            var ip = BackendById("ip");
            if (ip != null)
            {
                ConnectBackend(ip, ConnectMode.Host, null);
            }
        }

        private void AutoJoinIp(string address)
        {
            var ip = BackendById("ip");
            if (ip != null)
            {
                ConnectBackend(ip, ConnectMode.Join, Values(FieldId.IpAddress, address));
            }
        }

        private void AutoJoinSteam(ulong lobby)
        {
            var steam = BackendById("steam");
            if (steam != null)
            {
                ConnectBackend(steam, ConnectMode.Join,
                    Values(FieldId.Lobby, lobby.ToString(CultureInfo.InvariantCulture)));
            }
        }

        private static FieldValues Values(FieldId field, string value)
        {
            var values = new FieldValues();
            values[field] = value;
            return values;
        }

        /// <summary>Tears down every backend transport and the one pump that drove them. Called
        /// from Shutdown; idempotent.</summary>
        private void TearDownTransport()
        {
            List<KeyValuePair<INetworkingBackend, ConnectionId>> connections;
            lock (_sessionLock)
            {
                connections = new List<KeyValuePair<INetworkingBackend, ConnectionId>>(_attempts);
                _attempts.Clear();
                _children.Clear();
                // _wired is deliberately NOT cleared. The backends are process-lifetime singletons
                // and these are process-lifetime subscriptions, so re-subscribing every session
                // would stack duplicate callbacks - the same child added to the group once per
                // session, the same Hello sent once per session, and so on.
            }

            foreach (var pair in connections)
            {
                try
                {
                    pair.Key.Disconnect(pair.Value);
                }
                catch (Exception error) { CoopLog.Swallow(error); }
            }

            try
            {
                _pump?.Stop();
            }
            catch (Exception error) { CoopLog.Swallow(error); }

            try
            {
                _pump?.Dispose();
            }
            catch (Exception error) { CoopLog.Swallow(error); }

            _pump = null;
            _group = null;
            _activeBackend = null;
            Net = null;
        }

        private void OnDestroy()
        {
            UI.CoopMenus.Shutdown();
            UI.CoopHud.Destroy();
            UI.CoopScreen.Destroy();
            _messageRouter.HandlerFailed -= OnHandlerFailed;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            CEventManager.RemoveListener<CEventPlayer_OnOpenCardPack>(OnLocalPackOpened);
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnGameDataFinishLoaded);
            CEventManager.RemoveListener<CEventPlayer_FinishHideLoadingScreen>(OnGameLoadingFinished);
            Shutdown("plugin unloaded");
            try
            {
                _persistentRuntime?.Shutdown();
            }
            catch (Exception error)
            {
                // An external persistent handler must never skip the remainder of teardown.
                CoopPlugin.Log?.LogError("Co-op persistent runtime shutdown failed: " + error);
            }
            if (_persistentRuntime != null)
            {
                Destroy(_persistentRuntime);
                _persistentRuntime = null;
            }
            Util.PerfProbe.Reset();
            _dispatcher.Stop();
            if (ReferenceEquals(Instance, this))
            {
                Instance = null;
            }
        }

        private void OnApplicationQuit() => Shutdown("game closed");
    }
}
