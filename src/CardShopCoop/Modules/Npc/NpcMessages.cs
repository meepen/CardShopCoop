using CardShopCoop.Net;
using CardShopCoop.Net.Protocol;
using UnityEngine;

namespace CardShopCoop.Modules.Npc
{
    /// <summary>One transient interpolated state update for one NPC incarnation. Creation and
    /// removal are identity lifecycle messages; this stream only moves and poses an NPC that the
    /// identity module already created. The live object is the only identity on the wire: the
    /// client derives kind, pool index and incarnation from it through the identity module and
    /// drops the delta when the object cannot be resolved.</summary>
    [NetworkMessage(Reliability = Reliability.Transient)]
    public sealed class NpcStateDeltaMessage : INetMessage
    {
        public float HostTime;
        // Exactly one of these is set to the live NPC the delta describes.
        public Customer Customer;
        public Worker Worker;
        public Vector3 Position;
        public float Yaw;
        public float Speed;
        public ushort Flags;
        public int ActionSequence;
        public byte ActionKind;
        public bool HoldBig;
        public EItemType HoldItemType;
        /// <summary>The live world box the worker is carrying, or null when the worker holds no
        /// box. The client drives the real warehouse box and the held box prop from this object;
        /// there is no box-id fallback on the wire.</summary>
        public InteractablePackagingBox HoldBox;
        /// <summary>Open/closed state of the carried box.</summary>
        public bool HoldBoxOpened;
    }

    // Host -> client (reliable / one-shot). The live customer is the only identity on the wire;
    // the client drops the popup when it cannot resolve that object.
    [NetworkMessage]
    public sealed class NpcSpeechMessage : INetMessage
    {
        public Customer Customer;
        public string Text;
        public float OffsetUp;
    }

    [NetworkMessage]
    public sealed class NpcMoneyPopupMessage : INetMessage
    {
        public Customer Customer;
        public float Amount;
        public float OffsetUp;
    }
}
