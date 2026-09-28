using System;
using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Runtime;

namespace CardShopCoop.Modules.Prediction
{
    /// <summary>
    /// The whole prediction layer. The game performs every action through its own code path; a
    /// prediction only records how to redo (<c>apply</c>) and undo (<c>undo</c>) that game change,
    /// both through the game's own methods, so a rejection can be reconciled in layers back to the
    /// target state.
    ///
    /// Host accepted = retire, nothing else (the game already applied it), unless the host message
    /// carries authoritative absolute state the optimistic run did not produce: then
    /// <see cref="Confirm(Guid, Action)"/> retires and re-applies it, or
    /// <see cref="ApplyAuthoritative(Guid, Action)"/> reconciles it in layers so newer in-flight
    /// local edits on the same key survive.
    /// Host rejected = <see cref="Rollback(Guid)"/>, which undoes the rejected action and every
    /// still-pending action queued after it on the same key, then replays the survivors.
    /// </summary>
    public static class PredictionApi
    {
        private sealed class Prediction
        {
            internal Guid Id;
            internal string Key;
            internal Action Apply;
            internal Action Undo;
            internal Action Rejected;
        }

        private static readonly Dictionary<Guid, Prediction> ById = new();
        private static readonly Dictionary<string, List<Prediction>> ByKey = new(StringComparer.Ordinal);
        // Predictions registered by a game-path hook while a reconcile is running. The reconcile
        // iterates snapshots of its key list, so a live append from inside an undo/apply would
        // otherwise race the loop; these are registered in order once the reconcile finishes.
        private static readonly List<Prediction> Deferred = new();
        private static bool _active;
        private static bool _reconciling;
        private static int _applying;

        /// <summary>Raised when a prediction leaves the tracker.</summary>
        public static event Action<Guid> PredictionRetired;

        /// <summary>True while a rejection is being layered (undo/replay), so the hook code that
        /// forwards game changes can skip the changes this reconciliation itself performs.</summary>
        public static bool IsReconciling => _reconciling;

        /// <summary>True while a client prediction session is active (a client is in a session).</summary>
        public static bool IsActive => _active;

        /// <summary>True while a game-path apply/replay is running.</summary>
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

        /// <summary>Records an action the game already performed and sends it. The prediction only
        /// stores how to replay (<paramref name="apply"/>) and reverse (<paramref name="undo"/>) the
        /// change; it never mutates anything now. The game owns every local mutation.</summary>
        public static Guid Predict(string key, Action<Guid> send, Action apply, Action undo)
            => Predict(key, send, apply, undo, null);

        public static Guid Predict(string key, Action<Guid> send, Action apply, Action undo,
            Action rejected)
            => Record(key, send, apply, undo, rejected);

        private static Guid Record(string key, Action<Guid> send, Action apply, Action undo,
            Action rejected)
        {
            if (!_active)
                throw new InvalidOperationException("Client prediction is not active.");
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("A prediction key is required.", nameof(key));
            if (send == null)
                throw new ArgumentNullException(nameof(send));
            if (apply == null)
                throw new ArgumentNullException(nameof(apply));
            if (undo == null)
                throw new ArgumentNullException(nameof(undo));

            var prediction = new Prediction
            {
                Id = Guid.NewGuid(),
                Key = key,
                Apply = apply,
                Undo = undo,
                Rejected = rejected,
            };
            ById.Add(prediction.Id, prediction);
            if (_reconciling)
            {
                // A game-path hook driven by the reconcile's own undo/apply must not append to the
                // live key list the reconcile is iterating. Hold the new prediction and register it
                // in order once the reconcile completes.
                Deferred.Add(prediction);
            }
            else
            {
                GetOrAdd(key).Add(prediction);
            }

            try
            {
                send(prediction.Id);
            }
            catch
            {
                Remove(prediction);
                throw;
            }

            return prediction.Id;
        }

        /// <summary>The host accepted this prediction. The game already performed the action through
        /// its own path, so there is nothing to apply - retire it.</summary>
        public static void Ack(Guid predictionId)
        {
            if (predictionId == Guid.Empty)
            {
                return;
            }

            if (ById.TryGetValue(predictionId, out var prediction))
            {
                Remove(prediction);
            }
        }

        /// <summary>Either this message confirms our own prediction (retire it; the game already did
        /// the change) or it is a remote change (apply it through the game path).</summary>
        public static void AckOrApply(Guid predictionId, Action apply)
        {
            if (apply == null)
                throw new ArgumentNullException(nameof(apply));
            if (predictionId != Guid.Empty && ById.TryGetValue(predictionId, out var prediction))
            {
                Remove(prediction);
                return;
            }

            RunApplying(apply);
        }

        /// <summary>This message resolves our own prediction (retire it, without running anything
        /// from the retire) and then ALWAYS applies the authoritative state through the game path.
        /// Use it when the host's message supersedes the client's optimistic run - the optimistic
        /// apply alone did not produce the host's values (host-computed fields, canonical ids, or a
        /// full authoritative state-set). The apply is idempotent with respect to the optimistic run.
        /// An empty id just applies.</summary>
        public static void Confirm(Guid predictionId, Action apply)
        {
            if (apply == null)
                throw new ArgumentNullException(nameof(apply));
            if (predictionId != Guid.Empty && ById.TryGetValue(predictionId, out var prediction))
                Remove(prediction);

            RunApplying(apply);
        }

