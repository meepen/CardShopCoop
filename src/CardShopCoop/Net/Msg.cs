using System;
using System.IO;

namespace CardShopCoop.Net
{
    public enum MsgType : byte
    {
        Hello = 1,        // client -> host: version, playerName
        Welcome = 2,      // host -> client: version, hostName, saveSize
        SaveChunk = 3,    // host -> client: offset, bytes
        SaveDone = 4,     // host -> client: totalLength
        PlayerState = 5,  // both ways: pos, yaw, speed
        CoinSet = 6,      // host -> client: double amount
        DayTime = 7,      // host -> client: day, hour, minute
        Emote = 8,        // both ways: emote id
        Ping = 9,
        Pong = 10,
        Bye = 11,
        ShelfDelta = 12,   // host -> client: authoritative compartment states
        ShelfRequest = 13, // client -> host: player restocked/took items, please apply
        PriceList = 14,    // host -> client: full item price table
        BundleChunk = 15,  // host -> client: mod sidecar data bundle
        BundleDone = 16,
        ProgressSet = 17,  // host -> client: shop exp, shop level, fame
        Activity = 18,     // both ways: short activity ping ("opening a pack!")
        EconContrib = 19,  // client -> host: forwarded money/XP/fame earned by the joiner
        CardDelta = 20,    // both ways: a card entered/left the shared collection
        NpcState = 21,     // host -> client: batched customer/worker puppet states
        CardShelfDelta = 24,   // host -> client: authoritative card display slots
        CardShelfRequest = 25, // client -> host: joiner placed/removed a display card
        CardPriceSet = 26,     // both ways: a card's marked price changed
        RegisterState = 27,    // host -> client: observer checkout state + mannedBy
        Roster = 29,           // host -> clients: connId->name table for relayed peers
        RelayState = 30,       // host -> clients: another client's PlayerState [senderId + state]
        RelayTag = 31,         // host -> clients: another client's emote/activity [senderId + kind]
        ObjMoveDelta = 32,     // host -> client: authoritative placed-object transforms
        ObjMoveRequest = 33,   // client -> host: joiner moved a shelf/counter/decoration
        BoxState = 34,         // host -> client: full loose-box population snapshot
        BoxRequest = 35,       // client -> host: joiner's box edits (dispense/carry/trash)
        OrderRequest = 36,     // client -> host: joiner bought restock (spawn officially)
        ShopName = 37,         // host -> client: the shop's name
        ItemPriceContrib = 38, // client -> host: joiner set an item price
        LightState = 39,       // host -> client: full LightTimeData (sky phase, timers)
        PopState = 40,         // host -> client: placed-object population roster (all kinds)
        FurnitureOrder = 41,   // client -> host: joiner bought furniture (spawn officially)
        BoxRemoved = 42,       // client -> host: joiner trashed a loose box (destroy officially)
        LicenseUnlock = 43,    // both ways: a product license was purchased (itemType+size identity)
        LicenseState = 44,     // host -> client: full unlocked-license set (identity-keyed)
        StaffOp = 45,          // client -> host: hire/fire/manage a worker
        StaffState = 46,       // host -> client: hired roster + worker settings
        ShopOp = 47,           // client -> host: pay bill / unlock room / flip a sign
        ShopState = 48,        // host -> client: bills + room unlocks + sign states
        SettingsOp = 49,       // client -> host: deco / game event / counter toggles / table numbers
        SettingsState = 50,    // host -> client: those settings, authoritative
        MarketState = 51,      // host -> client: market % changes + price history (one shared market)
        ReportState = 52,      // host -> client: end-of-day report + customer reviews
        ContainerOp = 53,      // client -> host: container edits (storage/donation/openers/box bank)
        ContainerState = 54,   // host -> client: container contents, authoritative
        TournamentState = 55,  // host -> client: tournament schedule/rounds/signups
        GradingOp = 56,        // client -> host: joiner submits cards for grading (matures on host days)
        GradingState = 57,     // host -> client: pending grading submissions
        TradeOp = 58,          // client -> host: joiner accepts/declines a counter trade/sell-in
        TradeState = 59,       // host -> client: live trade/sell-in offer at a counter
        TableState = 60,       // host -> client: play-table card layout digest (visuals)
        CardBoxOp = 61,        // client -> host: joiner collects/moves a graded-returns card box
        CardBoxState = 62,     // host -> client: card packaging box population (graded returns)
        EnumSync = 63,         // host -> client: the host's enum_values.json (card-ID registry)
        Toast = 64,            // host -> client: one-line on-screen notice
        CatalogDigest = 65,    // client -> host: restock catalog identities (mismatch diagnosis)
        FurnBoxOp = 66,        // client -> host: furniture-box carry/place/destroy ops
        FurnBoxState = 67,     // host -> client: furniture delivery box population
        GradedRemove = 68,     // both ways: a graded card left the shared album (RemoveGradedCard)
        SprayHit = 69,         // client -> host: handheld deodorant hold-spray (pos+range+potency), replay against host customers
        CardDeltaBatch = 70,   // both ways: [int count][count x CardDelta payload] - one frame for a whole frame's card changes
        // BOTH WAYS, and the same payload writer/reader serves both directions on purpose.
        // Client -> host on the catalog gating (first send in-game, then hash change, min 45s);
        // host -> client only in REPLY to a divergent one, so the guest can see its own
        // one-sided difference. Report-only: receiving it never changes a card.
        GradedDigest = 71,     // both ways: graded-cert existence digest (album + hold + shelves + boxes + in-progress)
        RegisterCart = 72,     // host -> client: authoritative cart, prices, phase, and scanned slots
        RegisterOp = 73,       // client -> host: the manning player's register action (scan / payment / change / finish)
    }

