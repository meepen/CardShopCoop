using System;
using CardShopCoop.Api;
using CardShopCoop.Net;

namespace CardShopCoop.CustomTv
{
    /// <summary>One client playback intent. The host applies it once and publishes a new
    /// <see cref="TvState"/>.</summary>
    [NetworkMessage]
    public sealed class TvOpMessage : ActionMessage
    {
        internal const byte SeekOp = 6;

        /// <summary>The TV this intent targets. The identity module maps it to the same placed
        /// object on the host, so multi-TV shops do not fall back to whichever TV was last seen.</summary>
        public InteractableObject Tv;

        public byte Op;
        public string Url;
        public string Title;
        public double Value;

        /// <summary>The playback state before the local game action, used only for the client's
        /// own undo on rejection. Never serialized; the host applies the command itself.</summary>
        [NonSerialized]
        internal TvInterop.PlaybackUndoState Previous;

        [NonSerialized]
        internal double UndoSeek;

        public override void OnApply()
            => TvInterop.ApplyOp(Tv, Op, Url, Title, Value);

        public override void OnUndo()
        {
            if (Op == SeekOp)
            {
                TvInterop.UndoSeek(Tv, UndoSeek);
                return;
            }

            TvInterop.UndoOp(Tv, Previous);
        }
    }
}
