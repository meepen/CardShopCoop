using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using CardShopCoop.Modules.Catalog;
using CardShopCoop.Modules.World;
using CardShopCoop.Runtime;
using CardShopCoop.Util;
using UnityEngine;
using CardShopCoop.Api;

namespace CardShopCoop.Modules.SaveTransfer
{
    /// <summary>
    /// Owns the game's base-save snapshot and application.  The two live game versions share the
    /// CGameManager entry points, but their CSaveLoad backends differ (local JSON versus Gamecore
    /// containers).  Save object fields and backend-specific state are therefore resolved from
    /// the running assembly, never from a beta-only type or lookup API.
    /// </summary>
    public static class SaveTransferStorage
    {
        public const int HostSnapshotSlot = 6;

        /// <summary>The reserved slot the borrowed co-op world lives in on a guest. It is a
        /// negative value so it can never collide with a real save (0..99) or with the game's
        /// "-1 = no save" sentinel: CSaveLoad.Load(-100) reads savedGames_Release-100.json and EPL
        /// reads PrefabLoader/EnhancedPrefabLoader_-100, neither of which the save UI ever shows,
        /// so a guest's own local saves are never touched.</summary>
        public const int ReservedCoopSlot = -100;
        /// <summary>The largest single payload either side will hold: the save, the mod-data bundle, and
        /// the aggregate of the sidecar files inside it. Raised from 32 MiB so a bundled asset of the
        /// size <see cref="SaveTransferSidecars"/>'s per-file cap allows can actually cross. The receive
        /// buffers GROW to what arrives rather than reserving this, so this bounds a legitimate transfer
        /// instead of being allocated up front.</summary>
        public const int MaxTransferBytes = 256 * 1024 * 1024;
        public const int MaxSidecarRawBytes = MaxTransferBytes;

        /// <summary>Decompression cap. It must stay above <see cref="MaxTransferBytes"/>, or a payload we
        /// permit would be refused the moment it expanded. Still a bomb guard - just sized for the new
        /// transfer cap rather than the old one.</summary>
        public const int MaxExpandedBytes = 512 * 1024 * 1024;

        private const int FreshnessSlackSeconds = 1;
        private const int NoSlot = int.MinValue;
        private static string _startSceneName;

        // ONE-SHOT BORROWED-WORLD PAYLOAD consumed by the CSaveLoad.Load prefix (SaveTransferPatches).
        // The guest load entry point is CGameManager.LoadData, whose first act is CSaveLoad.Load(slot);
        // on the local-file backend that reads the JSON we wrote, but on the Xbox/Gamecore backend
        // CSaveLoad.Load reads PlatformManager's native store and the file we wrote is never read -
        // which is exactly why a Game Pass guest used to fall through to CreateDefaultData. The prefix
        // injects this payload straight into CSaveLoad.m_SavedGame with the same Unity parser the host
        // serialized with, so the world loads identically on both backends. Main-thread only.
        private static byte[] _borrowedWorldPayload;
        private static int _borrowedWorldPayloadLength;
        private static int _borrowedWorldSlot = NoSlot;

        public static int CoopSlot
        {
            get
            {
                var configured = CoopPlugin.ClientWorldSlot?.Value ?? ReservedCoopSlot;
                // -1 is the game's "no save" sentinel; never use it. Anything else is safe because
                // the slot is only ever used to build the save filename.
                return configured == -1 ? ReservedCoopSlot : configured;
            }
        }

        public static string SlotPath(int slot)
        {
            ValidateSlot(slot);
            return Path.Combine(Application.persistentDataPath, "savedGames_Release" + slot + ".json");
        }

        private static void ValidateSlot(int slot)
        {
            if (slot == -1)
            {
                throw new ArgumentOutOfRangeException(nameof(slot),
                    "Save slot -1 is the game's \"no save\" sentinel and cannot be used.");
            }
        }

        /// <summary>
        /// Force-saves the live host world into the throwaway slot and returns exactly the bytes
        /// that the client will load. A stale slot file is never accepted: either a fresh local
        /// file proves completion, or the game's save counter proves the in-memory backend ran.
        /// </summary>
        public static byte[] BuildHostPayload()
            => BuildHostPayload(out _);

