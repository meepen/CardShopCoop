using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Runtime;
using CardShopCoop.Util;
using UnityEngine;

namespace CardShopCoop.Modules.Register
{
    /// <summary>
    /// The register's small game-facing surface. All optional/private state is resolved here so
    /// the feature never mentions a type that exists in only one supported game build.
    /// </summary>
    internal static class RegisterInterop
    {
        private static MethodInfo _getOrCreateCardRigidbody;
        private static bool _cardRigidbodyMethodResolved;
        private static MethodInfo _getOrCreateItemRigidbody;
        private static bool _itemRigidbodyMethodResolved;

        internal static IList<InteractableCashierCounter> Counters
            => SceneRef<ShelfManager>.Get()?.m_CashierCounterList;

        internal static FieldInfo Field(Type type, string name)
            => ReflectionSurface.OptionalField(type, name);

        internal static object Read(object target, string name)
        {
            if (target == null)
            {
                return null;
            }

            return Field(target.GetType(), name)?.GetValue(target);
        }

        internal static void Write(object target, string name, object value)
        {
            if (target == null)
            {
                return;
            }

            var field = Field(target.GetType(), name);
            field?.SetValue(target, value);
        }

        internal static int Index(InteractableCashierCounter counter)
        {
            var counters = Counters;
            return counters == null || counter == null ? -1 : counters.IndexOf(counter);
        }

        internal static InteractableCashierCounter Counter(int index)
        {
            var counters = Counters;
            return counters == null || index < 0 || index >= counters.Count ? null : counters[index];
        }

        internal static UI_CashCounterScreen CashScreen(InteractableCashierCounter counter)
            => Read(counter, "m_UICashCounterScreen") as UI_CashCounterScreen;

        internal static UI_CreditCardScreen CreditScreen(InteractableCashierCounter counter)
            => Read(counter, "m_UICreditCardScreen") as UI_CreditCardScreen;

        internal static bool IsUsingCard(InteractableCashierCounter counter)
            => Read(counter, "m_IsUsingCard") is bool value && value;

        internal static bool IsChangeReady(InteractableCashierCounter counter)
            => Read(counter, "m_IsChangeReady") is bool value && value;

        internal static double Total(InteractableCashierCounter counter)
            => Convert.ToDouble(Read(counter, "m_TotalScannedItemCost") ?? 0d);

        internal static double Paid(InteractableCashierCounter counter)
            => Convert.ToDouble(Read(counter, "m_CustomerPaidAmount") ?? 0d);

        internal static double Change(InteractableCashierCounter counter)
            => Convert.ToDouble(Read(counter, "m_CurrentMoneyChangeValue") ?? 0d);

        internal static bool ChangeStarted(InteractableCashierCounter counter)
            => Read(counter, "m_IsStartGivingChange") is bool value && value;

        internal static bool TooMuchChange(InteractableCashierCounter counter)
            => Read(counter, "m_TooMuchChangeGiven") is bool value && value;

        internal static int GivenAmount(InteractableCounterMoneyChange change)
            => Convert.ToInt32(Read(change, "m_GivenAmount") ?? 0);

        internal static void SetGivenAmount(InteractableCounterMoneyChange change, int amount)
        {
            if (change == null)
            {
                return;
            }

            amount = Math.Max(0, amount);
            var current = GivenAmount(change);
            if (current == amount)
            {
                // The optimistic prediction (or an earlier delta) already produced this absolute
                // count. ResetAmountGiven only clears m_GivenAmount and the stack visuals - it does
                // NOT undo OnGiveChange, so replaying OnMouseButtonUp here called OnGiveChange a
                // second time and double-counted the counter's change value and coin/bill counters.
                return;
            }

            // Apply the difference with the game's own methods so m_GivenAmount, the stack
            // visuals, OnGiveChange and the coin/bill added counters all move together and the
            // net result equals the authoritative absolute count.
            if (current < amount)
            {
                for (var i = current; i < amount; i++)
                {
                    change.OnMouseButtonUp();
                }
            }
            else
            {
                for (var i = amount; i < current; i++)
                {
                    change.OnRightMouseButtonUp();
                }
            }

            // The loop only materializes the game's stack visuals when the counter is currently
            // accepting change. The field write makes the resulting denomination count exact
            // even when this absolute delta arrives after the phase has already advanced.
            Write(change, "m_GivenAmount", amount);
        }

        internal static int CustomerIndex(Customer customer)
        {
            var manager = SceneRef<CustomerManager>.Get();
            var customers = manager?.GetCustomerList();
            return customers == null || customer == null ? -1 : customers.IndexOf(customer);
        }

