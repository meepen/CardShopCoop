using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Economy;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Staff
{
    /// <summary>Host validation and keyed worker operation publication.</summary>
    [ServerBehaviour]
    public sealed class StaffHostBehaviour : CoopBehaviour
    {
        private sealed class WorkerIdentity
        {
            public Worker Worker;
            public uint Generation;
        }

        private static StaffHostBehaviour _active;
        private readonly Dictionary<int, int> _leaseOwner = new();
        private readonly Dictionary<int, WorkerIdentity> _identities = new();
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
        private Guid _intentPredictionId;

        private void OnEnable()
        {
            if (_shutdown || _context != null)
            {
                return;
            }

            _context = RuntimeContext;
            _context.Messages.RegisterAttributedHandlers(this);
            _active = this;
            _harmony = new Harmony("com.zwhit.cardshopcoop.staff.module.host");
            Patch(typeof(WorkerMousePatch));
            Patch(typeof(WorkerStopPatch));
            Patch(typeof(WorkerLifecyclePatch));
            Patch(typeof(WorkerTaskPatch));
            Patch(typeof(WorkerOptionsPatch));
            Patch(typeof(WorkerPackPatch));
            Patch(typeof(WorkerBonusPatch));
            Patch(typeof(WorkerExperiencePatch));
            Patch(typeof(WorkerFirePatch));
            Patch(typeof(WorkerManagerStartPatch));
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void Patch(Type patchType)
            => _harmony.CreateClassProcessor(patchType).Patch();

        [OnFullyJoined]
        private void SendJoinBaseline(PeerConnection connection)
        {
            if (connection != null && IsJoinPhase(connection.State) && _context.InGame()
                && GetWorkerManager()?.m_WorkerDataList != null)
            {
                _context.Send(connection.Id, BuildBaseline());
            }
        }

        private static bool IsJoinPhase(ConnectionState state)
            => state == ConnectionState.Transferring || state == ConnectionState.FullyJoined;

        [OnClientDisconnected]
        private void ForgetConnection(PeerConnection connection, DisconnectInfo _)
        {
            if (connection != null)
            {
                HostReleaseConnection(connection.Id);
            }
        }

        [MessageHandler(typeof(StaffModuleIntentMessage))]
        private void HandleIntent(MessageContext context, StaffModuleIntentMessage message)
        {
            if (!IsValidSender(context, message))
            {
                return;
            }

            var peerId = context.Connection.Id;
            _intentPredictionId = message.PredictionId;
            var accepted = false;
            try
            {
                switch (message.Kind)
                {
                    case StaffIntentKind.Hire:
                        accepted = HostHire(message.Index);
                        break;
                    case StaffIntentKind.Task:
                    case StaffIntentKind.Options:
                    case StaffIntentKind.Pack:
                        accepted = HostUpdate(message, peerId);
                        break;
                    case StaffIntentKind.Bonus:
                        accepted = HostBonus(message.Index, peerId);
                        break;
                    case StaffIntentKind.Fire:
                        accepted = HostFire(message.Index, peerId);
                        break;
                    case StaffIntentKind.BeginInteraction:
                        accepted = HostBeginInteraction(message.Index, peerId, message.Position);
                        break;
                    case StaffIntentKind.EndInteraction:
                        accepted = HostEndInteraction(message.Index, peerId);
                        break;
                }
            }
            finally
            {
                _intentPredictionId = Guid.Empty;
            }

            if (!accepted)
            {
                Reject(peerId, message.PredictionId);
            }
        }

        private bool IsValidSender(MessageContext context, StaffModuleIntentMessage message)
            => !_shutdown && _context.InGame() && context?.Connection != null
                && IsJoinPhase(context.Connection.State) && message != null;

        private StaffModuleBaselineMessage BuildBaseline()
        {
            var manager = GetWorkerManager();
            var baseline = new StaffModuleBaselineMessage();
            var entries = StaffModuleInterop.CaptureAll(manager);
            for (var i = 0; i < entries.Count; i++)
            {
                entries[i] = WithGeneration(i, entries[i]);
            }

            baseline.Entries = entries;
            return baseline;
        }

        private StaffModuleEntry WithGeneration(int index, StaffModuleEntry entry)
        {
            entry.Generation = EnsureGeneration(index);
            return entry;
        }

        private uint EnsureGeneration(int index)
        {
            if (!StaffModuleInterop.TryGetWorker(GetWorkerManager(), index, out var worker))
            {
                return _identities.TryGetValue(index, out var missing) ? missing.Generation : 1u;
            }

            if (!_identities.TryGetValue(index, out var identity)
                || !ReferenceEquals(identity.Worker, worker))
            {
                var generation = identity == null ? 1u : identity.Generation + 1;
                if (generation == 0)
                {
                    generation = 1;
                }

                identity = new WorkerIdentity { Worker = worker, Generation = generation };
                _identities[index] = identity;
            }

            return identity.Generation;
        }

        private WorkerManager GetWorkerManager()
            => SceneRef<WorkerManager>.Get();

        private bool HostHire(int index)
        {
            var manager = GetWorkerManager();
            if (!StaffModuleInterop.IsAddressable(manager, index)
                || CPlayerData.m_IsWorkerHired == null || index >= CPlayerData.m_IsWorkerHired.Count
                || CPlayerData.GetIsWorkerHired(index))
            {
                return false;
            }

            var workerData = manager.m_WorkerDataList[index];
            if (workerData == null || CPlayerData.m_ShopLevel + 1 < workerData.shopLevelRequired)
            {
                return false;
            }

            var gameManager = SceneRef<CGameManager>.Get();
            if (gameManager != null && gameManager.m_IsPrologue && !workerData.prologueShow)
            {
                return false;
            }

            if (!TrySpend(workerData.hiringCost, ETransactionType.HireWorker, index))
            {
                return false;
            }

            CPlayerData.SetIsWorkerHired(index, true);
            manager.ActivateWorker(index, resetTask: true);
            CPlayerData.m_GameReportDataCollect.employeeCost -= workerData.hiringCost;
            CPlayerData.m_GameReportDataCollectPermanent.employeeCost -= workerData.hiringCost;
            var hiredCount = 0;
            for (var i = 0; i < CPlayerData.m_IsWorkerHired.Count; i++)
            {
                if (CPlayerData.m_IsWorkerHired[i])
                {
                    hiredCount++;
                }
            }

            AchievementManager.OnStaffHired(hiredCount);
            SoundManager.PlayAudio("SFX_CustomerBuy", 0.6f);
            return true;
        }

        private bool HostUpdate(StaffModuleIntentMessage message, int connectionId)
        {
            if (!_leaseOwner.TryGetValue(message.Index, out var owner) || owner != connectionId)
            {
                return false;
            }

            var manager = GetWorkerManager();
            if (!StaffModuleInterop.IsValidWorker(manager, message.Index))
            {
                return false;
            }

            if (message.Kind == StaffIntentKind.Task || message.Kind == StaffIntentKind.Options
                || message.Kind == StaffIntentKind.Pack)
            {
                if (!AreTasksValid(message))
                {
                    return false;
                }
            }

            if (message.Kind == StaffIntentKind.Task)
            {
                var accepted = ApplyTask(message);
                if (accepted)
                {
                    PublishTask(StaffModuleInterop.GetWorkers(manager)[message.Index], true);
                    HostEndInteraction(message.Index, connectionId);
                }

                return accepted;
            }

            if (message.Kind == StaffIntentKind.Options)
            {
                if (!TryApplyOptions(message))
                {
                    return false;
                }

                var accepted = ApplyTask(message);
                if (accepted)
                {
                    PublishOptions(StaffModuleInterop.GetWorkers(manager)[message.Index], true);
                    HostEndInteraction(message.Index, connectionId);
                }

                return accepted;
            }

            if (message.Kind == StaffIntentKind.Pack)
            {
                var worker = StaffModuleInterop.GetWorkers(manager)[message.Index];
                var enabled = worker.GetCardPackItemTypeEnabledList();
                if (message.PackChanges == null || message.PackChanges.Count > 64 || enabled == null)
                {
                    return false;
                }

                for (var i = 0; i < message.PackChanges.Count; i++)
                {
                    var change = message.PackChanges[i];
                    if (change == null || change.Index < 0 || change.Index >= enabled.Count)
                    {
                        return false;
                    }
                }

                for (var i = 0; i < message.PackChanges.Count; i++)
                {
                    var change = message.PackChanges[i];
                    worker.SetCardPackItemTypeEnabled(change.Index, change.Enabled);
                }

                var accepted = ApplyTask(message);
                if (accepted)
                {
                    PublishPackBatch(StaffModuleInterop.GetWorkers(manager)[message.Index],
                        message.PackChanges, true);
                    HostEndInteraction(message.Index, connectionId);
                }

                return accepted;
            }

            return false;
        }

        private bool ApplyTask(StaffModuleIntentMessage message)
        {
            if (!AreTasksValid(message))
            {
                return false;
            }

            var worker = StaffModuleInterop.GetWorkers(GetWorkerManager())[message.Index];
            worker.SetTask((EWorkerTask)message.PrimaryTask);
            worker.SetLastTask((EWorkerTask)message.WorkerTask);
            worker.SetSecondaryTask((EWorkerTask)message.SecondaryTask);
            return true;
        }

        private static bool AreTasksValid(StaffModuleIntentMessage message)
            => StaffModuleInterop.IsValidIntentTask(message.PrimaryTask)
                && StaffModuleInterop.IsValidIntentTask(message.SecondaryTask)
                && StaffModuleInterop.IsValidIntentTask(message.WorkerTask);

        private bool TryApplyOptions(StaffModuleIntentMessage message)
        {
            if (!TryGetFinite(message.PriceMult, out var priceMult)
                || !TryGetFinite(message.CardPriceMult, out var cardPriceMult)
                || priceMult < 0f || priceMult > 10f || cardPriceMult < 0f || cardPriceMult > 10f)
            {
                return false;
            }

            var worker = StaffModuleInterop.GetWorkers(GetWorkerManager())[message.Index];
            worker.SetRestockShelfWithNoLabel(message.FillNoLabel);
            worker.UpdateSetPriceOption(message.RoundUpPrice, message.AvoidSetCardPrice,
                Mathf.Clamp(priceMult, 0f, 10f));
            worker.UpdateSetCardPriceOption(message.RoundUpCardPrice, message.AvoidSetCardPriceRestock,
                Mathf.Clamp(cardPriceMult, 0f, 10f));
            return true;
        }

        private bool HostBonus(int index, int connectionId)
        {
            var manager = GetWorkerManager();
            if (!StaffModuleInterop.IsValidWorker(manager, index)
                || !_leaseOwner.TryGetValue(index, out var owner) || owner != connectionId)
            {
                return false;
            }

            var worker = StaffModuleInterop.GetWorkers(manager)[index];
            if (worker.GetBonusBoostedCount() >= 3 || worker.GetWorkerData() == null)
            {
                return false;
            }

            var fee = worker.GetWorkerData().costPerDay;
            if (!TrySpend(fee, ETransactionType.WorkerSalary, 0))
            {
                return false;
            }

            worker.GiveSalaryBonus();
            CPlayerData.m_GameReportDataCollect.employeeCost -= fee;
            CPlayerData.m_GameReportDataCollectPermanent.employeeCost -= fee;
            return true;
        }

        private bool HostFire(int index, int connectionId)
        {
            var manager = GetWorkerManager();
            if (!StaffModuleInterop.IsValidWorker(manager, index)
                || !_leaseOwner.TryGetValue(index, out var owner) || owner != connectionId)
            {
                return false;
            }

            StaffModuleInterop.GetWorkers(manager)[index].FireWorker();
            HostEndInteraction(index, connectionId);
            return true;
        }

        private bool TrySpend(float amount, ETransactionType transactionType, int transactionIndex)
        {
            if (float.IsNaN(amount) || float.IsInfinity(amount) || amount < 0f
                || CPlayerData.m_CoinAmountDouble < amount)
            {
                return false;
            }

            if (!EconomyAuthority.TryReserveHostSpend(amount, out var spend))
            {
                return false;
            }

            try
            {
                if (!EconomyAuthority.QueueHostSpend(spend))
                {
                    spend.Dispose();
                    return false;
                }

                PriceChangeManager.AddTransaction(0f - amount, transactionType, transactionIndex);
                return true;
            }
            catch
            {
                spend.Dispose();
                throw;
            }
        }

        private bool HostBeginInteraction(int index, int connectionId, Vector3 playerPosition)
        {
            var manager = GetWorkerManager();
            if (!StaffModuleInterop.IsValidWorker(manager, index)
                || !StaffModuleInterop.IsFinite(playerPosition))
            {
                return false;
            }

            if (_leaseOwner.TryGetValue(index, out var owner))
            {
                return owner == connectionId;
            }

            var worker = StaffModuleInterop.GetWorkers(manager)[index];
            if (connectionId != 0)
            {
                StaffModuleInterop.AimWorkerAtPlayer(worker, playerPosition);
            }

            worker.m_IsPausingAction = true;
            _leaseOwner[index] = connectionId;
            PublishInteraction(index, true, connectionId);
            return true;
        }

        private bool HostEndInteraction(int index, int connectionId)
        {
            if (!_leaseOwner.TryGetValue(index, out var owner) || owner != connectionId)
            {
                return false;
            }

            if (StaffModuleInterop.TryGetWorker(GetWorkerManager(), index, out var worker))
            {
                worker.m_IsPausingAction = false;
            }

            _leaseOwner.Remove(index);
            PublishInteraction(index, false, 0);
            return true;
        }

        private void HostReleaseConnection(int connectionId)
        {
            var release = new List<int>();
            foreach (var pair in _leaseOwner)
            {
                if (pair.Value == connectionId)
                {
                    release.Add(pair.Key);
                }
            }

            for (var i = 0; i < release.Count; i++)
            {
                HostEndInteraction(release[i], connectionId);
            }
        }

        private void PublishInteraction(int index, bool occupied, int grantedPeer)
        {
            if (!_shutdown && _context.InGame())
            {
                _context.Broadcast(new StaffModuleDeltaMessage
                {
                    PredictionId = _intentPredictionId,
                    Kind = StaffDeltaKind.Interaction,
                    Index = index,
                    Generation = EnsureGeneration(index),
                    Granted = grantedPeer > 0,
                    Occupied = occupied,
                });
            }
        }

        private void PublishWorkerBootstrap(int index)
        {
            if (_shutdown || !_context.InGame() || !StaffModuleInterop.IsAddressable(GetWorkerManager(), index))
            {
                return;
            }

            var entry = WithGeneration(index, StaffModuleInterop.CaptureEntry(GetWorkerManager(), index));
            Broadcast(new StaffModuleDeltaMessage
            {
                PredictionId = _intentPredictionId,
                Kind = entry.Hired ? StaffDeltaKind.Hired : StaffDeltaKind.Fired,
                Index = index,
                Generation = entry.Generation,
                Hired = entry.Hired,
                Bootstrap = entry.Hired ? entry : default,
            });
        }

        private void PublishTask(Worker worker, bool force = false)
        {
            var index = worker?.m_WorkerIndex ?? -1;
            if (index < 0 || (!force && _intentPredictionId != Guid.Empty))
            {
                return;
            }

            var entry = StaffModuleInterop.CaptureWorkerEntry(worker, index);
            Broadcast(new StaffModuleDeltaMessage
            {
                PredictionId = _intentPredictionId,
                Kind = StaffDeltaKind.Task,
                Index = index,
                Generation = EnsureGeneration(index),
                PrimaryTask = entry.PrimaryTask,
                SecondaryTask = entry.SecondaryTask,
                WorkerTask = entry.WorkerTask,
                CurrentState = entry.CurrentState,
                GoingHome = entry.GoingHome,
            });
        }

        private void PublishOptions(Worker worker, bool force = false)
        {
            var index = worker?.m_WorkerIndex ?? -1;
            if (index < 0 || (!force && _intentPredictionId != Guid.Empty))
            {
                return;
            }

            var entry = StaffModuleInterop.CaptureWorkerEntry(worker, index);
            Broadcast(new StaffModuleDeltaMessage
            {
                PredictionId = _intentPredictionId,
                Kind = StaffDeltaKind.Options,
                Index = index,
                Generation = EnsureGeneration(index),
                PrimaryTask = entry.PrimaryTask,
                SecondaryTask = entry.SecondaryTask,
                WorkerTask = entry.WorkerTask,
                CurrentState = entry.CurrentState,
                GoingHome = entry.GoingHome,
                FillNoLabel = entry.FillNoLabel,
                RoundUpPrice = entry.RoundUpPrice,
                RoundUpCardPrice = entry.RoundUpCardPrice,
                AvoidSetCardPrice = entry.AvoidSetCardPrice,
                AvoidSetCardPriceRestock = entry.AvoidSetCardPriceRestock,
                PriceMult = entry.PriceMult,
                CardPriceMult = entry.CardPriceMult,
            });
        }

        private void PublishPack(Worker worker, int packIndex, bool enabled, bool force = false)
        {
            var index = worker?.m_WorkerIndex ?? -1;
            if (index >= 0 && (force || _intentPredictionId == Guid.Empty))
            {
                var entry = StaffModuleInterop.CaptureWorkerEntry(worker, index);
                Broadcast(new StaffModuleDeltaMessage
                {
                    PredictionId = _intentPredictionId,
                    Kind = StaffDeltaKind.Pack,
                    Index = index,
                    Generation = EnsureGeneration(index),
                    PackIndex = packIndex,
                    PackEnabled = enabled,
                    PrimaryTask = entry.PrimaryTask,
                    SecondaryTask = entry.SecondaryTask,
                    WorkerTask = entry.WorkerTask,
                    CurrentState = entry.CurrentState,
                    GoingHome = entry.GoingHome,
                });
            }
        }

        private void PublishPackBatch(Worker worker, List<StaffPackChange> changes, bool force)
        {
            var index = worker?.m_WorkerIndex ?? -1;
            if (index < 0 || (!force && _intentPredictionId != Guid.Empty))
            {
                return;
            }

            var enabled = worker.GetCardPackItemTypeEnabledList();
            for (var i = 0; changes != null && i < changes.Count; i++)
            {
                var change = changes[i];
                if (change == null || enabled == null || change.Index < 0 || change.Index >= enabled.Count)
                {
                    continue;
                }

                // A batch is split into one absolute result per pack. This lets a client retain
                // every independently keyed pack result while its worker is still being built.
                PublishPack(worker, change.Index, enabled[change.Index], force);
            }
        }

        private void PublishBonus(Worker worker)
        {
            var index = worker?.m_WorkerIndex ?? -1;
            if (index >= 0)
            {
                Broadcast(new StaffModuleDeltaMessage
                {
                    PredictionId = _intentPredictionId,
                    Kind = StaffDeltaKind.Bonus,
                    Index = index,
                    Generation = EnsureGeneration(index),
                    BonusCount = (byte)Mathf.Clamp(worker.GetBonusBoostedCount(), 0, byte.MaxValue),
                    BonusBoosted = worker.m_IsBonusBoosted,
                });
            }
        }

        private void PublishExperience(Worker worker, EWorkerTask task)
        {
            var index = worker?.m_WorkerIndex ?? -1;
            if (index >= 0)
            {
                var entry = StaffModuleInterop.CaptureWorkerEntry(worker, index);
                var taskIndex = (int)task;
                if (entry.ExpList == null || taskIndex < 0 || taskIndex >= entry.ExpList.Count)
                {
                    return;
                }

                Broadcast(new StaffModuleDeltaMessage
                {
                    PredictionId = _intentPredictionId,
                    Kind = StaffDeltaKind.Experience,
                    Index = index,
                    Generation = EnsureGeneration(index),
                    ExperienceTask = (byte)task,
                    // ExperienceAmount is the resulting absolute value, not an increment.
                    ExperienceAmount = entry.ExpList[taskIndex],
                });
            }
        }

        private void PublishFired(Worker worker)
        {
            var index = worker?.m_WorkerIndex ?? -1;
            if (index >= 0)
            {
                Broadcast(new StaffModuleDeltaMessage
                {
                    PredictionId = _intentPredictionId,
                    Kind = StaffDeltaKind.Fired,
                    Index = index,
                    Generation = EnsureGeneration(index),
                    Hired = false,
                });
            }
        }

        private void Broadcast(StaffModuleDeltaMessage message)
        {
            if (!_shutdown && _context.InGame())
            {
                _context.Broadcast(message);
            }
        }

        private static bool TryGetFinite(float value, out float result)
        {
            result = value;
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private void PublishBootstrapAll()
        {
            var manager = GetWorkerManager();
            var count = manager?.m_WorkerDataList?.Count ?? 0;
            for (var i = 0; i < Mathf.Min(count, StaffModuleInterop.MaxWorkers); i++)
            {
                if (StaffModuleInterop.IsHired(i))
                {
                    PublishWorkerBootstrap(i);
                }
            }
        }

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            ResetWorldState();
        }

        private void ResetWorldState()
        {
            foreach (var pair in _leaseOwner)
            {
                if (StaffModuleInterop.TryGetWorker(GetWorkerManager(), pair.Key, out var worker))
                {
                    worker.m_IsPausingAction = false;
                }
            }

            _leaseOwner.Clear();
            _identities.Clear();
        }

        private void Reject(int connectionId, Guid predictionId)
        {
            if (predictionId != Guid.Empty)
            {
                PredictionApi.Rollback(_context, connectionId, predictionId);
            }
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            ResetWorldState();
            SceneManager.sceneLoaded -= OnSceneLoaded;
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

        [HarmonyPatch(typeof(Worker), nameof(Worker.OnMousePress))]
        private static class WorkerMousePatch
        {
            [HarmonyPrefix]
            private static bool Prefix(Worker __instance)
                => _active == null || _active._shutdown || __instance == null
                    || _active.HostBeginInteraction(__instance.m_WorkerIndex, 0, default);
        }

        [HarmonyPatch(typeof(Worker), nameof(Worker.OnPressStopInteract))]
        private static class WorkerStopPatch
        {
            [HarmonyPostfix]
            private static void Postfix(Worker __instance)
            {
                if (_active != null && !_active._shutdown && __instance != null)
                {
                    _active.HostEndInteraction(__instance.m_WorkerIndex, 0);
                }
            }
        }

        [HarmonyPatch(typeof(Worker), "ActivateWorker")]
        private static class WorkerLifecyclePatch
        {
            [HarmonyPostfix]
            private static void Postfix(Worker __instance)
            {
                if (_active == null || __instance == null)
                {
                    return;
                }

                _active.PublishWorkerBootstrap(__instance.m_WorkerIndex);
            }
        }

        [HarmonyPatch(typeof(Worker), "SetTask")]
        [HarmonyPatch(typeof(Worker), "SetLastTask")]
        [HarmonyPatch(typeof(Worker), "SetSecondaryTask")]
        private static class WorkerTaskPatch
        {
            [HarmonyPostfix]
            private static void Postfix(Worker __instance) => _active?.PublishTask(__instance);
        }

        [HarmonyPatch(typeof(Worker), "SetRestockShelfWithNoLabel")]
        [HarmonyPatch(typeof(Worker), "UpdateSetPriceOption")]
        [HarmonyPatch(typeof(Worker), "UpdateSetCardPriceOption")]
        private static class WorkerOptionsPatch
        {
            [HarmonyPostfix]
            private static void Postfix(Worker __instance) => _active?.PublishOptions(__instance);
        }

        [HarmonyPatch(typeof(Worker), "SetCardPackItemTypeEnabled")]
        private static class WorkerPackPatch
        {
            [HarmonyPostfix]
            private static void Postfix(Worker __instance, int __0, bool __1)
                => _active?.PublishPack(__instance, __0, __1);
        }

        [HarmonyPatch(typeof(Worker), "GiveSalaryBonus")]
        private static class WorkerBonusPatch
        {
            [HarmonyPostfix]
            private static void Postfix(Worker __instance) => _active?.PublishBonus(__instance);
        }

        [HarmonyPatch(typeof(Worker), "AddExp")]
        private static class WorkerExperiencePatch
        {
            [HarmonyPostfix]
            private static void Postfix(Worker __instance, EWorkerTask __0)
                => _active?.PublishExperience(__instance, __0);
        }

        [HarmonyPatch(typeof(Worker), "FireWorker")]
        private static class WorkerFirePatch
        {
            [HarmonyPostfix]
            private static void Postfix(Worker __instance) => _active?.PublishFired(__instance);
        }

        [HarmonyPatch(typeof(WorkerManager), "Start")]
        private static class WorkerManagerStartPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => _active?.PublishBootstrapAll();
        }
    }
}
