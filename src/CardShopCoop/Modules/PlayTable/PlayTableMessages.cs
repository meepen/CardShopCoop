using System;
using System.Collections.Generic;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.PlayTable
{
    /// <summary>Complete table visuals and live match leases for a joining peer.</summary>
    [NetworkMessage]
    public sealed class PlayTableBaselineMessage : INetMessage
    {
        public List<PlayTableEntry> Tables = new();
        public List<PlayTableMatchEntry> Matches = new();
    }

    /// <summary>A table-level or single-seat visual change.</summary>
    [NetworkMessage]
    public sealed class PlayTableVisualDeltaMessage : IPredictedMessage
    {
        public const byte TableUpsert = 1;
        public const byte TableRemove = 2;
        public const byte SeatUpdate = 3;

        public Guid PredictionId
        {
            get; set;
        }
        public byte Operation;
        public int TableKey;
        public byte TableIndex;
        public bool Occupied;
        public bool Boxed;
        public byte Seat = byte.MaxValue;
        public PlayTableSeatEntry SeatState;
        public List<PlayTableSeatEntry> Seats = new();
    }

    /// <summary>An accepted match lease upsert or release keyed by match identity.</summary>
    [NetworkMessage]
    public sealed class PlayTableMatchDeltaMessage : IPredictedMessage
    {
        public const byte Upsert = 1;
        public const byte Release = 2;

        public Guid PredictionId
        {
            get; set;
        }
        public byte Operation;
        public string MatchId;
        public int TableKey;
        public PlayTableMatchEntry Match;
    }

    /// <summary>A client request in the match lease lifecycle.</summary>
    [NetworkMessage]
    public sealed class PlayTableMatchIntentMessage : IPredictedMessage
    {
        public const byte OpStart = 0;
        public const byte OpCancel = 1;
        public const byte OpStarted = 2;
        public const byte OpRelease = 3;

        public Guid PredictionId
        {
            get; set;
        }
        public byte Op;
        public string MatchId;
        public int TableKey;
        public byte TableIndex;
        public byte Seat;
        public bool SideA;
        public int DeckCardCount;
    }

    /// <summary>Stable-key table interaction sent before its local prediction.</summary>
    [NetworkMessage]
    public sealed class PlayTableIntentMessage : IPredictedMessage
    {
        public const byte IntentKindPlayTable = 1;
        public const byte IntentKickTable = 1;
        public const byte IntentBoxTable = 2;

        public Guid PredictionId
        {
            get; set;
        }
        public byte Kind = IntentKindPlayTable;
        public byte Action;
        public byte Target;
        public int ObjectKey;
    }

    public sealed class PlayTableEntry
    {
        public int TableKey;
        public byte Index;
        public bool Occupied;
        public bool Boxed;
        public List<PlayTableSeatEntry> Seats = new();
    }

    public sealed class PlayTableSeatEntry
    {
        public bool Active;
        public bool PlayerSeat;
        public EItemType PlayMat;
        public EItemType DeckBox;
        public EItemType Comic;
    }

    public sealed class PlayTableMatchEntry
    {
        public const int StateReleased = 0;
        public const int StateReserved = 1;
        public const int StateStarted = 2;

        public string MatchId;
        public int OwnerConn;
        public int TableKey;
        public byte TableIndex;
        public byte Seat;
        public bool SideA;
        public int Phase;
    }
}
