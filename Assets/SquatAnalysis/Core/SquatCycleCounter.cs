using System;

namespace MotionStudio.SquatAnalysis
{
    public enum SquatPhase { WaitingForStanding, Standing, Descending, Bottom, Ascending, TrackingLost, Paused }

    public sealed class SquatCounterSettings
    {
        public float StandingFlexion = 15f;
        public float StartFlexion = 25f;
        public float MinimumDepthFlexion = 45f;
        public float BottomFlexion = 65f;
        public float DirectionHysteresis = 4f;
        public double StandingConfirmSeconds = 0.25;
        public double StartConfirmSeconds = 0.10;
        public double DepthConfirmSeconds = 0.10;
        public double MaximumSampleGapSeconds = 0.50;

        internal SquatCounterSettings Copy()
        {
            return (SquatCounterSettings)MemberwiseClone();
        }

        internal void Validate()
        {
            if (!Finite(StandingFlexion) || !Finite(StartFlexion) ||
                !Finite(MinimumDepthFlexion) || !Finite(BottomFlexion) ||
                !Finite(DirectionHysteresis) || StandingFlexion < 0 ||
                StandingFlexion >= StartFlexion || StartFlexion >= MinimumDepthFlexion ||
                MinimumDepthFlexion > BottomFlexion || BottomFlexion > 180 ||
                DirectionHysteresis <= 0 || !Positive(StandingConfirmSeconds) ||
                !Positive(StartConfirmSeconds) || !Positive(DepthConfirmSeconds) ||
                !Positive(MaximumSampleGapSeconds))
                throw new ArgumentException("Squat thresholds and confirmation times are inconsistent.");
        }

        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static bool Positive(double value) { return Finite(value) && value > 0; }
    }

    // Uses capture time, not rendering time. Lost or interrupted cycles are discarded.
    public sealed class SquatCycleCounter
    {
        private readonly SquatCounterSettings settings;
        private double lastTime = double.NaN;
        private double standingSince = double.NaN;
        private double startingSince = double.NaN;
        private double depthSince = double.NaN;
        private bool cycleActive, depthReached, paused;
        private float peakFlexion;

        public int CompletedRepetitions { get; private set; }
        public SquatPhase Phase { get; private set; }
        public bool HasActiveCycle { get { return cycleActive; } }

        public SquatCycleCounter(SquatCounterSettings settings = null)
        {
            settings = settings ?? new SquatCounterSettings();
            settings.Validate();
            this.settings = settings.Copy();
            Reset();
        }

        public void Reset() { CompletedRepetitions = 0; Resume(); }
        public void Resume()
        {
            paused = false;
            ClearCycle();
            lastTime = double.NaN;
            Phase = SquatPhase.WaitingForStanding;
        }
        public void Pause()
        {
            ClearCycle();
            lastTime = double.NaN;
            paused = true;
            Phase = SquatPhase.Paused;
        }
        public void LoseTracking()
        {
            ClearCycle();
            lastTime = double.NaN;
            Phase = paused ? SquatPhase.Paused : SquatPhase.TrackingLost;
        }

        public bool ProcessSample(double captureTime, float flexion, bool valid = true)
        {
            return ProcessSample(captureTime, flexion, flexion, valid);
        }

        public bool ProcessSample(double captureTime, float leftFlexion, float rightFlexion, bool valid = true)
        {
            bool anglesValid = ValidAngle(leftFlexion) && ValidAngle(rightFlexion);
            return ProcessMeasurement(captureTime, Math.Min(leftFlexion, rightFlexion),
                Math.Max(leftFlexion, rightFlexion) <= settings.StandingFlexion, valid && anglesValid);
        }

        private bool ProcessMeasurement(double captureTime, float flexion, bool standing, bool valid)
        {
            if (paused) return false;
            if (!valid || double.IsNaN(captureTime) || double.IsInfinity(captureTime) ||
                captureTime < 0 || float.IsNaN(flexion) || float.IsInfinity(flexion) ||
                flexion < 0 || flexion > 180)
            {
                LoseTracking();
                return false;
            }
            if (!double.IsNaN(lastTime))
            {
                if (captureTime == lastTime) return false;
                if (captureTime < lastTime)
                {
                    ClearCycle();
                    Phase = SquatPhase.WaitingForStanding;
                    lastTime = captureTime;
                    return false;
                }
                if (captureTime - lastTime > settings.MaximumSampleGapSeconds)
                {
                    ClearCycle();
                    Phase = SquatPhase.WaitingForStanding;
                }
            }
            lastTime = captureTime;
            if (Phase == SquatPhase.TrackingLost) Phase = SquatPhase.WaitingForStanding;

            if (Phase == SquatPhase.WaitingForStanding)
            {
                if (Held(standing, captureTime,
                         ref standingSince, settings.StandingConfirmSeconds))
                {
                    ClearCycle();
                    Phase = SquatPhase.Standing;
                }
                return false;
            }
            if (!cycleActive)
            {
                if (!Held(flexion >= settings.StartFlexion, captureTime,
                          ref startingSince, settings.StartConfirmSeconds)) return false;
                cycleActive = true;
                peakFlexion = flexion;
                standingSince = double.NaN;
            }

            peakFlexion = Math.Max(peakFlexion, flexion);
            if (!depthReached)
                depthReached = Held(flexion >= settings.MinimumDepthFlexion, captureTime,
                                    ref depthSince, settings.DepthConfirmSeconds);

            if (Held(standing, captureTime,
                     ref standingSince, settings.StandingConfirmSeconds))
            {
                bool completed = depthReached;
                if (completed) CompletedRepetitions++;
                ClearCycle();
                Phase = SquatPhase.Standing;
                return completed;
            }

            if (peakFlexion - flexion >= settings.DirectionHysteresis)
                Phase = SquatPhase.Ascending;
            else if (flexion >= settings.BottomFlexion)
                Phase = SquatPhase.Bottom;
            else
                Phase = SquatPhase.Descending;
            return false;
        }

        private static bool ValidAngle(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0 && value <= 180;
        }

        private static bool Held(bool condition, double now, ref double since, double seconds)
        {
            if (!condition) { since = double.NaN; return false; }
            if (double.IsNaN(since)) since = now;
            return now - since >= seconds;
        }

        private void ClearCycle()
        {
            cycleActive = false;
            depthReached = false;
            peakFlexion = 0;
            standingSince = startingSince = depthSince = double.NaN;
        }
    }
}
