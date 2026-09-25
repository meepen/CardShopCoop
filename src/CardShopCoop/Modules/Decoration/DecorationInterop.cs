using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using CardShopCoop.Runtime;
using CardShopCoop.Util;
using UnityEngine;

namespace CardShopCoop.Modules.Decoration
{
    /// <summary>
    /// The two supported game builds keep the same decoration concepts but this module must not
    /// rely on Unity 6-only scene lookup APIs or private game members. Required surfaces are
    /// resolved once here and missing surfaces fail module initialization loudly.
    /// </summary>
    internal static class DecorationInterop
    {
        private sealed class ReferenceComparer : IEqualityComparer<InteractableObject>
        {
            public bool Equals(InteractableObject left, InteractableObject right)
                => ReferenceEquals(left, right);

            public int GetHashCode(InteractableObject value)
                => RuntimeHelpers.GetHashCode(value);
        }

        private static readonly MethodInfo FindObjectsWithInactive = typeof(UnityEngine.Object).GetMethod(
            "FindObjectsOfType", new[] { typeof(Type), typeof(bool) });
        private static readonly MethodInfo FindObjects = typeof(UnityEngine.Object).GetMethod(
            "FindObjectsOfType", new[] { typeof(Type) });

        private static readonly FieldInfo FiWall = ReflectionSurface.RequiredField(
            typeof(CPlayerData), "m_EquippedWallDecoIndex");
        private static readonly FieldInfo FiWallB = ReflectionSurface.RequiredField(
            typeof(CPlayerData), "m_EquippedWallDecoIndexB");
        private static readonly FieldInfo FiFloor = ReflectionSurface.RequiredField(
            typeof(CPlayerData), "m_EquippedFloorDecoIndex");
        private static readonly FieldInfo FiFloorB = ReflectionSurface.RequiredField(
            typeof(CPlayerData), "m_EquippedFloorDecoIndexB");
        private static readonly FieldInfo FiCeiling = ReflectionSurface.RequiredField(
            typeof(CPlayerData), "m_EquippedCeilingDecoIndex");
        private static readonly FieldInfo FiCeilingB = ReflectionSurface.RequiredField(
            typeof(CPlayerData), "m_EquippedCeilingDecoIndexB");
        private static readonly FieldInfo FiWallUnlocks = ReflectionSurface.RequiredField(
            typeof(CPlayerData), "m_UnlockedDecoWallList");
        private static readonly FieldInfo FiFloorUnlocks = ReflectionSurface.RequiredField(
            typeof(CPlayerData), "m_UnlockedDecoFloorList");
        private static readonly FieldInfo FiCeilingUnlocks = ReflectionSurface.RequiredField(
            typeof(CPlayerData), "m_UnlockedDecoCeilingList");
        private static readonly FieldInfo FiInventory = ReflectionSurface.RequiredField(
            typeof(CPlayerData), "m_DecorationInventoryList");
        private static readonly MethodInfo MiAddInventory = ReflectionSurface.RequiredMethod(
            typeof(CPlayerData), "AddDecoItemToInventory", typeof(EDecoObject), typeof(int));
        private static readonly MethodInfo MiGetInventory = ReflectionSurface.RequiredMethod(
            typeof(CPlayerData), "GetDecoItemInventoryCount", typeof(EDecoObject));
        private static readonly MethodInfo MiSpawn = ReflectionSurface.RequiredMethod(
            typeof(ShelfManager), "SpawnDecoObject", typeof(EDecoObject));
        private static readonly MethodInfo MiGetList = ReflectionSurface.RequiredMethod(
            typeof(ShelfManager), "GetDecoObjectList");
        private static readonly MethodInfo MiRemoveFromList = ReflectionSurface.RequiredMethod(
            typeof(ShelfManager), "RemoveDecoObject", typeof(InteractableObject));
        private static readonly MethodInfo MiInit = ReflectionSurface.RequiredMethod(
            typeof(InteractableObject), "Init");
        private static readonly MethodInfo MiStartMoveObject = ReflectionSurface.RequiredMethod(
            typeof(InteractableObject), "StartMoveObject");
        private static readonly MethodInfo MiSetTargetMovePosition = ReflectionSurface.RequiredMethod(
            typeof(InteractableObject), "SetTargetMovePosition", typeof(Vector3), typeof(Vector3),
            typeof(Transform));
        private static readonly MethodInfo MiUpdate = ReflectionSurface.RequiredMethod(
            typeof(InteractableObject), "Update");
        private static readonly MethodInfo MiPlaceMovedObject = ReflectionSurface.RequiredMethod(
            typeof(InteractableObject), "PlaceMovedObject");
        private static readonly MethodInfo MiOnPlacedMovedObject = ReflectionSurface.RequiredMethod(
            typeof(InteractableObject), "OnPlacedMovedObject");
        private static readonly MethodInfo MiOnDestroyed = ReflectionSurface.RequiredMethod(
            typeof(InteractableObject), "OnDestroyed");
        private static readonly MethodInfo MiBoxUpObject = ReflectionSurface.RequiredMethod(
            typeof(InteractableObject), "BoxUpObject", typeof(bool));
        private static readonly MethodInfo MiSetSnap = ReflectionSurface.RequiredMethod(
            typeof(InteractableObject), "SetVerticalSnapToWarehouseWall", typeof(bool), typeof(int));
        private static readonly FieldInfo FiValidPlacement = ReflectionSurface.RequiredField(
            typeof(InteractableObject), "m_IsMovingObjectValidState");
        private static readonly FieldInfo FiObjectType = ReflectionSurface.RequiredField(
            typeof(InteractableObject), "m_DecoObjectType");
        private static readonly FieldInfo FiVertical = ReflectionSurface.RequiredField(
            typeof(InteractableObject), "m_IsDecorationVertical");
        private static readonly FieldInfo FiPlacedBlocker = ReflectionSurface.RequiredField(
            typeof(InteractableObject), "m_DecoPlaccedLockedRoomBlocker");
        private static readonly MethodInfo MiGetSnap = ReflectionSurface.RequiredMethod(
            typeof(InteractableObject), "GetIsVerticalSnapToWarehouseWall");
        private static readonly MethodInfo MiGetWall = ReflectionSurface.RequiredMethod(
            typeof(InteractableObject), "GetVerticalSnapWallIndex");

