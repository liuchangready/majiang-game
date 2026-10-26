using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Xunit;

namespace Mahjong.LocalHost.Tests
{
    public sealed class LocalHostConcurrencyTests
    {
        [Fact]
        public async Task PartialCancellationOfConcurrentInitialOpenLetsTheOtherWaiterSucceed()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            var channel = (LocalTableChannel)await LocalTableHost.CreateChannelAsync(harness.Options, ct);
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            channel.OpenHookForTest = async token =>
            {
                await gate.Task.WaitAsync(token);
            };

            using var aCts = new CancellationTokenSource();
            Task a = OpenWithTicketAsync(channel, harness, aCts.Token);
            Task b = OpenWithTicketAsync(channel, harness, ct);
            await Task.Delay(100, ct);
            aCts.Cancel();
            gate.TrySetResult(true);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await a);
            await b;
            Assert.True(channel.IsOpen);

            await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), ct);
        }

        [Fact]
        public async Task AllCancelledInitialOpenClosesChannelAndLeavesNoResources()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            var channel = (LocalTableChannel)await LocalTableHost.CreateChannelAsync(harness.Options, ct);
            channel.OpenHookForTest = async token =>
            {
                await Task.Delay(Timeout.Infinite, token);
            };

            using var aCts = new CancellationTokenSource();
            using var bCts = new CancellationTokenSource();
            Task a = OpenWithTicketAsync(channel, harness, aCts.Token);
            Task b = OpenWithTicketAsync(channel, harness, bCts.Token);
            await Task.Delay(100, ct);
            aCts.Cancel();
            bCts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await a);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await b);
            await Task.Delay(100, ct);

            Assert.False(channel.IsOpen);
            Assert.False(File.Exists(harness.SavePath));
            await Assert.ThrowsAsync<TableChannelClosedException>(
                async () => await OpenWithTicketAsync(channel, harness, ct));
        }

        [Fact]
        public async Task AllCancelledTransientReopenKeepsActorAndFileLock()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            var channel = (LocalTableChannel)await LocalTableHost.CreateChannelAsync(harness.Options, ct);
            await OpenWithTicketAsync(channel, harness, ct);
            await channel.CloseAsync(new TableChannelCloseReason("TRANSIENT", false), ct);

            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            channel.ReopenHookForTest = async token =>
            {
                await gate.Task.WaitAsync(token);
            };

            using var cts = new CancellationTokenSource();
            Task reopen = OpenWithTicketAsync(channel, harness, cts.Token);
            await Task.Delay(100, ct);
            cts.Cancel();
            gate.TrySetCanceled(cts.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reopen);

            // The file lock is still held: a second physical host must fail.
            var second = (LocalTableChannel)await LocalTableHost.CreateChannelAsync(harness.Options, ct);
            await Assert.ThrowsAsync<LocalTableException>(
                async () => await OpenWithTicketAsync(second, harness, ct));

            channel.ReopenHookForTest = null;
            await OpenWithTicketAsync(channel, harness, ct);
            Assert.True(channel.IsOpen);
            await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), ct);
        }

        [Fact]
        public async Task CancelledTransientCloseStillReachesTransientClosedAndAllowsReopen()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            var channel = (LocalTableChannel)await LocalTableHost.CreateChannelAsync(harness.Options, ct);
            await OpenWithTicketAsync(channel, harness, ct);

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await channel.CloseAsync(new TableChannelCloseReason("TRANSIENT", false), cts.Token));
            await Task.Delay(100, ct);

            Assert.False(channel.IsOpen);
            await OpenWithTicketAsync(channel, harness, ct);
            Assert.True(channel.IsOpen);
            await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), ct);
        }

        [Fact]
        public async Task PermanentCloseWinsOverConcurrentTransientAndBothWaitForCleanup()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            var channel = (LocalTableChannel)await LocalTableHost.CreateChannelAsync(harness.Options, ct);
            await OpenWithTicketAsync(channel, harness, ct);

            Task transient = channel.CloseAsync(new TableChannelCloseReason("TRANSIENT", false), ct);
            Task permanent = channel.CloseAsync(new TableChannelCloseReason("PERMANENT", true), ct);
            await Task.WhenAll(transient, permanent);
            Assert.False(channel.IsOpen);

            // The lock was released by the permanent cleanup.
            var second = (LocalTableChannel)await LocalTableHost.CreateChannelAsync(harness.Options, ct);
            await OpenWithTicketAsync(second, harness, ct);
            await second.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), ct);
        }

        [Fact]
        public async Task PermanentCloseWithCancelledWaiterLeavesLaterCloseWaitingForRealCleanup()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            var channel = (LocalTableChannel)await LocalTableHost.CreateChannelAsync(harness.Options, ct);
            await OpenWithTicketAsync(channel, harness, ct);

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await channel.CloseAsync(new TableChannelCloseReason("PERMANENT", true), cts.Token));
            await channel.CloseAsync(new TableChannelCloseReason("PERMANENT", true), ct);

            var second = (LocalTableChannel)await LocalTableHost.CreateChannelAsync(harness.Options, ct);
            await OpenWithTicketAsync(second, harness, ct);
            await second.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), ct);
        }

        private static Task OpenWithTicketAsync(
            LocalTableChannel channel,
            P41Harness harness,
            CancellationToken cancellationToken)
        {
            string ticket = LocalTableHost.IssueTicket(harness.Options);
            return channel.OpenAsync(new TableChannelConnectRequest(ticket, "p41-test", null), cancellationToken);
        }
    }
}
