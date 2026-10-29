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
    /// prediction is recorded by the client's action-ledger closures (they capture the pre-change
    /// snapshot and the kind's authoritative version), so the framework hooks stay empty.</summary>
    [NetworkMessage]
    public sealed class HudContributionIntent : ActionMessage
    {
        public HudContributionKind Kind;
        public float Value;

        public override void OnApply()
        {
        }

        public override void OnUndo(ActionResponseMessage response)
        {
        }
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

    /// <summary>Host-authoritative wallet. State, not a command: the host owns every mutation.
    /// <see cref="ActionId"/> retires the contribution this absolute answers.</summary>
    [NetworkMessage]
    public sealed class HudWalletDeltaMessage : INetMessage
    {
        public Guid ActionId;
        public double Coins;
        public float CoinDisplay;
    }

    /// <summary>Host-authoritative shop experience/level. State, not a command.</summary>
    [NetworkMessage]
    public sealed class HudProgressDeltaMessage : INetMessage
    {
        public Guid ActionId;
        public int Experience;
        public int Level;
    }

    /// <summary>Host-authoritative fame. State, not a command.</summary>
    [NetworkMessage]
    public sealed class HudFameDeltaMessage : INetMessage
    {
        public Guid ActionId;
        public int Fame;
    }
}
