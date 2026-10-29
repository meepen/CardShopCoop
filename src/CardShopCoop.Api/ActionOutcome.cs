using System;

namespace CardShopCoop.Api
{
    /// <summary>How a host handler wants one <see cref="ActionMessage"/> resolved.</summary>
    public enum ActionDisposition : byte
    {
        /// <summary>No decision was produced. The framework treats this as a rejection.</summary>
        None = 0,

        /// <summary>The framework should run <see cref="ActionMessage.OnApply"/> on the host,
        /// then send the response.</summary>
        Apply = 1,

        /// <summary>The handler already applied the command on the host; the framework must not
        /// run <see cref="ActionMessage.OnApply"/> again.</summary>
        Applied = 2,

        /// <summary>The host did not apply the command. The client reverses its prediction with
        /// <see cref="ActionMessage.OnUndo"/>.</summary>
        Reject = 3,
    }

    /// <summary>
    /// The mandatory return value of an <see cref="ActionMessage"/> handler. The handler chooses
    /// whether the host applies the command through <see cref="ActionMessage.OnApply"/>, has
    /// already applied it, or rejects it before applying.
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

        public ActionDisposition Disposition
        {
            get;
        }

        /// <summary>Optional payload sent to the requesting client. Null sends only the internal
        /// accept/rollback control, with no custom response message.</summary>
        public ActionResponseMessage Response
        {
            get;
        }

        /// <summary>Human-readable rejection reason. Used when no response payload is supplied.</summary>
        public string Reason
        {
            get;
        }

        public static ActionOutcome Apply(ActionResponseMessage response = null)
            => new(ActionDisposition.Apply, response, null);

        public static ActionOutcome Applied(ActionResponseMessage response = null)
            => new(ActionDisposition.Applied, response, null);

        public static ActionOutcome Reject(string reason = null,
            ActionResponseMessage response = null)
            => new(ActionDisposition.Reject, response, reason);
    }
}
