using System.Collections.Generic;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Tournament
{
    /// <summary>Host-authoritative tournament schedule, prizes, participation and bracket digest.</summary>
    [NetworkMessage]
    public sealed class TournamentStateMessage : INetMessage
    {
        /// <summary>Complete join baseline when true; otherwise one numbered repair slice.</summary>
        public bool Full = true;

        public byte Flags;
        public int MaxPlayerCount;
        public int SignedUpCustomerCount;
        public int FinishedCurrentRoundCustomerCount;
        public int CurrentRound;
        public int MaxRound;
        public float Fee;
        public float TotalValue;
        public List<TournamentPrizeSlot> PrizeSlots = new();
        public List<TournamentBracketEntry> Bracket = new();

        // Game 1.0 added player participation to the tournament screen.
        public bool IsPlayerRegistered;
        public bool PlayerIsTournamentCustomer;
        public bool PlayerIsTournamentWin;
        public bool PlayerHasRegisteredResult;
        public int PlayerTournamentCustomerPlayTableIndex;
        public int PlayerTournamentWinCount;
        public int PlayerTournamentWinPoints;
        public int PlayerTournamentPlacementIndex;
        public TournamentPlayerState PlayerState;
    }

    [NetworkMessage]
    public sealed class TournamentDeltaMessage : INetMessage
    {
        public byte Kind;
        public TournamentStateMessage Header;
        public int PrizeIndex;
        public TournamentPrizeSlot Prize;
        public int BracketStart;
        public int BracketCount;
        public List<TournamentBracketEntry> Bracket = new();
    }

    /// <summary>Complete CustomerTournamentData state needed when a tournament play table reloads.</summary>
    public sealed class TournamentPlayerState
    {
        public bool IsTournamentCustomer;
        public bool IsTournamentWin;
        public bool HasFinishCurrentTournamentRound;
        public bool HasRegisteredTournamentStart;
        public bool HasRegisteredTournamentResult;
        public int TournamentCustomerIndex;
        public int TournamentCustomerSortedIndex;
        public int TournamentCustomerPlayTableIndex;
        public int TournamentWinCount;
        public int TournamentWinPoints;
        public int TournamentOMW;
        public int TournamentOOMW;
        public int CurrentPrizeDataIndex;
        public int TournamentPlacementIndex;
        public int CharacterModelIndex;
        public bool IsFemale;
        public List<int> TournamentOpponentIndexList = new();
        public List<TournamentPrizeEntry> PrizeDataList = new();
        public bool HasTargetPrizeData;
        public TournamentPrizeEntry TargetPrizeData;
    }

    public sealed class TournamentPrizeSlot
    {
        public List<TournamentPrizeEntry> Prizes = new();
    }

    public sealed class TournamentPrizeEntry
    {
        public bool HasCard;
        public CardData Card;
        public EItemType ItemType;
        public int Count;
    }

    public sealed class TournamentBracketEntry
    {
        public byte SortedIndex;
        public int ModelIndex;
        public byte Flags;
        public int WinCount;
        public int WinPoints;
        public int OMW;
        public int OOMW;
    }
}
