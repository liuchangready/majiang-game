using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts.Wire;
using Xunit;

namespace Mahjong.Client.Tests
{
    public class IndependentRound2ReviewTests
    {
        [Fact]
        public async Task FullPushCannotRollBackWithinSameBinding()
        {
            var (session, channel, _) = await TestSession.ConnectedAsync();
            channel.Enqueue(TestWire.PlayerPush(11, 5, 10, 6, 11));
            await session.PumpOnceAsync(CancellationToken.None);
            channel.Enqueue(TestWire.PlayerPush(11, 0, 0, 4, 9, isFullSnapshot: true));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(11UL, session.ConfirmedCursor!.Value.ProjectionSeq.Value);
            Assert.Equal(6UL, session.PlayerSnapshot!.StateVersion);
        }

        [Fact]
        public async Task CorrelatedSyncMustNotRollBackStateAndEventVersions()
        {
            var (session, channel, _) = await TestSession.ConnectedAsync();
            channel.Enqueue(TestWire.PlayerPush(11, 8, 13, 9, 14));
            await session.PumpOnceAsync(CancellationToken.None);
            var response = TestWire.PlayerSync(channel, 11, 5, 10);
            response.SyncResponse.StateVersion = 1;
            response.SyncResponse.EventSeq = 1;
            response.SyncResponse.Player.StateVersion = 1;
            response.SyncResponse.Player.EventSeq = 1;
            channel.Enqueue(response);
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(5UL, session.PlayerSnapshot!.StateVersion);
            Assert.Equal(105UL, session.PlayerSnapshot.EventSeq);
        }

        [Fact]
        public async Task AdvancingPushMustNotRollBackSnapshotVersions()
        {
            var (session, channel, _) = await TestSession.ConnectedAsync();
            var push = TestWire.PlayerPush(11, 5, 10, 6, 11);
            push.PlayerUpdate.Snapshot.StateVersion = 1;
            push.PlayerUpdate.Snapshot.EventSeq = 1;
            channel.Enqueue(push);
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(5UL, session.PlayerSnapshot!.StateVersion);
            Assert.Equal(105UL, session.PlayerSnapshot.EventSeq);
        }

        [Fact]
        public async Task ZeroSyncVersionCannotBypassSnapshotVersionAgreement()
        {
            var (session, channel, _) = await TestSession.HandshakeAsync();
            var response = TestWire.PlayerSync(channel, 11, 5, 10);
            response.SyncResponse.Player.StateVersion = 0;
            response.SyncResponse.Player.EventSeq = 0;
            channel.Enqueue(response);
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Null(session.PlayerSnapshot);
            Assert.NotEqual(TableConnectionState.Ready, session.State);
        }
    }
}
