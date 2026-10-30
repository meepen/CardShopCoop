using System.Collections.Generic;
using System;
using CardShopCoop.Api;
using CardShopCoop.Net;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using UnityEngine;

namespace CardShopCoop.Modules.Presence
{
    /// <summary>One player in the session, for the window's roster. <see cref="Id"/> is the host's
    /// canonical connection id (0 is the host on its own screen; 1 is the host as a client sees it).
    /// </summary>
    public readonly struct PresencePlayer
    {
        public PresencePlayer(int id, string name, bool isHost, bool isSelf)
        {
            Id = id;
            Name = name;
            IsHost = isHost;
            IsSelf = isSelf;
        }

        public int Id
        {
            get;
        }
        public string Name
        {
            get;
        }
        public bool IsHost
        {
            get;
        }
        public bool IsSelf
        {
            get;
        }
    }

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

        /// <summary>Everyone in the session, host first, then this player, then the other clients.
        /// Empty when no session is active. Allocates, so the window snapshots it once per layout.</summary>
        public static IReadOnlyList<PresencePlayer> SessionPlayers
        {
            get
            {
                var host = PresenceHostBehaviour.Active;
                if (host != null)
                {
                    return host.BuildPlayers();
                }

                var client = PresenceClientBehaviour.Active;
                return client != null ? client.BuildPlayers() : Array.Empty<PresencePlayer>();
            }
        }

        internal static void ConfigureIdentity(Func<string> localName)
        {
            _localName = localName;
        }

        internal static void SetPeerName(int connectionId, string name)
        {
            if (connectionId <= 0)
            {
                return;
            }


            Names[connectionId] = string.IsNullOrWhiteSpace(name) ? "Player " + connectionId : name;
            NotifyRosterChanged();
        }

        /// <summary>Copies the session's normalized name onto the public connection object and
        /// refreshes the peer identity every view reads (<see cref="ICoopPeer.Player"/>,
        /// <see cref="ICoopPeer.IsHost"/>, <see cref="ICoopPeer.IsSelf"/>).</summary>
        internal static void ApplyName(PeerConnection connection)
        {
            if (connection != null)
            {
                connection.Name = PeerName(connection.Id);
                PeerHandle.BindIdentity(connection);
            }
        }

        /// <summary>Resolves a host-canonical player id (0 = host, 1..N = clients) from the
        /// current roster. Null when the roster does not know it.</summary>
        internal static PlayerRef ResolvePlayerRef(int canonicalId)
        {
            var players = SessionPlayers;
            for (var i = 0; i < players.Count; i++)
            {
                var player = players[i];
                var id = player.IsHost ? 0 : player.Id;
                if (id == canonicalId)
                {
                    return new PlayerRef(id, player.Name, player.IsHost, player.IsSelf);
                }
            }

            return null;
        }

        internal static void RemovePeer(int connectionId)
        {
            if (Names.Remove(connectionId))
            {
                NotifyRosterChanged();
            }
        }

        internal static void ClearPeerNames()
        {
            Names.Clear();
            NotifyRosterChanged();
        }

        /// <summary>A copy of one session member's appearance, keyed by the roster connection id
        /// (0 is the host on its own screen; 1 is the host as a client sees it). False when no
        /// model has arrived yet. Intended for reporting, not for rendering.</summary>
        public static bool TryGetSessionModel(int connectionId, out PresenceModelEntry model)
        {
            var host = PresenceHostBehaviour.Active;
            if (host != null)
            {
                return host.TryGetModel(connectionId, out model);
            }

            var client = PresenceClientBehaviour.Active;
            if (client != null)
            {
                return client.TryGetModel(connectionId, out model);
            }

            model = null;
            return false;
        }

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

        /// <summary>Latest received camera pose for a holder, without the avatar interpolation
        /// delay. Placement targets are re-evaluated every game frame, so using the newest sample
        /// keeps the ghost closer to the mover's actual aim.</summary>
        public static bool TryGetLatestPeerCameraForHolder(int holderConnectionId,
            out Vector3 position, out Quaternion rotation)
        {
            position = default(Vector3);
            rotation = Quaternion.identity;
            var service = PresenceService.Active;
            return service != null
                && service.TryGetLatestPeerCameraForHolder(holderConnectionId, out position,
                    out rotation);
        }

        /// <summary>Chest-bone anchor of the avatar holding a box (holder id in the sender's
        /// connection space; 0 means the host). Used to ride the real carried box on the avatar.</summary>
        public static bool TryGetRemoteCarryAnchor(int holderConnectionId, out Transform anchor)
        {
            anchor = null;
            var service = PresenceService.Active;
            return service != null && service.TryGetRemoteCarryAnchor(holderConnectionId, out anchor);
        }

        /// <summary>Raised when a remote avatar body has just spawned. A feature that parents an
        /// object to an avatar skeleton (a carried box) can be announced before that avatar exists
        /// on this machine, so it subscribes here to re-resolve its anchor once one does.</summary>
        public static event Action RemoteAvatarsChanged;

        internal static void NotifyRemoteAvatarsChanged() => RemoteAvatarsChanged?.Invoke();

        /// <summary>Raised when the session roster (who is in the shop) changes. The co-op window
        /// uses it instead of re-reading the roster on a timer.</summary>
        public static event Action RosterChanged;

        internal static void NotifyRosterChanged() => RosterChanged?.Invoke();

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

        /// <summary>Changes whenever the wardrobe editor template is (re)created or torn down.
        /// The window snapshots the template, so it must refresh when this moves.</summary>
        public static int EditorGeneration => PresenceService.Active?.EditorGeneration ?? 0;

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

        /// <summary>Wardrobe slots this body can actually dress (table count bounded by the
        /// runtime collections). The window must not offer a row beyond this.</summary>
        internal static int DressableHairSlots(CC.CharacterCustomization custom)
            => CharacterClone.DressableHairSlots(custom);

        internal static int DressableApparelSlots(CC.CharacterCustomization custom)
            => CharacterClone.DressableApparelSlots(custom);

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
