using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Modules.StoreAccess
{
    /// <summary>Reflection boundary for sign meshes and gameplay state.</summary>
    internal static class StoreAccessInterop
    {
        private static InteractableOpenCloseSign _openSign;
        private static InteractableWarehouseAllowEnterSign _warehouseSign;
        private static UnlockRoomManager _unlockRoomManager;
        private static readonly FieldInfo FiOpenAnimation = AccessTools.Field(
            typeof(InteractableOpenCloseSign), "m_Anim");
        private static readonly FieldInfo FiOpenSwapping = AccessTools.Field(
            typeof(InteractableOpenCloseSign), "m_IsSwapping");
        private static readonly MethodInfo MiOpenMesh = AccessTools.Method(
            typeof(InteractableOpenCloseSign), "EvaluateSignOpenCloseMesh");
        private static readonly MethodInfo MiOpenDelay = AccessTools.Method(
            typeof(InteractableOpenCloseSign), "DelaySwapMesh");
        private static readonly FieldInfo FiWarehouseAnimation = AccessTools.Field(
            typeof(InteractableWarehouseAllowEnterSign), "m_Anim");
        private static readonly FieldInfo FiWarehouseSwapping = AccessTools.Field(
            typeof(InteractableWarehouseAllowEnterSign), "m_IsSwapping");
        private static readonly MethodInfo MiWarehouseMesh = AccessTools.Method(
            typeof(InteractableWarehouseAllowEnterSign), "EvaluateSignOpenCloseMesh");
        private static readonly MethodInfo MiWarehouseDelay = AccessTools.Method(
            typeof(InteractableWarehouseAllowEnterSign), "DelaySwapMesh");
        private static readonly MethodInfo MiWarehouseEvaluate = AccessTools.Method(
            typeof(UnlockRoomManager), "EvaluateWarehouseRoomOpenClose");

        internal static void Reset()
        {
            _openSign = null;
            _warehouseSign = null;
            _unlockRoomManager = null;
        }

        internal static T FindSceneObject<T>() where T : Component
        {
            var all = Resources.FindObjectsOfTypeAll<T>();
            for (var i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].gameObject.scene.IsValid())
                    return all[i];
            }

            return null;
        }

        internal static InteractableOpenCloseSign FindOpenSign()
        {
            if (_openSign == null)
                _openSign = FindSceneObject<InteractableOpenCloseSign>();
            return _openSign;
        }

        internal static InteractableWarehouseAllowEnterSign FindWarehouseSign()
        {
            if (_warehouseSign == null)
                _warehouseSign = FindSceneObject<InteractableWarehouseAllowEnterSign>();
            return _warehouseSign;
        }

        private static UnlockRoomManager FindUnlockRoomManager()
        {
            if (_unlockRoomManager == null)
                _unlockRoomManager = FindSceneObject<UnlockRoomManager>();
            return _unlockRoomManager;
        }

        internal static void RefreshOpenMesh(InteractableOpenCloseSign sign)
        {
            if (sign != null)
                MiOpenMesh?.Invoke(sign, null);
        }

        internal static bool PlayOpenAnimation(InteractableOpenCloseSign sign)
            => PlayAnimation(sign, FiOpenAnimation, FiOpenSwapping, MiOpenDelay);

        internal static bool PlayWarehouseAnimation(InteractableWarehouseAllowEnterSign sign)
            => PlayAnimation(sign, FiWarehouseAnimation, FiWarehouseSwapping, MiWarehouseDelay);

        internal static void RefreshWarehouseMesh(InteractableWarehouseAllowEnterSign sign)
        {
            if (sign != null)
                MiWarehouseMesh?.Invoke(sign, null);
        }

        internal static void RefreshWarehouseAccess(UnlockRoomManager manager)
        {
            manager ??= FindUnlockRoomManager();
            if (manager != null)
                MiWarehouseEvaluate?.Invoke(manager, null);
        }

        internal static bool IsSceneReady()
            => FindOpenSign() != null
                && (FindWarehouseSign() != null || FindUnlockRoomManager() != null);

        private static bool PlayAnimation(MonoBehaviour sign, FieldInfo animationField,
            FieldInfo swappingField, MethodInfo delayMethod)
        {
            if (sign == null || animationField == null || swappingField == null || delayMethod == null)
                return false;

            var animation = animationField.GetValue(sign) as Animation;
            var delay = delayMethod.Invoke(sign, null) as IEnumerator;
            if (animation == null || delay == null)
                return false;
            if (swappingField.GetValue(sign) is bool swapping && swapping)
                return false;

            swappingField.SetValue(sign, true);
            animation.Play();
            sign.StartCoroutine(delay);
            return true;
        }
    }
}
