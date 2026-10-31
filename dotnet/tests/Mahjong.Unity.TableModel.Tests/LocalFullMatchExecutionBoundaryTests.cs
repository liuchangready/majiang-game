#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Unity.App;
using Mahjong.Unity.Table;
using Mahjong.Unity.UI.Session;
using Xunit;
using ContractCursor = Mahjong.Contracts.StreamCursor;
using ClientReceiptStatus = Mahjong.Contracts.ReceiptStatus;
using WirePlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;
using WireScore = Mahjong.Contracts.Wire.ScoreView;
using WireDecisionView = Mahjong.Contracts.Wire.DecisionView;
using WireLegalActionView = Mahjong.Contracts.Wire.LegalActionView;

namespace Mahjong.Unity.TableModel.Tests
{
    /// <summary>
    /// Directed regressions for the shared production execution boundary. Each
    /// case drives the real boundary with a Client-shaped session/view, a counting
    /// command sink and a manual clock/budget. The fixture never supplies the
    /// expected error code and never writes an action/request row itself.
    /// </summary>
    public sealed class LocalFullMatchExecutionBoundaryTests
    {
        private static readonly MethodInfo ProjectPlayerMethod =
            typeof(TableVisibleEventBatch).GetMethod(
                "ProjectPlayer",
                BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Client ProjectPlayer method was not found.");

        private static readonly Type FenceType =
            typeof(TableVisibleEventBatch).Assembly.GetType("Mahjong.Client.TableVisibleEventFence", true)
            ?? throw new InvalidOperationException("Client visible-event fence type was not found.");

        private static readonly FullMatchExecutionBudget FastBudget =
            new FullMatchExecutionBudget(1_000L, 2_000L, 1_000L);

        [Fact]
        public void MandatoryDrawConfirmsOnlyWithExactAppliedReceiptAndCurrentRoundEndedBatch()
        {
            var clock = new ManualTableClock();
            object fence = NewFence();
            TableSessionBinding binding = P43TestData.Binding();
            SessionViewState baseline = DrawState(binding, fence, 5, 105, baseline: true);
            var transport = new ScriptedTransport(baseline);
            var ledger = new FullMatchRequestLedger();
            var boundary = new FullMatchExecutionBoundary(
                transport,
                ledger,
                FastBudget,
                notice => transport.Notices.Add(notice));
            var mapper = new PlayerTableProjectionMapper(clock);
            var factory = new TableCommandIntentFactory(clock);

            FullMatchActionPlan expected = ExpectedDrawPlan(baseline, clock, factory);
            FullMatchExecutionOutcome submitted = boundary.Poll(
                baseline,
                mapper.Project(baseline),
                factory,
                0L);

            Assert.Equal(FullMatchExecutionStatus.Waiting, submitted.Status);
            Assert.Single(transport.Submitted);
            ProtocolCommand command = transport.Submitted[0];
            Assert.Equal(CommandKind.SubmitAction, command.Kind);
            Assert.Equal(expected.RequestId, command.RequestId.Value);
            Assert.Equal(expected.ScopeId, command.ScopeId!.Value.Value);
            Assert.Equal(new StateVersion(5), command.ExpectedStateVersion);
            Assert.Equal(expected.RoundId, command.RoundId!.Value.Value);
            Assert.True(expected.Command.Payload.Equals(command.Payload), "canonical payload must be byte-identical");
            Assert.Equal(1, transport.SubmitCount);

            transport.RaiseReceipt(AppliedReceipt(command, evaluated: 5, commandVersion: 6, eventSeq: 106));
            SessionViewState confirmedState = DrawState(
                binding,
                fence,
                6,
                106,
                baseline: false,
                roundEndedCommandVersion: 6,
                phase: GamePhase.RoundEnd);
            transport.Publish(confirmedState);

            FullMatchExecutionOutcome confirmed = boundary.Poll(
                confirmedState,
                mapper.Project(confirmedState),
                factory,
                10L);

            Assert.Equal(FullMatchExecutionStatus.Confirmed, confirmed.Status);
            Assert.True(confirmed.ConfirmedByVisibleEvent);
            Assert.Equal(expected.RequestId, confirmed.Plan!.RequestId);
            Assert.Equal(1, transport.SubmitCount);
            Assert.False(ledger.IsUnresolved(expected));
            Assert.Contains(transport.Notices, n =>
                n.Kind == FullMatchExecutionNoticeKind.PlanRecorded &&
                n.Plan.RequestId == expected.RequestId);
            Assert.Contains(transport.Notices, n =>
                n.Kind == FullMatchExecutionNoticeKind.ActionConfirmed &&
                n.Plan.RequestId == expected.RequestId &&
                n.VisibleEvent);
        }

        [Fact]
        public void AppliedReceiptWithoutTheMatchingRoundEndedEventFailsAndStaysUnresolved()
        {
            var clock = new ManualTableClock();
            object fence = NewFence();
            TableSessionBinding binding = P43TestData.Binding();
            SessionViewState baseline = DrawState(binding, fence, 5, 105, baseline: true);
            var transport = new ScriptedTransport(baseline);
            var ledger = new FullMatchRequestLedger();
            var boundary = new FullMatchExecutionBoundary(transport, ledger, FastBudget);
            var mapper = new PlayerTableProjectionMapper(clock);
            var factory = new TableCommandIntentFactory(clock);
            FullMatchActionPlan expected = ExpectedDrawPlan(baseline, clock, factory);

            boundary.Poll(baseline, mapper.Project(baseline), factory, 0L);
            ProtocolCommand command = transport.Submitted[0];
            transport.RaiseReceipt(AppliedReceipt(command, evaluated: 5, commandVersion: 6, eventSeq: 106));
            SessionViewState emptyIncremental = DrawState(
                binding,
                fence,
                6,
                106,
                baseline: false,
                phase: GamePhase.Draw);
            transport.Publish(emptyIncremental);

            FullMatchExecutionOutcome outcome = boundary.Poll(
                emptyIncremental,
                mapper.Project(emptyIncremental),
                factory,
                FastBudget.ConfirmationTimeoutMs + 1L);

            Assert.Equal(FullMatchExecutionStatus.Failed, outcome.Status);
            Assert.Equal("VISIBLE_EVENT_MISSING", outcome.Code);
            Assert.True(ledger.IsUnresolved(expected), "an applied but unconfirmed action must stay unresolved");
            Assert.False(ledger.MayIssueNewRequestId(expected));
            Assert.Equal(1, transport.SubmitCount);

            FullMatchExecutionOutcome repeated = boundary.Poll(
                emptyIncremental,
                mapper.Project(emptyIncremental),
                factory,
                FastBudget.ConfirmationTimeoutMs + 5_000L);
            Assert.Equal("VISIBLE_EVENT_MISSING", repeated.Code);
            Assert.Equal(1, transport.SubmitCount);
        }

        [Fact]
        public void RoundEndedWithoutAnAppliedReceiptNeverConfirmsTheDraw()
        {
            var clock = new ManualTableClock();
            object fence = NewFence();
            TableSessionBinding binding = P43TestData.Binding();
            SessionViewState baseline = DrawState(binding, fence, 5, 105, baseline: true);
            var transport = new ScriptedTransport(baseline);
            var ledger = new FullMatchRequestLedger();
            var boundary = new FullMatchExecutionBoundary(transport, ledger, FastBudget);
            var mapper = new PlayerTableProjectionMapper(clock);
            var factory = new TableCommandIntentFactory(clock);
            FullMatchActionPlan expected = ExpectedDrawPlan(baseline, clock, factory);

            boundary.Poll(baseline, mapper.Project(baseline), factory, 0L);
            Assert.Single(transport.Submitted);

            // The exact terminal event arrives, but no receipt ever resolves: the
            // event alone must not confirm an unacknowledged request.
            SessionViewState terminalState = DrawState(
                binding,
                fence,
                6,
                106,
                baseline: false,
                roundEndedCommandVersion: 6,
                phase: GamePhase.RoundEnd);
            transport.Publish(terminalState);

            FullMatchExecutionOutcome outcome = boundary.Poll(
                terminalState,
                mapper.Project(terminalState),
                factory,
                FastBudget.ConfirmationTimeoutMs + 1L);

            Assert.Equal(FullMatchExecutionStatus.Failed, outcome.Status);
            Assert.Equal("RECEIPT_UNRESOLVED", outcome.Code);
            Assert.True(ledger.IsUnresolved(expected), "an unacknowledged action must stay unresolved");
            Assert.False(ledger.MayIssueNewRequestId(expected));
            Assert.Equal(1, transport.SubmitCount);
        }

        [Fact]
        public void RejectedDrawReceiptIsNotOverriddenByAMatchingRoundEndedEvent()
        {
            var clock = new ManualTableClock();
            object fence = NewFence();
            TableSessionBinding binding = P43TestData.Binding();
            SessionViewState baseline = DrawState(binding, fence, 5, 105, baseline: true);
            var transport = new ScriptedTransport(baseline);
            var ledger = new FullMatchRequestLedger();
            var boundary = new FullMatchExecutionBoundary(transport, ledger, FastBudget);
            var mapper = new PlayerTableProjectionMapper(clock);
            var factory = new TableCommandIntentFactory(clock);
            FullMatchActionPlan expected = ExpectedDrawPlan(baseline, clock, factory);

            boundary.Poll(baseline, mapper.Project(baseline), factory, 0L);
            transport.RaiseReceipt(RejectedReceipt(transport.Submitted[0], "STALE_STATE_VERSION"));
            SessionViewState terminalState = DrawState(
                binding,
                fence,
                6,
                106,
                baseline: false,
                roundEndedCommandVersion: 6,
                phase: GamePhase.RoundEnd);
            transport.Publish(terminalState);

            FullMatchExecutionOutcome outcome = boundary.Poll(
                terminalState,
                mapper.Project(terminalState),
                factory,
                FastBudget.ConfirmationTimeoutMs + 1L);

            Assert.Equal(FullMatchExecutionStatus.Replanned, outcome.Status);
            Assert.Equal("STALE_STATE_VERSION", outcome.ReceiptReason);
            Assert.NotEqual(FullMatchExecutionStatus.Confirmed, outcome.Status);
            Assert.False(ledger.IsUnresolved(expected));
            Assert.Equal(1, transport.SubmitCount);
        }

        [Fact]
        public void SentButUnresolvedReceiptTimesOutExactlyOnceAndNeverResendsTheSameId()
        {
            var clock = new ManualTableClock();
            object fence = NewFence();
            TableSessionBinding binding = P43TestData.Binding();
            SessionViewState baseline = DrawState(binding, fence, 5, 105, baseline: true);
            var transport = new ScriptedTransport(baseline);
            var ledger = new FullMatchRequestLedger();
            var boundary = new FullMatchExecutionBoundary(transport, ledger, FastBudget);
            var mapper = new PlayerTableProjectionMapper(clock);
            var factory = new TableCommandIntentFactory(clock);
            FullMatchActionPlan expected = ExpectedDrawPlan(baseline, clock, factory);

            boundary.Poll(baseline, mapper.Project(baseline), factory, 0L);
            Assert.Single(transport.Submitted);
            string requestId = transport.Submitted[0].RequestId.Value;

            FullMatchExecutionOutcome timeout = boundary.Poll(
                baseline,
                mapper.Project(baseline),
                factory,
                FastBudget.ConfirmationTimeoutMs + 1L);

            Assert.Equal(FullMatchExecutionStatus.Failed, timeout.Status);
            Assert.Equal("RECEIPT_UNRESOLVED", timeout.Code);
            Assert.Equal(requestId, expected.RequestId);
            Assert.True(ledger.IsUnresolved(expected));
            Assert.False(ledger.MayIssueNewRequestId(expected));
            Assert.Equal(1, transport.SubmitCount);

            FullMatchExecutionOutcome repeated = boundary.Poll(
                baseline,
                mapper.Project(baseline),
                factory,
                FastBudget.ConfirmationTimeoutMs + 60_000L);
            Assert.Equal("RECEIPT_UNRESOLVED", repeated.Code);
            Assert.Equal(1, transport.SubmitCount);
        }

        [Fact]
        public void NeverCompletingSubmitUsesTheBoundedSubmitBranchAndKeepsOneRequest()
        {
            var clock = new ManualTableClock();
            object fence = NewFence();
            TableSessionBinding binding = P43TestData.Binding();
            SessionViewState baseline = DrawState(binding, fence, 5, 105, baseline: true);
            var neverCompletes = new TaskCompletionSource<CommandSubmission>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var transport = new ScriptedTransport(baseline, _ => neverCompletes.Task);
            var ledger = new FullMatchRequestLedger();
            var boundary = new FullMatchExecutionBoundary(transport, ledger, FastBudget);
            var mapper = new PlayerTableProjectionMapper(clock);
            var factory = new TableCommandIntentFactory(clock);
            FullMatchActionPlan expected = ExpectedDrawPlan(baseline, clock, factory);

            FullMatchExecutionOutcome waiting = boundary.Poll(
                baseline,
                mapper.Project(baseline),
                factory,
                0L);
            Assert.Equal(FullMatchExecutionStatus.Waiting, waiting.Status);
            Assert.Equal(1, transport.SubmitCount);

            FullMatchExecutionOutcome timeout = boundary.Poll(
                baseline,
                mapper.Project(baseline),
                factory,
                FastBudget.SubmitTimeoutMs);

            Assert.Equal(FullMatchExecutionStatus.Failed, timeout.Status);
            Assert.Equal("SUBMIT_TIMEOUT", timeout.Code);
            Assert.True(ledger.IsUnresolved(expected));
            Assert.False(ledger.MayIssueNewRequestId(expected));
            Assert.Equal(1, transport.SubmitCount);
            Assert.Equal(expected.RequestId, transport.Submitted[0].RequestId.Value);

            FullMatchExecutionOutcome repeated = boundary.Poll(
                baseline,
                mapper.Project(baseline),
                factory,
                FastBudget.SubmitTimeoutMs + 60_000L);
            Assert.Equal("SUBMIT_TIMEOUT", repeated.Code);
            Assert.Equal(1, transport.SubmitCount);
        }

        [Fact]
        public void SubmissionThatThrowsSynchronouslyIsUnresolvedAndNotRetried()
        {
            var clock = new ManualTableClock();
            object fence = NewFence();
            TableSessionBinding binding = P43TestData.Binding();
            SessionViewState baseline = DrawState(binding, fence, 5, 105, baseline: true);
            var transport = new ScriptedTransport(
                baseline,
                _ => throw new InvalidOperationException("transport rejected the send"));
            var ledger = new FullMatchRequestLedger();
            var boundary = new FullMatchExecutionBoundary(transport, ledger, FastBudget);
            var mapper = new PlayerTableProjectionMapper(clock);
            var factory = new TableCommandIntentFactory(clock);
            FullMatchActionPlan expected = ExpectedDrawPlan(baseline, clock, factory);

            FullMatchExecutionOutcome outcome = boundary.Poll(
                baseline,
                mapper.Project(baseline),
                factory,
                0L);

            Assert.Equal(FullMatchExecutionStatus.Failed, outcome.Status);
            Assert.Equal("SUBMIT_NOT_SENT", outcome.Code);
            Assert.True(ledger.IsUnresolved(expected));
            Assert.Equal(1, transport.SubmitCount);
            Assert.Equal(
                "SUBMIT_NOT_SENT",
                boundary.Poll(baseline, mapper.Project(baseline), factory, 60_000L).Code);
            Assert.Equal(1, transport.SubmitCount);
        }

        [Fact]
        public void SubmissionNotHandedToTheTransportFailsClosedAsUnresolved()
        {
            var clock = new ManualTableClock();
            object fence = NewFence();
            TableSessionBinding binding = P43TestData.Binding();
            SessionViewState baseline = DrawState(binding, fence, 5, 105, baseline: true);
            var transport = new ScriptedTransport(
                baseline,
                command => Task.FromResult(NotSentSubmission(command)));
            var ledger = new FullMatchRequestLedger();
            var boundary = new FullMatchExecutionBoundary(transport, ledger, FastBudget);
            var mapper = new PlayerTableProjectionMapper(clock);
            var factory = new TableCommandIntentFactory(clock);
            FullMatchActionPlan expected = ExpectedDrawPlan(baseline, clock, factory);

            FullMatchExecutionOutcome outcome = boundary.Poll(
                baseline,
                mapper.Project(baseline),
                factory,
                0L);

            Assert.Equal(FullMatchExecutionStatus.Failed, outcome.Status);
            Assert.Equal("SUBMIT_NOT_SENT", outcome.Code);
            Assert.True(ledger.IsUnresolved(expected));
            Assert.Equal(1, transport.SubmitCount);
        }

        [Fact]
        public void RejectedReceiptIsADefinitiveNoOpAndIsNeverSubmittedAgain()
        {
            var clock = new ManualTableClock();
            object fence = NewFence();
            TableSessionBinding binding = P43TestData.Binding();
            SessionViewState baseline = DrawState(binding, fence, 5, 105, baseline: true);
            var transport = new ScriptedTransport(baseline);
            var ledger = new FullMatchRequestLedger();
            var boundary = new FullMatchExecutionBoundary(transport, ledger, FastBudget);
            var mapper = new PlayerTableProjectionMapper(clock);
            var factory = new TableCommandIntentFactory(clock);
            FullMatchActionPlan expected = ExpectedDrawPlan(baseline, clock, factory);

            boundary.Poll(baseline, mapper.Project(baseline), factory, 0L);
            transport.RaiseReceipt(RejectedReceipt(transport.Submitted[0], "STALE_STATE_VERSION"));

            FullMatchExecutionOutcome replanned = boundary.Poll(
                baseline,
                mapper.Project(baseline),
                factory,
                FastBudget.ConfirmationTimeoutMs + 1L);

            Assert.Equal(FullMatchExecutionStatus.Replanned, replanned.Status);
            Assert.Equal("STALE_STATE_VERSION", replanned.ReceiptReason);
            Assert.False(ledger.IsUnresolved(expected));
            Assert.Equal(1, transport.SubmitCount);

            FullMatchExecutionOutcome after = boundary.Poll(
                baseline,
                mapper.Project(baseline),
                factory,
                FastBudget.ConfirmationTimeoutMs + 2L);
            Assert.NotEqual(FullMatchExecutionStatus.Failed, after.Status);
            Assert.Equal(1, transport.SubmitCount);
        }

        [Fact]
        public void RejectedDrawAtATerminalEmptyWallIsANoOpAndWaitsForTheSettlement()
        {
            var clock = new ManualTableClock();
            object fence = NewFence();
            TableSessionBinding binding = P43TestData.Binding();
            SessionViewState baseline = DrawState(binding, fence, 5, 105, baseline: true);
            var transport = new ScriptedTransport(baseline);
            var ledger = new FullMatchRequestLedger();
            var boundary = new FullMatchExecutionBoundary(transport, ledger, FastBudget);
            var mapper = new PlayerTableProjectionMapper(clock);
            var factory = new TableCommandIntentFactory(clock);
            FullMatchActionPlan expected = ExpectedDrawPlan(baseline, clock, factory);

            boundary.Poll(baseline, mapper.Project(baseline), factory, 0L);
            transport.RaiseReceipt(RejectedReceipt(transport.Submitted[0], "ILLEGAL_PAYLOAD"));
            SessionViewState terminal = DrawState(
                binding,
                fence,
                6,
                106,
                baseline: false,
                roundEndedCommandVersion: 6,
                phase: GamePhase.RoundEnd,
                wallRemaining: 0);
            transport.Publish(terminal);

            FullMatchExecutionOutcome outcome = boundary.Poll(
                terminal,
                mapper.Project(terminal),
                factory,
                FastBudget.ConfirmationTimeoutMs + 1L);

            Assert.Equal(FullMatchExecutionStatus.Replanned, outcome.Status);
            Assert.Equal("ILLEGAL_PAYLOAD", outcome.ReceiptReason);
            Assert.False(ledger.IsUnresolved(expected));
            Assert.Equal(1, transport.SubmitCount);

            FullMatchExecutionOutcome complete = boundary.Poll(
                terminal,
                mapper.Project(terminal),
                factory,
                FastBudget.ConfirmationTimeoutMs + 2L);
            Assert.Equal(FullMatchExecutionStatus.RoundComplete, complete.Status);
            Assert.Equal(1, transport.SubmitCount);
        }

        [Fact]
        public void DefinitiveNonReplanableRejectionFailsClosedWithoutProgress()
        {
            var clock = new ManualTableClock();
            object fence = NewFence();
            TableSessionBinding binding = P43TestData.Binding();
            SessionViewState baseline = DrawState(binding, fence, 5, 105, baseline: true);
            var transport = new ScriptedTransport(baseline);
            var ledger = new FullMatchRequestLedger();
            var boundary = new FullMatchExecutionBoundary(transport, ledger, FastBudget);
            var mapper = new PlayerTableProjectionMapper(clock);
            var factory = new TableCommandIntentFactory(clock);
            FullMatchActionPlan expected = ExpectedDrawPlan(baseline, clock, factory);

            boundary.Poll(baseline, mapper.Project(baseline), factory, 0L);
            transport.RaiseReceipt(RejectedReceipt(transport.Submitted[0], "ILLEGAL_PAYLOAD"));

            FullMatchExecutionOutcome outcome = boundary.Poll(
                baseline,
                mapper.Project(baseline),
                factory,
                FastBudget.ConfirmationTimeoutMs + 1L);

            Assert.Equal(FullMatchExecutionStatus.Failed, outcome.Status);
            Assert.Equal("ACTION_REJECTED", outcome.Code);
            Assert.False(ledger.IsUnresolved(expected));
            Assert.Equal(1, transport.SubmitCount);
            Assert.Equal(5UL, mapper.Project(baseline).Identity.StateVersion);
        }

        [Fact]
        public void AppliedReceiptForADifferentRequestNeverConfirms()
        {
            var clock = new ManualTableClock();
            object fence = NewFence();
            TableSessionBinding binding = P43TestData.Binding();
            SessionViewState baseline = DrawState(binding, fence, 5, 105, baseline: true);
            var transport = new ScriptedTransport(baseline);
            var ledger = new FullMatchRequestLedger();
            var boundary = new FullMatchExecutionBoundary(transport, ledger, FastBudget);
            var mapper = new PlayerTableProjectionMapper(clock);
            var factory = new TableCommandIntentFactory(clock);
            FullMatchActionPlan expected = ExpectedDrawPlan(baseline, clock, factory);

            boundary.Poll(baseline, mapper.Project(baseline), factory, 0L);
            transport.RaiseReceipt(AppliedReceipt(
                transport.Submitted[0],
                evaluated: 5,
                commandVersion: 6,
                eventSeq: 106,
                requestIdOverride: "fm-someone-else"));
            SessionViewState state = DrawState(
                binding,
                fence,
                6,
                106,
                baseline: false,
                roundEndedCommandVersion: 6,
                phase: GamePhase.RoundEnd);
            transport.Publish(state);

            FullMatchExecutionOutcome outcome = boundary.Poll(
                state,
                mapper.Project(state),
                factory,
                FastBudget.ConfirmationTimeoutMs + 1L);

            Assert.Equal(FullMatchExecutionStatus.Failed, outcome.Status);
            Assert.Equal("RECEIPT_UNRESOLVED", outcome.Code);
            Assert.True(ledger.IsUnresolved(expected));
            Assert.Equal(1, transport.SubmitCount);
        }

        [Theory]
        [InlineData("recipient-seat")]
        [InlineData("round")]
        [InlineData("binding")]
        [InlineData("scope")]
        [InlineData("command-version")]
        public void WrongBatchIdentityNeverConfirmsTheAppliedRequest(string mutation)
        {
            var clock = new ManualTableClock();
            object fence = NewFence();
            TableSessionBinding binding = P43TestData.Binding();
            SessionViewState baseline = DrawState(binding, fence, 5, 105, baseline: true);
            var transport = new ScriptedTransport(baseline);
            var ledger = new FullMatchRequestLedger();
            var boundary = new FullMatchExecutionBoundary(transport, ledger, FastBudget);
            var mapper = new PlayerTableProjectionMapper(clock);
            var factory = new TableCommandIntentFactory(clock);
            FullMatchActionPlan expected = ExpectedDrawPlan(baseline, clock, factory);

            boundary.Poll(baseline, mapper.Project(baseline), factory, 0L);
            bool wrongVersion = mutation == "command-version";
            transport.RaiseReceipt(AppliedReceipt(
                transport.Submitted[0],
                evaluated: wrongVersion ? 4UL : 5UL,
                commandVersion: wrongVersion ? 5UL : 6UL,
                eventSeq: 106));

            const ulong eventVersion = 6UL;
            TableSessionBinding mutated = mutation switch
            {
                "recipient-seat" => P43TestData.Binding(seat: 1),
                "round" => binding,
                "binding" => P43TestData.Binding(bindingEpoch: P43TestData.BindingEpoch + 1),
                "scope" => P43TestData.Binding(scopeId: "scope-other"),
                _ => binding
            };
            string roundId = mutation == "round" ? "round-other" : P43TestData.RoundId;
            SessionViewState state = DrawState(
                mutated,
                fence,
                6,
                106,
                baseline: false,
                roundEndedCommandVersion: eventVersion,
                phase: GamePhase.RoundEnd,
                roundId: roundId);
            transport.Publish(state);

            FullMatchExecutionOutcome outcome = boundary.Poll(
                state,
                mapper.Project(state),
                factory,
                FastBudget.ConfirmationTimeoutMs + 1L);

            Assert.Equal(FullMatchExecutionStatus.Failed, outcome.Status);
            Assert.Equal("VISIBLE_EVENT_MISSING", outcome.Code);
            Assert.True(ledger.IsUnresolved(expected));
            Assert.Equal(1, transport.SubmitCount);
        }

        [Fact]
        public void SpectatorRoleBatchNeverConfirmsAPlayerRequest()
        {
            var clock = new ManualTableClock();
            object fence = NewFence();
            TableSessionBinding binding = P43TestData.Binding();
            SessionViewState baseline = DrawState(binding, fence, 5, 105, baseline: true);
            var transport = new ScriptedTransport(baseline);
            var ledger = new FullMatchRequestLedger();
            var boundary = new FullMatchExecutionBoundary(transport, ledger, FastBudget);
            var mapper = new PlayerTableProjectionMapper(clock);
            var factory = new TableCommandIntentFactory(clock);
            FullMatchActionPlan expected = ExpectedDrawPlan(baseline, clock, factory);

            boundary.Poll(baseline, mapper.Project(baseline), factory, 0L);
            transport.RaiseReceipt(AppliedReceipt(
                transport.Submitted[0],
                evaluated: 5,
                commandVersion: 6,
                eventSeq: 106));

            TableSessionBinding spectator = P43TestData.Binding();
            TableVisibleEventBatch spectatorBatch = Project(
                spectator,
                fence,
                6,
                106,
                P43TestData.RoundId,
                baseline: false,
                RoundEndedEvent(106, 6));
            ForceRoleForFaultInjection(spectatorBatch, ProtocolRole.Spectator);
            WirePlayerSnapshot snapshot = P43TestData.PlayerSnapshot(
                stateVersion: 6,
                eventSeq: 106,
                includeDecision: false);
            snapshot.PublicView.Board.Phase = GamePhase.RoundEnd;
            SessionViewState state = View(binding, snapshot, spectatorBatch, 2);
            transport.Publish(state);

            FullMatchExecutionOutcome outcome = boundary.Poll(
                state,
                mapper.Project(state),
                factory,
                FastBudget.ConfirmationTimeoutMs + 1L);

            Assert.Equal(FullMatchExecutionStatus.Failed, outcome.Status);
            Assert.Equal("VISIBLE_EVENT_MISSING", outcome.Code);
            Assert.True(ledger.IsUnresolved(expected));
            Assert.Equal(1, transport.SubmitCount);
        }

        [Fact]
        public void ResponsePassConfirmsAsAStateOnlyActionWithTheExactAppliedReceipt()
        {
            var clock = new ManualTableClock();
            object fence = NewFence();
            TableSessionBinding binding = P43TestData.Binding();
            WirePlayerSnapshot snapshot = P43TestData.PlayerSnapshot(
                stateVersion: 5,
                eventSeq: 105,
                includeDecision: false);
            snapshot.PrivateView.Decision = new WireDecisionView
            {
                DecisionId = "decision-pass",
                ControlEpoch = 7,
                DeadlineRemainingMs = 8_000
            };
            snapshot.PrivateView.Decision.Actions.Add(new WireLegalActionView
            {
                ActionType = (uint)ProtocolActionType.Pass,
                OptionId = "pass"
            });
            TableVisibleEventBatch baselineBatch = Project(
                binding, fence, 5, 105, P43TestData.RoundId, baseline: true);
            SessionViewState baseline = View(binding, snapshot, baselineBatch, 1);
            var transport = new ScriptedTransport(baseline);
            var ledger = new FullMatchRequestLedger();
            var boundary = new FullMatchExecutionBoundary(transport, ledger, FastBudget);
            var mapper = new PlayerTableProjectionMapper(clock);
            var factory = new TableCommandIntentFactory(clock);
            PlayerTableViewState view = mapper.Project(baseline);

            Assert.True(LocalFullMatchPolicy.TryCreateActionPlan(
                baseline,
                view,
                factory,
                clock.MonotonicMilliseconds,
                out FullMatchActionPlan? expected,
                out _,
                out _));
            Assert.Equal(PlayerActionKind.Pass, expected!.Action);
            Assert.False(expected.RequiresVisibleEvent);

            boundary.Poll(baseline, view, factory, 0L);
            transport.RaiseReceipt(AppliedReceipt(transport.Submitted[0], 5, 6, 106));

            FullMatchExecutionOutcome confirmed = boundary.Poll(
                baseline,
                view,
                factory,
                10L);

            Assert.Equal(FullMatchExecutionStatus.Confirmed, confirmed.Status);
            Assert.False(confirmed.ConfirmedByVisibleEvent);
            Assert.False(ledger.IsUnresolved(expected));
            Assert.Equal(1, transport.SubmitCount);
        }

        [Fact]
        public void OwnDiscardWithoutADecisionExpiresAtTheBoundedDecisionWaitWithoutSubmitting()
        {
            var clock = new ManualTableClock();
            object fence = NewFence();
            TableSessionBinding binding = P43TestData.Binding();
            WirePlayerSnapshot snapshot = P43TestData.PlayerSnapshot(
                stateVersion: 5,
                eventSeq: 105,
                includeDecision: false);
            snapshot.PublicView.Board.Phase = GamePhase.Discard;
            snapshot.PublicView.Board.CurrentSeat = 0;
            TableVisibleEventBatch batch = Project(
                binding, fence, 5, 105, P43TestData.RoundId, baseline: true);
            SessionViewState state = View(binding, snapshot, batch, 1);
            var transport = new ScriptedTransport(state);
            var boundary = new FullMatchExecutionBoundary(transport, new FullMatchRequestLedger(), FastBudget);
            var mapper = new PlayerTableProjectionMapper(clock);
            var factory = new TableCommandIntentFactory(clock);
            PlayerTableViewState view = mapper.Project(state);

            Assert.Null(view.Own?.Decision);
            Assert.Equal(FullMatchExecutionStatus.Waiting, boundary.Poll(state, view, factory, 0L).Status);
            Assert.Equal(
                FullMatchExecutionStatus.Waiting,
                boundary.Poll(state, view, factory, FastBudget.DecisionWaitTimeoutMs - 1L).Status);

            FullMatchExecutionOutcome expired = boundary.Poll(
                state,
                view,
                factory,
                FastBudget.DecisionWaitTimeoutMs);

            Assert.Equal(FullMatchExecutionStatus.Failed, expired.Status);
            Assert.Equal("DECISION_WAIT_EXPIRED", expired.Code);
            Assert.Equal(0, transport.SubmitCount);
            Assert.Equal(
                "DECISION_WAIT_EXPIRED",
                boundary.Poll(
                    state,
                    view,
                    factory,
                    FastBudget.DecisionWaitTimeoutMs + 60_000L).Code);
            Assert.Equal(0, transport.SubmitCount);
        }

        [Fact]
        public void DecisionWaitDoesNotExpireWhenTheSeatIsAutomatedOrItIsNotOurTurn()
        {
            var clock = new ManualTableClock();
            object fence = NewFence();
            TableSessionBinding binding = P43TestData.Binding();
            WirePlayerSnapshot automated = P43TestData.PlayerSnapshot(
                stateVersion: 5,
                eventSeq: 105,
                includeDecision: false,
                controlledByComputer: true);
            automated.PublicView.Board.Phase = GamePhase.Discard;
            automated.PublicView.Board.CurrentSeat = 0;
            TableVisibleEventBatch batch = Project(
                binding, fence, 5, 105, P43TestData.RoundId, baseline: true);
            SessionViewState automatedState = View(binding, automated, batch, 1);
            var transport = new ScriptedTransport(automatedState);
            var boundary = new FullMatchExecutionBoundary(transport, new FullMatchRequestLedger(), FastBudget);
            var mapper = new PlayerTableProjectionMapper(clock);
            var factory = new TableCommandIntentFactory(clock);
            PlayerTableViewState automatedView = mapper.Project(automatedState);

            Assert.Equal(
                FullMatchExecutionStatus.Waiting,
                boundary.Poll(
                    automatedState,
                    automatedView,
                    factory,
                    FastBudget.DecisionWaitTimeoutMs + 60_000L).Status);
            Assert.Equal(0, transport.SubmitCount);
        }

        [Fact]
        public void TerminalGateWaitsForDelayedAppliedReceiptThenCommitsOnce()
        {
            GateFixture fixture = NewGateFixture();
            SessionViewState terminal = TerminalState(fixture);
            fixture.Transport.Publish(terminal);
            var sink = new CommitSink();

            FullMatchTerminalGateResult waiting = PollTerminalGate(fixture, terminal, 0L);
            Assert.Equal(FullMatchTerminalGateStatus.Waiting, waiting.Status);
            Assert.False(waiting.MayCommit);
            Assert.Equal(0, sink.Commits);

            fixture.Transport.RaiseReceipt(AppliedReceipt(
                fixture.Transport.Submitted[0], evaluated: 5, commandVersion: 6, eventSeq: 106));
            FullMatchTerminalGateResult confirmed = PollTerminalGate(fixture, terminal, 10L);

            Assert.Equal(FullMatchTerminalGateStatus.Confirmed, confirmed.Status);
            Assert.True(confirmed.MayCommit);
            Assert.True(confirmed.Outcome!.ConfirmedByVisibleEvent);
            sink.Commit();
            sink.OpenNext();
            Assert.Equal(1, sink.Commits);
            Assert.Equal(1, sink.NextOpens);
            Assert.Equal(1, fixture.Transport.SubmitCount);
            Assert.False(fixture.Ledger.IsUnresolved(fixture.Plan));
            Assert.Equal(0, fixture.Transport.ReceiptHandlerCount);
            Assert.Contains(fixture.Transport.Notices, n =>
                n.Kind == FullMatchExecutionNoticeKind.ActionConfirmed &&
                n.Plan.RequestId == fixture.Plan.RequestId);
        }

        [Fact]
        public void TerminalGateMissingReceiptFailsBoundedWithZeroCommitAndPreservesIdentity()
        {
            GateFixture fixture = NewGateFixture();
            SessionViewState terminal = TerminalState(fixture);
            fixture.Transport.Publish(terminal);
            var sink = new CommitSink();

            FullMatchTerminalGateResult gate = PollTerminalGate(
                fixture, terminal, FastBudget.ConfirmationTimeoutMs + 1L);

            Assert.Equal(FullMatchTerminalGateStatus.Failed, gate.Status);
            Assert.True(gate.MustFail);
            Assert.False(gate.MayCommit);
            Assert.Equal("RECEIPT_UNRESOLVED", gate.Code);
            Assert.Equal(0, sink.Commits);
            Assert.Equal(0, sink.NextOpens);
            Assert.Equal(1, fixture.Transport.SubmitCount);
            Assert.True(fixture.Ledger.IsUnresolved(fixture.Plan));
            Assert.Equal(fixture.Plan.RequestId, fixture.Transport.Submitted[0].RequestId.Value);

            fixture.Boundary.Dispose();
            Assert.True(fixture.Ledger.IsUnresolved(fixture.Plan));
            Assert.False(fixture.Ledger.MayIssueNewRequestId(fixture.Plan));
            Assert.Equal(0, fixture.Transport.ReceiptHandlerCount);
        }

        [Fact]
        public void TerminalGateNeverCompletingSubmitFailsBoundedWithoutCommit()
        {
            var neverCompletes = new TaskCompletionSource<CommandSubmission>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            GateFixture fixture = NewGateFixture(_ => neverCompletes.Task);
            SessionViewState terminal = TerminalState(fixture);
            fixture.Transport.Publish(terminal);
            var sink = new CommitSink();

            FullMatchTerminalGateResult gate = PollTerminalGate(
                fixture, terminal, FastBudget.SubmitTimeoutMs);

            Assert.Equal(FullMatchTerminalGateStatus.Failed, gate.Status);
            Assert.Equal("SUBMIT_TIMEOUT", gate.Code);
            Assert.Equal(0, sink.Commits);
            Assert.Equal(0, sink.NextOpens);
            Assert.Equal(1, fixture.Transport.SubmitCount);
            Assert.True(fixture.Ledger.IsUnresolved(fixture.Plan));

            fixture.Boundary.Dispose();
            Assert.True(fixture.Ledger.IsUnresolved(fixture.Plan));
            Assert.False(fixture.Ledger.MayIssueNewRequestId(fixture.Plan));
            Assert.Equal(0, fixture.Transport.ReceiptHandlerCount);
        }

        [Theory]
        [InlineData("request")]
        [InlineData("command-version")]
        [InlineData("round")]
        [InlineData("recipient-seat")]
        [InlineData("role")]
        [InlineData("binding")]
        [InlineData("scope")]
        public void TerminalGateRejectsMismatchedIdentityWithZeroCommit(string mutation)
        {
            GateFixture fixture = NewGateFixture();
            TableSessionBinding binding = mutation switch
            {
                "recipient-seat" => P43TestData.Binding(seat: 1),
                "binding" => P43TestData.Binding(bindingEpoch: P43TestData.BindingEpoch + 1),
                "scope" => P43TestData.Binding(scopeId: "scope-other"),
                _ => fixture.Binding
            };
            string roundId = mutation == "round" ? "round-other" : P43TestData.RoundId;
            SessionViewState terminal = DrawState(
                binding,
                fixture.Fence,
                6,
                106,
                baseline: false,
                roundEndedCommandVersion: 6,
                phase: GamePhase.RoundEnd,
                roundId: roundId);
            if (mutation == "role")
            {
                ForceRoleForFaultInjection(terminal.VisibleEventBatch, ProtocolRole.Spectator);
            }

            fixture.Transport.Publish(terminal);
            bool wrongVersion = mutation == "command-version";
            fixture.Transport.RaiseReceipt(AppliedReceipt(
                fixture.Transport.Submitted[0],
                evaluated: wrongVersion ? 4UL : 5UL,
                commandVersion: wrongVersion ? 5UL : 6UL,
                eventSeq: 106,
                requestIdOverride: mutation == "request" ? "fm-someone-else" : null));
            var sink = new CommitSink();

            FullMatchTerminalGateResult gate = PollTerminalGate(
                fixture, terminal, FastBudget.ConfirmationTimeoutMs + 1L);

            Assert.Equal(FullMatchTerminalGateStatus.Failed, gate.Status);
            Assert.False(gate.MayCommit);
            Assert.Equal(0, sink.Commits);
            Assert.Equal(0, sink.NextOpens);
            Assert.Equal(1, fixture.Transport.SubmitCount);
            Assert.True(fixture.Ledger.IsUnresolved(fixture.Plan));
            Assert.Equal(fixture.Plan.RequestId, fixture.Transport.Submitted[0].RequestId.Value);

            fixture.Boundary.Dispose();
            Assert.True(fixture.Ledger.IsUnresolved(fixture.Plan));
            Assert.False(fixture.Ledger.MayIssueNewRequestId(fixture.Plan));
            Assert.Equal(0, fixture.Transport.ReceiptHandlerCount);
        }

        [Fact]
        public void TerminalGateDefinitiveRejectionIsANoOpAndNeverConfirmed()
        {
            GateFixture fixture = NewGateFixture();
            SessionViewState terminal = TerminalState(fixture);
            fixture.Transport.Publish(terminal);
            fixture.Transport.RaiseReceipt(RejectedReceipt(
                fixture.Transport.Submitted[0], "STALE_STATE_VERSION"));
            var sink = new CommitSink();

            FullMatchTerminalGateResult gate = PollTerminalGate(
                fixture, terminal, FastBudget.ConfirmationTimeoutMs + 1L);

            Assert.Equal(FullMatchTerminalGateStatus.Replanned, gate.Status);
            Assert.True(gate.MayCommit);
            Assert.NotEqual(FullMatchTerminalGateStatus.Confirmed, gate.Status);
            Assert.DoesNotContain(fixture.Transport.Notices, n =>
                n.Kind == FullMatchExecutionNoticeKind.ActionConfirmed);
            Assert.False(fixture.Ledger.IsUnresolved(fixture.Plan));
            Assert.Equal(1, fixture.Transport.SubmitCount);
            Assert.Equal(0, fixture.Transport.ReceiptHandlerCount);
            sink.Commit();
            sink.OpenNext();
            Assert.Equal(1, sink.Commits);
            Assert.Equal(1, sink.NextOpens);
        }

        [Fact]
        public void TerminalGateWithNoActiveOwnRequestCommitsNormally()
        {
            FullMatchTerminalGateResult gate = FullMatchTerminalCommitGate.Poll(
                null,
                null!,
                null!,
                null!,
                0L);
            Assert.Equal(FullMatchTerminalGateStatus.NoActiveRequest, gate.Status);
            Assert.True(gate.MayCommit);
        }

        [Fact]
        public void DisposedActiveRequestRemainsUnresolvedAndNotReissuable()
        {
            GateFixture fixture = NewGateFixture();
            Assert.True(fixture.Boundary.HasActiveRequest);

            fixture.Boundary.Dispose();

            Assert.True(fixture.Ledger.IsUnresolved(fixture.Plan));
            Assert.False(fixture.Ledger.MayIssueNewRequestId(fixture.Plan));
            Assert.False(fixture.Boundary.HasActiveRequest);
            Assert.Equal(0, fixture.Transport.ReceiptHandlerCount);
        }

        [Fact]
        public void SentWithoutReceiptReconcilesTheOriginalRequestAndConfirms()
        {
            var clock = new ManualTableClock();
            object fence = NewFence();
            TableSessionBinding binding = P43TestData.Binding();
            SessionViewState baseline = DrawState(binding, fence, 5, 105, baseline: true);
            var transport = new ScriptedTransport(baseline);
            var ledger = new FullMatchRequestLedger();
            var boundary = new FullMatchExecutionBoundary(
                transport,
                ledger,
                new FullMatchExecutionBudget(1_000L, 2_000L, 1_000L, 100L));
            var mapper = new PlayerTableProjectionMapper(clock);
            var factory = new TableCommandIntentFactory(clock);
            FullMatchActionPlan expected = ExpectedDrawPlan(baseline, clock, factory);
            SessionViewState terminal = DrawState(
                binding,
                fence,
                6,
                106,
                baseline: false,
                roundEndedCommandVersion: 6,
                phase: GamePhase.RoundEnd);

            boundary.Poll(baseline, mapper.Project(baseline), factory, 0L);
            Assert.Equal(1, transport.SubmitCount);
            transport.Publish(terminal);
            transport.Reconcile = _ =>
            {
                transport.RaiseReceipt(AppliedReceipt(
                    transport.Submitted[0], evaluated: 5, commandVersion: 6, eventSeq: 106));
                return Task.CompletedTask;
            };

            FullMatchExecutionOutcome outcome = boundary.Poll(
                terminal,
                mapper.Project(terminal),
                factory,
                100L);

            Assert.Equal(FullMatchExecutionStatus.Confirmed, outcome.Status);
            Assert.Equal(1, transport.SubmitCount);
            Assert.Equal(1, transport.ReconcileCalls);
            Assert.Equal(expected.RequestId, transport.Submitted[0].RequestId.Value);
            Assert.False(ledger.IsUnresolved(expected));
            Assert.Equal(0, transport.ReceiptHandlerCount);
        }

        [Fact]
        public void ReconciledButStillUnresolvedFailsBoundedWithoutResendOrNewId()
        {
            var clock = new ManualTableClock();
            object fence = NewFence();
            TableSessionBinding binding = P43TestData.Binding();
            SessionViewState baseline = DrawState(binding, fence, 5, 105, baseline: true);
            var transport = new ScriptedTransport(baseline);
            var ledger = new FullMatchRequestLedger();
            var boundary = new FullMatchExecutionBoundary(
                transport,
                ledger,
                new FullMatchExecutionBudget(1_000L, 2_000L, 1_000L, 100L));
            var mapper = new PlayerTableProjectionMapper(clock);
            var factory = new TableCommandIntentFactory(clock);
            FullMatchActionPlan expected = ExpectedDrawPlan(baseline, clock, factory);
            SessionViewState terminal = DrawState(
                binding,
                fence,
                6,
                106,
                baseline: false,
                roundEndedCommandVersion: 6,
                phase: GamePhase.RoundEnd);

            boundary.Poll(baseline, mapper.Project(baseline), factory, 0L);
            transport.Publish(terminal);
            transport.Reconcile = _ => Task.CompletedTask;

            FullMatchExecutionOutcome waiting = boundary.Poll(
                terminal,
                mapper.Project(terminal),
                factory,
                100L);
            Assert.Equal(FullMatchExecutionStatus.Waiting, waiting.Status);
            Assert.Equal(1, transport.ReconcileCalls);

            FullMatchExecutionOutcome failed = boundary.Poll(
                terminal,
                mapper.Project(terminal),
                factory,
                2_001L);
            Assert.Equal(FullMatchExecutionStatus.Failed, failed.Status);
            Assert.Equal("RECEIPT_UNRESOLVED", failed.Code);
            Assert.Equal(1, transport.SubmitCount);
            Assert.Equal(1, transport.ReconcileCalls);
            Assert.Equal(expected.RequestId, transport.Submitted[0].RequestId.Value);
            Assert.True(ledger.IsUnresolved(expected));
            Assert.False(ledger.MayIssueNewRequestId(expected));
        }

        private static FullMatchTerminalGateResult PollTerminalGate(
            GateFixture fixture,
            SessionViewState terminal,
            long nowMs)
        {
            return FullMatchTerminalCommitGate.Poll(
                fixture.Boundary,
                terminal,
                fixture.Mapper.Project(terminal),
                fixture.Factory,
                nowMs);
        }

        private static SessionViewState TerminalState(GateFixture fixture)
        {
            return DrawState(
                fixture.Binding,
                fixture.Fence,
                6,
                106,
                baseline: false,
                roundEndedCommandVersion: 6,
                phase: GamePhase.RoundEnd);
        }

        private static GateFixture NewGateFixture(
            Func<ProtocolCommand, Task<CommandSubmission>>? submit = null)
        {
            var clock = new ManualTableClock();
            object fence = NewFence();
            TableSessionBinding binding = P43TestData.Binding();
            SessionViewState baseline = DrawState(
                binding, fence, 5, 105, baseline: true);
            var transport = new ScriptedTransport(baseline, submit);
            var ledger = new FullMatchRequestLedger();
            var boundary = new FullMatchExecutionBoundary(
                transport,
                ledger,
                FastBudget,
                transport.Notices.Add);
            var mapper = new PlayerTableProjectionMapper(clock);
            var factory = new TableCommandIntentFactory(clock);
            FullMatchActionPlan plan = ExpectedDrawPlan(baseline, clock, factory);
            FullMatchExecutionOutcome first = boundary.Poll(
                baseline,
                mapper.Project(baseline),
                factory,
                0L);
            Assert.Equal(FullMatchExecutionStatus.Waiting, first.Status);
            Assert.Equal(1, transport.SubmitCount);
            return new GateFixture(
                clock, fence, binding, transport, ledger, boundary, mapper, factory, plan);
        }

        private sealed class GateFixture
        {
            public GateFixture(
                ManualTableClock clock,
                object fence,
                TableSessionBinding binding,
                ScriptedTransport transport,
                FullMatchRequestLedger ledger,
                FullMatchExecutionBoundary boundary,
                PlayerTableProjectionMapper mapper,
                TableCommandIntentFactory factory,
                FullMatchActionPlan plan)
            {
                Clock = clock;
                Fence = fence;
                Binding = binding;
                Transport = transport;
                Ledger = ledger;
                Boundary = boundary;
                Mapper = mapper;
                Factory = factory;
                Plan = plan;
            }

            public ManualTableClock Clock { get; }
            public object Fence { get; }
            public TableSessionBinding Binding { get; }
            public ScriptedTransport Transport { get; }
            public FullMatchRequestLedger Ledger { get; }
            public FullMatchExecutionBoundary Boundary { get; }
            public PlayerTableProjectionMapper Mapper { get; }
            public TableCommandIntentFactory Factory { get; }
            public FullMatchActionPlan Plan { get; }
        }

        private sealed class CommitSink
        {
            public int Commits { get; private set; }
            public int NextOpens { get; private set; }
            public void Commit() => Commits++;
            public void OpenNext() => NextOpens++;
        }

        private static SessionViewState DrawState(
            TableSessionBinding binding,
            object fence,
            ulong stateVersion,
            ulong eventSeq,
            bool baseline,
            ulong? roundEndedCommandVersion = null,
            GamePhase phase = GamePhase.Draw,
            string roundId = P43TestData.RoundId,
            uint wallRemaining = 69U)
        {
            WirePlayerSnapshot snapshot = P43TestData.PlayerSnapshot(
                stateVersion: stateVersion,
                eventSeq: eventSeq,
                roundId: roundId,
                includeDecision: false);
            snapshot.PublicView.Board.Phase = phase;
            snapshot.PublicView.Board.CurrentSeat = 0;
            snapshot.PublicView.Board.WallRemaining = wallRemaining;
            if (phase == GamePhase.RoundEnd)
            {
                snapshot.PublicView.SettlementRoundId = roundId;
                snapshot.PublicView.Session.FinalScores.Clear();
                for (int seat = 0; seat < 4; seat++)
                {
                    snapshot.PublicView.Session.FinalScores.Add(new WireScore { Seat = seat, Score = 25_000 });
                }
            }

            var events = new List<EventEnvelope>();
            if (roundEndedCommandVersion.HasValue)
            {
                events.Add(RoundEndedEvent(eventSeq, roundEndedCommandVersion.Value));
            }

            TableVisibleEventBatch batch = Project(
                binding,
                fence,
                stateVersion,
                eventSeq,
                roundId,
                baseline,
                events.ToArray());
            return View(binding, snapshot, batch, (long)stateVersion);
        }

        private static EventEnvelope RoundEndedEvent(ulong eventSeq, ulong commandVersion)
        {
            return new EventEnvelope
            {
                EventSchemaVersion = ProtocolConstants.EventSchemaVersion,
                EventSeq = eventSeq,
                CommandVersion = commandVersion,
                EventOrdinal = 0,
                EventType = EventType.RoundEnded,
                Visibility = EventVisibility.Public,
                RoundEnded = new RoundEnded
                {
                    TurnNumber = 1,
                    RoundWind = 1,
                    RoundNumber = 1
                }
            };
        }

        private static FullMatchActionPlan ExpectedDrawPlan(
            SessionViewState session,
            ManualTableClock clock,
            TableCommandIntentFactory factory)
        {
            Assert.True(LocalFullMatchPolicy.TryCreateMandatoryDrawPlan(
                session,
                new PlayerTableProjectionMapper(clock).Project(session),
                clock.MonotonicMilliseconds,
                out FullMatchActionPlan? plan,
                out string code,
                out string detail));
            return plan!;
        }

        private static object NewFence()
        {
            return Activator.CreateInstance(FenceType, nonPublic: true)
                ?? throw new InvalidOperationException("Could not construct the production visible-event fence.");
        }

        /// <summary>
        /// Fault-injects a role mismatch on an otherwise exact recipient batch.
        /// The real Client never produces a spectator player-stream frame; this
        /// isolates the role guard from the seat guard for the negative matrix.
        /// </summary>
        private static void ForceRoleForFaultInjection(TableVisibleEventBatch batch, ProtocolRole role)
        {
            FieldInfo? field = typeof(TableVisibleEventBatch).GetField(
                "<Role>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field!.SetValue(batch, role);
        }

        private static TableVisibleEventBatch Project(
            TableSessionBinding binding,
            object fence,
            ulong stateVersion,
            ulong eventSeq,
            string roundId,
            bool baseline,
            params EventEnvelope[] events)
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
                    (IReadOnlyList<EventEnvelope>)events,
                    fence
                })!;
        }

