using System;
using System.Collections.Generic;
using CardShopCoop.Api;
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
    public sealed class PlayTableVisualDeltaMessage : INetMessage
    {
        public const byte TableUpsert = 1;
        public const byte TableRemove = 2;
        public const byte SeatUpdate = 3;
        public byte Operation;
        /// <summary>The live table this visual state describes.</summary>
        public InteractablePlayTable Table;
        public bool Occupied;
        public bool Boxed;
        public byte Seat = byte.MaxValue;
        public PlayTableSeatEntry SeatState;
        public List<PlayTableSeatEntry> Seats = new();
    }

    /// <summary>An accepted match lease upsert or release keyed by match identity.</summary>
    [NetworkMessage]
    public sealed class PlayTableMatchDeltaMessage : INetMessage
    {
        public const byte Upsert = 1;
        public const byte Release = 2;
        public byte Operation;
        public string MatchId;
        /// <summary>The live table the match lease belongs to.</summary>
        public InteractablePlayTable Table;
        public PlayTableMatchEntry Match;
    }

    /// <summary>The acting client's authoritative table/match state after one predicted action.</summary>
    [NetworkMessage]
    public sealed class PlayTableActionResponse : ActionResponseMessage
    {
        public InteractablePlayTable Table;
        /// <summary>The host's post-action table visual entry, or null when the table is gone.</summary>
        public PlayTableEntry Entry;
        /// <summary>The host's match lease for the table, or null when none.</summary>
        public PlayTableMatchEntry Match;
        /// <summary>True when the action released the table's match lease.</summary>
        public bool MatchRemoved;
    }

    /// <summary>A client request to reserve the requested seat for a new match lease.</summary>
    [NetworkMessage]
    public sealed class StartTableMatchIntent : ActionMessage
    {
        public string MatchId;
        /// <summary>The live table this lease request targets.</summary>
        public InteractablePlayTable Table;
        public byte Seat;
        public bool SideA;
        public int DeckCardCount;
        /// <summary>True when vanilla already entered the reserved game, so the host publishes
        /// the lease as started rather than merely reserved.</summary>
        public bool Started;

        [NonSerialized]
        internal PlayTableClientBehaviour Client;

        [NonSerialized]
        internal PlayTableClientBehaviour.LaunchState Launch;

        public override void OnUndo(ActionResponseMessage response)
        {
            Client?.UndoLaunch(Table, Launch);
        }

        public override void OnMerge(ActionResponseMessage response)
        {
            var client = Client;
            Client = null;
            Launch = null;
            client?.ApplyResponse(response);
        }
    }

    /// <summary>A client request to release its lease after vanilla left the game.</summary>
    [NetworkMessage]
    public sealed class ReleaseTableMatchIntent : ActionMessage
    {
        public string MatchId;
        /// <summary>The live table the lease belongs to.</summary>
        public InteractablePlayTable Table;

        [NonSerialized]
        internal PlayTableClientBehaviour Client;

        [NonSerialized]
        internal PlayTableClientBehaviour.LaunchState Launch;

        public override void OnUndo(ActionResponseMessage response)
        {
            Client?.RestoreReleased(Launch);
        }

        public override void OnMerge(ActionResponseMessage response)
        {
            var client = Client;
            Client = null;
            Launch = null;
            client?.ApplyResponse(response);
        }
    }

    /// <summary>A client request to stop an occupied table's game.</summary>
    [NetworkMessage]
    public sealed class KickTableIntent : ActionMessage
    {
        /// <summary>The live table the kick targets; never a list index or placement key.</summary>
        public InteractablePlayTable Table;

        [NonSerialized]
        internal PlayTableClientBehaviour Client;

        [NonSerialized]
        internal PlayTableEntry Before;

        public override void OnUndo(ActionResponseMessage response)
        {
            Client?.RestoreEntry(Table, Before);
        }

        public override void OnMerge(ActionResponseMessage response)
        {
            var client = Client;
            Client = null;
            Before = null;
            client?.ApplyResponse(response);
        }
    }

    public sealed class PlayTableEntry
    {
        /// <summary>The live table this entry describes.</summary>
        public InteractablePlayTable Table;
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
        /// <summary>The live table this match lease occupies.</summary>
        public InteractablePlayTable Table;
        public byte Seat;
        public bool SideA;
        public int Phase;
    }
}
