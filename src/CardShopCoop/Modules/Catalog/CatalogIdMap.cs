using System;
using System.Collections.Generic;
using CardShopCoop.Net.Messages;

namespace CardShopCoop.Modules.Catalog
{
    /// <summary>The enum kinds whose numeric values are translated between peers. Six are enums
    /// EnhancedPrefabLoader mints ids into (see CatalogParity's ModdedEnumTypes); MonsterType is
    /// CardForge's, whose name-and-id list the handshake requires to match exactly, so its values
    /// are identical on both peers and its missing table resolves to the identity.</summary>
    public enum EnumKind
    {
        ItemType = 0,      // EItemType
        ObjectType = 1,    // EObjectType
        DecoObject = 2,    // EDecoObject
        CardExpansion = 3, // ECardExpansionType
        MonsterType = 4,   // EMonsterType (CreateCards/CardForge, NOT EPL)
        Rarity = 5,        // ERarity
        CollectionPack = 6, // ECollectionPackType
    }

    /// <summary>
    /// Value translation for the enum kinds used by the co-op protocol. Every enum on the wire and
    /// in a transferred save travels in the HOST's numeric space: the host is the identity
    /// translation, and the client builds <see cref="EnumValueMap"/> from the host's typed enum
    /// identity carried by the handshake, paired member-by-member by NAME. Ids are never compared
    /// for equality and no registry file is read.
    /// </summary>
    internal static class CatalogIdMap
    {
        private static readonly object _mapLock = new();
        private static EnumValueMap _enumMap;

        /// <summary>
        /// The per-kind value bijection between the host's numeric enum ids and this process's.
        /// Built on the CLIENT from the host's typed enum identity (the handshake value tables)
        /// paired to this process's members by NAME, ordinally first-name-wins so aliases pick the
        /// same canonical value on both peers. The HOST has no map: an absent map is the identity,
        /// so the host writes and reads its own raw values with zero translation. A value with no
        /// counterpart at all (a sentinel such as None, or an unknown id) is absent from the map
        /// and therefore passes through unchanged.
        /// </summary>
        private sealed class EnumValueMap
        {
            private readonly Dictionary<EnumKind, Dictionary<int, int>> _hostToLocal = new();
            private readonly Dictionary<EnumKind, Dictionary<int, int>> _localToHost = new();

            public void Add(EnumKind kind, int hostValue, int localValue)
            {
                if (!_hostToLocal.TryGetValue(kind, out var forward))
                {
                    _hostToLocal[kind] = forward = new Dictionary<int, int>();
                }

                if (!forward.ContainsKey(hostValue))
                {
                    forward[hostValue] = localValue;
                }

                if (!_localToHost.TryGetValue(kind, out var reverse))
                {
                    _localToHost[kind] = reverse = new Dictionary<int, int>();
                }

                if (!reverse.ContainsKey(localValue))
                {
                    reverse[localValue] = hostValue;
                }
            }

            public bool TryHostToLocal(EnumKind kind, int hostValue, out int localValue)
            {
                localValue = hostValue;
                return _hostToLocal.TryGetValue(kind, out var forward)
                    && forward.TryGetValue(hostValue, out localValue);
            }

            public bool TryLocalToHost(EnumKind kind, int localValue, out int hostValue)
            {
                hostValue = localValue;
                return _localToHost.TryGetValue(kind, out var reverse)
                    && reverse.TryGetValue(localValue, out hostValue);
            }

            public bool HasKind(EnumKind kind) => _hostToLocal.ContainsKey(kind);

            public int Kinds => _hostToLocal.Count;
        }

        /// <summary>The last "value map ready" summary actually logged at Info, so an identical
        /// one is demoted instead of repeating.
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

        // ------------------------------------------------------------------ value-map API

        /// <summary>Translate THIS process's runtime value into the host's numeric space, which is
        /// the one space every enum on the wire and in a transferred save uses. An absent map (the
        /// host's own process) or an unmapped value is the identity.</summary>
        internal static int ToHostValue(EnumKind kind, int localValue)
        {
            var map = _enumMap;
            if (map != null && map.TryLocalToHost(kind, localValue, out var hostValue))
            {
                return hostValue;
            }

            return localValue;
        }

        /// <summary>Translate a HOST numeric value into THIS process's runtime value using the
        /// handshake map. Unmapped values (sentinels, unknown ids) pass through unchanged.</summary>
        internal static int FromHostValue(EnumKind kind, int hostValue)
        {
            var map = _enumMap;
            if (map != null && map.TryHostToLocal(kind, hostValue, out var localValue))
            {
                return localValue;
            }

            return hostValue;
        }

        internal static bool HasEnumMap => _enumMap != null;

