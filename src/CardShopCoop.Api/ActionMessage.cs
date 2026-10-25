using System;

namespace CardShopCoop.Api
{
    /// <summary>
    /// A client action. The host applies it through <see cref="OnApply"/>; the framework keeps the
    /// client's local replay/undo and the authoritative response in order. A plain
    /// <see cref="Net.INetMessage"/> is just a message; an <see cref="ActionMessage"/> is a
    /// command. <see cref="OnApply"/> and <see cref="OnUndo"/> are optional overrides.
    /// </summary>
    public abstract class ActionMessage : Net.INetMessage
    {
        /// <summary>Correlation for the host's authoritative response. The framework fills this
        /// in; a module never sets it.</summary>
        public Guid ActionId
        {
            get; set;
        }

        /// <summary>The authenticated player whose action the host is applying, filled by the
        /// framework before <see cref="OnApply"/>. Null on a client and outside dispatch. Never
        /// serialized: the sender comes from the connection, not from the payload.</summary>
        [NonSerialized]
        public PlayerRef Sender;

        /// <summary>The transport view of <see cref="Sender"/> (state, name), filled by the
        /// framework before <see cref="OnApply"/>. Null on a client and outside dispatch.</summary>
        [NonSerialized]
        public Net.Connection.PeerConnection Connection;

        /// <summary>Runs the action on the peer applying it (normally the host).</summary>
        public virtual void OnApply()
        {
        }

        /// <summary>Reverses the client's local action when the host rejects it or when a newer
        /// authoritative state has to be layered around it.</summary>
        public virtual void OnUndo()
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
