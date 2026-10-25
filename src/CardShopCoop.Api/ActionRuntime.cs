using System;

namespace CardShopCoop.Api
{
    /// <summary>
    /// The advanced action surface. Normal integrations only send an <see cref="ActionMessage"/>
    /// through <see cref="ICoopContext"/> and let the framework handle it; this class is for a
    /// module whose authoritative response is an ordinary message instead of an extended-
    /// information value.
    /// </summary>
    internal static class ActionRuntime
    {
        public static bool IsAvailable => CoopApi.Binding != null && CoopApi.Binding.ActionActive;

        public static bool IsReconciling => CoopApi.Binding != null && CoopApi.Binding.IsReconciling;

        public static bool IsApplying => CoopApi.Binding != null && CoopApi.Binding.IsApplying;

        /// <summary>Sends an action message to the host and records the local replay/reverse. The
        /// apply/undo default to the message's overrides. The message's <see cref="ActionMessage.ActionId"/>
        /// is filled in by the runtime.</summary>
        public static Guid Submit(ActionMessage message, Action apply = null, Action undo = null)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            var binding = RequireBinding();
            var key = message.GetType().FullName + ", " + message.GetType().Assembly.GetName().Name;
            return binding.Submit(key, message, apply ?? message.OnApply, undo ?? message.OnUndo);
        }

        /// <summary>The host or client accepted this action: retire it without touching local state.</summary>
        public static void Ack(Guid actionId)
            => CoopApi.Binding?.Ack(actionId);

        /// <summary>Either this response resolves our own action (retire it) or it is a remote
        /// change (apply it through the game path).</summary>
        public static void AckOrApply(Guid actionId, Action apply)
            => CoopApi.Binding?.AckOrApply(actionId, apply);

        /// <summary>This response resolves our own action and always applies the authoritative
        /// state over the optimistic run.</summary>
        public static void Confirm(Guid actionId, Action apply)
            => CoopApi.Binding?.Confirm(actionId, apply);

        /// <summary>This response carries authoritative absolute state that may overlap newer
        /// local actions on the same key: reconcile in layers instead of dropping them.</summary>
        public static void ApplyAuthoritative(Guid actionId, Action apply)
            => CoopApi.Binding?.ApplyAuthoritative(actionId, apply);

        public static bool IsPending(Guid actionId)
            => CoopApi.Binding != null && CoopApi.Binding.IsPending(actionId);

        /// <summary>Host side: rejects one client action, telling that client to reverse it.</summary>
        public static void Rollback(ICoopContext context, int connectionId, Guid actionId)
        {
            if (actionId == Guid.Empty)
            {
                return;
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            CoopApi.Binding?.Rollback(context, connectionId, actionId);
        }

        private static ICoopBinding RequireBinding()
        {
            var binding = CoopApi.Binding;
            if (binding == null || !binding.ActionActive)
            {
                throw new InvalidOperationException("Co-op action runtime is not active.");
            }

            return binding;
        }
    }
}
