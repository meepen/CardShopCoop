using System;
using System.Collections.Generic;
using CardShopCoop.Modules.Grading;

namespace CardShopCoop.Modules.World
{
    /// <summary>
    /// Open-binder mirror for card changes the MOD applied (everything mutated behind
    /// <see cref="ApplyingRemoteCards"/>: remote deltas, prediction replays, grading moves).
    ///
    /// The game re-lays-out the binder only when it is OPENED
    /// (CollectionBinderFlipAnimCtrl.Update -> OnSortingMethodUpdated, gated by m_CanUpdateSort).
    /// Taking a card out is a SINGLE-SLOT update: SetSingleCard on that slot (count down, or hidden
    /// once empty) plus hiding the slot's m_InteractableCard3dList raycast proxy when the slot
    /// empties - vanilla deliberately leaves a visible hole and never moves another card.
    ///
    /// The mod must mimic that. The old behaviour invoked OnSortingMethodUpdated on a LIVE book,
    /// which (a) made every card jump to a new slot instead of leaving the hole, and (b) never
    /// re-synced the proxies: a proxy hidden when its slot emptied stayed hidden after the
    /// re-sort moved another card into that slot, so the card could be seen but not taken
    /// ("cannot take that card out after it moves"). The opposite mismatch was possible too (an
    /// active proxy over an emptied slot, clicking a card the player no longer owns).
    ///
    /// So: single-slot updates only; a remote gain refreshes the count of an already-visible
    /// slot but never reveals a newly gained card into an empty slot (the other player's copy
    /// may move again before this binder is reopened), and SetCanUpdateSort(true) arms the
    /// game's own open-time re-sort so the preserved holes heal on the next open.
    /// The one exception is a LOCAL repair (NotifyCardsChanged: a rejected prediction replaying
    /// its inverse, a trade undo, a grading claim restore) - the player watched that card leave
    /// the slot, so those are reflected even when the net change is a gain.
    /// </summary>
    internal sealed partial class WorldCardInteraction
    {
        /// <summary>One card changed by a mod apply this frame, folded by wire identity so the
        /// same card's add/remove pair nets out.</summary>
        private sealed class ChangedBinderCard
        {
            internal ECardExpansionType Expansion;
            internal bool IsDestiny;
            internal int SaveIndex;
            internal int EncodedGrade;
            internal int Added;
            internal int Removed;
        }

        private static readonly Dictionary<string, ChangedBinderCard> _changedBinderCards =
            new(StringComparer.Ordinal);
        private static bool _binderRefreshPending;

        /// <summary>Set by NotifyCardsChanged: the caller is repairing LOCAL state (a rejected
        /// prediction replaying its inverse, a trade accept/undo, a grading claim restore), so
        /// the affected slots must be re-synced even for a net GAIN - the player watched that
        /// card leave the binder, and a rejection putting it back must put it back visually.
        /// Remote-arrival gains never set this: they wait for the next open, like vanilla.</summary>
        private static bool _binderFullResync;

        // Binder internals the refresh path needs beyond the four cached in
        // WorldCardInteraction. Private in both builds, so AccessTools-cached.
        private static readonly System.Reflection.FieldInfo FiBinderSortedIndexList =
            HarmonyLib.AccessTools.Field(typeof(CollectionBinderFlipAnimCtrl), "m_SortedIndexList");
        private static readonly System.Reflection.FieldInfo FiBinderIndex =
            HarmonyLib.AccessTools.Field(typeof(CollectionBinderFlipAnimCtrl), "m_Index");
        private static readonly System.Reflection.FieldInfo FiBinderSortingType =
            HarmonyLib.AccessTools.Field(typeof(CollectionBinderFlipAnimCtrl), "m_SortingType");
        private static readonly System.Reflection.FieldInfo FiBinderInteractableCardList =
            HarmonyLib.AccessTools.Field(typeof(CollectionBinderFlipAnimCtrl),
                "m_InteractableCard3dList");

        /// <summary>Records one card movement the mod applied itself. Called from the observer
        /// patches for every CPlayerData mutation made behind ApplyingRemoteCards. Never touches
        /// the UI: the flush in <see cref="RefreshOpenBinder"/> runs once at the end of the
        /// frame.</summary>
        internal static void RecordCardChanged(CardData card, int amount, bool isAdd)
        {
            if (card == null || amount <= 0)
            {
                return;
            }

            var key = CardPriceKey(card);
            if (key == null)
            {
                return;
            }

            if (!_changedBinderCards.TryGetValue(key, out var change))
            {
                change = new ChangedBinderCard
                {
                    Expansion = card.expansionType,
                    IsDestiny = card.isDestiny,
                    SaveIndex = CPlayerData.GetCardSaveIndex(card),
                    EncodedGrade = GradingApi.Encoded(card),
                };
                _changedBinderCards.Add(key, change);
            }

            if (isAdd)
            {
                // CPlayerData.AddCard stores exactly ONE graded row per call, no matter how
                // large addAmount is (CPlayerData.cs:1602-1611); over-counting it here would
                // mask a genuine graded removal on the next line of the ledger.
                change.Added += card.cardGrade > 0 ? 1 : amount;
            }
            else
            {
                change.Removed += amount;
            }
            _binderRefreshPending = true;
        }

