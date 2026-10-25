using CardShopCoop.Net.Connection;
using CardShopCoop.Net.Protocol;

namespace CardShopCoop.Net
{
    /// <summary>
    /// One complete application frame whose envelope has already been validated off the Unity
    /// thread. The JSON payload is deliberately not decoded yet: entity-bearing DTOs must be
    /// deserialized on the Unity main thread so their converters can resolve live scene objects.
    /// </summary>
    public sealed class WireFrame
    {
        internal PeerConnection Connection;
        internal MessageDescriptor Descriptor;
        internal byte[] Frame;
        internal int PayloadOffset;
        internal int PayloadLength;
        internal int EncodedBytes;
        internal bool Reliable;
        internal bool BeforeActivation;
    }

    /// <summary>Transport seam for frame-level delivery. Transports that implement this keep
    /// JSON decoding on the Unity thread; the legacy decoded-message seam remains for transports
    /// that cannot.</summary>
    internal interface IWireFrameTransport
    {
        bool TryDequeueWireFrame(out WireFrame frame);
    }
}
