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
    /// <summary>Host authority for roster, appearance, and authenticated peer presence.</summary>
    [ServerBehaviour]
    public sealed class PresenceHostBehaviour : CoopBehaviour
    {
        private const int MaxCustomizationJson = 512 * 1024;
        private static PresenceHostBehaviour _active;
        // Canonical player id -> the peer that sends/receives for it. The id is wire payload
        // identity for the roster and model entries; sends always address the peer handle.
        private readonly Dictionary<int, ICoopPeer> _joined = new();
        private readonly Dictionary<int, PresenceModelEntry> _models = new();
        private readonly Dictionary<int, PresenceStateMessage> _latestStates = new();
        private readonly Dictionary<int, PresenceHoldMessage> _latestHolds = new();
        private PresenceService _service;
        private float _stateTimer;
        private bool _shutdown;

        internal static PresenceHostBehaviour Active => _active;

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

            using (Util.PerfProbe.Sample("module.presence.host-update"))
            {
                UpdateInner();
            }
        }

        private void UpdateInner()
        {
            _service.Tick(Time.deltaTime);
            if (RuntimeContext.InGame())
            {
                PublishLocalState(Time.deltaTime);
            }
        }

        [OnFullyJoined]
        private void SendJoinBaseline(PeerConnection connection)
        {
            var peer = PeerHandle.ForConnection(connection);
            if (_shutdown || peer == null)
            {
                return;
            }

            var id = PeerId(peer);
            _joined[id] = peer;
            ApplyPeerName(id);
            SendBaseline(peer);
            SendToJoined(new PresenceRosterDeltaMessage
            {
                Kind = PresenceEntityDeltaKind.Add,
                Id = id,
                Entry = BuildRosterEntry(id),
            }, peer);
            PresenceApi.NotifyRosterChanged();
        }

        [OnClientDisconnected]
        private void ForgetConnection(PeerConnection connection, DisconnectInfo _)
        {
            if (connection == null)
            {
                return;
            }

            var id = connection.Id;
            _joined.Remove(id);
            _models.Remove(id);
            _latestStates.Remove(id);
            _latestHolds.Remove(id);
            RuntimeContext.PeerPresence.Clear(id);
            _service?.Remove(id);
            SendToJoined(new PresenceRosterDeltaMessage
            {
                Kind = PresenceEntityDeltaKind.Remove,
                Id = id,
            });
            SendToJoined(new PresenceModelDeltaMessage
            {
                Kind = PresenceEntityDeltaKind.Remove,
                Id = id,
            });
            PresenceApi.NotifyRosterChanged();
        }

        [MessageHandler]
        private void HandleState(MessageContext context, PresenceStateMessage message)
        {
            var peer = context?.Peer;
            if (message == null || !IsAuthenticatedSender(peer, context?.IsAuthenticated == true))
            {
                return;
            }

            var id = PeerId(peer);
            RuntimeContext.PeerPresence.RecordAuthenticatedPosition(peer, message.Position);
            _service.ApplyState(id, message);
            _latestStates[id] = CloneState(message);
            SendToJoined(new PresenceRelayStateMessage
            {
                SenderId = id,
                State = CloneState(message),
            }, peer);
        }

        [MessageHandler]
        private void HandleHold(MessageContext context, PresenceHoldMessage message)
        {
            var peer = context?.Peer;
            if (message == null || !IsAuthenticatedSender(peer, context?.IsAuthenticated == true))
            {
                return;
            }

            var id = PeerId(peer);
            RuntimeContext.PeerPresence.RecordAuthenticatedHold(peer, message.Hold,
                message.HoldTypes);
            _service.ApplyHold(id, message);
            _latestHolds[id] = CloneHold(message);
            SendToJoined(new PresenceRelayHoldMessage
            {
                SenderId = id,
                Hold = CloneHold(message),
            }, peer);
        }

        [MessageHandler]
        private ActionOutcome HandleModelRequest(CoopMessageContext context,
            PresenceModelRequestMessage message)
        {
            var peer = context?.Peer;
            if (message == null || !IsAuthenticatedSender(peer, context?.IsAuthenticated == true))
            {
                return ActionOutcome.Reject("appearance request from an unauthenticated sender");
            }

            if (message.ModelIndex < 0 || message.CustomizationJson?.Length > MaxCustomizationJson)
            {
                return RejectModel(peer, "appearance payload is outside the allowed range");
            }

            var id = PeerId(peer);
            var kind = _models.ContainsKey(id)
                ? PresenceEntityDeltaKind.Update
                : PresenceEntityDeltaKind.Add;

            var model = new PresenceModelEntry
            {
                Id = id,
                Female = message.Female,
                ModelIndex = message.ModelIndex,
                CustomizationJson = message.CustomizationJson,
            };
            _models[id] = Clone(model);
            _service.SetModel(id, model);
            CoopPlugin.Log.LogInfo("appearance update from player " + id + ": "
                + (model.Female ? "female" : "male") + " " + model.ModelIndex);
            SendToJoined(new PresenceModelDeltaMessage
            {
                Kind = kind,
                Id = id,
                Entry = Clone(model),
            }, peer);
            return ActionOutcome.Applied(new PresenceModelResponseMessage
            {
                Entry = Clone(model),
            });
        }

        /// <summary>Answers a refused appearance intent with the host's current appearance for
        /// the actor, if the host has one. The client's undo stands when it has none.</summary>
        private ActionOutcome RejectModel(ICoopPeer peer, string reason)
        {
            var id = PeerId(peer);
            if (_models.TryGetValue(id, out var stored) && stored != null)
            {
                return ActionOutcome.Reject(reason, new PresenceModelResponseMessage
                {
                    Entry = Clone(stored),
                });
            }

            return ActionOutcome.Reject(reason);
        }

        [MessageHandler]
        private void HandleEmote(MessageContext context, PresenceEmoteMessage message)
        {
            var peer = context?.Peer;
            if (message == null || !IsAuthenticatedSender(peer, context?.IsAuthenticated == true))
            {
                return;
            }

            var id = PeerId(peer);
            _service.ShowEmote(id);
            SendToJoined(new PresenceRelayTagMessage
            {
                SenderId = id,
                Kind = 0,
                Extra = default(EItemType),
            }, peer);
        }

        [MessageHandler]
        private void HandleActivity(MessageContext context, PresenceActivityMessage message)
        {
            var peer = context?.Peer;
            if (message == null || !IsAuthenticatedSender(peer, context?.IsAuthenticated == true))
            {
                return;
            }

            var id = PeerId(peer);
            _service.ShowActivity(id, (int)message.Pack);
            SendToJoined(new PresenceRelayTagMessage
            {
                SenderId = id,
                Kind = 1,
                Extra = message.Pack,
            }, peer);
        }

        internal bool TryGetPeerPresence(int connectionId, out PeerPresence presence)
        {
            presence = default(PeerPresence);
            return RuntimeContext.PeerPresence.TryGet(connectionId, out presence);
        }

        /// <summary>A copy of one session member's appearance, keyed by the host's canonical
        /// connection id (0 is this host). Used only by the diagnostics report, which must never
        /// read a live model object while the renderer owns it.</summary>
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

        /// <summary>The session roster for the window: this host first, then each joined peer in id
        /// order. Ids are the host's canonical player ids (0 is this host).</summary>
        internal IReadOnlyList<PresencePlayer> BuildPlayers()
        {
            var players = new List<PresencePlayer>(_joined.Count + 1);
            players.Add(new PresencePlayer(0, PresenceApi.LocalPlayerName, true, true));

            var ids = new List<int>(_joined.Keys);
            ids.Sort();
            for (var i = 0; i < ids.Count; i++)
            {
                var id = ids[i];
                var name = RuntimeContext.PeerName?.Invoke(id);
                players.Add(new PresencePlayer(id,
                    string.IsNullOrEmpty(name) ? "Player " + id : name, false, false));
            }

            return players;
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

            _joined.Clear();
            _models.Clear();
            _latestStates.Clear();
            _latestHolds.Clear();
        }

        private void OnDestroy() => Shutdown();

        private void SendBaseline(ICoopPeer peer)
        {
            var id = PeerId(peer);
            peer.Send(BuildRoster(id));
            peer.Send(BuildModelState());
            if (_latestStates.TryGetValue(0, out var hostState))
            {
                peer.Send(CloneState(hostState));
            }

            foreach (var pair in _latestStates)
            {
                if (pair.Key == 0 || pair.Key == id || !_joined.ContainsKey(pair.Key))
                {
                    continue;
                }

                peer.Send(new PresenceRelayStateMessage
                {
                    SenderId = pair.Key,
                    State = CloneState(pair.Value),
                });
            }

            if (_latestHolds.TryGetValue(0, out var hostHold))
            {
                peer.Send(CloneHold(hostHold));
            }

            foreach (var pair in _latestHolds)
            {
                if (pair.Key == 0 || pair.Key == id || !_joined.ContainsKey(pair.Key))
                {
                    continue;
                }

                peer.Send(new PresenceRelayHoldMessage
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
            SendToJoined(state);
            PublishLocalHold();
        }

        /// <summary>Sends the host's hold appearance only when it changes, on the reliable lane.
        /// The 15 Hz transform stream stays fixed-size.</summary>
        private void PublishLocalHold()
        {
            if (!_service.TryConsumeLocalHoldChange(out var hold))
            {
                return;
            }

            _latestHolds[0] = CloneHold(hold);
            SendToJoined(hold);
        }

        private void OnLocalModelChanged(PresenceModelEntry model)
        {
            if (model == null || _shutdown)
            {
                return;
            }

            model.Id = 0;
            _models[0] = Clone(model);
            if (RuntimeContext.InGame())
            {
                SendToJoined(new PresenceModelDeltaMessage
                {
                    Kind = PresenceEntityDeltaKind.Update,
                    Id = 0,
                    Entry = Clone(model),
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
            var ids = new List<int>(_joined.Keys);
            ids.Sort();
            for (var i = 0; i < ids.Count; i++)
            {
                var id = ids[i];
                message.Entries.Add(BuildRosterEntry(id));
            }

            return message;
        }

        private PresenceRosterEntry BuildRosterEntry(int id, string name = null)
        {
            name ??= RuntimeContext.PeerName?.Invoke(id);
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
        private void ApplyPeerName(int id)
        {
            var name = RuntimeContext.PeerName?.Invoke(id);
            if (!string.IsNullOrEmpty(name))
            {
                _service.SetName(id, name);
            }
        }

        /// <summary>Sends to every other joined peer through its handle. Presence is advisory
        /// state, so only peers that already completed their baseline receive stream updates.</summary>
        private void SendToJoined(INetMessage message, ICoopPeer except = null)
        {
            if (_shutdown)
            {
                return;
            }

            foreach (var peer in _joined.Values)
            {
                if (except != null && peer.Equals(except))
                {
                    continue;
                }

                peer.Send(message);
            }
        }

        private static int PeerId(ICoopPeer peer)
            => peer is PeerHandle handle ? handle.CanonicalId : peer?.Player?.Id ?? 0;

        private bool IsAuthenticatedSender(ICoopPeer peer, bool authenticated)
        {
            return !_shutdown && RuntimeContext.InGame() && authenticated && peer != null
                && _joined.ContainsKey(PeerId(peer));
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
