using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Modules.Npc;
using CardShopCoop.Runtime;
using CardShopCoop.Util;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Modules.Staff
{
    /// <summary>
    /// Game-facing Staff helpers shared by the host and client behaviours.  Worker and
    /// WorkerSaveData are present in both supported game builds.  UI implementation details are
    /// private in both builds, so those members are intentionally resolved through Harmony's
    /// reflection surface instead of becoming compile-time dependencies.
    /// </summary>
    internal static class StaffModuleInterop
    {
        internal const int MaxWorkers = 32;
        internal static readonly FieldInfo PanelIsHired =
            AccessTools.Field(typeof(HireWorkerPanelUI), "m_IsHired");
        internal static readonly FieldInfo PanelIndex =
            AccessTools.Field(typeof(HireWorkerPanelUI), "m_Index");
        internal static readonly FieldInfo PanelLevelRequired =
            AccessTools.Field(typeof(HireWorkerPanelUI), "m_LevelRequired");
        internal static readonly FieldInfo PanelHireFee =
            AccessTools.Field(typeof(HireWorkerPanelUI), "m_TotalHireFee");
        internal static readonly FieldInfo PanelScreen =
            AccessTools.Field(typeof(HireWorkerPanelUI), "m_HireWorkerScreen");
        internal static readonly MethodInfo PanelEvaluateHired =
            AccessTools.Method(typeof(HireWorkerPanelUI), "EvaluateHired");
        internal static readonly FieldInfo InteractWorker =
            AccessTools.Field(typeof(WorkerInteractUIScreen), "m_Worker");
        internal static readonly FieldInfo OptionWorker =
            AccessTools.Field(typeof(WorkerOptionUIScreen), "m_Worker");
        internal static readonly FieldInfo PriceWorker =
            AccessTools.Field(typeof(WorkerOptionSetPriceUIScreen), "m_Worker");
        internal static readonly FieldInfo PackWorker =
            AccessTools.Field(typeof(WorkerSetPackOpenerTypeOptionScreen), "m_Worker");
        internal static readonly FieldInfo TaskToSet =
            AccessTools.Field(typeof(WorkerInteractUIScreen), "m_TaskToSet");
        internal static readonly FieldInfo OptionTaskIndex =
            AccessTools.Field(typeof(WorkerOptionUIScreen), "m_TaskIndex");
        internal static readonly FieldInfo PriceTask =
            AccessTools.Field(typeof(WorkerOptionSetPriceUIScreen), "m_TaskToSet");
        internal static readonly FieldInfo PriceRoundUp =
            AccessTools.Field(typeof(WorkerOptionSetPriceUIScreen), "m_IsRoundUpPrice");
        internal static readonly FieldInfo PriceCanSetCard =
            AccessTools.Field(typeof(WorkerOptionSetPriceUIScreen), "m_CanSetCardPrice");
        internal static readonly FieldInfo PriceMultiplier =
            AccessTools.Field(typeof(WorkerOptionSetPriceUIScreen), "m_SetPricePercentMultiplier");
        internal static readonly FieldInfo PackEnabled =
            AccessTools.Field(typeof(WorkerSetPackOpenerTypeOptionScreen), "m_IsPackTypeEnabled");
        internal static readonly FieldInfo WorkerTargetRotation =
            AccessTools.Field(typeof(Worker), "m_TargetLerpRotation");
        private static readonly FieldInfo WorkerList =
            AccessTools.Field(typeof(WorkerManager), "m_WorkerList");
        private static readonly MethodInfo EvaluateSkillLevel =
            AccessTools.Method(typeof(Worker), "EvaluateSkillLevel");

        internal static WorkerManager FindWorkerManager()
            => SceneRef<WorkerManager>.Get();

        internal static Worker WorkerFrom(FieldInfo field, object instance)
            => field?.GetValue(instance) as Worker;

        internal static List<Worker> GetWorkers(WorkerManager manager)
            => manager == null || WorkerList == null ? null : WorkerList.GetValue(manager) as List<Worker>;

        internal static bool TryGetWorker(WorkerManager manager, int index, out Worker worker)
        {
            worker = null;
            var workers = GetWorkers(manager);
            if (workers == null || index < 0 || index >= workers.Count)
            {
                return false;
            }

            worker = workers[index];
            return worker != null;
        }

        internal static bool TryGetWorkerFromPuppet(int index, out Worker worker)
        {
            worker = NpcClientBehaviour.GetWorkerPuppet(index);
            return worker != null;
        }

        internal static bool IsHired(int index)
            => CPlayerData.m_IsWorkerHired != null && index >= 0
                && index < CPlayerData.m_IsWorkerHired.Count
                && CPlayerData.GetIsWorkerHired(index);

        internal static bool IsAddressable(WorkerManager manager, int index)
            => manager != null && manager.m_WorkerDataList != null && index >= 0
                && index < Mathf.Min(manager.m_WorkerDataList.Count, MaxWorkers);

        internal static bool IsValidWorker(WorkerManager manager, int index)
            => IsAddressable(manager, index) && TryGetWorker(manager, index, out var worker)
                && IsHired(index);

        internal static bool IsValidTask(byte value)
        {
            return Enum.IsDefined(typeof(EWorkerTask), (EWorkerTask)value);
        }

        internal static bool IsValidIntentTask(byte value)
        {
            var task = (EWorkerTask)value;
            return value <= 6 || task == EWorkerTask.GoBackHome;
        }

        internal static bool IsValidState(byte value)
            => Enum.IsDefined(typeof(EWorkerState), (EWorkerState)value);

        internal static bool IsFinite(Vector3 value)
            => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

        internal static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

        internal static StaffModuleEntry CaptureEntry(WorkerManager manager, int index)
        {
            var entry = new StaffModuleEntry { Hired = IsHired(index) };
            WorkerSaveData data = null;
            var workers = GetWorkers(manager);
            var worker = workers != null && index >= 0 && index < workers.Count
                ? workers[index] : null;
            if (worker != null && worker.m_IsActive)
            {
                try
                {
                    data = worker.GetWorkerSaveData();
                }
                catch (Exception exception)
                {
                    Swallow.Log(exception);
                }
            }

            var saved = CPlayerData.m_WorkerSaveDataList;
            if (data == null && saved != null && index >= 0 && index < saved.Count)
            {
                data = saved[index];
            }

            if (data == null)
            {
                return entry;
            }

            entry.HasData = true;
            entry.PrimaryTask = (byte)data.primaryTask;
            entry.SecondaryTask = (byte)data.secondaryTask;
            entry.WorkerTask = (byte)data.workerTask;
            entry.CurrentState = (byte)data.currentState;
            entry.GoingHome = data.isGoingHome;
            entry.BonusCount = (byte)Mathf.Clamp(data.bonusBoostedCount, 0, byte.MaxValue);
            entry.BonusBoosted = data.isBonusBoosted;
            entry.FillNoLabel = data.isFillShelfWithoutLabel;
            entry.RoundUpPrice = data.isRoundUpPrice;
            entry.RoundUpCardPrice = data.isRoundUpCardPrice;
            entry.AvoidSetCardPrice = data.isAvoidSetCardPrice;
            entry.AvoidSetCardPriceRestock = data.isAvoidSetCardPriceWhileRestock;
            entry.PriceMult = data.setPriceMultiplier;
            entry.CardPriceMult = data.setCardPriceMultiplier;
            entry.PackTypes = data.cardPackItemTypeEnabledList == null
                ? null : new List<bool>(data.cardPackItemTypeEnabledList);
            entry.ExpList = data.expList == null ? null : new List<int>(data.expList);
            return entry;
        }

        internal static StaffModuleEntry CaptureWorkerEntry(Worker worker, int index)
        {
            var entry = new StaffModuleEntry
            {
                Hired = IsHired(index),
            };
            if (worker == null)
            {
                return entry;
            }

            entry.HasData = true;
            entry.PrimaryTask = (byte)worker.m_PrimaryTask;
            entry.SecondaryTask = (byte)worker.m_SecondaryTask;
            entry.WorkerTask = (byte)worker.m_WorkerTask;
            entry.CurrentState = (byte)worker.m_CurrentState;
            entry.BonusCount = (byte)Mathf.Clamp(worker.GetBonusBoostedCount(), 0, byte.MaxValue);
            entry.BonusBoosted = worker.m_IsBonusBoosted;
            entry.FillNoLabel = worker.GetWorkerSaveData().isFillShelfWithoutLabel;
            entry.RoundUpPrice = worker.GetIsRoundUpPrice();
            entry.RoundUpCardPrice = worker.GetIsRoundUpCardPrice();
            entry.AvoidSetCardPrice = worker.GetIsAvoidSetCardPrice();
            entry.AvoidSetCardPriceRestock = worker.GetIsAvoidSetCardPriceWhileRestock();
            entry.PriceMult = worker.GetPriceMultiplier();
            entry.CardPriceMult = worker.GetCardPriceMultiplier();
            var packTypes = worker.GetCardPackItemTypeEnabledList();
            entry.PackTypes = packTypes == null ? null : new List<bool>(packTypes);
            entry.ExpList = worker.m_ExpList == null ? null : new List<int>(worker.m_ExpList);
            return entry;
        }

        internal static List<StaffModuleEntry> CaptureAll(WorkerManager manager)
        {
            var entries = new List<StaffModuleEntry>();
            if (manager == null || manager.m_WorkerDataList == null)
            {
                return entries;
            }

            var count = Mathf.Min(manager.m_WorkerDataList.Count, MaxWorkers);
            for (var i = 0; i < count; i++)
            {
                entries.Add(CaptureEntry(manager, i));
            }

            return entries;
        }

        internal static void ApplyEntryToSave(StaffModuleEntry entry, WorkerSaveData data)
        {
            data.primaryTask = (EWorkerTask)entry.PrimaryTask;
            data.secondaryTask = (EWorkerTask)entry.SecondaryTask;
            data.workerTask = (EWorkerTask)entry.WorkerTask;
            data.currentState = (EWorkerState)entry.CurrentState;
            data.isGoingHome = entry.GoingHome;
            data.bonusBoostedCount = entry.BonusCount;
            data.isBonusBoosted = entry.BonusBoosted;
            data.isFillShelfWithoutLabel = entry.FillNoLabel;
            data.isRoundUpPrice = entry.RoundUpPrice;
            data.isRoundUpCardPrice = entry.RoundUpCardPrice;
            data.isAvoidSetCardPrice = entry.AvoidSetCardPrice;
            data.isAvoidSetCardPriceWhileRestock = entry.AvoidSetCardPriceRestock;
            data.setPriceMultiplier = entry.PriceMult;
            data.setCardPriceMultiplier = entry.CardPriceMult;
            if (entry.PackTypes != null)
            {
                data.cardPackItemTypeEnabledList = new List<bool>(entry.PackTypes);
            }

            if (entry.ExpList != null)
            {
                data.expList = new List<int>(entry.ExpList);
            }
        }

        internal static void ApplyEntryToWorker(Worker worker, StaffModuleEntry entry)
        {
            if (worker == null)
            {
                return;
            }

            // FireWorker disables the worker collider, and the game only re-enables it inside
            // ActivateWorker. The client puppet never runs that path, so a re-hired worker would
            // stay unclickable unless the hired state restores the interaction surface here.
            worker.m_IsActive = entry.Hired;
            if (worker.m_WorkerCollider != null)
            {
                worker.m_WorkerCollider.gameObject.SetActive(entry.Hired);
            }

            worker.m_PrimaryTask = (EWorkerTask)entry.PrimaryTask;
            worker.m_SecondaryTask = (EWorkerTask)entry.SecondaryTask;
            worker.m_WorkerTask = (EWorkerTask)entry.WorkerTask;
            worker.m_CurrentState = (EWorkerState)entry.CurrentState;
            worker.m_IsBonusBoosted = entry.BonusBoosted;
            worker.m_BonusBoostedCount = entry.BonusCount;
            worker.SetRestockShelfWithNoLabel(entry.FillNoLabel);
            worker.UpdateSetPriceOption(entry.RoundUpPrice, entry.AvoidSetCardPrice, entry.PriceMult);
            worker.UpdateSetCardPriceOption(entry.RoundUpCardPrice, entry.AvoidSetCardPriceRestock,
                entry.CardPriceMult);

            var packTypes = worker.GetCardPackItemTypeEnabledList();
            if (entry.PackTypes != null && packTypes != null)
            {
                while (packTypes.Count < entry.PackTypes.Count)
                {
                    packTypes.Add(true);
                }

                for (var i = 0; i < entry.PackTypes.Count; i++)
                {
                    worker.SetCardPackItemTypeEnabled(i, entry.PackTypes[i]);
                }
            }

            if (entry.ExpList != null && worker.m_ExpList != null)
            {
                worker.m_ExpList.Clear();
                worker.m_ExpList.AddRange(entry.ExpList);
            }
        }

        internal static void SetExperience(Worker worker, EWorkerTask task, int amount)
        {
            if (worker?.m_ExpList == null)
            {
                return;
            }

            var index = (int)task;
            if (index < 0 || index >= worker.m_ExpList.Count)
            {
                return;
            }

            worker.m_ExpList[index] = amount;
            EvaluateSkillLevel?.Invoke(worker, null);
        }

        internal static void AimWorkerAtPlayer(Worker worker, Vector3 playerPosition)
        {
            if (worker == null || WorkerTargetRotation == null)
            {
                return;
            }

            var direction = playerPosition - worker.transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.0001f)
            {
                WorkerTargetRotation.SetValue(worker, Quaternion.LookRotation(direction, Vector3.up));
            }
        }

        internal static void RefreshWorkerUi(int index, WorkerSaveData data)
            => NpcClientBehaviour.RefreshWorkerUi(index, data);
    }
}
