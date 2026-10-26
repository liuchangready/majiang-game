using System;
using System.Collections.Generic;
using Mahjong.Unity.Table;
using Mahjong.Contracts.Wire;
using Xunit;

namespace Mahjong.Unity.TableModel.Tests
{
    public sealed class ConfirmedAnimationQueueTests
    {
        private static PlayerTableViewState View(ManualTableClock clock, PlayerSnapshot? snapshot = null)
        {
            return new PlayerTableProjectionMapper(clock).Project(P43TestData.View(snapshot ?? P43TestData.PlayerSnapshot()));
        }

        private static PlayerTableViewState WithDiscard(
            ManualTableClock clock,
            ulong stateVersion,
            ulong eventSeq,
            int seat,
            uint tileId,
            ulong streamVersion = 5)
        {
            PlayerSnapshot snapshot = P43TestData.PlayerSnapshot(
                stateVersion: stateVersion,
                eventSeq: eventSeq,
                streamVersion: streamVersion,
                projectionSeq: stateVersion);
            snapshot.PublicView.Board.Players[seat].Discards.Add(new DiscardView
            {
                TileId = tileId,
                TurnNumber = (uint)stateVersion
            });
            return View(clock, snapshot);
        }

        private static PlayerSnapshot AddDiscard(
            PlayerSnapshot source,
            ulong stateVersion,
            ulong eventSeq,
            int seat,
            uint tileId)
        {
            PlayerSnapshot next = source.Clone();
            next.StateVersion = stateVersion;
            next.EventSeq = eventSeq;
            next.Cursor = new Mahjong.Contracts.Wire.StreamCursor { StreamVersion = stateVersion, ProjectionSeq = stateVersion };
            next.PublicView.Board.Players[seat].Discards.Add(new DiscardView
            {
                TileId = tileId,
                TurnNumber = (uint)stateVersion
            });
            return next;
        }

        private static ConfirmedAnimationContext Context(PlayerTableViewState view)
        {
            PlayerTableIdentity id = view.Identity;
            return new ConfirmedAnimationContext(
                id.Generation,
                id.SessionId,
                id.RoomId,
                id.RoundId,
                id.BindingEpoch,
                id.StateVersion,
                id.EventSeq,
                id.StreamVersion,
                id.ProjectionSeq);
        }

        private static ConfirmedVisibleEvent Event(
            ulong seq,
            uint ordinal,
            ConfirmedVisibleEventKind kind,
            ConfirmedEventVisibility visibility,
            int ownerSeat = -1,
            int seat = -1,
            uint? tileId = null)
        {
            return new ConfirmedVisibleEvent(seq, ordinal, kind, visibility, ownerSeat, seat, tileId);
        }

        [Fact]
        public void BaselineThenDifferenceEnqueuesOnlyTheConfirmedNewDiscard()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState first = View(clock);
            PlayerTableViewState second = WithDiscard(clock, 6, 106, 1, 44);
            var queue = new ConfirmedAnimationQueue();

            Assert.Equal(TableAnimationCodes.BaselineReset, queue.AcceptConfirmedBaseline(first).Code);
            TableAnimationResult result = queue.AcceptConfirmedDifference(first, second, false);

            Assert.True(result.Accepted);
            Assert.Equal(1, result.Enqueued);
            Assert.Equal(1, queue.PendingCount);
            Assert.True(queue.TryDequeue(out TableAnimationItem? item));
            Assert.Equal(TableAnimationKind.Discard, item!.Kind);
            Assert.Equal(1, item.Seat);
            Assert.Equal(44U, item.TileId);
            Assert.False(item.IsPrivate);
        }

        [Fact]
        public void RepeatedSnapshotDifferenceDoesNotReplay()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState first = View(clock);
            PlayerTableViewState second = WithDiscard(clock, 6, 106, 1, 44);
            var queue = new ConfirmedAnimationQueue();
            queue.AcceptConfirmedBaseline(first);
            Assert.Equal(1, queue.AcceptConfirmedDifference(first, second, false).Enqueued);

