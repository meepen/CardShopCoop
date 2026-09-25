using System;
using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Runtime;

namespace CardShopCoop.Modules.Prediction
{
    public static class PredictionApi
    {
        private sealed class Prediction
        {
            internal Guid Id;
            internal string Scope;
            internal Action Apply;
            internal Action Undo;
        }

        private static readonly Dictionary<Guid, Prediction> ById = new();
        private static readonly Dictionary<string, List<Prediction>> ByScope = new(StringComparer.Ordinal);
        private static bool _active;
        private static bool _reconciling;
        private static int _applying;

        /// <summary>Raised when a prediction leaves the tracker without a retry or result queue.</summary>
        public static event Action<Guid> PredictionRetired;

        public static bool IsReconciling => _reconciling;

        /// <summary>True while a client prediction session is active (a client is in a session).</summary>
        public static bool IsActive => _active;

        /// <summary>True while a prediction's optimistic local apply (or its re-apply during a
        /// reconcile) is running. Modules whose game method emits economy events use this to avoid
        /// also mirroring them: the host applies the same action authoritatively and would credit
        /// it twice.</summary>
        public static bool IsApplying => _applying > 0;

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

        /// <summary>Registers and sends a prediction. <paramref name="applyLocally"/> is false
        /// when the game's own method already performed the local mutation (the hook let it run)
        /// and only the replay closure is needed for a later re-apply after an undo.</summary>
        public static Guid Predict(string scope, Action<Guid> send, Action apply, Action undo,
            bool applyLocally = true)
        {
            if (!_active)
                throw new InvalidOperationException("Client prediction is not active.");
            if (string.IsNullOrEmpty(scope))
                throw new ArgumentException("A prediction scope is required.", nameof(scope));
            if (send == null)
                throw new ArgumentNullException(nameof(send));
            if (apply == null)
                throw new ArgumentNullException(nameof(apply));
            if (undo == null)
                throw new ArgumentNullException(nameof(undo));

            var prediction = new Prediction
            {
                Id = Guid.NewGuid(),
                Scope = scope,
                Apply = apply,
                Undo = undo,
            };
            if (!ByScope.TryGetValue(scope, out var predictions))
            {
                predictions = new List<Prediction>();
                ByScope.Add(scope, predictions);
            }
            predictions.Add(prediction);
            ById.Add(prediction.Id, prediction);

            try
            {
                send(prediction.Id);
            }
            catch
            {
                Remove(prediction);
                throw;
            }

            if (applyLocally)
            {
                _applying++;
                try
                {
                    apply();
                }
                finally
                {
                    _applying--;
                }
            }
            return prediction.Id;
        }

        public static void ApplyAuthoritative(Guid predictionId, Action apply)
        {
            if (apply == null)
                throw new ArgumentNullException(nameof(apply));
            if (predictionId == Guid.Empty || !ById.TryGetValue(predictionId, out var prediction))
            {
                apply();
                return;
            }

            Reconcile(prediction, apply);
        }

        /// <summary>Client side: applies a result that <b>confirms</b> the predicted action, retiring
        /// the prediction without undoing or replaying it. The optimistic apply already equals the
        /// accepted state, so running its inverse first (what <see cref="ApplyAuthoritative"/> does)
        /// would visibly roll the action back and re-perform it. Use
        /// <see cref="ApplyAuthoritative"/> when the incoming state can contradict the prediction,
        /// and <see cref="Rollback(Guid)"/> when the host rejected it.</summary>
        public static void ApplyConfirmed(Guid predictionId, Action apply)
        {
            if (apply == null)
                throw new ArgumentNullException(nameof(apply));

            ConfirmSuperseded(predictionId);
            apply();
        }

        public static void ConfirmSuperseded(Guid predictionId)
        {
            if (predictionId == Guid.Empty || !ById.TryGetValue(predictionId, out var prediction))
                return;

            Remove(prediction);
        }

        /// <summary>True while the local prediction is still awaiting a host decision. Modules use
        /// it to confirm their own accepted optimistic state instead of re-applying it.</summary>
        public static bool IsPending(Guid predictionId)
            => predictionId != Guid.Empty && ById.ContainsKey(predictionId);

        /// <summary>Host side: rejects one client prediction. The client undoes the optimistic
        /// action recorded with the matching <see cref="Predict"/> call. This is the single
        /// generic rejection path shared by every predictive feature.</summary>
        public static void Reject(ICoopContext context, int connectionId, Guid predictionId)
        {
            if (predictionId == Guid.Empty)
                return;
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            context.Send(connectionId, new PredictionRollbackMessage { PredictionId = predictionId });
        }

        public static void Rollback(ICoopContext context, int connectionId, Guid predictionId)
        {
            if (predictionId == Guid.Empty)
                return;
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            CoopPlugin.Log.LogInfo("prediction rollback -> conn " + connectionId + " id=" + predictionId);
            Reject(context, connectionId, predictionId);
        }

        /// <summary>Client side: undoes a rejected prediction. A rollback can cross with the
        /// authoritative message that already resolved the prediction, so an unknown id is
        /// ignored rather than treated as a protocol error.</summary>
        internal static void Rollback(Guid predictionId)
        {
            if (predictionId == Guid.Empty)
                return;
            if (!ById.TryGetValue(predictionId, out var prediction))
            {
                CoopPlugin.Log.LogInfo("prediction rollback ignored for already-resolved prediction "
                    + predictionId + ".");
                return;
            }

            Reconcile(prediction, null);
        }

        private static void Reconcile(Prediction prediction, Action authoritative)
        {
            var predictions = ByScope[prediction.Scope];
            var index = predictions.IndexOf(prediction);
            if (index < 0)
                throw new InvalidOperationException("Prediction scope is inconsistent: " + prediction.Id + ".");

            _reconciling = true;
            try
            {
                for (var i = predictions.Count - 1; i >= index; i--)
                    predictions[i].Undo();

                predictions.RemoveAt(index);
                ById.Remove(prediction.Id);
                PredictionRetired?.Invoke(prediction.Id);
                authoritative?.Invoke();

                for (var i = index; i < predictions.Count; i++)
                    predictions[i].Apply();
            }
            finally
            {
                _reconciling = false;
                if (predictions.Count == 0)
                    ByScope.Remove(prediction.Scope);
            }
        }

        private static void Remove(Prediction prediction)
        {
            if (ById.Remove(prediction.Id))
                PredictionRetired?.Invoke(prediction.Id);
            if (!ByScope.TryGetValue(prediction.Scope, out var predictions))
                return;
            predictions.Remove(prediction);
            if (predictions.Count == 0)
                ByScope.Remove(prediction.Scope);
        }

        private static void Clear()
        {
            ById.Clear();
            ByScope.Clear();
            _reconciling = false;
            _applying = 0;
        }
    }
}
