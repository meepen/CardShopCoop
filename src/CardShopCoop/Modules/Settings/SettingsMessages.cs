using System;
using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Settings
{
    // The operation numbers are retained from the legacy settings channel.  They are part of
    // the payload contract even though the DTO now belongs to this feature module.
    [NetworkMessage]
    public sealed class SettingsOpMessage : ActionMessage
    {
        public byte Op;
        public int Index;
        public ECardExpansionType Expansion;
        public float Fee;
        // Cashier/table intents name their target by the live placed object, never a list index
        // or a placement key: a concurrent placement reorders the local list, and a key would
        // re-introduce the entity-ID fallback this protocol removes.
        public InteractableCashierCounter Cashier;
        public byte CashierFlags;
        public InteractablePlayTable Table;
        public int TableNumber;

        // The game already applied the mutation locally. These capture what an undo must restore;
        // the host answer carries the authoritative state. Local state only, never serialized.
        [NonSerialized]
        public EGameEventFormat PreviousFormat;

        [NonSerialized]
        public ECardExpansionType PreviousExpansion;

        [NonSerialized]
        public float PreviousFee;

        [NonSerialized]
        public byte PreviousFlags;

        [NonSerialized]
        public int PreviousNumber;

        public override void OnUndo(ActionResponseMessage response)
            => SettingsClientBehaviour.Active?.UndoOperation(this);

        public override void OnMerge(ActionResponseMessage response)
            => SettingsClientBehaviour.Active?.MergeOperation(this, response);
    }

    /// <summary>The host's authoritative state after one settings operation. The framework sets
    /// <see cref="ActionResponseMessage.Rejected"/>; the state is applied either way.</summary>
    [NetworkMessage]
    public sealed class SettingsOpResponse : ActionResponseMessage
    {
        public SettingsStateMessage State;
    }

    [NetworkMessage]
    public sealed class SettingsStateMessage : INetMessage
    {
        public bool Full = true;
        public int Index = -1;
        // For partial fee mutations this is the price-list element. Cashier/table partials use the
        // live objects below instead, so a reordered list cannot target the wrong object.
        public int ItemIndex = -1;
        // Live cashier counter/play table a partial mutation targets.
        public InteractableCashierCounter Cashier;
        public InteractablePlayTable Table;
        public int GameEventFormat;
        public int PendingGameEventFormat;
        public ECardExpansionType GameEventExpansion;
        public ECardExpansionType PendingGameEventExpansion;
        // -1 preserves compatibility with older payloads that implied the length from the
        // value list. New payloads always carry the authoritative length so removals can trim
        // client tails even when the removed element has no replacement value.
        public int GameEventPriceCount = -1;
        public int CashierCount = -1;
        public int TableCount = -1;
        public bool Tombstone;
        public List<float> GameEventPrices = new();
        public List<byte> CashierFlags = new();
        public List<byte> TableNumbers = new();
        // Full-baseline only: the live cashier counter/play table of each entry in
        // CashierFlags/TableNumbers, parallel by position within the baseline payload. A null
        // element could not be resolved on this peer; the client logs and drops that entry
        // instead of trusting the payload position. Partial mutations use the scalars above.
        public List<InteractableCashierCounter> Cashiers = new();
        public List<InteractablePlayTable> Tables = new();
    }

}
