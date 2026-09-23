using System;
using System.Reflection;
using HarmonyLib;
using CardShopCoop.Runtime;
using CardShopCoop.Util;
using UnityEngine;
using CardShopCoop.Modules.Economy;

namespace CardShopCoop.Modules.Expansion
{
    /// <summary>Runtime-discovered expansion UI and scene access. No Unity 6-only lookup API is
    /// used, so the same DLL can find inactive screens on the legacy build.</summary>
    internal static class ExpansionInterop
    {
        /// <summary>Expansion-local handle for the shared Economy reservation. Vanilla remains
        /// the sole owner of the actual wallet charge.</summary>
        internal readonly struct SpendToken
        {
            internal SpendToken(double amount, EconomyAuthority.HostSpendReservation reservation)
            {
                Reservation = reservation;
            }
            internal bool IsValid
            {
                get
                {
                    return Reservation != null;
                }
            }
            internal EconomyAuthority.HostSpendReservation Reservation
            {
                get;
            }
        }

        private static readonly MethodInfo MiEvaluatePanel = AccessTools.Method(
            typeof(ExpansionShopUIScreen), "EvaluateShopPanelUI");
        private static readonly MethodInfo MiInitializeRooms = AccessTools.Method(
            typeof(UnlockRoomManager), "Init");
        private static readonly FieldInfo FiShopBUnlockLevelRequired =
            ReflectionSurface.RequiredField(typeof(UnlockRoomManager), "m_ShopB_UnlockLevelRequired");
        private static readonly FieldInfo FiShopBUnlockPrice =
            ReflectionSurface.RequiredField(typeof(UnlockRoomManager), "m_ShopB_UnlockPrice");
        internal static bool ApplyingAuthoritativeState
        {
            get;
            private set;
        }

        internal static UnlockRoomManager FindUnlockManager()
            => SceneRef<UnlockRoomManager>.Get();

        internal static ExpansionShopUIScreen FindExpansionScreen()
            => SceneRef<ExpansionShopUIScreen>.Get();

        internal static bool IsOpen(ExpansionShopUIScreen screen)
        {
            if (screen == null)
            {
                return false;
            }

            try
            {
                return screen.IsScreenOpened();
            }
            catch (Exception exception)
            {
                CoopPlugin.Log.LogWarning("Expansion screen state could not be read: " + exception.Message);
                return false;
            }
        }

        internal static void RefreshOpenScreen(ExpansionShopUIScreen screen)
        {
            if (IsOpen(screen))
            {
                MiEvaluatePanel?.Invoke(screen, null);
            }
        }

        internal static void SaveShelfData()
        {
            var shelf = SceneRef<ShelfManager>.Get();
            shelf?.SaveInteractableObjectData();
        }

        internal static bool TryValidatePurchase(UnlockRoomManager manager, byte kind,
            out int index, out float cost, out string rejection)
        {
            index = -1;
            cost = 0f;
            rejection = null;

            if (manager == null)
            {
                rejection = "unlock manager is unavailable";
                return false;
            }

            if (kind == 2)
            {
                var shopBRequiredLevel = Convert.ToInt32(FiShopBUnlockLevelRequired.GetValue(manager));
                cost = Convert.ToSingle(FiShopBUnlockPrice.GetValue(manager));
                if (CPlayerData.m_IsWarehouseRoomUnlocked)
                {
                    rejection = "shop lot B is already unlocked";
                }
                else if (CPlayerData.m_ShopLevel + 1 < shopBRequiredLevel)
                {
                    rejection = "shop level " + (CPlayerData.m_ShopLevel + 1)
                        + " is below required level " + shopBRequiredLevel;
                }
                else if (!HasEnoughCoins(cost))
                {
                    rejection = "host has " + CPlayerData.m_CoinAmountDouble
                        + " coins but needs " + cost;
                }

                return rejection == null;
            }

            if (kind != 0 && kind != 1)
            {
                rejection = "unsupported purchase kind " + kind;
                return false;
            }

            var warehouse = kind == 1;
            index = warehouse ? CPlayerData.m_UnlockWarehouseRoomCount : CPlayerData.m_UnlockRoomCount;
            var blockers = warehouse ? manager.m_LockedWarehouseRoomBlockerList
                : manager.m_LockedRoomBlockerList;
            var blockerCount = blockers?.Count ?? 0;
            if (index < 0 || index >= blockerCount)
            {
                rejection = (warehouse ? "warehouse" : "shop") + " expansion index " + index
                    + " is outside blocker count " + blockerCount;
                return false;
            }

            if (warehouse && !CPlayerData.m_IsWarehouseRoomUnlocked)
            {
                rejection = "warehouse room is not unlocked";
                return false;
            }

            var requiredLevel = warehouse
                ? 20 + index * 5 + index / 4 * 10
                : Mathf.Clamp(index * 2 + 1, 2, 60) + index / 4 * 2;
            if (CPlayerData.m_ShopLevel + 1 < requiredLevel)
            {
                rejection = (warehouse ? "warehouse" : "shop") + " expansion " + index
                    + " requires shop level " + requiredLevel + ", host is at "
                    + (CPlayerData.m_ShopLevel + 1);
                return false;
            }

            cost = warehouse ? CPlayerData.GetUnlockWarehouseRoomCost(index)
                : CPlayerData.GetUnlockShopRoomCost(index);
            if (!HasEnoughCoins(cost))
            {
                rejection = "host has " + CPlayerData.m_CoinAmountDouble
                    + " coins but needs " + cost;
                return false;
            }

            return true;
        }

