using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Api;
using CardShopCoop.Net;
using CardShopCoop.Runtime;
using CardShopCoop.Util;
using UnityEngine;

namespace CardShopCoop.Modules.World
{
    /// <summary>
    /// The workbench as a synced container: the bulk boxes stored on it, the tier it is currently
    /// bundling, and the box&lt;-&gt;workbench / hand&lt;-&gt;workbench transfers. It mirrors the
    /// machine containers (state-set, host-authoritative), except the bundle's binder deletions
    /// ride the generic card path as one atomic batch and the produced box is minted locally by
    /// vanilla <c>OnTaskCompleted</c> into the acting player's hand. That locally-minted box is the
    /// sole source of the bundle item; the host never hands one back.
    /// </summary>
    internal sealed class WorldWorkbenchInteraction
    {
        /// <summary>Placement kind of the workbench in <see cref="PlacementApi.GetList"/>.</summary>
        internal const int KindWorkbench = 7;

        // m_CurrentItemTypeSpawn: the box tier the bench will bundle. Read when publishing bundle
        // state and written when mirroring the authoritative spawn type; the game only ever sets it
        // as a side effect of PlayBundlingCardBoxSequence (which also plays the whole animation), so
        // there is no method that sets the tier alone and it is reflected.
        private static readonly FieldInfo FiSpawnType =
            ReflectionSurface.RequiredField(typeof(InteractableWorkbench), "m_CurrentItemTypeSpawn");

        // m_IsEditingDeck: the bench's "deck editing" freeze flag. AddItem/RemoveItem no-op while it
        // is set. It has a public setter (SetIsEditingDeck) but no getter, so it is read only to
        // restore it after a mirror rebuild.
        private static readonly FieldInfo FiIsEditingDeck =
            ReflectionSurface.RequiredField(typeof(InteractableWorkbench), "m_IsEditingDeck");

        /// <summary>True while the sync itself mutates a bench, so forwarding patches never
        /// mistake an applied mirror for a local player action.</summary>
        internal static bool ApplyingRemote;

        /// <summary>Client only: the one live client interaction, so the action hooks the
        /// framework invokes on a sent message can reach their prediction state. The host never
        /// predicts, so only a client instance registers here.</summary>
        private static WorldWorkbenchInteraction _activeClient;

        /// <summary>Client: the local prediction state of every unresolved bench action, keyed by
        /// the action object the framework will answer. The action's merge releases the entry.</summary>
        private readonly Dictionary<ActionMessage, PendingClientAction> _pendingClientActions = new();

        private readonly bool _host;
        private readonly Action<INetMessage> _broadcast;
        private readonly Action<ICoopPeer, INetMessage> _send;
        private readonly Func<bool> _inGame;

        internal WorldWorkbenchInteraction(bool host, Action<INetMessage> broadcast,
            Action<ICoopPeer, INetMessage> send, Func<bool> inGame = null)
        {
            _host = host;
            _broadcast = broadcast;
            _send = send;
            _inGame = inGame;
            if (!host)
            {
                _activeClient = this;
            }
        }

        internal void Reset()
        {
            // A join baseline re-runs this on the live client, so the instance stays registered:
            // only its unresolved predictions are dropped. A shutdown constructs a fresh client
            // interaction on the next session, which replaces the registration.
            ApplyingRemote = false;
            _pendingClientActions.Clear();
        }

        private bool InGame() => _inGame == null || _inGame();

        // ---------------- shared lookups ----------------

        private static ShelfManager Sm() => SceneRef<ShelfManager>.Get();

        private static IList Benches()
        {
            var sm = Sm();
            return sm == null ? null : PlacementApi.GetList(sm, KindWorkbench);
        }