        internal static bool IsManned(InteractableCashierCounter counter)
            => counter != null && counter.IsMannedByPlayer();

        internal static bool IsFinite(double value)
            => !double.IsNaN(value) && !double.IsInfinity(value);

        internal static InteractableCounterMoneyChange FindChange(
            InteractableCashierCounter counter, int slot, bool isCoin, double value)
        {
            var list = counter?.m_InteractableCounterMoneyChangeList;
            if (list == null || slot < 0)
            {
                return null;
            }

            for (var i = 0; i < list.Count; i++)
            {
                var candidate = list[i];
                if (candidate != null && candidate.m_Index == slot && candidate.m_IsCoin == isCoin
                    && Math.Abs(candidate.m_ValueDouble - value) <= 0.0001d)
                {
                    return candidate;
                }
            }

            return null;
        }

        internal static InteractableCashierCounter FindCounterForScanItem(InteractableScanItem item)
        {
            var counters = Counters;
            if (item == null || counters == null)
            {
                return null;
            }

            for (var i = 0; i < counters.Count; i++)
            {
                var customer = counters[i]?.m_CurrentCustomer;
                var items = customer?.GetItemInBagList();
                for (var j = 0; items != null && j < items.Count; j++)
                {
                    if (items[j] != null && items[j].m_InteractableScanItem == item)
                    {
                        return counters[i];
                    }
                }
            }

            return null;
        }

        internal static InteractableCashierCounter FindCounterForCard(InteractableCard3d card)
        {
            var counters = Counters;
            if (card == null || counters == null)
            {
                return null;
            }

            for (var i = 0; i < counters.Count; i++)
            {
                var customer = counters[i]?.m_CurrentCustomer;
                var cards = customer?.GetCardInBagList();
                for (var j = 0; cards != null && j < cards.Count; j++)
                {
                    if (cards[j] == card)
                    {
                        return counters[i];
                    }
                }
            }

            return null;
        }

        internal static InteractableCashierCounter FindCounterForCash(InteractableCustomerCash cash)
        {
            var counters = Counters;
            if (cash == null || counters == null)
            {
                return null;
            }

            for (var i = 0; i < counters.Count; i++)
            {
                var customer = counters[i]?.m_CurrentCustomer;
                if (customer != null && customer.m_CustomerCash == cash)
                {
                    return counters[i];
                }
            }

            return null;
        }

        internal static void PrepareCarrier(Customer customer, InteractableCashierCounter counter)
        {
            Write(customer, "m_IsActive", true);
            Write(customer, "m_ItemScannedCount", 0);
            Write(customer, "m_TotalScannedItemCost", 0f);
            Write(customer, "m_HasCheckedOut", false);
            Write(customer, "m_CurrentQueueCashierCounter", counter);
            customer.m_CustomerCash.Init(customer);
            customer.m_CustomerCash.gameObject.SetActive(false);
        }

        /// <summary>Returns the credit card machine to its resting spot and hides the hand-held
        /// card model. Mirrors the card half of vanilla <c>OnPressSpaceBar</c>, which only runs
        /// when the local player is standing at the counter. The client needs it too when a
        /// prediction is rolled back so a later re-entry into giving change cannot capture the
        /// already-moved machine as its "original" position.</summary>
        internal static void RestoreCreditCardMachine(InteractableCashierCounter counter)
        {
            if (counter == null)
            {
                return;
            }

            var machine = counter.m_CreditCardMachineModel;
            if (machine != null)
            {
                if (Read(counter, "m_CreditCardMachineOriginalPos") is Vector3 position)
                {
                    machine.position = position;
                }

                if (Read(counter, "m_CreditCardMachineOriginalRot") is Quaternion rotation)
                {
                    machine.rotation = rotation;
                }
            }

            counter.m_CreditCardModel?.SetActive(false);
        }

        internal static void ResetCheckoutVisuals(InteractableCashierCounter counter)
        {
            if (counter == null)
            {
                return;
            }

            var money = counter.m_InteractableCounterMoneyChangeList;
            for (var i = 0; money != null && i < money.Count; i++)
            {
                money[i]?.ResetAmountGiven();
            }

            CashScreen(counter)?.ResetCounter();
            CreditScreen(counter)?.ResetCounter();
            if (counter.m_CreditCardModel != null && counter.m_CreditCardModel.activeSelf)
            {
                RestoreCreditCardMachine(counter);
            }

            Write(counter, "m_IsUsingCard", false);
            Write(counter, "m_IsStartGivingChange", false);
            Write(counter, "m_IsChangeReady", false);
            Write(counter, "m_CurrentMoneyChangeValue", 0d);
            Write(counter, "m_TooMuchChangeGiven", false);
            counter.UpdateCashierCounterState(ECashierCounterState.Idle);
            counter.UpdateCurrentCustomer(null);
            counter.SetPlsaticBagVisibility(false);
        }

