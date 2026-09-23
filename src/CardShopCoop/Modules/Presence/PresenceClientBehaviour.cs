using System;
using System.Collections.Generic;
using CardShopCoop.Attributes;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Runtime;
using UnityEngine;

namespace CardShopCoop.Modules.Presence
{
    /// <summary>Client sender and host-state receiver for the presence stream.</summary>
    [ClientBehaviour]
    public sealed class PresenceClientBehaviour : CoopBehaviour
    {
        private static PresenceClientBehaviour _active;
        private readonly HashSet<int> _rosterIds = new();
        private readonly HashSet<int> _modelIds = new();
        private CoopRuntimeContext _context;
        private PresenceService _service;
        private float _stateTimer;
        private int _selfId = -1;
        private bool _joined;
        private bool _modelPending;
        private bool _shutdown;
        private PresenceModelStateMessage _pendingModelState;
        private PresenceModelEntry _lastSentModel;

        internal static PresenceClientBehaviour Active => _active;

        internal bool IsLocalConnection(int connectionId)
            => _selfId > 0 && connectionId == _selfId;

        private void OnEnable()
        {
            if (_shutdown || _context != null)
            {
                return;
            }

            _context = RuntimeContext;
            _service = new PresenceService(_context);
            _service.ModelChanged += OnLocalModelChanged;
            var registered = false;
            try
            {
                _context.Messages.RegisterAttributedHandlers(this);
                registered = true;
                _active = this;
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogError("Presence client initialization failed: " + error);
                if (registered)
                {
                    _context.Messages.UnregisterAttributedHandlers(this);
                }

                _service.Shutdown();
                _service = null;
                _context = null;
                throw;
            }
        }

        private void Update()
        {
            if (_shutdown)
            {
                return;
            }

            using (CardShopCoop.Util.PerfProbe.Sample("module.presence.client-update"))
            {
                UpdateInner();
            }
        }

        private void UpdateInner()
        {
            _service.Tick(Time.deltaTime);
            if (!_joined || !_context.InGame() || _context.PreloadHold?.Invoke() == true)
            {
                return;
            }

            if (_modelPending)
            {
                _modelPending = false;
                SendLocalModel();
            }

            PublishLocalState(Time.deltaTime);
        }

        [OnFullyJoined]
        private void MarkJoined(PeerConnection _)
        {
            if (_shutdown)
            {
                return;
            }

            _joined = true;
            _modelPending = true;
            SendLocalModel();
        }

        [MessageHandler(typeof(PresenceRosterMessage))]
        private void HandleRoster(MessageContext context, PresenceRosterMessage message)
        {
            if (message == null)
            {
                return;
            }

            _selfId = message.SelfId;

            var seen = new HashSet<int>();
            foreach (var entry in message.Entries)
            {
                if (entry.Id == _selfId)
                {
                    continue;
                }

                seen.Add(entry.Id);
                _rosterIds.Add(entry.Id);
                _service.SetName(1000 + entry.Id, entry.Name);
            }

            var stale = new List<int>();
            foreach (var id in _rosterIds)
            {
                if (!seen.Contains(id))
                {
                    stale.Add(id);
                }
            }

            foreach (var id in stale)
            {
                _rosterIds.Remove(id);
                _service.Remove(1000 + id);
            }

            var hostName = _context.PeerName?.Invoke(1);
            _service.SetName(1, string.IsNullOrEmpty(hostName) ? "Host" : hostName);

            if (_selfId > 0 && _pendingModelState != null)
            {
                var pending = _pendingModelState;
                _pendingModelState = null;
                ApplyModelState(pending);
            }
        }

        [MessageHandler(typeof(PresenceRosterDeltaMessage))]
        private void HandleRosterDelta(MessageContext _, PresenceRosterDeltaMessage message)
        {
            if (message == null)
            {
                return;
            }

            var avatarId = 1000 + message.Id;
            if (message.Kind == PresenceEntityDeltaKind.Remove)
            {
                _rosterIds.Remove(message.Id);
                _service.Remove(avatarId);
                return;
            }

            if (message.Entry == null)
            {
                return;
            }

            _rosterIds.Add(message.Id);
            _service.SetName(avatarId, message.Entry.Name);
        }

        [MessageHandler(typeof(PresenceStateMessage))]
        private void HandleHostState(MessageContext context, PresenceStateMessage message)
        {
            if (message == null)
            {
                return;
            }

            _service.ApplyState(1, message);
        }

        [MessageHandler(typeof(PresenceRelayStateMessage))]
        private void HandleRelayState(MessageContext context, PresenceRelayStateMessage message)
        {
            if (message == null)
            {
                return;
            }

            _service.ApplyState(1000 + message.SenderId, message.State);
        }

        [MessageHandler(typeof(PresenceModelStateMessage))]
        private void HandleModelState(MessageContext context, PresenceModelStateMessage message)
        {
            if (message == null)
            {
                return;
            }

            // The roster carries the host-authenticated canonical id. Do not classify a
            // baseline model entry for this client as a remote avatar if transport delivery
            // ever presents model state before roster state.
            if (_selfId <= 0)
            {
                _pendingModelState = message;
                return;
            }

            ApplyModelState(message);
        }

