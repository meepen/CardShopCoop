using System;
using CardShopCoop.Modules.Hud;
using HarmonyLib;

namespace CardShopCoop.Modules.Register
{
    /// <summary>
    /// The register's whole game-observation surface, in one place. Each Harmony hook is
    /// role-neutral: it asks the active host or client behaviour to observe the action, and the
    /// behaviour decides what that means (publish a snapshot, or record an action). Only one of
    /// the two behaviours exists on a peer, so the hook never has to choose.
    /// </summary>
    internal static class RegisterHooks
    {
        private static RegisterHostBehaviour Host => RegisterHostBehaviour.Active;

        private static RegisterClientBehaviour Client => RegisterClientBehaviour.Active;

        internal static void PatchHost(Harmony harmony)
        {
            Patch(harmony, typeof(ManningPatch));
            Patch(harmony, typeof(OutlinePatch));
            Patch(harmony, typeof(ExitPatch));
            Patch(harmony, typeof(CustomerPatch));
            Patch(harmony, typeof(CustomerArrivalPatch));
            Patch(harmony, typeof(StatePatch));
            Patch(harmony, typeof(PaidAmountPatch));
            Patch(harmony, typeof(ChangePhasePatch));
            Patch(harmony, typeof(ScanItemPatch));
            Patch(harmony, typeof(ScanCardPatch));
            Patch(harmony, typeof(AddChangePatch));
            Patch(harmony, typeof(RemoveChangePatch));
            Patch(harmony, typeof(FinishPatch));
            Patch(harmony, typeof(CounterAddedPatch));
            Patch(harmony, typeof(ResetCounterPatch));
        }

        internal static void PatchClient(Harmony harmony)
        {
            Patch(harmony, typeof(ManningPatch));
            Patch(harmony, typeof(OutlinePatch));
            Patch(harmony, typeof(ExitPatch));
            Patch(harmony, typeof(ScanItemPatch));
            Patch(harmony, typeof(ScanCardPatch));
            Patch(harmony, typeof(CashPaymentPatch));
            Patch(harmony, typeof(CardPaymentPatch));
            Patch(harmony, typeof(AddChangePatch));
            Patch(harmony, typeof(RemoveChangePatch));
            Patch(harmony, typeof(FinishPatch));
            Patch(harmony, typeof(CashTakenGuardPatch));
            Patch(harmony, typeof(FinishScanPatch));
            Patch(harmony, typeof(GuestDayStartedBlockPatch));
        }

        private static void Patch(Harmony harmony, Type type)
            => harmony.CreateClassProcessor(type).Patch();

        private static bool AtPayingPosition(Customer customer)
            => RegisterInterop.Read(customer, "m_IsAtPayingPosition") is bool atPaying && atPaying;

        [HarmonyPatch(typeof(InteractableCashierCounter), "OnMouseButtonUp")]
        private static class ManningPatch
        {
            // A register a guest is already manning is not the host's to take, so the host's click
            // is stopped before it enters cashier mode. On a guest the same decision is made from
            // the authoritative owner. A free counter passes through and the postfix observes the
            // successful man.
            [HarmonyPrefix]
            private static bool Prefix(InteractableCashierCounter __instance, out bool __state)
            {
                __state = false;
                var host = Host;
                if (host != null)
                {
                    if (__instance == null || !host.CanObserve)
                    {
                        return true;
                    }

                    if (host.IsOwnedByAnother(__instance))
                    {
                        CoopPlugin.Log.LogInfo("[register] host left counter " + __instance.name
                            + " to the player already manning it.");
                        return false;
                    }

                    return true;
                }

                var client = Client;
                if (client == null || __instance == null || !client.CanObserve)
                {
                    return true;
                }

                if (client.IsRemoteOwned(__instance))
                {
                    CoopPlugin.Log.LogInfo("[register] not manning counter " + __instance.name
                        + "; another player owns it.");
                    return false;
                }

                __state = !__instance.IsMannedByPlayer();
                return true;
            }

            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance, bool __state)
            {
                var host = Host;
                if (host != null)
                {
                    if (__instance != null && __instance.IsMannedByPlayer())
                    {
                        host.OnHostManned(__instance);
                    }

                    return;
                }

                var client = Client;
                if (client != null && __state && __instance != null
                    && __instance.IsMannedByPlayer())
                {
                    client.ObserveClaim(__instance);
                }
            }
        }

