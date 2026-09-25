using System;

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

        /// <summary>Registers and sends a prediction. Throws when no client prediction session is
        /// active, mirroring the built-in modules.</summary>
        public static Guid Predict(string scope, Action<Guid> send, Action apply, Action undo,
            bool applyLocally = true)
        {
            var binding = CoopApi.Binding;
            if (binding == null || !binding.PredictionActive)
            {
                throw new InvalidOperationException("Co-op client prediction is not active.");
            }

            return binding.Predict(scope, send, apply, undo, applyLocally);
        }

        public static void ApplyAuthoritative(Guid predictionId, Action apply)
            => CoopApi.Binding?.ApplyAuthoritative(predictionId, apply);

        public static void ApplyConfirmed(Guid predictionId, Action apply)
            => CoopApi.Binding?.ApplyConfirmed(predictionId, apply);

        public static void ConfirmSuperseded(Guid predictionId)
            => CoopApi.Binding?.ConfirmSuperseded(predictionId);

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
