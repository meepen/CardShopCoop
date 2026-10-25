using CardShopCoop.Api;
using CardShopCoop.Net;

namespace CardShopCoop.CustomTv
{
    /// <summary>One client playback intent. The host applies it once and publishes a new
    /// <see cref="TvState"/>.</summary>>
    [NetworkMessage]
    public sealed class TvOpMessage : ActionMessage
    {
        public byte Op;
        public string Url;
        public string Title;
        public double Value;
    }
}
