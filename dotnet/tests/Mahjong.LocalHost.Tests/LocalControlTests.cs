using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Domain;
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
using WireReceiptQuery = Mahjong.Contracts.Wire.ReceiptQuery;
using WireReceiptStatus = Mahjong.Contracts.Wire.ReceiptStatus;
using WireLookupStatus = Mahjong.Contracts.Wire.LookupStatus;
using WireScope = Mahjong.Contracts.Wire.RequestScope;

namespace Mahjong.LocalHost.Tests
{
    public sealed class LocalControlTests
    {
        private const string ControlReason = "LOCAL_CONTROL";

        [Fact]
        public async Task ReviewerProbe_ControlReplaySurvivesDecisionRetirement()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            await using RawControlConnection client = await RawControlConnection.OpenAsync(harness, ct);
            await WaitUntilAsync(
                () => !string.IsNullOrEmpty(client.Runtime.ActorForTest.GetPendingDecisionId(0)),
                "a live decision before recording the control request",
                ct);
            string decision = client.Runtime.ActorForTest.GetPendingDecisionId(0)!;
            const string requestId = "reviewer-control-replay-retired-decision";
            byte[] originalBytes = client.BuildCommandBytes(
                CommandKind.ClaimControl, 0, requestId, payloadBytes: null, decisionId: decision);
            WireEnvelope original = await client.SendRawAsync(originalBytes, requestId, ct);
            AssertReceipt(original, WireReceiptStatus.Recorded, "the original control request with a live decision");
            LocalControlRecord? record = client.Store.GetControlResult(
                SeatAccount, new RequestScopeId(client.ScopeId), new RequestId(requestId));
            Assert.NotNull(record);
            Assert.Equal(original.Receipt.CanonicalPayloadSha256, record!.Digest);

            harness.Clock.MonotonicMillis += 30_000L;
            harness.Clock.UtcNowMs += 30_000L;
            await WaitUntilAsync(
                () => !string.Equals(client.Runtime.ActorForTest.GetPendingDecisionId(0), decision, StringComparison.Ordinal),
                "the recorded request's decision to retire",
                ct);

            WireEnvelope replay = await client.SendRawAsync(originalBytes, requestId, ct);
            AssertReceipt(replay, WireReceiptStatus.Recorded, "the exact-byte replay after decision retirement");
            Assert.True(replay.Receipt.Replayed);
            Assert.Equal(original.Receipt.CanonicalPayloadSha256, replay.Receipt.CanonicalPayloadSha256);
            Assert.Equal(original.Receipt.EvaluatedStateVersion, replay.Receipt.EvaluatedStateVersion);
            Assert.Equal(0L, client.Store.ControlVersionForTest);

            // The same request id with different bytes stays a conflict even though the
            // decision that accompanied the original request has retired.
            byte[] conflictingBytes = client.BuildCommandBytes(
                CommandKind.ClaimControl,
                1,
                requestId,
                payloadBytes: null,
                decisionId: decision);
            WireEnvelope conflict = await client.SendRawAsync(conflictingBytes, requestId, ct);
            AssertReceipt(conflict, WireReceiptStatus.Rejected, "the different-byte replay after decision retirement");
            Assert.Equal(ProtocolErrorCode.RequestIdConflict, conflict.Receipt.ReasonCode);
            Assert.Equal(0L, client.Store.ControlVersionForTest);

            // A genuinely new request carrying the retired decision id is still rejected
            // deterministically; the replay ordering fix must not loosen this guard.
            const string staleRequestId = "reviewer-new-stale-decision-after-retirement";
            byte[] staleBytes = client.BuildCommandBytes(
                CommandKind.ClaimControl,
                0,
                staleRequestId,
                payloadBytes: null,
                decisionId: decision);
            WireEnvelope stale = await client.SendRawAsync(staleBytes, staleRequestId, ct);
            AssertReceipt(stale, WireReceiptStatus.Rejected, "the new control request with a retired decision");
            Assert.Equal(ProtocolErrorCode.DecisionMismatch, stale.Receipt.ReasonCode);
            Assert.Null(client.Store.GetControlResult(
                SeatAccount,
                new RequestScopeId(client.ScopeId),
                new RequestId(staleRequestId)));
            Assert.Equal(0L, client.Store.ControlVersionForTest);
            TableControlSnapshot control = await client.ControlSnapshotAsync(ct);
            Assert.Equal(0L, control.ControlVersion);
            Assert.Equal(0L, control.Seats[0].ControlEpoch);
        }

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

        [Fact]
        public async Task DroppedStaleControlRejectionReconcilesFromPersistentOutboxOnReconnect()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            string outboxPath = System.IO.Path.Combine(harness.Root, "stale-rejection-outbox");
            var outbox = new FileOutboxStore(outboxPath);
            DropRejectedControlReceiptChannel? dropper = null;
            await using LiveSession live = await LiveSession.OpenAsync(
                harness,
                outbox,
                ct,
                inner => dropper = new DropRejectedControlReceiptChannel(inner, 1));

            PlayerSnapshot snapshot = await live.WaitForDecisionAsync(ct);
            string decisionId = snapshot.PrivateView!.Decision!.DecisionId;
            harness.Clock.MonotonicMillis += 30_000L;
            harness.Clock.UtcNowMs += 30_000L;
            await WaitUntilAsync(
                () => !string.Equals(live.Runtime.ActorForTest.GetPendingDecisionId(0), decisionId, StringComparison.Ordinal),
                "the authoritative decision to retire",
                ct);

            const string requestId = "stale-rejection-reconnect";
            RequestScopeId scope = live.Scope;
            CommandSubmission submission = await live.SubmitControlAsync(
                CommandKind.ReleaseControl,
                0UL,
                requestId,
                new DecisionId(decisionId),
                ct);
            Assert.True(submission.Sent);
            string digest = submission.Command.DigestSha256;

            await WaitUntilAsync(
                () => dropper != null && dropper.DroppedCount == 1 &&
                      live.Session.PendingCommands.Count == 1,
                "the rejected control receipt to be dropped with the outbox still pending",
                ct);
            Assert.True(live.Session.PendingCommands[0].NeedsReconciliation);

            // The rejection must be durable before its receipt is published: the
            // store answers the query with the original reason, status, digest and
            // control kind, and no LOCAL_CONTROL record is fabricated for it.
            TableDurableReceipt? durable = await live.Runtime.StoreForTest.TryGetAsync(
                SeatAccount,
                scope,
                new RequestId(requestId),
                ct);
            Assert.NotNull(durable);
            Assert.Equal(ContractReceiptStatus.Rejected, durable!.Receipt.Status);
            Assert.Equal(ProtocolErrorCode.DecisionMismatch, durable.Receipt.ReasonCode);
            Assert.Equal("RELEASE_CONTROL", durable.Api);
            Assert.Null(durable.Receipt.CommandVersion);
            Assert.Null(durable.Receipt.EventSeq);
            Assert.Equal(
                digest,
                live.Runtime.StoreForTest.GetReceiptDigest(SeatAccount, scope, new RequestId(requestId)));
            Assert.Null(live.Runtime.StoreForTest.GetControlResult(SeatAccount, scope, new RequestId(requestId)));
            Assert.Equal(0L, live.Runtime.StoreForTest.ControlVersionForTest);
            TableControlSnapshot beforeReconnect = await live.ControlSnapshotAsync(ct);
            Assert.Equal(0L, beforeReconnect.ControlVersion);
            Assert.Equal(0L, beforeReconnect.Seats[0].ControlEpoch);

            await live.DisconnectAndReopenAsync(harness, ct);

            TableReceiptResolvedEventArgs resolved = await live.WaitResolutionAsync(
                args => string.Equals(args.Command.RequestId, requestId, StringComparison.Ordinal),
                "the reconnected stale-rejection receipt lookup",
                ct);
            Assert.Equal(OutboxDisposition.Rejected, resolved.Resolution.Disposition);
            Assert.Equal(ContractReceiptStatus.Rejected, resolved.Resolution.ReceiptStatus);
            Assert.Equal(ProtocolErrorCode.DecisionMismatch, resolved.Resolution.ReasonCode);
            Assert.True(resolved.Resolution.FromReceiptLookup, "the reconnect resolution must come from RECEIPT_QUERY");
            Assert.Equal(CommandKind.ReleaseControl, resolved.Command.Kind);
            Assert.Equal(digest, resolved.Command.DigestSha256);
            Assert.Equal(digest, resolved.Resolution.CanonicalPayloadSha256);
            Assert.Empty(live.Session.PendingCommands);

            var rebound = new FileOutboxStore(outboxPath);
            Assert.Empty(await rebound.LoadUnresolvedAsync(ct));

