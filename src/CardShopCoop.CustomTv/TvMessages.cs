using System;
using CardShopCoop.Api;
using CardShopCoop.Net;

namespace CardShopCoop.CustomTv
{
    /// <summary>Playback state captured before a local TV control, used to reverse it.</summary>
    internal readonly struct PlaybackUndoState
    {
        internal readonly string SourceUrl;
        internal readonly string StreamUrl;
        internal readonly string StreamTitle;
        internal readonly string PlaylistUrl;
        internal readonly bool IsLive;
        internal readonly bool IsSegmentedVod;
        internal readonly int PlaylistIndex;
        internal readonly double Position;
        internal readonly bool Paused;
        internal readonly bool PoweredOff;
        internal readonly bool Shuffle;
        internal readonly bool Barrier;
        internal readonly int Generation;

        internal PlaybackUndoState(string sourceUrl, string url, string title, string playlistUrl,
            bool live, bool segmented, int playlistIndex, double position, bool paused,
            bool powered, bool shuffle, bool barrier, int generation)
        {
            SourceUrl = sourceUrl;
            StreamUrl = url;
            StreamTitle = title;
            PlaylistUrl = playlistUrl;
            IsLive = live;
            IsSegmentedVod = segmented;
            PlaylistIndex = playlistIndex;
            Position = position;
            Paused = paused;
            PoweredOff = powered;
            Shuffle = shuffle;
            Barrier = barrier;
            Generation = generation;
        }
    }

    /// <summary>Shared client intent for one TV. <see cref="Tv"/> is also the action ledger key.</summary>
    public abstract class TvActionMessage : ActionMessage
    {
        public InteractableObject Tv;

        /// <summary>The playback state before the local game action, used only for the client's
        /// own undo on rejection. Never serialized; the host applies the command itself.</summary>
        [NonSerialized]
        internal PlaybackUndoState Previous;

        [NonSerialized]
        internal double UndoSeek;

        /// <summary>Set on the host when the operation was refused without changing the TV.
        /// The host then publishes the current state with <see cref="TvState.Rejected"/>.</summary>
        [NonSerialized]
        internal bool Rejected;

        internal abstract TvOperation Operation
        {
            get;
        }

        internal bool IsMedia => Operation == TvOperation.Open
            || Operation == TvOperation.Next
            || Operation == TvOperation.Previous;

        public override void OnApply()
        {
            try
            {
                Apply();
            }
            catch (InvalidOperationException)
            {
                var controller = TvInterop.ControllerFor(Tv);
                if (!TvInterop.Present || controller == null
                    || TvInterop.OwnerOf(controller)?.GetExtendedInformation<TvState>() == null)
                {
                    throw;
                }

                Rejected = true;
            }
        }

        public override void OnUndo(ActionResponseMessage response)
        {
            // TV state is authoritative and travels as TvState extended information, so the
            // response carries nothing this prediction needs. A rejected action only has to put
            // the TV back exactly where it was.
            if (Operation == TvOperation.Seek)
            {
                TvInterop.UndoSeek(Tv, UndoSeek);
                return;
            }

            TvInterop.UndoOp(Tv, Previous);
        }

        protected abstract void Apply();
    }

    [NetworkMessage]
    public sealed class TvNextMessage : TvActionMessage
    {
        internal override TvOperation Operation => TvOperation.Next;

        protected override void Apply() => TvInterop.ApplyOp(Tv, TvOperation.Next, null, null, 0);
    }

    [NetworkMessage]
    public sealed class TvPreviousMessage : TvActionMessage
    {
        internal override TvOperation Operation => TvOperation.Previous;

        protected override void Apply() => TvInterop.ApplyOp(Tv, TvOperation.Previous, null, null, 0);
    }

    [NetworkMessage]
    public sealed class TvOpenMessage : TvActionMessage
    {
        public string Url;
        public string Title;

        internal override TvOperation Operation => TvOperation.Open;

        protected override void Apply() => TvInterop.ApplyOp(Tv, TvOperation.Open, Url, Title, 0);
    }

    [NetworkMessage]
    public sealed class TvPauseMessage : TvActionMessage
    {
        internal override TvOperation Operation => TvOperation.Pause;

        protected override void Apply() => TvInterop.ApplyOp(Tv, TvOperation.Pause, null, null, 0);
    }

    [NetworkMessage]
    public sealed class TvPowerMessage : TvActionMessage
    {
        internal override TvOperation Operation => TvOperation.Power;

        protected override void Apply() => TvInterop.ApplyOp(Tv, TvOperation.Power, null, null, 0);
    }

    [NetworkMessage]
    public sealed class TvShuffleMessage : TvActionMessage
    {
        internal override TvOperation Operation => TvOperation.Shuffle;

        protected override void Apply() => TvInterop.ApplyOp(Tv, TvOperation.Shuffle, null, null, 0);
    }

    [NetworkMessage]
    public sealed class TvSeekMessage : TvActionMessage
    {
        public double Delta;

        internal override TvOperation Operation => TvOperation.Seek;

        protected override void Apply() => TvInterop.ApplyOp(Tv, TvOperation.Seek, null, null, Delta);
    }
}
