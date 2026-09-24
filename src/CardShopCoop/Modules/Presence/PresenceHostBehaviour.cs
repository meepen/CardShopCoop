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
    /// <summary>Host authority for roster, appearance, and authenticated peer presence.</summary>
    [ServerBehaviour]
    public sealed class PresenceHostBehaviour : CoopBehaviour
    {
        private const int MaxCustomizationJson = 512 * 1024;
        private static PresenceHostBehaviour _active;
        private readonly HashSet<int> _joined = new();
        private readonly Dictionary<int, PresenceModelEntry> _models = new();
        private readonly Dictionary<int, PresenceStateMessage> _latestStates = new();
        private readonly Dictionary<int, PresenceHoldMessage> _latestHolds = new();
        private CoopRuntimeContext _context;
        private PresenceService _service;
        private float _stateTimer;
        private bool _shutdown;

        internal static PresenceHostBehaviour Active => _active;

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
                CoopPlugin.Log.LogError("Presence host initialization failed: " + error);
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

            using (CardShopCoop.Util.PerfProbe.Sample("module.presence.host-update"))
            {
                UpdateInner();
            }
        }

        private void UpdateInner()
        {
            _service.Tick(Time.deltaTime);
            if (_context.InGame())
            {
                PublishLocalState(Time.deltaTime);
            }
        }

        [OnFullyJoined]
        private void SendJoinBaseline(PeerConnection connection)
        {
            if (_shutdown || connection == null)
            {
                return;
            }

            _joined.Add(connection.Id);
            ApplyPeerName(connection.Id);
            SendBaseline(connection.Id);
            BroadcastRosterDelta(new PresenceRosterDeltaMessage
            {
                Kind = PresenceEntityDeltaKind.Add,
                Id = connection.Id,
                Entry = BuildRosterEntry(connection.Id),
            }, connection.Id);
        }

        [OnClientDisconnected]
        private void ForgetConnection(PeerConnection connection, DisconnectInfo _)
        {
            if (connection == null)
            {
                return;
            }

            _joined.Remove(connection.Id);
            _models.Remove(connection.Id);
            _latestStates.Remove(connection.Id);
            _latestHolds.Remove(connection.Id);
            _context?.PeerPresence.Clear(connection.Id);
            _service?.Remove(connection.Id);
            BroadcastRosterDelta(new PresenceRosterDeltaMessage
            {
                Kind = PresenceEntityDeltaKind.Remove,
                Id = connection.Id,
            });
            BroadcastModelDelta(new PresenceModelDeltaMessage
            {
                Kind = PresenceEntityDeltaKind.Remove,
                Id = connection.Id,
                PredictionId = Guid.Empty,
            });
        }

        [MessageHandler(typeof(PresenceStateMessage))]
        private void HandleState(MessageContext context, PresenceStateMessage message)
        {
            if (!IsAuthenticatedSender(context) || message == null)
            {
                return;
            }

            _context.PeerPresence.RecordAuthenticatedPosition(context.Connection, message.Position);
            _service.ApplyState(context.Connection.Id, message);
            _latestStates[context.Connection.Id] = CloneState(message);

            var relay = new PresenceRelayStateMessage
            {
                SenderId = context.Connection.Id,
                State = CloneState(message),
            };
            foreach (var connectionId in ConnectionIds())
            {
                if (connectionId != context.Connection.Id && _joined.Contains(connectionId))
                {
                    _context.Send(connectionId, relay);
                }
            }
        }

        [MessageHandler(typeof(PresenceHoldMessage))]
        private void HandleHold(MessageContext context, PresenceHoldMessage message)
        {
            if (!IsAuthenticatedSender(context) || message == null)
            {
                return;
            }

            _context.PeerPresence.RecordAuthenticatedHold(context.Connection, message.Hold,
                message.HoldTypes);
            _service.ApplyHold(context.Connection.Id, message);
            _latestHolds[context.Connection.Id] = CloneHold(message);

            var relay = new PresenceRelayHoldMessage
            {
                SenderId = context.Connection.Id,
                Hold = CloneHold(message),
            };
            foreach (var connectionId in ConnectionIds())
            {
                if (connectionId != context.Connection.Id && _joined.Contains(connectionId))
                {
                    _context.Send(connectionId, relay);
                }
            }
        }

        [MessageHandler(typeof(PresenceModelRequestMessage))]
        private void HandleModelRequest(MessageContext context, PresenceModelRequestMessage message)
        {
            if (!IsAuthenticatedSender(context) || message == null)
            {
                return;
            }

            if (message.ModelIndex < 0 || message.CustomizationJson?.Length > MaxCustomizationJson)
            {
                Reject(context, message.PredictionId);
                return;
            }

            var kind = _models.ContainsKey(context.Connection.Id)
                ? PresenceEntityDeltaKind.Update
                : PresenceEntityDeltaKind.Add;

            var model = new PresenceModelEntry
            {
                Id = context.Connection.Id,
                Female = message.Female,
                ModelIndex = message.ModelIndex,
                CustomizationJson = message.CustomizationJson,
            };
            _models[context.Connection.Id] = Clone(model);
            _service.SetModel(context.Connection.Id, model);
            CoopPlugin.Log.LogInfo("appearance update from player " + context.Connection.Id + ": "
                + (model.Female ? "female" : "male") + " " + model.ModelIndex);
            BroadcastModelDelta(new PresenceModelDeltaMessage
            {
                Kind = kind,
                Id = context.Connection.Id,
                Entry = Clone(model),
                PredictionId = message.PredictionId,
            });
        }

        [MessageHandler(typeof(PresenceEmoteMessage))]
        private void HandleEmote(MessageContext context, PresenceEmoteMessage message)
        {
            if (!IsAuthenticatedSender(context) || message == null)
            {
                return;
            }

            _service.ShowEmote(context.Connection.Id);
            RelayTag(context.Connection.Id, 0, default(EItemType));
        }

        [MessageHandler(typeof(PresenceActivityMessage))]
        private void HandleActivity(MessageContext context, PresenceActivityMessage message)
        {
            if (!IsAuthenticatedSender(context) || message == null)
            {
                return;
            }

            _service.ShowActivity(context.Connection.Id, (int)message.Pack);
            RelayTag(context.Connection.Id, 1, message.Pack);
        }

        internal bool TryGetPeerPresence(int connectionId, out PeerPresence presence)
        {
            presence = default(PeerPresence);
            return _context != null && _context.PeerPresence.TryGet(connectionId, out presence);
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

            _joined.Clear();
            _models.Clear();
            _latestStates.Clear();
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        private void SendBaseline(int connectionId)
        {
            _context.Send(connectionId, BuildRoster(connectionId));
            _context.Send(connectionId, BuildModelState());
            if (_latestStates.TryGetValue(0, out var hostState))
            {
                _context.Send(connectionId, CloneState(hostState));
            }

            foreach (var pair in _latestStates)
            {
                if (pair.Key == 0 || pair.Key == connectionId || !_joined.Contains(pair.Key))
                {
                    continue;
                }

                _context.Send(connectionId, new PresenceRelayStateMessage
                {
                    SenderId = pair.Key,
                    State = CloneState(pair.Value),
                });
            }

            if (_latestHolds.TryGetValue(0, out var hostHold))
            {
                _context.Send(connectionId, CloneHold(hostHold));
            }

            foreach (var pair in _latestHolds)
            {
                if (pair.Key == 0 || pair.Key == connectionId || !_joined.Contains(pair.Key))
                {
                    continue;
                }

                _context.Send(connectionId, new PresenceRelayHoldMessage
                {
                    SenderId = pair.Key,
                    Hold = CloneHold(pair.Value),
                });
            }
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
            _latestStates[0] = CloneState(state);
            _context.Broadcast(state);
            PublishLocalHold();
        }

        /// <summary>Broadcasts the host's hold appearance only when it changes, on the reliable
        /// lane. The 15 Hz transform broadcast stays fixed-size.</summary>
        private void PublishLocalHold()
        {
            if (!_service.TryConsumeLocalHoldChange(out var hold))
            {
                return;
            }

            _latestHolds[0] = CloneHold(hold);
            _context.Broadcast(hold);
        }

        private void OnLocalModelChanged(PresenceModelEntry model)
        {
            if (model == null || _shutdown)
            {
                return;
            }

            model.Id = 0;
            _models[0] = Clone(model);
            if (_context.InGame())
            {
                BroadcastModelDelta(new PresenceModelDeltaMessage
                {
                    Kind = PresenceEntityDeltaKind.Update,
                    Id = 0,
                    Entry = Clone(model),
                    PredictionId = Guid.Empty,
                });
            }
        }

        private PresenceModelStateMessage BuildModelState()
        {
            var local = _service.GetLocalModel();
            local.Id = 0;
            _models[0] = Clone(local);
            var state = new PresenceModelStateMessage();
            foreach (var pair in _models)
            {
                var entry = Clone(pair.Value);
                entry.Id = pair.Key;
                state.Entries.Add(entry);
            }

            return state;
        }

        private PresenceRosterMessage BuildRoster(int selfId = 0)
        {
            var message = new PresenceRosterMessage { SelfId = selfId };
            var ids = ConnectionIds();
            for (var i = 0; i < ids.Count; i++)
            {
                var id = ids[i];
                if (!_joined.Contains(id))
                {
                    continue;
                }

                var name = _context.PeerName?.Invoke(id);
                message.Entries.Add(BuildRosterEntry(id, name));
            }

            return message;
        }

        private PresenceRosterEntry BuildRosterEntry(int id, string name = null)
        {
            name ??= _context.PeerName?.Invoke(id);
            return new PresenceRosterEntry
            {
                Id = id,
                Name = string.IsNullOrEmpty(name) ? "Player " + id : name,
                SteamId = 0,
            };
        }

        /// <summary>Hands the host-authoritative peer name to the local renderer. The renderer
        /// keeps its own per-avatar name and defaults it to "Player", so without this the host's
        /// view of a remote player falls back to "Player" even though the roster and HUD know the
        /// real name. The name is captured once at join, before any state is admitted, matching
        /// the wire lifetime of a peer name.</summary>
        private void ApplyPeerName(int connectionId)
        {
            var name = _context.PeerName?.Invoke(connectionId);
            if (!string.IsNullOrEmpty(name))
            {
                _service.SetName(connectionId, name);
            }
        }

        private void BroadcastRosterDelta(PresenceRosterDeltaMessage delta,
            int excludedConnectionId = -1)
        {
            if (!_shutdown)
            {
                foreach (var connectionId in ConnectionIds())
                {
                    if (connectionId != excludedConnectionId && _joined.Contains(connectionId))
                    {
                        _context.Send(connectionId, delta);
                    }
                }
            }
        }

        private void BroadcastModelDelta(PresenceModelDeltaMessage delta,
            int excludedConnectionId = -1)
        {
            if (_shutdown)
            {
                return;
            }

            foreach (var connectionId in ConnectionIds())
            {
                if (connectionId != excludedConnectionId && _joined.Contains(connectionId))
                {
                    _context.Send(connectionId, delta);
                }
            }
        }

        private void Reject(MessageContext context, Guid predictionId)
        {
            if (predictionId != Guid.Empty && context?.Connection != null)
            {
                PredictionApi.Rollback(_context, context.Connection.Id, predictionId);
            }
        }

        private void RelayTag(int senderId, byte kind, EItemType extra)
        {
            var tag = new PresenceRelayTagMessage { SenderId = senderId, Kind = kind, Extra = extra };
            foreach (var connectionId in ConnectionIds())
            {
                if (connectionId != senderId && _joined.Contains(connectionId))
                {
                    _context.Send(connectionId, tag);
                }
            }
        }

        private bool IsAuthenticatedSender(MessageContext context)
        {
            return !_shutdown && _context.InGame()
                && context?.Connection != null && _joined.Contains(context.Connection.Id)
                && context.IsAuthenticated;
        }

        private IReadOnlyList<int> ConnectionIds()
        {
            return _context.ConnectionIds?.Invoke() ?? new List<int>();
        }

        private static PresenceStateMessage CloneState(PresenceStateMessage state)
        {
            return state == null
                ? null
                : new PresenceStateMessage
                {
                    Position = state.Position,
                    Yaw = state.Yaw,
                    CameraPosition = state.CameraPosition,
                    CameraRotation = state.CameraRotation,
                    Speed = state.Speed,
                };
        }

        private static PresenceHoldMessage CloneHold(PresenceHoldMessage hold)
        {
            return hold == null
                ? null
                : new PresenceHoldMessage
                {
                    Hold = hold.Hold,
                    HoldTypes = hold.HoldTypes == null ? null : new List<int>(hold.HoldTypes),
                    HoldCards = hold.HoldCards == null ? null : new List<CardData>(hold.HoldCards),
                };
        }

        private static PresenceModelEntry Clone(PresenceModelEntry model)
        {
            return model == null
                ? new PresenceModelEntry()
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
