using System;
using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Catalog;
using CardShopCoop.Modules.Grading;
using CardShopCoop.Modules.Actions;
using CardShopCoop.Modules.World;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;
using CoopBehaviour = CardShopCoop.Runtime.CoopBehaviour;

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
        private bool _shutdown;
        private bool _initialized;
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
            if (_shutdown || _initialized)
            {
                return;
            }


            try
            {
                _active = this;
                PricingHostPatches.Apply(Harmony);
                if (RuntimeContext.InGame())
                {
                    PriceTagRegistry.Bootstrap();
                }
                CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnReady);
            }
            catch
            {
                CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnReady);


                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }


                throw;
            }
            _initialized = true;
        }

        [OnFullyJoined]
        private void SendJoinState(PeerConnection peer)
        {
            if (peer == null)
            {
                return;
            }


            _joined.Add(peer.Id);
            if (!SendBaseline(peer.Id))
            {
                _baselinePending.Add(peer.Id);
            }

        }

        private void OnReady(CEventPlayer_GameDataFinishLoaded _)
        {
            PriceTagRegistry.Bootstrap();
            SendPendingBaselines();
        }

        [OnClientDisconnected]
        private void ForgetPeer(PeerConnection peer, DisconnectInfo _)
        {
            if (peer == null)
            {
                return;
            }


            _joined.Remove(peer.Id);
            _baselinePending.Remove(peer.Id);
        }

        [MessageHandler]
        private void Item(MessageContext context, PricingItemIntentMessage message)
        {
            if (_shutdown || context?.Connection == null || message == null
                || !PricingInterop.IsItemTypeValid(message.ItemType)
                || !PricingInterop.ValidPrice(message.Price)
                || !TrySetItem(message.ItemType, message.Price, out var actual))
            {
                Reject(context, message?.ActionId ?? Guid.Empty);
                return;
            }

            QueueItemDelta(message.ActionId, message.ItemType, actual);
        }

        [MessageHandler]
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
                Reject(context, message?.ActionId ?? Guid.Empty);
                return;
            }

            if (message.Shelf == null)
            {
                CoopPlugin.Log.LogWarning("[pricing] dropped card intent with no live display shelf.");
                Reject(context, message.ActionId);
                return;
            }

            var key = PricingInterop.CardKey(message.Card, message.EncodedGrade);
            if (key == null
                || !WorldCardDisplay.TryGetCompartment(message.Shelf, message.Compartment,
                    out var compartment)
                || !WorldCardDisplay.Matches(compartment, message.Card, message.EncodedGrade))
            {
                CoopPlugin.Log.LogWarning("[pricing] rejected card intent: not the displayed card"
                    + " shelf=" + message.Shelf.name + " compartment=" + message.Compartment
                    + " card=" + Describe(message.Card) + " key=" + (key ?? "null") + " grade="
                    + message.EncodedGrade + ".");
                Reject(context, message.ActionId);
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
                Reject(context, message.ActionId);
                return;
            }

            GradingApi.Remember(canonical);
            _cards[key] = new OwnedCardState { Card = canonical, Price = actual };
            QueueCardDelta(message.ActionId, canonical, actual, false);
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
            {
                return;
            }


            var actual = PricingInterop.ReadItem(type);
            PricingDisplay.SetItem(type, actual);
            QueueItemDelta(Guid.Empty, type, actual);
        }

        private void CaptureCardSetterLocal(CardData card, float requestedPrice)
        {
            if (_shutdown || _applying || card == null || !PricingInterop.ValidPrice(requestedPrice)
                || !PricingInterop.ValidCard(card))
            {
                return;
            }


            var grade = GradingApi.Encoded(card);
            var canonical = PricingInterop.CopyCard(card, grade);
            var key = PricingInterop.CardKey(canonical, grade);
            if (key == null)
            {
                return;
            }


            var price = ResolveCardPrice(canonical, requestedPrice);
            _cards[key] = new OwnedCardState
            {
                Card = canonical,
                Price = price,
            };
            PricingDisplay.SetCard(canonical, grade, price);
            QueueCardDelta(Guid.Empty, canonical, price, false);
        }

        private void ObserveInventoryMutation(CardData card)
        {
            if (_shutdown || card == null || !PricingInterop.ValidCard(card))
            {
                return;
            }


            var grade = GradingApi.Encoded(card);
            var canonical = PricingInterop.CopyCard(card, grade);
            var key = PricingInterop.CardKey(canonical, grade);
            if (key == null)
            {
                return;
            }


            var owned = grade > 0
                ? CPlayerData.HasGradedCardInAlbum(canonical)
                : CPlayerData.GetCardAmount(canonical) > 0;
            if (!owned)
            {
                _cards.Remove(key);
                PricingDisplay.RemoveCard(canonical, grade);
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
                PricingDisplay.SetCard(canonical, grade, price);
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
            PricingDisplay.Clear();
            PriceTagRegistry.RefreshAll();
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
                // EPL's prepatcher keeps every id it has ever minted. The pricing baseline must
                // describe the catalog that is actually loaded, or it ships stale ids that the
                // peer cannot store (and could collide with a different local item id).
                if (!CatalogApi.IsActiveEnumValue(EnumKind.ItemType, (int)type)
                    || !PricingInterop.IsItemTypeValid(type))
                {
                    continue;
                }


                var price = PricingInterop.ReadItem(type);
                state.ItemTypes.Add(type);
                state.ItemPrices.Add(price);
                PricingDisplay.SetItem(type, price);
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
            if (_shutdown || !RuntimeContext.InGame() || PricingInterop.ItemCount == 0
                || WorldCardInteraction.Inv() == null)
            {

                return false;
            }


            Util.MemoryProbe.Log("pricing-baseline:before conn=" + connectionId);
            HydrateOwnedCards();
            var state = BuildState();
            Util.MemoryProbe.Log("pricing-baseline:after conn=" + connectionId
                + " items=" + state.ItemTypes.Count + " cards=" + state.Cards.Count);
            RuntimeContext.Send(connectionId, state);
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
        private void QueueItemDelta(Guid actionId, EItemType type, float price)
        {
            if (_shutdown || RuntimeContext.InGame() != true)
            {
                return;
            }


            _pendingItemDeltas.Add(new PricingItemDeltaEntry
            {
                ActionId = actionId,
                ItemType = type,
                Price = price,
            });
        }

        /// <summary>Queues one card price change for this frame's batch. The card is snapshotted
        /// here, while it is still the caller's live object.</summary>
        private void QueueCardDelta(Guid actionId, CardData card, float price, bool removed)
        {
            if (_shutdown || RuntimeContext.InGame() != true)
            {
                return;
            }


            _pendingCardDeltas.Add(new PricingCardDeltaEntry
            {
                ActionId = actionId,
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
            if (_shutdown || RuntimeContext.InGame() != true)
            {
                _pendingItemDeltas.Clear();
                _pendingCardDeltas.Clear();
                return;
            }

            while (_pendingItemDeltas.Count > 0)
            {
                var count = Math.Min(PricingDeltaBatchMax, _pendingItemDeltas.Count);
                var chunk = _pendingItemDeltas.GetRange(0, count);
                RuntimeContext.Broadcast(new PricingDeltaBatchMessage { Items = chunk });
                _pendingItemDeltas.RemoveRange(0, count);
            }

            while (_pendingCardDeltas.Count > 0)
            {
                var count = Math.Min(PricingDeltaBatchMax, _pendingCardDeltas.Count);
                var chunk = _pendingCardDeltas.GetRange(0, count);
                RuntimeContext.Broadcast(new PricingDeltaBatchMessage { Cards = chunk });
                _pendingCardDeltas.RemoveRange(0, count);
            }
        }

        private void Update()
        {
            if (_pendingItemDeltas.Count > 0 || _pendingCardDeltas.Count > 0)
            {
                FlushPendingDeltas();
            }

        }

        private void Reject(MessageContext context, Guid actionId)
        {
            if (actionId != Guid.Empty && context?.Connection != null)
            {
                ActionLedger.Rollback(RuntimeContext, context.Connection.Id, actionId);
            }

        }

        private void HydrateOwnedCards()
        {
            foreach (ECardExpansionType expansion in Enum.GetValues(typeof(ECardExpansionType)))
            {
                if (expansion == ECardExpansionType.None
                    || expansion == ECardExpansionType.FoodieGO
                    || expansion == ECardExpansionType.MAX)
                {
                    continue;
                }

                var dimensions = expansion == ECardExpansionType.Ghost ? 2 : 1;
                for (var dimension = 0; dimension < dimensions; dimension++)
                {
                    var isDestiny = dimension != 0;
                    List<EMonsterType> shown;
                    List<int> owned;
                    try
                    {
                        shown = InventoryBase.GetShownMonsterList(expansion);
                        owned = CPlayerData.GetCardCollectedList(expansion, isDestiny);
                    }
                    catch
                    {
                        continue;
                    }

                    if (shown == null || owned == null)
                    {
                        continue;
                    }

                    int amount;
                    try
                    {
                        amount = CPlayerData.GetCardAmountPerMonsterType(expansion);
                    }
                    catch
                    {
                        amount = 12;
                    }

                    for (var i = 0; i < shown.Count * amount && i < owned.Count; i++)
                    {
                        if (owned[i] > 0)
                        {
                            try
                            {
                                RememberOwnedCard(CPlayerData.GetCardData(i, expansion, isDestiny));
                            }
                            catch
                            {
                            }
                        }
                    }
                }
            }

            var graded = CPlayerData.m_GradedCardInventoryList;
            if (graded != null)
            {
                for (var i = 0; i < graded.Count; i++)
                {
                    if (graded[i] != null && graded[i].amount > 0)
                    {
                        RememberOwnedCard(CPlayerData.GetGradedCardData(graded[i]));
                    }
                }
            }

            try
            {
                var mgr = CSingleton<ShelfManager>.Instance ?? ShelfManager.m_Instance;
                if (mgr == null)
                {
                    return;
                }

                if (mgr.m_CardShelfList != null)
                {
                    for (var i = 0; i < mgr.m_CardShelfList.Count; i++)
                    {
                        var compartments = mgr.m_CardShelfList[i]?.GetCardCompartmentList();
                        if (compartments == null)
                        {
                            continue;
                        }

                        for (var j = 0; j < compartments.Count; j++)
                        {
                            var card = WorldCardDisplay.Read(compartments[j]);
                            if (card != null && card.monsterType != EMonsterType.None)
                            {
                                RememberOwnedCard(card);
                            }
                        }
                    }
                }

                if (mgr.m_CardItemCombiShelfList != null)
                {
                    for (var i = 0; i < mgr.m_CardItemCombiShelfList.Count; i++)
                    {
                        var compartments = mgr.m_CardItemCombiShelfList[i]?.GetCardCompartmentList();
                        if (compartments == null)
                        {
                            continue;
                        }

                        for (var j = 0; j < compartments.Count; j++)
                        {
                            var card = WorldCardDisplay.Read(compartments[j]);
                            if (card != null && card.monsterType != EMonsterType.None)
                            {
                                RememberOwnedCard(card);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                CoopLog.Swallow(ex);
            }
        }

        private void RememberOwnedCard(CardData card)
        {
            if (card == null || !PricingInterop.ValidCard(card))
            {
                return;
            }


            var grade = GradingApi.Encoded(card);
            var canonical = PricingInterop.CopyCard(card, grade);
            var key = PricingInterop.CardKey(canonical, grade);
            if (key == null)
            {
                return;
            }


            GradingApi.Remember(canonical);
            var price = EffectiveCardPrice(key, canonical);
            _cards[key] = new OwnedCardState
            {
                Card = canonical,
                Price = price,
            };
            PricingDisplay.SetCard(canonical, grade, price);
        }

        /// <summary>Returns the price to cache for an owned card. The game's price store returns 0
        /// for an expansion it cannot hold (a modded card this machine ignores), so an already
        /// cached authoritative price is kept instead of being clobbered by that read-back.</summary>
        private float EffectiveCardPrice(string key, CardData card)
        {
            var stored = PricingInterop.ReadCard(card);
            if (stored <= 0f && _cards.TryGetValue(key, out var existing) && existing.Price > 0f)
            {

                return existing.Price;
            }


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
                {

                    return false;
                }

            }
            finally
            {
                _applying = false;
            }

            PricingDisplay.SetItem(type, actual);
            // The vanilla OnPressConfirm now runs unsuppressed on the host, so its own
            // TutorialManager.AddTaskValue(SetItemPrice) call already covers the local confirm.
            // Applying a remote intent must not advance the host's tutorial.
            return true;
        }

        private bool TrySetCard(CardData card, float price, out float actual)
        {
            bool accepted;
            _applying = true;
            try
            {
                accepted = PricingInterop.SetCard(card, price, out actual);
            }
            finally
            {
                _applying = false;
            }

            if (accepted)
            {
                PricingDisplay.SetCard(card, GradingApi.Encoded(card), actual);
            }

            return accepted;
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }


            _shutdown = true;
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnReady);
            _cards.Clear();
            _joined.Clear();
            _baselinePending.Clear();
            _pendingItemDeltas.Clear();
            _pendingCardDeltas.Clear();
            PricingDisplay.Clear();
            PriceTagRegistry.RefreshAll();
            PriceTagRegistry.Clear();
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }


        }

        private void OnDestroy() => Shutdown();
    }
}
