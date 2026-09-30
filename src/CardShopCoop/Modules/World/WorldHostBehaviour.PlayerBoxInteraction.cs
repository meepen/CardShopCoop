using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Modules.Presence;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Modules.World
{
    public sealed partial class WorldHostBehaviour
    {
        private void InstallPlayerBoxInteractionPatches()
        {
            _harmony.CreateClassProcessor(typeof(PlayerBoxPickupPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(PlayerBoxThrowPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(PlayerBoxDropPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(PlayerBoxPlacementPatch)).Patch();
        }

        /// <summary>Host: subscribes to remote-avatar spawns so a held box announced before the
        /// holder's avatar exists can move onto the skeleton.</summary>
        private void InstallPlayerBoxPresenceHook()
            => PresenceApi.RemoteAvatarsChanged += OnRemoteAvatarsChanged;

        private void UninstallPlayerBoxPresenceHook()
            => PresenceApi.RemoteAvatarsChanged -= OnRemoteAvatarsChanged;

        /// <summary>Host baseline hook: appends the current carried boxes after the box
        /// descriptors, so a joiner attaches them to the holders' avatars.</summary>
        internal void AppendPlayerBoxHoldBaseline(Action<INetMessage> append)
            => _playerBoxInteraction?.AppendBaselineHolds(append);

        /// <summary>Presence raised that a remote avatar spawned: re-anchor any box currently
        /// mirrored as carried by that player.</summary>
        private void OnRemoteAvatarsChanged() => _playerBoxInteraction?.ReattachRemoteHolds();

        internal void ResetPlayerBoxInteractionState()
        {
            _playerBoxInteraction?.Reset();
        }

        /// <summary>A player left: release any box this host still mirrors as held by them, or the
        /// hold outlives the avatar and ghosts the box on every remaining observer.</summary>
        [OnClientDisconnected]
        private void ReleasePlayerBoxHolds(PeerConnection connection, DisconnectInfo info)
        {
            if (connection != null)
            {
                _playerBoxInteraction?.ReleaseRemoteHoldsForConnection(connection.Id);
            }
        }

        [MessageHandler(typeof(PlayerBoxPickupRequestMessage))]
        private void HandlePlayerBoxPickup(MessageContext context, PlayerBoxPickupRequestMessage message)
        {
            if (_context.InGame() && IsJoinPhaseSender(context))
            {
                ExecuteWorldCommand(context, message, () =>
                {
                    // The requester is the holder; record it so this host and every other observer
                    // can attach the box to that player's avatar.
                    message.HolderConnectionId = context.ConnectionId;
                    if (!ApplyPlayerBoxAction(message))
                    {
                        RefreshRejectedBoxAction(context, message.BoxNetworkId);
                        return false;
                    }
                    BroadcastWorld(new PlayerBoxPickupMessage
                    {
                        PredictionId = message.PredictionId,
                        BoxNetworkId = message.BoxNetworkId,
                        HeldPosition = message.HeldPosition,
                        HeldRotation = message.HeldRotation,
                        HolderConnectionId = message.HolderConnectionId,
                    });
                    return true;
                });
            }
            else
            {
                RejectWorldIntent(context, message);
            }
        }

        [MessageHandler(typeof(PlayerBoxPlacementRequestMessage))]
        private void HandlePlayerBoxPlacement(MessageContext context,
            PlayerBoxPlacementRequestMessage message)
        {
            if (_context.InGame() && IsJoinPhaseSender(context))
            {
                ExecuteWorldCommand(context, message, () =>
                {
                    if (!ApplyPlayerBoxAction(message))
                    {
                        RefreshRejectedBoxAction(context, message.BoxNetworkId);
                        return false;
                    }
                    BroadcastWorld(new PlayerBoxPlacementMessage
                    {
                        PredictionId = message.PredictionId,
                        BoxNetworkId = message.BoxNetworkId,
                        Position = message.Position,
                        Rotation = message.Rotation,
                    });
                    return true;
                });
            }
            else
            {
                RejectWorldIntent(context, message);
            }
        }

        [MessageHandler(typeof(PlayerBoxThrowRequestMessage))]
        private void HandlePlayerBoxThrow(MessageContext context, PlayerBoxThrowRequestMessage message)
        {
            if (_context.InGame() && IsJoinPhaseSender(context))
            {
                ExecuteWorldCommand(context, message, () =>
                {
                    if (!ApplyPlayerBoxAction(message))
                    {
                        RefreshRejectedBoxAction(context, message.BoxNetworkId);
                        return false;
                    }
                    BroadcastWorld(new PlayerBoxThrowMessage
                    {
                        PredictionId = message.PredictionId,
                        BoxNetworkId = message.BoxNetworkId,
                        Position = message.Position,
                        Rotation = message.Rotation,
                        Velocity = message.Velocity,
                        AngularVelocity = message.AngularVelocity,
                    });
                    return true;
                });
            }
            else
            {
                RejectWorldIntent(context, message);
            }
        }

        private PlayerBoxInteraction.LocalAction CapturePlayerBoxAction(InteractablePackagingBox box,
            bool isPlayer, bool alignBody)
        {
            return _playerBoxInteraction == null
                ? default
                : _playerBoxInteraction.CaptureLocalAction(box, isPlayer, alignBody);
        }

        private void PublishPlayerBoxPickup(InteractablePackagingBox box,
            PlayerBoxInteraction.LocalAction action) => _playerBoxInteraction?.PublishPickup(box, action);

        private void PublishPlayerBoxPlacement(InteractablePackagingBox box,
            PlayerBoxInteraction.LocalAction action) => _playerBoxInteraction?.PublishPlacement(box, action);

        private void PublishPlayerBoxThrow(InteractablePackagingBox box,
            PlayerBoxInteraction.LocalAction action) => _playerBoxInteraction?.PublishThrow(box, action);

        private bool ApplyPlayerBoxAction(PlayerBoxInteractionMessage message)
        {
            return _playerBoxInteraction != null && _playerBoxInteraction.ApplyIncoming(message);
        }

        /// <summary>Answers a rejected player-box intent with the box's authoritative state, sent
        /// to the requester before the generic rollback. Without it the client's undo would invert
        /// a snapshot authority may already have moved past - the box stored on a shelf or held by
        /// someone else - and leave a replica in the wrong place.</summary>
        private void RefreshRejectedBoxAction(MessageContext context, Guid boxNetworkId)
        {
            if (context?.Connection == null || boxNetworkId == Guid.Empty
                || _boxNetworkInteraction == null
                || !_boxNetworkInteraction.TryGetBox(boxNetworkId, out var box) || box == null)
            {
                // Unknown or destroyed id: there is no authoritative state to send.
                return;
            }

            if (PlayerBoxInteraction.IsBeingPlaced(box))
            {
                // The box is in someone's placement preview; that move publishes its own
                // authoritative result when it settles.
                return;
            }

            if (_boxNetworkInteraction.IsStored(boxNetworkId)
                && box is InteractablePackagingBox_Item item
                && item.GetBoxStoredCompartment() is ShelfCompartment compartment
                && compartment.GetWarehouseShelf() != null)
            {
                // The box is on a warehouse shelf: republish its location as a store so the
                // client takes it out of the hand through its own store path.
                SendWorldTo(context.Connection.Id, new WarehouseDeltaMessage
                {
                    IsStore = true,
                    ShelfIndex = compartment.GetWarehouseIndex(),
                    CompartmentIndex = compartment.GetIndex(),
                    BoxNetworkId = boxNetworkId,
                    ItemType = item.m_ItemCompartment == null
                        ? EItemType.None : item.m_ItemCompartment.GetItemType(),
                    Amount = item.m_ItemCompartment == null
                        ? 0 : item.m_ItemCompartment.GetItemCount(),
                    IsBig = item.m_IsBigBox,
                });
                return;
            }

            if (_playerBoxInteraction != null
                && ReferenceEquals(_playerBoxInteraction.LocalHeldBox, box))
            {
                SendWorldTo(context.Connection.Id, new PlayerBoxPickupMessage
                {
                    BoxNetworkId = boxNetworkId,
                    HeldPosition = box.transform.position,
                    HeldRotation = box.transform.rotation,
                    HolderConnectionId = 0,
                });
                return;
            }

            if (_playerBoxInteraction != null
                && _playerBoxInteraction.TryGetRemoteHolder(boxNetworkId, out var holder))
            {
                SendWorldTo(context.Connection.Id, new PlayerBoxPickupMessage
                {
                    BoxNetworkId = boxNetworkId,
                    HeldPosition = box.transform.position,
                    HeldRotation = box.transform.rotation,
                    HolderConnectionId = holder,
                });
                return;
            }

            // Any other state (loose or placed) is left to the requester's own undo: the box
            // action undos have no authority-changed guard, so a placement refresh here could
            // itself be clobbered by the rollback that follows.
        }

        [HarmonyPatch(typeof(InteractablePackagingBox), "StartHoldBox")]
        private static class PlayerBoxPickupPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractablePackagingBox __instance, bool isPlayer,
                out PlayerBoxInteraction.LocalAction __state)
            {
                // Capture only: the game decides whether a hold is valid (for example while the
                // box is in its placement preview) and owns the mutation.
                __state = _instance == null ? default
                    : _instance.CapturePlayerBoxAction(__instance, isPlayer, false);
            }

            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox __instance,
                PlayerBoxInteraction.LocalAction __state)
            {
                _instance?.PublishPlayerBoxPickup(__instance, __state);
            }
        }

        [HarmonyPatch(typeof(InteractablePackagingBox), "ThrowBox")]
        private static class PlayerBoxThrowPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractablePackagingBox __instance, bool isPlayer,
                out PlayerBoxInteraction.LocalAction __state)
            {
                // A box just taken or boxed up is still lerping into the hand, and ThrowBox does
                // not stop that lerp: the thrown box kept being dragged to the hand until the lerp
                // finished, which is what made the first throw after a pick-up look refused. Throw
                // from where the box actually is.
                if (isPlayer)
                {
                    __instance?.StopLerpToTransform();
                }

                // Capture only: the game decides whether a throw is valid and owns the mutation.
                __state = _instance == null ? default
                    : _instance.CapturePlayerBoxAction(__instance, isPlayer, true);
            }

            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox __instance,
                PlayerBoxInteraction.LocalAction __state)
            {
                _instance?.PublishPlayerBoxThrow(__instance, __state);
            }
        }

        /// <summary>The local player dropped the box for the game's placement preview. That
        /// release must stop the hold lerp for the same reason a throw does: otherwise the box is
        /// dragged back toward the hand while the moving-object logic aims it.</summary>
        [HarmonyPatch(typeof(InteractablePackagingBox), "DropBox")]
        private static class PlayerBoxDropPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractablePackagingBox __instance, bool isPlayer)
            {
                if (isPlayer)
                {
                    __instance?.StopLerpToTransform();
                }
            }
        }

        [HarmonyPatch(typeof(InteractableObject), "PlaceMovedObject")]
        private static class PlayerBoxPlacementPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractableObject __instance,
                out PlayerBoxInteraction.LocalAction __state)
            {
                // A packaging box finishes its placement here, not at DropBox: DropBox only drops
                // it for aiming in moving mode. Only the local player calls PlaceMovedObject on a
                // box, and CapturePlayerBoxAction ignores unregistered boxes.
                __state = _instance != null && __instance is InteractablePackagingBox box
                    && PlacementInterop.ReadMoveValidity(box)
                    ? _instance.CapturePlayerBoxAction(box, true, false)
                    : default;
            }

            [HarmonyPostfix]
            private static void Postfix(InteractableObject __instance,
                PlayerBoxInteraction.LocalAction __state)
            {
                if (__instance is InteractablePackagingBox box)
                {
                    _instance?.PublishPlayerBoxPlacement(box, __state);
                }
            }
        }
    }

    [NetworkMessage]
    public class PlayerBoxPickupMessage : PlayerBoxInteractionMessage
    {
        public Vector3 HeldPosition;
        public Quaternion HeldRotation;
        /// <summary>Connection id of the player now holding the box, in the sender's connection
        /// space (0 = the host). Observers use it to attach the box to that player's avatar.</summary>
        public int HolderConnectionId;
    }

    [NetworkMessage]
    public sealed class PlayerBoxPickupRequestMessage : PlayerBoxPickupMessage
    {
    }

    [NetworkMessage]
    public class PlayerBoxPlacementMessage : PlayerBoxInteractionMessage
    {
        public Vector3 Position;
        public Quaternion Rotation;
    }

    [NetworkMessage]
    public sealed class PlayerBoxPlacementRequestMessage : PlayerBoxPlacementMessage
    {
    }

    [NetworkMessage]
    public class PlayerBoxThrowMessage : PlayerBoxInteractionMessage
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public Vector3 AngularVelocity;
    }

    [NetworkMessage]
    public sealed class PlayerBoxThrowRequestMessage : PlayerBoxThrowMessage
    {
    }

    /// <summary>Every player-box action addresses exactly one host-assigned box network ID.</summary>
    public abstract class PlayerBoxInteractionMessage : WorldMessage
    {
        public Guid BoxNetworkId;
    }

    /// <summary>
    /// Applies player pickup/place/throw actions to a box already registered by
    /// <see cref="BoxNetworkInteraction"/>. It deliberately has no identity fallback: an
    /// unannounced box cannot produce an action.
    /// </summary>
    internal sealed class PlayerBoxInteraction
    {
        private const float MaxCoordinate = 1000f;
        private const float MaxVelocity = 1000f;

        // Tuned placement of the real carried box on the holder's avatar carry anchor. The
        // rotation yaws left 90 then pitches forward 90; the offset is applied in that rotated
        // (box-local) frame, so it reads as a shift along the box's own left.
        private static readonly Vector3 RemoteHoldLocalOffset = new Vector3(-0.12f, 0f, 0f);
        private static readonly Quaternion RemoteHoldLocalRotation =
            Quaternion.Euler(0f, -90f, 0f) * Quaternion.Euler(90f, 0f, 0f);
        private readonly Dictionary<Guid, Transform> _remoteHoldAnchors = new();
        private readonly HashSet<Guid> _remoteHeldBoxIds = new();
        // The holder a mirrored remote hold belongs to (box id -> holder connection id). A box held
        // by a departing connection is released here; without it the mirrored hold would survive the
        // disconnect and ghost the box on every observer. Cleared with the anchor it mirrors.
        private readonly Dictionary<Guid, int> _remoteHolders = new();
        private readonly PropertyInfo _rigidbodyVelocity = typeof(Rigidbody).GetProperty("velocity");
        private readonly PropertyInfo _rigidbodyLinearVelocity = typeof(Rigidbody).GetProperty("linearVelocity");
        private readonly BoxNetworkInteraction _boxes;
        private static readonly FieldInfo CurrentHoldingBoxField =
            AccessTools.Field(typeof(InteractionPlayerController), "m_CurrentHoldingBox");
        private readonly Action<INetMessage> _broadcast;
        private readonly Action<int, INetMessage> _send;
        private readonly bool _host;
        private bool _applyingPrediction;
        private int _coveredHold;

        /// <summary>The packaging box this peer is carrying, straight from the game's own hold
        /// state. This is the single source of truth for the local hand; the network id is derived
        /// from it through <see cref="BoxNetworkInteraction.TryGetId"/>.</summary>
        internal InteractablePackagingBox LocalHeldBox
        {
            get
            {
                var controller = SceneRef<InteractionPlayerController>.Get();
                return controller == null
                    ? null
                    : CurrentHoldingBoxField?.GetValue(controller) as InteractablePackagingBox;
            }
        }

        /// <summary>The network id of the box this peer is carrying (<see cref="Guid.Empty"/> = none).</summary>
        internal Guid LocalHeldBoxId
            => _boxes.TryGetId(LocalHeldBox, out var id) ? id : Guid.Empty;

        internal struct LocalAction
        {
            public bool Send;
            public Guid BoxNetworkId;
            public Action Undo;
        }

        internal PlayerBoxInteraction(BoxNetworkInteraction boxes, Action<INetMessage> broadcast)
        {
            _boxes = boxes ?? throw new ArgumentNullException(nameof(boxes));
            _broadcast = broadcast ?? throw new ArgumentNullException(nameof(broadcast));
            _host = true;
        }

        internal PlayerBoxInteraction(BoxNetworkInteraction boxes, Action<int, INetMessage> send)
        {
            _boxes = boxes ?? throw new ArgumentNullException(nameof(boxes));
            _send = send ?? throw new ArgumentNullException(nameof(send));
        }

        internal void Reset()
        {
            foreach (var pair in _remoteHoldAnchors)
            {
                if (_boxes.TryGetBox(pair.Key, out var box) && box != null
                    && ReferenceEquals(box.transform.parent, pair.Value))
                {
                    box.DropBox(false);
                }

                var anchor = pair.Value;
                if (anchor != null)
                {
                    UnityEngine.Object.Destroy(anchor.gameObject);
                }
            }

            _remoteHoldAnchors.Clear();
            _remoteHeldBoxIds.Clear();
            _remoteHolders.Clear();
            _coveredHold = 0;
        }

        internal LocalAction CaptureLocalAction(InteractablePackagingBox box, bool isPlayer,
            bool alignBody)
        {
            if (_applyingPrediction || PredictionApi.IsReconciling)
            {
                // A reconcile drives holds from network state (a prediction rollback or a
                // superseded replay), never from a fresh local pickup request.
                return default;
            }

            if (!isPlayer || box == null || !_boxes.TryGetId(box, out var id))
            {
                if (isPlayer && box != null)
                {
                    CoopPlugin.Log.LogWarning("[box-id] ignored action for unregistered box "
                        + box.name + ".");
                }

                return default;
            }

            if (alignBody)
            {
                AlignBodyToVisual(box);
            }

            // Capture the pre-mutation state for rollback. The intent itself is built and sent
            // after the game's own method runs, so it carries the real hold/throw pose and the
            // physics-computed throw velocity instead of the pre-action values.
            var priorParent = box.transform.parent;
            var priorPosition = box.transform.position;
            var priorRotation = box.transform.rotation;
            var priorVelocity = ReadVelocity(box.m_Rigidbody);
            var priorAngularVelocity = box.m_Rigidbody == null
                ? Vector3.zero : box.m_Rigidbody.angularVelocity;

            return new LocalAction
            {
                Send = true,
                BoxNetworkId = id,
                Undo = () => RestorePredictedLocal(box, priorParent, priorPosition,
                    priorRotation, priorVelocity, priorAngularVelocity),
            };
        }

        private void ApplyPredictedLocal(InteractablePackagingBox box,
            PlayerBoxInteractionMessage message)
        {
            if (IsBeingPlaced(box))
            {
                // The local player has the box in the game's placement preview. Replaying a
                // predicted pickup/drop/throw on top of the running move state machine is what
                // strands the box (physics on under the move lerp, Ignore Raycast layer kept).
                return;
            }

            _applyingPrediction = true;
            try
            {
                if (message is PlayerBoxPickupMessage)
                {
                    TakeIntoLocalHand(box);
                }
                else if (message is PlayerBoxPlacementMessage)
                    box.DropBox(true);
                else if (message is PlayerBoxThrowMessage)
                    box.ThrowBox(true);
                else
                    throw new InvalidOperationException("Unsupported predicted player-box action.");
            }
            finally
            {
                _applyingPrediction = false;
            }
        }

        /// <summary>Puts <paramref name="box"/> into this player's hand. A player can only ever
        /// carry one box, so a network-driven hold that arrives while another box is still held
        /// releases the current box first instead of leaving it orphaned in the hand (which made
        /// the old box undroppable and stacked two boxes on the same hand pose).</summary>
        internal void TakeIntoLocalHand(InteractablePackagingBox box)
        {
            if (box == null)
            {
                return;
            }

            // A hold that crossed this player's newer local throw/drop on the wire must not pull
            // the box back into the hand: that is what made the first throw after a take or box-up
            // look refused, with the next attempt then working. The pending move owns the box until
            // the host resolves it; its echo or rollback settles where the box ends up. Reconciles
            // are exempt: they replay pickups in order to restore the layered state.
            if (!PredictionApi.IsReconciling
                && _boxes.TryGetId(box, out var moveId) && _boxes.HasPendingLocalMove(moveId))
            {
                CoopPlugin.Log.LogInfo("[box-id] hold for id=" + moveId
                    + " crossed a pending local move; leaving the box with the move.");
                return;
            }

            var controller = SceneRef<InteractionPlayerController>.Get();
            if (controller == null)
            {
                return;
            }

            ApplyLocalHold(box);
        }

        /// <summary>Puts <paramref name="box"/> into the hand as part of an action that already owns
        /// a prediction (a warehouse or container take) or that is authoritative. The hold it
        /// produces is forwarded to the host without creating a second prediction, so one physical
        /// action maps to exactly one prediction.</summary>
        internal void TakeCoveredIntoLocalHand(InteractablePackagingBox box)
        {
            BeginCoveredHold();
            try
            {
                TakeIntoLocalHand(box);
            }
            finally
            {
                EndCoveredHold();
            }
        }

        /// <summary>Marks the holds produced from now until <see cref="EndCoveredHold"/> as
        /// belonging to an enclosing prediction, so the box channel forwards them without
        /// creating a second prediction. Used around a vanilla game call that owns the action
        /// (a warehouse take), whose own <c>StartHoldBox</c> must not become a separate intent.
        /// The caller must pair this with <see cref="EndCoveredHold"/> in a finally.</summary>
        internal void BeginCoveredHold()
        {
            _coveredHold++;
        }

        internal void EndCoveredHold()
        {
            if (_coveredHold > 0)
            {
                _coveredHold--;
            }
        }

        private void ApplyLocalHold(InteractablePackagingBox box)
        {
            if (box == null || IsBeingPlaced(box))
            {
                return;
            }

            var controller = SceneRef<InteractionPlayerController>.Get();
            if (controller != null)
            {
                // A player holds one box. Enforce it here (the single point every hold goes through)
                // by releasing any box already in the hand through the game's own DropBox, so its
                // m_IsBeingHold and the controller's current box stay consistent.
                ReleaseConflictingLocalHold(box);
                box.StartHoldBox(true, controller.m_HoldItemPos);
            }
        }

        /// <summary>Releases whatever box the controller currently holds unless it is
        /// <paramref name="keep"/>, so only one box is ever parented to the hand pose.</summary>
        private void ReleaseConflictingLocalHold(InteractablePackagingBox keep)
        {
            var controller = SceneRef<InteractionPlayerController>.Get();
            var current = LocalHeldBox;

            if (current == null || ReferenceEquals(current, keep))
            {
                return;
            }

            var hasId = _boxes.TryGetId(current, out var conflictingId);

            // The box may still be lerping toward the hand; stop that first or it keeps following
            // the hand pose as a visual-only ghost after the hold is dropped.
            current.StopLerpToTransform();
            if (hasId)
            {
                PublishForcedDrop(current, conflictingId, keep);
                ReleaseRemoteHold(conflictingId);
            }

            current.DropBox(false);
            controller.OnExitHoldBoxMode();
        }

        /// <summary>Tells the host (and observers) that <paramref name="box"/> is no longer held.
        /// The drop is authoritative once the host echoes it; a rejection restores the old hold
        /// and, because <paramref name="replacement"/> now owns the hand, releases it so the
        /// one-box-per-hand invariant survives a refused forced drop.</summary>
        private void PublishForcedDrop(InteractablePackagingBox box, Guid boxNetworkId,
            InteractablePackagingBox replacement)
        {
            var placement = _host
                ? (PlayerBoxPlacementMessage)new PlayerBoxPlacementMessage()
                : new PlayerBoxPlacementRequestMessage();
            placement.BoxNetworkId = boxNetworkId;
            placement.Position = box.transform.position;
            placement.Rotation = box.transform.rotation;
            if (_host)
            {
                _broadcast(placement);
                return;
            }

            if (_coveredHold > 0 || PredictionApi.IsApplying || PredictionApi.IsReconciling)
            {
                // The drop is part of an enclosing prediction (a warehouse/container take that
                // replaced the held box) or an authoritative apply. Forward it plainly so the
                // take stays one prediction.
                WorldClientBehaviour.SendClientIntent(placement);
                return;
            }

            WorldPrediction.Predict(WorldPrediction.BoxesScope, placement,
                () => ApplyPredictedLocal(box, placement), () => RestoreForcedHold(box),
                () => ReleaseRefusedReplacement(replacement));
        }

        /// <summary>Undo of a rejected forced drop: put the old box back into the local hand, unless
        /// another player has since taken it or it was stored authoritatively. Those states are
        /// newer than the drop we are reverting, and re-holding would claim an owned box again.</summary>
        private void RestoreForcedHold(InteractablePackagingBox box)
        {
            if (box == null)
            {
                return;
            }

            if (_boxes.TryGetId(box, out var id)
                && (HasMirroredHold(box, id) || _boxes.IsStored(id)))
            {
                return;
            }

            ApplyLocalHold(box);
        }

        /// <summary>A host rejection of the forced drop means the old box should be held again; the
        /// reconcile undo already did that, so this only relinquishes the replacement box that took
        /// the hand. It must not exit hold mode, or it would clear the restored hold and orphan the
        /// old box in the hand.</summary>
        private void ReleaseRefusedReplacement(InteractablePackagingBox replacement)
        {
            if (replacement == null)
            {
                return;
            }

            if (IsBeingPlaced(replacement))
            {
                return;
            }

            replacement.StopLerpToTransform();
            replacement.DropBox(false);
        }

        private void RestorePredictedLocal(InteractablePackagingBox box, Transform parent,
            Vector3 position, Quaternion rotation, Vector3 velocity, Vector3 angularVelocity)
        {
            // A destroyed box has nothing to restore, and the undo loop in Reconcile does not catch
            // exceptions, so a missing subject must not fall through to a DropBox dereference.
            if (box == null)
            {
                return;
            }

            if (IsBeingPlaced(box))
            {
                // Undoing a superseded box action would drop the box out of the game's placement
                // preview, so leave the move (and its transform) alone. The move is the newer,
                // locally-owned intent and it will send its own authoritative result.
                return;
            }

            // Another player's hold, or an authoritative store, is newer than the rejected
            // action. Restoring the local snapshot would pull the box out of their hands or off
            // the shelf, so the newer authority owns it and the snapshot is dropped.
            if (_boxes.TryGetId(box, out var id)
                && (HasMirroredHold(box, id) || _boxes.IsStored(id)))
            {
                return;
            }

            _applyingPrediction = true;
            try
            {
                box.DropBox(false);
            }
            finally
            {
                _applyingPrediction = false;
            }

            box.transform.SetParent(parent, true);
            box.transform.SetPositionAndRotation(position, rotation);
            if (box.m_Rigidbody != null)
            {
                box.m_Rigidbody.position = position;
                box.m_Rigidbody.rotation = rotation;
                WriteVelocity(box.m_Rigidbody, velocity);
                box.m_Rigidbody.angularVelocity = angularVelocity;
            }
        }

        internal void PublishPickup(InteractablePackagingBox box, LocalAction action)
        {
            if (!action.Send || box == null)
            {
                return;
            }

            // Taking the box locally supersedes any remote hold this peer was mirroring for it.
            ReleaseRemoteHold(action.BoxNetworkId);
            var pickup = _host
                ? (PlayerBoxPickupMessage)new PlayerBoxPickupMessage()
                : new PlayerBoxPickupRequestMessage();
            ReadHoldPose(box, out pickup.HeldPosition, out pickup.HeldRotation);
            pickup.BoxNetworkId = action.BoxNetworkId;
            // The host's own pickup has no remote avatar; a client's request is stamped by the host.
            pickup.HolderConnectionId = 0;
            PublishAction(box, action, pickup);
        }

        /// <summary>Announces a host hold that was not covered by the normal pickup capture: a
        /// box-up hold the game applied before the package had a network id, or a warehouse take
        /// whose pickup had to be re-announced after its shelf-removal delta (the live backend
        /// starts the hold before the game removes the box from the compartment). The host is
        /// already holding the box locally; observers just need the pickup to attach it to the
        /// host's avatar.</summary>
        internal void PublishHostHeld(InteractablePackagingBox box, Guid boxNetworkId)
        {
            if (!_host || box == null || boxNetworkId == Guid.Empty)
            {
                return;
            }

            var pickup = new PlayerBoxPickupMessage { BoxNetworkId = boxNetworkId };
            ReadHoldPose(box, out pickup.HeldPosition, out pickup.HeldRotation);
            pickup.HolderConnectionId = 0;
            _broadcast(pickup);
        }

        /// <summary>Host: a container just handed a box it created to a player (a warehouse take
        /// or an empty-box take), so announce the hold authoritatively. The acting client's own
        /// pickup for that action is a covered forward that races ahead of the box's creation and
        /// is dropped by the host, so without this the host never knows the box is held - its
        /// contents-change validation rejects the holder - and no observer ever attaches the box
        /// to the holder's avatar.</summary>
        internal void AnnounceGrantedHold(Guid boxNetworkId, int holderConnectionId)
        {
            if (!_host)
            {
                return;
            }

            if (boxNetworkId == Guid.Empty
                || !_boxes.TryGetBox(boxNetworkId, out var box) || box == null)
            {
                CoopPlugin.Log.LogWarning("[box-id] granted hold id=" + boxNetworkId
                    + " holder=" + holderConnectionId + " is not bound; not announced.");
                return;
            }

            if (_boxes.IsStored(boxNetworkId) || IsBeingPlaced(box))
            {
                CoopPlugin.Log.LogWarning("[box-id] granted hold for box id=" + boxNetworkId
                    + " name=" + box.name
                    + " could not be announced (stored=" + _boxes.IsStored(boxNetworkId)
                    + ", moving=" + IsBeingPlaced(box) + ").");
                return;
            }

            var pickup = new PlayerBoxPickupMessage
            {
                BoxNetworkId = boxNetworkId,
                HeldPosition = box.transform.position,
                HeldRotation = box.transform.rotation,
                HolderConnectionId = holderConnectionId,
            };
            var applied = ApplyIncoming(pickup);
            CoopPlugin.Log.LogInfo("[box-id] granted hold id=" + boxNetworkId + " name="
                + box.name + " holder=" + holderConnectionId + " applied=" + applied
                + " held=" + _boxes.IsBeingHeld(box) + ".");
            _broadcast(pickup);
        }

        internal void PublishPlacement(InteractablePackagingBox box, LocalAction action)
        {
            if (!action.Send || box == null)
            {
                return;
            }

            var placement = _host
                ? (PlayerBoxPlacementMessage)new PlayerBoxPlacementMessage()
                : new PlayerBoxPlacementRequestMessage();
            placement.Position = box.transform.position;
            placement.Rotation = box.transform.rotation;
            placement.BoxNetworkId = action.BoxNetworkId;
            PublishAction(box, action, placement);
        }

        internal void PublishThrow(InteractablePackagingBox box, LocalAction action)
        {
            if (!action.Send || box == null)
            {
                return;
            }

            var body = box.m_Rigidbody;
            var thrown = _host
                ? (PlayerBoxThrowMessage)new PlayerBoxThrowMessage()
                : new PlayerBoxThrowRequestMessage();
            thrown.Position = body == null ? box.transform.position : body.position;
            thrown.Rotation = body == null ? box.transform.rotation : body.rotation;
            thrown.Velocity = ReadVelocity(body);
            thrown.AngularVelocity = body == null ? Vector3.zero : body.angularVelocity;
            thrown.BoxNetworkId = action.BoxNetworkId;
            PublishAction(box, action, thrown);
        }

        /// <summary>Publishes one player-box action. The host broadcasts the authoritative
        /// result; a client registers the prediction now that the game's own method has already
        /// run, so the sent intent carries the real post-action pose and throw velocity.</summary>
        private void PublishAction(InteractablePackagingBox box, LocalAction action,
            PlayerBoxInteractionMessage message)
        {
            if (_host)
            {
                _broadcast(message);
                return;
            }

            if (_coveredHold > 0 && !PredictionApi.IsApplying && !PredictionApi.IsReconciling)
            {
                // A vanilla container take (a warehouse or empty-box take) is spawning and holding
                // a box the host has not created yet: this pickup names an id the host cannot
                // resolve, so forwarding it only produces a refused intent. The take's own
                // container op is the single forwarded action, and the host announces the granted
                // hold once it creates the box. A drop/throw produced inside the same window is
                // still forwarded below, because the host must release the box it replaces.
                if (message is PlayerBoxPickupMessage)
                {
                    return;
                }
            }

            if (_coveredHold > 0 || PredictionApi.IsApplying || PredictionApi.IsReconciling)
            {
                // This hold belongs to an action that already owns a prediction (a warehouse or
                // container take) or to an authoritative apply. Forward it without creating a
                // second prediction, so one physical action maps to exactly one prediction.
                WorldClientBehaviour.SendClientIntent(message);
                return;
            }

            var predictionId = WorldPrediction.Predict(WorldPrediction.BoxesScope, message,
                () => ApplyPredictedLocal(box, message),
                action.Undo);
            if (message is PlayerBoxPlacementRequestMessage || message is PlayerBoxThrowRequestMessage)
            {
                // A granted hold or creation descriptor that reaches this peer before the host's
                // decision does crossed this move on the wire and describes the box's earlier
                // state; see HasPendingLocalMove. Pickups are not recorded: their own echo is the
                // granted hold and applying it is the point.
                _boxes.NotePendingLocalMove(message.BoxNetworkId, predictionId);
            }
        }

        private static void ReadHoldPose(InteractablePackagingBox box, out Vector3 position,
            out Quaternion rotation)
        {
            var controller = SceneRef<InteractionPlayerController>.Get();
            var hand = controller == null ? null : controller.m_HoldItemPos;
            if (hand != null)
            {
                position = hand.position;
                rotation = hand.rotation;
            }
            else
            {
                position = box.transform.position;
                rotation = box.transform.rotation;
            }
        }

        /// <summary>Publishes the moving world pose of this peer's locally held box. The initial
        /// pickup remains reliable; these updates only keep the already-authoritative box in the
        /// remote player's hands while they walk.</summary>
        internal bool ApplyIncoming(PlayerBoxInteractionMessage message)
        {
            if (_host && !Validate(message))
            {
                return false;
            }
            if (!_boxes.TryGetBox(message.BoxNetworkId, out var box))
            {
                // A box lifecycle race is normal: the box can be stored, removed, or destroyed
                // while a hold/drop/throw for it is still in flight, so an action can name an id
                // this peer has already forgotten. On the host that is a stale intent and is
                // rejected (rolling the requester back); on an observer it is a no-op. Never fatal:
                // throwing here killed the reliable handler and disconnected the peer.
                CoopPlugin.Log.LogWarning("[box-id] ignoring player-box action for unknown box id="
                    + message.BoxNetworkId + " (" + message.GetType().Name + ").");
                return false;
            }

            if (IsBeingPlaced(box))
            {
                // This peer already owns the box in the game's placement preview. A networked
                // hold, drop, or throw on top of that fight the move state machine and strand the
                // box, and the move will publish its own authoritative result when it finishes.
                // A terminal action still means nobody holds it, so retire any mirrored hold.
                if (message is PlayerBoxPlacementMessage || message is PlayerBoxThrowMessage)
                {
                    ReleaseRemoteHold(message.BoxNetworkId);
                }

                return false;
            }

            if (message is PlayerBoxPickupMessage pickup)
            {
                if (_boxes.IsStored(pickup.BoxNetworkId))
                {
                    // The box is on a warehouse shelf on this peer. A hold that names a holder is
                    // stale - it raced the store that put the box there (the guest's pickup echo
                    // can arrive after its own optimistic store) - and re-attaching it would leave
                    // this peer holding a box the host already sees stored. A real take clears the
                    // stored flag before any hold follows.
                    ReleaseLocalHold(pickup.BoxNetworkId);
                    return false;
                }

                if (!_host && pickup.HolderConnectionId == CoopCore.LocalConnectionId)
                {
                    // The host named us the holder: either the echo of our own pickup, or a
                    // server-driven hold such as the furniture box-up we requested. Take the box
                    // into our own hand; attaching it to a remote anchor would strand it.
                    ApplyPredictedLocal(box, pickup);
                }
                else
                {
                    // Someone else is now the holder. If we still think we hold this box (the
                    // host took it out of our hands), release it first; otherwise the controller
                    // keeps hold mode and the box is misattributed to us.
                    if (LocalHeldBoxId == pickup.BoxNetworkId)
                    {
                        box.StopLerpToTransform();
                        box.DropBox(false);
                        SceneRef<InteractionPlayerController>.Get()?.OnExitHoldBoxMode();
                    }

                    ApplyPickup(box, pickup.BoxNetworkId, pickup.HeldPosition, pickup.HeldRotation,
                        pickup.HolderConnectionId);
                }
            }
            else if (message is PlayerBoxPlacementMessage placement)
            {
                ApplyPlacement(box, placement);
            }
            else if (message is PlayerBoxThrowMessage thrown)
            {
                ApplyThrow(box, thrown);
            }
            else
            {
                throw new InvalidOperationException("Unsupported player-box action "
                    + message.GetType().FullName + ".");
            }

            return true;
        }

        private void ApplyPickup(InteractablePackagingBox box, Guid boxNetworkId,
            Vector3 heldPosition, Quaternion heldRotation, int holderConnectionId)
        {
            var anchor = GetRemoteHoldAnchor(boxNetworkId, heldPosition, heldRotation);

            // Re-resolve the holder's carry anchor on every pickup. The id can remain in
            // _remoteHeldBoxIds from an earlier hold (a warehouse store does not release it), so
            // attaching only on the first add left a re-picked box riding a stale standalone
            // anchor at the old streamed hand pose instead of the avatar's hands.
            var carried = PresenceApi.TryGetRemoteCarryAnchor(holderConnectionId, out var carry)
                && carry != null;
            if (carried)
            {
                anchor.SetParent(carry, false);
                anchor.localPosition = Vector3.zero;
                anchor.localRotation = RemoteHoldLocalRotation;
            }

            _remoteHeldBoxIds.Add(boxNetworkId);
            _remoteHolders[boxNetworkId] = holderConnectionId;
            box.StartHoldBox(false, anchor);
            box.StopLerpToTransform();
            box.transform.SetParent(anchor, false);
            // The position offset is applied in the anchor's rotated frame (the box's own frame),
            // so it stays a left shift after the yaw/pitch above.
            box.transform.localPosition = carried ? RemoteHoldLocalOffset : Vector3.zero;
            box.transform.localRotation = Quaternion.identity;
        }

        private void ApplyPlacement(InteractablePackagingBox box,
            PlayerBoxPlacementMessage message)
        {
            box.DropBox(false);
            ReleaseRemoteHold(message.BoxNetworkId);
            ApplyPose(box, message.Position, message.Rotation);
        }

        private void ApplyThrow(InteractablePackagingBox box, PlayerBoxThrowMessage message)
        {
            // Face the box along the thrower's released rotation first so the game's own throw
            // force leaves in the right direction even when the velocity sample is weak; the
            // sender's sampled velocity then overrides it.
            ApplyPose(box, message.Position, message.Rotation);
            box.ThrowBox(false);
            ReleaseRemoteHold(message.BoxNetworkId);
            ApplyPose(box, message.Position, message.Rotation);
            if (box.m_Rigidbody != null)
            {
                WriteVelocity(box.m_Rigidbody, message.Velocity);
                box.m_Rigidbody.angularVelocity = message.AngularVelocity;
                box.m_Rigidbody.WakeUp();
            }
        }

        private Transform GetRemoteHoldAnchor(Guid boxNetworkId, Vector3 position,
            Quaternion rotation)
        {
            if (!_remoteHoldAnchors.TryGetValue(boxNetworkId, out var anchor) || anchor == null)
            {
                anchor = new GameObject("CardShopCoop.RemoteBoxHold." + boxNetworkId).transform;
                _remoteHoldAnchors[boxNetworkId] = anchor;
            }

            anchor.SetPositionAndRotation(position, rotation);
            return anchor;
        }

        /// <summary>Drops the mirrored hold for a box that is no longer carried (for example after
        /// it is stored on a shelf), so a later pickup starts from a clean anchor instead of
        /// reusing the stale one.</summary>
        internal void ReleaseRemoteHoldForBox(Guid boxNetworkId)
        {
            ReleaseRemoteHold(boxNetworkId);
        }

        /// <summary>True while this peer still mirrors <paramref name="box"/> as carried by another
        /// player, i.e. the replica is parented to this peer's hold anchor.</summary>
        internal bool HasMirroredHold(InteractablePackagingBox box, Guid boxNetworkId)
        {
            if (box == null || boxNetworkId == Guid.Empty
                || !_remoteHoldAnchors.TryGetValue(boxNetworkId, out var anchor) || anchor == null)
            {
                return false;
            }

            return ReferenceEquals(box.transform.parent, anchor);
        }

        /// <summary>Detaches a box this peer mirrors as remotely carried, through the game's own
        /// drop path, so the hold anchor no longer owns it. A caller that is about to move the box
        /// somewhere authoritative (a warehouse store) must detach first: releasing the anchor
        /// destroys whatever is still parented to it.</summary>
        internal bool DetachMirroredHold(InteractablePackagingBox box, Guid boxNetworkId)
        {
            if (!HasMirroredHold(box, boxNetworkId))
            {
                return false;
            }

            box.StopLerpToTransform();
            box.DropBox(false);
            return true;
        }

        /// <summary>The game is destroying <paramref name="box"/> locally. If this peer is holding
        /// it, release the hand through the game's own path first, so a box the game tears down
        /// cannot be left parented to the hand as a ghost.</summary>
        internal void ReleaseHeldObject(InteractablePackagingBox box)
        {
            if (box == null || !ReferenceEquals(LocalHeldBox, box))
            {
                return;
            }

            CoopPlugin.Log.LogInfo("[box-id] releasing held box " + box.name
                + " because the game is destroying it.");
            box.StopLerpToTransform();
            box.DropBox(false);
            SceneRef<InteractionPlayerController>.Get()?.OnExitHoldBoxMode();
        }

        /// <summary>Drops this peer's bookkeeping that it is holding the box. Used when a warehouse
        /// store takes it out of the hand, so a late pickup for the same box can't re-hold it. If
        /// the game still has the box in the hand (the store silently refused it), it is released
        /// through the game's own <see cref="InteractablePackagingBox.DropBox"/> path so it cannot
        /// stay parented to the hand as a ghost.</summary>
        internal void ForgetLocalHold(Guid boxNetworkId)
        {
            ReleaseLocalHold(boxNetworkId);
        }

        /// <summary>Releases the box this peer holds, if any, through the game's own path
        /// (<c>DropBox(isPlayer:true)</c> clears both the box's held flag and the controller's
        /// current box). Pass a specific id to only release when it matches.</summary>
        internal void ReleaseLocalHold(Guid boxNetworkId = default)
        {
            var controller = SceneRef<InteractionPlayerController>.Get();
            var current = LocalHeldBox;
            var currentId = _boxes.TryGetId(current, out var known) ? known : Guid.Empty;
            var matches = boxNetworkId == Guid.Empty || currentId == boxNetworkId;
            if (current != null && matches)
            {
                current.StopLerpToTransform();
                current.DropBox(true);
            }
            else if (current == null && matches && controller != null)
            {
                controller.OnExitHoldBoxMode();
            }
        }

        /// <summary>A box this peer modelled as held was destroyed authoritatively. Release the hand
        /// if the game still attributes it to us, so a destroyed box cannot leave a ghost.</summary>
        internal void ForgetDestroyed(Guid boxNetworkId)
        {
            if (boxNetworkId == Guid.Empty)
            {
                return;
            }

            ReleaseRemoteHold(boxNetworkId);
            if (LocalHeldBoxId != boxNetworkId)
            {
                return;
            }

            var current = LocalHeldBox;
            CoopPlugin.Log.LogInfo("[box-id] releasing held box id=" + boxNetworkId
                + " (authoritative destroy).");
            current.StopLerpToTransform();
            current.DropBox(false);
            SceneRef<InteractionPlayerController>.Get()?.OnExitHoldBoxMode();
        }

        /// <summary>Host: a player disconnected. Release every box this host was mirroring as held
        /// by that connection so it does not ghost on the remaining peers, drop it back into the
        /// world through the game's own path, and broadcast the resulting pose so every observer
        /// detaches it. Host-local holds have no remote anchor and connection 0 never disconnects,
        /// so they are not touched.</summary>
        internal void ReleaseRemoteHoldsForConnection(int connectionId)
        {
            if (!_host || connectionId == 0 || _remoteHolders.Count == 0)
            {
                return;
            }

            List<Guid> released = null;
            foreach (var pair in _remoteHolders)
            {
                if (pair.Value == connectionId)
                {
                    (released ??= new List<Guid>()).Add(pair.Key);
                }
            }

            if (released == null)
            {
                return;
            }

            for (var i = 0; i < released.Count; i++)
            {
                var boxNetworkId = released[i];
                if (!_boxes.TryGetBox(boxNetworkId, out var box) || box == null)
                {
                    ReleaseRemoteHold(boxNetworkId);
                    continue;
                }

                // The box is parented to this host's remote-hold anchor. DropBox re-parents it to
                // its original parent and re-enables physics, so the game owns it again; the
                // placement broadcast then tells the observers to detach the same box.
                box.StopLerpToTransform();
                box.DropBox(false);
                ReleaseRemoteHold(boxNetworkId);

                _broadcast(new PlayerBoxPlacementMessage
                {
                    BoxNetworkId = boxNetworkId,
                    Position = box.transform.position,
                    Rotation = box.transform.rotation,
                });
            }
        }

        private void ReleaseRemoteHold(Guid boxNetworkId)
        {
            _remoteHeldBoxIds.Remove(boxNetworkId);
            _remoteHolders.Remove(boxNetworkId);
            if (_remoteHoldAnchors.TryGetValue(boxNetworkId, out var anchor))
            {
                _remoteHoldAnchors.Remove(boxNetworkId);
                if (anchor != null)
                {
                    UnityEngine.Object.Destroy(anchor.gameObject);
                }
            }

        }

        /// <summary>Host: append one pickup per box currently carried, so a peer that joins after
        /// the carry began attaches the box to the holder's avatar instead of leaving it frozen at
        /// the transfer-time pose. Appended after the box descriptors in the same baseline.</summary>
        internal void AppendBaselineHolds(Action<INetMessage> append)
        {
            if (!_host || append == null)
            {
                return;
            }

            var localBox = LocalHeldBox;
            if (localBox != null && _boxes.TryGetId(localBox, out var localId)
                && !_boxes.IsStored(localId))
            {
                append(BuildBaselineHold(localId, localBox.transform.position,
                    localBox.transform.rotation, 0));
            }

            foreach (var pair in _remoteHolders)
            {
                // The box descriptor baseline skips stored boxes; do not announce a hold for one
                // (it would name an id the joiner never materialized).
                if (!_boxes.IsStored(pair.Key)
                    && _boxes.TryGetBox(pair.Key, out var box) && box != null)
                {
                    append(BuildBaselineHold(pair.Key, box.transform.position,
                        box.transform.rotation, pair.Value));
                }
            }
        }

        private static PlayerBoxPickupMessage BuildBaselineHold(Guid boxNetworkId, Vector3 position,
            Quaternion rotation, int holderConnectionId)
            => new()
            {
                BoxNetworkId = boxNetworkId,
                HeldPosition = position,
                HeldRotation = rotation,
                HolderConnectionId = holderConnectionId,
            };

        /// <summary>The connection this peer mirrors as the box's holder, if any.</summary>
        internal bool TryGetRemoteHolder(Guid boxNetworkId, out int holderConnectionId)
            => _remoteHolders.TryGetValue(boxNetworkId, out holderConnectionId);

        /// <summary>True while a vanilla container take is spawning and holding a box (the window
        /// where the host has not created its copy yet).</summary>
        internal bool IsCoveredHold => _coveredHold > 0;

        /// <summary>Re-resolves the avatar carry anchor for every mirrored remote hold. A held box
        /// can be announced (in a join baseline) before this machine has spawned the holder's
        /// avatar, so it initially rides a standalone anchor; this runs when remote avatars change
        /// and moves it onto the freshly spawned avatar.</summary>
        internal void ReattachRemoteHolds()
        {
            if (_remoteHolders.Count == 0)
            {
                return;
            }

            var entries = new List<KeyValuePair<Guid, int>>(_remoteHolders);
            for (var i = 0; i < entries.Count; i++)
            {
                var boxNetworkId = entries[i].Key;
                if (!_boxes.TryGetBox(boxNetworkId, out var box) || box == null
                    || _boxes.IsStored(boxNetworkId) || IsBeingPlaced(box))
                {
                    // A stored box must never be re-held onto an avatar: the store already put it
                    // on a shelf (an avatar respawn or a third join must not lift it back out).
                    continue;
                }

                ApplyPickup(box, boxNetworkId, box.transform.position, box.transform.rotation,
                    entries[i].Value);
            }
        }

        /// <summary>True while this peer has the box in the game's own placement preview
        /// (move-box mode, <c>m_IsMovingObject</c>). The move state machine owns the box then:
        /// the object is on the Ignore Raycast layer with its colliders off and its rigidbody
        /// kinematic, and <c>Update</c> lerps its transform toward the aim target. Applying a
        /// networked hold/drop/throw on top of that re-enables physics and re-parents the box
        /// while the move lerp keeps running, so it jitters in place and can no longer be picked
        /// up. A no-op until the move itself finishes.</summary>
        internal static bool IsBeingPlaced(InteractablePackagingBox box)
            => box != null && box.GetIsMovingObject();

        private static void AlignBodyToVisual(InteractablePackagingBox box)
        {
            if (box.m_Rigidbody == null)
            {
                return;
            }

            box.m_Rigidbody.position = box.transform.position;
            box.m_Rigidbody.rotation = box.transform.rotation;
            box.m_Rigidbody.angularVelocity = Vector3.zero;
        }

        private static void ApplyPose(InteractablePackagingBox box, Vector3 position,
            Quaternion rotation)
        {
            if (box.m_Rigidbody != null)
            {
                box.m_Rigidbody.position = position;
                box.m_Rigidbody.rotation = rotation;
            }

            box.transform.SetPositionAndRotation(position, rotation);
        }

        private Vector3 ReadVelocity(Rigidbody body)
        {
            var property = _rigidbodyLinearVelocity ?? _rigidbodyVelocity;
            return body != null && property?.GetValue(body) is Vector3 velocity ? velocity : Vector3.zero;
        }

        private void WriteVelocity(Rigidbody body, Vector3 velocity)
        {
            var property = _rigidbodyLinearVelocity ?? _rigidbodyVelocity;
            property?.SetValue(body, velocity);
        }

        private static bool Validate(PlayerBoxInteractionMessage message)
        {
            if (message == null || message.BoxNetworkId == Guid.Empty)
            {
                return false;
            }

            return message switch
            {
                PlayerBoxPickupMessage pickup => IsSanePose(pickup.HeldPosition, pickup.HeldRotation),
                PlayerBoxPlacementMessage placement => IsSanePose(placement.Position, placement.Rotation),
                PlayerBoxThrowMessage thrown => IsSanePose(thrown.Position, thrown.Rotation)
                    && IsSaneVector(thrown.Velocity, MaxVelocity)
                    && IsSaneVector(thrown.AngularVelocity, MaxVelocity),
                _ => false,
            };
        }

        private static bool IsSanePose(Vector3 position, Quaternion rotation)
        {
            return IsSaneVector(position, MaxCoordinate) && IsFinite(rotation.x) && IsFinite(rotation.y)
                && IsFinite(rotation.z) && IsFinite(rotation.w);
        }

        private static bool IsSaneVector(Vector3 vector, float maximum)
        {
            return IsFinite(vector.x) && IsFinite(vector.y) && IsFinite(vector.z)
                && Mathf.Abs(vector.x) <= maximum && Mathf.Abs(vector.y) <= maximum
                && Mathf.Abs(vector.z) <= maximum;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    }
}
