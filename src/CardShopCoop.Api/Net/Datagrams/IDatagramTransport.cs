using System;

namespace CardShopCoop.Net.Datagrams
{
    /// <summary>
    /// Backend-neutral identity for one datagram peer. The numeric token is allocated by the
    /// bearer and has meaning only for that bearer's lifetime; it is never a network identity
    /// and is never sent on the wire.
    /// </summary>
    public readonly struct DatagramPeer : IEquatable<DatagramPeer>
    {
        /// <summary>Creates a bearer-local peer token. The bearer must allocate a unique,
        /// positive token for each peer and keep it stable for that peer's lifetime.</summary>
        public DatagramPeer(long token)
        {
            if (token <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(token));
            }

            Token = token;
        }

        /// <summary>The bearer-local token. It identifies a peer inside one bearer, not a
        /// session player or a network address.</summary>
        public long Token
        {
            get;
        }

        public bool IsValid => Token > 0;

        public bool Equals(DatagramPeer other) => Token == other.Token;

        public override bool Equals(object obj) => obj is DatagramPeer other && Equals(other);

        public override int GetHashCode() => Token.GetHashCode();

        public static bool operator ==(DatagramPeer left, DatagramPeer right) => left.Equals(right);

        public static bool operator !=(DatagramPeer left, DatagramPeer right) => !left.Equals(right);
    }

    /// <summary>
    /// The datagram bearer a third-party transport provides. The runtime owns reliability,
    /// framing, peer identity, and authorization; the bearer only moves complete datagrams and
    /// reports peer availability/closure.
    ///
    /// Threading: <see cref="Start"/>, <see cref="Poll"/>, <see cref="Send"/>,
    /// <see cref="Close"/>, <see cref="Stop"/>, and <see cref="IDisposable.Dispose"/> are called on the
    /// network pump thread. <see cref="PeerAvailable"/> and <see cref="PeerClosed"/> may be
    /// raised from any thread. <see cref="Send"/> must consume or copy the segment before it
    /// returns; it may not retain the caller's array. Segments passed to the receive callback
    /// are valid only for the duration of that callback.
    /// </summary>
    public interface IDatagramTransport : IDisposable
    {
        /// <summary>Largest complete datagram the bearer accepts. The session never sends a
        /// datagram larger than this value.</summary>
        int MaxDatagramSize
        {
            get;
        }

        bool IsRunning
        {
            get;
        }

        /// <summary>A peer is now reachable. The session creates its own peer identity for this
        /// bearer token; the bearer must not assign session player ids.</summary>
        event Action<DatagramPeer> PeerAvailable;

        /// <summary>A peer is gone, with a bounded, player-readable reason when one is known.</summary>
        event Action<DatagramPeer, string> PeerClosed;

        /// <summary>Start the bearer. Called once, on the network pump thread.</summary>
        void Start();

        /// <summary>Delivers at most <paramref name="maxDatagrams"/> complete datagrams
        /// synchronously. Called on the network pump thread.</summary>
        int Poll(int maxDatagrams, Action<DatagramPeer, ArraySegment<byte>> receive);

        /// <summary>Sends one complete datagram. Called on the network pump thread.</summary>
        void Send(DatagramPeer peer, ArraySegment<byte> datagram);

        /// <summary>Closes one peer, with a bounded reason when one is known.</summary>
        void Close(DatagramPeer peer, string reason);

        /// <summary>Stops accepting and delivering datagrams. Idempotent.</summary>
        void Stop();
    }
}
