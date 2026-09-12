using System.Collections.Generic;

namespace CardShopCoop.Sync
{
    /// <summary>Client-only holding area for optimistic item mutations. Items stay as live game
    /// objects, but inactive, until the host says whether the mutation really happened. This is
    /// deliberately token-keyed: box and world acknowledgements have independent sequences.</summary>
    internal static class HandEscrow
    {
        private static readonly Dictionary<int, List<Item>> PendingTakes
            = new Dictionary<int, List<Item>>();
        private static readonly List<Item> ToHand = new List<Item>();
        private static int _nextToken;
        private static bool _resetting;

        public static int EscrowTake(int localType, int count)
        {
            if (count <= 0)
                return 0;
            var escrowed = new List<Item>(count);
            // Escrow regardless of a modal hand state: the items being taken were placed in
            // m_HoldItemList by vanilla, and leaving them there is exactly what lets the player
            // place/consume them before the host answers. (Attaching on the way back IS gated,
            // because AddHoldItemToFront must not run inside a modal state.)
            // DetachHeldItemAt removes the LAST matching item (the most recently taken), so
            // repeated calls escrow exactly the objects a take just added, newest first.
            for (int i = 0; i < count; i++)
            {
                var item = CoopCore.DetachHeldItemAt(localType);
                if (item == null)
                    break;
                item.gameObject.SetActive(false);
                escrowed.Add(item);
            }
            if (escrowed.Count < count)
                CoopPlugin.Log.LogWarning($"HandEscrow.EscrowTake: wanted {count} of type {localType}, escrowed {escrowed.Count}");
            if (escrowed.Count == 0)
                return 0;

            int token = NextToken();
            PendingTakes.Add(token, escrowed);
            return token;
        }

        public static void ResolveTake(int token, int acceptedMagnitude)
        {
            if (token <= 0)
                return; // nothing was escrowed for this transfer; there is nothing to reconcile
            if (!PendingTakes.TryGetValue(token, out var items))
            {
                CoopPlugin.Log.LogWarning($"HandEscrow.ResolveTake: unknown token {token}");
                return;
            }
            PendingTakes.Remove(token);
            if (acceptedMagnitude < 0)
            {
                CoopPlugin.Log.LogError($"HandEscrow.ResolveTake: negative accepted count {acceptedMagnitude} for token {token}; treating as zero");
                acceptedMagnitude = 0;
            }
            if (acceptedMagnitude > items.Count)
            {
                CoopPlugin.Log.LogWarning($"HandEscrow.ResolveTake: host accepted {acceptedMagnitude}, but token {token} contains {items.Count}; restoring all");
                acceptedMagnitude = items.Count;
            }
            for (int i = 0; i < items.Count; i++)
            {
                if (i < acceptedMagnitude)
                    QueueToHand(items[i]);
                else
                    CoopCore.DestroyDetachedItem(items[i]);
            }
        }

        public static void ExpireTake(int token)
        {
            if (token <= 0)
                return; // nothing was escrowed for this transfer
            if (!PendingTakes.TryGetValue(token, out var items))
            {
                CoopPlugin.Log.LogWarning($"HandEscrow.ExpireTake: unknown token {token}");
                return;
            }
            PendingTakes.Remove(token);
            for (int i = 0; i < items.Count; i++)
                QueueToHand(items[i]);
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
                foreach (var pair in PendingTakes)
                    for (int i = 0; i < pair.Value.Count; i++)
                        QueueToHand(pair.Value[i]);
                PendingTakes.Clear();
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
            while (PendingTakes.ContainsKey(_nextToken))
            {
                if (++_nextToken <= 0)
                    _nextToken = 1;
            }
            return _nextToken;
        }
    }
}
