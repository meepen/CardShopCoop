using System.Collections.Generic;
using CardShopCoop.Net;
using CardShopCoop.Sync;

namespace CardShopCoop.Net.Messages
{
    /// <summary>Protocol for playable card-table matches:
    /// a client requests or cancels a match, then the host validates and publishes its reservation.
    /// On completion the client reports the trusted result, duration, and any gifts it received.
    /// The host later applies the shared-world fee, tournament, gift, and save consequences.
    /// Match state is rostered by stable table identity rather than a mutable list index.
    /// Partial state uses one-entry slices; a host tombstone releases a reservation.</summary>
    /// MsgType 106-108 are introduced inside unreleased 1.4.0; WireVersion remains 104.
    /// Earlier 1.4.0-beta peers with the same plugin version can join but silently drop these
    /// frames; the release changelog carries "Both players must update."

    /// <summary>Client -> host: request to start or cancel a playable card-table match.</summary>
    [NetworkMessage(MsgType.PlayTableMatchRequest, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class PlayTableMatchRequest : INetMessage
    {
        public const byte OpStart = 0;
        public const byte OpCancel = 1;
        public const byte OpStarted = 2;

        /// <summary>Operation: 0=start, 1=cancel, 2=started.</summary>
        public byte Op;
        /// <summary>State epoch observed by the sender. Zero means no prior state was observed.</summary>
        public long Epoch;
        /// <summary>Latest reservation revision observed by the sender.</summary>
        public long Revision;
        /// <summary>Strictly increasing request number for this authenticated owner and session.</summary>
        public long RequestSequence;
        public string MatchId;

        /// <summary>Stable placed-table key, produced by
        /// <see cref="PlacedObjectIdentity.TryMakeObjectKey(int, InteractableObject, out int)"/>
        /// with kind 6 (the <c>ShelfManager.m_PlayTableList</c> population). It packs kind 6
        /// and the host-assigned session-local <c>PlacedObjectIdentity</c> ushort; it is not the
        /// table-list index.</summary>
        public int TableKey;

        /// <summary>Current m_PlayTableList index, retained only as a lookup hint/diagnostic;
        /// furniture changes can invalidate it.</summary>
        public byte TableIndex;

        public byte Seat;
        public bool SideA;
        public int DeckCardCount;

        public MsgType Type
        {
            get
            {
                return MsgType.PlayTableMatchRequest;
            }
        }
    }

    /// <summary>Host -> clients: authoritative playable-table match reservations. Full messages
    /// carry the complete roster for join catch-up or re-baselining. A partial message carries
    /// exactly one entry; omitted matches are UNCHANGED, never removed. The host releases a
    /// match with a tombstone entry having the same MatchId and <see cref="PlayTableMatchEntry.Phase"/>
    /// equal to zero.</summary>
    [NetworkMessage(MsgType.PlayTableMatchState, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class PlayTableMatchState : INetMessage
    {
        /// <summary>Incarnation of the reservation roster. A new epoch replaces all prior state.</summary>
        public long Epoch;

        public bool Full = true;
        public List<PlayTableMatchEntry> Matches = new List<PlayTableMatchEntry>();

        /// <summary>Latest per-table revision watermark, including omitted tables in a full frame.</summary>
        public Dictionary<int, long> TableRevisions = new Dictionary<int, long>();

        public MsgType Type
        {
            get
            {
                return MsgType.PlayTableMatchState;
            }
        }
    }

    /// <summary>One playable-table reservation. Phase 1 is active; phase 0 is a release
    /// tombstone and is not an active match. Other phases are reserved for future use.</summary>
    public sealed class PlayTableMatchEntry
    {
        public const byte StateReleased = 0;
        public const byte StateReserved = 1;
        public const byte StateStarted = 2;

        public string MatchId;
        public long Epoch;
        public long Revision;
        public int OwnerConn;
        public int TableKey;
        public byte TableIndex;
        public byte Seat;
        public bool SideA;
        /// <summary>Reservation state: released, reserved, or started.</summary>
        public int Phase;
    }

    /// <summary>Client -> host: final report for one playable card-table match.</summary>
    [NetworkMessage(MsgType.PlayTableMatchResult, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class PlayTableMatchResult : INetMessage
    {
        public const byte ResultWin = 1;
        public const byte ResultLoss = 2;
        public const byte ResultDraw = 3;

        public string MatchId;
        /// <summary>Epoch and revision of the reservation on which this result is based.</summary>
        public long Epoch;
        public long Revision;
        public int Nonce;
        public byte Result;
        public float DurationSeconds;
        public List<EItemType> Gifts = new List<EItemType>();

        public MsgType Type
        {
            get
            {
                return MsgType.PlayTableMatchResult;
            }
        }
    }
}
