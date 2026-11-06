#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Domain;
using Mahjong.LocalHost;
using Mahjong.Unity.App;
using Mahjong.Unity.Table;
using Mahjong.Unity.UI.Session;
using Xunit;
using ContractCursor = Mahjong.Contracts.StreamCursor;
using WireEvent = Mahjong.Contracts.Wire.EventEnvelope;
using WireScore = Mahjong.Contracts.Wire.ScoreView;
using WireDecisionView = Mahjong.Contracts.Wire.DecisionView;
using WireLegalActionView = Mahjong.Contracts.Wire.LegalActionView;
using WirePlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;

namespace Mahjong.Unity.TableModel.Tests
{
    /// <summary>
    /// Directed tests for the real P4.5 full-match driver policy. These compile the
    /// production policy source and use confirmed Client-shaped snapshots; they do
    /// not start a Unity Player or a synthetic match fixture.
    /// </summary>
    public sealed class LocalFullMatchPolicyTests
    {
        private static readonly MethodInfo ProjectPlayerMethod =
            typeof(TableVisibleEventBatch).GetMethod(
                "ProjectPlayer",
                BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Client ProjectPlayer method was not found.");

        private static readonly Type FenceType =
            typeof(TableVisibleEventBatch).Assembly.GetType("Mahjong.Client.TableVisibleEventFence", true)
            ?? throw new InvalidOperationException("Client visible-event fence type was not found.");

        private static readonly ConstructorInfo ObservedBatchCtor =
            typeof(ObservedVisibleEventBatch).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(SessionViewState) },
                null)
            ?? throw new InvalidOperationException("ObservedVisibleEventBatch constructor was not found.");

        [Fact]
        public void CandidateSelectionUsesOnlyServerActionsAndPrefersWins()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState view = View(clock, Decision(
                ("discard", ProtocolActionType.DiscardTile, new uint[] { 13u }),
                ("pass", ProtocolActionType.Pass, null),
                ("ron", ProtocolActionType.DeclareRon, null)));

            PlayerLegalActionState? selected = LocalFullMatchPolicy.SelectAction(view);
            Assert.NotNull(selected);
            Assert.Equal(PlayerActionKind.DeclareRon, selected!.Action);
            Assert.Equal("ron", selected.OptionId);

