using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.TableRuntime;
using Xunit;
using PlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;

namespace Mahjong.LocalHost.Tests
{
    /// <summary>
    /// Regression matrix for the local save recovery decision. The distinction under test is
    /// semantic, not "a file exists": a missing primary with a valid previous copy must restore
    /// the confirmed envelope and republish it, while no usable copy must be refused instead of
    /// silently starting a new game.
    /// </summary>
    public sealed class LocalSaveBackupRecoveryTests
    {
        [Fact]
        public async Task MissingPrimaryWithValidBackupRestoresConfirmedEnvelopeAndPublishesPrimary()
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            CancellationToken ct = TestContext.Current.CancellationToken;
            ConfirmedAction action = await CreateConfirmedActionAsync(harness, ct);
            ExpectedRuntime expectedRuntime = await CaptureExpectedRuntimeAsync(harness, ct);

            // The evidence scenario: the previous copy is byte-identical to the last confirmed
            // save, and the primary is absent (not truncated).
            byte[] expectedBytes = File.ReadAllBytes(harness.SavePath);
            Assert.True(expectedBytes.Length > 48);
            File.Copy(harness.SavePath, harness.BackupPath, true);
            File.Delete(harness.SavePath);
            byte[] backupBytes = File.ReadAllBytes(harness.BackupPath);
            Assert.Equal(Sha256(expectedBytes), Sha256(backupBytes));
            Assert.False(File.Exists(harness.SavePath));
            Assert.True(File.Exists(harness.BackupPath));

            LocalSave expected = LocalSaveCodec.DecodeEnvelope(expectedBytes);
            Assert.True(expected.CurrentState.StateVersion.Value >= action.CommandVersion);
            Assert.True(expected.Receipts.Count > 0);
            Assert.True(expected.Receipts.ContainsKey(ReceiptKey(action.AccountId, action.ScopeId, action.RequestId)));
            TableDurableReceipt expectedHumanReceipt = expected.Receipts[ReceiptKey(action.AccountId, action.ScopeId, action.RequestId)];
            Assert.Equal(ReceiptStatus.Applied, expectedHumanReceipt.Receipt.Status);
            action.PayloadSha256 = expectedHumanReceipt.PayloadSha256.ToHex();
            Assert.Equal(64, action.PayloadSha256.Length);

            using (LocalTableStore store = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, null, ct))
            {
                Assert.True(File.Exists(harness.SavePath), "the verified previous copy must be published as primary");
                LocalSave recovered = LocalSaveCodec.DecodeEnvelope(File.ReadAllBytes(harness.SavePath));
                AssertSaveIdentity(expected, recovered);
                AssertReceiptCollectionIdentity(expected, recovered);

                TableStateRow row = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(expected.CurrentState.StateVersion.Value, row.StateVersion.Value);
                Assert.Equal(expected.CurrentState.StateHash, row.StateHash);

                TableDurableReceipt? durable = await store.TryGetAsync(
                    action.AccountId,
                    new RequestScopeId(action.ScopeId),
                    new RequestId(action.RequestId),
                    ct);
                Assert.NotNull(durable);
                AssertReceiptIdentity(expected.Receipts[ReceiptKey(action.AccountId, action.ScopeId, action.RequestId)], durable!);
            }

            Assert.Equal(Sha256(backupBytes), Sha256(File.ReadAllBytes(harness.BackupPath)));
            await AssertRuntimePhysicalIdentityAsync(harness, expectedRuntime, ct);

