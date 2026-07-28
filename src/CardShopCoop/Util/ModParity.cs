using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Util
{
    /// <summary>
    /// Everything in this mod syncs by game IDs, which is only safe when both players run
    /// the SAME mod set (content mods define the ID space). These hashes are exchanged in
    /// the Hello handshake; mismatches are rejected with a readable reason instead of
    /// silently corrupting the shared world.
    /// </summary>
    public static class ModParity
    {
        private static string _plugins;
        private static string _enum;
        private static string _cards;

        /// <summary>Set once we have rewritten the on-disk enum registry (a host sync installed,
        /// or the guest's own file restored). The bytes on disk are now something the RUNNING
        /// game has never read - EPL loaded its ids at prepatch, long before either write - so
        /// this process is permanently out of step with its own registry and must not join
        /// anything until it restarts. CoopCore reads this by name to refuse a join with a
        /// "restart first" reason instead of letting the player retry into a silent ID desync.
        /// Never cleared: nothing short of a new process can make it false again.</summary>
        public static bool RestartRequired;

        /// <summary>Hash of the custom-card ID space minted by CreateCards: each
        /// MonsterConfig's "Monster Type = Monster Type ID" mapping, sorted. This is
        /// exactly what WriteCard/ReadCard sync depends on. The plugin and enum hashes
        /// do NOT cover it - custom EMonsterType cards aren't in EPL's enum_values.json -
        /// so without this, two players could pass the handshake and then silently show
        /// the wrong card. Cosmetic fields (art, stats, description) are excluded on
        /// purpose so a shared card with tweaked flavor still matches.</summary>
        public static string CardsHash()
        {
            if (_cards != null) return _cards;
            try
            {
                var entries = CardEntries();
                _cards = entries.Count == 0 ? "none" : Short(Sha1(string.Join(";", entries)));
            }
            catch { _cards = "err"; }
            return _cards;
        }

        /// <summary>The exact sorted "Monster Type=Monster Type ID" identity strings CardsHash
        /// hashes, exposed as a list so the handshake can SHOW the mismatch, not just reject it.
        /// Both come from CardEntries() so the list a player sees can never disagree with the
        /// hash that gated them. CoopCore calls this by name.</summary>
        public static List<string> CardsList()
        {
            try { return CardEntries(); }
            catch { return new List<string>(); }
        }

        /// <summary>The .ini-derived custom-card identity strings, sorted - the single source
        /// both CardsHash and CardsList read so hash and list are always in step.</summary>
        private static List<string> CardEntries()
        {
            string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "patchers", "CreateCardsPreloader", "MonsterConfigs");
            var entries = new List<string>();
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.GetFiles(dir, "*.ini"))
                {
                    string name = null, id = null;
                    foreach (var line in File.ReadAllLines(f))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim();
                        if (k.Equals("Monster Type", StringComparison.OrdinalIgnoreCase)) name = line.Substring(eq + 1).Trim();
                        else if (k.Equals("Monster Type ID", StringComparison.OrdinalIgnoreCase)) id = line.Substring(eq + 1).Trim();
                    }
                    if (name != null && id != null) entries.Add(name + "=" + id);
                }
            }
            entries.Sort(StringComparer.Ordinal);
            return entries;
        }

        /// <summary>Hash of the loaded BepInEx plugin set (guid=version, sorted).</summary>
        public static string PluginHash()
        {
            if (_plugins != null) return _plugins;
            try { _plugins = Short(Sha1(string.Join(";", PluginEntries()))); }
            catch { _plugins = "err"; }
            return _plugins;
        }

        /// <summary>The same sorted "guid=version" entries PluginHash hashes, exposed as a list
        /// so a mismatch can be shown side-by-side instead of just rejected. Shares PluginEntries()
        /// with PluginHash so the two never disagree. CoopCore calls this by name.</summary>
        public static List<string> PluginList()
        {
            try { return PluginEntries(); }
            catch { return new List<string>(); }
        }

        /// <summary>The loaded plugin set as sorted "guid=version" strings - the single source
        /// both PluginHash and PluginList read.</summary>
        private static List<string> PluginEntries()
        {
            var parts = new List<string>();
            foreach (var kv in BepInEx.Bootstrap.Chainloader.PluginInfos)
                parts.Add(kv.Key + "=" + kv.Value.Metadata.Version);
            parts.Sort(StringComparer.Ordinal);
            return parts;
        }

        /// <summary>EPL's custom-item ID registry on disk. Nothing about our IDENTITY reads this
        /// file any more (EnumHash/EnumLines walk the enums THIS PROCESS actually loaded) - it
        /// survives only as the payload we ship to a guest and write over theirs.
        /// THE INVARIANT, and the reason for that move: EPL regenerates enum_values.json at
        /// PREPATCH, on every boot, from whatever content is installed locally, minting ids in
        /// discovery order. So (a) comparing or installing the FILE can never converge while the
        /// two players' content differs - each boot re-mints it - and (b) the file on disk stops
        /// describing the running game the instant anyone writes to it. Hence: a write here MUST
        /// NOT change our hash mid-session; only a RESTART loads new ids. That is exactly what
        /// the 1.0.33 "_enum = null" invalidations broke - they let an unrestarted guest re-Hello
        /// on the strength of bytes the running game had never read, quietly defeating the
        /// documented restart requirement. Both are gone; see RestartRequired.</summary>
        public static string EnumFilePath()
        {
            return Path.Combine(Application.persistentDataPath, "PrefabLoader", "enum_values.json");
        }

        /// <summary>The enum types EPL mints custom ids into - exactly the six sections a live
        /// enum_values.json carries, all global-namespace enums in the decompiled Assembly-CSharp.
        /// Resolved BY NAME at runtime (the mod's usual AccessTools idiom) so a game update that
        /// renames or drops one costs us that one section instead of throwing a TypeLoadException
        /// straight through the handshake.</summary>
        private static readonly string[] ModdedEnumTypeNames =
        {
            "EObjectType", "EDecoObject", "EItemType",
            "ECardExpansionType", "ERarity", "ECollectionPackType"
        };

        /// <summary>First id EPL hands out. Vanilla members are a dense 0..~135 per enum (not one
        /// explicit value in any of the six decompiled enums), so everything from here up is
        /// modded content - the only slice of the ID space that can differ between two players.</summary>
        private const long ModdedIdFloor = 200000;

        /// <summary>Hash of the modded ID space THIS PROCESS is actually running. We ask the
        /// loaded enum types what ids they hold rather than reading enum_values.json, because the
        /// file is not the truth: EPL re-mints it at prepatch every boot (see EnumFilePath), so
        /// file-based comparison chased a moving target and file-based installation could never
        /// converge. The runtime walk describes what our game will really do with an incoming
        /// ID - and it cannot be faked by dropping a matching file in place, because nothing
        /// short of a restart changes it. Falls back to the old canonical FILE parse only when
        /// the walk finds nothing modded at all, and still ends at "none" on a clean vanilla
        /// machine: the Hello check treats "none" as "unmodded, don't gate on it".</summary>
        public static string EnumHash()
        {
            if (_enum != null) return _enum;
            try
            {
                var lines = RuntimeEnumEntries();
                if (lines.Count > 0) { _enum = Short(Sha1(string.Join("\n", lines))); return _enum; }
                // Zero modded ids in memory. Either the machine is vanilla ("none", as before),
                // or the walk came up empty against a registry that does exist - in which case
                // keep the pre-existing file behavior so this is never WORSE than 1.0.33. Hash
                // the registry's MEANING, not its bytes: EPL re-serializes the file on every
                // boot (ordering / whitespace churn), and a byte hash made two LOGICALLY
                // IDENTICAL registries mismatch forever - the endless "synced - RESTART -
                // rejoin" loop (field report: "custom card database error, regardless of
                // multiple restarts, via LAN"). Byte hash remains the last resort if the format
                // ever changes under the parser.
                string p = EnumFilePath();
                if (!File.Exists(p)) { _enum = "none"; return _enum; }
                var fileLines = CanonicalEnumLines(File.ReadAllText(p));
                _enum = fileLines != null && fileLines.Count > 0
                    ? Short(Sha1(string.Join("\n", fileLines)))
                    : Short(Sha1Bytes(File.ReadAllBytes(p)));
            }
            catch { _enum = "none"; }
            return _enum;
        }

        /// <summary>The exact sorted "EnumType:Name=id" lines EnumHash hashes, exposed as a list
        /// so a mismatch can be SHOWN (which custom item, whose id) instead of only rejected -
        /// the same pairing PluginHash/PluginList and CardsHash/CardsList already use. Both read
        /// RuntimeEnumEntries(), so the list a player is shown can never disagree with the hash
        /// that gated them. Empty when nothing modded is loaded - including the vanilla "none"
        /// case and the file-fallback branch above, where there are no runtime lines to name and
        /// the caller's generic wording is the honest answer. CoopCore calls this by name.</summary>
        public static List<string> EnumLines()
        {
            try { return RuntimeEnumEntries(); }
            catch { return new List<string>(); }
        }

        /// <summary>The modded enum ids resolved from the LOADED types, sorted - the single source
        /// both EnumHash and EnumLines read. EPL injects its minted members into these enums at
        /// prepatch, so Enum.GetNames/GetValues report them for real; anything below
        /// ModdedIdFloor is vanilla and identical for everyone, so it is left out. A type that
        /// won't resolve is skipped, not fatal: a partial answer still beats no handshake.</summary>
        private static List<string> RuntimeEnumEntries()
        {
            var lines = new List<string>();
            foreach (var typeName in ModdedEnumTypeNames)
            {
                try
                {
                    var t = AccessTools.TypeByName(typeName);
                    if (t == null || !t.IsEnum) continue;
                    // GetNames and GetValues are documented to run in the same (binary-value)
                    // order, so index i is one member - walking them in parallel keeps aliases
                    // (two names, one id) as the two distinct lines the registry file shows.
                    var names = Enum.GetNames(t);
                    var values = Enum.GetValues(t);
                    int n = Math.Min(names.Length, values.Length);
                    for (int i = 0; i < n; i++)
                    {
                        long id;
                        try { id = Convert.ToInt64(values.GetValue(i)); }
                        catch { continue; }
                        if (id < ModdedIdFloor) continue;
                        lines.Add(t.Name + ":" + names[i] + "=" + id);
                    }
                }
                catch (Exception e) { CoopPlugin.Log.LogWarning("enum walk (" + typeName + "): " + e.Message); }
            }
            lines.Sort(StringComparer.Ordinal);
            return lines;
        }

        /// <summary>Parse EPL's two-level registry ({ "EnumType": { "Name": id, ... }, ... })
        /// into sorted "EnumType:Name=id" lines. The file is machine-written with exactly this
        /// shape (integer leaves, no nested objects inside a section), so a compact regex walk
        /// is safe; returns null on anything surprising so the caller can fall back.</summary>
        private static List<string> CanonicalEnumLines(string json)
        {
            try
            {
                var outLines = new List<string>();
                // sections: "Name": { ...no nested braces (leaves are ints)... }
                var secRx = new System.Text.RegularExpressions.Regex(
                    "\"([^\"]+)\"\\s*:\\s*\\{([^{}]*)\\}",
                    System.Text.RegularExpressions.RegexOptions.Singleline);
                var pairRx = new System.Text.RegularExpressions.Regex(
                    "\"([^\"]+)\"\\s*:\\s*(-?\\d+)");
                foreach (System.Text.RegularExpressions.Match sec in secRx.Matches(json))
                {
                    string section = sec.Groups[1].Value;
                    foreach (System.Text.RegularExpressions.Match pr in pairRx.Matches(sec.Groups[2].Value))
                        outLines.Add(section + ":" + pr.Groups[1].Value + "=" + pr.Groups[2].Value);
                }
                if (outLines.Count == 0) return null; // nothing recognizable: let the byte hash decide
                outLines.Sort(StringComparer.Ordinal);
                return outLines;
            }
            catch { return null; }
        }

        /// <summary>Install the host's registry over ours, keeping timestamped backups
        /// (the newest 3). Returns a user-facing status line, and raises RestartRequired when
        /// bytes actually landed - the "already synced" early-out does NOT raise it, because
        /// nothing was written and this process is still in step with its own file.</summary>
        public static string InstallEnumFile(byte[] hostBytes)
        {
            string p = EnumFilePath();
            try
            {
                string bak = null;
                if (File.Exists(p))
                {
                    var current = File.ReadAllBytes(p);
                    // "already synced" must be a CANONICAL comparison, not bytes: EPL
                    // rewrites the file at boot, so a byte compare told the user "already
                    // synced - RESTART" on every attempt while the (equally byte-bound)
                    // host hash kept rejecting - the two halves of the endless loop. With
                    // canonical hashing on both sides this branch should rarely fire at
                    // all; when it does, the registries genuinely agree and the user's
                    // next join will pass.
                    var curLines = CanonicalEnumLines(System.Text.Encoding.UTF8.GetString(current));
                    var newLines = CanonicalEnumLines(System.Text.Encoding.UTF8.GetString(hostBytes));
                    bool same = curLines != null && newLines != null
                        ? string.Join("\n", curLines) == string.Join("\n", newLines)
                        : SameBytes(current, hostBytes);
                    if (same)
                        return "card database already synced - RESTART the game, then join again";
                    bak = p + ".coopbak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    File.Copy(p, bak, overwrite: true);
                    PruneBackups(p);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(p));
                }
                File.WriteAllBytes(p, hostBytes);
                // Deliberately NO cache invalidation here (see EnumFilePath): our identity is
                // what this process loaded at prepatch, and writing the file changed none of it.
                // The 1.0.33 "_enum = null" re-read these fresh bytes and handed the guest a
                // hash matching the host while the running game still held the old ids - a pass
                // through the very gate the restart requirement exists to close. The flag below
                // is the honest version of that signal.
                RestartRequired = true;
                // Drop a marker so the mod KNOWS the machine-global registry is now the HOST's,
                // not the guest's own. Without a restore path a mismatched-enum join used to
                // silently brick every modded SOLO save ("data lost") until the file was fixed
                // by hand; HostEnumInstalled() reads this marker to offer a one-click restore,
                // RestoreEnumBackup() clears it. We record the backup just made so the human (and
                // the restore) can find the guest's own file.
                WriteEnumMarker(bak);
                return "card database synced from host (your old file was backed up) - RESTART the game, then join again";
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("enum sync failed: " + e.Message);
                return "could not update the card database automatically - copy the host's enum_values.json manually (see mod page)";
            }
        }

        /// <summary>Marker written beside enum_values.json while the host's registry is on loan
        /// in place of the guest's own. Its mere existence is the signal that a restore is owed.</summary>
        private static string EnumMarkerPath()
        {
            return EnumFilePath() + ".hostlend";
        }

        private static void WriteEnumMarker(string newestBackup)
        {
            try
            {
                string content =
                    (newestBackup != null ? Path.GetFileName(newestBackup) : "(no prior registry - none to back up)")
                    + Environment.NewLine + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                File.WriteAllText(EnumMarkerPath(), content);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("enum marker write failed: " + e.Message); }
        }

        /// <summary>True while the host's registry is installed over the guest's own (the marker
        /// exists). Cheap File.Exists on purpose - it's polled rarely (a UI prompt), so there's no
        /// caching to go stale after a restore. CoopCore reads this to offer the restore action.</summary>
        public static bool HostEnumInstalled()
        {
            try { return File.Exists(EnumMarkerPath()); }
            catch { return false; }
        }

        /// <summary>Undo a host-enum lend: put the guest's OWN registry back so their modded solo
        /// saves load again. Finds the newest .coopbak-* (the file we set aside at install time),
        /// first copies the CURRENT (host's) file to .hostcopy so nothing is ever destroyed, then
        /// restores the backup over enum_values.json and clears the marker. The game reads the
        /// registry once at startup, so <paramref name="message"/> tells the user to restart
        /// before loading solo saves - and a successful restore raises RestartRequired, since
        /// the ids this process is running are now the HOST's while the file is ours. Returns
        /// false (with an explaining message) when no backup exists to restore. CoopCore calls
        /// this by name.</summary>
        public static bool RestoreEnumBackup(out string message)
        {
            string p = EnumFilePath();
            try
            {
                var dir = Path.GetDirectoryName(p);
                string newest = null;
                if (Directory.Exists(dir))
                {
                    var baks = Directory.GetFiles(dir, Path.GetFileName(p) + ".coopbak-*");
                    Array.Sort(baks, StringComparer.Ordinal); // timestamp suffix sorts oldest-first
                    if (baks.Length > 0) newest = baks[baks.Length - 1];
                }
                if (newest == null)
                {
                    // Nothing to hand back (e.g. there was no registry before the lend). Leave the
                    // marker so the prompt can reappear; explain the manual path.
                    message = "no backup of your card database was found to restore - if your solo saves won't load, put your own enum_values.json back by hand (see mod page)";
                    return false;
                }
                // Preserve the host's installed file first so a restore is never a one-way loss.
                if (File.Exists(p))
                    File.Copy(p, p + ".hostcopy", overwrite: true);
                File.Copy(newest, p, overwrite: true);
                // Same invariant as InstallEnumFile (see EnumFilePath): the registry on disk is
                // the guest's own again, but the running game is still holding the HOST's ids
                // from prepatch. Nothing about our hash may move until a restart reloads them.
                RestartRequired = true;
                try { File.Delete(EnumMarkerPath()); } catch { }
                message = "your card database was restored from backup - RESTART the game before loading your solo saves";
                return true;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("enum restore failed: " + e.Message);
                message = "could not restore your card database automatically - put your own enum_values.json backup back by hand (see mod page)";
                return false;
            }
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static void PruneBackups(string basePath)
        {
            try
            {
                var dir = Path.GetDirectoryName(basePath);
                var baks = Directory.GetFiles(dir, Path.GetFileName(basePath) + ".coopbak-*");
                Array.Sort(baks, StringComparer.Ordinal); // timestamp suffix sorts oldest-first
                for (int i = 0; i < baks.Length - 3; i++) File.Delete(baks[i]);
            }
            catch { }
        }

        private static string Sha1(string s) { return Sha1Bytes(Encoding.UTF8.GetBytes(s)); }

        private static string Sha1Bytes(byte[] data)
        {
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
        }

        private static string Short(string hex) { return hex.Length > 16 ? hex.Substring(0, 16) : hex; }
    }
}
