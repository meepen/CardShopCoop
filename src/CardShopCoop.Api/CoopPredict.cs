using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace CardShopCoop.Api
{
    /// <summary>
    /// Client-side prediction, shared with CardShopCoop's built-in features. A predicting client
    /// records optimistic apply/undo closures, sends the intent with the returned id, and
    /// reconciles when the host's authoritative message arrives (or rolls back on rejection).
    ///
    /// A DTO that carries a prediction id may implement <see cref="IPredictedMessage" />.
    /// </summary>
    public interface IPredictedMessage : Net.INetMessage
    {
        Guid PredictionId
        {
            get;
            set;
        }
    }

    public static class CoopPredict
    {
        public static bool IsAvailable => CoopApi.Binding != null && CoopApi.Binding.PredictionActive;
        public static bool IsReconciling => CoopApi.Binding != null && CoopApi.Binding.IsReconciling;
        public static bool IsApplying => CoopApi.Binding != null && CoopApi.Binding.IsApplying;

        /// <summary>Registers and sends a prediction for an action the game already performed. The
        /// local mutation is NOT performed now; only the replay/undo closures are recorded. The scope
        /// is namespaced with the calling assembly name automatically, so different mods can reuse the
        /// same short keys. Throws when no client prediction session is active, mirroring the built-in
        /// modules.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Guid Predict(string scope, Action<Guid> send, Action apply, Action undo)
        {
            var binding = RequireBinding();

            // GetCallingAssembly must run in the method the mod calls: inside a helper it would
            // always report CardShopCoop.Api, namespacing every mod under the same prefix.
            var caller = Assembly.GetCallingAssembly().GetName().Name;
            return binding.Predict(NamespaceScope(scope, caller), send, apply, undo);
        }

        private static string NamespaceScope(string scope, string caller)
        {
            if (string.IsNullOrEmpty(scope))
            {
                throw new ArgumentException("A prediction scope is required.", nameof(scope));
            }

            return string.IsNullOrEmpty(caller) ? scope : caller + ":" + scope;
        }

        private static ICoopBinding RequireBinding()
        {
            var binding = CoopApi.Binding;
            if (binding == null || !binding.PredictionActive)
            {
                throw new InvalidOperationException("Co-op client prediction is not active.");
            }

            return binding;
        }

        /// <summary>The host accepted the prediction (the game already applied it): retire it.</summary>
        public static void Ack(Guid predictionId)
            => CoopApi.Binding?.Ack(predictionId);

        /// <summary>Either this message confirms our own prediction (retire it) or is a remote change
        /// (apply it through the game path).</summary>
        public static void AckOrApply(Guid predictionId, Action apply)
            => CoopApi.Binding?.AckOrApply(predictionId, apply);

        /// <summary>This message resolves our own prediction (retire it without running anything
        /// from the retire) and then ALWAYS applies the authoritative state through the game path.
        /// Use when the host's message supersedes the client's optimistic run.</summary>
        public static void Confirm(Guid predictionId, Action apply)
            => CoopApi.Binding?.Confirm(predictionId, apply);

        public static bool IsPending(Guid predictionId)
            => CoopApi.Binding != null && CoopApi.Binding.IsPending(predictionId);

        /// <summary>Host side: rejects one client prediction, telling that client to undo it.</summary>
        public static void Rollback(ICoopContext context, int connectionId, Guid predictionId)
        {
            if (predictionId == Guid.Empty)
            {
                return;
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            CoopApi.Binding?.Rollback(context, connectionId, predictionId);
        }
    }
}
