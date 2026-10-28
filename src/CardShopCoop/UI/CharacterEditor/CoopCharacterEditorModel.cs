using System;
using System.Collections.Generic;
using System.Text;
using CardShopCoop.Modules.Presence;
using Newtonsoft.Json;
using UnityEngine;

namespace CardShopCoop.UI
{
    /// <summary>Read/write helpers shared by the local and service-backed character editor.
    /// Everything here works from the game's own <see cref="CC.CC_CharacterData"/>; the editor
    /// keeps no parallel appearance schema.</summary>
    internal static class CoopCharacterEditorModel
    {
        internal const string ApparelTintPrefix = "CardShopCoop.ApparelTint.";
        internal const string HairColorProperty = "_Hair_Color";
        internal const string SkinTintProperty = "_Skin_Tint";
        internal const string SkinTextureProperty = "_Skin_Texture";
        internal const string EyeColorProperty = "_Eye_Color";
        internal const string LipColorProperty = "_Lips_Tint";

        internal static PresenceModelEntry Clone(PresenceModelEntry model)
        {
            return model == null
                ? null
                : new PresenceModelEntry
                {
                    Id = model.Id,
                    Female = model.Female,
                    ModelIndex = model.ModelIndex,
                    CustomizationJson = model.CustomizationJson,
                };
        }

        internal static bool IsEditable(CC.CharacterCustomization customization, bool female)
        {
            if (customization == null || customization.StoredCharacterData == null)
            {
                return false;
            }

            return PresenceLocalEditorApi.DressableHairSlots(customization) > 0
                || PresenceLocalEditorApi.DressableApparelSlots(customization) > 0;
        }

        internal static bool IsNude(PresenceModelEntry model)
        {
            return PresenceLocalEditorApi.IsNude(model);
        }

