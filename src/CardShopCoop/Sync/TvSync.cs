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
    /// and best-effort stream position cross the wire.</summary>
    public sealed class TvSync
    {
        private const byte Next = 1, Previous = 2, Pause = 3, Power = 4, Shuffle = 5, Seek = 6, Open = 7;
        private const float Interval = 1f;
        private const float HealEvery = 15f;

        public Action<INetMessage> SendOp;
        public Action<INetMessage> BroadcastState;
        private float _timer;
        private float _heal;
        private string _lastUrl;
        private string _lastSource;
        private string _lastTitle;
        private string _lastPlaylist;
        private bool _lastLive, _lastSegmented, _lastPaused, _lastPowered, _lastShuffle;
        private int _lastPlaylistIndex;

        public void Reset()
        {
            TvInterop.ResetSession();
            _timer = -2.1f;
            _heal = HealEvery;
            _lastUrl = _lastTitle = _lastPlaylist = null;
            _lastSource = null;
            _lastLive = _lastSegmented = _lastPaused = _lastPowered = _lastShuffle = false;
            _lastPlaylistIndex = 0;
        }

        public void ForceResend() { _heal = HealEvery; }

        public void HostTick(float dt, bool inGame)
        {
            if (!inGame || !TvInterop.Present || BroadcastState == null) return;
            _timer += dt;
            _heal += dt;
            if (_timer < Interval) return;
            _timer -= Interval;

            try
            {
                string url = TvInterop.IsPlayingStream ? TvInterop.StreamUrl : null;
                string source = TvInterop.IsPlayingStream ? TvInterop.SourceUrl : null;
                string title = TvInterop.IsPlayingStream ? TvInterop.StreamTitle : null;
                string playlist = TvInterop.IsPlayingStream ? TvInterop.PlaylistUrl : null;
                bool changed = source != _lastSource || url != _lastUrl || title != _lastTitle || playlist != _lastPlaylist
                    || TvInterop.IsLive != _lastLive || TvInterop.IsSegmentedVod != _lastSegmented
                    || TvInterop.Paused != _lastPaused || TvInterop.PoweredOff != _lastPowered
                    || TvInterop.Shuffle != _lastShuffle || TvInterop.PlaylistIndex != _lastPlaylistIndex;
                if (!changed && _heal < HealEvery && TvInterop.IsLive) return;

                _lastSource = source; _lastUrl = url; _lastTitle = title; _lastPlaylist = playlist;
                _lastLive = TvInterop.IsLive; _lastSegmented = TvInterop.IsSegmentedVod;
                _lastPaused = TvInterop.Paused; _lastPowered = TvInterop.PoweredOff;
                _lastShuffle = TvInterop.Shuffle; _lastPlaylistIndex = TvInterop.PlaylistIndex;
                _heal = 0f;
                BroadcastState(new TvStateMessage
                {
                    SourceUrl = source,
                    StreamUrl = url, StreamTitle = title, PlaylistUrl = playlist,
                    IsLive = TvInterop.IsLive, IsSegmentedVod = TvInterop.IsSegmentedVod,
                    IsPlaylist = !string.IsNullOrEmpty(playlist), PlaylistIndex = TvInterop.PlaylistIndex,
                    Position = TvInterop.Position, Paused = TvInterop.Paused,
                    PoweredOff = TvInterop.PoweredOff, Shuffle = TvInterop.Shuffle
                });
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("TvSync host: " + e.Message); }
        }

        public void HostApplyOp(TvOpMessage message)
        {
            if (!TvInterop.Present || message == null) return;
            TvInterop.HostApply(message.Op, message.Url, message.Title, message.Value);
        }

        public void ClientApplyState(TvStateMessage message)
        {
            if (!TvInterop.Present || message == null) return;
            TvInterop.ApplyHostState(message.SourceUrl, message.StreamUrl, message.StreamTitle, message.PlaylistUrl,
                message.IsLive, message.IsSegmentedVod, message.PlaylistIndex, message.Position,
                message.Paused, message.PoweredOff, message.Shuffle);
        }

        private static void Send(byte op, string url = null, string title = null, double value = 0)
        {
            if (CoopCore.Role != CoopRole.Client || TvInterop.ApplyingRemote) return;
            CoopCore.Instance?.SendTvOp(new TvOpMessage { Op = op, Url = url, Title = title, Value = value });
        }

        public static bool ClientGlobalInputPrefix()
        {
            if (CoopCore.Role != CoopRole.Client || !TvInterop.Present) return true;
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            if (!alt) return true; // retain the mod's own UI opening path (Alt+Y)
            if (Input.GetKeyDown(KeyCode.Y)) return true;
            if (Input.GetKeyDown(KeyCode.P)) Send(Pause);
            else if (Input.GetKeyDown(KeyCode.Equals)) Send(Next);
            else if (Input.GetKeyDown(KeyCode.Minus)) Send(Previous);
            else if (Input.GetKey(KeyCode.RightBracket)) Send(Seek, value: 5);
            else if (Input.GetKey(KeyCode.LeftBracket)) Send(Seek, value: -5);
            else if (Input.GetKeyDown(KeyCode.S)) Send(Shuffle);
            else if (Input.GetKeyDown(KeyCode.O)) Send(Power);
            else return false; // prevent the TV mod from changing shared state locally
            return false;
        }

        public static bool ClientQualityChoicePrefix(bool download)
        {
            if (CoopCore.Role != CoopRole.Client || !TvInterop.Present) return true;
            string url = TvInterop.PendingUrl;
            if (string.IsNullOrWhiteSpace(url)) return true;
            TvInterop.FinishQualitySelection();
            Send(Open, url, "Shared stream");
            return false;
        }

        public static void ApplyPatches(Harmony h)
        {
            if (!TvInterop.Present) return;
            Try(h, "HandleGlobalInput", new HarmonyMethod(typeof(TvSync), nameof(ClientGlobalInputPrefix)));
            Try(h, "OnQualityChoice", new HarmonyMethod(typeof(TvSync), nameof(ClientQualityChoicePrefix)));
            Try(h, "StreamYouTube", null,
                postfix: new HarmonyMethod(typeof(TvInterop), nameof(TvInterop.StreamStartedPostfix)));
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
            if (CoopCore.Role != CoopRole.Client || !TvInterop.Present) return true;
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
                if (original == null) { CoopPlugin.Log.LogWarning("TV patch target missing: " + method); return; }
                h.Patch(original, prefix: prefix, postfix: postfix);
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("TV patch failed " + method + ": " + e.Message); }
        }

    }
}
