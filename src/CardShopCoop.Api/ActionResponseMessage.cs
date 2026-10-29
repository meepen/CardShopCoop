using System;
using CardShopCoop.Net;

namespace CardShopCoop.Api
{
    /// <summary>
    /// The host's answer to one <see cref="ActionMessage"/>. A response is a normal reliable
    /// message; the framework fills <see cref="ActionId"/> from the request and sets
    /// <see cref="Rejected"/> from the handler's <see cref="ActionOutcome"/>.
    ///
    /// Responses are intercepted by the client's action ledger and never require a
    /// <c>[MessageHandler]</c>: accepted responses call <see cref="ActionMessage.OnMerge"/>,
    /// rejected responses call <see cref="ActionMessage.OnUndo"/>.
    /// </summary>
    public abstract class ActionResponseMessage : INetMessage
    {
        public Guid ActionId;
        public bool Rejected;
    }
}
