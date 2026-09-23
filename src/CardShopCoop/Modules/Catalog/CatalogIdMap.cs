using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CardShopCoop.Modules.Catalog
{
    /// <summary>The enum kinds whose values cross the wire by name. Four are enums
    /// EnhancedPrefabLoader mints into (see CatalogParity's ModdedEnumTypeNames); MonsterType
    /// is not EPL's at all - custom monsters come from CreateCards/CardForge .ini files and use
    /// the exact CardForge name-and-id list validated during the handshake.</summary>
    public enum EnumKind
    {
        ItemType = 0,      // EItemType
        ObjectType = 1,    // EObjectType
        DecoObject = 2,    // EDecoObject
        CardExpansion = 3, // ECardExpansionType
        MonsterType = 4,   // EMonsterType (CreateCards/CardForge, NOT EPL)
    }

    /// <summary>
    /// NAME-BASED wire identity for the enum kinds used by the co-op protocol. Vanilla and
    /// custom enum values use their CLR enum member names. CardForge MonsterType values are not
    /// enum members, so they use their canonical CardForge names. The handshake requires the
    /// complete CardForge name-and-id list to match before this map is built.
    /// </summary>
    internal static class CatalogIdMap
    {
        private static readonly HashSet<string> _loggedNameMisses = new(StringComparer.Ordinal);
        private static readonly Dictionary<Type, NameTable> _nameTables = new();
        private static readonly object _nameTableLock = new();
        private static CardNameTable _cardNameTable;
        private static int _nameWrites;
        private static bool _nameSummaryLogged;

        private sealed class NameTable
        {
            public readonly Dictionary<int, string> ValueToName;
            public readonly Dictionary<string, int> NameToValue;
            public NameTable(Type type)
            {
                ValueToName = new Dictionary<int, string>();
                NameToValue = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var value in Enum.GetValues(type))
                {
                    var id = Convert.ToInt32(value);
                    var name = Enum.GetName(type, value);
                    if (name != null)
                    {
                        ValueToName[id] = name;
                        NameToValue[name] = id;
                    }
                }
            }
        }

        /// <summary>Canonical CardForge names for the MonsterType name wire. The table is built
        /// from this process's CardForge list; exact name-and-id parity was already required by
        /// the handshake, so both directions use the same numeric identity on both peers.</summary>
        private sealed class CardNameTable
        {
            public readonly Dictionary<int, string> LocalIdToName;
            public readonly Dictionary<string, int> NameToLocalId;

            public CardNameTable(Dictionary<int, string> localIdToName,
                Dictionary<string, int> nameToLocalId)
            {
                LocalIdToName = localIdToName;
                NameToLocalId = nameToLocalId;
            }
        }

        /// <summary>The last "id translation ready" summary actually logged at Info, so an
        /// identical one is demoted to Debug instead of repeating.
        ///
        /// DO NOT reset this in <see cref="Clear"/>. Clear runs on EVERY teardown and on BOTH
        /// host-start paths, i.e. at least once between any two Builds - clearing it would make
        /// every summary look new again, restore the per-join spam, and turn this into a no-op.
        /// A stale value across sessions is harmless: it only ever suppresses a duplicate line,
        /// and the moment the content set differs the text differs and it logs.</summary>
        private static string _lastSummary;

        /// <summary>How many times <see cref="Build"/> has run this process - i.e. the join
        /// number, since Build has exactly one call site (the client's Welcome handler). Printed
        /// with the repeat line so a field log still shows that a re-join HAPPENED even when the
        /// summary text is identical. NOT reset in <see cref="Clear"/>, same rule and same reason
        /// as <see cref="_lastSummary"/> above (Clear runs between any two Builds).</summary>
        private static int _buildCount;

        /// <summary>The value handed back for a modded id with no counterpart on this PC. The
        /// game's OWN "nothing" member for that enum, checked against the decompiled sources
        /// rather than assumed - because the convention is NOT uniform: EItemType, EObjectType
        /// and ECardExpansionType all declare None = -1, but EDecoObject.None = 0 and
        /// EMonsterType.None = 0 (EMonsterType's -1 is EarlyPlayer, a real member). Handing -1
        /// to a kind whose None is 0 would produce a value that is not a member at all.
        ///
        /// NOTE for anyone extending this: every one of these sentinels IS a defined enum
        /// member, so an Enum.IsDefined-style guard does NOT reject it on its own. Any skip
        /// path that has to refuse an unmappable id must test for None explicitly.</summary>
        public static int Sentinel(EnumKind kind)
        {
            switch (kind)
            {
                case EnumKind.DecoObject:
                    return 0;  // EDecoObject.None = 0
                case EnumKind.MonsterType:
                    return 0; // EMonsterType.None = 0 (EarlyPlayer = -1)
                default:
                    return -1;                  // EItemType/EObjectType/ECardExpansionType.None = -1
            }
        }

        // ------------------------------------------------------------------ name-wire API

        internal static Type WireType(EnumKind kind)
        {
            switch (kind)
            {
                case EnumKind.ItemType:
                    return typeof(EItemType);
                case EnumKind.ObjectType:
                    return typeof(EObjectType);
                case EnumKind.DecoObject:
                    return typeof(EDecoObject);
                case EnumKind.CardExpansion:
                    return typeof(ECardExpansionType);
                case EnumKind.MonsterType:
                    return typeof(EMonsterType);
                default:
                    throw new ArgumentOutOfRangeException("kind", kind, "Unknown enum kind");
            }
        }

        internal static string ToWireName(EnumKind kind, int value)
        {
            return ToWireName(WireType(kind), kind, value);
        }

        internal static string ToWireName(Type type, EnumKind kind, int value)
        {
            var table = Names(type);
            string name;
            if (kind == EnumKind.MonsterType
                && CardNames().LocalIdToName.TryGetValue(value, out name))
            {
                // CardForge owns this numeric identity even when the CLR enum has a member at
                // the same value. Sending the card name preserves that distinction on decode.
            }
            else if (!table.ValueToName.TryGetValue(value, out name))
            {
                throw new JsonSerializationException("Undefined " + type.Name + " value " + value + " cannot be sent on the wire");
            }

            lock (_nameTableLock)
            {
                _nameWrites++;
                if (!_nameSummaryLogged)
                {
                    _nameSummaryLogged = true;
                    Log("name wire active (values sent by name: " + _nameWrites + ")");
                }
            }
            return name;
        }

        internal static bool TryFromWireName(EnumKind kind, string name, out int value)
        {
            return TryFromWireName(WireType(kind), kind, name, out value);
        }

        internal static bool TryFromWireName(Type type, EnumKind kind, string name, out int value)
        {
            if (name == null)
            {
                throw new JsonSerializationException("Null " + type.Name + " enum name");
            }

            if (kind == EnumKind.MonsterType
                && CardNames().NameToLocalId.TryGetValue(name, out value))
            {
                return true;
            }

            var table = Names(type);
            if (table.NameToValue.TryGetValue(name, out value))
            {
                return true;
            }

            value = Sentinel(kind);
            LogNameMissOnce(kind, name);
            return false;
        }

        internal static bool TryReadWireName(EnumKind kind, JToken token, out int value)
        {
            var type = WireType(kind);
            if (token == null || token.Type == JTokenType.Null)
            {
                throw new JsonSerializationException("Null " + type.Name + " enum");
            }

            if (token.Type != JTokenType.String)
            {
                throw new JsonSerializationException("Numeric/non-string " + type.Name + " value " + token.ToString(Formatting.None) + " received: peer is sending pre-name wire");
            }

            return TryFromWireName(type, kind, token.Value<string>(), out value);
        }

        private static NameTable Names(Type type)
        {
            lock (_nameTableLock)
            {
                NameTable table;
                if (!_nameTables.TryGetValue(type, out table))
                {
                    table = new NameTable(type);
                    _nameTables.Add(type, table);
                }
                return table;
            }
        }

        /// <summary>Drop the negotiated card-name table. Called from CoopCore.Shutdown so the
        /// next session starts with the host's local canonical card list until a client Build
        /// establishes a new negotiated table.</summary>
        public static void Clear()
        {
            lock (_nameTableLock)
            {
                _cardNameTable = null;
            }
            lock (_loggedNameMisses)
            {
                _loggedNameMisses.Clear();
            }
        }

        // ------------------------------------------------------------------ construction

        /// <summary>CLIENT ONLY: establish the CardForge name table from this process's local
        /// list. The handshake has already rejected any differing CardForge name or id list, so
        /// no one-sided or intersection mapping is possible here. The enum-lines parameter is
        /// retained for the existing Welcome call shape; enum member names need no table.</summary>
        public static void Build(List<string> hostEnumLines, List<string> hostCardLines)
        {
            Clear();
            _buildCount++; // every Build is a join; Clear() must not reset this (see the field)
            try
            {
                var ourCards = ParseCardLines(SafeLines(CatalogParity.CardsList));
                var cardNames = BuildCardNameTable(ourCards);
                lock (_nameTableLock)
                {
                    _cardNameTable = cardNames;
                }
                var line = "name wire ready (CardForge MonsterType speaks canonical names): "
                    + cardNames.LocalIdToName.Count + " CardForge names";
                if (!string.Equals(line, _lastSummary, StringComparison.Ordinal))
                {
                    _lastSummary = line;
                    Log(line);
                }
                else
                {
                    Log("name wire ready (unchanged, join #" + _buildCount + ")");
                }
            }
            catch (Exception e)
            {
                // Name translation must never be the thing that breaks a join. Clear the
                // negotiated table so the process returns to its host-style local canonical
                // source rather than retaining a partial table.
                Clear();
                LogWarn("name wire could not be built (" + e.Message + ") - running with local card names");
            }
        }

        /// <summary>Build the name-wire pair from the local CardForge list. Numeric ids are
        /// deliberately not sent; the exact CardForge name-and-id handshake guarantees that the
        /// same canonical name resolves to the same numeric value on both peers.</summary>
        private static CardNameTable BuildCardNameTable(Dictionary<string, int> cards)
        {
            var localIdToName = new Dictionary<int, string>();
            var nameToLocalId = new Dictionary<string, int>(StringComparer.Ordinal);
            if (cards == null)
            {
                return new CardNameTable(localIdToName, nameToLocalId);
            }

            foreach (var kv in cards)
            {
                localIdToName[kv.Value] = kv.Key;
                nameToLocalId[kv.Key] = kv.Value;
            }

            return new CardNameTable(localIdToName, nameToLocalId);
        }

        /// <summary>Before a client receives Welcome, use this process's CardForge list. After
        /// Build, the exact-parity session table replaces this identity table.</summary>
        private static CardNameTable CardNames()
        {
            lock (_nameTableLock)
            {
                if (_cardNameTable == null)
                {
                    var localCards = ParseCardLines(SafeLines(CatalogParity.CardsList));
                    _cardNameTable = BuildCardNameTable(localCards);
                }

                return _cardNameTable;
            }
        }

        // ------------------------------------------------------------------ internals

        private static void LogNameMissOnce(EnumKind kind, string name)
        {
            var key = ((int)kind) + ":" + name;
            bool first;
            lock (_loggedNameMisses)
            {
                first = _loggedNameMisses.Add(key);
            }
            if (first)
            {
                Log("unknown " + kind + " wire name " + name
                    + " - using None; further occurrences of this name are silent");
            }
        }

        /// <summary>"MonsterName=id" lines (CatalogParity.CardsList) -&gt; name-&gt;id. No floor filter:
        /// CardForge ids can occupy the same numeric band as vanilla MonsterType values.</summary>
        private static Dictionary<string, int> ParseCardLines(List<string> lines)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            if (lines == null)
            {
                return map;
            }

            foreach (var raw in lines)
            {
                if (string.IsNullOrEmpty(raw))
                {
                    continue;
                }

                var line = raw.Trim();
                var eq = line.LastIndexOf('=');
                if (eq <= 0 || eq == line.Length - 1)
                {
                    continue;
                }

                int id;
                if (!int.TryParse(line.Substring(eq + 1).Trim(), out id))
                {
                    continue;
                }

                map[line.Substring(0, eq).Trim()] = id;
            }
            return map;
        }

        /// <summary>Our own registry reads must never throw into a session handshake.</summary>
        private static List<string> SafeLines(Func<List<string>> f)
        {
            try
            {
                return f() ?? new List<string>();
            }
            catch (Exception e) { Swallow.Log(e); return new List<string>(); }
        }

        private static void Log(string s)
        {
            try
            {
                CoopPlugin.Log.LogInfo("CatalogIdMap: " + s);
            }
            catch (Exception e) { Swallow.Log(e); }
        }

        private static void LogWarn(string s)
        {
            try
            {
                CoopPlugin.Log.LogWarning("CatalogIdMap: " + s);
            }
            catch (Exception e) { Swallow.Log(e); }
        }
    }
}