        private static readonly FieldInfo FiShopCategory = ReflectionSurface.RequiredField(
            typeof(ShopBuyDecoUIScreen), "m_CategoryIndex");
        private static readonly MethodInfo MiBuyShop = ReflectionSurface.RequiredMethod(
            typeof(ShopBuyDecoUIScreen), "OnPressBuyShopDeco", typeof(int), typeof(float));
        private static readonly MethodInfo MiBuyItem = ReflectionSurface.RequiredMethod(
            typeof(ShopBuyDecoUIScreen), "OnPressBuyShopDecoItem", typeof(EDecoObject), typeof(float));
        private static readonly MethodInfo MiSwitchShop = ReflectionSurface.RequiredMethod(
            typeof(PlaceDecoUIScreen), "OnPressSwitchShopDeco", typeof(int), typeof(bool));
        private static readonly FieldInfo FiObjectData = ReflectionSurface.RequiredField(
            typeof(InventoryBase), "m_ObjectData_SO");
        private static readonly FieldInfo FiWallData = ReflectionSurface.RequiredField(
            typeof(ShelfData_ScriptableObject), "m_WallDecoDataList");
        private static readonly FieldInfo FiFloorData = ReflectionSurface.RequiredField(
            typeof(ShelfData_ScriptableObject), "m_FloorDecoDataList");
        private static readonly FieldInfo FiCeilingData = ReflectionSurface.RequiredField(
            typeof(ShelfData_ScriptableObject), "m_CeilingDecoDataList");
        private static readonly FieldInfo FiItemData = ReflectionSurface.RequiredField(
            typeof(ShelfData_ScriptableObject), "m_DecoPurchaseDataList");
        private static readonly FieldInfo FiPrice = ReflectionSurface.RequiredField(
            typeof(ShopDecoData), "price");
        private static readonly FieldInfo FiItemPrice = ReflectionSurface.RequiredField(
            typeof(DecoPurchaseData), "price");

        private static readonly Dictionary<InteractableObject, long> HostIds =
            new(new ReferenceComparer());
        private static readonly Dictionary<long, InteractableObject> ClientObjects = new();
        private static long _nextHostId = 1;

        internal static void Reset()
        {
            HostIds.Clear();
            ClientObjects.Clear();
            _nextHostId = 1;
        }

        internal static DecorationSnapshot Snapshot()
        {
            if (!IsSceneReady())
            {
                throw new InvalidOperationException("Decoration scene is not ready.");
            }

            var result = new DecorationSnapshot
            {
                Wall = GetInt(FiWall),
                WallB = GetInt(FiWallB),
                Floor = GetInt(FiFloor),
                FloorB = GetInt(FiFloorB),
                Ceiling = GetInt(FiCeiling),
                CeilingB = GetInt(FiCeilingB),
                WallUnlocks = CopyBools(FiWallUnlocks),
                FloorUnlocks = CopyBools(FiFloorUnlocks),
                CeilingUnlocks = CopyBools(FiCeilingUnlocks),
                Inventory = CopyInts(FiInventory),
            };

            var live = GetLiveDecorations();
            var liveSet = new HashSet<InteractableObject>(live, new ReferenceComparer());
            var stale = new List<InteractableObject>();
            foreach (var pair in HostIds)
            {
                if (!liveSet.Contains(pair.Key))
                {
                    stale.Add(pair.Key);
                }
            }
            for (var i = 0; i < stale.Count; i++)
            {
                HostIds.Remove(stale[i]);
            }

            for (var i = 0; i < live.Count; i++)
            {
                var obj = live[i];
                if (obj == null || obj.GetIsMovingObject())
                {
                    continue;
                }

                // Reuse the object's stable id whenever it already has one. On a guest that id
                // comes from ClientObjects (assigned by the host); assigning a fresh synthetic id
                // here re-keyed every mapped object and made later host deltas miss, which
                // duplicated pieces whenever the host moved them.
                if (!TryGetHoldId(obj, out var id))
                {
                    id = _nextHostId++;
                    HostIds.Add(obj, id);
                }

                result.Placed.Add(new DecorationPose
                {
                    Id = id,
                    DecorationType = (EDecoObject)FiObjectType.GetValue(obj),
                    Position = obj.transform.position,
                    Rotation = obj.transform.rotation,
                    Vertical = GetVertical(obj),
                    WarehouseWallSnap = GetWarehouseWallSnap(obj),
                    Wall = GetWallIndex(obj),
                });
            }

            return result;
        }

