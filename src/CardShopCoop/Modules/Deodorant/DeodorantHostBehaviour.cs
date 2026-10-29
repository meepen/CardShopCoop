using CardShopCoop.Attributes;
using CardShopCoop.Modules.Actions;
using CardShopCoop.Net;
using CardShopCoop.Runtime;
using UnityEngine;
using System;

namespace CardShopCoop.Modules.Deodorant
{
    [ServerBehaviour]
    public sealed class DeodorantHostBehaviour : CoopBehaviour
    {
        private bool _shutdown;

        private void OnEnable()
        {
            if (_shutdown)
            {
                return;
            }

            var handlersRegistered = false;
            try
            {
                RuntimeContext.Messages.RegisterAttributedHandlers(this);
                handlersRegistered = true;
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogError($"Deodorant host initialization failed: {error}");
                if (handlersRegistered)
                {
                    RuntimeContext.Messages.UnregisterAttributedHandlers(this);
                }

                throw;
            }
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            RuntimeContext.Messages.UnregisterAttributedHandlers(this);
        }
        private void OnDestroy() => Shutdown();

        [MessageHandler]
        private void Handle(MessageContext context, HandheldSprayIntentMessage message)
        {
            if (!IsAuthenticatedSender(context) || message == null)
            {
                return;
            }

            if (!IsFinite(message.Position.x) || !IsFinite(message.Position.y)
                || !IsFinite(message.Position.z) || !IsFinite(message.Content)
                || message.Content < 0f || message.Content > 1f)
            {
                Reject(context, message.ActionId);
                return;
            }
            if (!RuntimeContext.PeerPresence.TryGet(context.Connection.Id, out var presence))
            {
                Reject(context, message.ActionId);
                return;
            }
            if (presence.Age > TimeSpan.FromSeconds(2) || presence.Hold != 2
                || !HasDeodorant(presence.HoldTypes))
            {
                Reject(context, message.ActionId);
                return;
            }
            if (Vector3.Distance(presence.Position, message.Position) > 3.5f)
            {
                Reject(context, message.ActionId);
                return;
            }

            var manager = GetCustomerManager();
            if (manager == null)
            {
                Reject(context, message.ActionId);
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
                    Customer = customer,
                    IsSmelly = isSmelly,
                    SmellyMeter = meter,
                });
            }

            RuntimeContext.Broadcast(new HandheldSprayEventMessage
            {
                ActionId = message.ActionId,
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
            return !_shutdown && RuntimeContext.InGame() && context?.Connection != null
                && context.IsAuthenticated;
        }

        private void Reject(MessageContext context, Guid actionId)
        {
            if (actionId != Guid.Empty && context?.Connection != null)
            {
                ActionLedger.Rollback(RuntimeContext, context.Connection.Id, actionId);
            }
        }

        private static bool HasDeodorant(System.Collections.Generic.IReadOnlyList<int> holdTypes)
        {
            if (holdTypes == null)
            {
                return false;
            }

            for (var i = 0; i < holdTypes.Count; i++)
            {
                if (holdTypes[i] == (int)EItemType.Deodorant)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
