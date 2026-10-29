using System;
using Mahjong.P5_4b.B1.HttpsAdmission.Worker;
using Xunit;

namespace Mahjong.P5_4b.B1.HttpsAdmission.Tests
{
    /// <summary>
    /// Pins the phase-local heartbeat gate: cumulative counters from an earlier phase must
    /// never satisfy a later phase, and only NEW PING/PONG frames plus monotonic duration
    /// may make a phase Ready.
    /// </summary>
    public sealed class HeartbeatLivenessGateTests
    {
        [Fact]
        public void ZeroNewHeartbeatsNeverSatisfyAPhase()
        {
            // Baselines already satisfy six PING/PONGs from an earlier gate. Reusing the
            // cumulative values (the original defect) would pass immediately; the phase
            // gate must remain Waiting until six NEW frames arrive.
            HeartbeatLivenessPhase phase = HeartbeatLivenessPhase.Start(
                "five admitted sessions",
                new[] { Sample("0", ready: true, pings: 6, pongs: 6) },
                6,
                TimeSpan.FromSeconds(30),
                DateTimeOffset.UtcNow);

            HeartbeatLivenessResult result = phase.Evaluate(
                new[] { Sample("0", ready: true, pings: 6, pongs: 6) },
                TimeSpan.FromSeconds(60));

            Assert.Equal(HeartbeatLivenessStatus.Waiting, result.Status);
            Assert.False(result.IsReady);
            Assert.Equal(0, result.NewPingsMin);
            Assert.Equal(0, result.NewPongsMin);
        }

        [Fact]
        public void SixNewPingsAndMatchingPongsOverThirtySecondsAreReady()
        {
            HeartbeatLivenessPhase phase = HeartbeatLivenessPhase.Start(
                "five admitted sessions",
                new[] { Sample("0", ready: true, pings: 6, pongs: 6) },
                6,
                TimeSpan.FromSeconds(30),
                DateTimeOffset.UtcNow);

            HeartbeatLivenessResult result = phase.Evaluate(
                new[] { Sample("0", ready: true, pings: 12, pongs: 12) },
                TimeSpan.FromSeconds(30));

            Assert.True(result.IsReady);
            Assert.Equal(6, result.NewPingsMin);
            Assert.Equal(6, result.NewPongsMin);
        }

        [Fact]
        public void SixNewPingsWithOnlyThreeNewPongsAreNotReady()
        {
            HeartbeatLivenessPhase phase = HeartbeatLivenessPhase.Start(
                "post-recovery live sessions",
                new[] { Sample("0", ready: true, pings: 4, pongs: 4) },
                4,
                TimeSpan.FromSeconds(20),
                DateTimeOffset.UtcNow);

            HeartbeatLivenessResult result = phase.Evaluate(
                new[] { Sample("0", ready: true, pings: 10, pongs: 7) },
                TimeSpan.FromSeconds(40));

            Assert.Equal(HeartbeatLivenessStatus.Waiting, result.Status);
            Assert.Equal(6, result.NewPingsMin);
            Assert.Equal(3, result.NewPongsMin);
        }

        [Fact]
        public void OnePrePhasePongIsNotCreditedToAPhasePing()
        {
            // Baseline has one in-flight PING (p2). One of the seven phase-observed
            // PONGs resolves that pre-phase nonce; exactly six belong to the six new
            // phase PINGs. Nonce-level correlation must still report 6/6, not 6/7.
            HeartbeatLivenessPhase phase = HeartbeatLivenessPhase.Start(
                "five admitted sessions",
                new[] { Sample("0", ready: true, pings: 3, pongs: 2) },
                6,
                TimeSpan.FromSeconds(30),
                DateTimeOffset.UtcNow);

            HeartbeatLivenessResult result = phase.Evaluate(
                new[] { Sample("0", ready: true, pings: 9, pongs: 9) },
                TimeSpan.FromSeconds(30));

            Assert.True(result.IsReady);
            Assert.Equal(6, result.NewPingsMin);
            Assert.Equal(6, result.NewPongsMin);
        }

        [Fact]
        public void CountsReachedBeforeTheMinimumSpanAreNotReady()
        {
            HeartbeatLivenessPhase phase = HeartbeatLivenessPhase.Start(
                "five admitted sessions",
                new[] { Sample("0", ready: true, pings: 0, pongs: 0) },
                6,
                TimeSpan.FromSeconds(30),
                DateTimeOffset.UtcNow);

            HeartbeatLivenessResult result = phase.Evaluate(
                new[] { Sample("0", ready: true, pings: 6, pongs: 6) },
                TimeSpan.FromSeconds(10));

            Assert.Equal(HeartbeatLivenessStatus.Waiting, result.Status);
            Assert.Equal(6, result.NewPingsMin);
        }