            // Remove the republished primary again and prove a genuinely separate OS process
            // performs the same decision through the production recovery path.
            File.Delete(harness.SavePath);
            byte[] backupBeforeCold = File.ReadAllBytes(harness.BackupPath);
            ColdRead cold = await RunColdRecoveryReadAsync(harness, ct);
            Assert.Equal("False", cold.PrimaryPresentBefore);
            Assert.Equal("True", cold.PrimaryPresentAfter);
            Assert.Equal(Sha256(backupBeforeCold), cold.BackupSha256Before.ToUpperInvariant());
            AssertSaveIdentity(expected, LocalSaveCodec.DecodeEnvelope(backupBeforeCold), allowRecoveryAuditAppend: true);
            Assert.Equal(expectedRuntime.StateVersion.ToString(CultureInfo.InvariantCulture), cold.StateVersion);
            Assert.Equal(expectedRuntime.RoundId, cold.RoundId);
            Assert.Equal(expectedRuntime.ConservationHash.ToString(CultureInfo.InvariantCulture), cold.TilesHash);
            Assert.Equal("136", cold.TilesAssigned);
            Assert.Equal(expected.Receipts.Count.ToString(CultureInfo.InvariantCulture), cold.ReceiptCount);
            Assert.Equal(ReceiptSetDigest(expected.Receipts), cold.ReceiptSetSha256);
            Assert.True(File.Exists(harness.SavePath), "the independent cold process must publish the verified previous copy");
            // A successful cold recovery appends one RecoveryAdjusted audit operation. State,
            // receipts and journals must remain identical; only that append-only audit may grow.
            AssertSaveIdentity(expected, LocalSaveCodec.DecodeEnvelope(File.ReadAllBytes(harness.SavePath)), allowRecoveryAuditAppend: true);
        }

        [Fact]
        public async Task MissingPrimaryWithCorruptBackupIsRefusedWithoutCreatingANewGame()
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            CancellationToken ct = TestContext.Current.CancellationToken;
            await CreateConfirmedActionAsync(harness, ct);

            File.Delete(harness.SavePath);
            byte[] corrupt = File.ReadAllBytes(harness.BackupPath);
            corrupt[corrupt.Length - 1] ^= 0x5A;
            File.WriteAllBytes(harness.BackupPath, corrupt);
            byte[] corruptCopy = File.ReadAllBytes(harness.BackupPath);

            LocalTableException error = await Assert.ThrowsAsync<LocalTableException>(
                async () => await LocalTableStore.OpenAsync(harness.Options, harness.Clock, null, ct));
            Assert.Equal(LocalTableErrorKind.CorruptState, error.ErrorKind);
            Assert.False(File.Exists(harness.SavePath), "a rejected recovery must not create a fresh authoritative save");
            Assert.Equal(Sha256(corruptCopy), Sha256(File.ReadAllBytes(harness.BackupPath)));
        }

        [Fact]
        public async Task BothCopiesAbsentAllowsTheLegitimateInitialNewGame()
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            CancellationToken ct = TestContext.Current.CancellationToken;
            Assert.False(File.Exists(harness.SavePath));
            Assert.False(File.Exists(harness.BackupPath));

            using (LocalTableStore empty = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, null, ct))
            {
                Assert.False(empty.Exists);
                Assert.Null(await empty.LoadStateAsync(P41Harness.RoomId, ct));
            }

            await using (LocalTableRuntime runtime = await LocalTableRuntime.OpenAsync(harness.Options, ct))
            {
                TableStateRow row = (await runtime.StoreForTest.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(0UL, row.StateVersion.Value);
                RoundStateMachine round = runtime.ActorForTest.GetRoundStateForHost();
                ConservationReport report = TilePositionMap.Validate(round.Tiles);
                Assert.True(report.IsValid, string.Join("; ", report.Errors));
                Assert.Equal(136, report.AssignedCount);
                Assert.Equal(0, report.UnassignedCount);
            }

            Assert.True(File.Exists(harness.SavePath));
            Assert.True(File.Exists(harness.BackupPath));
        }

        [Fact]
        public async Task ValidPrimaryWithCorruptBackupUsesThePrimaryWithoutRollingBack()
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            CancellationToken ct = TestContext.Current.CancellationToken;
            await CreateConfirmedActionAsync(harness, ct);
            ExpectedRuntime expectedRuntime = await CaptureExpectedRuntimeAsync(harness, ct);

            byte[] expectedBytes = File.ReadAllBytes(harness.SavePath);
            File.WriteAllBytes(harness.BackupPath, new byte[] { 0x4C, 0x48, 0x34, 0x50, 0x00, 0x00, 0x00, 0x01 });
            LocalSave expected = LocalSaveCodec.DecodeEnvelope(expectedBytes);

            using (LocalTableStore store = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, null, ct))
            {
                Assert.Equal(Sha256(expectedBytes), Sha256(File.ReadAllBytes(harness.SavePath)));
                LocalSave recovered = LocalSaveCodec.DecodeEnvelope(File.ReadAllBytes(harness.SavePath));
                AssertSaveIdentity(expected, recovered);
                AssertReceiptCollectionIdentity(expected, recovered);
            }

            await AssertRuntimePhysicalIdentityAsync(harness, expectedRuntime, ct);
        }

        [Fact]
        public async Task CorruptPrimaryWithValidBackupRestoresTheBackupAndRemovesQuarantine()
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            CancellationToken ct = TestContext.Current.CancellationToken;
            await CreateConfirmedActionAsync(harness, ct);
            ExpectedRuntime expectedRuntime = await CaptureExpectedRuntimeAsync(harness, ct);

            byte[] expectedBytes = File.ReadAllBytes(harness.SavePath);
            File.Copy(harness.SavePath, harness.BackupPath, true);
            byte[] corrupt = File.ReadAllBytes(harness.SavePath);
            corrupt[corrupt.Length - 1] ^= 0x33;
            File.WriteAllBytes(harness.SavePath, corrupt);
            LocalSave expected = LocalSaveCodec.DecodeEnvelope(expectedBytes);

            using (LocalTableStore store = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, null, ct))
            {
                LocalSave recovered = LocalSaveCodec.DecodeEnvelope(File.ReadAllBytes(harness.SavePath));
                AssertSaveIdentity(expected, recovered);
                AssertReceiptCollectionIdentity(expected, recovered);
                Assert.Empty(Directory.GetFiles(harness.Options.SaveDirectory, "*.corrupt.*"));
            }

            await AssertRuntimePhysicalIdentityAsync(harness, expectedRuntime, ct);
        }

        [Fact]
        public async Task BackupPublishFailureKeepsTheRoomAbsentAndTheVerifiedBackupUsable()
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            CancellationToken ct = TestContext.Current.CancellationToken;
            await CreateConfirmedActionAsync(harness, ct);
            ExpectedRuntime expectedRuntime = await CaptureExpectedRuntimeAsync(harness, ct);
            byte[] expectedBytes = File.ReadAllBytes(harness.SavePath);
            File.Copy(harness.SavePath, harness.BackupPath, true);
            File.Delete(harness.SavePath);
            LocalSave expected = LocalSaveCodec.DecodeEnvelope(expectedBytes);
            byte[] backupBytes = File.ReadAllBytes(harness.BackupPath);

            var faulting = new FaultingFileSystem
            {
                BeforeMove = destination => string.Equals(destination, harness.SavePath, StringComparison.Ordinal)
                    ? new IOException("injected previous-copy publish failure")
                    : null
            };
            LocalTableException error = await Assert.ThrowsAsync<LocalTableException>(
                async () => await LocalTableStore.OpenAsync(harness.Options, harness.Clock, faulting, ct));
            Assert.Equal(LocalTableErrorKind.Unavailable, error.ErrorKind);
            Assert.False(File.Exists(harness.SavePath), "failed publication must not fabricate a primary");
            Assert.Equal(Sha256(backupBytes), Sha256(File.ReadAllBytes(harness.BackupPath)));

            using (LocalTableStore retry = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, null, ct))
            {
                Assert.True(File.Exists(harness.SavePath));
                AssertSaveIdentity(expected, LocalSaveCodec.DecodeEnvelope(File.ReadAllBytes(harness.SavePath)));
            }

            await AssertRuntimePhysicalIdentityAsync(harness, expectedRuntime, ct);
        }

        [Fact]
        public async Task BackupPublishFailureRestoresTheQuarantinedPrimary()
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            CancellationToken ct = TestContext.Current.CancellationToken;
            await CreateConfirmedActionAsync(harness, ct);
            ExpectedRuntime expectedRuntime = await CaptureExpectedRuntimeAsync(harness, ct);
            byte[] expectedBytes = File.ReadAllBytes(harness.SavePath);
            File.Copy(harness.SavePath, harness.BackupPath, true);
            byte[] corruptPrimary = File.ReadAllBytes(harness.SavePath);
            corruptPrimary[corruptPrimary.Length - 1] ^= 0x11;
            File.WriteAllBytes(harness.SavePath, corruptPrimary);
            LocalSave expected = LocalSaveCodec.DecodeEnvelope(expectedBytes);

            int publishAttempts = 0;
            var faulting = new FaultingFileSystem
            {
                BeforeMove = destination =>
                {
                    if (!string.Equals(destination, harness.SavePath, StringComparison.Ordinal))
                    {
                        return null;
                    }

                    publishAttempts++;
                    return publishAttempts == 1
                        ? new IOException("injected publish failure after quarantine")
                        : null;
                }
            };
            LocalTableException error = await Assert.ThrowsAsync<LocalTableException>(
                async () => await LocalTableStore.OpenAsync(harness.Options, harness.Clock, faulting, ct));
            Assert.Equal(LocalTableErrorKind.Unavailable, error.ErrorKind);
            Assert.Equal(2, publishAttempts);
            Assert.True(File.Exists(harness.SavePath), "the quarantined primary must be restored on publication failure");
            Assert.Equal(Sha256(corruptPrimary), Sha256(File.ReadAllBytes(harness.SavePath)));
            Assert.Empty(Directory.GetFiles(harness.Options.SaveDirectory, "*.corrupt.*"));

            using (LocalTableStore retry = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, null, ct))
            {
                AssertSaveIdentity(expected, LocalSaveCodec.DecodeEnvelope(File.ReadAllBytes(harness.SavePath)));
            }

            await AssertRuntimePhysicalIdentityAsync(harness, expectedRuntime, ct);
        }

        private static async Task<ConfirmedAction> CreateConfirmedActionAsync(
            P41Harness harness,
            CancellationToken cancellationToken)
        {
            ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, cancellationToken);
            string channelTicket = LocalTableHost.IssueTicket(harness.Options);
            await channel.OpenAsync(new TableChannelConnectRequest(channelTicket, "backup-recovery-test", null), cancellationToken);
            var session = new TableSession(channel, new InMemoryOutboxStore());
            try
            {
                string sessionTicket = LocalTableHost.IssueTicket(harness.Options);
                await session.OpenAsync(new TableSessionOpenRequest(sessionTicket, "backup-recovery-test"), cancellationToken);
                for (int i = 0; i < 50 && session.State != TableConnectionState.Ready; i++)
                {
                    await session.PumpOnceAsync(cancellationToken);
                }

                Assert.Equal(TableConnectionState.Ready, session.State);
                PlayerSnapshot baseline = session.PlayerSnapshot!;
                var resolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<TableReceiptResolvedEventArgs> handler = (_, args) => resolved.TrySetResult(args);
                session.ReceiptResolved += handler;
                try
                {
                    BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                        baseline.RoundId,
                        "local-direct",
                        0UL,
                        ProtocolActionType.DiscardTile,
                        tileIds: new[] { baseline.PrivateView.ClosedTiles[0] });
                    var command = new ProtocolCommand(
                        CommandKind.SubmitAction,
                        payload,
                        new RequestId("backup-recovery-1"),
                        session.Binding!.Scope.ScopeId,
                        new StateVersion(baseline.StateVersion),
                        new RoundId(baseline.RoundId),
                        decisionId: null,
                        controlEpoch: new ControlEpoch(0));
                    CommandSubmission submission = await session.SubmitAsync(command, cancellationToken);
                    Assert.True(submission.Sent);

                    using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    pumpCts.CancelAfter(TimeSpan.FromSeconds(15));
                    Task pump = Task.Run(async () =>
                    {
                        try
                        {
                            while (!pumpCts.IsCancellationRequested && !resolved.Task.IsCompleted)
                            {
                                await session.PumpOnceAsync(pumpCts.Token);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    }, TestContext.Current.CancellationToken);
                    try
                    {
                        TableReceiptResolvedEventArgs receipt = await resolved.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
                        Assert.Equal(ReceiptStatus.Applied, receipt.Resolution.ReceiptStatus);
                        Assert.True(receipt.Resolution.CommandVersion.HasValue);
                        Assert.True(receipt.Resolution.EventSeq.HasValue);
                        return new ConfirmedAction
                        {
                            CommandVersion = receipt.Resolution.CommandVersion!.Value.Value,
                            AccountId = session.Binding!.Scope.AccountId,
                            ScopeId = session.Binding.Scope.ScopeId.Value,
                            RequestId = "backup-recovery-1",
                            PayloadSha256 = receipt.Resolution.CanonicalPayloadSha256
                        };
                    }
                    finally
                    {
                        pumpCts.Cancel();
                        await pump;
                    }
                }
                finally
                {
                    session.ReceiptResolved -= handler;
                }
            }
            finally
            {
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }

        /// <summary>
        /// Captures the authoritative runtime view before the file fault is injected. The
        /// envelope's base checkpoint is older than its replayed tail, so the physical identity
        /// must come from the real recovery path, not from decoding the base checkpoint alone.
        /// </summary>
        private static async Task<ExpectedRuntime> CaptureExpectedRuntimeAsync(
            P41Harness harness,
            CancellationToken cancellationToken)
        {
            await using LocalTableRuntime runtime = await LocalTableRuntime.OpenAsync(harness.Options, cancellationToken);
            TableStateRow row = (await runtime.StoreForTest.LoadStateAsync(P41Harness.RoomId, cancellationToken))!;
            RoundStateMachine round = runtime.ActorForTest.GetRoundStateForHost();
            ConservationReport report = TilePositionMap.Validate(round.Tiles);
            Assert.True(report.IsValid, string.Join("; ", report.Errors));
            Assert.Equal(136, report.AssignedCount);
            Assert.Equal(0, report.UnassignedCount);
            var tiles = new TilePosition[136];
            for (int i = 0; i < 136; i++)
            {
                tiles[i] = round.Tiles[i];
            }

            return new ExpectedRuntime
            {
                StateVersion = row.StateVersion.Value,
                StateHash = row.StateHash,
                RoundId = round.RoundId,
                ConservationHash = report.StateHash,
                Tiles = tiles
            };
        }

        private static async Task AssertRuntimePhysicalIdentityAsync(
            P41Harness harness,
            ExpectedRuntime expected,
            CancellationToken cancellationToken)
        {
            await using LocalTableRuntime runtime = await LocalTableRuntime.OpenAsync(harness.Options, cancellationToken);
            TableStateRow row = (await runtime.StoreForTest.LoadStateAsync(P41Harness.RoomId, cancellationToken))!;
            Assert.Equal(expected.StateVersion, row.StateVersion.Value);
            Assert.Equal(expected.StateHash, row.StateHash);

            RoundStateMachine recoveredRound = runtime.ActorForTest.GetRoundStateForHost();
            Assert.Equal(expected.RoundId, recoveredRound.RoundId);
            ConservationReport report = TilePositionMap.Validate(recoveredRound.Tiles);
            Assert.True(report.IsValid, string.Join("; ", report.Errors));
            Assert.Equal(136, report.AssignedCount);
            Assert.Equal(0, report.UnassignedCount);
            Assert.Equal(expected.ConservationHash, report.StateHash);
            for (int i = 0; i < 136; i++)
            {
                Assert.True(
                    expected.Tiles[i].Zone == recoveredRound.Tiles[i].Zone &&
                    expected.Tiles[i].Index == recoveredRound.Tiles[i].Index,
                    $"tile {i} differs: expected {expected.Tiles[i].Zone}:{expected.Tiles[i].Index}, " +
                    $"actual {recoveredRound.Tiles[i].Zone}:{recoveredRound.Tiles[i].Index}");
            }
        }

        private static async Task<ColdRead> RunColdRecoveryReadAsync(
            P41Harness harness,
            CancellationToken cancellationToken)
        {
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
            string marker = Path.Combine(harness.Root, "cold-backup-recovery-read.txt");
            ProcessStartInfo start = P41Harness.CreateWorkerStartInfo(
                workerDll,
                harness.Options.SaveDirectory,
                P41Harness.RoomId,
                marker,
                "backup-recovery-read");
            using Process process = Process.Start(start)!;
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waitCts.CancelAfter(TimeSpan.FromSeconds(30));
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(waitCts.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(waitCts.Token);
            try
            {
                await process.WaitForExitAsync(waitCts.Token);
                await Task.WhenAll(stdout, stderr);
            }
            catch (OperationCanceledException)
            {
                process.Kill(true);
                throw new TimeoutException("CrashWorker cold recovery read did not exit within 30 seconds.");
            }

            Assert.True(
                process.ExitCode == 0,
                "cold recovery read exit=" + process.ExitCode + " stderr=" + stderr.Result + " stdout=" + stdout.Result);
            Assert.True(File.Exists(marker), "cold recovery read did not write its marker.");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in File.ReadAllLines(marker))
            {
                int separator = line.IndexOf('=');
                if (separator > 0)
                {
                    values[line.Substring(0, separator)] = line.Substring(separator + 1);
                }
            }

            return new ColdRead
            {
                PrimaryPresentBefore = values["primary_present_before"],
                PrimaryPresentAfter = values["primary_present_after"],
                BackupSha256Before = values["backup_sha256_before"],
                StateVersion = values["state_version"],
                RoundId = values["round_id"],
                TilesHash = values["tiles_hash"],
                TilesAssigned = values["tiles_assigned"],
                ReceiptCount = values["receipt_count"],
                ReceiptSetSha256 = values["receipt_set_sha256"]
            };
        }

        private static void AssertSaveIdentity(LocalSave expected, LocalSave actual, bool allowRecoveryAuditAppend = false)
        {
            Assert.Equal(expected.RoomId, actual.RoomId);
            Assert.Equal(expected.FileFormatVersion, actual.FileFormatVersion);
            Assert.Equal(expected.RulesetVersion, actual.RulesetVersion);
            Assert.Equal(expected.Origin, actual.Origin);
            Assert.Equal(expected.CurrentState.StateVersion, actual.CurrentState.StateVersion);
            Assert.Equal(expected.CurrentState.LastEventSeq, actual.CurrentState.LastEventSeq);
            Assert.Equal(expected.CurrentState.StateFingerprint, actual.CurrentState.StateFingerprint);
            Assert.Equal(expected.CurrentState.StateHash, actual.CurrentState.StateHash);
            Assert.Equal(expected.CurrentState.CheatVersion, actual.CurrentState.CheatVersion);
            Assert.Equal(expected.CurrentState.CheatFingerprint, actual.CurrentState.CheatFingerprint);
            Assert.Equal(expected.CurrentState.CheatBound, actual.CurrentState.CheatBound);
            Assert.Equal(expected.CheckpointSha256, actual.CheckpointSha256);
            Assert.Equal(expected.CommandJournals.Count, actual.CommandJournals.Count);
            Assert.Equal(expected.EventJournals.Count, actual.EventJournals.Count);
            if (allowRecoveryAuditAppend)
            {
                // A successful runtime recovery appends an auditable RecoveryAdjusted record and
                // advances the timing fence. These are monotonic append-only facts, never a
                // rewrite or rollback of the recovered state/receipts asserted above.
                Assert.True(actual.CurrentState.ControlVersion >= expected.CurrentState.ControlVersion);
                Assert.True(actual.CurrentState.TimingVersion >= expected.CurrentState.TimingVersion);
                Assert.True(actual.Operations.Count >= expected.Operations.Count);
                Assert.True(actual.RecoveryAudits.Count >= expected.RecoveryAudits.Count);
            }
            else
            {
                Assert.Equal(expected.CurrentState.ControlVersion, actual.CurrentState.ControlVersion);
                Assert.Equal(expected.CurrentState.TimingVersion, actual.CurrentState.TimingVersion);
                Assert.Equal(expected.LatestTimingPayloadSha256, actual.LatestTimingPayloadSha256);
                Assert.Equal(expected.Operations.Count, actual.Operations.Count);
                Assert.Equal(expected.RecoveryAudits.Count, actual.RecoveryAudits.Count);
            }
        }

        private static void AssertReceiptCollectionIdentity(LocalSave expected, LocalSave actual)
        {
            Assert.Equal(expected.Receipts.Count, actual.Receipts.Count);
            foreach (KeyValuePair<string, TableDurableReceipt> pair in expected.Receipts)
            {
                Assert.True(actual.Receipts.TryGetValue(pair.Key, out TableDurableReceipt? recovered), "missing receipt " + pair.Key);
                AssertReceiptIdentity(pair.Value, recovered!);
            }
        }

        private static void AssertReceiptIdentity(TableDurableReceipt expected, TableDurableReceipt actual)
        {
            Assert.Equal(expected.AccountId, actual.AccountId);
            Assert.Equal(expected.ScopeId, actual.ScopeId);
            Assert.Equal(expected.RequestId, actual.RequestId);
            Assert.Equal(expected.Api, actual.Api);
            Assert.Equal(expected.PayloadSha256, actual.PayloadSha256);
            Assert.Equal(expected.Receipt.Status, actual.Receipt.Status);
            Assert.Equal(expected.Receipt.ReasonCode, actual.Receipt.ReasonCode);
            Assert.Equal(expected.Receipt.EvaluatedStateVersion, actual.Receipt.EvaluatedStateVersion);
            Assert.Equal(expected.Receipt.CommandVersion, actual.Receipt.CommandVersion);
            Assert.Equal(expected.Receipt.EventSeq, actual.Receipt.EventSeq);
            Assert.Equal(expected.Receipt.CanonicalPayloadSha256, actual.Receipt.CanonicalPayloadSha256);
            Assert.Equal(expected.CreatedAtUtcMs, actual.CreatedAtUtcMs);
            Assert.Equal(expected.ExpiresAtUtcMs, actual.ExpiresAtUtcMs);
        }

        private static string ReceiptSetDigest(Dictionary<string, TableDurableReceipt> receipts)
        {
            var text = new StringBuilder();
            foreach (KeyValuePair<string, TableDurableReceipt> pair in receipts.OrderBy(
                p => p.Key,
                StringComparer.Ordinal))
            {
                TableDurableReceipt receipt = pair.Value;
                text.Append(pair.Key).Append('|')
                    .Append(receipt.AccountId).Append('|')
                    .Append(receipt.ScopeId.Value).Append('|')
                    .Append(receipt.RequestId.Value).Append('|')
                    .Append(receipt.Api).Append('|')
                    .Append(((int)receipt.Receipt.Status).ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(receipt.Receipt.ReasonCode).Append('|')
                    .Append(receipt.Receipt.EvaluatedStateVersion.Value.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append((receipt.Receipt.CommandVersion?.Value ?? 0UL).ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append((receipt.Receipt.EventSeq?.Value ?? 0UL).ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(receipt.Receipt.CanonicalPayloadSha256).Append('|')
                    .Append(receipt.PayloadSha256.ToHex()).Append('|')
                    .Append(receipt.CreatedAtUtcMs.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(receipt.ExpiresAtUtcMs.ToString(CultureInfo.InvariantCulture))
                    .Append('\n');
            }

            return TablePayloadSha256.Compute(Encoding.UTF8.GetBytes(text.ToString())).ToHex();
        }

        private static string ReceiptKey(string accountId, string scopeId, string requestId) =>
            accountId + "\u001F" + scopeId + "\u001F" + requestId;

        private static string Sha256(byte[] bytes)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(bytes));
        }

        private sealed class ExpectedRuntime
        {
            public ulong StateVersion { get; set; }
            public TablePayloadSha256 StateHash { get; set; }
            public string RoundId { get; set; } = string.Empty;
            public ulong ConservationHash { get; set; }
            public TilePosition[] Tiles { get; set; } = Array.Empty<TilePosition>();
        }

        private sealed class ConfirmedAction
        {
            public ulong CommandVersion { get; set; }
            public string AccountId { get; set; } = string.Empty;
            public string ScopeId { get; set; } = string.Empty;
            public string RequestId { get; set; } = string.Empty;
            public string PayloadSha256 { get; set; } = string.Empty;
        }

        private sealed class ColdRead
        {
            public string PrimaryPresentBefore { get; set; } = string.Empty;
            public string PrimaryPresentAfter { get; set; } = string.Empty;
            public string BackupSha256Before { get; set; } = string.Empty;
            public string StateVersion { get; set; } = string.Empty;
            public string RoundId { get; set; } = string.Empty;
            public string TilesHash { get; set; } = string.Empty;
            public string TilesAssigned { get; set; } = string.Empty;
            public string ReceiptCount { get; set; } = string.Empty;
            public string ReceiptSetSha256 { get; set; } = string.Empty;
        }
    }
}
