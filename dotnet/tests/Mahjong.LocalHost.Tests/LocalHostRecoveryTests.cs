using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.TableRuntime;
using Xunit;

namespace Mahjong.LocalHost.Tests
{
using PlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;
    using DecisionView = Mahjong.Contracts.Wire.DecisionView;
    using LegalActionView = Mahjong.Contracts.Wire.LegalActionView;
    using MeldType = Mahjong.Contracts.Wire.MeldType;

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
                Assert.True(saved.StateVersion.Value >= baseline.StateVersion + 1UL, "saved=" + saved.StateVersion.Value);
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
                Assert.True(recovered.PlayerSnapshot!.StateVersion >= baseline.StateVersion + 1);
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
            harness.Options.Environment = LocalHostEnvironment.System;
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
            var start = CreateWorkerStartInfo(workerDll, harness.Options.SaveDirectory, P41Harness.RoomId, marker);
            using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!)
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waitCts.CancelAfter(TimeSpan.FromSeconds(30));
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(waitCts.Token);
                Task<string> stderrTask = process.StandardError.ReadToEndAsync(waitCts.Token);
                try
                {
                    await process.WaitForExitAsync(waitCts.Token);
                    await Task.WhenAll(stdoutTask, stderrTask);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(true);
                    throw new TimeoutException("CrashWorker did not exit/drain within the bounded timeout.");
                }

                Assert.NotEqual(0, process.ExitCode);
            }

            string[] markerLines = File.ReadAllLines(marker);
            Assert.True(markerLines.Length >= 7 + 136, "Marker file line count was " + markerLines.Length);
            bool hasReceiptMeta = markerLines.Length >= 13 + 136;
            int tileOffset = hasReceiptMeta ? 13 : 7;
            var scopeId = new RequestScopeId(markerLines[0]);
            var requestId = new RequestId(markerLines[1]);
            ulong committed = ulong.Parse(markerLines[2], System.Globalization.CultureInfo.InvariantCulture);
            string expectedAccount = markerLines[3];
            string expectedDigestHex = markerLines[4];
            string[] balancesStr = markerLines[5].Split(',');
            ulong expectedStateHash = ulong.Parse(markerLines[6], System.Globalization.CultureInfo.InvariantCulture);

            var expectedTiles = new (TileZone Zone, int Index)[136];
            for (int i = 0; i < 136; i++)
            {
                string[] parts = markerLines[tileOffset + i].Split(':');
                int tileId = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                var zone = (TileZone)int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                int idx = int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                expectedTiles[tileId] = (zone, idx);
            }

            using (LocalTableStore store = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, null, ct))
            {
                // 1) Receipt real-value verification
                TableDurableReceipt? receipt = await store.TryGetAsync(
                    expectedAccount,
                    scopeId,
                    requestId,
                    ct);
                Assert.NotNull(receipt);
                Assert.Equal(expectedAccount, receipt.AccountId);
                Assert.Equal(scopeId, receipt.ScopeId);
                Assert.Equal(requestId, receipt.RequestId);
                Assert.Equal(ReceiptStatus.Applied, receipt.Receipt.Status);
                Assert.True(receipt.IsAccepted);

                if (hasReceiptMeta)
                {
                    Assert.Equal(markerLines[7], receipt.Api);
                    Assert.Equal(ulong.Parse(markerLines[8], System.Globalization.CultureInfo.InvariantCulture), receipt.Receipt.EvaluatedStateVersion.Value);
                    Assert.Equal(ulong.Parse(markerLines[9], System.Globalization.CultureInfo.InvariantCulture), receipt.Receipt.CommandVersion!.Value.Value);
                    Assert.Equal(ulong.Parse(markerLines[10], System.Globalization.CultureInfo.InvariantCulture), receipt.Receipt.EventSeq!.Value.Value);
                    Assert.Equal(ulong.Parse(markerLines[11], System.Globalization.CultureInfo.InvariantCulture), receipt.CreatedAtUtcMs);
                    Assert.Equal(ulong.Parse(markerLines[12], System.Globalization.CultureInfo.InvariantCulture), receipt.ExpiresAtUtcMs);
                }
                Assert.Equal(scopeId, receipt.ScopeId);
                Assert.Equal(requestId, receipt.RequestId);
                Assert.Equal(ReceiptStatus.Applied, receipt.Receipt.Status);
                Assert.True(receipt.IsAccepted);

                // 2) Digest real-value verification (exact hex equality, not just length)
                Assert.Equal(expectedDigestHex, receipt.PayloadSha256.ToHex());

                // 3) Exact state version match
                TableStateRow row = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(committed, row.StateVersion.Value);

                // 4) Outbox is empty for non-terminal discard action
                IReadOnlyList<TableSettlementOutboxRecord> outbox = await store.ListUnpublishedAsync(ct);
                Assert.NotNull(outbox);
                Assert.Empty(outbox);

                // 5) Cheat Ledger balances and cheat state exact matching
                TableCheckpoint checkpoint = (await store.LoadCheckpointAsync(P41Harness.RoomId, ct))!;
                Assert.NotNull(checkpoint.CheatPayload);
                TableCheatLedgerCheckpoint cheatCp = TableCheatLedgerCheckpointCodec.Decode(checkpoint.CheatPayload!);
                CheatLedger ledger = cheatCp.Restore();
                Assert.NotNull(ledger);
                Assert.Equal(4, cheatCp.Balances.Count);
                for (int s = 0; s < 4; s++)
                {
                    Assert.Equal(long.Parse(balancesStr[s], System.Globalization.CultureInfo.InvariantCulture), cheatCp.Balances[s]);
                }
                CheatState cheatState = cheatCp.RestoreCheatState();
                Assert.NotNull(cheatState);
                Assert.NotNull(cheatState.TileMarks);
                Assert.NotNull(cheatState.SleeveBloom);
                Assert.NotNull(cheatState.BeamSwap);
            }

            // 6) 136 Physical entities conservation & exact position match through authoritative cold recovery
            await using (LocalTableRuntime runtime = await LocalTableRuntime.OpenAsync(
                harness.Options,
                ct))
            {
                RoundStateMachine recoveredRound = runtime.ActorForTest.GetRoundStateForHost();
                TilePositionMap recoveredTiles = recoveredRound.Tiles;
                ConservationReport report = TilePositionMap.Validate(recoveredTiles);
                Assert.True(report.IsValid, string.Join("; ", report.Errors));
                Assert.Equal(136, report.AssignedCount);
                Assert.Equal(0, report.UnassignedCount);
                for (int i = 0; i < 136; i++)
                {
                    Assert.True(
                        expectedTiles[i].Zone == recoveredTiles[i].Zone && expectedTiles[i].Index == recoveredTiles[i].Index,
                        $"Tile {i} differs: expected {expectedTiles[i].Zone}:{expectedTiles[i].Index}, actual {recoveredTiles[i].Zone}:{recoveredTiles[i].Index}");
                }
                Assert.Equal(expectedStateHash, report.StateHash);
            }
        }

        [Fact]
        public async Task CrashWorkerRealProcessKill_NonInitialCheatLeavesDurableCheatState()
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            harness.Options.Environment = LocalHostEnvironment.System;
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

            Assert.True(File.Exists(workerDll), "CrashWorker DLL not found at: " + workerDll);

            string marker = Path.Combine(harness.Root, "crash-cheat-marker.txt");
            var startInfo = CreateWorkerStartInfo(workerDll, harness.Options.SaveDirectory, P41Harness.RoomId, marker, "cheat");

            string stdout;
            string stderr;
            int exitCode;
            using (var process = System.Diagnostics.Process.Start(startInfo)!)
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waitCts.CancelAfter(TimeSpan.FromSeconds(30));
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(waitCts.Token);
                Task<string> stderrTask = process.StandardError.ReadToEndAsync(waitCts.Token);
                try
                {
                    await process.WaitForExitAsync(waitCts.Token);
                    stdout = await stdoutTask;
                    stderr = await stderrTask;
                    exitCode = process.ExitCode;
                }
                catch (OperationCanceledException)
                {
                    process.Kill(true);
                    throw new TimeoutException("CrashWorker cheat mode did not exit/drain within bounded timeout.");
                }

                Assert.NotEqual(0, process.ExitCode);
            }

            Assert.True(File.Exists(marker), $"Marker not found. ExitCode: {exitCode}, Stderr: {stderr}, Stdout: {stdout}");

            string[] markerLines = File.ReadAllLines(marker);
            Assert.True(markerLines.Length >= 17 + 136, "Marker file line count was " + markerLines.Length);
            var scopeId = new RequestScopeId(markerLines[0]);
            var requestId = new RequestId(markerLines[1]);
            ulong committedVersion = ulong.Parse(markerLines[2], System.Globalization.CultureInfo.InvariantCulture);
            string expectedAccount = markerLines[3];
            string expectedDigestHex = markerLines[4];
            string[] balancesStr = markerLines[5].Split(',');
            ulong expectedStateHash = ulong.Parse(markerLines[6], System.Globalization.CultureInfo.InvariantCulture);

            string expectedApi = markerLines[7];
            ulong expectedEvalVersion = ulong.Parse(markerLines[8], System.Globalization.CultureInfo.InvariantCulture);
            ulong expectedCommittedVersion = ulong.Parse(markerLines[9], System.Globalization.CultureInfo.InvariantCulture);
            ulong expectedEventSeq = ulong.Parse(markerLines[10], System.Globalization.CultureInfo.InvariantCulture);
            ulong expectedCreatedUtc = ulong.Parse(markerLines[11], System.Globalization.CultureInfo.InvariantCulture);
            ulong expectedExpiresUtc = ulong.Parse(markerLines[12], System.Globalization.CultureInfo.InvariantCulture);

            string expectedMarksHex = markerLines[13];
            string expectedSleeveHex = markerLines[14];
            string expectedBeamHex = markerLines[15];
            int expectedLedgerCount = int.Parse(markerLines[16], System.Globalization.CultureInfo.InvariantCulture);

            var expectedTiles = new (TileZone Zone, int Index)[136];
            for (int i = 0; i < 136; i++)
            {
                string[] parts = markerLines[17 + i].Split(':');
                int tileId = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                var zone = (TileZone)int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                int idx = int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                expectedTiles[tileId] = (zone, idx);
            }

            // 1) Verify durable store recovery
            using (LocalTableStore store = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, null, ct))
            {
                TableDurableReceipt? receipt = await store.TryGetAsync(expectedAccount, scopeId, requestId, ct);
                Assert.NotNull(receipt);
                Assert.Equal(expectedAccount, receipt.AccountId);
                Assert.Equal(scopeId, receipt.ScopeId);
                Assert.Equal(requestId, receipt.RequestId);
                Assert.Equal(ReceiptStatus.Applied, receipt.Receipt.Status);
                Assert.True(receipt.IsAccepted);

                // Full receipt fields
                Assert.Equal(expectedApi, receipt.Api);
                Assert.Equal(expectedEvalVersion, receipt.Receipt.EvaluatedStateVersion.Value);
                Assert.Equal(expectedCommittedVersion, receipt.Receipt.CommandVersion!.Value.Value);
                Assert.Equal(expectedEventSeq, receipt.Receipt.EventSeq!.Value.Value);
                Assert.Equal(expectedCreatedUtc, receipt.CreatedAtUtcMs);
                Assert.Equal(expectedExpiresUtc, receipt.ExpiresAtUtcMs);
                Assert.Equal(expectedDigestHex, receipt.PayloadSha256.ToHex());

                // State row cheat bindings
                TableStateRow row = (await store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(committedVersion, row.StateVersion.Value);
                Assert.True(row.CheatBound);
                Assert.True(row.CheatVersion > 0L);

                // Cheat state and ledger
                TableCheckpoint checkpoint = (await store.LoadCheckpointAsync(P41Harness.RoomId, ct))!;
                Assert.NotNull(checkpoint.CheatPayload);
                TableCheatLedgerCheckpoint cheatCp = TableCheatLedgerCheckpointCodec.Decode(checkpoint.CheatPayload!);
                CheatState restoredCheat = cheatCp.RestoreCheatState();
                Assert.NotNull(restoredCheat);
                Assert.Equal(4, cheatCp.Balances.Count);
                for (int s = 0; s < 4; s++)
                {
                    Assert.Equal(long.Parse(balancesStr[s], System.Globalization.CultureInfo.InvariantCulture), cheatCp.Balances[s]);
                }
                Assert.Equal(80, restoredCheat.Ledger.BalanceOf(0));
                Assert.Equal(expectedLedgerCount, restoredCheat.Ledger.Entries.Count);

                // Canonical byte verification of 3 tricks
                BusinessPayload pMarks = CommandPayloadFactory.SubmitTileMark(checkpoint.RoomId + "-r1", "dec-cw-1", 0UL, new uint[] { 10, 11, 12, 13 });
                BusinessPayload pSleeve = CommandPayloadFactory.SubmitSleeveBloom(checkpoint.RoomId + "-r1", "dec-cw-2", 0UL);
                BusinessPayload pBeam = CommandPayloadFactory.SubmitBeamSwap(checkpoint.RoomId + "-r1", "dec-cw-3", 0UL);
                Assert.Equal(expectedMarksHex, Convert.ToHexString(pMarks.CopyBytes()));
                Assert.Equal(expectedSleeveHex, Convert.ToHexString(pSleeve.CopyBytes()));
                Assert.Equal(expectedBeamHex, Convert.ToHexString(pBeam.CopyBytes()));
            }

            // 2) Authoritative cold recovery through LocalTableRuntime
            await using (LocalTableRuntime runtime = await LocalTableRuntime.OpenAsync(harness.Options, ct))
            {
                RoundStateMachine recoveredRound = runtime.ActorForTest.GetRoundStateForHost();
                TilePositionMap recoveredTiles = recoveredRound.Tiles;
                ConservationReport report = TilePositionMap.Validate(recoveredTiles);
                Assert.True(report.IsValid, string.Join("; ", report.Errors));
                Assert.Equal(136, report.AssignedCount);
                Assert.Equal(0, report.UnassignedCount);

                for (int i = 0; i < 136; i++)
                {
                    Assert.True(
                        expectedTiles[i].Zone == recoveredTiles[i].Zone && expectedTiles[i].Index == recoveredTiles[i].Index,
                        $"Tile {i} differs: expected {expectedTiles[i].Zone}:{expectedTiles[i].Index}, actual {recoveredTiles[i].Zone}:{recoveredTiles[i].Index}");
                }
                Assert.Equal(expectedStateHash, report.StateHash);

                // Hand tile physically in wall at slot 53
                int handTileAtWall53 = -1;
                for (int i = 0; i < 136; i++)
                {
                    if (recoveredTiles[i].Zone == TileZone.Wall && recoveredTiles[i].Index == 53)
                    {
                        handTileAtWall53 = i;
                        break;
                    }
                }
                Assert.True(handTileAtWall53 >= 0);

                // Authoritative actor cheat state
                var cheatField = typeof(TableActor).GetField("_cheat", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var actorCheat = cheatField?.GetValue(runtime.ActorForTest) as CheatState;
                Assert.NotNull(actorCheat);
                Assert.Equal(80, actorCheat.Ledger.BalanceOf(0));
                Assert.Equal(expectedLedgerCount, actorCheat.Ledger.Entries.Count);
                Assert.NotEmpty(actorCheat.TileMarks.Groups);
                Assert.NotEmpty(actorCheat.SleeveBloom.Reservations);
                Assert.NotNull(actorCheat.BeamSwap.Offer);
                Assert.Equal(53, actorCheat.BeamSwap.Offer.Candidates[0].Slot.Index);
            }
        }

        [Fact]
        public async Task CrashWorkerRealProcessKill_SettlementOutboxLeavesDurableOutbox()
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            harness.Options.Environment = LocalHostEnvironment.System;
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
            string marker = Path.Combine(harness.Root, "crash-settlement-marker.txt");
            var start = CreateWorkerStartInfo(workerDll, harness.Options.SaveDirectory, P41Harness.RoomId, marker, "settlement");

            using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!)
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waitCts.CancelAfter(TimeSpan.FromSeconds(30));
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(waitCts.Token);
                Task<string> stderrTask = process.StandardError.ReadToEndAsync(waitCts.Token);
                try
                {
                    await process.WaitForExitAsync(waitCts.Token);
                    await Task.WhenAll(stdoutTask, stderrTask);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(true);
                    throw new TimeoutException("CrashWorker settlement mode did not exit within timeout.");
                }

                Assert.NotEqual(0, process.ExitCode);
            }

            string[] markerLines = File.ReadAllLines(marker);
            Assert.Equal(5, markerLines.Length);
            string matchId = markerLines[0];
            int resultVersion = int.Parse(markerLines[1], System.Globalization.CultureInfo.InvariantCulture);
            string expectedHashHex = markerLines[2];
            string expectedPayloadHex = markerLines[3];
            string expectedStatus = markerLines[4];
            Assert.Equal("unpublished", expectedStatus);

            using (LocalTableStore store = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, null, ct))
            {
                IReadOnlyList<TableSettlementOutboxRecord> outbox = await store.ListUnpublishedAsync(ct);
                Assert.Single(outbox);
                Assert.Equal(matchId, outbox[0].MatchId);
                Assert.Equal(resultVersion, outbox[0].ResultVersion);
                Assert.Equal(expectedHashHex, outbox[0].PayloadSha256.ToHex());
                Assert.Equal(expectedPayloadHex, Convert.ToHexString(outbox[0].Payload));
                Assert.Null(outbox[0].PublishedAtUtcMs);

                TableSettlementOutboxRecord? record = await store.TryGetAsync(matchId, resultVersion, ct);
                Assert.NotNull(record);
                Assert.Equal(matchId, record!.MatchId);
                Assert.Equal(resultVersion, record.ResultVersion);
                Assert.Equal(expectedHashHex, record.PayloadSha256.ToHex());
                Assert.Equal(expectedPayloadHex, Convert.ToHexString(record.Payload));

                // Mark published and assert removal from unpublished list
                bool marked = await store.MarkPublishedAsync(matchId, resultVersion, 1_700_000_100_000UL, ct);
                Assert.True(marked);

                IReadOnlyList<TableSettlementOutboxRecord> afterPublish = await store.ListUnpublishedAsync(ct);
                Assert.Empty(afterPublish);

                TableSettlementOutboxRecord? publishedRecord = await store.TryGetAsync(matchId, resultVersion, ct);
                Assert.NotNull(publishedRecord);
                Assert.Equal(1_700_000_100_000UL, publishedRecord!.PublishedAtUtcMs);
            }
        }
        private static async Task WaitForStableStateAsync(TableSession session, CancellationToken ct)
        {
            ulong last = session.PlayerSnapshot!.StateVersion;
            int stable = 0;
            for (int i = 0; i < 100 && stable < 5; i++)
            {
                await Task.Delay(20, ct);
                ulong current = session.PlayerSnapshot!.StateVersion;
                if (current == last)
                {
                    stable++;
                }
                else
                {
                    last = current;
                    stable = 0;
                }
            }
        }
        [Fact]
        public void CandidateClonePreservesFuritenFacts()
        {
            var save = new LocalSave();
            save.TemporaryFuriten[1] = true;
            save.RiichiFuriten[1] = true;
            LocalSave clone = save.DeepCloneForCandidate();
            Assert.True(clone.TemporaryFuriten[1]);
            Assert.True(clone.RiichiFuriten[1]);
            clone.TemporaryFuriten[1] = false;
            Assert.True(save.TemporaryFuriten[1]);
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
                Assert.Equal(64, receipt.Resolution.CanonicalPayloadSha256.Length);
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

        private static System.Diagnostics.ProcessStartInfo CreateWorkerStartInfo(
            string workerDll,
            string saveDirectory,
            string roomId,
            string markerPath,
            string? mode = null)
        {
            string dotnet = ResolveDotnetPath();
            string args = "\"" + workerDll + "\" \"" + saveDirectory + "\" \"" + roomId + "\" \"" + markerPath + "\"";
            if (!string.IsNullOrEmpty(mode))
            {
                args += " " + mode;
            }

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = dotnet,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            string dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? string.Empty;
            if (string.IsNullOrEmpty(dotnetRoot) && File.Exists("D:/Unity/dotnet/dotnet.exe"))
            {
                dotnetRoot = "D:/Unity/dotnet";
            }

            if (!string.IsNullOrEmpty(dotnetRoot))
            {
                startInfo.Environment["DOTNET_ROOT"] = dotnetRoot;
            }

            return startInfo;
        }

        private static string ResolveDotnetPath()
        {
            string? processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath) &&
                Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return processPath;
            }

            string dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? string.Empty;
            if (!string.IsNullOrEmpty(dotnetRoot))
            {
                string candidate = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            if (File.Exists("D:/Unity/dotnet/dotnet.exe"))
            {
                return "D:/Unity/dotnet/dotnet.exe";
            }

            return "dotnet";
        }

        [Fact]
        public async Task PendingChiCandidateSurvivesColdRecoveryAndSubmitsSuccessfully()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;

            int[] hand = { 0, 4, 12, 16, 20, 24, 28, 36, 40, 44, 48, 52, 56, 132 };
            int[] slots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            var map = new Dictionary<int, int>();
            for (int i = 0; i < slots.Length; i++) map[slots[i]] = hand[i];
            map[53] = 133; map[54] = 134; map[55] = 8;
            map[100] = 9; map[101] = 10; map[102] = 11;

            var used = new HashSet<int>(map.Values);
            var rest = new Queue<int>(Enumerable.Range(0, 136).Where(id => !used.Contains(id)));
            var wall = Enumerable.Range(0, 136).Select(i => new TileId(map.TryGetValue(i, out int id) ? id : rest.Dequeue())).ToArray();
            var tiles = TilePositionMap.CreateDealt(RecordedRandomDraw.Create("review-wall", wall), 0);
            harness.Options.InitialRound = RoundStateMachine.CreateDealt(new RoundSpec("chi-rec-round", 0, 1, 1, 0,
                new long[] { 25000, 25000, 25000, 25000 }, 0), tiles, RoundPhase.CheatWindow);

            (ITableChannel channel, TableSession session) = await OpenSessionAsync(harness, ct);
            using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pumpCts.CancelAfter(TimeSpan.FromSeconds(20));
            Task pump = Task.Run(async () =>
            {
                try
                {
                    while (!pumpCts.IsCancellationRequested)
                    {
                        await session.PumpOnceAsync(pumpCts.Token);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException || ex is TableChannelClosedException)
                {
                }
            }, pumpCts.Token);

            LegalActionView? chiBefore = null;
            try
            {
                for (int i = 0; i < 50 && (session.PlayerSnapshot?.PrivateView?.Decision == null || session.PlayerSnapshot.PrivateView.Decision.Actions.Count == 0); i++)
                {
                    await Task.Delay(20, pumpCts.Token);
                }
                PlayerSnapshot initial = session.PlayerSnapshot!;
                Assert.NotNull(initial.PrivateView?.Decision);
                var d = initial.PrivateView.Decision;

                var discardResolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == "cold-rec-discard-132")
                    {
                        discardResolved.TrySetResult(args);
                    }
                };

                var payload = CommandPayloadFactory.SubmitAction(initial.RoundId, d.DecisionId, d.ControlEpoch,
                    ProtocolActionType.DiscardTile, tileIds: new uint[] { 132u });
                await session.SubmitAsync(new ProtocolCommand(CommandKind.SubmitAction, payload,
                    new RequestId("cold-rec-discard-132"), session.Binding!.Scope.ScopeId,
                    new StateVersion(initial.StateVersion), new RoundId(initial.RoundId),
                    decisionId: new DecisionId(d.DecisionId), controlEpoch: new ControlEpoch(d.ControlEpoch)), pumpCts.Token);
                await LocalHostCommandTests.WithTimeoutAsync(discardResolved.Task, pumpCts.Token);

                for (int i = 0; i < 50; i++)
                {
                    var dec = session.PlayerSnapshot?.PrivateView?.Decision;
                    if (dec != null && dec.Actions.Any(a => a.ActionType == (uint)ProtocolActionType.DeclareChi))
                    {
                        chiBefore = dec.Actions.First(a => a.ActionType == (uint)ProtocolActionType.DeclareChi);
                        break;
                    }
                    await Task.Delay(20, pumpCts.Token);
                }
                Assert.NotNull(chiBefore);
                Assert.Contains(0u, chiBefore.TileIds);
                Assert.Contains(4u, chiBefore.TileIds);
                Assert.Contains(8u, chiBefore.TileIds);
            }
            finally
            {
                pumpCts.Cancel();
                await pump;
                await CloseAsync(session, channel);
            }

            (ITableChannel reopened, TableSession restored) = await OpenSessionAsync(harness, ct);
            using var pumpCts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pumpCts2.CancelAfter(TimeSpan.FromSeconds(20));
            Task pump2 = Task.Run(async () =>
            {
                try
                {
                    while (!pumpCts2.IsCancellationRequested)
                    {
                        await restored.PumpOnceAsync(pumpCts2.Token);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException || ex is TableChannelClosedException)
                {
                }
            }, pumpCts2.Token);

            try
            {
                for (int i = 0; i < 50 && (restored.PlayerSnapshot?.PrivateView?.Decision == null || restored.PlayerSnapshot.PrivateView.Decision.Actions.Count == 0); i++)
                {
                    await Task.Delay(20, pumpCts2.Token);
                }

                PlayerSnapshot restoredSnapshot = restored.PlayerSnapshot!;
                Assert.NotNull(restoredSnapshot.PrivateView?.Decision);
                DecisionView restoredDecision = restoredSnapshot.PrivateView.Decision;
                LegalActionView? chiAfter = restoredDecision.Actions.FirstOrDefault(
                    a => a.ActionType == (uint)ProtocolActionType.DeclareChi);
                Assert.NotNull(chiAfter);
                Assert.Equal(3, chiAfter.TileIds.Count);
                Assert.Contains(0u, chiAfter.TileIds);
                Assert.Contains(4u, chiAfter.TileIds);
                Assert.Contains(8u, chiAfter.TileIds);

                var chiResolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                restored.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == "restored-chi-submission")
                    {
                        chiResolved.TrySetResult(args);
                    }
                };

                BusinessPayload chiPayload = CommandPayloadFactory.SubmitAction(
                    restoredSnapshot.RoundId,
                    restoredDecision.DecisionId,
                    restoredDecision.ControlEpoch,
                    ProtocolActionType.DeclareChi,
                    optionId: chiAfter.OptionId,
                    tileIds: chiAfter.TileIds.ToArray());
                var chiCommand = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    chiPayload,
                    new RequestId("restored-chi-submission"),
                    restored.Binding!.Scope.ScopeId,
                    new StateVersion(restoredSnapshot.StateVersion),
                    new RoundId(restoredSnapshot.RoundId),
                    decisionId: new DecisionId(restoredDecision.DecisionId),
                    controlEpoch: new ControlEpoch(restoredDecision.ControlEpoch));

                await restored.SubmitAsync(chiCommand, pumpCts2.Token);
                TableReceiptResolvedEventArgs receipt = await LocalHostCommandTests.WithTimeoutAsync(
                    chiResolved.Task, pumpCts2.Token);
                Assert.Equal(ReceiptStatus.Applied, receipt.Resolution.ReceiptStatus);

                for (int i = 0; i < 30 && restored.PlayerSnapshot!.PublicView.Board.Players[0].Melds.Count == 0; i++)
                {
                    await Task.Delay(20, pumpCts2.Token);
                }

                Assert.Single(restored.PlayerSnapshot!.PublicView.Board.Players[0].Melds);
                var meld = restored.PlayerSnapshot.PublicView.Board.Players[0].Melds[0];
                Assert.Equal(MeldType.Chi, meld.Type);
                Assert.Equal(3, meld.TileIds.Count);
                Assert.Contains(8u, meld.TileIds);
                Assert.Contains(0u, meld.TileIds);
                Assert.Contains(4u, meld.TileIds);

                pumpCts2.Cancel();
                await pump2;
            }
            finally
            {
                await CloseAsync(restored, reopened);
            }
        }
    }
}
