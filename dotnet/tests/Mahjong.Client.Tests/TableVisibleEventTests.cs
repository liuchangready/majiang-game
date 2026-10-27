using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Xunit;
using ContractCursor = Mahjong.Contracts.StreamCursor;
using WireCursor = Mahjong.Contracts.Wire.StreamCursor;
using WirePublicMatchView = Mahjong.Contracts.Wire.PublicMatchView;
using WirePublicSessionSummary = Mahjong.Contracts.Wire.PublicSessionSummary;

namespace Mahjong.Client.Tests
{
    /// <summary>
    /// P4.3-B2a regression: typed visible events are projected from the exact
    /// encoded PlayerUpdate/PublicUpdate frame that TableSession accepted, with
    /// the same binding/session/role/seat/scope/cursor identity. Frames are
    /// always produced through WireCodec; no test calls a notification path
    /// directly or fabricates event args.
    /// </summary>
    public sealed class TableVisibleEventsTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public async Task PlayerSeatBatchCarriesTypedEventsAndAcceptedIdentityAtomically(int seat)
        {
            (TableSession session, FakeTableChannel channel) = await ConnectedPlayerAsync(seat);
            var notifications = new List<TableSnapshotChangedEventArgs>();
            session.SnapshotChanged += (_, e) => notifications.Add(e);

            channel.Enqueue(PlayerPush(
                11,
                seat,
                5,
                10,
                6,
                11,
                isFullSnapshot: false,
                PrivateDraw(seat, 20u + (uint)seat, 106, 6, 0),
                PublicDiscard((seat + 1) % 4, 40u + (uint)seat, 107, 6, 1)));
            await session.PumpOnceAsync(CancellationToken.None);

            TableSnapshotChangedEventArgs args = Assert.Single(notifications);
            Assert.False(args.IsFullBaseline);
            Assert.Equal(6UL, args.Cursor.StreamVersion.Value);
            Assert.Equal(11UL, args.Cursor.ProjectionSeq.Value);
            Assert.Equal(2, args.VisibleEvents.Count);
            Assert.Same(args.PlayerSnapshot, session.PlayerSnapshot);

            TableVisibleEventBatch batch = args.VisibleEventBatch;
            Assert.Equal(TableVisibleEventBatchKind.Incremental, batch.Kind);
            Assert.False(batch.IsFullBaseline);
            Assert.True(batch.HasEvents);
            Assert.Equal(2, batch.Count);
            Assert.Equal(2, batch.PlayableEvents.Count);
            Assert.Equal(TestSession.SessionId, batch.SessionId);
            Assert.Equal(TestSession.RoomId, batch.RoomId);
            Assert.Equal("table-1", batch.TableId);
            Assert.Equal("round-1", batch.RoundId);
            Assert.Equal("scope-1", batch.ScopeId);
            Assert.Equal(ProtocolRole.Player, batch.Role);
            Assert.Equal(seat, batch.Seat);
            Assert.Equal(11UL, batch.BindingEpoch);
            Assert.Equal(1UL, batch.ConnectionEpoch);
            Assert.Equal(3UL, batch.OwnerEpoch);
            Assert.Equal(6UL, batch.StateVersion);
            Assert.Equal(107UL, batch.EventSeq);
            Assert.Equal(6UL, batch.StreamVersion);
            Assert.Equal(11UL, batch.ProjectionSeq);
            Assert.Equal(args.Cursor, batch.Cursor);

            TableVisibleEvent privateDraw = batch.Events[0];
            Assert.Equal(ProtocolEventType.TileDrawn, privateDraw.Type);
            Assert.Equal(ProtocolEventVisibility.Private, privateDraw.Visibility);
            Assert.True(privateDraw.IsPrivate);
            Assert.Equal(seat, privateDraw.OwnerSeat);
            Assert.Equal(seat, privateDraw.Seat);
            Assert.Equal(20u + (uint)seat, privateDraw.TileId);
            Assert.Equal(106UL, privateDraw.EventSeq);
            Assert.Equal(6UL, privateDraw.CommandVersion);
            Assert.Equal(0u, privateDraw.EventOrdinal);
            Assert.Null(privateDraw.Payload.Meld);

            TableVisibleEvent publicDiscard = batch.Events[1];
            Assert.Equal(ProtocolEventType.TileDiscarded, publicDiscard.Type);
            Assert.Equal(ProtocolEventVisibility.Public, publicDiscard.Visibility);
            Assert.False(publicDiscard.IsPrivate);
            Assert.Equal(-1, publicDiscard.OwnerSeat);
            Assert.Equal((seat + 1) % 4, publicDiscard.Seat);
            Assert.Equal(40u + (uint)seat, publicDiscard.TileId);
            Assert.Equal(107UL, publicDiscard.EventSeq);
            Assert.Equal(6UL, publicDiscard.CommandVersion);
            Assert.Equal(1u, publicDiscard.EventOrdinal);
        }