        internal static void ApplyBaseline(UnlockRoomManager manager, ExpansionBaselineMessage message)
        {
            ApplyingAuthoritativeState = true;
            try
            {
                CPlayerData.m_UnlockRoomCount = message.UnlockRoomCount;
                CPlayerData.m_UnlockWarehouseRoomCount = message.UnlockWarehouseRoomCount;
                CPlayerData.m_IsWarehouseRoomUnlocked = message.IsWarehouseRoomUnlocked;
                if (manager != null)
                {
                    MiInitializeRooms?.Invoke(manager, null);
                }
            }
            finally
            {
                ApplyingAuthoritativeState = false;
            }
        }

        internal static void ApplyDelta(UnlockRoomManager manager, ExpansionDeltaMessage message)
        {
            ApplyingAuthoritativeState = true;
            try
            {
                switch (message.Area)
                {
                    case 0:
                        CPlayerData.m_UnlockRoomCount = message.Count;
                        break;
                    case 1:
                        CPlayerData.m_UnlockWarehouseRoomCount = message.Count;
                        break;
                    case 2:
                        CPlayerData.m_IsWarehouseRoomUnlocked = message.Unlocked;
                        break;
                    default:
                        throw new InvalidOperationException("Unknown expansion delta area "
                            + message.Area + ".");
                }

                if (manager != null)
                {
                    MiInitializeRooms?.Invoke(manager, null);
                }
            }
            finally
            {
                ApplyingAuthoritativeState = false;
            }
        }

        internal static void InitializeRooms(UnlockRoomManager manager)
        {
            if (manager != null)
            {
                MiInitializeRooms?.Invoke(manager, null);
            }
        }

        internal static bool TryReserveExpansion(double amount, out SpendToken token)
        {
            if (double.IsNaN(amount) || double.IsInfinity(amount) || amount < 0d
                || !EconomyAuthority.TryReserveHostSpend(amount, out var reservation))
            {
                token = new SpendToken(amount, null);
                return false;
            }

            token = new SpendToken(amount, reservation);
            return true;
        }

        internal static bool CommitExpansion(SpendToken token)
        {
            return token.IsValid && EconomyAuthority.QueueHostSpend(token.Reservation);
        }

        internal static void ReleaseExpansion(SpendToken token)
        {
            if (token.IsValid)
            {
                EconomyAuthority.ReleaseHostSpend(token.Reservation);
            }
        }

        internal static void CancelExpansion(SpendToken token)
        {
            if (token.IsValid)
            {
                EconomyAuthority.CancelHostSpend(token.Reservation);
            }
        }

        internal readonly struct StateSnapshot
        {
            internal StateSnapshot(int rooms, int warehouseRooms, bool warehouseUnlocked)
            {
                Rooms = rooms;
                WarehouseRooms = warehouseRooms;
                WarehouseUnlocked = warehouseUnlocked;
            }

            internal int Rooms
            {
                get;
            }
            internal int WarehouseRooms
            {
                get;
            }
            internal bool WarehouseUnlocked
            {
                get;
            }
        }

        internal static StateSnapshot CaptureState()
            => new StateSnapshot(CPlayerData.m_UnlockRoomCount,
                CPlayerData.m_UnlockWarehouseRoomCount, CPlayerData.m_IsWarehouseRoomUnlocked);

        internal static void RollbackState(UnlockRoomManager manager, StateSnapshot snapshot)
        {
            CPlayerData.m_UnlockRoomCount = snapshot.Rooms;
            CPlayerData.m_UnlockWarehouseRoomCount = snapshot.WarehouseRooms;
            CPlayerData.m_IsWarehouseRoomUnlocked = snapshot.WarehouseUnlocked;
            MiInitializeRooms?.Invoke(manager, null);
        }

        private static bool HasEnoughCoins(double amount)
        {
            return !double.IsNaN(amount) && !double.IsInfinity(amount)
                && amount >= 0d && CPlayerData.m_CoinAmountDouble >= amount;
        }
    }
}
