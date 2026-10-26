using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Xunit;

namespace Mahjong.LocalHost.Tests
{
    using PlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;

    public sealed class LocalHostRecoveryTests
    {
        [Fact]
        public async Task ColdRestartRestoresCommittedStateVersion()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            ulong committedVersion;
            string roundId;

            (ITableChannel channel, TableSession session) = await OpenSessionAsync(harness, ct);
            try
            {
                PlayerSnapshot baseline = session.PlayerSnapshot!;
                roundId = baseline.RoundId;
                uint tile = baseline.PrivateView.ClosedTiles[0];
                await SubmitDiscardAsync(session, baseline, tile, "restart-1", ct);
                committedVersion = session.PlayerSnapshot!.StateVersion;
                Assert.True(committedVersion > 0);
            }
            finally
            {
                await CloseAsync(session, channel);
            }

            (ITableChannel reopened, TableSession restored) = await OpenSessionAsync(harness, ct);
            try
            {
                Assert.Equal(committedVersion, restored.PlayerSnapshot!.StateVersion);
                Assert.Equal(roundId, restored.PlayerSnapshot.RoundId);
            }
            finally
            {
                await CloseAsync(restored, reopened);
            }
        }

        [Fact]
        public async Task CorruptPrimaryFallsBackToPreviousSave()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            (ITableChannel channel, TableSession session) = await OpenSessionAsync(harness, ct);
            try
            {
                PlayerSnapshot baseline = session.PlayerSnapshot!;
                await SubmitDiscardAsync(session, baseline, baseline.PrivateView.ClosedTiles[0], "corrupt-1", ct);
            }
            finally
            {
                await CloseAsync(session, channel);
            }

            Assert.True(File.Exists(harness.BackupPath));
            byte[] primary = File.ReadAllBytes(harness.SavePath);
            File.WriteAllBytes(harness.SavePath, new byte[] { 1, 2, 3, 4, 5 });

            (ITableChannel reopened, TableSession restored) = await OpenSessionAsync(harness, ct);
            try
            {
                Assert.True(reopened.IsOpen);
                Assert.NotNull(restored.PlayerSnapshot);
                byte[] recovered = File.ReadAllBytes(harness.SavePath);
                Assert.True(recovered.Length > 5);
                Assert.True(primary.Length > 5);
            }
            finally
            {
                await CloseAsync(restored, reopened);
            }
        }

        [Fact]
        public async Task CorruptPrimaryAndBackupAreRefusedWithoutSilentNewGame()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            (ITableChannel channel, TableSession session) = await OpenSessionAsync(harness, ct);
            try
            {
                await SubmitDiscardAsync(
                    session,
                    session.PlayerSnapshot!,
                    session.PlayerSnapshot!.PrivateView.ClosedTiles[0],
                    "corrupt-both-1",
                    ct);
            }
            finally
            {
                await CloseAsync(session, channel);
            }

            File.WriteAllBytes(harness.SavePath, new byte[] { 9, 8, 7 });
            File.WriteAllBytes(harness.BackupPath, new byte[] { 6, 5, 4 });

            ITableChannel candidate = await LocalTableHost.CreateChannelAsync(harness.Options, ct);
            string ticket = LocalTableHost.IssueTicket(harness.Options);
            LocalTableException error = await Assert.ThrowsAsync<LocalTableException>(
                async () => await candidate.OpenAsync(
                    new TableChannelConnectRequest(ticket, "p41-test", null),
                    ct));
            Assert.Equal(LocalTableErrorKind.CorruptState, error.ErrorKind);
            Assert.True(File.Exists(harness.SavePath));
            Assert.Equal(3, new FileInfo(harness.SavePath).Length);
        }

        private static async Task<(ITableChannel Channel, TableSession Session)> OpenSessionAsync(
            P41Harness harness,
            CancellationToken cancellationToken)
        {
            ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, cancellationToken);
            string channelTicket = LocalTableHost.IssueTicket(harness.Options);
            await channel.OpenAsync(new TableChannelConnectRequest(channelTicket, "p41-test", null), cancellationToken);
            var session = new TableSession(channel, new InMemoryOutboxStore());
            string sessionTicket = LocalTableHost.IssueTicket(harness.Options);
            await session.OpenAsync(new TableSessionOpenRequest(sessionTicket, "p41-test"), cancellationToken);
            for (int i = 0; i < 20 && session.State != TableConnectionState.Ready; i++)
            {
                await session.PumpOnceAsync(cancellationToken);
            }

            Assert.Equal(TableConnectionState.Ready, session.State);
            return (channel, session);
        }

        private static async Task SubmitDiscardAsync(
            TableSession session,
            PlayerSnapshot baseline,
            uint tile,
            string requestId,
            CancellationToken cancellationToken)
        {
            var resolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var advanced = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<TableReceiptResolvedEventArgs> receiptHandler = (_, args) => resolved.TrySetResult(args);
            EventHandler<TableSnapshotChangedEventArgs> snapshotHandler = (_, _) =>
            {
                if (session.PlayerSnapshot != null && session.PlayerSnapshot.StateVersion > baseline.StateVersion)
                {
                    advanced.TrySetResult(true);
                }
            };
            session.ReceiptResolved += receiptHandler;
            session.SnapshotChanged += snapshotHandler;
            try
            {
                BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                    baseline.RoundId,
                    "local-direct",
                    0UL,
                    ProtocolActionType.DiscardTile,
                    tileIds: new[] { tile });
                var command = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    payload,
                    new RequestId(requestId),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(baseline.StateVersion),
                    new RoundId(baseline.RoundId),
                    decisionId: null,
                    controlEpoch: new ControlEpoch(0));
                CommandSubmission submission = await session.SubmitAsync(command, cancellationToken);
                Assert.True(submission.Sent);

                using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                pumpCts.CancelAfter(TimeSpan.FromSeconds(10));
                Task pump = Task.Run(async () =>
                {
                    try
                    {
                        while (!pumpCts.IsCancellationRequested &&
                               (!resolved.Task.IsCompleted || !advanced.Task.IsCompleted))
                        {
                            await session.PumpOnceAsync(pumpCts.Token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }, TestContext.Current.CancellationToken);

                TableReceiptResolvedEventArgs receipt = await LocalHostCommandTests.WithTimeoutAsync(
                    resolved.Task,
                    pumpCts.Token);
                Assert.True(
                    receipt.Resolution.ReceiptStatus == Mahjong.Contracts.ReceiptStatus.Applied,
                    "reason=" + receipt.Resolution.ReasonCode);
                await LocalHostCommandTests.WithTimeoutAsync(advanced.Task, pumpCts.Token);
                pumpCts.Cancel();
                await pump;
            }
            finally
            {
                session.ReceiptResolved -= receiptHandler;
                session.SnapshotChanged -= snapshotHandler;
            }
        }

        private static async Task CloseAsync(TableSession session, ITableChannel channel)
        {
            await session.CloseAsync("TEST_DONE", CancellationToken.None);
            await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
        }
    }
}
