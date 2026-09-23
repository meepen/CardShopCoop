using System.Reflection;
using CardShopCoop.Runtime;
using CardShopCoop.Util;

namespace CardShopCoop.Modules.Light
{
    /// <summary>Private LightManager methods required by the shop-light surface.</summary>
    internal static class LightInterop
    {
        internal static readonly MethodInfo EvaluateWorldUIBrightness =
            ReflectionSurface.RequiredMethod(typeof(LightManager), "EvaluateWorldUIBrightness");

        // LightManager exists briefly before its save-loaded scene state is ready. This flag
        // has the same name on the public and Unity 6 builds and is the safe application gate.
        internal static readonly FieldInfo FinishLoading =
            ReflectionSurface.RequiredField(typeof(LightManager), "m_FinishLoading");

        internal static bool IsSceneReady(LightManager manager)
        {
            return manager != null && (bool)FinishLoading.GetValue(manager);
        }

        internal static LightManager FindSceneManager()
            => SceneRef<LightManager>.Get();

    }
}
