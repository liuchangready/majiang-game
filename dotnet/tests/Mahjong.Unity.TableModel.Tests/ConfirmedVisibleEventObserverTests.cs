#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Unity.App;
using Mahjong.Unity.UI.Session;
using Xunit;
using ContractCursor = Mahjong.Contracts.StreamCursor;
using ClientReceiptStatus = Mahjong.Contracts.ReceiptStatus;

namespace Mahjong.Unity.TableModel.Tests
{
    /// <summary>
    /// Directed tests for the P4.5 cold-recovery smoke observer. The tests use
    /// the real Client visible-event projection and the production observer, not
    /// a mirror implementation. They exercise the exact failure mode where the
    /// action batch is published, then immediately replaced in
    /// <see cref="ISessionView.Current"/> by a later empty incremental batch.
    /// </summary>
    public sealed class ConfirmedVisibleEventObserverTests
    {
        private const string RequestId = "smoke-request-1";
        private const string ScopeId = P43TestData.ScopeId;
        private const string RoundId = P43TestData.RoundId;

        private static readonly MethodInfo ProjectPlayerMethod =
            typeof(TableVisibleEventBatch).GetMethod(
                "ProjectPlayer",
                BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Client ProjectPlayer method was not found.");

        private static readonly Type FenceType =
            typeof(TableVisibleEventBatch).Assembly.GetType("Mahjong.Client.TableVisibleEventFence", true)
            ?? throw new InvalidOperationException("Client visible-event fence type was not found.");

        [Fact]
        public void TransientActionBatchIsLostByCurrentPollingButKeptByObserver()
        {
            TableSessionBinding binding = P43TestData.Binding();
            object fence = NewFence();
            TableVisibleEventBatch baselineBatch = Project(
                binding, fence, 1, 1, RoundId, true);
            SessionViewState baseline = View(binding, 1, 1, baselineBatch, 1);
            var view = new FakeSessionView(baseline);
            using var observer = new ConfirmedVisibleEventObserver(view);
            observer.BeginAction(baseline, RequestId, ScopeId, "DiscardTile/4");
            observer.RecordReceipt(Receipt(RequestId, ScopeId, 1, 2, 2));

            TableVisibleEventBatch actionBatch = Project(
                binding, fence, 2, 2, RoundId, false, Discard(2, 2, seat: 0));
            TableVisibleEventBatch laterEmptyBatch = Project(
                binding, fence, 3, 3, RoundId, false);
            view.Publish(View(binding, 2, 2, actionBatch, 2));
            view.Publish(View(binding, 3, 3, laterEmptyBatch, 3));

            // This is exactly the old smoke predicate reading Current: the action
            // publication was replaced by a later empty incremental publication.
            Assert.Equal(3UL, view.Current.PlayerSnapshot!.StateVersion);
            Assert.Equal(TableVisibleEventBatchKind.Incremental, view.Current.VisibleEventBatch.Kind);
            Assert.Empty(view.Current.VisibleEventBatch.Events);

            ConfirmedVisibleActionEvent? confirmed = observer.TryGetConfirmation();
            Assert.NotNull(confirmed);
            Assert.Equal(2UL, confirmed!.Batch.StateVersion);
            Assert.Equal(2UL, confirmed.CommandVersion);
            Assert.Equal(ProtocolEventType.TileDiscarded, confirmed.Event.Type);
            Assert.Equal(0, confirmed.Event.Seat);
            Assert.Equal(2, observer.Batches.Count);
            ObservedVisibleEventBatch? actionPublication = observer.FindActionPublication(2);
            Assert.NotNull(actionPublication);
            Assert.Single(actionPublication!.Events);
        }

        [Fact]
        public void AppliedReceiptWithoutAnyVisibleEventIsNotAConfirmation()
        {
            TableSessionBinding binding = P43TestData.Binding();
            object fence = NewFence();
            TableVisibleEventBatch baselineBatch = Project(binding, fence, 1, 1, RoundId, true);
            SessionViewState baseline = View(binding, 1, 1, baselineBatch, 1);
            var view = new FakeSessionView(baseline);
            using var observer = new ConfirmedVisibleEventObserver(view);
            observer.BeginAction(baseline, RequestId, ScopeId, "Pass/11");
            observer.RecordReceipt(Receipt(RequestId, ScopeId, 1, 2, 2));
            view.Publish(View(binding, 2, 2, Project(binding, fence, 2, 2, RoundId, false), 2));

            Assert.True(observer.HasAppliedReceipt);
            Assert.Null(observer.TryGetConfirmation());
        }

        [Fact]
        public void UnrelatedAiEventNeverConfirmsThePlayerRequest()
        {
            TableSessionBinding binding = P43TestData.Binding();
            object fence = NewFence();
            TableVisibleEventBatch baselineBatch = Project(binding, fence, 1, 1, RoundId, true);
            SessionViewState baseline = View(binding, 1, 1, baselineBatch, 1);
            var view = new FakeSessionView(baseline);
            using var observer = new ConfirmedVisibleEventObserver(view);
            observer.BeginAction(baseline, RequestId, ScopeId, "DiscardTile/4");
            observer.RecordReceipt(Receipt(RequestId, ScopeId, 1, 2, 2));

            view.Publish(View(
                binding,
                3,
                3,
                Project(binding, fence, 3, 3, RoundId, false, Discard(3, 3, seat: 1)),
                2));
            Assert.Null(observer.TryGetConfirmation());

            view.Publish(View(
                binding,
                2,
                2,
                Project(binding, fence, 2, 2, RoundId, false, Discard(2, 2, seat: 0)),
                3));
            ConfirmedVisibleActionEvent? confirmed = observer.TryGetConfirmation();
            Assert.NotNull(confirmed);
            Assert.Equal(2UL, confirmed!.CommandVersion);
        }

        [Fact]
        public void BaselineReplayAndOldBindingOrRoundNeverConfirm()
        {
            TableSessionBinding binding = P43TestData.Binding();
            object fence = NewFence();
            TableVisibleEventBatch baselineBatch = Project(binding, fence, 1, 1, RoundId, true);
            SessionViewState baseline = View(binding, 1, 1, baselineBatch, 1);
            var view = new FakeSessionView(baseline);
            using var observer = new ConfirmedVisibleEventObserver(view);
            observer.BeginAction(baseline, RequestId, ScopeId, "DiscardTile/4");
            observer.RecordReceipt(Receipt(RequestId, ScopeId, 1, 2, 2));

            // A full baseline that happens to carry command 2 is history, not a
            // new action event.
            view.Publish(View(
                binding,
                2,
                2,
                Project(binding, fence, 2, 2, RoundId, true, Discard(2, 2, seat: 0)),
                2));
            Assert.Null(observer.TryGetConfirmation());

            // Same action/event on a superseded binding epoch must not confirm.
            TableSessionBinding oldBinding = P43TestData.Binding(bindingEpoch: P43TestData.BindingEpoch - 1);
            object oldFence = NewFence();
            view.Publish(View(
                oldBinding,
                2,
                2,
                Project(oldBinding, oldFence, 2, 2, RoundId, false, Discard(2, 2, seat: 0)),
                3));
            Assert.Null(observer.TryGetConfirmation());

            // Same binding but another round must not confirm.
            view.Publish(View(
                binding,
                2,
                2,
                Project(binding, fence, 2, 2, "other-round", false, Discard(2, 2, seat: 0)),
                4));
            Assert.Null(observer.TryGetConfirmation());

            view.Publish(View(
                binding,
                2,
                2,
                Project(binding, fence, 2, 2, RoundId, false, Discard(2, 2, seat: 0)),
                5));
            Assert.NotNull(observer.TryGetConfirmation());
        }

        [Fact]
        public void DuplicateFramesDoNotCreateASecondConfirmationOrDuplicateEvent()
        {
            TableSessionBinding binding = P43TestData.Binding();
            object fence = NewFence();
            TableVisibleEventBatch baselineBatch = Project(binding, fence, 1, 1, RoundId, true);
            SessionViewState baseline = View(binding, 1, 1, baselineBatch, 1);
            var view = new FakeSessionView(baseline);
            using var observer = new ConfirmedVisibleEventObserver(view);
            observer.BeginAction(baseline, RequestId, ScopeId, "DiscardTile/4");
            observer.RecordReceipt(Receipt(RequestId, ScopeId, 1, 2, 2));

            TableVisibleEventBatch actionBatch = Project(
                binding, fence, 2, 2, RoundId, false, Discard(2, 2, seat: 0));
            view.Publish(View(binding, 2, 2, actionBatch, 2));
            view.Publish(View(binding, 2, 2, actionBatch, 2));

            ConfirmedVisibleActionEvent? first = observer.TryGetConfirmation();
            ConfirmedVisibleActionEvent? second = observer.TryGetConfirmation();
            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(first!.Event.EventSeq, second!.Event.EventSeq);
            Assert.Equal(first.Event.EventOrdinal, second.Event.EventOrdinal);
            Assert.Equal(first.CommandVersion, second.CommandVersion);
            Assert.Single(actionBatch.Events);
            Assert.Equal(2, observer.Batches.Count);
        }

        [Fact]
        public void StateOnlyActionThenRealDiscardConfirmsTheSecondAction()
        {
            TableSessionBinding binding = P43TestData.Binding();
            object fence = NewFence();
            TableVisibleEventBatch baselineBatch = Project(binding, fence, 1, 1, RoundId, true);
            SessionViewState baseline = View(binding, 1, 1, baselineBatch, 1);
            var view = new FakeSessionView(baseline);
            using var observer = new ConfirmedVisibleEventObserver(view);

            observer.BeginAction(baseline, "smoke-pass", ScopeId, "Pass/11");
            observer.RecordReceipt(Receipt("smoke-pass", ScopeId, 1, 2, 2));
            SessionViewState stateOnly = View(
                binding, 2, 2, Project(binding, fence, 2, 2, RoundId, false), 2);
            view.Publish(stateOnly);
            Assert.True(observer.HasAppliedReceipt);
            Assert.Null(observer.TryGetConfirmation());

            observer.BeginAction(stateOnly, "smoke-discard", ScopeId, "DiscardTile/4");
            observer.RecordReceipt(Receipt("smoke-discard", ScopeId, 2, 3, 3));
            view.Publish(View(
                binding,
                3,
                3,
                Project(binding, fence, 3, 3, RoundId, false, Discard(3, 3, seat: 0)),
                3));

            ConfirmedVisibleActionEvent? confirmed = observer.TryGetConfirmation();
            Assert.NotNull(confirmed);
            Assert.Equal(3UL, confirmed!.CommandVersion);
            Assert.Equal(3UL, confirmed.Batch.StateVersion);
            Assert.Equal(ProtocolEventType.TileDiscarded, confirmed.Event.Type);
        }

        [Fact]
        public void ColdRecoveryPonEmptyBatchThenNextDiscardConfirms()
        {
            // Reproduces the observed cold-recovery shape from the preserved P4.5
            // baseline: the first real player action is a Pon whose domain event
            // has no protocol projection, so its incremental batch is empty. The
            // observer must not treat receipt/history as a visible action, but the
            // following real discard must confirm normally.
            TableSessionBinding binding = P43TestData.Binding();
            object fence = NewFence();
            TableVisibleEventBatch baselineBatch = Project(binding, fence, 3, 6, RoundId, true);
            SessionViewState baseline = View(binding, 3, 6, baselineBatch, 1);
            var view = new FakeSessionView(baseline);
            using var observer = new ConfirmedVisibleEventObserver(view);

            observer.BeginAction(baseline, "smoke-pon", ScopeId, "DeclarePon/6");
            observer.RecordReceipt(Receipt("smoke-pon", ScopeId, 4, 5, 10));
            SessionViewState ponState = View(
                binding, 5, 10, Project(binding, fence, 5, 10, RoundId, false), 2);
            view.Publish(ponState);
            Assert.True(observer.HasAppliedReceipt);
            Assert.Null(observer.TryGetConfirmation());
            ObservedVisibleEventBatch? ponPublication = observer.FindActionPublication(5);
            Assert.NotNull(ponPublication);
            Assert.Empty(ponPublication!.Events);

            observer.BeginAction(ponState, "smoke-discard-after-pon", ScopeId, "DiscardTile/4");
            observer.RecordReceipt(Receipt("smoke-discard-after-pon", ScopeId, 5, 6, 11));
            view.Publish(View(
                binding,
                6,
                11,
                Project(binding, fence, 6, 11, RoundId, false, Discard(11, 6, seat: 0)),
                3));

            ConfirmedVisibleActionEvent? confirmed = observer.TryGetConfirmation();
            Assert.NotNull(confirmed);
            Assert.Equal(6UL, confirmed!.CommandVersion);
            Assert.Equal(11UL, confirmed.Event.EventSeq);
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
            ulong stateVersion,
            ulong eventSeq,
            TableVisibleEventBatch batch,
            long acceptanceSequence)
        {
            PlayerSnapshot player = P43TestData.PlayerSnapshot(
                stateVersion: stateVersion,
                eventSeq: eventSeq,
                roundId: batch.RoundId);
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
                new ContractCursor(new StreamVersion(5), new ProjectionSeq(eventSeq + 10)),
                batch,
                acceptanceSequence);
        }

        private static EventEnvelope Discard(ulong eventSeq, ulong commandVersion, int seat)
        {
            return new EventEnvelope
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
                    TileId = 5
                }
            };
        }

        private static TableReceiptResolvedEventArgs Receipt(
            string requestId,
            string scopeId,
            ulong evaluated,
            ulong commandVersion,
            ulong eventSeq)
        {
            var key = new IdempotencyKey("local-player", new RequestScopeId(scopeId), new RequestId(requestId));
            var pending = new PendingCommandSnapshot(
                key,
                CommandKind.SubmitAction,
                "digest",
                1,
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
