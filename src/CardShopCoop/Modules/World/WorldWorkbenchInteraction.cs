using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
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
    /// ride the generic card path as one atomic batch and the produced box is minted by the host
    /// and granted to the acting player.
    /// </summary>
    internal sealed class WorldWorkbenchInteraction
    {
        /// <summary>Placement kind of the workbench in <see cref="PlacementApi.GetList"/>.</summary>
        internal const int KindWorkbench = 7;

        // Private game state this feature must read or write (no public accessors exist).
        private static readonly FieldInfo FiItemAmount =
            ReflectionSurface.RequiredField(typeof(InteractableWorkbench), "m_ItemAmount");
        private static readonly FieldInfo FiSpawnType =
            ReflectionSurface.RequiredField(typeof(InteractableWorkbench), "m_CurrentItemTypeSpawn");

        /// <summary>True while the sync itself mutates a bench, so forwarding patches never
        /// mistake an applied mirror for a local player action.</summary>
        internal static bool ApplyingRemote;

        private sealed class PendingGrant
        {
            internal EItemType ItemType;
            internal long GrantId;
        }

        private readonly bool _host;
        private readonly Action<INetMessage> _broadcast;
        private readonly Action<int, INetMessage> _send;
        private readonly Func<bool> _inGame;
        private readonly HashSet<object> _bundling = new();
        private readonly Dictionary<int, PendingGrant> _grants = new();
        private long _nextGrantId = 1;

        internal WorldWorkbenchInteraction(bool host, Action<INetMessage> broadcast,
            Action<int, INetMessage> send, Func<bool> inGame = null)
        {
            _host = host;
            _broadcast = broadcast;
            _send = send;
            _inGame = inGame;
        }

        internal void Reset()
        {
            _bundling.Clear();
            _grants.Clear();
            _nextGrantId = 1;
            ApplyingRemote = false;
        }

        private bool InGame() => _inGame == null || _inGame();

        // ---------------- shared lookups ----------------

        private static ShelfManager Sm() => SceneRef<ShelfManager>.Get();

        private static IList Benches()
        {
            var sm = Sm();
            return sm == null ? null : PlacementApi.GetList(sm, KindWorkbench);
        }

        internal static InteractableWorkbench GetBench(int index)
        {
            var list = Benches();
            return list != null && index >= 0 && index < list.Count
                ? list[index] as InteractableWorkbench
                : null;
        }

        private static int IndexOf(InteractableWorkbench bench)
        {
            var list = Benches();
            var idx = list?.IndexOf(bench) ?? -1;
            return idx is >= 0 and < 250 ? idx : -1;
        }

        private WorkbenchStateMessage BuildState(int index, InteractableWorkbench bench)
        {
            var message = new WorkbenchStateMessage
            {
                Index = (byte)index,
                StableEntityId = WorldMessageMetadata.WorkbenchEntityId(index),
                Bundling = bench != null && _bundling.Contains(bench),
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

        private static EItemType GetSpawnType(InteractableWorkbench bench)
            => bench != null && FiSpawnType?.GetValue(bench) is EItemType type
                ? type
                : EItemType.None;

        // ---------------- host ----------------

        internal void AppendBaselineMessages(Action<INetMessage> append)
        {
            if (!_host || append == null || !InGame())
            {
                return;
            }

            var list = Benches();
            for (var i = 0; list != null && i < list.Count && i < 250; i++)
            {
                if (list[i] is InteractableWorkbench bench)
                {
                    append(BuildState(i, bench));
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

        internal void HostBundleStarted(InteractableWorkbench bench)
        {
            if (bench == null)
            {
                return;
            }

            _bundling.Add(bench);
            HostPublish(bench);
        }

        /// <summary>The host finished its own bundle. Vanilla already spawned the box into the
        /// host's hand; only the bench state (bundling cleared) needs publishing.</summary>
        internal void HostBundleCompleted(InteractableWorkbench bench)
        {
            if (bench == null)
            {
                return;
            }

            _bundling.Remove(bench);
            HostPublish(bench);
        }

        private void HostPublish(InteractableWorkbench bench)
        {
            if (!_host || bench == null || _broadcast == null || !InGame())
            {
                return;
            }

            var idx = IndexOf(bench);
            if (idx < 0)
            {
                return;
            }

            _broadcast(BuildState(idx, bench));
        }

        /// <summary>Host: apply a client's authoritative-for-them bench state and republish it.
        /// The sender's own state already matches, so the echo is an idempotent state-set.</summary>
        internal void HostApplyStateRequest(int connId, WorkbenchStateRequestMessage message)
        {
            if (!_host || message == null)
            {
                return;
            }

            var bench = GetBench(message.Index);
            if (bench == null)
            {
                return;
            }

            SyncBench(bench, message.StoredTypes, message.SpawnItemType, message.Bundling);
            _broadcast?.Invoke(BuildState(message.Index, bench));
        }

        /// <summary>Host: mint the bundled box for the requesting client and grant it to them.
        /// The box tier is read from the host bench, so it cannot be spoofed by the client.</summary>
        internal bool HostCompleteBundle(int connId, int index)
        {
            if (!_host)
            {
                return false;
            }

            var bench = GetBench(index);
            if (bench == null)
            {
                return false;
            }

            var itemType = GetSpawnType(bench);
            if (itemType == EItemType.None)
            {
                return false;
            }

            _bundling.Remove(bench);
            _broadcast?.Invoke(BuildState(index, bench));

            var grant = new WorkbenchGrantMessage
            {
                ItemType = itemType,
                GrantId = _nextGrantId++,
            };
            _grants[connId] = new PendingGrant { ItemType = itemType, GrantId = grant.GrantId };
            _send?.Invoke(connId, grant);
            return true;
        }

        /// <summary>Host: re-send an outstanding bundle grant to a (re)joining connection.</summary>
        internal void ResendGrants(int connectionId)
        {
            if (!_host || connectionId <= 0 || !_grants.TryGetValue(connectionId, out var grant))
            {
                return;
            }

            _send?.Invoke(connectionId, new WorkbenchGrantMessage
            {
                ItemType = grant.ItemType,
                GrantId = grant.GrantId,
            });
        }

        // ---------------- client ----------------

        internal void ClientStorageChanged(InteractableWorkbench bench)
        {
            if (ApplyingRemote)
            {
                return;
            }

            ClientPublish(bench);
        }

        internal void ClientBundleStarted(InteractableWorkbench bench)
        {
            if (bench == null)
            {
                return;
            }

            _bundling.Add(bench);
            ClientPublish(bench);
        }

        /// <summary>Client: the vanilla completion is suppressed; hide the animation and ask the
        /// host to mint and grant the box.</summary>
        internal void ClientCompleteBundle(InteractableWorkbench bench)
        {
            if (_host || bench == null)
            {
                return;
            }

            _bundling.Remove(bench);
            HideBundlingAnimation(bench);
            var idx = IndexOf(bench);
            if (idx < 0)
            {
                return;
            }

            _send?.Invoke(1, new WorkbenchBundleMessage
            {
                StableEntityId = WorldMessageMetadata.WorkbenchEntityId(idx),
                Index = (byte)idx,
            });
        }

        private void ClientPublish(InteractableWorkbench bench)
        {
            if (_host || bench == null || _send == null)
            {
                return;
            }

            var idx = IndexOf(bench);
            if (idx < 0)
            {
                return;
            }

            var state = BuildState(idx, bench);
            _send(1, new WorkbenchStateRequestMessage
            {
                StableEntityId = state.StableEntityId,
                Index = state.Index,
                StoredTypes = state.StoredTypes,
                SpawnItemType = state.SpawnItemType,
                Bundling = state.Bundling,
            });
        }

        internal void ClientApplyState(WorkbenchStateMessage message)
        {
            if (message == null)
            {
                return;
            }

            var bench = GetBench(message.Index);
            if (bench == null)
            {
                return;
            }

            SyncBench(bench, message.StoredTypes, message.SpawnItemType, message.Bundling);
        }

        internal void ClientApplyGrant(WorkbenchGrantMessage message)
        {
            if (_host || message == null || message.ItemType == EItemType.None)
            {
                return;
            }

            SpawnInLocalHand(message.ItemType);
        }

        // ---------------- game-state apply ----------------

        /// <summary>Reconcile a bench's physical list and bundling visual to the authoritative
        /// state. Direct list writes (never the game add/remove methods) keep the forwarding
        /// patches from treating a mirror as a player action.</summary>
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
                    _bundling.Add(bench);
                    ShowBundlingAnimation(bench, spawnType);
                }
                else
                {
                    _bundling.Remove(bench);
                    HideBundlingAnimation(bench);
                }
            }
            finally
            {
                ApplyingRemote = false;
            }
        }

        /// <summary>Rebuild a bench's physical list from the authoritative type sequence.
        /// Vanilla <c>AddItem(addToFront:true)</c> keeps <c>list[j]</c> in slot
        /// <c>count-1-j</c> (the newest box fills the last slot), so the mirror must place each
        /// item the same way. Laying them out in list order instead reversed the bench, and the
        /// host then took the first slot rather than the last.</summary>
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

            for (var j = 0; j < stored.Count; j++)
            {
                if (stored[j] != null)
                {
                    ItemSpawnManager.DisableItem(stored[j]);
                }
            }

            stored.Clear();
            var count = types.Count;
            for (var j = 0; j < count; j++)
            {
                var slot = bench.m_PosList != null && count - 1 - j < bench.m_PosList.Count
                    ? bench.m_PosList[count - 1 - j]
                    : bench.transform;
                stored.Add(SpawnItem(types[j], slot));
            }

            FiItemAmount.SetValue(bench, stored.Count);
        }

        private static void ShowBundlingAnimation(InteractableWorkbench bench, EItemType spawnType)
        {
            if (bench == null || spawnType == EItemType.None)
            {
                return;
            }

            var meshData = InventoryBase.GetItemMeshData(spawnType);
            if (bench.m_JankBoxSkinMesh != null)
            {
                bench.m_JankBoxSkinMesh.material = meshData.material;
            }

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

                if (anims[i] != null)
                {
                    anims[i].gameObject.SetActive(false);
                }
            }
        }

        private static void SpawnInLocalHand(EItemType itemType)
        {
            var controller = SceneRef<InteractionPlayerController>.Get();
            if (controller == null)
            {
                return;
            }

            var parent = controller.m_HoldItemPos != null
                ? controller.m_HoldItemPos
                : controller.transform;
            var item = SpawnItem(itemType, parent);
            controller.AddHoldItemToFront(item);
        }

        /// <summary>The game's own save-load recipe for materializing an Item by type.</summary>
        internal static Item SpawnItem(EItemType itemType, Transform parent)
        {
            var meshData = InventoryBase.GetItemMeshData(itemType);
            var item = ItemSpawnManager.GetItem(parent);
            item.SetMesh(meshData.mesh, meshData.material, itemType,
                meshData.meshSecondary, meshData.materialSecondary);
            item.transform.localPosition = Vector3.zero;
            item.transform.localRotation = Quaternion.identity;
            item.gameObject.SetActive(true);
            return item;
        }
    }
}
