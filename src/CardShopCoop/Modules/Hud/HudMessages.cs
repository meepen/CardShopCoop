using System;
using CardShopCoop.Api;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Hud
{
    public enum HudContributionKind : byte
    {
        AddCoin = 1,
        ReduceCoin = 2,
        AddShopExperience = 3,
        AddFame = 4
    }

    /// <summary>Client command: one economy contribution the guest already applied locally. The
    /// pre-change snapshot and the kind's authoritative version are local undo state;
    /// <see cref="OnMerge"/> applies the host's absolute snapshot.</summary>
    [NetworkMessage]
    public sealed class HudContributionIntent : ActionMessage
    {
        public HudContributionKind Kind;
        public float Value;

        [NonSerialized]
        public HudAuthoritativeState Previous;

        [NonSerialized]
        public int Version;

        public override void OnUndo(ActionResponseMessage response)
            => HudClientBehaviour.Active?.UndoContribution(this);

        public override void OnMerge(ActionResponseMessage response)
            => HudClientBehaviour.Active?.MergeContribution(this, response);
    }

    /// <summary>The host's absolute wallet, progress and fame after one contribution. The
    /// framework sets <see cref="ActionResponseMessage.Rejected"/>; the values are authoritative
    /// whether the contribution was accepted or refused.</summary>
    [NetworkMessage]
    public sealed class HudContributionResponse : ActionResponseMessage
    {
        public HudAuthoritativeState State = new();
    }

    [NetworkMessage]
    public sealed class HudAuthoritativeState : INetMessage
    {
        public double Coins;
        public float CoinDisplay;
        public int Experience;
        public int Level;
        public int Fame;
    }

    /// <summary>Host-authoritative wallet. State, not a command: the host owns every mutation.</summary>
    [NetworkMessage]
    public sealed class HudWalletDeltaMessage : INetMessage
    {
        public double Coins;
        public float CoinDisplay;
    }

    /// <summary>Host-authoritative shop experience/level. State, not a command.</summary>
    [NetworkMessage]
    public sealed class HudProgressDeltaMessage : INetMessage
    {
        public int Experience;
        public int Level;
    }

    /// <summary>Host-authoritative fame. State, not a command.</summary>
    [NetworkMessage]
    public sealed class HudFameDeltaMessage : INetMessage
    {
        public int Fame;
    }
}
