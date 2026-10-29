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

    /// <summary>A client request to settle one object at a pose.</summary>
    [NetworkMessage]
    public sealed class PlacementMoveIntentMessage : WorldIntentMessage
    {
        public PlacementMoveEntry Move;
    }

    /// <summary>Guest -> host: sent once this peer is fully connected, asking for one more
    /// placement state baseline. The join baseline can race the guest's scene as objects finish
    /// their load-time spawn/recovery from the transferred save, so the fresh pass re-poses
    /// anything that missed. The host answers the requesting connection only.</summary>
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