    /// <summary>One received message, already reassembled from the wire.</summary>
    public struct InMsg
    {
        public int ConnId;
        public MsgType Type;
        public byte[] Payload;
    }

    /// <summary>Builders/parsers for message payloads. Wire format per frame:
    /// [int32 payloadLen+1][byte MsgType][payload]. All little-endian via BinaryWriter.</summary>
    public static class Msg
    {
        // One builder per thread, reused forever: Build runs ~30x/second in a session
        // (15Hz states + engine deltas) and a fresh MemoryStream+writer per message was
        // a steady GC drip that only existed while connected.
        [ThreadStatic] private static MemoryStream _buildMs;
        [ThreadStatic] private static BinaryWriter _buildBw;

        public static byte[] Build(MsgType type, Action<BinaryWriter> write = null)
        {
            if (_buildMs == null)
            {
                _buildMs = new MemoryStream(4096);
                _buildBw = new BinaryWriter(_buildMs);
            }
            var ms = _buildMs;
            var bw = _buildBw;
            ms.SetLength(0);
            ms.Position = 0;
            bw.Write(0);              // frame length placeholder
            bw.Write((byte)type);
            write?.Invoke(bw);
            bw.Flush();
            long end = ms.Position;
            ms.Position = 0;
            bw.Write((int)(end - 4)); // bytes after the length field
            bw.Flush();
            return ms.ToArray();      // the one remaining copy: transports own the array
        }

        public static BinaryReader Reader(byte[] payload)
        {
            return new BinaryReader(new MemoryStream(payload, writable: false));
        }

