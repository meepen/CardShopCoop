using System;
using CardShopCoop.Attributes;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Runtime;
using HarmonyLib;

namespace CardShopCoop.Modules.Shop
{
    [ServerBehaviour]
    public sealed class ShopHostBehaviour : CoopBehaviour
    {
        private static ShopHostBehaviour _active;
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;

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
                if (!ShopState.TryGet(out _))
                {
                    CoopPlugin.Log.LogWarning("Shop host started before a valid canonical shop name was loaded.");
                }

                _context.Messages.RegisterAttributedHandlers(this);
                handlersRegistered = true;
                _active = this;
                _harmony = new Harmony("com.zwhit.cardshopcoop.shop.host");
                _harmony.CreateClassProcessor(typeof(ShopConfirmPatch)).Patch();
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Shop host initialization failed: " + exception);
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
                ClearSessionState();
                _context = null;
                throw;
            }
        }

        [OnFullyJoined]
        private void SendBaseline(PeerConnection connection)
        {
            if (_shutdown || connection == null)
            {
                return;
            }

            if (ShopState.TryGet(out var name))
            {
                _context.Send(connection.Id, new ShopRenameDeltaMessage
                {
                    PredictionId = Guid.Empty,
                    Name = name,
                    Initial = true,
                });
            }
        }

        [MessageHandler(typeof(ShopRenameRequestMessage))]
        private void Handle(MessageContext context, ShopRenameRequestMessage message)
        {
            if (!IsAuthenticatedSender(context) || message == null)
            {
                return;
            }

            if (!ShopState.TryNormalize(message.Name, out var name))
            {
                Reject(context, message.PredictionId);
                return;
            }

            if (!ShopState.Apply(name))
            {
                // CPlayerData is still authoritative if the sign UI is between scenes. The
                // state broadcast remains valid and the next baseline will repaint the UI.
                CoopPlugin.Log.LogDebug("Shop rename accepted while the rename UI was unavailable.");
            }

            if (!ShopInterop.ApplyRemoteInitialNamingSideEffects())
            {
                CoopPlugin.Log.LogDebug("Shop rename accepted without completing the local "
                    + "rename UI/tutorial side effects.");
            }
            BroadcastRename(name, message.PredictionId);
        }

        private bool IsAuthenticatedSender(MessageContext context)
        {
            return !_shutdown && _context.InGame() && context?.Connection != null
                && context.IsAuthenticated;
        }

        private void Reject(MessageContext context, Guid predictionId)
        {
            if (predictionId != Guid.Empty && context?.Connection != null)
            {
                PredictionApi.Rollback(_context, context.Connection.Id, predictionId);
            }
        }

        private void BroadcastRename(string name, Guid predictionId)
        {
            if (_shutdown || !_context.InGame())
            {
                return;
            }

            _context.Broadcast(new ShopRenameDeltaMessage
            {
                PredictionId = predictionId,
                Name = name,
            });
        }

        private void ClearSessionState()
        {
            ShopState.Reset();
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            _context?.Messages.UnregisterAttributedHandlers(this);
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }
            _harmony?.UnpatchSelf();
            _harmony = null;
            ClearSessionState();
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        [HarmonyPatch(typeof(ShopRenamer), "OnPressConfirmShopName")]
        private static class ShopConfirmPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (_active == null || !_active._context.InGame())
                {
                    return;
                }

                if (ShopState.TryNormalize(CPlayerData.GetPlayerName(), out var name))
                {
                    ShopState.Apply(name);
                    _active.BroadcastRename(name, Guid.Empty);
                }
            }
        }
    }
}
