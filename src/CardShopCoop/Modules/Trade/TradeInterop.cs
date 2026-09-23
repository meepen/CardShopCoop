using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Modules.Npc;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Modules.Trade
{
    /// <summary>Small, build-neutral boundary around the game's customer trade screen. The
    /// members used here exist in both supported game baselines; reflection is used for private
    /// state so this module does not bind to a build-specific implementation detail.</summary>
    internal static class TradeInterop
    {
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public
            | BindingFlags.NonPublic;
        private const float VanillaWaitSeconds = 60f;

        internal static CustomerManager Manager => SceneRef<CustomerManager>.Get();

        internal static CustomerTradeCardScreen Screen => Manager?.m_CustomerTradeCardScreen;

        internal static IList<Customer> Customers => Manager?.GetCustomerList();

        internal static bool IsReachable(Vector3 player, Vector3 target, float radius)
        {
            return (player - target).sqrMagnitude <= radius * radius;
        }

        internal static int CustomerListIndex(Customer customer)
        {
            var customers = Customers;
            return customers == null || customer == null ? -1 : customers.IndexOf(customer);
        }

        internal static ushort CustomerIdentity(Customer customer)
        {
            var index = CustomerListIndex(customer);
            return index < 0 || index > ushort.MaxValue ? ushort.MaxValue : (ushort)index;
        }

        internal static int CustomerGeneration(Customer customer)
        {
            return customer == null ? 0 : NpcHostBehaviour.GetCustomerGeneration(customer);
        }

        internal static bool SameCustomer(Customer customer, ushort index, int generation)
        {
            return customer != null && CustomerIdentity(customer) == index
                && CustomerGeneration(customer) == generation;
        }

        internal static bool IsWaitingForTrade(Customer customer)
        {
            return customer != null && customer.m_CurrentState == ECustomerState.WaitingToTradeCard;
        }

        internal static int CounterIndex(Customer customer)
        {
            var counter = Read(customer, "m_CurrentTradeCardCashierCounter") as InteractableCashierCounter;
            var shelf = SceneRef<ShelfManager>.Get();
            var counters = shelf?.m_CashierCounterList;
            return counter == null || counters == null ? -1 : counters.IndexOf(counter);
        }

        internal static Customer CurrentCustomer(CustomerTradeCardScreen screen)
        {
            return Read(screen, "m_CurrentCustomer") as Customer;
        }

        internal static CustomerTradeData StoredData(Customer customer)
        {
            return Read(customer, "m_CustomerTradeData") as CustomerTradeData;
        }

        internal static void SetStoredData(Customer customer, CustomerTradeData data)
        {
            Field(customer, "m_CustomerTradeData").SetValue(customer, data);
        }

        internal static CustomerTradeData Capture(CustomerTradeCardScreen screen)
        {
            if (screen == null)
            {
                throw new InvalidOperationException("Customer trade screen is unavailable.");
            }

            return new CustomerTradeData
            {
                m_IsTrading = Get<bool>(screen, "m_IsTrading"),
                m_PriceSet = Get<float>(screen, "m_PriceSet"),
                m_LastPriceSet = Get<float>(screen, "m_LastPriceSet"),
                m_SellCardAskPrice = Get<float>(screen, "m_SellCardAskPrice"),
                m_SellCardMarketPrice = Get<float>(screen, "m_SellCardMarketPrice"),
                m_MaxDeclineCount = Get<int>(screen, "m_MaxDeclineCount"),
                m_DeclineCount = Get<int>(screen, "m_DeclineCount"),
                m_CardData_L = CopyCard(Get<CardData>(screen, "m_CardData_L")),
                m_CardData_R = CopyCard(Get<CardData>(screen, "m_CardData_R")),
            };
        }

        internal static CustomerTradeData CopyData(CustomerTradeData data)
        {
            if (data == null)
            {
                return null;
            }

            return new CustomerTradeData
            {
                m_IsTrading = data.m_IsTrading,
                m_PriceSet = data.m_PriceSet,
                m_LastPriceSet = data.m_LastPriceSet,
                m_SellCardAskPrice = data.m_SellCardAskPrice,
                m_SellCardMarketPrice = data.m_SellCardMarketPrice,
                m_MaxDeclineCount = data.m_MaxDeclineCount,
                m_DeclineCount = data.m_DeclineCount,
                m_CardData_L = CopyCard(data.m_CardData_L),
                m_CardData_R = CopyCard(data.m_CardData_R),
            };
        }

        internal static CustomerTradeData DataFromState(TradeOfferState state)
        {
            if (state == null)
            {
                return null;
            }

            return new CustomerTradeData
            {
                m_IsTrading = state.Trading,
                m_PriceSet = state.PriceSet,
                m_LastPriceSet = state.LastPriceSet,
                m_SellCardAskPrice = state.Price,
                m_SellCardMarketPrice = state.MarketPrice,
                m_MaxDeclineCount = state.MaxDeclineCount,
                m_DeclineCount = state.DeclineCount,
                m_CardData_L = CopyCard(state.CardL),
                m_CardData_R = CopyCard(state.CardR),
            };
        }

        internal static bool IsScreenOpen(CustomerTradeCardScreen screen)
        {
            return screen != null && screen.IsScreenOpened();
        }

        internal static bool HasAccepted(CustomerTradeCardScreen screen)
        {
            return Get<bool>(screen, "m_HasAccepted");
        }

        internal static float PriceSet(CustomerTradeCardScreen screen)
        {
            return Get<float>(screen, "m_PriceSet");
        }

        internal static bool IsTrading(CustomerTradeCardScreen screen)
        {
            return Get<bool>(screen, "m_IsTrading");
        }

        internal static int MaxDeclineCount(CustomerTradeCardScreen screen)
        {
            return Get<int>(screen, "m_MaxDeclineCount");
        }

        internal static float WaitRemaining(Customer customer)
        {
            var timer = Convert.ToSingle(Read(customer, "m_Timer"));
            return Mathf.Max(0f, VanillaWaitSeconds - timer);
        }

        internal static void SetPrice(CustomerTradeCardScreen screen, float price)
        {
            Field(screen, "m_PriceSet").SetValue(screen, price);
        }

        internal static void ResetCarrierInteraction(Customer customer)
        {
            Set(customer, "m_IsPausingAction", false);
            (Read(customer, "m_ExclaimationMesh") as GameObject)?.SetActive(true);
            (Read(customer, "m_InteractCollider") as GameObject)?.SetActive(true);
        }

        internal static void SetManagerTrading(bool value)
        {
            var manager = Manager;
            if (manager != null)
            {
                manager.m_IsPlayerTrading = value;
            }
        }

        internal static void OpenData(Customer customer, CustomerTradeData data)
        {
            var screen = Screen ?? throw new InvalidOperationException("Customer trade screen is unavailable.");
            screen.SetCustomer(customer, CopyData(data));
        }

        internal static bool HasCard(CardData card)
        {
            if (card == null)
            {
                return false;
            }

            return card.cardGrade > 0
                ? CPlayerData.HasGradedCardInAlbum(card)
                : CPlayerData.GetCardAmount(card) > 0;
        }

        /// <summary>Finishes a waiting customer without entering the host player's UI mode.
        /// This is the game's 60-second waiting timeout tail, reused for a confirmed guest
        /// decision so the counter and customer are advanced exactly once.</summary>
        internal static void FinishCustomer(Customer customer)
        {
            if (customer == null)
            {
                throw new ArgumentNullException(nameof(customer));
            }

            var counter = Read(customer, "m_CurrentTradeCardCashierCounter")
                as InteractableCashierCounter;
            var wasWaiting = IsWaitingForTrade(customer);
            if (counter == null && !wasWaiting && StoredData(customer) == null)
            {
                return;
            }

            SetStoredData(customer, null);
            Set(customer, "m_Timer", 0f);
            Set(customer, "m_TimerMax", 0f);
            Set(customer, "m_IsPausingAction", false);
            var exclaim = Read(customer, "m_ExclaimationMesh") as GameObject;
            var collider = Read(customer, "m_InteractCollider") as GameObject;
            exclaim?.SetActive(false);
            collider?.SetActive(false);

            if (counter != null)
            {
                Set(customer, "m_HasTradedCard", true);
                counter.CustomerFinishTradingCard();
                Set(customer, "m_CurrentTradeCardCashierCounter", null);
            }

            if (counter != null || wasWaiting)
            {
                Invoke(customer, "DetermineShopAction");
            }
        }

        internal static void RestorePlayerUi()
        {
            var controller = SceneRef<InteractionPlayerController>.Get();
            if (controller != null)
            {
                controller.ExitWorkerInteractMode();
                controller.StopAimLookAt();
                controller.m_WalkerCtrl?.SetStopMovement(false);
                controller.ExitUIMode();
            }

            GameUIScreen.ResetToolTipVisibility();
            GameUIScreen.ResetEnterGoNextDayIndicatorVisible();
            TutorialManager.SetGameUIVisible(true);
        }

        internal static void ShowAccepted(CustomerTradeCardScreen screen)
        {
            Set(screen, "m_HasAccepted", true);
            screen.m_AcceptBtn?.SetActive(false);
            screen.m_CancelBtn?.SetActive(false);
            screen.m_LetMeThinkBtn?.SetActive(false);
            screen.m_DoneBtn?.SetActive(true);
        }

        internal static void CloseScreen(CustomerTradeCardScreen screen)
        {
            if (screen == null || !screen.IsScreenOpened())
            {
                return;
            }

            screen.CloseScreen();
        }

        internal static object Read(object instance, string name)
        {
            return instance == null ? null : Field(instance, name).GetValue(instance);
        }

        private static T Get<T>(object instance, string name)
        {
            return (T)Field(instance, name).GetValue(instance);
        }

        private static void Set(object instance, string name, object value)
        {
            var field = Field(instance, name);
            if (value != null && !field.FieldType.IsInstanceOfType(value))
            {
                throw new InvalidCastException($"Trade field {field.DeclaringType?.Name}.{name} expects "
                    + field.FieldType.FullName + ", received " + value.GetType().FullName + ".");
            }

            field.SetValue(instance, value);
        }

        private static FieldInfo Field(object instance, string name)
        {
            if (instance == null)
            {
                throw new ArgumentNullException(nameof(instance));
            }

            return Field(instance.GetType(), name);
        }

        private static FieldInfo Field(Type type, string name)
        {
            var field = AccessTools.Field(type, name);
            return field ?? throw new MissingFieldException(type.FullName, name);
        }

        private static object Invoke(object instance, string name, params object[] args)
        {
            var method = AccessTools.Method(instance.GetType(), name);
            if (method == null)
            {
                throw new MissingMethodException(instance.GetType().FullName, name);
            }

            return method.Invoke(instance, args);
        }

        private static CardData CopyCard(CardData card)
        {
            if (card == null)
            {
                return null;
            }

            var copy = new CardData();
            copy.CopyData(card);
            return copy;
        }
    }
}
