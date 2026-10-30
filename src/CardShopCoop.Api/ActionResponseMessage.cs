using System;
using CardShopCoop.Net;

namespace CardShopCoop.Api
{
    /// <summary>
    /// The host's answer to one <see cref="ActionMessage"/>. A response is a normal reliable
    /// message; the framework fills <see cref="ActionId"/> from the request and sets
    /// <see cref="Rejected"/> from the handler's <see cref="ActionOutcome"/>.
    ///
    /// A custom response carries state that only the acting client needs - typically the host's
    /// post-action state - and derives from this type. Responses never require a
    /// <c>[MessageHandler]</c>: the framework gives them to the action's
    /// <see cref="ActionMessage.OnUndo"/> and <see cref="ActionMessage.OnMerge"/> hooks.
    /// </summary>
    public abstract class ActionResponseMessage : INetMessage
    {
        /// <summary>Correlation id copied from the <see cref="ActionMessage"/>; filled by the
        /// framework.</summary>
        public Guid ActionId;

        /// <summary>True when the host rejected the action; filled by the framework.</summary>
        public bool Rejected;
    }
}