        /// <summary>Like <see cref="FromHostValue"/> but reports whether the value actually had a
        /// counterpart. A kind with no table at all (this process is the host, or the kind never
        /// carried a host table such as MonsterType) is treated as the identity and reports true;
        /// only a kind that HAS a table but lacks this specific value reports false (unresolved).</summary>
        internal static bool TryFromHostValue(EnumKind kind, int hostValue, out int localValue)
        {
            var map = _enumMap;
            if (map == null || !map.HasKind(kind))
            {
                localValue = hostValue;
                return true;
            }

            return map.TryHostToLocal(kind, hostValue, out localValue);
        }

        // ------------------------------------------------------------------ type identity

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
                case EnumKind.Rarity:
                    return typeof(ERarity);
                case EnumKind.CollectionPack:
                    return typeof(ECollectionPackType);
                default:
                    throw new ArgumentOutOfRangeException("kind", kind, "Unknown enum kind");
            }
        }

        /// <summary>True when <paramref name="value"/> is a DEFINED member of the runtime enum for
        /// this kind. EPL injects its minted members into the loaded enum at prepatch, so on an EPL
        /// machine this validates vanilla and modded values alike - unlike a numeric range check,
        /// a value merely inside the modded band but absent from the enum returns false.</summary>
        internal static bool IsDefined(EnumKind kind, int value)
        {
            try
            {
                return Enum.IsDefined(WireType(kind), value);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Drop the negotiated value map. Called from CoopCore.Shutdown so the next
        /// session starts with the identity (host-style) translation until a client Build
        /// establishes a new map.</summary>
        public static void Clear()
        {
            lock (_mapLock)
            {
                _enumMap = null;
            }
        }

        // ------------------------------------------------------------------ construction

        /// <summary>CLIENT ONLY: establish the enum value map from the host's typed identity. The
        /// handshake has already rejected any differing enum key set, so no one-sided or
        /// intersection mapping is possible here.</summary>
        public static void Build(List<EnumKindIdentityDto> hostEnumIdentity)
        {
            Clear();
            _buildCount++; // every Build is a join; Clear() must not reset this (see the field)
            try
            {
                var enumMap = BuildEnumMap(hostEnumIdentity);
                lock (_mapLock)
                {
                    _enumMap = enumMap;
                }
                var line = "value map ready (" + enumMap.Kinds + " enum kinds)";
                if (!string.Equals(line, _lastSummary, StringComparison.Ordinal))
                {
                    _lastSummary = line;
                    Log(line);
                }
                else
                {
                    Log("value map ready (unchanged, join #" + _buildCount + ")");
                }
            }
            catch (Exception e)
            {
                // Translation must never be the thing that breaks a join. Clear the negotiated
                // map so the process returns to its local canonical source rather than retaining
                // a partial map.
                Clear();
                LogWarn("value map could not be built (" + e.Message + ") - running with local ids");
            }
        }

        /// <summary>Pair the host's typed enum members with this process's by NAME, producing the
        /// value bijection. Members are ordinal-sorted by the identity builder and first-name-wins,
        /// so aliases canonicalize identically on both peers.</summary>
        private static EnumValueMap BuildEnumMap(List<EnumKindIdentityDto> hostIdentity)
        {
            var map = new EnumValueMap();
            if (hostIdentity == null || hostIdentity.Count == 0)
            {
                return map;
            }

            var localByKind = new Dictionary<int, Dictionary<string, int>>();
            foreach (var kind in CatalogParity.EnumIdentity())
            {
                if (kind == null || kind.Members == null)
                {
                    continue;
                }

                var byName = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var member in kind.Members)
                {
                    if (member != null && member.Name != null
                        && member.Value >= int.MinValue && member.Value <= int.MaxValue)
                    {
                        byName[member.Name] = (int)member.Value;
                    }
                }

                localByKind[kind.Kind] = byName;
            }

            foreach (var hostKind in hostIdentity)
            {
                if (hostKind == null || hostKind.Members == null
                    || !localByKind.TryGetValue(hostKind.Kind, out var locals))
                {
                    continue;
                }

                var kind = (EnumKind)hostKind.Kind;
                var mapped = 0;
                var changed = 0;
                foreach (var member in hostKind.Members)
                {
                    if (member == null || member.Name == null
                        || member.Value < int.MinValue || member.Value > int.MaxValue
                        || !locals.TryGetValue(member.Name, out var localValue))
                    {
                        continue;
                    }

                    map.Add(kind, (int)member.Value, localValue);
                    mapped++;
                    if ((int)member.Value != localValue)
                    {
                        changed++;
                    }

                    if (member.Name == "BaseSetBoosterPacks"
                        || member.Name == "ykOPDenDenMushi_Mugiwaras"
                        || member.Name == "BaseSet")
                    {
                        Log("sample " + kind + " " + member.Name + " host=" + member.Value
                            + " local=" + localValue);
                    }
                }

                Log("map " + kind + " mapped=" + mapped + " changed=" + changed);
            }

            return map;
        }

        // ------------------------------------------------------------------ internals

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
