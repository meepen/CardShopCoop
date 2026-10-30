using System;
using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Net;
using UnityEngine;

namespace CardShopCoop.Modules.World
{
    /// <summary>Placement pose/state baseline sent when a peer joins or a scene is rebuilt. It
    /// never carries population or identity: creation and removal are lifecycle messages owned by
    /// the identity module.</summary>
    [NetworkMessage]
    public sealed class PlacementBaselineMessage : INetMessage
    {
        public List<PlacementMoveEntry> Moves = new();
    }

    /// <summary>A minimal host-owned placement entity state change. Creation and removal are not
    /// state operations; they belong to the identity lifecycle.</summary>
    [NetworkMessage]
    public sealed class PlacementDeltaMessage : WorldStateMessage
    {
        /// <summary>A full state update (pose and boxed state).</summary>
        public const byte Update = 1;

        /// <summary>A pose-only change for an entity whose other state is unchanged.</summary>
        public const byte Pose = 2;
        public byte Operation;
        public PlacementMoveEntry Entity;
    }

    /// <summary>A predicted client move: the object has already been placed locally through the
    /// game's own path. <see cref="OnUndo"/> restores the captured pre-move pose and
    /// <see cref="OnMerge"/> applies the host's post-action pose from
    /// <see cref="PlacementMoveResponse"/>.</summary>
    [NetworkMessage]
    public sealed class PlacementMoveIntentMessage : WorldIntentMessage
    {
        /// <summary>The pose the local game path produced. The host applies exactly this.</summary>
        public PlacementMoveEntry Move;

        /// <summary>The pose before this peer started moving the object. Local undo state; never
        /// serialized.</summary>
        [NonSerialized]
        internal PlacementMoveEntry Before;

        /// <summary>The client that owns this prediction and its in-flight move bookkeeping. Local
        /// only.</summary>
        [NonSerialized]
        internal WorldClientBehaviour Origin;

        public override void OnUndo(ActionResponseMessage response)
            => Origin?.UndoPlacementMove(this);

        public override void OnMerge(ActionResponseMessage response)
            => Origin?.MergePlacementMove(this, response as PlacementMoveResponse);
    }

    /// <summary>The host's authoritative placement state after one move, sent to the acting
    /// client on accept and reject alike. The actor merges it instead of trusting its own
    /// prediction.</summary>
    [NetworkMessage]
    public sealed class PlacementMoveResponse : ActionResponseMessage
    {
        /// <summary>The object's full pose/boxed state on the host. Null when the host could not
        /// resolve the entity at all.</summary>
        public PlacementMoveEntry Move;
    }

    /// <summary>Guest -> host: sent once this peer is fully connected, asking for one more
    /// placement state baseline. The join baseline can race the guest's scene as objects finish
    /// their load-time spawn/recovery from the transferred save, so the fresh pass re-poses
    /// anything that missed. The host answers the requesting peer only.</summary>
    [NetworkMessage]
    public sealed class PlacementBaselineRequestMessage : INetMessage
    {
    }

    /// <summary>One placement object's pose and boxed state. Entity is the live scene object; a
    /// message that arrives without one is dropped. There is no stable-id fallback here: identity
    /// is owned by the GameObjectIdentity module and its own lifecycle messages.</summary>
    public sealed class PlacementMoveEntry
    {
        public InteractableObject Entity;
        public Vector3 Pos;
        public Quaternion Rot;
        public bool IsBoxed;
        public Vector3 BoxedPos;
        public Quaternion BoxedRot;
    }
}
