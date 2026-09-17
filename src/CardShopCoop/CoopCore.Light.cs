using System;
using System.Collections;
using CardShopCoop.Sync;
using UnityEngine;

namespace CardShopCoop
{
    public partial class CoopCore
    {
        /// <summary>Queues the mirrored morning reset; the clock hand-off itself lives in
        /// <see cref="TimeSync"/>.</summary>
        internal void MarkClientDayResetPending()
        {
            _clientDayResetPending = true;
            _clientDayResetDeadline = Time.time + 10f;
        }

        /// <summary>Runs the same environment reset the host ran for a rollover, on the client's
        /// own LightManager. The authoritative day number has already arrived, so the client
        /// never calls GoNextDay itself.</summary>
        internal void TryStartClientDayReset()
        {
            if (Role != CoopRole.Client)
                return;

            if (_clientDayResetDeadline > 0f && Time.time >= _clientDayResetDeadline
                && (_clientDayResetPending || _clientDayResetInFlight))
            {
                _clientDayResetPending = false;
                _clientDayResetDeadline = 0f;
                _clientDayResetInFlight = false;
                Patches.GamePatches.AllowNextDayStarted = false;
                // Never leave prediction frozen when the mirrored reset is abandoned.
                _time.ClearSuspension();
                CoopPlugin.Log.LogWarning("client day reset timed out; continuing with host clock/light correction");
                return;
            }

            if (!_clientDayResetPending)
                return;
            if (_clientDayResetInFlight)
                return;
            if (!InGameLevel())
                return;
            if (LightClockInterop.DayReset == null)
                return;

            LightManager manager = ResolveLightManager();
            if (manager == null)
                return;

            // Freeze prediction while the mirrored DelayUpdateEnv rewrites the clock to 08:00,
            // so the predictor cannot fight it.
            _time.SuspendPrediction();

            // Close client-only UI/state before the environment coroutine starts. These
            // are intentionally best-effort; the reset itself must still be attempted.
            try
            {
                Sync.ReportSync.CloseClientReport();
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("day change: closing stale report: " + e.Message); }
            try
            {
                Sync.RegisterSync.ForceExitManned();
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("day change: force-exit register: " + e.Message); }

            _clientDayResetPending = false;
            _clientDayResetInFlight = true;
            _clientDayResetDeadline = Time.time + 10f;
            Patches.GamePatches.AllowNextDayStarted = true;
            manager.StartCoroutine(ClientDayResetRoutine(manager));
        }

        private IEnumerator ClientDayResetRoutine(LightManager manager)
        {
            try
            {
                yield return (IEnumerator)LightClockInterop.DayReset.Invoke(manager, null);
            }
            finally
            {
                _clientDayResetInFlight = false;
                _clientDayResetDeadline = 0f;
                Patches.GamePatches.AllowNextDayStarted = false;
                // DelayUpdateEnv left the clock at 08:00 of the new day: re-baseline there.
                _time.ResumePrediction();
            }
        }
    }
}
