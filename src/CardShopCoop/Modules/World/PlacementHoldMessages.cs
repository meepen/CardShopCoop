using CardShopCoop.Net;
using UnityEngine;

namespace CardShopCoop.Modules.World
{
    /// <summary>A player picked up one placed object for placement. Observers show the vanilla
    /// move preview for it and no other player may pick it up until the hold ends. The message
    /// carries the live scene object; a hold for an object without a shared identity is not sent.</summary>
    [NetworkMessage]
    public class PlacementHoldBeginMessage : INetMessage
    {
        /// <summary>The live object being held.</summary>
        public InteractableObject Entity;

        /// <summary>Connection id of the holder, stamped by the host (0 = the host itself).</summary>
        public int MoverConnectionId;

        /// <summary>The holder's camera at the instant the hold began. This seeds the observer's
        /// target inference before the next presence sample arrives.</summary>
        public bool HasCamera;
        public Vector3 CameraPosition;
        public Quaternion CameraRotation;

        /// <summary>The holder's placement reach, so the observer raycasts as far as the mover.</summary>
        public float RayDistance;

        /// <summary>The holder's local DisableSnap input state at the start of the hold.</summary>
        public bool StopSnap;

        /// <summary>The holder's vertical-flip state at the start of the hold.</summary>
        public bool IsFlipped;
    }

    [NetworkMessage]
    public sealed class PlacementHoldBeginRequestMessage : PlacementHoldBeginMessage
    {
    }

    /// <summary>A player put a held placement object down, or the hold was released on disconnect.</summary>
    [NetworkMessage]
    public class PlacementHoldEndMessage : INetMessage
    {
        public InteractableObject Entity;
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
        public InteractableObject Entity;
        public Quaternion Rotation;
    }

    [NetworkMessage]
    public sealed class PlacementHoldRotationRequestMessage : PlacementHoldRotationMessage
    {
    }

    /// <summary>Mover-owned input state that vanilla normally reads from the local controller.
    /// Sent only when it changes so an observer can run the holder's snap/flip behavior.</summary>
    [NetworkMessage]
    public class PlacementHoldStateMessage : INetMessage
    {
        public InteractableObject Entity;
        public bool StopSnap;
        public bool IsFlipped;
    }

    [NetworkMessage]
    public sealed class PlacementHoldStateRequestMessage : PlacementHoldStateMessage
    {
    }
}
