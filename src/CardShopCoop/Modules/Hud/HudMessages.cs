using System;
using CardShopCoop.Modules.Prediction;
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

    [NetworkMessage]
    public sealed class HudContributionIntent : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public HudContributionKind Kind;
        public float Value;
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

    [NetworkMessage]
    public sealed class HudWalletDeltaMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public double Coins;
        public float CoinDisplay;
    }

    [NetworkMessage]
    public sealed class HudProgressDeltaMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public int Experience;
        public int Level;
    }

    [NetworkMessage]
    public sealed class HudFameDeltaMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public int Fame;
    }
}
