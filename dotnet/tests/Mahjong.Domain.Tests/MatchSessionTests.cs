using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.Domain.Tests
{
    public class MatchSessionTests
    {
        private static readonly PersistedUtcTime Utc = new PersistedUtcTime(P13.FixedUtc);

        private static MatchSession Start(
            RoundStateMachine round,
            MatchRuleConfig? config = null,
            long? sessionVersion = null)
        {
            MatchSession session = MatchSession.Create(
                "session-" + round.RoundId,
                config,
                round.Scores,
                round.DealerSeat,
                round.RoundWind,
                round.RoundNumber,
                round.HonbaSticks,
                round.RiichiSticks);
            MatchTransition started = session.StartSession(round);
            Assert.True(started.IsAccepted, started.Error?.ToString());
            return started.State!;
        }

        private static MatchSession FinishTsumo(MatchSession session, long[] finalScores, int remainingSticks = 0)
        {
            RoundStateMachine round = session.ActiveRound!;
            RoundSettlement settlement = P13.Settlement(
                round,
                "res-" + round.RoundId,
                RoundOutcome.Tsumo(round.DealerSeat),
                finalScores,
                remainingSticks);
            RoundTransition ended = round.Execute(RoundCommand.Tsumo(round.RoundId, round.DealerSeat, settlement));
            Assert.True(ended.IsAccepted, ended.Error?.ToString());

            MatchTransition accepted = session.AcceptRoundResult(ended.State!, Utc, 2000L);
            Assert.True(accepted.IsAccepted, accepted.Error?.ToString());
            return accepted.State!;
        }

        [Fact]
        public void DealerTsumoRepeatsDealerAndIncrementsHonba()
        {
            RoundStateMachine round = P13.Dealt("m-dealer");
            MatchSession session = Start(round);

            MatchSession next = FinishTsumo(session, new long[] { 33000, 22333, 22334, 22333 });

            Assert.Equal(SessionLifecycle.WaitingReady, next.Lifecycle);
            Assert.Equal(0, next.CurrentDealer);
            Assert.Equal(0, next.CurrentRoundNumber);
            Assert.Equal(1, next.CurrentHonba);
            Assert.Equal(new long[] { 33000, 22333, 22334, 22333 }, next.BetweenRoundScores);
            Assert.Single(next.RoundRecords);
            Assert.Equal(round.RoundId, next.RoundRecords[0].RoundId);
            Assert.Equal(SessionLifecycle.RoundActive, session.Lifecycle);
        }

        [Fact]
        public void NewRoundHasNewIdAndOldRoundStaysFrozen()
        {
            RoundStateMachine first = P13.Dealt("m-new-a");
            MatchSession session = Start(first);
            MatchSession waiting = FinishTsumo(session, new long[] { 33000, 22333, 22334, 22333 });

            RoundStateMachine second = P13.Unstarted(
                "m-new-b",
                waiting.CurrentDealer);
            // Unstarted uses default 25000 scores; align them with the settled match.
            var spec = new RoundSpec(
                "m-new-b",
                waiting.CurrentDealer,
                waiting.CurrentRoundWind,
                waiting.CurrentRoundNumber,
                waiting.CurrentHonba,
                waiting.BetweenRoundScores,
                waiting.BetweenRoundRiichiSticks);
            second = RoundStateMachine.CreateUnstarted(
                spec,
                TilePositionMap.CreateWall(RecordedRandomDraw.Standard("m-new-b-wall")));

            MatchTransition started = waiting.StartNextRound(second);

            Assert.True(started.IsAccepted, started.Error?.ToString());
            MatchSession next = started.State!;
            Assert.Equal(SessionLifecycle.RoundActive, next.Lifecycle);
            Assert.Equal("m-new-b", next.ActiveRound!.RoundId);
            Assert.NotEqual(first.RoundId, next.ActiveRound.RoundId);
            Assert.Equal(0L, next.ActiveRound.Version);
            Assert.Equal(1, next.CurrentHonba);
            Assert.Equal(3L, next.Version);
            Assert.Equal(0, first.Version);
            Assert.Equal(RoundPhase.CheatWindow, first.Phase);
            Assert.Single(waiting.RoundRecords);
        }

        [Fact]
        public void ReusingACompletedRoundIdIsRejected()
        {
            RoundStateMachine first = P13.Dealt("m-reuse-a");
            MatchSession session = Start(first);
            MatchSession waiting = FinishTsumo(session, new long[] { 33000, 22333, 22334, 22333 });

            RoundStateMachine reused = P13.Unstarted(
                "m-reuse-a",
                waiting.CurrentDealer);
            var spec = new RoundSpec(
                "m-reuse-a",
                waiting.CurrentDealer,
                waiting.CurrentRoundWind,
                waiting.CurrentRoundNumber,
                waiting.CurrentHonba,
                waiting.BetweenRoundScores,
                waiting.BetweenRoundRiichiSticks);
            reused = RoundStateMachine.CreateUnstarted(
                spec,
                TilePositionMap.CreateWall(RecordedRandomDraw.Standard("m-reuse-wall")));

            MatchTransition rejected = waiting.StartNextRound(reused);

            Assert.False(rejected.IsAccepted);
            Assert.Equal(MatchTransitionErrorCode.RoundIdReuse, rejected.Error!.Code);
            Assert.Equal(SessionLifecycle.WaitingReady, waiting.Lifecycle);
            Assert.Single(waiting.RoundRecords);
        }

        [Fact]
        public void NextRoundWithMismatchedScoresIsRejected()
        {
            RoundStateMachine first = P13.Dealt("m-mismatch-a");
            MatchSession session = Start(first);
            MatchSession waiting = FinishTsumo(session, new long[] { 33000, 22333, 22334, 22333 });

            var spec = new RoundSpec(
                "m-mismatch-b",
                waiting.CurrentDealer,
                waiting.CurrentRoundWind,
                waiting.CurrentRoundNumber,
                waiting.CurrentHonba,
                new long[] { 25000, 25000, 25000, 25000 },
                0);
            RoundStateMachine wrong = RoundStateMachine.CreateUnstarted(
                spec,
                TilePositionMap.CreateWall(RecordedRandomDraw.Standard("m-mismatch-wall")));

            MatchTransition rejected = waiting.StartNextRound(wrong);

            Assert.False(rejected.IsAccepted);
            Assert.Equal(MatchTransitionErrorCode.RoundStateMismatch, rejected.Error!.Code);
            Assert.Equal(SessionLifecycle.WaitingReady, waiting.Lifecycle);
        }

        [Fact]
        public void NonDealerWinRotatesDealerAndClearsHonba()
        {
            RoundStateMachine round = P13.Dealt("m-nondealer", honba: 2);
            MatchSession session = Start(round);
            RoundSettlement settlement = P13.Settlement(
                round,
                "res-nondealer",
                RoundOutcome.Ron(1, 0),
                new long[] { 22000, 33000, 22500, 22500 });
            RoundTransition opened = round.Execute(RoundCommand.Discard(
                round.RoundId,
                0,
                P13.FirstHandTile(round, 0),
                new[] { 1 }));
            Assert.True(opened.IsAccepted, opened.Error?.ToString());
            RoundTransition ended = opened.State!.Execute(RoundCommand.Ron(round.RoundId, 1, settlement));
            Assert.True(ended.IsAccepted, ended.Error?.ToString());

            MatchTransition accepted = session.AcceptRoundResult(ended.State!, Utc, 3000L);

            Assert.True(accepted.IsAccepted, accepted.Error?.ToString());
            MatchSession next = accepted.State!;
            Assert.Equal(1, next.CurrentDealer);
            Assert.Equal(1, next.CurrentRoundNumber);
            Assert.Equal(0, next.CurrentHonba);
            Assert.Equal(SessionLifecycle.WaitingReady, next.Lifecycle);
        }

        [Fact]
        public void ExhaustiveDrawHonbaAndDealerRotationFollowTenpai()
        {
            RoundStateMachine continuingRound = P13.NoWallDealt("m-draw-tenpai");
            MatchSession continuingSession = Start(continuingRound);
            RoundTransition endedContinuing = continuingRound.Execute(RoundCommand.ExhaustiveDraw(
                continuingRound.RoundId,
                P13.Settlement(
                    continuingRound,
                    "res-draw-continuing",
                    RoundOutcome.ExhaustiveDraw(new[] { 0, 2 }),
                    new long[] { 25000, 25000, 25000, 25000 })));
            Assert.True(endedContinuing.IsAccepted, endedContinuing.Error?.ToString());
            MatchSession continuing = continuingSession.AcceptRoundResult(endedContinuing.State!, Utc, 4000L).State!;
            Assert.Equal(0, continuing.CurrentDealer);
            Assert.Equal(0, continuing.CurrentRoundNumber);
            Assert.Equal(1, continuing.CurrentHonba);
            Assert.Equal(SessionLifecycle.WaitingReady, continuing.Lifecycle);

            RoundStateMachine rotatingRound = P13.NoWallDealt("m-draw-rotate");
            MatchSession rotatingSession = Start(rotatingRound);
            RoundTransition endedRotating = rotatingRound.Execute(RoundCommand.ExhaustiveDraw(
                rotatingRound.RoundId,
                P13.Settlement(
                    rotatingRound,
                    "res-draw-rotating",
                    RoundOutcome.ExhaustiveDraw(new[] { 1, 2 }),
                    new long[] { 25000, 25000, 25000, 25000 })));
            Assert.True(endedRotating.IsAccepted, endedRotating.Error?.ToString());
            MatchSession rotating = rotatingSession.AcceptRoundResult(endedRotating.State!, Utc, 5000L).State!;
            Assert.Equal(1, rotating.CurrentDealer);
            Assert.Equal(1, rotating.CurrentRoundNumber);
            Assert.Equal(1, rotating.CurrentHonba);
        }

        [Fact]
        public void AbortiveDrawRepeatsDealerAndIncrementsHonba()
        {
            RoundStateMachine round = P13.Dealt("m-abortive");
            MatchSession session = Start(round);
            RoundTransition ended = round.Execute(RoundCommand.AbortiveDraw(
                round.RoundId,
                P13.Settlement(
                    round,
                    "res-abortive",
                    RoundOutcome.AbortiveDraw("FOUR_WINDS"),
                    new long[] { 25000, 25000, 25000, 25000 })));
            Assert.True(ended.IsAccepted, ended.Error?.ToString());

            MatchSession next = session.AcceptRoundResult(ended.State!, Utc, 6000L).State!;

            Assert.Equal(0, next.CurrentDealer);
            Assert.Equal(0, next.CurrentRoundNumber);
            Assert.Equal(1, next.CurrentHonba);
            Assert.Equal(SessionLifecycle.WaitingReady, next.Lifecycle);
        }

        [Fact]
        public void FinalDealerCanChooseAgariYame()
        {
            RoundStateMachine round = P13.Dealt(
                "m-agari-yame",
                wind: 2,
                number: 3,
                scores: new long[] { 35000, 25000, 20000, 20000 },
                sticks: 2);
            MatchSession session = Start(round);
            RoundSettlement settlement = P13.Settlement(
                round,
                "res-agari-yame",
                RoundOutcome.Tsumo(0),
                new long[] { 35000, 25000, 20000, 20000 },
                remainingSticks: 2);
            RoundTransition ended = round.Execute(RoundCommand.Tsumo(round.RoundId, 0, settlement));
            Assert.True(ended.IsAccepted, ended.Error?.ToString());

            MatchTransition accepted = session.AcceptRoundResult(ended.State!, Utc, 7000L);
            Assert.True(accepted.IsAccepted, accepted.Error?.ToString());
            MatchSession choice = accepted.State!;
            Assert.Equal(SessionLifecycle.WaitingDealerChoice, choice.Lifecycle);
            Assert.True(choice.CanDealerDeclareAgariYame);
            Assert.Equal(2, choice.BetweenRoundRiichiSticks);

            MatchTransition finished = choice.ChooseDealerEnd(true, Utc, 8000L);

            Assert.True(finished.IsAccepted, finished.Error?.ToString());
            Assert.Equal(SessionLifecycle.Finished, finished.State!.Lifecycle);
            Assert.Equal(MatchFinishReason.AgariYame, finished.Result!.Reason);
            Assert.Equal(new long[] { 37000, 25000, 20000, 20000 }, finished.Result.FinalScores);
            Assert.Equal(0, finished.Result.RemainingRiichiSticksTransferredTo);
        }

        [Fact]
        public void DecliningAgariYamePreparesTheNextRound()
        {
            RoundStateMachine round = P13.Dealt(
                "m-agari-no",
                wind: 2,
                number: 3,
                scores: new long[] { 35000, 25000, 20000, 20000 });
            MatchSession session = Start(round);
            RoundSettlement settlement = P13.Settlement(
                round,
                "res-agari-no",
                RoundOutcome.Tsumo(0),
                new long[] { 35000, 25000, 20000, 20000 });
            RoundTransition ended = round.Execute(RoundCommand.Tsumo(round.RoundId, 0, settlement));
            Assert.True(ended.IsAccepted, ended.Error?.ToString());
            MatchSession choice = session.AcceptRoundResult(ended.State!, Utc, 9000L).State!;
            Assert.Equal(SessionLifecycle.WaitingDealerChoice, choice.Lifecycle);

            MatchTransition waiting = choice.ChooseDealerEnd(false, Utc, 10000L);

            Assert.True(waiting.IsAccepted, waiting.Error?.ToString());
            Assert.Equal(SessionLifecycle.WaitingReady, waiting.State!.Lifecycle);
            Assert.Equal(0, waiting.State.CurrentDealer);
            Assert.Equal(2, waiting.State.CurrentRoundWind);
            Assert.Equal(3, waiting.State.CurrentRoundNumber);
            Assert.Equal(1, waiting.State.CurrentHonba);
            Assert.False(waiting.State.CanDealerDeclareAgariYame);
        }

        [Fact]
        public void BankruptcyFinishesBeforeNormalProgression()
        {
            RoundStateMachine round = P13.Dealt("m-bankrupt");
            MatchSession session = Start(round);
            RoundSettlement settlement = P13.Settlement(
                round,
                "res-bankrupt",
                RoundOutcome.Ron(1, 0),
                new long[] { -100, 50000, 25100, 25000 });
            RoundTransition opened = round.Execute(RoundCommand.Discard(
                round.RoundId,
                0,
                P13.FirstHandTile(round, 0),
                new[] { 1 }));
            Assert.True(opened.IsAccepted, opened.Error?.ToString());
            RoundTransition ended = opened.State!.Execute(RoundCommand.Ron(round.RoundId, 1, settlement));
            Assert.True(ended.IsAccepted, ended.Error?.ToString());

            MatchTransition accepted = session.AcceptRoundResult(ended.State!, Utc, 11000L);

            Assert.True(accepted.IsAccepted, accepted.Error?.ToString());
            Assert.Equal(SessionLifecycle.Finished, accepted.State!.Lifecycle);
            Assert.Equal(MatchFinishReason.Bankruptcy, accepted.Result!.Reason);
            Assert.Equal(new long[] { -100, 50000, 25100, 25000 }, accepted.Result.FinalScores);
        }

        [Fact]
        public void RiichiStickTransferHappensExactlyOnce()
        {
            RoundStateMachine round = P13.Dealt(
                "m-sticks",
                scores: new long[] { 24000, 25000, 25000, 26000 },
                sticks: 1);
            MatchSession session = Start(round);
            RoundSettlement settlement = P13.Settlement(
                round,
                "res-sticks",
                RoundOutcome.Tsumo(0),
                new long[] { 34000, 22334, 22333, 22333 },
                remainingSticks: 0);
            RoundTransition ended = round.Execute(RoundCommand.Tsumo(round.RoundId, 0, settlement));
            Assert.True(ended.IsAccepted, ended.Error?.ToString());

            MatchTransition accepted = session.AcceptRoundResult(ended.State!, Utc, 12000L);

            Assert.True(accepted.IsAccepted, accepted.Error?.ToString());
            Assert.Equal(0, accepted.State!.BetweenRoundRiichiSticks);
            Assert.Equal(34000, accepted.State.BetweenRoundScores[0]);
            Assert.Equal(101000, accepted.State.BetweenRoundScores.Sum());
            Assert.Equal(0, ended.Result!.RemainingRiichiSticks);
        }

        [Fact]
        public void AcceptingAnUnfinishedRoundIsRejected()
        {
            RoundStateMachine round = P13.Dealt("m-unfinished");
            MatchSession session = Start(round);
            long version = session.Version;

            MatchTransition rejected = session.AcceptRoundResult(Utc, 13000L);

            Assert.False(rejected.IsAccepted);
            Assert.Equal(MatchTransitionErrorCode.ActiveRoundNotFinished, rejected.Error!.Code);
            Assert.Equal(version, session.Version);
            Assert.Equal(SessionLifecycle.RoundActive, session.Lifecycle);
            Assert.Empty(session.RoundRecords);
        }

        [Fact]
        public void AResultCanOnlyBeAcceptedOnce()
        {
            RoundStateMachine round = P13.Dealt("m-once");
            MatchSession session = Start(round);
            RoundSettlement settlement = P13.Tsumo(round);
            RoundTransition ended = round.Execute(RoundCommand.Tsumo(round.RoundId, 0, settlement));
            Assert.True(ended.IsAccepted, ended.Error?.ToString());
            MatchTransition first = session.AcceptRoundResult(ended.State!, Utc, 14000L);
            Assert.True(first.IsAccepted);

            MatchTransition second = first.State!.AcceptRoundResult(Utc, 15000L);

            Assert.False(second.IsAccepted);
            Assert.Equal(MatchTransitionErrorCode.NoActiveRound, second.Error!.Code);
            Assert.Single(first.State.RoundRecords);
        }

        [Fact]
        public void FinishedSessionRejectsFurtherRounds()
        {
            RoundStateMachine round = P13.Dealt("m-finished");
            MatchSession session = Start(round);
            RoundSettlement settlement = P13.Settlement(
                round,
                "res-finished",
                RoundOutcome.Ron(1, 0),
                new long[] { -100, 50000, 25100, 25000 });
            RoundTransition opened = round.Execute(RoundCommand.Discard(
                round.RoundId,
                0,
                P13.FirstHandTile(round, 0),
                new[] { 1 }));
            Assert.True(opened.IsAccepted, opened.Error?.ToString());
            RoundTransition ended = opened.State!.Execute(RoundCommand.Ron(round.RoundId, 1, settlement));
            Assert.True(ended.IsAccepted, ended.Error?.ToString());
            MatchSession finished = session.AcceptRoundResult(ended.State!, Utc, 16000L).State!;
            Assert.Equal(SessionLifecycle.Finished, finished.Lifecycle);

            RoundStateMachine candidate = P13.Unstarted("m-finished-next");
            MatchTransition rejected = finished.StartNextRound(candidate);

            Assert.False(rejected.IsAccepted);
            Assert.Equal(MatchTransitionErrorCode.SessionFinished, rejected.Error!.Code);
        }

        [Fact]
        public void PauseAndResumeKeepTheActiveRound()
        {
            RoundStateMachine round = P13.Dealt("m-pause");
            MatchSession session = Start(round);
            RoundStateMachine active = session.ActiveRound!;

            MatchSession paused = session.Pause().State!;
            Assert.Equal(SessionLifecycle.Paused, paused.Lifecycle);
            Assert.Same(active, paused.ActiveRound);

            MatchSession resumed = paused.Resume().State!;
            Assert.Equal(SessionLifecycle.RoundActive, resumed.Lifecycle);
            Assert.Same(active, resumed.ActiveRound);
            Assert.Equal(3L, resumed.Version);
        }

        [Fact]
        public void NextRoundMustBeAFreshStackingInstance()
        {
            RoundStateMachine round = P13.Dealt("m-fresh-a");
            MatchSession session = Start(round);
            MatchSession waiting = FinishTsumo(session, new long[] { 33000, 22333, 22334, 22333 });

            RoundStateMachine notFresh = P13.Dealt(
                "m-fresh-b",
                waiting.CurrentDealer,
                waiting.BetweenRoundScores.ToArray(),
                0,
                waiting.CurrentRoundWind,
                waiting.CurrentRoundNumber,
                waiting.CurrentHonba);

            MatchTransition rejected = waiting.StartNextRound(notFresh);

            Assert.False(rejected.IsAccepted);
            Assert.Equal(MatchTransitionErrorCode.RoundStateMismatch, rejected.Error!.Code);
        }

        [Fact]
        public void ForeignTileBranchIsRejectedByUpdateActiveRound()
        {
            RoundStateMachine round = P13.Dealt("m-bind-update");
            MatchSession session = Start(round);
            RoundStateMachine bound = session.ActiveRound!;
            RoundStateMachine foreign = RoundStateMachine.CreateDealt(
                P13.Spec("m-bind-update"),
                TilePositionMap.CreateDealt(
                    RecordedRandomDraw.Standard("m-bind-foreign-map"),
                    0));
            RoundTransition foreignStep = foreign.Execute(RoundCommand.Discard(
                "m-bind-update",
                0,
                P13.FirstHandTile(foreign, 0),
                new[] { 1 }));
            Assert.True(foreignStep.IsAccepted, foreignStep.Error?.ToString());

            long sessionVersion = session.Version;
            long[] scores = session.BetweenRoundScores.ToArray();
            MatchTransition rejected = session.UpdateActiveRound(foreignStep.State!);

            Assert.False(rejected.IsAccepted);
            Assert.Equal(MatchTransitionErrorCode.ForeignRoundBranch, rejected.Error!.Code);
            Assert.Same(bound, session.ActiveRound);
            Assert.Equal(sessionVersion, session.Version);
            Assert.Equal(SessionLifecycle.RoundActive, session.Lifecycle);
            Assert.Empty(session.RoundRecords);
            Assert.Equal(scores, session.BetweenRoundScores);
            Assert.Null(session.Result);
        }

        [Fact]
        public void ForeignTerminalBranchIsRejectedWithoutRecordOrProgress()
        {
            RoundStateMachine round = P13.Dealt("m-bind-terminal");
            MatchSession session = Start(round);
            RoundStateMachine bound = session.ActiveRound!;
            RoundStateMachine foreign = RoundStateMachine.CreateDealt(
                P13.Spec("m-bind-terminal"),
                TilePositionMap.CreateDealt(
                    RecordedRandomDraw.Standard("m-bind-foreign-terminal"),
                    0));
            RoundTransition opened = foreign.Execute(RoundCommand.Discard(
                "m-bind-terminal",
                0,
                P13.FirstHandTile(foreign, 0),
                new[] { 1 }));
            Assert.True(opened.IsAccepted, opened.Error?.ToString());
            RoundTransition ended = opened.State!.Execute(RoundCommand.Ron(
                "m-bind-terminal",
                1,
                P13.Ron(foreign, 1, 0, "res-foreign-terminal")));
            Assert.True(ended.IsAccepted, ended.Error?.ToString());
            Assert.True(ended.State!.IsFinished);
            Assert.Equal(2L, ended.State.Version);

            long sessionVersion = session.Version;
            long[] scores = session.BetweenRoundScores.ToArray();
            MatchTransition rejected = session.AcceptRoundResult(ended.State, Utc, 30000L);

            Assert.False(rejected.IsAccepted);
            Assert.Equal(MatchTransitionErrorCode.ForeignRoundBranch, rejected.Error!.Code);
            Assert.Same(bound, session.ActiveRound);
            Assert.Equal(sessionVersion, session.Version);
            Assert.Equal(SessionLifecycle.RoundActive, session.Lifecycle);
            Assert.Empty(session.RoundRecords);
            Assert.Equal(scores, session.BetweenRoundScores);
            Assert.Null(session.Result);
        }

        [Fact]
        public void LegitimateSingleStepUpdateAndMultiStepTerminalStillWork()
        {
            RoundStateMachine round = P13.Dealt("m-bind-legal");
            MatchSession session = Start(round);
            RoundStateMachine bound = session.ActiveRound!;
            RoundTransition opened = bound.Execute(RoundCommand.Discard(
                "m-bind-legal",
                0,
                P13.FirstHandTile(bound, 0),
                new[] { 1 }));
            Assert.True(opened.IsAccepted, opened.Error?.ToString());
            long sessionVersion = session.Version;

            MatchTransition updated = session.UpdateActiveRound(opened.State!);

            Assert.True(updated.IsAccepted, updated.Error?.ToString());
            Assert.Same(opened.State, updated.State!.ActiveRound);
            Assert.Equal(sessionVersion, updated.State.Version);

            RoundTransition ended = updated.State.ActiveRound!.Execute(RoundCommand.Ron(
                "m-bind-legal",
                1,
                P13.Ron(bound, 1, 0, "res-legal-terminal")));
            Assert.True(ended.IsAccepted, ended.Error?.ToString());
            Assert.Equal(2L, ended.State!.Version);

            MatchTransition accepted = updated.State.AcceptRoundResult(ended.State, Utc, 31000L);

            Assert.True(accepted.IsAccepted, accepted.Error?.ToString());
            Assert.Single(accepted.State!.RoundRecords);
            Assert.Equal(SessionLifecycle.WaitingReady, accepted.State.Lifecycle);
        }

        [Fact]
        public void ConsecutiveUpdateActiveRoundTransitionsCarryOnlyNewEvents()
        {
            RoundStateMachine round = P13.Dealt("m-event-delta");
            MatchSession session = Start(round);
            RoundStateMachine first = session.ActiveRound!;

            RoundTransition step1 = first.Execute(RoundCommand.Discard(
                "m-event-delta",
                0,
                P13.FirstHandTile(first, 0),
                new[] { 1 }));
            Assert.True(step1.IsAccepted, step1.Error?.ToString());
            MatchTransition update1 = session.UpdateActiveRound(step1.State!);
            Assert.True(update1.IsAccepted, update1.Error?.ToString());
            AssertEventDelta(first, step1.State!, update1.Events);
            Assert.NotEmpty(update1.Events);

            RoundStateMachine second = update1.State!.ActiveRound!;
            RoundTransition step2 = second.Execute(RoundCommand.Pass("m-event-delta", 1));
            Assert.True(step2.IsAccepted, step2.Error?.ToString());
            MatchTransition update2 = update1.State.UpdateActiveRound(step2.State!);
            Assert.True(update2.IsAccepted, update2.Error?.ToString());

            AssertEventDelta(second, step2.State!, update2.Events);
            Assert.NotEmpty(update2.Events);
            Assert.Equal(second.Events.Count, update2.Events[0].Ordinal);
            Assert.DoesNotContain(update2.Events, e => e.Ordinal < second.Events.Count);
            Assert.DoesNotContain(update2.Events, e => update1.Events.Any(firstEvent => firstEvent.Ordinal == e.Ordinal));
        }

        [Fact]
        public void UpdateActiveRoundTerminalDeliversRoundEndedOnceAndAcceptDoesNotRepeatIt()
        {
            RoundStateMachine round = P13.Dealt("m-event-terminal-update");
            MatchSession session = Start(round);
            RoundStateMachine active = session.ActiveRound!;
            RoundTransition terminal = active.Execute(
                RoundCommand.Tsumo("m-event-terminal-update", 0, P13.Tsumo(active)));
            Assert.True(terminal.IsAccepted, terminal.Error?.ToString());

            MatchTransition installed = session.UpdateActiveRound(terminal.State!);
            Assert.True(installed.IsAccepted, installed.Error?.ToString());
            AssertEventDelta(active, terminal.State!, installed.Events);
            Assert.Equal(1, installed.Events.Count(e => e.Type == RoundEventType.RoundEnded));
            Assert.Equal(1, installed.State!.ActiveRound!.Events.Count(e => e.Type == RoundEventType.RoundEnded));

            MatchTransition accepted = installed.State.AcceptRoundResult(Utc, 32000L);

            Assert.True(accepted.IsAccepted, accepted.Error?.ToString());
            Assert.Empty(accepted.Events);
            Assert.Single(accepted.State!.RoundRecords);
        }

        [Fact]
        public void DirectAcceptDeliversUndeliveredTerminalEventsExactlyOnce()
        {
            RoundStateMachine round = P13.Dealt("m-event-terminal-direct");
            MatchSession session = Start(round);
            RoundStateMachine active = session.ActiveRound!;
            RoundTransition terminal = active.Execute(
                RoundCommand.Tsumo("m-event-terminal-direct", 0, P13.Tsumo(active)));
            Assert.True(terminal.IsAccepted, terminal.Error?.ToString());

            MatchTransition accepted = session.AcceptRoundResult(terminal.State!, Utc, 33000L);

            Assert.True(accepted.IsAccepted, accepted.Error?.ToString());
            AssertEventDelta(active, terminal.State!, accepted.Events);
            Assert.Equal(1, accepted.Events.Count(e => e.Type == RoundEventType.RoundEnded));
            Assert.Single(accepted.State!.RoundRecords);
        }

        private static void AssertEventDelta(
            RoundStateMachine source,
            RoundStateMachine target,
            IReadOnlyList<RoundEvent> delta)
        {
            Assert.Equal(source.Events.Count + delta.Count, target.Events.Count);
            for (int i = 0; i < delta.Count; i++)
            {
                RoundEvent expected = target.Events[source.Events.Count + i];
                Assert.Equal(source.Events.Count + i, expected.Ordinal);
                Assert.Equal(expected.Ordinal, delta[i].Ordinal);
                Assert.Equal(expected.Type, delta[i].Type);
                Assert.Equal(expected.Seat, delta[i].Seat);
                Assert.Equal(expected.Tile, delta[i].Tile);
                Assert.Equal(expected.Detail, delta[i].Detail);
            }

            Assert.Equal(
                delta.Count,
                delta.Select(e => e.Ordinal).Distinct().Count());
        }

        [Fact]
        public void CloseIsTerminalForTheSession()
        {
            RoundStateMachine round = P13.Dealt("m-close");
            MatchSession session = Start(round);

            MatchSession closed = session.Close().State!;
            Assert.Equal(SessionLifecycle.Closed, closed.Lifecycle);

            MatchTransition rejected = closed.Close();
            Assert.True(rejected.IsAccepted);
            Assert.Equal(SessionLifecycle.Closed, rejected.State!.Lifecycle);
        }
    }
}