        [Fact]
        public async Task SpectatorBatchCarriesOnlyPublicTypedEvents()
        {
            (TableSession session, FakeTableChannel channel) = await ConnectedSpectatorAsync();
            var notifications = new List<TableSnapshotChangedEventArgs>();
            session.SnapshotChanged += (_, e) => notifications.Add(e);

            channel.Enqueue(PublicPush(
                11,
                5,
                10,
                6,
                11,
                SpectatorDiscard(2, 44, 106, 6, 0),
                SpectatorDora(107, 6, 1)));
            await session.PumpOnceAsync(CancellationToken.None);

            TableSnapshotChangedEventArgs args = Assert.Single(notifications);
            TableVisibleEventBatch batch = args.VisibleEventBatch;
            Assert.Equal(TableVisibleEventBatchKind.Incremental, batch.Kind);
            Assert.Equal(ProtocolRole.Spectator, batch.Role);
            Assert.Equal(ProtocolConstants.NoSeat, batch.Seat);
            Assert.Equal(2, batch.Events.Count);
            Assert.All(batch.Events, visible => Assert.False(visible.IsPrivate));
            Assert.All(batch.Events, visible => Assert.Equal(ProtocolConstants.NoSeat, visible.OwnerSeat));

            Assert.Equal(ProtocolEventType.TileDiscarded, batch.Events[0].Type);
            Assert.Equal(2, batch.Events[0].Seat);
            Assert.Equal(44u, batch.Events[0].TileId);
            Assert.Equal(ProtocolEventVisibility.Public, batch.Events[0].Visibility);

            Assert.Equal(ProtocolEventType.DoraRevealed, batch.Events[1].Type);
            Assert.Equal(ProtocolEventVisibility.Public, batch.Events[1].Visibility);
            Assert.Equal(107UL, batch.Events[1].EventSeq);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public async Task InitialSyncWatermarkPreventsUnseenHistoricalEventReplay(int seat)
        {
            (TableSession session, FakeTableChannel channel) = await ConnectedPlayerAsync(seat);
            var notifications = new List<TableSnapshotChangedEventArgs>();
            session.SnapshotChanged += (_, e) => notifications.Add(e);

            channel.Enqueue(PlayerPush(
                11,
                seat,
                5,
                10,
                6,
                11,
                isFullSnapshot: false,
                PrivateDraw(seat, 20, 104, 5, 0),
                PrivateDraw(seat, 21, 105, 5, 3),
                PrivateDraw(seat, 22, 106, 6, 0)));
            await session.PumpOnceAsync(CancellationToken.None);

            TableVisibleEventBatch batch = Assert.Single(notifications).VisibleEventBatch;
            TableVisibleEvent playable = Assert.Single(batch.PlayableEvents);
            Assert.Equal(106UL, playable.EventSeq);
            Assert.DoesNotContain(batch.PlayableEvents, visible => visible.EventSeq <= 105UL);
            Assert.Equal(TableConnectionState.Ready, session.State);
        }

        [Fact]
        public async Task EmptyFullPushWatermarkPreventsUnseenHistoricalEventReplay()
        {
            (TableSession session, FakeTableChannel channel) = await ConnectedPlayerAsync(0);
            var notifications = new List<TableSnapshotChangedEventArgs>();
            session.SnapshotChanged += (_, e) => notifications.Add(e);

            channel.Enqueue(PlayerPush(
                11,
                0,
                5,
                10,
                6,
                11,
                isFullSnapshot: true));
            await session.PumpOnceAsync(CancellationToken.None);

            TableSnapshotChangedEventArgs baseline = Assert.Single(notifications);
            Assert.True(baseline.IsFullBaseline);
            Assert.Empty(baseline.VisibleEventBatch.Events);
            Assert.Equal(107UL, baseline.VisibleEventBatch.EventSeq);
            notifications.Clear();

            channel.Enqueue(PlayerPush(
                11,
                0,
                6,
                11,
                7,
                12,
                isFullSnapshot: false,
                PrivateDraw(0, 20, 106, 6, 0),
                PrivateDraw(0, 21, 107, 6, 2),
                PrivateDraw(0, 22, 108, 7, 0)));
            await session.PumpOnceAsync(CancellationToken.None);

            TableVisibleEventBatch batch = Assert.Single(notifications).VisibleEventBatch;
            TableVisibleEvent playable = Assert.Single(batch.PlayableEvents);
            Assert.Equal(108UL, playable.EventSeq);
            Assert.DoesNotContain(batch.PlayableEvents, visible => visible.EventSeq <= 107UL);
        }

        [Fact]
        public async Task FullBaselineWithEventsWatermarkPreventsHistoricalReplay()
        {
            (TableSession session, FakeTableChannel channel) = await ConnectedPlayerAsync(0);
            var notifications = new List<TableSnapshotChangedEventArgs>();
            session.SnapshotChanged += (_, e) => notifications.Add(e);

            channel.Enqueue(PlayerPush(
                11,
                0,
                5,
                10,
                6,
                11,
                isFullSnapshot: true,
                PrivateDraw(0, 20, 106, 6, 0)));
            await session.PumpOnceAsync(CancellationToken.None);

            TableSnapshotChangedEventArgs baseline = Assert.Single(notifications);
            Assert.Single(baseline.VisibleEventBatch.Events);
            Assert.Empty(baseline.VisibleEventBatch.PlayableEvents);
            notifications.Clear();

            channel.Enqueue(PlayerPush(
                11,
                0,
                6,
                11,
                7,
                12,
                isFullSnapshot: false,
                PrivateDraw(0, 20, 106, 6, 0),
                PrivateDraw(0, 21, 107, 6, 1),
                PrivateDraw(0, 22, 108, 7, 0)));
            await session.PumpOnceAsync(CancellationToken.None);

            TableVisibleEventBatch batch = Assert.Single(notifications).VisibleEventBatch;
            Assert.Equal(108UL, Assert.Single(batch.PlayableEvents).EventSeq);
        }

        [Fact]
        public async Task SpectatorSyncWatermarkPreventsHistoricalPublicEventReplay()
        {
            (TableSession session, FakeTableChannel channel) = await ConnectedSpectatorAsync();
            var notifications = new List<TableSnapshotChangedEventArgs>();
            session.SnapshotChanged += (_, e) => notifications.Add(e);

            channel.Enqueue(PublicPush(
                11,
                5,
                10,
                6,
                11,
                SpectatorDiscard(1, 40, 104, 5, 0),
                SpectatorDiscard(1, 41, 105, 5, 2),
                SpectatorDiscard(1, 42, 106, 6, 0)));
            await session.PumpOnceAsync(CancellationToken.None);

            TableVisibleEventBatch batch = Assert.Single(notifications).VisibleEventBatch;
            Assert.Equal(106UL, Assert.Single(batch.PlayableEvents).EventSeq);
            Assert.Equal(TableConnectionState.Ready, session.State);
        }

        [Fact]
        public async Task SparseFreshEventsRemainAllowedAboveTheBaselineWatermark()
        {
            (TableSession session, FakeTableChannel channel) = await ConnectedPlayerAsync(0);
            var notifications = new List<TableSnapshotChangedEventArgs>();
            session.SnapshotChanged += (_, e) => notifications.Add(e);

            Envelope push = PlayerPush(
                11,
                0,
                5,
                10,
                6,
                11,
                isFullSnapshot: false,
                PrivateDraw(0, 20, 109, 6, 0));
            push.PlayerUpdate.Snapshot.EventSeq = 110;
            channel.Enqueue(push);
            await session.PumpOnceAsync(CancellationToken.None);

            TableVisibleEventBatch batch = Assert.Single(notifications).VisibleEventBatch;
            Assert.Equal(109UL, Assert.Single(batch.PlayableEvents).EventSeq);
            Assert.Equal(TableConnectionState.Ready, session.State);
        }

        [Fact]
        public async Task RoundResetWatermarkPreventsUnseenHistoricalEventReplay()
        {
            (TableSession session, FakeTableChannel channel) = await ConnectedPlayerAsync(0);
            var notifications = new List<TableSnapshotChangedEventArgs>();
            session.SnapshotChanged += (_, e) => notifications.Add(e);

            channel.Enqueue(PlayerPush(
                11,
                0,
                5,
                10,
                7,
                12,
                isFullSnapshot: true,
                roundId: "round-2"));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.True(Assert.Single(notifications).IsFullBaseline);
            notifications.Clear();

            channel.Enqueue(PlayerPush(
                11,
                0,
                7,
                12,
                8,
                13,
                isFullSnapshot: false,
                roundId: "round-2",
                PrivateDraw(0, 20, 107, 7, 0),
                PrivateDraw(0, 21, 109, 8, 0)));
            await session.PumpOnceAsync(CancellationToken.None);

            TableVisibleEventBatch batch = Assert.Single(notifications).VisibleEventBatch;
            Assert.Equal(109UL, Assert.Single(batch.PlayableEvents).EventSeq);
        }

        [Fact]
        public async Task ReconnectBindingWatermarkPreventsUnseenHistoricalEventReplay()
        {
            (TableSession session, FakeTableChannel channel) = await ConnectedPlayerAsync(0);
            var notifications = new List<TableSnapshotChangedEventArgs>();
            session.SnapshotChanged += (_, e) => notifications.Add(e);

            channel.Enqueue(PlayerPush(
                11,
                0,
                5,
                10,
                6,
                11,
                isFullSnapshot: false,
                PrivateDraw(0, 20, 106, 6, 0)));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Single(notifications);

            await session.DisconnectAsync(CancellationToken.None);
            await session.OpenAsync(
                new TableSessionOpenRequest("ticket-1", "client-build-1"),
                CancellationToken.None);
            channel.Enqueue(TestWire.Welcome(12, "scope-2", 7, 20, seatId: 0));
            await session.PumpOnceAsync(CancellationToken.None);
            channel.Enqueue(PlayerSync(channel, 12, 0, 7, 20));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TableConnectionState.Ready, session.State);
            notifications.Clear();

            channel.Enqueue(PlayerPush(
                12,
                0,
                7,
                20,
                8,
                21,
                isFullSnapshot: false,
                PrivateDraw(0, 20, 106, 6, 0),
                PrivateDraw(0, 21, 107, 7, 0),
                PrivateDraw(0, 22, 108, 8, 0)));
            await session.PumpOnceAsync(CancellationToken.None);

            TableVisibleEventBatch batch = Assert.Single(notifications).VisibleEventBatch;
            Assert.Equal(108UL, Assert.Single(batch.PlayableEvents).EventSeq);
            Assert.Equal(12UL, batch.BindingEpoch);
        }

