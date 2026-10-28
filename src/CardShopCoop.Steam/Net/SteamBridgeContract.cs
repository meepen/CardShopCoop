using System;
using CardShopCoop.Net.Datagrams;

namespace CardShopCoop.Net
{
    /// <summary>Steam lobby size limits exposed without crossing the Steamworks boundary.</summary>
    public static class SteamLobbyLimits
    {
        public const int DefaultPlayers = 4;
        public const int MinPlayers = 2;
        public const int MaxPlayers = 250;
    }

    /// <summary>
    /// Everything CoopCore and the co-op UI are allowed to know about Steam. Every member is
    /// Steam-free BY CONSTRUCTION - ulong / string / bool / int / Action / Action&lt;ulong&gt;
    /// / IDatagramTransport - so the always-loaded types may reference
    /// this interface as freely as they like. The CLR resolves the implementing type only
    /// at runtime, and only on a build where it can actually load.
    ///
    /// A null ISteamBridge means the Steamworks wrapper is NOT USABLE here - either the
    /// assembly is absent (DRM-free) or it is the Game Pass stub without the real types - and
    /// Steam can never work on this build: hide the UI, never show an error. That is a
    /// different thing from <see cref="SteamAvailable"/> returning false, which means
    /// "Steam is installed but the client isn't running" - a state the player can fix, and
    /// which keeps today's error message. Never conflate the two.
    /// </summary>
    public interface ISteamBridge
    {
        /// <summary>Create the long-lived lobby callbacks. Call once, at CoopCore.Awake:
        /// the invite-accepted callback must be listening from startup so accepting an
        /// invite at any moment (or +connect_lobby) starts the join flow.</summary>
        void Init();

        /// <summary>Is the Steam CLIENT running? (The assembly being present is a
        /// separate question, already answered by this object being non-null.)</summary>
        bool SteamAvailable();

        /// <summary>Local Steam persona, or an empty string when unavailable.</summary>
        string LocalPersonaName
        {
            get;
        }

        /// <summary>Local Steam identity, or zero when Steam is unavailable.</summary>
        ulong LocalSteamId
        {
            get;
        }

        /// <summary>Local nickname for a Steam friend, or an empty string when the
        /// friend is not known locally or Steam is unavailable.</summary>
        string FriendNickname(ulong steamId);

        /// <summary>Build the datagram bearer used for this Steam session. The runtime creates the
        /// reliable session around it and owns it; the bridge binds the Steam sockets bearer when
        /// the lobby lifecycle supplies the host identity that bearer needs.</summary>
        IDatagramTransport CreateDatagramTransport();

        void Host(bool isPublic, string lobbyName, bool hasPassword, int maxPlayers);
        long Join(ulong lobbyId);
        void Leave();
        void OpenInviteDialog();

        /// <summary>Opens Steam's Game Servers window in the overlay. This is
        /// <c>ActivateGameOverlay("servers")</c> - an UNDOCUMENTED dialog name (the wrapper's enum
        /// lists only friends/community/players/settings/officialgamegroup/stats/achievements), but
        /// the one the overlay's own dispatch switch maps to its GameServers window. There is no
        /// lobby-list window at all, so this is the only browser Steam offers.</summary>
        void OpenGameServers();

        Action<string> OnError
        {
            get; set;
        }

        /// <summary>Host: the lobby exists and the transport is already wired to it;
        /// arg = the created lobby id. Steam-free by construction, like every other member
        /// here - the id is the raw ulong, never a CSteamID.</summary>
        Action<ulong> OnLobbyLive
        {
            get; set;
        }

        /// <summary>Client: we entered a lobby and the bearer is already wired to its
        /// owner. The ROLE CHECK IS THE SUBSCRIBER'S JOB - see CoopCore. The bearer
        /// argument identifies the session that produced this asynchronous callback.</summary>
        Action<IDatagramTransport> OnConnectedToHost
        {
            get; set;
        }

        /// <summary>Client: a current lobby join was rejected. The operation and
        /// bearer identify the exact attempt so a late callback cannot tear down a
        /// newer session.</summary>
        Action<ulong, long, IDatagramTransport, string> OnJoinFailed
        {
            get; set;
        }

        /// <summary>A current Steam session failed before it became usable (for example,
        /// asynchronous lobby creation or host bearer startup). The bearer argument
        /// identifies the exact session and prevents a late Steam callback from failing a
        /// newer one.</summary>
        Action<IDatagramTransport, string> OnSessionFailed
        {
            get; set;
        }

        /// <summary>The local player accepted somebody's invite; arg = lobby id.</summary>
        Action<ulong> OnInviteAccepted
        {
            get; set;
        }
    }

}
