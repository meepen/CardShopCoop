using System;
using System.Collections.Generic;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Modules.Presence;
using CardShopCoop.Api;

namespace CardShopCoop.Runtime
{
    public sealed class CoopRuntimeContext : ICoopContext
    {
        public MessageRouter Messages
        {
            get;
        }
        public Func<bool> InGame
        {
            get;
        }
        public Action<INetMessage> Broadcast
        {
            get;
        }
        public Action<int, INetMessage> Send
        {
            get;
        }
        public Func<IReadOnlyList<int>> ConnectionIds
        {
            get;
        }
        public Func<int, string> PeerName
        {
            get;
        }
        public Action<string, float> SetStatusLine
        {
            get;
        }
        public Func<bool> PreloadHold
        {
            get;
        }
        public Action<int, DisconnectInfo> Disconnect
        {
            get;
        }
        public IPeerPresence PeerPresence
        {
            get;
        }

        public CoopRuntimeContext(MessageRouter messages, Func<bool> inGame,
            Action<INetMessage> broadcast, Action<int, INetMessage> send,
            Func<IReadOnlyList<int>> connectionIds = null,
            Func<int, string> peerName = null,
            Action<string, float> setStatusLine = null,
            Func<bool> preloadHold = null,
            Action<int, DisconnectInfo> disconnect = null)
        {
            Messages = messages ?? throw new ArgumentNullException(nameof(messages));
            InGame = inGame ?? throw new ArgumentNullException(nameof(inGame));
            Broadcast = broadcast ?? throw new ArgumentNullException(nameof(broadcast));
            Send = send ?? throw new ArgumentNullException(nameof(send));
            ConnectionIds = connectionIds;
            PeerName = peerName;
            SetStatusLine = setStatusLine;
            PreloadHold = preloadHold;
            Disconnect = disconnect;
            PeerPresence = new PeerPresenceProvider(TimeSpan.FromSeconds(10));
        }

        // ICoopContext is the public view of this live session. Explicit implementations keep the
        // built-in rich members (Func/Action fields, MessageRouter) untouched for internal modules.
        bool ICoopContext.IsHost => CoopCore.Role == CoopRole.Host;
        bool ICoopContext.IsClient => CoopCore.Role == CoopRole.Client;
        bool ICoopContext.InSession => CoopCore.Role != CoopRole.None;
        bool ICoopContext.InGame => InGame != null && InGame();
        int ICoopContext.LocalConnectionId => CoopCore.LocalConnectionId;
        IReadOnlyList<int> ICoopContext.ConnectionIds => ConnectionIds == null
            ? Array.Empty<int>() : ConnectionIds();
        string ICoopContext.PeerName(int connectionId)
            => PeerName == null ? null : PeerName(connectionId);
        ICoopMessageRegistry ICoopContext.Messages => Messages;
        void ICoopContext.Send(int connectionId, INetMessage message) => Send(connectionId, message);
        void ICoopContext.Broadcast(INetMessage message) => Broadcast(message);
    }
}