            PlayerTableViewState noDecision = View(clock, P43TestData.PlayerSnapshot(includeDecision: false));
            Assert.Null(LocalFullMatchPolicy.SelectAction(noDecision));
        }

        [Fact]
        public void RequestIdIsStableForTheSameLogicalActionAndChangesWithTheDecision()
        {
            string first = LocalFullMatchPolicy.BuildSubmitRequestId(
                P43TestData.RoundId, "decision-a", PlayerActionKind.DiscardTile, "discard-13", new uint[] { 13 });
            string again = LocalFullMatchPolicy.BuildSubmitRequestId(
                P43TestData.RoundId, "decision-a", PlayerActionKind.DiscardTile, "discard-13", new uint[] { 13 });
            string otherDecision = LocalFullMatchPolicy.BuildSubmitRequestId(
                P43TestData.RoundId, "decision-b", PlayerActionKind.DiscardTile, "discard-13", new uint[] { 13 });

            Assert.Equal(first, again);
            Assert.NotEqual(first, otherDecision);
            Assert.StartsWith("fm-", first);
            Assert.True(first.Length <= ProtocolConstants.MaxIdChars);
        }

        [Fact]
        public void UnknownOutcomeKeepsTheOriginalIdAndForbidsANewOne()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState view = View(clock, Decision(
                ("discard", ProtocolActionType.DiscardTile, new uint[] { 13u })));
            var factory = new TableCommandIntentFactory(clock);
            SessionViewState session = P43TestData.View(P43TestData.PlayerSnapshot());

            Assert.True(LocalFullMatchPolicy.TryCreateActionPlan(
                session, view, factory, clock.MonotonicMilliseconds, out FullMatchActionPlan? plan, out _, out _));
            var ledger = new FullMatchRequestLedger();
            Assert.True(ledger.Record(plan!, out string existing));
            Assert.Equal(plan!.RequestId, existing);
            Assert.True(ledger.MayIssueNewRequestId(plan));

            ledger.MarkUnresolved(plan);
            Assert.True(ledger.IsUnresolved(plan));
            Assert.False(ledger.MayIssueNewRequestId(plan));

            Assert.True(LocalFullMatchPolicy.TryCreateActionPlan(
                session, view, factory, clock.MonotonicMilliseconds, out FullMatchActionPlan? replanned, out _, out _));
            Assert.Equal(plan.RequestId, replanned!.RequestId);
            Assert.True(ledger.Record(replanned, out _));
        }

        [Fact]
        public void ExpiredDecisionAndAutomatedSeatFailClosedWithoutACommand()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState expired = View(clock, Decision(
                ("discard", ProtocolActionType.DiscardTile, new uint[] { 13u })));
            clock.Advance(20_000);
            var factory = new TableCommandIntentFactory(clock);
            SessionViewState session = P43TestData.View(P43TestData.PlayerSnapshot());

            Assert.False(LocalFullMatchPolicy.TryCreateActionPlan(
                session, expired, factory, clock.MonotonicMilliseconds,
                out FullMatchActionPlan? plan, out string code, out _));
            Assert.Null(plan);
            Assert.Equal("DEADLINE_EXPIRED", code);

            var clock2 = new ManualTableClock();
            PlayerTableViewState automated = View(clock2, P43TestData.PlayerSnapshot(controlledByComputer: true));
            var factory2 = new TableCommandIntentFactory(clock2);
            Assert.False(LocalFullMatchPolicy.TryCreateActionPlan(
                P43TestData.View(P43TestData.PlayerSnapshot(controlledByComputer: true)),
                automated, factory2, clock2.MonotonicMilliseconds,
                out FullMatchActionPlan? automatedPlan, out string automatedCode, out _));
            Assert.Null(automatedPlan);
            Assert.Equal("CONTROL_AUTOMATED", automatedCode);
        }

        [Fact]
        public void RoundGuardRejectsRevivalAndAnUncommittedRoundChange()
        {
            var guard = new FullMatchRoundGuard();
            Assert.Equal(
                FullMatchRoundTransition.NewRound,
                guard.Observe(View(new ManualTableClock(), P43TestData.PlayerSnapshot(roundId: "r1")), out _, out _));
            Assert.Equal(
                FullMatchRoundTransition.InRound,
                guard.Observe(View(new ManualTableClock(), P43TestData.PlayerSnapshot(roundId: "r1")), out _, out _));

            var terminalSnapshot = RoundEndSnapshot("r1", 25_000, 25_000, 25_000, 25_000, 0);
            PlayerTableViewState terminalView = View(new ManualTableClock(), terminalSnapshot);
            Assert.Equal(
                FullMatchRoundTransition.Terminal,
                guard.Observe(terminalView, out _, out _));
            Assert.True(LocalFullMatchPolicy.TryCaptureTerminal(
                terminalSnapshot, terminalView, out FullMatchRoundTerminal? terminal, out _, out _));
            Assert.True(guard.CommitTerminal(
                terminal!,
                MatchSnapshot(new[]
                {
                    new LocalMatchRoundRecord("r1", "res1", RoundOutcomeKind.ExhaustiveDraw, -1, -1,
                        new long[] { 25_000, 25_000, 25_000, 25_000 }, 0)
                }, finished: false, finalScores: Array.Empty<long>(), rankings: Array.Empty<RankingEntry>()),
                out string code, out string detail));
            Assert.Equal(string.Empty, code + detail);

            Assert.Equal(
                FullMatchRoundTransition.NewRound,
                guard.Observe(View(new ManualTableClock(), P43TestData.PlayerSnapshot(roundId: "r2")), out _, out _));

            Assert.Equal(
                FullMatchRoundTransition.Failed,
                guard.Observe(terminalView, out string revivalCode, out _));
            Assert.Equal("ROUND_REVIVED", revivalCode);

            var guard2 = new FullMatchRoundGuard();
            guard2.Observe(View(new ManualTableClock(), P43TestData.PlayerSnapshot(roundId: "r1")), out _, out _);
            Assert.Equal(
                FullMatchRoundTransition.Failed,
                guard2.Observe(View(new ManualTableClock(), P43TestData.PlayerSnapshot(roundId: "r2")), out string changeCode, out _));
            Assert.Equal("ROUND_CHANGED_BEFORE_TERMINAL", changeCode);

            var guard3 = new FullMatchRoundGuard();
            guard3.Observe(View(new ManualTableClock(), P43TestData.PlayerSnapshot(roundId: "r1")), out _, out _);
            guard3.Observe(terminalView, out _, out _);
            Assert.Equal(
                FullMatchRoundTransition.Failed,
                guard3.Observe(View(new ManualTableClock(), P43TestData.PlayerSnapshot(roundId: "r1")), out string activeCode, out _));
            Assert.Equal("TERMINAL_ROUND_REVIVED", activeCode);

            var guard4 = new FullMatchRoundGuard();
            guard4.Observe(View(new ManualTableClock(), P43TestData.PlayerSnapshot(roundId: "r1")), out _, out _);
            guard4.Observe(terminalView, out _, out _);
            Assert.Equal(
                FullMatchRoundTransition.Failed,
                guard4.Observe(View(new ManualTableClock(), P43TestData.PlayerSnapshot(roundId: "r2")), out string uncommittedCode, out _));
            Assert.Equal("NEXT_ROUND_BEFORE_COMMIT", uncommittedCode);
        }

        [Fact]
        public void MandatoryDrawRequiresItsOwnVisibleEventAndStableRequestId()
        {
            var clock = new ManualTableClock();
            WirePlayerSnapshot raw = P43TestData.PlayerSnapshot(includeDecision: false);
            raw.PublicView.Board.Phase = GamePhase.Draw;
            raw.PublicView.Board.CurrentSeat = 0;
            PlayerTableViewState view = View(clock, raw);
            SessionViewState session = P43TestData.View(raw);

            Assert.True(LocalFullMatchPolicy.TryCreateMandatoryDrawPlan(
                session, view, clock.MonotonicMilliseconds,
                out FullMatchActionPlan? first, out _, out _));
            Assert.True(LocalFullMatchPolicy.TryCreateMandatoryDrawPlan(
                session, view, clock.MonotonicMilliseconds,
                out FullMatchActionPlan? second, out _, out _));
            Assert.Equal(first!.RequestId, second!.RequestId);
            Assert.Equal(PlayerActionKind.DrawTile, first.Action);
            Assert.True(first.RequiresVisibleEvent);
            Assert.Equal(CommandKind.SubmitAction, first.Command.Kind);
        }

        [Fact]
        public void TerminalCommitRejectsAClientScoreThatDoesNotEqualTheDurableRecord()
        {
            var guard = new FullMatchRoundGuard();
            var terminalSnapshot = RoundEndSnapshot("r1", 25_000, 25_000, 25_000, 25_000, 0);
            PlayerTableViewState terminalView = View(new ManualTableClock(), terminalSnapshot);
            Assert.Equal(
                FullMatchRoundTransition.NewRound,
                guard.Observe(terminalView, out _, out _));
            Assert.Equal(
                FullMatchRoundTransition.Terminal,
                guard.Observe(terminalView, out _, out _));
            Assert.True(LocalFullMatchPolicy.TryCaptureTerminal(
                terminalSnapshot, terminalView, out FullMatchRoundTerminal? terminal, out _, out _));

            LocalMatchSnapshot wrong = MatchSnapshot(new[]
            {
                new LocalMatchRoundRecord("r1", "res1", RoundOutcomeKind.ExhaustiveDraw, -1, -1,
                    new long[] { 26_000, 24_000, 25_000, 25_000 }, 0)
            }, finished: false, finalScores: Array.Empty<long>(), rankings: Array.Empty<RankingEntry>());
            Assert.False(guard.CommitTerminal(terminal!, wrong, out string code, out _));
            Assert.Equal("ROUND_SCORE_MISMATCH", code);
        }

        [Fact]
        public void ActionEventPredicateRejectsAnotherSeatAndAnotherBinding()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState view = View(clock, Decision(
                ("discard", ProtocolActionType.DiscardTile, new uint[] { 13u })));
            var factory = new TableCommandIntentFactory(clock);
            SessionViewState session = P43TestData.View(P43TestData.PlayerSnapshot());
            Assert.True(LocalFullMatchPolicy.TryCreateActionPlan(
                session, view, factory, clock.MonotonicMilliseconds, out FullMatchActionPlan? plan, out _, out _));

            TableSessionBinding binding = P43TestData.Binding();
            object fence = NewFence();
            TableVisibleEventBatch batch = Project(
                binding, fence, 6, 6, P43TestData.RoundId, false,
                Discard(6, 6, seat: 0));
            var observed = (ObservedVisibleEventBatch)ObservedBatchCtor.Invoke(new object[]
            {
                new SessionViewState(
                    1,
                    TableConnectionState.Ready,
                    binding,
                    P43TestData.PlayerSnapshot(stateVersion: 6, eventSeq: 6),
                    null,
                    null,
                    null,
                    null,
                    false,
                    new ContractCursor(new StreamVersion(5), new ProjectionSeq(6)),
                    batch,
                    6)
            });
            Assert.True(LocalFullMatchPolicy.IsOwnActionEvent(plan!, observed, observed.Events[0], 6));
            Assert.False(LocalFullMatchPolicy.IsOwnActionEvent(plan!, observed, observed.Events[0], 7));

            TableSessionBinding oldBinding = P43TestData.Binding(bindingEpoch: P43TestData.BindingEpoch - 1);
            TableVisibleEventBatch oldBatch = Project(
                oldBinding, NewFence(), 6, 6, P43TestData.RoundId, false,
                Discard(6, 6, seat: 0));
            ObservedVisibleEventBatch oldObserved = Observe(oldBatch, oldBinding, 6, 6);
            Assert.False(LocalFullMatchPolicy.IsOwnActionEvent(plan!, oldObserved, oldObserved.Events[0], 6));

            TableVisibleEventBatch otherSeatBatch = Project(
                binding, NewFence(), 6, 6, P43TestData.RoundId, false,
                Discard(6, 6, seat: 1));
            ObservedVisibleEventBatch otherSeat = Observe(otherSeatBatch, binding, 6, 6);
            Assert.False(LocalFullMatchPolicy.IsOwnActionEvent(plan!, otherSeat, otherSeat.Events[0], 6));

            TableVisibleEventBatch otherRoundBatch = Project(
                binding, NewFence(), 6, 6, "other-round", false,
                Discard(6, 6, seat: 0));
            ObservedVisibleEventBatch otherRound = Observe(otherRoundBatch, binding, 6, 6);
            Assert.False(LocalFullMatchPolicy.IsOwnActionEvent(plan!, otherRound, otherRound.Events[0], 6));
        }

        [Fact]
        public void TerminalCaptureRequiresRoundEndedOutcomeAndConservation()
        {
            var clock = new ManualTableClock();
            WirePlayerSnapshot valid = RoundEndSnapshot("r1", 25_000, 25_000, 25_000, 25_000, 0);
            PlayerTableViewState validView = View(clock, valid);
            Assert.True(LocalFullMatchPolicy.TryCaptureTerminal(
                valid, validView, out FullMatchRoundTerminal? terminal, out _, out _));
            Assert.Equal(100_000L, terminal!.TableTotal);
            Assert.Contains("RoundEnded", terminal.SettlementEventTypes);
            Assert.Contains("DrawGame", terminal.SettlementEventTypes);

            WirePlayerSnapshot missingOutcome = RoundEndSnapshot("r1", 25_000, 25_000, 25_000, 25_000, 0, includeOutcome: false);
            Assert.False(LocalFullMatchPolicy.TryCaptureTerminal(
                missingOutcome, View(clock, missingOutcome), out _, out string code, out _));
            Assert.Equal("TERMINAL_OUTCOME_MISSING", code);

            WirePlayerSnapshot badConservation = RoundEndSnapshot("r1", 24_000, 25_000, 25_000, 25_000, 0);
            Assert.False(LocalFullMatchPolicy.TryCaptureTerminal(
                badConservation, View(clock, badConservation), out _, out string conservationCode, out _));
            Assert.Equal("TERMINAL_CONSERVATION", conservationCode);
        }

        [Fact]
        public void MatchCompletionUsesTheDurableCoordinatorFactsAndRejectsMissingRounds()
        {
            FullMatchRoundTerminal terminal = Terminal("r1", 25_000, 25_000, 25_000, 25_000, 0);
            LocalMatchSnapshot match = MatchSnapshot(new[]
            {
                new LocalMatchRoundRecord("r1", "res1", RoundOutcomeKind.ExhaustiveDraw, -1, -1,
                    new long[] { 25_000, 25_000, 25_000, 25_000 }, 0)
            }, finished: true,
                finalScores: new long[] { 25_000, 25_000, 25_000, 25_000 },
                rankings: new[]
                {
                    new RankingEntry(1, 0, 25_000), new RankingEntry(2, 1, 25_000),
                    new RankingEntry(3, 2, 25_000), new RankingEntry(4, 3, 25_000)
                });

            Assert.True(LocalFullMatchPolicy.ValidateMatchCompletion(
                match, new[] { terminal }, out string code, out string detail), code + detail);

            Assert.False(LocalFullMatchPolicy.ValidateMatchCompletion(
                match, Array.Empty<FullMatchRoundTerminal>(), out string countCode, out _));
            Assert.Equal("MATCH_ROUND_COUNT", countCode);

            LocalMatchSnapshot unfinished = MatchSnapshot(new[]
            {
                new LocalMatchRoundRecord("r1", "res1", RoundOutcomeKind.ExhaustiveDraw, -1, -1,
                    new long[] { 25_000, 25_000, 25_000, 25_000 }, 0)
            }, finished: false, finalScores: Array.Empty<long>(), rankings: Array.Empty<RankingEntry>());
            Assert.False(LocalFullMatchPolicy.ValidateMatchCompletion(
                unfinished, new[] { terminal }, out string unfinishedCode, out _));
            Assert.Equal("MATCH_NOT_FINISHED", unfinishedCode);
        }

        private static PlayerTableViewState View(ManualTableClock clock, WirePlayerSnapshot snapshot)
        {
            return new PlayerTableProjectionMapper(clock).Project(P43TestData.View(snapshot));
        }

        private static WirePlayerSnapshot Decision(params (string Option, ProtocolActionType Type, uint[]? Tiles)[] actions)
        {
            WirePlayerSnapshot snapshot = P43TestData.PlayerSnapshot(includeDecision: false);
            snapshot.PrivateView.Decision = new WireDecisionView
            {
                DecisionId = "decision-p43",
                ControlEpoch = 7,
                DeadlineRemainingMs = 8000
            };
            foreach ((string option, ProtocolActionType type, uint[]? tiles) in actions)
            {
                var action = new WireLegalActionView
                {
                    ActionType = (uint)type,
                    OptionId = option
                };
                if (tiles != null)
                {
                    action.TileIds.Add(tiles);
                }

                snapshot.PrivateView.Decision.Actions.Add(action);
            }

            return snapshot;
        }

        private static WirePlayerSnapshot RoundEndSnapshot(
            string roundId,
            long s0,
            long s1,
            long s2,
            long s3,
            uint sticks,
            bool includeOutcome = true)
        {
            WirePlayerSnapshot snapshot = P43TestData.PlayerSnapshot(
                roundId: roundId,
                includeDecision: false,
                includeCheat: false);
            snapshot.PublicView.Board.Phase = GamePhase.RoundEnd;
            snapshot.PublicView.Board.RiichiSticks = sticks;
            snapshot.PublicView.SettlementRoundId = roundId;
            snapshot.PublicView.Session.Finished = false;
            snapshot.PublicView.Session.FinalScores.Clear();
            snapshot.PublicView.Session.FinalScores.Add(new WireScore { Seat = 0, Score = s0 });
            snapshot.PublicView.Session.FinalScores.Add(new WireScore { Seat = 1, Score = s1 });
            snapshot.PublicView.Session.FinalScores.Add(new WireScore { Seat = 2, Score = s2 });
            snapshot.PublicView.Session.FinalScores.Add(new WireScore { Seat = 3, Score = s3 });
            snapshot.PublicView.SettlementEvents.Add(PublicEvent(EventType.RoundEnded, e =>
                e.RoundEnded = new RoundEnded { TurnNumber = 1, RoundWind = 1, RoundNumber = 1 }));
            if (includeOutcome)
            {
                snapshot.PublicView.SettlementEvents.Add(PublicEvent(EventType.DrawGame, e =>
                    e.DrawGame = new DrawGame { TurnNumber = 1, Reason = "EXHAUSTIVE_DRAW" }));
            }

            return snapshot;
        }

        private static PublicEventEnvelope PublicEvent(EventType type, Action<PublicEventEnvelope> body)
        {
            var envelope = new PublicEventEnvelope
            {
                EventSchemaVersion = ProtocolConstants.EventSchemaVersion,
                EventSeq = 9,
                CommandVersion = 8,
                EventOrdinal = 0,
                EventType = type,
                Visibility = EventVisibility.SettlementPublic
            };
            body(envelope);
            return envelope;
        }

        private static LocalMatchSnapshot MatchSnapshot(
            IReadOnlyList<LocalMatchRoundRecord> records,
            bool finished,
            IReadOnlyList<long> finalScores,
            IReadOnlyList<RankingEntry> rankings)
        {
            return new LocalMatchSnapshot(
                "match-1",
                records.Count,
                finished ? string.Empty : "r" + (records.Count + 1),
                0,
                1,
                records.Count,
                0,
                records.Count == 0 ? new long[] { 25_000, 25_000, 25_000, 25_000 } : records[records.Count - 1].FinalScores,
                finalScores,
                records.Count == 0 ? 0 : records[records.Count - 1].RemainingRiichiSticks,
                finished,
                finished ? "ExhaustiveDrawLimit" : string.Empty,
                rankings,
                records);
        }

        private static FullMatchRoundTerminal Terminal(
            string roundId,
            long s0,
            long s1,
            long s2,
            long s3,
            int sticks)
        {
            return new FullMatchRoundTerminal(
                roundId,
                new long[] { s0, s1, s2, s3 },
                sticks,
                "Draw",
                -1,
                -1,
                5,
                9,
                new[] { "RoundEnded", "DrawGame" });
        }

        private static object NewFence()
        {
            return Activator.CreateInstance(FenceType, nonPublic: true)
                ?? throw new InvalidOperationException("Could not construct the production visible-event fence.");
        }

        private static TableVisibleEventBatch Project(
            TableSessionBinding binding,
            object fence,
            ulong stateVersion,
            ulong eventSeq,
            string roundId,
            bool baseline,
            params WireEvent[] events)
        {
            var cursor = new ContractCursor(new StreamVersion(5), new ProjectionSeq(eventSeq + 10));
            return (TableVisibleEventBatch)ProjectPlayerMethod.Invoke(
                null,
                new object[]
                {
                    binding,
                    cursor,
                    baseline,
                    stateVersion,
                    eventSeq,
                    roundId,
                    (IReadOnlyList<WireEvent>)events,
                    fence
                })!;
        }

        private static ObservedVisibleEventBatch Observe(
            TableVisibleEventBatch batch,
            TableSessionBinding binding,
            ulong stateVersion,
            ulong eventSeq)
        {
            return (ObservedVisibleEventBatch)ObservedBatchCtor.Invoke(new object[]
            {
                new SessionViewState(
                    1,
                    TableConnectionState.Ready,
                    binding,
                    P43TestData.PlayerSnapshot(stateVersion: stateVersion, eventSeq: eventSeq),
                    null,
                    null,
                    null,
                    null,
                    false,
                    new ContractCursor(new StreamVersion(5), new ProjectionSeq(eventSeq)),
                    batch,
                    5)
            });
        }

        private static WireEvent Discard(ulong eventSeq, ulong commandVersion, int seat)
        {
            return new WireEvent
            {
                EventSchemaVersion = ProtocolConstants.EventSchemaVersion,
                EventSeq = eventSeq,
                CommandVersion = commandVersion,
                EventOrdinal = 0,
                EventType = EventType.TileDiscarded,
                Visibility = EventVisibility.Public,
                TileDiscarded = new TileDiscarded
                {
                    TurnNumber = 1,
                    Player = seat,
                    TileId = 13
                }
            };
        }
    }
}
