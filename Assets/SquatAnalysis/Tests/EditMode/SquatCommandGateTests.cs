using NUnit.Framework;

namespace MotionStudio.SquatAnalysis.Tests
{
    public sealed class SquatCommandGateTests
    {
        [TestCase("start")]
        [TestCase("pause")]
        [TestCase("reset")]
        public void AcceptsKnownCommandsOnce(string command)
        {
            var gate = new SquatCommandGate("current");
            Assert.That(gate.Evaluate("current", "request", command), Is.EqualTo(SquatCommandDecision.Apply));
            Assert.That(gate.Evaluate("current", "request", command), Is.EqualTo(SquatCommandDecision.Duplicate));
        }

        [Test]
        public void RejectsPreviousUnityPlaySession()
        {
            var gate = new SquatCommandGate("current");
            Assert.That(gate.Evaluate("old", "request", "reset"), Is.EqualTo(SquatCommandDecision.WrongSession));
            Assert.That(gate.Evaluate("current", "request", "reset"), Is.EqualTo(SquatCommandDecision.Apply));
        }

        [Test]
        public void RetryAfterAnotherCommandDoesNotReapplyStart()
        {
            var gate = new SquatCommandGate("current");
            gate.Evaluate("current", "start-id", "start");
            gate.Evaluate("current", "pause-id", "pause");
            Assert.That(gate.Evaluate("current", "start-id", "start"), Is.EqualTo(SquatCommandDecision.Duplicate));
        }

        [Test]
        public void CannotReuseCommandIdForDifferentAction()
        {
            var gate = new SquatCommandGate("current");
            gate.Evaluate("current", "request", "start");
            Assert.That(gate.Evaluate("current", "request", "reset"), Is.EqualTo(SquatCommandDecision.Invalid));
        }

        [TestCase("")]
        [TestCase("delete")]
        [TestCase(null)]
        public void RejectsUnknownCommands(string command)
        {
            var gate = new SquatCommandGate("current");
            Assert.That(gate.Evaluate("current", "request", command), Is.EqualTo(SquatCommandDecision.Invalid));
        }

        [Test]
        public void RejectsMissingOrOversizedId()
        {
            var gate = new SquatCommandGate("current");
            Assert.That(gate.Evaluate("current", "", "start"), Is.EqualTo(SquatCommandDecision.Invalid));
            Assert.That(gate.Evaluate("current", new string('x', 65), "start"), Is.EqualTo(SquatCommandDecision.Invalid));
        }
    }
}
