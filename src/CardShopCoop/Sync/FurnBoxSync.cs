using CardShopCoop.Util;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Mirrors FURNITURE delivery boxes (InteractablePackagingBox_Shelf) between host
    /// and client. A furniture purchase spawns the real InteractableObject immediately
    /// and hides it inside a box (ShelfManager.SpawnInteractableObjectInPackageBox ->
    /// BoxUpObject -> RestockManager.SpawnPackageBoxShelf); the box registers in
    /// RestockManager.m_ShelfPackagingBoxList, which nothing else mirrors - the
    /// joiner's purchase forwards to the host (GamePatches.FurnitureOrderPrefix) but
    /// only the host could see or unpack the delivery.
    ///
    /// Architecture is BoxSync's (snapshot broadcast, hash gate + slow heal, immediate
    /// carry transitions via per-frame scan, stale-echo guards), with one twist: the
    /// boxed OBJECT already lives in a ShelfManager kind list (its subclass Awake
    /// self-registers even while boxed and hidden), and PopulationSync mirrors those
    /// lists. So the client never spawns furniture for a box - identity travels as
    /// the boxed object's (kind, index) in PopulationSync's shared list scheme, and
    /// the mirror is the game's own save-load recipe applied to the object
    /// PopulationSync already delivered: BoxUpObject(holdBox: false) + position the
    /// box. That keeps the population rosters identical instead of fighting them.
    /// Types also carry an FNV hash of the enum NAME: modded furniture ints can
    /// drift between machines, the name resolves them (license-sync philosophy).
    ///
    /// Joiner unpack: vanilla open/move runs locally (OnPressOpenBox just activates
    /// the mirrored object into move mode), but the placement CONFIRM is intercepted
    /// (PlaceMovedObject prefix): forwarded as a place op the host validates and runs
    /// through the real PlaceMovedObject - subclass OnPlacedMovedObject overrides
    /// included - so the box dies officially and the placed object keeps mirroring
    /// through the existing Population/ObjMove syncs. The joiner's local copy places
    /// immediately under ApplyingRemote (no destroy-forward) and a recently-unpacked
    /// guard keeps the state echo from boxing the object back up before the host
    /// confirms. Sell/trash (both require HOLDING the box) forward the OnDestroyed;
    /// a vanished snapshot entry replays as unpack when the box was last seen on the
    /// ground and as sell (object dies too) when it was last seen carried.
    /// </summary>
    public class FurnBoxSync
    {
        public struct Entry
        {
            public ushort Id;     // stable host identity for this delivery box
            public int WireType;  // host's (int)EObjectType of the BOXED object
            public int NameHash;  // Fnv of its enum name - survives modded int drift
            public byte Kind;     // PopulationSync kind, or GenericKind, or Unresolved
            public int ObjIndex;  // stable placed-object id (field name retained for wire shape)
            public Vector3 Pos;   // box transform
            public float Yaw;
            public bool Carried;  // in someone's hands: position is transient
            // 0=free, 1=host/local player, 2=remote client, 3=worker.
            public byte OwnerKind;
        }

        // Keep the count wide enough that a busy shop cannot silently lose the tail of
        // the delivery population. The wire count and operation indices are widened below.
        private const int MaxBoxes = 1000;
        private const float Period = 1.5f;
        private const byte GenericKind = 15;  // ShelfManager.m_InteractableObjectList
        private const byte Unresolved = 255;  // host couldn't place the object anywhere

        /// <summary>The live module instance, for the static Harmony patches.</summary>
        public static FurnBoxSync Instance;

        /// <summary>Set by CoopCore: is this box currently in the LOCAL player's hands?
        /// Use InteractionPlayerController.m_CurrentHoldingBox (the generic field) -
        /// m_CurrentHoldingBoxShelf is NOT cleared by OnExitHoldBoxMode and goes stale
        /// after every set-down, so it must never be part of this check.</summary>
        public static Func<InteractablePackagingBox_Shelf, bool> IsLocallyCarried = _ => false;

        /// <summary>Set by CoopCore: client -> host op (MsgType.FurnBoxOp).</summary>
        public Action<INetMessage> SendOp;

        /// <summary>Set by CoopCore: host -> clients state (MsgType.FurnBoxState).</summary>
        public Action<INetMessage> BroadcastState;

        /// <summary>True while sync code itself boxes/unboxes/destroys, so the patches
        /// don't mistake reconciliation for player action.</summary>
        public static bool ApplyingRemote;

        // op kinds on the FurnBoxOp wire
        private const byte OpReport = 0;  // carried flags + box positions
        private const byte OpPlace = 1;   // joiner confirmed an unpack placement
        private const byte OpRemoved = 2; // joiner sold/trashed a box (object dies too)

        private static readonly FieldInfo FiBoxedObject =
            ReflectionSurface.RequiredField(typeof(InteractablePackagingBox_Shelf), "m_BoxedObject");
        private static readonly FieldInfo FiMovingValid =
            ReflectionSurface.RequiredField(typeof(InteractableObject), "m_IsMovingObjectValidState");
        private static readonly FieldInfo FiGenericList =
            ReflectionSurface.RequiredField(typeof(ShelfManager), "m_InteractableObjectList");
        // BoxUpObject hides the cashier counter's world screens and only the mover's
        // OnStartMoveObject re-shows them - a headless remote placement must do it here
        private static readonly FieldInfo FiCounterScreen =
            ReflectionSurface.RequiredField(typeof(InteractableCashierCounter), "m_UICashCounterScreen");
        private static readonly FieldInfo FiCreditScreen =
            ReflectionSurface.RequiredField(typeof(InteractableCashierCounter), "m_UICreditCardScreen");

        /// <summary>
        /// The game's furniture-delivery spawn routine moves the box Transform after creating
        /// it, but does not move the box Rigidbody. The body can therefore remain at the
        /// construction position (usually the origin) while the visible box is at the delivery
        /// point. FurnBoxSync reads the body as authoritative, so repair that mismatch at the
        /// spawn boundary before the first snapshot is allowed to leave this machine.
        /// </summary>
        public static void AlignJustSpawnedBox(EObjectType objType, Vector3 position, Quaternion rotation)
        {
            try
            {
                var boxes = LiveBoxes();
                for (int i = boxes.Count - 1; i >= 0; i--)
                {
                    var box = boxes[i];
                    if (box == null || BoxedObject(box) == null
                        || BoxedObject(box).m_ObjectType != objType) continue;

                    // The newly-created box is appended to the list. Reverse order avoids
                    // accidentally repairing an older same-type delivery at the same location.
                    var rb = box.m_Rigidbody;
                    if (rb != null)
                    {
                        rb.position = position;
                        rb.rotation = rotation;
                        rb.velocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                        if (!rb.isKinematic) rb.WakeUp();
                        // ShelfManager moves the box Transform after spawning it, but Unity
                        // can leave a dynamic Rigidbody at the construction position until a
                        // later physics step. Commit both representations before snapshots.
                        box.transform.SetPositionAndRotation(position, rotation);
                    }
                    else box.transform.SetPositionAndRotation(position, rotation);
                    return;
                }
                CoopPlugin.Log.LogWarning($"FurnBoxSync: could not find newly spawned {objType} delivery box to align its Rigidbody");
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning($"FurnBoxSync: failed to align spawned {objType} box: {e.Message}");
            }
        }

        private static bool IsSanePose(Vector3 position)
        {
            // Reject NaN/Infinity and clearly impossible poses. Do not impose a tight shop
            // rectangle: expanded rooms and future maps may legitimately exceed the vanilla
            // bounds. The y floor is the important protection against boxes falling forever.
            return !float.IsNaN(position.x) && !float.IsNaN(position.y) && !float.IsNaN(position.z)
                && !float.IsInfinity(position.x) && !float.IsInfinity(position.y) && !float.IsInfinity(position.z)
                && position.y >= -2f && position.y <= 100f
                && Mathf.Abs(position.x) <= 1000f && Mathf.Abs(position.z) <= 1000f;
        }

        // boxes are stable components, so trackers key by REFERENCE - unlike BoxSync's
        // index keys there is nothing to clear when the list shifts (same semantics,
        // one less family of shifted-index bugs)
        private readonly Dictionary<InteractablePackagingBox_Shelf, Entry> _lastApplied
            = new Dictionary<InteractablePackagingBox_Shelf, Entry>();                    // client: host truth
        private readonly HashSet<InteractablePackagingBox_Shelf> _carriedLastTick
            = new HashSet<InteractablePackagingBox_Shelf>();
        private readonly HashSet<InteractablePackagingBox_Shelf> _remoteCarried
            = new HashSet<InteractablePackagingBox_Shelf>();                              // host: client-held boxes
        private readonly Dictionary<InteractablePackagingBox_Shelf, double> _recentlyReleased
            = new Dictionary<InteractablePackagingBox_Shelf, double>();                   // client: ignore stale carried echoes
        private readonly Dictionary<InteractablePackagingBox_Shelf, double> _locallyTouched
            = new Dictionary<InteractablePackagingBox_Shelf, double>();                   // client: my recent moves beat stale echoes
        private readonly HashSet<InteractablePackagingBox_Shelf> _hostCarriedLastTick
            = new HashSet<InteractablePackagingBox_Shelf>();                              // host: own-carry transitions
        private readonly Dictionary<InteractablePackagingBox_Shelf, double> _hostRecentlyReleased
            = new Dictionary<InteractablePackagingBox_Shelf, double>();                   // host: just set it down; stale client reports must not stomp it
        private readonly BoxIdentityMap<InteractablePackagingBox_Shelf> _hostIdentity
            = new BoxIdentityMap<InteractablePackagingBox_Shelf>();
        private readonly BoxIdentityMap<InteractablePackagingBox_Shelf> _clientIdentity
            = new BoxIdentityMap<InteractablePackagingBox_Shelf>();
        private Dictionary<InteractablePackagingBox_Shelf, ushort> _hostIds { get { return _hostIdentity.IdOf; } }
        private Dictionary<ushort, InteractablePackagingBox_Shelf> _hostById { get { return _hostIdentity.ById; } }
        private Dictionary<ushort, InteractablePackagingBox_Shelf> _clientById { get { return _clientIdentity.ById; } }
        private Dictionary<InteractablePackagingBox_Shelf, ushort> _clientIdOf { get { return _clientIdentity.IdOf; } }
        private readonly Dictionary<InteractableObject, double> _recentlyUnpacked
            = new Dictionary<InteractableObject, double>();                               // client: I placed it; the echo must not re-box it
        private double _suppressBoxUp;     // client: kind indices shifted (local sell); no NEW box-ups until the echo re-aligns
        private readonly Dictionary<InteractableObject, byte> _kindCache
            = new Dictionary<InteractableObject, byte>();                                 // which kind list an object lives in never changes
        private readonly List<ushort> _reportIds = new List<ushort>();
        private readonly List<Entry> _reportEntries = new List<Entry>();
        private Dictionary<int, int> _nameToType;                                         // Fnv(enum name) -> local int, built lazily
        private float _timer;
        // Carry transitions only need network-scale responsiveness. A 10 Hz poll preserves
        // immediate pickup/drop ownership while removing the full furniture-box scan from
        // every render frame.
        private float _carryPollTimer;
        private const float CarryPollInterval = 0.10f;
        private int _lastHostHash;
        private float _hostHeal;
        private RestockManager _rm;
        private ShelfManager _sm;

        public FurnBoxSync()
        {
            Instance = this;
        }

        /// <summary>Disable static Harmony hooks before session state is torn down.</summary>
        public static void ClearLive()
        {
            Instance = null;
            ApplyingRemote = false;
        }

        public static void ActivateLive(FurnBoxSync instance) { Instance = instance; }

        public void Reset()
        {
            _lastApplied.Clear();
            _carriedLastTick.Clear();
            _remoteCarried.Clear();
            _recentlyReleased.Clear();
            _locallyTouched.Clear();
            _hostCarriedLastTick.Clear();
            _hostRecentlyReleased.Clear();
            _hostIdentity.Clear();
            _clientIdentity.Clear();
            _carryPollTimer = 0f;
            _recentlyUnpacked.Clear();
            _kindCache.Clear();
            _suppressBoxUp = 0.0;
            _timer = -9.1f; // staggered phase vs the other snapshot engines
            _lastHostHash = 0;
            _hostHeal = 0f;
            _rm = null;
            _sm = null;
        }

        public void ForceResend()
        {
            _lastHostHash = 0;
            _hostHeal = 999f; // beats the hash gate even if the real hash is 0
        }

        /// <summary>Host: a peer disconnected - release any furniture box still marked
        /// client-carried, or it stays in its carried state forever (the set-down report
        /// is never coming; a rejoining guest starts with an empty carry set). Reuses the
        /// module's own un-carry apply at the box's current pose, exactly what a normal
        /// set-down report would have done. Releases everything (the set isn't keyed by
        /// connection); a surviving guest still carrying re-asserts on its next report.</summary>
        public void HostReleaseRemoteCarried()
        {
            if (_remoteCarried.Count == 0) return;
            int released = 0;
            foreach (var box in _remoteCarried)
            {
                if (box == null) continue;
                ApplyToBox(box, BoxSync.PhysicsPosition(box), BoxSync.PhysicsRotation(box).eulerAngles.y, carried: false);
                released++;
            }
            _remoteCarried.Clear();
            if (released > 0)
            {
                CoopPlugin.Log.LogInfo($"FurnBoxSync host: released {released} client-carried box(es) after a disconnect");
                ForceResend();
            }
        }

        private RestockManager Rm()
        {
            if (_rm == null) _rm = UnityEngine.Object.FindObjectOfType<RestockManager>();
            return _rm;
        }

        private ShelfManager Sm()
        {
            if (_sm == null) _sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            return _sm;
        }

        private static List<InteractablePackagingBox_Shelf> LiveBoxes()
        {
            return RestockManager.GetShelfPackagingBoxList();
        }

        public void ForceNextTick()
        {
            _timer = Period;
            _lastHostHash = 0;
        }

        private ushort HostIdFor(InteractablePackagingBox_Shelf box)
        {
            return _hostIdentity.GetOrAssign(box);
        }

        private static bool InGameLevel()
        {
            var gm = CSingleton<CGameManager>.Instance;
            return gm != null && gm.m_IsGameLevel;
        }

        // ---------------- patches ----------------

        public static void ApplyPatches(Harmony h)
        {
            // The joiner's unpack CONFIRM: PlaceMovedObject is the single funnel every
            // boxed object goes through (m_IsBoxedUp is still true at that moment and
            // only this call clears it), so one prefix catches every furniture kind.
            Try(h, typeof(InteractableObject), "PlaceMovedObject",
                prefix: new HarmonyMethod(typeof(FurnBoxSync), nameof(PlacePrefix)));

            // OnDestroyed IS an override on InteractablePackagingBox_Shelf (it kills
            // its boxed object and calls RestockManager.RemoveShelfPackageBox), so
            // patching it here catches ONLY furniture boxes - item and card boxes
            // keep their own modules' patches.
            Try(h, typeof(InteractablePackagingBox_Shelf), "OnDestroyed",
                prefix: new HarmonyMethod(typeof(FurnBoxSync), nameof(DestroyedPrefix)));
        }

        public static bool PlacePrefix(InteractableObject __instance)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote) return true;
            if (__instance == null || !__instance.GetIsBoxedUp()) return true; // ordinary move, not an unpack
            try
            {
                // vanilla no-ops on an invalid spot; match it exactly
                if (FiMovingValid != null && !(bool)FiMovingValid.GetValue(__instance)) return true;
                var box = __instance.GetPackagingBoxShelf();
                if (box == null) return true;
                Instance?.ClientPlace(__instance, box);
                return false; // forwarded + replayed locally under ApplyingRemote
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("FurnBoxSync place: " + e.Message);
                return true;
            }
        }

        public static bool DestroyedPrefix(InteractablePackagingBox_Shelf __instance)
        {
            // world-(re)load cleanup destroys are not player actions
            if (!ApplyingRemote && !CoopCore.ClientReloading) Instance?.OnLocalDestroyed(__instance);
            return true;
        }

        private static void Try(Harmony h, Type type, string method,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            try
            {
                var original = ReflectionSurface.RequiredMethod(type, method);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning($"Patch target missing: {type.Name}.{method}");
                    return;
                }
                h.Patch(original, prefix: prefix, postfix: postfix);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning($"Patch failed for {type.Name}.{method}: {e.Message}");
            }
        }

        // ---------------- host ----------------

        public void HostTick(float dt, bool inGame)
        {
            if (!inGame || Rm() == null) return;
            _carryPollTimer += dt;
            // carry transitions broadcast IMMEDIATELY - a box that still looks
            // on-the-floor invites another player to grab it too
            bool force = false;
            bool pollCarry = _carryPollTimer >= CarryPollInterval;
            if (pollCarry)
            {
                _carryPollTimer -= CarryPollInterval;
                if (_carryPollTimer > CarryPollInterval) _carryPollTimer = CarryPollInterval;
            }
            try
            {
                if (pollCarry)
                {
                    var scan = LiveBoxes();
                    for (int i = 0; i < scan.Count; i++)
                    {
                        var box = scan[i];
                        if (box == null) continue;
                        if (IsLocallyCarried(box))
                        {
                            if (_hostCarriedLastTick.Add(box)) force = true;
                        }
                        else if (_hostCarriedLastTick.Remove(box))
                        {
                            force = true;
                            _hostRecentlyReleased[box] = Time.realtimeSinceStartupAsDouble;
                        }
                    }
                }
            }
            catch { }
            _timer += dt;
            if (!force && _timer < Period) return;
            if (_timer >= Period) _timer -= Period;
            if (force) _lastHostHash = 0; // transitions bypass the unchanged-gate
            try
            {
                var boxes = LiveBoxes();
                var list = new List<Entry>(Mathf.Min(boxes.Count, MaxBoxes));
                for (int i = 0; i < boxes.Count && list.Count < MaxBoxes; i++)
                {
                    var box = boxes[i];
                    if (box == null) continue;
                    var obj = BoxedObject(box);
                    if (obj == null) continue; // mid-unpack this frame; next tick has truth
                    if (!TryFindObjKey(obj, out byte kind, out int objIdx)) continue;
                    var boxPos = BoxSync.PhysicsPosition(box);
                    if (!IsSanePose(boxPos))
                    {
                        CoopPlugin.Log.LogWarning($"FurnBoxSync host: refusing invalid delivery-box pose for {obj.m_ObjectType}: {boxPos}");
                        continue;
                    }
                    list.Add(new Entry
                    {
                        Id = HostIdFor(box),
                        WireType = (int)obj.m_ObjectType,
                        NameHash = Fnv(obj.m_ObjectType.ToString()),
                        Kind = kind,
                        ObjIndex = objIdx,
                        Pos = boxPos,
                        Yaw = BoxSync.PhysicsRotation(box).eulerAngles.y,
                        Carried = IsLocallyCarried(box) || _remoteCarried.Contains(box),
                        OwnerKind = IsLocallyCarried(box) ? (byte)1 : (_remoteCarried.Contains(box) ? (byte)2 : (byte)0),
                    });
                }
                // skip identical snapshots (deliveries sit still until collected); a
                // slow heal broadcast still repairs any client that missed one
                int hash = 17;
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    hash = hash * 31 + e.WireType;
                    hash = hash * 31 + (e.Kind << 16 | (e.ObjIndex & 0xFFFF));
                    hash = hash * 31 + (e.Carried ? 1 : 0);
                    hash = hash * 31 + e.OwnerKind;
                    hash = hash * 31 + (int)(e.Pos.x * 8f);
                    hash = hash * 31 + (int)(e.Pos.z * 8f);
                }
                _hostHeal += Period;
                if (hash == _lastHostHash && _hostHeal < 10f) return;
                _lastHostHash = hash;
                _hostHeal = 0f;
                var snap = list;
                var entries = new List<FurnBoxEntry>(snap.Count);
                for (int i = 0; i < snap.Count; i++) entries.Add(ToWire(snap[i]));
                BroadcastState?.Invoke(new FurnBoxStateMessage { Entries = entries });
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("FurnBoxSync host: " + e.Message); }
        }

        /// <summary>Host: dispatch a client op (report / place / removed).</summary>
        public void HostApplyOp(FurnBoxOpMessage message, int connId)
        {
            if (CoopCore.Role != CoopRole.Host) return;
            byte kind = message.Op;
            switch (kind)
            {
                case OpReport: HostApplyReport(message); break;
                case OpPlace: HostApplyPlace(message); break;
                case OpRemoved: HostApplyRemoved(message, connId); break;
                default:
                    CoopPlugin.Log.LogWarning($"FurnBoxSync: unknown op {kind}");
                    break;
            }
        }

        private void HostApplyReport(FurnBoxOpMessage message)
        {
            var report = message.Report;
            int n = Mathf.Min(report.Count, MaxBoxes);
            var boxes = LiveBoxes();
            double now = Time.realtimeSinceStartupAsDouble;
            for (int i = 0; i < n; i++)
            {
                var entry = report[i];
                ushort id = entry.Id;
                int wireType = entry.WireType;
                int nameHash = entry.NameHash;
                bool carried = entry.Carried;
                var pos = entry.Position;
                float yaw = entry.Yaw;
                if (!_hostById.TryGetValue(id, out var box) || box == null) continue;
                if (!IsSanePose(pos))
                {
                    CoopPlugin.Log.LogWarning($"FurnBoxSync host: rejected invalid client pose for box {id}: {pos}");
                    continue;
                }
                // the stable id is authoritative; type remains a corruption guard
                if (!BoxedTypeMatches(box, wireType, nameHash)) continue;
                if (IsLocallyCarried(box)) continue; // never stomp a box in the host's hands
                // just set down: a report the client built while we still carried it is
                // stale by definition - the race that teleported boxes mid-restock
                if (_hostRecentlyReleased.TryGetValue(box, out double rel) && now - rel < 6.0) continue;
                if (carried) _remoteCarried.Add(box);
                else _remoteCarried.Remove(box);
                ApplyToBox(box, pos, yaw, carried);
            }
            SweepOld(_hostRecentlyReleased, now);
            // fan the change out to everyone NOW - with 3+ players the other
            // clients otherwise wait out the periodic tick and the hash gate.
            // Exactly one period: a larger sentinel made the keep-the-phase
            // decrement fire EVERY frame for seconds (the post-throw jitter)
            _timer = Period;
            _lastHostHash = 0;
        }

        /// <summary>Host: the joiner confirmed an unpack placement. Validate the box
        /// still exists, then run the REAL PlaceMovedObject on the boxed object -
        /// subclass OnPlacedMovedObject overrides (play table tutorial hooks, nav cut
        /// refresh, box destroy via EmptyBoxShelf + OnDestroyed) all included - so
        /// PopulationSync/ObjMoveSync mirror the placed object to everyone.</summary>
        private void HostApplyPlace(FurnBoxOpMessage message)
        {
            ushort id = message.Id;
            int wireType = message.WireType;
            int nameHash = message.NameHash;
            var pos = message.Position;
            float yaw = message.Yaw;

            _hostById.TryGetValue(id, out var box);
            if (box != null && !BoxedTypeMatches(box, wireType, nameHash)) box = null;
            if (box == null)
            {
                CoopPlugin.Log.LogWarning("FurnBoxSync: place for unknown/mismatched box - ignored");
                return; // the joiner's guard expires and the echo re-boxes his copy; he retries
            }
            if (IsLocallyCarried(box)) return; // host is holding it: his hands win
            var obj = BoxedObject(box);
            if (obj == null) return;

            ApplyingRemote = true;
            try
            {
                PlaceFromBox(obj, pos, yaw, message.IsVertical ? message.Rotation : Quaternion.identity,
                    message.IsVertical, message.IsWarehouseWall, message.VerticalSnapWallIndex);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("FurnBoxSync place apply: " + e.Message); }
            finally { ApplyingRemote = false; }
            ForgetBox(box);
            _timer = Period; // exactly one period (see HostApplyReport)
            _lastHostHash = 0;
        }

        private void HostApplyRemoved(FurnBoxOpMessage message, int connId)
        {
            ushort id = message.Id;
            int wireType = message.WireType;
            int nameHash = message.NameHash;
            // shared budget with item/card boxes: a reloading client's world-teardown
            // echoes ALL THREE box lists as removals in one burst - and each furn-box
            // removal would take its boxed FURNITURE with it
            if (BoxSync.RemovalFlooded(connId, "furniture-box")) return;
            _hostById.TryGetValue(id, out var box);
            if (box != null && !BoxedTypeMatches(box, wireType, nameHash)) box = null;
            if (box == null || IsLocallyCarried(box)) return;
            // sell/trash: OnDestroyed with m_BoxedObject still set kills the furniture
            // too, exactly what the joiner's vanilla flow did on his side
            ApplyingRemote = true;
            try { box.OnDestroyed(); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("FurnBoxSync removal: " + e.Message); }
            finally { ApplyingRemote = false; }
            ForgetBox(box);
            _timer = Period;
            _lastHostHash = 0;
        }

        /// <summary>Resolve a box by index, falling back to a boxed-type scan when the
        /// index shifted - but only if the type is UNIQUE among live boxes (furniture
        /// types repeat, unlike card-box card lists).</summary>
        private static InteractablePackagingBox_Shelf FindBox(int idx, int wireType, int nameHash)
        {
            var boxes = LiveBoxes();
            if (idx >= 0 && idx < boxes.Count && boxes[idx] != null
                && BoxedTypeMatches(boxes[idx], wireType, nameHash))
                return boxes[idx];
            InteractablePackagingBox_Shelf found = null;
            for (int i = 0; i < boxes.Count; i++)
            {
                if (boxes[i] == null || !BoxedTypeMatches(boxes[i], wireType, nameHash)) continue;
                if (found != null) return null; // ambiguous - drop, the echo re-aligns
                found = boxes[i];
            }
            return found;
        }

        private void ForgetBox(InteractablePackagingBox_Shelf box)
        {
            if (box != null && _hostIds.TryGetValue(box, out var hostId))
            {
                _hostIds.Remove(box);
                _hostById.Remove(hostId);
            }
            if (box != null && _clientIdOf.TryGetValue(box, out var clientId))
            {
                _clientIdOf.Remove(box);
                _clientById.Remove(clientId);
            }
            _remoteCarried.Remove(box);
            _hostCarriedLastTick.Remove(box);
            _hostRecentlyReleased.Remove(box);
        }

        // ---------------- client ----------------

        /// <summary>Client: reconcile the live furniture-box population to the host's
        /// snapshot. The boxed object is resolved through PopulationSync's kind lists
        /// (it already exists here - PopulationSync mirrors it, hidden or not); boxing
        /// it up is the game's own save-load recipe (BoxUpObject(holdBox: false), the
        /// exact call ShelfManager's loader uses for isBoxed save entries).</summary>
        public void ClientApplyState(FurnBoxStateMessage message)
        {
            var entries = message.Entries;
            var hostList = new List<Entry>(entries.Count);
            for (int i = 0; i < entries.Count; i++) hostList.Add(ToEntry(entries[i]));
            ApplyingRemote = true;
            try { ClientApplyInner(hostList); }
            catch (Exception e) { CoopPlugin.Log.LogWarning("FurnBoxSync apply: " + e.Message); }
            finally { ApplyingRemote = false; }
        }

        private void ClientApplyInner(List<Entry> hostList)
        {
            if (Rm() == null || Sm() == null) return;
            double now = Time.realtimeSinceStartupAsDouble;

            // resolve every entry to its local object first; a failed resolve means
            // PopulationSync hasn't delivered that object yet - never treat anything
            // as an extra on a tick with partial knowledge
            bool allResolved = true;
            var wantedObjs = new List<InteractableObject>(hostList.Count);
            for (int i = 0; i < hostList.Count; i++)
            {
                var obj = ResolveEntryObject(hostList[i]);
                wantedObjs.Add(obj);
                // Every furniture object, including generic kind 15 objects, is
                // mirrored by PopulationSync. Never fabricate a second object+box
                // while waiting for that roster to arrive.
                if (obj == null) allResolved = false;
            }

            // vanished entries: replay what the host did. Last seen CARRIED means a
            // sell/trash (both only happen from the hands) - the object dies with the
            // box; last seen on the GROUND means an unpack - place the object where
            // the box stood and let ObjMoveSync pull it to the real spot
            // A full snapshot at the protocol ceiling is incomplete by definition;
            // never interpret entries beyond that ceiling as destroyed.
            bool truncated = hostList.Count >= MaxBoxes;
            if (allResolved && !truncated)
            {
                var live = LiveBoxes();
                for (int i = live.Count - 1; i >= 0; i--)
                {
                    var box = live[i];
                    if (box == null) continue;
                    var boxed = BoxedObject(box);
                    bool wanted = false;
                    for (int j = 0; j < wantedObjs.Count; j++)
                        if (ReferenceEquals(wantedObjs[j], boxed) && boxed != null) { wanted = true; break; }
                    if (wanted) continue;
                    bool lastCarried = _lastApplied.TryGetValue(box, out var lastE) && lastE.Carried;
                    try
                    {
                        if (boxed == null || lastCarried)
                        {
                            // if a player is holding this box, release hold-box mode first or
                            // OnDestroyed strands them in the soft-lock (no-op if not held)
                            try { CoopCore.ForceExitHoldBox(box); } catch { }
                            box.OnDestroyed(); // boxed object (if any) dies too - the sell
                        }
                        else
                        {
                            // Unpack inferred from the delivery box vanishing. Do NOT fabricate
                            // a placement rotation from the box's COSMETIC (random) spawn yaw -
                            // that placed furniture DIAGONALLY and then became the authoritative
                            // pose. Unbox in place, keeping the object's own (straight) rotation;
                            // PopulationSync + ObjMoveSync carry the real placed pose to us.
                            FiMovingValid?.SetValue(boxed, true);
                            boxed.gameObject.SetActive(true);
                            boxed.PlaceMovedObject();
                            ObjMoveSync.SyncTagGroup(boxed.transform);
                        }
                    }
                    catch (Exception e) { CoopPlugin.Log.LogWarning("FurnBoxSync retire: " + e.Message); }
                    _lastApplied.Remove(box);
                    _carriedLastTick.Remove(box);
                    _recentlyReleased.Remove(box);
                    _locallyTouched.Remove(box);
                }
            }

            // box up / update every wanted entry
            for (int i = 0; i < hostList.Count; i++)
            {
                var want = hostList[i];
                var obj = wantedObjs[i];
                if (obj == null)
                {
                    // PopulationSync owns creation of every placed object, including
                    // generic kind 15. Waiting is safe; spawning here created duplicate
                    // objects during the normal population/furniture ordering window.
                    continue;
                }
                var box = obj.GetIsBoxedUp() ? obj.GetPackagingBoxShelf() : null;
                if (box == null)
                {
                    // not boxed here yet - unless this is MY unpack the host hasn't
                    // confirmed, my mid-unpack move, or the shifted-index window after
                    // a local sell (same-type neighbor must not get boxed by mistake)
                    if (obj.GetIsMovingObject()) continue;
                    if (_recentlyUnpacked.TryGetValue(obj, out double up) && now - up < 6.0) continue;
                    if (now - _suppressBoxUp < 6.0) continue;
                    try
                    {
                        obj.BoxUpObject(holdBox: false); // vanilla recipe; registers the box itself
                        box = obj.GetPackagingBoxShelf();
                    }
                    catch (Exception e)
                    {
                        CoopPlugin.Log.LogWarning("FurnBoxSync boxup: " + e.Message);
                        continue;
                    }
                    if (box == null) continue; // e.g. play table vetoed the box-up; retry next tick
                }
                _clientById[want.Id] = box;
                _clientIdOf[box] = want.Id;
                // a box in MY hands is mine until I put it down; a box in the HOST's
                // hands has a transient position we don't copy
                if (IsLocallyCarried(box)) { _lastApplied[box] = want; continue; }
                // a stale "carried" echo about a box I JUST released must not hide it
                if (want.Carried && _recentlyReleased.TryGetValue(box, out double t) && now - t < 6.0)
                { _lastApplied[box] = want; continue; }
                // my own recent moves win over stale echoes; my report reaches the
                // host and the next echo agrees. VISIBILITY is exempt: someone else's
                // pickup/set-down must show here immediately, or their set-down box
                // stays invisible to me for the whole window
                if (_locallyTouched.TryGetValue(box, out double touched) && now - touched < 6.0)
                {
                    if (!want.Carried && !box.gameObject.activeSelf) ShowBox(box);
                    else if (want.Carried && box.gameObject.activeSelf) HideBox(box);
                    _lastApplied[box] = want;
                    continue;
                }
                ApplyToBox(box, want.Pos, want.Yaw, want.Carried);
                _lastApplied[box] = want;
            }
            SweepOld(_recentlyUnpacked, now);
        }

        /// <summary>Client: detect the local player's carried transitions and box moves
        /// and report them to the host (contents never change - a furniture box IS its
        /// object; unpack and sell travel as their own ops).</summary>
        public void ClientTick(float dt, bool inGame)
        {
            if (!inGame || Rm() == null || _lastApplied.Count == 0) return;
            // carry transitions are detected EVERY FRAME and reported immediately -
            // the periodic diff alone left pickups/set-downs invisible for seconds,
            // long enough for someone else to try grabbing the same box
            _carryPollTimer += dt;
            bool force = false;
            bool pollCarry = _carryPollTimer >= CarryPollInterval;
            if (pollCarry)
            {
                _carryPollTimer -= CarryPollInterval;
                if (_carryPollTimer > CarryPollInterval) _carryPollTimer = CarryPollInterval;
            }
            try
            {
                if (pollCarry)
                {
                    var scan = LiveBoxes();
                    for (int i = 0; i < scan.Count; i++)
                    {
                        var box = scan[i];
                        if (box == null) continue;
                        if (IsLocallyCarried(box))
                        {
                            BoxSync.CancelRemoteMotion(box);
                            if (_carriedLastTick.Add(box)) force = true; // pickup transition
                        }
                        else if (_carriedLastTick.Remove(box))
                        {
                            force = true; // set-down transition
                            _recentlyReleased[box] = Time.realtimeSinceStartupAsDouble;
                        }
                    }
                }
            }
            catch { }
            _timer += dt;
            if (!force && _timer < Period) return;
            if (_timer >= Period) _timer -= Period;
            try
            {
                var boxes = LiveBoxes();
                bool changed = force;
                double nowT = Time.realtimeSinceStartupAsDouble;
                _reportIds.Clear();
                _reportEntries.Clear();
                if (_reportIds.Capacity < boxes.Count) _reportIds.Capacity = boxes.Count;
                if (_reportEntries.Capacity < boxes.Count) _reportEntries.Capacity = boxes.Count;
                var idxList = _reportIds;
                var entryList = _reportEntries;
                for (int i = 0; i < boxes.Count && entryList.Count < MaxBoxes; i++)
                {
                    var box = boxes[i];
                    if (box == null || !_lastApplied.TryGetValue(box, out var last)) continue;
                    if (BoxSync.IsRemoteMotion(box)) continue; // don't echo interpolation as a guest move
                    Entry rep = last;
                    if (IsLocallyCarried(box))
                    {
                        // while I'M carrying it: tell the host (so everyone else hides
                        // their copy) but keep reporting the last settled position
                        rep.Carried = true;
                    }
                    else if (last.Carried
                        && !(_recentlyReleased.TryGetValue(box, out double rr) && nowT - rr < 6.0))
                    {
                        // hidden because ANOTHER player carries it: no local truth to
                        // report (its transform is parked at the hide spot) - UNLESS I
                        // just set it down myself: that report IS the set-down, and
                        // skipping it deadlocks the box as carried-forever
                    }
                    else
                    {
                        rep.Carried = false;
                        rep.Pos = BoxSync.PhysicsPosition(box);
                        rep.Yaw = BoxSync.PhysicsRotation(box).eulerAngles.y;
                        if ((rep.Pos - last.Pos).sqrMagnitude > 0.01f
                            || Mathf.Abs(Mathf.DeltaAngle(rep.Yaw, last.Yaw)) > 3f
                            || rep.Carried != last.Carried)
                        {
                            changed = true;
                            _locallyTouched[box] = nowT;
                        }
                    }
                    if (!_clientIdOf.TryGetValue(box, out var reportId)) continue;
                    idxList.Add(reportId);
                    entryList.Add(rep);
                }
                if (changed && SendOp != null)
                {
                    var report = new List<FurnBoxReportEntry>(entryList.Count);
                    for (int i = 0; i < entryList.Count; i++)
                    {
                        var e = entryList[i];
                        report.Add(new FurnBoxReportEntry
                        {
                            Id = idxList[i],
                            WireType = e.WireType,
                            NameHash = e.NameHash,
                            Carried = e.Carried,
                            Position = e.Pos,
                            Yaw = e.Yaw,
                        });
                    }
                    SendOp(new FurnBoxOpMessage { Op = OpReport, Report = report });
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("FurnBoxSync client: " + e.Message); }
        }

        /// <summary>Client: the joiner confirmed placement of an unpacked furniture
        /// mirror. Forward the place op, then run the vanilla placement locally under
        /// ApplyingRemote (the box's OnDestroyed must not double as a sell op) and
        /// shield the object from the pre-confirm state echo.</summary>
        private void ClientPlace(InteractableObject obj, InteractablePackagingBox_Shelf box)
        {
            bool hasId = _clientIdOf.TryGetValue(box, out ushort boxId);
            if (SendOp == null)
                CoopPlugin.Log.LogWarning("FurnBoxSync: no host link, placing locally only");
            else if (!hasId) return;

            // Replay vanilla first. For vertical furniture this is important: the game
            // determines the locked-room wall and snap index in OnPlacedMovedObject from
            // the last wall hit. The transform is also captured after vanilla has finished
            // its final wall-facing calculation, rather than flattening every object to y=0.
            bool isVertical = obj.m_IsDecorationVertical;
            int wireType = _lastApplied.TryGetValue(box, out var last)
                ? last.WireType : (int)obj.m_ObjectType; // echo the HOST's int back
            _recentlyUnpacked[obj] = Time.realtimeSinceStartupAsDouble;
            _lastApplied.Remove(box);
            _clientIdOf.Remove(box);
            if (hasId) _clientById.Remove(boxId);
            _carriedLastTick.Remove(box);
            _recentlyReleased.Remove(box);
            _locallyTouched.Remove(box);

            ApplyingRemote = true;
            try { obj.PlaceMovedObject(); }
            finally { ApplyingRemote = false; }

            if (SendOp != null)
            {
                var pos = obj.transform.position;
                var rotation = obj.transform.rotation;
                if (!isVertical) pos.y = 0f; // retain vanilla floor semantics explicitly
                SendOp(new FurnBoxOpMessage
                {
                    Op = OpPlace,
                    Id = boxId,
                    WireType = wireType,
                    NameHash = Fnv(obj.m_ObjectType.ToString()),
                    Position = pos,
                    Yaw = rotation.eulerAngles.y,
                    IsVertical = isVertical,
                    Rotation = rotation,
                    IsWarehouseWall = obj.GetIsVerticalSnapToWarehouseWall(),
                    VerticalSnapWallIndex = obj.GetVerticalSnapWallIndex(),
                });
            }
        }

        /// <summary>A furniture box died to LOCAL gameplay (sell confirm / trash bin),
        /// not reconciliation. On the host the next broadcast carries it; the client
        /// tells the host so the real box (and its furniture) dies too.</summary>
        private void OnLocalDestroyed(InteractablePackagingBox_Shelf box)
        {
            if (!InGameLevel()) return;
            if (CoopCore.Role == CoopRole.Host)
            {
                ForgetBox(box);
                return;
            }
            if (CoopCore.Role != CoopRole.Client) return;
            var obj = BoxedObject(box);
            if (!_clientIdOf.TryGetValue(box, out ushort boxId)) return;
            _lastApplied.TryGetValue(box, out var last);
            _lastApplied.Remove(box);
            _clientIdOf.Remove(box);
            _clientById.Remove(boxId);
            _carriedLastTick.Remove(box);
            _recentlyReleased.Remove(box);
            _locallyTouched.Remove(box);
            // an unpack destroys the box AFTER EmptyBoxShelf (m_BoxedObject null) and
            // is forwarded by the place op instead - only a real sell/trash goes here
            if (obj == null) return;
            // the furniture dies with the box: its kind list shifts, so a stale echo
            // could resolve onto the same-type NEIGHBOR - no new box-ups for a while
            _suppressBoxUp = Time.realtimeSinceStartupAsDouble;
            int wireType = last.WireType != 0 ? last.WireType : (int)obj.m_ObjectType;
            int nameHash = Fnv(obj.m_ObjectType.ToString());
            SendOp?.Invoke(new FurnBoxOpMessage
            {
                Op = OpRemoved,
                Id = boxId,
                WireType = wireType,
                NameHash = nameHash,
            });
        }

        // ---------------- shared apply ----------------

        /// <summary>Carried visibility + position, BoxSync's ApplyToBox for shelf boxes.
        /// The box's price tags live in a separate canvas group only dragged along by
        /// the box's own LateUpdate - hide them WITH the box or they strand mid-air.</summary>
        private static void ApplyToBox(InteractablePackagingBox_Shelf box, Vector3 pos, float yaw, bool carried)
        {
            try
            {
                if (carried)
                {
                    HideBox(box);
                    return;
                }
                if (!IsSanePose(pos))
                {
                    CoopPlugin.Log.LogWarning($"FurnBoxSync: ignored invalid remote delivery-box pose {pos}");
                    return;
                }
                ShowBox(box);
                var currentPos = BoxSync.PhysicsPosition(box);
                var currentYaw = BoxSync.PhysicsRotation(box).eulerAngles.y;
                if ((currentPos - pos).sqrMagnitude > 0.01f
                    || Mathf.Abs(Mathf.DeltaAngle(currentYaw, yaw)) > 3f)
                {
                    BoxSync.ScheduleRemoteMotion(box, pos, yaw);
                    try
                    {
                        // a sleeping rigidbody teleported mid-air hangs there frozen. Use the
                        // real body (m_Rigidbody), not GetComponentInChildren (which can return
                        // the kinematic rig-mesh child and skip the wake -> frozen floating box).
                        var rb = box.m_Rigidbody;
                        if (rb != null && !rb.isKinematic)
                        {
                            rb.velocity = Vector3.zero;
                            rb.angularVelocity = Vector3.zero;
                            rb.WakeUp();
                        }
                    }
                    catch { }
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("FurnBoxSync apply: " + e.Message); }
        }

        private static void HideBox(InteractablePackagingBox_Shelf box)
        {
            if (!box.gameObject.activeSelf) return;
            try { box.m_ItemCompartment.SetPriceTagVisibility(false); } catch { }
            box.gameObject.SetActive(false);
        }

        private static void ShowBox(InteractablePackagingBox_Shelf box)
        {
            if (box.gameObject.activeSelf) return;
            box.gameObject.SetActive(true);
            try { box.m_ItemCompartment.SetPriceTagVisibility(true); } catch { }
        }

        /// <summary>Run the vanilla placement on a still-boxed object without a player
        /// in move mode: activate it (OnPressOpenBox's job), pass the validity gate,
        /// then the REAL PlaceMovedObject so every subclass override runs and the box
        /// retires itself (EmptyBoxShelf + OnDestroyed). Caller holds ApplyingRemote.</summary>
        private static void PlaceFromBox(InteractableObject obj, Vector3 pos, float yaw,
            Quaternion rotation, bool isVertical, bool isWarehouseWall, int verticalSnapWallIndex)
        {
            obj.gameObject.SetActive(true);
            obj.transform.SetPositionAndRotation(pos,
                isVertical ? rotation : Quaternion.Euler(0f, yaw, 0f));
            FiMovingValid?.SetValue(obj, true);
            obj.PlaceMovedObject();
            // A headless remote placement has no raycast hit transform, so vanilla's
            // vertical OnPlacedMovedObject branch cannot populate the locked-room
            // blocker/index. Apply the state captured from the client's real placement.
            if (isVertical)
                obj.SetVerticalSnapToWarehouseWall(isWarehouseWall, verticalSnapWallIndex);
            // the counter's world screens are re-shown only by the mover's
            // OnStartMoveObject, which a headless placement never runs
            if (obj is InteractableCashierCounter)
            {
                try
                {
                    (FiCounterScreen?.GetValue(obj) as Component)?.gameObject.SetActive(true);
                    (FiCreditScreen?.GetValue(obj) as Component)?.gameObject.SetActive(true);
                }
                catch { }
            }
            ObjMoveSync.SyncTagGroup(obj.transform);
        }

        // ---------------- identity ----------------

        private static InteractableObject BoxedObject(InteractablePackagingBox_Shelf box)
        {
            try { return FiBoxedObject?.GetValue(box) as InteractableObject; }
            catch { return null; }
        }

        private static bool BoxedTypeMatches(InteractablePackagingBox_Shelf box, int wireType, int nameHash)
        {
            var obj = BoxedObject(box);
            if (obj == null) return false;
            if ((int)obj.m_ObjectType == wireType) return true;
            return Fnv(obj.m_ObjectType.ToString()) == nameHash; // modded int drift
        }

        /// <summary>Locate the boxed object in PopulationSync's kind lists (the shared
        /// "kind, index" scheme every other sync uses), falling back to ShelfManager's
        /// generic m_InteractableObjectList. The kind never changes per object; only
        /// the index is re-read each snapshot.</summary>
        private bool TryFindObjKey(InteractableObject obj, out byte kind, out int idx)
        {
            kind = Unresolved;
            idx = -1;
            var sm = Sm();
            if (sm == null) return false;
            if (_kindCache.TryGetValue(obj, out byte cached))
            {
                var list = KindList(sm, cached);
                int i = list?.IndexOf(obj) ?? -1;
                if (i >= 0)
                {
                    if (CoopCore.Role == CoopRole.Host) idx = PlacedObjectIdentity.AssignHost(obj);
                    else if (!PlacedObjectIdentity.TryGet(obj, out ushort knownId)) return false;
                    else idx = knownId;
                    kind = cached;
                    return true;
                }
                _kindCache.Remove(obj); // moved lists? rescan below
            }
            for (byte k = 0; k <= GenericKind; k++)
            {
                var list = KindList(sm, k);
                int i = list?.IndexOf(obj) ?? -1;
                if (i < 0) continue;
                if (CoopCore.Role == CoopRole.Host) idx = PlacedObjectIdentity.AssignHost(obj);
                else if (!PlacedObjectIdentity.TryGet(obj, out ushort knownId)) continue;
                else idx = knownId;
                _kindCache[obj] = k;
                kind = k;
                return true;
            }
            return false;
        }

        private InteractableObject ResolveEntryObject(Entry e)
        {
            if (e.Kind == Unresolved) return null;
            var sm = Sm();
            if (sm == null) return null;
            var list = KindList(sm, e.Kind);
            if (list == null || e.ObjIndex <= 0 || e.ObjIndex > ushort.MaxValue) return null;
            if (!PlacedObjectIdentity.TryResolve(sm, e.Kind, (ushort)e.ObjIndex, out var obj)) return null;
            // type gate: the roster may be mid-repair; boxing the wrong object is the
            // one mistake this module must never make
            int localType = ResolveObjType(e.WireType, e.NameHash);
            if ((int)obj.m_ObjectType != localType) return null;
            return obj;
        }

        private static IList KindList(ShelfManager sm, byte kind)
        {
            if (kind < GenericKind) return PopulationSync.GetList(sm, kind);
            if (kind == GenericKind) return FiGenericList?.GetValue(sm) as IList;
            return null;
        }

        /// <summary>Map a wire (int, nameHash) type to the LOCAL EObjectType int. The
        /// int matches directly when both machines run the same mods in the same order;
        /// otherwise the enum NAME finds it (license-sync philosophy).</summary>
        private int ResolveObjType(int wireType, int nameHash)
        {
            if (Fnv(((EObjectType)wireType).ToString()) == nameHash) return wireType;
            if (_nameToType == null)
            {
                _nameToType = new Dictionary<int, int>();
                foreach (var v in Enum.GetValues(typeof(EObjectType)))
                    _nameToType[Fnv(v.ToString())] = (int)v;
            }
            return _nameToType.TryGetValue(nameHash, out int local) ? local : wireType;
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

        private static void SweepOld<TKey>(Dictionary<TKey, double> dict, double now)
        {
            if (dict.Count == 0) return;
            List<TKey> dead = null;
            foreach (var kv in dict)
                if (now - kv.Value > 12.0) (dead ?? (dead = new List<TKey>())).Add(kv.Key);
            if (dead != null) foreach (var k in dead) dict.Remove(k);
        }

        // ---------------- wire ----------------

        private static Entry ToEntry(FurnBoxEntry e)
        {
            return new Entry
            {
                Id = e.Id,
                WireType = e.WireType,
                NameHash = e.NameHash,
                Kind = e.Kind,
                ObjIndex = e.ObjIndex,
                Pos = e.Position,
                Yaw = e.Yaw,
                Carried = e.Carried,
                OwnerKind = e.OwnerKind,
            };
        }

        private static FurnBoxEntry ToWire(Entry e)
        {
            return new FurnBoxEntry
            {
                Id = e.Id,
                WireType = e.WireType,
                NameHash = e.NameHash,
                Kind = e.Kind,
                ObjIndex = e.ObjIndex,
                Position = e.Pos,
                Yaw = e.Yaw,
                Carried = e.Carried,
                OwnerKind = e.OwnerKind,
            };
        }

    }
}
