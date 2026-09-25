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
                _harmony = new Harmony("com.zwhit.cardshopcoop.pricing.host");
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

            BroadcastItemDelta(message.PredictionId, message.ItemType,
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
            if (key == null || !WorldCardInteraction.TryGetDisplayedCard(message.Card,
                message.EncodedGrade, out var displayed))
            {
                CoopPlugin.Log.LogWarning("[pricing] rejected card intent: not displayed card="
                    + Describe(message.Card) + " key=" + (key ?? "null") + " grade="
                    + message.EncodedGrade + ".");
                Reject(context, message.PredictionId);
                return;
            }

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
            BroadcastCardDelta(message.PredictionId, canonical, actual, false);
        }

        private static string Describe(CardData card)
            => card == null ? "null"
                : (int)card.expansionType + "/" + (int)card.monsterType + "/"
                + (int)card.borderType + "/foil=" + (card.isFoil ? 1 : 0)
                + "/destiny=" + (card.isDestiny ? 1 : 0)
                + "/champ=" + (card.isChampionCard ? 1 : 0)
                + "/grade=" + card.cardGrade + "/saveIndex="
                + (PricingInterop.SafeSaveIndex(card));

        internal static bool Submit(SetItemPriceScreen screen)
        {
            var active = _active;
            return active == null || active._shutdown || active.SubmitLocal(screen);
        }

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

        private bool SubmitLocal(SetItemPriceScreen screen)
        {
            if (screen == null || !PricingInterop.TryReadConfirmPrice(screen, out var price))
                return true;

            if (!PricingInterop.ValidPrice(price))
            {
                CoopPlugin.Log.LogWarning("[pricing] host confirm rejected: invalid price " + price + ".");
                screen.CloseScreen();
                return false;
            }

            var item = screen.GetCurrentSettingPriceItemType();
            var card = screen.GetCurrentSettingPriceCardData();
            if (card != null)
            {
                if (!PricingInterop.ValidCard(card))
                {
                    // A card this build's pricing store cannot name (typically a modded
                    // expansion). Do not hijack the confirm: let the game's own OnPressConfirm
                    // apply it locally, exactly as it would without this mod, instead of leaving
                    // the player stuck in the menu with no price set.
                    CoopPlugin.Log.LogWarning("[pricing] host confirm: card is outside the local "
                        + "pricing store (" + Describe(card)
                        + "); deferring to the game's own confirm.");
                    return true;
                }

                var grade = GradingApi.Encoded(card);
                CoopPlugin.Log.LogInfo("[pricing] host confirm card saveIndex="
                    + PricingInterop.SafeSaveIndex(card) + " grade=" + grade + " price=" + price + ".");
                AuthorizeFromLocal(PricingInterop.CopyCard(card, grade), price, grade);
            }
            else
            {
                if (!PricingInterop.IsItemTypeValid(item))
                {
                    // Same fallback for an item slot our wire/store model cannot name.
                    CoopPlugin.Log.LogWarning("[pricing] host confirm: item " + item
                        + " is outside the local pricing store; deferring to the game's own confirm.");
                    return true;
                }

                CoopPlugin.Log.LogInfo("[pricing] host confirm item=" + item + " price=" + price + ".");
                AuthorizeFromLocal(item, price);
            }

            screen.CloseScreen();
            return false;
        }

        private void AuthorizeFromLocal(EItemType type, float price)
        {
            if (_shutdown || !PricingInterop.IsItemTypeValid(type)
                || !PricingInterop.ValidPrice(price) || !TrySetItem(type, price, out _))
                return;
            BroadcastItemDelta(Guid.Empty, type, PricingInterop.ReadItem(type));
        }

        private void AuthorizeFromLocal(CardData card, float price, int grade)
        {
            if (_shutdown || card == null || !PricingInterop.ValidCard(card)
                || !PricingInterop.ValidPrice(price))
                return;

            var canonical = PricingInterop.CopyCard(card, grade);
            var key = PricingInterop.CardKey(canonical, grade);
            if (key == null || !TrySetCard(canonical, price, out var actual))
                return;

            GradingApi.Remember(canonical);
            _cards[key] = new OwnedCardState { Card = canonical, Price = actual };
            BroadcastCardDelta(Guid.Empty, canonical, actual, false);
        }

        private void CaptureItemSetterLocal(EItemType type, float requestedPrice)
        {
            if (_shutdown || _applying || !PricingInterop.IsItemTypeValid(type)
                || !PricingInterop.ValidPrice(requestedPrice))
                return;
            BroadcastItemDelta(Guid.Empty, type, PricingInterop.ReadItem(type));
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
            BroadcastCardDelta(Guid.Empty, canonical, _cards[key].Price, false);
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
                BroadcastCardDelta(Guid.Empty, canonical, 0f, true);
            }
            else
            {
                var price = EffectiveCardPrice(key, canonical);
                _cards[key] = new OwnedCardState
                {
                    Card = canonical,
                    Price = price,
                };
                BroadcastCardDelta(Guid.Empty, canonical, price, false);
            }
        }

        private void ResetInventory()
        {
            _cards.Clear();
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

        private void BroadcastItemDelta(Guid predictionId, EItemType type, float price)
        {
            if (!_shutdown && _context?.InGame() == true)
                _context.Broadcast(new PricingItemDeltaMessage
                {
                    PredictionId = predictionId,
                    ItemType = type,
                    Price = price,
                });
        }

        private void BroadcastCardDelta(Guid predictionId, CardData card, float price, bool removed)
        {
            if (!_shutdown && _context?.InGame() == true)
                _context.Broadcast(new PricingCardDeltaMessage
                {
                    PredictionId = predictionId,
                    Card = PricingInterop.CopyCard(card, GradingApi.Encoded(card)),
                    Price = price,
                    EncodedGrade = GradingApi.Encoded(card),
                    Removed = removed,
                });
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

            var previous = PricingInterop.ReadItem(type);
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

            // This module prefix-suppresses SetItemPriceScreen.OnPressConfirm, which was the only
            // caller of TutorialManager.AddTaskValue(SetItemPrice). Mirror that host-owned effect
            // here, on the authority, whenever a confirmed price actually changes - exactly like
            // the vanilla OnPressConfirm does. The tutorial postfix then broadcasts the delta.
            if (Math.Abs(actual - previous) > PricingInterop.PriceEpsilon)
                TutorialManager.AddTaskValue(ETutorialTaskCondition.SetItemPrice, 1f);
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
            if (ReferenceEquals(_active, this))
                _active = null;
            _context = null;
        }

        private void OnDestroy() => Shutdown();
    }
}
