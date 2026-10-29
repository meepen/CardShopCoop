using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.World;
using CardShopCoop.Net.Connection;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.GameObjectIdentity
{
    /// <summary>
    /// Host-side placement lifecycle. The identity module mints and binds every placement id and
    /// publishes exactly one creation descriptor per placement object and one removal when the game
    /// destroys it. Pose/update replication stays in the World module; the World module never
    /// creates, removes, binds, or baselines a placement entity.
    /// </summary>
    public sealed partial class GameObjectIdentityHostBehaviour
    {
        // Fully connected peers, so a scene rebuild can re-announce identity to them. Keyed by
        // connection id because the transport only hands the behaviour a PeerConnection on the
        // join/disconnect edges.
        private readonly Dictionary<int, PeerConnection> _placementPeers = new();

        /// <summary>The world save is written after admission, so every live placement object must
        /// own its stable id before the transfer manifest freezes the save-time slots. Otherwise a
        /// guest that already loaded those objects from the save would have nothing to bind to and
        /// every entity would be materialized a second time.</summary>
        [OnClientJoined]
        private void PlacementClientJoined(PeerConnection connection)
            => PlacementObjectIdentity.BindLiveObjects();

        [OnFullyJoined]
        private void PlacementClientReady(PeerConnection connection)
        {
            if (connection != null)
            {
                _placementPeers[connection.Id] = connection;
            }
        }

        [OnClientDisconnected]
        private void PlacementClientLeft(PeerConnection connection, DisconnectInfo _)
        {
            if (connection != null)
            {
                _placementPeers.Remove(connection.Id);
            }
        }

        /// <summary>A rebuilt scene contains new placement objects with the same save-order slots,
        /// so placement ids restart with it and every peer must be re-announced.</summary>
        private void HandlePlacementSceneLoaded()
        {
            PlacementObjectIdentity.Reset();
            GameObjectIdentityBaselineContext.ConnectionId = 0;
            if (_placementPeers.Count == 0)
            {
                return;
            }

            var peers = new List<PeerConnection>(_placementPeers.Values);
            for (var i = 0; i < peers.Count; i++)
            {
                SendBaseline(peers[i]);
            }
        }

        internal static void OnPlacementSceneLoaded(Scene _, LoadSceneMode __)
            => Active?.HandlePlacementSceneLoaded();
    }

    internal static partial class GameObjectIdentityHostHooks
    {
        private static bool _placementSceneHooked;

        /// <summary>
        /// Placement creation and removal belong to this module. Creation is observed at the game's
        /// own list-registration hooks (every kind that lands in a replicated placement list), and
        /// removal at the game's destroy path (plus the generic-object list removal that runs inside
        /// it). Nothing here publishes pose deltas; World keeps that state.
        /// </summary>
        private static partial void InstallPlacement(Harmony harmony)
        {
            if (!_placementSceneHooked)
            {
                _placementSceneHooked = true;
                SceneManager.sceneLoaded += GameObjectIdentityHostBehaviour.OnPlacementSceneLoaded;
            }

            harmony.Patch(AccessTools.Method(typeof(InteractableObject), "OnDestroyed"),
                postfix: new HarmonyMethod(typeof(GameObjectIdentityHostHooks),
                    nameof(PlacementDestroyedPostfix)));
            harmony.Patch(AccessTools.Method(typeof(ShelfManager), "RemoveInteractableObject",
                    new[] { typeof(InteractableObject) }),
                postfix: new HarmonyMethod(typeof(GameObjectIdentityHostHooks),
                    nameof(PlacementListRemovedPostfix)));

            PatchPlacementInit(harmony, "InitInteractableObject", typeof(InteractableObject));
            PatchPlacementInit(harmony, "InitShelf", typeof(Shelf));
            PatchPlacementInit(harmony, "InitWarehouseShelf", typeof(WarehouseShelf));
            PatchPlacementInit(harmony, "InitCardShelf", typeof(CardShelf));
            PatchPlacementInit(harmony, "InitCardItemCombiShelf", typeof(CardItemCombiShelf));
            PatchPlacementInit(harmony, "InitTournamentPrizeShelf", typeof(TournamentPrizeShelf));
            PatchPlacementInit(harmony, "InitPlayTable", typeof(InteractablePlayTable));
            PatchPlacementInit(harmony, "InitAutoCleanser", typeof(InteractableAutoCleanser));
            PatchPlacementInit(harmony, "InitAutoPackOpener", typeof(InteractableAutoPackOpener));
            PatchPlacementInit(harmony, "InitWorkbench", typeof(InteractableWorkbench));
            PatchPlacementInit(harmony, "InitBulkDonationBox", typeof(InteractableBulkDonationBox));
            PatchPlacementInit(harmony, "InitCardStorageShelf", typeof(InteractableCardStorageShelf));
            PatchPlacementInit(harmony, "InitTrashBin", typeof(InteractableTrashBin));
            PatchPlacementInit(harmony, "InitEmptyBoxStorage", typeof(InteractableEmptyBoxStorage));
            PatchPlacementInit(harmony, "InitCashierCounter", typeof(InteractableCashierCounter));

            // A furniture purchase spawns its furniture, then boxes it. Defer the placement
            // descriptor until the package is attached.
            var purchaseSpawn = AccessTools.Method(typeof(ShelfManager),
                "SpawnInteractableObjectInPackageBox");
            if (purchaseSpawn != null)
            {
                harmony.Patch(purchaseSpawn,
                    prefix: new HarmonyMethod(typeof(GameObjectIdentityHostHooks),
                        nameof(FurniturePurchaseSpawnPrefix)),
                    postfix: new HarmonyMethod(typeof(GameObjectIdentityHostHooks),
                        nameof(FurniturePurchaseSpawnPostfix)),
                    finalizer: new HarmonyMethod(typeof(GameObjectIdentityHostHooks),
                        nameof(FurniturePurchaseSpawnFinalizer)));
            }

            // A host that starts a session inside an already-loaded shop has no init events left,
            // so bind what the world already contains before the first peer can join.
            PlacementObjectIdentity.BindLiveObjects();
        }

        private static void PatchPlacementInit(Harmony harmony, string method, Type argument)
        {
            var original = AccessTools.Method(typeof(ShelfManager), method, new[] { argument });
            if (original == null)
            {
                CoopPlugin.Log.LogWarning("placement identity init patch target missing: ShelfManager."
                    + method);
                return;
            }

            harmony.Patch(original, postfix: new HarmonyMethod(
                typeof(GameObjectIdentityHostHooks), nameof(PlacementInitPostfix)));
        }

        /// <summary>Every Init* hook is a static method whose first argument is the object that was
        /// just added to its placement list.</summary>
        public static void PlacementInitPostfix(InteractableObject __0)
            => PlacementObjectIdentity.HostCreated(__0);

        public static void PlacementListRemovedPostfix(InteractableObject __0)
            => PlacementObjectIdentity.HostRemoved(__0);

        public static void PlacementDestroyedPostfix(InteractableObject __instance)
            => PlacementObjectIdentity.HostRemoved(__instance);

        public static void FurniturePurchaseSpawnPrefix()
            => PlacementObjectIdentity.BeginFurniturePurchaseSpawn();

        public static void FurniturePurchaseSpawnPostfix()
            => PlacementObjectIdentity.CompleteFurniturePurchaseSpawn();

        public static void FurniturePurchaseSpawnFinalizer(Exception __exception)
        {
            if (__exception != null)
            {
                // A Harmony postfix does not run when the original throws. Release the depth or
                // every later placement would stay deferred until a scene reload.
                PlacementObjectIdentity.AbortFurniturePurchaseSpawn();
            }
        }
    }

    /// <summary>
    /// The single owner of placement identity: the id allocator, the host binding, descriptor
    /// construction, creation from an authoritative descriptor, and destruction. The World module
    /// keeps the game primitives and pose state only.
    /// </summary>
    internal static class PlacementObjectIdentity
    {
        internal const ushort Invalid = 0;

        /// <summary>Host-minted locator ids. A placement key is (kind &lt;&lt; 24) | id, so one
        /// monotonic counter keeps keys unique across kinds and never reuses an id in a scene.
        /// This key locates an object in the game's lists; the replicated identity is the
        /// session Guid bound to the same object.</summary>
        private static ushort _next = 1;
        private static readonly Dictionary<InteractableObject, int> ObjectKeys =
            new(ReferenceComparer.Instance);
        private static readonly Dictionary<int, InteractableObject> ObjectsByKey = new();
        // Host: the next new placement object is the furniture a client bought; bind it to the
        // Guid the buyer minted for that purchase instead of minting a second identity.
        private static Guid _pendingHostFurnitureId;
        // A furniture purchase boxes its furniture after the placement Init hook. Hold its
        // descriptor until the box is attached so peers never see an unboxed origin pose.
        private static int _furniturePurchaseDepth;
        private static InteractableObject _pendingFurniturePlacement;

        private sealed class ReferenceComparer : IEqualityComparer<InteractableObject>
        {
            internal static readonly ReferenceComparer Instance = new();

            public bool Equals(InteractableObject left, InteractableObject right)
                => ReferenceEquals(left, right);

            public int GetHashCode(InteractableObject value)
                => RuntimeHelpers.GetHashCode(value);
        }

        internal static void Reset()
        {
            _next = 1;
            ObjectKeys.Clear();
            ObjectsByKey.Clear();
            _pendingHostFurnitureId = Guid.Empty;
            _furniturePurchaseDepth = 0;
            _pendingFurniturePlacement = null;
        }

        internal static void BeginFurniturePurchaseSpawn() => _furniturePurchaseDepth++;

        internal static void CompleteFurniturePurchaseSpawn()
        {
            _furniturePurchaseDepth = Math.Max(0, _furniturePurchaseDepth - 1);
            if (_furniturePurchaseDepth != 0)
            {
                return;
            }

            var obj = _pendingFurniturePlacement;
            _pendingFurniturePlacement = null;
            if (obj != null && GameObjectIdentityHostBehaviour.Active != null)
            {
                HostCreated(obj);
            }
        }

        internal static void AbortFurniturePurchaseSpawn()
        {
            _furniturePurchaseDepth = Math.Max(0, _furniturePurchaseDepth - 1);
            if (_furniturePurchaseDepth == 0)
            {
                _pendingFurniturePlacement = null;
            }
        }

        internal static bool PushHostFurnitureId(Guid id)
        {
            if (id == Guid.Empty
                || GameObjectIdentityApi.TryResolve(GameObjectId.Furniture(id), out _)
                || GameObjectIdentityApi.TryResolve(GameObjectId.Box(id), out _))
            {
                return false;
            }

            _pendingHostFurnitureId = id;
            return true;
        }

        internal static void CancelHostFurnitureId(Guid id)
        {
            if (_pendingHostFurnitureId == id)
            {
                _pendingHostFurnitureId = Guid.Empty;
            }
        }

        internal static bool BindClientCreated(InteractableObject obj, Guid id)
        {
            if (obj == null || id == Guid.Empty)
            {
                return false;
            }

            if (GameObjectIdentityApi.TryGetBoundId(obj, out _))
            {
                return true;
            }

            var kind = PlacementInterop.FindKind(obj);
            if (kind < 0)
            {
                return false;
            }

            var key = EnsureLocator(obj, kind);
            return key != 0
                && BindByRole(obj, GameObjectId.Furniture(id), kind, ObjectIdOf(key));
        }

        internal static bool TryGetId(object entity, out GameObjectId id)
        {
            id = default;
            if (entity is not InteractableObject obj)
            {
                return false;
            }

            if (!GameObjectIdentityApi.TryGetBoundId(obj, out var bound)
                || bound.Kind != GameObjectKind.Furniture || !bound.IsValid)
            {
                return false;
            }

            id = bound;
            return true;
        }

        internal static bool TryResolve(GameObjectId id, out object entity)
        {
            entity = null;
            if (id.Kind != GameObjectKind.Furniture || !id.IsValid)
            {
                return false;
            }

            if (!GameObjectIdentityCache.TryResolveBound(id, out entity)
                || entity is not InteractableObject obj)
            {
                entity = null;
                return false;
            }

            // An identity only resolves while its object is still in a placement list.
            if (PlacementInterop.FindKind(obj) < 0)
            {
                entity = null;
                return false;
            }

            return true;
        }

        /// <summary>The bound object a (kind, object id) pair names, but only while it is still in
        /// its kind's live list. This is the placement-key resolve used by every consumer.</summary>
        internal static bool TryResolveInKind(ShelfManager manager, int kind, ushort id,
            out InteractableObject result)
        {
            result = null;
            var list = kind == PlacementApi.DecorationKind ? null : PlacementInterop.GetList(manager, kind);
            if (list == null || id == Invalid)
            {
                return false;
            }

            if (!ObjectsByKey.TryGetValue((kind << 24) | id, out var obj) || obj == null
                || !list.Contains(obj))
            {
                return false;
            }

            result = obj;
            return true;
        }

        internal static bool TryGetObjectKey(InteractableObject obj, out int key)
        {
            key = 0;
            if (obj == null)
            {
                return false;
            }

            // A packaging box is a view of its boxed furniture; the locator belongs to the
            // furniture, not to the transient package.
            if (obj is InteractablePackagingBox_Shelf shelf
                && BoxNetworkInteraction.TryGetBoxedFurniture(shelf, out var boxed))
            {
                obj = boxed;
            }

            return ObjectKeys.TryGetValue(obj, out key) && key != 0;
        }

        internal static bool IsIdentified(InteractableObject obj) => TryGetObjectKey(obj, out _);

        internal static ushort ObjectIdOf(int key) => (ushort)(key & 0xFFFF);

        internal static ushort ObjectIdFromCompartmentKey(int key) => ObjectIdOf(key >> 8);

        internal static ushort ObjectIdFromObjectKey(int key) => ObjectIdOf(key);

        internal static int KindOf(GameObjectId id) => id.SubKind;

        /// <summary>True when the object already owns a placement identity for <paramref name="kind"/>.</summary>
        internal static bool TryMakeObjectKey(int kind, InteractableObject obj, out int key, bool host)
        {
            key = 0;
            if (kind < 0 || kind == PlacementApi.DecorationKind
                || PlacementInterop.GetList(PlacementInterop.FindShelfManager(), kind) == null)
            {
                return false;
            }

            if (TryGetObjectKey(obj, out key))
            {
                return (key >> 24) == kind;
            }

            if (!host)
            {
                return false;
            }

            return AssignHost(obj, kind) != Invalid
                && TryGetObjectKey(obj, out key) && (key >> 24) == kind;
        }

        internal static bool TryMakeCompartmentKey(int kind, InteractableObject obj,
            int compartment, out int key, bool host)
        {
            key = 0;
            if (kind == PlacementApi.DecorationKind
                || !TryMakeObjectKey(kind, obj, out var objectKey, host))
            {
                return false;
            }

            key = (kind << 24) | (ObjectIdOf(objectKey) << 8) | (compartment & 0xFF);
            return true;
        }

        /// <summary>Host: the stable id of a live placement object, minting one at first use.</summary>
        internal static ushort AssignHost(InteractableObject obj)
        {
            if (obj == null)
            {
                return Invalid;
            }

            if (TryGetObjectKey(obj, out var existing))
            {
                return ObjectIdOf(existing);
            }

            var kind = PlacementInterop.FindKind(obj);
            return AssignHost(obj, kind);
        }

        private static ushort AssignHost(InteractableObject obj, int kind)
        {
            if (obj == null || kind < 0 || kind == PlacementApi.DecorationKind)
            {
                return Invalid;
            }

            if (TryGetObjectKey(obj, out var existing))
            {
                return ObjectIdOf(existing);
            }

            if (!GameObjectIdentityApi.TryGetBoundId(obj, out _))
            {
                var furnitureId = _pendingHostFurnitureId;
                _pendingHostFurnitureId = Guid.Empty;
                if (furnitureId == Guid.Empty
                    || GameObjectIdentityApi.TryResolve(GameObjectId.Furniture(furnitureId), out _)
                    || GameObjectIdentityApi.TryResolve(GameObjectId.Box(furnitureId), out _))
                {
                    if (furnitureId != Guid.Empty)
                    {
                        CoopPlugin.Log.LogWarning("[identity] buyer furniture id=" + furnitureId
                            + " was already claimed; minting a host id for " + obj.name + ".");
                    }

                    furnitureId = Guid.NewGuid();
                }

                var entityId = GameObjectId.Furniture(furnitureId);
                if (!GameObjectIdentityApi.BindHost(obj, entityId))
                {
                    CoopPlugin.Log.LogWarning("[identity] could not bind furniture id " + entityId
                        + " to " + obj.name + ".");
                    return Invalid;
                }
            }

            var key = EnsureLocator(obj, kind);
            return key == 0 ? Invalid : ObjectIdOf(key);
        }

        private static bool BindByRole(InteractableObject obj, GameObjectId id, int kind,
            ushort objectId)
        {
            var bound = CoopCore.Role == CoopRole.Host
                ? GameObjectIdentityApi.BindHost(obj, id)
                : GameObjectIdentityApi.BindClient(obj, id);
            if (!bound)
            {
                return false;
            }

            var key = (kind << 24) | objectId;
            if (ObjectsByKey.TryGetValue(key, out var existing) && !ReferenceEquals(existing, obj))
            {
                // The host locator can collide with a different local object when the guest's
                // list order differs. Keep identity independent of the locator and allocate a
                // local key for this object.
                key = EnsureLocator(obj, kind);
            }

            if (key == 0)
            {
                return false;
            }

            ObjectKeys[obj] = key;
            ObjectsByKey[key] = obj;
            return true;
        }

        /// <summary>Locates a placement object in its kind's list. The locator is local
        /// bookkeeping (save slots, compartment keys); it is never the replicated identity.</summary>
        private static int EnsureLocator(InteractableObject obj, int kind)
        {
            if (obj == null || kind < 0 || kind == PlacementApi.DecorationKind)
            {
                return 0;
            }

            if (TryGetObjectKey(obj, out var existing))
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
            while (id == Invalid || ObjectsByKey.ContainsKey((kind << 24) | id));

            var key = (kind << 24) | id;
            ObjectKeys[obj] = key;
            ObjectsByKey[key] = obj;
            return key;
        }

        /// <summary>Host: bind every placement object the loaded world already contains. No
        /// descriptor is published here; joining peers receive the identity baseline.</summary>
        internal static void BindLiveObjects()
        {
            var manager = PlacementInterop.FindShelfManager();
            if (manager == null)
            {
                return;
            }

            var bound = 0;
            for (var kind = 0; kind < PlacementApi.KindCount; kind++)
            {
                if (kind == PlacementApi.DecorationKind)
                {
                    continue;
                }

                var list = PlacementInterop.GetList(manager, kind);
                for (var i = 0; list != null && i < list.Count; i++)
                {
                    if (list[i] is InteractableObject obj && !IsIdentified(obj)
                        && AssignHost(obj, kind) != Invalid)
                    {
                        bound++;
                    }
                }
            }

            if (bound > 0)
            {
                CoopPlugin.Log.LogInfo("[identity] bound " + bound
                    + " live placement entities for the session.");
            }
        }

        /// <summary>Host: the game added a placement object. Bind its stable id and announce the
        /// complete descriptor once; later pose changes are state deltas from World.</summary>
        internal static void HostCreated(InteractableObject obj)
        {
            if (obj == null || GameObjectIdentityHostBehaviour.Active == null)
            {
                return;
            }

            var kind = PlacementInterop.FindKind(obj);
            if (kind < 0 || kind == PlacementApi.DecorationKind)
            {
                return;
            }

            if (_furniturePurchaseDepth > 0)
            {
                // The placement Init hook runs before the purchase package is attached. Bind the
                // identity now but announce only after the box-up has settled.
                if (AssignHost(obj, kind) == Invalid)
                {
                    CoopPlugin.Log.LogWarning("[identity] furniture purchase object " + obj.name
                        + " could not be bound.");
                }
                else
                {
                    _pendingFurniturePlacement = obj;
                }

                return;
            }

            if (AssignHost(obj, kind) == Invalid || !TryDescribe(obj, out var descriptor))
            {
                CoopPlugin.Log.LogWarning("[identity] placement object " + obj.name
                    + " could not be announced.");
                return;
            }

            GameObjectIdentityApi.HostPublishAdded(descriptor);
            PlacementInterop.NotifyStructureChanged(kind);
        }

        /// <summary>Host: the game is destroying a placement object (or removed it from the generic
        /// list on the way to destruction). Publish the removal once and unbind.</summary>
        internal static void HostRemoved(InteractableObject obj)
        {
            if (obj == null || GameObjectIdentityHostBehaviour.Active == null)
            {
                return;
            }

            if (!TryGetObjectKey(obj, out var key))
            {
                GameObjectIdentityApi.Unbind(obj);
                return;
            }

            var kind = key >> 24;
            ObjectKeys.Remove(obj);
            ObjectsByKey.Remove(key);
            if (TryGetId(obj, out var identity))
            {
                GameObjectIdentityApi.HostPublishRemoved(new EntityRemovedMessage
                {
                    Id = identity,
                    Mode = EntityRemovalMode.Destroy,
                });
            }

            // The World module needs to drop its pose state for this object before the game
            // destroys the component.
            PlacementInterop.NotifyPlacementObjectDestroyed(key, obj);
            GameObjectIdentityApi.Unbind(obj);
            PlacementInterop.NotifyStructureChanged(kind);
        }

        internal static bool TryDescribe(object entity, out EntityDescriptor descriptor)
        {
            descriptor = null;
            if (entity is not InteractableObject obj)
            {
                return false;
            }

            var kind = PlacementInterop.FindKind(obj);
            if (kind < 0 || kind == PlacementApi.DecorationKind)
            {
                return false;
            }

            if (!TryGetId(obj, out var id))
            {
                AssignHost(obj, kind);
                if (!TryGetId(obj, out id))
                {
                    return false;
                }
            }

            var key = EnsureLocator(obj, kind);
            if (key == 0)
            {
                return false;
            }

            var boxed = PlacementInterop.IsBoxed(obj);
            var package = boxed ? obj.GetPackagingBoxShelf() : null;
            var value = new PlacementEntityDescriptor
            {
                FurnitureId = id.GuidValue,
                PlacementKind = (byte)kind,
                ObjectId = ObjectIdOf(key),
                ObjectType = obj.m_ObjectType,
                Position = obj.transform.position,
                Rotation = obj.transform.rotation,
                IsBoxed = boxed,
                // A boxed save entry normally has its live package. If the package is not ready
                // yet, the furniture pose is a safe seed; the next placement delta settles it.
                BoxedPosition = package == null ? obj.transform.position : package.transform.position,
                BoxedRotation = package == null ? obj.transform.rotation : package.transform.rotation,
            };

            // A join baseline runs inside the baseline context, so the descriptor can name the slot
            // this entity occupied in the save the guest just loaded. The guest binds its existing
            // object instead of materializing a duplicate.
            var connectionId = GameObjectIdentityBaselineContext.ConnectionId;
            if (connectionId > 0 && WorldHostBehaviour.TryGetPlacementSnapshotSlot(connectionId,
                kind, value.ObjectId, out var slot))
            {
                value.SaveSlot = slot;
            }

            descriptor = value;
            return true;
        }

        /// <summary>Client: create or bind the placement entity an authoritative descriptor names.
        /// Unboxed furniture is materialized through the game factory; a boxed furniture object is
        /// owned by the box channel, so it is only bound when the save slot or an existing binding
        /// names it (the box descriptor then adopts the same identity).</summary>
        internal static bool TryCreate(EntityDescriptor descriptor, out object entity)
        {
            entity = null;
            if (descriptor is not PlacementEntityDescriptor placement
                || placement.FurnitureId == Guid.Empty
                || placement.ObjectId == Invalid
                || placement.ObjectType == EObjectType.None)
            {
                return false;
            }

            var kind = placement.PlacementKind;
            if (kind >= PlacementApi.KindCount || kind == PlacementApi.DecorationKind)
            {
                return false;
            }

            var manager = PlacementInterop.FindShelfManager();
            var list = PlacementInterop.GetList(manager, kind);
            if (list == null)
            {
                return false;
            }

            var id = GameObjectId.Furniture(placement.FurnitureId);
            if (!GameObjectIdentityCache.TryResolveBound(id, out var resolved))
            {
                var obj = ResolveSaveSlot(list, placement);
                if (obj != null)
                {
                    if (!BindByRole(obj, id, kind, placement.ObjectId))
                    {
                        return false;
                    }

                    resolved = obj;
                }
                else
                {
                    // Unboxed and boxed furniture both materialize from the placed object; boxing
                    // is a state of that object, not a separate entity.
                    obj = PlacementInterop.SpawnPlacedObject((int)placement.ObjectType,
                        placement.Position, placement.Rotation);
                    if (obj == null || !BindByRole(obj, id, kind, placement.ObjectId))
                    {
                        return false;
                    }

                    resolved = obj;
                }
            }

            if (resolved is not InteractableObject created)
            {
                return false;
            }

            ApplyCreatedState(created, placement);
            entity = created;
            PlacementInterop.NotifyStructureChanged(kind);
            return true;
        }

        /// <summary>The guest's own save-loaded object for this entity: the slot the host froze when
        /// it wrote the transferred save. The slot is only trusted when its object type matches, so
        /// a diverged save can never bind an identity to the wrong piece.</summary>
        private static InteractableObject ResolveSaveSlot(System.Collections.IList list,
            PlacementEntityDescriptor descriptor)
        {
            if (descriptor.SaveSlot < 0 || descriptor.SaveSlot >= list.Count
                || list[descriptor.SaveSlot] is not InteractableObject slot
                || slot.m_ObjectType != descriptor.ObjectType
                || GameObjectIdentityApi.TryGetBoundId(slot, out _))
            {
                return null;
            }

            return slot;
        }

        internal static void ApplyClientDescriptor(InteractableObject obj,
            PlacementEntityDescriptor descriptor)
            => ApplyCreatedState(obj, descriptor);

        private static void ApplyCreatedState(InteractableObject obj,
            PlacementEntityDescriptor descriptor)
        {
            if (PlacementInterop.IsBoxed(obj) != descriptor.IsBoxed)
            {
                if (descriptor.IsBoxed)
                {
                    // Furniture boxing is placement state. The box is a view of this object; the
                    // world box engine no longer owns a separate furniture entity.
                    PlacementInterop.BoxUpPlacedObject(obj);
                }
                else
                {
                    PlacementInterop.PlaceBoxedObject(obj, descriptor.Position, descriptor.Rotation);
                }
            }

            if (!descriptor.IsBoxed)
            {
                obj.transform.SetPositionAndRotation(descriptor.Position, descriptor.Rotation);
                PlacementInterop.SyncTagGroup(obj.transform);
                return;
            }

            var package = obj.GetPackagingBoxShelf();
            package?.transform.SetPositionAndRotation(descriptor.BoxedPosition,
                    descriptor.BoxedRotation);
        }

        internal static bool Destroy(object entity)
        {
            if (entity is not InteractableObject obj || !TryGetObjectKey(obj, out var key))
            {
                return false;
            }

            var kind = key >> 24;
            // Drop the World module's local and remote hold mirrors before teardown.
            if (TryGetId(obj, out var removedIdentity))
            {
                WorldClientBehaviour.ForgetDestroyedBox(removedIdentity.GuidValue);
            }

            // Drop the World module's pose state before the game tears the component down.
            PlacementInterop.NotifyPlacementObjectDestroyed(key, obj);
            // The package is a view of this furniture. Detach and retire it first so it cannot
            // outlive the furniture with a dead m_BoxedObject; then destroy the furniture through
            // the game's own path. The host removal postfix still needs the locator maps.
            var package = obj.GetPackagingBoxShelf() ?? PlacementInterop.FindPackagingBoxFor(obj);
            if (package != null)
            {
                package.EmptyBoxShelf();
                package.OnDestroyed();
            }

            obj.OnDestroyed();
            ObjectKeys.Remove(obj);
            ObjectsByKey.Remove(key);
            PlacementInterop.NotifyStructureChanged(kind);
            return true;
        }

    }

    /// <summary>Thin dispatch to <see cref="PlacementObjectIdentity"/>; the identity cache and the
    /// wire format stay owned by this module.</summary>
    internal static class PlacementIdentityAdapter
    {
        internal static bool TryGetId(object entity, out GameObjectId id)
            => PlacementObjectIdentity.TryGetId(entity, out id);

        internal static bool TryResolve(GameObjectId id, out object entity)
            => PlacementObjectIdentity.TryResolve(id, out entity);

        internal static bool TryDescribe(object entity, out EntityDescriptor descriptor)
            => PlacementObjectIdentity.TryDescribe(entity, out descriptor);

        internal static void ApplyClientDescriptor(InteractableObject obj,
            PlacementEntityDescriptor descriptor)
            => PlacementObjectIdentity.ApplyClientDescriptor(obj, descriptor);

        internal static bool BindClientCreated(InteractableObject obj, Guid id)
            => PlacementObjectIdentity.BindClientCreated(obj, id);

        internal static bool TryCreate(EntityDescriptor descriptor, out object entity)
            => PlacementObjectIdentity.TryCreate(descriptor, out entity);

        internal static bool Destroy(object entity) => PlacementObjectIdentity.Destroy(entity);
    }
}
