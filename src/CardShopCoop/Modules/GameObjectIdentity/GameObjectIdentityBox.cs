using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.World;
using CardShopCoop.Util;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Modules.GameObjectIdentity
{
    /// <summary>
    /// Packaging-box lifecycle. Every creation/destruction/binding/descriptor/baseline path for
    /// a packaging box lives here; the World box engine owns only contents, open state, holds,
    /// warehouse representation and delivery bookkeeping.
    /// </summary>
    public sealed partial class GameObjectIdentityHostBehaviour
    {
        [OnSessionStopped]
        private void ResetBoxLifecycle() => BoxIdentityLifecycle.Reset();
    }

    public sealed partial class GameObjectIdentityClientBehaviour
    {
        [OnSessionStarted]
        private void InstallBoxLifecycle()
        {
            BoxLifecycleHooks.InstallClient(Harmony);
        }

        [OnSessionStopped]
        private void ShutdownBoxLifecycle()
        {
            BoxIdentityLifecycle.Reset();
        }

        /// <summary>Sends one identity-owned intent on the host lane. The caller supplies the
        /// world payload; the framework assigns the action id and records the prediction.</summary>
        internal static void SendToHost(WorldMessage message)
        {
            var active = Active
                ?? throw new InvalidOperationException(
                    "The identity box client is not active; cannot send a box intent.");
            var context = active.Context
                ?? throw new InvalidOperationException(
                    "The identity box client has no live co-op session; cannot send a box intent.");
            context.Send(message);
        }
    }

    internal static partial class GameObjectIdentityHostHooks
    {
        private static partial void InstallBox(Harmony harmony)
        {
            harmony.CreateClassProcessor(typeof(BoxLifecycleHooks.ItemBoxCreatedPatch)).Patch();
            harmony.CreateClassProcessor(typeof(BoxLifecycleHooks.CardBoxCreatedPatch)).Patch();
            harmony.CreateClassProcessor(typeof(BoxLifecycleHooks.ItemBoxDestroyedPatch)).Patch();
            harmony.CreateClassProcessor(typeof(BoxLifecycleHooks.CardBoxDestroyedPatch)).Patch();
            harmony.CreateClassProcessor(typeof(BoxLifecycleHooks.FurnitureBoxDestroyedPatch)).Patch();

            // Warehouse takes use this factory. Resolve it at runtime so a future build that
            // moves or renames it degrades to the normal create hook instead of failing to load.
            var recordTakeFactory = AccessTools.Method(typeof(RestockManager),
                "SpawnPackageBoxItemAtTransform");
            if (recordTakeFactory != null)
            {
                harmony.Patch(recordTakeFactory, postfix: new HarmonyMethod(
                    typeof(BoxLifecycleHooks), nameof(BoxLifecycleHooks.RecordTakeBoxCreated)));
            }

            // A host that starts a session inside an already-loaded shop has no spawn events left
            // for the boxes that are already in the world.
            BoxIdentityLifecycle.BindLiveBoxes();
        }
    }

    /// <summary>The Harmony patch bodies for the box lifecycle. Host installs creation and
    /// destruction hooks; client installs its local creation binding and destroy forwarding.</summary>
    internal static class BoxLifecycleHooks
    {
        internal static void InstallClient(Harmony harmony)
        {
            harmony.CreateClassProcessor(typeof(ClientItemBoxCreatedPatch)).Patch();
            harmony.CreateClassProcessor(typeof(ClientCardBoxCreatedPatch)).Patch();
            harmony.CreateClassProcessor(typeof(ClientItemBoxDestroyedPatch)).Patch();
            harmony.CreateClassProcessor(typeof(ClientCardBoxDestroyedPatch)).Patch();
            harmony.CreateClassProcessor(typeof(ClientFurnitureBoxDestroyedPatch)).Patch();
        }

        internal static void RecordTakeBoxCreated(InteractablePackagingBox_Item __result)
            => BoxIdentityLifecycle.HostBoxCreated(__result);

        /// <summary>
        /// Diagnostics at the game's factory boundary. A thrown <c>SpawnPackageBoxItem</c> used to be
        /// a silent "no box ever appears": the delivery queue keeps its entry and the game retries it
        /// every frame, so the player only sees an order that never lands. Log the manager the game
        /// actually resolved (its own <c>CSingleton</c> cache, which may be an empty fabricated
        /// copy) next to the scene manager, so the two can be compared at a glance. The exception is
        /// returned unchanged so the game's own failure behaviour is untouched.
        /// </summary>
        internal static Exception LogFactoryFailure(Exception exception)
        {
            if (exception == null)
            {
                return null;
            }

            var cached = SingletonLatchGuard.TryGetCachedInstance(typeof(RestockManager),
                out var resolved) ? resolved as RestockManager : null;
            CoopPlugin.Log.LogError("[box-id] the game's package-box factory threw; no box was created"
                + " (gameCached=" + DescribeManager(cached)
                + ", scene=" + DescribeManager(RestockInterop.Manager)
                + "): " + exception);
            return exception;
        }

        private static string DescribeManager(RestockManager manager)
            => manager == null
                ? "<none>"
                : manager.name + "#" + manager.GetInstanceID()
                    + " prefabs=" + (manager.m_PackageBoxPrefab != null
                        && manager.m_PackageBoxSmallPrefab != null)
                    + " spawnLocations=" + (manager.m_PackageBoxSpawnLocationList?.Count ?? -1);

        [HarmonyPatch(typeof(RestockManager), "SpawnPackageBoxItem")]
        internal static class ItemBoxCreatedPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Item __result)
                => BoxIdentityLifecycle.HostBoxCreated(__result);

            [HarmonyFinalizer]
            private static Exception Finalizer(Exception __exception)
                => LogFactoryFailure(__exception);
        }

        [HarmonyPatch(typeof(RestockManager), "SpawnPackageBoxCard")]
        internal static class CardBoxCreatedPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Card __result)
                => BoxIdentityLifecycle.HostBoxCreated(__result);

            [HarmonyFinalizer]
            private static Exception Finalizer(Exception __exception)
                => LogFactoryFailure(__exception);
        }

        [HarmonyPatch(typeof(InteractablePackagingBox_Item), "OnDestroyed")]
        internal static class ItemBoxDestroyedPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractablePackagingBox_Item __instance,
                out BoxIdentityLifecycle.HostDestroyCapture __state)
                => __state = BoxIdentityLifecycle.CaptureHostDestroyed(__instance);

            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Item __instance,
                BoxIdentityLifecycle.HostDestroyCapture __state)
                => BoxIdentityLifecycle.HostBoxDestroyed(__instance, __state);
        }

        [HarmonyPatch(typeof(InteractablePackagingBox_Card), "OnDestroyed")]
        internal static class CardBoxDestroyedPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractablePackagingBox_Card __instance,
                out BoxIdentityLifecycle.HostDestroyCapture __state)
                => __state = BoxIdentityLifecycle.CaptureHostDestroyed(__instance);

            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Card __instance,
                BoxIdentityLifecycle.HostDestroyCapture __state)
                => BoxIdentityLifecycle.HostBoxDestroyed(__instance, __state);
        }

        [HarmonyPatch(typeof(RestockManager), "SpawnPackageBoxItem")]
        internal static class ClientItemBoxCreatedPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Item __result)
                => BoxIdentityLifecycle.ClientBoxCreated(__result);

            [HarmonyFinalizer]
            private static Exception Finalizer(Exception __exception)
                => LogFactoryFailure(__exception);
        }

        [HarmonyPatch(typeof(RestockManager), "SpawnPackageBoxCard")]
        internal static class ClientCardBoxCreatedPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Card __result)
                => BoxIdentityLifecycle.ClientBoxCreated(__result);

            [HarmonyFinalizer]
            private static Exception Finalizer(Exception __exception)
                => LogFactoryFailure(__exception);
        }

        [HarmonyPatch(typeof(InteractablePackagingBox_Shelf), "OnDestroyed")]
        internal static class FurnitureBoxDestroyedPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox_Shelf __instance)
                => BoxIdentityLifecycle.HostBoxDestroyed(__instance);
        }

        [HarmonyPatch(typeof(InteractablePackagingBox_Item), "OnDestroyed")]
        internal static class ClientItemBoxDestroyedPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractablePackagingBox_Item __instance,
                out BoxIdentityLifecycle.ClientDestroyCapture __state)
                => __state = BoxIdentityLifecycle.CaptureClientDestroyed(__instance);

            [HarmonyPostfix]
            private static void Postfix(BoxIdentityLifecycle.ClientDestroyCapture __state)
                => BoxIdentityLifecycle.ForwardClientDestroyed(__state);
        }

        [HarmonyPatch(typeof(InteractablePackagingBox_Card), "OnDestroyed")]
        internal static class ClientCardBoxDestroyedPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractablePackagingBox_Card __instance,
                out BoxIdentityLifecycle.ClientDestroyCapture __state)
                => __state = BoxIdentityLifecycle.CaptureClientDestroyed(__instance);

            [HarmonyPostfix]
            private static void Postfix(BoxIdentityLifecycle.ClientDestroyCapture __state)
                => BoxIdentityLifecycle.ForwardClientDestroyed(__state);
        }

        [HarmonyPatch(typeof(InteractablePackagingBox_Shelf), "OnDestroyed")]
        internal static class ClientFurnitureBoxDestroyedPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractablePackagingBox_Shelf __instance,
                out BoxIdentityLifecycle.ClientDestroyCapture __state)
                => __state = BoxIdentityLifecycle.CaptureClientDestroyed(__instance);

            [HarmonyPostfix]
            private static void Postfix(BoxIdentityLifecycle.ClientDestroyCapture __state)
                => BoxIdentityLifecycle.ForwardClientDestroyed(__state);
        }
    }

    /// <summary>The single owner of box identity, descriptors, materialization and baseline
    /// slotting. World calls in for content/pose description and application only.</summary>
    internal static class BoxIdentityLifecycle
    {
        /// <summary>Pre-destroy capture built in the client hook prefix while identity and the
        /// descriptor are still readable. A null capture means "do not forward".</summary>
        internal sealed class ClientDestroyCapture
        {
            internal Guid Id;
            internal InteractablePackagingBox Box;
        }

        /// <summary>Host teardown capture: OnDestroyed may clear Unity's liveness before the
        /// postfix runs, so the bound id must be read in the prefix.</summary>
        internal sealed class HostDestroyCapture
        {
            internal Guid Id;
            internal bool HasId;
            internal string Name;
            internal bool WasStored;
        }

        internal static HostDestroyCapture CaptureHostDestroyed(InteractablePackagingBox box)
        {
            var capture = new HostDestroyCapture();
            if (box == null)
            {
                return capture;
            }

            // Read everything that requires a live Unity object while the prefix still has one.
            capture.Name = box.name;
            capture.WasStored = BoxNetworkInteraction.IsStoredBox(box);
            if (GameObjectIdentityCache.TryGetBoundId(box, out var id)
                && id.Kind == GameObjectKind.PackagingBox && id.GuidValue != Guid.Empty)
            {
                capture.Id = id.GuidValue;
                capture.HasId = true;
            }

            return capture;
        }

        private static readonly Dictionary<Guid, PackagingBoxEntityDescriptor> PendingClientDescriptors =
            new();

        private static readonly Dictionary<byte, List<InteractablePackagingBox>> SceneSlots = new();
        private static readonly List<InteractablePackagingBox> BaselineSaveBoxes = new();
        private static bool _sceneSlotsCaptured;
        private static Transform _cardSpawnAnchor;
        private static int _materializing;
        private static int _applyingRemote;

        private static bool IsHost => CoopCore.Role == CoopRole.Host;

        internal static bool IsMaterializing => _materializing > 0;

        internal static void Reset()
        {
            PendingClientDescriptors.Clear();
            SceneSlots.Clear();
            BaselineSaveBoxes.Clear();
            WarehouseBoxLocations.Reset();
            _sceneSlotsCaptured = false;
            _materializing = 0;
            _applyingRemote = 0;
            if (_cardSpawnAnchor != null)
            {
                UnityEngine.Object.Destroy(_cardSpawnAnchor.gameObject);
                _cardSpawnAnchor = null;
            }
        }

        internal static void BindHostBox(Guid id, InteractablePackagingBox box)
        {
            if (id == Guid.Empty || box == null)
            {
                throw new InvalidOperationException("Cannot bind an invalid host box identity.");
            }

            GameObjectIdentityApi.BindHost(box, GameObjectId.Box(id));
        }

        internal static void ForgetBox(InteractablePackagingBox box)
        {
            if (box != null)
            {
                GameObjectIdentityApi.Unbind(box);
            }
        }

        /// <summary>Drops the live box object while keeping its Guid and extended information.
        /// Used when the box becomes a stored warehouse record (the record keeps the same id), so
        /// taking it again replays the information onto the new live box.</summary>
        internal static void DetachBox(InteractablePackagingBox box)
        {
            if (box != null)
            {
                GameObjectIdentityApi.Detach(box);
            }
        }

        internal static void BindClientBox(Guid id, InteractablePackagingBox box)
        {
            if (id == Guid.Empty || box == null)
            {
                throw new InvalidOperationException("Cannot bind an invalid client box identity.");
            }

            GameObjectIdentityApi.BindClient(box, GameObjectId.Box(id));
        }

        /// <summary>Host: bind every box already present in the loaded world.</summary>
        internal static void BindLiveBoxes()
        {
            BindLiveBoxes(RestockInterop.ItemBoxes);
            BindLiveBoxes(RestockInterop.CardBoxes);
        }

        private static void BindLiveBoxes<T>(IList<T> boxes) where T : InteractablePackagingBox
        {
            if (boxes == null)
            {
                return;
            }

            for (var i = 0; i < boxes.Count; i++)
            {
                if (boxes[i] != null)
                {
                    WorldHostBehaviour.EnsureHostBoxId(boxes[i]);
                }
            }
        }

        /// <summary>Host: one box was created by the game. Bind it to its creator-assigned id (or
        /// mint one) and publish its lifecycle descriptor.</summary>
        internal static void HostBoxCreated(InteractablePackagingBox box)
        {
            if (box == null || _materializing > 0)
            {
                return;
            }

            if (GameObjectIdentityCache.TryGetBoundId(box, out _))
            {
                // The second factory hook of a build-specific creation path must not re-announce
                // an entity that already crossed the wire.
                return;
            }

            // A furniture package is a view of its placed furniture, never a separate box
            // entity. Its placement descriptor owns creation and state.
            if (box is InteractablePackagingBox_Shelf)
            {
                return;
            }

            AnnounceHostBox(box);
        }

        /// <summary>Host: the game is tearing a box down. A stored box is a representation change
        /// (the warehouse channel owns its record), not a network destruction.</summary>
        internal static void HostBoxDestroyed(InteractablePackagingBox box,
            HostDestroyCapture capture = null)
        {
            if (box == null || _materializing > 0)
            {
                return;
            }

            // The placed furniture owns the lifecycle; its placement removal retires the box.
            if (box is InteractablePackagingBox_Shelf)
            {
                WorldHostBehaviour.ReleaseHoldForDestroyedBox(box);
                GameObjectIdentityApi.Unbind(box);
                return;
            }

            capture ??= CaptureHostDestroyed(box);
            WorldHostBehaviour.ReleaseHoldForDestroyedBox(box);
            if (capture == null || !capture.HasId)
            {
                return;
            }

            var id = capture.Id;
            var wasStored = capture.WasStored;
            if (wasStored)
            {
                // The warehouse record keeps this Guid and owns the representation. Preserve the
                // extended information so the next take rebinds it to the new live box.
                DetachBox(box);
            }
            else
            {
                GameObjectIdentityApi.Unbind(box);
            }

            CoopPlugin.Log.LogInfo("[box-id] host box destroyed id=" + id + " name="
                + (capture.Name ?? "<destroyed>") + " stored=" + wasStored + ".");
            if (wasStored)
            {
                return;
            }

            GameObjectIdentityApi.HostPublishRemoved(new EntityRemovedMessage
            {
                Id = GameObjectId.Box(id),
                Mode = EntityRemovalMode.Destroy,
                BoxNetworkId = id,
            });
            CoopPlugin.Log.LogInfo("[box-id] destroyed id=" + id + ".");
        }

        /// <summary>Client: the game created a box locally. A transferred save load is not a
        /// player creation, so it stays unbound until the host baseline names its slot.</summary>
        internal static void ClientBoxCreated(InteractablePackagingBox box)
        {
            if (box == null || _materializing > 0 || IsHost)
            {
                return;
            }

            if (WorldClientBehaviour.IsPreloadHeld)
            {
                return;
            }

            // A furniture package borrows the furniture's session Guid; it never mints one.
            if (box is InteractablePackagingBox_Shelf)
            {
                return;
            }

            WorldClientBehaviour.AssignCreatedBoxId(box);
        }

        /// <summary>Client: captures the local destroy decision before vanilla tears the object
        /// down. The prediction itself is registered after the game's own path ran.</summary>
        internal static ClientDestroyCapture CaptureClientDestroyed(InteractablePackagingBox box)
        {
            if (_applyingRemote > 0 || box == null)
            {
                return null;
            }

            WorldClientBehaviour.ReleaseHoldForDestroyedBox(box);

            if (WorldClientBehaviour.IsBoxDestroyForwardSuppressed())
            {
                // A container operation already carries the box id and owns the authoritative
                // result; a competing BoxDestroyRequest would be refused and resurrect the box.
                GameObjectIdentityApi.Unbind(box);
                return null;
            }

            // A furniture package is retired by the placement state/removal, not by a box
            // destroy intent. If the boxed furniture is still attached, this is a sale/trash of
            // the furniture: tell the host to retire its copy. Unboxing detaches the furniture
            // first, so it does not send a destroy.
            if (box is InteractablePackagingBox_Shelf shelf)
            {
                if (BoxNetworkInteraction.TryGetBoxedFurniture(shelf, out var furniture)
                    && furniture != null)
                {
                    var intent = new FurnitureDestroyIntent
                    {
                        Entity = furniture,
                    };
                    GameObjectIdentityClientBehaviour.SendToHost(intent);
                    CoopPlugin.Log.LogInfo("[box-id] forwarded furniture destroy id="
                        + (GameObjectIdentityApi.TryGetId(furniture, out var furnitureId)
                            ? furnitureId.ToString() : "<none>") + ".");
                }

                GameObjectIdentityApi.Unbind(box);
                return null;
            }

            if (!GameObjectIdentityCache.TryGetBoundId(box, out var gameObjectId)
                || gameObjectId.GuidValue == Guid.Empty)
            {
                return null;
            }

            var id = gameObjectId.GuidValue;
            WorldClientBehaviour.ForgetDestroyedBox(id);

            DetachStoredBoxForTeardown(box);
            GameObjectIdentityApi.Unbind(box);
            CoopPlugin.Log.LogInfo("[box-id] captured client destroy-forward id=" + id + ".");
            return new ClientDestroyCapture { Id = id, Box = box };
        }

        /// <summary>Forwards the local destroy after the game's own <c>OnDestroyed</c> ran. The
        /// game already owns the local mutation; the host either retires its copy (the
        /// authoritative EntityRemoved confirms it) or has no live box to remove, which is
        /// already the state this peer reached.</summary>
        internal static void ForwardClientDestroyed(ClientDestroyCapture capture)
        {
            if (capture == null)
            {
                return;
            }

            CoopPlugin.Log.LogInfo("[box-id] forwarding client destroy id=" + capture.Id + ".");
            var intent = new BoxDestroyRequestMessage
            {
                BoxNetworkId = capture.Id,
            };
            GameObjectIdentityClientBehaviour.SendToHost(intent);
        }

        /// <summary>Host: freeze each box's save-time slot for one connection's snapshot manifest.
        /// Captured at save serialization, in the game's own filter/order, so the guest's loaded
        /// list lines up index for index.</summary>
        internal static void CaptureTransferManifest(WorldTransferManifest manifest)
        {
            if (manifest == null)
            {
                return;
            }

            manifest.Reset();
            CaptureTransferKindSlots(RestockInterop.ItemBoxes, true, manifest);
            CaptureTransferKindSlots(RestockInterop.CardBoxes, false, manifest);
        }

        private static void CaptureTransferKindSlots<T>(IList<T> boxes, bool skipStored,
            WorldTransferManifest manifest)
            where T : InteractablePackagingBox
        {
            var slot = 0;
            for (var i = 0; boxes != null && i < boxes.Count; i++)
            {
                var box = boxes[i];
                if (box == null)
                {
                    continue;
                }

                if (skipStored && box is InteractablePackagingBox_Item item && item.m_IsStored)
                {
                    // The records backend saves the warehouse record, not this live box;
                    // counting it would shift every later slot.
                    continue;
                }

                var id = WorldHostBehaviour.EnsureHostBoxId(box);
                if (id == Guid.Empty)
                {
                    continue;
                }

                manifest.SetBoxSlot(id, slot);
                slot++;
            }
        }

        /// <summary>Client: the ordered identity baseline precedes the world baseline, so the
        /// guest's save boxes are captured lazily at the first descriptor, once the transferred
        /// save has loaded. A scene reload invalidates the capture explicitly. Deferred
        /// descriptors must survive this marker: they arrived in the identity baseline that
        /// precedes it.</summary>
        internal static void BeginClientBaseline()
        {
            // The baseline is the authoritative save snapshot. Recapture the scene slots here even
            // if an early descriptor tried to resolve them while the transferred save was still
            // loading; a lazy one-shot capture at that point is empty/partial and makes every save
            // box look unowned, so the baseline materializes a second copy instead of binding the
            // existing one.
            InvalidateSceneSlots();
            EnsureSceneSlots();
        }

        internal static void CompleteClientBaseline()
        {
            if (IsHost)
            {
                return;
            }

            SweepUnrepresentedSaveBoxes();
        }

        internal static void InvalidateSceneSlots()
        {
            SceneSlots.Clear();
            BaselineSaveBoxes.Clear();
            _sceneSlotsCaptured = false;
        }

        internal static void ApplyPendingClientDescriptor(Guid id, InteractablePackagingBox box)
        {
            if (PendingClientDescriptors.TryGetValue(id, out var descriptor))
            {
                PendingClientDescriptors.Remove(id);
                ApplyDescriptorToBox(box, descriptor);
            }
        }

        internal static void DeferClientDescriptor(PackagingBoxEntityDescriptor descriptor)
        {
            PendingClientDescriptors[descriptor.BoxNetworkId] = descriptor;
            CoopPlugin.Log.LogInfo("[box-id] deferring authoritative descriptor id="
                + descriptor.BoxNetworkId + " until the local delivery spawn binds it.");
        }

        internal static void EnsureClientSceneSlots() => EnsureSceneSlots();

        internal static bool TryResolveSceneSlotForCreate(PackagingBoxEntityDescriptor descriptor,
            out InteractablePackagingBox box)
        {
            EnsureSceneSlots();
            return TryResolveSceneSlot(descriptor.BoxKind, descriptor.SaveSlot,
                descriptor.BoxNetworkId, out box);
        }

        internal static void ApplyDescriptorToBoxForCreate(InteractablePackagingBox box,
            PackagingBoxEntityDescriptor descriptor)
            => ApplyDescriptorToBox(box, descriptor);

        /// <summary>Materializes one descriptor and applies its contents/pose. The caller binds
        /// the returned box to the descriptor identity.</summary>
        internal static bool TryMaterialize(PackagingBoxEntityDescriptor descriptor,
            out InteractablePackagingBox box)
        {
            box = Materialize(descriptor);
            if (box == null)
            {
                return false;
            }

            ApplyDescriptorToBox(box, descriptor);
            return true;
        }

        internal static void ReleasePendingClientDescriptors(IReadOnlyList<Guid> ids)
        {
            for (var i = 0; ids != null && i < ids.Count; i++)
            {
                PendingClientDescriptors.Remove(ids[i]);
            }
        }

        /// <summary>Client: makes a box exist for an authoritative descriptor, either by applying
        /// it to the already-bound local object (a creator's own box) or by materializing it.</summary>
        internal static bool TryEnsureClientBox(PackagingBoxEntityDescriptor descriptor)
        {
            if (descriptor == null || descriptor.BoxNetworkId == Guid.Empty)
            {
                return false;
            }

            var identity = GameObjectId.Box(descriptor.BoxNetworkId);
            if (GameObjectIdentityCache.TryResolveBound(identity, out var bound)
                && bound is InteractablePackagingBox existing)
            {
                ApplyDescriptorToBox(existing, descriptor);
                return true;
            }

            if (!BoxIdentityAdapter.TryCreate(descriptor, out var created)
                || created is not InteractablePackagingBox box)
            {
                return false;
            }

            GameObjectIdentityApi.BindClient(box, identity);
            return true;
        }

        /// <summary>Client: converts a container-state box payload into the lifecycle descriptor
        /// used by the identity materialization path.</summary>
        internal static PackagingBoxEntityDescriptor DescribeState(Guid id, BoxNetworkState state)
        {
            if (state == null)
            {
                return null;
            }

            var cards = new List<BoxCardState>(state.Cards?.Count ?? 0);
            if (state.Cards != null)
            {
                for (var i = 0; i < state.Cards.Count; i++)
                {
                    cards.Add(state.Cards[i]);
                }
            }

            return new PackagingBoxEntityDescriptor
            {
                BoxNetworkId = id,
                BoxKind = state.Kind,
                Position = state.Position,
                Rotation = state.Rotation,
                BoxBig = state.IsBig,
                BoxOpened = state.IsBoxOpened,
                BoxItemType = state.ItemType,
                BoxItemCount = state.ItemCount,
                BoxCards = cards,
            };
        }

        /// <summary>Client: materializes one box for the warehouse channel while suppressing the
        /// local creation hook (the warehouse owns the binding).</summary>
        internal static bool TryMaterializeWarehouseTake(Guid id, EItemType itemType, int amount,
            bool isBig, out InteractablePackagingBox_Item box)
        {
            box = null;
            if (id == Guid.Empty)
            {
                return false;
            }

            var descriptor = new PackagingBoxEntityDescriptor
            {
                BoxNetworkId = id,
                BoxKind = BoxNetworkKind.Item,
                BoxItemType = itemType,
                BoxItemCount = amount,
                BoxBig = isBig,
                Position = Vector3.zero,
                Rotation = Quaternion.identity,
            };

            _materializing++;
            try
            {
                box = MaterializeItem(descriptor);
            }
            finally
            {
                _materializing--;
            }

            if (box == null)
            {
                return false;
            }

            GameObjectIdentityApi.BindClient(box, GameObjectId.Box(id));
            return true;
        }

        /// <summary>Client: spawns the live representation of a record-backed warehouse box. The
        /// caller stores it into the compartment and binds it afterwards.</summary>
        internal static InteractablePackagingBox_Item MaterializeWarehouseStored(EItemType itemType,
            int amount, bool isBig)
        {
            _materializing++;
            try
            {
                return RestockManager.SpawnPackageBoxItem(itemType, amount, isBig);
            }
            finally
            {
                _materializing--;
            }
        }

        /// <summary>Tears a box down through the game's own <c>OnDestroyed</c> path (which removes
        /// it from RestockManager's lists) while suppressing destroy forwarding.</summary>
        internal static void DestroyWithoutNotification(InteractablePackagingBox box)
        {
            if (box == null)
            {
                return;
            }

            _applyingRemote++;
            try
            {
                DetachStoredBoxForTeardown(box);
                box.OnDestroyed();
                // OnDestroyed schedules the GameObject destroy for the end of the frame. Disable
                // it now so the dead box cannot be clicked or cast a shadow for that frame.
                if (box.gameObject != null)
                {
                    box.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(box.gameObject);
                }
            }
            finally
            {
                _applyingRemote--;
            }
        }

        private static void AnnounceHostBox(InteractablePackagingBox box)
        {
            var id = WorldHostBehaviour.EnsureHostBoxId(box);
            if (id == Guid.Empty)
            {
                CoopPlugin.Log.LogWarning("[box-id] host box " + box.name
                    + " could not be assigned an identity; it is not announced.");
                return;
            }

            if (!BoxIdentityAdapter.TryDescribe(box, out var descriptor))
            {
                CoopPlugin.Log.LogWarning("[box-id] host box id=" + id + " has no descriptor; "
                    + "it is not announced.");
                return;
            }

            GameObjectIdentityApi.HostPublishAdded(descriptor);
            WorldHostBehaviour.PublishCreatedBoxHold(box, id);
            CoopPlugin.Log.LogInfo("[box-id] created id=" + id + " kind=" + box.GetType().Name);
        }

        private static InteractablePackagingBox Materialize(PackagingBoxEntityDescriptor descriptor)
        {
            _materializing++;
            try
            {
                return descriptor.BoxKind switch
                {
                    BoxNetworkKind.Item => MaterializeItem(descriptor),
                    BoxNetworkKind.Card => MaterializeCard(descriptor),
                    _ => Unsupported(descriptor),
                };
            }
            finally
            {
                _materializing--;
            }
        }

        private static InteractablePackagingBox Unsupported(PackagingBoxEntityDescriptor descriptor)
        {
            // An unknown kind must not throw down the reliable lane; the caller drops it.
            CoopPlugin.Log.LogWarning("[box-id] unsupported authoritative box kind "
                + descriptor.BoxKind + " id=" + descriptor.BoxNetworkId + ".");
            return null;
        }

        private static InteractablePackagingBox_Item MaterializeItem(
            PackagingBoxEntityDescriptor descriptor)
        {
            return RestockManager.SpawnPackageBoxItem(descriptor.BoxItemType,
                descriptor.BoxItemCount, descriptor.BoxBig);
        }

        private static InteractablePackagingBox_Card MaterializeCard(
            PackagingBoxEntityDescriptor descriptor)
        {
            if (_cardSpawnAnchor == null)
            {
                _cardSpawnAnchor = new GameObject("CardShopCoop.BoxNetworkCardSpawn").transform;
            }

            _cardSpawnAnchor.SetPositionAndRotation(descriptor.Position, descriptor.Rotation);
            var cards = new List<CardData>(descriptor.BoxCards.Count);
            for (var i = 0; i < descriptor.BoxCards.Count; i++)
            {
                var card = BoxNetworkInteraction.FromState(descriptor.BoxCards[i]);
                if (card.cardGrade > 10)
                {
                    // Register the host's certificate like every other reconstruction path.
                    // Grading Overhaul flags any graded card whose cert is not burned and bound
                    // on this PC. No-op when that mod is absent.
                    WorldCardInteraction.RegisterGradedCardCert(card, "box materialize");
                }

                cards.Add(card);
            }

            return RestockManager.SpawnPackageBoxCard(cards, _cardSpawnAnchor);
        }

        private static void ApplyDescriptorToBox(InteractablePackagingBox box,
            PackagingBoxEntityDescriptor descriptor)
        {
            var state = new BoxNetworkState
            {
                BoxNetworkId = descriptor.BoxNetworkId,
                Kind = descriptor.BoxKind,
                Position = descriptor.Position,
                Rotation = descriptor.Rotation,
                ItemType = descriptor.BoxItemType,
                ItemCount = descriptor.BoxItemCount,
                IsBig = descriptor.BoxBig,
                IsBoxOpened = descriptor.BoxOpened,
                Cards = descriptor.BoxCards,
            };

            if (box is InteractablePackagingBox_Item item)
            {
                BoxNetworkInteraction.ApplyItemState(item, state);
            }

            // The descriptor carries the newest authoritative pose. A local throw or placement
            // still awaiting the host is newer, though, and posing on top of it teleported the
            // just-thrown box.
            if (!BoxNetworkInteraction.IsHeld(box)
                && !WorldClientBehaviour.HasPendingBoxMove(descriptor.BoxNetworkId))
            {
                BoxNetworkInteraction.ApplyPose(box, descriptor.Position, descriptor.Rotation);
            }
        }

        private static void EnsureSceneSlots()
        {
            if (IsHost || _sceneSlotsCaptured)
            {
                return;
            }

            // The save is the host's. Stored item boxes are represented by the warehouse channel,
            // never by a free save-slot identity, so both backends use the same non-stored order.
            var itemBoxes = RestockInterop.ItemBoxes;
            var cardBoxes = RestockInterop.CardBoxes;
            if (itemBoxes == null || cardBoxes == null)
            {
                // The manager is not loaded yet. Do not latch an empty snapshot: the next
                // baseline/retry must see the transferred save's real boxes.
                return;
            }

            SceneSlots.Clear();
            SceneSlots[(byte)BoxNetworkKind.Item] = SnapshotBoxes(itemBoxes, true);
            SceneSlots[(byte)BoxNetworkKind.Card] = SnapshotBoxes(cardBoxes, false);
            _sceneSlotsCaptured = true;

            BaselineSaveBoxes.Clear();
            foreach (var boxes in SceneSlots.Values)
            {
                BaselineSaveBoxes.AddRange(boxes);
            }
        }

        private static List<InteractablePackagingBox> SnapshotBoxes<T>(IList<T> boxes,
            bool skipStored)
            where T : InteractablePackagingBox
        {
            // Free save-slot identity excludes stored warehouse boxes on every backend; those
            // entries belong to the warehouse value protocol and its Guids.
            var list = new List<InteractablePackagingBox>();
            for (var i = 0; boxes != null && i < boxes.Count; i++)
            {
                var box = boxes[i];
                if (box == null)
                {
                    continue;
                }

                if (skipStored && box is InteractablePackagingBox_Item item && item.m_IsStored)
                {
                    continue;
                }

                list.Add(box);
            }

            return list;
        }

        private static bool TryResolveSceneSlot(BoxNetworkKind kind, int slot, Guid boxNetworkId,
            out InteractablePackagingBox box)
        {
            box = null;
            if (!SceneSlots.TryGetValue((byte)kind, out var boxes) || slot < 0
                || slot >= boxes.Count)
            {
                return false;
            }

            var candidate = boxes[slot];
            if (candidate == null)
            {
                return false;
            }

            if (candidate is InteractablePackagingBox_Item storedItem
                && BoxNetworkInteraction.IsStoredBox(storedItem))
            {
                // A stored warehouse box is not a free save box. The warehouse baseline owns its
                // identity and representation; adopting it here would list one box twice.
                return false;
            }

            if (GameObjectIdentityCache.TryGetBoundId(candidate, out var boundId)
                && boundId.GuidValue != boxNetworkId)
            {
                if (GameObjectIdentityCache.TryResolveBound(boundId, out _))
                {
                    // The box at this slot genuinely belongs to another live id; let the caller
                    // materialize the authoritative descriptor.
                    CoopPlugin.Log.LogWarning("[box-id] snapshot slot " + slot + " for kind=" + kind
                        + " is bound to live id=" + boundId + " (wanted " + boxNetworkId
                        + "); materializing the authoritative box.");
                    return false;
                }

                CoopPlugin.Log.LogWarning("[box-id] snapshot slot " + slot + " for kind=" + kind
                    + " had stale id=" + boundId + "; rebinding to " + boxNetworkId + ".");
            }

            box = candidate;
            return true;
        }

        private static void SweepUnrepresentedSaveBoxes()
        {
            if (IsHost || BaselineSaveBoxes.Count == 0)
            {
                return;
            }

            var swept = 0;
            for (var i = 0; i < BaselineSaveBoxes.Count; i++)
            {
                var box = BaselineSaveBoxes[i];
                if (box == null)
                {
                    continue;
                }

                if (box is InteractablePackagingBox_Item storedItem
                    && BoxNetworkInteraction.IsStoredBox(storedItem))
                {
                    // A stored warehouse box is a value in a compartment; the warehouse channel
                    // owns it. Sweeping it here would destroy the box the host baseline is about
                    // to bind.
                    continue;
                }

                if (GameObjectIdentityCache.TryGetBoundId(box, out var boundId)
                    && GameObjectIdentityCache.TryResolveBound(boundId, out _))
                {
                    continue;
                }

                CoopPlugin.Log.LogInfo("[box-id] sweeping save box " + box.name
                    + " the host no longer represents.");
                DestroyWithoutNotification(box);
                swept++;
            }

            BaselineSaveBoxes.Clear();
            if (swept > 0)
            {
                CoopPlugin.Log.LogInfo("[box-id] swept " + swept
                    + " unbound save box(es) after the world baseline.");
            }
        }

        /// <summary>Removes a stored item box from its warehouse compartment, if it is in one.
        /// Idempotent: a box already detached (or not stored) is left alone.</summary>
        internal static void DetachStoredBoxForTeardown(InteractablePackagingBox box)
        {
            if (box is not InteractablePackagingBox_Item item)
            {
                return;
            }

            var compartment = item.GetBoxStoredCompartment();
            if (compartment == null)
            {
                return;
            }

            var stored = compartment.GetInteractablePackagingBoxList();
            if (stored != null && stored.Contains(item))
            {
                CoopPlugin.Log.LogInfo("[warehouse] detaching destroyed stored box " + item.name
                    + " from shelf=" + compartment.GetWarehouseIndex() + " comp="
                    + compartment.GetIndex() + "; the slot would otherwise stay blocked.");
                compartment.RemoveBox(item);
            }
        }

        internal static bool TryGetSaveSlot(InteractablePackagingBox box, Guid boxId, out int slot)
        {
            slot = -1;
            var connectionId = GameObjectIdentityBaselineContext.ConnectionId;
            if (connectionId <= 0)
            {
                return false;
            }

            if (WorldHostBehaviour.TryGetBoxSnapshotSlot(connectionId, boxId, out slot))
            {
                return true;
            }

            if (WorldHostBehaviour.HasBoxSnapshotManifest(connectionId))
            {
                // The snapshot names this connection's save boxes. A box absent from it was
                // created after the save and has no save slot, so the guest materializes it.
                slot = -1;
                return false;
            }

            if (TryGetLiveBoxSlot(box, out slot))
            {
                CoopPlugin.Log.LogWarning("[box-id] no frozen snapshot manifest for box id=" + boxId
                    + "; this connection's baseline is using live slot " + slot + ".");
                return true;
            }

            slot = -1;
            return false;
        }

        private static bool TryGetLiveBoxSlot(InteractablePackagingBox box, out int slot)
        {
            slot = -1;
            if (box == null)
            {
                return false;
            }

            if (box is InteractablePackagingBox_Card)
            {
                return TrySlotInList(RestockInterop.CardBoxes, box, false, out slot);
            }

            return TrySlotInList(RestockInterop.ItemBoxes, box, true, out slot);
        }

        private static bool TrySlotInList<T>(IList<T> boxes, InteractablePackagingBox box,
            bool skipStored, out int slot)
            where T : InteractablePackagingBox
        {
            slot = 0;
            for (var i = 0; boxes != null && i < boxes.Count; i++)
            {
                var candidate = boxes[i];
                if (candidate == null)
                {
                    continue;
                }

                if (skipStored && candidate is InteractablePackagingBox_Item item && item.m_IsStored)
                {
                    continue;
                }

                if (ReferenceEquals(candidate, box))
                {
                    return true;
                }

                slot++;
            }

            slot = -1;
            return false;
        }
    }

    internal static class BoxIdentityAdapter
    {
        internal static bool TryGetId(object entity, out GameObjectId id)
        {
            // Guid boxes are bound explicitly by their creator. A furniture packaging box has no
            // identity of its own: it is a view of the placed furniture, so its id is the
            // furniture's session Guid. The cache stores that as a view without evicting the
            // canonical furniture binding.
            id = default;
            return entity is InteractablePackagingBox_Shelf shelf
                && BoxNetworkInteraction.TryGetBoxedFurniture(shelf, out var furniture)
                && GameObjectIdentityApi.TryGetId(furniture, out var furnitureId)
                && furnitureId.Kind == GameObjectKind.Furniture
                && (id = furnitureId).IsValid;
        }

        internal static bool TryResolve(GameObjectId id, out object entity)
        {
            // Guid boxes resolve through their direct binding. A furniture identity resolves
            // canonically to the placed object; callers that need its package use
            // GameObjectIdentityApi.TryResolveBox. Returning the package here would let generic
            // entity resolution bind the furniture id to a transient view.
            entity = null;
            return false;
        }

        internal static bool TryDescribe(object entity, out EntityDescriptor descriptor)
        {
            descriptor = null;
            // A furniture package is described by its placement entity, never as a box entity.
            if (entity is InteractablePackagingBox_Shelf)
            {
                return false;
            }

            if (entity is not InteractablePackagingBox box
                || !GameObjectIdentityCache.TryGetBoundId(box, out var gameObjectId)
                || gameObjectId.Kind != GameObjectKind.PackagingBox
                || gameObjectId.GuidValue == Guid.Empty)
            {
                return false;
            }

            if (box is InteractablePackagingBox_Item item && item.m_IsStored)
            {
                // The warehouse channel carries the stored box; the box baseline must not
                // materialize a second physical representation of it.
                return false;
            }

            var id = gameObjectId.GuidValue;
            var state = BoxNetworkInteraction.DescribeBox(id, box);
            var boxDescriptor = BoxIdentityLifecycle.DescribeState(id, state);

            if (BoxIdentityLifecycle.TryGetSaveSlot(box, id, out var slot))
            {
                boxDescriptor.SaveSlot = slot;
            }

            descriptor = boxDescriptor;
            return true;
        }

        internal static bool TryCreate(EntityDescriptor descriptor, out object entity)
        {
            entity = null;
            if (descriptor is not PackagingBoxEntityDescriptor boxDescriptor
                || boxDescriptor.BoxNetworkId == Guid.Empty)
            {
                return false;
            }

            if (CoopCore.Role != CoopRole.Host)
            {
                BoxIdentityLifecycle.EnsureClientSceneSlots();

                // A checkout pre-assigned this id for a box its own vanilla delivery queue will
                // still spawn. Materializing now would leave a duplicate beside that box, so hold
                // the descriptor until the real object is bound.
                if (WorldClientBehaviour.IsClientOwnedBox(boxDescriptor.BoxNetworkId))
                {
                    BoxIdentityLifecycle.DeferClientDescriptor(boxDescriptor);
                    return false;
                }

            }

            // A snapshot slot names a box this peer recovered from the transferred save. A slot
            // is only a binding hint: a missing or already-differently-bound entry falls back to
            // the authoritative descriptor.
            if (boxDescriptor.SaveSlot >= 0
                && BoxIdentityLifecycle.TryResolveSceneSlotForCreate(boxDescriptor,
                    out var sceneBox))
            {
                BoxIdentityLifecycle.ApplyDescriptorToBoxForCreate(sceneBox, boxDescriptor);
                CoopPlugin.Log.LogInfo("[box-id] bound save box id=" + boxDescriptor.BoxNetworkId
                    + " kind=" + boxDescriptor.BoxKind + " slot=" + boxDescriptor.SaveSlot + ".");
                entity = sceneBox;
                return true;
            }

            if (!BoxIdentityLifecycle.TryMaterialize(boxDescriptor, out var box))
            {
                CoopPlugin.Log.LogWarning("[box-id] could not materialize authoritative box id="
                    + boxDescriptor.BoxNetworkId + " kind=" + boxDescriptor.BoxKind
                    + " slot=" + boxDescriptor.SaveSlot + "; descriptor dropped.");
                return false;
            }

            CoopPlugin.Log.LogInfo("[box-id] materialized authoritative box id="
                + boxDescriptor.BoxNetworkId + " kind=" + boxDescriptor.BoxKind
                + " slot=" + boxDescriptor.SaveSlot + " object=" + box.name + ".");
            entity = box;
            return true;
        }

        internal static bool Destroy(object entity)
        {
            if (entity is not InteractablePackagingBox box)
            {
                return false;
            }

            // Drop every hold (local and mirrored) before the game's teardown, or the destroyed
            // object stays parented to a hand/anchor as a ghost.
            if (GameObjectIdentityCache.TryGetBoundId(box, out var gameObjectId)
                && gameObjectId.GuidValue != Guid.Empty)
            {
                WorldClientBehaviour.ForgetDestroyedBox(gameObjectId.GuidValue);
            }

            WorldClientBehaviour.ReleaseHoldForDestroyedBox(box);
            BoxIdentityLifecycle.DestroyWithoutNotification(box);
            return true;
        }
    }
}
