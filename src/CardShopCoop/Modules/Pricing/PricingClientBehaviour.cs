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
        private readonly Dictionary<EItemType, PricingItemDeltaMessage> _pendingItemDeltas = new();
        private readonly Dictionary<string, PricingCardDeltaMessage> _pendingCardDeltas = new();
        private bool _contentReady;
        private bool _applying;
        private bool _suppressCapture;
        private bool _shutdown;

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
                _harmony = new Harmony("com.zwhit.cardshopcoop.pricing.client");
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
                var deltas = new List<KeyValuePair<EItemType, PricingItemDeltaMessage>>(
                    _pendingItemDeltas);
                for (var i = 0; i < deltas.Count; i++)
                {
                    if (ApplyItemDelta(deltas[i].Value))
                        _pendingItemDeltas.Remove(deltas[i].Key);
                }
            }
            if (_pendingCardDeltas.Count > 0)
            {
                var deltas = new List<KeyValuePair<string, PricingCardDeltaMessage>>(
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

        [MessageHandler(typeof(PricingItemDeltaMessage))]
        private void HandleItemDelta(MessageContext context, PricingItemDeltaMessage message)
        {
            if (_shutdown)
                return;
            if (!_contentReady || !_context.InGame() || WorldCardInteraction.Inv() == null)
            {
                DeferItemDelta(message);
                return;
            }
            ApplyItemDelta(message);
        }

        [MessageHandler(typeof(PricingCardDeltaMessage))]
        private void HandleCardDelta(MessageContext context, PricingCardDeltaMessage message)
        {
            if (_shutdown)
                return;
            if (!_contentReady || !_context.InGame() || WorldCardInteraction.Inv() == null)
            {
                DeferCardDelta(message);
                return;
            }
            ApplyCardDelta(message);
        }

        private bool ApplyItemDelta(PricingItemDeltaMessage message)
        {
            PredictionApi.ApplyAuthoritative(message.PredictionId, () =>
            {
                _applying = true;
                try
                {
                    CPlayerData.SetItemPrice(message.ItemType, message.Price);
                }
                finally
                {
                    _applying = false;
                }
            });
            return true;
        }

        private bool ApplyCardDelta(PricingCardDeltaMessage message)
        {
            PredictionApi.ApplyAuthoritative(message.PredictionId, () =>
            {
                if (message.Removed)
                    return;
                var copy = PricingInterop.CopyCard(message.Card, message.EncodedGrade);
                GradingApi.Remember(copy);
                _applying = true;
                try
                {
                    CPlayerData.SetCardPrice(copy, message.Price);
                }
                finally
                {
                    _applying = false;
                }
            });
            return true;
        }

        private void DeferItemDelta(PricingItemDeltaMessage message)
        {
            if (_pendingItemDeltas.TryGetValue(message.ItemType, out var previous))
                PredictionApi.ConfirmSuperseded(previous.PredictionId);
            _pendingItemDeltas[message.ItemType] = message;
        }

        private void DeferCardDelta(PricingCardDeltaMessage message)
        {
            var key = PricingInterop.CardKey(message.Card, message.EncodedGrade) ?? "null";
            if (_pendingCardDeltas.TryGetValue(key, out var previous))
                PredictionApi.ConfirmSuperseded(previous.PredictionId);
            _pendingCardDeltas[key] = message;
        }

        internal static void EmitItem(EItemType type, float price)
            => _active?.PredictItem(type, price);

        internal static void Emit(CardData card, float price, int grade)
            => _active?.PredictCard(card, price, grade);

        internal static bool Submit(SetItemPriceScreen screen)
        {
            var active = _active;
            return active == null || active._shutdown || active.SubmitLocal(screen);
        }

        private bool SubmitLocal(SetItemPriceScreen screen)
        {
            if (screen == null || !PricingInterop.TryReadConfirmPrice(screen, out var price))
                return true;
            if (!PricingInterop.ValidPrice(price))
            {
                CoopPlugin.Log.LogWarning("[pricing] confirm ignored: invalid price " + price + ".");
                return false;
            }

            var item = screen.GetCurrentSettingPriceItemType();
            var card = screen.GetCurrentSettingPriceCardData();
            if (card != null)
            {
                if (!PricingInterop.ValidCard(card))
                {
                    CoopPlugin.Log.LogWarning("[pricing] confirm ignored: invalid card saveIndex="
                        + PricingInterop.SafeSaveIndex(card) + " expansion="
                        + (int)card.expansionType + " monster=" + (int)card.monsterType
                        + " grade=" + card.cardGrade + ".");
                    return false;
                }

                var grade = GradingApi.Encoded(card);
                CoopPlugin.Log.LogInfo("[pricing] forwarding card price saveIndex="
                    + PricingInterop.SafeSaveIndex(card) + " grade=" + grade + " price=" + price
                    + ".");
                PredictCard(PricingInterop.CopyCard(card, grade), price, grade);
            }
            else
            {
                if (!PricingInterop.IsItemTypeValid(item))
                {
                    CoopPlugin.Log.LogWarning("[pricing] confirm ignored: no card and invalid item "
                        + item + ".");
                    return false;
                }

                PredictItem(item, price);
            }

            screen.CloseScreen();
            return false;
        }

        internal static bool InterceptItemSetter(EItemType type, float price,
            out bool skipCapture)
        {
            skipCapture = false;
            var active = _active;
            if (active == null || active._shutdown || !active.CanSend())
                return true;
            if (active._applying)
            {
                skipCapture = true;
                return true;
            }

            skipCapture = true;
            active._suppressCapture = true;
            try
            {
                active.PredictItem(type, price);
            }
            finally
            {
                active._suppressCapture = false;
            }
            return false;
        }

        internal static bool InterceptCardSetter(CardData card, float price,
            out bool skipCapture)
        {
            skipCapture = false;
            var active = _active;
            if (active == null || active._shutdown || !active.CanSend())
                return true;
            if (active._applying)
            {
                skipCapture = true;
                return true;
            }

            skipCapture = true;
            active._suppressCapture = true;
            try
            {
                active.PredictCard(card, price, GradingApi.Encoded(card));
            }
            finally
            {
                active._suppressCapture = false;
            }
            return false;
        }

        private void PredictItem(EItemType type, float price)
        {
            if (!CanSend())
                return;
            var previous = PricingInterop.ReadItem(type);
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

        private void PredictCard(CardData card, float price, int grade)
        {
            if (!CanSend())
                return;
            var copy = PricingInterop.CopyCard(card, grade);
            var key = PricingInterop.CardKey(copy, grade);
            var previous = PricingInterop.ReadCard(copy);
            PredictionApi.Predict(
                "pricing-card:" + key,
                predictionId => _context.Send(1, new PricingCardIntentMessage
                {
                    PredictionId = predictionId,
                    Card = PricingInterop.CopyCard(copy, grade),
                    Price = price,
                    EncodedGrade = grade,
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

        internal static void CaptureItemSetter(EItemType type, float requestedPrice)
        {
            if (_active == null || _active._applying || _active._suppressCapture)
                return;
            EmitItem(type, PricingInterop.ReadItem(type));
        }

        internal static void CaptureCardSetter(CardData card, float requestedPrice)
        {
            if (_active == null || _active._applying || _active._suppressCapture || card == null)
                return;
            var grade = GradingApi.Encoded(card);
            GradingApi.Remember(card);
            Emit(card, PricingInterop.ReadCard(card), grade);
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
                PredictionApi.ConfirmSuperseded(delta.PredictionId);
            foreach (var delta in _pendingCardDeltas.Values)
                PredictionApi.ConfirmSuperseded(delta.PredictionId);
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
