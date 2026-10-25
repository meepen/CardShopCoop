using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Decoration;
using CardShopCoop.Modules.Actions;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using CardShopCoop.Util;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.GameObjectIdentity
{
    public sealed partial class GameObjectIdentityHostBehaviour
    {
        /// <summary>The identity baseline was sent before this callback, so every settled decoration
        /// currently bound is now known to at least one peer and later re-placements must publish a
        /// replacement instead of a first add.</summary>
        [OnFullyJoined]
        private void MarkDecorationBaselinePublished(PeerConnection connection)
        {
            if (connection != null)
            {
                GameObjectIdentityHostHooks.MarkDecorationBaselinePublished();
            }
        }
    }

    internal static partial class GameObjectIdentityHostHooks
    {
        /// <summary>Decoration ids the host has already announced (baseline or added). A published
        /// piece that is placed again is a move; an unpublished one is its first announcement.</summary>
        private static readonly HashSet<GameObjectId> PublishedDecorations = new();
        private static long _nextDecorationId = 1;
        private static InteractableObject _placingDecoration;
        private static int _intentDepth;
        private static long _proposedDecorationId;

        private static bool InDecorationIntent => _intentDepth > 0;

        private static partial void InstallDecoration(Harmony harmony)
        {
            PublishedDecorations.Clear();
            _nextDecorationId = 1;
            _placingDecoration = null;
            _intentDepth = 0;
            _proposedDecorationId = 0;

            PatchDecoration(harmony, typeof(ShelfManager), "InitDecoObject",
                new[] { typeof(InteractableObject) }, postfix: nameof(InitDecoObjectPostfix));
            PatchDecoration(harmony, typeof(ShelfManager), "RemoveDecoObject",
                new[] { typeof(InteractableObject) }, prefix: nameof(RemoveDecoObjectPrefix));
            PatchDecoration(harmony, typeof(InteractableObject), "PlaceMovedObject", Type.EmptyTypes,
                prefix: nameof(PlaceMovedObjectPrefix),
                finalizer: nameof(PlaceMovedObjectFinalizer));
            PatchDecoration(harmony, typeof(InteractableObject), "OnPlacedMovedObject", Type.EmptyTypes,
                postfix: nameof(OnPlacedMovedObjectPostfix));
            PatchDecoration(harmony, typeof(InteractableObject), "BoxUpObject",
                new[] { typeof(bool) }, prefix: nameof(BoxUpObjectPrefix),
                postfix: nameof(BoxUpObjectPostfix));
            PatchDecoration(harmony, typeof(InteractableObject), "OnLockedRoomHidden", Type.EmptyTypes,
                prefix: nameof(OnLockedRoomHiddenPrefix),
                postfix: nameof(OnLockedRoomHiddenPostfix));

            BindExistingDecorations();
        }

        /// <summary>A host can start its session while the shop is already loaded, so decorations
        /// that predate the hooks were never seen by InitDecoObject. Bind them before the first
        /// baseline is built.</summary>
        internal static void BindExistingDecorations()
        {
            if (!DecorationInterop.IsSceneReady())
            {
                return;
            }

            var live = DecorationInterop.GetLiveDecorations();
            for (var i = 0; i < live.Count; i++)
            {
                var obj = live[i];
                if (obj == null || obj.GetIsMovingObject()
                    || GameObjectIdentityCache.TryGetBoundId(obj, out _))
                {
                    continue;
                }

                EnsureHostId(obj);
            }
        }

        private static void PatchDecoration(Harmony harmony, Type type, string name, Type[] args,
            string prefix = null, string postfix = null, string finalizer = null)
        {
            var original = ReflectionSurface.RequiredMethod(type, name, args);
            harmony.Patch(original,
                prefix == null ? null : new HarmonyMethod(typeof(GameObjectIdentityHostHooks), prefix),
                postfix == null ? null : new HarmonyMethod(typeof(GameObjectIdentityHostHooks), postfix),
                finalizer: finalizer == null
                    ? null
                    : new HarmonyMethod(typeof(GameObjectIdentityHostHooks), finalizer));
        }

        /// <summary>Every decoration enters the game's decoration list exactly once. Binding here
        /// gives the piece its lifetime id before any state or removal can reference it; it is not
        /// announced until a placement settles.</summary>
        private static void InitDecoObjectPostfix(InteractableObject obj)
        {
            if (!DecorationInterop.IsDecoration(obj)
                || GameObjectIdentityCache.TryGetBoundId(obj, out _))
            {
                return;
            }

            var id = NextDecorationId();
            GameObjectIdentityApi.BindHost(obj, id);
            CoopPlugin.Log.LogInfo("[identity] decoration bound id=" + id + " type="
                + obj.m_DecoObjectType + ".");
        }

        /// <summary>Allocates the single host id space. A client that placed a new piece proposes a
        /// negative id in its intent; the host adopts that exact id so the client's preview is
        /// already the authoritative object when the added message arrives.</summary>
        private static GameObjectId NextDecorationId()
        {
            if (InDecorationIntent && _proposedDecorationId < 0
                && !GameObjectIdentityCache.TryResolveBound(
                    GameObjectId.Decoration(_proposedDecorationId), out _))
            {
                return GameObjectId.Decoration(_proposedDecorationId);
            }

            long value;
            do
            {
                value = _nextDecorationId++;
            }
            while (value <= 0
                || GameObjectIdentityCache.TryResolveBound(GameObjectId.Decoration(value), out _));

            return GameObjectId.Decoration(value);
        }

        internal static long EnsureHostId(InteractableObject obj)
        {
            if (obj == null)
            {
                return 0;
            }

            if (GameObjectIdentityCache.TryGetBoundId(obj, out var id)
                && id.Kind == GameObjectKind.Decoration)
            {
                return id.Value;
            }

            var created = NextDecorationId();
            GameObjectIdentityApi.BindHost(obj, created);
            return created.Value;
        }

        /// <summary>All destruction paths (box-up, locked-room hide, scene teardown) run through
        /// the game's decoration list removal. Publishing here covers every one of them once.</summary>
        private static void RemoveDecoObjectPrefix(InteractableObject obj)
        {
            if (!DecorationInterop.IsDecoration(obj)
                || !DecorationIdentityAdapter.TryGetId(obj, out var id))
            {
                return;
            }

            if (PublishedDecorations.Remove(id))
            {
                GameObjectIdentityApi.HostPublishRemoved(new EntityRemovedMessage
                {
                    Id = id,
                    Mode = EntityRemovalMode.Destroy,
                });
                CoopPlugin.Log.LogInfo("[identity] decoration removed id=" + id + ".");
            }

            GameObjectIdentityApi.Unbind(obj);
        }

        /// <summary>Marks the synchronous placement commit so the shared OnPlacedMovedObject
        /// callback (also used by box-up and the hold cancel) only publishes real placements.</summary>
        private static void PlaceMovedObjectPrefix(InteractableObject __instance)
        {
            if (__instance != null && DecorationInterop.IsDecoration(__instance)
                && __instance.GetIsMovingObject())
            {
                _placingDecoration = __instance;
            }
        }

        private static void PlaceMovedObjectFinalizer() => _placingDecoration = null;

        /// <summary>A settled placement either announces the piece or, when peers already know the
        /// id, replaces it at the new pose. Identity has no separate pose-update message, so a move
        /// is the atomic remove-with-replacement pair.</summary>
        private static void OnPlacedMovedObjectPostfix(InteractableObject __instance)
        {
            if (__instance == null || !ReferenceEquals(_placingDecoration, __instance)
                || !DecorationInterop.IsDecoration(__instance))
            {
                return;
            }

            if (!DecorationIdentityAdapter.TryGetId(__instance, out var id))
            {
                var created = EnsureHostId(__instance);
                if (created == 0)
                {
                    return;
                }

                id = GameObjectId.Decoration(created);
            }

            var descriptor = DecorationIdentityAdapter.Describe(__instance, id);
            if (descriptor == null)
            {
                return;
            }

            if (PublishedDecorations.Contains(id))
            {
                GameObjectIdentityApi.HostPublishRemoved(new EntityRemovedMessage
                {
                    Id = id,
                    Mode = EntityRemovalMode.Destroy,
                    Replacement = descriptor,
                });
                CoopPlugin.Log.LogInfo("[identity] decoration moved id=" + id + " type="
                    + descriptor.DecorationType + ".");
                return;
            }

            PublishedDecorations.Add(id);
            GameObjectIdentityApi.HostPublishAdded(descriptor);
            CoopPlugin.Log.LogInfo("[identity] decoration added id=" + id + " type="
                + descriptor.DecorationType + ".");

            // The intent handler broadcasts its own inventory delta with the prediction id, so an
            // intent-driven placement must not send a second, prediction-less count.
            if (!InDecorationIntent)
            {
                DecorationHostBehaviour.PublishInventoryDelta(DecorationActions.Place,
                    descriptor.DecorationType);
            }
        }

        private struct InventoryState
        {
            internal EDecoObject Type;
        }

        private static void BoxUpObjectPrefix(InteractableObject __instance, out InventoryState __state)
            => __state = new InventoryState
            {
                Type = DecorationInterop.IsDecoration(__instance)
                    ? __instance.m_DecoObjectType
                    : EDecoObject.None,
            };

        private static void BoxUpObjectPostfix(InventoryState __state)
        {
            if (__state.Type == EDecoObject.None || InDecorationIntent)
            {
                return;
            }

            DecorationHostBehaviour.PublishInventoryDelta(DecorationActions.Remove, __state.Type);
        }

        private static void OnLockedRoomHiddenPrefix(InteractableObject __instance,
            out InventoryState __state)
            => __state = new InventoryState
            {
                Type = DecorationInterop.IsDecoration(__instance)
                    ? __instance.m_DecoObjectType
                    : EDecoObject.None,
            };

        private static void OnLockedRoomHiddenPostfix(InventoryState __state)
        {
            if (__state.Type == EDecoObject.None || InDecorationIntent)
            {
                return;
            }

            DecorationHostBehaviour.PublishInventoryDelta(DecorationActions.Remove, __state.Type);
        }

        internal static void MarkDecorationBaselinePublished()
        {
            var live = DecorationInterop.GetLiveDecorations();
            for (var i = 0; i < live.Count; i++)
            {
                var obj = live[i];
                if (obj == null || obj.GetIsMovingObject())
                {
                    continue;
                }

                if (DecorationIdentityAdapter.TryGetId(obj, out var id))
                {
                    PublishedDecorations.Add(id);
                }
            }
        }

        internal static IDisposable BeginDecorationIntent(long proposedId)
        {
            _intentDepth++;
            var previous = _proposedDecorationId;
            if (proposedId < 0)
            {
                _proposedDecorationId = proposedId;
            }

            return new DecorationIntentScope(previous);
        }

        private sealed class DecorationIntentScope : IDisposable
        {
            private readonly long _previous;

            internal DecorationIntentScope(long previous) => _previous = previous;

            public void Dispose()
            {
                _intentDepth--;
                _proposedDecorationId = _previous;
            }
        }
    }

    /// <summary>The decoration half of the per-kind identity adapters. Every decoration id lives in
    /// the shared cache; this class only knows how to describe, create and destroy the game object
    /// behind it.</summary>
    internal static class DecorationIdentityAdapter
    {
        internal static bool TryGetId(object entity, out GameObjectId id)
        {
            if (entity is InteractableObject obj && DecorationInterop.IsDecoration(obj)
                && GameObjectIdentityCache.TryGetBoundId(obj, out id))
            {
                return true;
            }

            id = default;
            return false;
        }

        internal static bool TryResolve(GameObjectId id, out object entity)
        {
            // A decoration id is assigned by the host and exists only in the shared cache.
            entity = null;
            return false;
        }

        internal static bool TryDescribe(object entity, out EntityDescriptor descriptor)
        {
            descriptor = null;
            if (entity is not InteractableObject obj || !DecorationInterop.IsDecoration(obj)
                || obj.GetIsMovingObject() || !TryGetId(entity, out var id))
            {
                return false;
            }

            descriptor = Describe(obj, id);
            return descriptor != null;
        }

        internal static DecorationEntityDescriptor Describe(InteractableObject obj, GameObjectId id)
        {
            if (obj == null || id.Kind != GameObjectKind.Decoration)
            {
                return null;
            }

            var pose = DecorationInterop.ReadPose(obj);
            return new DecorationEntityDescriptor
            {
                DecorationId = id.Value,
                DecorationType = pose.DecorationType,
                Position = pose.Position,
                Rotation = pose.Rotation,
                Vertical = pose.Vertical,
                WarehouseWallSnap = pose.WarehouseWallSnap,
                WallIndex = pose.Wall,
            };
        }

        internal static bool TryCreate(EntityDescriptor descriptor, out object entity)
        {
            entity = null;
            if (descriptor is not DecorationEntityDescriptor decoration
                || !decoration.Id.IsValid || !DecorationInterop.IsSceneReady())
            {
                return false;
            }

            if (GameObjectIdentityCache.TryResolveBound(decoration.Id, out var existing))
            {
                entity = existing;
                return existing != null;
            }

            var pose = ToPose(decoration);
            InteractableObject created;
            bool placed;
            using (GameObjectIdentityDecorationClientBehaviour.SuppressPrediction())
            {
                placed = DecorationInterop.TryPlace(pose, out created, authoritative: true);
            }

            if (!placed || created == null)
            {
                return false;
            }

            entity = created;
            CoopPlugin.Log.LogInfo("[identity] decoration created id=" + decoration.Id + " type="
                + decoration.DecorationType + ".");
            return true;
        }

        internal static bool Destroy(object entity)
        {
            if (entity is not InteractableObject obj || !DecorationInterop.IsDecoration(obj))
            {
                return false;
            }

            var id = TryGetId(obj, out var bound) ? bound.ToString() : "<unknown>";
            GameObjectIdentityApi.Unbind(obj);
            using (GameObjectIdentityDecorationClientBehaviour.SuppressPrediction())
            {
                DecorationInterop.RemoveLocalPlacedObject(obj);
            }

            CoopPlugin.Log.LogInfo("[identity] decoration destroyed id=" + id + ".");
            return true;
        }

        internal static DecorationPose ToPose(DecorationEntityDescriptor descriptor)
            => new()
            {
                Id = descriptor.DecorationId,
                DecorationType = descriptor.DecorationType,
                Position = descriptor.Position,
                Rotation = descriptor.Rotation,
                Vertical = descriptor.Vertical,
                WarehouseWallSnap = descriptor.WarehouseWallSnap,
                Wall = descriptor.WallIndex,
            };

        internal static DecorationEntityDescriptor ToDescriptor(GameObjectId id, DecorationPose pose)
        {
            if (!id.IsValid || pose == null)
            {
                return null;
            }

            return new DecorationEntityDescriptor
            {
                DecorationId = id.Value,
                DecorationType = pose.DecorationType,
                Position = pose.Position,
                Rotation = pose.Rotation,
                Vertical = pose.Vertical,
                WarehouseWallSnap = pose.WarehouseWallSnap,
                WallIndex = pose.Wall,
            };
        }

        /// <summary>Resolves a decoration id on either role. Replaces the old per-module hold-id
        /// maps; the id is meaningful only through the shared cache.</summary>
        internal static bool TryResolve(long id, out InteractableObject obj)
        {
            obj = null;
            return id != 0
                && GameObjectIdentityApi.TryResolve(GameObjectId.Decoration(id), out var entity)
                && (obj = entity as InteractableObject) != null;
        }

        internal static bool TryGetHoldId(InteractableObject obj, out long id)
        {
            if (TryGetId(obj, out var bound))
            {
                id = bound.Value;
                return true;
            }

            id = 0;
            return false;
        }

        internal static long HostIdFor(InteractableObject obj)
            => TryGetHoldId(obj, out var id) ? id : 0;

        internal static long ClientIdFor(InteractableObject obj) => HostIdFor(obj);

        /// <summary>A client proposes ids as <c>-(connectionId &lt;&lt; 32 | sequence)</c>; the high
        /// word names the owning connection so one peer cannot claim another peer's creation id.</summary>
        internal static bool IsProposedIdOwnedBy(long id, int connectionId)
        {
            if (id >= 0 || connectionId <= 0)
            {
                return false;
            }

            var raw = unchecked(-id);
            return (raw >> 32) == connectionId;
        }

        /// <summary>Host intent placement: resolve the stable id to the game object here, then run
        /// the module's object-level placement primitive.</summary>
        internal static bool TryPlaceHost(DecorationPose pose, long existingId,
            out InteractableObject placed)
        {
            placed = null;
            InteractableObject existing = null;
            if (existingId != 0 && !TryResolve(existingId, out existing))
            {
                return false;
            }

            return DecorationInterop.TryPlace(pose, existing, out placed);
        }

        internal static bool TryRemoveHost(long id)
            => TryResolve(id, out var obj) && DecorationInterop.TryRemove(obj);

        /// <summary>Creates the object behind a descriptor and binds it. Prediction undo needs the
        /// binding immediately; the identity message path binds after the adapter returns.</summary>
        internal static bool CreateBound(DecorationEntityDescriptor descriptor, bool proposed,
            out InteractableObject created)
        {
            created = null;
            if (!TryCreate(descriptor, out var entity) || entity is not InteractableObject obj)
            {
                return false;
            }

            if (proposed)
            {
                GameObjectIdentityApi.BindClientProposed(obj, descriptor.Id);
            }
            else
            {
                GameObjectIdentityApi.BindClient(obj, descriptor.Id);
            }

            created = obj;
            return true;
        }

        /// <summary>Host-side scope around one decoration intent. It lets the creation hook adopt
        /// the client-proposed id and suppresses the prediction-less inventory broadcast because
        /// the intent handler sends that delta with the prediction id.</summary>
        internal static IDisposable BeginHostIntent(long proposedId)
            => GameObjectIdentityHostHooks.BeginDecorationIntent(proposedId);
    }

    /// <summary>
    /// Client decoration lifecycle. The local scene mutates through vanilla first (the game owns
    /// placement and box-up); this behaviour records the predicted lifecycle, sends the intent, and
    /// reconciles the entity through the identity messages. It never creates or destroys a
    /// decoration from authoritative state itself.
    /// </summary>
    [ClientBehaviour]
    public sealed class GameObjectIdentityDecorationClientBehaviour : CoopBehaviour
    {
        private sealed class Preview
        {
            internal InteractableObject Object;
            internal GameObjectId Id;
            internal int Generation;
        }

        private sealed class BoxState
        {
            internal DecorationSnapshot Before;
            internal GameObjectId Id;
            internal EDecoObject Type;
            internal DecorationPose Pose;
            internal InteractableObject Instance;
            internal bool Proposed;
        }

        private sealed class Suppression : IDisposable
        {
            public void Dispose() => _suppress--;
        }

        private sealed class ReferenceComparer : IEqualityComparer<InteractableObject>
        {
            internal static readonly ReferenceComparer Instance = new();

            public bool Equals(InteractableObject left, InteractableObject right)
                => ReferenceEquals(left, right);

            public int GetHashCode(InteractableObject value)
                => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
        }

        private static GameObjectIdentityDecorationClientBehaviour Active;
        private static int _suppress;

        private readonly Dictionary<Guid, Preview> _previews = new();
        private readonly Dictionary<InteractableObject, DecorationPose> _moveOrigins =
            new(ReferenceComparer.Instance);
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private InteractableObject _placing;
        private bool _placingWasBound;
        private bool _joined;
        private bool _shutdown;
        private bool _prunePending;
        // Process-wide so a reconnecting peer cannot reuse an id this process already handed to
        // the host for a decoration that still exists.
        private static long _proposalSequence;

        internal static bool Suppressed => _suppress > 0;

        internal static IDisposable SuppressPrediction()
        {
            _suppress++;
            return new Suppression();
        }

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
            {
                return;
            }

            _context = RuntimeContext;
            Active = this;
            _suppress = 0;
            ActionLedger.ActionSettled += OnActionSettled;
            CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
            SceneManager.sceneLoaded += OnSceneLoaded;
            _harmony = new Harmony("dev.meepen.cardshopcoop.gameobjectidentity.decoration.client");
            Patch(_harmony, typeof(ShelfManager), "Awake", Type.EmptyTypes,
                postfix: nameof(Hooks.ShelfAwakePostfix));
            Patch(_harmony, typeof(InteractableObject), "StartMoveObject", Type.EmptyTypes,
                prefix: nameof(Hooks.StartMoveObjectPrefix));
            Patch(_harmony, typeof(InteractableObject), "PlaceMovedObject", Type.EmptyTypes,
                prefix: nameof(Hooks.PlaceMovedObjectPrefix),
                postfix: nameof(Hooks.PlaceMovedObjectPostfix),
                finalizer: nameof(Hooks.PlaceMovedObjectFinalizer));
            Patch(_harmony, typeof(InteractableObject), "BoxUpObject", new[] { typeof(bool) },
                prefix: nameof(Hooks.BoxUpObjectPrefix), postfix: nameof(Hooks.BoxUpObjectPostfix));
            Patch(_harmony, typeof(InteractableObject), "OnLockedRoomHidden", Type.EmptyTypes,
                prefix: nameof(Hooks.OnLockedRoomHiddenPrefix));
        }

        private static void Patch(Harmony harmony, Type type, string name, Type[] args,
            string prefix = null, string postfix = null, string finalizer = null)
        {
            var original = ReflectionSurface.RequiredMethod(type, name, args);
            harmony.Patch(original,
                prefix == null ? null : new HarmonyMethod(typeof(Hooks), prefix),
                postfix == null ? null : new HarmonyMethod(typeof(Hooks), postfix),
                finalizer: finalizer == null ? null : new HarmonyMethod(typeof(Hooks), finalizer));
        }

        private bool CanPredict()
            => !_shutdown && _joined && _context != null && _context.InGame()
                && ActionLedger.IsActive && !ActionLedger.IsReconciling && !ActionLedger.IsApplying
                && _suppress == 0 && DecorationInterop.IsSceneReady();

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            // Previews and the placement marker belong to the outgoing scene.
            _previews.Clear();
            _moveOrigins.Clear();
            _placing = null;
            _placingWasBound = false;
        }

        private void OnWorldReady(CEventPlayer_GameDataFinishLoaded _)
        {
            // A save load rebuilds the decoration scene. Anything it leaves unbound is a local
            // copy of a world this peer no longer owns.
            if (_joined)
            {
                _prunePending = true;
            }

            TryPrune();
        }

        [OnFullyJoined]
        private void MarkJoined(PeerConnection connection)
        {
            _joined = true;
            _prunePending = true;
            TryPrune();
        }

        [OnClientDisconnected]
        private void ForgetHost(PeerConnection connection, DisconnectInfo info)
        {
            if (connection?.Id != 1)
            {
                return;
            }

            _joined = false;
            _previews.Clear();
            _moveOrigins.Clear();
            _placing = null;
        }

        private void TryPrune()
        {
            if (!_prunePending || _context == null || !_context.InGame()
                || !DecorationInterop.IsSceneReady())
            {
                return;
            }

            _prunePending = false;
            var live = DecorationInterop.GetLiveDecorations();
            var removed = 0;
            for (var i = 0; i < live.Count; i++)
            {
                var obj = live[i];
                if (obj == null || obj.GetIsMovingObject()
                    || GameObjectIdentityCache.TryGetBoundId(obj, out _))
                {
                    continue;
                }

                using (SuppressPrediction())
                {
                    DecorationInterop.RemoveLocalPlacedObject(obj);
                }
                removed++;
            }

            if (removed > 0)
            {
                CoopPlugin.Log.LogInfo("[identity] pruned " + removed
                    + " decoration(s) the identity baseline does not own.");
            }
        }

        private void OnDisable() => Shutdown();

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            ActionLedger.ActionSettled -= OnActionSettled;
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _harmony?.UnpatchSelf();
            _harmony = null;
            if (ReferenceEquals(Active, this))
            {
                Active = null;
            }

            _previews.Clear();
            _moveOrigins.Clear();
            _placing = null;
            _joined = false;
            _suppress = 0;
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        private long NextProposal()
        {
            var connection = CoopCore.LocalConnectionId;
            if (connection <= 0)
            {
                connection = 1;
            }

            _proposalSequence++;
            return -(((long)connection << 32) + _proposalSequence);
        }

        private void Predict(DecorationIntentMessage message, string key, Action apply, Action undo,
            Preview preview = null)
        {
            if (!CanPredict())
            {
                return;
            }

            ActionLedger.Submit(key, actionId =>
            {
                message.ActionId = actionId;
                if (preview != null)
                {
                    _previews[actionId] = preview;
                }

                _context.Send(1, message);
            }, apply, undo);
        }

        private void OnActionSettled(Guid actionId)
        {
            if (!_previews.TryGetValue(actionId, out var preview))
            {
                return;
            }

            _previews.Remove(actionId);

            // The host confirmed the piece by binding the same id as a client entity. Only a
            // still-proposed preview is a local object the host never accepted.
            if (GameObjectIdentityCache.TryResolve(preview.Id, out var current)
                && ReferenceEquals(current, preview.Object)
                && GameObjectIdentityCache.OwnershipOf(preview.Id) == GameObjectOwnership.ClientProposed)
            {
                GameObjectIdentityApi.Unbind(preview.Object);
                using (SuppressPrediction())
                {
                    DecorationInterop.RemoveLocalPlacedObject(preview.Object);
                }
            }
        }

        private void ApplyPlacePreview(Preview preview, GameObjectId proposed, long existingId,
            DecorationPose pose)
        {
            if (preview != null && preview.Generation != DecorationInterop.Generation)
            {
                return;
            }

            var instance = preview?.Object;
            if (instance == null)
            {
                // An earlier undo in the same reconcile removed the preview. The prediction still
                // owns that creation, so replay it from the descriptor it recorded.
                var id = proposed.IsValid ? proposed : GameObjectId.Decoration(existingId);
                var descriptor = DecorationIdentityAdapter.ToDescriptor(id, pose);
                DecorationIdentityAdapter.CreateBound(descriptor, proposed.IsValid, out instance);
                if (preview != null)
                {
                    preview.Object = instance;
                }
            }

            if (instance == null)
            {
                return;
            }

            DecorationInterop.ApplyPredictedPose(instance, pose);
            SceneRef<InteractionPlayerController>.Get()?.OnExitMoveObjectMode();
        }

        private void UndoPlacePreview(Preview preview, GameObjectId proposed, long existingId,
            DecorationPose original)
        {
            var instance = preview?.Object;
            if (proposed.IsValid)
            {
                // A fresh piece exists only because this prediction placed it. A confirmed piece
                // is already Client-owned and stays; only the proposal is discarded here.
                if (GameObjectIdentityCache.TryResolve(proposed, out var current)
                    && current is InteractableObject objectPreview
                    && GameObjectIdentityCache.OwnershipOf(proposed)
                        == GameObjectOwnership.ClientProposed)
                {
                    GameObjectIdentityApi.Unbind(objectPreview);
                    using (SuppressPrediction())
                    {
                        DecorationInterop.RemoveLocalPlacedObject(objectPreview);
                    }
                }

                return;
            }

            if (existingId != 0 && instance != null && original != null)
            {
                DecorationInterop.ApplyPose(instance, original);
            }
        }

        private void ApplyRemovePreview(BoxState state)
        {
            var instance = state.Instance;
            if (instance == null
                && !DecorationIdentityAdapter.TryResolve(state.Id.Value, out instance))
            {
                return;
            }

            DecorationInterop.FinalizeMovedObject(instance);
            GameObjectIdentityApi.Unbind(instance);
            using (SuppressPrediction())
            {
                DecorationInterop.RemoveLocalPlacedObject(instance);
            }
        }

        private void UndoRemovePreview(BoxState state)
        {
            if (state.Before == null || state.Before.Generation != DecorationInterop.Generation)
            {
                // The scene this removal described is gone; never resurrect its object or its
                // inventory into the new world.
                return;
            }

            DecorationInterop.Restore(state.Before);

            if (state.Proposed)
            {
                // The still-pending placement prediction owns this preview; its own retirement
                // decides whether the piece survives.
                return;
            }

            if (GameObjectIdentityCache.TryResolveBound(state.Id, out _))
            {
                return;
            }

            var descriptor = DecorationIdentityAdapter.ToDescriptor(state.Id, state.Pose);
            DecorationIdentityAdapter.CreateBound(descriptor, proposed: false, out _);
        }

        private static class Hooks
        {
            /// <summary>The shelf manager is the first point a load's decoration settings can be
            /// applied safely; the decoration state module owns that apply, not the hook.</summary>
            internal static void ShelfAwakePostfix() => DecorationClientBehaviour.NotifyShelfReady();

            /// <summary>Captures the settled pose before a local pickup, so a rejected move can be
            /// restored without a decoration pose snapshot on the wire.</summary>
            internal static void StartMoveObjectPrefix(InteractableObject __instance)
            {
                var client = Active;
                if (client == null || !client.CanPredict() || Suppressed
                    || !DecorationInterop.IsDecoration(__instance))
                {
                    return;
                }

                client._moveOrigins[__instance] = DecorationInterop.ReadPose(__instance);
            }

            internal static void PlaceMovedObjectPrefix(InteractableObject __instance)
            {
                var client = Active;
                if (client == null || !client.CanPredict()
                    || !DecorationInterop.IsDecoration(__instance)
                    || !__instance.GetIsMovingObject())
                {
                    return;
                }

                client._placing = __instance;
                client._placingWasBound = DecorationIdentityAdapter.TryGetId(
                    __instance, out _);
            }

            internal static void PlaceMovedObjectFinalizer()
            {
                var client = Active;
                if (client == null)
                {
                    return;
                }

                client._placing = null;
            }

            internal static void PlaceMovedObjectPostfix(InteractableObject __instance)
            {
                var client = Active;
                if (client == null || !client.CanPredict()
                    || !ReferenceEquals(client._placing, __instance)
                    || !DecorationInterop.IsDecoration(__instance))
                {
                    return;
                }

                var pose = DecorationInterop.ReadPose(__instance);
                client._moveOrigins.TryGetValue(__instance, out var original);
                client._moveOrigins.Remove(__instance);
                var existingId = 0L;
                var proposed = default(GameObjectId);
                if (client._placingWasBound
                    && DecorationIdentityAdapter.TryGetId(__instance, out var bound))
                {
                    existingId = bound.Value;
                }
                else
                {
                    proposed = GameObjectId.Decoration(client.NextProposal());
                    GameObjectIdentityApi.BindClientProposed(__instance, proposed);
                }

                var message = new DecorationIntentMessage
                {
                    Action = DecorationActions.Place,
                    DecorationType = pose.DecorationType,
                    Position = pose.Position,
                    Rotation = pose.Rotation,
                    Vertical = pose.Vertical,
                    WarehouseWallSnap = pose.WarehouseWallSnap,
                    WallIndex = pose.Wall,
                    Entity = existingId != 0 ? __instance : null,
                };
                var id = proposed.IsValid ? proposed.Value : existingId;
                var preview = new Preview
                {
                    Object = __instance,
                    Id = proposed.IsValid ? proposed : GameObjectId.Decoration(existingId),
                    Generation = DecorationInterop.Generation,
                };
                CoopPlugin.Log.LogInfo("[identity] decoration place intent id=" + id
                    + " new=" + proposed.IsValid + " type=" + pose.DecorationType + ".");
                client.Predict(message, "decoration:" + id,
                    () => client.ApplyPlacePreview(preview, proposed, existingId, pose),
                    () => client.UndoPlacePreview(preview, proposed, existingId, original),
                    proposed.IsValid ? preview : null);
            }

            internal static void BoxUpObjectPrefix(InteractableObject __instance,
                out BoxState __state)
            {
                __state = null;
                var client = Active;
                if (client == null || !DecorationInterop.IsDecoration(__instance)
                    || !DecorationIdentityAdapter.TryGetId(__instance, out var id))
                {
                    return;
                }

                client._moveOrigins.Remove(__instance);
                if (!client.CanPredict())
                {
                    return;
                }

                var pose = DecorationInterop.ReadPose(__instance);
                __state = new BoxState
                {
                    Before = DecorationInterop.Snapshot(),
                    Id = id,
                    Type = pose.DecorationType,
                    Pose = pose,
                    Instance = __instance,
                    Proposed = GameObjectIdentityCache.OwnershipOf(id)
                        == GameObjectOwnership.ClientProposed,
                };
            }

            internal static void BoxUpObjectPostfix(BoxState __state)
            {
                var client = Active;
                if (client == null || __state == null || !client.CanPredict())
                {
                    return;
                }

                var message = new DecorationIntentMessage
                {
                    Action = DecorationActions.Remove,
                    DecorationType = __state.Type,
                    Entity = __state.Instance,
                };
                CoopPlugin.Log.LogInfo("[identity] decoration remove intent id="
                    + __state.Id + " type=" + __state.Type + ".");
                client.Predict(message, "decoration:" + __state.Id.Value,
                    () => client.ApplyRemovePreview(__state),
                    () => client.UndoRemovePreview(__state));
            }

            /// <summary>Expansion and warehouse expansion hide the blocker and return its vertical
            /// decorations to the inventory through OnLockedRoomHidden. The host owns that removal
            /// and publishes it as an identity removal; the guest must not optimistically delete
            /// the pieces, or a rejected purchase removes them locally for good.</summary>
            internal static bool OnLockedRoomHiddenPrefix(InteractableObject __instance)
                => __instance == null || !DecorationInterop.IsDecoration(__instance);
        }
    }
}
