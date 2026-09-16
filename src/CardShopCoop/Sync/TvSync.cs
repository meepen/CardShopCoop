using System;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using CardShopCoop.Util;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>Optional host-authoritative synchronization for RTCGO Custom TV streams.
    /// Local files, mute, and volume deliberately remain local; only stream playback controls
    /// and stream identity cross the wire.</summary>
    public sealed class TvSync : TickableCoopModule
    {
        private const byte Next = 1, Previous = 2, Pause = 3, Power = 4, Shuffle = 5, Seek = 6, Open = 7, Ready = 8;
        // Slice 0 is playback controls/identity. The position field is retained in the DTO for
        // wire compatibility, but the game bridge does not consume it, so no inert position slice
        // is emitted. The one-slice safety pass is intentionally coarse to avoid a disguised full
        // refresh while still repairing a lost unchanged control frame.
        private const float SweepCycleSeconds = 30f;
        private const float SweepSliceSeconds = SweepCycleSeconds;
        private const int SweepSliceCount = 1;
        private const float ControlSendFloor = 0.25f;
        private const float BarrierTimeout = 10f;
        private static TvSync Active;

        public Action<INetMessage> SendOp;
        public Action<INetMessage> BroadcastState;
        public Func<int> PeerCount;
        public Action<int, INetMessage> SendToClient;
        private float _sweepTimer;
        private float _controlSendAge;
        private int _sweepCursor;
        private bool _hasHostState;
        private bool _controlPending;
        private string _lastUrl;
        private string _lastSource;
        private string _lastTitle;
        private string _lastPlaylist;
        private bool _lastLive, _lastSegmented, _lastPaused, _lastPowered, _lastShuffle;
        private int _lastPlaylistIndex;
        private bool _barrier, _hostReady, _resumePulse;
        private float _barrierAge;
        private int _generation;
        private readonly System.Collections.Generic.HashSet<int> _readyPeers = new System.Collections.Generic.HashSet<int>();
        private int _clientGeneration = -1;
        private bool _clientBarrier, _clientReported;
        private string _clientSource, _clientUrl, _clientTitle, _clientPlaylist;
        private bool _clientLive, _clientSegmented, _clientPaused, _clientPowered, _clientShuffle;
        private int _clientPlaylistIndex;

        public TvSync()
        {
            Active = this;
        }

        public override string Name => "tv";

        public override void Start()
        {
            Active = this;
        }

        protected override void OnHostTick(in SyncFrame frame)
        {
            HostTick(frame.Dt, frame.InGame);
        }

        protected override void OnClientTick(in SyncFrame frame)
        {
            ClientTick(frame.Dt, frame.InGame);
        }

        public override void Dispose()
        {
            base.Dispose();
            if (ReferenceEquals(Active, this))
                Active = null;
        }

        public override void Reset()
        {
            TvInterop.ResetSession();
            _sweepTimer = 0f;
            _controlSendAge = ControlSendFloor;
            _sweepCursor = 0;
            _hasHostState = false;
            _controlPending = false;
            _lastUrl = _lastTitle = _lastPlaylist = null;
            _lastSource = null;
            _lastLive = _lastSegmented = _lastPaused = _lastPowered = _lastShuffle = false;
            _lastPlaylistIndex = 0;
            _barrier = _hostReady = _resumePulse = false;
            _barrierAge = 0f;
            _generation = 0;
            _readyPeers.Clear();
            _clientGeneration = -1;
            _clientBarrier = _clientReported = false;
            _clientSource = _clientUrl = _clientTitle = _clientPlaylist = null;
            _clientLive = _clientSegmented = _clientPaused = _clientPowered = _clientShuffle = false;
            _clientPlaylistIndex = 0;
        }

        public override void ForceResend()
        {
            _sweepTimer = SweepSliceSeconds;
            _sweepCursor = 0;
        }

        public void HostTick(float dt, bool inGame)
        {
            if (!inGame || !TvInterop.Present || BroadcastState == null)
                return;
            _controlSendAge += dt;
            if (_barrier)
                _barrierAge += dt;

            try
            {
                bool playing = TvInterop.IsPlayingStream;
                bool fetching = TvInterop.IsFetching;
                bool streaming = playing || fetching;
                if (!streaming && !_hasHostState && !_barrier)
                    return;
                string url = streaming ? TvInterop.StreamUrl : null;
                string source = streaming ? TvInterop.SourceUrl : null;
                string title = streaming ? TvInterop.StreamTitle : null;
                string playlist = streaming ? TvInterop.PlaylistUrl : null;
                bool live = streaming && TvInterop.IsLive;
                bool segmented = streaming && TvInterop.IsSegmentedVod;
                bool paused = streaming && TvInterop.Paused;
                bool powered = streaming && TvInterop.PoweredOff;
                bool shuffle = streaming && TvInterop.Shuffle;
                int playlistIndex = streaming ? TvInterop.PlaylistIndex : 0;
                bool changed = !_hasHostState || source != _lastSource || url != _lastUrl || title != _lastTitle || playlist != _lastPlaylist
                    || live != _lastLive || segmented != _lastSegmented
                    || paused != _lastPaused || powered != _lastPowered
                    || shuffle != _lastShuffle || playlistIndex != _lastPlaylistIndex;
                if (source != _lastSource)
                {
                    _generation++;
                    if (!string.IsNullOrEmpty(source))
                    {
                        _barrier = true;
                        _hostReady = false;
                        _barrierAge = 0f;
                        _readyPeers.Clear();
                        TvInterop.SetSharedPaused(true);
                        CoopPlugin.Log.LogInfo("TV load barrier started (generation " + _generation + ")");
                    }
                }
                int peers = PeerCount == null ? 0 : Math.Max(0, PeerCount());
                if (_barrier && ((_hostReady && _readyPeers.Count >= peers) || _barrierAge >= BarrierTimeout))
                {
                    if (_barrierAge >= BarrierTimeout && _readyPeers.Count < peers)
                        CoopPlugin.Log.LogWarning("TV load barrier timed out; resuming with " + _readyPeers.Count + "/" + peers + " clients ready");
                    _barrier = false;
                    _resumePulse = true;
                    TvInterop.ResumePlayback();
                    changed = true;
                    CoopPlugin.Log.LogInfo("TV load barrier completed (generation " + _generation + ")");
                }
                if (_barrier && string.IsNullOrEmpty(source))
                {
                    _barrier = false;
                    _hostReady = false;
                    _readyPeers.Clear();
                    _resumePulse = false;
                    TvInterop.ResumePlayback();
                    changed = true;
                    CoopPlugin.Log.LogInfo("TV load barrier cancelled because the host returned to local playback");
                }
                _lastSource = source;
                _lastUrl = url;
                _lastTitle = title;
                _lastPlaylist = playlist;
                _lastLive = live;
                _lastSegmented = segmented;
                _lastPaused = paused;
                _lastPowered = powered;
                _lastShuffle = shuffle;
                _lastPlaylistIndex = playlistIndex;
                _hasHostState = true;
                if (changed)
                    _controlPending = true;
                if (_controlPending && _controlSendAge >= ControlSendFloor && HasPeers())
                {
                    SendControlNow();
                    _controlPending = false;
                    _controlSendAge = 0f;
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("TvSync host: " + e.Message); }
        }

        public void HostApplyOp(TvOpMessage message, int senderId)
        {
            if (!TvInterop.Present || message == null)
                return;
            if (message.Op == Ready)
            {
                if (_barrier && (int)message.Value == _generation)
                    _readyPeers.Add(senderId);
                return;
            }
            TvInterop.HostApply(message.Op, message.Url, message.Title, message.Value);
        }

        public void ClientApplyState(TvStateMessage message)
        {
            if (!TvInterop.Present || message == null)
                return;
            if (message.Full || message.Index == 0)
            {
                if (message.Generation < _clientGeneration)
                    return;
                if (message.Generation != _clientGeneration)
                {
                    _clientGeneration = message.Generation;
                    _clientReported = false;
                }
                _clientSource = message.SourceUrl;
                _clientUrl = message.StreamUrl;
                _clientTitle = message.StreamTitle;
                _clientPlaylist = message.PlaylistUrl;
                _clientLive = message.IsLive;
                _clientSegmented = message.IsSegmentedVod;
                _clientPlaylistIndex = message.PlaylistIndex;
                _clientPaused = message.Paused;
                _clientPowered = message.PoweredOff;
                _clientShuffle = message.Shuffle;
                _clientBarrier = message.Barrier;
                ApplyClientState(message.Resume);
            }
        }

        private void ApplyClientState(bool resume = false)
        {
            TvInterop.ApplyHostState(_clientSource, _clientUrl, _clientTitle, _clientPlaylist,
                _clientLive, _clientSegmented, _clientPlaylistIndex, 0.0,
                _clientPaused, _clientPowered, _clientShuffle, _clientBarrier, resume,
                _clientGeneration);
        }

        /// <summary>One host sweep ordinal: 0 is the playback-control/identity baseline. It is
        /// reasserted unconditionally once per coarse safety pass.</summary>
        public override void PeriodicUpdate(float delta)
        {
            if (CoopCore.Role != CoopRole.Host || BroadcastState == null || !CoopCore.InSessionWorld
                || !TvInterop.Present)
                return;
            _sweepTimer += delta;
            if (_sweepTimer < SweepSliceSeconds)
                return;
            _sweepTimer = 0f;
            Guarded("sweep", () =>
            {
                int index = _sweepCursor;
                _sweepCursor = (_sweepCursor + 1) % SweepSliceCount;
                if (index == 0 && HasPeers())
                {
                    // Re-assert unconditionally: a lost unchanged control frame must heal.
                    SendControlNow();
                }
            });
        }

        /// <summary>Host: complete state to one connection only (join/heal catch-up).</summary>
        public override void FullUpdate(int connId)
        {
            if (CoopCore.Role != CoopRole.Host || SendToClient == null || !TvInterop.Present)
                return;
            Guarded("full", () =>
            {
                SendToClient(connId, BuildState(true, -1, TvInterop.Position));
            });
        }

        private void SendControlNow()
        {
            if (BroadcastState == null || !_hasHostState)
                return;
            BroadcastState(BuildState(false, 0, 0.0));
            _resumePulse = false;
        }

        private bool HasPeers()
        {
            return PeerCount != null && PeerCount() > 0;
        }

        private TvStateMessage BuildState(bool full, int index, double position)
        {
            return new TvStateMessage
            {
                Full = full,
                Index = index,
                SourceUrl = _lastSource,
                StreamUrl = _lastUrl,
                StreamTitle = _lastTitle,
                PlaylistUrl = _lastPlaylist,
                IsLive = _lastLive,
                IsSegmentedVod = _lastSegmented,
                IsPlaylist = !string.IsNullOrEmpty(_lastPlaylist),
                PlaylistIndex = _lastPlaylistIndex,
                Position = position,
                Paused = _lastPaused,
                PoweredOff = _lastPowered,
                Shuffle = _lastShuffle,
                Barrier = _barrier,
                Resume = _resumePulse,
                Generation = _generation
            };
        }

        public void ClientTick(float dt, bool inGame)
        {
            if (!inGame || !TvInterop.Present)
                return;
            TvInterop.EnsureClientEnrollment();
        }

        public static void LocalPreparedPrefix(object __instance, object source)
        {
            Active?.BeforeLocalPrepared(__instance, source);
        }

        public static void LocalPreparedPostfix(object __instance, object source)
        {
            Active?.OnLocalPrepared(__instance, source);
        }

        private void BeforeLocalPrepared(object instance, object source)
        {
            bool active = CoopCore.Role == CoopRole.Host ? _barrier : _clientBarrier;
            if (active && TvInterop.IsMainPreparedPlayer(instance, source))
                TvInterop.SetSharedPausedForPrepare(false);
        }

        private void OnLocalPrepared(object instance, object source)
        {
            bool active = CoopCore.Role == CoopRole.Host ? _barrier : _clientBarrier;
            if (!active || !TvInterop.IsMainPreparedPlayer(instance, source))
                return;
            TvInterop.PausePreparedStream(instance);
            if (CoopCore.Role == CoopRole.Host)
            {
                _hostReady = true;
            }
            else if (CoopCore.Role == CoopRole.Client && !_clientReported)
            {
                _clientReported = true;
                Send(Ready, value: _clientGeneration);
            }
        }

        private static void Send(byte op, string url = null, string title = null, double value = 0)
        {
            if (CoopCore.Role != CoopRole.Client || TvInterop.ApplyingRemote)
                return;
            CoopCore.Instance?.SendTvOp(new TvOpMessage { Op = op, Url = url, Title = title, Value = value });
        }

        public static bool ClientGlobalInputPrefix()
        {
            if (CoopCore.Role != CoopRole.Client || !TvInterop.Present)
                return true;
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            if (!alt)
                return true; // retain the mod's own UI opening path (Alt+Y)
            if (Input.GetKeyDown(KeyCode.Y))
                return true;
            if (Input.GetKeyDown(KeyCode.P))
                Send(Pause);
            else if (Input.GetKeyDown(KeyCode.Equals))
                Send(Next);
            else if (Input.GetKeyDown(KeyCode.Minus))
                Send(Previous);
            else if (Input.GetKey(KeyCode.RightBracket))
                Send(Seek, value: 5);
            else if (Input.GetKey(KeyCode.LeftBracket))
                Send(Seek, value: -5);
            else if (Input.GetKeyDown(KeyCode.S))
                Send(Shuffle);
            else if (Input.GetKeyDown(KeyCode.O))
                Send(Power);
            else
                return false; // prevent the TV mod from changing shared state locally
            return false;
        }

        public static bool ClientQualityChoicePrefix(bool download)
        {
            if (CoopCore.Role != CoopRole.Client || !TvInterop.Present)
                return true;
            if (TvInterop.ApplyingRemote)
                return true;
            string url = TvInterop.PendingUrl;
            if (string.IsNullOrWhiteSpace(url))
                return true;
            TvInterop.FinishQualitySelection();
            Send(Open, url, "Shared stream");
            return false;
        }

        public static void ApplyPatches(Harmony h)
        {
            if (!TvInterop.Present)
                return;
            Try(h, "HandleGlobalInput", new HarmonyMethod(typeof(TvSync), nameof(ClientGlobalInputPrefix)));
            Try(h, "OnQualityChoice", new HarmonyMethod(typeof(TvSync), nameof(ClientQualityChoicePrefix)));
            Try(h, "StreamYouTube", null,
                postfix: new HarmonyMethod(typeof(TvInterop), nameof(TvInterop.StreamStartedPostfix)));
            Try(h, "OnLocalPrepared", new HarmonyMethod(typeof(TvSync), nameof(LocalPreparedPrefix)),
                postfix: new HarmonyMethod(typeof(TvSync), nameof(LocalPreparedPostfix)));
            Try(h, "RemotePlayPause", new HarmonyMethod(typeof(TvSync), nameof(ClientPausePrefix)));
            Try(h, "RemoteNext", new HarmonyMethod(typeof(TvSync), nameof(ClientNextPrefix)));
            Try(h, "RemotePrev", new HarmonyMethod(typeof(TvSync), nameof(ClientPreviousPrefix)));
            Try(h, "RemoteShuffle", new HarmonyMethod(typeof(TvSync), nameof(ClientShufflePrefix)));
            Try(h, "RemotePower", new HarmonyMethod(typeof(TvSync), nameof(ClientPowerPrefix)));
            // RemoteMute/RemoteVolUp/RemoteVolDown intentionally remain unpatched: audio is
            // explicitly client-local and must be usable independently on each PC.
        }

        private static bool ClientControl(byte op)
        {
            if (CoopCore.Role != CoopRole.Client || !TvInterop.Present)
                return true;
            Send(op);
            return false;
        }

        public static bool ClientPausePrefix() => ClientControl(Pause);
        public static bool ClientNextPrefix() => ClientControl(Next);
        public static bool ClientPreviousPrefix() => ClientControl(Previous);
        public static bool ClientShufflePrefix() => ClientControl(Shuffle);
        public static bool ClientPowerPrefix() => ClientControl(Power);

        private static void Try(Harmony h, string method, HarmonyMethod prefix, HarmonyMethod postfix = null)
        {
            try
            {
                var original = AccessTools.Method(AccessTools.TypeByName("Scripts.VideoPlayerController"), method);
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning("TV patch target missing: " + method);
                    return;
                }
                h.Patch(original, prefix: prefix, postfix: postfix);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("TV patch failed " + method + ": " + e.Message); }
        }

    }
}
