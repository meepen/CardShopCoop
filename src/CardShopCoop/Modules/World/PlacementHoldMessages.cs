using CardShopCoop.Net;
using UnityEngine;

namespace CardShopCoop.Modules.World
{
    /// <summary>A player picked up one placed object for placement. Observers show the vanilla
    /// move preview for it and no other player may pick it up until the hold ends.</summary>
    [NetworkMessage]
    public class PlacementHoldBeginMessage : INetMessage
    {
        /// <summary>Stable identity from <see cref="PlacementHoldKey"/>.</summary>
        public long HoldKey;

        /// <summary>Connection id of the holder, stamped by the host (0 = the host itself).</summary>
        public int MoverConnectionId;
    }

    [NetworkMessage]
    public sealed class PlacementHoldBeginRequestMessage : PlacementHoldBeginMessage
    {
    }

    /// <summary>A player put a held placement object down, or the hold was released on disconnect.</summary>
    [NetworkMessage]
    public class PlacementHoldEndMessage : INetMessage
    {
        public long HoldKey;
    }

    [NetworkMessage]
    public sealed class PlacementHoldEndRequestMessage : PlacementHoldEndMessage
    {
    }

    /// <summary>Event-driven rotation of a held placement object. The mover only emits this when
    /// its own rotate input actually changed the object, so observers never receive a per-frame
    /// pose stream; they apply the rotation to the ghost.</summary>
    [NetworkMessage]
    public class PlacementHoldRotationMessage : INetMessage
    {
        public long HoldKey;
        public Quaternion Rotation;
    }

    [NetworkMessage]
    public sealed class PlacementHoldRotationRequestMessage : PlacementHoldRotationMessage
    {
    }
}
