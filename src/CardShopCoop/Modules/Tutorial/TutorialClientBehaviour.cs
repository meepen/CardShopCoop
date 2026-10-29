using System;
using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Actions;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine.SceneManagement;
using CoopBehaviour = CardShopCoop.Runtime.CoopBehaviour;

namespace CardShopCoop.Modules.Tutorial
{
    /// <summary>Guest tutorial action forwarding and host-state application.</summary>
    [ClientBehaviour]
    public sealed class TutorialClientBehaviour : CoopBehaviour
    {
        private static TutorialClientBehaviour _active;
        private Harmony _harmony;
        private bool _shutdown;

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
            {
                return;
            }

            var lifecycle = false;
            try
            {
                TutorialInterop.Reset();
                _active = this;
                CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
                SceneManager.sceneLoaded += OnSceneLoaded;
                lifecycle = true;
                _harmony = new Harmony("dev.meepen.cardshopcoop.tutorial.client");
                _harmony.CreateClassProcessor(typeof(CreditPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(TutorialReadyPatch)).Patch();
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Tutorial client initialization failed: " + exception);
                _harmony?.UnpatchSelf();
                _harmony = null;
                if (lifecycle)
                {
                    CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
                    SceneManager.sceneLoaded -= OnSceneLoaded;
                }

                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }

                TutorialInterop.Reset();
                throw;
            }
        }

        /// <summary>The host's authoritative progression changed. The store owns the value, so the
        /// handler only projects it; an action value is applied through the ledger, which retires
        /// this client's action on an accepted echo and undoes it before a rejected re-anchor.</summary>
        [GlobalInformationHandler]
        private void HandleState(TutorialState state)
        {
            if (_shutdown || state == null)
            {
                return;
            }

            TryApplyStored();
        }

        /// <summary>Applies the stored authoritative state once the tutorial surface exists. The
        /// store is the deferred queue: a value that arrives while the scene is not ready stays
        /// there and is applied from the lifecycle hooks, so no per-message copy is retained.</summary>
        private void TryApplyStored()
        {
            if (_shutdown || !RuntimeContext.InGame())
            {
                return;
            }

            var state = Global<TutorialState>.Value;
            if (state == null || !TryApply(state))
            {
                return;
            }

            CoopPlugin.Log.LogDebug("tutorial state applied: index=" + state.TutorialIndex
                + " values=" + (state.Values == null ? 0 : state.Values.Length));
        }

        /// <summary>The game already applied this tutorial change through its own
        /// <c>AddTaskValue</c>; register one post-hoc action whose apply is only used to replay
        /// after an earlier rollback, and whose undo restores the captured state. The host validates
        /// and echoes the action, and a rejection rolls it back. A local decrement (a displayed card
        /// taken back off a shelf) is observed the same way even though the host accepts progress
        /// only: the host rejects it and the rollback restores the pre-decrement state, after which
        /// the host's own authoritative value arrives through the normal state path.</summary>
        private static void ForwardAction(ETutorialTaskCondition condition, float increment,
            TutorialState previous)
        {
            var client = _active;
            if (client == null || client._shutdown
                || !client.RuntimeContext.InGame())
            {
                return;
            }

            var actionId = ActionLedger.Submit(
                "tutorial",
                new TutorialActionIntent { Condition = condition, Increment = increment },
                () => ApplyLocalAction(condition, increment),
                () => TryApply(previous));
            if (actionId == Guid.Empty)
            {
                CoopPlugin.Log.LogError("Tutorial increment was not forwarded to the host: there is "
                    + "no live host connection, so the guest's progress is now local only.");
            }
        }

        private static TutorialState CaptureState()
        {
            var values = CPlayerData.m_TutorialDataList;
            var snapshot = new TutorialValue[values?.Count ?? 0];
            for (var i = 0; i < snapshot.Length; i++)
            {
                snapshot[i] = new TutorialValue
                {
                    Condition = values[i].tutorialTaskCondition,
                    Value = values[i].value,
                };
            }

            return new TutorialState
            {
                TutorialIndex = CPlayerData.m_TutorialIndex,
                Values = snapshot,
            };
        }

        private static void ApplyLocalAction(ETutorialTaskCondition condition, float increment)
        {
            TutorialManager.AddTaskValue(condition, increment);
        }

