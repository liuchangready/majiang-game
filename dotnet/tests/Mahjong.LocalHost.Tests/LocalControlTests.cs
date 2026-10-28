using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.TableRuntime;
using Xunit;
using Xunit.Sdk;
using ContractReceiptStatus = Mahjong.Contracts.ReceiptStatus;
using PlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;
using WireCodec = Mahjong.Contracts.Wire.WireCodec;
using WireCommand = Mahjong.Contracts.Wire.Command;
using WireCommandType = Mahjong.Contracts.Wire.CommandType;
using WireEnvelope = Mahjong.Contracts.Wire.Envelope;
using WireHello = Mahjong.Contracts.Wire.Hello;
using WireMessageType = Mahjong.Contracts.Wire.MessageType;
using WireReceipt = Mahjong.Contracts.Wire.Receipt;
using WireReceiptStatus = Mahjong.Contracts.Wire.ReceiptStatus;
using WireScope = Mahjong.Contracts.Wire.RequestScope;

namespace Mahjong.LocalHost.Tests
{
    public sealed class LocalControlTests
    {
        private const string ControlReason = "LOCAL_CONTROL";

        [Fact]
        public async Task ClientClaimReleaseProducesDurableRecordedResults()
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            CancellationToken ct = TestContext.Current.CancellationToken;
            ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, ct);
            string channelTicket = LocalTableHost.IssueTicket(harness.Options);
            await channel.OpenAsync(new TableChannelConnectRequest(channelTicket, "p41-test", null), ct);
            string outboxPath = System.IO.Path.Combine(harness.Root, "control-outbox");
            var session = new TableSession(channel, new FileOutboxStore(outboxPath));
            string sessionTicket = LocalTableHost.IssueTicket(harness.Options);
            await session.OpenAsync(new TableSessionOpenRequest(sessionTicket, "p41-test"), ct);
            using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pumpCts.CancelAfter(TimeSpan.FromSeconds(10));
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

