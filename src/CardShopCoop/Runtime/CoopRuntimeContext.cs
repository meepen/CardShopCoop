using System;
using System.Collections.Generic;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Modules.Presence;

namespace CardShopCoop.Runtime
{
    public sealed class CoopRuntimeContext
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
    }
}
