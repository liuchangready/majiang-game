using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Domain;
using Mahjong.TableRuntime;
using Xunit;
using ContractReceiptStatus = Mahjong.Contracts.ReceiptStatus;
using ContractRequestScope = Mahjong.Contracts.RequestScope;
using WireEnvelope = Mahjong.Contracts.Wire.Envelope;
using WireMessageType = Mahjong.Contracts.Wire.MessageType;
using WireReceiptStatus = Mahjong.Contracts.Wire.ReceiptStatus;

namespace Mahjong.LocalHost.Tests
{
    /// <summary>
    /// C04-D01: one real Discard is committed once, the exact canonical request is
    /// reconciled through the real TableSession -> ITableChannel -> LocalTableRuntime
    /// receipt-query wire route after five recovery classes, and repeated query/retry
    /// adds no state, event, ledger or physical-entity effect. The three frozen
    /// non-recovery classes (FRESH_START / REJECTED) are observed as zero-send
    /// boundaries rather than being forced to invent a new business side effect.
    /// </summary>
    public sealed class C04RecoveryRequestReconciliationTests
    {
        private const string RoomId = "room-1";
        private const string LocalAccount = "local-" + RoomId + "-seat0";

        public static IEnumerable<object[]> RecoveryRows()
        {
            yield return new object[] { "C04-G6-01", "None", "RECOVERED" };
            yield return new object[] { "C04-G6-03", "TruncatePrimary", "RECOVERED_FROM_BACKUP" };
            yield return new object[] { "C04-G6-04", "BadChecksumPrimary", "RECOVERED_FROM_BACKUP" };
            yield return new object[] { "C04-G6-05", "MissingPrimaryWithBackup", "RECOVERED_FROM_BACKUP" };
            yield return new object[] { "C04-G6-06", "BothMissing", "FRESH_START" };
            yield return new object[] { "C04-G6-07", "BothBad", "REJECTED" };
            yield return new object[] { "C04-G6-08", "IncompatiblePrimary", "REJECTED" };
        }

        [Theory]
        [MemberData(nameof(RecoveryRows))]
        public async Task RecoveryRow_ReconcilesOriginalRequestWithoutDuplicateSideEffect(
            string caseId,
            string fault,
            string expectedOutcome)
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            string evidenceRoot = NewEvidenceRoot(caseId);
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            harness.Options.RoomId = RoomId;

            var outbox = new InMemoryOutboxStore();
            OriginalCommit original = await CommitOriginalDiscardAsync(harness, outbox, caseId, ct);
            AssertOriginalCommit(caseId, original);

            // The accepted save writer publishes the previous primary as .prev. A
            // valid backup of the *post-Discard* state is therefore seeded from the
            // exact committed primary bytes before applying the read faults. Both
            // copies are hashed and the backup's decodability is checked below.
            File.Copy(harness.SavePath, harness.BackupPath, true);
            string primaryBeforeFault = Sha256File(harness.SavePath);
            string backupBeforeFault = Sha256File(harness.BackupPath);

            SaveFaultApplied saveFault = ApplySaveFault(harness, fault);
            var result = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["caseId"] = caseId,
                ["fault"] = fault,
                ["expectedOutcome"] = expectedOutcome,
                ["original"] = original.Summary(),
                ["primaryBeforeFaultSha256"] = primaryBeforeFault,
                ["backupBeforeFaultSha256"] = backupBeforeFault,
                ["saveFault"] = saveFault.ToSummary(),
            };

            if (expectedOutcome == "REJECTED")
            {
                await AssertRejectedZeroSendAsync(harness, outbox, caseId, fault, saveFault, ct, result);
            }
            else if (expectedOutcome == "FRESH_START")
            {
                await AssertFreshStartNoOldScopeAsync(harness, outbox, caseId, ct, result);
            }
            else
            {
                await AssertRecoveredAndReconciledAsync(
                    harness,
                    outbox,
                    original,
                    caseId,
                    fault,
                    expectedOutcome,
                    ct,
                    result);
            }

