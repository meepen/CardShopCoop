using System.Collections.Generic;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// The one place that reads and writes a packaging box's open/closed look.
    ///
    /// The levers differ per box type - item boxes use the (bool, bool) setter, card boxes
    /// the (bool) setter, and the SHELF box is driven by its m_BoxAnim "Open"/"Close" clips
    /// (the game never calls SetOpenCloseBox on a shelf, so its mesh flags alone don't move).
    /// A single bidirectional helper keeps them together and lets any caller correct drift,
    /// rather than one function to read and several to set.
    /// </summary>
    public static class BoxVisuals
    {
        // Which boxes have had their open state applied by us. Needed because the shelf box
        // never maintains a readable open flag; this lets EnsureOpenState skip replaying the
        // animation every snapshot while still correcting a wrong state.
        private static readonly Dictionary<InteractablePackagingBox, bool> Applied
            = new Dictionary<InteractablePackagingBox, bool>();

        public static bool ReadOpen(InteractablePackagingBox box)
        {
            if (box == null)
                return false;
            if (box is InteractablePackagingBox_Shelf)
                return Applied.TryGetValue(box, out var tracked) && tracked;
            if (box is InteractablePackagingBox_Item)
            {
                // IsBoxOpened() deliberately reports false while the game's 0.85s
                // open/close animation is running. The private flag is the committed
                // state and is what must travel over the wire during that animation.
                return BoxFields.ItemBoxOpened != null
                    && (bool)BoxFields.ItemBoxOpened.GetValue(box);
            }
            try
            {
                return box.IsBoxOpened();
            }
            catch { return false; }
        }

        public static void EnsureOpenState(InteractablePackagingBox box, bool open)
        {
            if (box == null)
                return;
            // Idempotent: shelves compare against our tracked value (they have no readable
            // flag); item/card boxes compare against their own live state.
            if (box is InteractablePackagingBox_Shelf)
            {
                if (Applied.TryGetValue(box, out var cur) && cur == open)
                    return;
            }
            else
            {
                try
                {
                    if (ReadOpen(box) == open)
                        return;
                }
                catch { }
            }

            try
            {
                switch (box)
                {
                    case InteractablePackagingBox_Shelf shelf:
                        shelf.SetOpenCloseBox(open);
                        if (shelf.m_BoxAnim != null)
                            shelf.m_BoxAnim.Play(open ? "Open" : "Close");
                        break;
                    case InteractablePackagingBox_Card card:
                        card.SetOpenCloseBox(open);
                        break;
                    case InteractablePackagingBox_Item item:
                        item.SetOpenCloseBox(open, isPlayer: false);
                        break;
                }
            }
            catch { }
            Applied[box] = open;
        }

        /// <summary>Apply an explicit box lifecycle event. Unlike snapshot reconciliation,
        /// creation/box-up events must always write the visual state: the newly-created
        /// object may have inherited a prefab or stale cached appearance.</summary>
        public static void ApplyOpenEvent(InteractablePackagingBox box, bool open)
        {
            if (box == null)
                return;
            switch (box)
            {
                case InteractablePackagingBox_Shelf shelf:
                    shelf.SetOpenCloseBox(open);
                    if (shelf.m_BoxAnim != null)
                        shelf.m_BoxAnim.Play(open ? "Open" : "Close");
                    break;
                case InteractablePackagingBox_Card card:
                    card.SetOpenCloseBox(open);
                    break;
                case InteractablePackagingBox_Item item:
                    item.SetOpenCloseBox(open, isPlayer: false);
                    break;
            }
            Applied[box] = open;
        }

        public static void Forget(InteractablePackagingBox box)
        {
            if (box != null)
                Applied.Remove(box);
        }

        /// <summary>The single visibility writer: show/hide a box AND its world label and
        /// compartment price tag together, so the three families cannot drift.</summary>
        public static void SetVisible(InteractablePackagingBox box, bool visible)
        {
            if (box == null)
                return;
            try
            {
                if (box.gameObject.activeSelf != visible)
                    box.gameObject.SetActive(visible);
            }
            catch { }
            BoxPlacement.SetLabelVisible(box, visible);
            try
            {
                switch (box)
                {
                    case InteractablePackagingBox_Item item:
                        item.m_ItemCompartment?.SetPriceTagVisibility(visible);
                        break;
                    case InteractablePackagingBox_Shelf shelf:
                        shelf.m_ItemCompartment?.SetPriceTagVisibility(visible);
                        break;
                }
            }
            catch { }
        }

        public static void Reset()
        {
            Applied.Clear();
        }
    }
}
