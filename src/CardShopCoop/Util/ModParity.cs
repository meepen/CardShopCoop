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
        /// list so a mismatch can be shown side-by-side instead of just rejected.</summary>
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
