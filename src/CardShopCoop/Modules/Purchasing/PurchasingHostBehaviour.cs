using System;
using System.Collections;
using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Catalog;
using CardShopCoop.Modules.Economy;
using CardShopCoop.Modules.Report;
using CardShopCoop.Modules.World;
using CardShopCoop.Net;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;
using CoopBehaviour = CardShopCoop.Runtime.CoopBehaviour;

namespace CardShopCoop.Modules.Purchasing
{
    /// <summary>
    /// Host-side purchasing authority. A request is only a proposal: every identity, quantity,
    /// price, wallet check, spawn position, and vanilla side effect is rebuilt on this machine.
    /// </summary>
    [ServerBehaviour]
    public sealed class PurchasingHostBehaviour : CoopBehaviour
    {
        private const int MaxLines = 512;
        private const int MaxBoxesPerLine = 100;
        private const int MaxTotalBoxes = 2500;
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
            internal readonly List<Guid> RejectedBoxIds = new();
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
            internal readonly BoxNetworkInteraction.DeliveryQueueState
                DeliveryQueue;
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
                Capture(RestockInterop.ItemBoxes);
                Capture(RestockInterop.CardBoxes);
                Capture(RestockInterop.ShelfBoxes);
                DeliveryQueue = WorldHostBehaviour.CaptureHostDeliveryQueueState();
                if (CPlayerData.m_AverageItemCostList != null)
                {
                    AverageCosts.AddRange(CPlayerData.m_AverageItemCostList);
                }


                if (CPlayerData.m_CurrentTotalItemCountList != null)
                {
                    CurrentItemCounts.AddRange(CPlayerData.m_CurrentTotalItemCountList);
                }

                CaptureTransactions();
                Report = CPlayerData.m_GameReportDataCollect;
                PermanentReport = CPlayerData.m_GameReportDataCollectPermanent;
                if (CPlayerData.m_GameReportDataCollectPastList != null)
                {
                    PastReports.AddRange(CPlayerData.m_GameReportDataCollectPastList);
                }


                if (CPlayerData.m_IsAchievementUnlocked != null)
                {
                    Achievements.AddRange(CPlayerData.m_IsAchievementUnlocked);
                }


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
                {
                    PendingRestockIndexes.AddRange(CPlayerData.m_SpawnBoxRestockIndexWaitingList);
                }


                if (CPlayerData.m_SpawnBoxItemCountWaitingList != null)
                {
                    PendingRestockCounts.AddRange(CPlayerData.m_SpawnBoxItemCountWaitingList);
                }


                ScannerUnlocked = CPlayerData.m_IsScannerRestockUnlocked;
                EventCount = QueueCount();
            }

            internal void Rollback()
            {
                DestroyNew(RestockInterop.ItemBoxes);
                DestroyNew(RestockInterop.CardBoxes);
                DestroyNew(RestockInterop.ShelfBoxes);
                RestoreList(CPlayerData.m_AverageItemCostList, AverageCosts);
                RestoreList(CPlayerData.m_CurrentTotalItemCountList, CurrentItemCounts);
                CPlayerData.m_GameReportDataCollect = Report;
                CPlayerData.m_GameReportDataCollectPermanent = PermanentReport;
                RestoreList(CPlayerData.m_GameReportDataCollectPastList, PastReports);
                RestoreList(CPlayerData.m_IsAchievementUnlocked, Achievements);
                RestoreTutorialData();
                CPlayerData.m_TutorialIndex = TutorialIndex;
                if (HasTutorialManager)
                {
                    TutorialFinished.SetValue(TutorialManagerInstance, TutorialWasFinished);
                }


                CPlayerData.m_ShopExpPoint = ShopExpPoint;
                CPlayerData.m_ShopLevel = ShopLevel;
                RestoreTransactions();
                RestoreList(PendingRestockBoxes, SceneRef<RestockManager>.Get(), PendingRestockRows);
                RestoreList(CPlayerData.m_SpawnBoxRestockIndexWaitingList, PendingRestockIndexes);
                RestoreList(CPlayerData.m_SpawnBoxItemCountWaitingList, PendingRestockCounts);
                WorldHostBehaviour.RestoreHostDeliveryQueueState(DeliveryQueue);
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
                {
                    return;
                }


                for (var i = 0; i < objects.Count; i++)
                {
                    if (objects[i] != null)
                    {
                        Objects.Add(objects[i]);
                    }

                }
            }

