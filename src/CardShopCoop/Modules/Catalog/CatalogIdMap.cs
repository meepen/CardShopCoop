using System;
using System.Collections.Generic;
using CardShopCoop.Net.Messages;
using CardShopCoop.Api;
using CardShopCoop.Modules.Hud;

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
    ///
    /// Two game builds can carry members the other build simply does not have. For the handful of
    /// <c>EObjectType</c> members that differ between the public build and the 1.00 beta this table
    /// coalesces a name the peer cannot represent onto a local equivalent, or drops it when there is
    /// no equivalent at all (see <see cref="ObjectTypeAliases"/> / <see cref="ObjectTypeDropped"/>).
    /// </summary>
    internal static class CatalogIdMap
    {
        private static readonly object _mapLock = new();
        private static EnumValueMap _enumMap;

        /// <summary>The PEER's enum members by name, per kind - the handshake identity the peer
        /// advertised. On the client this is the host's identity (set by <see cref="Build"/>); on
        /// the host it is the joining client's identity (set by <see cref="SetPeerIdentity"/>).
        /// Null means no peer is known (no session, or before the handshake carried an identity);
        /// the pre-send gate must never refuse in that state.</summary>
        private static Dictionary<EnumKind, Dictionary<string, int>> _peerNames;

        // ------------------------------------------------------------------ coalesce table

        /// <summary>The <c>EObjectType</c> members the 1.00 (Game Pass) build has and the public
        /// build does not, mapped onto the equivalent member both builds share. The mappings are
        /// deliberate, verified against both decompiled <c>EObjectType.cs</c> files, and change no
        /// gameplay: a full-tree grep shows those members are mentioned only in their entitlement
        /// manager and the enum itself, so nothing branches on them. Ids in the comments are the
        /// 1.00 ids; the other build's ids are irrelevant because pairing is by NAME.</summary>
        private static readonly Dictionary<string, string> ObjectTypeAliases =
            new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "PSPreOrderPlaytable", "PlayTable" },              // 56 -> 11
            { "XboxPreOrderPlaytable", "PlayTable" },            // 59 -> 11
            { "PSPreOrderCardTable", "VintageCardTable" },       // 57 -> 23
            { "XboxPreOrderCardTable", "VintageCardTable" },     // 60 -> 23
            { "PSPreOrderPackOpener", "AutoPackOpener1" },       // 58 -> 34
            { "XboxPreOrderPackOpener", "AutoPackOpener1" },     // 61 -> 34
            { "DeluxeInfiniteScentMachine", "AutoCleanser1" },   // 62 -> 31
        };

        /// <summary><c>EObjectType</c> members with NO equivalent on the other build. They are
        /// dropped (mapped to the kind's sentinel) and reported once, never aliased onto an
        /// unrelated member where they would silently become a different object.</summary>
        private static readonly HashSet<string> ObjectTypeDropped =
            new HashSet<string>(StringComparer.Ordinal)
        {
            "FoodBowl", // public build only, id 56
        };

        /// <summary>The local <c>EObjectType</c> member an alias source coalesces onto, or null
        /// when the name is not an alias source.</summary>
        internal static string ObjectTypeAliasTarget(string memberName)
            => memberName != null && ObjectTypeAliases.TryGetValue(memberName, out var target)
                ? target : null;

        /// <summary>True when <paramref name="memberName"/> is an <c>EObjectType</c> the other
        /// build cannot represent and that has no local equivalent: it is dropped, not coalesced.</summary>
        internal static bool IsObjectTypeDropped(string memberName)
            => memberName != null && ObjectTypeDropped.Contains(memberName);

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
            return kind switch
            {
                EnumKind.DecoObject => 0,// EDecoObject.None = 0
                EnumKind.MonsterType => 0,// EMonsterType.None = 0 (EarlyPlayer = -1)
                _ => -1,// EItemType/EObjectType/ECardExpansionType.None = -1
            };

        }

        // ------------------------------------------------------------------ value-map API

        /// <summary>Translate THIS process's runtime value into the host's numeric space, which is
        /// the one space every enum on the wire and in a transferred save uses. An absent map (the
        /// host's own process) or an unmapped value is the identity. A local member the peer lacks
        /// but that has a peer equivalent (the <see cref="ObjectTypeAliases"/> table) is coalesced
        /// onto the peer's equivalent so the peer does not receive an id it cannot name.</summary>
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
        /// handshake map. Unmapped values pass through unchanged: the handshake identity predates
        /// content mods finishing their runtime enum registration, so members a mod mints after the
        /// handshake are absent from the map even though both peers mint the same ids. Treating the
        /// miss as the identity (rather than 0 or "unresolved") is what lets those late-registered
        /// object types match, so a guest binds their shelves and can place/price cards on them.
        ///
        /// A value whose name this build lacks but for which the coalesce table has an equivalent is
        /// resolved to that equivalent; a value with no equivalent (a dropped member) resolves to the
        /// kind's sentinel and is reported once through the mod's status channel.</summary>
        internal static int FromHostValue(EnumKind kind, int hostValue)
        {
            var map = _enumMap;
            if (map != null && map.TryHostToLocal(kind, hostValue, out var localValue))
            {
                if (map.HasDrops && map.TryDroppedName(kind, hostValue, out var droppedName))
                {
                    NotifyDroppedOnce(kind, droppedName);
                }

                return localValue;
            }

            return hostValue;
        }

        // ------------------------------------------------------------------ peer representability

        /// <summary>True once the peer's enum identity is known for the current session. The
        /// pre-send gate refuses only while this is true: no session, no peer yet, or a disconnected
        /// peer must never block a local purchase. An EMPTY identity is "no peer" too - the handshake
        /// tolerates a peer that resolved no enum members, and treating that as a known peer whose
        /// every type is unrepresentable would refuse every furniture purchase for the session.</summary>
        internal static bool HasPeerIdentity => _peerNames != null && _peerNames.Count > 0;

        /// <summary>Record the PEER's enum identity on the HOST, after the joining client's Hello
        /// passed admission. The host has no value map (the wire is its own space); this identity is
        /// kept only so the pre-send gate can ask whether the client can represent an object type.</summary>
        internal static void SetPeerIdentity(List<EnumKindIdentityDto> peerIdentity)
        {
            lock (_mapLock)
            {
                _peerNames = NameTable(peerIdentity);
            }
        }

        /// <summary>Forget the peer identity without touching the value map or the session notice
        /// state. Called when a peer disconnects while the host keeps hosting, so a departed peer
        /// cannot keep blocking local purchases.</summary>
        internal static void ClearPeerIdentity()
        {
            lock (_mapLock)
            {
                _peerNames = null;
            }
        }

        /// <summary>Whether the PEER build can represent <paramref name="value"/> of
        /// <paramref name="kind"/> BY NAME. An exact name match is representable; a KNOWN
        /// cross-build divergence the peer does not have is NOT, even when the coalesce table has an
        /// equivalent - the alias exists for the INGEST direction (rendering a richer host's object
        /// as our equivalent), never for egress, where it would let the peer store the vanilla target
        /// and reconcile our own object down to it. A name absent from both the peer identity and
        /// the coalesce table is a member minted after the handshake (EPL bundle load): those travel
        /// by numeric identity on both peers, so they are treated as representable rather than
        /// blocked.</summary>
        internal static bool CanPeerRepresent(EnumKind kind, int value)
        {
            var peer = _peerNames;
            if (peer == null || peer.Count == 0 || !peer.TryGetValue(kind, out var peerNames))
            {
                // No peer, or the peer advertised no members of this kind: nothing is known to be
                // unrepresentable, so the gate must not refuse (it is a guard, not an authority).
                return true;
            }

            var name = LocalName(kind, value);
            if (name == null)
            {
                return false;
            }

            if (peerNames.ContainsKey(name))
            {
                return true;
            }

            if (kind == EnumKind.ObjectType
                && (ObjectTypeAliases.ContainsKey(name) || ObjectTypeDropped.Contains(name)))
            {
                // The peer does not have this member by name (the exact check above already
                // returned). An alias source would egress as its vanilla equivalent and be stored
                // as such - a silent, irreversible downgrade - so it is NOT representable.
                return false;
            }

            return true;
        }

        /// <summary>The local member name for a numeric value, or null when the value is not a
        /// defined member of this build's enum for the kind.</summary>
        private static string LocalName(EnumKind kind, int value)
        {
            try
            {
                var type = WireType(kind);
                return Enum.IsDefined(type, value) ? Enum.GetName(type, value) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ type identity

        internal static Type WireType(EnumKind kind)
        {
            return kind switch
            {
                EnumKind.ItemType => typeof(EItemType),
                EnumKind.ObjectType => typeof(EObjectType),
                EnumKind.DecoObject => typeof(EDecoObject),
                EnumKind.CardExpansion => typeof(ECardExpansionType),
                EnumKind.MonsterType => typeof(EMonsterType),
                EnumKind.Rarity => typeof(ERarity),
                EnumKind.CollectionPack => typeof(ECollectionPackType),
                _ => throw new ArgumentOutOfRangeException("kind", kind, "Unknown enum kind"),
            };

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
                _peerNames = null;
                _droppedNoticeShown = false;
            }
        }

        // ------------------------------------------------------------------ construction

        /// <summary>CLIENT ONLY: establish the enum value map from the host's typed identity. The
        /// handshake has already rejected any differing enum key set except the coalesce table, so
        /// the only one-sided names left are the aliased/dropped ones handled here.</summary>
        public static void Build(List<EnumKindIdentityDto> hostEnumIdentity)
        {
            Clear();
            _buildCount++; // every Build is a join; Clear() must not reset this (see the field)
            try
            {
                var peerNames = NameTable(hostEnumIdentity);
                var localNames = NameTable(CatalogParity.EnumIdentity());
                var enumMap = BuildEnumMap(peerNames, localNames);
                lock (_mapLock)
                {
                    _peerNames = peerNames;
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

        /// <summary>Turn a typed enum identity into a per-kind name table. The handshake identity
        /// is validated (unique names) before it reaches here, so first-wins is only a safety net.</summary>
        private static Dictionary<EnumKind, Dictionary<string, int>> NameTable(
            List<EnumKindIdentityDto> identity)
        {
            var table = new Dictionary<EnumKind, Dictionary<string, int>>();
            if (identity == null)
            {
                return table;
            }

            foreach (var kind in identity)
            {
                if (kind == null || kind.Members == null)
                {
                    continue;
                }

                var names = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var member in kind.Members)
                {
                    if (member != null && member.Name != null
                        && member.Value >= int.MinValue && member.Value <= int.MaxValue)
                    {
                        names[member.Name] = (int)member.Value;
                    }
                }

                table[(EnumKind)kind.Kind] = names;
            }

            return table;
        }

        /// <summary>Pair the peer's typed enum members with this process's by NAME, producing the
        /// value bijection. Exact name matches are laid down first (both directions, first-wins);
        /// then the <c>EObjectType</c> coalesce table adds a forward alias/drop for a peer name this
        /// build lacks, and a reverse alias for a local name the peer lacks. Members are
        /// ordinal-sorted by the identity builder so two peers canonicalize identically.</summary>
        private static EnumValueMap BuildEnumMap(
            Dictionary<EnumKind, Dictionary<string, int>> peerNames,
            Dictionary<EnumKind, Dictionary<string, int>> localNames)
        {
            var map = new EnumValueMap();
            foreach (EnumKind kind in Enum.GetValues(typeof(EnumKind)))
            {
                if (!peerNames.TryGetValue(kind, out var peer) || peer.Count == 0)
                {
                    continue;
                }

                if (!localNames.TryGetValue(kind, out var local) || local == null)
                {
                    local = new Dictionary<string, int>(StringComparer.Ordinal);
                }

                var mapped = 0;
                var changed = 0;
                var coalesced = 0;

                // Exact matches first: a shared name always wins over any alias, in both directions.
                foreach (var pair in peer)
                {
                    if (!local.TryGetValue(pair.Key, out var localValue))
                    {
                        continue;
                    }

                    map.AddExact(kind, pair.Value, localValue);
                    mapped++;
                    if (pair.Value != localValue)
                    {
                        changed++;
                    }

                    if (pair.Key == "BaseSetBoosterPacks"
                        || pair.Key == "ykOPDenDenMushi_Mugiwaras"
                        || pair.Key == "BaseSet")
                    {
                        Log("sample " + kind + " " + pair.Key + " peer=" + pair.Value
                            + " local=" + localValue);
                    }
                }

                if (kind == EnumKind.ObjectType)
                {
                    // Peer members this build cannot represent: coalesce onto a local equivalent,
                    // else drop to the kind's sentinel (reported once by FromHostValue).
                    foreach (var pair in peer)
                    {
                        if (local.ContainsKey(pair.Key))
                        {
                            continue;
                        }

                        if (ObjectTypeAliases.TryGetValue(pair.Key, out var target)
                            && local.TryGetValue(target, out var localValue))
                        {
                            map.AddAlias(kind, pair.Value, localValue);
                            coalesced++;
                        }
                        else if (ObjectTypeDropped.Contains(pair.Key))
                        {
                            map.AddDropped(kind, pair.Value, Sentinel(kind), pair.Key);
                            coalesced++;
                        }
                    }

                    // Local members the peer cannot represent: coalesce onto the peer's equivalent
                    // so this process never sends an id the peer cannot name.
                    foreach (var pair in local)
                    {
                        if (peer.ContainsKey(pair.Key))
                        {
                            continue;
                        }

                        if (ObjectTypeAliases.TryGetValue(pair.Key, out var target)
                            && peer.TryGetValue(target, out var peerValue))
                        {
                            map.AddAliasReverse(kind, pair.Value, peerValue);
                            coalesced++;
                        }
                    }
                }

                Log("map " + kind + " mapped=" + mapped + " changed=" + changed
                    + (coalesced > 0 ? " coalesced=" + coalesced : ""));
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
            catch (Exception e) { CoopLog.Swallow(e); }
        }

        private static void LogWarn(string s)
        {
            try
            {
                CoopPlugin.Log.LogWarning("CatalogIdMap: " + s);
            }
            catch (Exception e) { CoopLog.Swallow(e); }
        }

        // ------------------------------------------------------------------ state

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

        /// <summary>Set the first time a dropped member is ingested this session, so the notice is
        /// shown ONCE and never per object or per frame. Reset in <see cref="Clear"/> so a re-join
        /// can report it again.</summary>
        private static bool _droppedNoticeShown;

        /// <summary>Report a dropped member through the mod's own status channel, at most once per
        /// session, and log it. A dropped member has no local equivalent; silently becoming another
        /// object would be worse than not placing it.</summary>
        private static void NotifyDroppedOnce(EnumKind kind, string name)
        {
            if (_droppedNoticeShown)
            {
                return;
            }

            _droppedNoticeShown = true;
            LogWarn("coalesce drop: " + kind + " '" + (name ?? "?")
                + "' has no equivalent on this build; skipped.");
            try
            {
                HudApi.SetStatusLine("An item from the other player's build ("
                    + (name ?? "unknown") + ") isn't in your build, so it was skipped.", 8f);
            }
            catch (Exception e) { CoopLog.Swallow(e); }
        }

        /// <summary>
        /// The per-kind value bijection between the host's numeric enum ids and this process's,
        /// plus the coalesce additions. Exact matches populate both directions first-wins; aliases
        /// add one direction only so a shared name is never remapped onto a different id; dropped
        /// peer values carry their member name so the ingest path can report them.
        /// </summary>
        private sealed class EnumValueMap
        {
            private readonly Dictionary<EnumKind, Dictionary<int, int>> _hostToLocal = new();
            private readonly Dictionary<EnumKind, Dictionary<int, int>> _localToHost = new();
            private readonly Dictionary<long, string> _dropped = new();

            private static long DropKey(EnumKind kind, int hostValue)
                => ((long)(int)kind << 32) | (uint)hostValue;

            public bool HasDrops => _dropped.Count > 0;

            private void AddForward(EnumKind kind, int hostValue, int localValue)
            {
                if (!_hostToLocal.TryGetValue(kind, out var forward))
                {
                    _hostToLocal[kind] = forward = new Dictionary<int, int>();
                }

                if (!forward.ContainsKey(hostValue))
                {
                    forward[hostValue] = localValue;
                }
            }

            private void AddReverse(EnumKind kind, int localValue, int hostValue)
            {
                if (!_localToHost.TryGetValue(kind, out var reverse))
                {
                    _localToHost[kind] = reverse = new Dictionary<int, int>();
                }

                if (!reverse.ContainsKey(localValue))
                {
                    reverse[localValue] = hostValue;
                }
            }

            public void AddExact(EnumKind kind, int hostValue, int localValue)
            {
                AddForward(kind, hostValue, localValue);
                AddReverse(kind, localValue, hostValue);
            }

            public void AddAlias(EnumKind kind, int hostValue, int localValue)
                => AddForward(kind, hostValue, localValue);

            public void AddAliasReverse(EnumKind kind, int localValue, int hostValue)
                => AddReverse(kind, localValue, hostValue);

            public void AddDropped(EnumKind kind, int hostValue, int localValue, string name)
            {
                AddForward(kind, hostValue, localValue);
                _dropped[DropKey(kind, hostValue)] = name;
            }

            public bool TryHostToLocal(EnumKind kind, int hostValue, out int localValue)
            {
                if (_hostToLocal.TryGetValue(kind, out var forward)
                    && forward.TryGetValue(hostValue, out var mapped))
                {
                    localValue = mapped;
                    return true;
                }

                // A miss is the identity, not 0: callers that ignore the bool (the placement
                // move converter) must still receive a usable value.
                localValue = hostValue;
                return false;
            }

            public bool TryLocalToHost(EnumKind kind, int localValue, out int hostValue)
            {
                if (_localToHost.TryGetValue(kind, out var reverse)
                    && reverse.TryGetValue(localValue, out var mapped))
                {
                    hostValue = mapped;
                    return true;
                }

                hostValue = localValue;
                return false;
            }

            public bool TryDroppedName(EnumKind kind, int hostValue, out string name)
                => _dropped.TryGetValue(DropKey(kind, hostValue), out name);

            public int Kinds => _hostToLocal.Count;
        }
    }
}