            WriteEvidence(evidenceRoot, caseId, result);
            Assert.Equal(expectedOutcome, result["actualOutcome"]);
        }

        [Fact]
        public async Task ForceKilledProcess_ReconcilesOriginalRequestWithoutDuplicateSideEffect()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            const string caseId = "C04-G6-02";
            string evidenceRoot = NewEvidenceRoot(caseId);
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            harness.Options.RoomId = RoomId;
            string roomRoot = Path.Combine(harness.Root, "force");
            Directory.CreateDirectory(roomRoot);
            LocalTableHostOptions options = NewOptions(roomRoot, "c04-force-original-owner");

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

            string markerPath = Path.Combine(roomRoot, "force-marker.txt");
            ProcessStartInfo start = P41Harness.CreateWorkerStartInfo(
                workerDll,
                options.SaveDirectory,
                options.RoomId,
                markerPath);
            using Process process = Process.Start(start)!;
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(TimeSpan.FromSeconds(60));
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
                throw new TimeoutException("CrashWorker did not exit within 60s.");
            }

            Assert.NotEqual(0, process.ExitCode);
            Assert.True(File.Exists(markerPath), "CrashWorker marker is missing.");
            string[] marker = File.ReadAllLines(markerPath);
            Assert.True(marker.Length >= 7 + 136, "CrashWorker marker is incomplete.");
            string scopeId = marker[0];
            string requestId = marker[1];
            string markerStateVersion = marker[2];
            string account = marker[3];
            string markerDigest = marker[4];

            string savePath = Path.Combine(options.SaveDirectory, options.RoomId + ".save");
            byte[] saveBytes = File.ReadAllBytes(savePath);
            LocalSave save = LocalSaveCodec.DecodeEnvelope(saveBytes);
            TableDurableReceipt receipt = save.Receipts.Values.Single(r =>
                r.AccountId == account &&
                r.ScopeId.Value == scopeId &&
                r.RequestId.Value == requestId);
            Assert.Equal(ContractReceiptStatus.Applied, receipt.Receipt.Status);
            Assert.Equal("SUBMIT_ACTION", receipt.Api);
            Assert.True(
                string.Equals(markerDigest, receipt.PayloadSha256.ToHex(), StringComparison.OrdinalIgnoreCase),
                "marker/receipt digest differs: marker=" + markerDigest + " receipt=" + receipt.PayloadSha256.ToHex());

            TableCommandJournalRecord journal = save.CommandJournals.Single(j =>
                j.RequestId.Value == requestId && j.ScopeId.Value == scopeId);
            Assert.Equal(receipt.PayloadSha256, journal.PayloadSha256);
            Assert.Equal(receipt.Receipt.EvaluatedStateVersion, journal.EvaluatedStateVersion);
            Assert.Equal(receipt.Receipt.CommandVersion, journal.CommandVersion);
            RoundCommand journalCommand = RoundCommandCodec.Decode(journal.CommandPayload);
            Assert.Equal(RoundActionKind.DiscardTile, journalCommand.Action);
            Assert.Equal(0, journalCommand.Seat);
            Assert.True(journalCommand.Tile.HasValue);
            BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                journalCommand.RoundId,
                "local-direct",
                0UL,
                ProtocolActionType.DiscardTile,
                tileIds: new[] { (uint)journalCommand.Tile!.Value.Value });
            string payloadSha = TablePayloadSha256.Compute(payload.CopyBytes()).ToHex();
            Assert.True(
                string.Equals(markerDigest, payloadSha, StringComparison.OrdinalIgnoreCase),
                "marker/payload digest differs: marker=" + markerDigest + " payload=" + payloadSha +
                " journal=" + journal.PayloadSha256.ToHex() + " payloadBytes=" + payload.Length);
            LocalTableRecoveryFacts recoveredFacts = BuildForceSummaryFacts(
                saveBytes,
                save,
                journalCommand.RoundId,
                markerStateVersion);
            var command = new ProtocolCommand(
                CommandKind.SubmitAction,
                payload,
                new RequestId(requestId),
                new RequestScopeId(scopeId),
                journal.EvaluatedStateVersion,
                new RoundId(journalCommand.RoundId),
                decisionId: null,
                controlEpoch: new ControlEpoch(0),
                ownerEpoch: null,
                payloadSchemaVersion: CanonicalPayloadDigest.FormatVersion);

            // Reconstruct the exact durable outbox entry that a killed process would
            // leave behind. The canonical bytes come from the committed journal, not
            // from a newly encoded fixture.
            string outboxDirectory = Path.Combine(roomRoot, "outbox");
            var fileOutbox = new FileOutboxStore(outboxDirectory);
            var ownerScope = new ContractRequestScope(
                new RequestScopeId(scopeId),
                account,
                ProtocolRole.Player,
                new RoomId(options.RoomId),
                receipt.CreatedAtUtcMs,
                receipt.ExpiresAtUtcMs);
            var outboxEntry = new OutboxEntry(
                command,
                ownerScope,
                new SessionId("force-killed-original-session"),
                new RoomId(options.RoomId));
            Assert.Equal(
                CanonicalPayloadDigest.Compute(payload).ToHex(),
                outboxEntry.DigestSha256);
            await fileOutbox.SaveAsync(outboxEntry, ct);
            Assert.Single(await fileOutbox.LoadUnresolvedAsync(ct));

            SaveShape forceShape = ReadSaveShape(saveBytes);
            Assert.Equal(13, forceShape.HandCount);
            Assert.Equal(1, forceShape.RiverCount);
            Assert.Equal(1, forceShape.DiscardEvents);
            Assert.Equal(136, forceShape.PhysicalCount);
            Assert.Equal(136, forceShape.PhysicalUniqueCount);

            // A valid post-Discard backup is seeded exactly as in the non-Force rows.
            string backupPath = savePath + ".prev";
            File.Copy(savePath, backupPath, true);

            var noChannel = new RecordingChannel(new NullChannel());
            OriginalCommit original = new OriginalCommit(
                options,
                noChannel,
                new NullChannel(),
                null!,
                command,
                payload,
                scopeId,
                account,
                receipt,
                recoveredFacts,
                recoveredFacts,
                markerStateVersion,
                marker,
                process.Id,
                process.ExitCode,
                saveBytes,
                File.ReadAllBytes(backupPath),
                new List<FrameRecord>(),
                preShape: null,
                tile: (uint)save.EventJournals.Single(e =>
                    e.Type == RoundEventType.TileDiscarded && e.Seat == 0).Tile!.Value.Value,
                preHandCount: 14,
                preRiverCount: 0);
            var result = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["caseId"] = caseId,
                ["fault"] = "None",
                ["expectedOutcome"] = "RECOVERED",
                ["force"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["pid"] = process.Id,
                    ["nativeExitCode"] = process.ExitCode,
                    ["disposition"] = "FORCE_KILLED",
                    ["markerSha256"] = Sha256File(markerPath),
                    ["scopeId"] = scopeId,
                    ["requestId"] = requestId,
                    ["canonicalPayloadSha256"] = markerDigest,
                },
                ["original"] = original.Summary(),
            };

            await AssertRecoveredAndReconciledAsync(
                options,
                fileOutbox,
                original,
                caseId,
                "None",
                "RECOVERED",
                ct,
                result,
                expectedHandCount: 13,
                expectedRiverCount: 1);
            WriteEvidence(evidenceRoot, caseId, result);
        }

        [Fact]
        public async Task UnknownLookup_RetriesOnlySameCanonicalRequestAndReplaysWithoutDelta()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            harness.Options.RoomId = RoomId;
            var outbox = new InMemoryOutboxStore();
            OriginalCommit original = await CommitOriginalDiscardAsync(
                harness,
                outbox,
                "C04-UNKNOWN",
                ct,
                keepOpen: true);

            // The local handler is healthy and would answer FOUND. A transport-level
            // outcome fault replaces only the first lookup's status with UNKNOWN; the
            // retried command still traverses the real handler and durable receipt.
            RecordingChannel channel = original.Channel;
            channel.LookupFault = LookupFault.UnknownOnce;
            ITableChannel raw = original.RawChannel;
            TableSession session = original.Session;
            try
            {
                Assert.Equal(TableConnectionState.Ready, session.State);
                Assert.Single(session.PendingCommands);
                PendingCommandSnapshot pending = session.PendingCommands.Single();
                Assert.Equal(original.Command.RequestId.Value, pending.RequestId);
                Assert.Equal(original.Command.ScopeId!.Value.Value, pending.ScopeId);
                Assert.True(
                    string.Equals(original.CanonicalDigest, pending.DigestSha256, StringComparison.OrdinalIgnoreCase),
                    "pending canonical digest differs: expected=" + original.CanonicalDigest + " actual=" + pending.DigestSha256);

                // Simulate a lost original receipt response before the lookup: the
                // committed receipt is deliberately not delivered to this session.
                channel.DropReceiptRequestId = original.Command.RequestId.Value;
                LocalTableRecoveryFacts before = await CaptureFactsAsync(
                    harness.Options.SaveDirectory,
                    ct);
                await session.ReconcilePendingAsync(ct);
                using (var lookupCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    lookupCts.CancelAfter(TimeSpan.FromSeconds(10));
                    for (int i = 0; i < 20 && session.PendingCommands.Count > 0; i++)
                    {
                        await session.PumpOnceAsync(lookupCts.Token);
                        PendingCommandSnapshot? current = session.PendingCommands.FirstOrDefault();
                        if (current?.LastLookup == ReceiptLookupStatus.Unknown)
                        {
                            break;
                        }
                    }
                }

                Assert.Single(session.PendingCommands);
                PendingCommandSnapshot unknown = session.PendingCommands.Single();
                Assert.Equal(ReceiptLookupStatus.Unknown, unknown.LastLookup);
                channel.DropReceiptRequestId = null;

                CommandSubmission retry = await session.RetryPendingAsync(unknown.Key, ct);
                Assert.True(retry.Sent);
                using (var retryCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    retryCts.CancelAfter(TimeSpan.FromSeconds(10));
                    for (int i = 0; i < 20 && session.PendingCommands.Count > 0; i++)
                    {
                        await session.PumpOnceAsync(retryCts.Token);
                    }
                }

                Assert.Empty(session.PendingCommands);
                LocalTableRecoveryFacts after = await CaptureFactsAsync(
                    harness.Options.SaveDirectory,
                    ct);
                Assert.Equal(before.StateVersion, after.StateVersion);
                Assert.Equal(before.EventSeq, after.EventSeq);
                Assert.Equal(before.Ledger.EntriesSha256, after.Ledger.EntriesSha256);
                Assert.Equal(before.Ledger.BalancesSha256, after.Ledger.BalancesSha256);
                Assert.Equal(before.Physical.TilesSha256, after.Physical.TilesSha256);

                List<FrameRecord> commands = channel.Sent
                    .Where(f => f.MessageType == "Command" &&
                                f.RequestId == original.Command.RequestId.Value)
                    .ToList();
                Assert.Equal(2, commands.Count);
                Assert.All(commands, f => Assert.Equal(original.Command.ScopeId.Value.Value, f.ScopeId));
                Assert.Single(commands.Select(f => f.Sha256).Distinct());
            }
            finally
            {
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                await raw.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }

        private static async Task AssertRecoveredAndReconciledAsync(
            P41Harness harness,
            InMemoryOutboxStore outbox,
            OriginalCommit original,
            string caseId,
            string fault,
            string expectedOutcome,
            CancellationToken ct,
            Dictionary<string, object?> result)
        {
            await AssertRecoveredAndReconciledAsync(
                harness.Options,
                outbox,
                original,
                caseId,
                fault,
                expectedOutcome,
                ct,
                result,
                expectedHandCount: null,
                expectedRiverCount: null);
        }

        private static async Task AssertRecoveredAndReconciledAsync(
            LocalTableHostOptions sourceOptions,
            object outboxObject,
            OriginalCommit original,
            string caseId,
            string fault,
            string expectedOutcome,
            CancellationToken ct,
            Dictionary<string, object?> result,
            int? expectedHandCount,
            int? expectedRiverCount)
        {
            IOutboxStore outbox = outboxObject as IOutboxStore ??
                throw new ArgumentException("A real outbox store is required.", nameof(outboxObject));
            LocalTableHostOptions options = NewOptions(sourceOptions.SaveDirectory, "c04-recovery-" + caseId);
            var channel = new RecordingChannel(await LocalTableHost.CreateChannelAsync(options, ct));
            ITableChannel raw = channel.Inner;
            string channelTicket = LocalTableHost.IssueTicket(options);
            await raw.OpenAsync(new TableChannelConnectRequest(channelTicket, "c04-recovery", null), ct);
            TableSession? session = null;
            try
            {
                LocalTableRecoveryFacts baseline = await CaptureFactsAsync(
                    options.SaveDirectory,
                    ct);
                LocalTableRecoveryRequestFact? persisted = FindRequest(
                    baseline,
                    original.AccountId,
                    original.ScopeId,
                    original.Command.RequestId.Value);
                Assert.NotNull(persisted);
                Assert.Equal("APPLIED", persisted!.Status);
                Assert.True(
                    string.Equals(original.PayloadDigest, persisted.PayloadSha256, StringComparison.OrdinalIgnoreCase),
                    "persisted payload digest differs: expected=" + original.PayloadDigest + " actual=" + persisted.PayloadSha256);
                Assert.Equal(original.Receipt.Receipt.CommandVersion!.Value.Value.ToString(CultureInfo.InvariantCulture), persisted.CommandVersion);
                Assert.Equal(original.Receipt.Receipt.EventSeq!.Value.Value.ToString(CultureInfo.InvariantCulture), persisted.EventSeq);
                Assert.Equal(expectedOutcome, fault == "None" ? "RECOVERED" : "RECOVERED_FROM_BACKUP");

                session = new TableSession(channel, outbox);
                await LocalHostCommandTests.PumpUntilReadyAsync(
                    session,
                    LocalTableHost.IssueTicket(options),
                    ct);
                Assert.Single(session.PendingCommands);
                PendingCommandSnapshot pending = session.PendingCommands.Single();
                Assert.Equal(original.Command.RequestId.Value, pending.RequestId);
                Assert.Equal(original.ScopeId, pending.ScopeId);
                Assert.True(
                    string.Equals(original.CanonicalDigest, pending.DigestSha256, StringComparison.OrdinalIgnoreCase),
                    "pending canonical digest differs: expected=" + original.CanonicalDigest + " actual=" + pending.DigestSha256);

                var resolution = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<TableReceiptResolvedEventArgs> handler = (_, args) =>
                {
                    if (string.Equals(
                        args.Command.RequestId,
                        original.Command.RequestId.Value,
                        StringComparison.Ordinal))
                    {
                        resolution.TrySetResult(args);
                    }
                };
                session.ReceiptResolved += handler;
                try
                {
                    await session.ReconcilePendingAsync(ct);
                    using var reconcileCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    reconcileCts.CancelAfter(TimeSpan.FromSeconds(15));
                    for (int i = 0; i < 40 && session.PendingCommands.Count > 0; i++)
                    {
                        await session.PumpOnceAsync(reconcileCts.Token);
                    }

                    TableReceiptResolvedEventArgs resolved = await LocalHostCommandTests
                        .WithTimeoutAsync(resolution.Task, ct);
                    Assert.Empty(session.PendingCommands);
                    Assert.True(resolved.Resolution.FromReceiptLookup);
                    Assert.Equal(ContractReceiptStatus.Applied, resolved.Resolution.ReceiptStatus);
                    Assert.True(
                        string.Equals(original.CanonicalDigest, resolved.Resolution.CanonicalPayloadSha256, StringComparison.OrdinalIgnoreCase),
                        "resolution canonical digest differs: expected=" + original.CanonicalDigest +
                        " actual=" + resolved.Resolution.CanonicalPayloadSha256);
                    Assert.Equal(original.ScopeId, resolved.Command.ScopeId);
                }
                finally
                {
                    session.ReceiptResolved -= handler;
                }

                LocalTableRecoveryFacts after = await CaptureFactsAsync(
                    options.SaveDirectory,
                    ct);
                Assert.Equal(baseline.StateVersion, after.StateVersion);
                Assert.Equal(baseline.EventSeq, after.EventSeq);
                Assert.Equal(baseline.StateHash, after.StateHash);
                Assert.Equal(baseline.Ledger.EntriesSha256, after.Ledger.EntriesSha256);
                Assert.Equal(baseline.Ledger.BalancesSha256, after.Ledger.BalancesSha256);
                Assert.Equal(baseline.Physical.TilesSha256, after.Physical.TilesSha256);
                Assert.Equal(136, after.Physical.TileCount);
                Assert.Equal(136, after.Physical.UniqueCount);

                SaveShape shape = ReadSaveShape(File.ReadAllBytes(
                    Path.Combine(options.SaveDirectory, options.RoomId + ".save")));
                if (expectedHandCount.HasValue)
                {
                    Assert.Equal(expectedHandCount.Value, shape.HandCount);
                }

                if (expectedRiverCount.HasValue)
                {
                    Assert.Equal(expectedRiverCount.Value, shape.RiverCount);
                }

                List<FrameRecord> oldCommands = channel.Sent
                    .Where(f => f.MessageType == "Command" &&
                                f.RequestId == original.Command.RequestId.Value)
                    .ToList();
                Assert.Empty(oldCommands);
                List<FrameRecord> queries = channel.Sent
                    .Where(f => f.MessageType == "ReceiptQuery" &&
                                f.RequestId == original.Command.RequestId.Value &&
                                f.ScopeId == original.ScopeId)
                    .ToList();
                Assert.Single(queries);

                int sentAfterResolution = channel.Sent.Count;
                await session.ReconcilePendingAsync(ct);
                Assert.Equal(sentAfterResolution, channel.Sent.Count);
                LocalTableRecoveryFacts repeated = await CaptureFactsAsync(
                    options.SaveDirectory,
                    ct);
                Assert.Equal(after.StateVersion, repeated.StateVersion);
                Assert.Equal(after.EventSeq, repeated.EventSeq);
                Assert.Equal(after.Ledger.EntriesSha256, repeated.Ledger.EntriesSha256);
                Assert.Equal(after.Physical.TilesSha256, repeated.Physical.TilesSha256);

                result["actualOutcome"] = fault == "None" ? "RECOVERED" : "RECOVERED_FROM_BACKUP";
                result["binding"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["sessionId"] = session.Binding!.SessionId.Value,
                    ["scopeId"] = session.Binding.Scope.ScopeId.Value,
                    ["seat"] = session.Binding.SeatId,
                    ["role"] = session.Binding.Role.ToString(),
                };
                result["state"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["stateVersion"] = after.StateVersion,
                    ["eventSeq"] = after.EventSeq,
                    ["ledgerEntries"] = after.Ledger.EntryCount,
                    ["ledgerPoints"] = after.Ledger.Points,
                    ["ledgerSha256"] = after.Ledger.EntriesSha256,
                    ["physicalCount"] = after.Physical.TileCount,
                    ["physicalUnique"] = after.Physical.UniqueCount,
                    ["physicalSha256"] = after.Physical.TilesSha256,
                    ["handCount"] = shape.HandCount,
                    ["riverCount"] = shape.RiverCount,
                    ["discardEvents"] = shape.DiscardEvents,
                    ["queries"] = queries.Count,
                    ["framesSha256"] = string.Join(",", channel.Sent.Select(f => f.Sha256)),
                };
                Assert.Equal(1, shape.DiscardEvents);
                Assert.True(shape.RiverHasOriginalTile);
                Assert.Equal(original.PreHandCount - 1, shape.HandCount);
                Assert.Equal(original.PreRiverCount + 1, shape.RiverCount);
            }
            finally
            {
                if (session != null)
                {
                    await session.CloseAsync("TEST_DONE", CancellationToken.None);
                }

                await raw.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }

        private static async Task AssertFreshStartNoOldScopeAsync(
            P41Harness harness,
            InMemoryOutboxStore oldOutbox,
            string caseId,
            CancellationToken ct,
            Dictionary<string, object?> result)
        {
            LocalTableHostOptions options = NewOptions(harness.Options.SaveDirectory, "c04-fresh-" + caseId);
            var channel = new RecordingChannel(await LocalTableHost.CreateChannelAsync(options, ct));
            ITableChannel raw = channel.Inner;
            string ticket = LocalTableHost.IssueTicket(options);
            await raw.OpenAsync(new TableChannelConnectRequest(ticket, "c04-fresh", null), ct);
            TableSession? session = null;
            try
            {
                LocalTableRecoveryFacts facts = await CaptureFactsAsync(
                    options.SaveDirectory,
                    ct);
                Assert.Equal("0", facts.StateVersion);
                Assert.Equal("0", facts.EventSeq);
                Assert.Empty(facts.Requests);
                Assert.Equal(136, facts.Physical.TileCount);
                Assert.Equal(136, facts.Physical.UniqueCount);

                // The old outbox is deliberately NOT injected into the new room. A
                // normal new-room session uses a fresh outbox; no old-scope request
                // is replayed or queried.
                var freshOutbox = new InMemoryOutboxStore();
                session = new TableSession(channel, freshOutbox);
                await LocalHostCommandTests.PumpUntilReadyAsync(
                    session,
                    LocalTableHost.IssueTicket(options),
                    ct);
                Assert.Empty(session.PendingCommands);
                Assert.Equal(TableConnectionState.Ready, session.State);
                Assert.Equal("0", session.PlayerSnapshot!.StateVersion.ToString(CultureInfo.InvariantCulture));

                IReadOnlyList<OutboxEntry> oldUnresolved = await oldOutbox.LoadUnresolvedAsync(ct);
                Assert.Single(oldUnresolved);

                Assert.DoesNotContain(
                    channel.Sent,
                    f => f.MessageType == "Command" ||
                         f.MessageType == "ReceiptQuery" ||
                         f.RequestId.Length > 0);
                result["actualOutcome"] = "FRESH_START";
                result["state"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["stateVersion"] = facts.StateVersion,
                    ["eventSeq"] = facts.EventSeq,
                    ["oldReceiptCount"] = facts.Requests.Count,
                    ["oldOutboxUnresolved"] = oldUnresolved.Count,
                    ["physicalCount"] = facts.Physical.TileCount,
                    ["physicalUnique"] = facts.Physical.UniqueCount,
                    ["framesSha256"] = string.Join(",", channel.Sent.Select(f => f.Sha256)),
                };
            }
            finally
            {
                if (session != null)
                {
                    await session.CloseAsync("TEST_DONE", CancellationToken.None);
                }

                await raw.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }

        private static async Task AssertRejectedZeroSendAsync(
            P41Harness harness,
            InMemoryOutboxStore oldOutbox,
            string caseId,
            string fault,
            SaveFaultApplied applied,
            CancellationToken ct,
            Dictionary<string, object?> result)
        {
            string primarySha = Sha256File(harness.SavePath);
            string backupSha = Sha256File(harness.BackupPath);
            LocalTableHostOptions options = NewOptions(harness.Options.SaveDirectory, "c04-rejected-" + caseId);
            var channel = new RecordingChannel(await LocalTableHost.CreateChannelAsync(options, ct));
            ITableChannel raw = channel.Inner;
            string ticket = LocalTableHost.IssueTicket(options);
            try
            {
                LocalTableException error = await Assert.ThrowsAsync<LocalTableException>(
                    async () => await raw.OpenAsync(
                        new TableChannelConnectRequest(ticket, "c04-rejected", null),
                        ct));
                Assert.Equal(LocalTableErrorKind.CorruptState, error.ErrorKind);
                Assert.Empty(channel.Sent);
                Assert.False(raw.IsOpen);
                Assert.Single(await oldOutbox.LoadUnresolvedAsync(ct));
                Assert.Equal(primarySha, Sha256File(harness.SavePath));
                Assert.Equal(backupSha, Sha256File(harness.BackupPath));

                if (fault == "IncompatiblePrimary")
                {
                    // The backup is valid on purpose: the frozen policy must refuse the
                    // newer primary and must NOT silently roll back to the backup.
                    LocalSave backup = LocalSaveCodec.DecodeEnvelope(File.ReadAllBytes(harness.BackupPath));
                    Assert.Equal(1, backup.FileFormatVersion);
                }
                else
                {
                    Assert.Throws<InvalidDataException>(
                        () => LocalSaveCodec.DecodeEnvelope(File.ReadAllBytes(harness.SavePath)));
                    Assert.Throws<InvalidDataException>(
                        () => LocalSaveCodec.DecodeEnvelope(File.ReadAllBytes(harness.BackupPath)));
                }

                result["actualOutcome"] = "REJECTED";
                result["rejection"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["errorKind"] = error.ErrorKind.ToString(),
                    ["detail"] = error.Message,
                    ["sentFrames"] = channel.Sent.Count,
                    ["primarySha256"] = primarySha,
                    ["backupSha256"] = backupSha,
                    ["oldOutboxUnresolved"] = 1,
                };
                Assert.Equal(applied.PrimaryAfterSha256, primarySha);
                Assert.Equal(applied.BackupAfterSha256, backupSha);
            }
            finally
            {
                try
                {
                    await raw.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
                }
                catch
                {
                    // A failed open already normalized the channel to permanently closed.
                }
            }
        }

        private static async Task<OriginalCommit> CommitOriginalDiscardAsync(
            P41Harness harness,
            InMemoryOutboxStore outbox,
            string caseId,
            CancellationToken ct,
            bool keepOpen = false)
        {
            LocalTableHostOptions options = NewOptions(harness.Options.SaveDirectory, "c04-original-" + caseId);
            var channel = new RecordingChannel(await LocalTableHost.CreateChannelAsync(options, ct));
            ITableChannel raw = channel.Inner;
            string channelTicket = LocalTableHost.IssueTicket(options);
            await raw.OpenAsync(new TableChannelConnectRequest(channelTicket, "c04-original", null), ct);
            var session = new TableSession(channel, outbox);
            try
            {
                await LocalHostCommandTests.PumpUntilReadyAsync(
                    session,
                    LocalTableHost.IssueTicket(options),
                    ct);
                PlayerSnapshot pre = session.PlayerSnapshot!;
                Assert.NotEmpty(pre.PrivateView.ClosedTiles);
                uint tile = pre.PrivateView.ClosedTiles[0];
                LocalTableRecoveryFacts preFacts = await CaptureFactsAsync(
                    options.SaveDirectory,
                    ct);
                byte[] preSave = File.ReadAllBytes(Path.Combine(
                    options.SaveDirectory,
                    options.RoomId + ".save"));
                SaveShape preShape = ReadSaveShape(preSave);

                BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                    pre.RoundId,
                    "local-direct",
                    0UL,
                    ProtocolActionType.DiscardTile,
                    tileIds: new[] { tile });
                var command = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    payload,
                    new RequestId("c04-" + caseId + "-discard"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(pre.StateVersion),
                    new RoundId(pre.RoundId),
                    decisionId: null,
                    controlEpoch: new ControlEpoch(0));
                CommandSubmission submission = await session.SubmitAsync(command, ct);
                Assert.True(submission.Sent);
                Assert.Single(session.PendingCommands);
                Assert.Equal(command.RequestId.Value, session.PendingCommands[0].RequestId);

                LocalTableRecoveryFacts postFacts = await CaptureFactsAsync(
                    options.SaveDirectory,
                    ct);
                byte[] saveBytes = File.ReadAllBytes(Path.Combine(
                    options.SaveDirectory,
                    options.RoomId + ".save"));
                LocalSave save = LocalSaveCodec.DecodeEnvelope(saveBytes);
                TableDurableReceipt receipt = save.Receipts.Values.Single(r =>
                    r.AccountId == LocalAccount &&
                    r.ScopeId == command.ScopeId!.Value &&
                    r.RequestId == command.RequestId);
                Assert.Equal(ContractReceiptStatus.Applied, receipt.Receipt.Status);
                Assert.Equal("SUBMIT_ACTION", receipt.Api);
                Assert.Equal(TablePayloadSha256.Compute(payload.CopyBytes()).ToHex(), receipt.PayloadSha256.ToHex());
                Assert.Equal(preFacts.StateVersion, receipt.Receipt.EvaluatedStateVersion.Value.ToString(CultureInfo.InvariantCulture));
                Assert.Equal(command.RequestId, submission.Command.Key.RequestId);

                return new OriginalCommit(
                    options,
                    channel,
                    raw,
                    session,
                    command,
                    payload,
                    command.ScopeId!.Value.Value,
                    LocalAccount,
                    receipt,
                    preFacts,
                    postFacts,
                    pre.StateVersion.ToString(CultureInfo.InvariantCulture),
                    Array.Empty<string>(),
                    0,
                    0,
                    saveBytes,
                    Array.Empty<byte>(),
                    channel.Sent.ToList(),
                    preShape,
                    tile,
                    preShape.HandCount,
                    preShape.RiverCount);
            }
            finally
            {
                if (!keepOpen)
                {
                    await session.CloseAsync("C04_ORIGINAL_DONE", CancellationToken.None);
                    await raw.CloseAsync(new TableChannelCloseReason("C04_ORIGINAL_DONE", true), CancellationToken.None);
                }
            }
        }

        private static void AssertOriginalCommit(string caseId, OriginalCommit original)
        {
            Assert.Equal(ContractReceiptStatus.Applied, original.Receipt.Receipt.Status);
            Assert.Equal("SUBMIT_ACTION", original.Receipt.Api);
            Assert.True(
                string.Equals(original.PayloadDigest, original.Receipt.PayloadSha256.ToHex(), StringComparison.OrdinalIgnoreCase),
                "receipt payload digest differs: expected=" + original.PayloadDigest +
                " actual=" + original.Receipt.PayloadSha256.ToHex());
            Assert.Equal(
                original.PreFacts.StateVersion,
                original.Receipt.Receipt.EvaluatedStateVersion.Value.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(
                (original.Receipt.Receipt.EvaluatedStateVersion.Value + 1UL).ToString(CultureInfo.InvariantCulture),
                original.Receipt.Receipt.CommandVersion!.Value.Value.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(
                original.PostFacts.StateVersion,
                original.Receipt.Receipt.CommandVersion.Value.Value.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(
                original.PostFacts.EventSeq,
                original.Receipt.Receipt.EventSeq!.Value.Value.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(original.PreFacts.Ledger.EntryCount, original.PostFacts.Ledger.EntryCount);
            Assert.Equal(original.PreFacts.Ledger.Points, original.PostFacts.Ledger.Points);
            Assert.Equal(original.PreFacts.Ledger.EntriesSha256, original.PostFacts.Ledger.EntriesSha256);
            Assert.Equal(original.PreFacts.Ledger.BalancesSha256, original.PostFacts.Ledger.BalancesSha256);
            Assert.Equal(136, original.PostFacts.Physical.TileCount);
            Assert.Equal(136, original.PostFacts.Physical.UniqueCount);

            LocalSave save = LocalSaveCodec.DecodeEnvelope(original.SaveBytes);
            List<TableEventRecord> discards = save.EventJournals
                .Where(e => e.Type == RoundEventType.TileDiscarded && e.Seat == 0)
                .ToList();
            Assert.Single(discards);
            Assert.Equal(original.Tile, (uint)discards[0].Tile!.Value.Value);
            Assert.Equal(
                original.Command.RequestId,
                save.CommandJournals.Single(j => j.RequestId == original.Command.RequestId).RequestId);
            Assert.Contains(
                original.Channel.Sent,
                f => f.MessageType == "Command" &&
                     f.RequestId == original.Command.RequestId.Value &&
                     f.ScopeId == original.ScopeId);
            Assert.DoesNotContain(
                original.Channel.Sent,
                f => f.MessageType == "ReceiptQuery");
            Assert.False(string.IsNullOrWhiteSpace(caseId));
        }

        private static LocalTableRecoveryRequestFact? FindRequest(
            LocalTableRecoveryFacts facts,
            string accountId,
            string scopeId,
            string requestId)
        {
            return facts.Requests.FirstOrDefault(r =>
                string.Equals(r.AccountId, accountId, StringComparison.Ordinal) &&
                string.Equals(r.ScopeId, scopeId, StringComparison.Ordinal) &&
                string.Equals(r.RequestId, requestId, StringComparison.Ordinal));
        }

        private static SaveShape ReadSaveShape(byte[] saveBytes)
        {
            LocalSave save = LocalSaveCodec.DecodeEnvelope(saveBytes);
            byte[] statePayload = LatestStatePayload(save);
            RoundStateMachine round = RoundRecovery.Restore(RoundRecoveryCodec.Decode(statePayload));
            IReadOnlyList<TileId> hand = round.Tiles.TilesInZone(TileZone.Hand, 0);
            IReadOnlyList<TileId> river = round.Tiles.TilesInZone(TileZone.Discard, 0);
            int discarded = save.EventJournals.Count(e =>
                e.Type == RoundEventType.TileDiscarded && e.Seat == 0);
            return new SaveShape(
                hand.Count,
                river.Count,
                discarded,
                river.Count > 0,
                round.Tiles.AsPlacements().Count,
                round.Tiles.AsPlacements().Select(p => p.Tile.Value).Distinct().Count());
        }

        private static byte[] LatestStatePayload(LocalSave save)
        {
            TableCheckpoint checkpoint = LocalSaveCodec.DecodeCheckpoint(save.BaseCheckpointBlob);
            byte[] statePayload = checkpoint.StatePayload;
            for (int i = 0; i < save.Operations.Count; i++)
            {
                LocalDurableOperation operation = save.Operations[i];
                if (operation.ExpectedStateVersion < checkpoint.StateVersion.Value)
                {
                    continue;
                }

                if (operation.Kind == LocalOperationKind.SaveCheckpoint ||
                    (operation.Kind == LocalOperationKind.CommitControl && operation.CheckpointBlob != null))
                {
                    checkpoint = LocalSaveCodec.DecodeCheckpoint(operation.CheckpointBlob!);
                    statePayload = checkpoint.StatePayload;
                    continue;
                }

                if (operation.Kind == LocalOperationKind.Commit &&
                    operation.NextStatePayload != null &&
                    operation.NextStatePayload.Length > 0)
                {
                    statePayload = operation.NextStatePayload;
                }
            }

            return statePayload;
        }

        private static LocalTableRecoveryFacts BuildForceSummaryFacts(
            byte[] saveBytes,
            LocalSave save,
            string roundId,
            string stateVersion)
        {
            SaveShape shape = ReadSaveShape(saveBytes);
            var ledger = new LocalTableRecoveryLedgerFact(
                0,
                0,
                new[] { 0, 0, 0, 0 },
                0,
                string.Empty,
                string.Empty,
                0,
                0,
                "FORCE_SUMMARY");
            var physical = new LocalTableRecoveryPhysicalFact(
                shape.PhysicalCount,
                shape.PhysicalUniqueCount,
                0,
                0,
                string.Empty);
            return new LocalTableRecoveryFacts(
                Sha256(saveBytes),
                saveBytes.Length,
                save.RoomId,
                roundId,
                save.RulesetVersion,
                stateVersion,
                save.CurrentState.LastEventSeq.Value.ToString(CultureInfo.InvariantCulture),
                save.CurrentState.StateFingerprint.ToString(CultureInfo.InvariantCulture),
                save.CurrentState.StateHash.ToHex(),
                save.CurrentState.ControlVersion.ToString(CultureInfo.InvariantCulture),
                new long[4],
                "0",
                "0",
                string.Empty,
                Array.Empty<LocalTableRecoveryRequestFact>(),
                Array.Empty<LocalTableRecoveryControlFact>(),
                ledger,
                physical);
        }

        private static async Task<LocalTableRecoveryFacts> CaptureFactsAsync(
            string saveDirectory,
            CancellationToken cancellationToken)
        {
            LocalTableRecoveryCapture capture = await LocalTableHost.CaptureRecoveryAsync(
                saveDirectory,
                cancellationToken);
            return capture.Facts;
        }

        private static LocalTableHostOptions NewOptions(string saveDirectory, string owner)
        {
            return new LocalTableHostOptions
            {
                SaveDirectory = saveDirectory,
                RoomId = RoomId,
                RulesetVersion = 1,
                OwnerInstanceId = owner,
                LeaseDuration = TimeSpan.FromMinutes(5),
                AutoPlay = false,
            };
        }

        private static SaveFaultApplied ApplySaveFault(P41Harness harness, string fault)
        {
            string primary = harness.SavePath;
            string backup = harness.BackupPath;
            switch (fault)
            {
                case "None":
                    break;
                case "TruncatePrimary":
                {
                    byte[] bytes = File.ReadAllBytes(primary);
                    int keep = Math.Max(1, bytes.Length / 2);
                    File.WriteAllBytes(primary, bytes.Take(keep).ToArray());
                    break;
                }
                case "BadChecksumPrimary":
                {
                    byte[] bytes = File.ReadAllBytes(primary);
                    Assert.True(bytes.Length > 44);
                    bytes[44] ^= 0x5A;
                    File.WriteAllBytes(primary, bytes);
                    break;
                }
                case "IncompatiblePrimary":
                {
                    byte[] bytes = File.ReadAllBytes(primary);
                    Assert.True(bytes.Length >= 44);
                    bytes[4] = 2;
                    bytes[5] = 0;
                    bytes[6] = 0;
                    bytes[7] = 0;
                    File.WriteAllBytes(primary, bytes);
                    break;
                }
                case "MissingPrimaryWithBackup":
                    File.Delete(primary);
                    break;
                case "BothMissing":
                    File.Delete(primary);
                    File.Delete(backup);
                    break;
                case "BothBad":
                {
                    byte[] bytes = File.ReadAllBytes(primary);
                    File.WriteAllBytes(primary, bytes.Take(Math.Max(1, bytes.Length / 3)).ToArray());
                    byte[] backupBytes = File.ReadAllBytes(backup);
                    Assert.True(backupBytes.Length > 44);
                    backupBytes[44] ^= 0xA5;
                    File.WriteAllBytes(backup, backupBytes);
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(fault), fault, "Unsupported save fault.");
            }

            return new SaveFaultApplied(
                fault,
                File.Exists(primary) ? Sha256File(primary) : string.Empty,
                File.Exists(backup) ? Sha256File(backup) : string.Empty,
                File.Exists(primary),
                File.Exists(backup));
        }

        private static string NewEvidenceRoot(string caseId)
        {
            string root = Path.Combine(
                P41Harness.FindRepositoryRoot(),
                "build",
                "c04-recovery-request-tests",
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" +
                caseId + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            return root;
        }

        private static void WriteEvidence(
            string root,
            string caseId,
            Dictionary<string, object?> result)
        {
            string path = Path.Combine(root, "summary-" + caseId + ".json");
            string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }

        private static string Sha256File(string path)
        {
            using FileStream stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return ToHex(sha.ComputeHash(stream));
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                builder.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private sealed class NullChannel : ITableChannel
        {
            public bool IsOpen => false;

            public Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken) =>
                Task.CompletedTask;
        }

        private enum LookupFault
        {
            None = 0,
            UnknownOnce = 1,
        }

        private sealed class RecordingChannel : ITableChannel
        {
            private readonly object _gate = new object();
            private readonly List<FrameRecord> _sent = new List<FrameRecord>();
            private readonly List<FrameRecord> _received = new List<FrameRecord>();
            private int _unknownInjected;

            public RecordingChannel(ITableChannel inner)
            {
                Inner = inner;
            }

            public ITableChannel Inner { get; }

            public LookupFault LookupFault { get; set; }

            public string? DropReceiptRequestId { get; set; }

            public bool IsOpen => Inner.IsOpen;

            public Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken) =>
                Inner.OpenAsync(request, cancellationToken);

            public async Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken)
            {
                Record(_sent, wireEnvelope, "OUT");
                await Inner.SendAsync(wireEnvelope, cancellationToken).ConfigureAwait(false);
            }

            public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
            {
                while (true)
                {
                    ReadOnlyMemory<byte> frame = await Inner.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                    WireEnvelope envelope = WireEnvelope.Parser.ParseFrom(frame.ToArray());
                    if (!string.IsNullOrEmpty(DropReceiptRequestId) &&
                        envelope.BodyCase == WireEnvelope.BodyOneofCase.Receipt &&
                        string.Equals(envelope.RequestId, DropReceiptRequestId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (LookupFault == LookupFault.UnknownOnce &&
                        envelope.BodyCase == WireEnvelope.BodyOneofCase.ReceiptLookup &&
                        envelope.ReceiptLookup != null &&
                        Interlocked.CompareExchange(ref _unknownInjected, 1, 0) == 0)
                    {
                        envelope.ReceiptLookup.Status = LookupStatus.Unknown;
                        envelope.ReceiptLookup.Receipt = null;
                        envelope.ReceiptLookup.CanonicalPayloadSha256 = string.Empty;
                        frame = WireCodec.Encode(envelope);
                    }

                    Record(_received, frame, "IN");
                    return frame;
                }
            }

            public Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken) =>
                Inner.CloseAsync(reason, cancellationToken);

            public IReadOnlyList<FrameRecord> Sent
            {
                get
                {
                    lock (_gate)
                    {
                        return _sent.ToArray();
                    }
                }
            }

            public IReadOnlyList<FrameRecord> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private void Record(List<FrameRecord> list, ReadOnlyMemory<byte> frame, string direction)
            {
                byte[] bytes = frame.ToArray();
                string type = "UNPARSEABLE";
                string requestId = string.Empty;
                string scopeId = string.Empty;
                string status = string.Empty;
                string correlationId = string.Empty;
                try
                {
                    WireEnvelope envelope = WireEnvelope.Parser.ParseFrom(bytes);
                    type = envelope.BodyCase.ToString();
                    requestId = envelope.RequestId ?? string.Empty;
                    scopeId = envelope.Scope?.ScopeId ?? string.Empty;
                    status = envelope.Receipt?.Status.ToString() ??
                             envelope.ReceiptLookup?.Status.ToString() ??
                             string.Empty;
                    correlationId = envelope.CorrelationId ?? string.Empty;
                    if (envelope.BodyCase == WireEnvelope.BodyOneofCase.ReceiptQuery &&
                        envelope.ReceiptQuery != null)
                    {
                        requestId = envelope.ReceiptQuery.RequestId;
                        scopeId = envelope.ReceiptQuery.RequestScopeId;
                    }
                }
                catch (Google.Protobuf.InvalidProtocolBufferException)
                {
                    // Keep the raw bytes and mark the frame unparseable; the test will fail
                    // on the expected type rather than hiding a malformed transport frame.
                }

                lock (_gate)
                {
                    list.Add(new FrameRecord(
                        direction,
                        type,
                        requestId,
                        scopeId,
                        status,
                        correlationId,
                        Sha256(bytes),
                        bytes.Length));
                }
            }
        }

        private sealed class FrameRecord
        {
            public FrameRecord(
                string direction,
                string messageType,
                string requestId,
                string scopeId,
                string status,
                string correlationId,
                string sha256,
                int bytes)
            {
                Direction = direction;
                MessageType = messageType;
                RequestId = requestId;
                ScopeId = scopeId;
                Status = status;
                CorrelationId = correlationId;
                Sha256 = sha256;
                Bytes = bytes;
            }

            public string Direction { get; }
            public string MessageType { get; }
            public string RequestId { get; }
            public string ScopeId { get; }
            public string Status { get; }
            public string CorrelationId { get; }
            public string Sha256 { get; }
            public int Bytes { get; }
        }

        private sealed class OriginalCommit
        {
            public OriginalCommit(
                LocalTableHostOptions options,
                RecordingChannel channel,
                ITableChannel rawChannel,
                TableSession session,
                ProtocolCommand command,
                BusinessPayload payload,
                string scopeId,
                string accountId,
                TableDurableReceipt receipt,
                LocalTableRecoveryFacts preFacts,
                LocalTableRecoveryFacts postFacts,
                string preStateVersion,
                string[] marker,
                int pid,
                int nativeExit,
                byte[] saveBytes,
                byte[] backupBytes,
                List<FrameRecord> frames,
                SaveShape? preShape = null,
                uint tile = 0,
                int preHandCount = 0,
                int preRiverCount = 0,
                string payloadDigest = "")
            {
                Options = options;
                Channel = channel;
                RawChannel = rawChannel;
                Session = session;
                Command = command;
                Payload = payload;
                ScopeId = scopeId;
                AccountId = accountId;
                Receipt = receipt;
                PreFacts = preFacts;
                PostFacts = postFacts;
                PreStateVersion = preStateVersion;
                Marker = marker;
                Pid = pid;
                NativeExit = nativeExit;
                SaveBytes = saveBytes;
                BackupBytes = backupBytes;
                Frames = frames;
                PreShape = preShape;
                Tile = tile;
                PreHandCount = preHandCount;
                PreRiverCount = preRiverCount;
                PayloadDigest = string.IsNullOrEmpty(payloadDigest)
                    ? TablePayloadSha256.Compute(payload.CopyBytes()).ToHex()
                    : payloadDigest;
                CanonicalDigest = CanonicalPayloadDigest.Compute(payload).ToHex();
            }

            public LocalTableHostOptions Options { get; }
            public RecordingChannel Channel { get; }
            public ITableChannel RawChannel { get; }
            public TableSession Session { get; }
            public ProtocolCommand Command { get; }
            public BusinessPayload Payload { get; }
            public string ScopeId { get; }
            public string AccountId { get; }
            public TableDurableReceipt Receipt { get; }
            public LocalTableRecoveryFacts PreFacts { get; }
            public LocalTableRecoveryFacts PostFacts { get; }
            public string PreStateVersion { get; }
            public string[] Marker { get; }
            public int Pid { get; }
            public int NativeExit { get; }
            public byte[] SaveBytes { get; }
            public byte[] BackupBytes { get; }
            public List<FrameRecord> Frames { get; }
            public SaveShape? PreShape { get; }
            public uint Tile { get; }
            public int PreHandCount { get; }
            public int PreRiverCount { get; }
            public string PayloadDigest { get; }
            public string CanonicalDigest { get; }

            public Dictionary<string, object?> Summary()
            {
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["requestId"] = Command.RequestId.Value,
                    ["scopeId"] = ScopeId,
                    ["accountId"] = AccountId,
                    ["api"] = Receipt.Api,
                    ["payloadSha256"] = PayloadDigest,
                    ["canonicalDigest"] = CanonicalDigest,
                    ["receiptStatus"] = Receipt.Receipt.Status.ToString(),
                    ["evaluatedStateVersion"] = Receipt.Receipt.EvaluatedStateVersion.Value,
                    ["commandVersion"] = Receipt.Receipt.CommandVersion!.Value.Value,
                    ["eventSeq"] = Receipt.Receipt.EventSeq!.Value.Value,
                    ["preStateVersion"] = PreFacts.StateVersion,
                    ["postStateVersion"] = PostFacts.StateVersion,
                    ["preEventSeq"] = PreFacts.EventSeq,
                    ["postEventSeq"] = PostFacts.EventSeq,
                    ["preLedgerEntries"] = PreFacts.Ledger.EntryCount,
                    ["postLedgerEntries"] = PostFacts.Ledger.EntryCount,
                    ["preLedgerSha256"] = PreFacts.Ledger.EntriesSha256,
                    ["postLedgerSha256"] = PostFacts.Ledger.EntriesSha256,
                    ["physicalCount"] = PostFacts.Physical.TileCount,
                    ["physicalUnique"] = PostFacts.Physical.UniqueCount,
                    ["saveSha256"] = Sha256(SaveBytes),
                    ["preHandCount"] = PreHandCount,
                    ["preRiverCount"] = PreRiverCount,
                    ["tileId"] = Tile,
                    ["sentCommandSha256"] = Frames
                        .FirstOrDefault(f => f.MessageType == "Command" &&
                                             f.RequestId == Command.RequestId.Value)
                        ?.Sha256 ?? string.Empty,
                };
            }
        }

        private sealed class SaveFaultApplied
        {
            public SaveFaultApplied(
                string fault,
                string primaryAfterSha256,
                string backupAfterSha256,
                bool primaryExists,
                bool backupExists)
            {
                Fault = fault;
                PrimaryAfterSha256 = primaryAfterSha256;
                BackupAfterSha256 = backupAfterSha256;
                PrimaryExists = primaryExists;
                BackupExists = backupExists;
            }

            public string Fault { get; }
            public string PrimaryAfterSha256 { get; }
            public string BackupAfterSha256 { get; }
            public bool PrimaryExists { get; }
            public bool BackupExists { get; }

            public Dictionary<string, object?> ToSummary()
            {
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["fault"] = Fault,
                    ["primaryExists"] = PrimaryExists,
                    ["backupExists"] = BackupExists,
                    ["primaryAfterSha256"] = PrimaryAfterSha256,
                    ["backupAfterSha256"] = BackupAfterSha256,
                };
            }
        }

        private sealed class SaveShape
        {
            public SaveShape(
                int handCount,
                int riverCount,
                int discardEvents,
                bool riverHasOriginalTile,
                int physicalCount,
                int physicalUniqueCount)
            {
                HandCount = handCount;
                RiverCount = riverCount;
                DiscardEvents = discardEvents;
                RiverHasOriginalTile = riverHasOriginalTile;
                PhysicalCount = physicalCount;
                PhysicalUniqueCount = physicalUniqueCount;
            }

            public int HandCount { get; }
            public int RiverCount { get; }
            public int DiscardEvents { get; }
            public bool RiverHasOriginalTile { get; }
            public int PhysicalCount { get; }
            public int PhysicalUniqueCount { get; }
        }

        private static string Sha256(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return ToHex(sha.ComputeHash(bytes));
        }
    }
}
