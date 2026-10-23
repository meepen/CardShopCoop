using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.UI
{
    internal static class Live<T> where T : Component
    {
        private static readonly FieldInfo Field = AccessTools.Field(typeof(CSingleton<T>), "instance");

        internal static T Get()
        {
            if (Field == null)
            {
                return null;
            }

            var value = Field.GetValue(null);
            return value as T;
        }
    }
}
