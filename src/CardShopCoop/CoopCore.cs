using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using CardShopCoop.Sync;
using Newtonsoft.Json;
// NO `using Steamworks;` HERE, AND NEVER AGAIN. CoopCore is an always-loaded type: a
// single Steamworks token in one of its fields or method bodies makes the whole class
// (and therefore the whole mod) fail to load on the Game Pass build, which ships no
// com.rlabrecque.steamworks.net.dll. Everything Steam-shaped goes through
// Net.ISteamBridge. The absence of this using is the compiler-enforced proof.
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop
{
    public enum CoopRole { None, Host, Client }

    /// <summary>How far the invite code has got. Off = not LAN-hosting (or a Steam session,
    /// where Steam's own invites do this job). Resolving = the worker is still asking the
    /// router and the STUN server. Ready = the code carries a public address. LanOnly = we
    /// could not establish a public address, so the code carries the LAN one - still perfectly
    /// good for the other PC in the house, useless over the internet.</summary>
    public enum InviteState { Off, Resolving, Ready, LanOnly }

    public class CoopCore : MonoBehaviour
    {
        public static CoopCore Instance { get; private set; }

        /// <summary>True while this mod owns the game's modal UI state. Camera input
        /// is patched separately because the CMF camera reads raw mouse axes from its
        /// own component, outside InteractionPlayerController.Update.</summary>
        public static bool WindowBlocksInput
        {
            get { return Instance != null && Instance._uiModeHeldByWindow; }
        }
        public static CoopRole Role { get; private set; } = CoopRole.None;
        private static double _lastImmediateObjectSync;

        /// <summary>Called by mutation postfixes. The modules still coalesce their own
        /// state into one snapshot; this only removes the normal polling latency.</summary>
        public static void RequestImmediateObjectSync()
        {
            var core = Instance;
            if (core == null || Role == CoopRole.None) return;
            // Several vanilla methods can participate in one gameplay action (for example
            // removing an item updates both the compartment and the box). Coalesce those
            // callbacks into one sync pass; a 100 ms ceiling is still far below the normal
            // snapshot cadence and avoids repeatedly arming every scanner in one burst.
            double now = Time.realtimeSinceStartupAsDouble;
            if (now - _lastImmediateObjectSync < 0.10) return;
            _lastImmediateObjectSync = now;
            try
            {
                core._world.ForceNextTick();
                core._cardShelves.ForceNextTick();
                core._objMoves.ForceNextTick();
                core._boxes.ForceBroadcastNextTick();
                core._population.ForceNextTick();
                core._cardBoxes.ForceNextTick();
                core._furnBoxes.ForceNextTick();
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("Immediate sync request: " + e.Message); }
        }

        /// <summary>Host only: the host just sold/trashed a placed object, so its
        /// ShelfManager list re-indexed and every client's roster is now misaligned. Force
        /// the population broadcast out NEXT frame - before the ~0.75s content syncs can
        /// ship shifted-index deltas - so clients reconcile their placed-object structure
        /// (position-aware) and only then apply the content that follows. Without this the
        /// client can keep the wrong shelf and every item/card on it repaints onto the wrong
        /// physical shelf.</summary>
        public void NotifyHostStructureChanged()
        {
            if (Role != CoopRole.Host || !InGameLevel()) return;
            try { RequestImmediateObjectSync(); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("host structure change: " + e.Message); }
        }

        /// <summary>True from the moment the guest joins until it returns to the title
        /// screen. The guest is standing in the HOST'S world; saving would overwrite the
        /// guest's own slot with the host's shop. This stays set through a mid-session
        /// disconnect (when Role goes back to None but the guest is STILL in the borrowed
        /// world - the "keep walking around" state), so a day-end autosave or a quit-save
        /// after the host leaves can't pollute the guest's save. Cleared once the guest is
        /// safely back at the title (no session AND out of any game level) - see Update.</summary>
        public static bool GuestBorrowedWorld;

        /// <summary>True only while session teardown is clearing module state.</summary>
        public static bool IsTearingDown { get; private set; }

        public string StatusLine = "Not connected";
        public string ErrorLine = "";
        public string HostTimeLine = "";
        public string RegisterLine = "";
        public float RegisterLineTimer;
        public int PlayerModelGeneration { get; private set; }
        public readonly Dictionary<int, string> PeerNames = new Dictionary<int, string>();

        /// <summary>The name sent to peers: Steam persona when usable, otherwise the
        /// configured profile name.</summary>
        public string EffectivePlayerName
        {
            get
            {
                string persona = _steam == null ? "" : _steam.LocalPersonaName;
                return string.IsNullOrWhiteSpace(persona) ? CoopPlugin.PlayerName.Value : persona;
            }
        }

        public bool UsingSteamPersona
        {
            get
            {
                string persona = _steam == null ? "" : _steam.LocalPersonaName;
                return !string.IsNullOrWhiteSpace(persona);
            }
        }

        private ICoopTransport _net;
        /// <summary>Null on any build where the Steamworks assembly is absent (Game Pass /
        /// DRM-free). NOT the same as "Steam isn't running" - see ISteamBridge. Must stay an
        /// INTERFACE-typed field: a SteamLobby-typed one would put Steamworks metadata back
        /// on CoopCore.</summary>
        private ISteamBridge _steam;
        private readonly Dictionary<int, string> _peerWireNames = new Dictionary<int, string>();
        private readonly Dictionary<int, ulong> _peerSteamIds = new Dictionary<int, ulong>();
        private ulong _autoJoinSteamLobby; // from +connect_lobby (game launched via invite)
        public bool IsSteamSession { get; private set; }
        private readonly AvatarManager _avatars = new AvatarManager();
        private readonly Dictionary<int, PlayerModelEntry> _playerModels = new Dictionary<int, PlayerModelEntry>();
        private PlayerModelEntry _localPlayerModel;
        private bool _localPlayerModelReady;
        private Transform _localModelAppliedRoot;
        private bool _characterPreviewActive;
        private PlayerModelEntry _lastCommittedPlayerModel;
        private readonly List<PlayerModelEntry> _playerModelUndo = new List<PlayerModelEntry>();
        private readonly List<PlayerModelEntry> _playerModelRedo = new List<PlayerModelEntry>();
        private const int PlayerModelHistoryLimit = 30;
        private bool _localModelSavePending;
        private float _localModelSaveTimer;
        private readonly WorldSync _world = new WorldSync();
        private readonly NpcSync _npcs = new NpcSync();
        private readonly CardShelfSync _cardShelves = new CardShelfSync();
        private readonly ObjMoveSync _objMoves = new ObjMoveSync();
        private readonly BoxSync _boxes = new BoxSync();
        private readonly PopulationSync _population = new PopulationSync();

        // domain sync modules (v0.15): each owns one game system end-to-end and talks
        // through the standard SendOp/BroadcastState/HostApplyOp/ClientApplyState contract
        private readonly GradingSync _grading = new GradingSync();
        private readonly TradeServe _trades = new TradeServe();
        private readonly PlayTableSync _tables = new PlayTableSync();
        private readonly PlayerIntentBus _intents = new PlayerIntentBus();
        private readonly StaffSync _staff = new StaffSync();
        private readonly ShopStateSync _shopState = new ShopStateSync();
        private readonly SettingsSync _settings = new SettingsSync();
        private readonly MarketSync _market = new MarketSync();
        private readonly ReportSync _report = new ReportSync();
        private readonly ContainerSync _containers = new ContainerSync();
        private readonly TournamentSync _tournament = new TournamentSync();
        private readonly TvSync _tv = new TvSync();
        private readonly CardBoxSync _cardBoxes = new CardBoxSync();
        private readonly FurnBoxSync _furnBoxes = new FurnBoxSync();
        private readonly Sync.RegisterSync _register = new Sync.RegisterSync();
        private string _lastShopNameSent;
        private float _shopNameTimer = -1.0f; // staggered phase (see _lightSyncTimer note)
        private float _npcSweepTimer = -1.3f;
        public string PromptLine = "";

        // ---- invite code / UPnP (LAN hosting only) ----
        // COMPUTED BY A WORKER THREAD, WRITTEN ON THE MAIN ONE, READ BY OnGUI. The resolve
        // worker blocks on SSDP, HTTP and STUN for several seconds and the host panel has to
        // keep drawing something honest the entire time - so the worker hands each result back
        // through _mainThread (see Publish), which puts every write in the same single-threaded
        // order as Shutdown's reset and the UI's reads. They stay volatile anyway: that is one
        // cheap line against a future caller that writes one of them from a worker again, and
        // OnGUI itself latches them per Layout pass (CoopUI.DrawInvite) because an IMGUI window
        // whose CONTROL COUNT changes between the Layout and Repaint passes throws.
        public volatile InviteState InviteStatus = InviteState.Off;
        /// <summary>The composed code, or null while it is still unknown (the copy button is
        /// disabled until this exists).</summary>
        public volatile string InviteCodeText;
        /// <summary>Why the code is LAN-only, in words a player can act on. Null otherwise.</summary>
        public volatile string InviteReason;
        /// <summary>0 = nothing to say (pending, or auto-forwarding is off), 1 = the router
        /// accepted the request, 2 = the router declined. Deliberately not a bool: "we haven't
        /// asked yet" and "the router said no" must not draw the same line.</summary>
        public volatile int PortForwardState;
        /// <summary>Bumped by every invite resolve AND by every Shutdown. The worker captures
        /// it and publishes nothing if it has moved on - otherwise a host who stops and
        /// re-hosts inside the resolve window (which is SECONDS long) gets the dead session's
        /// address and port presented as this session's ready code.</summary>
        private int _inviteGen;

        private readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();
        private UI.CoopUI _ui;

        // client-side save + mod-sidecar download
        private MemoryStream _saveBuf;
        private int _saveExpected = -1;
        private byte[] _pendingSave;
        private MemoryStream _bundleBuf;
        private int _bundleExpected = -1;
        private int _hostSlot;
        private bool _worldRequested;

        // host price sync
        private float _priceTimer = -0.45f;
        private int _lastPriceHash;
        private float _priceHeal;
        private readonly List<KeyValuePair<int, float>> _priceBuf = new List<KeyValuePair<int, float>>();
        private readonly HashSet<int> _priceSeenTypes = new HashSet<int>();

        // timers
        private float _stateTimer;
        private float _pingTimer;
        private float _econTimer = -0.11f;
        private float _dayTimer = -0.9f;

        // local movement measurement
        private Vector3 _lastPos;
        private bool _hasLastPos;

        // host economy/progression change detection
        private double _lastCoinSent = double.MinValue;
        private long _lastProgressSent = long.MinValue;
        private float _coinHeal;      // re-send the wallet every 15s even if unchanged
        private float _progressHeal;  // ...and shop exp/level/fame, so a dropped packet self-heals
        // guest spends applied so far THIS frame; lets the host reject a spend the guest
        // passed against its stale (0.5s-lagged) wallet mirror before the shared balance
        // goes negative. Reset once per frame before the message drain.
        private double _pendingReduceThisFrame = 0.0;


        // one-time link confirmation logging
        private readonly HashSet<int> _gotStateFrom = new HashSet<int>();
        private bool _loggedEconLink;
        private bool _loggedTimeLink;

        // pipeline diagnostics: prove where sync stalls instead of guessing
        private long _diagSent;
        private long _diagRecvStates;
        private float _diagTimer = -7.3f;
        private float _errLogCooldown;

        private void Guarded(string stage, Action action)
        {
            try { action(); }
            catch (Exception e)
            {
                if (_errLogCooldown <= 0f)
                {
                    _errLogCooldown = 5f;
                    CoopPlugin.Log.LogError($"[{stage}] {e}");
                }
            }
        }

        // LightManager reflection (time of day)
        private static readonly FieldInfo FiTimeHour = Util.ReflectionSurface.RequiredField(typeof(LightManager), "m_TimeHour");
        private static readonly FieldInfo FiTimeMin = Util.ReflectionSurface.RequiredField(typeof(LightManager), "m_TimeMin");
        private static readonly FieldInfo FiTimeMinFloat = Util.ReflectionSurface.RequiredField(typeof(LightManager), "m_TimeMinFloat");
        private static readonly FieldInfo FiHasDayEnded = Util.ReflectionSurface.RequiredField(typeof(LightManager), "m_HasDayEnded");
        private static readonly System.Reflection.MethodInfo MiDayReset = Util.ReflectionSurface.RequiredMethod(typeof(LightManager), "DelayUpdateEnv");
        private static readonly FieldInfo FiTimeOfDayIdx = Util.ReflectionSurface.RequiredField(typeof(LightManager), "m_TImeOfDayIndex");
        private static readonly FieldInfo FiFinishLoading = Util.ReflectionSurface.RequiredField(typeof(LightManager), "m_FinishLoading");
        private static readonly System.Reflection.MethodInfo MiLightInit = Util.ReflectionSurface.RequiredMethod(typeof(LightManager), "Init");
        private static readonly System.Reflection.MethodInfo MiUpdateLightData = Util.ReflectionSurface.RequiredMethod(typeof(LightManager), "UpdateLightTimeData");
        private static readonly System.Reflection.MethodInfo MiEvaluateTimeClock = Util.ReflectionSurface.RequiredMethod(typeof(LightManager), "EvaluateTimeClock");
        private static readonly System.Reflection.MethodInfo MiEvaluateWorldUIBrightness = Util.ReflectionSurface.RequiredMethod(typeof(LightManager), "EvaluateWorldUIBrightness");
        private float _lightSyncTimer = -2.3f;   // timers carry staggered phases so the
        private LightManager _lightManager;      // periodic broadcasts never bunch into
        private float _cardResyncTimer = -5.2f;  // one frame (the rhythmic-hitch bug)
        private int _lastCardResyncHash;         // change-gate for the 12s full card repaint
        private float _cardResyncHeal;           // forces a repaint every 30s regardless
        private float _cardPriceHealTimer = -2.1f; // periodic displayed-card price rebroadcast
        private int _lastCardPriceHash;            // change-gate for the card-price heal
        private float _cardPriceHealBeat;          // forces a card-price resend every 30s
        private int _lastStockResyncHash;          // change-gate for the 12s item-STOCK full heal
        private float _stockResyncHeal;            // forces a stock resend every 36s regardless
        private readonly List<KeyValuePair<CardData, float>> _cardPriceBuf = new List<KeyValuePair<CardData, float>>();
        private float _licenseSyncTimer = -3.7f;
        private double _lastLicenseBuyTime = -999.0;
        private string _lastLightJson;
        private float _lightHeal;
        private bool _lightForceResend;
        private bool _observedShopLight;
        private bool _observedNightLight;
        private bool _observedSunlight;
        private bool _observedLightState;
        private bool _clientDayResetPending;
        private bool _clientDayResetInFlight;
        private int _lastLicenseHash;
        private float _licenseHeal;

        // per-frame stages run through cached delegates: a fresh closure per stage per
        // frame was ~600 allocations/second of GC pressure that only existed in-session
        private float _dt;
        private bool _syncActive;
        private Action _actNetPump, _actAvatars, _actWorld, _actCardShelves, _actObjMoves,
            _actBoxes, _actPopulation, _actNpcPuppets, _actNpcSweep,
            _actStateSend, _actNpcCollect, _actModules, _actCardPriceRetry,
            _actFrameCardWork;
        private CustomerManager _cmSweep;
        private CustomerManager _cmSpray; // host-side: real customer list for replayed guest deodorant sprays
        private bool _renamerHandled;
        // FIX E2: the 3D shop-name sign (ShopRenamer.m_ShopName). The client disables the
        // renamer trigger on join, which also unhooks the one-shot OnGameDataFinishLoaded
        // listener that repaints this sign - so a ShopName that arrives mid-join can leave
        // the sign showing the guest's own default. We cache the TMP before disabling and
        // write it directly. It lives on a SEPARATE GameObject from the renamer (verified:
        // the game deactivates the renamer in OnGameDataFinishLoaded AFTER setting this
        // sign, and it still shows), so writing .text after the disable renders fine.
        private TMPro.TMP_Text _shopSign;
        private string _lastShopNameApplied; // re-applied once when ClientReloading clears
        private int _heldBoxFrame = -1;
        private object _heldBoxA, _heldBoxB, _heldBoxC;

        /// <summary>True while a received world is loading (plus a settle grace):
        /// the game's own load-cleanup destroys objects and NOTHING destroyed in
        /// that window is a player action to forward.</summary>
        public static bool ClientReloading;
        private float _reloadStartedAt;
        private int _reloadStartedFrame;
        /// <summary>True for the entire borrowed-world load: the old world may still be
        /// live before the scene changes, and the new world is only partially constructed
        /// afterward. Hold client-side sync until ShelfManager reports completion.</summary>
        // The game's ShelfManager owns the load completion signal.  Do not use a
        // fixed grace period here: a large shop can legitimately take longer than
        // ten seconds to instantiate, and releasing the hold while it is still
        // rebuilding makes the guest compete with its own load on the main thread.
        private bool ClientPreloadHold => ClientReloading;
        private readonly System.Collections.Generic.List<InMsg> _dispatchBuf
            = new System.Collections.Generic.List<InMsg>(64);
        private readonly System.Collections.Generic.List<InMsg> _dispatchRetryNextFrame
            = new System.Collections.Generic.List<InMsg>(8);
        private readonly MessageRouter _messageRouter = new MessageRouter();
        private readonly System.Collections.Generic.HashSet<long> _dispatchSeen
            = new System.Collections.Generic.HashSet<long>();
        /// <summary>Work UNITS dispatched per frame. The whole Incoming queue is still drained
        /// into _dispatchBuf (the coalescer needs the full picture), but applying an unbounded
        /// backlog in one frame is the hitch itself; the remainder keeps its order and waits.</summary>
        private const int DispatchBudget = 256;
        private const byte MaxDispatchRetries = 3;
        private const int MainThreadActionBudget = 64;

        private sealed class MainThreadWork
        {
            public readonly string Stage;
            public readonly Action Action;
            public readonly bool Retryable;
            public int Attempts;
            public int NotBeforeFrame;

            public MainThreadWork(string stage, Action action, bool retryable)
            {
                Stage = stage; Action = action; Retryable = retryable;
            }
        }

        private void QueueMainThread(string stage, Action action, bool retryable)
        {
            if (action == null) throw new ArgumentNullException("action");
            var work = new MainThreadWork(stage, action, retryable);
            _mainThread.Enqueue(() => RunMainThread(work));
        }

        private void RunMainThread(MainThreadWork work)
        {
            if (Time.frameCount < work.NotBeforeFrame)
            {
                _mainThread.Enqueue(() => RunMainThread(work));
                return;
            }
            try { work.Action(); }
            catch (Exception e)
            {
                work.Attempts++;
                CoopPlugin.Log.LogError($"main-thread action '{work.Stage}' failed (attempt {work.Attempts}): {e}");
                if (work.Retryable && work.Attempts <= MaxDispatchRetries)
                {
                    work.NotBeforeFrame = Time.frameCount + 1 + work.Attempts;
                    _mainThread.Enqueue(() => RunMainThread(work));
                }
                else
                    CoopPlugin.Log.LogError($"main-thread action '{work.Stage}' abandoned after {work.Attempts} attempt(s)");
            }
        }

        /// <summary>What one queued message costs against DispatchBudget. Everything is 1 unit
        /// except a CardDeltaBatch, which carries up to CardDeltaBatchMax card applies behind a
        /// single message - charging it 1 made the budget bound message COUNT, not work. Read
        /// off the decoded DTO so nothing is deserialized twice; anything malformed falls back
        /// to 1 and the handler's own bogus-count guard drops it.</summary>
        private static int DispatchCost(InMsg m)
        {
            if (m.Type != MsgType.CardDeltaBatch) return 1;
            int n = m.Message is CardDeltaBatchMessage batch ? batch.Deltas.Count : 0;
            if (n < 1) return 1;
            return n > CardDeltaBatchMax ? CardDeltaBatchMax : n;
        }

        // headless auto-test / shortcut args: -coopautohost=SLOT  -coopautojoin=IP
        private int _autoHostSlot = -1;
        private string _autoJoinIp;
        private int _autoPhase;
        private float _autoTimer;

        private void Awake()
        {
            Instance = this;
            if (Util.PlayerModelStore.TryLoad(out var savedModel))
            {
                _localPlayerModel = savedModel;
                _localPlayerModelReady = true;
                CoopPlugin.Log.LogInfo("loaded local co-op player appearance");
            }
            _messageRouter.Register<PingMessage>((context, message) =>
            {
                context.Transport.Send(context.ConnectionId, new PongMessage());
            });
            _messageRouter.Register<PongMessage>((context, message) => { });
            _messageRouter.Register<EmoteMessage>((context, message) =>
            {
                _avatars.ShowEmote(context.ConnectionId);
                RelayTagToOthers(context.ConnectionId, 0);
            });
            _messageRouter.Register<ActivityMessage>((context, message) =>
            {
                _avatars.ShowTag(context.ConnectionId, "opening a pack!", 3f);
                _avatars.ShowPackOpen(context.ConnectionId, (int)message.Pack);
                RelayTagToOthers(context.ConnectionId, 1, (int)message.Pack);
            });
            _messageRouter.Register<CoinSetMessage>((context, message) =>
            {
                if (!_loggedEconLink)
                {
                    _loggedEconLink = true;
                    CoopPlugin.Log.LogInfo("Economy link active (host wallet mirrored)");
                }
                if (Math.Abs(CPlayerData.m_CoinAmountDouble - message.Coin) > 0.0001)
                    CEventManager.QueueEvent(new CEventPlayer_SetCoin(message.CoinFloat, message.Coin));
            });
            _messageRouter.Register<ProgressSetMessage>((context, message) =>
            {
                int previous = CPlayerData.m_ShopLevel;
                CPlayerData.m_ShopLevel = message.Level;
                CEventManager.QueueEvent(new CEventPlayer_SetShopExp(message.Experience));
                CEventManager.QueueEvent(new CEventPlayer_SetFame(message.Fame));
                if (message.Level > previous)
                    CEventManager.QueueEvent(new CEventPlayer_ShopLeveledUp(message.Level));
            });
            _messageRouter.Register<ShopNameMessage>((context, message) => ApplyShopName(message.Name));
            _messageRouter.Register<RosterMessage>((context, message) => ApplyRoster(message));
            _messageRouter.Register<PlayerStateMessage>((context, message) => ApplyPlayerState(context.ConnectionId, message, true));
            _messageRouter.Register<RelayStateMessage>((context, message) =>
                ApplyPlayerState(1000 + message.SenderId, message.State, false));
            _messageRouter.Register<PlayerModelRequestMessage>((context, message) =>
                ApplyPlayerModelRequest(context.ConnectionId, message));
            _messageRouter.Register<PlayerModelStateMessage>((context, message) =>
                ApplyPlayerModelState(message));
            _messageRouter.Register<EconContributionMessage>((context, message) => ApplyEconomyContribution(context.ConnectionId, message));
            _messageRouter.Register<PurchaseRequestMessage>((context, message) => ApplyPurchaseRequest(context.ConnectionId, message));
            _messageRouter.Register<PurchaseResultMessage>((context, message) => ApplyPurchaseResult(message));
            _messageRouter.Register<SprayHitMessage>((context, message) => ApplySprayHit(message));
            _messageRouter.Register<GradedRemoveMessage>((context, message) => ApplyGradedRemove(context.ConnectionId, message));
            _ui = new UI.CoopUI();
            _world.OnLocalChanges = OnLocalWorldChanges;
            _cardShelves.OnLocalChanges = changes =>
            {
                if (Role == CoopRole.Host)
                    Broadcast(new CardShelfDeltaMessage { Entries = changes });
                else if (Role == CoopRole.Client)
                    Send(1, new CardShelfRequestMessage { Entries = changes });
            };
            _objMoves.OnLocalChanges = changes =>
            {
                if (Role == CoopRole.Host)
                    Broadcast(new ObjMoveDeltaMessage { Entries = changes });
                else if (Role == CoopRole.Client)
                    Send(1, new ObjMoveRequestMessage { Entries = changes });
            };
            _population.OnHostSnapshot = all =>
                Broadcast(new PopStateMessage { Entries = all });
            _boxes.OnHostSnapshot = list =>
                Broadcast(new BoxStateMessage { Entries = list });
            _boxes.OnClientChanges = list =>
                Send(1, new BoxRequestMessage { Entries = list });
            BoxSync.IsLocallyCarried = box =>
            {
                if (_playerIpc == null || box == null) return false;
                try
                {
                    // resolve the held refs once per frame, not per box: this delegate
                    // runs for every box in every apply/tick pass
                    if (_heldBoxFrame != Time.frameCount)
                    {
                        _heldBoxFrame = Time.frameCount;
                        _heldBoxA = FiHoldItemBox?.GetValue(_playerIpc);
                        _heldBoxB = FiHoldBox?.GetValue(_playerIpc);
                        _heldBoxC = FiHoldBoxCard?.GetValue(_playerIpc);
                    }
                    return ReferenceEquals(_heldBoxA, box) || ReferenceEquals(_heldBoxB, box);
                }
                catch { return false; }
            };
            CardBoxSync.IsLocallyCarried = box =>
            {
                if (_playerIpc == null || box == null) return false;
                try
                {
                    // card boxes land in BOTH the generic hold field and the card-box
                    // field (OnEnterHoldBoxMode); reuse the per-frame cached reads
                    if (_heldBoxFrame != Time.frameCount)
                    {
                        _heldBoxFrame = Time.frameCount;
                        _heldBoxA = FiHoldItemBox?.GetValue(_playerIpc);
                        _heldBoxB = FiHoldBox?.GetValue(_playerIpc);
                        _heldBoxC = FiHoldBoxCard?.GetValue(_playerIpc);
                    }
                    return ReferenceEquals(_heldBoxC, box) || ReferenceEquals(_heldBoxB, box);
                }
                catch { return false; }
            };
            BoxSync.LocalBoxDestroyed = box =>
            {
                if (IsTearingDown || !InGameLevel() || ClientReloading) return;
                if (Role == CoopRole.Client) _boxes.NotifyLocalDestroyed(box);
                else if (Role == CoopRole.Host) _boxes.HostNotifyLocalDestroyed();
            };
            _boxes.OnLocalRemoved = (idx, type) =>
                Send(1, new BoxRemovedMessage { Index = idx, ItemType = (EItemType)type });
            PopulationSync.OnClientStructureChanged = kind =>
            {
                if (Role != CoopRole.Client) return;
                // The client's placed-object roster just changed (a shelf was removed or
                // spawned). Every index-keyed mirror's baseline is now keyed against stale
                // indexes, so read the structure back fresh instead of diffing against
                // garbage. Repaired objects start with loader defaults; those defaults must
                // not be reported as guest edits through any index-based mirror.
                _world.Reset();
                _objMoves.Reset();
                if (kind == 2 || kind == 3 || kind == 14)
                    _cardShelves.InvalidateBaseline();
                // container stations (card storage, cleansers, pack openers, box storage,
                // donation boxes) and play tables are index-keyed too; their per-index
                // mirrors/caches go stale the moment a machine/table of that kind shifts.
                if (kind >= 9 && kind <= 13) { _containers.Reset(); _containers.ForceResend(); }
                if (kind == 6) { _tables.Reset(); _tables.ForceResend(); }
                // Population reconciliation removes/inserts list elements, so every
                // index-keyed mirror needs an immediate authoritative snapshot rather than
                // waiting for its normal heal interval.
                _cardBoxes.ForceResend();
                _register.ForceResend();
            };
            _actCardPriceRetry = CardPriceRetryTick;
            _actFrameCardWork = FlushFrameCardWork;
            _actNetPump = () => _net.PumpMainThread();
            _actAvatars = () =>
            {
                AvatarManager.ViewCamera = _playerCamTf; // the camera the player SEES through
                _avatars.Tick(_dt);
            };
            // Population must be broadcast before any index-keyed content state. A shelf
            // can register in the host's list before the client has created its mirror;
            // sending ShelfDelta/CardShelfDelta first makes the client discard the update
            // and the host's diff baseline then prevents it from being sent again.
            _actPopulation = () => { if (Role == CoopRole.Host) _population.HostTick(_dt, _syncActive); };
            _actWorld = () => _world.Tick(_dt, _syncActive);
            _actCardShelves = () =>
            {
                _cardShelves.IsClientRole = Role == CoopRole.Client;
                _cardShelves.Tick(_dt, _syncActive);
            };
            _actObjMoves = () => _objMoves.Tick(_dt, _syncActive);
            _actBoxes = () =>
            {
                if (Role == CoopRole.Host) _boxes.HostTick(_dt, _syncActive);
                else if (Role == CoopRole.Client) _boxes.ClientTick(_dt, _syncActive && !ClientPreloadHold);
            };
            _actNpcPuppets = () => _npcs.TickPuppets(_dt, InGameLevel());
            _actNpcSweep = NpcSweepTick;
            _actStateSend = StateSendTick;
            _actNpcCollect = NpcCollectTick;

            _grading.SendOp = Send(1);
            _grading.BroadcastState = Broadcast;
            _trades.SendOp = Send(1);
            _trades.BroadcastState = Broadcast;
            _tables.BroadcastState = Broadcast;
            _tables.RegisterIntents(_intents);
            _intents.SendOp = Send(1);
            _register.SendOp = Send(1);
            _register.BroadcastState = Broadcast;
            _register.BroadcastCart = Broadcast;
            _staff.SendOp = Send(1);
            _staff.BroadcastState = Broadcast;
            _staff.SendToClient = Send;
            _staff.BroadcastInteraction = Broadcast;
            _shopState.SendOp = Send(1);
            _shopState.BroadcastState = Broadcast;
            _settings.SendOp = Send(1);
            _settings.BroadcastState = Broadcast;
            _market.BroadcastState = Broadcast;
            _report.BroadcastState = Broadcast;
            _containers.SendOp = Send(1);
            _containers.BroadcastState = Broadcast;
            _containers.RequestBoxResync = () => _boxes.ForceBroadcastNextTick();
            _containers.SendToClient = Send;
            _containers.HoldClientBox = TryHoldClientBox;
            _boxes.OnClientBoxCreated = (box, entry) => _containers.TryAutoHoldTakenBox(box, entry);
            _tournament.BroadcastState = Broadcast;
            _tv.SendOp = Send(1);
            _tv.BroadcastState = Broadcast;
            _tv.PeerCount = () => _net == null ? 0 : _net.ConnectionCount;
            _cardBoxes.SendOp = Send(1);
            _cardBoxes.BroadcastState = Broadcast;
            _cardBoxes.SendToClient = Send;
            _furnBoxes.SendOp = Send(1);
            _furnBoxes.BroadcastState = Broadcast;
            FurnBoxSync.IsLocallyCarried = box =>
            {
                if (_playerIpc == null || box == null) return false;
                try
                {
                    // ONLY the generic hold field: the game never nulls
                    // m_CurrentHoldingBoxShelf on set-down, so it lies forever
                    if (_heldBoxFrame != Time.frameCount)
                    {
                        _heldBoxFrame = Time.frameCount;
                        _heldBoxA = FiHoldItemBox?.GetValue(_playerIpc);
                        _heldBoxB = FiHoldBox?.GetValue(_playerIpc);
                        _heldBoxC = FiHoldBoxCard?.GetValue(_playerIpc);
                    }
                    return ReferenceEquals(_heldBoxB, box);
                }
                catch { return false; }
            };
            _actModules = ModulesTick;
            SceneManager.sceneLoaded += OnSceneLoaded;

            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.StartsWith("-coopautohost=") && int.TryParse(arg.Substring(14), out int slot))
                    _autoHostSlot = slot;
                else if (arg.StartsWith("-coopautojoin="))
                    _autoJoinIp = arg.Substring(14);
                else if (arg == "+connect_lobby" && i + 1 < args.Length && ulong.TryParse(args[i + 1], out ulong lob))
                    _autoJoinSteamLobby = lob; // game was launched by accepting a Steam invite
            }
            if (_autoHostSlot >= 0) CoopPlugin.Log.LogInfo($"AUTO: will load slot {_autoHostSlot} and host");
            if (_autoJoinIp != null) CoopPlugin.Log.LogInfo($"AUTO: will join {_autoJoinIp}");
            if (_autoJoinSteamLobby != 0) CoopPlugin.Log.LogInfo($"AUTO: will join Steam lobby {_autoJoinSteamLobby}");

            // Steam, if this build has any. TryCreate returns null - silently, and without
            // ever touching a Steamworks type - on Game Pass / DRM-free installs, and the
            // whole plugin simply runs LAN-only from there. The transport wiring these
            // callbacks used to do now lives inside the bridge (SteamBridgeImpl.Init),
            // because it is Steam-typed; only the Steam-free outcome arrives here.
            _steam = SteamBridge.TryCreate();

            // THE PLATFORM PROMISE IS MADE HERE, NOT IN CoopPlugin.Awake. The startup line up
            // there can only report whether the ASSEMBLY resolved, and 1.0.39's field reports
            // showed that answering TRUE and Steam actually working are different things (a
            // Game Pass install with a stray com.rlabrecque.steamworks.net.dll bundled by
            // another mod). This is the first point in startup that knows the real answer,
            // because TryCreate has now both loaded the type AND probed the native runtime
            // (SteamBridgeImpl's constructor) - so this is where a player is told what they
            // actually have. TryCreate's own warning is deliberately left alone: it carries the
            // exception detail, and these two lines carry the verdict.
            if (_steam == null && PlatformProbe.SteamworksPresent)
            {
                // The assembly loaded but the bridge did not: a stripped Steamworks (Game Pass
                // 0.70 has SteamAPI but no Callback`1) or a complete stray dll with no native
                // steam_api64.dll behind it. Either way Steam is unreachable here and the UI
                // stays hidden - say so plainly rather than leaving the startup line's
                // "assembly detected" as the last word on the subject.
                CoopPlugin.Log.LogInfo("Steamworks assembly present but NOT functional (stripped assembly or no Steam runtime - Game Pass build?) - LAN and direct IP only.");
            }

            if (_steam != null)
            {
                CoopPlugin.Log.LogInfo("Steam bridge ready - lobbies, invites and P2P available.");
                _steam.Init();
                _steam.OnError = err => { ErrorLine = err; CoopPlugin.Log.LogWarning(err); };
                _steam.OnLobbyLive = lobby =>
                {
                    StatusLine = "Hosting via Steam - click 'Invite friend'";
                    // The id is the one thing a support log needs from this line: it is what
                    // the joiner's "+connect_lobby <id>" and invite-accept both name, so
                    // without it the two halves of a failed join cannot be matched up.
                    CoopPlugin.Log.LogInfo("steam: lobby live " + lobby);
                };
                _steam.OnConnectedToHost = () =>
                {
                    // THE ROLE GUARD LIVES HERE NOW (it used to sit beside the transport
                    // wiring, inside the OnEnteredLobby handler). Do not drop it: it is
                    // what stops a stray lobby-enter from starting a handshake while we
                    // are hosting or idle.
                    if (Role != CoopRole.Client) return;
                    StatusLine = "Connected via Steam - requesting world...";
                    SendHello();
                };
                _steam.OnInviteAccepted = lobby =>
                {
                    CoopPlugin.Log.LogInfo("steam: invite accepted -> lobby " + lobby);
                    JoinSteam(lobby);
                };
            }

            CEventManager.AddListener<CEventPlayer_OnOpenCardPack>(OnLocalPackOpened);

            // FIX A-hook: warn ONCE per game session if the on-disk custom-card registry is
            // still a HOST-synced copy from a previous co-op join. The warning itself lives in
            // EnumLendState() now - see there for why Awake is too early to ask. This call is
            // kept only as the fast path for the case where EPL IS already chainloaded: it emits
            // the warning at boot (where a player looking for it expects it) and sets the memo so
            // EnumLendState cannot repeat it.
            try { EnumLendState(); }
            catch { }
        }

        /// <summary>Has the "your card database is the host's" warning already been logged this
        /// process? One-shot memo shared by the Awake fast path and EnumLendState.</summary>
        private static bool _enumLendWarned;

        /// <summary>FIX A-hook: exposed for the co-op UI (CoopUI, owned elsewhere). Returns a
        /// one-line notice when the on-disk enum registry is a host-synced copy - so the UI
        /// can show it and offer a restore button - or null when the registry is the user's
        /// own. The restore itself is Util.ModParity.RestoreEnumBackup(out msg).
        ///
        /// THE LOG WARNING LIVES HERE, NOT IN AWAKE. HostEnumInstalled() short-circuits on
        /// !EplLoaded(), and EplLoaded() probes for EPL's PLUGIN assembly types - which BepInEx
        /// may not have chainloaded yet when our Awake runs. A single call at Awake therefore
        /// answers "no" on exactly the modded machines the warning was written for, and being a
        /// one-shot, the warning was then lost for the whole session. CoopUI polls this every
        /// OnGUI frame, so emitting on the FIRST non-null answer catches it whenever EPL turns
        /// up; the memo keeps it to one line no matter how many frames ask.</summary>
        public static string EnumLendState()
        {
            try
            {
                if (!Util.ModParity.HostEnumInstalled()) return null;
                if (!_enumLendWarned)
                {
                    _enumLendWarned = true;
                    CoopPlugin.Log.LogWarning("CardShopCoop: your custom-card database is currently the HOST's synced copy from a co-op session. Your OWN solo modded saves may not load until you restore it (restore via the co-op window) and RESTART the game.");
                }
                return "custom-card database is the HOST's copy (co-op sync) - solo modded saves may not load; restore via the co-op window";
            }
            catch { return null; }
        }

        public string HostPassword = "";        // required from joiners when non-empty
        private string _joinPassword = "";       // sent in our Hello when joining
        /// <summary>Raw lobby id (0 = none) for the wrong-password retry flow. A ulong, NOT
        /// a CSteamID: a Steamworks STRUCT field here would stop CoopCore loading at all on
        /// a build without the Steamworks assembly.</summary>
        public ulong LastFailedLobby;

        /// <summary>The Steam facade, or NULL when this build has no Steamworks assembly at
        /// all. The UI uses `Steam == null` as its single "hide every Steam control" test.</summary>
        public ISteamBridge Steam => _steam;

        /// <summary>Join a host through Steam (invite accept, browser, +connect_lobby).</summary>
        public void JoinSteam(ulong lobby, string password = "")
        {
            ErrorLine = "";
            if (Role != CoopRole.None) { ErrorLine = "Already in a session."; return; }
            if (InGameLevel()) { ErrorLine = "Go to the main menu first, then accept the invite again."; return; }
            // Two DIFFERENT failures, two different messages: no assembly means Steam can
            // never work on this install (nothing the player can do), whereas the client
            // simply not running is fixable. Never collapse these into one line.
            if (_steam == null) { ErrorLine = "This build has no Steam support - use LAN or direct IP."; return; }
            if (!_steam.SteamAvailable()) { ErrorLine = "Steam isn't running."; return; }
            // FIX C: the card database on disk was replaced by the host's copy this session,
            // but THIS process is still running the old one (the registry is read once at
            // startup). Joining now would hand the host our stale ids and earn another
            // rejection - say so plainly instead of burning a whole handshake on it. Only the
            // INSTALL raises this; restoring your own backup is a solo-save matter and must
            // not cost a second restart before you can join (see ModParity's two flags).
            if (Util.ModParity.RestartRequiredForJoin)
            {
                ErrorLine = "the host's card database was installed on this PC - RESTART the game before joining";
                return;
            }
            Role = CoopRole.Client;
            ActivateLiveModuleHooks();
            GuestBorrowedWorld = true; // block ALL saves until we're back at the title screen
            IsSteamSession = true;
            _joinPassword = password ?? "";
            LastFailedLobby = lobby;
            // ORDER IS LOAD-BEARING: the transport must exist before Join(), because the
            // bridge's lobby-entered callback wires the host connection into it.
            _net = _steam.CreateTransport(false, new PingMessage());
            StatusLine = "Joining Steam lobby...";
            _steam.Join(lobby);
        }

        /// <summary>Host through Steam: friends-only (invite) or public (lobby browser).</summary>
        public void StartHostingSteam(bool isPublic, string lobbyName, string password)
        {
            ErrorLine = "";
            if (Role != CoopRole.None) { ErrorLine = "Already in a session."; return; }
            if (!InGameLevel()) { ErrorLine = "Load your shop first, then host."; return; }
            // Same two-step check as JoinSteam: missing assembly vs. client not running.
            if (_steam == null) { ErrorLine = "This build has no Steam support - use LAN instead."; return; }
            if (!_steam.SteamAvailable()) { ErrorLine = "Steam isn't running - use LAN instead."; return; }
            // a HOST must never translate: drop any table a previous session left behind
            Util.EnumMap.Clear();
            Role = CoopRole.Host;
            ActivateLiveModuleHooks();
            IsSteamSession = true;
            HostPassword = password ?? "";
            // ORDER IS LOAD-BEARING: transport first, then Host() - the bridge's
            // lobby-created callback stamps the new lobby id onto this transport.
            _net = _steam.CreateTransport(true, new PingMessage());
            StatusLine = "Creating Steam lobby...";
            _steam.Host(isPublic, lobbyName, HostPassword.Length > 0);
        }

        public void OpenSteamInvite() { _steam?.OpenInviteDialog(); }

        private void SendHello()
        {
            // Logged on BOTH sides of every Hello (the host logs the pair when it reads
            // them): when a cross-play session misbehaves, the two logs together say
            // immediately whether the two PCs are even on the same game build.
            CoopPlugin.Log.LogInfo($"game build: sending {Application.version} / Unity {Application.unityVersion}");
            byte[] gzEnum;
            try
            {
                var enumRaw = System.Text.Encoding.UTF8.GetBytes(
                    string.Join("\n", Util.ModParity.EnumLines()));
                gzEnum = Msg.Gzip(enumRaw);
            }
            catch (Exception e)
            {
                // never let a registry read failure abort the handshake: an EMPTY blob
                // reads on the host as "no modded entries", which conflicts with nobody -
                // exactly how the old "none" hash failed open.
                CoopPlugin.Log.LogWarning("enum lines for Hello: " + e.Message);
                gzEnum = Msg.Gzip(new byte[0]);
            }
            Send(1, new HelloMessage
            {
                WireVersion = Msg.WireVersion,
                Version = CoopPlugin.Version,
                PlayerName = EffectivePlayerName,
                SteamId = _steam == null ? 0 : _steam.LocalSteamId,
                Password = _joinPassword ?? "",
                PluginHash = Util.ModParity.PluginHash(),
                EnumHash = Util.ModParity.EnumHash(),
                CardsHash = Util.ModParity.CardsHash(),
                // FIX E3: append the actual plugin + custom-card lists so a rejecting host
                // can NAME what differs (a hash alone can't). Cap 256 each.
                PluginList = Util.ModParity.PluginList(),
                CardsList = Util.ModParity.CardsList(),
                EnumBlob = gzEnum,
                GameVersion = Application.version ?? "",
                UnityVersion = Application.unityVersion ?? "",
            });
        }

        /// <summary>FIX C: our own runtime registry lines, never throwing into the handshake.
        /// An empty list means "nothing modded here", which can never conflict.</summary>
        private static List<string> SafeEnumLines()
        {
            try { return Util.ModParity.EnumLines() ?? new List<string>(); }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("enum lines: " + e.Message);
                return new List<string>();
            }
        }

        /// <summary>Our CreateCards/CardForge custom-card identity lines ("MonsterName=id"),
        /// never throwing into the handshake. Empty means "no custom cards here".</summary>
        private static List<string> SafeCardsList()
        {
            try { return Util.ModParity.CardsList() ?? new List<string>(); }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("cards list: " + e.Message);
                return new List<string>();
            }
        }

        /// <summary>The one encoding for a registry blob on the wire, factored out of SendHello
        /// so the host's Welcome blobs are byte-identical in shape to the guest's Hello blob:
        /// gzipped "\n"-joined lines, written as [int gzLen][gz bytes] and read back by
        /// ReadCappedEnumBlob under EnumBlobCap. A read failure yields a gzipped EMPTY blob
        /// rather than aborting: the receiver reads that as "nothing modded", which is the
        /// pre-translation behavior and conflicts with nobody.</summary>
        private static byte[] GzipLines(List<string> lines)
        {
            try
            {
                var arr = (lines ?? new List<string>()).ToArray();
                var raw = System.Text.Encoding.UTF8.GetBytes(string.Join("\n", arr));
                // SAY SO WHEN WE ARE ABOUT TO SEND SOMETHING THE READER WILL THROW AWAY. The
                // reader caps the DECOMPRESSED text at EnumBlobCap, and until now the writer
                // never looked: an over-cap registry simply failed inside GunzipCapped on the
                // far side and was logged there as "no registry - fine if the host is vanilla",
                // which is the single most misleading thing we could say about the most heavily
                // modded host on the network. Still SEND it - the reader degrades to identity,
                // which is what this build did before translation existed - but leave a line in
                // the sender's own log that names the size, because that is the only machine
                // where the fix (fewer content packs, or a bigger cap) can be applied.
                if (raw.Length > EnumBlobCap)
                    CoopPlugin.Log.LogWarning("registry blob is OVER THE WIRE CAP: " + arr.Length + " ids, "
                        + raw.Length + " bytes uncompressed vs a " + EnumBlobCap
                        + "-byte cap - the other PC will IGNORE it and modded ids will not be translated this session (ids must already match)");
                return Msg.Gzip(raw);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("registry blob: " + e.Message);
                return Msg.Gzip(new byte[0]);
            }
        }

        /// <summary>Same as ReadCappedEnumBlob but over a blob the DTO already decoded from
        /// the payload. The DTO carries the raw compressed bytes; here we only split them
        /// into lines and fingerprint the digest.</summary>
        private static List<string> ReadCappedEnumBlob(byte[] gz, out string digest)
        {
            digest = "none";
            var lines = new List<string>();
            try
            {
                if (gz == null || gz.Length == 0) return lines;
                string text = GunzipCapped(gz, EnumBlobCap);
                if (text == null) return lines;
                digest = Fnv(text).ToString("X8");
                foreach (var line in text.Split('\n'))
                {
                    string s = line.Trim();
                    if (s.Length > 0) lines.Add(s);
                }
            }
            catch { }
            return lines;
        }

        /// <summary>FIX C wire cap. The real registry is ~100KB of text / ~7KB gzipped, so a
        /// quarter-megabyte is generous for anything honest and small enough that a malformed
        /// (or hostile) Hello - which arrives BEFORE the peer is accepted - can't make the
        /// host allocate its way into trouble.</summary>
        private const int EnumBlobCap = 256 * 1024;

        /// <summary>Bounded gunzip. Msg.Gunzip grows without limit, which is fine for our own
        /// world transfers (we asked for them) but not for a blob an unaccepted peer hands us.
        /// Returns null when the payload isn't valid gzip or blows past the cap - and LOGS which
        /// of the two it was, because the caller's silent empty-list return is otherwise
        /// indistinguishable from "the peer is vanilla and sent nothing", the exact confusion
        /// that had an over-cap modded host reported as a vanilla one.</summary>
        private static string GunzipCapped(byte[] data, int cap)
        {
            try
            {
                using (var src = new MemoryStream(data, writable: false))
                using (var gz = new System.IO.Compression.GZipStream(src, System.IO.Compression.CompressionMode.Decompress))
                using (var dst = new MemoryStream())
                {
                    var buf = new byte[8192];
                    int n;
                    while ((n = gz.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (dst.Length + n > cap)
                        {
                            // junk or a decompression bomb - but on an honest peer this is simply
                            // a registry bigger than the cap, so name the cap, not the peer.
                            CoopPlugin.Log.LogWarning("registry blob unpacked OVER CAP (more than "
                                + cap + " bytes from " + data.Length + " compressed) - ignored, NOT a vanilla peer");
                            return null;
                        }
                        dst.Write(buf, 0, n);
                    }
                    return System.Text.Encoding.UTF8.GetString(dst.ToArray());
                }
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("registry blob could not be unpacked (" + data.Length
                    + " bytes, not valid gzip: " + e.Message + ") - ignored");
                return null;
            }
        }

        /// <summary>FIX C: the ONLY registry difference that can corrupt a shared world - the
        /// same "Type:Name" bound to DIFFERENT ids on the two machines. Entries only one side
        /// has are NOT a conflict: nobody can spawn what the other doesn't know about, and the
        /// existing catalog-differs warning already tells both players their sets differ. A
        /// side with no modded entries at all conflicts with nobody, which preserves the old
        /// "none" hash guard. Each result reads "Type:Name -&gt; yours &lt;id&gt;, host &lt;id&gt;".</summary>
        private static List<string> EnumConflicts(List<string> theirs, List<string> ours)
        {
            var found = new List<string>();
            if (theirs == null || theirs.Count == 0 || ours == null || ours.Count == 0) return found;
            var theirMap = EnumMap(theirs);
            var ourMap = EnumMap(ours);
            foreach (var kv in theirMap)
            {
                if (ourMap.TryGetValue(kv.Key, out string ourId) && ourId != kv.Value)
                    found.Add($"{kv.Key} -> yours {kv.Value}, host {ourId}");
            }
            found.Sort(StringComparer.Ordinal);
            return found;
        }

        /// <summary>"Type:Name=id" -&gt; { "Type:Name": "id" }. Split on the LAST '=' so a name
        /// containing one still keys correctly; lines without a usable '=' are ignored.</summary>
        private static Dictionary<string, string> EnumMap(List<string> lines)
        {
            var m = new Dictionary<string, string>();
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line)) continue;
                int eq = line.LastIndexOf('=');
                if (eq <= 0 || eq == line.Length - 1) continue;
                m[line.Substring(0, eq)] = line.Substring(eq + 1); // last wins on a dup key
            }
            return m;
        }

        /// <summary>Name up to five conflicting entries in a reject line: a bare count leaves
        /// the player with nothing to search their content packs for.</summary>
        private static string DescribeConflicts(List<string> conflicts)
        {
            const int Max = 5;
            int n = Math.Min(conflicts.Count, Max);
            // "; " between entries: each entry already contains a comma ("yours X, host Y")
            string s = string.Join("; ", conflicts.GetRange(0, n).ToArray());
            if (conflicts.Count > n) s += $" (+{conflicts.Count - n} more)";
            if (s.Length > 400) s = s.Substring(0, 397) + "...";
            return s;
        }

        /// <summary>FIX C: identity for the enum-sync memory below. The Steam id would be
        /// ideal, but the transport keeps its connId-&gt;CSteamID map private - and connId is no
        /// good at all here: a rejected guest is KICKED, restarts the game and comes back on a
        /// fresh connId, which is precisely the round trip the loop-breaker has to recognise.
        /// The player name usually survives it, but it is a free-text config field - blank
        /// (PlayerName cleared) or shared-default names are both real - so the REGISTRY DIGEST
        /// is folded into the key instead of stored as its value. A blank name then falls back
        /// to the digest alone, which still survives the restart, so the loop-breaker fires for
        /// an unnamed player too; and two different registries can never collide onto one key,
        /// so a name collision cannot suppress somebody's first-ever sync. The residual case -
        /// two blank-named peers running the SAME registry - shares a key on purpose: the same
        /// file that couldn't help the first cannot help the second either.</summary>
        private static string PeerSyncKey(string name, string enumDigest)
        {
            string n = (name ?? "").Trim().ToLowerInvariant();
            string d = string.IsNullOrEmpty(enumDigest) ? "none" : enumDigest;
            return (n.Length > 0 ? "n:" + n : "anon") + "|" + d;
        }

        /// <summary>Loop-breaker memory (host only): how many times we have handed our enum file
        /// to a given (peer identity | registry digest) pair. Counted rather than a bare set
        /// since 1.0.36, because the premise changed: syncing genuinely CAN converge (EPL seeds
        /// its ids from the file it finds - see ModParity.EnumFilePath), so a peer who comes back
        /// with the SAME digest has almost certainly not APPLIED the file yet - they never fully
        /// restarted, the write failed, or the host was itself gated - rather than proved the
        /// file useless. One more attempt (EnumSyncMaxSends) is worth far more than the old flat
        /// refusal, and the counter still keeps the original promise: we never tell a player
        /// "restart and it will work" over and over. A peer who genuinely changed their content
        /// packs hashes to a new key and starts fresh. Incremented only after the file actually
        /// went out. Cleared in Shutdown.</summary>
        private readonly Dictionary<string, int> _enumSyncSentTo = new Dictionary<string, int>();

        /// <summary>The SECOND loop-breaker, keyed on the peer NAME alone, and the reason both
        /// keys exist. The digest-bearing key above is the precise one - a peer who genuinely
        /// changed their content packs SHOULD get a fresh budget - but it is only a terminator
        /// while the digest holds still. EPL re-mints ids whenever it hits a collision, so a
        /// guest in that state hashes to a DIFFERENT registry on every single boot, mints a
        /// brand-new key, and gets the full EnumSyncMaxSends budget again: an unbounded
        /// "synced - RESTART - rejoin" loop wearing a bounded counter's clothes. This counter
        /// cannot be shifted by anything on the guest's disk, so it always terminates. Both are
        /// kept because either alone is wrong: name-only would deny a legitimately re-packed
        /// guest their second chance, digest-only never ends.</summary>
        private readonly Dictionary<string, int> _enumSyncSentToPeer = new Dictionary<string, int>();

        /// <summary>How many times the same peer+registry may be sent our enum file before the
        /// terminal message. Two: one to install, and one for the very common "they clicked
        /// straight back to the title screen instead of quitting to desktop".</summary>
        private const int EnumSyncMaxSends = 2;

        /// <summary>Hard ceiling on sends to ONE peer per hosting session, whatever their registry
        /// digest does. Five, so a guest who really is re-minting ids still gets a couple of
        /// honest retries past the per-digest budget before we call it.</summary>
        private const int EnumSyncMaxSendsPerPeer = 5;

        /// <summary>FIX E3: build a precise reject reason by diffing the joiner's list
        /// against the host's. Entries are "key=value" ("guid=version" for plugins,
        /// "MonsterType=ID" for cards). Reports what the joiner is MISSING (host has, they
        /// don't), what they have EXTRA (they have, host doesn't), and same-key value
        /// mismatches. Returns null when the lists are absent or actually agree (hash
        /// differed on something unlisted) so the caller uses its generic wording. Compact,
        /// capped so a big mod set can't produce a wall of text.</summary>
        private static string DescribeModDiff(List<string> theirs, List<string> ours,
            string head, string diffLabel)
        {
            if (theirs == null || theirs.Count == 0 || ours == null || ours.Count == 0)
                return null;

            var theirMap = DiffMap(theirs);
            var ourMap = DiffMap(ours);

            var missing = new List<string>(); // host has, joiner lacks
            var extra = new List<string>();   // joiner has, host lacks
            var valDiff = new List<string>();  // same key, different value
            foreach (var kv in ourMap)
                if (!theirMap.ContainsKey(kv.Key)) missing.Add(kv.Key);
            foreach (var kv in theirMap)
            {
                if (!ourMap.TryGetValue(kv.Key, out var ourVal)) extra.Add(kv.Key);
                else if (ourVal != kv.Value) valDiff.Add($"{kv.Key} (host {ourVal} vs yours {kv.Value})");
            }
            if (missing.Count == 0 && extra.Count == 0 && valDiff.Count == 0)
                return null; // lists agree - the hash differed elsewhere; use generic wording

            missing.Sort(StringComparer.Ordinal);
            extra.Sort(StringComparer.Ordinal);
            valDiff.Sort(StringComparer.Ordinal);

            var parts = new List<string>();
            if (missing.Count > 0) parts.Add("you are missing: " + JoinCapped(missing));
            if (extra.Count > 0) parts.Add("you have extra: " + JoinCapped(extra));
            if (valDiff.Count > 0) parts.Add(diffLabel + ": " + JoinCapped(valDiff));
            string full = head + string.Join(" | ", parts);
            // hard cap so a pathological diff can't overflow the reject line / UI
            if (full.Length > 700) full = full.Substring(0, 697) + "...";
            return full;
        }

        /// <summary>Split "key=value" entries on the FIRST '=' (values may contain '=');
        /// entries without '=' key on the whole string.</summary>
        private static Dictionary<string, string> DiffMap(List<string> entries)
        {
            var m = new Dictionary<string, string>();
            foreach (var e in entries)
            {
                if (string.IsNullOrEmpty(e)) continue;
                int eq = e.IndexOf('=');
                string k = eq > 0 ? e.Substring(0, eq) : e;
                string v = eq > 0 ? e.Substring(eq + 1) : "";
                m[k] = v; // last wins on a dup key - harmless for a diagnostic
            }
            return m;
        }

        /// <summary>Comma-join with a per-clause char budget; overflow becomes "(+N more)".</summary>
        private static string JoinCapped(List<string> items)
        {
            var sb = new System.Text.StringBuilder();
            int shown = 0;
            for (int i = 0; i < items.Count; i++)
            {
                string next = (shown > 0 ? ", " : "") + items[i];
                if (shown > 0 && sb.Length + next.Length > 220) break;
                sb.Append(next);
                shown++;
            }
            if (shown < items.Count) sb.Append($" (+{items.Count - shown} more)");
            return sb.ToString();
        }

        // Bye must actually reach the peer before the connection dies; on Steam, sends
        // drain on later frames, so the kick is deferred a moment.
        private readonly List<KeyValuePair<int, float>> _pendingKicks = new List<KeyValuePair<int, float>>();

        private void RejectConn(int connId, string reason)
        {
            CoopPlugin.Log.LogWarning($"rejected connection {connId}: {reason}");
            Send(connId, new ByeMessage { Reason = reason });
            _pendingKicks.Add(new KeyValuePair<int, float>(connId, 1.5f));
        }

        // card/price mirrors that arrived during a scene load, flushed once in-game
        private struct PendingCard { public bool IsAdd; public int Amount; public CardData Card; }
        private readonly List<PendingCard> _pendingCardDeltas = new List<PendingCard>();
        private readonly List<KeyValuePair<CardData, float>> _pendingCardPrices = new List<KeyValuePair<CardData, float>>();

        // OUTGOING card deltas leave through a per-frame outbox instead of one reliable frame
        // each. A "collect all machines" click fires 300-1300 AddCard/ReduceCard calls in ONE
        // frame; that many individual CardDelta frames swamped the reliable lane (SteamNet
        // drops a frame Steam refuses 30 frames running) - which is exactly how a guest's card
        // price edit went missing. Flushed at the end of Update, and by the send helpers before
        // any OTHER message goes out so today's global ordering is preserved.
        private readonly List<PendingCard> _cardDeltaOutbox = new List<PendingCard>();
        private const int CardDeltaBatchMax = 200; // deltas per CardDeltaBatch frame
        private bool _flushingCardDeltas;          // re-entrancy guard for the send-helper hook
        private readonly List<PendingCard> _batchRelayBuf = new List<PendingCard>();

        // ONE binder relayout per frame, not per delta: with the book open RefreshOpenBinder
        // invokes the game's OnSortingMethodUpdated (O(N^2) re-sort + 72-slot UI rebuild +
        // album total recompute), which per delta is the reported 20-30s freeze.
        private static bool _binderRefreshPending;

        // The per-delta apply line is the field-log diagnosis for "cards didn't show up in the
        // binder", so it survives verbatim for ordinary changes (<=5 applied in a frame) and
        // folds into one summary line for a flood. Emitted by FlushFrameCardWork.
        private static readonly List<PendingCard> _deltaLogBuf = new List<PendingCard>();
        private static int _deltaAppliedThisFrame;

        /// <summary>A card price WE set locally that the other side has not confirmed yet.
        /// Card prices had NO ack and NO retry: a single dropped reliable frame stranded the
        /// edit, and the host's 3s price heal then broadcast its own stale value back over it.</summary>
        private struct MyCardPrice
        {
            public CardData Card;   // snapshot: the postfix restores the live object's grade
            public float Value;
            public bool Acked;
            public double LastSend;
            public int Attempts;
        }
        private readonly Dictionary<string, MyCardPrice> _myCardPrices = new Dictionary<string, MyCardPrice>();
        private readonly List<string> _cardPriceRetryKeys = new List<string>(); // scratch: no mutate-while-iterating
        private float _cardPriceRetryTimer;
        private const int MyCardPriceMax = 1024;
        private const int CardPriceMaxAttempts = 12;
        /// <summary>Price-equality tolerance. Strictly ABOVE half a display quantum (0.005)
        /// plus float error, and still far below the smallest price step anyone cares about.
        /// The game's price store legitimately rounds by up to exactly half a quantum, and the
        /// quantum depends on each machine's LOCAL currency setting (2dp vs 3dp) - so a
        /// cross-currency pair landed EXACTLY on the old 0.005 and the ack test at the
        /// CardPriceSet handler became deterministically unreachable: every edit burned all 12
        /// retries and then falsely surrendered.</summary>
        private const float CardPriceEpsilon = 0.0075f;

        /// <summary>An item price WE just set. The host's PriceList is a full-table overwrite
        /// built BEFORE our ItemPriceContrib landed, so for a few seconds it would repaint our
        /// fresh price back to the old one. Bulk host state, so a recency window is enough.</summary>
        private struct MyItemPrice { public float Value; public double At; }
        private readonly Dictionary<int, MyItemPrice> _myItemPriceEdits = new Dictionary<int, MyItemPrice>();
        private const int MyItemPriceMax = 256;
        private const double ItemPriceHoldSeconds = 6.0;

        private static InteractionPlayerController _deltaIpc; // NEVER CSingleton<>.Instance (fake-manager landmine)

        /// <summary>Returns true when the delta was actually applied - the host's relay to
        /// OTHER guests keys off this, so a delta this side REFUSED (corrupt grade, would-go-
        /// negative registry mismatch) is never propagated onward and can't spread divergence.
        /// <paramref name="relayAnyway"/> separates "THIS PC lacks the content" from "this delta
        /// is garbage", exactly as the price path does: identical registries do NOT imply
        /// identical installed data (EPL seeds enum ids from enum_values.json even for bundles
        /// that aren't installed), so the host can fully RESOLVE a card it has no data row for.
        /// Refusing it locally is right; swallowing it is not - a third player who DOES have the
        /// pack must still receive it, which is what the 3+ player regression was. True for the
        /// CardSetInstalledHere refusal, for the graded-remove album mismatch and for the
        /// suppressed follow-up add that pairs with one; false for a corrupt grade, a
        /// would-go-negative reduce, and (via the callers' catch) any throw.</summary>
        private static bool ApplyCardDelta(bool isAdd, int amount, CardData card, out bool relayAnyway)
        {
            relayAnyway = false;
            // A cardGrade > 10 is NOT corruption when Grading Overhaul is installed: it's an
            // ENCODED grade (company + 1-10 grade + cert serial). The old hard 1-10 drop-guard
            // discarded every real graded card. Only a >10 grade WITHOUT Grading Overhaul is
            // impossible/genuine corruption (vanilla only writes 1-10), so still refuse that.
            if (card.cardGrade != 0 && (card.cardGrade < 1 || card.cardGrade > 10) && !Util.GradingInterop.Present)
            {
                CoopPlugin.Log.LogWarning($"card delta: dropping corrupt graded card {CardIdent(card)} (grade {card.cardGrade}) - not applied (Grading Overhaul absent)");
                return false;
            }
            Patches.GamePatches.ApplyingRemoteCards = true;
            try
            {
                // UNKNOWN-CARD guard - the mirror of the negative-reduce guard below, and the
                // price of tolerating extra registry entries in the handshake (FIX C): a card
                // can now arrive for a content pack THIS PC doesn't have. Every branch below
                // resolves the card's slot through CPlayerData.GetCardSaveIndex, whose loop over
                // InventoryBase.GetShownMonsterList simply leaves the index at 0 when the monster
                // isn't in the list - and GetShownMonsterList itself falls back to the TETRAMON
                // list for an expansion outside the vanilla switch. So on the vanilla path an
                // unknown card doesn't error: it silently credits save index 0, i.e. the
                // receiver's FIRST Tetramon card, quietly inflating a real card's count (and,
                // for a graded one, filing a bogus entry in the graded album). Under EPL the same
                // lookup is an IndexOf that returns -1, and CardCountList[-1] THROWS.
                // Guards ALL THREE branches (it used to sit inside the add arm only, leaving the
                // graded-remove and ungraded-reduce paths to reach GetCardSaveIndex unguarded).
                // Returns false so the host's relay doesn't spread it.
                if (!CardSetInstalledHere(card))
                {
                    // RELAY ANYWAY: the delta is well-formed, this PC just has no data row for
                    // that card. Other peers may well have the pack, and before 1.0.37 this
                    // case reached the ungraded-reduce arm, returned true (a vanilla no-op) and
                    // so kept relaying - dropping the relay is what broke 3+ player sessions.
                    relayAnyway = true;
                    // Memoized per card key, like the price path: one host log showed 995
                    // identical lines. Printed through CardIdent because an ordinary modded id
                    // is a small ORDINAL: below ~122 the bare monsterType renders an unrelated
                    // VANILLA name, at 123+ it renders a bare number (EMonsterType has no
                    // members up there) - neither identifies the card without the expansion.
                    if (_priceWarnedKeys.Add("delta:" + CardPriceKey(card)))
                        CoopPlugin.Log.LogWarning($"card delta: {CardIdent(card)} is from a card set you don't have installed - skipped");
                    return false;
                }
                if (isAdd)
                {
                    // PAIRED-ADD SUPPRESSION. A graded remove this PC could not satisfy, followed
                    // seconds later by an add of the SAME key, is one gesture on the sender: the
                    // card came out of the album into their hand / a grading submit slot and went
                    // straight back (GradedCardSubmitSelectScreen.OnCloseScreen AddCards every
                    // occupied slot in ONE frame, decompiled :84-95). Their net change is ZERO -
                    // they still own exactly one copy. Applying only the ADD half therefore
                    // MANUFACTURES a copy here. It looked like a heal because sometimes the
                    // absence was a real deficit, but that is a coin flip on state neither side
                    // can see, and when the cert is already present here in mutated form GO's
                    // duplicate-cert sweep (decompiled-grading :8532-8582) answers the add by
                    // flagging BOTH rows FAKE - so the "heal" corrupts a card that was fine.
                    // Relay-anyway rather than a silent drop, by the same rule as the
                    // CardSetInstalledHere case above: a third peer that genuinely owns the pair
                    // must still receive it.
                    if (card.cardGrade > 0 && ConsumeGradedRemoveSkip(card))
                    {
                        CoopPlugin.Log.LogWarning($"graded add suppressed: {CardIdent(card)} (grade {card.cardGrade}) pairs with the remove this PC skipped moments ago - the sender only MOVED a card they still own, and this PC never had that copy, so applying the add alone would create one out of nothing");
                        relayAnyway = true;
                        return false;
                    }
                    // Register the host's cert with Grading Overhaul BEFORE AddCard, so its
                    // anti-cheat AddCard prefix sees the cert burned+bound and does NOT
                    // re-encode this card as FAKE (the ~20s changing-grade churn). BindCert
                    // is keyed by cardSaveIndex/expansion/isDestiny, so it survives AddCard's
                    // compaction into a fresh CompactCardDataAmount.
                    if (card.cardGrade > 10) Util.GradingInterop.Remember(card);
                    CPlayerData.AddCard(card, amount);
                }
                else if (card.cardGrade > 0)
                {
                    // graded cards live in m_GradedCardInventoryList; ReduceCard would miss
                    // them and wrongly decrement the ungraded array. Route through
                    // RemoveGradedCard (the graded-remove mirror normally arrives as
                    // MsgType.GradedRemove; this defends the CardDelta path too).
                    // Count what actually came out: removing NOTHING (album never had it -
                    // the graded analog of the registry mismatch below) must report
                    // not-applied so the host relay doesn't propagate a remove we refused.
                    int removed = 0;
                    for (int i = 0; i < amount && CPlayerData.HasGradedCardInAlbum(card); i++)
                    {
                        CPlayerData.RemoveGradedCard(card, ignoreGradedCardIndex: true);
                        removed++;
                    }
                    if (removed == 0)
                    {
                        RecordGradedRemoveSkip(card);
                        CoopPlugin.Log.LogWarning($"graded remove: {CardIdent(card)} (grade {card.cardGrade}) not in this album - skipped (album mismatch?)");
                        // RELAY ANYWAY, by the same argument that gave CardSetInstalledHere its
                        // relay above: THIS album saying nothing about a card says nothing about
                        // a THIRD peer's album. Before this the GradedRemove handler broke without
                        // relaying and the third player kept a ghost copy forever. No effect on a
                        // 2-player session.
                        relayAnyway = true;
                        return false;
                    }
                    // A remove for this key SUCCEEDED, so whatever the album was missing it is not
                    // missing now: drop the skip memo, or the NEXT legitimate re-add of the same
                    // card would be suppressed on the strength of a stale one.
                    _gradedRemoveSkipped.Remove(CardPriceKey(card));
                }
                else
                {
                    // Negative-reduce guard: a remove that outruns what this side actually owns
                    // silently underflows the collected-count array (ReduceCard just subtracts),
                    // which is the slow "total value drifts" leak. GetCardAmount resolves the
                    // owned count through the SAME GetCardSaveIndex + per-expansion collected list
                    // that ReduceCard decrements, so it's the exact amount the apply would hit.
                    // (Graded cards - cardGrade > 10 - never reach here; they route through
                    // RemoveGradedCard above.)
                    // No null-collected-list arm here any more: CardSetInstalledHere above owns
                    // that case (it refuses when GetCardCollectedList is null) and relays it on.
                    int owned = CPlayerData.GetCardAmount(card);
                    if (owned < amount)
                    {
                        CoopPlugin.Log.LogWarning($"card delta would drive {CardIdent(card)} negative (have {owned}, remove {amount}) - skipped (card registry mismatch?)");
                        return false;
                    }
                    CPlayerData.ReduceCard(card, amount);
                }
            }
            finally { Patches.GamePatches.ApplyingRemoteCards = false; }
            // "cards didn't show up in the binder" reports were undiagnosable from the
            // receiving side - applies were completely silent. The line still goes out for an
            // ordinary change; a bulk collect (hundreds of deltas in one frame) folds into one
            // summary instead of its own log flood. Both are emitted by FlushFrameCardWork.
            _deltaAppliedThisFrame++;
            if (_deltaLogBuf.Count < 5)
                _deltaLogBuf.Add(new PendingCard { IsAdd = isAdd, Amount = amount, Card = SnapshotCard(card) });
            // Deferred to the end of the frame: RefreshOpenBinder is O(N^2) re-sort + full UI
            // rebuild whenever the book is open, and running it per delta is the 20-30s freeze.
            _binderRefreshPending = true;
            return true;
        }

        /// <summary>Shared by the CardDelta and CardDeltaBatch handlers: hold the delta if a
        /// scene load is in flight (applying mid-load crashes into uninitialized card data;
        /// nothing is lost, FlushPendingCardWork replays it), otherwise apply it. Returns true
        /// only when it was actually applied - i.e. when it may be relayed onward - and reports
        /// through <paramref name="relayAnyway"/> the "this PC lacks the content, but the delta
        /// is fine" refusal that must still be forwarded (see ApplyCardDelta).</summary>
        private bool ApplyOrHoldCardDelta(bool isAdd, int amount, CardData card, out bool relayAnyway)
        {
            relayAnyway = false;
            if (!InGameLevel())
            {
                // HELD, not relayAnyway. Note this is NOT "it will relay later": the replay in
                // FlushPendingCardWork applies without relaying, so a delta held across a scene
                // load never reaches the other guests. That is pre-existing 1.0.36 behavior and
                // is deliberately left alone here - the relay-anyway work is about content this
                // PC lacks, not about the load window.
                _pendingCardDeltas.Add(new PendingCard { IsAdd = isAdd, Amount = amount, Card = card });
                return false;
            }
            return ApplyCardDelta(isAdd, amount, card, out relayAnyway);
        }

        /// <summary>A private copy of exactly the nine fields the wire carries. Anything that
        /// DEFERS a send must snapshot: AddCardPostfix/SetCardPricePostfix temporarily write the
        /// ENCODED grade into the game's live cardData and restore it in a finally, so reading
        /// the same object a frame later would ship the bare 1-10 grade instead.</summary>
        private static CardData SnapshotCard(CardData c)
        {
            return new CardData
            {
                expansionType = c.expansionType,
                monsterType = c.monsterType,
                borderType = c.borderType,
                isFoil = c.isFoil,
                isDestiny = c.isDestiny,
                isChampionCard = c.isChampionCard,
                isNew = c.isNew,
                cardGrade = c.cardGrade,
                gradedCardIndex = c.gradedCardIndex,
            };
        }

        /// <summary>Canonical identity of a card's MARKED PRICE - everything the price store
        /// keys on and nothing else (gradedCardIndex is a per-copy serial, isNew is cosmetic).
        /// Used both as the in-flight-edit key and as the human-readable id in the price logs,
        /// which is why it is a string rather than a packed hash.</summary>
        private static string CardPriceKey(CardData card)
        {
            if (card == null) return null;
            return (int)card.expansionType + ":" + (int)card.monsterType + ":" + (int)card.borderType
                + ":" + (card.isFoil ? 1 : 0) + (card.isDestiny ? 1 : 0) + (card.isChampionCard ? 1 : 0)
                + ":" + card.cardGrade;
        }

        /// <summary>Per-expansion set of the monster ids that genuinely have a data row on THIS
        /// machine, taken from InventoryBase.GetShownMonsterList - the one list EPL prefixes, so
        /// it reports the expansion's real card keys on the modded path and the vanilla ones on
        /// the vanilla path. Built lazily and kept for the session (the shown lists are
        /// ScriptableObject content: they do not change while the game runs).</summary>
        private static readonly Dictionary<ECardExpansionType, HashSet<EMonsterType>> _shownMonsters =
            new Dictionary<ECardExpansionType, HashSet<EMonsterType>>();

        /// <summary>Drop the shown-monster cache. Called from Shutdown beside EnumMap.Clear():
        /// the next session may load a different save/content set, and a stale membership set
        /// would either refuse cards this install now has or admit ones it doesn't.</summary>
        internal static void ClearCardSetCache()
        {
            _shownMonsters.Clear();
        }

        /// <summary>Graded removes this PC could NOT satisfy, keyed exactly as the album matches
        /// (<see cref="CardPriceKey"/> already carries expansion, monster, border, foil, isDestiny
        /// and the encoded grade - a superset of RemoveGradedCard's predicate) and stamped with
        /// realtimeSinceStartup. Read once, by the add arm, to recognise the second half of a
        /// stage-then-abandon gesture. Small and short-lived on purpose: it is a pairing hint, not
        /// state.</summary>
        private static readonly Dictionary<string, float> _gradedRemoveSkipped = new Dictionary<string, float>();
        /// <summary>Observed pair gaps in the field log run 3.2 / 4.9 / 10.2 / 13 / 19s, so 20s is
        /// already too tight - and 60s is still nowhere near "took it off the shelf again later".</summary>
        private const float GradedSkipWindow = 60f;
        private const int GradedSkipMax = 64;

        /// <summary>Cleared beside <see cref="ClearCardSetCache"/> on disconnect and on every
        /// scene load: a pairing hint from a dead session (or a different world) describes an
        /// album that no longer exists, and acting on it would suppress a legitimate add.</summary>
        internal static void ClearGradedSkipMemory()
        {
            _gradedRemoveSkipped.Clear();
            Util.GradingInterop.Reset();
        }

        private static void RecordGradedRemoveSkip(CardData card)
        {
            string key = CardPriceKey(card);
            if (key == null) return;
            if (_gradedRemoveSkipped.Count >= GradedSkipMax && !_gradedRemoveSkipped.ContainsKey(key))
            {
                string oldest = null;
                float at = float.MaxValue;
                foreach (var kv in _gradedRemoveSkipped)
                    if (kv.Value < at) { at = kv.Value; oldest = kv.Key; }
                if (oldest != null) _gradedRemoveSkipped.Remove(oldest);
            }
            _gradedRemoveSkipped[key] = Time.realtimeSinceStartup;
        }

        /// <summary>True when a skipped graded remove for this exact key is still inside the
        /// window. Always CONSUMES the memo (expired or not) - it has done its one job either
        /// way, and leaving stale keys behind would just burn the 64 slots.</summary>
        private static bool ConsumeGradedRemoveSkip(CardData card)
        {
            string key = CardPriceKey(card);
            if (key == null) return false;
            float at;
            if (!_gradedRemoveSkipped.TryGetValue(key, out at)) return false;
            _gradedRemoveSkipped.Remove(key);
            return Time.realtimeSinceStartup - at <= GradedSkipWindow;
        }

        /// <summary>Membership test: does a data row for this monster exist under this expansion
        /// on this machine? Fills the cache on first ask, but NEVER caches a null-or-empty list -
        /// that means "InventoryBase isn't ready yet" (pre-load, or mid scene swap), and latching
        /// it would turn a timing miss into a permanent refusal for the rest of the session.</summary>
        private static bool MonsterHasDataRowHere(ECardExpansionType expansion, EMonsterType monster)
        {
            // FABRICATED-SINGLETON GATE. InventoryBase.GetShownMonsterList reads
            // CSingleton<InventoryBase>.Instance, and that getter does NOT return null when the
            // real inventory is absent (client reload window - InGameLevel() stays true there):
            // it FABRICATES one (new GameObject + AddComponent + DontDestroyOnLoad) and caches
            // it forever, so the fake permanently shadows the real inventory for the rest of the
            // run. Same house rule as everywhere else in this file - see the comment above Inv()
            // (~"NEVER CSingleton<>.Instance for scene-lifetime managers"). Asking Inv() first
            // (FindObjectOfType, fabricates nothing) both avoids that and makes the no-latch
            // not-ready refusal below actually reachable: without it this window threw an NRE
            // out of the fake's empty fields and landed in CardSetInstalledHere's catch.
            if (Inv() == null) return false; // not ready - do not latch, do not fabricate
            HashSet<EMonsterType> set;
            if (!_shownMonsters.TryGetValue(expansion, out set))
            {
                var shown = InventoryBase.GetShownMonsterList(expansion);
                if (shown == null || shown.Count == 0) return false; // not ready - do not latch
                set = new HashSet<EMonsterType>(shown);
                _shownMonsters[expansion] = set;
            }
            return set.Contains(monster);
        }

        /// <summary>True when THIS install can actually place the card - i.e. a data row for
        /// (expansion, monster) really exists here. Anything else would mis-index through
        /// GetCardSaveIndex/GetShownMonsterList into save slot 0 (silent album corruption on the
        /// vanilla path) or into CardCountList[-1] (a throw on the EPL path), so it is refused.
        /// Errs toward REFUSING on any throw.
        ///
        /// TWO ORACLES THAT LOOK RIGHT AND ARE NOT - do not reinstate either:
        ///
        ///  1. Enum.IsDefined(typeof(EMonsterType), ...). EPL never MINTS EMonsterType members.
        ///     A modded expansion numbers its cards as plain ORDINALS - (EMonsterType)(index+1),
        ///     1..N - so the ids collide with whatever vanilla names happen to sit at those
        ///     numbers. That split the pack's own cards in two, which is what made the field
        ///     reports so confusing: ordinals 1..122 PASSED IsDefined by pure numeric collision
        ///     and synced SILENTLY (they never reached the refusal log at all, and they landed
        ///     in the save slot of the colliding vanilla card); ordinals 123 and up failed the
        ///     check on EVERY machine - including both players' - and were universally refused,
        ///     logging as BARE NUMBERS because EMonsterType simply has no members up there.
        ///     So "some of the modded cards work" was the collision half, and the missing cards
        ///     were the 123+ half. Note the ECardExpansionType half of the check IS legitimate -
        ///     expansions genuinely ARE EPL-minted enum members - which is exactly why the two
        ///     halves look symmetric and are not.
        ///
        ///  2. InventoryBase.GetMonsterData(...) != null. EPL does not patch that method; it
        ///     rewrites the game's own CALL SITES with a transpiler. A third-party caller like
        ///     this mod runs the ORIGINAL body, which for a modded id returns null or - worse -
        ///     the wrong vanilla monster's data by collision.
        ///
        /// The oracle that holds on both paths is per-expansion MEMBERSHIP in
        /// GetShownMonsterList, which EPL prefixes with the expansion's real card keys:
        /// membership means "a data row exists here", which is precisely the question.</summary>
        internal static bool CardSetInstalledHere(CardData card)
        {
            try
            {
                if (card == null) return false;
                // Refuse the None sentinels explicitly. Since 1.0.37 an incoming modded id whose
                // NAME does not exist on this PC is translated to the game's own None member
                // (Util.EnumMap.FromWire) instead of arriving as a foreign number, and None is a
                // DEFINED member of both enums (ECardExpansionType.None = -1, EMonsterType.None
                // = 0). Neither value names a real card, so refusing them costs nothing.
                if (card.expansionType == ECardExpansionType.None) return false;
                if (card.monsterType == EMonsterType.None) return false;
                // Expansions ARE EPL-minted enum members, so IsDefined is the correct oracle
                // HERE (and only here - see the doc comment above).
                if (!Enum.IsDefined(typeof(ECardExpansionType), card.expansionType)) return false;
                // ...and the expansion must still be one this save actually has a collected list
                // for. Without EPL's interceptor woven in, GetCardCollectedList returns null for
                // an out-of-vocabulary expansion, and every downstream lookup would fall through
                // GetShownMonsterList's default arm onto the TETRAMON list - the slot-0 mis-index.
                if (CPlayerData.GetCardCollectedList(card.expansionType, card.isDestiny) == null) return false;
                return MonsterHasDataRowHere(card.expansionType, card.monsterType);
            }
            catch (Exception e)
            {
                // MEMOIZED like every other per-card warning here. This catch used to be
                // effectively dead (the not-ready window NRE'd elsewhere); now that the
                // fabricated-singleton gate in MonsterHasDataRowHere returns cleanly, anything
                // that still throws here throws on EVERY card - and this path runs up to
                // CardDeltaBatchMax times per frame during a collect-all burst.
                if (_priceWarnedKeys.Add("check:" + CardPriceKey(card)))
                    CoopPlugin.Log.LogWarning("card set check: " + e.Message);
                return false;
            }
        }

        /// <summary>Human-readable card id for the log lines that can now carry MODDED cards.
        /// A modded expansion numbers its cards as plain ORDINALS (1..N), so for anything at or
        /// past the vanilla expansion range the bare monsterType renders a completely unrelated
        /// vanilla member NAME by numeric collision - which is worse than useless in a field log.
        /// Vanilla expansions keep the readable name; modded ones print Expansion#N.
        ///
        /// None (= -1, NOT a missing member) needs its own arm: it is numerically BELOW MAX, so
        /// the vanilla branch used to claim it and render whatever monster name collides with that
        /// ordinal - a confidently wrong card name on exactly the rows where the expansion is the
        /// thing that failed to resolve (a card off the wire from a pack this PC lacks, or one
        /// whose expansion id did not map). Naming the unknown beats naming the wrong card.</summary>
        private static string CardIdent(CardData c)
        {
            if (c == null) return "(null card)";
            if (c.expansionType == ECardExpansionType.None) return "unknown-pack card #" + (int)c.monsterType;
            if ((int)c.expansionType < (int)ECardExpansionType.MAX) return c.monsterType.ToString();
            return c.expansionType + "#" + (int)c.monsterType;
        }

        /// <summary>Shared "this PC can't process that card" warning for the sites that have no
        /// choice but to SKIP a card outright (grade-return, card-box collect) - unlike the delta
        /// path there is no relay to fall back on, so the card is genuinely lost here and a silent
        /// `continue` left zero trace in the field logs. Memoized per (context, card) on the same
        /// set the price/delta warnings use: a rejected 300-card grading submission would
        /// otherwise print 300 lines.</summary>
        internal static void WarnRefusedCard(CardData c, string context)
        {
            if (c == null) return;
            if (_priceWarnedKeys.Add(context + ":" + CardPriceKey(c)))
                CoopPlugin.Log.LogWarning($"{context}: {c.expansionType}#{(int)c.monsterType} is from a card set this PC doesn't have - the card could NOT be processed here");
        }

        /// <summary>Keys already warned about by ApplyRemoteCardPrice, once per session. The
        /// price heal rebroadcasts every displayed card at least every 30s, and one-sided
        /// content packs are ALLOWED (1.0.34) - without this memo an hours-long session logs
        /// the same "unknown card set" / "store did not accept" line thousands of times.</summary>
        private static readonly HashSet<string> _priceWarnedKeys = new HashSet<string>();

        /// <summary>Apply a received card price. For an ENCODED (>10) graded grade, register the
        /// card with Grading Overhaul first (so its price-store key matches) and let GO's own
        /// SetCardPrice patch route the write into its store; without GO, skip - the vanilla
        /// 10-slot price array can't index an encoded grade. Callers hold ApplyingRemotePrice.
        /// Returns true when the price is actually STORED here (the host's ack/echo keys off
        /// this), and reports through <paramref name="actual"/> the value the game's price store
        /// really ended up holding - vanilla SetCardPrice is a hardcoded six-expansion if-chain
        /// that silently no-ops for a modded expansion, so "applied" used to mean nothing.
        /// <paramref name="relayAnyway"/> separates "THIS machine can't hold this price" from
        /// "this price is garbage": the message is well-formed and other peers may well have
        /// the content pack / a working store, so the host must still forward it (and the
        /// sender still needs its ack). False only when the price itself is unusable.</summary>
        private static bool ApplyRemoteCardPrice(CardData card, float price, string from, out float actual, out bool relayAnyway)
        {
            actual = price;
            relayAnyway = false;
            if (card == null) return false; // nothing to relay
            string key = CardPriceKey(card);
            // RESOLVABILITY FIRST, on the same runtime-enum oracle the card-delta guard uses:
            // an id this process has never heard of mis-indexes through GetShownMonsterList's
            // Tetramon fallback and would price somebody ELSE'S card (save slot 0).
            if (!CardSetInstalledHere(card))
            {
                relayAnyway = true; // one-sided content pack: the OTHER peers may well have it
                if (_priceWarnedKeys.Add("set:" + key))
                    CoopPlugin.Log.LogWarning($"card price for unknown card set skipped - other side has a content pack this PC doesn't ({key}; further ones logged once each)");
                return false;
            }
            if (card.cardGrade > 10)
            {
                // modded grade, no grading mod HERE: can't price, but a peer that has Grading
                // Overhaul can, and the sender is still waiting on its ack
                if (!Util.GradingInterop.Present) { relayAnyway = true; return false; }
                Util.GradingInterop.Remember(card);             // bind cert so GO's price-store key matches
            }
            float before = float.NaN;
            try { before = CPlayerData.GetCardPrice(card); } catch { }
            try { CPlayerData.SetCardPrice(card, price); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("card price apply: " + e.Message); return false; }
            // READ-BACK, the same GetCardPrice the host's price heal reads: truth on the wire
            // lets the sender either converge on it or surface its own give-up warning.
            try { actual = CPlayerData.GetCardPrice(card); }
            catch (Exception e)
            {
                // memoized like the other price warnings: a store whose GetCardPrice throws
                // throws EVERY heal beat, which used to spam this line forever
                if (_priceWarnedKeys.Add("read:" + key))
                    CoopPlugin.Log.LogWarning("card price read-back: " + e.Message);
                actual = price;
            }
            if (Math.Abs(actual - price) > CardPriceEpsilon)
            {
                // F4, not F2: a rounding-sized mismatch printed as two IDENTICAL strings, so
                // the one line that explains the failure read as nonsense
                if (_priceWarnedKeys.Add("store:" + key))
                    CoopPlugin.Log.LogWarning($"card price {key}: the game's price store did not accept {price:F4} (it holds {actual:F4}) - modded expansion? (logged once per card)");
                // The store REJECTED the value. Echoing the read-back would push this
                // machine's stale/zero price onto every other guest (the reported "prices
                // reset to 0"), so this is not an apply - but peers with a working store
                // should still get the original, and the sender still needs its ack.
                relayAnyway = true;
                return false;
            }
            if (float.IsNaN(before) || Math.Abs(before - actual) > CardPriceEpsilon)
                CoopPlugin.Log.LogInfo($"card price applied: {key} = {actual:F2} (from {from})"); // this path was invisible in field logs
            return true;
        }

        // NEVER CSingleton<>.Instance (fake-manager landmine); cached, Unity re-resolves.
        private static readonly System.Reflection.MethodInfo MiBinderResort =
            HarmonyLib.AccessTools.Method(typeof(CollectionBinderFlipAnimCtrl), "OnSortingMethodUpdated");
        private static readonly System.Reflection.FieldInfo FiBinderIsBookOpen =
            HarmonyLib.AccessTools.Field(typeof(CollectionBinderFlipAnimCtrl), "m_IsBookOpen");
        // Extra binder internals we read to recompute the OPEN album's total-value text after a
        // card delta - the open path (CollectionBinderFlipAnimCtrl.Update ~807-830) branches on
        // these to pick which SetTotalValue variant to call. All private, so AccessTools-cached.
        private static readonly System.Reflection.FieldInfo FiBinderUI =
            HarmonyLib.AccessTools.Field(typeof(CollectionBinderFlipAnimCtrl), "m_CollectionBinderUI");
        private static readonly System.Reflection.FieldInfo FiBinderIsGradedAlbum =
            HarmonyLib.AccessTools.Field(typeof(CollectionBinderFlipAnimCtrl), "m_IsGradedCardAlbum");
        private static readonly System.Reflection.FieldInfo FiBinderExpansionType =
            HarmonyLib.AccessTools.Field(typeof(CollectionBinderFlipAnimCtrl), "m_ExpansionType");

        /// <summary>Make an ALREADY-OPEN collection binder re-lay-out after a card change.
        /// SetCanUpdateSort alone only ARMS a gate the vanilla per-frame Update never
        /// consumes, so a traded/pulled card stayed invisible until the player flipped a
        /// page or reopened the binder. When the book is open we also invoke the game's own
        /// OnSortingMethodUpdated (backToFirstPage:false, keeps the current page) which
        /// rebuilds the sorted list + relays out all page groups, so the card appears now.</summary>
        private static void RefreshOpenBinder()
        {
            try
            {
                if (_deltaIpc == null) _deltaIpc = FindObjectOfType<InteractionPlayerController>();
                var ctrl = _deltaIpc != null ? _deltaIpc.m_CollectionBinderFlipAnimCtrl : null;
                if (ctrl == null) return;
                ctrl.SetCanUpdateSort(canSort: true);
                bool isOpen = FiBinderIsBookOpen != null && (bool)FiBinderIsBookOpen.GetValue(ctrl);
                if (isOpen && MiBinderResort != null)
                    MiBinderResort.Invoke(ctrl, new object[] { false }); // backToFirstPage:false

                // OnSortingMethodUpdated re-lays out the cards but NEVER touches the total-value
                // text - that write only happens in the binder OPEN path. So a traded/pulled card
                // showed up on the page but the "total value" header stayed stale (the reported
                // "total value differs"). While the book is open, mirror the exact SetTotalValue
                // call the open path (CollectionBinderFlipAnimCtrl.Update ~807-830) would make for
                // the CURRENTLY open album. Behind the isOpen guard so a delta with no binder up
                // costs nothing.
                if (isOpen && FiBinderUI != null)
                {
                    var ui = FiBinderUI.GetValue(ctrl) as CollectionBinderUI;
                    if (ui != null)
                    {
                        bool isGraded = FiBinderIsGradedAlbum != null && (bool)FiBinderIsGradedAlbum.GetValue(ctrl);
                        var expansion = FiBinderExpansionType != null
                            ? (ECardExpansionType)FiBinderExpansionType.GetValue(ctrl)
                            : ECardExpansionType.None;
                        if (isGraded)
                        {
                            // graded album: sum GetCardMarketPrice over the graded inventory - and
                            // DELIBERATELY NOT the way the open path's loop (~810-819) does it.
                            //
                            // DO NOT "MAKE THIS MATCH VANILLA" AGAIN. Vanilla's loop WRITES
                            // m_GradedCardInventoryList[i].amount = 10 on every row over 10.
                            // CompactCardDataAmount is a CLASS (decompiled/CompactCardDataAmount.cs:4)
                            // and that list is the LIVE save list CGameData hands to the serializer
                            // BY REFERENCE (decompiled/CGameData.cs:1119 -> SetLoadData's
                            // `data = loadData` at :1019), so the write is a write to the save. With
                            // Grading Overhaul installed `amount` is not an amount at all: it is the
                            // ENCODED grade, packing grading company + the real 1-10 + the certificate
                            // serial (CPlayerData.AddCard stores cardGrade straight into it,
                            // decompiled/CPlayerData.cs:1506). Clamping it replaces every certificate
                            // in the album with a bare 10, permanently and world-wide, and it is
                            // UNRECOVERABLE - GO's own repair sweeps all skip amount <= 10
                            // (decompiled-grading :8266, :8652, :8804, :8901) and its
                            // EncodedGradeRegistry is keyed by CardData REFERENCE (:5881) while
                            // GetGradedCardData mints a fresh CardData every call (:1689).
                            //
                            // Ours was strictly worse than vanilla's, which is why this had to
                            // diverge rather than be left alone: vanilla runs that loop only on the
                            // binder OPEN transition (m_OpenBinder && !m_IsBookOpen,
                            // decompiled/CollectionBinderFlipAnimCtrl.cs:766), whereas this method is
                            // armed on the success tail of every applied card delta (~1333) and by
                            // the graded-adopt path, then drained per frame - so it re-ran on an
                            // ALREADY-OPEN book. GradeDataLifeSaver, the community fix, transpiles
                            // vanilla's clamp away but patches CollectionBinderFlipAnimCtrl.Update,
                            // so it can never reach a copy compiled into CardShopCoop.dll.
                            //
                            // So: READ the row, never write it. GetGradedCardData returns a brand-new
                            // CardData every call (:1689-1701), so nothing done to that throwaway
                            // copy can reach the save.
                            //
                            // AND WITH GO PRESENT, HAND ITS GRADE TO GetCardMarketPrice ENCODED AND
                            // UNTOUCHED. Do NOT "decode it first so vanilla sees a real 1-10" -
                            // vanilla already does. GO's
                            // PricingPatch_MarketPrice_GetMarketPrice.Prefix takes `ref int cardGrade`
                            // and does `if (cardGrade > 10) cardGrade = Helper.GetActualGrade(cardGrade)`
                            // (decompiled-grading :13697-13708), so MarketPrice.GetMarketPrice's body
                            // runs on the decoded value either way. Pre-decoding buys nothing there
                            // and COSTS the whole company multiplier: GO's postfix on
                            // CPlayerData.GetCardMarketPrice - PricingFix_GetCardMarketPrice_UseRegistry
                            // (:15052-15069) - reads the ENCODED value back off the card and applies
                            // nothing unless Helper.TryGetCompanyFromGrade accepts it, and that
                            // returns FALSE for every value in 1-10 (:16026-16035). Note the registry
                            // read it does that through is keyed by CardData REFERENCE (:5881, :5929),
                            // so for a fresh copy like this one it can only ever return the field we
                            // just set - the value we pass IS the value that decides the multiplier.
                            // Dropping it is not a rounding error: Cardinals 10 is 3x, PSA 10 is
                            // 16.43x, Beckett 10 is 21x and a Beckett Black Label multiplies that by
                            // 5 again for 105x (tables :1867-1881, applied at :1925-1975). A decoded
                            // copy therefore understates a top-grade album by up to 105x - and
                            // OVERSTATES a low-grade one, since grades 1-6 multiply by less than 1.
                            // An earlier version of this comment claimed that cost "a few percent";
                            // that was false, and it is why the decode is gone.
                            //
                            // The clamp survives for the NO-GO case ONLY. With GO absent nothing
                            // decodes an encoded grade on the way in, and vanilla indexes
                            // `(index * 10 + (cardGrade - 1)) % list.Count` (:1415-1420 ->
                            // MarketPrice.cs:18) - the `%` means an encoded int WRAPS rather than
                            // throws, so it cannot crash, it just totals a meaningless slot. Present
                            // is "GO's assembly loaded and its API resolved" (Util/GradingInterop.cs:119),
                            // which is also the only condition under which anything on this PC could
                            // have written an encoded grade in the first place. It does NOT track
                            // GO's own ConfigSettings.EnableMod, which gates both patches above; a
                            // player who installs GO and then disables it in config gets the same
                            // meaningless-slot number here, for the same harmless reason.
                            bool goPresent = Util.GradingInterop.Present;
                            float total = 0f;
                            for (int i = 0; i < CPlayerData.m_GradedCardInventoryList.Count; i++)
                            {
                                var row = CPlayerData.m_GradedCardInventoryList[i];
                                if (row == null) continue;
                                // PER-ROW, never around the loop: one bad row must not abandon the
                                // rest of the total. Same hazard as the compact-row walk documented
                                // on Util/GradingInterop.AddAllCompact, and it is NOT the divide by
                                // zero an earlier draft of this comment claimed.
                                // GetCardAmountPerMonsterType initialises num = 6 BEFORE its switch,
                                // every case assigns 6 (or 1 for Ghost), and there is no default arm
                                // (decompiled/CPlayerData.cs:692-721, the init at :694), so it
                                // returns 6 or 12 for an expansion it has never heard of and cannot
                                // return 0.
                                //
                                // What can actually throw is an out-of-range INDEX, and BOTH calls
                                // inside this try can do it for a row whose card set this install
                                // does not have. GetGradedCardData resolves the monster through
                                // GetMonsterTypeFromCardSaveIndex, which indexes
                                // InventoryBase.GetShownMonsterList(exp)[cardSaveIndex / perType]
                                // (:790-793) - a list that falls back to TETRAMON's for an unknown
                                // expansion (decompiled/InventoryBase.cs:290-308). GetCardMarketPrice
                                // then indexes m_GenCardMarketPriceList[GetCardSaveIndex(card)]
                                // (:1415-1420), and that list is sized from THIS install's own
                                // GetCardCollectionDataCount() + 100 (:491), so a high enough index
                                // runs off the end. Skipping such a row costs its value in one
                                // header total, which is the trade this loop wants.
                                //
                                // Verified against vanilla and Grading Overhaul (GO's only reference
                                // to GetCardAmountPerMonsterType is a read, decompiled-grading
                                // :7132). EPL is not visible from this repo and could patch it, so
                                // the catch - not the invariant - is what makes this safe.
                                try
                                {
                                    var copy = CPlayerData.GetGradedCardData(row);
                                    if (!goPresent && copy.cardGrade > 10) copy.cardGrade = 10;
                                    total += CPlayerData.GetCardMarketPrice(copy);
                                }
                                catch { continue; }
                            }
                            ui.SetTotalValue(total);
                        }
                        else if (expansion == ECardExpansionType.Ghost)
                        {
                            // Ghost/dimension album sums both the normal and dimension halves (~824).
                            ui.SetTotalValue(CPlayerData.GetCardAlbumTotalValue(expansion, isDimensionCard: false)
                                + CPlayerData.GetCardAlbumTotalValue(expansion, isDimensionCard: true));
                        }
                        else
                        {
                            // normal expansion album (~829).
                            ui.SetTotalValue(CPlayerData.GetCardAlbumTotalValue(expansion, isDimensionCard: false));
                        }
                    }
                }
            }
            catch (System.Exception e) { CoopPlugin.Log.LogWarning($"binder relayout after card change failed: {e.Message}"); }
        }

        private void FlushPendingCardWork()
        {
            if (!InGameLevel() || (_pendingCardDeltas.Count == 0 && _pendingCardPrices.Count == 0)) return;
            Guarded("pending-cards", () =>
            {
                // The relay-anyway flag is discarded here on purpose: this replay path has never
                // relayed anything (see ApplyOrHoldCardDelta's hold comment).
                foreach (var p in _pendingCardDeltas) ApplyCardDelta(p.IsAdd, p.Amount, p.Card, out _);
                if (_pendingCardDeltas.Count > 0)
                    CoopPlugin.Log.LogInfo($"applied {_pendingCardDeltas.Count} card change(s) held during loading");
                _pendingCardDeltas.Clear();
                // The results are NOT discardable on the host: a price queued during a scene
                // load still owes its sender the same ack/relay the live CardPriceSet handler
                // gives it. Dropping them meant a guest that priced a card while the host was
                // loading retried 12 times and then falsely surrendered. Echoes are collected
                // and sent AFTER the flag is cleared, exactly like the live handler.
                bool echoing = Role == CoopRole.Host;
                var echoes = echoing ? new List<KeyValuePair<CardData, float>>() : null;
                Patches.GamePatches.ApplyingRemotePrice = true;
                try
                {
                    foreach (var p in _pendingCardPrices)
                    {
                        bool applied = ApplyRemoteCardPrice(p.Key, p.Value, "load queue", out float actual, out bool relayAnyway);
                        if (!echoing) continue;                       // clients never echo
                        if (applied) echoes.Add(new KeyValuePair<CardData, float>(p.Key, actual));
                        else if (relayAnyway) echoes.Add(p);          // pure relay of the original
                    }
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("pending card price apply: " + e.Message); }
                finally { Patches.GamePatches.ApplyingRemotePrice = false; }
                _pendingCardPrices.Clear();
                if (echoes != null)
                {
                    for (int i = 0; i < echoes.Count; i++)
                    {
                        var kv = echoes[i];
                        Broadcast(new CardPriceSetMessage { Card = kv.Key, Price = kv.Value });
                    }
                }
            });
        }

        /// <summary>Host-side raw fan-out: forward a message a guest sent us on to the OTHER
        /// guests, byte-for-byte. The collection is one shared inventory, so a card a 3rd+ player
        /// gains/loses must reach every peer, not just the host. We re-wrap the ORIGINAL payload
        /// bytes (not a re-serialized CardData) so an encoded graded grade - the >10 company/cert
        /// packing - survives verbatim; re-serializing would risk lossy round-trips. Same shape as
        /// the CardShelfRequest relay, generalized. No-op unless we're the host with >1 peer.</summary>
        /// <summary>Host: relay only the deltas of a CardDeltaBatch that THIS side accepted.
        /// Used when part of the batch was refused here (corrupt grade / uninstalled card set /
        /// would-go-negative): the whole-batch case relays the ORIGINAL bytes, but a delta we
        /// refused must never be propagated onward - exactly the guarantee the single-delta
        /// case has always had.</summary>
        /// <summary>Host-side DTO fan-out: forward a decoded DTO a guest sent us to the OTHER
        /// guests. The DTO carries the exact CardData (including the encoded graded grade), so
        /// re-serializing it reproduces the original wire bytes.</summary>
        private void RelayToOthers(int senderConn, INetMessage message)
        {
            if (Role != CoopRole.Host || _net == null || _net.ConnectionCount <= 1) return;
            FlushCardDeltaOutbox();
            foreach (int cid in _net.ConnIds())
                if (cid != senderConn) _net.Send(cid, message);
        }

        private void RelayCardDeltaBatchToOthers(int senderConn, List<PendingCard> deltas)
        {
            if (Role != CoopRole.Host || _net == null || _net.ConnectionCount <= 1 || deltas.Count == 0) return;
            FlushCardDeltaOutbox(); // ordering: our own pending deltas leave first
            var relay = new CardDeltaBatchMessage();
            for (int i = 0; i < deltas.Count; i++)
                relay.Deltas.Add(new CardDeltaEntry
                { IsAdd = deltas[i].IsAdd, Amount = deltas[i].Amount, Card = deltas[i].Card });
            foreach (int cid in _net.ConnIds())
                if (cid != senderConn) _net.Send(cid, relay);
        }

        /// <summary>Send everything the card-delta outbox holds, at most CardDeltaBatchMax
        /// deltas per frame. Called at the end of Update AND by the send helpers before any
        /// other message goes out, so a game action that emits a delta and then a follow-up
        /// message (the graded-card flows) still puts them on the wire in that order.</summary>
        private void FlushCardDeltaOutbox()
        {
            if (_cardDeltaOutbox.Count == 0 || _flushingCardDeltas) return;
            if (_net == null) { _cardDeltaOutbox.Clear(); return; }
            _flushingCardDeltas = true;
            try
            {
                int total = _cardDeltaOutbox.Count;
                int sent = 0;
                while (sent < total)
                {
                    int start = sent;
                    int n = Math.Min(CardDeltaBatchMax, total - start);
                    var batch = new CardDeltaBatchMessage();
                    for (int i = start; i < start + n; i++)
                    {
                        var d = _cardDeltaOutbox[i];
                        batch.Deltas.Add(new CardDeltaEntry { IsAdd = d.IsAdd, Amount = d.Amount, Card = d.Card });
                    }
                    Broadcast(batch);
                    sent += n;
                }
                if (total > CardDeltaBatchMax)
                    CoopPlugin.Log.LogInfo($"card deltas: {total} sent as {(total + CardDeltaBatchMax - 1) / CardDeltaBatchMax} batch(es)");
                _cardDeltaOutbox.Clear();
            }
            finally { _flushingCardDeltas = false; }
        }

        /// <summary>End of frame: the folded card-delta log line(s), ONE binder relayout for
        /// everything applied this frame, then the batched outbox. Runs after every stage that
        /// can apply or produce a delta (dispatch, the held-during-loading flush, HostTick).</summary>
        private void FlushFrameCardWork()
        {
            if (_deltaAppliedThisFrame > 0)
            {
                if (_deltaAppliedThisFrame <= 5)
                {
                    for (int i = 0; i < _deltaLogBuf.Count; i++)
                    {
                        var d = _deltaLogBuf[i];
                        CoopPlugin.Log.LogInfo($"card delta applied: {(d.IsAdd ? "+" : "-")}{d.Amount} {CardIdent(d.Card)}{(d.Card.cardGrade > 0 ? $" (grade {d.Card.cardGrade})" : d.Card.isFoil ? " (foil)" : "")}");
                    }
                }
                else CoopPlugin.Log.LogInfo($"applied {_deltaAppliedThisFrame} card deltas");
                _deltaLogBuf.Clear();
                _deltaAppliedThisFrame = 0;
            }
            if (_binderRefreshPending)
            {
                _binderRefreshPending = false;
                RefreshOpenBinder();
            }
            FlushCardDeltaOutbox();
        }

        private void RelayTagToOthers(int senderConn, byte kind, int extra = -1)
        {
            if (Role != CoopRole.Host || _net == null || _net.ConnectionCount <= 1) return;
            // extra is the pack's EItemType for kind 1, and the unused -1 for an emote - which is
            // below the modded floor and so passes through the helper untouched. Host-side this
            // write is the identity function either way.
            var relay = new RelayTagMessage { SenderId = (byte)senderConn, Kind = kind, Extra = (EItemType)extra };
            foreach (int cid in _net.ConnIds())
                if (cid != senderConn) _net.Send(cid, relay);
        }

        private void BroadcastRoster()
        {
            if (Role != CoopRole.Host) return;
            var entries = new List<KeyValuePair<int, string>>(PeerNames);
            var roster = new RosterMessage();
            foreach (var e in entries)
            {
                _peerWireNames.TryGetValue(e.Key, out var wireName);
                if (string.IsNullOrEmpty(wireName)) wireName = e.Value;
                _peerSteamIds.TryGetValue(e.Key, out var steamId);
                roster.Entries.Add(new RosterEntry { Id = (byte)e.Key, Name = wireName, SteamId = steamId });
            }
            Broadcast(roster);
        }

        private int _selfId = -1; // our connId on the host, from Welcome
        private readonly HashSet<int> _relayIds = new HashSet<int>(); // other clients we render

        private void OnLocalPackOpened(CEventPlayer_OnOpenCardPack evt)
        {
            if (Role != CoopRole.None && _net != null && _net.ConnectionCount > 0)
                // m_PackIndex is named like an index but is spent as an EItemType on the far
                // side (AvatarManager.ShowPackOpen -> GetItemMeshData((EItemType)packIndex)),
                // so a modded pack needs translating like any other item id or the peer's
                // avatar holds up whichever product wears that number locally.
                Broadcast(new ActivityMessage { Activity = 1, Pack = (EItemType)evt.m_PackIndex });
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            CEventManager.RemoveListener<CEventPlayer_OnOpenCardPack>(OnLocalPackOpened);
            Shutdown("plugin unloaded");
        }

        private void OnApplicationQuit()
        {
            Shutdown("game closed");
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            PlayerModelGeneration++;
            _avatars.Clear();
            _world.Reset();
            _npcs.Reset();
            _cardShelves.Reset();
            _objMoves.Reset();
            _boxes.Reset();
            _population.Reset();
            ModulesReset();
            PromptLine = "";
            _lightManager = null;
            // Unity stops scene-owned coroutines during a load. If that interrupted the
            // mirrored morning reset, make it retry against the newly loaded manager.
            if (_clientDayResetInFlight)
            {
                _clientDayResetInFlight = false;
                _clientDayResetPending = true;
            }
            _cmSweep = null;
            _cmSpray = null;
            _inventory = null;
            _renamerHandled = false;
            _catalogSent = false;
            // A new world means a new album: the pairing hints and the whole diff describe one
            // that no longer exists, and a stale hint would suppress a legitimate add.
            ClearGradedSkipMemory();
            _gradedSent = false;
            _lastGradedHash = -1;
            _gradedPeerOnly.Clear();
            _gradedAlertEverShown.Clear();
            _gradedAlertStanding.Clear();
            GradedAdoptOffers.Clear();
            _playerTf = null;
            _playerCamTf = null;
            _playerIpc = null;
            _localModelAppliedRoot = null;
            _localPlayerModelReady = false;
            if (scene.name == "Title" && Role == CoopRole.Client && _net != null)
            {
                // client backed out to the main menu -> leave the session
                Shutdown("left the session");
            }
            // A game-level scene loading (not "Title") while a session is live and it was
            // NOT the mod's own join reload means someone loaded a DIFFERENT world out from
            // under the session: the guest hit pause -> Load Game -> its own save
            // (SaveLoadGameSlotSelectScreen loads "Start" mid-session), or the host loaded
            // another slot. The socket would otherwise stay open with the peer standing in a
            // world we no longer share, and the other side is never told. Shut the session
            // down cleanly, same path as the Title back-out above.
            //   - scene.name != "Title": the Title branch already handled the clean exit.
            //   - Role != None && _net != null: a session must actually be active (guards the
            //     HOST's own INITIAL world load, which happens from TitleScreen BEFORE
            //     StartHosting sets Role=Host - Role is still None there, so this can't fire).
            //   - !ClientReloading: the guest's mod-driven join reload (BundleDone sets this,
            //     and it also loads "Start") is the mod's OWN transition - never a leave.
            else if (scene.name != "Title" && Role != CoopRole.None && _net != null && !ClientReloading)
            {
                Shutdown("left the session (world reloaded)");
            }
        }

        private bool InGameLevel()
        {
            var gm = CSingleton<CGameManager>.Instance;
            return gm != null && gm.m_IsGameLevel;
        }

        public PlayerModelEntry GetLocalPlayerModel()
        {
            EnsureLocalPlayerModel();
            return ClonePlayerModel(_localPlayerModel);
        }

        public bool CanUndoPlayerModel { get { return _playerModelUndo.Count > 0; } }
        public bool CanRedoPlayerModel { get { return _playerModelRedo.Count > 0; } }

        public void SetCharacterPreview(bool active)
        {
            _characterPreviewActive = active;
        }

        public List<CC.CC_Property> GetLocalModelSliders()
        {
            EnsureLocalPlayerModel();
            return _avatars.GetBlendshapes(GetLocalCustomization());
        }

        public CC.CharacterCustomization GetLocalCustomization()
        {
            EnsureLocalPlayerModel();
            var custom = _avatars.GetEditorCustomization(_localPlayerModel.Female);
            if (custom != null && _localModelAppliedRoot != custom.transform)
            {
                _avatars.ApplyLocalModel(custom, _localPlayerModel);
                _localModelAppliedRoot = custom.transform;
            }
            return custom;
        }

        public void CommitLocalCustomization()
        {
            EnsureLocalPlayerModel();
            RecordLocalModelChange();
            var custom = GetLocalCustomization();
            _localPlayerModel = _avatars.CaptureLocalModel(custom, _localPlayerModel.Female, _localPlayerModel.ModelIndex);
            _localPlayerModelReady = true;
            _lastCommittedPlayerModel = ClonePlayerModel(_localPlayerModel);
            PlayerModelGeneration++;
            QueueLocalModelSave();
            SubmitLocalPlayerModel();
        }

        public bool UndoPlayerModel()
        {
            EnsureLocalPlayerModel();
            if (_playerModelUndo.Count == 0) return false;
            PushHistory(_playerModelRedo, _lastCommittedPlayerModel ?? _localPlayerModel);
            _localPlayerModel = _playerModelUndo[_playerModelUndo.Count - 1];
            _playerModelUndo.RemoveAt(_playerModelUndo.Count - 1);
            _lastCommittedPlayerModel = ClonePlayerModel(_localPlayerModel);
            _localModelAppliedRoot = null;
            GetLocalCustomization();
            PlayerModelGeneration++;
            QueueLocalModelSave();
            SubmitLocalPlayerModel();
            return true;
        }

        public bool RedoPlayerModel()
        {
            EnsureLocalPlayerModel();
            if (_playerModelRedo.Count == 0) return false;
            PushHistory(_playerModelUndo, _lastCommittedPlayerModel ?? _localPlayerModel);
            _localPlayerModel = _playerModelRedo[_playerModelRedo.Count - 1];
            _playerModelRedo.RemoveAt(_playerModelRedo.Count - 1);
            _lastCommittedPlayerModel = ClonePlayerModel(_localPlayerModel);
            _localModelAppliedRoot = null;
            GetLocalCustomization();
            PlayerModelGeneration++;
            QueueLocalModelSave();
            SubmitLocalPlayerModel();
            return true;
        }

        public List<string> GetLocalPresetNames()
        {
            var result = new List<string>();
            var custom = GetLocalCustomization();
            if (custom == null || custom.Presets == null || custom.Presets.Presets == null) return result;
            string prefix = _localPlayerModel.Female ? "Female" : "Male";
            foreach (var preset in custom.Presets.Presets)
                if (preset != null && (preset.CharacterName ?? "").StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    result.Add(preset.CharacterName);
            return result;
        }

        public void ApplyLocalPreset(string presetName)
        {
            EnsureLocalPlayerModel();
            var custom = GetLocalCustomization();
            if (custom == null || custom.Presets == null || custom.Presets.Presets == null) return;
            CC.CC_CharacterData preset = null;
            foreach (var candidate in custom.Presets.Presets)
                if (candidate != null && candidate.CharacterName == presetName) { preset = candidate; break; }
            if (preset == null) return;
            RecordLocalModelChange();
            bool female = (preset.CharacterName ?? "").StartsWith("Female", StringComparison.OrdinalIgnoreCase);
            int prefixLength = female ? 6 : 4;
            int index = 0;
            if (preset.CharacterName.Length > prefixLength)
                int.TryParse(preset.CharacterName.Substring(prefixLength), out index);
            _localPlayerModel.Female = female;
            _localPlayerModel.ModelIndex = Mathf.Max(0, index);
            _localPlayerModel.CustomizationJson = JsonConvert.SerializeObject(preset);
            _localModelAppliedRoot = null;
            GetLocalCustomization();
            _lastCommittedPlayerModel = ClonePlayerModel(_localPlayerModel);
            PlayerModelGeneration++;
            QueueLocalModelSave();
            SubmitLocalPlayerModel();
        }

        private void RecordLocalModelChange()
        {
            EnsureLocalPlayerModel();
            if (_lastCommittedPlayerModel == null)
                _lastCommittedPlayerModel = ClonePlayerModel(_localPlayerModel);
            PushHistory(_playerModelUndo, _lastCommittedPlayerModel);
            _playerModelRedo.Clear();
        }

        private static void PushHistory(List<PlayerModelEntry> history, PlayerModelEntry model)
        {
            if (model == null) return;
            history.Add(ClonePlayerModel(model));
            if (history.Count > PlayerModelHistoryLimit) history.RemoveAt(0);
        }

        public void ClearLocalHair(int slot)
        {
            if (_avatars.ClearHair(GetLocalCustomization(), slot)) CommitLocalCustomization();
        }

        public void ClearLocalApparel(int slot)
        {
            if (_avatars.ClearApparel(GetLocalCustomization(), slot)) CommitLocalCustomization();
        }

        public void SetLocalHairColor(int slot, Color color)
        {
            if (_avatars.SetHairColor(GetLocalCustomization(), slot, color)) CommitLocalCustomization();
        }

        public void SetLocalApparelTint(int slot, Color color)
        {
            if (_avatars.SetApparelTint(GetLocalCustomization(), slot, color)) CommitLocalCustomization();
        }

        public void SetLocalModelSlider(string propertyName, float value)
        {
            EnsureLocalPlayerModel();
            RecordLocalModelChange();
            if (!_avatars.SetBlendshape(GetLocalCustomization(), propertyName, value)) return;
            var custom = GetLocalCustomization();
            _localPlayerModel = _avatars.CaptureLocalModel(custom, _localPlayerModel.Female, _localPlayerModel.ModelIndex);
            _localPlayerModelReady = true;
            _lastCommittedPlayerModel = ClonePlayerModel(_localPlayerModel);
            QueueLocalModelSave();
            SubmitLocalPlayerModel();
        }

        public void SetLocalPlayerModel(bool female, int modelIndex)
        {
            EnsureLocalPlayerModel();
            RecordLocalModelChange();
            bool genderChanged = _localPlayerModel.Female != female;
            _localPlayerModel.Female = female;
            _localPlayerModel.ModelIndex = Mathf.Max(0, modelIndex);
            if (genderChanged) _localPlayerModel.CustomizationJson = null;
            _avatars.ApplyLocalModel(GetLocalCustomization(), _localPlayerModel);
            var editor = _avatars.GetEditorCustomization(_localPlayerModel.Female);
            if (editor != null) _localModelAppliedRoot = editor.transform;
            _lastCommittedPlayerModel = ClonePlayerModel(_localPlayerModel);
            PlayerModelGeneration++;
            QueueLocalModelSave();
            SubmitLocalPlayerModel();
        }

        private void EnsureLocalPlayerModel()
        {
            if (!_localPlayerModelReady)
            {
                if (!Util.PlayerModelStore.TryLoad(out _localPlayerModel))
                    _localPlayerModel = _avatars.CaptureLocalModel(_playerTf);
                _localPlayerModelReady = true;
            }
            var editor = _avatars.GetEditorCustomization(_localPlayerModel.Female);
            if (editor != null && _localModelAppliedRoot != editor.transform)
            {
                string customizationBeforeApply = _localPlayerModel.CustomizationJson;
                _avatars.ApplyLocalModel(editor, _localPlayerModel);
                _localModelAppliedRoot = editor.transform;
                if (customizationBeforeApply != _localPlayerModel.CustomizationJson)
                {
                    // A malformed/stale appearance is repaired to the template default by
                    // AvatarManager. Persist that repair so every later handshake uses the
                    // safe model instead of retrying the bad payload.
                    _lastCommittedPlayerModel = ClonePlayerModel(_localPlayerModel);
                    QueueLocalModelSave();
                }
            }
            if (_lastCommittedPlayerModel == null)
                _lastCommittedPlayerModel = ClonePlayerModel(_localPlayerModel);
        }

        private void QueueLocalModelSave()
        {
            _localModelSavePending = true;
            _localModelSaveTimer = 0.4f;
        }

        private void FlushLocalModelSave(float dt)
        {
            if (!_localModelSavePending || _localPlayerModel == null) return;
            _localModelSaveTimer -= dt;
            if (_localModelSaveTimer > 0f) return;
            _localModelSavePending = false;
            Util.PlayerModelStore.Save(_localPlayerModel);
        }

        private void SubmitLocalPlayerModel()
        {
            if (Role == CoopRole.Host)
            {
                _playerModels[0] = ClonePlayerModel(_localPlayerModel);
                BroadcastPlayerModelState();
            }
            else if (Role == CoopRole.Client && _net != null)
            {
                Send(1, new PlayerModelRequestMessage
                {
                    Female = _localPlayerModel.Female,
                    ModelIndex = _localPlayerModel.ModelIndex,
                    CustomizationJson = _localPlayerModel.CustomizationJson
                });
            }
        }

        private void ApplyPlayerModelRequest(int connId, PlayerModelRequestMessage request)
        {
            if (Role != CoopRole.Host || request == null || connId <= 0) return;
            _playerModels[connId] = new PlayerModelEntry
            {
                Id = (byte)connId,
                Female = request.Female,
                ModelIndex = Mathf.Max(0, request.ModelIndex),
                CustomizationJson = request.CustomizationJson
            };
            _avatars.SetModel(connId, request.Female, request.ModelIndex, request.CustomizationJson);
            CoopPlugin.Log.LogInfo($"appearance update from player {connId}: {(request.Female ? "female" : "male")} {request.ModelIndex}");
            BroadcastPlayerModelState();
        }

        private void BroadcastPlayerModelState()
        {
            if (Role != CoopRole.Host) return;
            Broadcast(BuildPlayerModelState());
        }

        private PlayerModelStateMessage BuildPlayerModelState()
        {
            EnsureLocalPlayerModel();
            _playerModels[0] = ClonePlayerModel(_localPlayerModel);
            var state = new PlayerModelStateMessage();
            foreach (var pair in _playerModels)
            {
                var model = ClonePlayerModel(pair.Value);
                model.Id = (byte)pair.Key;
                state.Entries.Add(model);
            }
            return state;
        }

        private void ApplyPlayerModelState(PlayerModelStateMessage state)
        {
            if (Role != CoopRole.Client || state == null) return;
            foreach (var entry in state.Entries)
            {
                if (entry == null) continue;
                if (entry.Id == _selfId)
                {
                    _localPlayerModel = ClonePlayerModel(entry);
                    _localPlayerModelReady = true;
                    continue;
                }
                int avatarId = entry.Id == 0 ? 1 : 1000 + entry.Id;
                _avatars.SetModel(avatarId, entry.Female, entry.ModelIndex, entry.CustomizationJson);
            }
        }

        private static PlayerModelEntry ClonePlayerModel(PlayerModelEntry source)
        {
            if (source == null) return new PlayerModelEntry();
            return new PlayerModelEntry
            {
                Id = source.Id,
                Female = source.Female,
                ModelIndex = source.ModelIndex,
                CustomizationJson = source.CustomizationJson
            };
        }

        /// <summary>
        /// Ends the guest's load hold from the game's actual completion signal rather than
        /// from a guessed number of seconds.  FindObjectOfType is deliberate here: asking
        /// CSingleton&lt;ShelfManager&gt;.Instance during a scene transition can create a fake,
        /// empty manager and make the readiness check lie.
        /// </summary>
        private bool TryFinishClientReload()
        {
            if (!InGameLevel()) return false;
            if (Time.frameCount <= _reloadStartedFrame || Time.realtimeSinceStartup - _reloadStartedAt < 0.25f)
                return false;

            var shelfManager = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            if (shelfManager == null || !shelfManager.m_FinishLoadingObjectData)
                return false;

            float elapsed = Time.realtimeSinceStartup - _reloadStartedAt;
            ClientReloading = false;
            CoopPlugin.Log.LogInfo($"Join world load completed in {elapsed:F2}s; resuming co-op sync");

            // A shop name that arrived while loading may have been painted onto a sign
            // that the reload then rebuilt. Re-stamp it now that the real world is ready.
            if (_shopSign != null && !string.IsNullOrEmpty(_lastShopNameApplied))
            {
                try { _shopSign.text = _lastShopNameApplied; } catch { }
            }
            return true;
        }

        // NEVER CSingleton<>.Instance for scene-lifetime managers (CGameManager above
        // is a REAL persistent singleton and stays on the getter): touched while no
        // real manager exists (client reload loading screen - InGameLevel() stays true
        // there - or host mid-session save load) the getter fabricates a fake empty
        // DontDestroyOnLoad manager that shadows the real one for the rest of the run
        // (see WorldSync.ResolveShelfManager). Cached; the Unity fake-null re-resolves
        // after scene loads, and OnSceneLoaded clears them besides.
        private static InventoryBase _inventory;

        private static InventoryBase Inv()
        {
            if (_inventory == null) _inventory = FindObjectOfType<InventoryBase>();
            return _inventory;
        }

        private void ModulesTick()
        {
            bool inGame = InGameLevel();
            if (Role == CoopRole.Host)
            {
                _grading.HostTick(_dt, inGame);
                _trades.HostTick(_dt, inGame);
                _tables.HostTick(_dt, inGame);
                _staff.HostTick(_dt, inGame);
                _shopState.HostTick(_dt, inGame);
                _settings.HostTick(_dt, inGame);
                _market.HostTick(_dt, inGame);
                _report.HostTick(_dt, inGame); // per-frame: its report-open flag fires outside the timer
                _containers.HostTick(_dt, inGame);
                _tournament.HostTick(_dt, inGame);
                _cardBoxes.HostTick(_dt, inGame);
                _furnBoxes.HostTick(_dt, inGame);
                _register.HostTick(_dt, inGame);
                _tv.HostTick(_dt, inGame);
            }
            else if (Role == CoopRole.Client)
            {
                _tv.ClientTick(_dt, inGame);
                _trades.ClientTick(_dt, inGame); // offer countdown + accept/decline keys
                _containers.ClientTick(); // retry container clicks waiting for population identity
                _cardBoxes.ClientTick(_dt, inGame && !ClientPreloadHold); // carried transitions + box moves
                _furnBoxes.ClientTick(_dt, inGame && !ClientPreloadHold);
                // Box trajectories are cosmetic client prediction only. Their endpoints are
                // still host-authored; this makes throw/drop/set-down reconciliation readable
                // instead of teleporting the remote copy.
                BoxSync.TickRemoteMotions(_dt);
                // content mods register their products SECONDS after the scene loads
                // (and per-save: a host mid-tutorial has none yet) - keep re-digesting
                // as our catalog changes so the comparison never goes stale
                _catalogTimer += _dt;
                if (inGame && (_catalogTimer >= 45f || !_catalogSent))
                {
                    _catalogTimer = 0f;
                    _catalogSent = true;
                    int h = LocalCatalogHash();
                    if (h != _lastCatalogSentHash)
                    {
                        _lastCatalogSentHash = h;
                        SendCatalogDigest();
                    }
                }
                // ...and the graded-cert digest on the same gating for the same reason: the album
                // changes constantly (every pack opened, every card graded), so a one-shot send
                // would be stale within a minute. Built once and reused for both the hash test
                // and the send - the union walk is the expensive half, not the write.
                _gradedTimer += _dt;
                if (inGame && (_gradedTimer >= 45f || !_gradedSent))
                {
                    var inv = Util.GradingInterop.BuildGradedCertInventory();
                    // NULL IS NOT AN EMPTY ALBUM - it is "the world here is still spawning", and
                    // sending a SHORT union is the one direction that hurts: the host would read
                    // every graded card on our own shelves as one-sided and offer them back for
                    // adoption. InGameLevel() cannot tell us apart from a loaded world (it stays
                    // true through the client reload screen), so this is the gate. Do not mark
                    // the send done; come back in a second rather than in 45.
                    if (inv == null)
                    {
                        _gradedTimer = 44f;
                    }
                    else
                    {
                        _gradedTimer = 0f;
                        _gradedSent = true;
                        int gh = GradedHash(inv);
                        if (gh != _lastGradedHash)
                        {
                            _lastGradedHash = gh;
                            SendGradedDigest(1, inv);
                        }
                    }
                }
            }
        }

        private static int LocalCatalogHash()
        {
            try
            {
                int total = CatalogCount(); // vanilla + EPL virtual entries
                int h = 17;
                for (int i = 0; i < total; i++)
                {
                    var rd = CatalogAt(i);
                    if (rd != null) h = h * 31 + (((int)rd.itemType << 1) | (rd.isBigBox ? 1 : 0));
                }
                return h;
            }
            catch { return 0; }
        }

        private void ModulesReset()
        {
            _grading.Reset();
            _trades.Reset();
            _tables.Reset();
            _staff.Reset();
            _shopState.Reset();
            _settings.Reset();
            _market.Reset();
            _report.Reset();
            _containers.Reset();
            _tournament.Reset();
            _cardBoxes.Reset();
            _furnBoxes.Reset();
            _register.Reset();
            _tv.Reset();
        }

        private void ActivateLiveModuleHooks()
        {
            Util.GradingInterop.Reset();
            NpcSync.ActivateLive(_npcs);
            TradeServe.ActivateLive(_trades);
            RegisterSync.ActivateLive(_register);
            BoxSync.ActivateLive(_boxes);
            ContainerSync.ActivateLive(_containers);
            FurnBoxSync.ActivateLive(_furnBoxes);
            GradingSync.ActivateLive(_grading);
        }

        private static void ClearLiveModuleHooks()
        {
            NpcSync.ClearLive();
            TradeServe.ClearLive();
            RegisterSync.ClearLive();
            BoxSync.ClearLive();
            ContainerSync.ClearLive();
            FurnBoxSync.ClearLive();
            GradingSync.ClearLive();
            Util.GradingInterop.Reset();
        }

        private void ModulesForceResend()
        {
            _grading.ForceResend();
            _trades.ForceResend();
            _tables.ForceResend();
            _staff.ForceResend();
            _shopState.ForceResend();
            _settings.ForceResend();
            _market.ForceResend();
            _report.ForceResend();
            _containers.ForceResend();
            _tournament.ForceResend();
            _cardBoxes.ForceResend();
            _furnBoxes.ForceResend();
            _register.ForceResend();
            _tv.ForceResend();
        }

        internal void SendTvOp(TvOpMessage message)
        {
            if (message != null && Role == CoopRole.Client) Send(1, message);
        }

        private void NpcSweepTick()
        {
            // the shop-naming world trigger (and its "!" marker) is host-only; find it
            // ONCE - once disabled, FindObjectOfType can never see it again and each
            // retry was a full-scene scan for nothing
            if (!_renamerHandled)
            {
                _renamerHandled = true;
                var renamer = FindObjectOfType<ShopRenamer>();
                if (renamer != null && renamer.gameObject.activeSelf)
                {
                    // FIX E2: cache the 3D sign TMP BEFORE disabling - once the renamer
                    // GameObject is inactive, FindObjectOfType can't reach it again.
                    try { _shopSign = renamer.m_ShopName; } catch { }
                    renamer.gameObject.SetActive(false);
                    CoopPlugin.Log.LogInfo("disabled shop-renamer trigger (host names the shop)");
                    // a ShopName may have already arrived while the renamer was still live
                    // (its listener overwrote the sign with the local default) - re-stamp
                    if (_shopSign != null && !string.IsNullOrEmpty(_lastShopNameApplied))
                    {
                        try { _shopSign.text = _lastShopNameApplied; } catch { }
                    }
                }
            }
            if (_cmSweep == null) _cmSweep = FindObjectOfType<CustomerManager>();
            if (_cmSweep != null)
            {
                var list = _cmSweep.GetCustomerList();
                for (int i = 0; i < list.Count; i++)
                {
                    var cust = list[i];
                    if (cust == null || Sync.RegisterSync.IsCarrier(cust)
                        || Sync.NpcSync.IsExistingCustomer(cust)) continue;
                    if (cust.gameObject.activeSelf) cust.gameObject.SetActive(false);
                }
            }
            var workers = WorkerManager.GetWorkerList();
            if (workers != null)
                for (int i = 0; i < workers.Count; i++)
                    if (workers[i] != null && workers[i].gameObject.activeSelf)
                        workers[i].gameObject.SetActive(false);
        }

        private void StateSendTick()
        {
            float interval = 1f / Mathf.Clamp(CoopPlugin.SendRateHz.Value, 4f, 30f);
            Transform playerTf = InGameLevel() ? ResolvePlayer() : null;
            if (_stateTimer < interval || playerTf == null) return;
            Vector3 pos = playerTf.position;
            float speed = 0f;
            if (_hasLastPos)
            {
                Vector3 delta = pos - _lastPos;
                delta.y = 0f;
                speed = Mathf.Clamp(delta.magnitude / _stateTimer, 0f, 6f);
            }
            _lastPos = pos; _hasLastPos = true;
            float yaw = _playerCamTf != null ? _playerCamTf.eulerAngles.y
                : (Camera.main != null ? Camera.main.transform.eulerAngles.y : playerTf.eulerAngles.y);
            byte hold = ComputeHoldState();
            BroadcastTransient(new PlayerStateMessage
            {
                Position = pos,
                Yaw = yaw,
                Speed = speed,
                Hold = hold,
                HoldTypes = hold == 3 ? null : new List<int>(_holdTypesBuf),
                HoldCards = hold == 3 ? new List<CardData>(_holdCardsBuf) : null
            });
            _diagSent++;
            _stateTimer = 0f; // full reset: the speed estimate divides by this elapsed time
        }

        private void NpcCollectTick()
        {
            var chunks = _npcs.HostCollect(_dt);
            if (chunks == null) return;
            for (int i = 0; i < chunks.Count; i++)
            {
                BroadcastTransient(chunks[i]);
            }
        }

        /// <summary>The game assigns neither CGameManager.Player nor
        /// InteractionPlayerController.m_Instance (both are dead statics), so find the
        /// player controller in the scene once and cache its transform. FindObjectOfType
        /// never auto-creates, unlike CSingleton&lt;T&gt;.Instance.</summary>
        private Transform _playerTf;   // the MOVING body: IPC.m_WalkerCtrl (CMF walker)
        private Transform _playerCamTf; // player camera, for look yaw
        private InteractionPlayerController _playerIpc;
        private bool _uiModeHeldByWindow;
        private InteractionPlayerController _uiModeController;

        /// <summary>InteractionPlayerController itself sits on a stationary manager object -
        /// its transform never moves (that was the frozen-avatar bug). The walking body is
        /// its public m_WalkerCtrl (the game's own teleport code moves the player by setting
        /// m_WalkerCtrl.transform.position), and look direction lives on m_Cam.</summary>
        private Transform ResolvePlayer()
        {
            if (_playerTf != null) return _playerTf;
            var ipc = InteractionPlayerController.m_Instance;
            if (ipc == null) ipc = FindObjectOfType<InteractionPlayerController>();
            if (ipc != null)
            {
                _playerIpc = ipc;
                _playerTf = ipc.m_WalkerCtrl != null ? ipc.m_WalkerCtrl.transform : ipc.transform;
                _playerCamTf = ipc.m_Cam != null ? ipc.m_Cam.transform : null;
                CoopPlugin.Log.LogInfo($"Player body resolved: {_playerTf.name} at {_playerTf.position}, cam={(_playerCamTf != null ? _playerCamTf.name : "none")}");
            }
            return _playerTf;
        }

        /// <summary>Make the co-op window modal while it is visible in a game level.
        /// Unity IMGUI handles its own controls, but the game reads world input from
        /// the legacy Input API, so IMGUI alone does not prevent a click from also
        /// interacting with the shop. The controller's own UI mode is the game's
        /// supported input gate.</summary>
        private void SyncWindowUIMode()
        {
            bool want = _ui.Visible && InGameLevel();

            if (want)
            {
                 // A scene transition can destroy the controller while the window
                 // remains visible. Do not retain ownership of the destroyed object;
                 // this lets the replacement controller be gated when it appears.
                if (_uiModeHeldByWindow && _uiModeController != null)
                {
                    // Another game flow may have called ExitUIMode while the
                    // co-op window stayed open. Reassert our modal state, but do
                    // not call EnterUIMode every frame.
                    if (!_uiModeController.IsInUIMode())
                        _uiModeController.EnterUIMode();
                    return;
                }

                var ipc = InteractionPlayerController.m_Instance;
                if (ipc == null) ipc = FindObjectOfType<InteractionPlayerController>();
                if (ipc == null) return;

                 // Preserve a modal state that belongs to the game; only undo UI
                 // mode that this window entered.
                if (ipc.IsInUIMode())
                {
                    _uiModeHeldByWindow = false;
                    _uiModeController = null;
                    return;
                }

                CoopPlugin.Log.LogInfo("Co-op window opened: entering game UI mode to block gameplay input");
                ipc.EnterUIMode();
                _uiModeHeldByWindow = true;
                _uiModeController = ipc;
                return;
            }

            if (!_uiModeHeldByWindow)
            {
                _uiModeController = null;
                return;
            }

            if (_uiModeController != null)
            {
                CoopPlugin.Log.LogInfo("Co-op window closed: restoring gameplay input");
                _uiModeController.ExitUIMode();
            }
            _uiModeHeldByWindow = false;
            _uiModeController = null;
        }

        /// <summary>Client-side half of an acknowledged empty-box take. Reuse the game's own
        /// StartHoldBox recipe so the controller enters HoldingBoxState, the box physics are
        /// disabled, and the normal carry report reaches the host on the next BoxSync tick.</summary>
        private static bool TryHoldClientBox(InteractablePackagingBox_Item box)
        {
            if (Role != CoopRole.Client || box == null) return false;
            var core = Instance;
            if (core == null) return false;
            try
            {
                if (core._playerIpc == null) core.ResolvePlayer();
                var ipc = core._playerIpc;
                if (ipc == null || ipc.m_HoldItemPos == null) return false;
                if (FiIsHoldBoxMode?.GetValue(ipc) is bool held && held) return false;
                box.StartHoldBox(isPlayer: true, ipc.m_HoldItemPos);
                // BoxSync caches the controller's held-box fields for the current frame.
                // Invalidate that cache because this handoff changes those fields inside the
                // BoxState reconciliation callback itself.
                core._heldBoxFrame = -1;
                return true;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("client empty-box hand-off: " + e.Message);
                return false;
            }
        }

        public static bool TryGetLocalPlayerPosition(out Vector3 position)
        {
            position = default(Vector3);
            var core = Instance;
            var player = core != null ? core.ResolvePlayer() : null;
            if (player == null) return false;
            position = player.position;
            return true;
        }

        // what the local player is carrying (private fields; the game has no public API)
        private static readonly FieldInfo FiHoldBox = HarmonyLib.AccessTools.Field(typeof(InteractionPlayerController), "m_CurrentHoldingBox");
        private static readonly FieldInfo FiHoldItemBox = HarmonyLib.AccessTools.Field(typeof(InteractionPlayerController), "m_CurrentHoldingItemBox");
        private static readonly FieldInfo FiHoldBoxShelf = HarmonyLib.AccessTools.Field(typeof(InteractionPlayerController), "m_CurrentHoldingBoxShelf");
        private static readonly FieldInfo FiHoldBoxCard = HarmonyLib.AccessTools.Field(typeof(InteractionPlayerController), "m_CurrentHoldingBoxCard");
        private static readonly FieldInfo FiHoldItemList = HarmonyLib.AccessTools.Field(typeof(InteractionPlayerController), "m_HoldItemList");
        private static readonly FieldInfo FiIsHoldBoxMode = HarmonyLib.AccessTools.Field(typeof(InteractionPlayerController), "m_IsHoldBoxMode");

        /// <summary>A box the local player is actively holding is about to be destroyed by a
        /// reconcile. The game's InteractablePackagingBox.OnDestroyed does NOT exit hold-box
        /// mode (only Throw/Place/Store/Discard do), so m_IsHoldBoxMode + the HoldingBoxState
        /// would stay set forever pointing at a fake-null box - and Update() then ONLY
        /// dispatches RaycastHoldBoxState, so the player can't interact with anything, not even
        /// the trash (the guest soft-lock report). Call the game's own public exit first so the
        /// state clears cleanly. No-op unless the box really is one the player holds.</summary>
        public static void ForceExitHoldBox(UnityEngine.Object heldBox)
        {
            var self = Instance;
            var ipc = self != null ? self._playerIpc : null;
            if (ipc == null) return;
            try
            {
                var a = FiHoldItemBox?.GetValue(ipc);
                var b = FiHoldBox?.GetValue(ipc);
                var c = FiHoldBoxCard?.GetValue(ipc);
                if (!ReferenceEquals(a, heldBox) && !ReferenceEquals(b, heldBox) && !ReferenceEquals(c, heldBox)) return;
                ipc.OnExitHoldBoxMode();
                CoopPlugin.Log.LogInfo("ForceExitHoldBox: released hold-box mode for a box being retired by reconcile");
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("ForceExitHoldBox: " + e.Message); }
        }

        /// <summary>Per-frame safety net: if the game is stuck in hold-box mode but the held
        /// box is gone (fake-null) - e.g. a player who hit the soft-lock before this fix -
        /// exit hold-box mode so they can interact again.</summary>
        private void RecoverStuckHoldBox()
        {
            if (_playerIpc == null) return;
            try
            {
                if (FiIsHoldBoxMode?.GetValue(_playerIpc) is bool held && held)
                {
                    var box = FiHoldBox?.GetValue(_playerIpc) as UnityEngine.Object;
                    var itemBox = FiHoldItemBox?.GetValue(_playerIpc) as UnityEngine.Object;
                    var cardBox = FiHoldBoxCard?.GetValue(_playerIpc) as UnityEngine.Object;
                    // Unity fake-null: a destroyed object == null. If hold-box mode is on but
                    // every candidate box is null/destroyed, nothing can ever clear it.
                    if (box == null && itemBox == null && cardBox == null)
                    {
                        _playerIpc.OnExitHoldBoxMode();
                        CoopPlugin.Log.LogInfo("RecoverStuckHoldBox: cleared a stranded hold-box lock (no live held box)");
                    }
                }
            }
            catch { }
        }

        private readonly List<int> _holdTypesBuf = new List<int>(6);
        private readonly List<CardData> _holdCardsBuf = new List<CardData>(4);
        private static readonly FieldInfo FiHoldCard3dList = HarmonyLib.AccessTools.Field(typeof(InteractionPlayerController), "m_CurrentHoldingCard3dList");
        private static readonly FieldInfo FiViewAlbum = HarmonyLib.AccessTools.Field(typeof(InteractionPlayerController), "m_IsViewCardAlbumMode");

        /// <summary>What the local player carries: 0 none / 1 box / 2 items / 3 cards / 4 binder.
        /// Items fill the type buffer, cards the CardData buffer - both render as the REAL
        /// things on the other side (modded ids resolve identically via registry parity).</summary>
        private byte ComputeHoldState()
        {
            _holdTypesBuf.Clear();
            _holdCardsBuf.Clear();
            if (_playerIpc == null) return 0;
            try
            {
                // The generic field is the only authoritative hold marker. The game does
                // not clear the type-specific shelf/card fields when Q enters move-box
                // mode, so consulting them creates a duplicate held visual.
                if (IsAlive(FiHoldBox))
                {
                    // describe the box (size + contents) so the avatar shows the real thing
                    if (FiHoldItemBox?.GetValue(_playerIpc) is InteractablePackagingBox_Item ib && ib != null)
                    {
                        _holdTypesBuf.Add(ib.m_IsBigBox ? 1 : 0);
                        try { _holdTypesBuf.Add((int)ib.m_ItemCompartment.GetItemType()); }
                        catch { _holdTypesBuf.Add(0); }
                    }
                    return 1; // carrying a box
                }
                if (FiHoldItemList?.GetValue(_playerIpc) is List<Item> items && items.Count > 0)
                {
                    for (int i = 0; i < items.Count && _holdTypesBuf.Count < 6; i++)
                        if (items[i] != null) _holdTypesBuf.Add((int)items[i].GetItemType());
                    return 2; // items in hand
                }
                if (FiHoldCard3dList?.GetValue(_playerIpc) is List<InteractableCard3d> cards && cards.Count > 0)
                {
                    for (int i = 0; i < cards.Count && _holdCardsBuf.Count < 4; i++)
                    {
                        var c = cards[i];
                        if (c != null && c.m_Card3dUI != null && c.m_Card3dUI.m_CardUI != null)
                            _holdCardsBuf.Add(c.m_Card3dUI.m_CardUI.GetCardData());
                    }
                    if (_holdCardsBuf.Count > 0) return 3; // loose cards fanned in hand
                }
                if (FiViewAlbum?.GetValue(_playerIpc) is bool album && album)
                    return 4; // reading the collection binder
            }
            catch { }
            return 0;
        }

        private bool IsAlive(FieldInfo fi)
        {
            var obj = fi?.GetValue(_playerIpc) as UnityEngine.Object;
            return obj != null;
        }

        // ------------------------------------------------ public entry points (UI)

        public void StartHosting()
        {
            ErrorLine = "";
            if (Role != CoopRole.None) { ErrorLine = "Already in a session."; return; }
            if (!InGameLevel()) { ErrorLine = "Load your shop first, then host."; return; }
            // a HOST must never translate: drop any table a previous session left behind
            Util.EnumMap.Clear();
            try
            {
                var tcp = new Transport { KeepaliveMessage = new PingMessage() };
                tcp.StartHost(CoopPlugin.Port.Value);
                _net = tcp;
                Role = CoopRole.Host;
                ActivateLiveModuleHooks();
                StatusLine = "Hosting - waiting for a player...";
                CoopPlugin.Log.LogInfo($"Hosting on port {CoopPlugin.Port.Value}");
                // THE PORT AND THE PASSWORD ARE ONE DECISION, so this sits here rather than in
                // the UI: LAN hosting may be about to ask the router to open this port to the
                // whole internet (AutoPortForward), and an open port with no password is an
                // open door into the player's game and save. Generated BEFORE
                // BeginInviteResolve, which snapshots HostPassword into the invite code - get
                // this order wrong and the code carries "" while the Hello gate demands the
                // password, i.e. a code that cannot join its own host.
                if (CoopPlugin.AutoLanPassword.Value && string.IsNullOrEmpty(HostPassword))
                    HostPassword = GenerateSessionPassword();
                BeginInviteResolve();
            }
            catch (Exception e)
            {
                ErrorLine = "Could not host: " + e.Message;
                _net?.Stop(); _net = null;
                Role = CoopRole.None;
                HostPassword = ""; // nothing is listening; don't leave a stale one behind
            }
        }

        /// <summary>Eight Crockford base32 characters from the crypto RNG - 40 bits, which is
        /// far beyond anything guessable through a TCP handshake, and short enough to read out
        /// loud. Same alphabet as the invite code on purpose: this string is meant to be spoken
        /// over voice chat when a friend types the IP by hand, and dropping I/L/O/U is exactly
        /// what makes that survivable. 32 divides 256, so the byte-to-symbol reduction below is
        /// unbiased.</summary>
        private static string GenerateSessionPassword()
        {
            const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
            var bytes = new byte[8];
            using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider())
                rng.GetBytes(bytes);
            var chars = new char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) chars[i] = alphabet[bytes[i] & 31];
            return new string(chars);
        }

        /// <summary>LAN hosting just started: work out the one string a friend can paste, on a
        /// worker thread, and (unless the player turned it off) ask the router to open the port
        /// while we're at it.
        ///
        /// EVERY LINE OF THIS RUNS OFF THE MAIN THREAD, and it must: SSDP discovery alone is a
        /// 3-second blocking wait, the router's HTTP can take another 4, and STUN 2 more. On
        /// the main thread that is a nine-second freeze of the whole GAME at the exact moment
        /// the player pressed Host. Hosting itself is already live before this starts - the
        /// invite code is a convenience layered on top, and nothing in here can fail in a way
        /// that touches the session.
        ///
        /// SEQUENCE: router mapping (optional) -> the router's own external address -> STUN as
        /// the fallback -> compose. A router that reports a PRIVATE external address is on
        /// carrier-grade NAT, and no amount of port forwarding at this end will make it
        /// reachable; that case is called out by name instead of being papered over with the
        /// carrier's address, which would produce a code that silently never connects.</summary>
        private void BeginInviteResolve()
        {
            InviteCodeText = null;
            InviteReason = null;
            PortForwardState = 0;
            InviteStatus = InviteState.Resolving;

            int port = CoopPlugin.Port.Value;
            string password = HostPassword ?? "";
            bool forward = CoopPlugin.AutoPortForward.Value;
            int gen = Interlocked.Increment(ref _inviteGen);

            new Thread(() =>
            {
                string lan = null, publicIp = null, reason = null;
                try
                {
                    NetHelpers.BeginDiscovery(); // one SSDP sweep per run, not one per call

                    if (forward)
                    {
                        bool mapped = NetHelpers.TryMapPort(port, out string why, out long epoch);
                        if (!mapped)
                            CoopPlugin.Log.LogInfo("automatic port forwarding did not happen: " + (why ?? "unknown"));

                        // ORPHAN GUARD. The gen check at the BOTTOM of this worker stops a dead
                        // session's code from being published, but a mapping is not a field we
                        // can decline to publish - it is a hole already open in the router. If
                        // the session ended while TryMapPort was blocking (host stopped, or
                        // stopped and re-hosted), nothing downstream would ever remove it:
                        // Shutdown's own unmap ran BEFORE this mapping existed. Close it here,
                        // on this thread, and stop - blocking is what worker threads are for.
                        //
                        // BY EPOCH, not just by port. A stop-and-re-host maps the SAME port
                        // again, and this worker can be finishing while the new session's
                        // worker is already inside NetHelpers; a plain "remove the mapping for
                        // port 27886" would then delete the LIVE session's forward and leave it
                        // reporting success. The epoch names the mapping THIS call made, and
                        // NetHelpers no-ops when a newer one has replaced it.
                        if (Volatile.Read(ref _inviteGen) != gen)
                        {
                            if (mapped)
                            {
                                CoopPlugin.Log.LogInfo("port mapping completed after its session ended - removing it again");
                                NetHelpers.RemoveMapping(epoch);
                            }
                            return;
                        }
                        Publish(gen, () => PortForwardState = mapped ? 1 : 2);

                        string ext = NetHelpers.UpnpExternalIp();
                        if (ext != null && !NetHelpers.IsPublicIPv4(ext))
                            reason = $"your router's own internet address ({ext}) is a private one - your line is behind carrier-grade NAT, so no port forward at this end can reach you";
                        else
                            publicIp = ext;
                    }

                    if (publicIp == null && reason == null)
                    {
                        publicIp = NetHelpers.StunPublicIp();
                        if (publicIp != null && !NetHelpers.IsPublicIPv4(publicIp)) publicIp = null;
                        if (publicIp == null) reason = "couldn't reach the internet resolver";
                    }

                    // THE LAN FALLBACK ADDRESS, AND WHY IT IS COMPUTED HERE rather than at the
                    // top of the worker. Once a gateway has been discovered we know which
                    // adapter actually talks to it, and that is the address the code must
                    // carry: LocalIPv4() only RANKS adapters by prefix, so a VirtualBox, VPN or
                    // Hyper-V adapter on 192.168.x can outrank the real LAN card - and then the
                    // code names one adapter while the port mapping points at another, which
                    // cannot work from either side of the router. No gateway (auto-forwarding
                    // off, or nothing answered) means there is nothing to be route-correct
                    // about, and the ranking is the best guess there is.
                    lan = NetHelpers.LocalIPv4ForGateway() ?? NetHelpers.LocalIPv4();
                }
                catch (Exception e)
                {
                    // Belt and braces - every helper already swallows its own failures, so
                    // landing here means something genuinely unexpected. The session is
                    // untouched either way; we just fall back to the LAN code.
                    reason = e.Message;
                    CoopPlugin.Log.LogWarning("invite code resolve failed: " + e.GetType().Name + ": " + e.Message);
                    // ...and the LAN address is computed inside the try now, so a throw above
                    // would otherwise take the LAN-only code down with it.
                    if (lan == null) { try { lan = NetHelpers.LocalIPv4(); } catch { } }
                }

                string chosen = publicIp ?? lan;
                string code = chosen != null ? InviteCode.Encode(chosen, port, password) : null;
                if (code == null && reason == null) reason = "this PC has no usable network address";

                string finalReason = reason;
                bool ready = publicIp != null && code != null;
                Publish(gen, () =>
                {
                    InviteCodeText = code;
                    InviteReason = finalReason;
                    InviteStatus = ready ? InviteState.Ready : InviteState.LanOnly;
                });
                CoopPlugin.Log.LogInfo(publicIp != null
                    ? "invite code ready (internet address)"
                    : "invite code is LAN-only: " + (reason ?? "no public address"));
            })
            { IsBackground = true, Name = "CoopInvite" }.Start();
        }

        /// <summary>Hands one invite-code field write back to the MAIN THREAD, and drops it if
        /// the session it belongs to has ended in the meantime.
        ///
        /// The fields are still volatile, and the UI still latches them once per Layout pass -
        /// this is the third leg of the same stool and the only one that removes the race
        /// rather than tolerating it. Worker-side publishing had two problems: OnGUI could see
        /// InviteCodeText and InviteStatus from DIFFERENT instants (a code with a status that
        /// disagrees), and the gen check could pass a nanosecond before Shutdown bumped it,
        /// resurrecting a dead session's code. Both disappear when the write happens on the
        /// same thread as Shutdown and the UI: the check and the assignment are then in the
        /// same single-threaded order as everything else.</summary>
        private void Publish(int gen, Action write)
        {
            QueueMainThread("invite-publish", () =>
            {
                if (Volatile.Read(ref _inviteGen) != gen)
                {
                    CoopPlugin.Log.LogInfo("invite code resolve finished after its session ended - discarded");
                    return;
                }
                write();
            }, false);
        }

        public void Join(string ip)
        {
            Join(ip, CoopPlugin.Port.Value, "");
        }

        /// <summary>The LAN join every path ends up in. The port and password arguments exist
        /// for the invite code, which carries a host's ACTUAL port rather than assuming both
        /// PCs left the config alone.</summary>
        public void Join(string ip, int joinPort, string password)
        {
            ErrorLine = "";
            if (Role != CoopRole.None) { ErrorLine = "Already in a session."; return; }
            if (InGameLevel()) { ErrorLine = "Join from the main menu (Title screen)."; return; }
            ip = (ip ?? "").Trim();
            if (ip.Length == 0) { ErrorLine = "Enter the host's IP address."; return; }
            // FIX C: same restart gate as JoinSteam - a HOST's registry was installed over ours,
            // but this process still has the OLD one loaded, so a join can only end in another
            // rejection until the game is restarted. (A restore does NOT gate here.)
            if (Util.ModParity.RestartRequiredForJoin)
            {
                ErrorLine = "the host's card database was installed on this PC - RESTART the game before joining";
                return;
            }

            CoopPlugin.LastJoinIP.Value = ip;
            Role = CoopRole.Client;
            ActivateLiveModuleHooks();
            GuestBorrowedWorld = true; // block ALL saves until we're back at the title screen
            // Sent in our Hello; empty for a plain "Join LAN", non-empty only when an invite
            // code carried the host's lobby password.
            _joinPassword = password ?? "";
            StatusLine = "Connecting to " + ip + "...";
            var net = new Transport { KeepaliveMessage = new PingMessage() };
            _net = net;
            // A code from a host on a non-default port has to win over our own config; a
            // nonsense value falls back rather than throwing at the socket.
            int port = (joinPort > 0 && joinPort <= 65535) ? joinPort : CoopPlugin.Port.Value;
            new Thread(() =>
            {
                try
                {
                    net.StartClient(ip, port);
                    QueueMainThread("connect-established", () =>
                    {
                        StatusLine = "Connected - requesting world...";
                        SendHello();
                    }, true);
                }
                catch (Exception e)
                {
                    QueueMainThread("connect-failed", () =>
                    {
                        ErrorLine = "Could not connect: " + e.Message;
                        Shutdown(null);
                    }, false);
                }
            }) { IsBackground = true, Name = "CoopConnect" }.Start();
        }

        public void Disconnect()
        {
            Shutdown("disconnected");
        }

        public void SendEmote()
        {
            if (_net != null && Role != CoopRole.None)
                Broadcast(new EmoteMessage { Emote = 1 });
        }

        /// <summary>Client: route a locally-earned gain/spend to the host's real economy.
        /// kinds: 1 AddCoin, 2 ReduceCoin, 3 AddShopExp, 4 AddFame.</summary>
        public void ForwardContribution(byte kind, float value)
        {
            if (Role != CoopRole.Client || _net == null) return;
            Send(1, new EconContributionMessage { Kind = kind, Value = value });
        }

        /// <summary>Client: replay a handheld-deodorant hold-spray against the HOST'S real
        /// customers. On the guest the sprayed customers are collider-less render puppets, so
        /// the vanilla RaycastHoldSprayState loop (InteractionPlayerController ~1628-1631)
        /// hits only inert local copies and the spray does nothing. We forward the spray
        /// origin + range + potency so the host runs the exact same DeodorantSprayCheck against
        /// its authoritative customers; the smelly-flag change then mirrors back to every guest
        /// through NpcSync automatically. No-op unless we're the guest.
        /// CALL CONTRACT: the GamePatches hold-spray hook (another file) invokes this once per
        /// vanilla spray tick with the SAME arguments the vanilla body passes -
        /// m_CurrentHoldSprayItem.transform.position, range 2.5f, potency 1.</summary>
        public void ForwardSprayHit(Vector3 pos, float range, int potency)
        {
            if (Role != CoopRole.Client || _net == null) return;
            Send(1, new SprayHitMessage { Position = pos, Range = range, Potency = potency });
        }

        /// <summary>Single marshalling point for transfer workers. Unity/game APIs must only be
        /// touched by actions drained from Update.</summary>
        internal static void EnqueueMainThread(Action action)
        {
            if (action == null) return;
            var core = Instance;
            if (core == null) throw new InvalidOperationException("CoopCore is not running");
            core.QueueMainThread("external-main-thread", action, false);
        }

        /// <summary>Host: relay a customer speech bubble after vanilla has actually
        /// displayed it. Speech is a one-shot cosmetic event, so it uses the reliable lane.</summary>
        public void ForwardNpcSpeech(ushort index, int identity, string text, float offsetUp)
        {
            if (Role != CoopRole.Host || _net == null || string.IsNullOrEmpty(text)) return;
            Broadcast(new NpcSpeechMessage
            {
                Kind = 0,
                Index = index,
                Identity = identity,
                Text = text,
                OffsetUp = offsetUp,
            });
        }

        /// <summary>Both roles: mirror a collection change (pack pull, trash, sale) to the
        /// other side so there is one shared binder.</summary>
        public void ForwardCardDelta(CardData card, int amount, bool isAdd)
        {
            if (Role == CoopRole.None || _net == null || card == null || amount <= 0) return;
            // QUEUED, not sent: a bulk "collect all machines" fires hundreds of these in one
            // frame and one reliable frame each overran the send lane. FlushCardDeltaOutbox
            // ships them as CardDeltaBatch at the end of the frame - or right now, if anything
            // else tries to send first (see the send helpers). The card is SNAPSHOT because
            // the postfix restores the live object's encoded grade the moment we return.
            _cardDeltaOutbox.Add(new PendingCard { IsAdd = isAdd, Amount = amount, Card = SnapshotCard(card) });
        }

        /// <summary>Host: send a card delta to ONE peer instead of broadcasting - for when
        /// only the sender's local state needs repair (e.g. a rejected re-grade submission,
        /// where the submitting guest alone removed the graded card from its album and no
        /// other peer's collection changed).</summary>
        public void SendCardDeltaTo(int connId, CardData card, int amount, bool isAdd)
        {
            if (Role != CoopRole.Host || _net == null || card == null || amount <= 0) return;
            Send(connId, new CardDeltaMessage { IsAdd = isAdd, Amount = amount, Card = card });
        }

        /// <summary>Both roles: mirror a GRADED-card removal (trade-in, donation, re-grade).
        /// Graded cards live in a separate album ReduceCard/CardDelta never touch, so this
        /// is its own message; the receiver applies RemoveGradedCard by identity.</summary>
        public void ForwardGradedRemoval(CardData card)
        {
            if (Role == CoopRole.None || _net == null || card == null || card.cardGrade <= 0) return;
            Broadcast(new GradedRemoveMessage { Card = card });
        }

        /// <summary>Client: the joiner bought restock - spawn the delivery on the host.</summary>
        public void ForwardOrder(int restockIndex, int count)
        {
            if (Role != CoopRole.Client || _net == null) return;
            // identity, never the raw index: modded restock lists (EPL packs) can be
            // ordered differently per machine - a raw index once turned a hololive
            // pack order into a $43 vanilla pack on the host
            RestockData rd = null;
            try { rd = InventoryBase.GetRestockData(restockIndex); } catch { }
            if (rd == null)
            {
                CoopPlugin.Log.LogWarning($"order: bad restock index {restockIndex}");
                return;
            }
            // the vanilla line cost rides along so a failed delivery can be REFUNDED -
            // the wallet charge already went through before the spawn call we intercept
            float lineCost = 0f;
            try
            {
                lineCost = CPlayerData.GetItemCost(rd.itemType)
                    * RestockManager.GetMaxItemCountInBox(rd.itemType, rd.isBigBox) * count;
            }
            catch { }
            Send(1, new OrderRequestMessage
            {
                ItemType = rd.itemType,
                IsBig = rd.isBigBox,
                Name = rd.name ?? "",
                Count = count,
                LineCost = lineCost,
            });
        }

        /// <summary>Either side bought a product license: share it by identity.</summary>
        public void ForwardLicense(int restockIndex)
        {
            if (Role == CoopRole.None || _net == null) return;
            RestockData rd = null;
            try { rd = InventoryBase.GetRestockData(restockIndex); } catch { }
            if (rd == null) return;
            _lastLicenseBuyTime = UnityEngine.Time.realtimeSinceStartupAsDouble;
            int itemType = (int)rd.itemType;
            bool isBig = rd.isBigBox;
            string rdName = rd.name ?? "";
            // rdName is the durable identity (ApplyLicenseUnlock falls back to it), but the id
            // is tried FIRST, so an untranslated modded id from a permuted registry unlocks the
            // wrong product on the peer. Host-side these writes are the identity function.
            var license = new LicenseUnlockMessage { ItemType = (EItemType)itemType, IsBig = isBig, RestockName = rdName };
            if (Role == CoopRole.Host)
                Broadcast(license);
            else
                Send(1, license);
        }

        // ---- EPL virtual catalog bridge ----
        // EPL never ADDS modded products to m_RestockDataList: it INTERCEPTS the
        // game's list accesses (count/indexing) and serves the extra entries from
        // its own ItemLibrary. Direct list reads from THIS assembly see only the
        // ~135 vanilla rows - which is why hosts "didn't have" products sitting on
        // their own shelves, catalogs compared "identical (135)", and modded
        // license heals missed. Every catalog walk must span rawCount + EPL's
        // entries and read rows through the game's INTERCEPTED GetRestockData
        // (calling a game method executes its rewritten body - field-proven by
        // ForwardOrder reading modded identities on the client).
        private static bool _eplProbed;
        private static System.Reflection.PropertyInfo _eplAssetsProp, _eplItemLibProp, _eplRestockProp;

        private static int EplExtraCount()
        {
            try
            {
                if (!_eplProbed)
                {
                    _eplProbed = true;
                    // assembly-qualified bind first, app-domain type walk only if it misses -
                    // see Util.ModParity.ResolveType for why the walk is worth avoiding
                    var t = Util.ModParity.ResolveType("EnhancedPrefabLoader.Core.EplRuntimeData", "EnhancedPrefabLoader");
                    const BindingFlags F = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                    _eplAssetsProp = t?.GetProperty("Assets", F);
                    var assets = _eplAssetsProp?.GetValue(null);
                    _eplItemLibProp = assets?.GetType().GetProperty("ItemLibrary", F);
                    var lib = assets == null ? null : _eplItemLibProp?.GetValue(assets);
                    _eplRestockProp = lib?.GetType().GetProperty("RestockEntries", F);
                    CoopPlugin.Log.LogInfo(_eplRestockProp != null
                        ? "EPL catalog bridge active (virtual restock entries visible)"
                        : "EPL catalog bridge inactive (EPL absent or its internals changed) - vanilla catalog only");
                }
                var a = _eplAssetsProp?.GetValue(null);
                var l = a == null ? null : _eplItemLibProp?.GetValue(a);
                return (l == null ? null : _eplRestockProp?.GetValue(l) as System.Collections.ICollection)?.Count ?? 0;
            }
            catch { return 0; }
        }

        /// <summary>Full catalog size as the GAME sees it: raw vanilla rows plus EPL's
        /// intercepted virtual entries.</summary>
        private static int CatalogCount()
        {
            int raw = 0;
            try { raw = Inv().m_StockItemData_SO.m_RestockDataList.Count; } catch { }
            return raw + EplExtraCount();
        }

        /// <summary>Catalog row through the game's intercepted accessor (valid for
        /// vanilla AND virtual indexes); null when out of range or unresolvable.</summary>
        private static RestockData CatalogAt(int i)
        {
            try { return InventoryBase.GetRestockData(i); }
            catch { return null; }
        }

        /// <summary>Find OUR restock entry for a partner's (itemType, boxSize) identity.
        /// Tiered: exact -> name+size -> same product any size -> name any size. Content
        /// DATA packs are invisible to the plugin-parity hash, so catalogs CAN differ
        /// between machines - a near-match beats a silently lost order.</summary>
        private static int ResolveRestockIndex(int itemType, bool isBig, string name, out bool sizeDiffers)
        {
            sizeDiffers = false;
            try
            {
                int n = CatalogCount();
                for (int i = 0; i < n; i++)
                {
                    var rd = CatalogAt(i);
                    if (rd != null && (int)rd.itemType == itemType && rd.isBigBox == isBig)
                        return i;
                }
                if (!string.IsNullOrEmpty(name))
                    for (int i = 0; i < n; i++)
                    {
                        var rd = CatalogAt(i);
                        if (rd != null && rd.name == name && rd.isBigBox == isBig)
                            return i;
                    }
                sizeDiffers = true;
                for (int i = 0; i < n; i++)
                {
                    var rd = CatalogAt(i);
                    if (rd != null && (int)rd.itemType == itemType)
                        return i;
                }
                if (!string.IsNullOrEmpty(name))
                    for (int i = 0; i < n; i++)
                    {
                        var rd = CatalogAt(i);
                        if (rd != null && rd.name == name)
                            return i;
                    }
            }
            catch { }
            return -1;
        }

        private bool ApplyLicenseUnlock(int itemType, bool isBig, string name)
        {
            int idx = ResolveRestockIndex(itemType, isBig, name, out _);
            if (idx < 0)
            {
                CoopPlugin.Log.LogWarning($"license unlock: no local product for type {itemType} big={isBig} '{name}'");
                return false;
            }
            if (CPlayerData.GetIsItemLicenseUnlocked(idx)) return true; // already ours
            Patches.GamePatches.ApplyingRemoteLicense = true;
            try
            {
                CPlayerData.SetUnlockItemLicense(idx);
                // the vanilla purchase's non-UI side effects: achievements, the global
                // flag, and the TUTORIAL TASK credit - without the last one the host's
                // "Unlock Basic Card Box" task never cleared when the joiner bought it
                try { AchievementManager.OnItemLicenseUnlocked((EItemType)itemType); } catch { }
                try { GameInstance.m_IsItemLicenseUnlocked = true; } catch { }
                try
                {
                    if ((EItemType)itemType == EItemType.BasicCardBox)
                        TutorialManager.AddTaskValue(ETutorialTaskCondition.UnlockBasicCardBox, 1f);
                }
                catch { }
            }
            finally { Patches.GamePatches.ApplyingRemoteLicense = false; }
            RefreshLicensePanels();
            CoopPlugin.Log.LogInfo($"license unlocked by partner: {(EItemType)itemType} big={isBig}");
            return true;
        }

        private static readonly FieldInfo FiPanelIndex = HarmonyLib.AccessTools.Field(typeof(RestockItemPanelUI), "m_Index");
        private static readonly FieldInfo FiPanelLicGrp = HarmonyLib.AccessTools.Field(typeof(RestockItemPanelUI), "m_LicenseUIGrp");
        private static readonly FieldInfo FiPanelUIGrp = HarmonyLib.AccessTools.Field(typeof(RestockItemPanelUI), "m_UIGrp");

        /// <summary>A phone shop that's OPEN while a partner's license lands keeps showing
        /// the locked panel until reopened - flip freshly-unlocked panels the way the
        /// vanilla purchase button does. Runs only on license events (rare).</summary>
        private static void RefreshLicensePanels()
        {
            try
            {
                var panels = FindObjectsOfType<RestockItemPanelUI>(); // active = phone open
                foreach (var p in panels)
                {
                    if (!(FiPanelIndex?.GetValue(p) is int idx) || idx < 0) continue;
                    // no raw-list bounds check: modded panels carry VIRTUAL indexes
                    // beyond the raw flag list; the game's accessor handles them
                    bool on = false;
                    try { on = CPlayerData.GetIsItemLicenseUnlocked(idx); } catch { }
                    if (!on) continue;
                    (FiPanelLicGrp?.GetValue(p) as GameObject)?.SetActive(false);
                    (FiPanelUIGrp?.GetValue(p) as GameObject)?.SetActive(true);
                }
            }
            catch { }
        }

        // ---- product catalog diagnosis: content DATA packs (PTCGO expansions) are
        // invisible to the plugin-parity hash, so both sides can pass the handshake
        // while selling different product lists - orders for the missing ones fail.
        // The joiner sends its catalog once; the host reports any difference loudly. ----

        private bool _catalogSent;
        private float _catalogTimer;
        private int _lastCatalogSentHash;
        private readonly HashSet<int> _catalogWarnedConns = new HashSet<int>();
        private readonly Dictionary<int, string> _rosterNames = new Dictionary<int, string>();
        private HashSet<int> _clientPriced = new HashSet<int>();   // itemTypes the host has priced
        private HashSet<int> _incomingPriced = new HashSet<int>(); // scratch, swapped per apply

        private string ResolvePeerName(ulong steamId, string wireName)
        {
            string fallback = string.IsNullOrWhiteSpace(wireName) ? "Player" : wireName;
            if (steamId == 0 || _steam == null) return fallback;
            string nickname = _steam.FriendNickname(steamId);
            return string.IsNullOrWhiteSpace(nickname) ? fallback : nickname;
        }

        private void SendCatalogDigest()
        {
            try
            {
                int total = CatalogCount(); // vanilla + EPL virtual entries
                var entries = new List<RestockData>(total);
                // blank-name entries are mod UI placeholders (Collection Tracker's
                // restock-shop replacement adds two) - not orderable, not comparable
                for (int i = 0; i < total; i++)
                {
                    var rd = CatalogAt(i);
                    if (rd != null && !string.IsNullOrEmpty(rd.name)) entries.Add(rd);
                }
                var digest = new CatalogDigestMessage();
                int cnt = Mathf.Min(entries.Count, ushort.MaxValue);
                for (int i = 0; i < cnt; i++)
                {
                    // The name hash rides along, but CatalogKey mixes the ID INTO the same
                    // key rather than falling back to the name, so an untranslated modded id
                    // makes an identical product read as "differs". That is exactly the false
                    // report this diagnostic produced in the field (a constant +6 offset on
                    // four of five "conflicts"), so the id is translated like any other.
                    digest.Entries.Add(new CatalogDigestEntry
                    {
                        ItemType = entries[i].itemType,
                        IsBigBox = entries[i].isBigBox,
                        NameHash = Fnv(entries[i].name ?? ""),
                    });
                }
                Send(1, digest);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("catalog digest: " + e.Message); }
        }

        private void CompareCatalogs(CatalogDigestMessage message, int connId)
        {
            var joiner = new HashSet<long>();
            for (int i = 0; i < message.Entries.Count; i++)
            {
                var e = message.Entries[i];
                // Host side, so this is the identity function; the joiner already translated.
                // A product the host does not have arrives as None and simply fails to match
                // any host row - which is the truth this diagnostic is trying to report.
                int t = (int)e.ItemType;
                bool big = e.IsBigBox;
                int nameHash = e.NameHash;
                joiner.Add(CatalogKey(t, big, nameHash));
            }
            if (Inv() == null) return; // no live world to compare against; the next digest retries
            int total = CatalogCount(); // vanilla + EPL virtual entries
            int hostOnly = 0, shared = 0;
            var examples = new List<string>();
            for (int i = 0; i < total; i++)
            {
                var rd = CatalogAt(i);
                if (rd == null || string.IsNullOrEmpty(rd.name)) continue; // placeholder rows, see SendCatalogDigest
                if (joiner.Contains(CatalogKey((int)rd.itemType, rd.isBigBox, Fnv(rd.name))))
                {
                    shared++;
                    continue;
                }
                hostOnly++;
                if (examples.Count < 6) examples.Add(rd.name);
            }
            int joinerOnly = joiner.Count - shared;
            if (hostOnly == 0 && joinerOnly == 0)
            {
                CoopPlugin.Log.LogInfo($"catalog check: identical ({shared} products)");
                // content mods register products seconds-to-minutes after load, so an
                // early check can cry wolf; the recheck should also retract the cry
                if (_catalogWarnedConns.Remove(connId))
                {
                    const string clear = "catalogs match now - the earlier warning was mod startup timing, all good";
                    RegisterLine = clear;
                    RegisterLineTimer = 8f;
                    Send(connId, new ToastMessage { Text = clear });
                }
                return;
            }
            string who = PeerNames.TryGetValue(connId, out var nm) ? nm : "joiner";
            string summary = $"heads-up: product catalogs differ ({hostOnly} only on host, {joinerOnly} only on {who}) - mismatched items can't be ordered; match your content packs";
            CoopPlugin.Log.LogWarning("catalog check: " + summary
                + (examples.Count > 0 ? " | host-only e.g.: " + string.Join(" / ", examples.ToArray()) : ""));
            _catalogWarnedConns.Add(connId);
            RegisterLine = summary;
            RegisterLineTimer = 10f;
            Send(connId, new ToastMessage { Text = summary });
        }

        // ---- graded-album divergence report (report-only) + opt-in one-way adopt ----
        //
        // Modelled line for line on the catalog digest above, for the same reason it exists: two
        // peers can pass every handshake and still hold different graded cards, and until now the
        // only trace was a "graded remove ... not in this album" warning that named one card at a
        // time and never said how deep the difference went.
        //
        // NOTHING HERE CHANGES A CARD BY ITSELF. The digest reports; the adopt button is a human
        // pressing a button, one-way, add-only. That is deliberate and the two rejected
        // alternatives are worth naming: auto-adopting the HOST's superset would have DELETED
        // roughly eight graded cards from the guest in the field case that prompted this (the host
        // was the deficient side), and a two-way merge hand-feeds GO's duplicate-cert anti-cheat -
        // put two different cards carrying one cert on one machine and BOTH get flagged FAKE, so a
        // merge converts real cards into fakes and is strictly worse than doing nothing.

        private bool _gradedSent;
        private float _gradedTimer;
        /// <summary>-1, not 0, so a guest whose graded album is genuinely EMPTY still sends its
        /// first (empty) digest instead of matching a zero-initialised hash and reporting nothing
        /// for the whole session.</summary>
        private int _lastGradedHash = -1;
        /// <summary>Peers we have EVER put a graded-drift alert ON SCREEN for. Read only by
        /// GradedAlertMode.OncePerSession; nothing but a new world or a dead session clears it.
        /// Separate from <see cref="_gradedAlertStanding"/> on purpose: "has this player ever
        /// been told" and "is an alarm currently up" answer different questions, and OncePerSession
        /// needs the first to survive the retraction that clears the second.</summary>
        private readonly HashSet<int> _gradedAlertEverShown = new HashSet<int>();
        /// <summary>Peers whose graded-drift alert is currently STANDING - put on screen and not
        /// yet retracted. THE ONLY LATCH THE ALL-CLEAR READS, and the whole retraction contract:
        /// it is set only inside the host's showAlert branch and cleared only when a later digest
        /// comes back identical, so an all-clear can never fire for an alarm the config
        /// suppressed, and a player who never saw the alarm never gets one. Nothing else touches
        /// it - notably a press of the adopt button does NOT, because a press does not prove the
        /// albums now match.</summary>
        private readonly HashSet<int> _gradedAlertStanding = new HashSet<int>();
        /// <summary>Per peer, the graded cards THEY have that WE do not - the adopt candidates.
        /// Populated on both roles: the host builds it from the guest's digest, and the guest
        /// builds it from the digest the host sends back when it finds a divergence.</summary>
        private readonly Dictionary<int, List<Util.GradingInterop.GradedEntry>> _gradedPeerOnly =
            new Dictionary<int, List<Util.GradingInterop.GradedEntry>>();

        /// <summary>One offer row for the F2 panel. Rebuilt on the main thread whenever the diff
        /// changes; CoopUI latches the list in its Layout pass (IMGUI matches Layout to Repaint by
        /// control index, so a row count that changes mid-frame throws over the whole window).</summary>
        public struct GradedAdoptOffer { public int ConnId; public string Who; public int Count; }
        public readonly List<GradedAdoptOffer> GradedAdoptOffers = new List<GradedAdoptOffer>();

        private static int GradedHash(List<Util.GradingInterop.GradedEntry> inv)
        {
            // Order-INdependent: the album list shifts on every RemoveAt, and an order-sensitive
            // hash would re-send the identical set every time a card moved position.
            int h = inv.Count;
            for (int i = 0; i < inv.Count; i++) h ^= inv[i].Key.GetHashCode();
            return h;
        }

        private void SendGradedDigest(int connId, List<Util.GradingInterop.GradedEntry> inv)
        {
            // "Cannot check now" never goes on the wire - a null union is a world still loading,
            // and the peer would read the short list as our real album (see
            // GradingInterop.BuildGradedCertInventory). Both callers already skip on null; this
            // is the backstop that keeps a future third caller from shipping a partial view.
            if (inv == null) return;
            try
            {
                var message = new GradedDigestMessage();
                for (int i = 0; i < inv.Count; i++)
                {
                    var e = inv[i];
                    message.Entries.Add(new GradedDigestEntry
                    {
                        Expansion = e.Expansion,
                        Monster = e.Monster,
                        Border = e.Border,
                        IsFoil = e.IsFoil,
                        IsDestiny = e.IsDestiny,
                        Encoded = e.Encoded,
                    });
                }
                Send(connId, message);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("graded digest: " + e.Message); }
        }

        // Graded-digest and catalog-digest wire layouts are owned by their DTOs
        // (GradedDigestMessage / CatalogDigestMessage) in Net\Messages.

        /// <summary>One entry as a report line. <paramref name="full"/> swaps in the complete
        /// save-index identity for the lines that assert two entries are DIFFERENT cards - see
        /// <see cref="CardIdentFull"/>; everywhere else the short name keeps the summaries
        /// readable.</summary>
        private static string GradedDesc(Util.GradingInterop.GradedEntry e, bool full = false)
        {
            int company, cert;
            Util.GradingInterop.DecodeCert(e.Encoded, out company, out cert);
            string s = (full ? CardIdentFull(e) : CardIdent(e.ToCard())) + " grade " + Util.GradingInterop.Actual(e.Encoded);
            if (cert > 0) s += " (cert " + cert + ")";
            if (Util.GradingInterop.CheatFlagged(e.Encoded)) s += " [FAKE-flagged]";
            return s;
        }

        /// <summary>Card id for the two lines that have to say WHY <see cref="SameCard"/> said no.
        /// CardIdent alone names only SOME of the identity, so a divergence in the rest rendered as
        /// the SAME STRING TWICE - the field log read "cert 4050 is AscendedHeroesEXP#411 here and
        /// AscendedHeroesEXP#411 on rocio", i.e. a collision reported against what it itself names
        /// as one card. This spells out all five fields <see cref="SameCard"/> compares, so a line
        /// that says DIFFERENT can always be checked against a line that says which field differed.
        ///
        /// The expansion is printed HERE rather than left to CardIdent, and that is the whole point
        /// of this method rather than an accident: CardIdent only carries the expansion on two of
        /// its three arms - a modded id renders "Expansion#N" and an unresolved one renders
        /// "unknown-pack card #N", but every VANILLA expansion falls into
        /// <c>(int)expansionType &lt; (int)ECardExpansionType.MAX</c> and returns the bare
        /// <c>monsterType.ToString()</c> with no expansion at all (CoopCore.cs:1568). SameCard does
        /// compare Expansion, so without this prefix two vanilla sets sharing a monster ordinal
        /// were exactly the illegible repeat this method exists to kill. Naming it unconditionally
        /// also means CardIdent can keep changing its own branching without silently re-opening
        /// that hole; the cost is that a modded id repeats its expansion, which is noise in a
        /// diagnostic line and cheap next to another unreadable field report.
        ///
        /// Border and foil are the rest of what CPlayerData.GetCardSaveIndex consumes
        /// (decompiled/CPlayerData.cs:795-811) and isDestiny is the one identity field SameCard
        /// checks from outside it. Reached by the CERT COLLISION line and, through
        /// <c>GradedDesc(e, full: true)</c>, by the adopt's DIFFERENT-card refusal; every other
        /// report line is about a card rather than a disagreement over one and keeps the short
        /// name.</summary>
        private static string CardIdentFull(Util.GradingInterop.GradedEntry e)
        {
            return e.Expansion + " " + CardIdent(e.ToCard()) + " " + e.Border
                + (e.IsFoil ? " foil" : "") + (e.IsDestiny ? " destiny" : "");
        }

        /// <summary>Same CARD, ignoring the grade - every field that feeds
        /// CPlayerData.GetCardSaveIndex (monster, border, foil) plus expansion and isDestiny.
        /// Border and foil are in here on purpose: two border variants of one monster are two
        /// different save slots, so calling them "the same card" would file a cert clash as a
        /// harmless re-encoding.</summary>
        private static bool SameCard(Util.GradingInterop.GradedEntry a, Util.GradingInterop.GradedEntry b)
        {
            return a.Expansion == b.Expansion && a.Monster == b.Monster && a.Border == b.Border
                && a.IsFoil == b.IsFoil && a.IsDestiny == b.IsDestiny;
        }

        /// <summary>Set difference between the peer's graded-cert union and ours. Report only -
        /// it never adds, removes or rewrites a card. <paramref name="isHost"/> owns the two
        /// things only a host may do: toast the sender, and answer EVERY digest with our own so
        /// the guest can see ITS one-sided half and can retract a stale offer (the guest must
        /// never answer back, or the two would ping-pong digests forever); only a divergence
        /// toasts.
        ///
        /// TWO SETS, TWO JOBS - do not collapse them back into one. `mineKeys` is the NARROW
        /// walk, because it is compared against a peer that can only see the mirrored containers
        /// and because the same list is what SendGradedDigest puts on the wire; `mineByCert` is
        /// the WIDE union (narrow + GradingInterop.LocalOnlyGradedCerts), because every question
        /// it answers is "does Grading Overhaul already see this cert on this PC?" and GO counts
        /// the hand. Widening mineByCert can only ever move a card OUT of the adopt offer and
        /// into a report count - peerOnlyTotal is still gated on the narrow mineKeys - so it
        /// cannot inflate the divergence.</summary>
        private void CompareGradedDigests(GradedDigestMessage message, int connId, bool isHost)
        {
            var theirs = new List<Util.GradingInterop.GradedEntry>(message.Entries.Count);
            for (int i = 0; i < message.Entries.Count; i++)
            {
                var e = message.Entries[i];
                theirs.Add(new Util.GradingInterop.GradedEntry
                {
                    Expansion = e.Expansion,
                    Monster = e.Monster,
                    Border = e.Border,
                    IsFoil = e.IsFoil,
                    IsDestiny = e.IsDestiny,
                    Encoded = e.Encoded,
                });
            }
            var mine = Util.GradingInterop.BuildGradedCertInventory();
            // FAIL CLOSED DURING A LOAD. Null is "cannot check now", never "my album is empty":
            // the live containers fill progressively while the world spawns, and a short union
            // here reports the peer's whole shelf as one-sided and offers it for adoption. Skip
            // the cycle entirely - the digest timer brings us straight back.
            if (mine == null)
            {
                CoopPlugin.Log.LogInfo("graded album check: skipped - the world is still loading here, so this PC cannot see its own shelves yet");
                return;
            }

            var mineKeys = new HashSet<string>();
            var mineByCert = new Dictionary<long, Util.GradingInterop.GradedEntry>();
            for (int i = 0; i < mine.Count; i++)
            {
                mineKeys.Add(mine[i].Key);
                long ck = Util.GradingInterop.CertKey(mine[i].Encoded);
                if (ck != 0L && !mineByCert.ContainsKey(ck)) mineByCert[ck] = mine[i];
            }
            // The local-only half of the GUARD set, mirrored containers first so a collision
            // report names the card the peer could actually be told about.
            var localOnly = Util.GradingInterop.LocalOnlyGradedCerts();
            for (int i = 0; i < localOnly.Count; i++)
            {
                long ck = Util.GradingInterop.CertKey(localOnly[i].Encoded);
                if (ck != 0L && !mineByCert.ContainsKey(ck)) mineByCert[ck] = localOnly[i];
            }

            string who = PeerNames.TryGetValue(connId, out var nm) ? nm : (isHost ? "the joiner" : "the host");

            var peerOnly = new List<Util.GradingInterop.GradedEntry>();
            var peerExamples = new List<string>();
            var collisions = new List<string>();
            var reEncodings = new List<string>();
            // Certs the two PCs agree on the CARD for but disagree on the ENCODING of. Both
            // halves are excluded from the one-sided counts below - they are ONE card in two
            // states, and printing them as "only here" plus "only on theirs" is what made the
            // field report read as two unrelated missing cards.
            var reEncodedCerts = new HashSet<long>();
            var theirKeys = new HashSet<string>();
            int peerOnlyTotal = 0, peerOnlyNoContent = 0, collisionTotal = 0;
            int reEncodedTotal = 0, peerOnlyCertHeld = 0;
            for (int i = 0; i < theirs.Count; i++)
            {
                var e = theirs[i];
                theirKeys.Add(e.Key);

                // Does a card carrying THIS cert already live somewhere on this PC - anywhere
                // Grading Overhaul can see it, hand and submit scratch set included? Decided
                // once, up front, because it gates both the report category and adopt candidacy,
                // and it reads the WIDE mineByCert for the reason given on this method.
                long theirCk = Util.GradingInterop.CertKey(e.Encoded);
                Util.GradingInterop.GradedEntry m = default(Util.GradingInterop.GradedEntry);
                bool certHeldHere = theirCk != 0L && mineByCert.TryGetValue(theirCk, out m);
                bool sameCard = certHeldHere && SameCard(m, e);
                // Same cert, same card, DIFFERENT encoding - the field symptom (host 1380002639
                // vs wire 380002639: identical company/grade/cert, one side carrying GO's +1e9
                // FAKE flag). Its own category: nothing is missing, GO's duplicate-cert sweep has
                // already fired on one side, and there is no add that repairs it.
                bool reEncoded = sameCard && m.Encoded != e.Encoded;
                if (reEncoded)
                {
                    reEncodedTotal++;
                    reEncodedCerts.Add(theirCk);
                    if (reEncodings.Count < 8)
                        reEncodings.Add($"{GradedDesc(m)} here vs {GradedDesc(e)} on {who}");
                }

                if (!mineKeys.Contains(e.Key) && !reEncoded)
                {
                    peerOnlyTotal++;
                    if (peerExamples.Count < 8) peerExamples.Add(GradedDesc(e));
                    // Only cards this install could actually PLACE, AND whose cert this PC does
                    // not already hold, become adopt candidates - so the button's count is the
                    // number it will really add. A card from a content pack this PC does not
                    // have still belongs in the REPORT (that difference is real and is worth
                    // naming) but adopting it is impossible - GetCardSaveIndex would mis-index
                    // it into save slot 0 or throw. A card whose cert is already held here is
                    // refused by GradedAdopt for the reason spelled out on that guard, so
                    // offering it would promise an add that never happens.
                    if (certHeldHere) peerOnlyCertHeld++;
                    else if (CardSetInstalledHere(e.ToCard())) peerOnly.Add(e);
                    else peerOnlyNoContent++;
                }

                // CERT COLLISION - a separate category on purpose, and the one that silently
                // turns real cards into fakes. Certs are only unique while both GO save stores
                // agree; a role swap, or a session where the sidecar did not apply, leaves two
                // machines issuing the same serial. Merging those two cards onto one PC is what
                // GO's duplicate-cert sweep flags FAKE, so this is reported and NEVER repaired.
                if (certHeldHere && !sameCard)
                {
                    collisionTotal++;
                    int company, cert;
                    Util.GradingInterop.DecodeCert(e.Encoded, out company, out cert);
                    if (collisions.Count < 8)
                        collisions.Add($"cert {cert} is {CardIdentFull(m)} here and {CardIdentFull(e)} on {who}");
                }
            }

            int oursOnly = 0;
            var ourExamples = new List<string>();
            for (int i = 0; i < mine.Count; i++)
            {
                if (theirKeys.Contains(mine[i].Key)) continue;
                // the other half of a re-encoding pair - already reported as its own category
                if (reEncodedCerts.Contains(Util.GradingInterop.CertKey(mine[i].Encoded))) continue;
                oursOnly++;
                if (ourExamples.Count < 8) ourExamples.Add(GradedDesc(mine[i]));
            }

            if (peerOnly.Count > 0) _gradedPeerOnly[connId] = peerOnly;
            else _gradedPeerOnly.Remove(connId);
            RebuildGradedAdoptOffers();

            if (oursOnly == 0 && peerOnlyTotal == 0 && collisionTotal == 0 && reEncodedTotal == 0)
            {
                CoopPlugin.Log.LogInfo($"graded album check: identical ({mine.Count} graded cards)");

                // ANSWER EVERY DIGEST, NOT ONLY A DIVERGENT ONE. This reply is DATA and is never
                // gated by the alert config. Until 1.0.42 the host replied only from the
                // divergence tail below, which meant the guest re-ran this compare only while a
                // difference persisted: the moment the albums healed the host went quiet, the
                // guest's _gradedPeerOnly was never rewritten, the else-branch that erases it was
                // never reached, and its adopt button stood on a minutes-old list for the rest of
                // the session. A ten-second transient became a permanent button. With this reply
                // the guest sees the match, clears the list and retracts the row.
                // NO PING-PONG: the reply stays gated on isHost, and a guest that receives it
                // takes this same early return with isHost false, so the exchange is still
                // exactly one reply per guest-initiated digest.
                if (isHost) SendGradedDigest(connId, mine);

                // Withdraw the cry. A digest pair can still straddle a real in-flight change
                // (a card between two mirrored containers, a shelf placement racing the 45s
                // tick), so a check that finds nothing must be able to take the alarm back.
                bool alertStood = _gradedAlertStanding.Remove(connId);
                // SCREEN, so it follows the alert config by construction: an all-clear can only
                // fire for an alarm this config actually let onto the screen.
                if (isHost && alertStood)
                {
                    const string clear = "graded albums match now - the earlier difference is gone";
                    RegisterLine = clear;
                    RegisterLineTimer = 8f;
                    Send(connId, new ToastMessage { Text = clear });
                }
                return;
            }

            string summary = $"heads-up: graded albums differ ({oursOnly} only here, {peerOnlyTotal} only on {who}) - nothing was changed"
                + (peerOnly.Count > 0 ? "; the co-op panel can adopt the " + peerOnly.Count + " you're missing" : "")
                + (peerOnlyNoContent > 0 ? $" ({peerOnlyNoContent} of them are from content packs this PC doesn't have)" : "")
                + (peerOnlyCertHeld > 0 ? $" ({peerOnlyCertHeld} can't be adopted - this PC already holds those certificate numbers)" : "")
                + (reEncodedTotal > 0 ? $"; {reEncodedTotal} more are the SAME card with a different grade encoding" : "");

            // SCREEN ONLY, AND DECIDED ABOVE THE LOG ON PURPOSE. Everything below this point that
            // writes RegisterLine or sends a Toast is gated by this flag; NOTHING else is. The
            // LogWarning immediately below, the RE-ENCODED line, the CERT COLLISION line and the
            // reply digest all run whatever the player chose - a log the player hands to someone
            // else has to say everything the check found, and the reply digest is the only way the
            // peer learns about its own half of the difference. The adopt button is not gated
            // either: "stop shouting at me" is not "hide the repair".
            //
            // AND IT IS THE HOST'S COPY OF THE SETTING THAT DECIDES, in both directions. Every
            // screen-facing statement in this method - the RegisterLine, the Toast, and the
            // all-clear above - sits inside `if (isHost)`, so on a guest this flag is computed
            // and never read: a joiner who picks Never still receives the host's heads-up, and a
            // joiner who picks Always still gets nothing if the host picked Never. That is
            // deliberate for now and the config description says so out loud. Closing the gap
            // would mean putting a preference byte on the wire and having the host keep per-conn
            // preference state across rejoins - new wire surface in the release whose whole point
            // is that this diagnostic over-reached - and it would let a guest silence the ONLY
            // graded-drift signal that ever reaches them, about their own album.
            var alertMode = CoopPlugin.GradedDriftAlert.Value;
            bool showAlert = alertMode == GradedAlertMode.Always
                || (alertMode == GradedAlertMode.OncePerSession && !_gradedAlertEverShown.Contains(connId));

            CoopPlugin.Log.LogWarning("graded album check: " + summary
                + (ourExamples.Count > 0 ? " | only here e.g.: " + string.Join(" / ", ourExamples.ToArray()) : "")
                + (peerExamples.Count > 0 ? $" | only on {who} e.g.: " + string.Join(" / ", peerExamples.ToArray()) : ""));
            if (reEncodedTotal > 0)
                CoopPlugin.Log.LogWarning($"graded album check: RE-ENCODED - {reEncodedTotal} cert(s) sit on the SAME card on both PCs but carry a DIFFERENT encoded grade. "
                    + "This is NOT a missing card and adopting it would not repair it: the two rows are one card in two states, and the usual cause is Grading Overhaul's "
                    + "duplicate-cert sweep having already fired on one side and rewritten that row to its FAKE encoding (+1,000,000,000 - e.g. 1380002639 against a clean 380002639). "
                    + "Adding the clean twin here would only make GO flag BOTH, so these are reported and never offered for adoption. | " + string.Join(" / ", reEncodings.ToArray()));
            if (collisionTotal > 0)
                CoopPlugin.Log.LogWarning($"graded album check: CERT COLLISION - {collisionTotal} cert(s) exist on BOTH PCs bound to DIFFERENT cards. "
                    + "This is NOT a missing card and there is NO automated repair: bringing both copies onto one PC is exactly what makes Grading Overhaul flag both of them FAKE. "
                    + "The two save stores have drifted apart and one side's certs need re-issuing by hand. | " + string.Join(" / ", collisions.ToArray()));

            if (isHost)
            {
                if (showAlert)
                {
                    _gradedAlertEverShown.Add(connId);
                    _gradedAlertStanding.Add(connId);
                    RegisterLine = summary;
                    RegisterLineTimer = 10f;
                    // Written from the GUEST's point of view, not reused from the host's summary:
                    // "only here" on the host means "only on yours" to the reader of this toast, and
                    // a heads-up that says the opposite of what the player sees is worse than none.
                    string toast = $"heads-up: your graded albums differ ({peerOnlyTotal} graded cards only on yours, {oursOnly} only on the host's) - nothing was changed"
                        + (oursOnly > 0 ? "; open the co-op panel to adopt the ones you're missing" : "")
                        + (collisionTotal > 0 ? " - and some certificate numbers clash, see the log" : "")
                        + (reEncodedTotal > 0 ? $" - and {reEncodedTotal} card(s) carry a different grade encoding on each PC, see the log" : "");
                    Send(connId, new ToastMessage { Text = toast });
                }
                // Send OUR digest back so the guest can see the half of the difference that is on
                // ITS side, and offer the same adopt button. Same writer, opposite direction.
                // ALWAYS - this is data, and gating it would blind the guest, not quiet it.
                SendGradedDigest(connId, mine);
            }
        }

        /// <summary>Counts only the ADOPTABLE entries, never the list length: GradedAdopt leaves
        /// the ones it refused in <see cref="_gradedPeerOnly"/> (marked) so the difference is
        /// still reportable, and a peer whose whole diff turned out to be unrepairable must show
        /// NO button at all rather than one that promises an add and then refuses every row.</summary>
        private void RebuildGradedAdoptOffers()
        {
            GradedAdoptOffers.Clear();
            foreach (var kv in _gradedPeerOnly)
            {
                if (kv.Value == null || kv.Value.Count == 0) continue;
                int adoptable = 0;
                for (int i = 0; i < kv.Value.Count; i++) if (!kv.Value[i].Refused) adoptable++;
                if (adoptable == 0) continue;
                string who = PeerNames.TryGetValue(kv.Key, out var nm) ? nm
                    : (Role == CoopRole.Host ? "the joiner" : "the host");
                GradedAdoptOffers.Add(new GradedAdoptOffer { ConnId = kv.Key, Who = who, Count = adoptable });
            }
        }

        /// <summary>The F2 button. ONE WAY, ADD ONLY, NEVER AUTOMATIC: it adds the graded cards
        /// the peer reported and we do not have, and it removes nothing, ever.
        ///
        /// THE LOAD-BEARING GUARD IS THE CERT ONE, AND IT REFUSES ON CERT PRESENCE ALONE.
        /// Grading Overhaul's duplicate-cert sweep (AntiCheat_AddCard_Patch, decompiled-grading
        /// :8534-8573) matches candidates on (company, cert) and NOTHING ELSE - it never compares
        /// card identity - and it reaches the comparison through Helper.DecodeGradeFull, which
        /// STRIPS the +1,000,000,000 FAKE flag before decoding (:15953). Two consequences, both
        /// of which the old "cert on a DIFFERENT card" test walked straight into:
        ///  - a FAKE-flagged local twin of the very same card decodes to the very same
        ///    (company, cert), so it is already in our cert map and GO already counts it;
        ///  - adopting past it calls AddCard, the sweep sees two rows on one cert, and it rewrites
        ///    BOTH to the FAKE encoding.
        /// The old guard waved that same-card twin through because the expansion/monster matched.
        /// GO then mutated the freshly adopted copy, mineKeys had recorded the CLEAN key, so the
        /// next digest still reported the card as missing and every press appended another FAKE
        /// row. Hence: if the cert exists here at all, in any card, in any encoding, refuse.
        ///
        /// The rest of the path is the one ApplyCardDelta already uses for a received graded card:
        /// Remember (burns + binds the cert so GO's anti-cheat leaves it alone), then AddCard.
        /// ApplyingRemoteCards is held over the loop so our own AddCard postfix does not forward
        /// the repair back to the peer as a fresh card.</summary>
        public void GradedAdopt(int connId)
        {
            if (!_gradedPeerOnly.TryGetValue(connId, out var wanted) || wanted == null || wanted.Count == 0) return;
            // Without GO there is no cert to burn or bind, so every added card would land on GO's
            // absent anti-cheat as an unvouched encoded grade the moment the peer installs it -
            // and the digest that produced this list is itself empty-by-construction here. Say so
            // rather than adding cards nothing on this PC can account for.
            if (!Util.GradingInterop.Present)
            {
                RegisterLine = "Grading Overhaul isn't loaded here - graded cards can't be adopted";
                RegisterLineTimer = 8f;
                CoopPlugin.Log.LogWarning("graded adopt: refused - Grading Overhaul is not present on this PC, so a received cert cannot be burned or bound");
                return;
            }
            if (!InGameLevel())
            {
                RegisterLine = "load into the shop first, then adopt";
                RegisterLineTimer = 6f;
                return;
            }

            // Rebuilt AT PRESS TIME, never reused from the digest-time snapshot. Minutes can pass
            // between the digest and the click, and this union is the only thing standing between
            // the peer's list and a duplicate AddCard - a stale one re-offers cards that have
            // since arrived by any other route (delta sync, a grading job maturing, a box opened).
            //
            // THE FULL UNION HERE, BOTH SETS FROM IT. Unlike CompareGradedDigests, NOTHING in
            // this method goes on the wire and nothing is compared against the peer's view, so
            // there is no reason to stay narrow and every reason not to: a card in the player's
            // hand or staged on the grading submit screen is a card this PC already HAS, so it
            // must count as `alreadyHere`, and its cert must count as a clash. Grading Overhaul
            // reads those same two containers in its AddCard duplicate-cert scan
            // (decompiled-grading :8554-8573) and would flag BOTH copies FAKE the moment this
            // button added a second row on that cert.
            var mine = Util.GradingInterop.BuildGradedCertInventory();
            // Null is "cannot check now" - see BuildGradedCertInventory. During a world load the
            // live containers are still spawning, so the guard would be blind in exactly the
            // direction that lets a duplicate through. Refuse rather than adopt against a partial
            // view; the offer is still there when the world has finished loading.
            if (mine == null)
            {
                RegisterLine = "still loading the shop - try adopting again in a moment";
                RegisterLineTimer = 6f;
                CoopPlugin.Log.LogWarning("graded adopt: refused - the world is still loading, so this PC cannot yet see every place a graded card lives; adopting now could duplicate a certificate");
                return;
            }
            mine.AddRange(Util.GradingInterop.LocalOnlyGradedCerts());
            var mineKeys = new HashSet<string>();
            var mineByCert = new Dictionary<long, Util.GradingInterop.GradedEntry>();
            for (int i = 0; i < mine.Count; i++)
            {
                mineKeys.Add(mine[i].Key);
                long ck0 = Util.GradingInterop.CertKey(mine[i].Encoded);
                if (ck0 != 0L && !mineByCert.ContainsKey(ck0)) mineByCert[ck0] = mine[i];
            }

            int added = 0, alreadyHere = 0, certClash = 0, noContent = 0;
            // Refused candidates are KEPT (marked) so the difference stays visible in the report
            // instead of vanishing with the button; RebuildGradedAdoptOffers counts only the
            // unmarked ones, so a peer whose whole diff is unrepairable shows no button at all.
            var keep = new List<Util.GradingInterop.GradedEntry>();
            Patches.GamePatches.ApplyingRemoteCards = true;
            try
            {
                for (int i = 0; i < wanted.Count; i++)
                {
                    var e = wanted[i];
                    e.Refused = false;
                    if (mineKeys.Contains(e.Key)) { alreadyHere++; continue; } // arrived since the digest
                    var card = e.ToCard();
                    if (!CardSetInstalledHere(card))
                    {
                        noContent++;
                        e.Refused = true; keep.Add(e);
                        CoopPlugin.Log.LogWarning($"graded adopt: {GradedDesc(e)} is from a card set you don't have installed - skipped");
                        continue;
                    }
                    long ck = Util.GradingInterop.CertKey(e.Encoded);
                    if (ck != 0L && mineByCert.TryGetValue(ck, out var clash))
                    {
                        certClash++;
                        e.Refused = true; keep.Add(e);
                        // Two genuinely different faults, so two distinct wordings - reading
                        // "already exists on a different card" under a same-card FAKE twin is
                        // what sent the last investigation looking for a card that was never
                        // there. SameCard compares the full save-index identity, not just the
                        // monster, so a border/foil variant still reads as the collision it is.
                        // The ALARMING arm now prints the FULL save-index identity on both sides so
                        // the line can be CHECKED rather than appearing to name one card twice: the
                        // field report that read "cert 4050 is AscendedHeroesEXP#411 here and
                        // AscendedHeroesEXP#411 on rocio" was almost certainly a REAL collision and
                        // merely illegible, the two entries differing by border, foil, destiny or
                        // expansion - none of which CardIdent is guaranteed to print (see
                        // CardIdentFull). Both arms refuse the adopt either way - the choice only
                        // ever decides what the log says.
                        if (!SameCard(clash, e))
                            CoopPlugin.Log.LogWarning($"graded adopt: REFUSED {GradedDesc(e, full: true)} - that certificate number is already on this PC bound to a DIFFERENT card, {GradedDesc(clash, full: true)}. "
                                + "Adding it would make Grading Overhaul flag BOTH cards FAKE, so it is left alone.");
                        else
                            CoopPlugin.Log.LogWarning($"graded adopt: REFUSED {GradedDesc(e)} - you already hold that cert; the local copy is FAKE-flagged (or identical): {GradedDesc(clash)}. "
                                + "Grading Overhaul's duplicate-cert sweep matches on (company, cert) alone and decodes past the FAKE flag, so adopting would make it flag both.");
                        continue;
                    }
                    Util.GradingInterop.Remember(card);
                    CPlayerData.AddCard(card, 1);
                    added++;
                    mineKeys.Add(e.Key);
                    if (ck != 0L && !mineByCert.ContainsKey(ck)) mineByCert[ck] = e;
                }
            }
            catch (Exception ex) { CoopPlugin.Log.LogWarning("graded adopt: " + ex.Message); }
            finally { Patches.GamePatches.ApplyingRemoteCards = false; }

            _binderRefreshPending = true;
            if (keep.Count > 0) _gradedPeerOnly[connId] = keep;
            else _gradedPeerOnly.Remove(connId);
            RebuildGradedAdoptOffers();
            _lastGradedHash = -1; // our album changed - re-digest on the next client tick

            string line = $"adopted {added} graded card(s)"
                + (alreadyHere > 0 ? $", {alreadyHere} already here" : "")
                + (certClash > 0 ? $", {certClash} refused (cert already on this PC - see the log)" : "")
                + (noContent > 0 ? $", {noContent} from missing content packs" : "");
            // A PRESS CHANGES NO ALARM STATE, AND THAT IS THE CONTRACT - do not "clear the
            // warning" here. The only thing that retracts a graded-drift alert is a later digest
            // that comes back IDENTICAL: CompareGradedDigests clears _gradedAlertStanding and
            // sends the all-clear, and only the HOST ever does either (the guest's standing set
            // is always empty, so nothing on the guest is waiting to be retracted). An adopt does
            // not prove the albums match - it repairs at most the half that is add-only, and what
            // typically REMAINS is one-sided the other way (cards only here) or unrepairable
            // (refused certs), which is precisely the case where the alarm SHOULD still stand.
            // Zero adoptable candidates remain for this peer by construction, every entry that
            // survived into `keep` was marked Refused, and the summary below is the whole of this
            // button's feedback.
            RegisterLine = line;
            RegisterLineTimer = 8f;
            CoopPlugin.Log.LogInfo("graded adopt: " + line);
        }

        /// <summary>"Did this file's CONTENT change?" as a cheap comparable string: length plus an
        /// FNV-1a-64 over the bytes. Absent files stamp as "-" so created-from-nothing reads as a
        /// change too. Errs to "?" on any IO fault, which compares unequal to itself and so at
        /// worst logs one extra notice.
        ///
        /// CONTENT, not metadata, and that is the whole point of the function. The obvious
        /// (length, LastWriteTimeUtc) stamp made the Grading Overhaul cert-store warning fire on
        /// EVERY join, because SidecarTransfer always rewrites the file whether or not the host's
        /// copy differs - so the mtime always advances and the stamp always changes. A warning
        /// that severe ("graded cards in this slot may now be flagged FAKE") has to mean something
        /// when it appears; one that cries on every single join is one players learn to scroll
        /// past, which is worse than not having it. Files here are a few KB of JSON, so hashing
        /// them twice per join costs nothing worth measuring.</summary>
        private static string FileStamp(string path)
        {
            try
            {
                if (!File.Exists(path)) return "-";
                var bytes = File.ReadAllBytes(path);
                unchecked
                {
                    ulong h = 14695981039346656037UL;
                    for (int i = 0; i < bytes.Length; i++) { h ^= bytes[i]; h *= 1099511628211UL; }
                    return bytes.Length + "#" + h.ToString("x16");
                }
            }
            catch { return "?"; }
        }

        private void LogCatalogCandidates(string name)
        {
            try
            {
                if (string.IsNullOrEmpty(name)) return;
                string probe = name.Split(' ')[0];
                int total = CatalogCount(); // vanilla + EPL virtual entries
                var found = new List<string>();
                for (int i = 0; i < total && found.Count < 8; i++)
                {
                    var rd = CatalogAt(i);
                    if (rd?.name != null && rd.name.IndexOf(probe, StringComparison.OrdinalIgnoreCase) >= 0)
                        found.Add($"{rd.name} (type {(int)rd.itemType}, big={rd.isBigBox})");
                }
                CoopPlugin.Log.LogInfo(found.Count > 0
                    ? "similar host entries: " + string.Join(" | ", found.ToArray())
                    : $"no host entries resembling '{probe}'");
            }
            catch { }
        }

        private static long CatalogKey(int type, bool big, int nameHash)
        {
            return ((long)type << 33) ^ ((long)(uint)nameHash << 1) ^ (big ? 1L : 0L);
        }

        /// <summary>Deterministic across machines (string.GetHashCode is not).</summary>
        private static int Fnv(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619; }
                return (int)h;
            }
        }

        /// <summary>Client: the joiner bought furniture - deliver it on the host.</summary>
        public void ForwardFurniture(int objType, Vector3 pos, Quaternion rot)
        {
            if (Role != CoopRole.Client || _net == null) return;
            Send(1, new FurnitureOrderMessage { ObjType = (EObjectType)objType, Position = pos, Rotation = rot });
        }

        /// <summary>Client: the joiner set an item price - the host's table is authoritative.</summary>
        public void ForwardItemPrice(EItemType itemType, float price)
        {
            if (Role != CoopRole.Client || _net == null) return;
            Send(1, new ItemPriceContribMessage { ItemType = itemType, Price = price });
            // Stamp it: the host's next PriceList was built BEFORE this contribution landed,
            // and applying that full table would visibly repaint our fresh price back.
            _myItemPriceEdits[(int)itemType] = new MyItemPrice { Value = price, At = Time.realtimeSinceStartupAsDouble };
            TrimMyItemPriceEdits();
        }

        /// <summary>Both roles: mirror a marked-card-price change.</summary>
        public void ForwardCardPrice(CardData card, float price)
        {
            if (Role == CoopRole.None || _net == null || card == null) return;
            Broadcast(new CardPriceSetMessage { Card = card, Price = price });
            // GUEST convergence. One fire-and-forget frame was all a card price ever got, and a
            // reliable frame the transport drops (the CardDelta flood) is gone for good - the
            // host's 3s price heal then "repaired" the card by broadcasting its own STALE value
            // back over the edit. Remember what we asked for, retry until the host confirms it,
            // and ignore any different value for this card until then (see the CardPriceSet
            // handler). The host needs none of this: its own write IS the authority, and
            // tracking it there would make the host permanently ignore guest edits.
            if (Role != CoopRole.Client) return;
            string key = CardPriceKey(card);
            if (key == null) return;
            _myCardPrices[key] = new MyCardPrice
            {
                Card = SnapshotCard(card), // the postfix restores the live object's encoded grade
                Value = price,
                Acked = false,
                LastSend = Time.realtimeSinceStartupAsDouble,
                Attempts = 1,
            };
            TrimMyCardPrices();
        }

        /// <summary>Cap the in-flight card-price table: the oldest ACKED entry goes first (it
        /// has nothing left to converge); only if nothing is acked do we drop an entry that is
        /// still trying.</summary>
        private void TrimMyCardPrices()
        {
            while (_myCardPrices.Count > MyCardPriceMax)
            {
                string victim = null;
                double oldest = double.MaxValue;
                foreach (var kv in _myCardPrices)
                    if (kv.Value.Acked && kv.Value.LastSend < oldest) { oldest = kv.Value.LastSend; victim = kv.Key; }
                if (victim == null)
                    foreach (var kv in _myCardPrices)
                        if (kv.Value.LastSend < oldest) { oldest = kv.Value.LastSend; victim = kv.Key; }
                if (victim == null) break;
                _myCardPrices.Remove(victim);
            }
        }

        private void TrimMyItemPriceEdits()
        {
            while (_myItemPriceEdits.Count > MyItemPriceMax)
            {
                int victim = 0;
                bool found = false;
                double oldest = double.MaxValue;
                foreach (var kv in _myItemPriceEdits)
                    if (!found || kv.Value.At < oldest) { oldest = kv.Value.At; victim = kv.Key; found = true; }
                if (!found) break;
                _myItemPriceEdits.Remove(victim);
            }
        }

        /// <summary>True when WE set this item's price within the hold window and the host's
        /// bulk table still disagrees - that PriceList was built before our edit arrived, so
        /// applying it would undo the edit in front of the player. Expired stamps are dropped
        /// here so the host's table goes back to winning.</summary>
        private bool HeldLocalItemPrice(int itemType, float incoming)
        {
            if (_myItemPriceEdits.Count == 0) return false;
            if (!_myItemPriceEdits.TryGetValue(itemType, out var e)) return false;
            if (Time.realtimeSinceStartupAsDouble - e.At >= ItemPriceHoldSeconds)
            {
                _myItemPriceEdits.Remove(itemType);
                return false;
            }
            // same tolerance as card prices, and for the same reason: the store rounds by up
            // to half a display quantum and the quantum differs per machine's local currency,
            // so a 0.0001f compare called an identical price "different" across a mixed pair
            return Math.Abs(e.Value - incoming) > CardPriceEpsilon;
        }

        /// <summary>Client, ~1Hz: re-send any card price the host has not confirmed. Nothing
        /// ever acked a CardPriceSet, so an edit lost with a dropped frame simply vanished.
        /// After CardPriceMaxAttempts we SURRENDER - drop our tracking entry so the host's
        /// heals take over again - and say so once, instead of retrying for the session.</summary>
        private void CardPriceRetryTick()
        {
            if (Role != CoopRole.Client || _net == null || _myCardPrices.Count == 0) return;
            // Mid-scene-load a guest would spend all 12 attempts against a world that isn't up
            // yet and surrender before the first one could ever be confirmed. LastSend keeps
            // aging while we're out, so retries resume immediately once the level lands.
            if (!InGameLevel()) return;
            double now = Time.realtimeSinceStartupAsDouble;
            _cardPriceRetryKeys.Clear();
            foreach (var kv in _myCardPrices)
                if (!kv.Value.Acked && now - kv.Value.LastSend >= 3.0) _cardPriceRetryKeys.Add(kv.Key);
            for (int i = 0; i < _cardPriceRetryKeys.Count; i++)
            {
                string key = _cardPriceRetryKeys[i];
                if (!_myCardPrices.TryGetValue(key, out var e)) continue;
                if (e.Attempts >= CardPriceMaxAttempts)
                {
                    // SURRENDER by forgetting the card, not by faking an ack. An untracked card
                    // adopts the host's next heal cleanly through the normal path; a fake
                    // "acked" entry only kept bookkeeping (and a stale Value) alive forever.
                    // Safe to remove here: we iterate the _cardPriceRetryKeys scratch list, not
                    // the dictionary.
                    _myCardPrices.Remove(key);
                    CoopPlugin.Log.LogWarning($"card price for {key} never confirmed - keeping the local value until the host's next price sync");
                    continue;
                }
                var card = e.Card;
                float value = e.Value;
                Broadcast(new CardPriceSetMessage { Card = card, Price = value });
                e.LastSend = now;
                e.Attempts++;
                _myCardPrices[key] = e;
            }
        }

        private void Shutdown(string reason)
        {
            if (_localPlayerModel != null) Util.PlayerModelStore.Save(_localPlayerModel);
            _localModelSavePending = false;
            PlayerModelGeneration++;
            IsTearingDown = true;
            // Harmony callbacks can arrive while transport and world teardown are in progress.
            // Drop all static module entry points first so they cannot touch the old instance
            // state (or a newly loaded world's objects).
            ClearLiveModuleHooks();
            if (_net != null)
            {
                try { Broadcast(new ByeMessage { Reason = "session ended" }); } catch { }
                _net.Stop();
                _net = null;
            }
            _avatars.Clear();
            PeerNames.Clear();
            _peerWireNames.Clear();
            _peerSteamIds.Clear();
            _rosterNames.Clear();
            _enumSyncSentTo.Clear(); // FIX C: the loop-breaker memory is per hosting session
            _enumSyncSentToPeer.Clear(); // ...and its digest-free companion ceiling
            // The canonical id space was the HOST's, and it died with the session. Clearing is
            // not housekeeping, it is correctness: the next session may be one where WE host,
            // and a host must translate nothing.
            Util.EnumMap.Clear();
            // Same reasoning for the per-expansion shown-monster membership sets: they describe
            // THIS install's content as it stood during the dead session.
            ClearCardSetCache();
            // The graded pairing hints and the graded diff die with the session too - they
            // describe the DEAD session's peer, and the adopt offers must not survive it.
            ClearGradedSkipMemory();
            _gradedSent = false;
            _lastGradedHash = -1;
            _gradedPeerOnly.Clear();
            _gradedAlertEverShown.Clear();
            _gradedAlertStanding.Clear();
            GradedAdoptOffers.Clear();
            // The PriceList swap-buffer pair, for the same reason and in the same breath: both
            // hold LOCAL item ids from the DEAD session's translation. Left behind, _clientPriced
            // is read by the next session's clear pass ("the host cleared everything not in this
            // list") and would zero real prices on a later host whose ids mean something else.
            _clientPriced.Clear();
            _incomingPriced.Clear();
            // 1.0.35 per-frame/per-session card state: retry stamps, an undelivered outbox and
            // a half-drained dispatch buffer must never leak into the NEXT session
            _cardDeltaOutbox.Clear();
            _batchRelayBuf.Clear();
            _flushingCardDeltas = false;
            _binderRefreshPending = false;
            _deltaLogBuf.Clear();
            _deltaAppliedThisFrame = 0;
            // RECEIVED but not-yet-applied card work from the dead session. Leaving these
            // queued replays the old world's AddCards/prices into whatever save loads next,
            // because FlushPendingCardWork drains them the moment ANY level is in-game again.
            _pendingCardDeltas.Clear();
            _pendingCardPrices.Clear();
            _myCardPrices.Clear();
            _cardPriceRetryKeys.Clear();
            _cardPriceRetryTimer = 0f;
            _myItemPriceEdits.Clear();
            _priceWarnedKeys.Clear(); // the once-per-session warn memo is per session
            _dispatchBuf.Clear();   // leftovers held back by the per-frame dispatch budget
            _dispatchRetryNextFrame.Clear();
            _dispatchSeen.Clear();
            _gotStateFrom.Clear();
            _saveBuf = null;
            _saveExpected = -1;
            _pendingSave = null;
            _bundleBuf = null;
            _bundleExpected = -1;
            _worldRequested = false;
            _hasLastPos = false;
            _lastCoinSent = double.MinValue;
            _lastPriceHash = 0;
            // card-price heal change-gate: a re-host inheriting the PREVIOUS world's hash
            // would gate away the new world's very first price sync (the guests would sit on
            // whatever they had until something moved), so it resets with the session.
            _cardPriceBuf.Clear();
            _lastCardPriceHash = 0;
            _cardPriceHealBeat = 0f;
            _cardPriceHealTimer = -2.1f;
            _lastProgressSent = long.MinValue;
            _world.Reset();
            _npcs.Reset();
            _cardShelves.Reset();
            _objMoves.Reset();
            _boxes.Reset();
            _population.Reset();
            ModulesReset();
            PromptLine = "";
            _lastShopNameSent = null;
            // UNCONDITIONAL PATH: Shutdown runs from OnDestroy and OnApplicationQuit, i.e.
            // on EVERY game exit and every LAN session too. The null-conditional is what
            // keeps this whole method JIT-clean on a Steamworks-less build.
            //
            // ORDERING: THIS MUST STAY ABOVE `Role = CoopRole.None` BELOW. The bridge's
            // lobby-entered path wires the transport and raises OnConnectedToHost BEFORE
            // CoopCore's role guard on that handler gets to run, so the only thing that
            // actually closes the stray-enter window is Leave() clearing the lobby's
            // _joining flag and dropping _tx. Clear Role first and there is a gap in which
            // a late lobby-enter callback can still land on a live bridge with the role
            // already reset - a connection nobody owns.
            _steam?.Leave();
            IsSteamSession = false;
            HostPassword = "";
            _joinPassword = "";
            _selfId = -1;
            _relayIds.Clear();
            _playerModels.Clear();
            _localPlayerModel = null;
            _localPlayerModelReady = false;
            _localModelAppliedRoot = null;
            _lastCommittedPlayerModel = null;
            _playerModelUndo.Clear();
            _playerModelRedo.Clear();
            _pendingKicks.Clear();
            // The hole we asked the router to open closes with the session. FIRE AND FORGET on
            // a worker, because Shutdown runs from OnDestroy and OnApplicationQuit - blocking
            // the main thread on a SOAP round trip there would hang the game on exit.
            //
            // THIS CALL IS THE CLEANUP, not a nicety on top of one. We ask for a 24h lease and
            // no longer retry as a PERMANENT mapping when the router refuses to lease (1.0.38 -
            // see NetHelpers.TryMapPort), so a mapping that outlives this delete should expire
            // on its own - but "should" is the router's opinion: nothing in UPnP stops it
            // clamping or ignoring the duration we asked for. Treat a failed delete as a hole
            // that stays open until someone clears it in the router's admin page, which is why
            // it now logs at Warning. HasMapping is false for every client and every Steam
            // session, so this costs those nothing.
            if (Net.NetHelpers.HasMapping)
                new Thread(Net.NetHelpers.RemoveMapping) { IsBackground = true, Name = "CoopUnmap" }.Start();
            Interlocked.Increment(ref _inviteGen); // a resolve still in flight belongs to a dead session
            InviteStatus = InviteState.Off;
            InviteCodeText = null;
            InviteReason = null;
            PortForwardState = 0;
            Application.runInBackground = false; // back to the game's normal behavior
            Role = CoopRole.None;
            IsTearingDown = false;
            // Only clear the save guard if we're NOT in a level - i.e. a join that failed at
            // the title before loading the host's world. A mid-session disconnect leaves the
            // guest standing in the borrowed world, so the guard MUST persist (a day-end
            // autosave or quit-save would otherwise write the host's shop to the guest's slot).
            // The title screen clears it on the clean way out.
            if (!InGameLevel()) GuestBorrowedWorld = false;
            if (reason != null)
            {
                StatusLine = "Not connected (" + reason + ")";
                CoopPlugin.Log.LogInfo("Session ended: " + reason);
            }
        }

        // ------------------------------------------------ send helpers

        // ORDERING GUARANTEE for the deferred card-delta outbox: card deltas are queued now,
        // not sent, so any OTHER message leaving before the flush would overtake them and
        // break today's global ordering (a graded-card flow sends a delta and then a follow-up
        // message about the same card). Every send funnels through these three, so flushing
        // here first is enough. The flush is UNCONDITIONAL - a CardDeltaBatch exemption here
        // and in RelayRawToOthers let a RELAYED batch jump ahead of our own queued deltas -
        // and the recursion is stopped by the _flushingCardDeltas re-entrancy guard inside
        // FlushCardDeltaOutbox, which turns the nested call into a no-op.

        private void Send(int connId, INetMessage message)
        {
            FlushCardDeltaOutbox();
            _net?.Send(connId, message);
        }

        /// <summary>Returns an Action&lt;INetMessage&gt; bound to one connId, for wiring
        /// subsystem callbacks that send to a fixed peer (the host, connId 1).</summary>
        private Action<INetMessage> Send(int connId)
        {
            return message => Send(connId, message);
        }

        private void Broadcast(INetMessage message)
        {
            FlushCardDeltaOutbox();
            _net?.Broadcast(message);
        }

        /// <summary>Fast lane: transient state that must never wait behind bulk transfers
        /// (unreliable-no-delay on Steam; a lost packet is replaced by the next tick).</summary>
        private void BroadcastTransient(INetMessage message)
        {
            // The flush only ORDERS the LAN transport, where the transient lane maps onto the
            // SAME ordered TCP stream. On SteamTransport the transient lane is drained BEFORE
            // the reliable lane every pump (SteamNet.PumpMainThread), so transient messages
            // intentionally overtake card deltas there and no flush can prevent it.
            // THEREFORE: no card-coupled message may EVER use the transient lane - only
            // self-replacing state (positions, crowd, register) belongs here.
            FlushCardDeltaOutbox();
            _net?.BroadcastTransient(message);
        }

        /// <summary>Client: ask the host to perform one complete purchase. The client
        /// deliberately does not charge, spawn, unlock, or grant XP locally.</summary>
        public void RequestPurchase(byte kind, List<PurchaseLine> lines)
        {
            if (Role != CoopRole.Client || _net == null || lines == null || lines.Count == 0) return;
            Send(1, new PurchaseRequestMessage { Kind = kind, Lines = lines });
        }

        /// <summary>True when a NATIVE game TMP input field is ACTIVELY BEING EDITED.
        /// UI.CoopUI.TextFieldFocused only tracks the mod's own IMGUI "coop_" controls, so it
        /// can't see the game's TextMeshPro fields - e.g. the haggle-price box on the native
        /// trade/sell-in screen. Without this, tapping the serve key while typing a price would
        /// also fire a register serve.
        /// CRITICAL: must check isFocused (editing NOW), not mere EventSystem SELECTION -
        /// Unity leaves currentSelectedGameObject pointing at the last-clicked UI element
        /// FOREVER after its screen closes, so a selection-only check permanently ate the
        /// serve key once the guest had touched any game text field (price screen, phone
        /// app, grading site) - field report: "guest can't interact with npc".
        /// Null-safe: no EventSystem / nothing selected / not editing -> false.</summary>
        internal static bool NativeTextInputFocused()
        {
            var sel = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
            if (sel == null) return false;
            var tmp = sel.GetComponent<TMPro.TMP_InputField>();
            return tmp != null && tmp.isFocused;
        }

        // ------------------------------------------------ per-frame

        private void Update()
        {
            int actionsRun = 0;
            while (actionsRun++ < MainThreadActionBudget && _mainThread.TryDequeue(out var act))
            {
                act();
            }

            // Release the guest save-guard only once we're safely back at the title: no
            // session AND out of any game level. Post-disconnect the guest is Role.None but
            // still standing in the host's world (InGameLevel true), so the guard persists
            // there and clears only after they actually return to the menu.
            if (GuestBorrowedWorld && Role == CoopRole.None && !InGameLevel())
                GuestBorrowedWorld = false;

            // guest soft-lock safety net: recover from a stranded hold-box mode
            if (Role == CoopRole.Client && InGameLevel()) RecoverStuckHoldBox();

            AutoTick(Time.deltaTime);
            if (_characterPreviewActive && Role != CoopRole.None && InGameLevel())
            {
                EnsureLocalPlayerModel();
                _avatars.UpdatePreview(_localPlayerModel, _playerTf, _playerCamTf, true);
            }
            else _avatars.DestroyPreview();
            FlushLocalModelSave(Time.deltaTime);

            // A day transition can arrive while the guest is changing scenes or has a
            // report screen open. Retry once the actual game level is ready instead of
            // permanently leaving the guest's sky behind.
            TryStartClientDayReset();

            if (Input.GetKeyDown(CoopPlugin.UiToggleKey.Value))
                _ui.Visible = !_ui.Visible;
            SyncWindowUIMode();
            if (Role != CoopRole.None && Input.GetKeyDown(CoopPlugin.EmoteKey.Value) && !UI.CoopUI.TextFieldFocused)
                SendEmote();

            if (RegisterLineTimer > 0f)
            {
                RegisterLineTimer -= Time.deltaTime;
                if (RegisterLineTimer <= 0f) RegisterLine = "";
            }
            if (_net == null) return;

            Guarded("net-pump", _actNetPump);

            // The game forces Application.runInBackground=false (changeFramerate coroutine),
            // which freezes the whole simulation when the window loses focus - fatal for
            // co-op (and for two-instance testing). Re-assert while in a session.
            if (!Application.runInBackground)
            {
                Application.runInBackground = true;
                CoopPlugin.Log.LogInfo("Forced runInBackground=true for the co-op session");
            }

            while (_net.Connects.TryDequeue(out int joined))
            {
                CoopPlugin.Log.LogInfo("Connection " + joined + " opened");
                // fresh joiner: defeat every module's unchanged-hash gate so full
                // authoritative state goes out on the next tick, not the next heal
                if (Role == CoopRole.Host)
                {
                    ModulesForceResend();
                    _lastCoinSent = double.MinValue;   // guarantee the wallet + progress
                    _lastProgressSent = long.MinValue; // snapshot the very next tick
                }
            }
            while (_net.Disconnects.TryDequeue(out int left))
            {
                string name = PeerNames.TryGetValue(left, out var n) ? n : ("player " + left);
                PeerNames.Remove(left);
                _peerWireNames.Remove(left);
                _peerSteamIds.Remove(left);
                _avatars.Remove(left);
                if (Role == CoopRole.Host) _playerModels.Remove(left);
                if (Role == CoopRole.Host)
                {
                    // release anything the departed guest was CARRYING: the set-down
                    // request is never coming, and without this the boxes stay hidden /
                    // worker-locked / carried-frozen on every peer until a full shutdown
                    // (verified stranding: item boxes hidden + m_PreventWorkerTakeBox
                    // pinned forever). Releasing all client-carried boxes is correct for
                    // 2-player and self-heals with 3+ (a survivor still carrying one
                    // re-asserts its carry on its next ~0.5s report).
                    try { _boxes.HostReleaseRemoteCarried(); } catch { }
                    try { _cardBoxes.HostReleaseRemoteCarried(); } catch { }
                    try { _furnBoxes.HostReleaseRemoteCarried(); } catch { }
                    try { _register.HostReleaseConn(left); } catch { }
                    try { _staff.HostReleaseConn(left); } catch { }
                    // and DROP any product still held for the departed guest: its charge
                    // is never coming, and the fail-open pump would otherwise deliver the
                    // product chargeless 1.5s from now. Its charge verdict goes too.
                    BroadcastRoster();
                    StatusLine = _net.ConnectionCount == 0
                        ? "Hosting - waiting for a player..."
                        : $"Hosting - {_net.ConnectionCount} player(s)";
                    CoopPlugin.Log.LogInfo(name + " left");
                }
                else if (Role == CoopRole.Client)
                {
                    ErrorLine = "Lost connection to the host. You can keep walking around; nothing here touches your own saves.";
                    Shutdown("host connection lost");
                    return;
                }
            }

            // Once the borrowed-world load starts, leave gameplay messages queued and let
            // vanilla finish constructing the scene without the mod walking its partial
            // shelf/box lists.  BundleDone can set ClientReloading during the dispatch pass
            // below; the second check after Dispatch handles that same-frame transition.
            if (ClientReloading && !TryFinishClientReload()) return;

            // Drain with coalescing: after any hitch the backlog holds dozens of stale
            // full-state packets; applying each in one frame turns one slow frame into
            // a cascade. For snapshot types only the NEWEST per (type, sender) matters.
            // (NpcState is chunked - every chunk carries different NPCs - and RelayState
            // multiplexes senders inside the payload, so neither may be coalesced.)
            _pendingReduceThisFrame = 0.0; // reset the per-frame guest-spend accumulator
            if (_dispatchRetryNextFrame.Count > 0)
            {
                _dispatchBuf.AddRange(_dispatchRetryNextFrame);
                _dispatchRetryNextFrame.Clear();
            }
            // Anything last frame's budget held back is still at the FRONT of _dispatchBuf, in
            // order; the fresh drain appends after it. The coalescer then re-runs over the
            // combined buffer, so a stale leftover snapshot still loses to a newer one.
            while (_net != null && _net.Incoming.TryDequeue(out var msg))
                _dispatchBuf.Add(msg);
            if (_dispatchBuf.Count > 8)
            {
                _dispatchSeen.Clear();
                for (int i = _dispatchBuf.Count - 1; i >= 0; i--)
                {
                    var t = _dispatchBuf[i].Type;
                    if (t != MsgType.PlayerState && t != MsgType.RegisterState
                        && t != MsgType.RegisterCart && t != MsgType.BoxState && t != MsgType.PopState) continue;
                    long key = ((long)t << 32) | (uint)_dispatchBuf[i].ConnId;
                    if (!_dispatchSeen.Add(key)) _dispatchBuf[i] = default; // superseded
                }
            }
            // BUDGET, in WORK UNITS not messages: coalescing can't help the types that carry
            // real work (a CardDeltaBatch flood, box/container ops), and applying an unbounded
            // backlog in one frame is the hitch we're trying to kill. Every message costs 1
            // unit EXCEPT a CardDeltaBatch, which is charged per delta it carries - counting a
            // 200-delta batch as one message meant the budget bounded nothing. Spend at most
            // DispatchBudget units; the rest keeps its place in line for the next frame. The
            // first message of a frame always goes through even if it alone busts the budget,
            // so an oversized batch can never wedge the queue.
            int consumed = 0;
            int dispatched = 0;
            int unitsSpent = 0;
            for (int i = 0; i < _dispatchBuf.Count; i++)
            {
                if (_dispatchBuf[i].Type == 0) { consumed = i + 1; continue; } // coalesced away
                int cost = DispatchCost(_dispatchBuf[i]);
                if (unitsSpent + cost > DispatchBudget && dispatched > 0) break; // next frame's work
                dispatched++;
                unitsSpent += cost;
                consumed = i + 1;
                InMsg current = _dispatchBuf[i];
                try { Dispatch(current); }
                catch (Exception e)
                {
                    bool retryable = IsRetryableDispatch(current.Type);
                    CoopPlugin.Log.LogError($"Dispatch conn={current.ConnId} type={current.Type} "
                        + (retryable ? "delta/op" : "snapshot") + " failed: " + e);
                    if (retryable && current.DispatchAttempts < MaxDispatchRetries)
                    {
                        current.DispatchAttempts++;
                        _dispatchRetryNextFrame.Add(current);
                    }
                    else
                    {
                        CoopPlugin.Log.LogError($"Dispatch conn={current.ConnId} type={current.Type} "
                            + "dropped after bounded retries; requesting authoritative heal");
                        RequestDispatchHeal(current.Type);
                    }
                }
                if (_net == null) break; // a Bye may have shut us down mid-drain
                if (ClientReloading) break; // BundleDone started the borrowed-world load
            }
            // (a Bye already cleared the buffer in Shutdown, hence the >= Count branch)
            if (consumed >= _dispatchBuf.Count) _dispatchBuf.Clear();
            else if (consumed > 0) _dispatchBuf.RemoveRange(0, consumed);
            if (_net == null) return;

            float dt = Time.deltaTime;
            if (_errLogCooldown > 0f) _errLogCooldown -= dt;

            // Every stage is individually armored: one failing subsystem must degrade
            // that feature only, never kill position sync for the whole session.
            if (!ClientReloading) FlushPendingCardWork();

            _dt = dt;
            if (ClientReloading)
            {
                // Do not let the co-op tick walk partially-created shelves, boxes or
                // machines while vanilla is rebuilding the borrowed world.  The incoming
                // network queue is intentionally left untouched below; it will be applied
                // after the game's own load-complete flag is raised.
                if (!TryFinishClientReload()) return;
            }
            Guarded("avatars", _actAvatars);
            _syncActive = Role != CoopRole.None && _net.ConnectionCount > 0 && InGameLevel()
                && !ClientPreloadHold;
            Guarded("population", _actPopulation);
            Guarded("world", _actWorld);
            Guarded("cardshelves", _actCardShelves);
            Guarded("objmoves", _actObjMoves);
            Guarded("boxes", _actBoxes);
            Guarded("modules", _actModules);

            if (Role == CoopRole.Client)
            {
                Guarded("npc-puppets", _actNpcPuppets);

                // chase any card price the host hasn't confirmed yet (the retry has its own
                // 3s per-entry cooldown; this is just the polling cadence)
                _cardPriceRetryTimer += dt;
                if (_cardPriceRetryTimer >= 1f)
                {
                    _cardPriceRetryTimer = 0f;
                    Guarded("card-price-retry", _actCardPriceRetry);
                }

                // The save-load path can leave inert vanilla customers standing around on
                // the client even though their AI is suppressed; sweep them off so only
                // the host's mirrored puppets are visible.
                _npcSweepTimer += dt;
                if (_npcSweepTimer >= 2f && InGameLevel())
                {
                    _npcSweepTimer -= 2f;
                    Guarded("npc-sweep", _actNpcSweep);
                }
            }

            // position updates
            _stateTimer += dt;
            Guarded("state-send", _actStateSend);

            _diagTimer += dt;
            if (_diagTimer >= 15f)
            {
                _diagTimer -= 15f;
                var diagTf = InGameLevel() ? ResolvePlayer() : null;
                string posStr = diagTf != null ? $"({diagTf.position.x:F1},{diagTf.position.y:F1},{diagTf.position.z:F1})" : "n/a";
                string npcStr = "";
                if (InGameLevel())
                {
                    try
                    {
                        int local = NpcSync.CountLocalActiveNpcs();
                        npcStr = Role == CoopRole.Client
                            ? $" puppets={_npcs.PuppetCount} localNpcs={local}(should be 0)"
                            : $" liveNpcs={local}";
                    }
                    catch { }
                }
                CoopPlugin.Log.LogInfo($"diag: role={Role} conns={_net.ConnectionCount} sentStates={_diagSent} recvStates={_diagRecvStates} inGame={InGameLevel()} pos={posStr}{npcStr}");
            }

            // deferred kicks (give a rejection Bye time to reach the peer first)
            for (int i = _pendingKicks.Count - 1; i >= 0; i--)
            {
                float left = _pendingKicks[i].Value - dt;
                if (left <= 0f)
                {
                    int cid = _pendingKicks[i].Key;
                    _pendingKicks.RemoveAt(i);
                    _net.Kick(cid);
                }
                else _pendingKicks[i] = new KeyValuePair<int, float>(_pendingKicks[i].Key, left);
            }

            // heartbeat + timeout
            _pingTimer += dt;
            if (_pingTimer >= 2f)
            {
                _pingTimer = 0f;
                Broadcast(new PingMessage());
                foreach (int id in _net.ConnIds())
                {
                    if (_net.SecondsSinceLastRecv(id) > _net.TimeoutSeconds)
                    {
                        CoopPlugin.Log.LogWarning("Connection " + id + " timed out");
                        _net.Kick(id);
                    }
                }
            }

            if (Role == CoopRole.Host) HostTick(dt);

            // LAST: one binder relayout + the folded delta log for everything applied this
            // frame, then the batched card-delta outbox. Everything above has had its chance
            // to apply or produce a delta by now.
            Guarded("frame-card-work", _actFrameCardWork);
        }

        /// <summary>Drives the -coopautohost / -coopautojoin command-line flows.</summary>
        private void AutoTick(float dt)
        {
            if (_autoHostSlot < 0 && _autoJoinIp == null) return;
            if (_autoPhase >= 99) return;
            _autoTimer += dt;

            if (_autoHostSlot >= 0)
            {
                if (_autoPhase == 0 && _autoTimer > 6f && !InGameLevel()
                    && CSingleton<CGameManager>.Instance != null)
                {
                    CoopPlugin.Log.LogInfo($"AUTO: loading slot {_autoHostSlot}...");
                    Sync.SaveTransfer.ForceLoadSlot(_autoHostSlot);
                    _autoPhase = 1;
                    _autoTimer = 0f;
                }
                else if (_autoPhase == 1 && InGameLevel() && GameInstance.m_FinishedSavefileLoading)
                {
                    _autoPhase = 2;
                    _autoTimer = 0f;
                }
                else if (_autoPhase == 2 && _autoTimer > 3f)
                {
                    CoopPlugin.Log.LogInfo("AUTO: hosting now");
                    StartHosting();
                    _autoPhase = 99;
                }
            }
            else if (_autoJoinIp != null)
            {
                if (_autoPhase == 0 && _autoTimer > 10f && !InGameLevel()
                    && CSingleton<CGameManager>.Instance != null)
                {
                    CoopPlugin.Log.LogInfo($"AUTO: joining {_autoJoinIp}...");
                    Join(_autoJoinIp);
                    _autoPhase = 99;
                }
            }
            // AutoTick runs unconditionally every frame from Update, so ANY Steamworks token
            // in this method body would fault at JIT on a Steamworks-less build - which is
            // why the CSteamID construction that used to live below had to go.
            else if (_autoJoinSteamLobby != 0 && _steam != null)
            {
                if (_autoPhase == 0 && _autoTimer > 10f && !InGameLevel()
                    && CSingleton<CGameManager>.Instance != null)
                {
                    CoopPlugin.Log.LogInfo($"AUTO: joining Steam lobby {_autoJoinSteamLobby}...");
                    JoinSteam(_autoJoinSteamLobby);
                    _autoPhase = 99;
                }
            }
        }

        /// <summary>Local snapshot diffs: host broadcasts authoritative state, client requests.</summary>
        private void OnLocalWorldChanges(System.Collections.Generic.List<WorldSync.Entry> changes)
        {
            if (Role == CoopRole.Host)
                Broadcast(new ShelfDeltaMessage { Entries = changes });
            else if (Role == CoopRole.Client)
                Send(1, new ShelfRequestMessage { Entries = changes });
        }

        private void HostTick(float dt)
        {
            if (_net.ConnectionCount == 0) return;

            if (InGameLevel())
            {
                Guarded("npc-collect", _actNpcCollect);
            }

            _priceTimer += dt;
            if (_priceTimer >= 3f)
            {
                _priceTimer -= 3f;
                try
                {
                    // the table is indexed by RAW itemType and EPL registers modded items
                    // at huge enum values (~200k entries) - ship only the set prices as
                    // (index, price) pairs; the old whole-table send truncated its count
                    // to 16 bits and modded prices never arrived.
                    // Read through the game's WOVEN GetItemPrice, never the raw list:
                    // EPL routes modded set-prices (index >= vanilla count) to its own
                    // per-item save data, so the raw list simply never contains them -
                    // hosts priced modded packs and joiners' tags stayed at "-" forever
                    // (field screenshots; same interception as the restock catalog)
                    _priceBuf.Clear();
                    var seenTypes = _priceSeenTypes;
                    seenTypes.Clear();
                    int n = CatalogCount();
                    int hash = 17;
                    for (int i = 0; i < n; i++)
                    {
                        var rd = CatalogAt(i);
                        if (rd == null) continue;
                        int t = (int)rd.itemType;
                        if (!seenTypes.Add(t)) continue; // big/small share one price row
                        float v = 0f;
                        try { v = CPlayerData.GetItemPrice(rd.itemType, preventZero: false); } catch { }
                        if (v == 0f) continue;
                        _priceBuf.Add(new KeyValuePair<int, float>(t, v));
                        hash = hash * 31 + t;
                        hash = hash * 31 + v.GetHashCode();
                    }
                    // heal beat: the hash updates BEFORE the send, so a single failed
                    // or lost broadcast used to leave those prices stale FOREVER (tag
                    // stuck at "-" on the joiner until the next unrelated price change).
                    // Every other snapshot engine already has a slow heal; now this does
                    _priceHeal += 3f;
                    if (hash != _lastPriceHash || _priceHeal >= 30f)
                    {
                        _lastPriceHash = hash;
                        _priceHeal = 0f;
                        var priceList = new PriceListMessage();
                        for (int i = 0; i < _priceBuf.Count; i++)
                            priceList.Prices.Add(new PriceEntry { ItemType = _priceBuf[i].Key, Price = _priceBuf[i].Value });
                        Broadcast(priceList);
                    }
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("price sync: " + e.Message); }
            }

            _shopNameTimer += dt;
            if (_shopNameTimer >= 3f)
            {
                _shopNameTimer -= 3f;
                string name = CPlayerData.GetPlayerName();
                if (name != _lastShopNameSent)
                {
                    _lastShopNameSent = name;
                    Broadcast(new ShopNameMessage { Name = name });
                }
            }

            _econTimer += dt;
            if (_econTimer >= 0.5f)
            {
                _econTimer -= 0.5f;
                double coin = CPlayerData.m_CoinAmountDouble;
                // heal beat like PriceList/LightState: change-gated alone stranded the
                // guest's wallet forever on a single dropped/failed CoinSet. Re-send every
                // 15s regardless so a missed economy packet self-corrects.
                _coinHeal += 0.5f;
                if (Math.Abs(coin - _lastCoinSent) > 0.0001 || _coinHeal >= 15f)
                {
                    _lastCoinSent = coin;
                    _coinHeal = 0f;
                    float coinF = CPlayerData.m_CoinAmount;
                    Broadcast(new CoinSetMessage { Coin = coin, CoinFloat = coinF });
                }

                int exp = CPlayerData.m_ShopExpPoint;
                int level = CPlayerData.m_ShopLevel;
                int fame = CPlayerData.m_FamePoint;
                long progress = ((long)level << 40) ^ ((long)fame << 20) ^ (uint)exp;
                _progressHeal += 0.5f;
                if (progress != _lastProgressSent || _progressHeal >= 15f)
                {
                    _lastProgressSent = progress;
                    _progressHeal = 0f;
                    Broadcast(new ProgressSetMessage { Experience = exp, Level = level, Fame = fame });
                }
            }

            // lighting-state heal: normal light changes are event-driven; this slow fallback
            // repairs scene-loads, unusual mods, or a missed change notification
            _lightSyncTimer += dt;
            if (_lightSyncTimer >= 30f || _lightForceResend)
            {
                bool forced = _lightForceResend;
                _lightSyncTimer = forced ? 0f : _lightSyncTimer - 30f;
                _lightForceResend = false;
                try
                {
                    if (_lightManager == null) _lightManager = FindObjectOfType<LightManager>();
                    if (_lightManager != null && MiUpdateLightData != null && CPlayerData.m_LightTimeData != null)
                    {
                        MiUpdateLightData.Invoke(_lightManager, null); // refresh bundle from live state
                        string lightJson = JsonUtility.ToJson(CPlayerData.m_LightTimeData);
                        // the client CORRECTS ITS DRIFT only when a packet arrives - a pure
                        // changed-only gate silenced the corrector whenever the host's sky
                        // was static (pre-open mornings) and the joiner drifted to sunset
                        _lightHeal += 30f;
                        if (lightJson != _lastLightJson || _lightHeal >= 60f)
                        {
                            _lastLightJson = lightJson;
                            _lightHeal = 0f;
                            Broadcast(new LightStateMessage
                            {
                                LightJson = lightJson,
                                Day = CPlayerData.m_CurrentDay,
                                HasDay = true,
                            });
                        }
                    }
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("light sync: " + e.Message); }
            }

            // slow full-truth repaint of card display slots: heals any client whose local
            // display diverged (population repairs, culled reads, missed deltas) without
            // waiting for the host to touch a slot again
            _cardResyncTimer += dt;
            if (_cardResyncTimer >= 12f && InGameLevel())
            {
                _cardResyncTimer -= 12f;
                try
                {
                    var full = _cardShelves.BuildFullState();
                    if (full.Count > 0)
                    {
                        // change-gate the full repaint like every other heal (PriceList,
                        // Population, Box): a big card wall was emitting a multi-KB reliable
                        // packet every 12s even when nothing moved. Hash full card identity;
                        // resend only on change, plus a 30s forced heal for a dropped delta.
                        int h = 17;
                        foreach (var e in full)
                        {
                            h = h * 31 + e.Key;
                            h = h * 31 + (e.Occupied ? 1 : 0);
                            var c = e.Card;
                            if (e.Occupied && c != null)
                            {
                                h = h * 31 + (int)c.monsterType;
                                h = h * 31 + (int)c.expansionType;
                                h = h * 31 + (int)c.borderType;
                                h = h * 31 + c.cardGrade;
                                h = h * 31 + c.gradedCardIndex;
                                h = h * 31 + (c.isFoil ? 1 : 0);
                                h = h * 31 + (c.isDestiny ? 1 : 0);
                                h = h * 31 + (c.isChampionCard ? 1 : 0);
                            }
                        }
                        _cardResyncHeal += 12f;
                        if (h != _lastCardResyncHash || _cardResyncHeal >= 30f)
                        {
                            _lastCardResyncHash = h;
                            _cardResyncHeal = 0f;
                            Broadcast(new CardShelfDeltaMessage { Entries = full });
                        }
                    }
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("card resync: " + e.Message); }

                // ITEM-STOCK full-truth heal on the same 12s beat: item stock previously had
                // NO periodic resync (only host local diffs), so a client that silently
                // adopted a slightly-wrong baseline at join (WorldSync's first-sighting
                // adoption - the mid-day-join wipe fix) stayed wrong until the host touched
                // that compartment. A full-state broadcast IS just a ShelfDelta carrying
                // every compartment (the client handler routes it through ApplyRemote), so
                // no new wire type. Change-gated on the payload bytes + a 36s forced heal.
                try
                {
                    var full = _world.BuildFullState();
                    if (full != null && full.Count > 0)
                    {
                        var fullMessage = new ShelfDeltaMessage { Entries = full };
                        // Hash the actual state fields. Serializing the complete shelf payload
                        // merely to decide whether it changed created a large allocation every
                        // 12 seconds in otherwise idle shops.
                        int h = 17;
                        for (int i = 0; i < full.Count; i++)
                        {
                            var e = full[i];
                            h = h * 31 + e.Key;
                            h = h * 31 + e.Type;
                            h = h * 31 + e.Count;
                        }
                        _stockResyncHeal += 12f;
                        if (h != _lastStockResyncHash || _stockResyncHeal >= 36f)
                        {
                            _lastStockResyncHash = h;
                            _stockResyncHeal = 0f;
                            Broadcast(fullMessage);
                        }
                    }
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("stock resync: " + e.Message); }
            }

            // card PRICE heal: card prices sync only via a single MsgType.CardPriceSet
            // broadcast with no re-send, so one dropped frame stranded a displayed card's
            // price forever (guest set a price on a display, host never saw it). Item prices
            // already self-heal (PriceList); give card prices the same change-gated beat.
            _cardPriceHealTimer += dt;
            if (_cardPriceHealTimer >= 3f && InGameLevel())
            {
                _cardPriceHealTimer -= 3f;
                try
                {
                    var full = _cardShelves.BuildFullState();
                    int h = 17;
                    _cardPriceBuf.Clear();
                    foreach (var e in full)
                    {
                        if (!e.Occupied || e.Card == null) continue;
                        // an encoded (>10) grade only prices via Grading Overhaul's own store
                        // (its GetCardPrice patch reads it); without GO it would IndexOutOfRange
                        if (e.Card.cardGrade > 10 && !Util.GradingInterop.Present) continue;
                        float p; try { p = CPlayerData.GetCardPrice(e.Card); } catch { continue; }
                        if (p <= 0f) continue;
                        _cardPriceBuf.Add(new KeyValuePair<CardData, float>(e.Card, p));
                        h = h * 31 + e.Key;
                        h = h * 31 + p.GetHashCode();
                    }
                    _cardPriceHealBeat += 3f;
                    if ((h != _lastCardPriceHash || _cardPriceHealBeat >= 30f) && _cardPriceBuf.Count > 0)
                    {
                        _lastCardPriceHash = h;
                        _cardPriceHealBeat = 0f;
                        // one message per card (CardPriceSet is a single-card frame); small
                        // and change-gated, so this only fires when a displayed price moved
                        for (int i = 0; i < _cardPriceBuf.Count; i++)
                        {
                            var kv = _cardPriceBuf[i];
                            Broadcast(new CardPriceSetMessage { Card = kv.Key, Price = kv.Value });
                        }
                    }
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("card price heal: " + e.Message); }
            }

            // shared product licenses, identity-keyed: the save-file bool list is indexed
            // by restock position, which modded lists can scramble between machines
            _licenseSyncTimer += dt;
            if (_licenseSyncTimer >= 10f && InGameLevel())
            {
                _licenseSyncTimer -= 10f;
                try
                {
                    // full virtual catalog + the game's INTERCEPTED flag accessor:
                    // raw list/flag reads are vanilla-length only, so modded license
                    // unlocks were invisible to this heal (the Hololive/Fossil
                    // "no local product" reports)
                    int total = CatalogCount();
                    var unlocked = new List<RestockData>();
                    for (int i = 0; i < total; i++)
                    {
                        bool on = false;
                        try { on = CPlayerData.GetIsItemLicenseUnlocked(i); } catch { }
                        if (!on) continue;
                        var rd = CatalogAt(i);
                        if (rd != null) unlocked.Add(rd);
                    }
                    bool scanner = CPlayerData.m_IsScannerRestockUnlocked;
                    // licenses change a few times per session: broadcast on change, plus
                    // a slow heal so a client that missed one still converges
                    int lh = 17;
                    foreach (var rd in unlocked)
                        lh = lh * 31 + (((int)rd.itemType << 1) | (rd.isBigBox ? 1 : 0));
                    lh = lh * 31 + (scanner ? 1 : 0);
                    _licenseHeal += 10f;
                    if (lh != _lastLicenseHash || _licenseHeal >= 60f)
                    {
                        _lastLicenseHash = lh;
                        _licenseHeal = 0f;
                        var licenseState = new LicenseStateMessage { Scanner = scanner };
                        foreach (var rd in unlocked)
                        {
                            licenseState.Entries.Add(new LicenseEntry
                            {
                                ItemType = rd.itemType,
                                Big = rd.isBigBox,
                                // NAME identity: modded enum ints can drift between
                                // machines; the heal must still map the unlock
                                NameFnv = Fnv(rd.name ?? ""),
                            });
                        }
                        Broadcast(licenseState);
                    }
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("license sync: " + e.Message); }
            }

            _dayTimer += dt;
            if (_dayTimer >= 2f)
            {
                _dayTimer -= 2f;
                int hour = 8, min = 0;
                try
                {
                    if (_lightManager == null) _lightManager = FindObjectOfType<LightManager>();
                    if (_lightManager != null)
                    {
                        if (FiTimeHour != null) hour = (int)FiTimeHour.GetValue(_lightManager);
                        if (FiTimeMin != null) min = (int)FiTimeMin.GetValue(_lightManager);
                    }
                }
                catch { }
                int day = CPlayerData.m_CurrentDay;
                float minFloat = min;
                try { if (_lightManager != null && FiTimeMinFloat != null) minFloat = (float)FiTimeMinFloat.GetValue(_lightManager); }
                catch { }
                bool shopOnceOpen = CPlayerData.m_IsShopOnceOpen;
                Broadcast(new DayTimeMessage { Day = day, Hour = hour, Minute = min, MinuteFloat = minFloat, ShopOnceOpen = shopOnceOpen });
            }
        }

        /// <summary>Causes the next host tick to immediately echo the authoritative lighting
        /// state. Used after a forwarded switch request so clients do not wait for the normal
        /// five-second lighting heartbeat.</summary>
        private void TryStartClientDayReset()
        {
            if (Role != CoopRole.Client || !_clientDayResetPending || _clientDayResetInFlight)
                return;
            if (!InGameLevel() || MiDayReset == null)
                return;
            if (_lightManager == null) _lightManager = FindObjectOfType<LightManager>();
            if (_lightManager == null)
                return;

            // Close client-only UI/state before the environment coroutine starts. These
            // are intentionally best-effort; the reset itself must still be attempted.
            try { Sync.ReportSync.CloseClientReport(); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("day change: closing stale report: " + e.Message); }
            try { Sync.RegisterSync.ForceExitManned(); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("day change: force-exit register: " + e.Message); }

            _clientDayResetPending = false;
            _clientDayResetInFlight = true;
            Patches.GamePatches.AllowNextDayStarted = true;
            _lightManager.StartCoroutine(ClientDayResetRoutine());
        }

        private IEnumerator ClientDayResetRoutine()
        {
            try
            {
                yield return (IEnumerator)MiDayReset.Invoke(_lightManager, null);
            }
            finally
            {
                _clientDayResetInFlight = false;
                Patches.GamePatches.AllowNextDayStarted = false;
            }
        }

        public void ForceLightResend()
        {
            if (Role == CoopRole.Host)
            {
                _lightForceResend = true;
                _lightSyncTimer = 0f;
            }
        }

        /// <summary>Called by LightManager hooks after vanilla or a mod has refreshed its
        /// lighting data. Direct SetActive changes have no setter to hook, so compare the
        /// actual groups and request an immediate authoritative echo only when they change.</summary>
        public void ObserveHostLightState(LightManager manager)
        {
            if (Role != CoopRole.Host || manager == null) return;
            bool shop = manager.m_ShoplightGrp != null && manager.m_ShoplightGrp.activeSelf;
            bool night = manager.m_NightlightGrp != null && manager.m_NightlightGrp.activeSelf;
            bool sunlight = manager.m_SunlightGrp != null && manager.m_SunlightGrp.activeSelf;
            if (!_observedLightState || shop != _observedShopLight || night != _observedNightLight || sunlight != _observedSunlight)
            {
                _observedShopLight = shop;
                _observedNightLight = night;
                _observedSunlight = sunlight;
                _observedLightState = true;
                ForceLightResend();
            }
        }

        private static void ApplyClientLightSwitchModels(bool isOn)
        {
            try
            {
                var switches = FindObjectsOfType<InteractableLightSwitch>(true);
                for (int i = 0; i < switches.Length; i++)
                {
                    var sw = switches[i];
                    if (sw == null) continue;
                    if (sw.m_SwitchOnModel != null) sw.m_SwitchOnModel.SetActive(isOn);
                    if (sw.m_SwitchOffModel != null) sw.m_SwitchOffModel.SetActive(!isOn);
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("light-switch visual apply: " + e.Message); }
        }

        // ------------------------------------------------ message handling

        private static bool IsRetryableDispatch(MsgType type)
        {
            switch (type)
            {
                case MsgType.ShelfRequest:
                case MsgType.CardShelfRequest:
                case MsgType.ObjMoveRequest:
                case MsgType.BoxRequest:
                case MsgType.OrderRequest:
                case MsgType.FurnitureOrder:
                case MsgType.BoxRemoved:
                case MsgType.ItemPriceContrib:
                case MsgType.LicenseUnlock:
                case MsgType.StaffOp:
                case MsgType.ShopOp:
                case MsgType.SettingsOp:
                case MsgType.ContainerOp:
                case MsgType.GradingOp:
                case MsgType.TradeOp:
                case MsgType.CardBoxOp:
                case MsgType.FurnBoxOp:
                case MsgType.RegisterOp:
                case MsgType.TvOp:
                case MsgType.EconContrib:
                case MsgType.PurchaseRequest:
                case MsgType.SprayHit:
                case MsgType.GradedRemove:
                case MsgType.CardDelta:
                case MsgType.CardDeltaBatch:
                    return true;
                default:
                    return false;
            }
        }

        private void RequestDispatchHeal(MsgType type)
        {
            // These calls only set a module's next-send flag; they do not walk game state,
            // so a failed dispatch cannot blow the frame budget a second time.
            switch (type)
            {
                case MsgType.ShelfRequest: _cardShelves.ForceNextTick(); break;
                case MsgType.CardShelfRequest: _cardShelves.ForceNextTick(); break;
                case MsgType.ObjMoveRequest: _objMoves.ForceNextTick(); break;
                case MsgType.BoxRequest: _boxes.ForceBroadcastNextTick(); break;
                case MsgType.RegisterOp: _register.ForceResend(); break;
                case MsgType.StaffOp: _staff.ForceResend(); break;
                case MsgType.ShopOp: _shopState.ForceResend(); break;
                case MsgType.SettingsOp: _settings.ForceResend(); break;
                case MsgType.ContainerOp: _containers.ForceResend(); break;
                case MsgType.GradingOp: _grading.ForceResend(); break;
                case MsgType.TradeOp: _trades.ForceResend(); break;
                case MsgType.CardBoxOp: _cardBoxes.ForceResend(); break;
                case MsgType.FurnBoxOp: _furnBoxes.ForceResend(); break;
                case MsgType.TvOp: _tv.ForceResend(); break;
                default:
                    // Economy, purchase, and one-shot card operations are healed by the
                    // normal state cadence; never replay them after bounded failure.
                    _coinHeal = 999f;
                    _progressHeal = 999f;
                    break;
            }
        }

        private void Dispatch(InMsg msg)
        {
            if (msg.Message != null && _messageRouter.Dispatch(new MessageContext
            {
                ConnectionId = msg.ConnId,
                Role = Role,
                InGame = InGameLevel(),
                Transport = _net
            }, msg.Message)) return;
            switch (msg.Type)
            {
                case MsgType.Hello:
                {
                    if (Role != CoopRole.Host) break;
                    if (msg.Message is HelloMessage hello)
                    {
                        int wireVersion = hello.WireVersion;
                        if (wireVersion != Msg.WireVersion)
                        {
                            RejectConn(msg.ConnId, $"wire protocol mismatch - host uses protocol {Msg.WireVersion}, you use {wireVersion}");
                            break;
                        }
                        // Version is checked FIRST so a peer on a different version (which
                        // may not send the newer handshake fields) is rejected before we
                        // try to read them.
                        string version = hello.Version ?? "";
                        if (version != CoopPlugin.Version)
                        {
                            RejectConn(msg.ConnId, $"version mismatch - host runs CardShopCoop {CoopPlugin.Version}, you have {version}");
                            break;
                        }
                        string name = hello.PlayerName ?? "";
                        string password = hello.Password ?? "";
                        string pluginHash = hello.PluginHash ?? "";
                        string enumHash = hello.EnumHash ?? "";
                        string cardsHash = hello.CardsHash ?? "";
                        // FIX E3: these follow cardsHash (append-only wire). Same-version
                        // peers always send them; older/absent -> empty list -> generic
                        // wording below.
                        var theirPlugins = hello.PluginList;
                        var theirCards = hello.CardsList;
                        // FIX C: ...and the joiner's runtime registry lines follow those. Read
                        // in WIRE ORDER here even though the conflict check that uses them sits
                        // further down with the other parity gates.
                        var theirEnumLines = ReadCappedEnumBlob(hello.EnumBlob, out string theirEnumDigest);
                        // GAME-BUILD FINGERPRINT: last two fields of the Hello (see SendHello
                        // for why appending at the END is what makes this wire-safe).
                        string theirGameVersion = hello.GameVersion ?? "";
                        string theirUnityVersion = hello.UnityVersion ?? "";
                        CoopPlugin.Log.LogInfo($"game build: host is {Application.version} / Unity {Application.unityVersion}; {name} is {theirGameVersion} / Unity {theirUnityVersion}");
                        if (HostPassword.Length > 0 && password != HostPassword)
                        {
                            RejectConn(msg.ConnId, "wrong password");
                            break;
                        }
                        // Cross-play between the Steam and Game Pass releases works ONLY when
                        // both PCs run the same game build - the two storefronts ship updates
                        // at different times, and a version skew shows up as inexplicable
                        // desyncs rather than an obvious failure. Runs AFTER the mod-version
                        // check above (both peers are 1.0.38+, so these fields always exist).
                        if (theirGameVersion != Application.version || theirUnityVersion != Application.unityVersion)
                        {
                            // ESCAPE HATCH: a host who has deliberately set AllowCrossBuildJoin
                            // takes the risk knowingly (supervised Steam <-> Game Pass testing),
                            // so we let the join through and shout about it instead. Rejecting
                            // stays the default; only the HOST's config can open this door.
                            if (!CoopPlugin.AllowCrossBuildJoin.Value)
                            {
                                RejectConn(msg.ConnId,
                                    $"your GAME build doesn't match the host's (host: {Application.version} / Unity {Application.unityVersion}, you: {theirGameVersion} / Unity {theirUnityVersion}) - the Steam and Game Pass versions of the game can only play together when both are on the same game version (a host who understands the risk can enable AllowCrossBuildJoin in the config)");
                                break;
                            }
                            CoopPlugin.Log.LogWarning($"AllowCrossBuildJoin is ON - letting {name} in on a DIFFERENT game build (host: {Application.version} / Unity {Application.unityVersion}, {name}: {theirGameVersion} / Unity {theirUnityVersion}). Two different game builds can corrupt each other's saves - back up before playing.");
                        }
                        if (pluginHash != Util.ModParity.PluginHash())
                        {
                            // FIX E3: name the differing mods when we can; fall back to the
                            // old generic wording if the lists are absent/agree.
                            string detail = DescribeModDiff(theirPlugins, Util.ModParity.PluginList(),
                                "mod set differs - ", "version differs");
                            RejectConn(msg.ConnId, detail
                                ?? "your mod set differs from the host's - both players need identical mods (same versions)");
                            break;
                        }
                        // FIX C: registries no longer have to be IDENTICAL, only
                        // NON-CONFLICTING. Extra entries on either side are fine - neither
                        // player can spawn what the other doesn't know about, and the
                        // catalog-differs warning already says the sets differ. What actually
                        // corrupts a shared world is the SAME "Type:Name" bound to DIFFERENT
                        // ids on the two PCs, so that is the only thing we reject on now.
                        var ourEnumLines = SafeEnumLines();
                        if (ourEnumLines.Count == 0)
                        {
                            // DEAD-GATE VISIBILITY. EnumConflicts short-circuits to "no
                            // conflicts" whenever either side is empty (correct: a machine with
                            // no modded ids can't clash with anyone), so an empty list HERE means
                            // the ID-conflict check waved this joiner through without comparing
                            // anything. On a vanilla host that is the intended answer; on a
                            // modded one it means our own registry walk came up empty and the
                            // only thing standing between two conflicting id spaces just went
                            // quiet - while the log line below still prints two plausible hashes.
                            CoopPlugin.Log.LogWarning($"enum check: the host has NO modded enum ids to compare against, so {name} was not ID-checked at all (expected on a vanilla host; on a modded one see the 'enum identity source' line at startup)");
                        }
                        var conflicts = EnumConflicts(theirEnumLines, ourEnumLines);
                        if (conflicts.Count > 0)
                        {
                            CoopPlugin.Log.LogInfo($"enum check: {name} hash {enumHash} vs host {Util.ModParity.EnumHash()} - {conflicts.Count} real conflict(s)");
                            // Can we honestly ship our registry file? The old test here was
                            // "is it a BORROWED copy?" (ModParity.HostEnumInstalled - the
                            // .hostlend marker). That was the wrong question, and an expensive
                            // one: the marker survives restarts, so a host who joined somebody
                            // once, restarted, and has been happily RUNNING that registry ever
                            // since - a perfectly coherent id space, and exactly the file this
                            // guest needs - still hard-rejected every conflicting guest AND
                            // skipped the send that would have fixed them. That PC could never
                            // auto-sync anyone again.
                            //
                            // The real hazard is narrower: the bytes on disk are not what this
                            // process actually loaded, so shipping them promises the guest an id
                            // space nobody is running. That is tracked precisely by our own two
                            // writes - see ModParity.RegistryFileMatchesRuntime.
                            if (!Util.ModParity.RegistryFileMatchesRuntime())
                            {
                                RejectConn(msg.ConnId,
                                    "your custom-card database conflicts with the host's, and the host's card-database FILE was changed this session so it no longer matches what the host is running - the HOST has to RESTART the game before it can be auto-synced to you (conflicting: "
                                    + DescribeConflicts(conflicts) + ")");
                                break;
                            }
                            // BOUNDED RETRY (1.0.36; was a one-shot loop-breaker). The premise
                            // this block was built on turned out to be wrong: EPL does NOT
                            // re-mint the registry from local content every boot, it LOADS the
                            // file and reuses every saved id verbatim (see
                            // ModParity.EnumFilePath). So our copy DOES survive the restart it
                            // demands, and sending it really is the fix - the mismatch is
                            // usually nothing but the same content packs installed in a
                            // different ORDER. What the old one-shot rule actually caught was
                            // guests who never APPLIED the file (no full restart, a failed
                            // write, or a host gated by the borrowed-registry branch above), and
                            // those deserve another copy plus a blunter instruction, not a
                            // permanent refusal. So: up to EnumSyncMaxSends attempts per (peer,
                            // registry), then the honest terminal message - the loop-breaker's
                            // original job, which is to never promise a restart forever.
                            string peerKey = PeerSyncKey(name, theirEnumDigest);
                            int sentBefore;
                            _enumSyncSentTo.TryGetValue(peerKey, out sentBefore);
                            // ...and the digest-free ceiling. A guest whose EPL re-mints ids on
                            // every boot presents a NEW digest each time, so the key above is
                            // fresh each time and its budget never runs out - see
                            // _enumSyncSentToPeer for why both counters are needed.
                            string peerNameKey = PeerSyncKey(name, null);
                            int sentToPeer;
                            _enumSyncSentToPeer.TryGetValue(peerNameKey, out sentToPeer);
                            if (sentBefore < EnumSyncMaxSends && sentToPeer < EnumSyncMaxSendsPerPeer)
                            {
                                // send our registry along with the rejection: the client
                                // backs theirs up, installs ours, and only has to restart -
                                // no more hand-copying enum_values.json between PCs
                                bool sent = false;
                                string failReason = null;
                                try
                                {
                                    string enumPath = Util.ModParity.EnumFilePath();
                                    if (!System.IO.File.Exists(enumPath))
                                    {
                                        // Distinct from an IO failure and worth naming: a host
                                        // whose registry file is missing has nothing to give.
                                        failReason = "the host has no card-database file on disk to send";
                                    }
                                    else
                                    {
                                        var enumBytes = System.IO.File.ReadAllBytes(enumPath);
                                        var gz = Msg.Gzip(enumBytes);
                                        Send(msg.ConnId, new EnumSyncMessage { Data = gz });
                                        // ONLY a peer we actually shipped the file to counts as
                                        // synced. Counting a failed read/send (missing file,
                                        // locked by EPL or antivirus, permissions) promised a
                                        // restart that could not possibly help, and burned one
                                        // of the attempts on something that never happened.
                                        _enumSyncSentTo[peerKey] = sentBefore + 1;
                                        _enumSyncSentToPeer[peerNameKey] = sentToPeer + 1;
                                        sent = true;
                                    }
                                }
                                catch (Exception e)
                                {
                                    failReason = e.Message;
                                    CoopPlugin.Log.LogWarning("enum sync send: " + e.Message);
                                }
                                string why = DescribeConflicts(conflicts);
                                string reject;
                                if (!sent)
                                {
                                    // Name the actual reason when we know it - "match your
                                    // content packs manually" alone told the player nothing
                                    // about which machine had the problem.
                                    reject = "your custom-card database conflicts with the host's, and the host could not send its card database"
                                        + (failReason != null ? " (" + failReason + ")" : "")
                                        + " - ask the host to check that "
                                        + "AppData\\LocalLow\\OPNeonGames\\Card Shop Simulator\\PrefabLoader\\enum_values.json exists and is readable, or copy it across by hand (conflicting: "
                                        + why + ")";
                                }
                                else if (sentBefore == 0)
                                {
                                    // Careful with the claims here: the guest only WRITES the
                                    // file if their AutoSyncCardDatabase option is on, and they
                                    // may have had no registry of their own to back up.
                                    reject = "your custom-card database conflicts with the host's - the host's copy has just been sent to you, and (unless you switched auto-sync off) saved on your PC with your old file backed up first. Now QUIT THE GAME TO DESKTOP, start it again, then join: the ids are only read while the game is booting, so nothing changes until you do (conflicting: "
                                        + why + ")";
                                }
                                else
                                {
                                    // Same digest as last time: their registry is byte-for-byte
                                    // the identity they sent before, so the file we sent was
                                    // never loaded. Say that plainly rather than repeating the
                                    // first message word for word.
                                    reject = "your card database is UNCHANGED since the last sync, so the host's copy never took effect - usually because the game was not fully closed (returning to the title screen is not enough), or because auto-sync is switched off on your side. It has been sent again: QUIT TO DESKTOP, start the game, then join (conflicting: "
                                        + why + ")";
                                }
                                RejectConn(msg.ConnId, reject);
                            }
                            else
                            {
                                // Out of attempts. The terminal message must be TRUE: syncing
                                // does work, so the fault is that it is not being applied - the
                                // old wording ("the game rebuilds it from YOUR content packs, so
                                // copying the host's file cannot fix this") was wrong in both
                                // halves and sent people off to reinstall content packs that
                                // were never the problem.
                                //
                                // WHAT THIS MESSAGE MUST NOT SAY: "check the host isn't on a
                                // borrowed card database". We only get here with a registry that
                                // RegistryFileMatchesRuntime() already vouched for - the file we
                                // sent IS the id space this host is running - so that advice is
                                // inapplicable, and a host who follows it restores a registry
                                // their own shop save was never written under and corrupts it.
                                // The guest-side cause the two-send messages already name is the
                                // one that belongs here: auto-sync switched off means the file
                                // never landed at all.
                                //
                                // Two ways to land here and the wording has to be true for both:
                                // the per-digest budget ran out (their registry never changed), or
                                // the per-peer ceiling did (their registry changes every boot, so
                                // the per-digest budget alone would never have ended). Only the
                                // first may claim the file is unchanged.
                                bool digestHeld = sentBefore >= EnumSyncMaxSends;
                                int sendsMade = digestHeld ? sentBefore : sentToPeer;
                                RejectConn(msg.ConnId,
                                    "your card database still conflicts after " + sendsMade + " sync"
                                    + (sendsMade == 1 ? "" : "s") + " from the host"
                                    + (digestHeld
                                        ? " and has not changed at all"
                                        : " and keeps coming back DIFFERENT each time (your card ids are being re-minted every boot)")
                                    + ", so the host's copy is not being applied on your PC. Syncing normally DOES fix this - the ids usually differ only because the same content packs were installed in a different order. Check, in this order: (1) you fully quit the game to DESKTOP after the sync and started it again (returning to the title screen is not enough); (2) CardShopCoop's AutoSyncCardDatabase option is ON on YOUR side - with it off nothing is ever written to your PC; (3) failing that, copy the host's enum_values.json from AppData\\LocalLow\\OPNeonGames\\Card Shop Simulator\\PrefabLoader into the same folder on your PC by hand and restart (conflicting: "
                                    + DescribeConflicts(conflicts) + ")");
                            }
                            break;
                        }
                        // Custom CreateCards/CardForge cards aren't covered by the enum
                        // registry above, so check their ID mapping directly. These are
                        // loose files we can't auto-sync, so it's a clear hard stop.
                        if (cardsHash != Util.ModParity.CardsHash())
                        {
                            // FIX E3: name the differing custom cards when we can. Entries
                            // are "MonsterType=ID", so a same-name different-ID is an ID
                            // clash (the exact desync). Generic fallback keeps the actionable
                            // "share the CardForge package" guidance.
                            string detail = DescribeModDiff(theirCards, Util.ModParity.CardsList(),
                                "custom cards differ - ", "ID differs");
                            RejectConn(msg.ConnId, detail
                                ?? "your custom cards differ from the host's - both players need the same custom cards installed (identical files + IDs), then restart. Share the exact card package (e.g. from CardForge).");
                            break;
                        }

                        _peerWireNames[msg.ConnId] = name;
                        _peerSteamIds[msg.ConnId] = hello.SteamId;
                        name = ResolvePeerName(hello.SteamId, name);
                        PeerNames[msg.ConnId] = name;
                        _avatars.SetName(msg.ConnId, name);
                        StatusLine = $"Hosting - {name} joined!";
                        CoopPlugin.Log.LogInfo(name + " joined, sending world...");
                        SendWorldTo(msg.ConnId);
                        BroadcastRoster();
                    }
                    break;
                }
                case MsgType.Welcome:
                {
                    if (Role != CoopRole.Client) break;
                    if (msg.Message is WelcomeMessage welcome)
                    {
                        int wireVersion = welcome.WireVersion;
                        if (wireVersion != Msg.WireVersion)
                        {
                            Shutdown("wire protocol mismatch");
                            break;
                        }
                        string hostName = ResolvePeerName(welcome.SteamId, welcome.HostName ?? "");
                        _saveExpected = welcome.SaveLength;
                        _hostSlot = welcome.HostSlot;
                        _bundleExpected = welcome.BundleLength;
                        _selfId = welcome.SelfId;
                        // ID TRANSLATION IS BUILT HERE. "Welcome is the first message of the
                        // session, so every later message is already translated" was the comment
                        // that used to sit here and it is FALSE - do not rely on it.
                        //
                        // THE REAL WINDOW: SendWorldTo snapshots the host's registry on the main
                        // thread but builds and sends the Welcome from a WORKER thread, while the
                        // main thread carries on broadcasting. Roster, PriceList and LicenseState
                        // can therefore reach a client BEFORE its Welcome, and on Steam a
                        // PlayerState riding the transient lane can overtake the reliable one and
                        // do the same. Those messages are dispatched with EnumMap inactive, i.e.
                        // UNTRANSLATED.
                        //
                        // WHY THAT IS ACCEPTABLE TODAY: a session only gets this far when the
                        // Hello conflict gate found no "same name, different id" between the two
                        // registries, so on every ALLOWED session the ids in that pre-Welcome
                        // traffic mean the same thing on both PCs - the translation would be the
                        // identity function anyway. This is exactly 1.0.35 behavior, unchanged.
                        //
                        // Both blobs fail to an EMPTY list (missing, truncated, not gzip, over the
                        // cap), and an empty list leaves that half of the map unbuilt, which is
                        // the identity function - today's behavior, never garbage.
                        var hostEnumLines = ReadCappedEnumBlob(welcome.HostEnumBlob, out _);
                        var hostCardLines = ReadCappedEnumBlob(welcome.HostCardsBlob, out _);
                        if (hostEnumLines.Count == 0 && hostCardLines.Count == 0)
                            CoopPlugin.Log.LogWarning("no registry from the host - modded ids will NOT be translated this session. Causes, in order of likelihood: the host is vanilla (fine, nothing to translate); or the host's registry was too big for the wire cap or could not be read/unpacked (see the host's log for 'registry blob over cap' / 'over-cap' - in that case ids must already match on both PCs)");
                        Util.EnumMap.Build(hostEnumLines, hostCardLines);
                        // The PriceList swap buffers must start EMPTY under this session's brand-
                        // new id map. Shutdown clears them too, but a PriceList can legitimately
                        // land BEFORE this Welcome (see the window described above), and anything
                        // it recorded was recorded UNTRANSLATED - i.e. in the host's ids, read as
                        // ours. Residue like that survives into the first translated PriceList,
                        // where the clear pass would zero real local prices for ids that never
                        // meant what they looked like. Dropping it costs nothing: the very next
                        // PriceList is a full sparse snapshot.
                        _clientPriced.Clear();
                        _incomingPriced.Clear();
                        PeerNames[msg.ConnId] = hostName;
                        _avatars.SetName(msg.ConnId, hostName);
                        // Publish the local appearance immediately. This gives the host and
                        // other clients a deterministic model even when the selector is never
                        // opened, while the UI can later submit richer CC slider data.
                        EnsureLocalPlayerModel();
                        SubmitLocalPlayerModel();
                        _saveBuf = new MemoryStream(_saveExpected > 0 ? _saveExpected : 1024);
                        _bundleBuf = new MemoryStream(_bundleExpected > 0 ? _bundleExpected : 16);
                        StatusLine = $"Downloading {hostName}'s shop ({(_saveExpected + _bundleExpected) / 1024} KB)...";
                    }
                    break;
                }
                case MsgType.SaveChunk:
                {
                    if (Role != CoopRole.Client || _saveBuf == null) break;
                    if (msg.Message is SaveChunkMessage saveChunk)
                    {
                        var bytes = saveChunk.Data ?? new byte[0];
                        _saveBuf.Write(bytes, 0, bytes.Length);
                        if (_saveExpected > 0)
                            StatusLine = $"downloading shop... {Math.Min(100, _saveBuf.Length * 100 / _saveExpected)}%";
                    }
                    break;
                }
                case MsgType.SaveDone:
                {
                    if (Role != CoopRole.Client || _saveBuf == null || _worldRequested) break;
                    var data = _saveBuf.ToArray();
                    _saveBuf = null;
                    if (_saveExpected >= 0 && data.Length != _saveExpected)
                    {
                        ErrorLine = $"World download looked corrupted ({data.Length}/{_saveExpected} bytes) - try again.";
                        Shutdown("bad download");
                        break;
                    }
                    try { data = Msg.Gunzip(data); }
                    catch
                    {
                        ErrorLine = "World download could not be unpacked - try again.";
                        Shutdown("bad download");
                        break;
                    }
                    if (data.Length < 1024 || data[0] != (byte)'{')
                    {
                        ErrorLine = "World download looked corrupted - try again.";
                        Shutdown("bad download");
                        break;
                    }
                    _pendingSave = data;
                    StatusLine = "shop received - downloading mod data...";
                    break;
                }
                case MsgType.BundleChunk:
                {
                    if (Role != CoopRole.Client || _bundleBuf == null) break;
                    if (msg.Message is BundleChunkMessage bundleChunk)
                    {
                        var bytes = bundleChunk.Data ?? new byte[0];
                        _bundleBuf.Write(bytes, 0, bytes.Length);
                        if (_bundleExpected > 0)
                            StatusLine = $"downloading mod data... {Math.Min(100, _bundleBuf.Length * 100 / _bundleExpected)}%";
                    }
                    break;
                }
                case MsgType.BundleDone:
                {
                    if (Role != CoopRole.Client || _worldRequested || _pendingSave == null) break;
                    var bundle = _bundleBuf != null ? _bundleBuf.ToArray() : new byte[0];
                    _bundleBuf = null;
                    _worldRequested = true;
                    StatusLine = "World received - loading...";
                    // GRADING OVERHAUL'S CERT STORE IS IN THAT BUNDLE, and its replacement is the
                    // single most consequential thing the sidecar does that nobody can see. GO
                    // keeps burned serials and cert->card bindings in
                    // <persistentDataPath>/Grading - Overhaul/GradingOverhaul_<slot>.json
                    // (decompiled-grading GetSaveFilePath/GetSaveDataDirectory :482-499), which
                    // matches the sidecar's slot pattern, so joining someone REPLACES it with
                    // theirs. GO then re-scans certs on every save load and rewrites any row it
                    // cannot vouch for to its FAKE encoding - which from this mod's side of the
                    // fence looks like graded cards silently vanishing. Stamped before and after
                    // rather than logged from inside ApplyBundle, which stays a dumb file copier -
                    // and stamped by CONTENT, because ApplyBundle rewrites this file on every join
                    // regardless of whether the host's copy differs, so any mtime-based test warns
                    // every single time and means nothing (see FileStamp).
                    string goStore = Path.Combine(Path.Combine(Application.persistentDataPath, "Grading - Overhaul"),
                        "GradingOverhaul_" + SaveTransfer.CoopSlot + ".json");
                    string goBefore = FileStamp(goStore);
                    try { if (bundle.Length > 0) bundle = Msg.Gunzip(bundle); }
                    catch (Exception e)
                    {
                        ErrorLine = "Mod data could not be unpacked - try again.";
                        CoopPlugin.Log.LogError("coop: sidecar unpack failed: " + e);
                        Shutdown("bad sidecar download");
                        break;
                    }
                    SidecarTransfer.ApplyBundleAsync(bundle, _hostSlot, SaveTransfer.CoopSlot,
                        () =>
                        {
                            if (FileStamp(goStore) != goBefore)
                                CoopPlugin.Log.LogWarning("Grading Overhaul cert store replaced by the host's copy for the borrowed world: "
                                    + goStore + " - your own SOLO save slots are untouched, but graded cards in THIS co-op slot are now judged "
                                    + "against the host's burned serials and cert bindings, and any this PC issued itself can be flagged FAKE on the next load. "
                                    + "The previous file was kept once as .coopbak beside it.");
                            SaveTransfer.ApplyAndLoadAsync(_pendingSave,
                                () => _pendingSave = null,
                                e =>
                                {
                                    ErrorLine = "Could not apply the received world: " + e.Message;
                                    CoopPlugin.Log.LogError("coop: world apply failed: " + e);
                                    Shutdown("world apply failed");
                                });
                            _pendingSave = null;
                        },
                        e =>
                        {
                            ErrorLine = "Could not apply mod data: " + e.Message;
                            CoopPlugin.Log.LogError("coop: sidecar apply failed: " + e);
                            Shutdown("sidecar apply failed");
                        });
                    // the game's world-(re)load teardown (LoadInteractableObjectData ->
                    // RestockManager.DestroyAllObject) destroys every existing box via
                    // OnDestroyed - if a world was live (rejoin, or solo save loaded
                    // while waiting for the invite) a 1.0.7 client forwarded all ~250
                    // as player trash actions, wiping the HOST's boxes (first field
                    // report). Suppress until vanilla reports that the world is settled.
                    ClientReloading = true;
                    _reloadStartedAt = Time.realtimeSinceStartup;
                    _reloadStartedFrame = Time.frameCount;
                    break;
                }
                case MsgType.ShelfDelta:
                {
                    // Dropping deltas while not in the game scene is safe (the world just
                    // loaded from the host's save; the host keeps re-diffing changes) and
                    // avoids touching scene managers that don't exist yet.
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    if (msg.Message is ShelfDeltaMessage shelfDelta)
                        _world.ApplyRemote(shelfDelta.Entries);
                    break;
                }
                case MsgType.ShelfRequest:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    if (msg.Message is ShelfRequestMessage shelfRequest)
                    {
                        var entries = shelfRequest.Entries;
                        _world.ApplyRemote(entries);
                        // applying updates the host's diff baseline, so its own tick
                        // never re-detects this change - with 3+ players the OTHER
                        // clients must be told explicitly
                        if (_net.ConnectionCount > 1)
                            Broadcast(new ShelfDeltaMessage { Entries = entries });
                    }
                    break;
                }
                case MsgType.PriceList:
                {
                    if (Role != CoopRole.Client) break;
                    if (msg.Message is PriceListMessage priceList)
                    {
                        Patches.GamePatches.ApplyingRemotePrice = true; // don't echo these back
                        try
                        {
                            _incomingPriced.Clear();
                            int changed = 0;
                            for (int k = 0; k < priceList.Prices.Count; k++)
                            {
                                var priceEntry = priceList.Prices[k];
                                // Both sets below are keyed by LOCAL item ids (they are compared
                                // against our own catalog and against _myItemPriceEdits), so the
                                // translation has to happen here, before anything is recorded.
                                bool known = Util.EnumMap.TryFromWire(Util.EnumKind.ItemType, priceEntry.ItemType, out int i);
                                float v = priceEntry.Price;
                                // A price for a product only the HOST has: there is nothing local
                                // to price, and - critically - it must NOT be recorded as priced,
                                // because every unmappable id would collapse onto the same None
                                // sentinel and the clear pass below reads this set.
                                if (!known) continue;
                                _incomingPriced.Add(i);
                                if (i < 0 || i > 500000) continue;
                                // this table was built BEFORE our own ItemPriceContrib landed:
                                // for a few seconds our fresh edit outranks it (it still counts
                                // as "priced" above, so the clear pass below leaves it alone)
                                if (HeldLocalItemPrice(i, v)) continue;
                                // write through the game's WOVEN SetItemPrice: raw list
                                // writes for modded types land in a shadow list the game
                                // never reads (EPL routes those rows to its own save
                                // data), which kept joiner tags at "-" while the value
                                // "applied" - and SetItemPrice fires the tag-repaint
                                // event itself
                                float cur = 0f;
                                try { cur = CPlayerData.GetItemPrice((EItemType)i, preventZero: false); } catch { }
                                if (Math.Abs(cur - v) > 0.0001f)
                                {
                                    try { CPlayerData.SetItemPrice((EItemType)i, v); changed++; } catch { }
                                }
                            }
                            // stale-price reports were undiagnosable: applies were silent
                            if (changed > 0)
                                CoopPlugin.Log.LogInfo($"price apply: {changed} price(s) updated from host");
                            // a price the host CLEARED is absent from the sparse set.
                            // BOTH sets hold LOCAL ids now, so this stays an apples-to-apples
                            // comparison. A host-only product can never be zeroed here: it never
                            // resolved, so it was never added above and so cannot be in
                            // _clientPriced either. One-sided content packs keep their prices.
                            foreach (int i in _clientPriced)
                                if (!_incomingPriced.Contains(i) && i >= 0 && i <= 500000)
                                {
                                    // a clear is an overwrite too: the host simply hasn't seen
                                    // our brand-new price yet
                                    if (HeldLocalItemPrice(i, 0f)) continue;
                                    float cur = 0f;
                                    try { cur = CPlayerData.GetItemPrice((EItemType)i, preventZero: false); } catch { }
                                    if (cur != 0f)
                                    {
                                        try { CPlayerData.SetItemPrice((EItemType)i, 0f); } catch { }
                                    }
                                }
                            var tmp = _clientPriced;
                            _clientPriced = _incomingPriced;
                            _incomingPriced = tmp;
                        }
                        finally { Patches.GamePatches.ApplyingRemotePrice = false; }
                    }
                    break;
                }
                case MsgType.PlayerState:
                {
                    var state = msg.Message as PlayerStateMessage;
                    if (state == null) break;
                    {
                        var pos = state.Position;
                        float yaw = state.Yaw;
                        float speed = state.Speed;
                        byte hold = state.Hold;
                        var holdTypes = state.HoldTypes;
                        var holdCards = state.HoldCards;
                        _diagRecvStates++;
                        _avatars.UpdateState(msg.ConnId, pos, yaw, speed, hold, holdTypes, holdCards);
                        if (PeerNames.TryGetValue(msg.ConnId, out var peerName))
                            _avatars.SetName(msg.ConnId, peerName); // re-seed after scene loads clear avatars
                        if (Role == CoopRole.Host && _net.ConnectionCount > 1)
                        {
                            // other clients should see this player too
                            var relay = new RelayStateMessage
                            {
                                SenderId = (byte)msg.ConnId,
                                State = new PlayerStateMessage
                                {
                                    Position = pos, Yaw = yaw, Speed = speed, Hold = hold,
                                    HoldTypes = hold == 3 ? null : holdTypes,
                                    HoldCards = hold == 3 ? holdCards : null
                                }
                            };
                            foreach (int cid in _net.ConnIds())
                                if (cid != msg.ConnId) _net.SendTransient(cid, relay);
                        }
                        if (_gotStateFrom.Add(msg.ConnId))
                        {
                            string who = PeerNames.TryGetValue(msg.ConnId, out var n) ? n : ("player " + msg.ConnId);
                            CoopPlugin.Log.LogInfo($"Position link active with {who}");
                            if (Role == CoopRole.Host) StatusLine = $"Hosting - {who} is in your shop!";
                        }
                    }
                    break;
                }
                case MsgType.CoinSet:
                {
                    if (Role != CoopRole.Client) break;
                    var coinMessage = msg.Message as CoinSetMessage;
                    if (coinMessage == null) break;
                    {
                        double coin = coinMessage.Coin;
                        float coinF = coinMessage.CoinFloat;
                        if (!_loggedEconLink)
                        {
                            _loggedEconLink = true;
                            CoopPlugin.Log.LogInfo("Economy link active (host wallet mirrored)");
                        }
                        if (Math.Abs(CPlayerData.m_CoinAmountDouble - coin) > 0.0001)
                            CEventManager.QueueEvent(new CEventPlayer_SetCoin(coinF, coin));
                    }
                    break;
                }
                case MsgType.DayTime:
                {
                    if (Role != CoopRole.Client) break;
                    var timeMessage = msg.Message as DayTimeMessage;
                    if (timeMessage == null) break;
                    {
                        int day = timeMessage.Day;
                        int hour = timeMessage.Hour;
                        int min = timeMessage.Minute;
                        float minFloat = timeMessage.MinuteFloat;
                        bool shopOnceOpen = timeMessage.ShopOnceOpen;
                        if (!_loggedTimeLink)
                        {
                            _loggedTimeLink = true;
                            CoopPlugin.Log.LogInfo($"Time link active (Day {day} {hour:00}:{min:00})");
                        }
                        HostTimeLine = $"Day {day + 1}  {hour:00}:{min:00}"; // HUD shows day+1
                        bool dayChanged = day != CPlayerData.m_CurrentDay;
                        CPlayerData.m_CurrentDay = day;
                        // Match the host's clock gate. Forcing this true made a client advance
                        // through a new morning while the host was still waiting to open shop.
                        CPlayerData.m_IsShopOnceOpen = shopOnceOpen;
                        if (dayChanged)
                            _clientDayResetPending = true;
                        try
                        {
                            if (_lightManager == null) _lightManager = FindObjectOfType<LightManager>();
                            if (_lightManager != null)
                            {
                        if (dayChanged || _clientDayResetPending || _clientDayResetInFlight)
                                {
                                    TryStartClientDayReset();
                                }
                                else
                                {
                                    FiTimeHour?.SetValue(_lightManager, hour);
                                    FiTimeMin?.SetValue(_lightManager, min);
                                    FiTimeMinFloat?.SetValue(_lightManager, minFloat);
                                    MiEvaluateTimeClock?.Invoke(_lightManager, null);
                                    // Keep the guest's day-end latch identical to the host. When
                                    // the authoritative clock is before closing, clear a stale
                                    // local latch so the clock can resume normally.
                                    if (hour < 21)
                                        FiHasDayEnded?.SetValue(_lightManager, false);
                                }
                            }
                        }
                        catch { }
                    }
                    break;
                }
                case MsgType.ProgressSet:
                {
                    if (Role != CoopRole.Client) break;
                    var progress = msg.Message as ProgressSetMessage;
                    if (progress == null) break;
                    {
                        int exp = progress.Experience;
                        int level = progress.Level;
                        int fame = progress.Fame;
                        int prevLevel = CPlayerData.m_ShopLevel;
                        // Order matters: set the level FIRST, because the SetShopExp handler
                        // levels up while exp >= required-for-current-level. With the host's
                        // consistent (level, exp) pair, exp < required and no spurious level-up.
                        CPlayerData.m_ShopLevel = level;
                        CEventManager.QueueEvent(new CEventPlayer_SetShopExp(exp));
                        CEventManager.QueueEvent(new CEventPlayer_SetFame(fame));
                        if (level > prevLevel)
                            CEventManager.QueueEvent(new CEventPlayer_ShopLeveledUp(level));
                    }
                    break;
                }
                case MsgType.Emote:
                {
                    _avatars.ShowEmote(msg.ConnId);
                    RelayTagToOthers(msg.ConnId, 0);
                    break;
                }
                case MsgType.Activity:
                {
                    var activity = msg.Message as ActivityMessage;
                    if (activity == null) break;
                    int packIdx = (int)activity.Pack;
                    _avatars.ShowTag(msg.ConnId, "opening a pack!", 3f);
                    _avatars.ShowPackOpen(msg.ConnId, packIdx);
                    RelayTagToOthers(msg.ConnId, 1, packIdx);
                    break;
                }
                case MsgType.Roster:
                {
                    if (Role != CoopRole.Client) break;
                    var roster = msg.Message as RosterMessage;
                    if (roster == null) break;
                    {
                        var seen = new HashSet<int>();
                        foreach (var entry in roster.Entries)
                        {
                            int id = entry.Id;
                            string name = ResolvePeerName(entry.SteamId, entry.Name ?? "");
                            if (id == _selfId) continue;
                            seen.Add(id);
                            _rosterNames[id] = name; // re-applied on every relay packet
                            if (_relayIds.Add(id)) CoopPlugin.Log.LogInfo($"peer in shop: {name}");
                            _avatars.SetName(1000 + id, name);
                        }
                        _relayIds.RemoveWhere(id =>
                        {
                            if (seen.Contains(id)) return false;
                            _avatars.Remove(1000 + id);
                            return true;
                        });
                    }
                    break;
                }
                case MsgType.RelayState:
                {
                    if (Role != CoopRole.Client) break;
                    var relayState = msg.Message as RelayStateMessage;
                    if (relayState == null) break;
                    {
                        int senderId = relayState.SenderId;
                        var state = relayState.State;
                        var pos = state.Position;
                        float yaw = state.Yaw;
                        float speed = state.Speed;
                        byte hold = state.Hold;
                        var holdTypes = state.HoldTypes;
                        var holdCards = state.HoldCards;
                        if (senderId != _selfId)
                        {
                            _avatars.UpdateState(1000 + senderId, pos, yaw, speed, hold, holdTypes, holdCards);
                            // the avatar may have spawned AFTER the roster named it - a
                            // relayed peer then wore the default "Player" tag forever
                            if (_rosterNames.TryGetValue(senderId, out var rn))
                                _avatars.SetName(1000 + senderId, rn);
                        }
                    }
                    break;
                }
                case MsgType.RelayTag:
                {
                    if (Role != CoopRole.Client) break;
                    var relayTag = msg.Message as RelayTagMessage;
                    if (relayTag == null) break;
                    {
                        int senderId = relayTag.SenderId;
                        byte kind = relayTag.Kind;
                        int extra = (int)relayTag.Extra;
                        if (senderId == _selfId) break;
                        if (kind == 0) _avatars.ShowEmote(1000 + senderId);
                        else
                        {
                            _avatars.ShowTag(1000 + senderId, "opening a pack!", 3f);
                            _avatars.ShowPackOpen(1000 + senderId, extra);
                        }
                    }
                    break;
                }
                // Nothing has sent a single CardDelta since the outbox landed (SendCardDeltaTo
                // still does, and older internal senders may), so this case stays exactly as
                // it was; CardDeltaBatch below runs the very same per-delta logic in a loop.
                case MsgType.CardDelta:
                {
                    if (msg.Message is CardDeltaMessage cardDelta)
                    {
                        if (!ApplyOrHoldCardDelta(cardDelta.IsAdd, cardDelta.Amount, cardDelta.Card, out bool relayAnyway) && !relayAnyway)
                            break; // held for the level load, or genuinely refused: don't propagate
                        // relayAnyway == this PC lacks the content pack but the delta is sound;
                        // a third player who HAS it still needs it, so fall through to the relay.
                        // Shared collection: a card a guest gained/lost has to reach the OTHER
                        // guests too, or their binder totals drift out of sync in 3+ player
                        // sessions. Forward to everyone except the sender.
                        RelayToOthers(msg.ConnId, msg.Message);
                    }
                    break;
                }
                case MsgType.CardDeltaBatch:
                {
                    // the host only needs the per-delta rebuild when it actually has other
                    // guests to relay to; snapshot BEFORE applying, because the game's AddCard
                    // path may mutate the CardData we hand it
                    bool needFiltered = Role == CoopRole.Host && _net != null && _net.ConnectionCount > 1;
                    _batchRelayBuf.Clear();
                    int total = 0, applied = 0, relayedOnly = 0;
                    if (msg.Message is CardDeltaBatchMessage batchMessage)
                    {
                        total = batchMessage.Deltas.Count;
                        if (total < 0 || total > CardDeltaBatchMax)
                        {
                            CoopPlugin.Log.LogWarning($"card delta batch: bogus count {total} - dropped");
                            break;
                        }
                        for (int i = 0; i < total; i++)
                        {
                            var delta = batchMessage.Deltas[i];
                            bool isAdd = delta.IsAdd; int amount = delta.Amount; CardData card = delta.Card;
                            var relayCopy = needFiltered ? SnapshotCard(card) : null;
                            bool ok, relayAnyway = false;
                            try { ok = ApplyOrHoldCardDelta(isAdd, amount, card, out relayAnyway); }
                            catch (Exception e)
                            {
                                CoopPlugin.Log.LogWarning($"card delta batch: delta {i + 1}/{total} failed to apply ({e.Message}) - skipped");
                                continue; // one bad delta costs one delta, not the batch
                            }
                            // A delta this PC can't hold because it lacks the content pack still
                            // belongs in the relay set (its snapshot was taken BEFORE the apply,
                            // same as the applied ones) - a third player may have that pack.
                            if (!ok && !relayAnyway) continue;
                            if (ok) applied++; else relayedOnly++;
                            if (needFiltered)
                                _batchRelayBuf.Add(new PendingCard { IsAdd = isAdd, Amount = amount, Card = relayCopy });
                        }
                    }
                    // Same shared-collection fan-out as CardDelta, and it runs on EVERY exit
                    // path above (clean, read-fault, apply-fault). The ORIGINAL bytes go out
                    // untouched only when every delta applied here and none was merely forwarded
                    // (encoded grades verbatim); otherwise the filtered rebuild carries the
                    // accepted deltas PLUS the ones this PC lacks the content for - a delta we
                    // genuinely refused (corrupt grade, would-go-negative, throw) must never
                    // spread, exactly as in the single-delta case above.
                    if (applied == total && relayedOnly == 0 && total > 0) RelayToOthers(msg.ConnId, msg.Message);
                    else if (_batchRelayBuf.Count > 0) RelayCardDeltaBatchToOthers(msg.ConnId, _batchRelayBuf);
                    break;
                }
                case MsgType.GradedRemove:
                {
                    if (msg.Message is GradedRemoveMessage gradedRemove)
                    {
                        var card = gradedRemove.Card;
                        if (card == null) break;
                        if (!InGameLevel())
                        {
                            // hold until the world is up, same as CardDelta; the graded
                            // branch of ApplyCardDelta routes it through RemoveGradedCard
                            _pendingCardDeltas.Add(new PendingCard { IsAdd = false, Amount = 1, Card = card });
                            break;
                        }
                        if (!ApplyCardDelta(isAdd: false, amount: 1, card: card, relayAnyway: out bool relayAnyway) && !relayAnyway)
                            break; // genuinely refused here: never propagate it
                        // ...but "this PC has no data row for that card" is not a refusal of the
                        // message, only of the local apply - a peer that HAS the pack still owns
                        // that graded copy and must see the removal. Same rule as CardDelta.
                    }
                    // same shared-collection fan-out as CardDelta: relay the graded-remove to the
                    // other guests.
                    RelayToOthers(msg.ConnId, msg.Message);
                    break;
                }
                case MsgType.NpcState:
                {
                    if (Role != CoopRole.Client) break;
                    if (msg.Message is NpcStateMessage npcState)
                        _npcs.ApplyBatch(npcState, InGameLevel());
                    break;
                }
                case MsgType.NpcSpeech:
                {
                    if (Role != CoopRole.Client) break;
                    if (msg.Message is NpcSpeechMessage npcSpeech)
                        _npcs.ShowSpeech(npcSpeech, InGameLevel());
                    break;
                }
                case MsgType.CardShelfDelta:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    if (msg.Message is CardShelfDeltaMessage cardShelfDelta)
                        _cardShelves.ApplyRemote(cardShelfDelta.Entries);
                    break;
                }
                case MsgType.CardShelfRequest:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    if (msg.Message is CardShelfRequestMessage cardShelfRequest)
                    {
                        var entries = cardShelfRequest.Entries;
                        _cardShelves.ApplyRemote(entries);
                        if (_net.ConnectionCount > 1) // see ShelfRequest note
                            Broadcast(new CardShelfDeltaMessage { Entries = entries });
                    }
                    break;
                }
                case MsgType.BoxState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    if (msg.Message is BoxStateMessage boxState)
                        _boxes.ClientApply(boxState.Entries);
                    break;
                }
                case MsgType.PopState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    if (msg.Message is PopStateMessage popState)
                        _population.ClientApply(popState.Entries);
                    break;
                }
                case MsgType.FurnitureOrder:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    if (msg.Message is FurnitureOrderMessage furnitureOrder)
                    {
                        // The whole handler keys off this id (price lookup, prefab lookup, spawn),
                        // so a permuted modded id would deliver - and charge for - the wrong piece
                        // of furniture. Unmappable becomes EObjectType.None, whose prefab lookup
                        // returns null, which drops into the existing refund-and-toast path.
                        var eObj = furnitureOrder.ObjType;
                        var pos = furnitureOrder.Position;
                        var rot = furnitureOrder.Rotation;
                        string who = PeerNames.TryGetValue(msg.ConnId, out var n) ? n : "player";

                        // Charge/product coupling (charge-first): honor this cart's charge
                        // verdict if it already landed (accept -> process, decline -> drop
                        // with toast); otherwise hold as a rare product-first straggler.
                        // Legacy product messages are accepted only for compatibility; new clients
                        // use PurchaseRequest and are charged atomically by the host.

                        // The guest already paid: its CEventPlayer_ReduceCoin was forwarded
                        // and debited the shared wallet BEFORE this spawn. If the prefab
                        // isn't in the host's catalog (different mods / load order), the
                        // vanilla spawn Instantiate(null)s and throws - swallowed by the
                        // per-message try/catch - so the money vanished with no box, no
                        // delivery, no refund (the field report). Match the restock-order
                        // path: pre-check, refund from the host's authoritative price, notify.
                        float refund = 0f;
                        try { var fp = InventoryBase.GetFurniturePurchaseData(eObj); if (fp != null) refund = fp.price; }
                        catch { } // GetFurniturePurchaseData indexes a parallel list; a catalog mismatch can throw

                        if (InventoryBase.GetSpawnInteractableObjectPrefab(eObj) == null)
                        {
                            CoopPlugin.Log.LogWarning($"{who} bought furniture {eObj} not in host catalog - refunding {refund:F0}");
                            if (refund > 0f && refund < 100000f)
                                CEventManager.QueueEvent(new CEventPlayer_AddCoin(refund));
                            Send(msg.ConnId, new ToastMessage { Text =
                                refund > 0f
                                    ? $"that furniture isn't in the host's catalog - refunded ${refund:F0}"
                                    : "that furniture isn't in the host's catalog - nothing was delivered" });
                            break;
                        }

                        CoopPlugin.Log.LogInfo($"{who} bought furniture: {eObj}");
                        try
                        {
                            ShelfManager.SpawnInteractableObjectInPackageBox(eObj, pos, rot);
                        }
                        catch (Exception e)
                        {
                            CoopPlugin.Log.LogWarning("furniture spawn failed on host: " + e.Message);
                            if (refund > 0f && refund < 100000f)
                                CEventManager.QueueEvent(new CEventPlayer_AddCoin(refund));
                            Send(msg.ConnId, new ToastMessage { Text =
                                refund > 0f
                                    ? $"furniture failed to deliver on the host - refunded ${refund:F0}"
                                    : "furniture failed to deliver on the host" });
                        }
                    }
                    break;
                }
                case MsgType.PurchaseRequest:
                {
                    if (Role == CoopRole.Host && InGameLevel() && msg.Message is PurchaseRequestMessage purchase)
                        ApplyPurchaseRequest(msg.ConnId, purchase);
                    break;
                }
                case MsgType.BoxRequest:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    if (msg.Message is BoxRequestMessage boxRequest)
                        _boxes.HostApplyRequest(boxRequest.Entries);
                    break;
                }
                case MsgType.OrderRequest:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    if (msg.Message is OrderRequestMessage orderRequest)
                    {
                        // ResolveRestockIndex matches on the id BEFORE the name, so an untranslated
                        // modded id from a peer whose EPL numbering is a permutation of ours would
                        // match the WRONG product on the first pass - the same class of bug as the
                        // raw restock index this message replaced. Unmappable lands on None, which
                        // no catalog row carries, so the name pass resolves it or the refund does.
                        int itemType = (int)orderRequest.ItemType;
                        bool isBig = orderRequest.IsBig;
                        string rdName = orderRequest.Name;
                        int count = orderRequest.Count;
                        float cost = orderRequest.LineCost;
                        string who = PeerNames.TryGetValue(msg.ConnId, out var n) ? n : "player";
                        // Charge/product coupling (charge-first): a multi-line cart's N
                        // OrderRequests all read the ONE summed charge's verdict - accept ->
                        // process each, decline -> drop each (one throttled toast). No fresh
                        // verdict -> hold as a rare product-first straggler.
                        // Legacy product messages are accepted only for compatibility.
                        int idx = ResolveRestockIndex(itemType, isBig, rdName, out bool sizeDiffers);
                        if (idx >= 0)
                        {
                            CoopPlugin.Log.LogInfo($"{who} ordered {(EItemType)itemType} big={isBig} x{count} -> restock {idx}{(sizeDiffers ? " (size fallback)" : "")}");
                            RestockManager.SpawnPackageBoxItemMultipleFrame(idx, count);
                            if (sizeDiffers)
                                Send(msg.ConnId, new ToastMessage { Text =
                                    $"'{rdName}' delivered in the host's box size (catalogs differ slightly)" });
                        }
                        else
                        {
                            // the money is already in the shared wallet (the charge fired
                            // before the spawn call we intercept) - give it back loudly
                            int hostCatalog = 0;
                            try { hostCatalog = CatalogCount(); } catch { }
                            CoopPlugin.Log.LogWarning($"{who} ordered unknown product type {itemType} '{rdName}' - refunding {cost:F0} (host catalog: {hostCatalog} products)");
                            LogCatalogCandidates(rdName);
                            if (cost > 0f && cost < 100000f)
                                CEventManager.QueueEvent(new CEventPlayer_AddCoin(cost));
                            // the resolver now spans the full EPL virtual catalog, so a
                            // miss with a big catalog is a genuine pack difference; a
                            // vanilla-sized catalog means the host's EPL entries aren't
                            // visible (bundles still loading right after boot, or the
                            // EPL bridge is inactive - the host's log says which)
                            string reason = hostCatalog > 140
                                ? "the host's content packs don't include this product - match your pack files"
                                : "the host's modded catalog hasn't finished loading (or EPL is missing on the host) - wait a minute and try again, and check the host's BepInEx log";
                            Send(msg.ConnId, new ToastMessage { Text =
                                $"'{rdName}' isn't in the host's catalog - refunded ${cost:F0}. Note: {reason}" });
                        }
                    }
                    break;
                }
                case MsgType.Toast:
                {
                    if (Role != CoopRole.Client) break;
                    if (msg.Message is ToastMessage toast)
                    {
                        RegisterLine = toast.Text ?? "";
                        RegisterLineTimer = 8f;
                        // support gold: the on-screen line vanishes in 8s, the log keeps it
                        CoopPlugin.Log.LogInfo("host says: " + RegisterLine);
                    }
                    break;
                }
                case MsgType.CatalogDigest:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    if (msg.Message is CatalogDigestMessage catalogDigest)
                        CompareCatalogs(catalogDigest, msg.ConnId);
                    break;
                }
                case MsgType.GradedDigest:
                {
                    // BOTH roles: the host compares a guest's digest, and a guest compares the
                    // one the host sends back when it found a difference. On the client the peer
                    // is always the host, so the diff is filed under conn 1 - the same id the
                    // client sends to - rather than whatever the transport labelled the frame.
                    if (Role == CoopRole.None || !InGameLevel()) break;
                    bool amHost = Role == CoopRole.Host;
                    if (msg.Message is GradedDigestMessage gradedDigest)
                        CompareGradedDigests(gradedDigest, amHost ? msg.ConnId : 1, amHost);
                    break;
                }
                case MsgType.BoxRemoved:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    if (msg.Message is BoxRemovedMessage boxRemoved)
                    {
                        int id = boxRemoved.Index;
                        // HostApplyRemoval refuses the removal unless this type matches the
                        // tracked box's own item type, so the id has to be in local terms. An
                        // unmappable one lands on None, fails that guard, and the box is left
                        // standing - the safe direction for a destructive op.
                        int type = (int)boxRemoved.ItemType;
                        string who = PeerNames.TryGetValue(msg.ConnId, out var n) ? n : "player";
                        CoopPlugin.Log.LogInfo($"{who} trashed box id {id} ({(EItemType)type})");
                        _boxes.HostApplyRemoval(id, type, msg.ConnId);
                    }
                    break;
                }
                case MsgType.LicenseUnlock:
                {
                    if (!InGameLevel()) break;
                    if (msg.Message is LicenseUnlockMessage licenseUnlock)
                    {
                        int itemType = (int)licenseUnlock.ItemType;
                        bool isBig = licenseUnlock.IsBig;
                        string rdName = licenseUnlock.RestockName;
                        // Charge/product coupling (host only, charge-first): honor this
                        // cart's charge verdict (accept -> process, decline -> drop with
                        // toast) or hold a rare product-first straggler. Host-gated so the
                        // host->clients echo (Role==Client on the receiver) is untouched.
                        // LicenseUnlock is now a host broadcast; legacy inbound messages remain harmless.
                        bool ok = ApplyLicenseUnlock(itemType, isBig, rdName);
                        if (Role == CoopRole.Host)
                        {
                            if (ok) // echo to the other clients + confirm to the buyer
                            {
                                Broadcast(new LicenseUnlockMessage
                                { ItemType = (EItemType)itemType, IsBig = isBig, RestockName = rdName });
                                Send(msg.ConnId, new ToastMessage { Text = $"license unlocked for everyone: {rdName}" });
                            }
                            else
                                Send(msg.ConnId, new ToastMessage { Text = $"'{rdName}' license couldn't unlock on the host (product missing) - match your content packs" });
                        }
                    }
                    break;
                }
                case MsgType.LicenseState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    if (msg.Message is LicenseStateMessage licenseState)
                    {
                        bool scanner = licenseState.Scanner;
                        var wanted = new HashSet<long>();
                        var wantedNames = new HashSet<long>();
                        for (int i = 0; i < licenseState.Entries.Count; i++)
                        {
                            var e = licenseState.Entries[i];
                            // The name key below is already machine-independent, but the ID key
                            // is NOT a redundant spare: it is OR'd in, and matched against our
                            // OWN rl[i].itemType, so an untranslated modded id from a permuted
                            // registry is a false POSITIVE - it unlocks whichever local product
                            // happens to wear that number. Translate it, and when the host has a
                            // product we don't, add no id key at all rather than letting every
                            // unmappable one collapse onto the None sentinel and match together.
                            bool known = Util.EnumMap.TryFromWire(Util.EnumKind.ItemType, (int)e.ItemType, out int t);
                            bool big = e.Big;
                            int nameFnv = e.NameFnv;
                            if (known) wanted.Add(((long)t << 1) | (big ? 1L : 0L));
                            wantedNames.Add(((long)(uint)nameFnv << 1) | (big ? 1L : 0L));
                        }
                        // don't re-lock during the window where our own purchase is
                        // still round-tripping to the host
                        bool allowLock = UnityEngine.Time.realtimeSinceStartupAsDouble
                            - _lastLicenseBuyTime > 12.0;
                        Guarded("license-apply", () =>
                        {
                            CPlayerData.m_IsScannerRestockUnlocked |= scanner;
                            var rl = Inv().m_StockItemData_SO.m_RestockDataList;
                            var flags = CPlayerData.m_IsItemLicenseUnlocked;
                            bool anyUnlocked = false;
                            for (int i = 0; i < rl.Count && i < flags.Count; i++)
                            {
                                if (rl[i] == null) continue;
                                long big = rl[i].isBigBox ? 1L : 0L;
                                bool should = wanted.Contains(((long)(int)rl[i].itemType << 1) | big)
                                    || wantedNames.Contains(((long)(uint)Fnv(rl[i].name ?? "") << 1) | big);
                                if (should && !flags[i])
                                {
                                    Patches.GamePatches.ApplyingRemoteLicense = true;
                                    try { CPlayerData.SetUnlockItemLicense(i); }
                                    finally { Patches.GamePatches.ApplyingRemoteLicense = false; }
                                    anyUnlocked = true;
                                    try
                                    {
                                        if (rl[i].itemType == EItemType.BasicCardBox)
                                            TutorialManager.AddTaskValue(ETutorialTaskCondition.UnlockBasicCardBox, 1f);
                                    }
                                    catch { }
                                }
                                else if (!should && flags[i] && i != 0 && allowLock)
                                {
                                    // scrambled save-transfer flag (index order differs
                                    // between machines): host truth says locked
                                    flags[i] = false;
                                }
                            }
                            if (anyUnlocked)
                            {
                                try { GameInstance.m_IsItemLicenseUnlocked = true; } catch { }
                                RefreshLicensePanels();
                            }
                        });
                    }
                    break;
                }
                case MsgType.StaffOp:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    if (msg.Message is StaffOpMessage staffOp) _staff.HostApplyOp(staffOp, msg.ConnId);
                    break;
                }
                case MsgType.StaffInteract:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    if (msg.Message is StaffInteractMessage staffInteract) StaffSync.ClientInteractionMessage(staffInteract);
                    break;
                }
                case MsgType.StaffState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    if (msg.Message is StaffStateMessage staffState) _staff.ClientApplyState(staffState);
                    break;
                }
                case MsgType.ShopOp:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    if (msg.Message is ShopOpMessage shopOp) _shopState.HostApplyOp(shopOp);
                    break;
                }
                case MsgType.ShopState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    if (msg.Message is ShopStateMessage shopState) _shopState.ClientApplyState(shopState);
                    break;
                }
                case MsgType.SettingsOp:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    if (msg.Message is SettingsOpMessage settingsOp) _settings.HostApplyOp(settingsOp);
                    break;
                }
                case MsgType.SettingsState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    if (msg.Message is SettingsStateMessage settingsState) _settings.ClientApplyState(settingsState);
                    break;
                }
                case MsgType.TvOp:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    if (msg.Message is TvOpMessage tvOp) _tv.HostApplyOp(tvOp, msg.ConnId);
                    break;
                }
                case MsgType.TvState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    if (msg.Message is TvStateMessage tvState) _tv.ClientApplyState(tvState);
                    break;
                }
                case MsgType.MarketState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    if (msg.Message is MarketStateMessage marketState) _market.ClientApplyState(marketState);
                    break;
                }
                case MsgType.ReportState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    if (msg.Message is ReportStateMessage reportState) _report.ClientApplyState(reportState);
                    break;
                }
                case MsgType.ContainerOp:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    if (msg.Message is ContainerOpMessage containerOp) _containers.HostApplyOp(containerOp, msg.ConnId);
                    break;
                }
                case MsgType.ContainerState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    if (msg.Message is ContainerStateMessage containerState) _containers.ClientApplyState(containerState);
                    break;
                }
                case MsgType.ContainerBoxTake:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    if (msg.Message is ContainerBoxTakeMessage containerTake) _containers.ClientApplyTakeAccepted(containerTake);
                    break;
                }
                case MsgType.ContainerPackClaim:
                {
                    if (Role == CoopRole.Client && msg.Message is ContainerPackClaimMessage claim)
                        _containers.ClientApplyPackClaim(claim);
                    break;
                }
                case MsgType.TournamentState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    if (msg.Message is TournamentStateMessage tournamentState) _tournament.ClientApplyState(tournamentState);
                    break;
                }
                case MsgType.CardBoxOp:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    if (msg.Message is CardBoxOpMessage cardBoxOp) _cardBoxes.HostApplyOp(cardBoxOp, msg.ConnId);
                    break;
                }
                case MsgType.CardBoxState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    if (msg.Message is CardBoxStateMessage cardBoxState) _cardBoxes.ClientApplyState(cardBoxState);
                    break;
                }
                case MsgType.CardBoxCollectResult:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    if (msg.Message is CardBoxCollectResultMessage cardBoxCollect) _cardBoxes.ClientApplyCollectRejected(cardBoxCollect);
                    break;
                }
                case MsgType.FurnBoxOp:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    if (msg.Message is FurnBoxOpMessage furnBoxOp) _furnBoxes.HostApplyOp(furnBoxOp, msg.ConnId);
                    break;
                }
                case MsgType.FurnBoxState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    if (msg.Message is FurnBoxStateMessage furnBoxState) _furnBoxes.ClientApplyState(furnBoxState);
                    break;
                }
                case MsgType.EnumSync:
                {
                    if (Role != CoopRole.Client) break;
                    if (msg.Message is EnumSyncMessage enumSync)
                    {
                        var hostBytes = Msg.Gunzip(enumSync.Data);
                        if (CoopPlugin.AutoSyncCardDatabase.Value)
                        {
                            StatusLine = Util.ModParity.InstallEnumFile(hostBytes);
                            CoopPlugin.Log.LogInfo("enum sync: " + StatusLine);
                        }
                        else
                        {
                            StatusLine = "card databases differ - auto-sync is disabled; copy the host's enum_values.json (PrefabLoader folder) yourself";
                        }
                    }
                    break;
                }
                case MsgType.GradingOp:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    if (msg.Message is GradingOpMessage gradingOp) _grading.HostApplyOp(gradingOp, msg.ConnId);
                    break;
                }
                case MsgType.GradingState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    if (msg.Message is GradingStateMessage gradingState) _grading.ClientApplyState(gradingState);
                    break;
                }
                case MsgType.TradeOp:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    if (msg.Message is TradeOpMessage tradeOp) _trades.HostApplyOp(tradeOp);
                    break;
                }
                case MsgType.TradeState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    if (msg.Message is TradeStateMessage tradeState) _trades.ClientApplyState(tradeState);
                    break;
                }
                case MsgType.TableState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    if (msg.Message is TableStateMessage tableState) _tables.ClientApplyState(tableState);
                    break;
                }
                case MsgType.PlayerIntent:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    if (msg.Message is PlayerIntentMessage intent) _intents.HostApplyOp(intent);
                    break;
                }
                case MsgType.LightState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    if (msg.Message is LightStateMessage lightState)
                    {
                        var data = JsonUtility.FromJson<LightTimeData>(lightState.LightJson);
                        if (data == null) break;
                        try
                        {
                            // Reject an older heartbeat that was already queued before a
                            // rollover. A newer day heartbeat is the fallback for a missed
                            // DayTime packet and must schedule the same full environment reset.
                            if (lightState.HasDay)
                            {
                                if (lightState.Day < CPlayerData.m_CurrentDay) break;
                                if (lightState.Day > CPlayerData.m_CurrentDay)
                                {
                                    CPlayerData.m_CurrentDay = lightState.Day;
                                    _clientDayResetPending = true;
                                    TryStartClientDayReset();
                                    break;
                                }
                            }
                            if (_clientDayResetPending || _clientDayResetInFlight) break;
                            if (_lightManager == null) _lightManager = FindObjectOfType<LightManager>();
                            if (_lightManager == null) break;
                            int localIdx = FiTimeOfDayIdx?.GetValue(_lightManager) is int idx ? idx : -1;
                            int localHour = FiTimeHour?.GetValue(_lightManager) is int h ? h : -1;
                            int localMin = FiTimeMin?.GetValue(_lightManager) is int m2 ? m2 : 0;
                            int driftMin = Math.Abs((data.m_TimeHour * 60 + data.m_TimeMin) - (localHour * 60 + localMin));
                            bool groupsDiffer = _lightManager.m_NightlightGrp == null
                                || _lightManager.m_ShoplightGrp == null
                                || _lightManager.m_SunlightGrp == null
                                || _lightManager.m_NightlightGrp.activeSelf != data.m_IsNightLightOn
                                || _lightManager.m_ShoplightGrp.activeSelf != data.m_IsShopLightOn
                                || _lightManager.m_SunlightGrp.activeSelf != data.m_IsSunlightOn;

                            // Apply every authoritative light group, not just the shop-light
                            // switch. This also repairs mods that change the groups directly.
                            if (groupsDiffer)
                            {
                                _lightManager.m_NightlightGrp?.SetActive(data.m_IsNightLightOn);
                                _lightManager.m_ShoplightGrp?.SetActive(data.m_IsShopLightOn);
                                _lightManager.m_SunlightGrp?.SetActive(data.m_IsSunlightOn);
                                MiEvaluateWorldUIBrightness?.Invoke(_lightManager, null);
                            }
                            ApplyClientLightSwitchModels(data.m_IsShopLightOn);
                            // re-run the game's own lighting restore only when the sky
                            // phase actually differs (avoids music/blend churn)
                            if (localIdx != data.m_TImeOfDayIndex || driftMin > 4)
                            {
                                CPlayerData.m_LightTimeData = data;
                                FiFinishLoading?.SetValue(_lightManager, false);
                                MiLightInit?.Invoke(_lightManager, null);
                                CoopPlugin.Log.LogInfo($"lighting re-synced (phase {localIdx}->{data.m_TImeOfDayIndex}, drift {driftMin}min)");
                            }
                            FiHasDayEnded?.SetValue(_lightManager, false);
                        }
                        catch (Exception e) { CoopPlugin.Log.LogWarning("light apply: " + e.Message); }
                    }
                    break;
                }
                case MsgType.ShopName:
                {
                    if (Role != CoopRole.Client) break;
                    var shopNameMessage = msg.Message as ShopNameMessage;
                    if (shopNameMessage == null) break;
                    {
                        string name = shopNameMessage.Name ?? "";
                        if (name.Length > 0)
                        {
                            if (CPlayerData.GetPlayerName() != name)
                            {
                                CPlayerData.PlayerName = name;
                                CoopPlugin.Log.LogInfo("shop name synced: " + name);
                            }
                            // FIX E2: refresh the 3D sign directly (the one-shot repaint
                            // listener can be missed during the join reload) and stash the
                            // name so it can be re-applied once the reload settles.
                            _lastShopNameApplied = name;
                            if (_shopSign != null) { try { _shopSign.text = name; } catch { } }
                        }
                    }
                    break;
                }
                case MsgType.ItemPriceContrib:
                {
                    if (Role != CoopRole.Host) break;
                    if (msg.Message is ItemPriceContribMessage itemPriceContrib)
                    {
                        // Host side, so this is the identity function. An unmappable id would
                        // arrive as EItemType.None (-1), which the existing range guard below
                        // already refuses.
                        int itemType = (int)itemPriceContrib.ItemType;
                        float price = itemPriceContrib.Price;
                        if (itemType >= 0 && itemType <= 500000)
                        {
                            // Resolvability first, same runtime-enum oracle as the card guards:
                            // a type this host has never heard of can't be priced here at all.
                            if (!Enum.IsDefined(typeof(EItemType), (EItemType)itemType))
                            {
                                CoopPlugin.Log.LogWarning($"item price for unknown item type {itemType} skipped - host missing content pack?");
                                // tell the SENDER too: the host log is invisible to the guest,
                                // who otherwise watches its price silently revert on the next
                                // PriceList with nothing anywhere explaining why
                                Send(msg.ConnId, new ToastMessage { Text = "the host couldn't apply that price - it may be missing that product" });
                                break;
                            }
                            // WOVEN SetItemPrice, never the raw list: EPL routes modded
                            // rows to its own save data, and a raw write is a shadow
                            // entry the game (and our own woven-read broadcast) never
                            // sees. Fires the tag-repaint event itself.
                            Patches.GamePatches.ApplyingRemotePrice = true;
                            // a bare catch here swallowed the whole failure: the guest saw its
                            // price "accepted" and nothing anywhere said otherwise
                            bool priceThrew = false;
                            try { CPlayerData.SetItemPrice((EItemType)itemType, price); }
                            catch (Exception e)
                            {
                                priceThrew = true;
                                CoopPlugin.Log.LogWarning($"item price apply ({(EItemType)itemType}): " + e.Message);
                            }
                            finally { Patches.GamePatches.ApplyingRemotePrice = false; }
                            // same reason as the unknown-type branch: the guest has to hear it
                            if (priceThrew)
                                Send(msg.ConnId, new ToastMessage { Text = "the host couldn't apply that price - it may be missing that product" });
                            // the periodic PriceList broadcast echoes this to every client
                        }
                    }
                    break;
                }
                case MsgType.ObjMoveDelta:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    if (msg.Message is ObjMoveDeltaMessage objMoveDelta)
                        _objMoves.ApplyRemote(objMoveDelta.Entries);
                    break;
                }
                case MsgType.ObjMoveRequest:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    if (msg.Message is ObjMoveRequestMessage objMoveRequest)
                    {
                        var entries = objMoveRequest.Entries;
                        // host authority: never let a client's (possibly stale) move-request
                        // override an object the host is actively dragging - that echo is what
                        // snapped placed machines back to their old spot every guest tick
                        // Relay only poses the host actually accepted. Broadcasting the
                        // original request made a rejected stale move teleport other guests.
                        var accepted = _objMoves.ApplyRemote(entries, dropIfHostMoving: true);
                        if (_net.ConnectionCount > 1 && accepted.Count > 0) // see ShelfRequest note
                            Broadcast(new ObjMoveDeltaMessage { Entries = accepted });
                    }
                    break;
                }
                case MsgType.CardPriceSet:
                {
                    if (msg.Message is CardPriceSetMessage cardPriceSet)
                    {
                        var card = cardPriceSet.Card;
                        float price = cardPriceSet.Price;
                        if (!InGameLevel())
                        {
                            _pendingCardPrices.Add(new KeyValuePair<CardData, float>(card, price));
                            break;
                        }
                        // IN-FLIGHT EDIT GATE. The host's price heal rebroadcasts its own
                        // GetCardPrice for every displayed card; when our own CardPriceSet was
                        // lost, that heal used to stomp the guest's fresh edit right back to the
                        // old value. While we are still chasing an edit for this card, only OUR
                        // value is allowed in - the retry loop keeps working until the host
                        // confirms it (or gives up loudly).
                        string key = CardPriceKey(card);
                        if (key != null && _myCardPrices.TryGetValue(key, out var mine))
                        {
                            if (Math.Abs(mine.Value - price) <= CardPriceEpsilon)
                            {
                                mine.Acked = true;   // the other side is holding our value: ack
                                _myCardPrices[key] = mine;
                            }
                            else if (!mine.Acked)
                            {
                                break;               // stale heal racing our edit: ignore it
                            }
                            else
                            {
                                mine.Value = price;  // they legitimately re-priced it; adopt, or
                                _myCardPrices[key] = mine; // our heals would war with theirs
                            }
                        }
                        bool applied;
                        float actual;
                        bool relayAnyway;
                        Patches.GamePatches.ApplyingRemotePrice = true;
                        // graded (>10 encoded) prices route through Grading Overhaul's own store
                        // (register the card, then GO's SetCardPrice patch handles it); ungraded
                        // prices use the vanilla path; no-op for a modded grade without GO.
                        string who = PeerNames.TryGetValue(msg.ConnId, out var pn) ? pn : ("conn " + msg.ConnId);
                        try { applied = ApplyRemoteCardPrice(card, price, who, out actual, out relayAnyway); }
                        finally { Patches.GamePatches.ApplyingRemotePrice = false; }
                        if (!applied)
                        {
                            // The HOST couldn't store it, but the price itself is fine (content
                            // pack missing here, encoded grade with no Grading Overhaul here,
                            // store rejected the write). Forward the ORIGINAL (card, price) once
                            // - a PURE RELAY, never the read-back, which would be this machine's
                            // wrong value. It doubles as the sender's ack (it sees its own number
                            // come back and stops retrying instead of burning 12 attempts) and
                            // lets guests that DO have the content pack converge.
                            // Loop-safe: only the host ever re-broadcasts, and a host's own
                            // Broadcast never comes back to it.
                            if (relayAnyway && Role == CoopRole.Host)
                            {
                                var passCard = card;
                                float passValue = price;
                                Broadcast(new CardPriceSetMessage { Card = passCard, Price = passValue });
                            }
                            break;
                        }
                        // HOST: the READ-BACK value goes straight back out to every client. That
                        // one broadcast is both the sender's ACK (this handler acked nothing
                        // before) and the 3+ player relay (it reached nobody but the host).
                        if (Role == CoopRole.Host)
                        {
                            var echoCard = card;
                            float echoValue = actual;
                            Broadcast(new CardPriceSetMessage { Card = echoCard, Price = echoValue });
                        }
                    }
                    break;
                }
                case MsgType.RegisterState:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    if (msg.Message is RegisterStateMessage registerState) _register.ClientApplyState(registerState);
                    break;
                }
                case MsgType.RegisterCart:
                {
                    if (Role != CoopRole.Client || !InGameLevel()) break;
                    if (msg.Message is RegisterCartMessage registerCart) _register.ClientApplyCart(registerCart);
                    break;
                }
                case MsgType.RegisterOp:
                {
                    if (Role != CoopRole.Host || !InGameLevel()) break;
                    if (msg.Message is RegisterOpMessage registerOp) _register.HostApplyOp(registerOp, msg.ConnId);
                    break;
                }
                case MsgType.Ping:
                    _net?.Send(msg.ConnId, new PongMessage());
                    break;
                case MsgType.Pong:
                    break;
                case MsgType.Bye:
                {
                    string reason = "the host ended the session";
                    var bye = msg.Message as ByeMessage;
                    if (bye != null && !string.IsNullOrEmpty(bye.Reason)) reason = bye.Reason;
                    if (Role == CoopRole.Client)
                    {
                        ErrorLine = reason;
                        Shutdown("rejected: " + reason);
                    }
                    else
                    {
                        _net.Kick(msg.ConnId);
                    }
                    break;
                }
            }
        }

        private void SendWorldTo(int connId)
        {
            byte[] rawSave;
            byte[] rawBundle;
            int hostSlot;
            try
            {
                // game-state reads must stay on the main thread; the gzip below moves to
                // the worker (compressing a multi-MB save used to freeze the host's frame
                // at every join)
                // the base save is snapshotted to the THROWAWAY slot (SaveTransfer.HostSnapshotSlot)
                // and the sidecar-mod files must come from that SAME slot: SaveGameData(6) fires
                // the sidecar mods' own save hooks (they key off the active slot), so slot 6
                // holds the FRESH per-save mod data matching the just-written world - while the
                // host's real slot may be an hour stale. Mismatched halves shipped stale card
                // prices/shop config to the joiner. The same slot digit rides the wire so the
                // client's rename (slot 6 -> its coop slot) stays symmetric.
                rawSave = SaveTransfer.BuildHostPayload();
                hostSlot = SaveTransfer.HostSnapshotSlot;
                try { rawBundle = SidecarTransfer.BuildBundle(hostSlot); }
                catch (Exception e)
                {
                    CoopPlugin.Log.LogWarning("Sidecar bundle failed (sending base save only): " + e.Message);
                    rawBundle = new byte[0];
                }
            }
            catch (Exception e)
            {
                ErrorLine = "Could not snapshot the shop: " + e.Message;
                CoopPlugin.Log.LogError(e);
                return;
            }

            // The HOST's id space is the canonical one for this session, so the client needs our
            // registry (EPL enums) and our custom-card list (CreateCards/CardForge) to translate
            // by NAME at the wire boundary - see Util.EnumMap. Snapshotted HERE, on the main
            // thread, because both readers walk loaded types and the BepInEx plugin list; the
            // send itself runs on the worker below. Same encoding as the Hello blob, same cap,
            // same fail-to-empty behavior (an empty blob leaves the client on identity, i.e. on
            // exactly what this build did before translation existed).
            byte[] gzHostEnum = GzipLines(SafeEnumLines());
            byte[] gzHostCards = GzipLines(SafeCardsList());
            PlayerModelStateMessage modelState = Role == CoopRole.Host ? BuildPlayerModelState() : null;

            var net = _net;
            new Thread(() =>
            {
                const int chunk = 128 * 1024;
                try
                {
                    byte[] payload = Msg.Gzip(rawSave);
                    byte[] bundle = rawBundle.Length > 0 ? Msg.Gzip(rawBundle) : rawBundle;
                    CoopPlugin.Log.LogInfo($"transfer: save {payload.Length / 1024} KB, mod data {bundle.Length / 1024} KB (compressed)");

                    net.Send(connId, new WelcomeMessage
                    {
                        WireVersion = Msg.WireVersion,
                        Version = CoopPlugin.Version,
                HostName = EffectivePlayerName,
                SteamId = _steam == null ? 0 : _steam.LocalSteamId,
                        SaveLength = payload.Length,
                        HostSlot = hostSlot,
                        BundleLength = bundle.Length,
                        SelfId = (byte)connId,
                        HostEnumBlob = gzHostEnum,
                        HostCardsBlob = gzHostCards,
                    });

                    for (int off = 0; off < payload.Length; off += chunk)
                    {
                        int len = Math.Min(chunk, payload.Length - off);
                        int o = off;
                        var chunkBytes = new byte[len];
                        Buffer.BlockCopy(payload, o, chunkBytes, 0, len);
                        net.Send(connId, new SaveChunkMessage { Offset = o, Data = chunkBytes });
                    }
                    net.Send(connId, new SaveDoneMessage { TotalLength = payload.Length });

                    for (int off = 0; off < bundle.Length; off += chunk)
                    {
                        int len = Math.Min(chunk, bundle.Length - off);
                        int o = off;
                        var chunkBytes = new byte[len];
                        Buffer.BlockCopy(bundle, o, chunkBytes, 0, len);
                        net.Send(connId, new BundleChunkMessage { Offset = o, Data = chunkBytes });
                    }
                    net.Send(connId, new BundleDoneMessage { TotalLength = bundle.Length });
                    if (modelState != null) net.Send(connId, modelState);
                }
                catch (Exception e)
                {
                    CoopPlugin.Log.LogError("World send failed: " + e.Message);
                }
            }) { IsBackground = true, Name = "CoopWorldSend" }.Start();
        }

        private void OnGUI()
        {
            _ui.Draw(this, _net);
        }

        private void ApplyShopName(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (CPlayerData.GetPlayerName() != name)
            {
                CPlayerData.PlayerName = name;
                CoopPlugin.Log.LogInfo("shop name synced: " + name);
            }
            _lastShopNameApplied = name;
            if (_shopSign != null) { try { _shopSign.text = name; } catch { } }
        }

        private void ApplyRoster(RosterMessage roster)
        {
            if (Role != CoopRole.Client) return;
            var seen = new HashSet<int>();
            foreach (var entry in roster.Entries)
            {
                int id = entry.Id;
                if (id == _selfId) continue;
                seen.Add(id);
                _rosterNames[id] = entry.Name;
                if (_relayIds.Add(id)) CoopPlugin.Log.LogInfo("peer in shop: " + entry.Name);
                _avatars.SetName(1000 + id, entry.Name);
            }
            _relayIds.RemoveWhere(id =>
            {
                if (seen.Contains(id)) return false;
                _avatars.Remove(1000 + id);
                return true;
            });
        }

        private void ApplyPlayerState(int avatarId, PlayerStateMessage state, bool directPeer)
        {
            _diagRecvStates++;
            _avatars.UpdateState(avatarId, state.Position, state.Yaw, state.Speed,
                state.Hold, state.HoldTypes, state.HoldCards);
            if (PeerNames.TryGetValue(avatarId, out var peerName)) _avatars.SetName(avatarId, peerName);

            if (directPeer && Role == CoopRole.Host && _net.ConnectionCount > 1)
            {
                var relay = new RelayStateMessage
                {
                    SenderId = (byte)avatarId,
                    State = state
                };
                foreach (int cid in _net.ConnIds())
                    if (cid != avatarId) _net.SendTransient(cid, relay);
            }

            if (directPeer && _gotStateFrom.Add(avatarId))
            {
                string who = PeerNames.TryGetValue(avatarId, out var name) ? name : ("player " + avatarId);
                CoopPlugin.Log.LogInfo("Position link active with " + who);
                if (Role == CoopRole.Host) StatusLine = "Hosting - " + who + " is in your shop!";
            }
        }

        private void ApplyPurchaseRequest(int connectionId, PurchaseRequestMessage request)
        {
            if (Role != CoopRole.Host || request == null || request.Lines == null || request.Lines.Count == 0) return;
            if (request.Kind > 2) { CoopPlugin.Log.LogWarning($"purchase request from conn {connectionId} has invalid kind {request.Kind}"); return; }

            Action<bool, string> result = (success, text) =>
                Send(connectionId, new PurchaseResultMessage { Kind = request.Kind, Success = success, Text = text });

            var resolved = new List<int>(request.Lines.Count);
            var linePrices = new List<double>(request.Lines.Count);
            double total = 0.0;
            for (int i = 0; i < request.Lines.Count; i++)
            {
                var line = request.Lines[i];
                if (line == null || line.Count <= 0) { result(false, "purchase cancelled - invalid request"); return; }
                int index = -1;
                float price = 0f;
                try
                {
                    if (request.Kind == 0)
                    {
                        index = ResolveRestockIndex(line.ItemType, line.IsBig, line.Name ?? "", out _);
                        if (index >= 0)
                        {
                            var rd = CatalogAt(index);
                            price = CPlayerData.GetItemCost(rd.itemType) * RestockManager.GetMaxItemCountInBox(rd.itemType, rd.isBigBox) * line.Count;
                        }
                    }
                    else if (request.Kind == 1)
                    {
                        var fp = InventoryBase.GetFurniturePurchaseData((EObjectType)line.ItemType);
                        if (fp != null && InventoryBase.GetSpawnInteractableObjectPrefab((EObjectType)line.ItemType) != null)
                        { index = line.ItemType; price = fp.price; }
                    }
                    else
                    {
                        index = ResolveRestockIndex(line.ItemType, line.IsBig, line.Name ?? "", out _);
                        if (index >= 0)
                        {
                            var rd = CatalogAt(index);
                            if (CPlayerData.GetIsItemLicenseUnlocked(index)) price = 0f;
                            else price = rd.licensePrice;
                        }
                    }
                }
                catch (Exception e)
                {
                    CoopPlugin.Log.LogWarning($"purchase preflight failed for conn {connectionId}, line {i}: {e.Message}");
                    index = -1;
                }
                if (index < 0)
                {
                    result(false, "purchase cancelled - the item is not in the host's catalog");
                    return;
                }
                resolved.Add(index);
                double linePrice = Math.Max(0f, price);
                if (double.IsNaN(linePrice) || double.IsInfinity(linePrice))
                {
                    CoopPlugin.Log.LogWarning($"purchase preflight produced invalid price for conn {connectionId}, line {i}");
                    result(false, "purchase cancelled - the host returned an invalid price");
                    return;
                }
                linePrices.Add(linePrice);
                total += linePrice;
            }

            double available = CPlayerData.m_CoinAmountDouble - _pendingReduceThisFrame;
            if (total > available + 0.0001)
            {
                CoopPlugin.Log.LogInfo($"purchase from conn {connectionId} declined: need {total:F2}, available {available:F2}");
                result(false, "not enough money - the purchase was cancelled");
                return;
            }

            string who = PeerNames.TryGetValue(connectionId, out var name) ? name : "player";
            var failures = new List<string>();
            // Reserve the validated amount before delivery. Dispatch is serialized on
            // the Unity thread, but other operations can be accepted in this same frame;
            // reserving here prevents a successful delivery from later becoming a free
            // delivery when the wallet is checked again after spawning.
            _pendingReduceThisFrame += total;
            double deliveredTotal = 0.0;
            for (int i = 0; i < request.Lines.Count; i++)
            {
                var line = request.Lines[i];
                try
                {
                    if (request.Kind == 0)
                    {
                        RestockManager.SpawnPackageBoxItemMultipleFrame(resolved[i], line.Count);
                        CEventManager.QueueEvent(new CEventPlayer_AddShopExp(line.Count * 5));
                    }
                    else if (request.Kind == 1)
                    {
                        CoopPlugin.Log.LogInfo($"{who} bought furniture: {(EObjectType)line.ItemType}");
                        ShelfManager.SpawnInteractableObjectInPackageBox((EObjectType)line.ItemType, line.Position, line.Rotation);
                    }
                    else if (!CPlayerData.GetIsItemLicenseUnlocked(resolved[i]))
                    {
                        if (!ApplyLicenseUnlock(line.ItemType, line.IsBig, line.Name ?? ""))
                            throw new InvalidOperationException("license unlock was not applied");
                        Broadcast(new LicenseUnlockMessage { ItemType = (EItemType)line.ItemType, IsBig = line.IsBig, RestockName = line.Name ?? "" });
                    }
                    deliveredTotal += linePrices[i];
                }
                catch (Exception e)
                {
                    string item = string.IsNullOrEmpty(line.Name) ? line.ItemType.ToString() : line.Name;
                    failures.Add($"'{item}' ({e.Message})");
                    CoopPlugin.Log.LogWarning($"purchase delivery failed for conn {connectionId}, line {i} ({item}): {e.Message}");
                }
            }

            // Release the reservation for failed lines; the remaining reservation is the
            // amount that will be backed by the queued authoritative coin reduction.
            _pendingReduceThisFrame -= total - deliveredTotal;
            bool charged = false;
            if (deliveredTotal > 0.0)
            {
                CEventManager.QueueEvent(new CEventPlayer_ReduceCoin((float)deliveredTotal));
                charged = true;
            }

            if (failures.Count == 0)
            {
                result(true, "purchase accepted");
            }
            else
            {
                string detail = string.Join(", ", failures);
                string chargeNote = charged ? $" charged ${deliveredTotal:F0}" : " (not charged)";
                result(false, $"purchase partially completed - {detail}; delivered items{chargeNote}");
            }
        }

        private void ApplyPurchaseResult(PurchaseResultMessage result)
        {
            if (Role != CoopRole.Client || result == null) return;
            if (result.Success)
                Patches.GamePatches.ClientPurchaseAccepted(result.Kind);
            RegisterLine = result.Text ?? "";
            RegisterLineTimer = 8f;
            CoopPlugin.Log.LogInfo("host says: " + RegisterLine);
        }

        private void ApplyEconomyContribution(int connectionId, EconContributionMessage message)
        {
            switch (message.Kind)
            {
                case 1: CEventManager.QueueEvent(new CEventPlayer_AddCoin(message.Value)); break;
                case 2:
                    if (message.Value <= 0f) break;
                    double balance = CPlayerData.m_CoinAmountDouble - _pendingReduceThisFrame;
                    if ((double)message.Value > balance + 0.0001)
                    { Send(connectionId, new ToastMessage { Text = "purchase declined - the shared wallet is short" }); _lastCoinSent = double.MinValue; }
                    else { _pendingReduceThisFrame += message.Value; CEventManager.QueueEvent(new CEventPlayer_ReduceCoin(message.Value)); }
                    break;
                case 3: CEventManager.QueueEvent(new CEventPlayer_AddShopExp((int)message.Value)); break;
                case 4: CEventManager.QueueEvent(new CEventPlayer_AddFame((int)message.Value)); break;
            }
        }

        private void ApplySprayHit(SprayHitMessage message)
        {
            if (_cmSpray == null) _cmSpray = FindObjectOfType<CustomerManager>();
            if (_cmSpray == null) return;
            var customers = _cmSpray.GetCustomerList();
            for (int i = 0; i < customers.Count; i++)
                if (customers[i] != null)
                    customers[i].DeodorantSprayCheck(message.Position, message.Range, message.Potency);
        }

        private void ApplyGradedRemove(int connectionId, GradedRemoveMessage message)
        {
            var card = message.Card;
            if (card == null) return;
            if (!InGameLevel())
            {
                _pendingCardDeltas.Add(new PendingCard { IsAdd = false, Amount = 1, Card = card });
                return;
            }
            if (!ApplyCardDelta(false, 1, card, out bool relayAnyway) && !relayAnyway) return;
            if (_net == null) return;
            foreach (int id in _net.ConnIds())
                if (id != connectionId) _net.Send(id, message);
        }
    }
}
