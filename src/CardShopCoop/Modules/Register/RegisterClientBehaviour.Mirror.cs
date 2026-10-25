using System;
using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Modules.Npc;
using CardShopCoop.Runtime;
using UnityEngine;

namespace CardShopCoop.Modules.Register
{
    /// <summary>Client local carrier/station projection: rebuild the borrowed NPC, reconcile the
    /// bag and change stack, and mirror the counter phase from one authoritative snapshot.</summary>
    public sealed partial class RegisterClientBehaviour
    {
        private sealed class LocalStation
        {
            public Customer Carrier;

            /// <summary>Local Npc mirror index of <see cref="Carrier"/>, never a wire field.</summary>
            public int CustomerIndex = -1;

            /// <summary>Host incarnation of <see cref="Carrier"/>. The wire reference resolves to
            /// the Npc puppet clone, so this - not object identity - decides whether a stored
            /// station still belongs to the customer a snapshot names.</summary>
            public int CustomerGeneration;
        }

        private LocalStation GetOrBuildStation(InteractableCashierCounter counter,
            Customer customer, IReadOnlyList<RegisterLine> lines)
        {
            // No m_IsActive test: the wire customer is the Npc puppet clone on a guest and the
            // client never activates a customer body. The host already normalized an inactive
            // customer to null before publishing.
            if (counter == null || customer == null
                || !NpcClientBehaviour.TryGetWireIdentity(customer, out var customerIndex,
                    out var customerGeneration))
            {
                return null;
            }

            if (_stations.TryGetValue(counter, out var existing)
                && existing.Carrier != null
                && existing.CustomerIndex == customerIndex
                && existing.CustomerGeneration == customerGeneration
                && CarrierMatchesLines(existing.Carrier, lines))
            {
                EnsureNpcAttachment(customerIndex, customerGeneration, existing.Carrier);
                if (!ReferenceEquals(counter.m_CurrentCustomer, existing.Carrier))
                {
                    // A local completion can clear the counter's customer before the host's
                    // echo; an older snapshot that still names this customer must put the carrier
                    // back on the counter as well as reuse the station.
                    counter.UpdateCurrentCustomer(existing.Carrier);
                    counter.SetPlsaticBagVisibility(true);
                }

                return existing;
            }

            if (_stations.TryGetValue(counter, out var stale) && stale != null)
            {
                // A re-anchor (a rejected checkout's refresh) can replace a station whose bag no
                // longer matches the host's. Log it so a report can be traced to the delta that
                // rebuilt the carrier.
                CoopPlugin.Log.LogInfo("[register] rebuilding station counter=" + counter.name
                    + " (was index=" + stale.CustomerIndex
                    + " bag="
                    + ((stale.Carrier?.GetItemInBagList()?.Count ?? 0)
                        + (stale.Carrier?.GetCardInBagList()?.Count ?? 0))
                    + " -> index=" + customerIndex
                    + " lines=" + (lines?.Count ?? 0) + ").");
            }

            ReleaseStation(counter);
            var carrier = FindCarrier(customer);
            if (carrier == null)
            {
                return null;
            }

            RegisterInterop.PrepareCarrier(carrier, counter);
            // A pooled customer can be reused while it still carries a previous customer's bag.
            // The Lines here are the full authoritative bag for this customer, so clear the
            // carrier first; otherwise the guest accumulates phantom items and its bag indices no
            // longer match the host's, so every scan the guest sends is rejected.
            var staleItems = carrier.GetItemInBagList()?.Count ?? 0;
            var staleCards = carrier.GetCardInBagList()?.Count ?? 0;
            if (staleItems > 0 || staleCards > 0)
            {
                CoopPlugin.Log.LogDebug("[register] cleared stale carrier bag counter="
                    + counter.name + " items=" + staleItems + " cards=" + staleCards + ".");
            }

            RegisterInterop.ReleaseContents(carrier);
            // The carrier's look is not the register's concern: the Npc module's identity layer
            // dressed this pooled customer in place when the host's identity arrived, so the
            // borrow below hands the real, already-dressed NPC the visual role. No register-side
            // preset resolution, no masking, no lookalike clone.

            if (counter.m_QueueStartPos != null)
            {
                carrier.transform.position = counter.m_QueueStartPos.position;
                var facing = counter.transform.position - carrier.transform.position;
                facing.y = 0f;
                carrier.transform.rotation = facing.sqrMagnitude > 0.0001f
                    ? Quaternion.LookRotation(facing, Vector3.up)
                    : counter.m_QueueStartPos.rotation;
            }

            EnsureNpcAttachment(customerIndex, customerGeneration, carrier);
            // AttachExistingCustomer hands the walking puppet's tracked pose and snapshot history
            // to the real body, so it continues the customer's approach frame-for-frame; the
            // queue-start placement above is only the fallback for a carrier with no puppet yet.
            carrier.gameObject.SetActive(true);
            counter.UpdateCurrentCustomer(carrier);
            counter.SetPlsaticBagVisibility(true);
            counter.UpdateCashierCounterState(ECashierCounterState.ScanningItem);

            var itemSlot = 0;
            var cardSlot = 0;
            for (var i = 0; lines != null && i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.IsCard)
                {
                    RegisterInterop.SpawnCard(counter, carrier, line.Card, line.Price, cardSlot++);
                }
                else
                {
                    RegisterInterop.SpawnItem(counter, carrier, line.ItemType, line.Price, itemSlot++);
                }
            }

