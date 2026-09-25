using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using CardShopCoop.Modules.Catalog;
using CardShopCoop.Modules.World;
using CardShopCoop.Runtime;
using UnityEngine;

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
        public const int MaxTransferBytes = 32 * 1024 * 1024;
        public const int MaxSidecarRawBytes = MaxTransferBytes;
        public const int MaxExpandedBytes = 64 * 1024 * 1024;

        private const int FreshnessSlackSeconds = 1;
        private static string _startSceneName;

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
        /// that the client will load. <paramref name="saveStartedUtc"/> is the instant the save
        /// began; every mod-data file written at or after it is the sidecar set to ship.
        /// </summary>
        public static byte[] BuildHostPayload(out DateTime saveStartedUtc)
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
            try
            {
                // SaveGameData is synchronous on both required builds. The save guard patch
                // permits this on the host, while the UpdateSlot patch handles beta slot 6 UI.
                manager.SaveGameData(HostSnapshotSlot);
            }
            catch (Exception error)
            {
                // The beta UI can throw after the save bytes have already been committed. The
                // freshness/counter checks below decide whether that is still a valid snapshot.
                CoopPlugin.Log.LogWarning("coop: SaveGameData(" + HostSnapshotSlot + ") threw: " + error.Message);
            }
            finally
            {
                manager.m_CurrentSaveLoadSlotSelectedIndex = previousSlot;
            }

            // The world snapshot has now been serialized in live-list order. Capture the matching
            // box slots immediately so the box baseline can name each scene box deterministically.
            WorldHostBehaviour.CaptureTransferBoxSlots();

            if (IsFresh(path, timestamp))
            {
                var bytes = File.ReadAllBytes(path);
                ValidateSavePayload(bytes);
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

        private static bool IsFresh(string path, DateTime before)
        {
            try
            {
                return File.Exists(path)
                    && File.GetLastWriteTimeUtc(path) >= before.AddSeconds(-FreshnessSlackSeconds);
            }
            catch (Exception error)
            {
                Swallow.Log(error);
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
                Swallow.Log(error);
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
                Swallow.Log(error);
                return null;
            }
        }

        private static string DescribeWorldJson(byte[] bytes)
        {
            try
            {
                var json = new UTF8Encoding(false).GetString(bytes);
                int? day = null;
                const string dayKey = "\"m_CurrentDay\":";
                var dayAt = json.IndexOf(dayKey, StringComparison.Ordinal);
                if (dayAt >= 0)
                {
                    var start = dayAt + dayKey.Length;
                    var end = start;
                    while (end < json.Length && (json[end] == '-' || char.IsDigit(json[end])))
                    {
                        end++;
                    }
                    if (int.TryParse(json.Substring(start, end - start), out var parsed))
                    {
                        day = parsed;
                    }
                }

                string player = null;
                const string playerKey = "\"m_PlayerName\":\"";
                var playerAt = json.IndexOf(playerKey, StringComparison.Ordinal);
                if (playerAt >= 0)
                {
                    var start = playerAt + playerKey.Length;
                    var end = start;
                    while (end < json.Length && !(json[end] == '"' && json[end - 1] != '\\'))
                    {
                        end++;
                    }
                    if (end < json.Length)
                    {
                        player = json.Substring(start, end - start);
                    }
                }

                var description = Describe(day, player);
                return description == null ? "" : " - " + description;
            }
            catch (Exception error)
            {
                Swallow.Log(error);
                return "";
            }
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
            if (bytes == null || bytes.Length < 32)
            {
                throw new InvalidDataException("Save payload is empty or too small.");
            }
            var json = new UTF8Encoding(false).GetString(bytes).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
            if (!json.StartsWith("{", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Save payload is not a JSON world.");
            }
        }

        public static void ApplyAndLoadAsync(byte[] saveBytes, int sessionGeneration,
            Action completed, Action<Exception> failed)
        {
            ValidateSavePayload(saveBytes);
            var copy = new byte[saveBytes.Length];
            Buffer.BlockCopy(saveBytes, 0, copy, 0, saveBytes.Length);
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

                        File.WriteAllBytes(path, copy);
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
                            ApplyAndLoad(copy);
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
        public static void ApplyAndLoad(byte[] saveBytes)
        {
            ValidateSavePayload(saveBytes);
            var manager = SceneRef<CGameManager>.Get();
            if (manager == null)
            {
                throw new InvalidOperationException("The game manager is not available for world load.");
            }

            // The bytes are already on disk at the reserved slot. The ONLY way the world loads is
            // the game's own CSaveLoad.Load(slot) (reached via ForceLoadSlot), so EPL's per-slot
            // load prefix and the native restore both run exactly as they do for a local save.
            manager.m_ForceNoCloudSaveLoad = true;
            CoopPlugin.Log.LogInfo("Coop save received (" + saveBytes.Length / 1024 + " KB), loading world...");
            // One-shot: the native load that follows is the host's save, whose gated enum ids are
            // in the host's space; translate them before the game propagates them.
            SaveEnumRemap.Arm();
            ForceLoadSlot(CoopSlot);
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
            typeof(CGameManager).GetField("m_InitLoaded", BindingFlags.Static
                | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(null, false);
            manager.LoadMainLevelAsync(StartSceneName(), slot);
        }

        private static string StartSceneName()
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
                Swallow.Log(error);
            }

            if (string.IsNullOrEmpty(_startSceneName))
            {
                _startSceneName = "Start";
            }
            CoopPlugin.Log.LogInfo("coop: loading co-op world via scene '" + _startSceneName + "'");
            return _startSceneName;
        }

        internal static byte[] GunzipCapped(byte[] compressed, int cap)
        {
            if (compressed == null || compressed.Length == 0)
            {
                return new byte[0];
            }
            if (compressed.Length > MaxTransferBytes || cap <= 0 || cap > MaxExpandedBytes)
            {
                throw new InvalidDataException("Compressed transfer exceeds its safety limit.");
            }

            using (var source = new MemoryStream(compressed, false))
            using (var gzip = new System.IO.Compression.GZipStream(source,
                System.IO.Compression.CompressionMode.Decompress))
            using (var destination = new MemoryStream())
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
                return destination.ToArray();
            }
        }
    }
}
