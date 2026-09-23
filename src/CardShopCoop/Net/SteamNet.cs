using CardShopCoop.Net.Datagrams;
using CardShopCoop.Net.Kcp;
using CardShopCoop.Util;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Steamworks;
using UnityEngine;
using CardShopCoop;

namespace CardShopCoop.Net
{
    /// <summary>
    /// Lobby lifecycle + invites. One long-lived instance: the GameLobbyJoinRequested
    /// callback must be listening from startup so accepting a Steam invite at any moment
    /// (or launching the game via an invite: +connect_lobby) starts the join flow.
    /// </summary>
    public class SteamLobby
    {
        private CallResult<LobbyCreated_t> _createLobby;
        private Callback<LobbyEnter_t> _cbEnter;
        private Callback<GameLobbyJoinRequested_t> _cbJoinRequested;
        private CallResult<LobbyMatchList_t> _lobbyList;

        public CSteamID LobbyId = CSteamID.Nil;
        private bool _joining;
        private CSteamID _pendingLobbyId = CSteamID.Nil;
        private long _operationGeneration;
        private long _pendingOperationGeneration;
        private long _activeLobbyOperationGeneration;
        private long _listOperationGeneration;
        private bool _pendingPublic;
        private string _pendingName = "";
        private bool _pendingHasPw;

        public Action<CSteamID, long> OnLobbyCreated;
        // host: lobby is live; args = lobby, operation token
        public Action<CSteamID, CSteamID, long> OnEnteredLobby;
        // client: joined; args = entered lobby, lobby owner, operation token
        public Action<CSteamID, long, string> OnJoinFailed;
        public Action<long, string> OnHostFailed;
        public Action<CSteamID> OnInviteAccepted; // local player accepted someone's invite
        public Action<string> OnError;
        public Action OnListUpdated;

        public struct LobbyRow
        {
            public CSteamID Id;
            public string Name;
            public int Players;
            public int Max;
            public bool HasPw;
            public string Ver;
        }

        public readonly List<LobbyRow> Lobbies = new();
        public bool ListRefreshing
        {
            get; private set;
        }

        public void Init()
        {
            _lobbyList = CallResult<LobbyMatchList_t>.Create((e, ioFail) =>
            {
                if (_listOperationGeneration == 0 || _listOperationGeneration != _operationGeneration)
                {
                    return; // stale list result must not clear or publish a newer operation
                }

                ListRefreshing = false;
                Lobbies.Clear();
                if (ioFail)
                {
                    OnError?.Invoke("Steam lobby list failed");
                    return;
                }
                for (var i = 0; i < e.m_nLobbiesMatching; i++)
                {
                    var id = SteamMatchmaking.GetLobbyByIndex(i);
                    if (id == CSteamID.Nil)
                    {
                        continue;
                    }

                    Lobbies.Add(new LobbyRow
                    {
                        Id = id,
                        Name = SteamMatchmaking.GetLobbyData(id, "name"),
                        Players = SteamMatchmaking.GetNumLobbyMembers(id),
                        Max = SteamMatchmaking.GetLobbyMemberLimit(id),
                        HasPw = SteamMatchmaking.GetLobbyData(id, "pw") == "1",
                        Ver = SteamMatchmaking.GetLobbyData(id, "coopver"),
                    });
                }
                OnListUpdated?.Invoke();
            });
            _cbEnter = Callback<LobbyEnter_t>.Create(e =>
            {
                CSteamID enteredLobby = new(e.m_ulSteamIDLobby);
                if (!IsCurrentJoin(enteredLobby, out var operation))
                {
                    return; // our own host-side enter
                }

                // LobbyEnter is also delivered for rejected joins.  Do not expose a
                // rejected lobby to the bridge: that would start the network session and send Hello as
                // though the join had succeeded.  Re-check the token in the failure
                // path so an old callback cannot tear down a newer join.
                if (e.m_EChatRoomEnterResponse != 1u) // EChatRoomEnterResponseSuccess
                {
                    FailJoin(operation, enteredLobby, e.m_EChatRoomEnterResponse);
                    return;
                }
                _joining = false;
                _pendingLobbyId = CSteamID.Nil;
                _pendingOperationGeneration = 0;
                LobbyId = enteredLobby;
                _activeLobbyOperationGeneration = operation;
                var owner = SteamMatchmaking.GetLobbyOwner(LobbyId);
                OnEnteredLobby?.Invoke(enteredLobby, owner, operation);
            });
            _cbJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(e =>
            {
                OnInviteAccepted?.Invoke(e.m_steamIDLobby);
            });
        }

