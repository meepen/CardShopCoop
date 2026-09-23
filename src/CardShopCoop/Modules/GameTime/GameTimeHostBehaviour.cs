using CardShopCoop.Attributes;
using CardShopCoop.Net.Connection;
using CardShopCoop.Runtime;
using HarmonyLib;

namespace CardShopCoop.Modules.GameTime
{
    /// <summary>Host-only source of the live authoritative game-time stream.</summary>
    [ServerBehaviour]
    public sealed class GameTimeHostBehaviour : CoopBehaviour
    {
        private static GameTimeHostBehaviour _active;
        private CoopRuntimeContext _context;
        private Harmony _harmony;
        private bool _hasObservedTime;
        private int _observedDay;
        private int _observedHour;
        private int _observedMinute;
        private bool _shutdown;

        private void OnEnable()
        {
            if (_shutdown || _harmony != null)
            {
                return;
            }

            _context = RuntimeContext;
            _active = this;
            _harmony = new Harmony("com.zwhit.cardshopcoop.game-time.host");
            _harmony.CreateClassProcessor(typeof(TimeChangedPatch)).Patch();
        }

        [OnFullyJoined]
        private void SendJoinBaseline(PeerConnection connection)
        {
            if (_shutdown || connection == null || !IsJoinPhase(connection.State)
                || !_context.InGame() || !TryBuildMessage(null, out var message))
            {
                return;
            }

            _context.Send(connection.Id, message);
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            _harmony?.UnpatchSelf();
            _harmony = null;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }

            _context = null;
            _hasObservedTime = false;
        }

        private void OnDestroy() => Shutdown();

        private void BroadcastWhenTimeChanged(LightManager manager)
        {
            if (_shutdown || !_context.InGame() || !TryBuildMessage(manager, out var message))
            {
                return;
            }

            if (_hasObservedTime && _observedDay == message.Day && _observedHour == message.Hour
                && _observedMinute == message.Minute)
            {
                return;
            }

            RecordObservedTime(message);
            _context.Broadcast(message);
        }

        private static bool TryBuildMessage(LightManager knownManager, out DayTimeMessage message)
        {
            var manager = knownManager ?? GameTimeInterop.FindSceneManager();
            if (!GameTimeInterop.IsSceneReady(manager))
            {
                message = null;
                return false;
            }

            var hour = (int)GameTimeInterop.TimeHour.GetValue(manager);
            var minute = (int)GameTimeInterop.TimeMin.GetValue(manager);
            message = new DayTimeMessage
            {
                Day = CPlayerData.m_CurrentDay,
                Hour = hour,
                Minute = minute,
                MinuteFloat = (float)GameTimeInterop.TimeMinFloat.GetValue(manager),
                // DelayUpdateEnv resets the light clock before the queued OnDayStarted event
                // resets the sign and CPlayerData. Do not carry the previous day's flag into
                // that transient morning frame.
                ShopOnceOpen = CPlayerData.m_IsShopOnceOpen
                    && !(hour == 8 && minute == 0
                        && !(bool)GameTimeInterop.HasDayEnded.GetValue(manager)),
                TimeOfDayIndex = (int)GameTimeInterop.TimeOfDayIndex.GetValue(manager),
                HasDayEnded = (bool)GameTimeInterop.HasDayEnded.GetValue(manager),
            };

            if (_active != null && _active._hasObservedTime && message.Day > _active._observedDay)
            {
                message.ShopOnceOpen = false;
            }

            return true;
        }

        private void RecordObservedTime(DayTimeMessage message)
        {
            _hasObservedTime = true;
            _observedDay = message.Day;
            _observedHour = message.Hour;
            _observedMinute = message.Minute;
        }

        private static bool IsJoinPhase(ConnectionState state)
        {
            return state == ConnectionState.Transferring || state == ConnectionState.FullyJoined;
        }

        /// <summary>
        /// EvaluateTimeClock is the game's authoritative live scalar stream after it advances the
        /// clock, including the morning reset and the 21:00 day-end transition. The hook sends
        /// only when its observable minute changes; it is not a periodic healing snapshot.
        /// </summary>
        [HarmonyPatch(typeof(LightManager), "EvaluateTimeClock")]
        private static class TimeChangedPatch
        {
            [HarmonyPostfix]
            private static void Postfix(LightManager __instance)
            {
                _active?.BroadcastWhenTimeChanged(__instance);
            }
        }
    }
}
