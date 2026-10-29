using System;
using System.Reflection;
using BepInEx.Logging;
using CardShopCoop.Net.Networking;

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
        IDisposable RegisterNetworkingBackend(INetworkingBackend backend);
        ICoopContext Context
        {
            get;
        }

        bool ActionActive
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
        Guid Submit(string scope, ActionMessage message, Action apply,
            Action<ActionResponseMessage> undo);
        void Ack(Guid actionId);
        void AckOrApply(Guid actionId, Action apply);
        void Confirm(Guid actionId, Action apply);
        void ApplyAuthoritative(Guid actionId, Action apply);
        bool IsPending(Guid actionId);
        void Rollback(ICoopContext context, int connectionId, Guid actionId);
        object GetExtendedInformation(Type informationType, object gameObject);
        void SetExtendedInformation(Type informationType, object gameObject, object information);
        bool RemoveExtendedInformation(Type informationType, object gameObject);
        object GetGlobalInformation(Type informationType);
        void SetGlobalInformation(Type informationType, object information);
        bool RemoveGlobalInformation(Type informationType);
    }

    /// <summary>
    /// Entry point for an integrating mod. Every member is safe when CardShopCoopCommunity is absent only
    /// if the API assembly is present; because the API ships with CardShopCoopCommunity, the normal
    /// optional-dependency pattern is to also guard direct calls with
    /// <c>BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("dev.meepen.cardshopcoop")</c>.
    /// The attribute/DTO integration path needs no guard at all: CardShopCoopCommunity discovers it, and
    /// auto-discovery through the BepInEx dependency graph is the preferred registration path.
    /// Explicit registration with <see cref="Register"/> is only valid once CardShopCoopCommunity is loaded.
    /// </summary>
    public static class CoopApi
    {
        internal static ICoopBinding Binding
        {
            get;
            set;
        }

        /// <summary>True while CardShopCoopCommunity is loaded and has installed its binding.</summary>
        public static bool IsAvailable => Binding != null;

        /// <summary>The live session context, or null when not connected.</summary>
        public static ICoopContext Context => Binding?.Context;

        /// <summary>Optional shared log source. CardShopCoopCommunity sets this at startup.</summary>
        public static ManualLogSource Log
        {
            get;
            internal set;
        }

        /// <summary>
        /// Registers an assembly's attributed DTOs and behaviours explicitly. Auto-discovery through
        /// the BepInEx dependency graph is the preferred path; use this escape hatch only for a mod
        /// that does not declare a BepInEx dependency. Only valid once CardShopCoopCommunity is loaded and
        /// has installed its binding (guard with <see cref="IsAvailable"/>, or declare a
        /// <c>BepInDependency</c> on dev.meepen.cardshopcoop); otherwise this throws.
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
                    "CardShopCoopCommunity has not installed its API binding yet; call CoopApi.Register from your "
                    + "plugin's Awake after CardShopCoopCommunity is loaded (declare a BepInDependency on "
                    + "dev.meepen.cardshopcoop, or guard with CoopApi.IsAvailable).");
            }

            binding.Register(assembly);
        }

        /// <summary>
        /// Registers a networking transport, so the co-op window offers it as another way to host
        /// or join. Returns a handle that unregisters it.
        ///
        /// BepInEx 5 has no plugin-unload path: a plugin component lives on BepInEx's shared
        /// manager object for the whole process, and that object can be destroyed by the game
        /// while the transport is still wanted. So do NOT dispose the handle from your plugin's
        /// <c>OnDestroy</c> - that is not an unload signal, and disposing there permanently
        /// removes the transport for the rest of the run. A transport's registration is
        /// process-lifetime (the Steam transport is the reference implementation); dispose the
        /// handle only from code that deliberately removes a transport that is no longer wanted.
        /// Only valid once CardShopCoopCommunity is loaded (declare a BepInDependency on
        /// dev.meepen.cardshopcoop, or guard with <see cref="IsAvailable"/>); otherwise this throws.
        /// </summary>
        public static IDisposable RegisterNetworkingBackend(INetworkingBackend backend)
        {
            if (backend == null)
            {
                throw new ArgumentNullException(nameof(backend));
            }

            var binding = Binding;
            if (binding == null)
            {
                throw new InvalidOperationException(
                    "CardShopCoopCommunity has not installed its API binding yet; call "
                    + "CoopApi.RegisterNetworkingBackend from your plugin's Awake after "
                    + "CardShopCoopCommunity is loaded (declare a BepInDependency on "
                    + "dev.meepen.cardshopcoop, or guard with CoopApi.IsAvailable).");
            }

            return binding.RegisterNetworkingBackend(backend);
        }

        /// <summary>
        /// Returns the extended information of this type attached to the object, or null when none
        /// is present or co-op is not loaded. <typeparamref name="TInfo"/> must be a concrete
        /// <see cref="ExtendGameObjectInformation{T}"/> subclass whose owner type the identity
        /// module replicates; any other type throws <see cref="ArgumentException"/>.
        /// </summary>
        internal static TInfo GetExtendedInformation<TInfo>(object gameObject) where TInfo : class
        {
            var binding = Binding;
            if (binding == null || gameObject == null)
            {
                return null;
            }

            return binding.GetExtendedInformation(typeof(TInfo), gameObject) as TInfo;
        }

        /// <summary>
        /// Stores/replaces extended information on the object. The host applies and broadcasts it;
        /// a client forwards the request to the host and applies the host's echo, so a client's
        /// <see cref="GetExtendedInformation{TInfo}"/> still returns the previous value until that
        /// echo arrives.
        ///
        /// <typeparamref name="TInfo"/> must be the value's concrete type. Passing a base type
        /// throws <see cref="ArgumentException"/>: the entry is keyed and serialized by the
        /// declared type, so a derived value would lose its derived fields on every other peer.
        /// </summary>
        internal static void SetExtendedInformation<TInfo>(object gameObject, TInfo information)
            where TInfo : class
        {
            if (gameObject == null)
            {
                throw new ArgumentNullException(nameof(gameObject));
            }
            if (information == null)
            {
                throw new ArgumentNullException(nameof(information));
            }

            RequireBinding().SetExtendedInformation(typeof(TInfo), gameObject, information);
        }

        /// <summary>
        /// Removes one extended-information entry from the object. On a client this returns true
        /// when the request was sent; the removal is applied on the host's echo. It throws
        /// <see cref="ArgumentException"/> when <typeparamref name="TInfo"/> is not a supported
        /// extended-information type.
        /// </summary>
        internal static bool RemoveExtendedInformation<TInfo>(object gameObject) where TInfo : class
        {
            if (gameObject == null)
            {
                throw new ArgumentNullException(nameof(gameObject));
            }

            return RequireBinding().RemoveExtendedInformation(typeof(TInfo), gameObject);
        }

        /// <summary>
        /// Returns the session-wide value of this type, or null when it has not been set or co-op
        /// is not loaded. <typeparamref name="TInfo"/> must be a class marked
        /// <see cref="GlobalInformationAttribute"/>. Unlike a DTO, this is a typed value in the
        /// shared store: the host applies and broadcasts it and a join baseline carries it.
        /// </summary>
        internal static TInfo GetGlobalInformation<TInfo>() where TInfo : class
        {
            var binding = Binding;
            return binding?.GetGlobalInformation(typeof(TInfo)) as TInfo;
        }

        /// <summary>
        /// Stores or replaces the session-wide value. The host applies and broadcasts it; a client
        /// forwards the request and applies the host echo, so <see cref="GetGlobalInformation{TInfo}"/>
        /// keeps returning the previous value until the echo arrives.
        /// <typeparamref name="TInfo"/> must be the value's concrete type.
        /// </summary>
        internal static void SetGlobalInformation<TInfo>(TInfo information) where TInfo : class
        {
            if (information == null)
            {
                throw new ArgumentNullException(nameof(information));
            }

            RequireBinding().SetGlobalInformation(typeof(TInfo), information);
        }

        /// <summary>Removes the session-wide value. On a client this returns true when the request
        /// was sent; the removal is applied on the host's echo.</summary>
        internal static bool RemoveGlobalInformation<TInfo>() where TInfo : class
            => RequireBinding().RemoveGlobalInformation(typeof(TInfo));

        private static ICoopBinding RequireBinding()
        {
            var binding = Binding;
            if (binding == null)
            {
                throw new InvalidOperationException(
                    "CardShopCoopCommunity has not installed its API binding yet; call this after "
                    + "the plugin has initialized (declare a BepInDependency on dev.meepen.cardshopcoop, "
                    + "or guard with CoopApi.IsAvailable).");
            }

            return binding;
        }
    }
}
