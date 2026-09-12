using System.Collections.Generic;

namespace CardShopCoop.Sync
{
    /// <summary>Client-only reservation area for optimistic item mutations. Reserved items stay
    /// live in the hand until the host says whether the mutation really happened. This is
    /// deliberately token-keyed: box and world acknowledgements have independent sequences.</summary>
    internal static class HandEscrow
    {
        private static readonly Dictionary<int, List<Item>> ReservedTakes
            = new Dictionary<int, List<Item>>();
        private static readonly HashSet<Item> Reserved = new HashSet<Item>();
        private static readonly List<Item> ToHand = new List<Item>();
        private static int _nextToken;
        private static bool _resetting;

        public static int ReserveTake(int localType, int count)
        {
            if (CoopCore.Role != CoopRole.Client)
                return 0;
            if (count <= 0)
                return 0;
            var items = new List<Item>(count);
            var held = CoopCore.GetHeldItemList(CoopCore.PlayerIpc);
            if (held != null)
            {
                for (int i = held.Count - 1; i >= 0 && items.Count < count; i--)
                    if (held[i] != null && !Reserved.Contains(held[i])
                        && (int)held[i].GetItemType() == localType)
                        items.Add(held[i]);
            }
            if (items.Count != count)
            {
                CoopPlugin.Log.LogWarning($"HandEscrow.ReserveTake: wanted {count} of type {localType}, found {items.Count}; reserved 0");
                return 0;
            }

            int token = NextToken();
            for (int i = 0; i < items.Count; i++)
                Reserved.Add(items[i]);
            ReservedTakes.Add(token, items);
            return token;
        }

        public static void ResolveTake(int token, int acceptedMagnitude)
        {
            if (token <= 0)
                return; // nothing was escrowed for this transfer; there is nothing to reconcile
            if (!ReservedTakes.TryGetValue(token, out var items))
            {
                CoopPlugin.Log.LogWarning($"HandEscrow.ResolveTake: unknown token {token}");
                return;
            }
            ReservedTakes.Remove(token);
            if (acceptedMagnitude < 0)
            {
                CoopPlugin.Log.LogError($"HandEscrow.ResolveTake: negative accepted count {acceptedMagnitude} for token {token}; treating as zero");
                acceptedMagnitude = 0;
            }
            if (acceptedMagnitude > items.Count)
            {
                CoopPlugin.Log.LogWarning($"HandEscrow.ResolveTake: host accepted {acceptedMagnitude}, but token {token} contains {items.Count}");
                acceptedMagnitude = items.Count;
            }
            for (int i = 0; i < items.Count; i++)
            {
                if (i < acceptedMagnitude)
                {
                    Reserved.Remove(items[i]);
                }
                else
                {
                    Reserved.Remove(items[i]);
                    if (CoopCore.RemoveHeldItemFromHand(items[i]))
                        CoopCore.DestroyDetachedItem(items[i]);
                    else
                        CoopPlugin.Log.LogError(
                            $"HandEscrow.ResolveTake: rejected item could not be removed from the hand for token {token}; left in hand rather than destroying a live object");
                }
            }
        }

        public static void ExpireTake(int token)
        {
            if (token <= 0)
                return; // nothing was escrowed for this transfer
            if (!ReservedTakes.TryGetValue(token, out var items))
                return;
            ReservedTakes.Remove(token);
            for (int i = 0; i < items.Count; i++)
                Reserved.Remove(items[i]);
        }

        public static bool IsReserved(Item item) => item != null && Reserved.Count != 0 && Reserved.Contains(item);

        public static bool HasReservedHeld(InteractionPlayerController ipc)
        {
            var items = CoopCore.GetHeldItemList(ipc);
            if (items == null)
                return false;
            for (int i = 0; i < items.Count; i++)
                if (IsReserved(items[i]))
                    return true;
            return false;
        }

        public static bool IsFrontReserved(InteractionPlayerController ipc)
        {
            var items = CoopCore.GetHeldItemList(ipc);
            return ipc != null && items != null
                && items.Count > 0 && IsReserved(items[0]);
        }

        public static int EscrowAdded(ShelfCompartment comp, int localType, int count)
        {
            if (comp == null || count <= 0 || (int)comp.GetItemType() != localType)
                return 0;
            int queued = 0;
            while (queued < count && comp.GetItemCount() > 0)
            {
                // A network result handler is a systemic boundary: a game-side take fault here
                // must not abort the handler with the remaining items left in the compartment.
                // Stop, leave the rest, and let the caller rebase its baseline to the truth.
                Item item;
                try
                {
                    item = comp.TakeItemToHand();
                }
                catch (System.Exception e)
                {
                    CoopPlugin.Log.LogWarning($"HandEscrow.EscrowAdded: TakeItemToHand failed after {queued} of {count}: {e.Message}");
                    break;
                }
                if (item == null)
                {
                    CoopPlugin.Log.LogError($"HandEscrow.EscrowAdded: compartment returned null after removing item {queued + 1}");
                    break;
                }
                QueueToHand(item);
                queued++;
            }
            if (queued < count)
                CoopPlugin.Log.LogWarning($"HandEscrow.EscrowAdded: wanted {count} of type {localType}, queued {queued}");
            return queued;
        }

        public static void Tick()
        {
            if (ToHand.Count == 0 || !CoopCore.HandAcceptsItems())
                return;
            while (ToHand.Count > 0 && CoopCore.HandAcceptsItems())
            {
                var item = ToHand[0];
                if (item == null || item.gameObject == null)
                {
                    CoopPlugin.Log.LogWarning("HandEscrow.Tick: discarded a destroyed queued item");
                    ToHand.RemoveAt(0);
                    continue;
                }
                // TryAttachHeldItem activates the object only once it can actually enter the
                // hand, so a failed attach leaves it inactive instead of visibly floating.
                if (!CoopCore.TryAttachHeldItem(item))
                    break;
                ToHand.RemoveAt(0);
            }
        }

        public static void Reset()
        {
            if (_resetting)
                return;
            _resetting = true;
            try
            {
                var reservedItems = new List<Item>(Reserved);
                for (int i = 0; i < reservedItems.Count; i++)
                    Reserved.Remove(reservedItems[i]);
                ReservedTakes.Clear();
                Reserved.Clear();
                Tick();
                int remainder = 0;
                for (int i = 0; i < ToHand.Count; i++)
                {
                    CoopCore.DestroyDetachedItem(ToHand[i]);
                    remainder++;
                }
                ToHand.Clear();
                if (remainder > 0)
                    CoopPlugin.Log.LogError($"HandEscrow.Reset: destroyed {remainder} queued items that could not return to hand");
            }
            finally
            {
                _resetting = false;
            }
        }

        private static void QueueToHand(Item item)
        {
            if (item == null)
                return;
            item.gameObject.SetActive(false);
            ToHand.Add(item);
        }

        private static int NextToken()
        {
            if (++_nextToken <= 0)
                _nextToken = 1;
            while (ReservedTakes.ContainsKey(_nextToken))
            {
                if (++_nextToken <= 0)
                    _nextToken = 1;
            }
            return _nextToken;
        }
    }
}
