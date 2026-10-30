using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Runtime;

namespace CardShopCoop.Modules.Deodorant
{
    /// <summary>Applies host-authoritative deodorant state through the game's live surfaces.</summary>
    internal static class DeodorantState
    {
        /// <summary>Restores the acting client's spray content. The held spray may have changed
        /// between the prediction and its resolution; a missing spray is a normal drop.</summary>
        internal static void TrySetContent(InteractionPlayerController controller, float content)
        {
            var target = controller != null ? controller : SceneRef<InteractionPlayerController>.Get();
            if (target == null)
            {
                return;
            }

            DeodorantInterop.TrySetContent(target, content);
        }

        /// <summary>Applies the host's post-spray content and customer results to the acting
        /// client. Customer entries without a live local customer are the normal unknown-entity
        /// drop.</summary>
        internal static void ApplySprayTick(InteractionPlayerController controller, float content,
            IList<DeodorantCustomerState> states)
        {
            TrySetContent(controller, content);
            ApplyCustomers(states);
        }

        internal static void ApplyCustomers(IList<DeodorantCustomerState> states)
        {
            for (var i = 0; states != null && i < states.Count; i++)
            {
                var state = states[i];
                if (state?.Customer == null)
                {
                    CoopPlugin.Log.LogWarning("[deodorant] dropped smell state with no live customer.");
                    continue;
                }

                DeodorantInterop.ApplyCustomerState(state.Customer, state.IsSmelly, state.SmellyMeter);
            }
        }
    }
}
