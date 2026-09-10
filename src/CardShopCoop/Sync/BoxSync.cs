using CardShopCoop.Util;
using CardShopCoop.Net;
using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Mirrors loose delivery/packaging boxes (item boxes) between host and client.
    /// The host's RestockManager list is the single source of truth, broadcast every
    /// 1.5s; the client reconciles its own live list to match, spawning via the
    /// game's own RestockManager.SpawnPackageBoxItem (the exact save-load recipe) and
    /// despawning via the box's own OnDestroyed. Client-side changes (dispensing to a
    /// shelf, carrying, trashing) are detected against the last applied state and sent
    /// as requests the host applies and echoes. The joiner's restock ORDERS are forwarded
    /// separately (GamePatches) so deliveries always spawn host-side, officially.
    ///
    /// Every box carries a host-assigned STABLE ID. Identity-by-list-index looked fine
    /// until any single removal shifted every later index: the client's reconcile then
    /// destroyed/respawned the whole shifted tail - including the box in a player's
    /// HANDS (second field report). IDs make removals surgical.
    /// </summary>
    public class BoxSync
    {
        /// <summary>The live box mirror. ContainerSync uses the same stable-id maps for
        /// empty-box storage hand-offs and atomic store-back.</summary>
        public static BoxSync Instance;

        public struct Entry
        {
            public ushort Id;    // host-assigned, stable for the box's lifetime
            public int Type;
            public int Count;
            public bool IsBig;
            public bool IsOpen;
            public bool Carried; // in someone's hands: position is transient, don't apply
            public bool Settled; // physics at rest: only settled poses are applied
            public bool Stored;  // on a warehouse rack slot: the compartment owns pose+contents
            public ushort StoreShelfId; // host-assigned WarehouseShelf identity, when available
            public byte StoreShelf; // WarehouseShelf.GetIndex() while stored
            public byte StoreComp;  // compartment index within that shelf while stored
            public Vector3 Pos;
            public float Yaw;
            public short HolderWorker; // -1 when not held by a worker
            // 0=free, 1=host/local player, 2=remote client, 3=worker.
            // Carried alone is insufficient during the transient throw/drop window.
            public byte OwnerKind;
            // A released box can still be in flight, or can be in the player's
            // placement preview. These are distinct from Carried: neither state
            // should be reconciled as a settled loose-box pose.
            public bool InFlight;
            public bool Moving;
            public Vector3 Velocity;
            public Vector3 AngularVelocity;
            public bool Owned;
            public int OwnerId;
            /// <summary>NOT a wire field - set by ReadEntries when Type came off the wire as a
            /// modded id with no counterpart on this PC (a content pack only the sender has).
            /// Type is then EItemType.None, which is indistinguishable from a legitimately
            /// empty box's compartment type, so the flag is the only way to tell them apart.</summary>
            public bool Unmapped;
        }

        /// <summary>Set by CoopCore: is this box currently in the LOCAL player's hands?</summary>
        public static Func<InteractablePackagingBox_Item, bool> IsLocallyCarried = _ => false;

        private static readonly System.Reflection.MethodInfo MiSetOpenClose =
            ReflectionSurface.RequiredMethod(typeof(InteractablePackagingBox_Item), "SetOpenCloseBox");
        private static readonly System.Reflection.FieldInfo FiAmountToSpawn =
            ReflectionSurface.RequiredField(typeof(InteractablePackagingBox_Item), "m_ItemAmountToSpawn");
        private static readonly System.Reflection.FieldInfo FiStoredList =
            ReflectionSurface.RequiredField(typeof(ShelfCompartment), "m_StoredItemList");
        // protected on InteractableObject; true while ANY holder (player or WORKER)
        // carries the box - the player-only IsLocallyCarried guard left worker-held
        // boxes unprotected, so guest reports teleported boxes out of the restocker's
        // hands and broke its bring-boxes-inside loop (field report)
        private static readonly System.Reflection.FieldInfo FiBeingHold =
            ReflectionSurface.RequiredField(typeof(InteractableObject), "m_IsBeingHold");
        private static readonly System.Reflection.FieldInfo FiWorkerHoldBox =
            ReflectionSurface.RequiredField(typeof(Worker), "m_CurrentHoldItemBox");
        // private on InteractablePackagingBox_Item (NOT InteractableObject): gates the
        // worker restock candidate filters via CanWorkerTakeBox() (= !m_PreventWorkerTakeBox,
        // decompiled InteractablePackagingBox_Item ~337-340). The worker filters
        // (Worker.cs ~567/580/1023...) check only IsValidObject()+CanWorkerTakeBox(),
        // never activeSelf, so a box a GUEST is carrying (hidden but not worker-locked)
        // is still a valid restock candidate and a worker will drain/take it out of the
        // guest's hands (field report). We flip this true while a box id is in
        // _remoteCarried and clear it when the id leaves. NOT m_IsBeingHold: that field
        // drives IsValidObject / hold state on other host paths and stomping it here
        // would fight them.
        private static readonly System.Reflection.FieldInfo FiPreventWorkerTake =
            ReflectionSurface.RequiredField(typeof(InteractablePackagingBox_Item), "m_PreventWorkerTakeBox");

        // Remote boxes do not run a second networked physics simulation.  They glide from
        // their last known pose to the host-confirmed pose, then are committed atomically.
        // Keeping this shared by item/card/furniture boxes is important: otherwise each box
        // family develops a different prediction/reconciliation rule.
        private sealed class RemoteMotion
        {
            public Vector3 From;
            public Vector3 To;
            public float FromYaw;
            public float ToYaw;
            public float Age;
            public float Duration;
        }

        private static readonly Dictionary<InteractablePackagingBox, RemoteMotion> RemoteMotions
            = new Dictionary<InteractablePackagingBox, RemoteMotion>();

        public static void ResetRemoteMotions()
        {
            RemoteMotions.Clear();
        }

        public static bool IsRemoteMotion(InteractablePackagingBox box)
        {
            return box != null && RemoteMotions.ContainsKey(box);
        }

        public static void CancelRemoteMotion(InteractablePackagingBox box)
        {
            if (box != null)
                RemoteMotions.Remove(box);
        }

        /// <summary>Schedule a visual-only reconciliation to an authoritative host pose.
        /// Carrying clients remain predicted locally; callers must invoke this only after the
        /// host has said the box is visible/not carried.</summary>
        public static void ScheduleRemoteMotion(InteractablePackagingBox box, Vector3 position, float yaw)
        {
            if (CoopCore.Role != CoopRole.Client || box == null)
                return;
            var from = PhysicsPosition(box);
            var fromYaw = PhysicsRotation(box).eulerAngles.y;
            float distance = Vector3.Distance(from, position);
            if (distance < 0.05f && Mathf.Abs(Mathf.DeltaAngle(fromYaw, yaw)) < 2f)
            {
                RemoteMotions.Remove(box);
                ApplyPhysicsPose(box, position, yaw);
                return;
            }
            RemoteMotions[box] = new RemoteMotion
            {
                From = from,
                To = position,
                FromYaw = fromYaw,
                ToYaw = yaw,
                Age = 0f,
                Duration = Mathf.Clamp(0.18f + distance * 0.06f, 0.18f, 0.45f)
            };
        }

        /// <summary>Advance client-only cosmetic box motion. The target remains the host's
        /// settled pose; prediction never changes authority.</summary>
        public static void TickRemoteMotions(float dt)
        {
            if (RemoteMotions.Count == 0)
                return;
            var finished = new List<InteractablePackagingBox>();
            foreach (var pair in RemoteMotions)
            {
                var box = pair.Key;
                var motion = pair.Value;
                if (box == null || !box.gameObject.activeInHierarchy)
                {
                    finished.Add(box);
                    continue;
                }
                motion.Age += Mathf.Max(0f, dt);
                float t = Mathf.Clamp01(motion.Age / motion.Duration);
                float eased = t * t * (3f - 2f * t);
                Vector3 p = Vector3.Lerp(motion.From, motion.To, eased);
                // A small arc makes a remote throw/drop read as motion rather than a teleport,
                // while the final endpoint is still exactly the host's authoritative pose.
                p.y += Mathf.Sin(t * Mathf.PI) * Mathf.Min(0.35f, 0.1f + Vector3.Distance(motion.From, motion.To) * 0.08f);
                ApplyPhysicsPose(box, p, Mathf.LerpAngle(motion.FromYaw, motion.ToYaw, eased));
                if (t >= 1f)
                    finished.Add(box);
            }
            for (int i = 0; i < finished.Count; i++)
            {
                var box = finished[i];
                if (box != null && RemoteMotions.TryGetValue(box, out var motion))
                    ApplyPhysicsPose(box, motion.To, motion.ToYaw);
                RemoteMotions.Remove(box);
            }
        }

        private static bool IsBeingHeld(InteractablePackagingBox_Item box)
        {
            try
            {
                return FiBeingHold?.GetValue(box) is bool b && b;
            }
            catch { return false; }
        }

        private static short WorkerHolding(InteractablePackagingBox_Item box)
        {
            var workers = WorkerManager.GetWorkerList();
            if (workers == null)
                return -1;
            for (int i = 0; i < workers.Count; i++)
                if (workers[i] != null && ReferenceEquals(FiWorkerHoldBox?.GetValue(workers[i]), box))
                    return (short)i;
            return -1;
        }

        /// <summary>Host: mark/unmark a box worker-untouchable while a GUEST carries it,
        /// so the restocker's candidate filters (CanWorkerTakeBox) skip it. Direct field
        /// write via the cached private FieldInfo; per-call try/catch so one bad box never
        /// aborts the caller's loop.</summary>
        private static void SetHostWorkerLock(InteractablePackagingBox_Item box, bool locked)
        {
            if (box == null)
                return;
            try
            {
                FiPreventWorkerTake?.SetValue(box, locked);
            }
            catch { }
        }

        /// <summary>Host: a peer disconnected. A box still marked client-carried would stay
        /// hidden AND worker-locked FOREVER - the set-down request is never coming, and a
        /// rejoining guest starts with an empty carry state so it never sends one either.
        /// Release every client-carried box: worker lock off, visible again, physics on; the
        /// next snapshot then broadcasts Carried=false so every guest un-hides it too.
        /// Ownership is keyed by connection where available, so a surviving guest's carry
        /// is not released when a different guest disconnects.</summary>
        public void HostReleaseConn(int connId)
        {
            var ids = new HashSet<ushort>(_remoteCarried);
            ids.UnionWith(_remoteMoving);
            foreach (var pair in _remoteOwner)
                if (pair.Value == connId)
                    ids.Add(pair.Key);
            if (ids.Count == 0)
                return;
            int released = 0;
            double now = Time.realtimeSinceStartupAsDouble;
            foreach (var id in ids)
            {
                if (_remoteOwner.TryGetValue(id, out var owner) && owner != 0 && owner != connId)
                    continue;
                // Open the ownership grace window instead of dropping the carry outright, and
                // do it for EVERY id we are force-releasing (before the liveness check, so a
                // box that is mid-teardown can't skip it). We are ending these carries on the
                // host's say-so, but in a 3-player session the peer that died may not be the
                // one holding this box - and a SURVIVING guest's set-down report is the message
                // that carries everything it did while holding it. Refusing that final edit is
                // how the drain gets duped back, and _remoteCarried.Clear() below removes the
                // only other thing that could have let it through. The per-id 6s check retires
                // these windows on its own, and HostPruneDead drops them with the box.
                _remoteReleased[id] = now;
                if (!_hostById.TryGetValue(id, out var box) || box == null)
                    continue;
                SetHostWorkerLock(box, false);
                // C-e: a box that was carried can be parked under the floor (hide spot / a
                // pose that slipped below the map). Re-showing it there drops it through the
                // world unrecoverable, so lift any under-floor box back to a sane pose
                // (keep x/z, y=0.5) BEFORE it becomes visible again.
                try
                {
                    if (box.transform.position.y < -2f)
                    {
                        var p = box.transform.position;
                        box.transform.position = new Vector3(p.x, 0.5f, p.z);
                    }
                }
                catch { }
                try
                {
                    if (!box.gameObject.activeSelf)
                        box.gameObject.SetActive(true);
                }
                catch { }
                try
                {
                    box.SetPhysicsEnabled(true);
                }
                catch { }
                released++;
                _remoteCarried.Remove(id);
                _remoteMoving.Remove(id);
                _remoteOwner.Remove(id);
            }
            // NB: _remoteReleased is deliberately NOT cleared here (each released id was
            // stamped above). The grace window allows an in-flight final edit to arrive after
            // the disconnect cleanup without reopening ownership for the departed client.
            if (released > 0)
            {
                CoopPlugin.Log.LogInfo($"BoxSync host: released {released} client-carried box(es) after a disconnect");
                ForceBroadcastNextTick();
            }
        }

        // client: host truth + id<->box maps (boxes spawned by our apply, or adopted
        // from the save-load population by type/order at first snapshot)
        private readonly List<Entry> _lastApplied = new List<Entry>();
        private readonly Dictionary<ushort, InteractablePackagingBox_Item> _byId =
            new Dictionary<ushort, InteractablePackagingBox_Item>();
        private readonly Dictionary<InteractablePackagingBox_Item, ushort> _idOf =
            new Dictionary<InteractablePackagingBox_Item, ushort>();
        private readonly HashSet<ushort> _carriedLastTick = new HashSet<ushort>();
        private readonly Dictionary<ushort, double> _recentlyReleased = new Dictionary<ushort, double>(); // client: ignore stale carried echoes
        private readonly Dictionary<ushort, double> _locallyTouched = new Dictionary<ushort, double>();   // client: my recent edits beat stale echoes
        private readonly HashSet<ushort> _snapshotIds = new HashSet<ushort>();      // scratch
        private readonly List<ushort> _removeScratch = new List<ushort>();          // scratch
        // client, per-apply scratch for the baseline merge: the baseline we entered this
        // apply with, indexed by id, plus the ids whose apply we SUPPRESSED this pass.
        // Both are rebuilt at the top of every ClientApplyInner - they never outlive one call.
        private readonly Dictionary<ushort, Entry> _prevApplied = new Dictionary<ushort, Entry>(); // scratch
        private readonly HashSet<ushort> _skippedIds = new HashSet<ushort>();       // scratch
        private readonly List<InteractablePackagingBox_Item> _orphanScratch =
            new List<InteractablePackagingBox_Item>();                              // scratch
        // client: id -> host's latest entry for the snapshot currently being applied,
        // so the store-retry ghost eviction (B2) can ask where the host claims a tracked
        // occupant lives without threading hostList through the static ApplyToBox
        private readonly Dictionary<ushort, Entry> _hostWhereScratch = new Dictionary<ushort, Entry>();
        // rate limits for the 1000-box-cap warnings (host oversize / client sweep-skip)
        private double _lastCapWarn;
        private double _lastCapSkipLog;

        // host: id assignment + per-client state
        private readonly BoxIdentityMap<InteractablePackagingBox_Item> _hostIdentity =
            new BoxIdentityMap<InteractablePackagingBox_Item>();
        private Dictionary<InteractablePackagingBox_Item, ushort> _hostIds
        {
            get
            {
                return _hostIdentity.IdOf;
            }
        }
        private Dictionary<ushort, InteractablePackagingBox_Item> _hostById
        {
            get
            {
                return _hostIdentity.ById;
            }
        }
        private readonly HashSet<ushort> _remoteCarried = new HashSet<ushort>();    // host: client-held boxes
        private readonly HashSet<ushort> _remoteMoving = new HashSet<ushort>();     // host: client placement drag
        private readonly Dictionary<ushort, int> _remoteOwner = new Dictionary<ushort, int>();
        private readonly HashSet<ushort> _hostCarriedLastTick = new HashSet<ushort>();
        private readonly Dictionary<ushort, double> _hostRecentlyReleased = new Dictionary<ushort, double>(); // host: just set it down; stale client reports must not stomp it
        // host: id -> when a GUEST let go of it. The guest's set-down report is the LAST
        // report that carries its edits (the drain it did while holding the box arrives in
        // the same message that clears Carried), so ownership has to outlive the carry flag
        // by a grace window or that final delta is dropped and the items are duped back.
        private readonly Dictionary<ushort, double> _remoteReleased = new Dictionary<ushort, double>();

        private float _timer;
        private const float BaseHostScanInterval = 1.5f;
        private const float MaxQuietHostScanInterval = 3.0f;
        private float _hostScanInterval = BaseHostScanInterval;
        // Carry transitions are latency-sensitive, not render-sensitive. Polling this at
        // 10 Hz keeps pickup/drop propagation below a frame of noticeable delay while
        // avoiding an all-box reflection scan on every Update.
        private float _carryPollTimer;
        private const float CarryPollInterval = 0.10f;
        private int _lastHostHash;
        private float _hostHeal;
        private readonly List<Entry> _reportBuf = new List<Entry>();
        private RestockManager _rm;

        public Action<List<Entry>> OnHostSnapshot;   // host: broadcast
        public Action<List<Entry>> OnClientChanges;  // client: request
        public Action<int, int> OnLocalRemoved;      // client: (id, type) I trashed a box
        /// <summary>Called after a host snapshot creates a new client-side box. ContainerSync
        /// uses this to claim an acknowledged empty-box take and put that exact mirror in the
        /// guest's hands.</summary>
        public Action<InteractablePackagingBox_Item, Entry> OnClientBoxCreated;

        /// <summary>Wired by CoopCore to the OnDestroyed patch: a box was destroyed by
        /// LOCAL gameplay (trash bin, storage) - not by sync reconciliation.</summary>
        public static Action<InteractablePackagingBox_Item> LocalBoxDestroyed;

        /// <summary>True while sync code itself destroys/spawns boxes, so the OnDestroyed
        /// patch doesn't mistake reconciliation for a player throwing boxes away.</summary>
        public static bool ApplyingRemote;

        public BoxSync()
        {
            Instance = this;
        }

        /// <summary>Disable static Harmony hooks before session state is torn down.</summary>
        public static void ClearLive()
        {
            Instance = null;
            ApplyingRemote = false;
            ResetRemoteMotions();
        }

        public static void ActivateLive(BoxSync instance)
        {
            Instance = instance;
        }

        public void Reset()
        {
            _lastApplied.Clear();
            _byId.Clear();
            _idOf.Clear();
            _carriedLastTick.Clear();
            _recentlyReleased.Clear();
            _locallyTouched.Clear();
            _prevApplied.Clear();
            _skippedIds.Clear();
            // clear the worker-untouchable lock on every box we still hold marked as
            // guest-carried BEFORE dropping the maps (B2): a session teardown must not
            // strand a box worker-locked into the next session. Best-effort - dead/
            // fake-null boxes just skip.
            foreach (var id in _remoteCarried)
                if (_hostById.TryGetValue(id, out var carried) && carried != null)
                    SetHostWorkerLock(carried, false);
            foreach (var id in _remoteMoving)
                if (_hostById.TryGetValue(id, out var moving) && moving != null)
                    SetHostWorkerLock(moving, false);
            _hostIdentity.Clear();
            _carryPollTimer = 0f;
            _remoteCarried.Clear();
            _remoteMoving.Clear();
            _remoteOwner.Clear();
            _remoteReleased.Clear(); // a reused id must not inherit a prior session's ownership window
            _hostCarriedLastTick.Clear();
            _hostRecentlyReleased.Clear();
            _remWindowStart.Clear();
            _remWindowCount.Clear();
            _timer = -0.6f; // staggered phase vs the other snapshot engines
            _hostScanInterval = BaseHostScanInterval;
            _lastHostHash = 0;
            _hostHeal = 0f;
            _rm = null;
            // static store-retry state leaks across sessions; a reused box id could inherit
            // a prior session's "gave up storing" and never get placed on the rack
            _storeFails.Clear();
            _storeGaveUp.Clear();
            _storeRetryAt.Clear();
            _underMapLogged.Clear(); // C-e: don't carry an under-map warning suppression across sessions
            _sm = null;
            _lastResolveWarn = 0;
            _hostWhereScratch.Clear();
            // drop any closure over a prior session's id maps so the ghost-eviction probe
            // can't fire against stale state before the next ClientApply re-wires it
            HostLocationOf = _ => default(HostBoxWhere);
            ResetRemoteMotions();
        }

        /// <summary>Force the next HostTick to broadcast the loose-box population immediately,
        /// bypassing the unchanged-hash gate and the 1.5s cadence. Used when another system
        /// (e.g. an empty-box dispense) spawns a box that must reach the guest promptly.</summary>
        public void ForceBroadcastNextTick()
        {
            _hostScanInterval = BaseHostScanInterval;
            _lastHostHash = 0;
            _timer = _hostScanInterval;
        }

        private RestockManager Rm()
        {
            if (_rm == null)
                _rm = UnityEngine.Object.FindObjectOfType<RestockManager>();
            return _rm;
        }

        private static List<InteractablePackagingBox_Item> LiveBoxes()
        {
            return RestockManager.GetItemPackagingBoxList();
        }

        /// <summary>Returns the authoritative world pose of a packaging box. The
        /// Rigidbody is the object the game actually simulates; using the root
        /// Transform alone can preserve a stale pose while a box is parented to a
        /// warehouse slot or is being moved by physics.</summary>
        public static Vector3 PhysicsPosition(InteractablePackagingBox box)
        {
            try
            {
                if (box != null && box.m_Rigidbody != null)
                    return box.m_Rigidbody.position;
            }
            catch { }
            return box != null ? box.transform.position : Vector3.zero;
        }

        public static Quaternion PhysicsRotation(InteractablePackagingBox box)
        {
            try
            {
                if (box != null && box.m_Rigidbody != null)
                    return box.m_Rigidbody.rotation;
            }
            catch { }
            return box != null ? box.transform.rotation : Quaternion.identity;
        }

        /// <summary>
        /// Vanilla hold mode moves the visible root to the hand while the dynamic
        /// Rigidbody remains at its previous world pose. ThrowBox/DropBox then
        /// enables that stale body, which makes the first network snapshot read
        /// the spawn/origin pose. Commit the visible pose to the real body before
        /// vanilla applies force or enters placement mode.
        /// </summary>
        public static void AlignHeldBody(InteractablePackagingBox box)
        {
            if (box == null || box.m_Rigidbody == null)
                return;
            try
            {
                var p = box.transform.position;
                var r = box.transform.rotation;
                box.m_Rigidbody.position = p;
                box.m_Rigidbody.rotation = r;
                box.m_Rigidbody.velocity = Vector3.zero;
                box.m_Rigidbody.angularVelocity = Vector3.zero;
                box.transform.SetPositionAndRotation(p, r);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning($"BoxSync: failed to align held body before release: {e.Message}");
            }
        }

        public static bool PhysicsSettled(InteractablePackagingBox box)
        {
            try
            {
                var rb = box != null ? box.m_Rigidbody : null;
                return rb == null || rb.isKinematic || rb.IsSleeping()
                    || rb.velocity.sqrMagnitude < 0.04f;
            }
            catch { return true; }
        }

        /// <summary>Moves the real physics body, not merely the visual root. This
        /// keeps the next physics tick and the next snapshot on the same pose.</summary>
        public static void ApplyPhysicsPose(InteractablePackagingBox box, Vector3 position, float yaw)
        {
            if (box == null)
                return;
            var rotation = Quaternion.Euler(0f, yaw, 0f);
            try
            {
                var rb = box.m_Rigidbody;
                if (rb != null)
                {
                    rb.position = position;
                    rb.rotation = rotation;
                    if (!rb.isKinematic)
                    {
                        rb.velocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                        rb.WakeUp();
                    }
                    // The vanilla game and its raycasts read Transform immediately, while
                    // Unity may defer copying a dynamic Rigidbody pose until FixedUpdate.
                    // Write both representations in this one commit so a box cannot exist at
                    // two clickable locations for a frame.
                    box.transform.SetPositionAndRotation(position, rotation);
                }
                else
                    box.transform.SetPositionAndRotation(position, rotation);
            }
            catch
            {
                try
                {
                    box.transform.SetPositionAndRotation(position, rotation);
                }
                catch { }
            }
            try
            {
                ObjMoveSync.SyncTagGroup(box.transform);
            }
            catch { }
        }

        private static Entry Snapshot(InteractablePackagingBox_Item box)
        {
            // mid-tumble poses must never be broadcast: applying them teleports the
            // other side's copy through the air (and a teleported SLEEPING body
            // freezes there) - only settled poses cross the wire
            bool settled = true;
            try
            {
                // use the box's REAL physics body, not GetComponentInChildren<Rigidbody>():
                // the prefab's open/close rig mesh (m_RigMeshGrp) carries its own KINEMATIC
                // child body that depth-first search returns first, so we'd read "settled"
                // while the real body is still mid-fall -> the host broadcasts a mid-air pose
                // and the box hangs frozen on the guest (the "floating boxes" report)
                settled = PhysicsSettled(box);
            }
            catch { }
            // warehouse-rack storage: the slot transform owns the pose, so a stored
            // box reports its slot address instead of a physics pose
            bool stored = false;
            int sShelf = 0, sComp = 0;
            ushort sShelfId = 0;
            try
            {
                if (box.m_IsStored)
                {
                    var sc = box.GetBoxStoredCompartment();
                    if (sc != null)
                    {
                        stored = true;
                        sShelf = sc.GetWarehouseIndex();
                        sComp = sc.GetIndex();
                        var warehouseShelf = sc.GetWarehouseShelf();
                        if (warehouseShelf != null)
                        {
                            if (CoopCore.Role == CoopRole.Host)
                                sShelfId = PlacedObjectIdentity.AssignHost(warehouseShelf);
                            else
                                PlacedObjectIdentity.TryGet(warehouseShelf, out sShelfId);
                        }
                    }
                }
            }
            catch { }
            return new Entry
            {
                Type = (int)box.m_ItemCompartment.GetItemType(),
                Count = box.m_ItemCompartment.GetItemCount(),
                IsBig = box.m_IsBigBox,
                IsOpen = box.IsBoxOpened(),
                // worker-held counts as carried: mirrors hide it while the restocker
                // walks it (instead of dragging a copy along the floor) and never
                // position-stomp the original out of its hands
                Carried = !stored && (IsLocallyCarried(box) || IsBeingHeld(box)),
                Settled = stored || settled,
                Stored = stored,
                StoreShelfId = sShelfId,
                StoreShelf = (byte)Mathf.Clamp(sShelf, 0, 255),
                StoreComp = (byte)Mathf.Clamp(sComp, 0, 255),
                Pos = PhysicsPosition(box),
                Yaw = PhysicsRotation(box).eulerAngles.y,
                HolderWorker = IsBeingHeld(box) ? WorkerHolding(box) : (short)-1,
                OwnerKind = IsBeingHeld(box) ? (byte)3 : (IsLocallyCarried(box) ? (byte)1 : (byte)0),
                // Physics motion alone is not possession. A loose box can be bumped
                // by a player and must remain visible/free rather than acquiring a
                // remote ownership lease.
                Owned = IsBeingHeld(box) || IsLocallyCarried(box) || box.GetIsMovingObject(),
                OwnerId = 0,
                InFlight = !stored && !IsLocallyCarried(box) && !box.GetIsMovingObject() && !settled,
                Moving = box.GetIsMovingObject(),
                Velocity = box.m_Rigidbody != null ? box.m_Rigidbody.velocity : Vector3.zero,
                AngularVelocity = box.m_Rigidbody != null ? box.m_Rigidbody.angularVelocity : Vector3.zero,
            };
        }

        private static bool Differs(Entry a, Entry b)
        {
            if (a.Type != b.Type || a.Count != b.Count || a.IsBig != b.IsBig || a.IsOpen != b.IsOpen)
                return true;
            if (a.Stored != b.Stored)
                return true;
            // Rack membership is separate bookkeeping. The physical pose is still
            // part of the truth while stored, so a stale parent/rig pose cannot hide
            // a real location mismatch.
            if (a.Stored)
                return a.StoreShelf != b.StoreShelf || a.StoreComp != b.StoreComp
                    || (a.Pos - b.Pos).sqrMagnitude > 0.01f
                    || Mathf.Abs(Mathf.DeltaAngle(a.Yaw, b.Yaw)) > 3f;
            return (a.Pos - b.Pos).sqrMagnitude > 0.01f || Mathf.Abs(Mathf.DeltaAngle(a.Yaw, b.Yaw)) > 3f;
        }

        /// <summary>Seed an EMPTY box's compartment so it can actually hold the wanted
        /// item type (B1). When a guest pulls the first item off a shelf into an empty
        /// open box, the box adopts the shelf's type on the guest side; the host box is
        /// still type None with an empty pos list, so PreSpawnItemUpdate/SpawnItem clamp
        /// every count to 0 (both CLAMP to m_ItemPosList.Count, ShelfCompartment ~498/507)
        /// and the gain evaporates. Initialize the type + pos list exactly the way
        /// FillBoxWithItem does (decompiled InteractablePackagingBox_Item ~100-118:
        /// SetItemType on the box, SetCompartmentItemType then CalculatePositionList on
        /// the compartment) so a subsequent count can land. Only touches EMPTY boxes
        /// (GetItemCount() <= 0): a box with real contents keeps its type, and an OPEN box
        /// with spawned Item objects positioned against this same pos list is never rebuilt
        /// under them. Returns true if it seeded (or the type already matched with a pos
        /// list), false if it declined (empty/None want, or non-empty box of another type).</summary>
        private static bool EnsureCompartmentType(InteractablePackagingBox_Item box, int wantType)
        {
            try
            {
                if (wantType == (int)EItemType.None)
                    return false; // nothing to seed
                var comp = box.m_ItemCompartment;
                int curType = (int)comp.GetItemType();
                bool hasPosList = comp.GetItemPosListCount() > 0;
                if (curType == wantType && hasPosList)
                    return true; // already usable
                // only adopt into a genuinely EMPTY box - never restyle one holding items
                if (comp.GetItemCount() > 0)
                    return curType == wantType;
                var et = (EItemType)wantType;
                box.SetItemType(et);                    // mirror FillBoxWithItem's box-side type
                comp.SetCompartmentItemType(et);
                comp.CalculatePositionList();
                return true;
            }
            catch { return false; }
        }

        /// <summary>Data-only count apply via the closed-box path (stored boxes are
        /// always closed): safe before OR after the box is slotted into a rack.</summary>
        private static void ApplyClosedCount(InteractablePackagingBox_Item box, int count)
        {
            try
            {
                var comp = box.m_ItemCompartment;
                if (comp.GetItemCount() == count)
                    return;
                // PreSpawnItemUpdate CLAMPS to m_ItemPosList.Count (ShelfCompartment.cs
                // ~496-501). An adopted/orphan-paired box that never ran FillBoxWithItem
                // has an EMPTY pos list, so every count clamps to 0 - the stored box's
                // contents pin to zero and never converge with the host (contents
                // divergence report). Initialize the pos list the way FillBoxWithItem
                // does (InteractablePackagingBox_Item.cs ~100-118: SetCompartmentItemType
                // then CalculatePositionList) so the count can actually apply. Guard to
                // stored/closed boxes only: an OPEN box mid-display has real, spawned
                // Item objects positioned against this same list, and rebuilding it under
                // them would shuffle the visible stack.
                bool storedOrClosed = true;
                try
                {
                    storedOrClosed = box.m_IsStored || !box.IsBoxOpened();
                }
                catch { }
                if (storedOrClosed && count > 0 && comp.GetItemPosListCount() <= 0)
                {
                    try
                    {
                        comp.SetCompartmentItemType(comp.GetItemType());
                        comp.CalculatePositionList();
                    }
                    catch { }
                }
                comp.PreSpawnItemUpdate(count);
                FiAmountToSpawn?.SetValue(box, count);
            }
            catch { }
        }

        /// <summary>A stored box destroyed without unhooking leaks the compartment's
        /// box count and leaves a dangling slot reference.</summary>
        private static void UnhookIfStored(InteractablePackagingBox_Item box)
        {
            try
            {
                if (box == null || !box.m_IsStored)
                    return;
                var comp = box.GetBoxStoredCompartment();
                if (comp != null)
                    comp.RemoveBox(box);
                box.m_IsStored = false;
            }
            catch { }
        }

        // NEVER CSingleton<ShelfManager>.Instance here: box snapshots arrive during
        // the client's LOADING SCREEN, and touching it then creates a fake empty
        // manager that shadows the real one all session - the 1.0.11 store mirror
        // silently found zero racks forever (second storage field report)
        private static ShelfManager _sm;
        private static double _lastResolveWarn;
        // storage-rack give-up tracking: after a few failed store attempts for a box
        // (full/mismatched slot) we stop retrying and leave it loose
        private static readonly Dictionary<ushort, int> _storeFails = new Dictionary<ushort, int>();
        private static readonly HashSet<ushort> _storeGaveUp = new HashSet<ushort>();
        private static readonly Dictionary<ushort, double> _storeRetryAt = new Dictionary<ushort, double>();

        /// <summary>What the host's CURRENT snapshot says about an occupant box we already
        /// track. Wired per-apply by ClientApplyInner (closure over _idOf + the incoming
        /// hostList) because ApplyToBox is static and can't see the instance maps. Used by
        /// the store-retry ghost eviction (B2): a compartment occupant that WE track but
        /// whom the host places somewhere ELSE (or not stored at all) is a stale ghost from
        /// rack-index divergence, and evicting it frees the slot the real store needs.</summary>
        public struct HostBoxWhere
        {
            public bool Tracked;   // this occupant is a box in our id maps (host knows it)
            public ushort Id;      // its stable id (0 if untracked)
            public bool Stored;    // host's latest: is it stored at all?
            public int Shelf;      // host's latest StoreShelf (valid only if Stored)
            public int Comp;       // host's latest StoreComp (valid only if Stored)
            // NOTE: there is deliberately no "this id resolves to a different object" flag.
            // _idOf and _byId are written in lockstep (every _idOf[box] = id sits beside a
            // _byId[id] = box), so such a test is dead by construction. The duplicate case that
            // does occur - one object listed twice in a compartment - is detected by reference
            // while walking the occupants in TryEvictGhostAndRetryStore.
        }
        /// <summary>Default: nothing is tracked (host path / not wired) - eviction no-ops.</summary>
        public static Func<InteractablePackagingBox_Item, HostBoxWhere> HostLocationOf =
            _ => default(HostBoxWhere);

        private static ShelfCompartment ResolveWarehouseCompartment(ushort shelfId, int shelfIdx, int compIdx)
        {
            try
            {
                if (_sm == null)
                    _sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
                var sm = _sm;
                if (sm == null)
                    return null;
                if (shelfId != 0
                    && PlacedObjectIdentity.TryResolve(sm, 1, shelfId, out var identifiedShelf))
                    return (identifiedShelf as WarehouseShelf)?.GetWarehouseCompartment(compIdx);
                var list = sm.m_WarehouseShelfList;
                for (int i = 0; i < list.Count; i++)
                {
                    var ws = list[i];
                    if (ws == null || ws.GetIndex() != shelfIdx)
                        continue;
                    return ws.GetWarehouseCompartment(compIdx);
                }
                // no silent skips: an unresolvable rack means the store mirror is
                // stuck retrying - say so (throttled) instead of shrugging
                double nowT = Time.realtimeSinceStartupAsDouble;
                if (nowT - _lastResolveWarn > 30.0)
                {
                    _lastResolveWarn = nowT;
                    CoopPlugin.Log.LogWarning($"BoxSync store: no warehouse rack with index {shelfIdx} (have {list.Count}) - retrying on next snapshot");
                }
            }
            catch { }
            return null;
        }

        /// <summary>B2: a stored-apply retry keeps getting rejected. Inspect the target
        /// compartment's box occupants; if any is a box WE track whose CURRENT host snapshot
        /// places it at a DIFFERENT shelf/comp (or not stored at all), it is a stale GHOST
        /// left over from rack-index divergence occupying the slot. Evict it (the game's own
        /// RemoveBox via UnhookIfStored) and retry the store in the same pass. Only evicts on
        /// POSITIVE identification - three cases, all provably slot leaks: a DESTROYED
        /// occupant the compartment still counts, the SAME OBJECT listed in the compartment
        /// twice, or a tracked box the host places elsewhere. An untracked
        /// box, or one the host agrees belongs here, is left alone and the caller falls
        /// through to fail-counting (the pin-at-host-pose fallback), which stays the terminal
        /// state for a GENUINE mismatch: a rejected box never registers in a compartment
        /// (decompiled InteractablePackagingBox_Item :200 is past all five rejection exits),
        /// so a pinned box costs no slot and un-pins the moment the host reports it
        /// not-stored. Freeing the leaked slots is what actually lets those retries land -
        /// do not add a retry loop on top of the pin. Always logs a diagnostic that proves-or-kills the
        /// index-divergence theory: the resolved rack's GetIndex/GetWarehouseIndex, the
        /// occupant ids found, and where the host claims each occupant lives.</summary>
        private static bool TryEvictGhostAndRetryStore(
            InteractablePackagingBox_Item box, ShelfCompartment rackComp, Entry want)
        {
            try
            {
                var occupants = rackComp.GetInteractablePackagingBoxList();
                // rack address as the compartment itself reports it - if these diverge from
                // want.StoreShelf/want.StoreComp the peers disagree on rack ordering
                int rackWarehouseIdx = -1, rackCompIdx = -1;
                try
                {
                    rackWarehouseIdx = rackComp.GetWarehouseIndex();
                }
                catch { }
                try
                {
                    rackCompIdx = rackComp.GetIndex();
                }
                catch { }

                var diag = new System.Text.StringBuilder();
                diag.Append($"BoxSync store DIAG: box id {want.Id} rejected by rack want="
                    + $"{want.StoreShelfId}/{want.StoreShelf}/{want.StoreComp} ")
                    .Append($"resolved warehouseIdx={rackWarehouseIdx} compIdx={rackCompIdx} ")
                    .Append($"occupants={(occupants == null ? 0 : occupants.Count)}: ");

                bool evictedAny = false;
                if (occupants != null)
                {
                    // snapshot the list: UnhookIfStored -> RemoveBox mutates it under us
                    var snap = new List<InteractablePackagingBox_Item>(occupants);
                    // Same OBJECT listed twice in one compartment. This is the real duplicate
                    // leak, and it is only visible here: the id maps cannot show it, because
                    // every writer that sets _idOf[box] sets _byId[id] on the adjacent line, so
                    // _byId[_idOf[occ]] is occ by construction and the old "resolves to a
                    // different object" test could never once be true. A repeated reference costs
                    // a real slot (AddBox incremented m_ItemAmount both times) while looking
                    // perfectly legitimate to the host-location test, since both entries ARE the
                    // box the host places here. Only LIVE occupants are ever compared (the
                    // != null test is Unity's, so a destroyed box falls through to the null
                    // branch below) - which matters, because UnityEngine.Object.Equals treats
                    // any two DESTROYED objects as equal and would otherwise read a compartment
                    // of dead boxes as one box repeated.
                    var seenOccupants = new HashSet<object>();
                    for (int i = 0; i < snap.Count; i++)
                    {
                        var occ = snap[i];
                        if (occ != null && !seenOccupants.Add(occ))
                        {
                            // BARE RemoveBox, never the take-recipe: the object is legitimately
                            // stored and staying stored - we are dropping one surplus LIST ENTRY
                            // and the counter that came with it. Unparenting it or turning its
                            // physics back on would make the box the player can actually see fall
                            // out of the rack. List.Remove drops the first match, which is the
                            // entry we already accepted; the object keeps its place either way.
                            diag.Append("[dup-ref-evicted] ");
                            evictedAny = true; // before the call - see the null branch
                            try
                            {
                                rackComp.RemoveBox(occ);
                            }
                            catch (Exception e) { CoopPlugin.Log.LogWarning("BoxSync store evict dup: " + e.Message); }
                            continue;
                        }
                        if (occ == null)
                        {
                            // A destroyed occupant that was never RemoveBox'd. Skipping it -
                            // which is what this branch used to do - leaked the slot FOREVER:
                            // ShelfCompartment.m_ItemAmount still counts it and HasEnoughSlot
                            // stays false, so one dead entry pins every box ever routed here
                            // (field log: rack 0/2 blocked box id 3, then box id 25 three
                            // minutes later). It is unambiguously reclaimable - AddBox never
                            // inserts null (decompiled ShelfCompartment :189-190), so a null
                            // element can only be a Unity-destroyed box. RemoveBox matches by
                            // reference through List.Remove, so this frees exactly one entry
                            // and decrements the counter with it. No unparent/physics recipe:
                            // there is no object left to make loose.
                            //
                            // FLAG BEFORE THE CALL, not after. RemoveBox commits the two things
                            // that matter in its FIRST TWO statements - m_ItemAmount-- then
                            // List.Remove (decompiled ShelfCompartment :230-231) - and only then
                            // walks the remaining occupants via SetPriceTagItemAmountText (:237),
                            // which is exactly where a compartment full of destroyed boxes
                            // throws. Setting the flag afterwards therefore lost the retry for a
                            // slot that HAD been freed: the caller saw evictedAny false, gave up,
                            // and the reclaimed slot sat unused until the next snapshot. The catch
                            // and its log stay - the throw is still real and still worth seeing.
                            diag.Append("[null-evicted] ");
                            evictedAny = true;
                            try
                            {
                                rackComp.RemoveBox(occ);
                            }
                            catch (Exception e) { CoopPlugin.Log.LogWarning("BoxSync store evict null: " + e.Message); }
                            continue;
                        }
                        var where = HostLocationOf(occ);
                        if (!where.Tracked)
                        {
                            diag.Append("[untracked] ");
                            continue;
                        }
                        // where does the host say this occupant lives?
                        string hostSays = where.Stored ? $"{where.Shelf}/{where.Comp}" : "not-stored";
                        bool hostDisagrees = !where.Stored
                            || where.Shelf != want.StoreShelf || where.Comp != want.StoreComp;
                        diag.Append($"[id {where.Id} host={hostSays}{(hostDisagrees ? " GHOST" : "")}] ");
                        // never evict the box we're trying to store, and never evict one the
                        // host agrees belongs in THIS slot (that's a legitimately-full slot).
                        if (hostDisagrees && !ReferenceEquals(occ, box))
                        {
                            // full take-recipe, not just RemoveBox: a stored box has physics
                            // OFF and is parented to the slot, so bare unhook would leave the
                            // evicted ghost a frozen kinematic husk at the old slot. Unhook +
                            // unparent + physics on = a normal loose box its own next apply
                            // pass repositions (or re-stores at the host's real slot).
                            UnhookIfStored(occ); // game's RemoveBox + clears m_IsStored
                            try
                            {
                                occ.transform.SetParent(null);
                            }
                            catch { }
                            try
                            {
                                occ.SetPhysicsEnabled(true);
                            }
                            catch { }
                            try
                            {
                                if (occ.m_MoveStateValidArea != null)
                                    occ.m_MoveStateValidArea.gameObject.SetActive(true);
                            }
                            catch { }
                            try
                            {
                                occ.m_ItemCompartment.SetPriceTagVisibility(occ.gameObject.activeSelf);
                            }
                            catch { }
                            evictedAny = true;
                        }
                    }
                }
                CoopPlugin.Log.LogWarning(diag.ToString());

                if (!evictedAny)
                    return false;
                // slot freed: retry the store in this same pass (physics already off from the
                // caller's attempt). DispenseItem returns void; caller re-checks box.m_IsStored.
                try
                {
                    box.DispenseItem(isPlayer: false, rackComp);
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("BoxSync store evict-retry: " + e.Message); }
                return true;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("BoxSync store evict: " + e.Message);
                return false;
            }
        }

        // ---------------- host ----------------

        private ushort HostIdFor(InteractablePackagingBox_Item box)
        {
            return _hostIdentity.GetOrAssign(box);
        }

        /// <summary>Assign (or retrieve) the authoritative id for a newly-created host box
        /// before its first periodic snapshot. Used by the empty-box storage acknowledgement
        /// so the requesting client can claim the exact mirrored box rather than guessing by
        /// position.</summary>
        public ushort EnsureHostId(InteractablePackagingBox_Item box)
        {
            if (box == null)
                return 0;
            return HostIdFor(box);
        }

        /// <summary>Client: resolve the stable id of a locally-mirrored box. A false result
        /// means the box has not appeared in a host snapshot yet.</summary>
        public bool TryGetClientId(InteractablePackagingBox_Item box, out ushort id)
        {
            if (box != null && _idOf.TryGetValue(box, out id))
                return true;
            id = 0;
            return false;
        }

        /// <summary>Client: resolve a host stable id back to the local mirror.</summary>
        public bool TryGetClientBox(ushort id, out InteractablePackagingBox_Item box)
        {
            if (_byId.TryGetValue(id, out box) && box != null)
                return true;
            box = null;
            return false;
        }

        /// <summary>Host: atomically consume a loose/remote-held empty box for an empty-box
        /// storage operation. The caller increments storage only when this succeeds, so a
        /// rejected/full storage cannot destroy the authoritative box.</summary>
        public bool TryConsumeForEmptyBoxStorage(ushort id, int expectedType, bool expectedBig)
        {
            if (CoopCore.Role != CoopRole.Host)
                return false;
            if (!_hostById.TryGetValue(id, out var box) || box == null)
                return false;
            try
            {
                if (box.m_IsBigBox != expectedBig)
                    return false;
                if ((int)box.m_ItemCompartment.GetItemType() != expectedType
                    || box.m_ItemCompartment.GetItemCount() != 0)
                    return false;
                if (IsLocallyCarried(box) || IsBeingHeld(box) || box.GetIsMovingObject())
                    return false;

                ApplyingRemote = true;
                try
                {
                    UnhookIfStored(box);
                    box.OnDestroyed();
                }
                finally { ApplyingRemote = false; }

                _hostIds.Remove(box);
                _hostById.Remove(id);
                _remoteCarried.Remove(id);
                _remoteMoving.Remove(id);
                _remoteReleased.Remove(id);
                _hostCarriedLastTick.Remove(id);
                _hostRecentlyReleased.Remove(id);
                _lastHostHash = 0;
                _timer = 1.5f;
                return true;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("BoxSync storage consume: " + e.Message);
                return false;
            }
        }

        private void HostPruneDead()
        {
            _removeScratch.Clear();
            foreach (var kv in _hostById)
                if (kv.Value == null)
                    _removeScratch.Add(kv.Key);
            for (int i = 0; i < _removeScratch.Count; i++)
            {
                ushort id = _removeScratch[i];
                if (_hostById.TryGetValue(id, out var dead) && !ReferenceEquals(dead, null))
                    _hostIds.Remove(dead); // Unity fake-null: reference still hashes
                _hostById.Remove(id);
                _remoteCarried.Remove(id);
                _remoteMoving.Remove(id);
                _remoteReleased.Remove(id);
                _hostCarriedLastTick.Remove(id);
                _hostRecentlyReleased.Remove(id);
            }
        }

        public void HostTick(float dt, bool active)
        {
            if (!active || Rm() == null)
                return;
            _carryPollTimer += dt;
            // carry transitions broadcast IMMEDIATELY - a box that still looks
            // on-the-floor invites another player to grab it too
            bool force = false;
            bool pollCarry = _carryPollTimer >= CarryPollInterval;
            if (pollCarry)
            {
                _carryPollTimer -= CarryPollInterval;
                if (_carryPollTimer > CarryPollInterval)
                    _carryPollTimer = CarryPollInterval;
            }
            try
            {
                if (pollCarry)
                {
                    var scan = LiveBoxes();
                    for (int i = 0; i < scan.Count; i++)
                    {
                        if (scan[i] == null)
                            continue;
                        ushort id = HostIdFor(scan[i]);
                        if (IsLocallyCarried(scan[i]) || IsBeingHeld(scan[i]))
                        {
                            if (_hostCarriedLastTick.Add(id))
                                force = true;
                        }
                        else if (_hostCarriedLastTick.Remove(id))
                        {
                            force = true;
                            _hostRecentlyReleased[id] = Time.realtimeSinceStartupAsDouble;
                        }
                    }
                }
            }
            catch { }
            _timer += dt;
            bool transient = false;
            var liveForCadence = LiveBoxes();
            for (int i = 0; i < liveForCadence.Count; i++)
            {
                var b = liveForCadence[i];
                if (b != null && (b.GetIsMovingObject() || !PhysicsSettled(b)))
                {
                    transient = true;
                    break;
                }
            }
            if (transient)
                _hostScanInterval = 0.05f;
            if (!force && _timer < _hostScanInterval)
                return;
            if (_timer >= _hostScanInterval)
                _timer -= _hostScanInterval;
            if (force)
                _lastHostHash = 0; // transitions bypass the unchanged-gate
            try
            {
                var boxes = LiveBoxes();
                // cap raised 250 -> 1000 (the wire count is now a ushort): a Day-100+ shop's
                // warehouse holds well over 250 boxes, and the old byte-capped snapshot
                // silently dropped everything past #250 - which is exactly where the game
                // APPENDS new delivery boxes. Guests then (a) never saw fresh deliveries
                // ("when guest orders items, the boxes only show on host") and (b) DESTROYED
                // their mapped copies of every box past the cap via the absent-id sweep -
                // permanent, compounding divergence ("items in boxes on storage shelf are
                // not the same"). 1000 entries * ~27B = ~27KB per changed snapshot: trivial
                // for the reliable channel.
                if (boxes.Count > 1000 && Time.realtimeSinceStartupAsDouble - _lastCapWarn > 60.0)
                {
                    _lastCapWarn = Time.realtimeSinceStartupAsDouble;
                    CoopPlugin.Log.LogWarning($"BoxSync host: {boxes.Count} live boxes exceed the 1000-box sync cap - boxes past the cap will not sync to guests");
                }
                var list = new List<Entry>(Mathf.Min(boxes.Count, 1000));
                for (int i = 0; i < boxes.Count && list.Count < 1000; i++)
                {
                    if (boxes[i] == null)
                        continue;
                    var e = Snapshot(boxes[i]);
                    e.Id = HostIdFor(boxes[i]);
                    // a client holds it: mark carried AND not-stored, so a not-yet-processed
                    // m_IsStored=true on our side can't broadcast a Stored=true echo that
                    // re-pins the taker's report back to stored (the rack-take desync)
                    if (_remoteCarried.Contains(e.Id))
                    {
                        e.Carried = true;
                        e.Stored = false;
                        e.OwnerKind = 2;
                        e.Owned = true;
                        e.OwnerId = _remoteOwner.TryGetValue(e.Id, out var owner) ? owner : 0;
                        // re-assert the worker lock every tick: a game path can clear
                        // m_PreventWorkerTakeBox out from under us (e.g. SetOpenCloseBox
                        // resets it false on close, decompiled ~370) while the guest still
                        // carries the box, which would re-open it to worker theft (B2)
                        SetHostWorkerLock(boxes[i], true);
                    }
                    else if (_remoteMoving.Contains(e.Id))
                    {
                        e.Carried = false;
                        e.Moving = true;
                        e.Stored = false;
                        e.OwnerKind = 2;
                        e.Owned = true;
                        e.OwnerId = _remoteOwner.TryGetValue(e.Id, out var owner) ? owner : 0;
                        SetHostWorkerLock(boxes[i], true);
                    }
                    list.Add(e);
                }
                // skip identical snapshots (boxes sit still most of the time); a slow
                // heal broadcast still repairs any client that missed one
                int hash = 17;
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    hash = hash * 31 + e.Id;
                    hash = hash * 31 + e.Type;
                    hash = hash * 31 + e.Count;
                    hash = hash * 31 + ((e.IsBig ? 1 : 0) | (e.IsOpen ? 2 : 0) | (e.Carried ? 4 : 0) | (e.Settled ? 8 : 0) | (e.Stored ? 16 : 0));
                    hash = hash * 31 + e.StoreShelf * 311 + e.StoreComp;
                    hash = hash * 31 + e.HolderWorker;
                    hash = hash * 31 + e.OwnerKind;
                    hash = hash * 31 + (e.InFlight ? 1 : 0) + (e.Moving ? 2 : 0);
                    hash = hash * 31 + (int)(e.Pos.x * 8f);
                    hash = hash * 31 + (int)(e.Pos.y * 8f); // include height: a box that settled to a corrected Y must re-broadcast
                    hash = hash * 31 + (int)(e.Pos.z * 8f);
                }
                _hostHeal += 1.5f;
                bool changed = hash != _lastHostHash;
                if (!changed && _hostHeal < 10f)
                {
                    _hostScanInterval = Math.Min(MaxQuietHostScanInterval, _hostScanInterval * 1.25f);
                    return;
                }
                _hostScanInterval = changed ? BaseHostScanInterval
                    : Math.Min(MaxQuietHostScanInterval, _hostScanInterval * 1.25f);
                _lastHostHash = hash;
                if (_hostHeal >= 10f)
                    HostPruneDead(); // slow housekeeping on the heal beat
                _hostHeal = 0f;
                OnHostSnapshot?.Invoke(list);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("BoxSync host: " + e.Message); }
        }

        /// <summary>Host: a client asked for box states (their local edits).</summary>
        public void HostApplyRequest(List<Entry> entries, int connId = 0)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (!_hostById.TryGetValue(e.Id, out var box) || box == null)
                    continue;
                if (_remoteOwner.TryGetValue(e.Id, out var currentOwner)
                    && currentOwner != 0 && currentOwner != connId)
                {
                    CoopPlugin.Log.LogWarning($"BoxSync: rejected box {e.Id} mutation from client {connId}; owned by client {currentOwner}");
                    continue;
                }
                // OWNERSHIP BOOKKEEPING FIRST, before any skip guard: the guards below
                // suppress the APPLY, but they must never suppress the record of who is
                // holding the box. A carry report that arrives while (say) the box is
                // still inside the host's 6s recently-released window used to be dropped
                // wholesale - _remoteCarried never learned of the guest's pickup, so the
                // ownership window could never open and the guest's set-down edits were
                // refused (items silently lost). Record carry/release always; apply later.
                // InFlight is loose physics, not ownership. Only an actual hold or
                // move-mode drag may acquire the client's lease.
                if (e.Carried)
                {
                    _remoteCarried.Add(e.Id);
                    _remoteOwner[e.Id] = connId;
                    SetHostWorkerLock(box, true);
                }
                else if (e.Moving)
                {
                    _remoteMoving.Add(e.Id);
                    _remoteCarried.Remove(e.Id);
                    _remoteOwner[e.Id] = connId;
                    SetHostWorkerLock(box, true);
                }
                else
                {
                    // stamp the real carried->loose edge only: it opens the ownership grace
                    // window the applyContent gate reads (this very request carries the
                    // edits the guest made while holding the box). The worker lock clears
                    // unconditionally, as it always did - it governs worker access, not
                    // content authority.
                    if (_remoteCarried.Remove(e.Id) | _remoteMoving.Remove(e.Id))
                        _remoteReleased[e.Id] = Time.realtimeSinceStartupAsDouble;
                    _remoteOwner.Remove(e.Id);
                    SetHostWorkerLock(box, false);
                }
                // type sanity: a mangled or ancient request must not RESTYLE a box that
                // already holds a different item. But an EMPTY host box (type None, or a
                // stale different type with zero contents) legitimately adopts the request's
                // type: the guest pulled an item off a shelf into an empty open box, and the
                // box's compartment took on the shelf's type (decompiled
                // RemoveItemFromShelf ~280-297 -> ShelfCompartment.CheckItemType ~242-247
                // sets the type when m_ItemAmount<=0). Rejecting that dropped the box's gain
                // while the shelf decrement synced through - the item was destroyed host-side
                // and healed away on the guest (B1). Only reject when the host box is
                // genuinely occupied with a CONFLICTING type. EItemType.None == -1.
                int htype = (int)box.m_ItemCompartment.GetItemType();
                if (htype != e.Type && htype != (int)EItemType.None
                    && box.m_ItemCompartment.GetItemCount() > 0)
                    continue;
                // never stomp a box in the host's or a WORKER's hands - AND (C-d) never a box
                // the HOST is currently moving/dragging in move-mode. GetIsMovingObject() is
                // the ObjMoveSync-style guard (InteractablePackagingBox_Item inherits it from
                // InteractableObject); the guest's report carries a stale pose that would snap
                // the box back out from under the host's drag. Skipping the whole request drops
                // its pose/store fields (which the hostAuthoritative apply below would ignore
                // anyway) plus its content, which a mid-move host box owns just like held ones.
                if (IsLocallyCarried(box) || IsBeingHeld(box))
                    continue;
                // just set down: a report the client built while we still carried it is
                // stale by definition - the race that teleported boxes mid-restock
                if (_hostRecentlyReleased.TryGetValue(e.Id, out double rel)
                    && Time.realtimeSinceStartupAsDouble - rel < 6.0)
                    continue;
                // (ownership bookkeeping moved ABOVE the skip guards - see the top of the
                // loop; a suppressed apply must still record carry/release edges)
                // CONTENT AUTHORITY, by OWNERSHIP instead of by direction-of-change. The old
                // gate let any count INCREASE through on an open host box, which is precisely
                // how the refill survived: the guest's _locallyTouched latch re-armed itself
                // forever (its only writer is the diff detector), so the guest kept echoing a
                // STALE HIGH count at a box the HOST was draining, and the gain carve-out
                // waved every one of those echoes through - the box refilled itself as fast as
                // the host emptied it. The mirror image of the same carve-out dropped the
                // guest's DRAIN at set-down (a loss, so it was blocked) and the items duped
                // back. Both die here: a guest can only change a box's contents while HOLDING
                // it, and the carry transition is force-sent every frame, so the host always
                // has the id in _remoteCarried before the set-down's gain arrives. Everything
                // outside that window is, by construction, a stale mirror echo - direction is
                // irrelevant, so losses apply inside the window too and the set-down drain
                // finally lands. The '|| !hostOpen' keeps the strictly-smaller, behavior-
                // preserving variant for CLOSED boxes: closed-box count applies were always
                // allowed, and B1's empty-box seeding path (guest pulls the first shelf item
                // into a box) depends on reaching the content apply at all.
                // C-a: hostAuthoritative=true also drops pose/store regardless.
                bool hostOpen = false;
                try
                {
                    hostOpen = box.IsBoxOpened();
                }
                catch { }
                bool guestOwns = _remoteCarried.Contains(e.Id) || _remoteMoving.Contains(e.Id)
                                  || (_remoteReleased.TryGetValue(e.Id, out double grel)
                                      && Time.realtimeSinceStartupAsDouble - grel < 6.0);
                bool applyContent = guestOwns || !hostOpen;
                // A client release (Q placement or the end of a throw) owns the
                // resulting loose pose for the hand-off window. The normal host
                // reconciliation deliberately ignores client poses, so commit this
                // transition before the authoritative content apply.
                if (guestOwns && !e.Stored && !e.Carried && !e.Moving && !e.InFlight)
                {
                    box.SetPhysicsEnabled(true);
                    ApplyPhysicsPose(box, e.Pos, e.Yaw);
                    if (box.m_Rigidbody != null)
                    {
                        box.m_Rigidbody.velocity = e.Velocity;
                        box.m_Rigidbody.angularVelocity = e.AngularVelocity;
                        box.m_Rigidbody.WakeUp();
                    }
                }
                ApplyToBox(box, e, hostAuthoritative: true, applyContent: applyContent);
            }
            // fan the change out to everyone NOW - with 3+ players the other
            // clients otherwise wait out the periodic tick and the hash gate.
            // Exactly one period: a larger sentinel made the keep-the-phase
            // decrement fire EVERY frame for seconds (the post-throw jitter)
            _timer = 1.5f;
            _lastHostHash = 0;
        }

        private static readonly Dictionary<int, double> _remWindowStart = new Dictionary<int, double>();
        private static readonly Dictionary<int, int> _remWindowCount = new Dictionary<int, int>();

        /// <summary>Shared by ALL box-family modules (item/card/furniture): true if this
        /// client's removal budget for the current 2s window is spent. A human trashing a
        /// warehouse won't exceed 16 boxes in 2 seconds (C-b raised the cap from 4) - but a
        /// client whose game is re-running its world-load teardown echoes its ENTIRE box
        /// population, all three lists, as "trashed" (first field incident: 250 boxes in
        /// 10ms). Per-connection, so one player's flood never eats another's legitimate
        /// trash. refusedId (optional) names the box in the throttled over-budget warning.</summary>
        public static bool RemovalFlooded(int connId, string channel, int refusedId = -1)
        {
            double nowT = Time.realtimeSinceStartupAsDouble;
            if (!_remWindowStart.TryGetValue(connId, out double start) || nowT - start > 2.0)
            {
                _remWindowStart[connId] = nowT;
                _remWindowCount[connId] = 0;
            }
            int c = _remWindowCount[connId] = _remWindowCount[connId] + 1;
            // C-b: cap raised 4 -> 16 (4x). This is friends-coop, not anti-grief-critical:
            // the old cap of 4/2s stranded host-side boxes during a legitimate warehouse
            // cleanup spree, and the guest - believing them trashed - watched them 'respawn
            // next day' and clog shelving (field report). A reload teardown echo still floods
            // hundreds in milliseconds, so the flood protection stays; only the ceiling moves.
            if (c <= 16)
                return false;
            // C-b: a refused removal must NOT be silent - name the box id and say 'removal
            // budget' so a stranded/respawning box is traceable. Throttled (first over-cap
            // removal of the window, then every 100th) so the reload-echo flood can't spam.
            if (c == 17 || c % 100 == 0)
                CoopPlugin.Log.LogWarning($"ignoring {channel} removal"
                    + (refusedId >= 0 ? $" of box id {refusedId}" : "")
                    + $" from client {connId} - removal budget spent for this 2s window (reload echo, not gameplay)");
            return true;
        }

        /// <summary>Host: a client trashed a box - destroy the real one so the next
        /// broadcast doesn't resurrect it at its old spot.</summary>
        public void HostApplyRemoval(int id, int type, int connId)
        {
            if (RemovalFlooded(connId, "item-box", id))
                return;
            if (!_hostById.TryGetValue((ushort)id, out var box) || box == null)
                return;
            if ((int)box.m_ItemCompartment.GetItemType() != type)
                return;
            if (IsLocallyCarried(box) || IsBeingHeld(box))
                return; // player's OR worker's hands
            ApplyingRemote = true;
            try
            {
                UnhookIfStored(box);
                box.OnDestroyed();
            } // OnDestroyed alone leaks the rack slot
            catch (Exception e) { CoopPlugin.Log.LogWarning("BoxSync removal: " + e.Message); }
            finally { ApplyingRemote = false; }
            _hostIds.Remove(box);
            _hostById.Remove((ushort)id);
            _remoteCarried.Remove((ushort)id);
            _remoteMoving.Remove((ushort)id);
            _remoteReleased.Remove((ushort)id);
            _hostCarriedLastTick.Remove((ushort)id);
            _hostRecentlyReleased.Remove((ushort)id);
        }

        /// <summary>Host: the host player destroyed a box locally - drop its id now
        /// rather than waiting for the heal-beat prune.</summary>
        public void HostNotifyLocalDestroyed()
        {
            HostPruneDead();
        }

        /// <summary>Client: the local player destroyed a box (trash bin etc.). Tell the
        /// host to remove the real one and stop tracking it, so no ghost report or stale
        /// echo brings it back.</summary>
        public void NotifyLocalDestroyed(InteractablePackagingBox_Item box)
        {
            if (!_idOf.TryGetValue(box, out ushort id))
                return; // never synced; host doesn't know it
            int type = 0;
            try
            {
                type = (int)box.m_ItemCompartment.GetItemType();
            }
            catch { }
            _idOf.Remove(box);
            _byId.Remove(id);
            _carriedLastTick.Remove(id);
            _locallyTouched.Remove(id);
            _recentlyReleased.Remove(id);
            for (int i = 0; i < _lastApplied.Count; i++)
            {
                if (_lastApplied[i].Id != id)
                    continue;
                _lastApplied.RemoveAt(i);
                break;
            }
            OnLocalRemoved?.Invoke(id, type);
        }

        // ---------------- client ----------------

        /// <summary>Client: reconcile the live box population to the host's snapshot.</summary>
        public void ClientApply(List<Entry> hostList)
        {
            ApplyingRemote = true;
            // publish "where does the host say this tracked occupant lives" for the store-
            // retry ghost eviction (B2). Rebuild the id->entry index for THIS snapshot, then
            // hand ApplyToBox a closure over our id maps. Reset in finally so a stale closure
            // never fires during the host's own apply path (HostApplyRequest).
            _hostWhereScratch.Clear();
            for (int i = 0; i < hostList.Count; i++)
                _hostWhereScratch[hostList[i].Id] = hostList[i];
            HostLocationOf = occupant =>
            {
                var r = default(HostBoxWhere);
                try
                {
                    if (occupant == null || !_idOf.TryGetValue(occupant, out ushort oid))
                        return r;
                    r.Tracked = true;
                    r.Id = oid;
                    if (_hostWhereScratch.TryGetValue(oid, out var he))
                    {
                        r.Stored = he.Stored;
                        r.Shelf = he.StoreShelf;
                        r.Comp = he.StoreComp;
                    }
                    // not in this snapshot at all: host no longer lists it stored -> not stored
                }
                catch { }
                return r;
            };
            try
            {
                ClientApplyInner(hostList);
            }
            finally { ApplyingRemote = false; HostLocationOf = _ => default(HostBoxWhere); }
        }

        private void ClientApplyInner(List<Entry> hostList)
        {
            // baseline merge setup: index the baseline we're ENTERING this apply with, and
            // start a fresh suppressed-id set. The baseline is what my mirror actually holds,
            // so every entry whose apply we skip below has to keep its OLD baseline instead
            // of adopting the host truth we refused to write (see the merge at the bottom).
            _prevApplied.Clear();
            for (int i = 0; i < _lastApplied.Count; i++)
                _prevApplied[_lastApplied[i].Id] = _lastApplied[i];
            _skippedIds.Clear();

            // drop map entries whose box died locally (reconcile destroys, scene churn)
            _removeScratch.Clear();
            foreach (var kv in _byId)
                if (kv.Value == null)
                    _removeScratch.Add(kv.Key);
            for (int i = 0; i < _removeScratch.Count; i++)
            {
                ushort id = _removeScratch[i];
                if (_byId.TryGetValue(id, out var dead) && !ReferenceEquals(dead, null))
                    _idOf.Remove(dead);
                _byId.Remove(id);
            }

            // ADOPT unmapped local boxes (spawned by the save-load or the game's own
            // post-load drip spawner, so they exist on both sides): pair them with
            // unmapped snapshot entries by type+size in list order - the client's
            // load order mirrors the host list the save was written from
            _orphanScratch.Clear();
            var live = LiveBoxes();
            for (int i = 0; i < live.Count; i++)
            {
                var b = live[i];
                if (b != null && !_idOf.ContainsKey(b))
                    _orphanScratch.Add(b);
            }
            if (_orphanScratch.Count > 0)
            {
                for (int i = 0; i < hostList.Count; i++)
                {
                    var want = hostList[i];
                    if (_byId.TryGetValue(want.Id, out var mapped) && mapped != null)
                        continue;
                    // an unmappable type pairs by type+size against nothing meaningful: its
                    // Type is the None sentinel, which would happily match any genuinely EMPTY
                    // local box and adopt the wrong one into the host's id
                    if (want.Unmapped)
                        continue;
                    for (int j = 0; j < _orphanScratch.Count; j++)
                    {
                        var cand = _orphanScratch[j];
                        if (cand == null)
                            continue;
                        if ((int)cand.m_ItemCompartment.GetItemType() != want.Type
                            || cand.m_IsBigBox != want.IsBig)
                            continue;
                        _byId[want.Id] = cand;
                        _idOf[cand] = want.Id;
                        _orphanScratch[j] = null;
                        break;
                    }
                }
            }

            _snapshotIds.Clear();
            double now = Time.realtimeSinceStartupAsDouble;
            for (int i = 0; i < hostList.Count; i++)
            {
                var want = hostList[i];
                _snapshotIds.Add(want.Id);
                // ONE-SIDED CONTENT PACK: the host's box holds an item type that does not exist
                // on this PC. Leave the id ENTIRELY alone - no spawn (there is nothing to
                // spawn), and above all no type-mismatch rebuild, which would read the
                // unmappable None as "wrong box here" and destroy a local box. The id stays in
                // _snapshotIds (added above, deliberately before this skip) so the absence
                // sweep below cannot read "we can't map it" as "the host destroyed it".
                if (want.Unmapped)
                    continue;
                _byId.TryGetValue(want.Id, out var box);
                bool created = false;
                if (box != null && (box.m_ItemCompartment.GetItemType() != (EItemType)want.Type
                                    || box.m_IsBigBox != want.IsBig))
                {
                    // shouldn't happen with stable ids - but never rebuild a box in
                    // someone's HANDS; wait for the set-down
                    if (IsLocallyCarried(box))
                    {
                        _skippedIds.Add(want.Id);
                        continue;
                    }
                    _idOf.Remove(box);
                    UnhookIfStored(box);
                    try
                    {
                        box.OnDestroyed();
                    }
                    catch { }
                    box = null;
                }
                if (box == null)
                {
                    try
                    {
                        // field diagnostic for the "guest orders show only on host" report:
                        // EPL modded ids live in [200000,500000] and their spawn path was
                        // suspected - log the attempt so success is as visible as failure
                        if (want.Type >= 200000)
                            CoopPlugin.Log.LogInfo($"BoxSync client: spawning modded-item box id {want.Id} type {want.Type} (EPL virtual id)");
                        box = RestockManager.SpawnPackageBoxItem((EItemType)want.Type, want.Count, want.IsBig);
                    }
                    catch (Exception e)
                    {
                        CoopPlugin.Log.LogWarning("BoxSync spawn: " + e.Message);
                        continue;
                    }
                    if (box == null)
                        continue;
                    _byId[want.Id] = box;
                    _idOf[box] = want.Id;
                    created = true;
                }
                if (created)
                {
                    try
                    {
                        OnClientBoxCreated?.Invoke(box, want);
                    }
                    catch (Exception e) { CoopPlugin.Log.LogWarning("BoxSync client box-created callback: " + e.Message); }
                }
                // a box in MY hands is mine until I put it down; a box in the HOST's
                // hands has a transient position we don't copy
                if (IsLocallyCarried(box) || box.GetIsMovingObject())
                {
                    _skippedIds.Add(want.Id);
                    continue;
                }
                // The thrower can receive one final carried snapshot before the
                // host processes the release report. Its local physics body is
                // already flying, so never let that stale echo hide the box.
                if (!box.m_IsStored && !PhysicsSettled(box))
                {
                    if (!box.gameObject.activeSelf)
                    {
                        box.gameObject.SetActive(true);
                        try
                        {
                            box.m_ItemCompartment.SetPriceTagVisibility(true);
                        }
                        catch { }
                        CoopPlugin.Log.LogInfo($"BoxSync client: ignored stale carried echo for locally moving box {want.Id}");
                    }
                    _skippedIds.Add(want.Id);
                    continue;
                }
                // a stale "carried" echo about a box I JUST released must not hide it
                if (want.Carried && _recentlyReleased.TryGetValue(want.Id, out double t) && now - t < 6.0)
                {
                    _skippedIds.Add(want.Id);
                    continue;
                }
                // my own recent edits (took an item, kicked it) win over stale echoes;
                // my report reaches the host and the next echo agrees. VISIBILITY is
                // exempt: someone else's pickup/set-down must show here immediately,
                // or their set-down box stays invisible to me for the whole window
                if (_locallyTouched.TryGetValue(want.Id, out double touched) && now - touched < 6.0)
                {
                    if (!want.Carried && !box.gameObject.activeSelf)
                    {
                        box.gameObject.SetActive(true);
                        try
                        {
                            box.m_ItemCompartment.SetPriceTagVisibility(true);
                        }
                        catch { }
                    }
                    else if (want.Carried && box.gameObject.activeSelf)
                    {
                        try
                        {
                            box.m_ItemCompartment.SetPriceTagVisibility(false);
                        }
                        catch { }
                        box.gameObject.SetActive(false);
                    }
                    _skippedIds.Add(want.Id);
                    continue;
                }
                // a CARRIED entry is a suppression too, even though we call through:
                // ApplyToBox hides the box and RETURNS before it touches content or pose,
                // so the host's count/pose never reach my mirror on this pass either.
                if (want.Carried)
                    _skippedIds.Add(want.Id);
                ApplyToBox(box, want, applyPosition: !want.Carried);
            }

            // remove local boxes whose id the host no longer lists: with stable ids
            // this is surgical - ONLY the genuinely-destroyed box dies, never an
            // index-shifted neighbor (the "vanished out of my hands" bug).
            // EXCEPT when the snapshot rode the cap: a truncated list proves nothing
            // about absence (the box may simply be past the cap, not destroyed), and
            // sweeping on it is exactly how guests permanently lost real warehouse
            // boxes back when the cap was 250. Destroys just defer to the next
            // un-capped snapshot.
            bool truncated = hostList.Count >= 1000;
            if (truncated && Time.realtimeSinceStartupAsDouble - _lastCapSkipLog > 60.0)
            {
                _lastCapSkipLog = Time.realtimeSinceStartupAsDouble;
                CoopPlugin.Log.LogInfo("BoxSync client: snapshot rode the 1000-box cap - skipping the absent-box sweep (can't tell destroyed from truncated)");
            }
            if (!truncated)
            {
                _removeScratch.Clear();
                foreach (var kv in _byId)
                    if (!_snapshotIds.Contains(kv.Key))
                        _removeScratch.Add(kv.Key);
                for (int i = 0; i < _removeScratch.Count; i++)
                {
                    ushort id = _removeScratch[i];
                    if (!_byId.TryGetValue(id, out var box))
                        continue;
                    if (box != null)
                    {
                        if (IsLocallyCarried(box))
                        {
                            CoopPlugin.Log.LogWarning($"host removed the box in your hands (id {id}, {(EItemType)(int)box.m_ItemCompartment.GetItemType()}) - it was consumed host-side");
                            // CRITICAL: OnDestroyed does NOT exit hold-box mode, so destroying a
                            // box the guest is holding strands the controller in HoldingBoxState
                            // forever (soft-lock: can't interact with anything, not even the trash).
                            // Release hold-box mode FIRST via the game's own exit.
                            try
                            {
                                CoopCore.ForceExitHoldBox(box);
                            }
                            catch { }
                        }
                        _idOf.Remove(box);
                        UnhookIfStored(box);
                        try
                        {
                            box.OnDestroyed();
                        }
                        catch { }
                    }
                    _byId.Remove(id);
                    _carriedLastTick.Remove(id);
                    _locallyTouched.Remove(id);
                    _recentlyReleased.Remove(id);
                }
            }

            // remember the APPLIED truth for local-change detection - applied, not merely
            // received. THIS is where the refill latch was born: the old code swallowed the
            // whole host list as the new baseline even for entries whose apply we had just
            // suppressed, so the baseline claimed a truth my mirror does not hold. The very
            // next ClientTick diffed the real box against that phantom baseline, Differs()
            // came back true, and _locallyTouched re-armed itself - forever. Its ONLY writer
            // is that diff detector, so once armed it kept the suppression alive, kept the
            // stale mirror echoing to the host, and the guest re-asserted its old count until
            // the host adopted it. A suppressed apply must therefore carry its OLD baseline
            // forward for everything the mirror owns (Type/Count/IsBig/IsOpen/Pos/Yaw/Settled;
            // Type+IsBig ride along because for a suppressed id they describe the box I
            // actually have, and identity/rebuild is decided against the LIVE box anyway) and
            // take only the host's carry/rack bookkeeping (Carried/Stored/StoreShelf/
            // StoreComp) - ClientTick keys its report decisions off exactly those fields, so
            // they must stay the host's. Every other id takes the host entry verbatim.
            _lastApplied.Clear();
            for (int i = 0; i < hostList.Count; i++)
            {
                var he = hostList[i];
                if (_skippedIds.Count > 0 && _skippedIds.Contains(he.Id))
                {
                    // CRITICAL: derive the suppressed baseline from the LIVE box, not the
                    // pre-suppression _prevApplied snapshot. The frozen snapshot could never
                    // track the guest's OWN edits made during the suppression window (a
                    // carried box's dispensing, for example), so Differs() fired against a
                    // stale baseline and _locallyTouched re-latched forever - the exact echo
                    // loop this merge exists to kill. Snapshot(live) == what my mirror IS,
                    // which is the merge's entire contract; only the host's carry/rack
                    // bookkeeping fields stay the host's (ClientTick keys its report
                    // decisions off exactly those).
                    if (_byId.TryGetValue(he.Id, out var liveBox) && liveBox != null)
                    {
                        Entry keep;
                        try
                        {
                            keep = Snapshot(liveBox);
                        }
                        catch { _lastApplied.Add(he); continue; }
                        keep.Id = he.Id;
                        keep.Carried = he.Carried;
                        keep.Stored = he.Stored;
                        keep.StoreShelf = he.StoreShelf;
                        keep.StoreComp = he.StoreComp;
                        _lastApplied.Add(keep);
                        continue;
                    }
                    // no live box resolved: fall through to the host entry (nothing local
                    // to preserve, and a missing box will reconcile on the next snapshot)
                }
                _lastApplied.Add(he);
            }
        }

        /// <summary>Client: detect the local player's own box edits and request them.</summary>
        public void ClientTick(float dt, bool active)
        {
            if (!active || Rm() == null || _lastApplied.Count == 0)
                return;
            // carry transitions are detected EVERY FRAME and reported immediately -
            // the periodic diff alone left pickups/set-downs invisible for seconds,
            // long enough for someone else to try grabbing the same box
            bool force = false;
            try
            {
                foreach (var kv in _idOf)
                {
                    if (kv.Key == null)
                        continue;
                    if (IsLocallyCarried(kv.Key))
                    {
                        if (_carriedLastTick.Add(kv.Value))
                            force = true; // pickup transition
                    }
                    else if (_carriedLastTick.Remove(kv.Value))
                    {
                        force = true; // set-down transition
                        _recentlyReleased[kv.Value] = Time.realtimeSinceStartupAsDouble;
                    }
                }
            }
            catch { }
            _timer += dt;
            bool transient = false;
            foreach (var kv in _idOf)
                if (kv.Key != null && (kv.Key.GetIsMovingObject() || !PhysicsSettled(kv.Key)))
                {
                    transient = true;
                    break;
                }
            float cadence = transient ? 0.05f : 1.5f;
            if (!force && _timer < cadence)
                return;
            if (_timer >= cadence)
                _timer -= cadence;
            try
            {
                bool changed = force;
                _reportBuf.Clear(); // serialized synchronously by the callback; safe to reuse
                var list = _reportBuf;
                double nowT = Time.realtimeSinceStartupAsDouble;
                for (int i = 0; i < _lastApplied.Count; i++)
                {
                    var truth = _lastApplied[i];
                    _byId.TryGetValue(truth.Id, out var box);
                    // C-c (guest side): the guest must only echo a box it has a REASON to
                    // report. The old loop parroted EVERY box every cycle - dead ones verbatim,
                    // stored/other-carried verbatim, and every live box's Snapshot - so a box's
                    // LAGGING mirror count was re-reported forever, and the host applied that
                    // stale count to a box it was actively dispensing from, refilling it
                    // endlessly ("boxes get unlimited items"). Reasons to report: I'm carrying
                    // it (carry transition), I just set it down, or its live snapshot DIFFERS
                    // from the applied truth / I touched it within the window. An untouched box
                    // is left out entirely; the host's own snapshot stays its authority.
                    if (box == null)
                        continue; // dead/unmapped locally: nothing local to report; a trash is settled by the separate BoxRemoved message

                    // A host-confirmed drop is being rendered by the shared cosmetic
                    // reconciler. Do not mistake its intermediate arc for a new client edit.
                    if (IsRemoteMotion(box))
                        continue;

                    bool touchedRecently = _locallyTouched.TryGetValue(truth.Id, out double tch) && nowT - tch < 6.0;
                    bool justReleased = _recentlyReleased.TryGetValue(truth.Id, out double rr) && nowT - rr < 6.0;

                    // while I'M carrying it: tell the host (so everyone else hides their copy)
                    // but keep reporting the last settled position. Carry IS a report reason;
                    // the pickup/set-down transitions themselves force a send (see above).
                    if (IsLocallyCarried(box))
                    {
                        CancelRemoteMotion(box);
                        var held = truth;
                        held.Carried = true;
                        held.Stored = false; // just took it off a rack: a stale stored flag would keep the host's copy slotted
                        list.Add(held);
                        continue;
                    }
                    // host says STORED: the rack slot is host-authoritative. Do NOT echo an
                    // untouched stored box - that is exactly the stale echo C-c removes, and
                    // reporting a not-stored state for a box the host legitimately shelved would
                    // command it to yank the box off the rack (revert war). Only report when I
                    // actively touched it recently. EXCEPTION: if WE physically took this exact
                    // box off the rack (no longer stored locally AND recently carried+released),
                    // fall through to Snapshot so the take mirrors even if the transient Carried
                    // frame was missed and a stale host Stored=true echo re-pinned us.
                    if (truth.Stored)
                    {
                        bool reallyStored = true;
                        try
                        {
                            reallyStored = box.m_IsStored;
                        }
                        catch { }
                        bool weTookItOff = !reallyStored && justReleased;
                        if (!weTookItOff)
                        {
                            if (touchedRecently)
                            {
                                list.Add(truth);
                                changed = true;
                            }
                            continue;
                        }
                        // else fall through to Snapshot(box): reports Stored=false + real pose
                    }
                    // hidden because ANOTHER player carries it: nothing local to report UNLESS I
                    // just set it down myself (that report IS the set-down, and skipping it
                    // deadlocks the box as carried-forever on the other screen)
                    if (truth.Carried && !justReleased)
                        continue;

                    var now = Snapshot(box);
                    now.Id = truth.Id;
                    bool differs = Differs(now, truth);
                    if (differs)
                    {
                        changed = true;
                        _locallyTouched[truth.Id] = nowT;
                    }
                    // C-c: echo this box only with a genuine local reason - a fresh diff this
                    // tick, a recent local edit still in the window, or a set-down/take we're
                    // confirming. An untouched box whose mirror already matches the applied
                    // truth is dropped (no lagging-count re-report -> no host refill).
                    if (differs || touchedRecently || justReleased || now.InFlight || now.Moving)
                    {
                        list.Add(now);
                        if (justReleased)
                            changed = true; // ensure the set-down/take actually sends
                    }
                }
                if (changed)
                    OnClientChanges?.Invoke(list);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("BoxSync client: " + e.Message); }
        }

        // ---------------- shared apply ----------------

        // C-e: box ids we've already warned about for an under-map pose, so the "refusing
        // under-map pose" warning fires ONCE per box id (a stuck under-floor host pose would
        // otherwise spam it every tick). Cleared in Reset() with the rest of the session state.
        private static readonly HashSet<ushort> _underMapLogged = new HashSet<ushort>();

        /// <summary>C-e: an under-map pose (y &lt; -2) must never be applied - a box written
        /// there falls through the world and is unrecoverable (a corrupt/late snapshot or a
        /// box that slipped below the floor host-side). Returns true (and logs ONCE per box id)
        /// when the wanted pose is under the floor, so pose-apply callers skip the position
        /// write and leave the box where it is.</summary>
        private static bool UnderMapPose(Entry want)
        {
            if (want.Pos.y >= -2f)
                return false;
            if (_underMapLogged.Add(want.Id))
                CoopPlugin.Log.LogWarning($"BoxSync: refusing under-map pose (y={want.Pos.y:F2}) for box id {want.Id} - keeping its current position");
            return true;
        }

        private static void ApplyToBox(InteractablePackagingBox_Item box, Entry want, bool applyPosition = true, bool hostAuthoritative = false, bool applyContent = true)
        {
            try
            {
                // Transient states are authoritative physics hand-offs. Do not run
                // them through the settled/rack reconciler: it intentionally zeros
                // velocity and would turn a throw into a teleport.
                if (want.InFlight)
                {
                    if (!box.gameObject.activeSelf)
                        box.gameObject.SetActive(true);
                    box.SetPhysicsEnabled(true);
                    ApplyPhysicsPose(box, want.Pos, want.Yaw);
                    if (box.m_Rigidbody != null)
                    {
                        box.m_Rigidbody.velocity = want.Velocity;
                        box.m_Rigidbody.angularVelocity = want.AngularVelocity;
                        box.m_Rigidbody.WakeUp();
                    }
                    return;
                }
                if (want.Moving)
                {
                    if (!box.gameObject.activeSelf)
                        box.gameObject.SetActive(true);
                    box.SetPhysicsEnabled(false);
                    ApplyPhysicsPose(box, want.Pos, want.Yaw);
                    return;
                }
                // C-a: on the HOST this runs for a guest's REQUEST (hostAuthoritative=true).
                // The host owns rack placement AND pose for every box it tracks - only guest-
                // authoritative transitions may cross: carried enter/leave (worker lock paired
                // in HostApplyRequest), open/close, CONTENT changes that pass the C-c guard
                // (applyContent), and the STORE-ON-SETDOWN below. Skip the client's stored/rack
                // mirror machinery and the pose write; the host's own next snapshot rebroadcasts
                // its truth and heals the guest. This is exactly what stops the 1.0.29 give-up
                // PIN (kinematic freeze at want.Pos) from running HOST-side and freezing
                // authoritative boxes at the guest's reported pose.
                //
                // STORE-ON-SETDOWN (the one stored-transition the host MUST record): a guest
                // racking a box is as guest-authoritative as carrying it off one - BoxSync's
                // Stored field is the ONLY carrier (no dedicated store message exists), so if
                // the host never stores its copy the box stays LOOSE in the authoritative
                // world, the guest's report re-differs forever, and the storage is silently
                // LOST on save. Run the game's own store recipe ONCE per report (no retry
                // loop, no give-up pin, no eviction - those are client-mirror machinery); if
                // the host's rack genuinely rejects it (slot truly full), the box stays a
                // normal loose box and the host's truth rebroadcast pops it back off the
                // guest's rack too - correct, visible, self-consistent.
                if (hostAuthoritative && want.Stored && !want.Carried)
                {
                    bool hostStored = false;
                    try
                    {
                        hostStored = box.m_IsStored;
                    }
                    catch { }
                    if (!hostStored)
                    {
                        var hostRack = ResolveWarehouseCompartment(want.StoreShelfId, want.StoreShelf, want.StoreComp);
                        if (hostRack != null)
                        {
                            try
                            {
                                // reactivate FIRST (a guest-carried box is hidden host-side):
                                // DispenseItem on an OPEN box runs SetOpenCloseBox, whose
                                // StartCoroutine throws on an inactive GameObject - the throw
                                // landed before m_IsStored and silently lost the store
                                if (!box.gameObject.activeSelf)
                                    box.gameObject.SetActive(true);
                                // seed contents only when the HOST box is genuinely empty
                                // (all DispenseItem's empty-check needs) - an unconditional
                                // count write here would bypass the applyContent authority
                                // gate and let a stale carried-mirror count shrink/inflate
                                // the host's authoritative contents at store time
                                if (box.m_ItemCompartment.GetItemCount() <= 0)
                                    ApplyClosedCount(box, Mathf.Max(want.Count, 1));
                                box.SetPhysicsEnabled(false);
                                box.DispenseItem(isPlayer: false, hostRack);
                                bool ok = false;
                                try
                                {
                                    ok = box.m_IsStored;
                                }
                                catch { }
                                if (!ok)
                                    box.SetPhysicsEnabled(true); // rejected: stay a normal loose box
                            }
                            catch (Exception e)
                            {
                                CoopPlugin.Log.LogWarning("BoxSync host store: " + e.Message);
                                try
                                {
                                    box.SetPhysicsEnabled(true);
                                }
                                catch { }
                            }
                        }
                    }
                }
                if (!hostAuthoritative)
                {
                    // warehouse-rack storage FIRST: a stored box is parented to a rack slot
                    // the game owns. Syncing it as a loose box yanked stored boxes off the
                    // rack on BOTH sides ("boxes all over the place" report) and left them
                    // uninteractable - the store/unstore transition must go through the
                    // game's own methods so the compartment registration mirrors too
                    bool locallyStored = false;
                    try
                    {
                        locallyStored = box.m_IsStored;
                    }
                    catch { }
                    if (want.Stored)
                    {
                        // contents FIRST: DispenseItem rejects "empty" boxes, and a mirror
                        // whose count went stale while the box was carried would otherwise
                        // live-lock the store forever (closed-box path is data-only, safe
                        // whether stored already or about to be)
                        ApplyClosedCount(box, want.Count);
                        if (locallyStored)
                        {
                            // Do not trust m_IsStored by itself. A previous mirror can leave
                            // this box registered on a DIFFERENT compartment after rack
                            // ordering changed or a worker took/set it down during an apply.
                            // Returning here would preserve the wrong membership forever and
                            // make the next worker placement appear desynced.
                            bool atRequestedSlot = false;
                            try
                            {
                                var current = box.GetBoxStoredCompartment();
                                var requested = ResolveWarehouseCompartment(
                                    want.StoreShelfId, want.StoreShelf, want.StoreComp);
                                atRequestedSlot = current != null && requested != null
                                    ? ReferenceEquals(current, requested)
                                    : current != null
                                        && current.GetWarehouseIndex() == want.StoreShelf
                                        && current.GetIndex() == want.StoreComp;
                            }
                            catch { }
                            if (atRequestedSlot)
                            {
                                if (want.Settled && !UnderMapPose(want))
                                    ApplyPhysicsPose(box, want.Pos, want.Yaw); // rack transform owns the slot; no loose-box glide
                                return; // membership is correct; pose is now authoritative too
                            }

                            // Move through the same unstore recipe used below instead of
                            // merely changing the transform. This keeps the old compartment's
                            // box list and item count in sync with m_IsStored.
                            UnhookIfStored(box);
                            try
                            {
                                box.transform.SetParent(null);
                            }
                            catch { }
                            try
                            {
                                box.SetPhysicsEnabled(true);
                            }
                            catch { }
                        }
                        // give-up guard: a genuinely full/mismatched rack slot rejects the
                        // store on EVERY tick, and the old code retried forever (field log:
                        // "rejected box id 252 ... retrying" every 30s all session). Once we
                        // give up we STOP dispensing - but instead of the old fall-through to a
                        // loose pose (which rendered the box floating at the elevated rack-slot
                        // world position want.Pos with physics on - the "storage boxes floating
                        // in the air" report), we KINEMATICALLY PIN it at the host pose so it
                        // reads as shelved (B1). A later snapshot retries the store on change.
                        if (_storeGaveUp.Contains(want.Id)
                            && _storeRetryAt.TryGetValue(want.Id, out var retryAt)
                            && Time.realtimeSinceStartupAsDouble >= retryAt)
                        {
                            // A pin is only a temporary visual fallback. Rack contents can
                            // change after a worker removes a neighboring box, so retry the
                            // identity-resolved slot periodically instead of leaving a
                            // permanently frozen shell.
                            _storeGaveUp.Remove(want.Id);
                        }
                        if (!_storeGaveUp.Contains(want.Id))
                        {
                            if (!box.gameObject.activeSelf)
                                box.gameObject.SetActive(true);
                            var rackComp = ResolveWarehouseCompartment(want.StoreShelfId, want.StoreShelf, want.StoreComp);
                            if (rackComp != null)
                            {
                                try
                                {
                                    // the game's OWN restore recipe (ShelfManager.DelayLoad):
                                    // physics off, then DispenseItem. In the player flow
                                    // StartHoldBox already disabled physics; a live loose box
                                    // here still has gravity and would fall off the rack
                                    box.SetPhysicsEnabled(false);
                                    box.DispenseItem(isPlayer: false, rackComp);
                                }
                                catch (Exception e) { CoopPlugin.Log.LogWarning("BoxSync store: " + e.Message); }
                                if (box.m_IsStored)
                                {
                                    _storeFails.Remove(want.Id);
                                    _storeRetryAt.Remove(want.Id);
                                    _storeGaveUp.Remove(want.Id);
                                    return; // stored successfully; slot owns pose
                                }
                                // DispenseItem returns void and eats failures. On the 2nd+ attempt,
                                // before we count another fail, check whether the slot is blocked by
                                // a GHOST occupant - a box WE track that the host places somewhere
                                // else (or not stored) - and if so evict it and retry in THIS pass
                                // (B2). This is also our field probe for the rack-index-divergence
                                // theory: we log the resolved rack's indices, the occupant ids, and
                                // where the host claims each occupant lives.
                                _storeFails.TryGetValue(want.Id, out int fails);
                                if (fails >= 1 && TryEvictGhostAndRetryStore(box, rackComp, want))
                                {
                                    if (box.m_IsStored)
                                    {
                                        _storeFails.Remove(want.Id);
                                        _storeRetryAt.Remove(want.Id);
                                        _storeGaveUp.Remove(want.Id);
                                        return; // ghost gone, store succeeded; slot owns pose
                                    }
                                }
                                box.SetPhysicsEnabled(true); // don't leave a loose box frozen mid-retry
                                _storeFails[want.Id] = ++fails;
                                if (fails < 4)
                                    return; // still trying; don't apply a loose pose mid-attempt
                                _storeGaveUp.Add(want.Id);
                                _storeRetryAt[want.Id] = Time.realtimeSinceStartupAsDouble + 10.0;
                                CoopPlugin.Log.LogWarning($"BoxSync store: rack {want.StoreShelf}/{want.StoreComp} keeps rejecting box id {want.Id} (slot full or size/type mismatch) - pinning it shelved at the host pose");
                                // fall through to the B1 pin below (do NOT return here)
                            }
                            else
                            {
                                return; // no rack yet; don't apply a loose pose mid-attempt
                            }
                        }
                        // GAVE UP (this pass or a prior one): the give-up branch OWNS the final
                        // physics/pose so the generic loose-apply further down can't fight it.
                        // Kinematic pin at the host pose = sits AT the rack, matching the host,
                        // instead of floating loose. SetPhysicsEnabled(false) => isKinematic +
                        // collider off (InteractablePackagingBox.cs ~163-175). Once the host
                        // takes it off the rack, want.Stored flips false and the else-branch
                        // below re-enables physics so it behaves normally again.
                        try
                        {
                            if (!box.gameObject.activeSelf)
                            {
                                box.gameObject.SetActive(true);
                                try
                                {
                                    box.m_ItemCompartment.SetPriceTagVisibility(true);
                                }
                                catch { }
                            }
                            box.SetPhysicsEnabled(false);
                            // C-e: never write an under-map pose (y < -2) - a box pinned there
                            // falls through the world unrecoverable; keep its current position.
                            if (!UnderMapPose(want))
                            {
                                ApplyPhysicsPose(box, want.Pos, want.Yaw);
                            }
                        }
                        catch (Exception e) { CoopPlugin.Log.LogWarning("BoxSync store pin: " + e.Message); }
                        return;
                    }
                    else
                    {
                        // host says NOT stored: clear any give-up state so a future store
                        // (rack slot freed up, box re-placed) is attempted fresh
                        bool wasPinned = _storeGaveUp.Count > 0 && _storeGaveUp.Remove(want.Id);
                        _storeRetryAt.Remove(want.Id);
                        if (_storeFails.Count > 0)
                            _storeFails.Remove(want.Id);
                        // a give-up box was pinned KINEMATIC (physics off) at the rack pose. Now
                        // the host has taken it off the rack, so it must behave as a normal loose
                        // box again - re-enable physics here (the locallyStored take-recipe below
                        // won't fire for it, because the pin never actually stored it: m_IsStored
                        // is false). Without this the box stays an unclickable frozen ghost.
                        if (wasPinned && !locallyStored)
                        {
                            try
                            {
                                box.SetPhysicsEnabled(true);
                            }
                            catch { }
                        }
                    }
                    if (locallyStored)
                    {
                        // remote took it off the rack: replicate the take recipe, not just
                        // the bookkeeping - a stored box has physics DISABLED, and skipping
                        // the re-enable left an unclickable kinematic ghost at the drop spot
                        UnhookIfStored(box);
                        try
                        {
                            box.transform.SetParent(null);
                        }
                        catch { }
                        try
                        {
                            box.SetPhysicsEnabled(true);
                        }
                        catch { }
                        try
                        {
                            if (box.m_MoveStateValidArea != null)
                                box.m_MoveStateValidArea.gameObject.SetActive(true);
                        }
                        catch { }
                        try
                        {
                            box.m_ItemCompartment.SetPriceTagVisibility(box.gameObject.activeSelf);
                        }
                        catch { }
                    }
                }
                else if (want.Carried)
                {
                    // C-a (host): the host ignores guest store/pose noise for tracked boxes,
                    // but a guest now CARRYING this box has physically taken it off the rack.
                    // Mirror THAT unstore via the game's own take recipe - a Carried-enter is a
                    // guest-authoritative transition, and for a stored box it means "off the
                    // rack." Skipping it leaves the host box registered on the slot: it
                    // rebroadcasts Stored=true when the guest sets it down and teleports the box
                    // back onto the rack on the guest (rack-take desync). Bare not-stored noise
                    // without a carry is still ignored (that's what C-a set out to stop).
                    bool hostStored = false;
                    try
                    {
                        hostStored = box.m_IsStored;
                    }
                    catch { }
                    if (hostStored)
                    {
                        UnhookIfStored(box);
                        try
                        {
                            box.transform.SetParent(null);
                        }
                        catch { }
                        try
                        {
                            box.SetPhysicsEnabled(true);
                        }
                        catch { }
                        try
                        {
                            if (box.m_MoveStateValidArea != null)
                                box.m_MoveStateValidArea.gameObject.SetActive(true);
                        }
                        catch { }
                        try
                        {
                            box.m_ItemCompartment.SetPriceTagVisibility(box.gameObject.activeSelf);
                        }
                        catch { }
                    }
                }

                // someone (remote) is carrying it: their avatar shows the box in hand,
                // so the world copy disappears until it's set down - tags included
                // (box price tags live in a separate canvas group)
                if (want.Carried)
                {
                    // Worker-held boxes are represented by a cosmetic clone on the
                    // worker puppet; never reparent this synchronized gameplay box.
                    if (want.HolderWorker >= 0 && CoopCore.Role == CoopRole.Client)
                        NpcSync.SetWorkerBoxVisual(want.HolderWorker, true, want.IsBig, want.Type);
                    if (box.gameObject.activeSelf)
                    {
                        try
                        {
                            box.m_ItemCompartment.SetPriceTagVisibility(false);
                        }
                        catch { }
                        box.gameObject.SetActive(false);
                    }
                    return;
                }
                if (!box.gameObject.activeSelf)
                {
                    box.gameObject.SetActive(true);
                    try
                    {
                        box.m_ItemCompartment.SetPriceTagVisibility(true);
                    }
                    catch { }
                }

                // open/close FIRST: content semantics depend on the resulting state.
                // C-c belt+braces: applyContent is false when the HOST box is open and the
                // guest does NOT own it (not carrying it, and outside the grace window that
                // follows its set-down) - an open box nobody is holding is being worked by the
                // host/workers and its contents are host-authoritative, so a guest's stale
                // count/open never stomps it. That guest echo of an open box's lagging count
                // was the endless item-refill ("boxes get unlimited items"). On the client
                // applyContent is always true, so nothing changes there.
                if (applyContent && box.IsBoxOpened() != want.IsOpen && MiSetOpenClose != null)
                {
                    try
                    {
                        MiSetOpenClose.Invoke(box, null);
                    }
                    catch { }
                }

                var comp = box.m_ItemCompartment;
                int cur = comp.GetItemCount();
                if (applyContent && cur != want.Count)
                {
                    // EMPTY box gaining its first item: adopt the wanted type + build the
                    // pos list (B1). Without this, an empty box whose host compartment is
                    // still type None (the guest pulled a shelf item into it) can't spawn
                    // anything - SpawnItem/PreSpawnItemUpdate both clamp to the empty pos
                    // list. EnsureCompartmentType only acts on genuinely empty compartments,
                    // so an open box mid-display with real items is left untouched.
                    if (want.Count > 0)
                        EnsureCompartmentType(box, want.Type);
                    if (box.IsBoxOpened())
                    {
                        // atomic clear-and-rebuild: SpawnItem is a LOADER (sets the count
                        // and appends fresh items), so incremental use duplicates objects
                        if (FiStoredList?.GetValue(comp) is List<Item> stored && stored.Count > 0)
                        {
                            foreach (var it in new List<Item>(stored))
                            {
                                if (it == null)
                                    continue;
                                comp.RemoveItem(it);
                                ItemSpawnManager.DisableItem(it);
                            }
                            stored.Clear();
                        }
                        if (want.Count > 0)
                            comp.SpawnItem(want.Count, spawnFromFront: true);
                        else
                            comp.PreSpawnItemUpdate(0);
                    }
                    else
                    {
                        // closed box: items are LAZY - the visible count and the amount
                        // spawned on first open must both track the synced count
                        comp.PreSpawnItemUpdate(want.Count);
                        FiAmountToSpawn?.SetValue(box, want.Count);
                    }
                }
                // C-a + relocation: apply the guest's settled/drop pose so a box the guest
                // carried and SET DOWN (or nudged loose) actually moves on the host too. This
                // is NOT the C-a bug - that was the STORE give-up PIN (kinematic freeze at
                // want.Pos) running host-side, which the skipped store block above now
                // prevents. Only boxes the guest actually touched reach here (its C-c report
                // gate drops untouched boxes), and a box the HOST is move-dragging never gets
                // this far (C-d drops it in HostApplyRequest) - so this is exactly a guest
                // relocation, not a stale echo or a snap-back. Skip when the box is (still)
                // STORED: the host owns rack placement and want.Pos for a stored box is the
                // elevated rack-slot pose, which as a loose pose would drop it off the rack.
                // C-e: never write an under-map pose (logged once per id inside UnderMapPose).
                bool boxStoredNow = false;
                try
                {
                    boxStoredNow = box.m_IsStored;
                }
                catch { }
                // Thrown/Q-dropped boxes have a valid transient physics pose even
                // before they settle. Sync that pose immediately; carried boxes
                // remain hidden and stored boxes use their rack pose.
                if (applyPosition && !want.Carried && !want.Stored && !boxStoredNow && !UnderMapPose(want))
                {
                    var t = box.transform;
                    if ((t.position - want.Pos).sqrMagnitude > 0.01f
                        || Mathf.Abs(Mathf.DeltaAngle(t.eulerAngles.y, want.Yaw)) > 3f)
                    {
                        ScheduleRemoteMotion(box, want.Pos, want.Yaw);
                    }
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("BoxSync apply: " + e.Message); }
        }

        // ---------------- wire ----------------
    }
}
