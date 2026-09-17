using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Staff (worker) sync. The hire screen lives on the phone, so the joiner can press
    /// Hire freely - but WorkerManager.ActivateWorker is blocked client-side, so vanilla
    /// would charge the shared wallet for a worker that never exists in the real
    /// simulation. We block the client's hire BEFORE it charges and forward a StaffOp
    /// the host applies through the vanilla path (fee, roster flag, activation), so the
    /// wallet is charged exactly once, host-side.
    ///
    /// Fire / give-bonus / task-assignment screens are NOT forwarded: they only open via
    /// Worker.OnMousePress on a live Worker, and on the joiner real workers are swept
    /// inactive while puppet clones are stripped of colliders and scripts - those screens
    /// are physically unreachable, so ops for them would be dead code.
    ///
    /// Host pushes the hired roster + per-worker save-data essentials as two things: an immediate
    /// per-worker slice on change (client ops, and the host's own hire/fire/bonus/task edits), and
    /// an UNCONDITIONAL round-robin slice sweep that re-asserts one batch of workers every slice so
    /// a frame a client dropped is repaired even though nothing changed since (see AGENTS.md,
    /// "Sync scheduling: no periodic full resends").
    /// </summary>
    public class StaffSync : TickableCoopModule
    {
        private const byte OpHire = 1;
        private const byte OpUpdate = 2;
        private const byte OpBonus = 3;
        private const byte OpFire = 4;
        private const byte OpBeginInteract = 5;
        private const byte OpEndInteract = 6;
        private const int MaxWorkers = 32;

        /// <summary>Patches are static but ops need the wired instance; CoopCore
        /// constructs exactly one StaffSync, so the constructor self-registers.</summary>
        public static StaffSync Instance;

        /// <summary>True while ClientApplyState writes the mirrors, so no patch of ours
        /// (present or future) mistakes an echo for a local action.</summary>
        public static bool ApplyingRemote;

        public Action<INetMessage> SendOp;         // set by CoopCore: client->host
        public Action<INetMessage> BroadcastState; // set by CoopCore: host->clients
        public Action<int, INetMessage> SendToClient;
        public Action<INetMessage> BroadcastInteraction;

        // HireWorkerPanelUI keeps its identity and guards private; read them instead of
        // duplicating fee/level math that a game update could drift away from
        private static readonly System.Reflection.FieldInfo FiPanelIsHired =
            AccessTools.Field(typeof(HireWorkerPanelUI), "m_IsHired");
        private static readonly System.Reflection.FieldInfo FiPanelIndex =
            AccessTools.Field(typeof(HireWorkerPanelUI), "m_Index");
        private static readonly System.Reflection.FieldInfo FiPanelLevelRequired =
            AccessTools.Field(typeof(HireWorkerPanelUI), "m_LevelRequired");
        private static readonly System.Reflection.FieldInfo FiPanelHireFee =
            AccessTools.Field(typeof(HireWorkerPanelUI), "m_TotalHireFee");
        private static readonly System.Reflection.FieldInfo FiPanelScreen =
            AccessTools.Field(typeof(HireWorkerPanelUI), "m_HireWorkerScreen");
        private static readonly System.Reflection.MethodInfo MiPanelEvaluateHired =
            AccessTools.Method(typeof(HireWorkerPanelUI), "EvaluateHired");
        private static readonly System.Reflection.FieldInfo FiInteractWorker =
            AccessTools.Field(typeof(WorkerInteractUIScreen), "m_Worker");
        private static readonly System.Reflection.FieldInfo FiOptionWorker =
            AccessTools.Field(typeof(WorkerOptionUIScreen), "m_Worker");
        private static readonly System.Reflection.FieldInfo FiPriceWorker =
            AccessTools.Field(typeof(WorkerOptionSetPriceUIScreen), "m_Worker");
        private static readonly System.Reflection.FieldInfo FiPackWorker =
            AccessTools.Field(typeof(WorkerSetPackOpenerTypeOptionScreen), "m_Worker");

        private WorkerManager _wm;
        private HireWorkerScreen _hireScreen;
        private bool _hireScreenSearched; // the screen may legitimately not exist yet
        private readonly List<Entry> _buf = new List<Entry>(MaxWorkers);

        // Gradual slice sweep (AGENTS.md: no periodic full resends). Each pass re-asserts ONE
        // batch of workers UNCONDITIONALLY - like WarehouseBoxSync, and deliberately NOT gated on
        // a change hash. The sweep is the eventual-correctness safety net: a frame the client
        // dropped must be re-sent on the next pass even though the worker has not changed since,
        // whereas a change-gated sweep would never re-send it (the hash already matches). Push on
        // change (the dirty hooks + SendWorkerNow) supplies the immediacy; this supplies the
        // correctness. Sized so a full pass takes ~SweepCycleSeconds.
        private const float SweepSliceSeconds = 0.5f;
        private const float SweepCycleSeconds = 5f;
        private float _sweepTimer;
        private int _sweepCursor;

        private readonly Dictionary<int, int> _workerLeaseOwner = new Dictionary<int, int>();
        private static readonly Dictionary<int, bool> ClientWorkerBusy = new Dictionary<int, bool>();
        private static readonly HashSet<int> ClientWorkerLease = new HashSet<int>();
        private static bool _allowClientWorkerOpen;
        private static readonly System.Reflection.FieldInfo FiWorkerTargetRotation =
            AccessTools.Field(typeof(Worker), "m_TargetLerpRotation");

        public StaffSync()
        {
            Instance = this;
        }

        public override void Start() => Instance = this;

        public override string Name => nameof(StaffSync);

        private struct Entry
        {
            public bool Hired;
            public bool HasData;
            public byte PrimaryTask;
            public byte SecondaryTask;
            public byte WorkerTask;
            public byte CurrentState;
            public bool GoingHome;
            public byte BonusCount;
            public bool BonusBoosted;
            public bool FillNoLabel;
            public bool RoundUpPrice;
            public bool RoundUpCardPrice;
            public bool AvoidSetCardPrice;
            public bool AvoidSetCardPriceRestock;
            public float PriceMult;
            public float CardPriceMult;
            public List<bool> PackTypes; // reference to the game's list; read-only here
            public List<int> ExpList;
        }

        public override void Reset()
        {
            _wm = null;
            _hireScreen = null;
            _hireScreenSearched = false;
            _sweepTimer = 0f;
            _sweepCursor = 0;
            _workerLeaseOwner.Clear();
            ClientWorkerBusy.Clear();
            ClientWorkerLease.Clear();
            _allowClientWorkerOpen = false;
        }

        /// <summary>Our baseline is stale (join / router heal). NOT a periodic full resend: rewind
        /// the sweep so a fresh pass starts immediately - every worker is re-asserted by that pass
        /// anyway, because the sweep is unconditional.</summary>
        public override void ForceResend()
        {
            _sweepTimer = 0f;
            _sweepCursor = 0;
        }

        public override void Dispose()
        {
            base.Dispose();
            if (ReferenceEquals(Instance, this))
                Instance = null;
            ApplyingRemote = false;
        }

        private WorkerManager Wm()
        {
            // NEVER CSingleton<WorkerManager>.Instance: resolved while no real manager
            // exists (host mid-session save load) the getter fabricates a fake empty
            // DontDestroyOnLoad manager that shadows the real one for the rest of the
            // run (see WorldSync.ResolveShelfManager)
            if (_wm == null)
                _wm = UnityEngine.Object.FindObjectOfType<WorkerManager>();
            return _wm;
        }

        // ---------------- patches ----------------

        public static void ApplyPatches(Harmony h)
        {
            Try(h, typeof(HireWorkerPanelUI), "OnPressHireButton",
                prefix: new HarmonyMethod(typeof(StaffSync), nameof(HirePrefix)),
                postfix: new HarmonyMethod(typeof(StaffSync), nameof(HostHirePostfix)));
            Try(h, typeof(WorkerInteractUIScreen), "SetTaskAsPrimaryOrSecondary",
                postfix: new HarmonyMethod(typeof(StaffSync), nameof(TaskChangedPostfix)));
            Try(h, typeof(WorkerOptionUIScreen), "OnPressRestockShelfWithNoLabel",
                postfix: new HarmonyMethod(typeof(StaffSync), nameof(OptionChangedPostfix)));
            Try(h, typeof(WorkerOptionSetPriceUIScreen), "OnPressConfirm",
                postfix: new HarmonyMethod(typeof(StaffSync), nameof(PriceOptionPostfix)));
            Try(h, typeof(WorkerSetPackOpenerTypeOptionScreen), "OnPressConfirm",
                postfix: new HarmonyMethod(typeof(StaffSync), nameof(PackOptionPostfix)));
            // The host's own hire/fire/bonus go straight into the game (no op round-trip), so the
            // matching postfixes push the affected worker slice at once. The prefixes above only
            // intercept the CLIENT path; without these the host's edits wait for the sweep.
            Try(h, typeof(WorkerInteractUIScreen), "OnPressGiveBonus",
                prefix: new HarmonyMethod(typeof(StaffSync), nameof(BonusPrefix)),
                postfix: new HarmonyMethod(typeof(StaffSync), nameof(HostInteractWorkerPostfix)));
            Try(h, typeof(WorkerInteractUIScreen), "OnPressFire",
                prefix: new HarmonyMethod(typeof(StaffSync), nameof(FirePrefix)),
                postfix: new HarmonyMethod(typeof(StaffSync), nameof(HostInteractWorkerPostfix)));
            Try(h, typeof(Worker), "OnMousePress",
                prefix: new HarmonyMethod(typeof(StaffSync), nameof(WorkerMousePressPrefix)));
            Try(h, typeof(Worker), "OnPressStopInteract",
                postfix: new HarmonyMethod(typeof(StaffSync), nameof(WorkerStopInteractPostfix)));
        }

        /// <summary>Client: block the vanilla hire BEFORE it charges the (forwarded)
        /// wallet or flips the local roster, and ask the host to run the real thing.
        /// The vanilla UX guards are re-checked locally so the button still talks back.</summary>
        public static bool HirePrefix(HireWorkerPanelUI __instance)
        {
            if (CoopCore.Role != CoopRole.Client)
                return true;
            try
            {
                if ((bool)FiPanelIsHired.GetValue(__instance))
                    return false;
                int index = (int)FiPanelIndex.GetValue(__instance);
                int levelRequired = (int)FiPanelLevelRequired.GetValue(__instance);
                float fee = (float)FiPanelHireFee.GetValue(__instance);
                if (CPlayerData.m_ShopLevel + 1 < levelRequired)
                {
                    NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.ShopLevelNotEnough);
                    return false;
                }
                // the panel was Init'd when the screen opened; the roster may have
                // echoed a hire (ours or the host's) since then
                if (index < CPlayerData.m_IsWorkerHired.Count && CPlayerData.GetIsWorkerHired(index))
                    return false;
                if (CPlayerData.m_CoinAmountDouble < (double)fee)
                {
                    NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.Money);
                    return false;
                }
                var self = Instance;
                if (self?.SendOp == null)
                {
                    // wired sessions always set SendOp; letting vanilla run here would
                    // charge the shared wallet for a worker that never exists
                    CoopPlugin.Log.LogWarning("StaffSync: hire pressed but SendOp not wired - ignored");
                    return false;
                }
                self.SendOp(new StaffOpMessage { Op = OpHire, Index = index });
                SoundManager.GenericConfirm();
                if (CoopCore.Instance != null)
                {
                    CoopCore.Instance.RegisterLine = "hired - starting work at the host's shop";
                    CoopCore.Instance.RegisterLineTimer = 4f;
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("StaffSync hire prefix: " + e.Message); }
            return false;
        }

        private static Worker WorkerFrom(System.Reflection.FieldInfo field, object instance)
        {
            try
            {
                return field?.GetValue(instance) as Worker;
            }
            catch (System.Exception e) { Swallow.Log(e); return null; }
        }

        private static void SendUpdate(Worker worker)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote || worker == null || Instance?.SendOp == null)
                return;
            try
            {
                var d = worker.GetWorkerSaveData();
                Instance.SendOp(new StaffOpMessage
                {
                    Op = OpUpdate,
                    Index = worker.m_WorkerIndex,
                    PrimaryTask = (byte)d.primaryTask,
                    SecondaryTask = (byte)d.secondaryTask,
                    WorkerTask = (byte)d.workerTask,
                    FillNoLabel = d.isFillShelfWithoutLabel,
                    RoundUpPrice = d.isRoundUpPrice,
                    AvoidSetCardPrice = d.isAvoidSetCardPrice,
                    RoundUpCardPrice = d.isRoundUpCardPrice,
                    AvoidSetCardPriceRestock = d.isAvoidSetCardPriceWhileRestock,
                    PriceMult = d.setPriceMultiplier,
                    CardPriceMult = d.setCardPriceMultiplier,
                    PackTypes = d.cardPackItemTypeEnabledList,
                });
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("StaffSync update send: " + e.Message); }
        }

        /// <summary>A local staff edit happened. On a client it is forwarded to the host; on the
        /// host it pushes the affected worker slice straight out, because the host's own edit goes
        /// directly into the game (no op round-trip) and the sweep would otherwise take up to one
        /// full pass to reach the guests.</summary>
        private static void HandleLocalWorkerChange(Worker worker)
        {
            if (worker == null)
                return;
            if (CoopCore.Role == CoopRole.Client)
                SendUpdate(worker);
            else if (CoopCore.Role == CoopRole.Host)
                Instance?.SendWorkerNow(worker.m_WorkerIndex);
        }

        public static void TaskChangedPostfix(WorkerInteractUIScreen __instance)
        {
            HandleLocalWorkerChange(WorkerFrom(FiInteractWorker, __instance));
        }
        public static void OptionChangedPostfix(WorkerOptionUIScreen __instance)
        {
            HandleLocalWorkerChange(WorkerFrom(FiOptionWorker, __instance));
        }
        public static void PriceOptionPostfix(WorkerOptionSetPriceUIScreen __instance)
        {
            HandleLocalWorkerChange(WorkerFrom(FiPriceWorker, __instance));
        }
        public static void PackOptionPostfix(WorkerSetPackOpenerTypeOptionScreen __instance)
        {
            HandleLocalWorkerChange(WorkerFrom(FiPackWorker, __instance));
        }

        /// <summary>Host: the vanilla hire/bonus/fire already ran locally, so push the worker the
        /// host just changed. (The client path is handled by the matching prefix.)</summary>
        public static void HostHirePostfix(HireWorkerPanelUI __instance)
        {
            if (CoopCore.Role != CoopRole.Host)
                return;
            try
            {
                Instance?.SendWorkerNow((int)FiPanelIndex.GetValue(__instance));
            }
            catch (System.Exception caught) { Swallow.Log(caught); }
        }

        public static void HostInteractWorkerPostfix(WorkerInteractUIScreen __instance)
        {
            if (CoopCore.Role != CoopRole.Host)
                return;
            var worker = WorkerFrom(FiInteractWorker, __instance);
            if (worker != null)
                Instance?.SendWorkerNow(worker.m_WorkerIndex);
        }

        public static bool BonusPrefix(WorkerInteractUIScreen __instance)
        {
            if (CoopCore.Role != CoopRole.Client)
                return true;
            var worker = WorkerFrom(FiInteractWorker, __instance);
            if (worker == null || Instance?.SendOp == null)
                return false;
            Instance.SendOp(new StaffOpMessage { Op = OpBonus, Index = worker.m_WorkerIndex });
            return false;
        }

        public static bool FirePrefix(WorkerInteractUIScreen __instance)
        {
            if (CoopCore.Role != CoopRole.Client)
                return true;
            var worker = WorkerFrom(FiInteractWorker, __instance);
            if (worker == null || Instance?.SendOp == null)
                return false;
            Instance.SendOp(new StaffOpMessage { Op = OpFire, Index = worker.m_WorkerIndex });
            try
            {
                worker.OnPressStopInteract();
                __instance.CloseScreen();
            }
            catch (System.Exception caught) { Swallow.Log(caught); }
            return false;
        }

        public static bool WorkerMousePressPrefix(Worker __instance)
        {
            if (__instance == null)
                return false;
            int index = __instance.m_WorkerIndex;
            if (_allowClientWorkerOpen)
                return true;
            if (CoopCore.Role == CoopRole.Host)
                return Instance != null && Instance.HostBeginInteraction(index, 0, default(Vector3));
            if (CoopCore.Role != CoopRole.Client)
                return true;
            if (ClientWorkerLease.Contains(index))
                return false;
            if (ClientWorkerBusy.TryGetValue(index, out bool busy) && busy)
                return false;
            if (Instance?.SendOp == null)
                return false;
            Vector3 pos;
            if (!CoopCore.TryGetLocalPlayerPosition(out pos))
                return false;
            Instance.SendOp(new StaffOpMessage { Op = OpBeginInteract, Index = index, Position = pos });
            return false;
        }

        public static void WorkerStopInteractPostfix(Worker __instance)
        {
            if (__instance == null)
                return;
            int index = __instance.m_WorkerIndex;
            if (CoopCore.Role == CoopRole.Host)
                Instance?.HostEndInteraction(index, 0);
            else if (CoopCore.Role == CoopRole.Client && ClientWorkerLease.Contains(index))
            {
                // Vanilla calls OnPressStopInteract before the enclosing screen method's
                // postfix. Delay the release one frame so that a committed task/option
                // update is sent while this client still owns the lease.
                if (CoopCore.Instance != null)
                    CoopCore.Instance.StartCoroutine(ReleaseClientWorkerLater(index));
                else
                    ReleaseClientWorker(index);
            }
        }

        private static IEnumerator ReleaseClientWorkerLater(int index)
        {
            yield return null;
            ReleaseClientWorker(index);
        }

        private static void ReleaseClientWorker(int index)
        {
            if (!ClientWorkerLease.Remove(index))
                return;
            Instance?.SendOp?.Invoke(new StaffOpMessage { Op = OpEndInteract, Index = index });
        }

        public static void ClientInteractionMessage(StaffInteractMessage message)
        {
            int index = message.Index;
            bool granted = message.Granted;
            bool occupied = message.Occupied;
            ClientWorkerBusy[index] = occupied;
            if (!occupied)
                ClientWorkerLease.Remove(index);
            if (!granted)
                return;
            ClientWorkerLease.Add(index);
            var worker = NpcSync.GetWorkerPuppet(index);
            // The puppet is built from the cloned prefab and can exist before its staff
            // data does. WorkerInteractUIScreen.OpenScreen dereferences GetWorkerData()
            // unconditionally, and Worker.OnMousePress has ALREADY shown the cursor and
            // locked movement by the time OpenScreen runs - so a null here used to leave
            // the joiner stuck in UI mode with no screen. Refuse to open until it is dressed.
            if (worker == null || worker.GetWorkerData() == null)
            {
                CoopPlugin.Log.LogWarning(
                    $"StaffSync: worker {index} UI not ready (puppet={worker != null}) - releasing interaction");
                ReleaseClientWorker(index);
                return;
            }
            _allowClientWorkerOpen = true;
            try
            {
                worker.OnMousePress();
            }
            catch (Exception e)
            {
                // OnMousePress enters UI mode before OpenScreen, so a throw leaves the cursor
                // visible and movement locked. Run vanilla's own stop path to restore both,
                // then hand the lease back so the host releases the worker.
                CoopPlugin.Log.LogError($"StaffSync: opening worker {index} UI failed: {e}");
                try
                {
                    worker.OnPressStopInteract();
                }
                catch (Exception restore)
                {
                    Swallow.Log(restore);
                }
                ReleaseClientWorker(index);
            }
            finally { _allowClientWorkerOpen = false; }
        }

        private static void Try(Harmony h, Type type, string method,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            try
            {
                var original = AccessTools.Method(type, method);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning($"Patch target missing: {type.Name}.{method}");
                    return;
                }
                h.Patch(original, prefix: prefix, postfix: postfix);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning($"Patch failed for {type.Name}.{method}: {e.Message}");
            }
        }

        // ---------------- host ----------------

        public void HostApplyOp(StaffOpMessage message, int connId)
        {
            byte op = message.Op;
            Guarded("apply", () =>
            {
                switch (op)
                {
                    case OpHire:
                        HostHire(message.Index);
                        break;
                    case OpUpdate:
                        HostUpdate(message, connId);
                        break;
                    case OpBonus:
                        HostBonus(message.Index, connId);
                        break;
                    case OpFire:
                        HostFire(message.Index, connId);
                        break;
                    case OpBeginInteract:
                        HostBeginInteraction(message.Index, connId, message.Position);
                        break;
                    case OpEndInteract:
                        HostEndInteraction(message.Index, connId);
                        break;
                    default:
                        CoopPlugin.Log.LogWarning("StaffSync: unknown op " + op);
                        break;
                }
            });
        }

        private bool ValidWorkerIndex(int index)
        {
            var workers = WorkerManager.GetWorkerList();
            return workers != null && index >= 0 && index < workers.Count && workers[index] != null
                && index < CPlayerData.m_IsWorkerHired.Count && CPlayerData.GetIsWorkerHired(index);
        }

        private void SendInteraction(int connId, int index, bool granted, bool occupied)
        {
            if (connId <= 0)
                return;
            SendToClient?.Invoke(connId, new StaffInteractMessage { Index = index, Granted = granted, Occupied = occupied });
        }

        private void BroadcastInteractionState(int index, bool occupied)
        {
            BroadcastInteraction?.Invoke(new StaffInteractMessage { Index = index, Granted = false, Occupied = occupied });
        }

        private bool HostBeginInteraction(int index, int connId, Vector3 playerPosition)
        {
            if (!ValidWorkerIndex(index))
                return false;
            if (_workerLeaseOwner.TryGetValue(index, out int owner))
            {
                if (owner == connId)
                    return true;
                SendInteraction(connId, index, false, true);
                return false;
            }
            var worker = WorkerManager.GetWorkerList()[index];
            if (connId != 0)
            {
                Vector3 toward = playerPosition - worker.transform.position;
                toward.y = 0f;
                if (toward.sqrMagnitude > 0.0001f)
                {
                    try
                    {
                        FiWorkerTargetRotation?.SetValue(worker, Quaternion.LookRotation(toward, Vector3.up));
                    }
                    catch (System.Exception caught) { Swallow.Log(caught); }
                }
            }
            worker.m_IsPausingAction = true;
            _workerLeaseOwner[index] = connId;
            SendInteraction(connId, index, true, true);
            BroadcastInteractionState(index, true);
            return true;
        }

        private void HostEndInteraction(int index, int connId)
        {
            if (!_workerLeaseOwner.TryGetValue(index, out int owner) || owner != connId)
                return;
            var workers = WorkerManager.GetWorkerList();
            if (workers != null && index >= 0 && index < workers.Count && workers[index] != null)
                workers[index].m_IsPausingAction = false;
            _workerLeaseOwner.Remove(index);
            BroadcastInteractionState(index, false);
        }

        public void HostReleaseConn(int connId)
        {
            var release = new List<int>();
            foreach (var kv in _workerLeaseOwner)
                if (kv.Value == connId)
                    release.Add(kv.Key);
            foreach (int index in release)
                HostEndInteraction(index, connId);
        }

        private void HostUpdate(StaffOpMessage message, int connId)
        {
            int index = message.Index;
            if (!_workerLeaseOwner.TryGetValue(index, out int leaseOwner) || leaseOwner != connId)
                return;
            var wm = Wm();
            if (wm == null || index < 0 || index >= wm.m_WorkerDataList.Count)
                return;
            var workers = WorkerManager.GetWorkerList();
            if (workers == null || index >= workers.Count || workers[index] == null)
                return;
            var w = workers[index];
            if (!CPlayerData.GetIsWorkerHired(index))
                return;
            var primary = (EWorkerTask)message.PrimaryTask;
            var secondary = (EWorkerTask)message.SecondaryTask;
            var task = (EWorkerTask)message.WorkerTask;
            bool fill = message.FillNoLabel, round = message.RoundUpPrice, avoid = message.AvoidSetCardPrice;
            bool cardRound = message.RoundUpCardPrice, cardAvoid = message.AvoidSetCardPriceRestock;
            float mult = Mathf.Clamp(message.PriceMult, 0f, 10f);
            float cardMult = Mathf.Clamp(message.CardPriceMult, 0f, 10f);
            int pn = message.PackTypes == null ? 0 : Math.Min(message.PackTypes.Count, 255);
            var packs = new bool[pn];
            for (int i = 0; i < pn; i++)
                packs[i] = message.PackTypes[i];
            if ((int)primary < 0 || ((int)primary > 6 && primary != EWorkerTask.GoBackHome))
                return;
            if ((int)secondary < 0 || ((int)secondary > 6 && secondary != EWorkerTask.GoBackHome))
                return;
            w.SetRestockShelfWithNoLabel(fill);
            w.UpdateSetPriceOption(round, avoid, mult);
            w.UpdateSetCardPriceOption(cardRound, cardAvoid, cardMult);
            for (int i = 0; i < packs.Length && i < w.GetCardPackItemTypeEnabledList().Count; i++)
                w.SetCardPackItemTypeEnabled(i, packs[i]);
            w.SetTask(primary);
            w.SetLastTask(task);
            w.SetSecondaryTask(secondary);
            SendWorkerNow(index); // push the updated worker at once, no full resend
        }

        private void HostBonus(int index, int connId)
        {
            var wm = Wm();
            var workers = WorkerManager.GetWorkerList();
            if (wm == null || workers == null || index < 0 || index >= workers.Count || workers[index] == null
                || !CPlayerData.GetIsWorkerHired(index))
                return;
            if (!_workerLeaseOwner.TryGetValue(index, out int owner) || owner != connId)
                return;
            var w = workers[index];
            if (w.GetBonusBoostedCount() >= 3)
                return;
            float fee = w.GetWorkerData().costPerDay;
            if (CPlayerData.m_CoinAmountDouble < fee)
                return;
            PriceChangeManager.AddTransaction(-fee, ETransactionType.WorkerSalary, 0);
            CEventManager.QueueEvent(new CEventPlayer_ReduceCoin(fee));
            CPlayerData.m_GameReportDataCollect.employeeCost -= fee;
            CPlayerData.m_GameReportDataCollectPermanent.employeeCost -= fee;
            w.GiveSalaryBonus();
            SendWorkerNow(index); // push the updated worker at once, no full resend
        }

        private void HostFire(int index, int connId)
        {
            var workers = WorkerManager.GetWorkerList();
            if (workers == null || index < 0 || index >= workers.Count || workers[index] == null
                || !CPlayerData.GetIsWorkerHired(index))
                return;
            if (!_workerLeaseOwner.TryGetValue(index, out int owner) || owner != connId)
                return;
            workers[index].FireWorker();
            HostEndInteraction(index, connId);
            SendWorkerNow(index); // push the updated worker at once, no full resend
        }

        /// <summary>Host: run HireWorkerPanelUI.OnPressHireButton's happy path minus its
        /// panel UI, so a joiner's hire is indistinguishable from the host's own. The
        /// wallet is charged HERE and only here - the client path was blocked before
        /// its ReduceCoin could fire.</summary>
        private void HostHire(int index)
        {
            try
            {
                var wm = Wm();
                if (wm == null || wm.m_WorkerDataList == null)
                    return;
                if (index < 0 || index >= wm.m_WorkerDataList.Count || index >= CPlayerData.m_IsWorkerHired.Count)
                {
                    CoopPlugin.Log.LogWarning("StaffSync: hire op for unknown worker " + index);
                    return;
                }
                // double-hire guard: duplicate ops, or both players racing the same panel
                if (CPlayerData.GetIsWorkerHired(index))
                    return;
                WorkerData workerData = WorkerManager.GetWorkerData(index);
                if (CPlayerData.m_ShopLevel + 1 < workerData.shopLevelRequired)
                    return;
                var gm = SceneRef<CGameManager>.Get();
                if (gm != null && gm.m_IsPrologue && !workerData.prologueShow)
                    return;
                if (CPlayerData.m_CoinAmountDouble < (double)workerData.hiringCost)
                {
                    // the client pre-checked its mirror; losing this race is rare and the
                    // roster echo (still unhired) is the correction
                    CoopPlugin.Log.LogInfo("StaffSync: hire refused, not enough money for worker " + index);
                    return;
                }
                // vanilla hire path, faithfully (including the report counters and the
                // achievement check the panel does)
                PriceChangeManager.AddTransaction(0f - workerData.hiringCost, ETransactionType.HireWorker, index);
                CEventManager.QueueEvent(new CEventPlayer_ReduceCoin(workerData.hiringCost));
                CPlayerData.SetIsWorkerHired(index, isHired: true);
                wm.ActivateWorker(index, resetTask: true);
                CPlayerData.m_GameReportDataCollect.employeeCost -= workerData.hiringCost;
                CPlayerData.m_GameReportDataCollectPermanent.employeeCost -= workerData.hiringCost;
                int hiredCount = 0;
                for (int i = 0; i < CPlayerData.m_IsWorkerHired.Count; i++)
                {
                    if (CPlayerData.m_IsWorkerHired[i])
                        hiredCount++;
                }
                AchievementManager.OnStaffHired(hiredCount);
                SoundManager.PlayAudio("SFX_CustomerBuy", 0.6f);
                CoopPlugin.Log.LogInfo("StaffSync: joiner hired worker " + index);
                SendWorkerNow(index); // the confirming echo goes out at once, no full resend
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("StaffSync host hire: " + e.Message); }
        }

        /// <summary>Gradual sweep: re-assert ONE batch of workers per slice, UNCONDITIONALLY. It is
        /// the eventual-correctness safety net, so it must re-send even when nothing changed - a
        /// change-gated sweep can never repair a frame the client dropped (the gate would already
        /// consider the slice up to date). Push on change still supplies the immediacy. A full pass
        /// takes ~<see cref="SweepCycleSeconds"/>.</summary>
        public override void PeriodicUpdate(float delta)
        {
            if (CoopCore.Role != CoopRole.Host || BroadcastState == null || !CoopCore.InSessionWorld)
                return;
            _sweepTimer += delta;
            if (_sweepTimer < SweepSliceSeconds)
                return;
            _sweepTimer = 0f;
            Guarded("sweep", () =>
            {
                var wm = Wm();
                if (wm == null || wm.m_WorkerDataList == null)
                    return;
                int total = Mathf.Min(wm.m_WorkerDataList.Count, MaxWorkers);
                if (total <= 0)
                {
                    _sweepCursor = 0;
                    return;
                }
                int perSlice = Mathf.Max(1,
                    Mathf.CeilToInt(total / Mathf.Max(1f, SweepCycleSeconds / SweepSliceSeconds)));
                for (int k = 0; k < perSlice; k++)
                {
                    if (_sweepCursor < 0 || _sweepCursor >= total)
                        _sweepCursor = 0;
                    int i = _sweepCursor;
                    _sweepCursor = (_sweepCursor + 1) % total;
                    SendWorkerNow(i);
                }
            });
        }

        /// <summary>Host: send the COMPLETE roster to one connection - the join catch-up path.
        /// Not periodic; the sweep is what guarantees eventual correctness.</summary>
        public override void FullUpdate(Connection connection)
        {
            int connId = connection.Id;
            if (CoopCore.Role != CoopRole.Host || SendToClient == null)
                return;
            Guarded("full", () =>
            {
                var msg = BuildFull();
                if (msg != null)
                    SendToClient(connId, msg);
            });
        }

        private StaffStateMessage BuildFull()
        {
            var wm = Wm();
            if (wm == null || wm.m_WorkerDataList == null)
                return null;
            Collect(wm, _buf);
            var entries = new List<StaffEntry>(_buf.Count);
            for (int i = 0; i < _buf.Count; i++)
            {
                entries.Add(ToStaffEntry(_buf[i]));
            }
            return new StaffStateMessage { Full = true, Index = -1, Entries = entries };
        }

        /// <summary>Host: push ONE worker's authoritative slice to every client right now. This is
        /// the push-on-change path; the sweep re-asserts the same slice unconditionally as the
        /// safety net, so nothing here needs to remember "what was already sent".</summary>
        private void SendWorkerNow(int index)
        {
            // Guarded: this is also called straight from Harmony postfixes on vanilla UI methods,
            // so a transport/serialization throw must not escape into the game's UI code path.
            Guarded("push", () =>
            {
                var wm = Wm();
                if (wm == null || BroadcastState == null)
                    return;
                if (!TryCollectOne(wm, index, out Entry e))
                    return;
                BroadcastState(new StaffStateMessage
                {
                    Full = false,
                    Index = index,
                    Entries = new List<StaffEntry> { ToStaffEntry(e) },
                });
            });
        }

        /// <summary>Essentials come from the LIVE Worker when it's active (the save-data
        /// list only refreshes on save), falling back to the saved copy for workers who
        /// are hired but home for the night.</summary>
        private static void Collect(WorkerManager wm, List<Entry> outList)
        {
            outList.Clear();
            int n = Mathf.Min(wm.m_WorkerDataList.Count, MaxWorkers);
            for (int i = 0; i < n; i++)
                outList.Add(CollectEntry(i));
        }

        /// <summary>One worker's entry. Essentials come from the LIVE Worker when it's active (the
        /// save-data list only refreshes on save), falling back to the saved copy for workers who
        /// are hired but home for the night.</summary>
        private static Entry CollectEntry(int i)
        {
            var e = new Entry
            {
                Hired = i < CPlayerData.m_IsWorkerHired.Count && CPlayerData.GetIsWorkerHired(i),
            };
            WorkerSaveData d = null;
            var workers = WorkerManager.GetWorkerList();
            var w = workers != null && i < workers.Count ? workers[i] : null;
            if (w != null && w.m_IsActive)
            {
                try
                {
                    d = w.GetWorkerSaveData();
                }
                catch (System.Exception caught) { Swallow.Log(caught); }
            }
            var saved = CPlayerData.m_WorkerSaveDataList;
            if (d == null && saved != null && i < saved.Count)
                d = saved[i];
            if (d != null)
            {
                e.HasData = true;
                e.PrimaryTask = (byte)d.primaryTask;
                e.SecondaryTask = (byte)d.secondaryTask;
                e.WorkerTask = (byte)d.workerTask;
                e.CurrentState = (byte)d.currentState;
                e.GoingHome = d.isGoingHome;
                e.BonusCount = (byte)Mathf.Clamp(d.bonusBoostedCount, 0, 255);
                e.BonusBoosted = d.isBonusBoosted;
                e.FillNoLabel = d.isFillShelfWithoutLabel;
                e.RoundUpPrice = d.isRoundUpPrice;
                e.RoundUpCardPrice = d.isRoundUpCardPrice;
                e.AvoidSetCardPrice = d.isAvoidSetCardPrice;
                e.AvoidSetCardPriceRestock = d.isAvoidSetCardPriceWhileRestock;
                e.PriceMult = d.setPriceMultiplier;
                e.CardPriceMult = d.setCardPriceMultiplier;
                e.PackTypes = d.cardPackItemTypeEnabledList;
                e.ExpList = d.expList;
            }
            return e;
        }

        private static bool TryCollectOne(WorkerManager wm, int i, out Entry e)
        {
            e = default(Entry);
            if (wm == null || wm.m_WorkerDataList == null)
                return false;
            if (i < 0 || i >= Mathf.Min(wm.m_WorkerDataList.Count, MaxWorkers))
                return false;
            e = CollectEntry(i);
            return true;
        }

        // ---------------- client ----------------

        public void ClientApplyState(StaffStateMessage message)
        {
            ApplyingRemote = true;
            try
            {
                Guarded("apply", () => ClientApplyInner(message));
            }
            finally { ApplyingRemote = false; }
        }

        private void ClientApplyInner(StaffStateMessage message)
        {
            int n = message.Entries.Count;
            bool rosterChanged = false;
            var saved = CPlayerData.m_WorkerSaveDataList;
            // A partial carries exactly ONE entry, for message.Index. Omitted workers are
            // unchanged - never treat absence as "fired", and never walk the roster for one.
            bool partial = !message.Full && message.Index >= 0;
            // A partial addresses exactly ONE worker. Validate that contract instead of trusting
            // the wire index: an out-of-range index would grow the roster list without bound, and
            // a multi-entry partial would be applied n times to the same worker.
            if (partial && (n != 1 || message.Index >= MaxWorkers))
            {
                CoopPlugin.Log.LogWarning("StaffSync: bad partial StaffState (index=" + message.Index
                    + ", entries=" + n + ") - dropped");
                return;
            }

            // Compare the complete incoming slice before looking up scene objects or touching
            // save data.  Sweeps are intentionally unconditional on the wire, but an unchanged
            // healing slice must be cheap on the receiving client.
            var changed = new bool[n];
            var dataChanged = new bool[n];
            for (int k = 0; k < n; k++)
            {
                int i = partial ? message.Index : k;
                var e = message.Entries[k];
                bool hired = i < CPlayerData.m_IsWorkerHired.Count && CPlayerData.GetIsWorkerHired(i);
                WorkerSaveData local = saved != null && i < saved.Count ? saved[i] : null;
                changed[k] = !EntryMatchesLocal(e, local, hired);
                if (hired != e.Hired)
                    rosterChanged = true;
                dataChanged[k] = e.HasData && saved != null && !SameWorkerData(e, local);
            }

            // One scene lookup per state apply, not per worker: the interaction screen caches
            // the bonus count when it opens, so an open screen must be refreshed from the mirror.
            // Do not even search for it when this state contains no changed worker data.
            WorkerInteractUIScreen interactScreen = null;
            for (int k = 0; k < n; k++)
            {
                if (dataChanged[k] && changed[k])
                {
                    interactScreen = UnityEngine.Object.FindObjectOfType<WorkerInteractUIScreen>(true);
                    break;
                }
            }

            for (int k = 0; k < n; k++)
            {
                int i = partial ? message.Index : k;
                var e = message.Entries[k];
                if (!changed[k])
                    continue;
                if (i < CPlayerData.m_IsWorkerHired.Count && CPlayerData.GetIsWorkerHired(i) != e.Hired)
                {
                    // roster only - no ActivateWorker: real workers stay suppressed on the
                    // client, puppets carry the visuals; this flag is what the hire screen
                    // and the salary totals (bills) read
                    CPlayerData.SetIsWorkerHired(i, e.Hired);
                }
                if (!dataChanged[k])
                    continue;
                // WorkerManager.m_WorkerSaveDataList aliases this list after load, so
                // writing entries in place updates both mirrors
                while (saved.Count <= i)
                    saved.Add(new WorkerSaveData());
                var d = saved[i];
                if (d == null)
                {
                    d = new WorkerSaveData();
                    saved[i] = d;
                }
                d.primaryTask = (EWorkerTask)e.PrimaryTask;
                d.secondaryTask = (EWorkerTask)e.SecondaryTask;
                d.workerTask = (EWorkerTask)e.WorkerTask;
                d.currentState = (EWorkerState)e.CurrentState;
                d.isGoingHome = e.GoingHome;
                d.bonusBoostedCount = e.BonusCount;
                d.isBonusBoosted = e.BonusBoosted;
                d.isFillShelfWithoutLabel = e.FillNoLabel;
                d.isRoundUpPrice = e.RoundUpPrice;
                d.isRoundUpCardPrice = e.RoundUpCardPrice;
                d.isAvoidSetCardPrice = e.AvoidSetCardPrice;
                d.isAvoidSetCardPriceWhileRestock = e.AvoidSetCardPriceRestock;
                d.setPriceMultiplier = e.PriceMult;
                d.setCardPriceMultiplier = e.CardPriceMult;
                if (e.PackTypes != null)
                    d.cardPackItemTypeEnabledList = new List<bool>(e.PackTypes);
                if (e.ExpList != null)
                    d.expList = new List<int>(e.ExpList);
                NpcSync.RefreshWorkerUi(i, d);
                RefreshInteractScreen(interactScreen, i);
            }
            if (rosterChanged)
                RefreshHirePanels();
        }

        private static bool EntryMatchesLocal(StaffEntry e, WorkerSaveData d, bool hired)
        {
            return e.Hired == hired && (!e.HasData || SameWorkerData(e, d));
        }

        private static bool SameWorkerData(StaffEntry e, WorkerSaveData d)
        {
            if (d == null)
                return false;
            return (byte)d.primaryTask == e.PrimaryTask
                && (byte)d.secondaryTask == e.SecondaryTask
                && (byte)d.workerTask == e.WorkerTask
                && (byte)d.currentState == e.CurrentState
                && d.isGoingHome == e.GoingHome
                && Mathf.Clamp(d.bonusBoostedCount, 0, 255) == e.BonusCount
                && d.isBonusBoosted == e.BonusBoosted
                && d.isFillShelfWithoutLabel == e.FillNoLabel
                && d.isRoundUpPrice == e.RoundUpPrice
                && d.isRoundUpCardPrice == e.RoundUpCardPrice
                && d.isAvoidSetCardPrice == e.AvoidSetCardPrice
                && d.isAvoidSetCardPriceWhileRestock == e.AvoidSetCardPriceRestock
                && d.setPriceMultiplier == e.PriceMult
                && d.setCardPriceMultiplier == e.CardPriceMult
                && SameList(d.cardPackItemTypeEnabledList, e.PackTypes)
                && SameList(d.expList, e.ExpList);
        }

        private static bool SameList<T>(List<T> local, List<T> incoming)
        {
            if (ReferenceEquals(local, incoming))
                return true;
            if (local == null || incoming == null || local.Count != incoming.Count)
                return false;
            for (int i = 0; i < local.Count; i++)
            {
                if (!EqualityComparer<T>.Default.Equals(local[i], incoming[i]))
                    return false;
            }
            return true;
        }

        /// <summary>The hire screen Init()s its panels on every open, but an echo that
        /// lands while the joiner is LOOKING at the screen (the case right after they
        /// press Hire) must flip the panel to "Hired" without a reopen.</summary>
        private void RefreshHirePanels()
        {
            if (!_hireScreenSearched)
            {
                _hireScreenSearched = true;
                _hireScreen = UnityEngine.Object.FindObjectOfType<HireWorkerScreen>(true);
            }
            if (_hireScreen == null || _hireScreen.m_HireWorkerPanelUIList == null
                || MiPanelEvaluateHired == null || FiPanelScreen == null)
                return;
            for (int i = 0; i < _hireScreen.m_HireWorkerPanelUIList.Count; i++)
            {
                var panel = _hireScreen.m_HireWorkerPanelUIList[i];
                if (panel == null)
                    continue;
                // a panel that was never Init'd has index 0 and no screen ref; skip it -
                // the screen's own OnOpenScreen -> Init covers the first open
                if (FiPanelScreen.GetValue(panel) == null)
                    continue;
                try
                {
                    MiPanelEvaluateHired.Invoke(panel, null);
                }
                catch (System.Exception caught) { Swallow.Log(caught); }
            }
        }

        /// <summary>The interaction screen caches the bonus count when it opens, so refresh
        /// its controls when a mirrored worker update lands while the screen is open.</summary>
        private static void RefreshInteractScreen(WorkerInteractUIScreen screen, int index)
        {
            if (screen == null || screen.m_ScreenGrp == null || !screen.m_ScreenGrp.activeSelf)
                return;
            var worker = FiInteractWorker.GetValue(screen) as Worker;
            if (worker == null || worker.m_WorkerIndex != index)
                return;
            int count = worker.GetBonusBoostedCount();
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

        // ---------------- wire ----------------

        private static byte PackFlags(Entry e)
        {
            return (byte)((e.Hired ? 1 : 0)
                | (e.HasData ? 2 : 0)
                | (e.BonusBoosted ? 4 : 0)
                | (e.FillNoLabel ? 8 : 0)
                | (e.RoundUpPrice ? 16 : 0)
                | (e.RoundUpCardPrice ? 32 : 0)
                | (e.AvoidSetCardPrice ? 64 : 0)
                | (e.AvoidSetCardPriceRestock ? 128 : 0));
        }

        /// <summary>Map the internal entry snapshot onto the wire DTO's entry shape.
        /// The two structs carry the same fields; only the type name differs.</summary>
        private static StaffEntry ToStaffEntry(Entry e)
        {
            return new StaffEntry
            {
                Hired = e.Hired,
                HasData = e.HasData,
                PrimaryTask = e.PrimaryTask,
                SecondaryTask = e.SecondaryTask,
                WorkerTask = e.WorkerTask,
                CurrentState = e.CurrentState,
                GoingHome = e.GoingHome,
                BonusCount = e.BonusCount,
                BonusBoosted = e.BonusBoosted,
                FillNoLabel = e.FillNoLabel,
                RoundUpPrice = e.RoundUpPrice,
                RoundUpCardPrice = e.RoundUpCardPrice,
                AvoidSetCardPrice = e.AvoidSetCardPrice,
                AvoidSetCardPriceRestock = e.AvoidSetCardPriceRestock,
                PriceMult = e.PriceMult,
                CardPriceMult = e.CardPriceMult,
                PackTypes = e.PackTypes,
                ExpList = e.ExpList,
            };
        }
    }
}