        /// <summary>The bench's absolute authoritative state, as broadcast to the other peers.</summary>
        internal WorkbenchStateMessage BuildState(InteractableWorkbench bench)
        {
            var message = new WorkbenchStateMessage
            {
                Bench = bench,
                Bundling = IsBundling(bench),
                SpawnItemType = GetSpawnType(bench),
            };
            var stored = bench?.m_StoredItemList;
            var count = Mathf.Min(stored?.Count ?? 0, 64);
            for (var i = 0; i < count; i++)
            {
                message.StoredTypes.Add(stored[i] != null ? stored[i].GetItemType() : EItemType.None);
            }

            return message;
        }

        /// <summary>The host's post-action state for the acting client, carried in every
        /// <see cref="ActionOutcome"/> so a rejected prediction merges onto authority instead of
        /// inverting a snapshot the host has already moved past.</summary>
        internal WorkbenchStateResponse BuildResponse(InteractableWorkbench bench)
        {
            if (bench == null)
            {
                return null;
            }

            var state = BuildState(bench);
            return new WorkbenchStateResponse
            {
                Bench = state.Bench,
                StoredTypes = state.StoredTypes,
                SpawnItemType = state.SpawnItemType,
                Bundling = state.Bundling,
            };
        }

        private static EItemType GetSpawnType(InteractableWorkbench bench)
            => bench != null && FiSpawnType?.GetValue(bench) is EItemType type
                ? type
                : EItemType.None;

        /// <summary>The bench's own bundling state. The game shows its jank box and plays the
        /// closing animation for the whole bundle (<c>PlayBundlingCardBoxSequence</c> through
        /// <c>OnTaskCompleted</c>/<c>OnPressEsc</c>), so the live animator is the source of truth
        /// instead of a mirrored set of benches.</summary>
        private static bool IsBundling(InteractableWorkbench bench)
            => bench != null && bench.m_JankBoxAnim != null
                && bench.m_JankBoxAnim.gameObject.activeSelf;

        // ---------------- host ----------------

        internal void AppendBaselineMessages(Action<INetMessage> append)
        {
            if (!_host || append == null || !InGame())
            {
                return;
            }

            var list = Benches();
            for (var i = 0; list != null && i < list.Count; i++)
            {
                if (list[i] is InteractableWorkbench bench)
                {
                    append(BuildState(bench));
                }
            }
        }

        /// <summary>The host itself changed a bench (its own player action).</summary>
        internal void HostStorageChanged(InteractableWorkbench bench)
        {
            if (ApplyingRemote)
            {
                return;
            }

            HostPublish(bench);
        }

        /// <summary>The host started its own bundle. Vanilla already turned the bench's jank box
        /// on, so publishing reads that live bundling state and the minted tier.</summary>
        internal void HostBundleStarted(InteractableWorkbench bench)
        {
            if (bench == null)
            {
                return;
            }

            HostPublish(bench);
        }

        /// <summary>The host finished its own bundle. Vanilla already spawned the box into the
        /// host's hand and stopped the bench's jank box; publishing reads that live state.</summary>
        internal void HostBundleCompleted(InteractableWorkbench bench)
        {
            if (bench == null)
            {
                return;
            }

            HostPublish(bench);
        }

        private void HostPublish(InteractableWorkbench bench)
        {
            if (!_host || bench == null || _broadcast == null || !InGame())
            {
                return;
            }

            _broadcast(BuildState(bench));
        }

