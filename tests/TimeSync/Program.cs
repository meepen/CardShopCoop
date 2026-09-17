using CardShopCoop.Sync;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition)
        throw new Exception(name);
    Console.WriteLine("PASS " + name);
    passed++;
}

// ---- HostNow: the predicted host clock between syncs -------------------------------

Check(Math.Abs(ClockPrediction.HostNow(480f, 10f, advancing: true, speed: 1f) - 490f) < 0.0001f,
    "advancing host advances one in-game minute per real second");
Check(Math.Abs(ClockPrediction.HostNow(480f, 10f, advancing: false, speed: 1f) - 480f) < 0.0001f,
    "a stopped host is held at the authoritative value");
Check(Math.Abs(ClockPrediction.HostNow(480f, -5f, advancing: true, speed: 1f) - 480f) < 0.0001f,
    "negative reference age cannot run the clock backwards");
Check(Math.Abs(ClockPrediction.HostNow(1259f, 120f, advancing: true, speed: 1f)
    - ClockPrediction.DayEndMinutes) < 0.0001f,
    "prediction clamps at day end (21:00)");
Check(Math.Abs(ClockPrediction.HostNow(480f, 10f, advancing: true, speed: 0.5f) - 485f) < 0.0001f,
    "a slower host is predicted at its own rate");

// ---- Unit conversion: LightManager stores minutes WITHIN the hour -------------------

Check(Math.Abs(ClockPrediction.ToTotalMinutes(8, 0f) - 480f) < 0.0001f,
    "08:00 within-hour converts to 480 total minutes");
Check(Math.Abs(ClockPrediction.ToTotalMinutes(9, 15f) - 555f) < 0.0001f,
    "09:15 within-hour converts to 555 total minutes");
Check(Math.Abs(ClockPrediction.ToTotalMinutes(20, 59f) - 1259f) < 0.0001f,
    "20:59 within-hour converts to 1259 total minutes");
// Regression: comparing total-minutes predicted against within-hour local made every
// frame look ~9 hours off and hard-snapped instead of rate-nudging.
Check(!ClockPrediction.ShouldSnap(555f - ClockPrediction.ToTotalMinutes(9, 15f)),
    "an aligned client at 09:15 does not trip the snap threshold");

// ---- ShouldSnap / RateFactor -------------------------------------------------------

Check(!ClockPrediction.ShouldSnap(ClockPrediction.SnapMinutes - 0.001f),
    "an offset just under the snap threshold is nudged, not snapped");
Check(ClockPrediction.ShouldSnap(ClockPrediction.SnapMinutes),
    "an offset at the snap threshold snaps");
Check(ClockPrediction.ShouldSnap(-ClockPrediction.SnapMinutes),
    "snap is symmetric for a client that ran ahead");

Check(ClockPrediction.RateFactor(0f) == 1f, "aligned client runs at exactly the vanilla rate");
Check(ClockPrediction.RateFactor(ClockPrediction.DeadbandMinutes / 2f) == 1f,
    "sub-deadband offset is not chased");
Check(ClockPrediction.RateFactor(0.1f) > 1f, "a lagging client speeds up");
Check(ClockPrediction.RateFactor(-0.1f) < 1f, "a leading client slows down");
Check(ClockPrediction.RateFactor(100f) == ClockPrediction.MaxRateFactor, "gain is bounded upward");
Check(ClockPrediction.RateFactor(-100f) == ClockPrediction.MinRateFactor, "gain is bounded downward");

// ---- Convergence: a client that starts behind reaches lockstep ---------------------

float Simulate(float hostStart, float clientStart, float seconds, out float finalError)
{
    const float dt = 1f / 60f;
    float host = hostStart;
    float client = clientStart;
    int frames = (int)(seconds / dt);
    for (int i = 0; i < frames; i++)
    {
        float error = ClockPrediction.HostNow(host, 0f, advancing: true, speed: 1f) - client;
        client += dt * ClockPrediction.RateFactor(error);
        host += dt;
    }
    finalError = host - client;
    return client;
}

Simulate(480f, 479.6f, 5f, out float behind);
Check(Math.Abs(behind) <= ClockPrediction.DeadbandMinutes + 0.001f,
    "a client 0.4 minutes behind converges to within the deadband");
Simulate(480f, 480.4f, 5f, out float ahead);
Check(Math.Abs(ahead) <= ClockPrediction.DeadbandMinutes + 0.001f,
    "a client 0.4 minutes ahead converges to within the deadband");
Simulate(480f, 480f, 5f, out float aligned);
Check(Math.Abs(aligned) < 0.001f, "an aligned client never drifts on its own");

Console.WriteLine($"ALL PASS ({passed})");
