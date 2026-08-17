using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace CardShopCoop.Net
{
    /// <summary>
    /// THE STEAM ISOLATION BOUNDARY.
    ///
    /// The Game Pass build of Card Shop Simulator ships NO
    /// com.rlabrecque.steamworks.net.dll. The CLR's failure mode for that is brutal and
    /// non-obvious: a value-type (struct) FIELD whose type lives in a missing assembly
    /// kills the load of the whole CONTAINING TYPE, and a method body that merely
    /// mentions such a type (including generic instantiations like Action&lt;CSteamID&gt;
    /// or List&lt;SteamLobby.LobbyRow&gt;) throws at JIT the first time it is called.
    /// A try/catch in the SAME method never catches that - the throw happens before the
    /// body runs, so the catch must live in a CALLER whose own body is clean.
    ///
    /// So: nothing in this file may EVER name a Steamworks type. No `using Steamworks;`,
    /// no CSteamID, not even in a generic argument. Deleting that using statement from
    /// CoopCore.cs and CoopUI.cs is what turns "did I miss a reference?" into a compiler
    /// error, and this file is what lets those two compile without it.
    ///
    /// Everything Steam-typed lives behind <see cref="ISteamBridge"/>, whose only
    /// implementation (SteamBridgeImpl, in SteamNet.cs) is allowed to fail to load. The
    /// single path that reaches it is <see cref="SteamBridge.TryCreate"/>.
    /// </summary>
    /// <remarks>
    /// Steam-free mirror of SteamLobby.LobbyRow. Identical shape except that the lobby
    /// id is the raw ulong instead of a CSteamID - which is precisely the point: CoopUI
    /// keeps a List&lt;LobbyRow&gt; local, and a List of a struct that cannot load takes
    /// the whole method with it. SteamBridgeImpl converts.
    /// </remarks>
    public struct LobbyRow
    {
        public ulong Id;
        public string Name;
        public int Players;
        public int Max;
        public bool HasPw;
        public string Ver;
    }

    /// <summary>
    /// Everything CoopCore and CoopUI are allowed to know about Steam. Every member is
    /// Steam-free BY CONSTRUCTION - ulong / string / bool / int / Action / Action&lt;ulong&gt;
    /// / List&lt;LobbyRow&gt; / ICoopTransport - so the always-loaded types may reference
    /// this interface as freely as they like. The CLR resolves the implementing type only
    /// at runtime, and only on a build where it can actually load.
    ///
    /// A null ISteamBridge means "the Steamworks ASSEMBLY is absent" (Game Pass / DRM-free)
    /// and Steam can never work here - hide the UI, never show an error. That is a
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

        /// <summary>Build the Steam P2P transport. MUST be called BEFORE Host()/Join():
        /// the lobby callbacks inside the bridge write the lobby id (and, on the client,
        /// the host connection) into the transport this returns. Reorder these two and
        /// the callback fires against a null transport and the Steam path silently never
        /// connects - on the Steam build, where nobody is looking for it.</summary>
        ICoopTransport CreateTransport(bool isHost, byte[] keepalive);

        void Host(bool isPublic, string lobbyName, bool hasPassword);
        void Join(ulong lobbyId);
        void Leave();
        void OpenInviteDialog();
        void RefreshList();
        bool ListRefreshing { get; }

        /// <summary>Public-lobby browser rows, already converted to the Steam-free
        /// <see cref="LobbyRow"/>. The list is reused between calls (OnGUI polls it every
        /// frame); treat it as read-only and never hold it across frames.
        ///
        /// THE GETTER REBUILDS THAT ONE SHARED LIST ON EVERY READ - it is not a property
        /// access, it is a clear-and-refill of the same instance. So: read it ONCE per
        /// frame into a local and work off that, and NEVER enumerate it re-entrantly (a
        /// second read taken while a foreach over the first is still running clears the
        /// list out from under that foreach - InvalidOperationException, from a getter
        /// that looks like a plain field).</summary>
        List<LobbyRow> Lobbies { get; }

        Action<string> OnError { get; set; }

        /// <summary>Host: the lobby exists and the transport is already wired to it;
        /// arg = the created lobby id. Steam-free by construction, like every other member
        /// here - the id is the raw ulong, never a CSteamID.</summary>
        Action<ulong> OnLobbyLive { get; set; }

        /// <summary>Client: we entered a lobby and the transport is already wired to its
        /// owner. The ROLE CHECK IS THE SUBSCRIBER'S JOB - see CoopCore.</summary>
        Action OnConnectedToHost { get; set; }

        /// <summary>The local player accepted somebody's invite; arg = lobby id.</summary>
        Action<ulong> OnInviteAccepted { get; set; }
    }

    /// <summary>
    /// Reflection-only presence test for com.rlabrecque.steamworks.net. Cached once, never
    /// throws, and never names a Steamworks type in metadata - the assembly and the type
    /// are STRINGS, which is the whole trick.
    /// </summary>
    public static class PlatformProbe
    {
        private static int _state; // 0 = unknown, 1 = present, 2 = absent

        /// <summary>True when the Steamworks.NET assembly can be resolved in this process.
        /// Evaluated at most once per session.</summary>
        public static bool SteamworksPresent
        {
            get
            {
                if (_state == 0) _state = Detect() ? 1 : 2;
                return _state == 1;
            }
        }

        private static bool Detect()
        {
            // (a) Already loaded? Cheapest, and true on the Steam build once the game's
            //     Heathen integration has touched Steamworks. Matched by SIMPLE NAME, so
            //     it is immune to strong-name and version differences.
            try
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    if (string.Equals(a.GetName().Name, "com.rlabrecque.steamworks.net",
                                      StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            catch { }

            // (b) Not loaded YET? Force a partial bind. DO NOT "SIMPLIFY" THIS AWAY: our
            //     Awake can easily run before the game first touches Steamworks, so (a)
            //     alone would return a false negative on a real Steam install and hide the
            //     Steam UI permanently. This is the branch that actually fires on Steam.
            //     throwOnError:false - it must never throw on Game Pass.
            try
            {
                if (Type.GetType("Steamworks.SteamAPI, com.rlabrecque.steamworks.net", false) != null)
                    return true;
            }
            catch { }

            return false;
        }
    }

    /// <summary>Factory for the one and only ISteamBridge implementation.</summary>
    public static class SteamBridge
    {
        /// <summary>
        /// THE boundary call. Returns null on any build where Steam cannot work, and never
        /// throws.
        ///
        /// DO NOT REMOVE THE [MethodImpl(NoInlining)] ATTRIBUTES ON THIS METHOD OR ON
        /// <see cref="Create"/>. Both are tiny, and Mono's JIT inlines methods this size
        /// aggressively. Inlined, the `newobj SteamBridgeImpl` token migrates up into
        /// CoopCore.Awake, and the type-load fault moves with it - which kills the entire
        /// mod on Game Pass, with no code change that looks even slightly related.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static ISteamBridge TryCreate()
        {
            if (!PlatformProbe.SteamworksPresent) return null;
            try { return Create(); }
            catch (Exception e)
            {
                // Reached when the assembly probed present but the type still failed to
                // load (a wrong/partial Steamworks build). Warn, then degrade to LAN.
                CoopPlugin.Log.LogWarning("Steam bridge unavailable: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// Deliberately a separate method, and deliberately NoInlining. This is the ONLY
        /// method in the plugin outside SteamNet.cs that names a Steam-bearing type, so it
        /// is the only one that can fail to JIT. The try/catch that saves us therefore has
        /// to sit in TryCreate, whose own body is Steamworks-free: a try/catch wrapped
        /// around `new SteamBridgeImpl()` INSIDE one method would catch nothing at all,
        /// because the throw happens at JIT time, before the body (and its handlers) ever
        /// run. DO NOT MERGE THESE TWO METHODS.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ISteamBridge Create()
        {
            return new SteamBridgeImpl();
        }
    }
}
