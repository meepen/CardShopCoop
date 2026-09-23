using System;

namespace CardShopCoop.Net.Datagrams
{
    /// <summary>
    /// Backend-neutral identity for one datagram peer. The numeric token is allocated by the
    /// backend and has meaning only for that backend's lifetime; it is never a network identity.
    /// </summary>
    public readonly struct DatagramPeer : IEquatable<DatagramPeer>
    {
        internal readonly long Token;

        internal DatagramPeer(long token)
        {
            if (token <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(token));
            }

            Token = token;
        }

        public bool IsValid => Token > 0;

        public bool Equals(DatagramPeer other) => Token == other.Token;

        public override bool Equals(object obj) => obj is DatagramPeer other && Equals(other);

        public override int GetHashCode() => Token.GetHashCode();

        public static bool operator ==(DatagramPeer left, DatagramPeer right) => left.Equals(right);

        public static bool operator !=(DatagramPeer left, DatagramPeer right) => !left.Equals(right);
    }

    /// <summary>
    /// Complete datagram bearer used by KCP. Implementations preserve packet boundaries and keep
    /// endpoints, Steam identities, and native connection handles entirely behind this contract.
    /// Received segments are valid only for the duration of the callback passed to
    /// <see cref="Poll"/>.
    /// </summary>
    public interface IDatagramTransport : IDisposable
    {
        int MaxDatagramSize
        {
            get;
        }

        bool IsRunning
        {
            get;
        }

        event Action<DatagramPeer> PeerAvailable;
        event Action<DatagramPeer, string> PeerClosed;

        void Start();

        /// <summary>Delivers at most <paramref name="maxDatagrams"/> packets synchronously.</summary>
        int Poll(int maxDatagrams, Action<DatagramPeer, ArraySegment<byte>> receive);

        /// <summary>
        /// Sends one complete datagram. The implementation must consume or copy the segment before
        /// returning; it may not retain the caller's array.
        /// </summary>
        void Send(DatagramPeer peer, ArraySegment<byte> datagram);

        void Close(DatagramPeer peer, string reason);
        void Stop();
    }
}
