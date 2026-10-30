using System;
using CardShopCoop.Api;

namespace CardShopCoop.CustomTv
{
    /// <summary>Operation that produced the current <see cref="TvState"/> snapshot.</summary>
    public enum TvOperation : byte
    {
        None = 0,
        Open = 1,
        Playlist = 2,
        Next = 3,
        Previous = 4,
        Pause = 5,
        Power = 6,
        Shuffle = 7,
        Seek = 8,
        Barrier = 9,
        Error = 10,
    }

    /// <summary>
    /// One immutable snapshot of one TV. It is extended information owned by the TV's
    /// <see cref="InteractableObject"/>, so the identity module replicates it per TV with the
    /// same stable id on both peers. The operation and action id identify which client action
    /// (if any) produced this snapshot, and <see cref="Rejected"/> tells the acting client to
    /// reverse its prediction before the snapshot is applied.
    /// </summary>
    [HostOnlyInformation]
    public sealed class TvState : ExtendGameObjectInformation<InteractableObject>, IActionValue
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
        public TvOperation Operation;
        public Guid ActionId
        {
            get; init;
        }
        public bool Rejected
        {
            get; init;
        }
    }
}
