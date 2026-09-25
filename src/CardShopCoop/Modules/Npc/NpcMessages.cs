using CardShopCoop.Net;
using CardShopCoop.Net.Protocol;
using UnityEngine;

namespace CardShopCoop.Modules.Npc
{
    /// <summary>Reliable authoritative NPC baseline sent once when a peer joins.</summary>
    [NetworkMessage]
    public sealed class NpcBaselineMessage : INetMessage
    {
        public float HostTime;
        public int CustomerCapacity = -1;
        public System.Collections.Generic.List<bool> CustomerFemales;
        public System.Collections.Generic.List<NpcEntry> Entries = new();
    }

    /// <summary>One transient interpolated state update for one NPC incarnation.</summary>
    [NetworkMessage(Reliability = Reliability.Transient)]
    public sealed class NpcStateDeltaMessage : INetMessage
    {
        public float HostTime;
        public byte Kind;
        public ushort Index;
        public int Identity;
        public bool Active;
        public Vector3 Position;
        public float Yaw;
        public float Speed;
        public ushort Flags;
        public int ActionSequence;
        public byte ActionKind;
        public bool HoldBig;
        public EItemType HoldItemType;
        /// <summary>Stable world box network id the worker is carrying, or 0. Lets the client
        /// remove the real warehouse box object the worker took and drive the held box prop.</summary>
        public long HoldBoxNetworkId;
        /// <summary>Open/closed state of the carried box.</summary>
        public bool HoldBoxOpened;
    }

    /// <summary>Reliable identity update for one pooled NPC incarnation.</summary>
    [NetworkMessage]
    public sealed class NpcIdentityDeltaMessage : INetMessage
    {
        public byte Kind;
        public ushort Index;
        public int Identity;
        public bool Female;
        public string CharName;
        public Vector3 Position;
    }

    // Host -> client (reliable / one-shot). The identity binds the bubble to the
    // current pooled customer incarnation, rather than a recycled list slot.
    [NetworkMessage]
    public sealed class NpcSpeechMessage : INetMessage
    {
        public byte Kind;
        public ushort Index;
        public int Identity;
        public string Text;
        public float OffsetUp;
    }

    [NetworkMessage]
    public sealed class NpcMoneyPopupMessage : INetMessage
    {
        public ushort Index;
        public int Identity;
        public float Amount;
        public float OffsetUp;
    }

    /// <summary>One NPC in the join baseline. Flags is the raw wire ushort (the game's NpcFlags
    /// is a private nested enum in NpcClientBehaviour).</summary>
    public sealed class NpcEntry
    {
        public byte Kind;
        public ushort Index;
        public int Identity;
        public bool Female;
        public bool HasName;
        public string CharName;   // only present when HasName
        public Vector3 Position;
        public float Yaw;
        public float Speed;
        public ushort Flags;
        public int ActionSequence;
        public byte ActionKind;
        public bool HoldBig;
        public EItemType HoldItemType;
        /// <summary>Stable world box network id the worker is carrying, or 0. Lets the client
        /// remove the real warehouse box object the worker took and drive the held box prop.</summary>
        public long HoldBoxNetworkId;
        /// <summary>Open/closed state of the carried box.</summary>
        public bool HoldBoxOpened;
    }
}
