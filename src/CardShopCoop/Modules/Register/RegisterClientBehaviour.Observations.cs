using System;
using CardShopCoop.Api;

namespace CardShopCoop.Modules.Register
{
    /// <summary>Client observations: turn the game action the player just performed into one
    /// action intent, and undo that action through the game's own path if the host rejects it.</summary>
    public sealed partial class RegisterClientBehaviour
    {
        internal sealed class ActionCapture
        {
            public InteractableCashierCounter Counter;
            public Customer Customer;
            public int Index;
            public int Slot;
            public bool IsCard;
            public InteractableCustomerCash Cash;
            public double Value;
            public InteractableCounterMoneyChange Change;
            public bool TakeBack;
            public int GivenBefore;
            public double Paid;
            public double ChangeValue;
        }

        private ICoopContext Api => _context;

        internal ActionCapture CaptureScanItem(InteractableScanItem item)
        {
            if (!CanObserve)
            {
                return null;
            }

            var counter = RegisterInterop.FindCounterForScanItem(item);
            var station = Station(counter);
            if (station == null || !ReferenceEquals(station.Carrier, counter?.m_CurrentCustomer))
            {
                return null;
            }

            var slot = ItemSlot(item, counter);
            if (slot < 0)
            {
                return null;
            }

            return new ActionCapture
            {
                Counter = counter,
                Customer = counter.m_CurrentCustomer,
                Index = counter.GetInstanceID(),
                Slot = slot,
                IsCard = false,
            };
        }

        internal ActionCapture CaptureScanCard(InteractableCard3d card)
        {
            if (!CanObserve)
            {
                return null;
            }

            var counter = RegisterInterop.FindCounterForCard(card);
            var station = Station(counter);
            if (station == null || !ReferenceEquals(station.Carrier, counter?.m_CurrentCustomer))
            {
                return null;
            }

            var slot = CardSlot(card, counter);
            if (slot < 0)
            {
                return null;
            }

            return new ActionCapture
            {
                Counter = counter,
                Customer = counter.m_CurrentCustomer,
                Index = counter.GetInstanceID(),
                Slot = slot,
                IsCard = true,
            };
        }

        /// <summary>Returns false when the click cannot belong to the authoritative station
        /// carrier. That is the "click cash and the UI breaks" hardening: vanilla OnCashTaken has
        /// no guards and would NRE on a null counter or drive the wrong counter into GivingChange.</summary>
        internal bool TryCaptureCashPayment(InteractableCustomerCash cash, out ActionCapture capture)
        {
            capture = null;
            if (!CanObserve)
            {
                return true;
            }

            var cashCustomer = RegisterInterop.Read(cash, "m_CurrentCustomer") as Customer;
            var counter = RegisterInterop.FindCounterForCash(cash);
            var station = Station(counter);
            if (counter == null || cashCustomer == null
                || !ReferenceEquals(counter.m_CurrentCustomer, cashCustomer)
                || station == null || !ReferenceEquals(station.Carrier, cashCustomer))
            {
                LogBlockedCashClick(cash, counter, cashCustomer);
                return false;
            }

            capture = new ActionCapture
            {
                Counter = counter,
                Customer = cashCustomer,
                Index = counter.GetInstanceID(),
                Cash = cash,
            };
            return true;
        }

        internal ActionCapture CaptureChange(InteractableCounterMoneyChange change, bool takeBack)
        {
            if (!CanObserve)
            {
                return null;
            }

            var counter = change?.m_CashierCounter;
            var station = Station(counter);
            if (station == null)
            {
                return null;
            }

            if (!ReferenceEquals(station.Carrier, counter?.m_CurrentCustomer))
            {
                LogCheckoutMismatch("change click", counter);
                return null;
            }

            // Vanilla already ignores a click past the change limits or outside giving change, so
            // the postfix's GivenAmount comparison records only a click the game really performed.
            return new ActionCapture
            {
                Counter = counter,
                Customer = counter.m_CurrentCustomer,
                Index = counter.GetInstanceID(),
                Change = change,
                TakeBack = takeBack,
                GivenBefore = RegisterInterop.GivenAmount(change),
            };
        }

        internal bool TryCaptureCardPayment(InteractableCashierCounter counter, double value,
            out ActionCapture capture)
        {
            capture = null;
            if (!CanObserve)
            {
                return true;
            }

            var station = Station(counter);
            if (station == null)
            {
                return true;
            }

            if (!ReferenceEquals(station.Carrier, counter.m_CurrentCustomer))
            {
                LogCheckoutMismatch("card payment", counter);
                return true;
            }

            EvaluatingCreditCard = true;
            capture = new ActionCapture
            {
                Counter = counter,
                Customer = counter.m_CurrentCustomer,
                Index = counter.GetInstanceID(),
                Value = value,
            };
            return true;
        }

