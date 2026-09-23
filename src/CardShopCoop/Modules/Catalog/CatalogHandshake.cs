using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Catalog
{
    /// <summary>Catalog identity fields that Core appends to Hello.</summary>
    internal sealed class CatalogHelloData
    {
        internal string EnumHash;
        internal string CardsHash;
        internal List<string> CardsList;
        internal byte[] EnumBlob;
    }

    /// <summary>A bounded, decoded registry/card blob. Invalid input retains an empty line list
    /// for safe pre-translation behavior, but IsValid keeps it distinct from valid empty content.</summary>
    internal sealed class CatalogBlobData
    {
        internal readonly List<string> Lines;
        internal readonly bool IsValid;
        internal readonly string FailureReason;

        internal CatalogBlobData(List<string> lines, bool isValid, string failureReason = null)
        {
            Lines = lines ?? new List<string>();
            IsValid = isValid;
            FailureReason = failureReason;
        }
    }

    /// <summary>Host registry blobs carried by Welcome. They are captured on the main thread
    /// before Core starts its worker transfer.</summary>
    internal sealed class CatalogWelcomeBlobs
    {
        internal byte[] EnumBlob;
        internal byte[] CardsBlob;
    }

    /// <summary>Result of the enum portion of the pre-auth parity check.</summary>
    internal sealed class CatalogEnumValidation
    {
        internal List<string> Conflicts;
        internal bool RegistryFileMatchesRuntime;
        internal bool HashesAreConsistent;
        internal string HashFailureReason;
    }

    /// <summary>A registry payload prepared for a rejected peer.</summary>
    internal sealed class CatalogEnumSyncOffer
    {
        internal byte[] Payload;
        internal string FailureReason;

        internal bool ShouldSend => Payload != null && Payload.Length > 0;
    }

    /// <summary>
    /// Catalog-owned handshake helpers. Generic plugin-set parity remains in Core; this boundary
    /// owns only the enum registry and CreateCards/CardForge identity spaces.
    /// </summary>
    internal static class CatalogHandshake
    {
        /// <summary>The decompressed text ceiling for Hello and Welcome catalog blobs.</summary>
        internal const int EnumBlobCap = 256 * 1024;

        internal static CatalogHelloData BuildHelloData()
        {
            var enumLines = CanonicalLines(SafeLines(CatalogParity.EnumLines));
            var cards = CatalogParity.CardsList();
            return new CatalogHelloData
            {
                EnumHash = CatalogParity.EnumHashForLines(enumLines),
                CardsHash = CatalogParity.CardsHashForLines(cards),
                CardsList = cards,
                EnumBlob = GzipLines(enumLines),
            };
        }

        internal static CatalogWelcomeBlobs BuildWelcomeBlobs()
        {
            var enumLines = CanonicalLines(SafeLines(CatalogParity.EnumLines));
            var cards = CatalogParity.CardsList();
            return new CatalogWelcomeBlobs
            {
                EnumBlob = GzipLines(enumLines),
                CardsBlob = GzipLines(cards),
            };
        }

        /// <summary>Read and gzip the registry file for one handshake response.</summary>
        internal static bool TryBuildEnumSyncPayload(out byte[] payload, out string failureReason)
        {
            payload = null;
            failureReason = null;
            var path = CatalogParity.EnumFilePath();
            try
            {
                if (!File.Exists(path))
                {
                    failureReason = "the host has no card-database file on disk to send";
                    return false;
                }

                var length = new FileInfo(path).Length;
                if (length > EnumBlobCap)
                {
                    failureReason = "the host card-database file exceeds the " + EnumBlobCap
                        + "-byte resource cap";
                    CoopPlugin.Log.LogWarning("enum sync send: " + failureReason);
                    return false;
                }

                var bytes = File.ReadAllBytes(path);
                if (!CatalogParity.TryParseEnumRegistry(bytes, out _, out var registryFailure))
                {
                    failureReason = "the host card-database file is not a valid EPL registry: "
                        + registryFailure;
                    CoopPlugin.Log.LogWarning("enum sync send: " + failureReason);
                    return false;
                }

                payload = Msg.Gzip(bytes);
                return true;
            }
            catch (Exception error)
            {
                failureReason = error.Message;
                CoopPlugin.Log.LogWarning("enum sync send: " + error.Message);
                return false;
            }
        }

        internal static string ApplyEnumSync(byte[] hostBytes)
            => CatalogParity.InstallEnumFile(hostBytes);

        /// <summary>Decode either the enum or custom-card blob. A valid gzip containing no text is
        /// legitimate empty content; missing, malformed, or over-cap input is explicitly invalid
        /// instead of being silently converted into an empty catalog.</summary>
        internal static CatalogBlobData ReadBlob(byte[] gz)
        {
            var lines = new List<string>();
            if (gz == null || gz.Length == 0)
            {
                return new CatalogBlobData(lines, false, "catalog gzip payload is empty");
            }

            if (!TryGunzipCapped(gz, out var raw, out var failureReason))
            {
                return new CatalogBlobData(lines, false, failureReason);
            }

            try
            {
                var text = StrictUtf8.GetString(raw);
                foreach (var line in text.Split('\n'))
                {
                    var value = line.Trim();
                    if (value.Length > 0)
                    {
                        lines.Add(value);
                    }
                }
                return new CatalogBlobData(CanonicalLines(lines), true);
            }
            catch (Exception error)
            {
                var reason = "catalog blob is not valid UTF-8: " + error.Message;
                CoopPlugin.Log.LogWarning(reason);
                return new CatalogBlobData(lines, false, reason);
            }
        }

        internal static CatalogEnumValidation ValidateEnums(string peerName, string peerHash,
            List<string> peerLines)
        {
            var canonicalPeerLines = CanonicalLines(peerLines);
            var localLines = CanonicalLines(SafeLines(CatalogParity.EnumLines));
            var expectedPeerHash = CatalogParity.EnumHashForLines(canonicalPeerLines);
            var expectedLocalHash = CatalogParity.EnumHashForLines(localLines);
            // The peer hash authenticates the peer's decoded input and the local hash checks the
            // host snapshot. Do not require the two full sets to be equal: CatalogIdMap supports
            // one-sided content packs, while EnumConflicts below rejects shared names with
            // incompatible IDs.
            var hashesAreConsistent = string.Equals(peerHash, expectedPeerHash,
                StringComparison.Ordinal);
            string hashFailureReason = null;
            if (!hashesAreConsistent)
            {
                hashFailureReason = "client EnumHash does not match its decoded EnumBlob"
                    + " (reported " + (peerHash ?? "<missing>") + ", decoded "
                    + expectedPeerHash + ")";
            }
            else if (!AreEnumLinesWellFormed(canonicalPeerLines))
            {
                hashesAreConsistent = false;
                hashFailureReason = "client EnumBlob contains a malformed enum identity line";
            }
            else if (!string.Equals(CatalogParity.EnumHash(), expectedLocalHash,
                StringComparison.Ordinal))
            {
                hashesAreConsistent = false;
                hashFailureReason = "host enum identity changed while the handshake was being built"
                    + " (expected " + expectedLocalHash + ", reported " + CatalogParity.EnumHash() + ")";
            }

            if (localLines.Count == 0)
            {
                CoopPlugin.Log.LogWarning("enum check: the host has NO modded enum ids to compare against, so "
                    + (peerName ?? "") + " was not ID-checked at all (expected on a vanilla host; on a modded one see the 'enum identity source' line at startup)");
            }

            return new CatalogEnumValidation
            {
                Conflicts = EnumConflicts(canonicalPeerLines, localLines),
                RegistryFileMatchesRuntime = CatalogParity.RegistryFileMatchesRuntime(),
                HashesAreConsistent = hashesAreConsistent,
                HashFailureReason = hashFailureReason,
            };
        }

        internal static string DescribeRuntimeConflict(List<string> conflicts)
            => "your custom-card database conflicts with the host's, and the host's card-database FILE was changed this session so it no longer matches what the host is running - the HOST has to RESTART the game before it can be auto-synced to you (conflicting: "
                + DescribeConflicts(conflicts ?? new List<string>()) + ")";

        /// <summary>Find only same-name/different-id conflicts. One-sided content remains legal;
        /// an empty side therefore has no conflicts.</summary>
        internal static List<string> EnumConflicts(List<string> theirs, List<string> ours)
        {
            var found = new List<string>();
            if (theirs == null || theirs.Count == 0 || ours == null || ours.Count == 0)
            {
                return found;
            }

            var theirMap = EnumMap(theirs);
            var ourMap = EnumMap(ours);
            foreach (var kv in theirMap)
            {
                if (ourMap.TryGetValue(kv.Key, out var ourId) && ourId != kv.Value)
                {
                    found.Add($"{kv.Key} -> yours {kv.Value}, host {ourId}");
                }
            }
            found.Sort(StringComparer.Ordinal);
            return found;
        }

        internal static CatalogEnumSyncOffer PrepareEnumSync()
        {
            var offer = new CatalogEnumSyncOffer();

            if (!TryBuildEnumSyncPayload(out var payload, out var failureReason))
            {
                offer.FailureReason = failureReason;
                return offer;
            }

            offer.Payload = payload;
            return offer;
        }

        internal static string DescribeEnumConflict(List<string> conflicts, CatalogEnumSyncOffer offer)
        {
            var why = DescribeConflicts(conflicts ?? new List<string>());
            if (offer != null && offer.ShouldSend)
            {
                return "your custom-card database conflicts with the host's - the host's copy has just been sent to you, and (unless you switched auto-sync off) saved on your PC with your old file backed up first. Now QUIT THE GAME TO DESKTOP, start it again, then join: the ids are only read while the game is booting, so nothing changes until you do (conflicting: "
                    + why + ")";
            }

            return "your custom-card database conflicts with the host's, and the host could not send its card database"
                + (offer != null && offer.FailureReason != null ? " (" + offer.FailureReason + ")" : "")
                + " - ask the host to check that AppData\\LocalLow\\OPNeonGames\\Card Shop Simulator\\PrefabLoader\\enum_values.json exists and is readable, or copy it across by hand (conflicting: "
                + why + ")";
        }

        internal static bool TryValidateCards(string peerHash, List<string> peerCards,
            out string reason)
        {
            reason = null;
            if (!CatalogParity.TryCanonicalizeCardLines(peerCards, out var canonicalPeerCards,
                out var peerFailure))
            {
                reason = "invalid CardForge card list: " + peerFailure;
                return false;
            }

            List<string> localCards;
            try
            {
                localCards = CatalogParity.CardsList();
            }
            catch (Exception error)
            {
                reason = "the host has an invalid installed CardForge configuration: "
                    + error.Message;
                CoopPlugin.Log.LogError("catalog card validation failed: " + reason);
                return false;
            }

            var expectedPeerHash = CatalogParity.CardsHashForLines(canonicalPeerCards);
            if (!string.Equals(peerHash, expectedPeerHash, StringComparison.Ordinal))
            {
                reason = "catalog card hash is inconsistent with the client's advertised card list"
                    + " (reported " + (peerHash ?? "<missing>") + ", decoded "
                    + expectedPeerHash + ")";
                return false;
            }

            var expectedLocalHash = CatalogParity.CardsHashForLines(localCards);
            if (string.Equals(peerHash, expectedLocalHash, StringComparison.Ordinal)
                && CardListsEqual(canonicalPeerCards, localCards))
            {
                return true;
            }

            var detail = DescribeCardsDiff(canonicalPeerCards, localCards);
            reason = detail
                ?? "your custom cards differ from the host's - both players need the same custom cards installed (identical files + IDs), then restart. Share the exact card package (e.g. from CardForge).";
            return false;
        }

        private static bool CardListsEqual(List<string> left, List<string> right)
        {
            if (left == null || right == null || left.Count != right.Count)
            {
                return false;
            }

            for (var i = 0; i < left.Count; i++)
            {
                if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        internal static string DescribeConflicts(List<string> conflicts)
        {
            const int max = 5;
            var count = Math.Min(conflicts.Count, max);
            var value = string.Join("; ", conflicts.GetRange(0, count).ToArray());
            if (conflicts.Count > count)
            {
                value += $" (+{conflicts.Count - count} more)";
            }

            if (value.Length > 400)
            {
                value = value.Substring(0, 397) + "...";
            }

            return value;
        }

        internal static bool TryGunzipCapped(byte[] data, out byte[] result,
            out string failureReason)
        {
            result = null;
            failureReason = null;
            if (data == null || data.Length == 0)
            {
                failureReason = "catalog gzip payload is empty";
                return false;
            }

            try
            {
                using (var source = new MemoryStream(data, writable: false))
                using (var gzip = new System.IO.Compression.GZipStream(source,
                    System.IO.Compression.CompressionMode.Decompress))
                using (var destination = new MemoryStream())
                {
                    var buffer = new byte[8192];
                    int count;
                    while ((count = gzip.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (destination.Length + count > EnumBlobCap)
                        {
                            failureReason = "catalog gzip expands beyond the " + EnumBlobCap
                                + "-byte resource cap";
                            CoopPlugin.Log.LogWarning("catalog blob rejected: " + failureReason
                                + " (" + data.Length + " compressed bytes)");
                            return false;
                        }

                        destination.Write(buffer, 0, count);
                    }

                    result = destination.ToArray();
                    return true;
                }
            }
            catch (Exception error)
            {
                failureReason = "catalog gzip could not be decompressed: " + error.Message;
                CoopPlugin.Log.LogWarning("catalog blob rejected: " + failureReason);
                return false;
            }
        }

        private static byte[] GzipLines(List<string> lines)
        {
            try
            {
                var values = (lines ?? new List<string>()).ToArray();
                var raw = Encoding.UTF8.GetBytes(string.Join("\n", values));
                if (raw.Length > EnumBlobCap)
                {
                    CoopPlugin.Log.LogWarning("registry blob is OVER THE WIRE CAP: " + values.Length + " ids, "
                        + raw.Length + " bytes uncompressed vs a " + EnumBlobCap
                        + "-byte cap - the other PC will IGNORE it and modded ids will not be translated this session (ids must already match)");
                }

                return Msg.Gzip(raw);
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("registry blob: " + error.Message);
                return Msg.Gzip(new byte[0]);
            }
        }

        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private static List<string> CanonicalLines(List<string> source)
        {
            var result = new List<string>();
            if (source != null)
            {
                foreach (var line in source)
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

        private static bool AreEnumLinesWellFormed(List<string> lines)
        {
            foreach (var line in lines)
            {
                var colon = line.IndexOf(':');
                var equals = line.LastIndexOf('=');
                if (colon <= 0 || equals <= colon + 1 || equals == line.Length - 1
                    || !long.TryParse(line.Substring(equals + 1), out _))
                {
                    return false;
                }
            }

            return true;
        }

        private static List<string> SafeLines(Func<List<string>> factory)
        {
            try
            {
                return factory() ?? new List<string>();
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("catalog parity lines: " + error.Message);
                return new List<string>();
            }
        }

        private static Dictionary<string, string> EnumMap(List<string> lines)
        {
            var result = new Dictionary<string, string>();
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                var equals = line.LastIndexOf('=');
                if (equals <= 0 || equals == line.Length - 1)
                {
                    continue;
                }

                result[line.Substring(0, equals)] = line.Substring(equals + 1);
            }

            return result;
        }

        private static string DescribeCardsDiff(List<string> theirs, List<string> ours)
        {
            return DescribeDiff(theirs, ours, "custom cards differ - ", "ID differs");
        }

        private static string DescribeDiff(List<string> theirs, List<string> ours,
            string head, string diffLabel)
        {
            if (theirs == null || theirs.Count == 0 || ours == null || ours.Count == 0)
            {
                return null;
            }

            var theirMap = DiffMap(theirs);
            var ourMap = DiffMap(ours);
            var missing = new List<string>();
            var extra = new List<string>();
            var valueDiff = new List<string>();
            foreach (var pair in ourMap)
            {
                if (!theirMap.ContainsKey(pair.Key))
                {
                    missing.Add(pair.Key);
                }
            }

            foreach (var pair in theirMap)
            {
                if (!ourMap.TryGetValue(pair.Key, out var ourValue))
                {
                    extra.Add(pair.Key);
                }
                else if (ourValue != pair.Value)
                {
                    valueDiff.Add($"{pair.Key} (host {ourValue} vs yours {pair.Value})");
                }
            }

            if (missing.Count == 0 && extra.Count == 0 && valueDiff.Count == 0)
            {
                return null;
            }

            missing.Sort(StringComparer.Ordinal);
            extra.Sort(StringComparer.Ordinal);
            valueDiff.Sort(StringComparer.Ordinal);
            var parts = new List<string>();
            if (missing.Count > 0)
            {
                parts.Add("you are missing: " + JoinCapped(missing));
            }
            if (extra.Count > 0)
            {
                parts.Add("you have extra: " + JoinCapped(extra));
            }
            if (valueDiff.Count > 0)
            {
                parts.Add(diffLabel + ": " + JoinCapped(valueDiff));
            }

            var full = head + string.Join(" | ", parts);
            return full.Length > 700 ? full.Substring(0, 697) + "..." : full;
        }

        private static Dictionary<string, string> DiffMap(List<string> entries)
        {
            var result = new Dictionary<string, string>();
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry))
                {
                    continue;
                }

                var equals = entry.IndexOf('=');
                var key = equals > 0 ? entry.Substring(0, equals) : entry;
                var value = equals > 0 ? entry.Substring(equals + 1) : "";
                result[key] = value;
            }

            return result;
        }

        private static string JoinCapped(List<string> items)
        {
            var builder = new StringBuilder();
            var shown = 0;
            for (var i = 0; i < items.Count; i++)
            {
                var next = (shown > 0 ? ", " : "") + items[i];
                if (shown > 0 && builder.Length + next.Length > 220)
                {
                    break;
                }

                builder.Append(next);
                shown++;
            }
            if (shown < items.Count)
            {
                builder.Append($" (+{items.Count - shown} more)");
            }

            return builder.ToString();
        }
    }
}
