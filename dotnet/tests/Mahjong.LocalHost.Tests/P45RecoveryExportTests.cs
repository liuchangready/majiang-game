using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Domain.Cheat;
using Mahjong.LocalHost;
using Mahjong.TableRuntime;
using Xunit;

namespace Mahjong.LocalHost.Tests
{
    public sealed class P45RecoveryExportTests
    {
        [Fact]
        public async Task CaptureUsesOnePersistedVersionAndSurvivesColdReopen()
        {
            await using var harness = new P41Harness();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            LocalTableRecoveryCapture first = await OpenAndCaptureAsync(harness, cts.Token);
            Assert.Equal("0", first.Facts.StateVersion);
            Assert.Equal("0", first.Facts.EventSeq);
            Assert.Equal(P41Harness.RoomId + "-r1", first.Facts.RoundId);
            Assert.Equal(136, first.Facts.Physical.TileCount);
            Assert.Equal(136, first.Facts.Physical.UniqueCount);
            Assert.Empty(first.Facts.Requests);
            Assert.Equal(0, first.Facts.Ledger.EntryCount);
            Assert.Equal("CURRENT_COMMITTED_ACTOR", first.Facts.Ledger.Source);
            Assert.Equal(Sha256Hex(first.CheatStatePayload), first.Facts.CheatStateSha256);
            Assert.Equal(first.AuthoritySaveBytes.Length, first.Facts.AuthoritySaveBytes);
            Assert.Equal(Sha256Hex(first.AuthoritySaveBytes), first.Facts.AuthoritySaveSha256);

            LocalTableRecoveryCapture second = await OpenAndCaptureAsync(harness, cts.Token);
            Assert.Equal(first.Facts.RoundId, second.Facts.RoundId);
            Assert.Equal(first.Facts.StateVersion, second.Facts.StateVersion);
            Assert.Equal(first.Facts.EventSeq, second.Facts.EventSeq);
            Assert.Equal(first.Facts.StateFingerprint, second.Facts.StateFingerprint);
            Assert.Equal(first.Facts.StateHash, second.Facts.StateHash);
            Assert.Equal(first.Facts.Physical.TilesSha256, second.Facts.Physical.TilesSha256);
            Assert.Equal(first.Facts.Ledger.EntriesSha256, second.Facts.Ledger.EntriesSha256);
            Assert.Equal(first.Facts.Ledger.BalancesSha256, second.Facts.Ledger.BalancesSha256);
            Assert.Equal(first.Facts.ControlVersion, second.Facts.ControlVersion);
            Assert.Equal(first.Facts.ControlEpochs, second.Facts.ControlEpochs);
        }

        [Fact]
        public async Task PreparedCaptureRunsBeforeDriverAndIsTakenOnce()
        {
            await using var harness = new P41Harness();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            LocalTableHost.PrepareRecoveryCaptureOnNextOpen(harness.Options.SaveDirectory);
            Assert.True(LocalTableDiagnosticRegistry.HasPendingCapture(harness.Options.SaveDirectory), "pending capture was not registered");
            ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, cts.Token);
            string ticket = LocalTableHost.IssueTicket(harness.Options);
            await channel.OpenAsync(new TableChannelConnectRequest(ticket, "p45-prepared-test", null), cts.Token);
            try
            {
                LocalTableRecoveryCapture capture = await LocalTableHost.TakePreparedRecoveryCaptureAsync(
                    harness.Options.SaveDirectory,
                    cts.Token);
                Assert.Equal("0", capture.Facts.StateVersion);
                Assert.Equal(136, capture.Facts.Physical.TileCount);
                await Assert.ThrowsAsync<LocalTableException>(async () =>
                    await LocalTableHost.TakePreparedRecoveryCaptureAsync(harness.Options.SaveDirectory, cts.Token));
            }
            finally
            {
                await channel.CloseAsync(
                    new TableChannelCloseReason("P45_PREPARED_TEST_CLOSE", true),
                    CancellationToken.None);
            }
        }

        [Fact]
        public async Task TamperedAuthorityEnvelopeIsRejected()
        {
            await using var harness = new P41Harness();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            LocalTableRecoveryCapture capture = await OpenAndCaptureAsync(harness, cts.Token);
            byte[] tampered = (byte[])capture.AuthoritySaveBytes.Clone();
            tampered[tampered.Length - 1] ^= 0x01;
            Assert.Throws<InvalidDataException>(() => LocalTableRecoveryFactsReader.ReadPersistedFacts(tampered));
        }