        internal static void ApplySnapshot(DecorationStateMessage message,
            bool removeUnlisted = true)
        {
            ApplyList(FiWallUnlocks, message.WallUnlocks);
            ApplyList(FiFloorUnlocks, message.FloorUnlocks);
            ApplyList(FiCeilingUnlocks, message.CeilingUnlocks);
            ApplyList(FiInventory, message.Inventory);
            SetInt(FiWall, message.Wall);
            SetInt(FiWallB, message.WallB);
            SetInt(FiFloor, message.Floor);
            SetInt(FiFloorB, message.FloorB);
            SetInt(FiCeiling, message.Ceiling);
            SetInt(FiCeilingB, message.CeilingB);
            ApplyMaterials(message);
            Reconcile(message.Placed, removeUnlisted);
            RefreshUi();
        }

        internal static bool IsUnlocked(int category, int index)
        {
            var field = UnlockField(category);
            var values = field?.GetValue(null) as IList;
            return values != null && index >= 0 && index < values.Count && Convert.ToBoolean(values[index]);
        }

        internal static bool TryGetCategoryPrice(int category, int index, out float price)
        {
            price = 0f;
            var list = CategoryData(category);
            if (list == null || index < 0 || index >= list.Count || list[index] == null)
            {
                return false;
            }

            price = Convert.ToSingle(FiPrice.GetValue(list[index]));
            return price >= 0f && !float.IsNaN(price) && !float.IsInfinity(price);
        }

        internal static bool TryGetItemPrice(EDecoObject type, out float price)
        {
            price = 0f;
            var index = (int)type;
            var list = ItemData();
            if (!IsValidDeco(type) || list == null || index < 0 || index >= list.Count
                || list[index] == null)
            {
                return false;
            }

            price = Convert.ToSingle(FiItemPrice.GetValue(list[index]));
            return price >= 0f && !float.IsNaN(price) && !float.IsInfinity(price);
        }

        internal static bool HasInventory(EDecoObject type)
        {
            if (!IsValidDeco(type))
            {
                return false;
            }
            return Convert.ToInt32(MiGetInventory.Invoke(null, new object[] { type })) > 0;
        }

        internal static int InventoryCount(EDecoObject type)
        {
            if (!IsValidDeco(type))
                return 0;
            return Convert.ToInt32(MiGetInventory.Invoke(null, new object[] { type }));
        }

        internal static void AdjustInventory(EDecoObject type, int amount)
        {
            if (!IsValidDeco(type))
            {
                throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown decoration type.");
            }
            MiAddInventory.Invoke(null, new object[] { type, amount });
        }

        /// <summary>Sets the local count to the host-authoritative value. Buy, place and box-up
        /// deltas all carry the resulting count, so the local list converges without polling.</summary>
        internal static void ApplyInventoryCount(EDecoObject type, int count)
        {
            if (count < 0 || !IsValidDeco(type))
            {
                return;
            }

            var current = InventoryCount(type);
            if (current != count)
            {
                MiAddInventory.Invoke(null, new object[] { type, count - current });
            }
        }

        internal static bool TryBuyCategory(int category, int index, float price)
        {
            var screen = FindFirst(typeof(ShopBuyDecoUIScreen));
            if (screen == null)
            {
                return false;
            }

            var previous = Convert.ToInt32(FiShopCategory.GetValue(screen));
            try
            {
                FiShopCategory.SetValue(screen, category);
                MiBuyShop.Invoke(screen, new object[] { index, price });
            }
            finally
            {
                FiShopCategory.SetValue(screen, previous);
            }
            return IsUnlocked(category, index);
        }

        internal static bool TryBuyItem(EDecoObject type, float price)
        {
            var screen = FindFirst(typeof(ShopBuyDecoUIScreen));
            if (screen == null || !IsValidDeco(type))
            {
                return false;
            }

            var before = Convert.ToInt32(MiGetInventory.Invoke(null, new object[] { type }));
            MiBuyItem.Invoke(screen, new object[] { type, price });
            var after = Convert.ToInt32(MiGetInventory.Invoke(null, new object[] { type }));
            return after > before;
        }

