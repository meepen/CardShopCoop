using CardShopCoop.Api;
using CardShopCoop.Modules.Npc;
using CardShopCoop.Runtime;

namespace CardShopCoop.Modules.Register
{
    /// <summary>Client snapshot application. One handler, one renderer: the snapshot is the whole
    /// truth, and rendering it is idempotent, so there is no operation dispatch and no "is this my
    /// action" branch.</summary>
    public sealed partial class RegisterClientBehaviour
    {
        /// <summary>Queue a counter for the next readiness sweep. The store already holds the
        /// value, so a signal that arrives mid-render only asks the running sweep to go once more.
        /// A null snapshot is a removal: release the station immediately, before the store clears
        /// and the object is destroyed.</summary>
        [ExtendedInformationHandler]
        private void OnRegisterState(InteractableCashierCounter counter, RegisterSnapshot snapshot)
        {
            if (_shutdown || counter == null)
            {
                return;
            }

            if (snapshot?.State == null)
            {
                _awaiting.Remove(counter);
                ReleaseStation(counter);
                return;
            }

            // The wire customer is the Npc module's puppet clone on a guest, and a client never
            // runs Customer.ActivateCustomer, so every customer body here reads m_IsActive ==
            // false. That flag is host-owned activation state, not "is a customer at the counter":
            // the host already normalizes an inactive customer to null before publishing, so a
            // non-null Customer is the whole authority. Only a null customer is idle.
            var customer = snapshot.State.Customer;
            if (customer == null)
            {
                ReleaseStation(counter);
            }
            else if (snapshot.Rejected && IsPendingActionFor(counter, snapshot.ActionId)
                && StationIsCheckedOut(counter))
            {
                // A rejected checkout can arrive after the local completion checked the carrier
                // out and released the station. Only the client that owned the rejected action
                // needs this: the re-anchor is broadcast to everyone, but `Rejected` is meaningful
                // only here, and every other client's station is still authoritative.
                ReleaseStation(counter);
            }

            _awaiting.Add(counter);
            OnReadinessSignal();
        }

        private static RegisterSnapshot StateOf(InteractableCashierCounter counter)
            => counter == null ? null : counter.GetExtendedInformation<RegisterSnapshot>();

        /// <summary>Renders the current stored snapshot for one counter. Returns false when the
        /// customer/carrier or placement structure is not ready yet, or while an older
        /// uncorrelated host state must not be applied over a pending local action.</summary>
        private bool TryRender(InteractableCashierCounter counter)
        {
            var snapshot = StateOf(counter);
            if (snapshot?.State == null)
            {
                ReleaseStation(counter);
                return true;
            }

            var state = snapshot.State;
            // A pending local action owns the entity until its own answer. Only that action's
            // state may apply; an older host state must wait, not walk the action back.
            if (HasPendingAction(counter) && !IsPendingActionFor(counter, snapshot.ActionId))
            {
                _awaiting.Add(counter);
                return false;
            }

            // Ownership is independent of the carrier: a release or day-start must unman even when
            // the customer's mirror is not ready yet.
            ApplyOwnershipFromAuthority(counter, state.Owner);

            // The host never publishes an inactive customer (it normalizes one to null first), so
            // no local activation test belongs here: on a guest every customer body is a puppet
            // clone that the client deliberately never activates.
            var customer = state.Customer;

            if (!CanRender(customer))
            {
                _awaiting.Add(counter);
                return false;
            }

            var rendered = Render(counter, state, customer);

            if (!rendered)
            {
                _awaiting.Add(counter);
                return false;
            }

            return true;
        }

        private bool CanRender(Customer customer)
        {
            if (!RuntimeContext.InGame() || !IsSceneReady())
            {
                return false;
            }

            if (customer == null)
            {
                return true;
            }

            return NpcClientBehaviour.TryGetWireIdentity(customer, out _, out _)
                && FindCarrier(customer) != null;
        }

        /// <summary>True when this counter's station carrier has finished its checkout. Vanilla
        /// <c>OnPressSpaceBar</c> runs on whatever is in <c>counter.m_CurrentCustomer</c>, which on
        /// a guest is the station carrier, so the flag belongs to the carrier and never to the
        /// wire reference.</summary>
        private bool StationIsCheckedOut(InteractableCashierCounter counter)
            => RegisterInterop.Read(Station(counter)?.Carrier, "m_HasCheckedOut") is bool value
                && value;

        private void ApplyOwnershipFromAuthority(InteractableCashierCounter counter, PlayerRef owner)
        {
            using (Writes.Enter())
            {
                ApplyOwnership(counter, owner);
            }
        }

