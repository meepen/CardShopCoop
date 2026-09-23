using System;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;

namespace CardShopCoop.Modules.Tv
{
    /// <summary>One join-time playback baseline.</summary>
    [NetworkMessage]
    public sealed class TvBaselineMessage : INetMessage
    {
        public string SourceUrl;
        public string StreamUrl;
        public string StreamTitle;
        public string PlaylistUrl;
        public bool IsLive;
        public bool IsSegmentedVod;
        public int PlaylistIndex;
        public double Position;
        public bool Paused;
        public bool PoweredOff;
        public bool Shuffle;
        public bool Barrier;
        public bool Resume;
        public int Generation;
    }

    /// <summary>Media identity changed because a stream or playlist item was opened.</summary>
    [NetworkMessage]
    public sealed class TvMediaDeltaMessage : IPredictedMessage
    {
        public const byte Open = 1;
        public const byte Playlist = 2;
        public const byte Next = 3;
        public const byte Previous = 4;

        public Guid PredictionId
        {
            get; set;
        }
        public byte Operation;
        public string SourceUrl;
        public string StreamUrl;
        public string StreamTitle;
        public string PlaylistUrl;
        public bool IsLive;
        public bool IsSegmentedVod;
        public int PlaylistIndex;
        public double Position;
        public bool Paused;
        public bool PoweredOff;
        public bool Shuffle;
        public bool Barrier;
        public bool Resume;
        public int Generation;
    }

    [NetworkMessage]
    public sealed class TvPauseDeltaMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public bool Paused;
    }

    [NetworkMessage]
    public sealed class TvPowerDeltaMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public bool PoweredOff;
    }

    [NetworkMessage]
    public sealed class TvShuffleDeltaMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public bool Shuffle;
    }

    [NetworkMessage]
    public sealed class TvSeekDeltaMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public double Position;
    }

    /// <summary>Signals completion of, or resumption after, an actual playback barrier.</summary>
    [NetworkMessage]
    public sealed class TvBarrierDeltaMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public bool Barrier;
        public bool Resume;
    }

    /// <summary>Signals an actual player error, rather than correcting ordinary state.</summary>
    [NetworkMessage]
    public sealed class TvPlaybackErrorDeltaMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public int Generation;
    }

    /// <summary>One client playback intent. The host applies it once.</summary>
    [NetworkMessage]
    public sealed class TvOpMessage : IPredictedMessage
    {
        public Guid PredictionId
        {
            get; set;
        }
        public byte Op;
        public string Url;
        public string Title;
        public double Value;
    }
}
