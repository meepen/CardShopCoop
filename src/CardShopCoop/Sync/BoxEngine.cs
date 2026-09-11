using System;
using System.Collections.Generic;
using CardShopCoop.Net.Messages;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// The one box engine. It owns identity, the possession lease, the host snapshot,
    /// the client apply, and the local-possession reporting. It knows nothing about
    /// item/card/furniture content - that lives in the <see cref="IBoxFamily"/>.
    ///
    /// Protocol: a box is Free, Held, Placing, or Removed. A BoxUpdate is accepted iff
    /// the box is unowned or the sender already owns it. Held/Placing keep the lease;
    /// Free/Removed release it. The host owns Free pose/velocity and rebroadcasts it.
    ///
    /// Snapshots are partial by default: the host emits only the boxes whose state hash
    /// changed, plus an explicit Removed for retired ids. A complete snapshot is sent at
    /// session start and on explicit request (join/resync); the reliable partial stream
    /// carries every subsequent change, so there is no periodic full scan. Both scans are
    /// time-sliced (round-robin over a small budget per flush), with a fast path for the
    /// actively held/placing box so interaction stays responsive.
    /// </summary>
    public class BoxEngine
    {
        private const int MaxBoxes = 1000;
        private const float PartialPeriod = 0.10f;  // host: how often changed boxes flush
        private const float LeaseTimeout = 3.0f;
        private const float LeaseRenewPeriod = 1.0f;
        private const float ContentReportPeriod = 0.2f;
        private const int ClientScanBudget = 16;    // boxes checked per frame (round-robin)
        private const int HostScanBudget = 24;      // boxes checked per partial flush

        private const int HostConn = 0;
        private const int NoOwner = -1;

        private struct Lease
        {
            public int Owner;                 // HostConn / connId / NoOwner
            public BoxPossession Possession;
            public float LastSeen;
        }

        private readonly List<IBoxFamily> _families;
        private readonly BoxIdentityMap<InteractablePackagingBox> _hostIdentity
            = new BoxIdentityMap<InteractablePackagingBox>();
        private readonly Dictionary<ushort, Lease> _leases = new Dictionary<ushort, Lease>();

        // ---- host snapshot state ----
        private readonly Dictionary<ushort, int> _hostHashes = new Dictionary<ushort, int>();
        private readonly HashSet<ushort> _hostDirty = new HashSet<ushort>();
        private readonly Dictionary<ushort, BoxFamily> _hostPendingRemoved = new Dictionary<ushort, BoxFamily>();
        private readonly List<ushort> _deadHost = new List<ushort>();
        private int _hostFam;
        private int _hostIdx;
        // Reused snapshot buffers. Broadcast serializes synchronously, so a buffer can be
        // refilled on the next flush.
        private readonly List<BoxWire> _hostList = new List<BoxWire>(256);
        private readonly List<BoxWire> _heldList = new List<BoxWire>(1);
        private bool _fullPending; // next flush is a complete snapshot (session start / join / resync)

        // ---- client state ----
        private readonly Dictionary<ushort, InteractablePackagingBox> _clientById
            = new Dictionary<ushort, InteractablePackagingBox>();
        private readonly Dictionary<InteractablePackagingBox, ushort> _clientIdOf
            = new Dictionary<InteractablePackagingBox, ushort>();
        private readonly Dictionary<InteractablePackagingBox, BoxPossession> _reported
            = new Dictionary<InteractablePackagingBox, BoxPossession>();
        private readonly Dictionary<InteractablePackagingBox, int> _reportedContent
            = new Dictionary<InteractablePackagingBox, int>();
        private readonly Dictionary<InteractablePackagingBox, float> _contentSentAt
            = new Dictionary<InteractablePackagingBox, float>();
        private readonly Dictionary<InteractablePackagingBox, float> _lastSentAt
            = new Dictionary<InteractablePackagingBox, float>();
        private readonly HashSet<InteractablePackagingBox> _active = new HashSet<InteractablePackagingBox>();
        private readonly List<InteractablePackagingBox> _activeScratch = new List<InteractablePackagingBox>();
        private readonly HashSet<InteractablePackagingBox> _clientDirty = new HashSet<InteractablePackagingBox>();
        private readonly HashSet<ushort> _snapshotIds = new HashSet<ushort>();
        private readonly List<ushort> _sweep = new List<ushort>();
        // Client-side ids this machine locally retired (place/collect/sell). The host's
        // in-flight snapshot may still list them; we skip and hold until it agrees. Cleared
        // when a full snapshot omits the id (no time window).
        private readonly HashSet<ushort> _localRemoved = new HashSet<ushort>();
        private int _clientFam;
        private int _clientIdx;

        private float _hostTimer;
        private float _leaseClock;

        /// <summary>Host: broadcast a snapshot (Full flag distinguishes complete vs partial).</summary>
        public Action<BoxSnapshotMessage> SendSnapshot;
        /// <summary>Client: send one possession update to the host.</summary>
        public Action<BoxUpdateMessage> SendUpdate;
        /// <summary>Client: a host snapshot just spawned this local box mirror. ContainerSync
        /// uses it to claim an acknowledged empty-box take the moment the box appears.</summary>
        public Action<InteractablePackagingBox> OnClientBoxSpawned;
        /// <summary>Wired by CoopCore: the box the LOCAL player is currently holding, if any.
        /// Processed every tick so a host/client pickup or drop emits on the same frame
        /// instead of waiting for the round-robin slice to reach it.</summary>
        public Func<InteractablePackagingBox> LocalHeld;

        public BoxEngine(IEnumerable<IBoxFamily> families)
        {
            _families = new List<IBoxFamily>(families);
        }

        public bool TryGetHostBox(ushort id, out InteractablePackagingBox box)
        {
            return _hostIdentity.ById.TryGetValue(id, out box);
        }

        public ushort EnsureHostId(InteractablePackagingBox box)
        {
            return _hostIdentity.GetOrAssign(box);
        }

        public void ForgetHostBox(ushort id)
        {
            // Emit an explicit Removed so partial-snapshot clients retire it immediately
            // (a full sweep still catches anything missed).
            BoxFamily family = BoxFamily.Item;
            if (_hostIdentity.ById.TryGetValue(id, out var box) && box != null)
                family = FamilyOf(box);
            if (!_hostPendingRemoved.ContainsKey(id))
                _hostPendingRemoved[id] = family;

            // Use the id overload: it removes the box->id side too even when the box is a
            // destroyed Unity object (a fake-null reference is not a C# null).
            _hostIdentity.Remove(id);
            _leases.Remove(id);
            _hostHashes.Remove(id);
            _hostDirty.Remove(id);
        }

        /// <summary>Host: drop ids for boxes that were destroyed without a Removed edge
        /// (scene teardown, a destroy path that bypassed the op).</summary>
        private void PruneDeadHostBoxes()
        {
            _deadHost.Clear();
            foreach (var kv in _hostIdentity.ById)
                if (kv.Value == null) // Unity fake-null
                    _deadHost.Add(kv.Key);
            for (int i = 0; i < _deadHost.Count; i++)
                ForgetHostBox(_deadHost[i]);
        }

        public bool TryGetClientBox(ushort id, out InteractablePackagingBox box)
        {
            return _clientById.TryGetValue(id, out box);
        }

        public bool TryGetClientId(InteractablePackagingBox box, out ushort id)
        {
            return _clientIdOf.TryGetValue(box, out id);
        }

        /// <summary>Client: this machine retired the box locally (place/collect/sell).
        /// Suppress the host's in-flight snapshot for that id until it agrees.</summary>
        public void ForgetClientBox(ushort id)
        {
            if (_clientById.TryGetValue(id, out var box))
                _clientIdOf.Remove(box); // works for a fake-null reference too (no C# null-key)
            _clientById.Remove(id);
            _localRemoved.Add(id);
        }

        /// <summary>Client: this machine destroyed a box through local gameplay (trash,
        /// storage). Tell the host to retire the real one and stop tracking the mirror.</summary>
        public void ClientNotifyLocalDestroyed(InteractablePackagingBox box)
        {
            if (box == null || !_clientIdOf.TryGetValue(box, out ushort id))
                return;
            SendUpdate?.Invoke(new BoxUpdateMessage
            {
                Box = new BoxWire
                {
                    Id = id,
                    Family = FamilyOf(box),
                    Possession = BoxPossession.Removed,
                },
            });
            ForgetClientBox(id);
        }

        /// <summary>Host: the host player destroyed a box locally - drop its identity so it
        /// is never re-broadcast and the id is not leaked.</summary>
        public void HostNotifyLocalDestroyed(InteractablePackagingBox box)
        {
            if (box == null || !_hostIdentity.TryGetId(box, out ushort id))
                return;
            ForgetHostBox(id);
            ForceNextTick();
        }

        /// <summary>Host: atomically consume a loose item box for an empty-box storage
        /// operation. The caller increments storage only when this succeeds.</summary>
        public bool HostConsumeForEmptyBoxStorage(ushort id, int expectedType, bool expectedBig)
        {
            if (CoopCore.Role != CoopRole.Host)
                return false;
            if (!_hostIdentity.ById.TryGetValue(id, out var baseBox)
                || !(baseBox is InteractablePackagingBox_Item box))
                return false;
            var family = Family(BoxFamily.Item);
            if (family == null
                || !family.TryReadLocal(box, out var poss, out _, out _, out _, out _, out _)
                || poss != BoxPossession.Free)
                return false;
            try
            {
                if (box.m_IsBigBox != expectedBig)
                    return false;
                if ((int)box.m_ItemCompartment.GetItemType() != expectedType
                    || box.m_ItemCompartment.GetItemCount() != 0)
                    return false;

                CoopPlugin.Log.LogInfo($"BoxEngine: host consuming empty box id {id} for storage");
                BoxShared.ApplyingRemote = true;
                try
                {
                    ItemBoxFamily.UnhookIfStored(box);
                    box.OnDestroyed();
                }
                finally { BoxShared.ApplyingRemote = false; }

                ForgetHostBox(id);
                ForceNextTick();
                return true;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("BoxEngine storage consume: " + e.Message);
                return false;
            }
        }

        public void Reset()
        {
            _hostIdentity.Clear();
            _leases.Clear();
            _hostHashes.Clear();
            _hostDirty.Clear();
            _hostPendingRemoved.Clear();
            _hostFam = 0;
            _hostIdx = 0;
            _clientById.Clear();
            _clientIdOf.Clear();
            _localRemoved.Clear();
            _reported.Clear();
            _reportedContent.Clear();
            _contentSentAt.Clear();
            _lastSentAt.Clear();
            _active.Clear();
            _clientDirty.Clear();
            _clientFam = 0;
            _clientIdx = 0;
            _hostTimer = 0f;
            _fullPending = true; // send a full snapshot on the first tick of a session
            _leaseClock = 0f;
            BoxPlacement.Reset();
            BoxVisuals.Reset();
        }

        /// <summary>Ask for a prompt host flush (partial).</summary>
        public void ForceNextTick()
        {
            _hostTimer = PartialPeriod;
        }

        /// <summary>Host: mark a specific box to be emitted on the next partial flush
        /// (e.g. an imminent throw that must go out as soon as its velocity is real).</summary>
        public void MarkBoxDirty(InteractablePackagingBox box)
        {
            if (box == null)
                return;
            if (CoopCore.Role == CoopRole.Host)
            {
                if (_hostIdentity.TryGetId(box, out ushort id))
                    _hostDirty.Add(id);
            }
            else if (CoopCore.Role == CoopRole.Client && _clientIdOf.ContainsKey(box))
            {
                _clientDirty.Add(box);
            }
        }

        /// <summary>Host: force the next flush to be a complete snapshot (join/resync).</summary>
        public void RequestFullSnapshot()
        {
            _fullPending = true;
        }

        // ---------------- host ----------------

        public void HostTick(float dt, bool active)
        {
            if (!active)
                return;
            _leaseClock += dt;
            // Forced path: the box the host player is holding must emit on the same frame,
            // not when the round-robin slice happens to reach it.
            ProcessLocalHeldHost();
            _hostTimer += dt;
            bool full = _fullPending;
            if (!full && _hostTimer < PartialPeriod)
                return;
            _hostTimer = 0f;
            if (full)
                _fullPending = false;

            ExpireLeases();
            PruneDeadHostBoxes();

            var list = _hostList;
            list.Clear();
            if (full)
            {
                // Clearing the hashes makes every live box "changed", so the scan emits a
                // complete list; ProcessHostBox rebuilds the per-id hashes as it goes.
                _hostHashes.Clear();
                ScanHostAll(list);
                _hostDirty.Clear();
                _hostPendingRemoved.Clear();
                _hostFam = 0;
                _hostIdx = 0;
                SendSnapshot?.Invoke(new BoxSnapshotMessage { Boxes = list, Full = true });
                return;
            }

            ScanHostDirty(list);
            ScanHostSlice(list);
            if (_hostPendingRemoved.Count > 0)
            {
                foreach (var kv in _hostPendingRemoved)
                    list.Add(new BoxWire { Id = kv.Key, Family = kv.Value, Possession = BoxPossession.Removed });
                _hostPendingRemoved.Clear();
            }
            if (list.Count > 0)
                SendSnapshot?.Invoke(new BoxSnapshotMessage { Boxes = list, Full = false });
        }

        /// <summary>Full scan: emit every live box (session start / explicit resync).</summary>
        private void ScanHostAll(List<BoxWire> list)
        {
            for (int f = 0; f < _families.Count; f++)
            {
                var family = _families[f];
                var boxes = family.LiveBoxes();
                if (boxes == null)
                    continue;
                for (int i = 0; i < boxes.Count && list.Count < MaxBoxes; i++)
                    ProcessHostBox(family, boxes[i], list);
            }
        }

        private void ScanHostDirty(List<BoxWire> list)
        {
            if (_hostDirty.Count == 0)
                return;
            _deadHost.Clear();
            foreach (var id in _hostDirty)
                _deadHost.Add(id);
            _hostDirty.Clear();
            for (int i = 0; i < _deadHost.Count; i++)
            {
                if (_hostIdentity.ById.TryGetValue(_deadHost[i], out var box) && box != null)
                    ProcessHostBox(Family(FamilyOf(box)), box, list);
            }
        }

        private void ScanHostSlice(List<BoxWire> list)
        {
            int budget = HostScanBudget;
            int guard = 0;
            while (budget > 0 && _families.Count > 0 && guard++ < 64 && list.Count < MaxBoxes)
            {
                if (_hostFam >= _families.Count)
                {
                    _hostFam = 0;
                    _hostIdx = 0;
                    break;
                }
                var family = _families[_hostFam];
                var boxes = family.LiveBoxes();
                if (boxes == null || boxes.Count == 0)
                {
                    _hostFam++;
                    _hostIdx = 0;
                    continue;
                }
                if (_hostIdx >= boxes.Count)
                {
                    _hostFam++;
                    _hostIdx = 0;
                    continue;
                }
                int take = Mathf.Min(budget, boxes.Count - _hostIdx);
                for (int k = 0; k < take && list.Count < MaxBoxes; k++)
                    ProcessHostBox(family, boxes[_hostIdx + k], list);
                _hostIdx += take;
                budget -= take;
                if (_hostIdx >= boxes.Count)
                {
                    _hostFam++;
                    _hostIdx = 0;
                }
            }
        }

        private void ProcessLocalHeldHost()
        {
            if (LocalHeld == null)
                return;
            var held = LocalHeld();
            if (held == null)
                return;
            var family = Family(FamilyOf(held));
            if (family == null)
                return;
            var one = _heldList;
            one.Clear();
            ProcessHostBox(family, held, one);
            if (one.Count > 0)
                SendSnapshot?.Invoke(new BoxSnapshotMessage { Boxes = one, Full = false });
        }

        private void ProcessHostBox(IBoxFamily family, InteractablePackagingBox box, List<BoxWire> list)
        {
            if (family == null || box == null)
                return;
            if (!family.TryReadLocal(box, out var localPoss, out var pos, out var yaw,
                    out var vel, out var angVel, out var stored))
                return;
            ushort id = _hostIdentity.GetOrAssign(box);
            if (id == 0)
                return;
            if (BoxPlacement.IsThrowPending(box))
            {
                if (localPoss != BoxPossession.Free)
                    BoxPlacement.ClearThrow(box); // picked back up before the throw was reported
                else if (!BoxPlacement.ThrowReady(box, vel))
                {
                    _hostDirty.Add(id); // retry next flush, once the impulse is integrated
                    return;
                }
                else
                    BoxPlacement.ClearThrow(box);
            }

            Lease lease = _leases.TryGetValue(id, out var l) ? l : default(Lease);
            bool clientOwned = lease.Owner != NoOwner && lease.Owner != HostConn;
            BoxPossession poss;
            int owner;
            if (clientOwned)
            {
                poss = lease.Possession;
                owner = lease.Owner;
            }
            else
            {
                poss = localPoss;
                owner = localPoss == BoxPossession.Held || localPoss == BoxPossession.Placing
                    ? HostConn : NoOwner;
                lease.Owner = owner;
                lease.Possession = localPoss;
                lease.LastSeen = _leaseClock;
                _leases[id] = lease;
            }

            var w = new BoxWire
            {
                Id = id,
                Family = family.Family,
                Possession = poss,
                OwnerConn = owner,
            };
            if (poss == BoxPossession.Free)
            {
                w.Pos = pos;
                w.Yaw = yaw;
                w.Velocity = vel;
                w.AngularVelocity = angVel;
            }
            // FillContent also fills the Free+stored rack address and the furniture Unpack flag.
            family.FillContent(box, ref w);

            int hash = HostHash(w);
            if (!_hostHashes.TryGetValue(id, out var old) || old != hash)
            {
                _hostHashes[id] = hash;
                list.Add(w);
            }
        }

        private static int HostHash(in BoxWire w)
        {
            int h = 17;
            h = h * 31 + w.Id;
            h = h * 31 + (int)w.Possession;
            h = h * 31 + w.OwnerConn;
            h = h * 31 + (w.Unpack ? 1 : 0);
            if (w.Possession == BoxPossession.Free)
            {
                h = h * 31 + Mathf.RoundToInt(w.Pos.x * 8f);
                h = h * 31 + Mathf.RoundToInt(w.Pos.y * 8f);
                h = h * 31 + Mathf.RoundToInt(w.Pos.z * 8f);
                h = h * 31 + Mathf.RoundToInt(w.Velocity.sqrMagnitude * 16f);
            }
            h = h * 31 + ContentHash(w);
            return h;
        }

        /// <summary>Content signature folded into the host hash (family content is already in
        /// the wire; this adds the fields that matter for a change and aren't covered above).</summary>
        private static int ContentHash(in BoxWire w)
        {
            int h = 17;
            h = h * 31 + w.ItemType;
            h = h * 31 + w.ItemCount;
            h = h * 31 + (w.Big ? 1 : 0);
            h = h * 31 + (w.Open ? 1 : 0);
            h = h * 31 + w.StoreShelfId;
            h = h * 31 + w.StoreShelf;
            h = h * 31 + w.StoreComp;
            h = h * 31 + w.ObjType;
            h = h * 31 + w.ObjIndex;
            h = h * 31 + w.NameHash;
            h = h * 31 + w.Kind;
            if (w.Cards != null)
            {
                h = h * 31 + w.Cards.Count;
                for (int i = 0; i < w.Cards.Count; i++)
                {
                    var c = w.Cards[i];
                    if (c == null)
                        continue;
                    h = h * 31 + (int)c.monsterType;
                    h = h * 31 + (int)c.expansionType;
                    h = h * 31 + (c.isFoil ? 1 : 0);
                }
            }
            return h;
        }

        public void HostApplyUpdate(BoxUpdateMessage msg, int connId)
        {
            if (msg == null)
                return;
            var w = msg.Box;
            var sender = PlayerRegistry.ForConnection(connId);
            Lease lease = _leases.TryGetValue(w.Id, out var l) ? l : default(Lease);
            var currentOwner = lease.Owner == NoOwner
                ? PlayerRef.None
                : lease.Owner == HostConn
                    ? new PlayerRef { Kind = PlayerKind.Host, Id = 0 }
                    : PlayerRegistry.ForConnection(lease.Owner);
            if (!BoxAuthority.AcceptBoxUpdate(currentOwner, sender))
            {
                BoxShared.DebugLog("box-rx", $"id={w.Id} fam={w.Family} sender={connId} state={w.Possession} accepted=false owner={currentOwner}");
                return;
            }

            var next = BoxAuthority.NextBoxOwner(sender, w.Possession);
            BoxShared.DebugLog("box-rx", $"id={w.Id} fam={w.Family} sender={connId} state={w.Possession} accepted=true owner={lease.Owner}->{next}");
            // The client does not know the host's owner id for itself; stamp the sender so
            // the families resolve the correct avatar for a remote Held/Placing box.
            w.OwnerConn = connId;
            lease.Owner = next.Kind == PlayerKind.None
                ? NoOwner
                : next.Kind == PlayerKind.Host ? HostConn : next.Id;
            lease.Possession = w.Possession;
            lease.LastSeen = _leaseClock;
            _leases[w.Id] = lease;

            // Apply the owner's state to the host's own copy so the host sees it too.
            if (_hostIdentity.ById.TryGetValue(w.Id, out var box) && box != null)
            {
                for (int f = 0; f < _families.Count; f++)
                    if (_families[f].Family == w.Family)
                        _families[f].ApplyState(box, w, isOwner: false);
            }

            if (w.Possession == BoxPossession.Removed
                && _hostIdentity.ById.TryGetValue(w.Id, out var dead) && dead != null)
            {
                // The sender retired the real object on their machine (trash/storage) - do
                // the same here, or the next snapshot re-assigns an id and resurrects it.
                BoxShared.ApplyingRemote = true;
                try
                {
                    Family(FamilyOf(dead))?.DestroyBox(dead);
                }
                finally { BoxShared.ApplyingRemote = false; }
                ForgetHostBox(w.Id);
                return;
            }
            _hostDirty.Add(w.Id); // emit just this box on the next flush
        }

        public void HostReleaseConn(int connId)
        {
            var release = new List<ushort>();
            foreach (var kv in _leases)
                if (kv.Value.Owner == connId)
                    release.Add(kv.Key);
            for (int i = 0; i < release.Count; i++)
            {
                if (_hostIdentity.ById.TryGetValue(release[i], out var box) && box != null)
                {
                    var w = new BoxWire
                    {
                        Id = release[i],
                        Possession = BoxPossession.Free,
                        Pos = BoxPlacement.PhysicsPosition(box),
                        Yaw = BoxPlacement.PhysicsRotation(box).eulerAngles.y
                    };
                    for (int f = 0; f < _families.Count; f++)
                        if (_families[f].Family == FamilyOf(box))
                            _families[f].ApplyState(box, w, isOwner: false);
                }
                _leases.Remove(release[i]);
                _hostDirty.Add(release[i]);
            }
            ForceNextTick();
        }

        private void ExpireLeases()
        {
            var stale = new List<ushort>();
            foreach (var kv in _leases)
                // The host's own possession is always current; only remote leases time out.
                if (kv.Value.Owner != NoOwner && kv.Value.Owner != HostConn
                    && kv.Value.Possession != BoxPossession.Free
                    && _leaseClock - kv.Value.LastSeen > LeaseTimeout)
                    stale.Add(kv.Key);
            for (int i = 0; i < stale.Count; i++)
            {
                var lease = _leases[stale[i]];
                lease.Owner = NoOwner;
                lease.Possession = BoxPossession.Free;
                _leases[stale[i]] = lease;
                _hostDirty.Add(stale[i]);
            }
        }

        private static BoxFamily FamilyOf(InteractablePackagingBox box)
        {
            if (box is InteractablePackagingBox_Card)
                return BoxFamily.Card;
            if (box is InteractablePackagingBox_Shelf)
                return BoxFamily.Furniture;
            return BoxFamily.Item;
        }

        // ---------------- client ----------------

        public void ClientApplySnapshot(BoxSnapshotMessage msg)
        {
            if (msg == null)
                return;
            bool full = msg.Full;
            bool truncated = full && msg.Boxes.Count >= MaxBoxes;
            if (full)
            {
                _snapshotIds.Clear();
                for (int i = 0; i < msg.Boxes.Count; i++)
                    _snapshotIds.Add(msg.Boxes[i].Id);
                _localRemoved.RemoveWhere(id => !_snapshotIds.Contains(id));
            }
            List<InteractablePackagingBox> spawned = null;
            for (int i = 0; i < msg.Boxes.Count; i++)
            {
                var w = msg.Boxes[i];
                var family = Family(w.Family);
                if (family == null)
                    continue;
                if (_localRemoved.Contains(w.Id))
                {
                    // The host's explicit Removed is its agreement that the box is gone;
                    // drop the local-retire guard so we don't depend on a full sweep to
                    // prune it (there is no periodic full snapshot).
                    if (w.Possession == BoxPossession.Removed)
                        _localRemoved.Remove(w.Id);
                    continue; // locally retired; wait for the host to agree
                }

                if (!_clientById.TryGetValue(w.Id, out var box) || box == null)
                {
                    if (w.Possession == BoxPossession.Removed)
                        continue;
                    // The client joins by loading the host's own save, so it usually already
                    // owns this box. Bind that object instead of spawning a duplicate.
                    box = AdoptExisting(family, w);
                    if (box != null)
                    {
                        _clientById[w.Id] = box;
                        _clientIdOf[box] = w.Id;
                        BoxShared.DebugLog("box-adopt", $"id={w.Id} fam={w.Family} state={w.Possession} name={box.name} adopted=true");
                    }
                    else
                    {
                        box = family.Spawn(w);
                        if (box == null)
                            continue;
                        _clientById[w.Id] = box;
                        _clientIdOf[box] = w.Id;
                        (spawned ?? (spawned = new List<InteractablePackagingBox>())).Add(box);
                        BoxShared.DebugLog("box-adopt", $"id={w.Id} fam={w.Family} state={w.Possession} name={box.name} adopted=false");
                    }
                }
                // Local possession wins: never let a remote state touch a box the local
                // player is currently holding/placing. A stale Free echo right after a quick
                // re-pickup would otherwise yank the held box out of the hand.
                if (w.Possession != BoxPossession.Removed
                    && family.TryReadLocal(box, out var localPoss, out _, out _, out _, out _, out _)
                    && (localPoss == BoxPossession.Held || localPoss == BoxPossession.Placing))
                    continue;
                if (!family.ContentMatches(box, w))
                {
                    DestroyClientBox(box);
                    _clientById.Remove(w.Id);
                    _clientIdOf.Remove(box);
                    continue;
                }
                if (w.Possession == BoxPossession.Removed)
                {
                    DestroyClientBox(box);
                    _clientById.Remove(w.Id);
                    _clientIdOf.Remove(box);
                    continue;
                }
                // my own Held/Placing box is driven by my local game; never re-render it
                bool mine = (w.Possession == BoxPossession.Held || w.Possession == BoxPossession.Placing)
                    && w.OwnerConn == CoopCore.LocalConnectionId;
                if (!mine)
                    family.ApplyState(box, w, isOwner: false);
            }

            if (spawned != null)
                for (int i = 0; i < spawned.Count; i++)
                    OnClientBoxSpawned?.Invoke(spawned[i]);

            if (full && !truncated)
            {
                _sweep.Clear();
                foreach (var kv in _clientById)
                    if (!_snapshotIds.Contains(kv.Key))
                        _sweep.Add(kv.Key);
                for (int i = 0; i < _sweep.Count; i++)
                {
                    if (_clientById.TryGetValue(_sweep[i], out var box) && box != null)
                        DestroyClientBox(box);
                    if (_clientById.TryGetValue(_sweep[i], out var b2) && b2 != null)
                        _clientIdOf.Remove(b2);
                    _clientById.Remove(_sweep[i]);
                }
            }
        }

        /// <summary>Destroy a client mirror without triggering the local-destroy op:
        /// this destroy IS the reconciliation, not player gameplay.</summary>
        private void DestroyClientBox(InteractablePackagingBox box)
        {
            bool prev = BoxShared.ApplyingRemote;
            BoxShared.ApplyingRemote = true;
            try
            {
                Family(FamilyOf(box))?.DestroyBox(box);
            }
            finally { BoxShared.ApplyingRemote = prev; }
        }

        /// <summary>Find a box this client already owns that represents the host's wire
        /// entry, so a join/reload binds the existing object instead of spawning a duplicate.
        /// Stored boxes match on their rack/slot address; loose boxes match on content plus
        /// nearest pose.</summary>
        private InteractablePackagingBox AdoptExisting(IBoxFamily family, in BoxWire w)
        {
            var boxes = family.LiveBoxes();
            if (boxes == null)
                return null;
            InteractablePackagingBox best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < boxes.Count; i++)
            {
                var cand = boxes[i];
                if (cand == null || _clientIdOf.ContainsKey(cand))
                    continue; // null or already bound to another id
                if (!family.ContentMatches(cand, w))
                    continue;
                var have = new BoxWire();
                family.FillContent(cand, ref have);
                if (have.IsStored != w.IsStored)
                    continue;
                if (w.IsStored)
                {
                    bool sameSlot = w.StoreShelfId != 0 && have.StoreShelfId != 0
                        ? have.StoreShelfId == w.StoreShelfId
                        : have.StoreShelf == w.StoreShelf && have.StoreComp == w.StoreComp;
                    if (sameSlot)
                        return cand;
                    continue;
                }
                float d = Vector3.Distance(BoxPlacement.PhysicsPosition(cand), w.Pos);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = cand;
                }
            }
            return best;
        }

        public void ClientTick(float dt, bool active)
        {
            if (!active)
                return;

            // Forced path: the box the local player just picked up is processed immediately,
            // not when the round-robin slice reaches it.
            if (LocalHeld != null)
            {
                var held = LocalHeld();
                if (held != null && ProcessClientBox(Family(FamilyOf(held)), held))
                    _active.Add(held);
            }

            // Mutation postfixes mark open/close edges directly. Process those before
            // the round-robin so a lid change is reported without waiting for the box's
            // position in a large warehouse scan.
            if (_clientDirty.Count > 0)
            {
                _activeScratch.Clear();
                foreach (var b in _clientDirty)
                    _activeScratch.Add(b);
                _clientDirty.Clear();
                for (int i = 0; i < _activeScratch.Count; i++)
                {
                    var b = _activeScratch[i];
                    if (b != null && ProcessClientBox(Family(FamilyOf(b)), b))
                        _active.Add(b);
                }
            }

            // Fast path: boxes currently carried/placed by the local player, checked every
            // frame so interaction is never delayed by the round-robin.
            if (_active.Count > 0)
            {
                _activeScratch.Clear();
                foreach (var b in _active)
                    _activeScratch.Add(b);
                for (int i = 0; i < _activeScratch.Count; i++)
                {
                    var box = _activeScratch[i];
                    if (box == null)
                    {
                        _active.Remove(box);
                        continue;
                    }
                    if (!ProcessClientBox(Family(FamilyOf(box)), box))
                        _active.Remove(box);
                }
            }

            // Round-robin the rest: a small budget per frame, eventually covering all boxes.
            int budget = ClientScanBudget;
            int guard = 0;
            while (budget > 0 && _families.Count > 0 && guard++ < 64)
            {
                if (_clientFam >= _families.Count)
                {
                    _clientFam = 0;
                    _clientIdx = 0;
                    break;
                }
                var family = _families[_clientFam];
                var boxes = family.LiveBoxes();
                if (boxes == null || boxes.Count == 0)
                {
                    _clientFam++;
                    _clientIdx = 0;
                    continue;
                }
                if (_clientIdx >= boxes.Count)
                {
                    _clientFam++;
                    _clientIdx = 0;
                    continue;
                }
                int take = Mathf.Min(budget, boxes.Count - _clientIdx);
                for (int k = 0; k < take; k++)
                {
                    var box = boxes[_clientIdx + k];
                    if (box == null)
                        continue;
                    if (ProcessClientBox(family, box))
                        _active.Add(box);
                }
                _clientIdx += take;
                budget -= take;
                if (_clientIdx >= boxes.Count)
                {
                    _clientFam++;
                    _clientIdx = 0;
                }
            }
        }

        /// <summary>Inspect one client-side box and report a change. Returns true while the
        /// box is carried/placed (so the caller keeps it on the every-frame fast path).</summary>
        private bool ProcessClientBox(IBoxFamily family, InteractablePackagingBox box)
        {
            if (family == null || box == null)
                return false;
            bool read = family.TryReadLocal(box, out var poss, out var pos, out var yaw,
                out var vel, out var angVel, out var stored);
            if (!_clientIdOf.ContainsKey(box))
            {
                // A box the local player is holding/placing but that has no host id cannot
                // be reported at all - its pickup/throw will never reach the host.
                if (read && (poss == BoxPossession.Held || poss == BoxPossession.Placing))
                    BoxShared.DebugLog("box-orphan",
                        $"fam={family.Family} name={box.name} state={poss} has no host id - local actions will NOT sync",
                        box.GetInstanceID(), 1f);
                return false;
            }
            if (!read)
                return false;
            if (BoxPlacement.IsThrowPending(box))
            {
                if (poss != BoxPossession.Free)
                    BoxPlacement.ClearThrow(box); // picked back up before the throw was reported
                else if (!BoxPlacement.ThrowReady(box, vel))
                    return true; // keep on the fast path; report once the impulse integrates
                else
                    BoxPlacement.ClearThrow(box);
            }
            bool change = !_reported.TryGetValue(box, out var prev) || prev != poss;
            int sig = family.ContentSignature(box);
            bool contentChange = !_reportedContent.TryGetValue(box, out var pc) || pc != sig;
            // Rate-limit content-only reports; a state edge is immediate.
            if (contentChange && !change
                && _contentSentAt.TryGetValue(box, out var lastContent)
                && Time.time - lastContent < ContentReportPeriod)
                contentChange = false;
            // A held/placing box sends no edge while it is held, so refresh the host lease
            // periodically or the 3s timeout would release it mid-hold.
            bool renewLease = (poss == BoxPossession.Held || poss == BoxPossession.Placing)
                && (!_lastSentAt.TryGetValue(box, out var lastSent)
                    || Time.time - lastSent >= LeaseRenewPeriod);
            bool isActive = poss == BoxPossession.Held || poss == BoxPossession.Placing;
            if (!change && !contentChange && !renewLease)
                return isActive;
            _reported[box] = poss;
            _reportedContent[box] = sig;
            _contentSentAt[box] = Time.time;
            _lastSentAt[box] = Time.time;
            var w = new BoxWire
            {
                Id = _clientIdOf[box],
                Family = family.Family,
                Possession = poss,
            };
            if (poss == BoxPossession.Free)
            {
                w.Pos = pos;
                w.Yaw = yaw;
                w.Velocity = vel;
                w.AngularVelocity = angVel;
            }
            family.FillContent(box, ref w);
            BoxShared.DebugLog("box-tx", $"id={w.Id} fam={w.Family} state={poss} sig={sig} name={box.name}",
                box.GetInstanceID(), 0.05f);
            SendUpdate?.Invoke(new BoxUpdateMessage { Box = w });
            return isActive;
        }

        private IBoxFamily Family(BoxFamily f)
        {
            for (int i = 0; i < _families.Count; i++)
                if (_families[i].Family == f)
                    return _families[i];
            return null;
        }
    }
}
