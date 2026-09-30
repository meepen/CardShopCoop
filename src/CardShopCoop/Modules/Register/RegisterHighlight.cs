using System.Reflection;
using CardShopCoop.Runtime;
using CardShopCoop.Util;

namespace CardShopCoop.Modules.Register
{
    /// <summary>
    /// The register counter's local interaction highlight: the green hover outline, the highlight
    /// object, and the interaction tooltips. Vanilla raises it from this machine's own raycast
    /// alone, so a register someone else already owns would still invite this player to take it
    /// over. <see cref="Clear"/> also drops the counter as the controller's current raycast
    /// target: the controller only re-runs <c>OnRaycasted</c> when the aimed target changes and
    /// re-adds the held target's tooltips on every DefaultState transition, so a suppressed
    /// counter would otherwise keep its prompt and still receive click callbacks. A detached
    /// target is re-raycast on the next frame, where the ownership decision runs again and a
    /// register that is free by then highlights normally.
    /// </summary>
    internal static class RegisterHighlight
    {
        private static FieldInfo _raycastedField;
        private static bool _raycastedFieldResolved;
        private static FieldInfo _controllerTargetField;
        private static bool _controllerTargetFieldResolved;

        private static bool IsRaycast(InteractableCashierCounter counter)
        {
            if (counter == null)
            {
                return false;
            }

            if (!_raycastedFieldResolved)
            {
                _raycastedField = ReflectionSurface.OptionalField(
                    typeof(InteractableObject), "m_IsRaycasted");
                _raycastedFieldResolved = true;
            }

            return _raycastedField?.GetValue(counter) is bool value && value;
        }

        private static FieldInfo ControllerTargetField()
        {
            if (!_controllerTargetFieldResolved)
            {
                _controllerTargetField = ReflectionSurface.OptionalField(
                    typeof(InteractionPlayerController), "m_CurrentRaycastObject");
                _controllerTargetFieldResolved = true;
            }

            return _controllerTargetField;
        }

        /// <summary>Drops this counter as the controller's current target when it is the aimed
        /// one. The controller never re-evaluates a target it already holds, so leaving a
        /// suppressed counter there would keep its tooltips alive and still route a click to it.
        /// </summary>
        private static void DetachIfAimed(InteractableCashierCounter counter)
        {
            if (counter == null)
            {
                return;
            }

            var controller = SceneRef<InteractionPlayerController>.Get();
            var targetField = controller == null ? null : ControllerTargetField();
            if (targetField == null || !ReferenceEquals(targetField.GetValue(controller), counter))
            {
                return;
            }

            targetField.SetValue(controller, null);
        }

        /// <summary>Clears the counter's highlight and detaches it as the aimed target, so a
        /// register that just became someone else's cannot keep advertising itself to a player
        /// who was already aiming at it.</summary>
        internal static void Clear(InteractableCashierCounter counter)
        {
            if (counter == null)
            {
                return;
            }

            if (IsRaycast(counter))
            {
                counter.OnRaycastEnded();
            }

            DetachIfAimed(counter);
        }
    }
}
