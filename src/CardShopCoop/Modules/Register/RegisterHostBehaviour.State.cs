using System;
using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.ExtendedInformation;
using CardShopCoop.Modules.Presence;
using CardShopCoop.Net.Connection;

namespace CardShopCoop.Modules.Register
{
    /// <summary>Host-side register state. It observes the game's own counter/customer data,
    /// builds one immutable snapshot, and writes it through the object-information store. The
    /// store owns the previous value, the wire, the baseline and the client apply; this file only
    /// decides what the next snapshot contains.</summary>
    public sealed partial class RegisterHostBehaviour
    {
        private bool _insideCustomerArrival;

        /// <summary>True while this host may observe the game. The framework's host-apply scope
        /// suppresses observation so the handler publishes exactly one snapshot after the action.</summary>
        internal bool CanObserve => !_shutdown && !RegisterActions.HostApplying
            && RuntimeContext.InGame();

        private static RegisterSnapshot StateOf(InteractableCashierCounter counter)
            => counter == null ? null : counter.GetExtendedInformation<RegisterSnapshot>();

        /// <summary>Seeds the store for counters that already exist when the host session starts.
        /// New counters publish from the counter-added hook; the store then owns the join baseline,
        /// so no join-time rebroadcast is needed (which could overwrite a live action on
        /// existing guests).</summary>
        private void PublishAllCounters(bool seed = false)
        {
            if (_shutdown || !RuntimeContext.InGame()
                || RegisterInterop.Counters == null)
            {
                return;
            }

            var counters = RegisterInterop.Counters;
            for (var i = 0; i < counters.Count; i++)
            {
                var counter = counters[i];
                if (counter != null)
                {
                    PublishNow(counter, ResolveOwner(counter), Guid.Empty, seed: seed);
                }
            }
        }

        /// <summary>Builds one immutable snapshot from the previous value and the live game state.
        /// Only an active customer is stored; an inactive pooled body is normalized to idle, so no
        /// register state ever references a body after it is available for reuse.</summary>
        private static RegisterState BuildState(InteractableCashierCounter counter, PlayerRef owner)
        {
            var customer = counter.m_CurrentCustomer;
            if (customer != null && !customer.m_IsActive)
            {
                customer = null;
            }

            return new RegisterState
            {
                Owner = owner,
                Customer = customer,
                Phase = BuildPhase(counter, customer),
                Lines = customer == null ? Array.Empty<RegisterLine>() : BuildLines(customer),
                ChangeItems = customer == null
                    ? Array.Empty<RegisterChange>() : BuildChanges(counter),
            };
        }

        /// <summary>The host's own player reference. Ownership is a roster value, never a raw
        /// connection id; the current roster's self entry is the host. The fallback keeps a
        /// host-owned counter publishable during the small window before the presence roster is
        /// built.</summary>
        private PlayerRef HostSelf()
            => ((ICoopContext)RuntimeContext)?.Self ?? new PlayerRef(0, null, true, true);

        /// <summary>Reads the effective owner from the authoritative snapshot and the game's manned
        /// state. A snapshot from before the manning hook still resolves the host as the owner, and
        /// a host that has left the counter resolves back to free.</summary>
        private PlayerRef ResolveOwner(InteractableCashierCounter counter)
        {
            if (counter == null)
            {
                return null;
            }

            var owner = StateOf(counter)?.State?.Owner;
            if (owner == null)
            {
                return counter.IsMannedByPlayer() ? HostSelf() : null;
            }

            return owner.IsHost && !counter.IsMannedByPlayer() ? null : owner;
        }

        private static RegisterPhase BuildPhase(InteractableCashierCounter counter, Customer customer)
            => new()
            {
                State = counter.m_CashierCounterState,
                UsingCard = RegisterInterop.IsUsingCard(counter),
                Paid = RegisterInterop.Paid(counter),
                Total = RegisterInterop.Total(counter),
                CustomerTotal = customer == null ? 0d : Convert.ToDouble(RegisterInterop.Read(
                    customer, "m_TotalScannedItemCost") ?? 0f),
                Change = RegisterInterop.Change(counter),
                ChangeReady = RegisterInterop.IsChangeReady(counter),
                ChangeStarted = RegisterInterop.ChangeStarted(counter),
                TooMuchChange = RegisterInterop.TooMuchChange(counter),
            };

