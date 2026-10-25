using System;
using UnityEngine;

namespace CardShopCoop.Modules.Npc
{
    /// <summary>
    /// One authoritative NPC incarnation snapshot. The host builds it from the live NPC and the
    /// identity module turns it into an <c>NpcEntityDescriptor</c>; the client uses the same shape
    /// for the initial spawn and for the transient pose stream. It is deliberately not a network
    /// type: lifecycle lives in <c>EntityDescriptor</c>, state lives in
    /// <see cref="NpcStateDeltaMessage"/>.
    /// </summary>
    internal struct NpcSnapshot
    {
        /// <summary>0 = customer, 1 = worker. Matches the identity module's NPC descriptor
        /// kind; the transient state message derives it from the live object instead.</summary>
        public byte Kind;
        public ushort Index;
        public int Identity;
        public bool Female;
        public string CharName;
        public Vector3 Position;
        public float Yaw;
        public float Speed;
        public ushort Flags;
        public int ActionSequence;
        public byte ActionKind;
        public bool HoldBig;
        public EItemType HoldItemType;
        /// <summary>The live box behind <see cref="HoldBoxNetworkId"/>. Transient state deltas
        /// carry this object; lifecycle descriptors keep the stable id.</summary>
        public InteractablePackagingBox HoldBox;
        public Guid HoldBoxNetworkId;
        public bool HoldBoxOpened;
        /// <summary>Host timestamp for the pose. State deltas carry their own; a spawn snapshot
        /// uses the local clock so the first pose seeds interpolation at the current time.</summary>
        public float HostTime;
    }
}
