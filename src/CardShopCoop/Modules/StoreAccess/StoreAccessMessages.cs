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

        /// <summary>The client's ledger replay/undo closures own the optimistic toggle; the
        /// message is only the host command.</summary>
        public override void OnApply()
        {
        }

        public override void OnUndo(ActionResponseMessage response)
        {
        }
    }
}
