using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using CardShopCoop.Modules.GameObjectIdentity;
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
            => PlacementObjectIdentity.TryMakeCompartmentKey(kind, obj, compartment, out key,
                WorldHostBehaviour.Active != null);

        public static bool TryMakeObjectKey(int kind, InteractableObject obj, out int key)
            => PlacementObjectIdentity.TryMakeObjectKey(kind, obj, out key,
                WorldHostBehaviour.Active != null);
        public static bool IsPlacementIdentityReady
            => WorldClientBehaviour.IsPlacementIdentityReady;

        public static bool TryResolve(ShelfManager shelfManager, int kind, ushort id,
            out InteractableObject result)
            => PlacementObjectIdentity.TryResolveInKind(shelfManager, kind, id, out result);

        public static ushort ObjectIdFromCompartmentKey(int key)
            => PlacementObjectIdentity.ObjectIdFromCompartmentKey(key);

        public static ushort ObjectIdFromObjectKey(int key)
            => PlacementObjectIdentity.ObjectIdFromObjectKey(key);

        public static Component ResolveObjectByKey(int key)
            => PlacementMoveState.ResolveObjectByKey(key);
    }

    /// <summary>Reference identity for placement objects. Unity's overloaded equality treats a
    /// destroyed object as null, which makes the object unsuitable as a dictionary key once it is
    /// being torn down; every dictionary using this comparer removes its entry in the destroy hook
    /// before the component becomes fake-null.</summary>
    internal sealed class PlacementObjectComparer : IEqualityComparer<InteractableObject>
    {
        internal static readonly PlacementObjectComparer Instance = new();

        public bool Equals(InteractableObject left, InteractableObject right)
            => ReferenceEquals(left, right);

        public int GetHashCode(InteractableObject value)
            => ReferenceEquals(value, null) ? 0 : RuntimeHelpers.GetHashCode(value);
    }

    internal static class PlacementInterop
    {
        // Raised from inside the placement/box apply paths that its own subscribers drive, so it is
        // coalesced: a nested change is delivered after the current delivery instead of recursing
        // back into the subscriber that caused it. See CoalescedEvent.
        private static readonly CoalescedEvent<int> StructureChangedEvent = new();

        internal static event Action<int> StructureChanged
        {
            add => StructureChangedEvent.Handlers += value;
            remove => StructureChangedEvent.Handlers -= value;
        }

        private static readonly FieldInfo FiMoveValid = AccessTools.Field(
            typeof(InteractableObject), "m_IsMovingObjectValidState");
        private static readonly FieldInfo FiMovingObject = AccessTools.Field(
            typeof(InteractableObject), "m_IsMovingObject");
        private static readonly FieldInfo FiSnappingPos = AccessTools.Field(
            typeof(InteractableObject), "m_IsSnappingPos");
        private static readonly FieldInfo FiLastHitTransform = AccessTools.Field(
            typeof(InteractableObject), "m_LastHitTransform");
        private static readonly FieldInfo FiHitNormal = AccessTools.Field(
            typeof(InteractableObject), "m_HitNormal");
        private static readonly FieldInfo FiIsFlipped = AccessTools.Field(
            typeof(InteractableObject), "m_IsFlipped");
        private static readonly FieldInfo FiOriginalLayer = AccessTools.Field(
            typeof(InteractableObject), "m_OriginalLayer");
        private static readonly FieldInfo FiItemCompartments = AccessTools.Field(
            typeof(InteractableObject), "m_ItemCompartmentList");
        private static readonly FieldInfo FiPlacedBlocker = AccessTools.Field(
            typeof(InteractableObject), "m_DecoPlaccedLockedRoomBlocker");
        private static readonly FieldInfo FiVerticalSnapWarehouse = AccessTools.Field(
            typeof(InteractableObject), "m_IsVerticalSnapToWarehouseWall");
        private static readonly FieldInfo FiVerticalSnapIndex = AccessTools.Field(
            typeof(InteractableObject), "m_VerticalSnapWallIndex");
        private static readonly FieldInfo FiStopSnap = AccessTools.Field(
            typeof(InteractionPlayerController), "m_IsStopSnapWhenMoving");
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

        private static bool _storedBoxMoveProbed;
        private static MethodInfo _setShelfMoving;
        private static object _storedBoxBatcher;
        private static bool _wakeSettledProbed;
        private static MethodInfo _wakeSettledBoxesAbove;

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

        /// <summary>Stable, null-safe label for placement diagnostics.</summary>
        internal static string Describe(InteractableObject obj)
            => obj == null ? "<null>" : obj.name + "#" + obj.GetInstanceID();

        /// <summary>True when the identity module has bound this object, so the global wire
        /// converter can name it. An in-flight decoration has no placed object and stays unbound
        /// until its placement is accepted.</summary>
        internal static bool HasSharedIdentity(InteractableObject obj)
            => obj != null && GameObjectIdentityApi.TryGetId(obj, out _);

        /// <summary>True when the object is bound to the placement identity of
        /// <paramref name="kind"/>. This is the identity module's own kind/value, not a local
        /// fallback id; it keeps a box or decoration that happens to sit in a generic list out of
        /// the placement state channel.</summary>
        internal static bool HasPlacementIdentity(InteractableObject obj, int kind)
            => obj != null && kind >= 0
                && GameObjectIdentityApi.TryGetId(obj, out var id)
                && id.Kind == GameObjectKind.Furniture && id.IsValid;

        internal static bool IsBoxed(InteractableObject obj)
            => obj != null && obj.GetIsBoxedUp();

        internal static void NotifyStructureChanged(int kind)
        {
            try
            {
                StructureChangedEvent.Raise(kind);
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("placement structure subscriber failed: " + error.Message);
            }
        }

        /// <summary>The identity module is about to destroy a placement entity. Both world roles
        /// release the pose/hold state they keep for that object while the component is still
        /// readable. The key remains on this boundary because the identity module supplies it; the
        /// placement module itself keys its state by the live object.</summary>
        internal static void NotifyPlacementObjectDestroyed(int key, InteractableObject obj)
        {
            WorldHostBehaviour.OnPlacementObjectDestroyed(obj);
            WorldClientBehaviour.OnPlacementObjectDestroyed(obj);
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
            WarehouseShelfInteraction.SetStoredBoxVisuals(obj, true);
        }

        internal static void BoxUpPlacedObject(InteractableObject obj)
        {
            if (obj != null && !IsBoxed(obj) && !obj.GetIsMovingObject())
            {
                obj.BoxUpObject(false);
            }

            WarehouseShelfInteraction.SetStoredBoxVisuals(obj, false);
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

        internal static void SetMoveValidity(InteractableObject obj, bool valid)
        {
            if (obj != null)
            {
                FiMoveValid?.SetValue(obj, valid);
            }
        }

        internal static void SetMovingObject(InteractableObject obj, bool moving)
        {
            if (obj != null)
            {
                FiMovingObject?.SetValue(obj, moving);
            }
        }

        internal static void SetSnappingPos(InteractableObject obj, bool snapping)
        {
            if (obj != null)
            {
                FiSnappingPos?.SetValue(obj, snapping);
            }
        }

        internal static void ClearLastHitTransform(InteractableObject obj)
        {
            if (obj != null)
            {
                FiLastHitTransform?.SetValue(obj, null);
            }
        }

        internal static Vector3 ReadHitNormal(InteractableObject obj)
            => obj != null && FiHitNormal?.GetValue(obj) is Vector3 normal ? normal : Vector3.up;

        internal static bool ReadIsFlipped(InteractableObject obj)
            => obj != null && FiIsFlipped?.GetValue(obj) is bool flipped && flipped;

        internal static void SetIsFlipped(InteractableObject obj, bool flipped)
        {
            if (obj != null)
            {
                FiIsFlipped?.SetValue(obj, flipped);
            }
        }

        internal static void SetOriginalLayer(InteractableObject obj, int layer)
        {
            if (obj != null)
            {
                FiOriginalLayer?.SetValue(obj, layer);
            }
        }

        internal static bool ReadStopSnap()
        {
            var controller = SceneRef<InteractionPlayerController>.Get();
            return controller != null && FiStopSnap?.GetValue(controller) is bool stop && stop;
        }

        internal static void SetStopSnap(bool stop)
        {
            var controller = SceneRef<InteractionPlayerController>.Get();
            if (controller != null)
            {
                FiStopSnap?.SetValue(controller, stop);
            }
        }

        internal static float ReadLocalRayDistance()
        {
            var controller = SceneRef<InteractionPlayerController>.Get();
            return controller?.m_RayDistance ?? 0f;
        }

        internal static bool TryGetLocalCamera(out Vector3 position, out Quaternion rotation)
        {
            var camera = SceneRef<InteractionPlayerController>.Get()?.m_Cam;
            if (camera != null)
            {
                position = camera.transform.position;
                rotation = camera.transform.rotation;
                return true;
            }

            position = default(Vector3);
            rotation = Quaternion.identity;
            return false;
        }

        /// <summary>Clears the vertical-decoration registration vanilla clears in
        /// <c>StartMoveObject</c>. The block type exists in both supported builds.</summary>
        internal static void ClearVerticalPlacementState(InteractableObject obj)
        {
            if (obj == null)
            {
                return;
            }

            if (FiPlacedBlocker?.GetValue(obj) is LockedRoomBlocker blocker)
            {
                blocker.RemoveFromVerticalDecoObjectList(obj);
                FiPlacedBlocker.SetValue(obj, null);
            }

            FiVerticalSnapWarehouse?.SetValue(obj, false);
            FiVerticalSnapIndex?.SetValue(obj, -1);
        }

        /// <summary>The warehouse's stored-box batcher must know the shelf is moving so it rebuilds
        /// its batched matrices against the moving transform. Resolved by name; a build without the
        /// batcher degrades to no-op.</summary>
        internal static void SetStoredBoxShelfMoving(InteractableObject obj, bool moving)
        {
            if (obj == null)
            {
                return;
            }

            ProbeStoredBoxMove();
            if (_setShelfMoving == null || FiItemCompartments?.GetValue(obj) is not IList list)
            {
                return;
            }

            var target = _setShelfMoving.IsStatic ? null : _storedBoxBatcher;
            if (!_setShelfMoving.IsStatic && target == null)
            {
                return;
            }

            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] != null)
                {
                    _setShelfMoving.Invoke(target, new object[] { list[i], moving });
                }
            }
        }

        /// <summary>Beta only: wake boxes resting above the collider before it is disabled.</summary>
        internal static void WakeSettledBoxesAbove(InteractableObject obj)
        {
            if (obj?.m_BoxCollider == null || !obj.m_BoxCollider.enabled)
            {
                return;
            }

            ProbeWakeSettled();
            _wakeSettledBoxesAbove?.Invoke(null, new object[] { obj.m_BoxCollider.bounds });
        }

        private static void ProbeStoredBoxMove()
        {
            if (_storedBoxMoveProbed)
            {
                return;
            }

            _storedBoxMoveProbed = true;
            var type = typeof(ShelfCompartment).Assembly.GetType("StoredBoxVisualBatcher", false);
            if (type == null)
            {
                return;
            }

            _setShelfMoving = type.GetMethod("SetShelfMoving",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(ShelfCompartment), typeof(bool) }, null);
            if (_setShelfMoving != null)
            {
                return;
            }

            _setShelfMoving = type.GetMethod("SetShelfMoving",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(ShelfCompartment), typeof(bool) }, null);
            var instance = type.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static);
            _storedBoxBatcher = instance?.GetValue(null);
        }

        private static void ProbeWakeSettled()
        {
            if (_wakeSettledProbed)
            {
                return;
            }

            _wakeSettledProbed = true;
            _wakeSettledBoxesAbove = AccessTools.Method(
                typeof(InteractablePackagingBox), "WakeSettledBoxesAbove");
        }

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

            var list = RestockInterop.ShelfBoxes;
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
            // Furniture packaging is placement state, not a box entity owned by the box engine.
            // The package is only a view of the placed furniture.
            if (box is InteractablePackagingBox_Shelf)
            {
                return false;
            }

            return WorldClientBehaviour.IsKnownPackagingBox(box)
                || WorldHostBehaviour.IsKnownPackagingBox(box);
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
            WarehouseShelfInteraction.SetStoredBoxVisuals(obj, false);
            var package = obj.GetPackagingBoxShelf();
            package?.transform.SetPositionAndRotation(position, rotation);

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

    /// <summary>
    /// Host: freezes the save-time list slot of every placement entity for one connection at the
    /// instant its world save is written. The guest loads that exact order, so the slot is how the
    /// identity baseline binds an entity the guest already has instead of materializing a second
    /// one. The slots themselves live in <see cref="WorldTransferManifest"/> beside the box slots.
    /// </summary>
    internal sealed class PlacementTransferSlots
    {
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

                    // The id is minted here if the object predates the session, so the frozen slot
                    // and the later identity baseline name the same stable entity.
                    manifest.SetPlacementSlot(kind, PlacementObjectIdentity.AssignHost(obj), order);
                    order++;
                }
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
                    if (list[i] is not InteractableObject obj || obj == null
                        || obj.GetIsMovingObject()
                        || !PlacementInterop.HasPlacementIdentity(obj, kind))
                    {
                        continue;
                    }

                    var boxed = PlacementInterop.IsBoxed(obj);
                    var package = boxed ? obj.GetPackagingBoxShelf() : null;
                    result.Add(new PlacementMoveEntry
                    {
                        Entity = obj,
                        Pos = obj.transform.position,
                        Rot = obj.transform.rotation,
                        IsBoxed = boxed,
                        BoxedPos = boxed
                            ? (package != null ? package.transform.position : obj.transform.position)
                            : Vector3.zero,
                        BoxedRot = boxed
                            ? (package != null ? package.transform.rotation : obj.transform.rotation)
                            : Quaternion.identity,
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
                : PlacementObjectIdentity.TryResolveInKind(manager, kind,
                    PlacementObjectIdentity.ObjectIdFromObjectKey(key), out var result)
                    ? result : null;
        }
    }

    internal sealed class PlacementEntitySnapshot
    {
        internal InteractableObject Entity;
        internal Vector3 Pos;
        internal Quaternion Rot;
        internal bool IsBoxed;
        internal Vector3 BoxedPos;
        internal Quaternion BoxedRot;

        internal PlacementMoveEntry ToEntry()
            => new()
            {
                Entity = Entity,
                Pos = Pos,
                Rot = Rot,
                IsBoxed = IsBoxed,
                BoxedPos = BoxedPos,
                BoxedRot = BoxedRot,
            };
    }

    internal static class PlacementEntityState
    {
        internal static PlacementEntitySnapshot Capture(InteractableObject obj)
        {
            var boxed = PlacementInterop.IsBoxed(obj);
            var package = obj?.GetPackagingBoxShelf();
            return new PlacementEntitySnapshot
            {
                Entity = obj,
                Pos = obj == null ? Vector3.zero : obj.transform.position,
                Rot = obj == null ? Quaternion.identity : obj.transform.rotation,
                IsBoxed = boxed,
                BoxedPos = boxed
                    ? (package != null ? package.transform.position : obj.transform.position)
                    : Vector3.zero,
                BoxedRot = boxed
                    ? (package != null ? package.transform.rotation : obj.transform.rotation)
                    : Quaternion.identity,
            };
        }

        internal static bool Apply(PlacementMoveEntry entry, bool settle)
        {
            if (entry == null || entry.Entity == null)
            {
                return false;
            }

            var obj = entry.Entity;

            if (entry.IsBoxed != PlacementInterop.IsBoxed(obj))
            {
                if (entry.IsBoxed)
                {
                    // Furniture boxing is placement state. The package is a view of the object.
                    PlacementInterop.BoxUpPlacedObject(obj);
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

        /// <summary>Applies a state delta. Creation and removal are never state operations: a
        /// delta without a live entity is dropped, never a request to build or destroy an object.</summary>
        internal static bool Apply(PlacementDeltaMessage message)
        {
            if (message == null || message.Entity == null || message.Entity.Entity == null)
            {
                return false;
            }

            if (message.Operation != PlacementDeltaMessage.Update
                && message.Operation != PlacementDeltaMessage.Pose)
            {
                return false;
            }

            return Apply(message.Entity, false);
        }
    }
}