            // The durable record is unchanged by the reconciliation: same receipt,
            // no second record, and zero control movement.
            TableDurableReceipt? afterReconcile = await live.Runtime.StoreForTest.TryGetAsync(
                SeatAccount,
                scope,
                new RequestId(requestId),
                ct);
            Assert.NotNull(afterReconcile);
            Assert.Equal(durable!.CreatedAtUtcMs, afterReconcile!.CreatedAtUtcMs);
            Assert.Equal(durable.ExpiresAtUtcMs, afterReconcile.ExpiresAtUtcMs);
            Assert.Equal(ProtocolErrorCode.DecisionMismatch, afterReconcile.Receipt.ReasonCode);
            Assert.Equal(0L, live.Runtime.StoreForTest.ControlVersionForTest);
            TableControlSnapshot afterReconcileControl = await live.ControlSnapshotAsync(ct);
            Assert.Equal(0L, afterReconcileControl.ControlVersion);
            Assert.Equal(0L, afterReconcileControl.Seats[0].ControlEpoch);
        }

        [Fact]
        public async Task PersistedStaleControlRejectionSurvivesColdProcessRestartUnchanged()
        {
            await using var harness = new P41Harness();
            // Match the crash worker's fixed owner so the save can be read by it.
            harness.Options.OwnerInstanceId = "crash-owner";
            CancellationToken ct = TestContext.Current.CancellationToken;
            string outboxPath = System.IO.Path.Combine(harness.Root, "stale-rejection-cold-outbox");
            var outbox = new FileOutboxStore(outboxPath);
            DropRejectedControlReceiptChannel? dropper = null;
            LiveSession live = await LiveSession.OpenAsync(
                harness,
                outbox,
                ct,
                inner => dropper = new DropRejectedControlReceiptChannel(inner, 1));

            const string requestId = "stale-rejection-cold";
            RequestScopeId scope = live.Scope;
            string digest;
            string roundId;
            string parentPendingDecision;
            string decisionId;
            RoundStateMachine parentRound;
            ulong evaluated;
            ulong createdAt;
            ulong expiresAt;
            ulong stateVersion;
            ulong parentStateVersion;
            ulong lastEventSeq;
            try
            {
                PlayerSnapshot snapshot = await live.WaitForDecisionAsync(ct);
                roundId = snapshot.RoundId;
                decisionId = snapshot.PrivateView!.Decision!.DecisionId;
                var hold = new AiSubmitHold();
                hold.Attach(live.Runtime);
                try
                {
                    harness.Clock.MonotonicMillis += 30_000L;
                    harness.Clock.UtcNowMs += 30_000L;
                    await SettleRetirementUnderAiHoldAsync(
                        live.Runtime,
                        hold,
                        decisionId,
                        expectHeldAi: true,
                        "before the cold restart",
                        ct);

                    // With the follow-up chain parked at the submit hold the
                    // actor's committed truth is frozen: no autonomous commit
                    // source remains (later follow-ups queue behind the held
                    // submit, fresh decisions carry fresh deadlines, the human
                    // decision opens only after the held AI submits). The
                    // authoritative rejection below must leave that truth
                    // exactly where it is.
                    TableSnapshot frozen = await live.Runtime.ActorForTest.GetSnapshotAsync(ct);

                    CommandSubmission submission = await live.SubmitControlAsync(
                        CommandKind.ClaimControl,
                        0UL,
                        requestId,
                        new DecisionId(decisionId),
                        ct);
                    Assert.True(submission.Sent);
                    digest = submission.Command.DigestSha256;
                    await WaitUntilAsync(
                        () => dropper != null && dropper.DroppedCount == 1 &&
                              live.Session.PendingCommands.Count == 1,
                        "the cold-restart stale rejection receipt to be dropped",
                        ct);

                    TableDurableReceipt? durable = await live.Runtime.StoreForTest.TryGetAsync(
                        SeatAccount,
                        scope,
                        new RequestId(requestId),
                        ct);
                    Assert.NotNull(durable);
                    Assert.Equal(ContractReceiptStatus.Rejected, durable!.Receipt.Status);
                    Assert.Equal(ProtocolErrorCode.DecisionMismatch, durable.Receipt.ReasonCode);
                    Assert.Equal("CLAIM_CONTROL", durable.Api);
                    evaluated = durable.Receipt.EvaluatedStateVersion.Value;
                    createdAt = durable.CreatedAtUtcMs;
                    expiresAt = durable.ExpiresAtUtcMs;
                    Assert.Equal(0L, live.Runtime.StoreForTest.ControlVersionForTest);
                    TableStateRow afterReject = (await live.Runtime.StoreForTest.LoadStateAsync(P41Harness.RoomId, ct))!;
                    Assert.Equal(frozen.StateVersion.Value, afterReject.StateVersion.Value);
                    Assert.Equal(frozen.LastEventSeq.Value, afterReject.LastEventSeq.Value);
                }
                finally
                {
                    hold.Release();
                    hold.Detach(live.Runtime);
                }

                // The released follow-up chain returns the turn to the human seat
                // exactly when every autonomous commit has causally landed; the
                // cold process then recovers this same pending decision from the
                // disk checkpoint.
                await WaitUntilFollowUpsSettleAsync(live.Runtime, "before the cold restart", ct);
                parentRound = live.Runtime.ActorForTest.GetRoundStateForHost();
                Assert.Equal(0, parentRound.CurrentSeat);
                TableSnapshot parentSnapshot = await live.Runtime.ActorForTest.GetSnapshotAsync(ct);
                parentStateVersion = parentSnapshot.StateVersion.Value;
                parentPendingDecision = live.Runtime.ActorForTest.GetPendingDecisionId(0)!;
                Assert.Equal(
                    "local-human-" +
                    parentStateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    "-0-" + parentRound.Phase.ToString(),
                    parentPendingDecision);

                // The durable row still lags the released cascade (only receipt
                // persists flush it), so one more authoritative stale-decision
                // rejection performs the final full save: the checkpoint handed
                // to the cold process is the post-cascade committed cut, whose
                // open decision is the same one recorded below. Exact stillness
                // across the cold replay does not rest on the parent side — the
                // worker runs its runtime with AutoPlay=false and a frozen clock,
                // so no autonomous commit source exists after the restart by
                // construction.
                const string flushRequestId = "pre-cold-flush";
                CommandSubmission flushSubmission = await live.SubmitControlAsync(
                    CommandKind.ClaimControl,
                    0UL,
                    flushRequestId,
                    new DecisionId(decisionId),
                    ct);
                Assert.True(flushSubmission.Sent);
                TableReceiptResolvedEventArgs flushResolved = await live.WaitResolutionAsync(
                    args => string.Equals(args.Command.RequestId, flushRequestId, StringComparison.Ordinal),
                    "the pre-cold flush rejection receipt",
                    ct);
                Assert.Equal(OutboxDisposition.Rejected, flushResolved.Resolution.Disposition);
                Assert.Equal(ProtocolErrorCode.DecisionMismatch, flushResolved.Resolution.ReasonCode);
            }
            finally
            {
                await live.DisposeAsync();
            }

            // The cold process will read the committed save, so the baseline the
            // restart must preserve is the durable state on disk (the live
            // runtime's in-memory view can sit ahead of it) — read it through the
            // real store exactly like the recovery-boundary evidence does.
            using (LocalTableStore baseline = await LocalTableStore.OpenAsync(
                harness.Options,
                harness.Clock,
                null,
                ct))
            {
                TableStateRow state = (await baseline.LoadStateAsync(P41Harness.RoomId, ct))!;
                // This durable cut is the checkpoint the cold process recovers;
                // its version/seq values feed the worker's exact-stillness
                // markers below.
                stateVersion = state.StateVersion.Value;
                lastEventSeq = state.LastEventSeq.Value;
                TableDurableReceipt? durableOnDisk = await baseline.TryGetAsync(
                    SeatAccount,
                    scope,
                    new RequestId(requestId),
                    ct);
                Assert.NotNull(durableOnDisk);
                Assert.Equal(createdAt, durableOnDisk!.CreatedAtUtcMs);
                Assert.Equal(expiresAt, durableOnDisk.ExpiresAtUtcMs);
                Assert.Equal(evaluated, durableOnDisk.Receipt.EvaluatedStateVersion.Value);
                Assert.Equal(0L, baseline.ControlVersionForTest);
            }

            // The cold cut must be the same committed state/seat the parent sampled, not an
            // older cascade anchor whose version only happens to share a pending id.
            Assert.Equal(parentStateVersion, stateVersion);

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
            string marker = System.IO.Path.Combine(harness.Root, "stale-rejection-marker.txt");
            System.Diagnostics.ProcessStartInfo start = CreateWorkerStartInfo(
                workerDll,
                harness.Options.SaveDirectory,
                P41Harness.RoomId,
                marker,
                "rejected-receipt-cold-session",
                harness.Clock.MonotonicMillis.ToString(System.Globalization.CultureInfo.InvariantCulture),
                harness.Clock.UtcNowMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                SeatAccount,
                scope.Value,
                requestId,
                decisionId,
                outboxPath);
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
                    throw new TimeoutException("The cold-process rejected-receipt reader did not exit in time.");
                }

                Assert.True(
                    process.ExitCode == 0,
                    "The cold-process rejected-receipt reader failed: exit=" + process.ExitCode +
                    " stderr=" + await stderr);
            }

            string[] lines = File.ReadAllLines(marker);
            // Evidence seam: the harness deletes its temp root on dispose, so a
            // delivery run may ask this test to keep the worker's raw marker
            // (the values below are still asserted verbatim; this only archives
            // the artifact and changes no assertion).
            string? evidenceDir = Environment.GetEnvironmentVariable("LH02_EVIDENCE_DIR");
            if (!string.IsNullOrEmpty(evidenceDir))
            {
                Directory.CreateDirectory(evidenceDir);
                File.WriteAllLines(Path.Combine(evidenceDir, "cold-worker-marker.txt"), lines);
            }

            string MarkerValue(string key)
            {
                string? line = lines.FirstOrDefault(candidate => candidate.StartsWith(key, StringComparison.Ordinal));
                Assert.True(line != null, "the cold-session marker is missing " + key);
                return line[key.Length..];
            }

            string[] expectedLines =
            {
                "mode=rejected-receipt-cold-session",
                "receipt_found=1",
                "receipt_status=Rejected",
                "receipt_reason=" + ProtocolErrorCode.DecisionMismatch,
                "receipt_api=CLAIM_CONTROL",
                "receipt_evaluated=" + evaluated.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "receipt_created=" + createdAt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "receipt_expires=" + expiresAt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "receipt_command_version_present=0",
                "receipt_event_seq_present=0",
                "digest_hex=" + digest,
                "state_version=" + stateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "last_event_seq=" + lastEventSeq.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "query_status=Found",
                "query_receipt_status=Rejected",
                "query_receipt_reason=" + ProtocolErrorCode.DecisionMismatch,
                "query_digest=" + digest,
                // Key-isolation honesty markers: the cold-issued scope under the
                // same request id is a NEW idempotency key (every Hello issues a
                // fresh scope and the binding gate rejects any other scope), so
                // these two cold submissions prove isolation, not original-key
                // replay; the original key is covered by the read-side queries.
                "replay_key_scope=cold-issued",
                "original_key_cold_command_replay=unreachable",
                "newkey1_replayed=0",
                "newkey1_status=Rejected",
                "newkey1_reason=" + ProtocolErrorCode.DecisionMismatch,
                "newkey1_digest=" + digest,
                "newkey2_replayed=1",
                "newkey2_reason=" + ProtocolErrorCode.DecisionMismatch,
                "newkey2_digest=" + digest,
                "newkey2_evaluated=0",
                "query2_status=Found",
                "query2_receipt_status=Rejected",
                "query2_reason=" + ProtocolErrorCode.DecisionMismatch,
                "query2_digest=" + digest,
                "query2_evaluated=" + evaluated.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "original_created_after=" + createdAt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "original_status_after=Rejected",
                "original_reason_after=" + ProtocolErrorCode.DecisionMismatch,
                "original_digest_after=" + digest,
                "original_evaluated_after=" + evaluated.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "final_state_version=" + stateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "final_last_event_seq=" + lastEventSeq.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "final_control_version=0",
                "final_control_record_present=0",
                "client_disposition=" + OutboxDisposition.Rejected,
                "client_reason=" + ProtocolErrorCode.DecisionMismatch,
                "client_digest=" + digest,
                "client_kind=ClaimControl",
                "client_from_lookup=1",
                "client_pending=0",
                "client_unresolved=0"
            };
            foreach (string expectedLine in expectedLines)
            {
                Assert.Contains(expectedLine, lines);
            }

            // The cold session's own identity boundaries: it recovered (not a fresh
            // room), bound a freshly issued scope (a pre-restart scope cannot carry
            // commands — which is precisely why the original key cannot be
            // command-replayed on the cold wire; the worker labels that honestly),
            // recovered the same round at the same pending decision, and the
            // cold-scope new-key submission landed at evaluated version 0 exactly
            // like the envelope's ExpectedStateVersion.
            Assert.True(
                ulong.Parse(MarkerValue("recovery_generation="), System.Globalization.CultureInfo.InvariantCulture) > 0UL,
                "the worker runtime must be a cold recovery");
            string coldScopeValue = MarkerValue("cold_scope=");
            Assert.False(string.IsNullOrEmpty(coldScopeValue), "the cold scope id must be present");
            Assert.NotEqual(scope.Value, coldScopeValue);
            Assert.Equal(roundId, MarkerValue("cold_round_id="));
            Assert.Equal(parentPendingDecision, MarkerValue("cold_pending_decision="));
            Assert.Equal(
                "local-human-" + MarkerValue("state_version=") + "-0-" + parentRound.Phase.ToString(),
                MarkerValue("cold_pending_decision="));
            Assert.Equal("cold-issued", MarkerValue("replay_key_scope="));
            Assert.Equal("unreachable", MarkerValue("original_key_cold_command_replay="));
            Assert.Equal("0", MarkerValue("newkey1_evaluated="));
            Assert.Equal(
                MarkerValue("newkey1_evaluated="),
                MarkerValue("newkey2_evaluated="));
            Assert.Equal(
                createdAt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                MarkerValue("original_created_after="));
            Assert.Equal(
                evaluated.ToString(System.Globalization.CultureInfo.InvariantCulture),
                MarkerValue("original_evaluated_after="));
            Assert.Equal(
                evaluated.ToString(System.Globalization.CultureInfo.InvariantCulture),
                MarkerValue("query2_evaluated="));
            ulong newKeyRecordCreated = ulong.Parse(
                MarkerValue("newkey_record_created="),
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(newKeyRecordCreated > 0UL, "the cold-scope new-key record must exist with a real creation timestamp");
            // The two records are distinct by KEY (fresh cold scope vs original
            // scope), not by creation timestamp: the worker clock is frozen at
            // the parent's pinned value, so both records legitimately share one
            // creation timestamp. Their coexistence at their own keys is asserted
            // in the final disk-truth block below.

            // Independent parent-side disk truth after the cold process exited:
            // state/event and control where the baseline left them, the original
            // record field-identical (the cold-scope new-key writes must not have
            // touched it), the cold-scope record present exactly once with the
            // same request id and digest, and the outbox fully resolved.
            using (LocalTableStore finalDisk = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, null, ct))
            {
                TableStateRow finalState = (await finalDisk.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(stateVersion, finalState.StateVersion.Value);
                Assert.Equal(lastEventSeq, finalState.LastEventSeq.Value);
                TableDurableReceipt? original = await finalDisk.TryGetAsync(
                    SeatAccount,
                    scope,
                    new RequestId(requestId),
                    ct);
                Assert.NotNull(original);
                Assert.Equal(createdAt, original!.CreatedAtUtcMs);
                Assert.Equal(expiresAt, original.ExpiresAtUtcMs);
                Assert.Equal(ContractReceiptStatus.Rejected, original.Receipt.Status);
                Assert.Equal(ProtocolErrorCode.DecisionMismatch, original.Receipt.ReasonCode);
                Assert.Equal(evaluated, original.Receipt.EvaluatedStateVersion.Value);
                Assert.Equal(digest, finalDisk.GetReceiptDigest(SeatAccount, scope, new RequestId(requestId)));
                var coldScopeId = new RequestScopeId(coldScopeValue);
                TableDurableReceipt? coldRecord = await finalDisk.TryGetAsync(
                    SeatAccount,
                    coldScopeId,
                    new RequestId(requestId),
                    ct);
                Assert.NotNull(coldRecord);
                Assert.Equal(newKeyRecordCreated, coldRecord!.CreatedAtUtcMs);
                Assert.Equal(ProtocolErrorCode.DecisionMismatch, coldRecord.Receipt.ReasonCode);
                Assert.Equal("CLAIM_CONTROL", coldRecord.Api);
                Assert.Equal(
                    digest,
                    finalDisk.GetReceiptDigest(SeatAccount, coldScopeId, new RequestId(requestId)));
                Assert.Null(finalDisk.GetControlResult(SeatAccount, coldScopeId, new RequestId(requestId)));
                Assert.Equal(0L, finalDisk.ControlVersionForTest);
            }

            Assert.Empty(await new FileOutboxStore(outboxPath).LoadUnresolvedAsync(ct));
        }

        [Fact]
        public async Task PersistedStaleControlRejectionExactReplayAndConflictKeepSingleRecord()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            await using RawControlConnection client = await RawControlConnection.OpenAsync(harness, ct);
            RequestScopeId scope = new RequestScopeId(client.ScopeId);

            await WaitUntilAsync(
                () => !string.IsNullOrEmpty(client.Runtime.ActorForTest.GetPendingDecisionId(0)),
                "a live decision to retire",
                ct);
            string decision = client.Runtime.ActorForTest.GetPendingDecisionId(0)!;
            harness.Clock.MonotonicMillis += 30_000L;
            harness.Clock.UtcNowMs += 30_000L;
            await WaitUntilAsync(
                () => !string.Equals(client.Runtime.ActorForTest.GetPendingDecisionId(0), decision, StringComparison.Ordinal),
                "the decision to retire",
                ct);

            const string requestId = "persisted-rejection-replay";
            byte[] staleBytes = client.BuildCommandBytes(
                CommandKind.ReleaseControl,
                0,
                requestId,
                payloadBytes: null,
                decisionId: decision);
            WireEnvelope rejected = await client.SendRawAsync(staleBytes, requestId, ct);
            AssertReceipt(rejected, WireReceiptStatus.Rejected, "the stale-decision ReleaseControl");
            Assert.Equal(ProtocolErrorCode.DecisionMismatch, rejected.Receipt.ReasonCode);
            string digest = rejected.Receipt.CanonicalPayloadSha256;

            TableDurableReceipt? first = await client.Store.TryGetAsync(SeatAccount, scope, new RequestId(requestId), ct);
            Assert.NotNull(first);
            ulong createdAt = first!.CreatedAtUtcMs;

            // The exact same bytes replay the stored rejection; the record is not
            // rewritten and no second record appears.
            WireEnvelope replay = await client.SendRawAsync(staleBytes, requestId, ct);
            AssertReceipt(replay, WireReceiptStatus.Rejected, "the exact-byte replay of the stale rejection");
            Assert.Equal(ProtocolErrorCode.DecisionMismatch, replay.Receipt.ReasonCode);
            Assert.True(replay.Receipt.Replayed, "the replayed rejection must be marked as replayed");
            Assert.Equal(digest, replay.Receipt.CanonicalPayloadSha256);
            Assert.Equal(rejected.Receipt.EvaluatedStateVersion, replay.Receipt.EvaluatedStateVersion);
            TableDurableReceipt? afterReplay = await client.Store.TryGetAsync(SeatAccount, scope, new RequestId(requestId), ct);
            Assert.NotNull(afterReplay);
            Assert.Equal(createdAt, afterReplay!.CreatedAtUtcMs);

            // Different bytes under the same request id conflict without touching
            // the durable record.
            byte[] conflictingBytes = client.BuildCommandBytes(
                CommandKind.ReleaseControl,
                1,
                requestId,
                payloadBytes: null,
                decisionId: decision);
            WireEnvelope conflict = await client.SendRawAsync(conflictingBytes, requestId, ct);
            AssertReceipt(conflict, WireReceiptStatus.Rejected, "the different-byte replay");
            Assert.Equal(ProtocolErrorCode.RequestIdConflict, conflict.Receipt.ReasonCode);
            TableDurableReceipt? afterConflict = await client.Store.TryGetAsync(SeatAccount, scope, new RequestId(requestId), ct);
            Assert.NotNull(afterConflict);
            Assert.Equal(createdAt, afterConflict!.CreatedAtUtcMs);
            Assert.Equal(ProtocolErrorCode.DecisionMismatch, afterConflict.Receipt.ReasonCode);

            // A different control kind under the same request id conflicts too.
            byte[] otherKindBytes = client.BuildCommandBytes(
                CommandKind.ClaimControl,
                0,
                requestId,
                payloadBytes: null,
                decisionId: decision);
            WireEnvelope otherKind = await client.SendRawAsync(otherKindBytes, requestId, ct);
            AssertReceipt(otherKind, WireReceiptStatus.Rejected, "the different-kind replay");
            Assert.Equal(ProtocolErrorCode.RequestIdConflict, otherKind.Receipt.ReasonCode);
            TableDurableReceipt? afterOtherKind = await client.Store.TryGetAsync(SeatAccount, scope, new RequestId(requestId), ct);
            Assert.NotNull(afterOtherKind);
            Assert.Equal(createdAt, afterOtherKind!.CreatedAtUtcMs);
            Assert.Equal("RELEASE_CONTROL", afterOtherKind.Api);

            Assert.Equal(0L, client.Store.ControlVersionForTest);
            TableControlSnapshot control = await client.ControlSnapshotAsync(ct);
            Assert.Equal(0L, control.ControlVersion);
            Assert.Equal(0L, control.Seats[0].ControlEpoch);
        }

        [Fact]
        public async Task AppliedCommandReplayAfterDecisionRetirementReplaysInsteadOfStaleRejection()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            await using RawControlConnection client = await RawControlConnection.OpenAsync(harness, ct);
            RequestScopeId scope = new RequestScopeId(client.ScopeId);

            await WaitUntilAsync(
                () => !string.IsNullOrEmpty(client.Runtime.ActorForTest.GetPendingDecisionId(0)),
                "a live decision before the applied command",
                ct);
            string decision = client.Runtime.ActorForTest.GetPendingDecisionId(0)!;

            RoundStateMachine round = client.Runtime.ActorForTest.GetRoundStateForHost();
            TileId handTile = round.Tiles.TilesInZone(TileZone.Hand, 0)[0];
            BusinessPayload actionPayload = CommandPayloadFactory.SubmitAction(
                round.RoundId,
                "raw-applied",
                0UL,
                ProtocolActionType.DiscardTile,
                tileIds: new[] { (uint)handTile.Value });
            byte[] actionBytes = actionPayload.CopyBytes();

            const string requestId = "applied-then-retired";
            byte[] originalBytes = client.BuildCommandBytes(
                CommandKind.SubmitAction,
                0,
                requestId,
                actionBytes,
                decisionId: null);
            WireEnvelope original = await client.SendRawAsync(originalBytes, requestId, ct);
            AssertReceipt(original, WireReceiptStatus.Applied, "the original discard with a live decision");
            Assert.False(original.Receipt.Replayed);

            TableDurableReceipt? applied = await client.Store.TryGetAsync(SeatAccount, scope, new RequestId(requestId), ct);
            Assert.NotNull(applied);
            Assert.Equal(ContractReceiptStatus.Applied, applied!.Receipt.Status);
            Assert.Equal("SUBMIT_ACTION", applied.Api);
            ulong createdAt = applied.CreatedAtUtcMs;

            var hold = new AiSubmitHold();
            hold.Attach(client.Runtime);
            try
            {
                harness.Clock.MonotonicMillis += 30_000L;
                harness.Clock.UtcNowMs += 30_000L;
                await SettleRetirementUnderAiHoldAsync(
                    client.Runtime,
                    hold,
                    decision,
                    expectHeldAi: true,
                    "after the applied command",
                    ct);

                // Frozen authoritative truth under the hold: the replay and the
                // stale rejection below must leave it exactly where it is.
                TableSnapshot frozen = await client.Runtime.ActorForTest.GetSnapshotAsync(ct);

                // The exact same request replayed after its decision retired must replay
                // the committed APPLIED receipt; it must never be re-judged as a stale
                // decision rejection just because the early check runs first.
                byte[] replayBytes = client.BuildCommandBytes(
                    CommandKind.SubmitAction,
                    0,
                    requestId,
                    actionBytes,
                    decisionId: decision);
                WireEnvelope replay = await client.SendRawAsync(replayBytes, requestId, ct);
                AssertReceipt(replay, WireReceiptStatus.Applied, "the exact replay after decision retirement");
                Assert.True(replay.Receipt.Replayed, "the applied replay must be marked as replayed");
                Assert.Equal(original.Receipt.CanonicalPayloadSha256, replay.Receipt.CanonicalPayloadSha256);
                Assert.Equal(original.Receipt.EvaluatedStateVersion, replay.Receipt.EvaluatedStateVersion);
                TableDurableReceipt? afterReplay = await client.Store.TryGetAsync(SeatAccount, scope, new RequestId(requestId), ct);
                Assert.NotNull(afterReplay);
                Assert.Equal(ContractReceiptStatus.Applied, afterReplay!.Receipt.Status);
                Assert.Equal(createdAt, afterReplay.CreatedAtUtcMs);
                TableStateRow replayedState = (await client.Store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(frozen.StateVersion.Value, replayedState.StateVersion.Value);
                Assert.Equal(frozen.LastEventSeq.Value, replayedState.LastEventSeq.Value);

                // A genuinely new normal command carrying the retired decision id is
                // still rejected deterministically — and that rejection is durable now.
                RoundStateMachine freshRound = client.Runtime.ActorForTest.GetRoundStateForHost();
                TileId nextTile = freshRound.Tiles.TilesInZone(TileZone.Hand, 0)[0];
                if (nextTile == handTile)
                {
                    IReadOnlyList<TileId> hand = freshRound.Tiles.TilesInZone(TileZone.Hand, 0);
                    nextTile = hand.Count > 1 ? hand[1] : hand[0];
                }

                BusinessPayload stalePayload = CommandPayloadFactory.SubmitAction(
                    freshRound.RoundId,
                    "raw-stale",
                    0UL,
                    ProtocolActionType.DiscardTile,
                    tileIds: new[] { (uint)nextTile.Value });
                const string staleRequestId = "stale-normal-new";
                byte[] staleBytes = client.BuildCommandBytes(
                    CommandKind.SubmitAction,
                    0,
                    staleRequestId,
                    stalePayload.CopyBytes(),
                    decisionId: decision);
                WireEnvelope stale = await client.SendRawAsync(staleBytes, staleRequestId, ct);
                AssertReceipt(stale, WireReceiptStatus.Rejected, "the new normal command with a retired decision");
                Assert.Equal(ProtocolErrorCode.DecisionMismatch, stale.Receipt.ReasonCode);
                TableDurableReceipt? staleDurable = await client.Store.TryGetAsync(
                    SeatAccount,
                    scope,
                    new RequestId(staleRequestId),
                    ct);
                Assert.NotNull(staleDurable);
                Assert.Equal(ContractReceiptStatus.Rejected, staleDurable!.Receipt.Status);
                Assert.Equal(ProtocolErrorCode.DecisionMismatch, staleDurable.Receipt.ReasonCode);
                Assert.Equal("SUBMIT_ACTION", staleDurable.Api);
                Assert.Equal(
                    stale.Receipt.CanonicalPayloadSha256,
                    client.Store.GetReceiptDigest(SeatAccount, scope, new RequestId(staleRequestId)));
                TableStateRow afterStale = (await client.Store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(frozen.StateVersion.Value, afterStale.StateVersion.Value);
                Assert.Equal(frozen.LastEventSeq.Value, afterStale.LastEventSeq.Value);
                Assert.Equal(0L, client.Store.ControlVersionForTest);
            }
            finally
            {
                hold.Release();
                hold.Detach(client.Runtime);
            }
        }

        [Fact]
        public async Task StaleNormalCommandRejectionReconcilesFromPersistentOutboxOnReconnect()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            string outboxPath = System.IO.Path.Combine(harness.Root, "stale-normal-rejection-outbox");
            const string requestId = "stale-normal-rejection";
            string digest;
            var outbox = new FileOutboxStore(outboxPath);
            DropRejectedControlReceiptChannel? dropper = null;
            await using LiveSession live = await LiveSession.OpenAsync(
                harness,
                outbox,
                ct,
                inner => dropper = new DropRejectedControlReceiptChannel(inner, 1));

            PlayerSnapshot snapshot = await live.WaitForDecisionAsync(ct);
            string decisionId = snapshot.PrivateView!.Decision!.DecisionId;
            var hold = new AiSubmitHold();
            hold.Attach(live.Runtime);
            try
            {
                harness.Clock.MonotonicMillis += 30_000L;
                harness.Clock.UtcNowMs += 30_000L;
                await SettleRetirementUnderAiHoldAsync(
                    live.Runtime,
                    hold,
                    decisionId,
                    expectHeldAi: true,
                    "before the rejected normal command",
                    ct);

                // Frozen authoritative truth under the hold: the rejection under
                // test must not move it any further.
                TableSnapshot frozen = await live.Runtime.ActorForTest.GetSnapshotAsync(ct);

                RequestScopeId scope = live.Scope;
                CommandSubmission submission = await live.SubmitActionAsync(requestId, new DecisionId(decisionId), ct);
                Assert.True(submission.Sent);
                digest = submission.Command.DigestSha256;

                await WaitUntilAsync(
                    () => dropper != null && dropper.DroppedCount == 1 &&
                          live.Session.PendingCommands.Count == 1,
                    "the rejected normal-command receipt to be dropped with the outbox still pending",
                    ct);

                TableDurableReceipt? durable = await live.Runtime.StoreForTest.TryGetAsync(
                    SeatAccount,
                    scope,
                    new RequestId(requestId),
                    ct);
                Assert.NotNull(durable);
                Assert.Equal(ContractReceiptStatus.Rejected, durable!.Receipt.Status);
                Assert.Equal(ProtocolErrorCode.DecisionMismatch, durable.Receipt.ReasonCode);
                Assert.Equal("SUBMIT_ACTION", durable.Api);
                Assert.Equal(
                    digest,
                    live.Runtime.StoreForTest.GetReceiptDigest(SeatAccount, scope, new RequestId(requestId)));
                TableStateRow afterReject = (await live.Runtime.StoreForTest.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(frozen.StateVersion.Value, afterReject.StateVersion.Value);
                Assert.Equal(frozen.LastEventSeq.Value, afterReject.LastEventSeq.Value);
            }
            finally
            {
                hold.Release();
                hold.Detach(live.Runtime);
            }

            // The released chain returns the turn to the human seat before the
            // reconnect below opens a fresh session against the same runtime.
            await WaitUntilFollowUpsSettleAsync(live.Runtime, "before the reconnect", ct);

            await live.DisconnectAndReopenAsync(harness, ct);

            TableReceiptResolvedEventArgs resolved = await live.WaitResolutionAsync(
                args => string.Equals(args.Command.RequestId, requestId, StringComparison.Ordinal),
                "the reconnected stale normal-command receipt lookup",
                ct);
            Assert.Equal(OutboxDisposition.Rejected, resolved.Resolution.Disposition);
            Assert.Equal(ProtocolErrorCode.DecisionMismatch, resolved.Resolution.ReasonCode);
            Assert.True(resolved.Resolution.FromReceiptLookup);
            Assert.Equal(CommandKind.SubmitAction, resolved.Command.Kind);
            Assert.Equal(digest, resolved.Resolution.CanonicalPayloadSha256);
            Assert.Empty(live.Session.PendingCommands);
            Assert.Empty(await new FileOutboxStore(outboxPath).LoadUnresolvedAsync(ct));
        }

        [Fact]
        public async Task ExpiredScopeStaleDecisionStaysTransportOnlyWithoutDurableRecord()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            await using RawControlConnection client = await RawControlConnection.OpenAsync(harness, ct);
            RequestScopeId scope = new RequestScopeId(client.ScopeId);

            await WaitUntilAsync(
                () => !string.IsNullOrEmpty(client.Runtime.ActorForTest.GetPendingDecisionId(0)),
                "a live decision to retire",
                ct);
            string decision = client.Runtime.ActorForTest.GetPendingDecisionId(0)!;

            // One clock advance expires the issued scope (24h) and retires the
            // decision (authoritative deadline). The authorization boundary runs
            // before the stale-decision rejection, so an expired scope can never
            // borrow the durable rejection path.
            harness.Clock.MonotonicMillis += (long)TimeSpan.FromHours(24).TotalMilliseconds + 30_000L;
            harness.Clock.UtcNowMs += (long)TimeSpan.FromHours(24).TotalMilliseconds + 30_000L;
            await WaitUntilAsync(
                () => !string.Equals(client.Runtime.ActorForTest.GetPendingDecisionId(0), decision, StringComparison.Ordinal),
                "the decision to retire with the scope expiry",
                ct);

            const string requestId = "expired-stale-claim";
            byte[] expiredBytes = client.BuildCommandBytes(
                CommandKind.ClaimControl,
                0,
                requestId,
                payloadBytes: null,
                decisionId: decision);
            WireEnvelope rejected = await client.SendRawAsync(expiredBytes, requestId, ct);
            AssertReceipt(rejected, WireReceiptStatus.Rejected, "the expired-scope stale-decision ClaimControl");
            Assert.Equal(ProtocolErrorCode.RequestScopeExpired, rejected.Receipt.ReasonCode);

            // Transport-only: no durable receipt, no digest, no control movement,
            // and the receipt query finds nothing.
            Assert.Null(await client.Store.TryGetAsync(SeatAccount, scope, new RequestId(requestId), ct));
            Assert.Null(client.Store.GetReceiptDigest(SeatAccount, scope, new RequestId(requestId)));
            Assert.Null(client.Store.GetControlResult(SeatAccount, scope, new RequestId(requestId)));
            Assert.Equal(0L, client.Store.ControlVersionForTest);
            WireEnvelope lookup = await client.SendReceiptQueryAsync(
                scope.Value,
                requestId,
                "expired-scope-lookup",
                ct);
            Assert.Equal(WireLookupStatus.NotFound, lookup.ReceiptLookup!.Status);
        }

        [Fact]
        public async Task ExpiredOrRevokedScopeCannotPersistRejectionOnEitherCommandPath()
        {
            // Four-cell probe (control/normal x expired/revoked), one fresh harness
            // per cell: an unauthorized scope must not borrow the durable rejection
            // path on either command path — its answer stays transport-only and the
            // idempotency store stays empty for the request.
            await ExpiredOrRevokedScopeCellAsync(control: true, revoke: false);
            await ExpiredOrRevokedScopeCellAsync(control: true, revoke: true);
            await ExpiredOrRevokedScopeCellAsync(control: false, revoke: false);
            await ExpiredOrRevokedScopeCellAsync(control: false, revoke: true);
        }

        private static async Task ExpiredOrRevokedScopeCellAsync(bool control, bool revoke)
        {
            string cell = (control ? "control" : "normal") + "/" + (revoke ? "revoked" : "expired");
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            await using RawControlConnection client = await RawControlConnection.OpenAsync(harness, ct);
            RequestScopeId scope = new RequestScopeId(client.ScopeId);

            await WaitUntilAsync(
                () => !string.IsNullOrEmpty(client.Runtime.ActorForTest.GetPendingDecisionId(0)),
                "a live decision to retire (" + cell + ")",
                ct);
            string decision = client.Runtime.ActorForTest.GetPendingDecisionId(0)!;
            var hold = new AiSubmitHold();
            hold.Attach(client.Runtime);
            TableSnapshot? frozen = null;
            TableStateRow? parkedBaseline = null;
            try
            {
                if (revoke)
                {
                    harness.Clock.MonotonicMillis += 30_000L;
                    harness.Clock.UtcNowMs += 30_000L;
                    await SettleRetirementUnderAiHoldAsync(
                        client.Runtime,
                        hold,
                        decision,
                        expectHeldAi: true,
                        "before the revocation (" + cell + ")",
                        ct);

                    // Frozen authoritative truth under the hold; the revocation
                    // and the probe below must leave it exactly where it is.
                    frozen = await client.Runtime.ActorForTest.GetSnapshotAsync(ct);
                    Assert.True(
                        client.Store.Scopes.Revoke(new RequestScopeId(client.ScopeId), (ulong)harness.Clock.UtcNowMs),
                        "the scope must be revocable for the " + cell + " cell");
                }
                else
                {
                    // One advance expires the 24h scope and retires the decision.
                    // The expiry cascade consumes the due decision in one atomic
                    // actor operation and parks the cheat window without opening
                    // any AI decision, so no follow-up ever reaches the hold and
                    // nothing commits afterwards; the durable row is the frozen
                    // baseline for the transport-only probe below.
                    harness.Clock.MonotonicMillis += (long)TimeSpan.FromHours(24).TotalMilliseconds + 30_000L;
                    harness.Clock.UtcNowMs += (long)TimeSpan.FromHours(24).TotalMilliseconds + 30_000L;
                    await SettleRetirementUnderAiHoldAsync(
                        client.Runtime,
                        hold,
                        decision,
                        expectHeldAi: false,
                        "with the scope expiry (" + cell + ")",
                        ct);
                    parkedBaseline = (await client.Store.LoadStateAsync(P41Harness.RoomId, ct))!;
                }
            }
            finally
            {
                hold.Release();
                hold.Detach(client.Runtime);
            }

            const string requestId = "probe-unauthorized";
            WireEnvelope rejected;
            if (control)
            {
                byte[] bytes = client.BuildCommandBytes(
                    CommandKind.ClaimControl,
                    0,
                    requestId,
                    payloadBytes: null,
                    decisionId: decision);
                rejected = await client.SendRawAsync(bytes, requestId, ct);
            }
            else
            {
                RoundStateMachine round = client.Runtime.ActorForTest.GetRoundStateForHost();
                TileId handTile = round.Tiles.TilesInZone(TileZone.Hand, 0)[0];
                BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                    round.RoundId,
                    "probe-unauthorized",
                    0UL,
                    ProtocolActionType.DiscardTile,
                    tileIds: new[] { (uint)handTile.Value });
                byte[] bytes = client.BuildCommandBytes(
                    CommandKind.SubmitAction,
                    0,
                    requestId,
                    payload.CopyBytes(),
                    decisionId: decision);
                rejected = await client.SendRawAsync(bytes, requestId, ct);
            }

            AssertReceipt(rejected, WireReceiptStatus.Rejected, "the " + cell + " stale-decision command");
            Assert.Equal(ProtocolErrorCode.RequestScopeExpired, rejected.Receipt.ReasonCode);

            // Transport-only: nothing durable under the request key on either path,
            // no control movement, no state movement, and the receipt query that a
            // real outbox entry would send finds nothing.
            Assert.Null(await client.Store.TryGetAsync(SeatAccount, scope, new RequestId(requestId), ct));
            Assert.Null(client.Store.GetReceiptDigest(SeatAccount, scope, new RequestId(requestId)));
            Assert.Null(client.Store.GetControlResult(SeatAccount, scope, new RequestId(requestId)));
            Assert.Equal(0L, client.Store.ControlVersionForTest);
            TableStateRow after = (await client.Store.LoadStateAsync(P41Harness.RoomId, ct))!;
            if (frozen != null)
            {
                Assert.Equal(frozen.StateVersion.Value, after.StateVersion.Value);
                Assert.Equal(frozen.LastEventSeq.Value, after.LastEventSeq.Value);
            }
            else
            {
                Assert.NotNull(parkedBaseline);
                Assert.Equal(parkedBaseline!.StateVersion.Value, after.StateVersion.Value);
                Assert.Equal(parkedBaseline.LastEventSeq.Value, after.LastEventSeq.Value);
            }

            WireEnvelope probeLookup = await client.SendReceiptQueryAsync(
                scope.Value,
                requestId,
                "probe-unauthorized-lookup",
                ct);
            Assert.Equal(WireLookupStatus.NotFound, probeLookup.ReceiptLookup!.Status);
        }

        [Fact]
        public async Task ForeignScopeCommandStaysUnauthenticatedWithoutDurableRecord()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            await using RawControlConnection client = await RawControlConnection.OpenAsync(harness, ct);
            RequestScopeId scope = new RequestScopeId(client.ScopeId);

            await WaitUntilAsync(
                () => !string.IsNullOrEmpty(client.Runtime.ActorForTest.GetPendingDecisionId(0)),
                "a live decision to retire for the foreign-scope probe",
                ct);
            string decision = client.Runtime.ActorForTest.GetPendingDecisionId(0)!;
            var hold = new AiSubmitHold();
            hold.Attach(client.Runtime);
            try
            {
                harness.Clock.MonotonicMillis += 30_000L;
                harness.Clock.UtcNowMs += 30_000L;
                await SettleRetirementUnderAiHoldAsync(
                    client.Runtime,
                    hold,
                    decision,
                    expectHeldAi: true,
                    "the foreign-scope probe",
                    ct);

                // Frozen authoritative truth under the hold: the transport-only
                // foreign probes and the own-scope durable rejection must leave
                // it exactly where it is.
                TableSnapshot frozen = await client.Runtime.ActorForTest.GetSnapshotAsync(ct);

            byte[] BuildForeignBytes(CommandKind kind, byte[]? payloadBytes, string requestId)
            {
                var foreignScope = new WireScope
                {
                    ScopeId = "foreign-probe-scope",
                    AccountId = SeatAccount
                };
                BusinessPayload payload = payloadBytes == null
                    ? (kind == CommandKind.ClaimControl
                        ? CommandPayloadFactory.ClaimControl(client.RoundId, 0UL)
                        : CommandPayloadFactory.ReleaseControl(client.RoundId, 0UL))
                    : BusinessPayload.Retain(payloadBytes);
                var envelope = new WireEnvelope
                {
                    ProtocolMajor = ProtocolConstants.Major,
                    ProtocolMinor = ProtocolConstants.Minor,
                    RulesetVersion = ProtocolConstants.RulesetVersion,
                    ProjectionVersion = ProtocolConstants.ProjectionVersion,
                    MessageType = WireMessageType.Command,
                    SessionId = client.Welcome.SessionId,
                    RoomId = P41Harness.RoomId,
                    SeatId = client.Welcome.SeatId,
                    BindingEpoch = client.Welcome.BindingEpoch,
                    RequestId = requestId,
                    Scope = foreignScope,
                    ControlEpoch = 0UL,
                    OwnerEpoch = client.Welcome.Welcome.OwnerEpoch,
                    Command = new WireCommand
                    {
                        Type = (WireCommandType)(int)kind,
                        CanonicalPayload = ByteString.CopyFrom(payload.CopyBytes()),
                        PayloadSchemaVersion = 1U,
                        ExpectedStateVersion = 0UL,
                        ExpectedRoundId = client.RoundId,
                        ExpectedDecisionId = decision
                    }
                };
                return WireCodec.Encode(envelope);
            }

            const string controlRequestId = "foreign-scope-control";
            await client.Channel.SendAsync(
                BuildForeignBytes(CommandKind.ClaimControl, null, controlRequestId),
                ct);
            WireEnvelope controlAnswer = await ReceiveEnvelopeAsync(
                client.Channel,
                envelope => envelope.Error != null ||
                            (envelope.Receipt != null &&
                             string.Equals(envelope.RequestId, controlRequestId, StringComparison.Ordinal)),
                "the foreign-scope control answer",
                ct);
            Assert.NotNull(controlAnswer.Error);
            Assert.Equal(ProtocolErrorCode.Unauthenticated, controlAnswer.Error.Code);

            RoundStateMachine round = client.Runtime.ActorForTest.GetRoundStateForHost();
            TileId handTile = round.Tiles.TilesInZone(TileZone.Hand, 0)[0];
            BusinessPayload actionPayload = CommandPayloadFactory.SubmitAction(
                round.RoundId,
                "foreign-scope-normal",
                0UL,
                ProtocolActionType.DiscardTile,
                tileIds: new[] { (uint)handTile.Value });
            const string normalRequestId = "foreign-scope-normal";
            await client.Channel.SendAsync(
                BuildForeignBytes(CommandKind.SubmitAction, actionPayload.CopyBytes(), normalRequestId),
                ct);
            WireEnvelope normalAnswer = await ReceiveEnvelopeAsync(
                client.Channel,
                envelope => envelope.Error != null ||
                            (envelope.Receipt != null &&
                             string.Equals(envelope.RequestId, normalRequestId, StringComparison.Ordinal)),
                "the foreign-scope normal answer",
                ct);
            Assert.NotNull(normalAnswer.Error);
            Assert.Equal(ProtocolErrorCode.Unauthenticated, normalAnswer.Error.Code);

            // Nothing durable under either request id, and the queries a real
            // outbox entry would send find nothing.
            foreach (string requestId in new[] { controlRequestId, normalRequestId })
            {
                Assert.Null(await client.Store.TryGetAsync(SeatAccount, scope, new RequestId(requestId), ct));
                Assert.Null(client.Store.GetReceiptDigest(SeatAccount, scope, new RequestId(requestId)));
                Assert.Null(client.Store.GetControlResult(SeatAccount, scope, new RequestId(requestId)));
                WireEnvelope probeLookup = await client.SendReceiptQueryAsync(
                    scope.Value,
                    requestId,
                    "foreign-lookup-" + requestId,
                    ct);
                Assert.Equal(WireLookupStatus.NotFound, probeLookup.ReceiptLookup!.Status);
            }

            // The rejected attempts did not consume the request ids either: the
            // same control id sent under the true scope is judged on its own
            // merits (a durable stale-decision rejection), not as a conflict.
            byte[] ownBytes = client.BuildCommandBytes(
                CommandKind.ClaimControl,
                0,
                controlRequestId,
                payloadBytes: null,
                decisionId: decision);
            WireEnvelope own = await client.SendRawAsync(ownBytes, controlRequestId, ct);
            AssertReceipt(own, WireReceiptStatus.Rejected, "the same request id under the true scope");
            Assert.Equal(ProtocolErrorCode.DecisionMismatch, own.Receipt.ReasonCode);
            Assert.False(own.Receipt.Replayed);
            TableDurableReceipt? ownDurable = await client.Store.TryGetAsync(
                SeatAccount,
                scope,
                new RequestId(controlRequestId),
                ct);
            Assert.NotNull(ownDurable);
            Assert.Equal(ContractReceiptStatus.Rejected, ownDurable!.Receipt.Status);

            TableStateRow after = (await client.Store.LoadStateAsync(P41Harness.RoomId, ct))!;
            Assert.Equal(frozen.StateVersion.Value, after.StateVersion.Value);
            Assert.Equal(frozen.LastEventSeq.Value, after.LastEventSeq.Value);
            Assert.Equal(0L, client.Store.ControlVersionForTest);
            }
            finally
            {
                hold.Release();
                hold.Detach(client.Runtime);
            }
        }

        [Fact]
        public async Task CancelledStaleCommandBeforeLinearizationWritesNothingAndKeyStaysReusable()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            // Gates the atomic writer's Move/Replace installs so later cells can
            // hold a caller inside the runtime's non-cancellable persist segment
            // and inside the control gate that the persisting request owns.
            var gfs = new GatedFileSystem();
            harness.Options.FileSystemForTest = gfs;
            await using RawControlConnection client = await RawControlConnection.OpenAsync(harness, ct);
            RequestScopeId scope = new RequestScopeId(client.ScopeId);

            await WaitUntilAsync(
                () => !string.IsNullOrEmpty(client.Runtime.ActorForTest.GetPendingDecisionId(0)),
                "a live decision to retire before the cancelled attempt",
                ct);
            string decision = client.Runtime.ActorForTest.GetPendingDecisionId(0)!;
            var hold = new AiSubmitHold();
            hold.Attach(client.Runtime);
            try
            {
                harness.Clock.MonotonicMillis += 30_000L;
                harness.Clock.UtcNowMs += 30_000L;
                await SettleRetirementUnderAiHoldAsync(
                    client.Runtime,
                    hold,
                    decision,
                    expectHeldAi: true,
                    "before the cancelled attempts",
                    ct);

                // Under the hold no autonomous commit source exists (the parked
                // AI follow-up and everything sequenced behind it cannot
                // commit), so the actor snapshot taken here is frozen truth.
                // The durable state row lags the actor and is flushed only by
                // the next receipt persist — the committed stale claim below
                // performs that full save — so the row must never serve as the
                // state baseline; every durable-row equality below is checked
                // against this snapshot.
                TableSnapshot frozen = await client.Runtime.ActorForTest.GetSnapshotAsync(ct);
                TableStateRow preAttemptRow = (await client.Store.LoadStateAsync(P41Harness.RoomId, ct))!;

                const string requestId = "cancelled-stale-claim";
                byte[] staleBytes = client.BuildCommandBytes(
                    CommandKind.ClaimControl,
                    0,
                    requestId,
                    payloadBytes: null,
                    decisionId: decision);

                // The runtime's SendAsync checks the caller token as its very first
                // statement — the pre-linearization cancellation boundary. The
                // cancelled call must observe OperationCanceledException and write
                // nothing at all.
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => client.Runtime.SendAsync(staleBytes, new CancellationToken(canceled: true)));

                Assert.Null(await client.Store.TryGetAsync(SeatAccount, scope, new RequestId(requestId), ct));
                Assert.Null(client.Store.GetReceiptDigest(SeatAccount, scope, new RequestId(requestId)));
                Assert.Null(client.Store.GetControlResult(SeatAccount, scope, new RequestId(requestId)));
                Assert.Equal(0L, client.Store.ControlVersionForTest);
                TableStateRow afterCancel = (await client.Store.LoadStateAsync(P41Harness.RoomId, ct))!;
                // The cancelled call linearizes nothing, and under the hold no
                // other persist trigger exists, so the durable row must stay
                // exactly at its pre-attempt value.
                Assert.Equal(preAttemptRow.StateVersion.Value, afterCancel.StateVersion.Value);
                Assert.Equal(preAttemptRow.LastEventSeq.Value, afterCancel.LastEventSeq.Value);
                WireEnvelope cancelledLookup = await client.SendReceiptQueryAsync(
                    scope.Value,
                    requestId,
                    "cancelled-lookup",
                    ct);
                Assert.Equal(WireLookupStatus.NotFound, cancelledLookup.ReceiptLookup!.Status);

                // The cancelled attempt neither wrote a record nor consumed the
                // idempotency key: the same bytes sent to completion commit the
                // durable rejection exactly once...
                WireEnvelope committed = await client.SendRawAsync(staleBytes, requestId, ct);
                AssertReceipt(committed, WireReceiptStatus.Rejected, "the completed stale-decision ClaimControl");
                Assert.Equal(ProtocolErrorCode.DecisionMismatch, committed.Receipt.ReasonCode);
                Assert.False(committed.Receipt.Replayed);
                TableDurableReceipt? durable = await client.Store.TryGetAsync(SeatAccount, scope, new RequestId(requestId), ct);
                Assert.NotNull(durable);
                Assert.Equal(ContractReceiptStatus.Rejected, durable!.Receipt.Status);
                ulong createdAt = durable.CreatedAtUtcMs;

                // ...and from the commit point onward the persist→publish segment runs
                // under CancellationToken.None by design: the record is queryable and
                // an identical resend replays it without a second record.
                WireEnvelope replay = await client.SendRawAsync(staleBytes, requestId, ct);
                AssertReceipt(replay, WireReceiptStatus.Rejected, "the post-commit identical resend");
                Assert.True(replay.Receipt.Replayed);
                Assert.Equal(committed.Receipt.CanonicalPayloadSha256, replay.Receipt.CanonicalPayloadSha256);
                TableDurableReceipt? afterReplay = await client.Store.TryGetAsync(SeatAccount, scope, new RequestId(requestId), ct);
                Assert.NotNull(afterReplay);
                Assert.Equal(createdAt, afterReplay!.CreatedAtUtcMs);
                Assert.Equal(0L, client.Store.ControlVersionForTest);
                TableStateRow afterReplayState = (await client.Store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(frozen.StateVersion.Value, afterReplayState.StateVersion.Value);
                Assert.Equal(frozen.LastEventSeq.Value, afterReplayState.LastEventSeq.Value);

                // --- Boundary 2: cancellation while ENTERED PROCESSING but not yet
                // linearized. Request A (gated) holds the control gate from inside its
                // non-cancellable persist (blocked at the gated Move/Replace), so the
                // parked request waits at the control gate — the first await of the
                // control path (HandleControlCommandAsync), because every pre-gate
                // statement (decode, binding, payload parse) runs synchronously. The
                // direct call below returns incomplete exactly because the request
                // parked at the contended gate; cancelling its token there observes
                // the cancellation and must write nothing, leaving the key reusable.
                const string gatedRequestId = "cancelled-request-holding-gate";
                const string parkedRequestId = "cancelled-parked-at-control-gate";
                byte[] gatedBytes = client.BuildCommandBytes(
                    CommandKind.ClaimControl,
                    0,
                    gatedRequestId,
                    payloadBytes: null,
                    decisionId: decision);
                byte[] parkedBytes = client.BuildCommandBytes(
                    CommandKind.ClaimControl,
                    0,
                    parkedRequestId,
                    payloadBytes: null,
                    decisionId: decision);

                gfs.Arm();
    #pragma warning disable xUnit1051 // the gated request must NOT be cancellable via the test-wide token; its boundary is driven by the file-system gate below
                Task gatedTask = Task.Run(() => client.Runtime.SendAsync(gatedBytes, DetachedToken));
    #pragma warning restore xUnit1051
                await gfs.WaitForEnteredAsync(ct);

                try
                {
                    using (CancellationTokenSource parkedCts = new CancellationTokenSource())
                    {
                        Task parkedTask = client.Runtime.SendAsync(parkedBytes, parkedCts.Token);
                        Assert.False(
                            parkedTask.IsCompleted,
                            "the request must still be waiting at the contended control gate.");
                        parkedCts.Cancel();
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parkedTask);
                    }

                    // No store reads happened while the persisting request owned the
                    // store lock; open the gate and let the gated request commit.
                }
                finally
                {
                    gfs.OpenGate();
                }

                await gatedTask;
                TableDurableReceipt? gatedDurable = await client.Store.TryGetAsync(
                    SeatAccount,
                    scope,
                    new RequestId(gatedRequestId),
                    ct);
                Assert.NotNull(gatedDurable);
                Assert.Equal(ContractReceiptStatus.Rejected, gatedDurable!.Receipt.Status);
                Assert.Equal(ProtocolErrorCode.DecisionMismatch, gatedDurable.Receipt.ReasonCode);
                Assert.False(gatedDurable.Receipt.Replayed);
                Assert.Null(await client.Store.TryGetAsync(SeatAccount, scope, new RequestId(parkedRequestId), ct));
                Assert.Null(client.Store.GetReceiptDigest(SeatAccount, scope, new RequestId(parkedRequestId)));
                Assert.Null(client.Store.GetControlResult(SeatAccount, scope, new RequestId(parkedRequestId)));
                WireEnvelope parkedLookup = await client.SendReceiptQueryAsync(
                    scope.Value,
                    parkedRequestId,
                    "parked-at-gate-lookup",
                    ct);
                Assert.Equal(WireLookupStatus.NotFound, parkedLookup.ReceiptLookup!.Status);
                TableStateRow afterGateCell = (await client.Store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(frozen.StateVersion.Value, afterGateCell.StateVersion.Value);
                Assert.Equal(frozen.LastEventSeq.Value, afterGateCell.LastEventSeq.Value);

                // The parked attempt neither wrote a record nor consumed the key.
                byte[] parkedRetryBytes = client.BuildCommandBytes(
                    CommandKind.ClaimControl,
                    0,
                    parkedRequestId,
                    payloadBytes: null,
                    decisionId: decision);
                WireEnvelope parkedRetry = await client.SendRawAsync(parkedRetryBytes, parkedRequestId, ct);
                AssertReceipt(parkedRetry, WireReceiptStatus.Rejected, "the retried request after the gate-boundary cancellation");
                Assert.Equal(ProtocolErrorCode.DecisionMismatch, parkedRetry.Receipt.ReasonCode);
                Assert.False(parkedRetry.Receipt.Replayed);
                TableDurableReceipt? parkedRetryDurable = await client.Store.TryGetAsync(
                    SeatAccount,
                    scope,
                    new RequestId(parkedRequestId),
                    ct);
                Assert.NotNull(parkedRetryDurable);
                Assert.Equal(ContractReceiptStatus.Rejected, parkedRetryDurable!.Receipt.Status);

                // --- Boundary 3: real cancellation INSIDE the non-cancellable
                // persist→publish segment. The caller's token fires while the
                // rejection commit is blocked in the atomic writer — the segment runs
                // under CancellationToken.None by design
                // (LocalTableStore.TryCommitRejectedReceipt → Persist). The commit
                // must stand: the final rejection is published, the record is
                // queryable, exactly one record exists for the key, and an identical
                // resend replays it.
                const string lateRequestId = "cancelled-inside-commit-segment";
                byte[] lateBytes = client.BuildCommandBytes(
                    CommandKind.ClaimControl,
                    0,
                    lateRequestId,
                    payloadBytes: null,
                    decisionId: decision);

                gfs.Arm();
                WireEnvelope? lateAnswer = null;
                try
                {
                    using (CancellationTokenSource lateCts = new CancellationTokenSource())
                    {
    #pragma warning disable xUnit1051 // this request's caller token (lateCts) is the boundary under test; the receive uses a deliberately detached token
                        Task lateTask = Task.Run(async () =>
                        {
                            await client.Runtime.SendAsync(lateBytes, lateCts.Token);
                            lateAnswer = await client.ReceiveCommandResultAsync(lateRequestId, DetachedToken);
                        });
    #pragma warning restore xUnit1051
                        await gfs.WaitForEnteredAsync(ct);
                        lateCts.Cancel();
                        gfs.OpenGate();
                        await lateTask;
                    }

                    Assert.NotNull(lateAnswer);
                    AssertReceipt(
                        lateAnswer!,
                        WireReceiptStatus.Rejected,
                        "the rejection whose caller token fired inside the commit segment");
                    Assert.Equal(ProtocolErrorCode.DecisionMismatch, lateAnswer!.Receipt!.ReasonCode);
                    Assert.False(lateAnswer.Receipt.Replayed);
                }
                finally
                {
                    gfs.OpenGate();
                    gfs.Disarm();
                }

                TableDurableReceipt? lateDurable = await client.Store.TryGetAsync(
                    SeatAccount,
                    scope,
                    new RequestId(lateRequestId),
                    ct);
                Assert.NotNull(lateDurable);
                Assert.Equal(ContractReceiptStatus.Rejected, lateDurable!.Receipt.Status);
                Assert.Equal(ProtocolErrorCode.DecisionMismatch, lateDurable.Receipt.ReasonCode);
                ulong lateCreatedAt = lateDurable.CreatedAtUtcMs;
                WireEnvelope lateLookup = await client.SendReceiptQueryAsync(
                    scope.Value,
                    lateRequestId,
                    "inside-commit-segment-lookup",
                    ct);
                Assert.Equal(WireLookupStatus.Found, lateLookup.ReceiptLookup!.Status);
                Assert.Equal(
                    lateAnswer!.Receipt!.CanonicalPayloadSha256,
                    client.Store.GetReceiptDigest(SeatAccount, scope, new RequestId(lateRequestId)));

                WireEnvelope lateReplay = await client.SendRawAsync(lateBytes, lateRequestId, ct);
                AssertReceipt(lateReplay, WireReceiptStatus.Rejected, "the identical resend after the in-segment cancellation");
                Assert.True(lateReplay.Receipt.Replayed);
                Assert.Equal(lateAnswer.Receipt.CanonicalPayloadSha256, lateReplay.Receipt.CanonicalPayloadSha256);
                TableDurableReceipt? afterLateReplay = await client.Store.TryGetAsync(
                    SeatAccount,
                    scope,
                    new RequestId(lateRequestId),
                    ct);
                Assert.NotNull(afterLateReplay);
                Assert.Equal(lateCreatedAt, afterLateReplay!.CreatedAtUtcMs);

                TableStateRow afterLate = (await client.Store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(frozen.StateVersion.Value, afterLate.StateVersion.Value);
                Assert.Equal(frozen.LastEventSeq.Value, afterLate.LastEventSeq.Value);
                Assert.Equal(0L, client.Store.ControlVersionForTest);
            }
            finally
            {
                hold.Release();
                hold.Detach(client.Runtime);
            }

            // Releasing the hold lands the deferred autonomous commits; no
            // assertion below depends on them, but the cascade must finish
            // before the harness disposes the runtime.
            await WaitUntilFollowUpsSettleAsync(client.Runtime, "the released follow-ups after the cancellation cells", ct);
        }

        [Fact]
        public async Task HeldAiFollowUpIsNotDeclaredSettledAndItsLateCommitLandsAfterRelease()
        {
            // Formal regression for the reviewer probe (D-LH02-R1-02): while an AI
            // follow-up sits chosen-but-unsubmitted inside the submit hold, the
            // settle boundary must never treat the cascade as settled — the held
            // AI is a live in-flight autonomous commit source. The stale-decision
            // probe below runs under exactly that condition; releasing the hold
            // then deterministically lands the deferred autonomous commit, which
            // the removed 5x25ms stillness gate ignored.
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            await using RawControlConnection client = await RawControlConnection.OpenAsync(harness, ct);
            RequestScopeId scope = new RequestScopeId(client.ScopeId);

            await WaitUntilAsync(
                () => !string.IsNullOrEmpty(client.Runtime.ActorForTest.GetPendingDecisionId(0)),
                "a live decision to retire under the held-AI probe",
                ct);
            string decision = client.Runtime.ActorForTest.GetPendingDecisionId(0)!;

            const string probeRequestId = "held-ai-probe-rejection";
            ulong heldVersion;
            ulong heldEventSeq;
            ulong probeCreatedAt;
            var hold = new AiSubmitHold();
            hold.Attach(client.Runtime);
            try
            {
                harness.Clock.MonotonicMillis += 30_000L;
                harness.Clock.UtcNowMs += 30_000L;
                await SettleRetirementUnderAiHoldAsync(
                    client.Runtime,
                    hold,
                    decision,
                    expectHeldAi: true,
                    "the held-AI probe",
                    ct);
                Assert.True(
                    hold.IsEntered,
                    "an AI follow-up must be held chosen-but-unsubmitted at the probe boundary.");

                // Probe WHILE the AI is held: a durable stale-decision rejection
                // lands under the held AI and the authoritative state stays
                // frozen at the held snapshot. The snapshot — not the lagging
                // durable row — is the baseline: the probe's receipt persist
                // performs a full save that flushes the row exactly to it.
                TableSnapshot heldBaseline = await client.Runtime.ActorForTest.GetSnapshotAsync(ct);
                heldVersion = heldBaseline.StateVersion.Value;
                heldEventSeq = heldBaseline.LastEventSeq.Value;
                byte[] probeBytes = client.BuildCommandBytes(
                    CommandKind.ClaimControl,
                    0,
                    probeRequestId,
                    payloadBytes: null,
                    decisionId: decision);
                WireEnvelope probe = await client.SendRawAsync(probeBytes, probeRequestId, ct);
                AssertReceipt(probe, WireReceiptStatus.Rejected, "the stale-decision claim under the held AI");
                Assert.Equal(ProtocolErrorCode.DecisionMismatch, probe.Receipt.ReasonCode);
                Assert.False(probe.Receipt.Replayed);
                TableDurableReceipt? probeDurable = await client.Store.TryGetAsync(
                    SeatAccount,
                    scope,
                    new RequestId(probeRequestId),
                    ct);
                Assert.NotNull(probeDurable);
                Assert.Equal(ContractReceiptStatus.Rejected, probeDurable!.Receipt.Status);
                probeCreatedAt = probeDurable.CreatedAtUtcMs;
                TableStateRow afterProbe = (await client.Store.LoadStateAsync(P41Harness.RoomId, ct))!;
                Assert.Equal(heldVersion, afterProbe.StateVersion.Value);
                Assert.Equal(heldEventSeq, afterProbe.LastEventSeq.Value);

            }
            finally
            {
                hold.Release();
            }

            // Releasing the hold deterministically lands the deferred autonomous
            // commits (the held AI plus its follow-ups) — the exact movement the
            // old heuristic silently ignored. The turn-return milestone is
            // causally after the last automatic commit, so the strict state
            // advance below is a deterministic fact, not a poll.
            hold.Detach(client.Runtime);
            await WaitUntilFollowUpsSettleAsync(client.Runtime, "the released held-AI follow-ups", ct);

            TableStateRow afterRelease = (await client.Store.LoadStateAsync(P41Harness.RoomId, ct))!;
            Assert.True(
                afterRelease.StateVersion.Value > heldVersion,
                "the held AI follow-up must land its autonomous commits after the release (held v" +
                heldVersion + ", after release v" + afterRelease.StateVersion.Value + ").");

            // The rejection recorded under the held AI is untouched by the late
            // autonomous commits, and the control ledger never moved.
            TableDurableReceipt? probeAfter = await client.Store.TryGetAsync(
                SeatAccount,
                scope,
                new RequestId(probeRequestId),
                ct);
            Assert.NotNull(probeAfter);
            Assert.Equal(ContractReceiptStatus.Rejected, probeAfter!.Receipt.Status);
            Assert.Equal(ProtocolErrorCode.DecisionMismatch, probeAfter.Receipt.ReasonCode);
            Assert.Equal(probeCreatedAt, probeAfter.CreatedAtUtcMs);
            Assert.Equal(0L, client.Store.ControlVersionForTest);
        }

        [Fact]
        public async Task ConcurrentStaleControlRejectionsPersistExactlyOncePerRequest()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            await using RawControlConnection client = await RawControlConnection.OpenAsync(harness, ct);
            RequestScopeId scope = new RequestScopeId(client.ScopeId);

            await WaitUntilAsync(
                () => !string.IsNullOrEmpty(client.Runtime.ActorForTest.GetPendingDecisionId(0)),
                "a live decision to retire",
                ct);
            string decision = client.Runtime.ActorForTest.GetPendingDecisionId(0)!;
            harness.Clock.MonotonicMillis += 30_000L;
            harness.Clock.UtcNowMs += 30_000L;
            await WaitUntilAsync(
                () => !string.Equals(client.Runtime.ActorForTest.GetPendingDecisionId(0), decision, StringComparison.Ordinal),
                "the decision to retire",
                ct);

            byte[] claimBytes = client.BuildCommandBytes(
                CommandKind.ClaimControl,
                0,
                "concurrent-stale-claim",
                payloadBytes: null,
                decisionId: decision);
            byte[] releaseBytes = client.BuildCommandBytes(
                CommandKind.ReleaseControl,
                0,
                "concurrent-stale-release",
                payloadBytes: null,
                decisionId: decision);
            await Task.WhenAll(
                client.Channel.SendAsync(claimBytes, ct),
                client.Channel.SendAsync(releaseBytes, ct));
            WireEnvelope first = await client.ReceiveCommandResultAsync(null, ct);
            WireEnvelope second = await client.ReceiveCommandResultAsync(null, ct);
            foreach (WireEnvelope receipt in new[] { first, second })
            {
                Assert.Equal(WireReceiptStatus.Rejected, receipt.Receipt.Status);
                Assert.Equal(ProtocolErrorCode.DecisionMismatch, receipt.Receipt.ReasonCode);
                Assert.False(receipt.Receipt.Replayed);
            }

            TableDurableReceipt? claimDurable = await client.Store.TryGetAsync(
                SeatAccount,
                scope,
                new RequestId("concurrent-stale-claim"),
                ct);
            TableDurableReceipt? releaseDurable = await client.Store.TryGetAsync(
                SeatAccount,
                scope,
                new RequestId("concurrent-stale-release"),
                ct);
            Assert.NotNull(claimDurable);
            Assert.NotNull(releaseDurable);
            Assert.Equal("CLAIM_CONTROL", claimDurable!.Api);
            Assert.Equal("RELEASE_CONTROL", releaseDurable!.Api);
            Assert.NotEqual(claimDurable.PayloadSha256, releaseDurable.PayloadSha256);

            // Two concurrent identical submissions of one stale request race the
            // durable commit: exactly one record is written and the loser replays.
            const string sameRequestId = "concurrent-stale-same";
            byte[] sameBytes = client.BuildCommandBytes(
                CommandKind.ReleaseControl,
                0,
                sameRequestId,
                payloadBytes: null,
                decisionId: decision);
            await Task.WhenAll(
                client.Channel.SendAsync(sameBytes, ct),
                client.Channel.SendAsync(sameBytes, ct));
            WireEnvelope sameFirst = await client.ReceiveCommandResultAsync(sameRequestId, ct);
            WireEnvelope sameSecond = await client.ReceiveCommandResultAsync(sameRequestId, ct);
            foreach (WireEnvelope receipt in new[] { sameFirst, sameSecond })
            {
                Assert.Equal(WireReceiptStatus.Rejected, receipt.Receipt.Status);
                Assert.Equal(ProtocolErrorCode.DecisionMismatch, receipt.Receipt.ReasonCode);
            }

            Assert.Contains(
                new[] { sameFirst, sameSecond },
                receipt => receipt.Receipt.Replayed);
            TableDurableReceipt? sameDurable = await client.Store.TryGetAsync(
                SeatAccount,
                scope,
                new RequestId(sameRequestId),
                ct);
            Assert.NotNull(sameDurable);
            Assert.Equal(sameFirst.Receipt.EvaluatedStateVersion, sameSecond.Receipt.EvaluatedStateVersion);
            Assert.Equal(0L, client.Store.ControlVersionForTest);
            TableControlSnapshot control = await client.ControlSnapshotAsync(ct);
            Assert.Equal(0L, control.ControlVersion);
            Assert.Equal(0L, control.Seats[0].ControlEpoch);
        }

        [Fact]
        public async Task RejectedReceiptCommitFailureLeavesOutcomeUnresolved()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            var faulting = new FaultingFileSystem();
            harness.Options.FileSystemForTest = faulting;
            string outboxPath = System.IO.Path.Combine(harness.Root, "stale-rejection-fault-outbox");
            var outbox = new FileOutboxStore(outboxPath);
            LiveSession live = await LiveSession.OpenAsync(harness, outbox, ct);
            RequestScopeId scope = live.Scope;
            const string requestId = "write-fail-rejection";
            try
            {
                PlayerSnapshot snapshot = await live.WaitForDecisionAsync(ct);
                string decisionId = snapshot.PrivateView!.Decision!.DecisionId;
                harness.Clock.MonotonicMillis += 30_000L;
                harness.Clock.UtcNowMs += 30_000L;
                await WaitUntilAsync(
                    () => !string.Equals(live.Runtime.ActorForTest.GetPendingDecisionId(0), decisionId, StringComparison.Ordinal),
                    "the authoritative decision to retire",
                    ct);

                // The durable commit fails before its atomic install: the runtime
                // must not publish a rejection that claims durability. The client
                // observes a transport error and keeps the entry for explicit
                // reconciliation.
                // A committed save already exists, so the atomic install goes
                // through Replace; arm both hooks to cover either install path.
                faulting.BeforeMove = _ => new IOException("injected rejection write failure");
                faulting.BeforeReplace = _ => new IOException("injected rejection write failure");
                CommandSubmission submission = await live.SubmitControlAsync(
                    CommandKind.ReleaseControl,
                    0UL,
                    requestId,
                    new DecisionId(decisionId),
                    ct);
                Assert.True(submission.Sent);
                await WaitUntilAsync(
                    () => faulting.BeforeMoveHits + faulting.BeforeReplaceHits > 0,
                    "the injected rejection write failure",
                    ct);
                // Disarm immediately so the injected fault cannot leak into an
                // unrelated autopilot write while the assertions run.
                faulting.BeforeMove = null;
                faulting.BeforeReplace = null;

                // The failed commit publishes an internal error; the client
                // session keeps the entry handed-but-unresolved for explicit
                // reconciliation.
                await WaitUntilAsync(
                    () => live.Session.PendingCommands.Count == 1 &&
                          live.Session.PendingCommands[0].NeedsReconciliation,
                    "the client entry to stay unresolved after the write failure",
                    ct);

                // The receipt must not be durably readable. A store that has
                // since observed an unknown-outcome write refuses to claim
                // absence from its stale in-memory view, so accept either the
                // plain null or the explicit unknown-outcome exception.
                async Task<TableDurableReceipt?> ReadReceiptOrThrowAsync()
                {
                    try
                    {
                        return await live.Runtime.StoreForTest.TryGetAsync(SeatAccount, scope, new RequestId(requestId), ct);
                    }
                    catch (LocalTableException exception)
                    {
                        Assert.Contains("the outcome remains unknown", exception.Message);
                        return null;
                    }
                }

                Assert.Null(await ReadReceiptOrThrowAsync());
                Assert.Null(live.Runtime.StoreForTest.GetReceiptDigest(SeatAccount, scope, new RequestId(requestId)));
                Assert.Null(live.Runtime.StoreForTest.GetControlResult(SeatAccount, scope, new RequestId(requestId)));
                Assert.Equal(0L, live.Runtime.StoreForTest.ControlVersionForTest);
                Assert.Single(live.Session.PendingCommands);
                Assert.True(live.Session.PendingCommands[0].NeedsReconciliation);
                Assert.Null(live.Session.PendingCommands[0].LastLookup);
            }
            finally
            {
                faulting.BeforeMove = null;
                faulting.BeforeReplace = null;
                await live.DisposeAsync();
            }

            // A recovered runtime answers the receipt query with NotFound — the
            // failed commit never pretended to be durable — and the entry stays
            // unresolved for explicit reconciliation.
            var reopened = new FileOutboxStore(outboxPath);
            await using LiveSession second = await LiveSession.OpenAsync(harness, reopened, ct);
            for (int i = 0; i < 500 && second.Session.PendingCommands[0].LastLookup == null; i++)
            {
                await Task.Delay(10, ct);
            }

            Assert.Equal(ReceiptLookupStatus.NotFound, second.Session.PendingCommands[0].LastLookup);
            Assert.Single(second.Session.PendingCommands);
            Assert.Null(await second.Runtime.StoreForTest.TryGetAsync(SeatAccount, scope, new RequestId(requestId), ct));
            Assert.Equal(0L, second.Runtime.StoreForTest.ControlVersionForTest);
        }

        [Fact]
        public async Task PersistedRejectionIsScopedToItsAccountAndScope()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            await using RawControlConnection client = await RawControlConnection.OpenAsync(harness, ct);
            RequestScopeId scope = new RequestScopeId(client.ScopeId);

            await WaitUntilAsync(
                () => !string.IsNullOrEmpty(client.Runtime.ActorForTest.GetPendingDecisionId(0)),
                "a live decision to retire",
                ct);
            string decision = client.Runtime.ActorForTest.GetPendingDecisionId(0)!;
            harness.Clock.MonotonicMillis += 30_000L;
            harness.Clock.UtcNowMs += 30_000L;
            await WaitUntilAsync(
                () => !string.Equals(client.Runtime.ActorForTest.GetPendingDecisionId(0), decision, StringComparison.Ordinal),
                "the decision to retire",
                ct);

            const string requestId = "scoped-rejection";
            byte[] staleBytes = client.BuildCommandBytes(
                CommandKind.ReleaseControl,
                0,
                requestId,
                payloadBytes: null,
                decisionId: decision);
            WireEnvelope rejected = await client.SendRawAsync(staleBytes, requestId, ct);
            AssertReceipt(rejected, WireReceiptStatus.Rejected, "the scoped stale-decision ReleaseControl");

            // The issuing account and scope read the rejection back.
            Assert.NotNull(await client.Store.TryGetAsync(SeatAccount, scope, new RequestId(requestId), ct));
            WireEnvelope ownScope = await client.SendReceiptQueryAsync(
                scope.Value,
                requestId,
                "own-scope-lookup",
                ct);
            Assert.Equal(WireLookupStatus.Found, ownScope.ReceiptLookup!.Status);
            Assert.Equal(WireReceiptStatus.Rejected, ownScope.ReceiptLookup!.Receipt!.Status);
            Assert.Equal(ProtocolErrorCode.DecisionMismatch, ownScope.ReceiptLookup!.Receipt!.ReasonCode);
            Assert.Equal(rejected.Receipt.CanonicalPayloadSha256, ownScope.ReceiptLookup!.CanonicalPayloadSha256);

            // A different scope or a different request id finds nothing.
            WireEnvelope foreignScope = await client.SendReceiptQueryAsync(
                "foreign-scope-0000000000000000000000000000000",
                requestId,
                "foreign-scope-lookup",
                ct);
            Assert.Equal(WireLookupStatus.NotFound, foreignScope.ReceiptLookup!.Status);
            WireEnvelope foreignRequest = await client.SendReceiptQueryAsync(
                scope.Value,
                "never-submitted-request",
                "foreign-request-lookup",
                ct);
            Assert.Equal(WireLookupStatus.NotFound, foreignRequest.ReceiptLookup!.Status);
        }

        private static string SeatAccount => "local-" + P41Harness.RoomId + "-seat0";

        /// <summary>
        /// Intentionally detached token for requests whose cancellation boundary
        /// is under test (the gate-boundary and in-segment cells must not be
        /// cancelled by the test-wide token — the caller-side cancellation is
        /// driven by dedicated per-request sources).
        /// </summary>
        private static readonly CancellationToken DetachedToken = CancellationToken.None;

        /// <summary>
        /// Deterministic settle boundary after a decision retirement. The caller
        /// arms <paramref name="hold"/> BEFORE the clock jump. The retirement
        /// transition (decision consumption plus forced action) is one atomic
        /// actor operation, so the pending-change wait below proves it landed.
        /// With the hold armed, every follow-up AI decision parks inside the
        /// submit hook before it can commit, and reaching the hook is the
        /// milestone that proves the chain is parked: later follow-ups are
        /// sequenced behind the held submit, decisions opened by follow-ups carry
        /// fresh deadlines so no actor-internal timeout is due, and the human
        /// seat's decision opens only after the held AI submits. No time
        /// heuristic is involved. The previous 5x25ms state-row stillness gate
        /// was proven unsound by the reviewer probe (D-LH02-R1-02,
        /// ReviewerHeldAiMustNotBeDeclaredQuiescent): a chosen-but-unsubmitted AI
        /// held at this very hook passed the stillness poll and committed right
        /// after the release, so the row freezing for 125ms never proved the
        /// absence of in-flight autonomous commits.
        /// </summary>
        private static async Task SettleRetirementUnderAiHoldAsync(
            LocalTableRuntime runtime,
            AiSubmitHold hold,
            string retiredDecisionId,
            bool expectHeldAi,
            string description,
            CancellationToken ct)
        {
            await WaitUntilAsync(
                () => !string.Equals(runtime.ActorForTest.GetPendingDecisionId(0), retiredDecisionId, StringComparison.Ordinal),
                "the decision to retire (" + description + ")",
                ct);

            if (expectHeldAi)
            {
                for (int i = 0; i < 1_000 && !hold.Entered.IsCompleted; i++)
                {
                    await Task.Delay(10, ct);
                }

                Assert.True(
                    hold.Entered.IsCompleted,
                    "the parked AI follow-up never reached the submit hold (" + description + ").");
            }
        }

        /// <summary>
        /// Milestone for the released 30s-retirement cascade: the driver keeps
        /// reacting (forced action, responses, draws and discards) until the turn
        /// comes back around to the human seat or the round ends. The turn-return
        /// (or round end) is causally after the last automatic commit, so state
        /// baselines read here are frozen without any stillness polling.
        /// </summary>
        private static async Task WaitUntilFollowUpsSettleAsync(LocalTableRuntime runtime, string description, CancellationToken ct)
        {
            await WaitUntilAsync(
                () =>
                {
                    RoundStateMachine round = runtime.ActorForTest.GetRoundStateForHost();
                    return (round.CurrentSeat == 0 &&
                            !string.IsNullOrEmpty(runtime.ActorForTest.GetPendingDecisionId(0))) ||
                           round.IsFinished;
                },
                "the automatic follow-up commits to settle (" + description + ")",
                ct);
        }

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

        /// <summary>
        /// Raw receive that returns the first envelope matching the predicate,
        /// including ERROR envelopes (unlike <see cref="RawControlConnection"/>),
        /// so tests can assert on transport-level rejections.
        /// </summary>
        private static async Task<WireEnvelope> ReceiveEnvelopeAsync(
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
                if (predicate(envelope))
                {
                    return envelope;
                }
            }
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

            public async Task<CommandSubmission> SubmitActionAsync(
                string requestId,
                DecisionId? decisionId,
                CancellationToken ct)
            {
                PlayerSnapshot baseline = Session.PlayerSnapshot ??
                    throw new InvalidOperationException("The session has no player baseline.");
                BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                    baseline.RoundId,
                    decisionId?.Value ?? "local-direct",
                    0UL,
                    ProtocolActionType.DiscardTile,
                    tileIds: new[] { baseline.PrivateView!.ClosedTiles[0] });
                var command = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    payload,
                    new RequestId(requestId),
                    Session.Binding!.Scope.ScopeId,
                    new StateVersion(baseline.StateVersion),
                    new RoundId(baseline.RoundId),
                    decisionId,
                    new ControlEpoch(0));
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

            public async Task<WireEnvelope> SendReceiptQueryAsync(
                string scopeId,
                string requestId,
                string correlation,
                CancellationToken ct)
            {
                var envelope = new WireEnvelope
                {
                    ProtocolMajor = ProtocolConstants.Major,
                    ProtocolMinor = ProtocolConstants.Minor,
                    RulesetVersion = ProtocolConstants.RulesetVersion,
                    ProjectionVersion = ProtocolConstants.ProjectionVersion,
                    MessageType = WireMessageType.ReceiptQuery,
                    SessionId = Welcome.SessionId,
                    RoomId = P41Harness.RoomId,
                    SeatId = Welcome.SeatId,
                    BindingEpoch = Welcome.BindingEpoch,
                    CorrelationId = correlation,
                    ReceiptQuery = new WireReceiptQuery
                    {
                        RequestScopeId = scopeId,
                        RequestId = requestId
                    }
                };
                await Channel.SendAsync(WireCodec.Encode(envelope), ct);
                return await ReceiveUntilAsync(
                    Channel,
                    candidate => candidate.ReceiptLookup != null &&
                                 string.Equals(candidate.CorrelationId, correlation, StringComparison.Ordinal),
                    "the receipt lookup for " + requestId,
                    ct);
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

        /// <summary>
        /// Deterministic park for the automatic AI follow-ups: attaches to the
        /// driver's test hook after an AI decision is chosen but BEFORE its
        /// submit, so no autonomous action can commit while held.
        /// <see cref="Entered"/> proves a follow-up actually reached the hold (an
        /// AI sits chosen-but-unsubmitted, exactly the reviewer probe's
        /// condition), and <see cref="Release"/> resumes it. The completion
        /// callbacks run asynchronously so the parked driver thread and the test
        /// thread never depend on each other's scheduling.
        /// </summary>
        private sealed class AiSubmitHold
        {
            private readonly TaskCompletionSource<bool> _entered =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _release =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Entered => _entered.Task;

            public bool IsEntered => _entered.Task.IsCompleted;

            public void Attach(LocalTableRuntime runtime) =>
                runtime.DriverForTest.BeforeAiSubmitForTest = HoldAsync;

            public void Detach(LocalTableRuntime runtime) =>
                runtime.DriverForTest.BeforeAiSubmitForTest = null;

            public void Release() => _release.TrySetResult(true);

            private async Task HoldAsync(int seat, Mahjong.Ai.AiObservation observation)
            {
                _entered.TrySetResult(true);
                await _release.Task.ConfigureAwait(false);
            }
        }

        /// <summary>
        /// File-system double that can hold the atomic writer's Move/Replace
        /// installs: <see cref="Arm"/> arms the gate, the first gated install
        /// completes <see cref="Entered"/> and blocks inside the caller's persist
        /// (which runs under CancellationToken.None by design), and
        /// <see cref="OpenGate"/> releases it. Used to hold a request INSIDE the
        /// non-cancellable persist→publish segment and inside the control gate
        /// that request owns. The sync block is deadlock-safe: the handler runs
        /// on a pool thread and the gate completes with
        /// RunContinuationsAsynchronously from the test thread.
        /// </summary>
        private sealed class GatedFileSystem : ILocalFileSystem
        {
            private readonly ILocalFileSystem _inner = new SystemLocalFileSystem();
            private TaskCompletionSource<bool>? _entered;
            private TaskCompletionSource<bool>? _release;

            public void Arm()
            {
                _entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public void Disarm()
            {
                _entered = null;
                _release = null;
            }

            public Task Entered => _entered?.Task ??
                throw new InvalidOperationException("The file-system gate is not armed.");

            public async Task WaitForEnteredAsync(CancellationToken ct)
            {
                TaskCompletionSource<bool>? entered = _entered ??
                    throw new InvalidOperationException("The file-system gate is not armed.");
                for (int i = 0; i < 1_000 && !entered.Task.IsCompleted; i++)
                {
                    await Task.Delay(10, ct);
                }

                Assert.True(entered.Task.IsCompleted, "no gated file install reached the armed gate.");
            }

            public void OpenGate() => _release?.TrySetResult(true);

            private void GatePass()
            {
                TaskCompletionSource<bool>? entered = _entered;
                TaskCompletionSource<bool>? release = _release;
                if (entered == null || release == null)
                {
                    return;
                }

                entered.TrySetResult(true);
                release.Task.Wait();
            }

            public bool FileExists(string path) => _inner.FileExists(path);

            public void CreateDirectory(string path) => _inner.CreateDirectory(path);

            public Stream CreateNew(string path) => _inner.CreateNew(path);

            public Stream OpenRead(string path) => _inner.OpenRead(path);

            public Stream OpenExclusive(string path) => _inner.OpenExclusive(path);

            public void FlushToDisk(Stream stream) => _inner.FlushToDisk(stream);

            public void Move(string source, string destination)
            {
                GatePass();
                _inner.Move(source, destination);
            }

            public void Replace(string source, string destination, string backup)
            {
                GatePass();
                _inner.Replace(source, destination, backup);
            }

            public void Copy(string source, string destination, bool overwrite) =>
                _inner.Copy(source, destination, overwrite);

            public void Delete(string path) => _inner.Delete(path);

            public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);
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

        /// <summary>
        /// Drops the first DECISION_MISMATCH rejection receipts so the outbox entry
        /// can only be resolved by a durable receipt query.
        /// </summary>
        private sealed class DropRejectedControlReceiptChannel : ITableChannel
        {
            private readonly ITableChannel _inner;
            private int _remaining;

            public DropRejectedControlReceiptChannel(ITableChannel inner, int dropCount)
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
                        envelope.Receipt.Status == WireReceiptStatus.Rejected &&
                        string.Equals(envelope.Receipt.ReasonCode, ProtocolErrorCode.DecisionMismatch, StringComparison.Ordinal))
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
