using System.Collections.Generic;

namespace CardShopCoop.Sync
{
    /// <summary>Exact bounded replay protection for authenticated play-table requests.</summary>
    public sealed class PlayTableRequestReplayGuard
    {
        public const int MaxOwners = 16;
        private readonly Dictionary<int, long> _lastSequences = new Dictionary<int, long>();

        public bool TryAccept(int ownerConn, long sequence)
        {
            if (sequence <= 0)
                return false;
            if (_lastSequences.TryGetValue(ownerConn, out long last))
            {
                if (sequence <= last)
                    return false;
                _lastSequences[ownerConn] = sequence;
                return true;
            }
            if (_lastSequences.Count >= MaxOwners)
                return false;
            _lastSequences.Add(ownerConn, sequence);
            return true;
        }

        public void RemoveOwner(int ownerConn) => _lastSequences.Remove(ownerConn);
        public void Reset() => _lastSequences.Clear();
    }
}
