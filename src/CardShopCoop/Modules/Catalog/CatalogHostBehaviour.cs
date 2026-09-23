using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Attributes;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Catalog
{
    /// <summary>
    /// Host authority for catalog entitlements. Full entitlement state is sent only as a join
    /// baseline; normal changes are keyed deltas.
    /// </summary>
    [ServerBehaviour]
    public sealed class CatalogHostBehaviour : CoopBehaviour
    {
        private static readonly FieldInfo ScannerPage = typeof(ScannerRestockScreen).GetField(
            "m_PageIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo ScannerEvaluatePage = typeof(ScannerRestockScreen).GetMethod(
            "EvaluatePanelUIPage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo ScannerUpdateTotals = typeof(ScannerRestockScreen).GetMethod(
            "UpdateTotalCostAndBoxCount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static CatalogHostBehaviour _active;
        private readonly HashSet<int> _joined = new();
        private readonly HashSet<int> _baselinePending = new();
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
        private bool _applying;

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
                return;

            _context = RuntimeContext;
            var registered = false;
            try
            {
                _context.Messages.RegisterAttributedHandlers(this);
                registered = true;
                _active = this;
                CatalogInterop.ProbeOptionalSurfaces();
                _harmony = new Harmony("com.zwhit.cardshopcoop.catalog.host");
                _harmony.CreateClassProcessor(typeof(ProductLicensePatch)).Patch();
                _harmony.CreateClassProcessor(typeof(ScannerUnlockPatch)).Patch();
                CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnReady);
                SceneManager.sceneLoaded += OnSceneLoaded;
            }
            catch
            {
                _harmony?.UnpatchSelf();
                _harmony = null;
                CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnReady);
                SceneManager.sceneLoaded -= OnSceneLoaded;
                if (registered)
                    _context.Messages.UnregisterAttributedHandlers(this);
                if (ReferenceEquals(_active, this))
                    _active = null;
                _context = null;
                throw;
            }
        }

        private void OnReady(CEventPlayer_GameDataFinishLoaded _)
            => SendPendingBaselines();

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
            => SendPendingBaselines();

        [OnFullyJoined]
        private void SendJoinBaseline(PeerConnection peer)
        {
            if (peer == null)
                return;

            _joined.Add(peer.Id);
            if (!SendState(peer.Id))
                _baselinePending.Add(peer.Id);
        }

        [OnClientDisconnected]
        private void ForgetPeer(PeerConnection peer, DisconnectInfo _)
        {
            if (peer == null)
                return;
            _joined.Remove(peer.Id);
            _baselinePending.Remove(peer.Id);
        }

        internal static bool ApplyProductLicense(EItemType itemType, bool isBigBox, string name,
            bool applyVanillaEntitlementSideEffects, bool publish, out bool changed)
        {
            changed = false;
            var active = _active;
            if (active == null || !CatalogInterop.TryResolve(itemType, isBigBox, name,
                out var index, out var data) || data == null || string.IsNullOrEmpty(data.name)
                || !CatalogInterop.IsWireableItemType(data.itemType, "catalog product"))
                return false;

            if (!CatalogInterop.TryGetLicense(index, out var unlocked, out _) || unlocked)
                return unlocked;

            active._applying = true;
            try
            {
                if (!CatalogInterop.SetLicense(index, true)
                    || !CatalogInterop.TryGetLicense(index, out var applied, out _) || !applied)
                    return false;
            }
            finally
            {
                active._applying = false;
            }

            changed = true;
            if (applyVanillaEntitlementSideEffects)
                ApplyProductEntitlementSideEffects(data);
            if (publish)
                active.BroadcastProductDelta(Guid.Empty, data, true);
            return true;
        }

        internal static bool PublishProductLicense(RestockData data, Guid predictionId)
        {
            if (_active == null || data == null)
                return false;
            _active.BroadcastProductDelta(predictionId, data, true);
            return true;
        }

        internal static bool ApplyScannerUnlock(bool publish, out bool changed)
        {
            changed = false;
            if (_active == null)
                return false;
            if (CPlayerData.m_IsScannerRestockUnlocked)
                return true;

            _active._applying = true;
            try
            {
                CPlayerData.m_IsScannerRestockUnlocked = true;
            }
            finally
            {
                _active._applying = false;
            }
            changed = true;
            if (publish)
                _active.BroadcastScannerDelta(Guid.Empty, true);
            return true;
        }

        internal static void RefreshScannerLicenseUi()
        {
            var screens = SceneComponentRegistry<ScannerRestockScreen>.Snapshot(
                SceneManager.GetActiveScene(), activeOnly: true);
            for (var i = 0; i < screens.Count; i++)
            {
                var screen = screens[i];
                if (screen == null || !screen.gameObject.scene.IsValid()
                    || !screen.gameObject.activeInHierarchy)
                    continue;

                var unlocked = CPlayerData.m_IsScannerRestockUnlocked;
                screen.m_UnlockedGrp?.SetActive(unlocked);
                screen.m_LockedGrp?.SetActive(!unlocked);
                if (unlocked && ScannerPage?.GetValue(screen) is int page)
                    ScannerEvaluatePage?.Invoke(screen, new object[] { page });
                ScannerUpdateTotals?.Invoke(screen, null);
            }
        }

        internal static bool PublishScannerUnlock(Guid predictionId)
        {
            if (_active == null)
                return false;
            _active.BroadcastScannerDelta(predictionId, true);
            return true;
        }

        internal static bool RestoreProductLicenseState(CatalogInterop.LicenseState state)
        {
            if (_active == null || state == null)
                return false;

            _active._applying = true;
            try
            {
                if (!CatalogInterop.TryRestoreLicenseState(state, out var changed))
                    return false;
                for (var i = 0; i < changed.Count; i++)
                {
                    var data = CatalogInterop.At(changed[i].Index);
                    if (data != null)
                        _active.BroadcastProductDelta(Guid.Empty, data, changed[i].Unlocked);
                }
            }
            finally
            {
                _active._applying = false;
            }
            return true;
        }

        internal static bool RestoreScannerUnlock(bool unlocked)
        {
            if (_active == null)
                return false;
            CPlayerData.m_IsScannerRestockUnlocked = unlocked;
            _active.BroadcastScannerDelta(Guid.Empty, unlocked);
            return true;
        }

        internal static void ApplyProductEntitlementSideEffects(RestockData data)
        {
            if (data == null)
                return;

            AchievementManager.OnItemLicenseUnlocked(data.itemType);
            GameInstance.m_IsItemLicenseUnlocked = true;
            if (data.itemType == EItemType.BasicCardBox)
                TutorialManager.AddTaskValue(ETutorialTaskCondition.UnlockBasicCardBox, 1f);
        }

        private bool SendState(int connectionId)
        {
            if (_shutdown || !_context.InGame() || !CatalogInterop.IsSceneReady
                || !CatalogInterop.TryBuildLicenseEntries(out var entries, out var scanner))
                return false;

            _context.Send(connectionId, new CatalogLicenseStateMessage
            {
                ScannerUnlocked = scanner,
                Entries = entries,
            });
            _baselinePending.Remove(connectionId);
            return true;
        }

        private void BroadcastProductDelta(Guid predictionId, RestockData data, bool unlocked)
        {
            if (_shutdown || !_context.InGame() || data == null)
                return;

            _context.Broadcast(new CatalogLicenseDeltaMessage
            {
                PredictionId = predictionId,
                Scanner = false,
                Unlocked = unlocked,
                ItemType = data.itemType,
                IsBigBox = data.isBigBox,
                Name = data.name ?? "",
            });
        }

        private void BroadcastScannerDelta(Guid predictionId, bool unlocked)
        {
            if (_shutdown || !_context.InGame())
                return;

            _context.Broadcast(new CatalogLicenseDeltaMessage
            {
                PredictionId = predictionId,
                Scanner = true,
                Unlocked = unlocked,
            });
        }

        private void SendPendingBaselines()
        {
            foreach (var connectionId in new List<int>(_baselinePending))
            {
                if (!_joined.Contains(connectionId))
                {
                    _baselinePending.Remove(connectionId);
                    continue;
                }
                SendState(connectionId);
            }
        }

        internal void Shutdown()
        {
            if (_shutdown)
                return;

            _shutdown = true;
            _context?.Messages.UnregisterAttributedHandlers(this);
            _harmony?.UnpatchSelf();
            _harmony = null;
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnReady);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _joined.Clear();
            _baselinePending.Clear();
            if (ReferenceEquals(_active, this))
                _active = null;
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        [HarmonyPatch(typeof(CPlayerData), "SetUnlockItemLicense")]
        private static class ProductLicensePatch
        {
            [HarmonyPostfix]
            private static void Postfix(int index)
            {
                if (_active != null && !_active._applying)
                {
                    var data = CatalogInterop.At(index);
                    if (data != null)
                        _active.BroadcastProductDelta(Guid.Empty, data, true);
                }
            }
        }

        [HarmonyPatch(typeof(ScannerRestockScreen), "OnPressUnlockButton")]
        private static class ScannerUnlockPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (_active != null && !_active._applying)
                    _active.BroadcastScannerDelta(Guid.Empty,
                        CPlayerData.m_IsScannerRestockUnlocked);
            }
        }
    }
}
