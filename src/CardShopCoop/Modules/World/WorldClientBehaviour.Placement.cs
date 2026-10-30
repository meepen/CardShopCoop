using System;
using System.Collections.Generic;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.World
{
    /// <summary>Guest-side placement pose prediction and state apply. Placement creation and
    /// removal are owned by the GameObjectIdentity module; this partial never builds or destroys a
    /// placement entity.</summary>
    public sealed partial class WorldClientBehaviour
    {
        private PlacementBaselineMessage _latestBaseline;
        private InteractableObject _movingObject;
        private PlacementMoveEntry _movingBefore;
        private bool _identityReady;
        // Latest unresolved move prediction per object. A newer move supersedes an older one, so
        // only the latest prediction may merge its response; older resolutions stay silent.
        private readonly Dictionary<InteractableObject, PlacementMoveIntentMessage> _pendingMoves =
            new(PlacementObjectComparer.Instance);

        internal bool IdentityReady => _identityReady;

        internal static bool IsPlacementIdentityReady
            => _instance == null || _instance._identityReady;

        internal void InstallPlacement()
        {
            PlacementInterop.ProbeBetaSurface();
            SceneManager.sceneLoaded += OnPlacementSceneLoaded;
            PlacementApi.StructureChanged += OnPlacementStateStructureChanged;
            InstallPlacementPatches();
        }

        internal void ShutdownPlacement()
        {
            SceneManager.sceneLoaded -= OnPlacementSceneLoaded;
            PlacementApi.StructureChanged -= OnPlacementStateStructureChanged;
            _latestBaseline = null;
            _identityReady = false;
            _movingObject = null;
            _movingBefore = null;
            _pendingMoves.Clear();
        }

        /// <summary>The identity module created or bound a placement entity. A baseline that
        /// arrived before the shelf manager existed is applied now; otherwise this is a no-op.</summary>
        private static void OnPlacementStateStructureChanged(int _)
        {
            var client = _instance;
            if (client == null || ReentrancyGuard.SuppressedForLocalObservation(client))
            {
                return;
            }

            client.ApplyLatestBaseline();
        }

        [MessageHandler]
        private void HandleBaseline(MessageContext _, PlacementBaselineMessage message)
        {
            if (_shutdown || message == null)
            {
                return;
            }

            _latestBaseline = message;
            ApplyLatestBaseline();
        }

        /// <summary>Sent once this peer is fully connected. The join baseline can arrive while
        /// the guest's scene is still settling (objects finishing their load-time spawn or their
        /// recovery from the transferred save), so ask the host for one fresh pass now that the
        /// scene is up. The host answers this peer only.</summary>
        private void RequestPlacementBaseline(PeerConnection connection)
        {
            if (connection == null)
            {
                return;
            }

            CoopPlugin.Log.LogInfo("[placement] fully connected; requesting a fresh state baseline.");
            Context.Send(new PlacementBaselineRequestMessage());
        }

        [MessageHandler]
        private void HandleDelta(MessageContext _, PlacementDeltaMessage message)
        {
            if (_shutdown || message == null || message.Entity == null
                || message.Entity.Entity == null)
            {
                CoopPlugin.Log.LogWarning("[placement] dropped delta with no live entity.");
                return;
            }

            if (HasPendingMove(message.Entity.Entity))
            {
                // This state crossed our newer local move on the wire. The move's own response is
                // the settle point, so an older authoritative pose must not drag it back.
                CoopPlugin.Log.LogInfo("[placement] skipped delta op=" + message.Operation
                    + " entity=" + PlacementInterop.Describe(message.Entity.Entity)
                    + " while a local move is pending.");
                return;
            }

            if (!TryApplyDelta(message))
            {
                CoopPlugin.Log.LogWarning("[placement] dropped delta op=" + message.Operation
                    + " entity=" + PlacementInterop.Describe(message.Entity.Entity) + ".");
                return;
            }

            CoopPlugin.Log.LogInfo("[placement] applied delta op=" + message.Operation
                + " entity=" + PlacementInterop.Describe(message.Entity.Entity) + ".");
        }

        private void ApplyLatestBaseline()
        {
            if (_latestBaseline == null || PlacementInterop.FindShelfManager() == null)
            {
                return;
            }

            var moves = _latestBaseline.Moves;
            var applied = 0;
            var dropped = 0;
            using (Writes.Enter())
            {
                for (var i = 0; moves != null && i < moves.Count; i++)
                {
                    var move = moves[i];
                    // A baseline entry is a state hint for an object this peer already holds. The
                    // entity is a live reference; an entry whose object did not resolve is
                    // dropped rather than retried under a stable id.
                    if (move == null || move.Entity == null)
                    {
                        CoopPlugin.Log.LogWarning("[placement] dropped baseline move with no live "
                            + "entity at index=" + i + ".");
                        dropped++;
                        continue;
                    }

                    if (!PlacementMoveState.Apply(move, false))
                    {
                        CoopPlugin.Log.LogWarning("[placement] dropped baseline move entity="
                            + PlacementInterop.Describe(move.Entity) + " that could not be "
                            + "applied.");
                        dropped++;
                        continue;
                    }

                    applied++;
                }

                _identityReady = true;
                _latestBaseline = null;
            }

            CoopPlugin.Log.LogInfo("[placement] baseline applied=" + applied + " dropped="
                + dropped + ".");
            PlacementInterop.NotifyStructureChanged(-1);
        }

        /// <summary>The identity module removed a placement entity. Release the local hold/move
        /// state this peer kept for it before the game destroys the component.</summary>
        internal static void OnPlacementObjectDestroyed(InteractableObject obj)
            => _instance?.ForgetPlacementObject(obj);

        private void ForgetPlacementObject(InteractableObject obj)
        {
            _placementHold?.LocalEnded(obj);
            if (obj != null)
            {
                _pendingMoves.Remove(obj);
            }

            if (ReferenceEquals(_movingObject, obj))
            {
                _movingObject = null;
                _movingBefore = null;
            }
        }

        private bool HasPendingMove(InteractableObject obj)
            => obj != null && _pendingMoves.ContainsKey(obj);

        private bool IsLatestPendingMove(PlacementMoveIntentMessage action)
            => action?.Move?.Entity != null
                && _pendingMoves.TryGetValue(action.Move.Entity, out var latest)
                && ReferenceEquals(latest, action);

        /// <summary>True while this peer is moving the object again. An older response must not
        /// drag the object out from under the newer, still-unsettled move.</summary>
        private bool IsMovingAgain(PlacementMoveIntentMessage action)
            => action?.Move?.Entity != null && _movingBefore != null
                && ReferenceEquals(_movingObject, action.Move.Entity);

        /// <summary>Client: reverse this move's prediction. A superseded move stays silent because
        /// a newer local move now owns the object.</summary>
        internal void UndoPlacementMove(PlacementMoveIntentMessage action)
        {
            if (_shutdown || action?.Before == null || !IsLatestPendingMove(action)
                || IsMovingAgain(action))
            {
                return;
            }

            ApplyMovePrediction(action.Before, false);
        }

        /// <summary>Client: apply the host's post-action pose. This is the settle point for the
        /// prediction and only the latest move for an object may write it.</summary>
        internal void MergePlacementMove(PlacementMoveIntentMessage action,
            PlacementMoveResponse response)
        {
            if (_shutdown || action == null)
            {
                return;
            }

            var latest = IsLatestPendingMove(action);
            if (latest)
            {
                _pendingMoves.Remove(action.Move.Entity);
            }

            if (!latest || response?.Move == null || IsMovingAgain(action))
            {
                return;
            }

            ApplyMovePrediction(response.Move, false);
        }

        private bool TryApplyDelta(PlacementDeltaMessage message)
        {
            if (message == null || message.Entity == null || message.Entity.Entity == null)
            {
                return false;
            }

            if (PlacementInterop.FindShelfManager() == null || !_identityReady)
            {
                return false;
            }

            using (Writes.Enter())
            {
                if (!PlacementEntityState.Apply(message))
                {
                    throw new InvalidOperationException(
                        "placement delta could not be applied for entity="
                        + PlacementInterop.Describe(message.Entity.Entity));
                }
            }

            PlacementInterop.NotifyStructureChanged(-1);
            return true;
        }

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
            if (ReentrancyGuard.SuppressedForLocalObservation(this))
            {
                return;
            }

            // The local move ended (placed or otherwise): release the shared hold so observers
            // stop the preview before the authoritative delta follows.
            _placementHold?.LocalEnded(obj);

            if (!RuntimeContext.InGame() || !ReferenceEquals(_movingObject, obj) || _movingBefore == null)
            {
                // Not a move this peer started through the placement channel (a hold mirror, a
                // remote apply, or a hold we lost). Nothing to predict.
                _movingObject = null;
                _movingBefore = null;
                return;
            }

            // The game already placed the object, so its settled state is the post-action state
            // (unboxed with the final pose); the prediction records the pre-move pose for undo
            // and leaves the authoritative settle to the host's response.
            var action = new PlacementMoveIntentMessage
            {
                Move = PlacementEntityState.Capture(obj).ToEntry(),
                Before = _movingBefore,
                Origin = this,
            };
            _movingObject = null;
            _movingBefore = null;
            // The framework allocates the action id while sending and records the prediction.
            _pendingMoves[obj] = action;
            Context.Send(action);
        }

        /// <summary>Runs a recorded pose through the game's own placement path. The undo and
        /// authoritative merge both use this one helper.</summary>
        private void ApplyMovePrediction(PlacementMoveEntry entry, bool settle)
        {
            using (Writes.Enter())
            {
                if (!PlacementMoveState.Apply(entry, settle) && settle)
                {
                    throw new InvalidOperationException(
                        "predicted placement move could not be applied");
                }
            }
        }

        private void OnPlacementSceneLoaded(Scene _, LoadSceneMode __)
        {
            // Unity dispatches the invocation list captured before a teardown, so a resolved
            // client can still receive this after Shutdown. Do not touch anything then.
            if (_shutdown)
            {
                return;
            }

            _placementHold?.Reset();
            _latestBaseline = null;
            _identityReady = false;
            _movingObject = null;
            _movingBefore = null;
            _pendingMoves.Clear();
        }

        /// <summary>Prediction/state hooks only. Creation and removal are installed by the
        /// GameObjectIdentity module.</summary>
        private void InstallPlacementPatches()
        {
            PatchPlacementState(typeof(InteractableObject), "StartMoveObject", nameof(MoveStartPrefix),
                nameof(MoveStartPostfix));
            PatchPlacementState(typeof(InteractableObject), "PlaceMovedObject", null,
                nameof(MoveIntentPostfix));
        }

        private void PatchPlacementState(Type target, string method, string prefix, string postfix)
        {
            var original = AccessTools.Method(target, method);
            if (original == null)
            {
                CoopPlugin.Log.LogWarning("placement client state patch target missing: "
                    + target.Name + "." + method);
                return;
            }

            Harmony.Patch(original,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(WorldClientBehaviour), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(WorldClientBehaviour), postfix));
        }

        /// <summary>Capture-only prefix: snapshot the pre-move pose while the object is still a live
        /// placement entity. The game performs StartMoveObject in full; the postfix observes
        /// whether the move actually started and takes the hold.</summary>
        public static void MoveStartPrefix(InteractableObject __instance,
            out PlacementMoveEntry __state)
        {
            __state = null;
            if (_instance == null || __instance == null
                || ReentrancyGuard.SuppressedForLocalObservation(_instance))
            {
                return;
            }

            if (PlacementInterop.FindKind(__instance) >= 0)
            {
                __state = PlacementEntityState.Capture(__instance).ToEntry();
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
    }
}
