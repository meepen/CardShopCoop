using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace CardShopCoop.Modules.Catalog
{
    /// <summary>
    /// Answers which members of the six EPL-minted enums are backed by content that is actually
    /// installed/loaded right now, so the catalog identity can drop EPL's leftovers.
    ///
    /// EPL's prepatcher mints every enum name it has EVER saved into Assembly-CSharp (its saved
    /// registry is never pruned when a content pack disappears), so the loaded enum is membership
    /// truth but not activity truth. Every source here is a LIVE C# object in this process:
    /// nothing is read from disk, and the stale enum_values.json file is never consulted.
    ///
    ///  * The prepatcher itself (<c>EnhancedPrefabLoaderPrepatch</c> is loaded into this process
    ///    by BepInEx 5's preloader and stays resident). Its live <c>PatchDataManager</c> holds
    ///    <c>SavedData</c> - every member EPL has ever minted, which is how an EPL mint is told
    ///    apart from a vanilla member - and <c>UserData</c> - the names the bundle descriptors
    ///    CURRENTLY installed reference. It is the only oracle that can answer BEFORE the game's
    ///    loading screen processes bundles (a client builds its Hello while still on the title
    ///    screen), so it is the primary source.
    ///  * EPL's runtime bundle registry: through the public EPL API (<c>Epl.Api.BundleRegistry</c>)
    ///    when the API assembly is present, otherwise through the same live EplRuntimeData
    ///    structures. It lists what EPL actually loaded this session for EItemType, EObjectType,
    ///    EDecoObject and ECardExpansionType; the API exposes no enum lists for
    ///    ECollectionPackType or ERarity, so those come from the live LookupLibrary /
    ///    CardExpansionLibrary dictionaries that back the API's per-value methods. This oracle
    ///    only exists after bundle loading completes, so it is a union contribution.
    ///
    /// The filter is fail-open: no EPL, no live prepatch registry, or any reflection failure
    /// means "unavailable" and the identity walk behaves exactly as it did before this class
    /// existed.
    /// </summary>
    internal static class EplActiveEnums
    {
        private const BindingFlags AnyMember = BindingFlags.Static | BindingFlags.Instance
            | BindingFlags.Public | BindingFlags.NonPublic;

        private const string PrepatchAssemblyName = "EnhancedPrefabLoaderPrepatch";
        private const string PrepatchPatcherTypeName = "EnhancedPrefabLoader.Prepatch.Patcher";
        private const string PrepatchManagerFieldName = "patchDataManager";

        // The API is preferred for the registry it documents; EplRuntimeData is the same live
        // object it wraps, used when the optional API assembly is not present.
        private const string EplApiAssemblyName = "EnhancedPrefabLoader.API";
        private const string EplApiTypeName = "EnhancedPrefabLoader.API.Epl";
        private const string EplCoreAssemblyName = "EnhancedPrefabLoader";
        private const string EplRuntimeDataTypeName = "EnhancedPrefabLoader.Core.EplRuntimeData";

        // EPL's own name normalization, taken from the prepatcher's StringExtensions.Sanitize:
        // whitespace is removed, every non-word character becomes '_', and a leading digit gets a
        // '_' prefix. EnumPatcher creates every minted field under the sanitized spelling, so
        // that is the name the runtime enum actually carries.
        private static readonly Regex EplWhitespace = new Regex("\\s", RegexOptions.Compiled);
        private static readonly Regex EplNonWord = new Regex("[^\\w]", RegexOptions.Compiled);

        private static readonly object Gate = new object();

        private static bool _prepatchProbed;
        private static PrepatchState _prepatch;
        private static bool _runtimeProbed;
        private static Dictionary<string, HashSet<long>> _runtimeValues;
        private static int _sourceMask;
        private static int _version;
        private static bool _unavailableLogged;

        /// <summary>Revision of the filter's source set. A session starts with no filter at all;
        /// the prepatch source appears on the first identity build, and the runtime source can
        /// appear later (once the loading screen processed bundles). Consumers that cache a
        /// derived value (the catalog's enum hash) must rebuild it when this changes.</summary>
        internal static int Version
        {
            get
            {
                lock (Gate)
                {
                    return _version;
                }
            }
        }

        /// <summary>The activity filter for this moment, or null when EPL is absent or no live
        /// source can answer (callers must then keep the pre-filter behavior). Cheap enough to
        /// call per identity build: the probes are latched, and identity builds are rare.</summary>
        internal static ActiveEnumFilter Current()
        {
            lock (Gate)
            {
                if (!CatalogParity.EplLoaded())
                {
                    // No EPL: nothing minted these members, so every member is real content.
                    return null;
                }

                if (!_prepatchProbed)
                {
                    _prepatchProbed = true;
                    _prepatch = ProbePrepatch();
                }
                if (_prepatch == null)
                {
                    // Without the prepatcher's live post-mint registry there is no way to tell a
                    // vanilla member from an EPL leftover, and dropping unbacked members would
                    // delete vanilla content. Fail open.
                    LogUnavailable("the prepatcher's live enum registry is unavailable");
                    return null;
                }

                if (!_runtimeProbed)
                {
                    _runtimeValues = ProbeRuntimeValues();
                    // Latch only once the runtime source actually answered. Until the loading
                    // screen has processed bundles the probe legitimately returns null, and a
                    // later identity build must pick the contribution up (no timer, no polling -
                    // it re-probes only when something asks for an identity). A permanently
                    // missing source costs one cheap loaded-assembly scan per build.
                    if (_runtimeValues != null)
                    {
                        _runtimeProbed = true;
                    }
                }

                // The prepatcher state is fixed for the process; the runtime contribution can
                // only appear once (bundle loading completes) and is then stable. The source
                // mask drives the version consumers use to invalidate caches.
                var mask = _runtimeValues != null ? 3 : 1;
                if (mask != _sourceMask)
                {
                    _sourceMask = mask;
                    _version++;
                    LogSources(mask == 3);
                }

                return new ActiveEnumFilter(_prepatch.MintedValues, _prepatch.ActiveNames, _runtimeValues);
            }
        }

        /// <summary>The live post-mint enum state of the prepatcher.</summary>
        private sealed class PrepatchState
        {
            /// <summary>Every value EPL has ever minted per enum section (SavedData after the
            /// prepatch, with vanilla name/value collisions already removed).</summary>
            internal Dictionary<string, HashSet<long>> MintedValues;

            /// <summary>The sanitized member names referenced by the bundle descriptors that are
            /// currently installed, per enum section.</summary>
            internal Dictionary<string, HashSet<string>> ActiveNames;
        }

        /// <summary>Reflection into the loaded prepatcher assembly. BepInEx 5 loads patcher
        /// DLLs with Assembly.LoadFile in the game's own AppDomain and never unloads them, so the
        /// static manager field - and with it the post-mint SavedData - is reachable here.</summary>
        private static PrepatchState ProbePrepatch()
        {
            try
            {
                var assembly = FindLoadedAssembly(PrepatchAssemblyName);
                var patcherType = assembly?.GetType(PrepatchPatcherTypeName, false);
                var manager = patcherType?
                    .GetField(PrepatchManagerFieldName, BindingFlags.NonPublic | BindingFlags.Static)?
                    .GetValue(null);
                if (manager == null)
                {
                    return null;
                }

                var managerType = manager.GetType();
                if (!(managerType.GetProperty("SavedData", AnyMember)?.GetValue(manager) is IDictionary savedData)
                    || !(managerType.GetProperty("UserData", AnyMember)?.GetValue(manager) is IDictionary userData))
                {
                    return null;
                }

                var state = new PrepatchState
                {
                    MintedValues = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal),
                    ActiveNames = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal),
                };

                foreach (DictionaryEntry section in savedData)
                {
                    if (!(section.Key is string enumName) || !(section.Value is IDictionary entries))
                    {
                        continue;
                    }

                    var values = new HashSet<long>();
                    foreach (DictionaryEntry entry in entries)
                    {
                        if (entry.Value is int value)
                        {
                            values.Add(value);
                        }
                    }
                    state.MintedValues[enumName] = values;
                }

                foreach (DictionaryEntry section in userData)
                {
                    if (!(section.Key is string enumName) || !(section.Value is IEnumerable rawNames))
                    {
                        continue;
                    }

                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var raw in rawNames)
                    {
                        if (raw is string text)
                        {
                            names.Add(SanitizeEplName(text));
                        }
                    }
                    state.ActiveNames[enumName] = names;
                }

                if (state.ActiveNames.Count == 0)
                {
                    // The six sections are always created together when the prepatcher loads its
                    // descriptor scan; an empty map means the data moved or we read it before
                    // population. Treat as unavailable rather than dropping every minted member.
                    return null;
                }

                return state;
            }
            catch (Exception e)
            {
                Swallow.Log(e);
                return null;
            }
        }

        /// <summary>The live runtime registry values per enum section, or null while bundle
        /// loading has not completed (the sets are legitimately empty until then) or when no
        /// runtime source can be resolved.</summary>
        private static Dictionary<string, HashSet<long>> ProbeRuntimeValues()
        {
            try
            {
                var coreAssembly = FindLoadedAssembly(EplCoreAssemblyName);
                var runtimeType = coreAssembly?.GetType(EplRuntimeDataTypeName, false);
                if (runtimeType == null)
                {
                    return null;
                }
                if (!(runtimeType.GetProperty("IsBundleLoadingComplete", AnyMember)?.GetValue(null) is bool complete)
                    || !complete)
                {
                    return null;
                }

                var assets = runtimeType.GetProperty("Assets", AnyMember)?.GetValue(null);
                if (assets == null)
                {
                    return null;
                }

                var result = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);

                // The four bundle-registered enum types. Prefer the documented API surface; its
                // BundleRegistry is the same live object EplRuntimeData exposes, so the fallback
                // reads identical data.
                var registry = TryGetApiBundleRegistry()
                    ?? assets.GetType().GetProperty("BundleRegistry", AnyMember)?.GetValue(assets);
                if (registry != null
                    && registry.GetType().GetProperty("BundleInfo", AnyMember)?.GetValue(registry) is IDictionary bundles)
                {
                    foreach (DictionaryEntry bundle in bundles)
                    {
                        var info = bundle.Value;
                        if (info == null)
                        {
                            continue;
                        }
                        AddList(result, "EItemType", info, "ItemTypes");
                        AddList(result, "EObjectType", info, "FurnitureTypes");
                        AddList(result, "EDecoObject", info, "DecoObjects");
                        AddList(result, "ECardExpansionType", info, "ExpansionTypes");
                    }
                }

                // ECollectionPackType has no enum-list API; this live dictionary is what
                // IItemLibrary.GetExpansionForPackType reads. A pack type is active exactly when
                // a loaded bundle registered it.
                var lookup = assets.GetType().GetProperty("LookupLibrary", AnyMember)?.GetValue(assets);
                if (lookup?.GetType().GetProperty("ExpansionLookup", AnyMember)?.GetValue(lookup) is IDictionary packTypes)
                {
                    AddValues(result, "ECollectionPackType", packTypes.Keys);
                }

                // ERarity has no API surface at all; the active rarities are the ones the
                // registered expansions declare (their own lists drive pack weights and the
                // per-expansion rarity menus).
                var expansions = assets.GetType().GetProperty("CardExpansionLibrary", AnyMember)?.GetValue(assets);
                if (expansions?.GetType().GetProperty("CardExpansionData", AnyMember)?.GetValue(expansions) is IDictionary expansionData)
                {
                    foreach (DictionaryEntry expansion in expansionData)
                    {
                        var data = expansion.Value;
                        if (data?.GetType().GetProperty("Rarities", AnyMember)?.GetValue(data) is IEnumerable rarities)
                        {
                            AddValues(result, "ERarity", rarities);
                        }
                    }
                }

                return result;
            }
            catch (Exception e)
            {
                Swallow.Log(e);
                return null;
            }
        }

        /// <summary>Epl.Api.BundleRegistry when the optional API assembly is present, loaded and
        /// registered; null otherwise (the caller then uses the same live registry through
        /// EplRuntimeData).</summary>
        private static object TryGetApiBundleRegistry()
        {
            try
            {
                var assembly = FindLoadedAssembly(EplApiAssemblyName);
                var eplType = assembly?.GetType(EplApiTypeName, false);
                if (eplType == null)
                {
                    return null;
                }
                if (!(eplType.GetProperty("IsAvailable", AnyMember)?.GetValue(null) is bool available)
                    || !available)
                {
                    return null;
                }

                var api = eplType.GetProperty("Api", AnyMember)?.GetValue(null);
                return api?.GetType().GetProperty("BundleRegistry", AnyMember)?.GetValue(api);
            }
            catch (Exception e)
            {
                Swallow.Log(e);
                return null;
            }
        }

        /// <summary>Already-loaded assemblies only: the probes must never trigger a global type
        /// walk (see CatalogParity.ResolveType) for an optional dependency, and everything they
        /// need - the prepatcher and EPL Core/the API plugin - is loaded during startup.</summary>
        private static Assembly FindLoadedAssembly(string simpleName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                    {
                        return assembly;
                    }
                }
                catch (Exception e)
                {
                    Swallow.Log(e);
                }
            }
            return null;
        }

        private static void AddList(Dictionary<string, HashSet<long>> result, string enumTypeName,
            object owner, string propertyName)
        {
            if (owner.GetType().GetProperty(propertyName, AnyMember)?.GetValue(owner) is IEnumerable values)
            {
                AddValues(result, enumTypeName, values);
            }
        }

        private static void AddValues(Dictionary<string, HashSet<long>> result, string enumTypeName,
            IEnumerable values)
        {
            if (!result.TryGetValue(enumTypeName, out var set))
            {
                set = new HashSet<long>();
                result[enumTypeName] = set;
            }
            foreach (var value in values)
            {
                try
                {
                    set.Add(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                }
                catch (Exception e)
                {
                    Swallow.Log(e);
                }
            }
        }

        /// <summary>EPL's prepatch StringExtensions.Sanitize, replicated exactly - the enum
        /// field names were created with it, so it is the only correct way to compare a
        /// descriptor name with a member name.</summary>
        private static string SanitizeEplName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "_Invalid";
            }
            var input = EplNonWord.Replace(EplWhitespace.Replace(value, ""), "_");
            return input.Length > 0 && !char.IsDigit(input[0]) ? input : "_" + input;
        }

        private static void LogSources(bool runtimeIncluded)
        {
            try
            {
                CoopPlugin.Log.LogInfo(runtimeIncluded
                    ? "EPL active enum filter: prepatch descriptor registry + runtime bundle registry"
                    : "EPL active enum filter: prepatch descriptor registry (runtime registry not ready)");
            }
            catch (Exception e)
            {
                Swallow.Log(e);
            }
        }

        private static void LogUnavailable(string reason)
        {
            if (_unavailableLogged)
            {
                return;
            }
            _unavailableLogged = true;
            try
            {
                CoopPlugin.Log.LogWarning("EPL detected, but the active-enum filter is unavailable ("
                    + reason + "); the enum identity keeps every minted member, as before.");
            }
            catch (Exception e)
            {
                Swallow.Log(e);
            }
        }

        /// <summary>Membership test for the catalog identity walk. A member stays when it is not
        /// an EPL mint at all (vanilla members and other mods' members can never be dropped), or
        /// when either live source still backs it.</summary>
        internal sealed class ActiveEnumFilter
        {
            private readonly Dictionary<string, HashSet<long>> _mintedValues;
            private readonly Dictionary<string, HashSet<string>> _activeNames;
            private readonly Dictionary<string, HashSet<long>> _runtimeValues;

            internal ActiveEnumFilter(Dictionary<string, HashSet<long>> mintedValues,
                Dictionary<string, HashSet<string>> activeNames,
                Dictionary<string, HashSet<long>> runtimeValues)
            {
                _mintedValues = mintedValues;
                _activeNames = activeNames;
                _runtimeValues = runtimeValues;
            }

            internal bool IsActive(string enumTypeName, string memberName, long memberValue)
            {
                if (!_mintedValues.TryGetValue(enumTypeName, out var minted)
                    || !minted.Contains(memberValue))
                {
                    return true;
                }
                if (_activeNames.TryGetValue(enumTypeName, out var names)
                    && names.Contains(memberName))
                {
                    return true;
                }
                return _runtimeValues != null
                    && _runtimeValues.TryGetValue(enumTypeName, out var values)
                    && values.Contains(memberValue);
            }
        }
    }
}
