namespace CardShopCoop.Modules.Grading
{
    /// <summary>The intentionally small cross-module grading surface.</summary>
    public static class GradingApi
    {
        public static bool Present => GradingInterop.Present;

        public static int Encoded(CardData card) => GradingInterop.Encoded(card);

        public static int Actual(int encoded) => GradingInterop.Actual(encoded);

        public static bool Remember(CardData card) => GradingInterop.Remember(card);

        /// <summary>World signals that a predicted collection removal was rejected by the host,
        /// so the card is back in the album. If that card sits in an unsubmitted grading
        /// selection, it must leave the selection too or the player is left with a reserved slot
        /// that the host never accepted (and a duplicated card if the selection is cancelled).
        /// No-op when this process is not hosting a grading client.</summary>
        public static void OnCardRemovalRefused(CardData card, int amount)
            => GradingClientBehaviour.OnCardRemovalRefused(card, amount);

        public static void Reset() => GradingInterop.Reset();
    }
}
