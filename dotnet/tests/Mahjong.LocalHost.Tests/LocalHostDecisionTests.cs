using System;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Xunit;

namespace Mahjong.LocalHost.Tests
{
    public sealed class LocalHostDecisionTests
    {
        [Fact]
        public async Task NoHumanInputStillTimesOutUnderTheAuthorityClock()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, ct);
            string channelTicket = LocalTableHost.IssueTicket(harness.Options);
            await channel.OpenAsync(new TableChannelConnectRequest(channelTicket, "p41-test", null), ct);
            var session = new TableSession(channel, new InMemoryOutboxStore());
            string sessionTicket = LocalTableHost.IssueTicket(harness.Options);
            using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pumpCts.CancelAfter(TimeSpan.FromSeconds(30));
            Task pump = Task.Run(async () =>
            {
                try
                {
                    while (!pumpCts.IsCancellationRequested)
                    {
                        await session.PumpOnceAsync(pumpCts.Token);
                    }
                }
                catch (Exception exception) when (
                    exception is OperationCanceledException ||
                    exception is TableChannelClosedException ||
                    exception is InvalidOperationException)
                {
                }
            }, TestContext.Current.CancellationToken);

            try
            {
                await session.OpenAsync(new TableSessionOpenRequest(sessionTicket, "p41-test"), pumpCts.Token);
                for (int i = 0; i < 500 && session.State != TableConnectionState.Ready; i++)
                {
                    await Task.Delay(20, pumpCts.Token);
                }

                Assert.Equal(TableConnectionState.Ready, session.State);
                Assert.NotNull(session.PlayerSnapshot);
                ulong baseline = session.PlayerSnapshot!.StateVersion;
                Assert.NotNull(session.PlayerSnapshot.PrivateView.Decision);
                Assert.False(string.IsNullOrEmpty(session.PlayerSnapshot.PrivateView.Decision.DecisionId));

                harness.Clock.MonotonicMillis += 100_000L;
                harness.Clock.UtcNowMs += 100_000L;

                for (int i = 0; i < 100 && session.PlayerSnapshot!.StateVersion == baseline; i++)
                {
                    await Task.Delay(20, pumpCts.Token);
                }

                Assert.True(
                    session.PlayerSnapshot!.StateVersion > baseline,
                    "the authoritative deadline did not advance the table; last=" + session.LastError);
            }
            finally
            {
                pumpCts.Cancel();
                await pump;
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }
    }
}
