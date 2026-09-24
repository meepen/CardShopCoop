using CardShopCoop.Util;
using CardShopCoop.Net;
using CardShopCoop.Runtime;
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
    /// Host-authoritative, keyed by Placement (kind, index): mutation hooks push
    /// exactly one changed record; joiner actions are blocked-and-forwarded as ops
    /// the host applies through
    /// the vanilla methods, and the next broadcast is the authoritative state.
    ///
    /// Pack opener specifics: the client keeps only inert visual queue items and blocks
    /// its own OpenPack RNG path, so vanilla's local Update can render the synchronized
    /// timer without inventing cards the host never rolled. Collect runs the reveal UI on
    /// the COLLECTOR (its AddCard calls travel through the existing CardDelta mirror into
    /// the shared binder), while the host clears the machine and banks the report counters
    /// WITHOUT re-adding the cards. No coin moves through this module, so the double-charge
    /// question never arises.
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

        /// <summary>Expected, non-mutating command validation failure.</summary>
        private sealed class ContainerOperationValidationException : InvalidOperationException
        {
            internal ContainerOperationValidationException(string message) : base(message)
            {
            }
        }

        /// <summary>Set by WorldCardInteraction: client -> host op (ContainerOpMessage).</summary>
        public Action<INetMessage> SendOp;
        /// <summary>Set by WorldCardInteraction: host -> clients state (ContainerStateMessage).</summary>
        public Action<INetMessage> BroadcastState;
        public Action<int, INetMessage> SendToClient;
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

        /// <summary>Client's copy of a pack opener's host-side truth. The game object receives
        /// only the inert visual queue and synchronized timer; the mirror remains authoritative
        /// for output/state and keeps the client from running pack RNG.</summary>
        private class PackMirror
        {
            public int StoredCount;
            public List<int> StoredTypes = new();
            public bool Processing;
            public int OpenedCount;
            public List<CompactCardDataAmount> Output = new();
            public int CurrentState;
            public bool CollectClaimed;
            public double PackStartTimestamp;
            public float PackDuration;
            public double LocalStartTimestamp;
        }

        /// <summary>One submitted pack-collect claim awaiting its ordered authoritative delta.
        /// The reveal is deliberately not applied to the local inventory until that delta.</summary>
        private sealed class PendingPackCollection
        {
            public int ClaimToken;
            public int OpenedCount;
            public List<CompactCardDataAmount> Cards;
            public bool CardsShown;
            public bool ReportUpdated;
        }

        private readonly Dictionary<object, int> _hostKeys = new();
        // A partial is exactly one ContainerRecord. Index is the packed kind/index identity;
        // Records carries the record itself, so omitted records are never deletions.
        private BulkDonationBoxUIScreen _cachedContainerScreen;
        private BulkDonationBoxPlusMinusScreen _cachedAmountModal;
        private readonly Dictionary<int, PackMirror> _packMirrors = new();
        private readonly Dictionary<int, PendingPackCollection> _pendingPackCollections = new();
        private readonly Dictionary<int, int> _packClaimOwner = new();
        private readonly Dictionary<int, int> _packClaimToken = new();
        private int _nextPackClaimToken = 1;
        private ContainerStateMessage _pendingClientState;
        private readonly BoxNetworkInteraction _boxes;
        private Guid _hostPredictionId;
        private BoxNetworkState _hostDeltaBox;
        private bool _hostDeltaTakeIntoHand;
        private bool _hostDeltaReleaseHold;
        private bool _hostCompletePackCollection;
        private byte _hostPackIndex;
        private int _hostPackOpenedCount;
        private List<CompactCardDataAmount> _hostRevealedCards;

        internal WorldContainerInteraction(BoxNetworkInteraction boxes = null)
        {
            _boxes = boxes;
        }

        public override string Name => "containers";

        public override void Reset()
        {
            _hostKeys.Clear();
            _cachedContainerScreen = null;
            _cachedAmountModal = null;
            _packMirrors.Clear();
            _pendingPackCollections.Clear();
            _packClaimOwner.Clear();
            _packClaimToken.Clear();
            _nextPackClaimToken = 1;
            _pendingClientState = null;
            _hostPredictionId = Guid.Empty;
            _hostDeltaBox = null;
            _hostDeltaTakeIntoHand = false;
            _hostDeltaReleaseHold = false;
            _hostCompletePackCollection = false;
            _hostPackIndex = 0;
            _hostPackOpenedCount = 0;
            _hostRevealedCards = null;
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
            BroadcastState?.Invoke(new ContainerDeltaMessage
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
                PackOpenedCount = _hostPackOpenedCount,
                RevealedCards = _hostRevealedCards ?? new List<CompactCardDataAmount>(),
            });
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
                            // literal 0 - 0 is a REAL item type, so a null slot used to arrive
                            // indistinguishable from that item. INERT EITHER WAY: PackMirror.StoredTypes
                            // is written and never read by anything, so nothing observable changes;
                            // this exists so the field cannot become a bug the day something reads it.
                            rec.StoredTypes.Add(stored[i] != null ? stored[i].GetItemType() : EItemType.None);
                        }

                        rec.Processing = p != null && p.GetIsProcessing();
                        rec.Timer = p != null ? (FiPoOpenTimer?.GetValue(p) as float? ?? 0f) : 0f;
                        rec.OpenedCount = p != null ? p.GetPackOpenedCount() : 0;
                        rec.Cards = p?.GetCompactCardDataAmountList() ?? new List<CompactCardDataAmount>();
                        var packNow = Time.realtimeSinceStartupAsDouble;
                        rec.PackTimestamp = packNow;
                        // Vanilla leaves m_CurrentState at 0 when a worker (or a full
                        // hopper) starts the machine through AddItem. The UI is still
                        // processing in that case, so advertise the effective state
                        // rather than the stale implementation detail.
                        rec.CurrentState = p != null && p.GetIsProcessing()
                            ? ((stored?.Count ?? 0) > 0 ? 1 : 2)
                            : 0;
                        if (rec.CurrentState == 1 && p != null)
                        {
                            var cycleDuration = Mathf.Max(0.001f, p.m_PackOpenTime);
                            var elapsed = Mathf.Clamp(rec.Timer, 0f, cycleDuration);
                            rec.PackStartTimestamp = packNow - elapsed;
                            // The game consumes one pack every cycle.  Duration is the
                            // remaining queue from the beginning of the current cycle;
                            // elapsed is recovered from the two synchronized timestamps.
                            rec.PackDuration = cycleDuration * (stored?.Count ?? 0);
                        }
                        else
                        {
                            rec.PackStartTimestamp = packNow;
                            rec.PackDuration = 0f;
                        }
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
                            var itemType = message.ItemType; // guest id -> ours; see PackOpenerAddItemPrefix
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
                            // apply unconditionally (like a worker refill would): dropping it
                            // would eat the pack the joiner's box already gave up
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
                            var revealed = message.Cards;
                            accepted = HostApplyPackCollect(idx, message.ClaimToken, revealed, connId,
                                out reason);
                            if (accepted)
                            {
                                _hostCompletePackCollection = true;
                                _hostPackIndex = message.Index;
                                _hostPackOpenedCount = GetOrCreatePackMirror(idx).OpenedCount;
                                _hostRevealedCards = new List<CompactCardDataAmount>(revealed);
                                HostChanged(KindPackOpener, Get<InteractableAutoPackOpener>(
                                    KindPackOpener, idx));
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
                                accepted = HostTakeEmptyBox(storage, out reason, out createdBox);
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

                            var boxId = message.BoxNetworkId > 0 ? message.BoxNetworkId : message.BoxId;
                            accepted = HostStoreEmptyBox(storage, boxId, out reason);
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
            catch (ContainerOperationValidationException e)
            {
                reason = e.Message;
            }

            if (!accepted && reason != null)
            {
                CoopPlugin.Log.LogWarning("WorldContainerInteraction rejected op=" + op
                    + " kind=" + kind + " index=" + idx + ": " + reason);
            }
            _hostPredictionId = Guid.Empty;
            _hostDeltaBox = null;
            _hostDeltaTakeIntoHand = false;
            _hostDeltaReleaseHold = false;
            _hostCompletePackCollection = false;
            _hostPackIndex = 0;
            _hostPackOpenedCount = 0;
            _hostRevealedCards = null;
            return accepted;
        }

        private bool HostTakeEmptyBox(InteractableEmptyBoxStorage storage,
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

            var box = RestockManager.SpawnPackageBoxItem(EItemType.None, 0, true);
            if (box == null)
            {
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
                box.OnDestroyed();
                reason = "created empty box cannot be picked up";
                return false;
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

                throw new InvalidOperationException("created empty box has no authoritative descriptor");
            }
            catch
            {
                FiEmptyStoredCount.SetValue(storage, count);
                MiEmptyEvaluateStack.Invoke(storage, null);
                box.OnDestroyed();
                throw;
            }
        }

        private bool HostStoreEmptyBox(InteractableEmptyBoxStorage storage, long boxId,
            out string reason)
        {
            reason = null;
            if (boxId <= 0 || !_boxes.TryGetBox(boxId, out var box)
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

            _packClaimOwner[key] = connId;
            _packClaimToken[key] = token;
            var claim = new ContainerPackClaimMessage
            {
                PredictionId = _hostPredictionId,
                Index = (byte)idx,
                ClaimToken = token,
                Cards = new List<CompactCardDataAmount>(output),
            };
            if (response != null)
                response(claim);
            else
                SendToClient?.Invoke(connId, claim);
            return true;
        }

        private bool HostApplyPackCollect(int idx, int token,
            List<CompactCardDataAmount> revealed, int connId, out string reason)
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
            if (revealed == null)
            {
                reason = "pack reveal is missing";
                return false;
            }
            if (!_packClaimOwner.TryGetValue(key, out var owner) || owner != connId
                || !_packClaimToken.TryGetValue(key, out var expected) || expected != token
                || !SameCards(output, revealed))
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
                        var types = new List<int>(rec.StoredTypes.Count);
                        for (var i = 0; i < rec.StoredTypes.Count; i++)
                            types.Add((int)rec.StoredTypes[i]);

                        var p = Get<InteractableAutoPackOpener>(kind, idx);
                        if (!_packMirrors.TryGetValue(idx, out var m))
                            _packMirrors[idx] = m = new PackMirror();
                        m.StoredCount = types.Count;
                        m.StoredTypes = types;
                        m.Processing = rec.Processing;
                        m.OpenedCount = rec.OpenedCount;
                        m.Output = rec.Cards;
                        m.CurrentState = rec.CurrentState;
                        m.CollectClaimed = rec.CollectClaimed;
                        m.PackStartTimestamp = rec.PackStartTimestamp;
                        m.PackDuration = rec.PackDuration;
                        var receivedAt = Time.realtimeSinceStartupAsDouble;
                        var elapsedAtSend = rec.PackTimestamp - rec.PackStartTimestamp;
                        m.LocalStartTimestamp = rec.CurrentState == 1
                            ? receivedAt - Math.Max(0.0, elapsedAtSend) : receivedAt;
                        ApplyPackMirrorToMachine(p, m);
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

            ClientApplyState(new ContainerStateMessage
            {
                Records = new List<ContainerRecord> { message.Record },
            });

            if (message.HasBox)
            {
                _boxes.ClientApplyCreated(new BoxCreatedMessage { Box = message.Box });
                if (!_boxes.TryGetBox(message.Box.BoxNetworkId, out var box))
                    throw new InvalidOperationException("Container delta box was not materialized.");

                if (message.TakeIntoHand && box is InteractablePackagingBox_Item item)
                {
                    var controller = SceneRef<InteractionPlayerController>.Get();
                    item.StartHoldBox(true, controller.m_HoldItemPos);
                }
            }

            if (message.ReleaseHold)
            {
                SceneRef<InteractionPlayerController>.Get()?.OnExitHoldBoxMode();
            }

            if (message.CompletePackCollection)
            {
                _pendingPackCollections.TryGetValue(message.PackIndex, out var pending);
                // The state delta is broadcast to every peer, but only the claimant owns the
                // reveal UI and pending claim token. Other peers still apply the authoritative
                // container record above and must not fabricate a local reveal.
                if (pending != null)
                    CompletePackCollection(message.PackIndex, pending);
            }
        }

        private void CompletePackCollection(int idx, PendingPackCollection pending)
        {
            var controller = SceneRef<InteractionPlayerController>.Get();
            if (controller == null || controller.m_ShowCardObtainedPage == null)
                throw new InvalidOperationException("Pack collection delta arrived without the reveal UI.");

            if (!pending.CardsShown)
            {
                controller.m_ShowCardObtainedPage.ShowCardObtained(pending.Cards);
                pending.CardsShown = true;
            }

            if (!pending.ReportUpdated)
            {
                CPlayerData.m_GameReportDataCollect.cardPackOpened += pending.OpenedCount;
                CPlayerData.m_GameReportDataCollectPermanent.cardPackOpened += pending.OpenedCount;
                AchievementManager.OnCardPackOpened(
                    CPlayerData.m_GameReportDataCollectPermanent.cardPackOpened);
                pending.ReportUpdated = true;
            }

            _pendingPackCollections.Remove(idx);
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

        /// <summary>Client: push a pack mirror onto the machine's UI/tooltip surface.
        /// The local queue contains inert visual items only. OpenPack is blocked on clients,
        /// while vanilla Update still advances its timer and paints its normal UI.</summary>
        private void ApplyPackMirrorToMachine(InteractableAutoPackOpener p, PackMirror m)
        {
            ApplyingRemote = true;
            try
            {
                SyncLocalPackItems(p, m.StoredCount, m.StoredTypes);
                FiPoIsProcessing?.SetValue(p, m.Processing); // drives the Collect tooltip
                FiPoOpenedCount?.SetValue(p, m.OpenedCount);
                // Keep the client object coherent with the state that vanilla's UI
                // actually represents. In particular, AddItem auto-starts a full
                // machine without setting m_CurrentState to 1.
                p.m_CurrentState = EffectivePackState(m);
                var now = Time.realtimeSinceStartupAsDouble;
                if (EffectivePackState(m) == 1)
                {
                    var cycle = Mathf.Max(0.001f, p.m_PackOpenTime);
                    var elapsed = Mathf.Clamp((float)(now - m.LocalStartTimestamp), 0f, cycle);
                    FiPoOpenTimer?.SetValue(p, elapsed);
                }
                else
                {
                    FiPoOpenTimer?.SetValue(p, 0f);
                }
                UpdatePackMirrorDisplay(p, m, now);
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
                    ItemSpawnManager.DisableItem(last);
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

        /// <summary>Paint the opener UI from the inert client mirror. This is deliberately
        /// separate from ApplyPackMirrorToMachine so the display never writes game simulation
        /// fields on the client.</summary>
        private static void UpdatePackMirrorDisplay(InteractableAutoPackOpener p, PackMirror m,
            double now)
        {
            if (p == null || m == null || FiPoUI?.GetValue(p) is not AutoCardOpenerUI ui)
            {
                return;
            }

            var state = EffectivePackState(m);
            if (state == 1)
            {
                var duration = Mathf.Max(0.001f, m.PackDuration);
                var elapsed = Mathf.Max(0f, (float)(now - m.LocalStartTimestamp));
                var remaining = Mathf.Max(0f, duration - elapsed);
                var cycle = Mathf.Max(0.001f, p.m_PackOpenTime);
                var virtualStored = Mathf.Max(0f, m.StoredCount - elapsed / cycle);
                ui.SetUIState(1);
                ui.UpdateProcessingFillBar(Mathf.Clamp01(
                    1f - virtualStored / Mathf.Max(1, p.m_MaxPackCount)));
                ui.UpdateProcessingTimeLeftText(remaining);
            }
            else if (state == 2)
            {
                ui.SetUIState(2);
            }
            else
            {
                ui.SetUIState(0);
                ui.UpdatePackCountText(m.StoredCount, p.m_MaxPackCount);
            }
        }

        /// <summary>Returns the state represented by the authoritative mirror. The state is
        /// carried separately because vanilla auto-starts a full opener from AddItem without
        /// updating the machine's m_CurrentState field.</summary>
        private static int EffectivePackState(PackMirror mirror) => mirror.CurrentState;

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
                    ItemSpawnManager.DisableItem(last);
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
                // the join-time fix in CleanserAddItemPrefix only helps from the next join).
                FiClItemAmount?.SetValue(c, stored?.Count ?? 0);
                // flags last: AddItem/RemoveItem flip m_IsNeedRefill on their own
                FiClTurnedOn?.SetValue(c, on);
                FiClNeedRefill?.SetValue(c, needRefill);
            }
            finally { ApplyingRemote = false; }
        }

        // ---------------- client: forwarded actions (called from patches) ----------------

        private void StartLocalPackCycle(InteractableAutoPackOpener opener, PackMirror mirror,
            int storedCount)
        {
            if (opener == null || mirror == null || storedCount <= 0)
            {
                return;
            }

            var now = Time.realtimeSinceStartupAsDouble;
            mirror.PackStartTimestamp = now;
            mirror.PackDuration = Mathf.Max(0.001f, opener.m_PackOpenTime) * storedCount;
            mirror.LocalStartTimestamp = now;
        }

        private void ClientPackOpenerClick(InteractableAutoPackOpener p)
        {
            var idx = IndexOf(KindPackOpener, p);
            if (idx < 0)
            {
                return;
            }

            var m = GetOrCreatePackMirror(idx);
            SoundManager.PlayAudio("SFX_ButtonLightTap", 0.6f, 0.5f);
            if (m == null || !m.Processing)
            {
                if (m != null && m.StoredCount > 0)
                {
                    var command = new ContainerOpMessage
                    {
                        Op = OpPackTurnOn,
                        Kind = (byte)KindPackOpener,
                        Index = (byte)idx
                    };
                    var priorProcessing = m.Processing;
                    var priorState = m.CurrentState;
                    var priorStart = m.PackStartTimestamp;
                    var priorDuration = m.PackDuration;
                    var priorLocalStart = m.LocalStartTimestamp;
                    WorldPrediction.Predict(WorldPrediction.ContainersScope, command,
                        () =>
                        {
                            m.Processing = true;
                            m.CurrentState = 1;
                            StartLocalPackCycle(p, m, m.StoredCount);
                            ApplyPackMirrorToMachine(p, m);
                        },
                        () =>
                        {
                            m.Processing = priorProcessing;
                            m.CurrentState = priorState;
                            m.PackStartTimestamp = priorStart;
                            m.PackDuration = priorDuration;
                            m.LocalStartTimestamp = priorLocalStart;
                            ApplyPackMirrorToMachine(p, m);
                        });
                }
                else
                {
                    NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.NoCardPackInMachine);
                }
            }
            else if (m.Output.Count > 0 && m.StoredCount <= 0)
            {
                if (m.CollectClaimed)
                {
                    if (_pendingPackCollections.TryGetValue(idx, out var pending))
                    {
                        TrySubmitPendingPackCollection(idx, pending);
                    }
                    NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.WaitAllCardPacksToBeProcessed);
                    return;
                }
                var claim = new ContainerOpMessage
                {
                    Op = OpPackClaim,
                    Kind = (byte)KindPackOpener,
                    Index = (byte)idx,
                };
                WorldPrediction.Predict(WorldPrediction.ContainersScope, claim,
                    () => m.CollectClaimed = true,
                    () => m.CollectClaimed = false);
            }
            else
            {
                NotEnoughResourceTextPopup.ShowText(ENotEnoughResourceText.WaitAllCardPacksToBeProcessed);
            }
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
                    // Only the host creates the physical box. Flagging the take as a player action
                    // makes the host's accepted delta arrive as TakeIntoHand, so the guest holds
                    // the authoritative box instead of a locally predicted duplicate.
                    IsPlayer = true,
                };
                // Predict only the storage count. The host's box is materialized by the box
                // engine from its BoxCreated/ContainerDelta and put into the hand by TakeIntoHand,
                // so the guest never spawns a second box it would then have to reconcile away.
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

        private void ClientEmptyBoxStore(InteractableEmptyBoxStorage storage,
            InteractablePackagingBox_Item box, bool isPlayer)
        {
            if (box == null || !_boxes.TryGetId(box, out var boxId))
                throw new InvalidOperationException("Empty-box store has no authoritative box ID.");

            var idx = IndexOf(KindEmptyBoxStorage, storage);
            if (idx >= 0)
            {
                var command = new ContainerOpMessage
                {
                    Op = OpEmptyBoxStore,
                    Kind = (byte)KindEmptyBoxStorage,
                    Index = (byte)idx,
                    BoxNetworkId = boxId,
                    IsPlayer = isPlayer,
                };
                WorldPrediction.Predict(WorldPrediction.ContainersScope, command,
                    () =>
                    {
                        ApplyingRemote = true;
                        _boxes?.BeginContainerConsume();
                        try
                        {
                            storage.StoreBox(box, false);
                        }
                        finally
                        {
                            _boxes?.EndContainerConsume();
                            ApplyingRemote = false;
                        }
                    },
                    () =>
                    {
                        ApplyingRemote = true;
                        try
                        {
                            storage.TakeBox(false);
                        }
                        finally
                        {
                            ApplyingRemote = false;
                        }
                    });
            }
        }

        private PackMirror GetOrCreatePackMirror(int idx)
        {
            if (!_packMirrors.TryGetValue(idx, out var mirror))
            {
                _packMirrors[idx] = mirror = new PackMirror();
            }

            return mirror;
        }

        public void ClientApplyPackClaim(ContainerPackClaimMessage message)
        {
            var revealed = new List<CompactCardDataAmount>(message.Cards);
            var opened = 0;
            var mirror = GetOrCreatePackMirror(message.Index);
            opened = mirror.OpenedCount;
            mirror.CollectClaimed = true;
            _pendingPackCollections[message.Index] = new PendingPackCollection
            {
                ClaimToken = message.ClaimToken,
                OpenedCount = opened,
                Cards = revealed,
            };
            // Sending the command is the first side effect. Until it is sent, leave the claim,
            // output, report counters, and local machine mirror untouched.
            TrySubmitPendingPackCollection(message.Index, _pendingPackCollections[message.Index]);
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
            WorldPrediction.Predict(WorldPrediction.ContainersScope, command,
                () => { }, () => { });
            return true;
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
                prefix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(StorageContentPrefix)));
            Try(h, typeof(InteractableBulkDonationBox), "SetCompactCardDataAmountList",
                prefix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(DonationContentPrefix)));
            Try(h, typeof(InteractableCardStorageShelf), "SetCanWorkerTake",
                prefix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(WorkerTakePrefix)));

            // Pack opener actions are host-owned on the client. The local queue is only an inert
            // visual mirror and never runs a second pack roll.
            Try(h, typeof(InteractableAutoPackOpener), "OnMouseButtonUp",
                prefix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(PackOpenerClickPrefix)));
            Try(h, typeof(InteractableAutoPackOpener), "AddItem",
                prefix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(PackOpenerAddItemPrefix)));
            Try(h, typeof(InteractableAutoPackOpener), "OpenPack",
                prefix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(PackOpenerSimulationPrefix)));
            Try(h, typeof(InteractableAutoPackOpener), "TakeItemToHand",
                prefix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(TakeItemBlockPrefix)));

            Try(h, typeof(InteractableAutoCleanser), "OnMouseButtonUp",
                prefix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(CleanserTogglePrefix)));
            Try(h, typeof(InteractableAutoCleanser), "AddItem",
                prefix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(CleanserAddItemPrefix)));
            Try(h, typeof(InteractableAutoCleanser), "RemoveItem",
                prefix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(ClientContainerMutationPrefix)));
            Try(h, typeof(InteractableAutoCleanser), "Spray",
                prefix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(ClientContainerMutationPrefix)));
            Try(h, typeof(InteractableAutoCleanser), "TakeItemToHand",
                prefix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(TakeItemBlockPrefix)));

            Try(h, typeof(InteractableEmptyBoxStorage), "OnMouseButtonUp",
                prefix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(EmptyBoxTakePrefix)));
            Try(h, typeof(InteractableEmptyBoxStorage), "StoreBox",
                prefix: new HarmonyMethod(typeof(WorldContainerInteraction), nameof(EmptyBoxStorePrefix)));

            // say so loudly rather than silently no-op the null-conditional: without this
            // field ApplyCleanserState can no longer force the counter back onto the list,
            // and the join-time drift would return unnoticed on a renamed game build
            if (FiClItemAmount == null)
            {
                CoopPlugin.Log.LogWarning(
                    "Field missing: InteractableAutoCleanser.m_ItemAmount - cleanser can count cannot self-heal");
            }
        }

        public static bool ClientContainerMutationPrefix()
        {
            return true;
        }

        private static bool ReserveContainerCommand(WorldContainerInteraction self,
            ContainerOpMessage command, Action apply, Action undo)
        {
            if (ApplyingRemote || self == null)
                return true;
            WorldPrediction.Predict(WorldPrediction.ContainersScope, command, apply, undo);
            return false;
        }

        public static bool StorageContentPrefix(InteractableCardStorageShelf __instance,
            object[] __args)
        {
            if (ApplyingRemote || __instance == null)
                return true;
            var self = Current;
            var cards = __args != null && __args.Length > 0
                ? __args[0] as List<CompactCardDataAmount> : null;
            if (self == null || cards == null)
                return false;
            var index = self.IndexOf(KindCardStorage, __instance);
            if (index < 0)
                return false;
            var command = new ContainerOpMessage
            {
                Op = OpContentSet,
                Kind = (byte)KindCardStorage,
                Index = (byte)index,
                CanWorkerTake = __instance.CanWorkerTake(),
                Cards = new List<CompactCardDataAmount>(cards),
            };
            var prior = new List<CompactCardDataAmount>(__instance.GetCompactCardDataAmountList());
            var priorCanTake = __instance.CanWorkerTake();
            var desired = new List<CompactCardDataAmount>(cards);
            var desiredCanTake = command.CanWorkerTake;
            return ReserveContainerCommand(self, command,
                () => self.ApplyContentInPlace(__instance, desired, desiredCanTake),
                () => self.ApplyContentInPlace(__instance, prior, priorCanTake));
        }

        public static bool DonationContentPrefix(InteractableBulkDonationBox __instance,
            object[] __args)
        {
            if (ApplyingRemote || __instance == null)
                return true;
            var self = Current;
            var cards = __args != null && __args.Length > 0
                ? __args[0] as List<CompactCardDataAmount> : null;
            if (self == null || cards == null)
                return false;
            var index = self.IndexOf(KindDonation, __instance);
            if (index < 0)
                return false;
            var command = new ContainerOpMessage
            {
                Op = OpContentSet,
                Kind = (byte)KindDonation,
                Index = (byte)index,
                Cards = new List<CompactCardDataAmount>(cards),
            };
            var prior = new List<CompactCardDataAmount>(__instance.GetCompactCardDataAmountList());
            var desired = new List<CompactCardDataAmount>(cards);
            return ReserveContainerCommand(self, command,
                () => self.ApplyContentInPlace(__instance, desired),
                () => self.ApplyContentInPlace(__instance, prior));
        }

        public static bool WorkerTakePrefix(InteractableCardStorageShelf __instance,
            bool canWorkerTake)
        {
            if (ApplyingRemote || __instance == null)
                return true;
            var self = Current;
            var index = self?.IndexOf(KindCardStorage, __instance) ?? -1;
            if (index < 0)
                return false;
            var prior = __instance.CanWorkerTake();
            return ReserveContainerCommand(self, new ContainerOpMessage
            {
                Op = OpWorkerTakeFlag,
                Kind = (byte)KindCardStorage,
                Index = (byte)index,
                CanWorkerTake = canWorkerTake,
            },
                () => ApplyWorkerTake(__instance, canWorkerTake),
                () => ApplyWorkerTake(__instance, prior));
        }

        public static bool CleanserTogglePrefix(InteractableAutoCleanser __instance)
        {
            if (ApplyingRemote || __instance == null)
                return true;
            var self = Current;
            var index = self?.IndexOf(KindCleanser, __instance) ?? -1;
            if (index < 0)
                return false;
            var prior = __instance.IsTurnedOn();
            var priorCooldown = FiClCooldown?.GetValue(__instance) as bool? ?? false;
            var priorTimer = FiClTimer?.GetValue(__instance) as float? ?? 0f;
            var desired = !prior;
            return ReserveContainerCommand(self, new ContainerOpMessage
            {
                Op = OpCleanserToggle,
                Kind = (byte)KindCleanser,
                Index = (byte)index,
                TurnedOn = desired,
            },
                () => ApplyCleanserToggle(__instance, desired),
                () =>
                {
                    FiClTurnedOn?.SetValue(__instance, prior);
                    FiClCooldown?.SetValue(__instance, priorCooldown);
                    FiClTimer?.SetValue(__instance, priorTimer);
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

        public static void PackOpenerChangedPostfix(InteractableAutoPackOpener __instance)
        {
            Current?.HostChanged(KindPackOpener, __instance);
        }

        public static bool PackOpenerSimulationPrefix()
        {
            if (ApplyingRemote)
            {
                return true;
            }

            // The local queue is only visual. Let vanilla Update advance its timer and
            // remove one inert item per cycle, but never let it roll a second RNG result.
            return false;
        }

        public static void CleanserChangedPostfix(InteractableAutoCleanser __instance)
        {
            Current?.HostChanged(KindCleanser, __instance);
        }

        public static bool PackOpenerClickPrefix(InteractableAutoPackOpener __instance)
        {
            Current?.ClientPackOpenerClick(__instance);
            return false;
        }

        public static bool EmptyBoxTakePrefix(InteractableEmptyBoxStorage __instance)
        {
            Current?.ClientEmptyBoxTake(__instance);
            return false;
        }

        public static bool EmptyBoxStorePrefix(InteractableEmptyBoxStorage __instance,
            InteractablePackagingBox_Item packagingBox, bool isPlayer)
        {
            if (ApplyingRemote)
            {
                return true;
            }
            Current?.ClientEmptyBoxStore(__instance, packagingBox, isPlayer);
            return false;
        }

        public static void EmptyBoxTakeChangedPostfix(InteractableEmptyBoxStorage __instance)
        {
            Current?.HostChanged(KindEmptyBoxStorage, __instance);
        }

        public static void EmptyBoxStoreChangedPostfix(InteractableEmptyBoxStorage __instance)
        {
            Current?.HostChanged(KindEmptyBoxStorage, __instance);
        }

        public static bool PackOpenerAddItemPrefix(InteractableAutoPackOpener __instance, Item item)
        {
            if (ApplyingRemote)
            {
                return true;
            }
            if (item == null)
                throw new InvalidOperationException("Pack opener received no item to insert.");
            // the guest's join world-load restores the host save via
            // InteractableAutoPackOpener.LoadData, which calls AddItem once per stored
            // pack (decompiled ~384). Those are NOT player inserts - forwarding each one
            // makes the host spawn a NEW pack it already has, duplicating every pack that
            // sat in an opener on every join/rejoin. Skip the op during the reload, same
            // as the symmetric destroy guard (card/furniture DestroyedPrefix).
            // Still retire the item (as below) so it doesn't float - the host echoes truth.
            if (Reloading)
            {
                ItemSpawnManager.DisableItem(item);
                return false;
            }

            var self = Current;
            if (self == null)
                throw new InvalidOperationException("Pack opener client command path is unavailable.");

            var idx = self.IndexOf(KindPackOpener, __instance);
            if (idx < 0)
                throw new InvalidOperationException("Pack opener has no placement identity.");

            var itemType = item.GetItemType();
            var command = new ContainerOpMessage
            {
                Op = OpPackInsert,
                Kind = (byte)KindPackOpener,
                Index = (byte)idx,
                // the host spawns a real pack prefab from this, so a modded id minted in
                // a different order here would insert the WRONG product on the host
                ItemType = (EItemType)itemType,
            };
            var mirror = self.GetOrCreatePackMirror(idx);
            var priorStored = mirror.StoredCount;
            var priorProcessing = mirror.Processing;
            var priorState = mirror.CurrentState;
            var priorStart = mirror.PackStartTimestamp;
            var priorDuration = mirror.PackDuration;
            var priorLocalStart = mirror.LocalStartTimestamp;
            WorldPrediction.Predict(WorldPrediction.ContainersScope, command,
                () =>
                {
                    mirror.StoredCount++;
                    // SyncLocalPackItems reads StoredTypes[stored.Count] while it grows the local
                    // list to StoredCount, so the optimistic increment must carry its item type or
                    // it throws IndexOutOfRange, aborting the caller before the source box gives
                    // the pack up.
                    mirror.StoredTypes.Add((int)itemType);
                    if (!mirror.Processing && mirror.StoredCount >= __instance.m_MaxPackCount)
                    {
                        mirror.Processing = true;
                        mirror.CurrentState = 1;
                        self.StartLocalPackCycle(__instance, mirror, mirror.StoredCount);
                    }
                    self.ApplyPackMirrorToMachine(__instance, mirror);
                    ItemSpawnManager.DisableItem(item);
                },
                () =>
                {
                    mirror.StoredCount = priorStored;
                    if (mirror.StoredTypes.Count > priorStored)
                    {
                        mirror.StoredTypes.RemoveRange(priorStored,
                            mirror.StoredTypes.Count - priorStored);
                    }
                    mirror.Processing = priorProcessing;
                    mirror.CurrentState = priorState;
                    mirror.PackStartTimestamp = priorStart;
                    mirror.PackDuration = priorDuration;
                    mirror.LocalStartTimestamp = priorLocalStart;
                    item.gameObject.SetActive(true);
                    self.ApplyPackMirrorToMachine(__instance, mirror);
                });
            return false;
        }

        /// <summary>Shared client block for both machines' TakeItemToHand: pulling an
        /// item back OUT client-side would hand the joiner a phantom the host still
        /// counts as inside the machine.</summary>
        public static bool TakeItemBlockPrefix(ref Item __result)
        {
            if (ApplyingRemote)
            {
                return true;
            }

            __result = null;
            return false;
        }

        public static bool CleanserAddItemPrefix(InteractableAutoCleanser __instance, Item item)
        {
            if (ApplyingRemote)
            {
                return true;
            }
            if (item == null)
                throw new InvalidOperationException("Cleanser received no refill item.");
            // same join-LoadData dupe as PackOpenerAddItemPrefix: InteractableAutoCleanser
            // .LoadData calls AddItem once per saved spray can (decompiled ~396). Forwarding
            // each as OpCleanserRefill makes the host spawn extra deodorant cans on every
            // join/rejoin, so the reload must not send an op.
            //
            // It must NOT suppress vanilla AddItem to do that, though: LoadData ends with an
            // unconditional `m_ItemAmount = saveData.itemAmount` (decompiled :398) that runs
            // whether or not the AddItem calls landed. Swallowing them left the guest with
            // m_ItemAmount = N and m_StoredItemList empty, and vanilla GetLastItem
            // (decompiled :353-357) guards on the LIST's length but subscripts with the
            // COUNTER - so every later reconcile shrink threw IndexOutOfRange, permanently,
            // for the rest of the session. Returning true lets AddItem move both together,
            // which is exactly what :398 then agrees with. Nothing is forwarded: the op is
            // written further down, past this early-out. The cans are inert local props -
            // cleanser state is collected host-side only and ApplyCleanserState reconciles
            // the guest's copy against it.
            //
            // Do NOT copy this to PackOpenerAddItemPrefix. The opener suppresses load-time
            // AddItem calls so it cannot duplicate packs, then rebuilds an inert visual queue
            // from the authoritative container record. Its client OpenPack path is blocked,
            // so the vanilla timer can render progress without running the pack RNG.
            if (Reloading)
            {
                return true;
            }

            var self = Current;
            if (self == null)
                throw new InvalidOperationException("Cleanser client command path is unavailable.");

            var idx = self.IndexOf(KindCleanser, __instance);
            if (idx < 0)
                throw new InvalidOperationException("Cleanser has no placement identity.");

            var fill = item.GetContentFill();
            var command = new ContainerOpMessage
            {
                Op = OpCleanserRefill,
                Kind = (byte)KindCleanser,
                Index = (byte)idx,
                Fill = fill,
            };
            WorldPrediction.Predict(WorldPrediction.ContainersScope, command,
                () => ItemSpawnManager.DisableItem(item),
                () => item.gameObject.SetActive(true));
            return false;
        }

        // ---------------- shared helpers ----------------

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
                        ItemSpawnManager.DisableItem(extra);
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
                        ItemSpawnManager.DisableItem(extra);
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
                    ItemSpawnManager.DisableItem(item);
                }
                throw;
            }
        }

        private static int AmountFor(List<CompactCardDataAmount> list, CompactCardDataAmount id)
        {
            if (list == null)
            {
                return 0;
            }

            for (var i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e != null && e.cardSaveIndex == id.cardSaveIndex
                    && e.expansionType == id.expansionType && e.isDestiny == id.isDestiny)
                {
                    return e.amount;
                }
            }
            return 0;
        }

        private static bool SameCards(List<CompactCardDataAmount> a, List<CompactCardDataAmount> b)
        {
            if (a == null || b == null || a.Count != b.Count)
            {
                return false;
            }

            for (var i = 0; i < a.Count; i++)
            {
                var e = a[i];
                if (e == null || AmountFor(b, e) != e.amount)
                {
                    return false;
                }
            }
            for (var i = 0; i < b.Count; i++)
            {
                var e = b[i];
                if (e == null || AmountFor(a, e) != e.amount)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(double value)
            => !double.IsNaN(value) && !double.IsInfinity(value);

        private static void Try(Harmony h, Type type, string method,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            try
            {
                var original = ReflectionSurface.RequiredMethod(type, method);
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
    }
}
