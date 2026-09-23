using System.Collections.Generic;

namespace CardShopCoop.Modules.Grading
{
    public readonly struct GradingOffer
    {
        public readonly int ConnectionId;
        public readonly string Who;
        public readonly int Count;

        public GradingOffer(int connectionId, string who, int count)
        {
            ConnectionId = connectionId;
            Who = who;
            Count = count;
        }
    }

    /// <summary>Legacy UI surface retained as an empty read-only surface.</summary>
    public static class GradingUiApi
    {
        private static readonly IReadOnlyList<GradingOffer> Empty = new List<GradingOffer>();

        public static IReadOnlyList<GradingOffer> Offers => Empty;

        public static bool Adopt(int connectionId) => false;
    }
}