            var station = new LocalStation
            {
                Carrier = carrier,
                CustomerIndex = customerIndex,
                CustomerGeneration = customerGeneration,
            };
            _stations[counter] = station;
            return station;
        }

        /// <summary>The local pool body for the authoritative customer. A customer that is already
        /// the attached mirror is used directly; otherwise the Npc module's local mirror index
        /// resolves the pool slot. Never a wire index.</summary>
        private static Customer FindCarrier(Customer authoritative)
        {
            if (authoritative == null)
            {
                return null;
            }

            if (NpcClientBehaviour.IsExistingCustomer(authoritative))
            {
                return authoritative;
            }

            if (!NpcClientBehaviour.TryGetWireIdentity(authoritative, out var index, out _))
            {
                return null;
            }

            var customers = SceneRef<CustomerManager>.Get()?.GetCustomerList();
            if (customers == null || index < 0 || index >= customers.Count)
            {
                return null;
            }

            return customers[index];
        }

        private static void EnsureNpcAttachment(int index, int generation, Customer carrier)
        {
            NpcClientBehaviour.SuppressedCustomer.Add(index);
            // Attach with the host's authoritative generation, refreshing an existing mirror when
            // the generation advanced (a reused pooled customer) instead of leaving the stale one.
            if (!NpcClientBehaviour.IsExistingCustomer(carrier)
                || !NpcClientBehaviour.TryGetCustomerGeneration(index, out var current)
                || current != generation)
            {
                // The real NPC is the visual: the Npc module's identity layer has already dressed
                // this pooled customer in place with the host's look, so the borrow parks the
                // walking puppet and the carrier renders itself. No register-side look resolution,
                // masking or clone handoff - the hand-back at detach reveals the puppet again.
                NpcClientBehaviour.AttachExistingCustomer(index, generation, carrier,
                    keepPuppetVisible: false);
            }
        }

