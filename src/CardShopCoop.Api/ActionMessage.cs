using System;
using CardShopCoop.Net;

namespace CardShopCoop.Api
{
    /// <summary>
    /// A predicted, validated, undoable world interaction. The client applies the prediction
    /// locally, sends the action through <see cref="ICoopContext"/> (or <see cref="ICoopPeer.Send"/>),
    /// and the host validates it and answers with an optional <see cref="ActionResponseMessage"/>.
    ///
    /// The framework calls <see cref="OnUndo"/> only when the host rejects the prediction, then
    /// calls <see cref="OnMerge"/> exactly once on every resolution - after <see cref="OnUndo"/>
    /// when rejected. <see cref="OnMerge"/> is where the authoritative state in the response is
    /// applied and local artifacts are cleaned up.
    ///
    /// A plain, non-predicted notification or continuous state update is an
    /// <see cref="INetMessage"/> instead.
    /// </summary>
    public abstract class ActionMessage : INetMessage
    {
        /// <summary>Correlation for the host's response. Filled by the framework; a client must
        /// leave this empty when sending, and a pre-set value is overwritten.</summary>
        public Guid ActionId
        {
            get; set;
        }

        /// <summary>The sender, filled by the framework on the host before the handler runs. Null
        /// on a client and outside dispatch. Never serialized: the sender comes from the
        /// authenticated connection, not the payload.</summary>
        [NonSerialized]
        public ICoopPeer Peer;

        /// <summary>Client: reverse this action's local prediction when the host rejects it. Do
        /// not apply authoritative state here; <see cref="OnMerge"/> does that. The default is a
        /// no-op.</summary>
        public virtual void OnUndo(ActionResponseMessage response)
        {
        }

        /// <summary>Client: apply the authoritative state carried by the response and settle this
        /// action. Called exactly once on every resolution, after <see cref="OnUndo"/> when
        /// rejected. Also the cleanup point for local artifacts. The default is a no-op.</summary>
        public virtual void OnMerge(ActionResponseMessage response)
        {
        }
    }

    /// <summary>
    /// A replicated extended-information value that carries the authoritative state for an
    /// action. The framework applies the value through the extended-information handler, then
    /// calls <see cref="ActionMessage.OnMerge"/> with a null response, so the value is the merge
    /// state. The value itself is otherwise normal extended-information data.
    /// </summary>
    public interface IActionValue
    {
        Guid ActionId
        {
            get;
        }

        bool Rejected
        {
            get;
        }
    }
}
