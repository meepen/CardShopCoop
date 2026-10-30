using System;
using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.PlayTable;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.World
{
    /// <summary>Host authority for placement pose/state deltas. Placement creation, removal and
    /// identity are owned by the GameObjectIdentity module; this partial only observes and
    /// broadcasts pose/boxed state for entities that already exist.</summary>
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

        // Save-time slots are frozen when a joining peer's world save is written; the identity
        // module reads them back to stamp the descriptor the peer binds its loaded objects with.
        private readonly PlacementTransferSlots _placementSlots = new();
        private readonly Dictionary<InteractableObject, PlacementEntitySnapshot> _known =
            new(PlacementObjectComparer.Instance);
        private int _applyingIntent;

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
            InstallPlacementMutationPatches();
            RefreshKnown();
        }

        internal void ShutdownPlacement()
        {
            SceneManager.sceneLoaded -= OnPlacementSceneLoaded;
            _known.Clear();
        }

        [OnFullyJoined]
        private void SendPlacementJoinBaseline(PeerConnection connection)
        {
            if (_shutdown || connection == null || !IsJoinPhase(connection.State))
            {
                return;
            }

            SendBaseline(PeerHandle.ForConnection(connection));
        }

        /// <summary>Answers a fully-connected guest's request for one more state baseline. This is
        /// deliberately per-peer: only the requester is re-sent, never the other players. A fresh
        /// baseline is built from the host's current live lists, so objects the guest's scene
        /// finished spawning after the join baseline are posed on this pass.</summary>
        [MessageHandler]
        private void HandlePlacementBaselineRequest(CoopMessageContext context,
            PlacementBaselineRequestMessage message)
        {
            if (_shutdown || context?.Peer == null || !context.IsAuthenticated)
            {
                return;
            }

            CoopPlugin.Log.LogInfo("[placement] re-sending state baseline to "
                + context.Peer.Name + " on request.");
            SendBaseline(context.Peer);
        }

        /// <summary>The identity module removed a placement entity. Drop this host's pose state for
        /// it before the game destroys the component.</summary>
        internal static void OnPlacementObjectDestroyed(InteractableObject obj)
            => _instance?.ForgetPlacementObject(obj);

        private void ForgetPlacementObject(InteractableObject obj)
        {
            _placementHold?.LocalEnded(obj);
            if (obj != null)
            {
                _known.Remove(obj);
            }
        }

        [MessageHandler]
        private ActionOutcome HandleMoveIntent(CoopMessageContext context,
            PlacementMoveIntentMessage message)
        {
            if (!IsPlacementPeerMessage(context) || message == null)
            {
                return ActionOutcome.Reject("placement intent is not admissible");
            }

            if (message.Move == null || message.Move.Entity == null)
            {
                CoopPlugin.Log.LogWarning("[placement] dropped move intent from peer="
                    + context.Peer.Name + " with no live entity.");
                return RejectPlacement(context, "placement intent has no live entity", null);
            }

            // A settled pose is host-applied, so it is bounded and finite before it can move a
            // real object: an infinite/extreme vector would teleport or corrupt the transform.
            var obj = message.Move.Entity;
            if (!IsSanePose(message.Move.Pos, message.Move.Rot)
                || (message.Move.IsBoxed
                    && !IsSanePose(message.Move.BoxedPos, message.Move.BoxedRot)))
            {
                return RejectPlacement(context, "placement pose is not finite/bounded", obj);
            }

            var table = obj as InteractablePlayTable;
            if (table != null && !PlayTableHostBehaviour.IsPlacementMoveAllowed(table))
            {
                return RejectPlacement(context, "play table is occupied or reserved", obj);
            }

            // The requester must actually be standing at the object it settles, exactly as the
            // trade and deodorant intents require. The authoritative presence is used, never the
            // client-supplied pose.
            var player = context.Peer.Player;
            if (player == null
                || !RuntimeContext.PeerPresence.TryGet(player.Id, out var presence)
                || presence.Age > TimeSpan.FromSeconds(2)
                || !IsWithinPlacementReach(presence.Position, obj.transform.position))
            {
                return RejectPlacement(context, "sender is not within reach of the placement", obj);
            }

            _applyingIntent++;
            try
            {
                if (!PlacementMoveState.Apply(message.Move, true))
                {
                    return RejectPlacement(context, "placement pose could not be applied", obj);
                }
            }
            finally
            {
                _applyingIntent--;
            }

            // The actor's own state travels in the response; everyone else gets the delta.
            NotifyObjectChanged(obj, true, context.Peer);
            return ActionOutcome.Applied(new PlacementMoveResponse
            {
                Move = PlacementEntityState.Capture(obj).ToEntry(),
            });
        }

        /// <summary>Re-read the live lists: publish a state update for every entity whose observed
        /// state changed, record newly seen entities, and forget entities the identity module
        /// removed. Creation and removal themselves are never published here.</summary>
        internal void NotifyStructureChanged()
        {
            var current = BuildCurrentEntities();
            var stale = new List<InteractableObject>();
            foreach (var pair in _known)
            {
                if (!current.ContainsKey(pair.Key))
                {
                    stale.Add(pair.Key);
                }
            }

            for (var i = 0; i < stale.Count; i++)
            {
                _known.Remove(stale[i]);
            }

            foreach (var pair in current)
            {
                PublishState(pair.Value, false, null);
            }

            PlacementInterop.NotifyStructureChanged(-1);
        }

        internal void NotifyObjectChanged(InteractableObject obj)
            => NotifyObjectChanged(obj, false, null);

        private void NotifyObjectChanged(InteractableObject obj, bool force, ICoopPeer except)
        {
            if (_applyingIntent != 0 || obj == null)
            {
                return;
            }

            if (PlacementInterop.FindKind(obj) < 0)
            {
                // A packaging box (or other object outside the placement lists) changing pose is
                // owned by its own channel, not placement. Do not emit a structure refresh for it.
                return;
            }

            PublishState(PlacementEntityState.Capture(obj), force, except);
        }

        /// <summary>Publishes the smallest delta that carries <paramref name="current"/>. An entity
        /// the host has not observed yet gets a full update rather than an add: the identity module
        /// already announced it and its creation pose, so this is a reference-message state sync,
        /// not a creation.</summary>
        private void PublishState(PlacementEntitySnapshot current, bool forcePose, ICoopPeer except)
        {
            if (current == null || current.Entity == null
                || !PlacementInterop.HasPlacementIdentity(current.Entity,
                    PlacementInterop.FindKind(current.Entity)))
            {
                CoopPlugin.Log.LogInfo("[placement] skipped state without a shared placement entity.");
                return;
            }

            if (!_known.TryGetValue(current.Entity, out var old))
            {
                PublishDelta(PlacementDeltaMessage.Update, current.ToEntry(), except);
            }
            else if (old.IsBoxed != current.IsBoxed
                || old.BoxedPos != current.BoxedPos || old.BoxedRot != current.BoxedRot)
            {
                PublishDelta(PlacementDeltaMessage.Update, current.ToEntry(), except);
            }
            else if (forcePose || old.Pos != current.Pos || old.Rot != current.Rot)
            {
                PublishDelta(PlacementDeltaMessage.Pose, current.ToEntry(), except);
            }
            else
            {
                CoopPlugin.Log.LogInfo("[placement] no change entity="
                    + PlacementInterop.Describe(current.Entity) + ".");
            }

            _known[current.Entity] = current;
        }

        private PlacementBaselineMessage BuildBaseline()
        {
            var manager = PlacementInterop.FindShelfManager();
            if (manager == null)
            {
                return null;
            }

            RefreshKnown();
            return new PlacementBaselineMessage
            {
                Moves = PlacementMoveState.Build(manager),
            };
        }

        private void SendBaseline(ICoopPeer peer)
        {
            if (peer == null || !RuntimeContext.InGame())
            {
                return;
            }

            var baseline = BuildBaseline();
            if (baseline != null)
            {
                peer.Send(baseline);
            }
        }

        private void PublishDelta(byte operation, PlacementMoveEntry entity, ICoopPeer except)
        {
            if (_shutdown || !RuntimeContext.InGame() || entity == null || entity.Entity == null
                || !PlacementInterop.HasSharedIdentity(entity.Entity))
            {
                CoopPlugin.Log.LogInfo("[placement] skip delta op=" + operation + " entity="
                    + (entity == null ? "<no-entry>" : PlacementInterop.Describe(entity.Entity))
                    + " shutdown=" + _shutdown + " inGame=" + RuntimeContext.InGame() + ".");
                return;
            }

            CoopPlugin.Log.LogInfo("[placement] publish delta op=" + operation + " entity="
                + PlacementInterop.Describe(entity.Entity) + ".");
            var delta = new PlacementDeltaMessage
            {
                Operation = operation,
                Entity = entity,
            };
            if (except != null)
            {
                Context.BroadcastExcept(except, delta);
            }
            else
            {
                Context.Broadcast(delta);
            }
        }

        private static ActionOutcome RejectPlacement(CoopMessageContext context, string reason,
            InteractableObject entity)
        {
            CoopPlugin.Log.LogWarning("[placement] move rejected peer="
                + (context?.Peer?.Name ?? "<unknown>") + ": " + reason + ".");
            var response = entity != null
                ? new PlacementMoveResponse
                {
                    Move = PlacementEntityState.Capture(entity).ToEntry(),
                }
                : null;
            return ActionOutcome.Reject(reason, response);
        }

        private bool IsPlacementPeerMessage(CoopMessageContext context)
            => !_shutdown && RuntimeContext.InGame() && context?.Peer != null
                && context.IsAuthenticated;

        private static bool IsJoinPhase(ConnectionState state)
            => state == ConnectionState.Transferring || state == ConnectionState.FullyJoined;

        private Dictionary<InteractableObject, PlacementEntitySnapshot> BuildCurrentEntities()
        {
            var result = new Dictionary<InteractableObject, PlacementEntitySnapshot>(
                PlacementObjectComparer.Instance);
            var manager = PlacementInterop.FindShelfManager();
            for (var kind = 0; kind < PlacementApi.KindCount; kind++)
            {
                var list = PlacementInterop.GetList(manager, kind);
                for (var i = 0; list != null && i < list.Count; i++)
                {
                    if (list[i] is not InteractableObject obj || obj == null
                        || !PlacementInterop.HasPlacementIdentity(obj, kind))
                    {
                        continue;
                    }

                    result[obj] = PlacementEntityState.Capture(obj);
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
        }

        private void OnPlacementSceneLoaded(Scene _, LoadSceneMode __)
        {
            // A scene load is either the end of this session (CoopCore shuts the session down for
            // any non-Title load) or the title screen. Nothing here may touch the game's managers:
            // `sceneLoaded` fires while the incoming shop's Awake/Start have not all run, so the
            // game's CSingleton getters (ShelfManager.GetCardShelfList, the RestockManager list
            // getters, ...) cannot resolve the scene object and instead fabricate an empty
            // DontDestroyOnLoad copy that shadows the real manager for the rest of the run. A host
            // that loaded a world while hosting hit exactly this: the shadow RestockManager has no
            // prefabs or spawn locations, so every delivery spawn threw and no restock box could
            // ever appear for either player. There is also no baseline to re-send here: the session
            // is already gone, and a joiner gets its baseline from OnFullyJoined.
            if (_shutdown)
            {
                // Unity dispatches the invocation list captured before the session shut down, so
                // this handler can still run after Shutdown. Do nothing in that case.
                return;
            }

            _placementHold?.Reset();
            // Placement ids restart with the scene, so every frozen manifest is stale; a post-reload
            // baseline falls back to descriptors rather than binding a reused id.
            _transferManifests.Clear();
            _known.Clear();
            _warehouseShelfInteraction?.Reset();
        }

        /// <summary>Only pose/state hooks here. Creation, removal and identity are installed by the
        /// GameObjectIdentity module.</summary>
        private void InstallPlacementMutationPatches()
        {
            PatchPlacementMutation(typeof(InteractableObject), "BoxUpObject");
            PatchPlacementMutation(typeof(InteractableObject), "PlaceMovedObject");
        }

        private void PatchPlacementMutation(Type target, string method)
        {
            var original = AccessTools.Method(target, method);
            if (original == null)
            {
                CoopPlugin.Log.LogWarning("placement host state patch target missing: "
                    + target.Name + "." + method);
                return;
            }

            Harmony.Patch(original, postfix: new HarmonyMethod(
                typeof(WorldHostBehaviour), nameof(ObjectMutationPostfix)));
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
            WarehouseShelfInteraction.SetStoredBoxVisuals(__instance,
                !__instance.GetIsBoxedUp());
            host.NotifyObjectChanged(__instance);
        }
    }
}