        /// <summary>Applies an authoritative tutorial state. Returns false while the scene's
        /// tutorial surface is not ready; the caller retains the store value and retries from its
        /// lifecycle hooks instead of failing the reliable message (which would drop the guest).</summary>
        private static bool TryApply(TutorialState state)
        {
            if (state == null)
            {
                return true;
            }

            var manager = TutorialInterop.FindManager();
            if (!TutorialInterop.IsSceneReady(manager) || CPlayerData.m_TutorialDataList == null)
            {
                return false;
            }

            ApplyInner(manager, state);
            return true;
        }

        private static void ApplyInner(TutorialManager manager, TutorialState state)
        {
            var values = state.Values ?? Array.Empty<TutorialValue>();
            var count = values.Length;
            var current = CPlayerData.m_TutorialDataList;
            // The intro is shared progression: whichever player named the shop ends it for every
            // peer. Clear this peer's key hints before SyncMarker hides the intro marker. This
            // must not depend on the local index still being 0 - the shop module's naming replay
            // can have advanced it first - and it is a no-op when the trigger already ended the
            // intro locally.
            if (state.TutorialIndex != 0)
            {
                TutorialInterop.ClearIntroPresentation();
            }

            TutorialInterop.SyncMarker(manager, state.TutorialIndex);

            var incomingValues = new List<TutorialData>(count);
            for (var i = 0; i < count; i++)
            {
                var value = values[i];
                incomingValues.Add(new TutorialData
                {
                    tutorialTaskCondition = value.Condition,
                    value = value.Value,
                });
            }

            current.Clear();
            current.AddRange(incomingValues);
            CPlayerData.m_TutorialIndex = state.TutorialIndex;

            // m_FinishedTutorial is sticky in the game: once set, AddTaskValue becomes a no-op
            // forever. Rebuild it from the authoritative state instead of inheriting whatever the
            // local machine happened to reach (an early guest once finished the intro by mistake).
            TutorialInterop.SetFinished(manager, false);

            for (var groupIndex = 0; groupIndex < manager.m_TutorialSubGroupList.Count; groupIndex++)
            {
                var group = manager.m_TutorialSubGroupList[groupIndex];
                TutorialInterop.FiSubgroupCurrent?.SetValue(group, 0f);
                TutorialInterop.FiSubgroupFinished?.SetValue(group, false);
                group.m_TutorialData.value = 0f;
                for (var valueIndex = 0; valueIndex < incomingValues.Count; valueIndex++)
                {
                    group.AddTaskValue(incomingValues[valueIndex].value,
                        incomingValues[valueIndex].tutorialTaskCondition);
                }

                // The game's completion flag is sticky, and a task's stored value can drop below
                // its max when a displayed card is removed. Rebuilding the flag from the value
                // alone would therefore reopen a task the host has already moved past (the guest
                // would show "9/10" forever while the host rejects every further placement).
                // Honour the authoritative index: every group before it is complete. The group on
                // the index keeps its value-derived flag so a genuinely finished last task still
                // ends the tutorial.
                if (TutorialInterop.FiSubgroupFinished != null
                    && groupIndex < state.TutorialIndex - 1)
                {
                    TutorialInterop.FiSubgroupFinished.SetValue(group, true);
                }
            }

            if (state.TutorialIndex == 0)
            {
                // EvaluateTaskVisibility treats index 0 as "no task open": it closes every group
                // and then marks the whole tutorial finished. Index 0 is the intro, not the end of
                // the tutorial, so keep it alive with every group closed.
                for (var groupIndex = 0; groupIndex < manager.m_TutorialSubGroupList.Count; groupIndex++)
                {
                    manager.m_TutorialSubGroupList[groupIndex].CloseScreen();
                }

                TutorialInterop.SyncMarker(manager, 0);
                return;
            }

            manager.EvaluateTaskVisibility();
        }

        private void OnWorldReady(CEventPlayer_GameDataFinishLoaded _) => TryApplyStored();

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            TutorialInterop.Reset();
            TryApplyStored();
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _harmony?.UnpatchSelf();
            _harmony = null;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

