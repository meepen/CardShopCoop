using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Catalog
{
    /// <summary>Applies the newest host catalog snapshot when local content is ready.</summary>
    [ClientBehaviour]
    public sealed class CatalogClientBehaviour : CoopBehaviour
    {
        private static readonly FieldInfo PanelScreen = typeof(RestockItemPanelUI).GetField(
            "m_RestockItemScreen", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo PanelIndex = typeof(RestockItemPanelUI).GetField(
            "m_Index", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo ScannerPage = typeof(ScannerRestockScreen).GetField(
            "m_PageIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo ScannerEvaluatePage = typeof(ScannerRestockScreen).GetMethod(
            "EvaluatePanelUIPage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo ScannerUpdateTotals = typeof(ScannerRestockScreen).GetMethod(
            "UpdateTotalCostAndBoxCount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static CatalogClientBehaviour _active;
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private CatalogLicenseStateMessage _pendingState;
        private readonly Dictionary<string, CatalogLicenseDeltaMessage> _pendingDeltas = new();
        private readonly HashSet<int> _productSideEffectsApplied = new();
        private bool _contentReady;
        private bool _shutdown;

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
            {
                return;
            }


            _context = RuntimeContext;
            var registered = false;
            try
            {
                _context.Messages.RegisterAttributedHandlers(this);
                registered = true;
                _active = this;
                CatalogInterop.ProbeOptionalSurfaces();
                _harmony = new Harmony("com.zwhit.cardshopcoop.catalog.client");
                CatalogPatches.ApplyClientLifecycle(_harmony);
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
                {
                    _context.Messages.UnregisterAttributedHandlers(this);
                }


                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }


                _context = null;
                throw;
            }
        }

        private void OnReady(CEventPlayer_GameDataFinishLoaded _)
            => ContentReady();

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
            => ContentReady();

        internal static void ContentReady()
        {
            if (_active == null || _active._shutdown)
            {
                return;
            }


            _active._contentReady = true;
            _active.ApplyPendingState();
        }

        [MessageHandler(typeof(CatalogLicenseStateMessage))]
        private void HandleState(MessageContext context, CatalogLicenseStateMessage message)
        {
            if (_shutdown)
            {
                return;
            }


            _pendingState = message;
            ApplyPendingState();
        }

        private void ApplyPendingState()
        {
            if (_shutdown || !_contentReady || !_context.InGame() || !CatalogInterop.IsSceneReady)
            {
                return;
            }


            if (_pendingState != null)
            {
                ApplyState(_pendingState);
                _pendingState = null;
            }
            if (_pendingDeltas.Count > 0)
            {
                var deltas = new List<KeyValuePair<string, CatalogLicenseDeltaMessage>>(
                    _pendingDeltas);
                for (var i = 0; i < deltas.Count; i++)
                {
                    if (ApplyDelta(deltas[i].Value))
                    {
                        _pendingDeltas.Remove(deltas[i].Key);
                    }

                }
            }
        }

        private void ApplyState(CatalogLicenseStateMessage state)
        {
            for (var i = 0; i < CatalogInterop.Count; i++)
            {
                var data = CatalogInterop.At(i);
                if (data == null)
                {

                    throw new InvalidOperationException("Catalog row " + i + " could not be applied");
                }


                var shouldUnlock = false;
                for (var j = 0; j < state.Entries.Count; j++)
                {
                    if (string.Equals(CatalogInterop.IdentityKey(data),
                        CatalogInterop.IdentityKey(state.Entries[j]), StringComparison.Ordinal))
                    {
                        shouldUnlock = true;
                        break;
                    }
                }

                var unlocked = CPlayerData.GetIsItemLicenseUnlocked(i);
                if (!shouldUnlock)
                {
                    _productSideEffectsApplied.Remove(i);
                }


                if (unlocked == shouldUnlock)
                {
                    continue;
                }


                if (!CatalogInterop.SetLicense(i, shouldUnlock))
                {

                    throw new InvalidOperationException("Catalog license apply failed for row " + i);
                }


                if (!unlocked && shouldUnlock)
                {
                    ApplyProductEntitlementSideEffectsOnce(i, data);
                }

            }

            if (CPlayerData.m_IsScannerRestockUnlocked != state.ScannerUnlocked)
            {
                CPlayerData.m_IsScannerRestockUnlocked = state.ScannerUnlocked;
                if (state.ScannerUnlocked)
                {
                    CEventManager.QueueEvent(new CEventPlayer_ScannerRestockUnlocked());
                }

            }

            RefreshOpenProductPanels();
            RefreshOpenScannerScreens();
        }

        [MessageHandler(typeof(CatalogLicenseDeltaMessage))]
        private void HandleDelta(MessageContext context, CatalogLicenseDeltaMessage message)
        {
            if (_shutdown)
            {
                return;
            }


            if (!_contentReady || !_context.InGame() || !CatalogInterop.IsSceneReady)
            {
                DeferDelta(message);
                return;
            }

            ApplyDelta(message);
        }

        private bool ApplyDelta(CatalogLicenseDeltaMessage message)
        {
            var sideEffectIndex = -1;
            RestockData sideEffectData = null;
            PredictionApi.ApplyAuthoritative(message.PredictionId, () =>
            {
                if (message.Scanner)
                {
                    CPlayerData.m_IsScannerRestockUnlocked = message.Unlocked;
                    if (message.Unlocked)
                    {
                        CEventManager.QueueEvent(new CEventPlayer_ScannerRestockUnlocked());
                    }

                }
                else
                {
                    if (!CatalogInterop.TryResolve(message.ItemType, message.IsBigBox,
                        message.Name ?? "", out var index, out var data) || data == null)
                    {
                        // The name-wire converter uses EItemType.None when a negotiated host
                        // product has no local counterpart. Reconcile the prediction but do not
                        // invent a local entitlement for content this process cannot represent.
                        if (message.ItemType == EItemType.None)
                        {
                            return;
                        }


                        throw new InvalidOperationException("Catalog delta product could not be resolved");
                    }
                    var wasUnlocked = CPlayerData.GetIsItemLicenseUnlocked(index);
                    if (!CatalogInterop.SetLicense(index, message.Unlocked))
                    {

                        throw new InvalidOperationException("Catalog delta license apply failed");
                    }


                    if (!message.Unlocked)
                    {
                        _active._productSideEffectsApplied.Remove(index);
                    }

                    else if (!wasUnlocked)
                    {
                        sideEffectIndex = index;
                        sideEffectData = data;
                    }
                }

                RefreshOpenProductPanels();
                RefreshOpenScannerScreens();
            });
            if (sideEffectData != null)
            {
                ApplyProductEntitlementSideEffectsOnce(sideEffectIndex, sideEffectData);
            }


            return true;
        }

        private void DeferDelta(CatalogLicenseDeltaMessage message)
        {
            var key = DeltaKey(message);
            if (_pendingDeltas.TryGetValue(key, out var previous))
            {
                PredictionApi.ConfirmSuperseded(previous.PredictionId);
            }


            _pendingDeltas[key] = message;
        }

        private static string DeltaKey(CatalogLicenseDeltaMessage message)
        {
            if (message.Scanner)
            {

                return "scanner";
            }


            return "product:" + (int)message.ItemType + ":" + (message.IsBigBox ? "1" : "0")
                + ":" + (message.Name ?? "");
        }

        internal static void ApplyPurchaseProductLicense(int index, bool unlocked)
            => _active?.ApplyLocalProduct(index, unlocked);

        internal static void ApplyPurchaseProductEntitlementSideEffects(int index)
            => _active?.ApplyLocalProductEntitlementSideEffects(index);

        internal static void ApplyPurchaseScannerLicense(bool unlocked)
            => _active?.ApplyLocalScanner(unlocked);

        private void ApplyLocalProduct(int index, bool unlocked)
        {
            if (!CatalogInterop.SetLicense(index, unlocked))
            {

                throw new InvalidOperationException("Catalog local license apply failed");
            }


            RefreshOpenProductPanels();
        }

        private void ApplyLocalScanner(bool unlocked)
        {
            CPlayerData.m_IsScannerRestockUnlocked = unlocked;
            RefreshOpenScannerScreens();
        }

        private void ApplyLocalProductEntitlementSideEffects(int index)
        {
            if (!CatalogInterop.TryAt(index, out var data) || data == null)
            {

                throw new InvalidOperationException("Catalog accepted product could not be resolved");
            }


            ApplyProductEntitlementSideEffectsOnce(index, data);
        }

        private void ApplyProductEntitlementSideEffectsOnce(int index, RestockData data)
        {
            if (_productSideEffectsApplied.Contains(index))
            {
                return;
            }


            AchievementManager.OnItemLicenseUnlocked(data.itemType);
            GameInstance.m_IsItemLicenseUnlocked = true;
            if (data.itemType == EItemType.BasicCardBox)
            {
                TutorialManager.AddTaskValue(ETutorialTaskCondition.UnlockBasicCardBox, 1f);
            }


            _productSideEffectsApplied.Add(index);
        }

        internal static void InventoryReset()
        {
            if (_active == null || _active._shutdown)
            {
                return;
            }


            _active._pendingState = null;
            _active.ClearPendingDeltas();
            _active._productSideEffectsApplied.Clear();
            _active._contentReady = false;
        }

        private void ClearPendingDeltas()
        {
            foreach (var delta in _pendingDeltas.Values)
            {
                PredictionApi.ConfirmSuperseded(delta.PredictionId);
            }


            _pendingDeltas.Clear();
        }

        private static void RefreshOpenProductPanels()
        {
            var panels = SceneComponentRegistry<RestockItemPanelUI>.Snapshot(
                SceneManager.GetActiveScene(), activeOnly: true);
            for (var i = 0; i < panels.Count; i++)
            {
                var panel = panels[i];
                if (panel == null || !panel.gameObject.scene.IsValid()
                    || !panel.gameObject.activeInHierarchy)
                {
                    continue;
                }


                if (PanelIndex?.GetValue(panel) is not int index || index < 0)
                {
                    continue;
                }


                var screen = PanelScreen?.GetValue(panel) as RestockItemScreen;
                if (screen != null)
                {
                    panel.Init(screen, index);
                    continue;
                }

                var unlocked = CPlayerData.GetIsItemLicenseUnlocked(index);
                panel.m_LicenseUIGrp?.SetActive(!unlocked);
                panel.m_UIGrp?.SetActive(unlocked);
            }
        }

        private static void RefreshOpenScannerScreens()
        {
            var screens = SceneComponentRegistry<ScannerRestockScreen>.Snapshot(
                SceneManager.GetActiveScene(), activeOnly: true);
            for (var i = 0; i < screens.Count; i++)
            {
                var screen = screens[i];
                if (screen == null || !screen.gameObject.scene.IsValid()
                    || !screen.gameObject.activeInHierarchy)
                {
                    continue;
                }


                var unlocked = CPlayerData.m_IsScannerRestockUnlocked;
                screen.m_UnlockedGrp?.SetActive(unlocked);
                screen.m_LockedGrp?.SetActive(!unlocked);
                if (unlocked && ScannerPage?.GetValue(screen) is int page)
                {
                    ScannerEvaluatePage?.Invoke(screen, new object[] { page });
                }


                ScannerUpdateTotals?.Invoke(screen, null);
            }
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }


            _shutdown = true;
            _context?.Messages.UnregisterAttributedHandlers(this);
            _harmony?.UnpatchSelf();
            _harmony = null;
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnReady);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _pendingState = null;
            ClearPendingDeltas();
            _productSideEffectsApplied.Clear();
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }


            _context = null;
        }

        private void OnDestroy() => Shutdown();
    }
}