            RequestScopeId controlScope = default;
            RequestId lastControlRequest = default;
            string lastControlDigest = string.Empty;
            try
            {
                await WaitReadyAsync(session, pumpCts.Token);
                PlayerSnapshot baseline = session.PlayerSnapshot!;
                ulong epoch = 0;
                for (int i = 0; i < 3; i++)
                {
                    bool claim = i != 0;
                    BusinessPayload payload = claim
                        ? CommandPayloadFactory.ClaimControl(baseline.RoundId, epoch)
                        : CommandPayloadFactory.ReleaseControl(baseline.RoundId, epoch);
                    var controlRequest = new RequestId("control-" + i + "-" + Guid.NewGuid().ToString("N"));
                    controlScope = session.Binding!.Scope.ScopeId;
                    lastControlDigest = CanonicalPayloadDigest.Compute(payload).ToHex();
                    lastControlRequest = controlRequest;
                    var command = new ProtocolCommand(
                        claim ? CommandKind.ClaimControl : CommandKind.ReleaseControl,
                        payload,
                        controlRequest,
                        controlScope,
                        new StateVersion(baseline.StateVersion),
                        new RoundId(baseline.RoundId),
                        decisionId: null,
                        controlEpoch: new ControlEpoch(epoch));
                    var resolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    EventHandler<TableReceiptResolvedEventArgs> handler = (_, args) => resolved.TrySetResult(args);
                    session.ReceiptResolved += handler;
                    try
                    {
                        CommandSubmission submission = await session.SubmitAsync(command, pumpCts.Token);
                        Assert.True(submission.Sent);
                        TableReceiptResolvedEventArgs receipt = await LocalHostCommandTests.WithTimeoutAsync(
                            resolved.Task,
                            pumpCts.Token);
                        Assert.Equal(OutboxDisposition.Recorded, receipt.Resolution.Disposition);
                        Assert.Equal(ControlReason, receipt.Resolution.ReasonCode);
                        Assert.Equal(64, receipt.Resolution.CanonicalPayloadSha256.Length);
                        Assert.Equal(claim ? CommandKind.ClaimControl : CommandKind.ReleaseControl, receipt.Command.Kind);
                    }
                    finally
                    {
                        session.ReceiptResolved -= handler;
                    }

                    epoch++;
                }

                Assert.Empty(session.PendingCommands);
            }
            finally
            {
                pumpCts.Cancel();
                await pump;
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
            using (LocalTableStore store = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, null, ct))
            {
                LocalControlRecord? record = store.GetControlResult(
                    "local-" + P41Harness.RoomId + "-seat0",
                    controlScope,
                    lastControlRequest);
                Assert.NotNull(record);
                Assert.Equal(lastControlDigest, record!.Digest);
                Assert.Equal("ClaimControl", record.Kind);
            }
        }

        [Fact]
        public async Task EmptyControlDigestDoesNotResolveTheOutbox()
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            CancellationToken ct = TestContext.Current.CancellationToken;
            ITableChannel raw = await LocalTableHost.CreateChannelAsync(harness.Options, ct);
            string channelTicket = LocalTableHost.IssueTicket(harness.Options);
            await raw.OpenAsync(new TableChannelConnectRequest(channelTicket, "p41-test", null), ct);
            var channel = new EmptyControlDigestChannel(raw);
            var session = new TableSession(channel, new InMemoryOutboxStore());
            string sessionTicket = LocalTableHost.IssueTicket(harness.Options);
            await session.OpenAsync(new TableSessionOpenRequest(sessionTicket, "p41-test"), ct);
            try
            {
                for (int i = 0; i < 50 && session.State != TableConnectionState.Ready; i++)
                {
                    await session.PumpOnceAsync(ct);
                }

                Assert.Equal(TableConnectionState.Ready, session.State);
                PlayerSnapshot baseline = session.PlayerSnapshot!;
                BusinessPayload payload = CommandPayloadFactory.ReleaseControl(baseline.RoundId, 0);
                var command = new ProtocolCommand(
                    CommandKind.ReleaseControl,
                    payload,
                    new RequestId("empty-digest-control"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(baseline.StateVersion),
                    new RoundId(baseline.RoundId),
                    decisionId: null,
                    controlEpoch: new ControlEpoch(0));
                CommandSubmission submission = await session.SubmitAsync(command, ct);
                Assert.True(submission.Sent);
                for (int i = 0; i < 200 && channel.Mutations == 0 && session.State != TableConnectionState.Faulted; i++)
                {
                    await session.PumpOnceAsync(ct);
                }

                Assert.True(channel.Mutations > 0, "empty control digest receipt was not consumed");
                for (int i = 0; i < 5 && session.State != TableConnectionState.Faulted; i++)
                {
                    try
                    {
                        await session.PumpOnceAsync(ct);
                    }
                    catch (TableChannelClosedException)
                    {
                        break;
                    }
                }

                Assert.True(
                    session.PendingCommands.Count > 0 || session.State == TableConnectionState.Faulted,
                    "empty control digest resolved the outbox");
            }
            finally
            {
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                await raw.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }

        [Fact]
        public async Task DroppedControlReceiptIsReconciledFromPersistentOutboxOnReconnect()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            string outboxPath = System.IO.Path.Combine(harness.Root, "control-outbox-reconnect");
            var outbox = new FileOutboxStore(outboxPath);
            DropFirstControlReceiptChannel? dropper = null;
            await using LiveSession live = await LiveSession.OpenAsync(
                harness,
                outbox,
                ct,
                inner =>
                {
                    dropper = new DropFirstControlReceiptChannel(inner, 1);
                    return dropper;
                });

            const string requestId = "reconnect-release";
            RequestScopeId scope = live.Scope;
            CommandSubmission submission = await live.SubmitControlAsync(
                CommandKind.ReleaseControl,
                0,
                requestId,
                decisionId: null,
                ct);
            Assert.True(submission.Sent);
            string digest = submission.Command.DigestSha256;

            await WaitUntilAsync(
                () => dropper != null && dropper.DroppedCount == 1,
                "the first control receipt to be dropped",
                ct);
            await WaitUntilAsync(
                () => live.Runtime.StoreForTest.GetControlResult(SeatAccount, scope, new RequestId(requestId)) != null,
                "the dropped-receipt control result to commit",
                ct);
            Assert.NotNull(dropper);
            Assert.Equal(1, dropper!.DroppedCount);
            Assert.Single(live.Session.PendingCommands);
            Assert.True(live.Session.PendingCommands[0].NeedsReconciliation);
            Assert.Equal(digest, live.Session.PendingCommands[0].DigestSha256);

            await live.DisconnectAndReopenAsync(harness, ct);

            TableReceiptResolvedEventArgs resolved = await live.WaitResolutionAsync(
                args => string.Equals(args.Command.RequestId, requestId, StringComparison.Ordinal),
                "the reconnected outbox receipt lookup",
                ct);
            Assert.Equal(OutboxDisposition.Recorded, resolved.Resolution.Disposition);
            Assert.Equal(ContractReceiptStatus.Recorded, resolved.Resolution.ReceiptStatus);
            Assert.Equal(ControlReason, resolved.Resolution.ReasonCode);
            Assert.True(resolved.Resolution.FromReceiptLookup, "the reconnect resolution must come from RECEIPT_QUERY");
            Assert.Equal(CommandKind.ReleaseControl, resolved.Command.Kind);
            Assert.Equal(digest, resolved.Command.DigestSha256);
            Assert.Equal(digest, resolved.Resolution.CanonicalPayloadSha256);
            Assert.Empty(live.Session.PendingCommands);

            LocalControlRecord? record = live.Runtime.StoreForTest.GetControlResult(
                SeatAccount,
                scope,
                new RequestId(requestId));
            Assert.NotNull(record);
            Assert.Equal(digest, record!.Digest);
            Assert.Equal("ReleaseControl", record.Kind);
            Assert.Equal(record.EvaluatedStateVersion, resolved.Resolution.EvaluatedStateVersion.Value);

            TableControlSnapshot afterReconcile = await live.ControlSnapshotAsync(ct);
            Assert.Equal(1L, afterReconcile.ControlVersion);
            Assert.Equal(1L, afterReconcile.Seats[0].ControlEpoch);

            // A second side effect would have advanced the epoch to 2. Claiming with the
            // original post-release epoch proves the release committed exactly once.
            CommandSubmission claim = await live.SubmitControlAsync(
                CommandKind.ClaimControl,
                1,
                "reconnect-claim",
                decisionId: null,
                ct);
            TableReceiptResolvedEventArgs claimed = await live.WaitResolutionAsync(
                args => string.Equals(args.Command.RequestId, "reconnect-claim", StringComparison.Ordinal),
                "the post-reconcile claim receipt",
                ct);
            Assert.Equal(OutboxDisposition.Recorded, claimed.Resolution.Disposition);
            Assert.True(claim.Sent);
            TableControlSnapshot afterClaim = await live.ControlSnapshotAsync(ct);
            Assert.Equal(2L, afterClaim.ControlVersion);
            Assert.Equal(2L, afterClaim.Seats[0].ControlEpoch);

            var rebound = new FileOutboxStore(outboxPath);
            Assert.Empty(await rebound.LoadUnresolvedAsync(ct));
        }

        [Fact]
        public async Task ControlReceiptSurvivesColdProcessRestartWithOriginalDigestAndKind()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            string outboxPath = System.IO.Path.Combine(harness.Root, "control-outbox-cold");
            var firstOutbox = new FileOutboxStore(outboxPath);
            DropFirstControlReceiptChannel? dropper = null;
            LiveSession first = await LiveSession.OpenAsync(
                harness,
                firstOutbox,
                ct,
                inner =>
                {
                    dropper = new DropFirstControlReceiptChannel(inner, 1);
                    return dropper;
                });

            const string requestId = "cold-claim";
            RequestScopeId scope = first.Scope;
            try
            {
                CommandSubmission submission = await first.SubmitControlAsync(
                    CommandKind.ClaimControl,
                    0,
                    requestId,
                    decisionId: null,
                    ct);
                Assert.True(submission.Sent);
                string digest = submission.Command.DigestSha256;
                await WaitUntilAsync(
                    () => dropper != null && dropper.DroppedCount == 1,
                    "the first cold-restart control receipt to be dropped",
                    ct);
                await WaitUntilAsync(
                    () => first.Runtime.StoreForTest.GetControlResult(SeatAccount, scope, new RequestId(requestId)) != null,
                    "the dropped-receipt ClaimControl result to commit",
                    ct);
                Assert.NotNull(dropper);
                Assert.Equal(1, dropper!.DroppedCount);
                Assert.Single(first.Session.PendingCommands);

                // Permanent close disposes the runtime and releases the file lock. The
                // unresolved control entry stays in the durable file outbox.
                await first.DisposeAsync();

                var secondOutbox = new FileOutboxStore(outboxPath);
                await using LiveSession second = await LiveSession.OpenAsync(harness, secondOutbox, ct);
                Assert.True(
                    second.Runtime.ActorForTest.RecoveryGeneration > 0,
                    "the second runtime must be a cold recovery, not a fresh room");
                TableReceiptResolvedEventArgs resolved = await second.WaitResolutionAsync(
                    args => string.Equals(args.Command.RequestId, requestId, StringComparison.Ordinal),
                    "the cold-restart outbox receipt lookup",
                    ct);
                Assert.Equal(OutboxDisposition.Recorded, resolved.Resolution.Disposition);
                Assert.Equal(ControlReason, resolved.Resolution.ReasonCode);
                Assert.True(resolved.Resolution.FromReceiptLookup);
                Assert.Equal(CommandKind.ClaimControl, resolved.Command.Kind);
                Assert.Equal(digest, resolved.Command.DigestSha256);
                Assert.Equal(digest, resolved.Resolution.CanonicalPayloadSha256);

                LocalControlRecord? cold = second.Runtime.StoreForTest.GetControlResult(
                    SeatAccount,
                    scope,
                    new RequestId(requestId));
                Assert.NotNull(cold);
                Assert.Equal(digest, cold!.Digest);
                Assert.Equal("ClaimControl", cold.Kind);
                Assert.Equal(cold.EvaluatedStateVersion, resolved.Resolution.EvaluatedStateVersion.Value);

                TableControlSnapshot control = await second.ControlSnapshotAsync(ct);
                Assert.Equal(0L, control.ControlVersion);
                Assert.Equal(0L, control.Seats[0].ControlEpoch);
                Assert.Empty(second.Session.PendingCommands);
            }
            finally
            {
                await first.DisposeAsync();
            }
        }

        [Fact]
        public async Task ControlOutboxIsReconciledByASeparateColdProcess()
        {
            await using var harness = new P41Harness();
            // Match the crash worker's fixed owner so the save can be hydrated by it.
            harness.Options.OwnerInstanceId = "crash-owner";
            CancellationToken ct = TestContext.Current.CancellationToken;
            string outboxPath = System.IO.Path.Combine(harness.Root, "control-outbox-process");
            var outbox = new FileOutboxStore(outboxPath);
            DropFirstControlReceiptChannel? dropper = null;
            LiveSession live = await LiveSession.OpenAsync(
                harness,
                outbox,
                ct,
                inner =>
                {
                    dropper = new DropFirstControlReceiptChannel(inner, 1);
                    return dropper;
                });

            const string requestId = "process-claim";
            RequestScopeId scope = live.Scope;
            string digest;
            string kind;
            ulong evaluated;
            long controlVersion;
            try
            {
                CommandSubmission submission = await live.SubmitControlAsync(
                    CommandKind.ClaimControl,
                    0,
                    requestId,
                    decisionId: null,
                    ct);
                Assert.True(submission.Sent);
                digest = submission.Command.DigestSha256;
                await WaitUntilAsync(
                    () => dropper != null && dropper.DroppedCount == 1,
                    "the first process control receipt to be dropped",
                    ct);
                await WaitUntilAsync(
                    () => live.Runtime.StoreForTest.GetControlResult(SeatAccount, scope, new RequestId(requestId)) != null,
                    "the dropped-receipt process ClaimControl result to commit",
                    ct);
                Assert.NotNull(dropper);
                Assert.Equal(1, dropper!.DroppedCount);
                LocalControlRecord record = live.Runtime.StoreForTest.GetControlResult(
                    SeatAccount,
                    scope,
                    new RequestId(requestId))!;
                Assert.Equal(digest, record.Digest);
                kind = record.Kind;
                evaluated = record.EvaluatedStateVersion;
                controlVersion = live.Runtime.StoreForTest.ControlVersionForTest;
                Assert.Equal("ClaimControl", kind);
                Assert.Equal(0L, controlVersion);
            }
            finally
            {
                await live.DisposeAsync();
            }

            string workerDll = System.IO.Path.Combine(
                P41Harness.FindRepositoryRoot(),
                "dotnet",
                "tests",
                "Mahjong.LocalHost.CrashWorker",
                "bin",
                "Release",
                "net8.0",
                "Mahjong.LocalHost.CrashWorker.dll");
            Assert.True(File.Exists(workerDll), "CrashWorker was not built: " + workerDll);
            string marker = System.IO.Path.Combine(harness.Root, "control-process-marker.txt");
            System.Diagnostics.ProcessStartInfo start = CreateWorkerStartInfo(
                workerDll,
                harness.Options.SaveDirectory,
                P41Harness.RoomId,
                marker,
                "control-outbox-reconcile",
                harness.Clock.MonotonicMillis.ToString(System.Globalization.CultureInfo.InvariantCulture),
                harness.Clock.UtcNowMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                outboxPath,
                SeatAccount,
                scope.Value,
                requestId);
            using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!)
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waitCts.CancelAfter(TimeSpan.FromSeconds(45));
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
                    throw new TimeoutException("The cold-process control outbox reader did not exit in time.");
                }

                Assert.True(
                    process.ExitCode == 0,
                    "The cold-process control outbox reader failed: exit=" + process.ExitCode +
                    " stderr=" + await stderr);
            }

            string[] lines = File.ReadAllLines(marker);
            string resolution = Assert.Single(
                lines,
                line => line.StartsWith("RESOLUTION|" + requestId + "|", StringComparison.Ordinal));
            string[] parts = resolution.Split('|');
            Assert.Equal(8, parts.Length);
            Assert.Equal("Recorded", parts[2]);
            Assert.Equal(ControlReason, parts[3]);
            Assert.Equal(digest, parts[4]);
            Assert.Equal("1", parts[5]);
            Assert.Equal(CommandKind.ClaimControl.ToString(), parts[6]);
            Assert.Equal(evaluated.ToString(System.Globalization.CultureInfo.InvariantCulture), parts[7]);
            Assert.Equal("STATE|Ready", Assert.Single(lines, line => line.StartsWith("STATE|", StringComparison.Ordinal)));
            Assert.Equal("PENDING|0", Assert.Single(lines, line => line.StartsWith("PENDING|", StringComparison.Ordinal)));
            Assert.Equal(
                "RECORD|" + digest + "|" + kind + "|" +
                evaluated.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Assert.Single(lines, line => line.StartsWith("RECORD|", StringComparison.Ordinal)));
            Assert.Equal(
                "CONTROLVERSION|" + controlVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Assert.Single(lines, line => line.StartsWith("CONTROLVERSION|", StringComparison.Ordinal)));

            var rebound = new FileOutboxStore(outboxPath);
            Assert.Empty(await rebound.LoadUnresolvedAsync(ct));
        }

        private static System.Diagnostics.ProcessStartInfo CreateWorkerStartInfo(
            string workerDll,
            string saveDirectory,
            string roomId,
            string markerPath,
            string mode,
            params string[] arguments)
        {
            string dotnet = ResolveDotnetPath();
            var parts = new List<string>
            {
                Quote(workerDll),
                Quote(saveDirectory),
                Quote(roomId),
                Quote(markerPath),
                mode
            };
            for (int i = 0; i < arguments.Length; i++)
            {
                parts.Add(Quote(arguments[i]));
            }

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = dotnet,
                Arguments = string.Join(" ", parts),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (string.IsNullOrEmpty(dotnetRoot) && File.Exists("D:/Unity/dotnet/dotnet.exe"))
            {
                dotnetRoot = "D:/Unity/dotnet";
            }

            if (!string.IsNullOrEmpty(dotnetRoot))
            {
                startInfo.Environment["DOTNET_ROOT"] = dotnetRoot!;
            }

            return startInfo;
        }

        private static string Quote(string value)
        {
            return "\"" + value + "\"";
        }

        private static string ResolveDotnetPath()
        {
            string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrEmpty(dotnetRoot))
            {
                string candidate = System.IO.Path.Combine(dotnetRoot!, "dotnet.exe");
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
        public async Task ControlReplayIsIdenticalAndDifferentBytesConflictWithoutSecondEffect()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            await using RawControlConnection client = await RawControlConnection.OpenAsync(harness, ct);

            const string requestId = "raw-release";
            byte[] releaseBytes = client.BuildCommandBytes(
                CommandKind.ReleaseControl,
                0,
                requestId,
                payloadBytes: null,
                decisionId: null);
            WireEnvelope first = await client.SendRawAsync(releaseBytes, requestId, ct);
            AssertReceipt(first, WireReceiptStatus.Recorded, "the first ReleaseControl");
            Assert.False(first.Receipt.Replayed);
            string digest = first.Receipt.CanonicalPayloadSha256;
            ulong evaluated = first.Receipt.EvaluatedStateVersion;

            TableControlSnapshot afterFirst = await client.ControlSnapshotAsync(ct);
            Assert.Equal(1L, afterFirst.ControlVersion);
            Assert.Equal(1L, afterFirst.Seats[0].ControlEpoch);

            WireEnvelope replay = await client.SendRawAsync(releaseBytes, requestId, ct);
            AssertReceipt(replay, WireReceiptStatus.Recorded, "the exact-byte replay");
            Assert.True(replay.Receipt.Replayed, "an exact replay must be marked as replayed");
            Assert.Equal(digest, replay.Receipt.CanonicalPayloadSha256);
            Assert.Equal(evaluated, replay.Receipt.EvaluatedStateVersion);
            Assert.Equal(first.Receipt.ReasonCode, replay.Receipt.ReasonCode);

            TableControlSnapshot afterReplay = await client.ControlSnapshotAsync(ct);
            Assert.Equal(1L, afterReplay.ControlVersion);
            Assert.Equal(1L, afterReplay.Seats[0].ControlEpoch);

            byte[] conflictBytes = client.BuildCommandBytes(
                CommandKind.ClaimControl,
                1,
                requestId,
                payloadBytes: null,
                decisionId: null);
            WireEnvelope conflict = await client.SendRawAsync(conflictBytes, requestId, ct);
            AssertReceipt(conflict, WireReceiptStatus.Rejected, "the different-byte replay");
            Assert.Equal(ProtocolErrorCode.RequestIdConflict, conflict.Receipt.ReasonCode);
            TableControlSnapshot afterConflict = await client.ControlSnapshotAsync(ct);
            Assert.Equal(1L, afterConflict.ControlVersion);
            Assert.Equal(1L, afterConflict.Seats[0].ControlEpoch);

            byte[] claimBytes = client.BuildCommandBytes(
                CommandKind.ClaimControl,
                1,
                "raw-claim",
                payloadBytes: null,
                decisionId: null);
            WireEnvelope claim = await client.SendRawAsync(claimBytes, "raw-claim", ct);
            AssertReceipt(claim, WireReceiptStatus.Recorded, "the new-epoch ClaimControl");
            Assert.False(claim.Receipt.Replayed);
            TableControlSnapshot afterClaim = await client.ControlSnapshotAsync(ct);
            Assert.Equal(2L, afterClaim.ControlVersion);
            Assert.Equal(2L, afterClaim.Seats[0].ControlEpoch);
        }

        [Fact]
        public async Task ExpiredControlScopeIsRejectedWithoutSideEffect()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            await using RawControlConnection client = await RawControlConnection.OpenAsync(harness, ct);
            RequestScopeId scope = new RequestScopeId(client.ScopeId);
            Assert.Null(client.Store.GetControlResult(SeatAccount, scope, new RequestId("expired-claim")));

            harness.Clock.MonotonicMillis += (long)TimeSpan.FromHours(24).TotalMilliseconds + 1_000L;
            harness.Clock.UtcNowMs += (long)TimeSpan.FromHours(24).TotalMilliseconds + 1_000L;

            byte[] expiredBytes = client.BuildCommandBytes(
                CommandKind.ClaimControl,
                0,
                "expired-claim",
                payloadBytes: null,
                decisionId: null);
            WireEnvelope rejected = await client.SendRawAsync(expiredBytes, "expired-claim", ct);
            AssertReceipt(rejected, WireReceiptStatus.Rejected, "the expired-scope ClaimControl");
            Assert.Equal(ProtocolErrorCode.RequestScopeExpired, rejected.Receipt.ReasonCode);
            Assert.Null(client.Store.GetControlResult(SeatAccount, scope, new RequestId("expired-claim")));
            Assert.Equal(0L, client.Store.ControlVersionForTest);
        }

        [Fact]
        public async Task ConcurrentClaimAndReleaseAdmitExactlyOneControlChange()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            await using RawControlConnection client = await RawControlConnection.OpenAsync(harness, ct);

            WireEnvelope setup = await client.SendCommandAsync(
                CommandKind.ReleaseControl,
                0,
                "concurrent-setup",
                payloadBytes: null,
                decisionId: null,
                ct);
            AssertReceipt(setup, WireReceiptStatus.Recorded, "the setup ReleaseControl");
            TableControlSnapshot before = await client.ControlSnapshotAsync(ct);
            Assert.Equal(1L, before.ControlVersion);
            Assert.Equal(1L, before.Seats[0].ControlEpoch);

            byte[] claimBytes = client.BuildCommandBytes(
                CommandKind.ClaimControl,
                1,
                "concurrent-claim",
                payloadBytes: null,
                decisionId: null);
            byte[] releaseBytes = client.BuildCommandBytes(
                CommandKind.ReleaseControl,
                1,
                "concurrent-release",
                payloadBytes: null,
                decisionId: null);

            Task claimTask = client.Channel.SendAsync(claimBytes, ct);
            Task releaseTask = client.Channel.SendAsync(releaseBytes, ct);
            await Task.WhenAll(claimTask, releaseTask);

            WireEnvelope first = await client.ReceiveCommandResultAsync(null, ct);
            WireEnvelope second = await client.ReceiveCommandResultAsync(null, ct);
            var receipts = new[] { first, second };
            Assert.Single(receipts, r => r.Receipt.Status == WireReceiptStatus.Recorded);
            WireEnvelope loser = Assert.Single(receipts, r => r.Receipt.Status == WireReceiptStatus.Rejected);
            Assert.Equal(TableRuntimeReasonCode.StaleControlEpoch, loser.Receipt.ReasonCode);
            Assert.Contains(
                receipts,
                r => string.Equals(r.RequestId, "concurrent-claim", StringComparison.Ordinal));
            Assert.Contains(
                receipts,
                r => string.Equals(r.RequestId, "concurrent-release", StringComparison.Ordinal));

            TableControlSnapshot after = await client.ControlSnapshotAsync(ct);
            Assert.Equal(2L, after.ControlVersion);
            Assert.Equal(2L, after.Seats[0].ControlEpoch);

            WireEnvelope winner = receipts.Single(r => r.Receipt.Status == WireReceiptStatus.Recorded);
            RequestScopeId scope = new RequestScopeId(client.ScopeId);
            string winnerId = winner.RequestId;
            string loserId = loser.RequestId;
            Assert.NotNull(client.Store.GetControlResult(SeatAccount, scope, new RequestId(winnerId)));
            Assert.Null(client.Store.GetControlResult(SeatAccount, scope, new RequestId(loserId)));
        }

        [Fact]
        public async Task ControlAroundAuthoritativeDeadlineRejectsStaleDecisionAndStaleEpoch()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            await using LiveSession live = await LiveSession.OpenAsync(
                harness,
                new InMemoryOutboxStore(),
                ct);

            PlayerSnapshot snapshot = await live.WaitForDecisionAsync(ct);
            string decisionId = snapshot.PrivateView!.Decision.DecisionId;
            Assert.False(string.IsNullOrEmpty(decisionId));

            // Before the deadline: the control receipt is recorded against the live decision.
            await live.SubmitAndWaitAsync(
                CommandKind.ClaimControl,
                0,
                "deadline-claim-before",
                decisionId: null,
                ct);
            TableControlSnapshot before = await live.ControlSnapshotAsync(ct);
            Assert.Equal(0L, before.ControlVersion);
            Assert.Equal(0L, before.Seats[0].ControlEpoch);

            // Advance past the authoritative 23s seat deadline; the driver applies the
            // timeout and the old decision identity is no longer current.
            harness.Clock.MonotonicMillis += 30_000L;
            harness.Clock.UtcNowMs += 30_000L;
            await WaitUntilAsync(
                () => !string.Equals(live.Runtime.ActorForTest.GetPendingDecisionId(0), decisionId, StringComparison.Ordinal),
                "the authoritative decision deadline to retire the old decision",
                ct);

            TableReceiptResolvedEventArgs stale = await live.SubmitAndWaitAsync(
                CommandKind.ReleaseControl,
                0,
                "deadline-stale-decision",
                new DecisionId(decisionId),
                ct);
            Assert.Equal(OutboxDisposition.Rejected, stale.Resolution.Disposition);
            Assert.Equal(ProtocolErrorCode.DecisionMismatch, stale.Resolution.ReasonCode);
            Assert.Null(live.Runtime.StoreForTest.GetControlResult(
                SeatAccount,
                live.Scope,
                new RequestId("deadline-stale-decision")));
            TableControlSnapshot afterStale = await live.ControlSnapshotAsync(ct);
            Assert.Equal(0L, afterStale.ControlVersion);
            Assert.Equal(0L, afterStale.Seats[0].ControlEpoch);

            // A control command without a stale decision id is still authorized.
            TableReceiptResolvedEventArgs released = await live.SubmitAndWaitAsync(
                CommandKind.ReleaseControl,
                0,
                "deadline-release",
                decisionId: null,
                ct);
            Assert.Equal(OutboxDisposition.Recorded, released.Resolution.Disposition);
            TableControlSnapshot afterRelease = await live.ControlSnapshotAsync(ct);
            Assert.Equal(1L, afterRelease.ControlVersion);
            Assert.Equal(1L, afterRelease.Seats[0].ControlEpoch);

            // The old epoch is stale; the new epoch is operable.
            TableReceiptResolvedEventArgs oldEpoch = await live.SubmitAndWaitAsync(
                CommandKind.ClaimControl,
                0,
                "deadline-old-epoch",
                decisionId: null,
                ct);
            Assert.Equal(OutboxDisposition.Rejected, oldEpoch.Resolution.Disposition);
            Assert.Equal(TableRuntimeReasonCode.StaleControlEpoch, oldEpoch.Resolution.ReasonCode);

            TableReceiptResolvedEventArgs newEpoch = await live.SubmitAndWaitAsync(
                CommandKind.ClaimControl,
                1,
                "deadline-new-epoch",
                decisionId: null,
                ct);
            Assert.Equal(OutboxDisposition.Recorded, newEpoch.Resolution.Disposition);
            TableControlSnapshot afterClaim = await live.ControlSnapshotAsync(ct);
            Assert.Equal(2L, afterClaim.ControlVersion);
            Assert.Equal(2L, afterClaim.Seats[0].ControlEpoch);
        }

        private static string SeatAccount => "local-" + P41Harness.RoomId + "-seat0";

        private static async Task WaitReadyAsync(TableSession session, CancellationToken ct)
        {
            for (int i = 0; i < 50 && session.State != TableConnectionState.Ready; i++)
            {
                await Task.Delay(10, ct);
            }

            Assert.Equal(TableConnectionState.Ready, session.State);
        }

        private static async Task WaitUntilAsync(Func<bool> predicate, string description, CancellationToken ct)
        {
            for (int i = 0; i < 1_500; i++)
            {
                if (predicate())
                {
                    return;
                }

                await Task.Delay(10, ct);
            }

            throw new XunitException("Timed out waiting for " + description + ".");
        }

        private static void AssertReceipt(WireEnvelope envelope, WireReceiptStatus status, string description)
        {
            if (envelope.Receipt == null)
            {
                throw new XunitException(
                    description + " did not return a receipt; body=" + envelope.MessageType +
                    " error=" + (envelope.Error == null ? "<none>" : envelope.Error.Code + "/" + envelope.Error.DetailCode));
            }

            if (envelope.Receipt.Status != status)
            {
                throw new XunitException(
                    description + " returned " + envelope.Receipt.Status + "/" + envelope.Receipt.ReasonCode +
                    " instead of " + status + ".");
            }
        }

        private sealed class LiveSession : IAsyncDisposable
        {
            private readonly object _gate = new object();
            private readonly List<TableReceiptResolvedEventArgs> _resolutions =
                new List<TableReceiptResolvedEventArgs>();
            private readonly CancellationToken _lifetimeToken;
            private readonly ITableChannel _inner;
            private CancellationTokenSource? _pumpCts;
            private Task? _pumpTask;
            private bool _disposed;

            private LiveSession(ITableChannel inner, TableSession session, CancellationToken ct)
            {
                _inner = inner;
                Session = session;
                _lifetimeToken = ct;
            }

            public void StartPump()
            {
                // The receive loop must start only after HELLO has reset the runtime's
                // outbound queue; a receiver already parked on that queue observes the reset
                // as LOCAL_CLOSED and would incorrectly enter Reconnecting.
                if (_pumpTask != null)
                {
                    return;
                }

                _pumpCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
                CancellationToken token = _pumpCts.Token;
                _pumpTask = Task.Run(() => PumpAsync(token));
            }

            private async Task StopPumpAsync()
            {
                CancellationTokenSource? cts = _pumpCts;
                Task? task = _pumpTask;
                _pumpCts = null;
                _pumpTask = null;
                if (cts != null)
                {
                    try
                    {
                        cts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }

                if (task != null)
                {
                    try
                    {
                        await task;
                    }
                    catch (Exception)
                    {
                    }
                }

                cts?.Dispose();
            }

            public TableSession Session { get; }

            public LocalTableRuntime Runtime =>
                _inner is LocalTableChannel local && local.RuntimeForMatch != null
                    ? local.RuntimeForMatch
                    : throw new InvalidOperationException("The local runtime is not open.");

            public RequestScopeId Scope => Session.Binding!.Scope.ScopeId;

            public static async Task<LiveSession> OpenAsync(
                P41Harness harness,
                IOutboxStore outbox,
                CancellationToken ct,
                Func<ITableChannel, ITableChannel>? wrap = null)
            {
                ITableChannel inner = await LocalTableHost.CreateChannelAsync(harness.Options, ct);
                string channelTicket = LocalTableHost.IssueTicket(harness.Options);
                await inner.OpenAsync(new TableChannelConnectRequest(channelTicket, "p41-test", null), ct);
                ITableChannel channel = wrap == null ? inner : wrap(inner);
                var session = new TableSession(channel, outbox);
                var live = new LiveSession(inner, session, ct);
                session.ReceiptResolved += live.OnReceiptResolved;
                string sessionTicket = LocalTableHost.IssueTicket(harness.Options);
                await session.OpenAsync(new TableSessionOpenRequest(sessionTicket, "p41-test"), ct);
                live.StartPump();
                await live.WaitReadyAsync(ct);
                return live;
            }

            public async Task DisconnectAndReopenAsync(P41Harness harness, CancellationToken ct)
            {
                // No receiver may be parked on the outbound queue while HELLO resets it.
                await StopPumpAsync();
                await Session.DisconnectAsync(ct);
                string ticket = LocalTableHost.IssueTicket(harness.Options);
                await Session.OpenAsync(new TableSessionOpenRequest(ticket, "p41-test"), ct);
                StartPump();
                await WaitReadyAsync(ct);
            }

            public async Task WaitReadyAsync(CancellationToken ct)
            {
                for (int i = 0; i < 1_000 && Session.State != TableConnectionState.Ready; i++)
                {
                    if (Session.State == TableConnectionState.Closed || Session.State == TableConnectionState.Faulted)
                    {
                        throw new XunitException(
                            "The session became " + Session.State + " before Ready; last=" + Session.LastError);
                    }

                    await Task.Delay(10, ct);
                }

                Assert.True(
                    Session.State == TableConnectionState.Ready,
                    "The session did not become Ready: state=" + Session.State +
                    " channel=" + (_inner is LocalTableChannel diagnostic ? diagnostic.StateForTest.ToString() : "wrapped") +
                    " last=" + Session.LastError +
                    " pending=" + Session.PendingCommands.Count);
            }

            public async Task<PlayerSnapshot> WaitForDecisionAsync(CancellationToken ct)
            {
                for (int i = 0; i < 1_000; i++)
                {
                    PlayerSnapshot? snapshot = Session.PlayerSnapshot;
                    if (snapshot?.PrivateView?.Decision != null &&
                        !string.IsNullOrEmpty(snapshot.PrivateView.Decision.DecisionId))
                    {
                        return snapshot;
                    }

                    await Task.Delay(10, ct);
                }

                throw new XunitException(
                    "No player decision was opened; state=" + Session.State +
                    " last=" + Session.LastError + " pending=" + Session.PendingCommands.Count);
            }

            public Task<CommandSubmission> SubmitControlAsync(
                CommandKind kind,
                ulong epoch,
                string requestId,
                DecisionId? decisionId,
                CancellationToken ct)
            {
                return SubmitControlCoreAsync(kind, epoch, requestId, decisionId, ct);
            }

            private async Task<CommandSubmission> SubmitControlCoreAsync(
                CommandKind kind,
                ulong epoch,
                string requestId,
                DecisionId? decisionId,
                CancellationToken ct)
            {
                PlayerSnapshot baseline = Session.PlayerSnapshot ??
                    throw new InvalidOperationException("The session has no player baseline.");
                BusinessPayload payload = kind == CommandKind.ClaimControl
                    ? CommandPayloadFactory.ClaimControl(baseline.RoundId, epoch)
                    : CommandPayloadFactory.ReleaseControl(baseline.RoundId, epoch);
                var command = new ProtocolCommand(
                    kind,
                    payload,
                    new RequestId(requestId),
                    Session.Binding!.Scope.ScopeId,
                    new StateVersion(baseline.StateVersion),
                    new RoundId(baseline.RoundId),
                    decisionId,
                    new ControlEpoch(epoch));
                CommandSubmission submission = await Session.SubmitAsync(command, ct);
                Assert.True(submission.Sent);
                return submission;
            }

            public async Task<TableReceiptResolvedEventArgs> SubmitAndWaitAsync(
                CommandKind kind,
                ulong epoch,
                string requestId,
                DecisionId? decisionId,
                CancellationToken ct)
            {
                await SubmitControlAsync(kind, epoch, requestId, decisionId, ct);
                return await WaitResolutionAsync(
                    args => string.Equals(args.Command.RequestId, requestId, StringComparison.Ordinal),
                    "the receipt for " + requestId,
                    ct);
            }

            public async Task<TableReceiptResolvedEventArgs> WaitResolutionAsync(
                Func<TableReceiptResolvedEventArgs, bool> predicate,
                string description,
                CancellationToken ct)
            {
                for (int i = 0; i < 1_500; i++)
                {
                    TableReceiptResolvedEventArgs? match;
                    lock (_gate)
                    {
                        match = _resolutions.FirstOrDefault(predicate);
                    }

                    if (match != null)
                    {
                        return match;
                    }

                    await Task.Delay(10, ct);
                }

                IReadOnlyList<PendingCommandSnapshot> pending = Session.PendingCommands;
                throw new XunitException(
                    "Timed out waiting for " + description + "; state=" + Session.State +
                    " last=" + Session.LastError +
                    " pending=" + string.Join(",", pending.Select(p => p.RequestId + ":" + p.Kind)));
            }

            public Task<TableControlSnapshot> ControlSnapshotAsync(CancellationToken ct)
            {
                return Runtime.ActorForTest.GetControlSnapshotAsync(ct).AsTask();
            }

            private async Task PumpAsync(CancellationToken token)
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Session.PumpOnceAsync(token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (TableChannelClosedException)
                    {
                    }
                    catch (InvalidOperationException)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                    }

                    if (Session.State != TableConnectionState.Ready)
                    {
                        try
                        {
                            await Task.Delay(5, token);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }
                }
            }

            private void OnReceiptResolved(object? sender, TableReceiptResolvedEventArgs args)
            {
                lock (_gate)
                {
                    _resolutions.Add(args);
                }
            }

            public async ValueTask DisposeAsync()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                Session.ReceiptResolved -= OnReceiptResolved;
                await StopPumpAsync();

                try
                {
                    await Session.CloseAsync("TEST_DONE", CancellationToken.None);
                }
                catch (Exception)
                {
                }

                try
                {
                    if (_inner.IsOpen)
                    {
                        await _inner.CloseAsync(
                            new TableChannelCloseReason("TEST_DONE", true),
                            CancellationToken.None);
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        private sealed class RawControlConnection : IAsyncDisposable
        {
            private RawControlConnection(ITableChannel channel, WireEnvelope welcome, string roundId, string scopeId)
            {
                Channel = channel;
                Welcome = welcome;
                RoundId = roundId;
                ScopeId = scopeId;
            }

            public ITableChannel Channel { get; }

            public WireEnvelope Welcome { get; }

            public string RoundId { get; }

            public string ScopeId { get; }

            public LocalTableRuntime Runtime =>
                Channel is LocalTableChannel local && local.RuntimeForMatch != null
                    ? local.RuntimeForMatch
                    : throw new InvalidOperationException("The raw local runtime is not open.");

            public LocalTableStore Store => Runtime.StoreForTest;

            public static async Task<RawControlConnection> OpenAsync(P41Harness harness, CancellationToken ct)
            {
                ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, ct);
                string ticket = LocalTableHost.IssueTicket(harness.Options);
                await channel.OpenAsync(new TableChannelConnectRequest(ticket, "p41-test", null), ct);
                var hello = new WireEnvelope
                {
                    ProtocolMajor = ProtocolConstants.Major,
                    ProtocolMinor = ProtocolConstants.Minor,
                    RulesetVersion = ProtocolConstants.RulesetVersion,
                    ProjectionVersion = ProtocolConstants.ProjectionVersion,
                    MessageType = WireMessageType.Hello,
                    BindingEpoch = 1UL,
                    CorrelationId = "raw-hello",
                    Hello = new WireHello
                    {
                        Ticket = ticket,
                        ProtocolMajor = ProtocolConstants.Major,
                        ProtocolMinor = ProtocolConstants.Minor,
                        RulesetVersion = ProtocolConstants.RulesetVersion,
                        ProjectionVersion = ProtocolConstants.ProjectionVersion,
                        ClientBuild = "p41-test",
                        ResumeRequested = false
                    }
                };
                await channel.SendAsync(WireCodec.Encode(hello), ct);
                WireEnvelope welcome = await ReceiveUntilAsync(
                    channel,
                    envelope => envelope.Welcome != null,
                    "the raw WELCOME",
                    ct);
                if (channel is not LocalTableChannel local || local.RuntimeForMatch == null)
                {
                    throw new XunitException("The raw channel did not expose its local runtime.");
                }

                string roundId = local.RuntimeForMatch.ActorForTest.GetRoundStateForHost().RoundId;
                return new RawControlConnection(
                    channel,
                    welcome,
                    roundId,
                    welcome.Welcome.Scope.ScopeId);
            }

            public byte[] BuildCommandBytes(
                CommandKind kind,
                ulong epoch,
                string requestId,
                byte[]? payloadBytes,
                string? decisionId)
            {
                BusinessPayload payload = payloadBytes == null
                    ? (kind == CommandKind.ClaimControl
                        ? CommandPayloadFactory.ClaimControl(RoundId, epoch)
                        : CommandPayloadFactory.ReleaseControl(RoundId, epoch))
                    : BusinessPayload.Retain(payloadBytes);
                var envelope = new WireEnvelope
                {
                    ProtocolMajor = ProtocolConstants.Major,
                    ProtocolMinor = ProtocolConstants.Minor,
                    RulesetVersion = ProtocolConstants.RulesetVersion,
                    ProjectionVersion = ProtocolConstants.ProjectionVersion,
                    MessageType = WireMessageType.Command,
                    SessionId = Welcome.SessionId,
                    RoomId = P41Harness.RoomId,
                    SeatId = Welcome.SeatId,
                    BindingEpoch = Welcome.BindingEpoch,
                    RequestId = requestId,
                    Scope = Welcome.Welcome.Scope.Clone(),
                    ControlEpoch = epoch,
                    OwnerEpoch = Welcome.Welcome.OwnerEpoch,
                    Command = new WireCommand
                    {
                        Type = (WireCommandType)(int)kind,
                        CanonicalPayload = ByteString.CopyFrom(payload.CopyBytes()),
                        PayloadSchemaVersion = 1U,
                        ExpectedStateVersion = 0UL,
                        ExpectedRoundId = RoundId,
                        ExpectedDecisionId = decisionId ?? string.Empty
                    }
                };
                return WireCodec.Encode(envelope);
            }

            public async Task<WireEnvelope> SendRawAsync(byte[] bytes, string requestId, CancellationToken ct)
            {
                await Channel.SendAsync(bytes, ct);
                return await ReceiveCommandResultAsync(requestId, ct);
            }

            public async Task<WireEnvelope> SendCommandAsync(
                CommandKind kind,
                ulong epoch,
                string requestId,
                byte[]? payloadBytes,
                string? decisionId,
                CancellationToken ct)
            {
                byte[] bytes = BuildCommandBytes(kind, epoch, requestId, payloadBytes, decisionId);
                return await SendRawAsync(bytes, requestId, ct);
            }

            public Task<WireEnvelope> ReceiveCommandResultAsync(string? requestId, CancellationToken ct)
            {
                return ReceiveUntilAsync(
                    Channel,
                    envelope => envelope.Receipt != null &&
                                (requestId == null ||
                                 string.Equals(envelope.RequestId, requestId, StringComparison.Ordinal)),
                    requestId == null ? "a raw control receipt" : "the receipt for " + requestId,
                    ct);
            }

            public Task<TableControlSnapshot> ControlSnapshotAsync(CancellationToken ct)
            {
                return Runtime.ActorForTest.GetControlSnapshotAsync(ct).AsTask();
            }

            private static async Task<WireEnvelope> ReceiveUntilAsync(
                ITableChannel channel,
                Func<WireEnvelope, bool> predicate,
                string description,
                CancellationToken ct)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                while (true)
                {
                    ReadOnlyMemory<byte> bytes = await channel.ReceiveAsync(timeout.Token);
                    WireEnvelope envelope = WireEnvelope.Parser.ParseFrom(bytes.ToArray());
                    if (envelope.Error != null)
                    {
                        throw new XunitException(
                            description + " was interrupted by ERROR " + envelope.Error.Code +
                            "/" + envelope.Error.DetailCode + ".");
                    }

                    if (predicate(envelope))
                    {
                        return envelope;
                    }
                }
            }

            public async ValueTask DisposeAsync()
            {
                try
                {
                    if (Channel.IsOpen)
                    {
                        await Channel.CloseAsync(
                            new TableChannelCloseReason("TEST_DONE", true),
                            CancellationToken.None);
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        private sealed class DropFirstControlReceiptChannel : ITableChannel
        {
            private readonly ITableChannel _inner;
            private int _remaining;

            public DropFirstControlReceiptChannel(ITableChannel inner, int dropCount)
            {
                _inner = inner;
                _remaining = dropCount;
            }

            public int DroppedCount { get; private set; }

            public bool IsOpen => _inner.IsOpen;

            public Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken) =>
                _inner.OpenAsync(request, cancellationToken);

            public Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken) =>
                _inner.SendAsync(wireEnvelope, cancellationToken);

            public Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken) =>
                _inner.CloseAsync(reason, cancellationToken);

            public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
            {
                while (true)
                {
                    ReadOnlyMemory<byte> bytes = await _inner.ReceiveAsync(cancellationToken);
                    WireEnvelope envelope = WireEnvelope.Parser.ParseFrom(bytes.ToArray());
                    if (_remaining > 0 &&
                        envelope.Receipt != null &&
                        string.Equals(envelope.Receipt.ReasonCode, ControlReason, StringComparison.Ordinal))
                    {
                        _remaining--;
                        DroppedCount++;
                        continue;
                    }

                    return bytes;
                }
            }
        }

        private sealed class EmptyControlDigestChannel : ITableChannel
        {
            private readonly ITableChannel _inner;
            public int Mutations { get; private set; }
            public EmptyControlDigestChannel(ITableChannel inner) { _inner = inner; }
            public bool IsOpen => _inner.IsOpen;
            public Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken) => _inner.OpenAsync(request, cancellationToken);
            public Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken) => _inner.SendAsync(wireEnvelope, cancellationToken);
            public Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken) => _inner.CloseAsync(reason, cancellationToken);
            public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
            {
                var bytes = await _inner.ReceiveAsync(cancellationToken);
                var envelope = WireEnvelope.Parser.ParseFrom(bytes.ToArray());
                if (envelope.Receipt != null && string.Equals(envelope.Receipt.ReasonCode, ControlReason, StringComparison.Ordinal))
                {
                    Mutations++;
                    envelope.Receipt.CanonicalPayloadSha256 = string.Empty;
                    return WireCodec.Encode(envelope);
                }

                return bytes;
            }
        }
    }
}