        private static SessionViewState View(
            TableSessionBinding binding,
            WirePlayerSnapshot player,
            TableVisibleEventBatch batch,
            long acceptanceSequence)
        {
            return new SessionViewState(
                1,
                TableConnectionState.Ready,
                binding,
                player,
                null,
                null,
                null,
                null,
                batch.IsFullBaseline,
                new ContractCursor(new StreamVersion(5), new ProjectionSeq(player.EventSeq + 10UL)),
                batch,
                acceptanceSequence);
        }

        private static TableReceiptResolvedEventArgs AppliedReceipt(
            ProtocolCommand command,
            ulong evaluated,
            ulong commandVersion,
            ulong eventSeq,
            string? requestIdOverride = null)
        {
            var key = new IdempotencyKey(
                "local-player",
                command.ScopeId!.Value,
                new RequestId(requestIdOverride ?? command.RequestId.Value));
            var pending = new PendingCommandSnapshot(
                key,
                command.Kind,
                "digest",
                command.Payload.Length,
                true,
                false,
                false,
                null,
                null,
                false);
            OutboxResolution resolution = OutboxResolution.FromReceipt(
                ClientReceiptStatus.Applied,
                string.Empty,
                new StateVersion(evaluated),
                new StateVersion(commandVersion),
                new EventSeq(eventSeq),
                1,
                "digest",
                false);
            return new TableReceiptResolvedEventArgs(pending, resolution);
        }

