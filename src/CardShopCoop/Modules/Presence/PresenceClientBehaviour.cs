using System;
using System.Collections.Generic;
using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using CoopBehaviour = CardShopCoop.Runtime.CoopBehaviour;
using UnityEngine;

namespace CardShopCoop.Modules.Presence
{
    /// <summary>Client sender and host-state receiver for the presence stream.</summary>
    [ClientBehaviour]
    public sealed class PresenceClientBehaviour : CoopBehaviour
    {
        private static PresenceClientBehaviour _active;
        private readonly HashSet<int> _rosterIds = new();
        // Host connection id -> display name, kept from the roster so the window can list players.
        private readonly Dictionary<int, string> _rosterNames = new();
        private readonly HashSet<int> _modelIds = new();
        private readonly Dictionary<int, PresenceModelEntry> _models = new();
        private PresenceService _service;
        private float _stateTimer;
        private int _selfId = -1;
        private bool _joined;
        private bool _modelPending;
        private int _pendingModelRequests;
        private bool _shutdown;
        private PresenceModelStateMessage _pendingModelState;
        private PresenceModelEntry _lastSentModel;

        internal static PresenceClientBehaviour Active => _active;

        internal bool IsLocalConnection(int connectionId)
            => _selfId > 0 && connectionId == _selfId;

        /// <summary>The host sends the host's own model as id 0, while a client's roster calls the
        /// host 1. Normalize the model store to roster ids so diagnostics can look a player up by
        /// the same id the window lists.</summary>
        private static int RosterModelId(int id) => id == 0 ? 1 : id;

        internal bool TryGetModel(int connectionId, out PresenceModelEntry model)
        {
            if (_models.TryGetValue(connectionId, out var stored) && stored != null)
            {
                model = Clone(stored);
                return true;
            }

            model = null;
            return false;
        }

        /// <summary>The session roster for the window: the host first (id 1 from a client's view),
        /// then this player, then the other clients in id order.</summary>
        internal IReadOnlyList<PresencePlayer> BuildPlayers()
        {
            var players = new List<PresencePlayer>(_rosterIds.Count + 2);
            var hostName = PresenceApi.PeerName(1);
            players.Add(new PresencePlayer(1,
                string.IsNullOrEmpty(hostName) ? "Host" : hostName, true, false));

            if (_selfId > 0)
            {
                players.Add(new PresencePlayer(_selfId, PresenceApi.LocalPlayerName, false, true));
            }

            var ids = new List<int>(_rosterIds);
            ids.Sort();
            for (var i = 0; i < ids.Count; i++)
            {
                var id = ids[i];
                var name = _rosterNames.TryGetValue(id, out var stored) ? stored : null;
                players.Add(new PresencePlayer(id,
                    string.IsNullOrEmpty(name) ? "Player " + id : name, false, false));
            }

            return players;
        }

        private void OnEnable()
        {
            if (_shutdown || _service != null)
            {
                return;
            }

            _service = new PresenceService(RuntimeContext);
            _service.ModelChanged += OnLocalModelChanged;
            _active = this;
        }

        private void Update()
        {
            if (_shutdown)
            {
                return;
            }

            using (Util.PerfProbe.Sample("module.presence.client-update"))
            {
                UpdateInner();
            }
        }

        private void UpdateInner()
        {
            _service.Tick(Time.deltaTime);
            if (!_joined || !RuntimeContext.InGame() || RuntimeContext.PreloadHold?.Invoke() == true)
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
            _service.InvalidateLocalHold();
            SendLocalModel();
        }

        [MessageHandler]
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
                _rosterNames[entry.Id] = entry.Name;
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
                _rosterNames.Remove(id);
                _service.Remove(1000 + id);
            }

            var hostName = RuntimeContext.PeerName?.Invoke(1);
            _service.SetName(1, string.IsNullOrEmpty(hostName) ? "Host" : hostName);

            if (_selfId > 0 && _pendingModelState != null)
            {
                var pending = _pendingModelState;
                _pendingModelState = null;
                ApplyModelState(pending);
            }

