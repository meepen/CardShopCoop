using System;
using System.Collections;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Catalog;
using CardShopCoop.Modules.Economy;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Modules.Report;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Modules.Purchasing
{
    /// <summary>
    /// Host-side purchasing authority. A request is only a proposal: every identity, quantity,
    /// price, wallet check, spawn position, and vanilla side effect is rebuilt on this machine.
    /// </summary>
    [ServerBehaviour]
    public sealed class PurchasingHostBehaviour : CoopBehaviour
    {
        private const int MaxLines = 64;
        private const int MaxBoxesPerLine = 100;
        private const int MaxTotalBoxes = 1000;
        private const float ScannerUnlockPrice = 12000f;
        private static readonly System.Reflection.FieldInfo PendingRestockBoxes =
            AccessTools.Field(typeof(RestockManager), "m_SpawnBoxItemWaitingList");
        private static readonly System.Reflection.FieldInfo PendingRestockIndexes =
            AccessTools.Field(typeof(CPlayerData), "m_SpawnBoxRestockIndexWaitingList");
        private static readonly System.Reflection.FieldInfo PendingRestockCounts =
            AccessTools.Field(typeof(CPlayerData), "m_SpawnBoxItemCountWaitingList");
        private static readonly System.Reflection.FieldInfo EventQueue =
            AccessTools.Field(typeof(CEventManager), "m_queueEvent");
        private static readonly System.Reflection.FieldInfo TutorialFinished =
            AccessTools.Field(typeof(TutorialManager), "m_FinishedTutorial");

        private sealed class PlannedLine
        {
            internal PurchaseLine Request;
            internal int RestockIndex = -1;
            internal RestockData Restock;
            internal FurniturePurchaseData Furniture;
            internal double Price;
        }

        private sealed class PurchasePlan
        {
            internal PurchaseKind Kind;
            internal bool IdempotentNoOp;
            internal readonly List<PlannedLine> Lines = new();
            internal double Total;
            internal int TotalBoxes;
        }

        private sealed class DeliveryResult
        {
            internal double Charged;
            internal int RestockBoxes;
            internal int RestockItems;
            internal bool Success;
            internal string Failure;
        }

        private sealed class MutationSnapshot
        {
            private sealed class TransactionState
            {
                internal TransactionData Reference;
                internal int Day;
                internal int HourMinute;
                internal int Index;
                internal int Amount;
                internal ETransactionType TransactionType;
                internal float MoneyChangeAmount;
                internal CardData CardData;
            }

            internal readonly HashSet<UnityEngine.Object> Objects = new();
            internal readonly List<float> AverageCosts = new();
            internal readonly List<int> CurrentItemCounts = new();
            private readonly List<TransactionState> Transactions = new();
            internal readonly List<GameReportDataCollect> PastReports = new();
            internal readonly List<bool> Achievements = new();
            internal readonly List<TutorialData> TutorialData = new();
            internal readonly List<RestockData> PendingRestockRows = new();
            internal readonly List<int> PendingRestockIndexes = new();
            internal readonly List<int> PendingRestockCounts = new();
            internal readonly GameReportDataCollect Report;
            internal readonly GameReportDataCollect PermanentReport;
            internal readonly int TutorialIndex;
            internal readonly bool TutorialWasFinished;
            internal readonly bool HasTutorialManager;
            internal readonly TutorialManager TutorialManagerInstance;
            internal readonly int ShopExpPoint;
            internal readonly int ShopLevel;
            internal readonly bool GameInstanceLicenseUnlocked;
            internal readonly CatalogApi.PurchaseLicenseState CatalogLicenses;
            internal readonly bool ScannerUnlocked;
            internal readonly int EventCount;

            internal MutationSnapshot(PurchasePlan plan)
            {
                Capture(RestockManager.GetItemPackagingBoxList());
                Capture(RestockManager.GetCardPackagingBoxList());
                Capture(RestockManager.GetShelfPackagingBoxList());
                if (CPlayerData.m_AverageItemCostList != null)
                    AverageCosts.AddRange(CPlayerData.m_AverageItemCostList);
                if (CPlayerData.m_CurrentTotalItemCountList != null)
                    CurrentItemCounts.AddRange(CPlayerData.m_CurrentTotalItemCountList);
                CaptureTransactions();
                Report = CPlayerData.m_GameReportDataCollect;
                PermanentReport = CPlayerData.m_GameReportDataCollectPermanent;
                if (CPlayerData.m_GameReportDataCollectPastList != null)
                    PastReports.AddRange(CPlayerData.m_GameReportDataCollectPastList);
                if (CPlayerData.m_IsAchievementUnlocked != null)
                    Achievements.AddRange(CPlayerData.m_IsAchievementUnlocked);
                CaptureTutorialData();
                TutorialIndex = CPlayerData.m_TutorialIndex;
                var tutorialManager = SceneRef<TutorialManager>.Get();
                TutorialManagerInstance = tutorialManager;
                HasTutorialManager = tutorialManager != null && TutorialFinished != null;
                TutorialWasFinished = HasTutorialManager
                    && (bool)TutorialFinished.GetValue(tutorialManager);
                ShopExpPoint = CPlayerData.m_ShopExpPoint;
                ShopLevel = CPlayerData.m_ShopLevel;
                GameInstanceLicenseUnlocked = GameInstance.m_IsItemLicenseUnlocked;
                CatalogApi.PurchaseLicenseState licenses = null;
                if (plan.Kind == PurchaseKind.ProductLicense
                    && !CatalogApi.TryCapturePurchaseState(out licenses))
                {
                    throw new InvalidOperationException("catalog license state could not be snapshotted");
                }
                CatalogLicenses = licenses;
                var restockManager = SceneRef<RestockManager>.Get();
                if (restockManager != null)
                {
                    CopyList(PendingRestockBoxes?.GetValue(restockManager) as IList,
                        PendingRestockRows);
                }
                if (CPlayerData.m_SpawnBoxRestockIndexWaitingList != null)
                    PendingRestockIndexes.AddRange(CPlayerData.m_SpawnBoxRestockIndexWaitingList);
                if (CPlayerData.m_SpawnBoxItemCountWaitingList != null)
                    PendingRestockCounts.AddRange(CPlayerData.m_SpawnBoxItemCountWaitingList);
                ScannerUnlocked = CPlayerData.m_IsScannerRestockUnlocked;
                EventCount = QueueCount();
            }

            internal void Rollback()
            {
                DestroyNew(RestockManager.GetItemPackagingBoxList());
                DestroyNew(RestockManager.GetCardPackagingBoxList());
                DestroyNew(RestockManager.GetShelfPackagingBoxList());
                RestoreList(CPlayerData.m_AverageItemCostList, AverageCosts);
                RestoreList(CPlayerData.m_CurrentTotalItemCountList, CurrentItemCounts);
                CPlayerData.m_GameReportDataCollect = Report;
                CPlayerData.m_GameReportDataCollectPermanent = PermanentReport;
                RestoreList(CPlayerData.m_GameReportDataCollectPastList, PastReports);
                RestoreList(CPlayerData.m_IsAchievementUnlocked, Achievements);
                RestoreTutorialData();
                CPlayerData.m_TutorialIndex = TutorialIndex;
                if (HasTutorialManager)
                    TutorialFinished.SetValue(TutorialManagerInstance, TutorialWasFinished);
                CPlayerData.m_ShopExpPoint = ShopExpPoint;
                CPlayerData.m_ShopLevel = ShopLevel;
                RestoreTransactions();
                RestoreList(PendingRestockBoxes, SceneRef<RestockManager>.Get(), PendingRestockRows);
                RestoreList(CPlayerData.m_SpawnBoxRestockIndexWaitingList, PendingRestockIndexes);
                RestoreList(CPlayerData.m_SpawnBoxItemCountWaitingList, PendingRestockCounts);
                TrimEvents(EventCount);
                GameInstance.m_IsItemLicenseUnlocked = GameInstanceLicenseUnlocked;
                if (CatalogLicenses != null
                    && !CatalogApi.HostRestorePurchaseState(CatalogLicenses))
                {
                    CoopPlugin.Log.LogError("Purchasing could not restore the catalog license state");
                }
                if (CPlayerData.m_IsScannerRestockUnlocked != ScannerUnlocked
                    && !CatalogApi.HostRestoreScannerLicense(ScannerUnlocked))
                {
                    CoopPlugin.Log.LogError("Purchasing could not restore scanner entitlement state");
                }
            }

            private void Capture<T>(IList<T> objects) where T : UnityEngine.Object
            {
                if (objects == null)
                    return;
                for (var i = 0; i < objects.Count; i++)
                {
                    if (objects[i] != null)
                        Objects.Add(objects[i]);
                }
            }

            private void DestroyNew<T>(IList<T> objects) where T : UnityEngine.Object
            {
                if (objects == null)
                    return;
                for (var i = objects.Count - 1; i >= 0; i--)
                {
                    if (objects[i] != null && !Objects.Contains(objects[i]))
                        UnityEngine.Object.Destroy(objects[i]);
                }
            }

            private void CaptureTransactions()
            {
                var transactions = CPlayerData.m_TransactionDataList;
                if (transactions == null)
                    return;
                for (var i = 0; i < transactions.Count; i++)
                {
                    var transaction = transactions[i];
                    Transactions.Add(transaction == null ? null : new TransactionState
                    {
                        Reference = transaction,
                        Day = transaction.day,
                        HourMinute = transaction.hourMinute,
                        Index = transaction.index,
                        Amount = transaction.amount,
                        TransactionType = transaction.transactionType,
                        MoneyChangeAmount = transaction.moneyChangeAmount,
                        CardData = transaction.cardData,
                    });
                }
            }

            private void CaptureTutorialData()
            {
                if (CPlayerData.m_TutorialDataList == null)
                    return;
                for (var i = 0; i < CPlayerData.m_TutorialDataList.Count; i++)
                {
                    var item = CPlayerData.m_TutorialDataList[i];
                    TutorialData.Add(item == null ? null : new TutorialData
                    {
                        tutorialTaskCondition = item.tutorialTaskCondition,
                        value = item.value,
                    });
                }
            }

            private void RestoreTransactions()
            {
                var transactions = CPlayerData.m_TransactionDataList;
                if (transactions == null)
                {
                    transactions = new List<TransactionData>();
                    CPlayerData.m_TransactionDataList = transactions;
                }
                transactions.Clear();
                for (var i = 0; i < Transactions.Count; i++)
                {
                    var state = Transactions[i];
                    if (state == null)
                    {
                        transactions.Add(null);
                        continue;
                    }
                    state.Reference.day = state.Day;
                    state.Reference.hourMinute = state.HourMinute;
                    state.Reference.index = state.Index;
                    state.Reference.amount = state.Amount;
                    state.Reference.transactionType = state.TransactionType;
                    state.Reference.moneyChangeAmount = state.MoneyChangeAmount;
                    state.Reference.cardData = state.CardData;
                    transactions.Add(state.Reference);
                }
            }

            private void RestoreTutorialData()
            {
                if (CPlayerData.m_TutorialDataList == null)
                    CPlayerData.m_TutorialDataList = new List<TutorialData>();
                CPlayerData.m_TutorialDataList.Clear();
                for (var i = 0; i < TutorialData.Count; i++)
                {
                    var item = TutorialData[i];
                    CPlayerData.m_TutorialDataList.Add(item == null ? null : new TutorialData
                    {
                        tutorialTaskCondition = item.tutorialTaskCondition,
                        value = item.value,
                    });
                }
            }

            private static void CopyList<T>(IList source, List<T> target)
            {
                if (source == null || target == null)
                    return;
                for (var i = 0; i < source.Count; i++)
                    target.Add((T)source[i]);
            }

            private static void RestoreList<T>(List<T> target, List<T> source)
            {
                if (target == null || source == null)
                    return;
                target.Clear();
                target.AddRange(source);
            }

            private static void RestoreList<T>(System.Reflection.FieldInfo field, object instance,
                List<T> source)
            {
                var list = field?.GetValue(instance) as IList;
                if (list == null || source == null)
                    return;
                list.Clear();
                for (var i = 0; i < source.Count; i++)
                    list.Add(source[i]);
            }

            private static int QueueCount()
            {
                var queue = EventQueue?.GetValue(SceneRef<CEventManager>.Get()) as Queue;
                return queue?.Count ?? 0;
            }

            private static void TrimEvents(int count)
            {
                var queue = EventQueue?.GetValue(SceneRef<CEventManager>.Get()) as Queue;
                if (queue == null || queue.Count <= count)
                    return;

                var retained = new List<object>(count);
                while (queue.Count > 0)
                {
                    var item = queue.Dequeue();
                    if (retained.Count < count)
                        retained.Add(item);
                }
                for (var i = 0; i < retained.Count; i++)
                    queue.Enqueue(retained[i]);
            }
        }

        private static PurchasingHostBehaviour _active;
        private bool _shutdown;
        private CoopRuntimeContext _context;
        private Harmony _harmony;

        private void OnEnable()
        {
            if (_shutdown || _context != null)
            {
                return;
            }

            _context = RuntimeContext;
            var handlersRegistered = false;
            try
            {
                _context.Messages.RegisterAttributedHandlers(this);
                handlersRegistered = true;
                _active = this;
                _harmony = new Harmony("com.zwhit.cardshopcoop.purchasing.host");
                _harmony.CreateClassProcessor(typeof(ProductLicenseGuardPatch)).Patch();
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogError("Purchasing host initialization failed: " + exception);
                if (handlersRegistered)
                {
                    _context.Messages.UnregisterAttributedHandlers(this);
                }

                _harmony?.UnpatchSelf();
                _harmony = null;

                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }

                _context = null;
                throw;
            }
        }

        [MessageHandler(typeof(PurchaseIntentMessage))]
        private void HandleIntent(MessageContext context, PurchaseIntentMessage message)
        {
            if (_shutdown || !_context.InGame()
                || context?.Connection == null || message == null)
            {
                return;
            }

            var connectionId = context.Connection.Id;
            var outcome = Process(message, connectionId, PeerLabel(connectionId));
            if (outcome.Success)
                SendOutcome(connectionId, outcome);
            else if (message.PredictionId != Guid.Empty)
                PredictionApi.Rollback(_context, connectionId, message.PredictionId);
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            _context?.Messages.UnregisterAttributedHandlers(this);
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

            _harmony?.UnpatchSelf();
            _harmony = null;

            _context = null;
        }

        private void OnDestroy() => Shutdown();

        private PurchaseOutcomeMessage Process(PurchaseIntentMessage message, int connectionId,
            string peer)
        {
            if (!Enum.IsDefined(typeof(PurchaseKind), message.Kind))
            {
                return Failure(message, peer, "purchase kind is invalid");
            }

            EconomyAuthority.HostSpendReservation spend = null;
            MutationSnapshot mutation = null;
            PurchasePlan plan = null;
            try
            {
                if (!TryBuildPlan(message, out plan, out var error))
                {
                    return Failure(message, peer, error);
                }

                if (plan.IdempotentNoOp)
                {
                    return Accepted(message, 0d);
                }

                if (!EconomyAuthority.TryReserveHostSpend(plan.Total, out spend))
                {
                    return Failure(message, peer,
                        "not enough money - the purchase was cancelled");
                }

                // Admit the authoritative debit before any factory, entitlement, report,
                // transaction, or save side effect. The debit is not undone by a later network
                // result; only local game mutations are compensated below.
                if (!EconomyAuthority.QueueHostSpend(spend))
                {
                    return Failure(message, peer, "purchase failed while admitting the debit");
                }
                spend = null;

                mutation = new MutationSnapshot(plan);

                DeliveryResult delivery;
                try
                {
                    delivery = Deliver(plan, peer);
                }
                catch (Exception exception)
                {
                    mutation.Rollback();
                    CoopPlugin.Log.LogError($"Purchasing delivery crashed for {peer}: {exception}");
                    return Failure(message, peer, "purchase failed while delivering the order");
                }

                if (!delivery.Success)
                {
                    mutation.Rollback();
                    return Failure(message, peer, delivery.Failure ?? "purchase delivery failed");
                }

                // Delivery recomputes the canonical amount again. In the normal path this is
                // exactly the preflight total, but keep the shared reservation honest if a game
                // factory changes a price between those two operations.
                if (Math.Abs(delivery.Charged - plan.Total) > 0.0001d)
                {
                    mutation.Rollback();
                    CoopPlugin.Log.LogError($"Purchasing canonical amount changed for "
                        + $"{peer}: "
                        + $"preflight={plan.Total}, delivered={delivery.Charged}");
                    return Failure(message, peer, "purchase failed while charging the order");
                }

                ApplyCommittedSideEffects(plan, delivery);
                if (plan.Kind == PurchaseKind.ProductLicense
                    || plan.Kind == PurchaseKind.ScannerLicense)
                {
                    ReportApi.NotifyCommittedPurchaseMutation();
                }
                if (plan.Kind == PurchaseKind.ProductLicense)
                {
                    if (!CatalogApi.HostPublishProductLicense(plan.Lines[0].Restock, Guid.Empty))
                    {
                        throw new InvalidOperationException("catalog license publication failed");
                    }
                    // Achievement/tutorial/platform effects are not reversible (Steam in
                    // particular has no un-achieve operation). Apply them only after the
                    // publication boundary has succeeded.
                    CatalogApi.HostApplyProductEntitlementSideEffects(plan.Lines[0].Restock);
                }
                else if (plan.Kind == PurchaseKind.ScannerLicense
                    && !CatalogApi.HostPublishScannerLicense(Guid.Empty))
                {
                    throw new InvalidOperationException("catalog scanner license publication failed");
                }

                var outcome = Accepted(message, delivery.Charged);
                // A delayed shelf save is itself an irreversible side effect. Schedule it only
                // after the outcome and every rollback-capable mutation have succeeded; there is
                // then no fallible purchase work left which could require a rollback.
                if (plan.Kind == PurchaseKind.Restock || plan.Kind == PurchaseKind.Furniture)
                {
                    StartCoroutine(SaveShelfDataAfterPurchase());
                }
                return outcome;
            }
            catch (Exception exception)
            {
                try
                {
                    mutation?.Rollback();
                }
                catch (Exception rollbackError)
                {
                    CoopPlugin.Log.LogError("Purchasing rollback failed for " + peer + ": "
                        + rollbackError);
                }
                CoopPlugin.Log.LogError($"Purchasing preflight crashed for {peer}: {exception}");
                return Failure(message, peer, "purchase could not be validated");
            }
        }

        private bool TryBuildPlan(PurchaseIntentMessage message, out PurchasePlan plan,
            out string error)
        {
            plan = new PurchasePlan
            {
                Kind = message.Kind,
            };
            error = null;

            if (message.Lines == null || message.Lines.Count == 0
                || message.Lines.Count > MaxLines)
            {
                error = "purchase has an invalid line count";
                return false;
            }

            if (message.Kind != PurchaseKind.Restock && message.ScannerCheckout)
            {
                error = "scanner cart flag is invalid for this purchase";
                return false;
            }

            switch (message.Kind)
            {
                case PurchaseKind.Restock:
                    return TryBuildRestockPlan(message, plan, out error);
                case PurchaseKind.Furniture:
                    return TryBuildFurniturePlan(message, plan, out error);
                case PurchaseKind.ProductLicense:
                    return TryBuildProductLicensePlan(message, plan, out error);
                case PurchaseKind.ScannerLicense:
                    return TryBuildScannerLicensePlan(message, plan, out error);
                default:
                    error = "purchase kind is invalid";
                    return false;
            }
        }

        private bool TryBuildRestockPlan(PurchaseIntentMessage message, PurchasePlan plan,
            out string error)
        {
            error = null;
            if (message.ScannerCheckout && !CPlayerData.m_IsScannerRestockUnlocked)
            {
                error = "scanner restock is locked on the host";
                return false;
            }

            var totalBoxes = 0;
            for (var i = 0; i < message.Lines.Count; i++)
            {
                var request = message.Lines[i];
                if (!ValidProductLine(request, MaxBoxesPerLine))
                {
                    error = $"restock line {i} is invalid";
                    return false;
                }

                if (!CatalogApi.TryResolveProduct(request.ItemType, request.IsBigBox,
                    request.Name ?? "", out var index, out var row) || row == null)
                {
                    error = $"restock line {i} is not in the host catalog";
                    return false;
                }

                if (!CatalogApi.TryGetLicense(index, out var licensed) || !licensed)
                {
                    error = $"restock line {i} is not licensed on the host";
                    return false;
                }

                var maxCount = RestockManager.GetMaxItemCountInBox(row.itemType, row.isBigBox);
                var itemCost = CPlayerData.GetItemCost(row.itemType);
                var linePrice = (double)itemCost * maxCount * request.Count;
                if (maxCount <= 0 || !IsFinite(itemCost) || itemCost < 0f
                    || !IsFinite(linePrice))
                {
                    error = $"restock line {i} has an invalid host price";
                    return false;
                }

                totalBoxes += request.Count;
                if (totalBoxes > MaxTotalBoxes)
                {
                    error = "restock order is too large";
                    return false;
                }

                plan.Lines.Add(new PlannedLine
                {
                    Request = request,
                    RestockIndex = index,
                    Restock = row,
                    Price = linePrice,
                });
                plan.Total += linePrice;
            }

            plan.TotalBoxes = totalBoxes;
            plan.Total += ScannerDeliveryFee(totalBoxes);

            return IsValidTotal(plan.Total, out error);
        }

        private static bool TryBuildFurniturePlan(PurchaseIntentMessage message,
            PurchasePlan plan, out string error)
        {
            error = null;
            if (message.Lines.Count != 1 || !ValidFurnitureLine(message.Lines[0]))
            {
                error = "furniture purchase must contain one valid item";
                return false;
            }

            var request = message.Lines[0];
            var furniture = InventoryBase.GetFurniturePurchaseData(request.ObjectType);
            if (furniture == null || InventoryBase.GetSpawnInteractableObjectPrefab(
                request.ObjectType) == null)
            {
                error = "furniture is not available in the host catalog";
                return false;
            }

            if (!PurchasingInterop.IsFurnitureAvailable(request.ObjectType))
            {
                error = "furniture is not available for this platform entitlement";
                return false;
            }

            if (CPlayerData.m_ShopLevel + 1 < furniture.levelRequirement)
            {
                error = "shop level is too low for this furniture";
                return false;
            }

            if (!IsFinite(furniture.price) || furniture.price <= 0f)
            {
                error = "furniture has an invalid host price";
                return false;
            }

            plan.Lines.Add(new PlannedLine
            {
                Request = request,
                Furniture = furniture,
                Price = furniture.price,
            });
            plan.Total = furniture.price;
            return IsValidTotal(plan.Total, out error);
        }

        private static bool TryBuildProductLicensePlan(PurchaseIntentMessage message,
            PurchasePlan plan, out string error)
        {
            error = null;
            if (message.Lines.Count != 1 || !ValidProductLine(message.Lines[0], 1))
            {
                error = "product license purchase must contain one valid item";
                return false;
            }

            var request = message.Lines[0];
            if (!CatalogApi.TryResolveProduct(request.ItemType, request.IsBigBox,
                request.Name ?? "", out var index, out var row) || row == null)
            {
                error = "product license is not in the host catalog";
                return false;
            }

            if (!CatalogApi.TryGetLicense(index, out var alreadyUnlocked))
            {
                error = "product license state is unavailable on the host";
                return false;
            }

            if (alreadyUnlocked)
            {
                plan.IdempotentNoOp = true;
                plan.Lines.Add(new PlannedLine
                {
                    Request = request,
                    RestockIndex = index,
                    Restock = row,
                    Price = 0d,
                });
                return true;
            }

            if (CPlayerData.m_ShopLevel + 1 < row.licenseShopLevelRequired)
            {
                error = "shop level is too low for this product license";
                return false;
            }

            if (!IsFinite(row.licensePrice) || row.licensePrice < 0f)
            {
                error = "product license has an invalid host price";
                return false;
            }

            plan.Lines.Add(new PlannedLine
            {
                Request = request,
                RestockIndex = index,
                Restock = row,
                Price = row.licensePrice,
            });
            plan.Total = row.licensePrice;
            return IsValidTotal(plan.Total, out error);
        }

        private static bool TryBuildScannerLicensePlan(PurchaseIntentMessage message,
            PurchasePlan plan, out string error)
        {
            error = null;
            if (message.Lines.Count != 1 || message.Lines[0] == null
                || message.Lines[0].Count != 1 || message.Lines[0].ItemType != EItemType.None
                || message.Lines[0].ObjectType != EObjectType.None
                || message.Lines[0].IsBigBox || message.Lines[0].Name == null
                || message.Lines[0].Name.Length != 0)
            {
                error = "scanner license purchase must contain one valid item";
                return false;
            }

            if (CPlayerData.m_IsScannerRestockUnlocked)
            {
                plan.IdempotentNoOp = true;
                plan.Lines.Add(new PlannedLine
                {
                    Request = message.Lines[0],
                    Price = 0d,
                });
                return true;
            }

            plan.Lines.Add(new PlannedLine
            {
                Request = message.Lines[0],
                Price = ScannerUnlockPrice,
            });
            plan.Total = ScannerUnlockPrice;
            return true;
        }

        private DeliveryResult Deliver(PurchasePlan plan, string peer)
        {
            var result = new DeliveryResult { Success = true };
            switch (plan.Kind)
            {
                case PurchaseKind.Restock:
                    DeliverRestock(plan, result, peer);
                    break;
                case PurchaseKind.Furniture:
                    DeliverFurniture(plan, result, peer);
                    break;
                case PurchaseKind.ProductLicense:
                    DeliverProductLicense(plan, result, peer);
                    break;
                case PurchaseKind.ScannerLicense:
                    DeliverScannerLicense(plan, result, peer);
                    break;
            }

            return result;
        }

        private void DeliverRestock(PurchasePlan plan, DeliveryResult result, string peer)
        {
            var deliveredAmount = 0d;
            for (var i = 0; i < plan.Lines.Count; i++)
            {
                var line = plan.Lines[i];
                try
                {
                    // This is the vanilla package factory. World owns the resulting physical
                    // boxes through its normal spawn/mirror patches; Purchasing owns no shelf.
                    RestockManager.SpawnPackageBoxItemMultipleFrame(line.RestockIndex,
                        line.Request.Count);
                    deliveredAmount += line.Price;
                    result.RestockBoxes += line.Request.Count;
                    result.RestockItems += line.Request.Count;
                }
                catch (Exception exception)
                {
                    result.Success = false;
                    result.Failure = $"restock delivery failed for '{line.Restock.name}': "
                        + exception.Message;
                    CoopPlugin.Log.LogError($"Purchasing restock delivery failed for {peer}: "
                        + exception);
                    break;
                }
            }

            if (result.RestockBoxes > 0)
            {
                var deliveryFee = ScannerDeliveryFee(result.RestockBoxes);
                deliveredAmount += deliveryFee;
                result.Charged = deliveredAmount;
            }
            else
            {
                result.Charged = 0d;
            }
        }

        private void DeliverFurniture(PurchasePlan plan, DeliveryResult result, string peer)
        {
            var line = plan.Lines[0];
            try
            {
                // The host intentionally chooses the pose. A client never supplies a transform.
                var spawn = PurchasingInterop.RandomPackageSpawn();
                if (spawn == null)
                {
                    throw new InvalidOperationException("no package spawn point is available");
                }

                ShelfManager.SpawnInteractableObjectInPackageBox(line.Request.ObjectType,
                    spawn.position, spawn.rotation);
                result.Charged = line.Price;
            }
            catch (Exception exception)
            {
                result.Success = false;
                result.Failure = "furniture delivery failed: " + exception.Message;
                CoopPlugin.Log.LogError($"Purchasing furniture delivery failed for {peer}: "
                    + exception);
            }
        }

        private void DeliverProductLicense(PurchasePlan plan, DeliveryResult result,
            string peer)
        {
            var line = plan.Lines[0];
            try
            {
                if (!CatalogApi.HostApplyProductLicense(line.Request.ItemType,
                    line.Request.IsBigBox, line.Request.Name ?? "", false, false, out var changed)
                    || !changed)
                {
                    throw new InvalidOperationException("catalog license was not changed");
                }
                result.Charged = line.Price;
                PurchasingInterop.RefreshProductLicensePanels(line.RestockIndex);
            }
            catch (Exception exception)
            {
                result.Success = false;
                result.Failure = "product license delivery failed: " + exception.Message;
                CoopPlugin.Log.LogError($"Purchasing product license failed for {peer}: "
                    + exception);
            }
        }

        private void DeliverScannerLicense(PurchasePlan plan, DeliveryResult result,
            string peer)
        {
            try
            {
                if (!CatalogApi.HostApplyScannerLicense(false, out var changed) || !changed)
                {
                    throw new InvalidOperationException("catalog scanner license was not changed");
                }
                result.Charged = plan.Total;
                CatalogApi.RefreshScannerLicenseUi();
            }
            catch (Exception exception)
            {
                result.Success = false;
                result.Failure = "scanner license delivery failed: " + exception.Message;
                CoopPlugin.Log.LogError($"Purchasing scanner license failed for {peer}: "
                    + exception);
            }
        }

        private void ApplyCommittedSideEffects(PurchasePlan plan, DeliveryResult result)
        {
            switch (plan.Kind)
            {
                case PurchaseKind.Restock:
                    for (var i = 0; i < plan.Lines.Count; i++)
                    {
                        var line = plan.Lines[i];
                        var totalItems = RestockManager.GetMaxItemCountInBox(line.Restock.itemType,
                            line.Restock.isBigBox) * line.Request.Count;
                        var unitPrice = totalItems > 0 ? (float)(line.Price / totalItems) : 0f;
                        CPlayerData.UpdateAverageItemCost(line.Restock.itemType, totalItems, unitPrice);
                        PriceChangeManager.AddTransaction(-(float)line.Price,
                            ETransactionType.Restock, (int)line.Restock.itemType, totalItems);
                    }
                    PriceChangeManager.AddTransaction(-(float)ScannerDeliveryFee(result.RestockBoxes),
                        ETransactionType.RestockDeliveryFee, 0);
                    ApplyRestockSideEffects(result.Charged, result.RestockItems);
                    break;

                case PurchaseKind.Furniture:
                    ApplyFurnitureSideEffects(plan.Lines[0].Furniture, result.Charged);
                    break;

                case PurchaseKind.ProductLicense:
                    ApplyProductLicenseSideEffects(plan.Lines[0], result.Charged);
                    break;

                case PurchaseKind.ScannerLicense:
                    ApplyScannerLicenseSideEffects(result.Charged);
                    break;
            }
        }

        private static void ApplyRestockSideEffects(double amount, int boxes)
        {
            CPlayerData.m_GameReportDataCollect.supplyCost -= (float)amount;
            CPlayerData.m_GameReportDataCollectPermanent.supplyCost -= (float)amount;
            CEventManager.QueueEvent(new CEventPlayer_AddShopExp(boxes * 5));
            TutorialManager.AddTaskValue(ETutorialTaskCondition.RestockItem, boxes);
            SoundManager.PlayAudio("SFX_CustomerBuy", 0.6f);
        }

        private static void ApplyFurnitureSideEffects(FurniturePurchaseData furniture,
            double amount)
        {
            PriceChangeManager.AddTransaction(-(float)amount, ETransactionType.BuyFurniture,
                (int)furniture.objectType);
            CEventManager.QueueEvent(new CEventPlayer_AddShopExp(
                Mathf.Clamp(Mathf.RoundToInt((float)amount / 100f), 5, 100)));
            CPlayerData.m_GameReportDataCollect.upgradeCost -= (float)amount;
            CPlayerData.m_GameReportDataCollectPermanent.upgradeCost -= (float)amount;
            SoundManager.PlayAudio("SFX_CustomerBuy", 0.6f);
        }

        private static void ApplyProductLicenseSideEffects(PlannedLine line, double amount)
        {
            PriceChangeManager.AddTransaction(-(float)amount, ETransactionType.PayRestockLicense,
                line.RestockIndex);
            CPlayerData.m_GameReportDataCollect.upgradeCost -= (float)amount;
            CPlayerData.m_GameReportDataCollectPermanent.upgradeCost -= (float)amount;

            SoundManager.PlayAudio("SFX_CustomerBuy", 0.6f);
        }

        private static void ApplyScannerLicenseSideEffects(double amount)
        {
            CPlayerData.m_GameReportDataCollect.upgradeCost -= (float)amount;
            CPlayerData.m_GameReportDataCollectPermanent.upgradeCost -= (float)amount;
            PriceChangeManager.AddTransaction(-(float)amount, ETransactionType.PayScannerUnlock, 0);
            SoundManager.PlayAudio("SFX_CustomerBuy", 0.6f);
            CEventManager.QueueEvent(new CEventPlayer_ScannerRestockUnlocked());
        }

        private IEnumerator SaveShelfDataAfterPurchase()
        {
            yield return new WaitForSeconds(1f);
            var shelf = PurchasingInterop.FindShelfManager();
            if (shelf == null)
            {
                CoopPlugin.Log.LogWarning("Purchasing could not schedule shelf save: ShelfManager is missing");
                yield break;
            }

            shelf.SaveInteractableObjectData();
        }

        private static PurchaseOutcomeMessage Accepted(PurchaseIntentMessage message, double charged)
            => new PurchaseOutcomeMessage
            {
                PredictionId = message.PredictionId,
                Kind = message.Kind,
                Success = true,
                Text = $"purchase accepted (${charged:F2})",
            };

        private PurchaseOutcomeMessage Failure(PurchaseIntentMessage message, string peer,
            string reason)
        {
            CoopPlugin.Log.LogWarning("Purchasing rejected " + peer + ": " + reason);
            return new PurchaseOutcomeMessage
            {
                Kind = message.Kind,
                Success = false,
                Text = "purchase rejected - " + reason,
            };
        }

        private void SendOutcome(int connectionId, PurchaseOutcomeMessage outcome)
        {
            if (!_shutdown)
                _context.Send(connectionId, outcome);
        }

        private static bool ValidProductLine(PurchaseLine line, int maxCount)
        {
            return line != null && line.ItemType != EItemType.None && line.Count > 0
                && line.Count <= maxCount && line.Name != null && line.Name.Length <= 256
                && line.ObjectType == EObjectType.None;
        }

        private static bool ValidFurnitureLine(PurchaseLine line)
        {
            return line != null && line.ObjectType != EObjectType.None && line.Count == 1
                && line.ItemType == EItemType.None && !line.IsBigBox
                && line.Name != null && line.Name.Length == 0;
        }

        private static bool IsValidTotal(double total, out string error)
        {
            if (!IsFinite(total) || total < 0d || total > float.MaxValue)
            {
                error = "purchase total is invalid";
                return false;
            }

            error = null;
            return true;
        }

        private static bool IsFinite(double value)
            => !double.IsNaN(value) && !double.IsInfinity(value);

        private static double ScannerDeliveryFee(int boxes)
            => boxes <= 0 ? 0d : Mathf.Clamp(10 * boxes / 5, 5, 1000);

        private string PeerLabel(int connectionId)
        {
            var name = _context?.PeerName?.Invoke(connectionId);
            return string.IsNullOrEmpty(name) ? $"conn {connectionId}" : $"{name}/conn {connectionId}";
        }

        [HarmonyPatch(typeof(RestockItemPanelUI), "OnPressPurchaseButton")]
        private static class ProductLicenseGuardPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(RestockItemPanelUI __instance)
            {
                var active = _active;
                if (active == null || active._shutdown || !active._context.InGame())
                {
                    return true;
                }

                var index = PurchasingInterop.PanelIndex(__instance);
                if (index < 0 || !CatalogApi.TryGetLicense(index, out var unlocked)
                    || !unlocked)
                {
                    // An unreadable catalog state must not block a legitimate native purchase.
                    return true;
                }

                // A guest may have unlocked this row while a host panel still displays the old
                // license button. Refresh and suppress only this stale, already-unlocked action;
                // a still-locked row continues through vanilla unchanged.
                PurchasingInterop.RefreshProductLicensePanels(index);
                active._context.SetStatusLine?.Invoke("product license is already unlocked", 5f);
                return false;
            }
        }
    }
}
