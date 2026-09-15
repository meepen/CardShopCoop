using System.Collections.Generic;

namespace CardShopCoop.Net.Messages
{
    // ------------------------------------------------------------------ warehouse records

    /// <summary>Host -> clients (game 1.0+): the authoritative warehouse stored-box record
    /// lists. In 1.0 a stored box is no longer a live InteractablePackagingBox_Item - it is a
    /// serialized StoredBoxRecord owned by a ShelfCompartment - so it is invisible to the box
    /// engine and needs its own channel. The host currently sends the full list on every change
    /// (Full=true); partial-by-compartment is reserved for a future refinement.</summary>
    [NetworkMessage(MsgType.WarehouseState, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class WarehouseStateMessage : INetMessage
    {
        public bool Full;
        public List<WarehouseCompartmentEntry> Compartments = new List<WarehouseCompartmentEntry>();

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
