using CardShopCoop.Api;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Tutorial
{
    /// <summary>One client tutorial increment. The client's own vanilla call already advanced its
    /// UI; the host validates the increment, applies it through the game, and publishes the
    /// resulting <see cref="TutorialState"/> under this action id.</summary>
    [NetworkMessage]
    public sealed class TutorialActionIntent : ActionMessage
    {
        /// <summary>The condition the local game path just advanced or decreased.</summary>
        public ETutorialTaskCondition Condition;

        /// <summary>The amount vanilla added locally. The host accepts progress only.</summary>
        public float Increment;

        public override void OnApply() => TutorialActions.Add(Condition, Increment);
    }
}