        internal ActionCapture CaptureComplete(InteractableCashierCounter counter)
        {
            if (!CanObserve || EvaluatingCreditCard)
            {
                return null;
            }

            var station = Station(counter);
            if (station == null)
            {
                return null;
            }

            if (!ReferenceEquals(station.Carrier, counter.m_CurrentCustomer))
            {
                LogCheckoutMismatch("complete press", counter);
                return null;
            }

            // Vanilla's OnPressSpaceBar only completes when the change is ready; otherwise it
            // shows the "wrong amount" / "too much change" popup and leaves the drawer open.
            // Let the game show its own popup and keep the register exactly as it was.
            if (!RegisterInterop.IsChangeReady(counter))
            {
                return null;
            }

            return new ActionCapture
            {
                Counter = counter,
                Customer = counter.m_CurrentCustomer,
                Index = counter.GetInstanceID(),
                Paid = RegisterInterop.Paid(counter),
                ChangeValue = RegisterInterop.Change(counter),
            };
        }

        internal void ObserveClaim(InteractableCashierCounter counter)
        {
            // The game already manned the counter (the hook is a postfix). The action records how
            // to replay the man and reverse it; only a rejection unmounts through OnPressEsc.
            if (counter == null)
            {
                return;
            }

            var intent = new RegisterClaimIntent { Counter = counter };
            Send(intent);
            TrackOwnership(intent.ActionId, counter, claim: true);
        }

        internal void ObserveRelease(InteractableCashierCounter counter)
        {
            if (counter == null || !ShouldObserveRelease(counter))
            {
                return;
            }

            // The game already un-manned the counter (postfix).
            var intent = new RegisterReleaseIntent { Counter = counter };
            Send(intent);
            TrackOwnership(intent.ActionId, counter, claim: false);
        }

        internal void ObserveScan(ActionCapture capture)
        {
            var customer = capture?.Customer;
            if (customer == null)
            {
                return;
            }

            CoopPlugin.Log.LogInfo("[register] observed " + (capture.IsCard ? "card" : "item")
                + " scan counter=" + capture.Index + " slot=" + capture.Slot + ".");
            if (capture.IsCard)
            {
                Send(new RegisterScanCardIntent
                {
                    Counter = capture.Counter,
                    Slot = capture.Slot,
                });
            }
            else
            {
                Send(new RegisterScanItemIntent
                {
                    Counter = capture.Counter,
                    Slot = capture.Slot,
                });
            }
        }

        internal void ObservePayment(ActionCapture capture)
        {
            if (capture?.Cash == null || capture.Counter == null)
            {
                return;
            }

            CoopPlugin.Log.LogInfo("[register] observed take payment card=" + capture.Cash.m_IsCard
                + " counterTotal=" + RegisterInterop.Total(capture.Counter).ToString("F3")
                + " state=" + capture.Counter.m_CashierCounterState);
            Send(new RegisterTakePaymentIntent
            {
                Counter = capture.Counter,
                IsCard = capture.Cash.m_IsCard,
            });
        }
        internal void ObserveCardPayment(ActionCapture capture)
        {
            if (capture?.Counter == null)
            {
                return;
            }

            CoopPlugin.Log.LogInfo("[register] observed card payment value="
                + capture.Value.ToString("F3") + " counterTotal="
                + RegisterInterop.Total(capture.Counter).ToString("F3")
                + " customerTotal=" + Convert.ToDouble(RegisterInterop.Read(
                    capture.Counter.m_CurrentCustomer, "m_TotalScannedItemCost") ?? 0f).ToString("F3")
                + " state=" + capture.Counter.m_CashierCounterState);
            Send(new RegisterCardPaymentIntent
            {
                Counter = capture.Counter,
                Value = capture.Value,
                WalletApplied = RegisterInterop.Total(capture.Counter) <= 0.0001d,
            });
        }

