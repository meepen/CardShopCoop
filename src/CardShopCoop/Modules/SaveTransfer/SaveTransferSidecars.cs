using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using CardShopCoop.Net;
using UnityEngine;

namespace CardShopCoop.Modules.SaveTransfer
{
    public sealed class SidecarBundle
    {
        public byte[] Payload
        {
            get;
        }
        public bool Complete
        {
            get;
        }
        public string Warning
        {
            get;
        }
        public int FileCount
        {
            get;
        }
        public long RawBytes
        {
            get;
        }

        internal SidecarBundle(byte[] payload, bool complete, string warning, int fileCount,
            long rawBytes)
        {
            Payload = payload ?? throw new ArgumentNullException(nameof(payload));
            Complete = complete;
            Warning = warning;
            FileCount = fileCount;
            RawBytes = rawBytes;
        }
    }

    /// <summary>
    /// Transfers mod-owned per-save files alongside the game's base save. Every received path is
    /// canonicalized under persistentDataPath before it is read or written; host data is never
    /// allowed to escape that root.
    /// </summary>
    public static class SaveTransferSidecars
    {
        internal const int MaxFiles = 4096;
        internal const int MaxFileBytes = 16 * 1024 * 1024;
        internal const int MaxAggregateRawBytes = SaveTransferStorage.MaxSidecarRawBytes;
        private const int MaxWarningLength = 512;
        private const int MaxWarningDetails = 8;

        public static byte[] BuildBundle(int hostSlot)
        {
            return BuildBundleWithMetadata(hostSlot, Application.persistentDataPath).Payload;
        }

        /// <summary>
        /// Builds the sidecar snapshot without touching Unity APIs. The caller captures the root
        /// on Unity's thread and invokes this method only after the synchronous game save has
        /// completed. This keeps directory enumeration, file reads, and bundle construction off
        /// the main thread.
        /// </summary>
        internal static SidecarBundle BuildBundleWithMetadata(int hostSlot, string root)
        {
            ValidateSlot(hostSlot);
            if (string.IsNullOrEmpty(root))
            {
                throw new ArgumentException("A sidecar root is required.", nameof(root));
            }

            var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var candidates = FindCandidates(hostSlot, rootPath, out var complete,
                out var warningDetails);
            var entries = new List<SidecarEntry>();
            long rawBytes = 0;

            for (var i = 0; i < candidates.Count; i++)
            {
                var file = candidates[i];
                var relative = GetRelativePath(rootPath, file);
                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                    var length = info.Length;
                    if (length > MaxFileBytes)
                    {
                        SkipFile(relative, "is " + length + " bytes; the per-file limit is "
                            + MaxFileBytes + " bytes", warningDetails);
                        complete = false;
                        continue;
                    }
                    if (length < 0 || rawBytes + length > MaxAggregateRawBytes)
                    {
                        SkipFile(relative, "would exceed the aggregate raw sidecar limit of "
                            + MaxAggregateRawBytes + " bytes", warningDetails);
                        complete = false;
                        continue;
                    }

                    var data = ReadStableFile(file, (int)length);
                    if (data == null)
                    {
                        SkipFile(relative, "changed while it was being read", warningDetails);
                        complete = false;
                        continue;
                    }

                    entries.Add(new SidecarEntry(relative, data));
                    rawBytes += data.Length;
                }
                catch (Exception error)
                {
                    SkipFile(relative, "could not be read: " + error.Message, warningDetails);
                    complete = false;
                }
            }

            while (true)
            {
                var payload = SerializeEntries(entries);
                if (payload.Length <= SaveTransferStorage.MaxTransferBytes)
                {
                    var warning = BuildWarning(warningDetails);
                    if (!complete)
                    {
                        CoopPlugin.Log?.LogWarning("Sidecar session is incomplete: " + warning);
                    }
                    CoopPlugin.Log?.LogInfo("Sidecar bundle: " + entries.Count + " mod files, "
                        + rawBytes / 1024 + " KB raw, " + payload.Length / 1024 + " KB encoded"
                        + (complete ? "" : " (partial)"));
                    return new SidecarBundle(payload, complete, warning, entries.Count, rawBytes);
                }

                if (entries.Count == 0)
                {
                    throw new InvalidDataException("Sidecar bundle metadata exceeds its transfer limit.");
                }

                var removed = entries[entries.Count - 1];
                entries.RemoveAt(entries.Count - 1);
                rawBytes -= removed.Data.Length;
                SkipFile(removed.RelativePath, "would exceed the encoded sidecar bundle limit",
                    warningDetails);
                complete = false;
            }
        }

