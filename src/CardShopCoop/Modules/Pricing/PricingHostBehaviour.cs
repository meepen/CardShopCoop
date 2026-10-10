using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Grading;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Modules.World;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;

namespace CardShopCoop.Modules.Pricing
{
    /// <summary>
    /// Host pricing authority. Full pricing state is a join baseline; normal changes are keyed
    /// item/card deltas.
    /// </summary>
    [ServerBehaviour]
    public sealed class PricingHostBehaviour : CoopBehaviour
    {
        private sealed class OwnedCardState
        {
            internal CardData Card;
            internal float Price;
        }

        private static PricingHostBehaviour _active;
        private readonly Dictionary<string, OwnedCardState> _cards = new(StringComparer.Ordinal);
        private readonly HashSet<int> _joined = new();
        private readonly HashSet<int> _baselinePending = new();
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _shutdown;
        private bool _applying;
        // One frame's pricing changes, flushed as capped batches in Update. Entries are appended
        // in apply order and each keeps its own prediction id.
        private readonly List<PricingItemDeltaEntry> _pendingItemDeltas = new();
        private readonly List<PricingCardDeltaEntry> _pendingCardDeltas = new();

        // Per-batch cap, mirroring WorldCardInteraction.CardDeltaBatchMax: a mass reprice is
        // split into capped chunks so one batch cannot stall a client frame (the dispatch budget
        // charges per entry) or exceed the encoded frame limit.
        internal const int PricingDeltaBatchMax = 200;

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
                return;