        private static TableReceiptResolvedEventArgs RejectedReceipt(
            ProtocolCommand command,
            string reason)
        {
            var key = new IdempotencyKey("local-player", command.ScopeId!.Value, command.RequestId);
            var pending = new PendingCommandSnapshot(
                key,
                command.Kind,
                "digest",
                command.Payload.Length,
                true,
                false,
                false,
                null,
                null,
                false);
            OutboxResolution resolution = OutboxResolution.FromReceipt(
                ClientReceiptStatus.Rejected,
                reason,
                new StateVersion(5),
                null,
                null,
                1,
                "digest",
                false);
            return new TableReceiptResolvedEventArgs(pending, resolution);
        }

        private static CommandSubmission SentSubmission(ProtocolCommand command)
        {
            var key = new IdempotencyKey("local-player", command.ScopeId!.Value, command.RequestId);
            var pending = new PendingCommandSnapshot(
                key,
                command.Kind,
                "digest",
                command.Payload.Length,
                true,
                false,
                false,
                null,
                null,
                false);
            return new CommandSubmission(pending, sent: true, wasDuplicate: false);
        }

        private static CommandSubmission NotSentSubmission(ProtocolCommand command)
        {
            var key = new IdempotencyKey("local-player", command.ScopeId!.Value, command.RequestId);
            var pending = new PendingCommandSnapshot(
                key,
                command.Kind,
                "digest",
                command.Payload.Length,
                false,
                false,
                false,
                null,
                null,
                false);
            return new CommandSubmission(pending, sent: false, wasDuplicate: false);
        }