        /// <summary>Consumes the frame's recorded changes once RefreshOpenBinder has run. The
        /// entries must not survive the flush: they are per-frame deltas, and a stale entry would
        /// let an unrelated later flush rewrite (or re-show) a slot from old data.</summary>
        internal static void ClearBinderMirrorFrame()
        {
            _changedBinderCards.Clear();
            _binderFullResync = false;
        }

        internal static void ResetBinderMirror()
        {
            ClearBinderMirrorFrame();
            _binderRefreshPending = false;
        }

        /// <summary>RefreshOpenBinder's live-book half: rewrite only the slots whose cards this
        /// side changed, exactly like vanilla's own take-out update. The album total is refreshed
        /// by the caller. A remote net gain only refreshes the count of a slot already visible;
        /// a local repair (see _binderFullResync) may also re-show the hole it left.</summary>
        private static void ApplyOpenBinderCardChanges(CollectionBinderFlipAnimCtrl ctrl)
        {
            var graded = FiBinderIsGradedAlbum != null && (bool)FiBinderIsGradedAlbum.GetValue(ctrl);
            if (graded)
            {
                ApplyGradedBinderRemovals(ctrl);
            }
            else
            {
                ApplyUngradedBinderRemovals(ctrl);
            }
        }

        private static void ApplyUngradedBinderRemovals(CollectionBinderFlipAnimCtrl ctrl)
        {
            var expansion = FiBinderExpansionType != null
                ? (ECardExpansionType)FiBinderExpansionType.GetValue(ctrl)
                : ECardExpansionType.None;
            var sorted = FiBinderSortedIndexList != null
                ? FiBinderSortedIndexList.GetValue(ctrl) as List<int>
                : null;
            var groups = ctrl.m_BinderPageGrpList;
            if (sorted == null || groups == null || groups.Count == 0)
            {
                return;
            }

            var pageIndex = FiBinderIndex != null ? (int)FiBinderIndex.GetValue(ctrl) : 0;
            var sorting = FiBinderSortingType != null
                ? (ECollectionSortingType)FiBinderSortingType.GetValue(ctrl)
                : ECollectionSortingType.Default;

            // Position lookup built at most once per flush: m_SortedIndexList holds each save
            // index exactly once, so the affected slot is one dictionary hit per changed card.
            Dictionary<int, int> positions = null;
            foreach (var change in _changedBinderCards.Values)
            {
                if (change.EncodedGrade != 0 || change.Expansion != expansion)
                {
                    continue;
                }

                // A remote net gain only refreshes the count of a slot the player can already
                // see; it must never reveal a newly gained card into an empty slot while the
                // book is open, because that card is the other player's gain and may move
                // again before this binder is reopened. A local repair (a rejected prediction
                // replaying its inverse, _binderFullResync) is exempt: the player watched
                // that card leave, so putting it back may re-show its slot.
                var remoteGain = !_binderFullResync && change.Removed <= change.Added;
                if (positions == null)
                {
                    positions = BuildBinderPositionIndex(sorted);
                }
                if (!positions.TryGetValue(BinderLookupIndex(change), out var position))
                {
                    continue;
                }

                // Page groups are [current page, next page, previous page] (see
                // CollectionBinderFlipAnimCtrl.Update's flip handling).
                var page = position / 12 + 1;
                var group = page == pageIndex ? 0 : page == pageIndex + 1 ? 1
                    : page == pageIndex - 1 ? 2 : -1;
                if (group < 0 || group >= groups.Count)
                {
                    continue; // not on a rendered page; the live render on flip reads real counts
                }

                var pageGroup = groups[group];
                if (pageGroup == null || pageGroup.m_CardList == null)
                {
                    continue;
                }
                var slot = position % 12;
                if (slot >= pageGroup.m_CardList.Count)
                {
                    continue;
                }

                var count = CPlayerData.GetCardAmountByIndex(change.SaveIndex, expansion,
                    change.IsDestiny);
                if (remoteGain)
                {
                    // Count text only: never touch the card art, never un-hide a hole, and
                    // keep the same DuplicatePrice display subtraction SetSingleCard applies.
                    var ui = pageGroup.m_CardList[slot];
                    if (ui == null || !ui.gameObject.activeSelf)
                    {
                        continue; // the gain's slot is a hole; it stays one until the next open
                    }
                    var duplicate = sorting == ECollectionSortingType.DuplicatePrice;
                    var displayCount = duplicate ? count - 1 : count;
                    if (displayCount > 0)
                    {
                        ui.SetCardCountText(displayCount, duplicate);
                        if (group == 0)
                        {
                            SyncBinderSlotProxy(ctrl, slot, true);
                        }
                    }
                    continue;
                }

                var shown = CPlayerData.GetCardData(change.SaveIndex, expansion, change.IsDestiny);
                pageGroup.SetSingleCard(slot, shown, count, sorting);
                if (group == 0)
                {
                    // SetSingleCard hides a DuplicatePrice slot whenever the DISPLAYED count
                    // (count-1 in that sort) is <= 0, and vanilla's take-out mirrors that same
                    // subtraction for its proxy decision (Update/OnRightMouseButtonUp ~232-242).
                    var proxyVisible = count > (sorting == ECollectionSortingType.DuplicatePrice ? 1 : 0);
                    SyncBinderSlotProxy(ctrl, slot, proxyVisible);
                }
            }
        }

