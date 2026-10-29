using System;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Actions;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Modules.Expansion
{
    /// <summary>Host authority for expansion purchases and state broadcasts.</summary>
    [ServerBehaviour]
    public sealed class ExpansionHostBehaviour : CoopBehaviour
    {
        private static ExpansionHostBehaviour _active;
        private bool _shutdown;
        private bool _initialized;
        private bool _purchaseInProgress;

        private void OnEnable()
        {
            if (_shutdown || _initialized)
            {
                return;
            }

            var handlersRegistered = false;
            try
            {
                RuntimeContext.Messages.RegisterAttributedHandlers(this);
                handlersRegistered = true;
                _active = this;
                Harmony.CreateClassProcessor(typeof(ExpansionChangedPatch)).Patch();
                Harmony.CreateClassProcessor(typeof(ShopRoomChangedPatch)).Patch();
                Harmony.CreateClassProcessor(typeof(WarehouseRoomChangedPatch)).Patch();
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Expansion host initialization failed: " + exception);
                if (handlersRegistered)
                {
                    RuntimeContext.Messages.UnregisterAttributedHandlers(this);
                }
                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }
                throw;
            }
            _initialized = true;
        }

        [OnFullyJoined]
        private void SendBaseline(PeerConnection connection)
        {
            if (_shutdown || connection == null || !IsJoinPhase(connection.State))
            {
                return;
            }

            // Join catch-up is deliberately one-shot. The normal event hooks below publish
            // later authoritative changes; a failed send is not turned into a retry queue.
            RuntimeContext.Send(connection.Id, BuildBaseline());
        }

        [MessageHandler]
        private void HandlePurchase(MessageContext context, ExpansionPurchaseMessage message)
        {
            // Every joined client, including the first connection (which the host numbers 1),
            // is allowed to ask the host to buy an expansion. The host is the only writer.
            if (_shutdown || message == null || context?.Connection == null
                || !RuntimeContext.InGame() || !IsJoinPhase(context.Connection.State))
            {
                return;
            }

            if (message.Kind > 2)
            {
                CoopPlugin.Log.LogWarning("Expansion purchase rejected peer="
                    + context.Connection.Id + " kind=" + message.Kind
                    + " reason=unsupported purchase kind");
                ActionLedger.Rollback(RuntimeContext, context.Connection.Id, message.ActionId);
                return;
            }

            var accepted = false;
            try
            {
                accepted = HostPurchase(message.Kind, context.Connection.Id, out _);
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Expansion purchase failed for peer "
                    + context.Connection.Id + ": " + exception);
                ActionLedger.Rollback(RuntimeContext, context.Connection.Id, message.ActionId);
                return;
            }

            if (!accepted)
            {
                ActionLedger.Rollback(RuntimeContext, context.Connection.Id, message.ActionId);
                return;
            }

            BroadcastDelta(message.Kind, message.ActionId);
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            RuntimeContext.Messages.UnregisterAttributedHandlers(this);
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }
        }

        private void OnDestroy() => Shutdown();

        private bool HostPurchase(byte kind, int peerId, out string rejection)
        {
            rejection = null;
            var unlock = ExpansionInterop.FindUnlockManager();
            var gameManager = SceneRef<CGameManager>.Get();
            if (unlock == null)
            {
                rejection = "unlock manager is unavailable";
                LogRejectedPurchase(peerId, kind, rejection);
                return false;
            }

            if (gameManager?.m_IsPrologue == true)
            {
                rejection = "purchases are disabled during the prologue";
                LogRejectedPurchase(peerId, kind, rejection);
                return false;
            }

            if (!ExpansionInterop.TryValidatePurchase(unlock, kind, out var index, out var cost,
                out rejection))
            {
                LogRejectedPurchase(peerId, kind, rejection);
                return false;
            }

            if (!ExpansionInterop.TryReserveExpansion(cost, out var spend))
            {
                rejection = "host wallet reservation failed for " + cost;
                LogRejectedPurchase(peerId, kind, rejection);
                return false;
            }

            var snapshot = ExpansionInterop.CaptureState();
            _purchaseInProgress = true;
            try
            {
                if (!ExpansionInterop.CommitExpansion(spend))
                {
                    ExpansionInterop.ReleaseExpansion(spend);
                    rejection = "expansion debit could not be admitted";
                    LogRejectedPurchase(peerId, kind, rejection);
                    return false;
                }

                try
                {
                    // These are the game's own authoritative unlock entry points. The client
                    // never invokes them for a purchase intent.
                    if (kind == 2)
                    {
                        unlock.SetUnlockWarehouseRoom(true);
                        if (!CPlayerData.m_IsWarehouseRoomUnlocked)
                        {
                            throw new InvalidOperationException("warehouse room unlock did not apply");
                        }
                    }
                    else if (kind == 1)
                    {
                        unlock.StartUnlockNextWarehouseRoom();
                        if (CPlayerData.m_UnlockWarehouseRoomCount != snapshot.WarehouseRooms + 1)
                        {
                            throw new InvalidOperationException(
                                "warehouse expansion unlock did not apply");
                        }
                    }
                    else
                    {
                        unlock.StartUnlockNextRoom();
                        if (CPlayerData.m_UnlockRoomCount != snapshot.Rooms + 1)
                        {
                            throw new InvalidOperationException("shop expansion unlock did not apply");
                        }
                    }
                }
                catch (Exception exception)
                {
                    ExpansionInterop.CancelExpansion(spend);
                    ExpansionInterop.RollbackState(unlock, snapshot);
                    rejection = "unlock failed after debit admission: " + exception.Message;
                    LogRejectedPurchase(peerId, kind, rejection);
                    return false;
                }

                if (kind == 2)
                {
                    PriceChangeManager.AddTransaction(-cost, ETransactionType.ShopExpansion, 1, -1);
                    AchievementManager.OnShopLotBUnlocked();
                }
                else if (kind == 1)
                {
                    PriceChangeManager.AddTransaction(-cost, ETransactionType.ShopExpansion, 0, index);
                }
                else
                {
                    PriceChangeManager.AddTransaction(-cost, ETransactionType.ShopExpansion, 1, index);
                }

                CEventManager.QueueEvent(new CEventPlayer_AddShopExp(
                    Mathf.Clamp(Mathf.RoundToInt(cost / 100f), 5, 100)));
                CPlayerData.m_GameReportDataCollect.upgradeCost -= cost;
                CPlayerData.m_GameReportDataCollectPermanent.upgradeCost -= cost;
                SoundManager.PlayAudio("SFX_CustomerBuy", 0.6f);
                ExpansionInterop.SaveShelfData();
                return true;
            }
            finally
            {
                _purchaseInProgress = false;
            }
        }

        private static void LogRejectedPurchase(int peerId, byte kind, string reason)
        {
            CoopPlugin.Log.LogWarning("Expansion purchase rejected peer=" + peerId + " kind=" + kind
                + " reason=" + reason + " prologue="
                + (SceneRef<CGameManager>.Get()?.m_IsPrologue ?? false)
                + " shopLevel=" + CPlayerData.m_ShopLevel + " shopRooms="
                + CPlayerData.m_UnlockRoomCount + " warehouseUnlocked="
                + CPlayerData.m_IsWarehouseRoomUnlocked + " warehouseRooms="
                + CPlayerData.m_UnlockWarehouseRoomCount + " coins="
                + CPlayerData.m_CoinAmountDouble);
        }

        private void BroadcastDelta(byte kind, Guid actionId)
        {
            if (_shutdown || !RuntimeContext.InGame())
            {
                return;
            }

            var message = new ExpansionDeltaMessage
            {
                ActionId = actionId,
                Area = kind,
            };
            switch (kind)
            {
                case 0:
                    message.Count = CPlayerData.m_UnlockRoomCount;
                    break;
                case 1:
                    message.Count = CPlayerData.m_UnlockWarehouseRoomCount;
                    break;
                case 2:
                    message.Unlocked = CPlayerData.m_IsWarehouseRoomUnlocked;
                    break;
                default:
                    throw new InvalidOperationException("Unknown expansion delta area " + kind + ".");
            }

            RuntimeContext.Broadcast(message);
        }

        private static ExpansionBaselineMessage BuildBaseline()
        {
            return new ExpansionBaselineMessage
            {
                UnlockRoomCount = CPlayerData.m_UnlockRoomCount,
                UnlockWarehouseRoomCount = CPlayerData.m_UnlockWarehouseRoomCount,
                IsWarehouseRoomUnlocked = CPlayerData.m_IsWarehouseRoomUnlocked,
            };
        }

        private static bool IsJoinPhase(ConnectionState state)
            => state == ConnectionState.Transferring || state == ConnectionState.FullyJoined;

        [HarmonyPatch(typeof(UnlockRoomManager), "SetUnlockWarehouseRoom")]
        private static class ExpansionChangedPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (_active != null && !_active._shutdown && !_active._purchaseInProgress)
                {
                    _active.BroadcastDelta(2, Guid.Empty);
                }
            }
        }

        [HarmonyPatch(typeof(UnlockRoomManager), "StartUnlockNextRoom")]
        private static class ShopRoomChangedPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (_active != null && !_active._shutdown && !_active._purchaseInProgress)
                {
                    _active.BroadcastDelta(0, Guid.Empty);
                }
            }
        }

        [HarmonyPatch(typeof(UnlockRoomManager), "StartUnlockNextWarehouseRoom")]
        private static class WarehouseRoomChangedPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (_active != null && !_active._shutdown && !_active._purchaseInProgress)
                {
                    _active.BroadcastDelta(1, Guid.Empty);
                }
            }
        }
    }
}
