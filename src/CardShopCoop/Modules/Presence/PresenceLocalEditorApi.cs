using CardShopCoop.Runtime;

namespace CardShopCoop.Modules.Presence
{
    /// <summary>Narrow surface for the native character editor when no presence service exists
    /// (title screen / no session). It owns the same persisted model file as the service and the
    /// same game appearance pipeline, so the editor never creates a second source of truth.</summary>
    internal static class PresenceLocalEditorApi
    {
        private static readonly PresenceAvatarRenderer Appearance = new();

        internal static bool HasActiveService => PresenceService.Active != null;

        internal static bool TryLoadSavedModel(out PresenceModelEntry model)
            => PresenceModelStore.TryLoad(out model);

        internal static void SaveModel(PresenceModelEntry model) => PresenceModelStore.Save(model);

        internal static bool IsNude(PresenceModelEntry model)
            => PresenceAvatarRenderer.IsNude(model);

        internal static bool IsNude(string customizationJson)
            => PresenceAvatarRenderer.IsNude(customizationJson);

        internal static PresenceModelEntry ModelFromPreset(CC.CC_CharacterData preset)
            => PresenceAvatarRenderer.ModelFromPreset(preset);

        internal static void ApplyModel(CC.CharacterCustomization customization,
            PresenceModelEntry model)
            => Appearance.ApplyLocalModel(customization, model);

        internal static void ApplyCharacterData(CC.CharacterCustomization customization,
            CC.CC_CharacterData data)
            => PresenceAvatarRenderer.ApplyCharacterData(customization, data);

        internal static void ClearEmptyWardrobeSlots(CC.CharacterCustomization customization,
            CC.CC_CharacterData data)
            => PresenceAvatarRenderer.ClearEmptyWardrobeSlots(customization, data);

        internal static int DressableHairSlots(CC.CharacterCustomization customization)
            => CharacterClone.DressableHairSlots(customization);

        internal static int DressableApparelSlots(CC.CharacterCustomization customization)
            => CharacterClone.DressableApparelSlots(customization);

        internal static bool ClearHair(CC.CharacterCustomization customization, int slot)
            => Appearance.ClearHair(customization, slot);

        internal static bool ClearApparel(CC.CharacterCustomization customization, int slot)
            => Appearance.ClearApparel(customization, slot);

        internal static bool SetHairColor(CC.CharacterCustomization customization, int slot,
            UnityEngine.Color color)
            => Appearance.SetHairColor(customization, slot, color);

        internal static bool SetApparelTint(CC.CharacterCustomization customization, int slot,
            UnityEngine.Color color)
            => Appearance.SetApparelTint(customization, slot, color);

        internal static bool SetBlendshape(CC.CharacterCustomization customization, string name,
            float value)
            => Appearance.SetBlendshape(customization, name, value);
    }
}
