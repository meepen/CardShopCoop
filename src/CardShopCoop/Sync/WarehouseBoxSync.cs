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
    /// Game 1.0 replaced warehouse-stored boxes with serialized <c>StoredBoxRecord</c>s owned by
    /// a <see cref="ShelfCompartment"/> and DESTROYS the live <c>InteractablePackagingBox_Item</c>.
    /// A stored box is therefore no longer enumerable by the box engine, so it needs its own
    /// authoritative channel: the host owns the record lists and clients mirror them. Client
    /// store/take are forwarded as <see cref="WarehouseOpMessage"/> requests; the host validates
    /// and runs vanilla, then the authoritative warehouse/box state is the echo.
    ///
    /// Everything 1.0-specific is resolved by reflection, so a build without the record API
    /// (0.70.3 / any stripped build) simply leaves the module inert and the old live stored-box
    /// path in <see cref="BoxEngine"/> keeps working.
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

        private readonly SnapshotGate _gate = new SnapshotGate(1.5f, 12f, -2.3f);

        // request bookkeeping
        private int _nextRequestId;
        private readonly Dictionary<ushort, float> _pendingStoreAt = new Dictionary<ushort, float>();
        private readonly Dictionary<int, int> _takeKeyByRequest = new Dictionary<int, int>();
        private readonly HashSet<int> _pendingTakeCompartments = new HashSet<int>();
        private readonly Dictionary<int, float> _pendingTakeAt = new Dictionary<int, float>();
        private readonly HashSet<ushort> _pendingTakeBoxes = new HashSet<ushort>();
        private readonly HashSet<long> _hostSeenRequests = new HashSet<long>();

        // ---- 1.0 record API (optional) ----
        private static bool _probed;
        private static bool _available;
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

        public WarehouseBoxSync()
        {
            _instance = this;
        }

        public override string Name => "warehouse";

        public override void Start() => _instance = this;

        protected override void OnHostTick(in SyncFrame frame) => HostTick(frame.Dt, frame.InGame);

        /// <summary>Client: expire warehouse take requests that never got a result (a dropped
        /// frame or a host-side exception), so a compartment cannot stay un-clickable forever.</summary>
        protected override void OnClientTick(in SyncFrame frame)
        {
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
            _gate.Reset(-2.3f);
            _nextRequestId = 0;
            _pendingStoreAt.Clear();
            _takeKeyByRequest.Clear();
            _pendingTakeCompartments.Clear();
            _pendingTakeAt.Clear();
            _pendingTakeBoxes.Clear();
            _hostSeenRequests.Clear();
        }

        public override void ForceResend() => _gate.Force();

        public override void Dispose()
        {
            base.Dispose();
            if (ReferenceEquals(_instance, this))
                _instance = null;
            ApplyingRemote = false;
        }

        // ---------------- capability probe ----------------

        /// <summary>True only when the running game exposes the 1.0 stored-box-record API.</summary>
        internal static bool Available()
        {
            Probe();
            return _available;
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
                var tBatcher = AccessTools.TypeByName("StoredBoxVisualBatcher");
                _miRebuild = tBatcher == null ? null : AccessTools.Method(tBatcher, "RebuildImmediate");
                if (_tRecord != null)
                {
                    _fiItemType = AccessTools.Field(_tRecord, "itemType");
                    _fiAmount = AccessTools.Field(_tRecord, "amount");
                    _fiBig = AccessTools.Field(_tRecord, "isBigBox");
                    _tEnumType = _fiItemType == null ? null : _fiItemType.FieldType;
                }
                _available = _tRecord != null && _miCount != null && _miPeek != null
                    && _miPop != null && _miAdd != null && _miCompartments != null
                    && _fiItemType != null && _fiAmount != null && _fiBig != null;
            }
            catch (Exception e) { Swallow.Log(e); }
            CoopPlugin.Log.LogInfo(_available
                ? "WarehouseBoxSync: 1.0 stored-box records present - warehouse storage is host-authoritative"
                : "WarehouseBoxSync: stored-box record API absent - live stored-box path unchanged");
        }

        // ---------------- patches ----------------

        public static void ApplyPatches(Harmony h)
        {
            if (!Available())
                return;
            // A client must not bank a warehouse record locally: the host owns the record list,
            // and a local DispenseItem would both create a record the host never sees and drive
            // the 1.0 data-only destroy (a spurious box Removed). Gate store host-only.
            Try(h, typeof(InteractablePackagingBox_Item), "DispenseItem",
                prefix: new HarmonyMethod(typeof(WarehouseBoxSync), nameof(ClientStorePrefix)));
            // Same for taking a record out: popping locally would delete a record the host still
            // owns (its next snapshot restores it) and spawn an unstamped box. Host-only.
            Try(h, typeof(InteractableStorageCompartment), "OnMouseButtonUp",
                prefix: new HarmonyMethod(typeof(WarehouseBoxSync), nameof(ClientTakePrefix)));
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
            ShelfCompartment targetItemCompartment)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote)
                return true;
            if (!IsWarehouse(targetItemCompartment))
                return true;
            var self = _instance;
            var boxes = CoopCore.Instance != null ? CoopCore.Instance.Boxes : null;
            if (self == null || boxes == null || __instance == null)
            {
                Notice("cannot store this box yet - try again");
                return false;
            }
            ushort boxId;
            if (!boxes.TryGetClientId(__instance, out boxId))
            {
                Notice("cannot store this box yet - try again");
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
                Notice("cannot resolve that warehouse slot");
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
            Notice("storing box...");
            return false;
        }

        /// <summary>Client: taking a warehouse record is forwarded to the host; popping locally
        /// would delete a record the host still owns and spawn an unstamped box.</summary>
        public static bool ClientTakePrefix(InteractableStorageCompartment __instance)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote)
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
                Notice("cannot resolve that warehouse slot");
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
            Notice("taking box...");
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

        // ---------------- host ----------------

        public void HostTick(float dt, bool inGame)
        {
            if (!inGame || BroadcastState == null || !Available())
                return;
            if (!_gate.Due(dt))
                return;
            Guarded("host", () =>
            {
                int hash = ComputeHash();
                if (!_gate.ShouldSend(hash))
                    return;
                BroadcastState(BuildState());
            });
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
            ForceResend(); // echo promptly even if refused (re-aligns the requester)
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
                // Vanilla does the store: sets m_IsStored, assigns the compartment, and schedules
                // the data-only destroy whose OnDestroyed banks the record and retires the live
                // box (ForgetHostBox -> authoritative Removed). We never hand-add a record.
                box.DispenseItem(false, comp);
                if (box.m_IsStored)
                {
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
            int count = RecordCount(comp);
            if (count <= 0)
            {
                SendTakeResult(connId, m, false, 0, 0);
                return;
            }
            try
            {
                // Canonical vanilla pop + visual rebuild + spawn, preserving last-record order.
                var candidate = new PackageBoxCandidate
                {
                    storedCompartment = comp,
                    storedRecordIndex = count - 1,
                };
                var box = RestockManager.MaterializeStoredCandidate(candidate);
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
                SendTakeResult(connId, m, true, id, RecordCount(comp));
                CoopPlugin.Log.LogInfo($"WarehouseBoxSync: host took a record for conn {connId} -> box id {id}");
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("WarehouseBoxSync host take: " + e.Message);
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

        private static List<ShelfCompartment> Compartments()
        {
            try
            {
                return _miCompartments.Invoke(null, null) as List<ShelfCompartment>;
            }
            catch (Exception e) { Swallow.Log(e); return null; }
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

        private static bool Usable(ShelfCompartment comp)
        {
            if (comp == null)
                return false;
            try
            {
                // GetWarehouseIndex()/GetIndex() dereference the owning WarehouseShelf, so a
                // compartment whose shelf is gone must be skipped rather than throw.
                return comp.GetWarehouseShelf() != null;
            }
            catch (Exception e) { Swallow.Log(e); return false; }
        }

        private static int ComputeHash()
        {
            int h = 17;
            var comps = Compartments();
            if (comps == null)
                return h;
            for (int c = 0; c < comps.Count; c++)
            {
                var comp = comps[c];
                if (!Usable(comp))
                    continue;
                h = h * 31 + comp.GetWarehouseIndex();
                h = h * 31 + comp.GetIndex();
                int n = RecordCount(comp);
                h = h * 31 + n;
                for (int i = 0; i < n; i++)
                {
                    int type, amount;
                    bool big;
                    if (!TryPeek(comp, i, out type, out amount, out big))
                        continue;
                    h = h * 31 + type;
                    h = h * 31 + amount;
                    h = h * 31 + (big ? 1 : 0);
                }
            }
            return h;
        }

        private static WarehouseStateMessage BuildState()
        {
            var msg = new WarehouseStateMessage { Full = true };
            var comps = Compartments();
            if (comps == null)
                return msg;
            for (int c = 0; c < comps.Count; c++)
            {
                var comp = comps[c];
                if (!Usable(comp))
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
                int n = RecordCount(comp);
                for (int i = 0; i < n; i++)
                {
                    int type, amount;
                    bool big;
                    if (!TryPeek(comp, i, out type, out amount, out big))
                        continue;
                    entry.Records.Add(new StoredBoxEntry { ItemType = (EItemType)type, Amount = amount, Big = big });
                }
                msg.Compartments.Add(entry);
            }
            return msg;
        }

        // ---------------- client ----------------

        public void ClientApplyState(WarehouseStateMessage message)
        {
            if (!Available() || message == null || message.Compartments == null)
                return;
            ApplyingRemote = true;
            try
            {
                Guarded("apply", () => ClientApplyInner(message));
            }
            finally { ApplyingRemote = false; }
        }

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
                    continue; // rack not streamed in yet; the next heal retries
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

        private static void ApplyCompartment(ShelfCompartment comp, WarehouseCompartmentEntry entry)
        {
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
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("WarehouseBoxSync apply: " + e.Message); }
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