        /// <summary>Host: apply a client's workbench edit to the authoritative list. Only the
        /// edited types travel; the host's own concurrent edits to the same bench are preserved
        /// instead of being overwritten by the client's whole snapshot. The outcome carries the
        /// host's absolute post-action state on accept and reject alike; on accept the caller
        /// publishes that state to the other peers.</summary>
        internal ActionOutcome HostApplyOp(WorkbenchOpMessage message)
        {
            if (!_host || message == null)
            {
                return ActionOutcome.Reject("the workbench action is missing");
            }

            var bench = message.Bench;
            if (bench == null)
            {
                CoopPlugin.Log.LogWarning(
                    "[workbench] dropped op with no live bench object.");
                return ActionOutcome.Reject("the workbench did not resolve to a live object");
            }

            var rejected = BuildResponse(bench);

            // The client's edit is untrusted: every type it names must be a real item type, and
            // the result must fit the bench's physical slots. Otherwise a client could mint an
            // arbitrary number of arbitrary packs through SyncBench's real item spawns.
            var capacity = bench.m_PosList?.Count ?? 0;
            if (capacity <= 0)
            {
                return ActionOutcome.Reject("the workbench has no storage slots", rejected);
            }

            if (message.SpawnItemType != EItemType.None
                && !IsRealItemType(message.SpawnItemType))
            {
                CoopPlugin.Log.LogWarning("[workbench] rejected op with an invalid spawn type "
                    + message.SpawnItemType + " on bench " + bench.name + ".");
                return ActionOutcome.Reject("the workbench spawn type is invalid", rejected);
            }

            var types = TypesOf(bench);
            var removed = message.RemovedTypes;
            if (removed != null && removed.Count > capacity)
            {
                CoopPlugin.Log.LogWarning("[workbench] rejected op removing " + removed.Count
                    + " types from a " + capacity + "-slot bench " + bench.name + ".");
                return ActionOutcome.Reject("the workbench removal exceeds its capacity", rejected);
            }

            for (var i = 0; removed != null && i < removed.Count; i++)
            {
                if (!IsRealItemType(removed[i]))
                {
                    CoopPlugin.Log.LogWarning("[workbench] rejected op with an invalid removed type "
                        + removed[i] + " on bench " + bench.name + ".");
                    return ActionOutcome.Reject("the workbench removal names an invalid item type",
                        rejected);
                }

                var at = types.IndexOf(removed[i]);
                if (at >= 0)
                {
                    types.RemoveAt(at);
                }
            }

            var added = message.AddedTypes;
            if (added != null && added.Count > capacity)
            {
                CoopPlugin.Log.LogWarning("[workbench] rejected op adding " + added.Count
                    + " types to a " + capacity + "-slot bench " + bench.name + ".");
                return ActionOutcome.Reject("the workbench addition exceeds its capacity", rejected);
            }

            if (types.Count + (added?.Count ?? 0) > capacity)
            {
                CoopPlugin.Log.LogWarning("[workbench] rejected op that would overfill bench "
                    + bench.name + " (" + types.Count + "+" + (added?.Count ?? 0) + " > "
                    + capacity + ").");
                return ActionOutcome.Reject("the workbench would be overfilled", rejected);
            }

            for (var i = 0; added != null && i < added.Count; i++)
            {
                if (!IsRealItemType(added[i]))
                {
                    CoopPlugin.Log.LogWarning("[workbench] rejected op with an invalid added type "
                        + added[i] + " on bench " + bench.name + ".");
                    return ActionOutcome.Reject("the workbench addition names an invalid item type",
                        rejected);
                }

                // Vanilla AddItem(addToFront: true) fills the last slot, so the newest item is
                // the front of the list; mirror that order for the host's authoritative list.
                types.Insert(0, added[i]);
            }

            SyncBench(bench, types, message.SpawnItemType, message.Bundling);
            return ActionOutcome.Applied(BuildResponse(bench));
        }

        /// <summary>True when <paramref name="type"/> is a real game item type backed by mesh
        /// data. Enum.IsDefined reflects the runtime enum, so types a content mod has minted are
        /// accepted alongside the vanilla ones; only <see cref="EItemType.None"/> and values no
        /// data row backs are refused.</summary>
        private static bool IsRealItemType(EItemType type)
            => type != EItemType.None && Enum.IsDefined(typeof(EItemType), type);

