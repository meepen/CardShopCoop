using CardShopCoop.Net;
using UnityEngine;

namespace CardShopCoop.Net.Messages
{
    [NetworkMessage(MsgType.EconContrib, Policy = MessagePolicy.HostOnly)]
    public sealed class EconContributionMessage : INetMessage
    {
        public byte Kind;
        public float Value;
        public MsgType Type { get { return MsgType.EconContrib; } }
    }

    [NetworkMessage(MsgType.SprayHit, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class SprayHitMessage : INetMessage
    {
        public Vector3 Position;
        public float Range;
        public int Potency;
        public MsgType Type { get { return MsgType.SprayHit; } }
    }

    [NetworkMessage(MsgType.GradedRemove)]
    public sealed class GradedRemoveMessage : INetMessage
    {
        public CardData Card;
        public MsgType Type { get { return MsgType.GradedRemove; } }
    }

    [NetworkMessage(MsgType.CardPriceSet)]
    public sealed class CardPriceSetMessage : INetMessage
    {
        public CardData Card;
        public float Price;
        public MsgType Type { get { return MsgType.CardPriceSet; } }
    }

    [NetworkMessage(MsgType.LicenseUnlock)]
    public sealed class LicenseUnlockMessage : INetMessage
    {
        public EItemType ItemType;
        public bool IsBig;
        public string RestockName;
        public MsgType Type { get { return MsgType.LicenseUnlock; } }
    }

    [NetworkMessage(MsgType.ItemPriceContrib, Policy = MessagePolicy.HostOnly)]
    public sealed class ItemPriceContribMessage : INetMessage
    {
        public EItemType ItemType;
        public float Price;
        public MsgType Type { get { return MsgType.ItemPriceContrib; } }
    }
}
