using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts.Wire;
using Xunit;

namespace Mahjong.Client.Tests
{
    public class IndependentRound3ReviewTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task NewerFullPushBeforeSyncMustNotBeSilentlyLost(bool spectator)
        {
            var channel = new FakeTableChannel();
            var session = new TableSession(channel, new RecordingOutboxStore());
            await session.OpenAsync(new TableSessionOpenRequest("ticket-1", "client-build-1"), CancellationToken.None);
            channel.Enqueue(TestWire.Welcome(11, "scope-1", 5, 10,
                seatId: spectator ? -1 : 0,
                role: spectator ? Role.Spectator : Role.Player));
            await session.PumpOnceAsync(CancellationToken.None);

            var push = spectator
                ? TestWire.PublicPush(11, 5, 10, 6, 11)
                : TestWire.PlayerPush(11, 5, 10, 6, 11, isFullSnapshot: true);
            if (spectator)
            {
                push.PublicUpdate.IsFullSnapshot = true;
            }

            channel.Enqueue(push);
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(1, session.EarlyBufferItemCount);
            int syncRequests = channel.CountSent(MessageType.SyncRequest);

            // The full PUSH is newer than the correlated baseline that follows.
            // With no later traffic, dropping it leaves a Ready cache stale forever.
            channel.Enqueue(spectator
                ? TestWire.PublicSync(channel, 11, 5, 10)
                : TestWire.PlayerSync(channel, 11, 5, 10));
            await session.PumpOnceAsync(CancellationToken.None);

            bool applied = session.ConfirmedCursor!.Value.ProjectionSeq.Value >= 11;
            bool recovering = session.State == TableConnectionState.Synchronizing &&
                channel.CountSent(MessageType.SyncRequest) > syncRequests;
            Assert.True(applied || recovering,
                $"Newer full PUSH was lost: state={session.State}, " +
                $"projection={session.ConfirmedCursor.Value.ProjectionSeq.Value}, " +
                $"syncRequests={channel.CountSent(MessageType.SyncRequest)}.");
        }
    }
}
