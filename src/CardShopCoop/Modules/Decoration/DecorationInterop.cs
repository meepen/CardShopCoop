using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Modules.World;
using CardShopCoop.Runtime;
using CardShopCoop.Util;
using UnityEngine;

namespace CardShopCoop.Modules.Decoration
{
    /// <summary>
    /// The two supported game builds keep the same decoration concepts, so the module's required
    /// game surfaces are resolved once here and a missing surface fails module initialization
    /// loudly.
    /// </summary>
    internal static class DecorationInterop
    {
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

        // The buy and place screens are siblings that each declare their own private
        // m_CategoryIndex, so one FieldInfo cannot be read from the other screen's instance
        // (FieldInfo throws ArgumentException when the target is not the declaring type).
        private static readonly FieldInfo FiBuyCategory = ReflectionSurface.RequiredField(
            typeof(ShopBuyDecoUIScreen), "m_CategoryIndex");
        private static readonly FieldInfo FiPlaceCategory = ReflectionSurface.RequiredField(
            typeof(PlaceDecoUIScreen), "m_CategoryIndex");
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

        private static int _generation;

        /// <summary>Bumped whenever the scene is reset, so an undo captured in the outgoing world
        /// can be recognised and skipped in the new one.</summary>
        internal static int Generation => _generation;

        internal static void Reset()
        {
            // Decoration identity lives in GameObjectIdentityCache. Only the local settings
            // generation belongs to this module now.
            _generation++;
        }

        /// <summary>Decoration settings only. Placed objects belong to the identity lifecycle, so a
        /// rollback snapshot can no longer be used to create or remove a piece.</summary>
        internal static DecorationSnapshot Snapshot()
        {
            if (!IsSceneReady())
            {
                throw new InvalidOperationException("Decoration scene is not ready.");
            }

            return new DecorationSnapshot
            {
                Generation = _generation,
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
        }

        internal static DecorationStateMessage ToStateMessage(DecorationSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return null;
            }

            return new DecorationStateMessage
            {
                Wall = snapshot.Wall,
                WallB = snapshot.WallB,
                Floor = snapshot.Floor,
                FloorB = snapshot.FloorB,
                Ceiling = snapshot.Ceiling,
                CeilingB = snapshot.CeilingB,
                WallUnlocks = snapshot.WallUnlocks,
                FloorUnlocks = snapshot.FloorUnlocks,
                CeilingUnlocks = snapshot.CeilingUnlocks,
                Inventory = snapshot.Inventory,
            };
        }

        internal static void ApplySnapshot(DecorationStateMessage message)
        {
            if (message == null)
            {
                return;
            }

            ApplySettings(message.Wall, message.WallB, message.Floor, message.FloorB,
                message.Ceiling, message.CeilingB, message.WallUnlocks, message.FloorUnlocks,
                message.CeilingUnlocks, message.Inventory);
        }

        internal static void Restore(DecorationSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            ApplySettings(snapshot.Wall, snapshot.WallB, snapshot.Floor, snapshot.FloorB,
                snapshot.Ceiling, snapshot.CeilingB, snapshot.WallUnlocks, snapshot.FloorUnlocks,
                snapshot.CeilingUnlocks, snapshot.Inventory);
        }

