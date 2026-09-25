namespace CardShopCoop.Net.Messages
{
    /// <summary>One enum member's identity: its member name and the numeric id THIS process's
    /// runtime assigned it. Carried in the handshake so the client can pair the host's ids with
    /// its own by name. <see cref="EnumKindIdentityDto.Kind"/> is the Catalog module's EnumKind
    /// value kept as an int so the generic Net layer does not depend on a feature module.</summary>
    public sealed class EnumMemberDto
    {
        public string Name;
        public long Value;
    }

    /// <summary>Every member of one enum kind, ordinally sorted by <see cref="EnumMemberDto.Name"/>.
    /// Plain typed data: no "key=value" text is produced or parsed anywhere.</summary>
    public sealed class EnumKindIdentityDto
    {
        public int Kind;
        public System.Collections.Generic.List<EnumMemberDto> Members = new();
    }

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
        public System.Collections.Generic.List<EnumKindIdentityDto> EnumIdentity = new();
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
        public int BundleLength;
        public bool SidecarsComplete;
        public string SidecarWarning;
        // int, not byte: the host's connection ids are unbounded; a byte wrapped at 255.
        public int SelfId;
        public System.Collections.Generic.List<EnumKindIdentityDto> HostEnumIdentity = new();
        public byte[] HostCardsBlob = new byte[0];
        public ulong SteamId;
        public System.Collections.Generic.List<string> MessageCatalog = new();
        public byte[] MessageReliability = new byte[0];
    }

}