        /// <summary>Host: accept the requesting client's finished bundle. The host is authoritative
        /// for the bench state: it clears the mirrored bundling animation and answers with the
        /// resulting state, which the caller publishes to the other peers. The bundle box itself
        /// was already minted locally by the acting client, so no box is handed out here and
        /// nothing needs to be remembered for a rejoin.</summary>
        internal ActionOutcome HostCompleteBundle(WorkbenchBundleMessage message)
        {
            if (!_host || message == null)
            {
                return ActionOutcome.Reject("the workbench bundle action is missing");
            }

            var bench = message.Bench;
            if (bench == null)
            {
                CoopPlugin.Log.LogWarning(
                    "[workbench] dropped bundle completion with no live bench object.");
                return ActionOutcome.Reject("the workbench did not resolve to a live object");
            }

            if (GetSpawnType(bench) == EItemType.None)
            {
                return ActionOutcome.Reject("the workbench is not bundling", BuildResponse(bench));
            }

            // The host's bench was showing the bundle started by the requesting client; stop it
            // on the game object itself before answering, so the response reads no bundling.
            HideBundlingAnimation(bench);
            return ActionOutcome.Applied(BuildResponse(bench));
        }

        // ---------------- client ----------------

        internal void ClientStorageChanged(InteractableWorkbench bench, List<EItemType> before)
        {
            if (ApplyingRemote)
            {
                return;
            }

            ClientPublish(bench, before, false);
        }

        internal void ClientBundleStarted(InteractableWorkbench bench)
        {
            if (bench == null)
            {
                return;
            }

            // The game already turned the bench's jank box on; forward one forced edit so the host
            // mirrors the (tier, bundling) state. No stored item changed, so the delta is empty.
            ClientPublish(bench, TypesOf(bench), true);
        }

        /// <summary>Client: vanilla already minted the bundle box into the acting hand; hide the
        /// bench's mirror animation and tell the host the bundle finished so the shared bench state
        /// clears. The acting client keeps that local box, which is the bundle's only source.</summary>
        internal void ClientCompleteBundle(InteractableWorkbench bench)
        {
            if (_host || bench == null)
            {
                return;
            }

            var action = new WorkbenchBundleMessage { Bench = bench };
            HideBundlingAnimation(bench);
            SendClientAction(action, bench, TypesOf(bench));
        }

        /// <summary>Client: forward one predicted workbench edit. <paramref name="before"/> is the
        /// bench's stored list captured before the local game mutation, so the delta is computed
        /// against the live <c>m_StoredItemList</c> instead of a shadow cache. <paramref name="force"/>
        /// sends even with an empty stored delta, which a bundle start needs because it changes only
        /// the (tier, bundling) visual.</summary>
        private void ClientPublish(InteractableWorkbench bench, List<EItemType> before, bool force)
        {
            if (_host || bench == null || _send == null)
            {
                return;
            }

            var after = TypesOf(bench);
            var added = new List<EItemType>();
            var removed = new List<EItemType>();
            DiffTypes(before, after, added, removed);
            if (!force && added.Count == 0 && removed.Count == 0)
            {
                // A no-op edit (for example an AddItem/RemoveItem while the deck UI freezes the
                // list): the stored list did not change, so there is nothing to forward.
                return;
            }

            SendClientAction(new WorkbenchOpMessage
            {
                Bench = bench,
                AddedTypes = added,
                RemovedTypes = removed,
                SpawnItemType = GetSpawnType(bench),
                Bundling = IsBundling(bench),
            }, bench, before);
        }

        /// <summary>Rebuilds a bench's local list from a type sequence. Used to replay or undo a
        /// workbench prediction; the vanilla mutation already ran, so this only mirrors types.</summary>
        private void ReplayItems(InteractableWorkbench bench, List<EItemType> types)
        {
            ApplyingRemote = true;
            try
            {
                SyncBenchItems(bench, types);
            }
            finally
            {
                ApplyingRemote = false;
            }
        }

        internal void ClientApplyState(WorkbenchStateMessage message)
        {
            if (message == null)
            {
                return;
            }

            var bench = message.Bench;
            if (bench == null)
            {
                CoopPlugin.Log.LogWarning(
                    "[workbench] dropped state with no live bench object.");
                return;
            }

            SyncBench(bench, message.StoredTypes, message.SpawnItemType, message.Bundling);
        }