        /// <summary>The graded album is a list of owned rows, not the collected-count table, so a
        /// removed row has no count to re-read. Hide the visible slot still showing that exact
        /// card (identity includes the encoded grade, which makes each Grading Overhaul
        /// certificate unique); a LOCAL repair (a rejection putting a card back, _binderFullResync)
        /// shows the hidden slot that still carries the card's data. Remote-arrival gains wait for
        /// the next open, like the normal album.</summary>
        private static void ApplyGradedBinderRemovals(CollectionBinderFlipAnimCtrl ctrl)
        {
            var groups = ctrl.m_BinderPageGrpList;
            if (groups == null || groups.Count == 0)
            {
                return;
            }
            var sorting = FiBinderSortingType != null
                ? (ECollectionSortingType)FiBinderSortingType.GetValue(ctrl)
                : ECollectionSortingType.Default;

            foreach (var change in _changedBinderCards.Values)
            {
                if (change.EncodedGrade == 0)
                {
                    continue;
                }

                // Removals mirror in BOTH modes: a grading reservation or a replayed rejection
                // can take a graded card away while the album is open.
                for (var remaining = change.Removed - change.Added; remaining > 0; remaining--)
                {
                    if (!HideOneVisibleGradedCard(ctrl, groups, change, sorting))
                    {
                        break; // nothing visible matched; the reopen re-render drops it
                    }
                }

                if (!_binderFullResync)
                {
                    continue; // remote-arrival gains wait for the next open, like the normal album
                }

                for (var remaining = change.Added - change.Removed; remaining > 0; remaining--)
                {
                    if (!ShowOneHiddenGradedCard(ctrl, groups, change, sorting))
                    {
                        break; // no hidden copy on a rendered page; the reopen re-render adds it
                    }
                }
            }
        }

