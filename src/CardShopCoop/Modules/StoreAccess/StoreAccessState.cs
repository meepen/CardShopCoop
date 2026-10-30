using System;
using CardShopCoop.Api;

namespace CardShopCoop.Modules.StoreAccess
{
    /// <summary>
    /// The host-authoritative state of the two physical access signs. This is session-wide rather
    /// than attached to a sign object: the gameplay flags live in <c>CPlayerData</c> and the
    /// customer/worker simulation reads them even when the visible sign is absent.
    ///
    /// Host-only: a client writes through <see cref="StoreAccessToggleIntent"/>, so the validated
    /// intent lane is the only way to change it. <see cref="ActionId"/> and <see cref="Rejected"/>
    /// let the client's extended-information handler settle the optimistic toggle it predicted.
    /// </summary>
    [GlobalInformation]
    [HostOnlyInformation]
    internal sealed class StoreAccessState : IActionValue
    {
        public bool IsShopOpen
        {
            get; init;
        }

        public bool IsWarehouseDoorClosed
        {
            get; init;
        }

        public Guid ActionId
        {
            get; init;
        }

        public bool Rejected
        {
            get; init;
        }
    }
}