        /// <summary>Client: reverses one workbench action's local prediction. Called by the
        /// action's own <see cref="ActionMessage.OnUndo"/>, which the framework runs only when the
        /// host rejects it.</summary>
        internal static void UndoClientAction(ActionMessage action)
            => _activeClient?.UndoPending(action);

        /// <summary>Client: settles one workbench action, applying the host's absolute post-action
        /// state. Called exactly once per resolution, after the undo when rejected.</summary>
        internal static void MergeClientAction(ActionMessage action, ActionResponseMessage response)
            => _activeClient?.MergePending(action, response);

        /// <summary>Client: sends one predicted workbench action and keeps the snapshot its undo
        /// needs until the action merges. A send failure releases the snapshot before the
        /// transport failure propagates.</summary>
        private void SendClientAction(ActionMessage action, InteractableWorkbench bench,
            List<EItemType> previousTypes)
        {
            if (action == null)
            {
                return;
            }

            if (_send == null)
            {
                CoopPlugin.Log.LogError("WorldWorkbenchInteraction: cannot send "
                    + action.GetType().Name + "; the workbench send seam is not wired.");
                return;
            }

            _pendingClientActions[action] = new PendingClientAction
            {
                Bench = bench,
                PreviousTypes = previousTypes,
            };
            try
            {
                _send(null, action);
            }
            catch
            {
                // The action never reached the host, so its merge will not run. Release the local
                // snapshot before the transport failure propagates.
                _pendingClientActions.Remove(action);
                throw;
            }
        }

        private void UndoPending(ActionMessage action)
        {
            if (action == null)
            {
                return;
            }

            if (!_pendingClientActions.TryGetValue(action, out var pending))
            {
                CoopPlugin.Log.LogWarning("WorldWorkbenchInteraction: rejected "
                    + action.GetType().Name + " has no local prediction to reverse.");
                return;
            }

            if (pending.Bench != null && pending.PreviousTypes != null)
            {
                ReplayItems(pending.Bench, pending.PreviousTypes);
            }
        }

        private void MergePending(ActionMessage action, ActionResponseMessage response)
        {
            if (action == null)
            {
                return;
            }

            _pendingClientActions.Remove(action);
            if (response is not WorkbenchStateResponse state || state.Bench == null)
            {
                CoopPlugin.Log.LogWarning("WorldWorkbenchInteraction: " + action.GetType().Name
                    + " settled without authoritative workbench state.");
                return;
            }

            SyncBench(state.Bench, state.StoredTypes, state.SpawnItemType, state.Bundling);
        }

        private sealed class PendingClientAction
        {
            internal InteractableWorkbench Bench;
            internal List<EItemType> PreviousTypes;
        }

        // ---------------- game-state apply ----------------

        /// <summary>Reconcile a bench's physical list and bundling visual to the authoritative
        /// state. The list is rebuilt through the game's own add/remove methods while
        /// <see cref="ApplyingRemote"/> suppresses the forwarding patches, so a mirror is never
        /// mistaken for a player action.</summary>
        private void SyncBench(InteractableWorkbench bench, List<EItemType> types,
            EItemType spawnType, bool bundling)
        {
            if (bench == null)
            {
                return;
            }

            ApplyingRemote = true;
            try
            {
                SyncBenchItems(bench, types);

                if (spawnType != EItemType.None)
                {
                    FiSpawnType.SetValue(bench, spawnType);
                }

                if (bundling)
                {
                    ShowBundlingAnimation(bench, spawnType);
                }
                else
                {
                    HideBundlingAnimation(bench);
                }
            }
            finally
            {
                ApplyingRemote = false;
            }
        }

