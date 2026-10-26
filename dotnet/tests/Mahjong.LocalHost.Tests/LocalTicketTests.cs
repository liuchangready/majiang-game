using System;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Xunit;

namespace Mahjong.LocalHost.Tests
{
    public sealed class LocalTicketTests
    {
        [Fact]
        public void TicketIsSingleUseAndRoomBound()
        {
            string ticket = LocalTicketIssuer.Issue("room-a");
            Assert.True(LocalTicketIssuer.TryConsume(ticket, "room-a"));
            Assert.False(LocalTicketIssuer.TryConsume(ticket, "room-a"));

            string other = LocalTicketIssuer.Issue("room-b");
            Assert.False(LocalTicketIssuer.TryConsume(other, "room-a"));
            Assert.False(LocalTicketIssuer.TryConsume(null, "room-a"));
        }
        [Fact]
        public async Task ForgedTicketIsRejectedByARealSessionHandshake()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, ct);
            string channelTicket = LocalTableHost.IssueTicket(harness.Options);
            await channel.OpenAsync(new TableChannelConnectRequest(channelTicket, "p41-test", null), ct);
            var session = new TableSession(channel, new InMemoryOutboxStore());
            try
            {
                await session.OpenAsync(new TableSessionOpenRequest("NEVER_ISSUED", "p41-test"), ct);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                for (int i = 0; i < 10 && session.State != TableConnectionState.Faulted; i++)
                {
                    await session.PumpOnceAsync(cts.Token);
                }

                Assert.Equal(TableConnectionState.Faulted, session.State);
                Assert.StartsWith(Mahjong.Contracts.ProtocolErrorCode.Unauthenticated, session.LastError);
            }
            finally
            {
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }    }
}
