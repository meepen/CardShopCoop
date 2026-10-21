using CardShopCoop.Net;
using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;
using CardShopCoop.Api;

namespace CardShopCoop.Modules.World
{
    /// <summary>
    /// One shared market. PriceChangeManager rerolls every item/card percent change with
    /// UnityEngine.Random at each day start, so from day 2 the joiner would price cards
    /// against a market that does not exist (host customers judge his tags against the
    /// HOST's numbers). The joiner's day roll is therefore a host-authority boundary: the
    /// client skips PriceChangeManager.OnDayStarted, and the host's post-roll table is
    /// broadcast: item % changes, all eight per-expansion card % changes, and the game-event
    /// price rows the phone apps read. The rolled percents the snapshot ships
    /// (GeneratedMarketPrice in FillMarket/ReadMarketInto) fully overwrite the client's tables.
    ///
    /// The day roll also appends price HISTORY (UpdateItemPricePercentChange /
    /// UpdatePastCardPricePercentChange). History is derived, not independent state: the host
    /// appends each table's post-roll value, and the client applies the same values before
    /// replaying the same append, so both curves stay in step without shipping the history.
    ///
    /// Card BASES: RestockManager.GenerateCardMarketPrice only writes each card's
    /// generatedMarketPrice base, which the snapshot ships and fully overwrites
    /// (GeneratedMarketPrice in FillMarket/ReadMarketInto), so it runs vanilla and is observed.
    ///
    /// All writes go INTO the existing lists / MarketPrice objects, never replacing them:
    /// PriceChangeManager.Init aliases its own fields to the CPlayerData lists, so a
    /// fresh list instance would silently orphan every consumer.
    /// </summary>
    internal sealed class WorldMarketInteraction
    {
        private readonly CoopRuntimeContext _context;
        private readonly bool _host;

        internal WorldMarketInteraction(CoopRuntimeContext context, bool host)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _host = host;
        }

        private static WorldMarketInteraction Current => WorldHostBehaviour.ActiveCards?.Market
            ?? WorldClientBehaviour.ActiveCards?.Market;

        private static bool IsHost => Current?._host == true;
        private static bool IsClient => Current != null && !Current._host;
        private static bool InGame => Current != null && Current._context.InGame();

        public Action<INetMessage> BroadcastState; // set by WorldCardInteraction: host -> clients
        // Client: newest snapshot received while not in game. Applied from the world-ready
        // lifecycle hook, after CGameData.PropagateLoadData has swapped the card tables.
        private MarketStateMessage _pendingState;

        // Host: set whenever the market data is known to have changed. Every writer of the
        // synced tables flags this (day roll, base generation, game-event price edits, load,
        // cost updates, join) and HostTick broadcasts on the next tick. No polling, no timer:
        // the snapshot is a Reliable message, so a send is a delivery.
        private static bool s_dirty;

        // Host: a day roll just finished and its complete post-roll snapshot still owes the
        // clients. Kept separate from s_dirty so an unrelated flush cannot consume the marker.
        private static bool s_dayRollPending;

        // Client: the last host day whose price history this peer has appended. History is
        // derived from the current percents, so applying the host snapshot and replaying the
        // vanilla append locally reproduces the host history without sending it.
        private int _lastHistoryDay;
        private bool _hasBaseline;

        // Cached EPL modded-id list (walking the EPL item dictionary + Convert.ToInt32 per key
        // was happening on every snapshot build). The registry is fixed for a session.
        private static List<int> s_eplModdedCache;

        // Host: modded-expansion card-market changes (deltas), captured by the
        // AddCardPricePercentChange / SetCardGeneratedMarketPrice postfixes. EPL prefixes
        // those SAME game methods (postfixes still run when a prefix returns false), so this
        // covers modded expansions without touching EPL's internals; the client replays the
        // exact same calls and EPL's own prefix stores them locally.
        private struct PendingModCard
        {
            public ECardExpansionType Expansion;
            public int Index;
            public bool IsDestiny;
            public bool HasPercent;
            public float Percent;   // absolute pricePercentChangeList
            public float Base;
            public bool HasBase;
        }
        private static readonly Dictionary<long, PendingModCard> s_modCardPending
            = new();

        internal void Reset()
        {
            _pendingState = null;
            _lastHistoryDay = 0;
            _hasBaseline = false;
            s_dirty = false;
            s_dayRollPending = false;
            InvalidateContentCache();
            s_modCardPending.Clear();
            s_rawWriteMissLogged = false;
        }

        /// <summary>Invalidates the fixed EPL registry only for an explicit content, scene, or
        /// session lifecycle event. It is intentionally not time based.</summary>
        internal static void InvalidateContentCache()
        {
            s_eplModdedCache = null;
        }

