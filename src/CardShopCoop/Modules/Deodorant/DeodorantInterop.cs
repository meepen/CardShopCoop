using System;
using System.Reflection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Modules.Deodorant
{
    /// <summary>Reflection boundary for fields that identify the handheld spray path.</summary>
    internal static class DeodorantInterop
    {
        private static readonly FieldInfo HeldItem = AccessTools.Field(
            typeof(InteractionPlayerController), "m_CurrentHoldSprayItem");
        private static readonly FieldInfo MouseDown = AccessTools.Field(
            typeof(InteractionPlayerController), "m_IsHoldingMouseDown");
        private static readonly FieldInfo MouseDownTime = AccessTools.Field(
            typeof(InteractionPlayerController), "m_MouseDownTime");
        private static readonly FieldInfo MouseDownTimeTotal = AccessTools.Field(
            typeof(InteractionPlayerController), "m_MouseDownTimeTotal");
        private static readonly FieldInfo HoldAutoFireRate = AccessTools.Field(
            typeof(InteractionPlayerController), "m_MouseHoldAutoFireRate");
        private static readonly FieldInfo CustomerSmelly = AccessTools.Field(
            typeof(Customer), "m_IsSmelly");
        private static readonly FieldInfo CustomerSmellyMeter = AccessTools.Field(
            typeof(Customer), "m_SmellyMeter");
        private static readonly FieldInfo CustomerSmellyFx = AccessTools.Field(
            typeof(Customer), "m_SmellyFX");

        internal static bool IsHoldingSpray(InteractionPlayerController controller)
        {
            return TryGetHeldSpray(controller, out _);
        }

        internal static bool TryGetSpray(InteractionPlayerController controller, out Vector3 position)
        {
            position = default;
            if (!TryGetHeldSpray(controller, out var item))
                return false;
            position = item.transform.position;
            return IsFinite(position);
        }

        internal static bool TryGetContent(InteractionPlayerController controller, out float content)
        {
            content = 0f;
            if (!TryGetHeldSpray(controller, out var item))
                return false;
            content = item.GetContentFill();
            return IsFinite(content);
        }

        internal static bool TrySetContent(InteractionPlayerController controller, float content)
        {
            if (!TryGetHeldSpray(controller, out var item) || !IsFinite(content))
                return false;
            item.SetContentFill(content);
            return true;
        }

        internal static int GetSmellyMeter(Customer customer)
            => customer == null || CustomerSmellyMeter == null
                ? 0 : (int)(CustomerSmellyMeter.GetValue(customer) ?? 0);

        internal static void ApplyCustomerState(Customer customer, bool isSmelly, int smellyMeter)
        {
            if (customer == null)
            {
                return;
            }

            CustomerSmelly?.SetValue(customer, isSmelly);
            CustomerSmellyMeter?.SetValue(customer, Math.Max(0, smellyMeter));
            var smellyFx = CustomerSmellyFx?.GetValue(customer) as GameObject;
            smellyFx?.SetActive(isSmelly);
            var manager = SceneRef<CustomerManager>.Get();
            if (manager == null)
            {
                return;
            }

            if (isSmelly)
            {
                manager.AddToSmellyCustomerList(customer);
            }
            else
            {
                manager.RemoveFromSmellyCustomerList(customer);
            }
        }

        internal static bool TryGetSprayTick(InteractionPlayerController controller,
            out Vector3 position, out float before, out float after)
        {
            position = default;
            before = 0f;
            after = 0f;
            if (!TryGetSpray(controller, out position)
                || !TryGetContent(controller, out before)
                || MouseDownTime == null || MouseDownTimeTotal == null || HoldAutoFireRate == null
                || MouseDownTime.GetValue(controller) is not float elapsed
                || MouseDownTimeTotal.GetValue(controller) is not float total
                || HoldAutoFireRate.GetValue(controller) is not float rate)
            {
                return false;
            }

            if (elapsed + Time.deltaTime < rate || total + Time.deltaTime <= 0.2f || before <= 0f)
            {
                return false;
            }

            after = Mathf.Max(0f, before - Time.fixedDeltaTime / 2.5f);
            return true;
        }

        private static bool TryGetHeldSpray(InteractionPlayerController controller, out Item item)
        {
            item = null;
            if (controller == null || HeldItem == null || MouseDown == null
                || MouseDown.GetValue(controller) is not bool held || !held)
            {
                return false;
            }

            item = HeldItem.GetValue(controller) as Item;
            return item != null && item.GetItemType() == EItemType.Deodorant;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }
}
