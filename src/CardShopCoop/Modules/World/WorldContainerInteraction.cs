using CardShopCoop.Util;
using CardShopCoop.Net;
using CardShopCoop.Runtime;
using CardShopCoop.Modules.Prediction;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace CardShopCoop.Modules.World
{
    /// <summary>
    /// Mirrors the CONTENTS of the placed container stations - card storage shelves,
    /// bulk donation boxes, auto pack openers, empty box storages and auto cleansers -
    /// which Placement only mirrors as physical objects. All of their internals
    /// (card lists, pack queues, box counts, spray cans) are per-client in vanilla, so
    /// cards the joiner donated landed in a box the host saw as empty and the box
    /// station literally ate the joiner's boxes.
    ///
    /// Host-authoritative, keyed by Placement (kind, index): mutation hooks observe the
    /// game's own change and forward exactly one changed record/intent; the host applies
    /// guest intents through the vanilla methods, and the next broadcast is the
    /// authoritative state.
    ///
    /// Pack opener specifics: the client runs the pack opener vanilla, including its local
    /// Update/RNG. The host's authoritative container record carries the rolled output and
    /// the queue, so the next broadcast corrects the client's local roll. Collect runs the
    /// reveal UI on the COLLECTOR (its AddCard calls travel through the existing CardDelta
    /// mirror into the shared binder), while the host clears the machine and banks the
    /// report counters WITHOUT re-adding the cards. No coin moves through this module, so
    /// the double-charge question never arises.
    /// </summary>
    public class WorldContainerInteraction : CoopModule
    {
        // Placement kind numbering so "kind 11 index 2"
        // means the same machine on every peer
        private const int KindCardStorage = 9;
        private const int KindCleanser = 10;
        private const int KindPackOpener = 11;
        private const int KindEmptyBoxStorage = 12;
        private const int KindDonation = 13;

        // client -> host op codes (first byte of a ContainerOp payload)
        private const byte OpContentSet = 1;
        private const byte OpPackInsert = 2;
        private const byte OpPackTurnOn = 3;
        private const byte OpPackCollect = 4;
        private const byte OpPackClaim = 6;
        private const byte OpCleanserToggle = 7;
        private const byte OpCleanserRefill = 8;
        private const byte OpWorkerTakeFlag = 9;
        private const byte OpEmptyBoxTake = 10;
        private const byte OpEmptyBoxStore = 11;

        /// <summary>Set by WorldCardInteraction: client -> host op (ContainerOpMessage).</summary>
        public Action<INetMessage> SendOp;
        /// <summary>Set by WorldCardInteraction: host -> clients state (ContainerStateMessage).</summary>
        public Action<INetMessage> BroadcastState;
        public Action<int, INetMessage> SendToClient;
        /// <summary>Host -> every client except one, for the per-actor hold flags.</summary>
        internal Action<int, INetMessage> RelayState;
        internal Func<bool> InGameProvider;
        internal Func<bool> ReloadingProvider;

        private static WorldContainerInteraction Current => WorldHostBehaviour.ActiveCards?.Containers
            ?? WorldClientBehaviour.ActiveCards?.Containers;

        private static bool InGame => Current != null && Current.InGameProvider != null
            && Current.InGameProvider();
        private static bool Reloading => Current != null && Current.ReloadingProvider != null
            && Current.ReloadingProvider();

        /// <summary>True while sync code itself mutates a container, so the forwarding
        /// patches don't mistake an applied echo for a local player action.</summary>
        public static bool ApplyingRemote;

        // private game state this module must read/write (no public accessors exist)
        private static readonly FieldInfo FiPoIsProcessing =
            ReflectionSurface.RequiredField(typeof(InteractableAutoPackOpener), "m_IsProcessing");
        private static readonly FieldInfo FiPoOpenTimer =
            ReflectionSurface.RequiredField(typeof(InteractableAutoPackOpener), "m_PackOpenTimer");
        private static readonly FieldInfo FiPoOpenedCount =
            ReflectionSurface.RequiredField(typeof(InteractableAutoPackOpener), "m_PackOpenedCount");
        private static readonly FieldInfo FiPoUI =
            ReflectionSurface.RequiredField(typeof(InteractableAutoPackOpener), "m_AutoCardOpenerUI");
        private static readonly FieldInfo FiClTurnedOn =
            ReflectionSurface.RequiredField(typeof(InteractableAutoCleanser), "m_IsTurnedOn");
        private static readonly FieldInfo FiClNeedRefill =
            ReflectionSurface.RequiredField(typeof(InteractableAutoCleanser), "m_IsNeedRefill");
        private static readonly FieldInfo FiClCooldown =
            ReflectionSurface.RequiredField(typeof(InteractableAutoCleanser), "m_IsSprayOnCooldown");
        private static readonly FieldInfo FiClTimer =
            ReflectionSurface.RequiredField(typeof(InteractableAutoCleanser), "m_Timer");
        // the cleanser is the only container whose count is stored independently of its
        // list (every other one derives from m_StoredItemList.Count), and vanilla indexes
        // the list with it - so the reconcile has to be able to force the two back into
        // agreement rather than trust either one. See ApplyCleanserState.
        private static readonly FieldInfo FiClItemAmount =
            ReflectionSurface.RequiredField(typeof(InteractableAutoCleanser), "m_ItemAmount");
        private static readonly FieldInfo FiEmptyStoredCount =
            ReflectionSurface.RequiredField(typeof(InteractableEmptyBoxStorage), "m_StoredBoxCount");
        private static readonly MethodInfo MiEmptyEvaluateStack =
            ReflectionSurface.RequiredMethod(typeof(InteractableEmptyBoxStorage),
                "EvaluateStoredBoxStackHeight");
        private static readonly FieldInfo FiScreenShelf =
            AccessTools.Field(typeof(BulkDonationBoxUIScreen), "m_InteractableCardStorageShelf");
        private static readonly FieldInfo FiScreenDonation =
            AccessTools.Field(typeof(BulkDonationBoxUIScreen), "m_InteractableBulkDonationBox");
        private static readonly FieldInfo FiScreenPage =
            AccessTools.Field(typeof(BulkDonationBoxUIScreen), "m_PageIndex");
        private static readonly FieldInfo FiScreenPageLimit =
            AccessTools.Field(typeof(BulkDonationBoxUIScreen), "m_PageLimitIndex");
        private static readonly FieldInfo FiScreenCurrentSlot =
            AccessTools.Field(typeof(BulkDonationBoxUIScreen), "m_CurrentSelectedSlotIndex");
        private static readonly FieldInfo FiScreenPanels =
            AccessTools.Field(typeof(BulkDonationBoxUIScreen), "m_BulkDonationBoxCardPanelUIList");
        private static readonly MethodInfo MiEvaluateCardPanelUI =
            AccessTools.Method(typeof(BulkDonationBoxUIScreen), "EvaluateCardPanelUI", new[] { typeof(int) });
        private static readonly FieldInfo FiModalParent =
            AccessTools.Field(typeof(BulkDonationBoxPlusMinusScreen), "m_BulkDonationBoxUIScreen");
        private static readonly FieldInfo FiModalCardData =
            AccessTools.Field(typeof(BulkDonationBoxPlusMinusScreen), "m_CardData");
        private static readonly FieldInfo FiModalInput =
            AccessTools.Field(typeof(BulkDonationBoxPlusMinusScreen), "m_CardAmountInput");
        private static readonly FieldInfo FiModalStackCount =
            AccessTools.Field(typeof(BulkDonationBoxPlusMinusScreen), "m_StackCardCount");
        private static readonly FieldInfo FiModalBoxTotal =
            AccessTools.Field(typeof(BulkDonationBoxPlusMinusScreen), "m_BoxTotalCardCount");
        private static readonly MethodInfo MiModalIsOpened =
            AccessTools.Method(typeof(UIScreenBase), "IsScreenOpened");
        private static readonly MethodInfo MiModalClose =
            AccessTools.Method(typeof(UIScreenBase), "CloseScreen");

        /// <summary>One submitted pack-collect claim awaiting its ordered authoritative delta.
        /// The claim needs its token (the collect identity) and the cards echoed back in that
        /// collect; vanilla's own collect on the claimant already banked and revealed, so nothing
        /// is applied when the delta lands beyond clearing this entry.</summary>
        private sealed class PendingPackCollection
        {
            public int ClaimToken;
            public List<CompactCardDataAmount> Cards;
        }

        /// <summary>Pre-action snapshot for an observed pack-opener click. Vanilla
        /// <c>OnMouseButtonUp</c> consumes the machine's processing/output, so the postfix cannot
        /// read the pre-click state from the machine; this captures what vanilla is about to act
        /// on so the postfix can forward exactly the one intent vanilla performed.</summary>
        internal sealed class PackOpenerObserve
        {
            internal int Index;
            internal bool PriorProcessing;
            internal int PriorStored;
            internal int PriorOutput;
            internal int PriorOpened;
            internal int PriorState;
            internal float PriorTimer;
        }

        /// <summary>Pre-action snapshot for an observed worker-take flag change.</summary>
        internal sealed class WorkerTakeObserve
        {
            internal int Index;
            internal bool Prior;
            internal bool Desired;
        }

        /// <summary>Pre-action snapshot for an observed cleanser toggle.</summary>
        internal sealed class CleanserToggleObserve
        {
            internal int Index;
            internal bool Prior;
            internal bool PriorCooldown;
            internal float PriorTimer;
        }

        /// <summary>Pre-action snapshot for an observed empty-box store. The game's own
        /// <c>StoreBox</c> destroys the box, so its identity, the storage count and the consume
        /// guard are captured before vanilla runs.</summary>
        internal sealed class EmptyBoxStoreObserve
        {
            internal WorldContainerInteraction Self;
            internal InteractableEmptyBoxStorage Storage;
            internal int Index;
            internal Guid BoxNetworkId;
            internal int PriorCount;
            internal bool IsPlayer;
            internal bool Consuming;
            internal bool Ended;

            /// <summary>Releases the box-destroy suppression exactly once, even when vanilla
            /// StoreBox threw after the prefix armed it.</summary>
            internal void EndConsume()
            {
                if (!Consuming || Ended)
                {
                    return;
                }

                Ended = true;
                Self._boxes?.EndContainerConsume();
            }
        }

        private readonly Dictionary<object, int> _hostKeys = new();
        // A partial is exactly one ContainerRecord. Index is the packed kind/index identity;
        // Records carries the record itself, so omitted records are never deletions.
        private BulkDonationBoxUIScreen _cachedContainerScreen;
        private BulkDonationBoxPlusMinusScreen _cachedAmountModal;
        private readonly Dictionary<int, PendingPackCollection> _pendingPackCollections = new();
        private readonly Dictionary<int, int> _packClaimOwner = new();
        private readonly Dictionary<int, int> _packClaimToken = new();
        // Client-side claim bookkeeping: a machine counts as claimed from the moment the local
        // click sends OpPackClaim until the collect completes or is rolled back. The host's own
        // claim maps are _packClaimOwner/_packClaimToken.
        private readonly HashSet<int> _claimedPackIndices = new();
        // The prediction ids of this peer's pack inserts still in flight. Their echoes reconcile in
        // layers (see ClientApplyDelta) instead of applying the stale record raw.
        private readonly HashSet<Guid> _pendingPackInserts = new();
        private int _nextPackClaimToken = 1;
        private ContainerStateMessage _pendingClientState;
        private readonly BoxNetworkInteraction _boxes;
        private readonly PlayerBoxInteraction _playerBox;
        private Guid _hostPredictionId;
        private int _hostActorConnectionId;
        private BoxNetworkState _hostDeltaBox;
        private bool _hostDeltaTakeIntoHand;
        private bool _hostDeltaReleaseHold;
        private bool _hostCompletePackCollection;
        private byte _hostPackIndex;

        internal WorldContainerInteraction(BoxNetworkInteraction boxes = null,
            PlayerBoxInteraction playerBox = null)
        {
            _boxes = boxes;
            _playerBox = playerBox;
        }

        /// <summary>Moves a container's authoritative box into the local player's hand through
        /// the shared single-hand path, so a take can never stack a second box on one already held.</summary>
        private void TakeIntoLocalHand(InteractablePackagingBox box)
        {
            if (box == null)
            {
                return;
            }

            if (_playerBox != null)
            {
                // The container take prediction (or the host's authoritative delta) owns this hold.
                _playerBox.TakeCoveredIntoLocalHand(box);
                return;
            }

            var controller = SceneRef<InteractionPlayerController>.Get();
            if (controller != null)
            {
                box.StartHoldBox(true, controller.m_HoldItemPos);
            }
        }

        public override string Name => "containers";

        public override void Reset()
        {
            _hostKeys.Clear();
            _cachedContainerScreen = null;
            _cachedAmountModal = null;
            _claimedPackIndices.Clear();
            _pendingPackInserts.Clear();
            _pendingPackCollections.Clear();
            _packClaimOwner.Clear();
            _packClaimToken.Clear();
            _nextPackClaimToken = 1;
            _pendingClientState = null;
            _hostPredictionId = Guid.Empty;
            _hostActorConnectionId = 0;
            _hostDeltaBox = null;
            _hostDeltaTakeIntoHand = false;
            _hostDeltaReleaseHold = false;
            _hostCompletePackCollection = false;
            _hostPackIndex = 0;
        }

        internal void FlushClientState()
        {
            if (_pendingClientState == null || !InGame)
                return;

            var pending = _pendingClientState;
            _pendingClientState = null;
            ClientApplyState(pending);
        }

        internal void AppendBaselineMessages(Action<INetMessage> append)
        {
            if (SendToClient != null && append != null && InGame)
            {
                append(BuildBaselineMessage());
            }
        }

        private ContainerStateMessage BuildBaselineMessage()
        {
            var records = new List<ContainerRecord>();
            var keys = GetContainerKeys();
            for (var i = 0; i < keys.Count; i++)
            {
                records.Add(BuildRecord(keys[i] >> 8, keys[i] & 0xFF));
            }

            return new ContainerStateMessage
            {
                Records = records,
            };
        }

        /// <summary>Release a disconnected client's pack-opener claims so a failed
        /// collection cannot lock the machine until the whole session resets.</summary>
        public void HostReleaseConn(int connId)
        {
            var released = new List<int>();
            foreach (var pair in _packClaimOwner)
            {
                if (pair.Value == connId)
                {
                    released.Add(pair.Key);
                }
            }

            for (var i = 0; i < released.Count; i++)
            {
                var key = released[i];
                _packClaimOwner.Remove(key);
                _packClaimToken.Remove(key);
            }
            if (released.Count > 0)
            {
                CoopPlugin.Log.LogInfo($"WorldContainerInteraction: released {released.Count} pack claim(s) from disconnected client {connId}");
            }
        }

        private ShelfManager Sm()
            => SceneRef<ShelfManager>.Get();

        private int IndexOf(int kind, object obj)
        {
            var sm = Sm();
            if (sm == null)
            {
                return -1;
            }

            var list = PlacementApi.GetList(sm, kind);
            if (list == null)
            {
                return -1;
            }

            var idx = list.IndexOf(obj);
            return idx < 250 ? idx : -1; // wire index is a byte (matches Placement's cap)
        }

        private T Get<T>(int kind, int idx) where T : class
        {
            var sm = Sm();
            if (sm == null)
            {
                return null;
            }

            var list = PlacementApi.GetList(sm, kind);
            if (list == null || idx < 0 || idx >= list.Count)
            {
                return null;
            }

            return list[idx] as T;
        }

        // ---------------- host: change pushes ----------------

        private List<int> GetContainerKeys()
        {
            var result = new List<int>();
            var sm = Sm();
            if (sm == null)
            {
                return result;
            }

            _hostKeys.Clear();
            AddKeys(result, sm, KindCardStorage);
            AddKeys(result, sm, KindDonation);
            AddKeys(result, sm, KindPackOpener);
            AddKeys(result, sm, KindEmptyBoxStorage);
            AddKeys(result, sm, KindCleanser);
            return result;
        }

        private void AddKeys(List<int> result, ShelfManager sm, int kind)
        {
            var list = PlacementApi.GetList(sm, kind);
            if (list == null)
            {
                return;
            }

            for (var i = 0; i < list.Count && i < 250; i++)
            {
                if (list[i] != null)
                {
                    var key = (kind << 8) | i;
                    result.Add(key);
                    _hostKeys[list[i]] = key;
                }
            }
        }

        private void SendHostRecord(int key)
        {
            var record = BuildRecord(key >> 8, key & 0xFF);
            var delta = new ContainerDeltaMessage
            {
                PredictionId = _hostPredictionId,
                Key = key,
                Record = record,
                HasBox = _hostDeltaBox != null,
                Box = _hostDeltaBox,
                TakeIntoHand = _hostDeltaTakeIntoHand,
                ReleaseHold = _hostDeltaReleaseHold,
                CompletePackCollection = _hostCompletePackCollection,
                PackIndex = _hostPackIndex,
            };

            // TakeIntoHand / ReleaseHold describe the acting player's own hands. Only the intent's
            // sender may apply them; every other peer receives the same record/box state with both
            // flags cleared, or it would grab/drop a box that is not its own.
            var actor = _hostActorConnectionId;
            if (actor > 0 && (delta.TakeIntoHand || delta.ReleaseHold)
                && SendToClient != null && RelayState != null)
            {
                SendToClient(actor, delta);
                RelayState(actor, new ContainerDeltaMessage
                {
                    PredictionId = delta.PredictionId,
                    Key = delta.Key,
                    Record = delta.Record,
                    HasBox = delta.HasBox,
                    Box = delta.Box,
                    TakeIntoHand = false,
                    ReleaseHold = false,
                    CompletePackCollection = delta.CompletePackCollection,
                    PackIndex = delta.PackIndex,
                });
                return;
            }

            BroadcastState?.Invoke(delta);
        }

        private void HostChanged(int kind, object container)
        {
            if (!InGame || container == null || BroadcastState == null)
            {
                return;
            }
            int key;
            if (!TryResolveHostKey(kind, container, out key))
            {
                return;
            }
            Guarded("change", () => SendHostRecord(key));
        }

        private bool TryResolveHostKey(int kind, object container, out int key)
        {
            key = 0;
            try
            {
                if (_hostKeys.TryGetValue(container, out key)
                    && (key >> 8) == kind
                    && ReferenceEquals(Get<object>(kind, key & 0xFF), container))
                {
                    return true;
                }
                GetContainerKeys();
                return _hostKeys.TryGetValue(container, out key)
                    && (key >> 8) == kind
                    && ReferenceEquals(Get<object>(kind, key & 0xFF), container);
            }
            catch (Exception e)
            {
                ModuleGuard.Log("containers:resolve-key", e);
                return false;
            }
        }

        private ContainerRecord BuildRecord(int kind, int idx)
        {
            var rec = new ContainerRecord
            {
                StableEntityId = WorldMessageMetadata.ContainerEntityId(kind, idx),
                Kind = (byte)kind,
                Index = (byte)idx,
            };
            switch (kind)
            {
                case KindCardStorage:
                    {
                        var s = Get<InteractableCardStorageShelf>(kind, idx);
                        rec.CanWorkerTake = s == null || s.CanWorkerTake();
                        rec.Cards = s?.GetCompactCardDataAmountList() ?? new List<CompactCardDataAmount>();
                        break;
                    }
                case KindDonation:
                    {
                        var b = Get<InteractableBulkDonationBox>(kind, idx);
                        rec.Cards = b?.GetCompactCardDataAmountList() ?? new List<CompactCardDataAmount>();
                        break;
                    }
                case KindPackOpener:
                    {
                        var p = Get<InteractableAutoPackOpener>(kind, idx);
                        var stored = p?.GetStoredItemList();
                        var n = Mathf.Min(stored?.Count ?? 0, 250);
                        rec.StoredTypes = new List<EItemType>(n);
                        for (var i = 0; i < n; i++)
                        {
                            // ONE id convention for the whole container family: WriteItemType here,
                            // (int)ReadItemType on the far side, like every other EItemType on the
                            // wire. An empty slot goes out as EItemType.None rather than the old
                            // literal 0 - 0 is a REAL item type, so a null slot would otherwise
                            // arrive indistinguishable from that item. The client reads these back
                            // to rebuild its visible queue (ApplyPackState).
                            rec.StoredTypes.Add(stored[i] != null ? stored[i].GetItemType() : EItemType.None);
                        }

                        rec.Processing = p != null && p.GetIsProcessing();
                        rec.Timer = p != null ? (FiPoOpenTimer?.GetValue(p) as float? ?? 0f) : 0f;
                        rec.OpenedCount = p != null ? p.GetPackOpenedCount() : 0;
                        rec.Cards = p?.GetCompactCardDataAmountList() ?? new List<CompactCardDataAmount>();
                        // Vanilla leaves m_CurrentState at 0 when a worker (or a full
                        // hopper) starts the machine through AddItem. The UI is still
                        // processing in that case, so advertise the effective state
                        // rather than the stale implementation detail.
                        rec.CurrentState = p != null && p.GetIsProcessing()
                            ? ((stored?.Count ?? 0) > 0 ? 1 : 2)
                            : 0;
                        rec.CollectClaimed = _packClaimOwner.ContainsKey((kind << 8) | idx);
                        break;
                    }
                case KindEmptyBoxStorage:
                    {
                        var storage = Get<InteractableEmptyBoxStorage>(kind, idx);
                        rec.Count = storage?.GetBoxStoredCount() ?? 0;
                        break;
                    }
                case KindCleanser:
                    {
                        var c = Get<InteractableAutoCleanser>(kind, idx);
                        byte flags = 0;
                        if (c != null && c.IsTurnedOn())
                        {
                            flags |= 1;
                        }

                        if (c == null || c.IsNeedRefill())
                        {
                            flags |= 2;
                        }

                        rec.Flags = flags;
                        var stored = c?.GetStoredItemList();
                        var n = Mathf.Min(stored?.Count ?? 0, 32);
                        rec.Fills = new List<float>(n);
                        for (var i = 0; i < n; i++)
                        {
                            rec.Fills.Add(stored[i] != null ? stored[i].GetContentFill() : 0f);
                        }

                        break;
                    }
            }
            return rec;
        }

        // ---------------- host: apply client ops ----------------

        public bool HostApplyOp(ContainerOpMessage message, int connId,
            Action<INetMessage> response = null)
        {
            if (message == null || connId <= 0)
            {
                return false;
            }

            var op = message.Op;
            var accepted = false;
            var reason = (string)null;
            BoxNetworkState createdBox = null;
            var kind = (int)message.Kind;
            var idx = (int)message.Index;
            if (op == OpWorkerTakeFlag)
                kind = KindCardStorage;
            else if (op == OpPackInsert || op == OpPackTurnOn || op == OpPackClaim
                || op == OpPackCollect)
                kind = KindPackOpener;
            else if (op == OpCleanserToggle || op == OpCleanserRefill)
                kind = KindCleanser;
            else if (op == OpEmptyBoxTake || op == OpEmptyBoxStore)
                kind = KindEmptyBoxStorage;
            _hostPredictionId = message.PredictionId;
            _hostActorConnectionId = connId;
            try
            {
                switch (op)
                {
                    case OpContentSet:
                        {
                            var canTake = message.CanWorkerTake;
                            var cards = message.Cards;
                            if ((kind != KindCardStorage && kind != KindDonation)
                                || Get<object>(kind, idx) == null)
                            {
                                reason = "container does not exist";
                                break;
                            }
                            if (cards == null)
                            {
                                reason = "container card payload is missing";
                                break;
                            }
                            // never drop this even if the host has the same UI open: the
                            // joiner's binder already paid these cards through the CardDelta
                            // mirror, so losing the list here would lose the cards for real
                            var priorCards = kind == KindCardStorage
                                ? new List<CompactCardDataAmount>(
                                    ((InteractableCardStorageShelf)Get<object>(kind, idx))
                                        .GetCompactCardDataAmountList())
                                : new List<CompactCardDataAmount>(
                                    ((InteractableBulkDonationBox)Get<object>(kind, idx))
                                        .GetCompactCardDataAmountList());
                            var priorCanTake = kind == KindCardStorage
                                && ((InteractableCardStorageShelf)Get<object>(kind, idx)).CanWorkerTake();
                            try
                            {
                                ApplyContent(kind, idx, cards, kind == KindCardStorage, canTake);
                                HostChanged(kind, Get<object>(kind, idx));
                            }
                            catch
                            {
                                var target = Get<object>(kind, idx);
                                if (target is InteractableCardStorageShelf shelf)
                                {
                                    var targetCards = shelf.GetCompactCardDataAmountList();
                                    targetCards.Clear();
                                    targetCards.AddRange(priorCards);
                                    shelf.SetCanWorkerTake(priorCanTake);
                                }
                                else if (target is InteractableBulkDonationBox donation)
                                {
                                    var targetCards = donation.GetCompactCardDataAmountList();
                                    targetCards.Clear();
                                    targetCards.AddRange(priorCards);
                                }
                                throw;
                            }
                            accepted = true;
                            break;
                        }
                    case OpWorkerTakeFlag:
                        {
                            var canTake = message.CanWorkerTake;
                            var s = Get<InteractableCardStorageShelf>(KindCardStorage, idx);
                            if (s == null)
                            {
                                reason = "card storage does not exist";
                                break;
                            }

                            ApplyingRemote = true;
                            try
                            {
                                var priorCanTake = s.CanWorkerTake();
                                try
                                {
                                    s.SetCanWorkerTake(canTake);
                                    s.OnCardStorageShelfSettingDone();
                                }
                                catch
                                {
                                    s.SetCanWorkerTake(priorCanTake);
                                    throw;
                                }
                            }
                            finally { ApplyingRemote = false; }
                            HostChanged(KindCardStorage, s);
                            accepted = true;
                            break;
                        }
                    case OpPackInsert:
                        {
                            var itemType = message.ItemType; // guest id -> ours; see PackOpenerAddItemPostfix
                            var p = Get<InteractableAutoPackOpener>(KindPackOpener, idx);
                            if (p == null)
                            {
                                reason = "pack opener does not exist";
                                break;
                            }
                            // a pack from a content pack THIS PC does not have arrives as
                            // EItemType.None. Note WHY this has to be an explicit value test:
                            // GetItemMeshData(None) does not fail, it returns a BLANK but NON-NULL
                            // ItemMeshData, so SpawnItem would happily build a meshless prop the
                            // opener then holds forever. Skip explicitly and say so once.
                            if (itemType == EItemType.None)
                            {
                                CoopPlugin.Log.LogWarning(
                                    "WorldContainerInteraction pack insert: item type has no counterpart here (one-sided content pack)");
                                reason = "pack type is unavailable on host";
                                break;
                            }
                            // The acting client's local machine can be ahead of the authoritative
                            // one: its vanilla collect resets the machine before this host applies
                            // OpPackCollect (the claim round trip still has to run), and two peers
                            // can pass their own local slot checks in the same instant. Re-check
                            // vanilla's own player gates against the host machine so neither race
                            // can push a pack past m_MaxPackCount (which vanilla itself never
                            // allows). A refusal rolls the client's insert prediction back, and its
                            // undo returns the pack to the acting hand - the pack is not eaten.
                            if (p.GetIsProcessing())
                            {
                                reason = "pack opener is already running";
                                break;
                            }
                            if (!p.HasEnoughSlot())
                            {
                                reason = "pack opener is full (" + p.GetStoredItemList().Count
                                    + "+" + p.GetPackOpenedCount() + "/" + p.m_MaxPackCount + ")";
                                break;
                            }
                            var priorStored = new List<Item>(p.GetStoredItemList());
                            var priorProcessing = p.GetIsProcessing();
                            var priorTimer = FiPoOpenTimer?.GetValue(p) as float? ?? 0f;
                            var priorOpenedCount = p.GetPackOpenedCount();
                            var priorState = p.m_CurrentState;
                            Item item = null;
                            try
                            {
                                item = SpawnItem(itemType, p.m_PosInside);

                                ApplyingRemote = true;
                                try
                                {
                                    p.AddItem(item, addToFront: true, isPlayer: false);
                                }
                                finally { ApplyingRemote = false; }
                                HostChanged(KindPackOpener, p);
                                accepted = true;
                            }
                            catch
                            {
                                RestorePackOpener(p, priorStored, priorProcessing, priorTimer,
                                    priorOpenedCount, priorState);
                                throw;
                            }
                            break;
                        }
                    case OpPackTurnOn:
                        {
                            var p = Get<InteractableAutoPackOpener>(KindPackOpener, idx);
                            // the state check pins vanilla OnMouseButtonUp to its turn-on
                            // branch; anything else means the click raced and is stale
                            if (p != null && !p.GetIsProcessing() && p.GetStoredItemList().Count > 0)
                            {
                                var priorStored = new List<Item>(p.GetStoredItemList());
                                var priorProcessing = p.GetIsProcessing();
                                var priorTimer = FiPoOpenTimer?.GetValue(p) as float? ?? 0f;
                                var priorOpenedCount = p.GetPackOpenedCount();
                                var priorState = p.m_CurrentState;
                                try
                                {
                                    p.OnMouseButtonUp();
                                    accepted = p.GetIsProcessing();
                                }
                                catch
                                {
                                    RestorePackOpener(p, priorStored,
                                        priorProcessing, priorTimer, priorOpenedCount, priorState);
                                    throw;
                                }
                            }
                            if (!accepted)
                                reason = p == null ? "pack opener does not exist" : "pack opener state changed";

                            break;
                        }
                    case OpPackClaim:
                        {
                            accepted = HostApplyPackClaim(message.Index, connId, out reason, response);
                            break;
                        }
                    case OpPackCollect:
                        {
                            var opener = Get<InteractableAutoPackOpener>(KindPackOpener, idx);
                            accepted = HostApplyPackCollect(idx, message.ClaimToken, connId,
                                out reason);
                            if (accepted)
                            {
                                _hostCompletePackCollection = true;
                                _hostPackIndex = message.Index;
                                HostChanged(KindPackOpener, opener);
                            }
                            break;
                        }
                    case OpCleanserToggle:
                        {
                            var on = message.TurnedOn;
                            var c = Get<InteractableAutoCleanser>(KindCleanser, idx);
                            if (c == null)
                            {
                                reason = "cleanser does not exist";
                                break;
                            }
                            // direct field write instead of vanilla OnMouseButtonUp: the
                            // vanilla path would flash tooltips/popups on the HOST's HUD
                            // for a button the host never touched
                            var priorOn = c.IsTurnedOn();
                            var priorCooldown = FiClCooldown?.GetValue(c) as bool? ?? false;
                            var priorTimer = FiClTimer?.GetValue(c) as float? ?? 0f;
                            try
                            {
                                FiClTurnedOn?.SetValue(c, on);
                                if (!on)
                                {
                                    FiClCooldown?.SetValue(c, true);
                                    FiClTimer?.SetValue(c, 0f);
                                }
                                HostChanged(KindCleanser, c);
                            }
                            catch
                            {
                                FiClTurnedOn?.SetValue(c, priorOn);
                                FiClCooldown?.SetValue(c, priorCooldown);
                                FiClTimer?.SetValue(c, priorTimer);
                                throw;
                            }
                            accepted = true;
                            break;
                        }
                    case OpCleanserRefill:
                        {
                            var fill = message.Fill;
                            var c = Get<InteractableAutoCleanser>(KindCleanser, idx);
                            if (!IsFinite(fill) || fill < 0f || fill > 1f)
                            {
                                reason = "invalid cleanser fill";
                                break;
                            }
                            if (c == null || !c.HasEnoughSlot())
                            {
                                reason = c == null ? "cleanser does not exist" : "cleanser has no slot";
                                break;
                            }

                            var priorStored = new List<Item>(c.GetStoredItemList());
                            var priorItemAmount = FiClItemAmount?.GetValue(c) as int?
                                ?? priorStored.Count;
                            var priorNeedRefill = c.IsNeedRefill();
                            Item item = null;
                            try
                            {
                                item = SpawnItem(EItemType.Deodorant, c.m_PosList[0], fill);

                                ApplyingRemote = true;
                                try
                                {
                                    c.AddItem(item, addToFront: true);
                                }
                                finally { ApplyingRemote = false; }
                                HostChanged(KindCleanser, c);
                                accepted = true;
                            }
                            catch
                            {
                                RestoreCleanser(c, priorStored, priorItemAmount,
                                    priorNeedRefill);
                                throw;
                            }
                            break;
                        }
                    case OpEmptyBoxTake:
                        {
                            var storage = Get<InteractableEmptyBoxStorage>(KindEmptyBoxStorage, idx);
                            if (storage == null)
                            {
                                reason = "empty-box storage does not exist";
                                break;
                            }

                            _boxes.HostPredictionId = _hostPredictionId;
                            try
                            {
                                accepted = HostTakeEmptyBox(storage, message.BoxNetworkId,
                                    out reason, out createdBox);
                            }
                            finally
                            {
                                _boxes.HostPredictionId = Guid.Empty;
                            }
                            if (accepted)
                            {
                                _hostDeltaBox = createdBox;
                                _hostDeltaTakeIntoHand = message.IsPlayer;
                                HostChanged(KindEmptyBoxStorage, storage);
                                if (message.IsPlayer && createdBox != null)
                                {
                                    // The taker's own pickup was a covered forward that raced
                                    // ahead of the box's creation; announce the grant so the host
                                    // knows the box is held and observers attach it to the avatar.
                                    _playerBox?.AnnounceGrantedHold(createdBox.BoxNetworkId, connId);
                                }
                            }
                            break;
                        }
                    case OpEmptyBoxStore:
                        {
                            var storage = Get<InteractableEmptyBoxStorage>(KindEmptyBoxStorage, idx);
                            if (storage == null)
                            {
                                reason = "empty-box storage does not exist";
                                break;
                            }

                            accepted = HostStoreEmptyBox(storage, message.BoxNetworkId, out reason);
                            if (accepted)
                            {
                                _hostDeltaReleaseHold = message.IsPlayer;
                                HostChanged(KindEmptyBoxStorage, storage);
                            }
                            break;
                        }
                    default:
                        reason = "unknown container operation";
                        break;
                }
            }
            catch (Exception e)
            {
                // Reliable-message handler boundary. An unexpected failure here propagated out of
                // the handler and disconnected the peer (spamming an empty-box take hit the
                // "created box has no authoritative descriptor" race). Report the op as rejected
                // and let the requesting client roll its prediction back instead.
                CoopPlugin.Log.LogError("WorldContainerInteraction op=" + op + " kind=" + kind
                    + " index=" + idx + " failed: " + e);
                reason = "container operation failed";
            }

            if (!accepted && reason != null)
            {
                CoopPlugin.Log.LogWarning("WorldContainerInteraction rejected op=" + op
                    + " kind=" + kind + " index=" + idx + ": " + reason);
            }
            _hostPredictionId = Guid.Empty;
            _hostActorConnectionId = 0;
            _hostDeltaBox = null;
            _hostDeltaTakeIntoHand = false;
            _hostDeltaReleaseHold = false;
            _hostCompletePackCollection = false;
            _hostPackIndex = 0;
            return accepted;
        }

        private bool HostTakeEmptyBox(InteractableEmptyBoxStorage storage, Guid creatorId,
            out string reason, out BoxNetworkState descriptor)
        {
            reason = null;
            descriptor = null;
            var count = storage.GetBoxStoredCount();
            if (count <= 0)
            {
                reason = "empty-box storage is empty";
                return false;
            }

            // The client is the creator and carried the box's stable id; bind the spawned box to
            // it. A non-fresh id means the client already bound a live box to it, so reject the
            // take rather than let the host mint a different id and desync that box.
            if (!_boxes.PushHostCreatedId(creatorId))
            {
                reason = "empty box creator id is not fresh";
                return false;
            }

            var box = RestockManager.SpawnPackageBoxItem(EItemType.None, 0, true);
            if (box == null)
            {
                // No box was created, so release the id that was parked for it; otherwise the next
                // unrelated host creation would bind to the client's id.
                _boxes.CancelHostCreatedId(creatorId);
                reason = "host could not create an empty box";
                return false;
            }

            if (storage.m_EmptyBoxSpawnLoc == null)
            {
                box.OnDestroyed();
                reason = "empty-box storage has no spawn location";
                return false;
            }

            box.transform.SetPositionAndRotation(storage.m_EmptyBoxSpawnLoc.position,
                storage.m_EmptyBoxSpawnLoc.rotation);
            if (!box.CanPickup())
            {
                // A box spawned this frame may still be mid-lerp. That is a cosmetic settle, not
                // a reason to refuse a valid take (which would roll the guest back).
                box.StopLerpToTransform();
            }

            box.ForceSetOpenCloseInstant(true);
            box.SetOpenCloseBox(false, false);
            if (!_boxes.TryGetId(box, out var id))
            {
                box.OnDestroyed();
                reason = "created empty box has no network identity";
                return false;
            }

            try
            {
                FiEmptyStoredCount.SetValue(storage, count - 1);
                MiEmptyEvaluateStack.Invoke(storage, null);

                // SpawnPackageBoxItem is announced by the host box patch before the storage has
                // moved it. Replace that descriptor with the authoritative spawn pose; the world
                // baseline deferral coalesces both announcements by box ID.
                _boxes.HostRefresh(id);
                descriptor = _boxes.DescribeAuthoritative(id);
                if (descriptor != null)
                {
                    return true;
                }

                // The box was torn down between its creation and this lookup (rapid spam). Undo the
                // count and reject; throwing here failed the reliable handler and disconnected the
                // guest.
                FiEmptyStoredCount.SetValue(storage, count);
                MiEmptyEvaluateStack.Invoke(storage, null);
                box.OnDestroyed();
                reason = "created empty box has no authoritative descriptor";
                return false;
            }
            catch (Exception e)
            {
                FiEmptyStoredCount.SetValue(storage, count);
                MiEmptyEvaluateStack.Invoke(storage, null);
                if (box != null)
                {
                    box.OnDestroyed();
                }

                // A throw before the box existed leaves the parked id unclaimed; release it so a
                // later, unrelated creation cannot bind to the client's id. No-op once consumed.
                _boxes.CancelHostCreatedId(creatorId);
                reason = "empty box take failed";
                CoopPlugin.Log.LogError("HostTakeEmptyBox failed: " + e);
                return false;
            }
        }

        private bool HostStoreEmptyBox(InteractableEmptyBoxStorage storage, Guid boxId,
            out string reason)
        {
            reason = null;
            if (boxId == Guid.Empty || !_boxes.TryGetBox(boxId, out var box)
                || box is not InteractablePackagingBox_Item item
                || item.m_ItemCompartment == null)
            {
                reason = "empty box identity is unknown";
                return false;
            }

            if (item.m_ItemCompartment.GetItemCount() != 0 || !item.m_IsBigBox)
            {
                reason = "only empty big boxes can be stored";
                return false;
            }

            if (!storage.HasStorageSpace())
            {
                reason = "empty-box storage is full";
                return false;
            }

            // StoreBox() silently refuses a box that is still playing its open/close
            // animation - the ~0.85s toggle a freshly-taken empty box starts (and the same
            // state a player-toggled box can be in). The host owns the box and has already
            // validated it, so settle the animation instantly and store it for real instead
            // of rejecting a valid store and rolling the client back.
            if (item.IsTogglingOpenClose())
            {
                item.ForceSetOpenCloseInstant(false);
            }

            var before = storage.GetBoxStoredCount();
            storage.StoreBox(item, false);
            if (storage.GetBoxStoredCount() != before + 1)
            {
                reason = "empty box could not be stored";
                return false;
            }

            return true;
        }

        private bool HostApplyPackClaim(int idx, int connId, out string reason,
            Action<INetMessage> response = null)
        {
            reason = null;
            var p = Get<InteractableAutoPackOpener>(KindPackOpener, idx);
            var output = p?.GetCompactCardDataAmountList();
            var key = (KindPackOpener << 8) | idx;
            if (p == null || !p.GetIsProcessing() || p.GetStoredItemList().Count > 0
                || output == null || output.Count == 0 || _packClaimOwner.ContainsKey(key))
            {
                reason = p == null ? "pack opener does not exist" : "pack opener has no claimable output";
                return false;
            }
            var token = _nextPackClaimToken++;
            if (token == 0)
            {
                token = _nextPackClaimToken++;
            }

            var claim = new ContainerPackClaimMessage
            {
                PredictionId = _hostPredictionId,
                Index = (byte)idx,
                ClaimToken = token,
                Cards = new List<CompactCardDataAmount>(output),
            };
            var claimCards = 0;
            for (var i = 0; i < output.Count; i++)
            {
                if (output[i] != null)
                {
                    claimCards += output[i].amount;
                }
            }

            CoopPlugin.Log.LogInfo("[pack] claim opener=" + idx + ": " + claimCards + " card(s) in "
                + output.Count + " entr(ies).");
            if (response != null)
                response(claim);
            else
                SendToClient?.Invoke(connId, claim);
            // Register the claim only once the reply was handed to the transport. A throwing
            // send must not leave the opener locked behind a claim the client never received
            // (HostApplyPackClaim refuses any further claim while the key is owned).
            _packClaimOwner[key] = connId;
            _packClaimToken[key] = token;
            return true;
        }

        private bool HostApplyPackCollect(int idx, int token,
            int connId, out string reason)
        {
            reason = null;
            var p = Get<InteractableAutoPackOpener>(KindPackOpener, idx);
            if (p == null)
            {
                reason = "pack opener does not exist";
                return false;
            }

            var output = p.GetCompactCardDataAmountList();
            var key = (KindPackOpener << 8) | idx;
            // The claim's identity is the host-issued ClaimToken, bound to this opener and owner
            // when the host minted the claim. The revealed-card list the client echoes back is NOT
            // part of that identity: the host banks its own output and opened count, so there is
            // nothing content-based to match. Matching by content multiset here was a fallback for
            // the missing identity and is exactly what the token replaces.
            if (!_packClaimOwner.TryGetValue(key, out var owner) || owner != connId
                || !_packClaimToken.TryGetValue(key, out var expected) || expected != token)
            {
                CoopPlugin.Log.LogWarning($"WorldContainerInteraction: rejected pack collect opener={idx} client={connId}");
                reason = "pack claim is stale or does not match host output";
                return false;
            }
            var priorOutput = new List<CompactCardDataAmount>(output);
            var priorOpenedCount = p.GetPackOpenedCount();
            var priorProcessing = p.GetIsProcessing();
            var priorState = p.m_CurrentState;
            var priorOwner = owner;
            var priorToken = expected;
            var priorReport = CPlayerData.m_GameReportDataCollect.cardPackOpened;
            var priorPermanentReport = CPlayerData.m_GameReportDataCollectPermanent.cardPackOpened;
            CoopPlugin.Log.LogInfo("[pack] collect opener=" + idx + ": " + priorOpenedCount
                + " pack(s) opened.");
            // Even if there's nothing left to bank (a stale/duplicate collect, or the host
            // already collected), still force the machine idle below. The old early-return
            // left m_IsProcessing pinned TRUE, and the heal then rebroadcast processing=true
            // to the guest forever - so the guest could never add packs again ("no empty
            // slot") after the first collect, while the host was unaffected.
            try
            {
                if (output != null && output.Count > 0)
                {
                    var opened = p.GetPackOpenedCount();
                    CPlayerData.m_GameReportDataCollect.cardPackOpened += opened;
                    CPlayerData.m_GameReportDataCollectPermanent.cardPackOpened += opened;
                    AchievementManager.OnCardPackOpened(
                        CPlayerData.m_GameReportDataCollectPermanent.cardPackOpened);
                    output.Clear();
                }
                FiPoOpenedCount?.SetValue(p, 0);
                FiPoIsProcessing?.SetValue(p, false);
                p.m_CurrentState = 0;
                if (FiPoUI?.GetValue(p) is AutoCardOpenerUI ui)
                {
                    ui.SetUIState(0);
                    ui.UpdatePackCountText(0, p.m_MaxPackCount);
                }
                _packClaimOwner.Remove(key);
                _packClaimToken.Remove(key);
                HostChanged(KindPackOpener, p);
            }
            catch
            {
                output.Clear();
                output.AddRange(priorOutput);
                FiPoOpenedCount?.SetValue(p, priorOpenedCount);
                FiPoIsProcessing?.SetValue(p, priorProcessing);
                p.m_CurrentState = priorState;
                _packClaimOwner[key] = priorOwner;
                _packClaimToken[key] = priorToken;
                CPlayerData.m_GameReportDataCollect.cardPackOpened = priorReport;
                CPlayerData.m_GameReportDataCollectPermanent.cardPackOpened = priorPermanentReport;
                throw;
            }
            return true;
        }

        // ---------------- client: apply authoritative state ----------------

        public void ClientApplyState(ContainerStateMessage message)
        {
            if (message == null)
                throw new InvalidOperationException("Authoritative container state is missing.");

            if (!InGame)
            {
                _pendingClientState = message;
                return;
            }

            CacheContainerScreens();
            var records = message.Records;
            for (var r = 0; r < records.Count; r++)
            {
                var rec = records[r];
                var kind = (int)rec.Kind;
                var idx = (int)rec.Index;
                switch (kind)
                {
                    case KindCardStorage:
                        ApplyContentInPlace(Get<InteractableCardStorageShelf>(kind, idx),
                            rec.Cards, rec.CanWorkerTake);
                        break;
                    case KindDonation:
                        ApplyContentInPlace(Get<InteractableBulkDonationBox>(kind, idx), rec.Cards);
                        break;
                    case KindPackOpener:
                        ApplyPackState(Get<InteractableAutoPackOpener>(kind, idx), rec);
                        if (rec.CollectClaimed)
                            _claimedPackIndices.Add(idx);
                        else
                            _claimedPackIndices.Remove(idx);
                        break;
                    case KindCleanser:
                        var cleanser = Get<InteractableAutoCleanser>(kind, idx);
                        ApplyCleanserState(idx, cleanser, (rec.Flags & 1) != 0,
                            (rec.Flags & 2) != 0, rec.Fills);
                        break;
                    case KindEmptyBoxStorage:
                        var storage = Get<InteractableEmptyBoxStorage>(kind, idx);
                        FiEmptyStoredCount.SetValue(storage, rec.Count);
                        MiEmptyEvaluateStack.Invoke(storage, null);
                        break;
                    default:
                        throw new InvalidOperationException("Unknown authoritative container kind "
                            + kind + ".");
                }
            }
        }

        public void ClientApplyDelta(ContainerDeltaMessage message)
        {
            if (message == null)
                throw new InvalidOperationException("Authoritative container delta is missing.");

            // An insert echo reconciles in layers: the record was built before any newer local
            // inserts still in flight, and applying it raw trimmed those newer optimistic entries
            // away - the pack count that bounced down and back up while a guest fed a machine.
            if (_pendingPackInserts.Remove(message.PredictionId))
            {
                WorldPrediction.ApplyAuthoritative(message, () => ApplyDeltaRecord(message));
            }
            else
            {
                // Every other record is an absolute state-set: retire this peer's own prediction
                // (the game already applied it) or apply another peer's change directly.
                WorldPrediction.Confirm(message, () => ApplyDeltaRecord(message));
            }

            if (message.HasBox)
            {
                _boxes.ClientApplyCreated(new BoxCreatedMessage { Box = message.Box });
                if (!_boxes.TryGetBox(message.Box.BoxNetworkId, out var box))
                    throw new InvalidOperationException("Container delta box was not materialized.");

                if (message.TakeIntoHand && box is InteractablePackagingBox_Item item)
                {
                    TakeIntoLocalHand(item);
                }
            }

            if (message.ReleaseHold)
            {
                // Release through the game's own path so the held box's flag and the controller's
                // current box clear together.
                _playerBox?.ReleaseLocalHold();
            }

            if (message.CompletePackCollection)
            {
                // The delta is broadcast to every peer, but only the claimant ever created a
                // pending collection (the host sends ContainerPackClaimMessage to the claimant
                // alone). Observers therefore have nothing to apply here and must not reveal or
                // bank on the claimant's behalf.
                if (_pendingPackCollections.ContainsKey(message.PackIndex))
                    CompletePackCollection(message.PackIndex);
            }
        }

        /// <summary>Applies the single record a delta carries.</summary>
        private void ApplyDeltaRecord(ContainerDeltaMessage message)
        {
            ClientApplyState(new ContainerStateMessage
            {
                Records = new List<ContainerRecord> { message.Record },
            });
        }

        /// <summary>Retires the local claim bookkeeping once the host's collect delta arrives.
        /// The collect itself has already run: the claimant plays vanilla
        /// <c>InteractableAutoPackOpener.OnMouseButtonUp</c> unsuppressed, and its collect branch
        /// banks the report counters and shows the obtained page on both supported builds (see
        /// the baseline's <c>cardPackOpened += m_PackOpenedCount</c> followed by
        /// <c>ShowCardObtained</c>). Banking or revealing again here would double both, so
        /// vanilla is the single writer and this only clears the claim.</summary>
        private void CompletePackCollection(int idx)
        {
            _pendingPackCollections.Remove(idx);
            _claimedPackIndices.Remove(idx);
        }

        private void ApplyContentInPlace(InteractableCardStorageShelf shelf,
            List<CompactCardDataAmount> cards, bool canWorkerTake)
        {
            ApplyingRemote = true;
            try
            {
                var target = shelf.GetCompactCardDataAmountList();
                target.Clear();
                target.AddRange(cards);
                shelf.SetCanWorkerTake(canWorkerTake);
                shelf.OnCardStorageShelfSettingDone();
                RefreshOpenContainerUI(shelf, null);
            }
            finally { ApplyingRemote = false; }
        }

        private void ApplyContentInPlace(InteractableBulkDonationBox box,
            List<CompactCardDataAmount> cards)
        {
            ApplyingRemote = true;
            try
            {
                var target = box.GetCompactCardDataAmountList();
                target.Clear();
                target.AddRange(cards);
                box.UpdateFillPercent(Mathf.Clamp01(
                    (float)box.GetTotalCardAmount() / box.GetBoxTotalCardCountMax()));
                RefreshOpenContainerUI(null, box);
            }
            finally { ApplyingRemote = false; }
        }

        private void CacheContainerScreens()
        {
            if (_cachedContainerScreen == null)
            {
                _cachedContainerScreen = UnityEngine.Object.FindObjectOfType<BulkDonationBoxUIScreen>();
            }
            if (_cachedAmountModal == null)
            {
                _cachedAmountModal = UnityEngine.Object.FindObjectOfType<BulkDonationBoxPlusMinusScreen>();
            }
        }

        private void RefreshOpenContainerUI(InteractableCardStorageShelf shelf,
            InteractableBulkDonationBox donation)
        {
            CacheContainerScreens();
            var screen = _cachedContainerScreen;
            if (screen == null || MiEvaluateCardPanelUI == null)
            {
                return;
            }
            var screenShelf = FiScreenShelf?.GetValue(screen);
            var screenDonation = FiScreenDonation?.GetValue(screen);
            if (!ReferenceEquals(screenShelf, shelf) || !ReferenceEquals(screenDonation, donation))
            {
                return;
            }
            var list = donation != null
                ? donation.GetCompactCardDataAmountList()
                : shelf.GetCompactCardDataAmountList();
            var panelCount = (FiScreenPanels?.GetValue(screen) as IList)?.Count ?? 0;
            if (panelCount <= 0)
            {
                return;
            }
            var pageLimit = FiScreenPageLimit?.GetValue(screen) as int? ?? 1;
            var page = FiScreenPage?.GetValue(screen) as int? ?? 0;
            var maxPage = Mathf.Max(0, list.Count / panelCount);
            maxPage = Mathf.Min(maxPage, Mathf.Max(0, pageLimit - 1));
            page = Mathf.Clamp(page, 0, maxPage);
            FiScreenPage?.SetValue(screen, page);
            var selected = FiScreenCurrentSlot?.GetValue(screen) as int? ?? 0;
            var modal = _cachedAmountModal;
            var modalOpen = modal != null && ReferenceEquals(FiModalParent?.GetValue(modal), screen)
                && MiModalIsOpened != null && (bool)MiModalIsOpened.Invoke(modal, null);
            if (modalOpen)
            {
                var modalCard = FiModalCardData?.GetValue(modal) as CardData;
                var modalIndex = FindCardIndex(list, modalCard);
                if (modalIndex < 0)
                {
                    if (MiModalClose == null || MiModalIsOpened == null
                        || FiModalInput == null || FiModalStackCount == null
                        || FiScreenCurrentSlot == null)
                    {
                        CoopPlugin.Log.LogWarning(
                            "WorldContainerInteraction: could not safely close stale card amount modal; leaving its slot valid");
                        return;
                    }
                    var amountInput = FiModalInput.GetValue(modal) as TMP_InputField;
                    if (amountInput == null)
                    {
                        CoopPlugin.Log.LogWarning(
                            "WorldContainerInteraction: card amount modal input was unavailable; leaving its slot valid");
                        return;
                    }
                    // TMP sends onEndEdit synchronously while the modal closes. Neutralize that
                    // callback before invalidating the parent index: OnInputTextUpdated("0")
                    // reaches OnPressRemoveAllBtn, whose zero stack count takes no list path.
                    var previousRemoteCards = CardShopCoop.Modules.World.WorldCardInteraction.ApplyingRemoteCards;
                    try
                    {
                        CardShopCoop.Modules.World.WorldCardInteraction.ApplyingRemoteCards = true;
                        FiModalStackCount.SetValue(modal, 0);
                        amountInput.text = "0";
                        if (amountInput.isFocused)
                        {
                            amountInput.DeactivateInputField();
                        }
                        var oldSelected = selected;
                        FiScreenCurrentSlot.SetValue(screen, -1);
                        try
                        {
                            MiModalClose.Invoke(modal, null);
                            var stillOpen = (bool)MiModalIsOpened.Invoke(modal, null);
                            if (stillOpen)
                            {
                                FiScreenCurrentSlot.SetValue(screen, list.Count == 0
                                    ? -1
                                    : Mathf.Clamp(oldSelected, 0, list.Count - 1));
                                CoopPlugin.Log.LogWarning(
                                    "WorldContainerInteraction: card amount modal did not close; restored its slot");
                            }
                        }
                        catch (Exception e)
                        {
                            FiScreenCurrentSlot.SetValue(screen, list.Count == 0
                                ? -1
                                : Mathf.Clamp(oldSelected, 0, list.Count - 1));
                            CoopPlugin.Log.LogWarning(
                                "WorldContainerInteraction: card amount modal close failed: " + e.Message);
                        }
                    }
                    finally
                    {
                        CardShopCoop.Modules.World.WorldCardInteraction.ApplyingRemoteCards = previousRemoteCards;
                    }
                }
                else
                {
                    FiScreenCurrentSlot?.SetValue(screen, modalIndex);
                    var modalAmount = list[modalIndex].gradedCardIndex > 0
                        ? 1
                        : list[modalIndex].amount;
                    FiModalStackCount?.SetValue(modal, modalAmount);
                    FiModalBoxTotal?.SetValue(modal, GetTotalCardAmount(list));
                }
            }
            else
            {
                FiScreenCurrentSlot?.SetValue(screen, list.Count == 0
                    ? -1
                    : Mathf.Clamp(selected, 0, list.Count - 1));
            }
            MiEvaluateCardPanelUI.Invoke(screen, new object[] { page });
        }

        /// <summary>Re-locates the row the open amount modal refers to after an authoritative
        /// container record replaced the local list. A graded row is identified by the game's own
        /// stable per-copy <c>gradedCardIndex</c> (the same id RemoveGradedCard uses); an ungraded
        /// row is a fungible stack and is identified by its content/save-index key. There is no
        /// content fallback for a graded card: an unmatched graded row returns -1, it never
        /// degrades to a content match.</summary>
        private static int FindCardIndex(List<CompactCardDataAmount> list, CardData card)
        {
            if (card == null || list == null)
            {
                return -1;
            }
            for (var i = 0; i < list.Count; i++)
            {
                var entry = list[i];
                if (entry == null)
                {
                    continue;
                }
                if (entry.gradedCardIndex > 0)
                {
                    if (entry.gradedCardIndex == card.gradedCardIndex)
                    {
                        return i;
                    }
                    continue;
                }
                var entryCard = CPlayerData.GetCardData(
                    entry.cardSaveIndex, entry.expansionType, entry.isDestiny);
                if (entryCard != null && entryCard.IsSameCardDataType(card)
                    && entryCard.expansionType == card.expansionType
                    && entryCard.isDestiny == card.isDestiny)
                {
                    return i;
                }
            }
            return -1;
        }

        private static int GetTotalCardAmount(List<CompactCardDataAmount> list)
        {
            var total = 0;
            if (list == null)
            {
                return total;
            }
            for (var i = 0; i < list.Count; i++)
            {
                var entry = list[i];
                total += entry == null || entry.gradedCardIndex > 0 ? 100 : entry.amount;
            }
            return total;
        }

        private void ApplyContent(int kind, int idx, List<CompactCardDataAmount> cards,
            bool hasFlag, bool canWorkerTake)
        {
            ApplyingRemote = true;
            try
            {
                if (kind == KindCardStorage)
                {
                    var s = Get<InteractableCardStorageShelf>(kind, idx);
                    if (s == null)
                    {
                        return;
                    }

                    if (hasFlag)
                    {
                        ApplyContentInPlace(s, cards, canWorkerTake);
                    }
                    else
                    {
                        ApplyContentInPlace(s, cards, s.CanWorkerTake());
                    }
                }
                else if (kind == KindDonation)
                {
                    var b = Get<InteractableBulkDonationBox>(kind, idx);
                    if (b == null)
                    {
                        return;
                    }

                    ApplyContentInPlace(b, cards);
                }
            }
            finally { ApplyingRemote = false; }
        }

        /// <summary>Client: reconcile the opener machine to the host's authoritative record. The
        /// client runs the opener vanilla (its Update advances the timer and rolls packs), so this
        /// writes the host's queue and processing fields straight onto the machine - the machine
        /// itself is the only pack state this peer keeps.</summary>
        private void ApplyPackState(InteractableAutoPackOpener p, ContainerRecord rec)
        {
            if (p == null)
            {
                return;
            }

            var types = new List<int>(rec.StoredTypes.Count);
            for (var i = 0; i < rec.StoredTypes.Count; i++)
            {
                types.Add((int)rec.StoredTypes[i]);
            }

            ApplyingRemote = true;
            try
            {
                SyncLocalPackItems(p, types.Count, types);
                FiPoIsProcessing?.SetValue(p, rec.Processing); // drives the Collect tooltip
                FiPoOpenedCount?.SetValue(p, rec.OpenedCount);
                // Keep the client object coherent with the state that vanilla's UI
                // actually represents. In particular, AddItem auto-starts a full
                // machine without setting m_CurrentState to 1.
                p.m_CurrentState = rec.CurrentState;
                // Never snap the local processing clock backwards. The host's sample is already
                // stale by the round trip, and overwriting the locally ticking timer with it made
                // the fill bar jump backwards on every delta. Seed it only when this machine is
                // not already running the cycle (a worker/host started the machine), and clear it
                // when the host says the cycle ended.
                if (rec.CurrentState != 1)
                {
                    FiPoOpenTimer?.SetValue(p, 0f);
                }
                else if (!p.GetIsProcessing())
                {
                    FiPoOpenTimer?.SetValue(p, rec.Timer);
                }

                PaintPackUI(p);
            }
            finally { ApplyingRemote = false; }
        }

        private static void SyncLocalPackItems(InteractableAutoPackOpener opener, int targetCount,
            List<int> storedTypes)
        {
            var stored = opener.GetStoredItemList();
            if (stored == null)
                throw new InvalidOperationException("Pack opener has no local storage list.");

            while (stored.Count > targetCount)
            {
                var last = stored[stored.Count - 1];
                stored.RemoveAt(stored.Count - 1);
                if (last != null)
                    last.DisableItem();
            }

            while (stored.Count < targetCount)
            {
                var itemType = stored.Count < storedTypes.Count
                    ? (EItemType)storedTypes[stored.Count]
                    : EItemType.None;
                var item = SpawnItem(itemType, opener.m_PosInside);
                stored.Add(item);
            }
        }

        /// <summary>Paint the opener UI from the machine's own queue, state and timer. Vanilla's
        /// Update repaints while it advances, but an authoritative idle/processing transition
        /// applied outside vanilla's own turn-on/collect paths still has to reach the visible
        /// deck.</summary>
        private static void PaintPackUI(InteractableAutoPackOpener p)
        {
            if (p == null || FiPoUI?.GetValue(p) is not AutoCardOpenerUI ui)
            {
                return;
            }

            var stored = p.GetStoredItemList()?.Count ?? 0;
            switch (p.m_CurrentState)
            {
                case 1:
                    {
                        var cycle = Mathf.Max(0.001f, p.m_PackOpenTime);
                        var timer = FiPoOpenTimer?.GetValue(p) as float? ?? 0f;
                        ui.SetUIState(1);
                        ui.UpdateProcessingFillBar(Mathf.Clamp01(
                            1f - (float)stored / Mathf.Max(1, p.m_MaxPackCount)));
                        ui.UpdateProcessingTimeLeftText(Mathf.Max(0f, cycle * stored - timer));
                        break;
                    }
                case 2:
                    ui.SetUIState(2);
                    break;
                default:
                    ui.SetUIState(0);
                    ui.UpdatePackCountText(stored, p.m_MaxPackCount);
                    break;
            }
        }

        private void ApplyCleanserState(int idx, InteractableAutoCleanser c, bool on, bool needRefill,
            List<float> fills)
        {
            ApplyingRemote = true;
            try
            {
                // Reconcile the visible spray cans through the vanilla add/remove so the
                // slot layout stays coherent - but drive the loops off the REAL stored list,
                // never off GetItemCount()/GetLastItem(). Vanilla keeps m_ItemAmount as a
                // second, independent count and indexes the list with it
                // (GetLastItem = m_StoredItemList[m_ItemAmount - 1], decompiled :357, behind
                // a guard that only tests the list's length), so any drift between the two
                // throws IndexOutOfRange straight out of this handler. Reading the list is
                // bounds-safe by construction, and the forced write at the bottom converges
                // the counter instead of merely dodging it.
                var stored = c.GetStoredItemList();
                while ((stored?.Count ?? 0) > fills.Count)
                {
                    // same element GetLastItem WOULD return once the two counts agree
                    var last = stored[stored.Count - 1];
                    if (last == null)
                    {
                        stored.RemoveAt(stored.Count - 1);
                        continue;
                    }
                    c.RemoveItem(last);
                    last.DisableItem();
                }
                // HasEnoughSlot is the game's own m_PosList bound for AddItem.
                while (stored.Count < fills.Count)
                {
                    var item = SpawnItem(EItemType.Deodorant, c.m_PosList[0], 1f);
                    c.AddItem(item, addToFront: true);
                }
                if (stored != null)
                {
                    for (var i = 0; i < stored.Count && i < fills.Count; i++)
                    {
                        if (stored[i] != null)
                        {
                            stored[i].SetContentFill(fills[i]);
                        }
                    }
                }
                // Force the counter into agreement with what the machine physically holds.
                // This is what makes the reconcile idempotent, and it is the only thing that
                // repairs a guest ALREADY diverged mid-session (LoadData never re-runs, so
                // the join-time fix in CleanserAddItemPostfix only helps from the next join).
                FiClItemAmount?.SetValue(c, stored?.Count ?? 0);
                // flags last: AddItem/RemoveItem flip m_IsNeedRefill on their own
                FiClTurnedOn?.SetValue(c, on);
                FiClNeedRefill?.SetValue(c, needRefill);
            }
            finally { ApplyingRemote = false; }
        }

        // ---------------- client: forwarded actions (called from patches) ----------------

        private void ClientPackOpenerClick(InteractableAutoPackOpener p, PackOpenerObserve observe)
        {
            if (p == null || observe == null)
            {
                return;
            }

            var idx = observe.Index;
            // Vanilla InteractableAutoPackOpener.OnMouseButtonUp already plays SFX_ButtonLightTap
            // (and the collect jingles) unconditionally before this postfix observes the click, so
            // the mod must not play a second tap.
            if (!observe.PriorProcessing)
            {
                if (observe.PriorStored > 0)
                {
                    var command = new ContainerOpMessage
                    {
                        Op = OpPackTurnOn,
                        Kind = (byte)KindPackOpener,
                        Index = (byte)idx
                    };
                    // Vanilla already turned the machine on; the prediction only records how to
                    // replay/undo the turn-on if a later rejection unwinds it.
                    WorldPrediction.Predict(WorldPrediction.ContainersScope, command,
                        () => ApplyPackRunning(p),
                        () => ApplyPackSnapshot(p, observe.PriorProcessing, observe.PriorState,
                            observe.PriorOpened, observe.PriorTimer));
                }
                else
                {
                    NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.NoCardPackInMachine);
                }
            }
            else if (observe.PriorOutput > 0 && observe.PriorStored <= 0)
            {
                if (_claimedPackIndices.Contains(idx))
                {
                    if (_pendingPackCollections.TryGetValue(idx, out var pending))
                    {
                        TrySubmitPendingPackCollection(idx, pending);
                    }
                    NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.WaitAllCardPacksToBeProcessed);
                    return;
                }
                // Vanilla already ran the collect branch; the claim only asks the host to mint the
                // authoritative output.
                _claimedPackIndices.Add(idx);
                var claim = new ContainerOpMessage
                {
                    Op = OpPackClaim,
                    Kind = (byte)KindPackOpener,
                    Index = (byte)idx,
                };
                WorldPrediction.Predict(WorldPrediction.ContainersScope, claim,
                    () => _claimedPackIndices.Add(idx),
                    () => _claimedPackIndices.Remove(idx));
            }
            else
            {
                NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.WaitAllCardPacksToBeProcessed);
            }
        }

        /// <summary>Replays the authoritative turn-on through the machine's own fields (a
        /// prediction follower after a later rollback).</summary>
        private static void ApplyPackRunning(InteractableAutoPackOpener p)
        {
            ApplyingRemote = true;
            try
            {
                FiPoIsProcessing?.SetValue(p, true);
                p.m_CurrentState = 1;
                PaintPackUI(p);
            }
            finally { ApplyingRemote = false; }
        }

        /// <summary>Restores the machine's pre-click pack fields for a rolled-back turn-on.</summary>
        private static void ApplyPackSnapshot(InteractableAutoPackOpener p, bool processing,
            int state, int opened, float timer)
        {
            ApplyingRemote = true;
            try
            {
                FiPoIsProcessing?.SetValue(p, processing);
                FiPoOpenedCount?.SetValue(p, opened);
                p.m_CurrentState = state;
                FiPoOpenTimer?.SetValue(p, timer);
                PaintPackUI(p);
            }
            finally { ApplyingRemote = false; }
        }

        private void ClientEmptyBoxTake(InteractableEmptyBoxStorage storage)
        {
            var idx = IndexOf(KindEmptyBoxStorage, storage);
            if (idx >= 0)
            {
                var command = new ContainerOpMessage
                {
                    Op = OpEmptyBoxTake,
                    Kind = (byte)KindEmptyBoxStorage,
                    Index = (byte)idx,
                    // The client is the creator: the box vanilla just spawned and held already has
                    // its stable id, carried so the host binds its counterpart to the same id.
                    BoxNetworkId = _playerBox != null && _playerBox.LocalHeldBox != null
                        && _boxes.TryGetId(_playerBox.LocalHeldBox, out var takenId)
                        ? takenId
                        : Guid.Empty,
                    // Only the host creates the authoritative physical box. Vanilla already spawned
                    // and held a local box; flagging the take as a player action makes the host's
                    // accepted delta arrive as TakeIntoHand, and the host box reconciles the local
                    // copy.
                    IsPlayer = true,
                };
                // The observation records the storage count so a rejection can restore it; vanilla
                // already decremented it.
                WorldPrediction.Predict(WorldPrediction.ContainersScope, command,
                    () =>
                    {
                        var count = storage.GetBoxStoredCount();
                        FiEmptyStoredCount.SetValue(storage, count - 1);
                        MiEmptyEvaluateStack.Invoke(storage, null);
                    },
                    () =>
                    {
                        var count = storage.GetBoxStoredCount();
                        FiEmptyStoredCount.SetValue(storage, count + 1);
                        MiEmptyEvaluateStack.Invoke(storage, null);
                    });
            }
        }

        /// <summary>Replays an observed empty-box store through the game's own store path. The
        /// stored box is a host-owned identity, so if a replay outlived the object (vanilla
        /// destroyed it), the id is rematerialized before the store runs.</summary>
        private void ApplyEmptyBoxStored(InteractableEmptyBoxStorage storage, Guid boxId)
        {
            if (storage == null)
            {
                return;
            }

            if (!_boxes.TryGetBox(boxId, out var box)
                || box is not InteractablePackagingBox_Item item)
            {
                if (!_boxes.ClientEnsureWarehouseTake(boxId, EItemType.None, 0, true, out item))
                {
                    CoopPlugin.Log.LogWarning("Empty-box store replay could not restore box "
                        + boxId + "; skipping.");
                    return;
                }
            }

            ApplyingRemote = true;
            _boxes.BeginContainerConsume();
            try
            {
                storage.StoreBox(item, false);
            }
            finally
            {
                _boxes.EndContainerConsume();
                ApplyingRemote = false;
            }
        }

        /// <summary>Reverses an observed empty-box store: the storage count comes back down through
        /// the game's own stack field and the host-owned box is rematerialized into the player's
        /// hand so a rejected store does not eat it.</summary>
        private void ApplyEmptyBoxTaken(InteractableEmptyBoxStorage storage, Guid boxId)
        {
            if (storage == null)
            {
                return;
            }

            var count = storage.GetBoxStoredCount();
            if (count > 0)
            {
                FiEmptyStoredCount.SetValue(storage, count - 1);
                MiEmptyEvaluateStack.Invoke(storage, null);
            }

            if (!_boxes.ClientEnsureWarehouseTake(boxId, EItemType.None, 0, true, out var live))
            {
                CoopPlugin.Log.LogWarning("Empty-box store rollback could not restore box "
                    + boxId + "; skipping.");
                return;
            }

            TakeIntoLocalHand(live);
        }

        public void ClientApplyPackClaim(ContainerPackClaimMessage message)
        {
            var idx = (int)message.Index;
            _claimedPackIndices.Add(idx);
            _pendingPackCollections[idx] = new PendingPackCollection
            {
                ClaimToken = message.ClaimToken,
                Cards = new List<CompactCardDataAmount>(message.Cards),
            };
            // Sending the command is the first side effect. Until it is sent, leave the claim,
            // output, report counters, and local machine untouched.
            TrySubmitPendingPackCollection(idx, _pendingPackCollections[idx]);
        }

        private bool TrySubmitPendingPackCollection(int idx, PendingPackCollection pending)
        {
            if (pending == null)
            {
                return false;
            }

            var command = new ContainerOpMessage
            {
                Op = OpPackCollect,
                Kind = (byte)KindPackOpener,
                Index = (byte)idx,
                ClaimToken = pending.ClaimToken,
                Cards = new List<CompactCardDataAmount>(pending.Cards),
            };
            // The host's claim already handed us the cards; the collect forwards that exact claim.
            // The collect itself is a no-op prediction: the claimant's vanilla collect already ran
            // (and already banked and revealed), so there is nothing to apply or undo in the game.
            // A rejection only clears the claim bookkeeping that otherwise keeps a stale token
            // alive and resubmitting forever.
            WorldPrediction.Predict(WorldPrediction.ContainersScope, command,
                () => { }, () => { }, () => AbandonPackCollection(idx));
            return true;
        }

        /// <summary>Clears the local claim bookkeeping for an opener whose collect the host
        /// refused. Without this the opener stays in <see cref="_claimedPackIndices"/> with a
        /// stale token, and <see cref="ClientPackOpenerClick"/> resubmits the doomed collect on
        /// every later click.</summary>
        private void AbandonPackCollection(int idx)
        {
            CoopPlugin.Log.LogWarning("WorldContainerInteraction: pack collect rejected opener="
                + idx + "; clearing local claim.");
            _pendingPackCollections.Remove(idx);
            _claimedPackIndices.Remove(idx);
        }

        // ---------------- patches ----------------

        public static void ApplyHostPatches(Harmony h)
        {
            // The host owns the authoritative change hooks. Client-only forwarding hooks are
            // installed by WorldClientBehaviour instead of being selected at runtime.
            Try(h, typeof(InteractableCardStorageShelf), "SetCompactCardDataAmountList",
                postfix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(HostStorageContentPostfix)));
            Try(h, typeof(InteractableBulkDonationBox), "SetCompactCardDataAmountList",
                postfix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(HostDonationContentPostfix)));
            Try(h, typeof(InteractableCardStorageShelf), "SetCanWorkerTake",
                postfix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(HostWorkerTakePostfix)));
            Try(h, typeof(InteractableCardStorageShelf), "GetRandomCard",
                postfix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(CardStorageRandomCardPostfix)));
            Try(h, typeof(InteractableCardStorageShelf), "RemoveRandomCardFromShelf",
                postfix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(CardStorageRandomCardPostfix)));
            Try(h, typeof(InteractableBulkDonationBox), "RemoveRandomCardFromShelf",
                postfix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(DonationRandomCardPostfix)));

            Try(h, typeof(InteractableAutoPackOpener), "OnMouseButtonUp",
                postfix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(PackOpenerChangedPostfix)));
            Try(h, typeof(InteractableAutoPackOpener), "AddItem",
                postfix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(PackOpenerChangedPostfix)));
            Try(h, typeof(InteractableAutoPackOpener), "RemoveItem",
                postfix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(PackOpenerChangedPostfix)));
            Try(h, typeof(InteractableAutoPackOpener), "OpenPack",
                postfix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(PackOpenerChangedPostfix)));
            Try(h, typeof(InteractableAutoPackOpener), "TakeItemToHand",
                postfix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(PackOpenerChangedPostfix)));

            Try(h, typeof(InteractableAutoCleanser), "OnMouseButtonUp",
                postfix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(CleanserChangedPostfix)));
            Try(h, typeof(InteractableAutoCleanser), "AddItem",
                postfix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(CleanserChangedPostfix)));
            Try(h, typeof(InteractableAutoCleanser), "RemoveItem",
                postfix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(CleanserChangedPostfix)));
            Try(h, typeof(InteractableAutoCleanser), "Spray",
                postfix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(CleanserChangedPostfix)));
            Try(h, typeof(InteractableAutoCleanser), "TakeItemToHand",
                postfix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(CleanserChangedPostfix)));

            Try(h, typeof(InteractableEmptyBoxStorage), "TakeBox",
                postfix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(EmptyBoxTakeChangedPostfix)));
            Try(h, typeof(InteractableEmptyBoxStorage), "StoreBox",
                postfix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(EmptyBoxStoreChangedPostfix)));
        }

        public static void ApplyClientPatches(Harmony h)
        {
            // The client owns intent forwarding and local mutation guards. There is no shared
            // hook that checks the runtime role to decide which side should run.
            Try(h, typeof(InteractableCardStorageShelf), "SetCompactCardDataAmountList",
                postfix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(StorageContentPostfix)));
            Try(h, typeof(InteractableBulkDonationBox), "SetCompactCardDataAmountList",
                postfix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(DonationContentPostfix)));
            Try(h, typeof(InteractableCardStorageShelf), "SetCanWorkerTake",
                prefix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(WorkerTakePrefix)),
                postfix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(WorkerTakePostfix)));

            // Pack opener actions run vanilla on the client; each hook observes the result and
            // forwards exactly one intent. The host's authoritative container state reconciles
            // the local roll. The click prefix snapshots what vanilla is about to consume, since
            // the post-vanilla machine can no longer say whether this click turned it on or
            // collected it.
            Try(h, typeof(InteractableAutoPackOpener), "OnMouseButtonUp",
                prefix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(PackOpenerClickPrefix)),
                postfix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(PackOpenerClickPostfix)));
            Try(h, typeof(InteractableAutoPackOpener), "AddItem",
                postfix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(PackOpenerAddItemPostfix)));

            Try(h, typeof(InteractableAutoCleanser), "OnMouseButtonUp",
                prefix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(CleanserTogglePrefix)),
                postfix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(CleanserTogglePostfix)));
            Try(h, typeof(InteractableAutoCleanser), "AddItem",
                postfix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(CleanserAddItemPostfix)));

            Try(h, typeof(InteractableEmptyBoxStorage), "OnMouseButtonUp",
                prefix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(EmptyBoxTakePrefix)),
                postfix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(EmptyBoxTakePostfix)),
                finalizer: new HarmonyMethod(typeof(WorldContainerInteraction),
                    nameof(EmptyBoxTakeFinalizer)));
            Try(h, typeof(InteractableEmptyBoxStorage), "StoreBox",
                prefix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(EmptyBoxStorePrefix)),
                postfix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(EmptyBoxStorePostfix)),
                finalizer: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(EmptyBoxStoreFinalizer)));

            // say so loudly rather than silently no-op the null-conditional: without this
            // field ApplyCleanserState can no longer force the counter back onto the list,
            // and the join-time drift would return unnoticed on a renamed game build
            if (FiClItemAmount == null)
            {
                CoopPlugin.Log.LogWarning(
                    "Field missing: InteractableAutoCleanser.m_ItemAmount - cleanser can count cannot self-heal");
            }
        }

        /// <summary>Observes the local player's shelf content edit. The game's own
        /// <c>SetCompactCardDataAmountList</c> already ran (the UI mutates the shelf's list in
        /// place before calling it), so the postfix forwards exactly one intent for the shelf's
        /// current content. The shelf itself holds the state; a rejected edit is repaired by the
        /// host's next authoritative broadcast.</summary>
        internal static void StorageContentPostfix(InteractableCardStorageShelf __instance)
        {
            var self = Current;
            if (ApplyingRemote || __instance == null || self == null)
            {
                return;
            }

            var index = self.IndexOf(KindCardStorage, __instance);
            if (index < 0)
            {
                return;
            }

            var desired = CloneCards(__instance.GetCompactCardDataAmountList());
            var desiredCanTake = __instance.CanWorkerTake();
            var command = new ContainerOpMessage
            {
                Op = OpContentSet,
                Kind = (byte)KindCardStorage,
                Index = (byte)index,
                CanWorkerTake = desiredCanTake,
                Cards = CloneCards(desired),
            };
            WorldPrediction.Predict(WorldPrediction.ContainersScope, command,
                () => self.ApplyContentInPlace(__instance, CloneCards(desired), desiredCanTake),
                () => { });
        }

        /// <summary>Observes the local player's donation-box content edit; the UI mutated the
        /// donation box's list in place, so the postfix forwards one intent for the box's current
        /// content.</summary>
        internal static void DonationContentPostfix(InteractableBulkDonationBox __instance)
        {
            var self = Current;
            if (ApplyingRemote || __instance == null || self == null)
            {
                return;
            }

            var index = self.IndexOf(KindDonation, __instance);
            if (index < 0)
            {
                return;
            }

            var desired = CloneCards(__instance.GetCompactCardDataAmountList());
            var command = new ContainerOpMessage
            {
                Op = OpContentSet,
                Kind = (byte)KindDonation,
                Index = (byte)index,
                Cards = CloneCards(desired),
            };
            WorldPrediction.Predict(WorldPrediction.ContainersScope, command,
                () => self.ApplyContentInPlace(__instance, CloneCards(desired)),
                () => { });
        }

        /// <summary>Observes the local player's worker-take toggle. Vanilla <c>SetCanWorkerTake</c>
        /// already set the flag; the postfix forwards one intent and the refresh mesh is handled by
        /// the setting screen's own close path, exactly as in vanilla.</summary>
        internal static void WorkerTakePrefix(InteractableCardStorageShelf __instance,
            bool canWorkerTake, out WorkerTakeObserve __state)
        {
            __state = null;
            var self = Current;
            if (ApplyingRemote || __instance == null || self == null)
            {
                return;
            }

            var index = self.IndexOf(KindCardStorage, __instance);
            if (index < 0)
            {
                return;
            }

            __state = new WorkerTakeObserve
            {
                Index = index,
                Prior = __instance.CanWorkerTake(),
                Desired = canWorkerTake,
            };
        }

        internal static void WorkerTakePostfix(InteractableCardStorageShelf __instance,
            WorkerTakeObserve __state)
        {
            if (__state == null || __instance == null || __state.Desired == __state.Prior)
            {
                return;
            }

            var command = new ContainerOpMessage
            {
                Op = OpWorkerTakeFlag,
                Kind = (byte)KindCardStorage,
                Index = (byte)__state.Index,
                CanWorkerTake = __state.Desired,
            };
            WorldPrediction.Predict(WorldPrediction.ContainersScope, command,
                () => ApplyWorkerTake(__instance, __state.Desired),
                () => ApplyWorkerTake(__instance, __state.Prior));
        }

        /// <summary>Observes the local player's cleanser toggle. Vanilla <c>OnMouseButtonUp</c>
        /// already flipped the machine (plus its local tooltips/sound); the postfix forwards one
        /// intent and the undo restores the exact pre-toggle cooldown/timer.</summary>
        internal static void CleanserTogglePrefix(InteractableAutoCleanser __instance,
            out CleanserToggleObserve __state)
        {
            __state = null;
            var self = Current;
            if (ApplyingRemote || __instance == null || self == null)
            {
                return;
            }

            var index = self.IndexOf(KindCleanser, __instance);
            if (index < 0)
            {
                return;
            }

            __state = new CleanserToggleObserve
            {
                Index = index,
                Prior = __instance.IsTurnedOn(),
                PriorCooldown = FiClCooldown?.GetValue(__instance) as bool? ?? false,
                PriorTimer = FiClTimer?.GetValue(__instance) as float? ?? 0f,
            };
        }

        internal static void CleanserTogglePostfix(InteractableAutoCleanser __instance,
            CleanserToggleObserve __state)
        {
            if (__state == null || __instance == null)
            {
                return;
            }

            var desired = __instance.IsTurnedOn();
            if (desired == __state.Prior)
            {
                return;
            }

            var command = new ContainerOpMessage
            {
                Op = OpCleanserToggle,
                Kind = (byte)KindCleanser,
                Index = (byte)__state.Index,
                TurnedOn = desired,
            };
            WorldPrediction.Predict(WorldPrediction.ContainersScope, command,
                () => ApplyCleanserToggle(__instance, desired),
                () =>
                {
                    FiClTurnedOn?.SetValue(__instance, __state.Prior);
                    FiClCooldown?.SetValue(__instance, __state.PriorCooldown);
                    FiClTimer?.SetValue(__instance, __state.PriorTimer);
                });
        }

        private static void ApplyWorkerTake(InteractableCardStorageShelf shelf, bool canWorkerTake)
        {
            ApplyingRemote = true;
            try
            {
                shelf.SetCanWorkerTake(canWorkerTake);
                shelf.OnCardStorageShelfSettingDone();
            }
            finally
            {
                ApplyingRemote = false;
            }
        }

        private static void ApplyCleanserToggle(InteractableAutoCleanser cleanser, bool turnedOn)
        {
            ApplyingRemote = true;
            try
            {
                FiClTurnedOn?.SetValue(cleanser, turnedOn);
                if (!turnedOn)
                {
                    FiClCooldown?.SetValue(cleanser, true);
                    FiClTimer?.SetValue(cleanser, 0f);
                }
            }
            finally
            {
                ApplyingRemote = false;
            }
        }

        public static void HostStorageContentPostfix(InteractableCardStorageShelf __instance)
        {
            Current?.HostChanged(KindCardStorage, __instance);
        }

        public static void HostDonationContentPostfix(InteractableBulkDonationBox __instance)
        {
            Current?.HostChanged(KindDonation, __instance);
        }

        public static void HostWorkerTakePostfix(InteractableCardStorageShelf __instance)
        {
            Current?.HostChanged(KindCardStorage, __instance);
        }

        public static void CardStorageRandomCardPostfix(InteractableCardStorageShelf __instance)
        {
            Current?.HostChanged(KindCardStorage, __instance);
        }

        public static void DonationRandomCardPostfix(InteractableBulkDonationBox __instance)
        {
            Current?.HostChanged(KindDonation, __instance);
        }

        /// <summary>Broadcasts one host-side pack-opener change. Sync code applying a remote intent
        /// sets <see cref="ApplyingRemote"/> and emits the intent's own single
        /// <c>HostChanged</c>; without this guard the patched mutate method inside that apply
        /// would broadcast the same record a second time, and the duplicate delta would re-apply
        /// the stale record over the requesting client's reconcile.</summary>
        public static void PackOpenerChangedPostfix(InteractableAutoPackOpener __instance)
        {
            if (ApplyingRemote)
            {
                return;
            }

            Current?.HostChanged(KindPackOpener, __instance);
        }

        public static void CleanserChangedPostfix(InteractableAutoCleanser __instance)
        {
            Current?.HostChanged(KindCleanser, __instance);
        }

        /// <summary>Snapshots the pack opener before vanilla's click runs.</summary>
        internal static void PackOpenerClickPrefix(InteractableAutoPackOpener __instance,
            out PackOpenerObserve __state)
        {
            __state = null;
            var self = Current;
            if (ApplyingRemote || __instance == null || self == null)
            {
                return;
            }

            var idx = self.IndexOf(KindPackOpener, __instance);
            if (idx < 0)
            {
                return;
            }

            __state = new PackOpenerObserve
            {
                Index = idx,
                PriorProcessing = __instance.GetIsProcessing(),
                PriorStored = __instance.GetStoredItemList()?.Count ?? 0,
                PriorOutput = __instance.GetCompactCardDataAmountList()?.Count ?? 0,
                PriorOpened = __instance.GetPackOpenedCount(),
                PriorState = __instance.m_CurrentState,
                PriorTimer = FiPoOpenTimer?.GetValue(__instance) as float? ?? 0f,
            };
        }

        /// <summary>Observes the local player's pack-opener click after vanilla ran it. Vanilla
        /// turned the machine on or collected its output; the postfix forwards the matching intent
        /// (turn-on or claim) exactly once. The host's authoritative container state reconciles the
        /// local roll.</summary>
        internal static void PackOpenerClickPostfix(InteractableAutoPackOpener __instance,
            PackOpenerObserve __state)
        {
            Current?.ClientPackOpenerClick(__instance, __state);
        }

        /// <summary>Pre-action snapshot for an observed empty-box take. Vanilla spawns and holds a
        /// box the host has not created yet, so the take must be covered (no separate pickup
        /// prediction) and only forwarded when the storage count actually moved: a refused take
        /// (storage emptied on another peer, or the spawn-frame CanPickup race) must not send an
        /// intent whose rollback would adjust the count for a take that never happened.</summary>
        internal sealed class EmptyBoxTakeObserve
        {
            internal bool Covered;
            internal int PriorCount;
        }

        /// <summary>Observes the local player's empty-box take after vanilla spawned and held the
        /// box. The postfix forwards one intent; the host mints the authoritative box and its delta
        /// reconciles the local copy through the box engine.</summary>
        internal static void EmptyBoxTakePostfix(InteractableEmptyBoxStorage __instance,
            EmptyBoxTakeObserve __state)
        {
            var self = Current;
            if (self == null)
            {
                return;
            }

            if (__state != null && __instance != null
                && __instance.GetBoxStoredCount() >= __state.PriorCount)
            {
                // The vanilla take did not happen; nothing to forward, and its prediction would
                // have rolled the storage count the wrong way.
                return;
            }

            self.ClientEmptyBoxTake(__instance);
        }

        /// <summary>Client: opens the covered-hold window around the vanilla empty-box take. The
        /// take spawns and holds a box the host has not created yet, so without the cover the box
        /// engine would send a separate pickup prediction for it, the host would refuse it as an
        /// unknown box, and the rollback would drop the just-taken box out of the hand. The take's
        /// own container op owns the action and the host announces the resulting hold.</summary>
        internal static void EmptyBoxTakePrefix(InteractableEmptyBoxStorage __instance,
            out EmptyBoxTakeObserve __state)
        {
            __state = null;
            var self = Current;
            if (self?._playerBox == null || __instance == null)
            {
                return;
            }

            self._playerBox.BeginCoveredHold();
            __state = new EmptyBoxTakeObserve
            {
                Covered = true,
                PriorCount = __instance.GetBoxStoredCount(),
            };
        }

        /// <summary>Closes the covered-hold window opened by <see cref="EmptyBoxTakePrefix"/>.
        /// Runs after the take postfix, which needs the hold still covered while it reads the box
        /// the vanilla take put in the hand.</summary>
        internal static void EmptyBoxTakeFinalizer(EmptyBoxTakeObserve __state)
        {
            if (__state?.Covered == true)
            {
                Current?._playerBox.EndCoveredHold();
            }
        }

        /// <summary>Captures the local player's empty-box store before the game's own
        /// <c>StoreBox</c> consumes the box. The game performs the whole store (destroy + count);
        /// the postfix forwards one intent when the count actually moved. The destroy suppression
        /// stops the box engine from emitting a competing BoxDestroyRequest for a box the container
        /// op already carries by id.</summary>
        internal static void EmptyBoxStorePrefix(InteractableEmptyBoxStorage __instance,
            InteractablePackagingBox_Item packagingBox, bool isPlayer,
            out EmptyBoxStoreObserve __state)
        {
            __state = null;
            if (ApplyingRemote || __instance == null || packagingBox == null)
            {
                return;
            }

            var self = Current;
            if (self?._boxes == null)
            {
                return;
            }

            if (!isPlayer || !self._boxes.TryGetId(packagingBox, out var boxId))
            {
                // A worker store (host-driven) has no client-side forward. A player store of a box
                // the host never assigned (a still-unbound candidate) has no authoritative identity,
                // so there is nothing to forward; warn so the divergence is diagnosable.
                if (isPlayer)
                {
                    CoopPlugin.Log.LogWarning("WorldContainerInteraction: empty-box store of box "
                        + packagingBox.name + " has no authoritative id; not forwarding.");
                }

                return;
            }

            var idx = self.IndexOf(KindEmptyBoxStorage, __instance);
            if (idx < 0)
            {
                return;
            }

            self._boxes.BeginContainerConsume();
            __state = new EmptyBoxStoreObserve
            {
                Self = self,
                Storage = __instance,
                Index = idx,
                BoxNetworkId = boxId,
                PriorCount = __instance.GetBoxStoredCount(),
                IsPlayer = isPlayer,
                Consuming = true,
            };
        }

        internal static void EmptyBoxStorePostfix(InteractableEmptyBoxStorage __instance,
            EmptyBoxStoreObserve __state)
        {
            if (__state == null)
            {
                return;
            }

            __state.EndConsume();
            if (__instance == null || __instance.GetBoxStoredCount() != __state.PriorCount + 1)
            {
                // Vanilla refused the store (toggling, not empty, wrong size, or full): no
                // mutation happened, so there is nothing authoritative to forward.
                return;
            }

            var self = __state.Self;
            var command = new ContainerOpMessage
            {
                Op = OpEmptyBoxStore,
                Kind = (byte)KindEmptyBoxStorage,
                Index = (byte)__state.Index,
                BoxNetworkId = __state.BoxNetworkId,
                IsPlayer = __state.IsPlayer,
            };
            WorldPrediction.Predict(WorldPrediction.ContainersScope, command,
                () => self.ApplyEmptyBoxStored(__state.Storage, __state.BoxNetworkId),
                () => self.ApplyEmptyBoxTaken(__state.Storage, __state.BoxNetworkId));
        }

        internal static Exception EmptyBoxStoreFinalizer(Exception __exception,
            EmptyBoxStoreObserve __state)
        {
            // Vanilla StoreBox can throw after the prefix armed the consume guard (a mid-frame
            // box teardown); release it so a later destroy is not silently swallowed. Returning
            // null lets the original exception propagate.
            __state?.EndConsume();
            return null;
        }

        public static void EmptyBoxTakeChangedPostfix(InteractableEmptyBoxStorage __instance)
        {
            Current?.HostChanged(KindEmptyBoxStorage, __instance);
        }

        public static void EmptyBoxStoreChangedPostfix(InteractableEmptyBoxStorage __instance)
        {
            Current?.HostChanged(KindEmptyBoxStorage, __instance);
        }

        /// <summary>Observes a pack the local player inserted. Vanilla <c>AddItem</c> already put
        /// the pack into the machine and moved the local queue, so only one <c>OpPackInsert</c> is
        /// forwarded and the apply/undo closures replay the game's own insert/remove for a
        /// rejection. An accepted insert echo reconciles in layers against this machine's own
        /// in-flight inserts (see <see cref="ClientApplyDelta"/>) instead of applying the stale
        /// record raw, which used to trim them and bounce the visible pack count. The guest's join
        /// world-load restores the host save via <c>InteractableAutoPackOpener.LoadData</c>, which
        /// calls AddItem once per stored pack (decompiled ~384). Those are not player inserts, so
        /// the reload forwards nothing.</summary>
        public static void PackOpenerAddItemPostfix(InteractableAutoPackOpener __instance, Item item)
        {
            if (ApplyingRemote || __instance == null || item == null || Reloading)
            {
                return;
            }

            var self = Current;
            var idx = self?.IndexOf(KindPackOpener, __instance) ?? -1;
            if (idx < 0)
            {
                CoopPlugin.Log.LogWarning("WorldContainerInteraction: pack insert had no placement "
                    + "identity; not forwarding.");
                return;
            }

            var command = new ContainerOpMessage
            {
                Op = OpPackInsert,
                Kind = (byte)KindPackOpener,
                Index = (byte)idx,
                // the host spawns a real pack prefab from this, so a modded id minted in
                // a different order here would insert the WRONG product on the host
                ItemType = item.GetItemType(),
            };
            // One prediction key per machine: an insert echo's layered reconcile must undo/replay
            // only this opener's own in-flight inserts, never another container's pending action.
            Guid predictionId = Guid.Empty;
            predictionId = WorldPrediction.Predict(PackInsertPredictionKey(idx), command,
                () =>
                {
                    ApplyingRemote = true;
                    try
                    {
                        if (!__instance.GetStoredItemList().Contains(item))
                        {
                            // A reconcile may have pooled the item while its prediction was in
                            // flight (a remote record trimmed the tail); bring it back visible
                            // before the game is handed it again.
                            item.gameObject.SetActive(true);
                            __instance.AddItem(item, addToFront: true, isPlayer: false);
                        }
                    }
                    finally
                    {
                        ApplyingRemote = false;
                    }
                },
                () =>
                {
                    // A follower is undone only for the duration of the reconcile and is replayed
                    // right after, so it is removed here. The retired target is left in place:
                    // either the authoritative record being applied already carries it, or a
                    // terminal rejection's callback hands it back.
                    if (!PredictionApi.IsPending(predictionId)
                        || !__instance.GetStoredItemList().Contains(item))
                    {
                        return;
                    }

                    ApplyingRemote = true;
                    try
                    {
                        __instance.RemoveItem(item);
                    }
                    finally
                    {
                        ApplyingRemote = false;
                    }
                },
                () =>
                {
                    // Terminal rejection: the target undo above deliberately left the pack in the
                    // machine, so take it out here and put it back in the acting hand.
                    self._pendingPackInserts.Remove(predictionId);
                    ApplyingRemote = true;
                    try
                    {
                        if (__instance.GetStoredItemList().Contains(item))
                        {
                            __instance.RemoveItem(item);
                        }
                    }
                    finally
                    {
                        ApplyingRemote = false;
                    }
                    RestoreHeldPack(item);
                });
            self._pendingPackInserts.Add(predictionId);
        }

        /// <summary>Observes the local player's cleanser refill. The game's own <c>AddItem</c> has
        /// already inserted the can (and moved its counter), so only one intent is forwarded and
        /// the apply/undo closures replay the game's insert/remove. The reload path is not a player
        /// action and is never forwarded, but it still runs vanilla: LoadData ends with an
        /// unconditional m_ItemAmount assignment, so suppressing the adds used to leave the counter
        /// and the list out of step and every later reconcile threw IndexOutOfRange.</summary>
        internal static void CleanserAddItemPostfix(InteractableAutoCleanser __instance, Item item)
        {
            if (ApplyingRemote || __instance == null || item == null || Reloading)
            {
                return;
            }

            var self = Current;
            var idx = self?.IndexOf(KindCleanser, __instance) ?? -1;
            if (idx < 0)
            {
                CoopPlugin.Log.LogWarning("WorldContainerInteraction: cleanser refill had no "
                    + "placement identity; not forwarding.");
                return;
            }

            var command = new ContainerOpMessage
            {
                Op = OpCleanserRefill,
                Kind = (byte)KindCleanser,
                Index = (byte)idx,
                Fill = item.GetContentFill(),
            };
            WorldPrediction.Predict(WorldPrediction.ContainersScope, command,
                () =>
                {
                    ApplyingRemote = true;
                    try
                    {
                        if (!__instance.GetStoredItemList().Contains(item))
                        {
                            __instance.AddItem(item, true);
                        }
                    }
                    finally
                    {
                        ApplyingRemote = false;
                    }
                },
                () =>
                {
                    ApplyingRemote = true;
                    try
                    {
                        if (__instance.GetStoredItemList().Contains(item))
                        {
                            __instance.RemoveItem(item);
                        }
                    }
                    finally
                    {
                        ApplyingRemote = false;
                    }
                });
        }

        // ---------------- shared helpers ----------------

        /// <summary>One prediction key per pack opener, so an insert echo's layered reconcile only
        /// undoes/replays that machine's own in-flight inserts.</summary>
        private static string PackInsertPredictionKey(int idx)
            => WorldPrediction.ContainersScope + ":pack:" + idx;

        /// <summary>Puts a pack that a rejected insert removed from the local hand back where it
        /// was. Vanilla's EvaluatePutItemOnShelf removes the pack from the hold list right after
        /// calling AddItem, so undoing only the machine side would leave the pack re-enabled but
        /// orphaned (and, if its lerp flags survived, drifting to the old hold position).</summary>
        private static void RestoreHeldPack(Item item)
        {
            if (item == null)
            {
                return;
            }

            item.gameObject.SetActive(true);
            var controller = SceneRef<InteractionPlayerController>.Get();
            if (controller != null)
            {
                controller.AddHoldItemToFront(item);
            }
        }

        private static void RestorePackOpener(InteractableAutoPackOpener opener,
            List<Item> stored, bool processing, float timer, int openedCount, int currentState)
        {
            var current = opener.GetStoredItemList();
            if (current != null)
            {
                for (var i = current.Count - 1; i >= 0; i--)
                {
                    if (stored.Contains(current[i]))
                    {
                        continue;
                    }

                    var extra = current[i];
                    current.RemoveAt(i);
                    if (extra != null)
                    {
                        extra.DisableItem();
                    }
                }
                current.Clear();
                current.AddRange(stored);
            }

            FiPoIsProcessing?.SetValue(opener, processing);
            FiPoOpenTimer?.SetValue(opener, timer);
            FiPoOpenedCount?.SetValue(opener, openedCount);
            opener.m_CurrentState = currentState;
        }

        private static void RestoreCleanser(InteractableAutoCleanser cleanser,
            List<Item> stored, int itemAmount, bool needRefill)
        {
            var current = cleanser.GetStoredItemList();
            if (current != null)
            {
                for (var i = current.Count - 1; i >= 0; i--)
                {
                    if (stored.Contains(current[i]))
                    {
                        continue;
                    }

                    var extra = current[i];
                    current.RemoveAt(i);
                    if (extra != null)
                    {
                        extra.DisableItem();
                    }
                }
                current.Clear();
                current.AddRange(stored);
            }

            FiClItemAmount?.SetValue(cleanser, itemAmount);
            FiClNeedRefill?.SetValue(cleanser, needRefill);
        }

        /// <summary>The game's own save-load recipe for materializing an Item by type.</summary>
        private static Item SpawnItem(EItemType itemType, Transform parent, float contentFill = -1f)
        {
            Item item = null;
            try
            {
                var meshData = InventoryBase.GetItemMeshData(itemType);
                item = ItemSpawnManager.GetItem(parent);
                item.SetMesh(meshData.mesh, meshData.material, itemType,
                    meshData.meshSecondary, meshData.materialSecondary);
                item.transform.localPosition = Vector3.zero;
                item.transform.localRotation = Quaternion.identity;
                if (contentFill >= 0f)
                {
                    item.SetContentFill(contentFill);
                }

                item.gameObject.SetActive(true);
                return item;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning($"WorldContainerInteraction spawn {itemType}: {e.Message}");
                if (item != null)
                {
                    item.DisableItem();
                }
                throw;
            }
        }

        /// <summary>Deep-copies a compact-card list. <see cref="CompactCardDataAmount"/> is a
        /// mutable reference type the UI edits in place, so a shallow copy would alias the game's
        /// entries and corrupt both the prediction closures and the content mirror.</summary>
        private static List<CompactCardDataAmount> CloneCards(List<CompactCardDataAmount> cards)
        {
            var copy = new List<CompactCardDataAmount>(cards?.Count ?? 0);
            for (var i = 0; cards != null && i < cards.Count; i++)
            {
                var card = cards[i];
                copy.Add(card == null ? null : new CompactCardDataAmount
                {
                    expansionType = card.expansionType,
                    cardSaveIndex = card.cardSaveIndex,
                    amount = card.amount,
                    gradedCardIndex = card.gradedCardIndex,
                    isDestiny = card.isDestiny,
                });
            }
            return copy;
        }

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(double value)
            => !double.IsNaN(value) && !double.IsInfinity(value);

        private static void Try(Harmony h, Type type, string method,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null,
            HarmonyMethod finalizer = null)
        {
            try
            {
                var original = ReflectionSurface.RequiredMethod(type, method);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning($"Patch target missing: {type.Name}.{method}");
                    return;
                }
                h.Patch(original, prefix: prefix, postfix: postfix, finalizer: finalizer);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning($"Patch failed for {type.Name}.{method}: {e.Message}");
            }
        }
    }
}