        internal static bool TryEquip(int category, int index, bool lotB)
        {
            var screen = FindFirst(typeof(PlaceDecoUIScreen));
            if (screen == null)
            {
                return false;
            }

            var previous = Convert.ToInt32(FiShopCategory.GetValue(screen));
            try
            {
                FiShopCategory.SetValue(screen, category);
                MiSwitchShop.Invoke(screen, new object[] { index, lotB });
            }
            finally
            {
                FiShopCategory.SetValue(screen, previous);
            }
            return EquippedIndex(category, lotB) == index;
        }

        internal static bool TryPlace(DecorationPose pose, out InteractableObject placed)
            => TryPlace(pose, 0, out placed);

        internal static bool TryPlace(DecorationPose pose, long existingId,
            out InteractableObject placed)
        {
            placed = null;
            if (pose == null || !IsValidDeco(pose.DecorationType)
                || !ValidPose(pose.Position, pose.Rotation)
                || !ValidWallIndex(pose.WarehouseWallSnap, pose.Wall))
            {
                return false;
            }

            InteractableObject spawned = null;
            var isExisting = existingId > 0;
            // Host ids live in HostIds on the authority and in ClientObjects on a guest, so an
            // existing object must be resolved through both maps. Using only HostIds silently
            // failed on clients and spawned a duplicate for every move the host published.
            if (isExisting && !TryResolveHoldId(existingId, out spawned))
            {
                return false;
            }
            if (!isExisting)
            {
                spawned = MiSpawn.Invoke(null, new object[] { pose.DecorationType }) as InteractableObject;
            }
            if (spawned == null || (EDecoObject)FiObjectType.GetValue(spawned) != pose.DecorationType
                || GetVertical(spawned) != pose.Vertical)
            {
                return false;
            }

            var original = isExisting ? ReadPose(spawned) : null;
            try
            {
                if (!isExisting)
                {
                    MiInit.Invoke(spawned, null);
                }
                // PlaceMovedObject only transitions an object that is in the game's move
                // lifecycle. Calling it on a freshly spawned object used to leave colliders,
                // layers and ShelfManager bookkeeping in the preview state.
                MiStartMoveObject.Invoke(spawned, null);
                spawned.transform.SetPositionAndRotation(pose.Position, pose.Rotation);
                var normal = pose.Vertical ? pose.Rotation * Vector3.forward : Vector3.up;
                MiSetTargetMovePosition.Invoke(spawned,
                    new object[] { pose.Position, normal, null });
                // Run the game's own move-state Update once at the requested pose. This executes
                // the build-specific Physics.OverlapBox checks, valid-area checks, and vertical
                // wall collision logic instead of trusting a client-provided bounded position.
                MiUpdate.Invoke(spawned, null);
                if (!Convert.ToBoolean(FiValidPlacement.GetValue(spawned)))
                {
                    if (isExisting)
                    {
                        RestoreMovedObject(spawned, original);
                    }
                    else
                    {
                        MiOnDestroyed.Invoke(spawned, null);
                    }
                    return false;
                }
                MiPlaceMovedObject.Invoke(spawned, null);
                RebindWallBlocker(spawned, pose.WarehouseWallSnap, pose.Wall);
                placed = spawned;
                return true;
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogWarning("Decoration placement failed: " + exception.Message);
                if (isExisting)
                {
                    RestoreMovedObject(spawned, original);
                    return false;
                }

                try
                {
                    MiOnDestroyed.Invoke(spawned, null);
                }
                catch (Exception cleanupException)
                {
                    CoopPlugin.Log.LogWarning("Decoration placement cleanup failed: "
                        + cleanupException.Message);
                }
                return false;
            }
        }

        private static void RestoreMovedObject(InteractableObject obj, DecorationPose original)
        {
            if (obj == null || original == null)
            {
                return;
            }

            try
            {
                obj.transform.SetPositionAndRotation(original.Position, original.Rotation);
                var normal = original.Vertical ? original.Rotation * Vector3.forward : Vector3.up;
                MiSetTargetMovePosition.Invoke(obj,
                    new object[] { original.Position, normal, null });
                MiUpdate.Invoke(obj, null);
                if (Convert.ToBoolean(FiValidPlacement.GetValue(obj)))
                {
                    MiPlaceMovedObject.Invoke(obj, null);
                    RebindWallBlocker(obj, original.WarehouseWallSnap, original.Wall);
                }
                else
                {
                    CoopPlugin.Log.LogError("Decoration move rollback failed legal validation.");
                }
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Decoration move rollback failed: " + exception);
            }
        }

        internal static bool TryRemove(long id)
        {
            // Same dual-map resolution as placement: guests own their objects in ClientObjects.
            if (id <= 0 || !TryResolveHoldId(id, out var obj))
            {
                return false;
            }

            MiBoxUpObject.Invoke(obj, new object[] { false });
            HostIds.Remove(obj);
            return true;
        }