        [HarmonyPatch(typeof(InteractableObject), "OnRaycasted")]
        private static class OutlinePatch
        {
            // The green hover outline is raised from this machine's raycast alone. A register that
            // someone else already mans is not interactable here, so it must not advertise itself
            // (or its tooltips) as if it were. Ownership is authoritative and known before the
            // raycast in the normal flow; a mid-look takeover is cleared when its claim is handled.
            [HarmonyPrefix]
            private static bool Prefix(InteractableObject __instance)
            {
                var host = Host;
                if (host != null)
                {
                    if (!host.CanObserve
                        || __instance is not InteractableCashierCounter hostCounter)
                    {
                        return true;
                    }

                    if (host.IsOwnedByAnother(hostCounter))
                    {
                        RegisterHighlight.Clear(hostCounter);
                        return false;
                    }

                    return true;
                }

                var client = Client;
                if (client == null || !client.CanObserve
                    || __instance is not InteractableCashierCounter clientCounter)
                {
                    return true;
                }

                if (client.IsRemoteOwned(clientCounter))
                {
                    RegisterHighlight.Clear(clientCounter);
                    return false;
                }

                return true;
            }
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "OnPressEsc")]
        private static class ExitPatch
        {
            // The host observes the postfix only. A guest captures the pre-exit manned state and
            // forwards the post-hoc release after the game has un-manned the counter.
            [HarmonyPrefix]
            private static void Prefix(InteractableCashierCounter __instance, out bool __state)
            {
                __state = false;
                if (Host != null)
                {
                    return;
                }

                var client = Client;
                if (client == null || __instance == null || !client.CanObserve
                    || !__instance.IsMannedByPlayer()
                    || !client.ShouldObserveRelease(__instance))
                {
                    return;
                }

                __state = true;
            }

            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance, bool __state)
            {
                var host = Host;
                if (host != null)
                {
                    if (__instance != null && host.IsHostOwned(__instance)
                        && !__instance.IsMannedByPlayer())
                    {
                        host.OnHostReleased(__instance);
                    }

                    return;
                }

                var client = Client;
                if (client != null && __state && __instance != null
                    && !__instance.IsMannedByPlayer())
                {
                    client.ObserveRelease(__instance);
                }
            }
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "UpdateCurrentCustomer")]
        private static class CustomerPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance)
                => Host?.OnCustomerUpdated(__instance);
        }

        [HarmonyPatch(typeof(Customer), "WaypointEndUpdate")]
        private static class CustomerArrivalPatch
        {
            // Vanilla announces the arrival and registers the bag in one paying-position block, in
            // that order: UpdateCurrentCustomer runs before RegisterScanItem/RegisterScanCard, so
            // a lifecycle built from the announcement read IsNotScanned() == false ("already
            // scanned") for every line that was merely not-yet-registered, and guests scanned the
            // customer's whole bag the moment it was placed on the counter. Publish once the block
            // has run instead - at that point the bag is registered and "un-scanned" means the
            // item is waiting on the counter, exactly what the guest must show. Gated on the
            // paying-position transition (false -> true) so the frames where the method
            // early-returns for a customer already at the counter publish nothing.
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static void Prefix(Customer __instance, out bool __state)
            {
                var host = Host;
                if (host == null)
                {
                    __state = true;
                    return;
                }

                __state = AtPayingPosition(__instance);
                host.BeginCustomerArrival();
            }

            [HarmonyPostfix]
            private static void Postfix(Customer __instance, bool __state)
            {
                var host = Host;
                if (host == null)
                {
                    return;
                }

                host.EndCustomerArrival();
                if (__state || !AtPayingPosition(__instance))
                {
                    return;
                }

                host.PublishCustomerArrival(RegisterInterop.CounterFor(__instance));
            }

            [HarmonyFinalizer]
            private static void Finalizer() => Host?.EndCustomerArrival();
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "UpdateCashierCounterState")]
        private static class StatePatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance)
                => Host?.OnCounterState(__instance);
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "SetCustomerPaidAmount")]
        private static class PaidAmountPatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance)
                => Host?.OnCounterState(__instance);
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "StartGivingChange")]
        private static class ChangePhasePatch
        {
            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance)
                => Host?.OnCounterState(__instance);
        }

        private sealed class ScanCapture
        {
            public InteractableCashierCounter Counter;
            public RegisterClientBehaviour.ActionCapture Client;
        }

        [HarmonyPatch(typeof(InteractableScanItem), "OnMouseButtonUp")]
        private static class ScanItemPatch
        {
            // The scan nulls the item's owning-customer reference, so capture the counter it
            // belongs to before vanilla runs. The host publishes the resulting snapshot from the
            // captured counter; the guest records the action from the captured slot.
            [HarmonyPrefix]
            private static void Prefix(InteractableScanItem __instance, out ScanCapture __state)
            {
                __state = null;
                var host = Host;
                if (host != null)
                {
                    if (!host.CanObserve)
                    {
                        return;
                    }

                    var counter = RegisterInterop.FindCounterForScanItem(__instance);
                    if (counter != null)
                    {
                        __state = new ScanCapture { Counter = counter };
                    }

                    return;
                }

                var client = Client;
                if (client == null || !client.CanObserve)
                {
                    return;
                }

                var capture = client.CaptureScanItem(__instance);
                if (capture != null)
                {
                    __state = new ScanCapture
                    {
                        Counter = capture.Counter,
                        Client = capture,
                    };
                }
            }

            [HarmonyPostfix]
            private static void Postfix(ScanCapture __state)
            {
                if (__state == null)
                {
                    return;
                }

                var host = Host;
                if (host != null)
                {
                    host.OnScanned(__state.Counter);
                    return;
                }

                if (__state.Client != null)
                {
                    Client?.ObserveScan(__state.Client);
                }
            }
        }

        [HarmonyPatch(typeof(InteractableCard3d), "OnMouseButtonUp")]
        private static class ScanCardPatch
        {
            // Card counterpart of ScanItemPatch: capture the counter before the card's owning
            // customer is cleared.
            [HarmonyPrefix]
            private static void Prefix(InteractableCard3d __instance, out ScanCapture __state)
            {
                __state = null;
                var host = Host;
                if (host != null)
                {
                    if (!host.CanObserve)
                    {
                        return;
                    }

                    var counter = RegisterInterop.FindCounterForCard(__instance);
                    if (counter != null)
                    {
                        __state = new ScanCapture { Counter = counter };
                    }

                    return;
                }

                var client = Client;
                if (client == null || !client.CanObserve)
                {
                    return;
                }

                var capture = client.CaptureScanCard(__instance);
                if (capture != null)
                {
                    __state = new ScanCapture
                    {
                        Counter = capture.Counter,
                        Client = capture,
                    };
                }
            }

            [HarmonyPostfix]
            private static void Postfix(ScanCapture __state)
            {
                if (__state == null)
                {
                    return;
                }

                var host = Host;
                if (host != null)
                {
                    host.OnScanned(__state.Counter);
                    return;
                }

                if (__state.Client != null)
                {
                    Client?.ObserveScan(__state.Client);
                }
            }
        }

        [HarmonyPatch(typeof(InteractableCustomerCash), "OnMouseButtonUp")]
        private static class CashPaymentPatch
        {
            // Capture-only: let the game take the cash (the customer transitions to giving change),
            // then forward the post-hoc payment. Block any click that cannot be attributed to the
            // authoritative station carrier and log the full state so the next report is traceable.
            [HarmonyPrefix]
            private static bool Prefix(InteractableCustomerCash __instance,
                out RegisterClientBehaviour.ActionCapture __state)
            {
                __state = null;
                var client = Client;
                if (client == null || !client.CanObserve)
                {
                    return true;
                }

                return client.TryCaptureCashPayment(__instance, out __state);
            }

            [HarmonyPostfix]
            private static void Postfix(RegisterClientBehaviour.ActionCapture __state)
            {
                if (__state != null)
                {
                    Client?.ObservePayment(__state);
                }
            }
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "EvaluateCreditCard")]
        private static class CardPaymentPatch
        {
            // Capture-only: let the game evaluate the card payment, then forward it. The vanilla
            // method ends by calling OnPressSpaceBar, so flag the nested call to make the finish
            // hook record only the one higher-level card payment.
            [HarmonyPrefix]
            private static void Prefix(InteractableCashierCounter __instance, double value,
                out RegisterClientBehaviour.ActionCapture __state)
            {
                __state = null;
                var client = Client;
                if (client == null || !client.CanObserve)
                {
                    return;
                }

                client.TryCaptureCardPayment(__instance, value, out __state);
                if (__state != null)
                {
                    // EvaluateCreditCard ends by calling OnPressSpaceBar, which queues the
                    // counter's AddCoin/AddShopExp. The host owns that charge for the guest's
                    // CardPayment intent, so suppress the Hud observer for this vanilla event.
                    EconomyActionScope.Enter();
                }
            }

            [HarmonyPostfix]
            private static void Postfix(RegisterClientBehaviour.ActionCapture __state)
            {
                var client = Client;
                if (client != null)
                {
                    client.EvaluatingCreditCard = false;
                    if (__state != null)
                    {
                        client.ObserveCardPayment(__state);
                    }
                }
            }

            [HarmonyFinalizer]
            private static void Finalizer(RegisterClientBehaviour.ActionCapture __state)
            {
                var client = Client;
                if (client != null)
                {
                    client.EvaluatingCreditCard = false;
                }

                if (__state != null)
                {
                    EconomyActionScope.Exit();
                }
            }
        }

        [HarmonyPatch(typeof(InteractableCounterMoneyChange), "OnMouseButtonUp")]
        private static class AddChangePatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractableCounterMoneyChange __instance,
                out RegisterClientBehaviour.ActionCapture __state)
                => CaptureChange(__instance, false, out __state);

            [HarmonyPostfix]
            private static void Postfix(InteractableCounterMoneyChange __instance,
                RegisterClientBehaviour.ActionCapture __state)
                => ApplyChange(__instance, __state);
        }

        [HarmonyPatch(typeof(InteractableCounterMoneyChange), "OnRightMouseButtonUp")]
        private static class RemoveChangePatch
        {
            [HarmonyPrefix]
            private static void Prefix(InteractableCounterMoneyChange __instance,
                out RegisterClientBehaviour.ActionCapture __state)
                => CaptureChange(__instance, true, out __state);

            [HarmonyPostfix]
            private static void Postfix(InteractableCounterMoneyChange __instance,
                RegisterClientBehaviour.ActionCapture __state)
                => ApplyChange(__instance, __state);
        }

        private static void CaptureChange(InteractableCounterMoneyChange change, bool takeBack,
            out RegisterClientBehaviour.ActionCapture capture)
        {
            capture = null;
            if (Host != null)
            {
                return;
            }

            var client = Client;
            if (client != null && client.CanObserve)
            {
                capture = client.CaptureChange(change, takeBack);
            }
        }

        private static void ApplyChange(InteractableCounterMoneyChange change,
            RegisterClientBehaviour.ActionCapture capture)
        {
            var host = Host;
            if (host != null)
            {
                if (host.CanObserve)
                {
                    host.OnChange(change);
                }

                return;
            }

            if (capture != null && RegisterInterop.GivenAmount(change) != capture.GivenBefore)
            {
                Client?.ObserveChange(capture);
            }
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "OnPressSpaceBar")]
        private static class FinishPatch
        {
            // Capture-only for the guest: let the game complete the checkout, then forward it. The
            // nested call inside EvaluateCreditCard is skipped so a card payment is exactly one
            // action. The host publishes the resulting snapshot.
            [HarmonyPrefix]
            private static void Prefix(InteractableCashierCounter __instance,
                out RegisterClientBehaviour.ActionCapture __state)
            {
                __state = null;
                if (Host != null)
                {
                    return;
                }

                var client = Client;
                if (client == null || !client.CanObserve)
                {
                    return;
                }

                __state = client.CaptureComplete(__instance);
                if (__state != null)
                {
                    // OnPressSpaceBar queues the counter's AddCoin/AddShopExp. The host owns that
                    // charge for the guest's Complete intent, so suppress the Hud observer for the
                    // vanilla event this call produces.
                    EconomyActionScope.Enter();
                }
            }

            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance,
                RegisterClientBehaviour.ActionCapture __state)
            {
                var host = Host;
                if (host != null)
                {
                    if (host.CanObserve)
                    {
                        host.OnCounterState(__instance);
                    }

                    return;
                }

                var client = Client;
                if (client != null && __state != null && !RegisterInterop.IsChangeReady(__instance))
                {
                    client.ObserveComplete(__state);
                }
            }

            [HarmonyFinalizer]
            private static void Finalizer(RegisterClientBehaviour.ActionCapture __state)
            {
                if (__state != null)
                {
                    EconomyActionScope.Exit();
                }
            }
        }

        [HarmonyPatch(typeof(Customer), "OnCashTaken")]
        private static class CashTakenGuardPatch
        {
            // Vanilla OnCashTaken has no guards: it drives m_CurrentQueueCashierCounter
            // unconditionally. A stale or foreign customer body (the "cash and card at once"
            // duplicate) either NREs on a null counter or pushes the wrong counter into
            // GivingChange. The capture path already blocks such clicks; this is the last line of
            // defense for any path that still reaches the game method.
            [HarmonyPrefix]
            private static bool Prefix(Customer __instance)
            {
                var client = Client;
                return client == null || !client.InGame || client.CanTakeCash(__instance);
            }
        }

        [HarmonyPatch(typeof(Customer), "EvaluateFinishScanItem")]
        private static class FinishScanPatch
        {
            // The customer's paid amount and cash-vs-card kind are host-authoritative: ONLY the
            // host runs this roll. A guest must never run it for ANY customer - every customer on
            // a guest is a host-driven mirror (NpcClientBehaviour suppresses Customer.Update for
            // the whole client pool), so a local roll would invent a kind and paid amount the host
            // did not choose. The host's result travels in the snapshot and is applied through the
            // game's own setters. This is the deliberate exception to "never stop vanilla": the
            // host owns this value the same way it owns another player's.
            [HarmonyPrefix]
            private static bool Prefix()
            {
                var client = Client;
                return client == null || !client.InGame;
            }
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "OnDayStarted")]
        private static class GuestDayStartedBlockPatch
        {
            // Guest: the register's day-start counter reset is host-authoritative - the host
            // publishes owner null and this client applies it (ApplyOwner calls OnPressEsc) - so
            // the guest does not run the local ForceResetCounter path at the roll-over.
            [HarmonyPrefix]
            private static bool Prefix() => false;
        }

        // The identity module binds and announces the counter's live identity in its own postfix.
        // Run after it so the state can serialize the counter reference it names.
        [HarmonyAfter("dev.meepen.cardshopcoop.gameobjectidentity.host")]
        [HarmonyPatch(typeof(ShelfManager), "InitCashierCounter")]
        private static class CounterAddedPatch
        {
            // InitCashierCounter is static in both game baselines, so Harmony binds __instance to
            // null. Bind the counter argument positionally.
            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __0)
                => Host?.OnCounterAdded(__0);
        }

        [HarmonyPatch(typeof(InteractableCashierCounter), "ForceResetCounter")]
        private static class ResetCounterPatch
        {
            // Day start force-clears the counter's manned state without OnPressEsc. Clear the
            // authoritative owner so nobody keeps, or is locked out of, a register no one mans.
            [HarmonyPostfix]
            private static void Postfix(InteractableCashierCounter __instance)
                => Host?.OnCounterReset(__instance);
        }
    }
}