            TableAnimationResult duplicate = queue.AcceptConfirmedDifference(second, second, false);
            Assert.True(duplicate.Accepted);
            Assert.Equal(TableAnimationCodes.Duplicate, duplicate.Code);
            Assert.Equal(0, duplicate.Enqueued);
            Assert.Equal(1, queue.PendingCount);
        }

        [Fact]
        public void OwnDrawDifferenceIsPrivate()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState first = View(clock);
            PlayerSnapshot next = P43TestData.PlayerSnapshot(stateVersion: 6, eventSeq: 106, projectionSeq: 6);
            next.PrivateView.DrawnTileId = 21;
            next.PrivateView.ClosedTiles.Add(13);
            next.PublicView.Board.Players[0].HandCount = 14;
            PlayerTableViewState second = View(clock, next);
            var queue = new ConfirmedAnimationQueue();
            queue.AcceptConfirmedBaseline(first);

            TableAnimationResult result = queue.AcceptConfirmedDifference(first, second, false);
            Assert.True(result.Accepted);
            Assert.True(queue.TryDequeue(out TableAnimationItem? item));
            Assert.Equal(TableAnimationKind.OwnDraw, item!.Kind);
            Assert.True(item.IsPrivate);
            Assert.Equal(0, item.OwnerSeat);
            Assert.Equal(21U, item.TileId);
        }

        [Fact]
        public void DifferenceWithoutBaselineIsRejected()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState first = View(clock);
            PlayerTableViewState second = WithDiscard(clock, 6, 106, 1, 44);
            var queue = new ConfirmedAnimationQueue();

            TableAnimationResult result = queue.AcceptConfirmedDifference(first, second, false);
            Assert.False(result.Accepted);
            Assert.Equal(TableAnimationCodes.NoBaseline, result.Code);
        }

        [Fact]
        public void OldGenerationBindingAndRoundAreRejected()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState first = View(clock);
            var queue = new ConfirmedAnimationQueue();
            queue.AcceptConfirmedBaseline(first);

            PlayerTableViewState oldGeneration = new PlayerTableProjectionMapper(clock).Project(
                P43TestData.View(P43TestData.PlayerSnapshot(stateVersion: 6, eventSeq: 106), generation: 0));
            Assert.False(queue.AcceptConfirmedDifference(first, oldGeneration, false).Accepted);

            PlayerTableViewState otherBinding = new PlayerTableProjectionMapper(clock).Project(
                P43TestData.View(P43TestData.PlayerSnapshot(stateVersion: 6, eventSeq: 106), bindingEpoch: 12));
            Assert.Equal(TableAnimationCodes.ContextMismatch, queue.AcceptConfirmedDifference(first, otherBinding, false).Code);

            PlayerTableViewState otherRound = View(clock, P43TestData.PlayerSnapshot(stateVersion: 6, eventSeq: 106, roundId: "round-next"));
            Assert.Equal(TableAnimationCodes.ContextMismatch, queue.AcceptConfirmedDifference(first, otherRound, false).Code);
        }

        [Fact]
        public void StaleBaselineWithAnOlderBindingOrStateVersionIsRejected()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState first = View(clock);
            var queue = new ConfirmedAnimationQueue();
            queue.AcceptConfirmedBaseline(first);

            PlayerTableViewState olderBinding = new PlayerTableProjectionMapper(clock).Project(
                P43TestData.View(P43TestData.PlayerSnapshot(stateVersion: 6, eventSeq: 106), bindingEpoch: 10));
            Assert.Equal(TableAnimationCodes.ContextMismatch, queue.AcceptConfirmedBaseline(olderBinding).Code);

            PlayerTableViewState olderState = new PlayerTableProjectionMapper(clock).Project(
                P43TestData.View(P43TestData.PlayerSnapshot(stateVersion: 4, eventSeq: 104)));
            Assert.Equal(TableAnimationCodes.StaleStateVersion, queue.AcceptConfirmedBaseline(olderState).Code);
        }

        [Fact]
        public void BaselineResetClearsOldAnimations()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState first = View(clock);
            PlayerTableViewState second = WithDiscard(clock, 6, 106, 1, 44);
            var queue = new ConfirmedAnimationQueue();
            queue.AcceptConfirmedBaseline(first);
            queue.AcceptConfirmedDifference(first, second, false);
            Assert.Equal(1, queue.PendingCount);

            Assert.Equal(TableAnimationCodes.BaselineReset, queue.AcceptConfirmedBaseline(second).Code);
            Assert.Equal(0, queue.PendingCount);
        }

        [Fact]
        public void NewBindingIsOnlyAdmittedByABaseline()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState first = View(clock);
            var queue = new ConfirmedAnimationQueue();
            queue.AcceptConfirmedBaseline(first);

            PlayerTableViewState second = new PlayerTableProjectionMapper(clock).Project(
                P43TestData.View(
                    P43TestData.PlayerSnapshot(stateVersion: 6, eventSeq: 106, sessionId: "session-next"),
                    sessionId: "session-next",
                    bindingEpoch: 12));

            Assert.Equal(TableAnimationCodes.ContextMismatch, queue.AcceptConfirmedDifference(first, second, false).Code);
            Assert.Equal(TableAnimationCodes.BaselineReset, queue.AcceptConfirmedBaseline(second).Code);
            Assert.Equal("session-next", queue.Context!.SessionId);
        }

        [Fact]
        public void SnapshotRemovalRequiresABaseline()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState first = View(clock);
            PlayerSnapshot next = P43TestData.PlayerSnapshot(stateVersion: 6, eventSeq: 106);
            next.PublicView.Board.Players[0].Discards.Clear();
            PlayerTableViewState second = View(clock, next);
            var queue = new ConfirmedAnimationQueue();
            queue.AcceptConfirmedBaseline(first);

            TableAnimationResult result = queue.AcceptConfirmedDifference(first, second, false);
            Assert.False(result.Accepted);
            Assert.Equal(TableAnimationCodes.NonMonotonicSnapshot, result.Code);
        }

        [Fact]
        public void StateVersionAndEventSeqAndStreamVersionAreFencedSeparately()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState first = View(clock);
            var queue = new ConfirmedAnimationQueue();
            queue.AcceptConfirmedBaseline(first);

            // A lagging stream version is context only, not a fence.
            PlayerTableViewState laggingStream = WithDiscard(clock, 6, 106, 1, 44, streamVersion: 4);
            TableAnimationResult accepted = queue.AcceptConfirmedDifference(first, laggingStream, false);
            Assert.True(accepted.Accepted);

            // A regressing event sequence is a separate fence.
            PlayerTableViewState regressingEvent = WithDiscard(clock, 7, 104, 2, 45);
            TableAnimationResult eventRegression = queue.AcceptConfirmedDifference(laggingStream, regressingEvent, false);
            Assert.False(eventRegression.Accepted);
            Assert.Equal(TableAnimationCodes.EventSeqRegression, eventRegression.Code);

            // The same state version is a duplicate even if the event seq differs.
            PlayerSnapshot sameState = P43TestData.PlayerSnapshot(stateVersion: 6, eventSeq: 200);
            PlayerTableViewState sameView = View(clock, sameState);
            Assert.Equal(TableAnimationCodes.Duplicate, queue.AcceptConfirmedDifference(laggingStream, sameView, false).Code);
        }

        [Fact]
        public void EventDuplicatesAndOutOfOrderEventsAreSkipped()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState view = View(clock);
            var queue = new ConfirmedAnimationQueue();
            queue.AcceptConfirmedBaseline(view);
            ConfirmedAnimationContext context = Context(view);

            var events = new List<ConfirmedVisibleEvent>
            {
                Event(106, 0, ConfirmedVisibleEventKind.TileDiscarded, ConfirmedEventVisibility.Public, seat: 1, tileId: 44),
                Event(106, 0, ConfirmedVisibleEventKind.TileDiscarded, ConfirmedEventVisibility.Public, seat: 1, tileId: 44),
                Event(106, 1, ConfirmedVisibleEventKind.RiichiDeclared, ConfirmedEventVisibility.Public, seat: 1)
            };
            TableAnimationResult result = queue.AcceptConfirmedEvents(context, events, TableAnimationAudience.Player(0));

            Assert.True(result.Accepted);
            Assert.Equal(2, result.Enqueued);
            Assert.Equal(2, queue.PendingCount);

            var stale = new List<ConfirmedVisibleEvent>
            {
                Event(105, 0, ConfirmedVisibleEventKind.TileDiscarded, ConfirmedEventVisibility.Public, seat: 2)
            };
            TableAnimationResult staleResult = queue.AcceptConfirmedEvents(context, stale, TableAnimationAudience.Player(0));
            Assert.Equal(TableAnimationCodes.Duplicate, staleResult.Code);
            Assert.Equal(0, staleResult.Enqueued);
        }

        [Fact]
        public void PrivateEventsAreOnlyForTheOwningPlayer()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState view = View(clock);
            var queue = new ConfirmedAnimationQueue();
            queue.AcceptConfirmedBaseline(view);
            ConfirmedAnimationContext context = Context(view);

            Assert.True(queue.AcceptConfirmedEvents(
                context,
                new[] { Event(106, 0, ConfirmedVisibleEventKind.SleeveBloomResolved, ConfirmedEventVisibility.Private, ownerSeat: 0, seat: 0) },
                TableAnimationAudience.Player(0)).Accepted);

            Assert.Equal(
                TableAnimationCodes.PrivateNotAuthorized,
                queue.AcceptConfirmedEvents(
                    context,
                    new[] { Event(107, 0, ConfirmedVisibleEventKind.SleeveBloomResolved, ConfirmedEventVisibility.Private, ownerSeat: 1, seat: 1) },
                    TableAnimationAudience.Player(0)).Code);

            Assert.Equal(
                TableAnimationCodes.AudienceMismatch,
                queue.AcceptConfirmedEvents(
                    context,
                    new[] { Event(108, 0, ConfirmedVisibleEventKind.BeamSwapResolved, ConfirmedEventVisibility.Private, ownerSeat: 0, seat: 0) },
                    TableAnimationAudience.Spectator()).Code);

            Assert.Equal(
                TableAnimationCodes.PrivateOwnerMissing,
                queue.AcceptConfirmedEvents(
                    context,
                    new[] { Event(109, 0, ConfirmedVisibleEventKind.BeamSwapResolved, ConfirmedEventVisibility.Private, ownerSeat: -1, seat: 0) },
                    TableAnimationAudience.Player(0)).Code);
        }

        [Fact]
        public void PublicEventsAreAllowedForSpectators()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState view = new PlayerTableProjectionMapper(clock).Project(
                P43TestData.View(null, P43TestData.PublicSnapshot(), seat: -1, role: Mahjong.Contracts.ProtocolRole.Spectator));
            var queue = new ConfirmedAnimationQueue();
            queue.AcceptConfirmedBaseline(view);
            ConfirmedAnimationContext context = Context(view);

            TableAnimationResult result = queue.AcceptConfirmedEvents(
                context,
                new[] { Event(106, 0, ConfirmedVisibleEventKind.TileDiscarded, ConfirmedEventVisibility.Public, seat: 0, tileId: 13) },
                TableAnimationAudience.Spectator());

            Assert.True(result.Accepted);
            Assert.Equal(1, result.Enqueued);
        }

        [Fact]
        public void EventVisibilityMismatchIsRejectedBeforeEnqueue()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState view = View(clock);
            var queue = new ConfirmedAnimationQueue();
            queue.AcceptConfirmedBaseline(view);
            ConfirmedAnimationContext context = Context(view);

            Assert.Equal(
                TableAnimationCodes.EventVisibilityMismatch,
                queue.AcceptConfirmedEvents(
                    context,
                    new[] { Event(106, 0, ConfirmedVisibleEventKind.TileDrawn, ConfirmedEventVisibility.Public, seat: 0) },
                    TableAnimationAudience.Player(0)).Code);

            Assert.Equal(
                TableAnimationCodes.EventVisibilityMismatch,
                queue.AcceptConfirmedEvents(
                    context,
                    new[] { Event(107, 0, ConfirmedVisibleEventKind.TileDiscarded, ConfirmedEventVisibility.Private, ownerSeat: 0, seat: 0) },
                    TableAnimationAudience.Player(0)).Code);
        }

        [Fact]
        public void QueueIsBoundedAndDropsOldestItems()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState first = View(clock);
            var queue = new ConfirmedAnimationQueue(capacity: 2);
            queue.AcceptConfirmedBaseline(first);

            PlayerSnapshot snapshot2 = AddDiscard(P43TestData.PlayerSnapshot(), 6, 106, 1, 44);
            PlayerTableViewState second = View(clock, snapshot2);
            queue.AcceptConfirmedDifference(first, second, false);
            PlayerSnapshot snapshot3 = AddDiscard(snapshot2, 7, 107, 2, 45);
            PlayerTableViewState third = View(clock, snapshot3);
            queue.AcceptConfirmedDifference(second, third, false);
            PlayerSnapshot snapshot4 = AddDiscard(snapshot3, 8, 108, 3, 46);
            PlayerTableViewState fourth = View(clock, snapshot4);
            TableAnimationResult result = queue.AcceptConfirmedDifference(third, fourth, false);

            Assert.Equal(2, queue.PendingCount);
            Assert.Equal(1, queue.DroppedCount);
            Assert.Equal(1, result.Dropped);
        }
    }
}
