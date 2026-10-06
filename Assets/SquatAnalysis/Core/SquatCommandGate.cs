using System;
using System.Collections.Generic;

namespace MotionStudio.SquatAnalysis
{
    public enum SquatCommandDecision { Apply, Duplicate, Invalid, WrongSession }

    // Commands belong to one Unity Play session. Retries must not restart a cycle.
    public sealed class SquatCommandGate
    {
        private readonly string session;
        private readonly Dictionary<string, string> seen = new Dictionary<string, string>();
        private readonly Queue<string> order = new Queue<string>();
        public SquatCommandGate(string session)
        {
            if (string.IsNullOrEmpty(session)) throw new ArgumentException("A session is required.");
            this.session = session;
        }

        public SquatCommandDecision Evaluate(string requestSession, string id, string command)
        {
            if (requestSession != session) return SquatCommandDecision.WrongSession;
            if (string.IsNullOrEmpty(id) || id.Length > 64 ||
                (command != "start" && command != "pause" && command != "reset"))
                return SquatCommandDecision.Invalid;
            string previous;
            if (seen.TryGetValue(id, out previous))
                return previous == command ? SquatCommandDecision.Duplicate : SquatCommandDecision.Invalid;
            seen.Add(id, command);
            order.Enqueue(id);
            if (order.Count > 64) seen.Remove(order.Dequeue());
            return SquatCommandDecision.Apply;
        }
    }
}
