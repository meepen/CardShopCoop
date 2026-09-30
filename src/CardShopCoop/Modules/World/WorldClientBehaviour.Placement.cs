using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.World
{
    /// <summary>Guest-side placement mirror and predictive move sender.</summary>
    public sealed partial class WorldClientBehaviour
    {
        private const string PredictionScope = "placement";
        private readonly PlacementPopulationState _placementPopulation = new();
        private readonly Dictionary<int, PlacementDeltaMessage> _pendingDeltas = new();
        private PlacementBaselineMessage _latestBaseline;
        private InteractableObject _movingObject;
        private PlacementMoveEntry _movingBefore;
        private bool _identityReady;
        private int _applyingState;

        internal bool IdentityReady => _identityReady;

        internal static bool IsPlacementIdentityReady
            => _instance == null || _instance._identityReady;

        internal void InstallPlacement()
        {
            PlacementInterop.ProbeBetaSurface();
            SceneManager.sceneLoaded += OnPlacementSceneLoaded;
            InstallPlacementPatches();
        }

        internal void ShutdownPlacement()
        {
            SceneManager.sceneLoaded -= OnPlacementSceneLoaded;
            _placementPopulation.Reset();
            ClearPendingDeltas();
            _latestBaseline = null;
            _identityReady = false;
            _movingObject = null;
            _movingBefore = null;
        }

        [MessageHandler(typeof(PlacementBaselineMessage))]
        private void HandleBaseline(MessageContext _, PlacementBaselineMessage message)
        {
            _latestBaseline = message;
            ApplyLatestBaseline();
        }

        /// <summary>Sent once this peer is fully connected. The join baseline can arrive while
        /// the guest's scene is still settling (objects finishing their load-time spawn or their
        /// recovery from the transferred save), so ask the host for one fresh pass now that the
        /// scene is up. The host answers this connection only.</summary>
        private void RequestPlacementBaseline(PeerConnection connection)
        {
            if (connection == null)
            {
                return;
            }

            CoopPlugin.Log.LogInfo("[placement] fully connected; requesting a fresh baseline.");
            _context.Send(connection.Id, new PlacementBaselineRequestMessage());
        }

        [MessageHandler(typeof(PlacementDeltaMessage))]
        private void HandleDelta(MessageContext _, PlacementDeltaMessage message)
        {
            if (_shutdown || message == null || message.Entity == null)
            {
                return;
            }

            if (!TryApplyDelta(message))
            {
                CoopPlugin.Log.LogInfo("[placement] defer delta op=" + message.Operation + " key="
                    + message.Entity.Key + ".");
                DeferDelta(message);
            }
            else
            {
                // A later delta for the same entity can now be applied immediately (for example a
                // boxed update arriving after its unboxed add was deferred); the older deferred
                // delta is obsolete and would otherwise be replayed onto the freshly built object.
                DiscardSupersededDelta(message.Entity.Key);
                CoopPlugin.Log.LogInfo("[placement] applied delta op=" + message.Operation + " key="
                    + message.Entity.Key + ".");
            }
        }

        private void DiscardSupersededDelta(int key)
        {
            if (_pendingDeltas.TryGetValue(key, out var previous))
            {
                PredictionApi.Ack(previous.PredictionId);
                _pendingDeltas.Remove(key);
            }
        }

        private void ApplyLatestBaseline()
        {
            if (_latestBaseline == null || PlacementInterop.FindShelfManager() == null)
            {
                return;
            }

            _applyingState++;
            try
            {
                if (!_placementPopulation.Apply(_latestBaseline.Population))
                {
                    return;
                }

                for (var i = 0; i < _latestBaseline.Moves.Count; i++)
                {
                    var move = _latestBaseline.Moves[i];
                    if (PlacementMoveState.Apply(move, false))
                    {
                        continue;
                    }

                    // A move is a binding hint, not identity: this peer's object lists can
                    // legitimately be shorter than the host's (a transferred save or a
                    // cross-build list difference), so a move naming an object this peer does
                    // not have must not tear the session down. A later population refresh or
                    // delta republishes the pose if the object ever exists here.
                    CoopPlugin.Log.LogWarning("[placement] baseline move key="
                        + (move == null ? -1 : move.Key) + " type="
                        + (move == null ? -1 : move.Type) + " did not resolve; skipping.");
                }

                _identityReady = true;
                _latestBaseline = null;
            }
            finally
            {
                _applyingState--;
            }

            ApplyPendingDeltas();
            PlacementInterop.NotifyStructureChanged(-1);
        }

        private void ApplyPendingDeltas()
        {
            if (!_identityReady)
            {
                return;
            }

            var pending = new List<PlacementDeltaMessage>(_pendingDeltas.Values);
            for (var i = 0; i < pending.Count; i++)
            {
                if (TryApplyDelta(pending[i]))
                {
                    _pendingDeltas.Remove(pending[i].Entity.Key);
                }
            }
        }

        /// <summary>Client: the box engine just bound the authoritative placement identity of a
        /// boxed furniture object (its box descriptor arrived, or this peer's own predicted box
        /// adopted it). Retry the deltas that were deferred while the key did not resolve, so the
        /// local object takes the host's authoritative state.</summary>
        internal static void RetryDeferredPlacementDeltas()
            => _instance?.ApplyPendingDeltas();

        private bool TryApplyDelta(PlacementDeltaMessage message)
        {
            if (PlacementInterop.FindShelfManager() == null)
            {
                return false;
            }

            if (message.Operation != PlacementDeltaMessage.Remove && !_identityReady)
            {
                return false;
            }

            if (message.Operation != PlacementDeltaMessage.Remove
                && !CanResolveStableKey(message.Entity))
            {
                return false;
            }

            _applyingState++;
            try
            {
                // The host applies the exact Move entry the guest sent and stamps the prediction
                // id (a stale intent is rejected via a prediction rollback), so this delta confirms
                // the move. Reconciling would snap the piece back to its pre-move pose first.
                PredictionApi.AckOrApply(message.PredictionId, () =>
                {
                    if (!PlacementEntityState.Apply(message))
                    {
                        throw new InvalidOperationException(
                            "placement delta could not be applied for key="
                            + message.Entity.Key.ToString("X"));
                    }
                });
            }
            finally
            {
                _applyingState--;
            }

            PlacementInterop.NotifyStructureChanged(-1);
            return true;
        }

        private void DeferDelta(PlacementDeltaMessage message)
        {
            var key = message.Entity.Key;
            if (_pendingDeltas.TryGetValue(key, out var previous))
                PredictionApi.Ack(previous.PredictionId);
            _pendingDeltas[key] = message;
        }

        /// <summary>True when a delta names an object this peer already holds under the stable key
        /// the host assigned. A key that does not resolve can only be applied after the box channel
        /// binds the exact identity it names; there is no same-type or nearby adoption.</summary>
        private static bool CanResolveStableKey(PlacementMoveEntry entry)
            => entry != null && PlacementMoveState.ResolveObjectByKey(entry.Key) != null;

        /// <summary>Observe the game's own PlaceMovedObject after it ran. Vanilla only clears the
        /// moving flag and performs the place when its own validity check passed; an object still
        /// moving means the aim was invalid and nothing happened. A placement-owned object this
        /// peer started to move registers exactly one post-hoc prediction for the change the game
        /// already made.</summary>
        private void ObserveMoveIntent(InteractableObject obj)
        {
            if (obj == null || obj.GetIsMovingObject())
            {
                return;
            }

            // Our own apply/replay drives PlaceMovedObject through the game path; it must not
            // register a second prediction or release a hold that is already gone.
            if (_applyingState != 0 || PredictionApi.IsReconciling)
            {
                return;
            }

            // The local move ended (placed or otherwise): release the shared hold so observers
            // stop the preview before the authoritative delta follows.
            _placementHold?.LocalEnded(obj);

            if (!_context.InGame() || !ReferenceEquals(_movingObject, obj) || _movingBefore == null)
            {
                // Not a move this peer started through the placement channel (a hold mirror, a
                // remote apply, or a hold we lost). Nothing to predict.
                _movingObject = null;
                _movingBefore = null;
                return;
            }

            // The game already placed the object, so its settled state is the post-action state
            // (unboxed with the final pose); the prediction only records how to redo and undo it.
            var after = PlacementEntityState.Capture(obj, _movingBefore.Key).ToEntry();
            var before = _movingBefore;
            _movingObject = null;
            _movingBefore = null;
            PredictionApi.Predict(
                PredictionScope,
                predictionId => _context.Send(1, new PlacementMoveIntentMessage
                {
                    PredictionId = predictionId,
                    Move = after,
                }),
                () => ApplyMovePrediction(after, true),
                () => ApplyMovePrediction(before, false));
        }

        /// <summary>Runs a recorded move through the game's own placement path: redo on replay of a
        /// surviving prediction, undo on a rollback. Kept out of the prediction closure so both
        /// directions share the single game-path helper.</summary>
        private void ApplyMovePrediction(PlacementMoveEntry entry, bool settle)
        {
            _applyingState++;
            try
            {
                if (!PlacementMoveState.Apply(entry, settle) && settle)
                {
                    throw new InvalidOperationException(
                        "predicted placement move could not be applied");
                }
            }
            finally
            {
                _applyingState--;
            }
        }

        private void OnPlacementSceneLoaded(Scene _, LoadSceneMode __)
        {
            _placementHold?.Reset();
            _placementPopulation.Reset();
            ClearPendingDeltas();
            _latestBaseline = null;
            _identityReady = false;
            _movingObject = null;
            _movingBefore = null;
            PlacementIdentity.Reset();
            _boxNetworkInteraction?.ClientInvalidateSceneSlots();
        }

        private void InstallPlacementPatches()
        {
            Patch(typeof(InteractableObject), "StartMoveObject", nameof(MoveStartPrefix),
                nameof(MoveStartPostfix));
            Patch(typeof(InteractableObject), "PlaceMovedObject", null,
                nameof(MoveIntentPostfix));
            Patch(typeof(InteractableObject), "OnDestroyed", null,
                nameof(InteractableObjectDestroyedPostfix));

            PatchInit(typeof(ShelfManager), "InitInteractableObject", typeof(InteractableObject));
            PatchInit(typeof(ShelfManager), "InitDecoObject", typeof(InteractableObject));
            PatchInit(typeof(ShelfManager), "InitShelf", typeof(Shelf));
            PatchInit(typeof(ShelfManager), "InitWarehouseShelf", typeof(WarehouseShelf));
            PatchInit(typeof(ShelfManager), "InitCardShelf", typeof(CardShelf));
            PatchInit(typeof(ShelfManager), "InitCardItemCombiShelf", typeof(CardItemCombiShelf));
            PatchInit(typeof(ShelfManager), "InitTournamentPrizeShelf", typeof(TournamentPrizeShelf));
            PatchInit(typeof(ShelfManager), "InitPlayTable", typeof(InteractablePlayTable));
            PatchInit(typeof(ShelfManager), "InitAutoCleanser", typeof(InteractableAutoCleanser));
            PatchInit(typeof(ShelfManager), "InitAutoPackOpener", typeof(InteractableAutoPackOpener));
            PatchInit(typeof(ShelfManager), "InitWorkbench", typeof(InteractableWorkbench));
            PatchInit(typeof(ShelfManager), "InitBulkDonationBox", typeof(InteractableBulkDonationBox));
            PatchInit(typeof(ShelfManager), "InitCardStorageShelf", typeof(InteractableCardStorageShelf));
            PatchInit(typeof(ShelfManager), "InitTrashBin", typeof(InteractableTrashBin));
            PatchInit(typeof(ShelfManager), "InitEmptyBoxStorage", typeof(InteractableEmptyBoxStorage));
            PatchInit(typeof(ShelfManager), "InitCashierCounter", typeof(InteractableCashierCounter));
        }

        private void PatchInit(Type target, string method, Type argument)
        {
            var original = AccessTools.Method(target, method, new[] { argument });
            if (original == null)
            {
                CoopPlugin.Log.LogWarning("placement client init patch target missing: "
                    + target.Name + "." + method);
                return;
            }

            _harmony.Patch(original, postfix: new HarmonyMethod(
                typeof(WorldClientBehaviour), nameof(ShelfInitPostfix)));
        }

        private void Patch(Type target, string method, string prefix, string postfix)
        {
            var original = AccessTools.Method(target, method);
            if (original == null)
            {
                CoopPlugin.Log.LogWarning("placement client patch target missing: "
                    + target.Name + "." + method);
                return;
            }

            _harmony.Patch(original,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(WorldClientBehaviour), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(WorldClientBehaviour), postfix));
        }

        /// <summary>Capture-only prefix: snapshot the pre-move pose while the object still owns its
        /// placement identity. The game performs StartMoveObject in full; the postfix observes
        /// whether the move actually started and takes the hold.</summary>
        public static void MoveStartPrefix(InteractableObject __instance,
            out PlacementMoveEntry __state)
        {
            __state = null;
            if (_instance == null || __instance == null || _instance._applyingState != 0)
            {
                return;
            }

            if (PlacementApi.TryMakeObjectKey(PlacementInterop.FindKind(__instance), __instance,
                out var key))
            {
                __state = PlacementEntityState.Capture(__instance, key).ToEntry();
            }
        }

        public static void MoveStartPostfix(InteractableObject __instance,
            PlacementMoveEntry __state)
        {
            if (_instance == null || __instance == null || !__instance.GetIsMovingObject())
            {
                // Vanilla StartMoveObject only enters move mode when the object is pickup-movable;
                // if it did not start, there is no hold to take and nothing to observe.
                return;
            }

            if (__state != null)
            {
                _instance._movingObject = __instance;
                _instance._movingBefore = __state;
            }

            if (_instance._placementHold?.LocalStarted(__instance) == false)
            {
                // Another player owns the hold; LocalStarted unwound the vanilla preview through
                // the game's exit path, so do not track a move this peer does not own.
                _instance._movingObject = null;
                _instance._movingBefore = null;
            }
        }

        public static void MoveIntentPostfix(InteractableObject __instance)
            => _instance?.ObserveMoveIntent(__instance);

        public static void ShelfInitPostfix()
        {
            // A placement apply can recreate an object through the game's package factory, which
            // invokes this postfix from inside the new object's own Awake, before its fields are
            // initialized. Re-entering the baseline/pending application there would pose (and
            // re-box) a half-built object, so let the in-flight apply finish first.
            if (_instance == null || _instance._applyingState != 0)
            {
                return;
            }

            _instance.ApplyLatestBaseline();
            _instance.ApplyPendingDeltas();
        }

        public static void InteractableObjectDestroyedPostfix(InteractableObject __instance)
        {
            if (__instance != null)
            {
                _instance?._placementHold?.LocalEnded(__instance);
                PlacementIdentity.Forget(__instance);
            }
        }

        private void ClearPendingDeltas()
        {
            foreach (var delta in _pendingDeltas.Values)
                PredictionApi.Ack(delta.PredictionId);
            _pendingDeltas.Clear();
        }
    }
}
