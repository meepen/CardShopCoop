using System.Collections.Generic;

namespace CardShopCoop.Net.Messages
{
    // ------------------------------------------------------------------ warehouse records

    /// <summary>Host -> clients: the authoritative warehouse stored-box lists, normalised to the
    /// ordered (itemType, amount, big) form so a record-backed host (game 1.00) and a live-box host
    /// (the public/legacy build) emit the same message. Sent on every real change, plus as a
    /// gradual sweep of one batch of compartments per slice; <see cref="Full"/> marks the complete
    /// state (join catch-up / explicit re-baseline) versus a partial sweep slice.</summary>
    [NetworkMessage(MsgType.WarehouseState, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class WarehouseStateMessage : INetMessage
    {
        public bool Full;
        public List<WarehouseCompartmentEntry> Compartments = new List<WarehouseCompartmentEntry>();

        /// <summary>True when the HOST's warehouse is live-box backed (game 1.0). A live guest then
        /// leaves its racks to the normal box channel; false means the host is record backed and a
        /// live guest must materialize these entries itself. Appended last: append-only wire.</summary>
        public bool HostLiveBoxes;

        public MsgType Type
        {
            get
            {
                return MsgType.WarehouseState;
            }
        }
    }

    /// <summary>One warehouse compartment's ordered record list.</summary>
    public sealed class WarehouseCompartmentEntry
    {
        public ushort ShelfId;        // PlacedObjectIdentity of the WarehouseShelf when known (0 = unknown)
        public int ShelfIndex;        // GetWarehouseIndex()
        public int CompartmentIndex;  // GetIndex()
        public List<StoredBoxEntry> Records = new List<StoredBoxEntry>();
    }

    /// <summary>One stored box record (mirrors the game's StoredBoxRecord). ItemType is typed as
    /// EItemType so the wire converter translates modded ids host<->local like every other item
    /// boundary; the record APIs themselves are reflection-only because 0.70.3 lacks them.</summary>
    public sealed class StoredBoxEntry
    {
        public EItemType ItemType;
        public int Amount;
        public bool Big;
    }

    // ------------------------------------------------------------------ warehouse ops

    /// <summary>Client -> host (game 1.0+): a warehouse store or take request. Mirrors the
    /// empty-box-storage op pattern; the host validates and runs vanilla, then the authoritative
    /// WarehouseState/BoxSnapshot messages are the echo.</summary>
    [NetworkMessage(MsgType.WarehouseOp, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class WarehouseOpMessage : INetMessage
    {
        public const byte OpStore = 1;
        public const byte OpTake = 2;

        public byte Op;
        public int RequestId;

        public ushort ShelfId;       // PlacedObjectIdentity of the rack (0 = unknown)
        public int ShelfIndex;       // fallback address
        public int CompartmentIndex;

        // OpStore
        public ushort BoxId;
        public EItemType ItemType;
        public int Amount;
        public bool IsBig;

        public MsgType Type
        {
            get
            {
                return MsgType.WarehouseOp;
            }
        }
    }

    /// <summary>Host -> requesting client: the outcome of a warehouse take. A store needs no
    /// result - the authoritative WarehouseState/BoxSnapshot are its echo.</summary>
    [NetworkMessage(MsgType.WarehouseTakeResult, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class WarehouseTakeResultMessage : INetMessage
    {
        public int RequestId;
        public ushort ShelfId;
        public int ShelfIndex;
        public int CompartmentIndex;

        public bool Accepted;
        public ushort BoxId;          // 0 when rejected
        public int RemainingRecords;

        public MsgType Type
        {
            get
            {
                return MsgType.WarehouseTakeResult;
            }
        }
    }
}