        internal void Tick()
        {
            if (!InGame)
            {
                return;
            }


            if (_host)
            {
                // A pending day-roll marker forces one complete post-roll snapshot even if an
                // earlier mid-day flush already cleared s_dirty.
                FlushHostMutation(s_dayRollPending);
            }
        }

        internal void FlushClientState()
        {
            if (_host || _pendingState == null)
            {
                return;
            }


            var pending = _pendingState;
            _pendingState = null;
            // The first buffered snapshot is the join baseline: the sidecar/world save already
            // carries the host's history through this day, so do not append it again. A later
            // buffered day-roll snapshot (a reload during play) must append normally.
            ClientApplyInner(pending, isBaseline: !_hasBaseline);
        }

        internal void AppendBaselineMessages(Action<INetMessage> append)
        {
            if (!_host || append == null || !InGame
                || CPlayerData.m_ItemPricePercentChangeList == null
                || CPlayerData.m_ItemPricePercentChangeList.Count == 0)
            {
                return;
            }

            append(BuildState(dayRoll: false));
            s_dirty = false;
        }

        // ---------------- patches ----------------

        internal static void ApplyPatches(Harmony h)
        {
            // The client must never roll its own market: vanilla's Random reroll (including
            // EPL's modded-expansion roll) would compete with the host's authoritative changes,
            // and only rows the host happened to reroll would ever be corrected. Blocking the
            // whole handler also skips the un-shipped history append, which is per-peer.
            // HostRolledPostfix stamps the dirty flag for the prompt broadcast and is a no-op
            // on a client (postfixes still run when the prefix skips the original).
            Try(h, typeof(PriceChangeManager), "OnDayStarted",
                prefix: new HarmonyMethod(typeof(WorldMarketInteraction), nameof(ClientBlockPrefix)),
                postfix: new HarmonyMethod(typeof(WorldMarketInteraction), nameof(HostRolledPostfix)));
            // Other writers of the synced market tables: a game-event price edit, the
            // initial load, first-seen base generation, and per-purchase cost updates.
            // Boot them all into the dirty flag so HostTick never has to poll.
            Try(h, typeof(PriceChangeManager), "SetGameEventPrice",
                postfix: new HarmonyMethod(typeof(WorldMarketInteraction), nameof(MarkDirtyHostPostfix)));
            Try(h, typeof(PriceChangeManager), "Init",
                postfix: new HarmonyMethod(typeof(WorldMarketInteraction), nameof(MarkDirtyHostPostfix)));
            // RestockManager.Init is the only caller of GenerateCardMarketPrice and the one
            // place generated item cost/market bases are first filled (a newly met item).
            // GenerateCardMarketPrice itself is NOT gated: its only output is each card's
            // generatedMarketPrice base, which the snapshot ships and overwrites
            // (GeneratedMarketPrice), so the game runs it and this file observes.
            Try(h, typeof(RestockManager), "Init",
                postfix: new HarmonyMethod(typeof(WorldMarketInteraction), nameof(MarkDirtyHostPostfix)));
            // Average item cost moves during normal gameplay (buying stock).
            Try(h, typeof(CPlayerData), "UpdateAverageItemCost",
                postfix: new HarmonyMethod(typeof(WorldMarketInteraction), nameof(MarkDirtyHostPostfix)));
            Try(h, typeof(CPlayerData), "SetAverageItemCost",
                postfix: new HarmonyMethod(typeof(WorldMarketInteraction), nameof(MarkDirtyHostPostfix)));
            // Modded-expansion card market: EPL PREFIXES these game writers (returning false
            // to divert to its own save data), but a postfix still runs when a prefix skips
            // the original, so this captures vanilla AND modded writes. The client replays the
            // same call, letting EPL's own prefix store it locally - no EPL types touched here.
            Try(h, typeof(CPlayerData), "AddCardPricePercentChange",
                postfix: new HarmonyMethod(typeof(WorldMarketInteraction), nameof(CardPercentChangedPostfix)));
            Try(h, typeof(CPlayerData), "SetCardGeneratedMarketPrice",
                postfix: new HarmonyMethod(typeof(WorldMarketInteraction), nameof(CardBaseChangedPostfix)));
        }

        /// <summary>Vanilla expansions are owned by the dense GenCardMarketPriceList sync;
        /// only modded (EPL) expansions go through the delta hook. The oracle itself lives with
        /// the other card-identity facts (WorldCardInteraction.IsVanillaCardExpansion) so the
        /// game-vs-modded boundary is defined exactly once.</summary>
        private static bool IsVanillaCardExpansion(ECardExpansionType expansion)
            => WorldCardInteraction.IsVanillaCardExpansion(expansion);