        private static void ApplySettings(int wall, int wallB, int floor, int floorB, int ceiling,
            int ceilingB, IList wallUnlocks, IList floorUnlocks,
            IList ceilingUnlocks, IList inventory)
        {
            ApplyList(FiWallUnlocks, wallUnlocks);
            ApplyList(FiFloorUnlocks, floorUnlocks);
            ApplyList(FiCeilingUnlocks, ceilingUnlocks);
            ApplyList(FiInventory, inventory);
            SetInt(FiWall, wall);
            SetInt(FiWallB, wallB);
            SetInt(FiFloor, floor);
            SetInt(FiFloorB, floorB);
            SetInt(FiCeiling, ceiling);
            SetInt(FiCeilingB, ceilingB);
            ApplyMaterials(wall, wallB, floor, floorB, ceiling, ceilingB);
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
            {
                return 0;
            }


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

            var previous = Convert.ToInt32(FiBuyCategory.GetValue(screen));
            try
            {
                FiBuyCategory.SetValue(screen, category);
                MiBuyShop.Invoke(screen, new object[] { index, price });
            }
            finally
            {
                FiBuyCategory.SetValue(screen, previous);
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

            var previous = Convert.ToInt32(FiPlaceCategory.GetValue(screen));
            try
            {
                FiPlaceCategory.SetValue(screen, category);
                MiSwitchShop.Invoke(screen, new object[] { index, lotB });
            }
            finally
            {
                FiPlaceCategory.SetValue(screen, previous);
            }
            return EquippedIndex(category, lotB) == index;
        }

        internal static bool TryPlace(DecorationPose pose, out InteractableObject placed,
            bool authoritative = false)
            => TryPlace(pose, null, out placed, authoritative);

        /// <summary>Object-level placement primitive. Identity resolves the stable id and hands the
        /// live object in; this method owns only the game's move/placement sequence.</summary>
        internal static bool TryPlace(DecorationPose pose, InteractableObject existing,
            out InteractableObject placed, bool authoritative = false)
        {
            placed = null;
            if (pose == null || !IsValidDeco(pose.DecorationType)
                || !ValidPose(pose.Position, pose.Rotation)
                || !ValidWallIndex(pose.WarehouseWallSnap, pose.Wall))
            {
                return false;
            }

            var spawned = existing;
            var isExisting = existing != null;
            if (!isExisting)
            {
                try
                {
                    spawned = MiSpawn.Invoke(null, new object[] { pose.DecorationType })
                        as InteractableObject;
                }
                catch (Exception exception)
                {
                    // A type with no spawnable prefab is a build/mod-content mismatch. Keep the
                    // authoritative pose eligible for a retry instead of failing the module.
                    CoopPlugin.Log.LogWarning("Decoration spawn failed for " + pose.DecorationType
                        + ": " + exception.Message);
                    return false;
                }
            }
            if (spawned == null || (EDecoObject)FiObjectType.GetValue(spawned) != pose.DecorationType
                || GetVertical(spawned) != pose.Vertical)
            {
                return false;
            }

            // A piece a player is already moving belongs to that hold. Running StartMoveObject a
            // second time would capture the temporary Ignore Raycast layer as m_OriginalLayer and
            // leave the piece unpickable, so an intent-driven apply refuses it; an authoritative
            // apply settles it at the host's pose below without re-entering the move.
            var wasMoving = spawned.GetIsMovingObject();
            if (wasMoving && !authoritative)
            {
                return false;
            }

            var original = isExisting ? ReadPose(spawned) : null;
            // A module-driven placement must not look like a local player hold: the hold layer's
            // release is skipped while a prediction reconcile runs, so an apply that started a
            // hold could leave it stuck and block every later hold this peer takes.
            PlacementHoldInteraction.BeginSuppressLocalStarts();
            try
            {
                if (!isExisting)
                {
                    MiInit.Invoke(spawned, null);
                }
                // PlaceMovedObject only transitions an object that is in the game's move
                // lifecycle. Calling it on a freshly spawned object used to leave colliders,
                // layers and ShelfManager bookkeeping in the preview state. An already-moving
                // piece keeps the layer its original StartMoveObject captured.
                if (!wasMoving)
                {
                    MiStartMoveObject.Invoke(spawned, null);
                }
                spawned.transform.SetPositionAndRotation(pose.Position, pose.Rotation);
                var normal = pose.Vertical ? pose.Rotation * Vector3.forward : Vector3.up;
                MiSetTargetMovePosition.Invoke(spawned,
                    new object[] { pose.Position, normal, null });
                if (authoritative)
                {
                    // The host already validated this pose. This peer's local furniture/NPC state
                    // can lag (the World module defers poses), so running the game's overlap test
                    // here would silently drop a piece every other peer has. Trust the host and
                    // finalise at exactly the authoritative pose.
                    FiValidPlacement.SetValue(spawned, true);
                }
                else
                {
                    // Host-side intent validation: run the game's own move-state Update once at
                    // the requested pose so the build-specific Physics.OverlapBox checks, valid
                    // area checks and vertical wall logic decide.
                    MiUpdate.Invoke(spawned, null);
                    if (!Convert.ToBoolean(FiValidPlacement.GetValue(spawned)))
                    {
                        if (isExisting)
                        {
                            RestoreMovedObject(spawned, original);
                        }
                        else
                        {
                            SettleAndDestroy(spawned);
                        }
                        return false;
                    }
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
                    FinalizeMovedObject(spawned);
                    return false;
                }

                SettleAndDestroy(spawned);
                return false;
            }
            finally
            {
                PlacementHoldInteraction.EndSuppressLocalStarts();
            }
        }

        /// <summary>Cleans up a freshly spawned placement that cannot be used. Settle the synthetic
        /// move lifecycle first so the local player controller and preview overlay leave moving
        /// mode, then destroy the object.</summary>
        private static void SettleAndDestroy(InteractableObject obj)
        {
            if (obj == null)
            {
                return;
            }


            FinalizeMovedObject(obj);
            try
            {
                MiOnDestroyed.Invoke(obj, null);
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogWarning("Decoration placement cleanup failed: "
                    + exception.Message);
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
                }
                else
                {
                    // The original pose should be legal; if local geometry moved under it, still
                    // leave the move lifecycle so the local player controller is not stranded.
                    CoopPlugin.Log.LogWarning(
                        "Decoration move rollback failed legal validation; settling at the original pose.");
                    FinalizeMovedObject(obj);
                }
                RebindWallBlocker(obj, original.WarehouseWallSnap, original.Wall);
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Decoration move rollback failed: " + exception);
            }
        }

