using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using CardShopCoop.Net;
using UnityEngine;

namespace CardShopCoop.Modules.SaveTransfer
{
    /// <summary>One mod-owned file written during a host save, ready to ship.</summary>
    internal sealed class SaveTransferFile
    {
        internal readonly string RelativePath;
        internal readonly byte[] Data;

        internal SaveTransferFile(string relativePath, byte[] data)
        {
            RelativePath = relativePath;
            Data = data;
        }
    }

    /// <summary>The files a host save wrote, plus whether the set had to be trimmed.</summary>
    internal sealed class SaveTransferFileSet
    {
        internal List<SaveTransferFile> Files = new();
        internal bool Complete = true;
        internal string Warning;
        internal long RawBytes;
    }

    /// <summary>
    /// Ships the mod-owned files a host save actually wrote. Rather than scanning by a hardcoded
    /// filename pattern (which misses a mod that names its file differently), we bracket the
    /// synchronous save with a timestamp and collect every mod-data file whose last-write time
    /// falls inside that window. Files are addressed by their path relative to
    /// persistentDataPath; the host snapshot slot token in a filename is rewritten to the guest's
    /// reserved slot on apply. enum_values.json is never sent (it is written at prepatch, not on
    /// save, and is deliberately out of scope).
    /// </summary>
    internal static class SaveTransferSidecars
    {
        internal const int MaxFiles = 4096;
        internal const int MaxFileBytes = 16 * 1024 * 1024;
        internal const int MaxAggregateRawBytes = SaveTransferStorage.MaxSidecarRawBytes;
        private const int MaxWarningLength = 512;
        private const int MaxWarningDetails = 8;
        // Filesystems with coarse timestamps (FAT ~2s) need slack; NTFS is far finer.
        private const int FreshnessSlackSeconds = 2;

        /// <summary>Enumerates every file written at or after <paramref name="sinceUtc"/> under the
        /// mod-data directories (every direct subdirectory of <paramref name="root"/> except
        /// Screenshots/Unity, recursively). Callers bracket the synchronous game save with the
        /// timestamp. Reads the file bytes here, so invoke off the main thread.</summary>
        internal static SaveTransferFileSet CollectWrittenFiles(string root, DateTime sinceUtc)
        {
            if (string.IsNullOrEmpty(root))
                throw new ArgumentException("A sidecar root is required.", nameof(root));

            var result = new SaveTransferFileSet();
            var warningDetails = new List<string>();
            var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var threshold = sinceUtc.AddSeconds(-FreshnessSlackSeconds);

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(rootPath);
            }
            catch (Exception error)
            {
                throw new InvalidDataException("mod-data directories could not be enumerated: "
                    + error.Message);
            }

