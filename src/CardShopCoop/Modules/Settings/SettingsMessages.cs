using System;
using System.Collections.Generic;
using CardShopCoop.Modules.Prediction;
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
        public byte CashierIndex;
        public byte CashierFlags;
        public byte TableIndex;
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
        // For partial cashier/table/fee mutations this is the keyed element. A value below zero
        // retains compatibility with the legacy list-shaped partial payload.
        public int ItemIndex = -1;
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
    }

}
