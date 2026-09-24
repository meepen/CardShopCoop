using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Npc;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using CardShopCoop.Util;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Staff
{
    /// <summary>Predictive worker UI and application of keyed host operations.</summary>
    [ClientBehaviour]
    public sealed class StaffClientBehaviour : CoopBehaviour
    {
        private const string PredictionScope = "staff";
        private static StaffClientBehaviour _active;
        private static bool _applyingRemote;
        private static bool _allowWorkerOpen;

        private readonly Dictionary<int, bool> _workerBusy = new();
        private readonly HashSet<int> _workerLease = new();
        private readonly Dictionary<int, uint> _workerGenerations = new();
        private readonly Dictionary<string, StaffModuleDeltaMessage> _deferredDeltas = new();
        private StaffModuleBaselineMessage _pendingBaseline;
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
        private bool _joined;
        private int _applyingPrediction;

        private sealed class InteractScreenCache : Cached<WorkerInteractUIScreen>
        {
            protected override WorkerInteractUIScreen GetRawValue()
                => UnityEngine.Object.FindObjectOfType<WorkerInteractUIScreen>(true);
        }

        private sealed class HireScreenCache : Cached<HireWorkerScreen>
        {
            protected override HireWorkerScreen GetRawValue()
                => UnityEngine.Object.FindObjectOfType<HireWorkerScreen>(true);
        }

        private readonly InteractScreenCache _interactScreenCache = new();
        private readonly HireScreenCache _hireScreenCache = new();

        private void OnEnable()
        {
            if (_shutdown || _context != null)
            {
                return;
            }

            _context = RuntimeContext;
            _context.Messages.RegisterAttributedHandlers(this);
            _active = this;
            _harmony = new Harmony("com.zwhit.cardshopcoop.staff.module.client");
            Patch(typeof(HirePatch));
            Patch(typeof(TaskPatch));
            Patch(typeof(OptionPatch));
            Patch(typeof(PriceOptionPatch));
            Patch(typeof(PackOptionPatch));
            Patch(typeof(BonusPatch));
            Patch(typeof(FirePatch));
            Patch(typeof(WorkerMousePatch));
            Patch(typeof(WorkerStopPatch));
            SceneManager.sceneLoaded += OnSceneLoaded;
            NpcClientBehaviour.WorkerManagerReady += OnReadinessSignal;
            OnReadinessSignal();
        }

        private void Patch(Type patchType)
            => _harmony.CreateClassProcessor(patchType).Patch();

        [OnFullyJoined]
        private void Joined(PeerConnection _)
        {
            _joined = true;
            TryApplyPending();
        }

        [MessageHandler(typeof(StaffModuleBaselineMessage))]
        private void HandleBaseline(MessageContext _, StaffModuleBaselineMessage message)
        {
            _pendingBaseline = message;
            TryApplyPending();
        }

        [MessageHandler(typeof(StaffModuleDeltaMessage))]
        private void HandleDelta(MessageContext _, StaffModuleDeltaMessage message)
        {
            if (!ApplyDelta(message))
            {
                var key = DeltaKey(message);
                if (_deferredDeltas.TryGetValue(key, out var previous))
                    PredictionApi.ConfirmSuperseded(previous.PredictionId);
                _deferredDeltas[key] = message;
                CoopPlugin.Log.LogInfo("[staff] deferred " + message.Kind + " index=" + message.Index
                    + " generation=" + message.Generation + " known="
                    + (_workerGenerations.TryGetValue(message.Index, out var known)
                        ? known.ToString() : "none"));
            }
        }

        private bool IsSceneReady()
        {
            var manager = StaffModuleInterop.FindWorkerManager();
            return manager != null && manager.m_WorkerDataList != null;
        }

        private void OnReadinessSignal()
        {
            if (_shutdown || !_context.InGame() || !IsSceneReady())
            {
                return;
            }

            TryApplyPending();
            foreach (var pair in new List<KeyValuePair<string, StaffModuleDeltaMessage>>(_deferredDeltas))
            {
                if (ApplyDelta(pair.Value))
                {
                    _deferredDeltas.Remove(pair.Key);
                }
            }
        }

        private void TryApplyPending()
        {
            if (_pendingBaseline == null || !_context.InGame() || !IsSceneReady())
            {
                return;
            }

            _applyingRemote = true;
            try
            {
                _workerGenerations.Clear();
                for (var i = 0; i < _pendingBaseline.Entries.Count; i++)
                {
                    ApplyEntry(i, _pendingBaseline.Entries[i]);
                }
            }
            finally
            {
                _applyingRemote = false;
            }

            _pendingBaseline = null;
        }

        private bool ApplyDelta(StaffModuleDeltaMessage message)
        {
            if (_shutdown || !_context.InGame() || !IsSceneReady())
            {
                return false;
            }

            // Interaction deltas only carry module lease/busy/mode state (plus optional stop/open
            // calls when the puppet exists), so they are safe before the worker puppet is ready.
            // Deferring them can strand the client's lease and block reopening the worker menu.
            if (message.Kind != StaffDeltaKind.Interaction && !IsWorkerGenerationReady(message))
            {
                return false;
            }

            if (message.Kind != StaffDeltaKind.Hired && message.Kind != StaffDeltaKind.Fired
                && message.Kind != StaffDeltaKind.Interaction
                && StaffModuleInterop.TryGetWorkerFromPuppet(message.Index, out var missing) == false)
            {
                return false;
            }

            if (message.Kind == StaffDeltaKind.Interaction && message.Occupied && message.Granted
                && !StaffModuleInterop.TryGetWorkerFromPuppet(message.Index, out _))
            {
                return false;
            }

            Action apply = () =>
            {
                _applyingRemote = true;
                try
                {
                    ApplyDeltaCore(message);
                }
                finally
                {
                    _applyingRemote = false;
                }
            };

            // Fired and Interaction deltas confirm the client's own optimistic action. The local
            // apply already closed/opened the interaction exactly as the host did, so undoing it
            // first (what ApplyAuthoritative does) would reopen the worker menu before re-closing,
            // or re-close before reopening. Every other kind can contradict the prediction, so
            // they stay authoritative.
            if (message.Kind == StaffDeltaKind.Fired || message.Kind == StaffDeltaKind.Interaction)
            {
                PredictionApi.ApplyConfirmed(message.PredictionId, apply);
            }
            else
            {
                PredictionApi.ApplyAuthoritative(message.PredictionId, apply);
            }

            return true;
        }

        private bool IsWorkerGenerationReady(StaffModuleDeltaMessage message)
        {
            if (message.Kind == StaffDeltaKind.Hired || message.Kind == StaffDeltaKind.Fired)
            {
                return true;
            }

            return _workerGenerations.TryGetValue(message.Index, out var generation)
                && generation == message.Generation;
        }

        private void ApplyDeltaCore(StaffModuleDeltaMessage message)
        {
            switch (message.Kind)
            {
                case StaffDeltaKind.Hired:
                    _workerGenerations[message.Index] = message.Generation;
                    ApplyEntry(message.Index, message.Bootstrap);
                    break;
                case StaffDeltaKind.Fired:
                    _workerGenerations[message.Index] = message.Generation;
                    EnsureHiredSlot(message.Index);
                    CPlayerData.SetIsWorkerHired(message.Index, false);
                    if (StaffModuleInterop.TryGetWorkerFromPuppet(message.Index, out var fired))
                    {
                        fired.FireWorker();
                        RefreshWorker(fired);
                    }

                    RefreshHirePanels();
                    break;
                case StaffDeltaKind.Task:
                    ApplyTask(message);
                    break;
                case StaffDeltaKind.Options:
                    ApplyOptions(message);
                    break;
                case StaffDeltaKind.Pack:
                    ApplyPack(message);
                    break;
                case StaffDeltaKind.Bonus:
                    ApplyBonus(message);
                    break;
                case StaffDeltaKind.Experience:
                    ApplyExperience(message);
                    break;
                case StaffDeltaKind.Interaction:
                    ApplyInteraction(message);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(message.Kind), message.Kind,
                        "Unknown staff delta.");
            }
        }

        private void ApplyEntry(int index, StaffModuleEntry entry)
        {
            // Prediction snapshots (CaptureEntry/CaptureWorkerEntry) leave Generation at its
            // default 0 because they describe only worker data. Never overwrite the tracked
            // generation with that default: doing so makes every later Task/Options/Bonus/
            // Experience delta for the worker fail the generation check and defer forever.
            if (entry.Generation != 0)
            {
                _workerGenerations[index] = entry.Generation;
            }

            EnsureHiredSlot(index);
            CPlayerData.SetIsWorkerHired(index, entry.Hired);
            if (entry.HasData)
            {
                var saved = CPlayerData.m_WorkerSaveDataList;
                while (saved.Count <= index)
                {
                    saved.Add(new WorkerSaveData());
                }

                var data = saved[index] ?? new WorkerSaveData();
                saved[index] = data;
                StaffModuleInterop.ApplyEntryToSave(entry, data);
                StaffModuleInterop.RefreshWorkerUi(index, data);
                if (StaffModuleInterop.TryGetWorkerFromPuppet(index, out var worker))
                {
                    StaffModuleInterop.ApplyEntryToWorker(worker, entry);
                }

                RefreshInteractScreen(_interactScreenCache.Get(), index);
            }

            RefreshHirePanels();
        }

        private void ApplyTask(StaffModuleDeltaMessage message)
        {
            if (!StaffModuleInterop.TryGetWorkerFromPuppet(message.Index, out var worker))
            {
                return;
            }

            worker.SetTask((EWorkerTask)message.PrimaryTask);
            worker.SetLastTask((EWorkerTask)message.WorkerTask);
            worker.SetSecondaryTask((EWorkerTask)message.SecondaryTask);
            RefreshWorker(worker);
        }

        private void ApplyOptions(StaffModuleDeltaMessage message)
        {
            if (!StaffModuleInterop.TryGetWorkerFromPuppet(message.Index, out var worker))
            {
                return;
            }

            worker.SetRestockShelfWithNoLabel(message.FillNoLabel);
            worker.UpdateSetPriceOption(message.RoundUpPrice, message.AvoidSetCardPrice, message.PriceMult);
            worker.UpdateSetCardPriceOption(message.RoundUpCardPrice, message.AvoidSetCardPriceRestock,
                message.CardPriceMult);
            worker.SetTask((EWorkerTask)message.PrimaryTask);
            worker.SetLastTask((EWorkerTask)message.WorkerTask);
            worker.SetSecondaryTask((EWorkerTask)message.SecondaryTask);
            RefreshWorker(worker);
        }

        private void ApplyPack(StaffModuleDeltaMessage message)
        {
            if (!StaffModuleInterop.TryGetWorkerFromPuppet(message.Index, out var worker))
            {
                return;
            }

            if (message.PackChanges != null && message.PackChanges.Count > 0)
            {
                for (var i = 0; i < message.PackChanges.Count; i++)
                {
                    var change = message.PackChanges[i];
                    worker.SetCardPackItemTypeEnabled(change.Index, change.Enabled);
                }
            }
            else
            {
                worker.SetCardPackItemTypeEnabled(message.PackIndex, message.PackEnabled);
            }

            worker.SetTask((EWorkerTask)message.PrimaryTask);
            worker.SetLastTask((EWorkerTask)message.WorkerTask);
            worker.SetSecondaryTask((EWorkerTask)message.SecondaryTask);
            RefreshWorker(worker);
        }

        private void ApplyBonus(StaffModuleDeltaMessage message)
        {
            if (!StaffModuleInterop.TryGetWorkerFromPuppet(message.Index, out var worker))
            {
                return;
            }

            worker.m_BonusBoostedCount = message.BonusCount;
            worker.m_IsBonusBoosted = message.BonusBoosted;
            RefreshWorker(worker);
            RefreshInteractScreen(_interactScreenCache.Get(), message.Index);
        }

        private void ApplyExperience(StaffModuleDeltaMessage message)
        {
            if (StaffModuleInterop.TryGetWorkerFromPuppet(message.Index, out var worker))
            {
                StaffModuleInterop.SetExperience(worker, (EWorkerTask)message.ExperienceTask,
                    message.ExperienceAmount);
                RefreshWorker(worker);
            }
        }

        private void ApplyInteraction(StaffModuleDeltaMessage message)
        {
            _workerBusy[message.Index] = message.Occupied;
            if (!message.Occupied)
            {
                _workerLease.Remove(message.Index);
                if (StaffModuleInterop.TryGetWorkerFromPuppet(message.Index, out var released))
                {
                    WithRemote(released.OnPressStopInteract);
                }

                return;
            }

            if (!message.Granted || !StaffModuleInterop.TryGetWorkerFromPuppet(message.Index,
                out var worker))
            {
                return;
            }

            _workerLease.Add(message.Index);
            _allowWorkerOpen = true;
            try
            {
                worker.OnMousePress();
            }
            finally
            {
                _allowWorkerOpen = false;
            }
        }

        private void RefreshWorker(Worker worker)
        {
            var entry = StaffModuleInterop.CaptureWorkerEntry(worker, worker.m_WorkerIndex);
            var saved = new WorkerSaveData();
            StaffModuleInterop.ApplyEntryToSave(entry, saved);
            StaffModuleInterop.RefreshWorkerUi(worker.m_WorkerIndex, saved);
        }

        private static void WithRemote(Action action)
        {
            _applyingRemote = true;
            try
            {
                action();
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        /// <summary>Drops the client's optimistic interaction with a worker. The host ends the
        /// interaction as part of Task/Options/Pack/Fire, and the running game method already
        /// stopped the local interaction UI, so the module's lease/busy flags must follow. This
        /// must not depend on the later <see cref="StaffDeltaKind.Interaction"/> delta, which can
        /// be deferred.</summary>
        private void ReleaseLocalInteraction(int index)
        {
            _workerLease.Remove(index);
            _workerBusy[index] = false;
        }

        private void WithPrediction(Action action)
        {
            _applyingPrediction++;
            try
            {
                action();
            }
            finally
            {
                _applyingPrediction--;
            }
        }

        private static bool RouteInteraction(Func<StaffClientBehaviour, bool> interaction,
            Func<StaffClientBehaviour, bool> allowOriginal = null)
        {
            var active = _active;
            // While a prediction is applying (or undoing) we are already running the game's own
            // method on purpose, so every prefix must let it through. Without this check the UI
            // patches below re-enter their own interceptor from inside the prediction apply, which
            // sends an unbounded stream of intents and disconnects the client.
            if (active == null || active._shutdown || _applyingRemote
                || active._applyingPrediction != 0)
            {
                return true;
            }

            if (allowOriginal != null && allowOriginal(active))
            {
                return true;
            }

            if (!active._joined)
            {
                return false;
            }

            return interaction(active);
        }

        private static void EnsureHiredSlot(int index)
        {
            while (CPlayerData.m_IsWorkerHired.Count <= index)
            {
                CPlayerData.m_IsWorkerHired.Add(false);
            }
        }

        private void RefreshHirePanels()
        {
            var screen = _hireScreenCache.Get();
            if (screen == null || screen.m_HireWorkerPanelUIList == null
                || StaffModuleInterop.PanelEvaluateHired == null
                || StaffModuleInterop.PanelScreen == null)
            {
                return;
            }

            for (var i = 0; i < screen.m_HireWorkerPanelUIList.Count; i++)
            {
                var panel = screen.m_HireWorkerPanelUIList[i];
                if (panel == null || StaffModuleInterop.PanelScreen.GetValue(panel) == null)
                {
                    continue;
                }

                StaffModuleInterop.PanelEvaluateHired.Invoke(panel, null);
            }
        }

        private static void RefreshInteractScreen(WorkerInteractUIScreen screen, int index)
        {
            if (screen == null || screen.m_ScreenGrp == null || !screen.m_ScreenGrp.activeSelf)
            {
                return;
            }

            var worker = StaffModuleInterop.WorkerFrom(StaffModuleInterop.InteractWorker, screen);
            if (worker == null || worker.m_WorkerIndex != index)
            {
                return;
            }

            var count = worker.GetBonusBoostedCount();
            screen.m_GiveBonusBtn.interactable = count < 3;
            if (count > 0)
            {
                screen.m_BonusAddAmountText.text = "+" + count;
                screen.m_BonusAddAmountGrp.SetActive(true);
            }
            else
            {
                screen.m_BonusAddAmountGrp.SetActive(false);
            }
        }

        private StaffModuleEntry CaptureBefore(Worker worker)
            => StaffModuleInterop.CaptureWorkerEntry(worker, worker.m_WorkerIndex);

        private StaffModuleEntry CaptureManagerEntry(int index)
        {
            var manager = StaffModuleInterop.FindWorkerManager();
            return StaffModuleInterop.CaptureEntry(manager, index);
        }

        private void Restore(StaffModuleEntry entry, Worker worker)
        {
            _applyingRemote = true;
            try
            {
                ApplyEntry(worker.m_WorkerIndex, entry);
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        private bool PredictWorker(string scope, StaffModuleIntentMessage message, Worker worker,
            StaffModuleEntry before, Action apply, Action undo = null)
        {
            PredictionApi.Predict(scope,
                id =>
                {
                    message.PredictionId = id;
                    Send(message);
                },
                apply,
                undo ?? (() => Restore(before, worker)));
            return false;
        }

        private bool InterceptHire(HireWorkerPanelUI panel)
        {
            if (panel == null || StaffModuleInterop.PanelIsHired == null
                || StaffModuleInterop.PanelIndex == null || StaffModuleInterop.PanelLevelRequired == null
                || StaffModuleInterop.PanelHireFee == null)
            {
                return false;
            }

            if ((bool)StaffModuleInterop.PanelIsHired.GetValue(panel))
            {
                return false;
            }

            var index = (int)StaffModuleInterop.PanelIndex.GetValue(panel);
            var levelRequired = (int)StaffModuleInterop.PanelLevelRequired.GetValue(panel);
            var fee = (float)StaffModuleInterop.PanelHireFee.GetValue(panel);
            if (CPlayerData.m_ShopLevel + 1 < levelRequired || CPlayerData.m_CoinAmountDouble < fee)
            {
                return false;
            }

            var before = CaptureManagerEntry(index);
            var predicted = before;
            predicted.Hired = true;
            predicted.HasData = true;
            predicted.PrimaryTask = (byte)EWorkerTask.Rest;
            predicted.SecondaryTask = (byte)EWorkerTask.Rest;
            predicted.WorkerTask = (byte)EWorkerTask.Rest;
            predicted.CurrentState = (byte)EWorkerState.Idle;
            var worker = StaffModuleInterop.TryGetWorkerFromPuppet(index, out var puppet) ? puppet : null;
            PredictionApi.Predict(PredictionScope + ":" + index,
                id => Send(new StaffModuleIntentMessage
                {
                    PredictionId = id,
                    Kind = StaffIntentKind.Hire,
                    Index = index,
                }),
                () => ApplyEntry(index, predicted),
                () => ApplyEntry(index, before));
            SoundManager.GenericConfirm();
            _context.SetStatusLine?.Invoke("hired - starting work at the host's shop", 4f);
            return false;
        }

        private bool InterceptBonus(WorkerInteractUIScreen screen)
        {
            var worker = StaffModuleInterop.WorkerFrom(StaffModuleInterop.InteractWorker, screen);
            if (worker == null)
            {
                return false;
            }

            var before = CaptureBefore(worker);
            return PredictWorker(PredictionScope + ":" + worker.m_WorkerIndex,
                new StaffModuleIntentMessage
                {
                    Kind = StaffIntentKind.Bonus,
                    Index = worker.m_WorkerIndex,
                },
                worker,
                before,
                () =>
                {
                    worker.GiveSalaryBonus();
                    RefreshWorker(worker);
                    RefreshInteractScreen(screen, worker.m_WorkerIndex);
                });
        }

        private bool InterceptFire(WorkerInteractUIScreen screen)
        {
            var worker = StaffModuleInterop.WorkerFrom(StaffModuleInterop.InteractWorker, screen);
            if (worker == null)
            {
                return false;
            }

            var index = worker.m_WorkerIndex;
            var before = CaptureBefore(worker);
            return PredictWorker(PredictionScope + ":" + index,
                new StaffModuleIntentMessage
                {
                    Kind = StaffIntentKind.Fire,
                    Index = index,
                },
                worker,
                before,
                () =>
                {
                    // The host ends the interaction as part of firing (HostFire ->
                    // HostEndInteraction), so the client ends it locally too but must not emit a
                    // second EndInteraction intent: OnPressStopInteract is run through
                    // WithPrediction so WorkerStopPatch lets the game method run directly instead
                    // of predicting a competing interaction the host would reject.
                    worker.FireWorker();
                    ReleaseLocalInteraction(index);
                    WithPrediction(worker.OnPressStopInteract);
                    screen.CloseScreen();
                },
                () =>
                {
                    Restore(before, worker);
                    _workerLease.Add(index);
                    _workerBusy[index] = true;
                    WithRemote(worker.OnMousePress);
                });
        }

        private bool InterceptTask(WorkerInteractUIScreen screen, bool isPrimary)
        {
            var worker = StaffModuleInterop.WorkerFrom(StaffModuleInterop.InteractWorker, screen);
            var task = StaffModuleInterop.TaskToSet?.GetValue(screen);
            if (worker == null || !(task is EWorkerTask workerTask))
            {
                return true;
            }

            if (workerTask == EWorkerTask.RestockShelf || workerTask == EWorkerTask.SetPrice
                || workerTask == EWorkerTask.RestockCardDisplay
                || workerTask == EWorkerTask.RefillCardOpener)
            {
                return true;
            }

            var before = CaptureBefore(worker);
            var message = BuildTaskIntent(worker, isPrimary, workerTask);
            return PredictWorker(PredictionScope + ":" + worker.m_WorkerIndex, message, worker, before,
                () =>
                {
                    WithPrediction(() => screen.SetTaskAsPrimaryOrSecondary(isPrimary));
                    ReleaseLocalInteraction(worker.m_WorkerIndex);
                });
        }

        private StaffModuleIntentMessage BuildTaskIntent(Worker worker, bool isPrimary, EWorkerTask task)
        {
            var data = worker.GetWorkerSaveData();
            return new StaffModuleIntentMessage
            {
                Kind = StaffIntentKind.Task,
                Index = worker.m_WorkerIndex,
                PrimaryTask = (byte)(isPrimary ? task : data.primaryTask),
                SecondaryTask = (byte)(isPrimary ? data.secondaryTask : task),
                WorkerTask = (byte)(isPrimary ? task : data.workerTask),
            };
        }

        private bool InterceptOptions(WorkerOptionUIScreen screen, bool noLabel)
        {
            var worker = StaffModuleInterop.WorkerFrom(StaffModuleInterop.OptionWorker, screen);
            if (worker == null)
            {
                return true;
            }

            var before = CaptureBefore(worker);
            var taskIndex = (int)StaffModuleInterop.OptionTaskIndex.GetValue(screen);
            var data = worker.GetWorkerSaveData();
            var message = new StaffModuleIntentMessage
            {
                Kind = StaffIntentKind.Options,
                Index = worker.m_WorkerIndex,
                FillNoLabel = noLabel,
                PrimaryTask = (byte)(worker.GetIsSetTaskSettingPrimarySecondary()
                    ? (EWorkerTask)taskIndex : data.primaryTask),
                SecondaryTask = (byte)(worker.GetIsSetTaskSettingPrimarySecondary()
                    ? data.secondaryTask : (EWorkerTask)taskIndex),
                WorkerTask = (byte)(worker.GetIsSetTaskSettingPrimarySecondary()
                    ? (EWorkerTask)taskIndex : data.workerTask),
                RoundUpPrice = data.isRoundUpPrice,
                AvoidSetCardPrice = data.isAvoidSetCardPrice,
                RoundUpCardPrice = data.isRoundUpCardPrice,
                AvoidSetCardPriceRestock = data.isAvoidSetCardPriceWhileRestock,
                PriceMult = data.setPriceMultiplier,
                CardPriceMult = data.setCardPriceMultiplier,
            };
            return PredictWorker(PredictionScope + ":" + worker.m_WorkerIndex, message, worker, before,
                () =>
                {
                    WithPrediction(() => screen.OnPressRestockShelfWithNoLabel(noLabel));
                    ReleaseLocalInteraction(worker.m_WorkerIndex);
                });
        }

        private bool InterceptPriceOptions(WorkerOptionSetPriceUIScreen screen)
        {
            var worker = StaffModuleInterop.WorkerFrom(StaffModuleInterop.PriceWorker, screen);
            if (worker == null)
            {
                return true;
            }

            var before = CaptureBefore(worker);
            var task = (EWorkerTask)StaffModuleInterop.PriceTask.GetValue(screen);
            var roundUp = (bool)StaffModuleInterop.PriceRoundUp.GetValue(screen);
            var canSet = (bool)StaffModuleInterop.PriceCanSetCard.GetValue(screen);
            var multiplier = (float)StaffModuleInterop.PriceMultiplier.GetValue(screen);
            var data = worker.GetWorkerSaveData();
            var isPrice = task == EWorkerTask.SetPrice;
            var message = new StaffModuleIntentMessage
            {
                Kind = StaffIntentKind.Options,
                Index = worker.m_WorkerIndex,
                PrimaryTask = (byte)(worker.GetIsSetTaskSettingPrimarySecondary() ? task : data.primaryTask),
                SecondaryTask = (byte)(worker.GetIsSetTaskSettingPrimarySecondary()
                    ? data.secondaryTask : task),
                WorkerTask = (byte)(worker.GetIsSetTaskSettingPrimarySecondary() ? task : data.workerTask),
                FillNoLabel = data.isFillShelfWithoutLabel,
                RoundUpPrice = isPrice ? roundUp : data.isRoundUpPrice,
                AvoidSetCardPrice = isPrice ? !canSet : data.isAvoidSetCardPrice,
                RoundUpCardPrice = isPrice ? data.isRoundUpCardPrice : roundUp,
                AvoidSetCardPriceRestock = isPrice
                    ? data.isAvoidSetCardPriceWhileRestock : !canSet,
                PriceMult = isPrice ? multiplier : data.setPriceMultiplier,
                CardPriceMult = isPrice ? data.setCardPriceMultiplier : multiplier,
            };
            return PredictWorker(PredictionScope + ":" + worker.m_WorkerIndex, message, worker, before,
                () =>
                {
                    WithPrediction(screen.OnPressConfirm);
                    ReleaseLocalInteraction(worker.m_WorkerIndex);
                });
        }

        private bool InterceptPackOptions(WorkerSetPackOpenerTypeOptionScreen screen)
        {
            var worker = StaffModuleInterop.WorkerFrom(StaffModuleInterop.PackWorker, screen);
            if (worker == null)
            {
                return true;
            }

            var before = CaptureBefore(worker);
            var enabled = (List<bool>)StaffModuleInterop.PackEnabled.GetValue(screen);
            var current = worker.GetCardPackItemTypeEnabledList();
            var changes = new List<StaffPackChange>();
            for (var i = 0; i < enabled.Count; i++)
            {
                if (i >= current.Count || current[i] != enabled[i])
                {
                    changes.Add(new StaffPackChange { Index = i, Enabled = enabled[i] });
                }
            }

            var data = worker.GetWorkerSaveData();
            var message = new StaffModuleIntentMessage
            {
                Kind = StaffIntentKind.Pack,
                Index = worker.m_WorkerIndex,
                PrimaryTask = (byte)(worker.GetIsSetTaskSettingPrimarySecondary()
                    ? EWorkerTask.RefillCardOpener : data.primaryTask),
                SecondaryTask = (byte)(worker.GetIsSetTaskSettingPrimarySecondary()
                    ? data.secondaryTask : EWorkerTask.RefillCardOpener),
                WorkerTask = (byte)(worker.GetIsSetTaskSettingPrimarySecondary()
                    ? EWorkerTask.RefillCardOpener : data.workerTask),
                PackChanges = changes,
            };
            return PredictWorker(PredictionScope + ":" + worker.m_WorkerIndex, message, worker, before,
                () =>
                {
                    WithPrediction(screen.OnPressConfirm);
                    ReleaseLocalInteraction(worker.m_WorkerIndex);
                });
        }

        private bool HandleWorkerMousePress(Worker worker)
        {
            if (_allowWorkerOpen)
            {
                return true;
            }

            var index = worker.m_WorkerIndex;
            if (_workerLease.Contains(index)
                || _workerBusy.TryGetValue(index, out var occupied) && occupied)
            {
                CoopPlugin.Log.LogInfo("[staff] open blocked index=" + index
                    + " lease=" + _workerLease.Contains(index)
                    + " busy=" + (_workerBusy.TryGetValue(index, out var busy) && busy));
                return false;
            }

            if (!CoopCore.TryGetLocalPlayerPosition(out var position))
            {
                return false;
            }

            PredictionApi.Predict(PredictionScope + ":" + index,
                id => Send(new StaffModuleIntentMessage
                {
                    PredictionId = id,
                    Kind = StaffIntentKind.BeginInteraction,
                    Index = index,
                    Position = position,
                }),
                () =>
                {
                    _workerBusy[index] = true;
                    _workerLease.Add(index);
                    _allowWorkerOpen = true;
                    try
                    {
                        worker.OnMousePress();
                    }
                    finally
                    {
                        _allowWorkerOpen = false;
                    }
                },
                () =>
                {
                    _workerLease.Remove(index);
                    _workerBusy[index] = false;
                    WithRemote(worker.OnPressStopInteract);
                });
            return false;
        }

        private bool HandleWorkerStopInteract(Worker worker)
        {
            var index = worker.m_WorkerIndex;
            if (!_workerLease.Contains(index))
            {
                return true;
            }

            PredictionApi.Predict(PredictionScope + ":" + index,
                id => Send(new StaffModuleIntentMessage
                {
                    PredictionId = id,
                    Kind = StaffIntentKind.EndInteraction,
                    Index = index,
                }),
                () =>
                {
                    _workerLease.Remove(index);
                    _workerBusy[index] = false;
                    WithPrediction(worker.OnPressStopInteract);
                },
                () =>
                {
                    _workerLease.Add(index);
                    _workerBusy[index] = true;
                    WithRemote(worker.OnMousePress);
                });
            return false;
        }

        private void Send(StaffModuleIntentMessage message)
        {
            if (_shutdown)
            {
                throw new InvalidOperationException("Cannot send a Staff intent after shutdown.");
            }

            if (!_joined)
            {
                throw new InvalidOperationException("Cannot send a Staff intent before FullyJoined.");
            }

            if (_context == null || !_context.InGame())
            {
                throw new InvalidOperationException("Cannot send a Staff intent outside the game.");
            }

            _context.Send(1, message);
        }

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            if (!_shutdown)
            {
                ResetWorldState();
                OnReadinessSignal();
            }
        }

        [OnClientDisconnected]
        private void Disconnected(PeerConnection connection, DisconnectInfo _)
        {
            if (connection?.Id == 1)
            {
                _joined = false;
                ResetWorldState();
            }
        }

        private void ResetWorldState()
        {
            _workerBusy.Clear();
            _workerLease.Clear();
            _workerGenerations.Clear();
            ClearDeferredDeltas();
            _pendingBaseline = null;
            _interactScreenCache.Clear();
            _hireScreenCache.Clear();
            _applyingRemote = false;
            _allowWorkerOpen = false;
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            ResetWorldState();
            NpcClientBehaviour.WorkerManagerReady -= OnReadinessSignal;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _context?.Messages.UnregisterAttributedHandlers(this);
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

            _harmony?.UnpatchSelf();
        }

        private void OnDestroy() => Shutdown();

        private static string DeltaKey(StaffModuleDeltaMessage message)
            => message.Index + ":" + (byte)message.Kind + ":" + message.Generation
                + ":" + message.PackIndex + ":" + message.ExperienceTask;

        private void ClearDeferredDeltas()
        {
            foreach (var delta in _deferredDeltas.Values)
                PredictionApi.ConfirmSuperseded(delta.PredictionId);
            _deferredDeltas.Clear();
        }

        [HarmonyPatch(typeof(HireWorkerPanelUI), nameof(HireWorkerPanelUI.OnPressHireButton))]
        private static class HirePatch
        {
            [HarmonyPrefix]
            private static bool Prefix(HireWorkerPanelUI __instance)
                => RouteInteraction(active => active.InterceptHire(__instance));
        }

        [HarmonyPatch(typeof(WorkerInteractUIScreen), nameof(WorkerInteractUIScreen.SetTaskAsPrimaryOrSecondary))]
        private static class TaskPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(WorkerInteractUIScreen __instance, bool isPrimary)
                => RouteInteraction(active => active.InterceptTask(__instance, isPrimary));
        }

        [HarmonyPatch(typeof(WorkerOptionUIScreen), nameof(WorkerOptionUIScreen.OnPressRestockShelfWithNoLabel))]
        private static class OptionPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(WorkerOptionUIScreen __instance, bool isFillShelfWithoutLabel)
                => RouteInteraction(active => active.InterceptOptions(__instance,
                    isFillShelfWithoutLabel));
        }

        [HarmonyPatch(typeof(WorkerOptionSetPriceUIScreen), nameof(WorkerOptionSetPriceUIScreen.OnPressConfirm))]
        private static class PriceOptionPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(WorkerOptionSetPriceUIScreen __instance)
                => RouteInteraction(active => active.InterceptPriceOptions(__instance));
        }

        [HarmonyPatch(typeof(WorkerSetPackOpenerTypeOptionScreen), nameof(WorkerSetPackOpenerTypeOptionScreen.OnPressConfirm))]
        private static class PackOptionPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(WorkerSetPackOpenerTypeOptionScreen __instance)
                => RouteInteraction(active => active.InterceptPackOptions(__instance));
        }

        [HarmonyPatch(typeof(WorkerInteractUIScreen), nameof(WorkerInteractUIScreen.OnPressGiveBonus))]
        private static class BonusPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(WorkerInteractUIScreen __instance)
                => RouteInteraction(active => active.InterceptBonus(__instance));
        }

        [HarmonyPatch(typeof(WorkerInteractUIScreen), nameof(WorkerInteractUIScreen.OnPressFire))]
        private static class FirePatch
        {
            [HarmonyPrefix]
            private static bool Prefix(WorkerInteractUIScreen __instance)
                => RouteInteraction(active => active.InterceptFire(__instance));
        }

        [HarmonyPatch(typeof(Worker), nameof(Worker.OnMousePress))]
        private static class WorkerMousePatch
        {
            [HarmonyPrefix]
            private static bool Prefix(Worker __instance)
                => RouteInteraction(active => active.HandleWorkerMousePress(__instance),
                    _ => __instance == null);
        }

        [HarmonyPatch(typeof(Worker), nameof(Worker.OnPressStopInteract))]
        private static class WorkerStopPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(Worker __instance)
                => RouteInteraction(active => active.HandleWorkerStopInteract(__instance),
                    _ => __instance == null);
        }
    }
}
