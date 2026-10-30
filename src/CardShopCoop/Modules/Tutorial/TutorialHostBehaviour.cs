using System;
using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.ExtendedInformation;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine.SceneManagement;
using CoopBehaviour = CardShopCoop.Runtime.CoopBehaviour;

namespace CardShopCoop.Modules.Tutorial
{
    /// <summary>
    /// Host authority for tutorial progress. The published <see cref="TutorialState"/> is the one
    /// truth: a guest's increment is validated, applied through the game's own
    /// <c>AddTaskValue</c>, and answered with the resulting state under the action id. A host-side
    /// change publishes the same value directly, so the join baseline and live changes share one
    /// path.
    /// </summary>
    [ServerBehaviour]
    public sealed class TutorialHostBehaviour : CoopBehaviour
    {
        private const float MaxActionIncrement = 100f;
        private const int MaxStateValues = 4096;

        private static TutorialHostBehaviour _active;

        private bool _shutdown;
        private bool _initialized;

        private void OnEnable()
        {
            if (_shutdown || _initialized)
            {
                return;
            }

            TutorialInterop.Reset();
            _active = this;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Harmony.CreateClassProcessor(typeof(TutorialProgressPatch)).Patch();
            _initialized = true;
        }

        /// <summary>Seeds the session value before the transport starts. The generic
        /// extended-information baseline delivers it to a late joiner; there is deliberately no
        /// module join message or per-peer send.</summary>
        [OnSessionStarted]
        private void SeedState()
        {
            if (_shutdown || !RuntimeContext.InGame())
            {
                // Hosting before the shop data is loaded has no progression to seed; the first
                // in-game change publishes it and the join baseline carries it from then on.
                return;
            }

            var state = BuildState(Guid.Empty, rejected: false);
            if (state == null)
            {
                CoopPlugin.Log.LogWarning(
                    "Tutorial host could not seed the session state; the local progression is not "
                    + "a valid snapshot.");
                return;
            }

            ExtendedInformationModule.SeedGlobal(typeof(TutorialState), state);
        }

        [IntentValidator]
        private IntentValidation Validate(MessageContext context, TutorialActionIntent message)
        {
            if (_shutdown || context?.Connection == null || message == null)
            {
                return IntentValidation.Deny("session is not active");
            }

            if (!context.IsAuthenticated)
            {
                return IntentValidation.Deny("player is not authenticated");
            }

            // Progress is gameplay traffic: it waits for the peer's fully-joined acknowledgement,
            // exactly as the old per-peer gate did.
            if (context.Connection.State != ConnectionState.FullyJoined)
            {
                return IntentValidation.Deny("player is still joining");
            }

            // Mirror vanilla: any defined condition may accumulate progress whenever its action
            // genuinely happens. The game's own TutorialData list records out-of-order progress
            // and the active subgroup consumes it when that task becomes current, so requiring the
            // client's captured action to equal the host's CURRENT task rejected legitimate
            // off-task progress. The host applies the increment and publishes the resulting
            // values, so it stays authoritative; only non-progress is refused.
            if (!Enum.IsDefined(typeof(ETutorialTaskCondition), message.Condition)
                || message.Condition == ETutorialTaskCondition.None)
            {
                return IntentValidation.Deny("unknown tutorial condition");
            }

            if (message.Increment <= 0f || float.IsNaN(message.Increment)
                || float.IsInfinity(message.Increment) || message.Increment > MaxActionIncrement)
            {
                return IntentValidation.Deny("increment is not progress");
            }

            return IntentValidation.Allow();
        }

        /// <summary>Answers a refused action with the pre-intent state under its own id, so the
        /// requester undoes its optimistic run before applying this authority. The generic
        /// rollback that follows then resolves an action that is already retired.</summary>
        [IntentRejected]
        private void Rejected(MessageContext context, TutorialActionIntent message, string reason)
        {
            if (_shutdown || context?.Connection == null || message == null)
            {
                return;
            }

            CoopPlugin.Log.LogDebug("tutorial intent rejected from peer "
                + context.ConnectionId + ": " + reason);
            Publish(message.ActionId, rejected: true);
        }

        /// <summary>Applies the validated increment through the game path, then publishes the
        /// authoritative post-action progression.</summary>
        [MessageHandler]
        private ActionOutcome Handle(MessageContext context, TutorialActionIntent message)
        {
            if (_shutdown || context?.Connection == null || message == null)
            {
                return ActionOutcome.Reject("tutorial session is not active");
            }

            TutorialActions.Add(message.Condition, message.Increment);
            Publish(message.ActionId, rejected: false);
            return ActionOutcome.Applied();
        }

        /// <summary>Publishes a host-originated change. While this behaviour is applying a client's
        /// intent (its handler) the guard suppresses this observer, so the intent's handler owns
        /// the one authoritative publish and the change is never broadcast twice.</summary>
        internal void PublishHostChange()
        {
            if (ReentrancyGuard.Suppressed(this))
            {
                return;
            }

            Publish(Guid.Empty, rejected: false);
        }

        private void Publish(Guid actionId, bool rejected)
        {
            if (_shutdown || CoopCore.Instance?.Net == null)
            {
                return;
            }

            var state = BuildState(actionId, rejected);
            if (state == null)
            {
                CoopPlugin.Log.LogWarning("Tutorial state was not published: the local progression "
                    + "is not a valid snapshot.");
                return;
            }

            // The intro's key hints belong to index 0. A guest can name the shop here on the
            // host, so the host must end its own intro presentation too; the marker is still up
            // at this point and ClearIntroPresentation is a no-op once it is gone.
            if (state.TutorialIndex != 0)
            {
                TutorialInterop.ClearIntroPresentation();
            }

            Global<TutorialState>.Value = state;
        }

        private static TutorialState BuildState(Guid actionId, bool rejected)
        {
            var values = CPlayerData.m_TutorialDataList;
            if (values == null || values.Count > MaxStateValues)
            {
                return null;
            }

            var snapshot = new TutorialValue[values.Count];
            for (var i = 0; i < values.Count; i++)
            {
                var value = values[i];
                if (value == null || !TutorialInterop.IsFiniteNonNegative(value.value))
                {
                    return null;
                }

                snapshot[i] = new TutorialValue
                {
                    Condition = value.tutorialTaskCondition,
                    Value = value.value,
                };
            }

            return new TutorialState
            {
                TutorialIndex = CPlayerData.m_TutorialIndex,
                Values = snapshot,
                ActionId = actionId,
                Rejected = rejected,
            };
        }

        private void OnSceneLoaded(Scene _, LoadSceneMode __) => TutorialInterop.Reset();

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

            TutorialInterop.Reset();
        }

        private void OnDestroy() => Shutdown();

        /// <summary><c>EvaluateTaskVisibility</c> is the one path that advances the tutorial
        /// index (it runs from <c>AddTaskValue</c> and from the shop-naming confirm), so it is the
        /// single point where host progression can change. Publishing from
        /// <c>AddTaskValue</c> alone left the session value on the intro index after the shop was
        /// named, which also gave a late joiner a stale baseline.</summary>
        [HarmonyPatch(typeof(TutorialManager), nameof(TutorialManager.EvaluateTaskVisibility))]
        private static class TutorialProgressPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => _active?.PublishHostChange();
        }
    }
}
