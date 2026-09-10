using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using UnityEngine;

namespace CardShopCoop
{
    public partial class CoopCore
    {
        private void QueueMainThread(string stage, Action action, bool retryable)
        {
            if (action == null)
                throw new ArgumentNullException("action");
            var work = new MainThreadWork(stage, action, retryable);
            _mainThread.Enqueue(() => RunMainThread(work));
        }

        private void RunMainThread(MainThreadWork work)
        {
            if (Time.frameCount < work.NotBeforeFrame)
            {
                _mainThread.Enqueue(() => RunMainThread(work));
                return;
            }
            try
            {
                work.Action();
            }
            catch (Exception e)
            {
                work.Attempts++;
                CoopPlugin.Log.LogError($"main-thread action '{work.Stage}' failed (attempt {work.Attempts}): {e}");
                if (work.Retryable && work.Attempts <= MaxDispatchRetries)
                {
                    work.NotBeforeFrame = Time.frameCount + 1 + work.Attempts;
                    _mainThread.Enqueue(() => RunMainThread(work));
                }
                else
                    CoopPlugin.Log.LogError($"main-thread action '{work.Stage}' abandoned after {work.Attempts} attempt(s)");
            }
        }

        /// <summary>What one queued message costs against DispatchBudget. Everything is 1 unit
        /// except a CardDeltaBatch, which carries up to CardDeltaBatchMax card applies behind a
        /// single message - charging it 1 made the budget bound message COUNT, not work. Read
        /// off the decoded DTO so nothing is deserialized twice; anything malformed falls back
        /// to 1 and the handler's own bogus-count guard drops it.</summary>
        private static int DispatchCost(InMsg m)
        {
            if (m.Type != MsgType.CardDeltaBatch)
                return 1;
            int n = m.Message is CardDeltaBatchMessage batch ? batch.Deltas.Count : 0;
            if (n < 1)
                return 1;
            return n > CardDeltaBatchMax ? CardDeltaBatchMax : n;
        }

        internal static void EnqueueMainThread(Action action)
        {
            if (action == null)
                return;
            var core = Instance;
            if (core == null)
                throw new InvalidOperationException("CoopCore is not running");
            core.QueueMainThread("external-main-thread", action, false);
        }

        /// <summary>Host: relay a customer speech bubble after vanilla has actually
        /// displayed it. Speech is a one-shot cosmetic event, so it uses the reliable lane.</summary>
        public static bool TryGetLocalPlayerPosition(out Vector3 position)
        {
            position = default(Vector3);
            var core = Instance;
            var player = core != null ? core.ResolvePlayer() : null;
            if (player == null)
                return false;
            position = player.position;
            return true;
        }

        // what the local player is carrying (private fields; the game has no public API)
        internal static bool NativeTextInputFocused()
        {
            var sel = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
            if (sel == null)
                return false;
            var tmp = sel.GetComponent<TMPro.TMP_InputField>();
            return tmp != null && tmp.isFocused;
        }

        private static bool IsRetryableDispatch(MsgType type)
        {
            switch (type)
            {
                case MsgType.ShelfRequest:
                case MsgType.CardShelfRequest:
                case MsgType.ObjMoveRequest:
                case MsgType.BoxRequest:
                case MsgType.BoxRemoved:
                case MsgType.ItemPriceContrib:
                case MsgType.LicenseUnlock:
                case MsgType.StaffOp:
                case MsgType.ShopOp:
                case MsgType.SettingsOp:
                case MsgType.ContainerOp:
                case MsgType.GradingOp:
                case MsgType.TradeOp:
                case MsgType.CardBoxOp:
                case MsgType.FurnBoxOp:
                case MsgType.RegisterOp:
                case MsgType.TvOp:
                case MsgType.EconContrib:
                case MsgType.PurchaseRequest:
                case MsgType.SprayHit:
                case MsgType.GradedRemove:
                case MsgType.CardDelta:
                case MsgType.CardDeltaBatch:
                    return true;
                default:
                    return false;
            }
        }

        private void RequestDispatchHeal(MsgType type)
        {
            // These calls only set a module's next-send flag; they do not walk game state,
            // so a failed dispatch cannot blow the frame budget a second time.
            switch (type)
            {
                case MsgType.ShelfRequest:
                    _cardShelves.ForceNextTick();
                    break;
                case MsgType.CardShelfRequest:
                    _cardShelves.ForceNextTick();
                    break;
                case MsgType.ObjMoveRequest:
                    _objMoves.ForceNextTick();
                    break;
                case MsgType.BoxRequest:
                    _boxes.ForceBroadcastNextTick();
                    break;
                case MsgType.RegisterOp:
                    _register.ForceResend();
                    break;
                case MsgType.StaffOp:
                    _staff.ForceResend();
                    break;
                case MsgType.ShopOp:
                    _shopState.ForceResend();
                    break;
                case MsgType.SettingsOp:
                    _settings.ForceResend();
                    break;
                case MsgType.ContainerOp:
                    _containers.ForceResend();
                    break;
                case MsgType.GradingOp:
                    _grading.ForceResend();
                    break;
                case MsgType.TradeOp:
                    _trades.ForceResend();
                    break;
                case MsgType.CardBoxOp:
                    _cardBoxes.ForceResend();
                    break;
                case MsgType.FurnBoxOp:
                    _furnBoxes.ForceResend();
                    break;
                case MsgType.TvOp:
                    _tv.ForceResend();
                    break;
                default:
                    // Economy, purchase, and one-shot card operations are healed by the
                    // normal state cadence; never replay them after bounded failure.
                    _coinHeal = 999f;
                    _progressHeal = 999f;
                    break;
            }
        }

    }
}