        internal static void ReleaseContents(Customer customer)
        {
            if (customer == null)
            {
                return;
            }

            var items = customer.m_ItemInBagList;
            for (var i = items == null ? -1 : items.Count - 1; i >= 0; i--)
            {
                items[i]?.DisableItem();
            }
            items?.Clear();

            var cards = customer.m_CardInBagList;
            for (var i = cards == null ? -1 : cards.Count - 1; i >= 0; i--)
            {
                cards[i]?.OnDestroyed();
            }
            cards?.Clear();
        }

        /// <summary>Reverses one item's scan so the same item can be scanned again. Mirrors the
        /// interactable half of <c>InteractableScanItem.OnMouseButtonUp</c> without touching the
        /// customer's scanned counters, which the caller restores from its own snapshot.</summary>
        internal static void UnscanItem(InteractableCashierCounter counter, Customer customer, Item item)
        {
            if (item == null || customer == null || item.m_InteractableScanItem == null)
            {
                return;
            }

            item.m_InteractableScanItem.StopLerpToTransform();
            item.gameObject.SetActive(true);
            item.m_Collider.enabled = true;
            var rigidbody = EnsureItemRigidbody(item);
            if (rigidbody != null)
            {
                rigidbody.isKinematic = false;
            }

            item.m_InteractableScanItem.enabled = true;
            item.m_InteractableScanItem.RegisterScanItem(customer,
                counter != null ? counter.m_ScannedItemLerpPos : null);
        }

        /// <summary>Card counterpart of <see cref="UnscanItem"/>.</summary>
        internal static void UnscanCard(InteractableCashierCounter counter, Customer customer,
            InteractableCard3d card)
        {
            if (card == null || customer == null)
            {
                return;
            }

            card.StopLerpToTransform();
            card.gameObject.SetActive(true);
            card.m_Collider.enabled = true;
            var rigidbody = EnsureCardRigidbody(card);
            if (rigidbody != null)
            {
                rigidbody.isKinematic = false;
            }

            card.RegisterScanCard(customer, counter != null ? counter.m_ScannedItemLerpPos : null);
        }

        internal static Item SpawnItem(InteractableCashierCounter counter, Customer customer,
            EItemType type, float price, int slot)
        {
            var meshData = InventoryBase.GetItemMeshData(type);
            if (meshData == null || counter?.m_CustomerPlaceItemPos == null)
            {
                return null;
            }

            var item = ItemSpawnManager.GetItem(counter.transform);
            item.SetMesh(meshData.mesh, meshData.material, type, meshData.meshSecondary,
                meshData.materialSecondary, meshData.materialList);
            item.SetCurrentPrice(price);
            item.transform.parent = counter.transform;
            item.transform.position = counter.m_CustomerPlaceItemPos.position;
            item.transform.position += counter.m_CustomerPlaceItemPos.forward * (-0.025f * (slot % 8));
            item.transform.position += counter.m_CustomerPlaceItemPos.right * (0.1f * Mathf.Clamp(slot / 8, 0, 1));
            item.transform.position += Vector3.up * (0.2f * Mathf.Clamp(slot / 16, 0, 2));
            item.transform.rotation = counter.m_CustomerPlaceItemPos.rotation;
            // Vanilla drops every item with a random tilt (Customer.WaypointEndUpdate's
            // counter-placement block), using the integer Random.Range overload. Without it the
            // item lands perfectly axis-aligned, so it settles flat instead of rocking/settling
            // the way a dropped item should.
            item.transform.Rotate(new Vector3(UnityEngine.Random.Range(-30, -5),
                UnityEngine.Random.Range(-5, 5), UnityEngine.Random.Range(-5, 5)));
            item.m_Mesh.enabled = true;
            item.gameObject.SetActive(true);
            item.m_Collider.enabled = true;
            // The 1.00 beta creates the item Rigidbody lazily via GetOrCreateRigidbody, so a
            // plain m_Rigidbody null check silently skipped physics on that build. Use the same
            // accessor the card path established.
            var rigidbody = EnsureItemRigidbody(item);
            if (rigidbody != null)
            {
                rigidbody.isKinematic = false;
            }

            item.m_InteractableScanItem.enabled = true;
            item.m_InteractableScanItem.RegisterScanItem(customer, counter.m_ScannedItemLerpPos);
            customer.m_ItemInBagList.Add(item);
            return item;
        }

