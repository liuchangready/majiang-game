using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts.Wire;
using Xunit;

namespace Mahjong.Client.Tests
{
    /// <summary>
    /// Companion regressions for IndependentRound3ReviewTests. A buffered full
    /// PUSH is a baseline candidate, not automatically stale: it may cross a
    /// cursor gap, but only the same binding's cursor/state_version/event_seq
    /// floors decide whether it is newer, duplicate, old or a version rollback.
    /// </summary>
    public class IndependentRound3RegressionTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task NewerBufferedFullIsAppliedWithFullNotification(bool spectator)
        {
            var (session, channel) = await HandshakeAsync(spectator);
            var events = new List<TableSnapshotChangedEventArgs>();
            session.SnapshotChanged += (_, e) => events.Add(e);

            channel.Enqueue(FullPush(spectator, 5, 10, 6, 11));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(1, session.EarlyBufferItemCount);

            channel.Enqueue(SyncResponse(spectator, channel, 5, 10));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(0, session.EarlyBufferItemCount);
            Assert.Equal(11UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(6UL, session.ConfirmedCursor.Value.StreamVersion.Value);
            Assert.Equal(6UL, StateVersion(session, spectator));
            Assert.Equal(106UL, EventSeq(session, spectator));
            Assert.Equal(1, channel.CountSent(MessageType.SyncRequest));

            // The applied frame must still be observable as a full baseline.
            Assert.NotEmpty(events);
            TableSnapshotChangedEventArgs last = events[events.Count - 1];
            Assert.True(last.IsFullBaseline);
            Assert.Equal(11UL, last.Cursor.ProjectionSeq.Value);
            Assert.Equal(6UL, last.Cursor.StreamVersion.Value);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OutOfOrderBufferedFullsSettleAtTheNewestCursor(bool ascending)
        {
            var (session, channel) = await HandshakeAsync(spectator: false);

            if (ascending)
            {
                channel.Enqueue(FullPush(false, 5, 10, 6, 11));
                channel.Enqueue(FullPush(false, 6, 11, 7, 13));
            }
            else
            {
                channel.Enqueue(FullPush(false, 5, 10, 7, 13));
                channel.Enqueue(FullPush(false, 6, 11, 6, 11));
            }

            await PumpAsync(session, 2);
            Assert.Equal(2, session.EarlyBufferItemCount);

            channel.Enqueue(TestWire.PlayerSync(channel, 11, 5, 10));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(0, session.EarlyBufferItemCount);
            Assert.Equal(13UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(7UL, session.ConfirmedCursor.Value.StreamVersion.Value);
            Assert.Equal(7UL, session.PlayerSnapshot!.StateVersion);
            Assert.Equal(107UL, session.PlayerSnapshot.EventSeq);
            Assert.Equal(1, channel.CountSent(MessageType.SyncRequest));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OutOfOrderBufferedFullsSettleAtTheNewestCursorForBothStreams(bool spectator)
        {
            var (session, channel) = await HandshakeAsync(spectator);

            // Newest first: the older full queued behind it is stale once the
            // newest one has been applied.
            channel.Enqueue(FullPush(spectator, 5, 10, 7, 13));
            channel.Enqueue(FullPush(spectator, 6, 11, 6, 11));
            await PumpAsync(session, 2);
            Assert.Equal(2, session.EarlyBufferItemCount);

            channel.Enqueue(SyncResponse(spectator, channel, 5, 10));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(0, session.EarlyBufferItemCount);
            Assert.Equal(13UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(7UL, session.ConfirmedCursor.Value.StreamVersion.Value);
            Assert.Equal(7UL, StateVersion(session, spectator));
            Assert.Equal(107UL, EventSeq(session, spectator));
            Assert.Equal(1, channel.CountSent(MessageType.SyncRequest));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BufferedFullThenFollowingIncrementalsApplyInOrder(bool spectator)
        {
            var (session, channel) = await HandshakeAsync(spectator);

            channel.Enqueue(FullPush(spectator, 5, 10, 6, 11));
            channel.Enqueue(IncrementalPush(spectator, 6, 11, 6, 12));
            channel.Enqueue(IncrementalPush(spectator, 6, 12, 7, 13));
            await PumpAsync(session, 3);
            Assert.Equal(3, session.EarlyBufferItemCount);

            channel.Enqueue(SyncResponse(spectator, channel, 5, 10));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(0, session.EarlyBufferItemCount);
            Assert.Equal(13UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(7UL, session.ConfirmedCursor.Value.StreamVersion.Value);
            Assert.Equal(7UL, StateVersion(session, spectator));
            Assert.Equal(107UL, EventSeq(session, spectator));
            Assert.Equal(1, channel.CountSent(MessageType.SyncRequest));
        }

        [Fact]
        public async Task BufferedFullAndIncrementalArrivingOutOfOrderConverge()
        {
            var (session, channel) = await HandshakeAsync(spectator: false);

            // The incremental names the full's cursor as its predecessor, but
            // arrives first. The drain must apply the full and then the
            // incremental, not discard either.
            channel.Enqueue(TestWire.PlayerPush(11, 6, 11, 6, 12));
            channel.Enqueue(FullPush(false, 5, 10, 6, 11));
            await PumpAsync(session, 2);
            Assert.Equal(2, session.EarlyBufferItemCount);

            channel.Enqueue(TestWire.PlayerSync(channel, 11, 5, 10));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(0, session.EarlyBufferItemCount);
            Assert.Equal(12UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(6UL, session.ConfirmedCursor.Value.StreamVersion.Value);
            Assert.Equal(6UL, session.PlayerSnapshot!.StateVersion);
            Assert.Equal(106UL, session.PlayerSnapshot.EventSeq);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BufferedFullMayCrossAProjectionGap(bool spectator)
        {
            var (session, channel) = await HandshakeAsync(spectator);

            // A full baseline is a reset, so its from cursor may be unrelated
            // and its projection may jump arbitrarily far ahead.
            channel.Enqueue(FullPush(spectator, 0, 0, 9, 20));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(1, session.EarlyBufferItemCount);

            channel.Enqueue(SyncResponse(spectator, channel, 5, 10));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(0, session.EarlyBufferItemCount);
            Assert.Equal(20UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(9UL, session.ConfirmedCursor.Value.StreamVersion.Value);
            Assert.Equal(9UL, StateVersion(session, spectator));
            Assert.Equal(109UL, EventSeq(session, spectator));
        }

        [Theory]
        [InlineData(false, 4UL, 106UL)]
        [InlineData(false, 6UL, 104UL)]
        [InlineData(true, 4UL, 106UL)]
        [InlineData(true, 6UL, 104UL)]
        public async Task BufferedFullWithRegressedVersionsIsRejected(
            bool spectator,
            ulong stateVersion,
            ulong eventSeq)
        {
            var (session, channel) = await HandshakeAsync(spectator);

            Envelope full = FullPush(spectator, 5, 10, 6, 11);
            Snapshot(full, spectator).StateVersion = stateVersion;
            Snapshot(full, spectator).EventSeq = eventSeq;
            channel.Enqueue(full);
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(1, session.EarlyBufferItemCount);

            channel.Enqueue(SyncResponse(spectator, channel, 5, 10));
            await session.PumpOnceAsync(CancellationToken.None);

            // The correlated baseline is authoritative. The illegal newer full
            // is refused rather than applied or silently left as Ready-stale:
            // the no-candidate path clears it and requests a fresh baseline.
            Assert.NotEqual(11UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(10UL, session.ConfirmedCursor.Value.ProjectionSeq.Value);
            Assert.Equal(5UL, session.ConfirmedCursor.Value.StreamVersion.Value);
            Assert.Equal(5UL, StateVersion(session, spectator));
            Assert.Equal(105UL, EventSeq(session, spectator));
            Assert.Equal(0, session.EarlyBufferItemCount);
            Assert.Equal(TableConnectionState.Synchronizing, session.State);
            Assert.Equal(2, channel.CountSent(MessageType.SyncRequest));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BufferedOldAndDuplicateFullsAreRejectedWithoutLosingTheBaseline(bool spectator)
        {
            var (session, channel) = await HandshakeAsync(spectator);

            // Old (cursor below the incoming baseline), old-by-stream-version
            // and duplicate (equal cursor) full frames are all queued before
            // the correlated baseline. None may replace it, and none may force
            // a pointless resync once the baseline has settled.
            channel.Enqueue(FullPush(spectator, 0, 0, 4, 9));
            channel.Enqueue(FullPush(spectator, 0, 0, 5, 10));
            channel.Enqueue(FullPush(spectator, 0, 0, 4, 20));
            await PumpAsync(session, 3);
            Assert.Equal(3, session.EarlyBufferItemCount);

            channel.Enqueue(SyncResponse(spectator, channel, 5, 10));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(0, session.EarlyBufferItemCount);
            Assert.Equal(10UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(5UL, session.ConfirmedCursor.Value.StreamVersion.Value);
            Assert.Equal(5UL, StateVersion(session, spectator));
            Assert.Equal(105UL, EventSeq(session, spectator));
            Assert.Equal(1, channel.CountSent(MessageType.SyncRequest));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BufferedDuplicateFullAfterNewerFullIsNotReapplied(bool spectator)
        {
            var (session, channel) = await HandshakeAsync(spectator);
            var fullEvents = 0;
            session.SnapshotChanged += (_, e) =>
            {
                if (e.IsFullBaseline && e.Cursor.ProjectionSeq.Value == 11)
                {
                    fullEvents++;
                }
            };

            channel.Enqueue(FullPush(spectator, 5, 10, 6, 11));
            channel.Enqueue(FullPush(spectator, 6, 11, 6, 11));
            await PumpAsync(session, 2);
            Assert.Equal(2, session.EarlyBufferItemCount);

            channel.Enqueue(SyncResponse(spectator, channel, 5, 10));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(0, session.EarlyBufferItemCount);
            Assert.Equal(11UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(6UL, StateVersion(session, spectator));
            Assert.Equal(1, fullEvents);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BufferedFullFromAnotherBindingIsNeverStoredOrApplied(bool spectator)
        {
            var (session, channel) = await HandshakeAsync(spectator);

            channel.Enqueue(FullPush(spectator, 0, 0, 9, 20, bindingEpoch: 22));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(0, session.EarlyBufferItemCount);

            channel.Enqueue(SyncResponse(spectator, channel, 5, 10));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(10UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(5UL, session.ConfirmedCursor.Value.StreamVersion.Value);
            Assert.Equal(5UL, StateVersion(session, spectator));
            Assert.Equal(105UL, EventSeq(session, spectator));
        }

        [Fact]
        public async Task OldFullAfterAnAppliedNewerFullCannotRollBack()
        {
            var (session, channel) = await HandshakeAsync(spectator: false);

            channel.Enqueue(FullPush(false, 5, 10, 6, 11));
            channel.Enqueue(TestWire.PlayerSync(channel, 11, 5, 10));
            await PumpAsync(session, 2);
            Assert.Equal(11UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);

            // Ready-state sibling of the early-buffer rule: a stale full must
            // still be rejected after the newer buffered baseline was applied.
            channel.Enqueue(FullPush(false, 0, 0, 5, 10));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(11UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(6UL, session.PlayerSnapshot!.StateVersion);
            Assert.Equal(106UL, session.PlayerSnapshot.EventSeq);
        }

        private static async Task PumpAsync(TableSession session, int count)
        {
            for (int i = 0; i < count; i++)
            {
                await session.PumpOnceAsync(CancellationToken.None);
            }
        }

        private static async Task<(TableSession Session, FakeTableChannel Channel)> HandshakeAsync(bool spectator)
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
                seatId: spectator ? -1 : 0,
                role: spectator ? Role.Spectator : Role.Player));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TableConnectionState.Synchronizing, session.State);
            return (session, channel);
        }

        private static Envelope SyncResponse(
            bool spectator,
            FakeTableChannel channel,
            ulong streamVersion,
            ulong projection)
        {
            return spectator
                ? TestWire.PublicSync(channel, 11, streamVersion, projection)
                : TestWire.PlayerSync(channel, 11, streamVersion, projection);
        }

        private static Envelope FullPush(
            bool spectator,
            ulong fromStreamVersion,
            ulong fromProjection,
            ulong toStreamVersion,
            ulong toProjection,
            int bindingEpoch = 11)
        {
            if (spectator)
            {
                Envelope push = TestWire.PublicPush(
                    bindingEpoch,
                    fromStreamVersion,
                    fromProjection,
                    toStreamVersion,
                    toProjection);
                push.PublicUpdate.IsFullSnapshot = true;
                return push;
            }

            return TestWire.PlayerPush(
                bindingEpoch,
                fromStreamVersion,
                fromProjection,
                toStreamVersion,
                toProjection,
                isFullSnapshot: true);
        }

        private static Envelope IncrementalPush(
            bool spectator,
            ulong fromStreamVersion,
            ulong fromProjection,
            ulong toStreamVersion,
            ulong toProjection)
        {
            return spectator
                ? TestWire.PublicPush(11, fromStreamVersion, fromProjection, toStreamVersion, toProjection)
                : TestWire.PlayerPush(11, fromStreamVersion, fromProjection, toStreamVersion, toProjection);
        }

        private static SnapshotVersionView Snapshot(Envelope envelope, bool spectator)
        {
            return spectator
                ? new SnapshotVersionView(envelope.PublicUpdate.Snapshot)
                : new SnapshotVersionView(envelope.PlayerUpdate.Snapshot);
        }

        private static ulong StateVersion(TableSession session, bool spectator)
        {
            return spectator
                ? session.PublicSnapshot!.StateVersion
                : session.PlayerSnapshot!.StateVersion;
        }

        private static ulong EventSeq(TableSession session, bool spectator)
        {
            return spectator
                ? session.PublicSnapshot!.EventSeq
                : session.PlayerSnapshot!.EventSeq;
        }

        /// <summary>Overload-free mutable view over the two snapshot bodies.</summary>
        private sealed class SnapshotVersionView
        {
            private readonly PlayerSnapshot? _player;
            private readonly PublicSnapshot? _public;

            public SnapshotVersionView(PlayerSnapshot player)
            {
                _player = player;
            }

            public SnapshotVersionView(PublicSnapshot publicSnapshot)
            {
                _public = publicSnapshot;
            }

            public ulong StateVersion
            {
                get => _player != null ? _player.StateVersion : _public!.StateVersion;
                set
                {
                    if (_player != null)
                    {
                        _player.StateVersion = value;
                    }
                    else
                    {
                        _public!.StateVersion = value;
                    }
                }
            }

            public ulong EventSeq
            {
                get => _player != null ? _player.EventSeq : _public!.EventSeq;
                set
                {
                    if (_player != null)
                    {
                        _player.EventSeq = value;
                    }
                    else
                    {
                        _public!.EventSeq = value;
                    }
                }
            }
        }
    }
}
