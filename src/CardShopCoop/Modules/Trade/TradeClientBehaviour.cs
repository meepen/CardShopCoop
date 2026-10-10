using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Modules.Hud;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Modules.World;
using CardShopCoop.Net;
using CardShopCoop.Modules.Npc;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.Modules.Trade
{
    /// <summary>Guest interaction and presentation for host-owned customer offers. This class
    /// never calls a trade screen method that changes cards or money.</summary>
    [ClientBehaviour]
    public sealed class TradeClientBehaviour : CoopBehaviour
    {
        private sealed class LocalOffer
        {
            public TradeOfferState State;
            public Customer Carrier;
            public Renderer[] MaskedRenderers;
            public bool[] RendererStates;
            public bool RemovedByHost;
        }

        private sealed class PredictionFrame
        {
            public TradeIntentOperation Operation;
            public TradeOfferState State;
            public bool ClaimAccepted;
            public bool TerminalSent;
            public bool ThinkingSent;
            public bool AcceptedShown;
            public bool ScreenOpen;
            public AcceptEffect Effect;
        }

        /// <summary>Pre-action state carried from a capture-only prefix (before vanilla mutates the
        /// offer) to the postfix that registers the one post-hoc prediction.</summary>
        private sealed class IntentCapture
        {
            public TradeIntentOperation Operation;
            public float Price;
            public PredictionFrame Frame;
            public TradeOfferState State;
            public CustomerTradeData AcceptBefore;

            /// <summary>The card-forwarding guard opened for the duration of the vanilla accept,
            /// disposed from the Harmony finalizer.</summary>
            public IDisposable CardForwarding;

            /// <summary>True while the wallet EconomyActionScope for this accept is entered.</summary>
            public bool EconomyScoped;
        }

        /// <summary>The card/coin movement a completed accept produced, captured before vanilla
        /// so the trade prediction can replay (<see cref="ApplyPrediction"/>) and reverse
        /// (<see cref="UndoPrediction"/>) it. Null for a kept or walked-away outcome.</summary>
        private sealed class AcceptEffect
        {
            public CardData Received;
            public CardData Given;
            public float CoinSpent;
        }

        /// <summary>The post-action result of the actor's own vanilla accept: the outcome and, for
        /// a kept offer, the resulting state the host must record and rebroadcast.</summary>
        private sealed class AcceptResult
        {
            public TradeOutcome Outcome;
            public TradeOfferState State;
        }

        private const string PredictionScope = "trade";

        private static TradeClientBehaviour _active;
        private readonly Dictionary<byte, LocalOffer> _offers = new();
        private readonly Dictionary<ushort, byte> _carrierKeysByCustomer = new();
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private TradeOfferBaselineMessage _pendingBaseline;
        private readonly Dictionary<byte, TradeOfferDeltaMessage> _pendingDeltas = new();
        private bool _applyingPending;
        private int _pendingCounter = -1;
        private bool _claimAccepted;
        private bool _awaitingPrediction;
        private bool _terminalSent;
        private bool _thinkingSent;
        private bool _acceptedShown;
        private bool _stopHandled;
        private bool _ignoreScreenClose;
        private bool _shutdown;

        private void OnEnable()
        {
            if (_harmony != null)
            {
                return;
            }

            _context = RuntimeContext;
            _active = this;
            _context.Messages.RegisterAttributedHandlers(this);
            _harmony = new Harmony("dev.meepen.cardshopcoop.trade.client");
            _harmony.CreateClassProcessor(typeof(CustomerPressPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(CustomerStopPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(AcceptPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(DeclinePatch)).Patch();
            _harmony.CreateClassProcessor(typeof(ThinkPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(ScreenClosePatch)).Patch();
            _harmony.CreateClassProcessor(typeof(CustomerLifecyclePatch)).Patch();
            _harmony.CreateClassProcessor(typeof(TradeScreenStartPatch)).Patch();
            NpcClientBehaviour.ExistingCustomerPuppetReady += OnExistingCustomerPuppetReady;
            NpcClientBehaviour.CustomerManagerReady += OnReadinessSignal;
            NpcClientBehaviour.CustomerPoolChanged += OnReadinessSignal;
            NpcClientBehaviour.CustomerCapacityChanged += OnCustomerCapacityChanged;
            NpcClientBehaviour.ExistingCustomerChanged += OnExistingCustomerChanged;
            SceneManager.sceneLoaded += OnSceneLoaded;
            OnReadinessSignal();
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            NpcClientBehaviour.ExistingCustomerPuppetReady -= OnExistingCustomerPuppetReady;
            NpcClientBehaviour.CustomerManagerReady -= OnReadinessSignal;
            NpcClientBehaviour.CustomerPoolChanged -= OnReadinessSignal;
            NpcClientBehaviour.CustomerCapacityChanged -= OnCustomerCapacityChanged;
            NpcClientBehaviour.ExistingCustomerChanged -= OnExistingCustomerChanged;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            ClearForSceneChange();
            _context?.Messages.UnregisterAttributedHandlers(this);
            _harmony?.UnpatchSelf();
            _harmony = null;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }
        }

        private void OnDestroy() => Shutdown();

        private void ClearPendingDeltas()
        {
            foreach (var delta in _pendingDeltas.Values)
                PredictionApi.Ack(delta.PredictionId);
            _pendingDeltas.Clear();
        }

        private void ClearForSceneChange()
        {
            var screen = TradeInterop.Screen;
            if (_pendingCounter >= 0 && screen != null && TradeInterop.IsScreenOpen(screen))
            {
                CloseRemoteScreen();
            }

            foreach (var offer in _offers.Values)
            {
                ReleaseCarrier(offer);
            }

            _offers.Clear();
            _carrierKeysByCustomer.Clear();
            _pendingBaseline = null;
            ClearPendingDeltas();
            ResetSession();
            _stopHandled = true;
        }

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            if (!_shutdown && ReferenceEquals(_active, this))
            {
                ClearForSceneChange();
            }
        }

        [MessageHandler(typeof(TradeOfferBaselineMessage))]
        private void HandleBaseline(MessageContext context, TradeOfferBaselineMessage message)
        {
            _pendingBaseline = message;
            ApplyPendingMessages();
        }

        [MessageHandler(typeof(TradeOfferDeltaMessage))]
        private void HandleDelta(MessageContext context, TradeOfferDeltaMessage message)
        {
            CoopPlugin.Log.LogInfo("[trade] client offer delta counter=" + message.Counter
                + " removed=" + message.Removed + " ready=" + IsTradeSceneReady() + ".");
            if (!_context.InGame() || !IsTradeSceneReady())
            {
                DeferDelta(message);
                return;
            }

            ApplyOfferDelta(message);
        }

        private void ApplyPendingMessages()
        {
            if (_applyingPending)
            {
                // Applying the baseline can synchronously raise the Npc customer
                // PoolChanged/ExistingCustomerChanged events (PrepareCarrier ->
                // AttachExistingCustomer) and this module subscribes to both. Re-entering here
                // re-applied the still-pending baseline and recursed without bound.
                CoopPlugin.Log.LogDebug("[trade] re-entrant offer apply suppressed");
                return;
            }

            if (!_context.InGame() || !IsTradeSceneReady())
            {
                if (_pendingBaseline != null || _pendingDeltas.Count > 0)
                {
                    CoopPlugin.Log.LogInfo("[trade] offers waiting: inGame=" + _context.InGame()
                        + " manager=" + (TradeInterop.Manager != null)
                        + " customers=" + (TradeInterop.Customers != null)
                        + " screen=" + (TradeInterop.Screen != null)
                        + " pending=" + _pendingDeltas.Count + " baseline="
                        + (_pendingBaseline != null) + ".");
                }

                return;
            }

            _applyingPending = true;
            try
            {
                if (_pendingBaseline != null)
                {
                    // Clear before applying so a synchronous re-entry can never observe a
                    // still-pending baseline.
                    var baseline = _pendingBaseline;
                    _pendingBaseline = null;
                    ApplyBaseline(baseline);
                }

                if (_pendingDeltas.Count == 0)
                {
                    return;
                }

                var deltas = new List<TradeOfferDeltaMessage>(_pendingDeltas.Values);
                _pendingDeltas.Clear();
                for (var i = 0; i < deltas.Count; i++)
                {
                    ApplyOfferDelta(deltas[i]);
                }
            }
            finally
            {
                _applyingPending = false;
            }
        }

        private void DeferDelta(TradeOfferDeltaMessage message)
        {
            if (_pendingDeltas.TryGetValue(message.Counter, out var previous))
                PredictionApi.Ack(previous.PredictionId);
            _pendingDeltas[message.Counter] = message;
        }

        private void ApplyBaseline(TradeOfferBaselineMessage message)
        {
            var seen = new HashSet<byte>();
            for (var i = 0; i < message.Offers.Count; i++)
            {
                var snapshot = message.Offers[i];
                seen.Add(snapshot.Counter);
                ApplyOfferState(snapshot);
            }

            var stale = new List<byte>();
            foreach (var pair in _offers)
            {
                if (seen.Contains(pair.Key))
                {
                    continue;
                }

                if (_pendingCounter == pair.Key && (_awaitingPrediction || _acceptedShown))
                {
                    pair.Value.RemovedByHost = true;
                    RemoveCarrierKey(pair.Value.State, pair.Key);
                    continue;
                }

                if (_pendingCounter == pair.Key)
                {
                    CloseRemoteScreen();
                }

                RemoveCarrierKey(pair.Value.State, pair.Key);
                ReleaseCarrier(pair.Value);
                stale.Add(pair.Key);
            }

            for (var i = 0; i < stale.Count; i++)
            {
                _offers.Remove(stale[i]);
            }
        }

        private void ApplyOfferState(TradeOfferState snapshot)
        {
            CoopPlugin.Log.LogInfo("[trade] client apply offer counter=" + snapshot.Counter
                + " index=" + snapshot.CustomerIndex + " gen=" + snapshot.CustomerGeneration
                + " nonce=" + snapshot.OfferNonce + " ask=" + snapshot.Price.ToString("F2")
                + " priceSet=" + snapshot.PriceSet.ToString("F2") + " trading=" + snapshot.Trading
                + ".");
            if (!_offers.TryGetValue(snapshot.Counter, out var offer))
            {
                offer = new LocalOffer();
                _offers.Add(snapshot.Counter, offer);
            }

            if (offer.State != null && (offer.State.OfferNonce != snapshot.OfferNonce
                || offer.State.CustomerGeneration != snapshot.CustomerGeneration
                || offer.State.CustomerIndex != snapshot.CustomerIndex))
            {
                RemoveCarrierKey(offer.State, snapshot.Counter);
                if (_pendingCounter == snapshot.Counter)
                {
                    CloseRemoteScreen();
                }

                ReleaseCarrier(offer);
            }

            offer.State = snapshot;
            offer.RemovedByHost = false;
            _carrierKeysByCustomer[snapshot.CustomerIndex] = snapshot.Counter;
            RefreshCarrierData(offer);
            PrepareCarrier(offer);
        }

        private void ApplyOfferDelta(TradeOfferDeltaMessage message)
        {
            // Confirm retires the actor's own pending prediction AND always folds the host's
            // authoritative delta: the delta must run even when it confirms our own prediction, or
            // _awaitingPrediction (and the terminal flags) would stay set for a kept outcome and
            // refuse the next Accept/Decline/Think locally. A remote or host-local delta takes the
            // same apply path.
            PredictionApi.Confirm(message.PredictionId,
                () => ApplyOfferDeltaAuthoritative(message));
        }

        private void ApplyOfferDeltaAuthoritative(TradeOfferDeltaMessage message)
        {
            if (message.Removed)
            {
                if (!_offers.TryGetValue(message.Counter, out var removedOffer))
                {
                    return;
                }

                removedOffer.RemovedByHost = true;
                if (_pendingCounter == message.Counter)
                {
                    _awaitingPrediction = false;
                    _claimAccepted = false;
                    _terminalSent = true;
                    if (message.Outcome == TradeOutcome.Accepted)
                    {
                        _acceptedShown = true;
                        TradeInterop.ShowAccepted(TradeInterop.Screen);
                    }
                    else
                    {
                        CloseRemoteScreen();
                    }
                }
                else
                {
                    RemoveCarrierKey(removedOffer.State, message.Counter);
                    ReleaseCarrier(removedOffer);
                    _offers.Remove(message.Counter);
                }

                return;
            }

            if (message.Offer == null)
            {
                return;
            }

            ApplyOfferState(message.Offer);
            if (!_offers.TryGetValue(message.Counter, out var offer)
                || _pendingCounter != message.Counter)
            {
                return;
            }

            switch (message.Outcome)
            {
                case TradeOutcome.Thinking:
                    _claimAccepted = false;
                    _thinkingSent = true;
                    CloseRemoteScreen();
                    break;
                case TradeOutcome.Haggle:
                case TradeOutcome.Refused:
                    // The offer is kept, so the actor's optimistic terminal flags must be cleared
                    // along with _awaitingPrediction or the next Decline (which checks
                    // _terminalSent) stays blocked. These are already false for a remote client.
                    _awaitingPrediction = false;
                    _terminalSent = false;
                    _acceptedShown = false;
                    TradeInterop.OpenData(offer.Carrier, TradeInterop.DataFromState(offer.State));
                    break;
                case TradeOutcome.None:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(message.Outcome), message.Outcome,
                        "Unknown authoritative trade outcome.");
            }
        }

        private void PrepareCarrier(LocalOffer offer)
        {
            if (offer?.State == null || offer.Carrier != null)
            {
                return;
            }

            var customers = TradeInterop.Customers;
            if (customers == null || offer.State.CustomerIndex >= customers.Count)
            {
                CoopPlugin.Log.LogWarning("[trade] carrier slot missing for offer id="
                    + offer.State.Counter + " index=" + offer.State.CustomerIndex + " count="
                    + (customers == null ? -1 : customers.Count) + ".");
                return;
            }

            var carrier = customers[offer.State.CustomerIndex];
            if (carrier == null)
            {
                CoopPlugin.Log.LogWarning("[trade] carrier is null for offer id="
                    + offer.State.Counter + " index=" + offer.State.CustomerIndex + ".");
                return;
            }

            if (NpcClientBehaviour.IsExistingCustomer(carrier))
            {
                CoopPlugin.Log.LogWarning("[trade] carrier index=" + offer.State.CustomerIndex
                    + " offer id=" + offer.State.Counter
                    + " is already an existing customer; not preparing a trade carrier for it.");
                return;
            }

            var data = TradeInterop.DataFromState(offer.State);

            var renderers = carrier.GetComponentsInChildren<Renderer>(true);
            var states = new bool[renderers.Length];
            for (var i = 0; i < renderers.Length; i++)
            {
                states[i] = renderers[i] != null && renderers[i].enabled;
            }

            carrier.transform.position = offer.State.Position;
            carrier.transform.rotation = Quaternion.Euler(0f, offer.State.Yaw, 0f);
            TradeInterop.SetStoredData(carrier, data);
            NpcClientBehaviour.SuppressedCustomer.Add(offer.State.CustomerIndex);
            NpcClientBehaviour.AttachExistingCustomer(offer.State.CustomerIndex,
                offer.State.CustomerGeneration, carrier, keepPuppetVisible: true);
            carrier.gameObject.SetActive(true);

            carrier.m_ExclaimationMesh?.SetActive(true);
            carrier.m_InteractCollider?.SetActive(true);
            offer.Carrier = carrier;
            offer.MaskedRenderers = renderers;
            offer.RendererStates = states;
            UpdateCarrierVisibility(offer);
            CoopPlugin.Log.LogInfo("[trade] prepared carrier id=" + offer.State.Counter + " index="
                + offer.State.CustomerIndex + " name=" + carrier.name + " active="
                + carrier.gameObject.activeSelf + " collider="
                + (carrier.m_InteractCollider != null && carrier.m_InteractCollider.activeSelf)
                + ".");
        }

        private static void RestoreRenderers(Renderer[] renderers, bool[] states)
        {
            for (var i = 0; renderers != null && i < renderers.Length && i < states.Length; i++)
            {
                if (renderers[i] != null)
                {
                    renderers[i].enabled = states[i];
                }
            }
        }

        private void RefreshCarrierData(LocalOffer offer)
        {
            if (offer?.Carrier != null && offer.State != null)
            {
                TradeInterop.SetStoredData(offer.Carrier, TradeInterop.DataFromState(offer.State));
            }
        }

        private void UpdateCarrierVisibility(LocalOffer offer)
        {
            if (offer?.Carrier == null || offer.State == null || offer.MaskedRenderers == null)
            {
                return;
            }

            var puppetReady = NpcClientBehaviour.IsExistingCustomerPuppetReady(
                offer.State.CustomerIndex, offer.State.CustomerGeneration);
            for (var i = 0; i < offer.MaskedRenderers.Length; i++)
            {
                if (offer.MaskedRenderers[i] != null)
                {
                    offer.MaskedRenderers[i].enabled = !puppetReady && offer.RendererStates[i];
                }
            }
        }

        private void ReleaseCarrier(LocalOffer offer)
        {
            if (offer == null)
            {
                return;
            }

            var carrier = offer.Carrier;
            var renderers = offer.MaskedRenderers;
            var states = offer.RendererStates;
            offer.Carrier = null;
            offer.MaskedRenderers = null;
            offer.RendererStates = null;
            RestoreRenderers(renderers, states);

            if (carrier == null)
            {
                return;
            }

            if (offer.State != null)
            {
                NpcClientBehaviour.DetachExistingCustomer(offer.State.CustomerIndex, carrier);
            }

            TradeInterop.SetStoredData(carrier, null);
            if (carrier.gameObject != null)
            {
                carrier.gameObject.SetActive(false);
            }
        }

        private void OnExistingCustomerPuppetReady(int index, int generation, Customer customer)
        {
            if (_carrierKeysByCustomer.TryGetValue((ushort)index, out var counter)
                && _offers.TryGetValue(counter, out var offer))
            {
                UpdateCarrierVisibility(offer);
            }
        }

        private void OnCustomerCapacityChanged(int index)
        {
            PrepareOfferForCustomer(index);
            ApplyPendingMessages();
        }

        private void OnExistingCustomerChanged(int index, int generation, Customer customer)
        {
            PrepareOfferForCustomer(index);
            ApplyPendingMessages();
        }

        private void PrepareOfferForCustomer(int index)
        {
            if (_carrierKeysByCustomer.TryGetValue((ushort)index, out var counter)
                && _offers.TryGetValue(counter, out var offer))
            {
                PrepareCarrier(offer);
            }
        }

        private void RemoveCarrierKey(TradeOfferState snapshot, byte counter)
        {
            if (snapshot != null && _carrierKeysByCustomer.TryGetValue(snapshot.CustomerIndex,
                out var mappedCounter) && mappedCounter == counter)
            {
                _carrierKeysByCustomer.Remove(snapshot.CustomerIndex);
            }
        }

        private bool IsTradeSceneReady()
        {
            return TradeInterop.Manager != null && TradeInterop.Customers != null
                && TradeInterop.Screen != null;
        }

        private void OnReadinessSignal()
        {
            if (!_shutdown && _context.InGame() && IsTradeSceneReady())
            {
                ApplyPendingMessages();
            }
        }

        [MessageHandler(typeof(TradeSessionMessage))]
        private void HandleSession(MessageContext context, TradeSessionMessage message)
        {
            CoopPlugin.Log.LogInfo("[trade] client session counter=" + message.Counter + " index="
                + message.CustomerIndex + " gen=" + message.CustomerGeneration + " nonce="
                + message.OfferNonce + " accepted=" + message.Accepted + " id=" + message.PredictionId
                + ".");
            // AckOrApply: the only state this folds is _claimAccepted, which BeginSession already
            // set optimistically for the actor (and the host only ever sends Accepted=true, with
            // refusals arriving as a separate rollback), so retiring without replaying is correct.
            // A remote peer's session has no pending id and still applies the claim.
            PredictionApi.AckOrApply(message.PredictionId, () =>
            {
                if (message.Accepted)
                {
                    _claimAccepted = true;
                }
            });
        }

        private LocalOffer PendingOffer()
        {
            return _pendingCounter >= 0 && _offers.TryGetValue((byte)_pendingCounter, out var offer)
                && offer.Carrier != null ? offer : null;
        }

        private bool IsCarrier(Customer customer, out LocalOffer offer)
        {
            foreach (var pair in _offers)
            {
                if (ReferenceEquals(pair.Value.Carrier, customer))
                {
                    offer = pair.Value;
                    return true;
                }
            }

            offer = null;
            return false;
        }

        private void BeginSession(LocalOffer offer)
        {
            _pendingCounter = offer.State.Counter;
            _claimAccepted = false;
            _awaitingPrediction = false;
            _terminalSent = false;
            _thinkingSent = false;
            _acceptedShown = false;
            _stopHandled = false;
            // OnMousePress already opened the screen, so the game owns the open: register one
            // post-hoc prediction for it and claim the session optimistically on top of the game
            // change (the claim is client bookkeeping, not a game mutation).
            if (PredictIntentObserved(TradeIntentOperation.Open, 0f))
            {
                _claimAccepted = true;
            }
        }

        /// <summary>Records one trade intent the game already performed (Open), so the prediction
        /// only stores the replay/undo closures and never mutates anything now.</summary>
        private bool PredictIntentObserved(TradeIntentOperation operation, float price)
            => RegisterIntent(operation, price, PredictionApi.Predict);

        private bool RegisterIntent(TradeIntentOperation operation, float price,
            Func<string, Action<Guid>, Action, Action, Guid> register)
        {
            var offer = PendingOffer();
            if (!_context.InGame() || offer?.State == null || _pendingCounter < 0)
            {
                return false;
            }

            if (offer.State.Trading && (operation == TradeIntentOperation.Accept
                || operation == TradeIntentOperation.Think))
            {
                price = 0f;
            }

            var frame = CapturePredictionFrame(offer, operation);
            register(PredictionScope,
                predictionId => SendIntent(predictionId, operation, price, offer.State),
                () => ApplyPrediction(operation),
                () => UndoPrediction(frame));
            return true;
        }

        /// <summary>Capture-only prefix for Accept/Decline/Think: snapshot the offer and UI flags
        /// before vanilla runs. The flags are set here (before the game method) so the screen's own
        /// close/stop path does not send a competing Close intent; the postfix then registers the
        /// one post-hoc prediction. The game already applied the local change, so the prediction's
        /// apply is only used to replay after an earlier rollback.</summary>
        private IntentCapture BeginIntent(TradeIntentOperation operation, float price,
            CustomerTradeData acceptBefore = null)
        {
            var offer = PendingOffer();
            if (!_context.InGame() || offer?.State == null || _pendingCounter < 0)
            {
                return null;
            }

            if (offer.State.Trading && (operation == TradeIntentOperation.Accept
                || operation == TradeIntentOperation.Think))
            {
                price = 0f;
            }

            var frame = CapturePredictionFrame(offer, operation);
            MarkPredicted(operation);
            return new IntentCapture
            {
                Operation = operation,
                Price = price,
                Frame = frame,
                State = offer.State,
                AcceptBefore = acceptBefore,
            };
        }

        private void EndIntent(IntentCapture capture)
        {
            if (capture == null)
            {
                return;
            }

            if (capture.Operation == TradeIntentOperation.Accept)
            {
                var result = BuildAcceptResult(capture);
                if (result == null)
                {
                    // Vanilla performed no change (insufficient funds or a missing trade card),
                    // so there is nothing for the host to accept or roll back. Undo only the
                    // optimistic bookkeeping the prefix set; the screen is already unchanged.
                    RestoreIntent(capture.Frame);
                    return;
                }

                capture.Frame.Effect = result.Outcome == TradeOutcome.Accepted
                    ? BuildAcceptEffect(capture)
                    : null;
                PredictionApi.Predict(PredictionScope,
                    predictionId => SendIntent(predictionId, capture.Operation, capture.Price,
                        capture.State, result.Outcome, result.State, capture.Frame.Effect),
                    () => ApplyPrediction(capture.Operation, capture.Frame.Effect),
                    () => UndoPrediction(capture.Frame));
                return;
            }

            PredictionApi.Predict(PredictionScope,
                predictionId => SendIntent(predictionId, capture.Operation, capture.Price,
                    capture.State),
                () => ApplyPrediction(capture.Operation),
                () => UndoPrediction(capture.Frame));
        }

        /// <summary>The card/coin movement a completed accept performed. Vanilla already applied
        /// it through its own methods; this only records how to replay and reverse it so the one
        /// trade prediction can fully revert on a host reject.</summary>
        private static AcceptEffect BuildAcceptEffect(IntentCapture capture)
        {
            return new AcceptEffect
            {
                Received = CopyCard(capture.State.CardL),
                Given = capture.State.Trading ? CopyCard(capture.State.CardR) : null,
                CoinSpent = capture.State.Trading ? 0f : capture.Price,
            };
        }

        /// <summary>Reads the result the actor's own vanilla accept already produced. The host
        /// records and broadcasts this instead of re-running the trade method, so the mint/RNG/
        /// coin spend the actor performed is the single application. Returns null when vanilla
        /// accepted nothing and changed nothing, which the caller rolls back locally.</summary>
        private AcceptResult BuildAcceptResult(IntentCapture capture)
        {
            var screen = TradeInterop.Screen;
            if (TradeInterop.HasAccepted(screen))
            {
                return new AcceptResult { Outcome = TradeOutcome.Accepted };
            }

            if (!TradeInterop.IsScreenOpen(screen))
            {
                // Vanilla closes the screen only when the haggling ran out of declines, so the
                // customer walked away and the offer must be removed.
                return new AcceptResult { Outcome = TradeOutcome.WalkedAway };
            }

            var after = TradeInterop.Capture(screen);
            var before = capture.AcceptBefore;
            var askChanged = before != null
                && !Mathf.Approximately(after.m_SellCardAskPrice, before.m_SellCardAskPrice);
            var declinesChanged = before != null
                && after.m_MaxDeclineCount != before.m_MaxDeclineCount;
            if (!askChanged && !declinesChanged)
            {
                return null;
            }

            return new AcceptResult
            {
                Outcome = askChanged ? TradeOutcome.Haggle : TradeOutcome.Refused,
                State = ApplyDataToState(capture.State, after),
            };
        }

        /// <summary>Reverts only the session flags the Accept prefix set, leaving the screen and
        /// offer state untouched (vanilla already left them unchanged).</summary>
        private void RestoreIntent(PredictionFrame frame)
        {
            _claimAccepted = frame.ClaimAccepted;
            _terminalSent = frame.TerminalSent;
            _thinkingSent = frame.ThinkingSent;
            _acceptedShown = frame.AcceptedShown;
            _awaitingPrediction = false;
        }

        private static TradeOfferState ApplyDataToState(TradeOfferState baseState,
            CustomerTradeData data)
        {
            var state = CloneState(baseState);
            state.Trading = data.m_IsTrading;
            state.CardL = CopyCard(data.m_CardData_L);
            state.CardR = CopyCard(data.m_CardData_R);
            state.Price = data.m_SellCardAskPrice;
            state.MarketPrice = data.m_SellCardMarketPrice;
            state.PriceSet = data.m_PriceSet;
            state.LastPriceSet = data.m_LastPriceSet;
            state.MaxDeclineCount = data.m_MaxDeclineCount;
            state.DeclineCount = data.m_DeclineCount;
            return state;
        }

        /// <summary>Mirrors vanilla's local UI result into the module's session flags. The game
        /// already performed the mutation, so only bookkeeping is recorded here.</summary>
        private void MarkPredicted(TradeIntentOperation operation)
        {
            switch (operation)
            {
                case TradeIntentOperation.Accept:
                    _awaitingPrediction = true;
                    _terminalSent = true;
                    _acceptedShown = true;
                    break;
                case TradeIntentOperation.Decline:
                    _awaitingPrediction = true;
                    _terminalSent = true;
                    break;
                case TradeIntentOperation.Think:
                    _awaitingPrediction = true;
                    _claimAccepted = false;
                    _thinkingSent = true;
                    break;
            }
        }

        private void SendIntent(Guid predictionId, TradeIntentOperation operation, float price,
            TradeOfferState state, TradeOutcome result = TradeOutcome.None,
            TradeOfferState resultState = null, AcceptEffect effect = null)
        {
            CoopPlugin.Log.LogInfo("[trade] client intent " + operation + " counter=" + state.Counter
                + " index=" + state.CustomerIndex + " gen=" + state.CustomerGeneration + " nonce="
                + state.OfferNonce + " price=" + price.ToString("F2") + " result=" + result + " id="
                + predictionId + ".");
            _context.Send(1, new TradeIntentMessage
            {
                PredictionId = predictionId,
                Operation = operation,
                Counter = state.Counter,
                CustomerIndex = state.CustomerIndex,
                CustomerGeneration = state.CustomerGeneration,
                OfferNonce = state.OfferNonce,
                Price = price,
                Result = result,
                ResultState = resultState,
                CardReceived = effect?.Received,
                CardRemoved = effect?.Given,
                CoinSpent = effect?.CoinSpent ?? 0f,
            });
        }

        private PredictionFrame CapturePredictionFrame(LocalOffer offer,
            TradeIntentOperation operation)
            => new PredictionFrame
            {
                Operation = operation,
                State = CloneState(offer.State),
                ClaimAccepted = _claimAccepted,
                TerminalSent = _terminalSent,
                ThinkingSent = _thinkingSent,
                AcceptedShown = _acceptedShown,
                ScreenOpen = TradeInterop.IsScreenOpen(TradeInterop.Screen),
            };

        private void ApplyPrediction(TradeIntentOperation operation, AcceptEffect effect = null)
        {
            switch (operation)
            {
                case TradeIntentOperation.Open:
                    _claimAccepted = true;
                    break;
                case TradeIntentOperation.Accept:
                    _awaitingPrediction = true;
                    _terminalSent = true;
                    _acceptedShown = true;
                    TradeInterop.ShowAccepted(TradeInterop.Screen);
                    break;
                case TradeIntentOperation.Decline:
                    _awaitingPrediction = true;
                    _terminalSent = true;
                    ClosePredictedScreen();
                    break;
                case TradeIntentOperation.Think:
                    _awaitingPrediction = true;
                    _claimAccepted = false;
                    _thinkingSent = true;
                    ClosePredictedScreen();
                    break;
            }

            if (effect != null)
            {
                ApplyAcceptEffect(effect);
            }
        }

        /// <summary>Replays the card/coin movement of an accepted trade through the game's own
        /// methods. Used only after an earlier prediction on the key was undone; the card helper
        /// sets the remote-apply guard so the observer does not forward a second time, and the
        /// wallet event is skipped by the Hud observer while reconciliation is running.</summary>
        private static void ApplyAcceptEffect(AcceptEffect effect)
        {
            if (effect.Received != null)
            {
                WorldCardInteraction.ApplyPredictedCardDelta(effect.Received, 1, true);
            }

            if (effect.Given != null)
            {
                WorldCardInteraction.ApplyPredictedCardDelta(effect.Given, 1, false);
            }

            if (effect.CoinSpent > 0f)
            {
                CEventManager.QueueEvent(new CEventPlayer_ReduceCoin(effect.CoinSpent));
            }

            if (effect.Received != null || effect.Given != null)
            {
                WorldClientBehaviour.ActiveCards?.NotifyCardsChanged();
            }
        }

        /// <summary>Reverses the card/coin movement of an accepted trade, newest mutation first.</summary>
        private static void UndoAcceptEffect(AcceptEffect effect)
        {
            if (effect.Given != null)
            {
                WorldCardInteraction.ApplyPredictedCardDelta(effect.Given, 1, true);
            }

            if (effect.Received != null)
            {
                WorldCardInteraction.ApplyPredictedCardDelta(effect.Received, 1, false);
            }

            if (effect.CoinSpent > 0f)
            {
                CEventManager.QueueEvent(new CEventPlayer_AddCoin(effect.CoinSpent, true));
            }

            if (effect.Received != null || effect.Given != null)
            {
                WorldClientBehaviour.ActiveCards?.NotifyCardsChanged();
            }
        }

        private void UndoPrediction(PredictionFrame frame)
        {
            CoopPlugin.Log.LogInfo("[trade] client undo " + frame.Operation + " state="
                + (frame.State == null ? "<null>" : frame.State.Counter.ToString())
                + " effect=" + (frame.Effect != null) + ".");
            if (frame.Effect != null)
            {
                UndoAcceptEffect(frame.Effect);
            }

            var offer = PendingOffer();
            if (offer != null && frame.State != null)
            {
                offer.State = CloneState(frame.State);
                RefreshCarrierData(offer);
            }

            _claimAccepted = frame.ClaimAccepted;
            _terminalSent = frame.TerminalSent;
            _thinkingSent = frame.ThinkingSent;
            _acceptedShown = frame.AcceptedShown;
            _awaitingPrediction = false;

            if (frame.Operation == TradeIntentOperation.Open)
            {
                // The host refused the session open. There is no session, so close the screen and
                // end the local offer: leaving it open would let the unaccepted screen's buttons
                // run the vanilla accept unguarded (its card/coin effects would then be forwarded
                // as separate intents into a trade the host never admitted).
                ClosePredictedScreen();
                EndLocalSession(false);
                return;
            }

            var screen = TradeInterop.Screen;
            var screenOpen = TradeInterop.IsScreenOpen(screen);
            if (frame.ScreenOpen && offer?.Carrier != null && offer.State != null)
            {
                TradeInterop.OpenData(offer.Carrier, TradeInterop.DataFromState(offer.State));
            }
            else if (!frame.ScreenOpen && screenOpen)
            {
                ClosePredictedScreen();
            }

            if (frame.AcceptedShown && screen != null && !TradeInterop.HasAccepted(screen))
            {
                TradeInterop.ShowAccepted(screen);
            }
        }

        private void ClosePredictedScreen()
        {
            var screen = TradeInterop.Screen;
            if (screen == null || !TradeInterop.IsScreenOpen(screen))
            {
                return;
            }

            _ignoreScreenClose = true;
            try
            {
                TradeInterop.CloseScreen(screen);
            }
            finally
            {
                _ignoreScreenClose = false;
            }
        }

        private static TradeOfferState CloneState(TradeOfferState state)
        {
            if (state == null)
            {
                return null;
            }

            return new TradeOfferState
            {
                Counter = state.Counter,
                Trading = state.Trading,
                CardL = CopyCard(state.CardL),
                CardR = CopyCard(state.CardR),
                Price = state.Price,
                MarketPrice = state.MarketPrice,
                PriceSet = state.PriceSet,
                LastPriceSet = state.LastPriceSet,
                MaxDeclineCount = state.MaxDeclineCount,
                DeclineCount = state.DeclineCount,
                Remaining = state.Remaining,
                CustomerIndex = state.CustomerIndex,
                CustomerGeneration = state.CustomerGeneration,
                OfferNonce = state.OfferNonce,
                CustomerFemale = state.CustomerFemale,
                Position = state.Position,
                Yaw = state.Yaw,
            };
        }

        private static CardData CopyCard(CardData card)
        {
            if (card == null)
            {
                return null;
            }

            var copy = new CardData();
            copy.CopyData(card);
            return copy;
        }

        private void ResetSession()
        {
            _pendingCounter = -1;
            _claimAccepted = false;
            _awaitingPrediction = false;
            _terminalSent = false;
            _thinkingSent = false;
            _acceptedShown = false;
        }

        private void EndLocalSession(bool sendClose)
        {
            if (_pendingCounter < 0)
            {
                return;
            }

            var counter = _pendingCounter;
            var offer = PendingOffer();
            if (sendClose && !_terminalSent && !_thinkingSent && !_awaitingPrediction && offer != null)
            {
                // A Close is a fire-and-forget notification: the host only clears the offer's owner
                // and never resolves or rejects it, so there is no authoritative state to roll back
                // and no prediction is registered (a registered one could never be retired). The
                // non-empty id only satisfies the host's sender validation.
                SendIntent(Guid.NewGuid(), TradeIntentOperation.Close, 0f, offer.State);
            }

            _stopHandled = true;
            ResetSession();
            if (offer != null && !offer.RemovedByHost && offer.State != null)
            {
                TradeInterop.SetStoredData(offer.Carrier, TradeInterop.DataFromState(offer.State));
                TradeInterop.ResetCarrierInteraction(offer.Carrier);
            }
            else
            {
                if (offer != null)
                {
                    ReleaseCarrier(offer);
                }

                _offers.Remove((byte)counter);
            }
        }

        private void CloseRemoteScreen()
        {
            var screen = TradeInterop.Screen;
            if (screen != null && TradeInterop.IsScreenOpen(screen))
            {
                _stopHandled = true;
                try
                {
                    TradeInterop.CloseScreen(screen);
                }
                finally
                {
                    EndLocalSession(false);
                }
            }
            else
            {
                EndLocalSession(false);
            }
        }

        [HarmonyPatch(typeof(Customer), "OnMousePress")]
        private static class CustomerPressPatch
        {
            // Runs AFTER vanilla OnMousePress opens the trade screen. BeginSession snapshots the
            // open/closed state for the prediction's undo; starting it in a prefix recorded the
            // screen as closed (vanilla opens it a moment later), so when the host confirmed the
            // session the reconcile "restored" that captured state and closed the screen the
            // guest had just opened.
            [HarmonyPostfix]
            private static void Postfix(Customer __instance)
            {
                var client = _active;
                if (client == null)
                {
                    return;
                }

                if (!client.IsCarrier(__instance, out var offer))
                {
                    if (client._carrierKeysByCustomer.Count > 0)
                    {
                        CoopPlugin.Log.LogInfo("[trade] click on customer "
                            + (__instance == null ? "<null>" : __instance.name)
                            + " that is not a trade carrier (carriers="
                            + client._carrierKeysByCustomer.Count + ").");
                    }

                    return;
                }

                if (offer.State == null || client._pendingCounter >= 0)
                {
                    return;
                }

                CoopPlugin.Log.LogInfo("[trade] carrier clicked id=" + offer.State.Counter
                    + "; starting session.");
                client.BeginSession(offer);
            }
        }

        [HarmonyPatch(typeof(Customer), "OnPressStopInteract")]
        private static class CustomerStopPatch
        {
            // The carrier customer is host-owned, but the client now plays vanilla: the game's own
            // stop clears the carrier's trade data and resumes it. The postfix finishes the local
            // session and sends one fire-and-forget Close intent for the host to resolve. A Close
            // has no authoritative result to roll back, so it is not a prediction.
            [HarmonyPostfix]
            private static void Postfix(Customer __instance)
            {
                var client = _active;
                if (client == null || !client.IsCarrier(__instance, out _)
                    || client._pendingCounter < 0)
                {
                    return;
                }

                client._stopHandled = true;
                TradeInterop.RestorePlayerUi();
                client.EndLocalSession(!client._terminalSent && !client._thinkingSent
                    && !client._awaitingPrediction);
            }
        }

        [HarmonyPatch(typeof(CustomerTradeCardScreen), "OnPressAccept")]
        private static class AcceptPatch
        {
            // The game owns the accept: vanilla mints/takes cards, spends coins and shows the
            // accepted panel. The prefix snapshots the session, marks the flags so the screen's
            // close path cannot send a competing Close, and brackets the call with the card/wallet
            // forwarding guards so the same effect is not sent to the host twice. The postfix
            // registers one post-hoc prediction carrying the whole card + coin + offer outcome.
            [HarmonyPrefix]
            private static void Prefix(CustomerTradeCardScreen __instance, out IntentCapture __state)
            {
                __state = null;
                var client = _active;
                var offer = client?.PendingOffer();
                if (client == null || offer == null
                    || !ReferenceEquals(TradeInterop.CurrentCustomer(__instance), offer.Carrier))
                {
                    return;
                }

                if (TradeInterop.HasAccepted(__instance))
                {
                    return;
                }

                if (client._awaitingPrediction || !client._claimAccepted)
                {
                    return;
                }

                if (!TradeInterop.IsTrading(__instance) && __instance.m_SetPriceInput != null
                    && !string.IsNullOrWhiteSpace(__instance.m_SetPriceInput.text))
                {
                    __instance.OnInputTextUpdated(__instance.m_SetPriceInput.text);
                }

                __state = client.BeginIntent(TradeIntentOperation.Accept,
                    TradeInterop.IsTrading(__instance) ? 0f : TradeInterop.PriceSet(__instance),
                    TradeInterop.Capture(__instance));
                if (__state == null)
                {
                    return;
                }

                // Vanilla still runs; only the duplicate forwarding is suppressed. The wallet
                // observer checks EconomyActionScope at event-queue time, and the card observer
                // checks the card guard, so neither registers a separate intent for this accept.
                __state.EconomyScoped = true;
                EconomyActionScope.Enter();
                __state.CardForwarding = WorldCardInteraction.SuppressCardForwarding();
            }

            [HarmonyPostfix]
            private static void Postfix(IntentCapture __state) => _active?.EndIntent(__state);

            [HarmonyFinalizer]
            private static void Finalizer(IntentCapture __state)
            {
                if (__state == null)
                {
                    return;
                }

                __state.CardForwarding?.Dispose();
                __state.CardForwarding = null;
                if (__state.EconomyScoped)
                {
                    __state.EconomyScoped = false;
                    EconomyActionScope.Exit();
                }
            }
        }

        [HarmonyPatch(typeof(CustomerTradeCardScreen), "OnPressDecline")]
        private static class DeclinePatch
        {
            // Vanilla decline closes the screen, whose close path stops the host-owned customer.
            // The prefix marks the session terminal so the close does not send a Close instead, and
            // the postfix registers the one Decline prediction.
            [HarmonyPrefix]
            private static void Prefix(CustomerTradeCardScreen __instance, out IntentCapture __state)
            {
                __state = null;
                var client = _active;
                var offer = client?.PendingOffer();
                if (client == null || offer == null
                    || !ReferenceEquals(TradeInterop.CurrentCustomer(__instance), offer.Carrier))
                {
                    return;
                }

                // A session the host never accepted (a rejected open) has nothing to resolve; let
                // vanilla close it locally.
                if (!client._claimAccepted)
                {
                    return;
                }

                if (!client._awaitingPrediction && !client._terminalSent)
                {
                    __state = client.BeginIntent(TradeIntentOperation.Decline, 0f);
                }
            }

            [HarmonyPostfix]
            private static void Postfix(IntentCapture __state) => _active?.EndIntent(__state);
        }

        [HarmonyPatch(typeof(CustomerTradeCardScreen), "OnPressLetMeThink")]
        private static class ThinkPatch
        {
            // Vanilla Think writes the customer's trade data and resumes it; the client now plays
            // that path. The prefix marks the session terminal and the postfix registers the one
            // Think prediction.
            [HarmonyPrefix]
            private static void Prefix(CustomerTradeCardScreen __instance, out IntentCapture __state)
            {
                __state = null;
                var client = _active;
                var offer = client?.PendingOffer();
                if (client == null || offer == null
                    || !ReferenceEquals(TradeInterop.CurrentCustomer(__instance), offer.Carrier))
                {
                    return;
                }

                if (!client._awaitingPrediction && client._claimAccepted && !client._thinkingSent)
                {
                    var price = TradeInterop.IsTrading(__instance) ? 0f
                        : TradeInterop.PriceSet(__instance);
                    __state = client.BeginIntent(TradeIntentOperation.Think, price);
                }
            }

            [HarmonyPostfix]
            private static void Postfix(IntentCapture __state) => _active?.EndIntent(__state);
        }

        [HarmonyPatch(typeof(CustomerTradeCardScreen), "OnCloseScreen")]
        private static class ScreenClosePatch
        {
            [HarmonyPostfix]
            private static void Postfix(CustomerTradeCardScreen __instance)
            {
                var client = _active;
                if (client == null || client._ignoreScreenClose || client._pendingCounter < 0
                    || TradeInterop.IsScreenOpen(__instance))
                {
                    return;
                }

                if (!client._stopHandled)
                {
                    client.EndLocalSession(!client._terminalSent && !client._thinkingSent
                        && !client._awaitingPrediction);
                }
            }
        }

        [HarmonyPatch(typeof(CustomerManager), "Start")]
        private static class CustomerManagerLifecyclePatch
        {
            [HarmonyPostfix]
            private static void Postfix()
                => _active?.OnReadinessSignal();
        }

        [HarmonyPatch(typeof(UIScreenBase), "Start")]
        private static class TradeScreenStartPatch
        {
            [HarmonyPostfix]
            private static void Postfix(UIScreenBase __instance)
            {
                if (__instance is CustomerTradeCardScreen)
                {
                    _active?.OnReadinessSignal();
                }
            }
        }

        [HarmonyPatch(typeof(Customer), "ActivateCustomer")]
        [HarmonyPatch(typeof(Customer), "DeactivateCustomer")]
        [HarmonyPatch(typeof(Customer), "OnPressStopInteract")]
        private static class CustomerLifecyclePatch
        {
            [HarmonyPostfix]
            private static void Postfix(Customer __instance)
            {
                if (_active == null || __instance == null)
                {
                    return;
                }

                var index = TradeInterop.CustomerListIndex(__instance);
                _active.PrepareOfferForCustomer(index);
                _active.ApplyPendingMessages();
            }
        }
    }
}
