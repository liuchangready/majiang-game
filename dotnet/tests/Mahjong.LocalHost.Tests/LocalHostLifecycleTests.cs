using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Xunit;

namespace Mahjong.LocalHost.Tests
{
    public sealed class LocalHostLifecycleTests
    {
        [Fact]
        public async Task CreateChannelReturnsNewWithoutTouchingDisk()
        {
            await using var harness = new P41Harness();
            ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, TestContext.Current.CancellationToken);

            Assert.False(channel.IsOpen);
            Assert.False(Directory.Exists(harness.Options.SaveDirectory));
            Assert.False(File.Exists(harness.SavePath));

            await channel.CloseAsync(new TableChannelCloseReason("TEST", true), TestContext.Current.CancellationToken);
            Assert.False(channel.IsOpen);
            Assert.False(Directory.Exists(harness.Options.SaveDirectory));
        }

        [Fact]
        public async Task CreateChannelWithPreCancelledTokenReturnsCanceledTask()
        {
            await using var harness = new P41Harness();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await LocalTableHost.CreateChannelAsync(harness.Options, cts.Token));
        }

        [Fact]
        public async Task FirstOpenCreatesSaveAndPermanentCloseReleasesLock()
        {
            await using var harness = new P41Harness();
            ITableChannel channel = await harness.OpenChannelAsync(TestContext.Current.CancellationToken);

            Assert.True(channel.IsOpen);
            Assert.True(File.Exists(harness.SavePath));
            Assert.True(File.Exists(harness.BackupPath));

            await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), TestContext.Current.CancellationToken);
            Assert.False(channel.IsOpen);

            // A second physical open after the lock release must succeed.
            ITableChannel second = await harness.OpenChannelAsync(TestContext.Current.CancellationToken);
            Assert.True(second.IsOpen);
            await second.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task SecondChannelOpenFailsWithLeaseConflict()
        {
            await using var harness = new P41Harness();
            ITableChannel first = await harness.OpenChannelAsync(TestContext.Current.CancellationToken);
            try
            {
                ITableChannel second = await LocalTableHost.CreateChannelAsync(harness.Options, TestContext.Current.CancellationToken);
                string ticket = LocalTableHost.IssueTicket(harness.Options);
                LocalTableException error = await Assert.ThrowsAsync<LocalTableException>(
                    async () => await second.OpenAsync(
                        new TableChannelConnectRequest(ticket, "p41-test", null),
                        TestContext.Current.CancellationToken));
                Assert.Equal(LocalTableErrorKind.LeaseConflict, error.ErrorKind);
                Assert.False(second.IsOpen);
            }
            finally
            {
                await first.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), TestContext.Current.CancellationToken);
            }
        }

        [Fact]
        public async Task TransientCloseInNewStateIsNoOpAndStillAllowsFirstOpen()
        {
            await using var harness = new P41Harness();
            ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, TestContext.Current.CancellationToken);
            await channel.CloseAsync(new TableChannelCloseReason("TRANSIENT", false), TestContext.Current.CancellationToken);
            Assert.False(channel.IsOpen);
            Assert.False(Directory.Exists(harness.Options.SaveDirectory));

            string ticket = LocalTableHost.IssueTicket(harness.Options);
            await channel.OpenAsync(
                new TableChannelConnectRequest(ticket, "p41-test", null),
                TestContext.Current.CancellationToken);
            Assert.True(channel.IsOpen);
            await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task PermanentCloseWithCancelledWaiterStillCompletesCleanup()
        {
            await using var harness = new P41Harness();
            ITableChannel channel = await harness.OpenChannelAsync(TestContext.Current.CancellationToken);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await channel.CloseAsync(new TableChannelCloseReason("PERMANENT", true), cts.Token));

            await channel.CloseAsync(new TableChannelCloseReason("PERMANENT", true), TestContext.Current.CancellationToken);
            Assert.False(channel.IsOpen);

            ITableChannel reopened = await harness.OpenChannelAsync(TestContext.Current.CancellationToken);
            Assert.True(reopened.IsOpen);
            await reopened.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task InvalidOptionsAreRejectedBeforeResourceAcquisition()
        {
            await using var harness = new P41Harness();
            var invalid = P41Harness.CloneOptions(harness.Options);
            invalid.RoomId = "../escape";

            LocalTableException error = await Assert.ThrowsAsync<LocalTableException>(
                async () => await LocalTableHost.CreateChannelAsync(invalid, TestContext.Current.CancellationToken));
            Assert.Equal(LocalTableErrorKind.InvalidOptions, error.ErrorKind);
            Assert.False(Directory.Exists(invalid.SaveDirectory));
        }
    }
}
