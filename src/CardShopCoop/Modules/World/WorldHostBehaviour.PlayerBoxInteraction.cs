using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Modules.Presence;
using CardShopCoop.Net;
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
            _harmony.CreateClassProcessor(typeof(PlayerBoxPlacementPatch)).Patch();
        }

        internal void ResetPlayerBoxInteractionState()
        {
            _playerBoxInteraction?.Reset();
        }

        [MessageHandler(typeof(PlayerBoxPickupRequestMessage))]
        private void HandlePlayerBoxPickup(MessageContext context, PlayerBoxPickupRequestMessage message)
        {
            if (_context.InGame() && IsFullyJoinedSender(context))
            {
                ExecuteWorldCommand(context, message, () =>
                {
                    // The requester is the holder; record it so this host and every other observer
                    // can attach the box to that player's avatar.
                    message.HolderConnectionId = context.ConnectionId;
                    if (!ApplyPlayerBoxAction(message))
                        return false;
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
            if (_context.InGame() && IsFullyJoinedSender(context))
            {
                ExecuteWorldCommand(context, message, () =>
                {
                    if (!ApplyPlayerBoxAction(message))
                        return false;
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
            if (_context.InGame() && IsFullyJoinedSender(context))
            {
                ExecuteWorldCommand(context, message, () =>
                {
                    if (!ApplyPlayerBoxAction(message))
                        return false;
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

        [HarmonyPatch(typeof(InteractablePackagingBox), "StartHoldBox")]
        private static class PlayerBoxPickupPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(InteractablePackagingBox __instance, bool isPlayer,
                out PlayerBoxInteraction.LocalAction __state)
            {
                __state = default;
                if (__instance != null && __instance.GetIsMovingObject())
                {
                    // Never take a box back into hand while it is in the game's placement preview:
                    // hold mode layered on top of move-box mode is what let the throw corrupt it.
                    CoopPlugin.Log.LogInfo("[box-id] ignoring hold on a box that is being placed.");
                    return false;
                }

                __state = _instance == null ? default
                    : _instance.CapturePlayerBoxAction(__instance, isPlayer, false);
                return true;
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
            private static bool Prefix(InteractablePackagingBox __instance, bool isPlayer,
                out PlayerBoxInteraction.LocalAction __state)
            {
                __state = default;
                if (isPlayer && __instance != null && __instance.GetIsMovingObject())
                {
                    // F while the box is in the placement preview: the box is being aimed, not
                    // held. A throw would enable physics under the running move lerp and strand
                    // it, so ignore the input and let placement finish.
                    CoopPlugin.Log.LogInfo("[box-id] ignoring throw on a box that is being placed.");
                    return false;
                }

                __state = _instance == null ? default
                    : _instance.CapturePlayerBoxAction(__instance, isPlayer, true);
                return true;
            }

            [HarmonyPostfix]
            private static void Postfix(InteractablePackagingBox __instance,
                PlayerBoxInteraction.LocalAction __state)
            {
                _instance?.PublishPlayerBoxThrow(__instance, __state);
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
        public long BoxNetworkId;
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
        private readonly Dictionary<long, Transform> _remoteHoldAnchors = new();
        private readonly HashSet<long> _remoteHeldBoxIds = new();
        private readonly PropertyInfo _rigidbodyVelocity = typeof(Rigidbody).GetProperty("velocity");
        private readonly PropertyInfo _rigidbodyLinearVelocity = typeof(Rigidbody).GetProperty("linearVelocity");
        private readonly BoxNetworkInteraction _boxes;
        private readonly Action<INetMessage> _broadcast;
        private readonly Action<int, INetMessage> _send;
        private readonly bool _host;
        private long _localHeldBoxNetworkId;
        private bool _applyingPrediction;

        internal struct LocalAction
        {
            public bool Send;
            public long BoxNetworkId;
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
            _localHeldBoxNetworkId = 0;
        }

        internal LocalAction CaptureLocalAction(InteractablePackagingBox box, bool isPlayer,
            bool alignBody)
        {
            if (_applyingPrediction)
            {
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
                CoopPlugin.Log.LogInfo("[box-id] skipping predicted box action while the box is being placed id="
                    + (message == null ? 0 : message.BoxNetworkId) + ".");
                return;
            }

            _applyingPrediction = true;
            try
            {
                if (message is PlayerBoxPickupMessage pickup)
                {
                    var controller = SceneRef<InteractionPlayerController>.Get();
                    box.StartHoldBox(true, controller.m_HoldItemPos);
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

        private void RestorePredictedLocal(InteractablePackagingBox box, Transform parent,
            Vector3 position, Quaternion rotation, Vector3 velocity, Vector3 angularVelocity)
        {
            if (IsBeingPlaced(box))
            {
                // Undoing a superseded box action would drop the box out of the game's placement
                // preview, so leave the move (and its transform) alone. The move is the newer,
                // locally-owned intent and it will send its own authoritative result.
                CoopPlugin.Log.LogInfo("[box-id] skipping prediction restore while the box is being placed.");
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

            _localHeldBoxNetworkId = action.BoxNetworkId;
            var pickup = _host
                ? (PlayerBoxPickupMessage)new PlayerBoxPickupMessage()
                : new PlayerBoxPickupRequestMessage();
            ReadHoldPose(box, out pickup.HeldPosition, out pickup.HeldRotation);
            pickup.BoxNetworkId = action.BoxNetworkId;
            // The host's own pickup has no remote avatar; a client's request is stamped by the host.
            pickup.HolderConnectionId = 0;
            PublishAction(box, action, pickup);
        }

        /// <summary>Announces a host box-up hold that the game applied before the package had a
        /// network id (so the normal pickup capture was skipped). The host is already holding the
        /// box locally; observers just need the pickup to attach it to the host's avatar.</summary>
        internal void PublishHostHeld(InteractablePackagingBox box, long boxNetworkId)
        {
            if (!_host || box == null || boxNetworkId <= 0)
            {
                return;
            }

            _localHeldBoxNetworkId = boxNetworkId;
            var pickup = new PlayerBoxPickupMessage { BoxNetworkId = boxNetworkId };
            ReadHoldPose(box, out pickup.HeldPosition, out pickup.HeldRotation);
            pickup.HolderConnectionId = 0;
            _broadcast(pickup);
        }

        internal void PublishPlacement(InteractablePackagingBox box, LocalAction action)
        {
            if (!action.Send || box == null)
            {
                return;
            }

            ClearLocalHeldBox(action.BoxNetworkId);
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

            ClearLocalHeldBox(action.BoxNetworkId);
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

            WorldPrediction.Predict(WorldPrediction.BoxesScope, message,
                () => ApplyPredictedLocal(box, message),
                action.Undo,
                applyLocally: false);
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
                throw new InvalidOperationException("Authoritative player-box action references unknown box "
                    + message.BoxNetworkId + ".");

            if (IsBeingPlaced(box))
            {
                // This peer already owns the box in the game's placement preview. A networked
                // hold, drop, or throw on top of that fight the move state machine and strand the
                // box, and the move will publish its own authoritative result when it finishes.
                CoopPlugin.Log.LogInfo("[box-id] ignoring authoritative box action while the box is being placed id="
                    + message.BoxNetworkId + " (" + message.GetType().Name + ").");
                return false;
            }

            if (message is PlayerBoxPickupMessage pickup)
            {
                if (!_host && (_localHeldBoxNetworkId == pickup.BoxNetworkId
                    || pickup.HolderConnectionId == CoopCore.LocalConnectionId))
                {
                    // The host named us the holder: either the echo of our own pickup, or a
                    // server-driven hold such as the furniture box-up we requested. Take the box
                    // into our own hand; attaching it to a remote anchor would strand it.
                    CoopPlugin.Log.LogInfo("[box-id] taking authoritative hold id="
                        + pickup.BoxNetworkId + " into the local hand.");
                    _localHeldBoxNetworkId = pickup.BoxNetworkId;
                    ApplyPredictedLocal(box, pickup);
                }
                else
                {
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

        private void ApplyPickup(InteractablePackagingBox box, long boxNetworkId,
            Vector3 heldPosition, Quaternion heldRotation, int holderConnectionId)
        {
            var anchor = GetRemoteHoldAnchor(boxNetworkId, heldPosition, heldRotation);
            var attached = false;
            if (_remoteHeldBoxIds.Add(boxNetworkId))
            {
                CoopPlugin.Log.LogInfo("[box-id] applying remote hold id=" + boxNetworkId
                    + " at " + heldPosition + ".");

                // Ride the holder's animated avatar when this peer has it, so the real box moves
                // smoothly and shows its own contents/open state. Otherwise it follows the
                // streamed hand anchor.
                if (PresenceApi.TryGetRemoteCarryAnchor(holderConnectionId, out var carry)
                    && carry != null)
                {
                    anchor.SetParent(carry, false);
                    anchor.localPosition = Vector3.zero;
                    anchor.localRotation = RemoteHoldLocalRotation;
                    attached = true;
                }

                box.StartHoldBox(false, anchor);
                box.StopLerpToTransform();
            }

            box.transform.SetParent(anchor, false);
            // The position offset is applied in the anchor's rotated frame (the box's own frame),
            // so it stays a left shift after the yaw/pitch above.
            box.transform.localPosition = attached ? RemoteHoldLocalOffset : Vector3.zero;
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

        private void ClearLocalHeldBox(long boxNetworkId)
        {
            if (_localHeldBoxNetworkId == boxNetworkId)
            {
                _localHeldBoxNetworkId = 0;
            }
        }

        private Transform GetRemoteHoldAnchor(long boxNetworkId, Vector3 position,
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

        private void ReleaseRemoteHold(long boxNetworkId)
        {
            _remoteHeldBoxIds.Remove(boxNetworkId);
            if (_remoteHoldAnchors.TryGetValue(boxNetworkId, out var anchor))
            {
                _remoteHoldAnchors.Remove(boxNetworkId);
                if (anchor != null)
                {
                    UnityEngine.Object.Destroy(anchor.gameObject);
                }
            }

        }

        /// <summary>True while this peer has the box in the game's own placement preview
        /// (move-box mode, <c>m_IsMovingObject</c>). The move state machine owns the box then:
        /// the object is on the Ignore Raycast layer with its colliders off and its rigidbody
        /// kinematic, and <c>Update</c> lerps its transform toward the aim target. Applying a
        /// networked hold/drop/throw on top of that re-enables physics and re-parents the box
        /// while the move lerp keeps running, so it jitters in place and can no longer be picked
        /// up. A no-op until the move itself finishes.</summary>
        private static bool IsBeingPlaced(InteractablePackagingBox box)
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
            if (message == null || message.BoxNetworkId <= 0)
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
