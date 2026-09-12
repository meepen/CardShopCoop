using System.Collections.Generic;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Family-specific game access for the shared <see cref="BoxEngine"/>. The engine
    /// owns possession, identity, wire routing, snapshots, and the absent-id sweep; a
    /// family supplies its live list, content encode/decode, spawn/destroy, the local
    /// possession read, and how to render a state. There is no family-specific
    /// networking anywhere.
    /// </summary>
    public interface IBoxFamily
    {
        BoxFamily Family
        {
            get;
        }

        /// <summary>True when a content mismatch should destroy and recreate the client mirror
        /// (delivery/item boxes, whose contents legitimately change). False for content that is
        /// immutable for a given id (graded card boxes), where a mismatch means data drift, not
        /// a change; recreating the mirror there multiplied boxes, so immutable families keep
        /// the existing mirror and let the host's Removed retire it.</summary>
        bool RecreateOnContentMismatch
        {
            get;
        }

        /// <summary>Applies only the entry's CONTENT/OPEN state to an existing local box, leaving
        /// pose, physics and possession untouched. The host uses this to honor a non-owner's
        /// legitimate loose-box edit (opening/closing, adding or taking items) without letting
        /// the report overwrite the authoritative pose - the stale-pose guard stays intact.
        /// Families whose content is immutable or derived (card, furniture) do nothing.</summary>
        void ReconcileContent(InteractablePackagingBox box, in BoxWire w);

        IList<InteractablePackagingBox> LiveBoxes();

        /// <summary>Reads the family content into the wire entry (host snapshot / client report).</summary>
        void FillContent(InteractablePackagingBox box, ref BoxWire w);

        /// <summary>True if this box already holds the entry's content (client resolve).</summary>
        bool ContentMatches(InteractablePackagingBox box, in BoxWire w);

        /// <summary>Cheap content change signature (host snapshot gating + client report edge).</summary>
        int ContentSignature(InteractablePackagingBox box);

        /// <summary>Client-side create of a box from the wire (the game's own spawn recipe).</summary>
        InteractablePackagingBox Spawn(in BoxWire w);

        /// <summary>Destroys a mirrored box (client retire / sell / trash).</summary>
        void DestroyBox(InteractablePackagingBox box);

        /// <summary>
        /// Renders an authoritative state onto a local box: visibility, label, physics,
        /// pose, velocity, and store-on-rack. Called on receivers, and on the host for a
        /// box it owns. <paramref name="isOwner"/> is true on the box's own machine.
        /// </summary>
        void ApplyState(InteractablePackagingBox box, in BoxWire w, bool isOwner);

        /// <summary>
        /// Reads the LOCAL game object's possession. Held/Placing come from the game's own
        /// flags; Free carries the current pose/velocity; stored reports a rack address.
        /// Returns false if the box is mid-spawn/mid-unpack this frame.
        /// </summary>
        bool TryReadLocal(InteractablePackagingBox box, out BoxPossession possession,
            out Vector3 pos, out float yaw, out Vector3 velocity, out Vector3 angularVelocity,
            out bool stored);
    }
}
