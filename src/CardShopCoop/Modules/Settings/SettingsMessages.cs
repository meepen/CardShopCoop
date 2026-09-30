using System;
using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Settings
{
    // The operation numbers are retained from the legacy settings channel.  They are part of
    // the payload contract even though the DTO now belongs to this feature module.
    [NetworkMessage]
    public sealed class SettingsOpMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public byte Op;
        public int Index;
        public ECardExpansionType Expansion;
        public float Fee;
        // Cashier/table intents address their target by the stable placement key, never a list
        // index: a concurrent placement (a purchase, a move, a removal) reorders the local list,
        // so an index could apply the edit to a different object.
        public int CashierKey;
        public byte CashierFlags;
        public int TableKey;
        public int TableNumber;
    }

    [NetworkMessage]
    public sealed class SettingsStateMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public bool Full = true;
        public int Index = -1;
        // For partial fee mutations this is the price-list element. Cashier/table partials use the
        // stable placement keys below instead, so a reordered list cannot target the wrong object.
        public int ItemIndex = -1;
        // Stable placement keys of the cashier counter/play table a keyed partial mutation targets.
        public int CashierKey;
        public int TableKey;
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
        // Full-baseline only: the stable placement key of each entry in CashierFlags/TableNumbers,
        // parallel by position within the baseline payload. A key of 0 means the host could not
        // compute an identity for that element; the client then applies that entry to nothing
        // instead of trusting the payload position. Partial mutations use the scalar keys above.
        public List<int> CashierKeys = new();
        public List<int> TableKeys = new();
    }

}
