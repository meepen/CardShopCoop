using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;

namespace CardShopCoop.Modules.Catalog
{
    /// <summary>Catalog identity fields that Core appends to Hello.</summary>
    internal sealed class CatalogHelloData
    {
        internal string EnumHash;
        internal string CardsHash;
        internal List<string> CardsList;
        internal List<EnumKindIdentityDto> EnumIdentity;
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
        internal List<EnumKindIdentityDto> EnumIdentity;
        internal byte[] CardsBlob;
    }

    /// <summary>Result of the enum portion of the pre-auth parity check.</summary>
    internal sealed class CatalogEnumValidation
    {
        internal List<string> Conflicts;
        internal bool HashesAreConsistent;
        internal string HashFailureReason;
    }

    /// <summary>
    /// Catalog-owned handshake helpers. Generic plugin-set parity remains in Core; this boundary
    /// owns only the enum registry and CreateCards/CardForge identity spaces.
    /// </summary>
    internal static class CatalogHandshake
    {
        /// <summary>The decompressed text ceiling for Hello and Welcome catalog blobs.</summary>
        internal const int CatalogBlobCap = 256 * 1024;

        internal static CatalogHelloData BuildHelloData()
        {
            var identity = SafeIdentity();
            var cards = CatalogParity.CardsList();
            return new CatalogHelloData
            {
                EnumHash = CatalogParity.EnumHashForIdentity(identity),
                CardsHash = CatalogParity.CardsHashForLines(cards),
                CardsList = cards,
                EnumIdentity = identity,
            };
        }

        internal static CatalogWelcomeBlobs BuildWelcomeBlobs()
        {
            var identity = SafeIdentity();
            var cards = CatalogParity.CardsList();
            return new CatalogWelcomeBlobs
            {
                EnumIdentity = identity,
                CardsBlob = GzipLines(cards),
            };
        }

        /// <summary>The typed enum identity, never throwing into the handshake. An empty identity
        /// is legitimate (no enum types resolved) and is treated as such by the host.</summary>
        private static List<EnumKindIdentityDto> SafeIdentity()
        {
            try
            {
                return CatalogParity.EnumIdentity() ?? new List<EnumKindIdentityDto>();
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("catalog enum identity: " + error.Message);
                return new List<EnumKindIdentityDto>();
            }
        }

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
            List<EnumKindIdentityDto> peerIdentity)
        {
            var localIdentity = SafeIdentity();
            var expectedPeerHash = CatalogParity.EnumHashForIdentity(peerIdentity);
            var expectedLocalHash = CatalogParity.EnumHashForIdentity(localIdentity);
            // The peer hash authenticates the peer's identity and the local hash checks the host
            // snapshot. We do not require the two full sets to be equal: EnumKeyConflicts below
            // compares the NAME sets only, since ids can differ between two installs and are
            // translated by the value map on the wire and in the save.
            var hashesAreConsistent = string.Equals(peerHash, expectedPeerHash,
                StringComparison.Ordinal);
            string hashFailureReason = null;
            if (!hashesAreConsistent)
            {
                hashFailureReason = "client EnumHash does not match its decoded enum identity"
                    + " (reported " + (peerHash ?? "<missing>") + ", decoded "
                    + expectedPeerHash + ")";
            }
            else if (!string.Equals(CatalogParity.EnumHash(), expectedLocalHash,
                StringComparison.Ordinal))
            {
                hashesAreConsistent = false;
                hashFailureReason = "host enum identity changed while the handshake was being built"
                    + " (expected " + expectedLocalHash + ", reported " + CatalogParity.EnumHash() + ")";
            }

            if (MemberCount(localIdentity) == 0)
            {
                CoopPlugin.Log.LogWarning("enum check: the host resolved NO enum identity members, so "
                    + (peerName ?? "") + " was not key-checked at all (the six enum types did not "
                    + "resolve this session; see the 'enum identity source' line at startup)");
            }

            return new CatalogEnumValidation
            {
                Conflicts = EnumKeyConflicts(peerIdentity, localIdentity),
                HashesAreConsistent = hashesAreConsistent,
                HashFailureReason = hashFailureReason,
            };
        }

        /// <summary>True when the identity has the shape the handshake expects: no duplicate
        /// kinds, every member named, no duplicate names within a kind. An EMPTY identity is
        /// allowed - the key-set comparison decides whether it is a mismatch, exactly as the
        /// previous line-based handshake did; only a null/malformed shape is rejected here.</summary>
        internal static bool IsIdentityUsable(List<EnumKindIdentityDto> identity)
        {
            if (identity == null)
            {
                return false;
            }

            if (identity.Count == 0)
            {
                return true;
            }

            var kinds = new HashSet<int>();
            foreach (var kind in identity)
            {
                if (kind == null || kind.Members == null || !kinds.Add(kind.Kind))
                {
                    return false;
                }

                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var member in kind.Members)
                {
                    if (member == null || string.IsNullOrEmpty(member.Name)
                        || !names.Add(member.Name))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static int MemberCount(List<EnumKindIdentityDto> identity)
        {
            var count = 0;
            if (identity == null)
            {
                return 0;
            }

            foreach (var kind in identity)
            {
                count += kind == null || kind.Members == null ? 0 : kind.Members.Count;
            }

            return count;
        }

        /// <summary>The peer's custom-content name set differs from ours. Ids are irrelevant - only
        /// which names exist matters, because the wire translates by name.</summary>
        internal static string DescribeEnumKeysMismatch(List<string> conflicts)
            => "your custom content does not match the host's - both players need the same custom "
                + "card/item packs installed (identical names; the numeric ids underneath may "
                + "differ). Differences: "
                + DescribeConflicts(conflicts ?? new List<string>());

        /// <summary>Find catalog KEY differences only: a member name present on one peer and not
        /// the other. The numeric ids are deliberately ignored - EPL mints them in bundle load
        /// order, so the same name can carry different ids on two peers; the wire and the save
        /// translate enum identity by name (<see cref="CatalogIdMap"/>), so differing ids are not
        /// a conflict.</summary>
        internal static List<string> EnumKeyConflicts(List<EnumKindIdentityDto> theirs,
            List<EnumKindIdentityDto> ours)
        {
            var found = new List<string>();
            var theirKeys = EnumKeySet(theirs);
            var ourKeys = EnumKeySet(ours);
            foreach (var key in theirKeys)
            {
                if (!ourKeys.Contains(key))
                {
                    found.Add("client-only " + key);
                }
            }

            foreach (var key in ourKeys)
            {
                if (!theirKeys.Contains(key))
                {
                    found.Add("host-only " + key);
                }
            }

            found.Sort(StringComparer.Ordinal);
            return found;
        }

        /// <summary>The "EnumType:MemberName" keys of one identity, for key-set comparison. The
        /// member's numeric id is intentionally not part of the key.</summary>
        private static HashSet<string> EnumKeySet(List<EnumKindIdentityDto> identity)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (identity == null)
            {
                return keys;
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
                    if (member != null && member.Name != null)
                    {
                        keys.Add(typeName + ":" + member.Name);
                    }
                }
            }

            return keys;
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
                        if (destination.Length + count > CatalogBlobCap)
                        {
                            failureReason = "catalog gzip expands beyond the " + CatalogBlobCap
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
                if (raw.Length > CatalogBlobCap)
                {
                    CoopPlugin.Log.LogWarning("enum identity blob is OVER THE WIRE CAP: " + values.Length + " lines, "
                        + raw.Length + " bytes uncompressed vs a " + CatalogBlobCap
                        + "-byte cap - the receiving PC cannot decode it and the join will be refused");
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
