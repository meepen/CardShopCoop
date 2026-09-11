using System.Collections.Generic;
using CardShopCoop.Sync;
using UnityEngine;

namespace CardShopCoop.Net.Messages
{
    /// <summary>Client -> host: one box possession update.
    /// A claim is a Held/Placing update on a Free box; a release is Free; a removal
    /// is Removed. The host validates ownership from its lease map.</summary>
    [NetworkMessage(MsgType.BoxUpdate, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class BoxUpdateMessage : INetMessage
    {
        public BoxWire Box;

        public MsgType Type
        {
            get
            {
                return MsgType.BoxUpdate;
            }
        }
    }

    /// <summary>Host -> clients: the authoritative box state (all families in one list).</summary>
    [NetworkMessage(MsgType.BoxSnapshot, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class BoxSnapshotMessage : INetMessage
    {
        public List<BoxWire> Boxes = new List<BoxWire>();
        /// <summary>True for a complete snapshot (the client may sweep absent ids). False for
        /// a partial: only the listed boxes changed and the rest are unchanged.</summary>
        public bool Full;

        public MsgType Type
        {
            get
            {
                return MsgType.BoxSnapshot;
            }
        }
    }

    /// <summary>Client -> host: the guest finished loading the borrowed world and needs
    /// authoritative state resent. Join-time snapshots are emitted during world transfer, before
    /// the guest can apply them.</summary>
    [NetworkMessage(MsgType.JoinResyncRequest, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class JoinResyncRequestMessage : INetMessage
    {
        public MsgType Type
        {
            get
            {
                return MsgType.JoinResyncRequest;
            }
        }
    }

    /// <summary>Client -> host: the local player is physically pushing this box. A transient
    /// pose/velocity stream (self-replacing, replaced by the next frame or the settle's
    /// reliable Free) so the host can mirror the push without a possession edge. The host
    /// validates that the box is loose (Free, not stored, not held) before accepting it.</summary>
    [NetworkMessage(MsgType.BoxMotion, Policy = MessagePolicy.HostOnlyInGame, Delivery = Delivery.Transient)]
    public sealed class BoxMotionMessage : INetMessage
    {
        public ushort Id;
        public Vector3 Pos;
        public float Yaw;
        public Vector3 Velocity;
        public Vector3 AngularVelocity;

        public MsgType Type
        {
            get
            {
                return MsgType.BoxMotion;
            }
        }
    }

    /// <summary>Host -> clients: the authoritative/relayed motion of a box a player is pushing.
    /// <see cref="DriverConn"/> is the host-side conn id of the player driving the push
    /// (0 = the host player); the driver ignores its own relay and every other peer smooths
    /// the box from it. Transient: a lost frame is replaced by the next one.</summary>
    [NetworkMessage(MsgType.BoxMotionState, Policy = MessagePolicy.ClientOnlyInGame, Delivery = Delivery.Transient)]
    public sealed class BoxMotionStateMessage : INetMessage
    {
        public ushort Id;
        public int DriverConn;
        public Vector3 Pos;
        public float Yaw;
        public Vector3 Velocity;
        public Vector3 AngularVelocity;

        public MsgType Type
        {
            get
            {
                return MsgType.BoxMotionState;
            }
        }
    }
}
