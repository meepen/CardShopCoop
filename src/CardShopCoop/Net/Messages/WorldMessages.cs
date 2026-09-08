using System.Collections.Generic;
using CardShopCoop.Net;
using CardShopCoop.Sync;
using UnityEngine;

namespace CardShopCoop.Net.Messages
{
    // Ordinary world-stock delta: host -> client. Reuses WorldSync's own wire writer so a
    // delta and the 12s full-state heal stay byte-identical (both are MsgType.ShelfDelta).
    [NetworkMessage(MsgType.ShelfDelta, Policy = MessagePolicy.ClientOnly)]
    public sealed class ShelfDeltaMessage : INetMessage
    {
        public List<WorldSync.Entry> Entries = new List<WorldSync.Entry>();
        public MsgType Type { get { return MsgType.ShelfDelta; } }
    }

    // Guest restock/take request: client -> host. Same Entry shape as the delta.
    [NetworkMessage(MsgType.ShelfRequest, Policy = MessagePolicy.HostOnly)]
    public sealed class ShelfRequestMessage : INetMessage
    {
        public List<WorldSync.Entry> Entries = new List<WorldSync.Entry>();
        public MsgType Type { get { return MsgType.ShelfRequest; } }
    }

    [NetworkMessage(MsgType.ObjMoveDelta, Policy = MessagePolicy.ClientOnly)]
    public sealed class ObjMoveDeltaMessage : INetMessage
    {
        public List<ObjMoveSync.Entry> Entries = new List<ObjMoveSync.Entry>();
        public MsgType Type { get { return MsgType.ObjMoveDelta; } }
    }

    [NetworkMessage(MsgType.ObjMoveRequest, Policy = MessagePolicy.HostOnly)]
    public sealed class ObjMoveRequestMessage : INetMessage
    {
        public List<ObjMoveSync.Entry> Entries = new List<ObjMoveSync.Entry>();
        public MsgType Type { get { return MsgType.ObjMoveRequest; } }
    }

    [NetworkMessage(MsgType.BoxState, Policy = MessagePolicy.ClientOnly)]
    public sealed class BoxStateMessage : INetMessage
    {
        public List<BoxSync.Entry> Entries = new List<BoxSync.Entry>();
        public MsgType Type { get { return MsgType.BoxState; } }
    }

    [NetworkMessage(MsgType.BoxRequest, Policy = MessagePolicy.HostOnly)]
    public sealed class BoxRequestMessage : INetMessage
    {
        public List<BoxSync.Entry> Entries = new List<BoxSync.Entry>();
        public MsgType Type { get { return MsgType.BoxRequest; } }
    }

    // Guest trashed a loose box: client -> host, keyed by the box's stable id + its type
    // (the host refuses a removal unless the wire type matches the tracked box's type, so
    // the id has to be in LOCAL terms - Msg.ReadItemType does that translation).
    [NetworkMessage(MsgType.BoxRemoved, Policy = MessagePolicy.HostOnly)]
    public sealed class BoxRemovedMessage : INetMessage
    {
        public int Index;
        public EItemType ItemType;
        public MsgType Type { get { return MsgType.BoxRemoved; } }
    }

    // Placed-object population roster, one list per kind: host -> client. Deferred entirely
    // to PopulationSync's writer/reader so the nested (list-of-lists) layout stays identical.
    [NetworkMessage(MsgType.PopState, Policy = MessagePolicy.ClientOnly)]
    public sealed class PopStateMessage : INetMessage
    {
        public List<List<PopulationSync.Entry>> Entries = new List<List<PopulationSync.Entry>>();
        public MsgType Type { get { return MsgType.PopState; } }
    }

    [NetworkMessage(MsgType.CardShelfDelta, Policy = MessagePolicy.ClientOnly)]
    public sealed class CardShelfDeltaMessage : INetMessage
    {
        public List<CardShelfSync.Entry> Entries = new List<CardShelfSync.Entry>();
        public MsgType Type { get { return MsgType.CardShelfDelta; } }
    }

    [NetworkMessage(MsgType.CardShelfRequest, Policy = MessagePolicy.HostOnly)]
    public sealed class CardShelfRequestMessage : INetMessage
    {
        public List<CardShelfSync.Entry> Entries = new List<CardShelfSync.Entry>();
        public MsgType Type { get { return MsgType.CardShelfRequest; } }
    }

    // One card entering/leaving the shared collection. Both ways on purpose (the single
    // CardDelta supply path relays a guest's gain/loss to the other guests).
    [NetworkMessage(MsgType.CardDelta)]
    public sealed class CardDeltaMessage : INetMessage
    {
        public bool IsAdd;
        public int Amount;
        public CardData Card;
        public MsgType Type { get { return MsgType.CardDelta; } }
    }

    /// <summary>One payload inside a CardDeltaBatch frame.</summary>
    public struct CardDeltaEntry
    {
        public bool IsAdd;
        public int Amount;
        public CardData Card;
    }

    // [int count][count x (bool isAdd, int amount, CardData card)] - one frame carries a
    // whole frame's worth of card changes. Both ways, so the sender's own serializer and the
    // relay path agree byte-for-byte on the per-delta layout.
    [NetworkMessage(MsgType.CardDeltaBatch)]
    public sealed class CardDeltaBatchMessage : INetMessage
    {
        public List<CardDeltaEntry> Deltas = new List<CardDeltaEntry>();
        public MsgType Type { get { return MsgType.CardDeltaBatch; } }
    }

    [NetworkMessage(MsgType.Toast, Policy = MessagePolicy.ClientOnly)]
    public sealed class ToastMessage : INetMessage
    {
        public string Text;
        public MsgType Type { get { return MsgType.Toast; } }
    }

    /// <summary>One (item id, price) pair in a PriceList table.</summary>
    public struct PriceEntry
    {
        public int ItemType; // raw wire id; the receiver translates via Util.EnumMap.TryFromWire
        public float Price;
    }

    // Host -> client: full sparse price table. The item id is stored RAW (host id space) and
    // translated in the handler via Util.EnumMap.TryFromWire, because the handler needs to
    // know whether a modded id actually resolved before recording it as "priced".
    [NetworkMessage(MsgType.PriceList, Policy = MessagePolicy.ClientOnly)]
    public sealed class PriceListMessage : INetMessage
    {
        public List<PriceEntry> Prices = new List<PriceEntry>();
        public MsgType Type { get { return MsgType.PriceList; } }
    }

    [NetworkMessage(MsgType.LightState, Policy = MessagePolicy.ClientOnly)]
    public sealed class LightStateMessage : INetMessage
    {
        public string LightJson;
        public MsgType Type { get { return MsgType.LightState; } }
    }

    /// <summary>One unlocked-license entry in a LicenseState frame.</summary>
    public struct LicenseEntry
    {
        public EItemType ItemType;
        public bool Big;     // stored as the product's isBigBox flag
        public int NameFnv;  // Fnv hash of the restock name (name identity survives id drift)
    }

    // Host -> client: full unlocked-license set, identity-keyed. Layout is [bool scanner]
    // [ushort count] then per entry [EItemType][bool isBigBox][int nameFnv].
    [NetworkMessage(MsgType.LicenseState, Policy = MessagePolicy.ClientOnly)]
    public sealed class LicenseStateMessage : INetMessage
    {
        public bool Scanner;
        public List<LicenseEntry> Entries = new List<LicenseEntry>();
        public MsgType Type { get { return MsgType.LicenseState; } }
    }
}
