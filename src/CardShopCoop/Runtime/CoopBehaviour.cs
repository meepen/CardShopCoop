using System;

namespace CardShopCoop.Runtime
{
    /// <summary>
    /// Internal behaviour base. It is a superset of the public <see cref="Api.CoopBehaviour"/>:
    /// built-in modules use the rich <see cref="RuntimeContext"/>, while the public base exposes
    /// the same live session to external mods through <see cref="Api.CoopBehaviour.Context"/>.
    /// </summary>
    public abstract class CoopBehaviour : Api.CoopBehaviour
    {
        protected CoopRuntimeContext RuntimeContext
        {
            get;
            private set;
        }

        internal void SetRuntimeContext(CoopRuntimeContext context)
        {
            RuntimeContext = context ?? throw new ArgumentNullException(nameof(context));
            SetContext(context);
        }
    }
}
