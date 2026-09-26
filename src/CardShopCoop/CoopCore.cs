using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CardShopCoop.Modules.Catalog;
using CardShopCoop.Modules.Hud;
using CardShopCoop.Modules.Presence;
using CardShopCoop.Modules.SaveTransfer;
using CardShopCoop.Modules.SessionInput;
using CardShopCoop.Modules.World;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Net.Messages;
using CardShopCoop.Net.Protocol;
using CardShopCoop.Runtime;
using PeerConnection = CardShopCoop.Net.Connection.PeerConnection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop
{
    public enum CoopRole
    {
        None,
        Host,
        Client
    }

    public enum InviteState
    {
        Off,
        Resolving,
        Ready,
        LanOnly
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
        public static bool WindowBlocksInput => SessionInputRuntime.WindowBlocksInput;
        public static bool IsTearingDown
        {
            get; private set;
        }
        public static bool InSessionWorld => Instance != null && Instance._sessionInGame;

        public string StatusLine = "Not connected";
        public string ErrorLine = "";
        public string EffectivePlayerName => PresenceApi.LocalPlayerName;
        public bool UsingSteamPersona => !string.Equals(EffectivePlayerName,
            CoopPlugin.PlayerName.Value, StringComparison.Ordinal);

        public ICoopTransport Net
        {
            get; private set;
        }
        public ISteamBridge Steam => _steam;
        public bool IsSteamSession
        {
            get; private set;
        }
        public string HostPassword = "";
        public ulong LastFailedLobby;
        public InviteState InviteStatus = InviteState.Off;
        public string InviteCodeText;
        public string InviteReason;
        public int PortForwardState;

        public string LastDisconnectCode { get; private set; } = "";
        public string LastDisconnectReason { get; private set; } = "";
        public bool LastDisconnectRetryable
        {
            get; private set;
        }

        private IConnectionProvider _connectionProvider;
        private ICoopTransport _providerTransportSource;
        private CoopRuntimeContext _liveRuntimeContext;
        private ISteamBridge _steam;
        private CoopRuntimeBehaviour _runtime;
        private PersistentRuntimeBehaviour _persistentRuntime;
        private UI.CoopUI _ui;
        private readonly MessageRouter _messageRouter = new();
        private readonly MainThreadDispatcher _dispatcher = new();
        private bool _shutdownCompleted;
        private bool _externalModsDiscovered;
        private bool _sessionInGame;
        private bool _worldReady;
        private int _localConnectionId = -1;
        private int _sessionGeneration = 1;
        private int _inviteGeneration;
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
        private const int MaxIncomingAdmissionPerFrame = DispatchBudget * 2;
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
        /// public <see cref="Api.CoopApi"/> binding.</summary>
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
            _messageRouter.RegisterCoreAttributedHandlers(this);
            _dispatcher.Start();
            PresenceApi.ConfigureIdentity(() =>
            {
                var persona = _steam == null ? "" : _steam.LocalPersonaName;
                return string.IsNullOrWhiteSpace(persona) ? CoopPlugin.PlayerName.Value : persona;
            });
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

            _ui = new UI.CoopUI();
            _persistentRuntime = gameObject.AddComponent<PersistentRuntimeBehaviour>();
            _persistentRuntime.InitializePersistent(new CoopRuntimeContext(
                _messageRouter, InGameLevel, Broadcast,
                Send, ConnectionIds, PresenceApi.PeerName,
                (text, seconds) => SetStatus(text), () => SaveTransferApi.PreloadHold,
                null));
            SceneManager.sceneLoaded += OnSceneLoaded;

            ReadCommandLine();
            _steam = SteamBridge.TryCreate();
            if (_steam == null && PlatformProbe.SteamworksPresent)
            {
                CoopPlugin.Log.LogInfo("Steamworks assembly present but not functional; LAN and direct IP only.");
            }

            if (_steam != null)
            {
                _steam.Init();
                _steam.OnError = error =>
                {
                    ErrorLine = error ?? "Steam error";
                    CoopPlugin.Log.LogWarning(ErrorLine);
                };
                _steam.OnLobbyLive = lobby =>
                {
                    SetStatus("Hosting via Steam - click 'Invite friend'");
                    CoopPlugin.Log.LogInfo("steam: lobby live " + lobby);
                };
                _steam.OnInviteAccepted = lobby => JoinSteamLobby(lobby);
            }

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

        public void JoinSteamLobby(ulong lobby, string password = "")
        {
            ErrorLine = "";
            ClearDisconnectMetadata();
            if (Role != CoopRole.None)
            {
                ErrorLine = "Already in a session.";
                return;
            }
            if (InGameLevel())
            {
                ErrorLine = "Go to the main menu first, then accept the invite again.";
                return;
            }
            if (_steam == null)
            {
                ErrorLine = "This build has no Steam support - use LAN or direct IP.";
                return;
            }
            if (!_steam.SteamAvailable())
            {
                ErrorLine = "Steam isn't running.";
                return;
            }

            BeginSession(CoopRole.Client, true);
            IsSteamSession = true;
            _joinPassword = password ?? "";
            LastFailedLobby = lobby;
            try
            {
                SetConnectionProvider(new SteamConnectionProvider(_steam));
                ConnectionProvider.Join(lobby.ToString(), 0, _joinPassword);
                SetStatus("Joining Steam lobby...");
            }
            catch (Exception error)
            {
                AbortSessionStart("Could not join: " + error.Message);
            }
        }

        public void StartHostingSteam(bool isPublic, string lobbyName, string password, int maxPlayers)
        {
            ErrorLine = "";
            ClearDisconnectMetadata();
            if (maxPlayers < SteamLobbyLimits.MinPlayers || maxPlayers > SteamLobbyLimits.MaxPlayers)
            {
                ErrorLine = $"Player count must be between {SteamLobbyLimits.MinPlayers} and {SteamLobbyLimits.MaxPlayers}.";
                return;
            }
            if (Role != CoopRole.None)
            {
                ErrorLine = "Already in a session.";
                return;
            }
            if (!TryAdmitHostStart(out var admissionError))
            {
                ErrorLine = admissionError;
                return;
            }
            if (_steam == null)
            {
                ErrorLine = "This build has no Steam support - use LAN instead.";
                return;
            }
            if (!_steam.SteamAvailable())
            {
                ErrorLine = "Steam isn't running - use LAN instead.";
                return;
            }

            BeginSession(CoopRole.Host, true);
            HostPassword = password ?? "";
            try
            {
                SetConnectionProvider(new SteamConnectionProvider(_steam));
                ConnectionProvider.Host(0, isPublic, lobbyName, HostPassword, maxPlayers);
                SetStatus("Creating Steam lobby...");
            }
            catch (Exception error)
            {
                AbortSessionStart("Could not host: " + error.Message);
            }
        }

        public void OpenSteamInvite() => _steam?.OpenInviteDialog();

        public void StartHosting()
        {
            ErrorLine = "";
            ClearDisconnectMetadata();
            if (Role != CoopRole.None)
            {
                ErrorLine = "Already in a session.";
                return;
            }
            if (!TryAdmitHostStart(out var admissionError))
            {
                ErrorLine = admissionError;
                return;
            }

            BeginSession(CoopRole.Host, false);
            try
            {
                SetConnectionProvider(new LanConnectionProvider());
                ConnectionProvider.Host(CoopPlugin.Port.Value);
                SetStatus("Hosting - waiting for a player...");
                if (CoopPlugin.AutoLanPassword.Value && string.IsNullOrEmpty(HostPassword))
                {
                    HostPassword = GenerateSessionPassword();
                }
                BeginInviteResolve();
            }
            catch (Exception error)
            {
                AbortSessionStart("Could not host: " + error.Message);
            }
        }

        public void JoinHostedGame(string ip) => JoinHostedGame(ip, CoopPlugin.Port.Value, "");

        public void JoinHostedGame(string ip, int joinPort, string password)
        {
            ErrorLine = "";
            ClearDisconnectMetadata();
            if (Role != CoopRole.None)
            {
                ErrorLine = "Already in a session.";
                return;
            }
            if (InGameLevel())
            {
                ErrorLine = "Join from the main menu (Title screen).";
                return;
            }
            ip = (ip ?? "").Trim();
            if (ip.Length == 0)
            {
                ErrorLine = "Enter the host's IP address.";
                return;
            }

            CoopPlugin.LastJoinIP.Value = ip;
            BeginSession(CoopRole.Client, false);
            _joinPassword = password ?? "";
            try
            {
                SetConnectionProvider(new LanConnectionProvider());
                var port = joinPort > 0 && joinPort <= 65535 ? joinPort : CoopPlugin.Port.Value;
                ConnectionProvider.Join(ip, port);
                SetStatus("Connecting to " + ip + "...");
            }
            catch (Exception error)
            {
                AbortSessionStart("Could not connect: " + error.Message);
            }
        }

        public void Disconnect()
        {
            ClearDisconnectMetadata();
            Shutdown("disconnected");
        }

        public void SendEmote() => PresenceApi.SendEmote();

        private void BeginSession(CoopRole role, bool steam)
        {
            _shutdownCompleted = false;
            _dispatcher.Start();
            Interlocked.Increment(ref _sessionGeneration);
            Role = role;
            IsSteamSession = steam;
            _localConnectionId = role == CoopRole.Host ? 0 : -1;
            if (role == CoopRole.Client)
            {
                SaveTransferApi.SetGuestBorrowedWorld(true);
            }
        }

        private IConnectionProvider ConnectionProvider
        {
            get => _connectionProvider;
            set
            {
                if (ReferenceEquals(_connectionProvider, value))
                {
                    OnProviderTransportChanged();
                    return;
                }

                if (_connectionProvider != null)
                {
                    _connectionProvider.TransportChanged -= OnProviderTransportChanged;
                    _connectionProvider.PeerConnected -= OnProviderPeerConnected;
                    _connectionProvider.Failed -= OnProviderFailed;
                }
                _connectionProvider = value;
                if (_connectionProvider != null)
                {
                    _connectionProvider.TransportChanged += OnProviderTransportChanged;
                    _connectionProvider.PeerConnected += OnProviderPeerConnected;
                    _connectionProvider.Connected += OnProviderConnected;
                    _connectionProvider.Failed += OnProviderFailed;
                }
                OnProviderTransportChanged();
            }
        }

        private void OnProviderConnected()
        {
        }

        private void OnProviderFailed(string reason)
        {
            AbortSessionStart(reason ?? "connection failed");
        }

        private void OnProviderTransportChanged()
        {
            var providerTransport = _connectionProvider?.Transport;
            if (ReferenceEquals(providerTransport, _providerTransportSource))
            {
                return;
            }

            _providerTransportSource = providerTransport;
            Net = providerTransport;
            if (Net != null && Role != CoopRole.None && _runtime == null)
            {
                ActivateLiveModuleHooks();
            }
        }

        private void OnProviderPeerConnected(PeerConnection connection)
        {
            if (connection == null || Net == null)
            {
                return;
            }

            if (Role == CoopRole.Host)
            {
                // Feature baselines are admitted only after the named Hello has passed. The
                // FullyJoined callback owns the authoritative post-transfer baseline; nothing
                // may queue application traffic for a still-handshaking peer here.
            }
            else if (Role == CoopRole.Client)
            {
                // The transport raises this from its pump thread, which is the network thread.
                // SendHello reads UnityEngine.Application and does catalog file/reflection work,
                // so it must stay on the Unity thread rather than blocking the network pump.
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
        }

        private void SetConnectionProvider(IConnectionProvider provider)
        {
            ConnectionProvider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        private void ActivateLiveModuleHooks()
        {
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
                        Net?.GracefulDisconnect(connection, info);
                });
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

        private void AbortSessionStart(string reason)
        {
            ErrorLine = reason ?? "session could not start";
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
                SteamId = _steam == null ? 0 : _steam.LocalSteamId,
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

                if (descriptor.Reliability == CardShopCoop.Net.Protocol.Reliability.Transient)
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
        {
            if (peerCatalog == null)
            {
                reason = "message catalog is missing";
                return false;
            }

            if (peerCatalog.Count != expectedCatalog.Count)
            {
                reason = "message catalog count differs (peer " + peerCatalog.Count
                    + ", local " + expectedCatalog.Count + ")";
                return false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < peerCatalog.Count; i++)
            {
                var peerName = peerCatalog[i];
                if (peerName == null)
                {
                    reason = "message catalog contains a null name at index " + i;
                    return false;
                }
                if (!seen.Add(peerName))
                {
                    reason = "message catalog contains a duplicate name at index " + i;
                    return false;
                }
            }

            for (var i = 0; i < expectedCatalog.Count; i++)
            {
                if (!string.Equals(peerCatalog[i], expectedCatalog[i], StringComparison.Ordinal))
                {
                    reason = "message catalog differs at index " + i + " (peer '"
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

        [MessageHandler(typeof(HelloMessage))]
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
                RejectConn(connection, "your mod set differs from the host's - both players need identical mods");
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
            if (!connection.TryTransition(ConnectionState.Transferring))
            {
                CoopPlugin.Log.LogWarning("Ignoring Hello from connection in invalid phase " + connection.State);
                return;
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
                    SteamId = _steam == null ? 0 : _steam.LocalSteamId,
                    SaveLength = offer.SaveLength,
                    BundleLength = offer.BundleLength,
                    SidecarsComplete = offer.SidecarsComplete,
                    SidecarWarning = offer.SidecarWarning,
                    SelfId = connection.Id,
                    HostEnumIdentity = blobs.EnumIdentity,
                    HostCardsBlob = blobs.CardsBlob,
                    MessageCatalog = new List<string>(messageCatalog),
                    MessageReliability = (byte[])messageReliability.Clone(),
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

        [MessageHandler(typeof(WelcomeMessage))]
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
            PresenceApi.SetPeerName(1, ResolvePeerName(welcome.SteamId, welcome.HostName));
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
            SetStatus("Downloading " + (PresenceApi.PeerName(1) ?? "host") + "'s shop...");
        }

        [MessageHandler(typeof(ByeMessage))]
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

        private void Send(int connectionId, INetMessage message)
        {
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
            if (Net == null)
            {
                CoopPlugin.Log.LogError("application broadcast failed: no live transport for message "
                    + message?.GetType().FullName);
                throw new InvalidOperationException("application broadcast has no live transport for message "
                    + message?.GetType().FullName);
            }
            Net.Broadcast(message);
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
            IsTearingDown = true;
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
                while (Net.Disconnects.TryDequeue(out var ended))
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
                    // shutdown above cannot deliver their session-stop event. Do that explicitly
                    // before Role is reset, while retaining the process-lifetime runtime.
                    _persistentRuntime?.SessionStopped();
                }
                finally
                {
                    ConnectionProvider?.Dispose();
                    ConnectionProvider = null;
                    Net = null;
                    _liveRuntimeContext = null;
                }
            }

            SaveTransferApi.ClearBorrowedWorldIfSafe(InGameLevel());
            SaveTransferRuntimeReset();
            PresenceApi.ClearPeerNames();
            HudApi.Clear();
            CatalogIdMap.Clear();
            Modules.World.WorldHostBehaviour.ResetCardState();
            Modules.World.WorldClientBehaviour.ResetCardState();
            Runtime.SceneRef.ClearAll();
            _dispatchBuffer.Clear();
            _dispatchRejectedConnections.Clear();
            _localConnectionId = -1;
            _sessionInGame = false;
            IsSteamSession = false;
            HostPassword = "";
            _joinPassword = "";
            _steam?.Leave();
            Application.runInBackground = false;
            Role = CoopRole.None;
            IsTearingDown = false;
            SetStatus(reason == null ? "Not connected" : "Not connected (" + reason + ")");
        }

        private static void SaveTransferRuntimeReset()
        {
            SaveTransferRuntime.ResetForShutdown();
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
            }
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

            if (Input.GetKeyDown(CoopPlugin.UiToggleKey.Value))
            {
                _ui.Visible = !_ui.Visible;
            }
            SessionInputRuntime.SetWindowModal(_ui.Visible && InGameplayScene());
            if (Role != CoopRole.None && Input.GetKeyDown(CoopPlugin.EmoteKey.Value)
                && !UI.CoopUI.TextFieldFocused && !NativeTextInputFocused())
            {
                SendEmote();
            }

            if (SaveTransferApi.GuestBorrowedWorld && Role == CoopRole.None && !InGameLevel())
            {
                SaveTransferApi.ClearBorrowedWorldIfSafe(false);
            }
            if (Net == null)
            {
                return;
            }
            if (!_sessionInGame && InGameLevel())
            {
                _sessionInGame = true;
                CoopPlugin.Log.LogInfo("co-op session world gate opened");
            }

            Application.runInBackground = true;
            using (Util.PerfProbe.Sample("core.disconnect-drain"))
            {
                DrainDisconnects();
            }
            if (SaveTransferApi.PreloadHold)
            {
                return;
            }
            using (Util.PerfProbe.Sample("core.admit-incoming"))
            {
                AdmitIncoming();
            }
            using (Util.PerfProbe.Sample("core.dispatch-incoming"))
            {
                DispatchIncoming();
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
                && Net != null && Net.Disconnects.TryDequeue(out var disconnected))
            {
                var id = disconnected.Connection.Id;
                _runtime?.ClientDisconnected(disconnected.Connection, disconnected.Disconnect);
                PresenceApi.RemovePeer(id);
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
                CoopPlugin.Log.LogInfo("peer " + id + " disconnected: "
                    + (disconnected.Disconnect?.Code ?? "?") + " / "
                    + (disconnected.Disconnect?.Reason ?? "no detail"));
                SetStatus(Net.ConnectionCount == 0
                    ? "Hosting - waiting for a player..."
                    : "Hosting - " + Net.ConnectionCount + " player(s)");
            }
        }

        private void AdmitIncoming()
        {
            var admitted = 0;
            while (admitted++ < MaxIncomingAdmissionPerFrame
                && Net != null && Net.TryDequeueIncoming(out var message))
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

        private void DispatchIncoming()
        {
            var consumed = 0;
            var units = 0;
            var dispatched = 0;
            for (var i = 0; i < _dispatchBuffer.Count; i++)
            {
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
                catch (ReliableMessageHandlerException error)
                {
                    MarkDispatchConnectionRejected(message.Connection);
                    CoopPlugin.Log.LogError("reliable dispatch failed for connection "
                        + message.Connection?.Id + " type=" + message.MessageType?.FullName
                        + "; the peer was disconnected and the remaining messages were held: "
                        + error.InnerException);
                    consumed = i + 1;
                    break;
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
            if (_messageRouter.DispatchCore(context, message.Message))
            {
                return true;
            }
            if (message.ReceivedBeforeMessageIdActivation)
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
                return Math.Max(1, Math.Min(count, Modules.World.WorldCardInteraction.CardDeltaBatchMax));
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

        private string ResolvePeerName(ulong steamId, string wireName)
        {
            var fallback = string.IsNullOrWhiteSpace(wireName) ? "Player" : wireName;
            if (steamId == 0 || _steam == null)
            {
                return fallback;
            }
            var nickname = _steam.FriendNickname(steamId);
            return string.IsNullOrWhiteSpace(nickname) ? fallback : nickname;
        }

        private void SetStatus(string status)
        {
            StatusLine = status ?? "";
            HudApi.SetStatusLine(StatusLine, 8f);
        }

        private void Guarded(string stage, Action action)
        {
            try
            {
                Util.PerfProbe.Measure(stage, action);
            }
            catch (Exception error)
            {
                Runtime.ModuleGuard.Log(stage, error);
            }
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
            LastFailedLobby = 0;
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

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _worldReady = false;
            // The game can rebuild its event manager between scenes; make sure the co-op
            // subscriptions follow the instance that is dispatching.
            RegisterEventListeners();
            Runtime.SceneRef.NotifySceneLoaded(scene);
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
        private static bool InGameplayScene()
        {
            var scene = SceneManager.GetActiveScene();
            return scene.isLoaded && scene.name != "Title";
        }

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
                    StartHosting();
                    _autoPhase = 99;
                }
            }
            else if (_autoJoinIp != null && _autoPhase == 0 && _autoTimer > 10f && !InGameLevel())
            {
                JoinHostedGame(_autoJoinIp);
                _autoPhase = 99;
            }
            else if (_autoJoinSteamLobby != 0 && _steam != null && _autoPhase == 0
                && _autoTimer > 10f && !InGameLevel())
            {
                JoinSteamLobby(_autoJoinSteamLobby);
                _autoPhase = 99;
            }
        }

        private void BeginInviteResolve()
        {
            InviteStatus = InviteState.Resolving;
            InviteCodeText = null;
            InviteReason = null;
            PortForwardState = 0;
            var port = CoopPlugin.Port.Value;
            var password = HostPassword ?? "";
            var generation = Interlocked.Increment(ref _inviteGeneration);
            new Thread(() =>
            {
                string publicIp = null;
                string reason = null;
                try
                {
                    NetHelpers.BeginDiscovery();
                    if (CoopPlugin.AutoPortForward.Value)
                    {
                        var mapped = NetHelpers.TryMapPort(port, out var why, out var mappingEpoch);
                        Publish(generation, () => PortForwardState = mapped ? 1 : 2);
                        if (mapped)
                        {
                            publicIp = NetHelpers.UpnpExternalIp();
                            if (publicIp != null && !NetHelpers.IsPublicIPv4(publicIp))
                            {
                                reason = "your router's internet address is private (carrier-grade NAT)";
                                publicIp = null;
                            }
                        }
                        else if (why != null)
                        {
                            CoopPlugin.Log.LogInfo("automatic port forwarding did not happen: " + why);
                        }
                    }
                    publicIp ??= NetHelpers.StunPublicIp();
                    if (publicIp != null && !NetHelpers.IsPublicIPv4(publicIp))
                    {
                        publicIp = null;
                    }
                }
                catch (Exception error)
                {
                    reason = error.Message;
                    CoopPlugin.Log.LogWarning("invite code resolve failed: " + error);
                }
                var address = publicIp ?? NetHelpers.LocalIPv4ForGateway() ?? NetHelpers.LocalIPv4();
                var code = address == null ? null : InviteCode.Encode(address, port, password);
                Publish(generation, () =>
                {
                    InviteCodeText = code;
                    InviteReason = reason;
                    InviteStatus = publicIp == null ? InviteState.LanOnly : InviteState.Ready;
                });
            })
            {
                IsBackground = true,
                Name = "CoopInvite"
            }.Start();
        }

        private void Publish(int generation, Action action)
        {
            _dispatcher.TryEnqueue("invite-publish", () =>
            {
                if (Volatile.Read(ref _inviteGeneration) == generation)
                {
                    action();
                }
            });
        }

        private static string GenerateSessionPassword()
        {
            const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
            var bytes = new byte[8];
            using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider())
            {
                rng.GetBytes(bytes);
            }
            var chars = new char[bytes.Length];
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = alphabet[bytes[i] & 31];
            }
            return new string(chars);
        }

        private void OnGUI()
        {
            using (Util.PerfProbe.Sample("core.ongui"))
            {
                _ui.Draw(this, Net);
            }
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            CEventManager.RemoveListener<CEventPlayer_OnOpenCardPack>(OnLocalPackOpened);
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnGameDataFinishLoaded);
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