        private static long ModCardKey(ECardExpansionType expansion, int index, bool isDestiny)
        {
            return ((long)(int)expansion << 33) | ((long)(uint)index << 1) | (isDestiny ? 1L : 0L);
        }

        public static void CardPercentChangedPostfix(int cardIndex, ECardExpansionType expansionType, bool isDestiny, float percentChange)
        {
            if (!IsHost || IsVanillaCardExpansion(expansionType))
            {
                return;
            }
            // Ship the ABSOLUTE percent (not the delta) so the client can converge exactly:
            // the delta is quantized and preserves any save-rounding offset, which flips a cent.
            // GetCardPricePercentChange is the public reader and is EPL-prefixed too.
            float absolute;
            try
            {
                absolute = CPlayerData.GetCardPricePercentChange(cardIndex, expansionType, isDestiny);
            }
            catch (Exception e) { CoopLog.Swallow(e); return; }
            var key = ModCardKey(expansionType, cardIndex, isDestiny);
            s_modCardPending.TryGetValue(key, out var p);
            p.Expansion = expansionType;
            p.Index = cardIndex;
            p.IsDestiny = isDestiny;
            p.Percent = absolute;
            p.HasPercent = true;
            s_modCardPending[key] = p;
            s_dirty = true;
        }

        public static void CardBaseChangedPostfix(int cardIndex, ECardExpansionType expansionType, bool isDestiny, float price)
        {
            if (!IsHost || IsVanillaCardExpansion(expansionType))
            {
                return;
            }

            var key = ModCardKey(expansionType, cardIndex, isDestiny);
            s_modCardPending.TryGetValue(key, out var p);
            p.Expansion = expansionType;
            p.Index = cardIndex;
            p.IsDestiny = isDestiny;
            p.Base = price;
            p.HasBase = true;
            s_modCardPending[key] = p;
            s_dirty = true;
        }

        /// <summary>Host: a market table was written; flush on the next tick.</summary>
        public static void MarkDirtyHostPostfix()
        {
            if (IsHost)
            {
                s_dirty = true;
                Current?.FlushHostMutation(dayRoll: false);
            }
        }

