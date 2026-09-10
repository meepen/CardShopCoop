using CardShopCoop.Net;

namespace CardShopCoop.Net.Messages
{
    [NetworkMessage(MsgType.TvState, Policy = MessagePolicy.ClientOnly)]
    public sealed class TvStateMessage : INetMessage
    {
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
