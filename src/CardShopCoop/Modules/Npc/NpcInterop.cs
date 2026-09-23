using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using CardShopCoop.Runtime;

namespace CardShopCoop.Modules.Npc
{
    /// <summary>Non-fabricating access to the scene-owned NPC managers.</summary>
    internal static class NpcInterop
    {
        private static readonly FieldInfo WorkerList =
            AccessTools.Field(typeof(WorkerManager), "m_WorkerList");

        internal static CustomerManager CustomerManager => SceneRef<CustomerManager>.Get();

        internal static WorkerManager WorkerManager => SceneRef<WorkerManager>.Get();

        internal static List<Worker> Workers
        {
            get
            {
                var manager = WorkerManager;
                return manager == null || WorkerList == null
                    ? null : WorkerList.GetValue(manager) as List<Worker>;
            }
        }

        internal static bool TryGetCustomerIndex(Customer customer, out int index)
        {
            index = -1;
            var customers = CustomerManager?.GetCustomerList();
            if (customers == null || customer == null)
            {
                return false;
            }

            index = customers.IndexOf(customer);
            return index >= 0;
        }

        internal static bool TryGetWorkerIndex(Worker worker, out int index)
        {
            index = worker == null ? -1 : worker.m_WorkerIndex;
            var workers = Workers;
            return workers != null && index >= 0 && index < workers.Count
                && ReferenceEquals(workers[index], worker);
        }
    }
}
