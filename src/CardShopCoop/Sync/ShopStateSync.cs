using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using System;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Shop-level state the host owns and the joiner must agree with: the three phone
    /// bills (rent / electric / employee), room + warehouse-room + shop-lot-B unlocks,
    /// and the two physical signs (OPEN/CLOSED, warehouse customer entry).
    ///
    /// Everything here is CPlayerData statics, so an unblocked joiner action would only
    /// mutate his mirrored copy - and for bills/unlocks it would ALSO charge the shared
    /// wallet (ReduceCoin is forwarded) for something the real simulation never gets.
    /// So every joiner handler is blocked BEFORE its coin charge and forwarded as an op;
    /// the host's vanilla path does the one and only charge, and the state broadcast is
    /// the echo that updates the joiner's phone/signs.
    /// </summary>
    public class ShopStateSync : TickableCoopModule
    {
        // ShopOp sub-ops (first byte of the payload)
        private const byte OpPayBill = 1;    // + byte: 0=all, else (byte)EBillType
        private const byte OpUnlock = 2;     // + byte kind: 0=room, 1=warehouseRoom, 2=shopB
        private const byte OpToggleSign = 3; // + byte which: 0=open/close, 1=warehouse entry
        private const byte OpToggleLight = 4; // + byte (unused): guest flipped the shop light switch

        private static ShopStateSync _instance; // patches are static; ops route through here

        /// <summary>True while ClientApplyState drives game code, so our own patches
        /// never mistake a sync-applied change for a local click and re-forward it.</summary>
        public static bool ApplyingRemote;

        public static void RequestLightToggle()
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote)
                return;
            _instance?.SendOp?.Invoke(new ShopOpMessage { Op = OpToggleLight, Arg = 0 });
        }

        // private game methods we drive on the client to make the UI/meshes tell the truth
        private static readonly System.Reflection.MethodInfo MiBillEvaluateUI =
            AccessTools.Method(typeof(RentBillScreen), "EvaluateUI");
        private static readonly System.Reflection.MethodInfo MiBillNotification =
            AccessTools.Method(typeof(RentBillScreen), "EvaluateBillNotification");
        private static readonly System.Reflection.MethodInfo MiOpenSignMesh =
            AccessTools.Method(typeof(InteractableOpenCloseSign), "EvaluateSignOpenCloseMesh");
        private static readonly System.Reflection.MethodInfo MiWarehouseSignMesh =
            AccessTools.Method(typeof(InteractableWarehouseAllowEnterSign), "EvaluateSignOpenCloseMesh");
        private static readonly System.Reflection.MethodInfo MiRoomInit =
            AccessTools.Method(typeof(UnlockRoomManager), "Init"); // idempotent wall/door repaint
        // tutorial/task progress mirror: the subgroup progress fields are private, so we
        // reset them by reflection before re-feeding the host's authoritative values
        private static readonly System.Reflection.FieldInfo FiSgCurrent =
            AccessTools.Field(typeof(TutorialSubGroup), "m_CurrentValue");
        private static readonly System.Reflection.FieldInfo FiSgFinish =
            AccessTools.Field(typeof(TutorialSubGroup), "m_IsTaskFinish");

        public Action<INetMessage> SendOp;         // set by CoopCore: client -> host
        public Action<INetMessage> BroadcastState; // set by CoopCore: host -> clients
        public Action<int, INetMessage> SendToClient; // set by CoopCore: host -> one client

        // Partial indexes: 0 = all three bills, 1 = room/unlock/sign block, 2 = the
        // complete tutorial index and list. The tutorial list is deliberately atomic:
        // ApplyTutorial presents one coherent tutorial state to the joiner.
        private const int SliceCount = 3;
        private const float SweepCycleSeconds = 5f;
        private const float SweepSliceSeconds = SweepCycleSeconds / SliceCount;
        private float _sweepTimer;
        private int _sweepCursor;
        private bool _forceSweepArmed;
        private double _lastRoomRepaint;
        private RentBillScreen _billScreen;                        // phone screen, often inactive
        private InteractableOpenCloseSign _openSign;               // world object by the door
        private InteractableWarehouseAllowEnterSign _warehouseSign;
        private UnlockRoomManager _urm;                            // NEVER CSingleton<>.Instance:
        private ShelfManager _shelfMgr;                            // it fabricates a fake empty
                                                                   // manager if touched during a
                                                                   // loading screen (see WorldSync)
        private TutorialManager _tutorialManager;

        private UnlockRoomManager Urm()
        {
            if (_urm == null)
                _urm = UnityEngine.Object.FindObjectOfType<UnlockRoomManager>();
            return _urm;
        }

        public ShopStateSync()
        {
            _instance = this;
        }

        public override string Name => nameof(ShopStateSync);

        public override void Start() => _instance = this;
        protected override void OnHostTick(in SyncFrame frame) => HostTick(frame.Dt, frame.InGame);

        public override void Reset()
        {
            _sweepTimer = 0f;
            _sweepCursor = 0;
            _forceSweepArmed = false;
            // Per-world timestamp: a value kept across a world reload would throttle the
            // fallback room repaint for a DIFFERENT world (the convention ContainerSync documents).
            _lastRoomRepaint = -999.0;
            _billScreen = null;
            _openSign = null;
            _warehouseSign = null;
            _urm = null;
            _shelfMgr = null;
            _tutorialManager = null;
        }

        public override void ForceResend()
        {
            if (!_forceSweepArmed)
            {
                _sweepCursor = 0;
                _sweepTimer = SweepSliceSeconds;
                _forceSweepArmed = true;
            }
        }

        public override void Dispose()
        {
            base.Dispose();
            if (ReferenceEquals(_instance, this))
                _instance = null;
            ApplyingRemote = false;
        }

        // ---------------- cached lookups ----------------

        private RentBillScreen BillScreen()
        {
            // phone screens live disabled until opened - the plain overload misses them
            if (_billScreen == null)
                _billScreen = UnityEngine.Object.FindObjectOfType<RentBillScreen>(true);
            return _billScreen;
        }

        private InteractableOpenCloseSign OpenSign()
        {
            if (_openSign == null)
                _openSign = UnityEngine.Object.FindObjectOfType<InteractableOpenCloseSign>(true);
            return _openSign;
        }

        private InteractableWarehouseAllowEnterSign WarehouseSign()
        {
            if (_warehouseSign == null)
                _warehouseSign = UnityEngine.Object.FindObjectOfType<InteractableWarehouseAllowEnterSign>(true);
            return _warehouseSign;
        }

        // ---------------- patches ----------------

        public static void ApplyPatches(Harmony h)
        {
            // Bills: block the joiner's pay buttons before CEventPlayer_ReduceCoin fires
            // (that event is forwarded to the shared wallet - letting it through would
            // charge everyone for a payment the host never records).
            Try(h, typeof(RentBillScreen), "OnPressPayRentBill",
                prefix: new HarmonyMethod(typeof(ShopStateSync), nameof(PayRentPrefix)));
            Try(h, typeof(RentBillScreen), "OnPressPayElectricBill",
                prefix: new HarmonyMethod(typeof(ShopStateSync), nameof(PayElectricPrefix)));
            Try(h, typeof(RentBillScreen), "OnPressPaySalaryBill",
                prefix: new HarmonyMethod(typeof(ShopStateSync), nameof(PaySalaryPrefix)));
            Try(h, typeof(RentBillScreen), "OnPressPayAllBill",
                prefix: new HarmonyMethod(typeof(ShopStateSync), nameof(PayAllPrefix)));

            // Bill accrual is host math (host room counts, host worker salaries, host
            // light hours). The joiner gets one allowed OnDayStarted per host day, which
            // would run PhoneManager -> EvaluateNewDayBill with LOCAL numbers - including
            // auto-force-paying overdue bills straight past our pay-button blocks.
            Try(h, typeof(RentBillScreen), "EvaluateNewDayBill",
                prefix: new HarmonyMethod(typeof(ShopStateSync), nameof(BillAccrualPrefix)));

            // Expansions: EvaluateCartCheckout is the single charge+unlock point for both
            // shop rooms and warehouse rooms; OnPressUnlockShopB is the lot-B purchase.
            Try(h, typeof(ExpansionShopUIScreen), "EvaluateCartCheckout",
                prefix: new HarmonyMethod(typeof(ShopStateSync), nameof(RoomCheckoutPrefix)));
            Try(h, typeof(ExpansionShopUIScreen), "OnPressUnlockShopB",
                prefix: new HarmonyMethod(typeof(ShopStateSync), nameof(UnlockShopBPrefix)));

            // Signs: a joiner flip must run in the real simulation (customer entry is
            // gated on the HOST's CPlayerData booleans), so forward and let the echo
            // flip the local sign.
            Try(h, typeof(InteractableOpenCloseSign), "OnMouseButtonUp",
                prefix: new HarmonyMethod(typeof(ShopStateSync), nameof(OpenSignPrefix)),
                postfix: new HarmonyMethod(typeof(ShopStateSync), nameof(RoomChangedPostfix)));
            Try(h, typeof(InteractableWarehouseAllowEnterSign), "OnMouseButtonUp",
                prefix: new HarmonyMethod(typeof(ShopStateSync), nameof(WarehouseSignPrefix)),
                postfix: new HarmonyMethod(typeof(ShopStateSync), nameof(RoomChangedPostfix)));
            Try(h, typeof(InteractableOpenCloseSign), "OnDayStarted",
                postfix: new HarmonyMethod(typeof(ShopStateSync), nameof(RoomChangedPostfix)));

            // Shop light: a joiner's wall-switch click only flipped its own local light.
            // Forward it; the host toggles authoritatively and the LightState broadcast
            // flips everyone's light (applied surgically in CoopCore's LightState handler).
            Try(h, typeof(InteractableLightSwitch), "OnMouseButtonUp",
                prefix: new HarmonyMethod(typeof(ShopStateSync), nameof(LightSwitchPrefix)));

            // Tutorial task credit is host-authoritative: the joiner's local credit is forwarded so
            // the host advances and broadcasts it back. Without this, a joiner's tutorial progress
            // was wiped by the host's next snapshot because the host never learned about it.
            Try(h, typeof(TutorialManager), "AddTaskValue",
                postfix: new HarmonyMethod(typeof(ShopStateSync), nameof(TutorialCreditPostfix)));
            Try(h, typeof(CPlayerData), "UpdateBill",
                postfix: new HarmonyMethod(typeof(ShopStateSync), nameof(BillChangedPostfix)));
            Try(h, typeof(CPlayerData), "SetBill",
                postfix: new HarmonyMethod(typeof(ShopStateSync), nameof(BillChangedPostfix)));
            Try(h, typeof(UnlockRoomManager), "SetUnlockWarehouseRoom",
                postfix: new HarmonyMethod(typeof(ShopStateSync), nameof(RoomChangedPostfix)));
            Try(h, typeof(UnlockRoomManager), "StartUnlockNextRoom",
                postfix: new HarmonyMethod(typeof(ShopStateSync), nameof(RoomChangedPostfix)));
            Try(h, typeof(UnlockRoomManager), "StartUnlockNextWarehouseRoom",
                postfix: new HarmonyMethod(typeof(ShopStateSync), nameof(RoomChangedPostfix)));
            Try(h, typeof(TutorialManager), "EvaluateTaskVisibility",
                postfix: new HarmonyMethod(typeof(ShopStateSync), nameof(TutorialChangedPostfix)));
            Try(h, typeof(ShopRenamer), "OnPressConfirmShopName",
                postfix: new HarmonyMethod(typeof(ShopStateSync), nameof(TutorialChangedPostfix)));
        }

        public static void BillChangedPostfix(EBillType billType)
        {
            if (CoopCore.Role == CoopRole.Host && !ApplyingRemote)
            {
                _instance?.SendBillsNow();
            }
        }

        public static void RoomChangedPostfix()
        {
            if (CoopCore.Role == CoopRole.Host && !ApplyingRemote)
            {
                _instance?.SendRoomNow();
            }
        }

        public static void TutorialChangedPostfix()
        {
            if (CoopCore.Role == CoopRole.Host && !ApplyingRemote)
            {
                _instance?.SendTutorialNow();
            }
        }

        /// <summary>Client: forward the task credit the local game just applied. Sends the ABSOLUTE
        /// value for the condition so the host can reconcile by difference rather than increment.</summary>
        public static void TutorialCreditPostfix(ETutorialTaskCondition tutorialTaskCondition)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote)
                return;
            var self = _instance;
            if (self == null)
                return;
            self.SendOp?.Invoke(new TutorialCreditMessage
            {
                Condition = (int)tutorialTaskCondition,
                Value = TutorialValue(tutorialTaskCondition),
            });
        }

        /// <summary>Current value for a tutorial condition in the local (possibly optimistic)
        /// list; 0 when the condition has no entry yet.</summary>
        private static float TutorialValue(ETutorialTaskCondition condition)
        {
            var list = CPlayerData.m_TutorialDataList;
            if (list == null)
                return 0f;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null && list[i].tutorialTaskCondition == condition)
                    return list[i].value;
            return 0f;
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
                CoopPlugin.Log.LogWarning($"Patch failed: {type.Name}.{method}: {e.Message}");
            }
        }

        private static bool PayBillPrefix(byte billType, bool forcePay)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote)
                return true;
            // forcePay only comes from the (blocked) accrual auto-pay; never forward it
            if (!forcePay)
                _instance?.SendOp?.Invoke(new ShopOpMessage { Op = OpPayBill, Arg = billType });
            return false;
        }

        public static bool PayRentPrefix(bool forcePay)
        {
            return PayBillPrefix((byte)EBillType.Rent, forcePay);
        }
        public static bool PayElectricPrefix(bool forcePay)
        {
            return PayBillPrefix((byte)EBillType.Electric, forcePay);
        }
        public static bool PaySalaryPrefix(bool forcePay)
        {
            return PayBillPrefix((byte)EBillType.Employee, forcePay);
        }
        public static bool PayAllPrefix()
        {
            return PayBillPrefix(0, forcePay: false);
        }

        public static bool BillAccrualPrefix()
        {
            return CoopCore.Role != CoopRole.Client; // accrual is host truth, echoed back
        }

        public static bool RoomCheckoutPrefix(bool isShopB)
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote)
                return true;
            byte kind = isShopB ? (byte)1 : (byte)0;
            _instance?.SendOp?.Invoke(new ShopOpMessage { Op = OpUnlock, Arg = kind });
            return false;
        }

        public static bool UnlockShopBPrefix()
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote)
                return true;
            // the vanilla body screens level/owned/coins itself; re-checked host-side
            if (CPlayerData.m_IsWarehouseRoomUnlocked)
                return true; // let it show 'owned'
            _instance?.SendOp?.Invoke(new ShopOpMessage { Op = OpUnlock, Arg = 2 });
            return false;
        }

        public static bool OpenSignPrefix()
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote)
                return true;
            _instance?.SendOp?.Invoke(new ShopOpMessage { Op = OpToggleSign, Arg = 0 });
            return false;
        }

        public static bool LightSwitchPrefix()
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote)
                return true;
            // block the local-only toggle; forward it. The host echoes the authoritative
            // result via LightState.
            RequestLightToggle();
            return false;
        }

        public static bool WarehouseSignPrefix()
        {
            if (CoopCore.Role != CoopRole.Client || ApplyingRemote)
                return true;
            _instance?.SendOp?.Invoke(new ShopOpMessage { Op = OpToggleSign, Arg = 1 });
            return false;
        }

        // ---------------- host ----------------

        public void HostTick(float dt, bool inGame)
        {
            // HostTick remains for the existing tick lifecycle; state is pushed by mutation
            // hooks and re-asserted by the unconditional gradual sweep below.
        }

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
                SendSlice(_sweepCursor);
                _sweepCursor = (_sweepCursor + 1) % SliceCount;
                if (_sweepCursor == 0)
                {
                    _forceSweepArmed = false;
                }
            });
        }

        public override void FullUpdate(Connection connection)
        {
            int connId = connection.Id;
            if (CoopCore.Role != CoopRole.Host || SendToClient == null)
                return;
            Guarded("full", () => SendToClient(connId, BuildFullMessage()));
        }

        private static ShopStateMessage BuildFullMessage()
        {
            var msg = new ShopStateMessage { Full = true, Index = -1 };
            var rent = CPlayerData.GetBill(EBillType.Rent);
            msg.Rent.DayPassed = rent.billDayPassed;
            msg.Rent.AmountToPay = rent.amountToPay;
            var electric = CPlayerData.GetBill(EBillType.Electric);
            msg.Electric.DayPassed = electric.billDayPassed;
            msg.Electric.AmountToPay = electric.amountToPay;
            var employee = CPlayerData.GetBill(EBillType.Employee);
            msg.Employee.DayPassed = employee.billDayPassed;
            msg.Employee.AmountToPay = employee.amountToPay;
            msg.UnlockRoomCount = CPlayerData.m_UnlockRoomCount;
            msg.UnlockWarehouseRoomCount = CPlayerData.m_UnlockWarehouseRoomCount;
            msg.IsWarehouseRoomUnlocked = CPlayerData.m_IsWarehouseRoomUnlocked;
            msg.IsShopOpen = CPlayerData.m_IsShopOpen;
            msg.IsWarehouseDoorClosed = CPlayerData.m_IsWarehouseDoorClosed;
            msg.TutorialIndex = CPlayerData.m_TutorialIndex;
            var tut = CPlayerData.m_TutorialDataList;
            if (tut != null)
                foreach (var td in tut)
                    msg.Tutorials.Add(new ShopTutorialEntry { Condition = (int)td.tutorialTaskCondition, Value = td.value });
            return msg;
        }

        private static ShopStateMessage BuildSliceMessage(int index)
        {
            var msg = new ShopStateMessage { Full = false, Index = index };
            if (index == 0)
            {
                var rent = CPlayerData.GetBill(EBillType.Rent);
                msg.Rent = new ShopBillEntry { DayPassed = rent.billDayPassed, AmountToPay = rent.amountToPay };
                var electric = CPlayerData.GetBill(EBillType.Electric);
                msg.Electric = new ShopBillEntry { DayPassed = electric.billDayPassed, AmountToPay = electric.amountToPay };
                var employee = CPlayerData.GetBill(EBillType.Employee);
                msg.Employee = new ShopBillEntry { DayPassed = employee.billDayPassed, AmountToPay = employee.amountToPay };
            }
            else if (index == 1)
            {
                msg.UnlockRoomCount = CPlayerData.m_UnlockRoomCount;
                msg.UnlockWarehouseRoomCount = CPlayerData.m_UnlockWarehouseRoomCount;
                msg.IsWarehouseRoomUnlocked = CPlayerData.m_IsWarehouseRoomUnlocked;
                msg.IsShopOpen = CPlayerData.m_IsShopOpen;
                msg.IsWarehouseDoorClosed = CPlayerData.m_IsWarehouseDoorClosed;
            }
            else
            {
                msg.TutorialIndex = CPlayerData.m_TutorialIndex;
                var tut = CPlayerData.m_TutorialDataList;
                if (tut != null)
                    foreach (var td in tut)
                        msg.Tutorials.Add(new ShopTutorialEntry { Condition = (int)td.tutorialTaskCondition, Value = td.value });
            }
            return msg;
        }

        private void SendSlice(int index)
        {
            if (BroadcastState != null)
                BroadcastState(BuildSliceMessage(index));
        }

        private void SendBillsNow() => SendSlice(0);
        private void SendRoomNow() => SendSlice(1);
        private void SendTutorialNow() => SendSlice(2);

        public void HostApplyOp(ShopOpMessage message)
        {
            if (CoopCore.Role != CoopRole.Host)
                return;
            byte op = message.Op;
            byte arg = message.Arg;
            try
            {
                switch (op)
                {
                    case OpPayBill:
                        HostPayBill(arg);
                        break;
                    case OpUnlock:
                        HostUnlock(arg);
                        break;
                    case OpToggleSign:
                        HostToggleSign(arg);
                        break;
                    case OpToggleLight:
                        HostToggleLight();
                        CoopCore.Instance?.ForceLightResend();
                        break;
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("ShopStateSync op " + op + ": " + e.Message); }
            ForceResend(); // echo promptly even if the op was refused (re-aligns the joiner)
        }

        /// <summary>Host: run the guest's forwarded shop-light toggle in the real sim. The
        /// LightState broadcaster then ships the flipped m_IsShopLightOn to all clients.</summary>
        private void HostToggleLight()
        {
            var lm = SceneRef<LightManager>.Get();
            if (lm != null)
                lm.ToggleShopLight();
        }

        /// <summary>Host: apply a joiner's forwarded tutorial credit. Advances the condition to at
        /// least the client's value, so a credit the host already applied for the same action is
        /// not double-counted. The next ShopState broadcast carries the result back.</summary>
        public void HostApplyTutorialCredit(TutorialCreditMessage message)
        {
            if (CoopCore.Role != CoopRole.Host || message == null)
                return;
            Guarded("tut-credit", () =>
            {
                // Never let a malformed credit mint a junk condition or a non-finite value into
                // the host's persisted tutorial list (mirrors the economy-contribution guard).
                if (!Enum.IsDefined(typeof(ETutorialTaskCondition), message.Condition)
                    || float.IsNaN(message.Value) || float.IsInfinity(message.Value)
                    || message.Value < 0f)
                {
                    CoopPlugin.Log.LogWarning(
                        $"ShopStateSync: ignoring invalid tutorial credit condition={message.Condition} value={message.Value}");
                    return;
                }
                var condition = (ETutorialTaskCondition)message.Condition;
                float have = TutorialValue(condition);
                float delta = message.Value - have;
                if (delta > 0f)
                {
                    TutorialManager.AddTaskValue(condition, delta);
                    CoopPlugin.Log.LogInfo(
                        $"ShopStateSync: applied client tutorial credit {condition} +{delta:0.##} (now {message.Value:0.##})");
                }
            });
            ForceResend(); // echo promptly so the joiner's optimistic value is confirmed
        }

        private void HostPayBill(byte billType)
        {
            var screen = BillScreen();
            if (screen == null)
            {
                CoopPlugin.Log.LogWarning("ShopStateSync: RentBillScreen not found; pay op dropped");
                return;
            }
            // the vanilla handlers hold all the rules: amount>0, coin check, transaction
            // log, report bookkeeping, SetBill. A double-clicked op no-ops on the re-run.
            switch (billType)
            {
                case 0:
                    screen.OnPressPayAllBill();
                    break;
                case (byte)EBillType.Rent:
                    screen.OnPressPayRentBill();
                    break;
                case (byte)EBillType.Electric:
                    screen.OnPressPayElectricBill();
                    break;
                case (byte)EBillType.Employee:
                    screen.OnPressPaySalaryBill();
                    break;
            }
        }

        private void HostUnlock(byte kind)
        {
            // Mirrors ExpansionShopUIScreen.EvaluateCartCheckout / OnPressUnlockShopB
            // verbatim, minus the screen-refresh calls: the vanilla methods need a live
            // screen instance with the right private tab selected, which the host may not
            // have open. Cost/eligibility are recomputed from HOST state so a stale
            // joiner UI can neither underpay nor double-buy.
            var urm = Urm();
            if (urm == null)
                return;
            var gm = SceneRef<CGameManager>.Get();
            if (gm != null && gm.m_IsPrologue)
                return;

            if (kind == 2) // shop lot B
            {
                if (CPlayerData.m_IsWarehouseRoomUnlocked)
                    return;
                float price = urm.m_ShopB_UnlockPrice;
                if (CPlayerData.m_ShopLevel + 1 < urm.m_ShopB_UnlockLevelRequired)
                    return;
                if (CPlayerData.m_CoinAmountDouble < (double)price)
                    return;
                PriceChangeManager.AddTransaction(0f - price, ETransactionType.ShopExpansion, 1, -1);
                CEventManager.QueueEvent(new CEventPlayer_ReduceCoin(price));
                urm.SetUnlockWarehouseRoom(isUnlocked: true);
                AchievementManager.OnShopLotBUnlocked();
                CEventManager.QueueEvent(new CEventPlayer_AddShopExp(Mathf.Clamp(Mathf.RoundToInt(price / 100f), 5, 100)));
                CPlayerData.m_GameReportDataCollect.upgradeCost -= price;
                CPlayerData.m_GameReportDataCollectPermanent.upgradeCost -= price;
                SoundManager.PlayAudio("SFX_CustomerBuy", 0.6f);
            }
            else if (kind == 1) // next warehouse room
            {
                if (!CPlayerData.m_IsWarehouseRoomUnlocked)
                    return; // lot B must exist first
                int index = CPlayerData.m_UnlockWarehouseRoomCount;
                if (index >= urm.m_LockedWarehouseRoomBlockerList.Count)
                    return;
                float cost = CPlayerData.GetUnlockWarehouseRoomCost(index);
                if (CPlayerData.m_CoinAmountDouble < (double)cost)
                    return;
                PriceChangeManager.AddTransaction(0f - cost, ETransactionType.ShopExpansion, 0, index);
                CEventManager.QueueEvent(new CEventPlayer_ReduceCoin(cost));
                urm.StartUnlockNextWarehouseRoom();
                CEventManager.QueueEvent(new CEventPlayer_AddShopExp(Mathf.Clamp(Mathf.RoundToInt(cost / 100f), 5, 100)));
                CPlayerData.m_GameReportDataCollect.upgradeCost -= cost;
                CPlayerData.m_GameReportDataCollectPermanent.upgradeCost -= cost;
                SoundManager.PlayAudio("SFX_CustomerBuy", 0.6f);
            }
            else // next shop room
            {
                int index = CPlayerData.m_UnlockRoomCount;
                if (index >= urm.m_LockedRoomBlockerList.Count)
                    return;
                float cost = CPlayerData.GetUnlockShopRoomCost(index);
                if (CPlayerData.m_CoinAmountDouble < (double)cost)
                    return;
                PriceChangeManager.AddTransaction(0f - cost, ETransactionType.ShopExpansion, 1, index);
                CEventManager.QueueEvent(new CEventPlayer_ReduceCoin(cost));
                urm.StartUnlockNextRoom();
                CEventManager.QueueEvent(new CEventPlayer_AddShopExp(Mathf.Clamp(Mathf.RoundToInt(cost / 100f), 5, 100)));
                CPlayerData.m_GameReportDataCollect.upgradeCost -= cost;
                CPlayerData.m_GameReportDataCollectPermanent.upgradeCost -= cost;
                SoundManager.PlayAudio("SFX_CustomerBuy", 0.6f);
            }
            // vanilla defers this by a second from the screen; immediate is equivalent
            try
            {
                if (_shelfMgr == null)
                    _shelfMgr = UnityEngine.Object.FindObjectOfType<ShelfManager>();
                if (_shelfMgr != null)
                    _shelfMgr.SaveInteractableObjectData();
            }
            catch (System.Exception e) { Swallow.Log(e); }
        }

        private void HostToggleSign(byte which)
        {
            // the sign's own click handler: tutorial gate, flip animation, mesh swap,
            // and the m_IsSwapping debounce (a mid-swap op is dropped; the echo simply
            // re-asserts the unchanged state and the joiner's sign snaps back)
            if (which == 0)
            {
                var sign = OpenSign();
                if (sign != null)
                    sign.OnMouseButtonUp();
            }
            else
            {
                var sign = WarehouseSign();
                if (sign != null)
                    sign.OnMouseButtonUp();
            }
        }

        // ---------------- client ----------------

        public void ClientApplyState(ShopStateMessage message)
        {
            ApplyingRemote = true;
            try
            {
                Guarded("apply", () => ClientApplyInner(message));
            }
            finally { ApplyingRemote = false; }
        }

        private void ClientApplyInner(ShopStateMessage message)
        {
            if (message == null)
                return;
            if (message.Full || message.Index == 0)
                ApplyBills(message);
            if (message.Full || message.Index == 1)
                ApplyRooms(message);
            if (message.Full || message.Index == 2)
                ApplyTutorialMessage(message);
        }

        private void ApplyBills(ShopStateMessage message)
        {
            bool changed = ApplyBill(EBillType.Rent, message.Rent);
            changed |= ApplyBill(EBillType.Electric, message.Electric);
            changed |= ApplyBill(EBillType.Employee, message.Employee);
            if (changed && BillScreen() != null)
            {
                MiBillEvaluateUI?.Invoke(_billScreen, null);
                MiBillNotification?.Invoke(_billScreen, null);
            }
        }

        private static bool ApplyBill(EBillType billType, ShopBillEntry entry)
        {
            // Keep the protocol field and the game's non-zero-based enum mapping explicit.
            var bill = CPlayerData.GetBill(billType);
            if (bill.billDayPassed == entry.DayPassed && bill.amountToPay == entry.AmountToPay)
            {
                return false;
            }
            bill.billDayPassed = entry.DayPassed;
            bill.amountToPay = entry.AmountToPay;
            return true;
        }

        private void ApplyRooms(ShopStateMessage message)
        {
            var urm = Urm();
            if (urm == null)
                return;
            bool unlocksChanged = (message.IsWarehouseRoomUnlocked && !CPlayerData.m_IsWarehouseRoomUnlocked)
                || CPlayerData.m_UnlockRoomCount < message.UnlockRoomCount
                || CPlayerData.m_UnlockWarehouseRoomCount < message.UnlockWarehouseRoomCount;
            if (message.IsWarehouseRoomUnlocked && !CPlayerData.m_IsWarehouseRoomUnlocked)
                urm.SetUnlockWarehouseRoom(isUnlocked: true);
            for (int guard = 0; CPlayerData.m_UnlockRoomCount < message.UnlockRoomCount && guard < 64; guard++)
                urm.StartUnlockNextRoom();
            for (int guard = 0; CPlayerData.m_UnlockWarehouseRoomCount < message.UnlockWarehouseRoomCount && guard < 64; guard++)
                urm.StartUnlockNextWarehouseRoom();
            if (CPlayerData.m_IsShopOpen != message.IsShopOpen)
            {
                CPlayerData.m_IsShopOpen = message.IsShopOpen;
                var sign = OpenSign();
                if (sign != null)
                    MiOpenSignMesh?.Invoke(sign, null);
            }
            if (CPlayerData.m_IsWarehouseDoorClosed != message.IsWarehouseDoorClosed)
            {
                CPlayerData.m_IsWarehouseDoorClosed = message.IsWarehouseDoorClosed;
                var sign = WarehouseSign();
                if (sign != null)
                    MiWarehouseSignMesh?.Invoke(sign, null);
                else
                    urm.EvaluateWarehouseRoomOpenClose();
            }
            double now = Time.realtimeSinceStartupAsDouble;
            if (unlocksChanged || now - _lastRoomRepaint > 60.0)
            {
                _lastRoomRepaint = now;
                MiRoomInit?.Invoke(urm, null);
            }
        }

        private void ApplyTutorialMessage(ShopStateMessage message)
        {
            int count = message.Tutorials == null ? 0 : Math.Min(message.Tutorials.Count, 4096);
            var cur = CPlayerData.m_TutorialDataList;
            bool same = message.TutorialIndex == CPlayerData.m_TutorialIndex
                && cur != null && cur.Count == count;
            if (same)
                for (int i = 0; i < count; i++)
                    if (cur[i].tutorialTaskCondition != (ETutorialTaskCondition)message.Tutorials[i].Condition
                        || Mathf.Abs(cur[i].value - message.Tutorials[i].Value) > 0.001f)
                    {
                        same = false;
                        break;
                    }
            bool leavingIntro = message.TutorialIndex != 0 && CPlayerData.m_TutorialIndex == 0;
            if (same)
            {
                // Normally an unchanged sweep slice needs no scene work at all. The one
                // exception is the host-only transition out of the naming step, whose HUD
                // presentation must still be repaired even when task data is unchanged.
                if (leavingIntro)
                    ApplyTutorialPresentation(message.TutorialIndex);
                else
                    SyncTutorialMarker(_tutorialManager, message.TutorialIndex);
                return;
            }

            var incoming = new System.Collections.Generic.List<TutorialData>(count);
            for (int i = 0; i < count; i++)
                incoming.Add(new TutorialData { tutorialTaskCondition = (ETutorialTaskCondition)message.Tutorials[i].Condition, value = message.Tutorials[i].Value });
            ApplyTutorial(message.TutorialIndex, incoming);
        }

        /// <summary>Client: replay the host's authoritative task progress so the guest's
        /// tutorial panel advances in step. No-ops when nothing changed (so a routine
        /// ShopState heal doesn't churn the panel). Resets each subgroup's private progress
        /// then re-feeds the snapshot value ONCE, which sets rather than accumulates.</summary>
        private TutorialManager GetTutorialManager()
        {
            if (_tutorialManager == null)
                _tutorialManager = UnityEngine.Object.FindObjectOfType<TutorialManager>(); // NOT CSingleton (fake-manager trap)
            return _tutorialManager;
        }

        private void ApplyTutorialPresentation(int tutIndex)
        {
            var tm = GetTutorialManager();
            SyncTutorialMarker(tm, tutIndex);
            if (tutIndex != 0 && CPlayerData.m_TutorialIndex == 0)
                ClearTutorialIntroPresentation();
        }

        private static void SyncTutorialMarker(TutorialManager tm, int tutIndex)
        {
            if (tm == null || tm.m_TutorialTargetIndicator == null)
                return;
            try
            {
                bool want = tutIndex == 0;
                if (tm.m_TutorialTargetIndicator.activeSelf != want)
                    tm.m_TutorialTargetIndicator.SetActive(want);
            }
            catch (Exception e) { Swallow.Log(e); }
        }

        private void ApplyTutorial(int tutIndex, System.Collections.Generic.List<TutorialData> incoming)
        {
            var tm = GetTutorialManager();

            // 1.0's shop-naming marker (m_TutorialTargetIndicator) is shown only while the tutorial
            // is at step 0, and is cleared by the LOCAL naming trigger / OnPressConfirmShopName -
            // neither of which a joiner runs - so a guest's marker stayed up after the host named
            // the shop. Mirror the host's step on every apply (heals included), before the
            // unchanged-data early return below.
            SyncTutorialMarker(tm, tutIndex);

            // Same class of host-only local action as the marker above: at step 0 the tutorial
            // calls ShopRenamer.SetIsTutorial(), which ADDS the seven movement key tooltips and
            // fades GameUIScreen out. Completing the step runs the walk-in trigger (tooltips off)
            // and OnPressConfirmShopName (GameUIScreen back on) - neither of which a joiner runs.
            // SetIsTutorial() DOES run on a joiner (TutorialManager.OnGameDataFinishLoaded reaches
            // it whenever the borrowed world was still at step 0), so without this a guest's HUD
            // stayed faded out and its movement tooltips stayed stuck once the host named the shop.
            // Mirror it on the frame the tutorial leaves step 0.
            if (tutIndex != 0 && CPlayerData.m_TutorialIndex == 0)
                ClearTutorialIntroPresentation();

            var cur = CPlayerData.m_TutorialDataList;
            // skip if identical to what we already have (avoids UI churn every heal)
            bool same = tutIndex == CPlayerData.m_TutorialIndex && cur != null && cur.Count == incoming.Count;
            if (same)
                for (int i = 0; i < incoming.Count; i++)
                    if (cur[i].tutorialTaskCondition != incoming[i].tutorialTaskCondition
                        || Mathf.Abs(cur[i].value - incoming[i].value) > 0.001f)
                    {
                        same = false;
                        break;
                    }
            if (same)
                return;

            if (CPlayerData.m_TutorialDataList == null)
                CPlayerData.m_TutorialDataList = new System.Collections.Generic.List<TutorialData>();
            CPlayerData.m_TutorialDataList.Clear();
            CPlayerData.m_TutorialDataList.AddRange(incoming);
            CPlayerData.m_TutorialIndex = tutIndex;

            if (tm == null || tm.m_TutorialSubGroupList == null)
                return;
            foreach (var sg in tm.m_TutorialSubGroupList)
            {
                if (sg == null)
                    continue;
                try
                {
                    FiSgCurrent?.SetValue(sg, 0f);
                    FiSgFinish?.SetValue(sg, false);
                    if (sg.m_TutorialData != null)
                        sg.m_TutorialData.value = 0f;
                    // only the subgroup whose condition matches actually consumes each value
                    for (int i = 0; i < incoming.Count; i++)
                        sg.AddTaskValue(incoming[i].value, incoming[i].tutorialTaskCondition);
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("tutorial subgroup apply: " + e.Message); }
            }
            try
            {
                tm.EvaluateTaskVisibility();
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("tutorial visibility: " + e.Message); }
        }

        // Exactly the set ShopRenamer.SetIsTutorial() adds and its walk-in trigger removes.
        private static readonly EGameAction[] TutorialIntroTooltips =
        {
            EGameAction.MoveForward,
            EGameAction.MoveLeft,
            EGameAction.MoveBackward,
            EGameAction.MoveRight,
            EGameAction.Jump,
            EGameAction.Sprint,
            EGameAction.Crouch,
        };

        /// <summary>Client: the local half of completing the shop-naming step. Reproduces
        /// ShopRenamer.OnTriggerEnter (movement tooltips off) and OnPressConfirmShopName
        /// (GameUIScreen visible again) so the joiner's presentation matches the host's.
        /// Both lookups go through <see cref="SceneRef{T}"/> so a missing manager is a no-op
        /// instead of a fabricated singleton.</summary>
        private static void ClearTutorialIntroPresentation()
        {
            try
            {
                if (SceneRef<InteractionPlayerController>.Get() != null)
                {
                    for (int i = 0; i < TutorialIntroTooltips.Length; i++)
                        InteractionPlayerController.RemoveToolTip(TutorialIntroTooltips[i]);
                }
            }
            catch (Exception e) { Swallow.Log(e); }
            try
            {
                if (SceneRef<GameUIScreen>.Get() != null)
                    GameUIScreen.SetGameUIVisible(isVisible: true);
            }
            catch (Exception e) { Swallow.Log(e); }
            CoopPlugin.Log.LogInfo("tutorial intro cleared: restored the game HUD and removed the movement tooltips");
        }
    }
}
