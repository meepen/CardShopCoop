using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using CardShopCoop.Runtime;

namespace CardShopCoop.Modules.Catalog
{
    /// <summary>
    /// The only boundary through which the catalog is read or written.  In particular, never
    /// walk m_RestockDataList directly: EnhancedPrefabLoader intercepts the game's accessor and
    /// appends virtual rows which are not present in that raw List.
    /// </summary>
    internal static class CatalogInterop
    {
        internal const int MaxEntries = 32768;
        private const BindingFlags AnyMember = BindingFlags.Static | BindingFlags.Instance
            | BindingFlags.Public | BindingFlags.NonPublic;
        private static bool _eplProbed;
        private static bool _eplLogged;
        private static bool _eplAvailable;
        private static PropertyInfo _eplAssets;
        private static PropertyInfo _eplItemLibrary;
        private static PropertyInfo _eplRestockEntries;
        private static PropertyInfo _eplServices;
        private static PropertyInfo _eplSaveDataManager;
        private static MethodInfo _eplTryGetSaveData;
        private static PropertyInfo _eplSmallUnlocked;
        private static PropertyInfo _eplBigUnlocked;
        private static FieldInfo _licenseList;
        private static readonly HashSet<string> ReportedAmbiguities = new(StringComparer.Ordinal);
        private static readonly Dictionary<int, long> ReportedUnwireableItemTypes = new();
        private const long UnwireableItemLogIntervalTicks = TimeSpan.TicksPerMinute;

        /// <summary>
        /// A complete catalog license snapshot.  The raw list is only the legacy game's part of
        /// the catalog; EnhancedPrefabLoader virtual rows are kept in Bits and are restored
        /// through the optional save-data surface below.  Keeping this type here prevents the
        /// purchasing module from ever referencing an EPL-only type.
        /// </summary>
        internal sealed class LicenseState
        {
            internal readonly List<bool> RawBits = new();
            internal readonly List<LicenseBit> Bits = new();
        }

        internal sealed class LicenseBit
        {
            internal int Index;
            internal bool Unlocked;
        }

        internal static int Count
        {
            get
            {
                var inventory = SceneInventory();
                var raw = inventory?.m_StockItemData_SO?.m_RestockDataList?.Count ?? 0;
                return raw + EplExtraCount();
            }
        }

        internal static int RawCount
        {
            get
            {
                var inventory = SceneInventory();
                return inventory?.m_StockItemData_SO?.m_RestockDataList?.Count ?? 0;
            }
        }

        internal static bool IsSceneReady => SceneInventory() != null && Count > 0;

        /// <summary>
        /// Returns true only for enum values that the common wire converter can name.  The
        /// game's price list is deliberately wider than EItemType on both supported builds, so
        /// its tail contains raw slots which are not products and must never be put in a message.
        /// Keep this check here, beside the catalog/wire boundary, rather than teaching each
        /// module a different definition of a wireable item.
        /// </summary>
        internal static bool IsWireableItemType(EItemType itemType, string context = null)
        {
            var value = (int)itemType;
            if (itemType == EItemType.None)
            {
                return false;
            }
            if (itemType == EItemType.Max)
            {
                CoopPlugin.Log.LogWarning("Catalog rejected EItemType.Max sentinel"
                    + (string.IsNullOrEmpty(context) ? "" : " while building " + context));
                return false;
            }
            if (value >= 0 && Enum.GetName(typeof(EItemType), itemType) != null)
            {
                return true;
            }

            var now = DateTime.UtcNow.Ticks;
            if (!ReportedUnwireableItemTypes.TryGetValue(value, out var last)
                || now - last >= UnwireableItemLogIntervalTicks)
            {
                ReportedUnwireableItemTypes[value] = now;
                CoopPlugin.Log.LogWarning("Catalog rejected non-wireable EItemType " + value
                    + (string.IsNullOrEmpty(context) ? "" : " while building " + context)
                    + "; the raw price-list slot was skipped.");
            }
            return false;
        }

