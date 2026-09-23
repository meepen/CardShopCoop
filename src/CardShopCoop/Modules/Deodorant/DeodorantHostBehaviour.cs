using CardShopCoop.Attributes;
using CardShopCoop.Modules.Npc;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Runtime;
using UnityEngine;
using System;

namespace CardShopCoop.Modules.Deodorant
{
    [ServerBehaviour]
    public sealed class DeodorantHostBehaviour : CoopBehaviour
    {
        private CoopRuntimeContext _context;
        private bool _shutdown;

        private void OnEnable()
        {
            if (_shutdown || _context != null)
            {
                return;
            }

            _context = RuntimeContext;
            var handlersRegistered = false;
            try
            {
                _context.Messages.RegisterAttributedHandlers(this);
                handlersRegistered = true;
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogError($"Deodorant host initialization failed: {error}");
                if (handlersRegistered)
                {
                    _context.Messages.UnregisterAttributedHandlers(this);
                }

                _context = null;
                throw;
            }
        }

        internal void Shutdown()
        {
            if (_shutdown)
                return;
            _shutdown = true;
            _context?.Messages.UnregisterAttributedHandlers(this);
            _context = null;
        }
        private void OnDestroy() => Shutdown();

        [MessageHandler(typeof(HandheldSprayIntentMessage))]
        private void Handle(MessageContext context, HandheldSprayIntentMessage message)
        {
            if (!IsAuthenticatedSender(context) || message == null)
                return;
            if (!IsFinite(message.Position.x) || !IsFinite(message.Position.y)
                || !IsFinite(message.Position.z) || !IsFinite(message.Content)
                || message.Content < 0f || message.Content > 1f)
            {
                Reject(context, message.PredictionId);
                return;
            }
            if (!_context.PeerPresence.TryGet(context.Connection.Id, out var presence))
            {
                Reject(context, message.PredictionId);
                return;
            }
            if (presence.Age > TimeSpan.FromSeconds(2) || presence.Hold != 2
                || !HasDeodorant(presence.HoldTypes))
            {
                Reject(context, message.PredictionId);
                return;
            }
            if (Vector3.Distance(presence.Position, message.Position) > 3.5f)
            {
                Reject(context, message.PredictionId);
                return;
            }

            var manager = GetCustomerManager();
            if (manager == null)
            {
                Reject(context, message.PredictionId);
                return;
            }
            var customers = manager.GetCustomerList();
            var states = new System.Collections.Generic.List<DeodorantCustomerState>();
            for (var i = 0; i < customers.Count; i++)
            {
                var customer = customers[i];
                if (customer == null)
                {
                    continue;
                }

                var wasSmelly = customer.IsSmelly();
                var previousMeter = DeodorantInterop.GetSmellyMeter(customer);
                customer.DeodorantSprayCheck(message.Position, 2.5f, 1);
                var isSmelly = customer.IsSmelly();
                var meter = DeodorantInterop.GetSmellyMeter(customer);
                if (wasSmelly == isSmelly && previousMeter == meter)
                {
                    continue;
                }

                states.Add(new DeodorantCustomerState
                {
                    Index = i,
                    Generation = NpcHostBehaviour.GetCustomerGeneration(customer),
                    IsSmelly = isSmelly,
                    SmellyMeter = meter,
                });
            }

            _context.Broadcast(new HandheldSprayEventMessage
            {
                PredictionId = message.PredictionId,
                Position = message.Position,
                Content = message.Content,
                Customers = states,
            });
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private CustomerManager GetCustomerManager()
            => SceneRef<CustomerManager>.Get();

        private bool IsAuthenticatedSender(MessageContext context)
        {
            return !_shutdown && _context.InGame() && context?.Connection != null
                && context.IsAuthenticated;
        }

        private void Reject(MessageContext context, Guid predictionId)
        {
            if (predictionId != Guid.Empty && context?.Connection != null)
            {
                PredictionApi.Rollback(_context, context.Connection.Id, predictionId);
            }
        }

        private static bool HasDeodorant(System.Collections.Generic.IReadOnlyList<int> holdTypes)
        {
            if (holdTypes == null)
                return false;
            for (var i = 0; i < holdTypes.Count; i++)
                if (holdTypes[i] == (int)EItemType.Deodorant)
                    return true;
            return false;
        }
    }
}
