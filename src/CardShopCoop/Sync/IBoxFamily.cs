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
