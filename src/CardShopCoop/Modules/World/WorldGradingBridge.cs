using System;
using System.Collections.Generic;
using CardShopCoop.Modules.Grading;
using CardShopCoop.Runtime;

namespace CardShopCoop.Modules.World
{
    /// <summary>
    /// Adapts the ordinary World inventory and live shelf services to Grading. The grading module
    /// owns policy and wire state; this class owns only the World validation and mutations.
    /// </summary>
    internal sealed class WorldGradingBridge : IGradingWorldBridge
    {
        private const float DeliveryFee = 10f;
        private readonly WorldCardInteraction _world;

        internal WorldGradingBridge(WorldCardInteraction world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
        }

        public bool SceneReady
        {
            get
            {
                if (!_world.InGameLevel())
                    return false;

                var inventory = WorldCardInteraction.Inv();
                var shelves = SceneRef<ShelfManager>.Get();
                return inventory != null && shelves != null && shelves.m_FinishLoadingObjectData;
            }
        }

        public bool CardSetInstalled(CardData card)
            => WorldCardInteraction.CardSetInstalledHere(card);

        public bool TryGetVanillaServiceCost(int serviceLevel, int cardCount, out float total)
        {
            total = 0f;
            if (cardCount < 0)
                return false;

            var service = GetService(serviceLevel);
            if (service == null)
                return false;

            total = DeliveryFee + service.m_CostPerCard * cardCount;
            return !float.IsNaN(total) && !float.IsInfinity(total) && total >= 0f;
        }

        public bool TryReserveCards(int connectionId, IList<CardData> cards,
            out IGradingCardReservation reservation, out string reason)
            => _world.TryReserveGradingCards(connectionId, cards, out reservation, out reason);

        public bool TryAddCard(CardData card, int amount)
        {
            if (card == null || amount <= 0)
                return false;

            var before = CountGraded(card);
            using (_world.BeginRemoteCardApply())
            {
                CPlayerData.AddCard(WorldCardInteraction.SnapshotCard(card), amount);
                var after = CountGraded(card);
                if (after == before + amount)
                    return true;

                for (var i = 0; i < Math.Max(0, after - before); i++)
                {
                    if (CPlayerData.HasGradedCardInAlbum(card))
                        CPlayerData.RemoveGradedCard(card, ignoreGradedCardIndex: true);
                }
            }
            return false;
        }

        public void NotifyCardsChanged()
            => _world.NotifyCardsChanged();

        public void WarnRefused(CardData card, string context)
            => WorldCardInteraction.WarnRefusedCard(card, context);

        private GradeCardServiceData GetService(int serviceLevel)
        {
            var inventory = WorldCardInteraction.Inv();
            var data = inventory?.m_MonsterData_SO;
            if (data == null || serviceLevel < 0)
                return null;

            // GetGradeCardServiceData is patched by Grading Overhaul for its additional tiers.
            // Calling the game's accessor, rather than indexing the vanilla list, keeps the
            // host-side fee calculation valid on both the vanilla and GO installs.
            try
            {
                return data.GetGradeCardServiceData(serviceLevel);
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("World grading bridge: service level " + serviceLevel
                    + " could not be resolved on the host - " + error.Message);
                return null;
            }
        }

        private static int CountGraded(CardData card)
        {
            var rows = CPlayerData.m_GradedCardInventoryList;
            if (rows == null || card == null)
                return 0;
            var encoded = GradingApi.Encoded(card);
            var count = 0;
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var candidate = row == null ? null : CPlayerData.GetGradedCardData(row);
                if (candidate != null && candidate.expansionType == card.expansionType
                    && candidate.monsterType == card.monsterType
                    && candidate.borderType == card.borderType
                    && candidate.isFoil == card.isFoil
                    && candidate.isDestiny == card.isDestiny
                    && GradingApi.Encoded(candidate) == encoded)
                    count++;
            }
            return count;
        }

    }
}
