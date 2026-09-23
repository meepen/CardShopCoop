namespace CardShopCoop.Net.Messages
{
    // ---------------------------------------------------------------- handshake
    // Every field below is in WIRE ORDER, and each Serialize/Deserialize pair is the
    // single source of truth for its message's byte layout. The same shape is produced
    // and consumed by CoopCore (SendHello / SendWorldTo / the DTO-type switch in
    // Dispatch); these DTOs are the typed mirror of those payloads.

    /// <summary>client -> host. See CoopCore.SendHello and case HelloMessage.</summary>
    [NetworkMessage]
    public sealed class HelloMessage : INetMessage
    {
        public int WireVersion;
        public string Version;
        public string PlayerName;
        public string Password;
        public string PluginHash;
        public string EnumHash;
        public string CardsHash;
        public System.Collections.Generic.List<string> PluginList = new();
        public System.Collections.Generic.List<string> CardsList = new();
        public byte[] EnumBlob = new byte[0];
        public string GameVersion;
        public string UnityVersion;
        public ulong SteamId;
        public System.Collections.Generic.List<string> MessageCatalog = new();
        public byte[] MessageReliability = new byte[0];
    }

    /// <summary>host -> client. See CoopCore.SendWorldTo and case WelcomeMessage.</summary>
    [NetworkMessage]
    public sealed class WelcomeMessage : INetMessage
    {
        public int WireVersion;
        public string Version;
        public string HostName;
        public int SaveLength;
        public int HostSlot;
        public int BundleLength;
        public bool SidecarsComplete;
        public string SidecarWarning;
        // int, not byte: the host's connection ids are unbounded; a byte wrapped at 255.
        public int SelfId;
        public byte[] HostEnumBlob = new byte[0];
        public byte[] HostCardsBlob = new byte[0];
        public ulong SteamId;
        public System.Collections.Generic.List<string> MessageCatalog = new();
        public byte[] MessageReliability = new byte[0];
    }

    // ---------------------------------------------------------------- registry sync

    /// <summary>host -> client. See case EnumSyncMessage (the Data is the gzipped
    /// enum_values.json; the receiver applies CatalogHandshake's capped decompressor).</summary>
    [NetworkMessage]
    public sealed class EnumSyncMessage : INetMessage
    {
        public byte[] Data = new byte[0];
    }

}
