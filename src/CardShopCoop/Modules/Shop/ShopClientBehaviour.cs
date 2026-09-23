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
                _harmony = new Harmony("com.zwhit.cardshopcoop.shop.client");
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

            PredictionApi.ApplyAuthoritative(message.PredictionId,
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
            [HarmonyPrefix]
            private static bool Prefix(ShopRenamer __instance)
            {
                if (_active == null || !_active._context.InGame())
                    return true;
                var name = CPlayerData.GetPlayerName();

                if (!ShopState.TryGet(out var previous))
                {
                    previous = name;
                }

                CoopPlugin.Log.LogDebug("Shop rename proposal sent to host: " + name);
                PredictionApi.Predict(
                    PredictionScope,
                    predictionId => _active._context.Send(1, new ShopRenameRequestMessage
                    {
                        PredictionId = predictionId,
                        Name = name,
                    }),
                    () => ShopState.Apply(name),
                    () => ShopState.Apply(previous));

                // The original also advances the tutorial and grants the Unity 6 naming bonus.
                // Those are host-owned side effects, so close only the client UI here.
                ShopInterop.CloseRenameUi(__instance);
                return false;
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