        /// <summary>Reconcile a bench's physical list to the authoritative type sequence through the
        /// game's own <c>RemoveItem</c>/<c>AddItem</c>, touching ONLY the items that actually differ.
        /// The old implementation rebuilt the whole list (disable every item, respawn every item);
        /// <c>Item.LerpToTransform</c> then hopped each respawned item for about a second, which is
        /// the visible jump the remaining stack made whenever one item was taken off the bench. An
        /// incremental sync leaves every already-correct item exactly where it is, so only the taken
        /// item leaves and only a genuinely new item is added.
        ///
        /// Vanilla <c>AddItem(addToFront:true)</c> inserts at list index 0 (newest first) and
        /// positions the item at the next slot, so additions are applied in reverse target order to
        /// reproduce the target list exactly.</summary>
        private static void SyncBenchItems(InteractableWorkbench bench, List<EItemType> types)
        {
            var stored = bench.m_StoredItemList;
            if (stored == null || types == null)
            {
                return;
            }

            var same = stored.Count == types.Count;
            for (var j = 0; same && j < types.Count; j++)
            {
                same = stored[j] != null && stored[j].GetItemType() == types[j];
            }

            if (same)
            {
                return;
            }

            // AddItem/RemoveItem are no-ops while the local player is editing a deck on this bench
            // (the game freezes the list for the deck UI). Clear that flag only for this synchronous
            // sync and restore it, so a remote edit cannot be silently dropped.
            var wasEditingDeck = FiIsEditingDeck != null && FiIsEditingDeck.GetValue(bench) is true;
            if (wasEditingDeck)
            {
                bench.SetIsEditingDeck(false);
            }

            try
            {
                // Remove only the items the target no longer has, matched by type so an identical
                // item is never needlessly respawned. Every kept item keeps its current slot.
                // Matching from the list's end keeps the earlier occurrences: the game's take
                // removes the first list item (the top slot), so the duplicate that left is the
                // one at the front and the survivors stay exactly where they were.
                var wanted = Tally(types);
                var removals = new List<Item>();
                for (var j = stored.Count - 1; j >= 0; j--)
                {
                    var item = stored[j];
                    var type = item != null ? item.GetItemType() : EItemType.None;
                    if (wanted.TryGetValue(type, out var have) && have > 0)
                    {
                        wanted[type] = have - 1;
                    }
                    else
                    {
                        removals.Add(item);
                    }
                }

                for (var j = 0; j < removals.Count; j++)
                {
                    var item = removals[j];
                    if (item == null)
                    {
                        continue;
                    }

                    bench.RemoveItem(item);
                    item.DisableItem();
                }

                // Add only the types the bench no longer has. Walking the target from its end means
                // each prepending AddItem lands in the right list position for the target order.
                var present = TallyItems(stored);
                for (var j = types.Count - 1; j >= 0; j--)
                {
                    if (present.TryGetValue(types[j], out var have) && have > 0)
                    {
                        present[types[j]] = have - 1;
                        continue;
                    }

                    AddBenchItem(bench, types[j]);
                }
            }
            finally
            {
                if (wasEditingDeck)
                {
                    bench.SetIsEditingDeck(true);
                }
            }
        }

        /// <summary>Adds one item of <paramref name="type"/> to the bench through the game's own
        /// <c>AddItem</c>, spawning it in the slot that method will position it into.</summary>
        private static void AddBenchItem(InteractableWorkbench bench, EItemType type)
        {
            var slotIndex = bench.GetItemCount();
            var slot = bench.m_PosList != null && slotIndex >= 0 && slotIndex < bench.m_PosList.Count
                ? bench.m_PosList[slotIndex]
                : bench.transform;
            var item = SpawnItem(type, slot);
            if (item == null)
            {
                return;
            }

            bench.AddItem(item, addToFront: true);
        }