        [Fact]
        public async Task DuplicateFrameAndDuplicateEventAreNotNotifiedTwice()
        {
            (TableSession session, FakeTableChannel channel) = await ConnectedPlayerAsync(0);
            var notifications = new List<TableSnapshotChangedEventArgs>();
            session.SnapshotChanged += (_, e) => notifications.Add(e);

            Envelope first = PlayerPush(
                11,
                0,
                5,
                10,
                6,
                11,
                isFullSnapshot: false,
                PrivateDraw(0, 20, 106, 6, 0));
            channel.Enqueue(first);
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Single(notifications);
            Assert.Equal(1, notifications[0].VisibleEventBatch.Count);

            // The exact same cursor is stale: no second notification.
            channel.Enqueue(PlayerPush(
                11,
                0,
                5,
                10,
                6,
                11,
                isFullSnapshot: false,
                PrivateDraw(0, 20, 106, 6, 0)));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Single(notifications);

            // A same-state projection refresh is accepted, but an already
            // delivered event pair is not replayed as a fresh event.
            channel.Enqueue(PlayerPush(
                11,
                0,
                6,
                11,
                6,
                12,
                isFullSnapshot: false,
                PrivateDraw(0, 20, 106, 6, 0)));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(2, notifications.Count);
            TableSnapshotChangedEventArgs refresh = notifications[1];
            Assert.Equal(12UL, refresh.Cursor.ProjectionSeq.Value);
            Assert.Empty(refresh.VisibleEventBatch.Events);
            Assert.Empty(refresh.VisibleEventBatch.PlayableEvents);
            Assert.Equal(TableVisibleEventBatchKind.Incremental, refresh.VisibleEventBatch.Kind);
        }

