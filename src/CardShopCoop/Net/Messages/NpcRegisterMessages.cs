using CardShopCoop.Net;
using CardShopCoop.Sync;
using UnityEngine;

namespace CardShopCoop.Net.Messages
{
    // ---- NpcState -------------------------------------------------------------
    // host -> client (unreliable / transient). One instance per chunk produced by
    // NpcSync.HostCollect: [float hostTime][byte count][count x NpcEntry].
    // Mirrors NpcSync.WriteEntry / NpcSync.ApplyBatch exactly.

    [NetworkMessage(MsgType.NpcState, Policy = MessagePolicy.ClientOnly, Delivery = Delivery.Transient)]
    public sealed class NpcStateMessage : INetMessage
    {
        public float HostTime;
        public System.Collections.Generic.List<NpcEntry> Entries = new System.Collections.Generic.List<NpcEntry>();

        public MsgType Type { get { return MsgType.NpcState; } }


    }

    // Host -> client (reliable / one-shot). The identity binds the bubble to the
    // current pooled customer incarnation, rather than a recycled list slot.
    [NetworkMessage(MsgType.NpcSpeech, Policy = MessagePolicy.ClientOnly)]
    public sealed class NpcSpeechMessage : INetMessage
    {
        public byte Kind;
        public ushort Index;
        public int Identity;
        public string Text;
        public float OffsetUp;

        public MsgType Type { get { return MsgType.NpcSpeech; } }


    }

    /// <summary>One NPC snapshot within an NpcState batch. Flags is the raw wire byte
    /// (the game's NpcFlags is a private nested enum in NpcSync).</summary>
    public sealed class NpcEntry
    {
        public byte Kind;
        public ushort Index;
        public int Identity;
        public bool HasName;
        public string CharName;   // only present when HasName
        public Vector3 Position;
        public float Yaw;
        public float Speed;
        public byte Flags;
        public int ActionSequence;
        public byte ActionKind;
    }

    // ---- RegisterState --------------------------------------------------------
    // host -> client (reliable). Observer checkout state: which player mans each
    // counter. Mirrors RegisterSync.WriteStates / ClientApplyState:
    // [byte count][count x (byte index, byte manned)].

    [NetworkMessage(MsgType.RegisterState, Policy = MessagePolicy.ClientOnly)]
    public sealed class RegisterStateMessage : INetMessage
    {
        public System.Collections.Generic.List<RegisterStateEntry> Entries = new System.Collections.Generic.List<RegisterStateEntry>();

        public MsgType Type { get { return MsgType.RegisterState; } }


    }

    /// <summary>0 = unmanned, 1 = host player, 2 = another guest.</summary>
    public sealed class RegisterStateEntry
    {
        public byte Index;
        public byte Manned;
    }

    // ---- RegisterCart ---------------------------------------------------------
    // host -> client (reliable). Authoritative bag + prices + phase + scanned slots
    // for every counter that currently has an active customer. Mirrors
    // RegisterSync.WriteCarts / ClientApplyCart:
    // [byte count][count x entry]
    //   entry = (byte index, int customerId) then, when customerId != 0:
    //     (ushort customerIndex, int customerGeneration, string characterName,
    //      byte state, bool isCard, double paidAmount, double totalScanned,
    //      float customerTotalScanned,
    //      byte itemCount, (EItemType, float price) x itemCount, bool scanned x itemCount,
    //      byte cardCount, (CardData, float price) x cardCount, bool scanned x cardCount).

    [NetworkMessage(MsgType.RegisterCart, Policy = MessagePolicy.ClientOnly)]
    public sealed class RegisterCartMessage : INetMessage
    {
        public System.Collections.Generic.List<RegisterCartEntry> Entries = new System.Collections.Generic.List<RegisterCartEntry>();

        public MsgType Type { get { return MsgType.RegisterCart; } }


    }

    /// <summary>One counter's authoritative cart digest. CustomerId is the opaque
    /// change token (0 = customer left; no other fields are present).</summary>
    public sealed class RegisterCartEntry
    {
        public byte Index;
        public int CustomerId;
        public ushort CustomerIndex;
        public int CustomerGeneration;
        public string CharacterName;
        public byte State;
        public bool IsCard;
        public double PaidAmount;
        public double TotalScanned;
        public float CustomerTotalScanned;
        public System.Collections.Generic.List<EItemType> ItemTypes = new System.Collections.Generic.List<EItemType>();
        public System.Collections.Generic.List<float> ItemPrices = new System.Collections.Generic.List<float>();
        public System.Collections.Generic.List<bool> ItemScanned = new System.Collections.Generic.List<bool>();
        public System.Collections.Generic.List<CardData> Cards = new System.Collections.Generic.List<CardData>();
        public System.Collections.Generic.List<float> CardPrices = new System.Collections.Generic.List<float>();
        public System.Collections.Generic.List<bool> CardScanned = new System.Collections.Generic.List<bool>();
    }

    // ---- RegisterOp -----------------------------------------------------------
    // client -> host (reliable). The manning player's register action; replayed on
    // the host through vanilla public methods. Mirrors RegisterSync.HostApplyOp and
    // the client-side emitters: [byte index][byte op][op-specific payload].

    [NetworkMessage(MsgType.RegisterOp, Policy = MessagePolicy.HostOnly)]
    public sealed class RegisterOpMessage : INetMessage
    {
        public byte Index;
        public byte Op;
        public byte BagIndex;      // OpScanItem / OpScanCard
        public bool IsCard;        // OpTakingPayment / OpTookPayment
        public double PaidAmount;  // OpTakingPayment (host's own payment is authoritative)
        public int ChangeIndex;    // OpGiveChange
        public double ChangeValue; // OpGiveChange
        public bool TakingBack;    // OpGiveChange
        public double TotalAmount; // OpFinishCard (EvaluateCreditCard total)

        public MsgType Type { get { return MsgType.RegisterOp; } }


    }
}
