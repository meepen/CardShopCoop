using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using CardShopCoop.Api;
using CardShopCoop.Modules.Prediction;
using CardShopCoop.Net;
using CardShopCoop.Net.Protocol;

namespace CardShopCoop.Runtime
{
    /// <summary>
    /// Discovers other mods that integrate with CardShopCoop and installs the public
    /// <see cref="CoopApi"/> binding for them.
    ///
    /// Discovery is dependency-driven: a mod declares
    /// <c>[BepInDependency("com.zwhit.cardshopcoop", SoftDependency)]</c> and marks its DTOs and
    /// behaviours with the public attributes; no registration call is required. BepInEx loads us
    /// before any dependent, so by the first frame every dependent has an instance and its
    /// assembly can be registered. Running later would also be safe for DTOs (the catalog only
    /// freezes when a session starts), but the first frame keeps it deterministic.
    /// </summary>
    internal sealed class ExternalCoopMods : ICoopBinding
    {
        private readonly object _gate = new object();
        private readonly List<Assembly> _assemblies = new List<Assembly>();
        private readonly List<IProtocolRegistration> _registrations = new List<IProtocolRegistration>();
        private bool _discovered;

        public static ExternalCoopMods Instance
        {
            get;
        } = new ExternalCoopMods();

        private ExternalCoopMods()
        {
        }

        /// <summary>Assemblies integrated so far, for behaviour discovery. Does NOT trigger
        /// discovery itself: discovery must run from the first frame, after BepInEx has
        /// instantiated dependents that load after us. Triggering it from a registry built during
        /// our own Awake would latch an empty scan.</summary>
        public IReadOnlyList<Assembly> Assemblies
        {
            get
            {
                lock (_gate)
                {
                    return _assemblies.ToArray();
                }
            }
        }

        /// <summary>Scans the BepInEx plugin graph once for dependents of CardShopCoop.</summary>
        public void EnsureDiscovered()
        {
            lock (_gate)
            {
                if (_discovered)
                {
                    return;
                }

                _discovered = true;
            }

            try
            {
                foreach (var pair in Chainloader.PluginInfos)
                {
                    var info = pair.Value;
                    if (info == null || info.Metadata == null
                        || string.Equals(info.Metadata.GUID, CoopPlugin.Guid, StringComparison.Ordinal)
                        || !DependsOnUs(info))
                    {
                        continue;
                    }

                    var instance = info.Instance;
                    var assembly = instance != null ? instance.GetType().Assembly : null;
                    if (assembly == null)
                    {
                        CoopPlugin.Log?.LogWarning("[api] '" + info.Metadata.GUID
                            + "' depends on CardShopCoop but has no live instance; skipping integration");
                        continue;
                    }

                    RegisterCore(assembly, info.Metadata.GUID);
                }
            }
            catch (Exception error)
            {
                CoopPlugin.Log?.LogError("[api] external co-op mod discovery failed: " + error);
            }
        }

        /// <summary>Explicit registration escape hatch for mods without a dependency attribute.</summary>
        public void Register(Assembly assembly)
        {
            RegisterCore(assembly, "manual:" + (assembly == null ? "<null>" : assembly.GetName().Name));
        }

        private static bool DependsOnUs(PluginInfo info)
        {
            var dependencies = info.Dependencies;
            if (dependencies == null)
            {
                return false;
            }

            foreach (var dependency in dependencies)
            {
                if (dependency != null
                    && string.Equals(dependency.DependencyGUID, CoopPlugin.Guid, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private void RegisterCore(Assembly assembly, string ownerId)
        {
            if (assembly == null)
            {
                return;
            }

            // Claim the assembly before the throwing work so a concurrent manual Register and
            // auto-discovery cannot both attempt the same (fail-fast) registration. Roll back the
            // claim if registration fails so a later retry is still possible.
            lock (_gate)
            {
                if (_assemblies.Contains(assembly))
                {
                    return;
                }

                _assemblies.Add(assembly);
            }

            var assemblyName = assembly.GetName().Name;
            CoopPlugin.Log?.LogInfo("[api] integrating external co-op mod '" + ownerId
                + "' (assembly " + assemblyName + ")");

            CardShopCoop.Net.Protocol.IProtocolRegistration registration = null;
            try
            {
                if (HasNetworkMessages(assembly))
                {
                    registration = MessageRegistry.RegisterAssembly(assembly, "ext:" + ownerId);
                }

                ModuleCatalog.BindExternal(assemblyName);
            }
            catch (Exception error)
            {
                // A broken external mod must not take the whole session down; name it loudly and
                // leave it out entirely (no DTOs, no behaviours) rather than integrating a
                // half-registered contract.
                try
                {
                    registration?.Dispose();
                }
                catch (Exception disposeError)
                {
                    CoopPlugin.Log?.LogWarning("[api] could not roll back message registration for '"
                        + assemblyName + "': " + disposeError.Message);
                }
                lock (_gate)
                {
                    _assemblies.Remove(assembly);
                }
                CoopPlugin.Log?.LogError("[api] external co-op mod '" + assemblyName
                    + "' failed integration and will not be registered: " + error);
                return;
            }

            if (registration != null)
            {
                lock (_gate)
                {
                    _registrations.Add(registration);
                }
            }
        }

        private static bool HasNetworkMessages(Assembly assembly)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                types = exception.Types;
            }

            foreach (var type in types)
            {
                if (type != null && type.GetCustomAttribute<NetworkMessageAttribute>(false) != null)
                {
                    return true;
                }
            }

            return false;
        }

        // ---- ICoopBinding: the runtime half read by the public CoopApi facade ----

        public ICoopContext Context => CoopCore.Instance == null ? null : CoopCore.Instance.LiveContext;

        public bool PredictionActive => PredictionApi.IsActive;
        public bool IsReconciling => PredictionApi.IsReconciling;
        public bool IsApplying => PredictionApi.IsApplying;

        public Guid Predict(string scope, Action<Guid> send, Action apply, Action undo,
            bool applyLocally)
            => PredictionApi.Predict(scope, send, apply, undo, applyLocally);

        public void ApplyAuthoritative(Guid predictionId, Action apply)
            => PredictionApi.ApplyAuthoritative(predictionId, apply);

        public void ApplyConfirmed(Guid predictionId, Action apply)
            => PredictionApi.ApplyConfirmed(predictionId, apply);

        public void ConfirmSuperseded(Guid predictionId)
            => PredictionApi.ConfirmSuperseded(predictionId);

        public bool IsPending(Guid predictionId) => PredictionApi.IsPending(predictionId);

        public void Rollback(ICoopContext context, int connectionId, Guid predictionId)
            => PredictionApi.Rollback(context, connectionId, predictionId);
    }
}
