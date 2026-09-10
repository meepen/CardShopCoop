using System.Collections.Generic;
using CardShopCoop.Sync;

namespace CardShopCoop.Net.Messages
{
    /// <summary>Client -> host: one box possession update.
    /// A claim is a Held/Placing update on a Free box; a release is Free; a removal
    /// is Removed. The host validates ownership from its lease map.</summary>
    [NetworkMessage(MsgType.BoxUpdate, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class BoxUpdateMessage : INetMessage
    {
        public BoxWire Box;

        public MsgType Type
        {
            get
            {
                return MsgType.BoxUpdate;
            }
        }
    }

    /// <summary>Host -> clients: the authoritative box state (all families in one list).</summary>
    [NetworkMessage(MsgType.BoxSnapshot, Policy = MessagePolicy.ClientOnlyInGame)]
    public sealed class BoxSnapshotMessage : INetMessage
    {
        public List<BoxWire> Boxes = new List<BoxWire>();
        /// <summary>True for a complete snapshot (the client may sweep absent ids). False for
        /// a partial: only the listed boxes changed and the rest are unchanged.</summary>
        public bool Full;

        public MsgType Type
        {
            get
            {
                return MsgType.BoxSnapshot;
            }
        }
    }
}
