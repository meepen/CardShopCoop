using System;
using CardShopCoop.Api;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.StoreAccess
{
    /// <summary>
    /// Client intent for one access sign. Zero is the shop open/closed sign; one is the warehouse
    /// customer-entry sign. The host applies the vanilla click, then publishes the resulting
    /// <see cref="StoreAccessState"/>; a denied or no-op click publishes the authoritative state
    /// under this action id and rolls the client's optimistic toggle back.
    /// </summary>
    [NetworkMessage]
    public sealed class StoreAccessToggleIntent : ActionMessage
    {
        public byte Which;

        /// <summary>The local flag before the click. Restores the prediction when the host
        /// refuses the toggle.</summary>
        [NonSerialized]
        public bool Before;

        public override void OnUndo(ActionResponseMessage response)
        {
            StoreAccessClientBehaviour.ApplyLocal(Which, Before);
        }

        public override void OnMerge(ActionResponseMessage response)
        {
            // StoreAccessState is an IActionValue: the extended-information handler projects the
            // host's post-toggle state onto the flags and signs before this hook.
            Before = false;
        }
    }
}
