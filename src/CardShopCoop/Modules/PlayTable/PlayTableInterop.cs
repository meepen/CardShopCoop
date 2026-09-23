using System;
using System.Collections.Generic;
using System.Reflection;
using CardShopCoop.Modules.World;
using CardShopCoop.Runtime;
using HarmonyLib;
using UnityEngine;

namespace CardShopCoop.Modules.PlayTable
{
    /// <summary>The narrow boundary from table state into placement's stable identity registry.</summary>
    internal static class PlayTablePlacementInterop
    {
        internal const int TableIdentityKind = 6;

        internal static bool TryGetTableKey(InteractablePlayTable table, out int key)
        {
            key = 0;
            if (table == null)
            {
                return false;
            }

            if (PlacementApi.TryMakeObjectKey(TableIdentityKind, table, out key))
            {
                return true;
            }

            CoopPlugin.Log.LogWarning("play-table: failed to resolve table key; table="
                + table.name + " type=" + table.m_ObjectType);
            return false;
        }
    }

    internal sealed class PlayTableSeatSnapshot
    {
        internal bool PlayerOccupied;
        internal bool SeatOccupied;
        internal bool SeatBooked;
        internal bool QueueOccupied;
        internal bool PlayerSeat;
    }

    internal static class PlayTableInterop
    {
        private static readonly FieldInfo PlayerOccupiedField =
            AccessTools.Field(typeof(InteractablePlayTable), "m_IsPlayerOccupied");
        private static readonly FieldInfo TableModeField =
            AccessTools.Field(typeof(PlayTableGame), "m_IsPlayTableGameMode");
        private static readonly FieldInfo CurrentTableField =
            AccessTools.Field(typeof(PlayTableGame), "m_CurrentInteractablePlayTable");
        private static readonly MethodInfo StopTableGameMethod =
            AccessTools.Method(typeof(InteractablePlayTable), "StopTableGame");

        internal static ShelfManager FindShelfManager()
            => SceneRef<ShelfManager>.Get();

        internal static List<InteractablePlayTable> Tables(ShelfManager manager)
            => manager == null ? null : manager.m_PlayTableList;

        internal static int TableIndex(ShelfManager manager, InteractablePlayTable table)
            => manager?.m_PlayTableList == null || table == null
                ? -1 : manager.m_PlayTableList.IndexOf(table);

        internal static bool IsPlayerSeat(InteractablePlayTable table, int seat)
        {
            var list = table?.m_IsPlayerSeat;
            return list != null && seat >= 0 && seat < list.Count && list[seat];
        }

        internal static bool IsRosterManaged(InteractablePlayTable table,
            Func<int, bool> hasActiveMatch, Func<int, bool> hasLocalLaunch)
        {
            return table != null && PlayTablePlacementInterop.TryGetTableKey(table, out var key)
                && (hasActiveMatch(key) || hasLocalLaunch(key));
        }

        internal static bool IsLocalGameplayTable(InteractablePlayTable table,
            Func<int, bool> hasLocalLaunch)
        {
            return table != null && (HasEnteredTable(table)
                || (PlayTablePlacementInterop.TryGetTableKey(table, out var key)
                    && hasLocalLaunch(key)));
        }

        internal static bool HasEnteredTable(InteractablePlayTable table)
        {
            var manager = SceneRef<PlayCardGameManager>.Get();
            var game = manager?.m_PlayTableGame;
            var current = game == null ? null : CurrentTableField?.GetValue(game)
                as InteractablePlayTable;
            return game != null && TableModeField != null && (bool)TableModeField.GetValue(game)
                && ReferenceEquals(current, table);
        }

        internal static InteractablePlayTable CurrentTable(PlayTableGame game)
            => game == null ? null : CurrentTableField?.GetValue(game) as InteractablePlayTable;

        internal static bool HostPlayingAt(InteractablePlayTable table)
            => HasEnteredTable(table);