        [Fact]
        public async Task ForeignBindingAndForeignScopeFramesPublishNothing()
        {
            (TableSession session, FakeTableChannel channel) = await ConnectedPlayerAsync(0);
            var notifications = new List<TableSnapshotChangedEventArgs>();
            session.SnapshotChanged += (_, e) => notifications.Add(e);

            channel.Enqueue(PlayerPush(
                22,
                0,
                5,
                10,
                6,
                11,
                isFullSnapshot: false,
                PrivateDraw(0, 20, 106, 6, 0)));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Empty(notifications);
            Assert.Equal(TableConnectionState.Ready, session.State);

            Envelope foreignScope = PlayerPush(
                11,
                0,
                5,
                10,
                6,
                11,
                isFullSnapshot: false,
                PrivateDraw(0, 20, 106, 6, 0));
            foreignScope.Scope = TestWire.Scope("scope-other");
            channel.Enqueue(foreignScope);
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Empty(notifications);
            Assert.Equal(TableConnectionState.Ready, session.State);

            channel.Enqueue(PlayerPush(
                11,
                0,
                5,
                10,
                6,
                11,
                isFullSnapshot: false,
                PrivateDraw(0, 20, 106, 6, 0)));
            await session.PumpOnceAsync(CancellationToken.None);
            TableSnapshotChangedEventArgs args = Assert.Single(notifications);
            Assert.Equal("scope-1", args.VisibleEventBatch.ScopeId);
            Assert.Equal(1, args.VisibleEventBatch.Count);
        }

        [Fact]
        public async Task WrongSeatFrameIsRejectedBeforeAnyEventPublication()
        {
            (TableSession session, FakeTableChannel channel) = await ConnectedPlayerAsync(0);
            var notifications = new List<TableSnapshotChangedEventArgs>();
            session.SnapshotChanged += (_, e) => notifications.Add(e);

            // A self-consistent frame for another seat is valid wire bytes but
            // cannot belong to this binding.
            channel.Enqueue(PlayerPush(
                11,
                1,
                5,
                10,
                6,
                11,
                isFullSnapshot: false,
                PrivateDraw(1, 21, 106, 6, 0)));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Empty(notifications);
            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(5UL, session.ConfirmedCursor!.Value.StreamVersion.Value);
            Assert.Equal(10UL, session.ConfirmedCursor.Value.ProjectionSeq.Value);
        }

        [Fact]
        public async Task WrongAudienceFramesPublishNothing()
        {
            (TableSession player, FakeTableChannel playerChannel) = await ConnectedPlayerAsync(0);
            var playerNotifications = new List<TableSnapshotChangedEventArgs>();
            player.SnapshotChanged += (_, e) => playerNotifications.Add(e);

            playerChannel.Enqueue(PublicPush(
                11,
                5,
                10,
                6,
                11,
                SpectatorDiscard(1, 30, 106, 6, 0)));
            await player.PumpOnceAsync(CancellationToken.None);
            Assert.Empty(playerNotifications);
            Assert.Equal(TableConnectionState.Faulted, player.State);

            (TableSession spectator, FakeTableChannel spectatorChannel) = await ConnectedSpectatorAsync();
            var spectatorNotifications = new List<TableSnapshotChangedEventArgs>();
            spectator.SnapshotChanged += (_, e) => spectatorNotifications.Add(e);

            spectatorChannel.Enqueue(PlayerPush(
                11,
                0,
                5,
                10,
                6,
                11,
                isFullSnapshot: false,
                PublicDiscard(1, 31, 106, 6, 0)));
            await spectator.PumpOnceAsync(CancellationToken.None);
            Assert.Empty(spectatorNotifications);
            Assert.Equal(TableConnectionState.Faulted, spectator.State);
        }

        [Fact]
        public async Task EarlyBufferedFramesPublishTheirEventsOnlyAfterOrderedApply()
        {
            (TableSession session, FakeTableChannel channel) = await HandshakePlayerAsync(0);
            var notifications = new List<TableSnapshotChangedEventArgs>();
            session.SnapshotChanged += (_, e) => notifications.Add(e);

            channel.Enqueue(PlayerPush(
                11,
                0,
                6,
                11,
                6,
                12,
                isFullSnapshot: false,
                PublicDiscard(1, 42, 107, 6, 1)));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(1, session.EarlyBufferItemCount);
            Assert.Empty(notifications);

            channel.Enqueue(PlayerPush(
                11,
                0,
                5,
                10,
                6,
                11,
                isFullSnapshot: false,
                PublicDiscard(1, 41, 106, 6, 0)));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(2, session.EarlyBufferItemCount);
            Assert.Empty(notifications);

            channel.Enqueue(PlayerSync(channel, 11, 0, 5, 10));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(0, session.EarlyBufferItemCount);
            Assert.NotEmpty(notifications);
            List<TableVisibleEventBatch> eventBatches = notifications
                .Where(e => e.VisibleEventBatch.HasEvents)
                .Select(e => e.VisibleEventBatch)
                .ToList();
            Assert.Equal(2, eventBatches.Count);
            Assert.Equal(106UL, eventBatches[0].Events.Single().EventSeq);
            Assert.Equal(107UL, eventBatches[1].Events.Single().EventSeq);
            Assert.All(eventBatches, batch => Assert.Equal(TableVisibleEventBatchKind.Incremental, batch.Kind));
        }

