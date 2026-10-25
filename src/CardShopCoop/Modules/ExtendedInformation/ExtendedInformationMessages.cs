using System.Collections.Generic;
using CardShopCoop.Modules.GameObjectIdentity;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.ExtendedInformation
{
    /// <summary>Host-authoritative set/replace of one extended-information entry.</summary>
    [NetworkMessage]
    public sealed class ExtendedInformationSetMessage : INetMessage
    {
        public GameObjectId Id;
        public ExtendedInformationValue Value;
    }

    /// <summary>Host-authoritative removal of one extended-information entry.</summary>
    [NetworkMessage]
    public sealed class ExtendedInformationRemovedMessage : INetMessage
    {
        public GameObjectId Id;
        public ushort TypeId;
    }

    /// <summary>Client request to change one entry. The host validates, applies and echoes.</summary>
    [NetworkMessage]
    public sealed class ExtendedInformationIntentMessage : INetMessage
    {
        public const byte Set = 1;
        public const byte Remove = 2;

        public byte Operation;
        public GameObjectId Id;
        public ushort TypeId;
        public ExtendedInformationValue Value;
    }

    /// <summary>Complete extended-information baseline sent once when a peer joins.</summary>
    [NetworkMessage]
    public sealed class ExtendedInformationBaselineMessage : INetMessage
    {
        public List<ExtendedInformationEntry> Entries = new();
    }

    public sealed class ExtendedInformationEntry
    {
        public GameObjectId Id;
        public ExtendedInformationValue Value;
    }
}