        private static RegisterLine[] BuildLines(Customer customer)
        {
            var lines = new List<RegisterLine>();
            // A bag item is registered for scanning (InteractableScanItem.m_CurrentCustomer set)
            // only once the customer reaches the paying position, and IsNotScanned() is
            // m_CurrentCustomer != null - so an item that was simply never registered reads
            // exactly like "already scanned". Two windows can publish a lifecycle while the bag is
            // not yet registered: a re-anchor sent while the customer is still walking to the
            // counter, and the arrival announcement itself (UpdateCurrentCustomer runs before
            // RegisterScanItem in Customer.WaypointEndUpdate - see the arrival hook, which owns
            // the arrival publish). Gate the flag on the customer actually being at the paying
            // position so both windows read every line as un-scanned.
            var atPayingPosition = RegisterInterop.Read(customer, "m_IsAtPayingPosition") is bool atPaying
                && atPaying;
            var items = customer.GetItemInBagList();
            for (var i = 0; items != null && i < items.Count; i++)
            {
                var item = items[i];
                lines.Add(new RegisterLine
                {
                    ItemType = item == null ? EItemType.None : item.GetItemType(),
                    Price = item == null ? 0f : item.GetCurrentPrice(),
                    Scanned = atPayingPosition && item != null && item.m_InteractableScanItem != null
                        && !item.m_InteractableScanItem.IsNotScanned(),
                });
            }

            var cards = customer.GetCardInBagList();
            for (var i = 0; cards != null && i < cards.Count; i++)
            {
                var card = cards[i];
                lines.Add(new RegisterLine
                {
                    IsCard = true,
                    Card = card?.m_Card3dUI?.m_CardUI?.GetCardData(),
                    Price = card == null ? 0f : card.GetCurrentPrice(),
                    Scanned = atPayingPosition && card != null && !card.IsNotScanned(),
                });
            }

            return lines.ToArray();
        }

        private static RegisterChange[] BuildChanges(InteractableCashierCounter counter)
        {
            var changes = new List<RegisterChange>();
            var money = counter.m_InteractableCounterMoneyChangeList;
            for (var i = 0; money != null && i < money.Count; i++)
            {
                var change = money[i];
                var count = change == null ? 0 : RegisterInterop.GivenAmount(change);
                if (change != null && count > 0)
                {
                    changes.Add(new RegisterChange
                    {
                        Slot = change.m_Index,
                        IsCoin = change.m_IsCoin,
                        Value = change.m_ValueDouble,
                        Count = count,
                    });
                }
            }

            return changes.ToArray();
        }

        /// <summary>Writes one snapshot through the object-information store. That store owns the
        /// baseline, the host broadcast, and the client apply.</summary>
        private void PublishNow(InteractableCashierCounter counter, PlayerRef owner, Guid actionId,
            bool rejected = false, bool seed = false)
        {
            if (_shutdown || counter == null || !RuntimeContext.InGame())
            {
                return;
            }

            var state = BuildState(counter, owner);
            var snapshot = new RegisterSnapshot
            {
                State = state,
                ActionId = actionId,
                Rejected = rejected,
            };
            if (seed)
            {
                // Session-start seeding runs before the transport exists. The normal host path
                // broadcasts and throws "no live transport"; store the value silently and let
                // the generic join baseline (and SeedForJoin) publish it.
                ExtendedInformationModule.Seed(typeof(RegisterSnapshot), counter, snapshot);
                return;
            }

            counter.SetExtendedInformation(snapshot);
        }

        /// <summary>Publishes a host-originated snapshot. Called by the game-observation hooks; an
        /// intent application suppresses these so the intent publishes once, after the action.</summary>
        private void Publish(InteractableCashierCounter counter)
        {
            if (RegisterActions.HostApplying || counter == null)
            {
                return;
            }

            PublishNow(counter, ResolveOwner(counter), Guid.Empty);
        }

