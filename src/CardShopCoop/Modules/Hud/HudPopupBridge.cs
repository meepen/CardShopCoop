using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Util;
using UnityEngine;

namespace CardShopCoop.Modules.Hud
{
    /// <summary>
    /// Feeds the game HUD's top-right money/experience change popups.
    ///
    /// In co-op the client replaces the game's own <c>CEventPlayer_AddCoin</c>,
    /// <c>CEventPlayer_ReduceCoin</c> and <c>CEventPlayer_AddShopExp</c> events with authoritative
    /// <c>Set*</c> events, so <see cref="GameUIScreen"/>'s handlers never run and its popup lists
    /// stay empty - the little "+$12" / "+30 xp" animations never appear on a guest. This bridge
    /// reproduces exactly the list/timer updates those handlers perform, without touching
    /// <see cref="CPlayerData"/> (whose value the authoritative flow has already set).
    /// </summary>
    internal static class HudPopupBridge
    {
        // Matches the opening popup delay GameUIScreen's own handlers use when the list was empty;
        // its Update loop then shows each queued entry in turn.
        private const float ImmediatePopupTimer = 10f;

        private static readonly FieldInfo AddMoneyList =
            ReflectionSurface.RequiredField(typeof(GameUIScreen), "m_AddMoneyPopupList");

        private static readonly FieldInfo AddMoneyTimer =
            ReflectionSurface.RequiredField(typeof(GameUIScreen), "m_AddMoneyPopupTimer");

        private static readonly FieldInfo AddExperienceList =
            ReflectionSurface.RequiredField(typeof(GameUIScreen), "m_AddShopExpPopupList");

        private static readonly FieldInfo AddExperienceTimer =
            ReflectionSurface.RequiredField(typeof(GameUIScreen), "m_AddShopExpPopupTimer");

        /// <summary>Queues one signed money change (positive gain, negative spend).</summary>
        internal static void ShowWallet(GameUIScreen screen, float delta)
        {
            if (screen == null || delta == 0f)
            {
                return;
            }

            if (AddMoneyList.GetValue(screen) is not List<float> popups)
            {
                return;
            }

            if (popups.Count == 0)
            {
                AddMoneyTimer.SetValue(screen, ImmediatePopupTimer);
            }

            popups.Add(delta);
        }

        /// <summary>Queues one experience gain exactly as the game's own handler would.</summary>
        internal static void ShowExperience(GameUIScreen screen, int experience)
        {
            if (screen == null || experience <= 0)
            {
                return;
            }

            if (AddExperienceList.GetValue(screen) is not List<int> popups)
            {
                return;
            }

            if (popups.Count == 0)
            {
                AddExperienceTimer.SetValue(screen, ImmediatePopupTimer);
            }

            popups.Add(experience);
        }
    }
}
