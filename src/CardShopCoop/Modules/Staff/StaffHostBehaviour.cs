using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Economy;
using CardShopCoop.Modules.Npc;
using CardShopCoop.Modules.Actions;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Staff
{
    /// <summary>Host validation and live-worker operation publication.</summary>
    [ServerBehaviour]
    public sealed class StaffHostBehaviour : CoopBehaviour
    {
        private sealed class WorkerIdentity
        {
            public Worker Worker;
            public uint Generation;
        }

        private static StaffHostBehaviour _active;
        private readonly Dictionary<Worker, int> _leaseOwner = new();
        private readonly Dictionary<Worker, WorkerIdentity> _identities = new();
        private bool _shutdown;
        private bool _initialized;
        private Guid _intentActionId;

        /// <summary>How far a remote player may be from a worker to begin an interaction. Vanilla
        /// only opens the worker panel on a nearby click, so this generous bound rejects a
        /// cross-shop begin.</summary>
        private const float StaffInteractionReach = 5f;

        private void OnEnable()
        {
            if (_shutdown || _initialized)
            {
                return;
            }

            _active = this;
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
            _initialized = true;
        }

        private void Patch(Type patchType)
            => Harmony.CreateClassProcessor(patchType).Patch();

        [OnFullyJoined]
        private void SendJoinBaseline(PeerConnection connection)
        {
            if (connection != null && IsJoinPhase(connection.State) && RuntimeContext.InGame()
                && GetWorkerManager()?.m_WorkerDataList != null)
            {
                RuntimeContext.Send(connection.Id, BuildBaseline());
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

        [MessageHandler]
        private void HandleIntent(MessageContext context, StaffModuleIntentMessage message)
        {
            if (!IsValidSender(context, message))
            {
                return;
            }

            var peerId = context.Connection.Id;
            _intentActionId = message.ActionId;
            var accepted = false;
            try
            {
                var worker = message.Worker;
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
                        accepted = HostBonus(worker, peerId);
                        break;
                    case StaffIntentKind.Fire:
                        accepted = HostFire(worker, peerId);
                        break;
                    case StaffIntentKind.BeginInteraction:
                        accepted = HostBeginInteraction(worker, peerId, message.Position);
                        break;
                    case StaffIntentKind.EndInteraction:
                        accepted = HostEndInteraction(worker, peerId);
                        break;
                }
            }
            finally
            {
                _intentActionId = Guid.Empty;
            }

            if (!accepted)
            {
                Reject(peerId, message.ActionId);
            }
        }

        private bool IsValidSender(MessageContext context, StaffModuleIntentMessage message)
        {
            if (_shutdown || !RuntimeContext.InGame() || context?.Connection == null
                || !IsJoinPhase(context.Connection.State) || message == null)
            {
                return false;
            }

            // A hire addresses a save-data slot: the worker does not exist yet, so it cannot be
            // carried on the wire. Every other intent operates a live, already-replicated worker.
            return message.Kind == StaffIntentKind.Hire
                ? StaffModuleInterop.IsAddressable(GetWorkerManager(), message.Index)
                : message.Worker != null;
        }

        private StaffModuleBaselineMessage BuildBaseline()
        {
            var manager = GetWorkerManager();
            var baseline = new StaffModuleBaselineMessage();
            var entries = StaffModuleInterop.CaptureAll(manager);
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                // Only an active worker has an identity the wire converter can serialize. An
                // inactive slot has no live replicated entity, so it is not a reference message.
                if (entry.Worker == null || !NpcHostBehaviour.IsNpcTracked(entry.Worker))
                {
                    continue;
                }

                baseline.Entries.Add(WithGeneration(entry.Worker, entry));
            }

            return baseline;
        }

        private StaffModuleEntry WithGeneration(Worker worker, StaffModuleEntry entry)
        {
            entry.Generation = EnsureGeneration(worker);
            return entry;
        }

        private uint EnsureGeneration(Worker worker)
        {
            if (worker == null)
            {
                return 1u;
            }

            if (!_identities.TryGetValue(worker, out var identity))
            {
                identity = new WorkerIdentity { Worker = worker, Generation = 1u };
                _identities.Add(worker, identity);
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

            // Vanilla ActivateWorker is a no-op after the day ends. Reject before spending so a
            // late hire cannot charge the shared wallet and leave the client's action pending
            // without a worker/delta.
            if (LightManager.GetHasDayEnded())
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
            var worker = message.Worker;
            if (!_leaseOwner.TryGetValue(worker, out var owner) || owner != connectionId)
            {
                return false;
            }

            if (!StaffModuleInterop.IsValidWorker(worker))
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
                    PublishTask(worker, true);
                    HostEndInteraction(worker, connectionId);
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
                    PublishOptions(worker, true);
                    HostEndInteraction(worker, connectionId);
                }

                return accepted;
            }

            if (message.Kind == StaffIntentKind.Pack)
            {
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
                    PublishPackBatch(worker, message.PackChanges, true);
                    HostEndInteraction(worker, connectionId);
                }

                return accepted;
            }

            return false;
        }

        private bool ApplyTask(StaffModuleIntentMessage message)
        {
            if (!AreTasksValid(message) || message.Worker == null)
            {
                return false;
            }

            var worker = message.Worker;
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
                || priceMult < 0f || priceMult > 10f || cardPriceMult < 0f || cardPriceMult > 10f
                || message.Worker == null)
            {
                return false;
            }

            var worker = message.Worker;
            worker.SetRestockShelfWithNoLabel(message.FillNoLabel);
            worker.UpdateSetPriceOption(message.RoundUpPrice, message.AvoidSetCardPrice,
                Mathf.Clamp(priceMult, 0f, 10f));
            worker.UpdateSetCardPriceOption(message.RoundUpCardPrice, message.AvoidSetCardPriceRestock,
                Mathf.Clamp(cardPriceMult, 0f, 10f));
            return true;
        }

        private bool HostBonus(Worker worker, int connectionId)
        {
            if (!StaffModuleInterop.IsValidWorker(worker)
                || !_leaseOwner.TryGetValue(worker, out var owner) || owner != connectionId)
            {
                return false;
            }

            if (worker.GetBonusBoostedCount() >= 3 || worker.GetWorkerData() == null)
            {
                return false;
            }

            var fee = worker.GetWorkerData().costPerDay;
            if (!TrySpend(fee, ETransactionType.WorkerSalary, worker.m_WorkerIndex))
            {
                return false;
            }

            worker.GiveSalaryBonus();
            CPlayerData.m_GameReportDataCollect.employeeCost -= fee;
            CPlayerData.m_GameReportDataCollectPermanent.employeeCost -= fee;
            return true;
        }

        private bool HostFire(Worker worker, int connectionId)
        {
            if (!StaffModuleInterop.IsValidWorker(worker)
                || !_leaseOwner.TryGetValue(worker, out var owner) || owner != connectionId)
            {
                return false;
            }

            worker.FireWorker();
            HostEndInteraction(worker, connectionId);
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

        private bool HostBeginInteraction(Worker worker, int connectionId, Vector3 playerPosition)
        {
            if (!StaffModuleInterop.IsValidWorker(worker)
                || !StaffModuleInterop.IsFinite(playerPosition))
            {
                return false;
            }

            if (connectionId != 0)
            {
                // A remote interaction must come from a player actually standing at the worker,
                // by the same authenticated-presence rule the trade/deodorant intents use - never
                // from the client's own position payload.
                if (!RuntimeContext.PeerPresence.TryGet(connectionId, out var presence)
                    || presence.Age > TimeSpan.FromSeconds(2)
                    || (presence.Position - worker.transform.position).sqrMagnitude
                        > StaffInteractionReach * StaffInteractionReach)
                {
                    return false;
                }
            }

            if (_leaseOwner.TryGetValue(worker, out var owner))
            {
                return owner == connectionId;
            }

            if (connectionId != 0)
            {
                StaffModuleInterop.AimWorkerAtPlayer(worker, playerPosition);
            }

            worker.m_IsPausingAction = true;
            _leaseOwner[worker] = connectionId;
            PublishInteraction(worker, true, connectionId);
            return true;
        }

        private bool HostEndInteraction(Worker worker, int connectionId)
        {
            if (!_leaseOwner.TryGetValue(worker, out var owner) || owner != connectionId)
            {
                return false;
            }

            if (worker != null)
            {
                worker.m_IsPausingAction = false;
            }

            _leaseOwner.Remove(worker);
            PublishInteraction(worker, false, 0);
            return true;
        }

        private void HostReleaseConnection(int connectionId)
        {
            var release = new List<Worker>();
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

        private void PublishInteraction(Worker worker, bool occupied, int grantedPeer)
        {
            if (_shutdown || !RuntimeContext.InGame() || worker == null)
            {
                return;
            }

            var message = new StaffModuleDeltaMessage
            {
                ActionId = _intentActionId,
                Kind = StaffDeltaKind.Interaction,
                Worker = worker,
                Generation = EnsureGeneration(worker),
                Granted = grantedPeer > 0,
                Occupied = occupied,
            };

            // Only the granted peer may open the worker menu. Everyone else receives the same
            // interaction state with the grant cleared, or it would open a menu (and believe it
            // holds a lease) that belongs to another player.
            if (grantedPeer > 0 && occupied)
            {
                RuntimeContext.Send(grantedPeer, message);
                RuntimeContext.Relay(grantedPeer, new StaffModuleDeltaMessage
                {
                    ActionId = message.ActionId,
                    Kind = message.Kind,
                    Worker = message.Worker,
                    Generation = message.Generation,
                    Granted = false,
                    Occupied = true,
                });
                return;
            }

            RuntimeContext.Broadcast(message);
        }

        private void PublishWorkerBootstrap(Worker worker)
        {
            var manager = GetWorkerManager();
            if (_shutdown || !RuntimeContext.InGame() || worker == null
                || !StaffModuleInterop.IsAddressable(manager, worker.m_WorkerIndex))
            {
                return;
            }

            var entry = WithGeneration(worker,
                StaffModuleInterop.CaptureEntry(manager, worker.m_WorkerIndex));
            Broadcast(new StaffModuleDeltaMessage
            {
                ActionId = _intentActionId,
                Kind = entry.Hired ? StaffDeltaKind.Hired : StaffDeltaKind.Fired,
                Worker = worker,
                Generation = entry.Generation,
                Hired = entry.Hired,
                Bootstrap = entry.Hired ? entry : default,
            });
        }

        private void PublishTask(Worker worker, bool force = false)
        {
            if (worker == null || (!force && _intentActionId != Guid.Empty))
            {
                return;
            }

            var entry = StaffModuleInterop.CaptureWorkerEntry(worker, worker.m_WorkerIndex);
            Broadcast(new StaffModuleDeltaMessage
            {
                ActionId = _intentActionId,
                Kind = StaffDeltaKind.Task,
                Worker = worker,
                Generation = EnsureGeneration(worker),
                PrimaryTask = entry.PrimaryTask,
                SecondaryTask = entry.SecondaryTask,
                WorkerTask = entry.WorkerTask,
                CurrentState = entry.CurrentState,
                GoingHome = entry.GoingHome,
            });
        }

        private void PublishOptions(Worker worker, bool force = false)
        {
            if (worker == null || (!force && _intentActionId != Guid.Empty))
            {
                return;
            }

            var entry = StaffModuleInterop.CaptureWorkerEntry(worker, worker.m_WorkerIndex);
            Broadcast(new StaffModuleDeltaMessage
            {
                ActionId = _intentActionId,
                Kind = StaffDeltaKind.Options,
                Worker = worker,
                Generation = EnsureGeneration(worker),
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
            if (worker != null && (force || _intentActionId == Guid.Empty))
            {
                var entry = StaffModuleInterop.CaptureWorkerEntry(worker, worker.m_WorkerIndex);
                Broadcast(new StaffModuleDeltaMessage
                {
                    ActionId = _intentActionId,
                    Kind = StaffDeltaKind.Pack,
                    Worker = worker,
                    Generation = EnsureGeneration(worker),
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
            if (worker == null || (!force && _intentActionId != Guid.Empty))
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
            if (worker != null)
            {
                Broadcast(new StaffModuleDeltaMessage
                {
                    ActionId = _intentActionId,
                    Kind = StaffDeltaKind.Bonus,
                    Worker = worker,
                    Generation = EnsureGeneration(worker),
                    BonusCount = (byte)Mathf.Clamp(worker.GetBonusBoostedCount(), 0, byte.MaxValue),
                    BonusBoosted = worker.m_IsBonusBoosted,
                });
            }
        }

        private void PublishExperience(Worker worker, EWorkerTask task)
        {
            if (worker != null)
            {
                var entry = StaffModuleInterop.CaptureWorkerEntry(worker, worker.m_WorkerIndex);
                var taskIndex = (int)task;
                if (entry.ExpList == null || taskIndex < 0 || taskIndex >= entry.ExpList.Count)
                {
                    return;
                }

                Broadcast(new StaffModuleDeltaMessage
                {
                    ActionId = _intentActionId,
                    Kind = StaffDeltaKind.Experience,
                    Worker = worker,
                    Generation = EnsureGeneration(worker),
                    ExperienceTask = (byte)task,
                    // ExperienceAmount is the resulting absolute value, not an increment.
                    ExperienceAmount = entry.ExpList[taskIndex],
                });
            }
        }

        private void PublishFired(Worker worker)
        {
            if (worker != null)
            {
                Broadcast(new StaffModuleDeltaMessage
                {
                    ActionId = _intentActionId,
                    Kind = StaffDeltaKind.Fired,
                    Worker = worker,
                    Generation = EnsureGeneration(worker),
                    Hired = false,
                });
            }
        }

        private void Broadcast(StaffModuleDeltaMessage message)
        {
            if (_shutdown || !RuntimeContext.InGame() || message?.Worker == null
                || !NpcHostBehaviour.IsNpcTracked(message.Worker))
            {
                return;
            }

            RuntimeContext.Broadcast(message);
        }

        private static bool TryGetFinite(float value, out float result)
        {
            result = value;
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private void PublishBootstrapAll()
        {
            var manager = GetWorkerManager();
            var workers = StaffModuleInterop.GetWorkers(manager);
            for (var i = 0; workers != null && i < Mathf.Min(workers.Count, StaffModuleInterop.MaxWorkers);
                i++)
            {
                if (StaffModuleInterop.IsHired(i))
                {
                    PublishWorkerBootstrap(workers[i]);
                }
            }
        }

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            ResetWorldState();
        }

        private void ResetWorldState()
        {
            foreach (var worker in _leaseOwner.Keys)
            {
                if (worker != null)
                {
                    worker.m_IsPausingAction = false;
                }
            }

            _leaseOwner.Clear();
            _identities.Clear();
        }

        private void Reject(int connectionId, Guid actionId)
        {
            if (actionId != Guid.Empty)
            {
                ActionLedger.Rollback(RuntimeContext, connectionId, actionId);
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
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

        }

        private void OnDestroy() => Shutdown();

        [HarmonyPatch(typeof(Worker), nameof(Worker.OnMousePress))]
        private static class WorkerMousePatch
        {
            [HarmonyPrefix]
            private static bool Prefix(Worker __instance)
                => _active == null || _active._shutdown || __instance == null
                    || _active.HostBeginInteraction(__instance, 0, default);
        }

        [HarmonyPatch(typeof(Worker), nameof(Worker.OnPressStopInteract))]
        private static class WorkerStopPatch
        {
            [HarmonyPostfix]
            private static void Postfix(Worker __instance)
            {
                if (_active != null && !_active._shutdown && __instance != null)
                {
                    _active.HostEndInteraction(__instance, 0);
                }
            }
        }

        // The identity module binds and announces the worker's live identity in its own postfix.
        // Run after it so the bootstrap delta can serialize the worker reference it names.
        [HarmonyAfter("dev.meepen.cardshopcoop.gameobjectidentity.host")]
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

                _active.PublishWorkerBootstrap(__instance);
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

        [HarmonyAfter("dev.meepen.cardshopcoop.gameobjectidentity.host")]
        [HarmonyPatch(typeof(WorkerManager), "Start")]
        private static class WorkerManagerStartPatch
        {
            [HarmonyPostfix]
            private static void Postfix() => _active?.PublishBootstrapAll();
        }
    }
}