        internal static bool ValidPose(Vector3 position, Quaternion rotation)
        {
            var magnitude = rotation.x * rotation.x + rotation.y * rotation.y
                + rotation.z * rotation.z + rotation.w * rotation.w;
            return position.sqrMagnitude <= 1000000f
                && magnitude > 0.99f && magnitude < 1.01f
                && !float.IsNaN(position.x) && !float.IsNaN(position.y)
                && !float.IsNaN(position.z) && !float.IsNaN(rotation.x)
                && !float.IsNaN(rotation.y) && !float.IsNaN(rotation.z)
                && !float.IsNaN(rotation.w);
        }

        private static bool ValidWallIndex(bool warehouse, int wall)
        {
            if (wall == -1)
            {
                return !warehouse;
            }
            if (wall < 0)
            {
                return false;
            }

            var manager = SceneRef<UnlockRoomManager>.Get();
            if (manager == null)
            {
                // The scene may not have loaded yet. A wall-indexed pose is deferred rather
                // than invoking SetVerticalSnapToWarehouseWall into a fabricated singleton.
                return !IsSceneReady();
            }

            var blockers = warehouse ? manager.m_LockedWarehouseRoomBlockerList
                : manager.m_LockedRoomBlockerList;
            return blockers != null && wall < blockers.Count;
        }

        internal static int CategoryFor(PlaceDecoUIScreen screen)
        {
            if (screen == null)
            {
                screen = FindFirst(typeof(PlaceDecoUIScreen)) as PlaceDecoUIScreen;
            }
            return screen == null ? -1 : Convert.ToInt32(FiShopCategory.GetValue(screen));
        }

        internal static int CategoryFor(ShopBuyDecoUIScreen screen)
        {
            if (screen == null)
            {
                screen = FindFirst(typeof(ShopBuyDecoUIScreen)) as ShopBuyDecoUIScreen;
            }
            return screen == null ? -1 : Convert.ToInt32(FiShopCategory.GetValue(screen));
        }

        internal static DecorationPose ReadPose(InteractableObject obj)
        {
            return new DecorationPose
            {
                DecorationType = (EDecoObject)FiObjectType.GetValue(obj),
                Position = obj.transform.position,
                Rotation = obj.transform.rotation,
                Vertical = GetVertical(obj),
                WarehouseWallSnap = GetWarehouseWallSnap(obj),
                Wall = GetWallIndex(obj),
            };
        }

        internal static long ClientIdFor(InteractableObject obj)
        {
            foreach (var pair in ClientObjects)
            {
                if (ReferenceEquals(pair.Value, obj))
                {
                    return pair.Key;
                }
            }
            return 0;
        }

        internal static long HostIdFor(InteractableObject obj)
        {
            if (obj == null)
                return 0;
            return HostIds.TryGetValue(obj, out var id) ? id : 0;
        }

        /// <summary>True when this placed object is a decoration (its own identity space).</summary>
        internal static bool IsDecoration(InteractableObject obj)
            => obj != null && FiObjectType.GetValue(obj) is EDecoObject deco
                && deco != EDecoObject.None;

        /// <summary>Host-space decoration id for a placed decoration, on either role. The host
        /// assigns it and the client receives it with the placement poses, so it is stable across
        /// peers; 0 means the object has no id yet.</summary>
        internal static bool TryGetHoldId(InteractableObject obj, out long id)
        {
            id = HostIdFor(obj);
            if (id <= 0)
            {
                id = ClientIdFor(obj);
            }

            return id > 0;
        }

        /// <summary>Resolves a host-space decoration id back to the live object on either role.</summary>
        internal static bool TryResolveHoldId(long id, out InteractableObject obj)
        {
            obj = id > 0 ? GetMapped(id) : null;
            return obj != null || (id > 0 && TryFindHostObject(id, out obj));
        }

        internal static void ApplyDelta(DecorationDeltaMessage message,
            InteractableObject predictedPreview = null)
        {
            switch (message.Action)
            {
                case DecorationActions.Equip:
                    TryEquip(message.Category, message.Index, message.LotB);
                    break;
                case DecorationActions.BuyShopDecoration:
                    if (UnlockField(message.Category)?.GetValue(null) is IList unlocks
                        && message.Index >= 0 && message.Index < unlocks.Count)
                        unlocks[message.Index] = true;
                    break;
                case DecorationActions.BuyItemDecoration:
                    if (IsValidDeco(message.DecorationType))
                    {
                        var current = InventoryCount(message.DecorationType);
                        if (current != message.InventoryCount)
                            MiAddInventory.Invoke(null, new object[] { message.DecorationType,
                                message.InventoryCount - current });
                    }
                    break;
                case DecorationActions.Place:
                    if (message.Pose != null)
                    {
                        var placed = ResolvePlacement(message.Pose, predictedPreview);
                        if (placed != null)
                            ClientObjects[message.Pose.Id] = placed;
                    }
                    ApplyInventoryCount(message.DecorationType, message.InventoryCount);
                    break;
                case DecorationActions.Remove:
                    TryRemove(message.ObjectId);
                    ApplyInventoryCount(message.DecorationType, message.InventoryCount);
                    break;
            }

            RefreshUi();
        }

