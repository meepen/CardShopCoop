using System;
using System.Collections.Generic;

namespace CardShopCoop.Modules.Grading
{
    /// <summary>
    /// The only World surface grading needs. World remains the owner of ordinary card
    /// inventory, card-delta suppression, and live shelf/box enumeration. The grading module
    /// owns the policy, certificate identity, and pending-job state around that surface.
    ///
    /// Scene readiness and card ownership are supplied by World; grading never infers ownership
    /// from a missing or partially loaded inventory.
    /// </summary>
    public interface IGradingWorldBridge
    {
        bool SceneReady
        {
            get;
        }
        bool CardSetInstalled(CardData card);
        bool TryGetVanillaServiceCost(int serviceLevel, int cardCount, out float total);

        /// <summary>Reserves the exact cards already validated by the host World owner. The
        /// reservation is all-or-nothing and must be committed after the grading transaction is
        /// complete; disposing an uncommitted reservation restores its ownership.</summary>
        bool TryReserveCards(int connectionId, IList<CardData> cards,
            out IGradingCardReservation reservation, out string reason);

        bool TryAddCard(CardData card, int amount);
        void NotifyCardsChanged();
        void WarnRefused(CardData card, string context);
    }

    public interface IGradingCardReservation : IDisposable
    {
        void Commit();
        /// <summary>Compensates both an active and an already committed reservation. This is
        /// required when a later local/network step throws after the ownership commit.</summary>
        void Rollback();
    }

    /// <summary>World's explicit attachment point for the grading feature.</summary>
    public static class GradingWorldBridge
    {
        private static IGradingWorldBridge _current;

        public static IGradingWorldBridge Current => _current;

        public static void Attach(IGradingWorldBridge bridge)
        {
            if (bridge == null)
                throw new ArgumentNullException(nameof(bridge));

            if (_current != null && !ReferenceEquals(_current, bridge))
                throw new InvalidOperationException("A different grading World bridge is already attached.");

            _current = bridge;
        }

        public static void Detach(IGradingWorldBridge bridge)
        {
            if (ReferenceEquals(_current, bridge))
                _current = null;
        }
    }
}