        /// <summary>This message carries the host's authoritative absolute state that supersedes our
        /// optimistic run. Unlike <see cref="Confirm(Guid, Action)"/>, which retires the target and
        /// applies over whatever newer local edits are still live, this reconciles in layers: when
        /// the id is a live prediction it undoes the target and every still-pending newer action on
        /// the same key (newest first), retires the target, applies the host state, then replays the
        /// surviving newer actions. That folds a concurrent host change AND preserves an overlapping
        /// in-flight local edit instead of transiently clobbering it. This is an ACCEPT, so no
        /// rejection callback fires; an empty or already-retired id just applies.</summary>
        public static void ApplyAuthoritative(Guid predictionId, Action apply)
        {
            if (apply == null)
                throw new ArgumentNullException(nameof(apply));
            if (predictionId != Guid.Empty && ById.TryGetValue(predictionId, out var prediction))
            {
                Reconcile(prediction, apply);
                return;
            }

            RunApplying(apply);
        }

        private static void RunApplying(Action apply)
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

        /// <summary>True while the local prediction is still awaiting a host decision.</summary>
        public static bool IsPending(Guid predictionId)
            => predictionId != Guid.Empty && ById.ContainsKey(predictionId);

        /// <summary>Host side: rejects one client prediction with the single generic rollback.</summary>
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

        /// <summary>Client side: the host rejected a prediction. Layered revert to the target state:
        /// undo every still-pending action at or after this one (newest first), retire the rejected
        /// action, then replay the later actions on top so the survivors land where they should. A
        /// rollback can cross with the authoritative message that already resolved the prediction,
        /// so an unknown id is ignored rather than treated as a protocol error.</summary>
        internal static void Rollback(Guid predictionId)
        {
            if (predictionId == Guid.Empty)
                return;
            if (!ById.TryGetValue(predictionId, out var rejected))
            {
                CoopPlugin.Log.LogInfo("prediction rollback ignored for already-resolved prediction "
                    + predictionId + ".");
                return;
            }

            CoopPlugin.Log.LogInfo("prediction rollback client key=" + rejected.Key
                + " id=" + predictionId + ".");
            var rejectedCallback = rejected.Rejected;
            Reconcile(rejected, null);
            rejectedCallback?.Invoke();
        }

        private static void Reconcile(Prediction prediction, Action authoritative)
        {
            if (!ByKey.TryGetValue(prediction.Key, out var predictions))
                throw new InvalidOperationException("Prediction key is inconsistent: " + prediction.Id + ".");
            var index = predictions.IndexOf(prediction);
            if (index < 0)
                throw new InvalidOperationException("Prediction key is inconsistent: " + prediction.Id + ".");

            // Snapshot the followers queued after the target before running any undo/apply. A nested
            // Predict() during undo/replay registers a new prediction that was never applied
            // locally; it must not be replayed here as if it were one of the original followers.
            var followers = new List<Prediction>(predictions.Count - index - 1);
            for (var i = index + 1; i < predictions.Count; i++)
                followers.Add(predictions[i]);

            // Snapshot the whole undo set (the target plus every follower) and remove the target
            // from the live key list BEFORE any undo runs. A hook driven by an undo can append to
            // or remove from the same key list; iterating the snapshot keeps that from shifting an
            // index out of range or undoing an entry twice.
            var undo = new List<Prediction>(followers.Count + 1) { prediction };
            undo.AddRange(followers);

            _reconciling = true;
            try
            {
                predictions.RemoveAt(index);
                ById.Remove(prediction.Id);
                PredictionRetired?.Invoke(prediction.Id);

                for (var i = undo.Count - 1; i >= 0; i--)
                    undo[i].Undo();

                authoritative?.Invoke();

                for (var i = 0; i < followers.Count; i++)
                {
                    var follower = followers[i];
                    if (!ById.ContainsKey(follower.Id))
                        continue;

                    try
                    {
                        follower.Apply();
                    }
                    catch (Exception error)
                    {
                        // A follower whose game subject is gone can no longer be replayed. Retire it
                        // with a log so one broken replay cannot abort the batch and tear down the
                        // session on the reliable lane.
                        CoopPlugin.Log.LogWarning("Prediction follower replay failed; retiring id="
                            + follower.Id + " key=" + follower.Key + ": " + error);
                        Remove(follower);
                    }
                }
            }
            finally
            {
                _reconciling = false;
                if (predictions.Count == 0)
                    ByKey.Remove(prediction.Key);
                FlushDeferred();
            }
        }

        /// <summary>Registers every prediction held back while a reconcile was running. Called once
        /// the reconcile (including its finally cleanup) has finished so the key lists are stable
        /// again. A prediction already removed mid-reconcile is dropped.</summary>
        private static void FlushDeferred()
        {
            if (Deferred.Count == 0)
                return;

            for (var i = 0; i < Deferred.Count; i++)
            {
                var prediction = Deferred[i];
                if (ById.ContainsKey(prediction.Id))
                    GetOrAdd(prediction.Key).Add(prediction);
            }

            Deferred.Clear();
        }

        private static List<Prediction> GetOrAdd(string key)
        {
            if (!ByKey.TryGetValue(key, out var predictions))
            {
                predictions = new List<Prediction>();
                ByKey.Add(key, predictions);
            }

            return predictions;
        }

        private static void Remove(Prediction prediction)
        {
            if (ById.Remove(prediction.Id))
                PredictionRetired?.Invoke(prediction.Id);
            // A prediction still held back by a reconcile was never appended to a key list.
            if (Deferred.Remove(prediction))
                return;
            if (!ByKey.TryGetValue(prediction.Key, out var predictions))
                return;
            predictions.Remove(prediction);
            if (predictions.Count == 0)
                ByKey.Remove(prediction.Key);
        }

        private static void Clear()
        {
            ById.Clear();
            ByKey.Clear();
            Deferred.Clear();
            _reconciling = false;
            _applying = 0;
        }
    }
}
