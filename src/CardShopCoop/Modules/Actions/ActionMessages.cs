using System;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Actions
{
    [NetworkMessage]
    public sealed class ActionRollbackMessage : INetMessage
    {
        public Guid ActionId;
    }
}
