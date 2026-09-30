using System;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Prediction
{
    [NetworkMessage]
    public sealed class PredictionRollbackMessage : INetMessage
    {
        public Guid PredictionId;
    }
}