        /// <summary>Binds an authoritative placement to a local object. A placement the guest
        /// predicted adopts that preview instead of spawning a second piece; every other case
        /// (join baseline, host action, late join) resolves or creates the object the usual way.</summary>
        private static InteractableObject ResolvePlacement(DecorationPose pose,
            InteractableObject predictedPreview)
        {
            if (TryResolveHoldId(pose.Id, out var mapped))
            {
                DiscardPreview(predictedPreview);
                // Never re-run StartMoveObject here: if the guest is already moving this piece
                // (its own pickup, or a remote hold render) the call captures the temporary
                // Ignore Raycast layer as m_OriginalLayer, and placement then restores that, so
                // the piece becomes invisible to the interaction raycast. Apply the
                // host-authoritative pose and finalize only if it is still mid-move.
                FinalizePlacement(mapped, pose);
                CoopPlugin.Log.LogInfo("[decoration] place id=" + pose.Id + " type="
                    + pose.DecorationType + " applied-existing.");
                return mapped;
            }

            if (predictedPreview != null)
            {
                if (IsAdoptable(predictedPreview, pose))
                {
                    FinalizePlacement(predictedPreview, pose);
                    CoopPlugin.Log.LogInfo("[decoration] place id=" + pose.Id + " type="
                        + pose.DecorationType + " adopted preview.");
                    return predictedPreview;
                }

                // The host's authoritative piece differs from the preview (wrong type or
                // orientation). Retire the preview so it cannot linger, then create the real one.
                CoopPlugin.Log.LogWarning("[decoration] place id=" + pose.Id
                    + " preview incompatible; discarding and spawning.");
                DiscardPreview(predictedPreview);
            }

            var spawned = TryPlace(pose, 0, out var created);
            CoopPlugin.Log.LogInfo("[decoration] place id=" + pose.Id + " type="
                + pose.DecorationType + " spawned ok=" + spawned + ".");
            return spawned ? created : null;
        }

        private static bool IsAdoptable(InteractableObject obj, DecorationPose pose)
            => obj != null
                && (EDecoObject)FiObjectType.GetValue(obj) == pose.DecorationType
                && GetVertical(obj) == pose.Vertical;

        /// <summary>Retires a predicted placement preview that never became authoritative
        /// (a rejected or superseded intent). Settles the move lifecycle, then removes it.</summary>
        internal static void DiscardPreview(InteractableObject obj)
        {
            if (obj == null)
                return;
            CoopPlugin.Log.LogInfo("[decoration] retiring predicted preview without confirmation.");
            FinalizeMovedObject(obj);
            RemoveLocalPlacedObject(obj);
        }

        internal static void RemoveLocalPlacedObject(InteractableObject obj)
        {
            if (obj == null)
            {
                return;
            }

            MiOnDestroyed.Invoke(obj, null);
            foreach (var pair in new List<KeyValuePair<long, InteractableObject>>(ClientObjects))
            {
                if (ReferenceEquals(pair.Value, obj))
                {
                    ClientObjects.Remove(pair.Key);
                    break;
                }
            }
        }

        internal static void ApplyPredictedPose(InteractableObject obj, DecorationPose pose)
        {
            if (obj == null || pose == null)
                return;
            obj.transform.SetPositionAndRotation(pose.Position, pose.Rotation);
            RebindWallBlocker(obj, pose.WarehouseWallSnap, pose.Wall);
        }

        /// <summary>Applies an authoritative pose to a local decoration and, when it is still
        /// mid-move, runs the game's own <c>OnPlacedMovedObject</c> to settle the move-preview
        /// overlay, colliders and layer. It never calls <c>StartMoveObject</c>; doing so on an
        /// object that is already moving would capture the temporary Ignore Raycast layer as its
        /// original layer and leave the piece unpickable.</summary>
        internal static void FinalizePlacement(InteractableObject obj, DecorationPose pose)
        {
            if (obj == null || pose == null)
                return;
            var moving = obj.GetIsMovingObject();
            obj.transform.SetPositionAndRotation(pose.Position, pose.Rotation);
            if (moving)
            {
                MiOnPlacedMovedObject.Invoke(obj, null);
            }
            RebindWallBlocker(obj, pose.WarehouseWallSnap, pose.Wall);
        }