        private static List<string> FindCandidates(int hostSlot, string root, out bool complete,
            out List<string> warningDetails)
        {
            complete = true;
            warningDetails = new List<string>();
            var files = new List<string>();
            var slotPattern = new Regex("(_|Release)" + Regex.Escape(hostSlot.ToString())
                + "(_|\\.|$)", RegexOptions.CultureInvariant);
            var directories = Directory.GetDirectories(root);
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
                    AddWarning("directory '" + GetRelativePath(root, directory)
                        + "' could not be enumerated: " + error.Message, warningDetails);
                    complete = false;
                    continue;
                }

                for (var j = 0; j < directoryFiles.Length; j++)
                {
                    var file = directoryFiles[j];
                    var fileName = Path.GetFileName(file);
                    if (!slotPattern.IsMatch(fileName)
                        && !string.Equals(fileName, "enum_values.json",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (files.Count >= MaxFiles)
                    {
                        AddWarning("more than " + MaxFiles + " sidecar files were found; the rest"
                            + " were not considered", warningDetails);
                        complete = false;
                        return files;
                    }
                    files.Add(file);
                }
            }

            files.Sort(StringComparer.Ordinal);
            return files;
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

        private static byte[] SerializeEntries(List<SidecarEntry> entries)
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

            var warning = string.Join("; ", warningDetails);
            return warning.Length <= MaxWarningLength
                ? warning : warning.Substring(0, MaxWarningLength - 3) + "...";
        }

        private sealed class SidecarEntry
        {
            internal readonly string RelativePath;
            internal readonly byte[] Data;

            internal SidecarEntry(string relativePath, byte[] data)
            {
                RelativePath = relativePath;
                Data = data;
            }
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
            if (bundle == null || bundle.Length < 4 || bundle.Length > SaveTransferStorage.MaxTransferBytes)
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

        public static void ApplyBundle(byte[] bundle, int hostSlot, int clientSlot)
        {
            ApplyBundle(bundle, hostSlot, clientSlot, Application.persistentDataPath);
        }

        private static void ApplyBundle(byte[] bundle, int hostSlot, int clientSlot, string root)
        {
            ValidateSlot(hostSlot);
            ValidateSlot(clientSlot);
            if (bundle == null || bundle.Length < 4 || bundle.Length > SaveTransferStorage.MaxTransferBytes)
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
                    if (string.Equals(name, "enum_values.json", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!File.Exists(originalPath))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(originalPath));
                            AtomicWrite(originalPath, data);
                            applied++;
                        }
                        else if (!BytesEqual(File.ReadAllBytes(originalPath), data))
                        {
                            skipped++;
                            CoopPlugin.Log.LogWarning("enum_values.json differs from the host's; modded item IDs may not line up.");
                        }
                        continue;
                    }

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
            if (slot < 0 || slot > 99)
            {
                throw new ArgumentOutOfRangeException(nameof(slot));
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
            full = Path.GetFullPath(Path.Combine(rootPath, relative.Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar)));
            return full.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)
                && full.Length > rootPath.Length;
        }

        private static void RejectUnsafe(string relative)
        {
            CoopPlugin.Log.LogWarning("sidecar: rejecting unsafe path '" + relative + "'");
        }

        private static bool BytesEqual(byte[] first, byte[] second)
        {
            if (first == null || second == null || first.Length != second.Length)
            {
                return false;
            }
            for (var i = 0; i < first.Length; i++)
            {
                if (first[i] != second[i])
                {
                    return false;
                }
            }
            return true;
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