        private static void Try(Harmony h, Type type, string method,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            try
            {
                var original = AccessTools.Method(type, method);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning($"Patch target missing: {type.Name}.{method}");
                    return;
                }
                h.Patch(original, prefix: prefix, postfix: postfix);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning($"Patch failed: {type.Name}.{method}: {e.Message}");
            }
        }

        /// <summary>Host-authority boundary: the host (and singleplayer, where no behaviour is
        /// active) runs the day roll; the client never does. Its market changes arrive from the
        /// host's snapshot instead, so the two peers can only drift together.</summary>
        public static bool ClientBlockPrefix()
        {
            return !IsClient;
        }

        public static void HostRolledPostfix()
        {
            // Postfixes run even when the prefix skipped the original; only the host broadcasts.
            if (IsHost)
            {
                // Do not build the snapshot here: EPL's own day-start postfixes may still be
                // adding modded-card changes. Tick sends one complete post-roll snapshot next
                // frame, after every postfix has run.
                s_dirty = true;
                s_dayRollPending = true;
            }
        }

        // ---------------- host ----------------

        private void FlushHostMutation(bool dayRoll)
        {
            if (BroadcastState == null || (!s_dirty && !dayRoll))
            {
                return;
            }

            try
            {
                // tables exist only after CPlayerData init; an empty item list means the
                // save hasn't landed yet. Keep s_dirty so the flush still happens later.
                if (CPlayerData.m_ItemPricePercentChangeList == null
                    || CPlayerData.m_ItemPricePercentChangeList.Count == 0)
                {
                    return;
                }

                var msg = BuildState(dayRoll);
                BroadcastState(msg);
                s_modCardPending.Clear();
                s_dirty = false;
                if (dayRoll)
                {
                    s_dayRollPending = false;
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("WorldMarketInteraction host: " + e.Message); }
        }

        private static MarketStateMessage BuildState(bool dayRoll)
        {
            var modded = EplModdedItemTypes(); // empty when the bridge is inactive
            var msg = new MarketStateMessage
            {
                IsDayRoll = dayRoll,
                Day = CPlayerData.m_CurrentDay,
            };
            FillPercents(msg.ItemPricePercentChangeList, CPlayerData.m_ItemPricePercentChangeList, modded);
            FillMarket(msg.GenCardMarketPriceList, CPlayerData.m_GenCardMarketPriceList);
            FillMarket(msg.GenCardMarketPriceListDestiny, CPlayerData.m_GenCardMarketPriceListDestiny);
            FillMarket(msg.GenCardMarketPriceListGhost, CPlayerData.m_GenCardMarketPriceListGhost);
            FillMarket(msg.GenCardMarketPriceListGhostBlack, CPlayerData.m_GenCardMarketPriceListGhostBlack);
            FillMarket(msg.GenCardMarketPriceListMegabot, CPlayerData.m_GenCardMarketPriceListMegabot);
            FillMarket(msg.GenCardMarketPriceListFantasyRPG, CPlayerData.m_GenCardMarketPriceListFantasyRPG);
            FillMarket(msg.GenCardMarketPriceListCatJob, CPlayerData.m_GenCardMarketPriceListCatJob);
            FillMarket(msg.GenCardMarketPriceListAscension, CPlayerData.m_GenCardMarketPriceListAscension);
            // game-event rows are raw prices, not clamped percents - full floats
            FillFloats(msg.SetGameEventPriceList, CPlayerData.m_SetGameEventPriceList);
            FillFloats(msg.GeneratedGameEventPriceList, CPlayerData.m_GeneratedGameEventPriceList);
            FillFloats(msg.GameEventPricePercentChangeList, CPlayerData.m_GameEventPricePercentChangeList);
            // Graded-card price multiplier: the client regenerates it locally (RNG) otherwise.
            FillFloats(msg.GenGradedCardPriceMultiplierList, CPlayerData.m_GenGradedCardPriceMultiplierList);
            FillModdedCards(msg.ModdedCards);
            FillSparse(msg.GeneratedMarketPriceList, CPlayerData.m_GeneratedMarketPriceList, modded, s_eplGenMarket);
            FillSparse(msg.GeneratedCostPriceList, CPlayerData.m_GeneratedCostPriceList, modded, s_eplGenCost);
            FillSparse(msg.AverageItemCostList, CPlayerData.m_AverageItemCostList, modded, s_eplAvgCost);
            return msg;
        }

        // ---------------- client ----------------

        public void ClientApplyState(MarketStateMessage message)
        {
            ClientApplyInner(message, isBaseline: false);
        }

        /// <summary>Client: apply now if in game, otherwise hold the newest snapshot until
        /// the world load finishes (see <see cref="_pendingState"/>).</summary>
        public void ClientApplyOrBuffer(MarketStateMessage message, bool inGame)
        {
            if (message == null)
            {

                throw new InvalidOperationException("Authoritative market state is missing.");
            }


            if (!inGame)
            {
                _pendingState = message;
                return;
            }
            if (_pendingState != null)
            {
                _pendingState = null;
            }
            ClientApplyState(message);
        }

        private void ClientApplyInner(MarketStateMessage message, bool isBaseline)
        {
            ReadPercentsInto(message.ItemPricePercentChangeList, CPlayerData.m_ItemPricePercentChangeList);
            ReadMarketInto(message.GenCardMarketPriceList, CPlayerData.m_GenCardMarketPriceList);
            ReadMarketInto(message.GenCardMarketPriceListDestiny, CPlayerData.m_GenCardMarketPriceListDestiny);
            ReadMarketInto(message.GenCardMarketPriceListGhost, CPlayerData.m_GenCardMarketPriceListGhost);
            ReadMarketInto(message.GenCardMarketPriceListGhostBlack, CPlayerData.m_GenCardMarketPriceListGhostBlack);
            ReadMarketInto(message.GenCardMarketPriceListMegabot, CPlayerData.m_GenCardMarketPriceListMegabot);
            ReadMarketInto(message.GenCardMarketPriceListFantasyRPG, CPlayerData.m_GenCardMarketPriceListFantasyRPG);
            ReadMarketInto(message.GenCardMarketPriceListCatJob, CPlayerData.m_GenCardMarketPriceListCatJob);
            ReadMarketInto(message.GenCardMarketPriceListAscension, CPlayerData.m_GenCardMarketPriceListAscension);
            ReadFloatsInto(message.SetGameEventPriceList, CPlayerData.m_SetGameEventPriceList);
            ReadFloatsInto(message.GeneratedGameEventPriceList, CPlayerData.m_GeneratedGameEventPriceList);
            ReadFloatsInto(message.GameEventPricePercentChangeList, CPlayerData.m_GameEventPricePercentChangeList);
            ReadFloatsInto(message.GenGradedCardPriceMultiplierList, CPlayerData.m_GenGradedCardPriceMultiplierList);
            ReadSparseFloatsInto(message.GeneratedMarketPriceList, CPlayerData.m_GeneratedMarketPriceList, ModdedGenMarket, "generated market price");
            ReadSparseFloatsInto(message.GeneratedCostPriceList, CPlayerData.m_GeneratedCostPriceList, ModdedGenCost, "generated cost price");
            ReadSparseFloatsInto(message.AverageItemCostList, CPlayerData.m_AverageItemCostList, ModdedAvgCost, "average item cost");
            ApplyModdedCards(message.ModdedCards);

            // History is derived from the current percents, not independent state: the host's
            // day roll appended each table's post-roll value, so replaying the same vanilla
            // append after applying the snapshot reproduces the host history exactly. EPL's
            // own postfix on UpdatePastCardPricePercentChange appends the modded-card rows.
            if (isBaseline || !_hasBaseline)
            {
                // The join transfer already carries history through the host's current day.
                _lastHistoryDay = message.Day;
                _hasBaseline = true;
                return;
            }

            if (message.IsDayRoll && message.Day > _lastHistoryDay)
            {
                CPlayerData.UpdateItemPricePercentChange();
                CPlayerData.UpdatePastCardPricePercentChange();
                _lastHistoryDay = message.Day;
            }
            else if (_lastHistoryDay == 0 && message.Day > 0)
            {
                // A snapshot that arrived without a buffered baseline: anchor the guard so an
                // older/replayed day-roll snapshot cannot append a duplicate point.
                _lastHistoryDay = message.Day;
            }
        }

        // ---------------- wire helpers ----------------

        // Percent changes are game-clamped to [-80, 200]; x100 fits a short, and 0.01%
        // resolution is beyond anything the UI displays. Entries are keyed by RAW
        // itemType and EPL registers modded items at huge enum values (>= 200k), so
        // only non-zero entries ship, as (index, pct) pairs - a dense send both
        // truncated past 65535 (modded items never synced) and wasted ~128KB per roll.
        // Vanilla values come off the raw list; modded values off the EPL bridge.
        private static void FillPercents(List<MarketPercentEntry> out_list, List<float> list, List<int> modded)
        {
            var vanilla = VanillaWalkCount(list);
            var moddedVals = CollectModded(modded, s_eplPctChange);
            for (var i = 0; i < vanilla; i++)
            {
                if (list[i] != 0f)
                {
                    // the index IS the raw-list EItemType id; the gated wire enum carries it
                    // numerically and the client maps it back by name (CatalogIdMap)
                    out_list.Add(new MarketPercentEntry
                    {
                        ItemType = (EItemType)i,
                        Percent = (short)Mathf.Clamp(Mathf.RoundToInt(list[i] * 100f), short.MinValue, short.MaxValue),
                    });
                }
            }

            for (var k = 0; k < moddedVals.Count; k++)
            {
                out_list.Add(new MarketPercentEntry
                {
                    ItemType = (EItemType)moddedVals[k].Key,
                    Percent = (short)Mathf.Clamp(Mathf.RoundToInt(moddedVals[k].Value * 100f), short.MinValue, short.MaxValue),
                });
            }
        }

        // Generated base prices are FULL floats keyed by raw itemType (the >= 200k
        // modded id space), non-zero entries only. Unlike percents, absent entries keep
        // their local value - the host may legitimately have gaps we filled at join.
        private static void FillSparse(List<MarketSparseEntry> out_list, List<float> list, List<int> modded, PropertyInfo eplField)
        {
            var vanilla = VanillaWalkCount(list);
            var moddedVals = CollectModded(modded, eplField);
            for (var i = 0; i < vanilla; i++)
            {
                if (list[i] != 0f)
                {
                    out_list.Add(new MarketSparseEntry { ItemType = (EItemType)i, Value = list[i] }); // vanilla identity
                }
            }

            for (var k = 0; k < moddedVals.Count; k++)
            {
                out_list.Add(new MarketSparseEntry { ItemType = (EItemType)moddedVals[k].Key, Value = moddedVals[k].Value });
            }
        }

        private static void ReadSparseFloatsInto(List<MarketSparseEntry> entries, List<float> list,
            Action<int, float> moddedWrite, string what)
        {
            for (var k = 0; k < entries.Count; k++)
            {
                var i = (int)entries[k].ItemType;
                var v = entries[k].Value;
                if (RoutesToEpl(i))
                {
                    moddedWrite(i, v);
                    continue;
                }
                WriteRawItem(list, i, v, what);
            }
        }

        private static void ReadPercentsInto(List<MarketPercentEntry> entries, List<float> list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                list[i] = 0f; // absent = no change rolled
            }

            if (EplMarketBridge())
            {
                // modded percents need the same absent-means-zero treatment, but they
                // live in EPL save data, not in the raw list zeroed above
                var modded = EplModdedItemTypes();
                for (var k = 0; k < modded.Count; k++)
                {
                    EplSetFloat(modded[k], s_eplPctChange, 0f);
                }
            }
            for (var k = 0; k < entries.Count; k++)
            {
                var i = (int)entries[k].ItemType;
                var v = entries[k].Percent / 100f;
                if (RoutesToEpl(i))
                {
                    EplSetFloat(i, s_eplPctChange, v);
                    continue;
                }
                WriteRawItem(list, i, v, "item percent change");
            }
        }

        private static void FillMarket(List<MarketCardEntry> out_list, List<MarketPrice> list)
        {
            var n = Mathf.Min(list?.Count ?? 0, ushort.MaxValue);
            for (var i = 0; i < n; i++)
            {
                out_list.Add(new MarketCardEntry
                {
                    Percent = (short)Mathf.Clamp(Mathf.RoundToInt((list[i] != null ? list[i].pricePercentChangeList : 0f) * 100f), short.MinValue, short.MaxValue),
                    // The card BASE, for the same reason the item bases ride along above: a save
                    // whose card price block failed to restore leaves every base at 0, and the
                    // percent alone multiplies 0 into $0.00 cards forever. Full float - unlike the
                    // percent these are raw prices with no game clamp.
                    GeneratedMarketPrice = list[i] != null ? list[i].generatedMarketPrice : 0f,
                });
            }
        }

        private static void ReadMarketInto(List<MarketCardEntry> entries, List<MarketPrice> list)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                var v = entries[i].Percent / 100f;
                var gen = entries[i].GeneratedMarketPrice;
                var row = list[i];
                row.pricePercentChangeList = v; // in place: consumers hold the object
                // The snapshot is authoritative, including rows the host legitimately leaves at
                // zero; copying the value makes a stale client row converge to the host state.
                row.generatedMarketPrice = gen;
            }
        }

        /// <summary>Host: move pending modded-card values into the snapshot.</summary>
        private static void FillModdedCards(List<MarketModdedCardEntry> out_list)
        {
            if (s_modCardPending.Count == 0)
            {
                return;
            }

            foreach (var kv in s_modCardPending)
            {
                var p = kv.Value;
                out_list.Add(new MarketModdedCardEntry
                {
                    Expansion = p.Expansion,
                    Index = p.Index,
                    IsDestiny = p.IsDestiny,
                    HasPercent = p.HasPercent,
                    Percent = p.Percent,
                    Base = p.Base,
                    HasBase = p.HasBase,
                });
            }
            // The pending map is cleared by the host tick only AFTER a successful broadcast:
            // BuildState must not consume it, or a failed send would lose those deltas.
        }

        /// <summary>Client: replay modded-card changes through the game's public writers so
        /// EPL's own prefix (if installed) stores them; without EPL the vanilla original runs.
        /// A content-incomplete client can hit an out-of-range index inside the game/EPL body,
        /// so each row is isolated.</summary>
        private static void ApplyModdedCards(List<MarketModdedCardEntry> entries)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.HasPercent)
                {
                    var current = CPlayerData.GetCardPricePercentChange(e.Index, e.Expansion, e.IsDestiny);
                    var delta = e.Percent - current;
                    if (delta != 0f)
                    {
                        CPlayerData.AddCardPricePercentChange(e.Index, e.Expansion, e.IsDestiny, delta);
                    }
                }
                if (e.HasBase)
                {
                    CPlayerData.SetCardGeneratedMarketPrice(e.Index, e.Expansion, e.IsDestiny, e.Base);
                }
            }
        }

        private static void FillFloats(List<float> out_list, List<float> list)
        {
            var n = Mathf.Min(list?.Count ?? 0, ushort.MaxValue);
            for (var i = 0; i < n; i++)
            {
                out_list.Add(list[i]);
            }
        }

        private static void ReadFloatsInto(List<float> entries, List<float> list)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                var v = entries[i];
                list[i] = v;
            }
        }

        // ---------------- EPL market bridge ----------------

        // EPL IL-weaves the GAME assembly's accesses to the per-item price lists
        // (generated market/cost, percent change, average cost, set price): for
        // index >= 130 (EItemType.Max, the weave boundary - see EplWeaveBoundary)
        // the woven Count/GetItem/SetItem serve its per-item save data
        // instead of the list (ItemPriceListHandler). Raw List access from THIS
        // assembly is NOT woven - it sees only the vanilla rows plus shadow rows the
        // game never reads, so modded market data raw-read here never left the host
        // and raw-written here never reached the joiner's game. Modded rows therefore
        // go through EPL's save data: reads and percent/generated writes mirror
        // ItemPriceListHandler.GetItem/SetItem via reflection (no clean game accessor
        // returns the RAW values - GetItemMarketPrice/GetItemCost bake the percent in,
        // GetAverageItemCost rounds and substitutes cost when out of range); average
        // cost writes ride CPlayerData.SetAverageItemCost, a clean setter whose woven
        // body IS the SetItem path. Wire indexes stay raw EItemType ints: EPL's modded ids
        // resolve identically on every machine, unlike its alternate [130, 130+modCount)
        // index space, which is ordered by the LOCALLY installed mod set and would
        // cross-assign prices between machines.
        private static bool s_eplProbed;
        private static object s_eplSaveMgr;    // EplServices.SaveDataManager (created once, never reassigned)
        private static MethodInfo s_eplTryGet; // TryGetSaveData<EItemType, ItemSaveData>(key, out data)
        private static PropertyInfo s_eplAssetsProp, s_eplItemLibProp, s_eplItemDataProp;
        private static PropertyInfo s_eplGenMarket, s_eplGenCost, s_eplPctChange, s_eplAvgCost;

        private static bool EplMarketBridge()
        {
            if (!s_eplProbed)
            {
                s_eplProbed = true;
                try
                {
                    // assembly-qualified bind first, app-domain type walk only if it misses -
                    // see Util.ModParity.ResolveType for why the walk is worth avoiding
                    const string EplAsm = "EnhancedPrefabLoader";
                    var t = Util.ModParity.ResolveType("EnhancedPrefabLoader.Core.EplRuntimeData", EplAsm);
                    var save = Util.ModParity.ResolveType("EnhancedPrefabLoader.Core.Models.SaveData.ItemSaveData", EplAsm);
                    const BindingFlags F = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                    s_eplAssetsProp = t?.GetProperty("Assets", F);
                    var assets = s_eplAssetsProp?.GetValue(null);
                    s_eplItemLibProp = assets?.GetType().GetProperty("ItemLibrary", F);
                    var lib = assets == null ? null : s_eplItemLibProp?.GetValue(assets);
                    s_eplItemDataProp = lib?.GetType().GetProperty("ItemData", F);
                    var services = t?.GetProperty("Services", F)?.GetValue(null);
                    s_eplSaveMgr = services?.GetType().GetProperty("SaveDataManager", F)?.GetValue(services);
                    if (s_eplSaveMgr != null && save != null)
                    {
                        foreach (var m in s_eplSaveMgr.GetType().GetMethods(F))
                        {
                            if (m.Name == "TryGetSaveData" && m.GetGenericArguments().Length == 2)
                            {
                                s_eplTryGet = m.MakeGenericMethod(typeof(EItemType), save);
                                break;
                            }
                        }
                    }

                    s_eplGenMarket = save?.GetProperty("GeneratedMarketPrice", F);
                    s_eplGenCost = save?.GetProperty("GeneratedCostPrice", F);
                    s_eplPctChange = save?.GetProperty("ItemPriceChangePercent", F);
                    s_eplAvgCost = save?.GetProperty("AverageItemCost", F);
                }
                catch (Exception e) { CoopLog.Swallow(e); }
                if (s_eplTryGet == null || s_eplItemDataProp == null || s_eplGenMarket == null
                    || s_eplGenCost == null || s_eplPctChange == null || s_eplAvgCost == null)
                {
                    s_eplTryGet = null; // all-or-nothing: half a bridge would desync the lists
                    CoopPlugin.Log.LogInfo("EPL market bridge inactive (EPL absent or its internals changed) - vanilla market only");
                }
                else
                {
                    CoopPlugin.Log.LogInfo("EPL market bridge active (modded item market data syncs)");
                }
            }
            return s_eplTryGet != null;
        }

        // With the bridge active the raw list is authoritative only below the woven
        // boundary; anything above it is shadow data (e.g. leftovers from a pre-fix
        // session) that must not ship. Without EPL the raw list is the whole truth.
        private static int VanillaWalkCount(List<float> list)
        {
            var n = list?.Count ?? 0;
            return EplMarketBridge() ? Mathf.Min(n, EplWeaveBoundary) : n;
        }

        /// <summary>The first id EPL's price-list weave serves from per-item save data:
        /// ItemPriceListHandler routes index >= 130 into its save-data surface and ItemTypeResolver
        /// enters its modded band at the same value. 130 is EItemType.Max, one past the last
        /// vanilla item on both supported builds, so the split is a numeric storage fact of EPL's
        /// weave - identical on every machine, independent of which content packs are installed.
        /// It must NOT be derived from the live item set (ItemLibrary.ItemData): the prepatcher
        /// pins every member it has EVER minted into the enum and never prunes it (see
        /// EplActiveEnums), while ItemData only holds currently installed packs, so subtracting
        /// ItemData classifies a stale mint as vanilla and lifts this boundary above every active
        /// modded id - sending modded ids into the raw list, which throws IndexOutOfRange.</summary>
        private static int EplWeaveBoundary => (int)EItemType.Max;

        /// <summary>True when this item id must be read/written through EPL's save data rather
        /// than the raw list. The id has already been translated into this process's enum space by
        /// the wire layer (CatalogIdMap); which STORE holds its prices is local layout that no
        /// translator can decide, and EPL decides it numerically.</summary>
        private static bool RoutesToEpl(int itemType)
            => EplMarketBridge() && itemType >= EplWeaveBoundary;

        // The raw list only holds rows the game itself indexes (below the weave boundary). An id
        // that still lands beyond the list's live length - a shorter saved list, e.g. the game's
        // 100-row pad - can only be dropped: indexing it would abort the whole snapshot for every
        // later row with an IndexOutOfRangeException.
        private static bool s_rawWriteMissLogged;

        private static void WriteRawItem(List<float> list, int itemType, float value, string what)
        {
            if (itemType >= 0 && itemType < list.Count)
            {
                list[itemType] = value;
                return;
            }

            if (s_rawWriteMissLogged)
            {
                return;
            }
            s_rawWriteMissLogged = true;
            CoopPlugin.Log.LogWarning("[market] dropped " + what + " for item " + itemType
                + ": the local item price list holds " + list.Count + " rows, so the id cannot be"
                + " stored (further drops this session are not logged).");
        }

        /// <summary>The raw itemType ids of EPL's CURRENTLY LOADED modded items (ItemLibrary.ItemData
        /// keys); empty without the bridge. Used to zero/track the local content that has EPL save
        /// rows - never as an id boundary: a storage route is the weave threshold
        /// (<see cref="EplWeaveBoundary"/>), because this set ignores stale mints the enum still
        /// carries, and any member absent here is not evidence it is vanilla.</summary>
        private static List<int> EplModdedItemTypes()
        {
            // The EPL registry is fixed after content registration. Populate once and only
            // invalidate from explicit scene/content/session lifecycle hooks.
            if (s_eplModdedCache != null)
            {
                return s_eplModdedCache;
            }

            var result = new List<int>();
            if (EplMarketBridge())
            {
                try
                {
                    var assets = s_eplAssetsProp.GetValue(null);
                    var lib = assets == null ? null : s_eplItemLibProp.GetValue(assets);
                    var dict = lib == null ? null : s_eplItemDataProp.GetValue(lib) as System.Collections.IDictionary;
                    if (dict != null)
                    {
                        foreach (var key in dict.Keys)
                        {
                            result.Add(Convert.ToInt32(key));
                        }
                    }
                }
                catch (Exception e) { CoopLog.Swallow(e); }
            }
            s_eplModdedCache = result;
            return result;
        }

        private static object EplSaveData(int itemType)
        {
            var args = new object[] { (EItemType)itemType, null };
            if (!(bool)s_eplTryGet.Invoke(s_eplSaveMgr, args))
            {

                throw new InvalidOperationException("EPL has no market row for item " + itemType + ".");
            }


            return args[1];
        }

        private static float EplGetFloat(int itemType, PropertyInfo field)
        {
            var d = EplSaveData(itemType);
            return d == null ? 0f : (float)field.GetValue(d, null);
        }

        private static void EplSetFloat(int itemType, PropertyInfo field, float value)
        {
            var d = EplSaveData(itemType);
            field.SetValue(d, value, null);
        }

        private static List<KeyValuePair<int, float>> CollectModded(List<int> modded, PropertyInfo field)
        {
            var vals = new List<KeyValuePair<int, float>>(modded.Count);
            if (field != null)
            {
                for (var k = 0; k < modded.Count; k++)
                {
                    var v = EplGetFloat(modded[k], field);
                    if (v != 0f)
                    {
                        vals.Add(new KeyValuePair<int, float>(modded[k], v));
                    }
                }
            }

            return vals;
        }

        // modded-index writers consumed by ReadSparseFloatsInto after RoutesToEpl picked the
        // save-data path
        private static void ModdedGenMarket(int itemType, float v)
        {
            EplSetFloat(itemType, s_eplGenMarket, v);
        }
        private static void ModdedGenCost(int itemType, float v)
        {
            EplSetFloat(itemType, s_eplGenCost, v);
        }
        private static void ModdedAvgCost(int itemType, float v)
        {
            // the GAME's setter: no math in its body, and its woven form routes
            // >= 130 (the weave boundary) into EPL save data exactly like the reflection path
            CPlayerData.SetAverageItemCost((EItemType)itemType, v);
        }
    }
}
