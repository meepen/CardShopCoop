namespace CardShopCoop.Modules.Grading
{
    /// <summary>The intentionally small cross-module grading surface.</summary>
    public static class GradingApi
    {
        public static bool Present => GradingInterop.Present;

        public static int Encoded(CardData card) => GradingInterop.Encoded(card);

        public static int Actual(int encoded) => GradingInterop.Actual(encoded);

        public static bool Remember(CardData card) => GradingInterop.Remember(card);

        public static void Reset() => GradingInterop.Reset();
    }
}
