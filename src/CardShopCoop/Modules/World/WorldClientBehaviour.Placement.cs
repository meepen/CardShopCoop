using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using HarmonyLib;
using UnityEngine;
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
                PredictionApi.ConfirmSuperseded(previous.PredictionId);
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
                    if (!PlacementMoveState.Apply(_latestBaseline.Moves[i], false))
                    {
                        throw new InvalidOperationException(
                            "placement baseline pose could not be applied");
                    }
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
                && PlacementMoveState.ResolveObjectByKey(message.Entity.Key) == null
                && !CanFindCandidate(message.Entity)
                && !CanMaterialize(message.Entity))
            {
                return false;
            }

            _applyingState++;
            try
            {
                // The host applies the exact Move entry the guest sent and stamps the prediction
                // id (a stale intent is rejected via a prediction rollback), so this delta confirms
                // the move. Reconciling would snap the piece back to its pre-move pose first.
                PredictionApi.ApplyConfirmed(message.PredictionId, () =>
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
                PredictionApi.ConfirmSuperseded(previous.PredictionId);
            _pendingDeltas[key] = message;
        }

        /// <summary>True when a delta names a boxed object that can be recreated through the game's
        /// package factory. This is how a peer adopts a host-spawned object it never predicted,
        /// such as a purchased furniture package whose pose only the host chooses.</summary>
        private static bool CanMaterialize(PlacementMoveEntry entry)
            => entry != null && entry.IsBoxed && entry.Type != PlacementInterop.NoType
                && (entry.Key >> 24) != PlacementApi.DecorationKind;

        private static bool CanFindCandidate(PlacementMoveEntry entry)
        {
            var list = PlacementInterop.GetList(PlacementInterop.FindShelfManager(),
                entry.Key >> 24);
            for (var i = 0; list != null && i < list.Count; i++)
            {
                if (list[i] is InteractableObject obj
                    && !PlacementIdentity.IsIdentified(obj)
                    && PlacementInterop.TypeIdOf(obj) == entry.Type)
                {
                    return true;
                }
            }

            return false;
        }

        private bool CaptureMoveIntent(InteractableObject obj)
        {
            if (_applyingState != 0 || PredictionApi.IsReconciling || obj == null
                || !_context.InGame())
            {
                return true;
            }

            var kind = PlacementInterop.FindKind(obj);
            if (kind < 0)
            {
                // Not a placement-owned object (a packaging box being placed, a container, ...).
                // Its own channel owns it, so let vanilla PlaceMovedObject finish. Blocking here
                // left a box the player dropped with the place key stuck in moving mode forever.
                return true;
            }

            if (!PlacementApi.TryMakeObjectKey(kind, obj, out var key))
            {
                CoopPlugin.Log.LogWarning("placement: blocked move without a stable object identity");
                return false;
            }

            var after = PlacementEntityState.Capture(obj, key).ToEntry();
            // PlaceMovedObject always clears m_IsBoxedUp, but this prefix runs before its body, so
            // Capture still reports the object as boxed when it is being unboxed. Record the
            // post-place state or the host keeps it boxed and republishes a boxed delta, which
            // makes the client re-box the furniture instead of placing it.
            after.IsBoxed = false;
            after.BoxedPos = Vector3.zero;
            after.BoxedRot = Quaternion.identity;
            var before = ReferenceEquals(_movingObject, obj) && _movingBefore != null
                ? _movingBefore : after;
            PredictionApi.Predict(
                PredictionScope,
                predictionId => _context.Send(1, new PlacementMoveIntentMessage
                {
                    PredictionId = predictionId,
                    Move = after,
                }),
                () =>
                {
                    _applyingState++;
                    try
                    {
                        if (!PlacementMoveState.Apply(after, true))
                        {
                            throw new InvalidOperationException(
                                "predicted placement move could not be applied");
                        }
                    }
                    finally
                    {
                        _applyingState--;
                    }
                },
                () =>
                {
                    _applyingState++;
                    try
                    {
                        PlacementMoveState.Apply(before, false);
                    }
                    finally
                    {
                        _applyingState--;
                    }
                });
            _movingObject = null;
            _movingBefore = null;
            return false;
        }

        private void CaptureMoveStart(InteractableObject obj)
        {
            if (_applyingState == 0 && obj != null
                && PlacementApi.TryMakeObjectKey(PlacementInterop.FindKind(obj), obj, out var key))
            {
                _movingObject = obj;
                _movingBefore = PlacementEntityState.Capture(obj, key).ToEntry();
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
            Patch(typeof(InteractableObject), "StartMoveObject", nameof(MoveStartPrefix), null);
            Patch(typeof(InteractableObject), "PlaceMovedObject", nameof(MoveIntentPrefix), null);
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

        public static bool MoveStartPrefix(InteractableObject __instance)
        {
            if (_instance == null)
            {
                return true;
            }

            if (_instance._placementHold != null && !_instance._placementHold.IsAllowed(__instance))
            {
                return false;
            }

            _instance.CaptureMoveStart(__instance);
            _instance._placementHold?.LocalStarted(__instance);
            return true;
        }

        public static bool MoveIntentPrefix(InteractableObject __instance)
        {
            if (_instance == null)
            {
                return true;
            }

            // Vanilla only performs the placement (and clears the moving flag) when its own
            // validity check passed. If it cannot place, do not release the shared hold or send a
            // placement: the mover keeps the ghost and observers keep the preview.
            if (__instance != null && !PlacementInterop.ReadMoveValidity(__instance))
            {
                return true;
            }

            _instance._placementHold?.LocalEnded(__instance);
            return _instance.CaptureMoveIntent(__instance);
        }

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
                PredictionApi.ConfirmSuperseded(delta.PredictionId);
            _pendingDeltas.Clear();
        }
    }
}