            _context = RuntimeContext;
            var registered = false;
            try
            {
                _context.Messages.RegisterAttributedHandlers(this);
                registered = true;
                _active = this;
                _harmony = new Harmony("dev.meepen.cardshopcoop.pricing.host");
                PricingHostPatches.Apply(_harmony);
                CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnReady);
            }
            catch
            {
                _harmony?.UnpatchSelf();
                _harmony = null;
                CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnReady);
                if (registered)
                    _context.Messages.UnregisterAttributedHandlers(this);
                if (ReferenceEquals(_active, this))
                    _active = null;
                _context = null;
                throw;
            }
        }

        [OnFullyJoined]
        private void SendJoinState(PeerConnection peer)
        {
            if (peer == null)
                return;
            _joined.Add(peer.Id);
            if (!SendBaseline(peer.Id))
                _baselinePending.Add(peer.Id);
        }

        private void OnReady(CEventPlayer_GameDataFinishLoaded _)
            => SendPendingBaselines();

        [OnClientDisconnected]
        private void ForgetPeer(PeerConnection peer, DisconnectInfo _)
        {
            if (peer == null)
                return;
            _joined.Remove(peer.Id);
            _baselinePending.Remove(peer.Id);
        }

        [MessageHandler(typeof(PricingItemIntentMessage))]
        private void Item(MessageContext context, PricingItemIntentMessage message)
        {
            if (_shutdown || context?.Connection == null || message == null
                || !PricingInterop.IsItemTypeValid(message.ItemType)
                || !PricingInterop.ValidPrice(message.Price)
                || !TrySetItem(message.ItemType, message.Price, out _))
            {
                Reject(context, message?.PredictionId ?? Guid.Empty);
                return;
            }

            QueueItemDelta(message.PredictionId, message.ItemType,
                PricingInterop.ReadItem(message.ItemType));
        }

        [MessageHandler(typeof(PricingCardIntentMessage))]
        private void Card(MessageContext context, PricingCardIntentMessage message)
        {
            if (_shutdown || context?.Connection == null || message?.Card == null
                || !PricingInterop.ValidCard(message.Card)
                || !PricingInterop.ValidPrice(message.Price) || message.EncodedGrade < 0)
            {
                CoopPlugin.Log.LogWarning("[pricing] rejected card intent: card="
                    + (message?.Card == null ? "null" : Describe(message.Card))
                    + " valid=" + (message?.Card != null && PricingInterop.ValidCard(message.Card))
                    + " price=" + (message?.Price ?? -1f) + " grade=" + (message?.EncodedGrade ?? -1)
                    + ".");
                Reject(context, message?.PredictionId ?? Guid.Empty);
                return;
            }

            var key = PricingInterop.CardKey(message.Card, message.EncodedGrade);
            if (key == null
                || !WorldCardDisplay.TryResolve(message.ShelfKey, message.Compartment,
                    out var compartment)
                || !WorldCardDisplay.Matches(compartment, message.Card, message.EncodedGrade))
            {
                CoopPlugin.Log.LogWarning("[pricing] rejected card intent: not the displayed card"
                    + " shelf=" + message.ShelfKey + " compartment=" + message.Compartment
                    + " card=" + Describe(message.Card) + " key=" + (key ?? "null") + " grade="
                    + message.EncodedGrade + ".");
                Reject(context, message.PredictionId);
                return;
            }

            var displayed = WorldCardDisplay.Read(compartment);
            var canonical = PricingInterop.CopyCard(displayed, GradingApi.Encoded(displayed));
            if (canonical == null || GradingApi.Encoded(canonical) != message.EncodedGrade
                || !TrySetCard(canonical, message.Price, out var actual))
            {
                CoopPlugin.Log.LogWarning("[pricing] rejected card intent: set failed card="
                    + Describe(message.Card) + " requestedGrade=" + message.EncodedGrade
                    + " canonicalGrade=" + (canonical == null ? -1
                        : GradingApi.Encoded(canonical)) + " requestedPrice=" + message.Price
                    + " actualPrice=" + (canonical == null ? -1f : PricingInterop.ReadCard(canonical))
                    + ".");
                Reject(context, message.PredictionId);
                return;
            }

            GradingApi.Remember(canonical);
            _cards[key] = new OwnedCardState { Card = canonical, Price = actual };
            QueueCardDelta(message.PredictionId, canonical, actual, false);
        }

        private static string Describe(CardData card)
            => card == null ? "null"
                : (int)card.expansionType + "/" + (int)card.monsterType + "/"
                + (int)card.borderType + "/foil=" + (card.isFoil ? 1 : 0)
                + "/destiny=" + (card.isDestiny ? 1 : 0)
                + "/champ=" + (card.isChampionCard ? 1 : 0)
                + "/grade=" + card.cardGrade + "/saveIndex="
                + (PricingInterop.SafeSaveIndex(card));

        internal static void CaptureItemSetter(EItemType type, float requestedPrice)
            => _active?.CaptureItemSetterLocal(type, requestedPrice);

        internal static void CaptureCardSetter(CardData card, float requestedPrice)
            => _active?.CaptureCardSetterLocal(card, requestedPrice);

        internal static void InventoryChanged(CardData card)
            => _active?.ObserveInventoryMutation(card);

        internal static void InventoryReset()
            => _active?.ResetInventory();

        internal static void GradedInventoryChanged()
        {
            // Graded ownership is updated by the normal card hooks. Never scan it from Update.
        }

        private void CaptureItemSetterLocal(EItemType type, float requestedPrice)
        {
            if (_shutdown || _applying || !PricingInterop.IsItemTypeValid(type)
                || !PricingInterop.ValidPrice(requestedPrice))
                return;
            QueueItemDelta(Guid.Empty, type, PricingInterop.ReadItem(type));
        }

        private void CaptureCardSetterLocal(CardData card, float requestedPrice)
        {
            if (_shutdown || _applying || card == null || !PricingInterop.ValidPrice(requestedPrice)
                || !PricingInterop.ValidCard(card))
                return;

            var grade = GradingApi.Encoded(card);
            var canonical = PricingInterop.CopyCard(card, grade);
            var key = PricingInterop.CardKey(canonical, grade);
            if (key == null)
                return;

            _cards[key] = new OwnedCardState
            {
                Card = canonical,
                Price = ResolveCardPrice(canonical, requestedPrice),
            };
            QueueCardDelta(Guid.Empty, canonical, _cards[key].Price, false);
        }

        private void ObserveInventoryMutation(CardData card)
        {
            if (_shutdown || card == null || !PricingInterop.ValidCard(card))
                return;

            var grade = GradingApi.Encoded(card);
            var canonical = PricingInterop.CopyCard(card, grade);
            var key = PricingInterop.CardKey(canonical, grade);
            if (key == null)
                return;

            var owned = grade > 0
                ? CPlayerData.HasGradedCardInAlbum(canonical)
                : CPlayerData.GetCardAmount(canonical) > 0;
            if (!owned)
            {
                _cards.Remove(key);
                QueueCardDelta(Guid.Empty, canonical, 0f, true);
            }
            else
            {
                var price = EffectiveCardPrice(key, canonical);
                _cards[key] = new OwnedCardState
                {
                    Card = canonical,
                    Price = price,
                };
                QueueCardDelta(Guid.Empty, canonical, price, false);
            }
        }

        private void ResetInventory()
        {
            _cards.Clear();
            // A reset means the world's pricing changed under us; anything still queued is a
            // pre-reset absolute price and must not be replayed after the reload.
            _pendingItemDeltas.Clear();
            _pendingCardDeltas.Clear();
        }

        private PricingStateMessage BuildState()
        {
            var state = new PricingStateMessage();
            // Enumerate the enum, not the raw price-list slots: EPL's minted members live outside
            // the raw list's vanilla indices and are served through its intercepted virtual list,
            // so a numeric 0..Count-1 loop would silently omit every modded pack from the baseline
            // (and on a plain vanilla machine this is simply every member).
            var itemTypes = (EItemType[])Enum.GetValues(typeof(EItemType));
            for (var i = 0; i < itemTypes.Length; i++)
            {
                var type = itemTypes[i];
                if (!PricingInterop.IsItemTypeValid(type))
                    continue;
                state.ItemTypes.Add(type);
                state.ItemPrices.Add(PricingInterop.ReadItem(type));
            }

            var keys = new List<string>(_cards.Keys);
            keys.Sort(StringComparer.Ordinal);
            for (var i = 0; i < keys.Count; i++)
            {
                var item = _cards[keys[i]];
                var card = PricingInterop.CopyCard(item.Card, GradingApi.Encoded(item.Card));
                state.Cards.Add(card);
                state.CardPrices.Add(item.Price);
                state.CardGrades.Add(GradingApi.Encoded(card));
            }
            return state;
        }

        private bool SendBaseline(int connectionId)
        {
            if (_shutdown || !_context.InGame() || PricingInterop.ItemCount == 0
                || WorldCardInteraction.Inv() == null)
                return false;
            HydrateOwnedCards();
            _context.Send(connectionId, BuildState());
            _baselinePending.Remove(connectionId);
            return true;
        }

        private void SendPendingBaselines()
        {
            foreach (var connectionId in new List<int>(_baselinePending))
            {
                if (!_joined.Contains(connectionId))
                {
                    _baselinePending.Remove(connectionId);
                    continue;
                }
                SendBaseline(connectionId);
            }
        }

        /// <summary>Queues one item price change for this frame's batch. Queuing instead of
        /// sending keeps a mass price pass to one message per frame per peer.</summary>
        private void QueueItemDelta(Guid predictionId, EItemType type, float price)
        {
            if (_shutdown || _context?.InGame() != true)
                return;
            _pendingItemDeltas.Add(new PricingItemDeltaEntry
            {
                PredictionId = predictionId,
                ItemType = type,
                Price = price,
            });
        }

        /// <summary>Queues one card price change for this frame's batch. The card is snapshotted
        /// here, while it is still the caller's live object.</summary>
        private void QueueCardDelta(Guid predictionId, CardData card, float price, bool removed)
        {
            if (_shutdown || _context?.InGame() != true)
                return;
            _pendingCardDeltas.Add(new PricingCardDeltaEntry
            {
                PredictionId = predictionId,
                Card = PricingInterop.CopyCard(card, GradingApi.Encoded(card)),
                Price = price,
                EncodedGrade = GradingApi.Encoded(card),
                Removed = removed,
            });
        }

        /// <summary>Sends everything queued this frame in capped batches, preserving entry order
        /// and per-entry prediction ids. Chunking mirrors the card batch: a mass reprice must not
        /// stall a client frame (the dispatch budget charges the batch by entry count) or exceed
        /// the encoded frame limit. A world change or shutdown drops the queue instead of
        /// replaying stale prices into the next world.</summary>
        private void FlushPendingDeltas()
        {
            if (_shutdown || _context?.InGame() != true)
            {
                _pendingItemDeltas.Clear();
                _pendingCardDeltas.Clear();
                return;
            }

            while (_pendingItemDeltas.Count > 0)
            {
                var count = Math.Min(PricingDeltaBatchMax, _pendingItemDeltas.Count);
                var chunk = _pendingItemDeltas.GetRange(0, count);
                _context.Broadcast(new PricingDeltaBatchMessage { Items = chunk });
                _pendingItemDeltas.RemoveRange(0, count);
            }

            while (_pendingCardDeltas.Count > 0)
            {
                var count = Math.Min(PricingDeltaBatchMax, _pendingCardDeltas.Count);
                var chunk = _pendingCardDeltas.GetRange(0, count);
                _context.Broadcast(new PricingDeltaBatchMessage { Cards = chunk });
                _pendingCardDeltas.RemoveRange(0, count);
            }
        }

        private void Update()
        {
            if (_pendingItemDeltas.Count > 0 || _pendingCardDeltas.Count > 0)
                FlushPendingDeltas();
        }

        private void Reject(MessageContext context, Guid predictionId)
        {
            if (predictionId != Guid.Empty && context?.Connection != null)
                PredictionApi.Rollback(_context, context.Connection.Id, predictionId);
        }

        private void HydrateOwnedCards()
        {
            for (var expansion = ECardExpansionType.Tetramon;
                expansion <= ECardExpansionType.Ascension; expansion++)
            {
                if (expansion == ECardExpansionType.FoodieGO)
                    continue;

                var dimensions = expansion == ECardExpansionType.Ghost ? 2 : 1;
                for (var dimension = 0; dimension < dimensions; dimension++)
                {
                    var isDestiny = dimension != 0;
                    var shown = InventoryBase.GetShownMonsterList(expansion);
                    var owned = CPlayerData.GetCardCollectedList(expansion, isDestiny);
                    if (shown == null || owned == null)
                        continue;

                    var amount = CPlayerData.GetCardAmountPerMonsterType(expansion);
                    for (var i = 0; i < shown.Count * amount && i < owned.Count; i++)
                    {
                        if (owned[i] > 0)
                            RememberOwnedCard(CPlayerData.GetCardData(i, expansion, isDestiny));
                    }
                }
            }

            var graded = CPlayerData.m_GradedCardInventoryList;
            if (graded == null)
                return;
            for (var i = 0; i < graded.Count; i++)
            {
                if (graded[i] != null && graded[i].amount > 0)
                    RememberOwnedCard(CPlayerData.GetGradedCardData(graded[i]));
            }
        }

        private void RememberOwnedCard(CardData card)
        {
            if (card == null || !PricingInterop.ValidCard(card))
                return;
            var grade = GradingApi.Encoded(card);
            var canonical = PricingInterop.CopyCard(card, grade);
            var key = PricingInterop.CardKey(canonical, grade);
            if (key == null)
                return;
            GradingApi.Remember(canonical);
            _cards[key] = new OwnedCardState
            {
                Card = canonical,
                Price = EffectiveCardPrice(key, canonical),
            };
        }

        /// <summary>Returns the price to cache for an owned card. The game's price store returns 0
        /// for an expansion it cannot hold (a modded card this machine ignores), so an already
        /// cached authoritative price is kept instead of being clobbered by that read-back.</summary>
        private float EffectiveCardPrice(string key, CardData card)
        {
            var stored = PricingInterop.ReadCard(card);
            if (stored <= 0f && _cards.TryGetValue(key, out var existing) && existing.Price > 0f)
                return existing.Price;
            return stored;
        }

        /// <summary>Returns the price a setter actually asked for when the store cannot hold it.
        /// Vanilla round-trips within <see cref="PricingInterop.PriceEpsilon"/>; a wider gap means
        /// the expansion's price store ignored the write, so report the requested value.</summary>
        private static float ResolveCardPrice(CardData card, float requested)
        {
            var stored = PricingInterop.ReadCard(card);
            return Math.Abs(stored - requested) <= PricingInterop.PriceEpsilon ? stored : requested;
        }

        private bool TrySetItem(EItemType type, float price, out float actual)
        {
            if (!PricingInterop.IsItemTypeValid(type))
            {
                actual = 0f;
                return false;
            }

            _applying = true;
            try
            {
                if (!PricingInterop.SetItem(type, price, out actual))
                    return false;
            }
            finally
            {
                _applying = false;
            }

            // The vanilla OnPressConfirm now runs unsuppressed on the host, so its own
            // TutorialManager.AddTaskValue(SetItemPrice) call already covers the local confirm.
            // Applying a remote intent must not advance the host's tutorial.
            return true;
        }

        private bool TrySetCard(CardData card, float price, out float actual)
        {
            _applying = true;
            try
            {
                return PricingInterop.SetCard(card, price, out actual);
            }
            finally
            {
                _applying = false;
            }
        }

        internal void Shutdown()
        {
            if (_shutdown)
                return;

            _shutdown = true;
            _context?.Messages.UnregisterAttributedHandlers(this);
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnReady);
            _harmony?.UnpatchSelf();
            _harmony = null;
            _cards.Clear();
            _joined.Clear();
            _baselinePending.Clear();
            _pendingItemDeltas.Clear();
            _pendingCardDeltas.Clear();
            if (ReferenceEquals(_active, this))
                _active = null;
            _context = null;
        }

        private void OnDestroy() => Shutdown();
    }
}