        internal static bool IsUsableJson(string customizationJson)
        {
            if (string.IsNullOrEmpty(customizationJson))
            {
                return false;
            }

            try
            {
                var data = JsonConvert.DeserializeObject<CC.CC_CharacterData>(customizationJson);
                return data != null && data.Blendshapes != null && data.HairNames != null
                    && data.ApparelNames != null && data.ApparelMaterials != null
                    && data.HairColor != null && data.FloatProperties != null
                    && data.TextureProperties != null && data.ColorProperties != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static CC.CC_CharacterData Data(CC.CharacterCustomization customization)
        {
            return customization?.StoredCharacterData;
        }

        internal static int ParseModelIndex(string characterName)
        {
            if (string.IsNullOrEmpty(characterName))
            {
                return 0;
            }

            var female = characterName.StartsWith("Female", StringComparison.OrdinalIgnoreCase);
            var prefixLength = female ? 6 : 4;
            if (characterName.Length <= prefixLength)
            {
                return 0;
            }

            int parsed;
            return int.TryParse(characterName.Substring(prefixLength), out parsed)
                ? Mathf.Max(0, parsed)
                : 0;
        }

        internal static bool IsFemaleName(string characterName)
        {
            return !string.IsNullOrEmpty(characterName)
                && characterName.StartsWith("Female", StringComparison.OrdinalIgnoreCase);
        }

        internal static string BaseCharacterName(bool female, int modelIndex)
        {
            return (female ? "Female" : "Male") + Mathf.Max(0, modelIndex);
        }

        internal static List<string> PresetNames(CC.CharacterCustomization customization,
            bool female)
        {
            var result = new List<string>();
            var presets = customization?.Presets?.Presets;
            if (presets == null)
            {
                return result;
            }

            var prefix = female ? "Female" : "Male";
            for (var i = 0; i < presets.Count; i++)
            {
                var preset = presets[i];
                if (preset != null && !string.IsNullOrEmpty(preset.CharacterName)
                    && preset.CharacterName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(preset.CharacterName);
                }
            }

            result.Sort((a, b) =>
            {
                var ai = ParseModelIndex(a);
                var bi = ParseModelIndex(b);
                if (ai != bi)
                {
                    return ai.CompareTo(bi);
                }
                return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        internal static CC.CC_CharacterData FindPreset(CC.CharacterCustomization customization,
            string characterName)
        {
            var presets = customization?.Presets?.Presets;
            if (presets == null || string.IsNullOrEmpty(characterName))
            {
                return null;
            }

            for (var i = 0; i < presets.Count; i++)
            {
                if (presets[i] != null && presets[i].CharacterName == characterName)
                {
                    return presets[i];
                }
            }
            return null;
        }

        internal static int ClampModelIndex(CC.CharacterCustomization customization, bool female,
            int modelIndex)
        {
            var index = Mathf.Max(0, modelIndex);
            if (FindPreset(customization, BaseCharacterName(female, index)) != null)
            {
                return index;
            }

            var names = PresetNames(customization, female);
            return names.Count > 0 ? ParseModelIndex(names[0]) : 0;
        }

        internal static List<string> BlendshapeNames(CC.CharacterCustomization customization,
            bool female)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            AddPropertyNames(names, customization?.StoredCharacterData?.Blendshapes);
            var presets = customization?.Presets?.Presets;
            if (presets != null)
            {
                var prefix = female ? "Female" : "Male";
                for (var i = 0; i < presets.Count; i++)
                {
                    var preset = presets[i];
                    if (preset != null && !string.IsNullOrEmpty(preset.CharacterName)
                        && preset.CharacterName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        AddPropertyNames(names, preset.Blendshapes);
                    }
                }
            }

            var result = new List<string>(names);
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        internal static List<string> FloatNames(CC.CharacterCustomization customization,
            bool female)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            AddPropertyNames(names, customization?.StoredCharacterData?.FloatProperties);
            var presets = customization?.Presets?.Presets;
            if (presets != null)
            {
                var prefix = female ? "Female" : "Male";
                for (var i = 0; i < presets.Count; i++)
                {
                    var preset = presets[i];
                    if (preset != null && !string.IsNullOrEmpty(preset.CharacterName)
                        && preset.CharacterName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        AddPropertyNames(names, preset.FloatProperties);
                    }
                }
            }

            var result = new List<string>(names);
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        internal static List<string> ColorNames(CC.CharacterCustomization customization,
            bool female)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            AddPropertyNames(names, customization?.StoredCharacterData?.ColorProperties);
            var presets = customization?.Presets?.Presets;
            if (presets != null)
            {
                var prefix = female ? "Female" : "Male";
                for (var i = 0; i < presets.Count; i++)
                {
                    var preset = presets[i];
                    if (preset != null && !string.IsNullOrEmpty(preset.CharacterName)
                        && preset.CharacterName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        AddPropertyNames(names, preset.ColorProperties);
                    }
                }
            }

            var result = new List<string>();
            foreach (var name in names)
            {
                if (name == HairColorProperty
                    || name.StartsWith(ApparelTintPrefix, StringComparison.Ordinal))
                {
                    continue;
                }
                result.Add(name);
            }

            result.Sort((a, b) =>
            {
                var ai = ColorPriority(a);
                var bi = ColorPriority(b);
                if (ai != bi)
                {
                    return ai.CompareTo(bi);
                }
                return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        internal static List<string> TextureValues(CC.CharacterCustomization customization,
            bool female, string propertyName)
        {
            var values = new HashSet<string>(StringComparer.Ordinal);
            AddTextureValues(values, customization?.StoredCharacterData?.TextureProperties,
                propertyName);
            var presets = customization?.Presets?.Presets;
            if (presets != null)
            {
                var prefix = female ? "Female" : "Male";
                for (var i = 0; i < presets.Count; i++)
                {
                    var preset = presets[i];
                    if (preset != null && !string.IsNullOrEmpty(preset.CharacterName)
                        && preset.CharacterName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        AddTextureValues(values, preset.TextureProperties, propertyName);
                    }
                }
            }

            var result = new List<string>(values);
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        internal static List<Color> SkinToneSwatches(CC.CharacterCustomization customization,
            bool female)
        {
            var result = new List<Color>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var presets = customization?.Presets?.Presets;
            if (presets == null)
            {
                return result;
            }

            var prefix = female ? "Female" : "Male";
            for (var i = 0; i < presets.Count; i++)
            {
                var preset = presets[i];
                if (preset == null || string.IsNullOrEmpty(preset.CharacterName)
                    || !preset.CharacterName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var property = FindProperty(preset.ColorProperties, SkinTintProperty);
                if (property == null || string.IsNullOrEmpty(property.stringValue)
                    || !seen.Add(property.stringValue))
                {
                    continue;
                }

                Color color;
                if (ColorUtility.TryParseHtmlString("#" + property.stringValue, out color))
                {
                    result.Add(color);
                }
            }
            return result;
        }

        internal static Color ReadColor(List<CC.CC_Property> properties, string name,
            Color fallback)
        {
            var property = FindProperty(properties, name);
            return ReadColor(property, fallback);
        }

        internal static Color ReadColor(CC.CC_Property property, Color fallback)
        {
            Color color;
            if (property != null && !string.IsNullOrEmpty(property.stringValue)
                && ColorUtility.TryParseHtmlString("#" + property.stringValue, out color))
            {
                return color;
            }
            return fallback;
        }

        internal static float ReadFloat(List<CC.CC_Property> properties, string name,
            float fallback)
        {
            var property = FindProperty(properties, name);
            return property != null ? property.floatValue : fallback;
        }

        internal static string ReadTexture(List<CC.CC_Property> properties, string name)
        {
            var property = FindProperty(properties, name);
            return property != null ? property.stringValue : "";
        }

        internal static Color HairColor(CC.CC_CharacterData data, int slot)
        {
            if (data?.HairColor == null || slot < 0 || slot >= data.HairColor.Count)
            {
                return Color.white;
            }
            return ReadColor(data.HairColor[slot], Color.white);
        }

        internal static Color ApparelTint(CC.CC_CharacterData data, int slot)
        {
            return ReadColor(data?.ColorProperties, ApparelTintPrefix + slot, Color.white);
        }

        internal static string HairName(CC.CC_CharacterData data, int slot)
        {
            return data?.HairNames != null && slot >= 0 && slot < data.HairNames.Count
                ? data.HairNames[slot] ?? ""
                : "";
        }

        internal static string ApparelName(CC.CC_CharacterData data, int slot)
        {
            return data?.ApparelNames != null && slot >= 0 && slot < data.ApparelNames.Count
                ? data.ApparelNames[slot] ?? ""
                : "";
        }

        internal static int ApparelMaterial(CC.CC_CharacterData data, int slot)
        {
            return data?.ApparelMaterials != null && slot >= 0 && slot < data.ApparelMaterials.Count
                ? Mathf.Max(0, data.ApparelMaterials[slot])
                : 0;
        }

        internal static void HairChoices(CC.CharacterCustomization customization, int slot,
            out List<string> display, out List<string> names)
        {
            display = new List<string> { "None" };
            names = new List<string> { "" };
            var table = customization?.HairTables != null && slot >= 0
                && slot < customization.HairTables.Count ? customization.HairTables[slot] : null;
            if (table?.Hairstyles == null)
            {
                return;
            }

            for (var i = 0; i < table.Hairstyles.Count; i++)
            {
                var hairstyle = table.Hairstyles[i];
                var name = hairstyle.Name ?? "";
                if (IsNoneName(name))
                {
                    // The leading "None" option already represents an empty slot.
                    continue;
                }
                names.Add(name);
                display.Add(string.IsNullOrEmpty(name) ? "(unnamed)" : name);
            }
        }

        internal static void ApparelChoices(CC.CharacterCustomization customization, int slot,
            out List<string> display, out List<string> names)
        {
            display = new List<string>();
            names = new List<string>();
            if (CoopPlugin.AllowNsfw.Value)
            {
                display.Add("Nude");
                names.Add("");
            }

            var table = customization?.ApparelTables != null && slot >= 0
                && slot < customization.ApparelTables.Count
                ? customization.ApparelTables[slot] : null;
            if (table?.Items == null)
            {
                return;
            }

            for (var i = 0; i < table.Items.Count; i++)
            {
                var item = table.Items[i];
                var name = item.Name ?? "";
                names.Add(name);
                display.Add(string.IsNullOrEmpty(item.DisplayName)
                    ? (string.IsNullOrEmpty(name) ? "(unnamed)" : name)
                    : item.DisplayName);
            }
        }

        internal static bool IsNoneName(string name)
        {
            return string.IsNullOrEmpty(name)
                || name.Equals("None", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsNudeApparelName(string name)
        {
            return IsNoneName(name) || name.Equals("Nude", StringComparison.OrdinalIgnoreCase);
        }

        internal static int FindChoice(List<string> names, string current, bool emptyIsFirst)
        {
            if (string.IsNullOrEmpty(current))
            {
                return emptyIsFirst ? 0 : -1;
            }

            var index = names.IndexOf(current);
            return index >= 0 ? index : 0;
        }

        internal static int MaterialCount(CC.CharacterCustomization customization, int slot,
            string apparelName)
        {
            var table = customization?.ApparelTables != null && slot >= 0
                && slot < customization.ApparelTables.Count
                ? customization.ApparelTables[slot] : null;
            if (table?.Items == null || string.IsNullOrEmpty(apparelName))
            {
                return 0;
            }

            for (var i = 0; i < table.Items.Count; i++)
            {
                if (table.Items[i].Name == apparelName)
                {
                    return table.Items[i].Materials?.Count ?? 0;
                }
            }
            return 0;
        }

        internal static string HairLabel(CC.CharacterCustomization customization, int slot)
        {
            var table = customization?.HairTables != null && slot >= 0
                && slot < customization.HairTables.Count ? customization.HairTables[slot] : null;
            var raw = table != null ? table.name : null;
            var label = Prettify(raw, "Hair " + (slot + 1));
            return label == "Hair" && slot > 0 ? "Hair " + (slot + 1) : label;
        }

        internal static string ApparelLabel(CC.CharacterCustomization customization, int slot)
        {
            var table = customization?.ApparelTables != null && slot >= 0
                && slot < customization.ApparelTables.Count
                ? customization.ApparelTables[slot] : null;
            if (table != null && !string.IsNullOrEmpty(table.Label))
            {
                return Prettify(table.Label, "Apparel " + (slot + 1));
            }
            return Prettify(table?.name, "Apparel " + (slot + 1));
        }

        internal static string ColorLabel(string propertyName)
        {
            if (propertyName == SkinTintProperty)
            {
                return "Skin tone";
            }
            if (propertyName == EyeColorProperty)
            {
                return "Eye color";
            }
            if (propertyName == LipColorProperty)
            {
                return "Lip color";
            }
            return Prettify(propertyName, propertyName);
        }

        /// <summary>Human-readable form of a game asset name (UpperBody_F -> Upper Body).</summary>
        internal static string Prettify(string raw, string fallback)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return fallback;
            }

            var text = raw.Trim();
            if (text.StartsWith("_", StringComparison.Ordinal))
            {
                text = text.Substring(1);
            }

            var builder = new StringBuilder(text.Length + 8);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '_')
                {
                    if (builder.Length > 0 && builder[builder.Length - 1] != ' ')
                    {
                        builder.Append(' ');
                    }
                    continue;
                }

                if (i > 0 && char.IsUpper(c) && (char.IsLower(text[i - 1])
                    || (i + 1 < text.Length && char.IsLower(text[i + 1])
                        && text[i - 1] != ' ')))
                {
                    if (builder.Length > 0 && builder[builder.Length - 1] != ' ')
                    {
                        builder.Append(' ');
                    }
                }
                builder.Append(c);
            }

            var result = builder.ToString().Trim();
            if (result.Length > 2 && (result.EndsWith(" F", StringComparison.Ordinal)
                || result.EndsWith(" M", StringComparison.Ordinal)))
            {
                result = result.Substring(0, result.Length - 2);
            }
            return result.Length > 0 ? result : fallback;
        }

        internal static CC.CC_Property FindProperty(List<CC.CC_Property> properties, string name)
        {
            if (properties == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            for (var i = 0; i < properties.Count; i++)
            {
                var property = properties[i];
                if (property != null && property.propertyName == name)
                {
                    return property;
                }
            }
            return null;
        }

        internal static void SetFloat(CC.CharacterCustomization customization, string name,
            float value)
        {
            if (customization?.StoredCharacterData == null || string.IsNullOrEmpty(name))
            {
                return;
            }

            var property = FindProperty(customization.StoredCharacterData.FloatProperties, name);
            if (property == null)
            {
                property = new CC.CC_Property { propertyName = name };
            }
            property.floatValue = value;
            customization.setFloatProperty(property, save: true);
        }

        internal static void SetColor(CC.CharacterCustomization customization, string name,
            Color color)
        {
            if (customization?.StoredCharacterData == null || string.IsNullOrEmpty(name))
            {
                return;
            }

            var property = FindProperty(customization.StoredCharacterData.ColorProperties, name);
            if (property == null)
            {
                property = new CC.CC_Property { propertyName = name };
            }
            customization.setColorProperty(property, color, save: true);
        }

        internal static void SetTexture(CC.CharacterCustomization customization, string name,
            string value)
        {
            if (customization?.StoredCharacterData == null || string.IsNullOrEmpty(name))
            {
                return;
            }

            var property = FindProperty(customization.StoredCharacterData.TextureProperties, name);
            if (property == null)
            {
                property = new CC.CC_Property { propertyName = name };
            }
            property.stringValue = value ?? "";
            customization.setTextureProperty(property, save: true);
        }

        internal static void AddPropertyNames(HashSet<string> names,
            List<CC.CC_Property> properties)
        {
            if (properties == null)
            {
                return;
            }

            for (var i = 0; i < properties.Count; i++)
            {
                var property = properties[i];
                if (property != null && !string.IsNullOrEmpty(property.propertyName))
                {
                    names.Add(property.propertyName);
                }
            }
        }

        private static void AddTextureValues(HashSet<string> values,
            List<CC.CC_Property> properties, string propertyName)
        {
            if (properties == null)
            {
                return;
            }

            for (var i = 0; i < properties.Count; i++)
            {
                var property = properties[i];
                if (property != null && property.propertyName == propertyName
                    && !string.IsNullOrEmpty(property.stringValue))
                {
                    values.Add(property.stringValue);
                }
            }
        }

        private static int ColorPriority(string name)
        {
            if (name == SkinTintProperty)
            {
                return 0;
            }
            if (name == EyeColorProperty)
            {
                return 1;
            }
            if (name == LipColorProperty)
            {
                return 2;
            }
            return 10;
        }
    }
}
