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
        public const int KindCount = 15;

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

            // extras beyond the host's roster: remove from the end (game removal shifts lists)
            int guard = 8;
            while (list.Count > want.Count && guard-- > 0)
            {
                var extra = list[list.Count - 1] as InteractableObject;
                if (extra == null) { list.RemoveAt(list.Count - 1); continue; }
                CoopPlugin.Log.LogInfo($"population: removing extra {extra.m_ObjectType} (kind {kind})");
                extra.OnDestroyed();
                OnClientStructureChanged?.Invoke(kind);
                list = GetList(sm, kind);
            }

            // type mismatches mid-list: repair ONE per tick (each removal shifts indices;
            // converges across ticks without ever mass-deleting on a glitch)
            for (int i = 0; i < list.Count && i < want.Count; i++)
            {
                var obj = list[i] as InteractableObject;
                if (obj == null) continue;
                // an object from a content pack only the HOST has: his id carries no name
                // we know, so want[i].ObjType is a None sentinel that would mismatch
                // whatever stands here and destroy it every tick. Leave the slot alone -
                // one-sided packs are allowed, and a mirror we cannot build is not a
                // licence to delete the joiner's furniture.
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

            // missing objects: spawn with the game's own save-load recipe (self-registers
            // at the end of the list, keeping order identical to the host's)
            guard = 8;
            while (list.Count < want.Count && guard-- > 0)
            {
                var e = want[list.Count];
                // nothing to spawn for content we don't have installed: the id resolved to
                // None, whose prefab lookup would fail anyway. Stop here rather than skip -
                // the slot IS the identity every other sync keys on, so it cannot be filled
                // by the next object along.
                if (e.Unresolved) break;
                // decorations self-register into m_DecoObjectList via SpawnDecoObject; the
                // generic SpawnInteractableObject would land them in the wrong list (and
                // resolve a null prefab from EObjectType.None), so they never appeared
                var spawned = (kind == 5)
                    ? ShelfManager.SpawnDecoObject((EDecoObject)e.ObjType)
                    : ShelfManager.SpawnInteractableObject((EObjectType)e.ObjType);
                if (spawned == null) break;
                spawned.transform.SetPositionAndRotation(e.Pos, e.Rot);
                CoopPlugin.Log.LogInfo($"population: spawned {(kind == 5 ? ((EDecoObject)e.ObjType).ToString() : ((EObjectType)e.ObjType).ToString())} (kind {kind})");
                OnClientStructureChanged?.Invoke(kind);
                list = GetList(sm, kind);
            }
        }

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
