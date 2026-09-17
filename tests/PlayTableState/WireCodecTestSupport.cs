using Newtonsoft.Json;

namespace CardShopCoop.Net
{
    public static class Msg
    {
        public const int FrameHeaderSize = 4;
        public const int TypeSize = 1;
    }

    public enum MsgType
    {
        PlayTableMatchRequest = 106,
        PlayTableMatchState = 107,
        PlayTableMatchResult = 108,
    }

    public enum MessagePolicy
    {
        HostOnlyInGame,
        ClientOnlyInGame,
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class NetworkMessageAttribute : Attribute
    {
        public NetworkMessageAttribute(MsgType type)
        {
        }

        public MessagePolicy Policy
        {
            get; set;
        }
    }

    public static class WireSettings
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Error,
        };
    }
}

public enum EItemType
{
    None,
}
