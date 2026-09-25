using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using UnityEngine;
using CardShopCoop.Net.Messages;

namespace CardShopCoop.Modules.Catalog
{
    /// <summary>Catalog-owned enum and custom-card identity, including the EPL registry lifecycle.</summary>
    internal static class CatalogParity
    {
        private static string _enum;

        /// <summary>Hash the exact canonical custom-card identity lines sent in a handshake.
        /// The plugin and enum hashes do not cover CreateCards/CardForge MonsterType values, so
        /// this identity is required to keep WriteCard/ReadCard synchronization safe.</summary>
        internal static string CardsHashForLines(List<string> lines)
        {
            if (!TryCanonicalizeCardLines(lines, out var canonical, out var failureReason))
            {
                throw new InvalidDataException("cannot hash invalid CardForge card list: "
                    + failureReason);
            }

            return canonical.Count == 0 ? "none" : Short(Sha1(string.Join(";", canonical)));
        }

        /// <summary>The exact sorted "Monster Type=Monster Type ID" identity strings exposed as a
        /// list so the handshake can SHOW the mismatch, not just reject it. The list and hash
        /// both come from CardEntries(), so they cannot disagree about the filesystem snapshot.</summary>
        public static List<string> CardsList()
            => CardEntries();

        /// <summary>The .ini-derived custom-card identity strings, sorted - the single source
        /// the handshake's hash and list read so they are always in step.</summary>
        private static List<string> CardEntries()
        {
            var dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "patchers", "CreateCardsPreloader", "MonsterConfigs");
            var entries = new List<string>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var ids = new HashSet<int>();
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.GetFiles(dir, "*.ini"))
                {
                    string name = null;
                    string idText = null;
                    var nameSeen = false;
                    var idSeen = false;
                    foreach (var line in File.ReadAllLines(f))
                    {
                        var eq = line.IndexOf('=');
                        if (eq <= 0)
                        {
                            continue;
                        }

                        var k = line.Substring(0, eq).Trim();
                        if (k.Equals("Monster Type", StringComparison.OrdinalIgnoreCase))
                        {
                            if (nameSeen)
                            {
                                throw new InvalidDataException("CardForge config '" + f
                                    + "' contains duplicate Monster Type entries");
                            }

                            nameSeen = true;
                            name = line.Substring(eq + 1).Trim();
                        }
                        else if (k.Equals("Monster Type ID", StringComparison.OrdinalIgnoreCase))
                        {
                            if (idSeen)
                            {
                                throw new InvalidDataException("CardForge config '" + f
                                    + "' contains duplicate Monster Type ID entries");
                            }

                            idSeen = true;
                            idText = line.Substring(eq + 1).Trim();
                        }
                    }

                    if (!nameSeen || string.IsNullOrEmpty(name))
                    {
                        throw new InvalidDataException("CardForge config '" + f
                            + "' has no nonempty Monster Type");
                    }
                    if (name.IndexOf('=') >= 0 || name.IndexOf('\r') >= 0
                        || name.IndexOf('\n') >= 0)
                    {
                        throw new InvalidDataException("CardForge config '" + f
                            + "' has a Monster Type that cannot be represented canonically");
                    }
                    if (!idSeen || !int.TryParse(idText, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var id))
                    {
                        throw new InvalidDataException("CardForge config '" + f
                            + "' has a non-integer Monster Type ID");
                    }

                    var entry = name + "=" + id.ToString(CultureInfo.InvariantCulture);
                    if (!names.Add(name))
                    {
                        throw new InvalidDataException("CardForge configs contain duplicate Monster Type name '"
                            + name + "'");
                    }
                    if (!ids.Add(id))
                    {
                        throw new InvalidDataException("CardForge configs contain duplicate Monster Type ID "
                            + id.ToString(CultureInfo.InvariantCulture));
                    }

                    entries.Add(entry);
                }
            }
            entries.Sort(StringComparer.Ordinal);
            return entries;
        }

        /// <summary>Parse the canonical CardForge wire shape. Empty lists are valid, but every
        /// advertised entry must be exactly one nonempty name and one canonical Int32 ID. This
        /// is shared by the local and peer paths so duplicate names or IDs can never be hashed
        /// into an ambiguous identity.</summary>
        internal static bool TryCanonicalizeCardLines(IList<string> source,
            out List<string> canonical, out string failureReason)
        {
            canonical = new List<string>();
            failureReason = null;
            if (source == null)
            {
                failureReason = "CardForge card list is missing";
                return false;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            var ids = new HashSet<int>();
            for (var i = 0; i < source.Count; i++)
            {
                var raw = source[i];
                if (raw == null)
                {
                    failureReason = "CardForge card line " + i + " is null";
                    return false;
                }

                var line = raw.Trim();
                var equals = line.IndexOf('=');
                if (line.Length == 0 || !string.Equals(raw, line, StringComparison.Ordinal)
                    || equals <= 0 || equals != line.LastIndexOf('='))
                {
                    failureReason = "CardForge card line " + i
                        + " is not canonical name=integer-id";
                    return false;
                }

                var name = line.Substring(0, equals);
                var idText = line.Substring(equals + 1);
                if (name.Length == 0 || name.Trim().Length != name.Length
                    || name.IndexOf('\r') >= 0 || name.IndexOf('\n') >= 0
                    || !int.TryParse(idText, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var id))
                {
                    failureReason = "CardForge card line " + i
                        + " is not canonical name=integer-id";
                    return false;
                }

                var normalized = name + "=" + id.ToString(CultureInfo.InvariantCulture);
                if (!string.Equals(line, normalized, StringComparison.Ordinal))
                {
                    failureReason = "CardForge card line " + i
                        + " is not canonical name=integer-id";
                    return false;
                }
                if (!names.Add(name))
                {
                    failureReason = "CardForge card list contains duplicate name '" + name + "'";
                    return false;
                }
                if (!ids.Add(id))
                {
                    failureReason = "CardForge card list contains duplicate ID "
                        + id.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                canonical.Add(normalized);
            }

            canonical.Sort(StringComparer.Ordinal);
            return true;
        }

        /// <summary>THE ONE WAY THIS MOD LOOKS UP ANOTHER MOD'S TYPE. Asks for the type by its
        /// assembly-qualified name first - a direct bind that loads nothing new, touches no
        /// other assembly and answers null quietly - and only falls back to
        /// AccessTools.TypeByName, which walks the types of EVERY loaded assembly, when that
        /// misses (a mod repackaged under a different assembly name, or a name we guessed
        /// wrong).
        ///
        /// The walk is the thing worth avoiding: OUR assembly is one of the ones it enumerates,
        /// and on the Game Pass build our DLL deliberately contains types that cannot load
        /// (everything Steam-typed - see Net/ISteamBridge). Every walk therefore makes HarmonyX
        /// log a ReflectionTypeLoadException naming Steamworks types, which reads to a player
        /// as the mod crashing while it is doing exactly what it was designed to do. This is the
        /// version the rest of the mod shares.
        ///
        /// <paramref name="assemblySimpleName"/> is the SIMPLE name of the assembly the type
        /// lives in (no version, no key) - "EnhancedPrefabLoader", "Grading Overhaul".</summary>
        public static Type ResolveType(string typeName, string assemblySimpleName)
        {
            try
            {
                var t = Type.GetType(typeName + ", " + assemblySimpleName, false);
                if (t != null)
                {
                    return t;
                }
            }
            catch (Exception e) { Swallow.Log(e); /* a lookup must never throw into a handshake, a probe or OnGUI */ }
            try
            {
                return HarmonyLib.AccessTools.TypeByName(typeName);
            }
            catch (Exception e) { Swallow.Log(e); return null; }
        }

        /// <summary>The enum types EPL mints custom ids into - exactly the six sections a live
        /// enum_values.json carries, all global-namespace enums in the decompiled Assembly-CSharp.
        /// Resolved BY NAME at runtime (the mod's usual AccessTools idiom) so a game update that
        /// renames or drops one costs us that one section instead of throwing a TypeLoadException
        /// straight through the handshake. The kind is carried so the typed identity and the
        /// value map agree on which section is which.</summary>
        private static readonly (string TypeName, EnumKind Kind)[] ModdedEnumTypes =
        {
            ("EObjectType", EnumKind.ObjectType),
            ("EDecoObject", EnumKind.DecoObject),
            ("EItemType", EnumKind.ItemType),
            ("ECardExpansionType", EnumKind.CardExpansion),
            ("ERarity", EnumKind.Rarity),
            ("ECollectionPackType", EnumKind.CollectionPack),
        };

        // EPL mints its enum members at PREPATCH, so by runtime the loaded enums already contain
        // both the vanilla members and EPL's. There is deliberately no numeric "modded floor": the
        // enum object is the membership truth, and vanilla members are identical on both peers for
        // the same game build, so they can never produce a key conflict.

        /// <summary>Hash of the exact canonical enum identity lines THIS PROCESS advertises. The
        /// lines come from the loaded enum types (vanilla members plus EPL's minted members).</summary>
        public static string EnumHash()
        {
            if (_enum != null)
            {
                return _enum;
            }

            try
            {
                _enum = EnumHashForIdentity(EnumIdentity());
                return _enum;
            }
            catch { _enum = "none"; }
            return _enum;
        }

        /// <summary>Hash the exact canonical enum identity carried in a handshake. Deterministic
        /// over kind order and member order, so two peers advertising the same identity hash to
        /// the same value.</summary>
        internal static string EnumHashForIdentity(List<EnumKindIdentityDto> identity)
        {
            var canonical = CanonicalHashLines(IdentityLines(identity));
            return canonical.Count == 0 ? "none" : Short(Sha1(string.Join("\n", canonical)));
        }

        /// <summary>The typed enum identity THIS process advertises: every member of the modded
        /// enum types (vanilla plus EPL/CardForge-minted members), one entry per kind, members
        /// ordinally sorted by name. The client pairs these values with its own by name; ids never
        /// cross the registry and are never compared for equality.</summary>
        public static List<EnumKindIdentityDto> EnumIdentity()
        {
            try
            {
                var identity = BuildEnumIdentity();
                var count = 0;
                foreach (var kind in identity)
                {
                    count += kind.Members == null ? 0 : kind.Members.Count;
                }
                if (count == 0)
                {
                    // Identity is runtime-only: none of the six enum types resolved, so this
                    // process cannot key-check custom content this session. There is deliberately
                    // no enum_values.json fallback.
                    LogEnumSourceOnce("runtime enum walk found no members (the enum types did not resolve)");
                }
                else
                {
                    LogEnumSourceOnce("runtime enums (" + count + " members)");
                }

                return identity;
            }
            catch (Exception e) { Swallow.Log(e); return new List<EnumKindIdentityDto>(); }
        }

        /// <summary>The canonical "Type:Name=Value" view of a typed identity, used only to hash it
        /// so two peers can verify they advertised the same content.</summary>
        private static List<string> IdentityLines(List<EnumKindIdentityDto> identity)
        {
            var lines = new List<string>();
            if (identity == null)
            {
                return lines;
            }

            foreach (var kind in identity)
            {
                if (kind == null || kind.Members == null)
                {
                    continue;
                }

                string typeName;
                try
                {
                    typeName = CatalogIdMap.WireType((EnumKind)kind.Kind).Name;
                }
                catch { continue; }

                foreach (var member in kind.Members)
                {
                    if (member == null || member.Name == null)
                    {
                        continue;
                    }
                    lines.Add(typeName + ":" + member.Name + "=" + member.Value);
                }
            }

            return lines;
        }

        private static bool _enumSourceLogged;

        /// <summary>Say ONCE per session where our registry lines came from. Which source
        /// answered decides whether the join-time ID-conflict gate is live or quietly
        /// short-circuited, so it belongs in the log next to the hashes - but it is read on
        /// every Hello, and one line per join attempt would be noise.</summary>
        private static void LogEnumSourceOnce(string source)
        {
            if (_enumSourceLogged)
            {
                return;
            }

            _enumSourceLogged = true;
            try
            {
                CoopPlugin.Log.LogInfo("enum identity source: " + source);
            }
            catch (Exception e) { Swallow.Log(e); }
        }

        /// <summary>The typed identity resolved from the LOADED types, per kind, members sorted by
        /// name - the single source both EnumHash and EnumIdentity read. EPL injects its minted
        /// members at prepatch, so Enum.GetNames/GetValues report them for real alongside the
        /// vanilla members, and we keep them all. Vanilla members are identical on both peers for
        /// the same game build; the modded members are exactly the ones that can carry different
        /// ids. A type that won't resolve is skipped, not fatal: a partial answer still beats no
        /// handshake.</summary>
        private static List<EnumKindIdentityDto> BuildEnumIdentity()
        {
            var result = new List<EnumKindIdentityDto>();
            foreach (var (typeName, kind) in ModdedEnumTypes)
            {
                try
                {
                    var t = AccessTools.TypeByName(typeName);
                    if (t == null || !t.IsEnum)
                    {
                        continue;
                    }

                    // GetNames and GetValues are documented to run in the same (binary-value)
                    // order, so index i is one member - walking them in parallel keeps aliases
                    // (two names, one id) as the two distinct entries the registry shows.
                    var names = Enum.GetNames(t);
                    var values = Enum.GetValues(t);
                    var n = Math.Min(names.Length, values.Length);
                    var members = new List<EnumMemberDto>(n);
                    for (var i = 0; i < n; i++)
                    {
                        long id;
                        try
                        {
                            id = Convert.ToInt64(values.GetValue(i));
                        }
                        catch { continue; }

                        members.Add(new EnumMemberDto { Name = names[i], Value = id });
                    }

                    // Ordinal sort by name is the canonical order both peers derive the same
                    // member ids from; never sorted by value, which is what can differ.
                    members.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
                    result.Add(new EnumKindIdentityDto { Kind = (int)kind, Members = members });
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("enum walk (" + typeName + "): " + e.Message); }
            }

            return result;
        }

        /// <summary>EPL's custom-item ID registry on disk. Our identity never reads it (enum
        /// identity walks the loaded enums); it matters only to the repair path, which can put a
        /// player's OWN registry back after an older build of this mod installed a host's copy.</summary>
        public static string EnumFilePath()
        {
            return Path.Combine(Application.persistentDataPath, "PrefabLoader", "enum_values.json");
        }

        /// <summary>Marker written beside enum_values.json by an older build when it installed a
        /// host's registry over the player's own. Its mere existence is the signal a restore is owed.</summary>
        private static string EnumMarkerPath()
        {
            return EnumFilePath() + ".hostlend";
        }

        /// <summary>Type names that exist only when EnhancedPrefabLoader is actually LOADED in
        /// this process. Deliberately the SAME strings the rest of the mod already probes for
        /// (CatalogInterop and WorldMarketInteraction both resolve EplRuntimeData; the latter also
        /// resolves ItemSaveData) so there is one set of names to keep in step with an EPL update.
        /// ANY hit counts as loaded; a dark probe fails toward "vanilla", which is the safe side
        /// (it only disables the EPL-specific bridges; enum identity comes from the game's own
        /// loaded enums, which exist regardless).</summary>
        private static readonly string[] EplSentinelTypeNames =
        {
            "EnhancedPrefabLoader.Core.EplRuntimeData",
            "EnhancedPrefabLoader.Core.Models.SaveData.ItemSaveData"
        };

        /// <summary>Simple name of the assembly the sentinels above live in - the second half of
        /// the assembly-qualified name EplLoaded asks Type.GetType for. Kept beside the sentinels
        /// so an EPL rename moves both together.</summary>
        private const string EplAssemblyName = "EnhancedPrefabLoader";

        private static bool _eplLoaded;
        private static bool _eplProbeComplete;
        private static bool _eplLoadedLogged;

        /// <summary>True when EnhancedPrefabLoader is loaded in THIS process, i.e. when a custom
        /// id registry is actually in play. The repair path must ask this FIRST: enum_values.json
        /// and the .hostlend marker beside it live under LocalLow and nothing removes them when EPL
        /// is uninstalled, so a plain vanilla game can still have leftovers on disk.
        ///
        /// The result is latched after the first lifecycle probe. BepInEx has populated its plugin
        /// registry before this plugin's normal runtime can reach the catalog APIs, so optional
        /// assemblies do not require a periodic UI-driven poll. The probe asks a targeted question
        /// (an assembly-qualified Type.GetType bind, then BepInEx's metadata-only plugin list)
        /// rather than AccessTools.TypeByName, which would enumerate types in our own DLL that
        /// intentionally cannot load on Game Pass (everything Steam-typed) and log a
        /// ReflectionTypeLoadException naming Steamworks types.</summary>
        public static bool EplLoaded()
        {
            if (_eplProbeComplete)
            {
                return _eplLoaded;
            }

            _eplProbeComplete = true;
            foreach (var typeName in EplSentinelTypeNames)
            {
                try
                {
                    if (Type.GetType(typeName + ", " + EplAssemblyName, false) == null)
                    {
                        continue;
                    }

                    _eplLoaded = true;
                    break;
                }
                catch (Exception e) { Swallow.Log(e); /* a probe must never throw into a handshake or into OnGUI */ }
            }
            if (!_eplLoaded)
            {
                try
                {
                    foreach (var kv in BepInEx.Bootstrap.Chainloader.PluginInfos)
                    {
                        var meta = kv.Value != null ? kv.Value.Metadata : null;
                        if (meta == null)
                        {
                            continue;
                        }

                        if (Mentions(kv.Key, "enhancedprefabloader") || Mentions(meta.Name, "enhancedprefabloader"))
                        {
                            _eplLoaded = true;
                            break;
                        }
                    }
                }
                catch (Exception e) { Swallow.Log(e); /* same rule: a probe must never throw */ }
            }
            if (_eplLoaded && !_eplLoadedLogged)
            {
                _eplLoadedLogged = true;
                try
                {
                    CoopPlugin.Log.LogInfo("EnhancedPrefabLoader detected - a custom id registry is in play");
                }
                catch (Exception e) { Swallow.Log(e); }
            }
            return _eplLoaded;
        }

        /// <summary>Case-insensitive substring test used by the plugin-list EPL backstop.</summary>
        private static bool Mentions(string haystack, string needle)
        {
            return haystack != null && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>True while a registry a previous build installed over the player's own is
        /// still on disk (the marker exists) AND this process actually loads a registry. Cheap
        /// File.Exists on purpose - there's no caching to go stale after a restore. CoopUI reads
        /// this to offer the one-click restore; nothing in the join path consults it.
        ///
        /// The !EplLoaded() short-circuit: the marker sits beside enum_values.json in LocalLow
        /// and outlives an EPL uninstall exactly like the registry does, so on a game that loads
        /// no registry it is pure litter - and a "true" here would pin a permanent "your card
        /// database is borrowed" banner for a copy that means nothing to a process loading no
        /// registry. Answer no.</summary>
        public static bool HostEnumInstalled()
        {
            try
            {
                if (!EplLoaded())
                {
                    return false;
                }

                return File.Exists(EnumMarkerPath());
            }
            catch (Exception e) { Swallow.Log(e); return false; }
        }

        /// <summary>Undo a registry a previous build of this mod installed: put the player's OWN
        /// enum_values.json back so their modded solo saves load again. Finds the newest .coopbak-*
        /// backup, keeps the current (host's) file as .hostcopy so nothing is destroyed, restores
        /// the backup over enum_values.json, and clears the marker. The game reads the registry
        /// once at startup, so the caller MUST tell the player to RESTART the game before loading
        /// solo saves. With no backup, nothing is changed and the marker is cleared so a stale
        /// prompt cannot latch. CoopUI calls this by name.</summary>
        public static bool RestoreEnumBackup(out string message)
        {
            var p = EnumFilePath();
            try
            {
                var dir = Path.GetDirectoryName(p);
                string newest = null;
                if (Directory.Exists(dir))
                {
                    var baks = Directory.GetFiles(dir, Path.GetFileName(p) + ".coopbak-*");
                    Array.Sort(baks, StringComparer.Ordinal); // timestamp suffix sorts oldest-first
                    if (baks.Length > 0)
                    {
                        newest = baks[baks.Length - 1];
                    }
                }

                if (newest == null)
                {
                    // Nothing was ever set aside (or the backups were pruned): clear the marker so
                    // the prompt cannot latch, and change nothing else. Nothing was written to
                    // enum_values.json, so no restart is owed here.
                    try
                    {
                        File.Delete(EnumMarkerPath());
                    }
                    catch (Exception e) { CoopPlugin.Log.LogWarning("enum marker clear: " + e.Message); }
                    bool cleared;
                    try
                    {
                        cleared = !File.Exists(EnumMarkerPath());
                    }
                    catch { cleared = false; }
                    message = cleared
                        ? "no backup of your card database was found, so your card database was left exactly as it is - the 'borrowed from a host' flag has been cleared. If your solo saves still won't load, put your own enum_values.json back by hand (see mod page)."
                        : "no backup of your card database was found, and the co-op marker could not be deleted - delete enum_values.json.hostlend by hand (it sits next to enum_values.json; see mod page)";
                    CoopPlugin.Log.LogWarning("enum restore: " + message);
                    return cleared;
                }

                // Preserve the currently installed (host's) file first so a restore is never a
                // one-way loss.
                if (File.Exists(p))
                {
                    File.Copy(p, p + ".hostcopy", overwrite: true);
                }

                File.Copy(newest, p, overwrite: true);
                try
                {
                    File.Delete(EnumMarkerPath());
                }
                catch (Exception e) { Swallow.Log(e); }
                message = "your card database was restored from backup - RESTART the game before loading your solo saves";
                CoopPlugin.Log.LogInfo("enum restore: " + message + " (from " + Path.GetFileName(newest) + ")");
                return true;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("enum restore failed: " + e.Message);
                message = "could not restore your card database automatically - put your own enum_values.json backup back by hand (see mod page)";
                return false;
            }
        }

        private static string Sha1(string s)
        {
            return Sha1Bytes(Encoding.UTF8.GetBytes(s));
        }

        private static string Sha1Bytes(byte[] data)
        {
            using (var sha = SHA1.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
            }
        }

        private static List<string> CanonicalHashLines(List<string> lines)
        {
            var result = new List<string>();
            if (lines != null)
            {
                foreach (var line in lines)
                {
                    var value = line == null ? "" : line.Trim();
                    if (value.Length > 0)
                    {
                        result.Add(value);
                    }
                }
            }

            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private static string Short(string hex)
        {
            return hex.Length > 16 ? hex.Substring(0, 16) : hex;
        }
    }
}