        /// <summary>True when the carrier already holds the same bag composition: the same
        /// item/card counts, item types, card data, and prices. Scanned flags deliberately do not
        /// participate: a scan must reconcile in place, not destroy and respawn the whole bag.</summary>
        private static bool CarrierMatchesLines(Customer carrier, IReadOnlyList<RegisterLine> lines)
        {
            var items = carrier?.GetItemInBagList();
            var cards = carrier?.GetCardInBagList();
            if (items == null || cards == null)
            {
                return false;
            }

            var itemCount = 0;
            var cardCount = 0;
            for (var i = 0; lines != null && i < lines.Count; i++)
            {
                if (lines[i] == null)
                {
                    return false;
                }

                if (lines[i].IsCard)
                {
                    cardCount++;
                }
                else
                {
                    itemCount++;
                }
            }

            if (items.Count != itemCount || cards.Count != cardCount)
            {
                return false;
            }

            var itemSlot = 0;
            var cardSlot = 0;
            for (var i = 0; lines != null && i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.IsCard)
                {
                    var card = cards[cardSlot++];
                    var data = card?.m_Card3dUI?.m_CardUI?.GetCardData();
                    if (card == null || data == null || line.Card == null
                        || !data.IsSameCardDataType(line.Card)
                        || Math.Abs(card.GetCurrentPrice() - line.Price) > 0.001f)
                    {
                        return false;
                    }
                }
                else
                {
                    var item = items[itemSlot++];
                    if (item == null || item.GetItemType() != line.ItemType
                        || Math.Abs(item.GetCurrentPrice() - line.Price) > 0.001f)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>Reconciles the host's per-line scanned flags in both directions. A scan is
        /// applied through the game path so its totals/visuals advance; an unscan re-registers the
        /// item/card and rebuilds the cash screen. The absolute totals are written by the phase that
        /// follows, so replay order never has to be perfect. Returns true when the rebuild happened,
        /// so the caller can put the change panel back if the counter is giving change.</summary>
        private static bool ApplyScanned(InteractableCashierCounter counter, Customer customer,
            IReadOnlyList<RegisterLine> lines)
        {
            var items = customer.GetItemInBagList();
            var cards = customer.GetCardInBagList();
            if (items == null || cards == null)
            {
                return false;
            }

            var itemSlot = 0;
            var cardSlot = 0;
            var scannedCount = 0;
            var changed = false;
            for (var i = 0; lines != null && i < lines.Count; i++)
            {
                var line = lines[i];
                if (line == null)
                {
                    continue;
                }

                if (line.IsCard)
                {
                    var card = cardSlot < cards.Count ? cards[cardSlot] : null;
                    if (card != null)
                    {
                        var scanned = !card.IsNotScanned();
                        if (line.Scanned && !scanned)
                        {
                            card.OnMouseButtonUp();
                        }
                        else if (!line.Scanned && scanned)
                        {
                            RegisterInterop.UnscanCard(counter, customer, card);
                            changed = true;
                        }
                    }

                    if (line.Scanned)
                    {
                        scannedCount++;
                    }

                    cardSlot++;
                }
                else
                {
                    var item = itemSlot < items.Count ? items[itemSlot] : null;
                    var scanItem = item?.m_InteractableScanItem;
                    if (scanItem != null)
                    {
                        var scanned = !scanItem.IsNotScanned();
                        if (line.Scanned && !scanned)
                        {
                            scanItem.OnMouseButtonUp();
                        }
                        else if (!line.Scanned && scanned)
                        {
                            RegisterInterop.UnscanItem(counter, customer, item);
                            changed = true;
                        }
                    }

                    if (line.Scanned)
                    {
                        scannedCount++;
                    }

                    itemSlot++;
                }
            }

            RegisterInterop.Write(customer, "m_ItemScannedCount", scannedCount);
            if (changed)
            {
                RebuildCashScreen(counter, customer);
            }

            return changed;
        }

        /// <summary>Applies the authoritative per-denomination change counts as a delta. Every
        /// denomination is addressed (including zero), and <see cref="RegisterInterop.SetGivenAmount"/>
        /// no-ops when the count already matches, so an unchanged snapshot costs nothing. The caller
        /// must ensure the counter is already giving cash change: the game's click path is a no-op in
        /// any other phase.</summary>
        private static void ApplyChangeCounts(InteractableCashierCounter counter,
            RegisterChange[] changes)
        {
            var money = counter?.m_InteractableCounterMoneyChangeList;
            for (var i = 0; money != null && i < money.Count; i++)
            {
                var change = money[i];
                if (change == null)
                {
                    continue;
                }

                var count = 0;
                for (var c = 0; changes != null && c < changes.Length; c++)
                {
                    if (changes[c].Slot == change.m_Index && changes[c].IsCoin == change.m_IsCoin)
                    {
                        count = changes[c].Count;
                        break;
                    }
                }

                RegisterInterop.SetGivenAmount(change, count);
            }
        }

        /// <summary>Rebuilds the given-change stack from an absolute snapshot when the client is
        /// entering the giving-change phase (join, re-anchor, or a rebuilt carrier). The game's
        /// per-denomination click path only works while the counter is already giving cash change,
        /// so reset the whole stack first and replay the counts; the caller restores the
        /// authoritative totals afterwards.</summary>
        private static void RebuildChangeStack(InteractableCashierCounter counter,
            RegisterChange[] changes)
        {
            var money = counter?.m_InteractableCounterMoneyChangeList;
            if (money == null)
            {
                return;
            }

            // ResetAmountGiven clears each denomination's count and its stack visuals but does NOT
            // roll m_CurrentMoneyChangeValue or the coin/bill added counters back, so zero the
            // derived totals before replaying.
            RegisterInterop.Write(counter, "m_CurrentMoneyChangeValue", 0d);
            RegisterInterop.Write(counter, "m_ChangeMoneyAddedCount", 0);
            RegisterInterop.Write(counter, "m_ChangeCoinAddedCount", 0);
            for (var i = 0; i < money.Count; i++)
            {
                money[i]?.ResetAmountGiven();
            }

            ApplyChangeCounts(counter, changes);
        }

        private static void WriteChangeTotals(InteractableCashierCounter counter,
            RegisterPhase phase)
        {
            RegisterInterop.Write(counter, "m_CurrentMoneyChangeValue", phase.Change);
            RegisterInterop.Write(counter, "m_IsChangeReady", phase.ChangeReady);
            RegisterInterop.Write(counter, "m_TooMuchChangeGiven", phase.TooMuchChange);
            RegisterInterop.CashScreen(counter)?.UpdateMoneyChangeAmount(phase.ChangeReady,
                phase.Paid, phase.Total, phase.Change);
        }

        private static void ApplyPhase(InteractableCashierCounter counter, Customer customer,
            RegisterPhase phase)
        {
            if (counter == null)
            {
                return;
            }

            phase ??= new RegisterPhase();
            var diagnosticBefore = RegisterInterop.Total(counter);
            var diagnosticBeforeCustomer = customer == null ? 0d
                : Convert.ToDouble(RegisterInterop.Read(customer, "m_TotalScannedItemCost") ?? 0f);
            var wantedState = phase.State;
            // StartGivingChange reads m_IsUsingCard (to choose the cash or card branch) and
            // CheckChangeReady reads the totals, so the authoritative scalar values must be in
            // place before OnCashTaken runs. The old order wrote m_IsUsingCard afterwards, which
            // let a guest's first change screen take the cash branch for a card payment.
            RegisterInterop.Write(counter, "m_IsUsingCard", phase.UsingCard);
            RegisterInterop.Write(counter, "m_CustomerPaidAmount", phase.Paid);
            RegisterInterop.Write(counter, "m_TotalScannedItemCost", phase.Total);
            RegisterInterop.Write(counter, "m_CurrentMoneyChangeValue", phase.Change);
            if (customer != null && wantedState == ECashierCounterState.GivingChange
                && counter.m_CashierCounterState != ECashierCounterState.GivingChange)
            {
                customer.OnCashTaken(phase.UsingCard);
            }

            RegisterInterop.Write(counter, "m_IsChangeReady", phase.ChangeReady);
            RegisterInterop.Write(counter, "m_IsStartGivingChange", phase.ChangeStarted);
            RegisterInterop.Write(counter, "m_TooMuchChangeGiven", phase.TooMuchChange);
            // This block is the guest's replacement for the customer half of vanilla
            // EvaluateFinishScanItem (the host rolls it; the result arrives in the snapshot).
            // Keep it in step with Customer.cs:2529-2534: the card kind, the visible hand-over
            // only while TakingCash, and the HandingOverCash anim. The one vanilla line
            // intentionally not mirrored is "m_IsCheckScanItemOutOfBound = false": both its
            // writer (WaypointEndUpdate) and its only reader live inside Customer.Update, which
            // NpcClientBehaviour suppresses for the whole client pool, so it can never matter on
            // a guest.
            if (customer != null)
            {
                RegisterInterop.Write(customer, "m_TotalScannedItemCost", (float)phase.CustomerTotal);
                if (customer.m_CustomerCash != null)
                {
                    var wasCard = customer.m_CustomerCash.m_IsCard;
                    customer.m_CustomerCash.SetIsCard(phase.UsingCard);
                    customer.m_CustomerCash.gameObject.SetActive(
                        wantedState == ECashierCounterState.TakingCash);
                    if (wantedState == ECashierCounterState.TakingCash && wasCard != phase.UsingCard)
                    {
                        // The offer kind is host-rolled; log every flip at the moment the offer
                        // becomes visible so a "customer shows cash but the counter expects card"
                        // report can be traced to the snapshot that set it.
                        CoopPlugin.Log.LogInfo("[register] offer counter="
                            + counter.name + " "
                            + (phase.UsingCard ? "card" : "cash") + " (was "
                            + (wasCard ? "card" : "cash") + "), customer="
                            + (customer.name ?? "?"));
                    }
                }

                customer.m_Anim.SetBool("HandingOverCash",
                    wantedState == ECashierCounterState.TakingCash);
            }

            counter.UpdateCashierCounterState(wantedState);
            RegisterInterop.CashScreen(counter)?.UpdateMoneyChangeAmount(phase.ChangeReady,
                phase.Paid, phase.Total, phase.Change);
            if (Math.Abs(phase.Total - diagnosticBefore) > 0.005
                || Math.Abs(phase.CustomerTotal - diagnosticBeforeCustomer) > 0.005)
            {
                CoopPlugin.Log.LogDebug("[register] phase counter=" + counter.name
                    + " state=" + wantedState + " card=" + phase.UsingCard
                    + " total=" + diagnosticBefore.ToString("F3") + "->"
                    + phase.Total.ToString("F3")
                    + " customer=" + diagnosticBeforeCustomer.ToString("F3") + "->"
                    + phase.CustomerTotal.ToString("F3")
                    + " paid=" + phase.Paid.ToString("F3") + " change="
                    + phase.Change.ToString("F3")
                    + " ready=" + phase.ChangeReady + " started=" + phase.ChangeStarted);
            }
        }

        /// <summary>Mirrors the authoritative owner onto this machine's counter. A roster
        /// reference decides locally whether the owner is this player; free is null.</summary>
        private static void ApplyOwner(InteractableCashierCounter counter, PlayerRef owner)
        {
            if (counter == null)
            {
                return;
            }

            var localOwner = owner?.IsSelf == true;
            if (localOwner == counter.IsMannedByPlayer())
            {
                return;
            }

            if (localOwner)
            {
                counter.OnMouseButtonUp();
            }
            else
            {
                counter.OnPressEsc();
            }
        }

        /// <summary>True when another peer - the host or another client - is the authoritative
        /// owner of this register. A free counter and the local claim are never remote.</summary>
        internal bool IsRemoteOwned(InteractableCashierCounter counter)
            => StateOf(counter)?.State?.Owner is { IsSelf: false };

        /// <summary>Keeps the local interaction highlight honest across an authoritative ownership
        /// change: a register that just became someone else's stops advertising itself. No
        /// restore is needed when one frees again - a detached counter is raycast again on the
        /// next frame and highlights normally if it is still the aimed target.</summary>
        private static void RefreshOwnershipHighlight(InteractableCashierCounter counter,
            PlayerRef owner)
        {
            if (owner != null && !owner.IsSelf)
            {
                RegisterHighlight.Clear(counter);
            }
        }

        private void ApplyOwnership(InteractableCashierCounter counter, PlayerRef owner)
        {
            if (counter == null)
            {
                return;
            }

            ApplyOwner(counter, owner);
            RefreshOwnershipHighlight(counter, owner);
        }

        private LocalStation Station(InteractableCashierCounter counter)
            => counter != null && _stations.TryGetValue(counter, out var station) ? station : null;
    }
}
