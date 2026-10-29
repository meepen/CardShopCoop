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
        private ICoopContext _context;
        private Harmony _harmony;
        private TvState _appliedState;
        private Guid _pendingActionId;
        private bool _shutdown;
        private bool _joined;
        private bool _clientBarrier;
        private int _clientGeneration = -1;

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
            {
                return;
            }

            _context = Context;
            try
            {
                ResetSessionState();
                CEventManager.AddListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
                SceneManager.sceneLoaded += OnSceneLoaded;
                _active = this;
                _harmony = new Harmony("dev.meepen.cardshopcoop.tv.client.runtime");
                ApplyPatches(_harmony);
                OnControllerLifecycle();
            }
            catch (Exception exception)
            {
                CoopLog.Error("TV client initialization failed: " + exception);
                _harmony?.UnpatchSelf();
                _harmony = null;
                CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
                SceneManager.sceneLoaded -= OnSceneLoaded;
                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }
                ResetSessionState();
                _context = null;
                throw;
            }
        }

        [OnFullyJoined]
        private void MarkJoined(PeerConnection _)
        {
            _joined = true;
            _appliedState = null;
            ApplyLatestState(null, true);
        }

        [GlobalInformationHandler]
        private void OnTvState(InformationChange<TvState> change)
        {
            if (_shutdown || !TvInterop.Present || change.Current == null)
            {
                return;
            }

            ApplyLatestState(change.Current, change.IsFirstValue);
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            CEventManager.RemoveListener<CEventPlayer_GameDataFinishLoaded>(OnWorldReady);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }
            _harmony?.UnpatchSelf();
            _harmony = null;
            ResetSessionState();
            _context = null;
        }

        private void OnDestroy() => Shutdown();

        private void ResetSessionState()
        {
            TvInterop.ResetSession();
            _appliedState = null;
            _pendingActionId = Guid.Empty;
            _joined = false;
            _clientBarrier = false;
            _clientGeneration = -1;
        }

        private void OnWorldReady(CEventPlayer_GameDataFinishLoaded _)
        {
            _appliedState = null;
            ApplyLatestState();
        }

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            TvInterop.NotifySceneLoaded();
            _clientGeneration = -1;
            _appliedState = null;
            ApplyLatestState();
        }

        [OnClientDisconnected]
        private void ForgetHost(PeerConnection connection, DisconnectInfo info)
        {
            if (connection?.Id == 1)
            {
                ResetSessionState();
            }
        }

        private void OnControllerLifecycle()
        {
            if (TvInterop.Present)
            {
                ApplyLatestState();
            }
        }

        private void ApplyLatestState()
            => ApplyLatestState(Global<TvState>.Value, false);

        private void ApplyLatestState(TvState state, bool first)
        {
            if (_shutdown || !_joined || _context == null || !_context.InGame
                || !TvInterop.Present || TvInterop.ApplyingRemote || !TvInterop.ControllerReady)
            {
                return;
            }

            if (state == null || ReferenceEquals(state, _appliedState))
            {
                return;
            }

            if (!first && state.Generation < _clientGeneration)
            {
                RetireAction(state.ActionId);
                _appliedState = state;
                return;
            }

            SupersedeAction(state.ActionId);
            if (!string.IsNullOrWhiteSpace(state.SourceUrl) && !TvInterop.EnrollmentReady)
            {
                TvInterop.RequestClientEnrollment();
                return;
            }

            if (first)
            {
                ApplyBaselineState(state);
            }
            else
            {
                ApplyState(state);
            }

            _appliedState = state;
        }

        private void ApplyBaselineState(TvState state)
        {
            ActionRuntime.Confirm(state.ActionId, () =>
            {
                _clientGeneration = state.Generation;
                _clientBarrier = state.Barrier;
                ApplyMediaState(state);
            });
            _pendingActionId = Guid.Empty;
        }

        private void SupersedeAction(Guid actionId)
        {
            if (_pendingActionId != Guid.Empty && _pendingActionId != actionId)
            {
                ActionRuntime.Ack(_pendingActionId);
            }

            _pendingActionId = actionId;
        }

        private static void RetireAction(Guid actionId)
        {
            if (actionId != Guid.Empty)
            {
                ActionRuntime.Ack(actionId);
            }
        }

        private void ApplyState(TvState state)
        {
            switch (state.Operation)
            {
                case TvStateOperations.Open:
                case TvStateOperations.Playlist:
                case TvStateOperations.Next:
                case TvStateOperations.Previous:
                    ActionRuntime.Confirm(state.ActionId, () =>
                    {
                        _clientGeneration = state.Generation;
                        _clientBarrier = state.Barrier;
                        ApplyMediaState(state);
                    });
                    break;

                case TvStateOperations.Pause:
                    ActionRuntime.AckOrApply(state.ActionId,
                        () => TvInterop.ApplyPaused(state.Paused));
                    break;

                case TvStateOperations.Power:
                    ActionRuntime.AckOrApply(state.ActionId,
                        () => TvInterop.ApplyPowered(state.PoweredOff));
                    break;

                case TvStateOperations.Shuffle:
                    ActionRuntime.AckOrApply(state.ActionId,
                        () => TvInterop.ApplyShuffle(state.Shuffle));
                    break;

                case TvStateOperations.Seek:
                    ActionRuntime.Confirm(state.ActionId,
                        () => TvInterop.ApplySeek(state.Position));
                    break;

                case TvStateOperations.Barrier:
                    ActionRuntime.Confirm(state.ActionId, () =>
                    {
                        _clientBarrier = state.Barrier;
                        TvInterop.ApplyBarrier(state.Barrier, state.Resume);
                    });
                    break;

                case TvStateOperations.Error:
                    ActionRuntime.Confirm(state.ActionId, () =>
                    {
                        _clientGeneration = Math.Max(_clientGeneration, state.Generation);
                        _clientBarrier = false;
                        TvInterop.ApplyPlaybackError();
                    });
                    break;

                default:
                    ActionRuntime.Confirm(state.ActionId, () =>
                    {
                        _clientGeneration = state.Generation;
                        _clientBarrier = state.Barrier;
                        ApplyMediaState(state);
                    });
                    break;
            }

            _pendingActionId = Guid.Empty;
        }

        private static void ApplyMediaState(TvState state)
        {
            TvInterop.ApplyMediaState(state.SourceUrl, state.StreamUrl, state.StreamTitle,
                state.PlaylistUrl, state.IsLive, state.IsSegmentedVod, state.PlaylistIndex,
                state.Position, state.Paused, state.PoweredOff, state.Shuffle,
                state.Barrier, state.Resume, state.Generation);
        }

        private static void ApplyPatches(Harmony harmony)
        {
            if (!TvInterop.Present)
            {
                return;
            }

            TryPatch(harmony, "OnEnable", null,
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(ControllerLifecyclePostfix)));
            TryPatch(harmony, "Start", null,
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(ControllerLifecyclePostfix)));
            TryPatch(harmony, "ChangeVideo", null,
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(ControllerLifecyclePostfix)));
            TryPatch(harmony, "ChangeToUrl", null,
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(ChangeToUrlPostfix)));
            TryPatch(harmony, "StreamYouTube", null,
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(StreamYouTubePostfix)));
            // The game performs every control through its own path; these observe the resulting
            // local change and register one post-hoc action. No vanilla input is suppressed.
            TryPatch(harmony, "HandleGlobalInput",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(GlobalInputPrefix)),
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(GlobalInputPostfix)));
            TryPatch(harmony, "OnQualityChoice",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(QualityPrefix)),
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(QualityPostfix)));
            TryPatch(harmony, "ToggleGlobalPause",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(CapturePrefix)),
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(PausePostfix)));
            TryPatch(harmony, "RemoteNext",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(CapturePrefix)),
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(NextPostfix)));
            TryPatch(harmony, "RemotePrev",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(CapturePrefix)),
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(PreviousPostfix)));
            TryPatch(harmony, "RemoteShuffle",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(CapturePrefix)),
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(ShufflePostfix)));
            TryPatch(harmony, "RemotePower",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(CapturePrefix)),
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(PowerPostfix)));
            TryPatch(harmony, "OnLocalPrepared",
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(LocalPreparedPrefix)),
                new HarmonyMethod(typeof(TvClientBehaviour), nameof(LocalPreparedPostfix)));
        }

        private static void TryPatch(Harmony harmony, string method, HarmonyMethod prefix,
            HarmonyMethod postfix)
        {
            try
            {
                var original = AccessTools.Method(TvInterop.OptionalControllerType, method);
                if (original == null)
                {
                    CoopLog.Warn("TV client patch target missing: " + method);
                    return;
                }

                harmony.Patch(original, prefix: prefix, postfix: postfix);
            }
            catch (Exception exception)
            {
                CoopLog.Warn("TV client patch failed " + method + ": " + exception.Message);
            }
        }

        private static void LocalPreparedPrefix(object __instance, object source)
        {
            TvInterop.RegisterController(__instance as Component);
            _active?.BeforeLocalPrepared(__instance, source);
        }

        private static void LocalPreparedPostfix(object __instance, object source)
        {
            TvInterop.RegisterController(__instance as Component);
            _active?.AfterLocalPrepared(__instance, source);
        }

        private static void ControllerLifecyclePostfix(object __instance)
        {
            TvInterop.RegisterController(__instance as Component);
            _active?.OnControllerLifecycle();
        }

        private static void ChangeToUrlPostfix(object __instance, string url, string title)
        {
            TvInterop.RegisterController(__instance as Component);
            TvInterop.ChangeToUrlPostfix(url);
            _active?.OnControllerLifecycle();
        }

        private static void StreamYouTubePostfix(object __instance, string url)
        {
            TvInterop.RegisterController(__instance as Component);
            TvInterop.StreamYouTubePostfix(url);
            _active?.OnControllerLifecycle();
        }

        private void BeforeLocalPrepared(object instance, object source)
        {
            if (_clientBarrier && TvInterop.IsMainPreparedPlayer(instance, source))
            {
                TvInterop.SetSharedPausedForPrepare(false);
            }
        }

        private void AfterLocalPrepared(object instance, object source)
        {
            if (!TvInterop.IsMainPreparedPlayer(instance, source))
            {
                return;
            }

            TvInterop.ApplyPreparedPosition(instance);
            ApplyLatestState();
            if (_clientBarrier)
            {
                TvInterop.PausePreparedStream(instance);
            }
        }

        private static bool CanAct()
        {
            return _active != null && !_active._shutdown && _active._joined
                && _active._context != null && _active._context.InGame && TvInterop.Present;
        }

        private static void SendAction(byte op, string url = null, string title = null,
            double value = 0, Action apply = null, Action undo = null)
            => ActionRuntime.Submit(new TvOpMessage
            {
                Op = op,
                Url = url,
                Title = title,
                Value = value,
            }, apply, undo);

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
            TvInterop.RegisterController(__instance as Component);
            __state = default;
            if (!CanAct() || TvInterop.ApplyingRemote)
            {
                return;
            }

            var url = TvInterop.PendingUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            __state.Armed = true;
            __state.Url = url;
            __state.Prior = TvInterop.CapturePlaybackState();
        }

        private static void QualityPostfix(object __instance, QualityState __state)
        {
            TvInterop.RegisterController(__instance as Component);
            if (!__state.Armed || !CanAct() || TvInterop.ApplyingRemote)
            {
                return;
            }

            ForwardMedia(Open, __state.Prior, __state.Url, "Shared stream");
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

        private static void GlobalInputPrefix(out GlobalInputState __state)
        {
            __state = default;
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

        private static void GlobalInputPostfix(GlobalInputState __state)
        {
            if (!CanAct() || TvInterop.ApplyingRemote)
            {
                return;
            }

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

        /// <summary>Captures the playback state before a control the game performs itself, so the
        /// postfix can register one observed action and replay it if the host rejects it.</summary>
        private static void CapturePrefix(object __instance, out TvInterop.PlaybackUndoState __state)
        {
            TvInterop.RegisterController(__instance as Component);
            __state = TvInterop.CapturePlaybackState();
        }

        private static void PausePostfix(TvInterop.PlaybackUndoState __state)
        {
            if (!CanAct() || TvInterop.ApplyingRemote || TvInterop.Paused == __state.Paused)
            {
                return;
            }

            SendAction(Pause,
                apply: () => TvInterop.ApplyPaused(!__state.Paused),
                undo: () => TvInterop.ApplyPaused(__state.Paused));
        }

        private static void PowerPostfix(TvInterop.PlaybackUndoState __state)
        {
            if (!CanAct() || TvInterop.ApplyingRemote
                || TvInterop.PoweredOff == __state.PoweredOff)
            {
                return;
            }

            SendAction(Power,
                apply: () => TvInterop.ApplyPowered(!__state.PoweredOff),
                undo: () => TvInterop.ApplyPowered(__state.PoweredOff));
        }

        private static void ShufflePostfix(TvInterop.PlaybackUndoState __state)
        {
            if (!CanAct() || TvInterop.ApplyingRemote || TvInterop.Shuffle == __state.Shuffle)
            {
                return;
            }

            SendAction(Shuffle,
                apply: () => TvInterop.ApplyShuffle(!__state.Shuffle),
                undo: () => TvInterop.ApplyShuffle(__state.Shuffle));
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

            SendAction(op, url, title,
                apply: () => TvInterop.ApplyOperation(op, url, title, 0),
                undo: () => TvInterop.RestorePlaybackState(prior));
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

            SendAction(op,
                apply: () => TvInterop.ApplyOperation(op, null, null, 0),
                undo: () => TvInterop.RestorePlaybackState(prior));
        }

        private static void ForwardSeek(double fromPosition, double delta)
        {
            var target = Math.Max(0.0,
                Math.Min(TvInterop.MaxPositionSeconds, fromPosition + delta));
            SendAction(Seek, value: delta,
                apply: () => TvInterop.ApplySeek(target),
                undo: () => TvInterop.ApplySeek(fromPosition));
        }
    }
}
