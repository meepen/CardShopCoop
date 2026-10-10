using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Grading;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Modules.World;
using CardShopCoop.Net;
using CardShopCoop.Runtime;
using HarmonyLib;

namespace CardShopCoop.Modules.Pricing
{
    /// <summary>Applies the host's latest pricing snapshot when the local content is ready.</summary>
    [ClientBehaviour]
    public sealed class PricingClientBehaviour : CoopBehaviour
    {
        private static PricingClientBehaviour _active;
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private PricingStateMessage _pendingState;
        private readonly Dictionary<EItemType, PricingItemDeltaEntry> _pendingItemDeltas = new();
        private readonly Dictionary<string, PricingCardDeltaEntry> _pendingCardDeltas = new();
        private bool _contentReady;
        private bool _applying;
        private bool _shutdown;

        // The game's price screen records the exact display compartment whose price tag was
        // clicked. Carry that slot's stable ShelfKey+compartment in the price intent so the host
        // resolves the addressed slot directly instead of searching every shelf for a content match.
        private static readonly System.Reflection.FieldInfo FiPriceScreenIsCard =
            AccessTools.Field(typeof(SetItemPriceScreen), "m_IsSetCardPrice");
        private static readonly System.Reflection.FieldInfo FiPriceScreenCardCompartment =
            AccessTools.Field(typeof(SetItemPriceScreen), "m_CurrentCardCompartment");

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
                _harmony = new Harmony("dev.meepen.cardshopcoop.pricing.client");
                PricingClientPatches.Apply(_harmony);
                CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnReady);
            }
            catch
            {
                _harmony?.UnpatchSelf();
                _harmony = null;
                if (registered)
                    _context.Messages.UnregisterAttributedHandlers(this);
                CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnReady);
                if (ReferenceEquals(_active, this))
                    _active = null;
                _context = null;
                throw;
            }
        }

        private void OnReady(CEventPlayer_GameDataFinishLoaded _)
            => ContentReady();

        [MessageHandler(typeof(PricingStateMessage))]
        private void State(MessageContext context, PricingStateMessage message)
        {
            if (_shutdown)
                return;

            _pendingState = message;
            ApplyPendingState();
        }

        internal static void ContentReady()
        {
            if (_active == null)
                return;

            _active._contentReady = true;
            _active.ApplyPendingState();
        }

        private void ApplyPendingState()
        {
            if (_shutdown || !_contentReady || !_context.InGame()
                || WorldCardInteraction.Inv() == null)
                return;

            if (_pendingState != null)
            {
                var state = _pendingState;
                ApplyState(state);
                _pendingState = null;
            }
            if (_pendingItemDeltas.Count > 0)
            {
                var deltas = new List<KeyValuePair<EItemType, PricingItemDeltaEntry>>(
                    _pendingItemDeltas);
                for (var i = 0; i < deltas.Count; i++)
                {
                    if (ApplyItemDelta(deltas[i].Value))
                        _pendingItemDeltas.Remove(deltas[i].Key);
                }
            }
            if (_pendingCardDeltas.Count > 0)
            {
                var deltas = new List<KeyValuePair<string, PricingCardDeltaEntry>>(
                    _pendingCardDeltas);
                for (var i = 0; i < deltas.Count; i++)
                {
                    if (ApplyCardDelta(deltas[i].Value))
                        _pendingCardDeltas.Remove(deltas[i].Key);
                }
            }
        }

        private void ApplyState(PricingStateMessage state)
        {
            _applying = true;
            try
            {
                for (var i = 0; i < state.ItemTypes.Count; i++)
                    CPlayerData.SetItemPrice(state.ItemTypes[i], state.ItemPrices[i]);

                for (var i = 0; i < state.Cards.Count; i++)
                {
                    var card = state.Cards[i];
                    var copy = PricingInterop.CopyCard(card, state.CardGrades[i]);
                    GradingApi.Remember(copy);
                    CPlayerData.SetCardPrice(copy, state.CardPrices[i]);
                }
            }
            finally
            {
                _applying = false;
            }
        }

        [MessageHandler(typeof(PricingDeltaBatchMessage))]
        private void HandleDeltaBatch(MessageContext context, PricingDeltaBatchMessage message)
        {
            if (_shutdown || message == null)
                return;

            // Entries are applied (or deferred) in the order the host produced them: the last
            // same-key entry is the newest absolute price, and every entry reconciles exactly the
            // prediction id it carries.
            var ready = _contentReady && _context.InGame() && WorldCardInteraction.Inv() != null;
            for (var i = 0; i < message.Items.Count; i++)
            {
                if (ready)
                    ApplyItemDelta(message.Items[i]);
                else
                    DeferItemDelta(message.Items[i]);
            }
            for (var i = 0; i < message.Cards.Count; i++)
            {
                if (ready)
                    ApplyCardDelta(message.Cards[i]);
                else
                    DeferCardDelta(message.Cards[i]);
            }
        }

        private bool ApplyItemDelta(PricingItemDeltaEntry entry)
        {
            // The batch carries the host's absolute price, so Confirm (retire our prediction, then
            // always apply the authoritative value). AckOrApply would retire our own echo without
            // applying it, so a remote same-key entry earlier in the batch could leave us showing
            // the older price while the host holds ours.
            PredictionApi.Confirm(entry.PredictionId, () =>
            {
                _applying = true;
                try
                {
                    CPlayerData.SetItemPrice(entry.ItemType, entry.Price);
                }
                finally
                {
                    _applying = false;
                }
            });
            return true;
        }

        private bool ApplyCardDelta(PricingCardDeltaEntry entry)
        {
            // Same as the item path: an absolute authoritative price that must be written even
            // when it retires one of this client's own predictions.
            PredictionApi.Confirm(entry.PredictionId, () =>
            {
                if (entry.Removed)
                    return;
                var copy = PricingInterop.CopyCard(entry.Card, entry.EncodedGrade);
                GradingApi.Remember(copy);
                _applying = true;
                try
                {
                    CPlayerData.SetCardPrice(copy, entry.Price);
                }
                finally
                {
                    _applying = false;
                }
            });
            return true;
        }

        private void DeferItemDelta(PricingItemDeltaEntry entry)
        {
            if (_pendingItemDeltas.TryGetValue(entry.ItemType, out var previous))
                PredictionApi.Ack(previous.PredictionId);
            _pendingItemDeltas[entry.ItemType] = entry;
        }

        private void DeferCardDelta(PricingCardDeltaEntry entry)
        {
            var key = PricingInterop.CardKey(entry.Card, entry.EncodedGrade) ?? "null";
            if (_pendingCardDeltas.TryGetValue(key, out var previous))
                PredictionApi.Ack(previous.PredictionId);
            _pendingCardDeltas[key] = entry;
        }

        /// <summary>Prefix snapshot: the game is about to run <c>SetItemPrice</c>, so read the
        /// pre-change value here. <see cref="float.NaN"/> means "do not record this setter"
        /// (a remote/replay apply, an invalid slot, or no active client).</summary>
        internal static float CaptureItemBefore(EItemType type)
        {
            var active = _active;
            if (active == null || active._applying || !PricingInterop.IsItemTypeValid(type))
                return float.NaN;
            return PricingInterop.ReadItem(type);
        }

        /// <summary>The game already set the price (postfix). Register one post-hoc prediction
        /// so the game keeps ownership of the local mutation and only a host rejection replays the
        /// previous value through the game's own setter.</summary>
        internal static void ObserveItemSetter(EItemType type, float price, float previous)
        {
            var active = _active;
            if (active == null || active._applying || float.IsNaN(previous)
                || !active.CanSend() || !PricingInterop.IsItemTypeValid(type)
                || !PricingInterop.ValidPrice(price))
                return;
            active.PredictItem(type, price, previous);
        }

        /// <summary>Card counterpart of <see cref="CaptureItemBefore"/>.</summary>
        internal static float CaptureCardBefore(CardData card)
        {
            var active = _active;
            if (active == null || active._applying || !PricingInterop.ValidCard(card))
                return float.NaN;
            return PricingInterop.ReadCard(card);
        }

        /// <summary>Card counterpart of <see cref="ObserveItemSetter"/>.</summary>
        internal static void ObserveCardSetter(CardData card, float price, float previous)
        {
            var active = _active;
            if (active == null || active._applying || float.IsNaN(previous) || card == null
                || !active.CanSend() || !PricingInterop.ValidCard(card)
                || !PricingInterop.ValidPrice(price))
                return;
            var compartment = CurrentCardPriceCompartment();
            if (compartment == null
                || !WorldCardDisplay.TryMakeKey(compartment, out var shelfKey, out var index))
            {
                // No stable display slot means the host cannot address the priced card. Dropping
                // the intent is correct: a content search would re-introduce the heuristic this
                // binding replaces. The display protocol drops the same shelf for the same reason.
                CoopPlugin.Log.LogWarning("[pricing] card price change had no display slot; "
                    + "not forwarded.");
                return;
            }

            var grade = GradingApi.Encoded(card);
            GradingApi.Remember(card);
            active.PredictCard(card, price, grade, previous, shelfKey, index);
        }

        /// <summary>The display compartment the local player is editing a CARD price for, or null
        /// when the open price screen is an item edit (or none is open). The game stores the exact
        /// compartment the price tag was clicked on, so this is the slot's stable identity, not a
        /// content lookup. Client workers never run their state machine (NpcClientBehaviour skips
        /// Worker.Update), so a client card price change always originates here.</summary>
        private static InteractableCardCompartment CurrentCardPriceCompartment()
        {
            var screen = SceneRef<SetItemPriceScreen>.Get();
            if (screen == null || FiPriceScreenIsCard == null
                || !(bool)FiPriceScreenIsCard.GetValue(screen))
            {
                return null;
            }

            return FiPriceScreenCardCompartment?.GetValue(screen) as InteractableCardCompartment;
        }

        private void PredictItem(EItemType type, float price, float previous)
        {
            if (!CanSend())
                return;
            PredictionApi.Predict(
                "pricing-item:" + (int)type,
                predictionId => _context.Send(1, new PricingItemIntentMessage
                {
                    PredictionId = predictionId,
                    ItemType = type,
                    Price = price,
                }),
                () => ApplyLocalItem(type, price),
                () => ApplyLocalItem(type, previous));
        }

        private void PredictCard(CardData card, float price, int grade, float previous,
            int shelfKey, int compartment)
        {
            if (!CanSend())
                return;
            var copy = PricingInterop.CopyCard(card, grade);
            var key = PricingInterop.CardKey(copy, grade);
            PredictionApi.Predict(
                "pricing-card:" + key,
                predictionId => _context.Send(1, new PricingCardIntentMessage
                {
                    PredictionId = predictionId,
                    Card = PricingInterop.CopyCard(copy, grade),
                    Price = price,
                    EncodedGrade = grade,
                    ShelfKey = shelfKey,
                    Compartment = compartment,
                }),
                () => ApplyLocalCard(copy, price),
                () => ApplyLocalCard(copy, previous));
        }

        private void ApplyLocalItem(EItemType type, float price)
        {
            _applying = true;
            try
            {
                CPlayerData.SetItemPrice(type, price);
            }
            finally
            {
                _applying = false;
            }
        }

        private void ApplyLocalCard(CardData card, float price)
        {
            GradingApi.Remember(card);
            _applying = true;
            try
            {
                CPlayerData.SetCardPrice(card, price);
            }
            finally
            {
                _applying = false;
            }
        }

        internal static void InventoryReset()
        {
            if (_active == null)
                return;
            _active._pendingState = null;
            _active.ClearPendingDeltas();
            _active._contentReady = false;
        }

        private void ClearPendingDeltas()
        {
            foreach (var delta in _pendingItemDeltas.Values)
                PredictionApi.Ack(delta.PredictionId);
            foreach (var delta in _pendingCardDeltas.Values)
                PredictionApi.Ack(delta.PredictionId);
            _pendingItemDeltas.Clear();
            _pendingCardDeltas.Clear();
        }

        private bool CanSend()
            => !_shutdown && _context?.InGame() == true;

        internal void Shutdown()
        {
            if (_shutdown)
                return;

            _shutdown = true;
            _context?.Messages.UnregisterAttributedHandlers(this);
            _harmony?.UnpatchSelf();
            _harmony = null;
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnReady);
            _pendingState = null;
            ClearPendingDeltas();
            if (ReferenceEquals(_active, this))
                _active = null;
            _context = null;
        }

        private void OnDestroy() => Shutdown();
    }
}