        /// <summary>The bench's current stored types in list order (null slots read as None).</summary>
        internal static List<EItemType> TypesOf(InteractableWorkbench bench)
        {
            var result = new List<EItemType>();
            var stored = bench?.m_StoredItemList;
            if (stored == null)
            {
                return result;
            }

            for (var i = 0; i < stored.Count; i++)
            {
                result.Add(stored[i] != null ? stored[i].GetItemType() : EItemType.None);
            }

            return result;
        }

        /// <summary>Multiset difference: <paramref name="added"/> = after - before and
        /// <paramref name="removed"/> = before - after.</summary>
        private static void DiffTypes(List<EItemType> before, List<EItemType> after,
            List<EItemType> added, List<EItemType> removed)
        {
            var counts = Tally(before);
            for (var i = 0; after != null && i < after.Count; i++)
            {
                if (counts.TryGetValue(after[i], out var have) && have > 0)
                {
                    counts[after[i]] = have - 1;
                }
                else
                {
                    added.Add(after[i]);
                }
            }

            counts = Tally(after);
            for (var i = 0; before != null && i < before.Count; i++)
            {
                if (counts.TryGetValue(before[i], out var have) && have > 0)
                {
                    counts[before[i]] = have - 1;
                }
                else
                {
                    removed.Add(before[i]);
                }
            }
        }

        private static Dictionary<EItemType, int> Tally(List<EItemType> types)
        {
            var counts = new Dictionary<EItemType, int>();
            for (var i = 0; types != null && i < types.Count; i++)
            {
                counts.TryGetValue(types[i], out var have);
                counts[types[i]] = have + 1;
            }

            return counts;
        }

        private static Dictionary<EItemType, int> TallyItems(List<Item> items)
        {
            var counts = new Dictionary<EItemType, int>();
            for (var i = 0; items != null && i < items.Count; i++)
            {
                var type = items[i] != null ? items[i].GetItemType() : EItemType.None;
                counts.TryGetValue(type, out var have);
                counts[type] = have + 1;
            }

            return counts;
        }

        private static void ShowBundlingAnimation(InteractableWorkbench bench, EItemType spawnType)
        {
            if (bench == null || spawnType == EItemType.None)
            {
                return;
            }

            Runtime.ItemVisuals.ApplyJankBoxSkin(bench.m_JankBoxSkinMesh, spawnType);

            if (bench.m_JankBoxAnim != null)
            {
                bench.m_JankBoxAnim.gameObject.SetActive(true);
                bench.m_JankBoxAnim.SetBool("IsClosing", true);
            }
        }

        private static void HideBundlingAnimation(InteractableWorkbench bench)
        {
            if (bench == null)
            {
                return;
            }

            if (bench.m_JankBoxAnim != null)
            {
                bench.m_JankBoxAnim.gameObject.SetActive(false);
                bench.m_JankBoxAnim.SetBool("IsClosing", false);
            }

            var anims = bench.m_CardEnterBoxAnimList;
            var cards = bench.m_InteractableCard3dList;
            var count = Mathf.Min(anims?.Count ?? 0, cards?.Count ?? 0);
            for (var i = 0; i < count; i++)
            {
                var card = cards[i];
                if (card?.m_Card3dUI != null)
                {
                    card.m_Card3dUI.SetVisibility(false);
                }

                anims[i]?.gameObject.SetActive(false);
            }
        }

        /// <summary>The game's own save-load recipe for materializing an Item by type.</summary>
        internal static Item SpawnItem(EItemType itemType, Transform parent)
            => Runtime.ItemVisuals.Spawn(itemType, parent);
    }

    /// <summary>Host -> the acting client: the workbench's absolute post-action state. The list,
    /// spawn tier and bundling flag are authoritative on accept and reject alike, so the client's
    /// merge lands on reality instead of inverting a snapshot authority has moved past.</summary>
    [NetworkMessage]
    public sealed class WorkbenchStateResponse : ActionResponseMessage
    {
        public InteractableWorkbench Bench;
        public List<EItemType> StoredTypes = new();
        public EItemType SpawnItemType;
        public bool Bundling;
    }
}
