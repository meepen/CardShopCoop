using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Host-authoritative warehouse storage, with ONE channel and TWO backends so the same DLL
    /// works on either game build:
    ///
    /// * record backend (game 1.00): a stored box is a serialized <c>StoredBoxRecord</c> owned by a
    ///   <see cref="ShelfCompartment"/> and the live <c>InteractablePackagingBox_Item</c> is
    ///   destroyed, so it is invisible to the box engine and needs this channel. Every
    ///   record-specific symbol is reflection-only.
    /// * live backend (legacy 1.0): a stored box is a real <c>InteractablePackagingBox_Item</c> the
    ///   compartment holds, so the box channel already carries it; this module adds the host-side
    ///   ops plus the normalised state a record-backed peer needs.
    ///
    /// Either way the wire is the same: the host owns the normalised ordered list
    /// (<see cref="WarehouseStateMessage"/>), and client store/take are forwarded as
    /// <see cref="WarehouseOpMessage"/> requests so rack membership has a single owner. Updates are
    /// pushed from the game's own mutation events - no poll - and eventual correctness comes from
    /// the gradual slice sweep in <see cref="PeriodicUpdate"/>, never a periodic full resend.
    /// </summary>
    public class WarehouseBoxSync : TickableCoopModule
    {
        private static WarehouseBoxSync _instance;

        /// <summary>True while this module drives game code, so our own patches do not mistake
        /// an applied change for a local action.</summary>
        public static bool ApplyingRemote;

        public Action<INetMessage> BroadcastState;    // set by CoopCore: host -> clients
        public Action<INetMessage> SendOp;            // set by CoopCore: client -> host
        public Action<int, INetMessage> SendToClient; // set by CoopCore: host -> one client
        public Func<InteractablePackagingBox_Item, bool> HoldClientBox; // set by CoopCore

        // request bookkeeping
        private int _nextRequestId;
        private readonly Dictionary<ushort, float> _pendingStoreAt = new Dictionary<ushort, float>();
        private readonly Dictionary<int, int> _takeKeyByRequest = new Dictionary<int, int>();
        private readonly HashSet<int> _pendingTakeCompartments = new HashSet<int>();
        private readonly Dictionary<int, float> _pendingTakeAt = new Dictionary<int, float>();
        private readonly HashSet<ushort> _pendingTakeBoxes = new HashSet<ushort>();
        private readonly HashSet<long> _hostSeenRequests = new HashSet<long>();

        /// <summary>Client: the host's warehouse backend as advertised by the last state. A live
        /// guest needs a record host's entries materialized as live boxes, but must leave a live
        /// host's racks alone (the box channel already carries those).</summary>
        private static bool _hostLiveBoxes;
        private static bool _hasHostState;

        /// <summary>Throttle for the "rack refused a materialised box" warning.</summary>
        private static double _lastMaterializeWarn;

        // ---- capability: one of two warehouse backends ----
        // 1.00 stores warehouse boxes as serialized StoredBoxRecord objects owned by a
        // ShelfCompartment (all reflection; absent from the legacy build). Legacy builds store
        // live InteractablePackagingBox_Item objects held by the compartment instead. The
        // module is enabled when EITHER backend resolves and picks records when they exist.
        private static bool _probed;
        private static bool _records; // the whole 1.00 record API resolved
        private static bool _live;    // the live-box warehouse model resolved (both builds)
        private static bool _liveUsable; // live model usable: resolved AND record storage absent
        private static Type _tRecord;
        private static MethodInfo _miCount;
        private static MethodInfo _miPeek;
        private static MethodInfo _miPop;
        private static MethodInfo _miAdd;
        private static MethodInfo _miCompartments;
        private static MethodInfo _miRebuild;
        private static FieldInfo _fiItemType;
        private static FieldInfo _fiAmount;
        private static FieldInfo _fiBig;
        private static Type _tEnumType;
        // The record build normally reaches this through the box's Update() lerp. A parked
        // (inactive) host box never gets that Update, so finish the game's own path explicitly.
        // This is present on both supported builds, but remains a reflected surface so the call
        // is not coupled to the protected game method's accessibility.
        private static readonly MethodInfo _miOnFinishLerp =
            CardShopCoop.Util.ReflectionSurface.RequiredMethod(typeof(InteractableObject), "OnFinishLerp");
        private static readonly FieldInfo _fiMarkForDataOnlyDestroy =
            AccessTools.Field(typeof(InteractablePackagingBox_Item), "m_MarkForDataOnlyDestroy");
        // The take path needs two more 1.00-only symbols - PackageBoxCandidate and
        // RestockManager.MaterializeStoredCandidate - so they are resolved by name like the
        // record API above and required by _records. Nothing may name them directly.
        private static Type _tCandidate;
        private static FieldInfo _fiCandidateCompartment;
        private static MethodInfo _miMaterialize;

        public WarehouseBoxSync()
        {
            _instance = this;
        }

        public override string Name => "warehouse";

        public override void Start() => _instance = this;

        /// <summary>Gradual re-assertion: a batch of compartments per slice, round-robin, sized so
        /// one full pass completes within roughly SweepCycleSeconds regardless of warehouse size -
        /// the removed heal re-asserted everything within 12 s, so an unbounded one-compartment-
        /// per-5 s cursor would be a latency regression (40 compartments => 200 s). A periodic FULL
        /// resend is still not sent (AGENTS.md): the pass is spread over slices.</summary>
        private const float SweepSliceSeconds = 5f;
        private const float SweepCycleSeconds = 30f;
        private float _sweepTimer;
        private int _sweepCursor;

        public override void PeriodicUpdate(float delta)
        {
            if (CoopCore.Role != CoopRole.Host || BroadcastState == null
                || !CoopCore.InSessionWorld || !Available())
                return;
            _sweepTimer += delta;
            if (_sweepTimer < SweepSliceSeconds)
                return;
            _sweepTimer = 0f;
            Guarded("sweep", () =>
            {
                var comps = Compartments();
                if (comps == null || comps.Count == 0)
                {
                    _sweepCursor = 0;
                    return;
                }
                int total = comps.Count;
                int slicesPerCycle = Mathf.Max(1, Mathf.RoundToInt(SweepCycleSeconds / SweepSliceSeconds));
                int perSlice = Mathf.Max(1, (total + slicesPerCycle - 1) / slicesPerCycle);
                if (_sweepCursor < 0 || _sweepCursor >= total)
                    _sweepCursor = 0;
                var msg = new WarehouseStateMessage { Full = false, HostLiveBoxes = !UsesRecords };
                for (int n = 0; n < perSlice; n++)
                {
                    var comp = comps[_sweepCursor];
                    _sweepCursor = (_sweepCursor + 1) % total;
                    if (!IsWarehouse(comp))
                        continue; // skipped this slice; the cursor still advanced
                    var entry = new WarehouseCompartmentEntry
                    {
                        ShelfIndex = comp.GetWarehouseIndex(),
                        CompartmentIndex = comp.GetIndex(),
                    };
                    var shelf = comp.GetWarehouseShelf();
                    if (shelf != null)
                        entry.ShelfId = PlacedObjectIdentity.AssignHost(shelf);
                    ReadStored(comp, entry.Records);
                    msg.Compartments.Add(entry);
                }
                if (msg.Compartments.Count > 0)
                    BroadcastState(msg);
            });
        }

        /// <summary>Host: send the COMPLETE warehouse to one connection - the join catch-up path.
        /// Not periodic; the sweep above is what guarantees eventual correctness.</summary>
        public override void FullUpdate(int connId)
        {
            if (CoopCore.Role != CoopRole.Host || SendToClient == null || !Available())
                return;
            Guarded("full", () => SendToClient(connId, BuildState()));
        }

        /// <summary>Client: expire warehouse take requests that never got a result (a dropped
        /// frame or a host-side exception), so a compartment cannot stay un-clickable forever.</summary>
        protected override void OnClientTick(in SyncFrame frame)
        {
            // A warehouse state that arrives before the client's racks exist is not retried here:
            // the host's unconditional slice sweep re-sends every live compartment on its next
            // pass, and a joiner re-baselines through JoinResyncRequest once its world has loaded.
            // A local retry loop was a second safety net for the same job, and because applying a
            // compartment is a teardown+respawn of its boxes it rebuilt the rack over and over.

            if (_pendingTakeAt.Count == 0)
                return;
            float now = (float)Time.realtimeSinceStartupAsDouble;
            List<int> stale = null;
            foreach (var kv in _pendingTakeAt)
                if (now - kv.Value > 5f)
                    (stale ?? (stale = new List<int>())).Add(kv.Key);
            if (stale == null)
                return;
            for (int i = 0; i < stale.Count; i++)
            {
                int req = stale[i];
                _pendingTakeAt.Remove(req);
                int key;
                if (_takeKeyByRequest.TryGetValue(req, out key))
                {
                    _takeKeyByRequest.Remove(req);
                    _pendingTakeCompartments.Remove(key);
                }
            }
        }

        public override void Reset()
        {
            _nextRequestId = 0;
            _sweepTimer = 0f;
            _sweepCursor = 0;
            _hostLiveBoxes = false;
            _hasHostState = false;
            _pendingStoreAt.Clear();
            _takeKeyByRequest.Clear();
            _pendingTakeCompartments.Clear();
            _pendingTakeAt.Clear();
            _pendingTakeBoxes.Clear();
            _hostSeenRequests.Clear();
        }

        public override void ForceResend()
        {
            _sweepTimer = 0f; // a heal just went out; don't sweep immediately after
            BroadcastNow();
        }

        /// <summary>Client: the placed-object roster changed, so every index-keyed compartment
        /// address may now point at a different object. Nothing to drop any more: each state is
        /// applied once, against the addresses as they are when it arrives.</summary>
        public void OnClientRosterChanged()
        {
        }

        public override void Dispose()
        {
            base.Dispose();
            if (ReferenceEquals(_instance, this))
                _instance = null;
            ApplyingRemote = false;
        }

        // ---------------- capability probe ----------------

        /// <summary>True when a warehouse backend is usable: the 1.00 stored-box records, or the
        /// live-box model the legacy build (and any build without records) actually stores.</summary>
        internal static bool Available()
        {
            Probe();
            return _records || _liveUsable;
        }

        /// <summary>True when the 1.00 stored-box-record backend is the one to use. A client
        /// realizes an incoming state in its OWN backend: a record-capable build rebuilds
        /// records, a legacy build materializes live boxes.</summary>
        internal static bool UsesRecords
        {
            get
            {
                Probe();
                return _records;
            }
        }

        /// <summary>True when the game has the record STORAGE model at all, even if part of its
        /// API failed to resolve. The box family uses this (not <see cref="UsesRecords"/>) to
        /// decide whether a stored box is a record or a live object: in a build with
        /// StoredBoxRecord present but an unresolved take symbol the channel is inert, and
        /// falling through to the live store recipe there would bank a local record and fire the
        /// data-only destroy - destroying the host's real box.</summary>
        internal static bool HasRecordStorage
        {
            get
            {
                Probe();
                return _tRecord != null;
            }
        }

        /// <summary>True when the warehouse channel owns rack materialization on this side, which
        /// means the box channel must park/hide a stored mirror instead of running the store recipe
        /// (two writers for one slot is exactly the duplicate this exists to prevent).
        ///
        /// Before the first host state the answer is deliberately FALSE, i.e. keep the box channel's
        /// previous behaviour: parking on a guess is the risky direction, because if the host never
        /// publishes a state (a build where a record symbol fails to resolve makes Available() false
        /// and gates every send) a parked mirror is never materialised by anything and the guest's
        /// warehouse racks render empty forever while the host's are full. Not parking can only cost
        /// a brief double-writer window at join, and the first state arrives right after the world
        /// load - long before anyone can store a box.</summary>
        public static bool RackOwnedByWarehouseChannel()
        {
            if (CoopCore.Role == CoopRole.Host)
            {
                return UsesRecords;
            }
            if (!_hasHostState)
            {
                return false;
            }
            return Available() && !_hostLiveBoxes;
        }

        private static void Probe()
        {
            if (_probed)
                return;
            _probed = true;
            try
            {
                _tRecord = typeof(ShelfCompartment).Assembly.GetType("StoredBoxRecord", false);
                _miCount = AccessTools.Method(typeof(ShelfCompartment), "GetStoredBoxRecordCount");
                _miPeek = AccessTools.Method(typeof(ShelfCompartment), "PeekStoredBoxRecord");
                _miPop = AccessTools.Method(typeof(ShelfCompartment), "TryPopLastStoredBoxRecord");
                _miAdd = AccessTools.Method(typeof(ShelfCompartment), "AddStoredBoxRecord");
                _miCompartments = AccessTools.Method(typeof(RestockManager), "GetWarehouseCompartmentList");
                var tBatcher = typeof(ShelfCompartment).Assembly.GetType("StoredBoxVisualBatcher", false);
                _miRebuild = tBatcher == null ? null : AccessTools.Method(tBatcher, "RebuildImmediate");
                if (_tRecord != null)
                {
                    _fiItemType = AccessTools.Field(_tRecord, "itemType");
                    _fiAmount = AccessTools.Field(_tRecord, "amount");
                    _fiBig = AccessTools.Field(_tRecord, "isBigBox");
                    _tEnumType = _fiItemType == null ? null : _fiItemType.FieldType;
                }
                // Resolve from the GAME assembly, never by bare name: a same-named type in another
                // loaded plugin assembly would otherwise win, silently disabling the whole record
                // channel on a 1.00 build (the same defence GamePatches.EnsureCheatManager uses).
                _tCandidate = typeof(ShelfCompartment).Assembly.GetType("PackageBoxCandidate", false);
                _miMaterialize = AccessTools.Method(typeof(RestockManager), "MaterializeStoredCandidate");
                if (_tCandidate != null)
                    _fiCandidateCompartment = AccessTools.Field(_tCandidate, "storedCompartment");
                _records = _tRecord != null && _miCount != null && _miPeek != null
                    && _miPop != null && _miAdd != null && _miCompartments != null
                    && _fiItemType != null && _fiAmount != null && _fiBig != null
                    && _tCandidate != null && _miMaterialize != null
                    && _fiCandidateCompartment != null;
                // Live-box model: the compartment's own box list, the warehouse enumeration, and
                // the add/remove used to store and take. All four exist in both builds, so the
                // probe only decides which backend owns warehouse storage.
                _live = AccessTools.Method(typeof(ShelfCompartment), "GetInteractablePackagingBoxList") != null
                    && AccessTools.Method(typeof(ShelfCompartment), "GetLastInteractablePackagingBox") != null
                    && AccessTools.Method(typeof(ShelfCompartment), "AddBox") != null
                    && AccessTools.Method(typeof(ShelfCompartment), "RemoveBox") != null
                    && AccessTools.Method(typeof(WarehouseShelf), "GetStorageCompartmentList") != null;
                // The live backend may only own warehouse storage when the record storage model is
                // genuinely ABSENT. If StoredBoxRecord exists but part of its API did not resolve,
                // the live path would run its store recipe against record-backed storage and bank
                // records locally - the exact corruption this module exists to prevent. Stay inert
                // and say so instead.
                _liveUsable = _live && _tRecord == null;
            }
            catch (Exception e) { Swallow.Log(e); }
            if (_records)
                CoopPlugin.Log.LogInfo("WarehouseBoxSync: stored-box records present - warehouse storage is record-backed and host-authoritative");
            else if (_liveUsable)
                CoopPlugin.Log.LogInfo("WarehouseBoxSync: no stored-box records - warehouse storage is live-box-backed and host-authoritative");
            else if (_tRecord != null)
                CoopPlugin.Log.LogError("WarehouseBoxSync: record storage is present but its API did not fully resolve - warehouse sync disabled rather than driving the live path against record storage");
            else
                CoopPlugin.Log.LogError("WarehouseBoxSync: no warehouse backend found on this build (no record storage, no live box API) - warehouse sync disabled");
        }

        // ---------------- patches ----------------

        public static void ApplyPatches(Harmony h)
        {
            Probe();
            if (!_records && !_liveUsable)
                return;

            // Client store/take are forwarded on BOTH backends, so warehouse rack membership has a
            // single owner (the host) in every pairing. Letting a live guest take vanilla-locally
            // was a bug: the host applies that Held claim WITHOUT un-hooking its own racked copy,
            // so its snapshot read Held+Stored and the client's stored-while-held rule then undid
            // the take. The prefixes still no-op when this machine is the host or a state is being
            // applied.
            Try(h, typeof(InteractablePackagingBox_Item), "DispenseItem",
                prefix: new HarmonyMethod(typeof(WarehouseBoxSync), nameof(ClientStorePrefix)));
            Try(h, typeof(InteractableStorageCompartment), "OnMouseButtonUp",
                prefix: new HarmonyMethod(typeof(WarehouseBoxSync), nameof(ClientTakePrefix)));

            if (UsesRecords)
            {
                // Event hooks: the mutation methods themselves push the new state - no poll, no
                // deferred flush.
                Try(h, typeof(ShelfCompartment), "AddStoredBoxRecord",
                    postfix: new HarmonyMethod(typeof(WarehouseBoxSync), nameof(AddStoredBoxRecordPostfix)));
                Try(h, typeof(ShelfCompartment), "TryPopLastStoredBoxRecord",
                    postfix: new HarmonyMethod(typeof(WarehouseBoxSync), nameof(TryPopStoredBoxRecordPostfix)));
                return;
            }

            // Live backend host hooks. AddBox/RemoveBox run for every shelf, so the postfix filters
            // to warehouse racks first.
            Try(h, typeof(ShelfCompartment), "AddBox",
                postfix: new HarmonyMethod(typeof(WarehouseBoxSync), nameof(AddBoxPostfix)));
            Try(h, typeof(ShelfCompartment), "RemoveBox",
                postfix: new HarmonyMethod(typeof(WarehouseBoxSync), nameof(RemoveBoxPostfix)));
        }

        private static void Try(Harmony h, Type type, string method,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            try
            {
                var original = AccessTools.Method(type, method);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning($"WarehouseBoxSync patch target missing: {type.Name}.{method}");
                    return;
                }
                h.Patch(original, prefix: prefix, postfix: postfix);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning($"WarehouseBoxSync patch failed: {type.Name}.{method}: {e.Message}");
            }
        }

        /// <summary>Client: a warehouse store click is forwarded to the host instead of running
        /// vanilla locally. Local vanilla would bank a record the host never sees and drive the
        /// 1.0 data-only destroy (a spurious box Removed).</summary>
        public static bool ClientStorePrefix(InteractablePackagingBox_Item __instance,
            bool isPlayer, ShelfCompartment targetItemCompartment)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote || BoxShared.ApplyingRemote)
                return true;
            // Only a real PLAYER store (OnHoldStateLeftMousePress passes isPlayer:true) is
            // forwarded. The SAME method also serves the world-load restore
            // (ShelfManager calls DispenseItem(isPlayer:false, ...)) and the box channel's own
            // mirror apply, so intercepting those left every restored rack box unregistered and
            // spawned a duplicate mirror of it.
            if (!isPlayer)
                return true;
            if (!IsWarehouse(targetItemCompartment))
                return true;
            var self = _instance;
            var boxes = CoopCore.Instance != null ? CoopCore.Instance.Boxes : null;
            if (self == null || boxes == null || __instance == null)
            {
                Notice("couldn't store that box yet - try again");
                return false;
            }
            ushort boxId;
            if (!boxes.TryGetClientId(__instance, out boxId))
            {
                CoopPlugin.Log.LogWarning("WarehouseBoxSync: client store ignored - no client id for the held box");
                Notice("couldn't store that box yet - try again");
                return false;
            }
            // Debounce a repeat click on the same box; if the request is dropped this self-heals.
            double now = Time.realtimeSinceStartupAsDouble;
            float at;
            if (self._pendingStoreAt.TryGetValue(boxId, out at) && now - at < 2f)
                return false;

            ushort shelfId;
            int shelfIdx, compIdx;
            if (!TryAddress(targetItemCompartment, out shelfId, out shelfIdx, out compIdx))
            {
                CoopPlugin.Log.LogWarning("WarehouseBoxSync: client store ignored - warehouse compartment address did not resolve");
                Notice("couldn't resolve that warehouse slot - try again");
                return false;
            }
            EItemType type;
            int amount;
            bool big;
            try
            {
                type = __instance.m_ItemCompartment.GetItemType();
                amount = __instance.m_ItemCompartment.GetItemCount();
                big = __instance.m_IsBigBox;
            }
            catch (Exception e) { Swallow.Log(e); return true; }
            if (amount <= 0)
                return true;
            self._pendingStoreAt[boxId] = (float)now;
            self.SendOp?.Invoke(new WarehouseOpMessage
            {
                Op = WarehouseOpMessage.OpStore,
                RequestId = self.NextRequest(),
                ShelfId = shelfId,
                ShelfIndex = shelfIdx,
                CompartmentIndex = compIdx,
                BoxId = boxId,
                ItemType = type,
                Amount = amount,
                IsBig = big,
            });
            return false;
        }

        /// <summary>Client: taking a warehouse record is forwarded to the host; popping locally
        /// would delete a record the host still owns and spawn an unstamped box.</summary>
        public static bool ClientTakePrefix(InteractableStorageCompartment __instance)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote || BoxShared.ApplyingRemote)
                return true;
            var self = _instance;
            if (self == null || __instance == null)
                return true;
            ShelfCompartment comp;
            try
            {
                comp = __instance.GetShelfCompartment();
            }
            catch (Exception e) { Swallow.Log(e); return true; }
            if (comp == null || !IsWarehouse(comp))
                return true;
            ushort shelfId;
            int shelfIdx, compIdx;
            if (!TryAddress(comp, out shelfId, out shelfIdx, out compIdx))
            {
                CoopPlugin.Log.LogWarning("WarehouseBoxSync: client take ignored - warehouse compartment address did not resolve");
                Notice("couldn't resolve that warehouse slot - try again");
                return false;
            }
            int key = (shelfIdx << 16) ^ (compIdx & 0xffff);
            if (!self._pendingTakeCompartments.Add(key))
                return false; // already awaiting a result for this compartment
            int req = self.NextRequest();
            self._takeKeyByRequest[req] = key;
            self._pendingTakeAt[req] = (float)Time.realtimeSinceStartupAsDouble;
            self.SendOp?.Invoke(new WarehouseOpMessage
            {
                Op = WarehouseOpMessage.OpTake,
                RequestId = req,
                ShelfId = shelfId,
                ShelfIndex = shelfIdx,
                CompartmentIndex = compIdx,
            });
            return false;
        }

        private int NextRequest()
        {
            int id = ++_nextRequestId;
            if (_nextRequestId == int.MaxValue)
                _nextRequestId = 0;
            return id;
        }

        /// <summary>Resolve a compartment's stable rack id and (shelf, compartment) indices.</summary>
        private static bool TryAddress(ShelfCompartment comp, out ushort shelfId,
            out int shelfIdx, out int compIdx)
        {
            shelfId = 0;
            shelfIdx = 0;
            compIdx = 0;
            if (comp == null)
                return false;
            try
            {
                var shelf = comp.GetWarehouseShelf();
                if (shelf != null)
                    PlacedObjectIdentity.TryGet(shelf, out shelfId);
                shelfIdx = comp.GetWarehouseIndex();
                compIdx = comp.GetIndex();
                return true;
            }
            catch (Exception e) { Swallow.Log(e); return false; }
        }

        private static bool IsWarehouse(ShelfCompartment comp)
        {
            try
            {
                return comp != null && comp.GetWarehouseShelf() != null;
            }
            catch (Exception e) { Swallow.Log(e); return false; }
        }

        private static void Notice(string line)
        {
            if (CoopCore.Instance == null)
                return;
            CoopCore.Instance.RegisterLine = line;
            CoopCore.Instance.RegisterLineTimer = 3f;
        }

        // ---------------- event hooks ----------------

        /// <summary>Host: a warehouse compartment just changed through vanilla code, so push the
        /// new authoritative state now. <c>ApplyingRemote</c> is checked so the client's own
        /// mirrored writes (which go through the same vanilla methods) never echo back, and
        /// <see cref="BroadcastNow"/> checks the role and that a session world is live.</summary>
        private static void NotifyWarehouseChanged()
        {
            var self = _instance;
            if (self == null || ApplyingRemote)
                return;
            self.BroadcastNow();
        }

        /// <summary>1.00 record model: a record was banked into a compartment.</summary>
        /// <summary>1.00 record model: a record was banked into a compartment. Also logs the
        /// resulting count and the call origin while BoxSyncDebug is on, because a store that
        /// banks TWO records prints here TWICE and the stack names whoever made the second call -
        /// that is the one datum the store logs could not provide.</summary>
        public static void AddStoredBoxRecordPostfix(ShelfCompartment __instance)
        {
            if (BoxShared.Debug && __instance != null)
            {
                try
                {
                    string origin = "?";
                    var st = new System.Diagnostics.StackTrace(1, false);
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < st.FrameCount && i < 6; i++)
                    {
                        var m = st.GetFrame(i) == null ? null : st.GetFrame(i).GetMethod();
                        if (m == null)
                            continue;
                        if (sb.Length > 0)
                            sb.Append(" < ");
                        sb.Append(m.DeclaringType == null ? "?" : m.DeclaringType.Name).Append('.').Append(m.Name);
                    }
                    if (sb.Length > 0)
                        origin = sb.ToString();
                    int shelfIdx = -1;
                    int compIdx = -1;
                    try
                    {
                        if (__instance.GetWarehouseShelf() != null)
                        {
                            shelfIdx = __instance.GetWarehouseIndex();
                            compIdx = __instance.GetIndex();
                        }
                    }
                    catch (Exception e) { Swallow.Log(e); }
                    BoxShared.DebugLog("record-add",
                        $"comp={shelfIdx}/{compIdx} count={RecordCount(__instance)} at={origin}");
                }
                catch (Exception e) { Swallow.Log(e); }
            }
            NotifyWarehouseChanged();
        }

        /// <summary>1.00 record model: a record was popped (only when the pop succeeded).</summary>
        public static void TryPopStoredBoxRecordPostfix(bool __result)
        {
            if (__result)
                NotifyWarehouseChanged();
        }

        /// <summary>Live-box model: any shelf gained a box, so filter to warehouse compartments.
        /// Only registered when the live backend owns warehouse storage.</summary>
        public static void AddBoxPostfix(ShelfCompartment __instance)
        {
            if (IsWarehouse(__instance))
                NotifyWarehouseChanged();
        }

        /// <summary>Live-box model: any shelf lost a box, so filter to warehouse compartments.</summary>
        public static void RemoveBoxPostfix(ShelfCompartment __instance)
        {
            if (IsWarehouse(__instance))
                NotifyWarehouseChanged();
        }

        // ---------------- host ----------------

        /// <summary>Host: broadcast the authoritative warehouse state right now. The wire is
        /// driven entirely by the game-side change events below (plus <see cref="ForceResend"/>
        /// on join and on a client's heal request), so there is no poll and no deferred flush: a
        /// guest's store/take is reflected as soon as vanilla has really changed the warehouse.
        /// Guarded because the hook can be invoked from inside vanilla game code.</summary>
        internal void BroadcastNow()
        {
            // Available() covers the build with neither backend (record storage present but
            // incomplete, and no live model): ForceResend() still lands here from the registry's
            // join/heal resend, and without the guard Compartments() would throw and an empty
            // state would be serialized.
            if (CoopCore.Role != CoopRole.Host || BroadcastState == null
                || !CoopCore.InSessionWorld || !Available())
                return;
            Guarded("broadcast", () => BroadcastState(BuildState()));
        }

        // ---------------- host ops ----------------

        public void HostApplyOp(WarehouseOpMessage message, int connId)
        {
            if (CoopCore.Role != CoopRole.Host || !Available() || message == null)
                return;
            long key = ((long)connId << 32) | (uint)message.RequestId;
            Guarded("op", () =>
            {
                // Bound the replay-dedup set; a full resync re-aligns anything dropped by a clear.
                if (_hostSeenRequests.Count > 8192)
                    _hostSeenRequests.Clear();
                if (!_hostSeenRequests.Add(key))
                    return; // duplicate/replayed request
                if (message.Op == WarehouseOpMessage.OpStore)
                    HostApplyStore(message, connId);
                else if (message.Op == WarehouseOpMessage.OpTake)
                    HostApplyTake(message, connId);
            });
            // No extra echo here: a successful mutation fires the record hook (AddStoredBoxRecord
            // / TryPopLastStoredBoxRecord) which broadcasts the new state, and a take always
            // answers with WarehouseTakeResultMessage. Echoing again here sent every accepted op
            // twice; a refused op changes nothing, so the requester stays consistent regardless.
        }

        private static void HostApplyStore(WarehouseOpMessage m, int connId)
        {
            var boxes = CoopCore.Instance != null ? CoopCore.Instance.Boxes : null;
            if (boxes == null)
                return;
            InteractablePackagingBox baseBox;
            if (!boxes.TryGetHostBox(m.BoxId, out baseBox))
                return;
            var box = baseBox as InteractablePackagingBox_Item;
            if (box == null || !boxes.HostBoxHeldByConnection(m.BoxId, connId))
                return; // only the connection actually holding the box may bank it
            var comp = ResolveCompartment(Compartments(), m.ShelfId, m.ShelfIndex, m.CompartmentIndex);
            if (comp == null)
                return;
            // A repeat click must not register the same object twice. On the live backend
            // DispenseItem appends to the compartment unconditionally, so a re-store would make
            // the rack report the box twice; the record backend is only protected because the box
            // is destroyed, which is not true here.
            if (box.m_IsStored)
            {
                CoopPlugin.Log.LogWarning($"WarehouseBoxSync: store ignored - box id {m.BoxId} is already stored");
                return;
            }
            try
            {
                if (box.m_IsBigBox != m.IsBig)
                    return;
                if ((int)box.m_ItemCompartment.GetItemType() != (int)m.ItemType)
                    return;
                if (box.m_ItemCompartment.GetItemCount() != m.Amount || m.Amount <= 0)
                    return;
            }
            catch (Exception e) { Swallow.Log(e); return; }
            try
            {
                int storedBefore = StoredCount(comp);
                BoxVisuals.SetVisible(box, true);
                BoxVisuals.EnsureOpenState(box, false);
                bool activeBefore = box.gameObject != null && box.gameObject.activeSelf;
                // Vanilla does the store: sets m_IsStored, assigns the compartment, and schedules
                // the data-only destroy whose OnDestroyed banks the record and retires the live
                // box (ForgetHostBox -> authoritative Removed). We never hand-add a record.
                box.DispenseItem(false, comp);
                int afterDispense = StoredCount(comp);
                if (BoxShared.Debug)
                {
                    BoxShared.DebugLog("box-store-step",
                        $"id={m.BoxId} afterDispense count={storedBefore}->{afterDispense} stored={box.m_IsStored}");
                }
                if (box.m_IsStored)
                {
                    // DispenseItem cannot have banked the record itself: it calls LerpToTransform
                    // at :202 but only sets m_MarkForDataOnlyDestroy at :210, and the bank is gated
                    // on that marker - so LerpToTransform's own OnDisable branch (which does call
                    // OnFinishLerp when the object is not active in hierarchy) runs BEFORE the
                    // marker exists. The count check below is therefore expected to be true on
                    // every forwarded store to a record host; it is kept because it is the
                    // condition under which forcing is correct, not because it is normally false.
                    //
                    // The forced invoke IS required, and not because of a race with Update: the
                    // guest still holds the box after a forwarded store (vanilla was suppressed),
                    // its next lease renewal can make the host's apply HIDE the box, and an
                    // inactive root never runs Update - so the lerp would never complete. Finishing
                    // the game's own sequence synchronously is the only deterministic path.
                    if (UsesRecords && _miOnFinishLerp != null && StoredCount(comp) == storedBefore)
                    {
                        // StopLerpToTransform BEFORE the invoke is load-bearing, not cosmetic: it
                        // clears m_IsLerpingToPos, which is the flag OnFinishLerp's natural callers
                        // (Update, and OnDisable) test. Without it, anything that deactivates the
                        // object during the invoke could re-enter OnFinishLerp while the destroy
                        // marker is still set and bank a second record. Do NOT remove it.
                        //
                        // The mark must NOT be cleared before the invoke - the record is only created
                        // under it - so it is cleared afterwards, defensively.
                        try
                        {
                            box.StopLerpToTransform();
                        }
                        catch (Exception e) { Swallow.Log(e); }
                        _miOnFinishLerp.Invoke(box, null);
                        if (BoxShared.Debug)
                        {
                            BoxShared.DebugLog("box-store-step",
                                $"id={m.BoxId} forcedFinishLerp count={afterDispense}->{StoredCount(comp)}");
                        }
                        try
                        {
                            _fiMarkForDataOnlyDestroy?.SetValue(box, false);
                        }
                        catch (Exception e) { Swallow.Log(e); }
                    }
                    else if (BoxShared.Debug)
                    {
                        BoxShared.DebugLog("box-store-step",
                            $"id={m.BoxId} gameFinishedStore count={storedBefore}->{StoredCount(comp)}");
                    }
                    // Restore the HOST's own view - but ONLY on the live backend, where the box
                    // itself is the stored representation. While the guest held it the host applied
                    // that Held claim and hid it (SetVisible(false) deactivates the root), and a
                    // forwarded store never runs vanilla on the guest, so the Free+Stored edge that
                    // would normally re-show it is never sent; left hidden, the rack looked empty
                    // and a later host take handed out an invisible box.
                    //
                    // On the RECORD backend the box is retired by the store (OnFinishLerp's
                    // data-only destroy) and the StoredBoxRecord owns the slot's visual. Re-showing
                    // it there left a second, physical box sitting in the rack next to the record's
                    // own - the duplicate a guest reported when IT put a box away. So hide it
                    // instead: the record is the representation now.
                    if (!UsesRecords)
                    {
                        try
                        {
                            BoxVisuals.SetVisible(box, true);
                            BoxVisuals.EnsureOpenState(box, false);
                        }
                        catch (Exception e) { Swallow.Log(e); }
                        if (!box.gameObject.activeSelf)
                            CoopPlugin.Log.LogWarning($"WarehouseBoxSync: stored box id {m.BoxId} is still inactive on the host");
                    }
                    else
                    {
                        try
                        {
                            BoxVisuals.SetVisible(box, false);
                        }
                        catch (Exception e) { Swallow.Log(e); }
                    }
                    int storedAfter = StoredCount(comp);
                    if (storedAfter != storedBefore + 1)
                    {
                        // Exactly one record, every time. Fewer means the game's sequence did not
                        // bank it; MORE means it banked twice - OnFinishLerp is not idempotent, and
                        // the box's own Update can re-enter it once the box is visible again, which
                        // showed up in game as one box bought and two on the rack (and the rack's
                        // own box-type gate then refusing the duplicate forever).
                        CoopPlugin.Log.LogError(
                            $"WarehouseBoxSync: stored box banked {storedAfter - storedBefore} records (expected 1) "
                            + $"id={m.BoxId} address=({m.ShelfId}/{m.ShelfIndex}/{m.CompartmentIndex}) "
                            + $"countBefore={storedBefore} countAfter={storedAfter}");
                    }
                    if (BoxShared.ShouldDebugLog(m.BoxId, 0.5f))
                    {
                        string mark = _fiMarkForDataOnlyDestroy == null
                            ? "unreadable"
                            : Convert.ToString(_fiMarkForDataOnlyDestroy.GetValue(box));
                        bool activeAfter = box.gameObject != null && box.gameObject.activeSelf;
                        BoxShared.DebugLog("box-store",
                            $"id={m.BoxId} address=({m.ShelfId}/{m.ShelfIndex}/{m.CompartmentIndex}) "
                            + $"activeSelf={activeBefore}->{activeAfter} stored={box.m_IsStored} "
                            + $"markForDataOnlyDestroy={mark} count={storedBefore}->{storedAfter}");
                    }
                    // On the live backend the box stays alive, so the guest that sent this store is
                    // still recorded as the lease owner and would keep holding it forever. Drop the
                    // lease so the next snapshot is Free+Stored and the guest yields (see
                    // BoxEngine's stored-while-held rule). On the record backend the box is
                    // destroyed and retired by the game within the frame, so this only marks a
                    // box that is about to vanish.
                    boxes.ReleaseLease(box);
                    boxes.MarkBoxDirty(box);
                    boxes.ForceNextTick();
                    CoopPlugin.Log.LogInfo($"WarehouseBoxSync: host stored box id {m.BoxId} for conn {connId}");
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("WarehouseBoxSync host store: " + e.Message); }
        }

        private static void HostApplyTake(WarehouseOpMessage m, int connId)
        {
            var comp = ResolveCompartment(Compartments(), m.ShelfId, m.ShelfIndex, m.CompartmentIndex);
            var boxes = CoopCore.Instance != null ? CoopCore.Instance.Boxes : null;
            if (comp == null || boxes == null)
            {
                SendTakeResult(connId, m, false, 0, 0);
                return;
            }
            int count = StoredCount(comp);
            if (count <= 0)
            {
                SendTakeResult(connId, m, false, 0, 0);
                return;
            }
            if (!UsesRecords)
            {
                HostApplyTakeLive(comp, m, connId);
                return;
            }
            try
            {
                // Canonical vanilla pop + visual rebuild + spawn, preserving last-record order.
                // PackageBoxCandidate and MaterializeStoredCandidate are both 1.00-only, so the
                // candidate is created and materialized by reflection (see Probe(), which requires
                // both for _records). Only storedCompartment is read by the vanilla method - it
                // recomputes the record index itself - so that is the only field we set.
                object candidate = Activator.CreateInstance(_tCandidate);
                _fiCandidateCompartment.SetValue(candidate, comp);
                var box = _miMaterialize.Invoke(null, new[] { candidate })
                    as InteractablePackagingBox_Item;
                if (box == null)
                {
                    SendTakeResult(connId, m, false, 0, count);
                    return;
                }
                // Make it a free live box so the store mirror does not re-bank it; the requesting
                // client auto-holds its authoritative mirror when the snapshot arrives.
                ItemBoxFamily.UnhookIfStored(box);
                ushort id = boxes.EnsureHostId(box);
                boxes.MarkBoxDirty(box);
                boxes.ForceNextTick();
                SendTakeResult(connId, m, true, id, StoredCount(comp));
                CoopPlugin.Log.LogInfo($"WarehouseBoxSync: host took a record for conn {connId} -> box id {id}");
            }
            catch (Exception e)
            {
                // MethodInfo.Invoke wraps a target throw in TargetInvocationException, which would
                // hide the real take failure - the exact path that needs debugging on a legacy
                // build. Log the cause, not the wrapper.
                var cause = (e as System.Reflection.TargetInvocationException)?.InnerException ?? e;
                CoopPlugin.Log.LogWarning("WarehouseBoxSync host take: " + cause);
                SendTakeResult(connId, m, false, 0, 0);
            }
        }

        /// <summary>Live backend take: pop the compartment's last live box and hand it to the box
        /// authority as a free box. The requesting client auto-holds that authoritative id when
        /// its mirror arrives, exactly like the record path.</summary>
        private static void HostApplyTakeLive(ShelfCompartment comp, WarehouseOpMessage m, int connId)
        {
            var boxes = CoopCore.Instance != null ? CoopCore.Instance.Boxes : null;
            if (boxes == null)
            {
                SendTakeResult(connId, m, false, 0, 0);
                return;
            }
            try
            {
                var last = comp.GetLastInteractablePackagingBox();
                if (last == null)
                {
                    SendTakeResult(connId, m, false, 0, 0);
                    return;
                }
                comp.RemoveBox(last);
                // The box is still flagged stored; unhook it so it becomes a free live box the
                // requester can hold instead of a box the store mirror re-banks.
                ItemBoxFamily.UnhookIfStored(last);
                ushort id = boxes.EnsureHostId(last);
                boxes.MarkBoxDirty(last);
                boxes.ForceNextTick();
                SendTakeResult(connId, m, true, id, StoredCount(comp));
                CoopPlugin.Log.LogInfo($"WarehouseBoxSync: host took a live warehouse box for conn {connId} -> box id {id}");
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("WarehouseBoxSync host take (live): " + e);
                SendTakeResult(connId, m, false, 0, 0);
            }
        }

        private static void SendTakeResult(int connId, WarehouseOpMessage m, bool accepted,
            ushort boxId, int remaining)
        {
            var self = _instance;
            if (self == null || self.SendToClient == null)
                return;
            self.SendToClient(connId, new WarehouseTakeResultMessage
            {
                RequestId = m.RequestId,
                ShelfId = m.ShelfId,
                ShelfIndex = m.ShelfIndex,
                CompartmentIndex = m.CompartmentIndex,
                Accepted = accepted,
                BoxId = boxId,
                RemainingRecords = remaining,
            });
        }

        /// <summary>The warehouse compartments the active backend owns, in shelf order. The
        /// record backend asks RestockManager; the live backend walks the warehouse shelves and
        /// takes each storage compartment's ShelfCompartment.</summary>
        private static List<ShelfCompartment> Compartments()
        {
            if (!UsesRecords)
                return LiveCompartments();
            try
            {
                return _miCompartments.Invoke(null, null) as List<ShelfCompartment>;
            }
            catch (Exception e) { Swallow.Log(e); return null; }
        }

        private static List<ShelfCompartment> LiveCompartments()
        {
            var list = new List<ShelfCompartment>();
            try
            {
                var sm = SceneRef<ShelfManager>.Get();
                var shelves = sm == null ? null : sm.m_WarehouseShelfList;
                if (shelves == null)
                    return list;
                for (int i = 0; i < shelves.Count; i++)
                {
                    var shelf = shelves[i];
                    if (shelf == null)
                        continue;
                    var storages = shelf.GetStorageCompartmentList();
                    if (storages == null)
                        continue;
                    for (int j = 0; j < storages.Count; j++)
                    {
                        var sc = storages[j];
                        if (sc == null)
                            continue;
                        var comp = sc.GetShelfCompartment();
                        if (comp != null)
                            list.Add(comp);
                    }
                }
            }
            catch (Exception e) { Swallow.Log(e); }
            return list;
        }

        /// <summary>How many stored boxes the active backend holds for a compartment. Used for the
        /// take guard and the reply's remaining count, so it must count exactly what
        /// <see cref="ReadStored"/> would emit (non-null, non-empty entries).</summary>
        private static int StoredCount(ShelfCompartment comp)
        {
            if (UsesRecords)
                return RecordCount(comp);
            try
            {
                var boxes = comp.GetInteractablePackagingBoxList();
                if (boxes == null)
                    return 0;
                int n = 0;
                for (int i = 0; i < boxes.Count; i++)
                {
                    var b = boxes[i];
                    if (b == null || b.m_ItemCompartment == null || b.m_ItemCompartment.GetItemCount() <= 0)
                        continue;
                    n++;
                }
                return n;
            }
            catch (Exception e) { Swallow.Log(e); return 0; }
        }

        /// <summary>Append the active backend's stored boxes for one compartment, in extraction
        /// order (both models pop the LAST entry, so order is wire-significant).</summary>
        private static void ReadStored(ShelfCompartment comp, List<StoredBoxEntry> into)
        {
            if (UsesRecords)
            {
                int n = RecordCount(comp);
                for (int i = 0; i < n; i++)
                {
                    int type, amount;
                    bool big;
                    if (!TryPeek(comp, i, out type, out amount, out big))
                        continue;
                    into.Add(new StoredBoxEntry { ItemType = (EItemType)type, Amount = amount, Big = big });
                }
                return;
            }
            try
            {
                var boxes = comp.GetInteractablePackagingBoxList();
                if (boxes == null)
                    return;
                for (int i = 0; i < boxes.Count; i++)
                {
                    var box = boxes[i];
                    if (box == null || box.m_ItemCompartment == null)
                        continue;
                    int amount = box.m_ItemCompartment.GetItemCount();
                    if (amount <= 0)
                        continue;
                    into.Add(new StoredBoxEntry
                    {
                        ItemType = box.m_ItemCompartment.GetItemType(),
                        Amount = amount,
                        Big = box.m_IsBigBox,
                    });
                }
            }
            catch (Exception e) { Swallow.Log(e); }
        }

        private static int RecordCount(ShelfCompartment comp)
        {
            try
            {
                return (int)_miCount.Invoke(comp, null);
            }
            catch (Exception e) { Swallow.Log(e); return 0; }
        }

        private static bool TryPeek(ShelfCompartment comp, int i,
            out int itemType, out int amount, out bool big)
        {
            itemType = 0;
            amount = 0;
            big = false;
            try
            {
                object rec = _miPeek.Invoke(comp, new object[] { i });
                if (rec == null)
                    return false;
                itemType = Convert.ToInt32(_fiItemType.GetValue(rec));
                amount = (int)_fiAmount.GetValue(rec);
                big = (bool)_fiBig.GetValue(rec);
                return true;
            }
            catch (Exception e) { Swallow.Log(e); return false; }
        }


        private static WarehouseStateMessage BuildState()
        {
            var msg = new WarehouseStateMessage { Full = true, HostLiveBoxes = !UsesRecords };
            var comps = Compartments();
            if (comps == null)
                return msg;
            for (int c = 0; c < comps.Count; c++)
            {
                var comp = comps[c];
                if (!IsWarehouse(comp))
                    continue;
                var entry = new WarehouseCompartmentEntry
                {
                    ShelfIndex = comp.GetWarehouseIndex(),
                    CompartmentIndex = comp.GetIndex(),
                };
                try
                {
                    var shelf = comp.GetWarehouseShelf();
                    if (shelf != null)
                        entry.ShelfId = PlacedObjectIdentity.AssignHost(shelf);
                }
                catch (Exception e) { Swallow.Log(e); }
                ReadStored(comp, entry.Records);
                msg.Compartments.Add(entry);
            }
            return msg;
        }

        // ---------------- client ----------------

        public void ClientApplyState(WarehouseStateMessage message)
        {
            if (!Available() || message == null || message.Compartments == null)
                return;
            _hostLiveBoxes = message.HostLiveBoxes;
            _hasHostState = true;
            // A live-box host's racks are real stored objects the box channel already
            // synchronises: rewriting or materialising them here would give rack state two owners
            // (see ApplyPatches). A record host has no live boxes to deliver, so a live guest
            // materializes the entries instead (ApplyCompartmentFromRecords).
            if (!UsesRecords && _hostLiveBoxes)
                return;
            ApplyingRemote = true;
            try
            {
                Guarded("apply", () => ClientApplyInner(message));
            }
            finally { ApplyingRemote = false; }
        }

        /// <summary>Applies a host state. A compartment whose rack has not streamed in yet is
        /// simply skipped this time - the host's sweep re-sends every live compartment on its
        /// next pass, so nothing needs to be remembered or retried here.</summary>
        private void ClientApplyInner(WarehouseStateMessage message)
        {
            var comps = Compartments();
            if (comps == null)
                return;
            for (int e = 0; e < message.Compartments.Count; e++)
            {
                var entry = message.Compartments[e];
                if (entry == null)
                    continue;
                var comp = ResolveCompartment(comps, entry);
                if (comp == null)
                    continue;
                // One attempt per message: if the rack resolved but refused its entries
                // (capacity/size/type) that is enough to know they do not fit. The next sweep
                // slice or a rejoin re-sends the compartment from scratch.
                ApplyCompartment(comp, entry);
            }
        }

        private static ShelfCompartment ResolveCompartment(List<ShelfCompartment> comps,
            WarehouseCompartmentEntry entry)
        {
            return entry == null
                ? null
                : ResolveCompartment(comps, entry.ShelfId, entry.ShelfIndex, entry.CompartmentIndex);
        }

        private static ShelfCompartment ResolveCompartment(List<ShelfCompartment> comps,
            ushort shelfId, int shelfIdx, int compIdx)
        {
            if (comps == null)
                return null;
            // Stable shelf identity first: ShelfManager re-indexes racks when a shelf is added or
            // removed, so the host's (shelf, compartment) indices can transiently point at the
            // wrong rack. Fall back to indices when identity is unavailable (unbound client).
            if (shelfId != 0)
            {
                for (int i = 0; i < comps.Count; i++)
                {
                    var comp = comps[i];
                    if (comp == null)
                        continue;
                    try
                    {
                        var shelf = comp.GetWarehouseShelf();
                        if (shelf != null && PlacedObjectIdentity.TryGet(shelf, out ushort id)
                            && id == shelfId
                            && comp.GetIndex() == compIdx)
                            return comp;
                    }
                    catch (Exception e) { Swallow.Log(e); }
                }
            }
            for (int i = 0; i < comps.Count; i++)
            {
                var comp = comps[i];
                if (comp == null)
                    continue;
                try
                {
                    if (comp.GetWarehouseIndex() == shelfIdx
                        && comp.GetIndex() == compIdx)
                        return comp;
                }
                catch (Exception e) { Swallow.Log(e); }
            }
            return null;
        }

        /// <summary>Applies one compartment. Returns false when it resolved but failed, so the caller
        /// keeps it pending and retries (the failure log is rate-limited by ModuleGuard).</summary>
        private static bool ApplyCompartment(ShelfCompartment comp, WarehouseCompartmentEntry entry)
        {
            // Live guest of a record host: the entries have no live counterpart anywhere, so
            // materialize them. ClientApplyState returns before reaching here for a live host
            // (the box channel already owns those racks).
            if (!UsesRecords)
                return ApplyCompartmentFromRecords(comp, entry);
            try
            {
                // Replace the list wholesale: the host's order is authoritative (extraction pops
                // the LAST record, so order decides which box comes out next).
                while (RecordCount(comp) > 0)
                {
                    object[] args = { null };
                    bool popped = (bool)_miPop.Invoke(comp, args);
                    if (!popped)
                        break;
                }
                for (int i = 0; i < entry.Records.Count; i++)
                {
                    var r = entry.Records[i];
                    // The wire converter maps a host-only modded id the client cannot resolve to
                    // EItemType.None; with no EItemType table it may pass the raw host id through.
                    // Skip anything not a defined local type (the policy every other item
                    // boundary uses) rather than materializing a phantom blank box.
                    if (r.ItemType == EItemType.None || r.Amount <= 0
                        || !Enum.IsDefined(typeof(EItemType), r.ItemType))
                        continue;
                    object rec = Activator.CreateInstance(_tRecord);
                    _fiItemType.SetValue(rec, Enum.ToObject(_tEnumType, (int)r.ItemType));
                    _fiAmount.SetValue(rec, r.Amount);
                    _fiBig.SetValue(rec, r.Big);
                    _miAdd.Invoke(comp, new[] { rec });
                }
                Rebuild(comp);
                return true;
            }
            catch (Exception e) { ModuleGuard.Log("warehouse:apply", e); return false; }
        }

        /// <summary>Live guest realizing a record host's entries: replace the compartment's racks
        /// with live boxes. Only reached when the host is record backed, so the box channel has no
        /// warehouse boxes of its own here - every box in these racks was spawned by this method and
        /// is ours to destroy, which keeps it from lingering as an unbound ghost (the box engine
        /// never adopts a box with no host id).</summary>
        /// <summary>True when the compartment's live boxes already are exactly the wanted records,
        /// so a re-apply can be skipped entirely.
        ///
        /// This matters because the apply below is a teardown+respawn: it destroys every box in
        /// the rack and spawns fresh ones. Two things re-run it constantly on a live guest of a
        /// record host - the unconditional slice sweep (every compartment is re-asserted once per
        /// pass) and <see cref="OnClientTick"/>'s retry of the last state - so without this the
        /// rack's boxes are destroyed and recreated over and over, and each new box runs
        /// <c>DispenseItem</c>'s lerp. That is what a guest sees as the boxes repeatedly flying
        /// back into the shelf from a wrong position.
        ///
        /// Compared as a multiset of (item type, amount, big): order in the rack is the game's own
        /// arrangement and is not something a re-apply should be forcing.</summary>
        private static bool CompartmentMatchesRecords(ShelfCompartment comp, WarehouseCompartmentEntry entry)
        {
            try
            {
                var have = new List<long>();
                var boxes = comp.GetInteractablePackagingBoxList();
                if (boxes != null)
                {
                    for (int i = 0; i < boxes.Count; i++)
                    {
                        var b = boxes[i];
                        if (b == null)
                            continue;
                        have.Add(RecordKey(
                            (int)b.m_ItemCompartment.GetItemType(),
                            b.m_ItemCompartment.GetItemCount(),
                            b.m_IsBigBox));
                    }
                }
                var want = new List<long>(entry.Records.Count);
                for (int i = 0; i < entry.Records.Count; i++)
                {
                    var r = entry.Records[i];
                    // Same filter the spawn loop uses, so a record it skips is not counted as wanted.
                    if (r.ItemType == EItemType.None || r.Amount <= 0
                        || !Enum.IsDefined(typeof(EItemType), r.ItemType))
                        continue;
                    want.Add(RecordKey((int)r.ItemType, r.Amount, r.Big));
                }
                if (have.Count != want.Count)
                    return false;
                have.Sort();
                want.Sort();
                for (int i = 0; i < have.Count; i++)
                {
                    if (have[i] != want[i])
                        return false;
                }
                return true;
            }
            catch (Exception e) { Swallow.Log(e); return false; }
        }

        private static long RecordKey(int itemType, int amount, bool big)
        {
            return ((long)itemType << 40) | ((long)amount << 8) | (big ? 1L : 0L);
        }

        private static bool ApplyCompartmentFromRecords(ShelfCompartment comp, WarehouseCompartmentEntry entry)
        {
            // Leave a matching rack completely alone - see CompartmentMatchesRecords.
            if (CompartmentMatchesRecords(comp, entry))
                return true;
            bool ok = true;
            // Mark the whole teardown+spawn as a remote apply: DestroyOwned must not be mistaken for
            // a local gameplay destroy (that emits a spurious Removed for a box the host still
            // owns) and the mirror DispenseItem must not be mistaken for a player store.
            bool prevApplying = BoxShared.ApplyingRemote;
            BoxShared.ApplyingRemote = true;
            try
            {
                var boxes = comp.GetInteractablePackagingBoxList();
                if (boxes != null)
                {
                    for (int i = boxes.Count - 1; i >= 0; i--)
                    {
                        var b = boxes[i];
                        if (b == null)
                            continue;
                        // Ours, not the box channel's: tear it down through the game's own
                        // de-registration (OnDestroyed) rather than a bare Destroy.
                        ItemBoxFamily.DestroyOwned(b);
                    }
                }
                for (int i = 0; i < entry.Records.Count; i++)
                {
                    var r = entry.Records[i];
                    if (r.ItemType == EItemType.None || r.Amount <= 0
                        || !Enum.IsDefined(typeof(EItemType), r.ItemType))
                        continue;
                    var box = RestockManager.SpawnPackageBoxItem(r.ItemType, r.Amount, r.Big);
                    if (box == null)
                    {
                        ok = false;
                        continue;
                    }
                    // Physics off BEFORE the store, exactly as the game's own 1.0 restore does
                    // (ShelfManager) and as ItemBoxFamily.ApplyStored does. DispenseItem only lerps
                    // for ~0.33 s and then nothing pins the transform, so a dynamic box would drop
                    // out of the rack - and its live collider would let the player grab a box the
                    // host still considers stored.
                    BoxLifecycle.ApplyEnabled(box, false);
                    box.DispenseItem(false, comp);
                    if (!box.m_IsStored)
                    {
                        WarnMaterializeRefused(r.ItemType, r.Amount, r.Big);
                        ItemBoxFamily.DestroyOwned(box);
                        ok = false;
                    }
                }
            }
            catch (Exception e) { ModuleGuard.Log("warehouse:apply-live", e); return false; }
            finally { BoxShared.ApplyingRemote = prevApplying; }
            return ok;
        }

        /// <summary>The rack refused a materialised box. Rate-limited: a stuck compartment is
        /// re-applied up to the pending cap (600 passes), so an unthrottled line would print 600
        /// times.</summary>
        private static void WarnMaterializeRefused(EItemType type, int amount, bool big)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (now - _lastMaterializeWarn < 5.0)
                return;
            _lastMaterializeWarn = now;
            CoopPlugin.Log.LogWarning(
                $"WarehouseBoxSync: rack refused a materialised box ({type} x{amount}, big={big}) - dropped, will retry");
        }

        // ---------------- client take result ----------------

        public void ClientApplyTakeResult(WarehouseTakeResultMessage message)
        {
            if (!Available() || message == null)
                return;
            _pendingTakeAt.Remove(message.RequestId);
            int key;
            if (_takeKeyByRequest.TryGetValue(message.RequestId, out key))
            {
                _takeKeyByRequest.Remove(message.RequestId);
                _pendingTakeCompartments.Remove(key);
            }
            if (!message.Accepted || message.BoxId == 0)
                return;
            _pendingTakeBoxes.Add(message.BoxId);
            var boxes = CoopCore.Instance != null ? CoopCore.Instance.Boxes : null;
            InteractablePackagingBox box;
            if (boxes != null && boxes.TryGetClientBox(message.BoxId, out box))
                TryAutoHoldTakenBox(box);
        }

        /// <summary>Client: hold the authoritative box this client asked to take, once its mirror
        /// exists. Wired from CoopCore's box-spawn callback and from ClientApplyTakeResult. Unlike
        /// the empty-box-storage handoff, the exact accepted BoxId is required - never a
        /// position/type guess.</summary>
        public void TryAutoHoldTakenBox(InteractablePackagingBox box)
        {
            var item = box as InteractablePackagingBox_Item;
            if (CoopCore.Role != CoopRole.Client || item == null)
                return;
            var boxes = CoopCore.Instance != null ? CoopCore.Instance.Boxes : null;
            if (boxes == null)
                return;
            ushort id;
            if (!boxes.TryGetClientId(box, out id) || !_pendingTakeBoxes.Contains(id))
                return;
            // The host has already removed this box from its rack and answered with the id, and a
            // live host's rack box arrives as a real stored object. Detach our own copy from the
            // rack before it goes to the hand - otherwise the local compartment keeps the slot and
            // its amount, because the box channel skips a Free apply for a box we now hold. (On the
            // record backend the accepted box is a fresh spawn, so this is a no-op.)
            ItemBoxFamily.UnhookIfStored(item);
            if (HoldClientBox != null && HoldClientBox(item))
                _pendingTakeBoxes.Remove(id);
        }

        private static void Rebuild(ShelfCompartment comp)
        {
            try
            {
                _miRebuild?.Invoke(null, new object[] { comp });
            }
            catch (Exception e) { Swallow.Log(e); }
        }
    }
}
