using System;
using System.Collections.Generic;
using System.Threading;

namespace CardShopCoop.Net.Networking
{
    /// <summary>
    /// The set of networking backends this process offers: the built-in IP backend, plus any
    /// transport that registered itself at plugin load (the Steam transport). Backends live in
    /// their OWN assemblies and register through
    /// <see cref="Api.CoopApi.RegisterNetworkingBackend"/>, so this core assembly never references
    /// a transport library and a build shipping a same-named Steamworks stub cannot affect it.
    ///
    /// The IP backend is one category on purpose: it opens the router's port when it can and punches
    /// when it cannot, because those are two halves of the same reachability question, not two things
    /// a player should have to choose between.
    ///
    /// "Which backends exist" is decided here at startup. "Is a backend usable right now" is the
    /// separate, runtime question answered by <see cref="INetworkingBackend.IsAvailable"/>.
    ///
    /// THE OFFERED SET CHANGES AFTER STARTUP, EVEN THOUGH IT READS LIKE A CONSTANT. This core plugin
    /// is loaded first, and a transport plugin's Awake registers a moment later - the Steam
    /// transport is registered about 20 ms after this assembly's Awake has already run. Anything
    /// that reads the set once and caches it therefore never sees a contributed transport at all.
    /// <see cref="Version"/> exists so a cached snapshot can tell when it is stale.
    /// </summary>
    internal static class NetworkingBackends
    {
        private static readonly object Gate = new();
        private static readonly IpNetworkingBackend Ip = new IpNetworkingBackend();
        private static readonly List<INetworkingBackend> Registered = new();
        private static readonly Dictionary<INetworkingBackend, int> Bands = new();
        private static int _version;
        private static int _nextBand = NetworkingBands.Size;

        /// <summary>Changes whenever the offered set changes. Compare it against a cached snapshot
        /// rather than rebuilding (and allocating) the list on every read.</summary>
        public static int Version => Volatile.Read(ref _version);

        /// <summary>The built-in backends first, then registered transports in registration
        /// order. A fresh list each call, safe to hold.</summary>
        public static IReadOnlyList<INetworkingBackend> All()
        {
            lock (Gate)
            {
                var all = new List<INetworkingBackend>(Registered.Count + 1) { Ip };
                all.AddRange(Registered);
                return all;
            }
        }

        /// <summary>The disjoint PeerConnection.Id band assigned to a backend for this process.
        /// The runtime allocates the band; a backend never chooses one, so two backends cannot
        /// collide. The band is stable for the backend's registration lifetime.</summary>
        public static int BandOf(INetworkingBackend backend)
        {
            if (backend == null)
            {
                throw new ArgumentNullException(nameof(backend));
            }

            lock (Gate)
            {
                if (Bands.TryGetValue(backend, out var band))
                {
                    return band;
                }

                band = _nextBand;
                _nextBand += NetworkingBands.Size;
                Bands[backend] = band;
                return band;
            }
        }

        /// <summary>Adds a contributed backend. The returned handle removes it again. Idempotent.
        ///
        /// Do NOT dispose the handle from a plugin component's OnDestroy: BepInEx 5 has no unload
        /// path, so OnDestroy is not an unload signal - it also fires when something else destroys
        /// the shared manager object every plugin component lives on, and unregistering there
        /// permanently removes a transport that is still perfectly usable. See
        /// <see cref="Api.CoopApi.RegisterNetworkingBackend"/>.</summary>
        public static IDisposable Register(INetworkingBackend backend)
        {
            if (backend == null)
            {
                throw new ArgumentNullException(nameof(backend));
            }

            var added = false;
            lock (Gate)
            {
                if (!Registered.Contains(backend))
                {
                    Registered.Add(backend);
                    added = true;
                }
            }

            if (added)
            {
                CoopPlugin.Log?.LogInfo("networking backend registered: " + backend.Id
                    + " (" + backend.DisplayName + ")");
                Interlocked.Increment(ref _version);
                LogSet();
            }

            return new Registration(backend);
        }

        private static void Unregister(INetworkingBackend backend)
        {
            var removed = false;
            lock (Gate)
            {
                removed = Registered.Remove(backend);
            }

            if (removed)
            {
                CoopPlugin.Log?.LogInfo("networking backend unregistered: " + backend.Id);
                Interlocked.Increment(ref _version);
                LogSet();
            }
        }

        /// <summary>The whole offered set on one line, so the log answers "which transports does
        /// this process have" without the reader joining per-backend lines in their head. Logged
        /// once at startup and again after every change.</summary>
        public static void LogSet()
        {
            var all = All();
            var ids = new List<string>(all.Count);
            for (var i = 0; i < all.Count; i++)
            {
                ids.Add(all[i].Id);
            }

            CoopPlugin.Log?.LogInfo("[net] " + all.Count + " networking backend(s): "
                + string.Join(", ", ids));
        }

        private sealed class Registration : IDisposable
        {
            private INetworkingBackend _backend;

            internal Registration(INetworkingBackend backend)
            {
                _backend = backend;
            }

            public void Dispose()
            {
                var backend = _backend;
                if (backend == null)
                {
                    return;
                }

                _backend = null;
                Unregister(backend);
            }
        }
    }
}
