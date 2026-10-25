using System;
using CardShopCoop.Runtime;

namespace CardShopCoop.Modules.Actions
{
    /// <summary>
    /// Generic host-side admission for client actions that compete for a shared,
    /// capacity-limited resource (shelf slots, binder slots, stock, workbench slots, ...).
    ///
    /// The host applies intents synchronously in arrival order, so the first accepted intent
    /// wins. A later intent that no longer fits the authoritative state is rejected with the one
    /// generic <see cref="ActionLedger.Reject"/> rollback, and the requesting client undoes the
    /// optimistic action it recorded with <see cref="ActionLedger.Submit"/>.
    ///
    /// A feature opts into the pattern by:
    ///   1. client: register the optimistic apply and its inverse around the local action;
    ///   2. host:   call <see cref="Admit"/> (or <see cref="Resolve"/>) with the authoritative
    ///              usage and capacity, or <see cref="Reject"/> when its own validation fails;
    ///   3. client: <see cref="ActionLedger.AckOrApply"/> the accepted host delta, or
    ///              <see cref="ActionLedger.Ack"/> when the optimistic state is
    ///              already exactly the accepted result.
    /// </summary>
    internal static class ActionHost
    {
        /// <summary>First-come-first-served admission for a bounded resource. Returns false and
        /// rolls the client's action back when the request does not fit.</summary>
        public static bool Admit(CoopRuntimeContext context, int connectionId, Guid actionId,
            int used, int capacity, int requested)
        {
            if (requested > 0 && used >= 0 && used + requested <= capacity)
            {
                return true;
            }

            Reject(context, connectionId, actionId);
            return false;
        }

        /// <summary>Runs an intent's host apply and rolls the client's action back when the
        /// handler reports it could not be applied.</summary>
        public static bool Resolve(CoopRuntimeContext context, int connectionId, Guid actionId,
            Func<bool> apply)
        {
            if (apply == null || !apply())
            {
                // Unlike ActionLedger.Rollback, this path had no diagnostic, so a silently
                // refused display/placement intent looked like the guest never sent it.
                CoopPlugin.Log.LogWarning("action rejected -> conn " + connectionId
                    + " id=" + actionId + " (host apply refused).");
                Reject(context, connectionId, actionId);
                return false;
            }

            return true;
        }

        public static void Reject(CoopRuntimeContext context, int connectionId, Guid actionId)
            => ActionLedger.Reject(context, connectionId, actionId);
    }
}