            private void DestroyNew<T>(IList<T> objects) where T : UnityEngine.Object
            {
                if (objects == null)
                {
                    return;
                }


                for (var i = objects.Count - 1; i >= 0; i--)
                {
                    if (objects[i] != null && !Objects.Contains(objects[i]))
                    {
                        if (objects[i] is InteractablePackagingBox box)
                        {
                            // The game's own teardown removes the box from RestockManager's
                            // packaging-box lists (and forwards the destroy). A raw Destroy left a
                            // dead entry there, and RestockManager.Update threw on it every
                            // out-of-bounds tick, which also disabled its box rescue.
                            box.OnDestroyed();
                        }
                        else
                        {
                            Destroy(objects[i]);
                        }
                    }
                }
            }

            private void CaptureTransactions()
            {
                var transactions = CPlayerData.m_TransactionDataList;
                if (transactions == null)
                {
                    return;
                }


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
                {
                    return;
                }


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
                {
                    CPlayerData.m_TutorialDataList = new List<TutorialData>();
                }


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
                {
                    return;
                }


                for (var i = 0; i < source.Count; i++)
                {
                    target.Add((T)source[i]);
                }

            }

            private static void RestoreList<T>(List<T> target, List<T> source)
            {
                if (target == null || source == null)
                {
                    return;
                }


                target.Clear();
                target.AddRange(source);
            }