        [Fact]
        public void AnUnansweredPhasePingTailIsNotReady()
        {
            HeartbeatLivenessPhase phase = HeartbeatLivenessPhase.Start(
                "five admitted sessions",
                new[] { Sample("0", ready: true, pings: 0, pongs: 0) },
                6,
                TimeSpan.FromSeconds(30),
                DateTimeOffset.UtcNow);

            HeartbeatLivenessResult result = phase.Evaluate(
                new[] { Sample("0", ready: true, pings: 9, pongs: 7) },
                TimeSpan.FromSeconds(45));

            Assert.Equal(HeartbeatLivenessStatus.Waiting, result.Status);
        }

        [Fact]
        public void ExactlyOneUnansweredPhasePingIsNotReady()
        {
            // Exact off-by-one boundary: 7 phase PINGs, only 6 matched PONGs. One
            // unanswered tail must keep the phase Waiting even though the count is
            // already above minimumNewPings=6 and the 30 s span is satisfied.
            HeartbeatLivenessPhase phase = HeartbeatLivenessPhase.Start(
                "five admitted sessions",
                new[] { Sample("0", ready: true, pings: 0, pongs: 0) },
                6,
                TimeSpan.FromSeconds(30),
                DateTimeOffset.UtcNow);

            HeartbeatLivenessResult result = phase.Evaluate(
                new[] { Sample("0", ready: true, pings: 7, pongs: 6) },
                TimeSpan.FromSeconds(30));

            Assert.Equal(HeartbeatLivenessStatus.Waiting, result.Status);
            Assert.False(result.IsReady);
            Assert.Equal(7, result.NewPingsMin);
            Assert.Equal(6, result.NewPongsMin);
            Assert.Contains("!1", result.Summary);
        }

        [Fact]
        public void APhaseBindingMismatchIsAViolation()
        {
            HeartbeatLivenessPhase phase = HeartbeatLivenessPhase.Start(
                "five admitted sessions",
                new[] { Sample("0", ready: true, pings: 0, pongs: 0) },
                6,
                TimeSpan.FromSeconds(30),
                DateTimeOffset.UtcNow);

            HeartbeatLivenessResult result = phase.Evaluate(
                new[] { Sample("0", ready: true, pings: 6, pongs: 6, pingMismatch: 1) },
                TimeSpan.FromSeconds(30));

            Assert.Equal(HeartbeatLivenessStatus.Violation, result.Status);
            Assert.Contains("binding/close defect", result.Failure);
        }

        [Fact]
        public void APhaseCloseIsAViolation()
        {
            HeartbeatLivenessPhase phase = HeartbeatLivenessPhase.Start(
                "five admitted sessions",
                new[] { Sample("0", ready: true, pings: 0, pongs: 0) },
                6,
                TimeSpan.FromSeconds(30),
                DateTimeOffset.UtcNow);

            HeartbeatLivenessResult result = phase.Evaluate(
                new[] { Sample("0", ready: true, pings: 6, pongs: 6, closeCalls: 1) },
                TimeSpan.FromSeconds(30));

            Assert.Equal(HeartbeatLivenessStatus.Violation, result.Status);
        }

        [Fact]
        public void LeavingReadyIsAViolation()
        {
            HeartbeatLivenessPhase phase = HeartbeatLivenessPhase.Start(
                "five admitted sessions",
                new[] { Sample("0", ready: true, pings: 0, pongs: 0) },
                6,
                TimeSpan.FromSeconds(30),
                DateTimeOffset.UtcNow);

            HeartbeatLivenessResult result = phase.Evaluate(
                new[] { Sample("0", ready: false, pings: 6, pongs: 6) },
                TimeSpan.FromSeconds(30));

            Assert.Equal(HeartbeatLivenessStatus.Violation, result.Status);
        }

        private static HeartbeatLivenessSample Sample(
            string label,
            bool ready,
            int pings,
            int pongs,
            int pingMismatch = 0,
            int pongMismatch = 0,
            int emptyIdentity = 0,
            int unmatchedPong = 0,
            int closeCalls = 0,
            int inboundClose = 0,
            int transportClose = 0)
        {
            // Deterministic nonce ledger: PING i has nonce "p<i>", and the first
            // `pongs` PONGs match those PINGs in order. This lets the tests pin exact
            // nonce correspondence, including a pre-phase in-flight PING.
            var pingNonces = new string[pings];
            for (int i = 0; i < pings; i++)
            {
                pingNonces[i] = "p" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            var pongNonces = new string[pongs];
            for (int i = 0; i < pongs; i++)
            {
                pongNonces[i] = "p" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return new HeartbeatLivenessSample(
                label,
                ready,
                new HeartbeatAuditCounters(
                    pings,
                    pongs,
                    pingMismatch,
                    pongMismatch,
                    emptyIdentity,
                    unmatchedPong,
                    closeCalls,
                    inboundClose,
                    transportClose,
                    pingNonces,
                    pongNonces));
        }
    }
}
