using System;
using CardShopCoop.Net.Connection;

namespace CardShopCoop.Modules.SaveTransfer
{
    /// <summary>
    /// The deliberately small bridge between Core's authenticated session lifecycle and the
    /// save-transfer owners.  Core owns authentication and connection state; this module owns
    /// every byte after authorization and exposes only the state needed by save/input guards.
    ///
    /// Core supplies lifecycle and transport callbacks at the session boundary. The transfer
    /// implementation does not reach back into Core for feature state or synchronization locks.
    /// </summary>
    public static class SaveTransferRuntime
    {
        private static readonly object DefaultTransferLock = new();
        private static readonly object StateLock = new();

        private static Func<CoopRole> _role;
        private static Func<int> _generation;
        private static Func<int, bool> _generationIsCurrent;
        private static Func<Action, bool> _enqueue;
        private static object _transferLock;
        private static Action _fullyJoinedEmitter;
        private static Action<string> _failureReporter;
        private static Action<PeerConnection, string> _transferFailure;
        private static bool _borrowedWorld;
        private static bool _reloading;

        /// <summary>True while vanilla is rebuilding the borrowed world.</summary>
        public static bool ClientReloading
        {
            get
            {
                lock (StateLock)
                {
                    return _reloading;
                }
            }
        }

        /// <summary>True from client authorization until the guest is safely back at Title.</summary>
        public static bool GuestBorrowedWorld
        {
            get
            {
                lock (StateLock)
                {
                    return _borrowedWorld;
                }
            }
        }

        /// <summary>Called by Core during the migration. All arguments are optional.</summary>
        public static void ConfigureCore(Func<CoopRole> role = null,
            Func<int> generation = null, Func<int, bool> isGenerationCurrent = null,
            Func<Action, bool> enqueueMainThread = null, object transferLock = null,
            Action fullyJoinedEmitter = null, Action<string> failureReporter = null,
            Action<PeerConnection, string> transferFailure = null)
        {
            _role = role;
            _generation = generation;
            _generationIsCurrent = isGenerationCurrent;
            _enqueue = enqueueMainThread;
            _transferLock = transferLock;
            _fullyJoinedEmitter = fullyJoinedEmitter;
            _failureReporter = failureReporter;
            _transferFailure = transferFailure;
        }

        /// <summary>Core calls this when an authenticated client begins borrowing a world.</summary>
        public static void SetGuestBorrowedWorld(bool borrowed)
        {
            lock (StateLock)
            {
                _borrowedWorld = borrowed;
            }
        }

        /// <summary>Only clear the process latch after the client is actually out of a level.</summary>
        public static void ClearBorrowedWorldIfSafe(bool inGame)
        {
            if (inGame)
            {
                return;
            }

            SetGuestBorrowedWorld(false);
        }

        internal static void SetReloading(bool reloading)
        {
            lock (StateLock)
            {
                _reloading = reloading;
            }
        }

        internal static CoopRole CurrentRole
        {
            get
            {
                if (_role != null)
                {
                    return _role();
                }

                return CoopRole.None;
            }
        }

        internal static bool CanWriteSave
        {
            get
            {
                var borrowed = GuestBorrowedWorld;
                return CurrentRole != CoopRole.Client && !borrowed;
            }
        }

        internal static int SessionGeneration
        {
            get
            {
                return _generation == null ? -1 : _generation();
            }
        }

        internal static bool IsSessionGeneration(int generation)
        {
            if (_generationIsCurrent != null)
            {
                return _generationIsCurrent(generation);
            }

            return false;
        }

        internal static bool TryEnqueueMainThread(Action action)
        {
            if (action == null)
            {
                return false;
            }

            if (_enqueue != null)
            {
                return _enqueue(action);
            }

            return false;
        }

        internal static object TransferLock => _transferLock ?? DefaultTransferLock;

        internal static void ReportFailure(string reason)
        {
            CoopPlugin.Log.LogError("coop: save transfer failed: " + reason);
            _failureReporter?.Invoke(reason);
        }

        internal static void FailTransfer(PeerConnection connection, string reason)
        {
            ReportFailure(reason);
            _transferFailure?.Invoke(connection, reason);
        }

        internal static void EmitFullyJoined()
        {
            if (_fullyJoinedEmitter != null)
            {
                _fullyJoinedEmitter();
                return;
            }

            // Compatibility seam for the staged migration. Core remains the owner of the
            // handshake DTO and connection transition; this call only asks it to emit the
            // already-authenticated completion signal through the normal context sender.
            SaveTransferClientBehaviour.EmitFullyJoinedFallback();
        }

        internal static void ResetForShutdown()
        {
            SetReloading(false);
            if (CurrentRole != CoopRole.Client)
            {
                SetGuestBorrowedWorld(false);
            }
        }

    }
}
