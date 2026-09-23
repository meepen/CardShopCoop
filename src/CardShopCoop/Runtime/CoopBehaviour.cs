using System;

namespace CardShopCoop.Runtime
{
    public abstract class CoopBehaviour : UnityEngine.MonoBehaviour
    {
        protected CoopRuntimeContext RuntimeContext
        {
            get;
            private set;
        }

        internal void SetRuntimeContext(CoopRuntimeContext context)
        {
            RuntimeContext = context ?? throw new ArgumentNullException(nameof(context));
        }

        // Registry-owned final safety net: feature shutdown methods unpatch their own
        // Harmony state, while this generic contract guarantees handler removal even when
        // a behaviour has no feature-specific shutdown implementation.
        internal void ShutdownLifecycle()
        {
            RuntimeContext?.Messages.UnregisterAttributedHandlers(this);
        }
    }
}
