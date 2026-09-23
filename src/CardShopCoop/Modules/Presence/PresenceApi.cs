using System.Collections.Generic;
using System;
using UnityEngine;

namespace CardShopCoop.Modules.Presence
{
    /// <summary>
    /// Narrow, cutover-safe surface for other features. Trade and Deodorant should consume the
    /// host-side peer sample API; Register and UI should use only the camera/model/position calls
    /// here rather than reaching into a behaviour or CoopCore fields.
    /// </summary>
    public static class PresenceApi
    {
        private static readonly Dictionary<int, string> Names = new();
        private static Func<string> _localName;

        public static string LocalPlayerName => _localName?.Invoke() ?? CoopPlugin.PlayerName.Value;

        public static IReadOnlyCollection<string> PeerNames => Names.Values;

        public static string PeerName(int connectionId)
            => Names.TryGetValue(connectionId, out var name) ? name : null;

        internal static void ConfigureIdentity(Func<string> localName)
        {
            _localName = localName;
        }

        internal static void SetPeerName(int connectionId, string name)
        {
            if (connectionId <= 0)
                return;
            Names[connectionId] = string.IsNullOrWhiteSpace(name) ? "Player " + connectionId : name;
        }

        internal static void RemovePeer(int connectionId) => Names.Remove(connectionId);

        internal static void ClearPeerNames() => Names.Clear();

        public static bool TryGetPeerPresence(int connectionId, out PeerPresence presence)
        {
            var active = PresenceHostBehaviour.Active;
            if (active != null)
            {
                return active.TryGetPeerPresence(connectionId, out presence);
            }

            presence = default(PeerPresence);
            return false;
        }

        public static bool TryGetPeerCamera(int avatarId, out Vector3 position, out Quaternion rotation)
        {
            position = default(Vector3);
            rotation = Quaternion.identity;
            var service = PresenceService.Active;
            return service != null && service.TryGetPeerCamera(avatarId, out position, out rotation);
        }

        /// <summary>Camera pose of the peer identified by a holder connection id (0 = the host),
        /// so placement presentation can aim a held object from that player's view.</summary>
        public static bool TryGetPeerCameraForHolder(int holderConnectionId, out Vector3 position,
            out Quaternion rotation)
        {
            position = default(Vector3);
            rotation = Quaternion.identity;
            var service = PresenceService.Active;
            return service != null
                && service.TryGetPeerCameraForHolder(holderConnectionId, out position, out rotation);
        }

        /// <summary>Chest-bone anchor of the avatar holding a box (holder id in the sender's
        /// connection space; 0 means the host). Used to ride the real carried box on the avatar.</summary>
        public static bool TryGetRemoteCarryAnchor(int holderConnectionId, out Transform anchor)
        {
            anchor = null;
            var service = PresenceService.Active;
            return service != null && service.TryGetRemoteCarryAnchor(holderConnectionId, out anchor);
        }

        public static bool TryGetLocalPlayerPosition(out Vector3 position)
        {
            position = default(Vector3);
            var service = PresenceService.Active;
            return service != null && service.TryGetLocalPosition(out position);
        }

        public static PresenceModelEntry GetLocalPlayerModel()
        {
            return PresenceService.Active?.GetLocalModel();
        }

        public static int PlayerModelGeneration => PresenceService.Active?.ModelGeneration ?? 0;

        public static bool IsLocalConnection(int connectionId)
        {
            if (connectionId == 0 && PresenceHostBehaviour.Active != null)
            {
                return true;
            }

            return PresenceClientBehaviour.Active?.IsLocalConnection(connectionId) == true;
        }

        public static bool CanUndoPlayerModel => PresenceService.Active?.CanUndo == true;
        public static bool CanRedoPlayerModel => PresenceService.Active?.CanRedo == true;

        public static void SetCharacterPreview(bool active) => PresenceService.Active?.SetPreview(active);

        public static List<CC.CC_Property> GetLocalModelSliders()
            => PresenceService.Active?.GetLocalSliders() ?? new List<CC.CC_Property>();

        public static CC.CharacterCustomization GetLocalCustomization()
            => PresenceService.Active?.GetLocalCustomization();

        public static void CommitLocalCustomization() => PresenceService.Active?.CommitCustomization();

        public static bool UndoPlayerModel() => PresenceService.Active?.Undo() == true;
        public static bool RedoPlayerModel() => PresenceService.Active?.Redo() == true;

        public static List<string> GetLocalPresetNames()
            => PresenceService.Active?.GetPresetNames() ?? new List<string>();

        public static void ApplyLocalPreset(string presetName) => PresenceService.Active?.ApplyPreset(presetName);

        public static void ClearLocalHair(int slot) => PresenceService.Active?.ClearHair(slot);
        public static void ClearLocalApparel(int slot) => PresenceService.Active?.ClearApparel(slot);
        public static void SetLocalHairColor(int slot, Color color)
            => PresenceService.Active?.SetHairColor(slot, color);
        public static void SetLocalApparelTint(int slot, Color color)
            => PresenceService.Active?.SetApparelTint(slot, color);
        public static void SetLocalModelSlider(string propertyName, float value)
            => PresenceService.Active?.SetSlider(propertyName, value);
        public static void SetLocalPlayerModel(bool female, int modelIndex)
            => PresenceService.Active?.SetModel(female, modelIndex);
        public static void SetNsfwAllowed(bool allowed) => PresenceService.Active?.SetNsfwAllowed(allowed);

        public static void SendEmote() => PresenceClientBehaviour.Active?.SendEmote();
        public static void SendActivity(EItemType pack) => PresenceClientBehaviour.Active?.SendActivity(pack);
    }
}
