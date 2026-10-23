using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts.Wire;
using Xunit;

namespace Mahjong.Client.Tests
{
    /// <summary>
    /// Companion regressions for IndependentRound2ReviewTests: they keep the
    /// reviewer's exact semantics and extend the same invariants to the
    /// Player/Public streams, independent state/event counters, duplicate and
    /// old full snapshots, new bindings, WELCOME floors and the early buffer.
    /// </summary>
    public class IndependentRound2RegressionTests
    {
        [Fact]
        public async Task FullPushAtSameCursorCannotRollBackVersions()
        {
            var (session, channel, _) = await TestSession.ConnectedAsync();
            channel.Enqueue(TestWire.PlayerPush(11, 5, 10, 6, 11));
            await session.PumpOnceAsync(CancellationToken.None);

            Envelope duplicateFull = TestWire.PlayerPush(11, 6, 11, 6, 11, isFullSnapshot: true);
            duplicateFull.PlayerUpdate.Snapshot.StateVersion = 5;
            duplicateFull.PlayerUpdate.Snapshot.EventSeq = 105;
            channel.Enqueue(duplicateFull);
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(11UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(6UL, session.ConfirmedCursor.Value.StreamVersion.Value);
            Assert.Equal(6UL, session.PlayerSnapshot!.StateVersion);
            Assert.Equal(106UL, session.PlayerSnapshot.EventSeq);
        }

        [Fact]
        public async Task FullPushWithHigherCursorCannotRollBackStateVersionOnly()
        {
            var (session, channel, _) = await TestSession.ConnectedAsync();
            channel.Enqueue(TestWire.PlayerPush(11, 5, 10, 6, 11));
            await session.PumpOnceAsync(CancellationToken.None);

            Envelope full = TestWire.PlayerPush(11, 6, 11, 7, 12, isFullSnapshot: true);
            full.PlayerUpdate.Snapshot.StateVersion = 5;
            channel.Enqueue(full);
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(11UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(6UL, session.PlayerSnapshot!.StateVersion);
            Assert.Equal(106UL, session.PlayerSnapshot.EventSeq);
        }

        [Fact]
        public async Task FullPushWithHigherCursorCannotRollBackEventSeqOnly()
        {
            var (session, channel, _) = await TestSession.ConnectedAsync();
            channel.Enqueue(TestWire.PlayerPush(11, 5, 10, 6, 11));
            await session.PumpOnceAsync(CancellationToken.None);

            Envelope full = TestWire.PlayerPush(11, 6, 11, 7, 12, isFullSnapshot: true);
            full.PlayerUpdate.Snapshot.EventSeq = 105;
            channel.Enqueue(full);
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(11UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(6UL, session.PlayerSnapshot!.StateVersion);
            Assert.Equal(106UL, session.PlayerSnapshot.EventSeq);
        }

        [Fact]
        public async Task AdvancingPushCannotRollBackStateVersionOnly()
        {
            var (session, channel, _) = await TestSession.ConnectedAsync();
            Envelope push = TestWire.PlayerPush(11, 5, 10, 6, 11);
            push.PlayerUpdate.Snapshot.StateVersion = 4;
            channel.Enqueue(push);
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(10UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(5UL, session.PlayerSnapshot!.StateVersion);
            Assert.Equal(105UL, session.PlayerSnapshot.EventSeq);
        }

        [Fact]
        public async Task AdvancingPushCannotRollBackEventSeqOnly()
        {
            var (session, channel, _) = await TestSession.ConnectedAsync();
            Envelope push = TestWire.PlayerPush(11, 5, 10, 6, 11);
            push.PlayerUpdate.Snapshot.EventSeq = 104;
            channel.Enqueue(push);
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(10UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(5UL, session.PlayerSnapshot!.StateVersion);
            Assert.Equal(105UL, session.PlayerSnapshot.EventSeq);
        }

        [Fact]
        public async Task RefreshWithEqualVersionsIsAccepted()
        {
            var (session, channel, _) = await TestSession.ConnectedAsync();
            channel.Enqueue(TestWire.PlayerPush(11, 5, 10, 6, 11));
            await session.PumpOnceAsync(CancellationToken.None);

            // Same table state, next projection emission: cursor moves, both
            // authoritative versions legitimately stay equal.
            channel.Enqueue(TestWire.PlayerPush(11, 6, 11, 6, 12));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(12UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(6UL, session.PlayerSnapshot!.StateVersion);
            Assert.Equal(106UL, session.PlayerSnapshot.EventSeq);
        }

        [Fact]
        public async Task NewBindingMayResetVersionFloors()
        {
            var (session, channel, _) = await TestSession.ConnectedAsync(bindingEpoch: 11);
            channel.Enqueue(TestWire.PlayerPush(11, 5, 10, 6, 11));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(6UL, session.PlayerSnapshot!.StateVersion);
            Assert.Equal(106UL, session.PlayerSnapshot.EventSeq);

            // A new binding may advertise lower counters. It becomes the new
            // floor, so the old confirmed versions must not block its baseline.
            await TestSession.ReconnectAsync(session, channel, 22, "scope-2", 4, 9);

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(9UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(4UL, session.PlayerSnapshot!.StateVersion);
            Assert.Equal(104UL, session.PlayerSnapshot.EventSeq);
        }

        [Fact]
        public async Task WelcomeBaselineIsTheFloorBeforeTheFirstSync()
        {
            var (session, channel, _) = await TestSession.HandshakeAsync();
            string correlation = channel.LastSent(MessageType.SyncRequest).CorrelationId;

            Envelope regressed = TestWire.PlayerSync(11, 5, 10, correlationId: correlation);
            regressed.SyncResponse.StateVersion = 4;
            regressed.SyncResponse.EventSeq = 104;
            regressed.SyncResponse.Player.StateVersion = 4;
            regressed.SyncResponse.Player.EventSeq = 104;
            channel.Enqueue(regressed);
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Synchronizing, session.State);
            Assert.Null(session.PlayerSnapshot);
            Assert.Null(session.ConfirmedCursor);

            channel.Enqueue(TestWire.PlayerSync(11, 5, 10, correlationId: correlation));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(5UL, session.PlayerSnapshot!.StateVersion);
            Assert.Equal(105UL, session.PlayerSnapshot.EventSeq);
        }

        [Fact]
        public async Task CorrelatedSyncWithAgreeingButRegressedVersionsIsIgnored()
        {
            var (session, channel, _) = await TestSession.ConnectedAsync();
            channel.Enqueue(TestWire.PlayerPush(11, 8, 13, 9, 14));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TableConnectionState.Synchronizing, session.State);

            string correlation = channel.LastSent(MessageType.SyncRequest).CorrelationId;
            Envelope regressed = TestWire.PlayerSync(11, 5, 10, correlationId: correlation);
            regressed.SyncResponse.StateVersion = 4;
            regressed.SyncResponse.EventSeq = 104;
            regressed.SyncResponse.Player.StateVersion = 4;
            regressed.SyncResponse.Player.EventSeq = 104;
            channel.Enqueue(regressed);
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Synchronizing, session.State);
            Assert.Equal(10UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(5UL, session.PlayerSnapshot!.StateVersion);
            Assert.Equal(105UL, session.PlayerSnapshot.EventSeq);

            channel.Enqueue(TestWire.PlayerSync(11, 9, 14, correlationId: correlation));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(14UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(9UL, session.PlayerSnapshot!.StateVersion);
            Assert.Equal(109UL, session.PlayerSnapshot.EventSeq);
        }

        [Theory]
        [InlineData(4UL, 105UL)]
        [InlineData(5UL, 104UL)]
        public async Task CorrelatedSyncCannotRegressOneVersionFamilyAlone(ulong stateVersion, ulong eventSeq)
        {
            var (session, channel, _) = await TestSession.ConnectedAsync();
            channel.Enqueue(TestWire.PlayerPush(11, 8, 13, 9, 14));
            await session.PumpOnceAsync(CancellationToken.None);

            string correlation = channel.LastSent(MessageType.SyncRequest).CorrelationId;
            Envelope regressed = TestWire.PlayerSync(11, 5, 10, correlationId: correlation);
            regressed.SyncResponse.StateVersion = stateVersion;
            regressed.SyncResponse.EventSeq = eventSeq;
            regressed.SyncResponse.Player.StateVersion = stateVersion;
            regressed.SyncResponse.Player.EventSeq = eventSeq;
            channel.Enqueue(regressed);
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Synchronizing, session.State);
            Assert.Equal(10UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(5UL, session.PlayerSnapshot!.StateVersion);
            Assert.Equal(105UL, session.PlayerSnapshot.EventSeq);
        }

        [Fact]
        public async Task SyncResponseZeroStateVersionCannotBeTreatedAsOmitted()
        {
            var (session, channel, _) = await TestSession.HandshakeAsync();
            Envelope response = TestWire.PlayerSync(channel, 11, 5, 10);
            response.SyncResponse.StateVersion = 0;
            channel.Enqueue(response);
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Null(session.PlayerSnapshot);
            Assert.NotEqual(TableConnectionState.Ready, session.State);
        }

        [Fact]
        public async Task SyncResponseZeroEventSeqCannotBeTreatedAsOmitted()
        {
            var (session, channel, _) = await TestSession.HandshakeAsync();
            Envelope response = TestWire.PlayerSync(channel, 11, 5, 10);
            response.SyncResponse.EventSeq = 0;
            channel.Enqueue(response);
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Null(session.PlayerSnapshot);
            Assert.NotEqual(TableConnectionState.Ready, session.State);
        }

        [Fact]
        public async Task BothZeroVersionsAreAcceptedWhenWelcomeBaselineIsZero()
        {
            var channel = new FakeTableChannel();
            var store = new RecordingOutboxStore();
            var session = new TableSession(channel, store);

            await session.OpenAsync(new TableSessionOpenRequest("ticket-1", "client-build-1"), CancellationToken.None);
            Envelope welcome = TestWire.Welcome(11, "scope-1", 0, 0);
            welcome.Welcome.Baseline.EventSeq = 0;
            channel.Enqueue(welcome);
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TableConnectionState.Synchronizing, session.State);

            Envelope response = TestWire.PlayerSync(channel, 11, 0, 0);
            response.SyncResponse.EventSeq = 0;
            response.SyncResponse.Player.EventSeq = 0;
            channel.Enqueue(response);
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.NotNull(session.PlayerSnapshot);
            Assert.Equal(0UL, session.PlayerSnapshot!.StateVersion);
            Assert.Equal(0UL, session.PlayerSnapshot.EventSeq);
            Assert.Equal(0UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
        }

        [Fact]
        public async Task EarlyBufferedPushWithRegressedVersionsIsNotApplied()
        {
            var (session, channel, _) = await TestSession.HandshakeAsync();
            Envelope regressed = TestWire.PlayerPush(11, 5, 10, 6, 11);
            regressed.PlayerUpdate.Snapshot.StateVersion = 1;
            regressed.PlayerUpdate.Snapshot.EventSeq = 1;
            channel.Enqueue(regressed);
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(1, session.EarlyBufferItemCount);

            channel.Enqueue(TestWire.PlayerSync(channel, 11, 5, 10));
            await session.PumpOnceAsync(CancellationToken.None);

            // The baseline itself is authoritative and must be applied, but the
            // buffered push may not overwrite it with regressed versions.
            Assert.Equal(10UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(5UL, session.PlayerSnapshot!.StateVersion);
            Assert.Equal(105UL, session.PlayerSnapshot.EventSeq);
            Assert.Equal(0, session.EarlyBufferItemCount);
            Assert.Equal(TableConnectionState.Synchronizing, session.State);
            Assert.Equal(2, channel.CountSent(MessageType.SyncRequest));
        }

        [Fact]
        public async Task PublicFullPushCannotRollBackWithinSameBinding()
        {
            var (session, channel, _) = await TestSession.ConnectedSpectatorAsync();

            Envelope full = TestWire.PublicPush(11, 5, 10, 4, 9);
            full.PublicUpdate.IsFullSnapshot = true;
            channel.Enqueue(full);
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(10UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(5UL, session.ConfirmedCursor.Value.StreamVersion.Value);
            Assert.Equal(5UL, session.PublicSnapshot!.StateVersion);
            Assert.Equal(105UL, session.PublicSnapshot.EventSeq);
        }

        [Fact]
        public async Task PublicAdvancingPushCannotRollBackVersions()
        {
            var (session, channel, _) = await TestSession.ConnectedSpectatorAsync();
            Envelope push = TestWire.PublicPush(11, 5, 10, 6, 11);
            push.PublicUpdate.Snapshot.StateVersion = 4;
            push.PublicUpdate.Snapshot.EventSeq = 104;
            channel.Enqueue(push);
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(10UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(5UL, session.PublicSnapshot!.StateVersion);
            Assert.Equal(105UL, session.PublicSnapshot.EventSeq);
        }

        [Fact]
        public async Task PublicWelcomeBaselineIsTheFloorBeforeTheFirstSync()
        {
            var (session, channel, _) = await SpectatorHandshakeAsync();
            string correlation = channel.LastSent(MessageType.SyncRequest).CorrelationId;

            Envelope regressed = TestWire.PublicSync(11, 5, 10, correlation);
            regressed.SyncResponse.StateVersion = 4;
            regressed.SyncResponse.EventSeq = 104;
            regressed.SyncResponse.Public.StateVersion = 4;
            regressed.SyncResponse.Public.EventSeq = 104;
            channel.Enqueue(regressed);
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Synchronizing, session.State);
            Assert.Null(session.PublicSnapshot);
            Assert.Null(session.ConfirmedCursor);

            channel.Enqueue(TestWire.PublicSync(11, 5, 10, correlation));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(5UL, session.PublicSnapshot!.StateVersion);
            Assert.Equal(105UL, session.PublicSnapshot.EventSeq);
        }

        [Fact]
        public async Task PublicCorrelatedSyncWithRegressedVersionsIsIgnored()
        {
            var (session, channel, _) = await TestSession.ConnectedSpectatorAsync();
            channel.Enqueue(TestWire.PublicPush(11, 8, 13, 9, 14));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TableConnectionState.Synchronizing, session.State);

            string correlation = channel.LastSent(MessageType.SyncRequest).CorrelationId;
            Envelope regressed = TestWire.PublicSync(11, 5, 10, correlation);
            regressed.SyncResponse.StateVersion = 4;
            regressed.SyncResponse.EventSeq = 104;
            regressed.SyncResponse.Public.StateVersion = 4;
            regressed.SyncResponse.Public.EventSeq = 104;
            channel.Enqueue(regressed);
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Synchronizing, session.State);
            Assert.Equal(10UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(5UL, session.PublicSnapshot!.StateVersion);
            Assert.Equal(105UL, session.PublicSnapshot.EventSeq);

            channel.Enqueue(TestWire.PublicSync(11, 9, 14, correlation));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(14UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(9UL, session.PublicSnapshot!.StateVersion);
            Assert.Equal(109UL, session.PublicSnapshot.EventSeq);
        }

        private static async Task<(TableSession Session, FakeTableChannel Channel, RecordingOutboxStore Store)> SpectatorHandshakeAsync(
            int bindingEpoch = 11,
            string scopeId = "scope-1",
            ulong baselineStreamVersion = 5,
            ulong baselineProjection = 10)
        {
            var channel = new FakeTableChannel();
            var store = new RecordingOutboxStore();
            var session = new TableSession(channel, store);

            await session.OpenAsync(new TableSessionOpenRequest("ticket-1", "client-build-1"), CancellationToken.None);
            channel.Enqueue(TestWire.Welcome(
                bindingEpoch,
                scopeId,
                baselineStreamVersion,
                baselineProjection,
                seatId: -1,
                role: Role.Spectator));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TableConnectionState.Synchronizing, session.State);
            return (session, channel, store);
        }
    }
}
