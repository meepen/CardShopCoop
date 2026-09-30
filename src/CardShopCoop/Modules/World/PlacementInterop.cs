using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Modules.World
{
    public static class PlacementApi
    {
        public const int KindCount = 16;
        public const int DecorationKind = 5;
        public const int PlayTableKind = 6;

        /// <summary>A local hook for consumers whose objects are created after the snapshot.</summary>
        public static event Action<int> StructureChanged
        {
            add => PlacementInterop.StructureChanged += value;
            remove => PlacementInterop.StructureChanged -= value;
        }

        public static IList GetList(ShelfManager shelfManager, int kind)
            => PlacementInterop.GetList(shelfManager, kind);

        public static bool TryMakeCompartmentKey(int kind, InteractableObject obj,
            int compartment, out int key)
            => PlacementIdentity.TryMakeCompartmentKey(kind, obj, compartment, out key,
                WorldHostBehaviour.Active != null);

        public static bool TryMakeObjectKey(int kind, InteractableObject obj, out int key)
            => PlacementIdentity.TryMakeObjectKey(kind, obj, out key,
                WorldHostBehaviour.Active != null);

        public static bool TryMakeBoxableFurnitureEntityId(InteractableObject obj, long worldEpoch,
            out string entityId)
            => PlacementIdentity.TryMakeBoxableFurnitureEntityId(obj, worldEpoch, out entityId,
                WorldHostBehaviour.Active != null);

        public static bool TryResolveBoxableFurnitureEntityId(string entityId, long worldEpoch,
            EObjectType expectedType, out InteractableObject result, bool requireUnboxed = false)
            => PlacementIdentity.TryResolveBoxableFurnitureEntityId(entityId, worldEpoch,
                expectedType, out result, requireUnboxed);

        /// <summary>Creates the exact boxed furniture a host entity id names, through the game's
        /// factory, and binds that identity to it. Used when an authoritative box descriptor
        /// arrives for a host-spawned object this peer never saw a placement delta for.</summary>
        public static bool TryCreateBoxableFurnitureForIdentity(string entityId, long worldEpoch,
            EObjectType expectedType, Vector3 position, Quaternion rotation,
            out InteractableObject created)
            => PlacementIdentity.TryCreateBoxableFurnitureForIdentity(entityId, worldEpoch,
                expectedType, position, rotation, out created);

        public static bool IsPlacementIdentityReady
            => WorldClientBehaviour.IsPlacementIdentityReady;

        public static bool TryResolve(ShelfManager shelfManager, int kind, ushort id,
            out InteractableObject result)
            => PlacementIdentity.TryResolve(shelfManager, kind, id, out result);

        public static ushort ObjectIdFromCompartmentKey(int key)
            => PlacementIdentity.ObjectIdFromCompartmentKey(key);

        public static ushort ObjectIdFromObjectKey(int key)
            => PlacementIdentity.ObjectIdFromObjectKey(key);

        public static Component ResolveObjectByKey(int key)
            => PlacementMoveState.ResolveObjectByKey(key);
    }

    internal static class PlacementInterop
    {
        internal const int NoType = int.MinValue;
        internal static event Action<int> StructureChanged;

        private static readonly FieldInfo FiMoveValid = AccessTools.Field(
            typeof(InteractableObject), "m_IsMovingObjectValidState");
        private static readonly FieldInfo FiTagGroup = AccessTools.Field(
            typeof(InteractableObject), "m_Shelf_WorldUIGrp");
        private static readonly FieldInfo FiPackagingBoxShelf = AccessTools.Field(
            typeof(InteractableObject), "m_InteractablePackagingBox_Shelf");
        private static readonly FieldInfo FiBeingHold = AccessTools.Field(
            typeof(InteractableObject), "m_IsBeingHold");
        private static readonly FieldInfo FiBoxedObject = AccessTools.Field(
            typeof(InteractablePackagingBox_Shelf), "m_BoxedObject");
        private static readonly FieldInfo FiCashScreen = AccessTools.Field(
            typeof(InteractableCashierCounter), "m_UICashCounterScreen");
        private static readonly FieldInfo FiCreditScreen = AccessTools.Field(
            typeof(InteractableCashierCounter), "m_UICreditCardScreen");
        private static readonly MethodInfo MiOpenerSetUi = AccessTools.Method(
            typeof(InteractableAutoPackOpener), "SetUITransform");

        private static bool _betaProbed;
        private static Type _storedBoxRecordType;
        private static Type _packageBoxCandidateType;

        internal static void ProbeBetaSurface()
        {
            if (_betaProbed)
            {
                return;
            }

            _betaProbed = true;
            var assembly = typeof(ShelfCompartment).Assembly;
            _storedBoxRecordType = assembly.GetType("StoredBoxRecord", false);
            _packageBoxCandidateType = assembly.GetType("PackageBoxCandidate", false);
            if (_storedBoxRecordType != null)
            {
                CoopPlugin.Log.LogDebug("placement: record-backed warehouse symbols detected");
            }
            else if (_packageBoxCandidateType != null)
            {
                CoopPlugin.Log.LogDebug("placement: package candidate symbol detected without record type");
            }
        }

        internal static bool IsRecordWarehouse
        {
            get
            {
                ProbeBetaSurface();
                return _storedBoxRecordType != null;
            }
        }

        internal static IList GetList(ShelfManager shelfManager, int kind)
        {
            if (shelfManager == null)
            {
                return null;
            }

            return kind switch
            {
                0 => shelfManager.m_ShelfList,
                1 => shelfManager.m_WarehouseShelfList,
                2 => shelfManager.m_CardShelfList,
                3 => shelfManager.m_CardItemCombiShelfList,
                4 => shelfManager.m_CashierCounterList,
                5 => null,
                6 => shelfManager.m_PlayTableList,
                7 => shelfManager.m_WorkbenchList,
                8 => shelfManager.m_TrashBinList,
                9 => shelfManager.m_CardStorageShelfList,
                10 => shelfManager.m_AutoCleanserList,
                11 => shelfManager.m_AutoPackOpenerList,
                12 => shelfManager.m_EmptyBoxStorageList,
                13 => shelfManager.m_BulkDonationBoxList,
                14 => shelfManager.m_TournamentPrizeShelfList,
                15 => shelfManager.m_InteractableObjectList,
                _ => null,
            };
        }

        internal static ShelfManager FindShelfManager() => SceneRef<ShelfManager>.Get();

        internal static int TypeIdOf(Component component)
            => component is InteractableObject obj ? (int)obj.m_ObjectType : NoType;

        internal static bool IsBoxed(InteractableObject obj)
            => obj != null && obj.GetIsBoxedUp();

        internal static void NotifyStructureChanged(int kind)
        {
            try
            {
                StructureChanged?.Invoke(kind);
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("placement structure subscriber failed: " + error.Message);
            }
        }

        internal static void SyncTagGroup(Transform objectTransform)
        {
            var objectComponent = objectTransform?.GetComponent<InteractableObject>();
            if (objectComponent == null || FiTagGroup?.GetValue(objectComponent) is not Transform group)
            {
                return;
            }

            group.SetPositionAndRotation(objectTransform.position, objectTransform.rotation);
        }

        internal static void InvokeOpenerUi(InteractableAutoPackOpener opener)
            => MiOpenerSetUi?.Invoke(opener, null);

        internal static void PlaceBoxedObject(InteractableObject obj, Vector3 position,
            Quaternion rotation)
        {
            if (obj == null)
            {
                return;
            }

            obj.gameObject.SetActive(true);
            var appliedRotation = obj.m_IsDecorationVertical
                ? rotation : Quaternion.Euler(0f, rotation.eulerAngles.y, 0f);
            obj.transform.SetPositionAndRotation(position, appliedRotation);
            FiMoveValid?.SetValue(obj, true);
            obj.PlaceMovedObject();
            if (obj.m_IsDecorationVertical)
            {
                obj.SetVerticalSnapToWarehouseWall(obj.GetIsVerticalSnapToWarehouseWall(),
                    obj.GetVerticalSnapWallIndex());
            }

            if (obj is InteractableCashierCounter counter)
            {
                (FiCashScreen?.GetValue(counter) as Component)?.gameObject.SetActive(true);
                (FiCreditScreen?.GetValue(counter) as Component)?.gameObject.SetActive(true);
            }

            SyncTagGroup(obj.transform);
            // The game's place path retires the packaging box but leaves the object pointing at
            // it until Unity's deferred destroy runs at end of frame. Drop the reference now so a
            // later re-box spawns a clean package instead of adopting the doomed one.
            DetachPackagingBox(obj);
        }

        internal static void BoxUpPlacedObject(InteractableObject obj)
        {
            if (obj != null && !IsBoxed(obj) && !obj.GetIsMovingObject())
            {
                obj.BoxUpObject(false);
            }
        }

        /// <summary>Drops an object's packaging-box reference without touching the package itself.
        /// The game's unbox path (<c>OnPlacedMovedObject</c>) empties and destroys the package but
        /// leaves this field pointing at the doomed object until Unity's deferred destroy runs at
        /// the end of the frame. That window lets a later authoritative apply adopt the package
        /// that is already scheduled to disappear; clearing the field makes the box engine spawn a
        /// clean replacement instead.</summary>
        internal static void DetachPackagingBox(InteractableObject obj)
        {
            if (obj != null)
            {
                FiPackagingBoxShelf?.SetValue(obj, null);
            }
        }

        internal static bool ReadMoveValidity(InteractableObject obj)
            => obj != null && FiMoveValid?.GetValue(obj) is bool valid && valid;

        /// <summary>True while an object is in a player's hand (locally or mirrored as a remote
        /// carry). A held package rides that player's avatar, so the placement channel must never
        /// reposition it.</summary>
        internal static bool IsBeingHeld(InteractableObject obj)
            => obj != null && FiBeingHold?.GetValue(obj) is bool held && held;

        /// <summary>The furniture packaging box a placed object was boxed into. Prefers the
        /// object's own reference and falls back to scanning the live shelf packages so a remote
        /// ghost can hide the box even when the object's field has already been detached.</summary>
        internal static InteractablePackagingBox_Shelf FindPackagingBoxFor(InteractableObject obj)
        {
            if (obj == null)
            {
                return null;
            }

            var direct = obj.GetPackagingBoxShelf();
            if (direct != null)
            {
                return direct;
            }

            var list = RestockManager.GetShelfPackagingBoxList();
            for (var i = 0; list != null && i < list.Count; i++)
            {
                var shelf = list[i];
                if (shelf != null && FiBoxedObject?.GetValue(shelf) is InteractableObject boxed
                    && ReferenceEquals(boxed, obj))
                {
                    return shelf;
                }
            }

            return null;
        }

        /// <summary>True when the shared box engine already owns this object's packaging box, on
        /// either role. The box engine is then the single owner of that box's lifecycle and its
        /// boxed flag, so the placement channel must not box the object itself.</summary>
        internal static bool IsBoxEngineOwned(InteractableObject obj)
        {
            if (obj == null)
            {
                return false;
            }

            var box = obj.GetPackagingBoxShelf();
            return CardShopCoop.Modules.World.WorldClientBehaviour.IsKnownPackagingBox(box)
                || CardShopCoop.Modules.World.WorldHostBehaviour.IsKnownPackagingBox(box);
        }

        /// <summary>Recreates a boxed placement object through the game's factory and returns
        /// exactly the object it created. The factory self-registers the object with its kind's
        /// list; locating it afterwards by pose could pick a different same-type object, so the
        /// instance is returned directly instead.</summary>
        internal static InteractableObject SpawnBoxedObject(int objectType, Vector3 position,
            Quaternion rotation)
        {
            var obj = ShelfManager.SpawnInteractableObject((EObjectType)objectType);
            if (obj == null)
            {
                return null;
            }

            obj.Init();
            obj.BoxUpObject(false);
            var package = obj.GetPackagingBoxShelf();
            if (package != null)
            {
                package.transform.SetPositionAndRotation(position, rotation);
            }

            return obj;
        }

        /// <summary>Recreates a placed (unboxed) placement object through the game's factory. The
        /// factory returns and self-registers the new object, unlike the package-box path.</summary>
        internal static InteractableObject SpawnPlacedObject(int objectType, Vector3 position,
            Quaternion rotation)
        {
            var obj = ShelfManager.SpawnInteractableObject((EObjectType)objectType);
            if (obj == null)
            {
                return null;
            }

            // Match the package-box factory: run the object's own init so it is registered with
            // every list the game expects, then settle its pose.
            obj.Init();
            obj.transform.SetPositionAndRotation(position, rotation);
            return obj;
        }

        internal static int FindKind(InteractableObject obj)
        {
            var manager = FindShelfManager();
            for (var kind = 0; kind < PlacementApi.KindCount; kind++)
            {
                if (GetList(manager, kind)?.Contains(obj) == true)
                {
                    return kind;
                }
            }

            return -1;
        }
    }

    internal static class PlacementIdentity
    {
        internal const ushort Invalid = 0;

        private sealed class ReferenceComparer : IEqualityComparer<InteractableObject>
        {
            public bool Equals(InteractableObject left, InteractableObject right)
                => ReferenceEquals(left, right);

            public int GetHashCode(InteractableObject value)
                => RuntimeHelpers.GetHashCode(value);
        }

        private static readonly Dictionary<InteractableObject, ushort> ByObject =
            new(new ReferenceComparer());
        private static readonly Dictionary<ushort, InteractableObject> ById = new();
        private static ushort _next = 1;

        internal static void Reset()
        {
            ByObject.Clear();
            ById.Clear();
            _next = 1;
        }

        internal static bool TryGet(InteractableObject obj, out ushort id)
        {
            if (obj != null && ByObject.TryGetValue(obj, out id) && id != Invalid)
            {
                return true;
            }

            id = Invalid;
            return false;
        }

        /// <summary>True when this object already owns a placement identity. Identity is
        /// exclusive: an incoming delta may only adopt an object that has none. Binding a new
        /// key onto an already-identified object silently steals it from its own entity, which
        /// is how a purchased furniture box adopted a same-type piece that was already placed.</summary>
        internal static bool IsIdentified(InteractableObject obj)
            => obj != null && ByObject.TryGetValue(obj, out var id) && id != Invalid;

        internal static ushort AssignHost(InteractableObject obj)
        {
            if (obj == null)
            {
                return Invalid;
            }

            if (TryGet(obj, out var existing))
            {
                return existing;
            }

            ushort id;
            do
            {
                id = _next++;
                if (_next == Invalid)
                {
                    _next = 1;
                }
            }
            while (id == Invalid || ById.ContainsKey(id));

            Bind(obj, id);
            return id;
        }

        internal static void Bind(InteractableObject obj, ushort id)
        {
            if (obj == null || id == Invalid)
            {
                return;
            }

            if (ByObject.TryGetValue(obj, out var old) && old != id)
            {
                CoopPlugin.Log.LogWarning("[placement] identity moved " + old + "->" + id + " on "
                    + obj.name + "; a placement object must keep one identity for its lifetime.");
                ById.Remove(old);
            }

            if (ById.TryGetValue(id, out var previous) && !ReferenceEquals(previous, obj))
            {
                CoopPlugin.Log.LogWarning("[placement] identity " + id + " moved off "
                    + (previous == null ? "<null>" : previous.name) + " onto " + obj.name
                    + "; two objects must not share one identity.");
                ByObject.Remove(previous);
            }

            ByObject[obj] = id;
            ById[id] = obj;
        }

        internal static void Forget(InteractableObject obj)
        {
            if (obj == null || !ByObject.TryGetValue(obj, out var id))
            {
                return;
            }

            ByObject.Remove(obj);
            if (ById.TryGetValue(id, out var current) && ReferenceEquals(current, obj))
            {
                ById.Remove(id);
            }
        }

        internal static bool TryMakeCompartmentKey(int kind, InteractableObject obj,
            int compartment, out int key, bool host)
        {
            key = 0;
            if (kind == PlacementApi.DecorationKind || !TryGetId(kind, obj, out var id, host))
            {
                return false;
            }

            key = (kind << 24) | (id << 8) | (compartment & 0xFF);
            return true;
        }

        internal static bool TryMakeObjectKey(int kind, InteractableObject obj, out int key,
            bool host)
        {
            key = 0;
            if (kind == PlacementApi.DecorationKind || !TryGetId(kind, obj, out var id, host))
            {
                return false;
            }

            key = (kind << 24) | id;
            return true;
        }

        internal static bool TryMakeBoxableFurnitureEntityId(InteractableObject obj,
            long worldEpoch, out string entityId, bool host)
        {
            entityId = null;
            if (obj == null || worldEpoch <= 0)
            {
                return false;
            }

            var kind = PlacementInterop.FindKind(obj);
            if (kind < 0 || !TryMakeObjectKey(kind, obj, out var key, host))
            {
                return false;
            }

            entityId = "furniture-placement:" + worldEpoch.ToString(CultureInfo.InvariantCulture)
                + ":" + key.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        internal static bool TryResolveBoxableFurnitureEntityId(string entityId, long worldEpoch,
            EObjectType expectedType, out InteractableObject result, bool requireUnboxed)
        {
            result = null;
            if (expectedType == EObjectType.None
                || !TryParseFurnitureEntityId(entityId, worldEpoch, out var key))
            {
                return false;
            }

            var kind = key >> 24;
            if (kind < 0 || kind >= PlacementApi.KindCount
                || kind == PlacementApi.DecorationKind || ObjectIdFromObjectKey(key) == Invalid)
            {
                return false;
            }

            if (!TryResolve(PlacementInterop.FindShelfManager(), kind, ObjectIdFromObjectKey(key),
                out result) || result.m_ObjectType != expectedType
                || (requireUnboxed && (result.GetIsBoxedUp() || result.GetIsMovingObject())))
            {
                result = null;
                return false;
            }

            return true;
        }

        /// <summary>Creates the exact boxed furniture the entity id names, through the game's
        /// factory, and binds the identity to it. The identity must be unbound; the caller reaches
        /// here only after a resolve attempt failed.</summary>
        internal static bool TryCreateBoxableFurnitureForIdentity(string entityId, long worldEpoch,
            EObjectType expectedType, Vector3 position, Quaternion rotation,
            out InteractableObject created)
        {
            created = null;
            if (expectedType == EObjectType.None
                || !TryParseFurnitureEntityId(entityId, worldEpoch, out var key))
            {
                return false;
            }

            var kind = key >> 24;
            var id = ObjectIdFromObjectKey(key);
            if (kind < 0 || kind >= PlacementApi.KindCount || kind == PlacementApi.DecorationKind
                || id == Invalid)
            {
                return false;
            }

            if (TryResolve(PlacementInterop.FindShelfManager(), kind, id, out created))
            {
                // Raced with another apply; the authoritative object already exists.
                return created.m_ObjectType == expectedType;
            }

            created = PlacementInterop.SpawnBoxedObject((int)expectedType, position, rotation);
            if (created == null)
            {
                return false;
            }

            Bind(created, id);
            return true;
        }

        /// <summary>Binds the identity an authoritative furniture entity id names onto the exact
        /// object a peer already owns. The box channel calls this when a descriptor arrives for a
        /// box this peer created: the descriptor names the host's entity for the furniture inside,
        /// and the local object must become that entity so the placement channel resolves one
        /// object per entity instead of materializing a duplicate. The object must match the
        /// descriptor's expected type. Returns true when the object holds the named identity
        /// afterwards (already or newly bound).</summary>
        internal static bool TryAdoptBoxableFurnitureIdentity(InteractableObject obj, string entityId,
            long worldEpoch, EObjectType expectedType)
        {
            if (obj == null || expectedType == EObjectType.None
                || !TryParseFurnitureEntityId(entityId, worldEpoch, out var key))
            {
                return false;
            }

            var kind = key >> 24;
            var id = ObjectIdFromObjectKey(key);
            if (kind < 0 || kind >= PlacementApi.KindCount || kind == PlacementApi.DecorationKind
                || id == Invalid)
            {
                return false;
            }

            if (obj.m_ObjectType != expectedType)
            {
                CoopPlugin.Log.LogWarning("[placement] furniture identity " + id + " names "
                    + expectedType + " but the boxed object is " + obj.m_ObjectType
                    + "; not adopting it.");
                return false;
            }

            if (TryGet(obj, out var existing))
            {
                if (existing == id)
                {
                    return true;
                }

                CoopPlugin.Log.LogWarning("[placement] furniture identity " + id
                    + " conflicts with the boxed object's own identity " + existing
                    + "; the descriptor cannot adopt it.");
                return false;
            }

            if (ById.TryGetValue(id, out var bound) && bound != null && !ReferenceEquals(bound, obj))
            {
                CoopPlugin.Log.LogWarning("[placement] furniture identity " + id
                    + " is already bound to " + bound.name + "; the descriptor cannot adopt it onto "
                    + obj.name + ".");
                return false;
            }

            var localKind = PlacementInterop.FindKind(obj);
            if (localKind != kind)
            {
                CoopPlugin.Log.LogWarning("[placement] furniture identity " + id + " is kind " + kind
                    + " but the boxed object " + obj.name + " lives in kind " + localKind
                    + "; not adopting it.");
                return false;
            }

            Bind(obj, id);
            CoopPlugin.Log.LogInfo("[placement] adopted authoritative furniture identity " + id
                + " for boxed object " + obj.name + ".");
            return true;
        }

        private static bool TryParseFurnitureEntityId(string entityId, long worldEpoch, out int key)
        {
            key = 0;
            if (string.IsNullOrEmpty(entityId) || worldEpoch <= 0)
            {
                return false;
            }

            var parts = entityId.Split(':');
            return parts.Length == 3 && parts[0] == "furniture-placement"
                && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var entityEpoch) && entityEpoch == worldEpoch
                && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out key);
        }

        private static bool TryGetId(int kind, InteractableObject obj, out ushort id, bool host)
        {
            id = Invalid;
            if (obj == null || PlacementInterop.GetList(PlacementInterop.FindShelfManager(), kind)
                == null)
            {
                return false;
            }

            if (host)
            {
                id = AssignHost(obj);
                return id != Invalid;
            }

            return TryGet(obj, out id);
        }

        internal static bool TryResolve(ShelfManager manager, int kind, ushort id,
            out InteractableObject result)
        {
            result = null;
            var list = kind == PlacementApi.DecorationKind ? null : PlacementInterop.GetList(manager, kind);
            if (list == null || id == Invalid)
            {
                return false;
            }

            if (ById.TryGetValue(id, out var mapped) && mapped != null && list.Contains(mapped))
            {
                result = mapped;
                return true;
            }

            return false;
        }

        internal static ushort ObjectIdFromCompartmentKey(int key)
            => (ushort)((key >> 8) & 0xFFFF);

        internal static ushort ObjectIdFromObjectKey(int key)
            => (ushort)(key & 0xFFFF);
    }

    internal sealed class PlacementPopulationState
    {
        internal void Reset() => PlacementIdentity.Reset();

        internal PlacementPopulationMessage BuildMessage(WorldTransferManifest manifest)
        {
            var manager = PlacementInterop.FindShelfManager();
            if (manager == null)
            {
                return null;
            }

            if (manifest == null)
            {
                CoopPlugin.Log.LogWarning("[placement] building a baseline without a frozen "
                    + "save-time manifest; falling back to live list indices. A guest that joined "
                    + "before this baseline may bind the wrong object if its list order differs.");
            }

            var message = new PlacementPopulationMessage();
            for (var kind = 0; kind < PlacementApi.KindCount; kind++)
            {
                message.Entries.Add(BuildKind(manager, kind, manifest));
            }

            return message;
        }

        /// <summary>Host: freeze, for one connection, the slot each placement entity occupies in
        /// its kind's live list at the instant the save is written. The guest loads that exact
        /// order, so the slot is the guest's binding key for an entity it already has. Ids are
        /// assigned here so the frozen slot and the later baseline share one stable identity.</summary>
        internal void CaptureTransferManifest(WorldTransferManifest manifest)
        {
            if (manifest == null)
            {
                return;
            }

            var manager = PlacementInterop.FindShelfManager();
            for (var kind = 0; kind < PlacementApi.KindCount; kind++)
            {
                if (kind == PlacementApi.DecorationKind)
                {
                    continue;
                }

                var list = PlacementInterop.GetList(manager, kind);
                var order = 0;
                for (var i = 0; list != null && i < list.Count; i++)
                {
                    if (list[i] is not InteractableObject obj)
                    {
                        continue;
                    }

                    manifest.SetPlacementSlot(kind, PlacementIdentity.AssignHost(obj), order);
                    order++;
                }
            }
        }

        internal bool Apply(PlacementPopulationMessage message)
        {
            var manager = PlacementInterop.FindShelfManager();
            for (var kind = 0; kind < message.Entries.Count; kind++)
            {
                if (kind != PlacementApi.DecorationKind
                    && PlacementInterop.GetList(manager, kind) == null)
                {
                    return false;
                }
            }

            for (var kind = 0; kind < message.Entries.Count; kind++)
            {
                ApplyKind(manager, kind, message.Entries[kind]);
            }

            return true;
        }

        private static List<PlacementPopulationEntry> BuildKind(ShelfManager manager, int kind,
            WorldTransferManifest manifest)
        {
            var list = PlacementInterop.GetList(manager, kind);
            var result = new List<PlacementPopulationEntry>();
            var order = 0;
            for (var i = 0; list != null && i < list.Count; i++)
            {
                if (list[i] is not InteractableObject obj)
                {
                    continue;
                }

                var id = PlacementIdentity.AssignHost(obj);
                // The slot is the save-time manifest key the guest already has. With no manifest it
                // is the live save-order index (a scene-reload re-baseline); when a manifest exists
                // but names no slot for this id, the entity was created after the snapshot, so it
                // must be materialized by its descriptor (-1) rather than bound to a save object.
                var slot = -1;
                if (manifest != null)
                {
                    if (!manifest.TryGetPlacementSlot(kind, id, out slot))
                    {
                        slot = -1;
                    }
                }
                else
                {
                    slot = order;
                }

                order++;

                var entry = new PlacementPopulationEntry
                {
                    Id = id,
                    Slot = slot,
                    ObjType = (int)obj.m_ObjectType,
                    Pos = obj.transform.position,
                    Rot = obj.transform.rotation,
                };
                if (PlacementInterop.IsBoxed(obj) && obj.GetPackagingBoxShelf() != null)
                {
                    entry.IsBoxed = true;
                    entry.BoxedPos = obj.GetPackagingBoxShelf().transform.position;
                    entry.BoxedRot = obj.GetPackagingBoxShelf().transform.rotation;
                }

                result.Add(entry);
            }

            return result;
        }

        private static void ApplyKind(ShelfManager manager, int kind,
            List<PlacementPopulationEntry> wanted)
        {
            var list = PlacementInterop.GetList(manager, kind);
            if (list == null)
            {
                return;
            }

            for (var ordinal = 0; ordinal < wanted.Count; ordinal++)
            {
                var entry = wanted[ordinal];
                var obj = ResolveBaselineObject(manager, kind, list, entry);
                if (obj == null)
                {
                    CoopPlugin.Log.LogWarning("[placement] baseline id=" + entry.Id + " kind=" + kind
                        + " type=" + entry.ObjType + " boxed=" + entry.IsBoxed
                        + " did not resolve and could not be recreated.");
                    continue;
                }

                ReconcileBaselineObject(obj, entry);
            }
        }

        /// <summary>The local object a baseline entry names, bound by its carried stable id. The id
        /// may already be bound (an earlier baseline or a delta bound it), or it may name the object
        /// at the entry's explicit transfer-time slot: host and guest reproduce the same list order
        /// from the transferred save, so the slot belongs to that id. The slot is only trusted when
        /// its object type matches the entry, so a save whose list order diverged (a different
        /// furniture item now occupies that slot) can never bind an id to the wrong object. An id
        /// that names no local object is recreated through the game's factory as the exact object it
        /// names; a same-type or nearby object is never adopted.</summary>
        private static InteractableObject ResolveBaselineObject(ShelfManager manager, int kind,
            IList list, PlacementPopulationEntry entry)
        {
            if (PlacementIdentity.TryResolve(manager, kind, entry.Id, out var bound))
            {
                return bound;
            }

            if (entry.Slot >= 0 && entry.Slot < list.Count
                && list[entry.Slot] is InteractableObject slot
                && (int)slot.m_ObjectType == entry.ObjType
                && !PlacementIdentity.IsIdentified(slot))
            {
                PlacementIdentity.Bind(slot, entry.Id);
                return slot;
            }

            return RecreateBaselineObject(entry);
        }

        private static InteractableObject RecreateBaselineObject(PlacementPopulationEntry entry)
        {
            if (entry.ObjType == PlacementInterop.NoType)
            {
                return null;
            }

            var obj = entry.IsBoxed
                ? PlacementInterop.SpawnBoxedObject(entry.ObjType, entry.BoxedPos, entry.BoxedRot)
                : PlacementInterop.SpawnPlacedObject(entry.ObjType, entry.Pos, entry.Rot);
            if (obj == null)
            {
                return null;
            }

            PlacementIdentity.Bind(obj, entry.Id);
            CoopPlugin.Log.LogInfo("[placement] baseline recreated missing id=" + entry.Id
                + " type=" + entry.ObjType + " boxed=" + entry.IsBoxed + " object=" + obj.name + ".");
            return obj;
        }

        private static void ReconcileBaselineObject(InteractableObject obj,
            PlacementPopulationEntry entry)
        {
            if (PlacementInterop.IsBoxed(obj) != entry.IsBoxed)
            {
                if (entry.IsBoxed)
                {
                    if (!PlacementInterop.IsBoxEngineOwned(obj))
                    {
                        PlacementInterop.BoxUpPlacedObject(obj);
                    }
                }
                else
                {
                    PlacementInterop.PlaceBoxedObject(obj, entry.Pos, entry.Rot);
                }
            }

            if (!entry.IsBoxed)
            {
                obj.transform.SetPositionAndRotation(entry.Pos, entry.Rot);
                PlacementInterop.SyncTagGroup(obj.transform);
            }
        }
    }

    internal static class PlacementMoveState
    {
        internal static List<PlacementMoveEntry> Build(ShelfManager manager)
        {
            var result = new List<PlacementMoveEntry>();
            for (var kind = 0; kind < PlacementApi.KindCount; kind++)
            {
                var list = PlacementInterop.GetList(manager, kind);
                for (var i = 0; list != null && i < list.Count; i++)
                {
                    if (list[i] is not InteractableObject obj || obj.GetIsMovingObject()
                        || !PlacementIdentity.TryMakeObjectKey(kind, obj, out var key,
                            WorldHostBehaviour.Active != null))
                    {
                        continue;
                    }

                    result.Add(new PlacementMoveEntry
                    {
                        Key = key,
                        Type = PlacementInterop.TypeIdOf(obj),
                        Pos = obj.transform.position,
                        Rot = obj.transform.rotation,
                        IsBoxed = PlacementInterop.IsBoxed(obj),
                        BoxedPos = PlacementInterop.IsBoxed(obj)
                            && obj.GetPackagingBoxShelf() != null
                            ? obj.GetPackagingBoxShelf().transform.position : Vector3.zero,
                        BoxedRot = PlacementInterop.IsBoxed(obj)
                            && obj.GetPackagingBoxShelf() != null
                            ? obj.GetPackagingBoxShelf().transform.rotation : Quaternion.identity,
                    });
                }
            }

            return result;
        }

        internal static bool Apply(PlacementMoveEntry entry, bool settle)
            => PlacementEntityState.Apply(entry, settle);

        internal static Component ResolveObjectByKey(int key)
        {
            var manager = PlacementInterop.FindShelfManager();
            var kind = key >> 24;
            return manager == null || kind < 0 || kind >= PlacementApi.KindCount
                ? null
                : PlacementIdentity.TryResolve(manager, kind,
                PlacementIdentity.ObjectIdFromObjectKey(key), out var result) ? result : null;
        }
    }

    internal sealed class PlacementEntitySnapshot
    {
        internal int Key;
        internal int Type;
        internal Vector3 Pos;
        internal Quaternion Rot;
        internal bool IsBoxed;
        internal Vector3 BoxedPos;
        internal Quaternion BoxedRot;

        internal PlacementMoveEntry ToEntry()
            => new()
            {
                Key = Key,
                Type = Type,
                Pos = Pos,
                Rot = Rot,
                IsBoxed = IsBoxed,
                BoxedPos = BoxedPos,
                BoxedRot = BoxedRot,
            };

        internal bool SameValue(PlacementEntitySnapshot other)
            => other != null && Type == other.Type && Pos == other.Pos && Rot == other.Rot
                && IsBoxed == other.IsBoxed && BoxedPos == other.BoxedPos
                && BoxedRot == other.BoxedRot;
    }

    internal static class PlacementEntityState
    {
        internal static PlacementEntitySnapshot Capture(InteractableObject obj, int key)
        {
            var boxed = PlacementInterop.IsBoxed(obj);
            var package = obj?.GetPackagingBoxShelf();
            return new PlacementEntitySnapshot
            {
                Key = key,
                Type = PlacementInterop.TypeIdOf(obj),
                Pos = obj == null ? Vector3.zero : obj.transform.position,
                Rot = obj == null ? Quaternion.identity : obj.transform.rotation,
                IsBoxed = boxed,
                BoxedPos = boxed && package != null ? package.transform.position : Vector3.zero,
                BoxedRot = boxed && package != null ? package.transform.rotation
                    : Quaternion.identity,
            };
        }

        internal static bool Apply(PlacementMoveEntry entry, bool settle)
        {
            if (entry == null)
            {
                return false;
            }

            var obj = PlacementMoveState.ResolveObjectByKey(entry.Key) as InteractableObject;
            if (obj == null || (entry.Type != PlacementInterop.NoType
                && PlacementInterop.TypeIdOf(obj) != entry.Type))
            {
                return false;
            }

            if (entry.IsBoxed != PlacementInterop.IsBoxed(obj))
            {
                if (entry.IsBoxed)
                {
                    // The world box engine owns the packaging box and its boxed flag; the
                    // placement channel must never box an object it already tracks.
                    if (!PlacementInterop.IsBoxEngineOwned(obj))
                    {
                        PlacementInterop.BoxUpPlacedObject(obj);
                    }
                }
                else
                {
                    PlacementInterop.PlaceBoxedObject(obj, entry.Pos, entry.Rot);
                }
            }

            if (entry.IsBoxed && obj.GetPackagingBoxShelf() != null)
            {
                // A package in a player's hand rides that player's avatar; its pose belongs to the
                // box channel. A placement refresh (the host republishes the entity right after a
                // box-up) must not yank it back out of the hands to the recorded world pose.
                var package = obj.GetPackagingBoxShelf();
                if (!PlacementInterop.IsBeingHeld(package))
                {
                    package.transform.SetPositionAndRotation(entry.BoxedPos, entry.BoxedRot);
                }
            }
            else
            {
                obj.transform.SetPositionAndRotation(entry.Pos, entry.Rot);
                PlacementInterop.SyncTagGroup(obj.transform);
                if (obj is InteractableAutoPackOpener opener)
                {
                    PlacementInterop.InvokeOpenerUi(opener);
                }
            }

            if (settle && obj.GetIsMovingObject())
            {
                obj.PlaceMovedObject();
            }

            return true;
        }

        internal static bool Apply(PlacementDeltaMessage message)
        {
            if (message == null || message.Entity == null)
            {
                return false;
            }

            if (message.Operation == PlacementDeltaMessage.Remove)
            {
                var removed = PlacementMoveState.ResolveObjectByKey(message.Entity.Key)
                    as InteractableObject;
                if (removed != null)
                {
                    PlacementIdentity.Forget(removed);
                }

                return true;
            }

            if (message.Operation != PlacementDeltaMessage.Add
                && message.Operation != PlacementDeltaMessage.Update
                && message.Operation != PlacementDeltaMessage.Pose)
            {
                return false;
            }

            var entry = message.Entity;
            var objectKey = entry.Key;
            var obj = PlacementMoveState.ResolveObjectByKey(objectKey) as InteractableObject;
            if (obj == null)
            {
                // The stable key names no local object yet. Boxed furniture is owned by the box
                // channel: its descriptor names this exact entity and binds it onto the local
                // object (the peer's own predicted box, or one materialized from the descriptor),
                // after which this delta is retried. Never construct a second object here - that
                // duplicate would be unknown to the host and could never be interacted with.
                return false;
            }

            return Apply(entry, false);
        }
    }
}
