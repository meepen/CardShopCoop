using System;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Pure math for the client's predicted day/night clock (see <see cref="TimeSync"/>). Kept
    /// free of Unity types so the correction rules can be exercised by a console test harness.
    ///
    /// The game's clock is a real-time integrator -
    /// <c>m_TimeMinFloat += Time.deltaTime * m_TimerLerpSpeed</c> - gated on the shop having been
    /// opened and the day not being over, and clamped at 21:00. Both peers integrate the same
    /// rate, so a client that runs the same clock with the host's gate flags stays in lockstep;
    /// this type only decides the small bounded nudge that pulls out a residual offset, and when
    /// an offset is too large to nudge and must be corrected immediately instead.
    /// </summary>
    public static class ClockPrediction
    {
        /// <summary>The clock stops here (day end), matching LightManager.EvaluateTimeClock.</summary>
        public const float DayEndMinutes = 21f * 60f;

        /// <summary>Morning reset time; the game starts every day here.</summary>
        public const float DayStartMinutes = 8f * 60f;

        /// <summary>Below this, an offset is left alone: it is under a second of real time and
        /// chasing it would keep re-writing the clock rate every frame.</summary>
        public const float DeadbandMinutes = 0.02f;

        /// <summary>An offset at or beyond this is a missed gate or a wrong phase, not drift, and
        /// is corrected immediately rather than nudged.</summary>
        public const float SnapMinutes = 2f;

        /// <summary>How far the client's own rate may be stretched while converging.</summary>
        public const float MinRateFactor = 0.85f;
        public const float MaxRateFactor = 1.15f;

        /// <summary>Rate-factor gain per in-game minute of offset.</summary>
        public const float RateGain = 1.5f;

        /// <summary>LightManager stores <c>m_TimeMinFloat</c> as minutes WITHIN the hour
        /// (EvaluateTimeClock resets it to 0 at 60) while the wire and this module carry total
        /// minutes since midnight. Convert before comparing them.</summary>
        public static float ToTotalMinutes(int hour, float minuteWithinHour)
        {
            return hour * 60f + minuteWithinHour;
        }

        /// <summary>
        /// Where the host's clock is now, given the last authoritative value and the real time
        /// elapsed since it was applied. A host that is not advancing (shop not open yet, or the
        /// day over) is held at the authoritative value.
        /// </summary>
        public static float HostNow(float hostMinFloat, float ageSeconds, bool advancing, float speed)
        {
            if (!advancing)
                return hostMinFloat;
            if (ageSeconds < 0f)
                ageSeconds = 0f;
            float now = hostMinFloat + ageSeconds * speed;
            return now > DayEndMinutes ? DayEndMinutes : now;
        }

        /// <summary>True when the offset is too large to converge smoothly and must be snapped.</summary>
        public static bool ShouldSnap(float errorMinutes)
        {
            return Math.Abs(errorMinutes) >= SnapMinutes;
        }

        /// <summary>
        /// The bounded multiplier applied to the client's own clock rate while it converges on
        /// the host. 1 when already aligned, so an aligned client is byte-for-byte vanilla.
        /// </summary>
        public static float RateFactor(float errorMinutes)
        {
            if (Math.Abs(errorMinutes) <= DeadbandMinutes)
                return 1f;
            float factor = 1f + errorMinutes * RateGain;
            if (factor < MinRateFactor)
                return MinRateFactor;
            if (factor > MaxRateFactor)
                return MaxRateFactor;
            return factor;
        }
    }
}