        private bool Render(InteractableCashierCounter counter, RegisterState state, Customer customer)
        {
            using (Writes.Enter())
            {
                ApplyOwnership(counter, state.Owner);
                if (customer == null)
                {
                    ReleaseStation(counter);
                    return true;
                }

                // A local completion leaves the carrier checked out and released (vanilla
                // OnPressSpaceBar ran CounterGiveChangeCompleted on it), so rebuild the station
                // instead of reusing the post-completion carrier. The wire customer is the puppet
                // clone here and never carries that flag; the carrier is the object the checkout
                // ran on. PrepareCarrier restores its active/checked-out flags on the rebuild.
                if (StationIsCheckedOut(counter))
                {
                    ReleaseStation(counter);
                }

                var station = GetOrBuildStation(counter, customer, state.Lines);
                if (station == null)
                {
                    // CanRender checked the same prerequisites, so this is a lifecycle race:
                    // drop this render and let the next readiness signal retry from the store.
                    CoopPlugin.Log.LogWarning("[register] station unavailable while rendering "
                        + counter.name + "; deferring the snapshot.");
                    return false;
                }

                var phase = state.Phase ?? new RegisterPhase();
                var drawerOpen = RegisterInterop.ChangeStarted(counter)
                    && !RegisterInterop.IsUsingCard(counter);
                var screenRebuilt = ApplyScanned(counter, station.Carrier, state.Lines);
                // A live giving-change counter can apply counts as a delta: the game's click path
                // is already enabled, so unchanged denominations no-op. Every other render is a
                // transition (join, re-anchor, rebuilt carrier) and must enter the phase first,
                // then rebuild the stack from zero because the clicks are ignored otherwise.
                var wasGivingChange = counter.m_CashierCounterState
                    == ECashierCounterState.GivingChange
                    && !RegisterInterop.IsUsingCard(counter);
                if (wasGivingChange)
                {
                    ApplyChangeCounts(counter, state.ChangeItems);
                    ApplyPhase(counter, station.Carrier, phase);
                }
                else
                {
                    ApplyPhase(counter, station.Carrier, phase);
                    if (phase.State == ECashierCounterState.GivingChange && !phase.UsingCard)
                    {
                        RebuildChangeStack(counter, state.ChangeItems);
                        WriteChangeTotals(counter, phase);
                    }
                }

                if ((!wasGivingChange || screenRebuilt)
                    && phase.State == ECashierCounterState.GivingChange && !phase.UsingCard)
                {
                    // Only entering (or being re-anchored back into) the giving-change phase needs
                    // the cash screen rebuilt: it restores the scanned item list after a local
                    // completion reset it. A counter that is already giving change shows the right
                    // list and only its totals move. Replaying this on every change click re-ran
                    // UI_CashCounterScreen.OnStartGivingChange, and its
                    // m_MoneyGuideFollowDrawerAnim is what made the denomination labels slide as if
                    // money were being drawn on every click while the bills stayed put.
                    RebuildCashScreen(counter, station.Carrier);
                    RegisterInterop.CashScreen(counter)?.OnStartGivingChange();
                    WriteChangeTotals(counter, phase);
                }

                ApplyInteraction(counter, phase, drawerOpen);
                return true;
            }
        }

        /// <summary>Restores the interaction state an action phase change can leave behind:
        /// the credit-card screen and its UI mode, the card machine, and the cash drawer. The
        /// authoritative phase decides all four, so no captured pre-action UI facts are needed.
        /// A cash checkout is deliberately never put into UI mode: vanilla keeps it in
        /// <c>m_IsCashCounterMode</c> (cursor hidden, world-space cash screen), and UI mode makes
        /// InteractionPlayerController return before its cash-counter raycast, which leaves the
        /// cashier with a free cursor and no way to click change or complete the sale.</summary>
        private static void ApplyInteraction(InteractableCashierCounter counter,
            RegisterPhase phase, bool drawerOpen)
        {
            var controller = SceneRef<InteractionPlayerController>.Get();
            var inUi = controller != null
                && RegisterInterop.Read(controller, "m_IsInUIMode") is bool uiValue && uiValue;

            var targetDrawerOpen = phase.ChangeStarted && !phase.UsingCard;
            if (drawerOpen != targetDrawerOpen)
            {
                if (targetDrawerOpen)
                {
                    RegisterInterop.OpenDrawer(counter);
                }
                else
                {
                    RegisterInterop.CloseDrawer(counter);
                }
            }

            var creditScreen = RegisterInterop.CreditScreen(counter);
            if (creditScreen != null)
            {
                var cardMode = RegisterInterop.CreditCardMode(counter);
                var wantsCardMode = phase.UsingCard
                    && phase.State == ECashierCounterState.GivingChange;
                if (wantsCardMode && !cardMode)
                {
                    creditScreen.EnableCreditCardMode(counter.IsMannedByPlayer());
                    if (controller != null && counter.IsMannedByPlayer() && !inUi)
                    {
                        // Vanilla StartGivingChange enters UI mode for exactly this screen; a
                        // re-anchor that restores the card phase restores the cursor with it.
                        controller.EnterUIMode();
                    }
                }
                else if (!wantsCardMode && cardMode)
                {
                    creditScreen.ResetCounter();
                    CoopPlugin.Log.LogInfo("[register] re-anchor reset the credit card screen "
                        + "for counter=" + counter.name + ".");
                    if (controller != null && inUi)
                    {
                        controller.ExitUIMode();
                        CoopPlugin.Log.LogInfo("[register] re-anchor released the counter UI mode "
                            + "for counter=" + counter.name + ".");
                    }
                }
            }

            if (phase.UsingCard)
            {
                if (phase.State == ECashierCounterState.GivingChange)
                {
                    RegisterInterop.ShowCreditCardMachine(counter);
                }
                else
                {
                    RegisterInterop.RestoreCreditCardMachine(counter);
                }
            }
        }
    }
}