            PresenceApi.NotifyRosterChanged();
        }

        [MessageHandler]
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
                _rosterNames.Remove(message.Id);
                _service.Remove(avatarId);
                PresenceApi.NotifyRosterChanged();
                return;
            }

            if (message.Entry == null)
            {
                return;
            }

            _rosterIds.Add(message.Id);
            _rosterNames[message.Id] = message.Entry.Name;
            _service.SetName(avatarId, message.Entry.Name);
            PresenceApi.NotifyRosterChanged();
        }

        [MessageHandler]
        private void HandleHostState(MessageContext context, PresenceStateMessage message)
        {
            if (message == null)
            {
                return;
            }

            _service.ApplyState(1, message);
        }

        [MessageHandler]
        private void HandleRelayState(MessageContext context, PresenceRelayStateMessage message)
        {
            if (message == null)
            {
                return;
            }

            _service.ApplyState(1000 + message.SenderId, message.State);
        }

        [MessageHandler]
        private void HandleHostHold(MessageContext context, PresenceHoldMessage message)
        {
            if (message == null)
            {
                return;
            }

            _service.ApplyHold(1, message);
        }

        [MessageHandler]
        private void HandleRelayHold(MessageContext context, PresenceRelayHoldMessage message)
        {
            if (message?.Hold == null)
            {
                return;
            }

            _service.ApplyHold(1000 + message.SenderId, message.Hold);
        }

        [MessageHandler]
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
                _models[RosterModelId(entry.Id)] = Clone(entry);
                if (entry.Id == _selfId)
                {
                    // A model request that is still waiting for its host decision owns the local
                    // appearance; this join baseline crossed it on the wire and is older.
                    if (_pendingModelRequests > 0)
                    {
                        continue;
                    }

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
                _models.Remove(RosterModelId(id));
                _service.Remove(id == 0 ? 1 : 1000 + id);
            }
        }

        [MessageHandler]
        private void HandleModelDelta(MessageContext _, PresenceModelDeltaMessage message)
        {
            if (message == null)
            {
                return;
            }

            if (message.Kind == PresenceEntityDeltaKind.Remove)
            {
                _modelIds.Remove(message.Id);
                _models.Remove(RosterModelId(message.Id));
                _service.Remove(message.Id == 0 ? 1 : 1000 + message.Id);
                return;
            }

            if (message.Entry == null)
            {
                return;
            }

            // The actor's own appearance is settled by its PresenceModelResponseMessage; a delta
            // is only relayed to the other peers.
            if (message.Id == _selfId)
            {
                return;
            }

            _models[RosterModelId(message.Id)] = Clone(message.Entry);
            _modelIds.Add(message.Id);
            _service.SetModel(message.Id == 0 ? 1 : 1000 + message.Id, message.Entry);
        }

        [MessageHandler]
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

        /// <summary>The host connection dropped: remove the remote avatars/names immediately so
        /// they cannot outlive the session teardown (which may be a frame or more away).</summary>
        [OnClientDisconnected]
        private void ForgetHost(PeerConnection connection, DisconnectInfo info)
        {
            if (PeerHandle.ForConnection(connection)?.IsHost != true)
            {
                return;
            }

            PresenceApi.ClearPeerNames();
            _rosterIds.Clear();
            _rosterNames.Clear();
            _modelIds.Clear();
            _models.Clear();
            _joined = false;
            _pendingModelRequests = 0;
            _service?.ClearRemoteAvatars();
        }

        internal void SendEmote()
        {
            if (CanSendIntent())
            {
                Context.Send(new PresenceEmoteMessage { Emote = 0 });
            }
        }

        internal void SendActivity(EItemType pack)
        {
            if (CanSendIntent())
            {
                Context.Send(new PresenceActivityMessage { Activity = 1, Pack = pack });
            }
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
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
            _rosterNames.Clear();
            _modelIds.Clear();
            _models.Clear();
            _joined = false;
            _modelPending = false;
            _pendingModelRequests = 0;
            _pendingModelState = null;
            _lastSentModel = null;
            PresenceApi.NotifyRosterChanged();
        }

        private void OnDestroy() => Shutdown();

        /// <summary>The editor already applied this model change through the service's game path
        /// (this observer runs from the service's ModelChanged event). Send the prediction with
        /// the previous local model as its undo state; the host answers with the authoritative
        /// appearance for both accept and reject.</summary>
        private void OnLocalModelChanged(PresenceModelEntry model)
        {
            if (model == null)
            {
                return;
            }

            if (!CanSendIntent())
            {
                if (_joined)
                {
                    _modelPending = true;
                }

                return;
            }

            _modelPending = false;
            var next = Clone(model);
            var previous = Clone(_lastSentModel);
            _lastSentModel = Clone(next);
            Context.Send(new PresenceModelRequestMessage
            {
                Female = next.Female,
                ModelIndex = next.ModelIndex,
                CustomizationJson = next.CustomizationJson,
                Previous = previous,
            });
            _pendingModelRequests++;
        }

        /// <summary>Client: reverse the appearance prediction. The authoritative state, if any,
        /// is applied by <see cref="MergeModel"/> afterwards.</summary>
        internal void UndoModel(PresenceModelRequestMessage message)
        {
            if (_shutdown || message == null || message.Previous == null)
            {
                return;
            }

            _lastSentModel = Clone(message.Previous);
            RememberSelfModel(message.Previous);
            _service.ApplyPredictionLocalModel(message.Previous);
        }

        /// <summary>Client: settle the appearance prediction with the host's authoritative
        /// answer. Runs once on every resolution, after <see cref="UndoModel"/> when rejected.</summary>
        internal void MergeModel(PresenceModelRequestMessage message,
            ActionResponseMessage response)
        {
            if (_pendingModelRequests > 0)
            {
                _pendingModelRequests--;
            }

            if (_shutdown || message == null
                || response is not PresenceModelResponseMessage answer || answer.Entry == null)
            {
                return;
            }

            _lastSentModel = Clone(answer.Entry);
            RememberSelfModel(answer.Entry);
            if (response.Rejected)
            {
                // The undo already restored the previous appearance; this settles it on the
                // host's exact stored state when that differs.
                _service.ApplyPredictionLocalModel(answer.Entry);
            }
            else
            {
                // The prediction is already on screen and saved. Re-anchor the local record to
                // the host's canonical entry without rebuilding the editor body.
                _service.ApplyAuthoritativeLocalModel(answer.Entry);
            }
        }

        /// <summary>Keeps the diagnostics model copy in step with the authoritative local
        /// appearance. This client's own entry is settled by the action response, not a delta.</summary>
        private void RememberSelfModel(PresenceModelEntry model)
        {
            if (_selfId > 0 && model != null)
            {
                _models[RosterModelId(_selfId)] = Clone(model);
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
            Context.Send(state);
            PublishLocalHold();
        }

        /// <summary>Sends the hold appearance only when it changes, on the reliable lane. It must
        /// not ride the 15 Hz transform stream: the verbose CardData list does not fit the
        /// unreliable MTU once a hand fills up.</summary>
        private void PublishLocalHold()
        {
            if (!CanSendIntent())
            {
                return;
            }

            if (_service.TryConsumeLocalHoldChange(out var hold))
            {
                Context.Send(hold);
            }
        }

        private bool CanSendIntent()
        {
            return !_shutdown && _joined
                && _selfId > 0 && RuntimeContext.InGame()
                && RuntimeContext.PreloadHold?.Invoke() != true;
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
