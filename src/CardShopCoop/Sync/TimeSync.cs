using System;
using System.Reflection;
using CardShopCoop.Net;
using CardShopCoop.Net.Messages;
using CardShopCoop.Util;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Host-authoritative day/night clock and lighting, with client-side prediction.
    ///
    /// The client used to be hard-frozen (its LightManager rate forced to zero every frame) and
    /// only corrected by a 2-second snapshot, so the clock and every time-derived visual stepped
    /// ~2 in-game minutes at once. Instead the client now runs the game's own clock between
    /// syncs, gated by the host-synced shop-open/day flags, so time and lighting advance smoothly.
    /// The host pushes a snapshot the instant a gate flips (shop first opened, day ended, day
    /// rolled over) and re-asserts the clock on a slow beat; the client only nudges its rate for
    /// small offsets and hard-snaps for large ones. Day rollover remains host-authoritative and
    /// runs the existing mirrored environment reset.
    /// </summary>
    public sealed class TimeSync : TickableCoopModule
    {
        public static TimeSync Instance;

        /// <summary>Host -> all clients.</summary>
        public Action<INetMessage> BroadcastState;

        /// <summary>Host -> one connection (join catch-up / explicit re-baseline).</summary>
        public Action<int, INetMessage> SendToClient;

        private const float ClockSliceSeconds = 2f;
        private const float LightSliceSeconds = 20f;

        /// <summary>LightManager.m_TimerLerpSpeed is 1f and is never assigned in any game build
        /// (verified in every decompiled baseline), so the host's clock rate is a constant. A
        /// modded host that changed it would be corrected by the periodic re-assertion rather
        /// than followed.</summary>
        private const float HostClockSpeed = 1f;

        // ---- host ----
        private float _clockTimer = -0.9f;
        private float _lightTimer = -2.3f;
        private int _lastGateHash;
        private bool _haveGate;
        private string _lastLightJson;
        private float _lightHeal;
        private bool _forceLight;
        private bool _observedShopLight;
        private bool _observedNightLight;
        private bool _observedSunlight;
        private bool _observedLightState;
        // Client: the last switch-model state we applied (1 on, 0 off, -1 unknown/never).
        // Tracked separately from the light GROUPS because the game's day reset flips the groups
        // without touching the InteractableLightSwitch meshes (verified in LightManager.
        // ResetSunlightIntensity), so the mesh state must be mirrored from the flag itself.
        private int _appliedSwitchLight = -1;

        // ---- client prediction ----
        private bool _clockInit;
        private bool _suspendPrediction;
        private float _hostMinFloat = ClockPrediction.DayStartMinutes;
        private float _hostRefTime;
        private bool _hostAdvancing;
        private float _rateFactor = 1f;
        private bool _loggedTimeLink;
        private bool _touchedClientClock;
        private int _lastHudTotalMinute = -1;

        public override string Name => nameof(TimeSync);

        public TimeSync()
        {
            Instance = this;
        }

        public override void Start()
        {
            Instance = this;
        }

        public override void Reset()
        {
            _clockTimer = -0.9f;
            _lightTimer = -2.3f;
            _lastGateHash = 0;
            _haveGate = false;
            _lastLightJson = null;
            _lightHeal = 0f;
            _forceLight = false;
            _lightSwitches.Clear();
            _appliedSwitchLight = -1;
            _observedShopLight = false;
            _observedNightLight = false;
            _observedSunlight = false;
            _observedLightState = false;

            _clockInit = false;
            _suspendPrediction = false;
            _hostMinFloat = ClockPrediction.DayStartMinutes;
            _hostRefTime = 0f;
            _hostAdvancing = false;
            _rateFactor = 1f;
            _loggedTimeLink = false;
            _touchedClientClock = false;
            _lastHudTotalMinute = -1;
        }

        public override void Dispose()
        {
            base.Dispose();
            if (ReferenceEquals(Instance, this))
                Instance = null;
        }

        public override void ForceResend()
        {
            _haveGate = false;
            _clockTimer = ClockSliceSeconds;
            _lightTimer = LightSliceSeconds;
            _forceLight = true;
        }

        // ============================================================ host

        protected override void OnHostTick(in SyncFrame frame)
        {
            if (CoopCore.Role != CoopRole.Host || !CoopCore.InSessionWorld || BroadcastState == null)
                return;

            // Gate transitions are what a predicting client cannot guess: the shop's first
            // opening starts the clock, day end stops it, and a rollover moves the date. Push
            // immediately rather than waiting for the beat so the client's clock starts and
            // stops with the host's instead of up to a slice late.
            Guarded("gate", () =>
            {
                LightManager manager = ResolveManager();
                if (manager == null)
                    return;
                int day = CPlayerData.m_CurrentDay;
                bool onceOpen = CPlayerData.m_IsShopOnceOpen;
                bool ended = ReadBool(LightClockInterop.HasDayEnded, manager);
                int hash = (day * 397) ^ (onceOpen ? 1 : 0) ^ (ended ? 2 : 0);
                if (!_haveGate || hash != _lastGateHash)
                {
                    _haveGate = true;
                    _lastGateHash = hash;
                    BroadcastState(BuildDayTime(manager));
                }
            });

            // Unconditional safety beat: the clock advances every frame, so an occasional
            // dropped or late gate push must still converge. Five scalars make this cheap.
            _clockTimer += frame.Dt;
            if (_clockTimer >= ClockSliceSeconds)
            {
                _clockTimer -= ClockSliceSeconds;
                Guarded("clock-slice", () =>
                {
                    LightManager manager = ResolveManager();
                    if (manager != null)
                        BroadcastState(BuildDayTime(manager));
                });
            }

            // Lighting is event-driven (ObserveHostLightState -> ForceLightResend); this is only
            // the slow repair for a missed group change.
            _lightTimer += frame.Dt;
            if (_lightTimer >= LightSliceSeconds || _forceLight)
            {
                _lightTimer = _forceLight ? 0f : _lightTimer - LightSliceSeconds;
                _forceLight = false;
                Guarded("light-slice", () => BroadcastLightState());
            }
        }

        public override void FullUpdate(Connection connection)
        {
            if (CoopCore.Role != CoopRole.Host || SendToClient == null)
                return;
            int connId = connection.Id;
            Guarded("full", () =>
            {
                LightManager manager = ResolveManager();
                if (manager == null)
                    return;
                SendToClient(connId, BuildDayTime(manager));
                LightStateMessage light = BuildLightState(manager, true);
                if (light != null)
                    SendToClient(connId, light);
            });
        }

        private void BroadcastLightState()
        {
            if (BroadcastState == null)
                return;
            LightManager manager = ResolveManager();
            if (manager == null)
                return;
            LightStateMessage message = BuildLightState(manager, false);
            if (message != null)
                BroadcastState(message);
        }

        private static DayTimeMessage BuildDayTime(LightManager manager)
        {
            int hour = ReadInt(LightClockInterop.TimeHour, manager, 8);
            int minute = ReadInt(LightClockInterop.TimeMin, manager, 0);
            float minuteFloat = ReadFloat(LightClockInterop.TimeMinFloat, manager, minute);
            return new DayTimeMessage
            {
                Day = CPlayerData.m_CurrentDay,
                Hour = hour,
                Minute = minute,
                MinuteFloat = minuteFloat,
                ShopOnceOpen = CPlayerData.m_IsShopOnceOpen,
            };
        }

        private LightStateMessage BuildLightState(LightManager manager, bool force)
        {
            if (CPlayerData.m_LightTimeData == null)
                return null;
            LightClockInterop.UpdateLightData.Invoke(manager, null); // refresh bundle from live state
            string json = JsonUtility.ToJson(CPlayerData.m_LightTimeData);
            _lightHeal += LightSliceSeconds;
            if (!force && json == _lastLightJson && _lightHeal < 60f)
                return null;
            _lastLightJson = json;
            _lightHeal = 0f;
            return new LightStateMessage
            {
                LightJson = json,
                Day = CPlayerData.m_CurrentDay,
                HasDay = true,
            };
        }

        /// <summary>Causes the next host tick to echo the authoritative lighting state. Used
        /// after a forwarded switch request so clients do not wait for the lighting beat.</summary>
        public void ForceLightResend()
        {
            if (CoopCore.Role != CoopRole.Host)
                return;
            _forceLight = true;
            _lightTimer = LightSliceSeconds;
        }

        /// <summary>Called by LightManager hooks after vanilla or a mod refreshed its lighting
        /// data. Direct SetActive changes have no setter to hook, so compare the actual groups
        /// and request an immediate authoritative echo only when they change.</summary>
        public void ObserveHostLightState(LightManager manager)
        {
            if (CoopCore.Role != CoopRole.Host || manager == null)
                return;
            bool shop = manager.m_ShoplightGrp != null && manager.m_ShoplightGrp.activeSelf;
            bool night = manager.m_NightlightGrp != null && manager.m_NightlightGrp.activeSelf;
            bool sunlight = manager.m_SunlightGrp != null && manager.m_SunlightGrp.activeSelf;
            if (!_observedLightState || shop != _observedShopLight
                || night != _observedNightLight || sunlight != _observedSunlight)
            {
                _observedShopLight = shop;
                _observedNightLight = night;
                _observedSunlight = sunlight;
                _observedLightState = true;
                ForceLightResend();
            }
        }

        // ============================================================ client

        /// <summary>LightManager.Update prefix: a client's clock is host-driven. It stays frozen
        /// until the first authoritative sync, then runs at the host rate under the current
        /// correction factor. A host is left untouched.</summary>
        public void OnLightManagerUpdate(LightManager manager)
        {
            if (manager == null)
                return;
            if (CoopCore.Role == CoopRole.Client)
            {
                float speed = (!_clockInit || _suspendPrediction) ? 0f : HostClockSpeed * _rateFactor;
                LightClockInterop.TimerLerpSpeed.SetValue(manager, speed);
                _touchedClientClock = true;
                return;
            }
            // A client promoted to host (handoff) must not keep running at a leftover correction
            // factor; the game's own rate is the constant we predict with.
            if (_touchedClientClock)
            {
                LightClockInterop.TimerLerpSpeed.SetValue(manager, HostClockSpeed);
                _touchedClientClock = false;
            }
        }

        protected override void OnClientTick(in SyncFrame frame)
        {
            if (CoopCore.Role != CoopRole.Client || !frame.InGame)
                return;
            Guarded("predict", () =>
            {
                LightManager manager = ResolveManager();
                if (manager == null)
                    return;
                if (!_clockInit || _suspendPrediction)
                {
                    _rateFactor = 1f;
                    LightClockInterop.TimerLerpSpeed.SetValue(manager, 0f);
                    return;
                }

                int localHour = ReadInt(LightClockInterop.TimeHour, manager, 8);
                float local = ClockPrediction.ToTotalMinutes(localHour,
                    ReadFloat(LightClockInterop.TimeMinFloat, manager, 0f));
                float age = Time.time - _hostRefTime;
                float predicted = ClockPrediction.HostNow(_hostMinFloat, age, _hostAdvancing, HostClockSpeed);
                float error = predicted - local;
                if (ClockPrediction.ShouldSnap(error))
                {
                    ApplyClock(manager, predicted);
                    _rateFactor = 1f;
                }
                else
                {
                    _rateFactor = ClockPrediction.RateFactor(error);
                }
                // Apply here as well as in the prefix: LightManager.Update may already have run
                // this frame, so the correction must not wait for the next one.
                LightClockInterop.TimerLerpSpeed.SetValue(manager, HostClockSpeed * _rateFactor);
                UpdateHudClock(predicted);
            });
        }

        /// <summary>Keeps the co-op HUD clock smooth by writing it from the predicted minute,
        /// once per displayed minute rather than once per sync packet.</summary>
        private void UpdateHudClock(float totalMinutes)
        {
            CoopCore core = CoopCore.Instance;
            if (core == null)
                return;
            int total = (int)totalMinutes;
            if (total == _lastHudTotalMinute)
                return;
            _lastHudTotalMinute = total;
            int hour = total / 60;
            int minute = total % 60;
            core.HostTimeLine = $"Day {CPlayerData.m_CurrentDay + 1}  {hour:00}:{minute:00}";
        }

        /// <summary>Client route: an authoritative clock snapshot. It only refreshes the
        /// prediction reference; the clock is hard-set only for a first baseline or a large
        /// offset. A day change hands off to the mirrored morning reset.</summary>
        public void ClientApplyDayTime(DayTimeMessage message)
        {
            if (CoopCore.Role != CoopRole.Client || message == null)
                return;
            CoopCore core = CoopCore.Instance;
            if (core == null)
                return;

            Guarded("day-apply", () =>
            {
                int day = message.Day;
                int hour = message.Hour;
                int minute = message.Minute;
                float minuteFloat = message.MinuteFloat;
                bool onceOpen = message.ShopOnceOpen;

                if (!_loggedTimeLink)
                {
                    _loggedTimeLink = true;
                    CoopPlugin.Log.LogInfo($"Time link active (Day {day} {hour:00}:{minute:00})");
                }
                core.HostTimeLine = $"Day {day + 1}  {hour:00}:{minute:00}"; // HUD shows day+1

                bool dayChanged = day != CPlayerData.m_CurrentDay;
                CPlayerData.m_CurrentDay = day;
                // The host's clock gate, not a forced true: a client that opened "today" while
                // the host still waited to open shop used to advance through the morning alone.
                CPlayerData.m_IsShopOnceOpen = onceOpen;

                float authoritative = hour * 60f + minuteFloat;
                SetReference(authoritative, onceOpen && hour < 21);

                if (dayChanged)
                    core.MarkClientDayResetPending();

                if (!_suspendPrediction)
                {
                    LightManager manager = ResolveManager();
                    if (manager != null)
                    {
                        if (!_clockInit)
                        {
                            ApplyClock(manager, authoritative);
                        }
                        else
                        {
                            int localHour = ReadInt(LightClockInterop.TimeHour, manager, 8);
                            float local = ClockPrediction.ToTotalMinutes(localHour,
                                ReadFloat(LightClockInterop.TimeMinFloat, manager, 0f));
                            if (ClockPrediction.ShouldSnap(authoritative - local))
                                ApplyClock(manager, authoritative);
                        }
                        if (hour < 21)
                            LightClockInterop.HasDayEnded.SetValue(manager, false);
                    }
                }

                // Starts the mirrored morning reset when a rollover was queued; a no-op otherwise.
                core.TryStartClientDayReset();
            });
        }

        /// <summary>Client route: the complete authoritative lighting snapshot. The clock branch
        /// is prediction-aware - it only hard-applies on a phase change or a large offset, and
        /// otherwise just refreshes the prediction reference.</summary>
        public void ClientApplyLightState(LightStateMessage message)
        {
            if (CoopCore.Role != CoopRole.Client || message == null)
                return;
            CoopCore core = CoopCore.Instance;
            if (core == null)
                return;

            Guarded("light-apply", () =>
            {
                if (string.IsNullOrEmpty(message.LightJson))
                {
                    core.RequestWorldResync();
                    return;
                }
                LightTimeData data;
                try
                {
                    data = JsonUtility.FromJson<LightTimeData>(message.LightJson);
                }
                catch (Exception e)
                {
                    CoopPlugin.Log.LogWarning("light state parse: " + e.Message);
                    core.RequestWorldResync();
                    return;
                }
                if (data == null)
                    return;

                // Reject an older heartbeat queued before a rollover. A newer day is the fallback
                // for a missed DayTime packet and must schedule the same full environment reset.
                if (message.HasDay)
                {
                    if (message.Day < CPlayerData.m_CurrentDay)
                        return;
                    if (message.Day > CPlayerData.m_CurrentDay)
                    {
                        CPlayerData.m_CurrentDay = message.Day;
                        core.MarkClientDayResetPending();
                        core.TryStartClientDayReset();
                        return;
                    }
                }

                LightManager manager = ResolveManager();
                if (manager == null)
                    return;

                int localPhase = ReadInt(LightClockInterop.TimeOfDayIndex, manager, -1);
                int localHour = ReadInt(LightClockInterop.TimeHour, manager, -1);
                int localMinute = ReadInt(LightClockInterop.TimeMin, manager, 0);
                int driftMinutes = Math.Abs((data.m_TimeHour * 60 + data.m_TimeMin)
                    - (localHour * 60 + localMinute));
                bool phaseDiffer = localPhase != data.m_TImeOfDayIndex;

                float authoritative = data.m_TimeHour * 60f + data.m_TimeMinFloat;
                bool advancing = CPlayerData.m_IsShopOnceOpen && !data.m_HasDayEnded && data.m_TimeHour < 21;
                SetReference(authoritative, advancing);

                CPlayerData.m_LightTimeData = data;

                bool snap = !_clockInit || phaseDiffer || driftMinutes > ClockPrediction.SnapMinutes;
                if (snap)
                {
                    LightClockInterop.TimeHour.SetValue(manager, data.m_TimeHour);
                    LightClockInterop.TimeMin.SetValue(manager, data.m_TimeMin);
                    LightClockInterop.TimeMinFloat.SetValue(manager, data.m_TimeMinFloat);
                    LightClockInterop.TimeOfDayIndex.SetValue(manager, data.m_TImeOfDayIndex);
                }

                bool groupsDiffer = manager.m_NightlightGrp == null
                    || manager.m_ShoplightGrp == null
                    || manager.m_SunlightGrp == null
                    || manager.m_NightlightGrp.activeSelf != data.m_IsNightLightOn
                    || manager.m_ShoplightGrp.activeSelf != data.m_IsShopLightOn
                    || manager.m_SunlightGrp.activeSelf != data.m_IsSunlightOn;
                // Apply every authoritative light group, not just the shop-light switch. This
                // also repairs mods that change the groups directly.
                if (groupsDiffer)
                {
                    manager.m_NightlightGrp?.SetActive(data.m_IsNightLightOn);
                    manager.m_ShoplightGrp?.SetActive(data.m_IsShopLightOn);
                    manager.m_SunlightGrp?.SetActive(data.m_IsSunlightOn);
                }
                if (groupsDiffer || phaseDiffer)
                    LightClockInterop.EvaluateWorldUIBrightness.Invoke(manager, null);
                // Mirror the switch meshes whenever the authoritative flag differs from what we
                // last applied - NOT only when the light groups/phase changed. The game's day
                // reset flips the light groups but never touches the InteractableLightSwitch
                // meshes, so a group-only gate could leave the guest's switches stuck on after a
                // rollover. Refreshing them used to cost a full-scene FindObjectsOfType on EVERY
                // LightState apply (~27 ms); the switch array is now cached, so this is a cheap
                // comparison on the common (unchanged) path.
                int switchState = data.m_IsShopLightOn ? 1 : 0;
                if (_appliedSwitchLight != switchState)
                {
                    ApplyClientLightSwitchModels(data.m_IsShopLightOn);
                    _appliedSwitchLight = switchState;
                }
                // Re-run the game's own lighting restore only when the sky phase actually
                // differs (avoids music/blend churn).
                if (phaseDiffer || driftMinutes > 4)
                {
                    LightClockInterop.FinishLoading.SetValue(manager, false);
                    LightClockInterop.LightInit.Invoke(manager, null);
                    CoopPlugin.Log.LogInfo(
                        $"lighting re-synced (phase {localPhase}->{data.m_TImeOfDayIndex}, drift {driftMinutes}min)");
                }
                LightClockInterop.HasDayEnded.SetValue(manager, data.m_HasDayEnded);
                if (snap)
                    _clockInit = true;
                _rateFactor = 1f;
            });
        }

        /// <summary>Client day-reset path: freeze prediction while the mirrored environment
        /// reset is running so it cannot fight <c>DelayUpdateEnv</c>'s 08:00 rewrite.</summary>
        public void SuspendPrediction()
        {
            if (CoopCore.Role != CoopRole.Client)
                return;
            _suspendPrediction = true;
            _rateFactor = 1f;
        }

        /// <summary>Client day-reset path: a reset that timed out (without a scene load to reset
        /// the module) must not leave prediction frozen forever. Clear the suspension without
        /// re-baselining - the next sync re-references the clock.</summary>
        public void ClearSuspension()
        {
            if (CoopCore.Role != CoopRole.Client)
                return;
            _suspendPrediction = false;
            _rateFactor = 1f;
        }

        /// <summary>Client day-reset path: the reset finished and the clock now reads 08:00 of
        /// the new day, so re-baseline the prediction reference there.</summary>
        public void ResumePrediction()
        {
            if (CoopCore.Role != CoopRole.Client)
                return;
            _suspendPrediction = false;
            _clockInit = true;
            _hostMinFloat = ClockPrediction.DayStartMinutes;
            _hostRefTime = Time.time;
            _hostAdvancing = CPlayerData.m_IsShopOnceOpen;
            _rateFactor = 1f;
        }

        // ============================================================ helpers

        private void ApplyClock(LightManager manager, float totalMinutes)
        {
            if (totalMinutes < ClockPrediction.DayStartMinutes)
                totalMinutes = ClockPrediction.DayStartMinutes;
            if (totalMinutes > ClockPrediction.DayEndMinutes)
                totalMinutes = ClockPrediction.DayEndMinutes;

            int total = (int)totalMinutes;
            int hour = total / 60;
            int minute = total % 60;
            float minuteFloat = totalMinutes - hour * 60f;

            int phaseBefore = ReadInt(LightClockInterop.TimeOfDayIndex, manager, -1);
            LightClockInterop.TimeHour.SetValue(manager, hour);
            LightClockInterop.TimeMin.SetValue(manager, minute);
            LightClockInterop.TimeMinFloat.SetValue(manager, minuteFloat);
            LightClockInterop.EvaluateTimeClock.Invoke(manager, null);
            int phaseAfter = ReadInt(LightClockInterop.TimeOfDayIndex, manager, -1);
            if (phaseBefore != phaseAfter)
            {
                LightClockInterop.FinishLoading.SetValue(manager, false);
                LightClockInterop.LightInit.Invoke(manager, null);
            }
            _clockInit = true;
        }

        private void SetReference(float authoritativeMinFloat, bool advancing)
        {
            _hostMinFloat = authoritativeMinFloat;
            _hostRefTime = Time.time;
            _hostAdvancing = advancing;
        }

        private static LightManager ResolveManager()
        {
            CoopCore core = CoopCore.Instance;
            return core == null ? null : core.ResolveLightManager();
        }

        // The switch set only changes on a scene load, which invalidates the cache through
        // Reset(), so it is resolved once per scene instead of once per LightState apply.
        private sealed class LightSwitchCache : Cached<InteractableLightSwitch[]>
        {
            protected override InteractableLightSwitch[] GetRawValue() =>
                UnityEngine.Object.FindObjectsByType<InteractableLightSwitch>(FindObjectsInactive.Include, FindObjectsSortMode.InstanceID);
        }

        private readonly LightSwitchCache _lightSwitches = new LightSwitchCache();

        private void ApplyClientLightSwitchModels(bool isOn)
        {
            try
            {
                var switches = _lightSwitches.Get();
                for (int i = 0; i < switches.Length; i++)
                {
                    var sw = switches[i];
                    if (sw == null)
                        continue;
                    if (sw.m_SwitchOnModel != null)
                        sw.m_SwitchOnModel.SetActive(isOn);
                    if (sw.m_SwitchOffModel != null)
                        sw.m_SwitchOffModel.SetActive(!isOn);
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("light-switch visual apply: " + e.Message); }
        }

        private static int ReadInt(FieldInfo field, LightManager manager, int fallback)
        {
            try
            {
                return field.GetValue(manager) is int value ? value : fallback;
            }
            catch (Exception e) { Swallow.Log(e); return fallback; }
        }

        private static float ReadFloat(FieldInfo field, LightManager manager, float fallback)
        {
            try
            {
                return field.GetValue(manager) is float value ? value : fallback;
            }
            catch (Exception e) { Swallow.Log(e); return fallback; }
        }

        private static bool ReadBool(FieldInfo field, LightManager manager)
        {
            try
            {
                return field.GetValue(manager) is bool value && value;
            }
            catch (Exception e) { Swallow.Log(e); return false; }
        }
    }
}
