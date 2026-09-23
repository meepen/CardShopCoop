using System;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Shop
{
    [NetworkMessage]
    public sealed class ShopRenameRequestMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public string Name;
    }

    [NetworkMessage]
    public sealed class ShopRenameDeltaMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public string Name;

        /// <summary>True only for the canonical name sent to a freshly joined guest. A baseline
        /// is not a rename: it must not replay the host's tutorial/entitlement naming side
        /// effects, which would advance the guest's tutorial behind the host's back.</summary>
        public bool Initial;
    }
}
