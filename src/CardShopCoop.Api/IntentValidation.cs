namespace CardShopCoop.Api
{
    /// <summary>
    /// The result of one host intent validator. A denial is a normal outcome: the handler does not
    /// run, the module's rejection hook (if any) runs, and an action intent is rolled back.
    /// </summary>
    public readonly struct IntentValidation
    {
        private IntentValidation(bool allowed, string reason)
        {
            Allowed = allowed;
            Reason = reason;
        }

        public bool Allowed
        {
            get;
        }

        /// <summary>Why the intent was denied; null when allowed.</summary>
        public string Reason
        {
            get;
        }

        public static IntentValidation Allow() => new(true, null);

        public static IntentValidation Deny(string reason)
            => new(false, string.IsNullOrEmpty(reason) ? "rejected by host" : reason);
    }
}
