using System;
using CardShopCoop.Net;

namespace CardShopCoop.Api
{
    /// <summary>
    /// A client command. The host runs <see cref="OnApply"/> (or the handler reports that it
    /// already applied the command), then answers with an optional <see cref="ActionResponseMessage"/>.
    /// The client replays <see cref="OnApply"/> during reconciliation and reverses it with
    /// <see cref="OnUndo"/> when the host rejects the command.
    ///
    /// A plain one-way message is an <see cref="INetMessage"/>. An ActionMessage is always
    /// ledgered, so handlers for it must return <see cref="ActionOutcome"/>.
    /// </summary>
    public abstract class ActionMessage : INetMessage
    {
        /// <summary>Correlation for the host's authoritative response. The framework fills this
        /// in; a module never sets it.</summary>
        public Guid ActionId
        {
            get; set;
        }

        /// <summary>The authenticated player whose action the host is applying, filled by the
        /// framework before the host handler runs. Null on a client and outside dispatch. Never
        /// serialized: the sender comes from the connection, not the payload.</summary>
        [NonSerialized]
        public PlayerRef Sender;

        /// <summary>The transport view of <see cref="Sender"/> (state, name), filled by the
        /// framework before the host handler runs. Null on a client and outside dispatch.</summary>
        [NonSerialized]
        public Net.Connection.PeerConnection Connection;

        /// <summary>Runs on the host when the handler returns <see cref="ActionOutcome.Apply"/>,
        /// and on the client when a rejected action's newer neighbours are replayed. The client
        /// performs its initial local prediction before sending; this is not called on send.
        /// The default is a no-op: a handler that applies the command itself returns
        /// <see cref="ActionOutcome.Applied"/> and does not need to override this.</summary>
        public virtual void OnApply()
        {
        }

        /// <summary>Reverses this action's local prediction when the host rejects it. The default
        /// is a no-op; an action that predicts locally must override it.</summary>
        public virtual void OnUndo(ActionResponseMessage response)
        {
        }

        /// <summary>Merges an accepted authoritative response into this action's local state.
        /// Only called for an accepted response; rejections use <see cref="OnUndo"/>.</summary>
        public virtual void OnMerge(ActionResponseMessage response)
        {
        }
    }

    /// <summary>An authoritative extended-information value that answers one
    /// <see cref="ActionMessage"/>. The framework applies it around the module's information
    /// handler in action order; the value itself is otherwise normal data.</summary>
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
