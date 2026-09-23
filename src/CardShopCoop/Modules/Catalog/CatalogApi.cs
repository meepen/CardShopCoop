using System;

namespace CardShopCoop.Modules.Catalog
{
    /// <summary>
    /// Narrow cross-boundary API for Core, the co-op window, and Purchasing. Catalog parity and
    /// entitlement state remain module-owned; callers do not need to know about EPL files,
    /// markers, backups, or behaviour lifetimes.
    /// </summary>
    public static class CatalogApi
    {
        internal sealed class PurchaseLicenseState
        {
            internal readonly CatalogInterop.LicenseState Value;

            internal PurchaseLicenseState(CatalogInterop.LicenseState value)
            {
                Value = value;
            }
        }

        private static bool _enumLendWarned;

        public static bool RestartRequiredForJoin => CatalogParity.RestartRequiredForJoin;
        public static bool RestartRequiredForSolo => CatalogParity.RestartRequiredForSolo;

        internal static bool IsWireableItemType(EItemType itemType, string context = null)
            => CatalogInterop.IsWireableItemType(itemType, context);

        /// <summary>Returns the player-facing borrowed-registry notice, or null when no host
        /// registry is currently installed. The first observed notice is logged once.</summary>
        public static string EnumLendState()
        {
            try
            {
                if (!CatalogParity.HostEnumInstalled())
                {
                    return null;
                }

                if (!_enumLendWarned)
                {
                    _enumLendWarned = true;
                    CoopPlugin.Log.LogWarning(CoopPlugin.Name + ": your custom-card database is currently the HOST's synced copy from a co-op session. Your OWN solo modded saves may not load until you restore it (restore via the co-op window) and RESTART the game.");
                }

                return "custom-card database is the HOST's copy (co-op sync) - solo modded saves may not load; restore via the co-op window";
            }
            catch (Exception error)
            {
                Swallow.Log(error);
                return null;
            }
        }

        public static bool RestoreEnumBackup(out string message)
            => CatalogParity.RestoreEnumBackup(out message);

        internal static bool TryGetProduct(int index, out RestockData data)
            => CatalogInterop.TryAt(index, out data);

        internal static bool TryResolveProduct(EItemType itemType, bool isBigBox, string name,
            out int index, out RestockData data)
            => CatalogInterop.TryResolve(itemType, isBigBox, name, out index, out data);

        internal static bool TryGetLicense(int index, out bool unlocked)
            => CatalogInterop.TryGetLicense(index, out unlocked, out _);

        internal static bool TryCapturePurchaseState(out PurchaseLicenseState state)
        {
            state = null;
            if (!CatalogInterop.TryCaptureLicenseState(out var value))
                return false;

            state = new PurchaseLicenseState(value);
            return true;
        }

        internal static bool HostRestorePurchaseState(PurchaseLicenseState state)
            => state != null && CatalogHostBehaviour.RestoreProductLicenseState(state.Value);

        internal static bool HostRestoreScannerLicense(bool unlocked)
            => CatalogHostBehaviour.RestoreScannerUnlock(unlocked);

        internal static bool HostApplyProductLicense(EItemType itemType, bool isBigBox, string name,
            bool applyVanillaEntitlementSideEffects, bool publish, out bool changed)
            => CatalogHostBehaviour.ApplyProductLicense(itemType, isBigBox, name,
                applyVanillaEntitlementSideEffects, publish, out changed);

        internal static bool HostApplyScannerLicense(bool publish, out bool changed)
            => CatalogHostBehaviour.ApplyScannerUnlock(publish, out changed);

        internal static bool HostPublishProductLicense(RestockData data, Guid predictionId)
            => CatalogHostBehaviour.PublishProductLicense(data, predictionId);

        internal static bool HostPublishScannerLicense(Guid predictionId)
            => CatalogHostBehaviour.PublishScannerUnlock(predictionId);

        internal static void HostApplyProductEntitlementSideEffects(RestockData data)
            => CatalogHostBehaviour.ApplyProductEntitlementSideEffects(data);

        internal static void ApplyClientProductLicense(int index, bool unlocked)
            => CatalogClientBehaviour.ApplyPurchaseProductLicense(index, unlocked);

        internal static void ApplyClientProductEntitlementSideEffects(int index)
            => CatalogClientBehaviour.ApplyPurchaseProductEntitlementSideEffects(index);

        internal static void ApplyClientScannerLicense(bool unlocked)
            => CatalogClientBehaviour.ApplyPurchaseScannerLicense(unlocked);

        internal static void RefreshScannerLicenseUi()
            => CatalogHostBehaviour.RefreshScannerLicenseUi();
    }
}
