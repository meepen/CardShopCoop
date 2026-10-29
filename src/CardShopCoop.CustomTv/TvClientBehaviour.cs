using System;
using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Net.Connection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.CustomTv
{
    /// <summary>Client playback mirror and immediately applied control forwarding.</summary>
    [ClientBehaviour]
    public sealed class TvClientBehaviour : CoopBehaviour
    {
        private const byte Next = 1;
        private const byte Previous = 2;
        private const byte Pause = 3;
        private const byte Power = 4;
        private const byte Shuffle = 5;
        private const byte Seek = 6;
        private const byte Open = 7;

        private static TvClientBehaviour _active;
        private bool _shutdown;
        private bool _initialized;
        private bool _joined;

        private void OnEnable()
        {
            if (_shutdown || _initialized)
            {
                return;
            }

            try
            {
                ResetSessionState();
                SceneManager.sceneLoaded += OnSceneLoaded;
                _active = this;
                ApplyPatches();
            }
            catch (Exception exception)
            {
                CoopLog.Error("TV client initialization failed: " + exception);
                SceneManager.sceneLoaded -= OnSceneLoaded;
                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }
                ResetSessionState();
                throw;
            }
            _initialized = true;
        }

        [OnFullyJoined]
        private void MarkJoined(PeerConnection _) => _joined = true;

        [ExtendedInformationHandler]
        private void OnTvState(InteractableObject tv, InformationChange<TvState> change)
        {
            if (_shutdown || !TvInterop.Present || tv == null || change.Removed)
            {
                return;
            }

            var controller = TvInterop.ControllerFor(tv);
            if (controller == null)
            {
                CoopLog.Warn("TV state arrived before its VideoPlayerController: " + tv.name);
                return;
            }

            ApplyTvState(controller, change.Current, change.Previous);
        }

        private void ApplyTvState(Component controller, TvState state, TvState previous)
        {
            if (_shutdown || !Context.InGame
                || !TvInterop.Present || TvInterop.ApplyingRemote)
            {
                return;
            }

            if (state == null || ReferenceEquals(state, previous)
                || (previous != null && state.Generation < previous.Generation))
            {
                return;
            }

            TvInterop.BeginFocus(controller);
            try
            {
                if (!string.IsNullOrWhiteSpace(state.SourceUrl) && !TvInterop.EnrollmentReady)
                {
                    TvInterop.RequestClientEnrollment();
                    return;
                }

                ApplyState(state);
            }
            finally
            {
                TvInterop.EndFocus();
            }
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }
            ResetSessionState();
        }

        private void OnDestroy() => Shutdown();

        private void ResetSessionState()
        {
            TvInterop.ResetState();
            _joined = false;
        }

        private void OnSceneLoaded(Scene _, LoadSceneMode mode)
        {
            // Only a full world replacement invalidates the controller and channel caches. An
            // additive scene (the character-prefab preload) loads next to the live shop and must
            // not clear the state of the TVs already in it.
            if (mode == LoadSceneMode.Single)
            {
                TvInterop.ResetState();
            }
        }

        [OnClientDisconnected]
        private void ForgetHost(PeerConnection connection, DisconnectInfo info)
        {
            if (connection?.Id == 1)
            {
                ResetSessionState();
            }
        }

        private static void ApplyState(TvState state)
        {
            switch (state.Operation)
            {
                case TvStateOperations.Open:
                case TvStateOperations.Playlist:
                case TvStateOperations.Next:
                case TvStateOperations.Previous:
                    ApplyMediaState(state);
                    break;

                case TvStateOperations.Pause:
                    TvInterop.ApplyPaused(state.Paused);
                    break;

                case TvStateOperations.Power:
                    TvInterop.ApplyPowered(state.PoweredOff);
                    break;

                case TvStateOperations.Shuffle:
                    TvInterop.ApplyShuffle(state.Shuffle);
                    break;

                case TvStateOperations.Seek:
                    TvInterop.ApplySeek(state.Position);
                    break;

                case TvStateOperations.Barrier:
                    TvInterop.ApplyBarrier(state.Barrier, state.Resume);
                    break;

                case TvStateOperations.Error:
                    TvInterop.ApplyPlaybackError();
                    break;

                default:
                    ApplyMediaState(state);
                    break;
            }
        }

        private static void ApplyMediaState(TvState state)
        {
            TvInterop.ApplyMediaState(state.SourceUrl, state.StreamUrl, state.StreamTitle,
                state.PlaylistUrl, state.IsLive, state.IsSegmentedVod, state.PlaylistIndex,
                state.Position, state.Paused, state.PoweredOff, state.Shuffle,
                state.Barrier, state.Resume, state.Generation);
        }

        private void ApplyPatches()
        {
            if (!TvInterop.Present)
            {
                return;
            }

            TryPatch(TvInterop.OptionalControllerType, "OnEnable", null,
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(ControllerLifecyclePostfix)));
            TryPatch(TvInterop.OptionalControllerType, "Start", null,
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(ControllerLifecyclePostfix)));
            TryPatch(TvInterop.OptionalControllerType, "ChangeVideo", null,
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(ControllerLifecyclePostfix)));
            TryPatch(TvInterop.OptionalControllerType, "ChangeToUrl", null,
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(ChangeToUrlPostfix)));
            TryPatch(TvInterop.OptionalControllerType, "StreamYouTube", null,
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(StreamYouTubePostfix)));
            // The game performs every control through its own path; these observe the resulting
            // local change and register one post-hoc action. No vanilla input is suppressed.
            TryPatch(TvInterop.OptionalControllerType, "HandleGlobalInput",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(GlobalInputPrefix)),
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(GlobalInputPostfix)));
            TryPatch(TvInterop.OptionalControllerType, "OnQualityChoice",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(QualityPrefix)),
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(QualityPostfix)));
            TryPatch(TvInterop.OptionalControllerType, "ToggleGlobalPause",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(CapturePrefix)),
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(PausePostfix)));
            TryPatch(TvInterop.OptionalControllerType, "RemoteNext",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(CapturePrefix)),
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(NextPostfix)));
            TryPatch(TvInterop.OptionalControllerType, "RemotePrev",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(CapturePrefix)),
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(PreviousPostfix)));
            TryPatch(TvInterop.OptionalControllerType, "RemoteShuffle",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(CapturePrefix)),
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(ShufflePostfix)));
            TryPatch(TvInterop.OptionalControllerType, "RemotePower",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(CapturePrefix)),
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(PowerPostfix)));
            TryPatch(TvInterop.OptionalControllerType, "OnLocalPrepared",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(LocalPreparedPrefix)),
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(LocalPreparedPostfix)));
            TryPatch(TvInterop.OptionalControllerType, "OnDisable", null,
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(ControllerDisablePostfix)));
        }

        private static void LocalPreparedPrefix(object __instance, object source)
        {
            var controller = __instance as Component;
            TvInterop.RegisterController(controller);
            if (_active == null || controller == null)
            {
                return;
            }

            TvInterop.BeginFocus(controller);
            try
            {
                _active.BeforeLocalPrepared(controller, source);
            }
            finally
            {
                TvInterop.EndFocus();
            }
        }

        private static void LocalPreparedPostfix(object __instance, object source)
        {
            var controller = __instance as Component;
            TvInterop.RegisterController(controller);
            if (_active == null || controller == null)
            {
                return;
            }

            TvInterop.BeginFocus(controller);
            try
            {
                _active.AfterLocalPrepared(controller, source);
            }
            finally
            {
                TvInterop.EndFocus();
            }
        }

        private static void ControllerLifecyclePostfix(object __instance)
            => TvInterop.RegisterController(__instance as Component);

        private static void ChangeToUrlPostfix(object __instance, string url, string title)
        {
            var controller = __instance as Component;
            TvInterop.RegisterController(controller);
            TvInterop.ChangeToUrlPostfix(controller, url);
        }

        private static void StreamYouTubePostfix(object __instance, string url)
        {
            var controller = __instance as Component;
            TvInterop.RegisterController(controller);
            TvInterop.StreamYouTubePostfix(controller, url);
        }

        private void BeforeLocalPrepared(Component controller, object source)
        {
            var owner = TvInterop.OwnerOf(controller);
            if (owner == null)
            {
                return;
            }

            if (owner.GetExtendedInformation<TvState>()?.Barrier == true
                && TvInterop.IsMainPreparedPlayer(controller, source))
            {
                TvInterop.SetSharedPausedForPrepare(false);
            }
        }

        private void AfterLocalPrepared(Component controller, object source)
        {
            var owner = TvInterop.OwnerOf(controller);
            if (owner == null || !TvInterop.IsMainPreparedPlayer(controller, source))
            {
                return;
            }

            TvInterop.ApplyPreparedPosition(controller);
            if (owner.GetExtendedInformation<TvState>()?.Barrier == true)
            {
                TvInterop.PausePreparedStream(controller);
            }
        }

        private static bool CanAct()
        {
            return _active != null && !_active._shutdown && _active._joined
                && _active.Context.InGame && TvInterop.Present;
        }

        private static void SendAction(byte op, string url = null, string title = null,
            double value = 0, TvInterop.PlaybackUndoState previous = default,
            double undoSeek = double.NaN)
        {
            if (!CanAct())
            {
                return;
            }

            var tv = TvInterop.OwnerOf(TvInterop.ActionController);
            if (tv == null)
            {
                return;
            }

            _active.Context.Send(_active.Context.HostConnection, new TvOpMessage
            {
                Tv = tv,
                Op = op,
                Url = url,
                Title = title,
                Value = value,
                Previous = previous,
                UndoSeek = undoSeek,
            });
        }

        /// <summary>Captures the pending URL before the game clears it, so the Open the quality
        /// dialog launches can be observed and forwarded with its original (pre-resolution) URL.</summary>
        private struct QualityState
        {
            public bool Armed;
            public string Url;
            public TvInterop.PlaybackUndoState Prior;
        }

        private static void QualityPrefix(object __instance, out QualityState __state)
        {
            var controller = __instance as Component;
            TvInterop.RegisterController(controller);
            __state = default;
            if (!CanAct() || TvInterop.ApplyingRemote || controller == null)
            {
                return;
            }

            TvInterop.BeginFocus(controller);
            try
            {
                var url = TvInterop.PendingUrl;
                if (string.IsNullOrWhiteSpace(url))
                {
                    return;
                }

                __state.Armed = true;
                __state.Url = url;
                __state.Prior = TvInterop.CapturePlaybackState();
            }
            finally
            {
                TvInterop.EndFocus();
            }
        }

        private static void QualityPostfix(object __instance, QualityState __state)
        {
            var controller = __instance as Component;
            TvInterop.RegisterController(controller);
            if (!__state.Armed || !CanAct() || TvInterop.ApplyingRemote || controller == null)
            {
                return;
            }

            TvInterop.BeginFocus(controller);
            try
            {
                ForwardMedia(Open, __state.Prior, __state.Url, "Shared stream");
            }
            finally
            {
                TvInterop.EndFocus();
            }
        }

        /// <summary>Observed keyboard controls. The game itself handles Alt+P/=/−/]/[ through its
        /// own <c>HandleGlobalInput</c>; this hook only forwards the changes it produces. Alt+S and
        /// Alt+O drive shuffle/power, which vanilla has no keys for, so their game methods are
        /// invoked here and the resulting change is observed like any other.</summary>
        private struct GlobalInputState
        {
            public bool Next;
            public bool Previous;
            public bool Shuffle;
            public bool Power;
            public TvInterop.PlaybackUndoState Prior;
        }

        // The seek keys are held, and the game advances the video position on its own cadence.
        // Track the position the game reports so each observed jump is forwarded once; the value is
        // read after the game has applied the previous frame's jump.
        private static double _seekObservedPosition;
        private static bool _seekHeld;

        private static void GlobalInputPrefix(object __instance, out GlobalInputState __state)
        {
            var controller = __instance as Component;
            __state = default;
            if (!TvInterop.IsSoundController(controller))
            {
                return;
            }

            TvInterop.RegisterController(controller);
            TvInterop.BeginFocus(controller);
            try
            {
                var alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
                var now = TvInterop.Position;
                var seekHeld = alt
                    && (Input.GetKey(KeyCode.RightBracket) || Input.GetKey(KeyCode.LeftBracket));
                if (CanAct() && !TvInterop.ApplyingRemote && _seekHeld
                    && Math.Abs(now - _seekObservedPosition) >= 1.0)
                {
                    ForwardSeek(_seekObservedPosition, now - _seekObservedPosition);
                }

                _seekObservedPosition = now;
                _seekHeld = seekHeld;

                if (!CanAct() || TvInterop.ApplyingRemote || !alt)
                {
                    return;
                }

                if (Input.GetKeyDown(KeyCode.Equals))
                {
                    __state.Next = true;
                }
                else if (Input.GetKeyDown(KeyCode.Minus))
                {
                    __state.Previous = true;
                }
                else if (Input.GetKeyDown(KeyCode.S))
                {
                    __state.Shuffle = true;
                }
                else if (Input.GetKeyDown(KeyCode.O))
                {
                    __state.Power = true;
                }
                else
                {
                    return;
                }

                __state.Prior = TvInterop.CapturePlaybackState();
            }
            finally
            {
                TvInterop.EndFocus();
            }
        }

        private static void GlobalInputPostfix(object __instance, GlobalInputState __state)
        {
            var controller = __instance as Component;
            if (!CanAct() || TvInterop.ApplyingRemote
                || !TvInterop.IsSoundController(controller))
            {
                return;
            }

            TvInterop.RegisterController(controller);
            TvInterop.BeginFocus(controller);
            try
            {
                if (__state.Next)
                {
                    ForwardMedia(Next, __state.Prior, null, null);
                }
                else if (__state.Previous)
                {
                    ForwardMedia(Previous, __state.Prior, null, null);
                }
                else if (__state.Shuffle)
                {
                    ForwardSelfApplied(Shuffle, __state.Prior);
                }
                else if (__state.Power)
                {
                    ForwardSelfApplied(Power, __state.Prior);
                }
            }
            finally
            {
                TvInterop.EndFocus();
            }
        }

        /// <summary>Captures the playback state before a control the game performs itself, so the
        /// postfix can register one observed action and replay it if the host rejects it.</summary>
        private static void CapturePrefix(object __instance, out TvInterop.PlaybackUndoState __state)
        {
            var controller = __instance as Component;
            TvInterop.RegisterController(controller);
            if (controller == null)
            {
                __state = default;
                return;
            }

            TvInterop.BeginFocus(controller);
            try
            {
                __state = TvInterop.CapturePlaybackState();
            }
            finally
            {
                TvInterop.EndFocus();
            }
        }

        private static void ControllerDisablePostfix(object __instance)
            => TvInterop.ForgetController(__instance as Component);

        private static void PausePostfix(TvInterop.PlaybackUndoState __state)
        {
            if (!CanAct() || TvInterop.ApplyingRemote || TvInterop.Paused == __state.Paused)
            {
                return;
            }

            SendAction(Pause, previous: __state);
        }

        private static void PowerPostfix(TvInterop.PlaybackUndoState __state)
        {
            if (!CanAct() || TvInterop.ApplyingRemote
                || TvInterop.PoweredOff == __state.PoweredOff)
            {
                return;
            }

            SendAction(Power, previous: __state);
        }

        private static void ShufflePostfix(TvInterop.PlaybackUndoState __state)
        {
            if (!CanAct() || TvInterop.ApplyingRemote || TvInterop.Shuffle == __state.Shuffle)
            {
                return;
            }

            SendAction(Shuffle, previous: __state);
        }

        private static void NextPostfix(TvInterop.PlaybackUndoState __state)
            => ForwardMedia(Next, __state, null, null);

        private static void PreviousPostfix(TvInterop.PlaybackUndoState __state)
            => ForwardMedia(Previous, __state, null, null);

        /// <summary>Records one observed media change (next/previous/open) the game already applied
        /// and sends it; the apply/undo closures replay it through the game path on a rejection.</summary>
        private static void ForwardMedia(byte op, TvInterop.PlaybackUndoState prior, string url,
            string title)
        {
            if (!CanAct() || TvInterop.ApplyingRemote)
            {
                return;
            }

            SendAction(op, url, title, previous: prior);
        }

        /// <summary>Shuffle/power have no vanilla key binding: drive the game's own control for the
        /// observed hotkey, then register the change it made. The game call runs under
        /// <c>ApplyingRemote</c>, so the control postfix does not also forward it.</summary>
        private static void ForwardSelfApplied(byte op, TvInterop.PlaybackUndoState prior)
        {
            if (!TvInterop.ApplyOperation(op, null, null, 0))
            {
                return;
            }

            SendAction(op, previous: prior);
        }

        private static void ForwardSeek(double fromPosition, double delta)
        {
            SendAction(Seek, value: delta, undoSeek: fromPosition);
        }
    }
}
