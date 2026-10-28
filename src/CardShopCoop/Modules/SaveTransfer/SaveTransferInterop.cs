using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using CardShopCoop.Api;

namespace CardShopCoop.Modules.SaveTransfer
{
    /// <summary>Optional save-screen surface shared by the two live game builds.</summary>
    internal static class SaveTransferInterop
    {
        private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance
            | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Type SaveScreenType =
            typeof(CGameManager).Assembly.GetType("SaveLoadGameSlotSelectScreen", false);

        private static readonly MethodInfo _updateSlotMethod =
            SaveScreenType == null ? null : AccessTools.Method(SaveScreenType, "UpdateSlot");

        private static readonly FieldInfo _saveSlotPanels = SaveScreenType?.GetField(
            "m_SaveLoadSlotPanelUIList", All);

        // Static CSingleton<T> properties are declared on the closed generic base, not on the
        // screen type itself. IsSet is resolved reflectively so a build whose base omits it still
        // loads; both live builds expose it.
        private static readonly Type _singletonType = SaveScreenType == null
            ? null
            : typeof(CSingleton<>).MakeGenericType(SaveScreenType);
        private static readonly PropertyInfo _screenIsSet = _singletonType?.GetProperty("IsSet", All);
        private static readonly PropertyInfo _screenInstance = _singletonType?.GetProperty("Instance", All);

        internal static MethodInfo UpdateSlotMethod => _updateSlotMethod;

        internal static bool IsVisibleSaveSlot(int slot)
        {
            try
            {
                if (ScreenIsSet())
                {
                    var screen = _screenInstance?.GetValue(null, null);
                    var panels = screen == null ? null : _saveSlotPanels?.GetValue(screen) as IList;
                    if (panels != null)
                    {
                        // UpdateSlot indexes both the panel list and the saved-data list. The
                        // vanilla save UI exposes exactly four real slots; never admit an
                        // out-of-band co-op slot merely because a reshaped list is longer.
                        var real = Math.Min(panels.Count, 4);
                        return slot >= 0 && slot < real;
                    }
                }
            }
            catch (Exception e)
            {
                CoopLog.Swallow(e);
            }

            // When the screen is not instantiated yet, the game still has its fixed four-slot
            // save model. This also keeps an unexpected UI shape from blocking a real slot.
            return slot >= 0 && slot < 4;
        }

        private static bool ScreenIsSet()
        {
            if (_screenIsSet == null)
            {
                // Both live builds expose IsSet. A build without it degrades to "not set", which
                // is the conservative answer for a UI-shape probe.
                return false;
            }

            try
            {
                return _screenIsSet.GetValue(null, null) is bool isSet && isSet;
            }
            catch (Exception e)
            {
                CoopLog.Swallow(e);
                return false;
            }
        }
    }
}
