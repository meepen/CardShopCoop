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
        private int _authorityDispatch;

        protected CoopRuntimeContext RuntimeContext
        {
            get;
            private set;
        }

        /// <summary>True while this behaviour is dispatching one of its own co-op handlers. The
        /// router and the extended-information store set it, so a module observer can tell an
        /// authoritative application apart from a local player action without keeping its own
        /// flag.</summary>
        internal bool IsApplyingAuthority => _authorityDispatch > 0;

        /// <summary>Write scope for an authoritative game change this behaviour performs outside a
        /// handler (a lifecycle retry, an ownership re-apply). Inside a handler the framework
        /// already marks the dispatch, so this only covers the rest.</summary>
        internal ObservationScope Writes { get; } = new();

        internal void BeginAuthorityDispatch() => _authorityDispatch++;

        internal void EndAuthorityDispatch()
        {
            if (_authorityDispatch > 0)
            {
                _authorityDispatch--;
            }
        }

        internal void SetRuntimeContext(CoopRuntimeContext context)
        {
            RuntimeContext = context ?? throw new ArgumentNullException(nameof(context));
            SetContext(context);
        }
    }
}
