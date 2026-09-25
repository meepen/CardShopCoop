using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using CardShopCoop.Attributes;

namespace CardShopCoop.Runtime
{
    /// <summary>
    /// Diagnostic module gate. A "module" is the feature namespace directly under
    /// <c>CardShopCoop.Modules</c> (World, Npc, Presence, ...); anything else is Core.
    ///
    /// Every module gets a boolean config entry (a checkbox in the co-op window and in BepInEx's
    /// ConfigurationManager). A disabled module's behaviours are never created, so the Harmony
    /// patches, message handlers and per-frame work they own are all skipped - use it to bisect
    /// a performance problem by turning whole features off.
    ///
    /// The network DTO catalog is discovered assembly-wide
    /// (<see cref="Net.Protocol.ProtocolRegistry.EnsureCoreAssemblyRegistered"/>), not from the
    /// behaviours, so disabling a module does NOT change the join handshake catalog: a peer with
    /// a module off still connects to a peer with it on. Only the disabled module's inbound
    /// messages go unhandled.
    /// </summary>
    internal static class ModuleCatalog
    {
        private const string ModulesRoot = "CardShopCoop.Modules.";
        private const string Section = "Modules";
        private const string CoreModule = "Core";

        private static readonly object Gate = new();
        private static Dictionary<string, ConfigEntry<bool>> _entries;
        private static List<string> _known;
        private static ConfigFile _config;

        /// <summary>The module a behaviour type belongs to. A type from an external mod belongs to
        /// that mod's assembly (one enable/disable checkbox per integrating mod, regardless of the
        /// namespace it happens to use); built-in modules are namespaces under
        /// <c>CardShopCoop.Modules</c>.</summary>
        internal static string ModuleOf(Type type)
        {
            var assembly = type?.Assembly;
            if (assembly != null && assembly != typeof(ModuleCatalog).Assembly)
            {
                return assembly.GetName().Name;
            }

            var ns = type?.Namespace;
            if (!string.IsNullOrEmpty(ns) && ns.StartsWith(ModulesRoot, StringComparison.Ordinal))
            {
                var rest = ns.Substring(ModulesRoot.Length);
                var dot = rest.IndexOf('.');
                return dot < 0 ? rest : rest.Substring(0, dot);
            }

            return CoreModule;
        }

        /// <summary>Bind one enable/disable checkbox per module. Call this once from plugin
        /// startup, before any runtime creates its behaviours.</summary>
        internal static void Bind(ConfigFile config)
        {
            if (config == null)
            {
                return;
            }

            lock (Gate)
            {
                if (_entries != null)
                {
                    return;
                }

                _config = config;
                var entries = new Dictionary<string, ConfigEntry<bool>>(StringComparer.OrdinalIgnoreCase);
                foreach (var module in Names())
                {
                    entries[module] = config.Bind(Section, module, true,
                        "Enable the " + module + " co-op module. Turn modules off one at a time to "
                        + "find which one costs frame rate. Applies after rejoining (persistent "
                        + "modules apply after a restart). The join handshake still matches with "
                        + "modules off - you may disable different modules on each PC.");
                }

                _entries = entries;
            }
        }

        internal static IReadOnlyList<string> Names()
        {
            lock (Gate)
            {
                return _known ??= Discover();
            }
        }

        /// <summary>Adds a late-discovered external mod's enable/disable checkbox. Called when an
        /// integrating mod's assembly is registered, after plugin startup.</summary>
        internal static void BindExternal(string module)
        {
            if (string.IsNullOrEmpty(module) || string.Equals(module, CoreModule,
                StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            lock (Gate)
            {
                if (_config == null || _entries == null || _entries.ContainsKey(module))
                {
                    return;
                }

                _entries[module] = _config.Bind(Section, module, true,
                    "Enable the external co-op mod '" + module + "'. Applies after rejoining.");
                if (_known != null && !_known.Contains(module))
                {
                    _known.Add(module);
                    _known.Sort(StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        internal static bool IsEnabled(string module)
        {
            if (string.IsNullOrEmpty(module)
                || string.Equals(module, CoreModule, StringComparison.OrdinalIgnoreCase))
            {
                // Core infrastructure (scene refs, network, plugin shell) is always on so a
                // stray config cannot strand the process.
                return true;
            }

            lock (Gate)
            {
                if (_entries != null && _entries.TryGetValue(module, out var entry) && entry != null)
                {
                    return entry.Value;
                }
            }

            // Unbound (e.g. a type in a namespace the scan did not see) stays enabled.
            return true;
        }

        internal static bool IsEnabled(Type type) => IsEnabled(ModuleOf(type));

        /// <summary>The config entry backing a module's checkbox, or null before
        /// <see cref="Bind"/>.</summary>
        internal static ConfigEntry<bool> Entry(string module)
        {
            lock (Gate)
            {
                return _entries != null && module != null
                    && _entries.TryGetValue(module, out var entry) ? entry : null;
            }
        }

        /// <summary>Logs which modules will run. Safe to call once at plugin startup.</summary>
        internal static void LogConfiguration()
        {
            var names = Names();
            var disabled = names.Where(name => !IsEnabled(name)).ToList();
            CoopPlugin.Log?.LogInfo("[modules] " + names.Count + " available: " + string.Join(", ", names));
            CoopPlugin.Log?.LogInfo(disabled.Count == 0
                ? "[modules] all enabled."
                : "[modules] disabled: " + string.Join(", ", disabled)
                    + " (rejoin or restart to apply)");
        }

        private static List<string> Discover()
        {
            var modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Type[] types;
            try
            {
                types = typeof(CoopBehaviour).Assembly.GetTypes();
            }
            catch (System.Reflection.ReflectionTypeLoadException exception)
            {
                types = exception.Types.Where(type => type != null).ToArray();
            }

            foreach (var type in types)
            {
                if (type == null || type.IsAbstract || !typeof(CoopBehaviour).IsAssignableFrom(type))
                {
                    continue;
                }

                if (type.GetCustomAttributes(typeof(ServerBehaviourAttribute), false).Length == 0
                    && type.GetCustomAttributes(typeof(ClientBehaviourAttribute), false).Length == 0
                    && type.GetCustomAttributes(typeof(PersistentBehaviourAttribute), false).Length == 0)
                {
                    continue;
                }

                modules.Add(ModuleOf(type));
            }

            modules.Remove(CoreModule);
            return modules.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