            private static void RestoreList<T>(System.Reflection.FieldInfo field, object instance,
                List<T> source)
            {
                var list = field?.GetValue(instance) as IList;
                if (list == null || source == null)
                {
                    return;
                }


                list.Clear();
                for (var i = 0; i < source.Count; i++)
                {
                    list.Add(source[i]);
                }

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
                {
                    return;
                }


                var retained = new List<object>(count);
                while (queue.Count > 0)
                {
                    var item = queue.Dequeue();
                    if (retained.Count < count)
                    {

                        retained.Add(item);
                    }

                }
                for (var i = 0; i < retained.Count; i++)
                {
                    queue.Enqueue(retained[i]);
                }

            }
        }

        private static PurchasingHostBehaviour _active;

        internal static PurchasingHostBehaviour Active => _active;
        private bool _shutdown;

        private void OnEnable()
        {
            if (_shutdown || ReferenceEquals(_active, this))
            {
                return;
            }

            _active = this;
        }

        /// <summary>Runs from the concrete restock intent's <c>OnApply</c> on the host, with the
        /// authenticated sender already filled in by the router.</summary>
        internal PurchaseOutcomeMessage ApplyRestockPurchase(RestockPurchaseIntent intent)
        {
            var lines = intent?.Lines;
            var scannerCheckout = intent?.ScannerCheckout ?? false;
            return ApplyPurchase(intent, PurchaseKind.Restock, lines,
                plan => TryBuildRestockPlan(scannerCheckout, lines, plan));
        }

        internal PurchaseOutcomeMessage ApplyFurniturePurchase(FurniturePurchaseIntent intent)
        {
            var lines = intent?.Lines;
            return ApplyPurchase(intent, PurchaseKind.Furniture, lines,
                plan => TryBuildFurniturePlan(lines, plan));
        }

        internal PurchaseOutcomeMessage ApplyProductLicensePurchase(
            ProductLicensePurchaseIntent intent)
        {
            var lines = intent?.Lines;
            return ApplyPurchase(intent, PurchaseKind.ProductLicense, lines,
                plan => TryBuildProductLicensePlan(lines, plan));
        }

        internal PurchaseOutcomeMessage ApplyScannerLicensePurchase(
            ScannerLicensePurchaseIntent intent)
        {
            var lines = intent?.Lines;
            return ApplyPurchase(intent, PurchaseKind.ScannerLicense, lines,
                plan => TryBuildScannerLicensePlan(lines, plan));
        }

        /// <summary>Shared admission for every concrete purchase intent: one authenticated sender,
        /// one line-count bound, and a kind-specific plan builder. Everything after the plan is
        /// built is the same for all purchase kinds.</summary>
        private PurchaseOutcomeMessage ApplyPurchase(ActionMessage intent, PurchaseKind kind,
            List<PurchaseLine> lines, Func<PurchasePlan, string> build)
        {
            if (_shutdown || !RuntimeContext.InGame() || intent == null)
            {
                return null;
            }

            var connectionId = intent.Connection?.Id ?? intent.Sender?.Id ?? 0;
            if (connectionId <= 0)
            {
                CoopPlugin.Log.LogWarning("Purchasing dropped a shipment without an authenticated "
                    + "sender.");
                return null;
            }

            return Process(intent, kind, lines, build, PeerLabel(connectionId));
        }

        [MessageHandler]
        private ActionOutcome HandleRestockPurchase(MessageContext context,
            RestockPurchaseIntent message)
        {
            if (message == null)
            {
                return ActionOutcome.Reject("purchase command is missing");
            }

            message.OnApply();
            return PublishIntentResult(context, message.Result);
        }

        [MessageHandler]
        private ActionOutcome HandleFurniturePurchase(MessageContext context,
            FurniturePurchaseIntent message)
        {
            if (message == null)
            {
                return ActionOutcome.Reject("purchase command is missing");
            }

            message.OnApply();
            return PublishIntentResult(context, message.Result);
        }

        [MessageHandler]
        private ActionOutcome HandleProductLicensePurchase(MessageContext context,
            ProductLicensePurchaseIntent message)
        {
            if (message == null)
            {
                return ActionOutcome.Reject("purchase command is missing");
            }

            message.OnApply();
            return PublishIntentResult(context, message.Result);
        }

        [MessageHandler]
        private ActionOutcome HandleScannerLicensePurchase(MessageContext context,
            ScannerLicensePurchaseIntent message)
        {
            if (message == null)
            {
                return ActionOutcome.Reject("purchase command is missing");
            }

            message.OnApply();
            return PublishIntentResult(context, message.Result);
        }

        /// <summary>The intent has already run <c>OnApply</c> and stored the host's answer.
        /// Publish an acceptance, or let the framework's one generic rollback reverse the
        /// client's optimistic checkout on a rejection.</summary>
        private ActionOutcome PublishIntentResult(MessageContext context,
            PurchaseOutcomeMessage result)
        {
            if (_shutdown || result == null || context?.Connection == null)
            {
                return ActionOutcome.Reject("purchasing is not ready on the host");
            }

            if (!result.Success)
            {
                return ActionOutcome.Reject(result.Text);
            }

            SendOutcome(context.Connection.Id, result);
            return ActionOutcome.Applied();
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

        }

        private void OnDestroy() => Shutdown();

        private PurchaseOutcomeMessage Process(ActionMessage intent, PurchaseKind kind,
            List<PurchaseLine> lines, Func<PurchasePlan, string> build, string peer)
        {
            EconomyAuthority.HostSpendReservation spend = null;
            MutationSnapshot mutation = null;
            PurchasePlan plan = null;
            // The amount of a debit that is committed but whose local work has not finished. A
            // failure while it is non-zero refunds it, so a failed delivery can never charge the
            // buyer for nothing.
            var committedDebit = 0d;
            try
            {
                if (lines == null || lines.Count == 0 || lines.Count > MaxLines)
                {
                    return Failure(kind, peer, "purchase has an invalid line count");
                }

                plan = new PurchasePlan
                {
                    Kind = kind,
                };
                var error = build(plan);
                if (error != null)
                {
                    return Failure(kind, peer, error);
                }

                if (plan.IdempotentNoOp)
                {
                    return Accepted(intent, kind, 0d);
                }

                if (!EconomyAuthority.TryReserveHostSpend(plan.Total, out spend))
                {
                    return Failure(kind, peer,
                        "not enough money - the purchase was cancelled");
                }

                // Admit the authoritative debit before any factory, entitlement, report,
                // transaction, or save side effect. The debit is not undone by a later network
                // result; only local game mutations are compensated below.
                if (!EconomyAuthority.QueueHostSpend(spend))
                {
                    return Failure(kind, peer,
                        "purchase failed while admitting the debit");
                }

                spend = null;
                committedDebit = plan.Total;

                mutation = new MutationSnapshot(plan);

                DeliveryResult delivery;
                try
                {
                    delivery = Deliver(plan, peer);
                }
                catch (Exception exception)
                {
                    mutation.Rollback();
                    RefundFailedPurchase(ref committedDebit);
                    CoopPlugin.Log.LogError($"Purchasing delivery crashed for {peer}: {exception}");
                    return Failure(kind, peer,
                        "purchase failed while delivering the order");
                }

                if (!delivery.Success)
                {
                    mutation.Rollback();
                    RefundFailedPurchase(ref committedDebit);
                    return Failure(kind, peer,
                        delivery.Failure ?? "purchase delivery failed");
                }

                // Delivery recomputes the canonical amount again. In the normal path this is
                // exactly the preflight total, but keep the shared reservation honest if a game
                // factory changes a price between those two operations.
                if (Math.Abs(delivery.Charged - plan.Total) > 0.0001d)
                {
                    mutation.Rollback();
                    RefundFailedPurchase(ref committedDebit);
                    CoopPlugin.Log.LogError($"Purchasing canonical amount changed for "
                        + $"{peer}: "
                        + $"preflight={plan.Total}, delivered={delivery.Charged}");
                    return Failure(kind, peer,
                        "purchase failed while charging the order");
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

                var outcome = Accepted(intent, kind, delivery.Charged,
                    delivery.RejectedBoxIds);
                committedDebit = 0d;
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
                if (plan != null)
                {
                    RefundFailedPurchase(ref committedDebit);
                }
                CoopPlugin.Log.LogError($"Purchasing preflight crashed for {peer}: {exception}");
                return Failure(kind, peer, "purchase could not be validated");
            }
        }

        /// <summary>A purchase failed after its debit was committed: give the money back through
        /// the authority's compensating wallet event, once. The debit stays authoritative for the
        /// successful path; this only compensates the host's own local failure.</summary>
        private static void RefundFailedPurchase(ref double committedDebit)
        {
            if (committedDebit > 0d)
            {
                EconomyAuthority.RefundHostSpend(committedDebit);
                committedDebit = 0d;
            }
        }

        /// <summary>Builds a purchase plan from the shipped lines. Returns the rejection reason, or
        /// null when the plan is valid and priced. The concrete intent already fixes the kind, so
        /// there is no incoming-kind dispatch left here.</summary>
        private static string TryBuildRestockPlan(bool scannerCheckout, List<PurchaseLine> lines,
            PurchasePlan plan)
        {
            if (scannerCheckout && !CPlayerData.m_IsScannerRestockUnlocked)
            {
                return "scanner restock is locked on the host";
            }

            var totalBoxes = 0;
            for (var i = 0; i < lines.Count; i++)
            {
                var request = lines[i];
                if (!ValidProductLine(request, MaxBoxesPerLine))
                {
                    return $"restock line {i} is invalid";
                }

                if (!CatalogApi.TryResolveProduct(request.ItemType, request.IsBigBox,
                    request.Name ?? "", out var index, out var row) || row == null)
                {
                    return $"restock line {i} is not in the host catalog";
                }

                if (!CatalogApi.TryGetLicense(index, out var licensed) || !licensed)
                {
                    return $"restock line {i} is not licensed on the host";
                }

                var maxCount = RestockManager.GetMaxItemCountInBox(row.itemType, row.isBigBox);
                var itemCost = CPlayerData.GetItemCost(row.itemType);
                var linePrice = (double)itemCost * maxCount * request.Count;
                if (maxCount <= 0 || !IsFinite(itemCost) || itemCost < 0f
                    || !IsFinite(linePrice))
                {
                    return $"restock line {i} has an invalid host price";
                }

                totalBoxes += request.Count;
                if (totalBoxes > MaxTotalBoxes)
                {
                    return "restock order is too large";
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

            return IsValidTotal(plan.Total, out var error) ? null : error;
        }

        private static string TryBuildFurniturePlan(List<PurchaseLine> lines, PurchasePlan plan)
        {
            if (lines.Count != 1 || !ValidFurnitureLine(lines[0]))
            {
                return "furniture purchase must contain one valid item";
            }

            var request = lines[0];
            var furniture = InventoryBase.GetFurniturePurchaseData(request.ObjectType);
            if (furniture == null || InventoryBase.GetSpawnInteractableObjectPrefab(
                request.ObjectType) == null)
            {
                return "furniture is not available in the host catalog";
            }

            if (!PurchasingInterop.IsFurnitureAvailable(request.ObjectType))
            {
                return "furniture is not available for this platform entitlement";
            }

            if (CPlayerData.m_ShopLevel + 1 < furniture.levelRequirement)
            {
                return "shop level is too low for this furniture";
            }

            if (!IsFinite(furniture.price) || furniture.price <= 0f)
            {
                return "furniture has an invalid host price";
            }

            plan.Lines.Add(new PlannedLine
            {
                Request = request,
                Furniture = furniture,
                Price = furniture.price,
            });
            plan.Total = furniture.price;
            return IsValidTotal(plan.Total, out var error) ? null : error;
        }

        private static string TryBuildProductLicensePlan(List<PurchaseLine> lines,
            PurchasePlan plan)
        {
            if (lines.Count != 1 || !ValidProductLine(lines[0], 1))
            {
                return "product license purchase must contain one valid item";
            }

            var request = lines[0];
            if (!CatalogApi.TryResolveProduct(request.ItemType, request.IsBigBox,
                request.Name ?? "", out var index, out var row) || row == null)
            {
                return "product license is not in the host catalog";
            }

            if (!CatalogApi.TryGetLicense(index, out var alreadyUnlocked))
            {
                return "product license state is unavailable on the host";
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
                return null;
            }

            if (CPlayerData.m_ShopLevel + 1 < row.licenseShopLevelRequired)
            {
                return "shop level is too low for this product license";
            }

            if (!IsFinite(row.licensePrice) || row.licensePrice < 0f)
            {
                return "product license has an invalid host price";
            }

            plan.Lines.Add(new PlannedLine
            {
                Request = request,
                RestockIndex = index,
                Restock = row,
                Price = row.licensePrice,
            });
            plan.Total = row.licensePrice;
            return IsValidTotal(plan.Total, out var error) ? null : error;
        }

        private static string TryBuildScannerLicensePlan(List<PurchaseLine> lines,
            PurchasePlan plan)
        {
            if (lines.Count != 1 || lines[0] == null
                || lines[0].Count != 1 || lines[0].ItemType != EItemType.None
                || lines[0].ObjectType != EObjectType.None
                || lines[0].IsBigBox || lines[0].Name == null
                || lines[0].Name.Length != 0)
            {
                return "scanner license purchase must contain one valid item";
            }

            if (CPlayerData.m_IsScannerRestockUnlocked)
            {
                plan.IdempotentNoOp = true;
                plan.Lines.Add(new PlannedLine
                {
                    Request = lines[0],
                    Price = 0d,
                });
                return null;
            }

            plan.Lines.Add(new PlannedLine
            {
                Request = lines[0],
                Price = ScannerUnlockPrice,
            });
            plan.Total = ScannerUnlockPrice;
            return null;
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
                    // The shipment carried the ids this peer minted for its boxes. Stage them for
                    // the game's deliver entry; the enqueue hook commits them to that entry so the
                    // host binds each spawned box to the same id. Staging is all-or-nothing: a
                    // non-fresh id means the client already bound a live box to it, so the order is
                    // rejected rather than reminted and desynced.
                    if (!WorldHostBehaviour.StageCreatedBoxIds(line.Request.BoxNetworkIds))
                    {
                        throw new InvalidOperationException(
                            "delivery box ids are not fresh; rejecting the order");
                    }

                    CoopPlugin.Log.LogInfo("[purchasing] delivering restock '" + line.Restock.name
                        + "' index=" + line.RestockIndex + " boxes=" + line.Request.Count
                        + " ids=" + (line.Request.BoxNetworkIds?.Count ?? 0)
                        + "; the game's delivery queue spawns them over its next frames.");

                    RestockManager.SpawnPackageBoxItemMultipleFrame(line.RestockIndex,
                        line.Request.Count);
                    deliveredAmount += line.Price;
                    result.RestockBoxes += line.Request.Count;
                    result.RestockItems += line.Request.Count;
                }
                catch (Exception exception)
                {
                    // The enqueue hook consumes the staged ids on success; a throw before it leaves
                    // them staged for the next unrelated enqueue, so drop them here.
                    WorldHostBehaviour.CancelStagedDeliveryIds();
                    // The host did not create this line's boxes; tell the minting client exactly
                    // which ones to destroy. Other lines still deliver (partial approval).
                    var rejected = line.Request.BoxNetworkIds;
                    if (rejected != null)
                    {
                        result.RejectedBoxIds.AddRange(rejected);
                    }

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
            var creatorId = line.Request.BoxNetworkIds != null
                && line.Request.BoxNetworkIds.Count > 0
                    ? line.Request.BoxNetworkIds[0]
                    : Guid.Empty;
            try
            {
                // The shipment carried the furniture Guid this peer minted. The host's spawned
                // furniture is bound to that exact id (the package is a view), so the buyer's
                // already-spawned furniture is adopted instead of duplicated.
                if (!CardShopCoop.Modules.GameObjectIdentity.PlacementObjectIdentity
                    .PushHostFurnitureId(creatorId))
                {
                    throw new InvalidOperationException(
                        "furniture id is not fresh; rejecting the order");
                }

                // The host intentionally chooses the pose. A client never supplies a transform,
                // and the identity module binds/announces the furniture it spawns here.
                var spawn = PurchasingInterop.RandomPackageSpawn();
                if (spawn == null)
                {
                    throw new InvalidOperationException("no package spawn point is available");
                }

                ShelfManager.SpawnInteractableObjectInPackageBox(line.Request.ObjectType,
                    spawn.position, spawn.rotation);
                // The spawn consumed the one-shot. If the game produced no placement object,
                // release it so it cannot bind an unrelated creation later.
                CardShopCoop.Modules.GameObjectIdentity.PlacementObjectIdentity
                    .CancelHostFurnitureId(creatorId);
                result.Charged = line.Price;
            }
            catch (Exception exception)
            {
                // A throw after the id was parked but before the game consumed it would strand it
                // on a later, unrelated creation. No-op once consumed.
                CardShopCoop.Modules.GameObjectIdentity.PlacementObjectIdentity
                    .CancelHostFurnitureId(creatorId);
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

        private static PurchaseOutcomeMessage Accepted(ActionMessage intent, PurchaseKind kind,
            double charged)
            => Accepted(intent, kind, charged, null);

        private static PurchaseOutcomeMessage Accepted(ActionMessage intent, PurchaseKind kind,
            double charged, IReadOnlyList<Guid> rejectedBoxIds)
            => new PurchaseOutcomeMessage
            {
                ActionId = intent.ActionId,
                Kind = kind,
                Success = true,
                Text = $"purchase accepted (${charged:F2})",
                RejectedBoxIds = rejectedBoxIds == null
                    ? new List<Guid>()
                    : new List<Guid>(rejectedBoxIds),
            };

        private PurchaseOutcomeMessage Failure(PurchaseKind kind, string peer, string reason)
        {
            CoopPlugin.Log.LogWarning("Purchasing rejected " + peer + ": " + reason);
            return new PurchaseOutcomeMessage
            {
                Kind = kind,
                Success = false,
                Text = "purchase rejected - " + reason,
            };
        }

        private void SendOutcome(int connectionId, PurchaseOutcomeMessage outcome)
        {
            if (!_shutdown)
            {
                RuntimeContext.Send(connectionId, outcome);
            }

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
            var name = RuntimeContext.PeerName?.Invoke(connectionId);
            return string.IsNullOrEmpty(name) ? $"conn {connectionId}" : $"{name}/conn {connectionId}";
        }
    }
}
