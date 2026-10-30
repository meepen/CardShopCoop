using System;
using CardShopCoop.Api;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Hud;
using CardShopCoop.Modules.Npc;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using CoopBehaviour = CardShopCoop.Runtime.CoopBehaviour;
using CardShopCoop.Util;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Staff
{
    /// <summary>Predictive worker UI and application of keyed host operations.</summary>
    [ClientBehaviour]
    public sealed class StaffClientBehaviour : CoopBehaviour
    {
        private static StaffClientBehaviour _active;
        private static bool _applyingRemote;
        private static bool _allowWorkerOpen;

        private readonly Dictionary<Worker, bool> _workerBusy = new();
        private readonly HashSet<Worker> _workerLease = new();
        private readonly Dictionary<Worker, uint> _workerGenerations = new();
        private readonly Dictionary<string, StaffModuleDeltaMessage> _deferredDeltas = new();
        private StaffModuleBaselineMessage _pendingBaseline;
        private bool _shutdown;
        private bool _initialized;
        private bool _joined;
        private int _applyingPrediction;

        // Non-zero while a converted staff UI action's own game method runs between its capture
        // prefix and its observe postfix. The nested Worker.OnPressStopInteract such an action
        // performs must still run vanilla, but must not register its own intent: the action's
        // prediction already covers the interaction end the host broadcasts with it.
        private int _insideStaffAction;

        /// <summary>Pre-action state carried from a converted capture-only prefix to its observe
        /// postfix. Null means the action was not observable and must not be predicted.</summary>
        internal sealed class StaffAction
        {
            /// <summary>The live worker the action targets. Null for a hire, which addresses the
            /// save-data slot in <see cref="Index"/> instead of a live entity.</summary>
            public Worker Worker;
            /// <summary>The save-data slot for a hire; otherwise the live worker's index.</summary>
            public int Index;
            public StaffModuleEntry Before;
            public StaffModuleIntentMessage Message;
            // The wallet amount vanilla debits for a hire/bonus, captured before the game spends
            // it so a rejected prediction can refund exactly that amount.
            public double Spend;

            /// <summary>Reverses the optimistic local change. Local-only.</summary>
            public Action Undo;
        }

        private sealed class InteractScreenCache : Cached<WorkerInteractUIScreen>
        {
            protected override WorkerInteractUIScreen GetRawValue()
                => FindFirstObjectByType<WorkerInteractUIScreen>(UnityEngine.FindObjectsInactive.Include);
        }

        private sealed class HireScreenCache : Cached<HireWorkerScreen>
        {
            protected override HireWorkerScreen GetRawValue()
                => FindFirstObjectByType<HireWorkerScreen>(UnityEngine.FindObjectsInactive.Include);
        }

        private readonly InteractScreenCache _interactScreenCache = new();
        private readonly HireScreenCache _hireScreenCache = new();

        private void OnEnable()
        {
            if (_shutdown || _initialized)
            {
                return;
            }

            _active = this;
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
            _initialized = true;
        }

        private void Patch(Type patchType)
            => Harmony.CreateClassProcessor(patchType).Patch();

        [OnFullyJoined]
        private void Joined(PeerConnection _)
        {
            _joined = true;
            TryApplyPending();
        }

        [MessageHandler]
        private void HandleBaseline(MessageContext _, StaffModuleBaselineMessage message)
        {
            _pendingBaseline = message;
            TryApplyPending();
        }

        [MessageHandler]
        private void HandleDelta(MessageContext _, StaffModuleDeltaMessage message)
        {
            if (message == null || message.Worker == null)
            {
                CoopPlugin.Log.LogWarning("[staff] client dropped delta with no live worker.");
                return;
            }

            if (!ApplyDelta(message))
            {
                var key = DeltaKey(message);
                _deferredDeltas[key] = message;
                CoopPlugin.Log.LogInfo("[staff] deferred " + message.Kind + " worker="
                    + message.Worker.name + " generation=" + message.Generation + " known="
                    + (_workerGenerations.TryGetValue(message.Worker, out var known)
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
            if (_shutdown || !RuntimeContext.InGame() || !IsSceneReady())
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
            if (_pendingBaseline == null || !RuntimeContext.InGame() || !IsSceneReady())
            {
                return;
            }

            _applyingRemote = true;
            try
            {
                _workerGenerations.Clear();
                for (var i = 0; i < _pendingBaseline.Entries.Count; i++)
                {
                    ApplyEntry(_pendingBaseline.Entries[i]);
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
            if (_shutdown || !RuntimeContext.InGame() || !IsSceneReady())
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

            // Every delta carries host-computed state the optimistic run does not produce
            // (_workerGenerations, the Hired bootstrap/manager entry, the interaction grant), so
            // the action response always applies every delta it contains; a remote or host-local
            // delta takes the same apply path.
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

            apply();
            return true;
        }

        private bool IsWorkerGenerationReady(StaffModuleDeltaMessage message)
        {
            if (message.Kind == StaffDeltaKind.Hired || message.Kind == StaffDeltaKind.Fired)
            {
                return true;
            }

            return _workerGenerations.TryGetValue(message.Worker, out var generation)
                && generation == message.Generation;
        }

        private void ApplyDeltaCore(StaffModuleDeltaMessage message)
        {
            switch (message.Kind)
            {
                case StaffDeltaKind.Hired:
                    _workerGenerations[message.Worker] = message.Generation;
                    ApplyEntry(message.Bootstrap);
                    break;
                case StaffDeltaKind.Fired:
                    _workerGenerations[message.Worker] = message.Generation;
                    EnsureHiredSlot(message.Worker.m_WorkerIndex);
                    CPlayerData.SetIsWorkerHired(message.Worker.m_WorkerIndex, false);
                    message.Worker.FireWorker();
                    RefreshWorker(message.Worker);
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

        private void ApplyEntry(StaffModuleEntry entry)
        {
            var worker = entry.Worker;
            if (worker == null)
            {
                CoopPlugin.Log.LogWarning("[staff] client dropped worker entry with no live worker.");
                return;
            }

            ApplyEntry(worker.m_WorkerIndex, entry);
        }

        /// <summary>Applies one manager slot. The slot index is authoritative, so a hire
        /// prediction can restore the save-data entry before any live worker is replicated.</summary>
        private void ApplyEntry(int index, StaffModuleEntry entry)
        {
            if (index < 0)
            {
                CoopPlugin.Log.LogWarning("[staff] client dropped worker entry with no slot index.");
                return;
            }

            var worker = entry.Worker;
            // Prediction snapshots (CaptureEntry/CaptureWorkerEntry) leave Generation at its
            // default 0 because they describe only worker data. Never overwrite the tracked
            // generation with that default: doing so makes every later Task/Options/Bonus/
            // Experience delta for the worker fail the generation check and defer forever.
            if (entry.Generation != 0 && worker != null)
            {
                _workerGenerations[worker] = entry.Generation;
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
                if (worker != null)
                {
                    StaffModuleInterop.ApplyEntryToWorker(worker, entry);
                }

                RefreshInteractScreen(_interactScreenCache.Get(), index);
            }

            RefreshHirePanels();
        }

        private void ApplyTask(StaffModuleDeltaMessage message)
        {
            var worker = message.Worker;
            worker.SetTask((EWorkerTask)message.PrimaryTask);
            worker.SetLastTask((EWorkerTask)message.WorkerTask);
            worker.SetSecondaryTask((EWorkerTask)message.SecondaryTask);
            RefreshWorker(worker);
        }

        private void ApplyOptions(StaffModuleDeltaMessage message)
        {
            var worker = message.Worker;
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
            var worker = message.Worker;
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
            var worker = message.Worker;
            worker.m_BonusBoostedCount = message.BonusCount;
            worker.m_IsBonusBoosted = message.BonusBoosted;
            RefreshWorker(worker);
            RefreshInteractScreen(_interactScreenCache.Get(), worker.m_WorkerIndex);
        }

        private void ApplyExperience(StaffModuleDeltaMessage message)
        {
            var worker = message.Worker;
            StaffModuleInterop.SetExperience(worker, (EWorkerTask)message.ExperienceTask,
                message.ExperienceAmount);
            RefreshWorker(worker);
        }

        private void ApplyInteraction(StaffModuleDeltaMessage message)
        {
            var worker = message.Worker;
            _workerBusy[worker] = message.Occupied;
            if (!message.Occupied)
            {
                _workerLease.Remove(worker);
                WithRemote(worker.OnPressStopInteract);
                return;
            }

            if (!message.Granted)
            {
                return;
            }

            _workerLease.Add(worker);
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
        private void ReleaseLocalInteraction(Worker worker)
        {
            if (worker == null)
            {
                return;
            }

            _workerLease.Remove(worker);
            _workerBusy[worker] = false;
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
                ApplyEntry(entry);
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        /// <summary>Registers a prediction for a game change this client just observed from a hook
        /// postfix. The game already applied the mutation locally, so the action only records how
        /// to reverse it and settles against the host's response deltas.</summary>
        private void RegisterPostHoc(StaffAction state, Action apply, Action undo)
        {
            if (state?.Message == null)
            {
                return;
            }

            state.Undo = undo;
            state.Message.Action = state;
            Send(state.Message);
        }

        /// <summary>Reverses one refused staff action from the message's undo hook.</summary>
        internal static void UndoAction(StaffAction state)
            => state?.Undo?.Invoke();

        /// <summary>Applies every authoritative delta the host returned for one staff action.</summary>
        internal static void MergeAction(StaffAction state, ActionResponseMessage response)
        {
            var client = _active;
            if (client == null || client._shutdown
                || response is not StaffIntentResponse result)
            {
                return;
            }

            for (var i = 0; i < result.Deltas.Count; i++)
            {
                var delta = result.Deltas[i];
                if (delta == null || delta.Worker == null)
                {
                    continue;
                }

                if (!client.ApplyDelta(delta))
                {
                    client._deferredDeltas[DeltaKey(delta)] = delta;
                }
            }
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
            => !_shutdown && _joined && RuntimeContext.InGame()
                && !_applyingRemote && _applyingPrediction == 0 && _insideStaffAction == 0
                && !ReentrancyGuard.Suppressed(this);

        /// <summary>Capture-only prefix for a local hire: snapshot the manager slot before vanilla
        /// OnPressHireButton spends the wallet and activates the worker. A hire addresses a
        /// save-data slot by index, not a live entity: an un-hired worker has no replicated
        /// identity yet. The postfix registers one post-hoc Hire prediction only when the game
        /// really hired.</summary>
        private StaffAction BeginHireAction(HireWorkerPanelUI panel)
        {
            if (!CanObserve())
            {
                return null;
            }

            if (panel == null || StaffModuleInterop.PanelIsHired == null
                || StaffModuleInterop.PanelIndex == null)
            {
                CoopPlugin.Log.LogWarning("[staff] hire panel is not addressable; not forwarding.");
                return null;
            }

            if ((bool)StaffModuleInterop.PanelIsHired.GetValue(panel))
            {
                return null;
            }

            var index = (int)StaffModuleInterop.PanelIndex.GetValue(panel);
            var manager = StaffModuleInterop.FindWorkerManager();
            if (!StaffModuleInterop.IsAddressable(manager, index)
                || manager.m_WorkerDataList[index] == null)
            {
                CoopPlugin.Log.LogWarning("[staff] hire slot index=" + index
                    + " is not addressable; not forwarding.");
                return null;
            }

            return new StaffAction
            {
                Index = index,
                Before = CaptureManagerEntry(index),
                Spend = manager.m_WorkerDataList[index].hiringCost,
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
            RegisterPostHoc(state,
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
            RuntimeContext.SetStatusLine?.Invoke("hired - starting work at the host's shop", 4f);
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
                    Worker = worker,
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

            RegisterPostHoc(state,
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
                    Worker = worker,
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
            RegisterPostHoc(state,
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
                    _workerLease.Add(worker);
                    _workerBusy[worker] = true;
                    WithRemote(worker.OnMousePress);
                });
            ReleaseLocalInteraction(worker);
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
            RegisterPostHoc(state,
                () => WithPrediction(() => screen.SetTaskAsPrimaryOrSecondary(isPrimary)),
                () => Restore(state.Before, worker));
            ReleaseLocalInteraction(worker);
        }

        private StaffModuleIntentMessage BuildTaskIntent(Worker worker, bool isPrimary, EWorkerTask task)
        {
            var data = worker.GetWorkerSaveData();
            return new StaffModuleIntentMessage
            {
                Kind = StaffIntentKind.Task,
                Worker = worker,
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
                Worker = worker,
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
            RegisterPostHoc(state,
                () => WithPrediction(() => screen.OnPressRestockShelfWithNoLabel(noLabel)),
                () => Restore(state.Before, worker));
            ReleaseLocalInteraction(worker);
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
                Worker = worker,
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
            RegisterPostHoc(state,
                () => WithPrediction(screen.OnPressConfirm),
                () => Restore(state.Before, worker));
            ReleaseLocalInteraction(worker);
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
                Worker = worker,
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
            RegisterPostHoc(state,
                () => WithPrediction(screen.OnPressConfirm),
                () => Restore(state.Before, worker));
            ReleaseLocalInteraction(worker);
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

            if (_workerLease.Contains(worker))
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
                Index = worker.GetInstanceID(),
                Message = new StaffModuleIntentMessage
                {
                    Kind = StaffIntentKind.BeginInteraction,
                    Worker = worker,
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
            _workerBusy[worker] = true;
            _workerLease.Add(worker);
            RegisterPostHoc(state,
                () => WithPrediction(() =>
                {
                    _workerBusy[worker] = true;
                    _workerLease.Add(worker);
                    worker.OnMousePress();
                }),
                () =>
                {
                    _workerLease.Remove(worker);
                    _workerBusy[worker] = false;
                    WithRemote(worker.OnPressStopInteract);
                });
        }

        /// <summary>Capture-only prefix for a local interaction end: vanilla OnPressStopInteract
        /// exits the interaction, and the postfix registers one post-hoc EndInteraction prediction
        /// once the client actually holds the lease. The internal stop calls a task/option/pack/fire
        /// screen makes are excluded by <see cref="CanObserve"/>'s action guard.</summary>
        private StaffAction BeginWorkerStop(Worker worker)
        {
            if (!CanObserve() || worker == null || !_workerLease.Contains(worker))
            {
                return null;
            }

            return new StaffAction
            {
                Worker = worker,
                Index = worker.GetInstanceID(),
                Message = new StaffModuleIntentMessage
                {
                    Kind = StaffIntentKind.EndInteraction,
                    Worker = worker,
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
            _workerLease.Remove(worker);
            _workerBusy[worker] = false;
            RegisterPostHoc(state,
                () => WithPrediction(() =>
                {
                    _workerLease.Remove(worker);
                    _workerBusy[worker] = false;
                    worker.OnPressStopInteract();
                }),
                () =>
                {
                    _workerLease.Add(worker);
                    _workerBusy[worker] = true;
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

            if (!RuntimeContext.InGame())
            {
                throw new InvalidOperationException("Cannot send a Staff intent outside the game.");
            }

            Context.Send(message);
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
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

        }

        private void OnDestroy() => Shutdown();

        private static string DeltaKey(StaffModuleDeltaMessage message)
            => (message.Worker == null ? 0 : message.Worker.GetInstanceID()) + ":"
                + (byte)message.Kind + ":" + message.Generation
                + ":" + message.PackIndex + ":" + message.ExperienceTask;

        private void ClearDeferredDeltas()
        {
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
