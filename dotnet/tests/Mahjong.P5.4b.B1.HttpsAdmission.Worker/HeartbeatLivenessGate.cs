using System;
using System.Collections.Generic;
using System.Linq;

namespace Mahjong.P5_4b.B1.HttpsAdmission.Worker
{
    /// <summary>Immutable, atomically captured copy of the heartbeat audit counters.</summary>
    public readonly struct HeartbeatAuditCounters
    {
        public HeartbeatAuditCounters(
            int serverPingCount,
            int matchedPongCount,
            int pingBindingMismatchCount,
            int pongBindingMismatchCount,
            int emptyIdentityPingCount,
            int unmatchedPongCount,
            int closeCallCount,
            int inboundCloseEnvelopeCount,
            int transportCloseCount,
            IReadOnlyList<string>? serverPingNonces = null,
            IReadOnlyList<string>? matchedPongNonces = null)
        {
            ServerPingCount = serverPingCount;
            MatchedPongCount = matchedPongCount;
            PingBindingMismatchCount = pingBindingMismatchCount;
            PongBindingMismatchCount = pongBindingMismatchCount;
            EmptyIdentityPingCount = emptyIdentityPingCount;
            UnmatchedPongCount = unmatchedPongCount;
            CloseCallCount = closeCallCount;
            InboundCloseEnvelopeCount = inboundCloseEnvelopeCount;
            TransportCloseCount = transportCloseCount;
            ServerPingNonces = serverPingNonces ?? Array.Empty<string>();
            MatchedPongNonces = matchedPongNonces ?? Array.Empty<string>();
        }

        public int ServerPingCount { get; }

        public int MatchedPongCount { get; }

        public int PingBindingMismatchCount { get; }

        public int PongBindingMismatchCount { get; }

        public int EmptyIdentityPingCount { get; }

        public int UnmatchedPongCount { get; }

        public int CloseCallCount { get; }

        public int InboundCloseEnvelopeCount { get; }

        public int TransportCloseCount { get; }

        /// <summary>Ordered server PING nonces; count matches <see cref="ServerPingCount"/>.</summary>
        public IReadOnlyList<string> ServerPingNonces { get; }

        /// <summary>Ordered matched PONG nonces; count matches <see cref="MatchedPongCount"/>.</summary>
        public IReadOnlyList<string> MatchedPongNonces { get; }

        public static HeartbeatAuditCounters Capture(HeartbeatBindingAuditChannel audit)
        {
            if (audit == null)
            {
                throw new ArgumentNullException(nameof(audit));
            }

            return audit.CaptureCounters();
        }
    }

    /// <summary>One client's state at a sampled instant, including cumulative counters.</summary>
    public sealed class HeartbeatLivenessSample
    {
        public HeartbeatLivenessSample(
            string clientLabel,
            bool ready,
            HeartbeatAuditCounters counters)
        {
            ClientLabel = clientLabel ?? throw new ArgumentNullException(nameof(clientLabel));
            Ready = ready;
            Counters = counters;
        }

        public string ClientLabel { get; }

        public bool Ready { get; }

        public HeartbeatAuditCounters Counters { get; }
    }

    public enum HeartbeatLivenessStatus
    {
        Ready,
        Waiting,
        Violation
    }

    public sealed class HeartbeatLivenessResult
    {
        internal HeartbeatLivenessResult(
            HeartbeatLivenessStatus status,
            string failure,
            string summary,
            DateTimeOffset startedAtUtc,
            TimeSpan elapsed,
            int newPingsMin,
            int newPongsMin)
        {
            Status = status;
            Failure = failure;
            Summary = summary;
            StartedAtUtc = startedAtUtc;
            Elapsed = elapsed;
            NewPingsMin = newPingsMin;
            NewPongsMin = newPongsMin;
        }

        public HeartbeatLivenessStatus Status { get; }

        public bool IsReady => Status == HeartbeatLivenessStatus.Ready;

        public bool IsViolation => Status == HeartbeatLivenessStatus.Violation;

        public string Failure { get; }

        public string Summary { get; }

        public DateTimeOffset StartedAtUtc { get; }

        public TimeSpan Elapsed { get; }

        public int NewPingsMin { get; }

        public int NewPongsMin { get; }
    }

    /// <summary>
    /// A phase-local heartbeat liveness gate.
    ///
    /// The counters exposed by <see cref="HeartbeatBindingAuditChannel"/> are cumulative from
    /// OpenAsync. Evaluating them directly makes a later gate pass immediately because the
    /// earlier gate already satisfied the count. This type captures per-client baselines at
    /// phase start and evaluates only frames observed inside the phase, together with a
    /// monotonic elapsed duration. Zero new PINGs can therefore never satisfy the phase.
    /// </summary>
    public sealed class HeartbeatLivenessPhase
    {
        private readonly Dictionary<string, HeartbeatAuditCounters> _baselines;
        private readonly string _context;
        private readonly int _minimumNewPings;
        private readonly TimeSpan _minimumElapsed;
        private readonly DateTimeOffset _startedAtUtc;