        public bool SteamAvailable()
        {
            try
            {
                return SteamAPI.IsSteamRunning();
            }
            catch (Exception e) { Swallow.Log(e); return false; }
        }

        public void Host(bool isPublic, string lobbyName, bool hasPassword, int maxPlayers)
        {
            if (maxPlayers < SteamLobbyLimits.MinPlayers || maxPlayers > SteamLobbyLimits.MaxPlayers)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPlayers), maxPlayers,
                    "Steam lobby player count must be between 2 and 250.");
            }

            AbortPendingOperation();
            _pendingPublic = isPublic;
            _pendingName = lobbyName ?? "";
            _pendingHasPw = hasPassword;
            var operation = ++_operationGeneration;
            _pendingOperationGeneration = operation;
            // CreateLobby is asynchronous. A process-wide Callback<LobbyCreated_t> cannot
            // identify which request produced a result, so bind this CallResult to this
            // exact SteamAPICall and also retain the immutable operation token.
            var name = _pendingName;
            var password = _pendingHasPw;
            var ownerName = CoopCore.Instance == null
                ? CoopPlugin.PlayerName.Value
                : CoopCore.Instance.EffectivePlayerName;
            _createLobby = CallResult<LobbyCreated_t>.Create((e, ioFail) =>
            {
                if (operation != _operationGeneration || _pendingOperationGeneration != operation || _joining)
                {
                    return; // result from an abandoned CreateLobby operation
                }

                _createLobby = null;
                if (ioFail || e.m_eResult != EResult.k_EResultOK)
                {
                    var error = "Steam lobby creation failed: " + e.m_eResult;
                    _pendingOperationGeneration = 0;
                    OnError?.Invoke(error);
                    OnHostFailed?.Invoke(operation, error);
                    return;
                }
                LobbyId = new CSteamID(e.m_ulSteamIDLobby);
                _activeLobbyOperationGeneration = operation;
                SteamMatchmaking.SetLobbyData(LobbyId, "coopmod", "communitymultiplayer");
                SteamMatchmaking.SetLobbyData(LobbyId, "coopver", CoopPlugin.Version);
                SteamMatchmaking.SetLobbyData(LobbyId, "name",
                    string.IsNullOrEmpty(name) ? (ownerName + "'s shop") : name);
                SteamMatchmaking.SetLobbyData(LobbyId, "pw", password ? "1" : "0");
                OnLobbyCreated?.Invoke(LobbyId, operation);
            });
            var call = SteamMatchmaking.CreateLobby(
                isPublic ? ELobbyType.k_ELobbyTypePublic : ELobbyType.k_ELobbyTypeFriendsOnly, maxPlayers);
            _createLobby.Set(call);
        }

        /// <summary>Fetch public lobbies of THIS mod (server-side filtered by our key).</summary>
        public void RefreshList()
        {
            if (ListRefreshing)
            {
                return;
            }

            ListRefreshing = true;
            _listOperationGeneration = ++_operationGeneration;
            SteamMatchmaking.AddRequestLobbyListStringFilter("coopmod", "communitymultiplayer", ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListResultCountFilter(100);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            var call = SteamMatchmaking.RequestLobbyList();
            _lobbyList.Set(call);
        }

        public long Join(CSteamID lobby)
        {
            AbortPendingOperation();
            _joining = true;
            _pendingLobbyId = lobby;
            _pendingOperationGeneration = ++_operationGeneration;
            SteamMatchmaking.JoinLobby(lobby);
            return _pendingOperationGeneration;
        }

        public void OpenInviteDialog()
        {
            if (LobbyId != CSteamID.Nil)
            {
                SteamFriends.ActivateGameOverlayInviteDialog(LobbyId);
            }
        }

        public void Leave()
        {
            AbortPendingOperation();
        }

        public bool IsCurrentLobby(CSteamID lobby, long operation)
        {
            return lobby != CSteamID.Nil && operation != 0
                && LobbyId == lobby && _activeLobbyOperationGeneration == operation
                && _operationGeneration == operation;
        }

        public bool IsCurrentOperation(long operation)
        {
            return operation != 0 && _operationGeneration == operation;
        }

        /// <summary>Checks the current lobby membership on the Steam callback/pump thread.</summary>
        public bool IsMember(CSteamID steamId)
        {
            if (LobbyId == CSteamID.Nil || steamId == CSteamID.Nil)
            {
                return false;
            }

            var memberCount = SteamMatchmaking.GetNumLobbyMembers(LobbyId);
            for (var i = 0; i < memberCount; i++)
            {
                if (SteamMatchmaking.GetLobbyMemberByIndex(LobbyId, i) == steamId)
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsCurrentJoin(CSteamID lobby, out long operation)
        {
            operation = _pendingOperationGeneration;
            return _joining && operation != 0 && operation == _operationGeneration
                && (_pendingLobbyId == CSteamID.Nil || _pendingLobbyId == lobby);
        }

        private void FailJoin(long operation, CSteamID lobby, uint response)
        {
            if (!_joining || operation == 0 || operation != _operationGeneration
                || _pendingOperationGeneration != operation || _pendingLobbyId != lobby)
            {
                return;
            }

            CoopPlugin.Log.LogWarning("Steam lobby join rejected (" + DescribeJoinFailure(response) + ").");
            _joining = false;
            _pendingLobbyId = CSteamID.Nil;
            _pendingOperationGeneration = 0;
            ++_operationGeneration;

            // Steam can report an enter response after creating a local lobby
            // membership.  Always leave that exact lobby, but never touch a newer
            // operation's LobbyId.
            try
            {
                SteamMatchmaking.LeaveLobby(lobby);
            }
            catch (Exception e) { Swallow.Log(e); }
            if (LobbyId == lobby)
            {
                LobbyId = CSteamID.Nil;
            }

            OnJoinFailed?.Invoke(lobby, operation,
                "Unable to join Steam lobby: " + DescribeJoinFailure(response) + ".");
        }

        private static string DescribeJoinFailure(uint response)
        {
            switch (response)
            {
                case 2u:
                    return "the lobby no longer exists";
                case 3u:
                    return "you are not allowed to join this lobby";
                case 4u:
                    return "the lobby is full";
                case 6u:
                    return "you are banned from this lobby";
                case 7u:
                    return "the lobby is temporarily unavailable";
                default:
                    return "Steam rejected the join (response " + response + ")";
            }
        }

        private void AbortPendingOperation()
        {
            ++_operationGeneration;
            _createLobby?.Dispose();
            _createLobby = null;
            _pendingOperationGeneration = 0;
            _activeLobbyOperationGeneration = 0;
            _listOperationGeneration = 0;
            ListRefreshing = false;
            _joining = false;
            _pendingLobbyId = CSteamID.Nil;
            if (LobbyId != CSteamID.Nil)
            {
                try
                {
                    SteamMatchmaking.LeaveLobby(LobbyId);
                }
                catch (Exception e) { Swallow.Log(e); }
                LobbyId = CSteamID.Nil;
            }
        }
    }

    /// <summary>
    /// Deferred Steam datagram bearer used to keep transport creation ahead of lobby entry.
    /// KCP is started by the connection provider immediately; the Steam sockets bearer is
    /// bound only once the lobby supplies the authorization context or owner identity.
    /// </summary>
    internal sealed class DeferredSteamDatagramTransport : IDatagramTransport
    {
        private readonly object _gate = new object();
        private IDatagramTransport _inner;
        private bool _started;
        private bool _disposed;

        public DeferredSteamDatagramTransport(int maxDatagramSize)
        {
            if (maxDatagramSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxDatagramSize));
            }

            MaxDatagramSize = maxDatagramSize;
        }

        public int MaxDatagramSize
        {
            get;
        }

        public bool IsRunning
        {
            get
            {
                lock (_gate)
                {
                    return _inner != null && _inner.IsRunning;
                }
            }
        }

        public event Action<DatagramPeer> PeerAvailable;
        public event Action<DatagramPeer, string> PeerClosed;

        public void Bind(IDatagramTransport inner)
        {
            if (inner == null)
            {
                throw new ArgumentNullException(nameof(inner));
            }

            bool start;
            lock (_gate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(DeferredSteamDatagramTransport));
                }

                if (_inner != null)
                {
                    throw new InvalidOperationException("Steam datagram bearer is already bound.");
                }

                if (inner.MaxDatagramSize != MaxDatagramSize)
                {
                    throw new ArgumentException(
                        "Steam datagram bearer size does not match the KCP transport.", nameof(inner));
                }

                _inner = inner;
                _inner.PeerAvailable += OnPeerAvailable;
                _inner.PeerClosed += OnPeerClosed;
                start = _started;
            }

            if (!start)
            {
                return;
            }

            try
            {
                inner.Start();
            }
            catch (Exception startError)
            {
                var detach = false;
                lock (_gate)
                {
                    _started = false;
                    if (ReferenceEquals(_inner, inner))
                    {
                        _inner = null;
                        detach = true;
                    }
                }

                if (detach)
                {
                    ReleaseInner(inner, startError);
                }

                throw;
            }
        }

        public void Start()
        {
            IDatagramTransport inner;
            lock (_gate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(DeferredSteamDatagramTransport));
                }

                if (_started)
                {
                    return;
                }

                _started = true;
                inner = _inner;
            }

            if (inner == null)
            {
                return;
            }

            try
            {
                inner.Start();
            }
            catch (Exception startError)
            {
                var detach = false;
                lock (_gate)
                {
                    _started = false;
                    if (ReferenceEquals(_inner, inner))
                    {
                        _inner = null;
                        detach = true;
                    }
                }

                if (detach)
                {
                    ReleaseInner(inner, startError);
                }

                throw;
            }
        }

        public int Poll(int maxDatagrams, Action<DatagramPeer, ArraySegment<byte>> receive)
        {
            if (receive == null)
            {
                throw new ArgumentNullException(nameof(receive));
            }

            IDatagramTransport inner;
            lock (_gate)
            {
                if (_disposed)
                {
                    return 0;
                }

                inner = _inner;
            }

            return inner == null ? 0 : inner.Poll(maxDatagrams, receive);
        }

        public void Send(DatagramPeer peer, ArraySegment<byte> datagram)
        {
            IDatagramTransport inner;
            lock (_gate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(DeferredSteamDatagramTransport));
                }

                inner = _inner;
            }

            if (inner == null)
            {
                throw new InvalidOperationException("Steam datagram bearer is not bound");
            }

            inner.Send(peer, datagram);
        }

        public void Close(DatagramPeer peer, string reason)
        {
            IDatagramTransport inner;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                inner = _inner;
            }

            inner?.Close(peer, reason);
        }

        public void Stop()
        {
            IDatagramTransport inner;
            lock (_gate)
            {
                if (!_started)
                {
                    return;
                }

                _started = false;
                inner = _inner;
            }

            inner?.Stop();
        }

        public void Dispose()
        {
            IDatagramTransport inner;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _started = false;
                inner = _inner;
                _inner = null;
            }

            if (inner != null)
            {
                ReleaseInner(inner);
            }
        }

        private void ReleaseInner(IDatagramTransport inner, Exception startupError = null)
        {
            var cleanupErrors = new List<Exception>();
            try
            {
                inner.PeerAvailable -= OnPeerAvailable;
            }
            catch (Exception cleanupError)
            {
                cleanupErrors.Add(cleanupError);
            }

            try
            {
                inner.PeerClosed -= OnPeerClosed;
            }
            catch (Exception cleanupError)
            {
                cleanupErrors.Add(cleanupError);
            }

            try
            {
                inner.Stop();
            }
            catch (Exception cleanupError)
            {
                cleanupErrors.Add(cleanupError);
            }

            try
            {
                inner.Dispose();
            }
            catch (Exception cleanupError)
            {
                cleanupErrors.Add(cleanupError);
            }

            if (cleanupErrors.Count != 0)
            {
                if (startupError != null)
                {
                    cleanupErrors.Insert(0, startupError);
                    throw new AggregateException(
                        "Steam datagram bearer startup cleanup failed.", cleanupErrors);
                }

                throw new AggregateException("Steam datagram bearer disposal failed.", cleanupErrors);
            }
        }

        private void OnPeerAvailable(DatagramPeer peer)
        {
            PeerAvailable?.Invoke(peer);
        }

        private void OnPeerClosed(DatagramPeer peer, string reason)
        {
            PeerClosed?.Invoke(peer, reason);
        }
    }

    /// <summary>
    /// The one and only <see cref="ISteamBridge"/> implementation. Steam identity and lobby
    /// lifecycle remain entirely on this side of the Steam-free bridge; the returned transport
    /// is the common KCP manager used by every backend.
    /// </summary>
    internal sealed class SteamBridgeImpl : ISteamBridge
    {
        internal const int MaxPendingCallbacks = 512;
        internal const int MaxCallbacksPerPump = 64;

        private readonly SteamLobby _lobby = new();
        private readonly ConcurrentQueue<BridgeCallback> _callbacks = new();
        private readonly object _transportGate = new object();
        private readonly List<LobbyRow> _rows = new();
        private DeferredSteamDatagramTransport _datagrams;
        private ICoopTransport _tx;
        private long _transportGeneration;
        private SteamBridgePump _pump;
        private int _pendingCallbackCount;

        internal int PendingCallbackCount => Math.Max(0, Volatile.Read(ref _pendingCallbackCount));

        public SteamBridgeImpl()
        {
            try
            {
                SteamAPI.IsSteamRunning();
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    "Steamworks runtime is not functional on this install (" +
                    e.GetType().Name + ": " + e.Message + ")");
            }

            var core = CoopCore.Instance;
            if (core != null)
            {
                _pump = core.gameObject.AddComponent<SteamBridgePump>();
                _pump.Initialize(PumpCallbacks);
            }
        }

        public Action<string> OnError
        {
            get; set;
        }
        public Action<ulong> OnLobbyLive
        {
            get; set;
        }
        public Action<ICoopTransport> OnConnectedToHost
        {
            get; set;
        }
        public Action<ulong> OnInviteAccepted
        {
            get; set;
        }
        public Action<ulong, long, ICoopTransport, string> OnJoinFailed
        {
            get; set;
        }
        public Action<ICoopTransport, string> OnSessionFailed
        {
            get; set;
        }

        public void Init()
        {
            _lobby.Init();
            _lobby.OnError = error => QueueGlobal(() => OnError?.Invoke(error));
            _lobby.OnJoinFailed = (lobby, operation, error) =>
            {
                CaptureTransport(out var generation, out var transport);
                if (transport == null)
                {
                    return;
                }

                QueueSession(generation, transport,
                    () => OnJoinFailed?.Invoke(lobby.m_SteamID, operation, transport, error));
            };
            _lobby.OnHostFailed = (operation, error) =>
            {
                CaptureTransport(out var generation, out var transport);
                DeferredSteamDatagramTransport datagrams;
                lock (_transportGate)
                {
                    datagrams = _datagrams;
                }

                if (transport == null || datagrams == null)
                {
                    return;
                }

                QueueSession(generation, transport,
                    () => FailHost(generation, transport, datagrams, operation, error, false));
            };
            _lobby.OnLobbyCreated = (id, operation) =>
            {
                CaptureTransport(out var generation, out var transport);
                DeferredSteamDatagramTransport datagrams;
                lock (_transportGate)
                {
                    datagrams = _datagrams;
                }

                if (transport == null || datagrams == null)
                {
                    return;
                }

                QueueSession(generation, transport,
                    () => BindHost(generation, transport, datagrams, id, operation));
            };
            _lobby.OnEnteredLobby = (lobby, owner, operation) =>
            {
                CaptureTransport(out var generation, out var transport);
                DeferredSteamDatagramTransport datagrams;
                lock (_transportGate)
                {
                    datagrams = _datagrams;
                }

                if (transport == null || datagrams == null)
                {
                    return;
                }

                QueueSession(generation, transport,
                    () => BindClient(generation, transport, datagrams, lobby, owner, operation));
            };
            _lobby.OnInviteAccepted = id => QueueGlobal(
                () => OnInviteAccepted?.Invoke(id.m_SteamID));
            _lobby.OnListUpdated = () => { };
        }

        public bool SteamAvailable()
        {
            return _lobby.SteamAvailable();
        }

        public string LocalPersonaName
        {
            get
            {
                try
                {
                    return SteamAvailable() ? (SteamFriends.GetPersonaName() ?? "") : "";
                }
                catch (Exception e) { Swallow.Log(e); return ""; }
            }
        }

        public ulong LocalSteamId
        {
            get
            {
                try
                {
                    if (!SteamAvailable())
                    {
                        return 0;
                    }

                    return SteamUser.GetSteamID().m_SteamID;
                }
                catch (Exception e) { Swallow.Log(e); return 0; }
            }
        }

        public string FriendNickname(ulong steamId)
        {
            if (steamId == 0)
            {
                return "";
            }

            try
            {
                if (!SteamAvailable())
                {
                    return "";
                }

                var friend = new CSteamID(steamId);
                if (!SteamFriends.HasFriend(friend, EFriendFlags.k_EFriendFlagImmediate))
                {
                    return "";
                }

                return SteamFriends.GetFriendPersonaName(friend) ?? "";
            }
            catch (Exception e) { Swallow.Log(e); return ""; }
        }

        public ICoopTransport CreateTransport(bool isHost)
        {
            lock (_transportGate)
            {
                if (_tx != null)
                {
                    throw new InvalidOperationException("Steam bridge already has an active transport.");
                }

                var datagrams = new DeferredSteamDatagramTransport(
                    SteamDatagramTransport.DefaultMaxDatagramSize);
                var transport = new KcpSessionManager(datagrams, isHost);
                _datagrams = datagrams;
                _tx = transport;
                _transportGeneration++;
                return transport;
            }
        }

        public void Host(bool isPublic, string lobbyName, bool hasPassword, int maxPlayers)
        {
            _lobby.Host(isPublic, lobbyName, hasPassword, maxPlayers);
        }

        public long Join(ulong lobbyId)
        {
            return _lobby.Join(new CSteamID(lobbyId));
        }

        public void Leave()
        {
            lock (_transportGate)
            {
                _transportGeneration++;
                _tx = null;
                _datagrams = null;
            }

            _lobby.Leave();
        }

        public void OpenInviteDialog()
        {
            _lobby.OpenInviteDialog();
        }

        public void RefreshList()
        {
            _lobby.RefreshList();
        }

        public bool ListRefreshing
        {
            get
            {
                return _lobby.ListRefreshing;
            }
        }

        public List<LobbyRow> Lobbies
        {
            get
            {
                _rows.Clear();
                foreach (var r in _lobby.Lobbies)
                {
                    _rows.Add(new LobbyRow
                    {
                        Id = r.Id.m_SteamID,
                        Name = r.Name,
                        Players = r.Players,
                        Max = r.Max,
                        HasPw = r.HasPw,
                        Ver = r.Ver,
                    });
                }

                return _rows;
            }
        }

        private void BindHost(long generation, ICoopTransport transport,
            DeferredSteamDatagramTransport datagrams, CSteamID lobby, long operation)
        {
            if (!IsCurrent(generation, transport, datagrams)
                || !_lobby.IsCurrentLobby(lobby, operation))
            {
                return;
            }

            try
            {
                datagrams.Bind(SteamDatagramTransport.CreateHost(
                    steamId => _lobby.IsMember(new CSteamID(steamId))));
            }
            catch (Exception error)
            {
                FailHost(generation, transport, datagrams, operation,
                    "Steam host transport startup failed: " + error.Message, true);
                return;
            }

            if (!IsCurrent(generation, transport, datagrams)
                || !_lobby.IsCurrentLobby(lobby, operation))
            {
                return;
            }

            OnLobbyLive?.Invoke(lobby.m_SteamID);
        }

        private void BindClient(long generation, ICoopTransport transport,
            DeferredSteamDatagramTransport datagrams, CSteamID lobby,
            CSteamID owner, long operation)
        {
            if (!IsCurrent(generation, transport, datagrams)
                || !_lobby.IsCurrentLobby(lobby, operation))
            {
                return;
            }

            try
            {
                if (owner == CSteamID.Nil)
                {
                    throw new InvalidOperationException(
                        "Steam lobby entered without an owner identity.");
                }

                datagrams.Bind(SteamDatagramTransport.CreateClient(owner.m_SteamID));
            }
            catch (Exception error)
            {
                FailJoin(generation, transport, datagrams, lobby, operation,
                    "Steam client transport startup failed: " + error.Message);
                return;
            }

            if (!IsCurrent(generation, transport, datagrams)
                || !_lobby.IsCurrentLobby(lobby, operation))
            {
                return;
            }

            OnConnectedToHost?.Invoke(transport);
        }

        private void FailHost(long generation, ICoopTransport transport,
            DeferredSteamDatagramTransport datagrams, long operation, string error,
            bool notifyUi)
        {
            if (!IsCurrent(generation, transport, datagrams)
                || !_lobby.IsCurrentOperation(operation))
            {
                return;
            }

            if (notifyUi)
            {
                OnError?.Invoke(error);
            }
            if (!IsCurrent(generation, transport, datagrams)
                || !_lobby.IsCurrentOperation(operation))
            {
                return;
            }

            OnSessionFailed?.Invoke(transport, error);
        }

        private void FailJoin(long generation, ICoopTransport transport,
            DeferredSteamDatagramTransport datagrams, CSteamID lobby,
            long operation, string error)
        {
            if (!IsCurrent(generation, transport, datagrams)
                || !_lobby.IsCurrentLobby(lobby, operation))
            {
                return;
            }

            OnError?.Invoke(error);
            if (!IsCurrent(generation, transport, datagrams)
                || !_lobby.IsCurrentLobby(lobby, operation))
            {
                return;
            }

            OnJoinFailed?.Invoke(lobby.m_SteamID, operation, transport, error);
        }

        private void CaptureTransport(out long generation, out ICoopTransport transport)
        {
            lock (_transportGate)
            {
                generation = _transportGeneration;
                transport = _tx;
            }
        }

        private bool IsCurrent(long generation, ICoopTransport transport,
            DeferredSteamDatagramTransport datagrams)
        {
            lock (_transportGate)
            {
                return generation == _transportGeneration
                    && ReferenceEquals(transport, _tx)
                    && ReferenceEquals(datagrams, _datagrams);
            }
        }

        private void QueueGlobal(Action action)
        {
            EnqueueCallback(new BridgeCallback(0, null, action));
        }

        private void QueueSession(long generation, ICoopTransport transport, Action action)
        {
            EnqueueCallback(new BridgeCallback(generation, transport, action));
        }

        private void EnqueueCallback(BridgeCallback callback)
        {
            var pending = Interlocked.Increment(ref _pendingCallbackCount);
            if (pending > MaxPendingCallbacks)
            {
                Interlocked.Decrement(ref _pendingCallbackCount);
                var error = new InvalidOperationException(
                    "Steam bridge callback queue exceeded its " + MaxPendingCallbacks + " item limit.");
                CoopPlugin.Log?.LogError(error.Message);
                throw error;
            }

            _callbacks.Enqueue(callback);
        }

        private void PumpCallbacks()
        {
            for (var processed = 0; processed < MaxCallbacksPerPump
                && _callbacks.TryDequeue(out var callback); processed++)
            {
                Interlocked.Decrement(ref _pendingCallbackCount);
                if (callback.Transport != null)
                {
                    DeferredSteamDatagramTransport datagrams;
                    lock (_transportGate)
                    {
                        datagrams = _datagrams;
                    }

                    if (!IsCurrent(callback.Generation, callback.Transport, datagrams))
                    {
                        continue;
                    }
                }

                callback.Action();
            }
        }

        private readonly struct BridgeCallback
        {
            public readonly long Generation;
            public readonly ICoopTransport Transport;
            public readonly Action Action;

            public BridgeCallback(long generation, ICoopTransport transport, Action action)
            {
                Generation = generation;
                Transport = transport;
                Action = action;
            }
        }
    }

    /// <summary>Drains Steam lobby callbacks on Unity's main-thread update loop.</summary>
    internal sealed class SteamBridgePump : MonoBehaviour
    {
        private Action _pump;

        internal void Initialize(Action pump)
        {
            _pump = pump ?? throw new ArgumentNullException(nameof(pump));
        }

        private void Update()
        {
            using (Util.PerfProbe.Sample("steam.callbacks"))
            {
                _pump?.Invoke();
            }
        }
    }
}
