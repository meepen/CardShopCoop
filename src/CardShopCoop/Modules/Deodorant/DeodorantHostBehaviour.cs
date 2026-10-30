using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Net;
using CardShopCoop.Runtime;
using CoopBehaviour = CardShopCoop.Runtime.CoopBehaviour;
using UnityEngine;
using System;

namespace CardShopCoop.Modules.Deodorant
{
    [ServerBehaviour]
    public sealed class DeodorantHostBehaviour : CoopBehaviour
    {
        private bool _shutdown;

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
        }
        private void OnDestroy() => Shutdown();

        [MessageHandler]
        private ActionOutcome Handle(MessageContext context, HandheldSprayIntentMessage message)
        {
            if (!IsAuthenticatedSender(context) || message == null)
            {
                return ActionOutcome.Reject("spray intent from an unauthenticated sender");
            }

            if (!IsFinite(message.Position.x) || !IsFinite(message.Position.y)
                || !IsFinite(message.Position.z) || !IsFinite(message.Content)
                || message.Content < 0f || message.Content > 1f)
            {
                return ActionOutcome.Reject("spray position or content is outside the allowed range");
            }
            if (!RuntimeContext.PeerPresence.TryGet(context.Connection.Id, out var presence))
            {
                return ActionOutcome.Reject("no authenticated presence for the spray intent");
            }
            if (presence.Age > TimeSpan.FromSeconds(2) || presence.Hold != 2
                || !HasDeodorant(presence.HoldTypes))
            {
                return ActionOutcome.Reject(
                    "spray source is not holding a deodorant in a fresh presence sample");
            }
            if (Vector3.Distance(presence.Position, message.Position) > 3.5f)
            {
                return ActionOutcome.Reject("spray source is too far from the spray position");
            }

            var manager = GetCustomerManager();
            if (manager == null)
            {
                return ActionOutcome.Reject("customer manager is not available on the host");
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

            RuntimeContext.BroadcastExcept(message.Peer, new HandheldSprayEventMessage
            {
                Position = message.Position,
                Content = message.Content,
                Customers = states,
            });
            return ActionOutcome.Applied(new HandheldSprayResponseMessage
            {
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
