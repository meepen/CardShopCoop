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
    /// (if any) produced this snapshot so the client can confirm or roll back its action.
    /// </summary>
    [HostOnlyInformation]
    public sealed class TvState : ExtendGameObjectInformation<InteractableObject>, IActionValue
    {
        public string SourceUrl
        {
            get; init;
        }
        public string StreamUrl
        {
            get; init;
        }
        public string StreamTitle
        {
            get; init;
        }
        public string PlaylistUrl
        {
            get; init;
        }
        public bool IsLive
        {
            get; init;
        }
        public bool IsSegmentedVod
        {
            get; init;
        }
        public int PlaylistIndex
        {
            get; init;
        }
        public double Position
        {
            get; init;
        }
        public bool Paused
        {
            get; init;
        }
        public bool PoweredOff
        {
            get; init;
        }
        public bool Shuffle
        {
            get; init;
        }
        public bool Barrier
        {
            get; init;
        }
        public bool Resume
        {
            get; init;
        }
        public int Generation
        {
            get; init;
        }
        public TvOperation Operation
        {
            get; init;
        }
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