            for (var i = 0; i < directories.Length; i++)
            {
                var directory = directories[i];
                var name = Path.GetFileName(directory);
                if (string.Equals(name, "Screenshots", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "Unity", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string[] directoryFiles;
                try
                {
                    directoryFiles = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
                }
                catch (Exception error)
                {
                    AddWarning("directory '" + GetRelativePath(rootPath, directory)
                        + "' could not be enumerated: " + error.Message, warningDetails);
                    result.Complete = false;
                    continue;
                }

                for (var j = 0; j < directoryFiles.Length; j++)
                {
                    var file = directoryFiles[j];
                    var fileName = Path.GetFileName(file);
                    if (string.Equals(fileName, "enum_values.json", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (!IsFresh(file, threshold))
                    {
                        continue;
                    }
                    if (result.Files.Count >= MaxFiles)
                    {
                        AddWarning("more than " + MaxFiles + " files were written; the rest were not"
                            + " considered", warningDetails);
                        result.Complete = false;
                        break;
                    }

                    var relative = GetRelativePath(rootPath, file);
                    try
                    {
                        var length = new FileInfo(file).Length;
                        if (length > MaxFileBytes)
                        {
                            SkipFile(relative, "is " + length + " bytes; the per-file limit is "
                                + MaxFileBytes + " bytes", warningDetails);
                            result.Complete = false;
                            continue;
                        }
                        if (length < 0 || result.RawBytes + length > MaxAggregateRawBytes)
                        {
                            SkipFile(relative, "would exceed the aggregate raw limit of "
                                + MaxAggregateRawBytes + " bytes", warningDetails);
                            result.Complete = false;
                            continue;
                        }

                        var data = ReadStableFile(file, (int)length);
                        if (data == null)
                        {
                            SkipFile(relative, "changed while it was being read", warningDetails);
                            result.Complete = false;
                            continue;
                        }

                        result.Files.Add(new SaveTransferFile(relative, data));
                        result.RawBytes += data.Length;
                    }
                    catch (Exception error)
                    {
                        SkipFile(relative, "could not be read: " + error.Message, warningDetails);
                        result.Complete = false;
                    }
                }
            }

            result.Files.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
            result.Warning = BuildWarning(warningDetails);
            CoopPlugin.Log?.LogInfo("Sidecar gather: " + result.Files.Count + " files written by the save, "
                + result.RawBytes / 1024 + " KB raw"
                + (result.Complete ? "" : " (partial)"));
            return result;
        }

        /// <summary>Serializes the gathered files, dropping entries from the end until the encoded
        /// bundle fits the wire cap.</summary>
        internal static byte[] BuildBundle(SaveTransferFileSet set)
        {
            if (set == null)
            {
                throw new ArgumentNullException(nameof(set));
            }

            while (true)
            {
                var payload = SerializeEntries(set.Files);
                if (payload.Length <= SaveTransferStorage.MaxTransferBytes)
                {
                    return payload;
                }

                if (set.Files.Count == 0)
                {
                    throw new InvalidDataException("Sidecar bundle metadata exceeds its transfer limit.");
                }

                var removed = set.Files[set.Files.Count - 1];
                set.Files.RemoveAt(set.Files.Count - 1);
                set.RawBytes -= removed.Data.Length;
                set.Complete = false;
                set.Warning = BoundWarning("file '" + removed.RelativePath
                    + "' would exceed the encoded sidecar bundle limit" + (set.Warning == null
                        ? "" : "; " + set.Warning));
                CoopPlugin.Log?.LogWarning("Sidecar skipped file '" + removed.RelativePath
                    + "' (encoded bundle limit)");
            }
        }

        private static bool IsFresh(string path, DateTime thresholdUtc)
        {
            try
            {
                return File.GetLastWriteTimeUtc(path) >= thresholdUtc;
            }
            catch (Exception error)
            {
                CoopPlugin.Log?.LogWarning("Sidecar timestamp read failed for " + path + ": "
                    + error.Message);
                return false;
            }
        }

        private static byte[] ReadStableFile(string path, int expectedLength)
        {
            var data = new byte[expectedLength];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan))
            {
                var offset = 0;
                while (offset < data.Length)
                {
                    var read = stream.Read(data, offset, data.Length - offset);
                    if (read <= 0)
                    {
                        return null;
                    }

                    offset += read;
                }

                if (stream.Length != expectedLength || stream.ReadByte() != -1)
                {
                    return null;
                }
            }
            return data;
        }

        private static byte[] SerializeEntries(List<SaveTransferFile> entries)
        {
            using (var stream = new MemoryStream())
            using (var writer = new NetWriter(stream))
            {
                writer.Write(entries.Count);
                for (var i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    writer.Write(entry.RelativePath);
                    writer.Write(entry.Data.Length);
                    writer.Write(entry.Data);
                }
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static string GetRelativePath(string root, string path)
        {
            var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var fullPath = Path.GetFullPath(path);
            if (fullPath.Length == normalizedRoot.Length)
            {
                return "";
            }
            var relative = fullPath.Substring(normalizedRoot.Length).TrimStart(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return relative.Replace(Path.DirectorySeparatorChar, '/').Replace(
                Path.AltDirectorySeparatorChar, '/');
        }

        private static void SkipFile(string relative, string reason, List<string> warningDetails)
        {
            var detail = "file '" + relative + "' " + reason;
            CoopPlugin.Log?.LogWarning("Sidecar skipped " + detail);
            AddWarning(detail, warningDetails);
        }

        private static void AddWarning(string detail, List<string> warningDetails)
        {
            if (warningDetails.Count < MaxWarningDetails)
            {
                warningDetails.Add(detail);
            }
        }

        private static string BuildWarning(List<string> warningDetails)
        {
            if (warningDetails == null || warningDetails.Count == 0)
            {
                return null;
            }

            return BoundWarning(string.Join("; ", warningDetails));
        }

        private static string BoundWarning(string warning)
        {
            if (string.IsNullOrWhiteSpace(warning))
            {
                return null;
            }
            warning = warning.Trim();
            return warning.Length <= MaxWarningLength
                ? warning : warning.Substring(0, MaxWarningLength - 3) + "...";
        }

        public static void ApplyBundleAsync(byte[] bundle, int hostSlot, int clientSlot,
            int sessionGeneration, Action completed, Action<Exception> failed)
        {
            ApplyBundleAsync(bundle, hostSlot, clientSlot, sessionGeneration,
                Application.persistentDataPath, completed, failed);
        }

        internal static void ApplyBundleAsync(byte[] bundle, int hostSlot, int clientSlot,
            int sessionGeneration, string root, Action completed, Action<Exception> failed)
        {
            if (bundle == null || bundle.Length < 4
                || bundle.Length > SaveTransferStorage.MaxTransferBytes)
            {
                throw new InvalidDataException("Sidecar bundle is empty or exceeds its limit.");
            }

            var copy = new byte[bundle.Length];
            Buffer.BlockCopy(bundle, 0, copy, 0, bundle.Length);
            var thread = new Thread(() =>
            {
                try
                {
                    lock (SaveTransferRuntime.TransferLock)
                    {
                        if (!SaveTransferRuntime.IsSessionGeneration(sessionGeneration))
                        {
                            CoopPlugin.Log.LogInfo("coop: stale sidecar apply discarded before disk write");
                            return;
                        }
                        ApplyBundle(copy, hostSlot, clientSlot, root);
                    }

                    if (!SaveTransferRuntime.IsSessionGeneration(sessionGeneration))
                    {
                        return;
                    }
                    if (!SaveTransferRuntime.TryEnqueueMainThread(() =>
                    {
                        if (SaveTransferRuntime.IsSessionGeneration(sessionGeneration))
                        {
                            completed?.Invoke();
                        }
                    }))
                    {
                        throw new InvalidOperationException("The co-op main-thread queue is unavailable.");
                    }
                }
                catch (Exception error)
                {
                    CoopPlugin.Log.LogError("coop: sidecar apply worker failed: " + error);
                    if (SaveTransferRuntime.IsSessionGeneration(sessionGeneration))
                    {
                        SaveTransferRuntime.TryEnqueueMainThread(() => failed?.Invoke(error));
                    }
                }
            })
            {
                IsBackground = true,
                Name = "CoopSidecarApply"
            };
            thread.Start();
        }

        private static void ApplyBundle(byte[] bundle, int hostSlot, int clientSlot, string root)
        {
            ValidateSlot(hostSlot);
            ValidateSlot(clientSlot);
            if (bundle == null || bundle.Length < 4
                || bundle.Length > SaveTransferStorage.MaxTransferBytes)
            {
                throw new InvalidDataException("Sidecar bundle is empty or exceeds its limit.");
            }

            var renamePattern = new Regex("(?<=_|Release)" + Regex.Escape(hostSlot.ToString())
                + "(?=_|\\.|$)", RegexOptions.CultureInvariant);
            var applied = 0;
            var skipped = 0;
            long rawBytes = 0;
            using (var reader = new NetReader(new MemoryStream(bundle, writable: false)))
            {
                var count = reader.ReadInt32();
                if (count < 0 || count > MaxFiles)
                {
                    throw new InvalidDataException("Sidecar file count is invalid.");
                }

                for (var i = 0; i < count; i++)
                {
                    var relative = reader.ReadString();
                    if (relative.Length > 1024)
                    {
                        throw new InvalidDataException("Sidecar path is too long.");
                    }
                    var length = reader.ReadInt32();
                    if (length < 0 || length > MaxFileBytes)
                    {
                        throw new InvalidDataException("Sidecar file length is invalid.");
                    }
                    if (rawBytes + length > MaxAggregateRawBytes)
                    {
                        throw new InvalidDataException("Sidecar aggregate raw length exceeds its limit.");
                    }
                    var data = reader.ReadBytes(length);
                    if (data.Length != length)
                    {
                        throw new EndOfStreamException("Sidecar file was truncated.");
                    }
                    rawBytes += length;

                    if (!TryGetSafePath(root, relative, out var originalPath))
                    {
                        RejectUnsafe(relative);
                        skipped++;
                        continue;
                    }

                    var directory = Path.GetDirectoryName(relative) ?? "";
                    var name = Path.GetFileName(relative);
                    var rewrittenName = renamePattern.Replace(name, clientSlot.ToString());
                    var rewrittenRelative = string.IsNullOrEmpty(directory)
                        ? rewrittenName : Path.Combine(directory, rewrittenName);
                    if (!TryGetSafePath(root, rewrittenRelative, out var targetPath))
                    {
                        RejectUnsafe(relative);
                        skipped++;
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                    if (File.Exists(targetPath) && !File.Exists(targetPath + ".coopbak"))
                    {
                        File.Copy(targetPath, targetPath + ".coopbak");
                    }
                    AtomicWrite(targetPath, data);
                    applied++;
                }
            }
            CoopPlugin.Log.LogInfo("Sidecar bundle applied: " + applied + " files (slot "
                + hostSlot + " -> " + clientSlot + "), " + skipped + " skipped");
        }

        private static void ValidateSlot(int slot)
        {
            if (slot == -1)
            {
                throw new ArgumentOutOfRangeException(nameof(slot),
                    "Save slot -1 is the game's \"no save\" sentinel and cannot be used.");
            }
        }

        private static bool TryGetSafePath(string root, string relative, out string full)
        {
            full = null;
            if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative)
                || relative.IndexOf(':') >= 0)
            {
                return false;
            }

            var pieces = relative.Split(new[] { '/', '\\' }, StringSplitOptions.None);
            foreach (var piece in pieces)
            {
                if (piece == ".." || piece == "." || piece.Length == 0)
                {
                    return false;
                }
            }

            var rootPath = Path.GetFullPath(root);
            if (!rootPath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                rootPath += Path.DirectorySeparatorChar;
            }
            full = Path.GetFullPath(Path.Combine(rootPath,
                relative.Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar)));
            return full.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)
                && full.Length > rootPath.Length;
        }

        private static void RejectUnsafe(string relative)
        {
            CoopPlugin.Log.LogWarning("sidecar: rejecting unsafe path '" + relative + "'");
        }

        private static void AtomicWrite(string path, byte[] data)
        {
            var temporary = path + ".cooptmp." + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(temporary, data);
                if (File.Exists(path))
                {
                    File.Replace(temporary, path, null);
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
    }
}