        /// <summary>A hidden graded slot keeps its last card data (SetSingleCard only deactivates
        /// the group), so a restore can reuse the very hole the removal left - the same way the
        /// ungraded path reads the hole's live count back.</summary>
        private static bool ShowOneHiddenGradedCard(CollectionBinderFlipAnimCtrl ctrl,
            List<BinderPageGrp> groups, ChangedBinderCard change, ECollectionSortingType sorting)
        {
            for (var pass = 0; pass < 2; pass++)
            {
                var requireExactGrade = pass == 0;
                for (var group = 0; group < groups.Count && group < 3; group++)
                {
                    var pageGroup = groups[group];
                    if (pageGroup == null || pageGroup.m_CardList == null)
                    {
                        continue;
                    }
                    for (var slot = 0; slot < pageGroup.m_CardList.Count; slot++)
                    {
                        var ui = pageGroup.m_CardList[slot];
                        if (ui == null || ui.m_CardUI == null || ui.gameObject.activeSelf)
                        {
                            continue; // only a hidden slot can be the hole this card left
                        }
                        if (!GradedSlotMatches(ui.m_CardUI.GetCardData(), change, requireExactGrade))
                        {
                            continue;
                        }

                        pageGroup.SetSingleCard(slot, ui.m_CardUI.GetCardData(), 1, sorting);
                        if (group == 0)
                        {
                            SyncBinderSlotProxy(ctrl, slot, true);
                        }
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool HideOneVisibleGradedCard(CollectionBinderFlipAnimCtrl ctrl,
            List<BinderPageGrp> groups, ChangedBinderCard change, ECollectionSortingType sorting)
        {
            for (var pass = 0; pass < 2; pass++)
            {
                var requireExactGrade = pass == 0;
                for (var group = 0; group < groups.Count && group < 3; group++)
                {
                    var pageGroup = groups[group];
                    if (pageGroup == null || pageGroup.m_CardList == null)
                    {
                        continue;
                    }
                    for (var slot = 0; slot < pageGroup.m_CardList.Count; slot++)
                    {
                        var ui = pageGroup.m_CardList[slot];
                        if (ui == null || ui.m_CardUI == null || !ui.gameObject.activeSelf)
                        {
                            continue; // an already-empty slot keeps stale data; never re-hide that
                        }
                        if (!GradedSlotMatches(ui.m_CardUI.GetCardData(), change, requireExactGrade))
                        {
                            continue;
                        }

                        pageGroup.SetSingleCard(slot, null, 0, sorting);
                        if (group == 0)
                        {
                            SyncBinderSlotProxy(ctrl, slot, false);
                        }
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>Graded-copy identity for slot matching. The EXACT pass compares the encoded
        /// grade too (with Grading Overhaul the certificate serial distinguishes copies). The
        /// coarse pass is required because CardUI.SetCardUI CLAMPS a grade above 10 to 10 IN
        /// PLACE on the display copy (decompiled/CardUI.cs:363-369), so a displayed Grading
        /// Overhaul card can no longer carry its encoded grade; its same-card copies are then
        /// interchangeable on screen, which is all a hide/show needs. Without GO the exact pass
        /// always matches.</summary>
        private static bool GradedSlotMatches(CardData shown, ChangedBinderCard change,
            bool requireExactGrade)
        {
            if (shown == null || shown.cardGrade <= 0
                || shown.expansionType != change.Expansion
                || shown.isDestiny != change.IsDestiny
                || CPlayerData.GetCardSaveIndex(shown) != change.SaveIndex)
            {
                return false;
            }
            return !requireExactGrade || GradingApi.Encoded(shown) == change.EncodedGrade;
        }

        private static Dictionary<int, int> BuildBinderPositionIndex(List<int> sorted)
        {
            var positions = new Dictionary<int, int>(sorted.Count);
            for (var i = 0; i < sorted.Count; i++)
            {
                positions[sorted[i]] = i;
            }
            return positions;
        }

        /// <summary>The Ghost expansion's black (destiny) half sits after the normal half in
        /// m_SortedIndexList; UpdateBinderAllCardUI re-derives the base index by subtracting that
        /// offset, so the lookup must add it back.</summary>
        private static int BinderLookupIndex(ChangedBinderCard change)
        {
            if (change.Expansion != ECardExpansionType.Ghost || !change.IsDestiny)
            {
                return change.SaveIndex;
            }
            var shown = InventoryBase.GetShownMonsterList(change.Expansion);
            if (shown == null)
            {
                return change.SaveIndex;
            }
            return change.SaveIndex
                + shown.Count * CPlayerData.GetCardAmountPerMonsterType(change.Expansion);
        }

        /// <summary>Keep one slot's raycast proxy in step with its visual. Vanilla deactivates
        /// the proxy when the player takes the last copy (CollectionBinderFlipAnimCtrl
        /// .OnRightMouseButtonUp), so a remote change must do the same - and the reverse, or the
        /// slot would stay unclickable after its card comes back.</summary>
        private static void SyncBinderSlotProxy(CollectionBinderFlipAnimCtrl ctrl, int slot,
            bool visible)
        {
            var proxies = FiBinderInteractableCardList != null
                ? FiBinderInteractableCardList.GetValue(ctrl) as List<InteractableCard3d>
                : null;
            if (proxies == null || slot < 0 || slot >= proxies.Count)
            {
                return;
            }
            var proxy = proxies[slot];
            if (proxy == null)
            {
                return;
            }

            if (visible)
            {
                if (!proxy.gameObject.activeSelf)
                {
                    proxy.gameObject.SetActive(true);
                }
                return;
            }

            if (!proxy.gameObject.activeSelf)
            {
                return;
            }
            if (ReferenceEquals(ctrl.m_CurrentRaycastedInteractableCard3d, proxy))
            {
                proxy.OnRaycastEnded();
                ctrl.m_CurrentRaycastedInteractableCard3d = null;
            }
            proxy.gameObject.SetActive(false);
        }
    }
}
