using System;
using System.Linq;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.Domain.Tests
{
    public class DecisionClockTests
    {
        [Fact]
        public void EachDecisionGrantsThreeSecondsBeforeConsumingReserve()
        {
            var clock = new DecisionClock();
            clock.BeginRound("r1", 0L);
            var first = new DecisionKey("r1", 0, "first");

            DecisionTiming timing = clock.Open(first, 0L);
            Assert.Equal(23000L, timing.DeadlineMillis);
            Assert.Equal(20000L, timing.ReserveAtOpenMillis);

            Assert.True(clock.Complete(first, 5000L));
            Assert.Equal(18000L, clock.RemainingReserve(0));

            DecisionTiming next = clock.Open(new DecisionKey("r1", 0, "next"), 5000L);
            Assert.Equal(26000L, next.DeadlineMillis);
            Assert.True(clock.Complete(new DecisionKey("r1", 0, "next"), 7000L));
            Assert.Equal(18000L, clock.RemainingReserve(0));
            Assert.Equal(20000L, clock.RemainingReserve(1));
        }

        [Fact]
        public void DuplicateOpensAndRoundNotificationsCannotResetADeadline()
        {
            var clock = new DecisionClock();
            clock.BeginRound("r1", 0L);
            var key = new DecisionKey("r1", 0, "choice");
            DecisionTiming opened = clock.Open(key, 0L);

            clock.BeginRound("r1", 12000L);
            Assert.Same(opened, clock.Open(key, 12000L));
            Assert.Throws<InvalidOperationException>(() => clock.Open(new DecisionKey("r1", 0, "another"), 12000L));
            Assert.Same(opened, clock.Timing(0));

            Assert.False(clock.Complete(key, 23000L));
            Assert.Single(clock.Expired(23000L));
            Assert.True(clock.Expire(key, opened.Generation, 23000L));
            Assert.False(clock.Expire(key, opened.Generation, 23000L));
            Assert.Equal(0L, clock.RemainingReserve(0));

            DecisionTiming after = clock.Open(new DecisionKey("r1", 0, "after-timeout"), 23000L);
            Assert.Equal(26000L, after.DeadlineMillis);
        }

        [Fact]
        public void ExpiredQueryIsReadOnlyAndGenerationMismatchHasNoSideEffect()
        {
            var clock = new DecisionClock();
            clock.BeginRound("r1", 0L);
            var key = new DecisionKey("r1", 0, "turn");
            DecisionTiming timing = clock.Open(key, 0L);

            Assert.Empty(clock.Expired(22999L));
            Assert.Single(clock.Expired(23000L));
            Assert.NotNull(clock.Timing(0));
            Assert.Equal(20000L, clock.RemainingReserve(0));

            Assert.False(clock.Expire(key, timing.Generation + 1L, 23000L));
            Assert.NotNull(clock.Timing(0));
            Assert.Equal(20000L, clock.RemainingReserve(0));
        }

        [Fact]
        public void CancelSpendsElapsedReserveAndOldTimerCannotExpireNewDecision()
        {
            var clock = new DecisionClock();
            clock.BeginRound("r1", 0L);
            var oldKey = new DecisionKey("r1", 1, "old");
            DecisionTiming old = clock.Open(oldKey, 0L);

            Assert.True(clock.Cancel(oldKey, 5000L));
            Assert.Equal(18000L, clock.RemainingReserve(1));

            DecisionTiming current = clock.Open(new DecisionKey("r1", 1, "new"), 5000L);
            Assert.False(clock.Expire(oldKey, old.Generation, 5000L));
            Assert.Same(current, clock.Timing(1));
            Assert.Equal(18000L, clock.RemainingReserve(1));
        }

        [Fact]
        public void RoundAndSessionReserveScopesDifferOnlyAtFreshDeal()
        {
            foreach (ReserveScope scope in new[] { ReserveScope.Round, ReserveScope.Session })
            {
                var clock = new DecisionClock(new DecisionClockConfig(reserveScope: scope));
                clock.BeginRound("r1", 0L);
                var key = new DecisionKey("r1", 2, "turn");
                clock.Open(key, 0L);
                Assert.True(clock.Complete(key, 13000L));
                Assert.Equal(10000L, clock.RemainingReserve(2));

                clock.BeginRound("r2", 13000L);

                Assert.Equal(scope == ReserveScope.Round ? 20000L : 10000L, clock.RemainingReserve(2));
                Assert.False(clock.Complete(key, 14000L));
            }
        }

        [Fact]
        public void SimultaneousResponseSeatsHaveIndependentDeadlinesAndBalances()
        {
            var clock = new DecisionClock();
            clock.BeginRound("r1", 0L);
            var one = new DecisionKey("r1", 1, "window-1");
            var two = new DecisionKey("r1", 2, "window-2");
            clock.Open(one, 0L);
            clock.Open(two, 0L);

            Assert.True(clock.Complete(one, 8000L));
            Assert.Equal(15000L, clock.RemainingReserve(1));
            Assert.Equal(15000L, clock.RemainingReserve(1));
            Assert.Equal(0L, clock.RemainingReserve(1) - 15000L);

            DecisionTiming[] expired = clock.Expired(23000L).ToArray();
            Assert.Single(expired);
            Assert.Equal(two, expired[0].Key);
        }

        [Fact]
        public void EarlyOrPausedTimeoutCallbacksDoNotConsumeReserve()
        {
            var clock = new DecisionClock();
            clock.BeginRound("r1", 0L);
            var key = new DecisionKey("r1", 0, "turn");
            DecisionTiming timing = clock.Open(key, 0L);

            Assert.False(clock.Expire(key, timing.Generation, 2000L));
            Assert.Equal(20000L, clock.RemainingReserve(0));
            Assert.NotNull(clock.Timing(0));

            clock.Pause(5000L);
            Assert.False(clock.Expire(key, timing.Generation, 50000L));
            Assert.False(clock.Complete(key, 50000L));
            Assert.Empty(clock.Expired(50000L));
            Assert.Equal(20000L, clock.RemainingReserve(0));
        }

        [Fact]
        public void OldRoundCannotOpenAgainstNewBudget()
        {
            var clock = new DecisionClock();
            clock.BeginRound("new-round", 0L);

            Assert.Throws<InvalidOperationException>(
                () => clock.Open(new DecisionKey("old-round", 0, "late"), 0L));
            Assert.Null(clock.Timing(0));
            Assert.Equal(20000L, clock.RemainingReserve(0));
        }

        [Fact]
        public void PauseFreezesTimeAndResumeDoesNotAwardNewFreeTime()
        {
            var clock = new DecisionClock();
            clock.BeginRound("r1", 0L);
            var key = new DecisionKey("r1", 0, "turn");
            clock.Open(key, 0L);

            clock.Pause(5000L);
            Assert.Empty(clock.Expired(50000L));
            Assert.False(clock.Expire(key, clock.Timing(0)!.Generation, 50000L));
            Assert.False(clock.Complete(key, 50000L));

            clock.Resume(50000L);
            Assert.Equal(68000L, clock.Timing(0)!.DeadlineMillis);
            Assert.True(clock.Complete(key, 52000L));
            Assert.Equal(16000L, clock.RemainingReserve(0));
        }

        [Fact]
        public void SessionBankSettlesUnfinishedDecisionBeforeAnotherDeal()
        {
            var clock = new DecisionClock(new DecisionClockConfig(reserveScope: ReserveScope.Session));
            clock.BeginRound("r1", 0L);
            clock.Open(new DecisionKey("r1", 0, "turn"), 0L);

            clock.BeginRound("r2", 8000L);

            Assert.Equal(15000L, clock.RemainingReserve(0));
            Assert.Null(clock.Timing(0));
            Assert.Equal("r2", clock.ActiveRoundId);
        }

        [Fact]
        public void RekeyPreservesDeadlineReserveAndGeneration()
        {
            var clock = new DecisionClock();
            clock.BeginRound("r1", 0L);
            var oldKey = new DecisionKey("r1", 0, "old");
            DecisionTiming old = clock.Open(oldKey, 0L);
            var newKey = new DecisionKey("r1", 0, "new");

            Assert.True(clock.Rekey(oldKey, newKey));

            DecisionTiming? current = clock.Timing(0);
            Assert.NotNull(current);
            Assert.Equal(newKey, current!.Key);
            Assert.Equal(old.DeadlineMillis, current.DeadlineMillis);
            Assert.Equal(old.Generation, current.Generation);
            Assert.Equal(old.ReserveAtOpenMillis, current.ReserveAtOpenMillis);
            Assert.False(clock.Complete(oldKey, 1000L));
            Assert.True(clock.Complete(newKey, 1000L));
            Assert.Equal(20000L, clock.RemainingReserve(0));
        }

        [Fact]
        public void OverflowingBudgetIsRejectedWithoutOpening()
        {
            Assert.Throws<ArgumentException>(() =>
                new DecisionClockConfig(baseMillis: long.MaxValue, reserveMillis: 1L));

            var clock = new DecisionClock(new DecisionClockConfig(baseMillis: long.MaxValue - 30L, reserveMillis: 20L));
            clock.BeginRound("r1", 0L);
            Assert.Throws<OverflowException>(() => clock.Open(new DecisionKey("r1", 0, "overflow"), 100L));
            Assert.Null(clock.Timing(0));
        }

        [Fact]
        public void ResetKeepsGenerationMonotonic()
        {
            var clock = new DecisionClock();
            clock.BeginRound("r1", 0L);
            DecisionTiming first = clock.Open(new DecisionKey("r1", 0, "a"), 0L);
            clock.Reset();
            clock.BeginRound("r2", 10L);

            DecisionTiming second = clock.Open(new DecisionKey("r2", 0, "b"), 10L);

            Assert.True(second.Generation > first.Generation);
        }

        [Fact]
        public void InvalidSeatIsRejected()
        {
            var clock = new DecisionClock();
            clock.BeginRound("r1", 0L);
            Assert.Throws<ArgumentOutOfRangeException>(() => clock.RemainingReserve(4));
            Assert.Throws<ArgumentOutOfRangeException>(() => clock.Timing(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new DecisionKey("r1", 4, "x"));
        }
    }

    public class DeadlinePolicyTests
    {
        private static PendingDecision Decision(
            int seat,
            string decisionId = "d",
            TileId? drawn = null,
            params LegalActionOption[] candidates)
        {
            return new PendingDecision(
                new DecisionKey("r1", seat, decisionId),
                0L,
                candidates,
                drawn);
        }

        private static LegalActionOption Pass(int seat) => new LegalActionOption(LegalActionKind.Pass, seat);

        private static LegalActionOption Ron(int seat) => new LegalActionOption(LegalActionKind.Ron, seat);

        private static LegalActionOption Tsumo(int seat) => new LegalActionOption(LegalActionKind.Tsumo, seat);

        private static LegalActionOption Discard(int seat, int tile) =>
            new LegalActionOption(LegalActionKind.Discard, seat, new TileId(tile));

        [Fact]
        public void TimeoutPolicyPrefersPassOverRon()
        {
            PendingDecision decision = Decision(0, "response", null, Ron(0), Pass(0));

            LegalActionOption? chosen = TimeoutPolicy.Choose(decision);

            Assert.NotNull(chosen);
            Assert.Equal(LegalActionKind.Pass, chosen!.Kind);
        }

        [Fact]
        public void TimeoutPolicyPrefersTsumogiriThenLowestLegalDiscard()
        {
            var drawn = new TileId(40);
            PendingDecision decision = Decision(
                0,
                "turn",
                drawn,
                Tsumo(0),
                Discard(0, 60),
                Discard(0, 40),
                Discard(0, 12));

            LegalActionOption chosen = TimeoutPolicy.Choose(decision)!;
            Assert.Equal(LegalActionKind.Discard, chosen.Kind);
            Assert.Equal(40, chosen.Tile!.Value.Value);

            PendingDecision noDrawn = Decision(0, "turn2", null, Discard(0, 60), Discard(0, 12));
            Assert.Equal(12, TimeoutPolicy.Choose(noDrawn)!.Tile!.Value.Value);
        }

        [Fact]
        public void TimeoutPolicyNeverChoosesAWinningOrKanAction()
        {
            PendingDecision decision = Decision(0, "win", null, Tsumo(0), Ron(0), new LegalActionOption(LegalActionKind.Kan, 0));
            Assert.Null(TimeoutPolicy.Choose(decision));

            PendingDecision empty = Decision(0, "empty");
            Assert.Null(TimeoutPolicy.Choose(empty));
        }

        [Fact]
        public void SameTickAiLosesToTheAuthoritativeDeadline()
        {
            var clock = new DecisionClock();
            clock.BeginRound("r1", 0L);
            var control = new ControlRegistry();
            var key = new DecisionKey("r1", 1, "d");
            clock.Open(key, 0L);
            var pending = new PendingDecision(
                key,
                0L,
                new[] { Pass(1), Ron(1) });
            var ai = new AiDecisionSubmission(key, 0L, Ron(1), 23000L);
            var arbiter = new DeadlineArbiter(clock, control);

            TickResult result = arbiter.ProcessTick(new TickInput(
                "r1",
                23000L,
                new[] { pending },
                new[] { ai }));

            Assert.Equal(2, result.Entries.Count);
            Assert.Equal(TickEntryKind.Timeout, result.Entries[0].Kind);
            Assert.Equal(LegalActionKind.Pass, result.Entries[0].Action!.Kind);
            Assert.Equal(TickEntryKind.AiRejected, result.Entries[1].Kind);
            Assert.Equal(TickRejectionCode.DeadlineExpired, result.Entries[1].RejectionCode);
            Assert.Null(clock.Timing(1));
            Assert.Equal(0L, clock.RemainingReserve(1));
            Assert.Empty(clock.Expired(23000L));
        }

        [Fact]
        public void AiBeforeItsDeadlineIsAcceptedAndCompletesTheDecision()
        {
            var clock = new DecisionClock();
            clock.BeginRound("r1", 1000L);
            var control = new ControlRegistry();
            var key = new DecisionKey("r1", 1, "d");
            clock.Open(key, 1000L);
            var action = Discard(1, 40);
            var pending = new PendingDecision(key, 0L, new[] { Tsumo(1), action }, new TileId(40));
            var ai = new AiDecisionSubmission(key, 0L, action, 5000L);
            var arbiter = new DeadlineArbiter(clock, control);

            TickResult result = arbiter.ProcessTick(new TickInput(
                "r1",
                5000L,
                new[] { pending },
                new[] { ai }));

            Assert.Single(result.Entries);
            Assert.Equal(TickEntryKind.AiAccepted, result.Entries[0].Kind);
            Assert.Null(clock.Timing(1));
            Assert.Equal(19000L, clock.RemainingReserve(1));
        }

        [Fact]
        public void StaleControlEpochRejectsAiWithoutClosingTheDecision()
        {
            var clock = new DecisionClock();
            clock.BeginRound("r1", 0L);
            var control = new ControlRegistry();
            var key = new DecisionKey("r1", 1, "d");
            clock.Open(key, 0L);
            var pending = new PendingDecision(key, 0L, new[] { Pass(1) });
            control.SetController(1, SeatControllerKind.Computer);
            var arbiter = new DeadlineArbiter(clock, control);

            TickResult result = arbiter.ProcessTick(new TickInput(
                "r1",
                1000L,
                new[] { pending },
                new[] { new AiDecisionSubmission(key, 0L, Pass(1), 1000L) }));

            Assert.Equal(TickEntryKind.AiRejected, result.Entries[0].Kind);
            Assert.Equal(TickRejectionCode.StaleEpoch, result.Entries[0].RejectionCode);
            Assert.NotNull(clock.Timing(1));
            Assert.Equal(20000L, clock.RemainingReserve(1));
        }

        [Fact]
        public void HumanSeatCannotSubmitAnAutomatedAction()
        {
            var clock = new DecisionClock();
            clock.BeginRound("r1", 0L);
            var control = new ControlRegistry();
            var key = new DecisionKey("r1", 0, "d");
            clock.Open(key, 0L);
            var pending = new PendingDecision(key, 0L, new[] { Pass(0) });
            var arbiter = new DeadlineArbiter(clock, control);

            TickResult result = arbiter.ProcessTick(new TickInput(
                "r1",
                1000L,
                new[] { pending },
                new[] { new AiDecisionSubmission(key, 0L, Pass(0), 1000L) }));

            Assert.Equal(TickRejectionCode.SeatNotAutomated, result.Entries[0].RejectionCode);
            Assert.NotNull(clock.Timing(0));
        }

        [Fact]
        public void IllegalAiActionIsRejectedWithoutClosingTheDecision()
        {
            var clock = new DecisionClock();
            clock.BeginRound("r1", 0L);
            var control = new ControlRegistry();
            var key = new DecisionKey("r1", 1, "d");
            clock.Open(key, 0L);
            var pending = new PendingDecision(key, 0L, new[] { Pass(1) });
            var arbiter = new DeadlineArbiter(clock, control);

            TickResult result = arbiter.ProcessTick(new TickInput(
                "r1",
                1000L,
                new[] { pending },
                new[] { new AiDecisionSubmission(key, 0L, Ron(1), 1000L) }));

            Assert.Equal(TickRejectionCode.IllegalAction, result.Entries[0].RejectionCode);
            Assert.NotNull(clock.Timing(1));
            Assert.Equal(20000L, clock.RemainingReserve(1));
        }

        [Fact]
        public void TimeoutOfAnEarlierSeatIsReportedBeforeALaterAiAcceptance()
        {
            var clock = new DecisionClock();
            clock.BeginRound("r1", 0L);
            var control = new ControlRegistry();
            var seat0 = new DecisionKey("r1", 0, "human");
            var seat1 = new DecisionKey("r1", 1, "ai");
            clock.Open(seat0, 0L);
            clock.Open(seat1, 10000L);
            var pending0 = new PendingDecision(seat0, 0L, new[] { Pass(0), Ron(0) });
            var pending1 = new PendingDecision(seat1, 0L, new[] { Pass(1) });
            var arbiter = new DeadlineArbiter(clock, control);

            TickResult result = arbiter.ProcessTick(new TickInput(
                "r1",
                23000L,
                new[] { pending0, pending1 },
                new[] { new AiDecisionSubmission(seat1, 0L, Pass(1), 23000L) }));

            Assert.Equal(2, result.Entries.Count);
            Assert.Equal(TickEntryKind.Timeout, result.Entries[0].Kind);
            Assert.Equal(0, result.Entries[0].Seat);
            Assert.Equal(TickEntryKind.AiAccepted, result.Entries[1].Kind);
            Assert.Equal(1, result.Entries[1].Seat);
        }

        [Fact]
        public void PausedClockNeitherExpiresNorAcceptsAi()
        {
            var clock = new DecisionClock();
            clock.BeginRound("r1", 0L);
            var control = new ControlRegistry();
            var key = new DecisionKey("r1", 1, "d");
            clock.Open(key, 0L);
            clock.Pause(5000L);
            var pending = new PendingDecision(key, 0L, new[] { Pass(1) });
            var arbiter = new DeadlineArbiter(clock, control);

            TickResult result = arbiter.ProcessTick(new TickInput(
                "r1",
                23000L,
                new[] { pending },
                new[] { new AiDecisionSubmission(key, 0L, Pass(1), 23000L) }));

            Assert.Single(result.Entries);
            Assert.Equal(TickRejectionCode.ClockPaused, result.Entries[0].RejectionCode);
            Assert.NotNull(clock.Timing(1));
            Assert.Equal(20000L, clock.RemainingReserve(1));
        }

        [Fact]
        public void ExpiredDecisionWithoutAPendingDefaultStillExpiresWithAnExplicitGap()
        {
            var clock = new DecisionClock();
            clock.BeginRound("r1", 0L);
            var control = new ControlRegistry();
            var key = new DecisionKey("r1", 0, "d");
            clock.Open(key, 0L);
            var arbiter = new DeadlineArbiter(clock, control);

            TickResult result = arbiter.ProcessTick(new TickInput("r1", 23000L));

            Assert.Single(result.Entries);
            Assert.Equal(TickEntryKind.Timeout, result.Entries[0].Kind);
            Assert.Equal(TickRejectionCode.NoDefaultAction, result.Entries[0].RejectionCode);
            Assert.Null(result.Entries[0].Action);
            Assert.Null(clock.Timing(0));
            Assert.Equal(0L, clock.RemainingReserve(0));
        }

        [Fact]
        public void SubmissionForAnotherRoundIsRejected()
        {
            var clock = new DecisionClock();
            clock.BeginRound("r1", 0L);
            var control = new ControlRegistry();
            var key = new DecisionKey("r1", 1, "d");
            clock.Open(key, 0L);
            var pending = new PendingDecision(key, 0L, new[] { Pass(1) });
            var stale = new AiDecisionSubmission(
                new DecisionKey("old", 1, "d"),
                0L,
                Pass(1),
                1000L);
            var arbiter = new DeadlineArbiter(clock, control);

            TickResult result = arbiter.ProcessTick(new TickInput(
                "r1",
                1000L,
                new[] { pending },
                new[] { stale }));

            Assert.Equal(TickRejectionCode.StaleRound, result.Entries[0].RejectionCode);
            Assert.NotNull(clock.Timing(1));
        }
    }
}