        /// <summary>
        /// Force-saves the live host world into the throwaway slot and returns exactly the bytes
        /// that the client will load, freezing the identity manifest for the authorizing
        /// connection in the same instant. <paramref name="saveStartedUtc"/> is the instant the
        /// save began; every mod-data file written at or after it is the sidecar set to ship.
        /// </summary>
        public static byte[] BuildHostPayload(out DateTime saveStartedUtc)
            => BuildHostPayload(0, out saveStartedUtc);

        /// <summary>
        /// Force-saves the live host world into the throwaway slot and returns exactly the bytes
        /// that the client will load, freezing the per-connection identity manifest (box slots and
        /// placement slots) at the moment the save is serialized. <paramref name="connectionId"/>
        /// is the authorizing peer; 0 captures no per-connection manifest.
        /// </summary>
        public static byte[] BuildHostPayload(int connectionId, out DateTime saveStartedUtc)
        {
            ValidateSlot(HostSnapshotSlot);
            var manager = SceneRef<CGameManager>.Get();
            if (manager == null)
            {
                throw new InvalidOperationException("The game manager is not available for a host snapshot.");
            }

            var path = SlotPath(HostSnapshotSlot);
            TryDelete(path);
            TryDelete(Path.Combine(Application.persistentDataPath,
                "savedGames_Release" + HostSnapshotSlot + ".gd"));

            var timestamp = DateTime.UtcNow;
            saveStartedUtc = timestamp;
            var counterBeforeKnown = Net.PlatformProbe.TrySampleSaveCounter(out var indexBefore,
                out var cycleBefore);
            var previousSlot = manager.m_CurrentSaveLoadSlotSelectedIndex;
            MemoryProbe.Log("host-snapshot:before");
            try
            {
                SaveLiveWorldForSnapshot(manager);
            }
            catch (Exception error)
            {
                // The beta UI can throw after the save bytes have already been committed. The
                // freshness/counter checks below decide whether that is still a valid snapshot.
                CoopPlugin.Log.LogWarning("coop: SaveGameData(" + HostSnapshotSlot + ") threw: " + error.Message);
            }
            finally
            {
                MemoryProbe.Log("host-snapshot:after-save");
            }

            try
            {
                var epl6 = Path.Combine(Application.persistentDataPath,
                    "EnhancedPrefabLoader_" + HostSnapshotSlot + ".json");
                if (!IsFresh(epl6, timestamp))
                {
                    string srcEpl = null;
                    if (previousSlot >= 0)
                    {
                        var candidate = Path.Combine(Application.persistentDataPath,
                            "EnhancedPrefabLoader_" + previousSlot + ".json");
                        if (File.Exists(candidate))
                        {
                            srcEpl = candidate;
                        }
                    }

                    if (srcEpl == null)
                    {
                        var latestTime = DateTime.MinValue;
                        var eplFiles = Directory.GetFiles(Application.persistentDataPath,
                            "EnhancedPrefabLoader_*.json", SearchOption.TopDirectoryOnly);
                        for (var i = 0; i < eplFiles.Length; i++)
                        {
                            var f = eplFiles[i];
                            var fn = Path.GetFileName(f);
                            if (fn.IndexOf("enum_values", StringComparison.OrdinalIgnoreCase) >= 0
                                || fn.IndexOf("-100", StringComparison.OrdinalIgnoreCase) >= 0
                                || string.Equals(f, epl6, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            var wt = File.GetLastWriteTimeUtc(f);
                            if (wt > latestTime)
                            {
                                latestTime = wt;
                                srcEpl = f;
                            }
                        }
                    }

                    if (srcEpl != null && File.Exists(srcEpl))
                    {
                        File.Copy(srcEpl, epl6, true);
                        File.SetLastWriteTimeUtc(epl6, DateTime.UtcNow);
                    }
                }
            }
            catch (Exception error)
            {
                CoopLog.Swallow(error);
            }

            // The world snapshot has now been serialized in live-list order. Freeze the matching
            // per-connection identity manifest immediately, using the game's own save filter, so
            // the baseline can name each box/placement by its snapshot slot deterministically.
            WorldHostBehaviour.CaptureTransferManifest(connectionId);

            if (IsFresh(path, timestamp))
            {
                var bytes = File.ReadAllBytes(path);
                ValidateSavePayload(bytes);
                MemoryProbe.Log("host-snapshot:after-read " + bytes.Length / 1024 + "KB");
                CoopPlugin.Log.LogInfo("coop: shipping world from slot file" + DescribeWorldJson(bytes)
                    + " (" + bytes.Length / 1024 + " KB)");
                return bytes;
            }

            var counterAfterKnown = Net.PlatformProbe.TrySampleSaveCounter(out var indexAfter,
                out var cycleAfter);
            var counterKnown = counterBeforeKnown && counterAfterKnown;
            var saveRan = counterKnown && (indexBefore != indexAfter || cycleBefore != cycleAfter);
            var backendAllowsMemory = saveRan || (!counterKnown && Net.PlatformProbe.GamePassBuild);
            if (backendAllowsMemory)
            {
                var memory = SerializeInMemorySave(saveRan ? indexAfter : -1, out var identity);
                if (memory != null)
                {
                    CoopPlugin.Log.LogInfo("coop: shipping in-memory world"
                        + (identity == null ? "" : " - " + identity) + " (" + memory.Length / 1024
                        + " KB, " + (saveRan ? "save-completion counter advanced" : "Gamecore backend") + ")");
                    return memory;
                }
            }

            throw new FileNotFoundException(counterKnown && !saveRan
                ? "Host snapshot failed: SaveGameData did not complete and no fresh slot file exists. Load into the shop fully, then retry."
                : "Host snapshot failed: neither a fresh slot file nor a readable in-memory world was produced. Load into the shop fully, then retry.", path);
        }

        /// <summary>
        /// Serializes the live world into the scratch slot through the data owner instead of
        /// <c>CGameManager.SaveGameData</c>. The wrapper adds UI bookkeeping and ends with a full
        /// <c>GC.Collect()</c>; a scratch snapshot only needs the serializer, so the UI side
        /// effects and that collection are not wanted. The data method is the same serializer the
        /// wrapper calls (CSaveLoad.Save), so the JSON, the .gd backup and every save postfix
        /// (including EPL's sidecar) are unchanged.
        /// </summary>
        private static void SaveLiveWorldForSnapshot(CGameManager manager)
        {
            if (manager == null)
            {
                throw new InvalidOperationException("The game manager is not available for a host snapshot.");
            }
            if (!manager.m_IsGameLevel)
            {
                throw new InvalidOperationException(
                    "The host is not in a gameplay level, so no live world can be snapshotted.");
            }
            if (GameInstance.m_HasLoadingError)
            {
                throw new InvalidOperationException(
                    "The loaded world has a loading error, so it cannot be snapshotted.");
            }
            if (!GameInstance.m_FinishedSavefileLoading)
            {
                throw new InvalidOperationException(
                    "The world is still loading, so it cannot be snapshotted yet.");
            }
            if (CPlayerData.m_CashCounterSaveDataList == null
                || CPlayerData.m_CashCounterSaveDataList.Count <= 0)
            {
                throw new InvalidOperationException(
                    "The live world has no shelf/counter data, so it cannot be snapshotted.");
            }

            var data = CGameData.instance;
            if (data == null)
            {
                throw new InvalidOperationException("The live game data is not available for a snapshot.");
            }

            // Preserve the manager flags exactly as the user-facing save path leaves them. The
            // scratch slot is not a UI save, so no slot-list or autosave-cooldown side effect is
            // wanted; only the slot index is consumed by a mod save hook during CSaveLoad.Save.
            var previousSlot = manager.m_CurrentSaveLoadSlotSelectedIndex;
            var previousManual = manager.m_IsManualSaveLoad;
            try
            {
                manager.m_CurrentSaveLoadSlotSelectedIndex = HostSnapshotSlot;
                manager.m_IsManualSaveLoad = true;
                data.SaveGameData(HostSnapshotSlot);
            }
            finally
            {
                manager.m_CurrentSaveLoadSlotSelectedIndex = previousSlot;
                manager.m_IsManualSaveLoad = previousManual;
            }
        }

        private static bool IsFresh(string path, DateTime before)
        {
            try
            {
                return File.Exists(path)
                    && File.GetLastWriteTimeUtc(path) >= before.AddSeconds(-FreshnessSlackSeconds);
            }
            catch (Exception error)
            {
                CoopLog.Swallow(error);
                return false;
            }
        }

        private static byte[] SerializeInMemorySave(int expectedSaveIndex, out string identity)
        {
            identity = null;
            try
            {
                var field = typeof(CSaveLoad).GetField("m_SavedGame",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null)
                {
                    return null;
                }

                var saved = field.GetValue(null);
                if (saved == null || !MatchesSaveIndex(saved, expectedSaveIndex))
                {
                    var liveField = field.FieldType.GetField("instance",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    var live = liveField?.GetValue(null);
                    if (live != null)
                    {
                        saved = live;
                    }
                }

                if (saved == null)
                {
                    return null;
                }

                identity = DescribeWorldObject(saved);
                var json = JsonUtility.ToJson(saved);
                if (string.IsNullOrEmpty(json) || json.Length < 32 || json == "{}")
                {
                    return null;
                }

                return new UTF8Encoding(false).GetBytes(json);
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("coop: in-memory save serialization failed: " + error.Message);
                return null;
            }
        }

        private static bool MatchesSaveIndex(object saved, int expected)
        {
            if (expected < 0)
            {
                return true;
            }

            try
            {
                var field = saved.GetType().GetField("m_SaveIndex",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return field == null || (int)field.GetValue(saved) == expected;
            }
            catch (Exception error)
            {
                CoopLog.Swallow(error);
                return true;
            }
        }

        private static string DescribeWorldObject(object world)
        {
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var type = world.GetType();
                var day = type.GetField("m_CurrentDay", flags)?.GetValue(world);
                var player = type.GetField("m_PlayerName", flags)?.GetValue(world) as string;
                return Describe(day is int d ? d : (int?)null, player);
            }
            catch (Exception error)
            {
                CoopLog.Swallow(error);
                return null;
            }
        }

        /// <summary>
        /// Reads only the two log fields out of the UTF-8 save. Decoding the whole world into a
        /// UTF-16 string here used to allocate roughly twice the save size on every join just to
        /// print a day and a shop name; the byte scan is allocation-free apart from the two values.
        /// </summary>
        private static string DescribeWorldJson(byte[] bytes)
        {
            try
            {
                int? day = null;
                const string dayKey = "\"m_CurrentDay\":";
                var dayAt = IndexOfAscii(bytes, dayKey);
                if (dayAt >= 0)
                {
                    var start = dayAt + dayKey.Length;
                    while (start < bytes.Length && (bytes[start] == ' ' || bytes[start] == '\t'
                        || bytes[start] == '\r' || bytes[start] == '\n'))
                    {
                        start++;
                    }
                    var end = start;
                    if (end < bytes.Length && bytes[end] == '-')
                    {
                        end++;
                    }
                    while (end < bytes.Length && bytes[end] >= '0' && bytes[end] <= '9')
                    {
                        end++;
                    }
                    if (end > start
                        && int.TryParse(Encoding.ASCII.GetString(bytes, start, end - start),
                            out var parsed))
                    {
                        day = parsed;
                    }
                }

                string player = null;
                const string playerKey = "\"m_PlayerName\":\"";
                var playerAt = IndexOfAscii(bytes, playerKey);
                if (playerAt >= 0)
                {
                    var start = playerAt + playerKey.Length;
                    var end = start;
                    while (end < bytes.Length
                        && !(bytes[end] == '"' && bytes[end - 1] != '\\'))
                    {
                        end++;
                    }
                    if (end < bytes.Length)
                    {
                        player = Encoding.UTF8.GetString(bytes, start, end - start);
                    }
                }

                var description = Describe(day, player);
                return description == null ? "" : " - " + description;
            }
            catch (Exception error)
            {
                CoopLog.Swallow(error);
                return "";
            }
        }

        private static int IndexOfAscii(byte[] bytes, string ascii)
        {
            if (bytes == null || ascii == null || ascii.Length == 0
                || bytes.Length < ascii.Length)
            {
                return -1;
            }

            var last = bytes.Length - ascii.Length;
            for (var i = 0; i <= last; i++)
            {
                var match = true;
                for (var j = 0; j < ascii.Length; j++)
                {
                    if (bytes[i + j] != ascii[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return i;
                }
            }

            return -1;
        }

        private static string Describe(int? day, string player)
        {
            if (day == null && string.IsNullOrEmpty(player))
            {
                return null;
            }
            if (!string.IsNullOrEmpty(player) && player.Length > 64)
            {
                player = player.Substring(0, 64) + "...";
            }
            if (day == null)
            {
                return "shop \"" + player + "\"";
            }
            return string.IsNullOrEmpty(player) ? "day " + day.Value
                : "day " + day.Value + ", shop \"" + player + "\"";
        }

        internal static void ValidateSavePayload(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new InvalidDataException("Save payload is empty or too small.");
            }
            ValidateSavePayload(bytes, bytes.Length);
        }

        /// <summary>Validates only the leading bytes of a save payload. The world JSON is a single
        /// object whose first non-whitespace character is '{', so decoding the whole 512 MiB payload
        /// into a string just to inspect that character would allocate the entire world twice.</summary>
        internal static void ValidateSavePayload(byte[] buffer, int length)
        {
            const int prefixBytes = 64;
            if (buffer == null || length < 32)
            {
                throw new InvalidDataException("Save payload is empty or too small.");
            }
            var json = new UTF8Encoding(false).GetString(buffer, 0, Math.Min(length, prefixBytes))
                .TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
            if (!json.StartsWith("{", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Save payload is not a JSON world.");
            }
        }

        public static void ApplyAndLoadAsync(MemoryStream save, int sessionGeneration,
            Action completed, Action<Exception> failed)
        {
            if (!save.TryGetBuffer(out var segment) || segment.Array == null)
            {
                throw new InvalidOperationException("The received world buffer is not accessible.");
            }
            var buffer = segment.Array;
            var length = (int)save.Length;
            ValidateSavePayload(buffer, length);
            var thread = new Thread(() =>
            {
                try
                {
                    lock (SaveTransferRuntime.TransferLock)
                    {
                        if (!SaveTransferRuntime.IsSessionGeneration(sessionGeneration))
                        {
                            CoopPlugin.Log.LogInfo("coop: stale save apply discarded before disk write");
                            return;
                        }

                        var path = SlotPath(CoopSlot);
                        var binaryPath = Path.ChangeExtension(path, ".gd");
                        var backupPath = Path.Combine(Application.persistentDataPath,
                            "savedGames_ReleaseBackupFile" + CoopSlot + ".json");
                        TryDelete(binaryPath);
                        TryDelete(path);
                        TryDelete(backupPath);
                        if (File.Exists(path) || File.Exists(binaryPath) || File.Exists(backupPath))
                        {
                            throw new IOException("A previous co-op save could not be cleared.");
                        }

                        // Stream the buffer straight to disk; the expanded world is never copied
                        // into a second full-size array.
                        using (var file = new FileStream(path, FileMode.Create, FileAccess.Write,
                            FileShare.None))
                        {
                            file.Write(buffer, 0, length);
                        }
                    }

                    if (!SaveTransferRuntime.IsSessionGeneration(sessionGeneration))
                    {
                        CoopPlugin.Log.LogInfo("coop: stale save apply discarded before load");
                        return;
                    }

                    if (!SaveTransferRuntime.TryEnqueueMainThread(() =>
                    {
                        if (!SaveTransferRuntime.IsSessionGeneration(sessionGeneration))
                        {
                            return;
                        }
                        try
                        {
                            ApplyAndLoad(buffer, length);
                            completed?.Invoke();
                        }
                        catch (Exception error)
                        {
                            CoopPlugin.Log.LogError("coop: received world main-thread apply failed: " + error);
                            failed?.Invoke(error);
                        }
                    }))
                    {
                        throw new InvalidOperationException("The co-op main-thread queue is unavailable.");
                    }
                }
                catch (Exception error)
                {
                    CoopPlugin.Log.LogError("coop: save apply worker failed: " + error);
                    if (SaveTransferRuntime.IsSessionGeneration(sessionGeneration))
                    {
                        SaveTransferRuntime.TryEnqueueMainThread(() => failed?.Invoke(error));
                    }
                }
            })
            {
                IsBackground = true,
                Name = "CoopSaveApply"
            };
            thread.Start();
        }

        /// <summary>Apply the borrowed world on Unity's main thread and enter vanilla's load path.</summary>
        public static void ApplyAndLoad(byte[] buffer, int length)
        {
            ValidateSavePayload(buffer, length);
            var manager = SceneRef<CGameManager>.Get();
            if (manager == null)
            {
                throw new InvalidOperationException("The game manager is not available for world load.");
            }

            // The bytes are also already on disk at the reserved slot, in case anything else reads
            // that file, but the disk file is NOT the load path on every backend: the public/Steam
            // builds read it through CSaveLoad.Load(slot), while the Xbox/Gamecore backend reads its
            // native store and would ignore the file entirely (falling through to LoadBackupData ->
            // CreateDefaultData). The CSaveLoad.Load prefix (SaveTransferPatches.SaveLoadPrefix)
            // therefore injects the armed payload into CSaveLoad.m_SavedGame and reports the load as
            // successful on BOTH backends, so EPL's per-slot load prefix and the game's
            // PropagateLoadData both still run exactly as they do for a local save.
            manager.m_ForceNoCloudSaveLoad = true;
            CoopPlugin.Log.LogInfo("Coop save received (" + length / 1024 + " KB), loading world...");
            // One-shot: the native load that follows is the host's save, whose gated enum ids are
            // in the host's space; translate them before the game propagates them.
            SaveEnumRemap.Arm();
            ArmBorrowedWorld(buffer, length, CoopSlot);
            ForceLoadSlot(CoopSlot);
        }

        /// <summary>Arm the one-shot borrowed-world payload the CSaveLoad.Load prefix consumes.
        /// Called on Unity's main thread immediately before the load of the borrowed world. The
        /// buffer is referenced, not copied; <paramref name="length"/> is the valid prefix.</summary>
        internal static void ArmBorrowedWorld(byte[] buffer, int length, int slot)
        {
            _borrowedWorldSlot = slot;
            _borrowedWorldPayload = buffer;
            _borrowedWorldPayloadLength = length;
        }

        /// <summary>
        /// If a borrowed world is armed for <paramref name="slot"/>, deserialize it into
        /// CSaveLoad.m_SavedGame with <see cref="JsonUtility"/> - the exact parser the host used to
        /// serialize it - and report the load as successful so CGameManager.LoadData propagates it
        /// instead of falling through to a default world. Returns false (letting the game's own
        /// load run) when no world is armed for this slot. Consumed once. Main-thread only.
        /// </summary>
        internal static bool TryInjectBorrowedWorld(int slot, out bool loaded)
        {
            loaded = false;
            var payload = _borrowedWorldPayload;
            if (payload == null || slot != _borrowedWorldSlot)
            {
                return false;
            }

            var length = _borrowedWorldPayloadLength;
            _borrowedWorldPayload = null;
            _borrowedWorldPayloadLength = 0;
            _borrowedWorldSlot = NoSlot;

            var field = typeof(CSaveLoad).GetField("m_SavedGame",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null)
            {
                throw new InvalidOperationException(
                    "CSaveLoad.m_SavedGame was not found, so the borrowed world cannot be injected.");
            }

            var json = new UTF8Encoding(false).GetString(payload, 0, length)
                .TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
            var world = JsonUtility.FromJson(json, field.FieldType);
            if (world == null)
            {
                throw new InvalidDataException("The game rejected the received world JSON.");
            }

            field.SetValue(null, world);
            loaded = true;
            CoopPlugin.Log.LogInfo("coop: injected the borrowed world into CSaveLoad for slot " + slot);
            return true;
        }

        /// <summary>Drop any world armed for a load that never reached CSaveLoad.Load (the session
        /// ended first), so a later session cannot consume a stale transfer.</summary>
        internal static void ResetBorrowedWorld()
        {
            _borrowedWorldPayload = null;
            _borrowedWorldPayloadLength = 0;
            _borrowedWorldSlot = NoSlot;
        }

        internal static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("coop: could not clear " + Path.GetFileName(path) + ": " + error.Message);
            }
        }

        public static void ForceLoadSlot(int slot)
        {
            ValidateSlot(slot);
            var manager = SceneRef<CGameManager>.Get();
            if (manager == null)
            {
                throw new InvalidOperationException("The game manager is not available for world load.");
            }

            manager.m_CurrentSaveLoadSlotSelectedIndex = slot;
            var initLoaded = typeof(CGameManager).GetField("m_InitLoaded", BindingFlags.Static
                | BindingFlags.Public | BindingFlags.NonPublic);
            if (initLoaded == null)
            {
                // Without this reset CGameManager.LoadDelay skips LoadData and the guest loads
                // its own world instead of the host's. A silent skip is worse than a loud failure.
                throw new InvalidOperationException(
                    "CGameManager.m_InitLoaded was not found, so a borrowed world cannot be loaded.");
            }

            initLoaded.SetValue(null, false);

            var sceneName = StartSceneName();
            if (string.IsNullOrEmpty(sceneName) || !Application.CanStreamedLevelBeLoaded(sceneName))
            {
                throw new InvalidOperationException("The game's shop scene is not streamable"
                    + (string.IsNullOrEmpty(sceneName) ? "." : " ('" + sceneName + "')."));
            }

            manager.LoadMainLevelAsync(sceneName, slot);
        }

        /// <summary>The gameplay scene this build loads. Every supported build exposes the name
        /// as <c>CGameManager.k_StartSceneName</c>; the fallbacks exist only for a future build
        /// that moves it or renames the scene. Cached for the process.</summary>
        internal static string StartSceneName()
        {
            if (_startSceneName != null)
            {
                return _startSceneName;
            }

            try
            {
                var field = typeof(CGameManager).GetField("k_StartSceneName",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                _startSceneName = field?.GetValue(null) as string;
            }
            catch (Exception error)
            {
                CoopLog.Swallow(error);
            }

            if (string.IsNullOrEmpty(_startSceneName)
                || !Application.CanStreamedLevelBeLoaded(_startSceneName))
            {
                _startSceneName = Application.CanStreamedLevelBeLoaded("StartOptimized")
                    ? "StartOptimized"
                    : Application.CanStreamedLevelBeLoaded("Start") ? "Start" : null;
            }

            CoopPlugin.Log.LogInfo("coop: co-op world scene is '" + _startSceneName + "'");
            return _startSceneName;
        }

        /// <summary>Gunzips a compressed transfer into a freshly grown <see cref="MemoryStream"/>
        /// positioned at 0, enforcing the same transfer and decompression caps as before. The stream
        /// is returned without a final <c>ToArray()</c>: callers read it through
        /// <see cref="MemoryStream.TryGetBuffer"/> so the expanded world is never duplicated.</summary>
        internal static MemoryStream GunzipCapped(Stream compressed, int cap)
        {
            if (compressed == null || compressed.Length == 0)
            {
                return new MemoryStream();
            }
            if (compressed.Length > MaxTransferBytes || cap <= 0 || cap > MaxExpandedBytes)
            {
                throw new InvalidDataException("Compressed transfer exceeds its safety limit.");
            }

            var destination = new MemoryStream();
            try
            {
                using (var gzip = new System.IO.Compression.GZipStream(compressed,
                    System.IO.Compression.CompressionMode.Decompress, leaveOpen: true))
                {
                    var buffer = new byte[64 * 1024];
                    int read;
                    while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (destination.Length + read > cap)
                        {
                            throw new InvalidDataException("Compressed transfer exceeds decompression limit.");
                        }
                        destination.Write(buffer, 0, read);
                    }
                }
                destination.Position = 0;
                return destination;
            }
            catch
            {
                destination.Dispose();
                throw;
            }
        }
    }
}
