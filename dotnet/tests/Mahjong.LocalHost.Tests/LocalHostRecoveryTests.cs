using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.TableRuntime;
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
            harness.Options.AutoPlay = false;
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

            using (LocalTableStore check = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, null, ct))
            {
                TableStateRow row = (await check.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.True(row.StateVersion.Value == committedVersion, "saved=" + row.StateVersion.Value + " committed=" + committedVersion);
            }

            (ITableChannel reopened, TableSession restored) = await OpenSessionAsync(harness, ct);
            try
            {
                Assert.True(restored.PlayerSnapshot!.StateVersion == committedVersion, "restored=" + restored.PlayerSnapshot.StateVersion + " committed=" + committedVersion);
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

        [Fact]
        public async Task LostReceiptIsReconciledAfterColdReopenWithoutDuplicateSideEffect()
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            CancellationToken ct = TestContext.Current.CancellationToken;
            var outbox = new InMemoryOutboxStore();
            ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, ct);
            string channelTicket = LocalTableHost.IssueTicket(harness.Options);
            await channel.OpenAsync(new TableChannelConnectRequest(channelTicket, "p41-test", null), ct);
            var session = new TableSession(channel, outbox);
            string sessionTicket = LocalTableHost.IssueTicket(harness.Options);
            await session.OpenAsync(new TableSessionOpenRequest(sessionTicket, "p41-test"), ct);
            for (int i = 0; i < 20 && session.State != TableConnectionState.Ready; i++)
            {
                await session.PumpOnceAsync(ct);
            }

            Assert.Equal(TableConnectionState.Ready, session.State);
            PlayerSnapshot baseline = session.PlayerSnapshot!;
            BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                baseline.RoundId,
                "local-direct",
                0UL,
                ProtocolActionType.DiscardTile,
                tileIds: new[] { baseline.PrivateView.ClosedTiles[0] });
            var command = new ProtocolCommand(
                CommandKind.SubmitAction,
                payload,
                new RequestId("lost-receipt-1"),
                session.Binding!.Scope.ScopeId,
                new StateVersion(baseline.StateVersion),
                new RoundId(baseline.RoundId),
                decisionId: null,
                controlEpoch: new ControlEpoch(0));
            CommandSubmission submission = await session.SubmitAsync(command, ct);
            Assert.True(submission.Sent);

            // Close before consuming the receipt/push: the outbox still has an unresolved side effect.
            await channel.CloseAsync(new TableChannelCloseReason("LOST_RECEIPT", true), CancellationToken.None);
            await session.CloseAsync("LOST_RECEIPT", CancellationToken.None);

            using (LocalTableStore check = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, null, ct))
            {
                TableStateRow saved = (await check.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(baseline.StateVersion + 1UL, saved.StateVersion.Value);
            }

            ITableChannel reopened = await LocalTableHost.CreateChannelAsync(harness.Options, ct);
            string reopenTicket = LocalTableHost.IssueTicket(harness.Options);
            await reopened.OpenAsync(new TableChannelConnectRequest(reopenTicket, "p41-test", null), ct);
            var recovered = new TableSession(reopened, outbox);
            string recoverTicket = LocalTableHost.IssueTicket(harness.Options);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await recovered.OpenAsync(new TableSessionOpenRequest(recoverTicket, "p41-test"), cts.Token);
                for (int i = 0; i < 50 && recovered.PendingCommands.Count > 0; i++)
                {
                    if (recovered.State == TableConnectionState.Ready)
                    {
                        await recovered.ReconcilePendingAsync(cts.Token);
                    }

                    await recovered.PumpOnceAsync(cts.Token);
                }

                Assert.Empty(recovered.PendingCommands);
                Assert.Equal(baseline.StateVersion + 1, recovered.PlayerSnapshot!.StateVersion);
            }
            finally
            {
                await recovered.CloseAsync("TEST_DONE", CancellationToken.None);
                await reopened.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }
        [Fact]
        public async Task CrashWorkerRealProcessKillLeavesDurableReceipt()
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            CancellationToken ct = TestContext.Current.CancellationToken;
            string root = P41Harness.FindRepositoryRoot();
            string workerDll = Path.Combine(
                root,
                "dotnet",
                "tests",
                "Mahjong.LocalHost.CrashWorker",
                "bin",
                "Release",
                "net8.0",
                "Mahjong.LocalHost.CrashWorker.dll");
            Assert.True(File.Exists(workerDll), "CrashWorker was not built: " + workerDll);
            string marker = Path.Combine(harness.Root, "crash-marker.txt");
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "D:/Unity/dotnet/dotnet.exe",
                Arguments = "\"" + workerDll + "\" \"" + harness.Options.SaveDirectory + "\" \"" +
                            P41Harness.RoomId + "\" \"" + marker + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!)
            {
                string stdout = await process.StandardOutput.ReadToEndAsync(ct);
                string stderr = await process.StandardError.ReadToEndAsync(ct);
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waitCts.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    await process.WaitForExitAsync(waitCts.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(true);
                    throw new TimeoutException("CrashWorker did not exit; stdout=" + stdout + " stderr=" + stderr);
                }

                Assert.NotEqual(0, process.ExitCode);
            }

            string[] markerLines = File.ReadAllLines(marker);
            Assert.Equal(3, markerLines.Length);
            var scopeId = new RequestScopeId(markerLines[0]);
            var requestId = new RequestId(markerLines[1]);
            ulong committed = ulong.Parse(markerLines[2], System.Globalization.CultureInfo.InvariantCulture);

            using (LocalTableStore store = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, null, ct))
            {
                TableDurableReceipt? receipt = await store.TryGetAsync(
                    "local-" + P41Harness.RoomId + "-seat0",
                    scopeId,
                    requestId,
                    ct);
                Assert.NotNull(receipt);
                TableStateRow row = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.True(row.StateVersion.Value >= committed);
            }
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
