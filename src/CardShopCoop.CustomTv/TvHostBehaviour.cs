using System;
using CardShopCoop.Api;
using CardShopCoop.Attributes;
using CardShopCoop.Net;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardShopCoop.CustomTv
{
    /// <summary>Host-authoritative TV controls and event-driven playback deltas.</summary>
    [ServerBehaviour]
    public sealed class TvHostBehaviour : CoopBehaviour
    {
        private const byte Next = 1;
        private const byte Previous = 2;
        private const byte Pause = 3;
        private const byte Power = 4;
        private const byte Shuffle = 5;
        private const byte Seek = 6;
        private const byte Open = 7;

        private static TvHostBehaviour _active;
        private ICoopContext _context;
        private bool _shutdown;
        private bool _initialized;
        private float _lastSeekInputTime;

        private void OnEnable()
        {
            if (_shutdown || _initialized)
            {
                return;
            }

            _context = Context;
            try
            {
                ResetSessionState();
                _active = this;
                SceneManager.sceneLoaded += OnSceneLoaded;
                ApplyPatches(Harmony);

                // A TV created before the host module was installed never ran a patched
                // lifecycle method. Write its current state once so the EIM store has the entry
                // that the normal join baseline will carry to clients.
                foreach (var controller in TvInterop.Controllers())
                {
                    OnControllerLifecycle(controller);
                }
            }
            catch (Exception exception)
            {
                CoopLog.Error("TV host initialization failed: " + exception);
                SceneManager.sceneLoaded -= OnSceneLoaded;
                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }
                ResetSessionState();
                _context = null;
                throw;
            }
            _initialized = true;
        }

        [MessageHandler]
        private void HandleOperation(CoopMessageContext context, TvOpMessage message)
        {
            if (_shutdown || message == null || !TvInterop.Present)
            {
                return;
            }

            // OnApply already performed the command. Publish the resulting state under the
            // action id; the EIM IActionValue wrapper retires or reverses the sender's action.
            var controller = TvInterop.ControllerFor(message.Tv);
            if (controller == null)
            {
                return;
            }

            var operation = message.Op == Next ? TvStateOperations.Next
                : message.Op == Previous ? TvStateOperations.Previous
                : message.Op == Open ? TvStateOperations.Open
                : ScalarOperation(message.Op);
            var mediaOperation = message.Op == Next || message.Op == Previous
                || message.Op == Open;
            if (CaptureHostState(controller, operation, message.ActionId, mediaOperation,
                out var snapshot) && snapshot != null)
            {
                PublishState(controller, snapshot);
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
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        private void ResetSessionState()
        {
            TvInterop.ResetState();
            _lastSeekInputTime = -0.25f;
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

        private void OnControllerLifecycle(Component controller)
        {
            if (!TvInterop.Present)
            {
                return;
            }

            controller ??= TvInterop.CurrentController;
            if (controller == null || TvInterop.ApplyingRemote)
            {
                return;
            }

            // Attach to the TV that actually owns this VideoPlayer, not its channel master, so a
            // secondary TV's failure is observed and attributed to the correct TV.
            TvInterop.AttachVideoErrorHandler(controller, OnHostVideoError);
            var owner = TvInterop.OwnerOf(controller);
            if (owner == null)
            {
                return;
            }

            if (CaptureHostState(controller, TvStateOperations.Open, Guid.Empty, false,
                out var snapshot) && !string.IsNullOrEmpty(snapshot.SourceUrl))
            {
                PublishState(controller, snapshot);
            }
        }

        private bool CaptureHostState(Component controller, byte operation, Guid actionId,
            bool startBarrier, out TvState snapshot)
        {
            snapshot = null;
            var owner = TvInterop.OwnerOf(controller);
            if (controller == null || owner == null)
            {
                return false;
            }

            var previous = owner.GetExtendedInformation<TvState>();
            TvInterop.BeginFocus(controller);
            try
            {
                var playing = TvInterop.IsPlayingStream;
                var fetching = TvInterop.IsFetching;
                var powered = TvInterop.PoweredOff;
                var streaming = playing || fetching
                    || !string.IsNullOrWhiteSpace(TvInterop.PendingSourceUrl);
                if (playing)
                {
                    TvInterop.ClearPendingSourceUrl();
                }

                var source = !string.IsNullOrWhiteSpace(TvInterop.SourceUrl)
                    ? TvInterop.SourceUrl
                    : (streaming ? TvInterop.StreamUrl : previous?.SourceUrl);
                if (streaming && string.IsNullOrWhiteSpace(source))
                {
                    return false;
                }

                var url = streaming ? TvInterop.StreamUrl : previous?.StreamUrl;
                var title = streaming ? TvInterop.StreamTitle : previous?.StreamTitle;
                var playlist = streaming ? TvInterop.PlaylistUrlValue : previous?.PlaylistUrl;
                var live = streaming && TvInterop.IsLive;
                var segmented = streaming && TvInterop.IsSegmentedVod;
                var paused = streaming ? TvInterop.Paused : previous?.Paused ?? false;
                var shuffle = streaming ? TvInterop.Shuffle : previous?.Shuffle ?? false;
                var playlistIndex = streaming
                    ? TvInterop.PlaylistIndexValue : previous?.PlaylistIndex ?? 0;
                var position = TvInterop.Position;
                var mediaReplaced = !string.Equals(source, previous?.SourceUrl,
                        StringComparison.Ordinal)
                    || !string.Equals(playlist, previous?.PlaylistUrl, StringComparison.Ordinal)
                    || (streaming && playlistIndex != previous?.PlaylistIndex);
                var changed = previous == null || mediaReplaced
                    || url != previous.StreamUrl || title != previous.StreamTitle
                    || live != previous.IsLive || segmented != previous.IsSegmentedVod
                    || paused != previous.Paused || powered != previous.PoweredOff
                    || shuffle != previous.Shuffle
                    || playlistIndex != previous.PlaylistIndex
                    || position != previous.Position;
                var generation = previous?.Generation ?? 0;
                var barrier = previous?.Barrier ?? false;

                if (mediaReplaced)
                {
                    generation++;
                    if (startBarrier && !string.IsNullOrEmpty(source))
                    {
                        barrier = true;
                        TvInterop.SetSharedPaused(true);
                    }
                }

                snapshot = new TvState
                {
                    SourceUrl = source,
                    StreamUrl = url,
                    StreamTitle = title,
                    PlaylistUrl = playlist,
                    IsLive = live,
                    IsSegmentedVod = segmented,
                    PlaylistIndex = playlistIndex,
                    Position = position,
                    Paused = paused,
                    PoweredOff = powered,
                    Shuffle = shuffle,
                    Barrier = barrier,
                    Resume = false,
                    Generation = generation,
                    Operation = operation,
                    ActionId = actionId,
                };
                return changed;
            }
            finally
            {
                TvInterop.EndFocus();
            }
        }

        private static TvState CopyState(TvState source, byte operation, Guid actionId,
            bool barrier, bool resume)
            => new TvState
            {
                SourceUrl = source.SourceUrl,
                StreamUrl = source.StreamUrl,
                StreamTitle = source.StreamTitle,
                PlaylistUrl = source.PlaylistUrl,
                IsLive = source.IsLive,
                IsSegmentedVod = source.IsSegmentedVod,
                PlaylistIndex = source.PlaylistIndex,
                Position = source.Position,
                Paused = source.Paused,
                PoweredOff = source.PoweredOff,
                Shuffle = source.Shuffle,
                Barrier = barrier,
                Resume = resume,
                Generation = source.Generation,
                Operation = operation,
                ActionId = actionId,
            };

        private void PublishScalarDelta(Component controller, byte operation, Guid actionId)
        {
            if (_shutdown || _context == null || !_context.InGame || !TvInterop.Present)
            {
                return;
            }

            CaptureHostState(controller, ScalarOperation(operation), actionId, false,
                out var snapshot);
            if (snapshot != null)
            {
                PublishState(controller, snapshot);
            }
        }

        private void PublishBarrierDelta(Component controller, bool barrier, Guid actionId)
        {
            if (_shutdown || _context == null || !_context.InGame || !TvInterop.Present)
            {
                return;
            }

            var owner = TvInterop.OwnerOf(controller);
            var previous = owner?.GetExtendedInformation<TvState>();
            if (previous != null)
            {
                PublishState(controller, CopyState(previous, TvStateOperations.Barrier,
                    actionId, barrier, true));
            }
        }

        /// <summary>Publishes one immutable snapshot as extended information on the TV itself.
        /// The identity module owns the baseline, host echo and client apply; the TV only decides
        /// when the snapshot changed and which operation caused it.</summary>
        private void PublishState(Component controller, TvState snapshot)
        {
            if (_shutdown || _context == null || !_context.InGame || !TvInterop.Present
                || snapshot == null)
            {
                return;
            }

            TvInterop.OwnerOf(controller)?.SetExtendedInformation(snapshot);
        }

        private static byte ScalarOperation(byte operation)
            => operation == Pause ? TvStateOperations.Pause
                : operation == Power ? TvStateOperations.Power
                : operation == Shuffle ? TvStateOperations.Shuffle
                : operation == Seek ? TvStateOperations.Seek
                : throw new InvalidOperationException("Unknown scalar TV operation " + operation + ".");

        private static void ApplyPatches(Harmony harmony)
        {
            if (!TvInterop.Present)
            {
                return;
            }

            TryPatch(harmony, "OnEnable", null,
                new HarmonyMethod(typeof(TvHostBehaviour), nameof(ControllerLifecyclePostfix)));
            TryPatch(harmony, "Start", null,
                new HarmonyMethod(typeof(TvHostBehaviour), nameof(ControllerLifecyclePostfix)));
            TryPatch(harmony, "ChangeVideo", null,
                new HarmonyMethod(typeof(TvHostBehaviour), nameof(ControllerLifecyclePostfix)));
            TryPatch(harmony, "HandleGlobalInput", null,
                new HarmonyMethod(typeof(TvHostBehaviour), nameof(HostGlobalInputPostfix)));
            TryPatch(harmony, "StreamYouTube", null,
                new HarmonyMethod(typeof(TvHostBehaviour), nameof(StreamYouTubePostfix)));
            TryPatch(harmony, "ChangeToUrl", null,
                new HarmonyMethod(typeof(TvHostBehaviour), nameof(ChangeToUrlPostfix)));
            TryPatch(harmony, "PlayPlaylistItem", null,
                new HarmonyMethod(typeof(TvHostBehaviour), nameof(PlaylistItemPostfix)));
            TryPatch(harmony, "ToggleGlobalPause", null,
                new HarmonyMethod(typeof(TvHostBehaviour), nameof(PausePostfix)));
            TryPatch(harmony, "RemotePower", null,
                new HarmonyMethod(typeof(TvHostBehaviour), nameof(PowerPostfix)));
            TryPatch(harmony, "RemoteShuffle", null,
                new HarmonyMethod(typeof(TvHostBehaviour), nameof(ShufflePostfix)));
            TryPatch(harmony, "OnDisable", null,
                new HarmonyMethod(typeof(TvHostBehaviour), nameof(ControllerDisablePostfix)));
            TryPatch(harmony, "OnLocalPrepared",
                new HarmonyMethod(typeof(TvHostBehaviour), nameof(LocalPreparedPrefix)),
                new HarmonyMethod(typeof(TvHostBehaviour), nameof(LocalPreparedPostfix)));
        }

        private static void TryPatch(Harmony harmony, string method, HarmonyMethod prefix,
            HarmonyMethod postfix)
        {
            try
            {
                var original = AccessTools.Method(TvInterop.OptionalControllerType, method);
                if (original == null)
                {
                    CoopLog.Warn("TV host patch target missing: " + method);
                    return;
                }

                harmony.Patch(original, prefix: prefix, postfix: postfix);
            }
            catch (Exception exception)
            {
                CoopLog.Warn("TV host patch failed " + method + ": " + exception.Message);
            }
        }

        private static void StreamYouTubePostfix(object __instance, string url)
        {
            var controller = __instance as Component;
            TvInterop.RegisterController(controller);
            TvInterop.StreamYouTubePostfix(controller, url);
            _active?.OnMediaSourceChanged(controller);
        }

        private static void ChangeToUrlPostfix(object __instance, string url, string title)
        {
            var controller = __instance as Component;
            TvInterop.RegisterController(controller);
            TvInterop.ChangeToUrlPostfix(controller, url);
            _active?.OnMediaSourceChanged(controller);
        }

        private static void PlaylistItemPostfix(object __instance)
        {
            var controller = __instance as Component;
            TvInterop.RegisterController(controller);
            if (_active == null || TvInterop.ApplyingRemote)
            {
                return;
            }

            _active.OnMediaSourceChanged(controller);
        }

        private static void PausePostfix(object __instance) => RegisterScalar(__instance, Pause);
        private static void PowerPostfix(object __instance) => RegisterScalar(__instance, Power);
        private static void ShufflePostfix(object __instance) => RegisterScalar(__instance, Shuffle);

        private static void RegisterScalar(object instance, byte operation)
        {
            var controller = instance as Component;
            TvInterop.RegisterController(controller);
            _active?.OnScalarChanged(controller, operation);
        }

        private void OnMediaSourceChanged(Component controller)
        {
            if (TvInterop.ApplyingRemote || controller == null)
            {
                return;
            }

            var owner = TvInterop.OwnerOf(controller);
            if (owner == null)
            {
                return;
            }

            if (CaptureHostState(controller, TvStateOperations.Open, Guid.Empty, true,
                out var snapshot) && snapshot != null
                && !string.IsNullOrEmpty(snapshot.SourceUrl))
            {
                PublishState(controller, snapshot);
            }
        }

        private void OnScalarChanged(Component controller, byte operation)
        {
            if (TvInterop.ApplyingRemote || controller == null)
            {
                return;
            }

            PublishScalarDelta(controller, operation, Guid.Empty);
        }

        private static void ControllerLifecyclePostfix(object __instance)
        {
            var controller = __instance as Component;
            TvInterop.RegisterController(controller);
            _active?.OnControllerLifecycle(controller);
        }

        private static void ControllerDisablePostfix(object __instance)
        {
            var controller = __instance as Component;
            var active = _active;
            if (active != null && controller != null)
            {
                var previous = TvInterop.OwnerOf(controller)?.GetExtendedInformation<TvState>();
                if (previous?.Barrier == true && !active._shutdown)
                {
                    active.AbortBarrier(controller,
                        "host TV controller playback was canceled");
                    active.PublishBarrierDelta(controller, false, Guid.Empty);
                }
            }

            TvInterop.ForgetController(controller);
        }

        private static void HostGlobalInputPostfix()
        {
            var alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            if (!alt || (!Input.GetKey(KeyCode.RightBracket) && !Input.GetKey(KeyCode.LeftBracket)
                && Mathf.Abs(Input.mouseScrollDelta.y) <= 0.0001f))
            {
                return;
            }

            if (!TvInterop.IsPlayingStream && !TvInterop.IsFetching)
            {
                return;
            }

            var controller = TvInterop.CurrentController;
            if (_active == null || controller == null)
            {
                return;
            }

            var now = Time.unscaledTime;
            if (!float.IsNaN(now) && !float.IsInfinity(now)
                && now - _active._lastSeekInputTime >= 0.25f)
            {
                _active._lastSeekInputTime = now;
                _active.PublishScalarDelta(controller, Seek, Guid.Empty);
            }
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

            // A TV that was already playing when the bridge initialized only reveals its stream
            // once the VideoPlayer prepares; write that first state now.
            _active.OnControllerLifecycle(controller);
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
            if (owner == null)
            {
                return;
            }

            if (owner.GetExtendedInformation<TvState>()?.Barrier != true
                || !TvInterop.IsMainPreparedPlayer(controller, source))
            {
                return;
            }

            TvInterop.PausePreparedStream(controller);
            CompleteBarrier(controller);
            PublishBarrierDelta(controller, false, Guid.Empty);
        }

        private void OnHostVideoError(Component failed, string message)
        {
            if (_shutdown)
            {
                return;
            }

            var controller = failed;
            var owner = TvInterop.OwnerOf(controller);
            if (owner == null)
            {
                return;
            }

            var previous = owner.GetExtendedInformation<TvState>();
            CoopLog.Error("Host TV VideoPlayer failed: " + (message ?? "unknown error"));
            TvInterop.BeginFocus(controller);
            try
            {
                TvInterop.AbortSharedPlayback();
                AbortBarrier(controller, "host TV playback failed");
                if (_context != null && _context.InGame && TvInterop.Present)
                {
                    PublishState(controller, new TvState
                    {
                        SourceUrl = null,
                        StreamUrl = null,
                        StreamTitle = null,
                        PlaylistUrl = null,
                        IsLive = false,
                        IsSegmentedVod = false,
                        PlaylistIndex = 0,
                        Position = 0.0,
                        Paused = false,
                        PoweredOff = TvInterop.PoweredOff,
                        Shuffle = TvInterop.Shuffle,
                        Barrier = false,
                        Resume = true,
                        Generation = previous?.Generation ?? 0,
                        Operation = TvStateOperations.Error,
                        ActionId = Guid.Empty,
                    });
                }
            }
            finally
            {
                TvInterop.EndFocus();
            }
        }

        private void CompleteBarrier(Component controller)
        {
            TvInterop.ResumePlayback();
        }

        private void AbortBarrier(Component controller, string reason)
        {
            TvInterop.ResumePlayback();
            CoopLog.Warn("TV load barrier aborted: " + reason);
        }
    }
}
