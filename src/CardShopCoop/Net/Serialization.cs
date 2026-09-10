using System.IO;

namespace CardShopCoop.Net
{
    /// <summary>Protocol-owned serialization primitives. Keeping these types separate
    /// from System.IO makes it impossible for gameplay code to accidentally become a
    /// stream/framing implementation.</summary>
    public sealed class NetWriter : BinaryWriter
    {
        public NetWriter(Stream stream) : base(stream) { }
    }

    public sealed class NetReader : BinaryReader
    {
        public NetReader(Stream stream) : base(stream) { }
    }
}