        /// <summary>Object-level removal primitive. Identity resolves the stable id; this method
        /// only runs the game's own box-up, which returns the item to the inventory and destroys
        /// the object through OnDestroyed.</summary>
        internal static bool TryRemove(InteractableObject obj)
        {
            if (obj == null || !IsDecoration(obj))
            {
                return false;
            }

            MiBoxUpObject.Invoke(obj, new object[] { false });
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
                // The scene may not have loaded yet. Refuse the pose; the identity client queues
                // the descriptor and retries it from a lifecycle hook.
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
            return screen == null ? -1 : Convert.ToInt32(FiPlaceCategory.GetValue(screen));
        }

        internal static int CategoryFor(ShopBuyDecoUIScreen screen)
        {
            if (screen == null)
            {
                screen = FindFirst(typeof(ShopBuyDecoUIScreen)) as ShopBuyDecoUIScreen;
            }
            return screen == null ? -1 : Convert.ToInt32(FiBuyCategory.GetValue(screen));
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

        /// <summary>True when this placed object is a decoration (its own identity space).</summary>
        internal static bool IsDecoration(InteractableObject obj)
            => obj != null && FiObjectType.GetValue(obj) is EDecoObject deco
                && deco != EDecoObject.None;

        internal static void ApplyDelta(DecorationDeltaMessage message)
        {
            switch (message.Action)
            {
                case DecorationActions.Equip:
                    TryEquip(message.Category, message.Index, message.LotB);
                    break;
                case DecorationActions.BuyShopDecoration:
                    if (UnlockField(message.Category)?.GetValue(null) is IList unlocks
                        && message.Index >= 0 && message.Index < unlocks.Count)
                    {
                        unlocks[message.Index] = true;
                    }


                    break;
                case DecorationActions.BuyItemDecoration:
                    if (IsValidDeco(message.DecorationType))
                    {
                        var current = InventoryCount(message.DecorationType);
                        if (current != message.InventoryCount)
                        {
                            MiAddInventory.Invoke(null, new object[] { message.DecorationType,
                                message.InventoryCount - current });
                        }

                    }
                    break;
                case DecorationActions.Place:
                case DecorationActions.Remove:
                    // Object creation and removal belong to the identity lifecycle. The state
                    // delta only carries the resulting inventory count.
                    ApplyInventoryCount(message.DecorationType, message.InventoryCount);
                    break;
            }

            RefreshUi();
        }

        /// <summary>Object-level destroy primitive. The game's OnDestroyed removes the piece from
        /// every scene registry and destroys the GameObject; identity owns the id binding.</summary>
        internal static void RemoveLocalPlacedObject(InteractableObject obj)
        {
            if (obj == null)
            {
                return;
            }

            MiOnDestroyed.Invoke(obj, null);
        }

        internal static void ApplyPredictedPose(InteractableObject obj, DecorationPose pose)
        {
            if (obj == null || pose == null)
            {
                return;
            }


            obj.transform.SetPositionAndRotation(pose.Position, pose.Rotation);
            RebindWallBlocker(obj, pose.WarehouseWallSnap, pose.Wall);
        }

        /// <summary>Runs the game's placement finalisation on an object being boxed while it is
        /// still mid move. Vanilla <c>BoxUpObject</c> does this before removing the piece; skipping
        /// it left the player controller and the move-preview overlay stuck in placement mode.</summary>
        internal static void FinalizeMovedObject(InteractableObject obj)
        {
            if (obj == null || !obj.GetIsMovingObject())
            {
                return;
            }


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

        internal static void ApplyPose(InteractableObject obj, DecorationPose pose)
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

        private static void ApplyMaterials(int wall, int wallB, int floor, int floorB, int ceiling,
            int ceilingB)
        {
            InvokeMaterial("ChangeWallMaterial", wall, false);
            InvokeMaterial("ChangeWallMaterial", wallB, true);
            InvokeMaterial("ChangeFloorMaterial", floor, false);
            InvokeMaterial("ChangeFloorMaterial", floorB, true);
            InvokeMaterial("ChangeCeilingMaterial", ceiling, false);
            InvokeMaterial("ChangeCeilingMaterial", ceilingB, true);
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
            => UnityEngine.Object.FindFirstObjectByType(type, UnityEngine.FindObjectsInactive.Include);

        internal static void RefreshUi()
        {
            // EvaluateDecoPageUI reads the live inventory catalogue, unlike the panel-only
            // re-render it replaces. A delta can arrive in a load window (the delta path is not
            // readiness-gated), so leave the refresh to the state apply that follows the load.
            if (!IsSceneReady())
            {
                return;
            }


            RefreshPage(typeof(PlaceDecoUIScreen));
            RefreshPage(typeof(ShopBuyDecoUIScreen));
        }

        /// <summary>Re-evaluates a screen through the same entry point its own tab switch and open
        /// use, which refreshes the cached equipped indices and data list from the current player
        /// data before rebuilding the panels. Rebuilding the panels alone rendered from the stale
        /// cache, so a remote equip left the previously equipped style shown as equipped.</summary>
        private static void RefreshPage(Type type)
        {
            var screen = FindFirst(type);
            if (screen == null)
            {
                return;
            }

            var category = type == typeof(PlaceDecoUIScreen)
                ? CategoryFor((PlaceDecoUIScreen)screen)
                : CategoryFor((ShopBuyDecoUIScreen)screen);
            var method = type.GetMethod("EvaluateDecoPageUI",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method != null)
            {
                method.Invoke(screen, new object[] { category });
                return;
            }

            type.GetMethod("EvaluatePanelUIPage",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.Invoke(screen, new object[] { 0 });
        }
    }

    internal sealed class DecorationSnapshot
    {
        internal int Generation;
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
    }
}
