using System;
using CardShopCoop.Api;

namespace CardShopCoop.Modules.Register
{
    /// <summary>
    /// The host-authoritative snapshot of one cashier counter. <see cref="Customer"/> is only set
    /// while that customer is active at the counter; the host normalizes an inactive customer to
    /// null, and the client releases the station for a null/inactive customer. A pooled body can be
    /// reused only after it is inactive, at which point no register state references it.
    /// </summary>
    internal sealed class RegisterState
    {
        /// <summary>The player manning the counter, or null when the counter is free. The value is
        /// a roster reference, so <see cref="PlayerRef.IsSelf"/> is resolved locally on every peer
        /// and the register never stores or compares raw connection ids.</summary>
        public PlayerRef Owner
        {
            get; init;
        }

        /// <summary>The active customer at the counter, or null when the counter is idle. The wire
        /// converter sends this as the customer's stable identity.</summary>
        public Customer Customer
        {
            get; init;
        }

        public RegisterPhase Phase
        {
            get; init;
        } = new RegisterPhase();

        /// <summary>The complete bag snapshot, in game order, including each line's scanned flag.</summary>
        public RegisterLine[] Lines
        {
            get; init;
        } = Array.Empty<RegisterLine>();

        /// <summary>The complete change stack snapshot.</summary>
        public RegisterChange[] ChangeItems
        {
            get; init;
        } = Array.Empty<RegisterChange>();

        /// <summary>The number of bag lines the host has already scanned, derived from the
        /// snapshot so a render can restore the customer's game counter without extra wire state.</summary>
        internal int ScannedCount()
        {
            var count = 0;
            var lines = Lines;
            for (var i = 0; lines != null && i < lines.Length; i++)
            {
                if (lines[i] != null && lines[i].Scanned)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>
    /// The extended-information value: one register state plus the correlation metadata the
    /// transport needs. Keeping <see cref="ActionId"/> here leaves <see cref="RegisterState"/>
    /// pure domain data, and <see cref="Rejected"/> tells the acting client to undo its prediction
    /// before this snapshot is applied. As an <see cref="IActionValue"/> the framework settles the
    /// correlated action exactly once around the client's snapshot handler.
    /// Host-only: a client writes register state through its validated intent lane.
    /// </summary>
    [HostOnlyInformation]
    internal sealed class RegisterSnapshot : ExtendGameObjectInformation<InteractableCashierCounter>,
        IActionValue
    {
        public RegisterState State
        {
            get; init;
        }

        /// <summary>The client action this snapshot resolves, or <see cref="Guid.Empty"/>
        /// for a host-originated change.</summary>
        public Guid ActionId
        {
            get; init;
        }

        /// <summary>True only for the pre-intent state published by the host's rejection hook.</summary>
        public bool Rejected
        {
            get; init;
        }
    }

    /// <summary>The counter phase and payment scalars, grouped so the client applies a snapshot's
    /// payment state as one value instead of an eleven-parameter method.</summary>
    internal sealed class RegisterPhase
    {
        public ECashierCounterState State
        {
            get; init;
        }

        public bool UsingCard
        {
            get; init;
        }

        public double Paid
        {
            get; init;
        }

        public double Total
        {
            get; init;
        }

        public double CustomerTotal
        {
            get; init;
        }

        public double Change
        {
            get; init;
        }

        public bool ChangeReady
        {
            get; init;
        }

        public bool ChangeStarted
        {
            get; init;
        }

        public bool TooMuchChange
        {
            get; init;
        }
    }

    /// <summary>One authoritative bag line. A card line carries the card's data; an item line
    /// carries its item type. The index in <see cref="RegisterState.Lines"/> is the game's bag
    /// position within its kind.</summary>
    internal sealed class RegisterLine
    {
        public bool IsCard
        {
            get; init;
        }

        public EItemType ItemType
        {
            get; init;
        }

        public CardData Card
        {
            get; init;
        }

        public float Price
        {
            get; init;
        }

        public bool Scanned
        {
            get; init;
        }
    }

    /// <summary>One authoritative change denomination. <see cref="Slot"/> is the denomination's
    /// game index, not its list position; <see cref="Count"/> is the absolute number given.</summary>
    internal sealed class RegisterChange
    {
        public int Slot
        {
            get; init;
        }

        public bool IsCoin
        {
            get; init;
        }

        public double Value
        {
            get; init;
        }

        public int Count
        {
            get; init;
        }
    }
}