        /// <summary>Publishes an explicit ownership decision from a game hook. The intent lane
        /// calls <see cref="PublishNow"/> directly after it clears the applying scope.</summary>
        internal void PublishOwnerNow(InteractableCashierCounter counter, PlayerRef owner)
        {
            if (RegisterActions.HostApplying)
            {
                return;
            }

            PublishNow(counter, owner, Guid.Empty);
        }

        internal bool IsOwnedByAnother(InteractableCashierCounter counter)
            => StateOf(counter)?.State?.Owner is { IsHost: false };

        internal bool IsHostOwned(InteractableCashierCounter counter)
            => StateOf(counter)?.State?.Owner?.IsHost == true;

        internal void OnHostManned(InteractableCashierCounter counter)
            => PublishOwnerNow(counter, HostSelf());

        internal void OnHostReleased(InteractableCashierCounter counter)
        {
            if (IsHostOwned(counter))
            {
                PublishOwnerNow(counter, null);
            }
        }

        internal void OnCounterState(InteractableCashierCounter counter) => Publish(counter);

        internal void OnCustomerUpdated(InteractableCashierCounter counter)
        {
            // The one non-null announcement in vanilla is the customer's arrival, and it is always
            // mid-block (before the bag is registered for scanning), so its lifecycle is owned by
            // the arrival hook. Only the customer-left and counter-reset announcements (null, no
            // lines) publish from here.
            if (counter != null && !_insideCustomerArrival)
            {
                Publish(counter);
            }
        }

        internal void BeginCustomerArrival() => _insideCustomerArrival = true;

        internal void EndCustomerArrival() => _insideCustomerArrival = false;

        internal void PublishCustomerArrival(InteractableCashierCounter counter) => Publish(counter);

        internal void OnScanned(InteractableCashierCounter counter) => Publish(counter);

        internal void OnChange(InteractableCounterMoneyChange change)
            => Publish(change?.m_CashierCounter);

        internal void OnCounterAdded(InteractableCashierCounter counter) => Publish(counter);

        internal void OnCounterReset(InteractableCashierCounter counter)
        {
            if (!CanObserve || counter == null || StateOf(counter)?.State?.Owner == null)
            {
                return;
            }

            // Day start force-clears the counter's manned state without OnPressEsc. Clear the
            // authoritative owner so nobody keeps, or is locked out of, a register no one mans.
            PublishNow(counter, null, Guid.Empty);
            RegisterHighlight.Clear(counter);
        }

        /// <summary>Answers a rejected checkout with this counter's authoritative snapshot under
        /// the rejected action id. The requester retires the rejected action and applies this
        /// authority, so the trailing generic rollback is ignored as already-resolved instead of
        /// inverting a snapshot the host has moved past.</summary>
        internal void PublishRejectionState(InteractableCashierCounter counter, Guid actionId)
        {
            if (_shutdown || counter == null || !RuntimeContext.InGame())
            {
                return;
            }

            CoopPlugin.Log.LogInfo("[register] re-anchoring counter " + counter.name
                + " action=" + actionId + ".");
            PublishNow(counter, ResolveOwner(counter), actionId, rejected: true);
        }

        [OnClientDisconnected]
        private void Disconnected(PeerConnection connection, DisconnectInfo _)
        {
            if (connection == null || RegisterInterop.Counters == null)
            {
                return;
            }

            var counters = RegisterInterop.Counters;
            var player = PresenceApi.ResolvePlayerRef(connection.Id)
                ?? new PlayerRef(connection.Id, connection.Name, connection.Id == 0, false);
            for (var i = 0; i < counters.Count; i++)
            {
                var counter = counters[i];
                var owner = StateOf(counter)?.State?.Owner;
                if (owner != null && owner.Equals(player))
                {
                    PublishNow(counter, null, Guid.Empty);
                }
            }
        }
    }
}