        internal static InteractableCard3d SpawnCard(InteractableCashierCounter counter, Customer customer,
            CardData data, float price, int slot)
        {
            if (data == null || counter?.m_CustomerPlaceItemPos == null)
            {
                return null;
            }

            var cardUi = SceneRef<Card3dUISpawner>.Get()?.GetCardUI();
            var cardObject = ShelfManager.SpawnInteractableObject(EObjectType.Card3d);
            var card = cardObject?.GetComponent<InteractableCard3d>();
            if (cardUi == null || card == null)
            {
                cardUi?.DisableCard();
                if (cardObject != null)
                {
                    UnityEngine.Object.Destroy(cardObject);
                }

                return null;
            }

            cardUi.m_CardUI.SetCardUI(data);
            card.SetCardUIFollow(cardUi);
            card.SetEnableCollision(false);
            card.SetCurrentPrice(price);
            var rigidbody = EnsureCardRigidbody(card);
            if (rigidbody == null)
            {
                cardUi.DisableCard();
                UnityEngine.Object.Destroy(cardObject);
                return null;
            }

            card.transform.parent = counter.transform;
            card.transform.position = counter.m_CustomerPlaceItemPos.position;
            card.transform.localScale = Vector3.one;
            card.transform.position += counter.m_CustomerPlaceItemPos.forward * (-0.035f * (slot % 8));
            card.transform.position += counter.m_CustomerPlaceItemPos.right * (0.1f * Mathf.Clamp(slot / 8, 0, 1));
            card.transform.position += Vector3.up * (0.3f * Mathf.Clamp(slot / 16, 0, 2));
            card.transform.rotation = counter.m_CustomerPlaceItemPos.rotation;
            // Cards use the same random drop tilt as items, offset by 180 degrees on X so the
            // face-down card tip is the leading edge, exactly as vanilla places them (integer
            // Random.Range overload: Customer.WaypointEndUpdate).
            card.transform.Rotate(new Vector3(UnityEngine.Random.Range(-30, -5) + 180,
                UnityEngine.Random.Range(-5, 5), UnityEngine.Random.Range(-5, 5)));
            card.m_Card3dUI.gameObject.SetActive(true);
            card.gameObject.SetActive(true);
            card.m_Collider.enabled = true;
            rigidbody.isKinematic = false;
            card.RegisterScanCard(customer, counter.m_ScannedItemLerpPos);
            customer.m_CardInBagList.Add(card);
            return card;
        }

        internal static Rigidbody EnsureCardRigidbody(InteractableCard3d card)
        {
            if (card == null)
            {
                return null;
            }

            if (card.m_Rigidbody != null)
            {
                return card.m_Rigidbody;
            }

            if (!_cardRigidbodyMethodResolved)
            {
                _getOrCreateCardRigidbody = ReflectionSurface.OptionalMethod(
                    typeof(InteractableCard3d), "GetOrCreateRigidbody");
                _cardRigidbodyMethodResolved = true;
            }

            var rigidbody = _getOrCreateCardRigidbody?.Invoke(card, null) as Rigidbody
                ?? card.GetComponent<Rigidbody>();
            card.m_Rigidbody = rigidbody;
            return rigidbody;
        }

        /// <summary>The item counterpart of <see cref="EnsureCardRigidbody"/>. The 1.00 beta
        /// creates an item's Rigidbody lazily via <c>Item.GetOrCreateRigidbody</c>; builds without
        /// that member already carry it on the prefab. Resolving both keeps the counter placement
        /// physically identical on every supported build.</summary>
        internal static Rigidbody EnsureItemRigidbody(Item item)
        {
            if (item == null)
            {
                return null;
            }

            if (item.m_Rigidbody != null)
            {
                return item.m_Rigidbody;
            }

            if (!_itemRigidbodyMethodResolved)
            {
                _getOrCreateItemRigidbody = ReflectionSurface.OptionalMethod(
                    typeof(Item), "GetOrCreateRigidbody");
                _itemRigidbodyMethodResolved = true;
            }

            var rigidbody = _getOrCreateItemRigidbody?.Invoke(item, null) as Rigidbody
                ?? item.GetComponent<Rigidbody>();
            item.m_Rigidbody = rigidbody;
            return rigidbody;
        }
    }
}