        internal void ObserveChange(ActionCapture capture)
        {
            var change = capture?.Change;
            if (change == null)
            {
                return;
            }

            CoopPlugin.Log.LogInfo("[register] observed change slot=" + change.m_Index
                + " coin=" + change.m_IsCoin + " takeBack=" + capture.TakeBack
                + " value=" + change.m_ValueDouble.ToString("F3")
                + " given=" + RegisterInterop.GivenAmount(change)
                + " counterTotal=" + RegisterInterop.Total(change.m_CashierCounter).ToString("F3"));
            Send(new RegisterChangeIntent
            {
                Counter = capture.Counter,
                Slot = change.m_Index,
                IsCoin = change.m_IsCoin,
                TakeBack = capture.TakeBack,
            });
        }

        internal void ObserveComplete(ActionCapture capture)
        {
            if (capture?.Counter == null)
            {
                return;
            }

            Send(new RegisterCompleteIntent
            {
                Counter = capture.Counter,
                Paid = capture.Paid,
                Change = capture.ChangeValue,
            });
        }

        /// <summary>The last-line guard for a game path that reaches vanilla <c>OnCashTaken</c>:
        /// only the customer actually standing at a counter may drive it.</summary>
        internal bool CanTakeCash(Customer customer)
        {
            var counter = RegisterInterop.CounterFor(customer);
            if (counter != null && ReferenceEquals(counter.m_CurrentCustomer, customer))
            {
                return true;
            }

            CoopPlugin.Log.LogWarning("[register] blocked OnCashTaken for a customer with no "
                + "matching counter; customer=" + (customer == null ? "null" : customer.name)
                + " counter=" + (counter == null ? "null" : counter.name));
            return false;
        }

        /// <summary>Diagnostic for a cash click that cannot belong to the authoritative station
        /// carrier; prints the exact state the "customer holds cash and card" report describes.</summary>
        private static void LogBlockedCashClick(InteractableCustomerCash cash,
            InteractableCashierCounter counter, Customer cashCustomer)
        {
            var hasStation = false;
            if (counter != null && _active != null)
            {
                hasStation = _active.Station(counter) != null;
            }

            CoopPlugin.Log.LogWarning("[register] blocked cash click: counter="
                + (counter == null ? "none" : counter.name)
                + " cashCustomer=" + (cashCustomer == null ? "null" : cashCustomer.name)
                + " counterCustomer=" + (counter == null || counter.m_CurrentCustomer == null
                    ? "null" : counter.m_CurrentCustomer.name)
                + " station=" + (hasStation ? "present" : "none")
                + " isCard=" + (cash != null && cash.m_IsCard)
                + " cashModel=" + (cash?.m_CashModel != null && cash.m_CashModel.activeSelf)
                + " cardModel=" + (cash?.m_CardModel != null && cash.m_CardModel.activeSelf)
                + " state=" + (counter == null ? "none" : counter.m_CashierCounterState.ToString())
                + " usingCard=" + (counter != null && RegisterInterop.IsUsingCard(counter)));
        }

        private static void LogCheckoutMismatch(string action, InteractableCashierCounter counter)
        {
            CoopPlugin.Log.LogWarning("[register] ignored " + action + " for a checkout whose "
                + "counter customer is not the station carrier; counter="
                + (counter == null ? "null" : counter.name)
                + " counterCustomer=" + (counter == null || counter.m_CurrentCustomer == null
                    ? "null" : counter.m_CurrentCustomer.name));
        }

        private void Send(RegisterIntent message)
        {
            if (_shutdown || !_joined || _context == null || !_context.InGame())
            {
                CoopPlugin.Log.LogWarning("[register] dropped " + message.GetType().Name
                    + " because the client is not in a joined game.");
                return;
            }

            var host = Api.HostConnection;
            if (host == null)
            {
                CoopPlugin.Log.LogWarning("[register] dropped " + message.GetType().Name
                    + " because the host connection is missing.");
                return;
            }

            // The core action send allocates the id and records the replay/undo before it sends.
            _context.Send(host.Id, message);
            if (message.ActionId != Guid.Empty && message.Counter != null)
            {
                _actionCounters[message.ActionId] = message.Counter;
            }
        }

        private static int ItemSlot(InteractableScanItem item, InteractableCashierCounter counter)
        {
            var items = counter?.m_CurrentCustomer?.GetItemInBagList();
            for (var i = 0; items != null && i < items.Count; i++)
            {
                if (items[i]?.m_InteractableScanItem == item)
                {
                    return i;
                }
            }

            return -1;
        }

        private static int CardSlot(InteractableCard3d card, InteractableCashierCounter counter)
        {
            var cards = counter?.m_CurrentCustomer?.GetCardInBagList();
            for (var i = 0; cards != null && i < cards.Count; i++)
            {
                if (cards[i] == card)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