        [Fact]
        public async Task GapRecoveryPublishesBufferedEventsOnlyAfterTheNewBaseline()
        {
            (TableSession session, FakeTableChannel channel) = await ConnectedPlayerAsync(0);
            var notifications = new List<TableSnapshotChangedEventArgs>();
            session.SnapshotChanged += (_, e) => notifications.Add(e);

            // The frame's predecessor is a baseline the client has not seen yet.
            channel.Enqueue(PlayerPush(
                11,
                0,
                6,
                11,
                6,
                12,
                isFullSnapshot: false,
                PublicDiscard(1, 42, 107, 6, 0)));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Synchronizing, session.State);
            Assert.Equal(1, session.EarlyBufferItemCount);
            Assert.Empty(notifications);
            Assert.Equal(2, channel.CountSent(MessageType.SyncRequest));

            channel.Enqueue(PlayerSync(channel, 11, 0, 6, 11));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(0, session.EarlyBufferItemCount);
            List<TableVisibleEventBatch> eventBatches = notifications
                .Where(e => e.VisibleEventBatch.HasEvents)
                .Select(e => e.VisibleEventBatch)
                .ToList();
            TableVisibleEventBatch eventBatch = Assert.Single(eventBatches);
            Assert.Equal(107UL, eventBatch.Events.Single().EventSeq);
            Assert.Equal(12UL, eventBatch.ProjectionSeq);
        }

        [Fact]
        public async Task EarlyBufferOverflowDoesNotPublishBufferedEvents()
        {
            var options = new TableSessionOptions { MaxEarlyBufferItems = 2 };
            (TableSession session, FakeTableChannel channel) = await HandshakePlayerAsync(0, options);
            var notifications = new List<TableSnapshotChangedEventArgs>();
            session.SnapshotChanged += (_, e) => notifications.Add(e);

            channel.Enqueue(PlayerPush(
                11,
                0,
                5,
                10,
                6,
                11,
                isFullSnapshot: false,
                PublicDiscard(1, 40, 106, 6, 0)));
            channel.Enqueue(PlayerPush(
                11,
                0,
                6,
                11,
                7,
                12,
                isFullSnapshot: false,
                PublicDiscard(1, 41, 107, 7, 0)));
            channel.Enqueue(PlayerPush(
                11,
                0,
                7,
                12,
                8,
                13,
                isFullSnapshot: false,
                PublicDiscard(1, 42, 108, 8, 0)));
            await PumpAsync(session, 3);

            Assert.Equal(1, session.EarlyBufferOverflowCount);
            Assert.Equal(0, session.EarlyBufferItemCount);
            Assert.Empty(notifications);
            Assert.Equal(2, channel.CountSent(MessageType.SyncRequest));

            channel.Enqueue(PlayerSync(channel, 11, 0, 5, 10));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(0, session.EarlyBufferItemCount);
            Assert.All(notifications, e => Assert.False(e.VisibleEventBatch.HasEvents));
            Assert.Contains(notifications, e => e.IsFullBaseline);
        }

        [Fact]
        public async Task PrivateEventForAnotherOwnerIsRejectedBeforePublication()
        {
            (TableSession session, FakeTableChannel channel) = await ConnectedPlayerAsync(0);
            var notifications = new List<TableSnapshotChangedEventArgs>();
            session.SnapshotChanged += (_, e) => notifications.Add(e);

            // Compatibility encoding lets this malformed owner binding reach
            // the wire; the decode/session boundary must still reject it and
            // publish nothing.
            Envelope wrongOwner = PlayerPush(
                11,
                0,
                5,
                10,
                6,
                11,
                isFullSnapshot: false,
                PrivateDraw(1, 21, 106, 6, 0));
            channel.EnqueueCompatibility(wrongOwner);
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Empty(notifications);
            Assert.Equal(TableConnectionState.Faulted, session.State);
            Assert.Contains(ProtocolErrorCode.PrivateDataInPublicStream, session.LastError ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal(10UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
        }

        [Fact]
        public async Task EarlyBufferByteOverflowDoesNotPublishBufferedEvents()
        {
            var options = new TableSessionOptions { MaxEarlyBufferBytes = 32 };
            (TableSession session, FakeTableChannel channel) = await HandshakePlayerAsync(0, options);
            var notifications = new List<TableSnapshotChangedEventArgs>();
            session.SnapshotChanged += (_, e) => notifications.Add(e);

            channel.Enqueue(PlayerPush(
                11,
                0,
                5,
                10,
                6,
                11,
                isFullSnapshot: false,
                PublicDiscard(1, 40, 106, 6, 0)));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(1, session.EarlyBufferOverflowCount);
            Assert.Equal(0, session.EarlyBufferItemCount);
            Assert.Empty(notifications);

            channel.Enqueue(PlayerSync(channel, 11, 0, 5, 10));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.All(notifications, e => Assert.False(e.VisibleEventBatch.HasEvents));
        }

        [Fact]
        public async Task FullBaselinePushEventsAreReconciliationOnlyAndNeverReplayed()
        {
            (TableSession session, FakeTableChannel channel) = await ConnectedPlayerAsync(0);
            var notifications = new List<TableSnapshotChangedEventArgs>();
            session.SnapshotChanged += (_, e) => notifications.Add(e);

            channel.Enqueue(PlayerPush(
                11,
                0,
                5,
                10,
                6,
                11,
                isFullSnapshot: true,
                PrivateDraw(0, 20, 106, 6, 0)));
            await session.PumpOnceAsync(CancellationToken.None);

            TableSnapshotChangedEventArgs baselineArgs = Assert.Single(notifications);
            Assert.True(baselineArgs.IsFullBaseline);
            Assert.Equal(TableVisibleEventBatchKind.Baseline, baselineArgs.VisibleEventBatch.Kind);
            Assert.Single(baselineArgs.VisibleEventBatch.Events);
            Assert.Empty(baselineArgs.VisibleEventBatch.PlayableEvents);

            // Repeating the same baseline does not re-publish its event.
            channel.Enqueue(PlayerPush(
                11,
                0,
                5,
                10,
                6,
                11,
                isFullSnapshot: true,
                PrivateDraw(0, 20, 106, 6, 0)));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Single(notifications);

            channel.Enqueue(PlayerPush(
                11,
                0,
                6,
                11,
                7,
                12,
                isFullSnapshot: false,
                PrivateDraw(0, 21, 108, 7, 0)));
            await session.PumpOnceAsync(CancellationToken.None);

            TableSnapshotChangedEventArgs next = notifications[1];
            Assert.Equal(TableVisibleEventBatchKind.Incremental, next.VisibleEventBatch.Kind);
            Assert.Single(next.VisibleEventBatch.PlayableEvents);
            Assert.Equal(108UL, next.VisibleEventBatch.PlayableEvents[0].EventSeq);
        }

        [Fact]
        public async Task ReconnectWithNewBindingDoesNotCarryOldPrivateEvents()
        {
            (TableSession session, FakeTableChannel channel) = await ConnectedPlayerAsync(0);
            var notifications = new List<TableSnapshotChangedEventArgs>();
            session.SnapshotChanged += (_, e) => notifications.Add(e);

            channel.Enqueue(PlayerPush(
                11,
                0,
                5,
                10,
                6,
                11,
                isFullSnapshot: false,
                PrivateDraw(0, 20, 106, 6, 0)));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Single(notifications);
            Assert.Equal(106UL, notifications[0].VisibleEventBatch.Events.Single().EventSeq);

            await session.DisconnectAsync(CancellationToken.None);
            await session.OpenAsync(
                new TableSessionOpenRequest("ticket-1", "client-build-1"),
                CancellationToken.None);
            channel.Enqueue(TestWire.Welcome(12, "scope-2", 7, 20, seatId: 0));
            await session.PumpOnceAsync(CancellationToken.None);
            channel.Enqueue(PlayerSync(channel, 12, 0, 7, 20));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(2, notifications.Count);
            TableSnapshotChangedEventArgs reconnectBaseline = notifications[1];
            Assert.True(reconnectBaseline.IsFullBaseline);
            Assert.Equal(0, reconnectBaseline.VisibleEventBatch.Count);
            Assert.Equal(12UL, reconnectBaseline.VisibleEventBatch.BindingEpoch);
            Assert.Equal("scope-2", reconnectBaseline.VisibleEventBatch.ScopeId);

            // An old-binding frame with a formerly valid private event must be
            // ignored rather than replayed into the new private cache.
            channel.Enqueue(PlayerPush(
                11,
                0,
                5,
                10,
                6,
                11,
                isFullSnapshot: false,
                PrivateDraw(0, 20, 106, 6, 0)));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(2, notifications.Count);

            channel.Enqueue(PlayerPush(
                12,
                0,
                7,
                20,
                8,
                21,
                isFullSnapshot: false,
                PrivateDraw(0, 22, 108, 8, 0)));
            await session.PumpOnceAsync(CancellationToken.None);

            TableSnapshotChangedEventArgs next = notifications[2];
            Assert.Equal(12UL, next.VisibleEventBatch.BindingEpoch);
            Assert.Equal(108UL, next.VisibleEventBatch.Events.Single().EventSeq);
            Assert.Equal(0, next.VisibleEventBatch.Events.Single().OwnerSeat);
        }

        [Fact]
        public async Task RoundResetAndStalePrivateEventAreNotReplayed()
        {
            (TableSession session, FakeTableChannel channel) = await ConnectedPlayerAsync(0);
            var notifications = new List<TableSnapshotChangedEventArgs>();
            session.SnapshotChanged += (_, e) => notifications.Add(e);

            channel.Enqueue(PlayerPush(
                11,
                0,
                5,
                10,
                6,
                11,
                isFullSnapshot: false,
                PrivateDraw(0, 20, 106, 6, 0)));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Single(notifications);

            // A new round baseline establishes a new snapshot but carries no
            // old private event.
            channel.Enqueue(PlayerPush(
                11,
                0,
                5,
                10,
                7,
                12,
                isFullSnapshot: true,
                roundId: "round-2"));
            await session.PumpOnceAsync(CancellationToken.None);

            TableSnapshotChangedEventArgs reset = notifications[1];
            Assert.True(reset.IsFullBaseline);
            Assert.Equal("round-2", reset.VisibleEventBatch.RoundId);
            Assert.Empty(reset.VisibleEventBatch.Events);
            Assert.Empty(reset.VisibleEventBatch.PlayableEvents);

            // A refresh that repeats an earlier private event must not make it
            // new again after the round reset.
            channel.Enqueue(PlayerPush(
                11,
                0,
                7,
                12,
                7,
                13,
                isFullSnapshot: false,
                roundId: "round-2",
                PrivateDraw(0, 20, 106, 6, 0)));
            await session.PumpOnceAsync(CancellationToken.None);

            TableSnapshotChangedEventArgs stale = notifications[2];
            Assert.Empty(stale.VisibleEventBatch.Events);
            Assert.Empty(stale.VisibleEventBatch.PlayableEvents);
            Assert.Equal("round-2", stale.VisibleEventBatch.RoundId);
        }

        [Fact]
        public async Task MalformedEventRejectsTheFrameWithoutPublishingTheSnapshot()
        {
            (TableSession session, FakeTableChannel channel) = await ConnectedPlayerAsync(0);
            var notifications = new List<TableSnapshotChangedEventArgs>();
            session.SnapshotChanged += (_, e) => notifications.Add(e);

            // Event sequence is beyond the snapshot's own event watermark.
            channel.Enqueue(PlayerPush(
                11,
                0,
                5,
                10,
                6,
                11,
                isFullSnapshot: false,
                PrivateDraw(0, 20, 108, 6, 0)));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Empty(notifications);
            Assert.Equal(TableConnectionState.Faulted, session.State);
            Assert.Contains(ProtocolErrorCode.MalformedMessage, session.LastError ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal(5UL, session.PlayerSnapshot!.StateVersion);
            Assert.Equal(10UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
        }

        [Fact]
        public async Task SubscriberCannotMutateTheConfirmedBatchOrItsTileList()
        {
            (TableSession session, FakeTableChannel channel) = await ConnectedPlayerAsync(0);
            var notifications = new List<TableSnapshotChangedEventArgs>();
            session.SnapshotChanged += (_, e) => notifications.Add(e);

            channel.Enqueue(PlayerPush(
                11,
                0,
                5,
                10,
                6,
                11,
                isFullSnapshot: false,
                PrivateDraw(0, 20, 106, 6, 0)));
            await session.PumpOnceAsync(CancellationToken.None);

            TableVisibleEventBatch batch = notifications[0].VisibleEventBatch;
            TableVisibleEvent visible = batch.Events[0];
            Assert.Throws<NotSupportedException>(() => ((IList<TableVisibleEvent>)batch.Events).Add(visible));
            Assert.Throws<NotSupportedException>(() => ((IList<TableVisibleEvent>)batch.PlayableEvents).Add(visible));
            Assert.Throws<NotSupportedException>(() => ((IList<uint>)visible.TileIds).Add(99));
            Assert.Throws<NotSupportedException>(() => ((IList<uint>)visible.TileIds)[0] = 99);

            Assert.Single(batch.Events);
            Assert.Equal(20u, visible.TileId);

            channel.Enqueue(PlayerPush(
                11,
                0,
                6,
                11,
                6,
                12,
                isFullSnapshot: false,
                PrivateDraw(0, 21, 107, 6, 0)));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Single(batch.Events);
            Assert.Equal(20u, visible.TileId);
            Assert.Equal(107UL, session.PlayerSnapshot!.EventSeq);
        }

        private static async Task<(TableSession Session, FakeTableChannel Channel)> ConnectedPlayerAsync(int seat)
        {
            (TableSession session, FakeTableChannel channel) = await HandshakePlayerAsync(seat);
            channel.Enqueue(PlayerSync(channel, 11, seat, 5, 10));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TableConnectionState.Ready, session.State);
            return (session, channel);
        }

        private static async Task<(TableSession Session, FakeTableChannel Channel)> HandshakePlayerAsync(
            int seat,
            TableSessionOptions? options = null)
        {
            var channel = new FakeTableChannel();
            var session = new TableSession(channel, new RecordingOutboxStore(), options);
            await session.OpenAsync(
                new TableSessionOpenRequest("ticket-1", "client-build-1"),
                CancellationToken.None);
            channel.Enqueue(TestWire.Welcome(11, "scope-1", 5, 10, seatId: seat));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TableConnectionState.Synchronizing, session.State);
            return (session, channel);
        }

        private static async Task<(TableSession Session, FakeTableChannel Channel)> ConnectedSpectatorAsync()
        {
            var channel = new FakeTableChannel();
            var session = new TableSession(channel, new RecordingOutboxStore());
            await session.OpenAsync(
                new TableSessionOpenRequest("ticket-1", "client-build-1"),
                CancellationToken.None);
            channel.Enqueue(TestWire.Welcome(
                11,
                "scope-1",
                5,
                10,
                seatId: ProtocolConstants.NoSeat,
                role: Role.Spectator));
            await session.PumpOnceAsync(CancellationToken.None);
            channel.Enqueue(TestWire.PublicSync(channel, 11, 5, 10));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TableConnectionState.Ready, session.State);
            return (session, channel);
        }

        private static Envelope PlayerSync(FakeTableChannel channel, int bindingEpoch, int seat, ulong streamVersion, ulong projection)
        {
            Envelope envelope = TestWire.Envelope(MessageType.SyncResponse, e => e.SyncResponse = new SyncResponse
            {
                Target = SyncTarget.PlayerStream,
                IsFullSnapshot = true,
                Player = PlayerSnapshot(seat, "round-1", streamVersion, projection, 100 + streamVersion),
                StateVersion = streamVersion,
                EventSeq = 100 + streamVersion
            });
            envelope.CorrelationId = channel.LastSent(MessageType.SyncRequest).CorrelationId;
            return TestWire.Bound(envelope, bindingEpoch, seat);
        }

        private static Envelope PlayerPush(
            int bindingEpoch,
            int seat,
            ulong fromStreamVersion,
            ulong fromProjection,
            ulong toStreamVersion,
            ulong toProjection,
            bool isFullSnapshot,
            params EventEnvelope[] events)
        {
            return PlayerPush(
                bindingEpoch,
                seat,
                fromStreamVersion,
                fromProjection,
                toStreamVersion,
                toProjection,
                isFullSnapshot,
                "round-1",
                events);
        }

        private static Envelope PlayerPush(
            int bindingEpoch,
            int seat,
            ulong fromStreamVersion,
            ulong fromProjection,
            ulong toStreamVersion,
            ulong toProjection,
            bool isFullSnapshot,
            string roundId,
            params EventEnvelope[] events)
        {
            var update = new PlayerUpdate
            {
                Snapshot = PlayerSnapshot(seat, roundId, toStreamVersion, toProjection, 101 + toStreamVersion),
                FromCursor = new WireCursor { StreamVersion = fromStreamVersion, ProjectionSeq = fromProjection },
                Cursor = new WireCursor { StreamVersion = toStreamVersion, ProjectionSeq = toProjection },
                IsFullSnapshot = isFullSnapshot
            };
            for (int i = 0; i < events.Length; i++)
            {
                update.VisibleEvents.Add(events[i]);
            }

            Envelope envelope = TestWire.Envelope(MessageType.PlayerUpdate, e => e.PlayerUpdate = update);
            return TestWire.Bound(envelope, bindingEpoch, seat);
        }

        private static Envelope PublicPush(
            int bindingEpoch,
            ulong fromStreamVersion,
            ulong fromProjection,
            ulong toStreamVersion,
            ulong toProjection,
            params PublicEventEnvelope[] events)
        {
            var update = new PublicUpdate
            {
                Snapshot = PublicSnapshot(toStreamVersion, toProjection, 101 + toStreamVersion),
                FromCursor = new WireCursor { StreamVersion = fromStreamVersion, ProjectionSeq = fromProjection },
                Cursor = new WireCursor { StreamVersion = toStreamVersion, ProjectionSeq = toProjection },
                IsFullSnapshot = false
            };
            for (int i = 0; i < events.Length; i++)
            {
                update.VisibleEvents.Add(events[i]);
            }

            Envelope envelope = TestWire.Envelope(MessageType.PublicUpdate, e => e.PublicUpdate = update);
            return TestWire.Bound(envelope, bindingEpoch, ProtocolConstants.NoSeat);
        }

        private static PlayerSnapshot PlayerSnapshot(int seat, string roundId, ulong streamVersion, ulong projection, ulong eventSeq)
        {
            var snapshot = new PlayerSnapshot
            {
                SessionId = TestSession.SessionId,
                RoundId = roundId,
                Cursor = new WireCursor { StreamVersion = streamVersion, ProjectionSeq = projection },
                StateVersion = streamVersion,
                EventSeq = eventSeq,
                SessionVersion = 1,
                RoundVersion = 2
            };
            snapshot.PublicView = new WirePublicMatchView
            {
                Session = new WirePublicSessionSummary { Finished = false },
                Paused = false,
                SettlementRoundId = roundId
            };
            snapshot.PrivateView = new PlayerPrivateView
            {
                Seat = seat,
                Furiten = false
            };
            snapshot.PrivateView.ClosedTiles.Add(1);
            snapshot.PrivateView.ClosedTiles.Add(2);
            return snapshot;
        }

        private static PublicSnapshot PublicSnapshot(ulong streamVersion, ulong projection, ulong eventSeq)
        {
            var snapshot = new PublicSnapshot
            {
                SessionId = TestSession.SessionId,
                RoundId = "round-1",
                Cursor = new WireCursor { StreamVersion = streamVersion, ProjectionSeq = projection },
                StateVersion = streamVersion,
                EventSeq = eventSeq,
                SessionVersion = 1,
                RoundVersion = 2
            };
            snapshot.View = new WirePublicMatchView
            {
                Session = new WirePublicSessionSummary { Finished = false },
                Paused = false,
                SettlementRoundId = "round-1"
            };
            return snapshot;
        }

        private static EventEnvelope PrivateDraw(int seat, uint tileId, ulong eventSeq, ulong commandVersion, uint ordinal)
        {
            return new EventEnvelope
            {
                EventSchemaVersion = ProtocolConstants.EventSchemaVersion,
                EventSeq = eventSeq,
                CommandVersion = commandVersion,
                EventOrdinal = ordinal,
                EventType = EventType.TileDrawn,
                Visibility = EventVisibility.Private,
                TileDrawn = new TileDrawn
                {
                    TurnNumber = 3,
                    Player = seat,
                    TileId = tileId,
                    WallRemaining = 50
                }
            };
        }

        private static EventEnvelope PublicDiscard(int seat, uint tileId, ulong eventSeq, ulong commandVersion, uint ordinal)
        {
            return new EventEnvelope
            {
                EventSchemaVersion = ProtocolConstants.EventSchemaVersion,
                EventSeq = eventSeq,
                CommandVersion = commandVersion,
                EventOrdinal = ordinal,
                EventType = EventType.TileDiscarded,
                Visibility = EventVisibility.Public,
                TileDiscarded = new TileDiscarded
                {
                    TurnNumber = 3,
                    Player = seat,
                    TileId = tileId,
                    RiichiDiscard = false,
                    Tsumogiri = false
                }
            };
        }

        private static PublicEventEnvelope SpectatorDiscard(int seat, uint tileId, ulong eventSeq, ulong commandVersion, uint ordinal)
        {
            return new PublicEventEnvelope
            {
                EventSchemaVersion = ProtocolConstants.EventSchemaVersion,
                EventSeq = eventSeq,
                CommandVersion = commandVersion,
                EventOrdinal = ordinal,
                EventType = EventType.TileDiscarded,
                Visibility = EventVisibility.Public,
                TileDiscarded = new TileDiscarded
                {
                    TurnNumber = 3,
                    Player = seat,
                    TileId = tileId,
                    RiichiDiscard = false,
                    Tsumogiri = false
                }
            };
        }

        private static PublicEventEnvelope SpectatorDora(ulong eventSeq, ulong commandVersion, uint ordinal)
        {
            return new PublicEventEnvelope
            {
                EventSchemaVersion = ProtocolConstants.EventSchemaVersion,
                EventSeq = eventSeq,
                CommandVersion = commandVersion,
                EventOrdinal = ordinal,
                EventType = EventType.DoraRevealed,
                Visibility = EventVisibility.Public,
                DoraRevealed = new DoraRevealed
                {
                    TurnNumber = 3,
                    IndicatorTileId = 90,
                    IsUraDora = false,
                    UraAuthorized = false
                }
            };
        }

        private static async Task PumpAsync(TableSession session, int count)
        {
            for (int i = 0; i < count; i++)
            {
                await session.PumpOnceAsync(CancellationToken.None);
            }
        }
    }
}
