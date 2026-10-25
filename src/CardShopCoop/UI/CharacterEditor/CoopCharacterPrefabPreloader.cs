using System;
using CardShopCoop.Modules.Presence;
using UnityEngine;

namespace CardShopCoop.UI
{
    /// <summary>
    /// Resolves the game's full character prefab once, as early as the title screen, so the
    /// character tab never has to wait for it and never falls back to CC_Dummy on a build that
    /// packages the prefab in Resources.
    ///
    /// <para>The public build exposes the full prefab through Resources (its ResourceManager
    /// contains <c>female_combined_hp variant</c>/<c>male_combined_hp variant</c>). The Unity 6
    /// build packages the same prefab in the shop scene's shared assets instead, where Unity's
    /// public Resources API cannot reach it before that scene loads; that build continues to use
    /// the live CustomerManager clone once a world exists.</para>
    /// </summary>
    internal sealed class CoopCharacterPrefabPreloader
    {
        private static readonly string[] FemalePrefabNames =
        {
            "Female_Combined_HP Variant",
            "Female_Combined_HP",
            "Female_Combined_HPOptimized",
        };

        private static readonly string[] MalePrefabNames =
        {
            "Male_Combined_HP Variant",
            "Male_Combined_HP VariantA",
            "Male_Combined_HP",
            "Male_Combined_HPOptimized",
        };

        private static GameObject _female;
        private static GameObject _male;
        private static bool _started;

        /// <summary>Raised on the main thread when the preload resolved a full prefab. The editor
        /// refreshes from this event, so it never polls for the preload.</summary>
        internal static event Action Completed;

        internal static bool HasAny => _female != null || _male != null;

        internal static void EnsureStarted()
        {
            if (_started)
            {
                if (_female == null || _male == null)
                {
                    // Returning to the title screen after a world has loaded can leave the shop's
                    // character prefab in memory even though this build has no Resources path for
                    // it. Re-scan once per caller rather than keeping a permanent dummy.
                    FindLoadedPrefabs();
                    Notify();
                }
                return;
            }

            _started = true;
            _female = LoadResource(FemalePrefabNames);
            _male = LoadResource(MalePrefabNames);
            if (_female == null || _male == null)
            {
                FindLoadedPrefabs();
            }

            CoopPlugin.Log?.LogInfo("co-op character editor preload: female="
                + (_female != null) + " male=" + (_male != null) + ".");
            Notify();
        }

        private static void Notify()
        {
            try
            {
                Completed?.Invoke();
            }
            catch (Exception error)
            {
                CoopPlugin.Log?.LogError("co-op character editor preload completion failed: "
                    + error);
            }
        }

        internal static bool TryGet(bool female, out GameObject prefab)
        {
            prefab = female ? _female : _male;
            return prefab != null;
        }

        private static GameObject LoadResource(string[] names)
        {
            for (var i = 0; i < names.Length; i++)
            {
                var prefab = Resources.Load<GameObject>(names[i]);
                if (IsUsable(prefab))
                {
                    CoopPlugin.Log?.LogInfo("co-op character editor: preloaded Resources prefab '"
                        + names[i] + "'.");
                    return prefab;
                }
            }
            return null;
        }

        /// <summary>Some builds load the shop's shared assets before the title screen becomes
        /// interactive, even though they have no Resources path. FindObjectsOfTypeAll sees those
        /// already-loaded prefab assets without forcing a scene load.</summary>
        private static void FindLoadedPrefabs()
        {
            var objects = Resources.FindObjectsOfTypeAll<GameObject>();
            for (var i = 0; i < objects.Length; i++)
            {
                var candidate = objects[i];
                if (candidate == null)
                {
                    continue;
                }

                if (_female == null && Matches(candidate.name, FemalePrefabNames)
                    && IsUsable(candidate))
                {
                    _female = candidate;
                }
                if (_male == null && Matches(candidate.name, MalePrefabNames)
                    && IsUsable(candidate))
                {
                    _male = candidate;
                }
                if (_female != null && _male != null)
                {
                    return;
                }
            }
        }

        private static bool Matches(string name, string[] names)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            for (var i = 0; i < names.Length; i++)
            {
                if (string.Equals(name, names[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsUsable(GameObject prefab)
        {
            if (prefab == null)
            {
                return false;
            }

            var customization = prefab.GetComponentInChildren<CC.CharacterCustomization>(true);
            return customization != null && customization.Presets != null
                && customization.Presets.Presets != null
                && customization.Presets.Presets.Count > 0;
        }
    }
}
