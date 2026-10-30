using System;
using System.Collections.Generic;
using CardShopCoop.Api;

namespace CardShopCoop.Modules.Actions
{
    /// <summary>
    /// Tracks the client-side predictions that are waiting for a host decision. An action is
    /// recorded when it is sent and settled by exactly one host <see cref="ActionResponseMessage"/>.
    /// The ledger knows nothing about entities or keys: a resolution names one action id.
    /// </summary>
    internal static class ActionLedger
    {
        private sealed class PendingRecord
        {
            internal Guid Id;
            internal ActionMessage Message;
        }

        private static readonly Dictionary<Guid, PendingRecord> ById = new();
        private static bool _active;
        private static int _applying;

        /// <summary>True while a client action session is active.</summary>
        internal static bool IsActive => _active;

        /// <summary>True while the ledger is running a prediction reversal or merge. The
        /// reentrancy guard uses this to keep the resulting game changes from being forwarded as
        /// new local actions.</summary>
        internal static bool IsApplying => _applying > 0;

        /// <summary>Raised once when a host decision settles an action, whether it was accepted
        /// or rejected. A send-failure cleanup does not settle anything because the action never
        /// reached the host.</summary>
        internal static event Action<Guid> Settled;

        internal static void Start()
        {
            Clear();
            _active = true;
        }

        internal static void Stop()
        {
            _active = false;
            Clear();
        }

        /// <summary>Records a client prediction and assigns the id the host will answer. A
        /// pre-set id is deliberately overwritten: only the framework allocates action ids.</summary>
        internal static void Record(ActionMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            var id = Guid.NewGuid();
            message.ActionId = id;
            ById.Add(id, new PendingRecord
            {
                Id = id,
                Message = message,
            });
        }

        /// <summary>Removes a prediction whose send failed. The action never reached the host, so
        /// there is nothing to reverse or merge.</summary>
        internal static void Forget(Guid id)
        {
            if (id != Guid.Empty)
            {
                ById.Remove(id);
            }
        }

        internal static bool IsPending(Guid id)
            => id != Guid.Empty && ById.ContainsKey(id);

        /// <summary>Applies an extended-information action value. The module handler applies the
        /// replicated value; the pending action is reversed when the value is rejected, then
        /// merged with a null response. This is the EIM equivalent of a custom response.</summary>
        internal static void ApplyValue(IActionValue value, Action apply)
        {
            if (apply == null)
            {
                throw new ArgumentNullException(nameof(apply));
            }

            if (value == null || value.ActionId == Guid.Empty
                || !ById.TryGetValue(value.ActionId, out var record))
            {
                RunApplying(apply);
                return;
            }

            ById.Remove(value.ActionId);
            try
            {
                using (ApplyScope())
                {
                    try
                    {
                        if (value.Rejected)
                        {
                            record.Message.OnUndo(null);
                        }
                    }
                    finally
                    {
                        try
                        {
                            apply();
                        }
                        finally
                        {
                            record.Message.OnMerge(null);
                        }
                    }
                }
            }
            finally
            {
                Settled?.Invoke(value.ActionId);
            }
        }

        private static void RunApplying(Action apply)
        {
            using (ApplyScope())
            {
                apply();
            }
        }

        /// <summary>Settles the prediction with the host's decision. A rejection reverses the
        /// prediction before the authoritative response state is merged; an acceptance only
        /// merges. <see cref="Settled"/> fires even when a hook throws.</summary>
        internal static void Resolve(Guid id, ActionResponseMessage response)
        {
            if (!ById.TryGetValue(id, out var record))
            {
                return;
            }

            ById.Remove(id);
            try
            {
                using (ApplyScope())
                {
                    try
                    {
                        if (response != null && response.Rejected)
                        {
                            record.Message.OnUndo(response);
                        }
                    }
                    finally
                    {
                        record.Message.OnMerge(response);
                    }
                }
            }
            finally
            {
                Settled?.Invoke(id);
            }
        }

        private static IDisposable ApplyScope()
        {
            _applying++;
            return new ApplyScopeHandle();
        }

        private sealed class ApplyScopeHandle : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _applying--;
            }
        }

        private static void Clear()
        {
            ById.Clear();
        }
    }
}
