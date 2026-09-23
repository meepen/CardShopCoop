using CardShopCoop.Net;

namespace CardShopCoop.Modules.GameTime
{
    /// <summary>Host-authoritative current game time.</summary>
    [NetworkMessage]
    public sealed class DayTimeMessage : INetMessage
    {
        public int Day;
        public int Hour;
        public int Minute;
        public float MinuteFloat;
        public bool ShopOnceOpen;
        public int TimeOfDayIndex;
        public bool HasDayEnded;
    }

}
