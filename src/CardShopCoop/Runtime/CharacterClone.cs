using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Runtime
{
    /// <summary>
    /// Prepares an Instantiated customer body for the game's wardrobe pipeline.
    ///
    /// <para>Instantiate copies the serialized fields and the child objects, but not the
    /// private, non-serialized <c>HairObjects</c>/<c>ApparelObjects</c> lists that reference the
    /// generated hair and apparel children. A source that has already been dressed therefore
    /// yields a clone with EMPTY lists and <c>m_HasInit == true</c>; the game's
    /// <c>Initialize()</c> then takes its already-initialized path and
    /// <c>ApplyCharacterVars</c> indexes those empty lists through <c>setHair</c>/<c>setApparel</c>
    /// and throws.</para>
    ///
    /// <para>This matters on the high-end-console (Xbox / Game Pass) build because
    /// <c>CustomerManager.ApplyTierSwap</c> repoints <c>m_CustomerPrefab</c> at a live pooled
    /// customer instead of a prefab asset, so a dressed NPC becomes the template. Rebuilding the
    /// cloned lists from the source's children lets <c>Initialize()</c> destroy and replace the
    /// inherited look normally, which is the same contract an uninitialized prefab provides.</para>
    /// </summary>
    internal static class CharacterClone
    {
        private static readonly FieldInfo HairObjectsField =
            AccessTools.Field(typeof(CC.CharacterCustomization), "HairObjects");
        private static readonly FieldInfo ApparelObjectsField =
            AccessTools.Field(typeof(CC.CharacterCustomization), "ApparelObjects");

        /// <summary>Prefers a live pooled customer of the requested gender that has never been
        /// dressed (its generated collections are empty on a clone as well), falls back to an
        /// inactive member, then to any member. Returns false so the caller can use the
        /// manager's template prefab.</summary>
        internal static bool TryFindPooledCustomer(CustomerManager manager, bool female,
            out Customer customer)
        {
            customer = null;
            var customers = manager?.GetCustomerList();
            if (customers == null)
            {
                return false;
            }

            Customer inactive = null;
            Customer any = null;
            for (var i = 0; i < customers.Count; i++)
            {
                var candidate = customers[i];
                if (candidate == null || candidate.m_IsFemale != female
                    || candidate.m_CharacterCustom == null)
                {
                    continue;
                }

                if (!candidate.m_IsActive && !candidate.m_CharacterCustom.m_HasInit)
                {
                    customer = candidate;
                    return true;
                }

                if (!candidate.m_IsActive && inactive == null)
                {
                    inactive = candidate;
                }

                if (any == null)
                {
                    any = candidate;
                }
            }

            customer = inactive ?? any;
            return customer != null;
        }

        /// <summary>The prefab a thrown-away body should be cloned from: a live pool member when
        /// one exists (it carries the wardrobe tables and animator the presets were authored
        /// against), otherwise the manager template.</summary>
        internal static Customer FindCustomerSource(CustomerManager manager, bool female)
        {
            if (TryFindPooledCustomer(manager, female, out var pooled))
            {
                return pooled;
            }

            if (manager == null)
            {
                return null;
            }

            return female ? manager.m_CustomerFemalePrefab : manager.m_CustomerPrefab;
        }

        /// <summary>Makes <paramref name="clone"/> behave like an uninitialized template, then
        /// runs the game's own <c>Initialize()</c> for <paramref name="characterName"/>.
        /// <paramref name="source"/> is the customization the clone was Instantiated from, or
        /// null when it is not available.</summary>
        internal static void PrepareForDressing(CC.CharacterCustomization clone,
            CC.CharacterCustomization source, string characterName, string context)
        {
            PrepareForDressing(clone, source, null, null, characterName, context);
        }

        /// <summary>Same as the short overload, with the source and clone roots of the
        /// Instantiated hierarchy so generated hair/apparel parented outside the customization
        /// transform can still be mapped. Pass null roots to map from the customization transforms.
        /// </summary>
        internal static void PrepareForDressing(CC.CharacterCustomization clone,
            CC.CharacterCustomization source, Transform sourceRoot, Transform cloneRoot,
            string characterName, string context)
        {
            if (clone == null)
            {
                return;
            }

            var hairSlots = clone.HairTables?.Count ?? 0;
            var apparelSlots = clone.ApparelTables?.Count ?? 0;
            // The explicit Initialize() below (or the inherited look we keep) owns dressing; a
            // template with Autoload set would otherwise run Start() later and stack a second look.
            clone.Autoload = false;
            clone.CharacterName = characterName ?? "";

            if (clone.m_HasInit)
            {
                var mapped = source == null
                    || RebuildInheritedCollections(clone, source, sourceRoot, cloneRoot);

                if (hairSlots > 0 && apparelSlots > 0 && (!mapped || !HasCollectionsForTables(clone)))
                {
                    // An initialized source whose generated children cannot be mapped (an
                    // appearance mod may build them outside the customization root) leaves no
                    // inherited look to replace. Drop back to a from-scratch initialize so the
                    // wardrobe is built from the tables instead of indexing empty lists.
                    ResetCollections(clone);
                    clone.m_HasInit = false;
                    CoopPlugin.Log.LogWarning("character clone (" + context + "): realized "
                        + "source had no mappable hair/apparel collections; rebuilding the "
                        + "wardrobe from its tables.");
                }
            }

            if (hairSlots == 0 || apparelSlots == 0)
            {
                // A source that lost a wardrobe table cannot be initialized: the preset applied
                // by Initialize indexes HairTables[slot]/ApparelTables[slot]. Keep the look the
                // clone inherited and let callers apply only the slots it can actually dress.
                CoopPlugin.Log.LogWarning("character clone (" + context + "): source has no usable "
                    + "wardrobe tables (hair=" + hairSlots + ", apparel=" + apparelSlots
                    + "); keeping its inherited look.");
                return;
            }

            CoopPlugin.Log.LogDebug("character clone (" + context + "): sourceInit="
                + (source != null && source.m_HasInit) + " cloneInit=" + clone.m_HasInit
                + " hair=" + Count(HairObjectsField, clone) + "/" + hairSlots
                + " apparel=" + Count(ApparelObjectsField, clone) + "/"
                + apparelSlots + " name='" + clone.CharacterName + "'");
            clone.Initialize();
        }

        /// <summary>Repoints a clone's private collections at the generated children it inherited
        /// from <paramref name="source"/>, without dressing it. Use when the clone must keep an
        /// authoritative look now but may be Initialize()d later: without this, that later call
        /// takes the already-initialized path and indexes the empty lists. Returns true when every
        /// real source child could be found on the clone.</summary>
        internal static bool RebuildInheritedCollections(CC.CharacterCustomization clone,
            CC.CharacterCustomization source)
        {
            return RebuildInheritedCollections(clone, source, null, null);
        }

        internal static bool RebuildInheritedCollections(CC.CharacterCustomization clone,
            CC.CharacterCustomization source, Transform sourceRoot, Transform cloneRoot)
        {
            if (clone == null || source == null || !clone.m_HasInit || !source.m_HasInit)
            {
                return false;
            }

            return RebuildCollections(clone, source, sourceRoot, cloneRoot);
        }

        /// <summary>Drops every generated wardrobe object the clone references and clears the
        /// lists so the game's from-scratch initialize can rebuild them at table size.</summary>
        private static void ResetCollections(CC.CharacterCustomization custom)
        {
            ResetCollection(custom, HairObjectsField);
            ResetCollection(custom, ApparelObjectsField);
        }

        private static void ResetCollection(CC.CharacterCustomization custom, FieldInfo field)
        {
            if (custom == null || field == null)
            {
                return;
            }

            var objects = field.GetValue(custom) as IList;
            if (objects == null)
            {
                field.SetValue(custom, new List<GameObject>());
                return;
            }

            for (var i = 0; i < objects.Count; i++)
            {
                if (objects[i] is GameObject generated && generated != null)
                {
                    Object.Destroy(generated);
                }
            }

            objects.Clear();
        }

        /// <summary>Repoints each cloned list entry at the clone's matching child, located by the
        /// sibling-index path it occupies from the supplied Instantiated roots (an appearance mod
        /// can parent generated hair/apparel outside the customization transform). Returns true
        /// when every real source child could be found on the clone. Initialize() then destroys and
        /// replaces the inherited look normally.</summary>
        private static bool RebuildCollections(CC.CharacterCustomization clone,
            CC.CharacterCustomization source, Transform sourceRoot, Transform cloneRoot)
        {
            sourceRoot ??= source.transform;
            cloneRoot ??= clone.transform;
            var hair = RebuildCollection(clone, source, sourceRoot, cloneRoot, HairObjectsField);
            var apparel = RebuildCollection(clone, source, sourceRoot, cloneRoot,
                ApparelObjectsField);
            return hair && apparel;
        }

        private static bool RebuildCollection(CC.CharacterCustomization clone,
            CC.CharacterCustomization source, Transform sourceRoot, Transform cloneRoot,
            FieldInfo field)
        {
            if (clone == null || source == null || field == null)
            {
                return false;
            }

            var sourceObjects = field.GetValue(source) as IList;
            var cloneObjects = field.GetValue(clone) as IList;
            if (sourceObjects == null || cloneObjects == null
                || ReferenceEquals(sourceObjects, cloneObjects))
            {
                return false;
            }

            var mappedAll = true;
            var rebuilt = new List<GameObject>(sourceObjects.Count);
            for (var i = 0; i < sourceObjects.Count; i++)
            {
                // A generated slot the source never filled holds a destroyed placeholder (Unity
                // fake-null); there is no clone child for it, so carry the empty slot across.
                var sourceChild = sourceObjects[i] as GameObject;
                var cloneChild = sourceChild == null ? null
                    : ResolveCloneChild(sourceRoot, cloneRoot, sourceChild.transform);
                if (sourceChild != null && cloneChild == null)
                {
                    mappedAll = false;
                }

                rebuilt.Add(cloneChild?.gameObject);
            }

            cloneObjects.Clear();
            for (var i = 0; i < rebuilt.Count; i++)
            {
                cloneObjects.Add(rebuilt[i]);
            }

            return mappedAll;
        }

        /// <summary>Walks the sibling-index path a source child occupies up to the source root and
        /// returns the clone child at the same path, or null when the clone does not mirror it.</summary>
        private static Transform ResolveCloneChild(Transform sourceRoot, Transform cloneRoot,
            Transform sourceChild)
        {
            if (sourceRoot == null || cloneRoot == null || sourceChild == null)
            {
                return null;
            }

            var indices = new List<int>();
            var current = sourceChild;
            while (current != null && current != sourceRoot)
            {
                indices.Add(current.GetSiblingIndex());
                current = current.parent;
            }

            if (current != sourceRoot)
            {
                return null;
            }

            var result = cloneRoot;
            for (var i = indices.Count - 1; i >= 0; i--)
            {
                if (result == null || indices[i] >= result.childCount)
                {
                    return null;
                }

                result = result.GetChild(indices[i]);
            }

            return result;
        }

        private static bool HasCollectionsForTables(CC.CharacterCustomization custom)
        {
            var hairSlots = custom.HairTables?.Count ?? 0;
            var apparelSlots = custom.ApparelTables?.Count ?? 0;
            return Count(HairObjectsField, custom) >= hairSlots
                && Count(ApparelObjectsField, custom) >= apparelSlots;
        }

        /// <summary>How many hair/apparel slots the clone can actually dress: the table count
        /// bounded by the runtime collections Initialize prepared. A payload must be clamped to
        /// this, not to the table count, or a partially prepared body indexes an empty list.</summary>
        internal static int DressableHairSlots(CC.CharacterCustomization custom)
        {
            if (custom == null)
            {
                return 0;
            }

            return Mathf.Min(custom.HairTables?.Count ?? 0, Count(HairObjectsField, custom));
        }

        internal static int DressableApparelSlots(CC.CharacterCustomization custom)
        {
            if (custom == null)
            {
                return 0;
            }

            return Mathf.Min(custom.ApparelTables?.Count ?? 0,
                Count(ApparelObjectsField, custom));
        }

        private static int Count(FieldInfo field, CC.CharacterCustomization custom)
        {
            var objects = field?.GetValue(custom) as ICollection;
            return objects?.Count ?? 0;
        }
    }
}