        private void ApplyModelState(PresenceModelStateMessage message)
        {
            if (message == null)
            {
                return;
            }

            var seen = new HashSet<int>();
            foreach (var entry in message.Entries)
            {
                seen.Add(entry.Id);
                if (entry.Id == _selfId)
                {
                    _lastSentModel = Clone(entry);
                    _service.ApplyAuthoritativeLocalModel(entry);
                    continue;
                }

                var avatarId = entry.Id == 0 ? 1 : 1000 + entry.Id;
                _modelIds.Add(entry.Id);
                _service.SetModel(avatarId, entry);
            }

            var stale = new List<int>();
            foreach (var id in _modelIds)
            {
                if (!seen.Contains(id))
                {
                    stale.Add(id);
                }
            }

            foreach (var id in stale)
            {
                _modelIds.Remove(id);
                _service.Remove(id == 0 ? 1 : 1000 + id);
            }
        }

        [MessageHandler(typeof(PresenceModelDeltaMessage))]
        private void HandleModelDelta(MessageContext _, PresenceModelDeltaMessage message)
        {
            if (message == null)
            {
                return;
            }

            if (message.Kind == PresenceEntityDeltaKind.Remove)
            {
                _modelIds.Remove(message.Id);
                _service.Remove(message.Id == 0 ? 1 : 1000 + message.Id);
                return;
            }

            if (message.Entry == null)
            {
                return;
            }

            if (message.Id == _selfId)
            {
                PredictionApi.ApplyAuthoritative(message.PredictionId,
                    () =>
                    {
                        _service.ApplyPredictionLocalModel(message.Entry);
                        _lastSentModel = Clone(message.Entry);
                    });
                return;
            }

            _modelIds.Add(message.Id);
            _service.SetModel(message.Id == 0 ? 1 : 1000 + message.Id, message.Entry);
        }

        [MessageHandler(typeof(PresenceRelayTagMessage))]
        private void HandleRelayTag(MessageContext context, PresenceRelayTagMessage message)
        {
            if (message == null)
            {
                return;
            }

            var avatarId = 1000 + message.SenderId;
            if (message.Kind == 0)
            {
                _service.ShowEmote(avatarId);
            }
            else if (message.Kind == 1)
            {
                _service.ShowActivity(avatarId, (int)message.Extra);
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(message.Kind), message.Kind,
                    "Unknown authoritative presence tag discriminator.");
            }
        }

        internal void SendEmote()
        {
            if (CanSendIntent())
            {
                _context.Send(1, new PresenceEmoteMessage { Emote = 0 });
            }
        }

        internal void SendActivity(EItemType pack)
        {
            if (CanSendIntent())
            {
                _context.Send(1, new PresenceActivityMessage { Activity = 1, Pack = pack });
            }
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            _context?.Messages.UnregisterAttributedHandlers(this);
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

            if (_service != null)
            {
                _service.ModelChanged -= OnLocalModelChanged;
                _service.Shutdown();
                _service = null;
            }

            _rosterIds.Clear();
            _modelIds.Clear();
            _joined = false;
            _modelPending = false;
            _pendingModelState = null;
            _lastSentModel = null;
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        private void OnLocalModelChanged(PresenceModelEntry model)
        {
            if (model == null)
            {
                return;
            }

            if (CanSendIntent())
            {
                _modelPending = false;
                var next = Clone(model);
                if (_lastSentModel == null)
                {
                    _context.Send(1, new PresenceModelRequestMessage
                    {
                        PredictionId = Guid.Empty,
                        Female = next.Female,
                        ModelIndex = next.ModelIndex,
                        CustomizationJson = next.CustomizationJson,
                    });
                    _lastSentModel = next;
                    return;
                }

                var previous = Clone(_lastSentModel);
                PredictionApi.Predict(
                    "presence.model",
                    predictionId => _context.Send(1, new PresenceModelRequestMessage
                    {
                        PredictionId = predictionId,
                        Female = next.Female,
                        ModelIndex = next.ModelIndex,
                        CustomizationJson = next.CustomizationJson,
                    }),
                    () =>
                    {
                        _service.ApplyPredictionLocalModel(next);
                        _lastSentModel = Clone(next);
                    },
                    () =>
                    {
                        _service.ApplyPredictionLocalModel(previous);
                        _lastSentModel = Clone(previous);
                    });
            }
            else if (_joined)
            {
                _modelPending = true;
            }
        }

        private void SendLocalModel()
        {
            if (_selfId <= 0)
            {
                _modelPending = true;
                return;
            }

            OnLocalModelChanged(_service.GetLocalModel());
        }

        private void PublishLocalState(float deltaTime)
        {
            _stateTimer += deltaTime;
            var interval = 1f / Mathf.Clamp(CoopPlugin.SendRateHz.Value, 4f, 30f);
            if (_stateTimer < interval || !_service.TryGetLocalState(out var state))
            {
                return;
            }

            _stateTimer = 0f;
            _context.Send(1, state);
        }

        private bool CanSendIntent()
        {
            return !_shutdown && _joined
                && _selfId > 0 && _context.InGame()
                && _context.PreloadHold?.Invoke() != true;
        }

        private static PresenceModelEntry Clone(PresenceModelEntry model)
        {
            return model == null
                ? null
                : new PresenceModelEntry
                {
                    Id = model.Id,
                    Female = model.Female,
                    ModelIndex = model.ModelIndex,
                    CustomizationJson = model.CustomizationJson,
                };
        }

    }
}
