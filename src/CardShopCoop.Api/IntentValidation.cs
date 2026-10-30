namespace CardShopCoop.Api
{
    /// <summary>
    /// The result of one host intent validator. A denial is a normal outcome: the handler does not
    /// run, the module's rejection hook (if any) runs, and the client reverses a predicted action
    /// with <see cref="ActionMessage.OnUndo"/>.
    /// </summary>
    public readonly struct IntentValidation
    {
        private IntentValidation(bool allowed, string reason)
        {
            Allowed = allowed;
            Reason = reason;
        }

        /// <summary>True when the intent is allowed; false when it is denied.</summary>
        public bool Allowed
        {
            get;
        }

        /// <summary>Why the intent was denied; null when allowed.</summary>
        public string Reason
        {
            get;
        }

        /// <summary>Allows the intent and lets the handler run.</summary>
        public static IntentValidation Allow() => new(true, null);

        /// <summary>Denies the intent with a player-readable reason. A null or empty reason
        /// becomes "rejected by host".</summary>
        public static IntentValidation Deny(string reason)
            => new(false, string.IsNullOrEmpty(reason) ? "rejected by host" : reason);
    }
}
