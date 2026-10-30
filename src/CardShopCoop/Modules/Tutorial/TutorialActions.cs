namespace CardShopCoop.Modules.Tutorial
{
    /// <summary>The game path for one validated tutorial increment. The host runs it from the
    /// action handler.</summary>
    internal static class TutorialActions
    {
        internal static void Add(ETutorialTaskCondition condition, float increment)
            => TutorialManager.AddTaskValue(condition, increment);
    }
}
