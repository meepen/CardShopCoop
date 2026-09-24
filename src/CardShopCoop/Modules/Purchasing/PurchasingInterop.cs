using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Modules.Catalog;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Modules.Purchasing
{
    /// <summary>
    /// The small game-facing surface used by Purchasing. Private UI fields are deliberately
    /// resolved here: both supported game builds have the public checkout methods, but their
    /// cart storage and refresh helpers are implementation details.
    /// </summary>
    internal static class PurchasingInterop
    {
        private static readonly FieldInfo FiRestockCart = AccessTools.Field(
            typeof(RestockItemScreen), "m_CartItemList");
        private static readonly FieldInfo FiScannerIndexes = AccessTools.Field(
            typeof(ScannerRestockScreen), "m_RestockIndexList");
        private static readonly FieldInfo FiScannerCounts = AccessTools.Field(
            typeof(ScannerRestockScreen), "m_RestockBoxCountList");
        private static readonly FieldInfo FiScannerPage = AccessTools.Field(
            typeof(ScannerRestockScreen), "m_PageIndex");
        private static readonly MethodInfo MiScannerPage = AccessTools.Method(
            typeof(ScannerRestockScreen), "EvaluatePanelUIPage");
        private static readonly MethodInfo MiScannerTotals = AccessTools.Method(
            typeof(ScannerRestockScreen), "UpdateTotalCostAndBoxCount");
        private static readonly FieldInfo FiCheckoutRestockScreen = AccessTools.Field(
            typeof(RestockItemCheckoutScreen), "m_RestockItemScreen");
        private static readonly FieldInfo FiPanelIndex = AccessTools.Field(
            typeof(RestockItemPanelUI), "m_Index");
        private static readonly FieldInfo FiPanelScreen = AccessTools.Field(
            typeof(RestockItemPanelUI), "m_RestockItemScreen");
        private static readonly FieldInfo FiLicenseGroup = AccessTools.Field(
            typeof(RestockItemPanelUI), "m_LicenseUIGrp");
        private static readonly FieldInfo FiProductGroup = AccessTools.Field(
            typeof(RestockItemPanelUI), "m_UIGrp");
        private static readonly FieldInfo FiFurnitureIndex = AccessTools.Field(
            typeof(FurnitureShopConfirmPurchaseScreen), "m_Index");
        private static readonly FieldInfo FiFurnitureOwner = AccessTools.Field(
            typeof(FurnitureShopConfirmPurchaseScreen), "m_FurnitureShopUIScreen");
        private static readonly Type PlatformManagerType = AccessTools.TypeByName("GA.PlatformManager");
        private static readonly PropertyInfo PlatformManagerInstance = PlatformManagerType?.GetProperty(
            "Instance", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo IsFurnitureAvailableMethod = PlatformManagerType?.GetMethod(
            "IsFurnitureAvailable", BindingFlags.Public | BindingFlags.Instance, null,
            new[] { typeof(EObjectType) }, null);

        internal static Dictionary<int, int> RestockCart(RestockItemScreen screen)
            => FiRestockCart?.GetValue(screen) as Dictionary<int, int>;

        internal static RestockItemScreen RestockScreen(RestockItemCheckoutScreen screen)
            => FiCheckoutRestockScreen?.GetValue(screen) as RestockItemScreen;

        internal static List<int> ScannerIndexes(ScannerRestockScreen screen)
            => FiScannerIndexes?.GetValue(screen) as List<int>;

        internal static List<int> ScannerCounts(ScannerRestockScreen screen)
            => FiScannerCounts?.GetValue(screen) as List<int>;

        internal static int PanelIndex(RestockItemPanelUI panel)
        {
            var value = FiPanelIndex?.GetValue(panel);
            return value == null ? -1 : Convert.ToInt32(value);
        }

        internal static int FurnitureIndex(FurnitureShopConfirmPurchaseScreen screen)
        {
            var value = FiFurnitureIndex?.GetValue(screen);
            return value == null ? -1 : Convert.ToInt32(value);
        }

        internal static FurnitureShopUIScreen FurnitureOwner(
            FurnitureShopConfirmPurchaseScreen screen)
            => FiFurnitureOwner?.GetValue(screen) as FurnitureShopUIScreen;

        internal static PurchaseLine RestockLine(int index, int count)
        {
            CatalogApi.TryGetProduct(index, out var row);
            if (row == null)
            {
                return new PurchaseLine
                {
                    ItemType = EItemType.None,
                    IsBigBox = false,
                    Name = "",
                    Count = count,
                };
            }

            return new PurchaseLine
            {
                ItemType = row.itemType,
                IsBigBox = row.isBigBox,
                Name = row.name ?? "",
                Count = count,
            };
        }

        internal static void RemoveRestockQuantities(RestockItemScreen screen,
            Dictionary<int, int> purchased)
        {
            var cart = RestockCart(screen);
            if (cart == null || purchased == null)
                throw new InvalidOperationException("Purchasing restock cart is not available.");

            // The checkout's optimistic apply already removed the purchased lines, and an accepted
            // outcome replays this same apply (PredictionApi.ApplyConfirmed) rather than undoing it
            // first. An absent line therefore means "already removed," not a missing cart entry.
            foreach (var purchase in purchased)
            {
                if (!cart.TryGetValue(purchase.Key, out var current))
                {
                    continue;
                }

                current -= purchase.Value;
                if (current > 0)
                {
                    cart[purchase.Key] = current;
                }
                else
                {
                    cart.Remove(purchase.Key);
                }
            }

            screen.m_RestockItemCheckoutScreen?.UpdateData(screen, cart, false);
        }

        internal static void RestoreRestockCart(RestockItemScreen screen,
            Dictionary<int, int> before)
        {
            var cart = RestockCart(screen);
            if (cart == null || before == null)
                return;
            cart.Clear();
            foreach (var item in before)
                cart[item.Key] = item.Value;
            screen.m_RestockItemCheckoutScreen?.UpdateData(screen, cart, false);
        }

        internal static void RemoveScannerQuantities(ScannerRestockScreen screen,
            List<KeyValuePair<int, int>> purchased)
        {
            var indexes = ScannerIndexes(screen);
            var counts = ScannerCounts(screen);
            if (indexes == null || counts == null || purchased == null)
                throw new InvalidOperationException("Purchasing scanner cart is not available.");

            for (var purchaseIndex = 0; purchaseIndex < purchased.Count; purchaseIndex++)
            {
                var requestedIndex = purchased[purchaseIndex].Key;
                var remaining = purchased[purchaseIndex].Value;
                for (var i = indexes.Count - 1;
                    i >= 0 && remaining > 0; i--)
                {
                    if (indexes[i] != requestedIndex)
                    {
                        continue;
                    }

                    counts[i] -= remaining;
                    if (counts[i] <= 0)
                    {
                        remaining = -counts[i];
                        indexes.RemoveAt(i);
                        counts.RemoveAt(i);
                    }
                    else
                        remaining = 0;
                }
            }

            var page = FiScannerPage?.GetValue(screen) is int value ? value : 0;
            MiScannerPage?.Invoke(screen, new object[] { page });
            MiScannerTotals?.Invoke(screen, null);
        }

        internal static void RestoreScannerCart(ScannerRestockScreen screen,
            List<KeyValuePair<int, int>> before)
        {
            var indexes = ScannerIndexes(screen);
            var counts = ScannerCounts(screen);
            if (indexes == null || counts == null || before == null)
                return;
            indexes.Clear();
            counts.Clear();
            for (var i = 0; i < before.Count; i++)
            {
                indexes.Add(before[i].Key);
                counts.Add(before[i].Value);
            }
            var page = FiScannerPage?.GetValue(screen) is int value ? value : 0;
            MiScannerPage?.Invoke(screen, new object[] { page });
            MiScannerTotals?.Invoke(screen, null);
        }

        internal static bool IsSameFurniturePurchase(FurnitureShopConfirmPurchaseScreen screen,
            FurnitureShopUIScreen owner, int index)
        {
            return screen != null && ReferenceEquals(FurnitureOwner(screen), owner)
                && FurnitureIndex(screen) == index;
        }

        internal static void RefreshProductLicensePanels(int index)
        {
            if (index < 0)
            {
                return;
            }

            var panels = SceneComponentRegistry<RestockItemPanelUI>.Snapshot(
                UnityEngine.SceneManagement.SceneManager.GetActiveScene(), activeOnly: true);
            for (var i = 0; i < panels.Count; i++)
            {
                var panel = panels[i];
                if (panel == null || !panel.gameObject.scene.IsValid()
                    || !panel.gameObject.activeInHierarchy || PanelIndex(panel) != index)
                {
                    continue;
                }

                var screen = FiPanelScreen?.GetValue(panel) as RestockItemScreen;
                if (screen != null)
                {
                    panel.Init(screen, index);
                    continue;
                }

                if (CatalogApi.TryGetLicense(index, out var unlocked))
                {
                    if (FiLicenseGroup?.GetValue(panel) is GameObject licenseGroup)
                    {
                        licenseGroup.SetActive(!unlocked);
                    }
                    if (FiProductGroup?.GetValue(panel) is GameObject productGroup)
                    {
                        productGroup.SetActive(unlocked);
                    }
                }
            }
        }

        internal static bool IsFurnitureAvailable(EObjectType objectType)
        {
            // The legacy branch has no GA.PlatformManager. Absence is the compatibility path;
            // when the beta surface exists, failing to read it must fail closed.
            if (PlatformManagerType == null)
            {
                return true;
            }

            if (PlatformManagerInstance == null || IsFurnitureAvailableMethod == null)
            {
                CoopPlugin.Log.LogError("Purchasing could not resolve beta furniture entitlement API");
                return false;
            }

            try
            {
                var manager = PlatformManagerInstance.GetValue(null);
                return manager != null
                    && Convert.ToBoolean(IsFurnitureAvailableMethod.Invoke(manager,
                        new object[] { objectType }));
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogError("Purchasing could not query furniture entitlement: " + error);
                return false;
            }
        }

        internal static void CloseFurnitureConfirmation(
            FurnitureShopConfirmPurchaseScreen screen)
        {
            screen?.CloseScreen();
        }

        internal static void ReopenFurnitureConfirmation(FurnitureShopUIScreen owner, int index)
        {
            if (owner != null && index >= 0)
                owner.OnPressPanelUIButton(index);
        }

        internal static Transform RandomPackageSpawn()
            => RestockManager.GetRandomPackageSpawnPos();

        internal static ShelfManager FindShelfManager()
            => SceneRef<ShelfManager>.Get();
    }
}
