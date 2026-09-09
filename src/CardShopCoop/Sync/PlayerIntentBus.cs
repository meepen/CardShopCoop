using System;
using System.Collections.Generic;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;

namespace CardShopCoop.Sync
{
    /// <summary>Small host-authoritative bus for single-shot shared-world intents.
    /// It deliberately does not replace request/response, lease, or held-item protocols.</summary>
    public sealed class PlayerIntentBus
    {
        public Action<INetMessage> SendOp;
        private readonly Dictionary<int, Action<PlayerIntentMessage>> _handlers
            = new Dictionary<int, Action<PlayerIntentMessage>>();

        public void Register(byte kind, byte action, Action<PlayerIntentMessage> handler)
        {
            if (handler == null) throw new ArgumentNullException("handler");
            _handlers[Key(kind, action)] = handler;
        }

        public bool TrySend(byte kind, byte action, byte target, int objectKey = 0,
            float floatA = 0f, int intA = 0, string json = null)
        {
            if (CoopCore.Role != CoopRole.Client || SendOp == null) return false;
            SendOp(new PlayerIntentMessage
            {
                Kind = kind, Action = action, Target = target, ObjectKey = objectKey,
                FloatA = floatA, IntA = intA, Json = json
            });
            return true;
        }

        public void HostApplyOp(PlayerIntentMessage message)
        {
            if (message == null) return;
            if (_handlers.TryGetValue(Key(message.Kind, message.Action), out var handler))
                handler(message);
            else
                CoopPlugin.Log.LogWarning($"PlayerIntentBus: unhandled intent {message.Kind}/{message.Action}");
        }

        private static int Key(byte kind, byte action) { return (kind << 8) | action; }
    }
}
