using CardShopCoop.Attributes;
using CardShopCoop.Net;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Runtime;
using HarmonyLib;
using System;

namespace CardShopCoop.Modules.Shop
{
    [ClientBehaviour]
    public sealed class ShopClientBehaviour : CoopBehaviour
    {
        private static ShopClientBehaviour _active;
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
        private string _pendingName;
        private bool _pendingInitial;
        private bool _remoteInitialNamingApplied;
        private const string PredictionScope = "shop.rename";

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
            {
                return;
            }
            _context = RuntimeContext;
            var handlersRegistered = false;
            try
            {
                _context.Messages.RegisterAttributedHandlers(this);
                handlersRegistered = true;
                _active = this;
                _harmony = new Harmony("dev.meepen.cardshopcoop.shop.client");
                _harmony.CreateClassProcessor(typeof(ShopConfirmPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(ShopRenamerReadyPatch)).Patch();
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Shop client initialization failed: " + exception);
                _harmony?.UnpatchSelf();
                _harmony = null;
                if (handlersRegistered)
                {
                    _context.Messages.UnregisterAttributedHandlers(this);
                }
                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }
                _pendingName = null;
                _pendingInitial = false;
                _remoteInitialNamingApplied = false;
                ShopState.Reset();
                _context = null;
                throw;
            }
        }

        [MessageHandler(typeof(ShopRenameDeltaMessage))]
        private void Handle(MessageContext _, ShopRenameDeltaMessage message)
        {
            if (message == null)
            {
                return;
            }

            // Confirm, not AckOrApply: the host normalizes the name (trim/length/control), so the
            // actor must retire its prediction AND apply the host's canonical value. AckOrApply
            // retires the actor's own echo and skips the apply, keeping the raw input.
            PredictionApi.Confirm(message.PredictionId,
                () =>
                {
                    _pendingName = message.Name;
                    _pendingInitial = message.Initial;
                    ApplyPendingNameWhenReady();
                });
        }

        private void ApplyPendingNameWhenReady()
        {
            if (_pendingName == null || !_context.InGame())
            {
                return;
            }

            if (!ShopState.Apply(_pendingName))
            {
                return;
            }

            // The join baseline carries the current canonical name, not a rename. Replaying the
            // naming side effects here would advance this guest's tutorial (and grant the shop
            // naming bonus) even though the host is still in the intro, so a guest of an unnamed
            // host would sit one tutorial step ahead forever. Only a real rename - the host's own
            // rename or this guest's accepted request - replays those host-owned side effects.
            if (!_pendingInitial && !_remoteInitialNamingApplied)
            {
                if (!ShopInterop.ApplyRemoteInitialNamingSideEffects())
                {
                    return;
                }

                _remoteInitialNamingApplied = true;
            }

            _pendingName = null;
            _pendingInitial = false;
        }

        /// <summary>Records the client's rename proposal the game already performed. Vanilla's
        /// confirm runs in every state, so the prediction only supplies the replay/undo closures and
        /// the canonical cache/sign are synced directly.</summary>
        private void PredictRenameObserved()
        {
            var (name, previous) = CaptureRename();
            PredictionApi.Predict(
                PredictionScope,
                RenameSend(name),
                () => ShopState.Apply(name),
                () => ShopState.Apply(previous));

            // Vanilla already performed the rename locally; keep the canonical cache and the
            // sign in step without double-applying through the prediction.
            ShopState.Apply(name);
        }

        private static (string Name, string Previous) CaptureRename()
        {
            var name = CPlayerData.GetPlayerName();
            if (!ShopState.TryGet(out var previous))
            {
                previous = name;
            }

            CoopPlugin.Log.LogDebug("Shop rename proposal sent to host: " + name);
            return (name, previous);
        }

        private Action<Guid> RenameSend(string name)
            => predictionId => _context.Send(1, new ShopRenameRequestMessage
            {
                PredictionId = predictionId,
                Name = name,
            });

        internal void Shutdown()
        {
            if (_shutdown)
                return;
            _shutdown = true;
            _pendingName = null;
            _pendingInitial = false;
            _context?.Messages.UnregisterAttributedHandlers(this);
            if (ReferenceEquals(_active, this))
                _active = null;
            _harmony?.UnpatchSelf();
            _harmony = null;
            _context = null;
            ShopState.Reset();
            _remoteInitialNamingApplied = false;
        }
        private void OnDestroy() => Shutdown();

        [HarmonyPatch(typeof(ShopRenamer), "OnPressConfirmShopName")]
        private static class ShopConfirmPatch
        {
            // Vanilla performs the rename in every state - closing the UI, and during the tutorial
            // advancing this peer's own tutorial and granting the naming bonus. The client always
            // plays the game as vanilla, so the postfix only observes the one change it made; an
            // invalid name is the host's call, expressed as a host rejection, not a client gate.
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (_active == null || !_active._context.InGame())
                {
                    return;
                }

                _active.PredictRenameObserved();
            }
        }

        [HarmonyPatch(typeof(ShopRenamer), "OnEnable")]
        private static class ShopRenamerReadyPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => _active?.ApplyPendingNameWhenReady();
        }
    }
}