        [Fact]
        public async Task CurrentCheatAggregateIsRequiredAndCarriesNonEmptyLedger()
        {
            await using var harness = new P41Harness();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            LocalTableRecoveryCapture capture = await OpenAndCaptureAsync(harness, cts.Token);

            // A current cheat binding that no longer equals the base checkpoint must fail
            // closed when no current aggregate is supplied.
            LocalSave stale = LocalSaveCodec.DecodeEnvelope(capture.AuthoritySaveBytes);
            stale.CurrentState.CheatVersion = 999;
            stale.CurrentState.CheatFingerprint = 1234567;
            stale.CurrentState.CheatBound = true;
            byte[] staleBytes = LocalSaveCodec.EncodeEnvelope(stale);
            Assert.Throws<InvalidDataException>(() =>
                LocalTableRecoveryFactsReader.ReadPersistedFacts(staleBytes));

            LocalSave save = LocalSaveCodec.DecodeEnvelope(capture.AuthoritySaveBytes);
            TableCheatLedgerCheckpoint current = TableCheatLedgerCheckpointCodec.Decode(capture.CheatStatePayload);
            CheatState state = current.RestoreCheatState();
            var entry = new CheatLedgerEntry("review-entry", 0, -10, state.Ledger.BalanceOf(0) - 10, "review-ledger");
            var balances = new[] { state.Ledger.BalanceOf(0) - 10, state.Ledger.BalanceOf(1), state.Ledger.BalanceOf(2), state.Ledger.BalanceOf(3) };
            CheatLedger ledger = CheatLedger.FromSnapshot(120, balances, new[] { entry });
            CheatState changed = state.WithLedger(ledger);
            save.CurrentState.CheatVersion = changed.Version;
            save.CurrentState.CheatFingerprint = changed.Fingerprint;
            save.CurrentState.CheatBound = true;
            byte[] changedSave = LocalSaveCodec.EncodeEnvelope(save);
            byte[] changedCheat = TableCheatLedgerCheckpointCodec.Encode(new TableCheatLedgerCheckpoint(changed));

            LocalTableRecoveryFacts facts = LocalTableRecoveryFactsReader.ReadPersistedFacts(changedSave, changedCheat);
            Assert.Equal(1, facts.Ledger.EntryCount);
            Assert.Equal(110, facts.Ledger.Balances[0]);
            Assert.Equal("CURRENT_COMMITTED_ACTOR", facts.Ledger.Source);
            Assert.Equal(Sha256Hex(changedCheat), facts.CheatStateSha256);

            // A stale aggregate that does not match the current binding is rejected.
            Assert.Throws<InvalidDataException>(() =>
                LocalTableRecoveryFactsReader.ReadPersistedFacts(changedSave, capture.CheatStatePayload));
        }

        [Fact]
        public async Task PersistedControlResultsAreExportedWithCanonicalIdentityAndDigest()
        {
            await using var harness = new P41Harness();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            const string digest = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
            LocalTableRecoveryCapture capture = await OpenAndCaptureAsync(harness, cts.Token);
            LocalSave save = LocalSaveCodec.DecodeEnvelope(capture.AuthoritySaveBytes);
            save.ControlResults["control-account\u001fcontrol-scope\u001fcontrol-request"] =
                new LocalControlRecord(7, digest, "CLAIM_CONTROL");
            byte[] encoded = LocalSaveCodec.EncodeEnvelope(save);
            LocalTableRecoveryFacts facts = LocalTableRecoveryFactsReader.ReadPersistedFacts(
                encoded,
                capture.CheatStatePayload);
            Assert.Single(facts.ControlRequests);
            LocalTableRecoveryControlFact control = facts.ControlRequests[0];
            Assert.Equal("control-account", control.AccountId);
            Assert.Equal("control-scope", control.ScopeId);
            Assert.Equal("control-request", control.RequestId);
            Assert.Equal(digest, control.Digest);
            Assert.Equal("CLAIM_CONTROL", control.Kind);
            Assert.Equal("7", control.EvaluatedStateVersion);
        }

