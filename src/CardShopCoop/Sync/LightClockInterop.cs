using System.Reflection;
using CardShopCoop.Util;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// LightManager's private clock and lighting surface, resolved once and shared by the
    /// day/night sync module and the client day-reset path. Keeping it in one place means a
    /// game update that renames one of these members fails loudly at one typed boundary rather
    /// than in a dozen scattered reflection lookups.
    /// </summary>
    internal static class LightClockInterop
    {
        // clock state
        internal static readonly FieldInfo TimerLerpSpeed =
            ReflectionSurface.RequiredField(typeof(LightManager), "m_TimerLerpSpeed");
        internal static readonly FieldInfo TimeHour =
            ReflectionSurface.RequiredField(typeof(LightManager), "m_TimeHour");
        internal static readonly FieldInfo TimeMin =
            ReflectionSurface.RequiredField(typeof(LightManager), "m_TimeMin");
        internal static readonly FieldInfo TimeMinFloat =
            ReflectionSurface.RequiredField(typeof(LightManager), "m_TimeMinFloat");
        internal static readonly FieldInfo HasDayEnded =
            ReflectionSurface.RequiredField(typeof(LightManager), "m_HasDayEnded");
        internal static readonly FieldInfo TimeOfDayIndex =
            ReflectionSurface.RequiredField(typeof(LightManager), "m_TImeOfDayIndex");
        internal static readonly FieldInfo FinishLoading =
            ReflectionSurface.RequiredField(typeof(LightManager), "m_FinishLoading");

        // lighting methods
        internal static readonly MethodInfo EvaluateTimeClock =
            ReflectionSurface.RequiredMethod(typeof(LightManager), "EvaluateTimeClock");
        internal static readonly MethodInfo EvaluateWorldUIBrightness =
            ReflectionSurface.RequiredMethod(typeof(LightManager), "EvaluateWorldUIBrightness");
        internal static readonly MethodInfo UpdateLightData =
            ReflectionSurface.RequiredMethod(typeof(LightManager), "UpdateLightTimeData");
        internal static readonly MethodInfo LightInit =
            ReflectionSurface.RequiredMethod(typeof(LightManager), "Init");
        internal static readonly MethodInfo DayReset =
            ReflectionSurface.RequiredMethod(typeof(LightManager), "DelayUpdateEnv");
    }
}
