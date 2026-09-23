using System;
using CardShopCoop.Modules.Presence;
using UnityEngine;

namespace CardShopCoop
{
    public partial class CoopCore
    {
        internal static void EnqueueMainThread(Action action)
        {
            if (action == null)
            {
                return;
            }

            if (!TryEnqueueMainThread(action))
            {
                throw new InvalidOperationException("CoopCore is not running");
            }
        }

        internal static bool TryEnqueueMainThread(Action action)
        {
            if (action == null)
            {
                return false;
            }

            var core = Instance;
            if (core == null)
            {
                return false;
            }

            return core._dispatcher.TryEnqueue("external-main-thread", action);
        }

        /// <summary>Host: relay a customer speech bubble after vanilla has actually
        /// displayed it. Speech is a one-shot cosmetic event, so it uses the reliable lane.</summary>
        public static bool TryGetLocalPlayerPosition(out Vector3 position)
        {
            position = default(Vector3);
            return PresenceApi.TryGetLocalPlayerPosition(out position);
        }

        // what the local player is carrying (private fields; the game has no public API)
        internal static bool NativeTextInputFocused()
        {
            var sel = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
            if (sel == null)
            {
                return false;
            }

            var tmp = sel.GetComponent<TMPro.TMP_InputField>();
            return tmp != null && tmp.isFocused;
        }

    }
}