        [Fact]
        public async Task ReleaseControlThroughPublicSessionIsPersistedAndExported()
        {
            await using var harness = new P41Harness();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, cts.Token);
            string channelTicket = LocalTableHost.IssueTicket(harness.Options);
            await channel.OpenAsync(new TableChannelConnectRequest(channelTicket, "p45-control-export", null), cts.Token);
            var session = new TableSession(channel, new InMemoryOutboxStore());
            await LocalHostCommandTests.PumpUntilReadyAsync(
                session,
                LocalTableHost.IssueTicket(harness.Options),
                cts.Token);
            try
            {
                PlayerSnapshot baseline = session.PlayerSnapshot!;
                BusinessPayload payload = CommandPayloadFactory.ReleaseControl(baseline.RoundId, 0);
                var requestId = new RequestId("p45-release-control-export");
                var command = new ProtocolCommand(
                    CommandKind.ReleaseControl,
                    payload,
                    requestId,
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(baseline.StateVersion),
                    new RoundId(baseline.RoundId),
                    decisionId: null,
                    controlEpoch: new ControlEpoch(0));
                var resolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<TableReceiptResolvedEventArgs> handler = (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == requestId.Value) { resolved.TrySetResult(args); }
                };
                session.ReceiptResolved += handler;
                using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
#pragma warning disable xUnit1051
                Task pump = Task.Run(async () =>
                {
                    while (!pumpCts.IsCancellationRequested)
                    {
                        try { await session.PumpOnceAsync(pumpCts.Token); }
                        catch (OperationCanceledException) { break; }
                    }
                });
#pragma warning restore xUnit1051
                try
                {
                    CommandSubmission submission = await session.SubmitAsync(command, cts.Token);
                    Assert.True(submission.Sent);
                    TableReceiptResolvedEventArgs receipt = await LocalHostCommandTests.WithTimeoutAsync(resolved.Task, cts.Token);
                    Assert.True(
                        receipt.Resolution.Disposition == OutboxDisposition.Recorded ||
                        receipt.Resolution.Disposition == OutboxDisposition.Applied);

                    LocalTableRecoveryCapture capture = await LocalTableHost.CaptureRecoveryAsync(
                        harness.Options.SaveDirectory,
                        cts.Token);
                    LocalTableRecoveryControlFact control = Assert.Single(capture.Facts.ControlRequests);
                    Assert.Equal(requestId.Value, control.RequestId);
                    Assert.Equal("ReleaseControl", control.Kind);
                    Assert.Equal(
                        CanonicalPayloadDigest.Compute(payload).ToHex().ToUpperInvariant(),
                        control.Digest);
                }
                finally
                {
                    pumpCts.Cancel();
                    try { await pump; } catch { }
                    session.ReceiptResolved -= handler;
                }
            }
            finally
            {
                await channel.CloseAsync(
                    new TableChannelCloseReason("P45_CONTROL_EXPORT_CLOSE", true),
                    CancellationToken.None);
            }
        }

        [Fact]
        public async Task TileMarkThroughPublicSessionExportsCurrentNonEmptyLedger()
        {
            await using var harness = new P41Harness();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, cts.Token);
            string channelTicket = LocalTableHost.IssueTicket(harness.Options);
            await channel.OpenAsync(new TableChannelConnectRequest(channelTicket, "p45-cheat-export", null), cts.Token);
            var session = new TableSession(channel, new InMemoryOutboxStore());
            await LocalHostCommandTests.PumpUntilReadyAsync(
                session,
                LocalTableHost.IssueTicket(harness.Options),
                cts.Token);
            try
            {
                PlayerSnapshot baseline = session.PlayerSnapshot!;
                Assert.True(baseline.PrivateView.ClosedTiles.Count >= 4);
                uint[] tiles = baseline.PrivateView.ClosedTiles.Take(4).ToArray();
                BusinessPayload payload = CommandPayloadFactory.SubmitTileMark(
                    baseline.RoundId,
                    "local-direct",
                    0UL,
                    tiles);
                var requestId = new RequestId("p45-tilemark-export");
                var command = new ProtocolCommand(
                    CommandKind.SubmitTileMark,
                    payload,
                    requestId,
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(baseline.StateVersion),
                    new RoundId(baseline.RoundId),
                    decisionId: null,
                    controlEpoch: new ControlEpoch(0));
                var resolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<TableReceiptResolvedEventArgs> handler = (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == requestId.Value) { resolved.TrySetResult(args); }
                };
                session.ReceiptResolved += handler;
                using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
#pragma warning disable xUnit1051
                Task pump = Task.Run(async () =>
                {
                    while (!pumpCts.IsCancellationRequested)
                    {
                        try { await session.PumpOnceAsync(pumpCts.Token); }
                        catch (OperationCanceledException) { break; }
                    }
                });
#pragma warning restore xUnit1051
                try
                {
                    CommandSubmission submission = await session.SubmitAsync(command, cts.Token);
                    Assert.True(submission.Sent);
                    TableReceiptResolvedEventArgs receipt = await LocalHostCommandTests.WithTimeoutAsync(resolved.Task, cts.Token);
                    Assert.True(
                        receipt.Resolution.Disposition == OutboxDisposition.Recorded ||
                        receipt.Resolution.Disposition == OutboxDisposition.Applied);

                    LocalTableRecoveryCapture capture = await LocalTableHost.CaptureRecoveryAsync(
                        harness.Options.SaveDirectory,
                        cts.Token);
                    Assert.True(capture.Facts.Ledger.EntryCount > 0);
                    Assert.True(capture.Facts.Ledger.Balances[0] < 120);
                    Assert.Equal("CURRENT_COMMITTED_ACTOR", capture.Facts.Ledger.Source);
                }
                finally
                {
                    pumpCts.Cancel();
                    try { await pump; } catch { }
                    session.ReceiptResolved -= handler;
                }
            }
            finally
            {
                await channel.CloseAsync(
                    new TableChannelCloseReason("P45_CHEAT_EXPORT_CLOSE", true),
                    CancellationToken.None);
            }
        }

        private static async Task<LocalTableRecoveryCapture> OpenAndCaptureAsync(
            P41Harness harness,
            CancellationToken cancellationToken)
        {
            ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, cancellationToken);
            string ticket = LocalTableHost.IssueTicket(harness.Options);
            await channel.OpenAsync(
                new TableChannelConnectRequest(ticket, "p45-recovery-test", null),
                cancellationToken);
            try
            {
                return await LocalTableHost.CaptureRecoveryAsync(harness.Options.SaveDirectory, cancellationToken);
            }
            finally
            {
                await channel.CloseAsync(
                    new TableChannelCloseReason("P45_RECOVERY_TEST_CLOSE", true),
                    CancellationToken.None);
            }
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToUpperInvariant();
        }
    }
}