        internal static bool TryGetClickedSeat(InteractablePlayTable table, out int seat)
        {
            seat = -1;
            var occupied = table?.m_IsSeatOccupied;
            var controller = SceneRef<InteractionPlayerController>.Get();
            var collider = controller?.m_PlayerCollider;
            if (table == null || occupied == null || occupied.Count < 2 || collider == null)
            {
                return false;
            }

            var vector = table.transform.position - collider.transform.position;
            vector.y = 0f;
            if (Vector3.Dot(vector.normalized, table.transform.right) > 0f)
            {
                if (occupied[1] && !occupied[0])
                {
                    seat = 0;
                }
            }
            else if (occupied[0] && !occupied[1])
            {
                seat = 1;
            }

            return seat >= 0;
        }

        internal static PlayTableSeatSnapshot CaptureSeat(InteractablePlayTable table, byte seat)
        {
            return new PlayTableSeatSnapshot
            {
                PlayerOccupied = table != null && (bool)(PlayerOccupiedField?.GetValue(table) ?? false),
                SeatOccupied = ReadSeat(table?.m_IsSeatOccupied, seat),
                SeatBooked = ReadSeat(table?.m_IsSeatBooked, seat),
                QueueOccupied = ReadSeat(table?.m_IsQueueOccupied, seat),
                PlayerSeat = ReadSeat(table?.m_IsPlayerSeat, seat),
            };
        }

        internal static void RestoreSeat(InteractablePlayTable table,
            PlayTableSeatSnapshot snapshot, byte seat)
        {
            if (table == null || snapshot == null)
            {
                return;
            }

            PlayerOccupiedField?.SetValue(table, snapshot.PlayerOccupied);
            SetSeat(table.m_IsSeatOccupied, snapshot.SeatOccupied, seat);
            SetSeat(table.m_IsSeatBooked, snapshot.SeatBooked, seat);
            SetSeat(table.m_IsQueueOccupied, snapshot.QueueOccupied, seat);
            SetSeat(table.m_IsPlayerSeat, snapshot.PlayerSeat, seat);
        }

        internal static bool ReserveSeat(InteractablePlayTable table, byte seat,
            out PlayTableSeatSnapshot before)
        {
            before = null;
            if (table == null || seat >= 2 || table.m_IsSeatOccupied == null
                || table.m_IsSeatBooked == null || table.m_IsQueueOccupied == null
                || table.m_IsPlayerSeat == null || seat >= table.m_IsSeatOccupied.Count
                || seat >= table.m_IsSeatBooked.Count || seat >= table.m_IsQueueOccupied.Count
                || seat >= table.m_IsPlayerSeat.Count || table.m_IsSeatOccupied[seat])
            {
                return false;
            }

            if (table.m_IsSeatBooked[seat] || table.m_IsQueueOccupied[seat])
            {
                return false;
            }

            before = CaptureSeat(table, seat);
            PlayerOccupiedField?.SetValue(table, true);
            SetSeat(table.m_IsSeatOccupied, true, seat);
            SetSeat(table.m_IsSeatBooked, true, seat);
            SetSeat(table.m_IsQueueOccupied, true, seat);
            SetSeat(table.m_IsPlayerSeat, true, seat);
            return true;
        }

        internal static void RestoreReservedSeat(InteractablePlayTable table,
            PlayTableSeatSnapshot before, byte seat)
        {
            if (table == null || before == null)
            {
                return;
            }

            // The reservation owns every field it changed. Vanilla can clear one of these
            // fields while leaving the others set, so restoring only when all four still match
            // the reservation leaks a partial reservation. Always restore the captured values.
            RestoreSeat(table, before, seat);
        }

        internal static int DeckCardCount()
        {
            var decks = CPlayerData.m_DeckCompactCardDataList;
            var index = CPlayerData.m_CurrentSelectedDeckIndex;
            return decks != null && index >= 0 && index < decks.Count && decks[index] != null
                ? decks[index].GetTotalCardCount() : 0;
        }

        internal static void InvokeStopTableGame(InteractablePlayTable table)
        {
            if (StopTableGameMethod == null)
            {
                throw new MissingMethodException(typeof(InteractablePlayTable).FullName,
                    "StopTableGame");
            }

            StopTableGameMethod.Invoke(table, null);
        }

        private static bool ReadSeat(List<bool> values, byte seat)
            => values != null && seat < values.Count && values[seat];

        private static void SetSeat(List<bool> values, bool value, byte seat)
        {
            if (values != null && seat < values.Count)
            {
                values[seat] = value;
            }
        }

    }
}