        /// <summary>World/bundle transfers are gzipped: the EPL sidecar json compresses
        /// ~5-10x, turning a minutes-long relay transfer into seconds.</summary>
        public static byte[] Gzip(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest))
                    gz.Write(data, 0, data.Length);
                return ms.ToArray();
            }
        }

        public static byte[] Gunzip(byte[] data)
        {
            using (var src = new MemoryStream(data, writable: false))
            using (var gz = new System.IO.Compression.GZipStream(src, System.IO.Compression.CompressionMode.Decompress))
            using (var dst = new MemoryStream())
            {
                gz.CopyTo(dst);
                return dst.ToArray();
            }
        }

        // ------------------------------------------------------------ typed enum wire helpers
        //
        // EVERY modded enum id that crosses the wire MUST go through one of these, and never
        // through a raw bw.Write((int)someEnum) / (SomeEnum)br.ReadInt32(). They are the single
        // enforcement point for the session's id contract:
        //
        //   THE WIRE SPEAKS THE HOST'S IDS. Only a CLIENT translates (Util.EnumMap); the host
        //   translates nothing, and every id below EnumMap's modded floor - i.e. all vanilla
        //   content - passes through untouched, so vanilla traffic cannot be affected by any of
        //   this. A modded id with no counterpart on the receiving PC becomes that enum's None
        //   sentinel, which the existing skip paths refuse; one-sided content packs are allowed
        //   and must never be "fixed" by zeroing or dropping the message.
        //
        // Enums NOT listed here are vanilla-only id spaces (identical on every PC) and are
        // deliberately written raw - adding a helper for one would be pure ceremony.
        //
        //   TRANSLATE EXACTLY ONCE, AT THE WIRE BOUNDARY. Util.EnumMap.ToWire/FromWire are
        //   called ONLY from these Msg.Read*/Write* helpers, plus the handful of explicit
        //   EnumMap.TryFromWire branch sites that must know whether an id resolved. NEVER
        //   from Sync\ code (or any other consumer) on a value that has already been parsed:
        //   everything handed to the Sync layer is LOCAL ids. Translating a second time
        //   remaps an already-local modded id into a different item or into None, and that is
        //   exactly how the AvatarManager UpdateState / ShowPackOpen double-translations
        //   happened. If a Sync method needs the raw wire id, it must read it itself.

        public static void WriteItemType(BinaryWriter bw, EItemType v) { bw.Write(Util.EnumMap.ToWire(Util.EnumKind.ItemType, (int)v)); }
        public static EItemType ReadItemType(BinaryReader br) { return (EItemType)Util.EnumMap.FromWire(Util.EnumKind.ItemType, br.ReadInt32()); }

        public static void WriteObjType(BinaryWriter bw, EObjectType v) { bw.Write(Util.EnumMap.ToWire(Util.EnumKind.ObjectType, (int)v)); }
        public static EObjectType ReadObjType(BinaryReader br) { return (EObjectType)Util.EnumMap.FromWire(Util.EnumKind.ObjectType, br.ReadInt32()); }

        public static void WriteDecoType(BinaryWriter bw, EDecoObject v) { bw.Write(Util.EnumMap.ToWire(Util.EnumKind.DecoObject, (int)v)); }
        public static EDecoObject ReadDecoType(BinaryReader br) { return (EDecoObject)Util.EnumMap.FromWire(Util.EnumKind.DecoObject, br.ReadInt32()); }

        public static void WriteExpansion(BinaryWriter bw, ECardExpansionType v) { bw.Write(Util.EnumMap.ToWire(Util.EnumKind.CardExpansion, (int)v)); }
        public static ECardExpansionType ReadExpansion(BinaryReader br) { return (ECardExpansionType)Util.EnumMap.FromWire(Util.EnumKind.CardExpansion, br.ReadInt32()); }

        public static void WriteMonsterType(BinaryWriter bw, EMonsterType v) { bw.Write(Util.EnumMap.ToWire(Util.EnumKind.MonsterType, (int)v)); }
        public static EMonsterType ReadMonsterType(BinaryReader br) { return (EMonsterType)Util.EnumMap.FromWire(Util.EnumKind.MonsterType, br.ReadInt32()); }

        /// <summary>Shared CardData wire format (used by CardDelta, CardShelfDelta, CardPriceSet).</summary>
        public static void WriteCard(BinaryWriter bw, CardData card)
        {
            WriteExpansion(bw, card.expansionType);
            WriteMonsterType(bw, card.monsterType);
            // borderType is VANILLA and stays raw ON PURPOSE. ECardBorderType is not one of the
            // enums EnhancedPrefabLoader mints custom ids into (ModParity.ModdedEnumTypeNames:
            // EObjectType, EDecoObject, EItemType, ECardExpansionType, ERarity,
            // ECollectionPackType), so its ids are identical on every PC and translating it
            // could only ever introduce a bug. Do not "fix" this line.
            bw.Write((int)card.borderType);
            bw.Write(card.isFoil);
            bw.Write(card.isDestiny);
            bw.Write(card.isChampionCard);
            bw.Write(card.isNew);
            bw.Write(card.cardGrade);
            bw.Write(card.gradedCardIndex);
        }

        public static CardData ReadCard(BinaryReader br)
        {
            return new CardData
            {
                expansionType = ReadExpansion(br),
                monsterType = ReadMonsterType(br),
                borderType = (ECardBorderType)br.ReadInt32(), // vanilla - see WriteCard
                isFoil = br.ReadBoolean(),
                isDestiny = br.ReadBoolean(),
                isChampionCard = br.ReadBoolean(),
                isNew = br.ReadBoolean(),
                cardGrade = br.ReadInt32(),
                gradedCardIndex = br.ReadInt32(),
            };
        }
    }
}
