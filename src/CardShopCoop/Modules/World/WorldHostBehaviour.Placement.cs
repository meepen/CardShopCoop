using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.PlayTable;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.World
{
    /// <summary>Host authority for stable placement identities and settled object poses.</summary>
    public sealed partial class WorldHostBehaviour
    {
        private const float MaxPlacementCoordinate = 1000f;

        private static bool IsSanePose(Vector3 position, Quaternion rotation)
            => IsFiniteCoordinate(position.x) && IsFiniteCoordinate(position.y)
                && IsFiniteCoordinate(position.z)
                && IsFinite(rotation.x) && IsFinite(rotation.y)
                && IsFinite(rotation.z) && IsFinite(rotation.w);

        private static bool IsFiniteCoordinate(float value)
            => IsFinite(value) && Mathf.Abs(value) <= MaxPlacementCoordinate;

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

        private readonly PlacementPopulationState _placementPopulation = new();
        private readonly Dictionary<int, PlacementEntitySnapshot> _known = new();
        private readonly Dictionary<ushort, int> _knownKeyById = new();
        private readonly HashSet<int> _fullyJoined = new();
        private int _applyingIntent;
        private Guid _nextMutationPrediction;

        /// <summary>How far a settling player may be from the object they move. Vanilla only lets
        /// the mover settle an object that is right in front of them, so this is a generous bound
        /// that rejects an intent from across the shop.</summary>
        private const float PlacementReach = 6f;

        private static bool IsWithinPlacementReach(Vector3 player, Vector3 target)
            => (player - target).sqrMagnitude <= PlacementReach * PlacementReach;

        internal void InstallPlacement()
        {
            PlacementInterop.ProbeBetaSurface();
            SceneManager.sceneLoaded += OnPlacementSceneLoaded;
            InstallPlacementPatches();
            RefreshKnown();
        }

        internal void ShutdownPlacement()
        {
            SceneManager.sceneLoaded -= OnPlacementSceneLoaded;
            _placementPopulation.Reset();
            _known.Clear();
            _knownKeyById.Clear();
            _fullyJoined.Clear();
        }

        [OnFullyJoined]
        private void SendPlacementJoinBaseline(PeerConnection connection)
        {
            if (_shutdown || connection == null || !IsJoinPhase(connection.State))
            {
                return;
            }

            _fullyJoined.Add(connection.Id);
            SendBaseline(connection.Id);
        }

        /// <summary>Answers a fully-connected guest's request for one more baseline. This is
        /// deliberately per-connection: only the requester is re-sent, never the other players.
        /// A fresh baseline is built from the host's current live lists, so objects the guest's
        /// scene finished spawning after the join baseline are bound and posed on this pass.</summary>
        [MessageHandler(typeof(PlacementBaselineRequestMessage))]
        private void HandlePlacementBaselineRequest(MessageContext context,
            PlacementBaselineRequestMessage message)
        {
            if (_shutdown || context?.Connection == null
                || !IsJoinPhase(context.Connection.State))
            {
                return;
            }

            CoopPlugin.Log.LogInfo("[placement] re-sending baseline to connection "
                + context.ConnectionId + " on request.");
            SendBaseline(context.Connection.Id);
        }

        [OnClientDisconnected]
        private void ForgetPlacementConnection(PeerConnection connection, DisconnectInfo _)
        {
            if (connection != null)
            {
                _fullyJoined.Remove(connection.Id);
            }
        }

        [MessageHandler(typeof(PlacementMoveIntentMessage))]
        private void HandleMoveIntent(MessageContext messageContext,
            PlacementMoveIntentMessage message)
        {
            if (!IsPeerMessage(messageContext) || message == null)
            {
                return;
            }

            if (message.Move == null)
            {
                Reject(messageContext.Connection.Id, message.PredictionId,
                    "placement intent has no entity");
                return;
            }

            // A settled pose is host-applied, so it is bounded and finite before it can move a
            // real object: an infinite/extreme vector would teleport or corrupt the transform.
            if (!IsSanePose(message.Move.Pos, message.Move.Rot)
                || (message.Move.IsBoxed
                    && !IsSanePose(message.Move.BoxedPos, message.Move.BoxedRot)))
            {
                Reject(messageContext.Connection.Id, message.PredictionId,
                    "placement pose is not finite/bounded");
                return;
            }

            var table = PlacementApi.ResolveObjectByKey(message.Move.Key) as InteractablePlayTable;
            if (table != null && !PlayTableHostBehaviour.IsPlacementMoveAllowed(table))
            {
                Reject(messageContext.Connection.Id, message.PredictionId,
                    "play table is occupied or reserved");
                return;
            }

            var obj = PlacementApi.ResolveObjectByKey(message.Move.Key) as InteractableObject;
            if (obj == null || (message.Move.Type != PlacementInterop.NoType
                && PlacementInterop.TypeIdOf(obj) != message.Move.Type))
            {
                Reject(messageContext.Connection.Id, message.PredictionId,
                    "placement identity or type is stale");
                return;
            }

            // The requester must actually be standing at the object it settles, exactly as the
            // trade and deodorant intents require. The authoritative presence is used, never the
            // client-supplied pose.
            if (!_context.PeerPresence.TryGet(messageContext.Connection.Id, out var presence)
                || presence.Age > TimeSpan.FromSeconds(2)
                || !IsWithinPlacementReach(presence.Position, obj.transform.position))
            {
                Reject(messageContext.Connection.Id, message.PredictionId,
                    "sender is not within reach of the placement");
                return;
            }

            _applyingIntent++;
            try
            {
                if (!PlacementMoveState.Apply(message.Move, true))
                {
                    Reject(messageContext.Connection.Id, message.PredictionId,
                        "placement pose could not be applied");
                    return;
                }
            }
            finally
            {
                _applyingIntent--;
            }

            NotifyObjectChanged(obj, message.PredictionId, true);
        }

        internal void NotifyStructureChanged()
        {
            var current = BuildCurrentEntities();
            var removed = new List<PlacementEntitySnapshot>();
            foreach (var pair in _known)
            {
                if (!current.ContainsKey(pair.Key))
                {
                    removed.Add(pair.Value);
                }
            }

            for (var i = 0; i < removed.Count; i++)
            {
                PublishDelta(PlacementDeltaMessage.Remove, RemovalEntry(removed[i]), Guid.Empty);
                _known.Remove(removed[i].Key);
            }

            foreach (var pair in current)
            {
                if (!_known.TryGetValue(pair.Key, out var old))
                {
                    PublishDelta(PlacementDeltaMessage.Add, pair.Value.ToEntry(), Guid.Empty);
                }
                else if (!old.SameValue(pair.Value))
                {
                    PublishDelta(PlacementDeltaMessage.Update, pair.Value.ToEntry(), Guid.Empty);
                }

                _known[pair.Key] = pair.Value;
            }

            RebuildKeyIndex();
            PlacementInterop.NotifyStructureChanged(-1);
        }

        internal void NotifyObjectChanged(InteractableObject obj)
            => NotifyObjectChanged(obj, Guid.Empty, false);

        internal void SetNextMutationPrediction(Guid predictionId)
            => _nextMutationPrediction = predictionId;

        private void NotifyObjectChanged(InteractableObject obj, Guid predictionId, bool force)
        {
            if (_applyingIntent != 0 || obj == null)
            {
                return;
            }

            var kind = PlacementInterop.FindKind(obj);
            if (kind < 0)
            {
                // A packaging box (or other object outside the placement lists) changing pose is
                // owned by its own channel, not placement. Do not emit a structure refresh for it.
                return;
            }

            if (!PlacementApi.TryMakeObjectKey(kind, obj, out var key))
            {
                CoopPlugin.Log.LogInfo("[placement] changed without a key (" + obj.name + ").");
                NotifyStructureChanged();
                return;
            }

            var current = PlacementEntityState.Capture(obj, key);
            if (!_known.TryGetValue(key, out var old))
            {
                PublishDelta(PlacementDeltaMessage.Add, current.ToEntry(), predictionId);
            }
            else if (old.Type != current.Type || old.IsBoxed != current.IsBoxed
                || old.BoxedPos != current.BoxedPos || old.BoxedRot != current.BoxedRot)
            {
                PublishDelta(PlacementDeltaMessage.Update, current.ToEntry(), predictionId);
            }
            else if (force || old.Pos != current.Pos || old.Rot != current.Rot)
            {
                PublishDelta(PlacementDeltaMessage.Pose, current.ToEntry(), predictionId);
            }
            else
            {
                CoopPlugin.Log.LogInfo("[placement] no change key=" + key + " (" + obj.name + ").");
            }

            _known[key] = current;
            _knownKeyById[PlacementApi.ObjectIdFromObjectKey(key)] = key;
        }

        private void NotifyObjectRemoved(InteractableObject obj)
        {
            if (obj == null || !PlacementIdentity.TryGet(obj, out var id))
            {
                return;
            }

            var kind = PlacementInterop.FindKind(obj);
            var key = kind < 0 ? 0 : (kind << 24) | id;
            if (kind < 0)
            {
                // The object can already be gone from its list when its destroy runs, so
                // FindKind cannot name the kind. Recover the key this object was registered
                // under from its own id through the index instead of scanning _known.
                _knownKeyById.TryGetValue(id, out key);
            }

            if (key == 0)
            {
                PlacementIdentity.Forget(obj);
                return;
            }

            _knownKeyById.Remove(id);
            if (_known.TryGetValue(key, out var known))
            {
                PublishDelta(PlacementDeltaMessage.Remove, RemovalEntry(known), Guid.Empty);
                _known.Remove(key);
            }
            else
            {
                PublishDelta(PlacementDeltaMessage.Remove, new PlacementMoveEntry
                {
                    Key = key,
                    Type = PlacementInterop.TypeIdOf(obj),
                }, Guid.Empty);
            }
        }

        private PlacementBaselineMessage BuildBaseline(WorldTransferManifest manifest)
        {
            var manager = PlacementInterop.FindShelfManager();
            var population = _placementPopulation.BuildMessage(manifest);
            if (manager == null || population == null)
            {
                return null;
            }

            RefreshKnown();
            return new PlacementBaselineMessage
            {
                Population = population,
                Moves = PlacementMoveState.Build(manager),
            };
        }

        private void SendBaseline(int connectionId)
        {
            if (connectionId <= 0 || !_context.InGame())
            {
                return;
            }

            var baseline = BuildBaseline(TransferManifestFor(connectionId));
            if (baseline != null)
            {
                _context.Send(connectionId, baseline);
            }
        }

        private void SendBaselinesToJoined()
        {
            foreach (var connectionId in new List<int>(_fullyJoined))
            {
                SendBaseline(connectionId);
            }
        }

        private void PublishDelta(byte operation, PlacementMoveEntry entity, Guid predictionId)
        {
            if (_shutdown || !_context.InGame() || entity == null)
            {
                CoopPlugin.Log.LogInfo("[placement] skip delta op=" + operation + " key="
                    + (entity == null ? -1 : entity.Key) + " shutdown=" + _shutdown
                    + " inGame=" + _context.InGame() + ".");
                return;
            }

            CoopPlugin.Log.LogInfo("[placement] publish delta op=" + operation + " key="
                + entity.Key + ".");
            _context.Broadcast(new PlacementDeltaMessage
            {
                PredictionId = predictionId,
                Operation = operation,
                Entity = entity,
            });
        }

        private static PlacementMoveEntry RemovalEntry(PlacementEntitySnapshot entity)
            => new()
            {
                Key = entity.Key,
                Type = entity.Type,
            };

        private void Reject(int connectionId, Guid predictionId, string reason)
        {
            CoopPlugin.Log.LogWarning("placement intent rejected for connection=" + connectionId
                + ": " + reason);
            if (predictionId != Guid.Empty)
            {
                PredictionApi.Rollback(_context, connectionId, predictionId);
            }
        }

        private bool IsPeerMessage(MessageContext messageContext)
            => !_shutdown && _context.InGame() && messageContext?.Connection != null
                && IsJoinPhase(messageContext.Connection.State);

        private static bool IsJoinPhase(ConnectionState state)
            => state == ConnectionState.Transferring || state == ConnectionState.FullyJoined;

        private Dictionary<int, PlacementEntitySnapshot> BuildCurrentEntities()
        {
            var result = new Dictionary<int, PlacementEntitySnapshot>();
            var manager = PlacementInterop.FindShelfManager();
            for (var kind = 0; kind < PlacementApi.KindCount; kind++)
            {
                var list = PlacementInterop.GetList(manager, kind);
                for (var i = 0; list != null && i < list.Count; i++)
                {
                    if (list[i] is not InteractableObject obj
                        || !PlacementApi.TryMakeObjectKey(kind, obj, out var key))
                    {
                        continue;
                    }

                    result[key] = PlacementEntityState.Capture(obj, key);
                }
            }

            return result;
        }

        private void RefreshKnown()
        {
            _known.Clear();
            foreach (var pair in BuildCurrentEntities())
            {
                _known.Add(pair.Key, pair.Value);
            }

            RebuildKeyIndex();
        }

        /// <summary>Rebuild the id -> key index from the live snapshot. A placement id is globally
        /// unique to one object for its lifetime, so this is a direct object-id lookup for a
        /// removal whose kind can no longer be read from the lists.</summary>
        private void RebuildKeyIndex()
        {
            _knownKeyById.Clear();
            foreach (var key in _known.Keys)
            {
                var id = PlacementApi.ObjectIdFromObjectKey(key);
                if (id != PlacementIdentity.Invalid)
                {
                    _knownKeyById[id] = key;
                }
            }
        }

        private void OnPlacementSceneLoaded(Scene _, LoadSceneMode __)
        {
            _placementHold?.Reset();
            _placementPopulation.Reset();
            // Placement ids reset with the scene, so every frozen manifest is now stale; a
            // post-reload baseline falls back to live indices rather than binding a reused id.
            _transferManifests.Clear();
            _known.Clear();
            PlacementIdentity.Reset();
            RefreshKnown();
            SendBaselinesToJoined();
        }

        private void InstallPlacementPatches()
        {
            Patch(typeof(InteractableObject), "OnDestroyed", null,
                nameof(InteractableObjectDestroyedPostfix));
            Patch(typeof(InteractableObject), "BoxUpObject", null,
                nameof(ObjectMutationPostfix));
            Patch(typeof(InteractableObject), "PlaceMovedObject", null,
                nameof(ObjectMutationPostfix));

            PatchInit(typeof(ShelfManager), "InitInteractableObject", typeof(InteractableObject));
            PatchInit(typeof(ShelfManager), "InitDecoObject", typeof(InteractableObject));
            PatchInit(typeof(ShelfManager), "InitShelf", typeof(Shelf));
            PatchInit(typeof(ShelfManager), "InitWarehouseShelf", typeof(WarehouseShelf));
            PatchInit(typeof(ShelfManager), "InitCardShelf", typeof(CardShelf));
            PatchInit(typeof(ShelfManager), "InitCardItemCombiShelf", typeof(CardItemCombiShelf));
            PatchInit(typeof(ShelfManager), "InitTournamentPrizeShelf", typeof(TournamentPrizeShelf));
            PatchInit(typeof(ShelfManager), "InitPlayTable", typeof(InteractablePlayTable));
            PatchInit(typeof(ShelfManager), "InitAutoCleanser", typeof(InteractableAutoCleanser));
            PatchInit(typeof(ShelfManager), "InitAutoPackOpener", typeof(InteractableAutoPackOpener));
            PatchInit(typeof(ShelfManager), "InitWorkbench", typeof(InteractableWorkbench));
            PatchInit(typeof(ShelfManager), "InitBulkDonationBox", typeof(InteractableBulkDonationBox));
            PatchInit(typeof(ShelfManager), "InitCardStorageShelf", typeof(InteractableCardStorageShelf));
            PatchInit(typeof(ShelfManager), "InitTrashBin", typeof(InteractableTrashBin));
            PatchInit(typeof(ShelfManager), "InitEmptyBoxStorage", typeof(InteractableEmptyBoxStorage));
            PatchInit(typeof(ShelfManager), "InitCashierCounter", typeof(InteractableCashierCounter));
        }

        private void PatchInit(Type target, string method, Type argument)
        {
            var original = AccessTools.Method(target, method, new[] { argument });
            if (original == null)
            {
                CoopPlugin.Log.LogWarning("placement host init patch target missing: "
                    + target.Name + "." + method);
                return;
            }

            _harmony.Patch(original, postfix: new HarmonyMethod(
                typeof(WorldHostBehaviour), nameof(ShelfInitPostfix)));
        }

        private void Patch(Type target, string method, string prefix, string postfix)
        {
            var original = AccessTools.Method(target, method);
            if (original == null)
            {
                CoopPlugin.Log.LogWarning("placement host patch target missing: "
                    + target.Name + "." + method);
                return;
            }

            _harmony.Patch(original,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(WorldHostBehaviour), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(WorldHostBehaviour), postfix));
        }

        public static void ObjectMutationPostfix(InteractableObject __instance)
        {
            var host = _instance;
            if (host == null)
            {
                return;
            }

            // A PlaceMovedObject that actually went through clears the moving flag; if the object
            // is still moving the aim was invalid, so leave the hold and ghost alone instead of
            // telling observers the piece was placed.
            if (__instance != null && __instance.GetIsMovingObject())
            {
                return;
            }

            host._placementHold?.LocalEnded(__instance);
            var predictionId = host._nextMutationPrediction;
            host._nextMutationPrediction = Guid.Empty;
            host.NotifyObjectChanged(__instance, predictionId, false);
        }

        public static void ShelfInitPostfix()
            => _instance?.NotifyStructureChanged();

        public static void InteractableObjectDestroyedPostfix(InteractableObject __instance)
        {
            var host = _instance;
            if (__instance == null || host == null)
            {
                return;
            }

            host._placementHold?.LocalEnded(__instance);
            host.NotifyObjectRemoved(__instance);
            PlacementIdentity.Forget(__instance);
        }
    }
}