        internal static RestockData At(int index)
        {
            return TryAt(index, out var data) ? data : null;
        }

        internal static bool TryAt(int index, out RestockData data)
        {
            data = null;
            if (index < 0)
                return false;

            try
            {
                // InventoryBase.GetRestockData is patched by EPL and therefore resolves both
                // raw and virtual indexes. This direct game reference exists in both builds.
                data = InventoryBase.GetRestockData(index);
                return data != null;
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning($"Catalog row {index} could not be resolved: {error.Message}");
                return false;
            }
        }

        /// <summary>Resolve by the durable name, box size, and translated enum identity. A
        /// name/size match is not enough: EPL can expose two rows with the same display name and
        /// size. Enum-only fallback remains valid only when it identifies exactly one row.</summary>
        internal static bool TryResolve(EItemType itemType, bool isBigBox, string name,
            out int index, out RestockData data)
            => TryResolve(itemType, isBigBox, name, out index, out data, out _);

        internal static bool TryResolve(EItemType itemType, bool isBigBox, string name,
            out int index, out RestockData data, out bool transient)
        {
            index = -1;
            data = null;
            transient = false;
            if (!IsWireableItemType(itemType, "catalog product resolution"))
                return false;
            var count = Count;

            var nameSizeCount = 0;
            var exactCount = 0;
            var exactIndex = -1;
            RestockData exactData = null;
            if (!string.IsNullOrEmpty(name))
            {
                for (var i = 0; i < count; i++)
                {
                    if (!TryAt(i, out var candidate))
                    {
                        transient = true;
                        continue;
                    }
                    if (candidate == null || candidate.isBigBox != isBigBox
                        || !string.Equals(candidate.name, name, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    nameSizeCount++;
                    if (candidate.itemType == itemType)
                    {
                        exactCount++;
                        exactIndex = i;
                        exactData = candidate;
                    }
                }

                if (transient)
                {
                    return false;
                }

                if (exactCount == 1)
                {
                    index = exactIndex;
                    data = exactData;
                    return true;
                }
                if (exactCount > 1)
                {
                    ReportAmbiguous(name, isBigBox, itemType, exactCount);
                    return false;
                }
                if (nameSizeCount > 0)
                {
                    // A durable name matched, but not the translated type. Never silently
                    // replace that product with an enum-only fallback.
                    ReportAmbiguous(name, isBigBox, itemType, nameSizeCount);
                    return false;
                }
            }

            var typeCount = 0;
            var typeIndex = -1;
            RestockData typeData = null;
            for (var i = 0; i < count; i++)
            {
                if (!TryAt(i, out var candidate))
                {
                    transient = true;
                    continue;
                }
                if (candidate != null && candidate.itemType == itemType
                    && candidate.isBigBox == isBigBox)
                {
                    typeCount++;
                    typeIndex = i;
                    typeData = candidate;
                }
            }

            if (transient)
            {
                return false;
            }

            if (typeCount == 1)
            {
                index = typeIndex;
                data = typeData;
                return true;
            }
            if (typeCount > 1)
            {
                ReportAmbiguous(name, isBigBox, itemType, typeCount);
            }

            return false;
        }

        internal static bool TryGetLicense(int index, out bool unlocked, out bool transient)
        {
            unlocked = false;
            transient = false;
            if (index < 0)
            {
                return false;
            }

            try
            {
                // This game method is itself intercepted by EPL for virtual indexes.
                unlocked = CPlayerData.GetIsItemLicenseUnlocked(index);
                return true;
            }
            catch (Exception error)
            {
                transient = true;
                CoopPlugin.Log.LogWarning($"Catalog license read failed for row {index}: {error.Message}");
                return false;
            }
        }

        internal static bool SetLicense(int index, bool unlocked)
        {
            if (index < 0 || index >= Count)
            {
                return false;
            }

            try
            {
                if (unlocked)
                {
                    // SetUnlockItemLicense is the game's virtual-list-aware setter.
                    CPlayerData.SetUnlockItemLicense(index);
                    return TryGetLicense(index, out var applied, out _) && applied;
                }

                if (index < RawCount)
                {
                    var licenses = LicenseList();
                    if (licenses == null || index >= licenses.Count)
                    {
                        return false;
                    }
                    licenses[index] = false;
                    return true;
                }

                // The game has no public relock method.  EPL stores virtual unlocks in its
                // private ItemSaveData records, so change that record by reflection.  This is
                // deliberately optional: a legacy build simply has no virtual rows.
                return SetEplVirtualLicense(index, false);
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning($"Catalog license write failed for row {index}: {error.Message}");
                return false;
            }
        }

        internal static bool TryCaptureLicenseState(out LicenseState state)
        {
            state = new LicenseState();
            var raw = LicenseList();
            if (raw == null)
            {
                CoopPlugin.Log.LogWarning("Catalog license snapshot failed: raw license list is missing");
                return false;
            }

            state.RawBits.AddRange(raw);
            var count = Count;
            if (count < 0 || count > MaxEntries)
            {
                CoopPlugin.Log.LogWarning("Catalog license snapshot failed: invalid row count " + count);
                return false;
            }

            for (var i = 0; i < count; i++)
            {
                if (!TryGetLicense(i, out var unlocked, out var transient))
                {
                    CoopPlugin.Log.LogWarning("Catalog license snapshot failed for row " + i
                        + (transient ? ": state was transiently unreadable" : ": state was unavailable"));
                    return false;
                }

                state.Bits.Add(new LicenseBit
                {
                    Index = i,
                    Unlocked = unlocked,
                });
            }

            return true;
        }

        /// <summary>Restores every raw and virtual catalog bit.  The caller publishes any
        /// changed rows after this method returns; this method deliberately has no network or
        /// gameplay side effects.</summary>
        internal static bool TryRestoreLicenseState(LicenseState state, out List<LicenseBit> changed)
        {
            changed = new List<LicenseBit>();
            if (state == null || state.RawBits == null || state.Bits == null)
            {
                return false;
            }

            var raw = LicenseList();
            var count = Count;
            if (raw == null || count != state.Bits.Count)
            {
                CoopPlugin.Log.LogError("Catalog license restore refused: catalog shape changed "
                    + "during the purchase transaction");
                return false;
            }

            var current = new List<bool>(count);
            for (var i = 0; i < count; i++)
            {
                if (!TryGetLicense(i, out var unlocked, out _))
                {
                    CoopPlugin.Log.LogError("Catalog license restore refused: row " + i
                        + " is unreadable");
                    return false;
                }
                current.Add(unlocked);
            }

            for (var i = 0; i < state.Bits.Count; i++)
            {
                var bit = state.Bits[i];
                if (current[i] == bit.Unlocked)
                {
                    continue;
                }

                if (!SetLicense(bit.Index, bit.Unlocked))
                {
                    CoopPlugin.Log.LogError("Catalog license restore failed for row " + bit.Index);
                    return false;
                }
                changed.Add(bit);
            }

            // SetLicense handles the game's row accessors.  Restore the complete legacy list as
            // well, including its length, because CGameData serializes this exact list.
            raw.Clear();
            raw.AddRange(state.RawBits);
            for (var i = 0; i < state.Bits.Count; i++)
            {
                if (!TryGetLicense(i, out var unlocked, out _)
                    || unlocked != state.Bits[i].Unlocked)
                {
                    CoopPlugin.Log.LogError("Catalog license restore verification failed for row "
                        + i);
                    return false;
                }
            }

            return true;
        }

        internal static string IdentityKey(RestockData data)
        {
            if (data == null)
            {
                return null;
            }

            return IdentityKey(data.name, data.isBigBox, data.itemType);
        }

        internal static string IdentityKey(CatalogLicenseEntry entry)
        {
            if (entry == null)
            {
                return null;
            }

            return IdentityKey(entry.Name, entry.IsBigBox, entry.ItemType);
        }

        internal static void ReportAmbiguousIdentity(RestockData data)
        {
            if (data != null)
            {
                ReportAmbiguous(data.name, data.isBigBox, data.itemType, 2);
            }
        }

        internal static void ReportAmbiguousIdentity(CatalogLicenseEntry entry)
        {
            if (entry != null)
            {
                ReportAmbiguous(entry.Name, entry.IsBigBox, entry.ItemType, 2);
            }
        }

        private static string IdentityKey(string name, bool isBigBox, EItemType itemType)
        {
            // A control character cannot occur in a normal asset name and avoids ambiguity
            // between the three durable identity portions.
            return (name ?? "") + "\u001f" + (isBigBox ? "1" : "0") + "\u001f"
                + ((int)itemType).ToString(CultureInfo.InvariantCulture);
        }

        private static void ReportAmbiguous(string name, bool isBigBox, EItemType itemType,
            int count)
        {
            var key = IdentityKey(name, isBigBox, itemType);
            if (!ReportedAmbiguities.Add(key))
            {
                return;
            }

            CoopPlugin.Log.LogWarning("Catalog identity is ambiguous; refusing to choose among "
                + count + " rows for name='" + (name ?? "") + "' big=" + isBigBox
                + " itemType=" + itemType);
        }

        private static InventoryBase SceneInventory()
        {
            return SceneRef<InventoryBase>.Get();
        }

        internal static bool TryBuildLicenseEntries(out List<CatalogLicenseEntry> entries,
            out bool scannerUnlocked)
        {
            entries = new List<CatalogLicenseEntry>();
            scannerUnlocked = false;
            var count = Count;
            if (count <= 0 || count > MaxEntries)
            {
                return false;
            }

            scannerUnlocked = CPlayerData.m_IsScannerRestockUnlocked;
            var identityCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                if (!TryAt(i, out var data))
                {
                    entries = new List<CatalogLicenseEntry>();
                    return false;
                }
                if (data == null || string.IsNullOrEmpty(data.name))
                {
                    continue;
                }
                if (!IsWireableItemType(data.itemType, "catalog license state"))
                {
                    continue;
                }

                var identity = IdentityKey(data);
                identityCounts.TryGetValue(identity, out var seen);
                identityCounts[identity] = seen + 1;
            }

            for (var i = 0; i < count; i++)
            {
                if (!TryAt(i, out var data))
                {
                    entries = new List<CatalogLicenseEntry>();
                    return false;
                }
                if (data == null || string.IsNullOrEmpty(data.name)
                    || !identityCounts.TryGetValue(IdentityKey(data), out var identityCount))
                {
                    continue;
                }
                if (identityCount != 1)
                {
                    ReportAmbiguousIdentity(data);
                    continue;
                }
                if (!TryGetLicense(i, out var unlocked, out var licenseTransient))
                {
                    if (licenseTransient)
                    {
                        entries = new List<CatalogLicenseEntry>();
                        return false;
                    }
                    continue;
                }
                if (!unlocked)
                {
                    continue;
                }

                entries.Add(new CatalogLicenseEntry
                {
                    ItemType = data.itemType,
                    IsBigBox = data.isBigBox,
                    Name = data.name,
                });
            }

            return true;
        }

        private static List<bool> LicenseList()
        {
            _licenseList ??= typeof(CPlayerData).GetField("m_IsItemLicenseUnlocked", AnyMember);
            return _licenseList?.GetValue(null) as List<bool>;
        }

        private static int EplExtraCount()
        {
            ProbeEplIfNeeded();
            try
            {
                var currentAssets = _eplAssets?.GetValue(null);
                var currentLibrary = currentAssets == null
                    ? null : _eplItemLibrary?.GetValue(currentAssets);
                return (_eplRestockEntries?.GetValue(currentLibrary) as ICollection)?.Count ?? 0;
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("Catalog EPL probe failed: " + error.Message);
                return 0;
            }
        }

        internal static void ProbeOptionalSurfaces()
            => ProbeEplIfNeeded();

        private static void ProbeEplIfNeeded()
        {
            if (_eplProbed)
            {
                return;
            }

            _eplProbed = true;
            try
            {
                var type = CatalogParity.ResolveType(
                    "EnhancedPrefabLoader.Core.EplRuntimeData", "EnhancedPrefabLoader");
                _eplAssets = type?.GetProperty("Assets", AnyMember);
                var assets = _eplAssets?.GetValue(null);
                _eplItemLibrary = assets?.GetType().GetProperty("ItemLibrary", AnyMember);
                var library = _eplItemLibrary?.GetValue(assets);
                _eplRestockEntries = library?.GetType().GetProperty("RestockEntries", AnyMember);

                _eplServices = type?.GetProperty("Services", AnyMember);
                var services = _eplServices?.GetValue(null);
                _eplSaveDataManager = services?.GetType().GetProperty("SaveDataManager", AnyMember);
                ResolveEplSaveDataSurface(_eplSaveDataManager?.GetValue(services));

                var available = _eplRestockEntries != null;
                if (!_eplLogged || available != _eplAvailable)
                {
                    _eplLogged = true;
                    _eplAvailable = available;
                    CoopPlugin.Log.LogInfo(!available
                        ? "Catalog: EPL virtual products unavailable; using vanilla rows."
                        : "Catalog: EPL virtual products enabled.");
                }
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("Catalog EPL surface probe failed: " + error.Message);
            }
        }

        private static void ResolveEplSaveDataSurface(object saveDataManager)
        {
            if (saveDataManager == null || _eplTryGetSaveData != null)
            {
                return;
            }

            foreach (var method in saveDataManager.GetType().GetMethods(AnyMember))
            {
                if (method.Name != "TryGetSaveData" || !method.IsGenericMethodDefinition
                    || method.GetGenericArguments().Length != 2
                    || method.GetParameters().Length != 2)
                {
                    continue;
                }

                var generic = method.MakeGenericMethod(typeof(EItemType),
                    CatalogParity.ResolveType(
                        "EnhancedPrefabLoader.Core.Models.SaveData.ItemSaveData",
                        "EnhancedPrefabLoader"));
                _eplTryGetSaveData = generic;
                var dataType = generic.GetGenericArguments()[1];
                _eplBigUnlocked = dataType.GetProperty("IsBigBoxUnlocked", AnyMember);
                _eplSmallUnlocked = dataType.GetProperty("IsSmallBoxUnlocked", AnyMember);
                return;
            }
        }

        private static bool SetEplVirtualLicense(int index, bool unlocked)
        {
            ProbeEplIfNeeded();
            var inventory = SceneInventory();
            var baseCount = inventory?.m_StockItemData_SO?.m_RestockDataList?.Count ?? 0;
            var data = At(index);
            if (data == null || index < baseCount || _eplTryGetSaveData == null)
            {
                return false;
            }

            var services = _eplServices?.GetValue(null);
            var saveDataManager = _eplSaveDataManager?.GetValue(services);
            if (saveDataManager == null)
            {
                return false;
            }

            var args = new object[] { data.itemType, null };
            if (!(bool)_eplTryGetSaveData.Invoke(saveDataManager, args) || args[1] == null)
            {
                return false;
            }

            var property = data.isBigBox ? _eplBigUnlocked : _eplSmallUnlocked;
            if (property == null || !property.CanWrite)
            {
                return false;
            }
            property.SetValue(args[1], unlocked);
            return true;
        }
    }
}
