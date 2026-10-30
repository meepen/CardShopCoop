using System;
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

        /// <summary>The client's pre-increment state. Restored when the host refuses the
        /// increment, before the host's rejected authoritative value is applied.</summary>
        [NonSerialized]
        internal TutorialState Previous;

        public override void OnUndo(ActionResponseMessage response)
        {
            TutorialClientBehaviour.ApplyPrevious(Previous);
        }

        public override void OnMerge(ActionResponseMessage response)
        {
            // TutorialState is an IActionValue: the extended-information handler applies the
            // host's post-increment progression before this hook, so there is nothing left to
            // merge here beyond dropping the local undo snapshot.
            Previous = null;
        }
    }
}