        /// <summary>Runs the game's placement finalisation on an object being boxed while it is
        /// still mid move. Vanilla <c>BoxUpObject</c> does this before removing the piece; skipping
        /// it left the player controller and the move-preview overlay stuck in placement mode.</summary>
        internal static void FinalizeMovedObject(InteractableObject obj)
        {
            if (obj == null || !obj.GetIsMovingObject())
                return;
            try
            {
                MiOnPlacedMovedObject.Invoke(obj, null);
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogWarning("Decoration move finalisation failed: " + exception.Message);
            }
        }

        internal static List<InteractableObject> GetLiveDecorations()
        {
            var list = MiGetList.Invoke(null, null) as IList;
            var result = new List<InteractableObject>(list?.Count ?? 0);
            if (list == null)
            {
                return result;
            }

            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] is InteractableObject obj && obj != null)
                {
                    result.Add(obj);
                }
            }
            return result;
        }

        private static void Reconcile(IList<DecorationPose> poses, bool removeUnlisted)
        {
            var used = new HashSet<InteractableObject>(new ReferenceComparer());
            var next = new Dictionary<long, InteractableObject>();
            var live = GetLiveDecorations();
            for (var i = 0; i < poses.Count; i++)
            {
                var pose = poses[i];
                var obj = GetMapped(pose.Id);
                if (obj == null || !live.Contains(obj) || used.Contains(obj))
                {
                    obj = null;
                    obj = FindMatching(live, used, pose);
                }
                if (obj == null && TryPlace(pose, out var spawned))
                {
                    obj = spawned;
                    live.Add(obj);
                }
                if (obj == null)
                {
                    throw new InvalidOperationException("Decoration reconciliation could not create type "
                        + pose.DecorationType + " for host object " + pose.Id + ".");
                }

                used.Add(obj);
                next[pose.Id] = obj;
                ApplyPose(obj, pose);
            }

            if (removeUnlisted)
            {
                for (var i = 0; i < live.Count; i++)
                {
                    var obj = live[i];
                    if (obj != null && !used.Contains(obj) && !obj.GetIsMovingObject())
                    {
                        RemoveLocalPlacedObject(obj);
                    }
                }
            }
            else
            {
                // A local rollback snapshot can be older than a piece the host has since confirmed,
                // so it must not drop host-owned mappings it does not mention. Authoritative state
                // (removeUnlisted) still prunes everything absent from the host's snapshot.
                foreach (var pair in ClientObjects)
                {
                    if (pair.Value != null && !next.ContainsKey(pair.Key))
                    {
                        next[pair.Key] = pair.Value;
                    }
                }
            }

            ClientObjects.Clear();
            foreach (var pair in next)
            {
                ClientObjects[pair.Key] = pair.Value;
            }
        }

        private static InteractableObject FindMatching(List<InteractableObject> live,
            HashSet<InteractableObject> used, DecorationPose pose)
        {
            InteractableObject best = null;
            var bestDistance = float.MaxValue;
            for (var i = 0; i < live.Count; i++)
            {
                var obj = live[i];
                if (obj == null || used.Contains(obj) || obj.GetIsMovingObject()
                    || (EDecoObject)FiObjectType.GetValue(obj) != pose.DecorationType)
                {
                    continue;
                }

                var distance = (obj.transform.position - pose.Position).sqrMagnitude;
                if (distance < bestDistance)
                {
                    best = obj;
                    bestDistance = distance;
                }
            }
            return best;
        }

        private static InteractableObject GetMapped(long id)
            => id > 0 && ClientObjects.TryGetValue(id, out var obj) ? obj : null;

        private static void ApplyPose(InteractableObject obj, DecorationPose pose)
        {
            obj.transform.SetPositionAndRotation(pose.Position, pose.Rotation);
            RebindWallBlocker(obj, pose.WarehouseWallSnap, pose.Wall);
        }

        private static void RebindWallBlocker(InteractableObject obj, bool warehouseWall, int wall)
        {
            var blocker = FiPlacedBlocker.GetValue(obj) as LockedRoomBlocker;
            if (blocker != null)
            {
                blocker.RemoveFromVerticalDecoObjectList(obj);
                FiPlacedBlocker.SetValue(obj, null);
            }
            MiSetSnap.Invoke(obj, new object[] { warehouseWall, wall });
        }

        private static bool TryFindHostObject(long id, out InteractableObject result)
        {
            foreach (var pair in HostIds)
            {
                if (pair.Value == id)
                {
                    result = pair.Key;
                    return result != null;
                }
            }
            result = null;
            return false;
        }

        private static void ApplyMaterials(DecorationStateMessage message)
        {
            InvokeMaterial("ChangeWallMaterial", message.Wall, false);
            InvokeMaterial("ChangeWallMaterial", message.WallB, true);
            InvokeMaterial("ChangeFloorMaterial", message.Floor, false);
            InvokeMaterial("ChangeFloorMaterial", message.FloorB, true);
            InvokeMaterial("ChangeCeilingMaterial", message.Ceiling, false);
            InvokeMaterial("ChangeCeilingMaterial", message.CeilingB, true);
        }

        private static void InvokeMaterial(string name, int index, bool lotB)
        {
            var method = typeof(ShopCustomizationManager).GetMethod(name,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(int), typeof(bool) }, null);
            if (method == null)
            {
                throw new MissingMethodException(typeof(ShopCustomizationManager).FullName, name);
            }
            method.Invoke(null, new object[] { index, lotB });
        }

        private static FieldInfo UnlockField(int category)
            => category == 0 ? FiWallUnlocks : category == 1 ? FiFloorUnlocks
                : category == 2 ? FiCeilingUnlocks : null;

        private static int EquippedIndex(int category, bool lotB)
        {
            if (category == 0)
            {
                return GetInt(lotB ? FiWallB : FiWall);
            }
            if (category == 1)
            {
                return GetInt(lotB ? FiFloorB : FiFloor);
            }
            if (category == 2)
            {
                return GetInt(lotB ? FiCeilingB : FiCeiling);
            }
            return -1;
        }

        private static IList CategoryData(int category)
        {
            var inventory = SceneRef<InventoryBase>.Get();
            var objectData = inventory == null ? null : FiObjectData.GetValue(inventory);
            var field = category == 0 ? FiWallData : category == 1 ? FiFloorData
                : category == 2 ? FiCeilingData : null;
            return field?.GetValue(objectData) as IList;
        }

        private static IList ItemData()
        {
            var inventory = SceneRef<InventoryBase>.Get();
            var objectData = inventory == null ? null : FiObjectData.GetValue(inventory);
            return FiItemData.GetValue(objectData) as IList;
        }

        private static List<bool> CopyBools(FieldInfo field)
        {
            var values = field.GetValue(null) as IEnumerable;
            var result = new List<bool>();
            if (values != null)
            {
                foreach (var value in values)
                {
                    result.Add(Convert.ToBoolean(value));
                }
            }
            return result;
        }

        private static List<int> CopyInts(FieldInfo field)
        {
            var values = field.GetValue(null) as IEnumerable;
            var result = new List<int>();
            if (values != null)
            {
                foreach (var value in values)
                {
                    result.Add(Convert.ToInt32(value));
                }
            }
            return result;
        }

        private static void ApplyList(FieldInfo field, IList incoming)
        {
            var target = field.GetValue(null) as IList;
            if (target == null || incoming == null)
            {
                throw new InvalidOperationException("Decoration state list is unavailable.");
            }

            target.Clear();
            for (var i = 0; i < incoming.Count; i++)
            {
                target.Add(incoming[i]);
            }
        }

        private static int GetInt(FieldInfo field) => Convert.ToInt32(field.GetValue(null));

        private static void SetInt(FieldInfo field, int value) => field.SetValue(null, value);

        /// <summary>True when a decoration type is a real, placeable member. None (0) and any
        /// undefined value are rejected. The host and the game both speak <see cref="EDecoObject"/>,
        /// so callers pass the enum directly rather than a raw int.</summary>
        private static bool IsValidDeco(EDecoObject type)
            => type != EDecoObject.None && Enum.IsDefined(typeof(EDecoObject), type);

        private static bool GetVertical(InteractableObject obj)
            => Convert.ToBoolean(FiVertical.GetValue(obj));

        private static bool GetWarehouseWallSnap(InteractableObject obj)
            => Convert.ToBoolean(MiGetSnap.Invoke(obj, null));

        private static int GetWallIndex(InteractableObject obj)
            => Convert.ToInt32(MiGetWall.Invoke(obj, null));

        internal static bool IsSceneReady()
            => SceneRef<ShelfManager>.Get() != null && SceneRef<InventoryBase>.Get() != null;

        private static object FindFirst(Type type)
        {
            var method = FindObjectsWithInactive ?? FindObjects;
            if (method == null)
            {
                throw new MissingMethodException("Unity Object.FindObjectsOfType is unavailable.");
            }

            var args = method == FindObjectsWithInactive
                ? new object[] { type, true }
                : new object[] { type };
            var found = method.Invoke(null, args) as Array;
            return found == null || found.Length == 0 ? null : found.GetValue(0);
        }

        private static void RefreshUi()
        {
            InvokePage(typeof(PlaceDecoUIScreen));
            InvokePage(typeof(ShopBuyDecoUIScreen));
        }

        private static void InvokePage(Type type)
        {
            var screen = FindFirst(type);
            if (screen == null)
            {
                return;
            }

            var method = type.GetMethod("EvaluatePanelUIPage",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            method?.Invoke(screen, new object[] { 0 });
        }
    }

    internal sealed class DecorationSnapshot
    {
        internal int Wall;
        internal int WallB;
        internal int Floor;
        internal int FloorB;
        internal int Ceiling;
        internal int CeilingB;
        internal List<bool> WallUnlocks = new();
        internal List<bool> FloorUnlocks = new();
        internal List<bool> CeilingUnlocks = new();
        internal List<int> Inventory = new();
        internal List<DecorationPose> Placed = new();
    }
}
