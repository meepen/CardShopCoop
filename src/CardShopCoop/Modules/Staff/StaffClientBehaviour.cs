using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Hud;
using CardShopCoop.Modules.Npc;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using CardShopCoop.Util;
using HarmonyLib;
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

        // Non-zero while a converted staff UI action's own game method runs between its capture
        // prefix and its observe postfix. The nested Worker.OnPressStopInteract such an action
        // performs must still run vanilla, but must not register its own intent: the action's
        // prediction already covers the interaction end the host broadcasts with it.
        private int _insideStaffAction;

        /// <summary>Pre-action state carried from a converted capture-only prefix to its observe
        /// postfix. Null means the action was not observable and must not be predicted.</summary>
        private sealed class StaffAction
        {
            public Worker Worker;
            public int Index;
            public StaffModuleEntry Before;
            public StaffModuleIntentMessage Message;
            // The wallet amount vanilla debits for a hire/bonus, captured before the game spends
            // it so a rejected prediction can refund exactly that amount.
            public double Spend;
        }

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
                    PredictionApi.Ack(previous.PredictionId);
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

            // Fired and Interaction deltas only confirm the client's own optimistic action. The
            // local apply already closed/opened the interaction (and released the lease/busy
            // flags) exactly as the host did, so retiring the prediction without replaying it
            // keeps the local interaction as-is.
            if (message.Kind == StaffDeltaKind.Fired || message.Kind == StaffDeltaKind.Interaction)
            {
                PredictionApi.AckOrApply(message.PredictionId, apply);
            }
            else
            {
                // Every other kind carries host-computed state the optimistic run does not
                // produce: _workerGenerations (prediction snapshots leave Generation at 0, see
                // ApplyEntry) and the Hired bootstrap/manager entry. AckOrApply retired the
                // actor's own prediction and returned, losing that state, so Confirm retires it
                // and still runs the authoritative apply.
                PredictionApi.Confirm(message.PredictionId, apply);
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

        /// <summary>Registers a post-hoc prediction for a game change this client just observed from
        /// a hook postfix. The game already applied the mutation locally, so the prediction only
        /// records how to replay and undo it and never re-runs it.</summary>
        private void RegisterPostHoc(string key, StaffModuleIntentMessage message, Action apply,
            Action undo)
        {
            PredictionApi.Predict(key,
                id =>
                {
                    message.PredictionId = id;
                    Send(message);
                },
                apply,
                undo);
        }

        /// <summary>Re-applies the wallet debit of a replayed staff action through the game's own
        /// coin event. Queued during reconciliation, so the Hud economy observer does not forward it
        /// as a second contribution.</summary>
        private static void Charge(double amount)
        {
            if (amount > 0.0001d)
            {
                CEventManager.QueueEvent(new CEventPlayer_ReduceCoin((float)amount));
            }
        }

        /// <summary>Reverses the wallet debit of a rejected staff action through the game's own coin
        /// event (an instant refund). Also queued during reconciliation.</summary>
        private static void Refund(double amount)
        {
            if (amount > 0.0001d)
            {
                CEventManager.QueueEvent(new CEventPlayer_AddCoin((float)amount, true));
            }
        }

        /// <summary>True when a client hook may register a prediction for the game change it is
        /// about to observe: joined and in-game, not replaying/reconciling a prediction, not
        /// applying remote state, and not inside a converted staff action. The game method always
        /// runs; only the prediction is skipped.</summary>
        private bool CanObserve()
            => !_shutdown && _joined && _context != null && _context.InGame()
                && !_applyingRemote && _applyingPrediction == 0 && _insideStaffAction == 0
                && !PredictionApi.IsReconciling;

        /// <summary>Capture-only prefix for a local hire: snapshot the manager slot before vanilla
        /// OnPressHireButton spends the wallet and activates the worker. The postfix registers one
        /// post-hoc Hire prediction only when the game really hired.</summary>
        private StaffAction BeginHireAction(HireWorkerPanelUI panel)
        {
            if (!CanObserve() || panel == null || StaffModuleInterop.PanelIsHired == null
                || StaffModuleInterop.PanelIndex == null)
            {
                return null;
            }

            if ((bool)StaffModuleInterop.PanelIsHired.GetValue(panel))
            {
                return null;
            }

            var index = (int)StaffModuleInterop.PanelIndex.GetValue(panel);
            var manager = StaffModuleInterop.FindWorkerManager();
            var spend = manager != null && manager.m_WorkerDataList != null
                && index >= 0 && index < manager.m_WorkerDataList.Count
                && manager.m_WorkerDataList[index] != null
                ? manager.m_WorkerDataList[index].hiringCost : 0f;
            return new StaffAction
            {
                Index = index,
                Before = CaptureManagerEntry(index),
                Spend = spend,
                Message = new StaffModuleIntentMessage
                {
                    Kind = StaffIntentKind.Hire,
                    Index = index,
                },
            };
        }

        private void EndHireAction(HireWorkerPanelUI panel, StaffAction state)
        {
            if (state == null || !CPlayerData.GetIsWorkerHired(state.Index))
            {
                return;
            }

            var index = state.Index;
            var before = state.Before;
            var spend = state.Spend;
            var predicted = before;
            predicted.Hired = true;
            predicted.HasData = true;
            predicted.PrimaryTask = (byte)EWorkerTask.Rest;
            predicted.SecondaryTask = (byte)EWorkerTask.Rest;
            predicted.WorkerTask = (byte)EWorkerTask.Rest;
            predicted.CurrentState = (byte)EWorkerState.Idle;
            RegisterPostHoc(PredictionScope + ":" + index, state.Message,
                () =>
                {
                    // Replay re-hires through the module path, which does not spend, so mirror the
                    // wallet debit the host owns for this hire.
                    ApplyEntry(index, predicted);
                    Charge(spend);
                },
                () =>
                {
                    // The vanilla OnPressHireButton already debited the guest's mirror (the Hud
                    // observer was suppressed), so a rejection must refund it.
                    ApplyEntry(index, before);
                    Refund(spend);
                });
            _context.SetStatusLine?.Invoke("hired - starting work at the host's shop", 4f);
        }

        /// <summary>Capture-only prefix for a salary bonus: snapshot the worker before vanilla
        /// OnPressGiveBonus spends the wallet and boosts the worker. The postfix registers one
        /// post-hoc Bonus prediction only when the bonus count actually rose.</summary>
        private StaffAction BeginBonusAction(WorkerInteractUIScreen screen)
        {
            if (!CanObserve() || screen == null)
            {
                return null;
            }

            var worker = StaffModuleInterop.WorkerFrom(StaffModuleInterop.InteractWorker, screen);
            if (worker == null)
            {
                return null;
            }

            return new StaffAction
            {
                Worker = worker,
                Index = worker.m_WorkerIndex,
                Before = CaptureBefore(worker),
                // Vanilla OnPressGiveBonus debits the worker's per-day salary cost, which is also
                // the fee the host owns for this intent.
                Spend = worker.GetWorkerData()?.costPerDay ?? 0f,
                Message = new StaffModuleIntentMessage
                {
                    Kind = StaffIntentKind.Bonus,
                    Index = worker.m_WorkerIndex,
                },
            };
        }

        private void EndBonusAction(WorkerInteractUIScreen screen, StaffAction state)
        {
            if (state == null)
            {
                return;
            }

            var worker = state.Worker;
            var index = worker.m_WorkerIndex;
            var spend = state.Spend;
            // An unaffordable or maxed bonus changes nothing; vanilla only shows its own popup.
            if (worker.GetBonusBoostedCount() <= state.Before.BonusCount)
            {
                return;
            }

            RegisterPostHoc(PredictionScope + ":" + index, state.Message,
                () => WithPrediction(() =>
                {
                    // GiveSalaryBonus does not spend; mirror the wallet debit the host owns.
                    worker.GiveSalaryBonus();
                    RefreshWorker(worker);
                    RefreshInteractScreen(screen, index);
                    Charge(spend);
                }),
                () =>
                {
                    // The vanilla OnPressGiveBonus already debited the guest's mirror (the Hud
                    // observer was suppressed), so a rejection must refund it.
                    Restore(state.Before, worker);
                    Refund(spend);
                });
        }

        /// <summary>Capture-only prefix for a local fire: snapshot the worker before vanilla
        /// OnPressFire (which fires the worker, ends the interaction and closes the screen). The
        /// postfix registers exactly one post-hoc Fire prediction for the change the game made.</summary>
        private StaffAction BeginFireAction(WorkerInteractUIScreen screen)
        {
            if (!CanObserve() || screen == null)
            {
                return null;
            }

            var worker = StaffModuleInterop.WorkerFrom(StaffModuleInterop.InteractWorker, screen);
            if (worker == null)
            {
                return null;
            }

            _insideStaffAction++;
            return new StaffAction
            {
                Worker = worker,
                Before = CaptureBefore(worker),
                Message = new StaffModuleIntentMessage
                {
                    Kind = StaffIntentKind.Fire,
                    Index = worker.m_WorkerIndex,
                },
            };
        }

        private void EndFireAction(WorkerInteractUIScreen screen, StaffAction state)
        {
            if (state == null)
            {
                return;
            }

            _insideStaffAction--;
            var worker = state.Worker;
            var index = worker.m_WorkerIndex;
            RegisterPostHoc(PredictionScope + ":" + index, state.Message,
                () => WithPrediction(() =>
                {
                    // The host ends the interaction as part of firing (HostFire ->
                    // HostEndInteraction), so the game path replays the fire, the interaction end
                    // and the close together. OnPressStopInteract runs inside WithPrediction so
                    // WorkerStopPatch lets the game method run instead of predicting a competing
                    // interaction the host would reject.
                    worker.FireWorker();
                    worker.OnPressStopInteract();
                    screen.CloseScreen();
                }),
                () =>
                {
                    Restore(state.Before, worker);
                    _workerLease.Add(index);
                    _workerBusy[index] = true;
                    WithRemote(worker.OnMousePress);
                });
            ReleaseLocalInteraction(index);
        }

        /// <summary>Capture-only prefix for a task choice: snapshot the worker before vanilla
        /// SetTaskAsPrimaryOrSecondary sets the task, ends the interaction and closes the screen.
        /// The four tasks that only open a sub-screen mutate nothing yet, so they are not observed.</summary>
        private StaffAction BeginTaskAction(WorkerInteractUIScreen screen, bool isPrimary)
        {
            if (!CanObserve() || screen == null)
            {
                return null;
            }

            var worker = StaffModuleInterop.WorkerFrom(StaffModuleInterop.InteractWorker, screen);
            var task = StaffModuleInterop.TaskToSet?.GetValue(screen);
            if (worker == null || task is not EWorkerTask workerTask)
            {
                return null;
            }

            if (workerTask == EWorkerTask.RestockShelf || workerTask == EWorkerTask.SetPrice
                || workerTask == EWorkerTask.RestockCardDisplay
                || workerTask == EWorkerTask.RefillCardOpener)
            {
                return null;
            }

            _insideStaffAction++;
            return new StaffAction
            {
                Worker = worker,
                Before = CaptureBefore(worker),
                Message = BuildTaskIntent(worker, isPrimary, workerTask),
            };
        }

        private void EndTaskAction(WorkerInteractUIScreen screen, bool isPrimary, StaffAction state)
        {
            if (state == null)
            {
                return;
            }

            _insideStaffAction--;
            var worker = state.Worker;
            RegisterPostHoc(PredictionScope + ":" + worker.m_WorkerIndex, state.Message,
                () => WithPrediction(() => screen.SetTaskAsPrimaryOrSecondary(isPrimary)),
                () => Restore(state.Before, worker));
            ReleaseLocalInteraction(worker.m_WorkerIndex);
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

        private StaffModuleIntentMessage BuildOptionsIntent(WorkerOptionUIScreen screen, Worker worker,
            bool noLabel)
        {
            var taskIndex = (int)StaffModuleInterop.OptionTaskIndex.GetValue(screen);
            var data = worker.GetWorkerSaveData();
            return new StaffModuleIntentMessage
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
        }

        /// <summary>Capture-only prefix for a restock-option choice: snapshot the worker before
        /// vanilla OnPressRestockShelfWithNoLabel sets the flag/task, ends the interaction and
        /// closes the screen. The postfix registers one post-hoc Options prediction.</summary>
        private StaffAction BeginOptionAction(WorkerOptionUIScreen screen, bool noLabel)
        {
            if (!CanObserve() || screen == null)
            {
                return null;
            }

            var worker = StaffModuleInterop.WorkerFrom(StaffModuleInterop.OptionWorker, screen);
            if (worker == null)
            {
                return null;
            }

            _insideStaffAction++;
            return new StaffAction
            {
                Worker = worker,
                Before = CaptureBefore(worker),
                Message = BuildOptionsIntent(screen, worker, noLabel),
            };
        }

        private void EndOptionAction(WorkerOptionUIScreen screen, bool noLabel, StaffAction state)
        {
            if (state == null)
            {
                return;
            }

            _insideStaffAction--;
            var worker = state.Worker;
            RegisterPostHoc(PredictionScope + ":" + worker.m_WorkerIndex, state.Message,
                () => WithPrediction(() => screen.OnPressRestockShelfWithNoLabel(noLabel)),
                () => Restore(state.Before, worker));
            ReleaseLocalInteraction(worker.m_WorkerIndex);
        }

        private StaffModuleIntentMessage BuildPriceIntent(WorkerOptionSetPriceUIScreen screen,
            Worker worker)
        {
            var task = (EWorkerTask)StaffModuleInterop.PriceTask.GetValue(screen);
            var roundUp = (bool)StaffModuleInterop.PriceRoundUp.GetValue(screen);
            var canSet = (bool)StaffModuleInterop.PriceCanSetCard.GetValue(screen);
            var multiplier = (float)StaffModuleInterop.PriceMultiplier.GetValue(screen);
            var data = worker.GetWorkerSaveData();
            var isPrice = task == EWorkerTask.SetPrice;
            return new StaffModuleIntentMessage
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
        }

        /// <summary>Capture-only prefix for a price-option confirm: snapshot the worker before
        /// vanilla OnPressConfirm updates the price option/task, ends the interaction and closes
        /// the screen. The postfix registers one post-hoc Options prediction.</summary>
        private StaffAction BeginPriceAction(WorkerOptionSetPriceUIScreen screen)
        {
            if (!CanObserve() || screen == null)
            {
                return null;
            }

            var worker = StaffModuleInterop.WorkerFrom(StaffModuleInterop.PriceWorker, screen);
            if (worker == null)
            {
                return null;
            }

            _insideStaffAction++;
            return new StaffAction
            {
                Worker = worker,
                Before = CaptureBefore(worker),
                Message = BuildPriceIntent(screen, worker),
            };
        }

        private void EndPriceAction(WorkerOptionSetPriceUIScreen screen, StaffAction state)
        {
            if (state == null)
            {
                return;
            }

            _insideStaffAction--;
            var worker = state.Worker;
            RegisterPostHoc(PredictionScope + ":" + worker.m_WorkerIndex, state.Message,
                () => WithPrediction(screen.OnPressConfirm),
                () => Restore(state.Before, worker));
            ReleaseLocalInteraction(worker.m_WorkerIndex);
        }

        private StaffModuleIntentMessage BuildPackIntent(WorkerSetPackOpenerTypeOptionScreen screen,
            Worker worker)
        {
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
            return new StaffModuleIntentMessage
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
        }

        /// <summary>Capture-only prefix for a pack-option confirm: snapshot the worker before
        /// vanilla OnPressConfirm applies the pack-type changes and task, ends the interaction and
        /// closes the screen. The postfix registers one post-hoc Pack prediction.</summary>
        private StaffAction BeginPackAction(WorkerSetPackOpenerTypeOptionScreen screen)
        {
            if (!CanObserve() || screen == null)
            {
                return null;
            }

            var worker = StaffModuleInterop.WorkerFrom(StaffModuleInterop.PackWorker, screen);
            if (worker == null)
            {
                return null;
            }

            _insideStaffAction++;
            return new StaffAction
            {
                Worker = worker,
                Before = CaptureBefore(worker),
                Message = BuildPackIntent(screen, worker),
            };
        }

        private void EndPackAction(WorkerSetPackOpenerTypeOptionScreen screen, StaffAction state)
        {
            if (state == null)
            {
                return;
            }

            _insideStaffAction--;
            var worker = state.Worker;
            RegisterPostHoc(PredictionScope + ":" + worker.m_WorkerIndex, state.Message,
                () => WithPrediction(screen.OnPressConfirm),
                () => Restore(state.Before, worker));
            ReleaseLocalInteraction(worker.m_WorkerIndex);
        }

        /// <summary>Capture-only prefix for a local worker click: vanilla OnMousePress owns the
        /// interaction open, and the postfix registers one post-hoc BeginInteraction prediction for
        /// the host's lease. A click that would not start a new interaction (already leased, no
        /// position) is not observed; vanilla still runs.</summary>
        private StaffAction BeginWorkerMouse(Worker worker)
        {
            if (!CanObserve() || _allowWorkerOpen || worker == null)
            {
                return null;
            }

            var index = worker.m_WorkerIndex;
            if (_workerLease.Contains(index))
            {
                return null;
            }

            if (!CoopCore.TryGetLocalPlayerPosition(out var position))
            {
                return null;
            }

            return new StaffAction
            {
                Worker = worker,
                Index = index,
                Message = new StaffModuleIntentMessage
                {
                    Kind = StaffIntentKind.BeginInteraction,
                    Index = index,
                    Position = position,
                },
            };
        }

        private void EndWorkerMouse(StaffAction state)
        {
            if (state == null)
            {
                return;
            }

            var worker = state.Worker;
            var index = state.Index;
            _workerBusy[index] = true;
            _workerLease.Add(index);
            RegisterPostHoc(PredictionScope + ":" + index, state.Message,
                () => WithPrediction(() =>
                {
                    _workerBusy[index] = true;
                    _workerLease.Add(index);
                    worker.OnMousePress();
                }),
                () =>
                {
                    _workerLease.Remove(index);
                    _workerBusy[index] = false;
                    WithRemote(worker.OnPressStopInteract);
                });
        }

        /// <summary>Capture-only prefix for a local interaction end: vanilla OnPressStopInteract
        /// exits the interaction, and the postfix registers one post-hoc EndInteraction prediction
        /// once the client actually holds the lease. The internal stop calls a task/option/pack/fire
        /// screen makes are excluded by <see cref="CanObserve"/>'s action guard.</summary>
        private StaffAction BeginWorkerStop(Worker worker)
        {
            if (!CanObserve() || worker == null || !_workerLease.Contains(worker.m_WorkerIndex))
            {
                return null;
            }

            return new StaffAction
            {
                Worker = worker,
                Index = worker.m_WorkerIndex,
                Message = new StaffModuleIntentMessage
                {
                    Kind = StaffIntentKind.EndInteraction,
                    Index = worker.m_WorkerIndex,
                },
            };
        }

        private void EndWorkerStop(StaffAction state)
        {
            if (state == null)
            {
                return;
            }

            var worker = state.Worker;
            var index = state.Index;
            _workerLease.Remove(index);
            _workerBusy[index] = false;
            RegisterPostHoc(PredictionScope + ":" + index, state.Message,
                () => WithPrediction(() =>
                {
                    _workerLease.Remove(index);
                    _workerBusy[index] = false;
                    worker.OnPressStopInteract();
                }),
                () =>
                {
                    _workerLease.Add(index);
                    _workerBusy[index] = true;
                    WithRemote(worker.OnMousePress);
                });
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
            _insideStaffAction = 0;
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
                PredictionApi.Ack(delta.PredictionId);
            _deferredDeltas.Clear();
        }

        [HarmonyPatch(typeof(HireWorkerPanelUI), nameof(HireWorkerPanelUI.OnPressHireButton))]
        private static class HirePatch
        {
            [HarmonyPrefix]
            private static void Prefix(HireWorkerPanelUI __instance, out StaffAction __state)
            {
                __state = _active?.BeginHireAction(__instance);
                if (__state != null)
                {
                    // OnPressHireButton spends the wallet through vanilla. The host owns that debit
                    // for the guest's Hire intent, so the Hud observer must not also forward it.
                    EconomyActionScope.Enter();
                }
            }

            [HarmonyPostfix]
            private static void Postfix(HireWorkerPanelUI __instance, StaffAction __state)
                => _active?.EndHireAction(__instance, __state);

            [HarmonyFinalizer]
            private static void Finalizer(StaffAction __state)
            {
                if (__state != null)
                {
                    EconomyActionScope.Exit();
                }
            }
        }

        [HarmonyPatch(typeof(WorkerInteractUIScreen), nameof(WorkerInteractUIScreen.SetTaskAsPrimaryOrSecondary))]
        private static class TaskPatch
        {
            [HarmonyPrefix]
            private static void Prefix(WorkerInteractUIScreen __instance, bool isPrimary,
                out StaffAction __state)
                => __state = _active?.BeginTaskAction(__instance, isPrimary);

            [HarmonyPostfix]
            private static void Postfix(WorkerInteractUIScreen __instance, bool isPrimary,
                StaffAction __state)
                => _active?.EndTaskAction(__instance, isPrimary, __state);
        }

        [HarmonyPatch(typeof(WorkerOptionUIScreen), nameof(WorkerOptionUIScreen.OnPressRestockShelfWithNoLabel))]
        private static class OptionPatch
        {
            [HarmonyPrefix]
            private static void Prefix(WorkerOptionUIScreen __instance, bool isFillShelfWithoutLabel,
                out StaffAction __state)
                => __state = _active?.BeginOptionAction(__instance, isFillShelfWithoutLabel);

            [HarmonyPostfix]
            private static void Postfix(WorkerOptionUIScreen __instance, bool isFillShelfWithoutLabel,
                StaffAction __state)
                => _active?.EndOptionAction(__instance, isFillShelfWithoutLabel, __state);
        }

        [HarmonyPatch(typeof(WorkerOptionSetPriceUIScreen), nameof(WorkerOptionSetPriceUIScreen.OnPressConfirm))]
        private static class PriceOptionPatch
        {
            [HarmonyPrefix]
            private static void Prefix(WorkerOptionSetPriceUIScreen __instance,
                out StaffAction __state)
                => __state = _active?.BeginPriceAction(__instance);

            [HarmonyPostfix]
            private static void Postfix(WorkerOptionSetPriceUIScreen __instance, StaffAction __state)
                => _active?.EndPriceAction(__instance, __state);
        }

        [HarmonyPatch(typeof(WorkerSetPackOpenerTypeOptionScreen), nameof(WorkerSetPackOpenerTypeOptionScreen.OnPressConfirm))]
        private static class PackOptionPatch
        {
            [HarmonyPrefix]
            private static void Prefix(WorkerSetPackOpenerTypeOptionScreen __instance,
                out StaffAction __state)
                => __state = _active?.BeginPackAction(__instance);

            [HarmonyPostfix]
            private static void Postfix(WorkerSetPackOpenerTypeOptionScreen __instance,
                StaffAction __state)
                => _active?.EndPackAction(__instance, __state);
        }

        [HarmonyPatch(typeof(WorkerInteractUIScreen), nameof(WorkerInteractUIScreen.OnPressGiveBonus))]
        private static class BonusPatch
        {
            [HarmonyPrefix]
            private static void Prefix(WorkerInteractUIScreen __instance, out StaffAction __state)
            {
                __state = _active?.BeginBonusAction(__instance);
                if (__state != null)
                {
                    // OnPressGiveBonus spends the wallet through vanilla. The host owns that debit
                    // for the guest's Bonus intent, so the Hud observer must not also forward it.
                    EconomyActionScope.Enter();
                }
            }

            [HarmonyPostfix]
            private static void Postfix(WorkerInteractUIScreen __instance, StaffAction __state)
                => _active?.EndBonusAction(__instance, __state);

            [HarmonyFinalizer]
            private static void Finalizer(StaffAction __state)
            {
                if (__state != null)
                {
                    EconomyActionScope.Exit();
                }
            }
        }

        [HarmonyPatch(typeof(WorkerInteractUIScreen), nameof(WorkerInteractUIScreen.OnPressFire))]
        private static class FirePatch
        {
            [HarmonyPrefix]
            private static void Prefix(WorkerInteractUIScreen __instance, out StaffAction __state)
                => __state = _active?.BeginFireAction(__instance);

            [HarmonyPostfix]
            private static void Postfix(WorkerInteractUIScreen __instance, StaffAction __state)
                => _active?.EndFireAction(__instance, __state);
        }

        [HarmonyPatch(typeof(Worker), nameof(Worker.OnMousePress))]
        private static class WorkerMousePatch
        {
            [HarmonyPrefix]
            private static void Prefix(Worker __instance, out StaffAction __state)
                => __state = _active?.BeginWorkerMouse(__instance);

            [HarmonyPostfix]
            private static void Postfix(StaffAction __state)
                => _active?.EndWorkerMouse(__state);
        }

        [HarmonyPatch(typeof(Worker), nameof(Worker.OnPressStopInteract))]
        private static class WorkerStopPatch
        {
            [HarmonyPrefix]
            private static void Prefix(Worker __instance, out StaffAction __state)
                => __state = _active?.BeginWorkerStop(__instance);

            [HarmonyPostfix]
            private static void Postfix(StaffAction __state)
                => _active?.EndWorkerStop(__state);
        }
    }
}
