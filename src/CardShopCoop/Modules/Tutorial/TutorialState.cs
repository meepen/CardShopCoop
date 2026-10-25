using System;
using CardShopCoop.Api;

namespace CardShopCoop.Modules.Tutorial
{
    /// <summary>One recorded tutorial condition value, in the game's own condition space. The
    /// game defines sixteen conditions, so a complete value list is a small snapshot.</summary>
    internal sealed class TutorialValue
    {
        public ETutorialTaskCondition Condition
        {
            get; init;
        }

        public float Value
        {
            get; init;
        }
    }

    /// <summary>
    /// The host-authoritative tutorial progression: the active index plus every recorded condition
    /// value. It is session-wide because the tutorial is one shared progression, and host-only
    /// because a guest writes progress through its validated intent. The value is a complete
    /// snapshot, so the store's join baseline and idempotent apply replace the old state message
    /// and the client's pending-delta queue.
    /// </summary>
    [GlobalInformation]
    [HostOnlyInformation]
    internal sealed class TutorialState : IActionValue
    {
        public int TutorialIndex
        {
            get; init;
        }

        public TutorialValue[] Values
        {
            get; init;
        } = Array.Empty<TutorialValue>();

        /// <summary>The client action this value answers, or <see cref="Guid.Empty"/> for a
        /// host-originated change or the join baseline.</summary>
        public Guid ActionId
        {
            get; init;
        }

        /// <summary>True only for the pre-intent state published when a validator refuses an
        /// intent, so the requester undoes its optimistic run before applying this authority.</summary>
        public bool Rejected
        {
            get; init;
        }
    }
}
