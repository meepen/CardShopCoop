using System;

namespace CardShopCoop.Api
{
    /// <summary>How a host handler resolved one <see cref="ActionMessage"/>.</summary>
    public enum ActionDisposition : byte
    {
        /// <summary>The host applied the action. The client keeps its prediction and merges the
        /// response state.</summary>
        Applied = 1,

        /// <summary>The host did not apply the action. The client reverses its prediction with
        /// <see cref="ActionMessage.OnUndo"/> and then merges the response state.</summary>
        Reject = 2,
    }

    /// <summary>
    /// The mandatory return value of an <see cref="ActionMessage"/> handler. The handler always
    /// applies the command itself on the host, then reports <see cref="Applied"/> or
    /// <see cref="Reject"/>. The response travels back to the acting client, where it merges the
    /// authoritative state; a payload-less outcome still settles the action.
    /// </summary>
    public readonly struct ActionOutcome
    {
        private ActionOutcome(ActionDisposition disposition, ActionResponseMessage response,
            string reason)
        {
            Disposition = disposition;
            Response = response;
            Reason = reason;
        }

        /// <summary>Whether the host applied or rejected the action.</summary>
        public ActionDisposition Disposition
        {
            get;
        }

        /// <summary>Optional actor-only state sent back to the requesting client. Null when the
        /// action needs no custom payload; the framework still settles it.</summary>
        public ActionResponseMessage Response
        {
            get;
        }

        /// <summary>Human-readable rejection reason. Used when no response payload is supplied.</summary>
        public string Reason
        {
            get;
        }

        /// <summary>Creates an applied outcome with an optional actor-only response payload.</summary>
        public static ActionOutcome Applied(ActionResponseMessage response = null)
            => new(ActionDisposition.Applied, response, null);

        /// <summary>Creates a rejected outcome with a reason and an optional actor-only response
        /// payload.</summary>
        public static ActionOutcome Reject(string reason = null,
            ActionResponseMessage response = null)
            => new(ActionDisposition.Reject, response, reason);
    }
}
