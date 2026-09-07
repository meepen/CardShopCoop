using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Keeps the PLACED-OBJECT POPULATION identical on every machine. All other syncs key
    /// objects by their index in ShelfManager's lists, so a host buying (or selling) a
    /// shelf mid-session used to desync every index after it - moves landing on the wrong
    /// shelves, "he sees two shelves, I see one". The host broadcasts each list's
    /// (objectType, transform) roster every 3s; clients reconcile using the game's own
    /// save-load recipe (SpawnInteractableObject self-registers in list order) and the
    /// object's own OnDestroyed removal.
    /// </summary>
    public class PopulationSync
    {
        // 15 is the generic interactable-object list. It must be included because
        // generic furniture is still a real placed object and is referenced by the
        // furniture-box and move mirrors.
        public const int KindCount = 16;

        /// <summary>Shared list resolver used by PopulationSync and ObjMoveSync so both
        /// always agree on what "kind 3, index 7" means.</summary>
        public static IList GetList(ShelfManager sm, int kind)
        {
            switch (kind)
            {
                case 0: return sm.m_ShelfList;
                case 1: return sm.m_WarehouseShelfList;
                case 2: return sm.m_CardShelfList;
                case 3: return sm.m_CardItemCombiShelfList;
                case 4: return sm.m_CashierCounterList;
                case 5: return sm.m_DecoObjectList;
                case 6: return sm.m_PlayTableList;
                case 7: return sm.m_WorkbenchList;
                case 8: return sm.m_TrashBinList;
                case 9: return sm.m_CardStorageShelfList;
                case 10: return sm.m_AutoCleanserList;
                case 11: return sm.m_AutoPackOpenerList;
                case 12: return sm.m_EmptyBoxStorageList;
                case 13: return sm.m_BulkDonationBoxList;
                case 14: return sm.m_TournamentPrizeShelfList;
                case 15:
                    return sm.m_InteractableObjectList;
                default: return null;
            }
        }

        public struct Entry
        {
            public int ObjType;
            /// <summary>Client only: the host sent a MODDED object id whose name does not
            /// exist on this PC (a content pack only he has). ObjType is then the enum's
            /// None sentinel and means nothing - so this entry may never be compared
            /// against a local object or spawned. Always false on the host, which never
            /// translates, and false for every vanilla id, which never needs to.</summary>
            public bool Unresolved;
            public Vector3 Pos;
            public Quaternion Rot;
        }

        private ShelfManager _sm;
        private float _timer;
        private int _lastHash;
        private float _heal;

        public Action<List<List<Entry>>> OnHostSnapshot;

        /// <summary>Fired when reconciliation destroys or spawns an object of a kind -
        /// other syncs' index baselines for that kind are stale from this moment.</summary>
        public static Action<int> OnClientStructureChanged;

        public void Reset()
        {
            _sm = null;
            _timer = -1.1f; // staggered phase vs the other snapshot engines
            _lastHash = 0;
            _heal = 0f;
        }

        public void ForceNextTick()
        {
            _timer = 3f;
            _lastHash = 0;
        }

        private ShelfManager Sm()
        {
            if (_sm == null) _sm = UnityEngine.Object.FindObjectOfType<ShelfManager>();
            return _sm;
        }

        public void HostTick(float dt, bool active)
        {
            if (!active) return;
            _timer += dt;
            if (_timer < 3f) return;
            _timer -= 3f;
            try
            {
                var sm = Sm();
                if (sm == null) return;
                // population changes a handful of times per session; hash the cheap
                // identity (counts + types) and skip the heavy build when unchanged,
                // with a slow heal so a client that missed one still converges
                int hash = 17;
                for (int kind = 0; kind < KindCount; kind++)
                {
                    var list = GetList(sm, kind);
                    int n = list?.Count ?? 0;
                    hash = hash * 31 + n;
                    if (list != null)
                        for (int i = 0; i < n; i++)
                            if (list[i] is InteractableObject obj)
                                // decorations (kind 5) have m_ObjectType == None(-1); their
                                // real identity is m_DecoObjectType, so hash THAT or a deco
                                // add/remove that preserves count never re-broadcasts
                                hash = hash * 31 + ((kind == 5) ? (int)obj.m_DecoObjectType : (int)obj.m_ObjectType);
                }
                _heal += 3f;
                if (hash == _lastHash && _heal < 30f) return;
                _lastHash = hash;
                _heal = 0f;
                var all = new List<List<Entry>>(KindCount);
                for (int kind = 0; kind < KindCount; kind++)
                {
                    var list = GetList(sm, kind);
                    var entries = new List<Entry>(list?.Count ?? 0);
                    if (list != null)
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            var obj = list[i] as InteractableObject;
                            if (obj == null) continue;
                            entries.Add(new Entry
                            {
                                // kind 5 = decorations: serialize the deco enum, not the -1
                                // m_ObjectType, so the guest can actually spawn them
                                ObjType = (kind == 5) ? (int)obj.m_DecoObjectType : (int)obj.m_ObjectType,
                                Pos = obj.transform.position,
                                Rot = obj.transform.rotation,
                            });
                        }
                    }
                    all.Add(entries);
                }
                OnHostSnapshot?.Invoke(all);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("PopulationSync host: " + e.Message); }
        }

        /// <summary>Client: make each object list match the host's roster.</summary>
        public void ClientApply(List<List<Entry>> hostLists)
        {
            var sm = Sm();
            if (sm == null) return;
            for (int kind = 0; kind < KindCount && kind < hostLists.Count; kind++)
            {
                try { ReconcileKind(sm, kind, hostLists[kind]); }
                catch (Exception e) { CoopPlugin.Log.LogWarning($"PopulationSync kind {kind}: {e.Message}"); }
            }
        }

        private static void ReconcileKind(ShelfManager sm, int kind, List<Entry> want)
        {
            var list = GetList(sm, kind);
            if (list == null) return;

            // Snapshot the live client objects once (non-null only, matching how the host
            // serializes its roster).
            var clientObjs = new List<InteractableObject>(list.Count);
            for (int i = 0; i < list.Count; i++)
                if (list[i] is InteractableObject o) clientObjs.Add(o);

            // Roster COUNT differs = a real add/remove. Use type + nearest position to
            // identify WHICH object is extra/missing: deleting a shelf out of the MIDDLE
            // re-indexes every object after it, so "remove extras from the end" pairs the
            // WRONG objects whenever shelves share a type - the client keeps the wrong shelf
            // and deletes the right one, and every index-keyed content sync then repaints
            // the wrong shelves forever. Positions are authoritative (both peers load the
            // same save and ObjMoveSync keeps poses in step), so type + nearest position
            // finds the true counterpart even after a middle removal.
            if (clientObjs.Count != want.Count)
            {
                ReconcileByPose(sm, kind, want, clientObjs);
                return;
            }

            // Same count: NO structural change. Verify identities the way the old code
            // did (repair one genuine type mismatch per tick) and never touch positions -
            // the periodic heal and a mid-move object must not churn shelves merely because
            // a pose momentarily differs.
            for (int i = 0; i < list.Count && i < want.Count; i++)
            {
                var obj = list[i] as InteractableObject;
                if (obj == null) continue;
                // an object from a content pack only the HOST has: his id carries no name
                // we know, so want[i].ObjType is a None sentinel that would mismatch
                // whatever stands here. Leave the slot alone - one-sided packs are allowed,
                // and a mirror we cannot build is not a licence to delete the joiner's furniture.
                if (want[i].Unresolved) continue;
                // compare on the correct identity per kind, or a wrong deco variant (whose
                // m_ObjectType is always -1) could never be detected and repaired
                int cur = (kind == 5) ? (int)obj.m_DecoObjectType : (int)obj.m_ObjectType;
                if (cur != want[i].ObjType)
                {
                    CoopPlugin.Log.LogInfo($"population: repairing index {i} (kind {kind}): {cur} -> {want[i].ObjType}");
                    obj.OnDestroyed();
                    OnClientStructureChanged?.Invoke(kind);
                    return; // re-align next tick
                }
            }
        }

        /// <summary>Reconcile a kind whose roster count differs from the host's - a real
        /// insert or (more importantly) a MIDDLE deletion that re-indexed the list. Matches
        /// host entries to client objects by (objectType, nearest position) so the extra
        /// object is the one actually removed, not merely "the last one".</summary>
        private static void ReconcileByPose(ShelfManager sm, int kind, List<Entry> want,
            List<InteractableObject> clientObjs)
        {
            var matchedClient = new bool[clientObjs.Count];
            var matchedHost = new bool[want.Count];

            // nearest-position matching among same-type peers: a true counterpart sits at
            // ~identical position (d ~ 0), so a removed shelf's neighbour never steals its
            // partner. TolSq is generous enough for float/save drift, tight enough that a
            // genuinely-missing object (no counterpart) is still left unmatched.
            const float TolSq = 1.0f;
            for (int w = 0; w < want.Count; w++)
            {
                // an object from a content pack only the HOST has: his id carries no name
                // we know, so ObjType is a None sentinel that would mismatch whatever stands
                // here. One-sided packs are allowed; a mirror we cannot build is not a
                // licence to delete the joiner's furniture - so protect the local object at
                // this position by pose alone and neither spawn nor remove for it.
                if (want[w].Unresolved)
                {
                    int best = -1;
                    float bestDs = float.MaxValue;
                    for (int c = 0; c < clientObjs.Count; c++)
                    {
                        if (matchedClient[c]) continue;
                        float ds = (clientObjs[c].transform.position - want[w].Pos).sqrMagnitude;
                        if (ds < bestDs) { bestDs = ds; best = c; }
                    }
                    if (best >= 0 && bestDs <= TolSq) matchedClient[best] = true;
                    continue;
                }
                int b = -1;
                float bDs = float.MaxValue;
                for (int c = 0; c < clientObjs.Count; c++)
                {
                    if (matchedClient[c]) continue;
                    // compare on the correct identity per kind, or a wrong deco variant
                    // (whose m_ObjectType is always -1) could never be detected
                    int curType = (kind == 5) ? (int)clientObjs[c].m_DecoObjectType : (int)clientObjs[c].m_ObjectType;
                    if (curType != want[w].ObjType) continue;
                    float ds = (clientObjs[c].transform.position - want[w].Pos).sqrMagnitude;
                    if (ds < bDs) { bDs = ds; b = c; }
                }
                if (b >= 0 && bDs <= TolSq)
                {
                    matchedClient[b] = true;
                    matchedHost[w] = true;
                }
            }

            // client objects with no host counterpart are extras: remove them (bounded per
            // tick so a glitch can't mass-delete in one frame; the next roster pass
            // converges the remainder). Removing shifts the list, so re-resolve after each
            // removal - but never destroy an object already pulled from the list, and NEVER
            // yank an object the local player is actively dragging (its pose is transient,
            // so it legitimately won't match a fixed host pose right now).
            int guard = 8;
            for (int c = clientObjs.Count - 1; c >= 0 && guard > 0; c--)
            {
                if (matchedClient[c]) continue;
                var obj = clientObjs[c];
                if (obj == null || !GetList(sm, kind).Contains(obj)) continue;
                if (obj.GetIsMovingObject()) continue;
                guard--;
                CoopPlugin.Log.LogInfo($"population: removing unmatched {TypeName(kind, obj)} (kind {kind})");
                obj.OnDestroyed();
                OnClientStructureChanged?.Invoke(kind);
            }

            // host entries with no local counterpart are missing: spawn with the game's own
            // save-load recipe (self-registers at the end of the list, keeping order
            // identical to the host's).
            guard = 8;
            for (int w = 0; w < want.Count && guard > 0; w++)
            {
                var e = want[w];
                if (e.Unresolved || matchedHost[w]) continue;
                // if a same-type client object is being dragged (matched nothing above
                // because its pose is transient), it is almost certainly this entry's
                // counterpart - consume it as the match instead of spawning a duplicate.
                if (TryClaimDragged(kind, clientObjs, matchedClient, e.ObjType)) continue;
                // nothing to spawn for content we don't have installed: the id resolved to
                // None, whose prefab lookup would fail anyway. Skip - the slot IS the
                // identity every other sync keys on, so it cannot be filled by the next
                // object along.
                // decorations self-register into m_DecoObjectList via SpawnDecoObject; the
                // generic SpawnInteractableObject would land them in the wrong list (and
                // resolve a null prefab from EObjectType.None), so they never appeared
                var spawned = (kind == 5)
                    ? ShelfManager.SpawnDecoObject((EDecoObject)e.ObjType)
                    : ShelfManager.SpawnInteractableObject((EObjectType)e.ObjType);
                if (spawned == null) continue;
                guard--;
                spawned.transform.SetPositionAndRotation(e.Pos, e.Rot);
                CoopPlugin.Log.LogInfo($"population: spawned {TypeName(kind, e.ObjType)} (kind {kind})");
                OnClientStructureChanged?.Invoke(kind);
            }
        }

        /// <summary>If an unmatched client object of this type is being dragged by the
        /// local player, mark it as this host entry's counterpart so we neither remove it
        /// (the removal pass skips moving objects) nor spawn a duplicate for the same
        /// physical object. Returns true if one was claimed.</summary>
        private static bool TryClaimDragged(int kind, List<InteractableObject> clientObjs,
            bool[] matchedClient, int objType)
        {
            if (clientObjs == null || matchedClient == null) return false;
            for (int c = 0; c < clientObjs.Count; c++)
            {
                if (matchedClient[c]) continue;
                var o = clientObjs[c];
                if (o == null || !o.GetIsMovingObject()) continue;
                int t = (kind == 5) ? (int)o.m_DecoObjectType : (int)o.m_ObjectType;
                if (t == objType) { matchedClient[c] = true; return true; }
            }
            return false;
        }

        /// <summary>A placed object's identity as a string, for the reconcile logs. Kind 5
        /// (decorations) carries m_DecoObjectType (m_ObjectType is always None there);
        /// every other kind carries m_ObjectType.</summary>
        private static string TypeName(int kind, InteractableObject obj)
            => kind == 5 ? ((EDecoObject)obj.m_DecoObjectType).ToString() : ((EObjectType)obj.m_ObjectType).ToString();

        private static string TypeName(int kind, int objType)
            => kind == 5 ? ((EDecoObject)objType).ToString() : ((EObjectType)objType).ToString();

        // ---- wire ----

        /// <summary>Which modded id space a kind's ObjType lives in: kind 5 serializes
        /// m_DecoObjectType (EDecoObject), every other kind m_ObjectType (EObjectType) -
        /// the same split the snapshot and ReconcileKind use. Both are enums EPL mints
        /// custom ids into, so both translate; the two are NOT interchangeable.</summary>
        private static Util.EnumKind KindOf(int kind)
        {
            return kind == 5 ? Util.EnumKind.DecoObject : Util.EnumKind.ObjectType;
        }

        public static void Write(BinaryWriter bw, List<List<Entry>> all)
        {
            bw.Write((byte)all.Count);
            for (int k = 0; k < all.Count; k++)
            {
                var entries = all[k];
                // ushort count (was byte capped at 250): a big shop can hold >250 of one
                // kind, and a byte cap silently dropped the tail AND could wedge the gate
                bw.Write((ushort)entries.Count);
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    bw.Write(Util.EnumMap.ToWire(KindOf(k), e.ObjType)); // -> host ids
                    bw.Write(e.Pos.x); bw.Write(e.Pos.y); bw.Write(e.Pos.z);
                    bw.Write(e.Rot.x); bw.Write(e.Rot.y); bw.Write(e.Rot.z); bw.Write(e.Rot.w);
                }
            }
        }

        public static List<List<Entry>> Read(BinaryReader br)
        {
            int kinds = br.ReadByte();
            var all = new List<List<Entry>>(kinds);
            for (int k = 0; k < kinds; k++)
            {
                int n = br.ReadUInt16();
                var entries = new List<Entry>(n);
                for (int i = 0; i < n; i++)
                {
                    var e = new Entry();
                    // TryFromWire, not FromWire: ReconcileKind BRANCHES on this id (spawn
                    // it / destroy a local object that disagrees with it), so it has to be
                    // able to tell "the host placed an object this PC does not have" apart
                    // from a real mismatch. Vanilla ids always resolve.
                    int objType;
                    e.Unresolved = !Util.EnumMap.TryFromWire(KindOf(k), br.ReadInt32(), out objType);
                    e.ObjType = objType;
                    e.Pos = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    e.Rot = new Quaternion(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    entries.Add(e);
                }
                all.Add(entries);
            }
            return all;
        }
    }
}
