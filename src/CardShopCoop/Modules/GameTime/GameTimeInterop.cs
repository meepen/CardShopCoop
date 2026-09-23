using System.Reflection;
using CardShopCoop.Runtime;
using CardShopCoop.Util;

namespace CardShopCoop.Modules.GameTime
{
    /// <summary>
    /// LightManager's private clock surface. Keeping it in one place means a game update that
    /// renames one of these members fails loudly at one typed boundary rather than in scattered
    /// reflection lookups.
    /// </summary>
    internal static class GameTimeInterop
    {
        // Clock state. These private members have the same names in the 1.00 beta and the
        // public build 25315983 baselines.
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

        // The game's own method refreshes its display string and applies the appropriate
        // day-end flag after a client receives a host timestamp.
        internal static readonly MethodInfo EvaluateTimeClock =
            ReflectionSurface.RequiredMethod(typeof(LightManager), "EvaluateTimeClock");
        internal static readonly MethodInfo ResetSunlightIntensity =
            ReflectionSurface.RequiredMethod(typeof(LightManager), "ResetSunlightIntensity");

        internal static bool IsSceneReady(LightManager manager)
        {
            return manager != null && (bool)FinishLoading.GetValue(manager);
        }

        internal static LightManager FindSceneManager()
            => SceneRef<LightManager>.Get();
    }
}


