using System;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Prediction
{
    public interface IPredictedMessage : INetMessage
    {
        Guid PredictionId
        {
            get;
            set;
        }
    }

    [NetworkMessage]
    public sealed class PredictionRollbackMessage : INetMessage
    {
        public Guid PredictionId;
    }
}
