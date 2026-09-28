using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace CardShopCoop.Util
{
    /// <summary>Generic loaded-plugin parity and shared optional-type lookup.</summary>
    public static class ModParity
    {
        private static string _plugins;

        /// <summary>Hash of the loaded BepInEx plugin set (guid=version, sorted).</summary>
        public static string PluginHash()
        {
            if (_plugins != null)
            {
                return _plugins;
            }

            try
            {
                _plugins = Short(Sha1(string.Join(";", PluginEntries())));
            }
            catch { _plugins = "err"; }
            return _plugins;
        }

        /// <summary>The same sorted "guid=version" entries PluginHash hashes, exposed as a
        /// list so a mismatch can name the offending mods via <see cref="DescribeMismatch"/>.</summary>
        public static List<string> PluginList()
        {
            try
            {
                return PluginEntries();
            }
            catch (Exception e) { Swallow.Log(e); return new List<string>(); }
        }

        private static List<string> PluginEntries()
        {
            var parts = new List<string>();
            foreach (var kv in BepInEx.Bootstrap.Chainloader.PluginInfos)
            {
                parts.Add(kv.Key + "=" + kv.Value.Metadata.Version);
            }

            parts.Sort(StringComparer.Ordinal);
            return parts;
        }

        /// <summary>Describe how another player's loaded plugin set differs from ours, as the
        /// bounded disconnect reason. Each side's extra mods and each shared-but-different
        /// version are listed by guid so players can fix the mismatch without guessing. The
        /// result never exceeds <paramref name="maxLength"/>; anything past the budget is
        /// replaced by a "(+N more)" tail.</summary>
        public static string DescribeMismatch(IReadOnlyList<string> peerEntries, int maxLength)
        {
            const string Prefix =
                "your mod set differs from the host's - both players need identical mods";
            if (maxLength < Prefix.Length)
            {
                maxLength = Prefix.Length;
            }

            var local = ParseEntries(PluginEntries());
            var peer = ParseEntries(peerEntries);

            var hostOnly = new List<string>();
            var clientOnly = new List<string>();
            var versionDiff = new List<string>();
            foreach (var guid in Union(local, peer))
            {
                var hasLocal = local.TryGetValue(guid, out var localVersion);
                var hasPeer = peer.TryGetValue(guid, out var peerVersion);
                if (hasLocal && hasPeer)
                {
                    if (!string.Equals(localVersion, peerVersion, StringComparison.Ordinal))
                    {
                        versionDiff.Add(guid + " (client " + peerVersion + ", host " + localVersion + ")");
                    }
                }
                else if (hasPeer)
                {
                    clientOnly.Add(guid + "=" + peerVersion);
                }
                else
                {
                    hostOnly.Add(guid + "=" + localVersion);
                }
            }

            if (hostOnly.Count == 0 && clientOnly.Count == 0 && versionDiff.Count == 0)
            {
                // Hash disagreed but the visible lists are identical: fall back to the plain
                // wording so an opaque hash difference is still explained.
                return Prefix;
            }

            var body = new StringBuilder();
            var omitted = 0;
            AppendCategory(body, "only on host", hostOnly, maxLength, ref omitted);
            AppendCategory(body, "only on client", clientOnly, maxLength, ref omitted);
            AppendCategory(body, "different version", versionDiff, maxLength, ref omitted);

            var result = body.Length == 0 ? Prefix : Prefix + ". " + body;
            if (omitted > 0)
            {
                result += " (+" + omitted + " more)";
            }

            return result.Length <= maxLength ? result : result.Substring(0, maxLength);
        }

        private static Dictionary<string, string> ParseEntries(IReadOnlyList<string> entries)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (entries == null)
            {
                return map;
            }

            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry))
                {
                    continue;
                }

                var split = entry.IndexOf('=');
                var guid = split < 0 ? entry : entry.Substring(0, split);
                var version = split < 0 ? "?" : entry.Substring(split + 1);
                // First entry wins: a malformed list with duplicates must not throw.
                if (!map.ContainsKey(guid))
                {
                    map[guid] = version;
                }
            }

            return map;
        }

        private static List<string> Union(Dictionary<string, string> local,
            Dictionary<string, string> peer)
        {
            var guids = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var guid in local.Keys)
            {
                guids.Add(guid);
            }

            foreach (var guid in peer.Keys)
            {
                guids.Add(guid);
            }

            return new List<string>(guids);
        }

        private static void AppendCategory(StringBuilder sb, string label, List<string> items,
            int maxLength, ref int omitted)
        {
            if (items.Count == 0)
            {
                return;
            }

            const int ReserveForOmittedTail = 16;
            var start = sb.Length;
            sb.Append(sb.Length == 0 ? "" : "; ").Append(label).Append(": ");
            var written = 0;
            for (var i = 0; i < items.Count; i++)
            {
                var addition = written == 0 ? items[i] : ", " + items[i];
                if (sb.Length + addition.Length + ReserveForOmittedTail > maxLength)
                {
                    omitted += items.Count - i;
                    if (written == 0)
                    {
                        // No room for even one entry: drop the dangling "label: " header.
                        sb.Length = start;
                    }

                    return;
                }

                sb.Append(addition);
                written++;
            }
        }

        /// <summary>Resolve an optional type without changing the established direct-bind then
        /// Harmony fallback behavior used by external-mod bridges.</summary>
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
            catch (Exception e) { Swallow.Log(e); }
            try
            {
                return HarmonyLib.AccessTools.TypeByName(typeName);
            }
            catch (Exception e) { Swallow.Log(e); return null; }
        }

        private static string Sha1(string value)
        {
            using (var sha = SHA1.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value)))
                    .Replace("-", "");
            }
        }

        private static string Short(string hex)
        {
            return hex.Length > 16 ? hex.Substring(0, 16) : hex;
        }
    }
}
