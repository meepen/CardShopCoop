using CardShopCoop.Net;

namespace CardShopCoop.Net.Messages
{
    [NetworkMessage(MsgType.TvState, Policy = MessagePolicy.ClientOnly)]
    public sealed class TvStateMessage : INetMessage
    {
        /// <summary>True = the COMPLETE state (join catch-up / explicit re-baseline). False = ONE
        /// slice identified by <see cref="Index"/>; state not carried by a partial slice is
        /// UNCHANGED, never removed.</summary>
        public bool Full = true;

        /// <summary>Slice ordinal for a partial message; -1 for a full message.</summary>
        public int Index = -1;

        public string SourceUrl;
        public string StreamUrl;
        public string StreamTitle;
        public string PlaylistUrl;
        public bool IsLive;
        public bool IsSegmentedVod;
        public bool IsPlaylist;
        public int PlaylistIndex;
        public double Position;
        public bool Paused;
        public bool PoweredOff;
        public bool Shuffle;
        public bool Barrier;
        public bool Resume;
        public int Generation;

        public MsgType Type
        {
            get
            {
                return MsgType.TvState;
            }
        }
    }

    [NetworkMessage(MsgType.TvOp, Policy = MessagePolicy.HostOnlyInGame)]
    public sealed class TvOpMessage : INetMessage
    {
        public byte Op;
        public string Url;
        public string Title;
        public double Value;

        public MsgType Type
        {
            get
            {
                return MsgType.TvOp;
            }
        }
    }
}
