using CardShopCoop.Api;

namespace CardShopCoop.Modules.PlayTable
{
    /// <summary>
    /// Host-authoritative move gate for one play table. A guest never simulates the customers, so
    /// its own <c>InteractablePlayTable.GetCurrentPlayerCount</c> cannot track the host's live game
    /// and may even hold a stale value restored from the host's save. This value carries the fields
    /// vanilla's <c>StartMoveObject</c> reads, so a guest can decide whether the first pickup must
    /// stop the game instead of guessing from a stale table mirror.
    /// </summary>
    [HostOnlyInformation]
    internal sealed class PlayTableGameState : ExtendGameObjectInformation<InteractablePlayTable>
    {
        /// <summary>The host's live <c>m_CurrentPlayerCount</c>.</summary>
        public int PlayerCount
        {
            get; init;
        }

        /// <summary>The host's live tournament flag, which vanilla uses to refuse moving an
        /// occupied table instead of stopping its game.</summary>
        public bool IsTournament
        {
            get; init;
        }

        /// <summary>True while a human player seat owns this table (the host's local duel or a
        /// guest's reserved match). Vanilla only clears the table state; a duel is unwound by
        /// <c>PlayTableGame.DelayExit</c>, which a remote kick never runs, so the host must keep
        /// owning the table until that exit.</summary>
        public bool PlayerOccupied
        {
            get; init;
        }
    }
}
