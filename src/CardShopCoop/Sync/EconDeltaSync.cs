using System.Collections.Generic;
using System.Reflection;

namespace CardShopCoop.Sync
{
    /// <summary>Replays the host's vanilla economy HUD queue on a client. The game remains
    /// responsible for timing, formatting, colors, and animation.</summary>
    internal static class EconDeltaSync
    {
        private static readonly FieldInfo FiMoneyList = Util.ReflectionSurface.RequiredField(
            typeof(GameUIScreen), "m_AddMoneyPopupList");
        private static readonly FieldInfo FiExpList = Util.ReflectionSurface.RequiredField(
            typeof(GameUIScreen), "m_AddShopExpPopupList");
        private static readonly FieldInfo FiMoneyTimer = Util.ReflectionSurface.RequiredField(
            typeof(GameUIScreen), "m_AddMoneyPopupTimer");
        private static readonly FieldInfo FiExpTimer = Util.ReflectionSurface.RequiredField(
            typeof(GameUIScreen), "m_AddShopExpPopupTimer");

        internal static void Apply(byte kind, float value)
        {
            if (CoopCore.Role != CoopRole.Client)
                return;
            GameUIScreen screen = CSingleton<GameUIScreen>.Instance;
            if (screen == null)
                return;

            if (kind == 1 || kind == 2)
            {
                var list = (List<float>)FiMoneyList.GetValue(screen);
                if (list == null)
                    return;
                if (list.Count == 0)
                    FiMoneyTimer.SetValue(screen, 10f);
                list.Add(value);
                return;
            }

            // GameUIScreen only renders positive XP deltas.
            if (kind == 3 && value > 0f)
            {
                var list = (List<int>)FiExpList.GetValue(screen);
                if (list == null)
                    return;
                if (list.Count == 0)
                    FiExpTimer.SetValue(screen, 10f);
                list.Add((int)value);
            }
        }
    }
}
