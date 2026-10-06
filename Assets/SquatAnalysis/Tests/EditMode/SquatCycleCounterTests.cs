using System;
using NUnit.Framework;

namespace MotionStudio.SquatAnalysis.Tests
{
    public sealed class SquatCycleCounterTests
    {
        private SquatCycleCounter counter;
        private double now;

        [SetUp]
        public void SetUp() { counter = new SquatCycleCounter(); now = 1; }

        private void Hold(float angle, double seconds) { Hold(angle, angle, seconds); }
        private void Hold(float left, float right, double seconds)
        {
            int steps = (int)Math.Ceiling(seconds / 0.05);
            for (int i = 0; i <= steps; i++)
            {
                counter.ProcessSample(now, left, right);
                now += 0.05;
            }
        }
        private void Stand() { Hold(5, 0.4); }
        private void Down() { Hold(35, 0.2); Hold(80, 0.3); }
        private void Complete() { Down(); Hold(45, 0.2); Stand(); }

        [Test]
        public void RequiresStandingConfirmationBeforeCounting()
        {
            counter.ProcessSample(now, 5);
            Assert.That(counter.Phase, Is.EqualTo(SquatPhase.WaitingForStanding));
            Stand();
            Assert.That(counter.Phase, Is.EqualTo(SquatPhase.Standing));
        }

        [Test]
        public void FiveCompleteCyclesCountFive()
        {
            Stand();
            for (int i = 0; i < 5; i++) Complete();
            Assert.That(counter.CompletedRepetitions, Is.EqualTo(5));
            Assert.That(counter.Phase, Is.EqualTo(SquatPhase.Standing));
        }

        [Test]
        public void HoldingBottomDoesNotCountUntilStanding()
        {
            Stand(); Down(); Hold(80, 5);
            Assert.That(counter.Phase, Is.EqualTo(SquatPhase.Bottom));
            Assert.That(counter.CompletedRepetitions, Is.Zero);
            Stand(); Stand();
            Assert.That(counter.CompletedRepetitions, Is.EqualTo(1));
        }

        [Test]
        public void ReportsDescendingBottomAndAscending()
        {
            Stand(); Hold(35, 0.2);
            Assert.That(counter.Phase, Is.EqualTo(SquatPhase.Descending));
            Hold(80, 0.3);
            Assert.That(counter.Phase, Is.EqualTo(SquatPhase.Bottom));
            Hold(45, 0.2);
            Assert.That(counter.Phase, Is.EqualTo(SquatPhase.Ascending));
        }

        [Test]
        public void ShallowCycleDoesNotCount()
        {
            Stand(); Hold(35, 0.5); Stand();
            Assert.That(counter.CompletedRepetitions, Is.Zero);
            Assert.That(counter.Phase, Is.EqualTo(SquatPhase.Standing));
        }

        [Test]
        public void DepthMustBeHeldAcrossCaptureFrames()
        {
            Stand(); Hold(35, 0.2);
            counter.ProcessSample(now, 80); now += 0.05;
            Hold(35, 0.2); Stand();
            Assert.That(counter.CompletedRepetitions, Is.Zero);
        }

        [Test]
        public void StartingMidSquatDoesNotCountPartialCycle()
        {
            Down(); Stand();
            Assert.That(counter.CompletedRepetitions, Is.Zero);
            Complete();
            Assert.That(counter.CompletedRepetitions, Is.EqualTo(1));
        }

        [Test]
        public void LostTrackingDiscardsUnfinishedCycle()
        {
            Stand(); Complete(); Down();
            counter.ProcessSample(now, 80, false); now += 0.05;
            Assert.That(counter.Phase, Is.EqualTo(SquatPhase.TrackingLost));
            Hold(80, 0.2); Stand();
            Assert.That(counter.CompletedRepetitions, Is.EqualTo(1));
            Complete();
            Assert.That(counter.CompletedRepetitions, Is.EqualTo(2));
        }

        [Test]
        public void PauseAndResumePreserveCompletedCountButDiscardPartial()
        {
            Stand(); Complete(); Down(); counter.Pause();
            Stand();
            Assert.That(counter.Phase, Is.EqualTo(SquatPhase.Paused));
            counter.Resume(); Stand();
            Assert.That(counter.CompletedRepetitions, Is.EqualTo(1));
            Complete();
            Assert.That(counter.CompletedRepetitions, Is.EqualTo(2));
        }

        [Test]
        public void ResetClearsCountAndRequiresStandingAgain()
        {
            Stand(); Complete(); counter.Reset();
            Assert.That(counter.CompletedRepetitions, Is.Zero);
            Assert.That(counter.Phase, Is.EqualTo(SquatPhase.WaitingForStanding));
        }

        [Test]
        public void LongCaptureGapDiscardsUnfinishedCycle()
        {
            Stand(); Down(); now += 1;
            Stand();
            Assert.That(counter.CompletedRepetitions, Is.Zero);
            Complete();
            Assert.That(counter.CompletedRepetitions, Is.EqualTo(1));
        }

        [Test]
        public void RewoundTimestampDiscardsUnfinishedCycle()
        {
            Stand(); Down(); now = 0.1;
            counter.ProcessSample(now, 80); now += 0.05; Stand();
            Assert.That(counter.CompletedRepetitions, Is.Zero);
            Complete();
            Assert.That(counter.CompletedRepetitions, Is.EqualTo(1));
        }

        [Test]
        public void DuplicateTimestampsDoNotAdvanceConfirmation()
        {
            for (int i = 0; i < 100; i++) counter.ProcessSample(now, 5);
            Assert.That(counter.Phase, Is.EqualTo(SquatPhase.WaitingForStanding));
            Stand(); Down();
            for (int i = 0; i < 100; i++) counter.ProcessSample(now, 5);
            Assert.That(counter.CompletedRepetitions, Is.Zero);
            now += 0.05; Stand();
            Assert.That(counter.CompletedRepetitions, Is.EqualTo(1));
        }

        [Test]
        public void BothKneesMustReachDepth()
        {
            Stand(); Hold(80, 10, 0.6); Stand();
            Assert.That(counter.CompletedRepetitions, Is.Zero);
        }

        [Test]
        public void BothKneesMustReturnToStanding()
        {
            Stand(); Down(); Hold(5, 80, 0.6);
            Assert.That(counter.CompletedRepetitions, Is.Zero);
            Stand();
            Assert.That(counter.CompletedRepetitions, Is.EqualTo(1));
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(-1f)]
        [TestCase(181f)]
        public void InvalidAngleDiscardsCycle(float angle)
        {
            Stand(); Down(); counter.ProcessSample(now, 80, angle); now += 0.05;
            Assert.That(counter.Phase, Is.EqualTo(SquatPhase.TrackingLost));
            Stand();
            Assert.That(counter.CompletedRepetitions, Is.Zero);
        }

        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(-1d)]
        public void InvalidCaptureTimeDiscardsCycle(double time)
        {
            Stand(); Down(); counter.ProcessSample(time, 80);
            Assert.That(counter.Phase, Is.EqualTo(SquatPhase.TrackingLost));
            Stand();
            Assert.That(counter.CompletedRepetitions, Is.Zero);
        }

        [Test]
        public void InconsistentSettingsAreRejected()
        {
            Assert.Throws<ArgumentException>(() => new SquatCycleCounter(
                new SquatCounterSettings { MinimumDepthFlexion = 20 }));
            Assert.Throws<ArgumentException>(() => new SquatCycleCounter(
                new SquatCounterSettings { MaximumSampleGapSeconds = double.NaN }));
        }
    }
}
