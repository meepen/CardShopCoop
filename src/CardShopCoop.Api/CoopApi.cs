using System;
using System.Reflection;
using BepInEx.Logging;

namespace CardShopCoop.Api
{
    /// <summary>
    /// The runtime side of the contract. CardShopCoop.dll implements and installs exactly one of
    /// these. External mods never see this interface; they read the public <see cref="CoopApi"/>
    /// surface, which is inert (all no-ops / null) until a binding is present.
    /// </summary>
    internal interface ICoopBinding
    {
        void Register(Assembly assembly);
        ICoopContext Context
        {
            get;
        }

        bool PredictionActive
        {
            get;
        }
        bool IsReconciling
        {
            get;
        }
        bool IsApplying
        {
            get;
        }
        Guid Predict(string scope, Action<Guid> send, Action apply, Action undo);
        void Ack(Guid predictionId);
        void AckOrApply(Guid predictionId, Action apply);
        void Confirm(Guid predictionId, Action apply);
        bool IsPending(Guid predictionId);
        void Rollback(ICoopContext context, int connectionId, Guid predictionId);
    }

    /// <summary>
    /// Entry point for an integrating mod. Every member is safe when CardShopCoop is absent only
    /// if the API assembly is present; because the API ships with CardShopCoop, the normal
    /// optional-dependency pattern is to also guard direct calls with
    /// <c>BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.zwhit.cardshopcoop")</c>.
    /// The attribute/DTO integration path needs no guard at all: CardShopCoop discovers it, and
    /// auto-discovery through the BepInEx dependency graph is the preferred registration path.
    /// Explicit registration with <see cref="Register"/> is only valid once CardShopCoop is loaded.
    /// </summary>
    public static class CoopApi
    {
        internal static ICoopBinding Binding
        {
            get;
            set;
        }

        /// <summary>True while CardShopCoop is loaded and has installed its binding.</summary>
        public static bool IsAvailable => Binding != null;

        /// <summary>The live session context, or null when not connected.</summary>
        public static ICoopContext Context => Binding == null ? null : Binding.Context;

        /// <summary>Optional shared log source. CardShopCoop sets this at startup.</summary>
        public static ManualLogSource Log
        {
            get;
            internal set;
        }

        /// <summary>
        /// Registers an assembly's attributed DTOs and behaviours explicitly. Auto-discovery through
        /// the BepInEx dependency graph is the preferred path; use this escape hatch only for a mod
        /// that does not declare a BepInEx dependency. Only valid once CardShopCoop is loaded and
        /// has installed its binding (guard with <see cref="IsAvailable"/>, or declare a
        /// <c>BepInDependency</c> on com.zwhit.cardshopcoop); otherwise this throws.
        /// </summary>
        public static void Register(Assembly assembly)
        {
            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            var binding = Binding;
            if (binding == null)
            {
                throw new InvalidOperationException(
                    "CardShopCoop has not installed its API binding yet; call CoopApi.Register from your "
                    + "plugin's Awake after CardShopCoop is loaded (declare a BepInDependency on "
                    + "com.zwhit.cardshopcoop, or guard with CoopApi.IsAvailable).");
            }

            binding.Register(assembly);
        }
    }
}
