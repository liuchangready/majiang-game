using System;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Xunit;

namespace Mahjong.LocalHost.Tests
{
    using PlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;

    public sealed class LocalHostCommandTests
    {
        [Fact]
        public async Task DiscardCommandCommitsAndResolvesReceiptOverLoopback()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, ct);
            string channelTicket = LocalTableHost.IssueTicket(harness.Options);
            await channel.OpenAsync(new TableChannelConnectRequest(channelTicket, "p41-test", null), ct);

            var session = new TableSession(channel, new InMemoryOutboxStore());
            string sessionTicket = LocalTableHost.IssueTicket(harness.Options);
            try
            {
                await PumpUntilReadyAsync(session, sessionTicket, ct);
                PlayerSnapshot before = session.PlayerSnapshot!;
                Assert.NotNull(before.PrivateView);
                Assert.NotEmpty(before.PrivateView.ClosedTiles);
                uint tile = before.PrivateView.ClosedTiles[0];

                var resolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var advanced = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.ReceiptResolved += (_, args) => resolved.TrySetResult(args);
                session.SnapshotChanged += (_, _) =>
                {
                    if (session.PlayerSnapshot != null && session.PlayerSnapshot.StateVersion > before.StateVersion)
                    {
                        advanced.TrySetResult(true);
                    }
                };

                BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                    before.RoundId,
                    "local-direct",
                    0UL,
                    ProtocolActionType.DiscardTile,
                    tileIds: new[] { tile });
                var command = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    payload,
                    new RequestId("req-discard-1"),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(before.StateVersion),
                    new RoundId(before.RoundId),
                    decisionId: null,
                    controlEpoch: new ControlEpoch(0));
                CommandSubmission submission = await session.SubmitAsync(command, ct);
                Assert.True(submission.Sent);

                using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
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

                TableReceiptResolvedEventArgs receipt;
                try
                {
                    receipt = await WithTimeoutAsync(resolved.Task, pumpCts.Token);
                }
                catch (Exception exception)
                {
                    throw new Xunit.Sdk.XunitException(
                        "receipt did not resolve: state=" + session.State +
                        " last=" + session.LastError +
                        " pending=" + session.PendingCommands.Count +
                        " inner=" + exception.GetType().Name);
                }
                try
                {
                    await WithTimeoutAsync(advanced.Task, pumpCts.Token);
                }
                catch (Exception exception)
                {
                    throw new Xunit.Sdk.XunitException(
                        "push did not advance: last=" + session.LastError +
                        " state=" + session.PlayerSnapshot?.StateVersion +
                        " cursor=" + session.ConfirmedCursor +
                        " receipt=" + receipt.Resolution.ReceiptStatus + "/" + receipt.Resolution.ReasonCode +
                        " inner=" + exception.GetType().Name);
                }
                pumpCts.Cancel();
                await pump;
                Assert.True(
                    receipt.Resolution.ReceiptStatus == Mahjong.Contracts.ReceiptStatus.Applied,
                    "status=" + receipt.Resolution.ReceiptStatus + " reason=" + receipt.Resolution.ReasonCode +
                    " round=" + before.RoundId + " phase=" + before.PublicView.Board.Phase + " seat=" + before.PublicView.Board.CurrentSeat);

                Assert.NotNull(session.PlayerSnapshot);
                Assert.True(session.PlayerSnapshot!.StateVersion > before.StateVersion);
                Assert.Empty(session.PendingCommands);
            }
            finally
            {
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }

        internal static async Task PumpUntilReadyAsync(TableSession session, string ticket, CancellationToken cancellationToken)
        {
            await session.OpenAsync(new TableSessionOpenRequest(ticket, "p41-test"), cancellationToken);
            for (int i = 0; i < 20 && session.State != TableConnectionState.Ready; i++)
            {
                await session.PumpOnceAsync(cancellationToken);
            }

            Assert.Equal(TableConnectionState.Ready, session.State);
        }

        internal static async Task<T> WithTimeoutAsync<T>(Task<T> task, CancellationToken cancellationToken)
        {
            Task completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
            if (completed != task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("The local loopback operation timed out.");
            }

            return await task;
        }
    }
}
