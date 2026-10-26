using System;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using PlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;
using Xunit;

namespace Mahjong.LocalHost.Tests
{
    public sealed class LocalControlTests
    {
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
                    var command = new ProtocolCommand(
                        claim ? CommandKind.ClaimControl : CommandKind.ReleaseControl,
                        payload,
                        new RequestId("control-" + i + "-" + Guid.NewGuid().ToString("N")),
                        session.Binding!.Scope.ScopeId,
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
                        Assert.Equal("LOCAL_CONTROL", receipt.Resolution.ReasonCode);
                        Assert.Equal(64, receipt.Resolution.CanonicalPayloadSha256.Length);
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
        }

        private static async Task WaitReadyAsync(TableSession session, CancellationToken ct)
        {
            for (int i = 0; i < 50 && session.State != TableConnectionState.Ready; i++)
            {
                await Task.Delay(10, ct);
            }

            Assert.Equal(TableConnectionState.Ready, session.State);
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
                for (int i = 0; i < 100 && session.PendingCommands.Count == 0 && session.State != TableConnectionState.Faulted; i++)
                {
                    await session.PumpOnceAsync(ct);
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

        private sealed class EmptyControlDigestChannel : ITableChannel
        {
            private readonly ITableChannel _inner;
            public EmptyControlDigestChannel(ITableChannel inner) { _inner = inner; }
            public bool IsOpen => _inner.IsOpen;
            public Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken) => _inner.OpenAsync(request, cancellationToken);
            public Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken) => _inner.SendAsync(wireEnvelope, cancellationToken);
            public Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken) => _inner.CloseAsync(reason, cancellationToken);
            public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
            {
                var bytes = await _inner.ReceiveAsync(cancellationToken);
                var envelope = Mahjong.Contracts.Wire.Envelope.Parser.ParseFrom(bytes.ToArray());
                if (envelope.Receipt != null && string.Equals(envelope.Receipt.ReasonCode, "LOCAL_CONTROL", StringComparison.Ordinal))
                {
                    envelope.Receipt.CanonicalPayloadSha256 = string.Empty;
                    return Mahjong.Contracts.Wire.WireCodec.Encode(envelope);
                }

                return bytes;
            }
        }
    }
}