        private sealed class ScriptedTransport : IFullMatchExecutionTransport
        {
            private readonly FakeSessionView _view;
            private readonly Func<ProtocolCommand, Task<CommandSubmission>> _submit;

            public ScriptedTransport(
                SessionViewState initial,
                Func<ProtocolCommand, Task<CommandSubmission>>? submit = null)
            {
                _view = new FakeSessionView(initial);
                _submit = submit ?? (command => Task.FromResult(SentSubmission(command)));
            }

            public ISessionView View => _view;

            public List<ProtocolCommand> Submitted { get; } = new List<ProtocolCommand>();

            public List<FullMatchExecutionNotice> Notices { get; } = new List<FullMatchExecutionNotice>();

            public int SubmitCount => Submitted.Count;

            public Func<CancellationToken, Task>? Reconcile { get; set; }

            public int ReconcileCalls { get; private set; }

            public Task ReconcileAsync(CancellationToken cancellationToken)
            {
                ReconcileCalls++;
                return Reconcile != null ? Reconcile(cancellationToken) : Task.CompletedTask;
            }

            private readonly List<EventHandler<TableReceiptResolvedEventArgs>> _receiptHandlers =
                new List<EventHandler<TableReceiptResolvedEventArgs>>();

            public int ReceiptHandlerCount => _receiptHandlers.Count;

