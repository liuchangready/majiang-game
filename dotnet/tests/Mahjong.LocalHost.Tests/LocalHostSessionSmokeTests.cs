using System;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Xunit;

namespace Mahjong.LocalHost.Tests
{
    public sealed class LocalHostSessionSmokeTests
    {
        [Fact]
        public async Task ClientSessionReachesReadyOverLoopback()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, ct);
            string ticket = LocalTableHost.IssueTicket(harness.Options);
            await channel.OpenAsync(new TableChannelConnectRequest(ticket, "p41-test", null), ct);

            var session = new TableSession(channel, new InMemoryOutboxStore());
            try
            {
                await session.OpenAsync(new TableSessionOpenRequest(ticket, "p41-test"), ct);
                for (int i = 0; i < 10 && session.State != TableConnectionState.Ready; i++)
                {
                    await session.PumpOnceAsync(ct);
                }

                Assert.Equal(TableConnectionState.Ready, session.State);
                Assert.NotNull(session.Binding);
                Assert.NotNull(session.PlayerSnapshot);
                Assert.Equal(0, session.Binding!.SeatId);
                Assert.NotNull(session.ConfirmedCursor);
            }
            finally
            {
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }
    }
}
