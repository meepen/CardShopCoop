using System;
using CardShopCoop.Attributes;
using CardShopCoop.Runtime;
using HarmonyLib;

namespace CardShopCoop.Modules.Economy
{
    /// <summary>Installs the one vanilla-event guard needed by RunWithVanillaSpend.</summary>
    [ServerBehaviour]
    public sealed class EconomyHostBehaviour : CoopBehaviour
    {
        private bool _active;
        private bool _shutdown;

        private void OnEnable()
        {
            if (_shutdown || _active)
            {
                return;
            }


            if (!EconomyAuthority.Activate(this, RuntimeContext))
            {

                throw new InvalidOperationException("Economy host initialization failed.");
            }


            try
            {
                Harmony.CreateClassProcessor(typeof(QueueEventPatch)).Patch();
                _active = true;
            }
            catch
            {
                EconomyAuthority.Deactivate(this);
                throw;
            }
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }


            _shutdown = true;
            if (_active)
            {
                EconomyAuthority.Deactivate(this);
                _active = false;
            }
        }

        private void OnDestroy() => Shutdown();

        [HarmonyPatch(typeof(CEventManager), "QueueEvent", new[] { typeof(CEvent) })]
        private static class QueueEventPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(CEvent evt)
                => !EconomyAuthority.DeferVanillaEvent(evt);
        }
    }
}
