using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Api;
using CardShopCoop.Attributes;
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
        /// <summary>True while this host is applying authority (a handler's own game change or
        /// an action's undo/merge) rather than observing a local player interaction. The box
        /// protocol uses this to keep its own replays from creating a second prediction.</summary>
        internal static bool IsApplyingAuthorityNow
            => _instance != null && ReentrancyGuard.Suppressed(_instance);

        private void InstallPlayerBoxInteractionPatches()
        {
            Harmony.CreateClassProcessor(typeof(PlayerBoxPickupPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(PlayerBoxThrowPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(PlayerBoxDropPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(PlayerBoxPlacementPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(FurnitureBoxUpPatch)).Patch();
            Harmony.CreateClassProcessor(typeof(FurnitureUnboxPatch)).Patch();
            // A furniture purchase boxes the furniture before the outer factory settles the
            // package at its delivery pose. Republish placement state once that move is done.
            var purchaseSpawn = AccessTools.Method(typeof(ShelfManager),
                "SpawnInteractableObjectInPackageBox");
            if (purchaseSpawn != null)
            {
                Harmony.Patch(purchaseSpawn, postfix: new HarmonyMethod(
                    typeof(WorldHostBehaviour), nameof(FurniturePurchaseSettledPostfix)));
            }
        }

        public static void FurniturePurchaseSettledPostfix()
            => _instance?.NotifyStructureChanged();

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

        [MessageHandler]
        private ActionOutcome HandlePlayerBoxPickup(MessageContext context,
            PlayerBoxPickupAction message)
            => HandlePlayerBoxAction(context, message, PlayerBoxActionKind.Pickup);

        [MessageHandler]
        private ActionOutcome HandlePlayerBoxPlacement(MessageContext context,
            PlayerBoxPlacementAction message)
            => HandlePlayerBoxAction(context, message, PlayerBoxActionKind.Placement);

        [MessageHandler]
        private ActionOutcome HandlePlayerBoxThrow(MessageContext context,
            PlayerBoxThrowAction message)
            => HandlePlayerBoxAction(context, message, PlayerBoxActionKind.Throw);

        /// <summary>
        /// Host: applies one predicted player-box interaction itself, publishes the resulting
        /// state to every other peer, and returns the same state to the acting client. The acting
        /// client already performed the game action locally, so it is excluded from the broadcast
        /// and settles through <see cref="PlayerBoxInteractionAction.OnMerge"/> instead. A refusal
        /// carries the box's authoritative state so the client's undo converges on what the host
        /// actually shows, not on an inverted local snapshot.
        /// </summary>
        private ActionOutcome HandlePlayerBoxAction(MessageContext context,
            PlayerBoxInteractionAction message, PlayerBoxActionKind kind)
        {
            var action = message as IPlayerBoxAction;
            if (message?.Box == null || action == null)
            {
                CoopPlugin.Log.LogWarning("[box-id] dropped player-box " + kind
                    + ": the box did not resolve to a live object.");
                return ActionOutcome.Reject("player-box action has no live box");
            }

            if (!context.InGame || !context.IsAuthenticated)
            {
                CoopPlugin.Log.LogWarning("Rejected world intent before host execution: "
                    + message.GetType().Name + " inGame=" + context.InGame
                    + " connState=" + context.Connection?.State + ".");
                return ActionOutcome.Reject("world intent rejected before host execution",
                    BuildAuthoritativeResult(message.Box, kind));
            }

            if (!PlayerBoxInteraction.Validate(action))
            {
                CoopPlugin.Log.LogWarning("[box-id] rejected player-box " + kind + " for "
                    + message.Box.name + ": the payload is not a sane pose or velocity.");
                return ActionOutcome.Reject("player-box pose is invalid",
                    BuildAuthoritativeResult(message.Box, kind));
            }

            // The requester is the holder for a pickup; the host stamps that on the action before
            // applying it, so the mirror and every observer attach the box to the right avatar.
            var holderConnectionId = context.ConnectionId;
            if (message is PlayerBoxPickupAction pickup)
            {
                pickup.HolderConnectionId = holderConnectionId;
            }

            if (!ApplyPlayerBoxAction(action))
            {
                return ActionOutcome.Reject("the host could not apply the player-box action",
                    BuildAuthoritativeResult(message.Box, kind));
            }

            PublishPlayerBoxAction(message, kind, holderConnectionId);
            return ActionOutcome.Applied(BuildAppliedResult(action, kind, holderConnectionId));
        }

        /// <summary>Publishes the accepted result to every peer except the actor, whose prediction
        /// already holds exactly this state.</summary>
        private void PublishPlayerBoxAction(PlayerBoxInteractionAction message,
            PlayerBoxActionKind kind, int holderConnectionId)
        {
            var except = message.Peer;
            if (except == null)
            {
                // The framework fills the sender before the handler runs. A missing peer means the
                // action did not arrive through the host dispatch path; do not fall back to a
                // broadcast that would echo the actor's own prediction back at it.
                CoopPlugin.Log.LogWarning("[box-id] player-box " + kind + " for " + message.Box?.name
                    + " has no sender; the accepted state was not published.");
                return;
            }

            switch (kind)
            {
                case PlayerBoxActionKind.Pickup:
                    Context.BroadcastExcept(except, new PlayerBoxPickupMessage
                    {
                        ActionId = message.ActionId,
                        Box = message.Box,
                        HeldPosition = message.Box.transform.position,
                        HeldRotation = message.Box.transform.rotation,
                        HolderConnectionId = holderConnectionId,
                    });
                    break;
                case PlayerBoxActionKind.Placement:
                    Context.BroadcastExcept(except, new PlayerBoxPlacementMessage
                    {
                        ActionId = message.ActionId,
                        Box = message.Box,
                        Position = message.Box.transform.position,
                        Rotation = message.Box.transform.rotation,
                    });
                    break;
                case PlayerBoxActionKind.Throw:
                    var body = message.Box.m_Rigidbody;
                    Context.BroadcastExcept(except, new PlayerBoxThrowMessage
                    {
                        ActionId = message.ActionId,
                        Box = message.Box,
                        Position = body == null ? message.Box.transform.position : body.position,
                        Rotation = body == null ? message.Box.transform.rotation : body.rotation,
                        Velocity = body == null || _playerBoxInteraction == null
                            ? Vector3.zero
                            : _playerBoxInteraction.ReadVelocity(body),
                        AngularVelocity = body == null ? Vector3.zero : body.angularVelocity,
                    });
                    break;
                default:
                    throw new InvalidOperationException(
                        "Unsupported player-box action " + kind + ".");
            }
        }

        /// <summary>The accepted state is exactly what the host just applied, so the actor's
        /// prediction already matches it; this carries it anyway so every resolution has a
        /// post-action state.</summary>
        private static PlayerBoxActionResult BuildAppliedResult(IPlayerBoxAction message,
            PlayerBoxActionKind kind, int holderConnectionId)
        {
            var result = new PlayerBoxActionResult
            {
                Kind = kind,
                Box = message.Box,
                HolderConnectionId = holderConnectionId,
                HeldPosition = message.HeldPosition,
                HeldRotation = message.HeldRotation,
                Position = message.Position,
                Rotation = message.Rotation,
                Velocity = message.Velocity,
                AngularVelocity = message.AngularVelocity,
            };
            return result;
        }

        /// <summary>Host: the state a refused action must merge back to. A held box reports its
        /// holder (the host itself or a mirrored remote holder), a stored box reports that the
        /// warehouse channel owns it, and anything else is a loose box at its current pose.</summary>
        private PlayerBoxActionResult BuildAuthoritativeResult(InteractablePackagingBox box,
            PlayerBoxActionKind fallbackKind)
        {
            var result = new PlayerBoxActionResult
            {
                Kind = fallbackKind,
                Box = box,
            };
            if (box == null)
            {
                return result;
            }

            var position = box.transform.position;
            var rotation = box.transform.rotation;
            result.Position = position;
            result.Rotation = rotation;
            result.HeldPosition = position;
            result.HeldRotation = rotation;

            if (_boxNetworkInteraction == null || !_boxNetworkInteraction.TryGetId(box, out var id))
            {
                // An unregistered box has no authoritative holder state to report; the client's
                // own undo still restores its local snapshot.
                return result;
            }

            if (_boxNetworkInteraction.IsStored(id))
            {
                result.Stored = true;
                result.Kind = PlayerBoxActionKind.Placement;
                return result;
            }

            if (_playerBoxInteraction != null
                && ReferenceEquals(_playerBoxInteraction.LocalHeldBox, box))
            {
                result.Kind = PlayerBoxActionKind.Pickup;
                result.HolderConnectionId = 0;
                return result;
            }

            if (_playerBoxInteraction != null
                && _playerBoxInteraction.TryGetRemoteHolder(id, out var holder))
            {
                result.Kind = PlayerBoxActionKind.Pickup;
                result.HolderConnectionId = holder;
                return result;
            }

            // Loose (or being placed): the pose is the authoritative state.
            result.Kind = PlayerBoxActionKind.Placement;
            return result;
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

        private bool ApplyPlayerBoxAction(IPlayerBoxAction message)
        {
            return _playerBoxInteraction != null && _playerBoxInteraction.ApplyIncoming(message);
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

        /// <summary>The host's own furniture box-up while the piece is still in placement move
        /// mode: release the placement hold first so the game's BoxUpObject can start the box
        /// hold, and tell observers the ghost ended. A client has the same prefix in
        /// WorldClientBehaviour.PlayerBoxInteraction.</summary>
        [HarmonyPatch(typeof(InteractableObject), "BoxUpObject")]
        private static class FurnitureBoxUpPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractableObject __instance, bool holdBox)
            {
                if (_instance == null || __instance == null || !holdBox
                    || __instance is InteractablePlayTable)
                {
                    return;
                }

                _instance._placementHold?.LocalEnded(__instance);
            }
        }

        /// <summary>Opening a furniture package on the host drops the package out of the host's
        /// hand before vanilla starts the unboxed piece's move preview. The client already gets
        /// this from the game's OnExitHoldBoxMode; doing it explicitly keeps the host's held box
        /// from staying parented to the hand until the piece is placed.</summary>
        [HarmonyPatch(typeof(InteractablePackagingBox_Shelf), "OnPressOpenBox")]
        private static class FurnitureUnboxPatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractablePackagingBox_Shelf __instance)
            {
                if (_instance?._playerBoxInteraction == null || __instance == null)
                {
                    return;
                }

                if (_instance._boxNetworkInteraction != null
                    && _instance._boxNetworkInteraction.TryGetId(__instance, out var boxId))
                {
                    _instance._playerBoxInteraction.ReleaseLocalHold(boxId);
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

    /// <summary>The three player-box interaction shapes. A host state message and a predicted
    /// client action share the same fields but different bases.</summary>
    public enum PlayerBoxActionKind : byte
    {
        Pickup = 1,
        Placement = 2,
        Throw = 3,
    }

    /// <summary>Shared read surface for a player-box interaction in either direction: a host state
    /// message, a predicted client action, or the host's post-action result.</summary>
    internal interface IPlayerBoxAction
    {
        PlayerBoxActionKind Kind
        {
            get;
        }
        InteractablePackagingBox Box
        {
            get;
        }
        int HolderConnectionId
        {
            get;
        }
        Vector3 HeldPosition
        {
            get;
        }
        Quaternion HeldRotation
        {
            get;
        }
        Vector3 Position
        {
            get;
        }
        Quaternion Rotation
        {
            get;
        }
        Vector3 Velocity
        {
            get;
        }
        Vector3 AngularVelocity
        {
            get;
        }
    }

    [NetworkMessage]
    public class PlayerBoxPickupMessage : PlayerBoxInteractionMessage, IPlayerBoxAction
    {
        public Vector3 HeldPosition;
        public Quaternion HeldRotation;
        /// <summary>Connection id of the player now holding the box, in the sender's connection
        /// space (0 = the host). Observers use it to attach the box to that player's avatar.</summary>
        public int HolderConnectionId;

        PlayerBoxActionKind IPlayerBoxAction.Kind => PlayerBoxActionKind.Pickup;
        InteractablePackagingBox IPlayerBoxAction.Box => Box;
        int IPlayerBoxAction.HolderConnectionId => HolderConnectionId;
        Vector3 IPlayerBoxAction.HeldPosition => HeldPosition;
        Quaternion IPlayerBoxAction.HeldRotation => HeldRotation;
        Vector3 IPlayerBoxAction.Position => default;
        Quaternion IPlayerBoxAction.Rotation => default;
        Vector3 IPlayerBoxAction.Velocity => default;
        Vector3 IPlayerBoxAction.AngularVelocity => default;
    }

    [NetworkMessage]
    public class PlayerBoxPlacementMessage : PlayerBoxInteractionMessage, IPlayerBoxAction
    {
        public Vector3 Position;
        public Quaternion Rotation;

        PlayerBoxActionKind IPlayerBoxAction.Kind => PlayerBoxActionKind.Placement;
        InteractablePackagingBox IPlayerBoxAction.Box => Box;
        int IPlayerBoxAction.HolderConnectionId => 0;
        Vector3 IPlayerBoxAction.HeldPosition => default;
        Quaternion IPlayerBoxAction.HeldRotation => default;
        Vector3 IPlayerBoxAction.Position => Position;
        Quaternion IPlayerBoxAction.Rotation => Rotation;
        Vector3 IPlayerBoxAction.Velocity => default;
        Vector3 IPlayerBoxAction.AngularVelocity => default;
    }

    [NetworkMessage]
    public class PlayerBoxThrowMessage : PlayerBoxInteractionMessage, IPlayerBoxAction
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public Vector3 AngularVelocity;

        PlayerBoxActionKind IPlayerBoxAction.Kind => PlayerBoxActionKind.Throw;
        InteractablePackagingBox IPlayerBoxAction.Box => Box;
        int IPlayerBoxAction.HolderConnectionId => 0;
        Vector3 IPlayerBoxAction.HeldPosition => default;
        Quaternion IPlayerBoxAction.HeldRotation => default;
        Vector3 IPlayerBoxAction.Position => Position;
        Quaternion IPlayerBoxAction.Rotation => Rotation;
        Vector3 IPlayerBoxAction.Velocity => Velocity;
        Vector3 IPlayerBoxAction.AngularVelocity => AngularVelocity;
    }

    /// <summary>
    /// One predicted player-box interaction. The client's game already performed the local
    /// mutation through vanilla (the hold, drop, or throw); the prediction remembers how to
    /// reverse it and how to clean up after a refusal. A player-box move can be interfered with by
    /// another player, so it is a validated action rather than a fire-and-forget notification.
    /// </summary>
    public abstract class PlayerBoxInteractionAction : WorldIntentMessage
    {
        public InteractablePackagingBox Box;

        /// <summary>Client-only reversal artifacts. Never serialized.</summary>
        [NonSerialized]
        internal PlayerBoxLocalPrediction LocalPrediction;

        public override void OnUndo(ActionResponseMessage response)
            => WorldClientBehaviour.UndoPlayerBoxAction(this);

        public override void OnMerge(ActionResponseMessage response)
            => WorldClientBehaviour.MergePlayerBoxAction(this, response);
    }

    /// <summary>What a rejected prediction has to put back, captured before the game mutated the
    /// box. <see cref="BoxNetworkId"/> lets the merge settle this move's pending-move entry even
    /// when the live box has since been destroyed.</summary>
    internal sealed class PlayerBoxLocalPrediction
    {
        internal Guid BoxNetworkId;
        internal Action Undo;
        internal Action Refused;
    }

    [NetworkMessage]
    public sealed class PlayerBoxPickupAction : PlayerBoxInteractionAction, IPlayerBoxAction
    {
        public Vector3 HeldPosition;
        public Quaternion HeldRotation;
        /// <summary>Stamped by the host with the authenticated sender before it applies the
        /// action; a client leaves this at zero.</summary>
        public int HolderConnectionId;

        PlayerBoxActionKind IPlayerBoxAction.Kind => PlayerBoxActionKind.Pickup;
        InteractablePackagingBox IPlayerBoxAction.Box => Box;
        int IPlayerBoxAction.HolderConnectionId => HolderConnectionId;
        Vector3 IPlayerBoxAction.HeldPosition => HeldPosition;
        Quaternion IPlayerBoxAction.HeldRotation => HeldRotation;
        Vector3 IPlayerBoxAction.Position => default;
        Quaternion IPlayerBoxAction.Rotation => default;
        Vector3 IPlayerBoxAction.Velocity => default;
        Vector3 IPlayerBoxAction.AngularVelocity => default;
    }

    [NetworkMessage]
    public sealed class PlayerBoxPlacementAction : PlayerBoxInteractionAction, IPlayerBoxAction
    {
        public Vector3 Position;
        public Quaternion Rotation;

        PlayerBoxActionKind IPlayerBoxAction.Kind => PlayerBoxActionKind.Placement;
        InteractablePackagingBox IPlayerBoxAction.Box => Box;
        int IPlayerBoxAction.HolderConnectionId => 0;
        Vector3 IPlayerBoxAction.HeldPosition => default;
        Quaternion IPlayerBoxAction.HeldRotation => default;
        Vector3 IPlayerBoxAction.Position => Position;
        Quaternion IPlayerBoxAction.Rotation => Rotation;
        Vector3 IPlayerBoxAction.Velocity => default;
        Vector3 IPlayerBoxAction.AngularVelocity => default;
    }

    [NetworkMessage]
    public sealed class PlayerBoxThrowAction : PlayerBoxInteractionAction, IPlayerBoxAction
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public Vector3 AngularVelocity;

        PlayerBoxActionKind IPlayerBoxAction.Kind => PlayerBoxActionKind.Throw;
        InteractablePackagingBox IPlayerBoxAction.Box => Box;
        int IPlayerBoxAction.HolderConnectionId => 0;
        Vector3 IPlayerBoxAction.HeldPosition => default;
        Quaternion IPlayerBoxAction.HeldRotation => default;
        Vector3 IPlayerBoxAction.Position => Position;
        Quaternion IPlayerBoxAction.Rotation => Rotation;
        Vector3 IPlayerBoxAction.Velocity => Velocity;
        Vector3 IPlayerBoxAction.AngularVelocity => AngularVelocity;
    }

    /// <summary>
    /// The host's post-action state for one player-box action, returned to the acting client on
    /// accept and on reject. On accept it mirrors the applied prediction; on reject it carries
    /// whatever authoritative state the box ended up in (held by someone else, stored, or loose),
    /// which the client merges after its own undo.
    /// </summary>
    [NetworkMessage]
    public sealed class PlayerBoxActionResult : ActionResponseMessage, IPlayerBoxAction
    {
        public PlayerBoxActionKind Kind;
        public InteractablePackagingBox Box;
        /// <summary>True when the box is stored in a warehouse compartment, so the client must
        /// release its hand and let the warehouse channel reconcile the stored record.</summary>
        public bool Stored;
        public int HolderConnectionId;
        public Vector3 HeldPosition;
        public Quaternion HeldRotation;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public Vector3 AngularVelocity;

        PlayerBoxActionKind IPlayerBoxAction.Kind => Kind;
        InteractablePackagingBox IPlayerBoxAction.Box => Box;
        int IPlayerBoxAction.HolderConnectionId => HolderConnectionId;
        Vector3 IPlayerBoxAction.HeldPosition => HeldPosition;
        Quaternion IPlayerBoxAction.HeldRotation => HeldRotation;
        Vector3 IPlayerBoxAction.Position => Position;
        Quaternion IPlayerBoxAction.Rotation => Rotation;
        Vector3 IPlayerBoxAction.Velocity => Velocity;
        Vector3 IPlayerBoxAction.AngularVelocity => AngularVelocity;
    }

    /// <summary>Every authoritative player-box state message addresses exactly one live packaging
    /// box and is published to every peer except the acting player.</summary>
    public abstract class PlayerBoxInteractionMessage : WorldStateMessage
    {
        public InteractablePackagingBox Box;
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
        private readonly Action<ICoopPeer, INetMessage> _send;
        private readonly bool _host;
        private bool _applyingPrediction;
        private int _coveredHold;

        /// <summary>True while the live behaviour on this role is applying authority (a handler's
        /// game change or an action's undo/merge) rather than observing a local interaction.</summary>
        private bool IsApplyingAuthority
            => _host
                ? WorldHostBehaviour.IsApplyingAuthorityNow
                : WorldClientBehaviour.IsApplyingAuthorityNow;

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
            public InteractablePackagingBox Box;
            public Action Undo;
        }

        /// <summary>Host: the mirror publishes authoritative state to every peer. The host never
        /// sends a command, so it has no command send channel.</summary>
        internal PlayerBoxInteraction(BoxNetworkInteraction boxes, Action<INetMessage> broadcast)
        {
            _boxes = boxes ?? throw new ArgumentNullException(nameof(boxes));
            _broadcast = broadcast ?? throw new ArgumentNullException(nameof(broadcast));
            _host = true;
        }

        /// <summary>Client: a predicted player-box interaction goes to the host through the public
        /// send seam. The peer slot is unused - a client has exactly one command recipient.</summary>
        internal PlayerBoxInteraction(BoxNetworkInteraction boxes, Action<ICoopPeer, INetMessage> send)
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
            _remoteHolders.Clear();
            _coveredHold = 0;
        }

        internal LocalAction CaptureLocalAction(InteractablePackagingBox box, bool isPlayer,
            bool alignBody)
        {
            if (_applyingPrediction || IsApplyingAuthority)
            {
                // The ledger is running a prediction reversal or merge, not a fresh local
                // interaction. Forwarding that replay would create a second prediction for one
                // physical action.
                return default;
            }

            if (!isPlayer || box == null || !_boxes.TryGetId(box, out _))
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
                Box = box,
                Undo = () => RestorePredictedLocal(box, priorParent, priorPosition,
                    priorRotation, priorVelocity, priorAngularVelocity),
            };
        }

        private void ApplyPredictedLocal(InteractablePackagingBox box,
            IPlayerBoxAction message)
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
                switch (message.Kind)
                {
                    case PlayerBoxActionKind.Pickup:
                        TakeIntoLocalHand(box);
                        break;
                    case PlayerBoxActionKind.Placement:
                        box.DropBox(true);
                        break;
                    case PlayerBoxActionKind.Throw:
                        box.ThrowBox(true);
                        break;
                    default:
                        throw new InvalidOperationException(
                            "Unsupported predicted player-box action.");
                }

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
            // the host resolves it; its response settles where the box ends up.
            if (_boxes.TryGetId(box, out var moveId) && _boxes.HasPendingLocalMove(moveId))
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
                PublishForcedDrop(current, keep);
                ReleaseRemoteHold(conflictingId);
            }

            current.DropBox(false);
            controller.OnExitHoldBoxMode();
        }

        /// <summary>Tells the host (and observers) that <paramref name="box"/> is no longer held.
        /// The drop is a predicted action: a rejection restores the old hold and, because
        /// <paramref name="replacement"/> now owns the hand, releases it so the one-box-per-hand
        /// invariant survives a refused forced drop.</summary>
        private void PublishForcedDrop(InteractablePackagingBox box,
            InteractablePackagingBox replacement)
        {
            if (_host)
            {
                _broadcast(new PlayerBoxPlacementMessage
                {
                    Box = box,
                    Position = box.transform.position,
                    Rotation = box.transform.rotation,
                });
                return;
            }

            if (IsApplyingAuthority)
            {
                // The ledger is reversing or merging a prediction. That replay is not a new
                // physical drop; forwarding it would predict our own rollback.
                return;
            }

            var command = new PlayerBoxPlacementAction
            {
                Box = box,
                Position = box.transform.position,
                Rotation = box.transform.rotation,
                LocalPrediction = new PlayerBoxLocalPrediction
                {
                    BoxNetworkId = _boxes.TryGetId(box, out var boxId) ? boxId : Guid.Empty,
                    Undo = () => RestoreForcedHold(box),
                    Refused = () => ReleaseRefusedReplacement(replacement),
                },
            };
            SendCommand(command);
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
            if (_boxes.TryGetId(action.Box, out var pickupId))
            {
                ReleaseRemoteHold(pickupId);
            }

            ReadHoldPose(box, out var heldPosition, out var heldRotation);
            if (_host)
            {
                // The host's own pickup has no remote avatar.
                _broadcast(new PlayerBoxPickupMessage
                {
                    Box = box,
                    HeldPosition = heldPosition,
                    HeldRotation = heldRotation,
                    HolderConnectionId = 0,
                });
                return;
            }

            if (_coveredHold > 0)
            {
                // A vanilla container take (a warehouse or empty-box take) is spawning and holding
                // a box the host has not created yet: this pickup names an id the host cannot
                // resolve. The take's own container action owns the interaction and the host
                // announces the granted hold once it creates the box.
                return;
            }

            // The host stamps the holder from the authenticated sender.
            SendCommand(new PlayerBoxPickupAction
            {
                Box = box,
                HeldPosition = heldPosition,
                HeldRotation = heldRotation,
                LocalPrediction = CapturePrediction(action),
            });
        }

        /// <summary>Announces a host hold that was not covered by the normal pickup capture: a
        /// box-up hold the game applied before the package had a network id, or a warehouse take
        /// whose pickup had to be re-announced after its shelf-removal delta. The host is
        /// already holding the box locally; observers just need the pickup to attach it to the
        /// host's avatar.</summary>
        internal void PublishHostHeld(InteractablePackagingBox box, Guid boxNetworkId)
        {
            if (!_host || box == null || boxNetworkId == Guid.Empty)
            {
                return;
            }

            var pickup = new PlayerBoxPickupMessage { Box = box };
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
        internal void AnnounceGrantedHold(InteractablePackagingBox box, int holderConnectionId)
        {
            if (!_host)
            {
                return;
            }

            if (box == null || !_boxes.TryGetId(box, out var boxNetworkId))
            {
                CoopPlugin.Log.LogWarning("[box-id] granted hold holder=" + holderConnectionId
                    + " has no live box; not announced.");
                return;
            }

            if (_boxes.IsStored(boxNetworkId) || IsBeingPlaced(box))
            {
                CoopPlugin.Log.LogWarning("[box-id] granted hold for box name=" + box.name
                    + " could not be announced (stored=" + _boxes.IsStored(boxNetworkId)
                    + ", moving=" + IsBeingPlaced(box) + ").");
                return;
            }

            var pickup = new PlayerBoxPickupMessage
            {
                Box = box,
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

            if (_host)
            {
                _broadcast(new PlayerBoxPlacementMessage
                {
                    Box = box,
                    Position = box.transform.position,
                    Rotation = box.transform.rotation,
                });
                return;
            }

            SendCommand(new PlayerBoxPlacementAction
            {
                Box = box,
                Position = box.transform.position,
                Rotation = box.transform.rotation,
                LocalPrediction = CapturePrediction(action),
            });
        }

        internal void PublishThrow(InteractablePackagingBox box, LocalAction action)
        {
            if (!action.Send || box == null)
            {
                return;
            }

            var body = box.m_Rigidbody;
            var position = body == null ? box.transform.position : body.position;
            var rotation = body == null ? box.transform.rotation : body.rotation;
            var velocity = ReadVelocity(body);
            var angularVelocity = body == null ? Vector3.zero : body.angularVelocity;
            if (_host)
            {
                _broadcast(new PlayerBoxThrowMessage
                {
                    Box = box,
                    Position = position,
                    Rotation = rotation,
                    Velocity = velocity,
                    AngularVelocity = angularVelocity,
                });
                return;
            }

            SendCommand(new PlayerBoxThrowAction
            {
                Box = box,
                Position = position,
                Rotation = rotation,
                Velocity = velocity,
                AngularVelocity = angularVelocity,
                LocalPrediction = CapturePrediction(action),
            });
        }

        /// <summary>Client: the local game method already ran, so the sent action carries the real
        /// post-action pose and throw velocity; the captured closure reverses the local change if
        /// the host refuses it.</summary>
        private static PlayerBoxLocalPrediction CapturePrediction(LocalAction action)
            => new()
            {
                Undo = action.Undo
            };

        /// <summary>Client: sends one predicted player-box action to the host. A move owns its box
        /// until the host resolves it, so a granted hold or pose that crossed it on the wire must
        /// not override it. The registration happens before the send so the entry exists for the
        /// whole in-flight window.</summary>
        private void SendCommand(PlayerBoxInteractionAction command)
        {
            if (_host)
            {
                throw new InvalidOperationException(
                    "The host never sends a player-box action command.");
            }
            if (command == null || command.Box == null)
            {
                return;
            }

            var id = _boxes.TryGetId(command.Box, out var boxId) ? boxId : Guid.Empty;
            command.LocalPrediction ??= new PlayerBoxLocalPrediction();
            command.LocalPrediction.BoxNetworkId = id;
            if (id != Guid.Empty
                && (command is PlayerBoxPlacementAction || command is PlayerBoxThrowAction))
            {
                _boxes.NotePendingLocalMove(id, command);
            }

            try
            {
                _send(null, command);
            }
            catch
            {
                // The action never reached the host, so nothing will settle it. Drop the pending
                // move before the transport failure propagates.
                _boxes.ForgetPendingLocalMove(id, command);
                throw;
            }
        }

        private static void ReadHoldPose(InteractablePackagingBox box, out Vector3 position,
            out Quaternion rotation)
        {
            var controller = SceneRef<InteractionPlayerController>.Get();
            var hand = controller?.m_HoldItemPos;
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
        internal bool ApplyIncoming(IPlayerBoxAction message)
        {
            if (message == null || message.Box == null)
            {
                CoopPlugin.Log.LogWarning("[box-id] dropped player-box action: the box did not "
                    + "resolve to a live object (" + (message?.GetType().Name ?? "<null>") + ").");
                return false;
            }

            var box = message.Box;
            if (!_boxes.TryGetId(box, out var boxNetworkId))
            {
                CoopPlugin.Log.LogWarning("[box-id] dropped player-box action for " + box.name
                    + ": the live box has no network identity ("
                    + message.GetType().Name + ").");
                return false;
            }

            if (_host && !Validate(message))
            {
                return false;
            }

            if (IsBeingPlaced(box))
            {
                // This peer already owns the box in the game's placement preview. A networked
                // hold, drop, or throw on top of that fight the move state machine and strand the
                // box, and the move will publish its own authoritative result when it finishes.
                // A terminal action still means nobody holds it, so retire any mirrored hold.
                if (message.Kind == PlayerBoxActionKind.Placement
                    || message.Kind == PlayerBoxActionKind.Throw)
                {
                    ReleaseRemoteHold(boxNetworkId);
                }

                return false;
            }

            if (message.Kind == PlayerBoxActionKind.Pickup)
            {
                if (_boxes.IsStored(boxNetworkId))
                {
                    // The box is on a warehouse shelf on this peer. A hold that names a holder is
                    // stale - it raced the store that put the box there (the guest's pickup echo
                    // can arrive after its own optimistic store) - and re-attaching it would leave
                    // this peer holding a box the host already sees stored. A real take clears the
                    // stored flag before any hold follows.
                    ReleaseLocalHold(boxNetworkId);
                    return false;
                }

                if (!_host && message.HolderConnectionId == CoopCore.LocalConnectionId)
                {
                    // The host named us the holder: either the echo of our own pickup, or a
                    // server-driven hold such as the furniture box-up we requested. Take the box
                    // into our own hand; attaching it to a remote anchor would strand it.
                    ApplyPredictedLocal(box, message);
                }
                else
                {
                    // Someone else is now the holder. If we still think we hold this box (the
                    // host took it out of our hands), release it first; otherwise the controller
                    // keeps hold mode and the box is misattributed to us.
                    if (LocalHeldBoxId == boxNetworkId)
                    {
                        box.StopLerpToTransform();
                        box.DropBox(false);
                        SceneRef<InteractionPlayerController>.Get()?.OnExitHoldBoxMode();
                    }

                    ApplyPickup(box, boxNetworkId, message.HeldPosition, message.HeldRotation,
                        message.HolderConnectionId);
                }
            }
            else if (message.Kind == PlayerBoxActionKind.Placement)
            {
                ApplyPlacement(box, boxNetworkId, message.Position, message.Rotation);
            }
            else if (message.Kind == PlayerBoxActionKind.Throw)
            {
                ApplyThrow(box, boxNetworkId, message);
            }
            else
            {
                throw new InvalidOperationException("Unsupported player-box action "
                    + message.GetType().FullName + ".");
            }

            return true;
        }

        /// <summary>Client: reverses the local prediction of one settled action. Only called for a
        /// rejection; the closure was captured from the box state before the game mutated it.</summary>
        internal void UndoAction(PlayerBoxInteractionAction action)
        {
            var prediction = action?.LocalPrediction;
            if (prediction?.Undo == null)
            {
                CoopPlugin.Log.LogWarning("[box-id] rejected " + action?.GetType().Name
                    + " has no local prediction to reverse.");
                return;
            }

            if (prediction.BoxNetworkId != Guid.Empty
                && _boxes.HasPendingLocalMove(prediction.BoxNetworkId)
                && !_boxes.IsPendingLocalMove(prediction.BoxNetworkId, action))
            {
                // A newer local move for the same box replaced this one and owns it now. Its own
                // resolution decides where the box lands; reversing this older snapshot here would
                // clobber that newer prediction.
                CoopPlugin.Log.LogInfo("[box-id] rejected " + action.GetType().Name + " box="
                    + prediction.BoxNetworkId + " is superseded by a newer move; not reverting.");
                return;
            }

            prediction.Undo();
        }

        /// <summary>Client: settles one resolved action. The move entry is released first so a
        /// newer in-flight move for the same box still owns it; a rejection then cleans up the
        /// prediction's local side effects and merges the host's authoritative state.</summary>
        internal void MergeAction(PlayerBoxInteractionAction action, ActionResponseMessage response)
        {
            var prediction = action?.LocalPrediction;
            var boxNetworkId = prediction?.BoxNetworkId ?? Guid.Empty;
            _boxes.ForgetPendingLocalMove(boxNetworkId, action);

            if (response == null || !response.Rejected)
            {
                // The host applied the exact payload this client predicted, so the local state is
                // already the authoritative state.
                return;
            }

            CoopPlugin.Log.LogInfo("[box-id] host rejected " + action.GetType().Name
                + " box=" + boxNetworkId + ".");
            try
            {
                prediction?.Refused?.Invoke();
            }
            finally
            {
                MergeAuthoritativeResult(response as PlayerBoxActionResult);
            }
        }

        /// <summary>Client: applies the host's post-action state after a rejected prediction. A
        /// newer local move for the same box owns it and settles through its own resolution, so it
        /// is never overridden here.</summary>
        private void MergeAuthoritativeResult(PlayerBoxActionResult result)
        {
            if (result?.Box == null)
            {
                // The box is gone on this peer (destroyed or not materialized); the local undo is
                // the only state left to settle.
                return;
            }

            if (!_boxes.TryGetId(result.Box, out var boxNetworkId))
            {
                return;
            }

            if (_boxes.HasPendingLocalMove(boxNetworkId))
            {
                return;
            }

            if (result.Stored)
            {
                // The warehouse channel owns a stored box: release the hand and let its own state
                // message place the record.
                ReleaseLocalHold(boxNetworkId);
                return;
            }

            if (result.Kind == PlayerBoxActionKind.Pickup
                && result.HolderConnectionId == CoopCore.LocalConnectionId
                && LocalHeldBoxId == boxNetworkId)
            {
                // The undo already restored our own hold; re-applying would restart the hold lerp.
                return;
            }

            ApplyIncoming(result);
        }

        private void ApplyPickup(InteractablePackagingBox box, Guid boxNetworkId,
            Vector3 heldPosition, Quaternion heldRotation, int holderConnectionId)
        {
            var anchor = GetRemoteHoldAnchor(boxNetworkId, heldPosition, heldRotation);

            // Re-resolve the holder's carry anchor on every pickup. The id can remain in
            // _remoteHoldAnchors from an earlier hold (a warehouse store does not release it), so
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

            _remoteHolders[boxNetworkId] = holderConnectionId;
            box.StartHoldBox(false, anchor);
            box.StopLerpToTransform();
            box.transform.SetParent(anchor, false);
            // The position offset is applied in the anchor's rotated frame (the box's own frame),
            // so it stays a left shift after the yaw/pitch above.
            box.transform.localPosition = carried ? RemoteHoldLocalOffset : Vector3.zero;
            box.transform.localRotation = Quaternion.identity;
        }

        private void ApplyPlacement(InteractablePackagingBox box, Guid boxNetworkId,
            Vector3 position, Quaternion rotation)
        {
            box.DropBox(false);
            ReleaseRemoteHold(boxNetworkId);
            ApplyPose(box, position, rotation);
        }

        private void ApplyThrow(InteractablePackagingBox box, Guid boxNetworkId,
            IPlayerBoxAction message)
        {
            // Face the box along the thrower's released rotation first so the game's own throw
            // force leaves in the right direction even when the velocity sample is weak; the
            // sender's sampled velocity then overrides it.
            ApplyPose(box, message.Position, message.Rotation);
            box.ThrowBox(false);
            ReleaseRemoteHold(boxNetworkId);
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
        internal void ReleaseRemoteHoldForBox(InteractablePackagingBox box)
        {
            if (_boxes.TryGetId(box, out var boxNetworkId))
            {
                ReleaseRemoteHold(boxNetworkId);
            }
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
                    Box = box,
                    Position = box.transform.position,
                    Rotation = box.transform.rotation,
                });
            }
        }

        private void ReleaseRemoteHold(Guid boxNetworkId)
        {
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
                append(BuildBaselineHold(localBox, localBox.transform.position,
                    localBox.transform.rotation, 0));
            }

            foreach (var pair in _remoteHolders)
            {
                // The box descriptor baseline skips stored boxes; do not announce a hold for one
                // (it would name an id the joiner never materialized).
                if (!_boxes.IsStored(pair.Key)
                    && _boxes.TryGetBox(pair.Key, out var box) && box != null)
                {
                    append(BuildBaselineHold(box, box.transform.position,
                        box.transform.rotation, pair.Value));
                }
            }
        }

        private static PlayerBoxPickupMessage BuildBaselineHold(InteractablePackagingBox box,
            Vector3 position, Quaternion rotation, int holderConnectionId)
            => new()
            {
                Box = box,
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

        internal Vector3 ReadVelocity(Rigidbody body)
        {
            var property = _rigidbodyLinearVelocity ?? _rigidbodyVelocity;
            return body != null && property?.GetValue(body) is Vector3 velocity ? velocity : Vector3.zero;
        }

        private void WriteVelocity(Rigidbody body, Vector3 velocity)
        {
            var property = _rigidbodyLinearVelocity ?? _rigidbodyVelocity;
            property?.SetValue(body, velocity);
        }

        internal static bool Validate(IPlayerBoxAction message)
        {
            if (message == null || message.Box == null)
            {
                return false;
            }

            return message.Kind switch
            {
                PlayerBoxActionKind.Pickup => IsSanePose(message.HeldPosition, message.HeldRotation),
                PlayerBoxActionKind.Placement => IsSanePose(message.Position, message.Rotation),
                PlayerBoxActionKind.Throw => IsSanePose(message.Position, message.Rotation)
                    && IsSaneVector(message.Velocity, MaxVelocity)
                    && IsSaneVector(message.AngularVelocity, MaxVelocity),
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