            public event EventHandler<TableReceiptResolvedEventArgs>? ReceiptResolved
            {
                add
                {
                    if (value != null)
                    {
                        _receiptHandlers.Add(value);
                    }
                }

                remove
                {
                    if (value != null)
                    {
                        _receiptHandlers.Remove(value);
                    }
                }
            }

            public Task<CommandSubmission> SubmitAsync(
                ProtocolCommand command,
                CancellationToken cancellationToken)
            {
                Submitted.Add(command);
                return _submit(command);
            }

            public void RaiseReceipt(TableReceiptResolvedEventArgs args)
            {
                EventHandler<TableReceiptResolvedEventArgs>[] handlers = _receiptHandlers.ToArray();
                for (int i = 0; i < handlers.Length; i++)
                {
                    handlers[i](this, args);
                }
            }

            public void Publish(SessionViewState state)
            {
                _view.Publish(state);
            }
        }

        private sealed class FakeSessionView : ISessionView
        {
            public FakeSessionView(SessionViewState initial)
            {
                Current = initial;
            }

            public SessionViewState Current { get; private set; }

            public event EventHandler<SessionViewState>? Changed;

            public void Publish(SessionViewState state)
            {
                Current = state;
                Changed?.Invoke(this, state);
            }
        }
    }
}
