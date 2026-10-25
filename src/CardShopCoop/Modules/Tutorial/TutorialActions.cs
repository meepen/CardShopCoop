namespace CardShopCoop.Modules.Tutorial
{
    /// <summary>The game path for one validated tutorial increment. The host runs it from the
    /// action message's <c>OnApply</c>; a client never calls it, because it applies its own vanilla
    /// call and only replays that through the action ledger.</summary>
    internal static class TutorialActions
    {
        internal static void Add(ETutorialTaskCondition condition, float increment)
            => TutorialManager.AddTaskValue(condition, increment);
    }
}
