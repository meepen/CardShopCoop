using System;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Actions
{
    [NetworkMessage]
    public sealed class ActionRollbackMessage : INetMessage
    {
        public Guid ActionId;
        /// <summary>True for a rejection (undo the optimistic local action). False when the host
        /// applied the action but its handler failed before publishing authority: retire the local
        /// action without undoing it.</summary>
        public bool Undo = true;
    }
}