            TutorialInterop.Reset();
        }

        private void OnDestroy() => Shutdown();

        /// <summary>
        /// True for the tutorial conditions the HOST itself produces when it applies the guest's
        /// action intent. The guest still runs vanilla <c>AddTaskValue</c> locally (its own UI
        /// advances immediately), but it must NOT forward a tutorial intent for these: the host's
        /// apply path already calls <c>TutorialManager.AddTaskValue</c> for the same condition, so
        /// forwarding would advance authoritative progress twice. The guest's local value is then
        /// overwritten by the host's authoritative state.
        ///
        /// Host-derived conditions and their call sites:
        /// <list type="bullet">
        /// <item><description><c>ShopLevel</c> - <c>CPlayerData.CPlayer_OnAddShopExp</c>
        /// (CPlayerData.cs:2261), reached when the host applies the guest's XP-bearing purchase
        /// intents (e.g. PurchasingHostBehaviour ApplyRestockSideEffects / ApplyFurnitureSideEffects,
        /// ExpansionHostBehaviour).</description></item>
        /// <item><description><c>CheckoutCustomer</c> -
        /// <c>RegisterHostBehaviour.CreditGuestCheckout</c>, run when the host resolves the guest's
        /// register Complete/CardPayment intent (the game's own credit is gated on
        /// <c>m_IsMannedByPlayer</c>, which is false on a host applying a guest's intent).</description></item>
        /// <item><description><c>RestockItem</c> -
        /// <c>PurchasingHostBehaviour.ApplyRestockSideEffects</c> (PurchasingHostBehaviour.cs:946),
        /// run when the host applies the guest's restock/scanner intent. The guest-side call sites
        /// are RestockItemScreen.EvaluateCartCheckout and ScannerRestockScreen.EvaluateCartCheckout
        /// (decompiled RestockItemScreen.cs:272, ScannerRestockScreen.cs:326).</description></item>
        /// <item><description><c>UnlockBasicCardBox</c> -
        /// <c>CatalogHostBehaviour.ApplyProductEntitlementSideEffects</c>
        /// (CatalogHostBehaviour.cs:228), reached from the guest's product-license purchase
        /// (PurchasingHostBehaviour.cs:497 via CatalogApi.HostApplyProductEntitlementSideEffects)
        /// and from the catalog entitlement path (CatalogHostBehaviour.cs:122).</description></item>
        /// <item><description><c>SellCard</c> and <c>CustomerPlay</c> - the host's own customer
        /// simulation (<c>Customer.ExitShop</c>, decompiled Customer.cs:2798; and
        /// <c>Customer.PlayTableGameEnded</c>, Customer.cs:3205). Customers are host-simulated
        /// (the guest skips Customer.Start), so these are host-derived by construction.</description></item>
        /// </list>
        /// Every other condition is a client-player interaction the host does not re-execute with
        /// the same method (pack opening, shelf/card placement, pricing, boxes, the open/close sign,
        /// the play table, cashier entry), so the guest keeps forwarding those.
        /// </summary>
        private static bool IsHostDerived(ETutorialTaskCondition condition)
            => condition switch
            {
                ETutorialTaskCondition.ShopLevel
                    or ETutorialTaskCondition.CheckoutCustomer
                    or ETutorialTaskCondition.RestockItem
                    or ETutorialTaskCondition.UnlockBasicCardBox
                    or ETutorialTaskCondition.SellCard
                    or ETutorialTaskCondition.CustomerPlay => true,
                _ => false,
            };

        [HarmonyPatch(typeof(TutorialManager), "AddTaskValue")]
        private static class CreditPatch
        {
            // Capture-only: vanilla always applies the change, then the postfix forwards one
            // post-hoc action for the change the game just performed. A local decrement runs
            // vanilla too; the host rejects it against its own progress and the rollback restores
            // the captured state. Host-derived conditions are not forwarded at all - the host's
            // apply path already credits the same condition, so forwarding would double it.
            [HarmonyPrefix]
            private static void Prefix(ETutorialTaskCondition tutorialTaskCondition, float valueAdd,
                out TutorialState __state)
            {
                __state = null;
                if (ReentrancyGuard.Suppressed(_active))
                {
                    return;
                }

                if (!_active.RuntimeContext.InGame())
                {
                    return;
                }

                if (IsHostDerived(tutorialTaskCondition))
                {
                    return;
                }

                __state = CaptureState();
            }

            [HarmonyPostfix]
            private static void Postfix(ETutorialTaskCondition tutorialTaskCondition, float valueAdd,
                TutorialState __state)
            {
                if (__state == null)
                {
                    return;
                }

                ForwardAction(tutorialTaskCondition, valueAdd, __state);
            }
        }

        [HarmonyPatch(typeof(TutorialManager), "Awake")]
        private static class TutorialReadyPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => _active?.TryApplyStored();
        }
    }
}