        private HeartbeatLivenessPhase(
            string context,
            Dictionary<string, HeartbeatAuditCounters> baselines,
            int minimumNewPings,
            TimeSpan minimumElapsed,
            DateTimeOffset startedAtUtc)
        {
            _context = context;
            _baselines = baselines;
            _minimumNewPings = minimumNewPings;
            _minimumElapsed = minimumElapsed;
            _startedAtUtc = startedAtUtc;
        }

        public static HeartbeatLivenessPhase Start(
            string context,
            IReadOnlyList<HeartbeatLivenessSample> initial,
            int minimumNewPings,
            TimeSpan minimumElapsed,
            DateTimeOffset startedAtUtc)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (initial == null || initial.Count == 0)
            {
                throw new ArgumentException("A heartbeat phase needs at least one client.", nameof(initial));
            }

            if (minimumNewPings <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumNewPings));
            }

            if (minimumElapsed < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumElapsed));
            }

            var baselines = new Dictionary<string, HeartbeatAuditCounters>(StringComparer.Ordinal);
            foreach (HeartbeatLivenessSample sample in initial)
            {
                if (!sample.Ready)
                {
                    throw new InvalidOperationException(
                        context + ": client " + sample.ClientLabel + " is not Ready at phase start.");
                }

                HeartbeatAuditCounters counters = sample.Counters;
                if (counters.ServerPingCount != counters.ServerPingNonces.Count ||
                    counters.MatchedPongCount != counters.MatchedPongNonces.Count)
                {
                    throw new InvalidOperationException(
                        context + ": client " + sample.ClientLabel +
                        " produced an inconsistent heartbeat snapshot at phase start.");
                }

                if (counters.PingBindingMismatchCount != 0 ||
                    counters.PongBindingMismatchCount != 0 ||
                    counters.EmptyIdentityPingCount != 0 ||
                    counters.UnmatchedPongCount != 0 ||
                    counters.CloseCallCount != 0 ||
                    counters.InboundCloseEnvelopeCount != 0 ||
                    counters.TransportCloseCount != 0)
                {
                    throw new InvalidOperationException(
                        context + ": client " + sample.ClientLabel +
                        " already has a heartbeat/close defect at phase start.");
                }

                if (baselines.ContainsKey(sample.ClientLabel))
                {
                    throw new ArgumentException(
                        "Duplicate heartbeat client label: " + sample.ClientLabel, nameof(initial));
                }

                baselines.Add(sample.ClientLabel, counters);
            }

            return new HeartbeatLivenessPhase(
                context,
                baselines,
                minimumNewPings,
                minimumElapsed,
                startedAtUtc);
        }

        public HeartbeatLivenessResult Evaluate(
            IReadOnlyList<HeartbeatLivenessSample> samples,
            TimeSpan elapsed)
        {
            if (samples == null || samples.Count != _baselines.Count)
            {
                return Violation("client sample set changed during the heartbeat phase", elapsed, 0, 0);
            }

            int newPingsMin = int.MaxValue;
            int newPongsMin = int.MaxValue;
            bool allMatched = true;
            var parts = new List<string>(samples.Count);
            foreach (HeartbeatLivenessSample sample in samples)
            {
                if (!_baselines.TryGetValue(sample.ClientLabel, out HeartbeatAuditCounters baseline))
                {
                    return Violation(
                        "unknown client '" + sample.ClientLabel + "' during the heartbeat phase",
                        elapsed,
                        newPingsMin,
                        newPongsMin);
                }

                if (!sample.Ready)
                {
                    return Violation(
                        "client " + sample.ClientLabel + " left Ready during the heartbeat phase",
                        elapsed,
                        newPingsMin,
                        newPongsMin);
                }

                HeartbeatAuditCounters counters = sample.Counters;
                if (counters.PingBindingMismatchCount != baseline.PingBindingMismatchCount ||
                    counters.PongBindingMismatchCount != baseline.PongBindingMismatchCount ||
                    counters.EmptyIdentityPingCount != baseline.EmptyIdentityPingCount ||
                    counters.UnmatchedPongCount != baseline.UnmatchedPongCount ||
                    counters.CloseCallCount != baseline.CloseCallCount ||
                    counters.InboundCloseEnvelopeCount != baseline.InboundCloseEnvelopeCount ||
                    counters.TransportCloseCount != baseline.TransportCloseCount)
                {
                    return Violation(
                        "client " + sample.ClientLabel +
                        " recorded a binding/close defect during the heartbeat phase",
                        elapsed,
                        newPingsMin,
                        newPongsMin);
                }

                if (counters.ServerPingCount != counters.ServerPingNonces.Count ||
                    counters.MatchedPongCount != counters.MatchedPongNonces.Count)
                {
                    return Violation(
                        "client " + sample.ClientLabel + " produced an inconsistent heartbeat snapshot",
                        elapsed,
                        newPingsMin,
                        newPongsMin);
                }

                if (counters.ServerPingNonces.Count < baseline.ServerPingNonces.Count ||
                    counters.MatchedPongNonces.Count < baseline.MatchedPongNonces.Count)
                {
                    return Violation(
                        "client " + sample.ClientLabel + " heartbeat nonce ledger regressed",
                        elapsed,
                        newPingsMin,
                        newPongsMin);
                }

                for (int i = 0; i < baseline.ServerPingNonces.Count; i++)
                {
                    if (!string.Equals(counters.ServerPingNonces[i], baseline.ServerPingNonces[i], StringComparison.Ordinal))
                    {
                        return Violation(
                            "client " + sample.ClientLabel + " PING nonce ledger changed during the phase",
                            elapsed,
                            newPingsMin,
                            newPongsMin);
                    }
                }

                for (int i = 0; i < baseline.MatchedPongNonces.Count; i++)
                {
                    if (!string.Equals(counters.MatchedPongNonces[i], baseline.MatchedPongNonces[i], StringComparison.Ordinal))
                    {
                        return Violation(
                            "client " + sample.ClientLabel + " PONG nonce ledger changed during the phase",
                            elapsed,
                            newPingsMin,
                            newPongsMin);
                    }
                }

                // Phase PINGs are exactly the append-only suffix after the baseline.
                // A PONG for a PING that was already in flight at phase start is not in
                // this suffix, so it cannot be credited to a phase PING. Conversely, a
                // phase PING without its OWN nonce in the matched-PONG ledger is an
                // unanswered tail and makes the phase NotReady.
                var phasePingNonces = new List<string>(
                    counters.ServerPingNonces.Skip(baseline.ServerPingNonces.Count));
                var matchedPongNonces = new HashSet<string>(
                    counters.MatchedPongNonces,
                    StringComparer.Ordinal);
                int matchedPongs = 0;
                int unansweredPings = 0;
                foreach (string nonce in phasePingNonces)
                {
                    if (matchedPongNonces.Contains(nonce))
                    {
                        matchedPongs++;
                    }
                    else
                    {
                        unansweredPings++;
                    }
                }

                if (unansweredPings > 0)
                {
                    // EXACT boundary: one phase PING without its own PONG is one too many.
                    allMatched = false;
                }

                newPingsMin = Math.Min(newPingsMin, phasePingNonces.Count);
                newPongsMin = Math.Min(newPongsMin, matchedPongs);
                parts.Add(
                    sample.ClientLabel + ":" + phasePingNonces.Count + "/" + matchedPongs +
                    (unansweredPings == 0 ? string.Empty : "!" + unansweredPings));
            }

            string summary = string.Join(",", parts);
            if (newPingsMin < _minimumNewPings || newPongsMin < _minimumNewPings)
            {
                return Waiting("not enough phase-local PING/PONG frames", elapsed, newPingsMin, newPongsMin, summary);
            }

            if (!allMatched)
            {
                return Waiting("a phase PING is still awaiting its PONG", elapsed, newPingsMin, newPongsMin, summary);
            }

            if (elapsed < _minimumElapsed)
            {
                return Waiting("phase duration below the required monotonic span", elapsed, newPingsMin, newPongsMin, summary);
            }

            return new HeartbeatLivenessResult(
                HeartbeatLivenessStatus.Ready,
                string.Empty,
                summary,
                _startedAtUtc,
                elapsed,
                newPingsMin,
                newPongsMin);
        }

        private HeartbeatLivenessResult Violation(
            string failure,
            TimeSpan elapsed,
            int newPingsMin,
            int newPongsMin)
        {
            return new HeartbeatLivenessResult(
                HeartbeatLivenessStatus.Violation,
                _context + ": " + failure,
                string.Empty,
                _startedAtUtc,
                elapsed,
                newPingsMin == int.MaxValue ? 0 : newPingsMin,
                newPongsMin == int.MaxValue ? 0 : newPongsMin);
        }

        private HeartbeatLivenessResult Waiting(
            string reason,
            TimeSpan elapsed,
            int newPingsMin,
            int newPongsMin,
            string summary)
        {
            return new HeartbeatLivenessResult(
                HeartbeatLivenessStatus.Waiting,
                _context + ": " + reason,
                summary,
                _startedAtUtc,
                elapsed,
                newPingsMin == int.MaxValue ? 0 : newPingsMin,
                newPongsMin == int.MaxValue ? 0 : newPongsMin);
        }
    }
}
